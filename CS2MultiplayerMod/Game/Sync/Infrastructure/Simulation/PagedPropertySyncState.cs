using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class PropertySyncLimits
    {
        public const int UpdatePartitions = 16;
        public const int MaxIncomingPages = 8;
        public const int MaxPumpPages = 2;
        public const int MaxCachedProperties = 131072;
        public const int MaxPendingIdentities = 4096;
        public const int MaxPriorityEntries = 2048;
        public const int MaxPropertiesObservedPerUpdate = 256;
        public const long ResolveRetryMs = 5000;
        public const long StatsIntervalMs = 30000;
    }

    internal class PendingPropertyState<T>
    {
        public T Entry;
        public uint SweepId;
        public long ExpiresMs;
        public long NextAttemptMs;
    }

    /// <summary>Owns the common bounded property page, cache, retry and observation state.
    /// Domain payloads and structural realization remain with their single writer.</summary>
    internal sealed class PagedPropertySyncState<TPage, TCache, TPending, THost, TPriority>
    {
        public readonly ConcurrentQueue<TPage> Incoming = new ConcurrentQueue<TPage>();
        public readonly Dictionary<Entity, TCache> Cache = new Dictionary<Entity, TCache>();
        public readonly Dictionary<PropertyRentIdentity, TPending> Pending =
            new Dictionary<PropertyRentIdentity, TPending>();
        public readonly ConcurrentQueue<PropertyRentIdentity> PendingOrder =
            new ConcurrentQueue<PropertyRentIdentity>();
        public readonly Dictionary<Entity, THost> HostObserved = new Dictionary<Entity, THost>();
        public readonly Dictionary<PropertyRentIdentity, TPriority> Priority =
            new Dictionary<PropertyRentIdentity, TPriority>();
        public readonly ConcurrentQueue<PropertyRentIdentity> PriorityOrder =
            new ConcurrentQueue<PropertyRentIdentity>();
        public readonly PropertyPartitions CachedPartitions = new PropertyPartitions();
        public readonly PropertyPartitions HostPartitions = new PropertyPartitions();
        public long NextPendingPumpMs;

        public bool Prioritize(PropertyRentIdentity identity, TPriority value, int capacity,
            out int dropped)
        {
            dropped = 0;
            if (Priority.ContainsKey(identity))
            {
                Priority[identity] = value;
                return false;
            }
            while (Priority.Count >= capacity &&
                   PriorityOrder.TryDequeue(out PropertyRentIdentity oldest))
                if (Priority.Remove(oldest)) dropped++;
            if (Priority.Count >= capacity) { dropped++; return false; }
            Priority[identity] = value;
            PriorityOrder.Enqueue(identity);
            return true;
        }

        public int Enqueue(TPage page)
        {
            int dropped = 0;
            lock (Incoming)
            {
                Incoming.Enqueue(page);
                while (Incoming.Count > PropertySyncLimits.MaxIncomingPages &&
                       Incoming.TryDequeue(out _)) dropped++;
            }
            return dropped;
        }

        public int PumpPages(int budget, Action<TPage> apply)
        {
            int pages = 0;
            while (pages < budget && Incoming.TryDequeue(out TPage page))
            {
                pages++;
                apply(page);
            }
            return pages;
        }
    }

    internal sealed class PropertyPartitions
    {
        public readonly List<Entity>[] Buckets =
            new List<Entity>[PropertySyncLimits.UpdatePartitions];
        public readonly HashSet<Entity>[] Members =
            new HashSet<Entity>[PropertySyncLimits.UpdatePartitions];
        public readonly int[] Cursor = new int[PropertySyncLimits.UpdatePartitions];
        public readonly bool[] Initialized = new bool[PropertySyncLimits.UpdatePartitions];

        public PropertyPartitions()
        {
            for (int i = 0; i < Buckets.Length; i++)
            {
                Buckets[i] = new List<Entity>();
                Members[i] = new HashSet<Entity>();
            }
        }

        public void Add(int bucket, Entity entity)
        {
            if (Members[bucket].Add(entity)) Buckets[bucket].Add(entity);
        }
    }

    internal static class PropertyRetryPump
    {
        public static void Pump<T>(Dictionary<PropertyRentIdentity, T> pending,
            ConcurrentQueue<PropertyRentIdentity> order, long now, int budget,
            Func<T, long> expiry, Func<T, long> retryAt, Action<T, long> setRetry,
            Func<T, bool> apply, Action expired)
        {
            // Inspect each identity at most once, including small not-yet-due queues.
            int count = Math.Min(budget, order.Count);
            while (count-- > 0 && order.TryDequeue(out PropertyRentIdentity identity))
            {
                if (!pending.TryGetValue(identity, out T value)) continue;
                if (expiry(value) <= now)
                {
                    pending.Remove(identity);
                    expired();
                }
                else if (retryAt(value) > now) order.Enqueue(identity);
                else if (apply(value)) pending.Remove(identity);
                else
                {
                    setRetry(value, now + PropertySyncLimits.ResolveRetryMs);
                    order.Enqueue(identity);
                }
            }
        }
    }
}
