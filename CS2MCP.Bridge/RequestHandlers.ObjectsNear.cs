using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.54: GET /objects/near?x=&amp;z=&amp;radius=&amp;limit=&amp;prefab= — read-only list of the top-level
    /// objects nearest a point, in the editor as in a city: entity id, prefab, transform, tree state,
    /// elevation and every component type, so two instances can be compared (A21 checkerboard trees).
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ObjectsNear(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
            {
                return BridgeResponse.Error(400, "GET /objects/near?x=&z=[&radius=50][&limit=10][&prefab=name]");
            }
            float radius = request.TryGetFloat("radius", out float r) ? math.clamp(r, 1f, 2000f) : 50f;
            int limit = request.TryGetInt("limit", out int l) ? math.clamp(l, 1, 200) : 10;
            request.Query.TryGetValue("prefab", out string prefabFilter);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var found = new List<(float d, Entity e, string prefab)>();
            var centre = new float2(x, z);
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            using (NativeArray<Transform> transforms = query.ToComponentDataArray<Transform>(Allocator.Temp))
            {
                var names = new Dictionary<Entity, string>();
                for (int i = 0; i < entities.Length; i++)
                {
                    float d = math.distance(transforms[i].m_Position.xz, centre);
                    if (d > radius)
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                    if (!names.TryGetValue(prefab, out string name))
                    {
                        name = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?";
                        names[prefab] = name;
                    }
                    if (!string.IsNullOrEmpty(prefabFilter) && !string.Equals(name, prefabFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    found.Add((d, entities[i], name));
                }
            }
            found.Sort((a, b) => a.d.CompareTo(b.d));
            var list = new List<object>();
            foreach ((float d, Entity e, string prefab) in found)
            {
                if (list.Count >= limit)
                {
                    break;
                }
                Transform t = EntityManager.GetComponentData<Transform>(e);
                object tree = null;
                if (EntityManager.HasComponent<Game.Objects.Tree>(e))
                {
                    Game.Objects.Tree tr = EntityManager.GetComponentData<Game.Objects.Tree>(e);
                    tree = new { state = tr.m_State.ToString(), growth = tr.m_Growth };
                }
                object elevation = null;
                if (EntityManager.HasComponent<Game.Objects.Elevation>(e))
                {
                    Game.Objects.Elevation el = EntityManager.GetComponentData<Game.Objects.Elevation>(e);
                    elevation = new { value = el.m_Elevation, flags = el.m_Flags.ToString() };
                }
                var components = new List<string>();
                using (NativeArray<ComponentType> types = EntityManager.GetComponentTypes(e, Allocator.Temp))
                {
                    foreach (ComponentType type in types)
                    {
                        components.Add(type.GetManagedType()?.Name ?? type.ToString());
                    }
                }
                components.Sort(StringComparer.Ordinal);
                list.Add(new
                {
                    entity = new { index = e.Index, version = e.Version },
                    prefab,
                    distance = Math.Round(d, 2),
                    position = new { x = t.m_Position.x, y = t.m_Position.y, z = t.m_Position.z },
                    rotation = new { x = t.m_Rotation.value.x, y = t.m_Rotation.value.y, z = t.m_Rotation.value.z, w = t.m_Rotation.value.w },
                    tree,
                    elevation,
                    components,
                });
            }
            return BridgeResponse.Json(new
            {
                x,
                z,
                radius,
                matched = found.Count,
                objects = list,
                note = "read-only; top-level objects (no Owner) nearest the point; /entity/inspect?index=&version= for one of them",
            });
        }
    }
}
