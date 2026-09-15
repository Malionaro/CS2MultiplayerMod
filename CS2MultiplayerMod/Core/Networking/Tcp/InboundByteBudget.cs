using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Reserves frame memory before it is allocated. The event queues are capped by event count,
    /// which says nothing about how much payload is queued behind that count, so a peer that sends
    /// nothing but maximum-size frames can hold far more memory than the count cap suggests.
    /// </summary>
    internal sealed class InboundByteBudget
    {
        internal const long PerConnectionLimit = 32L * 1024 * 1024;
        internal const long AggregateLimit = 64L * 1024 * 1024;

        // A queued frame costs more than its payload: the array header, the event wrapper and the
        // queue node ride along with it. Charged so many small frames cannot slip past the budget.
        private const long EventOverheadBytes = 132;

        private readonly Dictionary<ConnectionId, long> _connections =
            new Dictionary<ConnectionId, long>();
        private readonly object _gate = new object();
        private long _bytes;

        public bool TryReserve(ConnectionId id, int length)
        {
            if (length < 0) return false;
            long cost = length + EventOverheadBytes;
            lock (_gate)
            {
                long used;
                _connections.TryGetValue(id, out used);
                if (cost > PerConnectionLimit - used || cost > AggregateLimit - _bytes) return false;
                _connections[id] = used + cost;
                _bytes += cost;
                return true;
            }
        }

        public void Release(ConnectionId id, int length)
        {
            long cost = length + EventOverheadBytes;
            lock (_gate)
            {
                long used;
                // A release without a live reservation can only be a double release; charging it
                // would let the budget drift negative and stop bounding anything.
                if (!_connections.TryGetValue(id, out used)) return;
                long remaining = used - cost;
                if (remaining <= 0) _connections.Remove(id);
                else _connections[id] = remaining;
                _bytes -= cost < used ? cost : used;
            }
        }
    }
}
