using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using Node = Game.Net.Node;
using SubLane = Game.Net.SubLane;

namespace CS2MCP
{
    /// <summary>
    /// Writes Traffic (PDX 80095, 0.2.13) lane connections the way its TLEDataMigrationSystem does:
    /// node gets ModifiedConnections + a ModifiedLaneConnections buffer (one entry per modified source lane);
    /// each source lane gets an entity with DataOwner(node), PrefabRef(Traffic.FakePrefab) and a
    /// GeneratedConnection buffer listing every connection that lane keeps. Lanes not listed keep the
    /// game's defaults. Only this class references Traffic types (compile-time reference to Traffic.dll).
    ///
    /// Mapping (CS1 -> CS2), all by position: node = nearest CS2 net node to the imported node position;
    /// edge = the node's edge whose curve, at the probe distance from the node, is nearest the CS1 segment's
    /// probe point; lane = the CS2 car lane (flowing into the node for a source, out of it for a target)
    /// whose lateral offset from its direction group's centre is nearest the CS1 lane's. Lane counts must
    /// agree, otherwise the connection is skipped and reported. If any target of a CS1 source lane is skipped,
    /// all of that lane's connections are skipped (a partial list would drop a connection TM:PE kept).
    /// </summary>
    public static class TrafficLaneImport
    {
        private sealed class EdgeAtNode
        {
            public Entity Edge;
            public bool IsEnd;
            public float3[] Samples; // centre line from the node outward
            public float[] Distance;  // arc length of each sample from the node
            public Entity ExtendedThrough; // 0.9.30: the continuing edge appended to a short stub
        }

        private sealed class Cs2Lane
        {
            public Entity SubLane;
            public int Index;
            public float Lateral;
            public float Offset;
        }

        private sealed class MappedLane
        {
            public EdgeAtNode Edge;
            public Cs2Lane Lane;
            public float EdgeDistance;
            public float OffsetDelta;
            public bool RankAgrees;
        }

        private struct TargetKey : IEquatable<TargetKey>
        {
            public Entity Edge;
            public int Lane;
            public bool Equals(TargetKey o) => Edge == o.Edge && Lane == o.Lane;
            public override int GetHashCode() => Edge.GetHashCode() * 397 ^ Lane;
        }

        private sealed class SourceGroup
        {
            public EdgeAtNode Edge;
            public int Lane;
            public readonly List<(TargetKey key, EdgeAtNode edge, bool unsafeLane)> Targets = new List<(TargetKey, EdgeAtNode, bool)>();
            public readonly List<Dictionary<string, object>> Results = new List<Dictionary<string, object>>();
        }

        private sealed class NodePlan
        {
            public TmpeNodeDto Dto;
            public Entity Node;
            public float3 Position;
            public readonly Dictionary<TargetKey, SourceGroup> Groups = new Dictionary<TargetKey, SourceGroup>();
            public readonly List<(SourceGroup group, GeneratedConnection[] connections)> Write = new List<(SourceGroup, GeneratedConnection[])>();
        }

        // ---------------------------------------------------------------- import

        public static object Run(BridgeTmpeImportSystem system, TmpeImportDto body, TmpeUndoLog undoLog, string trafficVersion)
        {
            EntityManager em = system.EntityManager;
            em.CompleteAllTrackedJobs();
            TmpeOptionsDto options = body.options ?? new TmpeOptionsDto();
            Entity fakePrefab = GetFakePrefab();
            if (fakePrefab == Entity.Null || !em.Exists(fakePrefab))
            {
                throw new InvalidOperationException("Traffic.FakePrefab entity not found (Traffic creates it when a map loads); load the map with Traffic enabled");
            }

            string importId = $"tmpe-{DateTime.Now:yyyyMMdd-HHmmss}";
            var nodeResults = new List<object>();
            var connectionResults = new List<Dictionary<string, object>>();
            var arrowResults = new List<Dictionary<string, object>>();
            var plans = new List<NodePlan>();
            var nodeChecks = new List<object>();

            List<(Entity entity, float3 position)> netNodes = CollectNodes(em);

