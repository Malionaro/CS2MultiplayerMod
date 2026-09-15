using System;
using System.IO;
using System.Threading;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// One seekable snapshot shared by sequential recipients, each holding a lease. The producer
    /// releases its own lease once every recipient is queued, so the backing stream closes as soon
    /// as the last transfer finishes or is dropped.
    ///
    /// Reference counting is atomic: a cancelled save can be released from the task continuation
    /// while the game thread is releasing the same snapshot.
    /// </summary>
    public sealed class BlobSource : IDisposable
    {
        public const long MaxBytes = 256L * 1024 * 1024;

        private readonly Stream _stream;
        private int _references = 1;

        public int Length { get; }

        public BlobSource(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead || !stream.CanSeek || stream.Length <= 0 ||
                stream.Length > MaxBytes) throw new ArgumentException("Invalid blob source.");
            _stream = stream;
            Length = (int)stream.Length;
        }

        internal void Retain()
        {
            while (true)
            {
                int current = Volatile.Read(ref _references);
                if (current <= 0) throw new ObjectDisposedException(nameof(BlobSource));
                if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                    return;
            }
        }

        internal void Read(int offset, byte[] destination, int count)
        {
            if (Volatile.Read(ref _references) <= 0)
                throw new ObjectDisposedException(nameof(BlobSource));
            if (offset < 0 || count < 0 || offset > Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));

            _stream.Position = offset;
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(destination, read, count - read);
                if (n == 0) throw new EndOfStreamException("Snapshot source ended unexpectedly.");
                read += n;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            _stream.Dispose();
        }
    }
}
