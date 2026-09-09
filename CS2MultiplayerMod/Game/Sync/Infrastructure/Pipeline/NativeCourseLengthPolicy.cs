using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class NativeCourseLengthPolicy
    {
        // Generator state need not equal final arc length. This deliberately broad bound
        // rejects unusable/module-exploding inputs without rewriting valid native lengths.
        public static bool IsPlausible(float length, float measuredLength, bool point)
        {
            if (float.IsNaN(length) || float.IsInfinity(length) ||
                float.IsNaN(measuredLength) || float.IsInfinity(measuredLength) ||
                measuredLength < 0f || length < 0f) return false;
            if (!point && (length < NetPlacementCommand.MinCourseLength ||
                           measuredLength < NetPlacementCommand.MinCourseLength)) return false;
            return length <= (double)measuredLength * 4d + 100d;
        }
    }
}