            foreach (TmpeNodeDto nodeDto in body.nodes)
            {
                // 0.9.26: match in x/z only (CS1 and CS2 heights differ; stacked interchange nodes are
                // told apart by their edges), scoring every node within nodeSearchRadius by how well
                // its edges pass the probe points of the CS1 segments this node's data refers to.
                Entity node = Entity.Null;
                float best = float.MaxValue;
                float3 position = default;
                string matchNote = null;
                {
                    var wantedXZ = new float2(nodeDto.x, nodeDto.z);
                    List<TmpeSegmentRefDto> refs = SegmentRefs(nodeDto);
                    int bestMatched = -1;
                    float bestScore = float.MaxValue;
                    float searchRadius = math.max(options.nodeRadius, options.nodeSearchRadius);
                    foreach ((Entity entity, float3 nodePosition) in netNodes)
                    {
                        // Cheap pre-filter on Node.m_Position, then the curve meeting point: the
                        // game's node reduction can leave m_Position up to tens of metres from where
                        // the edge curves actually meet (A-v6: node 54722, 25.8 m).
                        if (math.distance(nodePosition.xz, wantedXZ) > searchRadius + 60f)
                        {
                            continue;
                        }
                        List<EdgeAtNode> candidateEdges = CollectEdges(em, entity);
                        float3 p = EffectivePosition(candidateEdges, nodePosition);
                        float dxz = math.distance(p.xz, wantedXZ);
                        if (dxz > searchRadius)
                        {
                            continue;
                        }
                        int matched = 0;
                        float sum = 0f;
                        if (refs.Count > 0)
                        {
                            foreach (TmpeSegmentRefDto r in refs)
                            {
                                MatchEdge(candidateEdges, r, float.MaxValue, out float d);
                                sum += math.min(d, 1000f);
                                if (d <= options.edgeTolerance)
                                {
                                    matched++;
                                }
                            }
                        }
                        float score = (refs.Count > 0 ? sum / refs.Count : 0f) + 0.1f * dxz;
                        if (matched > bestMatched || (matched == bestMatched && score < bestScore))
                        {
                            bestMatched = matched;
                            bestScore = score;
                            node = entity;
                            position = p;
                            best = dxz;
                        }
                    }
                    if (node != Entity.Null && refs.Count > 0)
                    {
                        matchNote = $"{bestMatched}/{refs.Count} CS1 segments matched by edges";
                        // Farther than nodeRadius: only when its edges carry every segment, or all but one (at least 2) within 2 x nodeRadius.
                        if (best > options.nodeRadius && bestMatched < refs.Count
                            && !(bestMatched >= 2 && bestMatched >= refs.Count - 1 && best <= 2f * options.nodeRadius))
                        {
                            best = float.MaxValue;
                        }
                    }
                }

                string nodeSkip = null;
                if (node == Entity.Null || best > math.max(options.nodeRadius, options.nodeSearchRadius))
                {
                    nodeSkip = $"no CS2 net node within {options.nodeSearchRadius} m (x/z) of ({nodeDto.x:F1}, {nodeDto.z:F1}) whose edges carry this node's CS1 segments" +
                        (matchNote != null ? $" (best: {matchNote})" : "");
                }
                else if (em.HasComponent<ModifiedConnections>(node) && !options.replace)
                {
                    nodeSkip = $"CS2 node {Id(node)} already has Traffic lane connections; reset it in the Lane Connector or run with replace (--replace)";
                }
                string skipStatus = "skipped";
                if (nodeSkip != null && node == Entity.Null || (nodeSkip != null && best == float.MaxValue))
                {
                    if (IsStraightIdentity(nodeDto))
                    {
                        skipStatus = "notNeeded";
                        nodeSkip = "no CS2 node here: the game joined the two collinear CS1 segments into one, and every TM:PE connection at this node is lane i -> lane i straight through (the game's default), so there is nothing to write";
                    }
                }
                nodeResults.Add(new
                {
                    id = nodeDto.id,
                    cs1Node = nodeDto.cs1Node,
                    cs2Node = nodeSkip == null || best <= options.nodeRadius ? Id(node) : null,
                    distance = Round(best),
                    match = matchNote,
                    status = nodeSkip == null ? "matched" : skipStatus,
                    reason = nodeSkip,
                });
                if (nodeSkip != null)
                {
                    foreach (TmpeConnectionDto c in nodeDto.connections)
                    {
                        connectionResults.Add(Result(c.id, nodeDto, skipStatus, nodeSkip));
                    }
                    foreach (TmpeArrowDto a in nodeDto.arrows)
                    {
                        Dictionary<string, object> r = Result(a.id, nodeDto, skipStatus, nodeSkip);
                        r["arrows"] = a.arrows;
                        arrowResults.Add(r);
                    }
                    continue;
                }

                List<EdgeAtNode> edges = CollectEdges(em, node);
                if (edges.Count == 2 && IsStraightIdentity(nodeDto))
                {
                    const string same = "a straight two-edge node whose TM:PE connections are all lane i -> lane i (the game's default); nothing to write";
                    foreach (TmpeConnectionDto c in nodeDto.connections)
                    {
                        connectionResults.Add(Result(c.id, nodeDto, "notNeeded", same));
                    }
                    foreach (TmpeArrowDto a in nodeDto.arrows)
                    {
                        Dictionary<string, object> r = Result(a.id, nodeDto, "notNeeded", same);
                        r["arrows"] = a.arrows;
                        arrowResults.Add(r);
                    }
                    continue;
                }
                var plan = new NodePlan { Dto = nodeDto, Node = node, Position = EffectivePosition(edges, position) };
                PlanConnections(em, plan, edges, options, connectionResults);
                PlanArrows(em, plan, edges, options, arrowResults);
                plans.Add(plan);
                nodeChecks.Add(SelfCheck(em, nodeDto, plan, edges, options));
            }

            // Traffic's own helper fills lanePositionMap / carriagewayAndGroupIndexMap from the compositions.
            ComponentLookup<Composition> compositions = system.GetComponentLookup<Composition>(true);
            BufferLookup<NetCompositionLane> compositionLanes = system.GetBufferLookup<NetCompositionLane>(true);
            foreach (NodePlan plan in plans)
            {
                foreach (SourceGroup group in plan.Groups.Values)
                {
                    var generated = new List<GeneratedConnection>();
                    string failure = null;
                    foreach ((TargetKey key, EdgeAtNode targetEdge, bool unsafeLane) in group.Targets)
                    {
                        if (!Traffic.Systems.Helpers.NetUtils.GetAdditionalLaneDetails(group.Edge.Edge, key.Edge, new int2(group.Lane, key.Lane),
                                new bool2(group.Edge.IsEnd, targetEdge.IsEnd), ref compositions, ref compositionLanes,
                                out float3x2 lanePositionMap, out int4 carriagewayAndGroup))
                        {
                            failure = $"composition lane lookup failed for {Id(group.Edge.Edge)}:{group.Lane} -> {Id(key.Edge)}:{key.Lane}";
                            break;
                        }
                        generated.Add(new GeneratedConnection
                        {
                            sourceEntity = group.Edge.Edge,
                            targetEntity = key.Edge,
                            laneIndexMap = new int2(group.Lane, key.Lane),
                            lanePositionMap = lanePositionMap,
                            carriagewayAndGroupIndexMap = carriagewayAndGroup,
                            method = PathMethod.Road,
                            isUnsafe = unsafeLane,
                        });
                    }
                    foreach (Dictionary<string, object> r in group.Results)
                    {
                        if (failure != null)
                        {
                            r["status"] = "skipped";
                            r["reason"] = failure;
                        }
                        else
                        {
                            r["status"] = options.dryRun ? "mapped" : "created";
                        }
                    }
                    if (failure == null)
                    {
                        // 0.9.26: the checks of Traffic's TrafficDataMigrationSystem.ValidateLoadedDataJob,
                        // run here so data Traffic would drop on the next load (or on the first write after
                        // a load) is never written; the failing rule is reported per connection.
                        failure = ValidateLikeTraffic(em, plan.Node, group.Edge.Edge, group.Lane, generated);
                        if (failure != null)
                        {
                            foreach (Dictionary<string, object> r in group.Results)
                            {
                                r["status"] = "skipped";
                                r["reason"] = "Traffic validation: " + failure;
                            }
                        }
                    }
                    if (failure == null)
                    {
                        plan.Write.Add((group, generated.ToArray()));
                    }
                }
            }

