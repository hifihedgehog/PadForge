using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.Flydigi;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Data;
using PadForge.Engine.Flydigi;
using PadForge.Services;
using Xunit;
using Ota = PadForge.Engine.Flydigi.FlydigiScreenOta;
using Proto = PadForge.Engine.Flydigi.FlydigiApexProtocol;
using X = PadForge.Engine.Flydigi.FlydigiApex4XinputScreen;

namespace PadForge.Tests
{
    /// <summary>
    /// A Flydigi Apex pad's session, lighting and screen (#586) against a
    /// pad scripted from the references' replies. In the peripheral
    /// collection: the service, the session's opener and the backup store
    /// are process-wide.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public sealed class FlydigiApexRuntimeTests : IDisposable
    {
        private readonly Func<string, IHidReportChannel> _opener = FlydigiApexSession.Opener;
        private readonly Func<long> _serviceClock = FlydigiApexService.Clock;
        private readonly Action<Action> _post = FlydigiApexService.Post;
        private readonly Action<int, CancellationToken> _sleep = FlydigiScreenSender.Sleep;
        private readonly List<FlydigiApexSession> _sessions = new();

        public FlydigiApexRuntimeTests()
        {
            // An engine another test stopped leaves the service shut down.
            FlydigiApexService.Restart();
            FlydigiApexService.Post = work => work();
            FlydigiScreenSender.Sleep = (_, _) => { };
            FlydigiApexService.ForgetAllForTest();
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true);
        }

        public void Dispose()
        {
            foreach (var session in _sessions) session.Dispose();
            FlydigiApexSession.Opener = _opener;
            FlydigiApexService.Clock = _serviceClock;
            FlydigiApexService.Post = _post;
            FlydigiScreenSender.Sleep = _sleep;
            FlydigiApexService.ForgetAllForTest();
            KeyboardProfileBackups.ResetForTest();
            FlydigiApexLighting.Apex6Session.ForgetWritesForTest();
        }

        // ── A scripted pad ──────────────────────────────────────────

        /// <summary>One pad behind any number of handles: what is written to
        /// one is answered on all.</summary>
        internal sealed class ScriptedPad
        {
            public readonly object Gate = new();
            public readonly List<byte[]> Writes = new();
            public readonly Queue<byte[]> Replies = new();
            public Func<byte[], IEnumerable<byte[]>> Respond = _ => Array.Empty<byte[]>();
            public int InputLength = 64, OutputLength = 64;

            public IHidReportChannel Open(string path) => new View(this, path);

            public List<byte[]> WritesOf(Func<byte[], bool> match)
            {
                lock (Gate) return Writes.Where(match).ToList();
            }

            private sealed class View : IHidReportChannel
            {
                private readonly ScriptedPad _pad;

                public View(ScriptedPad pad, string path)
                {
                    _pad = pad;
                    Path = path;
                }

                public string Path { get; }
                public bool FeatureOnly => false;
                public int InputReportLength => _pad.InputLength;
                public int OutputReportLength => _pad.OutputLength;
                public int FeatureReportLength => 0;
                public bool SetFeature(byte[] report) => false;
                public bool GetFeature(byte reportId, byte[] buffer) => false;

                public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
                {
                    lock (_pad.Gate)
                    {
                        _pad.Writes.Add((byte[])report.Clone());
                        foreach (var reply in _pad.Respond(report)) _pad.Replies.Enqueue(reply);
                    }
                    return true;
                }

                public int ReadInput(byte[] buffer, int timeoutMs)
                {
                    lock (_pad.Gate)
                    {
                        if (_pad.Replies.Count == 0) return 0;
                        var reply = _pad.Replies.Dequeue();
                        Array.Clear(buffer);
                        Array.Copy(reply, buffer, Math.Min(reply.Length, buffer.Length));
                        return Math.Min(reply.Length, buffer.Length);
                    }
                }

                public void Dispose() { }
            }
        }

        private static byte[] Apex5Reply(byte command, params (int At, byte Value)[] bytes)
        {
            var reply = new byte[32];
            reply[0] = 0x04; reply[1] = 0x5A; reply[2] = 0xA5; reply[3] = command;
            foreach (var (at, value) in bytes) reply[at] = value;
            return reply;
        }

        private static byte[] Apex6Reply(byte command, byte mode, Action<byte[]> fill)
        {
            var report = new byte[33];
            report[1] = 0x5A; report[2] = 0xA5; report[3] = command; report[4] = mode;
            fill?.Invoke(report);
            report[32] = Proto.Apex6Checksum(report.AsSpan(1));
            return report;
        }

        private FlydigiApexSession OpenSession(ScriptedPad pad, string path, FlydigiApexModel model)
        {
            FlydigiApexSession.Opener = p => pad.Open(p);
            var session = FlydigiApexSession.Open(path, model);
            Assert.NotNull(session);
            _sessions.Add(session);
            return session;
        }

