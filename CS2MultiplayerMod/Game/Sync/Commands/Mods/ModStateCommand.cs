using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Core.Sync.ModSync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "Everything another mod stores against this road, junction or building now looks like this."
    ///
    /// One command carries one carrier's complete replicated closure, so it can be applied on its
    /// own, applied twice without harm, and held and retried when the carrier has not arrived yet.
    /// It travels in both directions on the ordinary command path: whoever made the edit sends it,
    /// the host relays, and the sender is skipped by the usual origin check because it already has
    /// the state it is describing.
    /// </summary>
    public sealed class ModStateCommand : ISimulationCommand
    {
        public const ushort Id = 30;

        /// <summary>
        /// Refuses a body no honest closure reaches, on the network thread, before the type table
        /// is even consulted.
        /// </summary>
        public const int MaxBodyBytes = 512 * 1024;

        public ModStateSnapshot Snapshot;

        public ushort CommandId { get { return Id; } }

        /// <summary>
        /// The table both ends read this payload against. Set before encoding or decoding: the
        /// command names its types by index, so it is meaningless without one.
        /// </summary>
        public IModTypeLookup Types;

        public void Write(NetworkWriter writer)
        {
            Snapshot.Write(writer, Types);
        }

        public void Read(NetworkReader reader)
        {
            Snapshot = ModStateSnapshot.Read(reader, Types);
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(512);
            Write(writer);
            return writer.ToArray();
        }

        public static ModStateCommand Decode(byte[] body, IModTypeLookup types)
        {
            var command = new ModStateCommand { Types = types };
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
