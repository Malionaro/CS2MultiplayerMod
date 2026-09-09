using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Ordered lifecycle events with replaceable progress samples. Coalescing happens at ingress,
    /// including while road realization is held, so repeated samples cannot fill a command FIFO.
    /// Lifecycle events and changes of prefab/construction/status are ordering barriers.
    /// </summary>
    internal sealed class GrowableCommandInbox
    {
        public const int LifecycleCapacity = 1024;
        public const int StateCapacity = 16384;
        public const int StateBudgetPerFrame = 64;
        private const int MaxDuplicateSequenceLag = 1024;

        private readonly object _gate = new object();
        private readonly LinkedList<GrowableLifecycleCommand> _order =
            new LinkedList<GrowableLifecycleCommand>();
        private readonly struct AnchorKey : IEquatable<AnchorKey>
        {
            private readonly float _x, _z;
            public AnchorKey(GrowableLifecycleCommand command)
            {
                _x = command.AnchorX;
                _z = command.AnchorZ;
            }
            public bool Equals(AnchorKey other) => _x.Equals(other._x) && _z.Equals(other._z);
            public override bool Equals(object obj) => obj is AnchorKey other && Equals(other);
            public override int GetHashCode() => unchecked(_x.GetHashCode() * 397 ^ _z.GetHashCode());
        }

        private readonly Dictionary<AnchorKey, LinkedListNode<GrowableLifecycleCommand>> _latest =
            new Dictionary<AnchorKey, LinkedListNode<GrowableLifecycleCommand>>();
        private int _states, _lifecycle;
        private long _coalesced;
        private bool _hasReceivedSequence;
        private uint _lastReceivedSequence;

        public int Count { get { lock (_gate) return _order.Count; } }
        public long Coalesced { get { lock (_gate) return _coalesced; } }

        public long TakeCoalescedCount()
        {
            lock (_gate)
            {
                long count = _coalesced;
                _coalesced = 0;
                return count;
            }
        }

        public bool TryEnqueue(GrowableLifecycleCommand command)
        {
            return TryEnqueue(command, out _);
        }

        // A value in resetFrom distinguishes a broken sequence baseline from capacity
        // exhaustion. The caller must request a snapshot before relying on the new stream.
        public bool TryEnqueue(GrowableLifecycleCommand command, out uint? resetFrom)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (_gate)
            {
                resetFrom = null;
                // This inbox receives one host's reliable ordered stream. Remember ingress,
                // not just applied samples: a coalesced-away sequence must stay a duplicate
                // even after its replacement has drained. Reset on each world barrier.
                if (_hasReceivedSequence)
                {
                    int delta = unchecked((int)(command.Sequence - _lastReceivedSequence));
                    if (delta < -MaxDuplicateSequenceLag)
                    {
                        // A large rewind can indicate a new producer baseline without a
                        // matching world drain. Do not silently reject the entire new stream
                        // until it catches up, or mix its work with the old queued suffix.
                        resetFrom = _lastReceivedSequence;
                        Clear();
                        return false;
                    }
                    if (delta <= 0) return true;
                }
                bool state = command.Op == GrowableLifecycleCommand.OpState;
                var key = new AnchorKey(command);
                LinkedListNode<GrowableLifecycleCommand> previous;
                if (state && _latest.TryGetValue(key, out previous))
                {
                    GrowableLifecycleCommand old = previous.Value;
                    // Keep every semantic transition. Only condition/progress samples of the
                    // same building state supersede one another, never a remove/spawn/level.
                    if (old.PrefabName == command.PrefabName && old.Flags == command.Flags &&
                        old.StateFlags == command.StateFlags)
                    {
                        previous.Value = command;
                        _coalesced++;
                        RememberSequence(command.Sequence);
                        return true;
                    }
                    _latest.Clear();
                }
                if (state ? _states >= StateCapacity : _lifecycle >= LifecycleCapacity)
                    return false;
                if (!state) _latest.Clear();
                LinkedListNode<GrowableLifecycleCommand> node = _order.AddLast(command);
                if (state)
                {
                    _states++;
                    _latest[key] = node;
                }
                else _lifecycle++;
                RememberSequence(command.Sequence);
                return true;
            }
        }

        // A budget stops at the head; independent state and creation budgets never reorder work.
        public bool TryTake(bool allowLifecycle, bool allowState, out GrowableLifecycleCommand command)
        {
            lock (_gate)
            {
                command = null;
                LinkedListNode<GrowableLifecycleCommand> node = _order.First;
                if (node == null) return false;
                bool state = node.Value.Op == GrowableLifecycleCommand.OpState;
                if (state ? !allowState : !allowLifecycle) return false;
                command = node.Value;
                _order.RemoveFirst();
                if (state)
                {
                    _states--;
                    var key = new AnchorKey(command);
                    LinkedListNode<GrowableLifecycleCommand> latest;
                    if (_latest.TryGetValue(key, out latest) && latest == node) _latest.Remove(key);
                }
                else _lifecycle--;
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _order.Clear();
                _latest.Clear();
                _states = _lifecycle = 0;
                _coalesced = 0;
                _hasReceivedSequence = false;
                _lastReceivedSequence = 0;
            }
        }

        private void RememberSequence(uint sequence)
        {
            _hasReceivedSequence = true;
            _lastReceivedSequence = sequence;
        }

        public static bool SameTarget(GrowableLifecycleCommand a, GrowableLifecycleCommand b) =>
            a.AnchorX == b.AnchorX && a.AnchorZ == b.AnchorZ;
    }
}
