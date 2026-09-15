using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Serialization.Entities;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>One discovered type: what it is called, how to read it, and how it travels.</summary>
    internal sealed class ModCatalogEntry
    {
        public Type Type;
        public ModTypeDescriptor Descriptor;
        public ModTypeAccessor Accessor;
    }

    /// <summary>
    /// Every component and buffer type in the world that belongs to another mod and holds state
    /// worth replicating - found by asking the engine what types exist, never by naming a mod.
    ///
    /// Two filters do the work. The first is ownership: a type declared by the game, the engine or
    /// this mod is not another mod's state. The second is durability, and it is the one that
    /// matters: a type is replicated only if the runtime would write it to a savegame. Mods create
    /// a great deal of per-frame working state - tool previews, map overlays, hover highlights -
    /// that looks exactly like real state to a component scan and is pure noise on a wire.
    /// Whether the author marked it for serialization is their own answer to the question of
    /// whether it is worth keeping, and it is a far better answer than any heuristic here.
    /// </summary>
    internal sealed class ModComponentCatalog
    {
        private readonly Dictionary<string, ModCatalogEntry> _byKey =
            new Dictionary<string, ModCatalogEntry>(StringComparer.Ordinal);

        /// <summary>Entries in a stable order, so two machines list the same types the same way.</summary>
        public readonly List<ModCatalogEntry> Entries = new List<ModCatalogEntry>();

        /// <summary>Types that belong to another mod but are not replicated, each with its reason.</summary>
        public readonly List<string> Exclusions = new List<string>();

        /// <summary>How many third-party types were seen in total, replicated or not.</summary>
        public int ThirdPartyTypeCount;

        /// <summary>
        /// How many types the engine had registered when this catalogue was taken.
        ///
        /// A mod can be switched on while the game is running, and its components only enter the
        /// engine's type registry when its own systems first touch them. A catalogue taken before
        /// that saw a world without the mod in it, and nothing about it would ever say so - it
        /// would simply replicate nothing, for the rest of the session. Comparing this against the
        /// current count is how that is noticed.
        /// </summary>
        public int TypeCountAtBuild;

        /// <summary>
        /// Whether this catalogue replicates exactly what <paramref name="other"/> did.
        ///
        /// The engine's type count moves whenever anything at all registers a type for the first
        /// time, which is a far broader event than "a mod appeared". Rebuilding on the count is
        /// right; throwing away the session's type table and every recorded hash is only right when
        /// what travels has actually changed.
        /// </summary>
        public bool ReplicatesSameAs(ModComponentCatalog other)
        {
            if (other == null || other.Entries.Count != Entries.Count) return false;
            for (int i = 0; i < Entries.Count; i++)
            {
                ModTypeDescriptor mine = Entries[i].Descriptor;
                ModTypeDescriptor theirs = other.Entries[i].Descriptor;
                if (mine.Key != theirs.Key || mine.Fingerprint != theirs.Fingerprint) return false;
            }
            return true;
        }

        public bool TryGet(string key, out ModCatalogEntry entry)
        {
            return _byKey.TryGetValue(key, out entry);
        }

        public static ModComponentCatalog Build()
        {
            var catalog = new ModComponentCatalog();
            Assembly self = typeof(ModComponentCatalog).Assembly;

            catalog.TypeCountAtBuild = TypeManager.GetTypeCount();

            // AllTypes reads the registry in place; GetAllTypes would copy every entry in the game
            // into a new array just to walk it once.
            foreach (TypeManager.TypeInfo info in TypeManager.AllTypes)
            {
                Type type = SafeType(info);
                if (type == null) continue;
                if (type.Assembly == self || !IsThirdParty(type.Assembly)) continue;

                catalog.ThirdPartyTypeCount++;

                string key = ModTypeDescriptor.MakeKey(type.Assembly.GetName().Name, type.FullName);
                string reason;
                ModCatalogEntry entry = Accept(type, info, key, out reason);
                if (entry == null)
                {
                    catalog.Exclusions.Add(Short(type) + " - " + reason);
                    continue;
                }

                if (catalog._byKey.ContainsKey(key)) continue;
                catalog._byKey.Add(key, entry);
                catalog.Entries.Add(entry);
            }


            catalog.Entries.Sort(CompareByKey);
            return catalog;
        }

        private static int CompareByKey(ModCatalogEntry left, ModCatalogEntry right)
        {
            return string.CompareOrdinal(left.Descriptor.Key, right.Descriptor.Key);
        }

        private static ModCatalogEntry Accept(Type type, TypeManager.TypeInfo info, string key,
            out string reason)
        {
            reason = null;

            if (info.Category != TypeManager.TypeCategory.ComponentData &&
                info.Category != TypeManager.TypeCategory.BufferData)
            {
                reason = "is a " + info.Category;
                return null;
            }

            if (info.TypeIndex.IsManagedType)
            {
                reason = "is a managed component";
                return null;
            }

            // A reference to a blob, an asset or a Unity object is a pointer into this process's
            // own loaded content. It has no meaning on another machine, and nothing here could
            // translate one, so the type is left alone rather than half-copied.
            if (info.HasBlobAssetRefs) { reason = "holds blob references"; return null; }
            if (info.HasWeakAssetRefs) { reason = "holds asset references"; return null; }
            if (info.HasUnityObjectRefs) { reason = "holds engine object references"; return null; }

            if (ModSyncFeature.RequireDurableTypes && !IsDurable(type))
            {
                reason = "is not written to savegames";
                return null;
            }

            bool isBuffer = info.Category == TypeManager.TypeCategory.BufferData;
            ModFieldPlan plan;
            if (!ModFieldPlan.TryBuild(type, out plan, out reason)) return null;

            ModTypeKind kind = isBuffer
                ? ModTypeKind.Buffer
                : (plan.Count == 0 ? ModTypeKind.Tag : ModTypeKind.Component);

            if (kind == ModTypeKind.Buffer && plan.Count == 0)
            {
                reason = "is a buffer with no fields";
                return null;
            }

            var descriptor = new ModTypeDescriptor(key, kind, plan.Kinds);

            ModTypeAccessor accessor;
            try
            {
                accessor = ModTypeAccessor.Create(type, kind, plan, descriptor);
            }
            catch (Exception ex)
            {
                // A generic constraint this assembly cannot satisfy for that type. Naming it is
                // worth more than the type would have been.
                reason = "cannot be accessed (" + ex.GetType().Name + ")";
                return null;
            }

            return new ModCatalogEntry { Type = type, Descriptor = descriptor, Accessor = accessor };
        }

        /// <summary>
        /// Whether the runtime would write this type into a savegame - the discriminator between a
        /// mod's state and a mod's scratch space.
        /// </summary>
        private static bool IsDurable(Type type)
        {
            return typeof(ISerializable).IsAssignableFrom(type) ||
                   typeof(IEmptySerializable).IsAssignableFrom(type) ||
                   typeof(IDefaultSerializable).IsAssignableFrom(type) ||
                   typeof(IStrideSerializable).IsAssignableFrom(type);
        }

        private static Type SafeType(TypeManager.TypeInfo info)
        {
            // The type table holds entries whose managed type is not resolvable; asking is cheaper
            // than any guard that tries to predict which.
            try { return info.Type; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Whether an assembly belongs to somebody other than the game, the engine and the runtime.
        /// Prefix matching on the assembly name, because that is what a mod cannot accidentally
        /// collide with and what does not need a list of every game assembly to be kept current.
        /// </summary>
        private static bool IsThirdParty(Assembly assembly)
        {
            string name;
            try { name = assembly.GetName().Name; }
            catch (Exception) { return false; }
            if (string.IsNullOrEmpty(name)) return false;

            if (name == "Game" || name.StartsWith("Game.", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Colossal", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Unity", StringComparison.Ordinal)) return false;
            if (name.StartsWith("System", StringComparison.Ordinal)) return false;
            if (name == "mscorlib" || name == "netstandard") return false;
            return true;
        }

        private static string Short(Type type)
        {
            return type.FullName;
        }
    }
}
