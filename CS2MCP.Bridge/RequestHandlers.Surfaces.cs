using System;
using System.Collections.Generic;
using Game.Prefabs;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// POST /import/surfaces: surface areas (grass, sand, pavement...) from polygons in
    /// CS2 metres, run by BridgeObjectToolSystem like object imports and undone with
    /// POST /import/objects/undo?name=. Format in SURFACES.md.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class SurfaceDto
        {
            public string id;
            public string prefab;
            public float[][] polygon;
            public float[][][] holes;
        }

        public sealed class SurfaceImportDto
        {
            public string name;
            public ObjectOptionsDto options;
            public List<SurfaceDto> surfaces;
        }

        private const int kMaxSurfaceNodes = 2000;
        private EntityQuery m_SurfacePrefabQuery;
        private bool m_SurfacePrefabQueryCreated;

        private EntityQuery SurfacePrefabQuery
        {
            get
            {
                if (!m_SurfacePrefabQueryCreated)
                {
                    m_SurfacePrefabQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<SurfaceData>());
                    m_SurfacePrefabQueryCreated = true;
                }
                return m_SurfacePrefabQuery;
            }
        }
        private int m_SurfaceImportCounter;

        private BridgeResponse ImportSurfaces(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST" || string.IsNullOrEmpty(request.Body))
            {
                return BridgeResponse.Error(400, "POST a JSON body {name, options, surfaces:[{id,prefab,polygon:[[x,z],...]}]} (see SURFACES.md)");
            }
            SurfaceImportDto body;
            try
            {
                body = JsonConvert.DeserializeObject<SurfaceImportDto>(request.Body);
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body?.surfaces == null || body.surfaces.Count == 0)
            {
                return BridgeResponse.Error(400, "body needs a non-empty 'surfaces' array");
            }
            ObjectOptionsDto options = body.options ?? new ObjectOptionsDto();

            var prefabsByName = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<SurfaceData>()))
            using (NativeArray<Entity> prefabs = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefab in prefabs)
                {
                    PrefabBase prefabBase = prefabSystem.GetPrefab<PrefabBase>(prefab);
                    if (prefabBase != null && !prefabsByName.ContainsKey(prefabBase.name))
                    {
                        prefabsByName[prefabBase.name] = prefab;
                    }
                }
            }

            var job = new BridgeObjectToolSystem.ObjectJob
            {
                Id = $"srf-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{++m_SurfaceImportCounter}",
                Name = string.IsNullOrEmpty(body.name) ? null : body.name,
                Objects = new List<BridgeObjectToolSystem.ImportObject>(body.surfaces.Count),
                BatchSize = math.clamp(options.batchSize ?? 50, 1, 1000),
                SettleFrames = math.clamp(options.settleFrames ?? 1, 1, 30),
                FullReport = string.Equals(options.report, "full", StringComparison.OrdinalIgnoreCase),
                BypassValidation = options.bypassValidation == true,
            };
            job.Name = job.Name ?? job.Id;

            var problems = new List<string>();
            var unknown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int nodesTotal = 0;
            foreach (SurfaceDto dto in body.surfaces)
            {
                if (dto == null || string.IsNullOrEmpty(dto.id) || !ids.Add(dto.id))
                {
                    problems.Add($"surface '{dto?.id}': missing or duplicate id");
                    continue;
                }
                if (string.IsNullOrEmpty(dto.prefab) || !prefabsByName.TryGetValue(dto.prefab, out Entity prefab))
                {
                    unknown[dto.prefab ?? ""] = unknown.TryGetValue(dto.prefab ?? "", out int c) ? c + 1 : 1;
                    continue;
                }
                if (dto.holes != null && dto.holes.Length > 0)
                {
                    problems.Add($"surface '{dto.id}': CS2 areas have no holes; split the polygon into hole-free pieces");
                    continue;
                }
                if (!TryCleanPolygon(dto.polygon, out float2[] polygon, out string why))
                {
                    problems.Add($"surface '{dto.id}': {why}");
                    continue;
                }
                float2 centroid = float2.zero;
                foreach (float2 point in polygon)
                {
                    centroid += point;
                }
                centroid /= polygon.Length;
                nodesTotal += polygon.Length;
                job.Objects.Add(new BridgeObjectToolSystem.ImportObject
                {
                    Id = dto.id,
                    PrefabName = dto.prefab,
                    Prefab = prefab,
                    Kind = "surface",
                    Polygon = polygon,
                    Position = new float3(centroid.x, 0f, centroid.y),
                });
            }
            if (unknown.Count > 0)
            {
                var names = new List<string>();
                foreach (KeyValuePair<string, int> pair in unknown)
                {
                    names.Add($"'{pair.Key}' ({pair.Value}x)");
                }
                problems.Insert(0, $"unknown surface prefab(s): {string.Join(", ", names)} (names in SURFACES.md)");
            }
            if (problems.Count > 0)
            {
                return BridgeResponse.Json(new
                {
                    error = $"{problems.Count} problem(s) in the surface import; nothing was created",
                    problems = problems.Count > 200 ? problems.GetRange(0, 200) : problems,
                }, 400);
            }
            if (options.dryRun == true)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    valid = true,
                    mode = editor ? "editor" : "game",
                    surfaces = job.Objects.Count,
                    nodes = nodesTotal,
                    prefabsResolved = CountPrefabs(job),
                    batchSize = job.BatchSize,
                });
            }
            if (ObjectToolsBusy(out error))
            {
                return error;
            }
            if (!World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().TryStartImport(job, request))
            {
                return BridgeResponse.Error(409, "an object or surface import is already running");
            }
            return null;
        }

        /// <summary>
        /// Drops a closing point equal to the first and consecutive duplicates; checks
        /// bounds, node count and area. Self-intersections are left to the game, which
        /// rejects them as InvalidShape (the batch is then bisected down to that surface).
        /// </summary>
        private static bool TryCleanPolygon(float[][] raw, out float2[] polygon, out string why)
        {
            polygon = null;
            if (raw == null || raw.Length < 3)
            {
                why = "polygon needs at least 3 points [[x,z],...]";
                return false;
            }
            var points = new List<float2>(raw.Length);
            foreach (float[] p in raw)
            {
                if (p == null || p.Length < 2)
                {
                    why = "every point must be [x, z]";
                    return false;
                }
                var point = new float2(p[0], p[1]);
                if (math.abs(point.x) > kPlayableHalf + 1f || math.abs(point.y) > kPlayableHalf + 1f)
                {
                    why = $"point ({point.x:F1}, {point.y:F1}) is outside the playable area (±{kPlayableHalf} m)";
                    return false;
                }
                if (points.Count == 0 || math.distance(points[points.Count - 1], point) > 0.01f)
                {
                    points.Add(point);
                }
            }
            if (points.Count > 1 && math.distance(points[0], points[points.Count - 1]) <= 0.01f)
            {
                points.RemoveAt(points.Count - 1);
            }
            if (points.Count < 3)
            {
                why = "polygon has fewer than 3 distinct points";
                return false;
            }
            if (points.Count > kMaxSurfaceNodes)
            {
                why = $"polygon has {points.Count} points; simplify it to at most {kMaxSurfaceNodes}";
                return false;
            }
            double area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                float2 a = points[i];
                float2 b = points[(i + 1) % points.Count];
                area += (double)a.x * b.y - (double)b.x * a.y;
            }
            if (Math.Abs(area) * 0.5 < 1.0)
            {
                why = "polygon area is under 1 m²";
                return false;
            }
            polygon = points.ToArray();
            why = null;
            return true;
        }
    }
}
