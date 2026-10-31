using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.WinUsbKeyboards
{
    /// <summary>
    /// A keyboard's input reports as its report descriptor lays them out
    /// (#549), for a keyboard PadForge reads over WinUSB, where Windows'
    /// HID parser is not in the stack. Pure, with no I/O, so every rule is
    /// tested byte for byte.
    ///
    /// <para>The descriptor is parsed the way Linux's HID core builds its
    /// fields (linux-hid drivers/hid/hid-core.c). A Usage of one or two
    /// bytes takes the Usage Page in force when it is read, and a Usage Page
    /// declared after it, before the main item, replaces that page
    /// (complete_usage and hid_add_usage, :252-285, and
    /// hid_concatenate_last_usage_page, :609-638). A Usage Minimum and Usage
    /// Maximum pair adds every usage between them (:560-597). Each Input item
    /// is a field of report size times report count bits at the next offset
    /// of its report ID, and a field with no usage is padding that only
    /// moves the offset (hid_add_field, :290-375). The field's usage list
    /// is as long as its usages or its report count, whichever is more,
    /// and an excess value repeats the last usage. Logical Maximum is read
    /// signed only when Logical Minimum is below zero (:438-442).</para>
    ///
    /// <para>A report is decoded as Linux's hid_input_fetch_field and its
    /// field readers do (:1603-1700). A report shorter than its layout is
    /// read with zeros after its end (hid_report_raw_event). A variable
    /// field's values are the states of its usages. An array field's
    /// values name its pressed usages: a value counts when it lies within
    /// the logical range and indexes the usage list (hid_array_value_is_valid,
    /// :1584-1597), and a field holding ErrorRollOver, the keyboard's "too
    /// many keys" mark, is left as it was.</para>
    ///
    /// <para>Only the Keyboard/Keypad page and the Consumer page carry keys
    /// a keyboard row reads (<see cref="KbmHidKeyMap.TargetOf"/>). Fields
    /// on other pages are parsed for their offsets and never decoded.</para>
    /// </summary>
    internal sealed class HidKeyboardReports
    {
        internal const ushort KeyboardPage = 0x07;
        internal const ushort ConsumerPage = 0x0C;

        /// <summary>Keyboard ErrorRollOver, the usage a keyboard reports in
        /// every slot of its key array when more keys are down than the
        /// array holds (HID Usage Tables, Keyboard/Keypad page 0x01).</summary>
        internal const uint ErrorRollOver = ((uint)KeyboardPage << 16) | 0x01;

        /// <summary>The longest descriptor read. Keyboards declare a few
        /// hundred bytes. The bound keeps a malformed one from growing
        /// tables without end.</summary>
        internal const int MaxDescriptorLength = 4096;

        /// <summary>The most usages one field may list. A Usage Maximum that
        /// would pass it is cut to fit, as Linux cuts one (:572-584).</summary>
        internal const int MaxUsages = 12288;

        /// <summary>The longest report a field may reach into, in bits:
        /// 4096 bytes, far above any keyboard's report.</summary>
        internal const int MaxReportBits = 4096 * 8;

        /// <summary>How deep Push may nest. Linux refuses a Push past its
        /// own stack and a Pop with nothing pushed (:408-427).</summary>
        internal const int MaxPushDepth = 8;

        /// <summary>The widest Report Size Linux accepts (:474-478). A key
        /// field is at most 32 bits wide, and a wider field is parsed for
        /// its offset only.</summary>
        internal const int MaxReportSize = 256;

        /// <summary>One Input field: where it sits in its report and how
        /// its values read.</summary>
        internal sealed class Field
        {
            public byte ReportId { get; init; }
            public int BitOffset { get; init; }
            public int Size { get; init; }
            public int Count { get; init; }
            public int LogicalMinimum { get; init; }
            public int LogicalMaximum { get; init; }
            public bool IsVariable { get; init; }

            /// <summary>Page in the high 16 bits, usage in the low 16, as
            /// long as the larger of the usages listed and the report
            /// count.</summary>
            public uint[] Usages { get; init; }
        }

        private readonly Field[] _fields;
        private readonly HashSet<uint>[] _pressed;

        private HidKeyboardReports(Field[] fields, bool usesReportIds)
        {
            _fields = fields;
            UsesReportIds = usesReportIds;
            _pressed = new HashSet<uint>[fields.Length];
            for (int i = 0; i < fields.Length; i++) _pressed[i] = new HashSet<uint>();
        }

        /// <summary>True when the descriptor declares report IDs, so every
        /// input report starts with one.</summary>
        public bool UsesReportIds { get; }

        /// <summary>The fields that carry keys, in descriptor order.</summary>
        internal IReadOnlyList<Field> Fields => _fields;

        /// <summary>Parses a report descriptor, or null when it is malformed
        /// or declares no input field on the Keyboard/Keypad or Consumer
        /// page.</summary>
        internal static HidKeyboardReports Parse(ReadOnlySpan<byte> descriptor)
        {
            if (descriptor.Length == 0 || descriptor.Length > MaxDescriptorLength) return null;

            var fields = new List<Field>();
            var offsets = new Dictionary<byte, int>();
            var stack = new Stack<Globals>();
            var globals = new Globals();
            var usages = new List<uint>();
            var usageSizes = new List<int>();
            uint usageMinimum = 0;
            int delimiterDepth = 0, delimiterBranch = 0;
            bool usesReportIds = false;

            int i = 0;
            while (i < descriptor.Length)
            {
                byte prefix = descriptor[i++];

                // A long item: its data size, its tag, then the data. HID
                // defines no long item, so it is passed over.
                if (prefix == 0xFE)
                {
                    if (i + 2 > descriptor.Length) return null;
                    int longSize = descriptor[i];
                    i += 2 + longSize;
                    if (i > descriptor.Length) return null;
                    continue;
                }

                int size = prefix & 0x03;
                if (size == 3) size = 4;
                int type = (prefix >> 2) & 0x03;
                int tag = (prefix >> 4) & 0x0F;
                if (i + size > descriptor.Length) return null;
                uint udata = 0;
                for (int b = 0; b < size; b++) udata |= (uint)descriptor[i + b] << (8 * b);
                int sdata = size switch
                {
                    1 => (sbyte)udata,
                    2 => (short)udata,
                    4 => (int)udata,
                    _ => 0,
                };
                i += size;

                switch (type)
                {
                    case 1: // Global
                        switch (tag)
                        {
                            case 0: globals.UsagePage = udata & 0xFFFF; break;
                            case 1: globals.LogicalMinimum = sdata; break;
                            case 2: globals.LogicalMaximum = globals.LogicalMinimum < 0 ? sdata : (int)udata; break;
                            case 7: globals.ReportSize = (int)udata; break;
                            case 8:
                                if (udata == 0 || udata > 0xFF) return null;
                                globals.ReportId = (byte)udata;
                                usesReportIds = true;
                                break;
                            case 9: globals.ReportCount = (int)udata; break;
                            case 10:
                                if (stack.Count >= MaxPushDepth) return null;
                                stack.Push(globals);
                                break;
                            case 11:
                                if (stack.Count == 0) return null;
                                globals = stack.Pop();
                                break;
                        }
                        break;

                    case 2: // Local
                        switch (tag)
                        {
                            case 0:
                                // Only the first usage set of a delimited
                                // group counts, as in Linux (:544-548).
                                if (delimiterBranch > 1) break;
                                if (!AddUsage(usages, usageSizes, udata, size, globals.UsagePage)) return null;
                                break;
                            case 1:
                                if (delimiterBranch > 1) break;
                                usageMinimum = udata;
                                break;
                            case 2:
                                if (delimiterBranch > 1) break;
                                uint maximum = udata;
                                // A range past the bound is cut to fit
                                // (:572-584). A Usage Maximum below its
                                // Usage Minimum adds nothing.
                                if (maximum >= usageMinimum
                                    && (ulong)maximum - usageMinimum + (ulong)usages.Count >= MaxUsages)
                                {
                                    if (usages.Count >= MaxUsages) return null;
                                    maximum = usageMinimum + (uint)(MaxUsages - usages.Count - 1);
                                }
                                for (ulong n = usageMinimum; n <= maximum; n++)
                                    if (!AddUsage(usages, usageSizes, (uint)n, size, globals.UsagePage)) return null;
                                break;
                            case 10:
                                if (udata != 0)
                                {
                                    if (delimiterDepth != 0) return null;
                                    delimiterDepth++;
                                    delimiterBranch++;
                                }
                                else
                                {
                                    if (delimiterDepth < 1) return null;
                                    delimiterDepth--;
                                }
                                break;
                        }
                        break;

                    case 0: // Main
                        // A Usage Page declared after the usages, before this
                        // item, is theirs (hid_concatenate_last_usage_page).
                        for (int u = usages.Count - 1; u >= 0; u--)
                        {
                            if (usageSizes[u] > 2) continue;
                            if ((usages[u] >> 16) == globals.UsagePage) break;
                            usages[u] = (usages[u] & 0xFFFF) | (globals.UsagePage << 16);
                        }

                        if (tag == 8) // Input
                        {
                            if (globals.ReportSize < 0 || globals.ReportSize > MaxReportSize || globals.ReportCount < 0) return null;
                            if (LogicalRangeInvalid(globals.LogicalMinimum, globals.LogicalMaximum)) return null;
                            offsets.TryGetValue(globals.ReportId, out int offset);
                            long end = (long)offset + (long)globals.ReportSize * globals.ReportCount;
                            if (end > MaxReportBits) return null;
                            offsets[globals.ReportId] = (int)end;

                            // A field with no usage is padding (:334-335).
                            if (usages.Count > 0 && globals.ReportSize > 0 && globals.ReportSize <= 32
                                && globals.ReportCount > 0)
                            {
                                int count = Math.Max(usages.Count, globals.ReportCount);
                                var list = new uint[count];
                                for (int u = 0; u < count; u++)
                                    list[u] = usages[Math.Min(u, usages.Count - 1)];
                                if (CarriesKeys(list))
                                {
                                    fields.Add(new Field
                                    {
                                        ReportId = globals.ReportId,
                                        BitOffset = offset,
                                        Size = globals.ReportSize,
                                        Count = globals.ReportCount,
                                        LogicalMinimum = globals.LogicalMinimum,
                                        LogicalMaximum = globals.LogicalMaximum,
                                        IsVariable = (udata & 0x02) != 0,
                                        Usages = list,
                                    });
                                }
                            }
                        }

                        // Every main item ends the local items (:675).
                        usages.Clear();
                        usageSizes.Clear();
                        usageMinimum = 0;
                        delimiterDepth = 0;
                        delimiterBranch = 0;
                        break;
                }
            }

            return fields.Count == 0 ? null : new HidKeyboardReports(fields.ToArray(), usesReportIds);
        }

        /// <summary>Decodes one input report as the pipe delivered it and
        /// writes the usages held down across every report into
        /// <paramref name="pressed"/>. False, with
        /// <paramref name="pressed"/> left alone, for an empty report or
        /// one whose ID no key field uses.</summary>
        internal bool Decode(ReadOnlySpan<byte> report, HashSet<uint> pressed)
        {
            if (pressed == null || report.Length == 0) return false;
            byte id = 0;
            ReadOnlySpan<byte> data = report;
            if (UsesReportIds)
            {
                id = report[0];
                data = report.Slice(1);
            }

            bool any = false;
            for (int f = 0; f < _fields.Length; f++)
            {
                var field = _fields[f];
                if (field.ReportId != id) continue;
                any = true;
                if (field.IsVariable) ReadVariable(field, data, _pressed[f]);
                else ReadArray(field, data, _pressed[f]);
            }
            if (!any) return false;

            pressed.Clear();
            foreach (var set in _pressed) pressed.UnionWith(set);
            return true;
        }

        /// <summary>Forgets every pressed usage, for a keyboard that left.</summary>
        internal void Reset()
        {
            foreach (var set in _pressed) set.Clear();
        }

        private static void ReadVariable(Field field, ReadOnlySpan<byte> data, HashSet<uint> held)
        {
            held.Clear();
            for (int n = 0; n < field.Count; n++)
            {
                int value = Extract(data, field.BitOffset + n * field.Size, field.Size, field.LogicalMinimum < 0);
                uint usage = field.Usages[n];
                if (value != 0 && IsKey(usage)) held.Add(usage);
            }
        }

        private static void ReadArray(Field field, ReadOnlySpan<byte> data, HashSet<uint> held)
        {
            var values = new int[field.Count];
            for (int n = 0; n < field.Count; n++)
            {
                values[n] = Extract(data, field.BitOffset + n * field.Size, field.Size, field.LogicalMinimum < 0);
                // ErrorRollOver: the field keeps what it held (:1625-1631).
                if (Valid(field, values[n]) && field.Usages[values[n] - field.LogicalMinimum] == ErrorRollOver)
                    return;
            }
            held.Clear();
            foreach (int value in values)
            {
                if (!Valid(field, value)) continue;
                uint usage = field.Usages[value - field.LogicalMinimum];
                if (IsKey(usage)) held.Add(usage);
            }
        }

        private static bool Valid(Field field, int value)
            => value >= field.LogicalMinimum
               && value <= field.LogicalMaximum
               && (long)value - field.LogicalMinimum < field.Usages.Length;

        /// <summary>A usage a key can be: on the Keyboard/Keypad page above
        /// the three error codes and 0, or on the Consumer page above 0.</summary>
        internal static bool IsKey(uint usage)
        {
            uint page = usage >> 16, id = usage & 0xFFFF;
            return page == KeyboardPage ? id > 0x03 : page == ConsumerPage && id != 0;
        }

        private static bool CarriesKeys(uint[] usages)
        {
            foreach (uint usage in usages)
            {
                uint page = usage >> 16;
                if (page == KeyboardPage || page == ConsumerPage) return true;
            }
            return false;
        }

        /// <summary>A field's bits, least significant first, as HID packs
        /// them, read as zero past the report's end. Signed fields are
        /// sign-extended from their size.</summary>
        internal static int Extract(ReadOnlySpan<byte> data, int bitOffset, int size, bool signed)
        {
            if (size <= 0) return 0;
            uint value = 0;
            for (int b = 0; b < size; b++)
            {
                int bit = bitOffset + b;
                int index = bit >> 3;
                if (index < data.Length && ((data[index] >> (bit & 7)) & 1) != 0)
                    value |= 1u << b;
            }
            if (signed && size < 32 && (value & (1u << (size - 1))) != 0)
                value |= ~0u << size;
            return (int)value;
        }

        /// <summary>The logical range check of hid_add_field (:310-321).</summary>
        private static bool LogicalRangeInvalid(int minimum, int maximum)
            => minimum < 0 ? maximum < minimum : (uint)maximum < (uint)minimum;

        private static bool AddUsage(List<uint> usages, List<int> sizes, uint usage, int size, uint page)
        {
            if (usages.Count >= MaxUsages) return false;
            // An extended usage carries its own page. A short one takes the
            // page in force now (complete_usage).
            usages.Add(size <= 2 ? (usage & 0xFFFF) | (page << 16) : usage);
            sizes.Add(size);
            return true;
        }

        private struct Globals
        {
            public uint UsagePage;
            public int LogicalMinimum;
            public int LogicalMaximum;
            public int ReportSize;
            public int ReportCount;
            public byte ReportId;
        }
    }
}
