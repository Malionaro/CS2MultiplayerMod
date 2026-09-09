using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Applying the host's zoned-building lifecycle: a building spawns, changes level, is removed,
    // or has its condition and state corrected. A correction that arrives before the building does
    // is held and retried rather than dropped.
    //
    // Finding the building a command refers to is in RealizeMatch.cs; keeping a client from
    // growing its own is in RealizeGuard.cs.
    public partial class GrowableSyncSystem
    {
        /// <summary>Zone cells are 8 m; a lot's half-extent is therefore its cell count times four.</summary>
        private const float ZoneCellSize = 8f;

        /// <summary>
        /// Slack on the overlap test, in metres. Two buildings that merely touch along a shared lot
        /// edge are not in conflict - that is how a street of houses is meant to look.
        /// </summary>
        private const float OverlapTolerance = 0.5f;

        /// <summary>How far from the anchor an existing building may be and still be the same one.</summary>
        private const float AnchorMatchDistance = 0.5f;

        private const float AnchorSearchRadius = 8f;

        /// <summary>
        /// Applies the host's zoned-building decisions. Called from <see cref="SyncRealizeSystem"/>
        /// during ToolUpdate, the only phase in which a creation definition becomes a building.
        /// </summary>
        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> on frames this system is not allowed to run -
        /// terrain is catching up, or the net pipeline still has placements queued.
        ///
        /// A pending state correction's window is for waiting on its building to be generated, not
        /// for waiting on permission to look for it. Against the wall it expired while this system
        /// was gated off, and expiring asks for a world reload over a correction that was never
        /// once attempted. A stalled net placement holds this gate for its whole retry window.
        /// </summary>
        public void NotifyRealizeHeld(long nowMs)
        {
            ExtendPendingStateWindows(nowMs);
        }

        /// <summary>When this system last got to attempt its pending corrections.</summary>
        private long _lastGrowableRealizeMs;

        private void ExtendPendingStateWindows(long nowMs)
        {
            long heldMs = _lastGrowableRealizeMs == 0 ? 0 : nowMs - _lastGrowableRealizeMs;
            _lastGrowableRealizeMs = nowMs;
            if (heldMs <= 0) return;
            for (int i = 0; i < _pendingStateCorrections.Count; i++)
                _pendingStateCorrections[i].Expiry += heldMs;
        }

        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady) { ExtendPendingStateWindows(service.NowMs); return; }

            MultiplayerSession session = service.Session;
            long now = service.NowMs;

            // A host authors these; it must never apply one. Guards against a client that forges
            // the command as much as against a relay that echoes it back.
            if (session.Role == SessionRole.Host)
            {
                _incoming.Clear();
                return;
            }

            // A zoned building's transmitted height was sampled on the sender's terrain. Realizing
            // it while remote terraforming is still backlogged buries or floats it.
            if (DeferForTerrain) { ExtendPendingStateWindows(now); return; }
            _lastGrowableRealizeMs = now;

            _applied.Prune(now);
            RetryPendingStateCorrections(now);

            int realized = 0, states = 0;
            // Bound attempts, including rejected/duplicate work: lookups and validation
            // consume frame time even when no entity is changed.
            GrowableLifecycleCommand command;
            while (_incoming.TryTake(realized < MaxRealizePerFrame,
                       states < Infrastructure.GrowableCommandInbox.StateBudgetPerFrame, out command))
            {
                if (command.Op == GrowableLifecycleCommand.OpState) states++;
                else realized++;

                if (_applied.Contains(command.Sequence, now))
                {
                    _duplicates++;
                    SyncLog.Detail(LogTopic.Buildings, "GrowableSync: ignoring duplicate " +
                        GrowableLifecycleCommand.OpName(command.Op) + " seq=" + command.Sequence +
                        " (already applied).");
                    continue;
                }

                // A newer lifecycle/state command supersedes corrections still waiting for
                // this lot. Otherwise an old construction sample can overwrite completion.
                SupersedePendingState(command, now);
                Apply(command, now);
            }

            ReportClientStats(now);
        }

        /// <summary>
        /// Returns true when the command consumed realize budget. Every outcome is terminal -
        /// built, corrected, refused, or aimed at something that is already gone - and each one is
        /// recorded in the replay window, so a redelivery is recognised rather than re-applied.
        /// </summary>
        private bool Apply(GrowableLifecycleCommand command, long now)
        {
            switch (command.Op)
            {
                case GrowableLifecycleCommand.OpSpawn: return ApplySpawn(command, now);
                case GrowableLifecycleCommand.OpLevel: return ApplyLevel(command, now);
                case GrowableLifecycleCommand.OpRemove: return ApplyRemove(command, now);
                case GrowableLifecycleCommand.OpState: return ApplyState(command, now);
                default: return false;
            }
        }

        private bool ApplySpawn(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.HasComponent<SpawnableBuildingData>(candidate),
                    out prefab))
            {
                // Either an asset this machine does not have, or a command aimed at something that
                // is not a zoned building at all. Neither is retryable.
                _unknownPrefab++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Warn(LogTopic.Buildings, "GrowableSync: unknown zoned-building prefab '" +
                    command.PrefabName + "' at " + Format(position) + "; spawn dropped.");
                return true;
            }

            var rotation = new quaternion(math.normalizesafe(
                new float4(command.RotX, command.RotY, command.RotZ, command.RotW),
                new float4(0f, 0f, 0f, 1f)));

            var blockers = new NativeList<Entity>(8, Allocator.Temp);
            try
            {
                CollectOverlapping(prefab, position, rotation, blockers);

                // Already standing, same building, same lot: a redelivery whose sequence has aged
                // out of the replay window. Rebuilding it would be the duplicate this whole path
                // exists to prevent.
                if (AlreadySatisfied(blockers, prefab, position, now))
                {
                    Entity existing = FindGrowableAt(position, prefab, now);
                    if (existing != Entity.Null)
                    {
                        if (ApplyConditionAndState(existing, command))
                            EntityManager.AddComponent<Updated>(existing);
                    }
                    _duplicates++;
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    SyncLog.Detail(LogTopic.Buildings, "GrowableSync: '" + command.PrefabName +
                        "' already stands at " + Format(position) + "; spawn seq=" +
                        command.Sequence + " ignored.");
                    return true;
                }

                Entity placedBlocker = FirstPlayerPlaced(blockers, now);
                if (placedBlocker != Entity.Null)
                {
                    // A building a player put here by hand outranks a grown one: the host's own
                    // simulation would have condemned the growable against it too. Refusing keeps
                    // the two cities agreeing about the building that was deliberately placed.
                    _conflicts++;
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    SyncLog.Warn(LogTopic.Buildings, "GrowableSync conflict: '" + command.PrefabName +
                        "' at " + Format(position) + " overlaps " +
                        DescribeBlocker(placedBlocker, now) + "; spawn refused (seq=" +
                        command.Sequence + ").");
                    return true;
                }

                // Everything left is a grown building this machine produced on its own - only
                // possible if its spawner ran while the session was not synchronized. The host is
                // the authority on grown buildings, so these lose and are cleared out of the way.
                for (int i = 0; i < blockers.Length; i++)
                {
                    _conflicts++;
                    SyncLog.Warn(LogTopic.Buildings,
                        "GrowableSync conflict: evicting locally grown " +
                        DescribeBlocker(blockers[i], now) + " for the host's '" + command.PrefabName +
                        "' at " + Format(position) + ".");
                    EntityManager.AddComponent<Deleted>(blockers[i]);
                    SyncLog.Trace(LogTopic.Buildings, "growable evicted for host spawn");
                }
            }
            finally
            {
                blockers.Dispose();
            }

            _buildSync.RealizeSimulationBuilding(prefab, position, rotation, SeedFor(command),
                (command.Flags & GrowableLifecycleCommand.FlagUnderConstruction) != 0);
            NoteSelfRealized(prefab, position, command, now);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotSpawn++;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync realize: built '" + command.PrefabName +
                "' at " + Format(position) + " seed=" + command.RandomSeed + " seq=" +
                command.Sequence + ".");
            return true;
        }

        /// <summary>
        /// Hands a standing building the prefab it is becoming - the game's own level-change
        /// mechanism, so construction, notification and zone bookkeeping all run as usual. The
        /// target may be a prefab this machine's own simulation would never have chosen.
        /// </summary>
        private bool ApplyLevel(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.HasComponent<SpawnableBuildingData>(candidate),
                    out prefab))
            {
                _unknownPrefab++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Warn(LogTopic.Buildings, "GrowableSync: unknown level-change prefab '" +
                    command.PrefabName + "' at " + Format(position) + "; skipped.");
                return true;
            }

            Entity building = FindGrowableAt(position, Entity.Null, now);
            if (building == Entity.Null)
            {
                _unmatched++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Detail(LogTopic.Buildings, "GrowableSync: no building at " +
                    Format(position) + " to level to '" + command.PrefabName + "'; skipped.");
                return true;
            }

            // Already becoming that prefab: re-applying would restart construction. This is the
            // idempotence that matters in practice, because the local simulation may have proposed
            // its own level change for the same building before this one arrived.
            if (EntityManager.HasComponent<UnderConstruction>(building))
            {
                UnderConstruction current = EntityManager.GetComponentData<UnderConstruction>(building);
                if (current.m_NewPrefab == prefab)
                {
                    if (ApplyConditionAndState(building, command))
                        EntityManager.AddComponent<Updated>(building);
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    return true;
                }
                if (current.m_NewPrefab != Entity.Null)
                    SyncLog.Detail(LogTopic.Buildings,
                        "GrowableSync: replacing this machine's own level-change target " + "at " +
                        Format(position) + " with the host's '" + command.PrefabName + "'.");
                current.m_NewPrefab = prefab;
                current.m_Progress = command.ConstructionProgress;
                current.m_Speed = command.ConstructionSpeed;
                EntityManager.SetComponentData(building, current);
            }
            else
            {
                EntityManager.AddComponentData(building, new UnderConstruction
                {
                    m_NewPrefab = prefab,
                    m_Progress = command.ConstructionProgress,
                    m_Speed = command.ConstructionSpeed,
                });
            }

            ApplyConditionAndState(building, command);
            EntityManager.AddComponent<Updated>(building);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotLevel++;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync realize: level change to '" +
                command.PrefabName + "' at " + Format(position) + " seq=" + command.Sequence + ".");
            return true;
        }

        private bool ApplyRemove(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            Entity prefab;
            _prefabIndex.TryResolve(command.PrefabName, out prefab);
            Entity building = FindGrowableAt(position, prefab, now);
            if (building == Entity.Null)
            {
                // Nothing to remove. Convergent either way: the building this refers to was never
                // built here (its spawn was refused), or a player already bulldozed it.
                _unmatched++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Detail(LogTopic.Buildings, "GrowableSync: no building at " +
                    Format(position) + " to remove ('" + command.PrefabName + "'); already gone.");
                return true;
            }

            EntityManager.AddComponent<Deleted>(building);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotRemove++;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync realize: removed '" +
                command.PrefabName + "' at " + Format(position) + " seq=" + command.Sequence + ".");
            return true;
        }

        private bool ApplyState(GrowableLifecycleCommand command, long now)
        {
            return ApplyState(command, now, true);
        }

        private bool ApplyState(GrowableLifecycleCommand command, long now, bool allowPending)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);
            Entity prefab;
            _prefabIndex.TryResolve(command.PrefabName, out prefab);

            Entity building = FindGrowableAt(position, prefab, now);
            if (building == Entity.Null)
            {
                if (allowPending && _pendingStateSequences.Add(command.Sequence))
                {
                    if (_pendingStateCorrections.Count >= MaxPendingStateCorrections)
                    {
                        // Convergent to shed: corrections are progress ticks sent twice a
                        // second, and completion is announced by its own command.
                        _pendingStateSequences.Remove(command.Sequence);
                        _unmatched++;
                        _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    }
                    else
                    {
                        _pendingStateCorrections.Add(new PendingStateCorrection
                        {
                            Command = command,
                            Expiry = now + SelfRealizedWindowMs,
                            NextAttempt = now + RetryIntervalMs,
                        });
                    }
                }
                return true;
            }

            ApplyResolvedState(building, prefab, command, now);
            return true;
        }

        private void ApplyResolvedState(Entity building, Entity prefab,
            GrowableLifecycleCommand command, long now)
        {
            bool needsUpdate = RepairCompletedPrefab(building, prefab, command);
            needsUpdate |= ApplyConditionAndState(building, command);
            // Condition and the construction clock are read directly by their consumers.
            // Updated rebuilds road/utility/lot data and is only needed for lifecycle changes.
            if (needsUpdate)
            {
                EntityManager.AddComponent<Updated>(building);
                _stateRefreshes++;
            }
            else _stateDataOnly++;
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotState++;
        }

        /// <summary>
        /// A state update can share a network burst with the spawn whose live entity is generated
        /// later in the frame. Keep it ordered and bounded instead of terminally dropping it.
        /// </summary>
        private void RetryPendingStateCorrections(long now)
        {
            int remaining = _pendingStateCorrections.Count;
            int attempts = 0;
            while (remaining-- > 0 && _pendingStateCorrections.Count > 0 &&
                   attempts < MaxStateRetriesPerFrame)
            {
                if (_stateRetryCursor >= _pendingStateCorrections.Count) _stateRetryCursor = 0;
                int i = _stateRetryCursor;
                PendingStateCorrection pending = _pendingStateCorrections[i];
                if (pending.Expiry <= now)
                {
                    attempts++;
                    // Skipped, like the level change and the removal that cannot find their
                    // building either. Completed state can also repair a missed level prefab, but
                    // the absolute occupancy/company pages carry the same completed identity and
                    // remain its backstop after this short ordering window.
                    _pendingStateSequences.Remove(pending.Command.Sequence);
                    _pendingStateCorrections.RemoveAt(i);
                    _unmatched++;
                    _applied.Remember(pending.Command.Sequence, now, ReplayWindowMs);
                    SyncLog.Detail(LogTopic.Buildings, "GrowableSync: no building at " +
                        Format(new float3(pending.Command.AnchorX, pending.Command.AnchorY,
                            pending.Command.AnchorZ)) + " to correct ('" +
                        pending.Command.PrefabName + "'); skipped.");
                    continue;
                }

                _stateRetryCursor++;
                if (now < pending.NextAttempt) continue;
                pending.NextAttempt = now + RetryIntervalMs;
                attempts++;
                _stateRetryChecks++;

                var position = new float3(pending.Command.AnchorX,
                    pending.Command.AnchorY, pending.Command.AnchorZ);
                Entity prefab;
                _prefabIndex.TryResolve(pending.Command.PrefabName, out prefab);
                Entity building = FindGrowableAt(position, prefab, now);
                if (building == Entity.Null) continue;

                _pendingStateSequences.Remove(pending.Command.Sequence);
                _pendingStateCorrections.RemoveAt(i);
                _stateRetryCursor = i;
                ApplyResolvedState(building, prefab, pending.Command, now);
            }
        }

        private void SupersedePendingState(GrowableLifecycleCommand command, long now)
        {
            for (int i = _pendingStateCorrections.Count - 1; i >= 0; i--)
            {
                GrowableLifecycleCommand previous = _pendingStateCorrections[i].Command;
                if (!Infrastructure.GrowableCommandInbox.SameTarget(previous, command)) continue;
                _pendingStateCorrections.RemoveAt(i);
                _pendingStateSequences.Remove(previous.Sequence);
                _applied.Remember(previous.Sequence, now, ReplayWindowMs);
            }
        }

    }
}
