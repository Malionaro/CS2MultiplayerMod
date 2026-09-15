using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Sync.ModSync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.ModSync;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Mods
{
    public partial class ModStateSyncSystem
    {
        /// <summary>
        /// Closures waiting for something they name. Well above what a burst of edits produces;
        /// past it the oldest is dropped rather than letting a stuck carrier grow without limit.
        /// </summary>
        private const int MaxHeld = 256;

        private const int ComplaintThrottleMs = 10000;

        /// <summary>One carrier's most recent closure, and how long it has been waiting.</summary>
        private sealed class Held
        {
            public ModStateSnapshot Snapshot;
            public long FirstSeenMs;
            public string LastDetail;
            public int WaitedFrames;
        }

        /// <summary>
        /// Keyed by carrier, latest wins. A closure is a complete statement about one carrier, so a
        /// newer one for the same carrier replaces the older entirely - there is no sense in
        /// applying a superseded description of the same junction first.
        /// </summary>
        private readonly Dictionary<string, Held> _held =
            new Dictionary<string, Held>(System.StringComparer.Ordinal);

        private readonly List<string> _finished = new List<string>();
        private ModClosureApply _apply;
        private ModTypeBinding _applyBinding;
        private readonly Dictionary<string, ModEntityRef> _awaitingSettle =
            new Dictionary<string, ModEntityRef>(System.StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lastComplaintTick =
            new Dictionary<string, int>(System.StringComparer.Ordinal);

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            Receive(session, now);
            if (!ModSyncFeature.ApplyReceived || _held.Count == 0) return;

            if (_apply == null || _applyBinding != _binding)
            {
                _apply = new ModClosureApply(EntityManager, _identity, _binding, _catalog);
                _applyBinding = _binding;
            }

            _finished.Clear();
            foreach (KeyValuePair<string, Held> pair in _held)
            {
                Held held = pair.Value;
                ModApplyOutcome outcome;
                try
                {
                    outcome = _apply.Apply(held.Snapshot);
                }
                catch (System.Exception ex)
                {
                    // One bad closure must not cost every other carrier its edit this frame.
                    Complain(pair.Key, "Could not apply mod state at " + pair.Key + ".", ex);
                    _finished.Add(pair.Key);
                    continue;
                }

                if (outcome == ModApplyOutcome.Applied)
                {
                    _finished.Add(pair.Key);
                    _appliedTransactions++;
                    SyncLog.Trace(LogTopic.ModSync, "mod state applied " + pair.Key + " (" +
                        held.Snapshot.CarrierValues.Components.Count + " type(s), " +
                        held.Snapshot.Satellites.Count + " satellite(s))" +
                        (held.WaitedFrames > 0 ? " after waiting " + held.WaitedFrames + " frame(s)" : ""));
                    Settle(session, held.Snapshot.Carrier, pair.Key);
                    continue;
                }

                if (outcome == ModApplyOutcome.TypeMissing)
                {
                    // Waiting cannot help: this machine has nothing to write that type into.
                    Complain(pair.Key, "Mod state at " + pair.Key + " names a type this game " +
                        "does not have (" + _apply.Detail + ").", null);
                    _finished.Add(pair.Key);
                    continue;
                }

                if (held.WaitedFrames++ == 0)
                    SyncLog.Trace(LogTopic.ModSync, "mod state waiting at " + pair.Key + ": " +
                        (outcome == ModApplyOutcome.CarrierMissing
                            ? "no such object here yet"
                            : "waiting for " + _apply.Detail));

                held.LastDetail = _apply.Detail;
                if (now - held.FirstSeenMs < ModSyncFeature.UnresolvedHoldMs) continue;

                // The road or building it belongs to never turned up. Held rather than guessed at,
                // and now given up on by name, because applying it somewhere else would be worse.
                _finished.Add(pair.Key);
                _unresolvedGaveUp++;
                Complain(pair.Key, "Gave up on mod state at " + pair.Key + " after " +
                    (ModSyncFeature.UnresolvedHoldMs / 1000) + "s: " +
                    (outcome == ModApplyOutcome.CarrierMissing
                        ? "this machine has no such object"
                        : "it refers to " + held.LastDetail + ", which is not here"), null);
            }

            for (int i = 0; i < _finished.Count; i++) _held.Remove(_finished[i]);
        }

        private void Receive(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incomingState.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                ModStateCommand command;
                try
                {
                    command = ModStateCommand.Decode(message.Body, _binding);
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.ModSync,
                        "Dropping a malformed mod state command: " + ex.Message);
                    continue;
                }

                string key = command.Snapshot.Carrier.Key();
                SyncLog.Trace(LogTopic.ModSync, "mod state received " + key + " from player " +
                    message.OriginPlayerId + " (" + (message.Body == null ? 0 : message.Body.Length) +
                    " bytes, " + command.Snapshot.CarrierValues.Components.Count + " type(s), " +
                    command.Snapshot.Satellites.Count + " satellite(s))");

                Held existing;
                if (_held.TryGetValue(key, out existing))
                {
                    existing.Snapshot = command.Snapshot;
                    existing.FirstSeenMs = now;
                    existing.WaitedFrames = 0;
                    continue;
                }

                if (_held.Count >= MaxHeld)
                {
                    SyncLog.Warn(LogTopic.ModSync, "Mod state backlog is full (" + MaxHeld +
                        "); dropping the change at " + key + ".");
                    continue;
                }

                _held.Add(key, new Held { Snapshot = command.Snapshot, FirstSeenMs = now });
            }
        }

        /// <summary>
        /// Closes out a closure that has just been written into the world.
        ///
        /// The host answers for what the carrier holds afterwards, and says so to everyone - the
        /// sender included. Without that, two players editing the same junction inside one
        /// round trip end up on opposite answers rather than one: each applies the other's closure
        /// after their own, so the one whose change the host took first keeps the one the host
        /// discarded. Re-publishing the host's result is what makes them converge, and it is cheap
        /// because a closure is a complete statement - applying it twice changes nothing.
        ///
        /// A client has no such say. It records what it just applied so that its own capture
        /// recognises the state as already known and does not send it straight back.
        /// </summary>
        private void Settle(MultiplayerSession session, ModEntityRef carrierRef, string key)
        {
            _awaitingSettle[key] = carrierRef;
        }

        private void SettleApplied(MultiplayerSession session)
        {
            foreach (KeyValuePair<string, ModEntityRef> pair in _awaitingSettle)
            {
                Entity carrier;
                if (!_identity.TryResolve(pair.Value, out carrier)) continue;

                // Resolve using the sender's position, but record exactly what local capture
                // will read. Hashing the remote header makes even millimetre differences echo;
                // a position across a key bucket also bypasses the shadow entirely.
                ModEntityRef localRef;
                if (!_identity.TryDescribe(carrier, out localRef)) continue;
                byte[] body;
                ulong hash;
                if (!TryEncode(carrier, localRef, out body, out hash)) continue;

                string localKey = localRef.Key();
                _shadow[localKey] = hash;
                RememberCarrier(localKey, localRef);

                // An empty closure no longer matches the capture query. Publish explicitly so
                // removal acknowledgements also reach the sender, independent of chunk versions.
                if (session.Role != SessionRole.Host || !ModSyncFeature.SendCaptured) continue;
                session.SendCommand(0, ModStateCommand.Id, body);
                _sentBytes += body.Length;
                SyncLog.Trace(LogTopic.ModSync, "mod state settled " + localKey + " (" +
                    body.Length + " bytes)");
            }
            _awaitingSettle.Clear();
        }

        /// <summary>
        /// One complaint per carrier per ten seconds. A carrier that cannot be resolved usually
        /// cannot be resolved repeatedly, and a log that says so sixty times a second is a log
        /// nobody can read the rest of.
        /// </summary>
        private void Complain(string key, string message, System.Exception ex)
        {
            int now = System.Environment.TickCount;
            int last;
            if (_lastComplaintTick.TryGetValue(key, out last) &&
                unchecked(now - last) < ComplaintThrottleMs) return;
            _lastComplaintTick[key] = now;

            if (ex != null) SyncLog.Error(LogTopic.ModSync, message, ex);
            else SyncLog.Warn(LogTopic.ModSync, message);
        }
    }
}
