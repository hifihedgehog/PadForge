using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests.MonitorFamilies
{
    /// <summary>The Alienware monitors against OpenRGB's
    /// AlienwareMonitorController (#557).</summary>
    public class AlienwareMonitorLightingTests
    {
        private static OpenRgbRegistration Registration(string name)
            => OpenRgbRegistrations.Of(AlienwareMonitorLighting.OpenRgbFamily).Single(r => r.Name == name);

        [Fact]
        public void OnlyACollectionWithTheFamilysOutputReports_IsListed()
        {
            var dwf = Registration("Alienware AW3423DWF");
            var qf = Registration("Alienware AW3225QF");
            Assert.NotNull(AlienwareMonitorLighting.Instance.Match(MonitorTest.CollectionOf(dwf, output: 193)));
            Assert.Null(AlienwareMonitorLighting.Instance.Match(MonitorTest.CollectionOf(dwf, output: 192)));
            Assert.NotNull(AlienwareMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qf, output: 65)));
            Assert.Null(AlienwareMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qf, output: 64)));
            var model = AlienwareMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qf));
            Assert.Equal(50, model.RepaintMs);
            Assert.Equal(0, model.HoldMs);
        }

        /// <summary>Logo 01, number 02, power button 08
        /// (RGBController_AlienwareMonitor.cpp:54-96).</summary>
        [Fact]
        public void TheLights_AreLogoNumberAndPowerButton()
        {
            var lights = AlienwareMonitorLighting.Lights();
            Assert.Equal(new[] { 0x01, 0x02, 0x08 }, lights.Select(l => l.Address));
            Assert.Equal(new[] { LightKind.Logo, LightKind.Other, LightKind.Status }, lights.Select(l => l.Kind));
        }

        /// <summary>Initialize (AlienwareMonitorController.cpp:91-146): three
        /// reports, each followed by 50 ms.</summary>
        [Fact]
        public void AnAw3225QfSession_StartsWithInitialize()
        {
            var io = new MonitorChannel();
            var model = AlienwareMonitorLighting.Instance.Match(MonitorTest.CollectionOf(Registration("Alienware AW3225QF")));
            var session = model.Begin(io, io.Wait);
            Assert.NotNull(session);
            Assert.Equal("O W50 O W50 O W50", io.Kinds());
            var sent = io.Sent("O");
            Assert.Equal(MonitorTest.Report("00 95 00 00 00", 65, 0xFF), MonitorTest.Hex(sent[0]));
            Assert.Equal(MonitorTest.Report("00 92 37 08 00 51 85 01 FE 03 00 06 40", 65, 0xFF), MonitorTest.Hex(sent[1]));
            Assert.Equal(MonitorTest.Report("00 93 37 12", 65), MonitorTest.Hex(sent[2]));
            Assert.Null(model.Begin(new MonitorChannel { FailAfter = 1 }, _ => { }));
        }

        /// <summary>SendColor with fillInChecksum: 110 XOR bytes 5 to 13
        /// (AlienwareMonitorController.cpp:47-89).</summary>
        [Fact]
        public void AnAw3225QfColor_CarriesTheChecksum()
        {
            Assert.Equal(MonitorTest.Report("00 92 37 0A 00 51 87 D0 04 01 11 22 33 64 09", 65, 0xFF),
                MonitorTest.Hex(AlienwareMonitorLighting.QfColorFrame(0x01, 0x112233)));
            Assert.Equal(MonitorTest.Report("00 92 37 0A 00 51 87 D0 04 0B FF 00 00 64 FC", 65, 0xFF),
                MonitorTest.Hex(AlienwareMonitorLighting.QfColorFrame(0x0B, 0xFF0000)));
        }

        /// <summary>DeviceUpdateLEDs (RGBController_AlienwareMonitor.cpp:98-119):
        /// three equal colors go to 0B at once, others one per light.</summary>
        [Fact]
        public void APicture_IsOneWriteForOneColor_AndThreeOtherwise()
        {
            var io = new MonitorChannel();
            var session = new AlienwareMonitorLighting.QfSession();
            var lights = AlienwareMonitorLighting.Lights();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 0x102030, 0x102030, 0x102030 }));
            Assert.Equal("O W50", io.Kinds());
            Assert.Equal("0B 10 20 30", MonitorTest.Hex(io.Sent("O")[0], 9, 4));

            io = new MonitorChannel();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 0x010101, 0x020202, 0x080808 }));
            Assert.Equal("O W50 O W50 O W50", io.Kinds());
            Assert.Equal(new[] { "01 01 01 01", "02 02 02 02", "08 08 08 08" },
                io.Sent("O").Select(f => MonitorTest.Hex(f, 9, 4)));
            // The lights reach their LEDs by address, in any order.
            io = new MonitorChannel();
            session.Paint(io, io.Wait, lights.Reverse().ToArray(), new[] { 0x080808, 0x020202, 0x010101 });
            Assert.Equal(new[] { "01 01 01 01", "02 02 02 02", "08 08 08 08" },
                io.Sent("O").Select(f => MonitorTest.Hex(f, 9, 4)));
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new MonitorChannel { FailAfter = 1 }, _ => { }, lights, new[] { 1, 2, 3 }));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>GenerateKey (AlienwareAW3423DWFController.cpp:93-158),
        /// worked by hand: the OEM key folds to CA FE 7D 7E 48 74 33 F4.
        /// Bytes 0 and 15 with odd parity take bytes 0 to 7 with byte
        /// (byte 14 AND 7) mixed with the one eight on. Even parity takes
        /// bytes 8 to 15 with byte (byte 6 AND 7) mixed with the one eight
        /// back.</summary>
        [Fact]
        public void TheAw3423DwfKey_IsGenerateKeys()
        {
            var odd = MonitorTest.Bytes("01 02 03 04 05 06 07 08 10 20 30 40 50 60 70 00 99");
            Assert.Equal("DB FC 7E 7A 4D 72 34 FC", MonitorTest.Hex(AlienwareMonitorLighting.GenerateKey(odd)));
            var even = MonitorTest.Bytes("00 11 22 33 44 55 03 77 80 81 82 83 84 85 86 03 99");
            Assert.Equal("4A 7F FF CE CC F1 B5 F7", MonitorTest.Hex(AlienwareMonitorLighting.GenerateKey(even)));
            Assert.Equal("CA FE 7D 7E 48 74 33 F4", MonitorTest.Hex(AlienwareMonitorLighting.GenerateKey(new byte[17])));
        }

        /// <summary>SendColor (lines 62-91): a login, 40 E1 01, the input
        /// report, 40 E1 02 with the key at data byte 64, then the color, and
        /// 200 ms.</summary>
        [Fact]
        public void AnAw3423DwfColor_LogsInFirst()
        {
            var io = new RequestingMonitorChannel { OutputReportLength = 193, InputReportLength = 193 };
            var model = AlienwareMonitorLighting.Instance.Match(
                MonitorTest.CollectionOf(Registration("Alienware AW3423DWF")));
            var session = model.Begin(io, io.Wait);
            Assert.NotNull(session);
            Assert.Empty(io.Log);
            var input = new byte[193];
            MonitorTest.Bytes("01 02 03 04 05 06 07 08 10 20 30 40 50 60 70 00 99").CopyTo(input, 1);
            io.Requested.Enqueue(input);
            var lights = AlienwareMonitorLighting.Lights();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 0x445566, 0x445566, 0x445566 }));
            Assert.Equal("O R O O W200", io.Kinds());
            var sent = io.Sent("O");
            Assert.All(sent, f => Assert.Equal(193, f.Length));
            Assert.Equal(MonitorTest.Report("00 40 E1 01", 193), MonitorTest.Hex(sent[0]));
            Assert.Equal((byte)193, io.Log[1].Bytes[1]);
            var login = new byte[193];
            MonitorTest.Bytes("00 40 E1 02").CopyTo(login, 0);
            MonitorTest.Bytes("DB FC 7E 7A 4D 72 34 FC").CopyTo(login, 65);
            Assert.Equal(MonitorTest.Hex(login), MonitorTest.Hex(sent[1]));
            var color = new byte[193];
            MonitorTest.Bytes("00 40 C6").CopyTo(color, 0);
            color[7] = 0x0A;
            color[9] = 0x6E;
            color[11] = 0x82;
            MonitorTest.Bytes("51 87 D0 04 0B 44 55 66 64 FC").CopyTo(color, 65);
            Assert.Equal(MonitorTest.Hex(color), MonitorTest.Hex(sent[2]));
        }

        /// <summary>The second ID of each light (lines 64-66).</summary>
        [Theory]
        [InlineData(0x01, 0xF6)]
        [InlineData(0x02, 0xF5)]
        [InlineData(0x08, 0xFF)]
        [InlineData(0x0B, 0xFC)]
        public void AnAw3423DwfColor_CarriesTheLightsSecondId(int led, int second)
        {
            var frame = AlienwareMonitorLighting.DwfColorFrame((byte)led, 0x010203);
            Assert.Equal($"{led:X2} 01 02 03 64 {second:X2}", MonitorTest.Hex(frame, 69, 6));
        }

        /// <summary>The shared channel reads no input report on request, so an
        /// AW3423DWF session does not begin on it, and nothing goes out.</summary>
        [Fact]
        public void AnAw3423DwfSession_DoesNotBegin_WithoutAnInputReportRead()
        {
            var io = new MonitorChannel { OutputReportLength = 193 };
            var model = AlienwareMonitorLighting.Instance.Match(
                MonitorTest.CollectionOf(Registration("Alienware AW3423DWF")));
            Assert.Null(model.Begin(io, io.Wait));
            Assert.Empty(io.Log);
            Assert.False(typeof(IHidInputReportReader).IsAssignableFrom(typeof(HidReportChannel)));
            var session = new AlienwareMonitorLighting.DwfSession();
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, io.Wait, AlienwareMonitorLighting.Lights(), new[] { 1, 1, 1 }));
            Assert.Empty(io.Log);
            // A read that fails stops the picture before the key goes out.
            var failing = new RequestingMonitorChannel();
            Assert.Equal(HidLightingPaint.Failed, session.Paint(failing, failing.Wait, AlienwareMonitorLighting.Lights(), new[] { 1, 1, 1 }));
            Assert.Equal("O R", failing.Kinds());
        }
    }
}
