using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Prefabs.Terrain;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// Terrain material painting (0.9.29), as the editor's Terrain tool in material mode:
    /// TerrainToolSystem with a TerraformingPrefab whose m_Target is Material ->
    /// ApplyBrushesSystem -> TerrainMaterialSystem.ApplyBrush(Brush, BrushPrefab), which
    /// runs the paint (strength &gt; 0) or erase compute kernel on the user splatmaps
    /// (m_UserSplatmap 4096x4096 RG8 over the playable area, m_UserSplatmapWorld 1024x1024
    /// over the world). The material is the tool prefab's TerrainMaterialProperties.m_Type
    /// (Extra1..Extra4 = the extra texture slots of the current TerrainRenderSettingsPrefab;
    /// None = erase every channel). Both splatmaps are serialised with the map
    /// (TerrainMaterialSystem.Serialize, FormatTags.TerrainUserSplatmap).
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class TerrainPaintOptionsDto
        {
            public bool? dryRun;
            public string brush;
            public float? strength;
            public float? softEdge;
            /// <summary>0.9.30: metres over which strength ramps from 0 at the outline to full inside.</summary>
            public float? feather;
            public float? minStamp;
            public int? maxStamps;
            /// <summary>0.9.44: "raster" (default: one masked brush per material and strength) or "stamp" (the 0.9.29 brush stamps).</summary>
            public string method;
        }

        public sealed class TerrainPaintPolygonDto
        {
            public string id;
            public string material;
            public float[][] polygon;
            public float? strength;
            public float? softEdge;
            public float? feather;
        }

        public sealed class TerrainPaintStrokeDto
        {
            public string id;
            public string material;
            public float x;
            public float z;
            public float radius;
            public float? strength;
        }

        public sealed class TerrainPaintDto
        {
            public string name;
            public TerrainPaintOptionsDto options;
            public List<TerrainPaintPolygonDto> polygons;
            public List<TerrainPaintStrokeDto> strokes;
        }

        private sealed class PaintUndo
        {
            public string Id;
            public string Name;
            public RenderTexture Playable;
            public RenderTexture World;
        }

        private readonly List<PaintUndo> m_PaintUndo = new List<PaintUndo>();
        private int m_PaintCounter;

        private struct Stamp
        {
            public float2 Centre;
            public float Radius;
            public float Strength;
        }

        /// <summary>GET /map/terrain-materials</summary>
        private BridgeResponse TerrainMaterials()
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            var tools = new List<object>();
            var settings = CurrentLookPrefab(typeof(TerrainRenderSettingsPrefab)) as TerrainRenderSettingsPrefab;
            foreach (PrefabBase p in AllPrefabsOf(typeof(TerraformingPrefab)))
            {
                var t = (TerraformingPrefab)p;
                if (t.m_Target != TerraformingTarget.Material)
                {
                    continue;
                }
                TerrainMaterialType slot = t.TryGet(out TerrainMaterialProperties props) ? props.m_Type : TerrainMaterialType.None;
                tools.Add(new
                {
                    name = t.name,
                    slot = slot.ToString(),
                    aliases = MaterialAliases(slot),
                    action = slot == TerrainMaterialType.None ? "erase (clears every painted channel)" : "paint",
                    texture = settings == null ? null : SlotTexture(settings, slot),
                });
            }
            var brushes = new List<object>();
            foreach (PrefabBase p in AllPrefabsOf(typeof(BrushPrefab)))
            {
                var b = (BrushPrefab)p;
                brushes.Add(new { name = b.name, priority = b.m_Priority, texture = b.m_Texture != null ? b.m_Texture.name : null });
            }
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            return BridgeResponse.Json(new
            {
                materials = tools,
                brushes,
                terrainRenderSettings = settings?.name,
                splatmap = new
                {
                    playable = new { size = 4096, metersPerPixel = terrain.playableArea.x / 4096f },
                    world = new { size = 1024, metersPerPixel = terrain.worldSize.x / 1024f },
                    note = "RG8 user splatmaps, saved with the map; coverage is not read back (GPU texture)",
                },
                note = "use 'name' (or the slot Extra1..Extra4) as 'material' in POST /import/terrain-paint; what each slot looks like is the texture of the current terrain render settings (MAP_SETTINGS.md, terrainRenderSettings)",
            });
        }

        private static string[] MaterialAliases(TerrainMaterialType slot)
        {
            switch (slot)
            {
                case TerrainMaterialType.Extra1:
                    return new[] { "Dry", "Material Dry", "Texture Paint Tool 01" };
                case TerrainMaterialType.Extra2:
                    return new[] { "Lush", "Material Lush", "Texture Paint Tool 02" };
                case TerrainMaterialType.Extra3:
                    return new[] { "Dirt", "Material Dirt", "Texture Paint Tool 03" };
                case TerrainMaterialType.Extra4:
                    return new[] { "Sand", "Material Sand", "Texture Paint Tool 04" };
                default:
                    return new[] { "Clear", "Erase" };
            }
        }

        private static string SlotTexture(TerrainRenderSettingsPrefab s, TerrainMaterialType slot)
        {
            switch (slot)
            {
                case TerrainMaterialType.Extra1:
                    return s.m_Extra1Diffuse.ToString();
                case TerrainMaterialType.Extra2:
                    return s.m_Extra2Diffuse.ToString();
                case TerrainMaterialType.Extra3:
                    return s.m_Extra3Diffuse.ToString();
                case TerrainMaterialType.Extra4:
                    return s.m_Extra4Diffuse.ToString();
                default:
                    return null;
            }
        }

        /// <summary>POST /import/terrain-paint</summary>
        private BridgeResponse TerrainPaint(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            TerrainPaintDto body;
            try
            {
                body = JsonConvert.DeserializeObject<TerrainPaintDto>(request.Body ?? "");
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body == null || ((body.polygons == null || body.polygons.Count == 0) && (body.strokes == null || body.strokes.Count == 0)))
            {
                return BridgeResponse.Error(400, "POST {name, options, polygons:[{id, material, polygon:[[x,z],...]}], strokes:[{id, material, x, z, radius}]} (GET /map/terrain-materials lists the materials)");
            }
            TerrainPaintOptionsDto options = body.options ?? new TerrainPaintOptionsDto();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Materials by tool name or slot.
            var materials = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            var materialNames = new List<string>();
            foreach (PrefabBase p in AllPrefabsOf(typeof(TerraformingPrefab)))
            {
                var t = (TerraformingPrefab)p;
                if (t.m_Target != TerraformingTarget.Material || !prefabSystem.TryGetEntity(t, out Entity e))
                {
                    continue;
                }
                materials[t.name] = e;
                materialNames.Add(t.name);
                if (t.TryGet(out TerrainMaterialProperties props) && !materials.ContainsKey(props.m_Type.ToString()))
                {
                    materials[props.m_Type.ToString()] = e;
                }
                // The editor's names (en-US Locale: Assets.NAME / Assets.DESCRIPTION).
                if (t.TryGet(out TerrainMaterialProperties props2))
                {
                    foreach (string alias in MaterialAliases(props2.m_Type))
                    {
                        if (!materials.ContainsKey(alias))
                        {
                            materials[alias] = e;
                        }
                    }
                }
            }
            // Brush: the named one, else the hardest-looking, else the lowest priority (the tools' default).
            BrushPrefab brush = null;
            foreach (PrefabBase p in AllPrefabsOf(typeof(BrushPrefab)))
            {
                var b = (BrushPrefab)p;
                if (!string.IsNullOrEmpty(options.brush))
                {
                    if (string.Equals(b.name, options.brush, StringComparison.OrdinalIgnoreCase))
                    {
                        brush = b;
                    }
                    continue;
                }
                if (brush == null || b.m_Priority < brush.m_Priority)
                {
                    brush = b;
                }
            }
            if (brush == null || !prefabSystem.TryGetEntity(brush, out Entity brushEntity))
            {
                return BridgeResponse.Error(400, $"no brush prefab '{options.brush}' (GET /map/terrain-materials lists them)");
            }

            float defaultStrength = math.clamp(options.strength ?? 1f, 0f, 1f);
            float defaultSoft = math.max(0f, options.softEdge ?? 0f);
            float defaultFeather = math.max(0f, options.feather ?? 0f);
            float minStamp = math.max(1f, options.minStamp ?? 4f);
            int maxStamps = options.maxStamps ?? 250000;
            var problems = new List<string>();
            var plan = new List<(string id, Entity material, List<Stamp> stamps)>();
            int total = 0;
            bool raster = !string.Equals(options.method, "stamp", StringComparison.OrdinalIgnoreCase);
            var groups = new List<RasterGroup>();
            int rasterPolygons = 0;
            int rasterStrokes = 0;
            if (body.polygons != null)
            {
                foreach (TerrainPaintPolygonDto poly in body.polygons)
                {
                    if (poly == null || string.IsNullOrEmpty(poly.id))
                    {
                        problems.Add("a polygon has no id");
                        continue;
                    }
                    if (string.IsNullOrEmpty(poly.material) || !materials.TryGetValue(poly.material, out Entity material))
                    {
                        problems.Add($"polygon '{poly.id}': unknown material '{poly.material}'; available: {string.Join(", ", materialNames)} (or Extra1..Extra4)");
                        continue;
                    }
                    if (!TryCleanPolygon(poly.polygon, out float2[] ring, out string why))
                    {
                        problems.Add($"polygon '{poly.id}': {why}");
                        continue;
                    }
                    if (raster)
                    {
                        float strength = math.clamp(poly.strength ?? defaultStrength, 0f, 1f);
                        RasterGroup group = groups.Find(g => g.Material == material && math.abs(g.Strength - strength) < 0.0005f);
                        if (group == null)
                        {
                            group = new RasterGroup { Material = material, Strength = strength };
                            groups.Add(group);
                        }
                        group.Polygons.Add((poly.id, ring, math.max(0f, poly.feather ?? defaultFeather), math.max(0f, poly.softEdge ?? defaultSoft)));
                        rasterPolygons++;
                        continue;
                    }
                    var stamps = new List<Stamp>();
                    FillPolygon(ring, math.max(0f, poly.softEdge ?? defaultSoft), math.max(0f, poly.feather ?? defaultFeather), minStamp, math.clamp(poly.strength ?? defaultStrength, 0f, 1f), stamps);
                    total += stamps.Count;
                    plan.Add((poly.id, material, stamps));
                }
            }
            if (body.strokes != null)
            {
                foreach (TerrainPaintStrokeDto s in body.strokes)
                {
                    if (s == null || string.IsNullOrEmpty(s.id) || s.radius <= 0f)
                    {
                        problems.Add($"stroke '{s?.id}': needs id and radius > 0");
                        continue;
                    }
                    if (string.IsNullOrEmpty(s.material) || !materials.TryGetValue(s.material, out Entity material))
                    {
                        problems.Add($"stroke '{s.id}': unknown material '{s.material}'; available: {string.Join(", ", materialNames)} (or Extra1..Extra4)");
                        continue;
                    }
                    if (raster)
                    {
                        float strength = math.clamp(s.strength ?? defaultStrength, 0f, 1f);
                        RasterGroup group = groups.Find(g => g.Material == material && math.abs(g.Strength - strength) < 0.0005f);
                        if (group == null)
                        {
                            group = new RasterGroup { Material = material, Strength = strength };
                            groups.Add(group);
                        }
                        group.Strokes.Add((s.id, new float2(s.x, s.z), s.radius));
                        rasterStrokes++;
                        continue;
                    }
                    plan.Add((s.id, material, new List<Stamp> { new Stamp { Centre = new float2(s.x, s.z), Radius = s.radius, Strength = math.clamp(s.strength ?? defaultStrength, 0f, 1f) } }));
                    total++;
                }
            }
            if (total > maxStamps)
            {
                problems.Add($"{total} brush stamps exceed options.maxStamps {maxStamps}; raise it or minStamp");
            }
            if (problems.Count > 0)
            {
                return BridgeResponse.Json(new { error = $"{problems.Count} problem(s); nothing was painted", problems = problems.Count > 200 ? problems.GetRange(0, 200) : problems, materials = materialNames }, 400);
            }
            if (options.dryRun == true)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    method = raster ? "raster" : "stamp",
                    rasterGroups = groups.ConvertAll(g => (object)new { material = prefabSystem.GetPrefab<PrefabBase>(g.Material).name, strength = g.Strength, polygons = g.Polygons.Count, strokes = g.Strokes.Count }),
                    brush = brush.name,
                    stamps = total,
                    items = plan.ConvertAll(p => (object)new { id = p.id, material = prefabSystem.GetPrefab<PrefabBase>(p.material).name, stamps = p.stamps.Count }),
                    materials = materialNames,
                });
            }

            // Undo snapshot of both splatmaps, then paint.
            TerrainMaterialSystem materialSystem = World.GetOrCreateSystemManaged<TerrainMaterialSystem>();
            var guard = new NetGuard(EntityManager, EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Curve>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            }));
            string id = $"paint-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{++m_PaintCounter}";
            var undo = new PaintUndo
            {
                Id = id,
                Name = string.IsNullOrEmpty(body.name) ? id : body.name,
                Playable = Snapshot(materialSystem.UserSplatmap),
                World = Snapshot(materialSystem.UserSplatmapWorld),
            };
            int painted = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            List<object> rasterDetails = null;
            string calibration = null;
            if (groups.Count > 0)
            {
                RasterCalibration cal = null;
                if (!ResolveRasterApi(out string apiError))
                {
                    problems.Add(apiError);
                }
                else
                {
                    if (s_RasterCalibration == null)
                    {
                        s_RasterCalibration = Calibrate(materialSystem, groups[0].Material);
                        calibration = s_RasterCalibration == null ? "failed" : "measured: " + s_RasterCalibration.Detail;
                    }
                    cal = s_RasterCalibration;
                }
                if (cal != null)
                {
                    calibration = calibration ?? "cached";
                    rasterDetails = RasterPaint(materialSystem, groups, cal, clock, brush.m_Texture);
                    painted += groups.Count;
                }
                else
                {
                    // Fall back to stamps for this import.
                    calibration = (calibration ?? "unavailable") + "; fell back to stamps";
                    foreach (RasterGroup g in groups)
                    {
                        foreach ((string pid, float2[] ring, float feather, float soft) in g.Polygons)
                        {
                            var stamps = new List<Stamp>();
                            FillPolygon(ring, soft, feather, minStamp, g.Strength, stamps);
                            plan.Add((pid, g.Material, stamps));
                        }
                        foreach ((string sid, float2 c, float radius) in g.Strokes)
                        {
                            plan.Add((sid, g.Material, new List<Stamp> { new Stamp { Centre = c, Radius = radius, Strength = g.Strength } }));
                        }
                    }
                }
            }
            foreach ((string itemId, Entity material, List<Stamp> stamps) in plan)
            {
                foreach (Stamp s in stamps)
                {
                    var b = new Brush
                    {
                        m_Tool = material,
                        m_Position = new float3(s.Centre.x, 0f, s.Centre.y),
                        m_Target = new float3(s.Centre.x, 0f, s.Centre.y),
                        m_Start = new float3(s.Centre.x, 0f, s.Centre.y),
                        m_Angle = 0f,
                        m_Size = s.Radius * 2f,
                        m_Strength = math.max(s.Strength, 0.0001f),
                        m_Opacity = 1f,
                    };
                    materialSystem.ApplyBrush(b, brushEntity);
                    painted++;
                }
            }
            m_PaintUndo.Add(undo);
            while (m_PaintUndo.Count > 3)
            {
                ReleaseUndo(m_PaintUndo[0]);
                m_PaintUndo.RemoveAt(0);
            }
            guard.AfterBatch("terrain paint", new List<string>(), PaintPrefabName);
            return BridgeResponse.Json(new
            {
                importId = id,
                name = undo.Name,
                brush = brush.name,
                method = raster ? "raster" : "stamp",
                calibration,
                rasterPolygons,
                rasterStrokes,
                raster = rasterDetails,
                seconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
                items = plan.Count + rasterPolygons + rasterStrokes,
                stamps = painted,
                netGuard = guard.Report(),
                undo = "POST /import/terrain-paint/undo (newest import first; the last 3 are kept, in memory only)",
                note = "painted into the terrain material splatmaps; save the map to keep it",
            });
        }

        private string PaintPrefabName(Entity prefab)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            return prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?";
        }

        /// <summary>POST /import/terrain-paint/undo[?name=|?id=]: restores the splatmaps from before the newest paint import.</summary>
        private BridgeResponse TerrainPaintUndo(BridgeRequest request)
        {
            if (m_PaintUndo.Count == 0)
            {
                return BridgeResponse.Error(409, "nothing to undo (the last 3 paint imports of this game session are kept)");
            }
            PaintUndo target = m_PaintUndo[m_PaintUndo.Count - 1];
            request.Query.TryGetValue("name", out string name);
            request.Query.TryGetValue("id", out string id);
            if ((!string.IsNullOrEmpty(name) && name != target.Name) || (!string.IsNullOrEmpty(id) && id != target.Id))
            {
                return BridgeResponse.Error(409, $"only the newest paint import ({target.Id}, '{target.Name}') can be undone");
            }
            TerrainMaterialSystem materialSystem = World.GetOrCreateSystemManaged<TerrainMaterialSystem>();
            Restore(target.Playable, materialSystem.UserSplatmap);
            Restore(target.World, materialSystem.UserSplatmapWorld);
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            var whole = new Bounds2(terrain.worldOffset, terrain.worldOffset + terrain.worldSize);
            terrain.OnAreaChanged(in whole);
            materialSystem.ForceUpdateWholeSplatmap();
            ReleaseUndo(target);
            m_PaintUndo.RemoveAt(m_PaintUndo.Count - 1);
            return BridgeResponse.Json(new { undone = target.Id, name = target.Name, left = m_PaintUndo.Count });
        }

        public sealed class PaintProbeDto
        {
            public float x;
            public float z;
            public string material;
            public string brush;
            public float radius = 20f;
            public float[] strengths = { 0.4f, 0.4f, 0.4f, 1f };
        }

        /// <summary>
        /// POST /debug/terrain-paint/probe: stamps one spot with the given strengths in turn, reads the raw
        /// user-splatmap texel (RG8) at the centre and 0.75 x radius out after each stamp, then restores the
        /// splatmaps. Answers how stamps of strength &lt; 1 combine and how the four materials are encoded.
        /// </summary>
        private BridgeResponse TerrainPaintProbe(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            PaintProbeDto dto;
            try
            {
                dto = JsonConvert.DeserializeObject<PaintProbeDto>(request.Body ?? "{}") ?? new PaintProbeDto();
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            Entity material = Entity.Null;
            foreach (PrefabBase p in AllPrefabsOf(typeof(TerraformingPrefab)))
            {
                var t = (TerraformingPrefab)p;
                if (t.m_Target != TerraformingTarget.Material || !t.TryGet(out TerrainMaterialProperties props))
                {
                    continue;
                }
                bool match = string.Equals(t.name, dto.material, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(props.m_Type.ToString(), dto.material, StringComparison.OrdinalIgnoreCase)
                    || Array.Exists(MaterialAliases(props.m_Type), a => string.Equals(a, dto.material, StringComparison.OrdinalIgnoreCase));
                if (match)
                {
                    prefabSystem.TryGetEntity(t, out material);
                }
            }
            BrushPrefab brush = null;
            foreach (PrefabBase p in AllPrefabsOf(typeof(BrushPrefab)))
            {
                var b = (BrushPrefab)p;
                if (string.IsNullOrEmpty(dto.brush) ? (brush == null || b.m_Priority < brush.m_Priority) : string.Equals(b.name, dto.brush, StringComparison.OrdinalIgnoreCase))
                {
                    brush = b;
                }
            }
            if (material == Entity.Null || brush == null || !prefabSystem.TryGetEntity(brush, out Entity brushEntity))
            {
                return BridgeResponse.Error(400, "provide {x, z, material, brush?, radius?, strengths?} with a material and brush from GET /map/terrain-materials");
            }
            TerrainMaterialSystem materialSystem = World.GetOrCreateSystemManaged<TerrainMaterialSystem>();
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            RenderTexture before = Snapshot(materialSystem.UserSplatmap);
            RenderTexture beforeWorld = Snapshot(materialSystem.UserSplatmapWorld);
            var centre = new float2(dto.x, dto.z);
            var edgePoint = centre + new float2(dto.radius * 0.75f, 0f);
            var steps = new List<object> { new { step = "before", centre = ReadTexel(materialSystem.UserSplatmap, terrain, centre), edge = ReadTexel(materialSystem.UserSplatmap, terrain, edgePoint) } };
            foreach (float strength in dto.strengths ?? new float[0])
            {
                materialSystem.ApplyBrush(new Brush
                {
                    m_Tool = material,
                    m_Position = new float3(centre.x, 0f, centre.y),
                    m_Target = new float3(centre.x, 0f, centre.y),
                    m_Start = new float3(centre.x, 0f, centre.y),
                    m_Size = dto.radius * 2f,
                    m_Strength = strength,
                    m_Opacity = 1f,
                }, brushEntity);
                steps.Add(new { step = $"strength {strength}", centre = ReadTexel(materialSystem.UserSplatmap, terrain, centre), edge = ReadTexel(materialSystem.UserSplatmap, terrain, edgePoint) });
            }
            Restore(before, materialSystem.UserSplatmap);
            Restore(beforeWorld, materialSystem.UserSplatmapWorld);
            var whole = new Bounds2(terrain.worldOffset, terrain.worldOffset + terrain.worldSize);
            terrain.OnAreaChanged(in whole);
            materialSystem.ForceUpdateWholeSplatmap();
            ReleaseUndo(new PaintUndo { Playable = before, World = beforeWorld });
            return BridgeResponse.Json(new
            {
                brush = brush.name,
                material = prefabSystem.GetPrefab<PrefabBase>(material).name,
                steps,
                note = "raw RG8 texel values (0..255) of the playable user splatmap; the splatmaps were restored afterwards",
            });
        }

        private static object ReadTexel(Texture texture, TerrainSystem terrain, float2 world)
        {
            if (!(texture is RenderTexture rt) || !rt.IsCreated())
            {
                return null;
            }
            float2 uv = (world - terrain.playableOffset) / terrain.playableArea;
            int x = math.clamp((int)(uv.x * rt.width), 0, rt.width - 1);
            int y = math.clamp((int)(uv.y * rt.height), 0, rt.height - 1);
            RenderTexture active = RenderTexture.active;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
                tex.Apply();
                Color32 c = tex.GetPixel(0, 0);
                return new { x, y, r = c.r, g = c.g };
            }
            finally
            {
                RenderTexture.active = active;
                UnityEngine.Object.Destroy(tex);
            }
        }

        private static RenderTexture Snapshot(Texture source)
        {
            if (!(source is RenderTexture rt) || !rt.IsCreated())
            {
                return null; // nothing painted yet: undo clears instead
            }
            var copy = new RenderTexture(rt.descriptor);
            copy.Create();
            Graphics.CopyTexture(rt, copy);
            return copy;
        }

        private static void Restore(RenderTexture snapshot, Texture target)
        {
            if (!(target is RenderTexture rt))
            {
                return;
            }
            if (snapshot == null)
            {
                RenderTexture active = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(false, true, Color.clear);
                RenderTexture.active = active;
                return;
            }
            Graphics.CopyTexture(snapshot, rt);
        }

        private static void ReleaseUndo(PaintUndo undo)
        {
            if (undo.Playable != null)
            {
                undo.Playable.Release();
                UnityEngine.Object.Destroy(undo.Playable);
            }
            if (undo.World != null)
            {
                undo.World.Release();
                UnityEngine.Object.Destroy(undo.World);
            }
        }

        /// <summary>
        /// Adaptive cover of a polygon with round stamps: squares on a 256 m grid are stamped whole
        /// (radius = 0.75 x side) when their centre lies inside and at least that far from the edge,
        /// skipped when outside and that far away, and split otherwise, down to minStamp; edge cells
        /// are stamped with radius max(softEdge, 0.75 x side) when their centre is inside.
        /// </summary>
        private static void FillPolygon(float2[] ring, float softEdge, float feather, float minStamp, float strength, List<Stamp> stamps)
        {
            float2 min = ring[0], max = ring[0];
            foreach (float2 p in ring)
            {
                min = math.min(min, p);
                max = math.max(max, p);
            }
            const float top = 256f;
            for (float x = min.x; x < max.x + top; x += top)
            {
                for (float z = min.y; z < max.y + top; z += top)
                {
                    Cover(ring, new float2(x + top * 0.5f, z + top * 0.5f), top, softEdge, feather, minStamp, strength, stamps);
                }
            }
        }

        private static void Cover(float2[] ring, float2 centre, float side, float softEdge, float feather, float minStamp, float strength, List<Stamp> stamps)
        {
            bool inside = PointInRing(ring, centre);
            float edge = DistanceToRing(ring, centre);
            float reach = side * 0.75f;
            if (inside && edge >= reach + feather)
            {
                stamps.Add(new Stamp { Centre = centre, Radius = reach, Strength = strength });
                return;
            }
            if (!inside && edge >= reach)
            {
                return;
            }
            if (side <= minStamp)
            {
                if (inside)
                {
                    // Feather band: strength ramps with the distance inside the outline (no spill outward);
                    // without feather the edge stamps may spill softEdge metres outward at full strength.
                    float ramp = feather > 0f ? math.saturate(edge / feather) : 1f;
                    if (ramp * strength >= 0.01f)
                    {
                        stamps.Add(new Stamp { Centre = centre, Radius = feather > 0f ? reach : math.max(softEdge, reach), Strength = strength * ramp });
                    }
                }
                return;
            }
            float half = side * 0.5f;
            Cover(ring, centre + new float2(-half, -half) * 0.5f, half, softEdge, feather, minStamp, strength, stamps);
            Cover(ring, centre + new float2(half, -half) * 0.5f, half, softEdge, feather, minStamp, strength, stamps);
            Cover(ring, centre + new float2(-half, half) * 0.5f, half, softEdge, feather, minStamp, strength, stamps);
            Cover(ring, centre + new float2(half, half) * 0.5f, half, softEdge, feather, minStamp, strength, stamps);
        }

        private static bool PointInRing(float2[] ring, float2 p)
        {
            bool c = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                if ((ring[i].y > p.y) != (ring[j].y > p.y)
                    && p.x < (ring[j].x - ring[i].x) * (p.y - ring[i].y) / (ring[j].y - ring[i].y) + ring[i].x)
                {
                    c = !c;
                }
            }
            return c;
        }

        private static float DistanceToRing(float2[] ring, float2 p)
        {
            float best = float.MaxValue;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                float2 a = ring[j], b = ring[i], ab = b - a;
                float t = math.lengthsq(ab) > 0f ? math.saturate(math.dot(p - a, ab) / math.lengthsq(ab)) : 0f;
                best = math.min(best, math.distance(p, a + ab * t));
            }
            return best;
        }
    }
}
