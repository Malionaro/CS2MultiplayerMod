namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class NativeReplacementPolicy
    {
        // Delete wins over replacement, and lane cancellation wins over both. Only the
        // branch that actually replaces an edge/lane dereferences a mandatory original.
        public static bool RequiresOriginal(bool edge, bool lane, bool delete, bool cancel,
            bool replace, bool combine)
        {
            if (delete || (lane && cancel)) return false;
            return (edge && (replace || combine)) || (lane && replace);
        }
    }
}
