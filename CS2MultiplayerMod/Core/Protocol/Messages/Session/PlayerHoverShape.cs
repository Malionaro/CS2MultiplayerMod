using System;

namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    public enum PlayerHoverKind : byte { Circle = 1, Box = 2, Curve = 3 }

    /// <summary>Small, display-only geometry. Contains no entity references or tool commands.</summary>
    public struct PlayerHoverShape
    {
        public const int MaxShapes = 8;
        public const int WireSize = 62;
        public PlayerHoverKind Kind;
        public bool Placement;
        // Sender-local identity only controls smoothing; it is never resolved as a local entity.
        public int Key;
        public HoverPoint A, B, C, D;
        public float Width, Height;

        public void Write(NetworkWriter writer)
        {
            writer.WriteByte((byte)Kind);
            writer.WriteByte(Placement ? (byte)1 : (byte)0);
            writer.WriteInt(Key);
            A.Write(writer); B.Write(writer); C.Write(writer); D.Write(writer);
            writer.WriteFloat(Width); writer.WriteFloat(Height);
        }

        public static PlayerHoverShape Read(NetworkReader reader)
        {
            var shape = new PlayerHoverShape { Kind = (PlayerHoverKind)reader.ReadByte() };
            if (shape.Kind < PlayerHoverKind.Circle || shape.Kind > PlayerHoverKind.Curve)
                throw new ProtocolException("Unknown hover shape.");
            byte placement = reader.ReadByte();
            if (placement > 1) throw new ProtocolException("Invalid hover placement flag.");
            shape.Placement = placement != 0;
            shape.Key = reader.ReadInt();
            shape.A = HoverPoint.Read(reader); shape.B = HoverPoint.Read(reader);
            shape.C = HoverPoint.Read(reader); shape.D = HoverPoint.Read(reader);
            shape.Width = WireGuard.ReadFinite(reader);
            shape.Height = WireGuard.ReadFinite(reader);
            shape.Validate();
            return shape;
        }

        public void Validate()
        {
            if (Kind < PlayerHoverKind.Circle || Kind > PlayerHoverKind.Curve ||
                !A.Valid || !B.Valid || !C.Valid || !D.Valid ||
                float.IsNaN(Width) || Width < 0f || Width > 5000f ||
                float.IsNaN(Height) || Height < 0f || Height > 5000f)
                throw new ProtocolException("Invalid hover geometry.");
            if (Kind != PlayerHoverKind.Circle &&
                (!A.Near(B) || !A.Near(C) || !A.Near(D)))
                throw new ProtocolException("Hover geometry spans too far.");
        }
    }

    public struct HoverPoint
    {
        public float X, Y, Z;
        public HoverPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
        internal bool Valid => FiniteCoordinate(X) && FiniteCoordinate(Y) && FiniteCoordinate(Z);
        private static bool FiniteCoordinate(float value) =>
            !float.IsNaN(value) && Math.Abs(value) <= WireGuard.MaxCoordinate;
        internal bool Near(HoverPoint other) => Math.Abs(X - other.X) <= 10000f &&
            Math.Abs(Y - other.Y) <= 10000f && Math.Abs(Z - other.Z) <= 10000f;
        internal void Write(NetworkWriter writer)
        { writer.WriteFloat(X); writer.WriteFloat(Y); writer.WriteFloat(Z); }
        internal static HoverPoint Read(NetworkReader reader) => new HoverPoint(
            WireGuard.ReadCoordinate(reader), WireGuard.ReadCoordinate(reader), WireGuard.ReadCoordinate(reader));
    }
}
