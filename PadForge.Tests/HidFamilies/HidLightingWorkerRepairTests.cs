using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the shared worker owes a device it lost hold of, and how it keeps
    /// one device from holding up the others (#557): a hand-back that stays
    /// owed after a failed write, a refused session that asks for no walk, a
    /// handle that died inside a replug, a resume, keep-alives during another
    /// session's waits, the stored-picture bounds, and a fault in one family.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidLightingWorkerRepairTests : IDisposable
    {
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000d2");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly byte[] HandBackFrame = { 0x01, 0xAA };

        private readonly FakeWorkerFamily _family = new();
        private readonly List<FakeReportChannel> _opened = new();
        private readonly List<(string Path, byte[] Frame)> _oneShots = new();
        private long _tick = 1_000_000;
        private bool _oneShotsTaken = true;
        private int _walksAsked;

        public HidLightingWorkerRepairTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private static HidCollectionInfo Unit(string tail = "1") => HidLightingWorkerTests.Collection(tail: tail);

        private static OutputPath PathOf(HidCollectionInfo unit) => new(OutputFamily.HidLightingUnit, unit.Path);

        private Func<string, FakeReportChannel> _channelFor = path => new FakeReportChannel
        {
            Path = path, OutputReportLength = 65, FeatureReportLength = 65,
        };

        private IHidReportChannel Open(string path)
        {
            var channel = _channelFor(path);
            if (channel != null)
                lock (_opened) _opened.Add(channel);
            return channel;
        }

        private FakeReportChannel[] Opened()
        {
            lock (_opened) return _opened.ToArray();
        }

        private bool OneShot(string path, byte[] frame, int timeoutMs)
        {
            lock (_oneShots) _oneShots.Add((path, frame));
            _family.Add("oneshot");
            return _oneShotsTaken;
        }

        private int OneShots()
        {
            lock (_oneShots) return _oneShots.Count;
        }

        private Func<string, FakeReportChannel> _queryFor = path => new FakeReportChannel { Path = path, FeatureOnly = true };

        private Func<string, Guid> _containerOf = _ => Box;

        private HidLightingBackend Started(IHidLightingFamily[] families, params HidCollectionInfo[] present)
        {
            var backend = new HidLightingBackend(families, Open, path => _containerOf(path), Tick, OneShot, OneShot, ms => Advance(ms),
                idleTickMs: 5, query: path => _queryFor(path));
            backend.RescanRequested += () => Interlocked.Increment(ref _walksAsked);
            backend.Start();
            backend.Rescan(present);
            Passes(backend);
            return backend;
        }

        private HidLightingBackend Started(params HidCollectionInfo[] present)
            => Started(new IHidLightingFamily[] { _family }, present);

        private static void Passes(HidLightingBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                backend.Wake();
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        private static void Link(params HidCollectionInfo[] units)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = Device, Lighting = units.Select(PathOf).ToArray() },
            }));

        private static void Light(int rgb) => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1,
            (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private int Count(string line) => _family.Calls().Count(c => c == line);

        /// <summary>A session whose hand-back has crash frames, and whose
        /// Paint fails or waits as the test says.</summary>
        private sealed class OwingSession : FakeWorkerSession
        {
            private readonly Func<int> _waitMs;
            private readonly Func<bool> _fails;

            public OwingSession(FakeWorkerFamily family, IHidReportChannel io, Func<bool> fails, Func<int> waitMs = null)
                : base(family, io)
            {
                _fails = fails;
                _waitMs = waitMs;
            }

            public override byte[][] CrashFrames { get; } = { HandBackFrame };

            public override bool Feature => true;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
                IReadOnlyList<int> colors)
            {
                int ms = _waitMs?.Invoke() ?? 0;
                if (ms > 0) wait(ms);
                if (_fails()) return HidLightingPaint.Failed;
                return base.Paint(io, wait, lights, colors);
            }
        }

        private volatile bool _paintFails;

        private void OwingModel(Func<HidLightingModel, HidLightingModel> change = null) => _family.With(m =>
        {
            var model = new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map,
                Begin = (io, _) =>
                {
                    _family.Add("begin");
                    return new OwingSession(_family, io, () => _paintFails);
                },
            };
            return change == null ? model : change(model);
        });

        private HidLightingBackend LitThenFailed(HidCollectionInfo unit, out int passesToWake)
        {
            OwingModel();
            var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            _paintFails = true;
            Light(0x445566);
            Passes(backend);
            Assert.True(backend.Snapshot.Units[0].Asleep);
            _paintFails = false;
            passesToWake = 2;
            return backend;
        }

        // ─────────────────────────────────────────────
        //  A hand-back owed after a failed write
        // ─────────────────────────────────────────────

        /// <summary>A failed write leaves the hand-back owed: it keeps its
        /// place on the crash record, and goes out once the unit answers,
        /// with no claim on it by then.</summary>
        [Fact]
        public void AFailedWrite_OwesTheHandBack_SentOnceTheUnitAnswers()
        {
            var unit = Unit();
            using var backend = LitThenFailed(unit, out int passes);
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(unit.Path, record.Path);
            Assert.Equal(HandBackFrame, Assert.Single(record.Frames));
            Assert.Equal(0, OneShots());

            Unlight();
            Advance(HidLightingBackend.RetryMs);
            Passes(backend, passes);

            lock (_oneShots) Assert.Equal(HandBackFrame, Assert.Single(_oneShots).Frame);
            Assert.Empty(backend.Recorded);
            Assert.False(backend.Snapshot.Units[0].Asleep);
        }

        /// <summary>A claim still on the unit: what is owed goes ahead of
        /// the next session, so that session reads the device's own state.</summary>
        [Fact]
        public void AnOwedHandBack_GoesAheadOfTheNextSession()
        {
            var unit = Unit();
            using var backend = LitThenFailed(unit, out int passes);
            Advance(HidLightingBackend.RetryMs);
            Passes(backend, passes);
            var calls = _family.Calls();
            int oneShot = calls.IndexOf("oneshot");
            Assert.True(oneShot > 0, "the owed frame went out");
            Assert.Equal("begin", calls[oneShot + 1]);
            Assert.Equal("paint 445566,445566", calls[oneShot + 2]);
        }

        /// <summary>A unit that does not take it stays owed, stays asleep and
        /// stays on the crash record.</summary>
        [Fact]
        public void AnOwedHandBack_ThatDoesNotGoOut_StaysOwed()
        {
            var unit = Unit();
            using var backend = LitThenFailed(unit, out int passes);
            _oneShotsTaken = false;
            Unlight();
            Advance(HidLightingBackend.RetryMs);
            Passes(backend, passes);
            Assert.Equal(1, OneShots());
            Assert.True(backend.Snapshot.Units[0].Asleep);
            Assert.Single(backend.Recorded);

            _oneShotsTaken = true;
            Advance(HidLightingBackend.RetryMs);
            Passes(backend, passes);
            Assert.Equal(2, OneShots());
            Assert.Empty(backend.Recorded);
            Assert.False(backend.Snapshot.Units[0].Asleep);
        }

        /// <summary>A program of the model started: the debt ends with nothing
        /// sent, the rule every hand-back follows.</summary>
        [Fact]
        public void AnOwedHandBack_EndsWithNothingSent_WhileAProgramOfTheModelRuns()
        {
            var program = new VendorProgram("Fake hub", "FakeHub");
            OwingModel(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin, Programs = new[] { program },
            });
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            _paintFails = true;
            Light(0x445566);
            Passes(backend);
            _paintFails = false;
            PeripheralOutputs.Presence = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "FakeHub.exe" }, new[] { program });
            Unlight();
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(0, OneShots());
            Assert.Empty(backend.Recorded);
        }

        /// <summary>OpenRGB holds the device: the same.</summary>
        [Fact]
        public void AnOwedHandBack_EndsWithNothingSent_WhileOpenRgbHoldsTheDevice()
        {
            var unit = Unit();
            using var backend = LitThenFailed(unit, out int passes);
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = Device, TakenByOpenRgb = new[] { PathOf(unit) } },
            }));
            Advance(HidLightingBackend.RetryMs);
            Passes(backend, passes);
            Assert.Equal(0, OneShots());
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A stopping worker sends what it still owes through a
        /// bounded write of its own.</summary>
        [Fact]
        public void AStoppingWorker_SendsWhatItStillOwes()
        {
            var unit = Unit();
            var backend = LitThenFailed(unit, out _);
            backend.Stop();
            backend.Dispose();
            Assert.Equal(1, OneShots());
            Assert.Empty(backend.Recorded);
        }

        /// <summary>Two units whose sessions share one array of frames each keep
        /// their own place on the crash record.</summary>
        [Fact]
        public void TwoUnitsSharingFrames_KeepTheirOwnRecords()
        {
            byte[][] shared = { HandBackFrame };
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map,
                Begin = (io, _) => new SharedFramesSession(_family, io, shared),
            });
            var one = Unit("1");
            var two = Unit("2");
            using var backend = Started(one, two);
            Link(one, two);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(2, backend.Recorded.Length);

            // One leaves: the other's entry stays.
            backend.Rescan(new[] { two });
            Passes(backend);
            Assert.Equal(two.Path, Assert.Single(backend.Recorded).Path);
        }

        private sealed class SharedFramesSession : FakeWorkerSession
        {
            private readonly byte[][] _frames;

            public SharedFramesSession(FakeWorkerFamily family, IHidReportChannel io, byte[][] frames) : base(family, io)
                => _frames = frames;

            public override byte[][] CrashFrames => _frames;
        }

        /// <summary>A device that left has nothing to hand back to.</summary>
        [Fact]
        public void AUnitThatLeaves_OwesNothing()
        {
            var unit = Unit();
            using var backend = LitThenFailed(unit, out _);
            backend.Rescan(Array.Empty<HidCollectionInfo>());
            Passes(backend);
            Assert.Empty(backend.Recorded);
            Assert.Equal(0, OneShots());
        }

        /// <summary>A model that keeps its own backup settles it by its own
        /// repair, with no frames from the worker.</summary>
        [Fact]
        public void AModelThatKeepsItsOwnBackup_IsOwedNothingByTheWorker()
        {
            OwingModel(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin,
                Owed = _ => false, Repair = (_, _) => true,
            });
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            _paintFails = true;
            Light(0x445566);
            Passes(backend);
            _paintFails = false;
            Assert.Empty(backend.Recorded);
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(0, OneShots());
        }

        // ─────────────────────────────────────────────
        //  A refused session
        // ─────────────────────────────────────────────

        /// <summary>A handle with no access right on a collection Windows
        /// holds, for a model that needs more: the unit says that, no session
        /// begins, no walk is asked for, and from its second try on it is asked
        /// each minute.</summary>
        [Fact]
        public void ACollectionWindowsHolds_SaysSo_BeginsNothing_AsksNoWalk_AndIsAskedEachMinute()
        {
            int begins = 0;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map,
                Begin = (_, _) =>
                {
                    Interlocked.Increment(ref begins);
                    return null;
                },
            });
            _channelFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, FeatureReportLength = 65 };
            var unit = HidLightingWorkerTests.Collection(page: 0x0001);
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(0, Volatile.Read(ref begins));
            Assert.Equal(0, Volatile.Read(ref _walksAsked));
            var listed = backend.Snapshot.Units[0];
            Assert.True(listed.Asleep);
            Assert.True(listed.Refused);
            Assert.True(listed.WindowsHolds);
            Assert.False(listed.HeldByProgram);

            int opened = Opened().Length;
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(opened + 1, Opened().Length);
            Advance(HidLightingBackend.RefusedRetryMaxMs - 1);
            Passes(backend);
            Assert.Equal(opened + 1, Opened().Length);
            Advance(1);
            Passes(backend);
            Assert.Equal(opened + 2, Opened().Length);
        }

        /// <summary>The same handle on any other collection says another program
        /// has it open. Nothing is written, no walk is asked for, and it is tried
        /// again at every retry, so the device lights soon after that program
        /// closes.</summary>
        [Fact]
        public void ACollectionAnotherProgramHolds_SaysSo_AndLightsSoonAfterItCloses()
        {
            _family.With();
            _channelFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, FeatureReportLength = 65 };
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            var listed = backend.Snapshot.Units[0];
            Assert.True(listed.Asleep);
            Assert.True(listed.HeldByProgram);
            Assert.False(listed.WindowsHolds);
            Assert.False(listed.Refused);
            Assert.Equal(0, Volatile.Read(ref _walksAsked));
            Assert.Empty(_family.Calls());

            int opened = Opened().Length;
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(opened + 1, Opened().Length);
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(opened + 2, Opened().Length);
            Assert.Empty(_family.Calls());

            _channelFor = path => new FakeReportChannel { Path = path, OutputReportLength = 65, FeatureReportLength = 65 };
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            listed = backend.Snapshot.Units[0];
            Assert.False(listed.Asleep);
            Assert.False(listed.HeldByProgram);
            Assert.Equal(1, Count("paint 112233,112233"));
        }

        /// <summary>A program that takes the collection of a unit already asleep
        /// for another reason, and one that lets go of it, both show on the
        /// unit's next try. A unit a repair put to sleep says Windows holds it
        /// the same way.</summary>
        [Theory]
        [InlineData(0xFF00, false)]
        [InlineData(0x0001, true)]
        public void AUnitAsleepForAnotherReason_SaysWhoHoldsItAtItsNextTry(int page, bool windows)
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin,
                Owed = _ => true,
                Repair = (_, _) => true,
            });
            _channelFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, FeatureReportLength = 65 };
            var unit = HidLightingWorkerTests.Collection(page: (ushort)page);
            using var backend = Started(unit);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            var listed = backend.Snapshot.Units[0];
            Assert.True(listed.Asleep);
            Assert.False(listed.WindowsHolds || listed.HeldByProgram);

            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            listed = backend.Snapshot.Units[0];
            Assert.Equal(windows, listed.WindowsHolds);
            Assert.Equal(!windows, listed.HeldByProgram);
        }

        /// <summary>A sibling that opens with no access right says the same as a
        /// unit's own collection would: Windows has it where Windows holds that
        /// collection, and another program anywhere else.</summary>
        [Theory]
        [InlineData(0xFF01, false)]
        [InlineData(0x0001, true)]
        public void ASiblingWithNoAccessRight_SaysWhoHoldsIt(int page, bool windows)
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map,
                Siblings = new[] { new HidSibling(3, (ushort)page) },
                BeginWith = (io, _, _) => new FakeWorkerSession(_family, io),
            });
            var unit = Unit();
            var sibling = HidLightingWorkerTests.Collection(iface: 3, page: (ushort)page, tail: "2");
            _channelFor = path => new FakeReportChannel
            {
                Path = path, FeatureOnly = path == sibling.Path, OutputReportLength = 65, FeatureReportLength = 65,
            };
            using var backend = Started(unit, sibling);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            var listed = backend.Snapshot.Units.Single(u => u.Key == unit.Path);
            Assert.True(listed.Asleep);
            Assert.Equal(windows, listed.WindowsHolds);
            Assert.Equal(!windows, listed.HeldByProgram);
            Assert.Empty(_family.Calls());
        }

        /// <summary>A family that opens its collections itself, and says one of
        /// them opened with no access right.</summary>
        private sealed class HeldOwnFamily : IOwnLookFamily
        {
            public volatile bool Held = true;
            public HidLightingModel Model { get; }
            public string Name => "Held own";

            public HeldOwnFamily() => Model = new HidLightingModel
            {
                Family = this, Name = "Held own", Map = FakeWorkerFamily.Map(),
                Open = _ => null,
                OpenFoundHeld = () => Held,
                BeginOwn = (_, _) => null,
            };

            public HidLightingModel Match(HidCollectionInfo collection) => null;

            public IReadOnlyList<OwnDevice> Look() => new[] { new OwnDevice("own:1", Model) { Container = Box } };
        }

        /// <summary>The unit of such a family says another program has it open,
        /// and one that found nothing held still reads asleep.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnOwnLookUnit_WhoseCollectionAnotherProgramHolds_SaysSo(bool held)
        {
            var family = new HeldOwnFamily { Held = held };
            using var backend = Started(new IHidLightingFamily[] { family }, Array.Empty<HidCollectionInfo>());
            Link(new HidCollectionInfo { Path = "own:1" });
            Light(0x112233);
            Passes(backend);
            var listed = backend.Snapshot.Units[0];
            Assert.True(listed.Asleep);
            Assert.Equal(held, listed.HeldByProgram);
        }

        /// <summary>The same handle on a model that takes feature reports there
        /// begins its session, and nothing says Windows holds it.</summary>
        [Fact]
        public void AModelThatTakesAHeldCollection_BeginsItsSession_AndNothingSaysWindowsHoldsIt()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin, HeldByWindows = true,
            });
            _channelFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, FeatureReportLength = 65 };
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            var listed = backend.Snapshot.Units[0];
            Assert.False(listed.Refused);
            Assert.False(listed.WindowsHolds);
        }

        /// <summary>A device that opens and takes no session has not left, so
        /// no walk is asked for, the unit says it refused, and each try waits
        /// longer than the one before, up to the bound.</summary>
        [Fact]
        public void ARefusedSession_AsksNoWalk_SaysSo_AndBacksOff()
        {
            int begins = 0;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map,
                Begin = (_, _) =>
                {
                    Interlocked.Increment(ref begins);
                    return null;
                },
            });
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(1, Volatile.Read(ref begins));
            Assert.Equal(0, Volatile.Read(ref _walksAsked));
            var listed = backend.Snapshot.Units[0];
            Assert.True(listed.Asleep);
            Assert.True(listed.Refused);

            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(2, Volatile.Read(ref begins));

            // The second refusal waits twice as long.
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(2, Volatile.Read(ref begins));
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(3, Volatile.Read(ref begins));

            // Up to the bound.
            for (int i = 0; i < 8; i++)
            {
                Advance(HidLightingBackend.RefusedRetryMaxMs);
                Passes(backend);
            }
            int before = Volatile.Read(ref begins);
            Advance(HidLightingBackend.RefusedRetryMaxMs - 1);
            Passes(backend);
            Assert.Equal(before, Volatile.Read(ref begins));
            Advance(1);
            Passes(backend);
            Assert.Equal(before + 1, Volatile.Read(ref begins));
            Assert.Equal(0, Volatile.Read(ref _walksAsked));
        }

        /// <summary>A session that opens at last clears the refusal.</summary>
        [Fact]
        public void ASessionThatOpens_ClearsTheRefusal()
        {
            bool refuse = true;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map,
                Begin = (io, _) => Volatile.Read(ref refuse) ? null : new FakeWorkerSession(_family, io),
            });
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.True(backend.Snapshot.Units[0].Refused);
            Volatile.Write(ref refuse, false);
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            var listed = backend.Snapshot.Units[0];
            Assert.False(listed.Asleep);
            Assert.False(listed.Refused);
            Assert.Equal(1, Count("paint 112233,112233"));
        }

        /// <summary>A handle that does not open, or a write that fails, may
        /// mean the device left, so those still ask for a walk.</summary>
        [Fact]
        public void AWriteThatFails_AndAHandleThatDoesNotOpen_StillAskForAWalk()
        {
            var unit = Unit();
            using var backend = LitThenFailed(unit, out _);
            Assert.True(Volatile.Read(ref _walksAsked) >= 1);

            int before = Volatile.Read(ref _walksAsked);
            _channelFor = _ => null;
            // The owed frame goes out and the next open finds no handle.
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.True(Volatile.Read(ref _walksAsked) >= before);
        }

        /// <summary>The retry counts from the end of what the failure held
        /// the worker for, so a long wait cannot leave it due at once.</summary>
        [Fact]
        public void AFailureAfterALongWait_WaitsItsFullRetry()
        {
            int waitMs = 0;
            bool fails = false;
            int begins = 0;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map,
                Begin = (io, _) =>
                {
                    Interlocked.Increment(ref begins);
                    return new OwingSession(_family, io, () => Volatile.Read(ref fails), () => Volatile.Read(ref waitMs));
                },
            });
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Volatile.Write(ref fails, true);
            Volatile.Write(ref waitMs, 10_000);
            Light(0x445566);
            Passes(backend);
            Volatile.Write(ref fails, false);
            Volatile.Write(ref waitMs, 0);
            int atFailure = Volatile.Read(ref begins);
            Assert.True(backend.Snapshot.Units[0].Asleep);

            Advance(HidLightingBackend.RetryMs - 1);
            Passes(backend);
            Assert.True(backend.Snapshot.Units[0].Asleep);
            Assert.Equal(atFailure, Volatile.Read(ref begins));
            Advance(1);
            Passes(backend);
            Assert.False(backend.Snapshot.Units[0].Asleep);
        }

        // ─────────────────────────────────────────────
        //  A replug, a resume
        // ─────────────────────────────────────────────

        /// <summary>A device replugged between two walks comes back on the
        /// same path, so the walk's list does not change. Its old handle is
        /// dead, and the walk closes it, so the next pass opens a new session
        /// and the same picture goes out again.</summary>
        [Fact]
        public void AHandleThatDied_ClosesAtTheWalk_AndTheSamePictureGoesOutAgain()
        {
            OwingModel();
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            Assert.Equal(1, Count("begin"));

            var first = Assert.Single(Opened());
            first.StillConnected = false;
            backend.Rescan(new[] { unit });
            Passes(backend);

            Assert.True(first.Disposed);
            Assert.Equal(2, Count("begin"));
            Assert.Equal(2, Count("paint 112233,112233"));
            Assert.False(backend.Snapshot.Units[0].Asleep);
        }

        /// <summary>A handle still connected is left alone at a walk.</summary>
        [Fact]
        public void AHandleStillConnected_StaysOpenAcrossAWalk()
        {
            OwingModel();
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            backend.Rescan(new[] { unit });
            Passes(backend);
            Assert.Equal(1, Count("begin"));
            Assert.Equal(1, Count("paint 112233,112233"));
            Assert.False(Assert.Single(Opened()).Disposed);
        }

        /// <summary>A device that lost its colors across sleep keeps its
        /// collection in the walk, so a resume sends every open unit's
        /// picture once more, on the session it has.</summary>
        [Fact]
        public void AResume_SendsThePictureAgain_OnTheOpenSession()
        {
            OwingModel();
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            backend.NoteResume();
            Passes(backend);
            Assert.Equal(2, Count("paint 112233,112233"));
            Assert.Equal(1, Count("begin"));
            Passes(backend);
            Assert.Equal(2, Count("paint 112233,112233"));
        }

        /// <summary>A session whose recheck found the device showed something
        /// else since says so, and the unchanged picture goes out again.</summary>
        [Fact]
        public void ASessionThatSaysARepaintIsDue_GetsTheSamePictureAgain()
        {
            bool due = false;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, RecheckMs = 100,
                Begin = (io, _) => new RepaintingSession(_family, io, () => Interlocked.Exchange(ref due, false)),
            });
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            Advance(100);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            Volatile.Write(ref due, true);
            Advance(100);
            Passes(backend);
            Assert.Equal(2, Count("paint 112233,112233"));
        }

        private sealed class RepaintingSession : FakeWorkerSession
        {
            private readonly Func<bool> _due;

            public RepaintingSession(FakeWorkerFamily family, IHidReportChannel io, Func<bool> due) : base(family, io)
                => _due = due;

            internal override bool RepaintDue => _due();
        }

        // ─────────────────────────────────────────────
        //  One device does not hold up another's keep-alive
        // ─────────────────────────────────────────────

        /// <summary>A family with one model per collection's product, so two
        /// units of one test behave differently.</summary>
        private sealed class TwoModelFamily : IHidLightingFamily
        {
            public HidLightingModel Slow, Kept, Other;
            public string Name => "Two models";

            public HidLightingModel Match(HidCollectionInfo collection)
                => collection.ProductId == 1 ? Slow : collection.ProductId == 2 ? Kept : collection.ProductId == 3 ? Other : null;
        }

        /// <summary>A session that runs what the test hands it ahead of each
        /// picture, whose picture fails when that answers false.</summary>
        private sealed class HookedSession : FakeWorkerSession
        {
            private readonly Func<Action<int>, bool> _before;

            public HookedSession(FakeWorkerFamily family, IHidReportChannel io, Func<Action<int>, bool> before) : base(family, io)
                => _before = before;

            public byte[][] Frames { get; init; } = Array.Empty<byte[]>();

            public override byte[][] CrashFrames => Frames;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
                IReadOnlyList<int> colors)
                => _before(wait) ? base.Paint(io, wait, lights, colors) : HidLightingPaint.Failed;
        }

        /// <summary>A session that notes the thread of each keep-alive.</summary>
        private sealed class ThreadNotingSession : FakeWorkerSession
        {
            private readonly List<int> _threads;

            public ThreadNotingSession(FakeWorkerFamily family, IHidReportChannel io, List<int> threads) : base(family, io)
                => _threads = threads;

            public override bool KeepAlive(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
                IReadOnlyList<int> colors)
            {
                lock (_threads) _threads.Add(Environment.CurrentManagedThreadId);
                return true;
            }
        }

        private HidLightingBackend UnstartedGivingUpFast(IHidLightingFamily[] families)
        {
            var backend = new HidLightingBackend(families, Open, _ => Box, Tick, OneShot, OneShot, ms => Advance(ms),
                idleTickMs: 5, predecessorJoinMs: 50, stopJoinMs: 50, query: path => _queryFor(path));
            backend.RescanRequested += () => Interlocked.Increment(ref _walksAsked);
            return backend;
        }

        /// <summary>A worker a stop gave up on hands back while the next run's
        /// worker still waits for it, since nothing else has the device. Once
        /// that worker has left its wait it may have the device open, and the
        /// old one sends it nothing more.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AWorkerAStopGaveUpOn_HandsBack_OnlyWhileItsSuccessorWaitsForIt(bool successorLeftItsWait)
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var log = new FakeWorkerFamily();
            Thread left = null;
            int begins = 0;
            log.Model = new HidLightingModel
            {
                Family = log, Name = "Held", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) =>
                {
                    bool first = Interlocked.Increment(ref begins) == 1;
                    return new HookedSession(log, io, _ =>
                    {
                        if (first)
                        {
                            left = Thread.CurrentThread;
                            entered.Set();
                            gate.Wait(30000);
                        }
                        return true;
                    });
                },
            };
            var unit = Unit();
            HidLightingBackend Make() => new HidLightingBackend(new IHidLightingFamily[] { log }, Open, _ => Box, Tick, OneShot, OneShot, ms => Advance(ms),
                idleTickMs: 5, predecessorJoinMs: successorLeftItsWait ? 50 : 30000, stopJoinMs: 50, query: path => _queryFor(path));
            using var backend = Make();
            Link(unit);
            Light(0x112233);
            backend.Start();
            backend.Rescan(new[] { unit });
            Assert.True(entered.Wait(30000), "the first worker went into its wait");
            backend.Dispose();

            // Each engine start builds its own backends.
            using var next = Make();
            next.Start();
            next.Rescan(new[] { unit });
            if (successorLeftItsWait)
            {
                Passes(next);
                Assert.Equal(2, Volatile.Read(ref begins));
            }
            else
            {
                // It waits for the old worker, so nothing has begun.
                Assert.Equal(1, Volatile.Read(ref begins));
            }
            gate.Set();
            Assert.True(left.Join(30000), "the first worker ended");
            Assert.Equal(successorLeftItsWait ? 0 : 1, log.Calls().Count(c => c == "handback"));
            // The map of a unit the next run lights is that run's.
            Assert.True(!successorLeftItsWait || PeripheralLightMaps.For(PathOf(unit)) != null, "the next run's map stays up");
        }

        /// <summary>A run stopped inside its wait for the old worker has opened
        /// nothing, so it takes nothing over: the old worker still hands back
        /// when it comes out, also after that wait gave up.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_HandsBack_WhenTheNextRunStoppedInsideItsWait()
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var log = new FakeWorkerFamily();
            Thread left = null;
            log.Model = new HidLightingModel
            {
                Family = log, Name = "Held", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new HookedSession(log, io, _ =>
                {
                    left = Thread.CurrentThread;
                    entered.Set();
                    gate.Wait(30000);
                    return true;
                }),
            };
            var unit = Unit();
            using var backend = UnstartedGivingUpFast(new IHidLightingFamily[] { log });
            Link(unit);
            Light(0x112233);
            backend.Start();
            backend.Rescan(new[] { unit });
            Assert.True(entered.Wait(30000), "the first worker went into its wait");
            backend.Dispose();

            // Its wait is longer than its stop's, so the stop comes first.
            var next = new HidLightingBackend(new IHidLightingFamily[] { log }, Open, _ => Box, Tick, OneShot, OneShot, ms => Advance(ms),
                idleTickMs: 5, predecessorJoinMs: 300, stopJoinMs: 1, query: path => _queryFor(path));
            next.Start();
            next.Dispose();
            Assert.True(SpinWait.SpinUntil(() => !next.WorkerAlive, 30000), "the next worker ended");

            gate.Set();
            Assert.True(left.Join(30000), "the first worker ended");
            Assert.Single(log.Calls(), c => c == "handback");
            Assert.Null(PeripheralLightMaps.For(PathOf(unit)));
        }

        /// <summary>The next run tried the unit and it did not open. That run
        /// has no session to hand back, so the old worker still does.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_HandsBack_WhenTheNextRunsOpenFails()
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var log = new FakeWorkerFamily();
            Thread left = null;
            log.Model = new HidLightingModel
            {
                Family = log, Name = "Held", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new HookedSession(log, io, _ =>
                {
                    left = Thread.CurrentThread;
                    entered.Set();
                    gate.Wait(30000);
                    return true;
                }),
            };
            var unit = Unit();
            using var backend = UnstartedGivingUpFast(new IHidLightingFamily[] { log });
            Link(unit);
            Light(0x112233);
            backend.Start();
            backend.Rescan(new[] { unit });
            Assert.True(entered.Wait(30000), "the first worker went into its wait");
            backend.Dispose();

            _channelFor = _ => null;
            using var next = UnstartedGivingUpFast(new IHidLightingFamily[] { log });
            next.Start();
            next.Rescan(new[] { unit });
            Passes(next);
            gate.Set();
            Assert.True(left.Join(30000), "the first worker ended");
            Assert.Single(log.Calls(), c => c == "handback");
        }

        /// <summary>A unit the next run leaves alone is still the old worker's
        /// to hand back, once that run's worker has left its wait too, since
        /// nobody else knows the unit is lit.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_HandsBackAUnitTheNextRunLeavesAlone()
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var log = new FakeWorkerFamily();
            Thread left = null;
            log.Model = new HidLightingModel
            {
                Family = log, Name = "Held", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new HookedSession(log, io, _ =>
                {
                    left = Thread.CurrentThread;
                    entered.Set();
                    gate.Wait(30000);
                    return true;
                }),
            };
            var unit = Unit();
            using var backend = UnstartedGivingUpFast(new IHidLightingFamily[] { log });
            Link(unit);
            Light(0x112233);
            backend.Start();
            backend.Rescan(new[] { unit });
            Assert.True(entered.Wait(30000), "the first worker went into its wait");
            backend.Dispose();

            Link();
            using var next = UnstartedGivingUpFast(new IHidLightingFamily[] { log });
            next.Start();
            next.Rescan(new[] { unit });
            Passes(next);
            gate.Set();
            Assert.True(left.Join(30000), "the first worker ended");
            Assert.Single(log.Calls(), c => c == "handback");
        }

        /// <summary>A worker a stop gave up on comes out of its wait after the
        /// next run has started. Its waits serve none of the next run's units,
        /// and its end leaves the next run's keep-alives going: a session of
        /// that run that waits still keeps the other unit lit.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_LeavesTheNextRunsKeepAlivesAlone()
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var log = new FakeWorkerFamily();
            var family = new TwoModelFamily();
            var keptOn = new List<int>();
            Thread left = null;
            int slowBegins = 0, nextWaitMs = 0;
            family.Slow = new HidLightingModel
            {
                Family = family, Name = "Slow", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) =>
                {
                    bool first = Interlocked.Increment(ref slowBegins) == 1;
                    return new HookedSession(log, io, wait =>
                    {
                        if (first)
                        {
                            left = Thread.CurrentThread;
                            entered.Set();
                            gate.Wait(30000);
                            wait(200);
                            return true;
                        }
                        int ms = Volatile.Read(ref nextWaitMs);
                        if (ms > 0) wait(ms);
                        return true;
                    });
                },
            };
            family.Kept = new HidLightingModel
            {
                Family = family, Name = "Kept", Map = FakeWorkerFamily.Map(), KeepAliveMs = 50,
                Begin = (io, _) => new ThreadNotingSession(log, io, keptOn),
            };
            var slow = HidLightingWorkerTests.Collection(pid: 1, tail: "1");
            var kept = HidLightingWorkerTests.Collection(pid: 2, tail: "2");
            using var backend = UnstartedGivingUpFast(new IHidLightingFamily[] { family });
            // The kept unit first, so the old worker has its session when it
            // comes out of its wait.
            Link(kept, slow);
            Light(0x112233);
            backend.Start();
            backend.Rescan(new[] { kept, slow });
            Assert.True(entered.Wait(30000), "the first worker went into its wait");
            backend.Dispose();

            using var next = UnstartedGivingUpFast(new IHidLightingFamily[] { family });
            next.Start();
            next.Rescan(new[] { kept, slow });
            Passes(next);
            Assert.Equal(2, Volatile.Read(ref slowBegins));
            int nextWorker;
            lock (keptOn) keptOn.Clear();
            Advance(50);
            Passes(next);
            lock (keptOn) nextWorker = keptOn.Single();

            gate.Set();
            Assert.True(left.Join(30000), "the first worker ended");
            lock (keptOn) Assert.All(keptOn, thread => Assert.Equal(nextWorker, thread));

            int before;
            lock (keptOn) before = keptOn.Count;
            Volatile.Write(ref nextWaitMs, 600);
            Light(0x445566);
            Passes(next);
            int during;
            lock (keptOn) during = keptOn.Count - before;
            // 600 ms of waits at a 50 ms keep-alive.
            Assert.True(during >= 10, $"keep-alives during the wait: {during}");
        }

        /// <summary>The write that worker comes out of failed. The list and the
        /// crash record are the next run's by then, so the failure changes
        /// neither.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_WhoseWriteFails_LeavesTheNextRunsListAndRecordAlone()
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var log = new FakeWorkerFamily();
            var family = new TwoModelFamily();
            Thread left = null;
            int begins = 0;
            family.Slow = new HidLightingModel
            {
                Family = family, Name = "Slow", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) =>
                {
                    bool first = Interlocked.Increment(ref begins) == 1;
                    return new HookedSession(log, io, wait =>
                    {
                        if (!first) return true;
                        left = Thread.CurrentThread;
                        entered.Set();
                        gate.Wait(30000);
                        return false;
                    }) { Frames = first ? new[] { HandBackFrame } : Array.Empty<byte[]>() };
                },
            };
            var slow = HidLightingWorkerTests.Collection(pid: 1, tail: "1");
            using var backend = UnstartedGivingUpFast(new IHidLightingFamily[] { family });
            Link(slow);
            Light(0x112233);
            backend.Start();
            backend.Rescan(new[] { slow });
            Assert.True(entered.Wait(30000), "the first worker went into its write");
            backend.Dispose();

            using var next = UnstartedGivingUpFast(new IHidLightingFamily[] { family });
            next.Start();
            next.Rescan(new[] { slow });
            Passes(next);
            Assert.Equal(2, Volatile.Read(ref begins));
            var theirs = next.Snapshot;

            gate.Set();
            Assert.True(left.Join(30000), "the first worker ended");
            Assert.Same(theirs, next.Snapshot);
            Assert.False(next.Snapshot.Units.Single().Asleep);
            Assert.Empty(next.Recorded);
            lock (_oneShots) Assert.Empty(_oneShots);
            Assert.NotNull(PeripheralLightMaps.For(PathOf(slow)));
        }

        /// <summary>A program of a unit's model starts, or OpenRGB takes its
        /// path, while another unit's session waits. Nothing goes to that unit
        /// from inside the wait, and no picture on its turn after it. A third
        /// unit in the same wait is the proof the keep-alives ran.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AKeepAliveInsideAWait_SkipsAUnitAProgramOrOpenRgbHas(bool program)
        {
            var log = new FakeWorkerFamily();
            var family = new TwoModelFamily();
            var hub = new VendorProgram("Fake hub", "FakeHub");
            var slow = HidLightingWorkerTests.Collection(pid: 1, tail: "1");
            var kept = HidLightingWorkerTests.Collection(pid: 2, tail: "2");
            var other = HidLightingWorkerTests.Collection(pid: 3, tail: "3");
            int armed = 0, keptBefore = 0, otherBefore = 0;
            family.Slow = new HidLightingModel
            {
                Family = family, Name = "Slow", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new HookedSession(log, io, wait =>
                {
                    if (Interlocked.Exchange(ref armed, 0) == 0) return true;
                    if (program)
                        PeripheralOutputs.Presence = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "FakeHub.exe" }, new[] { hub });
                    else
                        PeripheralOutputs.PublishLinks(new LinkTable(new[]
                        {
                            new DeviceLinks { Device = Device, Lighting = new[] { PathOf(slow), PathOf(other) }, TakenByOpenRgb = new[] { PathOf(kept) } },
                        }));
                    keptBefore = log.Calls().Count(c => c.StartsWith("kept "));
                    otherBefore = log.Calls().Count(c => c.StartsWith("other "));
                    wait(600);
                    return true;
                }),
            };
            family.Kept = new HidLightingModel
            {
                Family = family, Name = "Kept", Map = FakeWorkerFamily.Map(), KeepAliveMs = 50,
                Programs = program ? new[] { hub } : Array.Empty<VendorProgram>(),
                Begin = (io, _) => new FakeWorkerSession(log, io) { KeepAliveLine = "kept" },
            };
            family.Other = new HidLightingModel
            {
                Family = family, Name = "Other", Map = FakeWorkerFamily.Map(), KeepAliveMs = 50,
                Begin = (io, _) => new FakeWorkerSession(log, io) { KeepAliveLine = "other" },
            };
            using var backend = Started(new IHidLightingFamily[] { family }, slow, kept, other);
            Link(slow, kept, other);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(3, log.Calls().Count(c => c == "paint 112233,112233"));

            Volatile.Write(ref armed, 1);
            Light(0x445566);
            Passes(backend);
            Assert.Equal(0, Volatile.Read(ref armed));
            Assert.Equal(keptBefore, log.Calls().Count(c => c.StartsWith("kept ")));
            Assert.True(log.Calls().Count(c => c.StartsWith("other ")) - otherBefore >= 10, "the other unit's keep-alives ran");
            Assert.Equal(2, log.Calls().Count(c => c == "paint 445566,445566"));
        }

        /// <summary>A keep-alive that failed inside a wait says the old handle
        /// failed. A walk then finds that handle dead and closes it, and the
        /// device on its new handle answers, so the old failure costs nothing
        /// more: no second time asleep and no walk asked for.</summary>
        [Fact]
        public void AKeepAliveThatFailedOnAHandleAWalkClosed_DoesNotFailTheNewOne()
        {
            var log = new FakeWorkerFamily();
            var family = new TwoModelFamily();
            HidLightingBackend backend = null;
            var slow = HidLightingWorkerTests.Collection(pid: 1, tail: "1");
            var kept = HidLightingWorkerTests.Collection(pid: 2, tail: "2");
            int armed = 0, keptBegins = 0;
            family.Slow = new HidLightingModel
            {
                Family = family, Name = "Slow", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new HookedSession(log, io, wait =>
                {
                    if (Interlocked.Exchange(ref armed, 0) == 0) return true;
                    wait(100);
                    Opened().First(c => c.Path == kept.Path).StillConnected = false;
                    backend.Rescan(new[] { kept, slow });
                    return true;
                }),
            };
            family.Kept = new HidLightingModel
            {
                Family = family, Name = "Kept", Map = FakeWorkerFamily.Map(), KeepAliveMs = 50,
                Begin = (io, _) => new FakeWorkerSession(log, io)
                {
                    KeepAliveLine = "kept", KeepAliveFails = Interlocked.Increment(ref keptBegins) == 1,
                },
            };
            backend = Started(new IHidLightingFamily[] { family }, kept, slow);
            using var stopped = backend;
            Link(kept, slow);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(1, Volatile.Read(ref keptBegins));

            Volatile.Write(ref armed, 1);
            Light(0x445566);
            Passes(backend, 3);
            Assert.Equal(2, Volatile.Read(ref keptBegins));
            Assert.False(backend.Snapshot.Units.Single(u => u.Key == kept.Path).Asleep);
            Assert.Equal(0, Volatile.Read(ref _walksAsked));
        }

        /// <summary>A session that waits its picture's length, as the Dell G
        /// series' reports do, serves another unit's keep-alive from inside
        /// the wait.</summary>
        [Fact]
        public void ALongWaitInOneSession_StillSendsAnotherUnitsKeepAlive()
        {
            var log = new FakeWorkerFamily();
            var family = new TwoModelFamily();
            family.Slow = new HidLightingModel
            {
                Family = family, Name = "Slow", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new OwingSession(log, io, () => false, () => 600),
            };
            family.Kept = new HidLightingModel
            {
                Family = family, Name = "Kept", Map = FakeWorkerFamily.Map(), KeepAliveMs = 50,
                Begin = (io, _) => new FakeWorkerSession(log, io) { KeepAliveLine = "kept" },
            };
            var slow = HidLightingWorkerTests.Collection(pid: 1, tail: "1");
            var kept = HidLightingWorkerTests.Collection(pid: 2, tail: "2");
            using var backend = Started(new IHidLightingFamily[] { family }, slow, kept);
            Link(slow, kept);
            Light(0x112233);
            Passes(backend);
            Assert.Contains("paint 112233,112233", log.Calls());

            int before = log.Calls().Count(c => c.StartsWith("kept "));
            Light(0x445566);
            Passes(backend);
            int after = log.Calls().Count(c => c.StartsWith("kept "));
            // 600 ms of waits at a 50 ms keep-alive.
            Assert.True(after - before >= 10, $"keep-alives during the wait: {after - before}");
        }

        /// <summary>A keep-alive that fails inside another session's wait
        /// lists its own unit asleep on that unit's turn.</summary>
        [Fact]
        public void AKeepAliveThatFailsInsideAWait_ListsItsOwnUnitAsleep()
        {
            var log = new FakeWorkerFamily();
            var family = new TwoModelFamily();
            family.Slow = new HidLightingModel
            {
                Family = family, Name = "Slow", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new OwingSession(log, io, () => false, () => 600),
            };
            family.Kept = new HidLightingModel
            {
                Family = family, Name = "Kept", Map = FakeWorkerFamily.Map(), KeepAliveMs = 50,
                Begin = (io, _) => new FakeWorkerSession(log, io) { KeepAliveLine = "kept", KeepAliveFails = true },
            };
            var slow = HidLightingWorkerTests.Collection(pid: 1, tail: "1");
            var kept = HidLightingWorkerTests.Collection(pid: 2, tail: "2");
            using var backend = Started(new IHidLightingFamily[] { family }, slow, kept);
            Link(slow, kept);
            Light(0x112233);
            Passes(backend);
            Light(0x445566);
            Passes(backend);
            Assert.True(backend.Snapshot.Units.Single(u => u.Key == kept.Path).Asleep);
            Assert.False(backend.Snapshot.Units.Single(u => u.Key == slow.Path).Asleep);
        }

        // ─────────────────────────────────────────────
        //  A unit that wakes ahead of the link pass
        // ─────────────────────────────────────────────

        /// <summary>A unit lit with a backup of its own, whose write failed, and
        /// whose path the link pass took away while it slept.</summary>
        private HidLightingBackend LitWithOwnBackupThenFailedAndUnlinked(HidCollectionInfo unit, Func<int> countRepairs, Action repaired)
        {
            OwingModel(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin,
                Owed = _ => true,
                Repair = (_, _) =>
                {
                    repaired();
                    return true;
                },
            });
            var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Advance(HidLightingBackend.RepairGraceMs);
            _paintFails = true;
            Light(0x445566);
            Passes(backend);
            _paintFails = false;
            Assert.True(backend.Snapshot.Units[0].Asleep);
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            Assert.Equal(0, countRepairs());
            return backend;
        }

        /// <summary>The link pass has not linked the unit again at the moment it
        /// wakes, so its claim looks gone. The backup stays until the pass has
        /// had its time, and the session the pass opens takes the unit back with
        /// nothing handed back between.</summary>
        [Fact]
        public void AUnitThatWakes_IsNotHandedBack_AheadOfTheLinkPassThatLinksItAgain()
        {
            int repairs = 0;
            var unit = Unit();
            using var backend = LitWithOwnBackupThenFailedAndUnlinked(unit, () => Volatile.Read(ref repairs),
                () => Interlocked.Increment(ref repairs));
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.False(backend.Snapshot.Units[0].Asleep);
            Assert.Equal(0, Volatile.Read(ref repairs));

            Link(unit);
            Advance(PeripheralOutputHost.LinkMs);
            Passes(backend);
            Assert.Equal(0, Volatile.Read(ref repairs));
            Assert.Equal(2, Count("begin"));
        }

        /// <summary>A unit no claim comes back to is handed back, once the link
        /// pass has had its time.</summary>
        [Fact]
        public void AUnitThatWakes_WithNoClaimToComeBack_IsHandedBack_AfterTheLinkPass()
        {
            int repairs = 0;
            var unit = Unit();
            using var backend = LitWithOwnBackupThenFailedAndUnlinked(unit, () => Volatile.Read(ref repairs),
                () => Interlocked.Increment(ref repairs));
            Advance(HidLightingBackend.RetryMs);
            Passes(backend);
            Assert.Equal(0, Volatile.Read(ref repairs));
            Advance(2 * PeripheralOutputHost.LinkMs);
            Passes(backend);
            Assert.True(Volatile.Read(ref repairs) >= 1);
        }

        // ─────────────────────────────────────────────
        //  A twin that outranks a lit unit
        // ─────────────────────────────────────────────

        /// <summary>A unit is lit, and an identical device arrives whose path sorts
        /// first and whose container Windows cannot name yet. Nothing says the two
        /// are one device, so the lit unit stays lit. Once the arrival's container
        /// is named, a twin in another container leaves it alone, and a collection
        /// of its own device takes its place.</summary>
        [Fact]
        public void ACollectionWhoseContainerHasNoNameYet_DoesNotOutrankALitUnit()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin,
                Outranks = (first, second) => string.CompareOrdinal(first.Path, second.Path) < 0,
            });
            var lit = Unit("2");
            var arrival = Unit("1");
            using var backend = Started(lit);
            Link(lit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));

            _containerOf = path => path == arrival.Path ? Guid.Empty : Box;
            backend.Rescan(new[] { arrival, lit });
            Passes(backend);
            Assert.Contains(lit.Path, backend.Snapshot.Units.Select(u => u.Key));
            Assert.False(Opened().Single(c => c.Path == lit.Path).Disposed);

            _containerOf = path => path == arrival.Path ? Device : Box;
            backend.Rescan(new[] { arrival, lit });
            Passes(backend);
            Assert.Contains(lit.Path, backend.Snapshot.Units.Select(u => u.Key));
        }

        /// <summary>The same arrival in the unit's own container takes its place:
        /// the rule stands wherever Windows names both.</summary>
        [Fact]
        public void ACollectionInTheSameContainer_OutranksALitUnit()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin,
                Outranks = (first, second) => string.CompareOrdinal(first.Path, second.Path) < 0,
            });
            var lit = Unit("2");
            var arrival = Unit("1");
            using var backend = Started(lit);
            Link(lit);
            Light(0x112233);
            Passes(backend);
            backend.Rescan(new[] { arrival, lit });
            Passes(backend);
            Assert.Equal(arrival.Path, Assert.Single(backend.Snapshot.Units).Key);
        }

        // ─────────────────────────────────────────────
        //  Stored pictures
        // ─────────────────────────────────────────────

        private HidLightingBackend HeldModelLit(HidCollectionInfo unit, int holdMs = 150, int repaintMs = 500)
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, HoldMs = holdMs, RepaintMs = repaintMs,
                Begin = (io, _) => new FakeWorkerSession(_family, io),
            });
            var backend = Started(unit);
            Link(unit);
            return backend;
        }

        /// <summary>A picture that never holds does not go out: the device
        /// stores each one, and a moving color would write it for hours. The
        /// device keeps its own lighting, and the row's line says why.</summary>
        [Fact]
        public void AColorThatNeverHolds_SendsNothing()
        {
            var unit = Unit();
            using var backend = HeldModelLit(unit);
            for (int i = 0; i < 20; i++)
            {
                Light(0x112233 + i);
                Passes(backend);
                Advance(33);
            }
            Assert.DoesNotContain(_family.Calls(), c => c.StartsWith("paint "));
        }

        /// <summary>A color that changes slower than the hold goes out at the
        /// repaint rate only for a burst. After that it goes out as stored
        /// writes come due, and the last color still lands.</summary>
        [Fact]
        public void StoredWrites_HaveABurstAndARate_AndTheLastColorStillLands()
        {
            var unit = Unit();
            using var backend = HeldModelLit(unit, holdMs: 10, repaintMs: 0);
            Light(0x000001);
            Passes(backend);
            Advance(10);
            Passes(backend);
            Assert.Equal(1, Count("paint 000001,000001"));
            int color = 1;
            for (int i = 0; i < HidLightingBackend.StoredWriteBurst + 5; i++)
            {
                Light(++color);
                Passes(backend);
                Advance(10);
                Passes(backend);
            }
            int painted = _family.Calls().Count(c => c.StartsWith("paint "));
            Assert.Equal(HidLightingBackend.StoredWriteBurst + 1, painted);

            string last = $"paint {color:X6},{color:X6}";
            Assert.Equal(0, Count(last));
            Advance(HidLightingBackend.StoredWriteRefillMs);
            Passes(backend);
            Assert.Equal(1, Count(last));
        }

        /// <summary>A model with no hold has no bucket.</summary>
        [Fact]
        public void AModelWithNoHold_HasNoStoredWriteBound()
        {
            _family.With();
            var unit = Unit();
            using var backend = Started(unit);
            Link(unit);
            for (int i = 0; i < HidLightingBackend.StoredWriteBurst + 5; i++)
            {
                Light(i + 1);
                Passes(backend);
            }
            Assert.Equal(HidLightingBackend.StoredWriteBurst + 5, _family.Calls().Count(c => c.StartsWith("paint ")));
        }

        /// <summary>A session that tells its device apart decides: a model with
        /// a hold whose session says this device holds nothing does not read as
        /// one that stores its pictures, once the session has opened.</summary>
        [Fact]
        public void ASessionThatHoldsNothing_IsListedAsOneThatStoresNothing()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, HoldMs = 150, RepaintMs = 500,
                Begin = (io, _) => new FakeWorkerSession(_family, io) { Hold = 0, Repaint = 0 },
            });
            var unit = Unit();
            using var backend = Started(unit);
            Assert.True(backend.Snapshot.Units[0].StoresPictures);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.False(backend.Snapshot.Units[0].StoresPictures);
        }

        /// <summary>The unit says so, for the tab's line.</summary>
        [Fact]
        public void AModelWithAHold_IsListedAsOneThatStoresItsPictures()
        {
            var unit = Unit();
            using var backend = HeldModelLit(unit);
            Assert.True(backend.Snapshot.Units[0].StoresPictures);
        }

        // ─────────────────────────────────────────────
        //  A fault in one family
        // ─────────────────────────────────────────────

        private sealed class ThrowingFamily : IHidLightingFamily
        {
            public string Name => "Throws";

            public HidLightingModel Match(HidCollectionInfo collection)
                => collection.ProductId == 9 ? throw new InvalidOperationException("match") : null;
        }

        /// <summary>A family that throws in Match costs its own collection,
        /// and the worker goes on lighting the others.</summary>
        [Fact]
        public void AFamilyThatThrowsInItsMatch_LeavesTheOthersListed()
        {
            OwingModel();
            var thrower = HidLightingWorkerTests.Collection(pid: 9, tail: "9");
            var good = Unit();
            using var backend = Started(new IHidLightingFamily[] { new ThrowingFamily(), _family }, thrower, good);
            Assert.True(backend.WorkerAlive);
            Assert.Contains(good.Path, backend.Snapshot.Units.Select(u => u.Key));
        }

        /// <summary>A unit whose model throws where the worker reads it costs
        /// that unit, listed asleep, and the worker goes on.</summary>
        [Fact]
        public void AFaultWhileServingOneUnit_ListsItAsleep_AndTheOthersStayLit()
        {
            var log = new FakeWorkerFamily();
            var family = new TwoModelFamily();
            family.Slow = new HidLightingModel
            {
                Family = family, Name = "Throws", Map = FakeWorkerFamily.Map(),
                Begin = (_, _) => null,
                Owed = _ => throw new InvalidOperationException("owed"),
            };
            family.Kept = new HidLightingModel
            {
                Family = family, Name = "Good", Map = FakeWorkerFamily.Map(),
                Begin = (io, _) => new FakeWorkerSession(log, io),
            };
            var bad = HidLightingWorkerTests.Collection(pid: 1, tail: "1");
            var good = HidLightingWorkerTests.Collection(pid: 2, tail: "2");
            using var backend = Started(new IHidLightingFamily[] { family }, bad, good);
            Link(good);
            Light(0x112233);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend, 3);
            Assert.True(backend.WorkerAlive);
            Assert.Contains("paint 112233,112233", log.Calls());
            Assert.True(backend.Snapshot.Units.Single(u => u.Key == bad.Path).Asleep);
        }

        // ─────────────────────────────────────────────
        //  Identity strings
        // ─────────────────────────────────────────────

        /// <summary>A check that came back false with no string read says
        /// nothing of the collection, so the next walk asks again. A check
        /// that read a string and said no is kept.</summary>
        [Fact]
        public void AnIdentityCheckThatReadNoStrings_IsAskedAgainAtTheNextWalk()
        {
            int asked = 0;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin,
                Identify = io =>
                {
                    Interlocked.Increment(ref asked);
                    return io.SerialNumber == "ours";
                },
            });
            var unit = Unit();
            _queryFor = path => new FakeReportChannel { Path = path, FeatureOnly = true };
            using var backend = Started(unit);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(1, Volatile.Read(ref asked));

            _queryFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, SerialNumber = "ours" };
            backend.Rescan(new[] { unit });
            Passes(backend);
            Assert.Equal(2, Volatile.Read(ref asked));
            Assert.Single(backend.Snapshot.Units);

            // A string that says it is not one stays decided.
            var other = Unit("2");
            _queryFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, ProductString = "Theirs" };
            backend.Rescan(new[] { unit, other });
            Passes(backend);
            Assert.Equal(3, Volatile.Read(ref asked));
            backend.Rescan(new[] { unit, other });
            Passes(backend);
            Assert.Equal(3, Volatile.Read(ref asked));
        }

        // ─────────────────────────────────────────────
        //  An own look reads the walk it is handed
        // ─────────────────────────────────────────────

        private sealed class WalkLookingFamily : IOwnLookFamily
        {
            public IReadOnlyList<HidCollectionInfo> Handed;
            public string Name => "Walk look";

            public HidLightingModel Match(HidCollectionInfo collection) => null;

            public IReadOnlyList<OwnDevice> Look() => throw new InvalidOperationException("walked again");

            public IReadOnlyList<OwnDevice> Look(IReadOnlyList<HidCollectionInfo> walk)
            {
                Handed = walk;
                return Array.Empty<OwnDevice>();
            }
        }

        /// <summary>A family that looks through the HID walk is handed the
        /// one the worker holds, and makes none of its own.</summary>
        [Fact]
        public void AnOwnLook_IsHandedTheWalkTheWorkerHolds()
        {
            var family = new WalkLookingFamily();
            var unit = Unit();
            var present = new[] { unit };
            using var backend = Started(new IHidLightingFamily[] { family }, present);
            Assert.Same(present, Volatile.Read(ref family.Handed));
        }
    }
}