            int written = 0, dataEntities = 0, generatedCount = 0;
            if (!options.dryRun)
            {
                foreach (NodePlan plan in plans)
                {
                    if (plan.Write.Count == 0)
                    {
                        continue;
                    }
                    if (em.HasBuffer<ModifiedLaneConnections>(plan.Node))
                    {
                        RemoveTrafficData(em, plan.Node); // replace: TLE-style data is rebuilt from scratch
                    }
                    var entries = new List<ModifiedLaneConnections>();
                    foreach ((SourceGroup group, GeneratedConnection[] connections) in plan.Write)
                    {
                        Entity data = em.CreateEntity();
                        em.AddComponentData(data, new DataOwner(plan.Node));
                        em.AddComponentData(data, new PrefabRef(fakePrefab));
                        DynamicBuffer<GeneratedConnection> buffer = em.AddBuffer<GeneratedConnection>(data);
                        foreach (GeneratedConnection c in connections)
                        {
                            buffer.Add(c);
                        }
                        entries.Add(new ModifiedLaneConnections
                        {
                            edgeEntity = group.Edge.Edge,
                            laneIndex = group.Lane,
                            lanePosition = connections[0].lanePositionMap.c0,
                            carriagewayAndGroup = connections[0].carriagewayAndGroupIndexMap.xy,
                            modifiedConnections = data,
                        });
                        dataEntities++;
                        generatedCount += connections.Length;
                    }
                    if (!em.HasComponent<ModifiedConnections>(plan.Node))
                    {
                        em.AddComponent<ModifiedConnections>(plan.Node);
                    }
                    DynamicBuffer<ModifiedLaneConnections> nodeBuffer = em.HasBuffer<ModifiedLaneConnections>(plan.Node)
                        ? em.GetBuffer<ModifiedLaneConnections>(plan.Node)
                        : em.AddBuffer<ModifiedLaneConnections>(plan.Node);
                    nodeBuffer.Clear();
                    foreach (ModifiedLaneConnections entry in entries)
                    {
                        nodeBuffer.Add(entry);
                    }
                    MarkUpdated(em, plan.Node);
                    if (!undoLog.Nodes.Contains(plan.Node))
                    {
                        undoLog.Nodes.Add(plan.Node);
                    }
                    written++;
                }
                undoLog.ImportId = importId;
            }
            else
            {
                foreach (NodePlan plan in plans)
                {
                    dataEntities += plan.Write.Count;
                    generatedCount += plan.Write.Sum(w => w.connections.Length);
                    if (plan.Write.Count > 0)
                    {
                        written++;
                    }
                }
            }

            object Count(List<Dictionary<string, object>> list) => list
                .GroupBy(r => (string)r["status"])
                .ToDictionary(g => g.Key, g => g.Count());

            var report = new
            {
                importId = options.dryRun ? null : importId,
                dryRun = options.dryRun,
                trafficVersion,
                pinnedTrafficVersion = RequestHandlers.PinnedTrafficVersion,
                summary = new
                {
                    nodes = new { requested = body.nodes.Count, matched = plans.Count, skipped = body.nodes.Count - plans.Count },
                    connections = new { requested = connectionResults.Count, byStatus = Count(connectionResults) },
                    arrows = new { requested = arrowResults.Count, byStatus = Count(arrowResults) },
                    traffic = new
                    {
                        nodes = written,
                        modifiedSourceLanes = dataEntities,
                        generatedConnections = generatedCount,
                        note = options.dryRun ? "dry run: nothing written" : "written; Traffic regenerates the node lanes this frame. Save the map to keep them.",
                    },
                },
                nodes = nodeResults,
                selfCheck = nodeChecks,
                connections = connectionResults,
                arrows = arrowResults,
            };
            Mod.Log.Info($"TM:PE import {(options.dryRun ? "dry run" : importId)}: {written} nodes, {dataEntities} source lanes, {generatedCount} connections");
            return report;
        }

        private static void PlanConnections(EntityManager em, NodePlan plan, List<EdgeAtNode> edges, TmpeOptionsDto options,
            List<Dictionary<string, object>> results)
        {
            // Map every connection first; then drop CS1 source lanes with any unmapped target.
            var mapped = new List<(TmpeConnectionDto dto, Dictionary<string, object> result, MappedLane source, MappedLane target)>();
            var failedSources = new HashSet<long>();
            foreach (TmpeConnectionDto c in plan.Dto.connections)
            {
                Dictionary<string, object> r = Result(c.id, plan.Dto, null, null);
                results.Add(r);
                MappedLane source = MapLane(em, plan, edges, c.source, true, options, out string sourceError);
                MappedLane target = source == null ? null : MapLane(em, plan, edges, c.target, false, options, out sourceError);
                r["source"] = Describe(c.source, source);
                r["target"] = Describe(c.target, target);
                if (source == null || target == null)
                {
                    r["status"] = "skipped";
                    r["reason"] = sourceError;
                    failedSources.Add(c.source.cs1Lane);
                    continue;
                }
                mapped.Add((c, r, source, target));
            }
            foreach ((TmpeConnectionDto c, Dictionary<string, object> r, MappedLane source, MappedLane target) in mapped)
            {
                if (failedSources.Contains(c.source.cs1Lane))
                {
                    r["status"] = "skipped";
                    r["reason"] = "another connection from the same CS1 source lane could not be mapped; a partial list would drop it";
                    continue;
                }
                var sourceKey = new TargetKey { Edge = source.Edge.Edge, Lane = source.Lane.Index };
                if (!plan.Groups.TryGetValue(sourceKey, out SourceGroup group))
                {
                    group = new SourceGroup { Edge = source.Edge, Lane = source.Lane.Index };
                    plan.Groups.Add(sourceKey, group);
                }
                var targetKey = new TargetKey { Edge = target.Edge.Edge, Lane = target.Lane.Index };
                if (!group.Targets.Any(t => t.key.Equals(targetKey)))
                {
                    group.Targets.Add((targetKey, target.Edge, false));
                }
                group.Results.Add(r);
            }
        }

