using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Services;

namespace PadForge.Common.Input.WinUsbKeyboards
{
    /// <summary>A keyboard the user moved to PadForge's WinUSB driver from
    /// the Devices page (#549), or a mouse moved by the same switch: the USB
    /// node that was bound, the ID its package names, and the row the
    /// device had as a Raw Input keyboard or mouse, so the row PadForge
    /// reads it through takes over that row's slots, mappings and
    /// macros.</summary>
    public sealed class KeyboardTakeoverEntry
    {
        /// <summary>The <see cref="Kind"/> of a mouse's takeover. A
        /// keyboard's has none, as every takeover before the mouse switch
        /// had none.</summary>
        public const string MouseKind = "Mouse";

        /// <summary>The bound USB node's instance ID: the whole device, or
        /// the interface of a composite device or receiver the keyboard is
        /// on.</summary>
        [XmlAttribute] public string NodeId { get; set; }

        /// <summary>The ID the package names
        /// (<see cref="VendorUsbDriverInstaller.BindId"/>).</summary>
        [XmlAttribute] public string BindId { get; set; }

        /// <summary>The keyboard's row ID as a Raw Input keyboard, which
        /// SdlKeyboardWrapper hashes from its device path.</summary>
        [XmlAttribute] public string RowId { get; set; }

        /// <summary>The keyboard's name as its Raw Input row showed it.</summary>
        [XmlAttribute] public string Name { get; set; }

        [XmlAttribute] public ushort VendorId { get; set; }
        [XmlAttribute] public ushort ProductId { get; set; }

        /// <summary><see cref="MouseKind"/> for a mouse, empty for a
        /// keyboard.</summary>
        [XmlAttribute] public string Kind { get; set; }

        /// <summary>True for a mouse's takeover.</summary>
        [XmlIgnore]
        public bool IsMouse => string.Equals(Kind, MouseKind, StringComparison.OrdinalIgnoreCase);

        /// <summary>The row ID, or empty when the file held none.</summary>
        [XmlIgnore]
        public Guid Row => Guid.TryParse(RowId, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// The keyboard takeovers the settings hold, for the threads that read
    /// them away from the settings view model: the device pass, the input
    /// hooks and the Devices page's offer. Written whole at load and after
    /// each driver switch, from copies, so a reader never sees an entry
    /// change under it.
    /// </summary>
    internal static class KeyboardTakeovers
    {
        private static volatile KeyboardTakeoverEntry[] s_entries = Array.Empty<KeyboardTakeoverEntry>();

        /// <summary>Replaces the list with copies of the valid entries.</summary>
        internal static void Apply(IEnumerable<KeyboardTakeoverEntry> entries)
        {
            s_entries = entries == null
                ? Array.Empty<KeyboardTakeoverEntry>()
                : entries.Where(IsValid).Select(Copy).ToArray();
        }

        /// <summary>The takeovers now.</summary>
        internal static IReadOnlyList<KeyboardTakeoverEntry> Current => s_entries;

        /// <summary>True for the row of a keyboard or mouse PadForge's driver
        /// serves, whether or not it is plugged in. Its input never reaches
        /// Windows, so nothing of it goes to the input hooks.</summary>
        internal static bool IsTakenOver(Guid rowId)
        {
            if (rowId == Guid.Empty) return false;
            foreach (var entry in s_entries)
                if (entry.Row == rowId) return true;
            return false;
        }

        /// <summary>The takeover of a row, or null.</summary>
        internal static KeyboardTakeoverEntry ForRow(Guid rowId)
        {
            if (rowId == Guid.Empty) return null;
            foreach (var entry in s_entries)
                if (entry.Row == rowId) return entry;
            return null;
        }

        /// <summary>The takeover whose node a WinUSB interface path names,
        /// or null.</summary>
        internal static KeyboardTakeoverEntry ForInterfacePath(string path)
            => ForInterfacePath(s_entries, path);

        internal static KeyboardTakeoverEntry ForInterfacePath(IEnumerable<KeyboardTakeoverEntry> entries, string path)
        {
            if (entries == null || string.IsNullOrEmpty(path)) return null;
            foreach (var entry in entries)
                if (entry != null && VendorUsbDriverInstaller.NamesNode(new[] { path }, entry.NodeId)) return entry;
            return null;
        }

        /// <summary>The takeover of a node, or null.</summary>
        internal static KeyboardTakeoverEntry ForNode(IEnumerable<KeyboardTakeoverEntry> entries, string nodeId)
        {
            if (entries == null || string.IsNullOrEmpty(nodeId)) return null;
            foreach (var entry in entries)
                if (entry != null && string.Equals(entry.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)) return entry;
            return null;
        }

        /// <summary>An entry with a node, an ID to bind and a row.</summary>
        internal static bool IsValid(KeyboardTakeoverEntry entry)
            => entry != null
               && !string.IsNullOrWhiteSpace(entry.NodeId)
               && !string.IsNullOrWhiteSpace(entry.BindId)
               && entry.Row != Guid.Empty;

        internal static KeyboardTakeoverEntry Copy(KeyboardTakeoverEntry entry) => new()
        {
            NodeId = entry.NodeId,
            BindId = entry.BindId,
            RowId = entry.RowId,
            Name = entry.Name,
            VendorId = entry.VendorId,
            ProductId = entry.ProductId,
            Kind = entry.Kind,
        };
    }
}
