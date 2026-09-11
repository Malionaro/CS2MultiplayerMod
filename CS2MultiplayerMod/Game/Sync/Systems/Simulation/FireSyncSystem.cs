using System.Collections.Concurrent;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates the *start* of a building or tree fire - and nothing else. One
    /// <see cref="FireIgniteCommand"/> per ignition carries what the receiving game cannot
    /// derive for itself (which target, how hard); every machine then runs the burn with
    /// its own simulation (escalation, spread, rescue, extinguish). Streaming the burn
    /// would put a message on the wire every dozen frames for as long as anything smoulders.
    ///
    /// Both sides capture and both sides realize, so two cities converge on the union of
    /// their fires. That differs from disasters on purpose: disaster rolls can be switched
    /// off on clients, but there is no safe switch for ignition alone - disabling the
    /// ignite pipeline would also pile up the spread requests the local burn keeps
    /// producing. Ends stay local on both sides: each simulation extinguishes on its own
    /// clock, and the damage left behind is already host-authoritative through the
    /// growable condition sync.
    ///
    /// Realization never creates an ignite event, it adds <c>OnFire</c> to the matched
    /// target the way the game's own ignite pipeline would. A replica therefore never
    /// shows up in the capture query and no echo guard is needed: receiving the same
    /// start twice finds the target already burning and skips it silently.
    /// </summary>
    public partial class FireSyncSystem : GameSystemBase
    {
        /// <summary>Ignitions arrive rarely; a per-frame cap keeps a wildfire night from stalling a frame.</summary>
        private const int MaxRealizePerFrame = 4;

        /// <summary>Target match tolerance, squared metres (2 m): buildings do not move.</summary>
        private const float MatchTolSq = 4f;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private SimulationSystem _simulation;
        private EntityQuery _createdIgnites;
        private EntityQuery _liveTargets;
        private CommandObserver _observer;
        private long _skippedTargets;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();

            _createdIgnites = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event,
                    global::Game.Events.Ignite, Created>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            _liveTargets = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<PrefabRef, global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, FireIgniteCommand.Id)
                    {
                        MaxBodyBytes = FireIgniteCommand.MaxEncodedBytes,
                    },
                DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            SyncObserverBinding.Unbind(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("FireSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady) return;

                CaptureIgnites(service.Session);
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate, next to disasters:
        /// a fire is a plain simulation state change, no definitions and no terrain involved.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady)
            {
                SyncInbox.Clear(_incoming);
                return;
            }

            MultiplayerSession session = service.Session;
            int realized = 0;
            SimulationCommandMessage message;
            while (realized < MaxRealizePerFrame && _incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                FireIgniteCommand command;
                try { command = FireIgniteCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.City, "FireSync: dropping malformed command: " +
                        ex.Message);
                    continue;
                }

                if (Realize(command, message.OriginPlayerId)) realized++;
            }
        }

        // ---- Capture ------------------------------------------------------------

        private void CaptureIgnites(MultiplayerSession session)
        {
            if (_createdIgnites.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> events = _createdIgnites.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    Entity entity = events[i];
                    global::Game.Events.Ignite ignite =
                        EntityManager.GetComponentData<global::Game.Events.Ignite>(entity);
                    Entity target = ignite.m_Target;

                    if (target == Entity.Null || !EntityManager.Exists(target))
                    {
                        Skip("untargeted");
                        continue;
                    }
                    if (!EntityManager.HasComponent<PrefabRef>(target))
                    {
                        // The game's own ignite pipeline requires PrefabRef on the target
                        // too, so this request dies locally as well - nothing to replicate.
                        Skip("target without prefab");
                        continue;
                    }
                    if (EntityManager.HasComponent<global::Game.Vehicles.Vehicle>(target))
                    {
                        // Vehicles move: no stable identity to match on the receiver, and
                        // vehicles simulate locally on every machine anyway.
                        Skip("vehicle target");
                        continue;
                    }
                    if (!EntityManager.HasComponent<global::Game.Objects.Transform>(target))
                    {
                        Skip("target without position");
                        continue;
                    }

                    Entity targetPrefab =
                        EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
                    string prefabName = _prefabIndex.NameOf(targetPrefab);
                    if (string.IsNullOrEmpty(prefabName))
                    {
                        Skip("unresolvable target prefab");
                        continue;
                    }
                    float3 position = EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(target).m_Position;

                    var command = new FireIgniteCommand
                    {
                        PrefabName = prefabName,
                        X = position.x,
                        Y = position.y,
                        Z = position.z,
                        Intensity = math.clamp(ignite.m_Intensity, 0f,
                            FireIgniteCommand.MaxIntensityValue),
                    };
                    try
                    {
                        session.SendCommand(0, FireIgniteCommand.Id, command.Encode());
                    }
                    catch (System.Exception ex)
                    {
                        SyncLog.Warn(LogTopic.City, "FireSync: refusing to send ignite of '" +
                            prefabName + "': " + ex.Message);
                        continue;
                    }
                    SyncLog.Detail(LogTopic.City, "FireSync sent ignite of '" + prefabName +
                        "' at " + position + ", intensity " + command.Intensity + ".");
                }
            }
            finally
            {
                events.Dispose();
            }
        }

        private void Skip(string reason)
        {
            _skippedTargets++;
            SyncLog.Detail(LogTopic.City, "FireSync: not replicating ignite with " + reason +
                " (total skipped this session: " + _skippedTargets + ").");
        }

        // ---- Realize ------------------------------------------------------------

        private bool Realize(FireIgniteCommand command, int originPlayerId)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                SyncLog.Warn(LogTopic.City, "FireSync: no local prefab named '" +
                    command.PrefabName + "'; ignoring the ignition.");
                return false;
            }

            float3 target = new float3(command.X, command.Y, command.Z);
            Entity best = Entity.Null;
            float bestDistSq = MatchTolSq;

            NativeArray<Entity> candidates = _liveTargets.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab)
                        continue;
                    float3 position = EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(candidate).m_Position;
                    float distSq = math.distancesq(position, target);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = candidate;
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }

            if (best == Entity.Null)
            {
                SyncLog.Warn(LogTopic.City, "FireSync: no local '" + command.PrefabName +
                    "' near " + target + "; ignoring the ignition.");
                return false;
            }
            if (EntityManager.HasComponent<global::Game.Events.OnFire>(best))
            {
                // Already burning here - either our own simulation got there first or this
                // is the echo of a start both sides rolled. Either way there is nothing to do.
                return true;
            }

            // What the game's ignite pipeline would have installed: the burn state plus the
            // batch marker it uses to let installed upgrades react. Rescue requests, icons
            // and journal entries derive from the running burn on this machine.
            EntityManager.AddComponentData(best, new global::Game.Events.OnFire
            {
                m_Intensity = command.Intensity,
                m_RequestFrame = _simulation.frameIndex,
            });
            EntityManager.AddComponent<BatchesUpdated>(best);
            if (EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(best))
            {
                DynamicBuffer<global::Game.Buildings.InstalledUpgrade> upgrades =
                    EntityManager.GetBuffer<global::Game.Buildings.InstalledUpgrade>(best);
                for (int i = 0; i < upgrades.Length; i++)
                    if (EntityManager.Exists(upgrades[i].m_Upgrade))
                        EntityManager.AddComponent<BatchesUpdated>(upgrades[i].m_Upgrade);
            }

            SyncLog.Detail(LogTopic.City, "FireSync realized ignite of '" + command.PrefabName +
                "' at " + target + " from player " + originPlayerId + ".");
            return true;
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
        }
    }
}
