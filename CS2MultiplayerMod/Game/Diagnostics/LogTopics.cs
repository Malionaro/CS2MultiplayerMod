using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// The per-topic detail switches, kept here in code rather than on the options screen.
    ///
    /// A player has exactly one logging choice (Verbose Logging, on the General tab): everything
    /// they could be asked for is either always written or covered by that one switch. Narrowing a
    /// log down to a single subsystem is a developer's job, and it is done here - eighteen
    /// checkboxes only ever asked the player to guess which part of the mod broke.
    ///
    /// Flip a topic on by uncommenting it in <see cref="Enabled"/> and rebuilding, or set the
    /// fields from a debugger mid-run; they are deliberately not <c>const</c> so both work.
    /// Everything here must ship false - these are chatty per-action lines.
    /// </summary>
    internal static class LogTopics
    {
        /// <summary>
        /// Topics whose <see cref="SyncLog.Detail"/> lines are written in this build, exactly as
        /// if the player had turned verbose logging on for them alone.
        /// </summary>
        private static readonly LogTopic[] Enabled =
        {
            // LogTopic.Nets,
            // LogTopic.Buildings,
            // LogTopic.Residential,
        };

        /// <summary>Every topic at once - the developer-side equivalent of the player's switch.</summary>
        private static readonly bool AllTopics = false;

        /// <summary>
        /// Whether <see cref="SyncLog.Trace"/> breadcrumbs also reach the readable game log.
        ///
        /// Off even under verbose logging, and that is the point: traces are per-command and
        /// per-entity, and in a field log they outnumbered everything else six to one, which is
        /// how a full log stops being readable. They are always in the flight log, which is the
        /// file a report is read from.
        /// </summary>
        private static readonly bool TracesInGameLog = false;

        /// <summary>Whether this build asks for <see cref="SyncLog.Detail"/> on this topic.</summary>
        public static bool DetailEnabled(LogTopic topic)
        {
            if (AllTopics) return true;
            for (int i = 0; i < Enabled.Length; i++)
                if (Enabled[i] == topic) return true;
            return false;
        }

        /// <summary>Whether a <see cref="SyncLog.Trace"/> on this topic is mirrored to the game log.</summary>
        public static bool TraceInGameLog(LogTopic topic)
        {
            return TracesInGameLog || DetailEnabled(topic);
        }
    }
}
