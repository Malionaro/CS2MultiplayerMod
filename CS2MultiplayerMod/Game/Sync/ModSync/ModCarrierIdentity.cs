using Colossal.Collections;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Sync.ModSync;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// Says what an entity is in terms both machines share, and finds it again from that.
    ///
    /// An entity index is a position in one process's arrays; it means something else in another,
    /// and a client keeps creating entities of its own for as long as it is connected, so the two
    /// drift apart from the first frame. What both machines do agree on is the city: a junction is
    /// at a place, a road runs between two junctions, a building stands on a spot and was placed
    /// from a named prefab. Everything that crosses the wire is described that way and looked up
    /// through the game's own search trees, which is also what keeps the lookup off the whole-city
    /// walk that has cost this mod frames before.
    /// </summary>
    internal sealed class ModCarrierIdentity
    {
        /// <summary>
        /// How far a lookup may stray from the transmitted position. Both machines' worlds came
        /// from the same savegame, so this covers float noise and a node that a local edit nudged -
        /// not a search for something plausible nearby.
        /// </summary>
        private const float ToleranceXZ = 3f;

        /// <summary>Vertical slack. Generous, because elevation is re-derived on each machine.</summary>
        private const float ToleranceY = 12f;

        private readonly EntityManager _entities;
        private readonly PrefabSystem _prefabs;
        private readonly global::Game.Net.SearchSystem _netSearch;
        private readonly ObjectSearch _objectSearch;
        private readonly PrefabIndex _prefabIndex;

        public ModCarrierIdentity(EntityManager entities, PrefabSystem prefabs,
            global::Game.Net.SearchSystem netSearch, ObjectSearch objectSearch,
            PrefabIndex prefabIndex)
        {
            _entities = entities;
            _prefabs = prefabs;
            _netSearch = netSearch;
            _objectSearch = objectSearch;
            _prefabIndex = prefabIndex;
        }

        /// <summary>
        /// Describes <paramref name="entity"/> portably, or fails when it has no place in the world
        /// - which is how an entity a mod made purely for its own bookkeeping is recognised.
        /// </summary>
        public bool TryDescribe(Entity entity, out ModEntityRef reference)
        {
            reference = ModEntityRef.Null;
            if (!ModEntityEligibility.IsLive(_entities, entity)) return false;

            if (_entities.HasComponent<global::Game.Net.Node>(entity))
            {
                float3 position = _entities.GetComponentData<global::Game.Net.Node>(entity).m_Position;
                reference = ModEntityRef.Node(position.x, position.y, position.z);
                return true;
            }

            if (_entities.HasComponent<global::Game.Net.Edge>(entity))
            {
                global::Game.Net.Edge edge = _entities.GetComponentData<global::Game.Net.Edge>(entity);
                float3 start, end;
                if (!TryNodePosition(edge.m_Start, out start)) return false;
                if (!TryNodePosition(edge.m_End, out end)) return false;
                reference = ModEntityRef.Edge(start.x, start.y, start.z, end.x, end.y, end.z);
                return true;
            }

            if (_entities.HasComponent<global::Game.Objects.Transform>(entity) &&
                _entities.HasComponent<PrefabRef>(entity))
            {
                float3 position =
                    _entities.GetComponentData<global::Game.Objects.Transform>(entity).m_Position;
                Entity prefab = _entities.GetComponentData<PrefabRef>(entity).m_Prefab;
                string name = PrefabIndex.SafeName(_prefabs, prefab);
                if (string.IsNullOrEmpty(name)) return false;
                reference = ModEntityRef.Object(position.x, position.y, position.z, name);
                return true;
            }

            if (_entities.HasComponent<PrefabData>(entity))
            {
                string name = PrefabIndex.SafeName(_prefabs, entity);
                if (string.IsNullOrEmpty(name)) return false;
                reference = ModEntityRef.Prefab(name);
                return true;
            }

            return false;
        }

        /// <summary>Finds what a description names here, or reports that it is not here yet.</summary>
        public bool TryResolve(ModEntityRef reference, out Entity entity)
        {
            entity = Entity.Null;
            switch (reference.Kind)
            {
                case ModRefKind.NetNode:
                    return TryFindNode(new float3(reference.X, reference.Y, reference.Z), out entity);

                case ModRefKind.NetEdge:
                    return TryFindEdge(
                        new float3(reference.X, reference.Y, reference.Z),
                        new float3(reference.X2, reference.Y2, reference.Z2), out entity);

                case ModRefKind.Object:
                    return TryFindObject(new float3(reference.X, reference.Y, reference.Z),
                        reference.Name, out entity);

                case ModRefKind.Prefab:
                    return _prefabIndex.TryResolve(reference.Name, out entity);

                default:
                    return false;
            }
        }

        private bool TryNodePosition(Entity node, out float3 position)
        {
            position = default(float3);
            if (node == Entity.Null || !_entities.Exists(node)) return false;
            if (!_entities.HasComponent<global::Game.Net.Node>(node)) return false;
            position = _entities.GetComponentData<global::Game.Net.Node>(node).m_Position;
            return true;
        }

        private bool TryFindNode(float3 wanted, out Entity found)
        {
            found = Entity.Null;
            float best = float.MaxValue;

            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                CollectNets(wanted, candidates);
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!IsLiveNet(candidate)) continue;
                    if (!_entities.HasComponent<global::Game.Net.Node>(candidate)) continue;

                    float3 position =
                        _entities.GetComponentData<global::Game.Net.Node>(candidate).m_Position;
                    if (math.abs(position.y - wanted.y) > ToleranceY) continue;
                    float distance = math.distancesq(position.xz, wanted.xz);
                    if (distance > ToleranceXZ * ToleranceXZ || distance >= best) continue;

                    best = distance;
                    found = candidate;
                }
            }
            finally
            {
                candidates.Dispose();
            }
            return found != Entity.Null;
        }

        /// <summary>
        /// An edge is found through its own start node rather than by searching for a curve: the
        /// two nodes fix it exactly, and the node already carries the list of edges leaving it, so
        /// this never has to decide which of several overlapping curves was meant.
        /// </summary>
        private bool TryFindEdge(float3 start, float3 end, out Entity found)
        {
            found = Entity.Null;

            Entity startNode;
            if (!TryFindNode(start, out startNode)) return false;
            Entity endNode;
            if (!TryFindNode(end, out endNode)) return false;
            if (!_entities.HasBuffer<global::Game.Net.ConnectedEdge>(startNode)) return false;

            DynamicBuffer<global::Game.Net.ConnectedEdge> connected =
                _entities.GetBuffer<global::Game.Net.ConnectedEdge>(startNode, true);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity edge = connected[i].m_Edge;
                if (!IsLiveNet(edge) || !_entities.HasComponent<global::Game.Net.Edge>(edge)) continue;

                global::Game.Net.Edge ends = _entities.GetComponentData<global::Game.Net.Edge>(edge);

                // Either orientation: which node a mod called the start is its own business, and an
                // edge redrawn the other way round is still the same road.
                if ((ends.m_Start == startNode && ends.m_End == endNode) ||
                    (ends.m_Start == endNode && ends.m_End == startNode))
                {
                    found = edge;
                    return true;
                }
            }
            return false;
        }

        private bool TryFindObject(float3 wanted, string prefabName, out Entity found)
        {
            found = Entity.Null;
            if (_objectSearch == null) return false;

            float best = float.MaxValue;
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(wanted, ToleranceXZ, candidates);
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!_entities.Exists(candidate)) continue;
                    if (_entities.HasComponent<Temp>(candidate) ||
                        _entities.HasComponent<Deleted>(candidate)) continue;
                    if (!_entities.HasComponent<global::Game.Objects.Transform>(candidate) ||
                        !_entities.HasComponent<PrefabRef>(candidate)) continue;

                    Entity prefab = _entities.GetComponentData<PrefabRef>(candidate).m_Prefab;
                    if (PrefabIndex.SafeName(_prefabs, prefab) != prefabName) continue;

                    float3 position = _entities
                        .GetComponentData<global::Game.Objects.Transform>(candidate).m_Position;
                    float distance = math.distancesq(position, wanted);
                    if (distance >= best) continue;

                    best = distance;
                    found = candidate;
                }
            }
            finally
            {
                candidates.Dispose();
            }
            return found != Entity.Null;
        }

        private bool IsLiveNet(Entity entity)
        {
            // The search tree is only as fresh as its last update, so existence comes first and
            // preview/deleted entities are never a legitimate answer.
            return entity != Entity.Null && _entities.Exists(entity) &&
                   !_entities.HasComponent<Temp>(entity) &&
                   !_entities.HasComponent<Deleted>(entity);
        }

        private void CollectNets(float3 around, NativeList<Entity> results)
        {
            Unity.Jobs.JobHandle dependencies;
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                _netSearch.GetNetSearchTree(true, out dependencies);

            // Read on the main thread; the caller writes structurally straight afterwards.
            dependencies.Complete();

            var extent = new float3(ToleranceXZ, ToleranceY, ToleranceXZ);
            var iterator = new NearNetIterator
            {
                Bounds = new Bounds3(around - extent, around + extent),
                Results = results,
            };
            tree.Iterate(ref iterator);
        }

        private struct NearNetIterator :
            INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>,
            IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds3 Bounds;
            public NativeList<Entity> Results;

            public bool Intersect(QuadTreeBoundsXZ bounds) =>
                MathUtils.Intersect(bounds.m_Bounds, Bounds);

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (MathUtils.Intersect(bounds.m_Bounds, Bounds)) Results.Add(item);
            }
        }
    }
}
