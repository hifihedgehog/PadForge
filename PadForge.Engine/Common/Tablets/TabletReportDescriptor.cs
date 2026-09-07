using System.Runtime.InteropServices;

namespace PadForge.Engine.Tablets;

internal interface ITabletReportValues
{
    uint ReadValue(TabletReportDescriptor.ValueField field, IntPtr report, int length, out uint value);
    uint ReadButtons(TabletReportDescriptor.ButtonField field, IntPtr report, int length, ushort[] values, ref uint count);
}

internal sealed class TabletReportDescriptor : IDisposable
{
    internal readonly record struct ValueField(byte ReportId, ushort Page, ushort Link, ushort Usage, int Minimum, int Maximum, ushort Bits, bool HasNull, ushort LinkPage = 0, ushort LinkUsage = 0)
    {
        internal bool TryNormalize(uint raw, out float value)
        {
            long number = raw;
            if (Minimum < 0 && Bits is > 0 and < 32 && (raw & (1u << (Bits - 1))) != 0)
                number = (int)(raw | (uint.MaxValue << Bits));
            else if (Minimum < 0 && Bits == 32) number = (int)raw;
            uint mask = Bits is > 0 and < 32 ? (1u << Bits) - 1 : uint.MaxValue;
            long maximum = Minimum >= 0 ? (long)((uint)Maximum & mask) : Maximum;
            if (maximum <= Minimum || (HasNull && (number < Minimum || number > maximum)))
            {
                value = 0;
                return false;
            }
            value = (float)Math.Clamp((double)(number - Minimum) / (maximum - Minimum), 0, 1);
            return true;
        }
    }

    internal readonly record struct ButtonField(byte ReportId, ushort Page, ushort Link, ushort Usage);
    private readonly object gate = new();
    private IntPtr preparsed;
    private readonly ValueField x, y;
    private readonly ValueField? pressure;
    private readonly ButtonField tip;
    private readonly ButtonField? inRange;
    private readonly ButtonField? eraser;
    private readonly ITabletReportValues reportValues;
    private bool disposed;
    private readonly ButtonField[] buttons;
    private readonly ushort[] usageBuffer;
    private float currentX = 0.5f, currentY = 0.5f, currentPressure;
    private bool hasX, hasY, currentTip, currentInRange, currentEraser, wasContact, wasEraser;
    private int contactId = -1, nextContactId;
    private readonly bool[] currentButtons;
    private readonly bool[] nextButtons;

    internal string Path { get; }
    internal string InstanceId { get; }
    internal string Name { get; }
    internal string Serial { get; }
    internal ushort Vendor { get; }
    internal ushort Product { get; }
    internal int ReportLength { get; }
    internal bool HasPressure => pressure.HasValue;
    internal int ButtonCount => buttons.Length;
    internal ButtonField[] Buttons => buttons;

    internal TabletReportDescriptor(string path, string instance, string name, string serial, ushort vendor, ushort product,
        IntPtr data, int reportLength, ValueField xField, ValueField yField, ValueField? pressureField, ButtonField tipField, ButtonField[] buttonFields, int usageCount, ITabletReportValues values = null)
    {
        Path = path; InstanceId = instance; Name = name; Serial = serial; Vendor = vendor; Product = product;
        preparsed = data; ReportLength = reportLength; x = xField; y = yField; pressure = pressureField; tip = tipField;
        buttons = buttonFields; currentButtons = new bool[buttons.Length]; nextButtons = new bool[buttons.Length]; usageBuffer = new ushort[Math.Max(16, usageCount)];
        reportValues = values;
        inRange = buttonFields.Where(f => f.Page == 13 && f.Usage == 0x32).Select(f => (ButtonField?)f).FirstOrDefault();
        eraser = buttonFields.Where(f => f.Page == 13 && f.Usage == 0x45).Select(f => (ButtonField?)f).FirstOrDefault();
        currentInRange = !inRange.HasValue;
    }