        private static ScriptedPad Apex5Pad(byte[] rgb, int slot = 1)
        {
            var pad = new ScriptedPad { InputLength = 32, OutputLength = 32 };
            pad.Respond = report =>
            {
                if (report[0] != 0x03) return Array.Empty<byte[]>();
                switch (report[3])
                {
                    case 0x01: return new[] { Apex5Reply(0x01, (6, 128), (7, 1)) };
                    case 0xA1: return new[] { Apex5Reply(0xA1, (6, (byte)slot)) };
                    case 0xA7:
                        return Enumerable.Range(0, 19).Select(i =>
                        {
                            var reply = Apex5Reply(0xA7, (4, 19), (5, (byte)i), (6, (byte)slot));
                            Array.Copy(rgb, i * 20, reply, 7, 20);
                            return reply;
                        }).ToArray();
                    case 0xA2: case 0xA8: case 0xA9: return new[] { Apex5Reply(report[3]) };
                    default: return Array.Empty<byte[]>();
                }
            };
            return pad;
        }

        // ── The session ─────────────────────────────────────────────

        [Fact]
        public void Apex5Session_OpensOnItsIdentity_AndTakesTheWiredPolicy()
        {
            var pad = Apex5Pad(new byte[380]);
            var session = OpenSession(pad, @"\\?\hid#vid_37d7&pid_2501#a", FlydigiApexModel.Apex5);
            Assert.Equal(FlydigiApexModel.Apex5, session.Identity.Model);
            Assert.True(session.Identity.Wired);
            Assert.NotNull(session.Triggers);
            Assert.Equal(Proto.Apex5IdentityRequest(), pad.Writes[0]);
        }

        [Fact]
        public void AnUnansweredIdentity_OpensNoSession()
        {
            var pad = new ScriptedPad { InputLength = 32, OutputLength = 32 };
            FlydigiApexSession.Opener = p => pad.Open(p);
            Assert.Null(FlydigiApexSession.Open(@"\\?\hid#vid_37d7&pid_2502#a", FlydigiApexModel.Apex6Pro));
        }

        [Fact]
        public void Apex4ParameterChanges_AreRationed_ByHowFarTheyMove()
        {
            var a = new FlydigiTriggerCommand(FlydigiTriggerSide.Left, FlydigiTriggerMode.Race, 10, 100);
            Assert.True(FlydigiApexSession.Apex4BigChange(a, a with { P1 = 140 }));
            Assert.False(FlydigiApexSession.Apex4BigChange(a, a with { P1 = 110 }));
            // The floor of 4 for small values (triggers.py _change_allowed).
            Assert.False(FlydigiApexSession.Apex4BigChange(a with { P0 = 2 }, a with { P0 = 5 }));
        }

        [Fact]
        public void AGamesOff_HoldsTheTriggerForAMoment_ThenLetsTheTabThrough()
        {
            var external = new bool[2];
            var offSince = new long[2];
            var effect = new byte[11];
            effect[0] = 0x21;
            Assert.True(UserEffectsDispatcher.FlydigiTakesInbound(external, offSince, 0, effect, true, 1000, out bool held));
            Assert.False(held);
            var off = new byte[11];
            Assert.False(UserEffectsDispatcher.FlydigiTakesInbound(external, offSince, 0, off, true, 1010, out held));
            Assert.True(held);
            Assert.False(UserEffectsDispatcher.FlydigiTakesInbound(external, offSince, 0, off, true, 1010 + UserEffectsDispatcher.FlydigiOffHoldMs, out held));
            Assert.False(held);
            Assert.False(external[0]);
        }

        [Fact]
        public void TheTriggerEngine_SendsEachCommandOnce_AndOnlyItsOwnOffReleases()
        {
            var sent = new List<FlydigiTriggerCommand>();
            var engine = new FlydigiTriggerEngine(emulate: false, sent.Add);
            // A simple resistance from 0x40 with force 0xA0 is a Race
            // (AdaptiveTriggerTranslation.cpp).
            var block = new byte[11];
            block[0] = 0x01;
            block[1] = 0x40; block[2] = 0xA0;
            engine.Set(FlydigiTriggerSide.Right, block, ours: false, 0);
            int afterFirst = sent.Count;
            Assert.Equal(1, afterFirst);
            Assert.Equal(FlydigiTriggerMode.Race, sent[0].Mode);
            engine.Set(FlydigiTriggerSide.Right, block, ours: false, 10);
            Assert.Equal(afterFirst, sent.Count);
            engine.Set(FlydigiTriggerSide.Right, new byte[11], ours: false, 20);
            Assert.Equal(afterFirst, sent.Count);
            engine.Set(FlydigiTriggerSide.Right, new byte[11], ours: true, 30);
            Assert.True(sent.Last().IsOff);
            engine.Forget();
            engine.Clear(FlydigiTriggerSide.Right);
            Assert.True(sent.Last().IsOff);
        }

