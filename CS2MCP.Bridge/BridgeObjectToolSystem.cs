using System;
using System.Collections.Generic;
using Game;
using Game.City;
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
using AgeMask = Game.Tools.AgeMask;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// Headless batch placement of objects (trees, props, buildings, outside
    /// connections) for POST /import/objects and its undo. Same handshake as the
    /// network importer: Warmup, then per batch Define (applyMode Clear, one
    /// ported LineTool definition per object, so buildings get their sub-objects,
    /// placeholders their variant and trees their age) → Settle (None) → Apply
    /// (GetAllowApply, else bisect) → Resolve. The game turns each Temp object
    /// into the permanent one (ApplyObjectsSystem.Create removes Temp), so new
    /// objects are found among this batch's Temp entities before the apply,
    /// never by scanning every object on the map.
    /// </summary>
    public sealed partial class BridgeObjectToolSystem : ObjectToolBaseSystem
    {
        public sealed class ImportObject
        {
            public string Id;
            public string PrefabName;
            public Entity Prefab;
            public string Kind;
            public float3 Position;
            public float Elevation;
            public quaternion Rotation;
            public AgeMask Age;
            /// <summary>Net node an outside connection is attached to (CoursePos-like parent of the object).</summary>
            public Entity AttachTo;
            /// <summary>Surface (area) polygon in world x/z; Position holds its centroid. Null for objects.</summary>
            public float2[] Polygon;
            public string Status = "pending";
            public string Error;
            public Entity Entity;
        }

        public sealed class ObjectJob
        {
            public string Id;
            public string Name;
            public List<ImportObject> Objects;
            public int BatchSize;
            public int SettleFrames;
            public bool FullReport;
            public bool BypassValidation;
            public List<string> Warnings = new List<string>();
        }

        public sealed class ObjectRecord
        {
            public string Id;
            public string Name;
            public List<Entity> Entities = new List<Entity>();
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

        private sealed class WorkItem
        {
            public bool Delete;
            public List<int> Indices;
        }

        private const int kMaxRecords = 50;
        private const int kWarmupFrames = 3;
        private const int kDeleteBatch = 2000;
        private const int kReportListCap = 200;
        private const float kMatchDistance = 1.5f;

        private Stage m_Stage = Stage.Idle;
        private readonly List<WorkItem> m_Queue = new List<WorkItem>();
        private WorkItem m_Current;
        private int m_SettleLeft;
        private int m_WarmupLeft;
        private ObjectJob m_Job;
        private ObjectRecord m_Record;
        private ObjectRecord m_UndoTarget;
        private List<Entity> m_DeleteEntities;
        private int m_Deleted;
        private BridgeRequest m_PendingRequest;
        private ToolBaseSystem m_PreviousTool;
        private readonly Dictionary<int, Entity> m_Matched = new Dictionary<int, Entity>();
        private int m_BatchesApplied;
        private int m_BatchesRejected;
        private int m_Frames;
        private DateTime m_Started;
        private bool m_IgnoreErrorsAtStart;
        private bool m_RestoreIgnoreErrors;
        private bool m_IgnoreErrorsBefore;
        private bool m_AnarchyDetected;
        private bool? m_AnarchyEnabledAtStart;
        private bool m_AnarchyRegistered;

        private readonly List<ObjectRecord> m_Records = new List<ObjectRecord>();
        private object m_LastReport;

        private CityConfigurationSystem m_CityConfigurationSystem;
        private EntityQuery m_TempObjectQuery;
        private EntityQuery m_TempAreaQuery;
        private EntityQuery m_IconQuery;

        public override string toolID => "CS2MCP.Objects";

        public bool IsBusy => m_Stage != Stage.Idle;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_TempObjectQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_TempAreaQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<Temp>(),
                },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_GuardEdgeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Curve>(), ComponentType.ReadOnly<PrefabRef>() },
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
            return false;
        }

        public object Status()
        {
            if (m_Stage == Stage.Idle)
            {
                var records = new List<object>();
                foreach (ObjectRecord record in m_Records)
                {
                    records.Add(new { id = record.Id, name = record.Name, objects = record.Entities.Count });
                }
                return new { running = false, undoable = records, last = m_LastReport };
            }
            if (m_Job == null)
            {
                return new { running = true, operation = "undo", importId = m_UndoTarget?.Id, deleted = m_Deleted };
            }
            int placed = 0;
            int failed = 0;
            foreach (ImportObject obj in m_Job.Objects)
            {
                if (obj.Status == "created")
                {
                    placed++;
                }
                else if (obj.Status == "failed")
                {
                    failed++;
                }
            }
            return new
            {
                running = true,
                operation = "import",
                importId = m_Job.Id,
                total = m_Job.Objects.Count,
                created = placed,
                failed,
                batchesQueued = m_Queue.Count,
                frames = m_Frames,
            };
        }

        private EntityQuery m_GuardEdgeQuery;
        private NetGuard m_NetGuard;
        private int m_Protected;

        public bool TryStartImport(ObjectJob job, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_Job = job;
            m_NetGuard = new NetGuard(EntityManager, m_GuardEdgeQuery);
            m_Protected = 0;
            m_Record = new ObjectRecord { Id = job.Id, Name = job.Name };
            m_UndoTarget = null;
            m_Queue.Clear();
            for (int start = 0; start < job.Objects.Count; start += job.BatchSize)
            {
                var indices = new List<int>();
                for (int i = start; i < math.min(start + job.BatchSize, job.Objects.Count); i++)
                {
                    // Road/rail outside connections are resolved by the handler, not placed.
                    if (job.Objects[i].Status == "pending")
                    {
                        indices.Add(i);
                    }
                }
                if (indices.Count > 0)
                {
                    m_Queue.Add(new WorkItem { Indices = indices });
                }
            }
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Undo by import id or name; the newest import when both are empty.</summary>
        public bool TryStartUndo(string idOrName, BridgeRequest request, out string error)
        {
            error = null;
            if (m_Stage != Stage.Idle)
            {
                error = "an object import or undo is running";
                return false;
            }
            ObjectRecord target = null;
            for (int i = m_Records.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(idOrName) || m_Records[i].Id == idOrName || m_Records[i].Name == idOrName)
                {
                    target = m_Records[i];
                    break;
                }
            }
            if (target == null)
            {
                error = m_Records.Count == 0
                    ? "nothing to undo: no object import recorded since the game started (records are kept in memory only)"
                    : $"no object import with id or name '{idOrName}'";
                return false;
            }
            m_DeleteEntities = new List<Entity>();
            foreach (Entity entity in target.Entities)
            {
                if (EntityManager.Exists(entity) && !EntityManager.HasComponent<Deleted>(entity)
                    && (EntityManager.HasComponent<Transform>(entity) || EntityManager.HasBuffer<Game.Areas.Node>(entity)))
                {
                    m_DeleteEntities.Add(entity);
                }
            }
            m_Job = null;
            m_Record = null;
            m_UndoTarget = target;
            m_Deleted = 0;
            m_Queue.Clear();
            for (int start = 0; start < m_DeleteEntities.Count; start += kDeleteBatch)
            {
                var indices = new List<int>();
                for (int i = start; i < math.min(start + kDeleteBatch, m_DeleteEntities.Count); i++)
                {
                    indices.Add(i);
                }
                m_Queue.Add(new WorkItem { Delete = true, Indices = indices });
            }
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void Activate()
        {
            m_Current = null;
            m_BatchesApplied = 0;
            m_BatchesRejected = 0;
            m_Frames = 0;
            m_Started = DateTime.UtcNow;
            m_IgnoreErrorsAtStart = m_ToolSystem.ignoreErrors;
            m_AnarchyDetected = AnarchyInterop.Detected;
            m_AnarchyEnabledAtStart = AnarchyInterop.ToggleState();
            if (!m_AnarchyRegistered && m_AnarchyDetected)
            {
                m_AnarchyRegistered = AnarchyInterop.RegisterTool(this);
            }
            if (m_Job != null && m_Job.BypassValidation)
            {
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
                Mod.Log.Warn($"BridgeObjectToolSystem error in stage {m_Stage}: {e}");
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
                m_PreviousTool = null;
                Finish("interrupted because another tool became active (the user picked a tool?)");
            }
            base.OnStopRunning();
        }

        [Preserve]
        protected override void OnDestroy()
        {
            RestoreIgnoreErrors();
            base.OnDestroy();
        }

        private void StageDefine()
        {
            if (m_Queue.Count == 0)
            {
                applyMode = ApplyMode.None;
                Finish(null);
                return;
            }
            m_Current = m_Queue[0];
            m_Queue.RemoveAt(0);
            if (m_Current.Delete)
            {
                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                foreach (int index in m_Current.Indices)
                {
                    DefineDelete(m_DeleteEntities[index], commandBuffer);
                }
            }
            else
            {
                CreateDefinitions definitions = NewDefinitionsTemplate();
                foreach (int index in m_Current.Indices)
                {
                    ImportObject obj = m_Job.Objects[index];
                    if (obj.Polygon != null)
                    {
                        DefineArea(obj, definitions.m_CommandBuffer);
                        continue;
                    }
                    definitions.m_ObjectPrefab = obj.Prefab;
                    definitions.m_AgeMask = obj.Age;
                    definitions.m_RandomSeed = RandomSeed.Next();
                    definitions.m_ControlPoint = new ControlPoint
                    {
                        m_Position = obj.Position,
                        m_HitPosition = obj.Position,
                        m_Rotation = obj.Rotation,
                        m_OriginalEntity = obj.AttachTo,
                        m_Elevation = obj.Elevation,
                        m_ElementIndex = new int2(-1, -1),
                    };
                    definitions.Execute();
                }
            }
            applyMode = ApplyMode.Clear;
            m_SettleLeft = m_Job != null ? m_Job.SettleFrames : 1;
            m_Stage = Stage.Settle;
        }

        private CreateDefinitions NewDefinitionsTemplate()
        {
            CreateDefinitions definitions = default;
            definitions.m_RandomizationEnabled = false;
            definitions.m_FixedRandomSeed = 0;
            definitions.m_EditorMode = m_ToolSystem.actionMode.IsEditor();
            definitions.m_LefthandTraffic = m_CityConfigurationSystem.leftHandTraffic;
            definitions.m_Theme = m_CityConfigurationSystem.defaultTheme;
            definitions.m_AgeMask = AgeMask.Mature;
            // The requested age in the editor too: one AgeMask bit gives one fixed age
            // (ToolUtils.GetRandomAge: 0, 0.175, 0.425, 0.775), never a dead tree.
            definitions.m_FixedAge = true;
            definitions.m_AttachmentPrefab = default;
            definitions.m_OwnerData = GetComponentLookup<Owner>(true);
            definitions.m_TransformData = GetComponentLookup<Transform>(true);
            definitions.m_AttachedData = GetComponentLookup<Game.Objects.Attached>(true);
            definitions.m_LocalTransformCacheData = GetComponentLookup<LocalTransformCache>(true);
            definitions.m_ElevationData = GetComponentLookup<Game.Objects.Elevation>(true);
            definitions.m_BuildingData = GetComponentLookup<Game.Buildings.Building>(true);
            definitions.m_LotData = GetComponentLookup<Game.Buildings.Lot>(true);
            definitions.m_EdgeData = GetComponentLookup<Edge>(true);
            definitions.m_NodeData = GetComponentLookup<Node>(true);
            definitions.m_CurveData = GetComponentLookup<Curve>(true);
            definitions.m_NetElevationData = GetComponentLookup<Game.Net.Elevation>(true);
            definitions.m_OrphanData = GetComponentLookup<Orphan>(true);
            definitions.m_UpgradedData = GetComponentLookup<Upgraded>(true);
            definitions.m_CompositionData = GetComponentLookup<Composition>(true);
            definitions.m_AreaClearData = GetComponentLookup<Game.Areas.Clear>(true);
            definitions.m_AreaSpaceData = GetComponentLookup<Game.Areas.Space>(true);
            definitions.m_AreaLotData = GetComponentLookup<Game.Areas.Lot>(true);
            definitions.m_EditorContainerData = GetComponentLookup<Game.Tools.EditorContainer>(true);
            definitions.m_PrefabRefData = GetComponentLookup<PrefabRef>(true);
            definitions.m_PrefabNetObjectData = GetComponentLookup<NetObjectData>(true);
            definitions.m_PrefabBuildingData = GetComponentLookup<BuildingData>(true);
            definitions.m_PrefabAssetStampData = GetComponentLookup<AssetStampData>(true);
            definitions.m_PrefabBuildingExtensionData = GetComponentLookup<BuildingExtensionData>(true);
            definitions.m_PrefabSpawnableObjectData = GetComponentLookup<SpawnableObjectData>(true);
            definitions.m_PrefabObjectGeometryData = GetComponentLookup<ObjectGeometryData>(true);
            definitions.m_PrefabPlaceableObjectData = GetComponentLookup<PlaceableObjectData>(true);
            definitions.m_PrefabAreaGeometryData = GetComponentLookup<AreaGeometryData>(true);
            definitions.m_PrefabBuildingTerraformData = GetComponentLookup<BuildingTerraformData>(true);
            definitions.m_PrefabCreatureSpawnData = GetComponentLookup<CreatureSpawnData>(true);
            definitions.m_PlaceholderBuildingData = GetComponentLookup<PlaceholderBuildingData>(true);
            definitions.m_PrefabNetGeometryData = GetComponentLookup<NetGeometryData>(true);
            definitions.m_PrefabCompositionData = GetComponentLookup<NetCompositionData>(true);
            definitions.m_SubObjects = GetBufferLookup<Game.Objects.SubObject>(true);
            definitions.m_CachedNodes = GetBufferLookup<LocalNodeCache>(true);
            definitions.m_InstalledUpgrades = GetBufferLookup<Game.Buildings.InstalledUpgrade>(true);
            definitions.m_SubNets = GetBufferLookup<Game.Net.SubNet>(true);
            definitions.m_ConnectedEdges = GetBufferLookup<ConnectedEdge>(true);
            definitions.m_SubAreas = GetBufferLookup<Game.Areas.SubArea>(true);
            definitions.m_AreaNodes = GetBufferLookup<Game.Areas.Node>(true);
            definitions.m_AreaTriangles = GetBufferLookup<Game.Areas.Triangle>(true);
            definitions.m_PrefabSubObjects = GetBufferLookup<Game.Prefabs.SubObject>(true);
            definitions.m_PrefabSubNets = GetBufferLookup<Game.Prefabs.SubNet>(true);
            definitions.m_PrefabSubLanes = GetBufferLookup<Game.Prefabs.SubLane>(true);
            definitions.m_PrefabSubAreas = GetBufferLookup<Game.Prefabs.SubArea>(true);
            definitions.m_PrefabSubAreaNodes = GetBufferLookup<SubAreaNode>(true);
            definitions.m_PrefabPlaceholderElements = GetBufferLookup<PlaceholderObjectElement>(true);
            definitions.m_PrefabRequirementElements = GetBufferLookup<ObjectRequirementElement>(true);
            definitions.m_PrefabServiceUpgradeBuilding = GetBufferLookup<ServiceUpgradeBuilding>(true);
            definitions.m_WaterSurfaceData = m_WaterSystem.GetSurfaceData(out JobHandle waterDeps);
            waterDeps.Complete();
            definitions.m_TerrainHeightData = m_TerrainSystem.GetHeightData();
            definitions.m_CommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            return definitions;
        }

        /// <summary>
        /// Area definition for a surface, as AreaToolSystem creates (and as the bridge's
        /// district tool does): CreationDefinition + node buffer; elevation
        /// float.MinValue lets the game drape each node on the terrain.
        /// </summary>
        private void DefineArea(ImportObject obj, EntityCommandBuffer commandBuffer)
        {
            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            Entity e = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(e, new CreationDefinition { m_Prefab = obj.Prefab, m_RandomSeed = random.NextInt() });
            commandBuffer.AddComponent(e, default(Updated));
            DynamicBuffer<Game.Areas.Node> nodes = commandBuffer.AddBuffer<Game.Areas.Node>(e);
            foreach (float2 point in obj.Polygon)
            {
                nodes.Add(new Game.Areas.Node(new float3(point.x, 0f, point.y), float.MinValue));
            }
            // 0.9.26: close the ring. GenerateAreasSystem marks a new area Complete only when its
            // node buffer has >= 4 nodes and the last equals the first (as AreaToolSystem and its
            // GenerateAreas helper write them). An incomplete area is validated with noErrors, so
            // what it overlaps gets the Warning component instead of Error, and on Apply
            // ToolApplySystem adds Deleted to every Warning entity: with bypassValidation every
            // imported surface deleted the net segments and objects under it (A-v4: 309 segments).
            float2 first = obj.Polygon[0];
            nodes.Add(new Game.Areas.Node(new float3(first.x, 0f, first.y), float.MinValue));
        }

        /// <summary>Bulldoze definition for a top-level object or area, as BulldozeToolSystem.AddEntity.</summary>
        private void DefineDelete(Entity target, EntityCommandBuffer commandBuffer)
        {
            if (!EntityManager.Exists(target) || EntityManager.HasComponent<Deleted>(target))
            {
                return;
            }
            if (!EntityManager.HasComponent<Transform>(target))
            {
                if (EntityManager.HasBuffer<Game.Areas.Node>(target))
                {
                    Entity area = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(area, new CreationDefinition { m_Original = target, m_Flags = CreationFlags.Delete });
                    commandBuffer.AddComponent(area, default(Updated));
                    DynamicBuffer<Game.Areas.Node> source = EntityManager.GetBuffer<Game.Areas.Node>(target, isReadOnly: true);
                    commandBuffer.AddBuffer<Game.Areas.Node>(area).CopyFrom(source.AsNativeArray());
                }
                return;
            }
            Transform transform = EntityManager.GetComponentData<Transform>(target);
            var objectDefinition = new ObjectDefinition
            {
                m_Position = transform.m_Position,
                m_Rotation = transform.m_Rotation,
                m_LocalPosition = transform.m_Position,
                m_LocalRotation = transform.m_Rotation,
                m_Probability = 100,
                m_PrefabSubIndex = -1,
                m_ParentMesh = -1,
            };
            if (EntityManager.HasComponent<Game.Objects.Elevation>(target))
            {
                Game.Objects.Elevation elevation = EntityManager.GetComponentData<Game.Objects.Elevation>(target);
                objectDefinition.m_Elevation = elevation.m_Elevation;
                objectDefinition.m_ParentMesh = Game.Objects.ObjectUtils.GetSubParentMesh(elevation.m_Flags);
            }
            Entity e = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(e, new CreationDefinition { m_Original = target, m_Flags = CreationFlags.Delete });
            commandBuffer.AddComponent(e, objectDefinition);
            commandBuffer.AddComponent(e, default(Updated));
        }

        private void StageApply()
        {
            bool allowed = GetAllowApply();
            if (allowed)
            {
                if (!m_Current.Delete)
                {
                    MatchTempObjects();
                }
                applyMode = ApplyMode.Apply;
                m_BatchesApplied++;
                m_Stage = Stage.Resolve;
                return;
            }

            List<string> errors = CollectTempErrors();
            string reason = "blocked by game validation (" +
                (errors.Count > 0 ? string.Join(", ", errors) : "overlap, water, steep slope...") + ")";
            applyMode = ApplyMode.Clear;
            m_BatchesRejected++;
            m_Stage = Stage.Define;
            if (m_Current.Delete)
            {
                Finish("undo " + reason + "; the rest was not deleted");
                return;
            }
            if (m_Current.Indices.Count > 1)
            {
                int half = m_Current.Indices.Count / 2;
                m_Queue.Insert(0, new WorkItem { Indices = m_Current.Indices.GetRange(half, m_Current.Indices.Count - half) });
                m_Queue.Insert(0, new WorkItem { Indices = m_Current.Indices.GetRange(0, half) });
                return;
            }
            ImportObject obj = m_Job.Objects[m_Current.Indices[0]];
            obj.Status = "failed";
            obj.Error = reason;
        }

        /// <summary>
        /// Pairs this batch's new top-level Temp objects with the requested objects
        /// by position (nearest unclaimed within 1.5 m in x/z). A grid keyed on 4 m
        /// cells keeps it linear in the batch size.
        /// </summary>
        private void MatchTempObjects()
        {
            m_Matched.Clear();
            var grid = new Dictionary<long, List<(Entity entity, float2 xz)>>();
            using (NativeArray<Entity> temps = m_TempObjectQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in temps)
                {
                    if (EntityManager.GetComponentData<Temp>(entity).m_Original != Entity.Null)
                    {
                        continue;
                    }
                    float2 xz = EntityManager.GetComponentData<Transform>(entity).m_Position.xz;
                    long key = CellKey(xz);
                    if (!grid.TryGetValue(key, out var list))
                    {
                        list = new List<(Entity, float2)>();
                        grid[key] = list;
                    }
                    list.Add((entity, xz));
                }
            }
            // Surfaces: the Temp area matched by the centroid of its nodes.
            using (NativeArray<Entity> areas = m_TempAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in areas)
                {
                    if (EntityManager.GetComponentData<Temp>(entity).m_Original != Entity.Null)
                    {
                        continue;
                    }
                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, isReadOnly: true);
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
                    long key = CellKey(centroid);
                    if (!grid.TryGetValue(key, out var list))
                    {
                        list = new List<(Entity, float2)>();
                        grid[key] = list;
                    }
                    list.Add((entity, centroid));
                }
            }
            var claimed = new HashSet<Entity>();
            foreach (int index in m_Current.Indices)
            {
                float2 xz = m_Job.Objects[index].Position.xz;
                int2 cell = (int2)math.floor(xz / 4f);
                Entity best = Entity.Null;
                float bestDistance = kMatchDistance;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue(Key(cell + new int2(dx, dz)), out var list))
                        {
                            continue;
                        }
                        foreach ((Entity entity, float2 position) in list)
                        {
                            float distance = math.distance(position, xz);
                            if (distance < bestDistance && !claimed.Contains(entity))
                            {
                                bestDistance = distance;
                                best = entity;
                            }
                        }
                    }
                }
                if (best != Entity.Null)
                {
                    claimed.Add(best);
                    m_Matched[index] = best;
                }
            }
        }

        private static long CellKey(float2 xz)
        {
            return Key((int2)math.floor(xz / 4f));
        }

        private static long Key(int2 cell)
        {
            return ((long)cell.x << 32) ^ (uint)cell.y;
        }

        private void StageResolve()
        {
            if (m_Current.Delete)
            {
                m_Deleted += m_Current.Indices.Count;
            }
            else
            {
                foreach (int index in m_Current.Indices)
                {
                    ImportObject obj = m_Job.Objects[index];
                    if (m_Matched.TryGetValue(index, out Entity entity) && EntityManager.Exists(entity)
                        && !EntityManager.HasComponent<Temp>(entity) && !EntityManager.HasComponent<Deleted>(entity))
                    {
                        obj.Status = "created";
                        obj.Entity = entity;
                        m_Record.Entities.Add(entity);
                        // What Anarchy's toggle would have added: keeps the object visible where a
                        // net or area overlaps it (the game's Overridden flag).
                        if (m_Job.BypassValidation && obj.Polygon == null && AnarchyProtection.Protect(EntityManager, entity))
                        {
                            m_Protected++;
                        }
                    }
                    else
                    {
                        obj.Status = "failed";
                        obj.Error = "the batch was applied but no new object was found at this position";
                    }
                }
            }
            if (!m_Current.Delete && m_NetGuard != null)
            {
                var ids = new List<string>();
                foreach (int index in m_Current.Indices)
                {
                    ids.Add(m_Job.Objects[index].Id);
                }
                m_NetGuard.AfterBatch($"batch {m_BatchesApplied}", ids, GuardPrefabName);
            }
            m_Current = null;
            m_Stage = Stage.Define;
        }

        private string GuardPrefabName(Entity prefab)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            return prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?";
        }

        private List<string> CollectTempErrors()
        {
            var names = new List<string>();
            using (NativeArray<Entity> icons = m_IconQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity icon in icons)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(icon).m_Owner;
                    if (!EntityManager.Exists(owner) || !EntityManager.HasComponent<Temp>(owner))
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab;
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

        private void RestoreIgnoreErrors()
        {
            if (m_RestoreIgnoreErrors)
            {
                m_ToolSystem.ignoreErrors = m_IgnoreErrorsBefore;
                m_RestoreIgnoreErrors = false;
            }
        }

        private void Finish(string abortReason)
        {
            BridgeResponse response;
            if (m_Job != null)
            {
                object report = BuildReport(abortReason);
                m_LastReport = report;
                if (m_Record != null && m_Record.Entities.Count > 0)
                {
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
                if (abortReason == null && m_UndoTarget != null)
                {
                    m_Records.Remove(m_UndoTarget);
                }
                object report = new
                {
                    undone = abortReason == null,
                    importId = m_UndoTarget?.Id,
                    name = m_UndoTarget?.Name,
                    objectsDeleted = m_Deleted,
                    objectsRecorded = m_UndoTarget != null ? m_UndoTarget.Entities.Count : 0,
                    error = abortReason,
                    frames = m_Frames,
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
            m_DeleteEntities = null;
            m_Matched.Clear();
            RestoreIgnoreErrors();
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null && m_PreviousTool != this ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
        }

        private object BuildReport(string abortReason)
        {
            var counts = new Dictionary<string, int> { ["created"] = 0, ["failed"] = 0, ["pending"] = 0 };
            var byPrefab = new Dictionary<string, Dictionary<string, int>>();
            var failed = new List<object>();
            var results = m_Job.FullReport ? new List<object>(m_Job.Objects.Count) : null;
            foreach (ImportObject obj in m_Job.Objects)
            {
                counts[obj.Status] = counts.TryGetValue(obj.Status, out int n) ? n + 1 : 1;
                if (!byPrefab.TryGetValue(obj.PrefabName, out Dictionary<string, int> perPrefab))
                {
                    perPrefab = new Dictionary<string, int>();
                    byPrefab[obj.PrefabName] = perPrefab;
                }
                perPrefab[obj.Status] = perPrefab.TryGetValue(obj.Status, out int m) ? m + 1 : 1;
                if (obj.Status == "failed" && failed.Count < kReportListCap)
                {
                    failed.Add(new { id = obj.Id, prefab = obj.PrefabName, error = obj.Error });
                }
                results?.Add(new
                {
                    id = obj.Id,
                    status = obj.Status,
                    error = obj.Error,
                    prefab = obj.PrefabName,
                    kind = obj.Kind,
                    entity = obj.Entity != Entity.Null ? $"{obj.Entity.Index}:{obj.Entity.Version}" : null,
                    attachedTo = obj.AttachTo != Entity.Null ? $"{obj.AttachTo.Index}:{obj.AttachTo.Version}" : null,
                });
            }
            var connections = new List<object>();
            foreach (ImportObject obj in m_Job.Objects)
            {
                if (obj.Kind == "outsideConnection" && connections.Count < kReportListCap)
                {
                    connections.Add(new
                    {
                        id = obj.Id,
                        status = obj.Status,
                        node = obj.AttachTo != Entity.Null ? $"{obj.AttachTo.Index}:{obj.AttachTo.Version}" : null,
                        entity = obj.Entity != Entity.Null ? $"{obj.Entity.Index}:{obj.Entity.Version}" : null,
                    });
                }
            }
            return new
            {
                importId = m_Job.Id,
                name = m_Job.Name,
                completed = abortReason == null,
                abortReason,
                objects = new { requested = m_Job.Objects.Count, created = counts["created"], failed = counts["failed"], notRun = counts["pending"] },
                byPrefab,
                batches = new { applied = m_BatchesApplied, rejected = m_BatchesRejected },
                frames = m_Frames,
                seconds = Math.Round((DateTime.UtcNow - m_Started).TotalSeconds, 1),
                validation = new
                {
                    bypassValidation = m_Job.BypassValidation,
                    ignoreErrors = m_IgnoreErrorsAtStart,
                    anarchyDetected = m_AnarchyDetected,
                    anarchyEnabled = m_AnarchyEnabledAtStart,
                    anarchyPreventOverride = new
                    {
                        available = AnarchyProtection.Available,
                        added = m_Protected,
                        note = "with bypassValidation, created top-level static objects get Anarchy's PreventOverride (as with Anarchy's toggle on), so nets/areas overlapping them do not hide them; nets and areas themselves have no such protection",
                    },
                },
                netGuard = m_NetGuard?.Report(),
                undo = new
                {
                    objectsRecorded = m_Record != null ? m_Record.Entities.Count : 0,
                    note = "POST /import/objects/undo?name=<name> removes them (in memory only; lost when the game restarts)",
                },
                warningCount = m_Job.Warnings.Count,
                warnings = m_Job.Warnings.Count > kReportListCap ? m_Job.Warnings.GetRange(0, kReportListCap) : m_Job.Warnings,
                failedObjects = failed,
                outsideConnections = connections,
                results,
            };
        }
    }
}