    internal static TabletReportDescriptor TryOpen(string path, string instance)
    {
        using var handle = TabletNative.OpenMetadata(path);
        if (handle.IsInvalid || !TabletNative.HidD_GetPreparsedData(handle, out IntPtr data)) return null;
        try
        {
            if (TabletNative.HidP_GetCaps(data, out var caps) != TabletNative.Success || caps.UsagePage != 13 || caps.Usage is not (1 or 2)) return null;
            if (caps.InputLength is < 2 or > 4096 || caps.InputValues > 512 || caps.InputButtons > 512) return null;
            var values = new TabletNative.ValueCap[caps.InputValues];
            var buttonCaps = new TabletNative.ButtonCap[caps.InputButtons];
            ushort valueCount = caps.InputValues, buttonCount = caps.InputButtons;
            if (valueCount == 0 || TabletNative.HidP_GetValueCaps(0, values, ref valueCount, data) != TabletNative.Success) return null;
            if (buttonCount == 0 || TabletNative.HidP_GetButtonCaps(0, buttonCaps, ref buttonCount, data) != TabletNative.Success) return null;
            var fields = new List<ValueField>();
            foreach (var cap in values.Take(valueCount))
            {
                if (cap.IsAlias != 0 || cap.IsAbsolute == 0 || cap.BitSize is 0 or > 32 || cap.IsRange == 0 && cap.ReportCount > 1) continue;
                int end = cap.IsRange != 0 ? cap.UsageMax : cap.UsageMin;
                if (end < cap.UsageMin || end - cap.UsageMin > 128) continue;
                for (int usage = cap.UsageMin; usage <= end; usage++)
                    fields.Add(new(cap.ReportId, cap.Page, cap.Link, (ushort)usage, cap.LogicalMin, cap.LogicalMax, cap.BitSize, cap.HasNull != 0, cap.LinkPage, cap.LinkUsage));
            }
            var buttonFields = new List<ButtonField>();
            foreach (var cap in buttonCaps.Take(buttonCount))
            {
                if (cap.IsAlias != 0) continue;
                int end = cap.IsRange != 0 ? cap.UsageMax : cap.UsageMin;
                if (end < cap.UsageMin || end - cap.UsageMin > 128) continue;
                for (int usage = cap.UsageMin; usage <= end; usage++)
                    buttonFields.Add(new(cap.ReportId, cap.Page, cap.Link, (ushort)usage));
            }
            foreach (var candidate in fields.Where(f => f.Page == 1 && f.Usage == 0x30 && !(f.LinkPage == 13 && f.LinkUsage == 0x22))
                .OrderByDescending(f => f.LinkPage == 13 && f.LinkUsage == 0x20).ThenBy(f => f.Link).ThenBy(f => f.ReportId))
            {
                var ys = fields.Where(f => f.Page == 1 && f.Usage == 0x31 && f.Link == candidate.Link).ToArray();
                var tips = buttonFields.Where(f => f.Page == 13 && f.Usage == 0x42 && f.Link == candidate.Link).ToArray();
                if (ys.Length == 0 || tips.Length == 0) continue;
                if (!candidate.TryNormalize(unchecked((uint)candidate.Minimum), out _) || !ys[0].TryNormalize(unchecked((uint)ys[0].Minimum), out _)) continue;
                var pressures = fields.Where(f => f.Page == 13 && f.Usage == 0x30 && f.Link == candidate.Link).ToArray();
                pressures = pressures.Where(f => f.TryNormalize(unchecked((uint)f.Minimum), out _)).ToArray();
                var exposed = buttonFields.Where(f => f.Link == candidate.Link &&
                    (f.Page == 9 || f.Page == 13 && f.Usage is 0x44 or 0x5A or 0x45 or 0x3C or 0x32)).Distinct()
                    .OrderBy(f => f.Page == 13 ? f.Usage switch { 0x44 => 0, 0x5A => 1, 0x45 => 2, 0x3C => 3, _ => 4 } : 5)
                    .ThenBy(f => f.Usage).Take(64).ToArray();
                var attributes = new TabletNative.Attributes { Size = Marshal.SizeOf<TabletNative.Attributes>() };
                if (!TabletNative.HidD_GetAttributes(handle, ref attributes)) return null;
                string name = TabletNative.ReadString(handle, false);
                if (string.IsNullOrWhiteSpace(name)) name = $"HID {attributes.Vendor:X4}:{attributes.Product:X4}";
                var result = new TabletReportDescriptor(path, instance, name, TabletNative.ReadString(handle, true), attributes.Vendor, attributes.Product,
                    data, caps.InputLength, candidate, ys[0], pressures.Length > 0 ? pressures[0] : null, tips[0], exposed, buttonFields.Count + 1);
                data = IntPtr.Zero;
                return result;
            }
            return null;
        }
        finally { if (data != IntPtr.Zero) TabletNative.HidD_FreePreparsedData(data); }
    }

