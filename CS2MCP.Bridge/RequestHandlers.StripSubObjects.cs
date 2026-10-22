using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.58: POST /build/strip-subobjects. The A23 ring of checker trees around OldCastle01 were
    /// the building's OWN tree sub-objects: OverrideSystem hides owned objects that something
    /// else overlaps (Overridden + Updated), and Anarchy's RemoveOverridenSystem strips Overridden
    /// (+ Updated) from every owned overridden object each frame while the active tool is Default or
    /// Bulldoze (or any top-level PreventOverride object is looping) and PreventOverrideInEditor is
    /// on, so they loop. This deletes the owned tree/plant sub-objects of the named building
    /// prefabs (by default only those caught in the loop: Overridden or Updated right now).
    /// SubObjectSystem recreates a building's sub-objects whenever the building is Updated, so run it
    /// as a last build step; a saved map keeps the result (loading does not regenerate them).
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class StripSubObjectsDto
        {
            public List<string> prefabs;
            public List<string> kinds;
            public bool? onlyLooping;
            public bool? dryRun;
        }

        private BridgeResponse StripSubObjects(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return BridgeResponse.Error(405, "POST /build/strip-subobjects {prefabs:[building prefab names], kinds?:[tree, plant, object], onlyLooping?: true, dryRun?}");
            }
            StripSubObjectsDto body;
            try
            {
                body = JsonConvert.DeserializeObject<StripSubObjectsDto>(request.Body ?? "{}") ?? new StripSubObjectsDto();
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (body.prefabs == null || body.prefabs.Count == 0)
            {
                return BridgeResponse.Error(400, "prefabs: building prefab names whose sub-objects to strip, e.g. [\"OldCastle01\"]");
            }
            var prefabNames = new HashSet<string>(body.prefabs, StringComparer.OrdinalIgnoreCase);
            var kinds = new HashSet<string>(body.kinds ?? new List<string> { "tree", "plant" }, StringComparer.OrdinalIgnoreCase);
            bool onlyLooping = body.onlyLooping ?? true;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string NameOf(Entity prefab) =>
                prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?";

            var targets = new List<Entity>();
            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int buildings = 0, kept = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.SubObject>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity building in entities)
                {
                    if (!prefabNames.Contains(NameOf(EntityManager.GetComponentData<PrefabRef>(building).m_Prefab)))
                    {
                        continue;
                    }
                    buildings++;
                    var stack = new Stack<Entity>();
                    stack.Push(building);
                    while (stack.Count > 0)
                    {
                        Entity owner = stack.Pop();
                        if (!EntityManager.HasBuffer<Game.Objects.SubObject>(owner))
                        {
                            continue;
                        }
                        foreach (Game.Objects.SubObject sub in EntityManager.GetBuffer<Game.Objects.SubObject>(owner, true))
                        {
                            Entity e = sub.m_SubObject;
                            if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e))
                            {
                                continue;
                            }
                            stack.Push(e);
                            string kind = EntityManager.HasComponent<Game.Objects.Tree>(e) ? "tree"
                                : EntityManager.HasComponent<Game.Objects.Plant>(e) ? "plant"
                                : EntityManager.HasComponent<Game.Buildings.Building>(e) ? "building"
                                : "object";
                            if (!kinds.Contains(kind))
                            {
                                continue;
                            }
                            // 0.9.59: seen Overridden/Updated in any sample of the last ~2 s (OverrideSampler).
                            bool looping = OverrideSampler.IsLooping(e) || EntityManager.HasComponent<Game.Common.Overridden>(e);
                            if (onlyLooping && !looping)
                            {
                                kept++;
                                continue;
                            }
                            targets.Add(e);
                            string key = $"{kind}: {NameOf(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab)}";
                            counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                        }
                    }
                }
            }
            var perPrefab = new List<object>();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                perPrefab.Add(new { prefab = pair.Key, count = pair.Value });
            }
            if (body.dryRun == true || targets.Count == 0)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = body.dryRun == true,
                    buildings,
                    matched = targets.Count,
                    keptNotLooping = kept,
                    perPrefab,
                    note = targets.Count == 0 ? "nothing matched" : "dry run: nothing was removed",
                });
            }
            Mod.Log.Info($"strip-subobjects: deleting {targets.Count} owned objects of {buildings} building(s)");
            if (!World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().TryQueueStrip(targets, request, new { buildings, keptNotLooping = kept, perPrefab }, out string reason))
            {
                return BridgeResponse.Error(409, reason);
            }
            return null;
        }
    }
}
