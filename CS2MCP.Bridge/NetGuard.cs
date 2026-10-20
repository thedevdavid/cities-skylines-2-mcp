using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.24: watches the network while a non-network import runs (objects, surfaces).
    /// Those imports should never remove a segment; A-v4 lost 309 imported segments
    /// (mostly in chains under the imported field, sand and pavement surfaces) at an
    /// unknown step. A snapshot of every segment before each batch is compared after the
    /// batch is applied, so the report names the batch (and its item ids) that lost any.
    /// </summary>
    public sealed class NetGuard
    {
        private struct Seg
        {
            public Entity Prefab;
            public float2 Mid;
        }

        private readonly EntityManager m_EntityManager;
        private readonly EntityQuery m_EdgeQuery;
        private Dictionary<Entity, Seg> m_Before;
        public int SegmentsAtStart;
        public int SegmentsAtEnd;
        public readonly List<object> Losses = new List<object>();
        public int Lost;
        public int GeometryGone;

        public NetGuard(EntityManager entityManager, EntityQuery edgeQuery)
        {
            m_EntityManager = entityManager;
            m_EdgeQuery = edgeQuery;
            m_Before = Snapshot();
            SegmentsAtStart = m_Before.Count;
        }

        private Dictionary<Entity, Seg> Snapshot()
        {
            var map = new Dictionary<Entity, Seg>();
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in edges)
                {
                    Curve curve = m_EntityManager.GetComponentData<Curve>(e);
                    map[e] = new Seg
                    {
                        Prefab = m_EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                        Mid = MathUtils.Position(curve.m_Bezier, 0.5f).xz,
                    };
                }
            }
            return map;
        }

        /// <summary>After a batch is applied: records segments that disappeared, then re-snapshots.</summary>
        public void AfterBatch(string batchLabel, List<string> itemIds, Func<Entity, string> prefabName)
        {
            Dictionary<Entity, Seg> now = Snapshot();
            var gone = new List<KeyValuePair<Entity, Seg>>();
            foreach (KeyValuePair<Entity, Seg> pair in m_Before)
            {
                if (!now.ContainsKey(pair.Key))
                {
                    gone.Add(pair);
                }
            }
            if (gone.Count > 0)
            {
                // A segment replaced in place (split, merge) leaves a segment near its midpoint.
                var samples = new List<object>();
                int geometryGone = 0;
                foreach (KeyValuePair<Entity, Seg> g in gone)
                {
                    bool replaced = false;
                    foreach (Seg s in now.Values)
                    {
                        if (math.distancesq(s.Mid, g.Value.Mid) < 36f)
                        {
                            replaced = true;
                            break;
                        }
                    }
                    if (!replaced)
                    {
                        geometryGone++;
                    }
                    if (samples.Count < 50)
                    {
                        samples.Add(new
                        {
                            segment = $"{g.Key.Index}:{g.Key.Version}",
                            prefab = prefabName(g.Value.Prefab),
                            x = g.Value.Mid.x,
                            z = g.Value.Mid.y,
                            replacedInPlace = replaced,
                        });
                    }
                }
                Lost += gone.Count;
                GeometryGone += geometryGone;
                if (Losses.Count < 200)
                {
                    Losses.Add(new
                    {
                        batch = batchLabel,
                        items = itemIds.Count > 20 ? itemIds.GetRange(0, 20) : itemIds,
                        itemCount = itemIds.Count,
                        segmentsRemoved = gone.Count,
                        geometryGone,
                        samples,
                    });
                }
            }
            m_Before = now;
        }

        public object Report()
        {
            SegmentsAtEnd = m_Before.Count;
            return new
            {
                segmentsAtStart = SegmentsAtStart,
                segmentsAtEnd = SegmentsAtEnd,
                segmentsRemoved = Lost,
                geometryGone = GeometryGone,
                verdict = Lost == 0 ? "no segment was removed during this import"
                    : "SEGMENTS WERE REMOVED during this import; 'batches' names the batch and its item ids",
                batches = Losses,
            };
        }
    }

    /// <summary>
    /// Anarchy's object protection, by reflection (Anarchy is optional). Anarchy's
    /// AnarchyPlopSystem adds Anarchy.Components.PreventOverride to top-level static
    /// objects applied while its toggle is on; PreventOverrideSystem then strips the game's
    /// Overridden flag (set when a net or area overlaps the object, which hides it) and
    /// marks the object Updated, so overlapped objects stay visible. It applies to objects
    /// only: on game load AnarchyPlopSystem removes PreventOverride from everything that is
    /// not such an object, so it cannot protect net segments.
    /// </summary>
    public static class AnarchyProtection
    {
        private static bool s_Resolved;
        private static ComponentType s_PreventOverride;
        private static bool s_Available;

        public static bool Available
        {
            get
            {
                Resolve();
                return s_Available;
            }
        }

        private static void Resolve()
        {
            if (s_Resolved)
            {
                return;
            }
            s_Resolved = true;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType("Anarchy.Components.PreventOverride", false);
                if (type != null)
                {
                    s_PreventOverride = ComponentType.ReadWrite(type);
                    s_Available = true;
                    return;
                }
            }
        }

        /// <summary>Adds PreventOverride to a top-level static object; returns true when added.</summary>
        public static bool Protect(EntityManager entityManager, Entity entity)
        {
            Resolve();
            if (!s_Available || !entityManager.Exists(entity) || entityManager.HasComponent<Owner>(entity)
                || entityManager.HasComponent<Game.Buildings.Building>(entity) || !entityManager.HasComponent<Game.Objects.Static>(entity)
                || entityManager.HasComponent(entity, s_PreventOverride))
            {
                return false;
            }
            entityManager.AddComponent(entity, s_PreventOverride);
            return true;
        }
    }
}
