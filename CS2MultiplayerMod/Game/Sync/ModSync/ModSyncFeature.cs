namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// The switches mod-state replication is built against. Developer-side, like
    /// <see cref="Diagnostics.LogTopics"/>: a player has no useful answer to any of them, and every
    /// one of them is a statement about how the mechanism should behave rather than a preference.
    /// </summary>
    internal static class ModSyncFeature
    {
        /// <summary>
        /// Replicate a third-party type only if the runtime would write it to a savegame - that is,
        /// if it carries one of the engine's serialization markers.
        ///
        /// Filtering by declaring assembly alone is far too coarse. Measured on a session with four
        /// large mods: by assembly, 56 types and 1.67 MB in two minutes, around 95% of it tool
        /// previews and map overlays that kept churning while the player did nothing at all. With
        /// this filter: 8 types, 12 KB, and no traffic whatsoever while idle. State the game does
        /// not save cannot be state that survives a join, so the mod author has already answered
        /// the question and this reads their answer.
        ///
        /// Turn it off only to measure what local working state costs; never to ship.
        /// </summary>
        public static bool RequireDurableTypes = true;

        /// <summary>
        /// Whether captured changes are actually sent. Capture and logging are safe on their own,
        /// which makes observing a session a cheap first step when a new mod is being looked at.
        /// </summary>
        public static bool SendCaptured = true;

        /// <summary>Whether arriving changes are written into the world.</summary>
        public static bool ApplyReceived = true;

        /// <summary>
        /// Carriers examined in one capture pass. The prefilter already narrows this to chunks that
        /// were written to, so the ceiling only bounds the worst frame - a mod rewriting half the
        /// city at once - rather than the normal one.
        /// </summary>
        public static int MaxCarriersPerPass = 64;

        /// <summary>
        /// How far a closure walk follows entity references away from its carrier. A mod's
        /// bookkeeping entity may point at another; nothing measured goes beyond two.
        /// </summary>
        public static int MaxClosureDepth = 3;

        /// <summary>
        /// How long an arriving change waits for a carrier this machine cannot find yet, before it
        /// is given up on with a log line. A road being built arrives as its own command and may
        /// realize a frame or two later; the wait covers that and nothing longer.
        /// </summary>
        public static long UnresolvedHoldMs = 5000;
    }
}
