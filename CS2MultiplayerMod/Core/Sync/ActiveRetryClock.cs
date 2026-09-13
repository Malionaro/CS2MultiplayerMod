namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>A retry clock that does not spend deadlines while dependencies prevent progress.</summary>
    public sealed class ActiveRetryClock
    {
        private long _lastWallMs;
        private bool _initialized;
        private bool _wasHeld;

        public long NowMs { get; private set; }

        public long Observe(long wallMs, bool held)
        {
            // Exclude the release interval too: its preceding sample was still held.
            if (_initialized && !_wasHeld && !held && wallMs > _lastWallMs)
                NowMs += wallMs - _lastWallMs;
            _lastWallMs = wallMs;
            _wasHeld = held;
            _initialized = true;
            return NowMs;
        }

        public void Reset()
        {
            _initialized = false;
            _wasHeld = false;
            _lastWallMs = 0;
            NowMs = 0;
        }
    }
}
