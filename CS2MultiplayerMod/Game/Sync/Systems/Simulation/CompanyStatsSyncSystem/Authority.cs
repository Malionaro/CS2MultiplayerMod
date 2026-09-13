using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Common;
using Game.Companies;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CompanyStatsSyncSystem
    {
        /// <summary>
        /// The host owns workplace tenancy and company accounting. The spawn/search systems
        /// choose which business occupies a property; the accounting system only derives
        /// CompanyStatisticData from local resources, employees and utility inputs. Channel 22
        /// supplies those accounting fields already, so clients need not calculate them first.
        /// Loaded-world figures remain available until their first host page arrives.
        ///
        /// Not on this list, deliberately:
        ///
        /// * <c>CompanyProfitabilitySystem</c> also updates entities outside the rented-company
        ///   roster carried by channel 22. Holding it globally would freeze their monthly figures.
        /// * <c>CompanyDividendSystem</c> transfers money to employee households, including
        ///   commuters that residential occupancy does not replicate.
        /// * <c>CompanyMoveAwaySystem</c> stays running. It executes a closure the host roster
        ///   asked for; it does not choose who closes.
        /// * <c>CommercialAISystem</c> and <c>IndustrialAISystem</c> stay running. Besides
        ///   proposing that a business give up, they produce resource orders and demand signals the
        ///   rest of the local simulation reads. The every-update lifecycle boundary strips their
        ///   move-away and property-seeking proposals before the consumers run, which is the
        ///   narrow part, and leaves the rest intact.
        /// * <c>PropertyProcessingSystem</c> and <c>PropertyRenterSystem</c> stay running. They
        ///   maintain the native renter links and execute the move-ins this system queues.
        /// </summary>
        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "CompanyStats", "workplace state", "business tenancy and accounting",
            "company state authority",
            typeof(global::Game.Simulation.CommercialSpawnSystem),
            typeof(global::Game.Simulation.IndustrialSpawnSystem),
            typeof(global::Game.Simulation.CommercialFindPropertySystem),
            typeof(global::Game.Simulation.IndustrialFindPropertySystem),
            typeof(global::Game.Simulation.CompanyEconomyStatisticSystem));

        /// <summary>
        /// Hands workplace tenancy to the host. Idempotent, and re-checked every update so a
        /// system the game re-enables on a state change does not quietly start opening businesses
        /// this peer's own way again.
        /// </summary>
        private void ApplyLocalAuthority(MultiplayerSession session)
        {
            // A session hosted with simulation sync off never announces these decisions, so
            // holding the local systems would leave this city unable to make them either.
            if (!session.SimulationSyncEnabled)
            {
                RestoreLocalAuthority();
                return;
            }
            _authority.Apply(World, session);
        }

        /// <summary>
        /// Gives the local simulation its economy back when the session ends. Without this a
        /// player who leaves a session keeps a city no business can ever open in again.
        /// </summary>
        private void RestoreLocalAuthority() => _authority.Restore(World);

        /// <summary>
        /// Called immediately before the native move-away executor. The systems that propose a
        /// closure also produce figures and demand this peer still needs, so rather than holding
        /// them their proposals are removed here, at the last point before anything acts on them.
        /// Closures this system asked for are whitelisted and pass straight through.
        ///
        /// Both cancellations are issued in bulk: a busy economy proposes plenty of these, and one
        /// structural change per business would be a sync point each.
        /// </summary>
        internal void CancelClientLifecycleDecisions()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != SessionRole.Client) return;

            if (!_departingCompanies.IsEmptyIgnoreFilter)
            {
                if (_authorizedMoveAways.Count == 0)
                {
                    _cancelledDecisions += _departingCompanies.CalculateEntityCount();
                    EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                        _departingCompanies);
                }
                else
                {
                    NativeArray<Entity> departing =
                        _departingCompanies.ToEntityArray(Allocator.Temp);
                    NativeList<Entity> cancelled =
                        new NativeList<Entity>(departing.Length, Allocator.Temp);
                    try
                    {
                        for (int i = 0; i < departing.Length; i++)
                        {
                            Entity company = departing[i];
                            if (_authorizedMoveAways.Contains(company)) continue;
                            cancelled.Add(company);
                        }
                        if (cancelled.Length > 0)
                        {
                            _cancelledDecisions += cancelled.Length;
                            EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                                cancelled.AsArray());
                        }
                    }
                    finally
                    {
                        cancelled.Dispose();
                        departing.Dispose();
                    }
                }
            }

            // PropertySeeker is enableable, so this query holds exactly the businesses whose flag
            // is set. Clearing the bits a chunk at a time replaces one main-thread call each.
            if (!_companySeekers.IsEmptyIgnoreFilter)
                EntityManager.SetComponentEnabled<global::Game.Agents.PropertySeeker>(
                    _companySeekers, false);

            PruneAuthorizedMoveAways();
        }

        private void AuthorizeMoveAway(Entity company) => _authorizedMoveAways.Add(company);

        /// <summary>
        /// The whitelist only ever holds businesses this system is closing, so it is naturally
        /// small; it is still swept once it grows, because a closure that never completes would
        /// otherwise keep a dead entity handle alive for the rest of the session.
        /// </summary>
        private void PruneAuthorizedMoveAways()
        {
            if (_authorizedMoveAways.Count <= 1024) return;
            _authorizedScratch.Clear();
            foreach (Entity company in _authorizedMoveAways)
                if (!EntityManager.Exists(company) ||
                    EntityManager.HasComponent<Deleted>(company) ||
                    !EntityManager.HasComponent<CompanyData>(company))
                    _authorizedScratch.Add(company);
            for (int i = 0; i < _authorizedScratch.Count; i++)
                _authorizedMoveAways.Remove(_authorizedScratch[i]);
            _authorizedScratch.Clear();
        }
    }
}
