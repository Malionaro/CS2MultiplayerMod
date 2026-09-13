using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class NetJunctionRefresh
    {
        /// <summary>
        /// Edge lane regeneration resets endpoint PathNodes. LaneReferencesSystem restores the
        /// shared paths through short/skipped junction lanes only for Updated nodes. An edge-only
        /// refresh must therefore include both endpoint nodes in the same modification cycle.
        /// </summary>
        public static int Refresh(EntityManager em, NativeArray<Edge> edges, HashSet<Entity> nodes)
        {
            nodes.Clear();
            for (int i = 0; i < edges.Length; i++)
            {
                Collect(em, edges[i].m_Start, nodes);
                Collect(em, edges[i].m_End, nodes);
            }

            // Collect before changing archetypes. The caller supplies copied Edge values, and
            // no dynamic buffer or component view survives the structural changes below.
            foreach (Entity node in nodes) em.AddComponent<Updated>(node);
            return nodes.Count;
        }

        private static void Collect(EntityManager em, Entity node, HashSet<Entity> nodes)
        {
            if (node == Entity.Null || !em.Exists(node) || !em.HasComponent<Node>(node) ||
                em.HasComponent<Temp>(node) || em.HasComponent<Deleted>(node) ||
                em.HasComponent<Disabled>(node) || em.HasComponent<Updated>(node)) return;
            nodes.Add(node);
        }
    }
}
