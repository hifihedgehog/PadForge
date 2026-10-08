using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>Which vendor software is installed or running, read cheaply
    /// (registry, files, process names) and never by opening an SDK, so an
    /// unassigned device costs nothing (#494).</summary>
    internal sealed record PeripheralPresence(
        bool LogitechLedSdk, bool LogitechGHubRunning, bool RazerSynapse, bool SteelSeriesGG, bool SensaPlatform)
    {
        public static readonly PeripheralPresence None = new(false, false, false, false, false);

        /// <summary>The Chroma SDK runtime Synapse installs, which Colore's
        /// native layer loads by this name.</summary>
        internal const string ChromaRuntimeDll = "RzChromaSDK64.dll";
        internal const string ChromaSdkKey = @"SOFTWARE\Razer Chroma SDK";

        public static PeripheralPresence Read()
        {
            bool ledSdk = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(LogiLedEngineNative.ServerBinaryKey);
                string path = key?.GetValue(null)?.ToString();
                ledSdk = !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            catch { }

            bool gHub = false;
            try { gHub = new LogiLedEngineNative().SoftwarePresent(); }
            catch { }

            bool synapse = false;
            try
            {
                synapse = File.Exists(Path.Combine(Environment.SystemDirectory, ChromaRuntimeDll));
                if (!synapse)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(ChromaSdkKey);
                    synapse = key != null;
                }
            }
            catch { }

            bool gg = false;
            try
            {
                gg = File.Exists(GameSenseTactile.DefaultCorePropsPath)
                     || Directory.Exists(Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteelSeries", "GG"));
            }
            catch { }

            return new PeripheralPresence(ledSdk, gHub, synapse, gg, PlatformSupport.SensaAvailable);
        }
    }

    /// <summary>One device row as the linker sees it: an online mouse or
    /// keyboard of a supported vendor with its container ID, or a vendor
    /// row.</summary>
    internal readonly record struct LinkRow(Guid Device, int CapType, ushort VendorId, ushort ProductId,
        Guid Container, PeripheralRowKind? VendorRow);

    /// <summary>
    /// Matches device rows to output paths (#494). Pure, so every rule is
    /// testable without a device.
    ///
    /// <para>A mouse or keyboard row is one Raw Input interface. Its vendor
    /// channel is another collection of the same physical device, and the
    /// two share a container ID: on the bench's LIGHTSPEED receiver the mouse
    /// (MI_00), keyboard and consumer (MI_01) and HID++ (MI_02) collections
    /// all report one. A receiver gives one mouse row and one keyboard row
    /// however many devices are paired to it, so a row takes the units of its
    /// own kind, from feature 0x0005's device type. A unit that gave no type
    /// goes to every row in its container.</para>
    /// </summary>
    internal static class PeripheralLinker
    {
        internal const ushort LogitechVid = 0x046D;
        internal const ushort RazerVid = 0x1532;
        internal const ushort SteelSeriesVid = 0x1038;

        /// <summary>The tactile mice GameSense drives: the Rival 500, 700 and
        /// 710 (gamesense-sdk standard-zones.md:49-52), by the product IDs
        /// rivalcfg lists (rival500.py:22, rival700.py:21 and 27).</summary>
        internal static readonly ushort[] RivalTactilePids = { 0x170E, 0x1700, 0x1730 };

        internal static readonly OutputPath GameSenseTactilePath = new(OutputFamily.GameSenseTactile, "tactile");
        internal static readonly OutputPath SensaPath = new(OutputFamily.Interhaptics, "sensa");

        public static LinkTable Build(IEnumerable<LinkRow> rows, HidppSnapshot hidpp, PeripheralPresence presence)
        {
            var result = new List<DeviceLinks>();
            foreach (var row in rows)
            {
                var haptics = new List<OutputPath>();
                if (row.VendorRow is PeripheralRowKind kind)
                {
                    if (kind == PeripheralRowKind.RazerSensa && presence.SensaPlatform)
                        haptics.Add(SensaPath);
                }
                else if (row.VendorId == LogitechVid && IsMouseOrKeyboard(row.CapType))
                {
                    foreach (var unit in hidpp.Units)
                    {
                        if (!unit.HasHaptics || !Matches(unit, row)) continue;
                        haptics.Add(new OutputPath(OutputFamily.HidppUnit, unit.Key));
                    }
                }
                else if (row.VendorId == SteelSeriesVid && row.CapType == InputDeviceType.Mouse
                         && presence.SteelSeriesGG && Array.IndexOf(RivalTactilePids, row.ProductId) >= 0)
                {
                    haptics.Add(GameSenseTactilePath);
                }

                if (haptics.Count == 0) continue;
                result.Add(new DeviceLinks
                {
                    Device = row.Device,
                    Haptics = haptics.ToArray(),
                    CatchAll = row.VendorRow != null,
                });
            }
            return new LinkTable(result);
        }

        internal static bool IsMouseOrKeyboard(int capType)
            => capType == InputDeviceType.Mouse || capType == InputDeviceType.Keyboard;

        /// <summary>A unit belongs to a row in its container whose kind it
        /// shares, or to every row there when it gave no kind.</summary>
        internal static bool Matches(HidppUnit unit, LinkRow row)
        {
            if (row.Container == Guid.Empty || unit.ContainerId != row.Container) return false;
            if (unit.DeviceType < 0) return true;
            return row.CapType == InputDeviceType.Keyboard ? unit.IsKeyboardKind : unit.IsMouseKind;
        }

        /// <summary>The outputs a row keeps on record. What the links found,
        /// plus what it had while its container still has a slot that has not
        /// answered or the HID++ worker has not scanned yet, so a device
        /// asleep at launch keeps its tabs.</summary>
        internal static int Capabilities(int previous, DeviceLinks links, bool stillLooking)
        {
            int found = links == null ? 0 : (int)links.Kinds;
            return stillLooking ? found | previous : found;
        }
    }
}
