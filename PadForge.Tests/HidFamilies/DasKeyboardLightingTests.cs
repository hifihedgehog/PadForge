using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Das Keyboard family against OpenRGB's
    /// DasKeyboardController (#557). Expected bytes are written out from the
    /// reference lines named.</summary>
    public class DasKeyboardLightingTests
    {
        private static string Hex(IEnumerable<byte> bytes) => CherryKeyboardLightingTests.Hex(bytes);

        private static OpenRgbRegistration Registration(string name)
            => OpenRgbRegistrations.Of("DasKeyboardController").Single(r => r.Name == name);

        /// <summary>DasKeyboardControllerDetect.cpp:55-57: the 4Q by page and
        /// usage, the 5Q and 5QS by interface, taken where the collection
        /// has feature reports of 8 bytes.</summary>
        [Fact]
        public void EveryRegistration_IsAModel()
        {
            var registrations = OpenRgbRegistrations.Of("DasKeyboardController").ToList();
            Assert.Equal(3, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = CherryKeyboardLightingTests.CollectionOf(r, featureLength: 9);
                var model = DasKeyboardLighting.Instance.Match(collection);
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Equal(131, model.Map.Lights.Count);
            }
            var fiveQ = CherryKeyboardLightingTests.CollectionOf(Registration("Das Keyboard 5Q RGB"), featureLength: 0);
            Assert.Null(DasKeyboardLighting.Instance.Match(fiveQ));
            var fourQ = CherryKeyboardLightingTests.CollectionOf(Registration("Das Keyboard 4Q RGB"), featureLength: 0);
            Assert.NotNull(DasKeyboardLighting.Instance.Match(fourQ));
            Assert.Contains(DasKeyboardLighting.Instance, HidLightingFamilies.All);
        }

        /// <summary>RGBController_DasKeyboard.cpp:20-193: LED 0 is Left
        /// Control at row 6 column 0, LED 5 Escape at row 1 column 0, LED 81,
        /// which only the other grid places, at row 4 column 12, and LED 130
        /// the Q button, labelled no key.</summary>
        [Fact]
        public void TheMaps_AreOpenRgbs()
        {
            var us = DasKeyboardLighting.MapOf(true, "p").Lights;
            Assert.Equal((ushort)0xE0, us[0].Usage);
            Assert.Equal((0f, 6f), (us[0].X, us[0].Y));
            Assert.Equal((ushort)0x29, us[5].Usage);
            Assert.Equal((0f, 1f), (us[5].X, us[5].Y));
            Assert.False(us[81].HasPosition);
            var eu = DasKeyboardLighting.MapOf(false, "p").Lights;
            Assert.Equal((12f, 4f), (eu[81].X, eu[81].Y));
            Assert.Equal((17f, 0f), (eu[126].X, eu[126].Y));
            Assert.Equal(LightKind.Other, us[130].Kind);
            Assert.Equal(LightKind.Other, us[11].Kind);
        }

        /// <summary>SendDataModern (DasKeyboardController.cpp:193-230): 01,
        /// the message, its XOR. EA ^ 02 ^ B0 = 58; EA ^ 03 ^ 78 ^ 0A = 9B.</summary>
        [Fact]
        public void TheModernForm_IsOpenRgbs()
        {
            Assert.Equal("01 EA 02 B0 58", Hex(DasKeyboardLighting.Modern(DasKeyboardLighting.Initialize)));
            Assert.Equal("01 EA 03 78 0A 9B", Hex(DasKeyboardLighting.Modern(DasKeyboardLighting.Apply)));
            // SendColors (lines 93-125): key 5, mode 1, 11 22 33, XOR 96.
            Assert.Equal("01 EA 08 78 08 05 01 11 22 33 96",
                Hex(DasKeyboardLighting.Modern(DasKeyboardLighting.KeyMessage(5, 0x112233))));
            // The Q button takes the color alone: EA 06 78 06 11 22 33.
            Assert.Equal("EA 06 78 06 11 22 33", Hex(DasKeyboardLighting.KeyMessage(130, 0x112233)));
        }

        /// <summary>SendDataTraditional (lines 232-281): 01 and seven bytes,
        /// the XOR after the message, zeros after it.</summary>
        [Fact]
        public void TheTraditionalForm_IsOpenRgbs()
        {
            Assert.Equal(new[] { "01 EA 02 B0 58 00 00 00" },
                DasKeyboardLighting.Traditional(DasKeyboardLighting.Initialize).Select(Hex));
            Assert.Equal(new[] { "01 EA 08 78 08 05 01 11", "01 22 33 96 00 00 00 00" },
                DasKeyboardLighting.Traditional(DasKeyboardLighting.KeyMessage(5, 0x112233)).Select(Hex));
        }

        /// <summary>A keyboard that answers EA 02 B0 with the version
        /// "ABC16XYZ21.27.000" behind two more bytes, in 8-byte reports
        /// whose XOR is 0, then a report with first byte 0.</summary>
        private static FakeReportChannel Keyboard(string version = "ABC16XYZ21.27.000")
        {
            var answer = new List<byte> { 0x01, 0x00 };
            var text = Encoding.ASCII.GetBytes("ab" + version);
            answer.AddRange(text);
            answer.Add(0);
            answer[1] = (byte)(text.Length + 2);
            while (answer.Count % 8 != 7) answer.Add(0);
            byte x = 0;
            foreach (byte b in answer) x ^= b;
            answer.Add(x);
            var reports = new Queue<byte[]>();
            for (int i = 0; i < answer.Count; i += 8) reports.Enqueue(answer.Skip(i).Take(8).ToArray());
            var io = new FakeReportChannel { FeatureReportLength = 9 };
            io.Feature = _ => reports.Count > 0 ? reports.Dequeue() : new byte[8];
            return io;
        }

        [Fact]
        public void TheVersion_PicksTheGrid()
        {
            // GetLayoutString (lines 34-52): characters 3 and 4.
            Assert.True(DasKeyboardLighting.IsUs("ABC16XYZ21.27.000"));
            Assert.True(DasKeyboardLighting.IsUs("ABC63XYZ21.27.000"));
            Assert.False(DasKeyboardLighting.IsUs("ABC28XYZ21.27.000"));
            Assert.False(DasKeyboardLighting.IsUs("ABC16"));
            // The string starts two bytes into the answer (line 165).
            Assert.Equal("ABC16", DasKeyboardLighting.VersionOf(Encoding.ASCII.GetBytes("abABC16\0zz")));

            var model = DasKeyboardLighting.Instance.Match(CherryKeyboardLightingTests.CollectionOf(Registration("Das Keyboard 5Q RGB"), 9));
            var session = Assert.IsType<DasKeyboardLighting.Session>(model.Begin(Keyboard(), _ => { }));
            Assert.Equal("ABC16XYZ21.27.000", session.Version);
            Assert.False(session.Traditional);
            Assert.False(session.Map.Lights[81].HasPosition);
        }

        [Fact]
        public void AnUnansweredInitialize_TriesTheTraditionalForm()
        {
            var io = new FakeReportChannel { FeatureReportLength = 9, Feature = _ => new byte[8] };
            var model = DasKeyboardLighting.Instance.Match(CherryKeyboardLightingTests.CollectionOf(Registration("Das Keyboard 4Q RGB"), 9));
            var session = Assert.IsType<DasKeyboardLighting.Session>(model.Begin(io, _ => { }));
            Assert.True(session.Traditional);
            var sets = io.Calls.Where(c => c.Kind == "set").Select(c => Hex(c.Bytes)).ToList();
            Assert.Equal(new[] { "01 EA 02 B0 58", "01 EA 02 B0 58 00 00 00" }, sets);
            // No version: the grid that is not the US one.
            Assert.True(session.Map.Lights[81].HasPosition);
        }

        [Fact]
        public void APicture_SendsTheKeysThatChanged_ThenApplies()
        {
            var io = Keyboard();
            var model = DasKeyboardLighting.Instance.Match(CherryKeyboardLightingTests.CollectionOf(Registration("Das Keyboard 5QS RGB"), 9));
            var session = model.Begin(io, _ => { });
            var lights = session.Map.Lights;
            var colors = new int[131];
            int before = io.Calls.Count(c => c.Kind == "set");
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            var sets = io.Calls.Where(c => c.Kind == "set").Skip(before).Select(c => c.Bytes).ToList();
            Assert.Equal(132, sets.Count);
            Assert.Equal("01 EA 08 78 08 00 01 00 00 00 93", Hex(sets[0]));
            Assert.Equal("01 EA 06 78 06 00 00 00 92", Hex(sets[130]));
            Assert.Equal("01 EA 03 78 0A 9B", Hex(sets[131]));

            colors[5] = 0x112233;
            before = io.Calls.Count(c => c.Kind == "set");
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            sets = io.Calls.Where(c => c.Kind == "set").Skip(before).Select(c => c.Bytes).ToList();
            Assert.Equal(new[] { "01 EA 08 78 08 05 01 11 22 33 96", "01 EA 03 78 0A 9B" }, sets.Select(Hex));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void AFailedReport_IsTriedThreeTimes()
        {
            var io = new FakeReportChannel { FailWrites = true };
            var waits = new List<int>();
            var session = new DasKeyboardLighting.Session(waits.Add);
            Assert.False(session.Send(io, DasKeyboardLighting.Apply));
            Assert.Equal(3, waits.Count);
        }
    }
}
