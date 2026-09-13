using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.City;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Channels;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates dev-tree node purchases by detecting <see cref="DevTreeNodeData"/> unlocks
    /// and broadcasting <see cref="DevTreePurchaseCommand"/>. Applies remotely via
    /// <see cref="EndFrameBarrier"/> (load-bearing: must reach MainLoop, not UIUpdate),
    /// charges the host's <see cref="DevTreePoints"/> to stop refill-on-snapshot. Echo guard
    /// suppresses re-detecting applied unlocks.
    /// </summary>
    public partial class DevTreeSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly HashSet<string> _knownUnlocked = new HashSet<string>();
        private readonly Dictionary<string, Entity> _nodeByName = new Dictionary<string, Entity>();

        private PrefabSystem _prefabSystem;
        private DeferredPrefabUnlocker _unlocks;
        private EntityQuery _nodes;
        private EntityQuery _pointsQuery;
        private CommandObserver _observer;
        private bool _initialized;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _unlocks = new DeferredPrefabUnlocker(EntityManager);
            // DevTree nodes are prefab entities — IncludePrefab so the query finds them.
            _nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<DevTreeNodeData>(),
                None = SyncQuery.ReadOnly<Temp>(),
                Options = EntityQueryOptions.IncludePrefab,
            });
            _pointsQuery = GetEntityQuery(ComponentType.ReadWrite<DevTreePoints>());
            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, DevTreePurchaseCommand.Id));
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("DevTree"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    _initialized = false;
                    _unlocks.Reset();
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);
                _unlocks.PruneCompleted();

                // Apply remote purchases first so their unlocks are accounted for before we
                // diff for local ones.
                ApplyIncoming(session, now);

                // First ready tick: adopt the current unlocked set as the baseline so the
                // already-unlocked nodes from the loaded save are never re-broadcast.
                if (!_initialized)
                {
                    SeedKnown();
                    _initialized = true;
                    return;
                }

                DetectLocalPurchases(session, now);
            }
        }

        private bool IsLocked(Entity node) =>
            EntityManager.HasComponent<Locked>(node) && EntityManager.IsComponentEnabled<Locked>(node);

        private void SeedKnown()
        {
            _knownUnlocked.Clear();
            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (IsLocked(nodes[i])) continue;
                    string name = PrefabIndex.SafeName(_prefabSystem, nodes[i]);
                    if (!string.IsNullOrEmpty(name)) _knownUnlocked.Add(name);
                }
            }
            finally { nodes.Dispose(); }
        }

        private void DetectLocalPurchases(MultiplayerSession session, long now)
        {
            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    string name = PrefabIndex.SafeName(_prefabSystem, nodes[i]);
                    if (string.IsNullOrEmpty(name)) continue;

                    bool unlocked = !IsLocked(nodes[i]);
                    bool known = _knownUnlocked.Contains(name);

                    if (unlocked && !known)
                    {
                        _knownUnlocked.Add(name);
                        if (_guard.Consume(NodeKey(name), now)) continue; // we applied it — no echo

                        var command = new DevTreePurchaseCommand { NodePrefabName = name };
                        session.SendCommand(0, DevTreePurchaseCommand.Id, command.Encode());
                        SyncLog.Detail(LogTopic.City, "DevTreeSync: broadcast purchase of '" + name +
                            "'.");
                    }
                    else if (!unlocked && known)
                    {
                        // Re-locked (a world resync reloaded the host's state) — let a
                        // future unlock be detected again.
                        _knownUnlocked.Remove(name);
                    }
                }
            }
            finally { nodes.Dispose(); }
        }

        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                DevTreePurchaseCommand command;
                try { command = DevTreePurchaseCommand.Decode(message.Body); }
                catch (System.Exception ex) { SyncLog.Warn(LogTopic.City, "DevTreeSync: dropping malformed command: " + ex.Message); continue; }

                Entity node = ResolveNode(command.NodePrefabName);
                if (node == Entity.Null)
                {
                    SyncLog.Warn(LogTopic.City, "DevTreeSync: unknown node '" +
                        command.NodePrefabName + "' from player " + message.OriginPlayerId +
                        "; skipping.");
                    continue;
                }
                if (!IsLocked(node)) continue; // already unlocked here — nothing to do

                // Unlock the node everywhere so the partner's tree updates. Defer the event
                // to the EndFrameBarrier — creating it directly from UIUpdate would have it
                // reaped by CleanUpSystem this same frame, before UnlockSystem (MainLoop)
                // could process it. The barrier replays it at the next MainLoop where the
                // game's own unlock pipeline (node + dependent-content cascade) runs.
                if (!_unlocks.TryQueue(node)) continue;
                _guard.Mark(NodeKey(command.NodePrefabName), now);

                // Only the host owns the points: charge the node's cost so the authoritative
                // snapshot reflects the spend instead of refilling the buyer.
                if (session.Role == SessionRole.Host &&
                    EntityManager.HasComponent<DevTreeNodeData>(node) &&
                    !_pointsQuery.IsEmptyIgnoreFilter)
                {
                    int cost = EntityManager.GetComponentData<DevTreeNodeData>(node).m_Cost;
                    DevTreePoints points = _pointsQuery.GetSingleton<DevTreePoints>();
                    points.m_Points -= cost;
                    _pointsQuery.SetSingleton(points);
                }

                SyncLog.Detail(LogTopic.City, "DevTreeSync: applied purchase of '" +
                    command.NodePrefabName + "' from player " + message.OriginPlayerId + ".");
            }
        }

        private Entity ResolveNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return Entity.Null;

            Entity cached;
            if (_nodeByName.TryGetValue(name, out cached) && EntityManager.Exists(cached)) return cached;

            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    string candidate = PrefabIndex.SafeName(_prefabSystem, nodes[i]);
                    if (!string.IsNullOrEmpty(candidate)) _nodeByName[candidate] = nodes[i];
                }
            }
            finally { nodes.Dispose(); }

            return _nodeByName.TryGetValue(name, out cached) ? cached : Entity.Null;
        }

        private static string NodeKey(string name) => "devtree|" + name;

    }
}
