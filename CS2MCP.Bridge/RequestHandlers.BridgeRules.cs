using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Bridge-prefab checks for /import/network (0.9.22), from the decompiled game:
    /// NetInitializeSystem turns Game.Prefabs.Bridge into NetGeometryData/PlaceableNetData/
    /// FixedNetElement, CourseSplitSystem.TryAddCoursePhase2 splits a course (one course
    /// per import edge), ValidationHelpers raises ShortDistance / LowElevation.
    /// Rule names: bridge.length.short, bridge.straight, bridge.lowElevation,
    /// bridge.fixed.short, bridge.fixed.repeat, bridge.fixed.chained, bridge.waterClearance.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private List<object> CheckBridgeEdges(BridgeImportToolSystem.ImportJob job, string mode, List<string> problems, ref TerrainHeightData heightData)
        {
            var report = new List<object>();
            if (mode == "off")
            {
                return report;
            }
            WaterSurfaceData<SurfaceWater> water = default;
            bool haveWater = false;
            try
            {
                water = World.GetOrCreateSystemManaged<WaterSystem>().GetSurfaceData(out JobHandle deps);
                deps.Complete();
                haveWater = true;
            }
            catch (Exception)
            {
            }

            // Nodes used by edges of fixed-span prefabs, to find chains.
            var fixedNodeUse = new Dictionary<int, int>();
            foreach (BridgeImportToolSystem.ImportEdge edge in job.Edges)
            {
                if (EntityManager.HasBuffer<FixedNetElement>(edge.Prefab))
                {
                    fixedNodeUse[edge.A] = fixedNodeUse.TryGetValue(edge.A, out int ca) ? ca + 1 : 1;
                    fixedNodeUse[edge.B] = fixedNodeUse.TryGetValue(edge.B, out int cb) ? cb + 1 : 1;
                }
            }

            report.AddRange(CheckCourseOwnerEdges(job, mode, problems, ref heightData));
            foreach (BridgeImportToolSystem.ImportEdge edge in job.Edges)
            {
                if (!EntityManager.HasComponent<BridgeData>(edge.Prefab) || !EntityManager.HasComponent<NetGeometryData>(edge.Prefab))
                {
                    continue;
                }
                NetGeometryData geometry = EntityManager.GetComponentData<NetGeometryData>(edge.Prefab);
                float minWater = EntityManager.HasComponent<PlaceableNetData>(edge.Prefab)
                    ? EntityManager.GetComponentData<PlaceableNetData>(edge.Prefab).m_MinWaterElevation
                    : 0f;
                var errors = new List<string>();
                var warnings = new List<string>();

                // Polyline a → control → b in x/z.
                var points = new List<float2> { job.Nodes[edge.A].XZ };
                if (edge.Control != null)
                {
                    points.AddRange(edge.Control);
                }
                points.Add(job.Nodes[edge.B].XZ);
                float length = 0f;
                for (int i = 1; i < points.Count; i++)
                {
                    length += math.distance(points[i - 1], points[i]);
                }
                float2 chordA = points[0];
                float2 chordB = points[points.Count - 1];
                float bend = 0f;
                for (int i = 1; i < points.Count - 1; i++)
                {
                    bend = math.max(bend, DistanceToLine(points[i], chordA, chordB));
                }
                bool straightOnly = (geometry.m_Flags & GeometryFlags.StraightEdges) != 0;
                if (straightOnly && bend > 1f)
                {
                    errors.Add($"bridge.straight: the prefab allows straight edges only (Bridge.m_CanCurve false); control points bend {bend:F1} m off the chord");
                }

                float yA = NodeDeckY(job.Nodes[edge.A], ref heightData);
                float yB = NodeDeckY(job.Nodes[edge.B], ref heightData);
                float elevA = yA - TerrainUtils.SampleHeight(ref heightData, new float3(chordA.x, 0f, chordA.y));
                float elevB = yB - TerrainUtils.SampleHeight(ref heightData, new float3(chordB.x, 0f, chordB.y));
                float2 mid = math.lerp(chordA, chordB, 0.5f);
                float elevMid = (yA + yB) * 0.5f - TerrainUtils.SampleHeight(ref heightData, new float3(mid.x, 0f, mid.y));
                float needed = geometry.m_ElevationLimit * 2f;
                if (math.max(math.max(elevA, elevB), elevMid) < needed)
                {
                    errors.Add($"bridge.lowElevation: bridges must be elevated (RequireElevated); the highest of ends/middle is {math.max(math.max(elevA, elevB), elevMid):F1} m above the terrain, needs >= {needed:F1} m");
                }

                object split;
                if (EntityManager.HasBuffer<FixedNetElement>(edge.Prefab))
                {
                    DynamicBuffer<FixedNetElement> elements = EntityManager.GetBuffer<FixedNetElement>(edge.Prefab, true);
                    split = FixedSplit(elements, length, errors, warnings, bend);
                    if ((fixedNodeUse.TryGetValue(edge.A, out int ua) && ua > 1) || (fixedNodeUse.TryGetValue(edge.B, out int ub) && ub > 1))
                    {
                        errors.Add("bridge.fixed.chained: a fixed-span bridge must be ONE import edge from abutment to abutment; the game cuts it into its spans itself. This edge shares a node with another edge of a fixed-span prefab");
                    }
                }
                else
                {
                    Bounds1 range = geometry.m_EdgeLengthRange;
                    float max = geometry.m_ElevatedLength > 0f ? geometry.m_ElevatedLength : range.max;
                    int pieces = max > 0f ? math.max(1, (int)math.ceil((length - 0.16f) / max)) : 1;
                    float piece = length / pieces;
                    if (piece < range.min)
                    {
                        errors.Add($"bridge.length.short: {length:F1} m is below the minimum edge length {range.min:F1} m (0.6 x Bridge.m_SegmentLength); the game reports ShortDistance");
                    }
                    split = new { pieces, pieceLength = piece, minEdge = range.min, maxEdge = max };
                }

                if (haveWater && minWater > 0f)
                {
                    float worst = float.MaxValue;
                    float2 worstAt = default;
                    for (int s = 0; s <= 8; s++)
                    {
                        float t = s / 8f;
                        float2 p = math.lerp(chordA, chordB, t);
                        float3 pos = new float3(p.x, 0f, p.y);
                        float surface = WaterUtils.SampleHeight(ref water, ref heightData, pos, out float depth);
                        if (depth < 0.2f)
                        {
                            continue;
                        }
                        float clearance = math.lerp(yA, yB, t) - surface;
                        if (clearance < worst)
                        {
                            worst = clearance;
                            worstAt = p;
                        }
                    }
                    if (worst < minWater)
                    {
                        warnings.Add($"bridge.waterClearance: deck {worst:F1} m above the water at ({worstAt.x:F0}, {worstAt.y:F0}), the prefab wants >= {minWater:F1} m (Bridge.m_ElevationOnWater; the net tool raises the deck to it, validation does not check it)");
                    }
                }

                foreach (string e in errors)
                {
                    string message = $"edge '{edge.Id}' ({edge.PrefabName}): {e}";
                    if (mode == "error")
                    {
                        problems.Add(message);
                    }
                    else
                    {
                        job.Warnings.Add(message);
                    }
                }
                foreach (string w in warnings)
                {
                    job.Warnings.Add($"edge '{edge.Id}' ({edge.PrefabName}): {w}");
                }
                report.Add(new
                {
                    edge = edge.Id,
                    prefab = edge.PrefabName,
                    length,
                    bend,
                    straightOnly,
                    deckY = new[] { yA, yB },
                    elevation = new[] { elevA, elevMid, elevB },
                    minWaterElevation = minWater,
                    split,
                    errors,
                    warnings,
                });
            }
            return report;
        }

        /// <summary>
        /// The game's split of one course into the fixed spans (CourseSplitSystem.TryAddCoursePhase2):
        /// the course is repeated ceil((L - 0.16) / sum(max)) times; within one repeat each
        /// element gets min + its share of max(0, L - sum(min)) by its (max - min) range, or
        /// is shrunk in proportion when L &lt; sum(min). The last element ends at the course end.
        /// </summary>
        private static object FixedSplit(DynamicBuffer<FixedNetElement> elements, float length, List<string> errors, List<string> warnings, float bend)
        {
            float2 total = 0f;
            float variable = 0f;
            var spans = new List<object>();
            for (int i = 0; i < elements.Length; i++)
            {
                FixedNetElement e = elements[i];
                int2 count = math.select(e.m_CountRange, new int2(0, 10000), e.m_CountRange == 0);
                float2 range = new float2(e.m_LengthRange.min, e.m_LengthRange.max) * count;
                total += range;
                variable += e.m_LengthRange.max != e.m_LengthRange.min ? range.y - range.x : 0f;
            }
            int repeats = math.max(1, (int)math.ceil((length - 0.16f) / math.max(1f, total.y)));
            if (repeats > 1)
            {
                errors.Add($"bridge.fixed.repeat: {length:F1} m is longer than the whole span sequence allows ({total.y:F1} m), so the game would build the sequence {repeats} times");
            }
            float one = length / repeats;
            if (one < total.x)
            {
                warnings.Add($"bridge.fixed.short: {one:F1} m is below the sequence minimum {total.x:F1} m; the game shrinks every span in proportion and validation may report ShortDistance");
            }
            float extra = math.max(0f, one - total.x);
            float shrink = math.min(0f, one - total.x);
            float along = 0f;
            for (int i = 0; i < elements.Length; i++)
            {
                FixedNetElement e = elements[i];
                int2 count = math.select(e.m_CountRange, new int2(0, 10000), e.m_CountRange == 0);
                float2 range = new float2(e.m_LengthRange.min, e.m_LengthRange.max) * count;
                float span = range.x + (e.m_LengthRange.max != e.m_LengthRange.min ? range.y - range.x : 0f) * extra / math.max(1f, variable);
                span += range.x * shrink / math.max(1f, total.x);
                if (i == elements.Length - 1)
                {
                    span = one - along;
                }
                int pieces = math.clamp((int)math.ceil((span - 0.16f) / math.max(0.01f, e.m_LengthRange.max)), count.x, count.y);
                bool straight = (e.m_Flags & FixedNetFlags.Straight) != 0;
                if (straight && bend > 1f)
                {
                    warnings.Add($"bridge.straight: fixed span {i} is straight-only; the game straightens it along the end tangent");
                }
                spans.Add(new
                {
                    fixedIndex = i,
                    start = along,
                    length = span,
                    lengthRange = new[] { e.m_LengthRange.min, e.m_LengthRange.max },
                    countRange = new[] { e.m_CountRange.x, e.m_CountRange.y },
                    segments = pieces,
                    straight,
                });
                along += span;
            }
            return new { repeats, sequenceMin = total.x, sequenceMax = total.y, spans };
        }

        /// <summary>
        /// Dams and other course-owner nets (GeometryFlags.SubOwner): rules dam.single,
        /// dam.level, dam.elevation (warning), dam.flow (warning). Reports the spawned
        /// course objects and the flow orientation the import will use.
        /// </summary>
        private List<object> CheckCourseOwnerEdges(BridgeImportToolSystem.ImportJob job, string mode, List<string> problems, ref TerrainHeightData heightData)
        {
            var report = new List<object>();
            var use = new Dictionary<int, int>();
            var owners = new List<BridgeImportToolSystem.ImportEdge>();
            foreach (BridgeImportToolSystem.ImportEdge edge in job.Edges)
            {
                if (EntityManager.HasComponent<NetGeometryData>(edge.Prefab)
                    && (EntityManager.GetComponentData<NetGeometryData>(edge.Prefab).m_Flags & GeometryFlags.SubOwner) != 0)
                {
                    owners.Add(edge);
                    use[edge.A] = use.TryGetValue(edge.A, out int ca) ? ca + 1 : 1;
                    use[edge.B] = use.TryGetValue(edge.B, out int cb) ? cb + 1 : 1;
                }
            }
            if (owners.Count == 0 || mode == "off")
            {
                return report;
            }
            BridgeImportToolSystem tool = World.GetOrCreateSystemManaged<BridgeImportToolSystem>();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            foreach (BridgeImportToolSystem.ImportEdge edge in owners)
            {
                var errors = new List<string>();
                var warnings = new List<string>();
                if (use[edge.A] > 1 || use[edge.B] > 1)
                {
                    errors.Add("dam.single: each edge of this prefab spawns its own plant (MakeOwner course object); send the whole dam as ONE edge abutment to abutment, sharing no node with another edge of the prefab");
                }
                float yA = NodeDeckY(job.Nodes[edge.A], ref heightData);
                float yB = NodeDeckY(job.Nodes[edge.B], ref heightData);
                NetGeometryData geometry = EntityManager.GetComponentData<NetGeometryData>(edge.Prefab);
                if (geometry.m_MaxSlopeSteepness == 0f && math.abs(yA - yB) > 0.5f)
                {
                    errors.Add($"dam.level: the prefab has max slope 0 (the net tool levels the crest to the start height); the ends are at {yA:F1} and {yB:F1} m. Give both nodes the same absolute y (the crest)");
                }
                float2 elevationRange = default;
                Game.Net.PlacementFlags placement = default;
                if (EntityManager.HasComponent<PlaceableNetData>(edge.Prefab))
                {
                    PlaceableNetData placeable = EntityManager.GetComponentData<PlaceableNetData>(edge.Prefab);
                    elevationRange = new float2(placeable.m_ElevationRange.min, placeable.m_ElevationRange.max);
                    placement = placeable.m_PlacementFlags;
                }
                float2 a = job.Nodes[edge.A].XZ;
                float2 b = job.Nodes[edge.B].XZ;
                float eA = yA - TerrainUtils.SampleHeight(ref heightData, new float3(a.x, 0f, a.y));
                float eB = yB - TerrainUtils.SampleHeight(ref heightData, new float3(b.x, 0f, b.y));
                if (eA < elevationRange.x - 1f || eA > elevationRange.y + 1f || eB < elevationRange.x - 1f || eB > elevationRange.y + 1f)
                {
                    warnings.Add($"dam.elevation: the abutments should sit on the terrain (prefab elevation range {elevationRange.x:F0}..{elevationRange.y:F0} m); they are {eA:F1} and {eB:F1} m above it. Put the end nodes where the valley sides reach the crest height");
                }
                float weight = 0f;
                if ((placement & (Game.Net.PlacementFlags.FlowLeft | Game.Net.PlacementFlags.FlowRight)) != 0)
                {
                    Bezier4x3 curve = NetUtils.StraightCurve(new float3(a.x, yA, a.y), new float3(b.x, yB, b.y));
                    weight = tool.FlowWeight(curve, placement);
                    if (math.abs(weight) < 0.01f)
                    {
                        warnings.Add("dam.flow: no water flows across the line, so the side cannot be chosen from the flow (the edge is built a -> b unless flow is 'invert'); the plant only produces power with water flowing through the dam");
                    }
                }
                var spawn = new List<string>();
                if (EntityManager.HasBuffer<Game.Prefabs.SubObject>(edge.Prefab))
                {
                    foreach (Game.Prefabs.SubObject sub in EntityManager.GetBuffer<Game.Prefabs.SubObject>(edge.Prefab, true))
                    {
                        if ((sub.m_Flags & Game.Prefabs.SubObjectFlags.CoursePlacement) != 0)
                        {
                            string name = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(sub.m_Prefab), out PrefabBase p) ? p.name : "?";
                            string at = (sub.m_Flags & Game.Prefabs.SubObjectFlags.StartPlacement) != 0 ? "start" : "end";
                            string own = (sub.m_Flags & Game.Prefabs.SubObjectFlags.MakeOwner) != 0 ? " (owner of the dam edges)" : "";
                            spawn.Add($"{name} at the course {at}{own}, local offset ({sub.m_Position.x:F1}, {sub.m_Position.y:F1}, {sub.m_Position.z:F1})");
                        }
                    }
                }
                string flowMode = (edge.Flow ?? "auto").ToLowerInvariant();
                bool invert = flowMode == "invert" || (flowMode == "auto" && weight < 0f);
                foreach (string e in errors)
                {
                    string message = $"edge '{edge.Id}' ({edge.PrefabName}): {e}";
                    if (mode == "error")
                    {
                        problems.Add(message);
                    }
                    else
                    {
                        job.Warnings.Add(message);
                    }
                }
                foreach (string w in warnings)
                {
                    job.Warnings.Add($"edge '{edge.Id}' ({edge.PrefabName}): {w}");
                }
                report.Add(new
                {
                    edge = edge.Id,
                    prefab = edge.PrefabName,
                    kind = "courseOwner",
                    crest = new[] { yA, yB },
                    elevation = new[] { eA, eB },
                    placement = placement.ToString(),
                    flowWeight = weight,
                    courseStartsAt = invert ? job.Nodes[edge.B].Id : job.Nodes[edge.A].Id,
                    spawns = spawn,
                    errors,
                    warnings,
                });
            }
            return report;
        }

        private float NodeDeckY(BridgeImportToolSystem.ImportNode node, ref TerrainHeightData heightData)
        {
            if (node.Entity != Entity.Null && EntityManager.Exists(node.Entity) && EntityManager.HasComponent<Node>(node.Entity))
            {
                return EntityManager.GetComponentData<Node>(node.Entity).m_Position.y;
            }
            return TerrainUtils.SampleHeight(ref heightData, new float3(node.XZ.x, 0f, node.XZ.y)) + node.Elevation;
        }

        public sealed class PillarRemoveDto
        {
            public List<string> segments;
        }

        /// <summary>POST /net/pillars/remove {segments:["index:version",...]}: deletes the Pillar sub-objects of existing segments.</summary>
        private BridgeResponse RemovePillarsRequest(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            PillarRemoveDto body = null;
            try
            {
                body = Newtonsoft.Json.JsonConvert.DeserializeObject<PillarRemoveDto>(request.Body ?? "");
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body?.segments == null || body.segments.Count == 0)
            {
                return BridgeResponse.Error(400, "POST {segments:[\"index:version\", ...]}");
            }
            var segments = new List<Entity>();
            var bad = new List<string>();
            foreach (string id in body.segments)
            {
                string[] parts = (id ?? "").Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int index) && int.TryParse(parts[1], out int version))
                {
                    var entity = new Entity { Index = index, Version = version };
                    if (EntityManager.Exists(entity) && EntityManager.HasComponent<Edge>(entity))
                    {
                        segments.Add(entity);
                        continue;
                    }
                }
                bad.Add(id);
            }
            int removed = World.GetOrCreateSystemManaged<BridgeImportToolSystem>().RemovePillars(segments, out int nodes);
            return BridgeResponse.Json(new
            {
                segments = segments.Count,
                innerNodes = nodes,
                pillarsDeleted = removed,
                notFound = bad,
                note = "pillars come back when the game updates the segment (an edge or node there is changed)",
            });
        }

        private static float DistanceToLine(float2 p, float2 a, float2 b)
        {
            float2 ab = b - a;
            float len = math.length(ab);
            if (len < 0.001f)
            {
                return math.distance(p, a);
            }
            return math.abs(ab.x * (p.y - a.y) - ab.y * (p.x - a.x)) / len;
        }
    }
}
