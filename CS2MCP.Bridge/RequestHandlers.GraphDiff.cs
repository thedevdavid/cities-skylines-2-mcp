using System;
using System.Collections.Generic;
using System.IO;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// GET /graph/diff?body=&lt;import file&gt;[,&lt;file&gt;...] or POST /graph/diff {paths:[...]} | {import:{nodes,edges}}
    /// (0.9.30): compares import files with the live network. "missing" = import edges with no live
    /// segment of any prefab within radius of their midpoint (never built, or removed later);
    /// "extra" = live segments of a prefab the import uses with no import edge of that prefab within
    /// radius of their midpoint (built by the game or another step). Counts per 500 m cell; optional
    /// area {x, z, radius} filter.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class GraphDiffDto
        {
            public List<string> paths;
            public JObject import;
            public float radius = 2f;
            public float cell = 500f;
            public bool owned;
            public float[] area;
            public int limit = 2000;
        }

        private sealed class DiffEdge
        {
            public string Id;
            public string Prefab;
            public float2[] Points;
            public float2 Mid;
        }

        private BridgeResponse GraphDiff(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            GraphDiffDto dto;
            if (request.Method == "POST")
            {
                try
                {
                    dto = JsonConvert.DeserializeObject<GraphDiffDto>(request.Body ?? "{}") ?? new GraphDiffDto();
                }
                catch (Exception e)
                {
                    return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
                }
            }
            else
            {
                dto = new GraphDiffDto();
                if (request.Query.TryGetValue("body", out string paths))
                {
                    dto.paths = new List<string>(paths.Split(','));
                }
                if (request.Query.TryGetValue("radius", out string r) && float.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rv))
                {
                    dto.radius = rv;
                }
                if (request.Query.TryGetValue("area", out string a))
                {
                    string[] parts = a.Split(',');
                    if (parts.Length == 3)
                    {
                        dto.area = Array.ConvertAll(parts, p => float.Parse(p, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
                dto.owned = request.TryGetBool("owned", out bool o) && o;
            }

            // Import edges.
            var imports = new List<DiffEdge>();
            var sources = new List<object>();
            var bodies = new List<(string name, JObject body)>();
            if (dto.import != null)
            {
                bodies.Add(("inline", dto.import));
            }
            foreach (string path in dto.paths ?? new List<string>())
            {
                try
                {
                    bodies.Add((path, JObject.Parse(File.ReadAllText(path.Trim()))));
                }
                catch (Exception e)
                {
                    return BridgeResponse.Error(400, $"could not read '{path}': {e.Message}");
                }
            }
            if (bodies.Count == 0)
            {
                return BridgeResponse.Error(400, "give ?body=<import file>[,<file>] or POST {paths:[...]} / {import:{nodes, edges}}");
            }
            foreach ((string name, JObject body) in bodies)
            {
                var nodes = new Dictionary<string, float2>();
                foreach (JToken n in body["nodes"] as JArray ?? new JArray())
                {
                    nodes[(string)n["id"]] = new float2((float)n["x"], (float)n["z"]);
                }
                int count = 0;
                foreach (JToken e in body["edges"] as JArray ?? new JArray())
                {
                    if (!nodes.TryGetValue((string)e["a"] ?? "", out float2 pa) || !nodes.TryGetValue((string)e["b"] ?? "", out float2 pb))
                    {
                        continue;
                    }
                    Bezier4x2 curve = new Bezier4x2(pa, math.lerp(pa, pb, 1f / 3f), math.lerp(pa, pb, 2f / 3f), pb);
                    if (e["control"] is JArray control && control.Count == 2)
                    {
                        curve.b = new float2((float)control[0][0], (float)control[0][2]);
                        curve.c = new float2((float)control[1][0], (float)control[1][2]);
                    }
                    else if (e["control"] is JArray q && q.Count == 1)
                    {
                        var m = new float2((float)q[0][0], (float)q[0][2]);
                        curve.b = pa + (m - pa) * (2f / 3f);
                        curve.c = pb + (m - pb) * (2f / 3f);
                    }
                    imports.Add(Sampled((string)e["id"], (string)e["prefab"], curve));
                    count++;
                }
                sources.Add(new { source = name, edges = count });
            }

            // Live segments.
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var live = new List<DiffEdge>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (NativeArray<Entity> edges = query.ToEntityArray(Allocator.Temp))
            {
                var names = new Dictionary<Entity, string>();
                foreach (Entity e in edges)
                {
                    if (!dto.owned && EntityManager.HasComponent<Owner>(e))
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!names.TryGetValue(prefab, out string name))
                    {
                        name = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?";
                        names[prefab] = name;
                    }
                    live.Add(Sampled($"{e.Index}:{e.Version}", name, EntityManager.GetComponentData<Curve>(e).m_Bezier.xz));
                }
            }

            bool InArea(float2 p) => dto.area == null || dto.area.Length < 3 || math.distance(p, new float2(dto.area[0], dto.area[1])) <= dto.area[2];
            var liveGrid = Grid(live);
            var importGrid = Grid(imports);
            var importPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DiffEdge d in imports)
            {
                importPrefabs.Add(d.Prefab ?? "");
            }

            var missing = new List<object>();
            var extra = new List<object>();
            var cells = new SortedDictionary<string, int[]>();
            void Count(float2 p, int which)
            {
                string key = $"{math.floor(p.x / dto.cell) * dto.cell},{math.floor(p.y / dto.cell) * dto.cell}";
                if (!cells.TryGetValue(key, out int[] c))
                {
                    c = new int[2];
                    cells[key] = c;
                }
                c[which]++;
            }
            int missingCount = 0, extraCount = 0;
            foreach (DiffEdge d in imports)
            {
                if (!InArea(d.Mid))
                {
                    continue;
                }
                DiffEdge near = Nearest(liveGrid, d.Mid, dto.radius, null, out _);
                if (near != null)
                {
                    continue;
                }
                missingCount++;
                Count(d.Mid, 0);
                if (missing.Count < dto.limit)
                {
                    DiffEdge closest = Nearest(liveGrid, d.Mid, 30f, null, out float dist);
                    missing.Add(new { id = d.Id, prefab = d.Prefab, x = Math.Round(d.Mid.x, 1), z = Math.Round(d.Mid.y, 1), nearestLive = closest?.Id, nearestLivePrefab = closest?.Prefab, nearestDistance = closest != null ? Math.Round(dist, 1) : (double?)null });
                }
            }
            foreach (DiffEdge d in live)
            {
                if (!importPrefabs.Contains(d.Prefab) || !InArea(d.Mid))
                {
                    continue;
                }
                if (Nearest(importGrid, d.Mid, dto.radius, d.Prefab, out _) != null)
                {
                    continue;
                }
                extraCount++;
                Count(d.Mid, 1);
                if (extra.Count < dto.limit)
                {
                    extra.Add(new { segment = d.Id, prefab = d.Prefab, x = Math.Round(d.Mid.x, 1), z = Math.Round(d.Mid.y, 1) });
                }
            }
            var cellList = new List<object>();
            foreach (KeyValuePair<string, int[]> c in cells)
            {
                cellList.Add(new { cell = c.Key, missing = c.Value[0], extra = c.Value[1] });
            }
            return BridgeResponse.Json(new
            {
                sources,
                importEdges = imports.Count,
                liveSegments = live.Count,
                radius = dto.radius,
                missingCount,
                extraCount,
                cells = cellList,
                missing,
                extra,
                note = "missing: import edge midpoint has no live segment of any prefab within radius (nearestLive = closest within 30 m). " +
                       "extra: live segment of a prefab used by the import with no import edge of that prefab within radius. " +
                       "Live segments re-split or merged by the game still count as present, since only midpoints are compared against whole curves.",
            });
        }

        private static DiffEdge Sampled(string id, string prefab, Bezier4x2 curve)
        {
            const int n = 16;
            var points = new float2[n + 1];
            for (int i = 0; i <= n; i++)
            {
                points[i] = MathUtils.Position(curve, (float)i / n);
            }
            return new DiffEdge { Id = id, Prefab = prefab, Points = points, Mid = MathUtils.Position(curve, 0.5f) };
        }

        private static Dictionary<long, List<DiffEdge>> Grid(List<DiffEdge> items)
        {
            var grid = new Dictionary<long, List<DiffEdge>>();
            foreach (DiffEdge d in items)
            {
                var seen = new HashSet<long>();
                for (int i = 0; i < d.Points.Length; i++)
                {
                    float2 p = d.Points[i];
                    float2 q = d.Points[math.min(i + 1, d.Points.Length - 1)];
                    // Cover the sample and the midpoint of each piece (pieces are at most ~1/16 of a segment).
                    foreach (float2 s in new[] { p, (p + q) * 0.5f })
                    {
                        long key = GridKey(s);
                        if (seen.Add(key))
                        {
                            if (!grid.TryGetValue(key, out List<DiffEdge> list))
                            {
                                list = new List<DiffEdge>();
                                grid[key] = list;
                            }
                            list.Add(d);
                        }
                    }
                }
            }
            return grid;
        }

        private static long GridKey(float2 p)
        {
            var c = (int2)math.floor(p / 32f);
            return ((long)c.x << 32) ^ (uint)c.y;
        }

        private static DiffEdge Nearest(Dictionary<long, List<DiffEdge>> grid, float2 p, float radius, string prefab, out float best)
        {
            best = float.MaxValue;
            DiffEdge result = null;
            int reach = (int)math.ceil(radius / 32f) + 2;
            var c = (int2)math.floor(p / 32f);
            for (int dx = -reach; dx <= reach; dx++)
            {
                for (int dz = -reach; dz <= reach; dz++)
                {
                    long key = ((long)(c.x + dx) << 32) ^ (uint)(c.y + dz);
                    if (!grid.TryGetValue(key, out List<DiffEdge> list))
                    {
                        continue;
                    }
                    foreach (DiffEdge d in list)
                    {
                        if (prefab != null && !string.Equals(d.Prefab, prefab, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        for (int i = 0; i < d.Points.Length - 1; i++)
                        {
                            float2 a = d.Points[i], b = d.Points[i + 1], ab = b - a;
                            float t = math.lengthsq(ab) > 0f ? math.saturate(math.dot(p - a, ab) / math.lengthsq(ab)) : 0f;
                            float dist = math.distance(p, a + ab * t);
                            if (dist < best)
                            {
                                best = dist;
                                result = d;
                            }
                        }
                    }
                }
            }
            return best <= radius ? result : null;
        }
    }
}
