using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The haptic protocols behind rumble on mice (#494, asked in discussion
    /// #488). No MX Master 4 or Rival mouse is on the bench, so the device
    /// side runs against a scripted HID++ channel built from the wire format
    /// Solaar, OpenLogi and LiveHaptics agree on, and against a local
    /// GameSense server built from the gamesense-sdk docs. The live test at
    /// the end talks to a real Logitech receiver when PADFORGE_LIVE_HIDPP is
    /// set, past a scope that lifts the test host's refusal, so the class
    /// runs with no other beside it.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidppHapticsTests
    {
        private readonly ITestOutputHelper _output;

        public HidppHapticsTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ── Scripted HID++ channel ──

        internal sealed class FakeChannel : IHidppChannel
        {
            public string Path { get; init; } = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#fake";
            public bool Bluetooth { get; init; }
            public Func<byte, byte, byte, byte[], HidppReply> Answer = (d, f, fn, p) => NoAnswer();
            public volatile bool FailWrites;
            public readonly ConcurrentQueue<(long Ms, byte[] Frame)> Writes = new();
            public readonly ConcurrentQueue<(byte Device, byte Feature, byte Function)> Asked = new();

            /// <summary>How long each request was given for its answer.</summary>
            public readonly ConcurrentQueue<(byte Feature, byte Function, int TimeoutMs)> Waits = new();
            public volatile bool Disposed;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            /// <summary>Everything that went out, in the order it went: "ask"
            /// with a request's frame, "reg" with the short frame of a
            /// receiver register read, "write" with a frame nothing waited
            /// on.</summary>
            public readonly ConcurrentQueue<(string Kind, byte[] Frame)> Traffic = new();

            /// <summary>Runs inside each write, before the frame is recorded,
            /// so a test can read what else is true at that moment.</summary>
            public Action<byte[]> Writing;

            public bool Write(byte[] frame)
            {
                if (FailWrites) return false;
                if (frame[0] == HidppHapticProtocol.VeryLongReportId && !HasVeryLong) return false;
                Writing?.Invoke(frame);
                Writes.Enqueue((_clock.ElapsedMilliseconds, frame));
                Traffic.Enqueue(("write", frame));
                return true;
            }

            /// <summary>The feature ID of every Root.getFeature asked.</summary>
            public readonly ConcurrentQueue<ushort> RootLookups = new();

            public HidppWaiter Waiter { get; set; }

            /// <summary>While set, a request or a register read the script
            /// leaves unanswered waits its timeout out through the worker's
            /// waiter, as the shipping channel does. Unset, it comes back at
            /// once, so a test that waits on nothing takes no time.</summary>
            public volatile bool HoldUnanswered;

            /// <summary>The frame of the request waiting out its timeout
            /// now, or null.</summary>
            public volatile byte[] Waiting;

            private static readonly ManualResetEventSlim s_never = new(false);

            private HidppReply Held(HidppReply reply, byte[] frame, int timeoutMs)
            {
                if (reply.Kind != HidppReplyKind.Timeout || !HoldUnanswered || Waiter == null) return reply;
                Waiting = frame;
                try { Waiter.Wait(s_never, timeoutMs); }
                finally { Waiting = null; }
                return reply;
            }

            /// <summary>Whether the channel reaches a very long collection
            /// (#557). A very long frame on a channel without one fails.</summary>
            public bool HasVeryLong { get; set; }

            public HidppReply RequestFrame(byte[] frame, int timeoutMs)
            {
                if (FailWrites) return new HidppReply(HidppReplyKind.WriteFailed);
                if (frame[0] == HidppHapticProtocol.VeryLongReportId && !HasVeryLong)
                    return new HidppReply(HidppReplyKind.WriteFailed);
                byte device = frame[1], feature = frame[2], function = (byte)(frame[3] >> 4);
                var parameters = frame.Skip(4).ToArray();
                Asked.Enqueue((device, feature, function));
                Waits.Enqueue((feature, function, timeoutMs));
                Traffic.Enqueue(("ask", frame));
                if (feature == 0 && function == 0)
                    RootLookups.Enqueue((ushort)((parameters[0] << 8) | parameters[1]));
                return Held(Answer(device, feature, function, parameters), frame, timeoutMs);
            }

            public HidppReply Request(byte deviceIndex, byte featureIndex, byte function, byte[] parameters, int timeoutMs)
            {
                if (FailWrites) return new HidppReply(HidppReplyKind.WriteFailed);
                Asked.Enqueue((deviceIndex, featureIndex, function));
                Waits.Enqueue((featureIndex, function, timeoutMs));
                byte[] frame = HidppHapticProtocol.Frame(deviceIndex, featureIndex, function, parameters ?? Array.Empty<byte>());
                Traffic.Enqueue(("ask", frame));
                if (featureIndex == 0 && function == 0 && parameters != null && parameters.Length >= 2)
                    RootLookups.Enqueue((ushort)((parameters[0] << 8) | parameters[1]));
                return Held(Answer(deviceIndex, featureIndex, function, parameters ?? Array.Empty<byte>()), frame, timeoutMs);
            }

            /// <summary>The receiver's register records, by register and
            /// sub-register. Unanswered unless a test scripts them.</summary>
            public Func<byte, byte, HidppReply> Register = (r, s) => NoAnswer();
            public readonly ConcurrentQueue<(byte Register, byte Sub)> RegistersAsked = new();

            public HidppReply ReadReceiverRegister(byte register, byte sub, int timeoutMs)
            {
                if (FailWrites) return new HidppReply(HidppReplyKind.WriteFailed);
                RegistersAsked.Enqueue((register, sub));
                byte[] frame = HidppReceiverProtocol.ReadRegister(register, sub);
                Traffic.Enqueue(("reg", frame));
                return Held(Register(register, sub), frame, timeoutMs);
            }

            public void Dispose() => Disposed = true;
        }

        internal static HidppReply NoAnswer() => new(HidppReplyKind.Timeout);

        /// <summary>A frame from its bytes in hex, padded with zeros to the
        /// 20 bytes of a long report, so a test states the bytes on the wire
        /// and builds them through no code under test.</summary>
        internal static byte[] Hex(string bytes, int length = HidppHapticProtocol.LongReportLength)
        {
            var frame = new byte[length];
            string[] parts = bytes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) frame[i] = Convert.ToByte(parts[i], 16);
            return frame;
        }

        /// <summary>A frame as hex without its trailing zeros, the form the
        /// references print one in.</summary>
        internal static string Text(byte[] frame)
        {
            int length = frame.Length;
            while (length > 1 && frame[length - 1] == 0) length--;
            return BitConverter.ToString(frame, 0, length).Replace('-', ' ');
        }

        /// <summary>One line per frame of a channel's traffic: its kind,
        /// then its bytes.</summary>
        internal static string[] Lines(IEnumerable<(string Kind, byte[] Frame)> traffic)
            => traffic.Select(t => t.Kind + " " + Text(t.Frame)).ToArray();

        /// <summary>An answer report: the long header with PadForge's
        /// software ID, then the payload.</summary>
        /// <summary>A very long answer, as a G810 gives its key list (#557).</summary>
        internal static HidppReply AnsVeryLong(byte device, byte feature, byte function, params byte[] payload)
        {
            var report = new byte[64];
            report[0] = 0x12;
            report[1] = device;
            report[2] = feature;
            report[3] = (byte)((function << 4) | HidppHapticProtocol.SoftwareId);
            payload.AsSpan(0, Math.Min(payload.Length, 60)).CopyTo(report.AsSpan(4));
            return new HidppReply(HidppReplyKind.Answer, 0, report);
        }

        internal static HidppReply Ans(byte device, byte feature, byte function, params byte[] payload)
        {
            var report = new byte[20];
            report[0] = 0x11;
            report[1] = device;
            report[2] = feature;
            report[3] = (byte)((function << 4) | HidppHapticProtocol.SoftwareId);
            payload.AsSpan(0, Math.Min(payload.Length, 16)).CopyTo(report.AsSpan(4));
            return new HidppReply(HidppReplyKind.Answer, 0, report);
        }

        /// <summary>A haptic mouse at one device index, answering the way the
        /// references describe: 0x19B0 at feature index 0x0B, the mask
        /// big-endian in payload bytes 4 to 7, the enable flag in bit 0, and
        /// its name and type through feature 0x0005 at index 0x03 (Solaar's
        /// table: 3 is a mouse).</summary>
        internal static Func<byte, byte, byte, byte[], HidppReply> Mouse(byte deviceIndex,
            string name = "MX Master 4", uint mask = 0x7FFF, bool enabled = true, byte type = 3)
        {
            const byte haptic = 0x0B, nameFeature = 0x03;
            return (d, f, fn, p) =>
            {
                if (d != deviceIndex) return NoAnswer();
                if (f == nameFeature && fn == 2)
                    return Ans(d, f, 2, type);
                if (f == 0 && fn == 0)
                {
                    int id = (p[0] << 8) | p[1];
                    return Ans(d, 0, 0, id == 0x19B0 ? haptic : id == 0x0005 ? nameFeature : (byte)0);
                }
                if (f == haptic && fn == 0)
                    return Ans(d, f, 0, 0, 0, 0, 0, (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask);
                if (f == haptic && fn == 1)
                    return Ans(d, f, 1, (byte)(enabled ? 1 : 0), 60, 0);
                if (f == nameFeature && fn == 0)
                    return Ans(d, f, 0, (byte)Encoding.UTF8.GetByteCount(name));
                if (f == nameFeature && fn == 1)
                    return Ans(d, f, 1, Encoding.UTF8.GetBytes(name).Skip(p[0]).Take(16).ToArray());
                return NoAnswer();
            };
        }

        internal static string MissingCoreProps()
            => Path.Combine(Path.GetTempPath(), "padforge-no-gg-" + Guid.NewGuid().ToString("N"), "coreProps.json");

        // ── The shaper ──

        [Theory]
        [InlineData(0f, 0, 0)]
        [InlineData(0.04f, 0, 0)]    // under the light threshold
        [InlineData(0.05f, 0, 1)]
        [InlineData(0.04f, 1, 1)]    // held by hysteresis
        [InlineData(0.02f, 1, 0)]
        [InlineData(0.33f, 1, 2)]
        [InlineData(0.31f, 2, 2)]
        [InlineData(0.29f, 2, 1)]
        [InlineData(0.66f, 2, 3)]
        [InlineData(0.64f, 3, 3)]
        [InlineData(0.62f, 3, 2)]
        [InlineData(1f, 0, 3)]
        [InlineData(0f, 3, 0)]
        public void Level_ClimbsAtTheOnThresholds_AndFallsAtTheOffOnes(float amplitude, int current, int expected)
        {
            Assert.Equal(expected, HapticRumbleShaper.Level(amplitude, current));
        }

        [Fact]
        public void Interval_RunsFrom250MsDownTo80Ms()
        {
            Assert.Equal(250, HapticRumbleShaper.IntervalMs(0f));
            Assert.Equal(250, HapticRumbleShaper.IntervalMs(HapticRumbleShaper.LightOn));
            Assert.Equal(80, HapticRumbleShaper.IntervalMs(1f));
            Assert.Equal(80, HapticRumbleShaper.IntervalMs(2f));
            int previous = int.MaxValue;
            for (float a = 0f; a <= 1f; a += 0.01f)
            {
                int interval = HapticRumbleShaper.IntervalMs(a);
                Assert.True(interval <= previous, $"interval rose at {a}");
                previous = interval;
            }
        }

        [Fact]
        public void Waveform_PicksTheCollisionForTheLevel_AndFallsBackToWhatTheMouseLists()
        {
            const uint all = (1u << 2) | (1u << 3) | (1u << 4);
            Assert.Null(HapticRumbleShaper.Waveform(0, all));
            Assert.Equal((byte)0x04, HapticRumbleShaper.Waveform(1, all));   // subtle
            Assert.Equal((byte)0x03, HapticRumbleShaper.Waveform(2, all));   // damp
            Assert.Equal((byte)0x02, HapticRumbleShaper.Waveform(3, all));   // sharp

            // OpenLogi's two Actions Ring waveforms, damp state change (1)
            // and subtle collision (4): every level falls to subtle.
            const uint ring = (1u << 1) | (1u << 4);
            Assert.Equal((byte)0x04, HapticRumbleShaper.Waveform(3, ring));
            Assert.Equal((byte)0x04, HapticRumbleShaper.Waveform(1, ring));

            Assert.Equal((byte)0x03, HapticRumbleShaper.Waveform(3, 1u << 3));
            Assert.Equal((byte)0x02, HapticRumbleShaper.Waveform(1, 1u << 2));
            Assert.Null(HapticRumbleShaper.Waveform(3, (1u << 0) | (1u << 1) | (1u << 14)));
        }

        // ── HID++ frames ──

        [Fact]
        public void Frames_AreLongReportsWithPadForgesSoftwareId()
        {
            // Root.getFeature(0x19B0) to a receiver's second slot.
            Assert.Equal(
                new byte[] { 0x11, 0x02, 0x00, 0x0C, 0x19, 0xB0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                HidppHapticProtocol.GetFeature(0x02, 0x19B0));
            // Function 4, play, as OpenLogi documents it: the waveform and
            // two zeros. 0x4C is function 4 with software ID 0x0C.
            Assert.Equal(
                new byte[] { 0x11, 0xFF, 0x0B, 0x4C, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                HidppHapticProtocol.Play(0xFF, 0x0B, HidppHapticProtocol.SharpCollision));
            // Solaar's table and LiveHaptics: nobody else claims 0x0C.
            Assert.DoesNotContain(HidppHapticProtocol.SoftwareId, new byte[] { 0x01, 0x07, 0x0A, 0x0B, 0x0D, 0x0F });
        }

        [Fact]
        public void Match_TakesOurAnswersAndErrors_AndNothingElse()
        {
            byte[] answer = { 0x11, 0x02, 0x00, 0x0C, 0x0B, 0, 0 };
            Assert.Equal(HidppReplyKind.Answer, HidppHapticProtocol.Match(answer, 0x02, 0x00, 0, out _));

            // Another tool's software ID (Solaar's 0x0B) on the same request.
            byte[] solaar = { 0x11, 0x02, 0x00, 0x0B, 0x0B, 0, 0 };
            Assert.Equal(HidppReplyKind.None, HidppHapticProtocol.Match(solaar, 0x02, 0x00, 0, out _));

            // Another device index, another feature, a short report.
            Assert.Equal(HidppReplyKind.None, HidppHapticProtocol.Match(answer, 0x01, 0x00, 0, out _));
            Assert.Equal(HidppReplyKind.None, HidppHapticProtocol.Match(answer, 0x02, 0x05, 0, out _));
            byte[] shortReport = { 0x10, 0x02, 0x00, 0x0C, 0x0B, 0, 0 };
            Assert.Equal(HidppReplyKind.None, HidppHapticProtocol.Match(shortReport, 0x02, 0x00, 0, out _));

            // A HID++ 2.0 error: 0xFF, our feature index and function byte, the code.
            byte[] busy = { 0x11, 0x02, 0xFF, 0x0B, 0x1C, 0x08, 0 };
            Assert.Equal(HidppReplyKind.Error, HidppHapticProtocol.Match(busy, 0x02, 0x0B, 1, out byte code));
            Assert.Equal(HidppHapticProtocol.ErrorBusy, code);
        }

        [Fact]
        public void Capabilities_ReadTheMaskBigEndian_AndConfigurationReadsBitZero()
        {
            var caps = Ans(0xFF, 0x0B, 0, 0xAA, 0xBB, 0xCC, 0xDD, 0x00, 0x00, 0x40, 0x1C);
            Assert.Equal(0x0000401Cu, HidppHapticProtocol.WaveformMask(caps));
            Assert.True(HidppHapticProtocol.FeedbackEnabled(Ans(0xFF, 0x0B, 1, 0x01, 50, 0x0A)));
            Assert.False(HidppHapticProtocol.FeedbackEnabled(Ans(0xFF, 0x0B, 1, 0x00, 50, 0x0A)));
        }

        [Fact]
        public void BluetoothPaths_GetTheLongerWindow()
        {
            const string ble = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b042&col03#9&1&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string usb = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&2&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            Assert.True(HidppHapticProtocol.IsBluetoothPath(ble));
            Assert.False(HidppHapticProtocol.IsBluetoothPath(usb));
            Assert.Equal(700, HidppHapticProtocol.TimeoutMs(true));
            Assert.Equal(300, HidppHapticProtocol.TimeoutMs(false));
        }

        // ── Discovery ──

        [Fact]
        public void Probe_FindsTheMouseBehindAReceiver_AndSettlesWhatAnswered()
        {
            var keyboardAndMouse = Mouse(0x02);
            var channel = new FakeChannel
            {
                Answer = (d, f, fn, p) => d == 0x01 && f == 0 && fn == 0
                    ? Ans(d, 0, 0, 0)                // a keyboard in slot 1: no 0x19B0
                    : keyboardAndMouse(d, f, fn, p),
            };
            var state = new HidppPathState();

            var found = HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 1000);

            var mouse = Assert.Single(found);
            Assert.Equal(0x02, mouse.DeviceIndex);
            Assert.Equal(0x0B, mouse.HapticIndex);
            Assert.Equal(0x7FFFu, mouse.WaveformMask);
            Assert.True(mouse.FeedbackEnabled);
            Assert.Equal("MX Master 4", mouse.Name);
            Assert.True(state.Receiver);
            Assert.False(state.Direct);
            Assert.True(state.Settled[1]);
            Assert.True(state.Settled[2]);
            Assert.False(state.Settled[3]);
            Assert.True(state.RetryAt[3] > 1000, "an empty slot backs off");

            // Inside the backoff nothing is asked again.
            int asked = channel.Asked.Count;
            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 2000));
            Assert.Equal(asked, channel.Asked.Count);

            // After it, only the open slots are asked, and 0xFF no longer is:
            // the receiver answered on a slot.
            while (channel.Asked.TryDequeue(out _)) { }
            HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 1000 + HidppHapticProbe.FirstRetryMs);
            var again = channel.Asked.Select(a => a.Device).Distinct().OrderBy(x => x).ToArray();
            Assert.Equal(new byte[] { 3, 4, 5, 6 }, again);
        }

        [Fact]
        public void Probe_ABluetoothMouseIsTheDeviceItself()
        {
            var channel = new FakeChannel { Bluetooth = true, Answer = Mouse(0xFF, "MX Master 4 Mac") };
            var state = new HidppPathState();

            var mouse = Assert.Single(HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 0));

            Assert.Equal(0xFF, mouse.DeviceIndex);
            Assert.Equal("MX Master 4 Mac", mouse.Name);
            Assert.True(state.Direct);
            Assert.DoesNotContain(channel.Asked, a => a.Device != 0xFF);
        }

        [Fact]
        public void Probe_ASilentBluetoothPathAsksNoSlots()
        {
            var channel = new FakeChannel { Bluetooth = true };
            var state = new HidppPathState();
            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 0));
            Assert.All(channel.Asked, a => Assert.Equal(0xFF, a.Device));
            Assert.True(state.RetryAt[0] > 0);
        }

        /// <summary>A silent slot is asked again after 5, then 10, then 15
        /// seconds, and never waits longer, so a mouse that wakes is found
        /// within about 15 seconds.</summary>
        [Fact]
        public void Probe_ASilentSlotBacksOffToFifteenSeconds()
        {
            var channel = new FakeChannel { Bluetooth = true };
            var state = new HidppPathState();
            long now = 0;
            var waits = new List<long>();
            for (int i = 0; i < 6; i++)
            {
                HidppUnitProbe.Probe(channel, state, Guid.Empty, now);
                waits.Add(state.RetryAt[0] - now);
                now = state.RetryAt[0];
            }
            Assert.Equal(new long[] { 5000, 10000, 15000, 15000, 15000, 15000 }, waits);
        }

        [Fact]
        public void Probe_ADeviceWithoutTheFeatureIsSettled()
        {
            var channel = new FakeChannel { Answer = (d, f, fn, p) => d == 0xFF ? Ans(d, 0, 0, 0) : NoAnswer() };
            var state = new HidppPathState();
            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 0));
            Assert.True(state.Direct);
            Assert.True(state.Settled[0]);
            Assert.DoesNotContain(channel.Asked, a => a.Device != 0xFF);
        }

        [Fact]
        public void Probe_AFailedWriteMarksThePathDead()
        {
            var channel = new FakeChannel { FailWrites = true };
            var state = new HidppPathState();
            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 0));
            Assert.True(state.Dead);
        }

        [Fact]
        public void Probe_AMissingCapabilityAnswerIsRetried_NotSettled()
        {
            var mouse = Mouse(0xFF);
            var channel = new FakeChannel { Answer = (d, f, fn, p) => f == 0x0B && fn == 0 ? NoAnswer() : mouse(d, f, fn, p) };
            var state = new HidppPathState();
            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, now: 0));
            Assert.False(state.Settled[0]);
            Assert.True(state.RetryAt[0] > 0);
        }

        [Fact]
        public void ReadName_JoinsFragmentsLikeSolaar()
        {
            var channel = new FakeChannel { Answer = Mouse(0x01, "Logitech MX Master 4 for Business") };
            Assert.Equal("Logitech MX Master 4 for Business", HidppHapticProbe.ReadName(channel, 0x01, 100));
            var nameless = new FakeChannel { Answer = (d, f, fn, p) => Ans(d, 0, 0, 0) };
            Assert.Null(HidppHapticProbe.ReadName(nameless, 0x01, 100));
        }

        [Fact]
        public void ReadFeedbackEnabled_IsNullForASilentDevice()
        {
            Assert.False(HidppHapticProbe.ReadFeedbackEnabled(new FakeChannel { Answer = Mouse(0x01, enabled: false) }, 0x01, 0x0B));
            Assert.True(HidppHapticProbe.ReadFeedbackEnabled(new FakeChannel { Answer = Mouse(0x01) }, 0x01, 0x0B));
            Assert.Null(HidppHapticProbe.ReadFeedbackEnabled(new FakeChannel(), 0x01, 0x0B));
        }

        // ── GameSense ──

        internal sealed class FakeGameSense : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly Thread _thread;
            private volatile bool _running = true;
            public readonly ConcurrentQueue<(string Path, string Body)> Posts = new();
            public string Address { get; }
            public volatile int Status = 200;

            /// <summary>The status for one post by its path and body, or null
            /// for <see cref="Status"/>.</summary>
            public Func<string, string, int?> StatusFor;

            /// <summary>Runs on the server's thread with a post's path, after
            /// the post is recorded and before it is answered, so a test can
            /// hold the caller inside its call.</summary>
            public Action<string> BeforeAnswer;

            public FakeGameSense()
            {
                var rng = new Random();
                for (int attempt = 0; ; attempt++)
                {
                    int port = 20000 + rng.Next(20000);
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    try { l.Start(); }
                    catch (HttpListenerException) when (attempt < 10) { continue; }
                    _listener = l;
                    Address = $"127.0.0.1:{port}";
                    break;
                }
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            private void Serve()
            {
                while (_running)
                {
                    HttpListenerContext ctx;
                    try { ctx = _listener.GetContext(); }
                    catch { break; }
                    string body;
                    // A stop, or a caller that left, can end this read too.
                    // An exception here is on the server's own thread, where
                    // nothing but the test host would catch it.
                    try
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        body = reader.ReadToEnd();
                    }
                    catch { continue; }
                    Posts.Enqueue((ctx.Request.Url.AbsolutePath, body));
                    try { BeforeAnswer?.Invoke(ctx.Request.Url.AbsolutePath); } catch { }
                    try
                    {
                        ctx.Response.StatusCode = StatusFor?.Invoke(ctx.Request.Url.AbsolutePath, body) ?? Status;
                        byte[] bytes = Encoding.UTF8.GetBytes("{}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.OutputStream.Close();
                    }
                    catch { }
                }
            }

            public string WriteCoreProps()
            {
                string dir = Path.Combine(Path.GetTempPath(), "padforge-gg-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "coreProps.json");
                File.WriteAllText(file, "{\"address\":\"" + Address + "\",\"encryptedAddress\":\"127.0.0.1:1\"}");
                return file;
            }

            public List<string> Paths() => Posts.Select(p => p.Path).ToList();

            /// <summary>The rumble levels posted, in order.</summary>
            public List<int> EventValues() => Posts.Where(p => p.Path == "/game_event" && EventOf(p.Body) == "RUMBLE")
                .Select(p => JsonDocument.Parse(p.Body).RootElement.GetProperty("data").GetProperty("value").GetInt32())
                .ToList();

            /// <summary>The colors posted to one color event, in order, as
            /// 0x00RRGGBB.</summary>
            public List<int> ColorValues(string evt) => Posts
                .Where(p => p.Path == "/game_event" && EventOf(p.Body) == evt)
                .Select(p =>
                {
                    var c = JsonDocument.Parse(p.Body).RootElement.GetProperty("data").GetProperty("frame").GetProperty("color");
                    return (c.GetProperty("red").GetInt32() << 16) | (c.GetProperty("green").GetInt32() << 8)
                           | c.GetProperty("blue").GetInt32();
                })
                .ToList();

            /// <summary>The events posted to one endpoint, in order.</summary>
            public List<string> EventsAt(string path) => Posts.Where(p => p.Path == path).Select(p => EventOf(p.Body)).ToList();

            private static string EventOf(string body)
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
            }

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }

            /// <summary>True once the server's thread has ended.</summary>
            public bool Ended(int withinMs) => _thread.Join(withinMs);
        }

        [Fact]
        public void GameSense_ReadsTheAddress()
        {
            Assert.True(GameSenseClient.TryReadAddress("{\"address\":\"127.0.0.1:51248\"}", out string a));
            Assert.Equal("127.0.0.1:51248", a);
            Assert.False(GameSenseClient.TryReadAddress("{\"address\":\"\"}", out _));
            Assert.False(GameSenseClient.TryReadAddress("{\"address\":51248}", out _));
            Assert.False(GameSenseClient.TryReadAddress("[]", out _));
            Assert.False(GameSenseClient.TryReadAddress("not json", out _));
            Assert.Null(GameSenseClient.ReadAddress(MissingCoreProps()));
        }

        /// <summary>The handler JSON parses, targets the tactile device on
        /// zone one, plays nothing at 0, and every repeating pulse ends
        /// before its next repeat (the doc's queuing warning).</summary>
        [Fact]
        public void GameSense_TheHandlerPlaysNothingAtZero_AndNoPulseOutlastsItsRepeat()
        {
            using var doc = JsonDocument.Parse(GameSenseClient.BindBody);
            var root = doc.RootElement;
            Assert.Equal("PADFORGE", root.GetProperty("game").GetString());
            Assert.Equal("RUMBLE", root.GetProperty("event").GetString());
            var handler = Assert.Single(root.GetProperty("handlers").EnumerateArray().ToList());
            Assert.Equal("tactile", handler.GetProperty("device-type").GetString());
            Assert.Equal("one", handler.GetProperty("zone").GetString());
            Assert.Equal("vibrate", handler.GetProperty("mode").GetString());

            var ranges = handler.GetProperty("pattern").EnumerateArray().ToList();
            Assert.Equal(0, ranges[0].GetProperty("pattern").GetArrayLength());
            var frequencies = handler.GetProperty("rate").GetProperty("frequency").EnumerateArray().ToList();
            for (int level = 0; level <= 3; level++)
            {
                int value = GameSenseClient.LevelValue(level);
                var range = ranges.Single(r => r.GetProperty("low").GetInt32() <= value && value <= r.GetProperty("high").GetInt32());
                var pulses = range.GetProperty("pattern").EnumerateArray().ToList();
                var rate = frequencies.SingleOrDefault(r => r.GetProperty("low").GetInt32() <= value && value <= r.GetProperty("high").GetInt32());
                if (level == 0)
                {
                    Assert.Empty(pulses);
                    Assert.Equal(JsonValueKind.Undefined, rate.ValueKind);
                    continue;
                }
                int length = Assert.Single(pulses).GetProperty("length-ms").GetInt32();
                int hz = rate.GetProperty("frequency").GetInt32();
                Assert.True(length < 1000 / hz, $"level {level}: a {length} ms pulse repeats every {1000 / hz} ms");
            }
            Assert.True(GameSenseClient.LevelValue(1) < GameSenseClient.LevelValue(2));
            Assert.True(GameSenseClient.LevelValue(2) < GameSenseClient.LevelValue(3));
        }

        [Fact]
        public void GameSense_BindsOnce_PostsOnlyChanges_BeatsWhileRumbleHolds_AndStops()
        {
            using var server = new FakeGameSense();
            using var client = new GameSenseClient(server.WriteCoreProps(), 10000);
            Assert.True(client.TryConnect(now: 0));
            // The connect also removes every color event a dropped session
            // left bound (#494), and the light event of each type that has
            // lights (#557).
            var removed = new[] { "COLOR_MOUSE", "LIGHTS_MOUSE", "COLOR_KEYBOARD", "LIGHTS_KEYBOARD", "COLOR_HEADSET" };
            Assert.Equal(new[] { "mouse", "keyboard", "headset" }, PadForge.Common.Input.Peripherals.PeripheralLinker.GameSenseColorTypes);
            Assert.Equal(new[] { "/game_metadata", "/bind_game_event" }.Concat(removed.Select(_ => "/remove_game_event")),
                server.Paths());
            Assert.Equal(removed, server.EventsAt("/remove_game_event"));

            Assert.True(client.Render(2, now: 10));
            Assert.True(client.Render(2, now: 20));            // same level: nothing posted
            Assert.Equal(new[] { 50 }, server.EventValues());

            Assert.True(client.Render(2, now: 10 + GameSenseClient.HeartbeatMs));
            Assert.Equal("/game_heartbeat", server.Paths().Last());

            Assert.True(client.Render(0, now: 20 + GameSenseClient.HeartbeatMs));
            Assert.True(client.Render(0, now: 40 + 3 * GameSenseClient.HeartbeatMs));   // no beat in silence
            Assert.Equal(new[] { 50, 0 }, server.EventValues());

            Assert.True(client.Render(3, now: 50 + 3 * GameSenseClient.HeartbeatMs));
            client.Close();
            Assert.Equal(new[] { 50, 0, 84, 0 }, server.EventValues());
            Assert.Equal("/stop_game", server.Paths().Last());
            Assert.False(client.Connected);
        }

        [Fact]
        public void GameSense_ARefusalOrAMissingEngineDoesNotConnect()
        {
            using (var client = new GameSenseClient(MissingCoreProps(), 500))
                Assert.False(client.TryConnect(0));

            using var server = new FakeGameSense { Status = 500 };
            using (var client = new GameSenseClient(server.WriteCoreProps(), 1000))
                Assert.False(client.TryConnect(0));
        }

        [Fact]
        public void GameSense_AnEngineThatStopsAnsweringDisconnects()
        {
            var server = new FakeGameSense();
            // Ten seconds for an answer. Half a second ran out under a full
            // run's load on 2026-10-11, and the connect below read as a
            // refusal. The system refuses a post to an engine that has
            // stopped well inside the ten seconds, so the longer wait
            // changes nothing after the stop.
            using var client = new GameSenseClient(server.WriteCoreProps(), 10000);
            Assert.True(client.TryConnect(0));
            server.Dispose();
            Assert.False(client.Render(1, 10));
            Assert.False(client.Connected);
        }

        /// <summary>The fake engine's thread outlives a stop that lands while
        /// it reads a request. That read was the one step of its loop with
        /// nothing around it, and an exception there has nothing above it but
        /// the test host: a full run on 2026-10-11 ended after 1,999 tests on
        /// an ObjectDisposedException from that line.</summary>
        [Fact]
        public void GameSense_TheFakeEngineOutlivesAStopInsideARequest()
        {
            var server = new FakeGameSense();
            using var caller = new System.Net.Sockets.TcpClient();
            caller.Connect(IPAddress.Loopback, int.Parse(server.Address.Split(':')[1]));
            var stream = caller.GetStream();
            stream.ReadTimeout = 10000;
            // Headers and no body. http.sys hands the request to the server's
            // thread, and answers 100 Continue once that thread asks for the
            // body, which is where the stop finds it.
            byte[] head = Encoding.ASCII.GetBytes("POST /game_event HTTP/1.1\r\nHost: " + server.Address
                + "\r\nContent-Type: application/json\r\nContent-Length: 8\r\nExpect: 100-continue\r\n\r\n");
            stream.Write(head, 0, head.Length);
            var answer = new byte[64];
            int read = stream.Read(answer, 0, answer.Length);
            Assert.StartsWith("HTTP/1.1 100", Encoding.ASCII.GetString(answer, 0, read));

            server.Dispose();
            caller.Close();
            Assert.True(server.Ended(10000), "the fake engine's thread did not end");
            Assert.Empty(server.Posts);
        }

        // ── Live hardware, opt-in ──

        /// <summary>Runs only with PADFORGE_LIVE_HIDPP=1: opens every HID++
        /// long collection on this machine through the shipping channel and
        /// probes it read-only (Root.getFeature and the name, never play). An
        /// awake device without 0x19B0 answers index 0.
        ///
        /// <para>The positive control rides the same window: the receiver's
        /// own record of each slot's device name, HID++ 1.0 register 0xB5
        /// sub 0x40 + slot - 1, which OpenRGB reads with a short request and
        /// which the receiver answers "even when the device is asleep"
        /// (LogitechProtocolCommon.cpp getWirelessDeviceName). Raw taps on
        /// both collections log every report, independent of the parser.</para></summary>
        [Fact]
        public void Live_ProbesTheRealHidppCollections()
        {
            if (Environment.GetEnvironmentVariable("PADFORGE_LIVE_HIDPP") != "1") return;
            using var real = RealHidAccess.AllowForTest();
            var all = VendorHidRuntime.Enumerate() ?? new List<VendorHidCollection>();
            // The collections the HID++ worker itself opens.
            var collections = all.Where(HidppBackend.IsHidppLong).ToList();
            _output.WriteLine($"{collections.Count} HID++ long collection(s)");
            foreach (var collection in collections)
            {
                var raw = new ConcurrentQueue<string>();
                var shortCollection = all.FirstOrDefault(c => c.VendorId == collection.VendorId
                    && c.ProductId == collection.ProductId && c.UsagePage == 0xFF00 && c.Usage == 0x0001);
                using var longTap = new VendorHidReader(collection);
                longTap.ReportReceived += (r, buf, len) => raw.Enqueue("long  " + BitConverter.ToString(buf, 0, len));
                Assert.True(longTap.Open());
                using var shortTap = shortCollection == null ? null : new VendorHidReader(shortCollection);
                if (shortTap != null)
                {
                    shortTap.ReportReceived += (r, buf, len) => raw.Enqueue("short " + BitConverter.ToString(buf, 0, len));
                    Assert.True(shortTap.Open());
                    for (byte slot = 1; slot <= 6; slot++)
                    {
                        byte[] names = { 0x10, 0xFF, 0x83, 0xB5, (byte)(0x40 + slot - 1), 0x00, 0x00 };
                        _output.WriteLine($"control write slot {slot}: {RawHidOutput.Write(shortCollection.Path, names)}");
                        Thread.Sleep(100);
                    }
                }

                using var channel = HidppChannel.Open(collection);
                Assert.NotNull(channel);
                foreach (byte index in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                {
                    var reply = channel.Request(index, 0x00, 0, new byte[] { 0x19, 0xB0 },
                        HidppHapticProtocol.TimeoutMs(channel.Bluetooth));
                    string name = reply.Kind == HidppReplyKind.Answer ? HidppHapticProbe.ReadName(channel, index, 300) : null;
                    _output.WriteLine($"{collection.ProductId:X4} index 0x{index:X2}: {reply.Kind} error=0x{reply.ErrorCode:X2} " +
                                      $"feature=0x{reply.Param(0):X2} name='{name}'");
                }
                Thread.Sleep(200);
                foreach (string line in raw) _output.WriteLine(line);
                if (shortCollection != null) RawHidOutput.ResetDevice(shortCollection.Path);
            }
        }
    }

    /// <summary>
    /// How a request waits for its answer (#494), on the shipping channel
    /// through its test seam: no reader and no device, its frames going to
    /// the test and its reports coming from the test. The waiter's clock is
    /// the test's, moved from the turn the waiter gives between two slices,
    /// so each test runs on one thread and times nothing.
    /// </summary>
    public class HidppChannelWaitTests
    {
        private const string LongPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#wait";
        private const string ShortPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#wait";

        /// <summary>The shipping channel with a waiter on a clock each turn
        /// between slices moves on by <c>stepMs</c>.</summary>
        private sealed class Rig
        {
            public long Now;
            public int Turns;

            /// <summary>Every frame written, where it went and when.</summary>
            public readonly List<(string Path, byte[] Frame, long At)> Written = new();
            public readonly HidppChannel Channel;

            /// <summary>What a turn between two slices does, after the
            /// clock moved.</summary>
            public Action<Rig> Turn;

            /// <summary>What the device does with a frame it was sent.</summary>
            public Action<Rig, byte[]> Sent;

            public Rig(int stepMs)
            {
                Channel = new HidppChannel(LongPath, ShortPath, (path, frame) =>
                {
                    Written.Add((path, frame, Now));
                    Sent?.Invoke(this, frame);
                    return true;
                });
                Channel.Waiter = new HidppWaiter(() => Now, 1, () =>
                {
                    Turns++;
                    Now += stepMs;
                    Turn?.Invoke(this);
                });
            }

            public string[] Frames() => Written.Select(w => HidppHapticsTests.Text(w.Frame)).ToArray();
        }

        /// <summary>A request nobody answers ends when its timeout has
        /// passed on the waiter's clock, with a turn after each slice that
        /// brought nothing.</summary>
        [Fact]
        public void ARequestNobodyAnswers_TimesOutOnTheWaitersClock_WithATurnAfterEachSlice()
        {
            var rig = new Rig(stepMs: 100);

            var reply = rig.Channel.Request(0x02, 0x00, 0, new byte[] { 0x00, 0x05 }, 300);

            Assert.Equal(HidppReplyKind.Timeout, reply.Kind);
            Assert.Equal(3, rig.Turns);
            Assert.Equal(300, rig.Now);
            var sent = Assert.Single(rig.Written);
            Assert.Equal(LongPath, sent.Path);
            Assert.Equal("11 02 00 0C 00 05", HidppHapticsTests.Text(sent.Frame));
        }

        /// <summary>An answer ends the wait at the next slice, with the
        /// timeout far off.</summary>
        [Fact]
        public void AnAnswer_EndsTheWaitAtOnce()
        {
            var rig = new Rig(stepMs: 10);
            rig.Turn = r => { if (r.Turns == 2) r.Channel.Feed(HidppHapticsTests.Hex("11 02 00 0C 0B")); };

            var reply = rig.Channel.Request(0x02, 0x00, 0, new byte[] { 0x19, 0xB0 }, 300);

            Assert.Equal(HidppReplyKind.Answer, reply.Kind);
            Assert.Equal(0x0B, reply.Param(0));
            Assert.Equal(2, rig.Turns);
            Assert.Equal(20, rig.Now);
        }

        /// <summary>A pulse written while a request waits goes out at once,
        /// and nothing a device can say to it ends the request: not the
        /// pulse's own answer, which carries the request's device index,
        /// feature index and software ID with function 4, not the busy
        /// error a device still working on the request gives the pulse
        /// (Logitech's "HID++ 2.0 Draft Specification", p.3), not another
        /// device's answer and not another program's. The request's own
        /// answer then ends it.</summary>
        [Fact]
        public void APulseBesideARequest_GoesOutAtOnce_AndNoAnswerToItEndsTheRequest()
        {
            var rig = new Rig(stepMs: 10);
            bool pulseWritten = false;
            rig.Turn = r =>
            {
                if (r.Turns == 1)
                {
                    pulseWritten = r.Channel.Write(HidppHapticProtocol.Play(0x01, 0x0B, HidppHapticProtocol.SharpCollision));
                    r.Channel.Feed(HidppHapticsTests.Hex("11 01 0B 4C 02"));
                    r.Channel.Feed(HidppHapticsTests.Hex("11 01 FF 0B 4C 08"));
                    r.Channel.Feed(HidppHapticsTests.Hex("11 02 0B 1C 00 32"));
                    r.Channel.Feed(HidppHapticsTests.Hex("11 01 0B 1B 00 32"));
                }
                if (r.Turns == 3) r.Channel.Feed(HidppHapticsTests.Hex("11 01 0B 1C 01 3C"));
            };

            var reply = rig.Channel.Request(0x01, 0x0B, HidppHapticProtocol.FunctionGetConfiguration, Array.Empty<byte>(), 300);

            Assert.True(pulseWritten);
            Assert.Equal(HidppReplyKind.Answer, reply.Kind);
            Assert.Equal(0x01, reply.Param(0));
            Assert.Equal(0x3C, reply.Param(1));
            Assert.Equal(3, rig.Turns);
            Assert.Equal(new[] { "11 01 0B 1C", "11 01 0B 4C 02" }, rig.Frames());
            Assert.Equal(10, rig.Written[1].At);
        }

        /// <summary>A device that answers busy is asked once more after
        /// <see cref="HidppChannel.BusyRetryMs"/> on the waiter's clock, the
        /// pause taken in the same slices as a wait.</summary>
        [Fact]
        public void ABusyAnswer_IsAskedAgainAfterAPauseOnTheWaitersClock()
        {
            var rig = new Rig(stepMs: 10);
            int sends = 0;
            rig.Sent = (r, frame) => r.Channel.Feed(HidppHapticsTests.Hex(++sends == 1 ? "11 01 FF 0B 1C 08" : "11 01 0B 1C 01 3C"));

            var reply = rig.Channel.Request(0x01, 0x0B, HidppHapticProtocol.FunctionGetConfiguration, Array.Empty<byte>(), 300);

            Assert.Equal(HidppReplyKind.Answer, reply.Kind);
            Assert.Equal(new[] { "11 01 0B 1C", "11 01 0B 1C" }, rig.Frames());
            Assert.Equal(0, rig.Written[0].At);
            Assert.Equal(HidppChannel.BusyRetryMs, rig.Written[1].At);
            Assert.Equal(HidppChannel.BusyRetryMs / 10, rig.Turns);

            // Busy twice is the device's answer.
            var busy = new Rig(stepMs: 10);
            busy.Sent = (r, frame) => r.Channel.Feed(HidppHapticsTests.Hex("11 01 FF 0B 1C 08"));
            reply = busy.Channel.Request(0x01, 0x0B, HidppHapticProtocol.FunctionGetConfiguration, Array.Empty<byte>(), 300);
            Assert.Equal(HidppReplyKind.Error, reply.Kind);
            Assert.Equal(HidppHapticProtocol.ErrorBusy, reply.ErrorCode);
            Assert.Equal(2, busy.Written.Count);
        }

        /// <summary>A receiver's register read waits the same way: its
        /// request goes out short on the short collection, its record ends
        /// the wait, its error does, and unanswered it times out on the
        /// waiter's clock.</summary>
        [Fact]
        public void ARegisterRead_WaitsOnTheWaiterToo()
        {
            var silent = new Rig(stepMs: 600);
            var reply = silent.Channel.ReadReceiverRegister(HidppReceiverProtocol.ReceiverInfo, 0x52,
                HidppReceiverProtocol.RegisterTimeoutMs);
            Assert.Equal(HidppReplyKind.Timeout, reply.Kind);
            Assert.Equal(3, silent.Turns);
            var sent = Assert.Single(silent.Written);
            Assert.Equal(ShortPath, sent.Path);
            Assert.Equal("10 FF 83 B5 52", HidppHapticsTests.Text(sent.Frame));
            Assert.Equal(HidppReceiverProtocol.ShortReportLength, sent.Frame.Length);

            var paired = new Rig(stepMs: 10);
            paired.Turn = r => r.Channel.Feed(HidppHapticsTests.Hex("11 FF 83 B5 52 01 02"));
            reply = paired.Channel.ReadReceiverRegister(HidppReceiverProtocol.ReceiverInfo, 0x52,
                HidppReceiverProtocol.RegisterTimeoutMs);
            Assert.Equal(HidppReplyKind.Answer, reply.Kind);
            Assert.Equal(1, paired.Turns);

            var empty = new Rig(stepMs: 10);
            empty.Sent = (r, frame) => r.Channel.Feed(HidppHapticsTests.Hex("10 FF 8F 83 B5 03", HidppReceiverProtocol.ShortReportLength));
            reply = empty.Channel.ReadReceiverRegister(HidppReceiverProtocol.ReceiverInfo, 0x52,
                HidppReceiverProtocol.RegisterTimeoutMs);
            Assert.Equal(HidppReplyKind.Error, reply.Kind);
            Assert.Equal(0x03, reply.ErrorCode);
            Assert.True(reply.Hidpp10);
            Assert.Equal(0, empty.Turns);
        }

        /// <summary>The waiter alone: an answer already there is taken
        /// with no turn given, a timeout of nothing gives no turn, a turn
        /// that throws costs the wait nothing, and a pause is the same wait
        /// on nothing.</summary>
        [Fact]
        public void TheWaiter_TakesWhatIsAlreadyThere_AndOutlastsATurnThatThrows()
        {
            long now = 0;
            int turns = 0;
            var waiter = new HidppWaiter(() => now, 1, () => { turns++; now += 10; });
            using var set = new ManualResetEventSlim(true);
            using var unset = new ManualResetEventSlim(false);

            Assert.True(waiter.Wait(set, 300));
            Assert.True(waiter.Wait(set, 0));
            Assert.False(waiter.Wait(unset, 0));
            Assert.Equal(0, turns);

            var throwing = new HidppWaiter(() => now, 1, () =>
            {
                turns++;
                now += 10;
                throw new InvalidOperationException();
            });
            Assert.False(throwing.Wait(unset, 30));
            Assert.Equal(3, turns);

            long before = now;
            waiter.Pause(50);
            Assert.Equal(before + 50, now);
            Assert.Equal(8, turns);
        }

        /// <summary>With no waiter a request waits its timeout in one
        /// piece. A channel with no short collection reads no register and
        /// writes nothing for it, a write that fails is no timeout, and a
        /// closed channel writes nothing.</summary>
        [Fact]
        public void WithNoWaiter_ARequestWaitsInOnePiece_AndNothingGoesOutThatCannot()
        {
            var written = new List<byte[]>();
            var channel = new HidppChannel(LongPath, null, (path, frame) => { written.Add(frame); return true; });
            Assert.Null(channel.Waiter);
            Assert.Equal(HidppReplyKind.Timeout, channel.Request(0x01, 0x00, 0, new byte[] { 0x00, 0x05 }, 1).Kind);
            Assert.Single(written);
            Assert.Equal(HidppReplyKind.None, channel.ReadReceiverRegister(HidppReceiverProtocol.ReceiverInfo, 0x51, 1).Kind);
            Assert.Single(written);

            var gone = new HidppChannel(LongPath, ShortPath, (path, frame) => false);
            Assert.Equal(HidppReplyKind.WriteFailed, gone.Request(0x01, 0x00, 0, new byte[] { 0x00, 0x05 }, 300).Kind);
            Assert.Equal(HidppReplyKind.WriteFailed,
                gone.ReadReceiverRegister(HidppReceiverProtocol.ReceiverInfo, 0x51, 300).Kind);

            channel.Dispose();
            Assert.False(channel.Write(new byte[HidppHapticProtocol.LongReportLength]));
            Assert.Equal(HidppReplyKind.WriteFailed, channel.Request(0x01, 0x00, 0, new byte[] { 0x00, 0x05 }, 300).Kind);
            Assert.Single(written);
        }
    }

    /// <summary>
    /// Rumble beside a request (#494). The HID++ worker asks its questions
    /// on the thread that plays rumble, and a question to a device that is
    /// asleep waits out its whole timeout. Each test holds the worker
    /// inside such a wait, on a clock the test moves by hand, and reads the
    /// pulse that goes out beside it: on the scripted channel beside a
    /// receiver's pairing read and each silent slot's question, and on the
    /// shipping channel beside a question to the mouse that plays.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidppPulseBesideARequestTests : IDisposable
    {
        public HidppPulseBesideARequestTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static readonly VendorHidCollection Receiver = new()
        {
            Path = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#pulse",
            VendorId = 0x046D, ProductId = 0xC548, UsagePage = 0xFF00, Usage = 0x0002, InputReportLength = 20,
        };

        private static readonly VendorHidCollection ShortCollection = new()
        {
            Path = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#pulse",
            VendorId = 0x046D, ProductId = 0xC548, UsagePage = 0xFF00, Usage = 0x0001, InputReportLength = 7,
        };

        /// <summary>Function 4 of the mouse's haptic feature at index 0x0B,
        /// the sharp collision: the pulse full rumble plays.</summary>
        private const string Pulse = "11 01 0B 4C 02";

        /// <summary>The worker's clock in a test's hands. Once released,
        /// every reading moves it ten seconds on, so a request still
        /// waiting when the test ends gives up at its next look and the
        /// worker can stop.</summary>
        private sealed class HandClock
        {
            private long _now = 100_000;
            private volatile bool _released;

            public long Read() => _released ? Interlocked.Add(ref _now, 10_000) : Interlocked.Read(ref _now);
            public long Now => Interlocked.Read(ref _now);
            public void Advance(long ms) => Interlocked.Add(ref _now, ms);
            public void Release() => _released = true;
        }

        /// <summary>A whole pass of the worker's, begun after this call.</summary>
        private static void Passes(HidppBackend backend)
        {
            int seen = backend.PassCount;
            Assert.True(SpinWait.SpinUntil(() => backend.PassCount >= seen + 2, 30000), "the worker made a pass");
        }

        /// <summary>A whole turn the worker took beside the request it is
        /// waiting on, begun after this call. The worker counts a turn as it
        /// ends, so once two more have ended, one of them began after the
        /// call.</summary>
        private static void Turns(HidppBackend backend)
        {
            int seen = backend.TurnsBesideARequest;
            Assert.True(SpinWait.SpinUntil(() => backend.TurnsBesideARequest >= seen + 2, 30000),
                "the worker took no turn beside its request");
        }

        private static LinkTable RumbleLink(Guid device, HidppUnit unit) => new(new[]
        {
            new DeviceLinks { Device = device, Haptics = new[] { new OutputPath(OutputFamily.HidppUnit, unit.Key) } },
        });

        /// <summary>A scan of a receiver whose pairing read goes
        /// unanswered and whose other five slots stay silent waits 1800 ms
        /// and then 300 ms five times. Rumble that starts inside the first
        /// of those waits plays in the worker's next turn, with no tick of
        /// the clock gone by and the pairing read still waiting, to a device
        /// index the pending request does not name. The pulses then follow
        /// on their own interval through every wait of the scan. The scan
        /// asks what it asks with no rumble, in the same order, which the
        /// run without rumble shows against the same list.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void APulseGoesOutInsideOneTick_WhileAProbesRequestIsStillUnanswered(bool rumble)
        {
            var clock = new HandClock();
            var mouse = HidppHapticsTests.Mouse(1);
            var channel = new HidppHapticsTests.FakeChannel
            {
                Path = Receiver.Path,
                Answer = (d, f, fn, p) => d == 1 ? mouse(d, f, fn, p) : HidppHapticsTests.NoAnswer(),
            };
            static string Waiting(HidppHapticsTests.FakeChannel c) => c.Waiting is byte[] frame ? HidppHapticsTests.Text(frame) : null;
            HidppBackend backend = null;
            // The worker is inside the wait for this frame's answer, and
            // that wait has read the clock: the channel names the frame
            // before the wait begins, and a turn beside the request comes
            // only after the wait has read the clock. Moved any sooner, the
            // clock could move under a wait that had not read it yet, and
            // the wait would count its timeout from after the move.
            void Held(string frame)
            {
                Assert.True(SpinWait.SpinUntil(() => Waiting(channel) == frame, 30000), "the worker never waited on " + frame);
                Turns(backend);
                Assert.Equal(frame, Waiting(channel));
            }
            using var worker = backend = new HidppBackend(() => new[] { Receiver, ShortCollection }, (c, s) => channel,
                _ => Guid.NewGuid(), 1, 5, ticks: clock.Read);
            try
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 30000), "the scan found the mouse");
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(RumbleLink(device, backend.Snapshot.Units[0]));
                Passes(backend);
                while (channel.Traffic.TryDequeue(out _)) { }
                while (channel.Writes.TryDequeue(out _)) { }

                // The next scan comes due, and from here every request the
                // script leaves unanswered waits its timeout out.
                channel.HoldUnanswered = true;
                clock.Advance(HidppBackend.FirstScanMs);
                Held("10 FF 83 B5 52");
                int passes = backend.PassCount;
                long before = clock.Now;
                int pulses = 0;
                if (rumble)
                {
                    PeripheralOutputs.SetMotors(device, 65535, 0);
                    Turns(backend);
                    Assert.Equal(new[] { "reg 10 FF 83 B5 52", "write " + Pulse }, HidppHapticsTests.Lines(channel.Traffic));
                    Assert.Equal(before, clock.Now);
                    Assert.Equal("10 FF 83 B5 52", Waiting(channel));
                    Assert.Equal(passes, backend.PassCount);

                    // The next pulse follows on its interval and no sooner.
                    clock.Advance(HapticRumbleShaper.IntervalMs(1f) - 1);
                    Turns(backend);
                    Assert.Single(channel.Writes);
                    clock.Advance(1);
                    Turns(backend);
                    Assert.Equal(2, channel.Writes.Count);
                    Assert.Equal("10 FF 83 B5 52", Waiting(channel));
                    pulses = 2;
                }

                // The pairing read gives up, and then each silent slot.
                clock.Advance(HidppReceiverProtocol.RegisterTimeoutMs - (clock.Now - before));
                for (byte slot = 2; slot <= HidppHapticProtocol.LastReceiverIndex; slot++)
                {
                    Held($"11 {slot:X2} 00 0C 00 05");
                    Assert.Equal(passes, backend.PassCount);
                    // One pulse for each move of the clock past the
                    // interval, played beside whichever request waits.
                    if (rumble) Assert.Equal(++pulses, channel.Writes.Count);
                    clock.Advance(HidppHapticProtocol.TimeoutMs(false));
                }
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > passes, 30000), "the scan ended and the worker went on");

                Assert.Equal(new[]
                {
                    "reg 10 FF 83 B5 52",
                    "ask 11 02 00 0C 00 05", "ask 11 03 00 0C 00 05", "ask 11 04 00 0C 00 05",
                    "ask 11 05 00 0C 00 05", "ask 11 06 00 0C 00 05",
                }, HidppHapticsTests.Lines(channel.Traffic.Where(t => t.Kind != "write")));
                Assert.All(channel.Writes, w => Assert.Equal(Pulse, HidppHapticsTests.Text(w.Frame)));
                if (!rumble) Assert.Empty(channel.Writes);
                Assert.Single(backend.Snapshot.Units);
            }
            finally
            {
                channel.HoldUnanswered = false;
                clock.Release();
            }
        }

        /// <summary>The same on the shipping channel, beside a question to
        /// the mouse that plays. The liveness check asks the mouse for its
        /// configuration and the mouse is slow to answer. Rumble that starts
        /// meanwhile plays in the worker's next turn. The mouse answers the
        /// first pulse busy, as a device still working on a request answers
        /// a newcomer (Logitech's "HID++ 2.0 Draft Specification", p.3),
        /// and the second with the pulse's own echo. Neither ends the
        /// check's wait, and the answer that is the check's own does: the
        /// mouse stays found, with the configuration that answer gave.</summary>
        [Fact]
        public void OnTheShippingChannel_APulseGoesOutBesideAQuestionToTheSameMouse_AndOnlyItsOwnAnswerEndsTheQuestion()
        {
            var clock = new HandClock();
            var mouse = HidppHapticsTests.Mouse(1);
            var written = new ConcurrentQueue<(byte[] Frame, int Turns)>();
            HidppBackend backend = null;
            HidppChannel channel = null;
            int slow = 0, plays = 0;
            channel = new HidppChannel(Receiver.Path, ShortCollection.Path, (path, frame) =>
            {
                written.Enqueue((frame, backend.TurnsBesideARequest));
                if (frame[0] == HidppReceiverProtocol.ShortReportId)
                {
                    // The receiver's pairing table: a device in slot 1 and
                    // no other.
                    channel.Feed(frame[4] == 0x51
                        ? HidppHapticsTests.Hex("11 FF 83 B5 51")
                        : HidppHapticsTests.Hex("10 FF 8F 83 B5 03", HidppReceiverProtocol.ShortReportLength));
                    return true;
                }
                byte d = frame[1], f = frame[2], fn = (byte)(frame[3] >> 4);
                if (d != 1) return true;
                if (f == 0x0B && fn == HidppHapticProtocol.FunctionPlay)
                {
                    channel.Feed(HidppHapticsTests.Hex(Interlocked.Increment(ref plays) == 1 ? "11 01 FF 0B 4C 08" : Pulse));
                    return true;
                }
                if (f == 0x0B && fn == HidppHapticProtocol.FunctionGetConfiguration && Volatile.Read(ref slow) != 0) return true;
                var reply = mouse(d, f, fn, frame.AsSpan(4).ToArray());
                if (reply.Kind != HidppReplyKind.Answer) return true;
                var report = new byte[HidppHapticProtocol.LongReportLength];
                Array.Copy(frame, report, 4);
                for (int i = 0; i < report.Length - 4; i++) report[4 + i] = reply.Param(i);
                channel.Feed(report);
                return true;
            });
            string Last() => written.LastOrDefault().Frame is byte[] frame ? HidppHapticsTests.Text(frame) : null;
            int Count(string frame) => written.Count(w => HidppHapticsTests.Text(w.Frame) == frame);
            using var worker = backend = new HidppBackend(() => new[] { Receiver, ShortCollection }, (c, s) => channel,
                _ => Guid.NewGuid(), 1, 5, ticks: clock.Read);
            try
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 30000), "the scan found the mouse");
                Assert.True(backend.Snapshot.Units[0].FeedbackEnabled);
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(RumbleLink(device, backend.Snapshot.Units[0]));
                Passes(backend);

                // The liveness check comes due, and its question waits.
                Volatile.Write(ref slow, 1);
                clock.Advance(HidppBackend.CheckMs);
                Assert.True(SpinWait.SpinUntil(() => Last() == "11 01 0B 1C"
                    && backend.TurnsBesideARequest > written.Last().Turns, 30000), "the worker never waited on its check");
                int passes = backend.PassCount;
                long before = clock.Now;

                PeripheralOutputs.SetMotors(device, 65535, 0);
                Turns(backend);
                Assert.Equal(1, Count(Pulse));
                Assert.Equal(before, clock.Now);
                Assert.Equal(passes, backend.PassCount);

                clock.Advance(HapticRumbleShaper.IntervalMs(1f));
                Turns(backend);
                Assert.Equal(2, Count(Pulse));
                Assert.Equal(passes, backend.PassCount);
                Assert.Equal(1, Count("11 01 0B 1C") - 1);

                // The check's own answer: enabled, intensity 60.
                channel.Feed(HidppHapticsTests.Hex("11 01 0B 1C 01 3C"));
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > passes, 30000), "the check's own answer ended its wait");
                var kept = Assert.Single(backend.Snapshot.Units);
                Assert.True(kept.FeedbackEnabled);
            }
            finally
            {
                Volatile.Write(ref slow, 0);
                clock.Release();
            }
        }
    }
}
