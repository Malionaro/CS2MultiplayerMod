using System;
using System.Collections.Generic;
using Game.Common;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ZoneSyncSystem
    {
        private void ApplyZoneCommands(bool retryDue, long now)
        {
            Dictionary<long, List<Entity>> lookup = GetBlockLookup(now);
            int remaining = MaxApplyPerFrame;

            // Fresh states take priority. Because each source layout occurs at most once in _ready,
            // one frame can never structurally update the same Block over and over.
            int fresh = Math.Min(_ready.Count, remaining);
            for (int i = 0; i < fresh; i++)
            {
                ZoneBlockKey key;
                ZonePaintCommand command;
                if (!_ready.TryTake(out key, out command)) break;
                remaining--;

                bool matched, changed, absentGrid;
                ApplyOne(command, lookup, out matched, out changed, out absentGrid);
                if (changed) _diagnosticApplied++;
                if (!matched)
                {
                    var pending = new PendingZone
                    {
                        Command = command,
                        DeadlineMs = _retryClock.NowMs + ZoneRetryWindowMs,
                    };
                    if (!_pending.TrySetLatest(key, pending, MaxPendingZones))
                    {
                        RecoverFromQueueOverflow("zone target retry queue overflow");
                        return;
                    }
                    _diagnosticDeferred++;
                }
            }

            // Retry only with budget left after fresh work. If fresh work spends the frame budget,
            // leave the timer due so retries run at the first available frame instead of waiting
            // another interval.
            if (retryDue && remaining > 0)
            {
                int retries = Math.Min(_pending.Count, remaining);
                if (retries > 0) _lastRetryMs = now;
                for (int i = 0; i < retries; i++)
                {
                    ZoneBlockKey key;
                    PendingZone pending;
                    if (!_pending.TryTake(out key, out pending)) break;

                    bool matched, changed, absentGrid;
                    ApplyOne(pending.Command, lookup, out matched, out changed, out absentGrid);
                    if (matched)
                    {
                        WithdrawZoneRecovery(pending, now);
                        if (changed) _diagnosticApplied++;
                    }
                    else
                    {
                        if (pending.RecoveryReport == null && _retryClock.NowMs >= pending.DeadlineMs)
                        {
                            // Every cell still unresolved sits on a live block that exposes no
                            // zonable cell there. Zoning it is a no-op on this machine, so the
                            // patch is dropped rather than treated as a diverged city.
                            if (!absentGrid)
                            {
                                _diagnosticUnzonable++;
                                continue;
                            }
                            _diagnosticExpired++;
                            ZonePaintCommand command = pending.Command;
                            pending.RecoveryReport = Diagnostics.ResyncReport
                                .Create("edited zoning cells did not resolve", "zone",
                                    Diagnostics.ResyncEvidence.MissingTarget)
                                .About("zoning patch at " + command.PosX + "," + command.PosY + "," +
                                    command.PosZ + " facing " + command.DirX + "," + command.DirZ +
                                    " size " + command.SizeX + "x" + command.SizeY)
                                .Tried("retried unresolved cells for 12 s of eligible time; continuing during the recovery hold");
                            Infrastructure.SyncInbox.Settle(pending.RecoveryReport);
                        }
                        // Keep trying while recovery is held, and withdraw the report if the grid
                        // catches up. Reporting every retry would falsely corroborate the same miss.
                        // The removed slot remains ours, so reinsertion cannot exceed the bound.
                        _pending.TrySetLatest(key, pending, MaxPendingZones);
                    }
                }
            }
        }

        private static void WithdrawZoneRecovery(PendingZone pending, long now)
        {
            Diagnostics.ResyncReport report = pending.RecoveryReport;
            if (report == null) return;
            Diagnostics.ResyncArbiter.Withdraw(report.Subsystem, report.Reason, report.Subject, now,
                "the zoning patch resolved or was merged into a newer pending edit");
        }

        private Dictionary<long, List<Entity>> GetBlockLookup(long now)
        {
            if (!_blockLookupBuilt || now < _blockLookupBuiltAtMs ||
                now - _blockLookupBuiltAtMs >= BlockLookupRefreshMs)
                RebuildBlockLookup(now);
            return _blockLookup;
        }

        private void RebuildBlockLookup(long now)
        {
            ClearBlockLookup();
            NativeArray<Entity> blocks = _allBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    Block block = EntityManager.GetComponentData<Block>(blocks[i]);
                    AddBlockToLookup(blocks[i], block);
                }
            }
            finally
            {
                blocks.Dispose();
            }
            _blockLookupBuilt = true;
            _blockLookupBuiltAtMs = now;
        }

        private void ClearBlockLookup()
        {
            foreach (List<Entity> entities in _blockLookup.Values)
            {
                entities.Clear();
                _blockLookupListPool.Add(entities);
            }
            _blockLookup.Clear();
        }

        private void AddBlockToLookup(Entity entity, Block block)
        {
            float2 direction = block.m_Direction;
            float2 right = new float2(direction.y, -direction.x);
            float2 extents = math.abs(direction) * (block.m_Size.y * 4f) +
                             math.abs(right) * (block.m_Size.x * 4f) + ZoneCellMatchRules.LookupPadding;
            float2 center = block.m_Position.xz;
            int minX = (int)math.floor((center.x - extents.x) / BlockLookupBucketSize);
            int maxX = (int)math.floor((center.x + extents.x) / BlockLookupBucketSize);
            int minZ = (int)math.floor((center.y - extents.y) / BlockLookupBucketSize);
            int maxZ = (int)math.floor((center.y + extents.y) / BlockLookupBucketSize);

            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    long key = PackSpatialBucket(x, z);
                    List<Entity> entities;
                    if (!_blockLookup.TryGetValue(key, out entities))
                    {
                        if (_blockLookupListPool.Count > 0)
                        {
                            int last = _blockLookupListPool.Count - 1;
                            entities = _blockLookupListPool[last];
                            _blockLookupListPool.RemoveAt(last);
                        }
                        else
                        {
                            entities = new List<Entity>(8);
                        }
                        _blockLookup.Add(key, entities);
                    }
                    entities.Add(entity);
                }
        }

        /// <summary>
        /// Apply edited cells to the local grid. <paramref name="matched"/> tells the
        /// caller whether every edited cell resolved (so unresolved cells can be retried);
        /// <paramref name="changed"/> whether any cell actually changed;
        /// <paramref name="absentGrid"/> whether any unresolved cell had no zoning grid at all
        /// where the source put it, which is the only miss that can mean a diverged city.
        /// </summary>
        private void ApplyOne(ZonePaintCommand command, Dictionary<long, List<Entity>> lookup,
            out bool matched, out bool changed, out bool absentGrid)
        {
            matched = true;
            changed = false;
            absentGrid = false;
            var changedBlocks = new List<Entity>(4);
            var resolvedZones = new ushort[command.ZoneNames.Length];
            var knownZones = new bool[command.ZoneNames.Length];
            for (int i = 0; i < command.ZoneNames.Length; i++)
            {
                ushort resolved;
                if (_nameToIndex.TryGetValue(command.ZoneNames[i], out resolved) ||
                    TryResolveZoneIndex(command.ZoneNames[i], out resolved))
                {
                    resolvedZones[i] = resolved;
                    knownZones[i] = true;
                }
            }

            for (int c = 0; c < command.Cells.Length; c++)
            {
                if (!command.IsCellEdited(c) || !command.IsCellVisible(c)) continue;

                byte tableIndex = command.Cells[c];
                ushort wanted = 0;
                if (tableIndex != ZonePaintCommand.NoneCell)
                {
                    if (!knownZones[tableIndex]) { matched = false; absentGrid = true; continue; }
                    wanted = resolvedZones[tableIndex];
                }

                float sourceX, sourceY, sourceZ;
                if (!command.TryGetCellCenter(c, out sourceX, out sourceY, out sourceZ))
                {
                    matched = false;
                    absentGrid = true;
                    continue;
                }

                Entity blockEntity;
                int localIndex;
                bool gridCoversCell;
                if (!TryFindLocalCell(lookup, command,
                        new float3(sourceX, sourceY, sourceZ), command.CellStates[c],
                        out blockEntity, out localIndex, out gridCoversCell))
                {
                    matched = false;
                    if (!gridCoversCell) absentGrid = true;
                    continue;
                }

                // Retire each successful cell separately. Replaying a partially matched block
                // must not undo a newer paint/erase on a cell that already succeeded.
                command.CellStates[c] &= unchecked((byte)~ZonePaintCommand.StateEdited);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                Cell cell = cells[localIndex];
                if (cell.m_Zone.m_Index == wanted) continue;
                cell.m_Zone = new ZoneType { m_Index = wanted };
                cells[localIndex] = cell;
                AddUnique(changedBlocks, blockEntity);
                changed = true;
            }

            for (int i = 0; i < changedBlocks.Count; i++)
            {
                Entity block = changedBlocks[i];
                if (IsLiveBlock(block) && !EntityManager.HasComponent<Updated>(block))
                    EntityManager.AddComponent<Updated>(block);
            }
        }

        /// <summary>
        /// Map one visible source cell to the closest semantically compatible visible local cell.
        /// Searching immediate index neighbours handles a half-cell alignment difference without
        /// copying any of the sender's locally generated state flags.
        /// <para>
        /// <paramref name="gridCoversCell"/> separates the two ways this can fail: no live block
        /// reaches the position at all (the zoning grid itself is missing here) versus a block
        /// that reaches it but exposes no zonable cell there. Only the first is a divergence.
        /// </para>
        /// </summary>
        private bool TryFindLocalCell(Dictionary<long, List<Entity>> lookup,
            ZonePaintCommand command, float3 sourcePosition, byte sourceState,
            out Entity bestBlock, out int bestIndex, out bool gridCoversCell)
        {
            bestBlock = Entity.Null;
            bestIndex = -1;
            gridCoversCell = false;
            List<Entity> candidates;
            if (!lookup.TryGetValue(SpatialBucket(sourcePosition.xz), out candidates)) return false;

            float2 sourceDirection = math.normalizesafe(new float2(command.DirX, command.DirZ));
            float2 sourceBlockPosition = new float2(command.PosX, command.PosZ);
            float bestScore = float.MaxValue;

            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                Entity blockEntity = candidates[candidateIndex];
                if (!IsLiveBlock(blockEntity)) continue;
                gridCoversCell = true;

                Block block = EntityManager.GetComponentData<Block>(blockEntity);
                float signedAlignment = math.dot(sourceDirection,
                    math.normalizesafe(block.m_Direction));
                float stripOffset = math.abs(math.dot(block.m_Position.xz - sourceBlockPosition,
                    sourceDirection));

                int2 baseIndex = ZoneUtils.GetCellIndex(block, sourcePosition.xz);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity, true);
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int2 local = baseIndex + new int2(offsetX, offsetY);
                        if (local.x < 0 || local.y < 0 ||
                            local.x >= block.m_Size.x || local.y >= block.m_Size.y)
                            continue;

                        int index = local.y * block.m_Size.x + local.x;
                        if (index < 0 || index >= cells.Length) continue;
                        Cell cell = cells[index];
                        if ((cell.m_State & CellFlags.Visible) == 0) continue;

                        float3 localPosition = ZoneUtils.GetCellPosition(block, local);
                        float distanceSquared = math.lengthsq(localPosition.xz - sourcePosition.xz);
                        float score;
                        if (!ZoneCellMatchRules.TryScore(distanceSquared,
                                block.m_Position.y - sourcePosition.y, signedAlignment, stripOffset,
                                sourceState, PortableCellState(cell.m_State),
                                block.m_Size.x == command.SizeX && block.m_Size.y == command.SizeY,
                                out score)) continue;

                        if (score >= bestScore) continue;
                        bestScore = score;
                        bestBlock = blockEntity;
                        bestIndex = index;
                    }
            }

            return bestBlock != Entity.Null;
        }

        private bool IsLiveBlock(Entity block)
        {
            return block != Entity.Null &&
                EntityManager.Exists(block) &&
                EntityManager.HasComponent<Block>(block) &&
                EntityManager.HasBuffer<Cell>(block) &&
                !EntityManager.HasComponent<Temp>(block) &&
                !EntityManager.HasComponent<Deleted>(block);
        }

        private static void AddUnique(List<Entity> entities, Entity entity)
        {
            if (!entities.Contains(entity)) entities.Add(entity);
        }

        private static long SpatialBucket(float2 position) =>
            PackSpatialBucket((int)math.floor(position.x / BlockLookupBucketSize),
                              (int)math.floor(position.y / BlockLookupBucketSize));

        private static long PackSpatialBucket(int x, int z) => ((long)x << 32) | (uint)z;
    }
}
