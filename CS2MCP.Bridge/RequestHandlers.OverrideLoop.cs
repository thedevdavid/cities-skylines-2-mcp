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
    /// 0.9.56: the PreventOverride loop. Game.Objects.OverrideSystem gives an Overridable object that
    /// overlaps a building's geometry Overridden + Updated; Anarchy's PreventOverrideSystem removes
    /// Overridden from every PreventOverride entity and adds Updated; OverrideSystem re-checks every
    /// Updated object, so the pair never settles. Each such object is updated every frame (A21: the
    /// deciduous trees inside the OldCastle01 lot rendered as a checkerboard; undoing the castle
    /// healed them within seconds). Vanilla clears plants from a building lot instead.
    /// GET /debug/override-loop lists PreventOverride objects inside building footprints;
    /// /import/objects options.skipInsideBuildings leaves such objects out at import.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private struct BuildingFootprint
        {
            public Entity Entity;
            public string Prefab;
            public float3 Position;
            public quaternion InverseRotation;
            public float2 Min;
            public float2 Max;
            public float2 BoundsMin;
            public float2 BoundsMax;
        }

        /// <summary>Top-level buildings with their prefab geometry bounds, rotated with the building.</summary>
        private List<BuildingFootprint> BuildingFootprints()
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var list = new List<BuildingFootprint>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!EntityManager.HasComponent<ObjectGeometryData>(prefab))
                    {
                        continue;
                    }
                    Colossal.Mathematics.Bounds3 b = EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
                    Transform t = EntityManager.GetComponentData<Transform>(e);
                    // World-space AABB of the rotated footprint, for a cheap first test.
                    float2 wMin = new float2(float.MaxValue), wMax = new float2(float.MinValue);
                    foreach (float3 corner in new[] { new float3(b.min.x, 0f, b.min.z), new float3(b.max.x, 0f, b.min.z), new float3(b.min.x, 0f, b.max.z), new float3(b.max.x, 0f, b.max.z) })
                    {
                        float3 w = t.m_Position + math.rotate(t.m_Rotation, corner);
                        wMin = math.min(wMin, w.xz);
                        wMax = math.max(wMax, w.xz);
                    }
                    list.Add(new BuildingFootprint
                    {
                        Entity = e,
                        Prefab = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?",
                        Position = t.m_Position,
                        InverseRotation = math.inverse(t.m_Rotation),
                        Min = wMin,
                        Max = wMax,
                        BoundsMin = b.min.xz,
                        BoundsMax = b.max.xz,
                    });
                }
            }
            return list;
        }

        private static bool InsideFootprint(List<BuildingFootprint> footprints, float3 position, out BuildingFootprint hit)
        {
            foreach (BuildingFootprint f in footprints)
            {
                if (position.x < f.Min.x || position.x > f.Max.x || position.z < f.Min.y || position.z > f.Max.y)
                {
                    continue;
                }
                float3 local = math.rotate(f.InverseRotation, position - f.Position);
                if (local.x >= f.BoundsMin.x && local.x <= f.BoundsMax.x && local.z >= f.BoundsMin.y && local.z <= f.BoundsMax.y)
                {
                    hit = f;
                    return true;
                }
            }
            hit = default;
            return false;
        }

        /// <summary>GET /debug/override-loop</summary>
        private BridgeResponse OverrideLoop(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!AnarchyProtection.Available)
            {
                return BridgeResponse.Json(new { anarchy = false, note = "Anarchy's PreventOverride component is not loaded; no loop is possible" });
            }
            List<BuildingFootprint> footprints = BuildingFootprints();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var perBuilding = new Dictionary<Entity, (BuildingFootprint building, Dictionary<string, int> prefabs)>();
            int total = 0;
            var looping = new List<Entity>();
            var ids = new List<object>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>(), AnarchyProtection.PreventOverrideType },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Buildings.Building>() },
            }))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                var names = new Dictionary<Entity, string>();
                foreach (Entity e in entities)
                {
                    if (!InsideFootprint(footprints, EntityManager.GetComponentData<Transform>(e).m_Position, out BuildingFootprint hit))
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!names.TryGetValue(prefab, out string name))
                    {
                        name = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : "?";
                        names[prefab] = name;
                    }
                    if (!perBuilding.TryGetValue(hit.Entity, out var entry))
                    {
                        entry = (hit, new Dictionary<string, int>());
                        perBuilding[hit.Entity] = entry;
                    }
                    entry.prefabs[name] = entry.prefabs.TryGetValue(name, out int c) ? c + 1 : 1;
                    total++;
                    looping.Add(e);
                    if (ids.Count < 1000)
                    {
                        Transform t = EntityManager.GetComponentData<Transform>(e);
                        ids.Add(new { index = e.Index, version = e.Version, prefab = name, building = hit.Prefab, x = Math.Round(t.m_Position.x, 1), z = Math.Round(t.m_Position.z, 1) });
                    }
                }
            }
            // 0.9.57: POST ?demolish=true removes exactly these objects through the object tool's
            // bulldoze batches (same report as /import/objects/undo).
            if (request.Method == "POST" && request.TryGetBool("demolish", out bool demolish) && demolish)
            {
                if (looping.Count == 0)
                {
                    return BridgeResponse.Json(new { objectsInLoop = 0, note = "nothing to remove" });
                }
                if (TransitToolsBusy(out error))
                {
                    return error;
                }
                string label = $"override-loop-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                Mod.Log.Info($"{label}: removing {looping.Count} PreventOverride objects inside building footprints");
                if (!World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().TryStartDelete(looping, label, request, out string reason))
                {
                    return BridgeResponse.Error(409, reason);
                }
                return null;
            }
            var buildings = new List<object>();
            foreach (var pair in perBuilding.Values)
            {
                buildings.Add(new
                {
                    building = new { index = pair.building.Entity.Index, version = pair.building.Entity.Version },
                    prefab = pair.building.Prefab,
                    position = new { x = pair.building.Position.x, z = pair.building.Position.z },
                    objects = pair.prefabs,
                });
            }
            // 0.9.58: owned objects (building sub-objects) caught in Anarchy's RemoveOverridenSystem
            // loop: Overridden or Updated right now, per owning building prefab.
            var owned = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int ownedTotal = 0;
            using (EntityQuery ownedQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<PrefabRef>() },
                Any = new[] { ComponentType.ReadOnly<Game.Common.Overridden>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (NativeArray<Entity> ownedEntities = ownedQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in ownedEntities)
                {
                    Entity top = e;
                    while (EntityManager.HasComponent<Owner>(top))
                    {
                        top = EntityManager.GetComponentData<Owner>(top).m_Owner;
                    }
                    if (!EntityManager.Exists(top) || !EntityManager.HasComponent<Game.Buildings.Building>(top))
                    {
                        continue;
                    }
                    string buildingName = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(EntityManager.GetComponentData<PrefabRef>(top).m_Prefab), out PrefabBase bp) ? bp.name : "?";
                    string subName = prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab), out PrefabBase sp) ? sp.name : "?";
                    string key = $"{buildingName} / {subName}";
                    owned[key] = owned.TryGetValue(key, out int c) ? c + 1 : 1;
                    ownedTotal++;
                }
            }
            return BridgeResponse.Json(new
            {
                ownedInLoop = ownedTotal,
                owned,
                buildingsChecked = footprints.Count,
                objectsInLoop = total,
                buildings,
                objects = ids,
                note = "POST /debug/override-loop?demolish=true removes exactly these. PreventOverride objects whose position lies inside a top-level building's prefab geometry bounds (rotated): OverrideSystem and Anarchy's PreventOverrideSystem update them every frame. Remove them (/build/demolish-area) or re-import with options.skipInsideBuildings.",
            });
        }
    }
}
