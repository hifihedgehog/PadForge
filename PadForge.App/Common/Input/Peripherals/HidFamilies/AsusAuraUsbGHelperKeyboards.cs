using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One of G-Helper's keyboard model classes: its product ID, its name,
    /// the layouts its LayoutName picks between, and where the class is.</summary>
    internal sealed record GHelperKeyboard(ushort ProductId, string Name, string Layout, string IsoLayout, string Source);

    /// <summary>
    /// The ASUS keyboards only G-Helper lights (#557), as its AsusKeyboard class
    /// does (app/Peripherals/Keyboard/AsusKeyboard.cs at the pin of
    /// <see cref="GHelperKeyboardTables"/>): today the ROG Falchion Ace 75 HE,
    /// 0B05:1C7D, which G-Helper added in a2bd94e
    /// (Models/Falchion.cs:98-118). No OpenRGB family and no SignalRGB plugin
    /// names it.
    ///
    /// <para>The collection. G-Helper's candidates are the keyboard's collections
    /// whose output report is 64 bytes or longer, the ones with a usage on the
    /// vendor page 0xFF00 first (FindVendorDevices, lines 109-136). It writes to
    /// an only candidate unasked, and with several it asks 12 00 of each and takes
    /// the first that answers with data, or the first of them when none does
    /// (SetProvider and ProbeInterface, lines 159-208). PadForge makes no such
    /// probe: a vendor-page candidate is the unit ahead of the others
    /// (<see cref="GHelperOutranks"/>), and candidates of one kind are a unit
    /// each. One candidate is expected. The keyboard's interface 1 is its only one
    /// with an output endpoint, 64 bytes each way, behind a 34-byte report
    /// descriptor (the USB descriptor dump in G-Helper issue 6340), and SignalRGB
    /// finds the Falchion RX, the class this one derives from, and the Falchion
    /// Ace HFX beside it on interface 1, page 0xFF00, usage 1 (signal-plugins
    /// Plugins/Asus/ASUS_Keyboard.js:403 and 430 on its beta branch).</para>
    ///
    /// <para>Every exchange is WriteForResponse (lines 312-358): what came in is read
    /// away (Drain, WindowsUsbProvider.cs:86-103, here the family's
    /// <see cref="Clear"/>), a 65-byte output report goes out, report ID 00 first
    /// (reportId, line 35, and USBPacketSize, lines 307-310), and one input report
    /// is read back within 300 ms (USBTimeout, lines 302-305, the stream's read and
    /// write timeouts, WindowsUsbProvider.cs:28-29). A write that fails, no answer,
    /// or an answer of FF AA at bytes 1 and 2, fails it.</para>
    ///
    /// <para>The layout. A session asks 12 12, and byte 6 of the answer is the
    /// board's factory layout (ReadMultiLayout, lines 360-372). An ISO one of
    /// IsoMultiLayouts (lines 363-364) takes the model's ISO layout, and any other
    /// the ANSI one (LayoutName, Falchion.cs:114-117). With no answer, FF AA or
    /// another command's answer G-Helper leaves the layout as it was (line 369),
    /// ANSI at first (line 361), and lights the keyboard all the same
    /// (PeripheralsProvider.cs:312-322). A session whose 12 12 is answered FF AA
    /// or as another command starts the same way, with no map of its own, so the
    /// worker keeps the one it had (<see cref="HidLightingSession.Map"/>). A
    /// keyboard that answers nothing gets no session, the worker's rule for a
    /// device that does not answer (<see cref="HidLightingModel.Begin"/>), where
    /// G-Helper would go on to write a frame and wait for its answer. Each light
    /// is a key of the layout in G-Helper's order, at its LED ID
    /// (AuraKeyboardLayouts.cs:332-335). An ISO board's Enter is two cells there
    /// on one LED, 6B, and so two lights.</para>
    ///
    /// <para>A picture is SetLedColors' frames (lines 753-783): C0 81, the count of
    /// the frame's keys, 00, then LED ID, red, green and blue per key, fourteen keys
    /// to a frame, each frame an exchange, and the first that fails ends the
    /// picture. G-Helper scales each channel by its own brightness setting (Dim,
    /// line 755), 100 unless its user moves it (StoredBrightness, line 876), and
    /// PadForge's colors come already dimmed, so they go out as they are. G-Helper
    /// sends a picture of one color as its 51 2C Static mode (lines 759-760), which
    /// sets a mode and takes black as a random color (BuildAuraPacket, lines
    /// 634-636). PadForge sends that picture as the frames too, as it does every
    /// other.</para>
    ///
    /// <para>No mode frame comes first: G-Helper sends none before C0 81
    /// (ApplyStoredLighting, lines 888-893). Nothing is saved: 50 55 (SaveLighting,
    /// lines 789-794) follows a change in G-Helper's settings window or a mode it
    /// copies from the laptop's keyboard (SyncFromLaptopAura, lines 850-861), and
    /// the picture G-Helper sends when the keyboard connects goes with none
    /// (PeripheralsProvider.cs:316-319). Hand-back: G-Helper writes nothing on exit
    /// (Program.cs:475-489) or when it lets the keyboard go (Dispose, lines
    /// 218-224). It reads which effect the board renders (ReadProfile, lines
    /// 815-835) and not that effect's colors, speed or brightness, all of which
    /// 51 2C sets, so no command of AsusKeyboard.cs puts the board back on its own
    /// effect. The keyboard keeps PadForge's last picture until it is replugged,
    /// as G-Helper's "Custom" mode keeps its own (HasTransientLighting, line
    /// 895).</para>
    /// </summary>
    internal sealed partial class AsusAuraUsbLighting
    {
        /// <summary>FindVendorDevices' vendor page and least output length.</summary>
        internal const ushort GHelperVendorPage = 0xFF00;
        internal const int GHelperLeastOutput = 64;

        /// <summary>USBTimeout.</summary>
        internal const int GHelperTimeoutMs = 300;

        /// <summary>The keys of one C0 81 frame (lines 753-754 and 763-765).</summary>
        internal const int GHelperKeysPerFrame = 14;

        internal static GHelperKeyboard GHelperKeyboardOf(ushort productId)
            => GHelperKeyboardTables.Keyboards.FirstOrDefault(k => k.ProductId == productId);

        private HidLightingModel GHelperKeyboardModel(HidCollectionInfo collection, GHelperKeyboard keyboard)
        {
            if (!GHelperCandidate(collection)) return null;
            return _models.GetOrAdd("g-helper:" + keyboard.Source + ":" + collection.ProductId.ToString("X4"),
                _ => Model("ASUS " + keyboard.Name, keyboard.ProductId, GHelperKeyboardMap(keyboard.ProductId, keyboard.Layout),
                    (io, wait) => BeginGHelperKeyboard(io, keyboard), outranks: GHelperOutranks));
        }

        /// <summary>One of FindVendorDevices' candidates: a collection whose output
        /// report is 64 bytes or longer (line 124).</summary>
        internal static bool GHelperCandidate(HidCollectionInfo collection)
            => collection.OutputReportLength >= GHelperLeastOutput;

        /// <summary>FindVendorDevices' order: a candidate on the vendor page comes
        /// before one that is not (lines 125-128 and 134).</summary>
        internal static bool GHelperOutranks(HidCollectionInfo first, HidCollectionInfo second)
            => first.VendorId == VendorId && first.ProductId == second.ProductId && GHelperCandidate(first)
            && first.UsagePage == GHelperVendorPage && second.UsagePage != GHelperVendorPage;

        /// <summary>A layout's lights in G-Helper's order, each at its LED ID, its row and
        /// its cell (<see cref="PluginKeyNames.Lights"/>). Fn takes its label, and the
        /// key right of it, which no usage names and the table leaves unnamed, is a key
        /// Map Key Lights asks about.</summary>
        internal static LightMap GHelperKeyboardMap(ushort productId, string layout)
        {
            var table = GHelperKeyboardTables.Layout(layout);
            string product = ProductKey(productId, layout);
            if (table == null) return new LightMap(Array.Empty<LightInfo>()) { Product = product };
            return new LightMap(PluginKeyNames.Lights(table)) { Product = product };
        }

        /// <summary>ReadMultiLayout's query.</summary>
        internal static byte[] GHelperLayoutQuery() => Frame(PacketSize, 0x00, 0x12, 0x12);

        /// <summary>The factory layout a 12 12 answer names, report ID first, or -1 for
        /// FF AA and for another command's answer, as MultiLayout starts (line 361).</summary>
        internal static int GHelperLayoutOf(byte[] answer)
            => answer != null && answer.Length > 6 && answer[1] == 0x12 && answer[2] == 0x12 ? answer[6] : -1;

        /// <summary>WriteForResponse's write and read (lines 335-352): the answer, report
        /// ID first, or null when the write failed or none came within the timeout.</summary>
        internal static byte[] GHelperAsk(IHidReportChannel io, byte[] frame, byte[] scratch)
        {
            Clear(io, scratch);
            if (!io.WriteOutput(frame, GHelperTimeoutMs)) return null;
            var answer = new byte[Math.Max(PacketSize, io.InputReportLength)];
            return io.ReadInput(answer, GHelperTimeoutMs) > 0 ? answer : null;
        }

        /// <summary>Whether WriteForResponse hands an answer on: any but none and
        /// FF AA (line 355).</summary>
        internal static bool GHelperTaken(byte[] answer) => answer != null && !(answer[1] == 0xFF && answer[2] == 0xAA);

        /// <summary>SetLedColors' frames for the lights at their LED IDs and colors.</summary>
        internal static List<byte[]> GHelperKeyFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            var frames = new List<byte[]>();
            for (int start = 0; start < count; start += GHelperKeysPerFrame)
            {
                int keys = Math.Min(GHelperKeysPerFrame, count - start);
                var frame = Frame(PacketSize, 0x00, 0xC0, 0x81, (byte)keys, 0x00);
                for (int j = 0; j < keys; j++)
                {
                    frame[5 + j * 4] = (byte)lights[start + j].Address;
                    Put(frame, 6 + j * 4, colors[start + j]);
                }
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>Asks the layout. Null when the keyboard answered nothing. The
        /// session reads a map only from a 12 12 answer.</summary>
        internal static HidLightingSession BeginGHelperKeyboard(IHidReportChannel io, GHelperKeyboard keyboard)
        {
            if (io == null) return null;
            var scratch = new byte[Math.Max(PacketSize, io.InputReportLength)];
            var answer = GHelperAsk(io, GHelperLayoutQuery(), scratch);
            if (answer == null) return null;
            int layout = GHelperLayoutOf(answer);
            bool iso = Array.IndexOf(GHelperKeyboardTables.IsoLayouts, layout) >= 0;
            var map = layout < 0 ? null : GHelperKeyboardMap(keyboard.ProductId, iso ? keyboard.IsoLayout : keyboard.Layout);
            return new GHelperKeyboardSession(map, scratch);
        }

        internal sealed class GHelperKeyboardSession : HidLightingSession
        {
            private readonly LightMap _map;
            private readonly byte[] _scratch;

            public GHelperKeyboardSession(LightMap map, byte[] scratch)
            {
                _map = map;
                _scratch = scratch;
            }

            public override bool Feature => false;

            public override LightMap Map => _map;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in GHelperKeyFrames(lights, colors))
                    if (!GHelperTaken(GHelperAsk(io, frame, _scratch))) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
