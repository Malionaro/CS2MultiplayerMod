using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using ObjectTransform = Game.Objects.Transform;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Puts the game's own hover outline on what a partner is pointing at, instead of drawing a box
    /// around it: an entity carrying <see cref="Highlighted"/> is drawn with an outline traced on
    /// its actual mesh. Nothing about the target travels but its geometry, so the local entity is
    /// found by rebuilding what the sender would have measured for each nearby candidate and keeping
    /// the one that matches. A partner pointing at something this city cannot match finds nothing,
    /// and no generic building box is substituted.
    /// </summary>
    public partial class RemotePlayerHighlightSystem : GameSystemBase
    {
        /// <summary>A hover older than this stops being shown, as in the marker system.</summary>
        private const long StaleAfterMs = 1500;

        /// <summary>
        /// Total mismatch an object may carry, in metres, over its footprint centre, both side
        /// lengths and its height. Both cities hold the same prefab and the same transform, so the
        /// right object scores about zero; the tolerance only has to survive a world that has
        /// drifted, where outlining the wrong building would be worse than outlining none.
        /// </summary>
        private const float MatchTolerance = 1.5f;

        /// <summary>
        /// Search radius around the received footprint centre. An object's pivot is not its
        /// footprint centre - asymmetric bounds put it metres away - and the tree is searched once
        /// per hovered target, not once per frame.
        /// </summary>
        private const float SearchRadius = 32f;

        /// <summary>
        /// Compare the full course, including height, so stacked roads and buried utilities
        /// cannot claim each other's highlight.
        /// </summary>
        private const float NetMatchTolerance = 3f;
        private const float NetSearchRadius = 24f;
        private const float NetSearchHeight = 200f;

        /// <summary>What a partner's hover is holding outlined here.</summary>
        private sealed class Held
        {
            /// <summary>What resolution matched. The rest of the street hangs off it.</summary>
            public Entity Target;
            /// <summary>The sender's own key and centre, to recognise the same target again.</summary>
            public int Key;
            public float3 Centre;
            public PlayerHoverShape Shape;
            public long RetryAtMs;
            /// <summary>Targets held by this player; shared highlights are reference counted.</summary>
            public readonly List<Entity> Marked = new List<Entity>();
        }

        private ObjectSearch _objects;
        private global::Game.Net.SearchSystem _nets;
        private NativeList<Entity> _candidates;
        private readonly List<Entity> _street = new List<Entity>();
        private readonly Dictionary<int, Held> _held = new Dictionary<int, Held>();
        private readonly List<int> _departed = new List<int>();
        private readonly Dictionary<Entity, int> _references = new Dictionary<Entity, int>();
        private readonly HashSet<Entity> _ownedHighlights = new HashSet<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _objects = new ObjectSearch(World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _nets = World.GetOrCreateSystemManaged<global::Game.Net.SearchSystem>();
            _candidates = new NativeList<Entity>(32, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            // Nothing is un-marked here: OnDestroy runs with the world being torn down, and every
            // entity holding a highlight goes with it.
            _held.Clear();
            _references.Clear();
            _ownedHighlights.Clear();
            if (_candidates.IsCreated) _candidates.Dispose();
            base.OnDestroy();
        }

        /// <summary>True while a partner's hover is shown by the game's outline rather than drawn.</summary>
        public bool HasNativeHighlight(int playerId)
        {
            Held held;
            return _held.TryGetValue(playerId, out held) && held.Target != Entity.Null;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("PartnerHover.Highlight"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady ||
                    (Mod.Setting != null && !Mod.Setting.ShowPartnerMarkers))
                {
                    ReleaseAll();
                    return;
                }

                long now = service.NowMs;
                foreach (RemotePlayer player in service.RemotePlayers)
                {
                    PlayerHoverShape shape;
                    if (now - player.LastUpdateMs > StaleAfterMs || !TryHoveredShape(player, out shape))
                    {
                        Hold(player.PlayerId, Entity.Null, 0, default(float3));
                        continue;
                    }

                    // The same target is re-sent ten times a second for as long as it is pointed
                    // at; searching the tree for each of those would put a job-system round trip in
                    // every frame to answer the same question.
                    float3 centre = Centre(shape);
                    Held held;
                    if (_held.TryGetValue(player.PlayerId, out held) && SameShape(held.Shape, shape) &&
                        (held.Target == Entity.Null ? now < held.RetryAtMs : Alive(held.Target)))
                    {
                        // Retry misses at a bounded cadence: a newly built target may arrive later.
                        if (held.Target != Entity.Null) Refresh(held);
                        continue;
                    }

                    Entity target = shape.Kind == PlayerHoverKind.Curve
                        ? ResolveNet(shape, centre)
                        : ResolveObject(shape, centre);
                    Hold(player.PlayerId, target, shape.Key, centre);
                    held = _held[player.PlayerId];
                    held.Shape = shape;
                    held.RetryAtMs = now + 500;
                }

                DropDeparted(service);
            }
        }

        /// <summary>What a partner is pointing at, as opposed to what they are about to build.</summary>
        private static bool TryHoveredShape(RemotePlayer player, out PlayerHoverShape shape)
        {
            shape = default(PlayerHoverShape);
            PlayerHoverShape[] shapes = player.Hover;
            if (shapes == null) return false;
            for (int i = 0; i < shapes.Length; i++)
            {
                if (shapes[i].Placement) continue;
                shape = shapes[i];
                return true;
            }
            return false;
        }

        /// <summary>
        /// The local object whose own footprint matches the received one. Candidates come from the
        /// game's static-object search tree rather than a walk of the object domain.
        /// </summary>
        private Entity ResolveObject(PlayerHoverShape shape, float3 centre)
        {
            float3 a = Vector(shape.A);
            bool circular = shape.Kind == PlayerHoverKind.Circle;

            _objects.CollectNear(centre, SearchRadius, _candidates);
            Entity best = Entity.Null;
            float bestScore = MatchTolerance;
            for (int i = 0; i < _candidates.Length; i++)
            {
                Entity candidate = _candidates[i];
                if (!Alive(candidate) || !EntityManager.HasComponent<ObjectTransform>(candidate) ||
                    !EntityManager.HasComponent<PrefabRef>(candidate)) continue;

                Entity prefab = EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;
                if (prefab == Entity.Null ||
                    !EntityManager.HasComponent<ObjectGeometryData>(prefab)) continue;
                ObjectGeometryData geometry = EntityManager.GetComponentData<ObjectGeometryData>(prefab);
                if (((geometry.m_Flags & global::Game.Objects.GeometryFlags.Circular) != 0) != circular)
                    continue;

                ObjectTransform transform = EntityManager.GetComponentData<ObjectTransform>(candidate);
                Bounds3 bounds = geometry.m_Bounds;
                float3 candidateBase = transform.m_Position + new float3(0f, bounds.min.y, 0f);
                float score;
                if (circular)
                {
                    score = math.distance(candidateBase, centre) + math.abs(
                        math.max(bounds.max.x - bounds.min.x, bounds.max.z - bounds.min.z) - shape.Width);
                }
                else
                {
                    Quad3 corners = global::Game.Objects.ObjectUtils.CalculateBaseCorners(
                        candidateBase, transform.m_Rotation, bounds);
                    score = (math.distance(corners.a, a) + math.distance(corners.b, Vector(shape.B)) +
                             math.distance(corners.c, Vector(shape.C)) + math.distance(corners.d, Vector(shape.D))) * 0.25f +
                            math.abs(bounds.max.y - bounds.min.y - shape.Height);
                }

                if (score >= bestScore) continue;
                bestScore = score;
                best = candidate;
            }
            return best;
        }

        /// <summary>
        /// Match the local course in either direction, including its control points and depth.
        /// The game's search tree supplies nearby candidates.
        /// </summary>
        private Entity ResolveNet(PlayerHoverShape shape, float3 centre)
        {
            float3 start = Vector(shape.A), end = Vector(shape.D);
            float3 controlB = Vector(shape.B), controlC = Vector(shape.C);

            JobHandle dependencies;
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                _nets.GetNetSearchTree(readOnly: true, out dependencies);
            // Read on the main thread; the marking that follows is a structural change anyway.
            dependencies.Complete();

            _candidates.Clear();
            var iterator = new NearNetIterator
            {
                Bounds = new Bounds3(
                    centre - new float3(NetSearchRadius, NetSearchHeight, NetSearchRadius),
                    centre + new float3(NetSearchRadius, NetSearchHeight, NetSearchRadius)),
                Results = _candidates,
            };
            tree.Iterate(ref iterator);

            Entity best = Entity.Null;
            float bestScore = NetMatchTolerance;
            for (int i = 0; i < _candidates.Length; i++)
            {
                Entity candidate = _candidates[i];
                if (!Alive(candidate) ||
                    !EntityManager.HasComponent<global::Game.Net.Curve>(candidate)) continue;

                Bezier4x3 curve =
                    EntityManager.GetComponentData<global::Game.Net.Curve>(candidate).m_Bezier;
                float score = math.min(
                    math.distance(curve.a, start) + math.distance(curve.b, controlB) +
                    math.distance(curve.c, controlC) + math.distance(curve.d, end),
                    math.distance(curve.a, end) + math.distance(curve.b, controlC) +
                    math.distance(curve.c, controlB) + math.distance(curve.d, start));
                if (score >= bestScore) continue;
                bestScore = score;
                best = candidate;
            }
            return best;
        }

        /// <summary>
        /// Moves a partner's highlight onto <paramref name="target"/>, releasing whatever it was on.
        /// </summary>
        private void Hold(int playerId, Entity target, int key, float3 centre)
        {
            Held held;
            if (_held.TryGetValue(playerId, out held))
            {
                if (target == Entity.Null)
                {
                    held.Shape = default;
                    held.RetryAtMs = 0;
                }
                if (held.Target == target)
                {
                    held.Key = key;
                    held.Centre = centre;
                    Refresh(held);
                    return;
                }
                Release(held);
            }

            if (held == null)
            {
                held = new Held();
                _held.Add(playerId, held);
            }
            held.Target = target;
            held.Key = key;
            held.Centre = centre;
            if (target != Entity.Null) Mark(held);
        }

        /// <summary>
        /// Re-marks a hold whose highlight has gone: a tool clearing its own highlights takes any
        /// on the same entity with it, and one this system found already highlighted loses it the
        /// moment the local player points somewhere else.
        /// </summary>
        private void Refresh(Held held)
        {
            for (int i = 0; i < held.Marked.Count; i++) EnsureHighlighted(held.Marked[i]);
        }

        private void EnsureHighlighted(Entity entity)
        {
            if (!Alive(entity) || EntityManager.HasComponent<Highlighted>(entity)) return;
            EntityManager.AddComponent<Highlighted>(entity);
            EntityManager.AddComponent<BatchesUpdated>(entity);
            _ownedHighlights.Add(entity);
        }

        /// <summary>
        /// Outlines the target - and, for a road, the whole street it belongs to, which is what the
        /// game's own hover covers.
        /// </summary>
        private void Mark(Held held)
        {
            Release(held);
            _street.Clear();
            _street.Add(held.Target);
            CollectStreet(held.Target, _street);

            for (int i = 0; i < _street.Count; i++)
            {
                Entity entity = _street[i];
                if (!Alive(entity) || held.Marked.Contains(entity)) continue;
                _references.TryGetValue(entity, out int references);
                _references[entity] = references + 1;
                held.Marked.Add(entity);
                EnsureHighlighted(entity);
            }
        }

        /// <summary>
        /// The other edges of the target's street, read out before anything is marked: adding a
        /// component moves chunks, which would leave the buffer behind it invalid.
        /// </summary>
        private void CollectStreet(Entity target, List<Entity> results)
        {
            if (!EntityManager.HasComponent<global::Game.Net.Aggregated>(target)) return;
            Entity aggregate =
                EntityManager.GetComponentData<global::Game.Net.Aggregated>(target).m_Aggregate;
            if (aggregate == Entity.Null || !EntityManager.Exists(aggregate) ||
                !EntityManager.HasBuffer<global::Game.Net.AggregateElement>(aggregate)) return;

            DynamicBuffer<global::Game.Net.AggregateElement> elements =
                EntityManager.GetBuffer<global::Game.Net.AggregateElement>(aggregate, true);
            for (int i = 0; i < elements.Length; i++)
            {
                Entity edge = elements[i].m_Edge;
                if (edge != Entity.Null && edge != target) results.Add(edge);
            }
        }

        private void Release(Held held)
        {
            for (int i = 0; i < held.Marked.Count; i++)
            {
                Entity entity = held.Marked[i];
                if (!_references.TryGetValue(entity, out int references)) continue;
                if (references > 1)
                {
                    _references[entity] = references - 1;
                    continue;
                }
                _references.Remove(entity);
                if (!_ownedHighlights.Remove(entity) || !EntityManager.Exists(entity)) continue;
                if (EntityManager.HasComponent<Highlighted>(entity))
                    EntityManager.RemoveComponent<Highlighted>(entity);
                if (!EntityManager.HasComponent<Deleted>(entity))
                    EntityManager.AddComponent<BatchesUpdated>(entity);
            }
            held.Marked.Clear();
        }

        private void ReleaseAll()
        {
            if (_held.Count == 0) return;
            foreach (KeyValuePair<int, Held> entry in _held) Release(entry.Value);
            _held.Clear();
        }

        /// <summary>A player who left the session stops turning up in the loop above.</summary>
        private void DropDeparted(MultiplayerService service)
        {
            if (_held.Count == 0) return;
            foreach (KeyValuePair<int, Held> entry in _held)
            {
                bool present = false;
                foreach (RemotePlayer player in service.RemotePlayers)
                    if (player.PlayerId == entry.Key) { present = true; break; }
                if (!present) _departed.Add(entry.Key);
            }

            for (int i = 0; i < _departed.Count; i++)
            {
                Release(_held[_departed[i]]);
                _held.Remove(_departed[i]);
            }
            _departed.Clear();
        }

        private static bool SameShape(PlayerHoverShape a, PlayerHoverShape b) =>
            a.Kind == b.Kind && a.Key == b.Key && a.Placement == b.Placement &&
            math.abs(a.Width - b.Width) < 0.01f && math.abs(a.Height - b.Height) < 0.01f &&
            math.distancesq(Vector(a.A), Vector(b.A)) < 0.0001f &&
            math.distancesq(Vector(a.B), Vector(b.B)) < 0.0001f &&
            math.distancesq(Vector(a.C), Vector(b.C)) < 0.0001f &&
            math.distancesq(Vector(a.D), Vector(b.D)) < 0.0001f;

        private bool Alive(Entity entity) =>
            entity != Entity.Null && EntityManager.Exists(entity) &&
            !EntityManager.HasComponent<Deleted>(entity) && !EntityManager.HasComponent<Temp>(entity);

        /// <summary>The centre a shape was measured around.</summary>
        private static float3 Centre(PlayerHoverShape shape) =>
            shape.Kind == PlayerHoverKind.Circle
                ? Vector(shape.A)
                : (Vector(shape.A) + Vector(shape.B) + Vector(shape.C) + Vector(shape.D)) * 0.25f;

        private static float3 Vector(HoverPoint point) => new float3(point.X, point.Y, point.Z);

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
