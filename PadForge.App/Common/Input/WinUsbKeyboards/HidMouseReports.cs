using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.WinUsbKeyboards
{
    /// <summary>
    /// A mouse's input reports as its report descriptor lays them out, for a
    /// mouse PadForge reads over WinUSB in place of Windows (the mouse
    /// switch beside #549's keyboard switch). The descriptor goes through
    /// the keyboard's parser (<see cref="HidKeyboardReports.ParseFields"/>)
    /// with a filter of its own. Pure, with no I/O.
    ///
    /// <para>The fields read are the ones Linux's HID input layer gives a
    /// mouse (linux-hid drivers/hid/hid-input.c). A Button page usage n is
    /// mouse button n, so 1 to 5 are left, right, middle, side and extra
    /// (:783-788, BTN_MOUSE + n - 1). Generic Desktop X and Y are motion
    /// when the field is relative (:908-914), and Wheel is the vertical
    /// wheel (:932). A boot mouse's report is that layout, described
    /// (drivers/hid/usbhid/usbmouse.c:69-77). The horizontal wheel is AC
    /// Pan on the Consumer page, read as REL_HWHEEL (:1282-1284), for Wheel
    /// Travel Sideways (#600).</para>
    ///
    /// <para>Motion and the wheel are changes since the last report, so
    /// they add up. A button field carries the state of its buttons, so it
    /// replaces them, and buttons in no field of the report keep theirs.</para>
    ///
    /// <para>A wheel with a Resolution Multiplier feature counts that many
    /// times finer while the feature is set (hid-core.c:1209-1219). Windows'
    /// mouse driver can leave it set, so <see cref="WheelMultiplierFeature"/>
    /// names the one that applies to the wheel, and the row divides by what
    /// it reads there (<see cref="EffectiveMultiplier"/>).</para>
    /// </summary>
    internal sealed class HidMouseReports
    {
        internal const ushort GenericDesktopPage = 0x01;
        internal const ushort ButtonPage = 0x09;
        internal const uint X = ((uint)GenericDesktopPage << 16) | 0x30;
        internal const uint Y = ((uint)GenericDesktopPage << 16) | 0x31;
        internal const uint Wheel = ((uint)GenericDesktopPage << 16) | 0x38;
        internal const ushort ConsumerPage = 0x0C;
        internal const uint Pan = ((uint)ConsumerPage << 16) | 0x238;

        /// <summary>The buttons a mouse row reads, Button page usages 1 to 5
        /// (bit n - 1 of the button state for button n).</summary>
        internal const int Buttons = 5;

        private readonly HidKeyboardReports.Field[] _fields;

        private HidMouseReports(HidKeyboardReports.Field[] fields, bool usesReportIds, HidKeyboardReports.Shape shape)
        {
            _fields = fields;
            UsesReportIds = usesReportIds;
            LongestInputReport = shape.LongestInputReport;
            WheelMultiplierFeature = MultiplierOf(shape, shape.WheelCollections);
            PanMultiplierFeature = MultiplierOf(shape, shape.PanCollections);
        }

        /// <summary>True when the descriptor declares report IDs, so every
        /// input report starts with one.</summary>
        public bool UsesReportIds { get; }

        /// <summary>The bytes of the longest input report the descriptor
        /// declares, on any page, the report ID included.</summary>
        public int LongestInputReport { get; }

        /// <summary>The Resolution Multiplier that applies to the wheel, or
        /// null when none does.</summary>
        internal HidKeyboardReports.Multiplier WheelMultiplierFeature { get; }

        /// <summary>The Resolution Multiplier that applies to the horizontal
        /// wheel, AC Pan, by the same rule, or null.</summary>
        internal HidKeyboardReports.Multiplier PanMultiplierFeature { get; }

        /// <summary>The fields that carry buttons, motion or the wheel, in
        /// descriptor order.</summary>
        internal IReadOnlyList<HidKeyboardReports.Field> Fields => _fields;

        /// <summary>Parses a report descriptor, or null when it is malformed
        /// or declares no relative X or Y, which is no mouse to read.</summary>
        internal static HidMouseReports Parse(ReadOnlySpan<byte> descriptor)
        {
            var fields = HidKeyboardReports.ParseFields(descriptor, CarriesPointer, out bool usesReportIds, out var shape);
            if (fields == null) return null;
            foreach (var field in fields)
            {
                if (!field.IsVariable || !field.IsRelative) continue;
                foreach (uint usage in field.Usages)
                    if (usage == X || usage == Y) return new HidMouseReports(fields, usesReportIds, shape);
            }
            return null;
        }

        /// <summary>The multiplier a wheel takes: one in a logical collection
        /// the wheel sits in, or one in no logical collection, the last such
        /// in the descriptor, as Linux leaves it (hid-core.c:1177-1198). A
        /// mouse that gives its wheel and its horizontal wheel a multiplier
        /// each keeps them in separate logical collections, so each wheel's
        /// <paramref name="collections"/> find its own.</summary>
        private static HidKeyboardReports.Multiplier MultiplierOf(HidKeyboardReports.Shape shape, List<int[]> collections)
        {
            HidKeyboardReports.Multiplier found = null;
            foreach (var multiplier in shape.Multipliers)
            {
                if (multiplier.LogicalCollection < 0) { found = multiplier; continue; }
                foreach (int[] chain in collections)
                    if (Array.IndexOf(chain, multiplier.LogicalCollection) >= 0) found = multiplier;
            }
            return found;
        }

        /// <summary>What the wheel counts per notch with the feature report
        /// as read, the HID Usage Tables' Effective Resolution Multiplier. A
        /// range of nothing, a report too short, or an answer of zero or past
        /// 255 reads as 1, as Linux takes it (hid_calculate_multiplier,
        /// hid-core.c:1083-1120).</summary>
        internal int EffectiveMultiplier(ReadOnlySpan<byte> featureReport)
            => EffectiveMultiplier(WheelMultiplierFeature, featureReport);

        /// <summary>The same for <paramref name="feature"/>, the horizontal
        /// wheel's included.</summary>
        internal int EffectiveMultiplier(HidKeyboardReports.Multiplier feature, ReadOnlySpan<byte> featureReport)
        {
            if (feature == null) return 1;
            int span = feature.LogicalMaximum - feature.LogicalMinimum;
            if (span == 0 || featureReport.Length < feature.ReportLength) return 1;
            ReadOnlySpan<byte> data = UsesReportIds ? featureReport.Slice(1) : featureReport;
            int value = HidKeyboardReports.Extract(data, feature.BitOffset, feature.Size, feature.LogicalMinimum < 0);
            long multiplier = (long)(value - feature.LogicalMinimum) * (feature.PhysicalMaximum - feature.PhysicalMinimum) / span
                + feature.PhysicalMinimum;
            return multiplier == 0 || multiplier > 255 || multiplier < -255 ? 1 : (int)multiplier;
        }

        /// <summary>Decodes one input report as the pipe delivered it: adds
        /// its motion and wheel counts and sets the buttons its fields
        /// carry. False, with nothing changed, for an empty report or one
        /// whose ID no field uses.</summary>
        internal bool Decode(ReadOnlySpan<byte> report, ref int dx, ref int dy, ref int wheel, ref int buttons)
        {
            int pan = 0;
            return Decode(report, ref dx, ref dy, ref wheel, ref pan, ref buttons);
        }

        /// <summary>The same, with the horizontal wheel's counts added to
        /// <paramref name="pan"/>.</summary>
        internal bool Decode(ReadOnlySpan<byte> report, ref int dx, ref int dy, ref int wheel, ref int pan, ref int buttons)
        {
            if (report.Length == 0) return false;
            byte id = 0;
            ReadOnlySpan<byte> data = report;
            if (UsesReportIds)
            {
                id = report[0];
                data = report.Slice(1);
            }

            bool any = false;
            foreach (var field in _fields)
            {
                if (field.ReportId != id) continue;
                any = true;
                if (field.IsVariable) ReadVariable(field, data, ref dx, ref dy, ref wheel, ref pan, ref buttons);
                else ReadArray(field, data, ref buttons);
            }
            return any;
        }

        private static void ReadVariable(HidKeyboardReports.Field field, ReadOnlySpan<byte> data,
            ref int dx, ref int dy, ref int wheel, ref int pan, ref int buttons)
        {
            bool signed = field.LogicalMinimum < 0;
            for (int n = 0; n < field.Count; n++)
            {
                uint usage = field.Usages[n];
                int value = HidKeyboardReports.Extract(data, field.BitOffset + n * field.Size, field.Size, signed);
                int bit = ButtonBit(usage);
                if (bit >= 0)
                {
                    if (value != 0) buttons |= bit;
                    else buttons &= ~bit;
                }
                else if (field.IsRelative)
                {
                    if (usage == X) dx += value;
                    else if (usage == Y) dy += value;
                    else if (usage == Wheel) wheel += value;
                    else if (usage == Pan) pan += value;
                }
            }
        }

        /// <summary>A button array lists the buttons down, each value
        /// indexing the field's usages within its logical range, as a
        /// keyboard's key array does (hid_array_value_is_valid). Its
        /// buttons take that list, and no others change.</summary>
        private static void ReadArray(HidKeyboardReports.Field field, ReadOnlySpan<byte> data, ref int buttons)
        {
            int mask = 0;
            foreach (uint usage in field.Usages)
            {
                int bit = ButtonBit(usage);
                if (bit >= 0) mask |= bit;
            }
            if (mask == 0) return;

            bool signed = field.LogicalMinimum < 0;
            int down = 0;
            for (int n = 0; n < field.Count; n++)
            {
                int value = HidKeyboardReports.Extract(data, field.BitOffset + n * field.Size, field.Size, signed);
                if (value < field.LogicalMinimum || value > field.LogicalMaximum
                    || (long)value - field.LogicalMinimum >= field.Usages.Length)
                    continue;
                int bit = ButtonBit(field.Usages[value - field.LogicalMinimum]);
                if (bit >= 0) down |= bit;
            }
            buttons = (buttons & ~mask) | down;
        }

        /// <summary>The state bit of a Button page usage 1 to 5, or -1.</summary>
        private static int ButtonBit(uint usage)
        {
            if (usage >> 16 != ButtonPage) return -1;
            uint n = usage & 0xFFFF;
            return n >= 1 && n <= Buttons ? 1 << (int)(n - 1) : -1;
        }

        private static bool CarriesPointer(uint[] usages)
        {
            foreach (uint usage in usages)
                if (usage >> 16 == ButtonPage || usage == X || usage == Y || usage == Wheel || usage == Pan) return true;
            return false;
        }
    }
}
