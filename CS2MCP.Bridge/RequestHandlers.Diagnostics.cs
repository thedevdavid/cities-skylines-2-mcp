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
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// GET/POST /diagnostics/hierarchy: finds top-level objects whose prefab no longer
    /// has a valid PrefabSystem index (a prefab removed at runtime gets m_Index
    /// -1000000000). The editor's Workspace hierarchy (EditorHierarchyUISystem) collects
    /// every top-level object's PrefabData and names it with the unchecked
    /// PrefabSystem.GetPrefab, so one such object makes it throw every frame
    /// (ArgumentOutOfRangeException) and it never rebuilds. POST can delete those
    /// objects and reset the panel's caches.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private BridgeResponse HierarchyDiagnostics(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            bool post = request.Method == "POST";
            bool remove = post && request.TryGetBool("removeInvalid", out bool rawRemove) && rawRemove;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            int prefabCount = PrefabCount(prefabSystem);
            var groups = new Dictionary<Entity, List<Entity>>();
            int total = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Object>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>() },
            }))
            using (NativeArray<Entity> objects = query.ToEntityArray(Allocator.Temp))
            {
                total = objects.Length;
                foreach (Entity obj in objects)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(obj).m_Prefab;
                    if (!groups.TryGetValue(prefab, out List<Entity> list))
                    {
                        list = new List<Entity>();
                        groups[prefab] = list;
                    }
                    list.Add(obj);
                }
            }

            var invalid = new List<object>();
            var toDelete = new List<Entity>();
            foreach (KeyValuePair<Entity, List<Entity>> group in groups)
            {
                Entity prefab = group.Key;
                bool exists = EntityManager.Exists(prefab);
                bool hasData = exists && EntityManager.HasComponent<PrefabData>(prefab);
                int index = hasData ? EntityManager.GetComponentData<PrefabData>(prefab).m_Index : int.MinValue;
                if (hasData && index >= 0 && index < prefabCount)
                {
                    continue;
                }
                var samples = new List<object>();
                for (int i = 0; i < group.Value.Count && samples.Count < 5; i++)
                {
                    Entity obj = group.Value[i];
                    Transform t = EntityManager.HasComponent<Transform>(obj) ? EntityManager.GetComponentData<Transform>(obj) : default;
                    samples.Add(new { entity = $"{obj.Index}:{obj.Version}", x = t.m_Position.x, z = t.m_Position.z });
                }
                invalid.Add(new
                {
                    prefabEntity = $"{prefab.Index}:{prefab.Version}",
                    prefabExists = exists,
                    prefabDeleted = exists && EntityManager.HasComponent<Deleted>(prefab),
                    prefabIndex = hasData ? index : (int?)null,
                    reason = !exists ? "prefab entity no longer exists"
                        : !hasData ? "prefab entity has no PrefabData"
                        : index < 0 ? "prefab was removed from the PrefabSystem (negative index)"
                        : $"prefab index {index} beyond the {prefabCount} loaded prefabs",
                    objects = group.Value.Count,
                    samples,
                });
                toDelete.AddRange(group.Value);
            }

            int deleted = 0;
            if (remove && toDelete.Count > 0)
            {
                // Raw Deleted: the bulldoze pipeline needs the prefab's data, which is gone.
                foreach (Entity obj in toDelete)
                {
                    if (EntityManager.Exists(obj) && !EntityManager.HasComponent<Deleted>(obj))
                    {
                        EntityManager.AddComponent<Deleted>(obj);
                        deleted++;
                    }
                }
            }
            // Inspect the panel's own cache before any reset: a stale or half-written entry
            // there keeps it throwing even when every object is fine.
            object panelCache = InspectHierarchyCache(prefabCount);
            string reset = post ? ResetEditorHierarchy() : null;

            return BridgeResponse.Json(new
            {
                topLevelObjects = total,
                prefabsReferenced = groups.Count,
                loadedPrefabs = prefabCount,
                invalidGroups = invalid.Count,
                invalidObjects = toDelete.Count,
                deleted,
                panelCache,
                hierarchyReset = reset,
                invalid,
                note = invalid.Count == 0
                    ? "every top-level object has a valid prefab; if the Workspace panel still throws, POST to reset its caches"
                    : "these objects reference prefabs the game no longer has (removed at runtime, e.g. an asset pack " +
                      "unloaded or updated during the session); POST ?removeInvalid=true deletes them and resets the panel",
            });
        }

        /// <summary>Counts entries in EditorHierarchyUISystem.m_PrefabsCache whose index is outside the prefab list.</summary>
        private object InspectHierarchyCache(int prefabCount)
        {
            try
            {
                EditorHierarchyUISystem hierarchy = World.GetExistingSystemManaged<EditorHierarchyUISystem>();
                if (hierarchy == null)
                {
                    return null;
                }
                FieldInfo field = typeof(EditorHierarchyUISystem).GetField("m_PrefabsCache", BindingFlags.Instance | BindingFlags.NonPublic);
                if (!(field?.GetValue(hierarchy) is NativeParallelHashSet<PrefabData> cache) || !cache.IsCreated)
                {
                    return new { readable = false };
                }
                using (NativeArray<PrefabData> entries = cache.ToNativeArray(Allocator.Temp))
                {
                    var bad = new List<int>();
                    foreach (PrefabData entry in entries)
                    {
                        if (entry.m_Index < 0 || entry.m_Index >= prefabCount)
                        {
                            bad.Add(entry.m_Index);
                        }
                    }
                    return new
                    {
                        readable = true,
                        entries = entries.Length,
                        invalidEntries = bad.Count,
                        invalidIndices = bad.Count > 20 ? bad.GetRange(0, 20) : bad,
                        stuck = bad.Count > 0,
                        note = bad.Count > 0
                            ? "the Workspace panel holds invalid prefab indices and rethrows every frame; POST /diagnostics/hierarchy resets it"
                            : null,
                    };
                }
            }
            catch (Exception e)
            {
                return new { readable = false, error = $"{e.GetType().Name}: {e.Message}" };
            }
        }

        private static int PrefabCount(PrefabSystem prefabSystem)
        {
            FieldInfo field = typeof(PrefabSystem).GetField("m_Prefabs", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(prefabSystem) is ICollection list ? list.Count : int.MaxValue;
        }

        /// <summary>
        /// Clears EditorHierarchyUISystem's prefab caches and marks it dirty, so it
        /// rebuilds from the current objects instead of rethrowing on a stale entry.
        /// </summary>
        private string ResetEditorHierarchy()
        {
            try
            {
                EditorHierarchyUISystem hierarchy = World.GetExistingSystemManaged<EditorHierarchyUISystem>();
                if (hierarchy == null)
                {
                    return "no EditorHierarchyUISystem (not in the editor)";
                }
                Type type = typeof(EditorHierarchyUISystem);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object cache = type.GetField("m_PrefabsCache", flags)?.GetValue(hierarchy);
                cache?.GetType().GetMethod("Clear")?.Invoke(cache, null);
                (type.GetField("m_NamesCache", flags)?.GetValue(hierarchy) as IDictionary)?.Clear();
                (type.GetField("m_LockedCache", flags)?.GetValue(hierarchy) as IDictionary)?.Clear();
                type.GetField("m_HierarchyDirty", flags)?.SetValue(hierarchy, true);
                type.GetField("m_FilteringDirty", flags)?.SetValue(hierarchy, true);
                return "caches cleared; the panel rebuilds next frame";
            }
            catch (Exception e)
            {
                return $"reset failed: {e.GetType().Name}: {e.Message}";
            }
        }
    }
}
