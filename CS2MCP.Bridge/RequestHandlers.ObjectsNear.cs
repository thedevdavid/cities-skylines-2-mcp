using System;
using System.Collections.Generic;
using System.Reflection;
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
        private static MethodInfo s_GetComponentData;
        private static MethodInfo s_IsComponentEnabled;
        private static MethodInfo s_GetBuffer;

        /// <summary>
        /// 0.9.55: field values of every component on the entity, by reflection: plain components
        /// via EntityManager.GetComponentData&lt;T&gt; (with the enabled state for enableable ones),
        /// buffers as length plus the first 4 elements; tags as "tag".
        /// </summary>
        private Dictionary<string, object> ComponentValues(Entity e)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (s_GetComponentData == null)
            {
                foreach (MethodInfo m in typeof(EntityManager).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (m.Name == "GetComponentData" && m.IsGenericMethodDefinition && p.Length == 1 && p[0].ParameterType == typeof(Entity))
                    {
                        s_GetComponentData = m;
                    }
                    else if (m.Name == "IsComponentEnabled" && m.IsGenericMethodDefinition && p.Length == 1 && p[0].ParameterType == typeof(Entity))
                    {
                        s_IsComponentEnabled = m;
                    }
                    else if (m.Name == "GetBuffer" && m.IsGenericMethodDefinition && p.Length == 2 && p[0].ParameterType == typeof(Entity))
                    {
                        s_GetBuffer = m;
                    }
                }
            }
            var result = new Dictionary<string, object>();
            using (NativeArray<ComponentType> types = EntityManager.GetComponentTypes(e, Allocator.Temp))
            {
                foreach (ComponentType type in types)
                {
                    Type managed = type.GetManagedType();
                    if (managed == null)
                    {
                        continue;
                    }
                    string key = managed.FullName;
                    try
                    {
                        if (type.IsZeroSized)
                        {
                            result[key] = "tag";
                        }
                        else if (type.IsBuffer)
                        {
                            object buffer = s_GetBuffer.MakeGenericMethod(managed).Invoke(EntityManager, new object[] { e, true });
                            int length = (int)buffer.GetType().GetProperty("Length").GetValue(buffer);
                            var items = new List<object>();
                            PropertyInfo indexer = buffer.GetType().GetProperty("Item");
                            for (int i = 0; i < math.min(length, 4); i++)
                            {
                                items.Add(FieldsOf(indexer.GetValue(buffer, new object[] { i }), 2));
                            }
                            result[key] = new { length, first = items };
                        }
                        else if (type.IsComponent && !type.IsManagedComponent && !type.IsSharedComponent)
                        {
                            object data = s_GetComponentData.MakeGenericMethod(managed).Invoke(EntityManager, new object[] { e });
                            bool? enabled = null;
                            if (type.IsEnableable && s_IsComponentEnabled != null)
                            {
                                enabled = (bool)s_IsComponentEnabled.MakeGenericMethod(managed).Invoke(EntityManager, new object[] { e });
                            }
                            result[key] = enabled.HasValue ? (object)new { enabled, fields = FieldsOf(data, 2) } : FieldsOf(data, 2);
                        }
                        else
                        {
                            result[key] = "shared/managed (not read)";
                        }
                    }
                    catch (Exception ex)
                    {
                        result[key] = $"{(ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException.Message : ex.Message)}";
                    }
                }
            }
            return result;

            object FieldsOf(object value, int depth)
            {
                if (value == null)
                {
                    return null;
                }
                Type t = value.GetType();
                if (t.IsPrimitive || t.IsEnum || value is string || depth <= 0 || t.Namespace == "Unity.Mathematics" || t == typeof(Entity))
                {
                    return value.ToString();
                }
                var fields = new Dictionary<string, object>();
                foreach (FieldInfo f in t.GetFields(any))
                {
                    fields[f.Name] = FieldsOf(f.GetValue(value), depth - 1);
                }
                return fields.Count > 0 ? (object)fields : value.ToString();
            }
        }

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
            bool values = request.TryGetBool("values", out bool v) && v;
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
                    values = values ? ComponentValues(e) : null,
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
