using Game.Common;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    internal static class ModEntityEligibility
    {
        public static bool IsLive(EntityManager entities, Entity entity)
        {
            return entity != Entity.Null && entities.Exists(entity) &&
                   !entities.HasComponent<Temp>(entity) && !entities.HasComponent<Deleted>(entity);
        }
    }
}
