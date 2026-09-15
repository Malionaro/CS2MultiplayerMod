using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Sync.ModSync
{
    /// <summary>
    /// The replicated types of a session, in the order the host published them.
    ///
    /// Types are named once and then referred to by index, because a name is the largest thing in
    /// a transaction that never changes. The order is the host's: a client binds its own types to
    /// the host's indices and answers for whatever it cannot bind, so a payload can never be read
    /// against a different type than it was written from.
    /// </summary>
    public sealed class ModTypeTable : IModTypeLookup
    {
        /// <summary>Types in one session. Far above anything measured; a table past this is refused.</summary>
        public const int MaxTypes = 512;

        private readonly List<ModTypeDescriptor> _byIndex = new List<ModTypeDescriptor>();
        private readonly Dictionary<string, int> _byKey =
            new Dictionary<string, int>(System.StringComparer.Ordinal);

        public int Count { get { return _byIndex.Count; } }

        public ModTypeDescriptor ByIndex(int index) { return _byIndex[index]; }

        public bool TryIndexOf(string key, out int index)
        {
            return _byKey.TryGetValue(key, out index);
        }

        /// <summary>Appends a type and returns its index, or the existing index if it is already in.</summary>
        public int Add(ModTypeDescriptor descriptor)
        {
            int existing;
            if (_byKey.TryGetValue(descriptor.Key, out existing)) return existing;
            int index = _byIndex.Count;
            _byIndex.Add(descriptor);
            _byKey.Add(descriptor.Key, index);
            return index;
        }

        public IEnumerable<ModTypeDescriptor> All { get { return _byIndex; } }

        public void Write(NetworkWriter writer)
        {
            writer.WriteShort((short)_byIndex.Count);
            for (int i = 0; i < _byIndex.Count; i++) _byIndex[i].Write(writer);
        }

        public static ModTypeTable Read(NetworkReader reader)
        {
            int count = reader.ReadShort();
            if (count < 0 || count > MaxTypes)
                throw new ProtocolException("Mod type table declares " + count + " types.");

            var table = new ModTypeTable();
            for (int i = 0; i < count; i++)
            {
                ModTypeDescriptor descriptor = ModTypeDescriptor.Read(reader);

                // Indices are positional, so a duplicate key would silently shift every type after
                // it by one on one side only.
                int existing;
                if (table._byKey.TryGetValue(descriptor.Key, out existing))
                    throw new ProtocolException("Mod type table repeats " + descriptor.DisplayName + ".");
                table.Add(descriptor);
            }
            return table;
        }
    }
}
