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
                gg = File.Exists(GameSenseClient.DefaultCorePropsPath)
                     || Directory.Exists(Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteelSeries", "GG"));
            }
            catch { }

            return new PeripheralPresence(ledSdk, gHub, synapse, gg, PlatformSupport.SensaAvailable);
        }
    }

    /// <summary>One device row as the linker sees it: an online mouse,
    /// keyboard or analog keyboard of a supported vendor with its container
    /// ID, or a vendor row.</summary>
    internal readonly record struct LinkRow(Guid Device, int CapType, ushort VendorId, ushort ProductId,
        Guid Container, PeripheralRowKind? VendorRow);

    /// <summary>
    /// Matches device rows to output paths (#494). Pure, so every rule is
    /// testable without a device.
    ///
    /// <para>Lighting: a Logitech device lit through HID++ feature 0x8070
    /// takes its own unit while G HUB is not running, and its LED SDK device
    /// type while G HUB runs, since G HUB then owns the device. A Razer row
    /// takes its Chroma category while Synapse is installed, a SteelSeries
    /// row its GameSense device type while GG is, each named by the device's
    /// product ID where <see cref="PeripheralProductIds"/> lists it, so every
    /// row of one device lights as that device. The vendor rows take every
    /// path of their family, which a device claiming the path on its own
    /// outranks (<see cref="PeripheralOutputs.Rules"/>).</para>
    ///
    /// <para>A mouse or keyboard row is one Raw Input interface. Its vendor
    /// channel is another collection of the same physical device, and the
    /// two share a container ID: on the bench's LIGHTSPEED receiver the mouse
    /// (MI_00), keyboard and consumer (MI_01) and HID++ (MI_02) collections
    /// all report one. A receiver gives one mouse row and one keyboard row
    /// however many devices are paired to it, so a row there takes the units
    /// of its own kind, from feature 0x0005's device type. A wired or
    /// Bluetooth device is the only unit in its container, so every row of
    /// it takes it, as every row of a Razer or SteelSeries device lights that
    /// device. A unit that gave no type goes to every row in its
    /// container.</para>
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

        /// <summary>The Chroma REST categories (Colore Rest/RestApi.cs, the
        /// official REST docs), in the order the init lists them.</summary>
        internal static readonly string[] ChromaCategories =
            { "keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink" };

        /// <summary>The LED SDK path for whole devices: the Logitech devices
        /// with no zones, the manual's G710+, G600, G510, G110, G19, G105,
        /// G300, G11, G13 and G15 (LogitechGamingLEDSDK.pdf pp. 9-18), lit
        /// with one color on the RGB and monochrome targets. Only the
        /// LIGHTSYNC row takes it, as the lightbar mirror lit them.</summary>
        internal const string LedSdkWholeDevices = "device";

        /// <summary>The LED SDK paths in paint order: the whole devices
        /// first, since that call reaches zonal devices too, then the device
        /// types a zone call addresses (the 9.00 header's LogiLed::DeviceType,
        /// logitech-led-sdk-rs bindings-x86_64.rs:136-142).</summary>
        internal static readonly string[] LedSdkTypes = { LedSdkWholeDevices, "keyboard", "mouse", "mousemat", "headset", "speaker" };

        /// <summary>The GameSense general device types a color reaches
        /// (gamesense-sdk standard-zones.md:5-17). The fourth, indicator, is
        /// a single status light such as the Sims 4 Plumbob, and stays
        /// out.</summary>
        internal static readonly string[] GameSenseColorTypes = { "mouse", "keyboard", "headset" };

        /// <summary>Razer keypads, which Windows lists as keyboards and Chroma
        /// lights as its keypad category: the Nostromo, the Tartarus family
        /// and the Orbweaver family (openrazer keyboards.py:74-273). Any row
        /// of one, its mouse collection included, lights the keypad.</summary>
        internal static readonly ushort[] RazerKeypadPids = { 0x0111, 0x0201, 0x0208, 0x022B, 0x0244, 0x0113, 0x0207 };

        public static LinkTable Build(IEnumerable<LinkRow> rows, HidppSnapshot hidpp, PeripheralPresence presence)
        {
            var result = new List<DeviceLinks>();
            foreach (var row in rows)
            {
                var haptics = new List<OutputPath>();
                var lighting = new List<OutputPath>();
                var units = new List<string>();
                if (row.VendorRow is PeripheralRowKind kind)
                {
                    switch (kind)
                    {
                        case PeripheralRowKind.RazerSensa when presence.SensaPlatform:
                            haptics.Add(SensaPath);
                            break;
                        case PeripheralRowKind.RazerChroma when presence.RazerSynapse:
                            foreach (var category in ChromaCategories)
                                lighting.Add(new OutputPath(OutputFamily.ChromaCategory, category));
                            break;
                        case PeripheralRowKind.LogitechLightsync when presence.LogitechLedSdk:
                            foreach (var type in LedSdkTypes)
                                lighting.Add(new OutputPath(OutputFamily.LedSdkType, type));
                            break;
                        case PeripheralRowKind.SteelSeriesGG when presence.SteelSeriesGG:
                            foreach (var type in GameSenseColorTypes)
                                lighting.Add(new OutputPath(OutputFamily.GameSenseColor, type));
                            break;
                    }
                }
                else if (row.VendorId == LogitechVid && IsLinkable(row.CapType))
                {
                    foreach (var unit in hidpp.Units)
                    {
                        if (!Matches(unit, row)) continue;
                        units.Add(unit.Key);
                        if (unit.HasHaptics)
                            haptics.Add(new OutputPath(OutputFamily.HidppUnit, unit.Key));
                        if (!unit.HasRgb) continue;
                        if (presence.LogitechGHubRunning)
                        {
                            // The unit's own kind where it gave one, so every
                            // row of a device lights that device's type.
                            if (presence.LogitechLedSdk)
                                AddOnce(lighting, new OutputPath(OutputFamily.LedSdkType,
                                    LedSdkTypeOf(unit, row)));
                        }
                        else if (unit.DirectRgb)
                        {
                            lighting.Add(new OutputPath(OutputFamily.HidppUnit, unit.Key));
                        }
                    }
                }
                else if (row.VendorId == RazerVid && IsLinkable(row.CapType))
                {
                    if (presence.RazerSynapse)
                        lighting.Add(new OutputPath(OutputFamily.ChromaCategory, RazerCategory(row)));
                }
                else if (row.VendorId == SteelSeriesVid && IsLinkable(row.CapType))
                {
                    if (presence.SteelSeriesGG)
                    {
                        bool keyboard = PeripheralProductIds.LightsAsKeyboard(row.ProductId, row.CapType,
                            PeripheralProductIds.SteelSeriesMice, PeripheralProductIds.SteelSeriesKeyboards);
                        lighting.Add(new OutputPath(OutputFamily.GameSenseColor, keyboard ? "keyboard" : "mouse"));
                        // Every row of a tactile Rival is that mouse, so each
                        // one takes its rumble.
                        if (!keyboard && Array.IndexOf(RivalTactilePids, row.ProductId) >= 0)
                            haptics.Add(GameSenseTactilePath);
                    }
                }

                if (haptics.Count == 0 && lighting.Count == 0) continue;
                result.Add(new DeviceLinks
                {
                    Device = row.Device,
                    Haptics = haptics.ToArray(),
                    Lighting = lighting.ToArray(),
                    HidppUnits = units.ToArray(),
                    CatchAll = row.VendorRow != null,
                });
            }
            return new LinkTable(result);
        }

        /// <summary>The Chroma category a Razer row lights: the keypad
        /// category for a keypad, else the device's kind by product ID, else
        /// the row's own kind.</summary>
        internal static string RazerCategory(LinkRow row)
        {
            if (Array.IndexOf(RazerKeypadPids, row.ProductId) >= 0) return "keypad";
            // The Huntsman V3 boards openrazer does not list yet, which the
            // analog keyboard reader already knows (AnalogKeyboardCatalog).
            if (PadForge.Engine.Common.AnalogKeyboard.AnalogKeyboardCatalog.IsRazerHuntsmanV3(row.ProductId))
                return "keyboard";
            return PeripheralProductIds.LightsAsKeyboard(row.ProductId, row.CapType,
                PeripheralProductIds.RazerMice, PeripheralProductIds.RazerKeyboards) ? "keyboard" : "mouse";
        }

        /// <summary>The LED SDK type a Logitech unit lights as: its own kind
        /// where it gave one, else its row's.</summary>
        internal static string LedSdkTypeOf(HidppUnit unit, LinkRow row)
        {
            if (unit.IsKeyboardKind) return "keyboard";
            if (unit.IsMouseKind) return "mouse";
            return IsKeyboard(row.CapType) ? "keyboard" : "mouse";
        }

        private static void AddOnce(List<OutputPath> paths, OutputPath path)
        {
            if (!paths.Contains(path)) paths.Add(path);
        }

        /// <summary>The device rows the linker reads: a mouse, a keyboard, and
        /// an analog keyboard's vendor interface (#468), which is a keyboard
        /// too.</summary>
        internal static bool IsLinkable(int capType)
            => capType == InputDeviceType.Mouse || IsKeyboard(capType);

        internal static bool IsKeyboard(int capType)
            => capType == InputDeviceType.Keyboard || capType == InputDeviceType.AnalogKeyboard;

        /// <summary>A unit belongs to a row in its container whose kind it
        /// shares, or to every row there when it gave no kind. A direct unit,
        /// wired or Bluetooth, is the device itself and alone in its
        /// container, so it belongs to every row of it that is a mouse or
        /// keyboard, while a remote or presenter there stays out.</summary>
        internal static bool Matches(HidppUnit unit, LinkRow row)
        {
            if (row.Container == Guid.Empty || unit.ContainerId != row.Container) return false;
            if (unit.DeviceType < 0) return true;
            if (unit.DeviceIndex == PadForge.Common.Input.HidppHapticProtocol.DirectDeviceIndex)
                return unit.IsKeyboardKind || unit.IsMouseKind;
            return IsKeyboard(row.CapType) ? unit.IsKeyboardKind : unit.IsMouseKind;
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