        private static void PlanArrows(EntityManager em, NodePlan plan, List<EdgeAtNode> edges, TmpeOptionsDto options,
            List<Dictionary<string, object>> results)
        {
            foreach (TmpeArrowDto a in plan.Dto.arrows)
            {
                Dictionary<string, object> r = Result(a.id, plan.Dto, null, null);
                r["arrows"] = a.arrows;
                results.Add(r);
                if (a.supersededByConnections)
                {
                    r["status"] = "superseded";
                    r["reason"] = "the lane has TM:PE lane connections, which TM:PE applies instead of its arrows";
                    continue;
                }
                MappedLane source = MapLane(em, plan, edges, a.source, true, options, out string error);
                r["source"] = Describe(a.source, source);
                if (source == null)
                {
                    r["status"] = "skipped";
                    r["reason"] = error;
                    continue;
                }
                var sourceKey = new TargetKey { Edge = source.Edge.Edge, Lane = source.Lane.Index };
                if (plan.Groups.ContainsKey(sourceKey))
                {
                    r["status"] = "superseded";
                    r["reason"] = "the CS2 lane already carries mapped lane connections";
                    continue;
                }

                var allowedEdges = new HashSet<Entity>();
                var targetInfo = new List<object>();
                foreach (TmpeArrowTargetDto t in a.targets)
                {
                    EdgeAtNode edge = MatchEdge(edges, t, options.edgeTolerance, out float distance);
                    targetInfo.Add(new { segment = t.segment, arrow = t.arrow, allowed = t.allowed, cs2Edge = edge != null ? Id(edge.Edge) : null, distance = Round(distance) });
                    if (edge != null && t.allowed)
                    {
                        allowedEdges.Add(edge.Edge);
                    }
                }
                r["targets"] = targetInfo;
                if (allowedEdges.Count == 0)
                {
                    r["status"] = "skipped";
                    r["reason"] = "no CS2 edge matches a CS1 segment in the arrow direction";
                    continue;
                }

                // The game's current connections from this lane, filtered to the arrow directions.
                var defaults = new List<(TargetKey key, EdgeAtNode edge, bool unsafeLane)>();
                DynamicBuffer<SubLane> nodeLanes = em.GetBuffer<SubLane>(plan.Node, true);
                for (int i = 0; i < nodeLanes.Length; i++)
                {
                    Entity lane = nodeLanes[i].m_SubLane;
                    if ((nodeLanes[i].m_PathMethods & PathMethod.Road) == 0 || em.HasComponent<MasterLane>(lane) || !em.HasComponent<CarLane>(lane))
                    {
                        continue;
                    }
                    Lane laneData = em.GetComponentData<Lane>(lane);
                    if (!laneData.m_StartNode.OwnerEquals(new PathNode(source.Edge.Edge, 0)) || (laneData.m_StartNode.GetLaneIndex() & 0xff) != source.Lane.Index)
                    {
                        continue;
                    }
                    CarLaneFlags flags = em.GetComponentData<CarLane>(lane).m_Flags;
                    if ((flags & CarLaneFlags.Forbidden) != 0)
                    {
                        continue;
                    }
                    EdgeAtNode targetEdge = edges.FirstOrDefault(e => laneData.m_EndNode.OwnerEquals(new PathNode(e.Edge, 0)));
                    if (targetEdge == null)
                    {
                        continue;
                    }
                    var key = new TargetKey { Edge = targetEdge.Edge, Lane = laneData.m_EndNode.GetLaneIndex() & 0xff };
                    if (!defaults.Any(d => d.key.Equals(key)))
                    {
                        defaults.Add((key, targetEdge, (flags & CarLaneFlags.Unsafe) != 0));
                    }
                }
                var kept = defaults.Where(d => allowedEdges.Contains(d.key.Edge)).ToList();
                r["cs2Default"] = defaults.Select(d => $"{Id(d.key.Edge)}:{d.key.Lane}").ToArray();
                r["cs2Kept"] = kept.Select(d => $"{Id(d.key.Edge)}:{d.key.Lane}").ToArray();
                if (kept.Count == 0)
                {
                    r["status"] = "skipped";
                    r["reason"] = "CS2 does not connect this lane to any edge in the arrow direction";
                    continue;
                }
                if (kept.Count == defaults.Count)
                {
                    r["status"] = "unchanged";
                    r["reason"] = "CS2's default connections already follow the arrows; nothing to write";
                    continue;
                }
                var group = new SourceGroup { Edge = source.Edge, Lane = source.Lane.Index };
                group.Targets.AddRange(kept);
                group.Results.Add(r);
                plan.Groups.Add(sourceKey, group);
            }
        }

        // ---------------------------------------------------------------- mapping

