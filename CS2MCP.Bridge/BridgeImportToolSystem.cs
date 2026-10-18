using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// Headless batch importer for a node-and-edge network (POST /import/network),
    /// usable in the map editor as well as in a city. Separate from the other
    /// bridge tools so their code stays untouched.
    ///
    /// Each batch goes through the vanilla tool handshake over four tool frames:
    ///   Define  — applyMode Clear, definitions written through ToolOutputBarrier;
    ///   Settle  — applyMode None, the Temp preview is generated and validated;
    ///   Apply   — applyMode Apply if GetAllowApply(), else Clear and the batch is
    ///             split in half and retried, down to single items, so one bad
    ///             edge never blocks the rest;
    ///   Resolve — applyMode None, the new permanent entities are found by diffing
    ///             against a snapshot taken before Define.
    ///
    /// Nodes first: every import node gets its own node entity (a zero-length
    /// course, as GenerateNodesSystem's single-point branch creates) before any
    /// edge is built, and edges then attach to node entities through
    /// CoursePos.m_Entity, never by coordinates. If the game refuses standalone
    /// nodes, the importer falls back to building edges with free ends and
    /// resolving each new node once, so later batches still attach by entity.
    /// Within one batch, free ends with bit-identical positions merge into one
    /// node (GenerateNodesSystem.NodeKey), which is why node positions are
    /// computed once per node.
    /// </summary>
    public sealed partial class BridgeImportToolSystem : ObjectToolBaseSystem
    {
        public sealed class ImportNode
        {
            public string Id;
            public float2 XZ;
            public float Elevation;
            public Entity Entity;
            public bool Given;
            public bool CreatedHere;
            public Entity CreatePrefab;
            public float2 Direction;
            public string Error;
        }

        public sealed class ImportEdge
        {
            public string Id;
            public int A;
            public int B;
            public string PrefabName;
            public Entity Prefab;
            public float2[] Control;
            public string Status = "pending";
            public string Error;
            public List<Entity> Segments = new List<Entity>();
        }

        public sealed class ImportJob
        {
            public string Id;
            public string Name;
            public List<ImportNode> Nodes;
            public List<ImportEdge> Edges;
            public int BatchSize;
            public bool NodesFirst;
            public int SettleFrames;
            public List<string> Warnings = new List<string>();
        }

        /// <summary>Entities created by one import, for /import/undo. Kept in memory only (not saved with the map).</summary>
        public sealed class ImportRecord
        {
            public string Id;
            public string Name;
            public List<Entity> Edges = new List<Entity>();
            public List<Entity> Nodes = new List<Entity>();
            public DateTime Finished;
        }

        private enum Stage
        {
            Idle,
            Define,
            Settle,
            Apply,
            Resolve,
        }

        private enum ItemKind
        {
            Nodes,
            Edges,
            Delete,
        }

        private sealed class WorkItem
        {
            public ItemKind Kind;
            public List<int> Indices;
        }

        private const int kMaxRecords = 20;
        private const float kNodeMatchDistance = 0.75f;
        private const float kCourseMatchDistance = 2f;

        private Stage m_Stage = Stage.Idle;
        private readonly List<WorkItem> m_Queue = new List<WorkItem>();
        private WorkItem m_Current;
        private int m_CurrentDefined;
        private int m_SettleLeft;
        private ImportJob m_Job;
        private ImportRecord m_Record;
        private ImportRecord m_UndoTarget;
        private List<Entity> m_DeleteEdges;
        private List<Entity> m_DeleteNodes;
        private BridgeRequest m_PendingRequest;
        private ToolBaseSystem m_PreviousTool;
        private HashSet<Entity> m_NodeSnapshot;
        private HashSet<Entity> m_EdgeSnapshot;
        private bool m_NodesFirstDisabled;
        private string m_NodesFirstDisabledReason;
        private int m_NodeSuccesses;
        private int m_BatchesApplied;
        private int m_BatchesRejected;
        private int m_Frames;
        private DateTime m_Started;

        private readonly List<ImportRecord> m_Records = new List<ImportRecord>();
        private object m_LastReport;

        private TerrainSystem m_Terrain;
        private EntityQuery m_NodeQuery;
        private EntityQuery m_EdgeQuery;
        private EntityQuery m_IconQuery;

        public override string toolID => "CS2MCP.Import";

        public bool IsBusy => m_Stage != Stage.Idle;

        public object LastReport => m_LastReport;

        public int RecordCount => m_Records.Count;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_NodeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_EdgeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_IconQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Notifications.Icon>(),
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<PrefabRef>());
        }

        public override PrefabBase GetPrefab()
        {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            // Never let the game UI select this tool via asset selection.
            return false;
        }

        /// <summary>Progress of the running import, or the report of the last finished one.</summary>
        public object Status()
        {
            if (m_Stage == Stage.Idle)
            {
                return new { running = false, undoableImports = m_Records.Count, last = m_LastReport };
            }
            if (m_Job == null)
            {
                return new { running = true, operation = "undo", importId = m_UndoTarget != null ? m_UndoTarget.Id : null };
            }
            int created = 0;
            int failed = 0;
            foreach (ImportEdge edge in m_Job.Edges)
            {
                if (edge.Status == "created")
                {
                    created++;
                }
                else if (edge.Status == "failed")
                {
                    failed++;
                }
            }
            return new
            {
                running = true,
                operation = "import",
                importId = m_Job.Id,
                edgesTotal = m_Job.Edges.Count,
                edgesCreated = created,
                edgesFailed = failed,
                batchesQueued = m_Queue.Count,
                batchesApplied = m_BatchesApplied,
                batchesRejected = m_BatchesRejected,
                frames = m_Frames,
            };
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryStartImport(ImportJob job, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_Job = job;
            m_Record = new ImportRecord { Id = job.Id, Name = job.Name };
            m_UndoTarget = null;
            m_Queue.Clear();
            m_NodesFirstDisabled = !job.NodesFirst;
            m_NodesFirstDisabledReason = job.NodesFirst ? null : "disabled by options.nodesFirst=false";
            m_NodeSuccesses = 0;
            m_BatchesApplied = 0;
            m_BatchesRejected = 0;
            m_Frames = 0;
            m_Started = DateTime.UtcNow;

            if (job.NodesFirst)
            {
                var pending = new List<int>();
                for (int i = 0; i < job.Nodes.Count; i++)
                {
                    if (job.Nodes[i].Entity == Entity.Null && job.Nodes[i].CreatePrefab != Entity.Null)
                    {
                        pending.Add(i);
                    }
                }
                EnqueueChunks(ItemKind.Nodes, pending, job.BatchSize);
            }
            var edges = new List<int>();
            for (int i = 0; i < job.Edges.Count; i++)
            {
                edges.Add(i);
            }
            EnqueueChunks(ItemKind.Edges, edges, job.BatchSize);

            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Must be called on the simulation thread. Deletes what the last import (or the one with this id) created.</summary>
        public bool TryStartUndo(string importId, BridgeRequest request, out string error)
        {
            error = null;
            if (m_Stage != Stage.Idle)
            {
                error = "an import or undo is running";
                return false;
            }
            if (m_Records.Count == 0)
            {
                error = "nothing to undo: no import recorded since the game started (records are kept in memory only)";
                return false;
            }
            ImportRecord target = m_Records[m_Records.Count - 1];
            if (!string.IsNullOrEmpty(importId) && target.Id != importId)
            {
                error = $"only the most recent import ({target.Id}) can be undone; undo imports in reverse order";
                return false;
            }

            m_DeleteEdges = new List<Entity>();
            foreach (Entity edge in target.Edges)
            {
                if (EntityManager.Exists(edge) && EntityManager.HasComponent<Edge>(edge) && !EntityManager.HasComponent<Deleted>(edge))
                {
                    m_DeleteEdges.Add(edge);
                }
            }
            // Nodes go away with their last edge; only standalone nodes (from the
            // nodes-first pass, never attached) need deleting on their own.
            m_DeleteNodes = new List<Entity>();
            foreach (Entity node in target.Nodes)
            {
                if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node) || EntityManager.HasComponent<Deleted>(node))
                {
                    continue;
                }
                if (!EntityManager.HasBuffer<ConnectedEdge>(node) || EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true).Length == 0)
                {
                    m_DeleteNodes.Add(node);
                }
            }

            m_Job = null;
            m_Record = null;
            m_UndoTarget = target;
            m_Queue.Clear();
            m_Queue.Add(new WorkItem { Kind = ItemKind.Delete, Indices = new List<int>() });
            m_BatchesApplied = 0;
            m_BatchesRejected = 0;
            m_Frames = 0;
            m_Started = DateTime.UtcNow;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void EnqueueChunks(ItemKind kind, List<int> indices, int size)
        {
            for (int start = 0; start < indices.Count; start += size)
            {
                m_Queue.Add(new WorkItem { Kind = kind, Indices = indices.GetRange(start, math.min(size, indices.Count - start)) });
            }
        }

        private void Activate()
        {
            m_Current = null;
            m_Stage = Stage.Define;
            m_PreviousTool = m_ToolSystem.activeTool;
            m_ToolSystem.activeTool = this;
        }

        [Preserve]
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (m_Stage != Stage.Idle)
            {
                m_Frames++;
            }
            try
            {
                switch (m_Stage)
                {
                    case Stage.Define:
                        StageDefine();
                        break;

                    case Stage.Settle:
                        applyMode = ApplyMode.None;
                        if (--m_SettleLeft <= 0)
                        {
                            m_Stage = Stage.Apply;
                        }
                        break;

                    case Stage.Apply:
                        StageApply();
                        break;

                    case Stage.Resolve:
                        applyMode = ApplyMode.None;
                        StageResolve();
                        break;

                    default:
                        applyMode = ApplyMode.None;
                        if (m_ToolSystem.activeTool == this)
                        {
                            m_ToolSystem.activeTool = m_DefaultToolSystem;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"BridgeImportToolSystem error in stage {m_Stage}: {e}");
                applyMode = ApplyMode.Clear;
                Finish($"{e.GetType().Name}: {e.Message}");
            }
            return inputDeps;
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            if (m_Stage != Stage.Idle && m_ToolSystem.activeTool != this)
            {
                // Another tool took over mid-import: stop, keep the user's tool and
                // report what exists (it stays recorded for /import/undo).
                m_PreviousTool = null;
                Finish("interrupted because another tool became active (the user picked a tool?)");
            }
            base.OnStopRunning();
        }

        private void StageDefine()
        {
            while (m_Queue.Count > 0)
            {
                m_Current = m_Queue[0];
                m_Queue.RemoveAt(0);
                if (m_Current.Kind == ItemKind.Nodes && m_NodesFirstDisabled)
                {
                    continue;
                }

                TakeSnapshot();
                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                TerrainHeightData heightData = m_Terrain.GetHeightData();
                m_CurrentDefined = 0;
                switch (m_Current.Kind)
                {
                    case ItemKind.Nodes:
                        foreach (int index in m_Current.Indices)
                        {
                            DefineNode(m_Job.Nodes[index], commandBuffer, ref heightData);
                            m_CurrentDefined++;
                        }
                        break;
                    case ItemKind.Edges:
                        var kept = new List<int>();
                        foreach (int index in m_Current.Indices)
                        {
                            if (DefineEdge(m_Job.Edges[index], commandBuffer, ref heightData))
                            {
                                kept.Add(index);
                                m_CurrentDefined++;
                            }
                        }
                        m_Current.Indices = kept;
                        break;
                    case ItemKind.Delete:
                        foreach (Entity edge in m_DeleteEdges)
                        {
                            DefineDeleteEdge(edge, commandBuffer);
                            m_CurrentDefined++;
                        }
                        foreach (Entity node in m_DeleteNodes)
                        {
                            DefineDeleteNode(node, commandBuffer);
                            m_CurrentDefined++;
                        }
                        break;
                }

                if (m_CurrentDefined == 0)
                {
                    if (m_Current.Kind == ItemKind.Delete)
                    {
                        m_Current = null;
                        Finish(null);
                        return;
                    }
                    continue;
                }
                applyMode = ApplyMode.Clear;
                m_SettleLeft = m_Job != null ? m_Job.SettleFrames : 1;
                m_Stage = Stage.Settle;
                return;
            }

            applyMode = ApplyMode.None;
            m_Current = null;
            Finish(null);
        }

        private void StageApply()
        {
            if (GetAllowApply())
            {
                applyMode = ApplyMode.Apply;
                m_BatchesApplied++;
                m_Stage = Stage.Resolve;
                return;
            }

            List<string> errors = CollectTempErrors();
            string reason = "blocked by game validation (" +
                (errors.Count > 0 ? string.Join(", ", errors) : "overlap, steep slope, water, too short...") + ")";
            applyMode = ApplyMode.Clear;
            m_BatchesRejected++;
            m_Stage = Stage.Define;

            if (m_Current.Kind == ItemKind.Delete)
            {
                Finish("undo " + reason + "; nothing was deleted");
                return;
            }
            if (m_Current.Indices.Count > 1)
            {
                // Bisect: retry both halves first, before the rest of the queue.
                int half = m_Current.Indices.Count / 2;
                m_Queue.Insert(0, new WorkItem { Kind = m_Current.Kind, Indices = m_Current.Indices.GetRange(half, m_Current.Indices.Count - half) });
                m_Queue.Insert(0, new WorkItem { Kind = m_Current.Kind, Indices = m_Current.Indices.GetRange(0, half) });
                return;
            }

            int single = m_Current.Indices[0];
            if (m_Current.Kind == ItemKind.Edges)
            {
                ImportEdge edge = m_Job.Edges[single];
                edge.Status = "failed";
                edge.Error = reason;
                return;
            }

            ImportNode node = m_Job.Nodes[single];
            node.Error = "standalone node " + reason;
            if (m_NodeSuccesses == 0)
            {
                // The game refuses standalone nodes of this kind altogether; build
                // edges with free ends instead (their nodes are resolved after each batch).
                m_NodesFirstDisabled = true;
                m_NodesFirstDisabledReason = "the first standalone node was rejected (" + node.Error +
                    "); fell back to edges with free ends, resolved to node entities after each batch";
                m_Queue.RemoveAll(item => item.Kind == ItemKind.Nodes);
            }
        }

        private void StageResolve()
        {
            switch (m_Current.Kind)
            {
                case ItemKind.Nodes:
                    ResolveNodes();
                    break;
                case ItemKind.Edges:
                    ResolveEdges();
                    break;
                case ItemKind.Delete:
                    m_Current = null;
                    m_Records.Remove(m_UndoTarget);
                    Finish(null);
                    return;
            }
            m_Current = null;
            m_Stage = Stage.Define;
        }

        // ---- definitions ----

        private float3 NodePosition(ImportNode node, ref TerrainHeightData heightData, out float elevation)
        {
            if (node.Entity != Entity.Null && EntityManager.Exists(node.Entity) && EntityManager.HasComponent<Node>(node.Entity))
            {
                elevation = EntityManager.HasComponent<Game.Net.Elevation>(node.Entity)
                    ? math.csum(EntityManager.GetComponentData<Game.Net.Elevation>(node.Entity).m_Elevation) * 0.5f
                    : 0f;
                return EntityManager.GetComponentData<Node>(node.Entity).m_Position;
            }
            elevation = node.Elevation;
            var position = new float3(node.XZ.x, 0f, node.XZ.y);
            position.y = TerrainUtils.SampleHeight(ref heightData, position) + node.Elevation;
            return position;
        }

        private void DefineNode(ImportNode node, EntityCommandBuffer commandBuffer, ref TerrainHeightData heightData)
        {
            float3 position = NodePosition(node, ref heightData, out float elevation);
            var tangent = new float3(node.Direction.x, 0f, node.Direction.y);
            CoursePosFlags flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight;
            if (elevation != 0f)
            {
                flags |= CoursePosFlags.FreeHeight;
            }
            var pos = new CoursePos
            {
                m_Entity = Entity.Null,
                m_Position = position,
                m_Rotation = NetUtils.GetNodeRotation(tangent),
                m_Elevation = new float2(elevation),
                m_CourseDelta = 0f,
                m_ParentMesh = -1,
                m_Flags = flags,
            };
            NetCourse course = default;
            course.m_Curve = new Bezier4x3(position, position, position, position);
            course.m_Length = 0f;
            course.m_FixedIndex = -1;
            course.m_Elevation = new float2(elevation);
            course.m_StartPosition = pos;
            pos.m_CourseDelta = 1f;
            course.m_EndPosition = pos;

            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            Entity entity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(entity, new CreationDefinition
            {
                m_Prefab = node.CreatePrefab,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.SubElevation,
            });
            commandBuffer.AddComponent(entity, default(Updated));
            commandBuffer.AddComponent(entity, course);
        }

        /// <summary>
        /// One course per edge, mirroring BridgeRoadToolSystem's connected build
        /// (NetToolSystem.CreateDefinitionsJob.CreateStraightLine): ends with a
        /// node entity attach through CoursePos.m_Entity and keep that node's
        /// height (no FreeHeight); free ends sit at terrain + elevation.
        /// </summary>
        private bool DefineEdge(ImportEdge edge, EntityCommandBuffer commandBuffer, ref TerrainHeightData heightData)
        {
            if (edge.Status != "pending")
            {
                return false;
            }
            ImportNode nodeA = m_Job.Nodes[edge.A];
            ImportNode nodeB = m_Job.Nodes[edge.B];
            if (nodeA.Entity != Entity.Null && !EntityManager.Exists(nodeA.Entity))
            {
                nodeA.Entity = Entity.Null;
            }
            if (nodeB.Entity != Entity.Null && !EntityManager.Exists(nodeB.Entity))
            {
                nodeB.Entity = Entity.Null;
            }
            if (nodeA.Entity != Entity.Null && nodeA.Entity == nodeB.Entity)
            {
                edge.Status = "failed";
                edge.Error = $"both ends resolved to the same node entity {nodeA.Entity.Index}:{nodeA.Entity.Version}";
                return false;
            }

            float3 start = NodePosition(nodeA, ref heightData, out float startElevation);
            float3 end = NodePosition(nodeB, ref heightData, out float endElevation);
            bool startAttached = nodeA.Entity != Entity.Null;
            bool endAttached = nodeB.Entity != Entity.Null;

            Bezier4x3 bezier;
            if (edge.Control != null && edge.Control.Length == 2)
            {
                bezier = new Bezier4x3(start,
                    new float3(edge.Control[0].x, math.lerp(start.y, end.y, 1f / 3f), edge.Control[0].y),
                    new float3(edge.Control[1].x, math.lerp(start.y, end.y, 2f / 3f), edge.Control[1].y),
                    end);
            }
            else if (edge.Control != null && edge.Control.Length == 1)
            {
                // Quadratic bezier through the control point, elevated to cubic.
                float3 m = new float3(edge.Control[0].x, math.lerp(start.y, end.y, 0.5f), edge.Control[0].y);
                bezier = new Bezier4x3(start, start + (m - start) * (2f / 3f), end + (m - end) * (2f / 3f), end);
            }
            else
            {
                bezier = NetUtils.StraightCurve(start, end);
            }
            Curve raw = new Curve { m_Bezier = bezier, m_Length = MathUtils.Length(bezier) };
            bezier = NetUtils.AdjustPosition(raw, startAttached, false, endAttached, ref heightData).m_Bezier;
            bezier.b.y += math.lerp(startElevation, endElevation, 1f / 3f);
            bezier.c.y += math.lerp(startElevation, endElevation, 2f / 3f);
            bezier.a = start;
            bezier.d = end;

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;
            course.m_Elevation = new float2(math.min(startElevation, endElevation), math.max(startElevation, endElevation));
            course.m_StartPosition = new CoursePos
            {
                m_Entity = nodeA.Entity,
                m_Position = start,
                m_Elevation = startElevation,
                m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier)),
                m_CourseDelta = 0f,
                m_ParentMesh = -1,
                m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
            };
            course.m_EndPosition = new CoursePos
            {
                m_Entity = nodeB.Entity,
                m_Position = end,
                m_Elevation = endElevation,
                m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier)),
                m_CourseDelta = 1f,
                m_ParentMesh = -1,
                m_Flags = CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
            };
            // Never FreeHeight on an attached end (it breaks the joined node's height).
            if (!startAttached && startElevation != 0f)
            {
                course.m_StartPosition.m_Flags |= CoursePosFlags.FreeHeight;
            }
            if (!endAttached && endElevation != 0f)
            {
                course.m_EndPosition.m_Flags |= CoursePosFlags.FreeHeight;
            }

            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            Entity entity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(entity, new CreationDefinition
            {
                m_Prefab = edge.Prefab,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.SubElevation,
            });
            commandBuffer.AddComponent(entity, default(Updated));
            commandBuffer.AddComponent(entity, course);
            return true;
        }

        /// <summary>Bulldoze definition for a segment, as BridgeToolSystem's demolish (BulldozeToolSystem.AddEntity).</summary>
        private void DefineDeleteEdge(Entity target, EntityCommandBuffer commandBuffer)
        {
            Edge edge = EntityManager.GetComponentData<Edge>(target);
            NetCourse course = default;
            course.m_Curve = EntityManager.GetComponentData<Curve>(target).m_Bezier;
            course.m_Length = MathUtils.Length(course.m_Curve);
            course.m_FixedIndex = EntityManager.HasComponent<Fixed>(target)
                ? EntityManager.GetComponentData<Fixed>(target).m_Index
                : -1;
            course.m_StartPosition.m_Entity = edge.m_Start;
            course.m_StartPosition.m_Position = course.m_Curve.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_EndPosition.m_Entity = edge.m_End;
            course.m_EndPosition.m_Position = course.m_Curve.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve));
            course.m_EndPosition.m_CourseDelta = 1f;

            Entity e = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(e, new CreationDefinition { m_Original = target, m_Flags = CreationFlags.Delete });
            commandBuffer.AddComponent(e, default(Updated));
            commandBuffer.AddComponent(e, course);
        }

        /// <summary>Bulldoze definition for a standalone node: a zero-length course on the node.</summary>
        private void DefineDeleteNode(Entity target, EntityCommandBuffer commandBuffer)
        {
            float3 position = EntityManager.GetComponentData<Node>(target).m_Position;
            var pos = new CoursePos
            {
                m_Entity = target,
                m_Position = position,
                m_Rotation = NetUtils.GetNodeRotation(float3.zero),
                m_CourseDelta = 0f,
                m_ParentMesh = -1,
                m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLast,
            };
            NetCourse course = default;
            course.m_Curve = new Bezier4x3(position, position, position, position);
            course.m_FixedIndex = -1;
            course.m_StartPosition = pos;
            pos.m_CourseDelta = 1f;
            course.m_EndPosition = pos;

            Entity e = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(e, new CreationDefinition { m_Original = target, m_Flags = CreationFlags.Delete });
            commandBuffer.AddComponent(e, default(Updated));
            commandBuffer.AddComponent(e, course);
        }

        // ---- resolution ----

        private void TakeSnapshot()
        {
            m_NodeSnapshot = new HashSet<Entity>();
            m_EdgeSnapshot = new HashSet<Entity>();
            using (NativeArray<Entity> nodes = m_NodeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    m_NodeSnapshot.Add(node);
                }
            }
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity edge in edges)
                {
                    m_EdgeSnapshot.Add(edge);
                }
            }
        }

        private void ResolveNodes()
        {
            var created = new List<Entity>();
            using (NativeArray<Entity> nodes = m_NodeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    if (!m_NodeSnapshot.Contains(node))
                    {
                        created.Add(node);
                    }
                }
            }
            var claimed = new HashSet<Entity>();
            foreach (int index in m_Current.Indices)
            {
                ImportNode node = m_Job.Nodes[index];
                Entity best = Entity.Null;
                float bestDistance = kNodeMatchDistance;
                foreach (Entity candidate in created)
                {
                    if (claimed.Contains(candidate))
                    {
                        continue;
                    }
                    float distance = math.distance(EntityManager.GetComponentData<Node>(candidate).m_Position.xz, node.XZ);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
                if (best == Entity.Null)
                {
                    node.Error = "standalone node applied but no new node entity found at its position";
                    continue;
                }
                claimed.Add(best);
                node.Entity = best;
                node.CreatedHere = true;
                node.Error = null;
                m_NodeSuccesses++;
                m_Record.Nodes.Add(best);
            }
            if (m_NodeSuccesses == 0)
            {
                m_NodesFirstDisabled = true;
                m_NodesFirstDisabledReason = "a standalone-node batch was applied but produced no node entities; " +
                    "fell back to edges with free ends, resolved to node entities after each batch";
                m_Queue.RemoveAll(item => item.Kind == ItemKind.Nodes);
            }
        }

        private void ResolveEdges()
        {
            var newEdges = new List<Entity>();
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity edge in edges)
                {
                    if (!m_EdgeSnapshot.Contains(edge))
                    {
                        newEdges.Add(edge);
                    }
                }
            }

            TerrainHeightData heightData = m_Terrain.GetHeightData();
            foreach (int index in m_Current.Indices)
            {
                ImportEdge edge = m_Job.Edges[index];
                ImportNode nodeA = m_Job.Nodes[edge.A];
                ImportNode nodeB = m_Job.Nodes[edge.B];
                float2 a = nodeA.Entity != Entity.Null ? NodePosition(nodeA, ref heightData, out _).xz : nodeA.XZ;
                float2 b = nodeB.Entity != Entity.Null ? NodePosition(nodeB, ref heightData, out _).xz : nodeB.XZ;
                Bezier4x2 course = CourseXZ(edge, a, b);

                foreach (Entity candidate in newEdges)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != edge.Prefab)
                    {
                        continue;
                    }
                    Bezier4x3 bezier = EntityManager.GetComponentData<Curve>(candidate).m_Bezier;
                    if (MathUtils.Distance(course, bezier.a.xz, out _) > kCourseMatchDistance
                        || MathUtils.Distance(course, bezier.d.xz, out _) > kCourseMatchDistance
                        || MathUtils.Distance(course, MathUtils.Position(bezier, 0.5f).xz, out _) > kCourseMatchDistance)
                    {
                        continue;
                    }
                    edge.Segments.Add(candidate);
                }

                if (edge.Segments.Count == 0)
                {
                    edge.Status = "failed";
                    edge.Error = "the batch was applied but no new segment of this prefab was found along the course";
                    continue;
                }
                edge.Status = "created";
                foreach (Entity segment in edge.Segments)
                {
                    m_Record.Edges.Add(segment);
                }
                ResolveEndNode(nodeA, edge.Segments);
                ResolveEndNode(nodeB, edge.Segments);
            }
        }

        private void ResolveEndNode(ImportNode node, List<Entity> segments)
        {
            Entity best = Entity.Null;
            float bestDistance = kNodeMatchDistance;
            foreach (Entity segment in segments)
            {
                Edge ends = EntityManager.GetComponentData<Edge>(segment);
                foreach (Entity candidate in new[] { ends.m_Start, ends.m_End })
                {
                    if (!EntityManager.HasComponent<Node>(candidate))
                    {
                        continue;
                    }
                    float distance = math.distance(EntityManager.GetComponentData<Node>(candidate).m_Position.xz, node.XZ);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }
            if (best == Entity.Null)
            {
                if (node.Entity == Entity.Null)
                {
                    m_Job.Warnings.Add($"node {node.Id}: no node entity found at its position after building an edge; later edges will start a new node there");
                }
                return;
            }
            if (node.Entity != Entity.Null && node.Entity != best && node.Given)
            {
                m_Job.Warnings.Add($"node {node.Id}: given entity {node.Entity.Index}:{node.Entity.Version} was replaced by {best.Index}:{best.Version}");
            }
            if (node.Entity == Entity.Null && !m_NodeSnapshot.Contains(best))
            {
                m_Record.Nodes.Add(best);
                node.CreatedHere = true;
            }
            node.Entity = best;
        }

        private static Bezier4x2 CourseXZ(ImportEdge edge, float2 a, float2 b)
        {
            if (edge.Control != null && edge.Control.Length == 2)
            {
                return new Bezier4x2(a, edge.Control[0], edge.Control[1], b);
            }
            if (edge.Control != null && edge.Control.Length == 1)
            {
                float2 m = edge.Control[0];
                return new Bezier4x2(a, a + (m - a) * (2f / 3f), b + (m - b) * (2f / 3f), b);
            }
            return new Bezier4x2(a, math.lerp(a, b, 1f / 3f), math.lerp(a, b, 2f / 3f), b);
        }

        /// <summary>Names of the validation errors the game raised on temp entities.</summary>
        private List<string> CollectTempErrors()
        {
            var names = new List<string>();
            using (NativeArray<Entity> icons = m_IconQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < icons.Length; i++)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(icons[i]).m_Owner;
                    if (!EntityManager.Exists(owner) || !EntityManager.HasComponent<Temp>(owner))
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(icons[i]).m_Prefab;
                    if (!EntityManager.HasComponent<ToolErrorData>(prefab))
                    {
                        continue;
                    }
                    string name = EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error.ToString();
                    if (!names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
            return names;
        }

        // ---- completion ----

        private void Finish(string abortReason)
        {
            BridgeResponse response;
            if (m_Job != null)
            {
                object report = BuildImportReport(abortReason);
                m_LastReport = report;
                if (m_Record != null && (m_Record.Edges.Count > 0 || m_Record.Nodes.Count > 0))
                {
                    m_Record.Finished = DateTime.UtcNow;
                    m_Records.Add(m_Record);
                    if (m_Records.Count > kMaxRecords)
                    {
                        m_Records.RemoveAt(0);
                    }
                }
                response = BridgeResponse.Json(report);
            }
            else
            {
                object report = new
                {
                    undone = abortReason == null,
                    importId = m_UndoTarget != null ? m_UndoTarget.Id : null,
                    edgesDeleted = abortReason == null && m_DeleteEdges != null ? m_DeleteEdges.Count : 0,
                    standaloneNodesDeleted = abortReason == null && m_DeleteNodes != null ? m_DeleteNodes.Count : 0,
                    error = abortReason,
                    undoableImportsLeft = m_Records.Count,
                    note = "deleted through the game's bulldoze pipeline; nodes left without segments are removed by the game",
                };
                m_LastReport = report;
                response = abortReason == null ? BridgeResponse.Json(report) : BridgeResponse.Json(report, 409);
            }

            m_PendingRequest?.Complete(response);
            m_PendingRequest = null;
            m_Stage = Stage.Idle;
            m_Queue.Clear();
            m_Current = null;
            m_Job = null;
            m_Record = null;
            m_UndoTarget = null;
            m_DeleteEdges = null;
            m_DeleteNodes = null;
            m_NodeSnapshot = null;
            m_EdgeSnapshot = null;
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null && m_PreviousTool != this ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
        }

        private object BuildImportReport(string abortReason)
        {
            var results = new List<object>();
            int created = 0;
            int failed = 0;
            int notRun = 0;
            foreach (ImportEdge edge in m_Job.Edges)
            {
                if (edge.Status == "created")
                {
                    created++;
                }
                else if (edge.Status == "failed")
                {
                    failed++;
                }
                else
                {
                    notRun++;
                }
                ImportNode a = m_Job.Nodes[edge.A];
                ImportNode b = m_Job.Nodes[edge.B];
                var segments = new List<object>();
                foreach (Entity segment in edge.Segments)
                {
                    segments.Add(new { index = segment.Index, version = segment.Version });
                }
                results.Add(new
                {
                    id = edge.Id,
                    status = edge.Status,
                    error = edge.Error,
                    prefab = edge.PrefabName,
                    segments,
                    nodeA = a.Entity != Entity.Null ? new { index = a.Entity.Index, version = a.Entity.Version } : null,
                    nodeB = b.Entity != Entity.Null ? new { index = b.Entity.Index, version = b.Entity.Version } : null,
                });
            }

            var nodeResults = new List<object>();
            int nodesResolved = 0;
            foreach (ImportNode node in m_Job.Nodes)
            {
                if (node.Entity != Entity.Null)
                {
                    nodesResolved++;
                }
                if (node.Error != null || node.Entity == Entity.Null)
                {
                    nodeResults.Add(new { id = node.Id, error = node.Error ?? "no node entity (no created edge reaches it)" });
                }
            }

            return new
            {
                importId = m_Job.Id,
                name = m_Job.Name,
                completed = abortReason == null,
                abortReason,
                edges = new { requested = m_Job.Edges.Count, created, failed, notRun },
                nodes = new { requested = m_Job.Nodes.Count, resolved = nodesResolved },
                nodesFirst = new { used = !m_NodesFirstDisabled, fallbackReason = m_NodesFirstDisabledReason },
                batches = new { applied = m_BatchesApplied, rejected = m_BatchesRejected },
                frames = m_Frames,
                seconds = Math.Round((DateTime.UtcNow - m_Started).TotalSeconds, 1),
                undo = new
                {
                    segmentsRecorded = m_Record != null ? m_Record.Edges.Count : 0,
                    nodesRecorded = m_Record != null ? m_Record.Nodes.Count : 0,
                    note = "POST /import/undo removes what this import created (in memory only; lost when the game restarts)",
                },
                warnings = m_Job.Warnings,
                unresolvedNodes = nodeResults,
                results,
            };
        }
    }
}
