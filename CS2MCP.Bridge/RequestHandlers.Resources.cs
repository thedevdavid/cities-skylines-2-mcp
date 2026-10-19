using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Simulation;
using Newtonsoft.Json;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// POST /import/resources writes natural resources (fertility, ore, oil, fish)
    /// and groundwater from a regular grid in CS2 metres; GET /map/resources reads
    /// a layer back, downsampled. Both maps are 256×256 cells of 56 m over the
    /// playable area (CellMapSystem, origin at the -x/-z corner). Writing mirrors
    /// the editor's own "import resource from image" (ResourcePanelSystem):
    /// complete the map's dependencies, write synchronously, then mark map
    /// features Updated so extractor areas refresh.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class ResourceGridDto
        {
            public int size;
            public float cellSize;
            public float originX;
            public float originZ;
            public string rowAxis;
        }

        public sealed class ResourceOptionsDto
        {
            public bool? dryRun;
            public string mode;
            public Dictionary<string, float> scale;
        }

        public sealed class ResourceImportDto
        {
            public string name;
            public ResourceOptionsDto options;
            public ResourceGridDto grid;
            public Dictionary<string, int[]> layers;
        }

        // NaturalResourceSystem.MAX_BASE_RESOURCES and GroundWaterSystem.kMaxGroundWater.
        private const float kResourceFull = 10000f;
        private static readonly string[] kResourceLayers = { "fertility", "ore", "oil", "fish", "groundwater" };

        private BridgeResponse ImportResources(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST" || string.IsNullOrEmpty(request.Body))
            {
                return BridgeResponse.Error(400, "POST a JSON body {grid:{size,cellSize,originX,originZ,rowAxis}, layers:{fertility:[...],...}}");
            }
            ResourceImportDto body;
            try
            {
                body = JsonConvert.DeserializeObject<ResourceImportDto>(request.Body);
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body?.grid == null || body.layers == null || body.layers.Count == 0)
            {
                return BridgeResponse.Error(400, "body needs 'grid' and a non-empty 'layers' object");
            }
            ResourceGridDto grid = body.grid;
            if (grid.size < 2 || grid.size > 4096 || !(grid.cellSize > 0f))
            {
                return BridgeResponse.Error(400, "grid.size must be 2-4096 and grid.cellSize > 0");
            }
            bool rowsAlongPlusZ;
            switch ((grid.rowAxis ?? "+z").Trim())
            {
                case "+z":
                    rowsAlongPlusZ = true;
                    break;
                case "-z":
                    rowsAlongPlusZ = false;
                    break;
                default:
                    return BridgeResponse.Error(400, "grid.rowAxis must be '+z' (row 0 at originZ, rows go north) or '-z'");
            }
            ResourceOptionsDto options = body.options ?? new ResourceOptionsDto();
            string mode = (options.mode ?? "replace").ToLowerInvariant();
            if (mode != "replace" && mode != "max" && mode != "add")
            {
                return BridgeResponse.Error(400, "options.mode must be replace, max or add");
            }

            var problems = new List<string>();
            var ignored = new List<object>();
            var accepted = new List<string>();
            foreach (KeyValuePair<string, int[]> layer in body.layers)
            {
                string key = layer.Key.ToLowerInvariant();
                if (key == "forest" || key == "wood")
                {
                    ignored.Add(new { layer = layer.Key, reason = "CS2 has no forest cell layer: wood comes from tree entities; import trees instead" });
                    continue;
                }
                if (Array.IndexOf(kResourceLayers, key) < 0)
                {
                    problems.Add($"unknown layer '{layer.Key}' (CS2 has fertility, ore, oil, fish, groundwater)");
                    continue;
                }
                if (layer.Value == null || layer.Value.Length != grid.size * grid.size)
                {
                    problems.Add($"layer '{layer.Key}' has {layer.Value?.Length ?? 0} values; expected size*size = {grid.size * grid.size}");
                    continue;
                }
                accepted.Add(key);
            }
            if (problems.Count > 0)
            {
                return BridgeResponse.Json(new { error = "nothing was written", problems }, 400);
            }

            NaturalResourceSystem natural = World.GetOrCreateSystemManaged<NaturalResourceSystem>();
            GroundWaterSystem groundWater = World.GetOrCreateSystemManaged<GroundWaterSystem>();
            var summaries = new List<object>();
            bool dryRun = options.dryRun == true;

            CellMapData<NaturalResourceCell> naturalData = natural.GetData(readOnly: dryRun, out JobHandle naturalDeps);
            naturalDeps.Complete();
            CellMapData<GroundWater> groundData = groundWater.GetData(readOnly: dryRun, out JobHandle groundDeps);
            groundDeps.Complete();

            foreach (string key in accepted)
            {
                int[] values = FindLayer(body.layers, key);
                float scale = kResourceFull;
                if (options.scale != null && options.scale.TryGetValue(key, out float customScale))
                {
                    scale = customScale;
                }
                int textureSize = key == "groundwater" ? groundData.m_TextureSize.x : naturalData.m_TextureSize.x;
                int cells = textureSize * textureSize;
                int written = 0;
                int nonZero = 0;
                double sum = 0;
                int max = 0;
                for (int index = 0; index < cells; index++)
                {
                    float3 center = CellMapSystem<NaturalResourceCell>.GetCellCenter(index, textureSize);
                    float sample = SampleGrid(values, grid, rowsAlongPlusZ, center.x, center.z);
                    int amount = (int)math.round(sample / 255f * scale);
                    if (amount > 0)
                    {
                        nonZero++;
                    }
                    sum += amount;
                    max = math.max(max, amount);
                    if (dryRun)
                    {
                        continue;
                    }
                    if (key == "groundwater")
                    {
                        GroundWater cell = groundData.m_Buffer[index];
                        int value = Combine(mode, cell.m_Max, amount, (int)kResourceFull);
                        cell.m_Amount = (short)value;
                        cell.m_Max = (short)value;
                        groundData.m_Buffer[index] = cell;
                    }
                    else
                    {
                        NaturalResourceCell cell = naturalData.m_Buffer[index];
                        NaturalResourceAmount current = key == "fertility" ? cell.m_Fertility
                            : key == "ore" ? cell.m_Ore
                            : key == "oil" ? cell.m_Oil
                            : cell.m_Fish;
                        var next = new NaturalResourceAmount { m_Base = (ushort)Combine(mode, current.m_Base, amount, 65535), m_Used = 0 };
                        switch (key)
                        {
                            case "fertility":
                                cell.m_Fertility = next;
                                break;
                            case "ore":
                                cell.m_Ore = next;
                                break;
                            case "oil":
                                cell.m_Oil = next;
                                break;
                            default:
                                cell.m_Fish = next;
                                break;
                        }
                        naturalData.m_Buffer[index] = cell;
                    }
                    written++;
                }
                summaries.Add(new
                {
                    layer = key,
                    scale,
                    cellsWritten = written,
                    cellsNonZero = nonZero,
                    mean = Math.Round(sum / cells, 1),
                    max,
                    note = key == "fish"
                        ? "the game recomputes fish from surface-water depth every update, so written fish values are overwritten; shape fish with water"
                        : null,
                });
            }

            if (!dryRun)
            {
                EntityQuery features = EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<MapFeatureElement>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>() },
                });
                if (!features.IsEmptyIgnoreFilter)
                {
                    EntityManager.AddComponent<Updated>(features);
                }
                features.Dispose();
            }

            return BridgeResponse.Json(new
            {
                name = body.name,
                dryRun,
                mode = editor ? "editor" : "game",
                writeMode = options.mode ?? "replace",
                gameGrid = new { size = naturalData.m_TextureSize.x, cellSize = naturalData.m_CellSize.x, originX = -7168f, originZ = -7168f, rowAxis = "+z" },
                layers = summaries,
                ignored,
                note = "values are scaled 0-255 → 0-scale (default 10000 = full concentration, the editor brush maximum); m_Used is reset to 0",
            });
        }

        private static int[] FindLayer(Dictionary<string, int[]> layers, string key)
        {
            foreach (KeyValuePair<string, int[]> layer in layers)
            {
                if (string.Equals(layer.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return layer.Value;
                }
            }
            return null;
        }

        private static int Combine(string mode, int current, int amount, int max)
        {
            int value = mode == "max" ? math.max(current, amount) : mode == "add" ? current + amount : amount;
            return math.clamp(value, 0, max);
        }

        /// <summary>Bilinear sample of the input grid (values at cell centres) at world x/z; 0 outside the grid.</summary>
        private static float SampleGrid(int[] values, ResourceGridDto grid, bool rowsAlongPlusZ, float x, float z)
        {
            float col = (x - grid.originX) / grid.cellSize - 0.5f;
            float row = rowsAlongPlusZ
                ? (z - grid.originZ) / grid.cellSize - 0.5f
                : (grid.originZ - z) / grid.cellSize - 0.5f;
            if (col < -0.5f || row < -0.5f || col > grid.size - 0.5f || row > grid.size - 0.5f)
            {
                return 0f;
            }
            int c0 = math.clamp((int)math.floor(col), 0, grid.size - 1);
            int r0 = math.clamp((int)math.floor(row), 0, grid.size - 1);
            int c1 = math.min(c0 + 1, grid.size - 1);
            int r1 = math.min(r0 + 1, grid.size - 1);
            float fc = math.saturate(col - c0);
            float fr = math.saturate(row - r0);
            float top = math.lerp(values[r0 * grid.size + c0], values[r0 * grid.size + c1], fc);
            float bottom = math.lerp(values[r1 * grid.size + c0], values[r1 * grid.size + c1], fc);
            return math.clamp(math.lerp(top, bottom, fr), 0f, 255f);
        }

        private BridgeResponse ReadResources(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            string layer = request.Query.TryGetValue("layer", out string rawLayer) ? rawLayer.ToLowerInvariant() : "fertility";
            if (Array.IndexOf(kResourceLayers, layer) < 0)
            {
                return BridgeResponse.Error(400, "layer must be fertility, ore, oil, fish or groundwater");
            }
            string field = request.Query.TryGetValue("field", out string rawField) ? rawField.ToLowerInvariant() : "base";
            int size = request.TryGetInt("size", out int rawSize) ? math.clamp(rawSize, 8, 256) : 128;

            int[] amounts;
            int textureSize;
            if (layer == "groundwater")
            {
                CellMapData<GroundWater> data = World.GetOrCreateSystemManaged<GroundWaterSystem>().GetData(readOnly: true, out JobHandle deps);
                deps.Complete();
                textureSize = data.m_TextureSize.x;
                amounts = new int[data.m_Buffer.Length];
                for (int i = 0; i < amounts.Length; i++)
                {
                    amounts[i] = field == "max" ? data.m_Buffer[i].m_Max : data.m_Buffer[i].m_Amount;
                }
            }
            else
            {
                CellMapData<NaturalResourceCell> data = World.GetOrCreateSystemManaged<NaturalResourceSystem>().GetData(readOnly: true, out JobHandle deps);
                deps.Complete();
                textureSize = data.m_TextureSize.x;
                amounts = new int[data.m_Buffer.Length];
                for (int i = 0; i < amounts.Length; i++)
                {
                    NaturalResourceCell cell = data.m_Buffer[i];
                    NaturalResourceAmount amount = layer == "fertility" ? cell.m_Fertility
                        : layer == "ore" ? cell.m_Ore
                        : layer == "oil" ? cell.m_Oil
                        : cell.m_Fish;
                    amounts[i] = field == "available" ? math.max(0, amount.m_Base - amount.m_Used) : field == "used" ? amount.m_Used : amount.m_Base;
                }
            }

            // Average blocks of game cells (or repeat them when size > textureSize).
            var values = new int[size * size];
            int max = 0;
            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    int r0 = row * textureSize / size;
                    int r1 = math.max(r0 + 1, (row + 1) * textureSize / size);
                    int c0 = col * textureSize / size;
                    int c1 = math.max(c0 + 1, (col + 1) * textureSize / size);
                    long sum = 0;
                    for (int r = r0; r < r1; r++)
                    {
                        for (int c = c0; c < c1; c++)
                        {
                            sum += amounts[r * textureSize + c];
                        }
                    }
                    int value = (int)(sum / ((r1 - r0) * (c1 - c0)));
                    values[row * size + col] = value;
                    max = math.max(max, value);
                }
            }
            return BridgeResponse.Json(new
            {
                layer,
                field,
                size,
                cellSize = 14336f / size,
                originX = -7168f,
                originZ = -7168f,
                rowAxis = "+z",
                max,
                note = "row-major, row 0 at originZ; values in game units (10000 = full concentration)",
                values,
            });
        }
    }
}
