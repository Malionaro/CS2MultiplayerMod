namespace CS2MultiplayerMod.Core.Sync.ModSync
{
    /// <summary>
    /// The leaf types a replicated third-party component can be broken down into.
    ///
    /// A component travels as its flattened leaves rather than as a block of memory: the same
    /// struct can sit at a different offset in another process, and an <see cref="EntityRef"/>
    /// leaf has to be translated on arrival rather than copied. Anything that cannot be reduced
    /// to this list is not replicated, and the catalogue says so by name.
    /// </summary>
    public enum ModValueKind : byte
    {
        Bool = 1,
        I8 = 2,
        U8 = 3,
        I16 = 4,
        U16 = 5,
        I32 = 6,
        U32 = 7,
        I64 = 8,
        U64 = 9,
        F32 = 10,
        F64 = 11,

        /// <summary>A reference to another entity - never copied, always re-resolved on arrival.</summary>
        EntityRef = 12,

        /// <summary>A fixed-capacity string (the engine's FixedString family).</summary>
        Text = 13,
    }

    /// <summary>How a replicated type attaches to an entity.</summary>
    public enum ModTypeKind : byte
    {
        /// <summary>A component with at least one field.</summary>
        Component = 1,

        /// <summary>A zero-sized component: its presence is the whole value.</summary>
        Tag = 2,

        /// <summary>A dynamic buffer; the element count travels with the payload.</summary>
        Buffer = 3,
    }
}
