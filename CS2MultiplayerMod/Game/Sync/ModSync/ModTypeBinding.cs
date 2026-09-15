using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Sync.ModSync;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// The session's type table joined to this machine's own types.
    ///
    /// The host publishes the order; every machine binds its local types to it. A type the host
    /// named and this machine cannot produce is recorded rather than skipped quietly: same mod,
    /// different layout is the case a matching version number does not catch, and the player would
    /// otherwise see one feature of one mod silently not travelling.
    /// </summary>
    internal sealed class ModTypeBinding : IModTypeLookup
    {
        private readonly ModTypeTable _table;
        private readonly ModTypeAccessor[] _accessors;
        private readonly Dictionary<string, int> _indexByKey =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Types the host named that this machine has nothing to bind to.</summary>
        public readonly List<string> Missing = new List<string>();

        private ModTypeBinding(ModTypeTable table)
        {
            _table = table;
            _accessors = new ModTypeAccessor[table.Count];
            for (int i = 0; i < table.Count; i++) _indexByKey[table.ByIndex(i).Key] = i;
        }

        public int Count { get { return _table.Count; } }

        public ModTypeTable Table { get { return _table; } }

        public ModTypeDescriptor ByIndex(int index) { return _table.ByIndex(index); }

        /// <summary>The way to read this type here, or null when this machine could not bind it.</summary>
        public ModTypeAccessor AccessorFor(int index)
        {
            return index >= 0 && index < _accessors.Length ? _accessors[index] : null;
        }

        public bool TryIndexOf(string key, out int index)
        {
            return _indexByKey.TryGetValue(key, out index);
        }

        /// <summary>The host's own binding: the table is this machine's catalogue, in its order.</summary>
        public static ModTypeBinding ForHost(ModComponentCatalog catalog)
        {
            var table = new ModTypeTable();
            for (int i = 0; i < catalog.Entries.Count; i++) table.Add(catalog.Entries[i].Descriptor);

            var binding = new ModTypeBinding(table);
            for (int i = 0; i < catalog.Entries.Count; i++)
                binding._accessors[i] = catalog.Entries[i].Accessor;
            return binding;
        }

        /// <summary>
        /// A receiving machine's binding: the host's order, filled in with local types wherever the
        /// key and the layout both agree.
        /// </summary>
        public static ModTypeBinding ForClient(ModComponentCatalog catalog, ModTypeTable table)
        {
            var binding = new ModTypeBinding(table);
            for (int i = 0; i < table.Count; i++)
            {
                ModTypeDescriptor wanted = table.ByIndex(i);
                ModCatalogEntry local;
                if (!catalog.TryGet(wanted.Key, out local))
                {
                    binding.Missing.Add(wanted.DisplayName + " - not present here");
                    continue;
                }

                if (local.Descriptor.Fingerprint != wanted.Fingerprint)
                {
                    binding.Missing.Add(wanted.DisplayName + " - different layout here (" +
                        local.Descriptor.LeafCount + " field(s) against " + wanted.LeafCount + ")");
                    continue;
                }

                binding._accessors[i] = local.Accessor;
            }
            return binding;
        }
    }
}
