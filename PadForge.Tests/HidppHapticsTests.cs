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
    /// set.
    /// </summary>
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
            public volatile bool Disposed;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public bool Write(byte[] frame)
            {
                if (FailWrites) return false;
                Writes.Enqueue((_clock.ElapsedMilliseconds, frame));
                return true;
            }

            /// <summary>The feature ID of every Root.getFeature asked.</summary>
            public readonly ConcurrentQueue<ushort> RootLookups = new();

            public HidppReply Request(byte deviceIndex, byte featureIndex, byte function, byte[] parameters, int timeoutMs)
            {
                if (FailWrites) return new HidppReply(HidppReplyKind.WriteFailed);
                Asked.Enqueue((deviceIndex, featureIndex, function));
                if (featureIndex == 0 && function == 0 && parameters != null && parameters.Length >= 2)
                    RootLookups.Enqueue((ushort)((parameters[0] << 8) | parameters[1]));
                return Answer(deviceIndex, featureIndex, function, parameters ?? Array.Empty<byte>());
            }

            /// <summary>The receiver's register records, by register and
            /// sub-register. Unanswered unless a test scripts them.</summary>
            public Func<byte, byte, HidppReply> Register = (r, s) => NoAnswer();
            public readonly ConcurrentQueue<(byte Register, byte Sub)> RegistersAsked = new();

            public HidppReply ReadReceiverRegister(byte register, byte sub, int timeoutMs)
            {
                if (FailWrites) return new HidppReply(HidppReplyKind.WriteFailed);
                RegistersAsked.Enqueue((register, sub));
                return Register(register, sub);
            }

            public void Dispose() => Disposed = true;
        }

        internal static HidppReply NoAnswer() => new(HidppReplyKind.Timeout);

        /// <summary>An answer report: the long header with PadForge's
        /// software ID, then the payload.</summary>
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
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        body = reader.ReadToEnd();
                    Posts.Enqueue((ctx.Request.Url.AbsolutePath, body));
                    try
                    {
                        ctx.Response.StatusCode = Status;
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

            public List<int> EventValues() => Posts.Where(p => p.Path == "/game_event")
                .Select(p => JsonDocument.Parse(p.Body).RootElement.GetProperty("data").GetProperty("value").GetInt32())
                .ToList();

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }

        [Fact]
        public void GameSense_ReadsTheAddress()
        {
            Assert.True(GameSenseTactile.TryReadAddress("{\"address\":\"127.0.0.1:51248\"}", out string a));
            Assert.Equal("127.0.0.1:51248", a);
            Assert.False(GameSenseTactile.TryReadAddress("{\"address\":\"\"}", out _));
            Assert.False(GameSenseTactile.TryReadAddress("{\"address\":51248}", out _));
            Assert.False(GameSenseTactile.TryReadAddress("[]", out _));
            Assert.False(GameSenseTactile.TryReadAddress("not json", out _));
            Assert.Null(GameSenseTactile.ReadAddress(MissingCoreProps()));
        }

        /// <summary>The handler JSON parses, targets the tactile device on
        /// zone one, plays nothing at 0, and every repeating pulse ends
        /// before its next repeat (the doc's queuing warning).</summary>
        [Fact]
        public void GameSense_TheHandlerPlaysNothingAtZero_AndNoPulseOutlastsItsRepeat()
        {
            using var doc = JsonDocument.Parse(GameSenseTactile.BindBody);
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
                int value = GameSenseTactile.LevelValue(level);
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
            Assert.True(GameSenseTactile.LevelValue(1) < GameSenseTactile.LevelValue(2));
            Assert.True(GameSenseTactile.LevelValue(2) < GameSenseTactile.LevelValue(3));
        }

        [Fact]
        public void GameSense_BindsOnce_PostsOnlyChanges_BeatsWhileRumbleHolds_AndStops()
        {
            using var server = new FakeGameSense();
            using var client = new GameSenseTactile(server.WriteCoreProps(), 1000);
            Assert.True(client.TryConnect(now: 0));
            Assert.Equal(new[] { "/game_metadata", "/bind_game_event" }, server.Paths());

            Assert.True(client.Render(2, now: 10));
            Assert.True(client.Render(2, now: 20));            // same level: nothing posted
            Assert.Equal(new[] { 50 }, server.EventValues());

            Assert.True(client.Render(2, now: 10 + GameSenseTactile.HeartbeatMs));
            Assert.Equal("/game_heartbeat", server.Paths().Last());

            Assert.True(client.Render(0, now: 20 + GameSenseTactile.HeartbeatMs));
            Assert.True(client.Render(0, now: 40 + 3 * GameSenseTactile.HeartbeatMs));   // no beat in silence
            Assert.Equal(new[] { 50, 0 }, server.EventValues());

            Assert.True(client.Render(3, now: 50 + 3 * GameSenseTactile.HeartbeatMs));
            client.Close();
            Assert.Equal(new[] { 50, 0, 84, 0 }, server.EventValues());
            Assert.Equal("/stop_game", server.Paths().Last());
            Assert.False(client.Connected);
        }

        [Fact]
        public void GameSense_ARefusalOrAMissingEngineDoesNotConnect()
        {
            using (var client = new GameSenseTactile(MissingCoreProps(), 500))
                Assert.False(client.TryConnect(0));

            using var server = new FakeGameSense { Status = 500 };
            using (var client = new GameSenseTactile(server.WriteCoreProps(), 1000))
                Assert.False(client.TryConnect(0));
        }

        [Fact]
        public void GameSense_AnEngineThatStopsAnsweringDisconnects()
        {
            var server = new FakeGameSense();
            using var client = new GameSenseTactile(server.WriteCoreProps(), 500);
            Assert.True(client.TryConnect(0));
            server.Dispose();
            Assert.False(client.Render(1, 10));
            Assert.False(client.Connected);
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
            var all = VendorHidRuntime.Enumerate() ?? new List<VendorHidCollection>();
            var collections = all
                .Where(c => c.VendorId == 0x046D && c.UsagePage == 0xFF00 && c.Usage == 0x0002 && c.InputReportLength == 20)
                .ToList();
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
}
