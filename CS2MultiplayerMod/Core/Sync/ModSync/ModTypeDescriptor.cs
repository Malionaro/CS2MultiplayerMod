using System;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Sync.ModSync
{
    /// <summary>
    /// One replicated third-party type as it travels: what it is called, how it attaches, and the
    /// exact sequence of leaves its payload carries.
    ///
    /// The leaf sequence is part of the identity, not a hint. Both machines run the same mod build,
    /// but "same mod" is a claim about files on disk; a type whose fields differ would decode a
    /// payload into the wrong fields and write plausible nonsense into the other player's city.
    /// The fingerprint turns that into a refusal naming the type.
    /// </summary>
    public sealed class ModTypeDescriptor
    {
        /// <summary>Ceiling on leaves in one type - a struct past this is excluded, not truncated.</summary>
        public const int MaxLeaves = 256;

        /// <summary>Assembly-qualified enough to be unique, short enough to send once per session.</summary>
        public string Key { get; private set; }

        public ModTypeKind Kind { get; private set; }

        /// <summary>The flattened fields, in declaration order.</summary>
        public ModValueKind[] Leaves { get; private set; }

        /// <summary>Key plus leaf sequence, folded. Equal fingerprints mean equal decoding.</summary>
        public ulong Fingerprint { get; private set; }

        public ModTypeDescriptor(string key, ModTypeKind kind, ModValueKind[] leaves)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A mod type needs a key.", "key");
            Key = key;
            Kind = kind;
            Leaves = leaves ?? new ModValueKind[0];
            Fingerprint = ComputeFingerprint(key, kind, Leaves);
        }

        /// <summary>Leaves in one element - zero for a tag.</summary>
        public int LeafCount { get { return Leaves.Length; } }

        public static string MakeKey(string assemblyName, string typeFullName)
        {
            return assemblyName + "|" + typeFullName;
        }

        /// <summary>The type name alone, for a log line that has to stay readable.</summary>
        public string DisplayName
        {
            get
            {
                int bar = Key.IndexOf('|');
                return bar < 0 ? Key : Key.Substring(bar + 1);
            }
        }

        private static ulong ComputeFingerprint(string key, ModTypeKind kind, ModValueKind[] leaves)
        {
            // FNV-1a: stable across processes and runtimes, which string.GetHashCode is not.
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            for (int i = 0; i < keyBytes.Length; i++) { hash ^= keyBytes[i]; hash *= prime; }
            hash ^= (byte)kind; hash *= prime;
            for (int i = 0; i < leaves.Length; i++) { hash ^= (byte)leaves[i]; hash *= prime; }
            return hash;
        }

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(Key);
            writer.WriteByte((byte)Kind);
            writer.WriteLong(unchecked((long)Fingerprint));
            writer.WriteShort((short)Leaves.Length);
            for (int i = 0; i < Leaves.Length; i++) writer.WriteByte((byte)Leaves[i]);
        }

        public static ModTypeDescriptor Read(NetworkReader reader)
        {
            string key = reader.ReadString();
            var kind = (ModTypeKind)reader.ReadByte();
            ulong claimed = unchecked((ulong)reader.ReadLong());
            int count = reader.ReadShort();
            if (count < 0 || count > MaxLeaves)
                throw new ProtocolException("Mod type '" + key + "' declares " + count + " leaves.");

            var leaves = new ModValueKind[count];
            for (int i = 0; i < count; i++) leaves[i] = (ModValueKind)reader.ReadByte();

            var descriptor = new ModTypeDescriptor(key, kind, leaves);

            // The sender's own fingerprint has to agree with what its declaration reduces to here,
            // or the table itself is inconsistent and nothing built on it can be trusted.
            if (descriptor.Fingerprint != claimed)
                throw new ProtocolException("Mod type '" + key + "' carries a fingerprint that does not match its layout.");
            return descriptor;
        }
    }
}