        private static MappedLane MapLane(EntityManager em, NodePlan plan, List<EdgeAtNode> edges, TmpeLaneEndDto end, bool incoming,
            TmpeOptionsDto options, out string error)
        {
            error = null;
            EdgeAtNode edge = MatchEdge(edges, end, options.edgeTolerance, out float distance);
            if (edge == null)
            {
                error = $"no edge at CS2 node {Id(plan.Node)} matches CS1 segment {end.segment} (nearest {Round(distance)} m off at {end.probeDist} m from the node)";
                return null;
            }
            List<Cs2Lane> lanes = CarLanes(em, edge, plan.Position, incoming);
            if (lanes.Count != end.laneCount)
            {
                error = $"lane-count mismatch on CS1 segment {end.segment}: CS1 has {end.laneCount} car lanes {(incoming ? "into" : "out of")} the node, CS2 edge {Id(edge.Edge)} has {lanes.Count}";
                return null;
            }
            Cs2Lane bestLane = null;
            float bestDelta = float.MaxValue;
            foreach (Cs2Lane lane in lanes)
            {
                float d = math.abs(lane.Offset - end.offset);
                if (d < bestDelta)
                {
                    bestDelta = d;
                    bestLane = lane;
                }
            }
            return new MappedLane
            {
                Edge = edge,
                Lane = bestLane,
                EdgeDistance = distance,
                OffsetDelta = bestDelta,
                RankAgrees = lanes.IndexOf(bestLane) == end.rank,
            };
        }

        /// <summary>
        /// 0.9.28 dry-run self-check: per matched node, which CS2 edge carries each CS1 segment (perpendicular
        /// probe distance), and the car lanes on both sides with their composition lane indices, so a lane-count
        /// or index problem is visible before anything is written.
        /// </summary>
        private static object SelfCheck(EntityManager em, TmpeNodeDto dto, NodePlan plan, List<EdgeAtNode> edges, TmpeOptionsDto options)
        {
            var segments = new List<object>();
            foreach (TmpeSegmentRefDto r in SegmentRefs(dto))
            {
                EdgeAtNode edge = MatchEdge(edges, r, options.edgeTolerance, out float d);
                List<Cs2Lane> into = edge != null ? CarLanes(em, edge, plan.Position, true) : null;
                List<Cs2Lane> outOf = edge != null ? CarLanes(em, edge, plan.Position, false) : null;
                segments.Add(new
                {
                    cs1Segment = r.segment,
                    cs1Prefab = r.prefab,
                    cs1LaneCount = (r as TmpeLaneEndDto)?.laneCount,
                    cs2Edge = edge != null ? Id(edge.Edge) : null,
                    viaStubTo = edge != null && edge.ExtendedThrough != Entity.Null ? Id(edge.ExtendedThrough) : null,
                    distance = Round(d),
                    cs2LanesIntoNode = into?.Select(l => l.Index).ToArray(),
                    cs2LanesOutOfNode = outOf?.Select(l => l.Index).ToArray(),
                });
            }
            return new
            {
                id = dto.id,
                cs2Node = Id(plan.Node),
                curveMeetingPoint = new[] { Round(plan.Position.x), Round(plan.Position.z) },
                cs1Position = new[] { Round(dto.x), Round(dto.z) },
                connectedEdges = edges.Select(e => Id(e.Edge)).ToArray(),
                segments,
            };
        }

        /// <summary>Every CS1 segment reference (with a probe) in a node's connections and arrows.</summary>
        private static List<TmpeSegmentRefDto> SegmentRefs(TmpeNodeDto node)
        {
            var refs = new List<TmpeSegmentRefDto>();
            var seen = new HashSet<int>();
            void Add(TmpeSegmentRefDto r)
            {
                if (r != null && r.probe != null && r.probe.Length >= 2 && seen.Add(r.segment))
                {
                    refs.Add(r);
                }
            }
            foreach (TmpeConnectionDto c in node.connections ?? new List<TmpeConnectionDto>())
            {
                Add(c.source);
                Add(c.target);
            }
            foreach (TmpeArrowDto a in node.arrows ?? new List<TmpeArrowDto>())
            {
                Add(a.source);
                foreach (TmpeArrowTargetDto t in a.targets ?? new List<TmpeArrowTargetDto>())
                {
                    Add(t);
                }
            }
            return refs;
        }

        /// <summary>
        /// Port of Traffic 0.2.13 TrafficDataMigrationSystem.ValidateLoadedDataJob
        /// (FillEdgeCompositions + ValidateLaneConnectionsSettings + TryFixGeneratedConnections):
        /// returns the first rule the data breaks, or null. Traffic drops the WHOLE node when one item
        /// fails, so a failing source lane must not be written.
        /// </summary>
        private static string ValidateLikeTraffic(EntityManager em, Entity node, Entity sourceEdge, int sourceLane, List<GeneratedConnection> generated)
        {
            DynamicBuffer<ConnectedEdge> connected = em.GetBuffer<ConnectedEdge>(node, true);
            var lanesByEdge = new Dictionary<Entity, (bool road, bool track, HashSet<int> lanes)>();
            for (int i = 0; i < connected.Length; i++)
            {
                Entity e = connected[i].m_Edge;
                if (!em.HasComponent<Composition>(e))
                {
                    return $"connected edge {Id(e)} has no Composition (Traffic rejects the node)";
                }
                Composition composition = em.GetComponentData<Composition>(e);
                if (i == 0)
                {
                    bool isEnd = em.GetComponentData<Edge>(e).m_End == node;
                    Entity nodeComposition = isEnd ? composition.m_EndNode : composition.m_StartNode;
                    if (em.HasComponent<NetCompositionData>(nodeComposition)
                        && (em.GetComponentData<NetCompositionData>(nodeComposition).m_Flags.m_General & CompositionFlags.General.Roundabout) != 0)
                    {
                        return "the node is a roundabout (Traffic keeps no lane connections there)";
                    }
                }
                Entity edgeComposition = composition.m_Edge;
                if (!em.HasBuffer<NetCompositionLane>(edgeComposition))
                {
                    continue;
                }
                bool road = em.HasComponent<RoadComposition>(edgeComposition);
                bool track = em.HasComponent<TrackComposition>(edgeComposition) && em.GetComponentData<TrackComposition>(edgeComposition).m_TrackType != TrackTypes.None;
                var lanes = new HashSet<int>();
                DynamicBuffer<NetCompositionLane> buffer = em.GetBuffer<NetCompositionLane>(edgeComposition, true);
                for (int j = 0; j < buffer.Length; j++)
                {
                    if ((buffer[j].m_Flags & (LaneFlags.Slave | LaneFlags.Road | LaneFlags.Track | LaneFlags.BicyclesOnly)) != 0)
                    {
                        lanes.Add(j);
                        track |= (buffer[j].m_Flags & LaneFlags.Track) != 0;
                    }
                }
                if (road || track)
                {
                    lanesByEdge[e] = (road, track, lanes);
                }
            }
            if (!lanesByEdge.TryGetValue(sourceEdge, out var source))
            {
                return $"source edge {Id(sourceEdge)} is not a road/track edge connected to node {Id(node)}";
            }
            if (!source.lanes.Contains(sourceLane))
            {
                return $"source lane index {sourceLane} is not a road/track lane of edge {Id(sourceEdge)}'s composition (valid: {string.Join(",", source.lanes)})";
            }
            foreach (GeneratedConnection c in generated)
            {
                if (c.sourceEntity != sourceEdge)
                {
                    return $"connection source {Id(c.sourceEntity)} differs from the item's edge {Id(sourceEdge)}";
                }
                if (!lanesByEdge.TryGetValue(c.targetEntity, out var target))
                {
                    return $"target edge {Id(c.targetEntity)} is not a road/track edge connected to node {Id(node)}";
                }
                if (!target.lanes.Contains(c.laneIndexMap.y))
                {
                    return $"target lane index {c.laneIndexMap.y} is not a road/track lane of edge {Id(c.targetEntity)}'s composition (valid: {string.Join(",", target.lanes)})";
                }
                if ((c.method & PathMethod.Road) != 0 && !target.road)
                {
                    return $"road connection onto edge {Id(c.targetEntity)}, whose composition has no RoadComposition";
                }
            }
            return null;
        }

