using System;
using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// GET /entity/subnets lists a building's own network pieces (Game.Net.SubNet):
    /// its internal paths and, importantly, its electricity marker nodes, the
    /// orphan nodes a power line must end on to connect (ElectricityBuildingGraphSystem
    /// links a building to the grid through those nodes). Network imports attach to
    /// one with the node field connectTo.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private BridgeResponse EntitySubNets(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(400, "provide ?index=&version= of a building (e.g. from an object import report)");
            }
            var entity = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(entity) || !EntityManager.HasBuffer<Game.Net.SubNet>(entity))
            {
                return BridgeResponse.Error(404, $"entity {index}:{version} does not exist or has no sub-nets");
            }
            var items = new List<object>();
            foreach (SubNetInfo info in SubNetsOf(entity))
            {
                items.Add(new
                {
                    entity = $"{info.Entity.Index}:{info.Entity.Version}",
                    prefab = info.Prefab,
                    kind = info.IsNode ? "node" : "edge",
                    position = info.IsNode ? new { x = info.Position.x, y = info.Position.y, z = info.Position.z } : null,
                    voltage = info.Voltage,
                    segmentsConnected = info.Connected,
                });
            }
            return BridgeResponse.Json(new
            {
                building = $"{index}:{version}",
                subNets = items,
                note = "power lines connect only by ending on an electricity marker node (voltage high/low); " +
                       "in /import/network give that node connectTo:{index,version,voltage}",
            });
        }

        private struct SubNetInfo
        {
            public Entity Entity;
            public string Prefab;
            public bool IsNode;
            public float3 Position;
            public string Voltage;
            public int Connected;
        }

        private List<SubNetInfo> SubNetsOf(Entity building)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var result = new List<SubNetInfo>();
            DynamicBuffer<Game.Net.SubNet> subNets = EntityManager.GetBuffer<Game.Net.SubNet>(building, isReadOnly: true);
            for (int i = 0; i < subNets.Length; i++)
            {
                Entity subNet = subNets[i].m_SubNet;
                if (!EntityManager.Exists(subNet) || !EntityManager.HasComponent<PrefabRef>(subNet))
                {
                    continue;
                }
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(subNet).m_Prefab;
                var info = new SubNetInfo
                {
                    Entity = subNet,
                    Prefab = prefabSystem.TryGetPrefab(prefab, out PrefabBase prefabBase) ? prefabBase.name : null,
                    IsNode = EntityManager.HasComponent<Node>(subNet),
                };
                if (info.IsNode)
                {
                    info.Position = EntityManager.GetComponentData<Node>(subNet).m_Position;
                    info.Connected = EntityManager.HasBuffer<ConnectedEdge>(subNet)
                        ? EntityManager.GetBuffer<ConnectedEdge>(subNet, isReadOnly: true).Length
                        : 0;
                }
                if (EntityManager.HasComponent<ElectricityConnectionData>(prefab))
                {
                    info.Voltage = EntityManager.GetComponentData<ElectricityConnectionData>(prefab).m_Voltage == Game.Prefabs.ElectricityConnection.Voltage.High
                        ? "high"
                        : "low";
                }
                result.Add(info);
            }
            return result;
        }

        /// <summary>
        /// connectTo with a building id, or without one: the nearest electricity marker node
        /// of the wanted voltage on any building within radius (default 80 m) of x/z.
        /// </summary>
        private bool TryResolveConnect(ImportConnectDto connect, float2 near, out Entity marker, out string why)
        {
            if (connect.index.HasValue && connect.version.HasValue)
            {
                return TryResolveConnectTo(new ImportEntityRef { index = connect.index.Value, version = connect.version.Value },
                    connect.voltage, near, out marker, out why);
            }
            marker = Entity.Null;
            float radius = math.clamp(connect.radius ?? 80f, 1f, 500f);
            string wanted = string.IsNullOrEmpty(connect.voltage) ? null : connect.voltage.ToLowerInvariant();
            float best = radius;
            using (EntityQuery buildings = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Net.SubNet>()))
            using (Unity.Collections.NativeArray<Entity> entities = buildings.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                foreach (Entity building in entities)
                {
                    foreach (SubNetInfo info in SubNetsOf(building))
                    {
                        if (!info.IsNode || info.Voltage == null || (wanted != null && info.Voltage != wanted))
                        {
                            continue;
                        }
                        float distance = math.distance(info.Position.xz, near);
                        if (distance < best)
                        {
                            best = distance;
                            marker = info.Entity;
                        }
                    }
                }
            }
            why = marker == Entity.Null
                ? $"no {(wanted ?? "electricity")}-voltage marker node of a building within {radius} m"
                : null;
            return marker != Entity.Null;
        }

        /// <summary>
        /// connectTo: the building's electricity marker node of the wanted voltage that
        /// lies nearest to the import node's x/z (any voltage when none is given).
        /// </summary>
        private bool TryResolveConnectTo(ImportEntityRef building, string voltage, float2 near, out Entity marker, out string why)
        {
            marker = Entity.Null;
            var entity = new Entity { Index = building.index, Version = building.version };
            if (!EntityManager.Exists(entity) || !EntityManager.HasBuffer<Game.Net.SubNet>(entity))
            {
                why = $"connectTo {building.index}:{building.version} does not exist or has no sub-nets";
                return false;
            }
            string wanted = string.IsNullOrEmpty(voltage) ? null : voltage.ToLowerInvariant();
            float best = float.MaxValue;
            var seen = new List<string>();
            foreach (SubNetInfo info in SubNetsOf(entity))
            {
                if (!info.IsNode || info.Voltage == null)
                {
                    continue;
                }
                seen.Add(info.Voltage);
                if (wanted != null && info.Voltage != wanted)
                {
                    continue;
                }
                float distance = math.distance(info.Position.xz, near);
                if (distance < best)
                {
                    best = distance;
                    marker = info.Entity;
                }
            }
            if (marker == Entity.Null)
            {
                why = seen.Count == 0
                    ? $"building {building.index}:{building.version} has no electricity marker nodes"
                    : $"building {building.index}:{building.version} has no {wanted}-voltage marker node (it has: {string.Join(", ", seen)})";
                return false;
            }
            why = null;
            return true;
        }
    }
}
