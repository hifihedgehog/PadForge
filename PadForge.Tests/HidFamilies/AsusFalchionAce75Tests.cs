using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The ROG Falchion Ace 75 HE (0B05:1C7D) against G-Helper at the pinned
    /// commit, the one reference that lights it (a2bd94e). Every expected byte is
    /// written out from G-Helper's own code: the model at
    /// app/Peripherals/Keyboard/Models/Falchion.cs:98-118, its layouts at
    /// AuraKeyboardLayouts.cs:125-133 and 332-335, and the exchanges of
    /// AsusKeyboard.cs.</summary>
    public class AsusFalchionAce75Tests
    {
        private const ushort Product = 0x1C7D;

        private static string Hex(byte[] bytes) => AsusAuraUsbLightingTests.Hex(bytes);

        /// <summary>A collection of the keyboard. The device's interface 1 is its only one
        /// with an output endpoint, 64 bytes each way (the USB descriptor dump in G-Helper
        /// issue 6340), and G-Helper writes to a collection whose output report is 64 bytes
        /// or longer, one on the vendor page 0xFF00 first (FindVendorDevices,
        /// AsusKeyboard.cs:109-136).</summary>
        private static HidCollectionInfo Collection(ushort page = 0xFF00, ushort usage = 0x01, int iface = 1, ushort output = 65)
            => new()
            {
                Path = $@"\\?\hid#vid_0b05&pid_1c7d&mi_{iface:x2}#fake",
                VendorId = 0x0B05,
                ProductId = Product,
                InterfaceNumber = iface,
                UsagePage = page,
                Usage = usage,
                InputReportLength = 65,
                OutputReportLength = output,
            };

        /// <summary>The 12 12 answer, as HidSharp hands it to G-Helper: the report ID 00
        /// first, then 12 12, and the factory layout ID at byte 6 (ReadMultiLayout,
        /// AsusKeyboard.cs:360-372).</summary>
        private static byte[] LayoutAnswer(byte layout)
        {
            var answer = new byte[65];
            answer[1] = 0x12;
            answer[2] = 0x12;
            answer[6] = layout;
            return answer;
        }

        private static byte[] Ack()
        {
            var answer = new byte[65];
            answer[1] = 0xC0;
            answer[2] = 0x81;
            return answer;
        }

        private static List<byte[]> Outs(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();

        /// <summary>FalchionAce75's LED IDs (AuraKeyboardLayouts.cs:332-333), key by key in
        /// the order of FalchionAce75Rows.</summary>
        private static readonly int[] AnsiLeds =
        {
            0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68, 0x70,
            0x01, 0x09, 0x11, 0x19, 0x21, 0x29, 0x31, 0x39, 0x41, 0x49, 0x51, 0x59, 0x61, 0x69, 0x71,
            0x02, 0x0A, 0x12, 0x1A, 0x22, 0x2A, 0x32, 0x3A, 0x42, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x72,
            0x03, 0x0B, 0x13, 0x1B, 0x23, 0x2B, 0x33, 0x3B, 0x43, 0x4B, 0x53, 0x5B, 0x6B, 0x73,
            0x04, 0x14, 0x1C, 0x24, 0x2C, 0x34, 0x3C, 0x44, 0x4C, 0x54, 0x5C, 0x64, 0x6C, 0x74,
            0x05, 0x0D, 0x15, 0x35, 0x4D, 0x55, 0x5D, 0x65, 0x6D, 0x75,
        };

        /// <summary>FalchionAce75ISO's (lines 334-335), in the order ToIso gives the rows
        /// (lines 96-112): the backslash's place takes Enter, # comes before Enter, and
        /// &lt; after the left Shift.</summary>
        private static readonly int[] IsoLeds =
        {
            0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68, 0x70,
            0x01, 0x09, 0x11, 0x19, 0x21, 0x29, 0x31, 0x39, 0x41, 0x49, 0x51, 0x59, 0x61, 0x69, 0x71,
            0x02, 0x0A, 0x12, 0x1A, 0x22, 0x2A, 0x32, 0x3A, 0x42, 0x4A, 0x52, 0x5A, 0x62, 0x6B, 0x72,
            0x03, 0x0B, 0x13, 0x1B, 0x23, 0x2B, 0x33, 0x3B, 0x43, 0x4B, 0x53, 0x5B, 0x63, 0x6B, 0x73,
            0x04, 0x0C, 0x14, 0x1C, 0x24, 0x2C, 0x34, 0x3C, 0x44, 0x4C, 0x54, 0x5C, 0x64, 0x6C, 0x74,
            0x05, 0x0D, 0x15, 0x35, 0x4D, 0x55, 0x5D, 0x65, 0x6D, 0x75,
        };

        /// <summary>The keyboard tables come from the G-Helper commit the laptop tables come
        /// from, the one pin release prep moves (gen_ghelper_keyboard_tables.py imports
        /// it from gen_asus_laptop_tables.py).</summary>
        [Fact]
        public void TheKeyboardTables_AreReadAtTheLaptopTablesGHelperCommit()
        {
            Assert.Equal(AsusLaptopTables.GHelperCommit, GHelperKeyboardTables.GHelperCommit);
            var keyboard = Assert.Single(GHelperKeyboardTables.Keyboards);
            Assert.Equal((Product, "ROG Falchion Ace 75 HE", "FalchionAce75", "FalchionAce75ISO"),
                (keyboard.ProductId, keyboard.Name, keyboard.Layout, keyboard.IsoLayout));
            Assert.Equal(new[] { 3, 4, 5, 7, 8, 11, 12, 15, 18, 19, 20, 23, 25 }, GHelperKeyboardTables.IsoLayouts);
        }

        /// <summary>FindVendorDevices (AsusKeyboard.cs:109-136): a collection of the keyboard
        /// is a candidate when its output report is 64 bytes or longer (line 124), and the
        /// candidates on the vendor page 0xFF00 come before the others (lines 125-128 and
        /// 134).</summary>
        [Fact]
        public void TheKeyboard_IsMatchedOnACollectionWithA64ByteOutputReport_TheVendorPagesFirst()
        {
            var family = new AsusAuraUsbLighting();
            var model = family.Match(Collection());
            Assert.NotNull(model);
            Assert.Equal("ASUS ROG Falchion Ace 75 HE", model.Name);
            Assert.Same(family, model.Family);
            Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, model.Programs);
            Assert.Equal(HidppBackend.PaintMs, model.RepaintMs);
            Assert.Same(model, family.Match(Collection()));
            // The boot keyboard on interface 0, and vendor collections too short for a
            // 64-byte report.
            Assert.Null(family.Match(Collection(page: 0x0001, usage: 0x06, iface: 0, output: 2)));
            Assert.Null(family.Match(Collection(output: 21)));
            Assert.Null(family.Match(Collection(output: 63)));
            Assert.Same(model, family.Match(Collection(output: 64)));
            // A collection off the vendor page with such a report is a candidate too, behind
            // the vendor page's.
            var other = Collection(page: 0xFF31, iface: 2);
            Assert.Same(model, family.Match(other));
            Assert.NotNull(model.Outranks);
            Assert.True(model.Outranks(Collection(), other));
            Assert.False(model.Outranks(other, Collection()));
            // Two of one kind: neither is ahead of the other.
            Assert.False(model.Outranks(Collection(), Collection(iface: 3)));
            Assert.False(model.Outranks(other, Collection(page: 0xFF32, iface: 3)));
            // A vendor collection that is no candidate is ahead of nothing, and neither is
            // another product's or another maker's.
            Assert.False(model.Outranks(Collection(output: 21), other));
            Assert.False(model.Outranks(Collection() with { ProductId = 0x1B7E }, other));
            Assert.False(model.Outranks(Collection() with { VendorId = 0x046D }, other));
        }

        /// <summary>The lights are the layout's keys in its order, each at its LED ID, so the
        /// colors go out in the order SetLedColors writes them (AsusKeyboard.cs:757-783).</summary>
        [Fact]
        public void TheModelsMap_IsTheAnsiLayout_AtItsLedIds()
        {
            var map = new AsusAuraUsbLighting().Match(Collection()).Map;
            Assert.Equal(AnsiLeds, map.Lights.Select(l => l.Address).ToArray());
            Assert.True(map.Positioned);
            Assert.Equal(AsusAuraUsbLighting.ProductKey(Product, "FalchionAce75"), map.Product);
            LightInfo At(int led) => map.Lights.Single(l => l.Address == led);
            // Esc, Home, End, Backspace, Insert, Delete, Page Up and Page Down by their places.
            Assert.Equal(0x29, At(0x00).Usage);
            Assert.Equal(0x4A, At(0x68).Usage);
            Assert.Equal(0x4D, At(0x70).Usage);
            Assert.Equal(0x2A, At(0x69).Usage);
            Assert.Equal(0x49, At(0x71).Usage);
            Assert.Equal(0x4C, At(0x72).Usage);
            Assert.Equal(0x4B, At(0x73).Usage);
            Assert.Equal(0x4E, At(0x74).Usage);
            Assert.Equal(0x31, At(0x6A).Usage);
            Assert.Equal(0x28, At(0x6B).Usage);
            Assert.Equal(0x2C, At(0x35).Usage);
            Assert.Equal(0x52, At(0x6C).Usage);
            Assert.Equal(0x4F, At(0x75).Usage);
            Assert.All(map.Lights.Where(l => l.Address is not (0x55 or 0x5D)), l => Assert.Equal(LightKeySource.Generated, l.KeySource));
            // Fn has no usage, and neither has the key right of it: Map Key Lights asks about
            // that one.
            Assert.Equal(LightKind.Other, At(0x55).Kind);
            Assert.Equal(LightLabel.Fn, At(0x55).Label);
            Assert.Equal(LightKind.Key, At(0x5D).Kind);
            Assert.Equal(0, At(0x5D).Usage);
            Assert.Equal(1, map.UnnamedKeyCount);
            // Each key at its row and the cell its place in the row rounds to.
            Assert.Equal((4f, 5f), (At(0x35).X, At(0x35).Y));
            Assert.Equal((15f, 5f), (At(0x75).X, At(0x75).Y));
            Assert.Equal((14f, 0f), (At(0x68).X, At(0x68).Y));
            Assert.Equal((13f, 1f), (At(0x69).X, At(0x69).Y));
        }

        /// <summary>A session asks 12 12 first. A US board (17) takes the ANSI layout, and a
        /// picture goes out as C0 81 frames of fourteen keys each, the count of that frame
        /// at byte 3, then LED, red, green and blue per key, each frame read back before the
        /// next (SetLedColors, AsusKeyboard.cs:753-783, and WriteForResponse, lines 312-358,
        /// with a 300 ms timeout, lines 302-305, and 65-byte reports, lines 307-310).</summary>
        [Fact]
        public void APicture_GoesOutAsFourteenKeyFrames_EachReadBack()
        {
            var model = new AsusAuraUsbLighting().Match(Collection());
            var io = AsusAuraUsbLightingTests.Channel(new[] { LayoutAnswer(17) }.Concat(Enumerable.Repeat(Ack(), 6)).ToArray());
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            Assert.False(session.Feature);
            var lights = session.Map?.Lights ?? model.Map.Lights;
            Assert.Equal(AnsiLeds, lights.Select(l => l.Address).ToArray());
            AsusAuraUsbLightingTests.Starts("00 12 12", Outs(io)[0], 65);

            var colors = Enumerable.Range(0, lights.Count).Select(i => 0x010203 * (i + 1)).ToList();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            var frames = Outs(io).Skip(1).ToList();
            Assert.Equal(6, frames.Count);
            Assert.Equal("00 C0 81 0E 00 00 01 02 03 08 02 04 06", Hex(frames[0].Take(13).ToArray()));
            Assert.Equal(65, frames[0].Length);
            // The fourteenth key ends at byte 60, and the rest of the report is zeros.
            Assert.Equal("68 0E 1C 2A",Hex(frames[0].Skip(57).Take(4).ToArray()));
            Assert.All(frames[0].Skip(61), b => Assert.Equal(0, b));
            // 83 keys: five frames of fourteen, then thirteen.
            AsusAuraUsbLightingTests.Starts("00 C0 81 0D 00 " + string.Join(" ",
                Enumerable.Range(70, 13).Select(i => $"{AnsiLeds[i]:X2} {i + 1:X2} {2 * (i + 1):X2} {3 * (i + 1) & 0xFF:X2}")), frames[5], 65);
            for (int f = 0; f < 6; f++)
                for (int j = 0; j < frames[f][3]; j++)
                    Assert.Equal(AnsiLeds[f * 14 + j], frames[f][5 + j * 4]);
            // Every frame waits for its answer before the next goes out.
            var kinds = io.Calls.Select(c => c.Kind).ToList();
            int firstFrame = kinds.IndexOf("out", kinds.IndexOf("out") + 1);
            for (int i = firstFrame; i < kinds.Count; i++)
                if (kinds[i] == "out") Assert.Equal("in", kinds.Skip(i + 1).FirstOrDefault());
            Assert.Equal(300, io.LongestReadMs);
        }

        /// <summary>A board whose 12 12 answer names an ISO layout (IsoMultiLayouts,
        /// AsusKeyboard.cs:363-364) takes FalchionAce75ISO (Falchion.cs:114-117).</summary>
        [Fact]
        public void AnIsoBoard_TakesTheIsoLayout()
        {
            var model = new AsusAuraUsbLighting().Match(Collection());
            foreach (byte iso in new byte[] { 3, 4, 5, 7, 8, 11, 12, 15, 18, 19, 20, 23, 25 })
            {
                var io = AsusAuraUsbLightingTests.Channel(new[] { LayoutAnswer(iso) }.Concat(Enumerable.Repeat(Ack(), 7)).ToArray());
                var session = model.Begin(io, _ => { });
                Assert.NotNull(session);
                var map = session.Map;
                Assert.NotNull(map);
                Assert.Equal(IsoLeds, map.Lights.Select(l => l.Address).ToArray());
                Assert.Equal(AsusAuraUsbLighting.ProductKey(Product, "FalchionAce75ISO"), map.Product);
                Assert.Equal(0x32, map.Lights.Single(l => l.Address == 0x63).Usage);
                Assert.Equal(0x64, map.Lights.Single(l => l.Address == 0x0C).Usage);
                Assert.Equal(2, map.Lights.Count(l => l.Address == 0x6B && l.Usage == 0x28));
                var colors = Enumerable.Repeat(0x102030, map.Lights.Count).ToList();
                Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, map.Lights, colors));
                var frames = Outs(io).Skip(1).ToList();
                Assert.Equal(7, frames.Count);
                AsusAuraUsbLightingTests.Starts("00 C0 81 01 00 75 10 20 30", frames[6], 65);
            }
            // Any other layout a 12 12 answer names, a US one among them, is ANSI.
            foreach (byte ansi in new byte[] { 17, 2, 0 })
            {
                var io = AsusAuraUsbLightingTests.Channel(LayoutAnswer(ansi));
                var session = model.Begin(io, _ => { });
                Assert.NotNull(session);
                Assert.NotNull(session.Map);
                Assert.Equal(AnsiLeds, session.Map.Lights.Select(l => l.Address).ToArray());
                Assert.Equal(AsusAuraUsbLighting.ProductKey(Product, "FalchionAce75"), session.Map.Product);
            }
        }

        /// <summary>WriteForResponse fails on a write that fails, on no answer and on an
        /// answer of FF AA (AsusKeyboard.cs:343-357), and SetLedColors stops at the first
        /// failed frame (line 780).</summary>
        [Fact]
        public void AFrameWithNoAnswer_OrAnFfAaAnswer_FailsThePicture()
        {
            var model = new AsusAuraUsbLighting().Match(Collection());
            var nak = new byte[65];
            nak[1] = 0xFF;
            nak[2] = 0xAA;
            foreach (var answers in new[] { new[] { LayoutAnswer(17), Ack() }, new[] { LayoutAnswer(17), Ack(), nak } })
            {
                var io = AsusAuraUsbLightingTests.Channel(answers);
                var session = model.Begin(io, _ => { });
                var lights = session.Map?.Lights ?? model.Map.Lights;
                Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, lights, Enumerable.Repeat(0xFFFFFF, lights.Count).ToList()));
                Assert.Equal(1 + 2, Outs(io).Count);
            }
            var failing = AsusAuraUsbLightingTests.Channel(LayoutAnswer(17));
            var started = model.Begin(failing, _ => { });
            failing.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, started.Paint(failing, _ => { }, model.Map.Lights,
                Enumerable.Repeat(0xFFFFFF, model.Map.Lights.Count).ToList()));
            Assert.Single(Outs(failing));
        }

        /// <summary>A keyboard that answers nothing to 12 12, or whose write fails, gets no
        /// session: the worker's rule for a device that does not answer
        /// (HidLightingModel.Begin), under which it writes nothing more and tries again
        /// later. G-Helper would go on to write the first frame and wait for its answer
        /// (AsusKeyboard.cs:366-372 and 780).</summary>
        [Fact]
        public void AKeyboardThatDoesNotAnswer_GetsNoSession()
        {
            var model = new AsusAuraUsbLighting().Match(Collection());
            var io = AsusAuraUsbLightingTests.Channel();
            Assert.Null(model.Begin(io, _ => { }));
            AsusAuraUsbLightingTests.Starts("00 12 12", Assert.Single(Outs(io)), 65);
            Assert.Equal(300, io.LongestReadMs);
            var failing = new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65, FailWrites = true };
            Assert.Null(model.Begin(failing, _ => { }));
        }

        /// <summary>ReadMultiLayout leaves the layout as it was when 12 12 is answered FF AA or
        /// as another command (AsusKeyboard.cs:366-372), and G-Helper lights the keyboard all
        /// the same (PeripheralsProvider.cs:312-322). So the session starts and reads no map:
        /// the model's ANSI one stays, or the one an earlier session read, and the picture
        /// goes out by it.</summary>
        [Fact]
        public void AKeyboardThatAnswersNoLayout_IsLitByTheLayoutItHad()
        {
            var model = new AsusAuraUsbLighting().Match(Collection());
            var nak = new byte[65];
            nak[1] = 0xFF;
            nak[2] = 0xAA;
            foreach (var first in new[] { nak, Ack() })
            {
                var io = AsusAuraUsbLightingTests.Channel(new[] { first }.Concat(Enumerable.Repeat(Ack(), 6)).ToArray());
                var session = model.Begin(io, _ => { });
                Assert.NotNull(session);
                Assert.Null(session.Map);
                AsusAuraUsbLightingTests.Starts("00 12 12", Assert.Single(Outs(io)), 65);
                var lights = model.Map.Lights;
                Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, Enumerable.Repeat(0x112233, lights.Count).ToList()));
                var frames = Outs(io).Skip(1).ToList();
                Assert.Equal(6, frames.Count);
                Assert.Equal("00 C0 81 0E 00 00 11 22 33 08 11 22 33", Hex(frames[0].Take(13).ToArray()));
            }
        }

        /// <summary>G-Helper writes nothing when it lets the keyboard go: its exit
        /// (Program.cs:475-489) and the keyboard's Dispose (AsusKeyboard.cs:218-224) send
        /// no frame, and no command of AsusKeyboard.cs puts the board back on its own
        /// effect. So the hand-back after a picture sends nothing, and neither does the
        /// crash path.</summary>
        [Fact]
        public void TheHandBack_AndTheCrashPath_SendNothing()
        {
            var model = new AsusAuraUsbLighting().Match(Collection());
            var io = AsusAuraUsbLightingTests.Channel(new[] { LayoutAnswer(17) }.Concat(Enumerable.Repeat(Ack(), 6)).ToArray());
            var session = model.Begin(io, _ => { });
            var lights = session.Map.Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, Enumerable.Repeat(0x112233, lights.Count).ToList()));
            int sent = io.Calls.Count;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(sent, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            Assert.Null(model.Owed);
        }
    }
}
