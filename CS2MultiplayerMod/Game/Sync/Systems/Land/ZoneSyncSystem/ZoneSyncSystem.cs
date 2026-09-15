using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Sync; // Shared scheduling and queues.
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates selected cells from native zoning commits. Sparse patches preserve unrelated
    /// local edits and merge across bounded frame budgets. Receivers retain only unresolved
    /// cells while their road/grid dependencies catch up.
    /// </summary>
    public partial class ZoneSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ActiveRetryClock _retryClock = new ActiveRetryClock();
        private readonly LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand> _outgoing =
            new LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand>();
        private readonly LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand> _ready =
            new LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand>();

        private PrefabSystem _prefabSystem;
        private EntityQuery _zonePreviews;
        private ToolSystem _toolSystem;
        private EntityQuery _allBlocks;
        private EntityQuery _zonePrefabs;
        private CommandObserver _observer;

        // A zone command whose target Block doesn't exist yet — the road, or the zoning
        // grid the game generates for it, hasn't finished building on this machine — is
        // deferred and retried until it matches or times out. This lag (zoning right after
        // laying road) was the main reason zoning "didn't sync": the old apply matched once
        // and dropped every miss.
        private readonly LatestByKeyQueue<ZoneBlockKey, PendingZone> _pending =
            new LatestByKeyQueue<ZoneBlockKey, PendingZone>();
        private long _lastRetryMs;
        private const long ZoneRetryIntervalMs = 500;
        private const long ZoneRetryWindowMs = 12000;
        private const int MaxPendingZones = 8192;
        private const int MaxIncomingZones = 8192;
        private const int MaxBufferedOutgoingZones = 32768;
        private const int MaxDecodePerFrame = 64;
        private const int MaxSendPerFrame = 16;
        private const int MaxApplyPerFrame = 24;

        private struct PendingZone
        {
            public ZonePaintCommand Command;
            public long DeadlineMs;
            public ResyncReport RecoveryReport;
        }

        private struct ZoneBlockKey : System.IEquatable<ZoneBlockKey>
        {
            public long Position;
            public int DirectionX;
            public int DirectionZ;
            public int SizeX;
            public int SizeY;

            public bool Equals(ZoneBlockKey other) =>
                Position == other.Position && DirectionX == other.DirectionX &&
                DirectionZ == other.DirectionZ && SizeX == other.SizeX && SizeY == other.SizeY;

            public override bool Equals(object obj) => obj is ZoneBlockKey && Equals((ZoneBlockKey)obj);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)(Position ^ (Position >> 32));
                    hash = hash * 397 ^ DirectionX;
                    hash = hash * 397 ^ DirectionZ;
                    hash = hash * 397 ^ SizeX;
                    return hash * 397 ^ SizeY;
                }
            }
        }

        // Reusing the spatial index for a short window avoids rescanning every Block for every
        // source cell in a large zoning burst. Each list is pooled across rebuilds, and stale
        // entities are validated before use.
        private readonly Dictionary<long, List<Entity>> _blockLookup =
            new Dictionary<long, List<Entity>>();
        private readonly List<List<Entity>> _blockLookupListPool = new List<List<Entity>>();
        private bool _blockLookupBuilt;
        private long _blockLookupBuiltAtMs;
        private const long BlockLookupRefreshMs = 500;
        private const float BlockLookupBucketSize = 32f;

        private long _diagnosticWindowStartMs = -1;
        private int _diagnosticCaptured;
        private int _diagnosticSent;
        private int _diagnosticDecoded;
        private int _diagnosticCoalesced;
        private int _diagnosticApplied;
        private int _diagnosticDeferred;
        private int _diagnosticExpired;
        private int _diagnosticUnzonable;
        private const long DiagnosticWindowMs = 5000;

        // ZoneType.m_Index <-> prefab name, rebuilt whenever an unknown index appears
        // (zone prefabs can register late, e.g. DLC/mod zones).
        private readonly Dictionary<ushort, string> _indexToName = new Dictionary<ushort, string>();
        private readonly Dictionary<string, ushort> _nameToIndex = new Dictionary<string, ushort>();

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _zonePreviews = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Block, Cell, Temp>(),
                None = SyncQuery.ReadOnly<Deleted>(),
            });

            _allBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Block, Cell>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _zonePrefabs = GetEntityQuery(
                ComponentType.ReadOnly<ZoneData>(),
                ComponentType.ReadOnly<PrefabData>());

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, ZonePaintCommand.Id)
                    {
                        // A marquee can cover many blocks in one commit. Retain the burst while
                        // the frame-budgeted patch coalescer catches up.
                        QueueCap = MaxIncomingZones,
                        MaxBodyBytes = ZonePaintCommand.MaxEncodedBytes,
                    },
                DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueue);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _outgoing.Clear();
            _ready.Clear();
            _pending.Clear();
            _retryClock.Reset();
            ClearBlockLookup();
            _blockLookupBuilt = false;
            _lastRetryMs = 0;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("ZoneSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady) return;

                long now = service.NowMs;

                FlushOutgoing(session);
                FlushDiagnostics(now);
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            _retryClock.Observe(now, false);

            SimulationCommandMessage message;
            int examined = 0;
            while (examined < MaxDecodePerFrame && _incoming.TryDequeue(out message))
            {
                examined++;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    ZonePaintCommand command = ZonePaintCommand.Decode(message.Body);
                    ZoneBlockKey key = StateKey(command);
                    ZonePaintCommand earlier;
                    bool coalesced = _ready.TryGetValue(key, out earlier);
                    if (coalesced) command.MergeEarlier(earlier);
                    PendingZone pending;
                    if (_pending.TryGetValue(key, out pending))
                    {
                        command.MergeEarlier(pending.Command);
                        WithdrawZoneRecovery(pending, now);
                        _pending.Remove(key);
                        coalesced = true;
                    }
                    if (!_ready.TrySetLatest(key, command, MaxIncomingZones))
                    {
                        RecoverFromQueueOverflow("zone ready-state coalescer overflow");
                        break;
                    }
                    _diagnosticDecoded++;
                    if (coalesced) _diagnosticCoalesced++;
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.Land, "ZoneSync: dropping malformed command: " +
                        ex.Message);
                }
            }

            // Always process fresh commands; retry deferred ones on a timer (their blocks
            // may have finished generating since the last attempt).
            bool retryDue = _pending.Count > 0 && now - _lastRetryMs >= ZoneRetryIntervalMs;
            if (_ready.Count > 0 || retryDue) ApplyZoneCommands(retryDue, now);
        }

        private void RecoverFromQueueOverflow(string reason)
        {
            SyncInbox.Clear(_incoming);
            _outgoing.Clear();
            _ready.Clear();
            _pending.Clear();
            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                .Create(reason, "zone", CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                .About("zone latest-state queue")
                .Tried("nothing - the bounded queue was full and its zoning changes were shed"));
            SyncLog.Warn(LogTopic.Land, "ZoneSync overflowed its bounded latest-state queue; " +
                "requesting a fresh world sync.");
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagnosticWindowStartMs < 0) _diagnosticWindowStartMs = now;
            if (now - _diagnosticWindowStartMs < DiagnosticWindowMs) return;

            if (_diagnosticCaptured > 0 || _diagnosticSent > 0 ||
                _diagnosticDecoded > 0 || _diagnosticApplied > 0 ||
                _diagnosticDeferred > 0 || _diagnosticExpired > 0 || _diagnosticUnzonable > 0)
            {
                SyncLog.Detail(LogTopic.Land, "ZoneSync/5s: captured=" + _diagnosticCaptured +
                    " sent=" + _diagnosticSent + " decoded=" + _diagnosticDecoded + " coalesced=" +
                    _diagnosticCoalesced + " applied=" + _diagnosticApplied + " deferred=" +
                    _diagnosticDeferred + " expired=" + _diagnosticExpired + " unzonable=" +
                    _diagnosticUnzonable + " queues(out=" +
                    _outgoing.Count + ", inbox=" + _incoming.Count + ", ready=" + _ready.Count +
                    ", retry=" + _pending.Count + ").");
            }

            _diagnosticCaptured = 0;
            _diagnosticSent = 0;
            _diagnosticDecoded = 0;
            _diagnosticCoalesced = 0;
            _diagnosticApplied = 0;
            _diagnosticDeferred = 0;
            _diagnosticExpired = 0;
            _diagnosticUnzonable = 0;
            _diagnosticWindowStartMs = now;
        }


        internal void NotifyRealizeHeld(long now) => _retryClock.Observe(now, true);

        private string ResolveZoneName(ushort index)
        {
            string name;
            if (_indexToName.TryGetValue(index, out name)) return name;
            RebuildZoneMap();
            return _indexToName.TryGetValue(index, out name) ? name : null;
        }

        private bool TryResolveZoneIndex(string name, out ushort index)
        {
            RebuildZoneMap();
            return _nameToIndex.TryGetValue(name, out index);
        }

        private void RebuildZoneMap()
        {
            _indexToName.Clear();
            _nameToIndex.Clear();

            NativeArray<Entity> prefabs = _zonePrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    ushort index = EntityManager.GetComponentData<ZoneData>(prefabs[i]).m_ZoneType.m_Index;
                    string name = PrefabIndex.SafeName(_prefabSystem, prefabs[i]);
                    if (string.IsNullOrEmpty(name)) continue;
                    _indexToName[index] = name;
                    _nameToIndex[name] = index;
                }
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        private static long QuantizedPos(float3 position)
        {
            // 0.5 m buckets packed into a single key (blocks are metres apart, so this is
            // far finer than block spacing yet tolerant of float drift).
            return PackQuant((long)math.round(position.x * 2f),
                             (long)math.round(position.y * 2f),
                             (long)math.round(position.z * 2f));
        }

        private static long PackQuant(long qx, long qy, long qz) =>
            ((qx & 0x1FFFFF) << 42) | ((qy & 0x1FFFFF) << 21) | (qz & 0x1FFFFF);

        private static ZoneBlockKey StateKey(Block block) =>
            StateKey(block.m_Position, block.m_Direction, block.m_Size.x, block.m_Size.y);

        private static ZoneBlockKey StateKey(ZonePaintCommand command) =>
            StateKey(new float3(command.PosX, command.PosY, command.PosZ),
                     new float2(command.DirX, command.DirZ), command.SizeX, command.SizeY);

        private static ZoneBlockKey StateKey(float3 position, float2 direction, int sizeX, int sizeY) =>
            new ZoneBlockKey
            {
                Position = QuantizedPos(position),
                DirectionX = (int)math.round(direction.x * 4096f),
                DirectionZ = (int)math.round(direction.y * 4096f),
                SizeX = sizeX,
                SizeY = sizeY,
            };

    }
}
