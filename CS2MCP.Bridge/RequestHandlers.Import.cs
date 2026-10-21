using System;
using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Batch network import (POST /import/network), its progress
    /// (GET /import/status) and undo (POST /import/undo). The body format is
    /// documented in IMPORT_SCHEMA.md; the work runs in BridgeImportToolSystem.
    /// Works in the map editor and in a city.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class ImportEntityRef
        {
            public int index;
            public int version;
        }

        public sealed class ImportSnapDto
        {
            public float radius;
            public string prefabContains;
        }

        public sealed class ImportAttachDto
        {
            public ImportSnapDto a;
            public ImportSnapDto b;
        }

        public sealed class ImportConnectDto
        {
            public int? index;
            public int? version;
            public string voltage;
            public float? radius;
        }

        public sealed class ImportNodeDto
        {
            public ImportConnectDto connectTo;
            public ImportSnapDto snapToSegment;
            public string id;
            public float x;
            public float? y;
            public float z;
            public float? elevation;
            public ImportEntityRef entity;
        }

        public sealed class ImportEdgeDto
        {
            public string id;
            public string a;
            public string b;
            public string prefab;
            public float[][] control;
            public float[] elevation;
            public ImportAttachDto attachTo;
            public string wall;
            /// <summary>Accepted and ignored: CourseSplitSystem assigns fixed indices itself.</summary>
            public int? fixedIndex;
            /// <summary>Delete the generic pillars of this edge's segments after the import.</summary>
            public bool? noPillars;
            /// <summary>Water-flow nets (dams): auto (default), asIs, invert.</summary>
            public string flow;
        }

        public sealed class ImportOptionsDto
        {
            public int? batchSize;
            public bool? nodesFirst;
            public string heightMode;
            public int? settleFrames;
            public bool? dryRun;
            public string report;
            public bool? bypassValidation;
            /// <summary>Bridge-prefab checks: "error" (default, blocks), "warn", "off".</summary>
            public string bridgeRules;
            /// <summary>0.9.33: allow edges of nets another prefab spawns as AuxiliaryNets (refused by default).</summary>
            public bool? allowAuxiliaryNets;
        }

        public sealed class ImportRequestDto
        {
            public string name;
            public List<ImportNodeDto> nodes;
            public List<ImportEdgeDto> edges;
            public ImportOptionsDto options;
            /// <summary>Accepted at the top level too (same as options.dryRun).</summary>
            public bool? dryRun;
        }

        private const float kImportMinEdgeLength = 4f;
        private const float kImportMaxEdgeLength = 1500f;

        private int m_ImportCounter;

        private BridgeResponse ImportStatus()
        {
            return BridgeResponse.Json(World.GetOrCreateSystemManaged<BridgeImportToolSystem>().Status());
        }

        private BridgeResponse ImportReport(BridgeRequest request)
        {
            request.Query.TryGetValue("status", out string status);
            request.Query.TryGetValue("prefab", out string prefab);
            int offset = request.TryGetInt("offset", out int rawOffset) ? math.max(rawOffset, 0) : 0;
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 1000) : 200;
            return BridgeResponse.Json(World.GetOrCreateSystemManaged<BridgeImportToolSystem>().QueryResults(status, prefab, offset, limit));
        }

        private BridgeResponse UndoImport(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return BridgeResponse.Error(405, "use POST /import/undo (optionally ?id=<importId>)");
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }
            request.Query.TryGetValue("id", out string importId);
            BridgeImportToolSystem tool = World.GetOrCreateSystemManaged<BridgeImportToolSystem>();
            if (!tool.TryStartUndo(importId, request, out string reason))
            {
                return BridgeResponse.Error(409, reason);
            }
            return null;
        }

        private BridgeResponse ImportNetwork(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST" || string.IsNullOrEmpty(request.Body))
            {
                return BridgeResponse.Error(400, "POST a JSON body {nodes:[...], edges:[...], options?} (see IMPORT_SCHEMA.md)");
            }

            ImportRequestDto body;
            try
            {
                body = JsonConvert.DeserializeObject<ImportRequestDto>(request.Body);
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body == null || body.nodes == null || body.edges == null || body.edges.Count == 0)
            {
                return BridgeResponse.Error(400, "body needs non-empty 'nodes' and 'edges' arrays");
            }

            ImportOptionsDto options = body.options ?? new ImportOptionsDto();
            bool absoluteHeights;
            switch ((options.heightMode ?? "elevation").ToLowerInvariant())
            {
                case "elevation":
                    absoluteHeights = false;
                    break;
                case "absolute":
                    absoluteHeights = true;
                    break;
                default:
                    return BridgeResponse.Error(400, "options.heightMode must be 'elevation' (default) or 'absolute'");
            }

            var problems = new List<string>();
            var job = new BridgeImportToolSystem.ImportJob
            {
                Id = $"imp-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{++m_ImportCounter}",
                Name = body.name,
                Nodes = new List<BridgeImportToolSystem.ImportNode>(body.nodes.Count),
                Edges = new List<BridgeImportToolSystem.ImportEdge>(body.edges.Count),
                BatchSize = math.clamp(options.batchSize ?? 64, 1, 512),
                NodesFirst = options.nodesFirst ?? true,
                SettleFrames = math.clamp(options.settleFrames ?? 1, 1, 30),
                FullReport = string.Equals(options.report, "full", StringComparison.OrdinalIgnoreCase),
                BypassValidation = options.bypassValidation == true,
            };

            TerrainHeightData heightData = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var rejectedNodes = new HashSet<string>(StringComparer.Ordinal);
            var explicitElevation = new List<bool>(body.nodes.Count);
            foreach (ImportNodeDto dto in body.nodes)
            {
                if (dto == null || string.IsNullOrEmpty(dto.id))
                {
                    problems.Add("a node has no id");
                    continue;
                }
                if (nodeIndex.ContainsKey(dto.id))
                {
                    problems.Add($"duplicate node id '{dto.id}'");
                    continue;
                }
                var node = new BridgeImportToolSystem.ImportNode { Id = dto.id, XZ = new float2(dto.x, dto.z) };
                bool hasElevation = dto.elevation.HasValue;
                if (hasElevation)
                {
                    node.Elevation = dto.elevation.Value;
                }
                else if (absoluteHeights && dto.y.HasValue)
                {
                    float ground = TerrainUtils.SampleHeight(ref heightData, new float3(dto.x, 0f, dto.z));
                    node.Elevation = dto.y.Value - ground;
                    node.AbsoluteY = dto.y.Value;
                    hasElevation = true;
                }
                if (dto.entity != null)
                {
                    var entity = new Entity { Index = dto.entity.index, Version = dto.entity.version };
                    if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Node>(entity))
                    {
                        problems.Add($"node '{dto.id}': entity {dto.entity.index}:{dto.entity.version} is not an existing network node");
                    }
                    else
                    {
                        node.Entity = entity;
                        node.Given = true;
                        node.XZ = EntityManager.GetComponentData<Node>(entity).m_Position.xz;
                    }
                }
                if (dto.connectTo != null)
                {
                    if (!TryResolveConnect(dto.connectTo, node.XZ, out Entity marker, out string why))
                    {
                        problems.Add($"node '{dto.id}': {why}");
                        rejectedNodes.Add(dto.id);
                        continue;
                    }
                    node.Entity = marker;
                    node.Given = true;
                    node.XZ = EntityManager.GetComponentData<Node>(marker).m_Position.xz;
                }
                node.OriginalXZ = node.XZ;
                if (dto.snapToSegment != null && !ApplySnap(node, dto.snapToSegment, $"node '{dto.id}'", problems, job))
                {
                    continue;
                }
                nodeIndex[dto.id] = job.Nodes.Count;
                job.Nodes.Add(node);
                explicitElevation.Add(hasElevation);
            }

            var prefabs = NetPrefabsByName();
            Dictionary<string, string> auxiliaryOwners = null;
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ImportEdgeDto dto in body.edges)
            {
                if (dto == null || string.IsNullOrEmpty(dto.id))
                {
                    problems.Add("an edge has no id");
                    continue;
                }
                if (!edgeIds.Add(dto.id))
                {
                    problems.Add($"duplicate edge id '{dto.id}'");
                    continue;
                }
                if ((dto.a != null && rejectedNodes.Contains(dto.a)) || (dto.b != null && rejectedNodes.Contains(dto.b)))
                {
                    problems.Add($"edge '{dto.id}': skipped because its node '{(rejectedNodes.Contains(dto.a ?? "") ? dto.a : dto.b)}' was rejected (see that node's problem)");
                    continue;
                }
                if (dto.a == null || dto.b == null || !nodeIndex.TryGetValue(dto.a, out int a) || !nodeIndex.TryGetValue(dto.b, out int b))
                {
                    problems.Add($"edge '{dto.id}': a/b must name node ids from 'nodes'");
                    continue;
                }
                if (a == b)
                {
                    problems.Add($"edge '{dto.id}': a and b are the same node");
                    continue;
                }
                if (string.IsNullOrEmpty(dto.prefab) || !prefabs.TryGetValue(dto.prefab, out Entity prefabEntity))
                {
                    problems.Add($"edge '{dto.id}': unknown network prefab '{dto.prefab}' (exact names via /prefabs?category=net&query=...)");
                    continue;
                }
                auxiliaryOwners = auxiliaryOwners ?? AuxiliaryNetOwners();
                if (options.allowAuxiliaryNets != true && auxiliaryOwners.TryGetValue(dto.prefab, out string auxOwner))
                {
                    // 0.9.33: A-v8 crashed natively (no managed stack) right after a standalone
                    // "BXP Double Deck Suspension Bridge - Lower Highway" edge was built: that net is
                    // the upper deck's AuxiliaryNets entry, which the upper deck spawns itself.
                    problems.Add($"edge '{dto.id}': '{dto.prefab}' is an auxiliary net of '{auxOwner}' (spawned by that prefab, not placeable on its own); import '{auxOwner}' only, or set options.allowAuxiliaryNets");
                    continue;
                }
                if (options.allowAuxiliaryNets != true
                    && World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(prefabEntity) is PrefabBase ownerPrefab
                    && ownerPrefab.TryGet(out AuxiliaryNets ownAux) && ownAux.m_AuxiliaryNets != null && ownAux.m_AuxiliaryNets.Length > 0)
                {
                    // 0.9.36: the owner side. A lone "BXP Double Deck Suspension Bridge - Highway" edge
                    // (976 m, nodesFirst) killed the game natively every time (A-v8 16:11 and 17:44);
                    // CourseSplitSystem builds the aux courses in Burst jobs without bounds checks.
                    var paired = new List<string>();
                    foreach (AuxiliaryNetInfo info in ownAux.m_AuxiliaryNets)
                    {
                        paired.Add(info?.m_Prefab != null ? info.m_Prefab.name : "?");
                    }
                    problems.Add($"edge '{dto.id}': '{dto.prefab}' spawns auxiliary nets ({string.Join(", ", paired)}); importing it crashed the game natively (A-v8), so it is refused; place it by hand with the net tool, or set options.allowAuxiliaryNets at your own risk");
                    continue;
                }
                float2[] control = null;
                if (dto.control != null && dto.control.Length > 0)
                {
                    if (dto.control.Length > 2 || Array.Exists(dto.control, p => p == null || p.Length != 3))
                    {
                        problems.Add($"edge '{dto.id}': control must be [[x,y,z]] or [[x,y,z],[x,y,z]]");
                        continue;
                    }
                    control = new float2[dto.control.Length];
                    for (int i = 0; i < control.Length; i++)
                    {
                        control[i] = new float2(dto.control[i][0], dto.control[i][2]);
                    }
                }
                if (dto.attachTo != null)
                {
                    if (dto.attachTo.a != null && job.Nodes[a].SnapRadius <= 0f
                        && !ApplySnap(job.Nodes[a], dto.attachTo.a, $"edge '{dto.id}' attachTo.a", problems, job))
                    {
                        continue;
                    }
                    if (dto.attachTo.b != null && job.Nodes[b].SnapRadius <= 0f
                        && !ApplySnap(job.Nodes[b], dto.attachTo.b, $"edge '{dto.id}' attachTo.b", problems, job))
                    {
                        continue;
                    }
                }
                if (dto.elevation != null && dto.elevation.Length != 2)
                {
                    problems.Add($"edge '{dto.id}': elevation must be [e1, e2]");
                    continue;
                }

                float length = math.distance(job.Nodes[a].XZ, job.Nodes[b].XZ);
                if (length < kImportMinEdgeLength || length > kImportMaxEdgeLength)
                {
                    problems.Add($"edge '{dto.id}': end-to-end length {length:F1} m is outside {kImportMinEdgeLength}-{kImportMaxEdgeLength} m; split or merge it");
                    continue;
                }

                // Edge elevations only fill in nodes without their own: a node has one
                // height, so every edge meeting there shares the exact same position.
                if (dto.elevation != null)
                {
                    ApplyEdgeElevation(job, explicitElevation, a, dto.elevation[0], dto.id);
                    ApplyEdgeElevation(job, explicitElevation, b, dto.elevation[1], dto.id);
                }

                // Standalone nodes take the prefab of the first edge that uses them.
                bool courseOwner = EntityManager.HasComponent<NetGeometryData>(prefabEntity)
                    && (EntityManager.GetComponentData<NetGeometryData>(prefabEntity).m_Flags & Game.Net.GeometryFlags.SubOwner) != 0;
                foreach (int end in new[] { a, b })
                {
                    BridgeImportToolSystem.ImportNode node = job.Nodes[end];
                    // A dam's nodes belong to the plant it spawns: build them with the edge, not first.
                    node.NoStandalone |= courseOwner;
                    if (node.CreatePrefab == Entity.Null)
                    {
                        node.CreatePrefab = prefabEntity;
                        node.CreatePrefabName = dto.prefab;
                        float2 other = job.Nodes[end == a ? b : a].XZ;
                        node.Direction = math.normalizesafe(other - node.XZ);
                    }
                }

                CompositionFlags upgrades = default;
                switch ((dto.wall ?? "").Trim().ToLowerInvariant())
                {
                    case "":
                    case "none":
                        break;
                    case "left":
                        upgrades.m_Left |= CompositionFlags.Side.Raised;
                        break;
                    case "right":
                        upgrades.m_Right |= CompositionFlags.Side.Raised;
                        break;
                    case "both":
                        upgrades.m_Left |= CompositionFlags.Side.Raised;
                        upgrades.m_Right |= CompositionFlags.Side.Raised;
                        break;
                    default:
                        problems.Add($"edge '{dto.id}': wall must be left, right, both or none");
                        continue;
                }
                job.Edges.Add(new BridgeImportToolSystem.ImportEdge
                {
                    Id = dto.id,
                    A = a,
                    B = b,
                    PrefabName = dto.prefab,
                    Prefab = prefabEntity,
                    Control = control,
                    Upgrades = upgrades,
                    NoPillars = dto.noPillars == true,
                    Flow = dto.flow,
                });
                if (dto.fixedIndex.HasValue && job.Warnings.Count < 1000)
                {
                    job.Warnings.Add($"edge '{dto.id}': fixedIndex ignored; send one edge per fixed-span bridge and the game numbers the spans (IMPORT_SCHEMA.md, bridges)");
                }
            }

            string bridgeMode = (options.bridgeRules ?? "error").Trim().ToLowerInvariant();
            if (bridgeMode != "error" && bridgeMode != "warn" && bridgeMode != "off")
            {
                problems.Add("options.bridgeRules must be error, warn or off");
            }
            List<object> bridgeReport = problems.Count == 0 ? CheckBridgeEdges(job, bridgeMode, problems, ref heightData) : new List<object>();

            if (problems.Count > 0)
            {
                return BridgeResponse.Json(new
                {
                    error = $"{problems.Count} problem(s) in the import body; nothing was built",
                    problems = problems.Count > 200 ? problems.GetRange(0, 200) : problems,
                    bridges = bridgeReport.Count > 0 ? bridgeReport : null,
                }, 400);
            }

            int unusedNodes = 0;
            foreach (BridgeImportToolSystem.ImportNode node in job.Nodes)
            {
                if (node.CreatePrefab == Entity.Null)
                {
                    unusedNodes++;
                }
            }
            if (unusedNodes > 0)
            {
                job.Warnings.Add($"{unusedNodes} node(s) are not used by any edge and were skipped");
            }

            if (options.dryRun == true || body.dryRun == true)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    valid = true,
                    mode = editor ? "editor" : "game",
                    nodes = job.Nodes.Count,
                    edges = job.Edges.Count,
                    batchSize = job.BatchSize,
                    nodesFirst = job.NodesFirst,
                    warnings = job.Warnings,
                    bridges = bridgeReport,
                });
            }

            if (TransitToolsBusy(out error))
            {
                return error;
            }
            BridgeImportToolSystem tool = World.GetOrCreateSystemManaged<BridgeImportToolSystem>();
            if (!tool.TryStartImport(job, request))
            {
                return BridgeResponse.Error(409, "an import or undo is already running; see GET /import/status");
            }
            // Completed by BridgeImportToolSystem when every batch has been applied or rejected.
            return null;
        }

        private const float kMaxSnapRadius = 100f;

        private static bool ApplySnap(BridgeImportToolSystem.ImportNode node, ImportSnapDto snap, string what,
            List<string> problems, BridgeImportToolSystem.ImportJob job)
        {
            if (!(snap.radius > 0f) || snap.radius > kMaxSnapRadius)
            {
                problems.Add($"{what}: snap radius must be > 0 and <= {kMaxSnapRadius} m");
                return false;
            }
            if (node.Entity != Entity.Null)
            {
                job.Warnings.Add($"{what}: node '{node.Id}' already names an entity; snapping ignored");
                return true;
            }
            node.SnapRadius = snap.radius;
            node.SnapPrefabContains = snap.prefabContains;
            return true;
        }

        private static void ApplyEdgeElevation(BridgeImportToolSystem.ImportJob job, List<bool> explicitElevation, int node, float elevation, string edgeId)
        {
            if (!explicitElevation[node])
            {
                job.Nodes[node].Elevation = elevation;
                explicitElevation[node] = true;
            }
            else if (math.abs(job.Nodes[node].Elevation - elevation) > 0.01f)
            {
                job.Warnings.Add($"edge '{edgeId}': elevation {elevation:F2} at node '{job.Nodes[node].Id}' ignored; the node uses {job.Nodes[node].Elevation:F2}");
            }
        }

        /// <summary>Net prefab name -> name of a net prefab that lists it in its AuxiliaryNets.</summary>
        private Dictionary<string, string> AuxiliaryNetOwners()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = NetPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab == null || !prefab.TryGet(out AuxiliaryNets aux) || aux.m_AuxiliaryNets == null)
                    {
                        continue;
                    }
                    foreach (AuxiliaryNetInfo info in aux.m_AuxiliaryNets)
                    {
                        if (info?.m_Prefab != null && !result.ContainsKey(info.m_Prefab.name))
                        {
                            result[info.m_Prefab.name] = prefab.name;
                        }
                    }
                }
            }
            return result;
        }

        private Dictionary<string, Entity> NetPrefabsByName()
        {
            var result = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = NetPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab != null && !result.ContainsKey(prefab.name))
                    {
                        result[prefab.name] = entity;
                    }
                }
            }
            return result;
        }
    }
}
