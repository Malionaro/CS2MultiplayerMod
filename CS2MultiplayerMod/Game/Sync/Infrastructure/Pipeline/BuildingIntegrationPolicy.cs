namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class BuildingIntegrationPolicy
    {
        // Consumption metadata alone is not an obligation: generators and resource-area
        // placeholders carry it without one or both consumer components. Only the consumer types
        // the prefab's durable archetype declares are expected back.
        public static bool HasExpectedConsumers(bool expectsElectricity, bool expectsWater,
            bool hasElectricity, bool hasWater)
        {
            return (!expectsElectricity || hasElectricity) && (!expectsWater || hasWater);
        }
    }
}
