using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the city loan as a player-editable channel. The host arbitrates client
    /// requests through the game's ChangeLoan API. Clients apply the confirmed balance
    /// directly: the separate money channel already includes the transaction.
    /// </summary>
    public sealed class LoanStateChannel : IStateChannel
    {
        public const byte Id = 12;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<global::Game.Simulation.Loan>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;

            // Frame indices differ between machines; only the amount is shared state.
            writer.WriteInt(em.GetComponentData<global::Game.Simulation.Loan>(_query.GetSingletonEntity()).m_Amount);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int amount = reader.ReadInt();
            if (_query.CalculateEntityCount() == 0) return;

            Entity entity = _query.GetSingletonEntity();
            var loan = em.GetComponentData<global::Game.Simulation.Loan>(entity);
            if (loan.m_Amount == amount) return;

            if (Mod.Service != null && Mod.Service.Session.Role == SessionRole.Client)
            {
                // ChangeLoan clamps repayment against local cash and queues it for a later
                // frame. After the money snapshot this can refuse an already-paid repayment,
                // charge it twice, or let edit detection send the old loan back to the host.
                // Land the authoritative amount now without another treasury transaction.
                loan.m_Amount = amount;
                loan.m_LastModified = em.World
                    .GetOrCreateSystemManaged<global::Game.Simulation.SimulationSystem>().frameIndex;
                em.SetComponentData(entity, loan);
                return;
            }

            em.World.GetOrCreateSystemManaged<global::Game.Tools.LoanSystem>().ChangeLoan(amount);
        }
    }
}
