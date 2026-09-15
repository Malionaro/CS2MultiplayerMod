using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        /// <summary>Send a blob to every handshaked peer, one recipient at a time.</summary>
        public void SendBlob(string channel, byte[] data) =>
            SendBlobTo(ConnectionId.None, channel, data);

        /// <summary>
        /// Send a blob to a single peer - auto-ships map to just-joined client
        /// without re-sending to everyone already in the session.
        /// </summary>
        public void SendBlobTo(ConnectionId target, string channel, byte[] data) =>
            SendBlobTo(target, channel, 0, data);

        /// <summary>Send an epoch-tagged blob to one peer.</summary>
        public void SendBlobTo(ConnectionId target, string channel, long transferId, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            using (var source = new BlobSource(new MemoryStream(data, false)))
                ChunkAndSend(channel, transferId, source, target);
        }

        public void SendBlobTo(ConnectionId target, string channel, long transferId, BlobSource source) =>
            ChunkAndSend(channel, transferId, source, target);

        private sealed class OutgoingBlob
        {
            public ConnectionId Target;
            public string Channel;
            public long TransferId;
            public BlobSource Data;
            public int Offset;
        }

        // One reusable chunk buffer: SendTo encodes the message before it returns, so the same
        // buffer can carry every chunk of every transfer.
        private readonly byte[] _blobChunkBuffer = new byte[ProtocolConstants.BlobChunkBytes];
        // Ceiling for one transfer and for everything being reassembled at once.
        private const long MaxBlobMemoryBytes = BlobSource.MaxBytes;
        // Encoded backlog allowed to stand ahead of the socket before the next chunk is cut.
        private const long BlobSendWindowBytes = 2L * 1024 * 1024;
        private readonly ConcurrentQueue<OutgoingBlob> _outgoingBlobs = new ConcurrentQueue<OutgoingBlob>();
        private readonly Dictionary<string, long> _completedBlobTransfers = new Dictionary<string, long>();

        private void ChunkAndSend(string channel, long transferId, BlobSource data, ConnectionId target)
        {
            if (_transport == null || Status != SessionStatus.Connected || data == null) return;

            // Blobs flow host → client only; a client has no business streaming one.
            if (Role != SessionRole.Host)
            {
                _log.Warn(LogTopic.WorldTransfer, "Ignoring outgoing blob '" + channel +
                    "': only the host streams blobs.");
                return;
            }
            if ((transferId > 0 && (!_worldSyncSuspended || transferId != _worldSyncEpoch)) ||
                (_worldSyncSuspended && transferId == 0))
            {
                _log.Warn(LogTopic.WorldTransfer, "Ignoring outgoing blob '" + channel +
                    "' transfer " + transferId +
                    ": it does not match the active world-sync epoch.");
                return;
            }

            if (data.Length == 0 || data.Length > MaxBlobMemoryBytes ||
                string.IsNullOrEmpty(channel) || channel.Length > WireGuard.MaxNameLength)
                throw new ArgumentException("Invalid outgoing blob.");
            if (target.IsNone)
            {
                foreach (Peer peer in _peers.Values)
                    if (peer.Handshaked) ChunkAndSend(channel, transferId, data, peer.Connection);
                return;
            }
            // Fanout retains one shared snapshot. A different source must wait for this transfer.
            foreach (OutgoingBlob queued in _outgoingBlobs)
            {
                if (!ReferenceEquals(queued.Data, data))
                    throw new InvalidOperationException("Another snapshot is still queued.");
                if (queued.Target == target && queued.Channel == channel &&
                    queued.TransferId == transferId) return;
            }
            if (_outgoingBlobs.Count >= 24)
                throw new InvalidOperationException("Too many snapshot recipients.");
            if (!_outgoingBlobActive)
            {
                _outgoingBlobTotal = 0;
                _outgoingBlobSent = 0;
            }
            data.Retain();
            _outgoingBlobs.Enqueue(new OutgoingBlob
                { Target = target, Channel = channel, TransferId = transferId, Data = data });
            _outgoingBlobTotal += data.Length;
            _outgoingBlobActive = true;
        }

        private void ClearOutgoingBlobs()
        {
            while (_outgoingBlobs.TryDequeue(out OutgoingBlob blob)) blob.Data.Dispose();
        }

        private void PumpOutgoingBlobs()
        {
            // Bound encoded backlog and work per update; recipients are served sequentially.
            for (int chunks = 0; chunks < 8 && _outgoingBlobs.Count > 0 &&
                 _transport.PendingSendBytes < BlobSendWindowBytes; chunks++)
            {
                OutgoingBlob next;
                if (!_outgoingBlobs.TryPeek(out next)) break;
                Peer peer;
                if (!_peers.TryGetValue(next.Target.Value, out peer) || !peer.Handshaked ||
                    (next.TransferId > 0 && (!_worldSyncSuspended || next.TransferId != _worldSyncEpoch)))
                {
                    _outgoingBlobTotal -= next.Data.Length - next.Offset;
                    _outgoingBlobs.TryDequeue(out _);
                    next.Data.Dispose();
                    continue;
                }
                int size = Math.Min(ProtocolConstants.BlobChunkBytes, next.Data.Length - next.Offset);
                byte[] chunk = _blobChunkBuffer;
                try { next.Data.Read(next.Offset, chunk, size); }
                catch (Exception ex)
                {
                    _log.Warn(LogTopic.WorldTransfer, "Snapshot read failed: " + ex.Message);
                    _transport.Disconnect(next.Target);
                    _outgoingBlobs.TryDequeue(out _);
                    next.Data.Dispose();
                    continue;
                }
                next.Offset += size;
                SendTo(next.Target, new BlobChunkMessage(next.Channel, next.TransferId,
                    next.Data.Length, next.Offset == next.Data.Length, chunk, size));
                if (next.Offset == next.Data.Length)
                {
                    _outgoingBlobs.TryDequeue(out _);
                    next.Data.Dispose();
                }
            }
        }

        private void HandleBlobChunk(ConnectionId from, Peer peer, BlobChunkMessage chunk, long nowUnixMs)
        {
            // Blobs flow host → client only. The "map" channel is auto-LOADED as a
            // savegame on arrival, so accepting blobs from clients would let any joiner
            // replace the host's running city.
            if (Role == SessionRole.Host)
            {
                Punt(from, peer, "client attempted to stream a blob", "BlobChunk");
                return;
            }
            if (chunk.TransferId < 0 ||
                (_worldSyncSuspended && chunk.TransferId != _worldSyncEpoch) ||
                (!_worldSyncSuspended && chunk.TransferId != 0))
            {
                _log.Warn(LogTopic.WorldTransfer, "Dropping blob '" + (chunk.Channel ?? "<null>") +
                    "' transfer " + chunk.TransferId +
                    ": it does not match active world-sync epoch " +
                    (_worldSyncSuspended ? _worldSyncEpoch.ToString() : "none") + ".");
                return;
            }

            // Only channels the game layer registered are expected — and each carries
            // its own size ceiling (a savegame cap is far below the 512 MiB of old).
            int maxBytes;
            if (string.IsNullOrEmpty(chunk.Channel) ||
                !_allowedBlobChannels.TryGetValue(chunk.Channel, out maxBytes))
            {
                _log.Warn(LogTopic.WorldTransfer, "Dropping blob chunk on unregistered channel '" +
                    (chunk.Channel ?? "<null>") + "'.");
                return;
            }

            long completedTransfer;
            if (chunk.TransferId > 0 && _completedBlobTransfers.TryGetValue(chunk.Channel, out completedTransfer) &&
                completedTransfer == chunk.TransferId) return;

            if (chunk.TotalBytes <= 0 || chunk.TotalBytes > maxBytes)
            {
                _log.Warn(LogTopic.WorldTransfer, "Dropping blob '" + chunk.Channel +
                    "': announced " + chunk.TotalBytes + " bytes is outside (0, " + maxBytes +
                    "].");
                _blobs.Remove(chunk.Channel);
                _blobTransferIds.Remove(chunk.Channel);
                ClearBlobProgress();
                return;
            }

            BlobReassembler reassembler;
            long activeTransferId;
            if (_blobs.TryGetValue(chunk.Channel, out reassembler) &&
                (!_blobTransferIds.TryGetValue(chunk.Channel, out activeTransferId) ||
                 activeTransferId != chunk.TransferId))
            {
                _log.Warn(LogTopic.WorldTransfer, "Replacing incomplete blob '" + chunk.Channel +
                    "' transfer " + activeTransferId + " with transfer " + chunk.TransferId + ".");
                _blobs.Remove(chunk.Channel);
                _blobTransferIds.Remove(chunk.Channel);
                reassembler = null;
            }
            if (!_blobs.TryGetValue(chunk.Channel, out reassembler))
            {
                long reservedBytes = 0;
                foreach (BlobReassembler active in _blobs.Values) reservedBytes += active.ExpectedBytes;
                if (_blobs.Count >= MaxActiveBlobs || chunk.TotalBytes > MaxBlobMemoryBytes - reservedBytes)
                {
                    _log.Warn(LogTopic.WorldTransfer, "Dropping blob '" + chunk.Channel +
                        "': too many active transfers.");
                    return;
                }
                reassembler = new BlobReassembler(chunk.TotalBytes, nowUnixMs);
                _blobs[chunk.Channel] = reassembler;
                _blobTransferIds[chunk.Channel] = chunk.TransferId;
                _log.Detail(LogTopic.WorldTransfer, "Receiving blob '" + chunk.Channel +
                    "' transfer " + chunk.TransferId + ": expecting " + chunk.TotalBytes +
                    " bytes.");
            }

            try
            {
                reassembler.Append(chunk.TotalBytes, chunk.Data, nowUnixMs);

                IncomingBlobChannel = chunk.Channel;
                IncomingBlobTransferId = chunk.TransferId;
                IncomingBlobReceived = reassembler.ReceivedBytes;
                IncomingBlobTotal = reassembler.ExpectedBytes;

                if (!chunk.Last) return;

                // Completion verifies ReceivedBytes == TotalBytes exactly; a short or
                // overlong transfer never reaches the game layer.
                byte[] data = reassembler.Complete();
                _blobs.Remove(chunk.Channel);
                _blobTransferIds.Remove(chunk.Channel);
                ClearBlobProgress();
                if (chunk.TransferId > 0) _completedBlobTransfers[chunk.Channel] = chunk.TransferId;
                NotifyBlob(chunk.Channel, chunk.TransferId, data);
            }
            catch (ProtocolException ex)
            {
                _log.Warn(LogTopic.WorldTransfer, "Dropping blob '" + chunk.Channel + "': " +
                    ex.Message);
                _blobs.Remove(chunk.Channel);
                _blobTransferIds.Remove(chunk.Channel);
                ClearBlobProgress();
            }
        }

        /// <summary>Abandon transfers that stopped making progress (sender died or stalls on purpose).</summary>
        private void SweepStalledBlobs(long nowUnixMs)
        {
            if (_blobs.Count == 0 || nowUnixMs - _lastBlobSweepMs < 5000) return;
            _lastBlobSweepMs = nowUnixMs;

            List<string> stalled = null;
            foreach (var pair in _blobs)
                if (nowUnixMs - pair.Value.LastChunkAtMs > BlobStallTimeoutMs)
                    (stalled ?? (stalled = new List<string>())).Add(pair.Key);

            if (stalled == null) return;
            foreach (string channel in stalled)
            {
                _log.Warn(LogTopic.WorldTransfer, "Abandoning stalled blob '" + channel +
                    "' (no chunk for " + (BlobStallTimeoutMs / 1000) + " s).");
                _blobs.Remove(channel);
                _blobTransferIds.Remove(channel);
            }
            ClearBlobProgress();
        }

        private void ClearBlobProgress()
        {
            IncomingBlobChannel = null;
            IncomingBlobTransferId = 0;
            IncomingBlobReceived = 0;
            IncomingBlobTotal = 0;
        }

    }
}
