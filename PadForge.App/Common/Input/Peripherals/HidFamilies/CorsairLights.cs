using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>A key map of OpenRGB's: <see cref="Rows"/> rows of
    /// <see cref="Cols"/> cells, row by row, each cell a light's index or
    /// -1 where OpenRGB writes NA.</summary>
    internal sealed record CorsairMatrix(int Rows, int Cols, int[] Cells)
    {
        /// <summary>The row and column of the first cell that holds
        /// <paramref name="index"/>, or false.</summary>
        public bool TryFind(int index, out int row, out int col)
        {
            for (int i = 0; i < Cells.Length; i++)
            {
                if (Cells[i] != index) continue;
                row = i / Cols;
                col = i % Cols;
                return true;
            }
            row = col = 0;
            return false;
        }
    }

    /// <summary>OpenRGB's zone types (RGBController.h zone_type).</summary>
    internal enum CorsairZoneType
    {
        Single,
        Linear,
        Matrix,
    }

    /// <summary>The OpenRGB device types the Corsair tables use.</summary>
    internal enum CorsairDeviceType
    {
        Keyboard,
        Mouse,
        Mousemat,
        HeadsetStand,
    }

    /// <summary>One corsair_v2_zone (CorsairPeripheralV2Devices.h:41-47).</summary>
    internal sealed record CorsairV2Zone(string Name, CorsairZoneType Type, int Rows, int Cols);

    /// <summary>One corsair_v2_device (CorsairPeripheralV2Devices.h:58-66):
    /// its product ID, type, size, zones in order and the name of its key
    /// layout in KeyboardLayouts, null for none.</summary>
    internal sealed record CorsairV2Device(string Name, ushort ProductId, CorsairDeviceType Type, int Rows, int Cols,
        CorsairV2Zone[] Zones, string Layout)
    {
        /// <summary>The lights of a device OpenRGB has no table for, from
        /// SignalRGB's plugin, in place of <see cref="Zones"/>.</summary>
        public IReadOnlyList<LightInfo> OwnLights { get; init; }

        /// <summary>The colors of a block of a plugin's device with
        /// <see cref="OwnLights"/>, where its spacing runs past its lights. 0: one
        /// past its highest light.</summary>
        public int OwnBuffer { get; init; }

        /// <summary>The interface its plugin writes to.</summary>
        public int OwnInterface { get; init; } = 1;

        /// <summary>A keyboard with red LEDs only, which opens its own lighting resource
        /// and takes one byte per LED (ckb-next device_bragi.c:83-86, led_bragi.c:94-99).</summary>
        public bool Monochrome { get; init; }
    }

    /// <summary>
    /// The lights of the Corsair families (#557), from OpenRGB's tables
    /// (<c>CorsairLights.g.cs</c>, written by tools/gen_corsair_lights.py
    /// at the pinned commit), and what the two families share: the kind of
    /// a light by OpenRGB's name for it, and the record of a device left in
    /// software mode.
    /// </summary>
    internal static partial class CorsairLights
    {
        internal const ushort VendorId = 0x1B1C;

        /// <summary>The product key a model's map and its backup record are
        /// stored under.</summary>
        internal static string Product(ushort productId) => $"corsair:{VendorId:X4}:{productId:X4}";

        /// <summary>
        /// The light at <paramref name="address"/> that OpenRGB calls
        /// <paramref name="name"/>. A name <see cref="OpenRgbKeys"/> gives a
        /// usage is a key, and one it gives a label is a light with that
        /// label, as <see cref="KeyboardLayoutManager.Lights"/> makes them.
        /// The Corsair tables' own names: "Key: Macro G1" to G18 are the
        /// G-keys (RGBController_CorsairPeripheral.cpp:661-666 and 804-821),
        /// "Light" is the brightness key, the light ckb-next numbers 0x89
        /// and calls "light" (ckb-next src/daemon/keymap.c, keymap[]) and
        /// OpenRGB's V1 tables call KEY_EN_BRIGHTNESS at the same number
        /// (CorsairPeripheralController.cpp:26 and
        /// RGBController_CorsairPeripheral.cpp:288). A name with "Logo" in
        /// it is a logo light. "Light Bar", "Top Bar" and "Underglow" lights
        /// are edge lights. A name with "Indicator" in it, or one that
        /// starts "DPI", is a status light. Any other light takes
        /// <paramref name="fallback"/>.
        /// </summary>
        internal static LightInfo Light(int address, string name, LightKind fallback)
        {
            if (OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number))
            {
                return usage != 0
                    ? new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated)
                    : new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
            }
            name ??= string.Empty;
            const string macro = "Key: Macro G";
            if (name.StartsWith(macro, StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(macro.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int g) && g > 0)
                return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.GKey, LabelNumber = g };
            if (name == "Light")
                return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.Brightness };
            if (name.Contains("Logo", StringComparison.Ordinal))
                return new LightInfo(address, LightKind.Logo, 0, LightKeySource.None);
            if (name.StartsWith("Light Bar", StringComparison.Ordinal) || name.StartsWith("Top Bar", StringComparison.Ordinal)
                || name.StartsWith("Underglow", StringComparison.Ordinal))
                return new LightInfo(address, LightKind.Edge, 0, LightKeySource.None);
            if (name.Contains("Indicator", StringComparison.Ordinal) || name.StartsWith("DPI", StringComparison.Ordinal))
                return new LightInfo(address, LightKind.Status, 0, LightKeySource.None);
            return new LightInfo(address, fallback, 0, LightKeySource.None);
        }

        /// <summary>Numbers the zone lights of <paramref name="lights"/>
        /// from 1 in their order: each is a zone of its own, as each light
        /// of a Lenovo zone table is (<see cref="LenovoLights.Build"/>).</summary>
        internal static List<LightInfo> NumberZones(List<LightInfo> lights)
        {
            int zone = 0;
            for (int i = 0; i < lights.Count; i++)
                if (lights[i].Kind == LightKind.Zone)
                    lights[i] = lights[i] with { Zone = ++zone };
            return lights;
        }

        /// <summary>A light placed at its matrix cell, in grid cells, as
        /// <see cref="KeyboardLayoutManager.Lights"/> places a key.</summary>
        internal static LightInfo Placed(LightInfo light, CorsairMatrix matrix, int index)
            => matrix != null && matrix.TryFind(index, out int row, out int col)
                ? light with { X = col, Y = row, Width = 1, Height = 1, HasPosition = true }
                : light;

        /// <summary>A 65-byte report, report ID 0 and the bytes given from
        /// byte 1, as every Corsair frame here is
        /// (CORSAIR_PERIPHERAL_PACKET_LENGTH and CORSAIR_V2_WRITE_SIZE).</summary>
        internal static byte[] Frame(params byte[] bytes)
        {
            var frame = new byte[PacketLength];
            Array.Copy(bytes, 0, frame, 1, Math.Min(bytes.Length, PacketLength - 1));
            return frame;
        }

        /// <summary>CORSAIR_PERIPHERAL_PACKET_LENGTH
        /// (CorsairPeripheralController.h:18) and CORSAIR_V2_WRITE_SIZE
        /// (CorsairPeripheralV2Controller.h:29), the report ID included.</summary>
        internal const int PacketLength = 65;
    }

    /// <summary>
    /// What a session of either Corsair family shares (#557). Every
    /// Corsair device here is put in a software mode when a session
    /// starts, and stays in it until it is handed back or re-enumerates:
    /// OpenRGB's V2 controller hands the device back in its destructor
    /// because "the mode persists until the next USB enumeration if it is
    /// never reverted" (CorsairPeripheralV2Controller.cpp:142-150). So a
    /// session records the device as owed in <see cref="KeyboardProfileBackups"/>
    /// before its first picture, as the Lenovo Y740 to Y760 session does
    /// (<see cref="LenovoLegionUsbLighting"/>), with the hand-back frame as
    /// its contents and the number of times it goes out as its profile, and
    /// the next run sends it for a device a forced kill left. Where the
    /// reference keeps a device from falling back to its own lighting by
    /// sending the last picture again after a time without one, the model
    /// names that time and the worker sends it
    /// (<see cref="HidLightingModel.KeepAliveMs"/>).
    /// </summary>
    internal abstract class CorsairSession : HidLightingSession
    {
        /// <summary>How often a picture that waits for the record to reach
        /// the settings file looks again, the Lenovo session's interval
        /// (<see cref="LenovoLegionUsbLighting.SavingMs"/>).</summary>
        internal const int SavingMs = LenovoLegionUsbLighting.SavingMs;

        private readonly string _product;
        private readonly byte[] _handBack;
        private readonly int _handBackCount;
        private readonly int _handBackGapMs;
        private readonly byte[][] _crash;

        protected CorsairSession(string product, byte[] handBack, int handBackCount, int handBackGapMs)
        {
            _product = product;
            _handBack = handBack;
            _handBackCount = Math.Max(1, handBackCount);
            _handBackGapMs = handBackGapMs;
            _crash = new byte[_handBackCount][];
            for (int i = 0; i < _crash.Length; i++) _crash[i] = handBack;
        }

        public override bool Feature => false;

        /// <summary>The hand-back, from the session's start, since the
        /// software mode is on from then.</summary>
        public override byte[][] CrashFrames => _crash;

        /// <summary>Sends one picture's frames. False when a write
        /// failed.</summary>
        protected abstract bool Send(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
            IReadOnlyList<int> colors);

        public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
            IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            if (KeyboardProfileBackups.Get(_product) == null)
                KeyboardProfileBackups.Take(_product, (byte)_handBackCount, _handBack);
            if (!KeyboardProfileBackups.IsPersisted(_product))
            {
                LaterMs = SavingMs;
                return HidLightingPaint.Later;
            }
            return Send(io, wait, lights, colors) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
        }

        public override bool HandBack(IHidReportChannel io, Action<int> wait)
        {
            if (!SendHandBack(io, wait)) return false;
            KeyboardProfileBackups.Drop(_product);
            return true;
        }

        /// <summary>The hand-back frame, as many times as the session's
        /// device takes it, with the gap between.</summary>
        protected virtual bool SendHandBack(IHidReportChannel io, Action<int> wait)
        {
            for (int i = 0; i < _handBackCount; i++)
            {
                if (i > 0 && _handBackGapMs > 0) wait(_handBackGapMs);
                if (!io.WriteOutput(_handBack)) return false;
            }
            return true;
        }

        /// <summary>Hands back a device an earlier run left in software
        /// mode, from its record. True when nothing is owed any more.</summary>
        internal static bool Repair(IHidReportChannel io, Action<int> wait, string product, int gapMs)
        {
            var backup = KeyboardProfileBackups.Get(product);
            if (backup == null) return true;
            int count = Math.Max(1, (int)backup.Value.Profile);
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && gapMs > 0) wait(gapMs);
                if (!io.WriteOutput(backup.Value.Contents))
                {
                    KeyboardProfileBackups.NoteFailure(product);
                    return false;
                }
            }
            KeyboardProfileBackups.Drop(product);
            return true;
        }

        /// <summary>How many times one run tries to hand back a device an
        /// earlier run left, the Lenovo session's bound
        /// (<see cref="LenovoLegionUsbLighting.RepairTries"/>).</summary>
        internal const int RepairTries = LenovoLegionUsbLighting.RepairTries;

        /// <summary>Whether a device of <paramref name="product"/> is owed a
        /// hand-back this run can still try.</summary>
        internal static bool Owed(string product)
            => KeyboardProfileBackups.Get(product) != null && KeyboardProfileBackups.Failures(product) < RepairTries;
    }
}
