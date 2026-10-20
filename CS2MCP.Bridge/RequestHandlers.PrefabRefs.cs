using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.UI.Editor;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// GET /debug/prefab-refs: names whatever puts an invalid PrefabData into the editor's
    /// Workspace hierarchy. It reports every prefab entity whose index is outside
    /// PrefabSystem's list, with the PrefabRef holders of each one (top-level objects
    /// separately); the prefabs whose PrefabID collides with another (the losers resolve
    /// to the winner's index); the IDs a loaded save could not resolve; and the
    /// Workspace "Prefabs" list (EditorHierarchyUISystem.m_Prefabs), which CollectPrefabs
    /// adds to the panel cache unchecked. POST ?fixWorkspace=true drops Workspace entries
    /// with an invalid index and resets the panel.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private BridgeResponse PrefabRefsDebug(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            bool fix = request.Method == "POST" && request.TryGetBool("fixWorkspace", out bool rawFix) && rawFix;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var prefabs = typeof(PrefabSystem).GetField("m_Prefabs", Private)?.GetValue(prefabSystem) as List<PrefabBase>;
            int count = prefabs?.Count ?? int.MaxValue;
            IList obsolete = typeof(PrefabSystem).GetField("m_ObsoleteIDs", Private)?.GetValue(prefabSystem) as IList;

            // 1. Prefab entities with an index outside the list.
            var badPrefabs = new Dictionary<Entity, int>();
            int prefabEntities = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            }))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                prefabEntities = entities.Length;
                foreach (Entity e in entities)
                {
                    int index = EntityManager.GetComponentData<PrefabData>(e).m_Index;
                    if (index < 0 || index >= count)
                    {
                        badPrefabs[e] = index;
                    }
                }
            }

            // 2. Who references them (any PrefabRef, top-level objects counted apart).
            var holders = new Dictionary<Entity, List<Entity>>();
            var missingTargets = new Dictionary<Entity, List<Entity>>();
            int refs = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabRef>()))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                refs = entities.Length;
                foreach (Entity e in entities)
                {
                    Entity target = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    Dictionary<Entity, List<Entity>> into = null;
                    if (badPrefabs.ContainsKey(target))
                    {
                        into = holders;
                    }
                    else if (!EntityManager.Exists(target) || !EntityManager.HasComponent<PrefabData>(target))
                    {
                        into = missingTargets;
                    }
                    if (into == null)
                    {
                        continue;
                    }
                    if (!into.TryGetValue(target, out List<Entity> list))
                    {
                        list = new List<Entity>();
                        into[target] = list;
                    }
                    list.Add(e);
                }
            }

            var invalid = new List<object>();
            foreach (KeyValuePair<Entity, int> bad in badPrefabs)
            {
                holders.TryGetValue(bad.Key, out List<Entity> users);
                invalid.Add(new
                {
                    prefabEntity = Id(bad.Key),
                    index = bad.Value,
                    name = NameForIndex(prefabSystem, prefabs, obsolete, bad.Key, bad.Value),
                    deleted = EntityManager.HasComponent<Deleted>(bad.Key),
                    kind = bad.Value == -1000000000 ? "removed from PrefabSystem at runtime"
                        : bad.Value < 0 ? "obsolete: a save referenced a prefab that is not loaded"
                        : "index past the prefab list",
                    referencedBy = users?.Count ?? 0,
                    topLevelObjects = users == null ? 0 : users.FindAll(IsTopLevelObject).Count,
                    samples = Describe(users),
                });
            }
            var dangling = new List<object>();
            foreach (KeyValuePair<Entity, List<Entity>> m in missingTargets)
            {
                dangling.Add(new
                {
                    prefabEntity = Id(m.Key),
                    exists = EntityManager.Exists(m.Key),
                    referencedBy = m.Value.Count,
                    topLevelObjects = m.Value.FindAll(IsTopLevelObject).Count,
                    samples = Describe(m.Value),
                });
            }

            // 3. Duplicate prefab IDs among the loaded prefabs.
            var duplicates = new List<object>();
            var indices = typeof(PrefabSystem).GetField("m_PrefabIndices", Private)?.GetValue(prefabSystem) as IDictionary;
            if (prefabs != null)
            {
                var byId = new Dictionary<string, List<int>>();
                for (int i = 0; i < prefabs.Count; i++)
                {
                    if (prefabs[i] == null)
                    {
                        continue;
                    }
                    string key = prefabs[i].GetPrefabID().ToString();
                    if (!byId.TryGetValue(key, out List<int> list))
                    {
                        list = new List<int>();
                        byId[key] = list;
                    }
                    list.Add(i);
                }
                foreach (KeyValuePair<string, List<int>> group in byId)
                {
                    if (group.Value.Count < 2)
                    {
                        continue;
                    }
                    PrefabID id = prefabs[group.Value[0]].GetPrefabID();
                    object winner = indices != null && indices.Contains(id) ? indices[id] : null;
                    var members = new List<object>();
                    foreach (int i in group.Value)
                    {
                        members.Add(new
                        {
                            index = i,
                            name = prefabs[i].name,
                            type = prefabs[i].GetType().Name,
                            asset = prefabs[i].asset?.ToString(),
                            resolvesTo = winner is int w && w == i,
                        });
                    }
                    duplicates.Add(new { id = group.Key, members });
                }
            }

            // 4. IDs from the loaded save that did not resolve.
            var loadedObsolete = new List<object>();
            if (typeof(PrefabSystem).GetField("m_LoadedObsoleteIDs", Private)?.GetValue(prefabSystem) is IDictionary lo)
            {
                foreach (DictionaryEntry entry in lo)
                {
                    loadedObsolete.Add(new { loadedIndex = entry.Key, id = entry.Value?.ToString() });
                }
            }
            var obsoleteEntities = new List<object>();
            if (obsolete != null)
            {
                for (int j = 0; j < obsolete.Count; j++)
                {
                    object o = obsolete[j];
                    Type t = o.GetType();
                    var e = (Entity)t.GetField("m_Entity").GetValue(o);
                    obsoleteEntities.Add(new { index = -1 - j, entity = Id(e), id = t.GetField("m_ID").GetValue(o)?.ToString() });
                }
            }

            // 5. The Workspace "Prefabs" list.
            object workspace = null;
            int dropped = 0;
            EditorHierarchyUISystem hierarchy = World.GetExistingSystemManaged<EditorHierarchyUISystem>();
            if (hierarchy != null && typeof(EditorHierarchyUISystem).GetField("m_Prefabs", Private)?.GetValue(hierarchy) is List<PrefabBase> ws)
            {
                var items = new List<object>();
                for (int i = ws.Count - 1; i >= 0; i--)
                {
                    PrefabBase p = ws[i];
                    int? index = null;
                    string entity = null;
                    if (p != null && prefabSystem.TryGetEntity(p, out Entity pe) && EntityManager.HasComponent<PrefabData>(pe))
                    {
                        index = EntityManager.GetComponentData<PrefabData>(pe).m_Index;
                        entity = Id(pe);
                    }
                    bool valid = index.HasValue && index.Value >= 0 && index.Value < count;
                    items.Insert(0, new { name = p?.name, type = p?.GetType().Name, asset = p?.asset?.ToString(), entity, index, valid });
                    if (fix && !valid)
                    {
                        ws.RemoveAt(i);
                        dropped++;
                    }
                }
                workspace = items;
            }

            object panelCache = InspectHierarchyCache(count);
            string reset = fix ? ResetEditorHierarchy() : null;
            return BridgeResponse.Json(new
            {
                loadedPrefabs = count,
                prefabEntities,
                prefabRefs = refs,
                invalidPrefabs = invalid,
                danglingRefs = dangling,
                duplicateIds = duplicates,
                loadedObsoleteIds = loadedObsolete,
                obsoleteEntities,
                workspacePrefabs = workspace,
                workspaceDropped = fix ? dropped : (int?)null,
                panelCache,
                hierarchyReset = reset,
            });
        }

        private string NameForIndex(PrefabSystem prefabSystem, List<PrefabBase> prefabs, IList obsolete, Entity entity, int index)
        {
            if (index < 0 && index > -1000000000 && obsolete != null && -1 - index < obsolete.Count)
            {
                object o = obsolete[-1 - index];
                return "obsolete " + o.GetType().GetField("m_ID").GetValue(o);
            }
            // A removed prefab keeps its PrefabBase in m_Entities until cleanup: look it up by entity.
            if (prefabSystem != null && typeof(PrefabSystem).GetField("m_Entities", Private)?.GetValue(prefabSystem) is IDictionary entities)
            {
                foreach (DictionaryEntry entry in entities)
                {
                    if (entry.Value is Entity e && e == entity)
                    {
                        return (entry.Key as PrefabBase)?.name;
                    }
                }
            }
            return null;
        }

        private bool IsTopLevelObject(Entity e)
        {
            return EntityManager.HasComponent<Game.Objects.Object>(e) && !EntityManager.HasComponent<Owner>(e)
                && !EntityManager.HasComponent<Temp>(e) && !EntityManager.HasComponent<Deleted>(e);
        }

        private List<object> Describe(List<Entity> entities)
        {
            var samples = new List<object>();
            if (entities == null)
            {
                return samples;
            }
            for (int i = 0; i < entities.Count && samples.Count < 5; i++)
            {
                Entity e = entities[i];
                Game.Objects.Transform t = EntityManager.HasComponent<Game.Objects.Transform>(e) ? EntityManager.GetComponentData<Game.Objects.Transform>(e) : default;
                Entity owner = EntityManager.HasComponent<Owner>(e) ? EntityManager.GetComponentData<Owner>(e).m_Owner : Entity.Null;
                samples.Add(new
                {
                    entity = Id(e),
                    obj = EntityManager.HasComponent<Game.Objects.Object>(e),
                    owner = owner == Entity.Null ? null : Id(owner),
                    deleted = EntityManager.HasComponent<Deleted>(e),
                    temp = EntityManager.HasComponent<Temp>(e),
                    x = t.m_Position.x,
                    z = t.m_Position.z,
                });
            }
            return samples;
        }

    }
}
