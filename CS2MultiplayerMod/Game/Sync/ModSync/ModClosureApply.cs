using System.Collections.Generic;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Game.Common;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>What became of an arriving closure.</summary>
    internal enum ModApplyOutcome
    {
        Applied,

        /// <summary>The carrier is not on this machine yet. Worth waiting for.</summary>
        CarrierMissing,

        /// <summary>Something the payload points at is not here yet. Also worth waiting for.</summary>
        ReferenceMissing,

        /// <summary>A type in the payload has nothing to bind to here. Waiting will not help.</summary>
        TypeMissing,
    }

    /// <summary>
    /// Writes an arriving closure into this machine's world.
    ///
    /// Resolution and writing are separate passes on purpose. A closure names several places in the
    /// city, and a road that is still a frame or two from being built makes one of them
    /// temporarily unfindable; discovering that halfway through would leave the junction holding
    /// half of somebody else's edit with no record of what the other half was. So everything is
    /// looked up first, and nothing is written until all of it is found.
    /// </summary>
    internal sealed class ModClosureApply
    {
        private readonly EntityManager _entities;
        private readonly ModCarrierIdentity _identity;
        private readonly ModTypeBinding _binding;
        private readonly ModComponentCatalog _catalog;

        private readonly Dictionary<string, Entity> _resolved =
            new Dictionary<string, Entity>(System.StringComparer.Ordinal);
        private readonly List<Entity> _newSatellites = new List<Entity>();

        /// <summary>What went wrong, in words fit for a log line.</summary>
        public string Detail { get; private set; }

        public ModClosureApply(EntityManager entities, ModCarrierIdentity identity,
            ModTypeBinding binding, ModComponentCatalog catalog)
        {
            _entities = entities;
            _identity = identity;
            _binding = binding;
            _catalog = catalog;
        }

        public ModApplyOutcome Apply(ModStateSnapshot snapshot)
        {
            Detail = null;
            _resolved.Clear();
            _newSatellites.Clear();

            Entity carrier;
            if (!_identity.TryResolve(snapshot.Carrier, out carrier))
            {
                Detail = snapshot.Carrier.Key();
                return ModApplyOutcome.CarrierMissing;
            }

            ModApplyOutcome check = Resolve(snapshot);
            if (check != ModApplyOutcome.Applied) return check;

            // Everything the payload names is present. From here the world is written to, and the
            // only remaining failure would be a bug rather than a race.
            List<Entity> stale = CollectExistingSatellites(carrier);

            for (int i = 0; i < snapshot.Satellites.Count; i++)
                _newSatellites.Add(_entities.CreateEntity());

            Write(carrier, snapshot.CarrierValues);
            for (int i = 0; i < snapshot.Satellites.Count; i++)
                Write(_newSatellites[i], snapshot.Satellites[i]);

            StripAbsentTypes(carrier, snapshot.CarrierValues);
            DestroySatellites(stale);

            // The same signal an ordinary edit leaves, so the mod's own systems and the game's
            // rendering both notice that this junction changed.
            if (!_entities.HasComponent<Updated>(carrier)) _entities.AddComponent<Updated>(carrier);
            if (!_entities.HasComponent<BatchesUpdated>(carrier))
                _entities.AddComponent<BatchesUpdated>(carrier);

            return ModApplyOutcome.Applied;
        }

        /// <summary>
        /// Looks up every place in the city the payload names, and checks that every type in it can
        /// be written here. Touches nothing.
        /// </summary>
        private ModApplyOutcome Resolve(ModStateSnapshot snapshot)
        {
            ModApplyOutcome outcome = ResolveEntity(snapshot.CarrierValues);
            if (outcome != ModApplyOutcome.Applied) return outcome;

            for (int i = 0; i < snapshot.Satellites.Count; i++)
            {
                outcome = ResolveEntity(snapshot.Satellites[i]);
                if (outcome != ModApplyOutcome.Applied) return outcome;
            }
            return ModApplyOutcome.Applied;
        }

        private ModApplyOutcome ResolveEntity(ModEntityValues values)
        {
            for (int i = 0; i < values.Components.Count; i++)
            {
                ModComponentValue component = values.Components[i];
                ModTypeAccessor accessor = _binding.AccessorFor(component.TypeIndex);
                if (accessor == null)
                {
                    Detail = _binding.ByIndex(component.TypeIndex).DisplayName;
                    return ModApplyOutcome.TypeMissing;
                }

                ModTypeDescriptor descriptor = _binding.ByIndex(component.TypeIndex);
                for (int leaf = 0; leaf < component.Leaves.Length; leaf++)
                {
                    if (descriptor.Leaves[leaf % descriptor.LeafCount] != ModValueKind.EntityRef)
                        continue;

                    ModEntityRef reference = component.Leaves[leaf].Reference;
                    if (reference.Kind == ModRefKind.Null || reference.Kind == ModRefKind.Satellite)
                        continue;

                    string key = reference.Key();
                    if (_resolved.ContainsKey(key)) continue;

                    Entity target;
                    if (!_identity.TryResolve(reference, out target))
                    {
                        Detail = key;
                        return ModApplyOutcome.ReferenceMissing;
                    }
                    _resolved.Add(key, target);
                }
            }
            return ModApplyOutcome.Applied;
        }

        private void Write(Entity entity, ModEntityValues values)
        {
            for (int i = 0; i < values.Components.Count; i++)
            {
                ModComponentValue component = values.Components[i];
                ModTypeAccessor accessor = _binding.AccessorFor(component.TypeIndex);
                if (accessor == null) continue;
                accessor.Apply(_entities, entity, component.Leaves, component.ElementCount, Translate);
            }
        }

        private Entity Translate(ModEntityRef reference)
        {
            switch (reference.Kind)
            {
                case ModRefKind.Null:
                    return Entity.Null;
                case ModRefKind.Satellite:
                    return reference.SatelliteIndex < _newSatellites.Count
                        ? _newSatellites[reference.SatelliteIndex]
                        : Entity.Null;
                default:
                    Entity found;
                    return _resolved.TryGetValue(reference.Key(), out found) ? found : Entity.Null;
            }
        }

        /// <summary>
        /// Takes off the carrier any replicated type the arriving closure does not mention. A query
        /// on a type cannot report that it was removed somewhere else, so a removal only travels as
        /// the absence of that type from a snapshot - and this is where that absence is acted on.
        /// </summary>
        private void StripAbsentTypes(Entity carrier, ModEntityValues values)
        {
            for (int i = 0; i < _catalog.Entries.Count; i++)
            {
                ModCatalogEntry entry = _catalog.Entries[i];
                if (!entry.Accessor.Has(_entities, carrier)) continue;

                int typeIndex;
                if (!_binding.TryIndexOf(entry.Descriptor.Key, out typeIndex)) continue;

                bool mentioned = false;
                for (int c = 0; c < values.Components.Count; c++)
                {
                    if (values.Components[c].TypeIndex != typeIndex) continue;
                    mentioned = true;
                    break;
                }
                if (!mentioned) entry.Accessor.Remove(_entities, carrier);
            }
        }

        /// <summary>
        /// The bookkeeping entities the carrier owned before this closure arrived. They are
        /// replaced wholesale rather than matched up, because the mods rebuild them on every edit
        /// anyway and there is nothing stable to match them on.
        /// </summary>
        private List<Entity> CollectExistingSatellites(Entity carrier)
        {
            var capture = new ModClosureCapture(_entities, _identity, _binding, _catalog);
            ModEntityRef carrierRef;
            if (!_identity.TryDescribe(carrier, out carrierRef)) return new List<Entity>();

            capture.Capture(carrier, carrierRef);

            // A rejected capture still leaves behind whatever it reached before it stopped, and
            // those are still this carrier's satellites.
            return new List<Entity>(capture.SatelliteEntities);
        }

        private void DestroySatellites(List<Entity> satellites)
        {
            for (int i = 0; i < satellites.Count; i++)
            {
                Entity satellite = satellites[i];
                if (satellite == Entity.Null || !_entities.Exists(satellite)) continue;

                // Never a thing in the world, even if the walk that found it went wrong: only an
                // entity with no place of its own can have been one of these.
                ModEntityRef described;
                if (_identity.TryDescribe(satellite, out described)) continue;

                _entities.DestroyEntity(satellite);
            }
        }
    }
}