        // ── The service ─────────────────────────────────────────────

        [Fact]
        public void ThePadsCollection_IsLitOnly_WhileThePollLoopReadsAVerifiedPad()
        {
            long now = 10_000;
            FlydigiApexService.Clock = () => now;
            FlydigiApexSession.Opener = _ => null;
            string path = @"\\?\hid#vid_37d7&pid_2501&mi_02#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var collection = new HidCollectionInfo { Path = path, VendorId = 0x37D7, ProductId = 0x2501, UsagePage = 0xFFA0, Usage = 1 };
            Assert.Null(FlydigiApexLighting.Instance.Match(collection));

            var ud = new UserDevice { VendorId = 0x37D7, ProdId = 0x2501, DevicePath = path, FlydigiApexModel = (int)FlydigiApexModel.Apex5 };
            ud.InstanceGuid = Guid.NewGuid();
            FlydigiApexService.OnPolled(ud, null);
            Assert.Equal(FlydigiApexModel.Apex5, FlydigiApexService.ModelAt(path));
            var model = FlydigiApexLighting.Instance.Match(collection);
            Assert.NotNull(model);
            Assert.Equal(12, model.Map.Lights.Count);
            Assert.Null(FlydigiApexLighting.Instance.Match(collection with { UsagePage = 0xFF00 }));

            // Unread for longer than the stale time, the pad has left.
            now += FlydigiApexService.StaleAfterMs + 1;
            Assert.Equal(FlydigiApexModel.None, FlydigiApexService.ModelAt(path));
            FlydigiApexService.Sweep();
            Assert.Null(FlydigiApexService.SessionAt(path));
        }

        [Fact]
        public void ARecordsModel_IsTrustedOnlyForItsOwnIds()
        {
            var ud = new UserDevice { VendorId = 0x37D7, ProdId = 0x2502, FlydigiApexModel = (int)FlydigiApexModel.Apex6Pro };
            ud.InstanceGuid = Guid.NewGuid();
            Assert.True(FlydigiApexService.HasVoiceCoils(ud));
            Assert.True(HapticToneService.DeviceHasHaptics(ud));
            ud.FlydigiApexModel = (int)FlydigiApexModel.Apex5;
            Assert.Equal(FlydigiApexModel.None, FlydigiApexService.ModelOf(ud));
            Assert.False(HapticToneService.DeviceHasHaptics(ud));
        }

        [Fact]
        public void TheModelAPadAnswersWith_IsRecordedOnce_AndAsksForTheSave()
        {
            var ud = new UserDevice { VendorId = 0x37D7, ProdId = 0x2501 };
            ud.InstanceGuid = Guid.NewGuid();
            int saves = 0;
            Action save = () => saves++;
            FlydigiApexService.ModelRecorded += save;
            try
            {
                Assert.True(FlydigiApexService.Record(ud, FlydigiApexModel.Apex5));
                Assert.Equal((int)FlydigiApexModel.Apex5, ud.FlydigiApexModel);
                Assert.Equal(1, saves);
                // The same answer at the next open leaves nothing to save.
                Assert.False(FlydigiApexService.Record(ud, FlydigiApexModel.Apex5));
                Assert.Equal(1, saves);
            }
            finally
            {
                FlydigiApexService.ModelRecorded -= save;
            }
        }

        /// <summary>An open that finishes after its pad left the list, or
        /// after the pad came back at another path, closes the session it
        /// made, and one whose pad is still there keeps it.</summary>
        [Fact]
        public void AnOpenThatFinishesAfterThePadLeftOrMoved_ClosesItsSession()
        {
            string path = @"\\?\hid#vid_37d7&pid_2501#k";
            var ud = new UserDevice { VendorId = 0x37D7, ProdId = 0x2501, DevicePath = path };
            ud.InstanceGuid = Guid.NewGuid();

            var gone = OpenSession(Apex5Pad(new byte[380]), path, FlydigiApexModel.Apex5);
            Assert.False(FlydigiApexService.KeepForTest(ud, path, gone));
            Assert.False(gone.Exclusive(_ => true));
            Assert.Null(FlydigiApexService.SessionOf(ud.InstanceGuid));

            FlydigiApexService.AddForTest(ud.InstanceGuid, path + "2", null);
            var moved = OpenSession(Apex5Pad(new byte[380]), path, FlydigiApexModel.Apex5);
            Assert.False(FlydigiApexService.KeepForTest(ud, path, moved));
            Assert.False(moved.Exclusive(_ => true));
            Assert.Null(FlydigiApexService.SessionOf(ud.InstanceGuid));

            FlydigiApexService.AddForTest(ud.InstanceGuid, path, null);
            var kept = OpenSession(Apex5Pad(new byte[380]), path, FlydigiApexModel.Apex5);
            Assert.True(FlydigiApexService.KeepForTest(ud, path, kept));
            Assert.Same(kept, FlydigiApexService.SessionOf(ud.InstanceGuid));
            Assert.True(kept.Exclusive(_ => true));
            Assert.Equal((int)FlydigiApexModel.Apex5, ud.FlydigiApexModel);
        }

