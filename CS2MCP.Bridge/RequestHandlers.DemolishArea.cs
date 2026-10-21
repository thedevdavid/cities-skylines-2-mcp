using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.49: POST /build/demolish-area removes the top-level objects (and optionally surfaces)
    /// inside a polygon or bounds, by position, through the bulldoze batches the object undo uses.
    /// Import undo records do not survive a reload; this lets one object layer be redone on a
    /// loaded map. Nets are never touched; buildings only with kinds ["building"].
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class DemolishBoundsDto
        {
            public float minX;
            public float minZ;
            public float maxX;
            public float maxZ;
        }

        public sealed class DemolishAreaDto
        {
            public float[][] polygon;
            public DemolishBoundsDto bounds;
            public List<string> prefabs;
            public List<string> kinds;
            public bool? dryRun;
            public bool? force;
        }

        private const float kDemolishMaxArea = 4_000_000f;

        private BridgeResponse DemolishArea(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return BridgeResponse.Error(405, "POST /build/demolish-area {polygon:[[x,z],...] | bounds:{minX,minZ,maxX,maxZ}, prefabs?, kinds?: [object, tree, surface, building], dryRun?, force?}");
            }
            DemolishAreaDto body;
            try
            {
                body = JsonConvert.DeserializeObject<DemolishAreaDto>(request.Body ?? "{}") ?? new DemolishAreaDto();
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            float2[] ring;
            if (body.polygon != null && body.polygon.Length > 0)
            {
                if (!TryCleanPolygon(body.polygon, out ring, out string why))
                {
                    return BridgeResponse.Error(400, $"polygon: {why}");
                }
            }
            else if (body.bounds != null && body.bounds.maxX > body.bounds.minX && body.bounds.maxZ > body.bounds.minZ)
            {
                DemolishBoundsDto b = body.bounds;
                ring = new[] { new float2(b.minX, b.minZ), new float2(b.maxX, b.minZ), new float2(b.maxX, b.maxZ), new float2(b.minX, b.maxZ) };
            }
            else
            {
                return BridgeResponse.Error(400, "give polygon [[x,z],...] (3+ points) or bounds {minX,minZ,maxX,maxZ}");
            }
            float area = 0f;
            float2 min = ring[0], max = ring[0];
            for (int i = 0; i < ring.Length; i++)
            {
                float2 p = ring[i], q = ring[(i + 1) % ring.Length];
                area += p.x * q.y - q.x * p.y;
                min = math.min(min, p);
                max = math.max(max, p);
            }
            area = math.abs(area) * 0.5f;
            if (area > kDemolishMaxArea && body.force != true)
            {
                return BridgeResponse.Error(400, $"area {area / 1e6f:F2} km² is larger than 4 km²; pass force:true if that is intended");
            }
            var kinds = new HashSet<string>(body.kinds ?? new List<string> { "object", "tree", "surface" }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> prefabFilter = body.prefabs != null && body.prefabs.Count > 0
                ? new HashSet<string>(body.prefabs, StringComparer.OrdinalIgnoreCase)
                : null;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = new Dictionary<Entity, string>();
            string NameOf(Entity prefab)
            {
                if (!names.TryGetValue(prefab, out string name))
                {
                    name = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?";
                    names[prefab] = name;
                }
                return name;
            }
            bool Inside(float2 p) => p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y && PointInRing(ring, p);

            var targets = new List<Entity>();
            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byKind = new Dictionary<string, int>();
            void Take(Entity e, string kind, string prefab)
            {
                targets.Add(e);
                string key = $"{kind}: {prefab}";
                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                byKind[kind] = byKind.TryGetValue(kind, out int k) ? k + 1 : 1;
            }

            using (EntityQuery objects = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (NativeArray<Entity> entities = objects.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    float3 position = EntityManager.GetComponentData<Transform>(e).m_Position;
                    if (!Inside(position.xz))
                    {
                        continue;
                    }
                    string kind = EntityManager.HasComponent<Game.Buildings.Building>(e) ? "building"
                        : EntityManager.HasComponent<Game.Objects.Tree>(e) ? "tree"
                        : "object";
                    if (!kinds.Contains(kind))
                    {
                        continue;
                    }
                    string prefab = NameOf(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab);
                    if (prefabFilter != null && !prefabFilter.Contains(prefab))
                    {
                        continue;
                    }
                    Take(e, kind, prefab);
                }
            }
            if (kinds.Contains("surface"))
            {
                using (EntityQuery surfaces = EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Game.Areas.Surface>(), ComponentType.ReadOnly<Game.Areas.Node>(), ComponentType.ReadOnly<PrefabRef>() },
                    None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
                }))
                using (NativeArray<Entity> entities = surfaces.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity e in entities)
                    {
                        DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(e, true);
                        if (nodes.Length == 0)
                        {
                            continue;
                        }
                        float2 centroid = float2.zero;
                        for (int i = 0; i < nodes.Length; i++)
                        {
                            centroid += nodes[i].m_Position.xz;
                        }
                        centroid /= nodes.Length;
                        if (!Inside(centroid))
                        {
                            continue;
                        }
                        string prefab = NameOf(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab);
                        if (prefabFilter != null && !prefabFilter.Contains(prefab))
                        {
                            continue;
                        }
                        Take(e, "surface", prefab);
                    }
                }
            }

            var perPrefab = new List<object>();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                perPrefab.Add(new { prefab = pair.Key, count = pair.Value });
            }
            if (body.dryRun == true || targets.Count == 0)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = body.dryRun == true,
                    areaKm2 = Math.Round(area / 1e6f, 4),
                    kinds,
                    total = targets.Count,
                    byKind,
                    perPrefab,
                    note = targets.Count == 0 ? "nothing matched" : "dry run: nothing was removed",
                });
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }
            string label = $"demolish-area-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            Mod.Log.Info($"{label}: removing {targets.Count} ({string.Join(", ", byKind)})");
            if (!World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().TryStartDelete(targets, label, request, out string reason))
            {
                return BridgeResponse.Error(409, reason);
            }
            // Completed by BridgeObjectToolSystem (same report as /import/objects/undo).
            return null;
        }
    }
}
