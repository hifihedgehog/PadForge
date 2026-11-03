using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A Raise's serial port, scripted as its firmware answers:
    /// every line it is sent is logged, and each is answered as
    /// FocusSerial answers, "led.mode" with the mode and a space, then
    /// "\r\n.\r\n" (Kaleidoscope-Dygma FocusSerial.cpp:53, FocusSerial.h:43-46,
    /// LEDControl.cpp:375-381).</summary>
    internal sealed class FakeSerialLine : ISerialLineChannel
    {
        public readonly List<string> Lines = new();
        private readonly Queue<byte> _pending = new();
        public int Mode = 3;
        public bool Mute;
        public bool FailWrites;
        public bool FailReads;
        public bool Disposed;

        /// <summary>How many bytes a read hands over at most, so an answer
        /// comes in pieces.</summary>
        public int Chunk = 3;

        public bool Write(byte[] bytes)
        {
            if (FailWrites) return false;
            string line = Encoding.ASCII.GetString(bytes);
            Lines.Add(line);
            if (Mute) return true;
            string answer = line == "led.mode\n" ? Mode + " \r\n.\r\n" : "\r\n.\r\n";
            if (line.StartsWith("led.mode ", StringComparison.Ordinal)) Mode = int.Parse(line.Substring(9).Trim());
            foreach (byte b in Encoding.ASCII.GetBytes(answer)) _pending.Enqueue(b);
            return true;
        }

        public int Read(byte[] buffer)
        {
            if (FailReads) return -1;
            int n = 0;
            while (n < buffer.Length && n < Chunk && _pending.Count > 0) buffer[n++] = _pending.Dequeue();
            return n;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>The Dygma Raise family against OpenRGB's DygmaRaiseController
    /// and the Raise firmware's Focus commands (#557).</summary>
    public class DygmaRaiseLightingTests
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        private static DygmaRaiseLighting Family(FakeSerialLine port = null, IReadOnlyList<ComPort> ports = null,
            List<(string, byte[], int)> shots = null)
            => new(() => ports ?? Array.Empty<ComPort>(), _ => Box, _ => port,
                (path, bytes, ms) => { shots?.Add((path, bytes, ms)); return true; });

        [Fact]
        public void APicture_IsOpenRgbsThemeLine()
        {
            // "led.theme", then " %d" for red, green and blue of each light,
            // then "\n" (DygmaRaiseController.cpp:56-100).
            Assert.Equal("led.theme 1 2 3 255 0 16 0 0 0\n",
                Encoding.ASCII.GetString(DygmaRaiseLighting.ThemeLine(new[] { 0x010203, 0xFF0010, 0 })));
            // The whole keyboard: 132 lights, each "rrr ggg bbb" at most,
            // within OpenRGB's MAX_LEN of 4 x 3 x 132 + 9
            // (DygmaRaiseController.h:23).
            var line = DygmaRaiseLighting.ThemeLine(Enumerable.Repeat(0xFFFFFF, 132).ToArray());
            Assert.Equal(9 + 132 * 12 + 1, line.Length);
            Assert.True(line.Length <= 4 * 3 * 132 + 9 + 1);
            Assert.Equal("led.mode 7\n", Encoding.ASCII.GetString(DygmaRaiseLighting.ModeLine(7)));
            Assert.Equal("led.mode\n", Encoding.ASCII.GetString(DygmaRaiseLighting.Command("led.mode")));
        }

        [Fact]
        public void TheLights_AreOpenRgbsZones_KeysNamedByTheirUsage()
        {
            var lights = DygmaRaiseLighting.Lights();
            // 69 keys, 62 underglow, the Neuron (RGBController_DygmaRaise.cpp:54-59).
            Assert.Equal(132, lights.Count);
            Assert.Equal(Enumerable.Range(0, 132), lights.Select(l => l.Address));
            Assert.Equal(69, DygmaRaiseLighting.Keys.Length);
            Assert.All(lights.Skip(69).Take(62), l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(LightKind.Logo, lights[131].Kind);
            // A few against the HID Usage Tables, by OpenRGB's names.
            Assert.Equal(0x29, lights[0].Usage);   // Escape
            Assert.Equal(0x64, lights[20].Usage);  // \ (ISO)
            Assert.Equal(0x2A, lights[33].Usage);  // Backspace
            Assert.Equal(0x28, lights[40].Usage);  // Enter
            Assert.Equal(0x32, lights[48].Usage);  // #
            Assert.Equal(0xE6, lights[64].Usage);  // Right Alt
            // The thumb keys are keys no source names.
            foreach (int n in new[] { 29, 30, 31, 32, 65, 66, 67, 68 })
            {
                Assert.Equal(LightKind.Key, lights[n].Kind);
                Assert.Equal(0, lights[n].Usage);
                Assert.Equal(LightKeySource.None, lights[n].KeySource);
            }
            // Fn carries its label.
            Assert.Equal(LightKind.Other, lights[63].Kind);
            Assert.Equal(LightLabel.Fn, lights[63].Label);
            // Every other key is named, each once.
            var named = lights.Take(69).Where(l => l.Usage != 0).ToList();
            Assert.Equal(60, named.Count);
            Assert.Equal(60, named.Select(l => l.Usage).Distinct().Count());
            foreach (string name in DygmaRaiseLighting.Keys.Where(k => !k.StartsWith("Key: T", StringComparison.Ordinal) || k == "Key: Tab"))
                Assert.True(OpenRgbKeys.TryGet(name, out _, out _), name);
        }

        [Fact]
        public void TheLook_TakesEveryComPortOfTheRaise_ByOpenRgbsHardwarePrefix()
        {
            var ports = new[]
            {
                new ComPort("COM3", "Raise (COM3)", @"USB\VID_1209&PID_2201\6&1A2B3C4D&0&2"),
                new ComPort("COM4", "Other (COM4)", @"USB\VID_1209&PID_2202\6&1A2B3C4D&0&3"),
                new ComPort("COM7", "Raise (COM7)", @"USB\VID_1209&PID_2201&MI_00\7&2&0&0000"),
                new ComPort("COM9", "Bluetooth (COM9)", @"BTHENUM\{00001101}\7&9"),
            };
            var found = Family(ports: ports).Look();
            Assert.Equal(new[] { @"\\.\COM3", @"\\.\COM7" }, found.Select(d => d.Key));
            Assert.All(found, d =>
            {
                Assert.Equal((ushort)0x1209, d.VendorId);
                Assert.Equal((ushort)0x2201, d.ProductId);
                Assert.Equal(Box, d.Container);
                Assert.False(d.BuiltInKeyboard);
            });

            // A port whose container cannot be read waits for the next look.
            var noContainer = new DygmaRaiseLighting(() => ports, _ => Guid.Empty, _ => null, null);
            Assert.Empty(noContainer.Look());
            // A port list that could not be read keeps what was found.
            Assert.Null(new DygmaRaiseLighting(() => throw new InvalidOperationException(), _ => Box, _ => null, null).Look());
        }

        [Fact]
        public void ASession_ReadsTheModeFirst_SendsThePicture_AndSetsTheModeAgainOnHandBack()
        {
            var port = new FakeSerialLine { Mode = 5 };
            var shots = new List<(string, byte[], int)>();
            var model = Family(port, shots: shots).Model;
            Assert.Equal(new[] { VendorPrograms.Bazecor }, model.Programs);
            var channel = model.Open(@"\\.\COM3");
            Assert.Same(port, channel);
            var session = Assert.IsType<DygmaRaiseLighting.Session>(model.BeginOwn(channel, _ => { }));
            Assert.Equal(5, session.Mode);
            Assert.Equal(new[] { "led.mode\n" }, port.Lines);
            Assert.Empty(session.CrashFrames);

            var colors = Enumerable.Repeat(0x102030, 132).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, model.Map.Lights, colors));
            Assert.Equal(Encoding.ASCII.GetString(DygmaRaiseLighting.ThemeLine(colors)), port.Lines[^1]);
            Assert.Equal("led.mode 5\n", Encoding.ASCII.GetString(Assert.Single(session.CrashFrames)));

            // The crash path's line goes through a port of its own.
            Assert.True(model.WriteOnce(@"\\.\COM3", session.CrashFrames[0], 200));
            Assert.Equal((@"\\.\COM3", "led.mode 5\n", 200),
                (shots[0].Item1, Encoding.ASCII.GetString(shots[0].Item2), shots[0].Item3));

            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal("led.mode 5\n", port.Lines[^1]);
            Assert.Empty(session.CrashFrames);
            int lines = port.Lines.Count;
            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal(lines, port.Lines.Count);
        }

        [Fact]
        public void AKeyboardThatDoesNotAnswerTheModeRead_GetsNoPicture()
        {
            int waited = 0;
            var port = new FakeSerialLine { Mute = true };
            var model = Family(port).Model;
            Assert.Null(model.BeginOwn(port, ms => waited += ms));
            Assert.True(waited >= DygmaRaiseLighting.AnswerWaitMs);
            Assert.Equal(new[] { "led.mode\n" }, port.Lines);

            // An answer that is no mode.
            Assert.Null(DygmaRaiseLighting.ReadAnswer(new FakeSerialLine { FailReads = true }, _ => { }));
            Assert.Null(model.BeginOwn(new FakeSerialLine { FailWrites = true }, _ => { }));
            Assert.Null(model.BeginOwn(new FakeAtkAcpi(), _ => { }));
        }

        [Fact]
        public void TheAnswer_IsTheLinesBeforeTheLoneDot_AsBazecorReadsIt()
        {
            var port = new FakeSerialLine();
            port.Write(Encoding.ASCII.GetBytes("led.mode\n"));
            Assert.Equal("3", DygmaRaiseLighting.ReadAnswer(port, _ => { }));
            // An empty answer is an answer.
            port.Write(Encoding.ASCII.GetBytes("led.theme 1 2 3\n"));
            Assert.Equal("", DygmaRaiseLighting.ReadAnswer(port, _ => { }));
        }

        [Fact]
        public void AFailedWriteOrAMissingAnswer_FailsThePicture_AndKeepsTheCrashLine()
        {
            var port = new FakeSerialLine();
            var model = Family(port).Model;
            var session = model.BeginOwn(port, _ => { });
            port.Mute = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(null, _ => { }, model.Map.Lights, new int[132]));
            Assert.Single(session.CrashFrames);
            port.FailWrites = true;
            Assert.False(session.HandBack(null, _ => { }));
            Assert.Single(session.CrashFrames);
        }

        [Fact]
        public void TheRealChannels_RefuseInTheTestHost()
        {
            Assert.Null(OwnLightingChannels.OpenSerial(@"\\.\COM250", DygmaRaiseLighting.Baud));
            Assert.False(OwnLightingChannels.SerialWriteOnce(@"\\.\COM250", DygmaRaiseLighting.Baud, new byte[] { 0x0A }, 100));
            Assert.Null(OwnLightingChannels.OpenAtkAcpi());
            Assert.Null(OwnLightingChannels.OpenHpBios());
            Assert.False(OwnLightingChannels.HpBiosCallOnce(0x20009, 3, new byte[128], 100));
        }
    }
}
