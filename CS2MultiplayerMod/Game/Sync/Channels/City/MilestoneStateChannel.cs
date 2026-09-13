using Unity.Entities;
using Unity.Collections;
using Game.City;
using Game.Prefabs;
using Game.Simulation;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the achieved milestone level and loan limit, then repairs the prefab unlock
    /// cascade that belongs to every reached milestone. One-time rewards stay host-authoritative.
    /// </summary>
    public sealed class MilestoneStateChannel : IStateChannel, IPumpedStateChannel
    {
        public const byte Id = 4;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private EntityQuery _milestones;
        private DeferredPrefabUnlocker _unlocks;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(
                ComponentType.ReadWrite<MilestoneLevel>(),
                ComponentType.ReadWrite<Creditworthiness>());
            // Match MilestoneSystem's catalogue query. MilestoneData is visible without the
            // IncludePrefab option required by development-tree node queries.
            _milestones = em.CreateEntityQuery(ComponentType.ReadOnly<MilestoneData>());
            _unlocks = new DeferredPrefabUnlocker(em);
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;
            Entity city = _query.GetSingletonEntity();
            writer.WriteInt(em.GetComponentData<MilestoneLevel>(city).m_AchievedMilestone);
            writer.WriteInt(em.GetComponentData<Creditworthiness>(city).m_Amount);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int level = reader.ReadInt();
            int creditworthiness = reader.ReadInt();
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in milestone state.");
            if (level < 0)
                throw new ProtocolException("Negative achieved milestone: " + level + ".");
            if (creditworthiness < 0)
                throw new ProtocolException("Negative creditworthiness: " + creditworthiness + ".");
            if (_query.CalculateEntityCount() == 0) return;

            NativeArray<Entity> milestoneEntities = _milestones.ToEntityArray(Allocator.Temp);
            NativeArray<MilestoneData> milestoneData =
                _milestones.ToComponentDataArray<MilestoneData>(Allocator.Temp);
            try
            {
                int highestMilestone = 0;
                for (int i = 0; i < milestoneData.Length; i++)
                    if (milestoneData[i].m_Index > highestMilestone)
                        highestMilestone = milestoneData[i].m_Index;
                if (level > highestMilestone)
                    throw new ProtocolException("Achieved milestone " + level +
                        " exceeds highest known milestone " + highestMilestone + ".");

                ApplyValidated(em, level, creditworthiness, milestoneEntities, milestoneData);
            }
            finally
            {
                milestoneData.Dispose();
                milestoneEntities.Dispose();
            }
        }

        private void ApplyValidated(EntityManager em, int level, int creditworthiness,
            NativeArray<Entity> milestoneEntities, NativeArray<MilestoneData> milestoneData)
        {
            Entity e = _query.GetSingletonEntity();
            MilestoneLevel m = em.GetComponentData<MilestoneLevel>(e);
            m.m_AchievedMilestone = level;
            em.SetComponentData(e, m);
            em.SetComponentData(e, new Creditworthiness { m_Amount = creditworthiness });

            // Repair every achieved milestone, even when the number already matched. Older
            // clients could receive the number without the prefab unlock event, and a later
            // snapshot must be able to heal that partial state. Queueing only Unlock keeps cash,
            // development points and other additive MilestoneReachedEvent rewards untouched. The
            // loan limit above is an absolute host value, so a repeated snapshot cannot add it twice.
            int queued = ReconcileUnlocks(em, level, milestoneEntities, milestoneData);
            if (queued > 0)
            {
                SyncLog.Detail(LogTopic.City, "MilestoneState: queued " + queued +
                    " missing milestone unlock(s) through level " + level + ".");
            }
        }

        private int ReconcileUnlocks(EntityManager em, int achievedMilestone,
            NativeArray<Entity> entities, NativeArray<MilestoneData> milestones)
        {
            int queued = 0;
            for (int i = 0; i < milestones.Length; i++)
            {
                Entity milestone = entities[i];
                bool locked = em.HasComponent<Locked>(milestone) &&
                              em.IsComponentEnabled<Locked>(milestone);
                if (locked && milestones[i].m_Index <= achievedMilestone &&
                    _unlocks.TryQueue(milestone))
                    queued++;
            }
            return queued;
        }

        public void Pump(EntityManager em)
        {
            if (_unlocks != null) _unlocks.PruneCompleted();
        }

        public void ResetPending()
        {
            if (_unlocks != null) _unlocks.Reset();
        }
    }
}
