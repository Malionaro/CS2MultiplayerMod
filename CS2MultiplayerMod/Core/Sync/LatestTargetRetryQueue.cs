using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>Bounded latest-per-target retries, with deadlines measured only while progress is allowed.</summary>
    public sealed class LatestTargetRetryQueue<TKey, TValue>
    {
        private sealed class Entry
        {
            public TKey Key;
            public TValue Value;
            public long Deadline;
        }

        private readonly int _capacity;
        private readonly long _timeoutMs;
        private readonly ActiveRetryClock _clock = new ActiveRetryClock();
        private readonly LinkedList<Entry> _order = new LinkedList<Entry>();
        private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries =
            new Dictionary<TKey, LinkedListNode<Entry>>();

        public LatestTargetRetryQueue(int capacity, long timeoutMs)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            _capacity = capacity;
            _timeoutMs = timeoutMs;
        }

        private bool _held;

        public int Count => _entries.Count;
        public void Observe(long nowMs, bool held)
        {
            _held = held;
            _clock.Observe(nowMs, held);
        }

        /// <summary>False means the oldest different target was evicted; the new value is retained.</summary>
        public bool SetLatest(TKey key, TValue value)
        {
            LinkedListNode<Entry> node;
            bool noLoss = true;
            if (!_entries.TryGetValue(key, out node))
            {
                if (_entries.Count == _capacity)
                {
                    Remove(_order.First.Value.Key);
                    noLoss = false;
                }
                node = _order.AddLast(new Entry { Key = key });
                _entries.Add(key, node);
            }
            node.Value.Value = value;
            node.Value.Deadline = _clock.NowMs + _timeoutMs;
            return noLoss;
        }

        public void Pump(Func<TValue, bool> tryApply, Action<TValue> onExpired)
        {
            for (var node = _order.First; node != null;)
            {
                var next = node.Next;
                Entry entry = node.Value;
                if (tryApply(entry.Value)) Remove(entry.Key);
                else if (!_held && _clock.NowMs >= entry.Deadline)
                {
                    Remove(entry.Key);
                    onExpired(entry.Value);
                }
                node = next;
            }
        }

        public bool Remove(TKey key)
        {
            LinkedListNode<Entry> node;
            if (!_entries.TryGetValue(key, out node)) return false;
            _entries.Remove(key);
            _order.Remove(node);
            return true;
        }

        public void Clear()
        {
            _entries.Clear();
            _order.Clear();
            _clock.Reset();
            _held = false;
        }
    }
}
