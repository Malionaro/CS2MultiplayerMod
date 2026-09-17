using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        // A native object brush already commits this load shape in one frame. Preserve that on the
        // receiver, but keep a hard ceiling so a hostile peer cannot turn batching into an
        // unbounded main-thread spike.
        private const int MaxBrushPlacementsPerFrame =
            ObjectPlacementBatchCommand.MaxPlacements;
        private int _rzFrameBatchedObjects;
        private bool _suppressBatchedObjectDetail;

        private bool TryCaptureObjectBrushPlacements(MultiplayerSession session,
            List<Entity> created)
        {
            if (!LocalObjectBrushAppliedThisFrame || created.Count == 0) return false;

            var batches = new Dictionary<string,
                List<ObjectPlacementBatchCommand.Placement>>();
            for (int i = 0; i < created.Count; i++)
            {
                Entity entity = created[i];
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;

                // A brush placement is expected to be a simple tree/prop. If a future game build
                // permits brushing a prefab with an owned graph, retain the existing complete-
                // lifecycle recovery path instead of flattening that graph into this batch.
                if (RequiresCompleteObjectLifecycle(prefab)) return false;

                string name = _prefabSystem.GetPrefabName(prefab);
                if (string.IsNullOrEmpty(name)) continue;
                Transform transform = EntityManager.GetComponentData<Transform>(entity);
                int randomSeed = EntityManager.HasComponent<PseudoRandomSeed>(entity)
                    ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                    : (int)(math.hash(transform.m_Position) & 0xffffu);
                float age = EntityManager.HasComponent<Tree>(entity)
                    ? TreeAge(EntityManager.GetComponentData<Tree>(entity))
                    : 0f;

                List<ObjectPlacementBatchCommand.Placement> placements;
                if (!batches.TryGetValue(name, out placements))
                {
                    placements = new List<ObjectPlacementBatchCommand.Placement>();
                    batches[name] = placements;
                }
                placements.Add(new ObjectPlacementBatchCommand.Placement
                {
                    PosX = transform.m_Position.x,
                    PosY = transform.m_Position.y,
                    PosZ = transform.m_Position.z,
                    RotX = transform.m_Rotation.value.x,
                    RotY = transform.m_Rotation.value.y,
                    RotZ = transform.m_Rotation.value.z,
                    RotW = transform.m_Rotation.value.w,
                    RandomSeed = randomSeed,
                    Age = age,
                });
                RecordDiagnostic(name);
            }

            foreach (KeyValuePair<string, List<ObjectPlacementBatchCommand.Placement>> batch in
                     batches)
            {
                for (int offset = 0; offset < batch.Value.Count;
                     offset += ObjectPlacementBatchCommand.MaxPlacements)
                {
                    int count = System.Math.Min(ObjectPlacementBatchCommand.MaxPlacements,
                        batch.Value.Count - offset);
                    var chunk = new ObjectPlacementBatchCommand.Placement[count];
                    batch.Value.CopyTo(offset, chunk, 0, count);
                    var command = new ObjectPlacementBatchCommand
                    {
                        PrefabName = batch.Key,
                        Placements = chunk,
                    };
                    session.SendCommand(0, ObjectPlacementBatchCommand.Id, command.Encode());
                }
            }

            SyncLog.Trace(LogTopic.Buildings, "object brush captured batches=" + batches.Count +
                " placements=" + created.Count);
            return true;
        }

        /// <summary>
        /// Returns false only when a valid batch must wait for the next frame's brush allowance.
        /// Malformed/unsupported batches are consumed and logged so they cannot wedge the queue.
        /// </summary>
        private bool TryRealizeObjectPlacementBatch(SimulationCommandMessage message, long now)
        {
            ObjectPlacementBatchCommand batch;
            try { batch = ObjectPlacementBatchCommand.Decode(message.Body); }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: dropping malformed object-placement batch: " + ex.Message);
                return true;
            }

            if (_rzFrameBatchedObjects > 0 &&
                _rzFrameBatchedObjects + batch.Placements.Length > MaxBrushPlacementsPerFrame)
                return false;

            Entity prefab;
            if (!_prefabIndex.TryResolve(batch.PrefabName,
                    candidate => EntityManager.HasComponent<ObjectData>(candidate), out prefab))
            {
                SyncLog.Warn(LogTopic.Buildings, "BuildSync realize: unknown batched prefab '" +
                    batch.PrefabName + "' from player " + message.OriginPlayerId + "; skipping.");
                return true;
            }
            if (IsSimulationOnlyPlacementPrefab(prefab) ||
                RequiresCompleteObjectLifecycle(prefab))
            {
                RecordRefused(batch.PrefabName);
                return true;
            }

            _rzFrameBatchedObjects += batch.Placements.Length;
            int before = _rzFrameSpawned;
            _suppressBatchedObjectDetail = true;
            try
            {
                for (int i = 0; i < batch.Placements.Length; i++)
                {
                    ObjectPlacementBatchCommand.Placement value = batch.Placements[i];
                    RealizeCommand(new ObjectPlacementCommand
                    {
                        PrefabName = batch.PrefabName,
                        PosX = value.PosX,
                        PosY = value.PosY,
                        PosZ = value.PosZ,
                        RotX = value.RotX,
                        RotY = value.RotY,
                        RotZ = value.RotZ,
                        RotW = value.RotW,
                        RandomSeed = value.RandomSeed,
                        Age = value.Age,
                        AttachKind = ObjectAttachKind.None,
                    }, prefab, message.OriginPlayerId, now);
                }
            }
            finally { _suppressBatchedObjectDetail = false; }

            SyncLog.Detail(LogTopic.Buildings, "BuildSync realize: placed object-brush batch '" +
                batch.PrefabName + "' n=" + (_rzFrameSpawned - before) +
                " requested=" + batch.Placements.Length + ".");
            return true;
        }
    }
}
