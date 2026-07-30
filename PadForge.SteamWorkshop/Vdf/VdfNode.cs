using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PadForge.SteamWorkshop.Vdf
{
    /// <summary>
    /// A node in a parsed Steam KeyValues (VDF) document. A node is either an object
    /// (an ordered list of key/child pairs, duplicate keys preserved), a scalar value
    /// (a string), or the <see cref="Missing"/> sentinel returned by the indexer when a
    /// key is absent. The sentinel makes navigation null-safe: indexing or enumerating a
    /// missing node yields another missing node / an empty sequence rather than throwing.
    /// </summary>
    public sealed class VdfNode
    {
        private enum Kind { Missing, Value, Object }

        /// <summary>Null-safe sentinel returned for absent keys.</summary>
        public static readonly VdfNode Missing = new VdfNode();

        private readonly List<KeyValuePair<string, VdfNode>> _children;
        private readonly string _value;
        private readonly Kind _kind;

        private VdfNode()
        {
            _kind = Kind.Missing;
        }

        private VdfNode(string value)
        {
            _kind = Kind.Value;
            _value = value;
        }

        private VdfNode(List<KeyValuePair<string, VdfNode>> children)
        {
            _kind = Kind.Object;
            _children = children;
        }

        internal static VdfNode NewValue(string value) => new VdfNode(value);

        internal static VdfNode NewObject(List<KeyValuePair<string, VdfNode>> children) => new VdfNode(children);

        public bool IsMissing => _kind == Kind.Missing;

        public bool IsObject => _kind == Kind.Object;

        public bool IsValue => _kind == Kind.Value;

        /// <summary>The scalar string for a value node; null for object/missing nodes.</summary>
        public string AsString => _kind == Kind.Value ? _value : null;

        /// <summary>The scalar parsed as a base-10 integer (InvariantCulture), or null.</summary>
        public long? AsInt
        {
            get
            {
                if (_kind != Kind.Value || _value == null) return null;
                return long.TryParse(_value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : (long?)null;
            }
        }

        /// <summary>The scalar parsed as a double (InvariantCulture), or null.</summary>
        public double? AsDouble
        {
            get
            {
                if (_kind != Kind.Value || _value == null) return null;
                return double.TryParse(_value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : (double?)null;
            }
        }

        /// <summary>The scalar parsed as a bool ("1"/"0" or true/false), or null.</summary>
        public bool? AsBool
        {
            get
            {
                var s = AsString?.Trim();
                if (string.IsNullOrEmpty(s)) return null;
                if (s == "1") return true;
                if (s == "0") return false;
                return bool.TryParse(s, out var b) ? b : (bool?)null;
            }
        }

        /// <summary>Number of key/child pairs on an object node (0 for value/missing).</summary>
        public int ChildCount => _children?.Count ?? 0;

        /// <summary>
        /// The first child with the given key (case-insensitive, VDF semantics), or
        /// <see cref="Missing"/>. Never returns null.
        /// </summary>
        public VdfNode this[string key]
        {
            get
            {
                if (_kind != Kind.Object || key == null) return Missing;
                foreach (var kv in _children)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
                return Missing;
            }
        }

        /// <summary>
        /// All children with the given key in document order (case-insensitive). Steam
        /// VDFs routinely repeat "group", "preset", and "binding" keys at the same level.
        /// </summary>
        public IReadOnlyList<VdfNode> Multi(string key)
        {
            if (_kind != Kind.Object || key == null) return Array.Empty<VdfNode>();
            List<VdfNode> list = null;
            foreach (var kv in _children)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    (list ??= new List<VdfNode>()).Add(kv.Value);
            }
            return (IReadOnlyList<VdfNode>)list ?? Array.Empty<VdfNode>();
        }

        /// <summary>All key/child pairs in document order (empty for value/missing nodes).</summary>
        public IEnumerable<KeyValuePair<string, VdfNode>> Children =>
            _children ?? Enumerable.Empty<KeyValuePair<string, VdfNode>>();
    }
}
