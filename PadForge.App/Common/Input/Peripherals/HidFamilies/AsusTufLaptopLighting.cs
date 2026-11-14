using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The keyboard of an ASUS TUF Gaming laptop, lit through the ATK ACPI
    /// device as OpenRGB's AsusTUFLaptopController lights it on Windows
    /// (#557). One color for the whole keyboard.
    ///
    /// <para>The look starts from OpenRGB's detector: the PC's product name holds
    /// "TUF Gaming" (AsusTUFLaptopDetect_Windows.cpp:26-33) and the ASUS WMI
    /// interface is present as ACPI\PNP0C14\ATK
    /// (AsusTUFLaptopController_Windows.cpp:63 and 97). Neither changes,
    /// so the answer is kept for the life of the process.</para>
    ///
    /// <para>The color goes out as OpenRGB's Static mode sends it: DEVS on
    /// the RGB mode device, with the magic that does not store the setting
    /// (setMode, AsusTUFLaptopController_Windows.cpp:255-298). G-Helper
    /// sends the same six bytes (AsusACPI.cs:938-959, its files as of 991145fc), and where that device
    /// does not take them, sends them to its second one, TUF_KB2 (lines
    /// 950-957), which OpenRGB does not know. PadForge does the same with the
    /// magic that does not store.</para>
    ///
    /// <para>G-Helper's look is wider than OpenRGB's. It lights "TUF", "TX Gaming"
    /// and "TX Air" this way, and Vivobook, Zenbook, ProArt, ExpertBook, V16 and
    /// "ASUSLaptop" as well (isACPI, Aura.cs:108, AppConfig.cs:418-421 and
    /// 438-441). Most of that second list have one white backlight, so one of
    /// them is listed only when DSTS says an RGB mode device is there
    /// (DeviceGet and IsSupported, AsusACPI.cs:490-498 and 797-803).</para>
    ///
    /// <para>Hand-back. Nothing goes out. Each color PadForge sends uses
    /// the magic that leaves the keyboard's stored setting as it was
    /// (ASUS_WMI_KEYBOARD_MAGIC_USE, AsusTUFLaptopController_Windows.h:36,
    /// and the comment at AsusTUFLaptopController_Windows.cpp:282-285), and
    /// no reference reads that setting back to send it again: OpenRGB has
    /// no read of the mode, the Linux driver's mode "can only be set, not
    /// read back" (asusctl rog-platform/src/keyboard_led.rs:22), and
    /// G-Helper sends its own saved setting. The
    /// keyboard shows the last color PadForge sent until Armoury Crate,
    /// G-Helper or a restart sets it again.</para>
    /// </summary>
    internal sealed class AsusTufLaptopLighting : IOwnLookFamily
    {
        public static readonly AsusTufLaptopLighting Instance = new();

        /// <summary>DEVS, the set method (AsusTUFLaptopController_Windows.cpp:199,
        /// AsusACPI.cs:36).</summary>
        internal const uint Devs = 0x53564544;

        /// <summary>ASUS_WMI_DEVID_TUF_RGB_MODE (AsusTUFLaptopController_Windows.h:15).</summary>
        internal const uint RgbModeDevice = 0x00100056;

        /// <summary>TUF_KB2 (AsusACPI.cs:142).</summary>
        internal const uint RgbModeDevice2 = 0x0010005A;

        /// <summary>DSTS, the read method (AsusACPI.cs:35).</summary>
        internal const uint Dsts = 0x53545344;

        /// <summary>IsTUF (AppConfig.cs:418-421). No check of the device, as with
        /// OpenRGB's look.</summary>
        internal static readonly string[] TufMarkers = { "TUF", "TX Gaming", "TX Air" };

        /// <summary>IsVivoZenPro (AppConfig.cs:438-441).</summary>
        internal static readonly string[] OtherMarkers = { "Vivobook", "Zenbook", "ProArt", "EXPERTBOOK", " V16", "ASUSLaptop" };

        /// <summary>ASUS_WMI_KEYBOARD_MAGIC_USE (AsusTUFLaptopController_Windows.h:36).</summary>
        internal const byte MagicUse = 0xB3;

        /// <summary>ASUS_WMI_KEYBOARD_MODE_STATIC (AsusTUFLaptopController_Windows.h:28).</summary>
        internal const byte ModeStatic = 0x00;

        /// <summary>The speed byte of OpenRGB's Static mode: the mode has no
        /// speed, so ControllerSetMode passes ASUS_WMI_KEYBOARD_SPEED_NORMAL
        /// (RGBController_AsusTUFLaptop_Windows.cpp:130), which setMode's
        /// switch sends as it is (AsusTUFLaptopController_Windows.cpp:268-280,
        /// AsusTUFLaptopController_Windows.h:22). G-Helper sends 0xEB for a
        /// static color too (Aura.cs:1214).</summary>
        internal const byte SpeedNormal = 0xEB;

        /// <summary>The unit's key, the device OpenRGB opens.</summary>
        internal const string Key = OwnLightingChannels.AtkAcpiPath;

        /// <summary>The shortest time between two colors. A color is not
        /// stored, and no reference bounds the rate, so the bound is
        /// PadForge's.</summary>
        internal const int RepaintMs = 50;

        private readonly Func<string> _productName;
        private readonly Func<string, bool> _instancePresent;
        private readonly Func<IAtkAcpiChannel> _open;
        private OwnDevice[] _found;

        public AsusTufLaptopLighting()
            : this(null, null, null) { }

        /// <summary>Test seam: the product name read, the device instance
        /// check and the opener.</summary>
        internal AsusTufLaptopLighting(Func<string> productName, Func<string, bool> instancePresent,
            Func<IAtkAcpiChannel> open)
        {
            _productName = productName ?? OwnLightingChannels.ProductName;
            _instancePresent = instancePresent ?? OwnLightingChannels.InstancePresent;
            _open = open ?? OwnLightingChannels.OpenAtkAcpi;
            Model = new HidLightingModel
            {
                Family = this,
                Name = "ASUS TUF Laptop Keyboard",
                Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) },
                    "OpenRGB AsusTUFLaptopController")
                {
                    Product = "asus:tuf-laptop",
                },
                Programs = new[] { VendorPrograms.ArmouryCrate },
                RepaintMs = RepaintMs,
                Open = _ => _open(),
                BeginOwn = (channel, _) => channel is IAtkAcpiChannel atk ? new Session(atk) : null,
            };
        }

        public string Name => "ASUS TUF Laptop";

        /// <summary>The one model: OpenRGB's single zone with its single
        /// light (RGBController_AsusTUFLaptop_Windows.cpp:93-113).</summary>
        internal HidLightingModel Model { get; }

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        public IReadOnlyList<OwnDevice> Look()
        {
            var found = Volatile.Read(ref _found);
            if (found != null) return found;
            string product = _productName();
            // A query that gave no answer is asked again at the next look.
            if (product == null) return null;
            found = _instancePresent(OwnLightingChannels.AtkWmiInstance) && Looks(product)
                ? new[]
                {
                    new OwnDevice(Key, Model)
                    {
                        Container = PeripheralLinker.LocalMachineContainer, BuiltInKeyboard = true,
                    },
                }
                : Array.Empty<OwnDevice>();
            Volatile.Write(ref _found, found);
            return found;
        }

        /// <summary>Whether a PC of this name is lit through the ATK device.</summary>
        private bool Looks(string product)
        {
            foreach (string marker in TufMarkers)
                if (product.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string marker in OtherMarkers)
                if (product.Contains(marker, StringComparison.OrdinalIgnoreCase)) return HasRgbDevice();
            return false;
        }

        /// <summary>Whether DSTS reports either RGB mode device. Its answer less
        /// 0x10000 is not negative for one that is there (DeviceGet, IsSupported).</summary>
        private bool HasRgbDevice()
        {
            IAtkAcpiChannel atk = null;
            try { atk = _open(); } catch { }
            if (atk == null) return false;
            try
            {
                foreach (uint device in new[] { RgbModeDevice, RgbModeDevice2 })
                {
                    var arguments = new byte[8];
                    BitConverter.TryWriteBytes(arguments.AsSpan(0, 4), device);
                    if (atk.Call(Dsts, arguments) is int status && status - 0x10000 >= 0) return true;
                }
                return false;
            }
            finally
            {
                try { atk.Dispose(); } catch { }
            }
        }

        /// <summary>The arguments of DEVS for a color: the device, then the
        /// two numbers setMode builds, low byte first
        /// (AsusTUFLaptopController_Windows.cpp:294-297, three ints at
        /// lines 190-199): the magic, the mode, red, green, then blue, the
        /// speed and two zero bytes.</summary>
        internal static byte[] ColorArguments(int rgb, uint device = RgbModeDevice)
        {
            var arguments = new byte[12];
            BitConverter.TryWriteBytes(arguments.AsSpan(0, 4), device);
            arguments[4] = MagicUse;
            arguments[5] = ModeStatic;
            arguments[6] = (byte)(rgb >> 16);
            arguments[7] = (byte)(rgb >> 8);
            arguments[8] = (byte)rgb;
            arguments[9] = SpeedNormal;
            return arguments;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly IAtkAcpiChannel _atk;
            private uint _device = RgbModeDevice;

            public Session(IAtkAcpiChannel atk) => _atk = atk;

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            /// <summary>The one light's color. DEVS answers 1 when it took
            /// it (AsusTUFLaptopController_Windows.cpp:205-208).</summary>
            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                if (_atk.Call(Devs, ColorArguments(colors[0], _device)) == 1) return HidLightingPaint.Sent;
                if (_device == RgbModeDevice2 || _atk.Call(Devs, ColorArguments(colors[0], RgbModeDevice2)) != 1)
                    return HidLightingPaint.Failed;
                _device = RgbModeDevice2;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
