using System;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Owns temporary candidates for a read-only realization pump.</summary>
    internal sealed class PropertySearchScope : IDisposable
    {
        public ObjectSearch.Batch Batch { get; }
        public NativeList<Entity> Candidates { get; }
        public PropertySearchScope(ObjectSearch search)
        {
            Batch = PropertySpatialPass.Current != null
                ? PropertySpatialPass.Current.Acquire(search) : search.BeginBatch();
            Candidates = new NativeList<Entity>(16, Allocator.Temp);
        }
        public void Dispose() => Candidates.Dispose();
    }
}
