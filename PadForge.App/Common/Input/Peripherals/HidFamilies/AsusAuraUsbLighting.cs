using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's AsusAuraUSBController family (#557): every ASUS Aura device
    /// OpenRGB lights over USB HID, each through the sub-controller its
    /// detector names. The collection is the first of OpenRGB's candidates
    /// for it (<see cref="OpenRgbRegistrations.Match(string, HidCollectionInfo, int)"/>),
    /// since RunHIDDetector stops at the first detector that returns a
    /// controller (DetectionManager.cpp:1599-1622) and every detector of this
    /// family returns one for a collection it opens. The sub-controllers and
    /// their sources, all under Controllers/AsusAuraUSBController at the
    /// pinned commit:
    ///
    /// <para>Keyboards (AsusAuraKeyboardController, <see cref="Keyboard"/>),
    /// mice (AsusAuraMouseController, <see cref="Mouse"/>), the first ROG
    /// mice (AsusAuraMouseGen1Controller, <see cref="StrixEvolve"/> and
    /// <see cref="Spatha"/>), mousemats (AsusAuraMousematController), headset
    /// stands (AsusAuraHeadsetStandController), monitors
    /// (AsusAuraMonitorController), mainboards, addressable controllers, the
    /// AURA Terminal and the Strix LC III (AsusAuraUSBController), the Ryuo
    /// and Ryujin coolers (AsusAuraRyuoAIOController), the Strix LC cooler
    /// (AsusROGStrixLCController) and the ROG Ally (AsusROGAllyController).</para>
    ///
    /// <para>What each write does to the device's stored state. Every frame
    /// PadForge sends is one of OpenRGB's Direct modes, or for a device with
    /// none, the mode OpenRGB lights per LED with, and none of them is
    /// followed by the save frame OpenRGB sends only from DeviceSaveMode
    /// (50 55 for keyboards and headset stands, 50 03 for mice and mousemats,
    /// EC 3F 55 for mainboards, 5A B4 for the Ally, UpdateProfile for the
    /// first ROG mice). So nothing is written to the device's memory, and a
    /// replug brings back what the device stored. No reference reads the
    /// mode a device of this family shows, so none can be put back while the
    /// device stays plugged in, save where the device has a command for it:
    /// the first ROG mice take ResetToSavedLighting
    /// (AsusAuraMouseGen1Controller.cpp:172-181), and the Claymore takes
    /// AllowRemoteControl(0), the frame OpenRGB ends every mode change of the
    /// Claymore with (RGBController_AsusAuraKeyboard.cpp:657-662). Every other
    /// device keeps the last colors PadForge sent until it is replugged or
    /// Armoury Crate lights it. SignalRGB's plugins for these devices end on a
    /// shutdown color or a rainbow mode they save
    /// (signal-plugins Plugins/Asus/ASUS_ROG_Strix_Scope.js:41-45), and
    /// return none of them to its own lighting either.</para>
    ///
    /// <para>Timing. No reference keeps these devices alive or bounds their
    /// rate. A picture goes out at most every <see cref="HidppBackend.PaintMs"/>,
    /// the effects dispatcher's own tick, and each frame waits for the
    /// device's answer where OpenRGB waits for it.</para>
    ///
    /// <para>Armoury Crate lights every one of these devices, and while it
    /// runs each is left to it (<see cref="VendorPrograms.ArmouryCrate"/>).</para>
    /// </summary>
    internal sealed partial class AsusAuraUsbLighting : IHidLightingFamily
    {
        public static readonly AsusAuraUsbLighting Instance = new();

        /// <summary>The family's folder name in OpenRGB's Controllers.</summary>
        internal const string OpenRgbFamily = "AsusAuraUSBController";

        /// <summary>AURA_USB_VID (AsusAuraUSBControllerDetect.cpp:20).</summary>
        internal const ushort VendorId = 0x0B05;

        /// <summary>The length every output frame of the family goes out at,
        /// the report ID included (hid_write(dev, buf, 65) throughout).</summary>
        internal const int PacketSize = 65;

        /// <summary>How long a read waits for an answer OpenRGB waits for
        /// with no bound (hid_read): AsusAuraKeyboardController.cpp:170 and
        /// AsusAuraUSBController.cpp:212.</summary>
        internal const int AnswerMs = 1000;

        /// <summary>How many queued answers a read of what came in drops at
        /// most. OpenRGB's ClearResponses reads until nothing comes
        /// (AsusAuraKeyboardController.cpp:724-732). The bound keeps a device
        /// that answers without end from holding the worker.</summary>
        internal const int ClearLimit = 64;

        public string Name => "ASUS Aura USB";

        private readonly ConcurrentDictionary<string, HidLightingModel> _models = new(StringComparer.Ordinal);

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var found = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (found.Count == 0) return null;
            var registration = found[0];
            if (!Carries(registration, collection)) return null;
            return _models.GetOrAdd(registration.Source + ":" + collection.ProductId.ToString("X4"),
                _ => ModelFor(registration, collection.ProductId));
        }

        /// <summary>Whether the collection has the reports the
        /// sub-controller writes, for a registration that names no usage
        /// page: a mainboard's or addressable controller's every collection
        /// answers its detector (AsusAuraUSBControllerDetect.cpp:149-163), and
        /// a collection with no 65-byte output report cannot carry the EC
        /// frames, so OpenRGB's controller on it reads no config table and
        /// lights nothing (AsusAuraUSBController.cpp:193-239). A length of 0
        /// is a walk that did not read it, and passes.</summary>
        internal static bool Carries(OpenRgbRegistration registration, HidCollectionInfo collection)
        {
            if (registration.UsagePage != OpenRgbRegistration.Any) return true;
            return collection.OutputReportLength == 0 || collection.OutputReportLength >= PacketSize;
        }

        /// <summary>The model a registration's detector builds for a
        /// product, or null for a detector this family does not port.</summary>
        internal HidLightingModel ModelFor(OpenRgbRegistration registration, int productId)
            => registration.Detector switch
            {
                "DetectAsusAuraUSBKeyboard" => Keyboard(registration.Name, productId),
                "DetectAsusAuraUSBMice" => Mouse(registration.Name, productId),
                "DetectAsusAuraUSBStrixEvolve" => StrixEvolve(registration.Name, productId),
                "DetectAsusAuraUSBSpatha" => Spatha(registration.Name, productId),
                "DetectAsusAuraUSBMousemats" => Mousemat(registration.Name, productId),
                "DetectAsusAuraUSBHeadsetStand" => HeadsetStand(registration.Name, productId),
                "DetectAsusAuraUSBMonitor" => Monitor(registration.Name, productId),
                "DetectAsusAuraUSBAddressable" or "DetectAsusAuraUSBTerminal"
                    => AuraUsb(registration.Name, productId, mainboard: false),
                "DetectAsusAuraUSBMotherboards" or "DetectAsusAuraUSBLiquidCoolers"
                    => AuraUsb(registration.Name, productId, mainboard: true),
                "DetectAsusAuraUSBRyuoAIO" => Ryuo(registration.Name, productId),
                "DetectAsusAuraUSBROGStrixLC" => StrixLc(registration.Name, productId),
                "DetectAsusROGAlly" => Ally(registration.Name, productId),
                _ => null,
            };

        /// <summary>A model with the family's programs and the stream's
        /// repaint interval.</summary>
        private HidLightingModel Model(string name, int productId, LightMap map,
            Func<IHidReportChannel, Action<int>, HidLightingSession> begin)
            => new()
            {
                Family = this,
                Name = name,
                Map = map,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                RepaintMs = HidppBackend.PaintMs,
                Begin = begin,
            };

        /// <summary>The key Map Key Lights stores a model's names under.</summary>
        internal static string ProductKey(int productId, string detail = null)
            => detail == null ? $"asus:{VendorId:X4}:{productId:X4}" : $"asus:{VendorId:X4}:{productId:X4}:{detail}";

        private static LightInfo Zone(int address, int zone)
            => new(address, LightKind.Zone, 0, LightKeySource.None) { Zone = zone };

        private static LightInfo Edge(int address) => new(address, LightKind.Edge, 0, LightKeySource.None);

        private static LightInfo Logo(int address) => new(address, LightKind.Logo, 0, LightKeySource.None);

        private static LightInfo Other(int address) => new(address, LightKind.Other, 0, LightKeySource.None);

        private static LightInfo[] Edges(int count)
        {
            var lights = new LightInfo[count];
            for (int i = 0; i < count; i++) lights[i] = Edge(i);
            return lights;
        }

        // ─────────────────────────────────────────────
        //  The exchanges every sub-controller shares
        // ─────────────────────────────────────────────

        /// <summary>A zeroed frame of <paramref name="length"/> bytes with the
        /// bytes given from byte 0.</summary>
        internal static byte[] Frame(int length, params byte[] head)
        {
            var frame = new byte[length];
            head.CopyTo(frame, 0);
            return frame;
        }

        /// <summary>The R, G and B bytes of a color.</summary>
        internal static void Put(byte[] frame, int at, int rgb)
        {
            frame[at] = (byte)(rgb >> 16);
            frame[at + 1] = (byte)(rgb >> 8);
            frame[at + 2] = (byte)rgb;
        }

        /// <summary>Drops the answers that came in, OpenRGB's ClearResponses
        /// (AsusAuraKeyboardController.cpp:724-732).</summary>
        internal static void Clear(IHidReportChannel io, byte[] scratch)
        {
            for (int i = 0; i < ClearLimit && io.ReadInput(scratch, 0) > 0; i++) { }
        }

        /// <summary>Waits up to <paramref name="ms"/> for one answer,
        /// OpenRGB's AwaitResponse (AsusAuraKeyboardController.cpp:718-722).</summary>
        internal static void Await(IHidReportChannel io, byte[] scratch, int ms) => io.ReadInput(scratch, ms);

        /// <summary>Writes a frame and reads its answer, the hid_write then
        /// hid_read OpenRGB's reads make. Null when the write failed or no
        /// answer came.</summary>
        internal static byte[] Ask(IHidReportChannel io, byte[] frame)
        {
            if (!io.WriteOutput(frame)) return null;
            var answer = new byte[Math.Max(PacketSize, io.InputReportLength)];
            return io.ReadInput(answer, AnswerMs) > 0 ? answer : null;
        }
    }
}
