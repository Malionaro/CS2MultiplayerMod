using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Finding the properties whose roster changed. A rotating cursor walks one partition per
    // update so the cost does not grow with the city, and native renter events prioritise a
    // property the moment it changes rather than waiting for the sweep to come round.
    public partial class ResidentialOccupancySyncSystem
    {
        private PageAddResult TryAddPageEntry(ResidentialOccupancySnapshot snapshot,
            PageBudget budget, OccupancyProperty property)
        {
            if (budget.Identities.Contains(property.Identity)) return PageAddResult.Duplicate;
            if (!ResidentialOccupancySnapshot.IsValidProperty(property))
                return PageAddResult.Invalid;

            int households = property.Households.Length;
            int citizens = 0;
            int pets = 0;
            int vehicles = 0;
            int bytes = 38;
            _pageEntryNames.Clear();
            _pageEntryNames.Add(property.PrefabName);
            for (int h = 0; h < property.Households.Length; h++)
            {
                OccupancyHousehold household = property.Households[h];
                if (budget.HouseholdIds.Contains(household.HouseholdId))
                    return PageAddResult.Invalid;
                citizens += household.Citizens.Length;
                pets += household.Pets.Length;
                vehicles += household.OwnedVehicles.Length;
                bytes += 63 + household.NameIndices.Length * 4 +
                         (household.Pets.Length + household.OwnedVehicles.Length) * 2;
                _pageEntryNames.Add(household.PrefabName);
                for (int c = 0; c < household.Citizens.Length; c++)
                {
                    OccupancyCitizen citizen = household.Citizens[c];
                    if (budget.CitizenIds.Contains(citizen.CitizenId))
                        return PageAddResult.Invalid;
                    bytes += 25 + citizen.NameIndices.Length * 4;
                    _pageEntryNames.Add(citizen.PrefabName);
                }
                for (int p = 0; p < household.Pets.Length; p++)
                    _pageEntryNames.Add(household.Pets[p]);
                for (int v = 0; v < household.OwnedVehicles.Length; v++)
                    _pageEntryNames.Add(household.OwnedVehicles[v]);
            }

            if (budget.Households + households > ResidentialOccupancySnapshot.MaxHouseholdsPerPage ||
                budget.Citizens + citizens > ResidentialOccupancySnapshot.MaxCitizensPerPage ||
                budget.Pets + pets > ResidentialOccupancySnapshot.MaxPetsPerPage ||
                budget.Vehicles + vehicles > ResidentialOccupancySnapshot.MaxVehiclesPerPage)
                return PageAddResult.Full;

            int newNameCount = 0;
            foreach (string name in _pageEntryNames)
            {
                if (budget.Names.Contains(name)) continue;
                newNameCount++;
                bytes += 4 + Encoding.UTF8.GetByteCount(name);
            }
            if (budget.Names.Count + newNameCount > ResidentialOccupancySnapshot.MaxNames ||
                budget.Bytes + bytes > ResidentialOccupancySnapshot.MaxEncodedBytes)
                return PageAddResult.Full;

            budget.Identities.Add(property.Identity);
            budget.Households += households;
            budget.Citizens += citizens;
            budget.Pets += pets;
            budget.Vehicles += vehicles;
            budget.Bytes += bytes;
            foreach (string name in _pageEntryNames) budget.Names.Add(name);
            for (int h = 0; h < property.Households.Length; h++)
            {
                OccupancyHousehold household = property.Households[h];
                budget.HouseholdIds.Add(household.HouseholdId);
                for (int c = 0; c < household.Citizens.Length; c++)
                    budget.CitizenIds.Add(household.Citizens[c].CitizenId);
            }
            snapshot.Properties.Add(property);
            return PageAddResult.Added;
        }

        /// <summary>
        /// The change detector that gets a newly occupied building onto the wire in seconds rather
        /// than waiting for the rolling baseline sweep to come round to it. It walks at most
        /// <see cref="MaxPropertiesObservedPerUpdate"/> properties of one residential partition per
        /// update and resumes where it stopped, so its cost does not grow with the city. A
        /// partition counts as initialized, and its stale observations are pruned, only once the
        /// cursor has been all the way round it.
        /// </summary>
        private void ScanHostChanges(int bucket)
        {
            _properties.SetSharedComponentFilter(new UpdateFrame((uint)bucket));
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            bool wrapped = false;
            try
            {
                properties = _properties.ToEntityArray(Allocator.Temp);
                bool initialized = _hostBucketInitialized[bucket];
                int cursor = _hostBucketCursor[bucket];
                if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                int examine = properties.Length < MaxPropertiesObservedPerUpdate
                    ? properties.Length : MaxPropertiesObservedPerUpdate;
                for (int i = 0; i < examine; i++)
                {
                    if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                    Entity property = properties[cursor++];
                    _observedProperties++;

                    // The allocation-free probe decides this; almost every property it looks at is
                    // unchanged and stops here. See CaptureProbe.cs for why the full capture is
                    // the wrong instrument for the question.
                    int hash;
                    if (!TryHashProperty(property, out hash)) continue;
                    HostObserved observed;
                    bool known = _hostObserved.TryGetValue(property, out observed);
                    if (known && !observed.Stale && observed.Hash == hash)
                    {
                        _probeSkipped++;
                        continue;
                    }

                    // Queue an identity, not a throwaway serialized roster. The page builder
                    // captures current state, validates it and registers departure tracking before
                    // emission. Previously we allocated/captured here and repeated it at send time.
                    // Identities never sent to a peer do not need speculative tombstone tracking.
                    PropertyRentIdentity identity;
                    if (!TryGetHostPropertyIdentity(property, out identity)) continue;
                    if (!known)
                    {
                        _hostObserved[property] = new HostObserved { Hash = hash, Bucket = bucket };
                        _hostObservedBuckets[bucket].Add(property);
                        if (initialized) Prioritize(property, identity);
                    }
                    else if (observed.Stale)
                    {
                        // A renter event already queued this property; re-baseline only.
                        observed.Stale = false;
                        observed.Hash = hash;
                    }
                    else
                    {
                        observed.Hash = hash;
                        if (initialized) Prioritize(property, identity);
                    }
                }
                if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                _hostBucketCursor[bucket] = cursor;
                if (wrapped) _hostBucketInitialized[bucket] = true;
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
                _properties.ResetFilter();
            }
            if (wrapped) PruneHostObservedBucket(bucket);
        }

        /// <summary>
        /// Fast path for native renter transactions. This is called every simulation frame after
        /// PropertyProcessingSystem, while its RentersUpdated event still names the exact property
        /// whose absolute roster changed.
        /// </summary>
        internal void CaptureRenterChanges()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                _renterUpdates.IsEmptyIgnoreFilter) return;

            NativeArray<RentersUpdated> updates = default(NativeArray<RentersUpdated>);
            try
            {
                updates = _renterUpdates.ToComponentDataArray<RentersUpdated>(Allocator.Temp);
                for (int i = 0; i < updates.Length; i++)
                {
                    Entity property = updates[i].m_Property;
                    if (service.Session.Role == Core.Session.SessionRole.Client)
                    {
                        // PropertyProcessing and removal systems are intentionally still live on
                        // the client. If either changes a renter link, immediately reassert the
                        // last absolute host roster instead of waiting up to a full rolling sweep.
                        if (!IsLiveProperty(property) || !_cache.ContainsKey(property)) continue;
                        MarkDirty(property);
                        _clientRenterRepairSignals++;
                        continue;
                    }

                    // Only the portable identity is needed to queue the signal. Serializing the
                    // whole roster here was work thrown away: the page builder recaptures a
                    // priority property at send time anyway, and this runs every simulation frame
                    // over however many renter transactions the city just completed.
                    PropertyRentIdentity identity;
                    if (!TryGetHostPropertyIdentity(property, out identity)) continue;

                    // The event is authoritative proof of a completed renter mutation, so it is
                    // always queued. The observer's stored hash now describes a roster that no
                    // longer exists; mark it rather than recomputing it, so the next rolling pass
                    // re-baselines without queueing the same property a second time.
                    HostObserved observed;
                    if (_hostObserved.TryGetValue(property, out observed)) observed.Stale = true;
                    Prioritize(property, identity);
                }
            }
            finally
            {
                if (updates.IsCreated) updates.Dispose();
            }
        }

        /// <summary>
        /// The property's portable identity without serializing anything inside it. Prefab names
        /// come from the cached catalogue, so this is a handful of component reads.
        /// </summary>
        private bool TryGetHostPropertyIdentity(Entity property, out PropertyRentIdentity identity)
        {
            identity = default(PropertyRentIdentity);
            if (!IsLiveProperty(property)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            string prefabName = _prefabIndex.NameOf(prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;
            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(property);
            identity = new PropertyRentIdentity(prefabName, transform.m_Position.x,
                transform.m_Position.y, transform.m_Position.z);
            return true;
        }

        private void Prioritize(Entity entity, PropertyRentIdentity identity)
        {
            int dropped;
            if (_propertyState.Prioritize(identity, entity, MaxPriorityProperties, out dropped)) _priorityChanges++;
            _priorityDrops += dropped;
        }

        private void PruneHostObservedBucket(int bucket)
        {
            List<Entity> entities = _hostObservedBuckets[bucket];
            int write = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                HostObserved observed;
                if (!_hostObserved.TryGetValue(entity, out observed)) continue;
                if (!IsLiveProperty(entity) || observed.Bucket != bucket ||
                    EntityManager.GetSharedComponent<UpdateFrame>(entity).m_Index != (uint)bucket)
                {
                    _hostObserved.Remove(entity);
                    continue;
                }
                entities[write++] = entity;
            }
            if (write < entities.Count) entities.RemoveRange(write, entities.Count - write);
        }
    }
}
