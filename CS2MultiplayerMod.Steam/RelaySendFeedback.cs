using System;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    /// <summary>Tracks acknowledged bytes while the session keeps replenishing the outbox.</summary>
    internal sealed class RelaySendFeedback
    {
        private long _acknowledged;

        public long Sample(long acceptedBytes, long outstandingBytes)
        {
            long acknowledged = Math.Max(0L, acceptedBytes - Math.Max(0L, outstandingBytes));
            long moved = Math.Max(0L, acknowledged - _acknowledged);
            _acknowledged = Math.Max(_acknowledged, acknowledged);
            return moved;
        }

        // A growing wire rate can be retransmissions. Probe only when useful delivery
        // is keeping up with the current pace, including before quality reports arrive.
        public static bool CanProbe(long goodput, int sendRate) =>
            sendRate > 0 && goodput >= sendRate * 3L / 4L;
    }
}
