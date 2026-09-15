using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Sync.ModSync
{
    /// <summary>What a <see cref="ModEntityRef"/> points at, and therefore how it is resolved.</summary>
    public enum ModRefKind : byte
    {
        /// <summary>No entity. Also what an unresolvable reference degrades to when it is allowed to.</summary>
        Null = 0,

        /// <summary>A net node, found by its position.</summary>
        NetNode = 1,

        /// <summary>A net edge, found by the positions of the two nodes it runs between.</summary>
        NetEdge = 2,

        /// <summary>A placed object, found by its position and the prefab it was placed from.</summary>
        Object = 3,

        /// <summary>An area, found by the centroid of its nodes.</summary>
        Area = 4,

        /// <summary>A prefab, found by name - the one identity that is the same on every machine.</summary>
        Prefab = 5,

        /// <summary>
        /// An entity the mod made for its own bookkeeping, with no place in the world. It has no
        /// identity of its own: it exists as part of one carrier's state and is rebuilt with it,
        /// which is why it travels as an index into the transaction rather than as a world lookup.
        /// </summary>
        Satellite = 6,
    }

    /// <summary>
    /// A reference to an entity that means the same thing on both machines.
    ///
    /// Entity indices do not survive the trip - they are positions in one process's arrays, and a
    /// client keeps creating entities of its own the whole time it is connected. Everything that
    /// crosses the wire is therefore described by something the receiver can look up for itself.
    /// </summary>
    public struct ModEntityRef
    {
        public ModRefKind Kind;

        /// <summary>Primary position: the node, the object, the area centroid, the edge's first node.</summary>
        public float X, Y, Z;

        /// <summary>The edge's second node. Unused by every other kind.</summary>
        public float X2, Y2, Z2;

        /// <summary>Prefab name for <see cref="ModRefKind.Object"/> and <see cref="ModRefKind.Prefab"/>.</summary>
        public string Name;

        /// <summary>Position in the transaction's satellite list.</summary>
        public int SatelliteIndex;

        public static readonly ModEntityRef Null = new ModEntityRef { Kind = ModRefKind.Null };

        public static ModEntityRef Satellite(int index)
        {
            return new ModEntityRef { Kind = ModRefKind.Satellite, SatelliteIndex = index };
        }

        public static ModEntityRef Node(float x, float y, float z)
        {
            return new ModEntityRef { Kind = ModRefKind.NetNode, X = x, Y = y, Z = z };
        }

        public static ModEntityRef Edge(float ax, float ay, float az, float bx, float by, float bz)
        {
            return new ModEntityRef
            {
                Kind = ModRefKind.NetEdge,
                X = ax, Y = ay, Z = az,
                X2 = bx, Y2 = by, Z2 = bz,
            };
        }

        public static ModEntityRef Object(float x, float y, float z, string prefab)
        {
            return new ModEntityRef { Kind = ModRefKind.Object, X = x, Y = y, Z = z, Name = prefab };
        }

        public static ModEntityRef Area(float x, float y, float z)
        {
            return new ModEntityRef { Kind = ModRefKind.Area, X = x, Y = y, Z = z };
        }

        public static ModEntityRef Prefab(string name)
        {
            return new ModEntityRef { Kind = ModRefKind.Prefab, Name = name };
        }

        /// <summary>A stable, comparable identity - the echo guard and the shadow state key on it.</summary>
        public string Key()
        {
            switch (Kind)
            {
                case ModRefKind.Null: return "null";
                case ModRefKind.Satellite: return "sat:" + SatelliteIndex;
                case ModRefKind.Prefab: return "prefab:" + Name;
                case ModRefKind.NetEdge:
                    return "edge:" + Round(X) + "," + Round(Y) + "," + Round(Z) + "/" +
                           Round(X2) + "," + Round(Y2) + "," + Round(Z2);
                case ModRefKind.Object:
                    return "object:" + Name + ":" + Round(X) + "," + Round(Y) + "," + Round(Z);
                case ModRefKind.Area:
                    return "area:" + Round(X) + "," + Round(Y) + "," + Round(Z);
                default:
                    return "node:" + Round(X) + "," + Round(Y) + "," + Round(Z);
            }
        }

        /// <summary>Half-metre buckets: enough to survive float noise, far below anything's spacing.</summary>
        private static long Round(float value)
        {
            return (long)System.Math.Round(value * 2f);
        }

        public void Write(NetworkWriter writer)
        {
            writer.WriteByte((byte)Kind);
            switch (Kind)
            {
                case ModRefKind.Null:
                    break;
                case ModRefKind.Satellite:
                    writer.WriteInt(SatelliteIndex);
                    break;
                case ModRefKind.Prefab:
                    writer.WriteString(Name);
                    break;
                case ModRefKind.NetEdge:
                    writer.WriteFloat(X); writer.WriteFloat(Y); writer.WriteFloat(Z);
                    writer.WriteFloat(X2); writer.WriteFloat(Y2); writer.WriteFloat(Z2);
                    break;
                case ModRefKind.Object:
                    writer.WriteFloat(X); writer.WriteFloat(Y); writer.WriteFloat(Z);
                    writer.WriteString(Name);
                    break;
                default:
                    writer.WriteFloat(X); writer.WriteFloat(Y); writer.WriteFloat(Z);
                    break;
            }
        }

        public static ModEntityRef Read(NetworkReader reader, int satelliteCeiling)
        {
            var kind = (ModRefKind)reader.ReadByte();
            var value = new ModEntityRef { Kind = kind };
            switch (kind)
            {
                case ModRefKind.Null:
                    break;
                case ModRefKind.Satellite:
                    value.SatelliteIndex = reader.ReadInt();
                    if (value.SatelliteIndex < 0 || value.SatelliteIndex >= satelliteCeiling)
                        throw new ProtocolException("Mod state reference names satellite " +
                            value.SatelliteIndex + " of " + satelliteCeiling + ".");
                    break;
                case ModRefKind.Prefab:
                    value.Name = reader.ReadString();
                    break;
                case ModRefKind.NetEdge:
                    value.X = reader.ReadFloat(); value.Y = reader.ReadFloat(); value.Z = reader.ReadFloat();
                    value.X2 = reader.ReadFloat(); value.Y2 = reader.ReadFloat(); value.Z2 = reader.ReadFloat();
                    break;
                case ModRefKind.Object:
                    value.X = reader.ReadFloat(); value.Y = reader.ReadFloat(); value.Z = reader.ReadFloat();
                    value.Name = reader.ReadString();
                    break;
                case ModRefKind.NetNode:
                case ModRefKind.Area:
                    value.X = reader.ReadFloat(); value.Y = reader.ReadFloat(); value.Z = reader.ReadFloat();
                    break;
                default:
                    throw new ProtocolException("Mod state reference has unknown kind " + (byte)kind + ".");
            }
            return value;
        }
    }
}
