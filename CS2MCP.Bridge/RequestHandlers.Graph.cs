using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// GET /graph/export: every network segment and node of the map in one compact
    /// JSON document, written to a file on disk (path=...) so large maps do not go
    /// through the MCP client. Works in the editor and in a city.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const int kGraphInlineLimit = 2000;

        private BridgeResponse ExportGraph(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            request.Query.TryGetValue("path", out string path);
            request.Query.TryGetValue("prefab", out string prefabFilter);
            bool includeOwned = request.TryGetBool("owned", out bool owned) && owned;
            bool curves = request.TryGetBool("curves", out bool rawCurves) && rawCurves;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var prefabNames = new Dictionary<Entity, string>();
            var segments = new List<object>();
            var nodes = new Dictionary<Entity, int>();
            var perPrefab = new Dictionary<string, int>();
            using (NativeArray<Entity> edges = NetEdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity edge in edges)
                {
                    if (!includeOwned && EntityManager.HasComponent<Owner>(edge))
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    if (!prefabNames.TryGetValue(prefab, out string name))
                    {
                        PrefabBase prefabBase = prefabSystem.GetPrefab<PrefabBase>(prefab);
                        name = prefabBase != null ? prefabBase.name : "<unknown>";
                        prefabNames[prefab] = name;
                    }
                    if (!string.IsNullOrEmpty(prefabFilter) && name.IndexOf(prefabFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    Edge ends = EntityManager.GetComponentData<Edge>(edge);
                    Curve curve = EntityManager.GetComponentData<Curve>(edge);
                    float2 elevation = EntityManager.HasComponent<Game.Net.Elevation>(edge)
                        ? EntityManager.GetComponentData<Game.Net.Elevation>(edge).m_Elevation
                        : float2.zero;
                    nodes[ends.m_Start] = 0;
                    nodes[ends.m_End] = 0;
                    perPrefab[name] = perPrefab.TryGetValue(name, out int n) ? n + 1 : 1;
                    segments.Add(new
                    {
                        id = Id(edge),
                        prefab = name,
                        a = Id(ends.m_Start),
                        b = Id(ends.m_End),
                        length = Math.Round(curve.m_Length, 2),
                        elevation = math.any(elevation != 0f) ? new[] { Math.Round(elevation.x, 2), Math.Round(elevation.y, 2) } : null,
                        owned = includeOwned && EntityManager.HasComponent<Owner>(edge) ? true : (bool?)null,
                        curve = curves ? CurvePoints(curve.m_Bezier) : null,
                    });
                }
            }

            var nodeItems = new List<object>(nodes.Count);
            foreach (Entity node in nodes.Keys)
            {
                if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
                {
                    continue;
                }
                float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
                int degree = EntityManager.HasBuffer<ConnectedEdge>(node)
                    ? EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true).Length
                    : 0;
                nodeItems.Add(new
                {
                    id = Id(node),
                    x = Math.Round(position.x, 2),
                    y = Math.Round(position.y, 2),
                    z = Math.Round(position.z, 2),
                    degree,
                });
            }

            var document = new
            {
                exportedAt = DateTime.UtcNow.ToString("o"),
                mode = editor ? "editor" : "game",
                prefabFilter,
                includeOwned,
                note = "ids are 'index:version' entity ids; degree counts every connected segment, including ones filtered out",
                counts = new { segments = segments.Count, nodes = nodeItems.Count, perPrefab },
                segments,
                nodes = nodeItems,
            };

            if (string.IsNullOrEmpty(path))
            {
                if (segments.Count > kGraphInlineLimit)
                {
                    return BridgeResponse.Error(400, $"{segments.Count} segments match; pass path=<file> to write them to disk " +
                        $"(inline answers are limited to {kGraphInlineLimit} segments)");
                }
                return BridgeResponse.Json(document);
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(fullPath, JsonConvert.SerializeObject(document, Formatting.None), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(500, $"could not write '{path}': {e.Message}");
            }
            return BridgeResponse.Json(new
            {
                path = fullPath,
                bytes = new FileInfo(fullPath).Length,
                counts = new { segments = segments.Count, nodes = nodeItems.Count, perPrefab },
            });
        }

        private static string Id(Entity entity)
        {
            return $"{entity.Index}:{entity.Version}";
        }

        private static double[][] CurvePoints(Colossal.Mathematics.Bezier4x3 bezier)
        {
            return new[]
            {
                new[] { Math.Round(bezier.a.x, 2), Math.Round(bezier.a.y, 2), Math.Round(bezier.a.z, 2) },
                new[] { Math.Round(bezier.b.x, 2), Math.Round(bezier.b.y, 2), Math.Round(bezier.b.z, 2) },
                new[] { Math.Round(bezier.c.x, 2), Math.Round(bezier.c.y, 2), Math.Round(bezier.c.z, 2) },
                new[] { Math.Round(bezier.d.x, 2), Math.Round(bezier.d.y, 2), Math.Round(bezier.d.z, 2) },
            };
        }
    }
}
