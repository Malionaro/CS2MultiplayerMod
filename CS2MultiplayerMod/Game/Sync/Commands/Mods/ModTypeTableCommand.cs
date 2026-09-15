using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Core.Sync.ModSync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "These are the third-party types this session replicates, in this order."
    ///
    /// Sent by the host when gameplay sync opens and whenever a peer joins, because a transaction
    /// names its types by position in this table. A client binds each entry to its own local type
    /// and reports whatever it cannot bind: same mod, different layout is exactly the case that
    /// matching version numbers do not catch.
    /// </summary>
    public sealed class ModTypeTableCommand : ISimulationCommand
    {
        public const ushort Id = 29;

        public ModTypeTable Table;

        public ushort CommandId { get { return Id; } }

        public void Write(NetworkWriter writer)
        {
            Table.Write(writer);
        }

        public void Read(NetworkReader reader)
        {
            Table = ModTypeTable.Read(reader);
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(2048);
            Write(writer);
            return writer.ToArray();
        }

        public static ModTypeTableCommand Decode(byte[] body)
        {
            var command = new ModTypeTableCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