    internal bool Decode(IntPtr report, int length)
    {
        lock (gate)
        {
            if (disposed || preparsed == IntPtr.Zero && reportValues == null || report == IntPtr.Zero || length != ReportLength) return false;
            byte reportId = Marshal.ReadByte(report);
            bool recognized = false;
            float nx = currentX, ny = currentY, np = currentPressure;
            bool hx = hasX, hy = hasY, nt = currentTip, nr = currentInRange, ne = currentEraser;
            if (!ReadCoordinate(x, reportId, report, length, ref nx, ref hx, ref recognized)
                || !ReadCoordinate(y, reportId, report, length, ref ny, ref hy, ref recognized)) return false;
            if (pressure is { } field && field.ReportId == reportId)
            {
                if (ReadValue(field, report, length, out uint raw) != TabletNative.Success) return false;
                if (!field.TryNormalize(raw, out np)) np = 0;
                recognized = true;
            }
            if (!ReadSwitch(tip, reportId, report, length, ref nt, ref recognized)) return false;
            if (inRange is { } range && range.ReportId == reportId)
                if (!ReadSwitch(range, reportId, report, length, ref nr, ref recognized)) return false;
            if (eraser is { } erase && erase.ReportId == reportId)
                if (!ReadSwitch(erase, reportId, report, length, ref ne, ref recognized)) return false;
            Array.Copy(currentButtons, nextButtons, currentButtons.Length);
            for (int i = 0; i < buttons.Length; i++)
                if (!ReadSwitch(buttons[i], reportId, report, length, ref nextButtons[i], ref recognized)) return false;
            if (!recognized) return false;
            currentX = nx; currentY = ny; currentPressure = np;
            hasX = hx; hasY = hy; currentTip = nt; currentInRange = nr; currentEraser = ne;
            Array.Copy(nextButtons, currentButtons, currentButtons.Length);
            bool contact = (currentTip || currentEraser) && currentInRange && hasX && hasY;
            if (wasContact && !contact) currentPressure = 0;
            if (contact && (!wasContact || currentEraser != wasEraser)) contactId = nextContactId++ & int.MaxValue;
            else if (!contact) contactId = -1;
            wasContact = contact;
            wasEraser = currentEraser;
            return recognized;
        }
    }

    private uint ReadValue(ValueField field, IntPtr report, int length, out uint raw)
        => reportValues != null ? reportValues.ReadValue(field, report, length, out raw)
            : TabletNative.HidP_GetUsageValue(0, field.Page, field.Link, field.Usage, out raw, preparsed, report, (uint)length);

    private bool ReadCoordinate(ValueField field, byte id, IntPtr report, int length, ref float value, ref bool hasValue, ref bool recognized)
    {
        if (field.ReportId != id) return true;
        if (ReadValue(field, report, length, out uint raw) != TabletNative.Success) return false;
        hasValue = field.TryNormalize(raw, out float next);
        if (hasValue) value = next;
        recognized = true;
        return true;
    }

    private bool ReadSwitch(ButtonField field, byte reportId, IntPtr report, int length, ref bool value, ref bool recognized)
    {
        if (field.ReportId != reportId) return true;
        uint count = (uint)usageBuffer.Length;
        uint status = reportValues != null ? reportValues.ReadButtons(field, report, length, usageBuffer, ref count)
            : TabletNative.HidP_GetUsages(0, field.Page, field.Link, usageBuffer, ref count, preparsed, report, (uint)length);
        if (status != TabletNative.Success || count > usageBuffer.Length) return false;
        value = false;
        for (int i = 0; i < count; i++) if (usageBuffer[i] == field.Usage) { value = true; break; }
        recognized = true;
        return true;
    }

    internal void CopyInto(CustomInputState state)
    {
        lock (gate)
        {
            if (state.Touchpads is not { Length: 1 } || state.Touchpads[0]?.MaxFingers != 1)
                state.Touchpads = new[] { new TouchpadInputState(1) };
            var contact = state.Touchpads[0];
            contact.FingerX[0] = currentX;
            contact.FingerY[0] = currentY;
            contact.FingerPressure[0] = wasContact && HasPressure ? currentPressure : 0;
            contact.FingerDown[0] = wasContact;
            contact.FingerContactId[0] = contactId;
            contact.Clicked = false;
            Array.Copy(currentButtons, state.Buttons, Math.Min(currentButtons.Length, state.Buttons.Length));
        }
    }

    internal void Reset()
    {
        lock (gate)
        {
            currentX = currentY = 0.5f;
            currentPressure = 0;
            hasX = hasY = currentTip = currentEraser = wasContact = wasEraser = false;
            currentInRange = !inRange.HasValue;
            contactId = -1;
            Array.Clear(currentButtons);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (preparsed != IntPtr.Zero) TabletNative.HidD_FreePreparsedData(preparsed);
            preparsed = IntPtr.Zero;
            Reset();
        }
    }
}
