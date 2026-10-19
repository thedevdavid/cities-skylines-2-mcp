using System;
using System.Collections.Generic;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// Water source import (POST /import/water) and its undo (POST /import/water/undo),
    /// in the map editor or a city. Sources are created the way Water Features
    /// (yenyang) does: an entity with WaterSourceData + Transform + Created, plus
    /// Updated, written through the ToolOutputBarrier, with an id from
    /// WaterSystem.GetNextSourceId(). Undo marks the sources Deleted the same way.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class WaterSourceDto
        {
            public string id;
            public float x;
            public float z;
            public string type;
            public float? height;
            public float? depth;
            public float? rate;
            public float radius;
            public float? polluted;
        }

        public sealed class WaterImportDto
        {
            public string name;
            public List<WaterSourceDto> sources;
            public bool? dryRun;
        }

        private sealed class WaterRecord
        {
            public string Id;
            public string Name;
            public List<int> SourceIds = new List<int>();
        }

        // Legacy WaterSourceData.m_ConstantDepth values (Water Features' SourceType order).
        private const int kWaterStream = 0;
        private const int kWaterLake = 1;
        private const int kWaterRiver = 2;
        private const int kWaterSea = 3;

        private readonly List<WaterRecord> m_WaterRecords = new List<WaterRecord>();
        private int m_WaterImportCounter;
        private EntityQuery m_WaterSourceQuery;
        private bool m_WaterSourceQueryCreated;

        private EntityQuery WaterSourceQuery
        {
            get
            {
                if (!m_WaterSourceQueryCreated)
                {
                    m_WaterSourceQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<WaterSourceData>(), ComponentType.ReadOnly<Transform>() },
                        None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
                    });
                    m_WaterSourceQueryCreated = true;
                }
                return m_WaterSourceQuery;
            }
        }

        private BridgeResponse ImportWater(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST" || string.IsNullOrEmpty(request.Body))
            {
                return BridgeResponse.Error(400, "POST a JSON body {sources:[{id,x,z,type,height|depth|rate,radius}]} (see IMPORT_SCHEMA.md)");
            }
            WaterImportDto body;
            try
            {
                body = JsonConvert.DeserializeObject<WaterImportDto>(request.Body);
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body == null || body.sources == null || body.sources.Count == 0)
            {
                return BridgeResponse.Error(400, "body needs a non-empty 'sources' array");
            }

            WaterSystem waterSystem = World.GetOrCreateSystemManaged<WaterSystem>();
            bool legacy = waterSystem.UseLegacyWaterSources;
            TerrainHeightData heightData = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData(waitForPending: true);

            var problems = new List<string>();
            var planned = new List<(WaterSourceDto dto, WaterSourceData data, float3 position, List<string> notes)>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (WaterSourceDto dto in body.sources)
            {
                if (dto == null || string.IsNullOrEmpty(dto.id) || !ids.Add(dto.id))
                {
                    problems.Add($"source '{dto?.id}': missing or duplicate id");
                    continue;
                }
                if (!(dto.radius > 0f) || dto.radius > WaterSystem.kMaxSourceRadius)
                {
                    problems.Add($"source '{dto.id}': radius must be > 0 and <= {WaterSystem.kMaxSourceRadius} m");
                    continue;
                }
                int kind;
                switch ((dto.type ?? "").ToLowerInvariant())
                {
                    case "stream":
                        kind = kWaterStream;
                        break;
                    case "natural":
                    case "lake":
                        kind = kWaterLake;
                        break;
                    case "river":
                        kind = kWaterRiver;
                        break;
                    case "sea":
                        kind = kWaterSea;
                        break;
                    default:
                        problems.Add($"source '{dto.id}': type must be natural (lake), stream, river or sea");
                        continue;
                }
                if (kind == kWaterSea && !legacy)
                {
                    problems.Add($"source '{dto.id}': this map uses the new water system, which has no sea sources " +
                        "(the game removes them); set the sea level in the editor instead");
                    continue;
                }

                var notes = new List<string>();
                var position = new float3(dto.x, 0f, dto.z);
                float ground = TerrainUtils.SampleHeight(ref heightData, position);
                position.y = ground;

                // m_Height: legacy stream = flow rate; legacy lake/river/sea = absolute
                // water level; new water system = water depth above the terrain at the source.
                float value;
                if (legacy && kind == kWaterStream)
                {
                    if (!dto.rate.HasValue)
                    {
                        problems.Add($"source '{dto.id}': a stream needs 'rate' (legacy water system)");
                        continue;
                    }
                    value = dto.rate.Value;
                }
                else if (legacy)
                {
                    if (dto.height.HasValue)
                    {
                        value = dto.height.Value;
                    }
                    else if (dto.depth.HasValue)
                    {
                        value = ground + dto.depth.Value;
                    }
                    else
                    {
                        problems.Add($"source '{dto.id}': give 'height' (absolute water level) or 'depth' (above terrain)");
                        continue;
                    }
                    if (value <= ground)
                    {
                        notes.Add($"water level {value:F1} m is not above the terrain ({ground:F1} m) at the source");
                    }
                }
                else
                {
                    if (dto.depth.HasValue)
                    {
                        value = dto.depth.Value;
                    }
                    else if (dto.height.HasValue)
                    {
                        value = dto.height.Value - ground;
                    }
                    else if (dto.rate.HasValue)
                    {
                        value = dto.rate.Value;
                        notes.Add("new water system: 'rate' used as the source depth (streams are depth-driven there; unverified)");
                    }
                    else
                    {
                        problems.Add($"source '{dto.id}': give 'height' (absolute water level) or 'depth' (above terrain)");
                        continue;
                    }
                    if (value <= 0f)
                    {
                        problems.Add($"source '{dto.id}': water level is not above the terrain ({ground:F1} m) at the source");
                        continue;
                    }
                    if (value > WaterSystem.kMaxSourceHeight)
                    {
                        notes.Add($"depth {value:F1} m clamped to {WaterSystem.kMaxSourceHeight} m");
                        value = WaterSystem.kMaxSourceHeight;
                    }
                }

                var data = new WaterSourceData
                {
                    m_Height = value,
                    m_ConstantDepth = kind,
                    m_Radius = dto.radius,
                    m_Polluted = math.clamp(dto.polluted ?? 0f, 0f, 1f),
                    m_Multiplier = 30f,
                    m_Modifier = 1f,
                };
                if (kind != kWaterRiver && kind != kWaterSea)
                {
                    // As Water Features: grow the radius until the source covers at least one water cell.
                    data.m_Multiplier = 1f;
                    for (int i = 0; i < 1000 && data.m_Multiplier == 1f; i++)
                    {
                        data.m_Multiplier = WaterSystem.CalculateSourceMultiplier(data, position);
                        if (data.m_Multiplier == 1f)
                        {
                            data.m_Radius++;
                        }
                    }
                    if (data.m_Radius != dto.radius)
                    {
                        notes.Add($"radius increased to {data.m_Radius} m (smaller sources do not reach a water cell)");
                    }
                }
                planned.Add((dto, data, position, notes));
            }

            if (problems.Count > 0)
            {
                return BridgeResponse.Json(new { error = $"{problems.Count} problem(s); nothing was created", problems }, 400);
            }

            var record = new WaterRecord { Id = $"wat-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{++m_WaterImportCounter}", Name = body.name };
            var results = new List<object>();
            EntityCommandBuffer commandBuffer = body.dryRun == true
                ? default
                : World.GetOrCreateSystemManaged<ToolOutputBarrier>().CreateCommandBuffer();
            EntityArchetype archetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<WaterSourceData>(),
                ComponentType.ReadWrite<Transform>(),
                ComponentType.ReadWrite<Created>());
            foreach ((WaterSourceDto dto, WaterSourceData data0, float3 position, List<string> notes) in planned)
            {
                WaterSourceData data = data0;
                if (body.dryRun != true)
                {
                    data.m_Id = waterSystem.GetNextSourceId();
                    Entity entity = commandBuffer.CreateEntity(archetype);
                    commandBuffer.SetComponent(entity, data);
                    commandBuffer.SetComponent(entity, new Transform { m_Position = position, m_Rotation = quaternion.identity });
                    commandBuffer.AddComponent(entity, default(Updated));
                    record.SourceIds.Add(data.m_Id);
                }
                results.Add(new
                {
                    id = dto.id,
                    sourceId = body.dryRun == true ? (int?)null : data.m_Id,
                    constantDepth = data.m_ConstantDepth,
                    height = data.m_Height,
                    radius = data.m_Radius,
                    multiplier = data.m_Multiplier,
                    terrainY = position.y,
                    notes,
                });
            }
            if (body.dryRun != true)
            {
                m_WaterRecords.Add(record);
            }

            return BridgeResponse.Json(new
            {
                importId = body.dryRun == true ? null : record.Id,
                dryRun = body.dryRun == true,
                mode = editor ? "editor" : "game",
                waterSystem = legacy ? "legacy" : "new",
                created = body.dryRun == true ? 0 : planned.Count,
                note = "sources are created at the end of this frame; water needs simulation time to spread " +
                       "(in the editor, let the water simulation run). POST /import/water/undo removes them.",
                sources = results,
            });
        }

        private BridgeResponse UndoWater(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return BridgeResponse.Error(405, "use POST /import/water/undo (optionally ?id=<importId>)");
            }
            if (m_WaterRecords.Count == 0)
            {
                return BridgeResponse.Error(409, "nothing to undo: no water import recorded since the game started");
            }
            WaterRecord record = m_WaterRecords[m_WaterRecords.Count - 1];
            if (request.Query.TryGetValue("id", out string id) && !string.IsNullOrEmpty(id) && id != record.Id)
            {
                return BridgeResponse.Error(409, $"only the most recent water import ({record.Id}) can be undone");
            }

            var wanted = new HashSet<int>(record.SourceIds);
            int deleted = 0;
            EntityCommandBuffer commandBuffer = World.GetOrCreateSystemManaged<ToolOutputBarrier>().CreateCommandBuffer();
            using (NativeArray<Entity> entities = WaterSourceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (wanted.Contains(EntityManager.GetComponentData<WaterSourceData>(entity).m_Id))
                    {
                        commandBuffer.AddComponent(entity, default(Deleted));
                        deleted++;
                    }
                }
            }
            m_WaterRecords.RemoveAt(m_WaterRecords.Count - 1);
            return BridgeResponse.Json(new
            {
                undone = true,
                importId = record.Id,
                sourcesDeleted = deleted,
                sourcesRecorded = record.SourceIds.Count,
                undoableWaterImportsLeft = m_WaterRecords.Count,
                note = deleted < record.SourceIds.Count
                    ? "some recorded sources no longer exist (deleted by hand, or ids changed by a water-system upgrade)"
                    : null,
            });
        }
    }
}
