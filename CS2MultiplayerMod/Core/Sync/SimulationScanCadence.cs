using System;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>
    /// Keep fallback scanning near its 1x frequency as simulation ticks accelerate. This must
    /// never gate event capture, arrived state, native-writer corrections or queue draining.
    /// Partitions advance only when visited, independently of the game's current partition.
    /// </summary>
    public sealed class SimulationScanCadence
    {
        private bool _started;
        private double _credit;
        private int _partition;

        public bool TryRun(float selectedSpeed)
        {
            if (!_started)
            {
                _started = true;
                return true;
            }
            double scale = float.IsNaN(selectedSpeed) || float.IsInfinity(selectedSpeed)
                ? 1d : Math.Max(1d, Math.Min(16d, selectedSpeed));
            if (scale == 1d)
            {
                _credit = 0d;
                return true;
            }
            _credit += 1d / scale;
            if (_credit + 1e-9 < 1d) return false;
            _credit = Math.Max(0d, _credit - 1d);
            return true;
        }

        public bool TryTakePartition(float selectedSpeed, int partitionCount, out int partition)
        {
            if (partitionCount <= 0) throw new ArgumentOutOfRangeException(nameof(partitionCount));
            partition = 0;
            if (!TryRun(selectedSpeed)) return false;
            partition = _partition % partitionCount;
            _partition = (partition + 1) % partitionCount;
            return true;
        }

        public void Reset()
        {
            _started = false;
            _credit = 0d;
            _partition = 0;
        }
    }
}