        /// <summary>The open that records the model runs only against a
        /// pad, which the test host refuses, so the two seams that carry
        /// the model to disk are read from the source: the open records
        /// it, and the engine saves the settings when a record takes
        /// one.</summary>
        [Fact]
        public void TheOpen_RecordsTheModel_AndTheEngineSavesTheRecord()
        {
            string service = Src(@"PadForge.App\Common\Input\Flydigi\FlydigiApexService.cs");
            Assert.Contains("Record(ud, opened.Identity.Model);", service);
            string engine = Src(@"PadForge.App\Services\InputService.cs");
            Assert.Contains("FlydigiApexService.ModelRecorded += OnPeripheralCapabilitiesChanged;", engine);
            Assert.Contains("FlydigiApexService.ModelRecorded -= OnPeripheralCapabilitiesChanged;", engine);
        }

        private static string Src(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "SharedVersion.cs")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(System.IO.Path.Combine(dir.FullName, relative));
        }

        // ── Lighting ────────────────────────────────────────────────

        [Fact]
        public void Apex5Picture_GoesOutLatched_ThenItsFirstPacketUnlatched()
        {
            var stored = new byte[380];
            stored[0] = 0x00; stored[1] = 0x03; stored[6] = 60;
            var pad = Apex5Pad(stored, slot: 1);
            string path = @"\\?\hid#vid_37d7&pid_2501#b";
            var session = OpenSession(pad, path, FlydigiApexModel.Apex5);
            FlydigiApexService.AddForTest(Guid.NewGuid(), path, session);
            var io = new FakeReportChannel { Path = path };

            var lit = (FlydigiApexLighting.Apex5Session)FlydigiApexLighting.Apex5Session.Begin(io, _ => { });
            Assert.NotNull(lit);
            Assert.Equal(1, lit.Slot);
            Assert.Equal(stored, lit.Read);
            lock (pad.Gate) pad.Writes.Clear();

            var lights = FlydigiApexLighting.Zones(12);
            Assert.Equal(HidLightingPaint.Sent, lit.Paint(io, _ => { }, lights, Enumerable.Repeat(0x00FF00, 12).ToList()));
            var packs = pad.WritesOf(w => w[3] == 0xA9);
            var starts = pad.WritesOf(w => w[3] == 0xA8);
            Assert.Equal(20, packs.Count);
            Assert.Equal(2, starts.Count);
            // The latch byte of the first packet, the block's byte 2 at
            // report byte 8: set, then clear (test_rumble_bridge.cpp
            // latchValues {1, 0}).
            Assert.Equal(new byte[] { 1, 0 }, packs.Where(p => p[5] == 0).Select(p => p[8]).Take(1).Concat(new[] { packs[^1][8] }));
            // The brightness Space Station stored stays.
            Assert.Equal(60, packs[0][6 + 6]);
            Assert.Single(lit.CrashFrames);

            lock (pad.Gate) pad.Writes.Clear();
            Assert.True(lit.HandBack(io, _ => { }));
            Assert.Single(pad.WritesOf(w => w[3] == 0xA2));
            Assert.Empty(lit.CrashFrames);
        }

        [Fact]
        public void Apex4Picture_IsTheReadBlock_Steady_InFiftyAcknowledgedParcels()
        {
            var stored = new byte[500];
            stored[1] = 0x02; stored[6] = 70; stored[7] = 4; stored[8] = 3;
            var pad = new ScriptedPad { InputLength = 32, OutputLength = 15 };
            pad.Respond = report =>
            {
                if (report[0] != 0x05) return Array.Empty<byte[]>();
                switch (report[1])
                {
                    case 0xEC:
                    {
                        var reply = new byte[32];
                        reply[15] = 0xEC; reply[3] = 84; reply[13] = 1;
                        return new[] { reply };
                    }
                    case 0xEB:
                        return new[] { new byte[] { 0x04, 0xEB, 0xA0, 2 } };
                    case 0xE5:
                        return Enumerable.Range(0, 50).Select(i =>
                        {
                            var reply = new byte[32];
                            reply[15] = 229; reply[3] = (byte)i;
                            Array.Copy(stored, i * 10, reply, 5, 10);
                            return reply;
                        }).ToArray();
                    case 0xE7:
                    case 0x33:
                    {
                        var ack = new byte[32];
                        ack[15] = report[1]; ack[3] = report[1] == 0xE7 ? (byte)0xFF : (byte)(report[13] + 1);
                        return new[] { ack };
                    }
                    default:
                        return Array.Empty<byte[]>();
                }
            };
            string path = @"\\?\hid#vid_04b4&pid_2412&mi_02#c";
            var session = OpenSession(pad, path, FlydigiApexModel.Apex4);
            Assert.True(session.Identity.Wired);
            FlydigiApexService.AddForTest(Guid.NewGuid(), path, session);
            var io = new FakeReportChannel { Path = path };

            var lit = (FlydigiApexLighting.Apex4Session)FlydigiApexLighting.Apex4Session.Begin(io, _ => { });
            Assert.NotNull(lit);
            Assert.Equal(2, lit.Slot);
            lock (pad.Gate) pad.Writes.Clear();
            Assert.Equal(HidLightingPaint.Sent,
                lit.Paint(io, _ => { }, FlydigiApexLighting.Zones(4), new[] { 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFFFF }));
            var start = pad.WritesOf(w => w[1] == 0xE7);
            var parcels = pad.WritesOf(w => w[1] == 0x33);
            Assert.Single(start);
            Assert.Equal(2, start[0][2]);
            Assert.Equal(50, parcels.Count);
            var written = parcels.SelectMany(p => p.Skip(2).Take(10)).ToArray();
            Assert.Equal(5, written[8]);
            Assert.Equal(70, written[6]);
            Assert.Equal(new byte[] { 100, 0, 0 }, written.Skip(20).Take(3));
            Assert.Equal(new byte[] { 100, 100, 100 }, written.Skip(20 + 90).Take(3));

            lock (pad.Gate) pad.Writes.Clear();
            Assert.True(lit.HandBack(io, _ => { }));
            var back = pad.WritesOf(w => w[1] == 0x33).SelectMany(p => p.Skip(2).Take(10)).ToArray();
            Assert.Equal(stored, back);
        }

        [Fact]
        public void Apex6Color_IsSavedBeforeItsWrite_AndHandedBackWithAReadBack()
        {
            var ram = Proto.Apex6SolidLighting(10, 20, 30, 90, true);
            byte[] current = ram;
            byte[] writing = null;
            var pad = new ScriptedPad { InputLength = 33, OutputLength = 33 };
            pad.Respond = report =>
            {
                var frame = report.AsSpan(1);
                if (frame[2] == 0x01)
                    return new[] { Apex6Reply(0x01, 1, r => { r[6] = 0x96; r[30] = 0x98; }) };
                if (frame[2] == 0xA3 && frame[4] == 1)
                    return new[] { Apex6Reply(0xA3, 1, r =>
                    {
                        r[6] = 4; r[8] = (byte)current.Length; r[9] = (byte)(current.Length >> 8);
                        BitConverter.GetBytes(Proto.Crc32(current)).CopyTo(r, 10);
                    }) };
                if (frame[2] == 0xA3 && frame[4] == 0)
                {
                    int index = frame[6];
                    return new[] { Apex6Reply(0xA3, 0, r =>
                    {
                        r[6] = 4; r[10] = (byte)index;
                        int take = Math.Min(16, current.Length - index * 16);
                        r[11] = (byte)take;
                        Array.Copy(current, index * 16, r, 12, take);
                    }) };
                }
                if (frame[2] == 0xA4)
                {
                    byte mode = frame[4];
                    if (mode == 0) writing = new byte[frame[6] | (frame[7] << 8)];
                    if (mode == 1)
                    {
                        int offset = frame[5] | (frame[6] << 8);
                        frame.Slice(8, frame[7]).CopyTo(writing.AsSpan(offset));
                    }
                    if (mode == 2) current = writing;
                    return new[] { Apex6Reply(0xA4, mode, r =>
                    {
                        if (mode != 2) return;
                        r[8] = (byte)current.Length;
                        BitConverter.GetBytes(Proto.Crc32(current)).CopyTo(r, 10);
                    }) };
                }
                return Array.Empty<byte[]>();
            };
            string path = @"\\?\hid#vid_37d7&pid_2502#d";
            var session = OpenSession(pad, path, FlydigiApexModel.Apex6Pro);
            FlydigiApexService.AddForTest(Guid.NewGuid(), path, session);
            var io = new FakeReportChannel { Path = path };
            string key = KeyboardProfileBackups.SessionKey(FlydigiApexLighting.Product(FlydigiApexModel.Apex6Pro), path);

            var lit = (FlydigiApexLighting.Apex6Session)FlydigiApexLighting.Apex6Session.Begin(io, _ => { }, key);
            Assert.NotNull(lit);
            Assert.Equal(ram, lit.Original);
            Assert.Null(KeyboardProfileBackups.Get(key));

            Assert.Equal(HidLightingPaint.Sent, lit.Paint(io, _ => { }, FlydigiApexLighting.Zones(1), new[] { 0xFF8000 }));
            Assert.Equal(ram, KeyboardProfileBackups.Get(key)?.Contents);
            // The block written keeps the pad's brightness and vibration flag.
            Assert.True(Proto.ParseApex6Lighting(current, out bool vibration, out byte brightness, out byte mode));
            Assert.Equal((1, 90, true), (mode, brightness, vibration));
            Assert.Equal(new byte[] { 0xFF, 0x80, 0x00 }, current.Skip(17).Take(3));

            // A color within 12 a channel of the one shown writes nothing.
            int writes = pad.WritesOf(w => w[3] == 0xA4).Count;
            Assert.Equal(HidLightingPaint.Sent, lit.Paint(io, _ => { }, FlydigiApexLighting.Zones(1), new[] { 0xF58508 }));
            Assert.Equal(writes, pad.WritesOf(w => w[3] == 0xA4).Count);

            Assert.True(lit.HandBack(io, _ => { }));
            Assert.Equal(ram, current);
            Assert.Null(KeyboardProfileBackups.Get(key));
        }

        // ── The screen ──────────────────────────────────────────────

        /// <summary>A screen bootloader that answers every packet.</summary>
        private sealed class ScriptedBootloader : IOtaLink
        {
            public readonly bool Strict;
            public readonly List<byte[]> Packets = new();
            private readonly Queue<byte[]> _replies = new();
            public uint Base = 0x002FF000;

            public ScriptedBootloader(bool strict) => Strict = strict;

            public bool Write(byte[] data)
            {
                Packets.Add((byte[])data.Clone());
                byte op = data[0];
                var payload = data.AsSpan(3);
                byte[] reply = op switch
                {
                    Ota.OpGetBase => new byte[] { 0, op, 4, 0 }.Concat(BitConverter.GetBytes(Base)).ToArray(),
                    Ota.OpGetVersion => new byte[] { 0, op, 4, 0, 1, 0, 0, 0 },
                    Ota.OpErase => new byte[] { 0, op, 4, 0 }.Concat(payload.Slice(0, 4).ToArray()).ToArray(),
                    Ota.OpWrite => new byte[] { 0, op, 6, 0 }.Concat(payload.Slice(0, 6).ToArray()).ToArray(),
                    _ => new byte[] { 0, op },
                };
                _replies.Enqueue(reply);
                return true;
            }

            public byte[] ReadChunk(int timeoutMs, CancellationToken ct) => _replies.Count > 0 ? _replies.Dequeue() : Array.Empty<byte>();

            public byte[] ReadReply(int timeoutMs, CancellationToken ct) => ReadChunk(timeoutMs, ct);

            public void Dispose() { }
        }

        private static FlydigiScreenPicture Picture(int frames)
            => new(Enumerable.Range(0, frames).Select(i => FlydigiScreenFormat.EncodeFrame(new byte[160 * 80 * 3])).ToList(), 100);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheSerialUpload_IsBaseVersionErasesWritesThenReset(bool strict)
        {
            var link = new ScriptedBootloader(strict);
            var picture = Picture(2);
            uint at = FlydigiScreenSender.Upload(link, picture.Frames, picture.IntervalMs, strict, null, CancellationToken.None);
            Assert.Equal(0x002FF000u, at);
            int length = 2 * FlydigiScreenFormat.FrameLength;
            int erases = Ota.Erases(length), writes = Ota.Writes(length);
            Assert.Equal(2 + erases + writes + 1, link.Packets.Count);
            Assert.Equal(Ota.OpGetBase, link.Packets[0][0]);
            Assert.Equal(Ota.PictureGif, link.Packets[0][3]);
            Assert.Equal(2, link.Packets[0][4]);
            Assert.Equal(Ota.OpGetVersion, link.Packets[1][0]);
            Assert.All(link.Packets.Skip(2).Take(erases), p => Assert.Equal(Ota.OpErase, p[0]));
            Assert.Equal(0x002FF000u + 4096u, BitConverter.ToUInt32(link.Packets[3], 3));
            Assert.All(link.Packets.Skip(2 + erases).Take(writes), p => Assert.Equal(Ota.OpWrite, p[0]));
            var reset = link.Packets[^1];
            Assert.Equal(Ota.OpReset, reset[0]);
            var data = FlydigiScreenFormat.Join(picture.Frames);
            Assert.Equal(Ota.Checksum(data), BitConverter.ToUInt32(reset, 7));
            // The last write declares 55 on the Apex 5 and its length on the
            // Apex 6.
            var last = link.Packets[2 + erases + writes - 1];
            int tail = length - (writes - 1) * Ota.WriteChunk;
            Assert.Equal(strict ? tail : Ota.WriteChunk, BitConverter.ToUInt16(last, 7));
        }

        /// <summary>The Apex 4's interrupt pipes, answering each screen packet,
        /// and the first data packet once with a request to send it again.</summary>
        private sealed class ScriptedXboxPad : FlydigiScreenSender.IXboxPipe
        {
            public readonly List<byte[]> Packets = new();
            private readonly Queue<byte[]> _reports = new();
            private bool _askedAgain;

            public bool Write(byte[] packet)
            {
                Packets.Add((byte[])packet.Clone());
                var report = new byte[64];
                if (packet[1] == X.CmdDeviceInfo)
                {
                    report[14] = 0xA5; report[15] = 0x10; report[16] = 84; report[25] = 1;
                }
                else if (packet[1] >= X.CmdScreenStart && packet[1] <= X.CmdScreenEndAll)
                {
                    report[14] = 0x5A; report[15] = 0xA5;
                    report[16] = packet[1] == X.CmdScreenEndAll ? X.CmdScreenEnd : packet[1];
                    if (packet[1] == X.CmdScreenData && !_askedAgain)
                    {
                        _askedAgain = true;
                        report[18] = 1;
                    }
                }
                else return true;
                _reports.Enqueue(report);
                return true;
            }

            public int Read(byte[] buffer, int timeoutMs)
            {
                if (_reports.Count == 0) return 0;
                var report = _reports.Dequeue();
                report.CopyTo(buffer, 0);
                return report.Length;
            }

            public void Dispose() { }
        }

        [Fact]
        public void TheApex4Upload_IsStartDataEndPerFrame_ThenTheEnd_AndSendsAgainWhenAsked()
        {
            var pipe = new ScriptedXboxPad();
            FlydigiScreenSender.UploadApex4(pipe, Picture(1), null, CancellationToken.None);
            int data = (FlydigiScreenFormat.FrameLength + X.Chunk - 1) / X.Chunk;
            // One data packet went twice.
            Assert.Equal(1 + data + 1 + 1 + 1, pipe.Packets.Count);
            Assert.Equal(X.CmdScreenStart, pipe.Packets[0][1]);
            Assert.Equal(X.CmdScreenData, pipe.Packets[1][1]);
            Assert.Equal(pipe.Packets[1], pipe.Packets[2]);
            Assert.Equal(X.CmdScreenEnd, pipe.Packets[^2][1]);
            Assert.Equal(X.CmdScreenEndAll, pipe.Packets[^1][1]);
        }

        [Fact]
        public void TheApex4Send_RefusesWithAnotherXboxPadPluggedIn_BeforeItSwitchesAnything()
        {
            var nodes = FlydigiScreenSender.UsbNodes;
            try
            {
                FlydigiScreenSender.UsbNodes = () => new[]
                {
                    new VendorUsbDriverInstaller.UsbNode(@"USB\VID_045E&PID_028E\1", 0x045E, 0x028E, -1, "xusb22",
                        Array.Empty<string>(), Array.Empty<string>()),
                };
                var pad = Apex4Pad(out _);
                var session = OpenSession(pad, @"\\?\hid#vid_04b4&pid_2412&mi_02#e", FlydigiApexModel.Apex4);
                lock (pad.Gate) pad.Writes.Clear();
                var ex = Assert.Throws<FlydigiScreenException>(() =>
                    FlydigiScreenSender.Send(session, Picture(1), new FlydigiScreenSender.Apex4Binding(), null, CancellationToken.None));
                Assert.Equal(FlydigiScreenFailure.OtherXboxPads, ex.Failure);
                Assert.Empty(pad.WritesOf(w => w[1] == X.DInputSwitchToXInput));
            }
            finally
            {
                FlydigiScreenSender.UsbNodes = nodes;
            }
        }

        [Fact]
        public void TheApex4Send_BindsOnlyAPadNamedFlydigi_AndGivesTheNodeBackWhateverHappens()
        {
            var nodes = FlydigiScreenSender.UsbNodes;
            var name = FlydigiScreenSender.ProductStringOf;
            var bind = FlydigiScreenSender.BindWinUsb;
            var restore = FlydigiScreenSender.RestoreDriver;
            var open = FlydigiScreenSender.OpenXbox;
            try
            {
                var pad = Apex4Pad(out _);
                var session = OpenSession(pad, @"\\?\hid#vid_04b4&pid_2412&mi_02#f", FlydigiApexModel.Apex4);
                bool switched = false;
                var node = new VendorUsbDriverInstaller.UsbNode(@"USB\VID_045E&PID_028E\7&2", 0x045E, 0x028E, -1, "xusb22",
                    Array.Empty<string>(), Array.Empty<string>());
                pad.Respond = report =>
                {
                    if (report[0] == 0x05 && report[1] == X.DInputSwitchToXInput) switched = true;
                    return Array.Empty<byte[]>();
                };
                FlydigiScreenSender.UsbNodes = () => switched ? new[] { node } : Array.Empty<VendorUsbDriverInstaller.UsbNode>();
                var log = new List<string>();
                int binds = 0, restores = 0;
                FlydigiScreenSender.BindWinUsb = (plan, id, all, ct) =>
                {
                    binds++;
                    Assert.Equal(@"USB\VID_045E&PID_028E", plan.BindId);
                    return true;
                };
                FlydigiScreenSender.RestoreDriver = (id, ct) =>
                {
                    restores++;
                    return true;
                };
                var pipe = new ScriptedXboxPad();
                FlydigiScreenSender.OpenXbox = _ => pipe;
                var binding = new FlydigiScreenSender.Apex4Binding
                {
                    Owe = id => log.Add("owe " + id),
                    Settle = id => log.Add("settle " + id),
                    HoldSdl = off => log.Add("sdl " + off),
                };

                // A pad that does not name Flydigi is never bound.
                FlydigiScreenSender.ProductStringOf = _ => "Controller (XBOX 360 For Windows)";
                var ex = Assert.Throws<FlydigiScreenException>(() =>
                    FlydigiScreenSender.Send(session, Picture(1), binding, null, CancellationToken.None));
                Assert.Equal(FlydigiScreenFailure.NotApex4, ex.Failure);
                Assert.Equal(0, binds);
                Assert.Empty(log);

                // One that does is bound, sent to, switched back and given back,
                // with the record made before the move and settled after.
                switched = false;
                FlydigiScreenSender.ProductStringOf = _ => "Flydigi APEX4";
                FlydigiScreenSender.Send(session, Picture(1), binding, null, CancellationToken.None);
                Assert.Equal(1, binds);
                Assert.Equal(1, restores);
                Assert.Equal(new[] { "owe " + node.InstanceId, "sdl True", "sdl False", "settle " + node.InstanceId }, log);
                Assert.Equal(X.CmdDeviceInfo, pipe.Packets[0][1]);
                Assert.Equal(X.CmdSwitchToDInput, pipe.Packets[^1][1]);
            }
            finally
            {
                FlydigiScreenSender.UsbNodes = nodes;
                FlydigiScreenSender.ProductStringOf = name;
                FlydigiScreenSender.BindWinUsb = bind;
                FlydigiScreenSender.RestoreDriver = restore;
                FlydigiScreenSender.OpenXbox = open;
            }
        }

        private static ScriptedPad Apex4Pad(out byte[] identity)
        {
            var reply = new byte[32];
            reply[15] = 0xEC; reply[3] = 84; reply[13] = 1;
            identity = reply;
            var pad = new ScriptedPad { InputLength = 32, OutputLength = 15 };
            pad.Respond = report => report[0] == 0x05 && report[1] == 0xEC ? new[] { reply } : Array.Empty<byte[]>();
            return pad;
        }

        // ── Links and settings ──────────────────────────────────────

        [Fact]
        public void APadRow_TakesItsLightingUnit_OnlyAsALitPad()
        {
            var device = Guid.NewGuid();
            var box = Guid.NewGuid();
            var unit = new DirectUnit(OutputFamily.HidLightingUnit, @"\\?\hid#vid_37d7&pid_2501#g", box, 0x37D7, 0x2501);
            var inputs = new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None) { Units = new[] { unit } };
            var pad = new LinkRow(device, PadForge.Engine.InputDeviceType.Gamepad, 0x37D7, 0x2501, box, null);
            Assert.False(PeripheralLinker.MatchesDevice(unit, pad));
            var lit = pad with { LitPad = true };
            Assert.True(PeripheralLinker.MatchesDevice(unit, lit));
            var table = PeripheralLinker.Build(new[] { lit }, inputs);
            Assert.Contains(table.For(device).Lighting, p => p.Key == unit.Key);
        }

        [Fact]
        public void TheModelAndTheOwedBindings_RoundTripThroughTheSettingsFile()
        {
            var ud = new UserDevice { FlydigiApexModel = (int)FlydigiApexModel.Apex4 };
            var serializer = new XmlSerializer(typeof(UserDevice));
            using var text = new StringWriter();
            serializer.Serialize(text, ud);
            var back = (UserDevice)serializer.Deserialize(new StringReader(text.ToString()));
            Assert.Equal((int)FlydigiApexModel.Apex4, back.FlydigiApexModel);

            var data = new AppSettingsData { FlydigiScreenBindings = new[] { @"USB\VID_045E&PID_028E\7&2" } };
            var appSerializer = new XmlSerializer(typeof(AppSettingsData));
            using var appText = new StringWriter();
            appSerializer.Serialize(appText, data);
            Assert.Contains("FlydigiScreenBindings", appText.ToString());
            var appBack = (AppSettingsData)appSerializer.Deserialize(new StringReader(appText.ToString()));
            Assert.Equal(data.FlydigiScreenBindings, appBack.FlydigiScreenBindings);
        }
    }
}
