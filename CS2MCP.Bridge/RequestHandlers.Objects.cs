using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AgeMask = Game.Tools.AgeMask;

namespace CS2MCP
{
    /// <summary>
    /// POST /import/objects (trees, props, buildings, outside connections) and
    /// POST /import/objects/undo, run by BridgeObjectToolSystem. Format in
    /// IMPORT_SCHEMA.md.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class ObjectDto
        {
            public string id;
            public string prefab;
            public float x;
            public float z;
            public float? y;
            public float? rotation;
            public string kind;
            public string age;
            public string net;
        }

        public sealed class ObjectOptionsDto
        {
            public bool? dryRun;
            public int? batchSize;
            public bool? bypassValidation;
            public string report;
            public int? settleFrames;
        }

        public sealed class ObjectImportDto
        {
            public string name;
            public ObjectOptionsDto options;
            public List<ObjectDto> objects;
            public bool? dryRun;
        }

        private const float kPlayableHalf = 7168f;
        private const float kConnectionSearch = 20f;
        private int m_ObjectImportCounter;
        private EntityQuery m_ObjectPrefabQuery;
        private bool m_ObjectPrefabQueryCreated;
        private EntityQuery m_NodeQuery;
        private bool m_NodeQueryCreated;

        private EntityQuery ObjectPrefabQuery
        {
            get
            {
                if (!m_ObjectPrefabQueryCreated)
                {
                    m_ObjectPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ObjectData>());
                    m_ObjectPrefabQueryCreated = true;
                }
                return m_ObjectPrefabQuery;
            }
        }

        private EntityQuery NodeQuery
        {
            get
            {
                if (!m_NodeQueryCreated)
                {
                    m_NodeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>() },
                        None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
                    });
                    m_NodeQueryCreated = true;
                }
                return m_NodeQuery;
            }
        }

        private bool ObjectToolsBusy(out BridgeResponse error)
        {
            if (TransitToolsBusy(out error))
            {
                return true;
            }
            if (World.GetOrCreateSystemManaged<BridgeImportToolSystem>().IsBusy
                || World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().IsBusy)
            {
                error = BridgeResponse.Error(409, "an import or undo is running; see GET /import/status or /import/objects/status");
                return true;
            }
            return false;
        }

        private BridgeResponse ImportObjects(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST" || string.IsNullOrEmpty(request.Body))
            {
                return BridgeResponse.Error(400, "POST a JSON body {name, options, objects:[{id,prefab,x,z,...}]} (see IMPORT_SCHEMA.md)");
            }
            ObjectImportDto body;
            try
            {
                body = JsonConvert.DeserializeObject<ObjectImportDto>(request.Body);
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body == null || body.objects == null || body.objects.Count == 0)
            {
                return BridgeResponse.Error(400, "body needs a non-empty 'objects' array");
            }
            ObjectOptionsDto options = body.options ?? new ObjectOptionsDto();

            // Every prefab name is resolved once.
            var prefabsByName = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> prefabs = ObjectPrefabQuery.ToEntityArray(Allocator.Temp))
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

            TerrainHeightData heightData = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData(waitForPending: true);
            Bounds3 terrainBounds = TerrainUtils.GetBounds(ref heightData);
            var job = new BridgeObjectToolSystem.ObjectJob
            {
                Id = $"obj-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{++m_ObjectImportCounter}",
                Name = string.IsNullOrEmpty(body.name) ? null : body.name,
                Objects = new List<BridgeObjectToolSystem.ImportObject>(body.objects.Count),
                BatchSize = math.clamp(options.batchSize ?? 500, 1, 5000),
                SettleFrames = math.clamp(options.settleFrames ?? 1, 1, 30),
                FullReport = string.Equals(options.report, "full", StringComparison.OrdinalIgnoreCase),
                BypassValidation = options.bypassValidation == true,
            };
            job.Name = job.Name ?? job.Id;

            var problems = new List<string>();
            var unknownPrefabs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var perKind = new Dictionary<string, int>();
            foreach (ObjectDto dto in body.objects)
            {
                if (dto == null || string.IsNullOrEmpty(dto.id) || !ids.Add(dto.id))
                {
                    problems.Add($"object '{dto?.id}': missing or duplicate id");
                    continue;
                }
                string hint = (dto.kind ?? "").Trim();
                bool connection = string.Equals(hint, "outsideConnection", StringComparison.OrdinalIgnoreCase);
                string net = (dto.net ?? "").Trim().ToLowerInvariant();

                if (math.abs(dto.x) > kPlayableHalf + 1f || math.abs(dto.z) > kPlayableHalf + 1f)
                {
                    problems.Add($"object '{dto.id}': ({dto.x:F1}, {dto.z:F1}) is outside the playable area (±{kPlayableHalf} m)");
                    continue;
                }

                var obj = new BridgeObjectToolSystem.ImportObject { Id = dto.id, PrefabName = dto.prefab ?? "" };

                // Road and rail outside connections are not placed: the game tags a net node
                // on the map border and spawns the connection marker on it by itself.
                if (connection && (net == "road" || net == "rail" || net == "train"))
                {
                    obj.Kind = "outsideConnection";
                    obj.PrefabName = string.IsNullOrEmpty(obj.PrefabName) ? $"({net} border node)" : obj.PrefabName;
                    ResolveBorderConnection(obj, new float2(dto.x, dto.z), net == "road");
                    Count(perKind, obj.Kind);
                    job.Objects.Add(obj);
                    continue;
                }

                if (string.IsNullOrEmpty(dto.prefab) || !prefabsByName.TryGetValue(dto.prefab, out Entity prefabEntity))
                {
                    unknownPrefabs[dto.prefab ?? ""] = unknownPrefabs.TryGetValue(dto.prefab ?? "", out int c) ? c + 1 : 1;
                    continue;
                }
                obj.Prefab = prefabEntity;
                obj.Kind = KindOf(prefabEntity);
                string hintKind = string.Equals(hint, "prop", StringComparison.OrdinalIgnoreCase) || string.Equals(hint, "decal", StringComparison.OrdinalIgnoreCase)
                    ? "object"
                    : hint;
                if (!string.IsNullOrEmpty(hintKind) && !string.Equals(hintKind, obj.Kind, StringComparison.OrdinalIgnoreCase)
                    && job.Warnings.Count < 1000)
                {
                    job.Warnings.Add($"object '{dto.id}': kind '{hint}' given, prefab '{dto.prefab}' is a {obj.Kind}");
                }

                float3 position = new float3(dto.x, 0f, dto.z);
                float ground = TerrainUtils.SampleHeight(ref heightData, position);
                float rotation = dto.rotation ?? 0f;
                if (obj.Kind == "outsideConnection")
                {
                    // Free border objects (airplane, ship): on the terrain edge, facing out,
                    // as ObjectToolSystem.HandleWorldSize does; NotOnBorder otherwise.
                    position.xz = SnapToBorder(position.xz, terrainBounds, out float outward);
                    if (!dto.rotation.HasValue)
                    {
                        rotation = outward;
                    }
                    ground = TerrainUtils.SampleHeight(ref heightData, position);
                }
                position.y = dto.y ?? ground;
                obj.Position = position;
                obj.Elevation = dto.y.HasValue ? math.max(0f, dto.y.Value - ground) : 0f;
                obj.Rotation = quaternion.RotateY(math.radians(rotation));
                if (!TryParseAge(dto.age, out obj.Age))
                {
                    problems.Add($"object '{dto.id}': age must be seedling, sapling, adult or elderly");
                    continue;
                }
                Count(perKind, obj.Kind);
                job.Objects.Add(obj);
            }

            if (unknownPrefabs.Count > 0)
            {
                var names = new List<string>();
                foreach (KeyValuePair<string, int> pair in unknownPrefabs)
                {
                    names.Add($"'{pair.Key}' ({pair.Value}x)");
                }
                problems.Insert(0, $"unknown object prefab(s): {string.Join(", ", names)} (exact names via /prefabs?category=tree|building or PREFAB_NAMES.md)");
            }
            if (problems.Count > 0)
            {
                return BridgeResponse.Json(new
                {
                    error = $"{problems.Count} problem(s) in the object import; nothing was placed",
                    problems = problems.Count > 200 ? problems.GetRange(0, 200) : problems,
                }, 400);
            }

            if (options.dryRun == true || body.dryRun == true)
            {
                var connections = new List<object>();
                foreach (BridgeObjectToolSystem.ImportObject obj in job.Objects)
                {
                    if (obj.Kind == "outsideConnection")
                    {
                        connections.Add(new
                        {
                            id = obj.Id,
                            status = obj.Status,
                            error = obj.Error,
                            node = obj.AttachTo != Entity.Null ? $"{obj.AttachTo.Index}:{obj.AttachTo.Version}" : null,
                            position = new { x = obj.Position.x, z = obj.Position.z },
                        });
                    }
                }
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    valid = true,
                    mode = editor ? "editor" : "game",
                    objects = job.Objects.Count,
                    prefabsResolved = CountPrefabs(job),
                    perKind,
                    batchSize = job.BatchSize,
                    warningCount = job.Warnings.Count,
                    warnings = job.Warnings.Count > 200 ? job.Warnings.GetRange(0, 200) : job.Warnings,
                    outsideConnections = connections,
                });
            }

            if (ObjectToolsBusy(out error))
            {
                return error;
            }
            if (!World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().TryStartImport(job, request))
            {
                return BridgeResponse.Error(409, "an object import or undo is already running");
            }
            return null;
        }

        private BridgeResponse UndoObjects(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return BridgeResponse.Error(405, "use POST /import/objects/undo[?name=<import name>|?id=<importId>]");
            }
            if (ObjectToolsBusy(out error))
            {
                return error;
            }
            string key = request.Query.TryGetValue("name", out string name) && !string.IsNullOrEmpty(name) ? name
                : request.Query.TryGetValue("id", out string id) ? id : null;
            if (!World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().TryStartUndo(key, request, out string reason))
            {
                return BridgeResponse.Error(409, reason);
            }
            return null;
        }

        private BridgeResponse ObjectsStatus()
        {
            return BridgeResponse.Json(World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().Status());
        }

        private string KindOf(Entity prefab)
        {
            if (EntityManager.HasComponent<OutsideConnectionData>(prefab))
            {
                return "outsideConnection";
            }
            if (EntityManager.HasComponent<TreeData>(prefab))
            {
                return "tree";
            }
            if (EntityManager.HasComponent<BuildingData>(prefab))
            {
                return "building";
            }
            if (EntityManager.HasComponent<PlantData>(prefab))
            {
                return "plant";
            }
            return "object";
        }

        private static bool TryParseAge(string age, out AgeMask mask)
        {
            switch ((age ?? "adult").Trim().ToLowerInvariant())
            {
                case "seedling":
                    mask = AgeMask.Sapling;
                    return true;
                case "sapling":
                case "young":
                    mask = AgeMask.Young;
                    return true;
                case "adult":
                case "mature":
                case "":
                    mask = AgeMask.Mature;
                    return true;
                case "elderly":
                case "old":
                    mask = AgeMask.Elderly;
                    return true;
                default:
                    mask = AgeMask.Mature;
                    return false;
            }
        }

        private static void Count(Dictionary<string, int> counts, string key)
        {
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        private static int CountPrefabs(BridgeObjectToolSystem.ObjectJob job)
        {
            var set = new HashSet<Entity>();
            foreach (BridgeObjectToolSystem.ImportObject obj in job.Objects)
            {
                if (obj.Prefab != Entity.Null)
                {
                    set.Add(obj.Prefab);
                }
            }
            return set.Count;
        }

        /// <summary>Nearest point on the terrain edge and the yaw (degrees) that faces out of the map there.</summary>
        private static float2 SnapToBorder(float2 xz, Bounds3 bounds, out float outwardYaw)
        {
            float toWest = xz.x - bounds.min.x;
            float toEast = bounds.max.x - xz.x;
            float toSouth = xz.y - bounds.min.z;
            float toNorth = bounds.max.z - xz.y;
            float best = math.min(math.min(toWest, toEast), math.min(toSouth, toNorth));
            if (best == toEast)
            {
                outwardYaw = 90f;
                return new float2(bounds.max.x, xz.y);
            }
            if (best == toWest)
            {
                outwardYaw = 270f;
                return new float2(bounds.min.x, xz.y);
            }
            if (best == toNorth)
            {
                outwardYaw = 0f;
                return new float2(xz.x, bounds.max.z);
            }
            outwardYaw = 180f;
            return new float2(xz.x, bounds.min.z);
        }

        /// <summary>
        /// Road/rail outside connection: the nearest node within 20 m whose segments are
        /// roads (or train tracks). "created" when the game has tagged it as an outside
        /// connection (Game.Net.OutsideConnection, given to nodes on the terrain border);
        /// otherwise "failed" with what is missing.
        /// </summary>
        private void ResolveBorderConnection(BridgeObjectToolSystem.ImportObject obj, float2 xz, bool road)
        {
            Entity best = Entity.Null;
            float bestDistance = kConnectionSearch;
            using (NativeArray<Entity> nodes = NodeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
                    float distance = math.distance(position.xz, xz);
                    if (distance >= bestDistance)
                    {
                        continue;
                    }
                    DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
                    bool matches = false;
                    for (int i = 0; i < edges.Length && !matches; i++)
                    {
                        matches = road
                            ? EntityManager.HasComponent<Road>(edges[i].m_Edge)
                            : EntityManager.HasComponent<TrainTrack>(edges[i].m_Edge);
                    }
                    if (matches)
                    {
                        bestDistance = distance;
                        best = node;
                    }
                }
            }
            obj.Position = new float3(xz.x, 0f, xz.y);
            if (best == Entity.Null)
            {
                obj.Status = "failed";
                obj.Error = $"no {(road ? "road" : "train track")} node within {kConnectionSearch} m; import the {(road ? "road" : "track")} " +
                    "so that it ends on the map border, the game then creates the outside connection there";
                return;
            }
            obj.AttachTo = best;
            obj.Position = EntityManager.GetComponentData<Node>(best).m_Position;
            if (EntityManager.HasComponent<Game.Net.OutsideConnection>(best))
            {
                obj.Status = "created";
                obj.Entity = best;
                obj.Error = null;
            }
            else
            {
                obj.Status = "failed";
                obj.Error = $"node {best.Index}:{best.Version} is {bestDistance:F1} m away but not on the terrain border, so the game " +
                    "did not make it an outside connection; extend the net to the border (x or z = ±7168)";
            }
        }
    }
}
