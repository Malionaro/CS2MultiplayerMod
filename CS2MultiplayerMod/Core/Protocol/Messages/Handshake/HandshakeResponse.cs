namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Host's reply to a <see cref="HandshakeRequest"/>. On acceptance it carries the
    /// assigned player id and the session's simulation-sync choice; on rejection it
    /// carries a human-readable reason.
    /// </summary>
    public sealed class HandshakeResponse : INetMessage
    {
        public bool Accepted;
        public int AssignedPlayerId;
        public string Reason;

        /// <summary>
        /// The host's simulation-sync choice for this session. A client follows it instead of
        /// its own setting: a peer that disagreed would hold its local simulation waiting for
        /// messages the host is never going to send, and end up with a city that never grows.
        /// </summary>
        public bool SimulationSync = true;

        public HandshakeResponse() { }

        public static HandshakeResponse Accept(int playerId, bool simulationSync = true) =>
            new HandshakeResponse
            {
                Accepted = true,
                AssignedPlayerId = playerId,
                Reason = null,
                SimulationSync = simulationSync,
            };

        public static HandshakeResponse Reject(string reason) =>
            new HandshakeResponse { Accepted = false, AssignedPlayerId = 0, Reason = reason };

        public MessageType Type => MessageType.HandshakeResponse;

        public void Write(NetworkWriter writer)
        {
            writer.WriteBool(Accepted);
            writer.WriteInt(AssignedPlayerId);
            writer.WriteString(Reason);
            writer.WriteBool(SimulationSync);
        }

        public void Read(NetworkReader reader)
        {
            Accepted = reader.ReadBool();
            AssignedPlayerId = reader.ReadInt();
            Reason = reader.ReadString();
            SimulationSync = reader.ReadBool();
        }
    }
}
