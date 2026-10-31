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
    /// (drivers/hid/usbhid/usbmouse.c:69-77). The horizontal wheel, AC Pan
    /// on the Consumer page (:1282), has no axis on a mouse row and is not
    /// read.</para>
    ///
    /// <para>Motion and the wheel are changes since the last report, so
    /// they add up. A button field carries the state of its buttons, so it
    /// replaces them, and buttons in no field of the report keep theirs.</para>
    /// </summary>
    internal sealed class HidMouseReports
    {
        internal const ushort GenericDesktopPage = 0x01;
        internal const ushort ButtonPage = 0x09;
        internal const uint X = ((uint)GenericDesktopPage << 16) | 0x30;
        internal const uint Y = ((uint)GenericDesktopPage << 16) | 0x31;
        internal const uint Wheel = ((uint)GenericDesktopPage << 16) | 0x38;

        /// <summary>The buttons a mouse row reads, Button page usages 1 to 5
        /// (bit n - 1 of the button state for button n).</summary>
        internal const int Buttons = 5;

        private readonly HidKeyboardReports.Field[] _fields;

        private HidMouseReports(HidKeyboardReports.Field[] fields, bool usesReportIds)
        {
            _fields = fields;
            UsesReportIds = usesReportIds;
        }

        /// <summary>True when the descriptor declares report IDs, so every
        /// input report starts with one.</summary>
        public bool UsesReportIds { get; }

        /// <summary>The fields that carry buttons, motion or the wheel, in
        /// descriptor order.</summary>
        internal IReadOnlyList<HidKeyboardReports.Field> Fields => _fields;

        /// <summary>Parses a report descriptor, or null when it is malformed
        /// or declares no relative X or Y, which is no mouse to read.</summary>
        internal static HidMouseReports Parse(ReadOnlySpan<byte> descriptor)
        {
            var fields = HidKeyboardReports.ParseFields(descriptor, CarriesPointer, out bool usesReportIds);
            if (fields == null) return null;
            foreach (var field in fields)
            {
                if (!field.IsVariable || !field.IsRelative) continue;
                foreach (uint usage in field.Usages)
                    if (usage == X || usage == Y) return new HidMouseReports(fields, usesReportIds);
            }
            return null;
        }

        /// <summary>Decodes one input report as the pipe delivered it: adds
        /// its motion and wheel counts and sets the buttons its fields
        /// carry. False, with nothing changed, for an empty report or one
        /// whose ID no field uses.</summary>
        internal bool Decode(ReadOnlySpan<byte> report, ref int dx, ref int dy, ref int wheel, ref int buttons)
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
                if (field.IsVariable) ReadVariable(field, data, ref dx, ref dy, ref wheel, ref buttons);
                else ReadArray(field, data, ref buttons);
            }
            return any;
        }

        private static void ReadVariable(HidKeyboardReports.Field field, ReadOnlySpan<byte> data,
            ref int dx, ref int dy, ref int wheel, ref int buttons)
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
                if (usage >> 16 == ButtonPage || usage == X || usage == Y || usage == Wheel) return true;
            return false;
        }
    }
}
