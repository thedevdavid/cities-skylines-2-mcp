using System.Collections.Generic;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.59: owned objects caught in the Overridden loop (OverrideSystem adds Overridden + Updated,
    /// Anarchy's RemoveOverridenSystem removes it + Updated) flip within a frame, so one read rarely
    /// sees them (A23c: a snapshot caught 26 of a visible ring of ~130). Sampled every frame from two
    /// phases (ToolUpdate via BridgeTmpeImportSystem, UIUpdate via BridgeSystem); an entity counts as
    /// looping when it was seen with Overridden or Updated within the last kWindow samples.
    /// </summary>
    internal static class OverrideSampler
    {
        private const int kWindow = 240;

        private static readonly Dictionary<Entity, int> s_LastSeen = new Dictionary<Entity, int>();
        private static EntityQuery s_Query;
        private static bool s_QueryCreated;
        private static int s_Sample;

        public static void Sample(EntityManager entityManager)
        {
            if (!s_QueryCreated)
            {
                s_Query = entityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Owner>() },
                    Any = new[] { ComponentType.ReadOnly<Overridden>(), ComponentType.ReadOnly<Updated>() },
                    None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
                });
                s_QueryCreated = true;
            }
            s_Sample++;
            if (!s_Query.IsEmptyIgnoreFilter)
            {
                using (NativeArray<Entity> entities = s_Query.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity e in entities)
                    {
                        s_LastSeen[e] = s_Sample;
                    }
                }
            }
            if (s_Sample % kWindow == 0 && s_LastSeen.Count > 0)
            {
                var stale = new List<Entity>();
                foreach (KeyValuePair<Entity, int> pair in s_LastSeen)
                {
                    if (s_Sample - pair.Value > kWindow)
                    {
                        stale.Add(pair.Key);
                    }
                }
                foreach (Entity e in stale)
                {
                    s_LastSeen.Remove(e);
                }
            }
        }

        /// <summary>Seen Overridden or Updated within the last kWindow samples (~2 s at 60 fps, two samples per frame).</summary>
        public static bool IsLooping(Entity e)
        {
            return s_LastSeen.TryGetValue(e, out int seen) && s_Sample - seen <= kWindow;
        }

        /// <summary>Entities currently counted as looping.</summary>
        public static List<Entity> Looping()
        {
            var list = new List<Entity>();
            foreach (KeyValuePair<Entity, int> pair in s_LastSeen)
            {
                if (s_Sample - pair.Value <= kWindow)
                {
                    list.Add(pair.Key);
                }
            }
            return list;
        }

        public static int Samples => s_Sample;
    }
}