        /// <summary>
        /// 0.9.27: the edge whose curve passes nearest the CS1 probe point (perpendicular distance to
        /// the whole curve, not the point at probeDist along it) and that leaves the node in the CS1
        /// segment's direction (dot >= 0.5). Robust to the game moving or removing degree-2 nodes.
        /// </summary>
        private static EdgeAtNode MatchEdge(List<EdgeAtNode> edges, TmpeSegmentRefDto segment, float tolerance, out float bestDistance)
        {
            bestDistance = float.MaxValue;
            EdgeAtNode best = null;
            if (segment.probe == null || segment.probe.Length < 2)
            {
                return null;
            }
            var probe = new float2(segment.probe[0], segment.probe[1]);
            float2 dir = segment.dir != null && segment.dir.Length >= 2 ? math.normalizesafe(new float2(segment.dir[0], segment.dir[1])) : float2.zero;
            foreach (EdgeAtNode edge in edges)
            {
                if (math.lengthsq(dir) > 0f)
                {
                    float2 outward = math.normalizesafe(PointAt(edge, math.min(10f, edge.Distance[edge.Distance.Length - 1])) - edge.Samples[0].xz);
                    if (math.dot(outward, dir) < 0.5f)
                    {
                        continue;
                    }
                }
                float d = math.distance(NearestSample(edge, probe), probe);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = edge;
                }
            }
            return bestDistance <= tolerance ? best : null;
        }

        /// <summary>Where the node's edge curves actually meet (mean of their node-side ends).</summary>
        private static float3 EffectivePosition(List<EdgeAtNode> edges, float3 fallback)
        {
            if (edges.Count == 0)
            {
                return fallback;
            }
            float3 sum = float3.zero;
            foreach (EdgeAtNode e in edges)
            {
                sum += e.Samples[0];
            }
            return sum / edges.Count;
        }

        /// <summary>
        /// A CS1 node joining exactly two opposite segments whose every connection maps lane rank i to
        /// rank i with equal lane counts, and no arrows: TM:PE data that only restates the default.
        /// </summary>
        private static bool IsStraightIdentity(TmpeNodeDto node)
        {
            if (node.connections == null || node.connections.Count == 0 || (node.arrows != null && node.arrows.Exists(a => !a.supersededByConnections)))
            {
                return false;
            }
            var segments = new HashSet<int>();
            foreach (TmpeConnectionDto c in node.connections)
            {
                if (c.source == null || c.target == null || c.source.rank != c.target.rank || c.source.laneCount != c.target.laneCount)
                {
                    return false;
                }
                segments.Add(c.source.segment);
                segments.Add(c.target.segment);
                if (c.source.dir != null && c.target.dir != null && c.source.dir.Length >= 2 && c.target.dir.Length >= 2
                    && c.source.dir[0] * c.target.dir[0] + c.source.dir[1] * c.target.dir[1] > -0.9f)
                {
                    return false;
                }
            }
            return segments.Count == 2;
        }

        private static float2 PointAt(EdgeAtNode edge, float distance)
        {
            for (int i = 1; i < edge.Samples.Length; i++)
            {
                if (edge.Distance[i] >= distance)
                {
                    float span = edge.Distance[i] - edge.Distance[i - 1];
                    float t = span > 0 ? (distance - edge.Distance[i - 1]) / span : 0;
                    return math.lerp(edge.Samples[i - 1].xz, edge.Samples[i].xz, t);
                }
            }
            return edge.Samples[edge.Samples.Length - 1].xz;
        }

