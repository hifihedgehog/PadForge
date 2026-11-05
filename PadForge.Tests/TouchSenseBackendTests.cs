using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The worker that buzzes TouchSense mice (#494), against scripted
    /// channels and a clock the test sets: which mice are units, when a
    /// level becomes a frame, the stop at every silence edge, the asleep
    /// rule after a failed write, the crash path, the last frames at a
    /// stop and the calls a stopped backend still takes. No device is
    /// opened or written to. Every interval runs on the test's clock, and
    /// a test waits for the worker by passes, never for a length of time.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class TouchSenseBackendTests : IDisposable
    {
        public TouchSenseBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private const string InterfaceGuid = "{fd826c66-3556-4845-9436-0fe7580ded02}";
        private const string Key = @"\\?\usb#vid_046d&pid_c030#5&2e1a4c7b&0&2#" + InterfaceGuid;
        private const string OtherKey = @"\\?\usb#vid_046d&pid_c032#5&2e1a4c7b&0&3#" + InterfaceGuid;
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid OtherBox = new("99999999-2222-3333-4444-555555555555");
        private static readonly TouchSenseMouse IFeel = new(Key, 0x046D, 0xC030);
        private static readonly TouchSenseMouse MouseMan = new(OtherKey, 0x046D, 0xC032);

        // The motor values the tests set and the frames they become on the
        // iFeel Mouse, and half on the iFeel MouseMan, whose model has a
        // factor of its own (the table TouchSenseProtocolTests checks level
        // by level).
        private const ushort HalfMotor = 32768, FullMotor = 65535, QuarterMotor = 16384;
        private const string Half = "11 0A 9E 0A 00 1E 00";
        private const string Full = "11 0A F0 0A 00 1E 00";
        private const string Quarter = "11 0A 75 0A 00 1E 00";
        private const string HalfOnTheMouseMan = "11 0A 96 0A 00 1E 00";
        private const string Stop = "13 00 00 00 00 00 00";
        private const string StopAll = "F3 00 00 00 00 00 00";
        private const string KeepAlive = "14 00 00 00 00 00 00";

        /// <summary>The frame a motor value becomes on the iFeel Mouse.</summary>
        private static string Frame(ushort motor)
            => BitConverter.ToString(TouchSenseProtocol.FrameFor(motor / 65535f, IFeel.ProductId)).Replace('-', ' ');

        /// <summary>One backend on scripted parts: the look, the container
        /// lookup, the channels and the clock are all the test's.</summary>
        private sealed class Bench : IDisposable
        {
            private long _now = 1_000_000;
            private readonly object _gate = new();
            private TouchSenseMouse[] _bound = Array.Empty<TouchSenseMouse>();
            private readonly ConcurrentDictionary<string, ITouchSenseChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
            private int _looks, _lookups, _containerLookups, _snapshotChanges, _walksAsked;

            public readonly TouchSenseChannels Table = new();
            public readonly ConcurrentDictionary<string, Guid> Containers = new(StringComparer.OrdinalIgnoreCase);
            public readonly TouchSenseBackend Backend;

            /// <summary>The look answers that it failed, or throws.</summary>
            public volatile bool LookFails, LookThrows;

            /// <summary>The container lookup throws.</summary>
            public volatile bool ContainerThrows;

            /// <param name="keepAlive">Null leaves the keep-alive as a
            /// build has it, which is what the tests of "not sent" run on.</param>
            public Bench(bool? keepAlive = null, int activeTickMs = 1, int idleTickMs = 1,
                int predecessorJoinMs = TouchSenseBackend.PredecessorJoinMs, int stopJoinMs = TouchSenseBackend.StopJoinMs)
            {
                Func<string, ITouchSenseChannel> channelOf = key =>
                {
                    Interlocked.Increment(ref _lookups);
                    return _channels.TryGetValue(key, out var channel) ? channel : null;
                };
                Func<IReadOnlyList<TouchSenseMouse>> find = () =>
                {
                    Interlocked.Increment(ref _looks);
                    if (LookThrows) throw new InvalidOperationException("scripted");
                    if (LookFails) return null;
                    lock (_gate) return _bound;
                };
                Func<string, Guid> containerOf = path =>
                {
                    Interlocked.Increment(ref _containerLookups);
                    if (ContainerThrows) throw new InvalidOperationException("scripted");
                    return Containers.TryGetValue(path, out var container) ? container : Guid.Empty;
                };
                Func<long> ticks = () => Interlocked.Read(ref _now);
                Backend = keepAlive is bool on
                    ? new TouchSenseBackend(Table, channelOf, find, containerOf, ticks, activeTickMs, idleTickMs, on,
                        predecessorJoinMs, stopJoinMs)
                    : new TouchSenseBackend(Table, channelOf, find, containerOf, ticks, activeTickMs, idleTickMs,
                        predecessorJoinMs: predecessorJoinMs, stopJoinMs: stopJoinMs);
                Backend.SnapshotChanged += () => Interlocked.Increment(ref _snapshotChanges);
                Backend.RescanRequested += () => Interlocked.Increment(ref _walksAsked);
            }

            public long Now => Interlocked.Read(ref _now);
            public int Looks => Volatile.Read(ref _looks);
            public int Lookups => Volatile.Read(ref _lookups);
            public int ContainerLookups => Volatile.Read(ref _containerLookups);
            public int SnapshotChanges => Volatile.Read(ref _snapshotChanges);
            public int WalksAsked => Volatile.Read(ref _walksAsked);
            public IReadOnlyList<DirectUnit> Units => Backend.Snapshot.Units;

            /// <summary>Returns once a whole pass of the worker has run
            /// after this call. A pass is counted as it starts, so of two
            /// more starts the first began after the call, and it has
            /// ended by the second.</summary>
            public void Pass()
            {
                int seen = Backend.PassCount;
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    Backend.Nudge();
                    return Backend.PassCount >= seen + 2;
                }, 30000), "the worker made no pass");
            }

            /// <summary>The wait the worker chose after a whole pass.</summary>
            public int Cadence()
            {
                Pass();
                return Backend.WaitMs;
            }

            /// <summary>Moves the clock on and lets the worker see it.</summary>
            public void Advance(long ms)
            {
                Interlocked.Add(ref _now, ms);
                Pass();
            }

            /// <summary>What the look finds from now on.</summary>
            public void Bind(params TouchSenseMouse[] mice)
            {
                lock (_gate) _bound = mice;
            }

            /// <summary>The host's walk, which is this family's cue to
            /// look, and the pass that makes the look.</summary>
            public void Rescan()
            {
                Backend.Rescan(null);
                Pass();
            }

            /// <summary>Opens a mouse the way its row does. Announced, the
            /// table tells the worker. Unannounced, only the worker's own
            /// interval finds the channel.</summary>
            public FakeTouchSenseChannel Open(string key, bool announce = false)
            {
                var channel = new FakeTouchSenseChannel(key, () => Now);
                Use(channel, announce);
                return channel;
            }

            public void Use(ITouchSenseChannel channel, bool announce = false)
            {
                _channels[channel.Path] = channel;
                if (announce) Table.Publish(channel);
            }

            public void Dispose() => Backend.Dispose();
        }

        private static void Link(params (Guid Row, string Key)[] rows)
            => PeripheralOutputs.PublishLinks(new LinkTable(rows.Select(r => new DeviceLinks
            {
                Device = r.Row,
                Haptics = new[] { new OutputPath(OutputFamily.TouchSenseUnit, r.Key) },
            })));

        /// <summary>A running backend with one bound iFeel Mouse: found,
        /// its container known, its channel open and one row linked to
        /// it.</summary>
        private static (Bench Bench, FakeTouchSenseChannel Channel, Guid Row) OneMouse(bool? keepAlive = null,
            int activeTickMs = 1, int idleTickMs = 1, int stopJoinMs = TouchSenseBackend.StopJoinMs)
        {
            var bench = new Bench(keepAlive, activeTickMs, idleTickMs, stopJoinMs: stopJoinMs);
            bench.Bind(IFeel);
            bench.Containers[Key] = Box;
            var channel = bench.Open(Key);
            var row = Guid.NewGuid();
            Link((row, Key));
            bench.Backend.Start();
            bench.Rescan();
            Assert.Single(bench.Units);
            return (bench, channel, row);
        }

        // ── Units ──

        [Fact]
        public void ABoundMouse_IsAUnitKeyedByItsInterfacePath_FoundByTheLookAWalkCues()
        {
            using var bench = new Bench();
            Assert.Equal(OutputFamily.TouchSenseUnit, bench.Backend.Family);
            Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);
            bench.Bind(IFeel);
            bench.Containers[Key] = Box;
            bench.Backend.Start();

            // A bound mouse is in no walk of the HID collections, so the
            // look waits for the walk that cues it.
            bench.Pass();
            Assert.Equal(0, bench.Looks);
            Assert.False(bench.Backend.Snapshot.Scanned);

            bench.Rescan();
            Assert.Equal(1, bench.Looks);
            var snapshot = bench.Backend.Snapshot;
            Assert.True(snapshot.Scanned);
            Assert.Equal(new DirectUnit(OutputFamily.TouchSenseUnit, Key, Box, 0x046D, 0xC030) { Name = "Logitech iFeel Mouse" },
                Assert.Single(snapshot.Units));
            Assert.Equal(1, bench.SnapshotChanges);

            // A walk that finds the same mouse is no news, and a container
            // that is known is not asked for again.
            bench.Rescan();
            Assert.Equal(2, bench.Looks);
            Assert.Same(snapshot, bench.Backend.Snapshot);
            Assert.Equal(1, bench.SnapshotChanges);
            Assert.Equal(1, bench.ContainerLookups);
            // A mouse nobody rumbles costs no channel lookup and no walk.
            Assert.Equal(0, bench.Lookups);
            Assert.Equal(0, bench.WalksAsked);
        }

        /// <summary>The first look that works is news even with no mouse:
        /// the device rows stop waiting for this backend to scan.</summary>
        [Fact]
        public void TheFirstLook_IsPublishedEvenWithNoMouse()
        {
            using var bench = new Bench();
            bench.Backend.Start();
            bench.Rescan();
            Assert.True(bench.Backend.Snapshot.Scanned);
            Assert.Empty(bench.Units);
            Assert.Equal(1, bench.SnapshotChanges);
            bench.Rescan();
            Assert.Equal(1, bench.SnapshotChanges);
        }

        [Fact]
        public void EachMouse_PlaysItsOwnRowsLevel_OnItsOwnChannel()
        {
            using var bench = new Bench();
            // Found in one order, listed by key, so two snapshots of the
            // same mice are equal.
            bench.Bind(MouseMan, IFeel, IFeel with { Path = Key.ToUpperInvariant() });
            bench.Containers[Key] = Box;
            bench.Containers[OtherKey] = OtherBox;
            var first = bench.Open(Key);
            var second = bench.Open(OtherKey);
            Guid rowOfFirst = Guid.NewGuid(), rowOfSecond = Guid.NewGuid();
            Link((rowOfFirst, Key), (rowOfSecond, OtherKey));
            bench.Backend.Start();
            bench.Rescan();

            Assert.Equal(new[]
            {
                new DirectUnit(OutputFamily.TouchSenseUnit, Key, Box, 0x046D, 0xC030) { Name = "Logitech iFeel Mouse" },
                new DirectUnit(OutputFamily.TouchSenseUnit, OtherKey, OtherBox, 0x046D, 0xC032) { Name = "Logitech iFeel MouseMan" },
            }, bench.Units);

            PeripheralOutputs.SetMotors(rowOfFirst, FullMotor, 0);
            bench.Pass();
            Assert.Equal(new[] { Full }, first.Frames);
            Assert.Equal(0, second.Attempts);

            // The stronger of the two motors is the level: the mouse has
            // one. Its strength byte is its own model's, not the byte the
            // same level is on the other mouse.
            PeripheralOutputs.SetMotors(rowOfSecond, QuarterMotor, HalfMotor);
            bench.Pass();
            Assert.Equal(new[] { HalfOnTheMouseMan }, second.Frames);
            Assert.Equal(new[] { Full }, first.Frames);
        }

        [Fact]
        public void AMouseTheLookNoLongerFinds_LeavesTheList_AndIsAUnitAgainWhenItIsBack()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            Assert.Equal(new[] { Half }, channel.Frames);

            // Unplugged, or its driver went back to Windows.
            bench.Bind();
            bench.Rescan();
            Assert.True(bench.Backend.Snapshot.Scanned);
            Assert.Empty(bench.Units);
            // A mouse that was buzzing and still takes a frame gets the stop.
            Assert.Equal(new[] { Half, Stop }, channel.Frames);
            // With no unit nothing more goes out, whatever the level.
            bench.Advance(1000);
            Assert.Equal(2, channel.Frames.Length);

            bench.Bind(IFeel);
            bench.Rescan();
            Assert.Single(bench.Units);
            // The level in force plays at once.
            Assert.Equal(new[] { Half, Stop, Half }, channel.Frames);
        }

        /// <summary>A look that fails says nothing about the mice: what was
        /// known stands, the mouse keeps playing, and the look is made
        /// again five seconds later with no walk to cue it.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFailedLook_KeepsWhatWasKnown_AndIsMadeAgainAfterFiveSeconds(bool throws)
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            var known = bench.Backend.Snapshot;
            int looks = bench.Looks;
            if (throws) bench.LookThrows = true;
            else bench.LookFails = true;

            bench.Rescan();
            Assert.Equal(looks + 1, bench.Looks);
            Assert.Same(known, bench.Backend.Snapshot);
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            Assert.Equal(new[] { Half }, channel.Frames);

            bench.Advance(TouchSenseBackend.ReopenMs - 1);
            Assert.Equal(looks + 1, bench.Looks);
            bench.Advance(1);
            Assert.Equal(looks + 2, bench.Looks);
            // Still failing: five seconds more.
            bench.Advance(TouchSenseBackend.ReopenMs - 1);
            Assert.Equal(looks + 2, bench.Looks);
            bench.LookThrows = false;
            bench.LookFails = false;
            bench.Advance(1);
            Assert.Equal(looks + 3, bench.Looks);
            Assert.Same(known, bench.Backend.Snapshot);

            // A look that worked is not made again until a walk cues one.
            bench.Advance(10L * TouchSenseBackend.ReopenMs);
            Assert.Equal(looks + 3, bench.Looks);
        }

        [Fact]
        public void AFirstLookThatFails_LeavesTheBackendUnscanned_UntilOneWorks()
        {
            using var bench = new Bench();
            bench.Bind(IFeel);
            bench.Containers[Key] = Box;
            bench.LookFails = true;
            bench.Backend.Start();
            bench.Rescan();
            Assert.Equal(1, bench.Looks);
            Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);
            Assert.Equal(0, bench.SnapshotChanges);

            bench.LookFails = false;
            bench.Advance(TouchSenseBackend.ReopenMs);
            Assert.Equal(2, bench.Looks);
            Assert.True(bench.Backend.Snapshot.Scanned);
            Assert.Single(bench.Units);
        }

        /// <summary>A unit with no container matches no row, so a container
        /// that could not be read is asked for again at every look until
        /// it is known, as the HID++ worker asks again for its own.</summary>
        [Fact]
        public void AContainerThatIsNotKnownYet_IsAskedForAgainAtTheNextLook()
        {
            using var bench = new Bench();
            bench.Bind(IFeel);
            bench.ContainerThrows = true;
            bench.Backend.Start();
            // A lookup that throws is a lookup that found nothing: the
            // mouse is listed, with no container yet.
            bench.Rescan();
            Assert.Equal(Guid.Empty, Assert.Single(bench.Units).ContainerId);
            Assert.Equal(1, bench.ContainerLookups);
            Assert.Equal(1, bench.SnapshotChanges);

            // One that answers with none is asked again too, and is no news.
            bench.ContainerThrows = false;
            bench.Rescan();
            Assert.Equal(Guid.Empty, Assert.Single(bench.Units).ContainerId);
            Assert.Equal(2, bench.ContainerLookups);
            Assert.Equal(1, bench.SnapshotChanges);

            bench.Containers[Key] = Box;
            bench.Rescan();
            Assert.Equal(Box, Assert.Single(bench.Units).ContainerId);
            Assert.Equal(3, bench.ContainerLookups);
            Assert.Equal(2, bench.SnapshotChanges);
            bench.Rescan();
            Assert.Equal(3, bench.ContainerLookups);
        }

        // ── From a level to frames ──

        [Fact]
        public void ALevelThatRisesFromSilence_GoesOutAtOnce_AndOnce()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            long t0 = bench.Now;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            Assert.Equal(new[] { Half }, channel.Frames);
            Assert.Equal(new[] { t0 }, channel.SentAt);
            bench.Pass();
            bench.Pass();
            Assert.Single(channel.Frames);
            Assert.Equal(0, bench.WalksAsked);
        }

        [Fact]
        public void AHeldLevel_GoesOutAgainEveryHundredMilliseconds()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            long t0 = bench.Now;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            bench.Advance(TouchSenseProtocol.ResendMs - 1);
            Assert.Single(channel.Frames);
            bench.Advance(1);
            Assert.Equal(new[] { t0, t0 + 100 }, channel.SentAt);

            // A second of it on the worker's own 10 ms tick: eleven frames
            // 100 ms apart, the first and the last included, all the same.
            for (int t = 110; t <= 1000; t += TouchSenseBackend.ActiveTickMs) bench.Advance(TouchSenseBackend.ActiveTickMs);
            Assert.Equal(Enumerable.Range(0, 11).Select(i => t0 + 100L * i), channel.SentAt);
            Assert.All(channel.Frames, frame => Assert.Equal(Half, frame));
        }

        /// <summary>A level that changes while the motor buzzes goes out
        /// once 33 ms have passed since the last frame, with no stop
        /// between the two buzz frames, and a level that keeps moving
        /// inside the gap goes out once, as it stands when the gap ends.</summary>
        [Fact]
        public void AChangedLevel_WaitsOutTheGap_WithNoStopBetweenTwoBuzzFrames()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            long t0 = bench.Now;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();

            bench.Advance(10);
            PeripheralOutputs.SetMotors(row, 39321, 0);
            bench.Pass();
            Assert.Single(channel.Frames);
            bench.Advance(22);
            Assert.Single(channel.Frames);
            bench.Advance(1);
            Assert.Equal(new[] { Half, "11 0A AE 0A 00 1E 00" }, channel.Frames);
            Assert.Equal(new[] { t0, t0 + TouchSenseProtocol.MinGapMs }, channel.SentAt);

            bench.Advance(10);
            PeripheralOutputs.SetMotors(row, 45874, 0);
            bench.Pass();
            bench.Advance(10);
            PeripheralOutputs.SetMotors(row, 52428, 0);
            bench.Pass();
            Assert.Equal(2, channel.Frames.Length);
            bench.Advance(12);
            Assert.Equal(2, channel.Frames.Length);
            bench.Advance(1);
            Assert.Equal(new[] { Half, Frame(39321), Frame(52428) }, channel.Frames);
            Assert.Equal(t0 + 2 * TouchSenseProtocol.MinGapMs, channel.SentAt[2]);

            // From there the level holds: again after 100 ms and not before.
            bench.Advance(TouchSenseProtocol.ResendMs - 1);
            Assert.Equal(3, channel.Frames.Length);
            bench.Advance(1);
            Assert.Equal(new[] { Half, Frame(39321), Frame(52428), Frame(52428) }, channel.Frames);
            Assert.DoesNotContain(Stop, channel.Frames);
        }

        [Fact]
        public void ALevelThatFallsSilent_IsStoppedAtOnce_AndNothingFollows()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            long t0 = bench.Now;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();

            // Inside the gap a changed level would wait out: a stop waits for nothing.
            bench.Advance(5);
            PeripheralOutputs.SetMotors(row, 0, 0);
            bench.Pass();
            Assert.Equal(new[] { Half, Stop }, channel.Frames);
            Assert.Equal(new[] { t0, t0 + 5 }, channel.SentAt);
            bench.Advance(TouchSenseProtocol.ResendMs);
            bench.Advance(TouchSenseProtocol.KeepAliveMs);
            bench.Advance(60000);
            Assert.Equal(2, channel.Frames.Length);

            // The next level starts from silence, so it goes out at once,
            // however soon after a stop.
            PeripheralOutputs.SetMotors(row, FullMotor, 0);
            bench.Pass();
            PeripheralOutputs.SetMotors(row, 0, 0);
            bench.Pass();
            bench.Advance(1);
            PeripheralOutputs.SetMotors(row, QuarterMotor, 0);
            bench.Pass();
            Assert.Equal(new[] { Half, Stop, Full, Stop, Quarter }, channel.Frames);
            Assert.Equal(bench.Now, channel.SentAt[4]);
        }

        /// <summary>The level under the vendor's cut plays nothing, and one
        /// that climbs past it raises no notice, so the worker's fast tick
        /// is what finds it.</summary>
        [Fact]
        public void ALevelUnderTheMotorsCut_PlaysNothing_AndPlaysOnceItClimbsPastIt()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            PeripheralOutputs.SetMotors(row, 655, 0);
            bench.Pass();
            bench.Advance(1000);
            Assert.Equal(0, channel.Attempts);
            PeripheralOutputs.SetMotors(row, 1311, 0);
            bench.Pass();
            // 0.02 of full is the first level that plays.
            Assert.Equal(new[] { "11 0A 4F 0A 00 1E 00" }, channel.Frames);
            bench.Advance(TouchSenseProtocol.MinGapMs);
            PeripheralOutputs.SetMotors(row, 655, 0);
            bench.Pass();
            Assert.Equal(new[] { "11 0A 4F 0A 00 1E 00", Stop }, channel.Frames);
        }

        [Fact]
        public void TheMouse_BuzzesAtTheStrongestLevelAmongItsLinkedRows()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            Guid second = Guid.NewGuid(), lit = Guid.NewGuid();
            var path = new OutputPath(OutputFamily.TouchSenseUnit, Key);
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = row, Haptics = new[] { path } },
                new DeviceLinks { Device = second, Haptics = new[] { path } },
                // Tied to the path for something that is not rumble.
                new DeviceLinks { Device = lit, Lighting = new[] { path } },
            }));

            PeripheralOutputs.SetMotors(lit, FullMotor, 0);
            bench.Pass();
            Assert.Equal(0, channel.Attempts);

            PeripheralOutputs.SetMotors(row, QuarterMotor, 0);
            bench.Pass();
            bench.Advance(TouchSenseProtocol.MinGapMs);
            PeripheralOutputs.SetMotors(second, 49151, 0);
            bench.Pass();
            bench.Advance(TouchSenseProtocol.MinGapMs);
            PeripheralOutputs.SetMotors(second, 0, 0);
            bench.Pass();
            bench.Advance(TouchSenseProtocol.MinGapMs);
            PeripheralOutputs.SetMotors(row, 0, 0);
            bench.Pass();
            Assert.Equal(new[] { Quarter, Frame(49151), Quarter, Stop }, channel.Frames);
        }

        // ── The silence edges ──

        /// <summary>A row that is unassigned or goes offline loses its path
        /// in the link table, and its level may still stand: the stop goes
        /// out, and no buzz while nothing links the mouse.</summary>
        [Fact]
        public void ARowThatLeavesItsMouse_GetsTheStop_AndNoBuzzWhileNothingLinksIt()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();

            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            bench.Pass();
            Assert.Equal(new[] { Half, Stop }, channel.Frames);
            bench.Advance(1000);
            Assert.Equal(2, channel.Frames.Length);

            Link((row, Key));
            bench.Pass();
            Assert.Equal(new[] { Half, Stop, Half }, channel.Frames);
        }

        /// <summary>Engine stop, the crash quiesce and focus suspend zero
        /// the levels where Step 2 does not run, and a row going offline
        /// zeroes its own: a stop each, and no buzz frame after it until a
        /// level comes back.</summary>
        [Fact]
        public void EverySilenceEdgeOfTheEngine_GetsTheStop_AndNoBuzzAfterIt()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            var edges = new Action[]
            {
                () => PeripheralOutputs.SilenceHaptics(),
                () => PeripheralOutputs.SilenceHaptics(keepRelayed: true),
                () => PeripheralOutputs.StopHaptics(row),
            };
            var expected = new List<string>();
            foreach (var edge in edges)
            {
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                bench.Pass();
                expected.Add(Half);
                Assert.Equal(expected, channel.Frames);

                edge();
                bench.Pass();
                expected.Add(Stop);
                Assert.Equal(expected, channel.Frames);
                bench.Advance(1000);
                Assert.Equal(expected, channel.Frames);
            }
        }

        /// <summary>Focus suspend leaves a level a linked PC drives
        /// playing, as it leaves a peer-driven controller's rumble.</summary>
        [Fact]
        public void ALevelALinkedPcDrives_KeepsPlayingThroughAFocusSuspend()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0, relayed: true);
            bench.Pass();
            PeripheralOutputs.SilenceHaptics(keepRelayed: true);
            bench.Pass();
            Assert.Equal(new[] { Half }, channel.Frames);
            bench.Advance(TouchSenseProtocol.ResendMs);
            Assert.Equal(new[] { Half, Half }, channel.Frames);
            // An engine stop ends the relay too.
            PeripheralOutputs.SilenceHaptics();
            bench.Pass();
            Assert.Equal(new[] { Half, Half, Stop }, channel.Frames);
        }

        // ── A write that fails ──

        [Fact]
        public void AFailedWrite_ClosesTheChannel_ListsTheUnitAsleep_AndAsksForOneWalk()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            channel.FailSends = true;
            long t0 = bench.Now;
            int changes = bench.SnapshotChanges;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();

            Assert.Equal(1, channel.Attempts);
            Assert.Equal(1, channel.Disposals);
            // Never off the list for a failed write.
            Assert.Equal(new DirectUnit(OutputFamily.TouchSenseUnit, Key, Box, 0x046D, 0xC030) { Name = "Logitech iFeel Mouse", Asleep = true },
                Assert.Single(bench.Units));
            Assert.Equal(changes + 1, bench.SnapshotChanges);
            Assert.Equal(1, bench.WalksAsked);

            // While it sleeps nothing is sent to it and nothing more is asked.
            bench.Pass();
            bench.Advance(1000);
            Assert.Equal(1, channel.Attempts);
            Assert.Equal(1, bench.WalksAsked);
            Assert.Equal(changes + 1, bench.SnapshotChanges);

            // The row opens the mouse again. No notice reaches the worker
            // here, so its own interval is what finds the channel: five
            // seconds after the failure and not a tick before.
            var reopened = bench.Open(Key);
            bench.Advance(TouchSenseBackend.ReopenMs - 1000 - 1);
            Assert.True(Assert.Single(bench.Units).Asleep);
            Assert.Equal(0, reopened.Attempts);
            bench.Advance(1);
            Assert.False(Assert.Single(bench.Units).Asleep);
            Assert.Equal(changes + 2, bench.SnapshotChanges);
            // The level in force is the first frame after.
            Assert.Equal(new[] { Half }, reopened.Frames);
            Assert.Equal(new[] { t0 + TouchSenseBackend.ReopenMs }, reopened.SentAt);
            Assert.Equal(1, bench.WalksAsked);
            Assert.Equal(1, channel.Attempts);
        }

        [Fact]
        public void ASleepingMouse_IsTriedEveryFiveSeconds_AndAtOnceWhenItsChannelOpens()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            channel.FailSends = true;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            Assert.True(Assert.Single(bench.Units).Asleep);

            // A channel notice that is another mouse's finds nothing for
            // this one, and does not put its own interval off.
            bench.Advance(4000);
            bench.Backend.ChannelOpened();
            bench.Pass();
            Assert.True(Assert.Single(bench.Units).Asleep);

            // Nothing open at the first try: the next is five seconds on.
            bench.Advance(1000);
            Assert.True(Assert.Single(bench.Units).Asleep);
            var reopened = bench.Open(Key);
            bench.Advance(TouchSenseBackend.ReopenMs - 1);
            Assert.True(Assert.Single(bench.Units).Asleep);
            Assert.Equal(0, reopened.Attempts);
            bench.Advance(1);
            Assert.False(Assert.Single(bench.Units).Asleep);
            Assert.Equal(new[] { Half }, reopened.Frames);

            // It fails again on the next frame: asleep again, and one more walk asked.
            reopened.FailSends = true;
            bench.Advance(TouchSenseProtocol.ResendMs);
            Assert.True(Assert.Single(bench.Units).Asleep);
            Assert.Equal(1, reopened.Disposals);
            Assert.Equal(2, bench.WalksAsked);

            // The row lists the channel it opens, and the table tells the
            // worker: the mouse plays again with the clock where it was.
            long now = bench.Now;
            var third = bench.Open(Key, announce: true);
            bench.Pass();
            Assert.False(Assert.Single(bench.Units).Asleep);
            Assert.Equal(new[] { Half }, third.Frames);
            Assert.Equal(new[] { now }, third.SentAt);
        }

        /// <summary>A mouse whose row has not opened it yet has no channel
        /// to write on. It sleeps until one opens, and no walk is asked
        /// for: nothing was written, and nothing says the mouse left.</summary>
        [Fact]
        public void AMouseWithNoOpenChannel_SleepsUntilOneOpens_AndAsksForNoWalk()
        {
            using var bench = new Bench();
            bench.Bind(IFeel);
            bench.Containers[Key] = Box;
            var row = Guid.NewGuid();
            Link((row, Key));
            bench.Backend.Start();
            bench.Rescan();
            // Listed awake: no frame was due yet, so no channel was asked for.
            Assert.False(Assert.Single(bench.Units).Asleep);
            Assert.Equal(0, bench.Lookups);

            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            Assert.True(Assert.Single(bench.Units).Asleep);
            Assert.Equal(0, bench.WalksAsked);

            var channel = bench.Open(Key, announce: true);
            bench.Pass();
            Assert.False(Assert.Single(bench.Units).Asleep);
            Assert.Equal(new[] { Half }, channel.Frames);
            Assert.Equal(0, bench.WalksAsked);
        }

        /// <summary>The channel's own rule seen from the worker: once the
        /// control transfer fails and the interrupt pipe takes a frame,
        /// every later frame of that channel goes to the pipe, the last
        /// frames at the stop included.</summary>
        [Fact]
        public void TheWorker_KeepsTheInterruptPipeForTheRestOfTheChannelsLife()
        {
            using var bench = new Bench();
            bench.Bind(IFeel);
            bench.Containers[Key] = Box;
            var usb = new FakeTouchSenseUsb();
            var channel = TouchSenseChannel.Open(Key, usb);
            Assert.NotNull(channel);
            bench.Use(channel);
            var row = Guid.NewGuid();
            Link((row, Key));
            bench.Backend.Start();
            bench.Rescan();

            usb.FailControlOut = true;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            usb.FailControlOut = false;
            bench.Advance(TouchSenseProtocol.ResendMs);
            PeripheralOutputs.SetMotors(row, 0, 0);
            bench.Pass();
            bench.Backend.Stop();

            Assert.Equal(new[]
            {
                "out 21 0A 00 00 00 00 00 00 | ",
                "out 21 09 00 02 00 00 07 00 | " + Half,
            }, usb.Calls("out"));
            Assert.Equal(new[]
            {
                "pipe 01 | " + Half,
                "pipe 01 | " + Half,
                "pipe 01 | " + Stop,
                "pipe 01 | " + Stop,
                "pipe 01 | " + StopAll,
            }, usb.Calls("pipe"));
            // The handle is the row's to close.
            Assert.False(usb.Disposed);
            channel.Dispose();
        }

        // ── The crash path ──

        [Fact]
        public void TheCrashPath_WaitsForTheStopFrame()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();

            // The engine's quiesce zeroes every level, then the panic path
            // gives the worker its wait. The stop is out when it returns.
            PeripheralOutputs.SilenceHaptics();
            bench.Backend.WaitForSilence(30000);
            Assert.Equal(new[] { Half, Stop }, channel.Frames);

            // A silent mouse has nothing to wait for, and neither has a
            // backend whose worker is gone.
            bench.Backend.WaitForSilence(30000);
            bench.Backend.Stop();
            bench.Backend.WaitForSilence(30000);
        }

        /// <summary>The crash path's first step makes the run play nothing
        /// more: the stop goes to a mouse that buzzes, and a level that
        /// still arrives buzzes nothing. Its second step has no handle of
        /// its own to write through, so it sends nothing.</summary>
        [Fact]
        public void TheCrashPathsFirstStep_PlaysNothingMore_WhateverLevelArrives()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();

            bench.Backend.SignalStop();
            bench.Pass();
            Assert.Equal(new[] { Half, Stop }, channel.Frames);
            PeripheralOutputs.SetMotors(row, FullMotor, 0);
            bench.Advance(1000);
            Assert.Equal(2, channel.Frames.Length);

            int attempts = channel.Attempts;
            bench.Backend.ReleaseClaimedNow(Environment.TickCount64 + 1000);
            Assert.Equal(attempts, channel.Attempts);

            // The next run of the engine plays again.
            bench.Backend.Stop();
            bench.Backend.Start();
            bench.Rescan();
            Assert.Equal(new[] { Half, Stop, Stop, StopAll, Full }, channel.Frames);
        }

        // ── Stop ──

        /// <summary>The worker's last frames to a mouse it wrote to are the
        /// stop and then F3, the vendor driver's own last frame. A mouse
        /// it never wrote to gets nothing, and no handle is closed: the
        /// mouse's row closes it afterward.</summary>
        /// <summary>A worker a stop gave up on sends no last frames when it comes back with a
        /// later worker running. Its own last frame asked for 300 ms of pulses, over by then,
        /// and a stop would land between the later worker's frames.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_SendsNoStopOverALaterWorker()
        {
            var (bench, channel, row) = OneMouse(stopJoinMs: 50);
            using var _ = bench;
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            Thread stuck = null;
            int held = 0;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            int sent = channel.Frames.Length;
            Assert.True(sent > 0, "the level went out");
            channel.BeforeSend = () =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0)
                {
                    stuck = Thread.CurrentThread;
                    entered.Set();
                    gate.Wait(30000);
                }
            };
            try
            {
                bench.Advance(TouchSenseProtocol.MinGapMs);
                PeripheralOutputs.SetMotors(row, 39321, 0);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    bench.Backend.Nudge();
                    return entered.IsSet;
                }, 30000), "the worker is inside a frame");
                bench.Backend.Stop();

                using var later = new Bench(predecessorJoinMs: 50, stopJoinMs: 50);
                later.Backend.Start();
                later.Pass();
            }
            finally
            {
                gate.Set();
            }
            Assert.True(stuck.Join(30000), "the first worker ended");
            Assert.DoesNotContain("13 00 00 00 00 00 00", channel.Frames);
            Assert.DoesNotContain("F3 00 00 00 00 00 00", channel.Frames);
        }

        [Fact]
        public void Stopping_SendsTheStopThenTheVendorsLastFrame_ToEveryMouseItWroteTo()
        {
            using var bench = new Bench();
            bench.Bind(IFeel, MouseMan);
            bench.Containers[Key] = Box;
            bench.Containers[OtherKey] = OtherBox;
            var buzzing = bench.Open(Key);
            var untouched = bench.Open(OtherKey);
            Guid row = Guid.NewGuid(), otherRow = Guid.NewGuid();
            Link((row, Key), (otherRow, OtherKey));
            bench.Backend.Start();
            bench.Rescan();
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();

            bench.Backend.Stop();
            Assert.False(bench.Backend.WorkerAlive);
            Assert.Equal(new[] { Half, Stop, StopAll }, buzzing.Frames);
            Assert.Equal(0, untouched.Attempts);
            Assert.Equal(0, buzzing.Disposals);
            Assert.True(buzzing.IsOpen);
            Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);
        }

        /// <summary>The row opened the mouse again after the last frame:
        /// the last frames go on the channel that is open now.</summary>
        [Fact]
        public void Stopping_SendsTheLastFramesOnTheChannelThatIsOpenNow()
        {
            var (bench, first, row) = OneMouse();
            using var _ = bench;
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            first.Dispose();
            var second = bench.Open(Key);
            bench.Backend.Stop();
            Assert.Equal(new[] { Half }, first.Frames);
            Assert.Equal(new[] { Stop, StopAll }, second.Frames);
        }

        [Fact]
        public void AMouseThatIsSilentAtTheStop_StillGetsBothLastFrames_AndOneThatTakesNoStopGetsNoMore()
        {
            var (bench, channel, row) = OneMouse();
            using (bench)
            {
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                bench.Pass();
                PeripheralOutputs.SetMotors(row, 0, 0);
                bench.Pass();
                bench.Backend.Stop();
                Assert.Equal(new[] { Half, Stop, Stop, StopAll }, channel.Frames);
            }

            (bench, channel, row) = OneMouse();
            using (bench)
            {
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                bench.Pass();
                channel.FailSends = true;
                bench.Backend.Stop();
                // The buzz, then the stop it would not take, and no F3 after it.
                Assert.Equal(2, channel.Attempts);
                Assert.Equal(new[] { Half }, channel.Frames);
                // A last frame that fails closes nothing either.
                Assert.Equal(0, channel.Disposals);
            }
        }

        // ── The keep-alive ──

        [Fact]
        public void NoKeepAliveGoesOut_UnlessItsSwitchIsOn()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            for (int second = 0; second < 5; second++) bench.Advance(TouchSenseProtocol.KeepAliveMs);
            Assert.Equal(0, channel.Attempts);

            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            PeripheralOutputs.SetMotors(row, 0, 0);
            bench.Pass();
            for (int second = 0; second < 5; second++) bench.Advance(TouchSenseProtocol.KeepAliveMs);
            bench.Backend.Stop();
            Assert.Equal(new[] { Half, Stop, Stop, StopAll }, channel.Frames);
            Assert.DoesNotContain(channel.Frames, frame => frame.StartsWith("14", StringComparison.Ordinal));
        }

        /// <summary>With the switch on, the vendor's frame goes out after
        /// each full second without a frame of any kind, to a mouse a row
        /// links.</summary>
        [Fact]
        public void WithItsSwitchOn_AKeepAliveGoesOutAfterEachFullSecondWithoutAFrame()
        {
            var (bench, channel, row) = OneMouse(keepAlive: true);
            using var _ = bench;
            long t0 = bench.Now;
            bench.Advance(TouchSenseProtocol.KeepAliveMs - 1);
            Assert.Equal(0, channel.Attempts);
            bench.Advance(1);
            Assert.Equal(new[] { KeepAlive }, channel.Frames);
            bench.Advance(TouchSenseProtocol.KeepAliveMs - 1);
            Assert.Single(channel.Frames);
            bench.Advance(1);
            Assert.Equal(new[] { t0 + 1000, t0 + 2000 }, channel.SentAt);

            // A buzz is a frame: none while it plays, and the second runs
            // from the stop that ends it.
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            bench.Pass();
            for (int i = 0; i < 15; i++) bench.Advance(TouchSenseProtocol.ResendMs);
            PeripheralOutputs.SetMotors(row, 0, 0);
            bench.Pass();
            long stopped = bench.Now;
            Assert.Equal(new[] { KeepAlive, KeepAlive }.Concat(Enumerable.Repeat(Half, 16)).Append(Stop), channel.Frames);
            bench.Advance(TouchSenseProtocol.KeepAliveMs - 1);
            Assert.Equal(19, channel.Frames.Length);
            bench.Advance(1);
            Assert.Equal(KeepAlive, channel.Frames[19]);
            Assert.Equal(stopped + 1000, channel.SentAt[19]);

            // Not to a mouse no row links.
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            bench.Advance(5 * TouchSenseProtocol.KeepAliveMs);
            Assert.Equal(20, channel.Frames.Length);
        }

        // ── Cadence ──

        /// <summary>The worker waits its fast tick while a linked row holds
        /// a level above zero, the level under the motor's cut included,
        /// and its idle tick otherwise. The test reads the wait the worker
        /// chose and times nothing.</summary>
        [Fact]
        public void TheWorker_WaitsItsIdleTick_UntilALinkedRowHoldsALevel()
        {
            const int active = 3, idle = 7;
            var (bench, channel, row) = OneMouse(activeTickMs: active, idleTickMs: idle);
            using var _ = bench;
            Assert.Equal(idle, bench.Cadence());

            PeripheralOutputs.SetMotors(row, 655, 0);
            Assert.Equal(active, bench.Cadence());
            Assert.Equal(0, channel.Attempts);
            PeripheralOutputs.SetMotors(row, HalfMotor, 0);
            Assert.Equal(active, bench.Cadence());
            Assert.Equal(new[] { Half }, channel.Frames);
            PeripheralOutputs.SetMotors(row, 0, 0);
            Assert.Equal(idle, bench.Cadence());

            // A level on a row nothing links is nobody's to play.
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            PeripheralOutputs.SetMotors(row, FullMotor, 0);
            Assert.Equal(idle, bench.Cadence());

            Assert.Equal((10, 100, 5000), (TouchSenseBackend.ActiveTickMs, TouchSenseBackend.IdleTickMs, TouchSenseBackend.ReopenMs));
        }

        // ── Calls after the stop ──

        /// <summary>The host's link pass can outlive the wait Stop makes
        /// for it, so a stopped or a disposed backend takes every call:
        /// it looks for nothing, sends nothing, starts no thread and
        /// throws nothing.</summary>
        [Fact]
        public void AStoppedOrDisposedBackend_TakesEveryCallAndDoesNothing()
        {
            var (bench, channel, row) = OneMouse();
            using var _ = bench;
            bench.Backend.Stop();
            int looks = bench.Looks;

            void EveryCall()
            {
                bench.Backend.Stop();
                bench.Backend.Rescan(null);
                bench.Backend.Rescan(Array.Empty<HidCollectionInfo>());
                bench.Backend.SignalStop();
                bench.Backend.ReleaseClaimedNow(long.MaxValue);
                bench.Backend.WaitForSilence(30000);
                bench.Backend.ChannelOpened();
                bench.Backend.Nudge();
                // The table's notice and a level's reach nobody.
                bench.Table.Publish(new FakeTouchSenseChannel(OtherKey));
                PeripheralOutputs.StopHaptics(row);
                PeripheralOutputs.SetMotors(row, FullMotor, 0);
                Assert.False(bench.Backend.WorkerAlive);
                Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);
                Assert.Equal(OutputFamily.TouchSenseUnit, bench.Backend.Family);
            }
            EveryCall();
            Assert.Equal(looks, bench.Looks);
            Assert.Equal(0, channel.Attempts);

            // A stopped backend starts again, with a run of its own.
            bench.Backend.Start();
            bench.Rescan();
            Assert.Single(bench.Units);
            Assert.Equal(new[] { Full }, channel.Frames);
            // A second Start beside a running worker starts no second one.
            int passes = bench.Backend.PassCount;
            bench.Backend.Start();
            bench.Pass();
            Assert.True(bench.Backend.PassCount > passes);

            bench.Backend.Dispose();
            Assert.Equal(new[] { Full, Stop, StopAll }, channel.Frames);
            looks = bench.Looks;
            bench.Backend.Dispose();
            bench.Backend.Start();
            EveryCall();
            Assert.Equal(looks, bench.Looks);
            Assert.Equal(3, channel.Frames.Length);
        }

        // ── A worker that outlives its Stop ──

        /// <summary>A Stop whose join gives up leaves the worker inside a
        /// transfer. The worker a Start then makes waits for it, so the old
        /// one's last frames, the stop and F3, never land between the new
        /// one's buzz frames.</summary>
        [Fact]
        public void ARestartAfterAStopThatGaveUp_LetsTheOldWorkerFinishFirst()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var (bench, channel, row) = OneMouse(stopJoinMs: 50);
            int sends = 0;
            channel.BeforeSend = () =>
            {
                if (Interlocked.Increment(ref sends) != 1) return;
                held.Set();
                release.Wait(30000);
            };
            try
            {
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                Assert.True(held.Wait(30000), "the worker is held inside its first frame");
                bench.Backend.Stop();
                Assert.Empty(channel.Frames);
                // A stopped backend lists nothing, whatever its worker is still doing.
                Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);

                int passes = bench.Backend.PassCount;
                bench.Backend.Start();
                bench.Backend.Rescan(null);
                // The new worker makes no pass while the old one is held.
                Assert.False(SpinWait.SpinUntil(() => bench.Backend.PassCount > passes, 200));

                release.Set();
                Assert.True(SpinWait.SpinUntil(() => channel.Frames.Length >= 4, 30000), "the new worker played");
                Assert.Equal(new[] { Half, Stop, StopAll, Half }, channel.Frames.Take(4));
            }
            finally
            {
                release.Set();
                bench.Dispose();
            }
        }

        /// <summary>A worker whose stop is set starts no buzz: a pass that a
        /// Stop cut into finishes the frame it is in, starts none for the
        /// mice after it, and goes on to its last frames.</summary>
        [Fact]
        public void AStoppingWorker_StartsNoBuzz()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var bench = new Bench(stopJoinMs: 50);
            bench.Bind(IFeel, MouseMan);
            bench.Containers[Key] = Box;
            bench.Containers[OtherKey] = OtherBox;
            var first = bench.Open(Key);
            var second = bench.Open(OtherKey);
            Guid row = Guid.NewGuid(), otherRow = Guid.NewGuid();
            Link((row, Key), (otherRow, OtherKey));
            Thread worker = null;
            first.BeforeSend = () =>
            {
                if (held.IsSet) return;
                Volatile.Write(ref worker, Thread.CurrentThread);
                held.Set();
                release.Wait(30000);
            };
            try
            {
                // Both levels are up before the worker is, so one pass has both mice to start.
                bench.Backend.Rescan(null);
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                PeripheralOutputs.SetMotors(otherRow, FullMotor, 0);
                bench.Backend.Start();
                Assert.True(held.Wait(30000), "the worker is held inside the first mouse's frame");
                bench.Backend.Stop();
                release.Set();
                Assert.True(Volatile.Read(ref worker).Join(30000), "the worker ended");

                Assert.Equal(new[] { Half, Stop, StopAll }, first.Frames);
                Assert.Equal(0, second.Attempts);
            }
            finally
            {
                release.Set();
                bench.Dispose();
            }
        }

        /// <summary>A predecessor that never comes back holds the next
        /// worker only for its bounded wait: the new one then says so and
        /// goes on.</summary>
        [Fact]
        public void AWedgedPredecessor_HoldsTheNextWorkerOnlyForItsBoundedWait()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var (wedged, channel, row) = OneMouse(stopJoinMs: 50);
            var next = new Bench(predecessorJoinMs: 137);
            channel.BeforeSend = () =>
            {
                held.Set();
                release.Wait(30000);
            };
            const string line = "PERIPHERAL TouchSense previous worker still running after 137 ms, starting anyway";
            try
            {
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                Assert.True(held.Wait(30000), "the first worker is held inside its frame");
                Assert.DoesNotContain(line, SdlDiagLog.Snapshot());

                next.Backend.Start();
                next.Backend.Rescan(null);
                Assert.True(SpinWait.SpinUntil(() => next.Backend.Snapshot.Scanned, 30000), "the new worker went on");
                Assert.True(wedged.Backend.WorkerAlive, "the predecessor was still inside its frame");
                Assert.Contains(line, SdlDiagLog.Snapshot());
            }
            finally
            {
                release.Set();
                next.Dispose();
                wedged.Dispose();
            }
        }

        /// <summary>A worker a Stop gave up on, which also outlasts the next
        /// worker's wait for it, ends after that worker has listed its
        /// mice. It sends its own last frames and leaves the backend's
        /// list alone: the list is the new run's.</summary>
        [Fact]
        public void AWorkerThatEndsAfterTheNextRunStarted_LeavesThatRunsListAlone()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var bench = new Bench(predecessorJoinMs: 50, stopJoinMs: 50);
            bench.Bind(IFeel);
            bench.Containers[Key] = Box;
            var channel = bench.Open(Key);
            var row = Guid.NewGuid();
            Link((row, Key));
            Thread old = null;
            int sends = 0;
            channel.BeforeSend = () =>
            {
                if (Interlocked.Increment(ref sends) != 1) return;
                Volatile.Write(ref old, Thread.CurrentThread);
                held.Set();
                release.Wait(30000);
            };
            try
            {
                // The cue and the level are there before the worker is, so
                // its first pass finds the mouse and is held in the frame
                // with that find still to publish.
                bench.Backend.Rescan(null);
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                bench.Backend.Start();
                Assert.True(held.Wait(30000), "the first worker is held inside its first frame");
                Assert.False(bench.Backend.Snapshot.Scanned);
                bench.Backend.Stop();

                // The level is gone, so the new run only lists the mouse.
                PeripheralOutputs.StopHaptics(row);
                bench.Backend.Start();
                bench.Backend.Rescan(null);
                Assert.True(SpinWait.SpinUntil(() => bench.Backend.Snapshot.Scanned && bench.Units.Count == 1, 30000),
                    "the new worker went on and listed the mouse");
                var listed = bench.Backend.Snapshot;

                release.Set();
                Assert.True(Volatile.Read(ref old).Join(30000), "the first worker ended");
                Assert.Equal(new[] { Half }, channel.Frames);
                Assert.Same(listed, bench.Backend.Snapshot);
            }
            finally
            {
                release.Set();
                bench.Dispose();
            }
        }

        /// <summary>A Dispose whose Stop gave up its join releases the wake
        /// under a worker still inside a transfer. The worker ends quietly
        /// when it comes back, with its last frames sent and no fault
        /// logged.</summary>
        [Fact]
        public void ADisposeThatOutlastsItsJoin_EndsTheWorkerQuietly()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var (bench, channel, row) = OneMouse(stopJoinMs: 50);
            int sends = 0;
            channel.BeforeSend = () =>
            {
                if (Interlocked.Increment(ref sends) != 1) return;
                held.Set();
                release.Wait(30000);
            };
            static int Faults() => SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains("PERIPHERAL TouchSense worker fault"));
            try
            {
                PeripheralOutputs.SetMotors(row, HalfMotor, 0);
                Assert.True(held.Wait(30000), "the worker is held inside its first frame");
                int faults = Faults();
                bench.Backend.Dispose();
                release.Set();
                // A fault would be logged before the last frames go out.
                Assert.True(SpinWait.SpinUntil(() => channel.Frames.Length >= 3, 30000), "the worker sent its last frames");
                Assert.Equal(new[] { Half, Stop, StopAll }, channel.Frames);
                Assert.Equal(faults, Faults());
            }
            finally
            {
                release.Set();
                bench.Dispose();
            }
        }

        /// <summary>The same Dispose under a worker that is past its frames
        /// and held by a listener of its own notice: it comes back to a
        /// wake that is gone, and ends there with no fault logged.</summary>
        [Fact]
        public void AWorkerThatComesBackToAReleasedWake_EndsQuietly()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var bench = new Bench(stopJoinMs: 50);
            bench.Bind(IFeel);
            bench.Containers[Key] = Box;
            Thread worker = null;
            int notices = 0;
            bench.Backend.SnapshotChanged += () =>
            {
                if (Interlocked.Increment(ref notices) != 1) return;
                Volatile.Write(ref worker, Thread.CurrentThread);
                held.Set();
                release.Wait(30000);
            };
            static int Faults() => SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains("PERIPHERAL TouchSense worker fault"));
            try
            {
                bench.Backend.Start();
                bench.Backend.Rescan(null);
                Assert.True(held.Wait(30000), "the worker is held by the listener of its first list");
                int faults = Faults();
                bench.Backend.Dispose();
                release.Set();
                Assert.True(Volatile.Read(ref worker).Join(30000), "the worker ended");
                Assert.Equal(faults, Faults());
            }
            finally
            {
                release.Set();
                bench.Dispose();
            }
        }

        // ── Registration ──

        [Fact]
        public void TheBackend_IsRegisteredForItsFamily_OnTheSharedTable()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "DirectBackends.cs");
            Assert.Contains("Add(backends, OutputFamily.TouchSenseUnit, () => new TouchSenseBackend());", source);
            using var backend = new TouchSenseBackend();
            Assert.Equal(OutputFamily.TouchSenseUnit, backend.Family);
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
        }
    }
}
