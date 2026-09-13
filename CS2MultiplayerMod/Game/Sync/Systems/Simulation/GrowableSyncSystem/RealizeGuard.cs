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
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Keeping a client's own simulation from writing zoned buildings: rejecting the ones it grew
    // locally, and checking that the ones the host asked for actually landed with the road and
    // utility connections they need.
    public partial class GrowableSyncSystem
    {
        /// <summary>
        /// Remembers that a building was just asked for at this spot. The definition does not
        /// become an entity until a later phase, so the only way to recognise our own building when
        /// it appears is the position we asked for it at.
        /// </summary>
        private void NoteSelfRealized(Entity prefab, float3 position,
            GrowableLifecycleCommand command, long now)
        {
            if (_selfRealized.Count >= MaxSelfRealized) _selfRealized.RemoveAt(0);
            _selfRealized.Add(new PendingRealizedSpawn
            {
                Prefab = prefab,
                Position = position,
                Expiry = now + SelfRealizedWindowMs,
                Command = command,
            });
        }

        private bool TryTakeSelfRealized(Entity prefab, float3 position, long now,
            out GrowableLifecycleCommand command)
        {
            command = null;
            for (int i = _selfRealized.Count - 1; i >= 0; i--)
            {
                PendingRealizedSpawn entry = _selfRealized[i];
                if (entry.Expiry <= now) { _selfRealized.RemoveAt(i); continue; }
                if (entry.Prefab != prefab ||
                    math.distancesq(entry.Position.xz, position.xz) >
                    AnchorMatchDistance * AnchorMatchDistance) continue;
                command = entry.Command;
                _selfRealized.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes zoned buildings this client grew by itself. Its spawner is held for as long as
        /// the session is synchronized, so in normal running this finds nothing - but authority is
        /// handed back whenever sync drops (a resync, a world reload), and anything grown in that
        /// window would otherwise stand forever on a lot the host has its own plans for.
        ///
        /// Catching them as they appear is what keeps the invariant simple: on a client, every
        /// zoned building came from the host.
        /// </summary>
        private void RejectLocallyGrownBuildings(long now)
        {
            if (_createdBuildings.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!IsAutonomousGrowable(entity, now)) continue;

                    global::Game.Objects.Transform transform = EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(entity);
                    float3 position = transform.m_Position;
                    GrowableLifecycleCommand command;
                    if (TryTakeSelfRealized(prefab, position, now, out command))
                    {
                        // The definition is now a real native building. This is the first point at
                        // which its construction clock and state payload can be applied safely.
                        ApplyConditionAndState(entity, command);
                        if (_buildSync != null)
                            _buildSync.TrackRemoteBuilding(entity, prefab, position,
                                transform.m_Rotation,
                                roadConnectionExpected: true, source: "growable");
                        continue;
                    }

                    EntityManager.AddComponent<Deleted>(entity);
                    _rejectedLocal++;
                    SyncLog.Warn(LogTopic.Buildings, "GrowableSync: this client grew '" +
                        PrefabIndexSafeName(prefab) + "' at " + Format(position) +
                        " on its own; removed (the host decides zoned buildings).");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

    }
}
