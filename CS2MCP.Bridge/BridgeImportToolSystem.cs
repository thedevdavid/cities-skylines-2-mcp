using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
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
            /// <summary>0.9.38: heightMode absolute: the requested world y, resolved against the terrain at build time.</summary>
            public float? AbsoluteY;
            /// <summary>0.9.40: no elevation reproduces AbsoluteY under the game's FreeHeight rule; keep the given y.</summary>
            public bool KeepY;
            public Entity Entity;
            public bool Given;
            public bool CreatedHere;
            public Entity CreatePrefab;
            public string CreatePrefabName;
            public float2 Direction;
            /// <summary>No node exists here any more: the game merged the two collinear segments meeting here into one.</summary>
            public bool Absorbed;
            /// <summary>snapToSegment: attach to the nearest existing segment within this radius (0 = off).</summary>
            public float SnapRadius;
            public string SnapPrefabContains;
            /// <summary>Used by a course-owner net (dam): never created as a standalone node.</summary>
            public bool NoStandalone;
            public float2 OriginalXZ;
            public bool SnapTried;
            public Entity SnapSegment;
            public float SnapT;
            public float SnapDistance;
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
            /// <summary>Upgrade flags forced on the new segment (quay walls: Side.Raised on the wall side).</summary>
            public CompositionFlags Upgrades;
            /// <summary>Delete the generic Game.Objects.Pillar sub-objects of the built segments at the end of the import.</summary>
            public bool NoPillars;
            /// <summary>0.9.39: raise the stored Elevation so the edge gets the Elevated composition and pillars.</summary>
            public bool ForceElevated;
            /// <summary>Water-flow nets (dams): "auto" (default, as the net tool), "asIs" or "invert".</summary>
            public string Flow;
            public float FlowWeight;
            public bool Inverted;
            /// <summary>Course objects the prefab spawned (the dam's power plant and ends).</summary>
            public List<string> CourseObjects = new List<string>();
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
            public bool FullReport;
            /// <summary>Set ToolSystem.ignoreErrors for the duration of the import (restored afterwards).</summary>
            public bool BypassValidation;
            public List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// Water sources to create and/or delete in one tool frame. The command
        /// buffer of ToolOutputBarrier may only be created inside a system update,
        /// so /import/water and its undo run here, as Water Features does from its tool.
        /// </summary>
        public sealed class WaterWork
        {
            public List<Game.Simulation.WaterSourceData> Sources = new List<Game.Simulation.WaterSourceData>();
            public List<float3> Positions = new List<float3>();
            public List<Entity> Deletes = new List<Entity>();
            /// <summary>Called on the main thread with the assigned source ids; builds the HTTP answer.</summary>
            public Func<List<int>, BridgeResponse> Complete;
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
            Warmup,
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
        // Standalone nodes are judged per network prefab: one that the game refuses
        // (e.g. a track type) falls back to free edge ends without affecting others.
        private readonly HashSet<Entity> m_NodePrefabsAccepted = new HashSet<Entity>();
        private readonly Dictionary<Entity, string> m_NodePrefabsRefused = new Dictionary<Entity, string>();
        private int m_NodeSuccesses;
        private int m_BatchesApplied;
        private int m_BatchesRejected;
        private int m_Frames;
        private DateTime m_Started;

        private readonly List<ImportRecord> m_Records = new List<ImportRecord>();
        private readonly HashSet<Entity> m_RecordEdgeSet = new HashSet<Entity>();
        // Segments split and nodes placed by a split in the current batch: a second
        // split of the same segment, or another edge at that node, waits for the next batch.
        private readonly HashSet<Entity> m_BatchSplitSegments = new HashSet<Entity>();
        private readonly HashSet<ImportNode> m_BatchSplitNodes = new HashSet<ImportNode>();
        private readonly Dictionary<Entity, string> m_PrefabNames = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, float> m_HalfWidths = new Dictionary<Entity, float>();
        private object m_LastReport;

        private TerrainSystem m_Terrain;
        private EntityArchetype m_WaterSourceArchetype;
        private WaterWork m_WaterWork;
        private WaterSurfaceData<SurfaceWater> m_WaterData;
        private bool m_HaveWater;
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
            m_WaterSourceArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<Game.Simulation.WaterSourceData>(),
                ComponentType.ReadWrite<Game.Objects.Transform>(),
                ComponentType.ReadWrite<Created>());
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
                return new
                {
                    running = false,
                    undoableImports = m_Records.Count,
                    validation = AnarchyInterop.Describe(m_ToolSystem),
                    last = m_LastReport,
                };
            }
            if (m_Job == null)
            {
                return new
                {
                    running = true,
                    operation = m_WaterWork != null ? "water" : "undo",
                    importId = m_UndoTarget != null ? m_UndoTarget.Id : null,
                };
            }
            int created = 0;
            int failed = 0;
            foreach (ImportEdge edge in m_Job.Edges)
            {
                if (edge.Status == "created" || edge.Status == "merged")
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
            m_RecordEdgeSet.Clear();
            m_UndoTarget = null;
            m_Queue.Clear();
            m_NodePrefabsAccepted.Clear();
            m_NodePrefabsRefused.Clear();
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
                    // Nodes that snap to a segment are placed by the split, not as standalone nodes.
                    if (job.Nodes[i].Entity == Entity.Null && job.Nodes[i].CreatePrefab != Entity.Null && job.Nodes[i].SnapRadius <= 0f
                        && !job.Nodes[i].NoStandalone)
                    {
                        pending.Add(i);
                    }
                }
                EnqueueChunks(ItemKind.Nodes, pending, job.BatchSize);
            }
            // Edges with a snapping end go last, so the segments they snap to exist by then.
            var edges = new List<int>();
            var snapping = new List<int>();
            for (int i = 0; i < job.Edges.Count; i++)
            {
                ImportEdge edge = job.Edges[i];
                (job.Nodes[edge.A].SnapRadius > 0f || job.Nodes[edge.B].SnapRadius > 0f ? snapping : edges).Add(i);
            }
            edges.AddRange(snapping);
            EnqueueChunks(ItemKind.Edges, edges, job.BatchSize);

            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Must be called on the simulation thread. Creates/deletes water sources on the next tool frames.</summary>
        public bool TryStartWater(WaterWork work, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_Job = null;
            m_Record = null;
            m_UndoTarget = null;
            m_Queue.Clear();
            m_WaterWork = work;
            m_Frames = 0;
            m_Started = DateTime.UtcNow;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void RunWater()
        {
            applyMode = ApplyMode.None;
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            var ids = new List<int>(m_WaterWork.Sources.Count);
            for (int i = 0; i < m_WaterWork.Sources.Count; i++)
            {
                Game.Simulation.WaterSourceData data = m_WaterWork.Sources[i];
                data.m_Id = m_WaterSystem.GetNextSourceId();
                Entity entity = commandBuffer.CreateEntity(m_WaterSourceArchetype);
                commandBuffer.SetComponent(entity, data);
                commandBuffer.SetComponent(entity, new Game.Objects.Transform { m_Position = m_WaterWork.Positions[i], m_Rotation = quaternion.identity });
                commandBuffer.AddComponent(entity, default(Updated));
                ids.Add(data.m_Id);
            }
            foreach (Entity entity in m_WaterWork.Deletes)
            {
                if (EntityManager.Exists(entity) && !EntityManager.HasComponent<Deleted>(entity))
                {
                    commandBuffer.AddComponent(entity, default(Deleted));
                }
            }
            FinishWater(m_WaterWork.Complete(ids));
        }

        private void FinishWater(BridgeResponse response)
        {
            m_PendingRequest?.Complete(response);
            m_PendingRequest = null;
            m_WaterWork = null;
            m_Stage = Stage.Idle;
            RestoreIgnoreErrors();
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null && m_PreviousTool != this ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
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

        // Tool frames between activating this tool and the first definitions: on a
        // tool change Anarchy's EnableToolErrorsSystem restores every tool error for
        // a frame before its DisableToolErrorsSystem suppresses them again.
        private const int kWarmupFrames = 3;
        private int m_WarmupLeft;
        private bool m_IgnoreErrorsAtStart;
        private bool m_AnarchyDetected;
        private bool? m_AnarchyEnabledAtStart;
        private bool m_AnarchyRegistered;
        private bool m_RestoreIgnoreErrors;
        private bool m_IgnoreErrorsBefore;

        private void Activate()
        {
            m_Current = null;
            m_IgnoreErrorsAtStart = m_ToolSystem.ignoreErrors;
            m_AnarchyDetected = AnarchyInterop.Detected;
            m_AnarchyEnabledAtStart = AnarchyInterop.ToggleState();
            if (!m_AnarchyRegistered && m_AnarchyDetected)
            {
                m_AnarchyRegistered = AnarchyInterop.RegisterTool(this);
            }
            if (m_Job != null && m_Job.BypassValidation)
            {
                // Same switch as the game's own "ignore errors" debug option: GetAllowApply
                // then accepts batches whatever the validation errors.
                m_IgnoreErrorsBefore = m_ToolSystem.ignoreErrors;
                m_RestoreIgnoreErrors = true;
                m_ToolSystem.ignoreErrors = true;
            }
            m_WarmupLeft = kWarmupFrames;
            m_Stage = Stage.Warmup;
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
                    case Stage.Warmup:
                        applyMode = ApplyMode.None;
                        if (--m_WarmupLeft <= 0)
                        {
                            m_Stage = Stage.Define;
                        }
                        break;

                    case Stage.Define:
                        if (m_WaterWork != null)
                        {
                            RunWater();
                        }
                        else
                        {
                            StageDefine();
                        }
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
                if (m_WaterWork != null)
                {
                    FinishWater(BridgeResponse.Error(500, $"water operation failed: {e.GetType().Name}: {e.Message}"));
                }
                else
                {
                    Finish($"{e.GetType().Name}: {e.Message}");
                }
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
                if (m_WaterWork != null)
                {
                    FinishWater(BridgeResponse.Error(409, "interrupted because another tool became active before the water sources were created; nothing was changed"));
                }
                else
                {
                    Finish("interrupted because another tool became active (the user picked a tool?)");
                }
            }
            base.OnStopRunning();
        }

        private void StageDefine()
        {
            while (m_Queue.Count > 0)
            {
                m_Current = m_Queue[0];
                m_Queue.RemoveAt(0);
                if (m_Current.Kind == ItemKind.Nodes)
                {
                    m_Current.Indices.RemoveAll(i => m_NodePrefabsRefused.ContainsKey(m_Job.Nodes[i].CreatePrefab));
                    if (m_Current.Indices.Count == 0)
                    {
                        continue;
                    }
                }

                TakeSnapshot();
                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                TerrainHeightData heightData = m_Terrain.GetHeightData();
                m_HaveWater = false;
                try
                {
                    m_WaterData = m_WaterSystem.GetSurfaceData(out JobHandle waterDeps);
                    waterDeps.Complete();
                    m_HaveWater = true;
                }
                catch (Exception)
                {
                }
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
                        m_BatchSplitSegments.Clear();
                        m_BatchSplitNodes.Clear();
                        var kept = new List<int>();
                        var deferred = new List<int>();
                        foreach (int index in m_Current.Indices)
                        {
                            switch (DefineEdge(m_Job.Edges[index], commandBuffer, ref heightData))
                            {
                                case EdgeDefinition.Defined:
                                    kept.Add(index);
                                    m_CurrentDefined++;
                                    break;
                                case EdgeDefinition.Deferred:
                                    deferred.Add(index);
                                    break;
                            }
                        }
                        m_Current.Indices = kept;
                        if (deferred.Count > 0)
                        {
                            m_Queue.Insert(0, new WorkItem { Kind = ItemKind.Edges, Indices = deferred });
                        }
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
            if (!m_NodePrefabsAccepted.Contains(node.CreatePrefab))
            {
                // The game refuses standalone nodes of this prefab; its nodes get
                // free edge ends instead (resolved to node entities after each batch).
                RefuseNodePrefab(node, "the first standalone node of this prefab was rejected (" + node.Error + ")");
            }
        }

        private void RefuseNodePrefab(ImportNode node, string reason)
        {
            if (!m_NodePrefabsRefused.ContainsKey(node.CreatePrefab))
            {
                m_NodePrefabsRefused[node.CreatePrefab] = (node.CreatePrefabName ?? "?") + ": " + reason +
                    "; its nodes were built as free edge ends instead";
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

        private float3 NodePosition(ImportNode node, ref TerrainHeightData heightData, out float elevation, Entity edgePrefab = default)
        {
            if (node.Entity != Entity.Null && EntityManager.Exists(node.Entity) && EntityManager.HasComponent<Node>(node.Entity))
            {
                elevation = EntityManager.HasComponent<Game.Net.Elevation>(node.Entity)
                    ? math.csum(EntityManager.GetComponentData<Game.Net.Elevation>(node.Entity).m_Elevation) * 0.5f
                    : 0f;
                return EntityManager.GetComponentData<Node>(node.Entity).m_Position;
            }
            var position = new float3(node.XZ.x, 0f, node.XZ.y);
            float ground = TerrainUtils.SampleHeight(ref heightData, position);
            if (node.AbsoluteY.HasValue)
            {
                // 0.9.40: a FreeHeight course end is placed by CourseSplitSystem.InitializeCoursePos
                // at max(terrain, water surface + 2 x elevation limit) + elevation (terrain only when
                // the elevation is below -1). An elevation measured from the terrain alone put every
                // absolute node over water that much too high: a test import's interchange stood 34-38 m
                // above its CS1 y. Measure from the same base, so the end lands on AbsoluteY.
                float y = node.AbsoluteY.Value;
                float baseHeight = ground;
                if (m_HaveWater)
                {
                    WaterUtils.SampleHeight(ref m_WaterData, ref heightData, position, out float terrainHeight, out float waterHeight, out float depth);
                    float limit = edgePrefab != Entity.Null && EntityManager.HasComponent<NetGeometryData>(edgePrefab)
                        ? EntityManager.GetComponentData<NetGeometryData>(edgePrefab).m_ElevationLimit
                        : 4f;
                    if (depth >= 0.2f)
                    {
                        baseHeight = math.max(terrainHeight, waterHeight + limit * 2f);
                    }
                    ground = terrainHeight;
                }
                elevation = y - baseHeight;
                node.KeepY = false;
                if (elevation < -1f)
                {
                    // Below -1 the game measures from the terrain alone.
                    elevation = y - ground;
                    if (elevation >= -1f)
                    {
                        // Between the two bases: no elevation gives y; build at y without FreeHeight.
                        node.KeepY = true;
                    }
                }
                node.Elevation = elevation;
                position.y = y;
                return position;
            }
            elevation = node.Elevation;
            position.y = ground + node.Elevation;
            return position;
        }

        private void DefineNode(ImportNode node, EntityCommandBuffer commandBuffer, ref TerrainHeightData heightData)
        {
            float3 position = NodePosition(node, ref heightData, out float elevation, node.CreatePrefab);
            var tangent = new float3(node.Direction.x, 0f, node.Direction.y);
            CoursePosFlags flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight;
            if (elevation != 0f && !node.KeepY)
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

        private struct EndInfo
        {
            public Entity Entity;
            /// <summary>&gt; 0 when Entity is a segment split at this curve parameter.</summary>
            public float Split;
            public float3 Position;
            public float Elevation;
            public bool Snapped;
            public bool SnapFellBack;
            public Entity SnapSegment;
            public float SnapT;
            public float SnapDistance;
        }

        private enum EdgeDefinition
        {
            Skipped,
            Defined,
            Deferred,
        }

        /// <summary>
        /// Where an edge end attaches: the import node's entity if it still exists;
        /// else, if the game merged the segments through that node (it drops a node
        /// between two collinear segments of the same prefab), a split of the
        /// imported segment that now runs through the point (CoursePos.m_Entity =
        /// segment + m_SplitPosition, as the road tool snaps onto a segment); else
        /// a free point at terrain + elevation.
        /// </summary>
        private EndInfo ResolveEnd(ImportNode node, Entity edgePrefab, ref TerrainHeightData heightData)
        {
            if (node.Entity != Entity.Null
                && (!EntityManager.Exists(node.Entity) || !EntityManager.HasComponent<Node>(node.Entity)
                    || EntityManager.HasComponent<Deleted>(node.Entity)))
            {
                node.Entity = Entity.Null;
                node.Absorbed = true;
            }
            if (node.Entity != Entity.Null)
            {
                float3 position = NodePosition(node, ref heightData, out float elevation, edgePrefab);
                return new EndInfo { Entity = node.Entity, Position = position, Elevation = elevation };
            }
            bool snapFellBack = false;
            if (node.SnapRadius > 0f && !node.SnapTried)
            {
                if (FindSnapSegment(node, edgePrefab, out Entity snapSegment, out float snapT, out float snapDistance))
                {
                    var info = new EndInfo { Snapped = true, SnapSegment = snapSegment, SnapT = snapT, SnapDistance = snapDistance };
                    if (snapT < 0.02f || snapT > 0.98f)
                    {
                        Edge ends = EntityManager.GetComponentData<Edge>(snapSegment);
                        info.Entity = snapT < 0.02f ? ends.m_Start : ends.m_End;
                        info.Position = EntityManager.GetComponentData<Node>(info.Entity).m_Position;
                        info.Elevation = EntityManager.HasComponent<Game.Net.Elevation>(info.Entity)
                            ? math.csum(EntityManager.GetComponentData<Game.Net.Elevation>(info.Entity).m_Elevation) * 0.5f
                            : 0f;
                    }
                    else
                    {
                        info.Entity = snapSegment;
                        info.Split = snapT;
                        info.Position = MathUtils.Position(EntityManager.GetComponentData<Curve>(snapSegment).m_Bezier, snapT);
                        info.Elevation = EntityManager.HasComponent<Game.Net.Elevation>(snapSegment)
                            ? math.csum(EntityManager.GetComponentData<Game.Net.Elevation>(snapSegment).m_Elevation) * 0.5f
                            : 0f;
                    }
                    return info;
                }
                snapFellBack = true;
            }
            if (node.Absorbed && FindRecordedSegmentAt(node.XZ, out Entity segment, out float t))
            {
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(segment).m_Bezier;
                float elevation = EntityManager.HasComponent<Game.Net.Elevation>(segment)
                    ? math.csum(EntityManager.GetComponentData<Game.Net.Elevation>(segment).m_Elevation) * 0.5f
                    : 0f;
                if (t < 0.02f || t > 0.98f)
                {
                    Edge ends = EntityManager.GetComponentData<Edge>(segment);
                    Entity endNode = t < 0.02f ? ends.m_Start : ends.m_End;
                    node.Entity = endNode;
                    node.Absorbed = false;
                    float3 nodePosition = NodePosition(node, ref heightData, out float nodeElevation, edgePrefab);
                    return new EndInfo { Entity = endNode, Position = nodePosition, Elevation = nodeElevation };
                }
                return new EndInfo { Entity = segment, Split = t, Position = MathUtils.Position(curve, t), Elevation = elevation };
            }
            float3 free = NodePosition(node, ref heightData, out float freeElevation, edgePrefab);
            return new EndInfo { Position = free, Elevation = freeElevation, SnapFellBack = snapFellBack };
        }

        /// <summary>
        /// snapToSegment: the nearest existing top-level segment within the node's
        /// radius (measured from its requested position) whose prefab name contains
        /// the filter and whose layers can connect to the edge's prefab.
        /// </summary>
        private bool FindSnapSegment(ImportNode node, Entity edgePrefab, out Entity segment, out float t, out float distance)
        {
            segment = Entity.Null;
            t = 0f;
            distance = node.SnapRadius;
            NetData netData = EntityManager.HasComponent<NetData>(edgePrefab) ? EntityManager.GetComponentData<NetData>(edgePrefab) : default;
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity candidate in edges)
                {
                    if (EntityManager.HasComponent<Owner>(candidate))
                    {
                        continue;
                    }
                    Bezier4x3 curve = EntityManager.GetComponentData<Curve>(candidate).m_Bezier;
                    if (!InBounds(curve.xz, node.OriginalXZ, distance))
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;
                    if (EntityManager.HasComponent<NetData>(prefab))
                    {
                        NetData other = EntityManager.GetComponentData<NetData>(prefab);
                        if ((netData.m_ConnectLayers & other.m_RequiredLayers) == 0 && (other.m_ConnectLayers & netData.m_RequiredLayers) == 0)
                        {
                            continue;
                        }
                    }
                    if (!string.IsNullOrEmpty(node.SnapPrefabContains)
                        && PrefabName(prefab).IndexOf(node.SnapPrefabContains, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    float candidateDistance = MathUtils.Distance(curve.xz, node.OriginalXZ, out float candidateT);
                    if (candidateDistance < distance)
                    {
                        distance = candidateDistance;
                        segment = candidate;
                        t = candidateT;
                    }
                }
            }
            return segment != Entity.Null;
        }

        private string PrefabName(Entity prefab)
        {
            if (!m_PrefabNames.TryGetValue(prefab, out string name))
            {
                PrefabBase prefabBase = m_PrefabSystem.GetPrefab<PrefabBase>(prefab);
                name = prefabBase != null ? prefabBase.name : "";
                m_PrefabNames[prefab] = name;
            }
            return name;
        }

        /// <summary>Applies what resolving an end decided, once the edge is actually defined.</summary>
        private void CommitEnd(ImportNode node, EndInfo end)
        {
            if (end.Snapped)
            {
                node.SnapTried = true;
                node.SnapSegment = end.SnapSegment;
                node.SnapT = end.SnapT;
                node.SnapDistance = end.SnapDistance;
                node.XZ = end.Position.xz;
                if (end.Split <= 0f)
                {
                    node.Entity = end.Entity;
                }
            }
            else if (end.SnapFellBack)
            {
                node.SnapTried = true;
            }
            if (end.Split > 0f)
            {
                m_BatchSplitSegments.Add(end.Entity);
                m_BatchSplitNodes.Add(node);
            }
        }

        /// <summary>The nearest segment this import created that passes within 1 m of the point.</summary>
        private bool FindRecordedSegmentAt(float2 point, out Entity segment, out float t)
        {
            segment = Entity.Null;
            t = 0f;
            float best = 1f;
            foreach (Entity candidate in m_RecordEdgeSet)
            {
                if (!EntityManager.Exists(candidate) || !EntityManager.HasComponent<Edge>(candidate)
                    || EntityManager.HasComponent<Deleted>(candidate))
                {
                    continue;
                }
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(candidate).m_Bezier;
                if (!InBounds(curve.xz, point, best))
                {
                    continue;
                }
                float distance = MathUtils.Distance(curve.xz, point, out float candidateT);
                if (distance < best)
                {
                    best = distance;
                    segment = candidate;
                    t = candidateT;
                }
            }
            return segment != Entity.Null;
        }

        private static bool InBounds(Bezier4x2 curve, float2 point, float margin)
        {
            float2 min = math.min(math.min(curve.a, curve.b), math.min(curve.c, curve.d)) - margin;
            float2 max = math.max(math.max(curve.a, curve.b), math.max(curve.c, curve.d)) + margin;
            return math.all(point >= min) && math.all(point <= max);
        }

        /// <summary>
        /// One course per edge, mirroring BridgeRoadToolSystem's connected build
        /// (NetToolSystem.CreateDefinitionsJob.CreateStraightLine): ends with a
        /// node entity attach through CoursePos.m_Entity and keep that node's
        /// height (no FreeHeight); free ends sit at terrain + elevation.
        /// </summary>
        /// <summary>Above this elevation at both ends an absolute-height edge gets a linear y profile.</summary>
        private const float kElevatedForLinearY = 2f;

        private EdgeDefinition DefineEdge(ImportEdge edge, EntityCommandBuffer commandBuffer, ref TerrainHeightData heightData)
        {
            if (edge.Status != "pending")
            {
                return EdgeDefinition.Skipped;
            }
            ImportNode nodeA = m_Job.Nodes[edge.A];
            ImportNode nodeB = m_Job.Nodes[edge.B];
            if (m_BatchSplitNodes.Contains(nodeA) || m_BatchSplitNodes.Contains(nodeB))
            {
                return EdgeDefinition.Deferred;
            }
            EndInfo endA = ResolveEnd(nodeA, edge.Prefab, ref heightData);
            EndInfo endB = ResolveEnd(nodeB, edge.Prefab, ref heightData);
            if (endA.Entity != Entity.Null && endA.Entity == endB.Entity)
            {
                edge.Status = "failed";
                edge.Error = $"both ends resolve to the same entity {endA.Entity.Index}:{endA.Entity.Version}" +
                    (endA.Split > 0f || endB.Split > 0f ? " (both would split the same segment)" : "");
                return EdgeDefinition.Skipped;
            }
            if ((endA.Split > 0f && m_BatchSplitSegments.Contains(endA.Entity))
                || (endB.Split > 0f && m_BatchSplitSegments.Contains(endB.Entity)))
            {
                return EdgeDefinition.Deferred;
            }
            CommitEnd(nodeA, endA);
            CommitEnd(nodeB, endB);

            float3 start = endA.Position;
            float3 end = endB.Position;
            float startElevation = endA.Elevation;
            float endElevation = endB.Elevation;
            bool startAttached = endA.Entity != Entity.Null;
            bool endAttached = endB.Entity != Entity.Null;

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
            bool level = EntityManager.HasComponent<NetGeometryData>(edge.Prefab)
                && EntityManager.GetComponentData<NetGeometryData>(edge.Prefab).m_MaxSlopeSteepness == 0f;
            if (level)
            {
                // Max-slope-0 nets (the hydro dam): NetToolSystem.CreateContinuousCurve /
                // CreateStraightLine clamp the elevation to the prefab range (0..0) and then
                // SetHeight every control point to the start height, so the course is a level
                // straight line at the start's y with elevation 0 at both ends. A course that
                // follows the terrain instead makes CourseSplitSystem lay the straight fixed
                // spans along the curve but sample the others from the ends, and the spans
                // stop sharing nodes (0.9.24 built a dam in five unconnected pieces).
                if (!endAttached)
                {
                    end.y = start.y;
                }
                bezier = NetUtils.StraightCurve(start, end);
                if (edge.Control != null && m_Job.Warnings.Count < 1000)
                {
                    m_Job.Warnings.Add($"edge '{edge.Id}': control points ignored; {edge.PrefabName} has max slope 0 and is built as a level straight line, as the net tool does");
                }
                startElevation = 0f;
                endElevation = 0f;
            }
            else if (nodeA.AbsoluteY.HasValue && nodeB.AbsoluteY.HasValue && math.min(startElevation, endElevation) > kElevatedForLinearY)
            {
                // 0.9.38: an elevated absolute-height edge keeps the straight y line between its
                // ends; following the terrain under the control points made curved elevated
                // pieces sag or hump 5-9 m mid-span (bezier.b/c already carry the lerped y).
                bezier.a = start;
                bezier.d = end;
            }
            else
            {
                Curve raw = new Curve { m_Bezier = bezier, m_Length = MathUtils.Length(bezier) };
                bezier = NetUtils.AdjustPosition(raw, startAttached, false, endAttached, ref heightData).m_Bezier;
                bezier.b.y += math.lerp(startElevation, endElevation, 1f / 3f);
                bezier.c.y += math.lerp(startElevation, endElevation, 2f / 3f);
                bezier.a = start;
                bezier.d = end;
            }

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;
            course.m_Elevation = new float2(math.min(startElevation, endElevation), math.max(startElevation, endElevation));
            course.m_StartPosition = new CoursePos
            {
                m_Entity = endA.Entity,
                m_SplitPosition = endA.Split,
                m_Position = start,
                m_Elevation = startElevation,
                m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier)),
                m_CourseDelta = 0f,
                m_ParentMesh = -1,
                m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
            };
            course.m_EndPosition = new CoursePos
            {
                m_Entity = endB.Entity,
                m_SplitPosition = endB.Split,
                m_Position = end,
                m_Elevation = endElevation,
                m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier)),
                m_CourseDelta = 1f,
                m_ParentMesh = -1,
                m_Flags = CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
            };
            // Never FreeHeight on an attached end (it breaks the joined node's height).
            if (!startAttached && startElevation != 0f && !nodeA.KeepY)
            {
                course.m_StartPosition.m_Flags |= CoursePosFlags.FreeHeight;
            }
            if (!endAttached && endElevation != 0f && !nodeB.KeepY)
            {
                course.m_EndPosition.m_Flags |= CoursePosFlags.FreeHeight;
            }

            OrientForFlow(edge, ref course);
            bool owned = DefineCourseObjects(edge, course, commandBuffer, out OwnerDefinition owner);

            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            Entity entity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(entity, new CreationDefinition
            {
                m_Prefab = edge.Prefab,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.SubElevation,
            });
            commandBuffer.AddComponent(entity, default(Updated));
            if (owned)
            {
                commandBuffer.AddComponent(entity, owner);
                if (m_ToolSystem.actionMode.IsEditor())
                {
                    Game.Objects.Transform inverse = Game.Objects.ObjectUtils.InverseTransform(new Game.Objects.Transform(owner.m_Position, owner.m_Rotation));
                    commandBuffer.AddComponent(entity, new LocalCurveCache
                    {
                        m_Curve = new Bezier4x3(
                            Game.Objects.ObjectUtils.WorldToLocal(inverse, course.m_Curve.a),
                            Game.Objects.ObjectUtils.WorldToLocal(inverse, course.m_Curve.b),
                            Game.Objects.ObjectUtils.WorldToLocal(inverse, course.m_Curve.c),
                            Game.Objects.ObjectUtils.WorldToLocal(inverse, course.m_Curve.d)),
                    });
                }
            }
            commandBuffer.AddComponent(entity, course);
            if (edge.Upgrades != default(CompositionFlags))
            {
                // GenerateEdgesSystem copies the definition's Upgraded onto the new edge and
                // CompositionSelectSystem ORs it into the composition, as road upgrades do.
                commandBuffer.AddComponent(entity, new Upgraded { m_Flags = edge.Upgrades });
            }
            return EdgeDefinition.Defined;
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
                m_NodePrefabsAccepted.Add(node.CreatePrefab);
                m_Record.Nodes.Add(best);
            }
            foreach (int index in m_Current.Indices)
            {
                ImportNode node = m_Job.Nodes[index];
                if (node.Entity == Entity.Null && !m_NodePrefabsAccepted.Contains(node.CreatePrefab))
                {
                    RefuseNodePrefab(node, "a standalone-node batch was applied but produced no node entities");
                }
            }
        }

        /// <summary>
        /// A course counts as built when every sample point along it lies on a
        /// segment of the requested prefab that is new in this batch or was
        /// created earlier by this import. "created" = one segment spans exactly
        /// the course; "merged" = the game joined it with neighbouring collinear
        /// courses into a longer segment (it drops the node between two such
        /// segments), so the covering segment is longer than the course.
        /// </summary>
        private void ResolveEdges()
        {
            var candidates = new List<Entity>();
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity edge in edges)
                {
                    if (!m_EdgeSnapshot.Contains(edge) || m_RecordEdgeSet.Contains(edge))
                    {
                        candidates.Add(edge);
                    }
                }
            }
            var prefabs = new Entity[candidates.Count];
            var curves = new Bezier4x2[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                prefabs[i] = EntityManager.GetComponentData<PrefabRef>(candidates[i]).m_Prefab;
                curves[i] = EntityManager.GetComponentData<Curve>(candidates[i]).m_Bezier.xz;
            }

            foreach (int index in m_Current.Indices)
            {
                ImportEdge edge = m_Job.Edges[index];
                ImportNode nodeA = m_Job.Nodes[edge.A];
                ImportNode nodeB = m_Job.Nodes[edge.B];
                Bezier4x2 course = CourseXZ(edge, nodeA.XZ, nodeB.XZ);
                float tolerance = HalfWidth(edge.Prefab) + 0.5f;

                bool exact = false;
                int covered = 0;
                foreach (float t in kCourseSamples)
                {
                    float2 sample = MathUtils.Position(course, t);
                    int best = -1;
                    float bestDistance = tolerance;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (prefabs[i] != edge.Prefab || !InBounds(curves[i], sample, bestDistance))
                        {
                            continue;
                        }
                        float distance = MathUtils.Distance(curves[i], sample, out _);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = i;
                        }
                    }
                    if (best < 0)
                    {
                        continue;
                    }
                    covered++;
                    if (!edge.Segments.Contains(candidates[best]))
                    {
                        edge.Segments.Add(candidates[best]);
                        exact |= SameEnds(curves[best], course);
                    }
                }

                if (covered < kCourseSamples.Length && CoveredByExisting(edge, course, tolerance, out List<Entity> existing))
                {
                    // 0.9.29: the game folded the course into an older neighbour segment (and may re-split it),
                    // which keeps its entity, so it is neither new in this batch nor recorded by this import.
                    // The course is there; report it, but keep those segments out of this import's undo record.
                    edge.Segments.Clear();
                    edge.Segments.AddRange(existing);
                    edge.Status = "merged";
                    var folded = new List<string>();
                    foreach (Entity segment in existing)
                    {
                        folded.Add($"{segment.Index}:{segment.Version}");
                    }
                    edge.Error = "folded by the game into existing segment " + string.Join(", ", folded) +
                        " (built before this batch; not part of this import's undo)";
                    ResolveEndNode(nodeA, edge.Segments);
                    ResolveEndNode(nodeB, edge.Segments);
                    continue;
                }
                if (covered < kCourseSamples.Length)
                {
                    edge.Status = "failed";
                    edge.Error = DescribeOverlap(edge, course)
                        ?? $"the batch was applied but only {covered} of {kCourseSamples.Length} points along the course " +
                           "lie on a segment of this prefab";
                    edge.Segments.Clear();
                    continue;
                }
                // Also record the short pieces of a split course that no sample point hit,
                // so undo removes the whole course (seen live: 8 m dam pieces left behind).
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (prefabs[i] != edge.Prefab || edge.Segments.Contains(candidates[i]))
                    {
                        continue;
                    }
                    if (MathUtils.Distance(course, curves[i].a, out _) < tolerance
                        && MathUtils.Distance(course, curves[i].d, out _) < tolerance
                        && MathUtils.Distance(course, MathUtils.Position(curves[i], 0.5f), out _) < tolerance)
                    {
                        edge.Segments.Add(candidates[i]);
                    }
                }
                edge.Status = exact ? "created" : "merged";
                if (!exact)
                {
                    var names = new List<string>();
                    foreach (Entity segment in edge.Segments)
                    {
                        names.Add($"{segment.Index}:{segment.Version}");
                    }
                    edge.Error = "merged by the game into segment " + string.Join(", ", names) +
                        " (collinear segments of one prefab are joined and the node between them dropped)";
                }
                foreach (Entity segment in edge.Segments)
                {
                    RecordSegment(segment);
                }
                ResolveEndNode(nodeA, edge.Segments);
                ResolveEndNode(nodeB, edge.Segments);
            }
        }

        private static readonly float[] kCourseSamples = { 0.2f, 0.5f, 0.8f };

        /// <summary>Every sample point of the course lies on some live segment of the prefab (any age).</summary>
        private bool CoveredByExisting(ImportEdge edge, Bezier4x2 course, float tolerance, out List<Entity> segments)
        {
            segments = new List<Entity>();
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (float t in kCourseSamples)
                {
                    float2 sample = MathUtils.Position(course, t);
                    Entity best = Entity.Null;
                    float bestDistance = tolerance;
                    foreach (Entity e in edges)
                    {
                        if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab != edge.Prefab)
                        {
                            continue;
                        }
                        Bezier4x2 curve = EntityManager.GetComponentData<Curve>(e).m_Bezier.xz;
                        if (!InBounds(curve, sample, bestDistance))
                        {
                            continue;
                        }
                        float distance = MathUtils.Distance(curve, sample, out _);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = e;
                        }
                    }
                    if (best == Entity.Null)
                    {
                        return false;
                    }
                    if (!segments.Contains(best))
                    {
                        segments.Add(best);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Why an applied course left no segment: if it runs inside one existing
        /// segment's footprint (every sample closer to that segment's centre line than
        /// the two half-widths), the game discards it without a validation error,
        /// even with ignoreErrors (seen live with retaining-wall quays beside highways).
        /// </summary>
        private string DescribeOverlap(ImportEdge edge, Bezier4x2 course)
        {
            float ownHalf = HalfWidth(edge.Prefab);
            Entity bestSegment = Entity.Null;
            float bestWorst = float.MaxValue;
            float bestNeed = 0f;
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity candidate in edges)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;
                    if (prefab == edge.Prefab)
                    {
                        continue;
                    }
                    Bezier4x2 curve = EntityManager.GetComponentData<Curve>(candidate).m_Bezier.xz;
                    float need = HalfWidth(prefab) + ownHalf;
                    if (!InBounds(curve, MathUtils.Position(course, 0.5f), need))
                    {
                        continue;
                    }
                    float worst = 0f;
                    foreach (float t in kCourseSamples)
                    {
                        worst = math.max(worst, MathUtils.Distance(curve, MathUtils.Position(course, t), out _));
                    }
                    if (worst < need && worst < bestWorst)
                    {
                        bestWorst = worst;
                        bestSegment = candidate;
                        bestNeed = need;
                    }
                }
            }
            if (bestSegment == Entity.Null)
            {
                return null;
            }
            string name = PrefabName(EntityManager.GetComponentData<PrefabRef>(bestSegment).m_Prefab);
            return $"dropped by the game: the course runs inside the existing '{name}' segment {bestSegment.Index}:{bestSegment.Version} " +
                $"(centre lines up to {bestWorst:F1} m apart, the two half-widths need {bestNeed:F1} m); " +
                $"move it at least {bestNeed - bestWorst + 0.5f:F1} m sideways, or leave it out";
        }

        private static bool SameEnds(Bezier4x2 segment, Bezier4x2 course)
        {
            return (math.distance(segment.a, course.a) < kCourseMatchDistance && math.distance(segment.d, course.d) < kCourseMatchDistance)
                || (math.distance(segment.a, course.d) < kCourseMatchDistance && math.distance(segment.d, course.a) < kCourseMatchDistance);
        }

        private float HalfWidth(Entity prefab)
        {
            if (!m_HalfWidths.TryGetValue(prefab, out float half))
            {
                half = EntityManager.HasComponent<NetGeometryData>(prefab)
                    ? EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth * 0.5f
                    : 2f;
                half = math.max(half, 1f);
                m_HalfWidths[prefab] = half;
            }
            return half;
        }

        private void RecordSegment(Entity segment)
        {
            if (m_RecordEdgeSet.Add(segment))
            {
                m_Record.Edges.Add(segment);
            }
        }

        private void ResolveEndNode(ImportNode node, List<Entity> segments)
        {
            if (node.Entity != Entity.Null && EntityManager.Exists(node.Entity) && !EntityManager.HasComponent<Deleted>(node.Entity))
            {
                return;
            }
            Entity best = Entity.Null;
            float bestDistance = kNodeMatchDistance;
            bool onSegment = false;
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
                onSegment |= MathUtils.Distance(EntityManager.GetComponentData<Curve>(segment).m_Bezier.xz, node.XZ, out _) < 1f;
            }
            if (best != Entity.Null)
            {
                if (!m_NodeSnapshot.Contains(best))
                {
                    m_Record.Nodes.Add(best);
                    node.CreatedHere = true;
                }
                node.Entity = best;
                node.Absorbed = false;
                return;
            }
            if (onSegment)
            {
                // The node lies inside a merged segment; later edges split that segment there.
                node.Entity = Entity.Null;
                node.Absorbed = true;
                return;
            }
            m_Job.Warnings.Add($"node {node.Id}: no node entity or imported segment at its position after building an edge; " +
                "later edges will start a new node there");
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

        /// <summary>
        /// Deletes the Game.Objects.Pillar sub-objects owned by these segments and by the
        /// nodes that join only these segments (inner nodes of a split course). The game
        /// does not recreate them until SubObjectSystem sees the owner Updated again.
        /// </summary>
        public int RemovePillars(List<Entity> segments, out int innerNodes)
        {
            var set = new HashSet<Entity>(segments);
            var owners = new List<Entity>();
            var nodeSeen = new HashSet<Entity>();
            innerNodes = 0;
            foreach (Entity segment in segments)
            {
                if (!EntityManager.Exists(segment) || !EntityManager.HasComponent<Edge>(segment))
                {
                    continue;
                }
                owners.Add(segment);
                Edge edge = EntityManager.GetComponentData<Edge>(segment);
                foreach (Entity node in new[] { edge.m_Start, edge.m_End })
                {
                    if (!nodeSeen.Add(node) || !EntityManager.HasBuffer<ConnectedEdge>(node))
                    {
                        continue;
                    }
                    bool inner = true;
                    foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    {
                        inner &= set.Contains(connected.m_Edge);
                    }
                    if (inner)
                    {
                        owners.Add(node);
                        innerNodes++;
                    }
                }
            }
            var pillars = new List<Entity>();
            foreach (Entity owner in owners)
            {
                if (!EntityManager.HasBuffer<Game.Objects.SubObject>(owner))
                {
                    continue;
                }
                foreach (Game.Objects.SubObject sub in EntityManager.GetBuffer<Game.Objects.SubObject>(owner, true))
                {
                    if (EntityManager.Exists(sub.m_SubObject) && EntityManager.HasComponent<Game.Objects.Pillar>(sub.m_SubObject)
                        && !EntityManager.HasComponent<Deleted>(sub.m_SubObject))
                    {
                        pillars.Add(sub.m_SubObject);
                    }
                }
            }
            foreach (Entity pillar in pillars)
            {
                EntityManager.AddComponent<Deleted>(pillar);
            }
            return pillars.Count;
        }

        /// <summary>
        /// 0.9.39, edges with elevated:true. CompositionSelectSystem takes the edge flags from
        /// NetCompositionHelpers.GetElevationFlags: per side, max(min elevation at start, at end,
        /// middle elevation) >= 2 x NetGeometryData.m_ElevationLimit (4 m for every road, so 8 m)
        /// gives General.Elevated (deck on pillars); >= 1 x limit gives Side.Raised (retaining
        /// walls); below that the ground composition, with FlattenTerrain raising the terrain to
        /// the road (earth embankment). SubObjectSystem places RequireElevated pillars with the same
        /// stored Elevation: limit x (1, 2) + max(0, elevationRange.min). A CS1 elevated ramp 5 m up
        /// is therefore Raised in CS2. Raising the stored Elevation of the segments (middle) and of
        /// the inner nodes to that threshold and marking them Updated gives the CS1 look without
        /// moving the geometry. End nodes shared with other edges are left alone; an edge whose
        /// middle is elevated needs only limit x 1 at its nodes for edge-placed pillars.
        /// </summary>
        private void ForceElevatedEdges()
        {
            int edges = 0, segmentsRaised = 0, nodesRaised = 0;
            foreach (ImportEdge edge in m_Job.Edges)
            {
                if (!edge.ForceElevated || edge.Segments.Count == 0 || !EntityManager.HasComponent<NetGeometryData>(edge.Prefab))
                {
                    continue;
                }
                NetGeometryData geometry = EntityManager.GetComponentData<NetGeometryData>(edge.Prefab);
                float rangeMin = EntityManager.HasComponent<PlaceableNetData>(edge.Prefab)
                    ? math.max(0f, EntityManager.GetComponentData<PlaceableNetData>(edge.Prefab).m_ElevationRange.min)
                    : 0f;
                float threshold = geometry.m_ElevationLimit * 2f + rangeMin + 0.05f;
                var set = new HashSet<Entity>(edge.Segments);
                var nodes = new HashSet<Entity>();
                edges++;
                foreach (Entity segment in edge.Segments)
                {
                    if (!EntityManager.Exists(segment) || !EntityManager.HasComponent<Edge>(segment) || EntityManager.HasComponent<Deleted>(segment))
                    {
                        continue;
                    }
                    if (RaiseElevation(segment, threshold))
                    {
                        segmentsRaised++;
                    }
                    Edge e = EntityManager.GetComponentData<Edge>(segment);
                    nodes.Add(e.m_Start);
                    nodes.Add(e.m_End);
                    EntityManager.AddComponent<Updated>(segment);
                }
                foreach (Entity node in nodes)
                {
                    if (!EntityManager.Exists(node) || !EntityManager.HasBuffer<ConnectedEdge>(node))
                    {
                        continue;
                    }
                    bool inner = true;
                    foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    {
                        inner &= set.Contains(connected.m_Edge);
                    }
                    if (inner && RaiseElevation(node, threshold))
                    {
                        nodesRaised++;
                    }
                    EntityManager.AddComponent<Updated>(node);
                }
            }
            if (edges > 0)
            {
                m_Job.Warnings.Add($"elevated: {edges} edge(s) forced to the Elevated composition ({segmentsRaised} segment(s) and {nodesRaised} inner node(s) had their stored Elevation raised to 2 x the prefab's elevation limit; geometry unchanged). A later net-tool edit of those segments recomputes the elevation from the terrain.");
            }
        }

        private bool RaiseElevation(Entity entity, float threshold)
        {
            if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
            {
                Game.Net.Elevation elevation = EntityManager.GetComponentData<Game.Net.Elevation>(entity);
                if (math.all(elevation.m_Elevation >= threshold))
                {
                    return false;
                }
                elevation.m_Elevation = math.max(elevation.m_Elevation, threshold);
                EntityManager.SetComponentData(entity, elevation);
                return true;
            }
            EntityManager.AddComponentData(entity, new Game.Net.Elevation(new float2(threshold)));
            return true;
        }

        private void Finish(string abortReason)
        {
            BridgeResponse response;
            if (m_Job != null)
            {
                var noPillarSegments = new List<Entity>();
                int noPillarEdges = 0;
                foreach (ImportEdge edge in m_Job.Edges)
                {
                    if (edge.NoPillars && edge.Segments.Count > 0)
                    {
                        noPillarEdges++;
                        noPillarSegments.AddRange(edge.Segments);
                    }
                }
                ForceElevatedEdges();
                if (noPillarSegments.Count > 0)
                {
                    int removed = RemovePillars(noPillarSegments, out int nodes);
                    m_Job.Warnings.Add($"noPillars: deleted {removed} pillar object(s) on {noPillarEdges} edge(s) ({noPillarSegments.Count} segments, {nodes} inner nodes); they come back when the game updates those segments (an edge or node touched later)");
                }
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
            RestoreIgnoreErrors();
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

        /// <summary>Per-edge outcome of the last finished import, for GET /import/report.</summary>
        public sealed class EdgeResult
        {
            public string id;
            public string status;
            public string error;
            public string prefab;
            public List<object> segments;
            public object nodeA;
            public object nodeB;
        }

        private List<EdgeResult> m_LastResults = new List<EdgeResult>();
        private string m_LastImportId;

        /// <summary>A page of the last import's per-edge results, filtered by status and/or prefab name.</summary>
        public object QueryResults(string status, string prefab, int offset, int limit)
        {
            var matches = new List<EdgeResult>();
            foreach (EdgeResult result in m_LastResults)
            {
                if (!string.IsNullOrEmpty(status) && !string.Equals(result.status, status, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(prefab) && (result.prefab == null
                    || result.prefab.IndexOf(prefab, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                matches.Add(result);
            }
            offset = math.clamp(offset, 0, matches.Count);
            int count = math.min(limit, matches.Count - offset);
            return new
            {
                importId = m_LastImportId,
                totalMatches = matches.Count,
                offset,
                returned = count,
                results = matches.GetRange(offset, count),
            };
        }

        private const int kReportListCap = 200;

        private void RestoreIgnoreErrors()
        {
            if (m_RestoreIgnoreErrors)
            {
                m_ToolSystem.ignoreErrors = m_IgnoreErrorsBefore;
                m_RestoreIgnoreErrors = false;
            }
        }

        [Preserve]
        protected override void OnDestroy()
        {
            RestoreIgnoreErrors();
            base.OnDestroy();
        }

        private object BuildImportReport(string abortReason)
        {
            var results = new List<EdgeResult>(m_Job.Edges.Count);
            var counts = new Dictionary<string, int> { ["created"] = 0, ["merged"] = 0, ["failed"] = 0, ["pending"] = 0 };
            var byPrefab = new Dictionary<string, Dictionary<string, int>>();
            var failedEdges = new List<object>();
            foreach (ImportEdge edge in m_Job.Edges)
            {
                counts[edge.Status] = counts.TryGetValue(edge.Status, out int n) ? n + 1 : 1;
                if (!byPrefab.TryGetValue(edge.PrefabName, out Dictionary<string, int> perPrefab))
                {
                    perPrefab = new Dictionary<string, int>();
                    byPrefab[edge.PrefabName] = perPrefab;
                }
                perPrefab[edge.Status] = perPrefab.TryGetValue(edge.Status, out int m) ? m + 1 : 1;
                if (edge.Status == "failed" && failedEdges.Count < kReportListCap)
                {
                    failedEdges.Add(new { id = edge.Id, prefab = edge.PrefabName, error = edge.Error });
                }

                ImportNode a = m_Job.Nodes[edge.A];
                ImportNode b = m_Job.Nodes[edge.B];
                var segments = new List<object>();
                foreach (Entity segment in edge.Segments)
                {
                    segments.Add(new { index = segment.Index, version = segment.Version });
                }
                results.Add(new EdgeResult
                {
                    id = edge.Id,
                    status = edge.Status,
                    error = edge.Error,
                    prefab = edge.PrefabName,
                    segments = segments,
                    nodeA = a.Entity != Entity.Null ? new { index = a.Entity.Index, version = a.Entity.Version } : null,
                    nodeB = b.Entity != Entity.Null ? new { index = b.Entity.Index, version = b.Entity.Version } : null,
                });
            }
            m_LastResults = results;
            m_LastImportId = m_Job.Id;

            var unresolved = new List<object>();
            int unresolvedCount = 0;
            int nodesResolved = 0;
            int absorbed = 0;
            foreach (ImportNode node in m_Job.Nodes)
            {
                if (node.CreatePrefab == Entity.Null)
                {
                    continue; // not used by any edge
                }
                if (node.Entity != Entity.Null)
                {
                    nodesResolved++;
                }
                else if (node.Absorbed)
                {
                    absorbed++;
                }
                else
                {
                    unresolvedCount++;
                    if (unresolved.Count < kReportListCap)
                    {
                        unresolved.Add(new { id = node.Id, error = node.Error ?? "no node entity (no built edge reaches it)" });
                    }
                }
            }

            var refused = new List<string>(m_NodePrefabsRefused.Values);

            var snaps = new List<object>();
            int snapRequested = 0;
            int snapped = 0;
            int snapFellBack = 0;
            foreach (ImportNode node in m_Job.Nodes)
            {
                if (node.SnapRadius <= 0f)
                {
                    continue;
                }
                snapRequested++;
                object entry = null;
                if (node.SnapSegment != Entity.Null)
                {
                    snapped++;
                    entry = new
                    {
                        id = node.Id,
                        splitSegment = $"{node.SnapSegment.Index}:{node.SnapSegment.Version}",
                        distance = Math.Round(node.SnapDistance, 2),
                        t = Math.Round(node.SnapT, 3),
                        joinedAtEnd = node.SnapT < 0.02f || node.SnapT > 0.98f,
                        node = node.Entity != Entity.Null ? $"{node.Entity.Index}:{node.Entity.Version}" : null,
                        // 0.9.31: where the junction node actually ended up (the game can move or merge it after the split).
                        driftAfterBuild = node.Entity != Entity.Null && EntityManager.Exists(node.Entity) && EntityManager.HasComponent<Node>(node.Entity)
                            ? Math.Round(math.distance(EntityManager.GetComponentData<Node>(node.Entity).m_Position.xz, node.OriginalXZ), 2)
                            : (double?)null,
                        nodeDegree = node.Entity != Entity.Null && EntityManager.Exists(node.Entity) && EntityManager.HasBuffer<ConnectedEdge>(node.Entity)
                            ? EntityManager.GetBuffer<ConnectedEdge>(node.Entity, true).Length
                            : (int?)null,
                    };
                }
                else if (node.SnapTried)
                {
                    snapFellBack++;
                    entry = new { id = node.Id, result = "no matching segment within radius; built as a free node" };
                }
                if (entry != null && snaps.Count < kReportListCap)
                {
                    snaps.Add(entry);
                }
            }
            return new
            {
                importId = m_Job.Id,
                name = m_Job.Name,
                completed = abortReason == null,
                abortReason,
                edges = new
                {
                    requested = m_Job.Edges.Count,
                    created = counts["created"],
                    merged = counts["merged"],
                    failed = counts["failed"],
                    notRun = counts["pending"],
                },
                byPrefab,
                nodes = new
                {
                    used = nodesResolved + absorbed + unresolvedCount,
                    resolved = nodesResolved,
                    absorbedIntoMergedSegments = absorbed,
                    unresolved = unresolvedCount,
                    standaloneCreated = m_NodeSuccesses,
                },
                nodesFirst = new { enabled = m_Job.NodesFirst, refusedPrefabs = refused },
                snapToSegment = new { requested = snapRequested, snapped, fellBack = snapFellBack, details = snaps },
                heights = HeightCheck(),
                batches = new { applied = m_BatchesApplied, rejected = m_BatchesRejected },
                validation = new
                {
                    bypassValidation = m_Job.BypassValidation,
                    ignoreErrors = m_IgnoreErrorsAtStart,
                    anarchyDetected = m_AnarchyDetected,
                    anarchyEnabled = m_AnarchyEnabledAtStart,
                    anarchyEnabledAtEnd = AnarchyInterop.ToggleState(),
                    note = m_Job.BypassValidation
                        ? "bypassValidation: ToolSystem.ignoreErrors was on during the import and restored afterwards"
                        : m_AnarchyDetected && m_AnarchyEnabledAtStart != true
                            ? "Anarchy is loaded but its toggle was off: the game's validation applied in full"
                            : null,
                },
                frames = m_Frames,
                seconds = Math.Round((DateTime.UtcNow - m_Started).TotalSeconds, 1),
                undo = new
                {
                    segmentsRecorded = m_Record != null ? m_Record.Edges.Count : 0,
                    nodesRecorded = m_Record != null ? m_Record.Nodes.Count : 0,
                    note = "POST /import/undo removes what this import created (in memory only; lost when the game restarts)",
                },
                warningCount = m_Job.Warnings.Count,
                warnings = m_Job.Warnings.Count > kReportListCap ? m_Job.Warnings.GetRange(0, kReportListCap) : m_Job.Warnings,
                failedEdges,
                unresolvedNodes = unresolved,
                note = "'merged' edges exist: the game joined collinear courses into one longer segment (listed per edge). " +
                    "Lists are capped at 200; page through every per-edge result with GET /import/report?status=&prefab=&offset=&limit=.",
                results = m_Job.FullReport ? results : null,
            };
        }

        /// <summary>
        /// 0.9.38: requested vs live y per absolute-height edge, at both ends and mid-span
        /// (the live point nearest in plan to the requested point, over the edge's segments).
        /// </summary>
        private object HeightCheck()
        {
            int checkedEdges = 0, off = 0;
            double worst = 0;
            var list = new List<(double dev, object entry)>();
            foreach (ImportEdge edge in m_Job.Edges)
            {
                ImportNode a = m_Job.Nodes[edge.A];
                ImportNode b = m_Job.Nodes[edge.B];
                if (!a.AbsoluteY.HasValue || !b.AbsoluteY.HasValue || edge.Segments.Count == 0)
                {
                    continue;
                }
                var curves = new List<Bezier4x3>();
                foreach (Entity segment in edge.Segments)
                {
                    if (EntityManager.Exists(segment) && EntityManager.HasComponent<Curve>(segment))
                    {
                        curves.Add(EntityManager.GetComponentData<Curve>(segment).m_Bezier);
                    }
                }
                if (curves.Count == 0)
                {
                    continue;
                }
                float2 midXZ = edge.Control != null && edge.Control.Length == 2
                    ? MathUtils.Position(new Bezier4x2(a.XZ, edge.Control[0], edge.Control[1], b.XZ), 0.5f)
                    : edge.Control != null && edge.Control.Length == 1
                        ? 0.25f * a.XZ + 0.5f * edge.Control[0] + 0.25f * b.XZ
                        : (a.XZ + b.XZ) * 0.5f;
                float ya = a.AbsoluteY.Value, yb = b.AbsoluteY.Value, ym = (ya + yb) * 0.5f;
                float la = LiveY(curves, a.XZ, out float da);
                float lb = LiveY(curves, b.XZ, out float db);
                float lm = LiveY(curves, midXZ, out float dm);
                double dev = math.max(math.abs(la - ya), math.max(math.abs(lb - yb), math.abs(lm - ym)));
                checkedEdges++;
                if (dev > 1.0)
                {
                    off++;
                }
                worst = math.max(worst, dev);
                list.Add((dev, new
                {
                    edge = edge.Id,
                    prefab = edge.PrefabName,
                    requested = new[] { Math.Round(ya, 2), Math.Round(ym, 2), Math.Round(yb, 2) },
                    live = new[] { Math.Round(la, 2), Math.Round(lm, 2), Math.Round(lb, 2) },
                    maxDeviation = Math.Round(dev, 2),
                    planDistance = Math.Round(math.max(da, math.max(db, dm)), 2),
                }));
            }
            list.Sort((x, y) => y.dev.CompareTo(x.dev));
            var worstList = new List<object>();
            foreach ((double dev, object entry) in list)
            {
                if (dev <= 1.0 || worstList.Count >= kReportListCap)
                {
                    break;
                }
                worstList.Add(entry);
            }
            return new
            {
                checkedEdges,
                offMoreThan1m = off,
                worstDeviation = Math.Round(worst, 2),
                worst = worstList,
                note = "absolute-height edges only: requested y at a / mid / b (mid = mean of the ends) vs the live segment y nearest in plan; a ground-level edge follows the terrain mid-span by design",
            };
        }

        private static float LiveY(List<Bezier4x3> curves, float2 xz, out float planDistance)
        {
            planDistance = float.MaxValue;
            float y = 0f;
            foreach (Bezier4x3 curve in curves)
            {
                for (int i = 0; i <= 32; i++)
                {
                    float3 p = MathUtils.Position(curve, i / 32f);
                    float d = math.distance(p.xz, xz);
                    if (d < planDistance)
                    {
                        planDistance = d;
                        y = p.y;
                    }
                }
            }
            return y;
        }
    }
}