        /// <summary>
        /// Car lanes of an edge that flow into (incoming) or out of the node, ordered left to right in the
        /// direction of travel, with lateral offsets (metres, right positive) from their group's centre.
        /// </summary>
        private static List<Cs2Lane> CarLanes(EntityManager em, EdgeAtNode edge, float3 nodePosition, bool incoming)
        {
            var lanes = new List<Cs2Lane>();
            if (!em.HasBuffer<SubLane>(edge.Edge))
            {
                return lanes;
            }
            DynamicBuffer<SubLane> subLanes = em.GetBuffer<SubLane>(edge.Edge, true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;
                if ((subLanes[i].m_PathMethods & PathMethod.Road) == 0 || em.HasComponent<MasterLane>(lane)
                    || !em.HasComponent<CarLane>(lane) || !em.HasComponent<Curve>(lane))
                {
                    continue;
                }
                Bezier4x3 b = em.GetComponentData<Curve>(lane).m_Bezier;
                // 0.9.28: the game splits long edge lanes into pieces (EdgeLane.m_EdgeDelta); only the piece
                // touching the node counts, otherwise a 2-lane edge cut in two looked like 4 lanes.
                Lane laneData = em.GetComponentData<Lane>(lane);
                PathNode nodeSide;
                if (em.HasComponent<EdgeLane>(lane))
                {
                    float2 delta = em.GetComponentData<EdgeLane>(lane).m_EdgeDelta;
                    float nodeDelta = edge.IsEnd ? 1f : 0f;
                    bool endsAtNode = math.abs(delta.y - nodeDelta) < 0.001f;
                    bool startsAtNode = math.abs(delta.x - nodeDelta) < 0.001f;
                    if (incoming ? !endsAtNode : !startsAtNode)
                    {
                        continue;
                    }
                }
                else
                {
                    bool endsAtNode = math.distance(b.d.xz, nodePosition.xz) < math.distance(b.a.xz, nodePosition.xz);
                    if (endsAtNode != incoming)
                    {
                        continue;
                    }
                }
                // The node-side path node carries the composition lane index (LaneSystem: PathNode(edge,
                // NetCompositionLane.m_Index, segment)); the far side of a split piece is a middle node with
                // a running counter, which is what 0.9.26 wrote for incoming lanes (Traffic rejected it).
                nodeSide = incoming ? laneData.m_EndNode : laneData.m_StartNode;
                if (!nodeSide.OwnerEquals(new PathNode(edge.Edge, 0)))
                {
                    // 0.9.30: on straight pass-through nodes the node-side path node can belong to the
                    // neighbouring edge; the far end of the piece still carries this edge's composition index.
                    PathNode farSide = incoming ? laneData.m_StartNode : laneData.m_EndNode;
                    if (!farSide.OwnerEquals(new PathNode(edge.Edge, 0)))
                    {
                        continue;
                    }
                    nodeSide = farSide;
                }
                float3 end = incoming ? b.d : b.a;
                float2 heading = math.normalizesafe(incoming ? (b.d - b.c).xz : (b.b - b.a).xz);
                if (math.lengthsq(heading) < 1e-6f)
                {
                    heading = math.normalizesafe(incoming ? (b.d - b.a).xz : (b.d - b.a).xz);
                }
                float2 right = new float2(heading.y, -heading.x);
                float2 centre = NearestSample(edge, end.xz);
                lanes.Add(new Cs2Lane
                {
                    SubLane = lane,
                    Index = nodeSide.GetLaneIndex() & 0xff,
                    Lateral = math.dot(end.xz - centre, right),
                });
            }
            float mean = lanes.Count > 0 ? lanes.Average(l => l.Lateral) : 0f;
            foreach (Cs2Lane l in lanes)
            {
                l.Offset = l.Lateral - mean;
            }
            lanes.Sort((p, q) => p.Lateral.CompareTo(q.Lateral));
            return lanes;
        }

