using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Restores the host's household income in the same frame the local pass recomputes it.
    /// <see cref="ResidentialHouseholdEconomyCorrectionSystem"/> carries the rest of the daily
    /// scalars at a later point, chosen so the money writers have already run; income cannot wait
    /// for it, because its consumers now run first. See
    /// <c>docs/internals/residential-occupancy-sync.md</c>.
    /// </summary>
    public sealed partial class ResidentialHouseholdIncomeBoundarySystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;
        private EntityQuery _changedHouseholds;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _changedHouseholds = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Household, PropertyRenter>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, TouristHousehold, CommuterHousehold>(),
            });
            // Income is written on Household alone, so this filter stays narrower than the economy
            // boundary's: it must not wake on a shopping pass that only touched Resources.
            _changedHouseholds.SetChangedVersionFilter(new[]
            {
                ComponentType.ReadOnly<Household>(),
            });
        }

        /// <summary>
        /// The local writer's own interval, which also hands this system the writer's update
        /// offset - so each run sees the household partition that was just recomputed.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation
                ? 262144 / (global::Game.Simulation.HouseholdBehaviorSystem.kUpdatesPerDay * 16)
                : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Income",
                       Diagnostics.SyncZone.Residential))
            {
                if (_occupancy == null) return;
                if (!_occupancy.WantsHouseholdEconomyCorrection)
                {
                    _occupancy.ClearHouseholdIncomeCorrections();
                    return;
                }
                NativeArray<Entity> households = default(NativeArray<Entity>);
                try
                {
                    if (!_changedHouseholds.IsEmptyIgnoreFilter)
                    {
                        households = _changedHouseholds.ToEntityArray(Allocator.Temp);
                        if (households.Length != 0)
                            _occupancy.QueueHouseholdIncomeCorrections(households);
                    }
                }
                finally
                {
                    if (households.IsCreated) households.Dispose();
                }
                // Drain even on a frame whose changed-version query is empty: those are the
                // retained entities that did not fit the previous frame's bounded correction.
                _occupancy.CorrectHouseholdIncomeAfterLocalUpdate();
            }
        }
    }
}
