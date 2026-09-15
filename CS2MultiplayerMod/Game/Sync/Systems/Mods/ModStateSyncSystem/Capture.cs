using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Sync.ModSync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.ModSync;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Mods
{
    public partial class ModStateSyncSystem
    {
        /// <summary>Known carriers held at once. Past this the oldest are simply forgotten.</summary>
        private const int MaxKnownCarriers = 8192;

        /// <summary>Known carriers re-examined per frame while a structural sweep is pending.</summary>
        private const int SweepBudgetPerPass = 32;

        private EntityQuery _replicated;
        private EntityTypeHandle _entityHandle;
        private DynamicComponentTypeHandle[] _typeHandles;
        private bool _queryBuilt;

        private int _lastOrderVersion;
        private bool _sweepPending;

        private ModClosureCapture _capture;
        private ModTypeBinding _captureBinding;

        private readonly List<Entity> _candidates = new List<Entity>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Entity> _pendingCandidates =
            new System.Collections.Concurrent.ConcurrentQueue<Entity>();
        private readonly HashSet<Entity> _queuedCandidates = new HashSet<Entity>();
        private readonly HashSet<string> _reportedOnce =
            new HashSet<string>(System.StringComparer.Ordinal);
        private readonly HashSet<string> _seenThisPass =
            new HashSet<string>(System.StringComparer.Ordinal);

        private void CaptureChanges(MultiplayerSession session, long now)
        {
            if (_catalog.Entries.Count == 0) return;
            if (!EnsureQuery()) return;

            // ToolUpdate applies the payload; ModificationEnd sees the result after native
            // systems have rebuilt derived state. Only that result can close the echo.
            SettleApplied(session);

            // A structural change to one of these types is the only way a removal ever shows: the
            // entity stops matching the query, so nothing that looks at the query can see it go.
            // When one happens, every carrier known to hold state is looked at again.
            int orderVersion = _replicated.GetCombinedComponentOrderVersion(true);
            if (orderVersion != _lastOrderVersion)
            {
                _lastOrderVersion = orderVersion;
                _sweepPending = true;
            }

            CollectChangedCarriers();
            PublishCandidates(session, now);
            RunSweep(session, now);
        }

        private bool EnsureQuery()
        {
            if (_queryBuilt) return _typeHandles != null;
            _queryBuilt = true;

            var any = new ComponentType[_catalog.Entries.Count];
            for (int i = 0; i < _catalog.Entries.Count; i++)
                any[i] = ComponentType.ReadOnly(_catalog.Entries[i].Type);

            _replicated = GetEntityQuery(new EntityQueryDesc
            {
                Any = any,
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _entityHandle = GetEntityTypeHandle();
            _typeHandles = new DynamicComponentTypeHandle[any.Length];
            for (int i = 0; i < any.Length; i++) _typeHandles[i] = GetDynamicComponentTypeHandle(any[i]);
            return true;
        }

        /// <summary>
        /// Asks the engine which chunks holding replicated types were written to since the last
        /// pass. This is a prefilter and not a finding: the engine reports write access, not a
        /// changed value, which is why every candidate is compared against what was last published
        /// before anything is sent. Idle frames report nothing at all, and that is what keeps this
        /// free when nobody is using any of these mods.
        /// </summary>
        private void CollectChangedCarriers()
        {
            _candidates.Clear();
            _seenThisPass.Clear();

            _entityHandle.Update(this);
            for (int i = 0; i < _typeHandles.Length; i++) _typeHandles[i].Update(this);

            NativeArray<ArchetypeChunk> chunks = _replicated.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    bool changed = false;
                    for (int t = 0; t < _typeHandles.Length; t++)
                    {
                        if (!chunk.DidChange(ref _typeHandles[t], LastSystemVersion)) continue;
                        changed = true;
                        break;
                    }
                    if (!changed) continue;

                    NativeArray<Entity> entities = chunk.GetNativeArray(_entityHandle);
                    for (int e = 0; e < entities.Length; e++)
                    {
                        Entity entity = entities[e];
                        if (_queuedCandidates.Add(entity)) _pendingCandidates.Enqueue(entity);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            // Record all changed entities before LastSystemVersion advances. The per-frame
            // budget limits encoding, not discovery; overflow must survive an idle next frame.
            Entity pending;
            while (_candidates.Count < ModSyncFeature.MaxCarriersPerPass &&
                   _pendingCandidates.TryDequeue(out pending))
            {
                Entity entity = pending;
                _queuedCandidates.Remove(entity);
                _candidates.Add(entity);
            }
        }

        private void PublishCandidates(MultiplayerSession session, long now)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                Entity entity = _candidates[i];
                if (!EntityManager.Exists(entity)) continue;

                Entity carrier;
                ModEntityRef carrierRef;
                if (!TryFindCarrier(entity, out carrier, out carrierRef))
                {
                    // Holds replicated state, is not a place in the world, and points at nothing
                    // that is. Nothing can be said about it, so it is counted and named rather
                    // than dropped in silence - this is the count that says a whole mod is not
                    // travelling and nobody could see why.
                    _noCarrier++;
                    ReportOnce("nocarrier:" + DescribeTypes(entity),
                        "Mod state on an entity with no carrier (" + DescribeTypes(entity) +
                        "); it is not a place in the world and refers to none.");
                    continue;
                }

                PublishCarrier(session, carrier, carrierRef, entity);
            }
        }

        /// <summary>
        /// The entity that changed is usually the carrier itself - a junction, a road, a building.
        /// When it is not, it is one of the mod's own bookkeeping entities, and those carry a
        /// reference back to what they belong to; following that is what makes a change to a
        /// satellite alone still travel as its carrier's closure.
        /// </summary>
        private bool TryFindCarrier(Entity entity, out Entity carrier, out ModEntityRef carrierRef)
        {
            if (_identity.TryDescribe(entity, out carrierRef))
            {
                carrier = entity;
                return true;
            }

            carrier = Entity.Null;
            Entity found = Entity.Null;
            ModEntityRef foundRef = ModEntityRef.Null;
            var scratch = new List<ModLeaf>();

            for (int i = 0; i < _catalog.Entries.Count && found == Entity.Null; i++)
            {
                ModCatalogEntry entry = _catalog.Entries[i];
                if (!entry.Accessor.Plan.HasReferences) continue;
                if (!entry.Accessor.Has(EntityManager, entity)) continue;

                scratch.Clear();
                entry.Accessor.ReadInto(EntityManager, entity, scratch, target =>
                {
                    if (found != Entity.Null || target == Entity.Null) return ModEntityRef.Null;
                    ModEntityRef described;
                    if (!_identity.TryDescribe(target, out described)) return ModEntityRef.Null;
                    found = target;
                    foundRef = described;
                    return described;
                });
            }

            if (found == Entity.Null) return false;
            carrier = found;
            carrierRef = foundRef;
            return true;
        }

        /// <summary>
        /// Describes one carrier's whole closure and sends it if it differs from what this machine
        /// last saw there. The comparison is against the encoded form, which makes it exact and
        /// also closes the echo: state written here because it arrived from somebody else encodes
        /// to what was already recorded, so it is not sent back.
        /// </summary>
        private void PublishCarrier(MultiplayerSession session, Entity carrier, ModEntityRef carrierRef,
            Entity changedEntity = default(Entity))
        {
            string key = carrierRef.Key();
            if (_seenThisPass.Contains(key)) return;

            byte[] body;
            ulong hash;
            if (!TryEncode(carrier, carrierRef, out body, out hash)) return;

            // A reference to a road does not establish ownership. Preview bookkeeping can
            // reference live roads without belonging to their committed mod state.
            if (changedEntity != Entity.Null && changedEntity != carrier &&
                !_capture.SatelliteEntities.Contains(changedEntity)) return;
            _seenThisPass.Add(key);

            ulong previous;
            if (_shadow.TryGetValue(key, out previous) && previous == hash) return;

            _shadow[key] = hash;
            RememberCarrier(key, carrierRef);

            _capturedTransactions++;
            if (!ModSyncFeature.SendCaptured) return;

            session.SendCommand(0, ModStateCommand.Id, body);
            _sentBytes += body.Length;
            SyncLog.Trace(LogTopic.ModSync, "mod state captured " + key + " (" + body.Length +
                " bytes)");
        }

        /// <summary>Builds the closure and its encoded form, or explains why it could not be sent.</summary>
        private bool TryEncode(Entity carrier, ModEntityRef carrierRef, out byte[] body, out ulong hash)
        {
            body = null;
            hash = 0;

            if (_capture == null || _captureBinding != _binding)
            {
                _capture = new ModClosureCapture(EntityManager, _identity, _binding, _catalog);
                _captureBinding = _binding;
            }

            ModStateSnapshot snapshot;
            try
            {
                snapshot = _capture.Capture(carrier, carrierRef);
            }
            catch (System.Exception ex)
            {
                _rejectedClosures++;
                SyncLog.Warn(LogTopic.ModSync, "Could not read mod state at " + carrierRef.Key() +
                    ": " + ex.Message);
                return false;
            }

            if (snapshot == null)
            {
                _rejectedClosures++;
                SyncLog.Detail(LogTopic.ModSync, "Mod state at " + carrierRef.Key() +
                    " not replicated: it " + _capture.Rejection + ".");
                return false;
            }

            var command = new ModStateCommand { Snapshot = snapshot, Types = _binding };
            try
            {
                body = command.Encode();
            }
            catch (ProtocolException ex)
            {
                _rejectedClosures++;
                SyncLog.Warn(LogTopic.ModSync, "Could not encode mod state at " +
                    carrierRef.Key() + ": " + ex.Message);
                return false;
            }

            DrainUnsharedTypes();
            hash = Fold(body);
            return true;
        }

        private void RememberCarrier(string key, ModEntityRef carrierRef)
        {
            if (_knownCarriers.ContainsKey(key))
            {
                // Keep sweeps on the same local coordinates as the latest shadow. Otherwise
                // float drift makes sweeps and ordinary capture alternate different headers.
                _knownCarriers[key] = carrierRef;
                return;
            }
            if (_knownCarriers.Count >= MaxKnownCarriers) return;
            _knownCarriers.Add(key, carrierRef);
            _sweepOrder.Add(key);
        }

        /// <summary>
        /// Re-examines carriers that were holding state when something structural happened. This is
        /// where a removal is noticed: the closure comes back empty, which differs from what was
        /// recorded, so the empty closure is published and the receiver takes the state off.
        /// </summary>
        private void RunSweep(MultiplayerSession session, long now)
        {
            if (!_sweepPending || _sweepOrder.Count == 0) return;

            int budget = SweepBudgetPerPass;
            while (budget-- > 0 && _sweepOrder.Count > 0)
            {
                if (_sweepCursor >= _sweepOrder.Count)
                {
                    _sweepCursor = 0;
                    _sweepPending = false;
                    return;
                }

                string key = _sweepOrder[_sweepCursor];
                ModEntityRef carrierRef = _knownCarriers[key];

                Entity carrier;
                if (!_identity.TryResolve(carrierRef, out carrier))
                {
                    // The road or building itself is gone. Its own deletion travels as a deletion;
                    // there is nothing left here to describe.
                    _knownCarriers.Remove(key);
                    _shadow.Remove(key);
                    _sweepOrder.RemoveAt(_sweepCursor);
                    continue;
                }

                PublishCarrier(session, carrier, carrierRef);
                _sweepCursor++;
            }
        }

        /// <summary>The replicated types present on an entity, for a line that has to identify it.</summary>
        private string DescribeTypes(Entity entity)
        {
            string names = null;
            for (int i = 0; i < _catalog.Entries.Count; i++)
            {
                ModCatalogEntry entry = _catalog.Entries[i];
                if (!entry.Accessor.Has(EntityManager, entity)) continue;
                names = names == null
                    ? entry.Descriptor.DisplayName
                    : names + ", " + entry.Descriptor.DisplayName;
            }
            return names ?? "no replicated type";
        }

        /// <summary>
        /// Names each type this machine holds that the session does not replicate. The host's table
        /// decides what travels, so a type only this peer has is skipped mid-closure and would
        /// otherwise be invisible on both sides.
        /// </summary>
        private void DrainUnsharedTypes()
        {
            if (_capture.TypesNotInSession.Count == 0) return;
            foreach (string name in _capture.TypesNotInSession)
                ReportOnce("unshared:" + name, "Mod state type " + name +
                    " is not replicated in this session: the host's type table does not name it.");
            _capture.TypesNotInSession.Clear();
        }

        /// <summary>FNV-1a over the encoded closure - stable across processes, unlike a string hash.</summary>
        private static ulong Fold(byte[] data)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= prime;
            }
            return hash;
        }
    }
}