        private static float2 NearestSample(EdgeAtNode edge, float2 point)
        {
            float2 best = edge.Samples[0].xz;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < edge.Samples.Length - 1; i++)
            {
                float2 a = edge.Samples[i].xz, b = edge.Samples[i + 1].xz;
                float2 ab = b - a;
                float t = math.lengthsq(ab) > 0 ? math.saturate(math.dot(point - a, ab) / math.lengthsq(ab)) : 0;
                float2 p = a + ab * t;
                float d = math.distancesq(p, point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = p;
                }
            }
            return best;
        }

        private static List<(Entity, float3)> CollectNodes(EntityManager em)
        {
            var result = new List<(Entity, float3)>();
            EntityQuery query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    result.Add((e, em.GetComponentData<Node>(e).m_Position));
                }
            }
            return result;
        }

        /// <summary>
        /// 0.9.30: an edge shorter than 15 m (A-v7: 4-8 m stubs of the neighbouring prefab where the import
        /// put the prefab change a few metres off the CS1 node) gets the shape of the edge that continues
        /// it straight on at its far node (degree 2, |angle| &lt; 30 deg) appended, so probes 20 m out still
        /// find the stub as the edge in that direction. Lanes and Traffic data stay on the stub itself.
        /// </summary>
        private static void ExtendStub(EntityManager em, EdgeAtNode edge, Entity farNode)
        {
            float length = edge.Distance[edge.Distance.Length - 1];
            if (length >= 15f || !em.HasBuffer<ConnectedEdge>(farNode))
            {
                return;
            }
            DynamicBuffer<ConnectedEdge> connected = em.GetBuffer<ConnectedEdge>(farNode, true);
            if (connected.Length != 2)
            {
                return;
            }
            float2 heading = math.normalizesafe(edge.Samples[edge.Samples.Length - 1].xz - edge.Samples[0].xz);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity next = connected[i].m_Edge;
                if (next == edge.Edge || !em.HasComponent<Curve>(next) || !em.HasComponent<Edge>(next))
                {
                    continue;
                }
                Edge nextData = em.GetComponentData<Edge>(next);
                bool fromStart = nextData.m_Start == farNode;
                if (!fromStart && nextData.m_End != farNode)
                {
                    continue;
                }
                Bezier4x3 b = em.GetComponentData<Curve>(next).m_Bezier;
                const int n = 64;
                var extra = new float3[n];
                for (int s = 1; s <= n; s++)
                {
                    float t = (float)s / n;
                    extra[s - 1] = MathUtils.Position(b, fromStart ? t : 1f - t);
                }
                float2 nextHeading = math.normalizesafe(extra[math.min(8, n - 1)].xz - edge.Samples[edge.Samples.Length - 1].xz);
                if (math.dot(heading, nextHeading) < 0.866f)
                {
                    return;
                }
                var samples = new List<float3>(edge.Samples);
                var distance = new List<float>(edge.Distance);
                foreach (float3 p in extra)
                {
                    distance.Add(distance[distance.Count - 1] + math.distance(p.xz, samples[samples.Count - 1].xz));
                    samples.Add(p);
                }
                edge.Samples = samples.ToArray();
                edge.Distance = distance.ToArray();
                edge.ExtendedThrough = next;
                return;
            }
        }

        private static List<EdgeAtNode> CollectEdges(EntityManager em, Entity node)
        {
            var edges = new List<EdgeAtNode>();
            DynamicBuffer<ConnectedEdge> connected = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity e = connected[i].m_Edge;
                if (em.HasComponent<Deleted>(e) || !em.HasComponent<Edge>(e) || !em.HasComponent<Curve>(e))
                {
                    continue;
                }
                Edge edgeData = em.GetComponentData<Edge>(e);
                if (edgeData.m_Start != node && edgeData.m_End != node)
                {
                    continue;
                }
                bool isEnd = edgeData.m_End == node;
                Bezier4x3 b = em.GetComponentData<Curve>(e).m_Bezier;
                const int n = 128;
                var samples = new float3[n + 1];
                var distance = new float[n + 1];
                for (int s = 0; s <= n; s++)
                {
                    float t = (float)s / n;
                    samples[s] = MathUtils.Position(b, isEnd ? 1f - t : t);
                    distance[s] = s == 0 ? 0 : distance[s - 1] + math.distance(samples[s].xz, samples[s - 1].xz);
                }
                var edgeAtNode = new EdgeAtNode { Edge = e, IsEnd = isEnd, Samples = samples, Distance = distance };
                ExtendStub(em, edgeAtNode, isEnd ? edgeData.m_Start : edgeData.m_End);
                edges.Add(edgeAtNode);
            }
            return edges;
        }

        // ---------------------------------------------------------------- undo

        public static object Undo(EntityManager em, TmpeUndoLog undoLog, string trafficVersion)
        {
            em.CompleteAllTrackedJobs();
            int nodes = 0, data = 0;
            foreach (Entity node in undoLog.Nodes)
            {
                if (em.Exists(node) && em.HasBuffer<ModifiedLaneConnections>(node))
                {
                    data += RemoveTrafficData(em, node);
                    MarkUpdated(em, node);
                    nodes++;
                }
            }
            string importId = undoLog.ImportId;
            undoLog.Nodes.Clear();
            undoLog.ImportId = null;
            return new { undone = importId, trafficVersion, nodes, modifiedSourceLanes = data };
        }

        /// <summary>As Traffic's RemoveLaneConnectionsJob: delete the data entities, drop the node's components.</summary>
        private static int RemoveTrafficData(EntityManager em, Entity node)
        {
            var dataEntities = new List<Entity>();
            DynamicBuffer<ModifiedLaneConnections> buffer = em.GetBuffer<ModifiedLaneConnections>(node, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                dataEntities.Add(buffer[i].modifiedConnections);
            }
            foreach (Entity d in dataEntities)
            {
                if (d != Entity.Null && em.Exists(d) && !em.HasComponent<Deleted>(d))
                {
                    em.AddComponent<Deleted>(d);
                }
            }
            em.RemoveComponent<ModifiedLaneConnections>(node);
            if (em.HasComponent<ModifiedConnections>(node))
            {
                em.RemoveComponent<ModifiedConnections>(node);
            }
            return dataEntities.Count;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Updated on the node, its edges and their other nodes, as TLEDataMigrationSystem does.</summary>
        private static void MarkUpdated(EntityManager em, Entity node)
        {
            var targets = new List<Entity> { node };
            DynamicBuffer<ConnectedEdge> connected = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity e = connected[i].m_Edge;
                if (em.HasComponent<Deleted>(e) || !em.HasComponent<Edge>(e))
                {
                    continue;
                }
                Edge edge = em.GetComponentData<Edge>(e);
                targets.Add(e);
                targets.Add(edge.m_Start == node ? edge.m_End : edge.m_Start);
            }
            foreach (Entity t in targets)
            {
                if (em.Exists(t) && !em.HasComponent<Updated>(t))
                {
                    em.AddComponent<Updated>(t);
                }
            }
        }

        private static Entity GetFakePrefab()
        {
            FieldInfo field = typeof(Traffic.Systems.ModDefaultsSystem).GetField("FakePrefabRef", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return field != null ? (Entity)field.GetValue(null) : Entity.Null;
        }

        private static Dictionary<string, object> Result(string id, TmpeNodeDto node, string status, string reason)
        {
            var r = new Dictionary<string, object> { ["id"] = id, ["node"] = node.id };
            if (status != null)
            {
                r["status"] = status;
            }
            if (reason != null)
            {
                r["reason"] = reason;
            }
            return r;
        }

        private static object Describe(TmpeLaneEndDto end, MappedLane mapped)
        {
            return new
            {
                segment = end.segment,
                cs1LaneIndex = end.cs1LaneIndex,
                cs2Edge = mapped != null ? Id(mapped.Edge.Edge) : null,
                cs2Lane = mapped?.Lane.Index,
                edgeDistance = mapped != null ? Round(mapped.EdgeDistance) : (float?)null,
                offsetDelta = mapped != null ? Round(mapped.OffsetDelta) : (float?)null,
                rankAgrees = mapped?.RankAgrees,
            };
        }

        private static string Id(Entity e) => $"{e.Index}:{e.Version}";

        private static float Round(float v) => float.IsInfinity(v) || v == float.MaxValue ? -1f : (float)Math.Round(v, 2);
    }
}
