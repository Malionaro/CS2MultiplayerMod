using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Keeps the numeric asking rent calculated by the host on properties that already exist
    /// locally, plus rents for non-household tenants. ResidentialOccupancySyncSystem owns each
    /// identified household's actual PropertyRenter value; keeping that single-writer boundary
    /// prevents a property-wide fallback from overwriting different household contracts during
    /// turnover.
    ///
    /// Vanilla recalculates one of sixteen UpdateFrame partitions in RentAdjustSystem. This system
    /// is ordered after that calculation and earlier in the phase than PropertyRenterSystem, and
    /// runs at the same interval, so only the partition vanilla just touched is walked. It corrects
    /// asking rent and company rent that later systems consume. Household lifecycle and household
    /// rent use the identity-aware occupancy channel.
    /// </summary>
    // State, lifecycle and the per-update cycle. The host's side - sweeping partitions and writing
    // a page - is in Capture.cs; the client's - resolving a page's properties and applying the
    // rents - is in Realize.cs.
    public partial class PropertyRentSyncSystem : GameSystemBase
    {
        // The paging, bounded cache, partition walk, retry and priority mechanics the three
        // property domains share. Only the payloads and the realization policy below are local;
        // the fields underneath are named views onto this state, not separate containers.
        private readonly PagedPropertySyncState<PropertyRentSnapshot, CachedProperty, PendingProperty, HostObservedRent, PropertyRentEntry>
            _propertyState = new PagedPropertySyncState<PropertyRentSnapshot, CachedProperty, PendingProperty, HostObservedRent, PropertyRentEntry>();
        private const int UpdatePartitions = PropertySyncLimits.UpdatePartitions;
        private const int RentUpdateInterval = 262144 / (16 * UpdatePartitions);
        private const float AnchorMatchDistance = 4f;
        private const float AnchorSearchRadius = 8f;
        private const float AmbiguousDistanceEpsilon = 0.01f;
        private const int MaxIncomingPages = PropertySyncLimits.MaxIncomingPages;
        private const int MaxPumpPages = PropertySyncLimits.MaxPumpPages;
        private const int MaxCachedProperties = PropertySyncLimits.MaxCachedProperties;
        private const int MaxPendingIdentities = PropertySyncLimits.MaxPendingIdentities;
        private const int MaxPendingRetriesPerUpdate = 192;
        private const long ResolveRetryMs = PropertySyncLimits.ResolveRetryMs;
        private const long ResolveTimeoutMs = 120000;
        private const int MaxPriorityEntries = PropertySyncLimits.MaxPriorityEntries;
        private const int PriorityEntriesPerPage = 64;
        private const long StatsIntervalMs = PropertySyncLimits.StatsIntervalMs;

        private ConcurrentQueue<PropertyRentSnapshot> _incoming => _propertyState.Incoming;
        private Dictionary<Entity, CachedProperty> _cache => _propertyState.Cache;
        private List<Entity>[] _cacheBuckets => _propertyState.CachedPartitions.Buckets;
        private HashSet<Entity>[] _cacheBucketMembers => _propertyState.CachedPartitions.Members;
        private Dictionary<PropertyRentIdentity, PendingProperty> _pending => _propertyState.Pending;
        private ConcurrentQueue<PropertyRentIdentity> _pendingOrder => _propertyState.PendingOrder;
        private readonly List<Entity> _cacheScratch = new List<Entity>();

        // Host-side change priority. The rolling baseline is always sent; these entries merely
        // shorten the time from a newly changed rent to the next page that carries it.
        private Dictionary<Entity, HostObservedRent> _hostObserved => _propertyState.HostObserved;
        private List<Entity>[] _hostObservedBuckets => _propertyState.HostPartitions.Buckets;
        private bool[] _hostBucketInitialized => _propertyState.HostPartitions.Initialized;
        private int[] _hostBucketCursor => _propertyState.HostPartitions.Cursor;

        /// <summary>
        /// Properties the rolling rent observer examines per update. See the same ceiling in
        /// <see cref="ResidentialOccupancySyncSystem"/>: the observer only shortens latency, and a
        /// city large enough to hit this simply takes longer to come all the way round.
        /// </summary>
        private const int MaxPropertiesObservedPerUpdate = PropertySyncLimits.MaxPropertiesObservedPerUpdate;
        private Dictionary<PropertyRentIdentity, PropertyRentEntry> _priority => _propertyState.Priority;
        private ConcurrentQueue<PropertyRentIdentity> _priorityOrder => _propertyState.PriorityOrder;

        private EntityQuery _properties;
        private EntityQuery _prefabs;
        private Entity[] _hostSweepEntities;
        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private SimulationSystem _simulationSystem;
        private ResidentialOccupancySyncSystem _occupancy;

        private int _captureCursor;
        private uint _captureSweepId = 1;
        private int _capturePageIndex;
        private uint _clientSweepId;
        private int _clientNextPage;
        private bool _clientSweepIntact;
        private long _lastSeededWorldInstallGeneration;
        private bool _clientBaselineWarned;
        private bool _syncWasReady;
        private long _nextPendingPumpMs
        {
            get => _propertyState.NextPendingPumpMs;
            set => _propertyState.NextPendingPumpMs = value;
        }

        private long _lastStatsMs;
        private long _sentBytes;
        private int _sentPages;
        private int _sentEntries;
        private int _priorityChanges;
        private int _priorityDrops;
        private int _localCaptureSkips;
        private int _localIdentityCollisions;
        private int _receivedPages;
        private int _droppedPages;
        private int _resolved;
        private int _unresolved;
        private int _ambiguous;
        private int _expired;
        private int _cacheDrops;
        private int _pruned;
        private int _appliedProperties;
        private int _appliedRenters;
        private int _appliedMarkets;

        private sealed class CachedProperty
        {
            public PropertyRentIdentity Identity;
            public Entity Prefab;
            public int Rent;
            public int Bucket;
            public uint LastSeenSweep;
        }

        private sealed class PendingProperty : PendingPropertyState<PropertyRentEntry>
        { }


        private sealed class HostObservedRent
        {
            public int Rent;
            public int Bucket;
        }


        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? RentUpdateInterval : 1;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabs = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _simulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _properties = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, Renter, PrefabRef,
                    global::Game.Objects.Transform, UpdateFrame>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner, StorageProperty>(),
            });
            SyncInbox.RegisterDrain(DrainForWorldChange);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainForWorldChange);
            DrainForWorldChange();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("PropertyRent"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.SimulationSyncReady)
                {
                    if (_syncWasReady) DrainForWorldChange();
                    _syncWasReady = false;
                    return;
                }
                _syncWasReady = true;

                MultiplayerSession session = service.Session;
                uint updateFrame = SimulationUtils.GetUpdateFrame(
                    _simulationSystem.frameIndex, UpdatePartitions, 16);
                int bucket = (int)(updateFrame % UpdatePartitions);
                if (session.Role == SessionRole.Host)
                {
                    DropIncomingPages();
                    ScanHostChanges(bucket);
                    ReportStats(session, service.NowMs);
                    return;
                }

                // Normally CityState's UIUpdate pump has already merged all pages into the managed
                // cache. Pump once more here as a harmless fallback before this bucket is consumed.
                PumpIncoming();
                ApplyBucket(bucket);
                // RentAdjust also rewrites household contracts. Channel 20 intentionally skips them,
                // so restore each channel-21 identity at this same pre-payment boundary.
                if (_occupancy != null) _occupancy.CorrectHouseholdRentsAfterRentAdjust(bucket);
                ReportStats(session, service.NowMs);
            }
        }

        /// <summary>
        /// Resolve and cache a bounded number of absolute pages from CityState's every-frame pump.
        /// This path performs only reads against ECS; the economic component writes remain solely
        /// in <see cref="OnUpdate"/> at the RentAdjustSystem cadence/order.
        /// </summary>
        internal void PumpIncoming()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;
            if (service.Session.Role == SessionRole.Host)
            {
                DropIncomingPages();
                return;
            }

            // The world transfer already contains the host's rents at its save cut. Seed those
            // values before a local RentAdjust partition can replace them while the 96-entry/s
            // rolling correction is still warming a large city.
            long installGeneration = service.WorldInstallGeneration;
            if (installGeneration > _lastSeededWorldInstallGeneration &&
                !SeedClientBaseline(installGeneration)) return;

            long now = service.NowMs;
            bool retryDue = _pending.Count > 0 && now >= _nextPendingPumpMs;
            if (_incoming.IsEmpty && !retryDue) return;

            using (var scope = new PropertySearchScope(_objectSearch))
            {
                ObjectSearch.Batch search = scope.Batch;
                NativeList<Entity> candidates = scope.Candidates;
                DrainIncoming(now, search, candidates, MaxPumpPages);
                if (retryDue)
                {
                    RetryPending(now, search, candidates);
                    _nextPendingPumpMs = now + ResolveRetryMs;
                }
            }
        }
    }
}
