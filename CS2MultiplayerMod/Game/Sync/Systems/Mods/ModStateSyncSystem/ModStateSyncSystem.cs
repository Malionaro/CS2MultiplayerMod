using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Sync.ModSync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.ModSync;
using Game;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Mods
{
    /// <summary>
    /// Replicates the state other mods keep in the world, without knowing which mods are installed.
    ///
    /// Everything here is decided by the kind of state, never by a mod's name. A type is replicated
    /// because the runtime would write it to a savegame; an entity is found again because it is a
    /// place in the city; a change is noticed because the engine says that chunk was written to.
    /// One mod's lane connections and another's road speeds travel through the same mechanism for
    /// the same reason, and a mod nobody has written yet will travel through it too.
    ///
    /// A joining player needs nothing from this system: durable state is in the savegame they
    /// download, by the same definition that puts it on this wire. What this covers is the rest of
    /// the session - the edits made after that point, in both directions.
    /// </summary>
    public partial class ModStateSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incomingState =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ConcurrentQueue<SimulationCommandMessage> _incomingTable =
            new ConcurrentQueue<SimulationCommandMessage>();

        private ModSyncObserver _observer;

        private ModComponentCatalog _catalog;
        private ModTypeBinding _binding;
        private ModCarrierIdentity _identity;
        private PrefabSystem _prefabs;
        private PrefabIndex _prefabIndex;
        private global::Game.Net.SearchSystem _netSearch;
        private ObjectSearch _objectSearch;

        /// <summary>The closure hash last seen for a carrier, keyed the portable way.</summary>
        private readonly Dictionary<string, ulong> _shadow =
            new Dictionary<string, ulong>(System.StringComparer.Ordinal);

        /// <summary>
        /// Carriers this machine has published state for, so that one going empty can be noticed.
        /// A query on a type cannot report that the type was removed - the entity simply stops
        /// matching - so the only way a removal is ever seen is by remembering what was there.
        /// </summary>
        private readonly Dictionary<string, ModEntityRef> _knownCarriers =
            new Dictionary<string, ModEntityRef>(System.StringComparer.Ordinal);

        private readonly List<string> _sweepOrder = new List<string>();
        private int _sweepCursor;

        private ModTypeTable _lastReceivedTable;
        private bool _tablePublished;
        private bool _announcedCatalog;
        private long _lastReportMs;

        // What the 30-second line reports.
        private int _capturedTransactions;
        private int _sentBytes;
        private int _appliedTransactions;
        private int _unresolvedGaveUp;
        private int _rejectedClosures;
        private int _noCarrier;
        private bool _saidWaitingForTable;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _netSearch = World.GetOrCreateSystemManaged<global::Game.Net.SearchSystem>();
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _prefabIndex = new PrefabIndex(_prefabs,
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _observer = SyncObserverBinding.Bind(
                () => new ModSyncObserver(this, _incomingState, _incomingTable), DrainQueues);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueues);
            base.OnDestroy();
        }

        private void DrainQueues()
        {
            SimulationCommandMessage ignored;
            while (_incomingState.TryDequeue(out ignored)) { }
            while (_incomingTable.TryDequeue(out ignored)) { }

            // A world replacement invalidates every portable key at once: the carriers they named
            // belong to a world that is gone.
            _shadow.Clear();
            _knownCarriers.Clear();
            _sweepOrder.Clear();
            _sweepCursor = 0;
            _held.Clear();
            _awaitingSettle.Clear();
            Entity pending;
            while (_pendingCandidates.TryDequeue(out pending)) { }
            _queuedCandidates.Clear();
            _candidates.Clear();
            _lastOrderVersion = 0;
            _sweepPending = true;
            _lastComplaintTick.Clear();
            _tablePublished = false;
            _binding = null;

            // A replaced world is a fresh subject: a condition that was worth saying once about the
            // old one is worth saying once about this one.
            _reportedOnce.Clear();
            _saidWaitingForTable = false;
            _lastReceivedTable = null;
        }

        protected override void OnUpdate()
        {
            using (SyncProfiler.Measure("ModSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady) return;

                MultiplayerSession session = service.Session;
                long now = service.NowMs;

                EnsureCatalog();
                NegotiateTable(session);
                if (_binding == null)
                {
                    if (!_saidWaitingForTable)
                    {
                        _saidWaitingForTable = true;
                        SyncLog.Event(LogTopic.ModSync,
                            "Mod state is idle: waiting for the host to publish its type table.");
                    }
                    return;
                }

                CaptureChanges(session, now);
                Report(now);
            }
        }

        /// <summary>
        /// Called by <see cref="Systems.SyncRealizeSystem"/> during ToolUpdate. Arriving state is
        /// written there for the same reason every other replicated edit is: it is the phase where
        /// a structural change still reaches the systems that have to see it this frame.
        /// </summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (_binding == null) return;

            using (SyncProfiler.Measure("ModSync.Realize"))
            {
                RealizeIncoming(service.Session, service.NowMs);
            }
        }

        /// <summary>
        /// Finds every third-party type in the world once per session. Deliberately not in
        /// OnCreate: mods register their types as they load, and this system is created early.
        /// </summary>
        private void EnsureCatalog()
        {
            // A player can switch a mod on while the game is running - it takes effect without a
            // restart - and its components only enter the engine's registry when its own systems
            // first touch them. A catalogue taken before that moment saw a world without the mod,
            // and would go on replicating nothing for the rest of the session while looking
            // perfectly healthy. The registry's size is the cheap signal that this happened.
            if (_catalog != null && TypeManager.GetTypeCount() == _catalog.TypeCountAtBuild) return;

            ModComponentCatalog previous = _catalog;
            int before = previous != null ? previous.Entries.Count : 0;

            _catalog = ModComponentCatalog.Build();

            // The count moved but nothing this session replicates did - a type registered somewhere
            // else in the game. Keep the table and every recorded hash.
            if (previous != null && _catalog.ReplicatesSameAs(previous))
            {
                _catalog = previous;
                _catalog.TypeCountAtBuild = TypeManager.GetTypeCount();
                return;
            }

            _identity = new ModCarrierIdentity(EntityManager, _prefabs, _netSearch, _objectSearch,
                _prefabIndex);

            if (previous != null)
            {
                // Type indices are positions in the table, so everything built on the old one goes:
                // the query and its handles, the binding, and every recorded hash.
                _queryBuilt = false;
                _typeHandles = null;
                _capture = null;
                _apply = null;
                _binding = null;
                _tablePublished = false;
                _shadow.Clear();
                _knownCarriers.Clear();
                _sweepOrder.Clear();
                _sweepCursor = 0;
                _reportedOnce.Clear();
                _announcedCatalog = false;

                SyncLog.Event(LogTopic.ModSync, "Mod state rescanned: " + before + " -> " +
                    _catalog.Entries.Count + " replicable type(s); a mod was loaded or unloaded " +
                    "after the session started.");
            }

            if (_announcedCatalog) return;
            _announcedCatalog = true;

            SyncLog.Event(LogTopic.ModSync, "Mod state: " + _catalog.Entries.Count +
                " replicable of " + _catalog.ThirdPartyTypeCount + " third-party type(s).");
            for (int i = 0; i < _catalog.Entries.Count; i++)
            {
                ModTypeDescriptor descriptor = _catalog.Entries[i].Descriptor;
                SyncLog.Detail(LogTopic.ModSync, "  " + descriptor.DisplayName + " (" +
                    descriptor.Kind + ", " + descriptor.LeafCount + " field(s))");
            }
            for (int i = 0; i < _catalog.Exclusions.Count; i++)
                SyncLog.Detail(LogTopic.ModSync, "  excluded: " + _catalog.Exclusions[i]);
        }

        /// <summary>
        /// Agrees the type table. The host owns the order and everyone binds their own types to it,
        /// so a payload can never be read against a different type than it was written from.
        /// </summary>
        private void NegotiateTable(MultiplayerSession session)
        {
            if (session.Role == SessionRole.Host)
            {
                if (_binding == null) _binding = ModTypeBinding.ForHost(_catalog);

                // Republished whenever somebody joins, because the table is what makes every
                // later transaction readable and a joiner has not seen the first one.
                if (_tablePublished && !_observer.TakePeerJoined()) return;
                _tablePublished = true;

                var command = new ModTypeTableCommand { Table = _binding.Table };
                session.SendCommand(0, ModTypeTableCommand.Id, command.Encode());
                SyncLog.Event(LogTopic.ModSync, "Mod type table published: " +
                    _binding.Table.Count + " type(s).");
                return;
            }

            // The host only re-sends on a join or when its own table changed. A client that enabled
            // a mod after the table arrived would otherwise wait for a resend that never comes, so
            // it rebinds against the one it already has.
            if (_binding == null && _lastReceivedTable != null)
            {
                _binding = ModTypeBinding.ForClient(_catalog, _lastReceivedTable);
                SyncLog.Event(LogTopic.ModSync, "Mod type table re-bound after a local rescan: " +
                    _lastReceivedTable.Count + " type(s), " + _binding.Missing.Count +
                    " not available here.");
                _shadow.Clear();
            }

            SimulationCommandMessage message;
            while (_incomingTable.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                ModTypeTableCommand command;
                try
                {
                    command = ModTypeTableCommand.Decode(message.Body);
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.ModSync,
                        "Dropping a malformed mod type table: " + ex.Message);
                    continue;
                }

                _lastReceivedTable = command.Table;
                _binding = ModTypeBinding.ForClient(_catalog, command.Table);
                SyncLog.Event(LogTopic.ModSync, "Mod type table bound: " + command.Table.Count +
                    " type(s), " + _binding.Missing.Count + " not available here.");
                for (int i = 0; i < _binding.Missing.Count; i++)
                    SyncLog.Warn(LogTopic.ModSync, "  missing: " + _binding.Missing[i]);

                // The world was just replaced or this is a fresh join: nothing that was captured
                // against an older table can be compared against this one.
                _shadow.Clear();
            }
        }

        /// <summary>
        /// Says something the first time it is true and never again. These are conditions that hold
        /// for a whole session once they hold at all - a type that is not in the table, an entity
        /// shape nothing can describe - so repeating them per frame would bury everything else.
        /// </summary>
        private void ReportOnce(string key, string message)
        {
            if (!_reportedOnce.Add(key)) return;
            SyncLog.Warn(LogTopic.ModSync, message);
        }

        private void Report(long now)
        {
            if (now - _lastReportMs < 30000) return;
            _lastReportMs = now;

            // Silence while idle is the point - a line every 30 s saying nothing happened is what
            // makes the lines that matter unreadable.
            if (_capturedTransactions == 0 && _appliedTransactions == 0 && _held.Count == 0 &&
                _unresolvedGaveUp == 0 && _rejectedClosures == 0 && _noCarrier == 0) return;

            SyncLog.Trace(LogTopic.ModSync, "ModSync/30s captured=" + _capturedTransactions +
                " sentKB=" + (_sentBytes >> 10) + " applied=" + _appliedTransactions +
                " waiting=" + _held.Count + " gaveUp=" + _unresolvedGaveUp +
                " refused=" + _rejectedClosures + " noCarrier=" + _noCarrier +
                " tracking=" + _shadow.Count + " known=" + _knownCarriers.Count +
                " role=" + (Mod.Service != null ? Mod.Service.Session.Role.ToString() : "?"));

            _capturedTransactions = 0;
            _sentBytes = 0;
            _appliedTransactions = 0;
            _unresolvedGaveUp = 0;
            _rejectedClosures = 0;
            _noCarrier = 0;
        }

        /// <summary>
        /// Funnels both mod-sync commands and remembers that somebody joined, which is the moment
        /// the host has to say again what this session replicates.
        /// </summary>
        private sealed class ModSyncObserver : SessionObserver
        {
            private readonly ModStateSyncSystem _owner;
            private readonly ConcurrentQueue<SimulationCommandMessage> _state;
            private readonly ConcurrentQueue<SimulationCommandMessage> _table;
            private int _peerJoined;

            public ModSyncObserver(ModStateSyncSystem owner,
                ConcurrentQueue<SimulationCommandMessage> state,
                ConcurrentQueue<SimulationCommandMessage> table)
            {
                _owner = owner;
                _state = state;
                _table = table;
            }

            public override void OnPeerJoined(Peer peer)
            {
                System.Threading.Interlocked.Exchange(ref _peerJoined, 1);
            }

            /// <summary>Reads and clears the flag - the network thread sets it, this reads it.</summary>
            public bool TakePeerJoined()
            {
                return System.Threading.Interlocked.Exchange(ref _peerJoined, 0) != 0;
            }

            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == ModTypeTableCommand.Id)
                {
                    SyncInbox.Push(_table, command, SyncInbox.DefaultCap, "mod type table");
                    return;
                }

                if (command.CommandId != ModStateCommand.Id) return;
                if (command.Body != null && command.Body.Length > ModStateCommand.MaxBodyBytes)
                {
                    SyncLog.Warn(LogTopic.ModSync, "Dropping an oversized mod state command (" +
                        command.Body.Length + " bytes).");
                    return;
                }
                SyncInbox.Push(_state, command, SyncInbox.DefaultCap, "mod state");
            }
        }
    }
}
