using System.Collections.Generic;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Reuses membership sets during one read-only pass. Reset before the next pass so moves,
    /// removals and recycled entity handles can never reuse an old relationship.
    /// </summary>
    internal sealed class PassMembershipIndex<TKey, TMember>
    {
        private readonly Dictionary<TKey, HashSet<TMember>> _members =
            new Dictionary<TKey, HashSet<TMember>>();
        private readonly List<HashSet<TMember>> _pool = new List<HashSet<TMember>>();
        private int _used;

        public HashSet<TMember> GetMembers(TKey key, out bool firstVisit)
        {
            HashSet<TMember> members;
            firstVisit = !_members.TryGetValue(key, out members);
            if (!firstVisit) return members;
            if (_used == _pool.Count) _pool.Add(new HashSet<TMember>());
            members = _pool[_used++];
            _members.Add(key, members);
            return members;
        }

        public void Reset()
        {
            _members.Clear();
            for (int i = 0; i < _used; i++) _pool[i].Clear();
            _used = 0;
        }
    }
}
