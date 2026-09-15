using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// A third-party struct reduced to an ordered list of leaves, and the reflection needed to read
    /// and write them.
    ///
    /// Copying the struct's memory would be shorter, and wrong twice over: field offsets are a
    /// property of one process's layout decisions, and an <see cref="Entity"/> inside the struct is
    /// an index into one world's arrays that means something else in another. Walking declared
    /// fields costs reflection, which the measured traffic - a few hundred values on the frames
    /// where anything happens at all, none in between - can easily afford.
    /// </summary>
    internal sealed class ModFieldPlan
    {
        /// <summary>How deep a struct may nest before it is refused rather than walked further.</summary>
        private const int MaxDepth = 8;

        private const BindingFlags Fields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private FieldInfo[][] _paths;

        /// <summary>The leaf kinds, in the same order as <see cref="_paths"/>.</summary>
        public ModValueKind[] Kinds { get; private set; }

        public int Count { get { return Kinds.Length; } }

        /// <summary>True when at least one leaf is a reference that has to be translated.</summary>
        public bool HasReferences { get; private set; }

        /// <summary>
        /// Flattens <paramref name="type"/>, or explains in one phrase why it cannot be replicated.
        /// The reason is written into the catalogue listing, because "this mod is not synchronized"
        /// is only actionable when it says which type and what about it was the problem.
        /// </summary>
        public static bool TryBuild(Type type, out ModFieldPlan plan, out string reason)
        {
            plan = null;
            reason = null;

            var paths = new List<FieldInfo[]>();
            var kinds = new List<ModValueKind>();
            var path = new List<FieldInfo>();

            if (!Walk(type, path, paths, kinds, 0, ref reason)) return false;

            if (kinds.Count > ModTypeDescriptor.MaxLeaves)
            {
                reason = "has " + kinds.Count + " fields";
                return false;
            }

            plan = new ModFieldPlan
            {
                _paths = paths.ToArray(),
                Kinds = kinds.ToArray(),
            };
            for (int i = 0; i < plan.Kinds.Length; i++)
                if (plan.Kinds[i] == ModValueKind.EntityRef) plan.HasReferences = true;
            return true;
        }

        private static bool Walk(Type type, List<FieldInfo> path, List<FieldInfo[]> paths,
            List<ModValueKind> kinds, int depth, ref string reason)
        {
            if (depth > MaxDepth)
            {
                reason = "nests structs more than " + MaxDepth + " deep";
                return false;
            }

            FieldInfo[] fields = type.GetFields(Fields);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsStatic) continue;

                Type fieldType = field.FieldType;
                path.Add(field);
                try
                {
                    ModValueKind kind;
                    if (TryScalarKind(fieldType, out kind))
                    {
                        paths.Add(path.ToArray());
                        kinds.Add(kind);
                        continue;
                    }

                    if (!fieldType.IsValueType || fieldType.IsPointer)
                    {
                        reason = "field " + field.Name + " is a " + fieldType.Name;
                        return false;
                    }

                    // A fixed-size buffer ("fixed byte x[16]") compiles to a nested struct holding
                    // one field that stands for the first element, so walking it would quietly
                    // describe one byte and carry away fifteen. Refused by name instead. The
                    // engine's fixed-capacity strings are the common case and are handled above.
                    if (field.IsDefined(typeof(FixedBufferAttribute), false))
                    {
                        reason = "field " + field.Name + " is a fixed buffer";
                        return false;
                    }

                    if (!Walk(fieldType, path, paths, kinds, depth + 1, ref reason)) return false;
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }
            }
            return true;
        }

        private static bool TryScalarKind(Type type, out ModValueKind kind)
        {
            if (type == typeof(Entity)) { kind = ModValueKind.EntityRef; return true; }

            if (type.IsEnum) type = Enum.GetUnderlyingType(type);

            if (type == typeof(bool)) { kind = ModValueKind.Bool; return true; }
            if (type == typeof(sbyte)) { kind = ModValueKind.I8; return true; }
            if (type == typeof(byte)) { kind = ModValueKind.U8; return true; }
            if (type == typeof(short)) { kind = ModValueKind.I16; return true; }
            if (type == typeof(ushort)) { kind = ModValueKind.U16; return true; }
            if (type == typeof(char)) { kind = ModValueKind.U16; return true; }
            if (type == typeof(int)) { kind = ModValueKind.I32; return true; }
            if (type == typeof(uint)) { kind = ModValueKind.U32; return true; }
            if (type == typeof(long)) { kind = ModValueKind.I64; return true; }
            if (type == typeof(ulong)) { kind = ModValueKind.U64; return true; }
            if (type == typeof(float)) { kind = ModValueKind.F32; return true; }
            if (type == typeof(double)) { kind = ModValueKind.F64; return true; }

            if (IsFixedString(type)) { kind = ModValueKind.Text; return true; }

            kind = default(ModValueKind);
            return false;
        }

        /// <summary>
        /// The engine's fixed-capacity strings, recognised by the interfaces they carry rather than
        /// by listing the five sizes - a sixth would otherwise silently stop a mod being supported.
        /// </summary>
        private static bool IsFixedString(Type type)
        {
            return typeof(IUTF8Bytes).IsAssignableFrom(type) &&
                   typeof(INativeList<byte>).IsAssignableFrom(type);
        }

        /// <summary>Reads one boxed value's leaves into <paramref name="destination"/>.</summary>
        public void Read(object boxed, ModLeaf[] destination, int offset, Func<Entity, ModEntityRef> translate)
        {
            for (int i = 0; i < _paths.Length; i++)
            {
                object value = boxed;
                FieldInfo[] path = _paths[i];
                for (int step = 0; step < path.Length; step++) value = path[step].GetValue(value);
                destination[offset + i] = ToLeaf(Kinds[i], value, translate);
            }
        }

        /// <summary>Writes leaves back into a boxed value and returns it (structs box by copy).</summary>
        public object Write(object boxed, ModLeaf[] source, int offset, Func<ModEntityRef, Entity> translate)
        {
            for (int i = 0; i < _paths.Length; i++)
            {
                FieldInfo[] path = _paths[i];
                object value = FromLeaf(Kinds[i], path[path.Length - 1].FieldType, source[offset + i], translate);
                boxed = SetPath(boxed, path, 0, value);
            }
            return boxed;
        }

        private static object SetPath(object owner, FieldInfo[] path, int index, object value)
        {
            FieldInfo field = path[index];
            if (index == path.Length - 1)
            {
                field.SetValue(owner, value);
                return owner;
            }

            // Each level has to be read out, written into and put back: a boxed struct's nested
            // struct is a copy, so setting a field on it would otherwise be thrown away.
            object child = field.GetValue(owner);
            child = SetPath(child, path, index + 1, value);
            field.SetValue(owner, child);
            return owner;
        }

        private static ModLeaf ToLeaf(ModValueKind kind, object value, Func<Entity, ModEntityRef> translate)
        {
            switch (kind)
            {
                case ModValueKind.EntityRef:
                    return ModLeaf.FromReference(translate((Entity)value));
                case ModValueKind.Text:
                    return ModLeaf.FromText(value == null ? string.Empty : value.ToString());
                case ModValueKind.F32:
                    return ModLeaf.FromReal((float)value);
                case ModValueKind.F64:
                    return ModLeaf.FromReal((double)value);
                case ModValueKind.Bool:
                    return ModLeaf.FromInteger((bool)value ? 1 : 0);
                default:
                    return ModLeaf.FromInteger(ToInteger(value));
            }
        }

        private static long ToInteger(object value)
        {
            if (value is ulong) return unchecked((long)(ulong)value);
            if (value.GetType().IsEnum)
                value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()));
            if (value is ulong) return unchecked((long)(ulong)value);
            return Convert.ToInt64(value);
        }

        private static object FromLeaf(ModValueKind kind, Type fieldType, ModLeaf leaf,
            Func<ModEntityRef, Entity> translate)
        {
            switch (kind)
            {
                case ModValueKind.EntityRef:
                    return translate(leaf.Reference);
                case ModValueKind.Text:
                    return MakeFixedString(fieldType, leaf.Text);
                case ModValueKind.F32:
                    return (float)leaf.Real;
                case ModValueKind.F64:
                    return leaf.Real;
                case ModValueKind.Bool:
                    return leaf.Integer != 0;
            }

            Type target = fieldType.IsEnum ? Enum.GetUnderlyingType(fieldType) : fieldType;
            object scalar;
            if (target == typeof(sbyte)) scalar = unchecked((sbyte)leaf.Integer);
            else if (target == typeof(byte)) scalar = unchecked((byte)leaf.Integer);
            else if (target == typeof(short)) scalar = unchecked((short)leaf.Integer);
            else if (target == typeof(ushort)) scalar = unchecked((ushort)leaf.Integer);
            else if (target == typeof(char)) scalar = unchecked((char)leaf.Integer);
            else if (target == typeof(int)) scalar = unchecked((int)leaf.Integer);
            else if (target == typeof(uint)) scalar = unchecked((uint)leaf.Integer);
            else if (target == typeof(long)) scalar = leaf.Integer;
            else if (target == typeof(ulong)) scalar = unchecked((ulong)leaf.Integer);
            else scalar = Convert.ChangeType(leaf.Integer, target);

            return fieldType.IsEnum ? Enum.ToObject(fieldType, scalar) : scalar;
        }

        /// <summary>
        /// Builds a fixed-capacity string, shortening the text rather than throwing if it does not
        /// fit. A refused transaction over a name that is one character too long would strand the
        /// whole carrier, and the capacity is the receiving type's, so it cannot be checked here.
        /// </summary>
        private static object MakeFixedString(Type type, string text)
        {
            if (text == null) text = string.Empty;
            while (true)
            {
                try
                {
                    return Activator.CreateInstance(type, new object[] { text });
                }
                catch (Exception)
                {
                    if (text.Length == 0) return Activator.CreateInstance(type);
                    text = text.Substring(0, text.Length / 2);
                }
            }
        }
    }
}
