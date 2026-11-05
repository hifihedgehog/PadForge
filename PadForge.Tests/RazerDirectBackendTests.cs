using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;
using P = PadForge.Common.Input.Peripherals.RazerDirectProtocol;

namespace PadForge.Tests
{
    /// <summary>
    /// The Razer direct worker against scripted devices (#494): what it
    /// opens and when, every report it sends and in which order, and what
    /// it leaves alone. A device here is a <see cref="FakeReportChannel"/>
    /// with a script behind it. The worker reads a clock the test moves and
    /// waits through a delegate that sleeps for nothing, so a test waits
    /// for worker passes by count and for no length of time.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class RazerDirectBackendTests : IDisposable
    {
        private const int Red = 0xFF0000, Green = 0x00FF00, Blue = 0x0000FF;
        private const ushort DeathAdderV2 = 0x0084, BlackWidowChroma = 0x0203;

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;

        public RazerDirectBackendTests() => PeripheralOutputs.ResetForTest();

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
        }

        // ─────────────────────────────────────────────
        //  The scripted device and its bench
        // ─────────────────────────────────────────────

        /// <summary>A reply that echoes a request's header, with a status
        /// and arguments of its own.</summary>
        internal static byte[] Reply(byte[] request, byte status, params byte[] arguments)
        {
            var reply = new byte[P.BufferLength];
            reply[P.StatusAt] = status;
            reply[P.TransactionAt] = request[P.TransactionAt];
            reply[P.SizeAt] = request[P.SizeAt];
            reply[P.ClassAt] = request[P.ClassAt];
            reply[P.CommandAt] = request[P.CommandAt];
            arguments.CopyTo(reply, P.ArgumentsAt);
            return reply;
        }

        private static bool Is(byte[] request, byte commandClass, byte command)
            => request[P.ClassAt] == commandClass && request[P.CommandAt] == command;

        private static bool IsRead(byte[] request) => (request[P.CommandAt] & 0x80) != 0;

        /// <summary>What a device answers when a test scripts nothing: a
        /// success that echoes the request and its arguments, a brightness
        /// of FF, and "not supported" for the stored effect read, which no
        /// project sends.</summary>
        private static byte[] Answers(byte[] request)
        {
            if (Is(request, 0x0F, 0x82)) return Reply(request, P.StatusNotSupported);
            var reply = Reply(request, P.StatusSuccess, request.Skip(P.ArgumentsAt).Take(P.MaxArguments).ToArray());
            if (Is(request, 0x0F, 0x84) || Is(request, 0x03, 0x83)) reply[P.ArgumentsAt + 2] = 0xFF;
            if (Is(request, 0x0E, 0x84)) reply[P.ArgumentsAt + 1] = 0xFF;
            return reply;
        }

        /// <summary>A reply that is nobody's: status 00 and no echo, what a
        /// device shows for a request it never read.</summary>
        private static byte[] Silence(byte[] request) => new byte[P.BufferLength];

        private sealed class FakeRazer
        {
            private readonly Bench _bench;
            public readonly ushort ProductId;
            public readonly RazerDirectRow Table;
            public readonly Guid Container = Guid.NewGuid();

            /// <summary>The device row linked to it.</summary>
            public readonly Guid Row = Guid.NewGuid();
            public readonly List<HidCollectionInfo> Collections = new();
            public readonly ConcurrentQueue<FakeReportChannel> Channels = new();

            /// <summary>The reply to a request, or null for a read that
            /// fails at the handle.</summary>
            public volatile Func<byte[], byte[]> Answer = Answers;

            /// <summary>The reply by the collection the request came in
            /// on, where a test gives a device's collections answers of
            /// their own. It goes before <see cref="Answer"/>.</summary>
            public volatile Func<string, byte[], byte[]> AnswerAt;
            public volatile bool Present = true;
            public volatile bool ContainerKnown = true;
            public volatile bool OpenFails;
            private volatile bool _failWrites;

            public FakeRazer(Bench bench, ushort productId)
            {
                _bench = bench;
                ProductId = productId;
                Table = RazerDirectTable.Find(productId);
            }

            /// <summary>Every call on every handle fails, as on a device
            /// that left.</summary>
            public bool FailWrites
            {
                get => _failWrites;
                set
                {
                    _failWrites = value;
                    foreach (var channel in Channels) channel.FailWrites = value;
                }
            }

            public string Key => Collections[0].Path;
            public OutputPath Path => new(OutputFamily.RazerUnit, Key);
            public int Opens => Channels.Count;
            public bool Open => Channels.Any(c => !c.Disposed);

            public HidCollectionInfo Add(int mi, int col, ushort page, ushort usage, ushort feature = 91, ushort vendorId = 0x1532)
            {
                var info = new HidCollectionInfo
                {
                    Path = $@"\\?\hid#vid_{vendorId:x4}&pid_{ProductId:x4}&mi_{mi:x2}" + (col > 0 ? $"&col{col:x2}" : "")
                        + $"#{Container:N}&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                    VendorId = vendorId, ProductId = ProductId, UsagePage = page, Usage = usage, InterfaceNumber = mi,
                    FeatureReportLength = feature,
                };
                Collections.Add(info);
                return info;
            }

            public IHidReportChannel OpenChannel(string path)
            {
                if (OpenFails || !Collections.Any(c => c.Path == path)) return null;
                FakeReportChannel channel = null;
                channel = new FakeReportChannel
                {
                    Path = path,
                    FeatureOnly = true,
                    Ticks = () => _bench.Stamp(),
                    Feature = _ =>
                    {
                        var last = channel.Calls.LastOrDefault(c => c.Kind == "set").Bytes;
                        var at = AnswerAt;
                        return last == null ? new byte[P.BufferLength] : at != null ? at(path, last) : Answer(last);
                    },
                };
                // Listed first, so a handle opened while the flag is being
                // set is either seen by the setter or reads what it set.
                Channels.Enqueue(channel);
                channel.FailWrites = _failWrites;
                return channel;
            }

            /// <summary>Every call on every handle, in the order made.</summary>
            public List<(string Kind, byte[] Bytes, long Tick, string Path)> Calls
                => Channels.SelectMany(c => c.Calls.Select(call => (call.Kind, call.Bytes, call.Tick, c.Path)))
                    .OrderBy(c => c.Tick).ToList();

            /// <summary>The requests sent after a mark, as lines.</summary>
            public string[] Sent(long after = 0)
                => Calls.Where(c => c.Kind == "set" && c.Tick > after).Select(c => RazerDirectProtocolTests.Line(c.Bytes)).ToArray();

            public string[] Sent(long after, Func<byte[], bool> which)
                => Calls.Where(c => c.Kind == "set" && c.Tick > after && which(c.Bytes))
                    .Select(c => RazerDirectProtocolTests.Line(c.Bytes)).ToArray();
        }

        private sealed class FakeGuard : IRazerGuard
        {
            private readonly Bench _bench;
            public volatile bool Busy;
            public readonly ConcurrentQueue<(string What, int WaitMs, long Tick, int Thread)> Journal = new();
            public FakeGuard(Bench bench) => _bench = bench;

            public bool Take(int waitMs)
            {
                bool free = !Busy;
                Journal.Enqueue((free ? "take" : "busy", waitMs, _bench.Stamp(), Environment.CurrentManagedThreadId));
                return free;
            }

            public void Release() => Journal.Enqueue(("release", 0, _bench.Stamp(), Environment.CurrentManagedThreadId));
        }

        /// <summary>One worker on scripted devices, a scripted lock and a
        /// clock the test moves.</summary>
        private sealed class Bench : IDisposable
        {
            private long _now = 1_000_000;
            private long _stamp;

            /// <summary>A queue, since a test may plug a device in while
            /// the worker is reading the list.</summary>
            public readonly ConcurrentQueue<FakeRazer> Devices = new();
            public readonly FakeGuard Guard;
            public readonly ConcurrentQueue<int> Waits = new();
            public readonly ConcurrentQueue<(string Path, byte[] Report, int TimeoutMs)> OneShots = new();
            public readonly ConcurrentDictionary<int, ISet<Guid>> OnSlot = new();
            public readonly ConcurrentDictionary<int, int> Shown = new();

            /// <summary>The crash path's record as it stood at each call
            /// on a handle and each take of the lock, by stamp.</summary>
            public readonly ConcurrentDictionary<long, IReadOnlyList<(string Path, byte[][] HandBack)>> Records = new();
            public Action<string, byte[], int> OnOneShot;

            /// <summary>The one-shot writer reports a write that did not
            /// go out.</summary>
            public volatile bool OneShotFails;
            public RazerDirectBackend Backend;
            public int Walks;
            public int Changes;

            /// <summary>The worker threads seen so far, in the order they
            /// first read the clock, so a test can wait for one to end.</summary>
            public readonly ConcurrentQueue<Thread> Workers = new();
            private readonly IRazerGuard _lock;

            /// <summary>A bench on the scripted lock, or on
            /// <paramref name="guard"/> where a test brings a lock of its
            /// own.</summary>
            public Bench(int idleTickMs = 2, int reassertMs = RazerDirectBackend.ReassertMs,
                int predecessorJoinMs = RazerDirectBackend.PredecessorJoinMs, int stopJoinMs = RazerDirectBackend.StopJoinMs,
                IRazerGuard guard = null)
            {
                Guard = new FakeGuard(this);
                _lock = guard ?? Guard;
                Backend = Make(idleTickMs, reassertMs, predecessorJoinMs, stopJoinMs);
            }

            private long Tick()
            {
                var thread = Thread.CurrentThread;
                if (thread.Name == "PeripheralRazer" && !Workers.Contains(thread)) Workers.Enqueue(thread);
                return Now;
            }

            /// <summary>Another worker on the same devices, lock and clock.</summary>
            public RazerDirectBackend Make(int idleTickMs = 2, int reassertMs = RazerDirectBackend.ReassertMs,
                int predecessorJoinMs = RazerDirectBackend.PredecessorJoinMs, int stopJoinMs = RazerDirectBackend.StopJoinMs)
            {
                var backend = new RazerDirectBackend(
                    open: path => Devices.Select(d => d.OpenChannel(path)).FirstOrDefault(c => c != null),
                    containerOf: path =>
                    {
                        var device = Devices.FirstOrDefault(d => d.Collections.Any(c => c.Path == path));
                        return device != null && device.ContainerKnown ? device.Container : Guid.Empty;
                    },
                    ticks: Tick,
                    wait: us => Waits.Enqueue(us),
                    guard: _lock,
                    setFeatureOnce: (path, report, timeoutMs) =>
                    {
                        OneShots.Enqueue((path, (byte[])report.Clone(), timeoutMs));
                        OnOneShot?.Invoke(path, report, timeoutMs);
                        return !OneShotFails;
                    },
                    devicesOnSlot: slot => OnSlot.TryGetValue(slot, out var devices) ? devices : new HashSet<Guid>(),
                    slotNumber: slot => Shown.TryGetValue(slot, out int number) ? number : slot + 1,
                    idleTickMs: idleTickMs, reassertMs: reassertMs, predecessorJoinMs: predecessorJoinMs,
                    stopJoinMs: stopJoinMs);
                backend.RescanRequested += () => Interlocked.Increment(ref Walks);
                backend.SnapshotChanged += () => Interlocked.Increment(ref Changes);
                return backend;
            }

            public long Now => Interlocked.Read(ref _now);
            public void Advance(long ms) => Interlocked.Add(ref _now, ms);

            /// <summary>The next stamp in the one order every handle and
            /// the lock share, with the crash path's record as it stands.</summary>
            public long Stamp()
            {
                long stamp = Interlocked.Increment(ref _stamp);
                var backend = Backend;
                if (backend != null) Records[stamp] = backend.Claimed;
                return stamp;
            }

            /// <summary>The newest stamp, to tell what came after it.</summary>
            public long Mark => Interlocked.Read(ref _stamp);

            public FakeRazer Add(ushort productId, params (int Mi, int Col, ushort Page, ushort Usage)[] collections)
            {
                var device = new FakeRazer(this, productId);
                foreach (var (mi, col, page, usage) in collections) device.Add(mi, col, page, usage);
                Devices.Enqueue(device);
                return device;
            }

            /// <summary>A device on the one collection its table row names.</summary>
            public FakeRazer Add(ushort productId)
            {
                var named = RazerDirectTable.Find(productId).Collections[0];
                return Add(productId, (named.Interface, 0, named.UsagePage, named.Usage));
            }

            /// <summary>Hands the worker the collections present now, as the
            /// host's walk does.</summary>
            public void Walk(RazerDirectBackend backend = null)
                => (backend ?? Backend).Rescan(Devices.Where(d => d.Present).SelectMany(d => d.Collections).ToList());

            public void StartAndWalk()
            {
                Backend.Start();
                Walk();
                Passes();
            }

            /// <summary>Waits until one whole pass has run after the
            /// caller's last change. The worker counts a pass as it starts,
            /// so the first new count is a pass that began after the
            /// change, and the second says that pass has ended.</summary>
            public void Passes(int count = 2, RazerDirectBackend backend = null)
            {
                backend ??= Backend;
                for (int i = 0; i < count; i++)
                {
                    int before = backend.PassCount;
                    Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
                }
            }

            /// <summary>The link table as the host's pass would publish it:
            /// each device's row holds the device's unit for lighting.</summary>
            public void Link(params FakeRazer[] devices)
                => PeripheralOutputs.PublishLinks(new LinkTable(devices.Select(d => new DeviceLinks
                {
                    Device = d.Row, Lighting = new[] { d.Path }, HasVendorProgram = true,
                    Shadows = new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") },
                })));

            public void Light(FakeRazer device, int rgb, int slot = 0, int player = 1)
                => PeripheralOutputs.SetLighting(device.Row, slot, player, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

            public DirectUnit Unit(FakeRazer device)
                => Backend.Snapshot.Units.SingleOrDefault(u => u.Key == device.Key);

            /// <summary>What holds for every device whatever the test did:
            /// no report of class 00, where the device mode lives, but a
            /// receiver's read of its paired devices (00/BF) and a keyboard's
            /// read of its layout (00/86), and no request
            /// sent while the reply to the one before it on that handle was
            /// still unread.</summary>
            public void Verify(bool everyReplyRead = true)
            {
                foreach (var device in Devices)
                {
                    foreach (var call in device.Calls.Where(c => c.Kind == "set"))
                    {
                        Assert.True(call.Bytes[P.ClassAt] != 0x00 || call.Bytes[P.CommandAt] is 0xBF or 0x86);
                        Assert.Equal(P.BufferLength, call.Bytes.Length);
                        Assert.Equal(P.Crc(call.Bytes), call.Bytes[P.CrcAt]);
                    }
                    if (!everyReplyRead) continue;
                    foreach (var channel in device.Channels)
                    {
                        string before = null;
                        foreach (var call in channel.Calls)
                        {
                            Assert.False(call.Kind == "set" && before == "set", "a request went out before the last reply was read");
                            before = call.Kind;
                        }
                    }
                }
            }

            public void Dispose()
            {
                foreach (var device in Devices) device.Answer = Answers;
                Backend.Dispose();
            }
        }

        private static string[] Lines(params byte[][] reports) => reports.Select(RazerDirectProtocolTests.Line).ToArray();

        /// <summary>Holds the worker inside the read of the device's next
        /// reply, on a paint this starts, until the gate opens.</summary>
        private static void HoldTheWorkerInARead(Bench bench, FakeRazer device, ManualResetEventSlim gate)
        {
            int held = 0;
            device.Answer = request =>
            {
                Volatile.Write(ref held, 1);
                gate.Wait(30000);
                return Answers(request);
            };
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(device, Blue);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the worker is held in a read");
        }

        // ─────────────────────────────────────────────
        //  The scan
        // ─────────────────────────────────────────────

        /// <summary>A unit is a listed product with a color path on the
        /// collection whose feature report is 91 bytes. Where a device shows
        /// two of them it is one unit, keyed by the one on the interface
        /// the table names. Nothing is opened and nothing is sent to find
        /// it, and a walk that finds the same leaves the snapshot in place.</summary>
        [Fact]
        public void AUnitIsAListedProductsNinetyOneByteCollection_AndNothingIsSentToFindIt()
        {
            using var bench = new Bench();
            // A DeathAdder V2: the mouse collection carries the report, and
            // its keyboard collection has no feature report at all.
            var mouse = bench.Add(DeathAdderV2, (0, 0, 0x01, 0x02));
            mouse.Add(1, 1, 0x01, 0x06, feature: 0);
            // A BlackWidow Chroma that shows the report on two interfaces.
            // The table names interface 2.
            var keyboard = bench.Add(BlackWidowChroma, (2, 1, 0x01, 0x02), (3, 0, 0x0C, 0x01));
            keyboard.Collections.Reverse();
            // A BlackWidow V4 Tenkeyless HyperSpeed on its cable, which
            // shows it on two interfaces too. The table names interface 3,
            // the one whose path sorts last.
            var board = bench.Add(0x02D7, (2, 1, 0x01, 0x02), (3, 0, 0x0C, 0x01));
            // A listed product with no color path, a product no row lists,
            // another vendor's device whose feature report is 91 bytes, and
            // a listed product on a collection of another length.
            bench.Add(0x00B8, (0, 0, 0x01, 0x02));
            bench.Add(0x7777, (0, 0, 0x01, 0x02));
            var other = bench.Add(DeathAdderV2);
            other.Collections.Clear();
            other.Add(0, 0, 0x01, 0x02, vendorId: 0x046D);
            var bare = bench.Add(0x0043);
            bare.Collections.Clear();
            bare.Add(0, 0, 0x01, 0x02, feature: 90);

            Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);
            Assert.Equal(OutputFamily.RazerUnit, bench.Backend.Family);
            bench.Backend.Start();
            bench.Passes();
            // Started, and no list handed over yet.
            Assert.False(bench.Backend.Snapshot.Scanned);

            bench.Walk();
            bench.Passes();
            var snapshot = bench.Backend.Snapshot;
            Assert.True(snapshot.Scanned);
            string keyboardKey = keyboard.Collections.Single(c => c.InterfaceNumber == 2).Path;
            string boardKey = board.Collections.Single(c => c.InterfaceNumber == 3).Path;
            Assert.Equal(new[] { mouse.Key, keyboardKey, boardKey }.OrderBy(k => k, StringComparer.Ordinal), snapshot.Units.Select(u => u.Key));
            var unit = snapshot.Units.Single(u => u.Key == mouse.Key);
            // Named by the product table, the name its row takes when no
            // mouse row reaches it (#557).
            Assert.Equal(new DirectUnit(OutputFamily.RazerUnit, mouse.Key, mouse.Container, 0x1532, DeathAdderV2)
            {
                Name = "Razer DeathAdder V2",
            }, unit);
            Assert.False(unit.Asleep);
            Assert.Equal(0, unit.MaxClaimLevel);
            Assert.Equal(keyboard.Container, snapshot.Units.Single(u => u.Key == keyboardKey).ContainerId);
            Assert.Equal(1, Volatile.Read(ref bench.Changes));
            Assert.All(bench.Devices, d => Assert.Equal(0, d.Opens));

            // The same collections again: the same snapshot.
            bench.Walk();
            bench.Passes();
            Assert.Same(snapshot, bench.Backend.Snapshot);
            Assert.Equal(1, Volatile.Read(ref bench.Changes));

            // A unit leaves the list when its collection leaves the walk.
            mouse.Present = false;
            bench.Walk();
            bench.Passes();
            Assert.Equal(new[] { keyboardKey, boardKey }.OrderBy(k => k, StringComparer.Ordinal),
                bench.Backend.Snapshot.Units.Select(u => u.Key));
            Assert.Equal(2, Volatile.Read(ref bench.Changes));
            Assert.Equal(0, Volatile.Read(ref bench.Walks));
            Assert.All(bench.Devices, d => Assert.Equal(0, d.Opens));
        }

        /// <summary>A unit whose container is not known yet matches no row,
        /// so it is not listed until the lookup answers. The lookup is made
        /// again on the worker's own interval, from the list in hand, and
        /// the scan still counts as run meanwhile.</summary>
        [Fact]
        public void AUnitWhoseContainerIsNotKnownYet_IsListedOnceTheLookupAnswers()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            mouse.ContainerKnown = false;
            bench.StartAndWalk();
            Assert.True(bench.Backend.Snapshot.Scanned);
            Assert.Empty(bench.Backend.Snapshot.Units);

            mouse.ContainerKnown = true;
            bench.Advance(RazerDirectBackend.RetryMs - 1);
            bench.Passes();
            Assert.Empty(bench.Backend.Snapshot.Units);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(mouse.Container, bench.Unit(mouse).ContainerId);
            Assert.Equal(0, mouse.Opens);
        }

        // ─────────────────────────────────────────────
        //  Claim, paint, hand back
        // ─────────────────────────────────────────────

        /// <summary>A unit is opened only once a row links its path and a
        /// color resolves on it. The claim read comes first, then the reads
        /// of what the device shows, then the color, one report per LED,
        /// each followed by a read of its reply. A new color paints again,
        /// and when the claim ends the hand-back goes out and the handle is
        /// closed.</summary>
        [Fact]
        public void AUnitIsClaimedPaintedAndHandedBack_InThatOrder()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();

            // A color with no row linked to the unit, then a row with no
            // color: nothing is opened for either.
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(0, mouse.Opens);
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Link(mouse);
            bench.Passes();
            Assert.Equal(0, mouse.Opens);
            Assert.Empty(bench.Guard.Journal);

            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(1, mouse.Opens);
            Assert.Equal(Lines(
                P.ExtendedBrightnessRead(0x3F, 0x04),
                P.RegionListRead(0x3F),
                P.StoredEffectRead(0x3F, 0x04),
                P.StoredEffectRead(0x3F, 0x01),
                P.ExtendedStatic(0x3F, 0x04, Red),
                P.ExtendedStatic(0x3F, 0x01, Red)), mouse.Sent());
            Assert.Equal("3F 0F/02 size 09 [00 04 01 00 00 01 FF 00 00]", mouse.Sent()[4]);
            // Each request is followed by one read of its reply.
            Assert.Equal(Enumerable.Repeat(new[] { "set", "get" }, 6).SelectMany(p => p), mouse.Calls.Select(c => c.Kind));
            // The wired wait, once per request, and nothing slept for.
            Assert.Equal(Enumerable.Repeat(600, 6), bench.Waits);
            var record = Assert.Single(bench.Backend.Claimed);
            Assert.Equal(mouse.Key, record.Path);
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), Lines(record.HandBack));

            // The same color again changes nothing, and neither does time
            // short of the re-assert.
            long mark = bench.Mark;
            bench.Light(mouse, Red);
            bench.Advance(1000);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));

            bench.Light(mouse, Blue);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Blue), P.ExtendedStatic(0x3F, 0x01, Blue)), mouse.Sent(mark));

            // The claim ends with its path still linked: the device gets
            // its own lighting back, and the handle is closed.
            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(1, mouse.Opens);
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Equal(0, Volatile.Read(ref bench.Walks));
            bench.Verify();
        }

        // ─────────────────────────────────────────────
        //  Light by light (#557)
        // ─────────────────────────────────────────────

        private const ushort Lancehead = 0x0059, MouseDockPro = 0x00A4, BasiliskV3ProWireless = 0x00AB;

        private static void LightKeys(FakeRazer device, int rgb, KeyFrame keys)
            => PeripheralOutputs.SetLighting(device.Row, 0, 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, keys: keys);

        private static KeyFrame Logo(int rgb)
            => new(null, new Dictionary<string, int> { [LightGroups.Logo] = rgb }, null, null);

        /// <summary>A Lancehead's frame of 16 with its logo, cell 14 in
        /// OpenRGB's order (RazerDevices.cpp:6193-6244), in one color and
        /// every other cell in another.</summary>
        private static int[] LanceheadFrame(int rest, int logo)
        {
            var cells = Enumerable.Repeat(rest, 16).ToArray();
            cells[14] = logo;
            return cells;
        }

        /// <summary>A product with more than one light has its map published
        /// under its unit's path once the unit is listed, and taken down when
        /// the unit leaves and when the worker stops. A product with one
        /// light has none. Nothing is sent to publish it.</summary>
        [Fact]
        public void AUnitsLightMap_IsPublishedWhileItIsListed()
        {
            PeripheralLightMaps.ResetForTest();
            try
            {
                using var bench = new Bench();
                var lancehead = bench.Add(Lancehead);
                var viper = bench.Add(0x0078);
                bench.StartAndWalk();
                var map = PeripheralLightMaps.For(lancehead.Path);
                Assert.NotNull(map);
                Assert.Equal("razer:1532:0059", map.Product);
                Assert.Equal(16, map.Lights.Count);
                Assert.Equal(LightKind.Logo, map.Lights[14].Kind);
                Assert.Null(PeripheralLightMaps.For(viper.Path));
                Assert.All(bench.Devices, d => Assert.Equal(0, d.Opens));

                lancehead.Present = false;
                bench.Walk();
                bench.Passes();
                Assert.Null(PeripheralLightMaps.For(lancehead.Path));

                lancehead.Present = true;
                bench.Walk();
                bench.Passes();
                Assert.NotNull(PeripheralLightMaps.For(lancehead.Path));
                bench.Backend.Stop();
                Assert.Null(PeripheralLightMaps.For(lancehead.Path));
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>
        /// A claim with key colors paints the product light by light: a
        /// Lancehead, painted one color one report per LED, takes the
        /// extended matrix frame and the custom frame effect, its logo in
        /// the logo's color and every other cell in the claim's color. The
        /// same picture again sends nothing, a new one paints again, and a
        /// claim with no key colors paints the one color as before.
        /// </summary>
        [Fact]
        public void KeyColors_PaintTheFrame_AndNoneLeaveTheOneColor()
        {
            PeripheralLightMaps.ResetForTest();
            try
            {
                using var bench = new Bench();
                var mouse = bench.Add(Lancehead);
                bench.StartAndWalk();
                bench.Link(mouse);
                bench.Light(mouse, Red);
                bench.Passes();
                // The claim of #494, unchanged: the reads, then a static
                // effect per LED.
                Assert.Equal(Lines(
                    P.ExtendedBrightnessRead(0x3F, 0x04),
                    P.RegionListRead(0x3F),
                    P.StoredEffectRead(0x3F, 0x04), P.StoredEffectRead(0x3F, 0x01),
                    P.StoredEffectRead(0x3F, 0x11), P.StoredEffectRead(0x3F, 0x10),
                    P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red),
                    P.ExtendedStatic(0x3F, 0x11, Red), P.ExtendedStatic(0x3F, 0x10, Red)), mouse.Sent());

                long mark = bench.Mark;
                bench.Advance(RazerDirectBackend.PaintMs);
                LightKeys(mouse, Red, Logo(Green));
                bench.Passes();
                Assert.Equal(Lines(P.ExtendedMatrixRow(0x3F, 0, LanceheadFrame(Red, Green)), P.ExtendedCustom(0x3F)),
                    mouse.Sent(mark));

                // The same picture: nothing.
                mark = bench.Mark;
                bench.Advance(RazerDirectBackend.PaintMs);
                LightKeys(mouse, Red, Logo(Green));
                bench.Passes();
                Assert.Empty(mouse.Sent(mark));

                // Another background under the same key colors.
                bench.Advance(RazerDirectBackend.PaintMs);
                LightKeys(mouse, Blue, Logo(Green));
                bench.Passes();
                Assert.Equal(Lines(P.ExtendedMatrixRow(0x3F, 0, LanceheadFrame(Blue, Green)), P.ExtendedCustom(0x3F)),
                    mouse.Sent(mark));

                // No key colors: the one color, one report per LED.
                mark = bench.Mark;
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(mouse, Blue);
                bench.Passes();
                Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Blue), P.ExtendedStatic(0x3F, 0x01, Blue),
                    P.ExtendedStatic(0x3F, 0x11, Blue), P.ExtendedStatic(0x3F, 0x10, Blue)), mouse.Sent(mark));

                // A frame that sets its own background paints every other
                // cell in it, not in the claim's color
                // (LightRenderer.Fill).
                mark = bench.Mark;
                bench.Advance(RazerDirectBackend.PaintMs);
                LightKeys(mouse, Red, new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Logo] = Green }, null, Blue));
                bench.Passes();
                Assert.Equal(Lines(P.ExtendedMatrixRow(0x3F, 0, LanceheadFrame(Blue, Green)), P.ExtendedCustom(0x3F)),
                    mouse.Sent(mark));

                // The re-assert repaints the picture that is wanted.
                bench.Advance(RazerDirectBackend.PaintMs);
                LightKeys(mouse, Red, Logo(Green));
                bench.Passes();
                mark = bench.Mark;
                bench.Advance(RazerDirectBackend.ReassertMs);
                bench.Passes();
                Assert.Equal(Lines(P.ExtendedMatrixRow(0x3F, 0, LanceheadFrame(Red, Green)), P.ExtendedCustom(0x3F)),
                    mouse.Sent(mark));
                bench.Verify();
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>A device that refuses its first paint light by light
        /// before it took any of it is painted one color in the same paint,
        /// by its own family, and stays one color while its claim lasts:
        /// the refusal does not count against the family.</summary>
        [Fact]
        public void ADeviceThatRefusesItsFrame_IsPaintedOneColor()
        {
            PeripheralLightMaps.ResetForTest();
            try
            {
                using var bench = new Bench();
                var mouse = bench.Add(Lancehead);
                mouse.Answer = request => Is(request, 0x0F, 0x03) ? Reply(request, P.StatusNotSupported) : Answers(request);
                bench.StartAndWalk();
                bench.Link(mouse);
                LightKeys(mouse, Red, Logo(Green));
                bench.Passes();
                var sent = mouse.Sent();
                Assert.Equal(Lines(P.ExtendedMatrixRow(0x3F, 0, LanceheadFrame(Red, Green)),
                    P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red),
                    P.ExtendedStatic(0x3F, 0x11, Red), P.ExtendedStatic(0x3F, 0x10, Red)), sent.Skip(6));
                Assert.False(bench.Unit(mouse).Asleep);

                long mark = bench.Mark;
                bench.Advance(RazerDirectBackend.PaintMs);
                LightKeys(mouse, Red, Logo(Blue));
                bench.Passes();
                Assert.Empty(mouse.Sent(mark));
                bench.Advance(RazerDirectBackend.PaintMs);
                LightKeys(mouse, Green, Logo(Blue));
                bench.Passes();
                Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Green), P.ExtendedStatic(0x3F, 0x01, Green),
                    P.ExtendedStatic(0x3F, 0x11, Green), P.ExtendedStatic(0x3F, 0x10, Green)), mouse.Sent(mark));
                bench.Verify();
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>
        /// A Mouse Dock Pro's claim read is its list of paired devices
        /// (OpenRGB RazerController.cpp:1273-1306). A paired product the
        /// table paints gets a unit of its own, listed under the dock's
        /// product ID and container ahead of the dock, and reached through
        /// the dock's collection on a handle of its own under its own ID.
        /// Each of its replies is read every 2 ms until it is past busy
        /// (2098-2124), and it is painted light by light like any unit.
        /// The two share the dock's collection on the crash path's record,
        /// and leave with it.
        /// </summary>
        [Fact]
        public void ADockProsPairedMouse_IsAUnitOfItsOwn_ReachedThroughTheDock()
        {
            PeripheralLightMaps.ResetForTest();
            try
            {
                using var bench = new Bench(reassertMs: int.MaxValue);
                var dock = bench.Add(MouseDockPro);
                byte[] polled = null;
                int busy = 0;
                dock.Answer = request =>
                {
                    if (Is(request, 0x00, 0xBF))
                        return Reply(request, P.StatusSuccess, 2, 0x01, 0x00, 0xAB, 0x01, 0x00, 0xA4);
                    if (request[P.TransactionAt] != 0x1F) return Answers(request);
                    // The receiver relays: three reads of busy, then the answer.
                    if (!ReferenceEquals(request, polled))
                    {
                        polled = request;
                        busy = 0;
                    }
                    return ++busy <= 3 ? Reply(request, P.StatusBusy) : Answers(request);
                };
                bench.StartAndWalk();
                bench.Link(dock);
                bench.Light(dock, Red);
                bench.Passes();
                var red9 = Enumerable.Repeat(Red, 9).ToArray();
                Assert.Equal(Lines(P.PairedDevicesRead(0xFF), P.StoredEffectRead(0xFF, 0x00),
                    P.ExtendedMatrixRow(0xFF, 0, red9), P.ExtendedCustom(0xFF)), dock.Sent());

                // The mouse behind it, listed ahead of the dock.
                string pairedKey = RazerDirectBackend.PairedKey(dock.Key, BasiliskV3ProWireless);
                bench.Passes();
                var units = bench.Backend.Snapshot.Units;
                Assert.Equal(new[] { pairedKey, dock.Key }, units.Select(u => u.Key));
                // The paired mouse takes its own name from the table, the
                // dock its own.
                Assert.Equal(new DirectUnit(OutputFamily.RazerUnit, pairedKey, dock.Container, 0x1532, MouseDockPro)
                {
                    Name = "Razer Basilisk V3 Pro (Wireless)",
                }, units[0]);
                Assert.Equal("Razer Mouse Dock Pro", units[1].Name);
                var pairedPath = new OutputPath(OutputFamily.RazerUnit, pairedKey);
                Assert.Equal("razer:1532:00AB", PeripheralLightMaps.For(pairedPath).Product);

                // The dock's row lights both.
                long mark = bench.Mark;
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks { Device = dock.Row, Lighting = new[] { pairedPath, dock.Path }, HasVendorProgram = true },
                }));
                LightKeys(dock, Red, Logo(Green));
                for (int step = 0; step < 400 && dock.Sent(mark, r => r[P.TransactionAt] == 0x1F).Length < 5; step++)
                {
                    bench.Advance(2);
                    bench.Passes(1);
                }
                // The last report's reads, 2 ms apart.
                for (int step = 0; step < 8; step++)
                {
                    bench.Advance(2);
                    bench.Passes(1);
                }
                bench.Passes();
                var cells = Enumerable.Repeat(Red, 13).ToArray();
                cells[1] = Green;
                Assert.Equal(Lines(P.ExtendedBrightnessRead(0x1F, 0x00), P.RegionListRead(0x1F), P.StoredEffectRead(0x1F, 0x00),
                    P.ExtendedMatrixRow(0x1F, 0, cells), P.ExtendedCustom(0x1F)),
                    dock.Sent(mark, r => r[P.TransactionAt] == 0x1F));
                Assert.Equal(2, dock.Opens);
                // On the mouse's own handle, every request is followed by
                // four reads, three of them busy, each 2 ms after the last.
                var relay = dock.Channels.ToArray()[1];
                Assert.Equal(Enumerable.Repeat(new[] { "set", "get", "get", "get", "get" }, 5).SelectMany(k => k),
                    relay.Calls.Select(c => c.Kind));
                Assert.All(relay.Calls, c => Assert.Equal(dock.Key, relay.Path));
                // The dock repainted its own lights meanwhile, and no
                // request of either went out while the other's reply was
                // still unread: one request at a time on the receiver.
                Assert.NotEmpty(dock.Sent(mark, r => r[P.TransactionAt] == 0xFF));
                var timeline = dock.Channels.SelectMany((c, i) => c.Calls.Select(call => (Channel: i, call.Kind, call.Tick)))
                    .OrderBy(e => e.Tick).ToList();
                for (int i = 0; i < timeline.Count; i++)
                {
                    var e = timeline[i];
                    if (e.Kind != "set") continue;
                    // A request is open until its handle's last read before
                    // that handle's next request.
                    int last = i;
                    for (int j = i + 1; j < timeline.Count && !(timeline[j].Channel == e.Channel && timeline[j].Kind == "set"); j++)
                        if (timeline[j].Channel == e.Channel) last = j;
                    for (int j = i + 1; j <= last; j++)
                        Assert.False(timeline[j].Kind == "set" && timeline[j].Channel != e.Channel,
                            $"a request on handle {timeline[j].Channel} went out while handle {e.Channel} waited");
                }
                // The crash path's record holds both on the one collection.
                Assert.Equal(new[] { dock.Key, dock.Key }, bench.Backend.Claimed.Select(e => e.Path));

                // The dock leaves, and the mouse with it.
                dock.Present = false;
                bench.Walk();
                bench.Passes();
                Assert.Empty(bench.Backend.Snapshot.Units);
                Assert.Null(PeripheralLightMaps.For(pairedPath));
                Assert.Empty(bench.Backend.Claimed);
                bench.Verify();
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>
        /// A keyboard laid out by OpenRGB's KeyboardLayoutManager is asked its
        /// layout once, right after its claim read, and its map follows the
        /// region it names before its first paint: a BlackWidow Chroma that
        /// answers 06, a UK board, is laid out ISO, and a Q colored by its
        /// usage lights cell 46 of the frame, with the frame's background on
        /// every other cell, those no key holds included. It is not asked
        /// again on its next claim.
        /// </summary>
        [Fact]
        public void AKeyboard_SaysItsRegion_AndItsMapFollowsIt()
        {
            PeripheralLightMaps.ResetForTest();
            try
            {
                using var bench = new Bench();
                var board = bench.Add(BlackWidowChroma);
                board.Answer = request => Is(request, 0x00, 0x86) ? Reply(request, P.StatusSuccess, 0x06, 0x01) : Answers(request);
                bench.StartAndWalk();
                Assert.Equal(110, PeripheralLightMaps.For(board.Path).Lights.Count);
                bench.Link(board);
                LightKeys(board, Red, new KeyFrame(new Dictionary<ushort, int> { [0x14] = Green }, null, null, Blue));
                bench.Passes();
                var iso = PeripheralLightMaps.For(board.Path);
                Assert.Equal(111, iso.Lights.Count);
                Assert.Contains(iso.Lights, l => l.Usage == 0x64);
                var frame = new List<byte[]>();
                for (int row = 0; row < 6; row++)
                {
                    // The frame's own background, Blue, on every other cell,
                    // a key's or none's.
                    var cells = Enumerable.Repeat(Blue, 22).ToArray();
                    if (row == 2) cells[2] = Green;
                    frame.Add(P.StandardRow(0xFF, row, cells));
                }
                frame.Add(P.StandardShow(0xFF));
                Assert.Equal(Lines(new[] { P.StandardBrightnessRead(0xFF, 0x05), P.KeyboardInfoRead(0xFF) }.Concat(frame).ToArray()),
                    board.Sent());

                // The next claim does not ask again.
                PeripheralOutputs.ReleaseLighting(board.Row, 0);
                bench.Passes();
                long mark = bench.Mark;
                bench.Light(board, Blue);
                bench.Passes();
                Assert.Empty(board.Sent(mark, r => r[P.ClassAt] == 0x00));
                bench.Verify();
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>A Basilisk V4 Pro's claim read is its region list, the
        /// read OpenRGB opens it with (RazerController.cpp:1207-1224), and
        /// the claim takes its regions from that answer with no second
        /// read: then the stored effect of LED 00 and the static effect on
        /// it, under ID 1F.</summary>
        [Fact]
        public void ABasiliskV4Pro_IsReadOnceForItsRegions_AndPaintedOnLed00()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x00E3);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            for (int step = 0; step < 50 && mouse.Sent().Length < 3; step++)
            {
                bench.Advance(32);
                bench.Passes(1);
            }
            bench.Advance(32);
            bench.Passes();
            Assert.Equal(Lines(P.RegionListRead(0x1F), P.StoredEffectRead(0x1F, 0x00), P.ExtendedStatic(0x1F, 0x00, Red)),
                mouse.Sent());
            bench.Verify();
        }

        /// <summary>A Leviathan V2 X's reports carry report ID 07 and its
        /// replies are read as report 07 (OpenRGB RazerController.cpp:50-60),
        /// its hand-back on the crash path's record too.</summary>
        [Fact]
        public void ALeviathanV2XsReports_CarryReportIdSeven()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var speaker = bench.Add(0x054A);
            bench.StartAndWalk();
            bench.Link(speaker);
            bench.Light(speaker, Red);
            bench.Passes();
            var calls = speaker.Calls;
            Assert.Equal(Lines(P.RegionListRead(0x1F), P.StoredEffectRead(0x1F, 0x00), P.ExtendedStatic(0x1F, 0x00, Red)),
                speaker.Sent());
            Assert.All(calls.Where(c => c.Kind == "set"), c => Assert.Equal(0x07, c.Bytes[0]));
            Assert.All(calls.Where(c => c.Kind == "get"), c => Assert.Equal(new byte[] { 0x07 }, c.Bytes));
            Assert.All(Assert.Single(bench.Backend.Claimed).HandBack, report => Assert.Equal(0x07, report[0]));
            bench.Verify();
        }

        /// <summary>A paired device the dock no longer lists leaves, and the
        /// dock's own product ID is never a unit behind it.</summary>
        [Fact]
        public void ADeviceTheDockNoLongerLists_Leaves()
        {
            PeripheralLightMaps.ResetForTest();
            try
            {
                using var bench = new Bench(reassertMs: int.MaxValue);
                var dock = bench.Add(MouseDockPro);
                var listed = new byte[] { 2, 0x01, 0x00, 0xAB, 0x01, 0x00, 0xA4 };
                dock.Answer = request => Is(request, 0x00, 0xBF) ? Reply(request, P.StatusSuccess, listed) : Answers(request);
                bench.StartAndWalk();
                bench.Link(dock);
                bench.Light(dock, Red);
                bench.Passes();
                bench.Passes();
                Assert.Equal(2, bench.Backend.Snapshot.Units.Count);

                // A busy answer is no list: the mouse stays.
                var answered = dock.Answer;
                dock.Answer = request => Is(request, 0x00, 0xBF) ? Reply(request, P.StatusBusy) : Answers(request);
                PeripheralOutputs.ReleaseLighting(dock.Row, 0);
                bench.Passes();
                bench.Light(dock, Red);
                bench.Passes();
                bench.Passes();
                Assert.Equal(2, bench.Backend.Snapshot.Units.Count);
                dock.Answer = answered;

                // Unpaired: the next claim's list names nobody.
                listed = new byte[] { 1, 0x00, 0xFF, 0xFF };
                PeripheralOutputs.ReleaseLighting(dock.Row, 0);
                bench.Passes();
                bench.Light(dock, Red);
                bench.Passes();
                bench.Passes();
                Assert.Equal(new[] { dock.Key }, bench.Backend.Snapshot.Units.Select(u => u.Key));
                bench.Verify();
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>The crash path's record holds the unit, with the
        /// hand-back built from what was read, by the time the first write
        /// goes out, and not while the claim is still only reading.</summary>
        [Fact]
        public void TheCrashRecordHoldsTheUnit_BeforeItsFirstWrite()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();

            var requests = mouse.Calls.Where(c => c.Kind == "set").ToList();
            Assert.Equal(6, requests.Count);
            foreach (var request in requests)
            {
                var record = bench.Records[request.Tick];
                if (IsRead(request.Bytes))
                {
                    Assert.Empty(record);
                    continue;
                }
                var entry = Assert.Single(record);
                Assert.Equal(mouse.Key, entry.Path);
                Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), Lines(entry.HandBack));
            }
            Assert.Equal(2, requests.Count(r => !IsRead(r.Bytes)));
        }

        /// <summary>A claim whose color goes away while its reads are still
        /// under way writes nothing: the handle is closed once the reads
        /// are through, and the unit never reaches the crash path's record.</summary>
        [Fact]
        public void AClaimNobodyWantsByTheTimeItsReadsAreThrough_WritesNothing()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            // A Viper Ultimate, whose claim is spread over several passes.
            var mouse = bench.Add(0x007A);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));

            // The switch goes off with the last of the claim's reads out.
            for (int step = 0; step < 8 && mouse.Calls.Count(c => c.Kind == "set") < 3; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            Assert.Equal(3, mouse.Calls.Count(c => c.Kind == "set"));
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            for (int step = 0; step < 8 && mouse.Open; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            Assert.False(mouse.Open);
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04), P.RegionListRead(0x3F), P.StoredEffectRead(0x3F, 0x04)),
                mouse.Sent());
            Assert.All(bench.Records.Values, record => Assert.Empty(record));
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(bench.Unit(mouse).Asleep);
            bench.Verify();
        }

        /// <summary>A claim nobody wants by the time it would read the device's own
        /// lighting reads none of it.</summary>
        [Fact]
        public void AClaimNobodyWantsAheadOfItsLastReads_AsksNothingMore()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x007A);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));

            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            for (int step = 0; step < 8 && mouse.Open; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            Assert.False(mouse.Open);
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04), P.RegionListRead(0x3F)), mouse.Sent());
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>No color is queued. Two colors that arrive inside the
        /// paint interval give one paint, the newer, once the interval is
        /// over.</summary>
        [Fact]
        public void TwoColorsInsideThePaintInterval_GiveOnePaint_TheNewer()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            long mark = bench.Mark;

            bench.Light(mouse, Green);
            bench.Passes();
            bench.Light(mouse, Blue);
            bench.Advance(RazerDirectBackend.PaintMs - 1);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));

            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Blue), P.ExtendedStatic(0x3F, 0x01, Blue)), mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>The claim reports and the color go out again every five
        /// seconds on a clock of their own, whatever the color is doing, and
        /// once more when Windows resumes from sleep. Between two of them a
        /// new color is the color alone.</summary>
        [Fact]
        public void TheClaimAndTheColorGoOutAgain_OnTheirInterval_AndOnAResume()
        {
            using var bench = new Bench();
            // A DeathAdder Chroma: classic LEDs, whose claim turns each on.
            var mouse = bench.Add(0x0043);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            string[] States(long after) => mouse.Sent(after, r => Is(r, 0x03, 0x00));
            string[] Colors(long after) => mouse.Sent(after, r => Is(r, 0x03, 0x01));
            Assert.Equal(Lines(P.ClassicState(0x3F, 0x01, true), P.ClassicState(0x3F, 0x04, true)), States(0));
            Assert.Equal(Lines(P.ClassicColor(0x3F, 0x01, Red), P.ClassicColor(0x3F, 0x04, Red)), Colors(0));

            // The color changes every 100 ms for 4.9 seconds: each change
            // is a color and an effect per LED, never a state.
            long mark = bench.Mark;
            int painted = 0;
            for (int at = 100; at < RazerDirectBackend.ReassertMs; at += 100)
            {
                bench.Advance(100);
                bench.Light(mouse, 0x010000 * (at / 100));
                bench.Passes();
                painted++;
            }
            Assert.Equal(49, painted);
            Assert.Empty(States(mark));
            Assert.Equal(2 * painted, Colors(mark).Length);

            // Nothing one tick short of the interval, and on it the claim
            // and the color as it stands.
            mark = bench.Mark;
            bench.Advance(99);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(
                P.ClassicState(0x3F, 0x01, true), P.ClassicState(0x3F, 0x04, true),
                P.ClassicColor(0x3F, 0x01, 0x310000), P.ClassicEffect(0x3F, 0x01, 0x00),
                P.ClassicColor(0x3F, 0x04, 0x310000), P.ClassicEffect(0x3F, 0x04, 0x00)), mouse.Sent(mark));

            // And not again until five more seconds have gone.
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.ReassertMs - 1);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(2, States(mark).Length);

            // Windows resumed: once more, at once.
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Backend.NoteResume();
            bench.Passes();
            Assert.Equal(6, mouse.Sent(mark).Length);
            Assert.Equal(2, States(mark).Length);
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            bench.Verify();
        }

        // ─────────────────────────────────────────────
        //  The transaction ID and a device that does not answer
        // ─────────────────────────────────────────────

        /// <summary>The claim read settles the transaction ID. A device that
        /// stays silent to the row's first ID is listed asleep, with no walk
        /// asked for, and is asked with the next ID five seconds later, one
        /// ID an attempt. The ID that answered is the one every report then
        /// carries.</summary>
        [Fact]
        public void TheClaimReadSettlesTheTransactionId_OneCandidateAnAttempt()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            // Its firmware reads 1F, OpenRGB's ID for it, and no other.
            mouse.Answer = request => request[P.TransactionAt] == 0x1F ? Answers(request) : Silence(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();

            // Five attempts under 3F, each read twice before the next.
            Assert.Equal(Enumerable.Repeat(RazerDirectProtocolTests.Line(P.ExtendedBrightnessRead(0x3F, 0x04)), 5), mouse.Sent());
            Assert.Equal(Enumerable.Repeat(new[] { "set", "get", "get" }, 5).SelectMany(p => p), mouse.Calls.Select(c => c.Kind));
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(0, Volatile.Read(ref bench.Walks));
            // 10 ms between attempts, never after the last.
            Assert.Equal(4, bench.Waits.Count(w => w == RazerDirectBackend.AttemptGapMs * 1000));

            // The host links no path to an asleep unit. Its row stays lit.
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs - 1);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));

            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x1F, 0x04)), mouse.Sent(mark));
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.False(mouse.Open);

            // Listed awake, it is linked again, and the standing claim paints.
            mark = bench.Mark;
            bench.Link(mouse);
            bench.Passes();
            Assert.Equal(Lines(
                P.ExtendedBrightnessRead(0x1F, 0x04), P.RegionListRead(0x1F),
                P.StoredEffectRead(0x1F, 0x04), P.StoredEffectRead(0x1F, 0x01),
                P.ExtendedStatic(0x1F, 0x04, Red), P.ExtendedStatic(0x1F, 0x01, Red)), mouse.Sent(mark));
            Assert.All(mouse.Calls.Where(c => c.Kind == "set" && c.Tick > mark), c => Assert.Equal(0x1F, c.Bytes[P.TransactionAt]));
            bench.Verify();
        }

        /// <summary>A device that never answers is asked with each of its
        /// IDs in turn, one attempt every five seconds, and no ID outside
        /// the row's list is ever sent.</summary>
        [Fact]
        public void ASilentDevice_IsAskedWithEachOfItsIdsInTurn_AndNoOther()
        {
            using var bench = new Bench();
            // A Mamba Chroma: FF, then 3F, then 80.
            var mouse = bench.Add(0x0044);
            mouse.Answer = Silence;
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            for (int attempt = 1; attempt < 5; attempt++)
            {
                bench.Advance(RazerDirectBackend.RetryMs);
                bench.Passes();
            }
            var ids = mouse.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes[P.TransactionAt]).ToArray();
            Assert.Equal(25, ids.Length);
            Assert.Equal(new byte[] { 0xFF, 0x3F, 0x80, 0xFF, 0x3F }, Enumerable.Range(0, 5).Select(i => ids[i * 5]));
            Assert.All(mouse.Calls.Where(c => c.Kind == "set"), c => Assert.True(Is(c.Bytes, 0x03, 0x83)));
            Assert.True(bench.Unit(mouse).Asleep);
            bench.Verify();
        }

        /// <summary>An answer that echoes the request settles the ID even
        /// where the firmware has no such value to give: 05 and 03 mean it
        /// read the request. The claim goes on and the first paint decides.</summary>
        [Theory]
        [InlineData((byte)0x05, 1)]
        [InlineData((byte)0x03, 5)]
        public void AClaimReadTheFirmwareRefuses_StillSettlesTheId(byte status, int attempts)
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = request => Is(request, 0x0F, 0x84) ? Reply(request, status) : Answers(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(attempts, mouse.Sent(0, r => Is(r, 0x0F, 0x84)).Length);
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)),
                mouse.Sent(0, r => Is(r, 0x0F, 0x02)));
            Assert.False(bench.Unit(mouse).Asleep);
            bench.Verify();
        }

        /// <summary>A device whose own brightness is 0 shows no color,
        /// whatever is painted on it. It is claimed and painted like any
        /// other, no report sets a brightness, and the log names it once
        /// however often it is claimed.</summary>
        [Fact]
        public void ADeviceWhoseOwnBrightnessIsZero_IsPaintedAsItIs_AndNamedOnce()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = request => Is(request, 0x0F, 0x84) ? Reply(request, P.StatusSuccess, 0x01, 0x04, 0x00) : Answers(request);
            string mark = DiagMark.Write();
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)),
                mouse.Sent(0, r => Is(r, 0x0F, 0x02)));

            // Released and claimed again: the same read, and still one line.
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(2, mouse.Sent(0, r => Is(r, 0x0F, 0x84)).Length);
            Assert.Equal(1, DiagMark.LinesSince(mark, "PERIPHERAL Razer 'Razer DeathAdder V2' has its brightness at 0"));
            // Every write is the effect report, a color or the hand-back.
            Assert.All(mouse.Calls.Where(c => c.Kind == "set" && !IsRead(c.Bytes)), c => Assert.True(Is(c.Bytes, 0x0F, 0x02)));
            bench.Verify();
        }

        /// <summary>A receiver that answers 04 has a mouse that is asleep or
        /// on its cable. The unit is listed asleep with nothing on the crash
        /// path's record, and it is asked again every five seconds until
        /// the mouse answers.</summary>
        [Fact]
        public void AReceiverThatAnswers04_HasAMouseThatIsAsleep_AndIsAskedAgain()
        {
            using var bench = new Bench();
            // A Basilisk V3 Pro on its receiver.
            var mouse = bench.Add(0x00AB);
            bool asleep = true;
            mouse.Answer = request => Volatile.Read(ref asleep) ? Reply(request, P.StatusTimeout) : Answers(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            // A slow reply is not waited for in place: the read comes a
            // pass after its wait is over.
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            bench.Advance(31);
            bench.Passes();
            Assert.Equal(new[] { "set", "get" }, mouse.Calls.Select(c => c.Kind));
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(mouse.Open);
            Assert.Equal(0, Volatile.Read(ref bench.Walks));

            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            bench.Advance(31);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x1F, 0x00)), mouse.Sent(mark));
            Assert.True(bench.Unit(mouse).Asleep);

            Volatile.Write(ref asleep, false);
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            bench.Advance(31);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x1F, 0x00)), mouse.Sent(mark));
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Empty(bench.Waits);
            bench.Verify();
        }

        /// <summary>A receiver's 04 settles no ID: it says so for a mouse
        /// that is asleep and for an ID nothing behind it reads alike. A
        /// Naga Pro whose receiver answers 04 under the row's first ID, and
        /// whose mouse reads the second, is asked under the second at the
        /// next attempt and painted under it.</summary>
        [Fact]
        public void AReceivers04_SettlesNoId_AndTheNextAttemptAsksUnderTheRowsNextOne()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x0090);
            Assert.Equal(new byte[] { 0x1F, 0xFF }, P.TransactionIds(mouse.Table));
            mouse.Answer = request => request[P.TransactionAt] == 0xFF ? Answers(request) : Reply(request, P.StatusTimeout);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            bench.Advance(31);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x1F, 0x00)), mouse.Sent());
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Equal(0, Volatile.Read(ref bench.Walks));

            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            bench.Advance(31);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0xFF, 0x00)), mouse.Sent(mark));
            Assert.False(bench.Unit(mouse).Asleep);

            // Listed awake and linked again, it is claimed and painted
            // under the ID that answered.
            mark = bench.Mark;
            bench.Link(mouse);
            for (int step = 0; step < 30 && mouse.Sent(mark, r => Is(r, 0x0F, 0x03)).Length == 0; step++)
            {
                bench.Passes();
                bench.Advance(31);
            }
            bench.Passes();
            bench.Advance(31);
            bench.Passes();
            Assert.Contains(RazerDirectProtocolTests.Line(P.ExtendedFrameRow(0xFF, 3, Red)), mouse.Sent(mark));
            Assert.All(mouse.Calls.Where(c => c.Kind == "set" && c.Tick > mark), c => Assert.Equal(0xFF, c.Bytes[P.TransactionAt]));
            Assert.Single(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>A DeathAdder Chroma is read under FF, the ID openrazer
        /// reads it under, and painted under 3F, the ID openrazer paints it
        /// under. A firmware that answers each kind only under its own ID
        /// is claimed at the first attempt: the read's answer settles the
        /// row's first ID for every write, and every read keeps the ID
        /// that answered.</summary>
        [Fact]
        public void AProductReadUnderAnIdOfItsOwn_IsAskedUnderItFirst_AndPaintedUnderItsFirstId()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x0043);
            mouse.Answer = request => request[P.TransactionAt] == (IsRead(request) ? 0xFF : 0x3F)
                ? Answers(request) : Silence(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(RazerDirectProtocolTests.Line(P.StandardBrightnessRead(0xFF, 0x01)), mouse.Sent()[0]);
            Assert.All(mouse.Calls.Where(c => c.Kind == "set"),
                c => Assert.Equal(IsRead(c.Bytes) ? 0xFF : 0x3F, c.Bytes[P.TransactionAt]));
            // Every request was answered at its first attempt.
            Assert.Equal(mouse.Calls.Count(c => c.Kind == "set"), mouse.Sent().Distinct().Count());
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Single(bench.Backend.Claimed);
            Assert.Equal(Lines(P.ClassicColor(0x3F, 0x01, Red), P.ClassicColor(0x3F, 0x04, Red)), mouse.Sent(0, r => Is(r, 0x03, 0x01)));

            // Let go and claimed again: the read under FF, once.
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            long mark = bench.Mark;
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.Equal(Lines(P.StandardBrightnessRead(0xFF, 0x01)), mouse.Sent(mark, r => Is(r, 0x03, 0x83)));
            Assert.Equal(Lines(P.ClassicColor(0x3F, 0x01, Green), P.ClassicColor(0x3F, 0x04, Green)), mouse.Sent(mark, r => Is(r, 0x03, 0x01)));
            bench.Verify();
        }

        /// <summary>The same product on a firmware that reads 3F alone. The
        /// read under FF goes unanswered and the unit is listed asleep. The
        /// next attempt asks under 3F, which answers and then carries every
        /// report, the read included.</summary>
        [Fact]
        public void AProductThatDoesNotAnswerTheReadsOwnId_IsAskedUnderItsPaintIdNext()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x0043);
            mouse.Answer = request => request[P.TransactionAt] == 0x3F ? Answers(request) : Silence(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Enumerable.Repeat(RazerDirectProtocolTests.Line(P.StandardBrightnessRead(0xFF, 0x01)), 5), mouse.Sent());
            Assert.True(bench.Unit(mouse).Asleep);

            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(Lines(P.StandardBrightnessRead(0x3F, 0x01)), mouse.Sent(mark));
            Assert.False(bench.Unit(mouse).Asleep);

            mark = bench.Mark;
            bench.Link(mouse);
            bench.Passes();
            Assert.NotEmpty(mouse.Sent(mark, r => Is(r, 0x03, 0x01)));
            Assert.All(mouse.Calls.Where(c => c.Kind == "set" && c.Tick > mark), c => Assert.Equal(0x3F, c.Bytes[P.TransactionAt]));
            Assert.Single(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>A device that answers the claim read and then takes no
        /// report of the claim is written to under the row's next ID at
        /// the next attempt, and read under the ID that answered. A
        /// BlackWidow V4 X whose firmware reads under 1F and takes its
        /// color under 3F, the two IDs openrazer sends it, is claimed at
        /// the third attempt.</summary>
        [Fact]
        public void ADeviceThatAnswersTheReadAndTakesNoWrite_IsWrittenUnderTheRowsNextId()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var keyboard = bench.Add(0x0293);
            Assert.Equal(new byte[] { 0x3F, 0x1F }, P.TransactionIds(keyboard.Table));
            keyboard.Answer = request => request[P.TransactionAt] == (IsRead(request) ? 0x1F : 0x3F)
                ? Answers(request) : Silence(request);
            string diag = DiagMark.Write();
            bench.StartAndWalk();
            bench.Link(keyboard);
            bench.Light(keyboard, Red);
            bench.Passes();
            // The first attempt: the read under 3F, which gets no answer.
            Assert.Equal(Enumerable.Repeat(RazerDirectProtocolTests.Line(P.ExtendedBrightnessRead(0x3F, 0x05)), 5), keyboard.Sent());
            Assert.True(bench.Unit(keyboard).Asleep);

            // The second: the read under 1F answers, and the color under
            // 1F gets none.
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes(4);
            var second = keyboard.Calls.Where(c => c.Kind == "set" && c.Tick > mark).ToList();
            Assert.All(second, c => Assert.Equal(0x1F, c.Bytes[P.TransactionAt]));
            Assert.Equal(5, second.Count(c => Is(c.Bytes, 0x0F, 0x02)));
            Assert.True(bench.Unit(keyboard).Asleep);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(1, DiagMark.LinesSince(diag, "answers reads under 1F and took no report of its claim, writing under 3F next"));

            // The third: reads under 1F, the color under 3F.
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes(4);
            var third = keyboard.Calls.Where(c => c.Kind == "set" && c.Tick > mark).ToList();
            Assert.All(third, c => Assert.Equal(IsRead(c.Bytes) ? 0x1F : 0x3F, c.Bytes[P.TransactionAt]));
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x05, Red)),
                third.Where(c => !IsRead(c.Bytes)).Select(c => RazerDirectProtocolTests.Line(c.Bytes)));
            Assert.False(bench.Unit(keyboard).Asleep);
            Assert.Single(bench.Backend.Claimed);

            // The pair holds from then on: let go, claimed again, one attempt.
            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(keyboard.Row, 0);
            bench.Passes();
            bench.Light(keyboard, Green);
            bench.Passes();
            Assert.All(keyboard.Calls.Where(c => c.Kind == "set" && c.Tick > mark),
                c => Assert.Equal(IsRead(c.Bytes) ? 0x1F : 0x3F, c.Bytes[P.TransactionAt]));
            Assert.Contains(RazerDirectProtocolTests.Line(P.ExtendedStatic(0x3F, 0x05, Green)), keyboard.Sent(mark));
            Assert.Equal(1, DiagMark.LinesSince(diag, "took no report of its claim"));
            bench.Verify();
        }

        /// <summary>Only a claim that never got a report through moves its
        /// writes to another ID. A lit device that stops answering, and one
        /// that took the first report of its first paint and no more, are
        /// asked again five seconds later under the IDs that worked.</summary>
        [Fact]
        public void ADeviceThatTookAReport_KeepsTheIdsThatWorked_WhenItStopsAnswering()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);

            // Lit, and then silent to its next color.
            string diag = DiagMark.Write();
            mouse.Answer = Silence;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);

            // It answers again, all but its scroll wheel's color: the first
            // paint of the new claim gets one report through and no more.
            // The hand-back it is owed from the claim before goes out
            // first, and it takes both reports of that.
            mouse.Answer = request => Is(request, 0x0F, 0x02) && request[P.ArgumentsAt + 1] == 0x01 && request[P.ArgumentsAt + 2] == 0x01
                ? Silence(request) : Answers(request);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes(4);
            Assert.Contains(RazerDirectProtocolTests.Line(P.ExtendedStatic(0x3F, 0x04, Green)), mouse.Sent(mark));
            Assert.True(bench.Unit(mouse).Asleep);

            mouse.Answer = Answers;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes(4);
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Single(bench.Backend.Claimed);
            Assert.All(mouse.Calls.Where(c => c.Kind == "set" && c.Tick > mark), c => Assert.Equal(0x3F, c.Bytes[P.TransactionAt]));
            Assert.Equal(0, DiagMark.LinesSince(diag, "took no report of its claim"));
            bench.Verify();
        }

        /// <summary>A row with one ID has no other to write under. A Naga
        /// Epic that answers its reads and takes no report is asked again
        /// under the same pair: reads under FF, writes under 3F.</summary>
        [Fact]
        public void AOneIdRow_ThatTakesNoWrite_IsAskedAgainUnderTheSameIds()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x001F);
            Assert.Equal(new byte[] { 0x3F }, P.TransactionIds(mouse.Table));
            mouse.Answer = request => IsRead(request) ? Answers(request) : Silence(request);
            string diag = DiagMark.Write();
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes(4);
            var again = mouse.Calls.Where(c => c.Kind == "set" && c.Tick > mark).Select(c => c.Bytes).ToList();
            Assert.Contains(again, r => !IsRead(r));
            Assert.All(again, r => Assert.Equal(IsRead(r) ? 0xFF : 0x3F, r[P.TransactionAt]));
            Assert.Equal(0, DiagMark.LinesSince(diag, "took no report of its claim"));
        }

        /// <summary>The asleep rule. A write that fails closes the unit's
        /// channel and lists the unit asleep, never takes it off the list,
        /// and asks for a walk once. Nothing can be handed back then, with
        /// no channel left to hand it back through, so the hand-back is
        /// owed and keeps its place on the crash path's record. The unit
        /// is tried again on the worker's own interval. When it answers,
        /// the hand-back is the first thing it is sent, and then it is
        /// listed awake and the standing claim paints again.</summary>
        [Fact]
        public void AFailedWrite_ClosesTheChannel_ListsTheUnitAsleep_AndTheClaimPaintsAgainOnceItAnswers()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);
            int changes = Volatile.Read(ref bench.Changes);

            // The device stops taking reports in the middle of a session.
            long mark = bench.Mark;
            mouse.FailWrites = true;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Blue);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            Assert.False(mouse.Open);
            var unit = bench.Unit(mouse);
            Assert.NotNull(unit);
            Assert.True(unit.Asleep);
            Assert.Equal(mouse.Container, unit.ContainerId);
            var owed = Assert.Single(bench.Backend.Claimed);
            Assert.Equal(mouse.Key, owed.Path);
            Assert.Equal(MouseHandBack, Lines(owed.HandBack));
            Assert.Equal(1, Volatile.Read(ref bench.Walks));
            Assert.Equal(changes + 1, Volatile.Read(ref bench.Changes));

            // The host unlinks an asleep unit and walks. The collection is
            // still there, so the unit stays listed, asleep.
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            bench.Walk();
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Equal(changes + 1, Volatile.Read(ref bench.Changes));

            // Tried again after five seconds, and still failing: one more
            // open, and no second walk asked for.
            int opens = mouse.Opens;
            bench.Advance(RazerDirectBackend.RetryMs - 1);
            bench.Passes();
            Assert.Equal(opens, mouse.Opens);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(opens + 1, mouse.Opens);
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Equal(1, Volatile.Read(ref bench.Walks));

            // It answers again: handed back what it was owed and listed
            // awake, then linked, claimed and painted in the color that
            // stands now.
            mouse.FailWrites = false;
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04)).Concat(MouseHandBack), mouse.Sent(mark));
            Assert.Empty(bench.Backend.Claimed);
            mark = bench.Mark;
            bench.Link(mouse);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Blue), P.ExtendedStatic(0x3F, 0x01, Blue)),
                mouse.Sent(mark, r => Is(r, 0x0F, 0x02)));
            Assert.Single(bench.Backend.Claimed);
            Assert.Equal(1, Volatile.Read(ref bench.Walks));
            bench.Verify();
        }

        /// <summary>A read that fails at the handle is a failed call like a
        /// write that does, and a collection that cannot be opened at all
        /// is one too: asleep, with a walk asked for.</summary>
        [Fact]
        public void AReadThatFailsAtTheHandle_OrAnOpenThatFails_IsAFailedCall()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            mouse.Answer = _ => null;
            keyboard.OpenFails = true;
            bench.StartAndWalk();
            // Both colors stand before the table links either unit, so one
            // pass finds both wanted.
            bench.Light(mouse, Red);
            bench.Light(keyboard, Red);
            bench.Link(mouse, keyboard);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.True(bench.Unit(keyboard).Asleep);
            Assert.Equal(5, mouse.Sent().Length);
            Assert.Equal(0, keyboard.Opens);
            // One ask for a walk answers both.
            Assert.Equal(1, Volatile.Read(ref bench.Walks));
            Assert.Equal(2, bench.Backend.Snapshot.Units.Count);
        }

        /// <summary>An asleep unit is asked again on the worker's interval
        /// only while somebody still wants it lit: a row that held its path
        /// has a claim, or a Set Chroma Color is live on its virtual
        /// controller. A device nobody lights any more is asked once after
        /// each walk the host hands over and is otherwise sent nothing.</summary>
        [Fact]
        public void AnAsleepUnitNobodyLights_IsLeftAlone_UntilSomebodyDoes_OrAWalkComesIn()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = Silence;
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            PeripheralOutputs.PublishLinks(LinkTable.Empty);

            // The switch goes off while it sleeps.
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            int opens = mouse.Opens;
            bench.Advance(3 * RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(opens, mouse.Opens);
            Assert.True(bench.Unit(mouse).Asleep);

            // A Set Chroma Color on its virtual controller wants it.
            bench.OnSlot[3] = new HashSet<Guid> { mouse.Row };
            PeripheralOutputs.AssertChromaMacro(3, 1, 2, 3, bench.Now);
            bench.Passes();
            Assert.Equal(opens + 1, mouse.Opens);

            // And so does the switch, once the macro has run out.
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(opens + 1, mouse.Opens);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(opens + 2, mouse.Opens);
        }

        /// <summary>The row that held an asleep unit can be replaced while
        /// the unit sleeps, and the new row is linked to nothing until the
        /// unit is listed awake. So each walk the host hands over, which it
        /// makes when a row comes or goes, asks an asleep unit once more
        /// whoever lights it, no sooner than its interval allows, and a
        /// unit that answers is listed awake for the new row to take.</summary>
        [Fact]
        public void AWalkAsksAnAsleepUnitOnceMore_SoARowReplacedWhileItSleptFindsItAwake()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = Silence;
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);

            // The row is removed: no table links the unit, no claim stands.
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            int opens = mouse.Opens;

            // A walk, inside the interval of the attempt before: nothing
            // yet, and one attempt once the interval is over.
            bench.Walk();
            bench.Advance(RazerDirectBackend.RetryMs - 1);
            bench.Passes();
            Assert.Equal(opens, mouse.Opens);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(opens + 1, mouse.Opens);
            Assert.True(bench.Unit(mouse).Asleep);

            // One walk, one attempt: time alone brings no other.
            bench.Advance(3 * RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(opens + 1, mouse.Opens);

            // The mouse is awake at the next walk. It is listed awake, with
            // nothing but the claim read sent, under the ID whose turn it
            // is, and no handle left open.
            mouse.Answer = Answers;
            long mark = bench.Mark;
            bench.Walk();
            bench.Passes();
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04)), mouse.Sent(mark));
            Assert.False(mouse.Open);

            // Awake, it is asked nothing more until a row lights it.
            mark = bench.Mark;
            bench.Walk();
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            bench.Verify();
        }

        // ─────────────────────────────────────────────
        //  A hand-back that is owed
        // ─────────────────────────────────────────────

        /// <summary>The two reports a DeathAdder V2 is handed back with:
        /// the spectrum effect on its logo and on its scroll wheel, since
        /// the bench's device says nothing of a stored effect.</summary>
        private static string[] MouseHandBack => Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01));

        /// <summary>A DeathAdder V2 painted red that goes silent to its
        /// next color, and whose switch then goes off: listed asleep, lit
        /// and linked by nobody, and owed its hand-back.</summary>
        private static FakeRazer AsleepAndOwed(Bench bench)
        {
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            mouse.Answer = Silence;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.False(mouse.Open);
            var owed = Assert.Single(bench.Backend.Claimed);
            Assert.Equal(mouse.Key, owed.Path);
            Assert.Equal(MouseHandBack, Lines(owed.HandBack));
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            return mouse;
        }

        /// <summary>A unit that falls asleep painted is owed its hand-back,
        /// whether or not anybody still lights it. The hand-back keeps its
        /// place on the crash path's record, the unit is asked again on
        /// the worker's interval with nobody lighting it, and once it
        /// answers the hand-back goes out behind the question, before the
        /// unit is listed awake. Nothing follows it, since no row wants
        /// the unit.</summary>
        [Fact]
        public void AUnitThatFallsAsleepPainted_IsOwedItsHandBack_AndGetsItWhenItAnswers_LitOrNot()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = AsleepAndOwed(bench);

            // Nobody lights it, and it is asked all the same, once an
            // interval.
            int opens = mouse.Opens;
            bench.Advance(RazerDirectBackend.RetryMs - 1);
            bench.Passes();
            Assert.Equal(opens, mouse.Opens);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(opens + 1, mouse.Opens);
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Single(bench.Backend.Claimed);
            int changes = Volatile.Read(ref bench.Changes);

            // It answers: the question, the hand-back, and then awake.
            string diag = DiagMark.Write();
            mouse.Answer = Answers;
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04)).Concat(MouseHandBack), mouse.Sent(mark));
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(changes + 1, Volatile.Read(ref bench.Changes));
            Assert.Equal(1, DiagMark.LinesSince(diag, "lighting handed back, owed since it stopped answering"));
            // The record held the hand-back until the last report of it
            // was answered.
            Assert.All(mouse.Calls.Where(c => c.Tick > mark), c => Assert.Single(bench.Records[c.Tick]));

            // Nothing more is asked of it.
            mark = bench.Mark;
            bench.Advance(3 * RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            bench.Verify();
        }

        /// <summary>An owed hand-back the device takes part of is still
        /// owed whole. The unit goes back to sleep without having been
        /// listed awake, the record keeps the hand-back, and the next
        /// attempt sends every report of it again.</summary>
        [Fact]
        public void AnOwedHandBackTheDeviceDoesNotTake_StaysOwedWhole_AndIsSentAgain()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = AsleepAndOwed(bench);
            int changes = Volatile.Read(ref bench.Changes);
            int walks = Volatile.Read(ref bench.Walks);

            // It answers its question and its logo's report, and falls
            // silent on the scroll wheel's.
            mouse.Answer = request => Is(request, 0x0F, 0x02) && request[P.ArgumentsAt + 1] == 0x01 ? Silence(request) : Answers(request);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x04))
                .Concat(Enumerable.Repeat(MouseHandBack[1], RazerDirectBackend.Attempts)), mouse.Sent(mark));
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.False(mouse.Open);
            Assert.Equal(MouseHandBack, Lines(Assert.Single(bench.Backend.Claimed).HandBack));
            Assert.Equal(changes, Volatile.Read(ref bench.Changes));
            Assert.Equal(walks, Volatile.Read(ref bench.Walks));

            // Whole again at the next attempt, and then it is awake.
            mouse.Answer = Answers;
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04)).Concat(MouseHandBack), mouse.Sent(mark));
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>An owed hand-back is never skipped for the lock, as no
        /// hand-back is. Its question needs the lock like any read. Behind
        /// it, with another program at the lock, the first report waits
        /// for it once and goes out without it, and the second asks with
        /// no wait.</summary>
        [Fact]
        public void AnOwedHandBack_WaitsForABusyLockOnce_AndGoesOutWithoutIt()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = AsleepAndOwed(bench);
            // Another program takes the lock as the mouse answers its
            // question.
            long asked = long.MaxValue;
            mouse.Answer = request =>
            {
                if (Is(request, 0x0F, 0x84) && !bench.Guard.Busy)
                {
                    bench.Guard.Busy = true;
                    Volatile.Write(ref asked, bench.Mark);
                }
                return Answers(request);
            };
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04)).Concat(MouseHandBack), mouse.Sent(mark));
            Assert.Equal(new[] { ("busy", RazerGuard.CommandWaitMs), ("busy", 0) },
                bench.Guard.Journal.Where(e => e.Tick > Volatile.Read(ref asked) && e.What != "release").Select(e => (e.What, e.WaitMs)));
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Empty(bench.Backend.Claimed);
        }

        /// <summary>The owed hand-back goes out before a claim reads the
        /// device again. This DeathAdder Chroma answers a color read with
        /// the color it was last sent, stored or not, which no reference
        /// rules out for a classic LED. It falls silent in the middle of a
        /// session and answers again with its row still lit. It is handed
        /// back first, so the claim that follows reads the mouse's own
        /// colors and not the one it was painted, and the release at the
        /// end gives those back.</summary>
        [Fact]
        public void AnOwedHandBack_GoesOutBeforeTheNextClaimReadsTheDevice()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x0043);
            var shown = new ConcurrentDictionary<byte, int>();
            shown[0x01] = 0xAABBCC;
            shown[0x04] = 0x112233;
            int silent = 0;
            mouse.Answer = request =>
            {
                if (Volatile.Read(ref silent) == 1) return Silence(request);
                byte led = request[P.ArgumentsAt + 1];
                if (Is(request, 0x03, 0x01))
                    shown[led] = (request[P.ArgumentsAt + 2] << 16) | (request[P.ArgumentsAt + 3] << 8) | request[P.ArgumentsAt + 4];
                if (!Is(request, 0x03, 0x81)) return Answers(request);
                int rgb = shown[led];
                return Reply(request, P.StatusSuccess, 0x01, led, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            };
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            string[] own =
            {
                "3F 03/01 size 05 [00 01 AA BB CC]", "3F 03/02 size 03 [00 01 00]", "3F 03/00 size 03 [00 01 00]",
                "3F 03/01 size 05 [00 04 11 22 33]", "3F 03/02 size 03 [00 04 00]", "3F 03/00 size 03 [00 04 00]",
            };
            Assert.Equal(own, Lines(bench.Backend.Claimed.Single().HandBack));
            Assert.Equal(Red, shown[0x01]);

            // Silent to its next color, and asleep.
            Volatile.Write(ref silent, 1);
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Equal(Red, shown[0x01]);

            // It answers again, lit as it is: the hand-back first, then
            // the claim, which reads the mouse's own colors.
            Volatile.Write(ref silent, 0);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes(4);
            var sent = mouse.Sent(mark);
            Assert.Equal(Lines(P.StandardBrightnessRead(0xFF, 0x01)).Concat(own), sent.Take(7));
            Assert.Contains(RazerDirectProtocolTests.Line(P.ClassicColorRead(0xFF, 0x01)), sent.Skip(7));
            Assert.Equal(Green, shown[0x01]);
            Assert.Equal(own, Lines(bench.Backend.Claimed.Single().HandBack));

            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(own, mouse.Sent(mark));
            Assert.Equal(0xAABBCC, shown[0x01]);
            Assert.Equal(0x112233, shown[0x04]);
            bench.Verify();
        }

        /// <summary>A unit that took no write of its claim is owed nothing
        /// when it falls asleep: nothing of PadForge's is on it.</summary>
        [Fact]
        public void AUnitThatTookNoWriteOfItsClaim_IsOwedNothing()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = request => IsRead(request) ? Answers(request) : Silence(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Empty(bench.Backend.Claimed);
            bench.Backend.Stop();
            Assert.Empty(bench.OneShots);
        }

        /// <summary>A stop that finds a unit asleep and owed has no handle
        /// on it. The hand-back goes out as on the crash path: each report
        /// through a handle opened for it, bounded, behind the lock for no
        /// longer than a paint waits, with the product's reply time
        /// between two reports since no reply is read. The record is empty
        /// after it.</summary>
        [Fact]
        public void AtAStop_WhatIsOwedGoesOutThroughAHandleOpenedForEachReport()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = AsleepAndOwed(bench);
            int opens = mouse.Opens;
            int waits = bench.Waits.Count;
            long mark = bench.Mark;

            bench.Backend.Stop();
            Assert.False(bench.Backend.WorkerAlive);
            Assert.Equal(new[]
            {
                (mouse.Key, MouseHandBack[0], RazerDirectBackend.OneShotMs),
                (mouse.Key, MouseHandBack[1], RazerDirectBackend.OneShotMs),
            }, bench.OneShots.Select(s => (s.Path, RazerDirectProtocolTests.Line(s.Report), s.TimeoutMs)));
            Assert.Equal(new[] { ("take", RazerGuard.PaintWaitMs), ("release", 0), ("take", RazerGuard.PaintWaitMs), ("release", 0) },
                bench.Guard.Journal.Where(e => e.Tick > mark).Select(e => (e.What, e.WaitMs)));
            Assert.Equal(new[] { mouse.Table.ReplyWaitUs }, bench.Waits.Skip(waits));
            Assert.Equal(opens, mouse.Opens);
            Assert.Empty(bench.Backend.Claimed);
        }

        /// <summary>A report of an owed hand-back that does not go out at
        /// a stop ends it: the rest would fare no better.</summary>
        [Fact]
        public void AtAStop_AnOwedReportThatDoesNotGoOut_EndsTheHandBack()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            AsleepAndOwed(bench);
            bench.OneShotFails = true;
            bench.Backend.Stop();
            Assert.Equal(new[] { MouseHandBack[0] }, bench.OneShots.Select(s => RazerDirectProtocolTests.Line(s.Report)));
            Assert.Equal(bench.Guard.Journal.Count(e => e.What == "take"), bench.Guard.Journal.Count(e => e.What == "release"));
            Assert.Empty(bench.Backend.Claimed);
        }

        /// <summary>A Razer program that starts in the moment before the
        /// worker stops gets an owed unit as it gets a lit one: the
        /// worker's last act sends it nothing. The program and the stop
        /// both come from the worker's own thread here, as a pass ends, so
        /// no pass falls between them and the last act.</summary>
        [Fact]
        public void TheWorkersLastAct_SendsNothingOwed_WhileARazerProgramRuns()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            AsleepAndOwed(bench);
            var worker = Assert.Single(bench.Workers);
            int armed = 1;
            bench.Backend.SnapshotChanged += () =>
            {
                if (Interlocked.Exchange(ref armed, 0) != 1) return;
                PeripheralOutputs.Presence = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
                bench.Backend.SignalStop();
            };
            long mark = bench.Mark;
            bench.Add(BlackWidowChroma);
            bench.Walk();
            Assert.True(worker.Join(30000));
            Assert.Empty(bench.OneShots);
            Assert.Equal(mark, bench.Mark);
            Assert.Empty(bench.Backend.Claimed);
        }

        /// <summary>The crash path hands back what is owed with the rest
        /// of its record. A worker held inside a call on another device
        /// cannot, so the reports go out through handles of the crash
        /// path's own.</summary>
        [Fact]
        public void TheCrashPath_HandsBackWhatIsOwed_WhenTheWorkerIsHeld()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            // A keyboard painted by frame, which has no hand-back.
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            bench.StartAndWalk();
            bench.Light(mouse, Red);
            bench.Light(keyboard, Red);
            bench.Link(mouse, keyboard);
            bench.Passes();
            mouse.Answer = Silence;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            try
            {
                HoldTheWorkerInARead(bench, keyboard, gate);
                bench.Backend.ReleaseClaimedNow(bench.Now + 250);
                Assert.True(bench.Backend.WorkerAlive);
                Assert.Equal(MouseHandBack.Select(line => (mouse.Key, line)),
                    bench.OneShots.Select(s => (s.Path, RazerDirectProtocolTests.Line(s.Report))));
                Assert.Empty(bench.Backend.Claimed);
            }
            finally
            {
                gate.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !bench.Backend.WorkerAlive, 30000), "the worker ended once it came back");
        }

        /// <summary>A Razer program that starts has every Razer device, an
        /// asleep one too. What a unit was owed ends there with no report:
        /// off the record at once, and not sent when the program has quit
        /// and the unit answers again, nor at a stop.</summary>
        [Fact]
        public void WhenARazerProgramStarts_WhatAUnitIsOwedEndsWithNoReport()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = AsleepAndOwed(bench);
            long mark = bench.Mark;
            PeripheralOutputs.Presence = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            bench.Passes();
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(mark, bench.Mark);

            // The program quits and a walk asks the unit once more: its
            // question, and nothing behind it.
            PeripheralOutputs.Presence = PeripheralPresence.None;
            mouse.Answer = Answers;
            bench.Walk();
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedBrightnessRead(0x3F, 0x04)), mouse.Sent(mark));
            Assert.False(bench.Unit(mouse).Asleep);
            bench.Backend.Stop();
            Assert.Empty(bench.OneShots);
        }

        /// <summary>What a unit is owed ends when its collection leaves
        /// the walk: the device is gone, and with it anything a hand-back
        /// could reach.</summary>
        [Fact]
        public void WhatAUnitIsOwed_EndsWhenItsCollectionLeavesTheWalk()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = AsleepAndOwed(bench);
            long mark = bench.Mark;
            mouse.Present = false;
            bench.Walk();
            bench.Passes();
            Assert.Empty(bench.Backend.Snapshot.Units);
            Assert.Empty(bench.Backend.Claimed);
            bench.Backend.Stop();
            Assert.Empty(bench.OneShots);
            Assert.Equal(mark, bench.Mark);
        }

        /// <summary>A DeathAdder Chroma that turns its LEDs on, answers 05
        /// to its first color and falls silent from there: off the list as
        /// a product the table cannot paint, lit and linked by nobody, and
        /// owed the hand-back of the two reports it took.</summary>
        private static FakeRazer RefusedAndOwed(Bench bench, out string[] handBack)
        {
            var mouse = bench.Add(0x0043);
            int refused = 0;
            mouse.Answer = request =>
            {
                if (Volatile.Read(ref refused) == 1) return Silence(request);
                if (!Is(request, 0x03, 0x01)) return Answers(request);
                Volatile.Write(ref refused, 1);
                return Reply(request, P.StatusNotSupported);
            };
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Empty(bench.Backend.Snapshot.Units);
            Assert.False(mouse.Open);
            handBack = Lines(Assert.Single(bench.Backend.Claimed).HandBack);
            Assert.Equal(6, handBack.Length);
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            return mouse;
        }

        /// <summary>A product that refuses every paint leaves the list
        /// once its hand-back is through. One that misses that hand-back
        /// is still owed it. It stays out of the list and on the record,
        /// is asked again on the interval with nobody lighting it, and
        /// leaves for good once the hand-back went out.</summary>
        [Fact]
        public void AProductThatRefusesEveryPaint_AndMissesItsHandBack_GetsItWhenItAnswers_ThenLeaves()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = RefusedAndOwed(bench, out string[] handBack);
            int opens = mouse.Opens;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(opens + 1, mouse.Opens);
            Assert.Single(bench.Backend.Claimed);

            mouse.Answer = Answers;
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(Lines(P.StandardBrightnessRead(0xFF, 0x01)).Concat(handBack), mouse.Sent(mark));
            Assert.Empty(bench.Backend.Claimed);
            Assert.Empty(bench.Backend.Snapshot.Units);
            Assert.False(mouse.Open);

            // Gone for good, as a product the table cannot paint: no walk
            // and no length of time brings another question.
            mark = bench.Mark;
            bench.Walk();
            bench.Advance(3 * RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            bench.Verify();
        }

        /// <summary>Such a product that is unplugged while it is owed
        /// takes the debt with it, like any unit: nothing more is asked of
        /// its collection, and a stop sends it nothing.</summary>
        [Fact]
        public void AProductThatRefusesEveryPaint_AndLeavesWhileItIsOwed_TakesTheDebtWithIt()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = RefusedAndOwed(bench, out _);
            mouse.Answer = Answers;
            long mark = bench.Mark;
            mouse.Present = false;
            bench.Walk();
            bench.Passes();
            Assert.Empty(bench.Backend.Claimed);
            bench.Advance(3 * RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            bench.Backend.Stop();
            Assert.Empty(bench.OneShots);
        }

        /// <summary>One device hung keeps the worker past the next run's wait, and the
        /// other shows that worker's picture. The next run reads none of that device's
        /// own lighting, and hands back what that worker read.</summary>
        [Fact]
        public void AWorkerThatOutlivedItsRun_LeavesWhatItReadToTheNextRun()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            try
            {
                bench.StartAndWalk();
                bench.Light(mouse, Red);
                bench.Light(keyboard, Red);
                bench.Link(mouse, keyboard);
                bench.Passes();
                var read = Lines(bench.Backend.Claimed.First(entry => entry.Path == mouse.Key).HandBack);
                Assert.NotEmpty(read);
                var first = Assert.Single(bench.Workers);

                int held = 0;
                keyboard.Answer = request =>
                {
                    if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                    return Answers(request);
                };
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(keyboard, Blue);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the first worker is held");
                bench.Backend.Dispose();

                long mark = bench.Mark;
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                Assert.True(SpinWait.SpinUntil(
                    () => bench.Backend.Claimed.Any(entry => entry.Path == mouse.Key), 30000), "the next run claimed the mouse");
                Assert.Empty(mouse.Sent(mark, r => Is(r, 0x0F, 0x82)));
                Assert.Equal(read, Lines(bench.Backend.Claimed.First(entry => entry.Path == mouse.Key).HandBack));
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A claim that ends before it takes a late worker's hand-back leaves it
        /// for the next claim: a row unlinked during the claim's first reads and linked again
        /// has the next run read none of what that worker left on the device.</summary>
        [Fact]
        public void AClaimThatEndsEarly_LeavesWhatALateWorkerReadForTheNext()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            // A Viper Ultimate, whose claim is spread over several passes.
            var mouse = bench.Add(0x007A);
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            try
            {
                bench.StartAndWalk();
                bench.Light(mouse, Red);
                bench.Light(keyboard, Red);
                bench.Link(mouse, keyboard);
                bool Painted(long after)
                {
                    bench.Advance(60);
                    bench.Passes();
                    return mouse.Sent(after, r => Is(r, 0x0F, 0x02)).Length > 0;
                }
                Assert.True(SpinWait.SpinUntil(() => Painted(0), 30000), "the first run painted the mouse");
                var read = Lines(bench.Backend.Claimed.First(entry => entry.Path == mouse.Key).HandBack);
                Assert.NotEmpty(read);

                int held = 0;
                keyboard.Answer = request =>
                {
                    if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                    return Answers(request);
                };
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(keyboard, Blue);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the first worker is held");
                bench.Backend.Dispose();

                // The switch goes off with the next run's claim under way.
                long mark = bench.Mark;
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                Assert.True(SpinWait.SpinUntil(() => mouse.Sent(mark).Length > 0, 30000), "the next run began its claim");
                PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
                for (int step = 0; step < 8; step++)
                {
                    bench.Advance(60);
                    bench.Passes();
                }
                Assert.Empty(mouse.Sent(mark, r => Is(r, 0x0F, 0x02)));

                bench.Light(mouse, Red);
                Assert.True(SpinWait.SpinUntil(() => Painted(mark), 30000), "the next run painted the mouse");
                Assert.Empty(mouse.Sent(mark, r => Is(r, 0x0F, 0x82)));
                Assert.Equal(read, Lines(bench.Backend.Claimed.First(entry => entry.Path == mouse.Key).HandBack));
            }
            finally
            {
                gate.Set();
            }
        }

        private static PeripheralPresence SynapseRuns()
            => new(false, false, true, false, false) { RazerSoftwareRunning = true };

        /// <summary>A Razer program takes the device from a unit at rest. What the run
        /// read is old from here on, and no later run takes it.</summary>
        [Fact]
        public void ARazerProgramThatTakesAUnitAtRest_EndsWhatTheRunRead()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.True(RazerDirectBackend.HandBackHeldForTest(mouse.Key));

            PeripheralOutputs.Presence = SynapseRuns();
            bench.Passes();
            Assert.False(mouse.Open);
            Assert.False(RazerDirectBackend.HandBackHeldForTest(mouse.Key));
        }

        /// <summary>The same for a unit that stopped answering before a write of the
        /// claim went through, with nothing owed.</summary>
        [Fact]
        public void ARazerProgramThatFindsAUnitAsleep_EndsWhatTheRunRead()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = request => Is(request, 0x0F, 0x02) ? Silence(request) : Answers(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            Assert.True(SpinWait.SpinUntil(() => bench.Unit(mouse)?.Asleep == true, 30000), "the mouse stopped answering");
            Assert.True(RazerDirectBackend.HandBackHeldForTest(mouse.Key));

            PeripheralOutputs.Presence = SynapseRuns();
            bench.Passes();
            Assert.False(RazerDirectBackend.HandBackHeldForTest(mouse.Key));
        }

        /// <summary>A worker a stop gave up on, its mouse's own lighting in its books, and
        /// a Razer program that has the mouse when the next run starts. What the program
        /// stores is the mouse's own lighting from then on, so the next run reads it once
        /// the program has stopped.</summary>
        [Fact]
        public void ARazerProgramBetweenTwoRuns_HasTheNextRunReadTheDevice()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            try
            {
                HoldTheFirstWorkerOnTheKeyboard(bench, mouse, keyboard, gate);
                long mark = bench.Mark;
                PeripheralOutputs.Presence = SynapseRuns();
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                bench.Passes();
                Assert.False(RazerDirectBackend.HandBackHeldForTest(mouse.Key));

                PeripheralOutputs.Presence = PeripheralPresence.None;
                Assert.True(SpinWait.SpinUntil(() => mouse.Sent(mark, r => Is(r, 0x0F, 0x82)).Length > 0, 30000),
                    "the next run read the mouse");
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>The same worker coming back once that program has stopped, with no
        /// row asking for the mouse, sends it nothing: its hand-back is older than what
        /// the program stored.</summary>
        [Fact]
        public void AWorkerThatComesBackAfterARazerProgram_SendsNothing()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            try
            {
                HoldTheFirstWorkerOnTheKeyboard(bench, mouse, keyboard, gate);
                var first = Assert.Single(bench.Workers);
                PeripheralOutputs.Presence = SynapseRuns();
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                bench.Passes();

                PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
                PeripheralOutputs.Presence = PeripheralPresence.None;
                bench.Passes();
                long mark = bench.Mark;
                gate.Set();
                Assert.True(first.Join(30000), "the first worker ended");
                Assert.Empty(mouse.Sent(mark));
            }
            finally
            {
                gate.Set();
            }
        }

        private static void HoldTheFirstWorkerOnTheKeyboard(Bench bench, FakeRazer mouse, FakeRazer keyboard, ManualResetEventSlim gate)
        {
            bench.StartAndWalk();
            bench.Light(mouse, Red);
            bench.Light(keyboard, Red);
            bench.Link(mouse, keyboard);
            bench.Passes();
            Assert.True(RazerDirectBackend.HandBackHeldForTest(mouse.Key));

            int held = 0;
            keyboard.Answer = request =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                return Answers(request);
            };
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(keyboard, Blue);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the first worker is held");
            bench.Backend.Dispose();
        }

        /// <summary>A collection that leaves the walk takes what the run read of it along.</summary>
        [Fact]
        public void AUnitWhoseCollectionLeaves_EndsWhatTheRunRead()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.True(RazerDirectBackend.HandBackHeldForTest(mouse.Key));

            mouse.Present = false;
            bench.Walk();
            bench.Passes();
            Assert.False(RazerDirectBackend.HandBackHeldForTest(mouse.Key));
        }

        /// <summary>The same for a mouse its dock no longer lists.</summary>
        [Fact]
        public void APairedMouseItsDockNoLongerLists_EndsWhatTheRunRead()
        {
            PeripheralLightMaps.ResetForTest();
            try
            {
                using var bench = new Bench(reassertMs: int.MaxValue);
                var dock = bench.Add(MouseDockPro);
                var listed = new byte[] { 2, 0x01, 0x00, 0xAB, 0x01, 0x00, 0xA4 };
                dock.Answer = request => Is(request, 0x00, 0xBF) ? Reply(request, P.StatusSuccess, listed) : Answers(request);
                bench.StartAndWalk();
                bench.Link(dock);
                bench.Light(dock, Red);
                bench.Passes();
                bench.Passes();

                string pairedKey = RazerDirectBackend.PairedKey(dock.Key, BasiliskV3ProWireless);
                var mouseRow = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks { Device = dock.Row, Lighting = new[] { dock.Path }, HasVendorProgram = true },
                    new DeviceLinks { Device = mouseRow, Lighting = new[] { new OutputPath(OutputFamily.RazerUnit, pairedKey) }, HasVendorProgram = true },
                }));
                PeripheralOutputs.SetLighting(mouseRow, slot: 1, player: 2, 0x11, 0x22, 0x33);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    bench.Advance(2);
                    bench.Passes(1);
                    return RazerDirectBackend.HandBackHeldForTest(pairedKey);
                }, 30000), "the run read the mouse");

                // Unpaired: the dock's next claim's list names nobody.
                listed = new byte[] { 1, 0x00, 0xFF, 0xFF };
                PeripheralOutputs.ReleaseLighting(dock.Row, 0);
                bench.Passes();
                bench.Light(dock, Red);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    bench.Advance(2);
                    bench.Passes(1);
                    return bench.Backend.Snapshot.Units.Count == 1;
                }, 30000), "the dock's list dropped the mouse");
                Assert.Equal(new[] { dock.Key }, bench.Backend.Snapshot.Units.Select(u => u.Key));
                Assert.False(RazerDirectBackend.HandBackHeldForTest(pairedKey));
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>A Razer program that starts in the middle of a paint ends what the run
        /// read, as at rest.</summary>
        [Fact]
        public void ARazerProgramThatStartsInAPaint_EndsWhatTheRunRead()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.True(RazerDirectBackend.HandBackHeldForTest(mouse.Key));

            mouse.Answer = request =>
            {
                if (Is(request, 0x0F, 0x02)) PeripheralOutputs.Presence = SynapseRuns();
                return Answers(request);
            };
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Blue);
            bench.Passes();
            Assert.False(mouse.Open);
            Assert.False(RazerDirectBackend.HandBackHeldForTest(mouse.Key));
        }

        /// <summary>The same on a worker that comes out of a hung read into its stop, with
        /// a program on the device by then. Nothing goes out to it, and nothing it read
        /// stays for the next run.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AStopWithAUnitLeftToAProgram_EndsWhatTheRunRead(bool razerProgram)
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            try
            {
                bench.StartAndWalk();
                bench.Light(mouse, Red);
                bench.Light(keyboard, Red);
                bench.Link(mouse, keyboard);
                bench.Passes();
                Assert.True(RazerDirectBackend.HandBackHeldForTest(mouse.Key));

                int held = 0;
                keyboard.Answer = request =>
                {
                    if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                    return Answers(request);
                };
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(keyboard, Blue);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the worker is held");
                bench.Backend.Dispose();

                long mark = bench.Mark;
                if (razerProgram) PeripheralOutputs.Presence = SynapseRuns();
                else
                    PeripheralOutputs.PublishLinks(new LinkTable(new[]
                    {
                        new DeviceLinks
                        {
                            Device = mouse.Row,
                            Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                            TakenByOpenRgb = new[] { mouse.Path },
                        },
                    }));
                gate.Set();
                Assert.True(Assert.Single(bench.Workers).Join(30000), "the worker ended");
                Assert.Empty(mouse.Sent(mark));
                Assert.False(RazerDirectBackend.HandBackHeldForTest(mouse.Key));
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>The run that took a late worker's hand-back owes it from that moment,
        /// though no write of its own went through: that worker has no more say on the
        /// device, and the device shows its picture.</summary>
        [Fact]
        public void TheRunThatTookALateHandBack_OwesItWithNoWriteOfItsOwn()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            try
            {
                bench.StartAndWalk();
                bench.Light(mouse, Red);
                bench.Light(keyboard, Red);
                bench.Link(mouse, keyboard);
                bench.Passes();
                var read = Lines(bench.Backend.Claimed.First(entry => entry.Path == mouse.Key).HandBack);

                int held = 0;
                keyboard.Answer = request =>
                {
                    if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                    return Answers(request);
                };
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(keyboard, Blue);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the first worker is held");
                bench.Backend.Dispose();

                // Its paints go unanswered, in the next run.
                mouse.Answer = request => Is(request, 0x0F, 0x02) ? Silence(request) : Answers(request);
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                Assert.True(SpinWait.SpinUntil(() => bench.Unit(mouse)?.Asleep == true, 30000), "the mouse stopped answering the next run");
                var owed = bench.Backend.Claimed.First(entry => entry.Path == mouse.Key);
                Assert.Equal(read, Lines(owed.HandBack));
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A worker that outlived its run pays nothing it is owed
        /// on a collection its successor has claimed since. The successor
        /// read that device and holds it, so what it shows is the
        /// successor's to say, and the record keeps the successor's claim.</summary>
        [Fact]
        public void AWorkerThatOutlivedItsRun_PaysNothingOwed_OnACollectionItsSuccessorClaimed()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            using var gate = new ManualResetEventSlim(false);
            try
            {
                bench.StartAndWalk();
                bench.Light(mouse, Red);
                bench.Light(keyboard, Red);
                bench.Link(mouse, keyboard);
                bench.Passes();
                mouse.Answer = Silence;
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(mouse, Green);
                bench.Passes();
                Assert.True(bench.Unit(mouse).Asleep);
                Assert.Equal(2, bench.Backend.Claimed.Count);
                var first = Assert.Single(bench.Workers);

                // The first worker is held in a read of the keyboard, the
                // one time, and its Stop gives up on it.
                int held = 0;
                keyboard.Answer = request =>
                {
                    if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                    return Answers(request);
                };
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(keyboard, Blue);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the first worker is held in a read");
                bench.Backend.Dispose();

                // The mouse answers again, and the next run claims it and
                // paints it, on a backend of its own, as each engine start
                // has it.
                mouse.Answer = Answers;
                long mark = bench.Mark;
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                Assert.True(SpinWait.SpinUntil(
                    () => mouse.Sent(mark, r => Is(r, 0x0F, 0x02) && r[P.ArgumentsAt + 2] == 0x01).Length == 2, 30000),
                    "the second worker painted the mouse");
                bench.Passes();
                Assert.True(first.IsAlive);
                Assert.Contains(bench.Backend.Claimed, entry => entry.Path == mouse.Key);

                // The first worker comes back and ends.
                gate.Set();
                Assert.True(first.Join(30000));
                Assert.Empty(bench.OneShots);
                Assert.Contains(bench.Backend.Claimed, entry => entry.Path == mouse.Key);
                Assert.True(mouse.Open);
            }
            finally
            {
                gate.Set();
            }
        }

        // ─────────────────────────────────────────────
        //  A Razer program
        // ─────────────────────────────────────────────

        /// <summary>Synapse starts while a unit is lit. From the pass that
        /// sees it running nothing more is sent, a hand-back least of all,
        /// since it would land on the effect Synapse applies. The claim is
        /// forgotten and the handle closed, whether or not the link table
        /// has withdrawn the path yet. A path that goes for any other
        /// reason, and a claim that ends while its path is linked, are
        /// handed back.</summary>
        [Fact]
        public void WhenARazerProgramStarts_NothingMoreIsSent_AndEveryOtherReleaseHandsBack()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            var synapseRuns = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);

            // The link pass publishes the presence, then the table without
            // the unit's path: the row is lit through its Chroma category.
            long mark = bench.Mark;
            PeripheralOutputs.Presence = synapseRuns;
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks
                {
                    Device = mouse.Row, DirectWhenClosed = true, HasVendorProgram = true,
                    Lighting = new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") },
                },
            }));
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            Assert.Equal(mark, bench.Mark);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(bench.Unit(mouse).Asleep);

            // Synapse quits and the path comes back: the standing claim is
            // claimed and painted again, from the claim read on.
            PeripheralOutputs.Presence = PeripheralPresence.None;
            bench.Link(mouse);
            bench.Passes();
            Assert.Equal(6, mouse.Sent(mark).Length);
            Assert.Single(bench.Backend.Claimed);

            // The presence alone, with the path still in the table and
            // nothing to send: the handle is closed and the claim
            // forgotten at once, with no report.
            mark = bench.Mark;
            PeripheralOutputs.Presence = synapseRuns;
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            // A new color changes nothing while the program runs.
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Blue);
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            // And nothing is claimed while it runs.
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            Assert.Equal(2, mouse.Opens);

            // No program runs. The path goes, a row that went offline:
            // handed back.
            PeripheralOutputs.Presence = PeripheralPresence.None;
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);
            mark = bench.Mark;
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>OpenRGB holds the mouse (#557): the linker closes the
        /// unit's path and names it taken, and from that pass nothing more
        /// is sent, a hand-back least of all, as when Synapse starts. The
        /// same table without the mark hands the unit back.</summary>
        [Fact]
        public void APathOpenRgbTakes_IsLetGoWithNothingSent()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);
            LinkTable ThroughOpenRgb(bool taken) => new(new[]
            {
                new DeviceLinks
                {
                    Device = mouse.Row,
                    Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                    TakenByOpenRgb = taken ? new[] { mouse.Path } : Array.Empty<OutputPath>(),
                },
            });

            long mark = bench.Mark;
            PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: true));
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(bench.Unit(mouse).Asleep);

            // OpenRGB lets go: claimed and painted again. The path then
            // goes with no mark: handed back.
            bench.Link(mouse);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);
            mark = bench.Mark;
            PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: false));
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>A Razer program that starts in the middle of a paint
        /// gets the device at once: the reports of that paint that were not
        /// sent yet stay unsent.</summary>
        [Fact]
        public void ARazerProgramThatStartsInTheMiddleOfAPaint_StopsItThere()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var keyboard = bench.Add(BlackWidowChroma);
            bench.StartAndWalk();
            bench.Link(keyboard);
            bench.Light(keyboard, Red);
            bench.Passes();
            long mark = bench.Mark;

            // Synapse appears as the device takes the third row of a frame.
            keyboard.Answer = request =>
            {
                if (Is(request, 0x03, 0x0B) && request[P.ArgumentsAt + 1] == 0x02)
                    PeripheralOutputs.Presence = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
                return Answers(request);
            };
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(keyboard, Blue);
            bench.Passes();
            Assert.Equal(Lines(P.StandardRow(0xFF, 0, 22, Blue), P.StandardRow(0xFF, 1, 22, Blue), P.StandardRow(0xFF, 2, 22, Blue)),
                keyboard.Sent(mark));
            Assert.False(keyboard.Open);
            Assert.Empty(bench.Backend.Claimed);
            // Every take of the lock was released.
            Assert.Equal(bench.Guard.Journal.Count(e => e.What == "take"), bench.Guard.Journal.Count(e => e.What == "release"));
            bench.Verify();
        }

        /// <summary>A Razer program that starts while a slow device still
        /// owes a reply: the reply is never read, the lock taken for the
        /// request is released, and the handle is closed.</summary>
        [Fact]
        public void ARazerProgramThatStartsWhileAReplyIsOwed_LeavesTheReplyUnread()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            // A Viper Ultimate, whose reply takes 59.9 ms.
            var mouse = bench.Add(0x007A);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            Assert.Equal(new[] { "take" }, bench.Guard.Journal.Select(e => e.What));

            PeripheralOutputs.Presence = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            bench.Advance(60);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            Assert.Equal(new[] { "take", "release" }, bench.Guard.Journal.Select(e => e.What));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
        }

        // ─────────────────────────────────────────────
        //  The lock
        // ─────────────────────────────────────────────

        /// <summary>The lock is taken before each request and released
        /// after the read of its reply, on the worker's thread. A claim
        /// waits 250 ms for it and a paint 25.</summary>
        [Fact]
        public void TheLockIsHeldAroundEachRequestAndItsReply()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Blue);
            bench.Passes();

            var events = bench.Guard.Journal.Select(e => (e.What, e.Tick))
                .Concat(mouse.Calls.Select(c => (What: c.Kind, c.Tick))).OrderBy(e => e.Tick).Select(e => e.What).ToArray();
            Assert.Equal(Enumerable.Repeat(new[] { "take", "set", "get", "release" }, 8).SelectMany(p => p), events);
            Assert.Single(bench.Guard.Journal.Select(e => e.Thread).Distinct());
            // The claim's six requests, then the two of a paint.
            Assert.Equal(Enumerable.Repeat(RazerGuard.CommandWaitMs, 6).Concat(Enumerable.Repeat(RazerGuard.PaintWaitMs, 2)),
                bench.Guard.Journal.Where(e => e.What == "take").Select(e => e.WaitMs));
        }

        /// <summary>A lock another program keeps skips a paint: nothing is
        /// sent, nothing is recorded as painted, and the color that stands
        /// when the lock is free again is the one that goes out. A
        /// hand-back is never skipped. It waits for the lock once and goes
        /// out without it.</summary>
        [Fact]
        public void ABusyLockSkipsAPaint_AndNeverAHandBack()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();

            long mark = bench.Mark;
            bench.Guard.Busy = true;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            var refusals = bench.Guard.Journal.Where(e => e.Tick > mark).ToList();
            Assert.NotEmpty(refusals);
            Assert.All(refusals, e => Assert.Equal(("busy", RazerGuard.PaintWaitMs), (e.What, e.WaitMs)));
            Assert.True(mouse.Open);

            // A skipped paint is tried again no sooner than the interval.
            int tries = refusals.Count;
            bench.Passes();
            Assert.Equal(tries, bench.Guard.Journal.Count(e => e.Tick > mark));

            // Free again: the color that stands now.
            bench.Guard.Busy = false;
            bench.Light(mouse, Blue);
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Blue), P.ExtendedStatic(0x3F, 0x01, Blue)), mouse.Sent(mark));

            // The hand-back with the lock kept: both reports go out, the
            // first after the one wait.
            mark = bench.Mark;
            bench.Guard.Busy = true;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            Assert.Equal(new[] { ("busy", RazerGuard.CommandWaitMs), ("busy", 0) },
                bench.Guard.Journal.Where(e => e.Tick > mark).Select(e => (e.What, e.WaitMs)));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>A hand-back waits for the lock once. A device that does
        /// not answer is sent its report five times, and only the first
        /// attempt waits: the four after it ask for the lock with no wait.</summary>
        [Fact]
        public void AHandBackToASilentDevice_WaitsForABusyLockOnce()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();

            long mark = bench.Mark;
            bench.Guard.Busy = true;
            mouse.Answer = Silence;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(Enumerable.Repeat(RazerDirectProtocolTests.Line(P.ExtendedSpectrum(0x3F, 0x04)), 5), mouse.Sent(mark));
            Assert.Equal(new[] { ("busy", RazerGuard.CommandWaitMs), ("busy", 0), ("busy", 0), ("busy", 0), ("busy", 0) },
                bench.Guard.Journal.Where(e => e.Tick > mark).Select(e => (e.What, e.WaitMs)));
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.False(mouse.Open);
        }

        /// <summary>A hand-back the device does not take is a failed call
        /// like any other. A handle that fails lists the unit asleep and
        /// asks for one walk, a 04 lists it asleep with no walk, and
        /// neither is logged as a hand-back. The hand-back stays owed, on
        /// the crash path's record.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHandBackTheDeviceDoesNotTake_ListsTheUnitAsleep(bool handleFails)
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);
            int changes = Volatile.Read(ref bench.Changes);

            string diag = DiagMark.Write();
            if (handleFails) mouse.FailWrites = true;
            else mouse.Answer = request => Reply(request, P.StatusTimeout);
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.False(mouse.Open);
            Assert.Equal(MouseHandBack, Lines(Assert.Single(bench.Backend.Claimed).HandBack));
            Assert.Equal(handleFails ? 1 : 0, Volatile.Read(ref bench.Walks));
            Assert.Equal(changes + 1, Volatile.Read(ref bench.Changes));
            Assert.Equal(0, DiagMark.LinesSince(diag, "lighting handed back"));
            Assert.Equal(1, DiagMark.LinesSince(diag, handleFails ? "handle failed, closing it" : "does not answer, asking again later"));
            bench.Verify();
        }

        /// <summary>A claim that cannot get the lock sends nothing and is
        /// made again five seconds later. The unit is no less awake for it,
        /// and no walk is asked for.</summary>
        [Fact]
        public void AClaimTheLockKeepsOut_IsMadeAgainLater()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            bench.Guard.Busy = true;
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Empty(mouse.Sent());
            Assert.Equal(("busy", RazerGuard.CommandWaitMs), (bench.Guard.Journal.Single().What, bench.Guard.Journal.Single().WaitMs));
            Assert.False(mouse.Open);
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(0, Volatile.Read(ref bench.Walks));

            bench.Guard.Busy = false;
            bench.Advance(RazerDirectBackend.RetryMs - 1);
            bench.Passes();
            Assert.Empty(mouse.Sent());
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(6, mouse.Sent().Length);
            bench.Verify();
        }

        /// <summary>A claim whose reads went through and whose first color
        /// the lock keeps out has written nothing: reads change nothing on
        /// a device. The handle is closed with nothing handed back, the
        /// unit is no less awake, and the claim is made again five seconds
        /// later.</summary>
        [Fact]
        public void AClaimWhoseFirstColorTheLockKeepsOut_SendsNothingBack_AndIsMadeAgainLater()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            // Another program takes the lock as the device answers the
            // claim's last read.
            mouse.Answer = request =>
            {
                if (Is(request, 0x0F, 0x82) && request[P.ArgumentsAt + 1] == 0x01) bench.Guard.Busy = true;
                return Answers(request);
            };
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(
                P.ExtendedBrightnessRead(0x3F, 0x04), P.RegionListRead(0x3F),
                P.StoredEffectRead(0x3F, 0x04), P.StoredEffectRead(0x3F, 0x01)), mouse.Sent());
            Assert.Equal(("busy", RazerGuard.CommandWaitMs), (bench.Guard.Journal.Last().What, bench.Guard.Journal.Last().WaitMs));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Equal(0, Volatile.Read(ref bench.Walks));

            long mark = bench.Mark;
            mouse.Answer = Answers;
            bench.Guard.Busy = false;
            bench.Advance(RazerDirectBackend.RetryMs - 1);
            bench.Passes();
            Assert.Equal(mark, bench.Mark);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)),
                mouse.Sent(mark, r => Is(r, 0x0F, 0x02)));
            bench.Verify();
        }

        /// <summary>The worker on a lock that is a real mutex, one of this
        /// test's own with no name. A Windows mutex belongs to the thread
        /// that took it: the worker holds it from a request to the read of
        /// its reply, across passes, and no other thread can have it
        /// meanwhile. Once the claim is through, every take has been
        /// released on the worker's thread and the mutex is free.</summary>
        [Fact]
        public void OnARealMutex_TheWorkerHoldsTheLockFromARequestToItsReply_AndLeavesItFree()
        {
            using var mutex = new RazerGuard((string)null);
            using var bench = new Bench(reassertMs: int.MaxValue, guard: mutex);
            // A Viper Ultimate, whose reply is read a pass after its request.
            var mouse = bench.Add(0x007A);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            Assert.False(mutex.Unavailable);
            Assert.False(mutex.Take(0));

            // The claim is four requests, each read 60 ms after it went out.
            for (int step = 0; step < 8 && mouse.Calls.Count < 8; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            Assert.Equal(8, mouse.Calls.Count);
            Assert.Single(bench.Backend.Claimed);
            Assert.True(mutex.Take(0));
            mutex.Release();
            bench.Verify();
        }

        /// <summary>The worker's only two ways to a device are the shared
        /// HID helpers, which refuse in the test host: the opener and the
        /// crash path's one-shot writer default to them and to nothing
        /// else, and the file holds no other call that opens a handle.
        /// Read from the source, so a default that went around the helpers
        /// fails here whatever a path with no device behind it would do.</summary>
        [Fact]
        public void TheWorkersTwoWaysToADevice_AreTheSharedHelpersTheTestHostRefuses()
        {
            Assert.True(RealHidAccess.Refused);
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "RazerDirectBackend.cs");
            Assert.Contains("_open = open ?? (path => HidReportChannel.Open(path));", source);
            Assert.Contains("_setFeatureOnce = setFeatureOnce ?? HidReportChannel.SetFeatureOnce;", source);
            Assert.Equal(2, source.Split("HidReportChannel.").Length - 1);
            foreach (string call in new[] { "CreateFile", "DllImport", "SafeFileHandle", "FileStream" })
                Assert.DoesNotContain(call, source);
        }

        /// <summary>On the opener and the one-shot writer the app gives it,
        /// a unit whose collection does not open is listed asleep, a walk
        /// is asked for, and nothing is sent or recorded. The path names no
        /// device on any PC, so it would not open with the fence down
        /// either.</summary>
        [Fact]
        public void OnTheAppsOwnOpener_AUnitThatDoesNotOpen_IsListedAsleep_AndNothingIsWritten()
        {
            Assert.True(RealHidAccess.Refused);
            var container = Guid.NewGuid();
            var collection = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_1532&pid_0084&mi_00#padforge-test#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                VendorId = 0x1532, ProductId = DeathAdderV2, UsagePage = 0x01, Usage = 0x02, InterfaceNumber = 0,
                FeatureReportLength = 91,
            };
            Assert.Null(HidReportChannel.Open(collection.Path));
            Assert.False(HidReportChannel.SetFeatureOnce(collection.Path, P.ExtendedSpectrum(0x3F, 0x04), RazerDirectBackend.OneShotMs));

            using var bench = new Bench();
            long now = 1_000_000;
            int walks = 0;
            using var backend = new RazerDirectBackend(containerOf: _ => container, ticks: () => Volatile.Read(ref now),
                wait: _ => { }, guard: bench.Guard, idleTickMs: 2);
            backend.RescanRequested += () => Interlocked.Increment(ref walks);
            backend.Start();
            backend.Rescan(new[] { collection });
            bench.Passes(backend: backend);
            var unit = Assert.Single(backend.Snapshot.Units);
            Assert.False(unit.Asleep);

            var row = Guid.NewGuid();
            PeripheralOutputs.SetLighting(row, 0, 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = row, Lighting = new[] { unit.Path } } }));
            bench.Passes(backend: backend);
            // The open failed: asleep, a walk asked for, and nothing sent
            // or recorded, so no lock was asked for either.
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Equal(1, Volatile.Read(ref walks));
            Assert.Empty(backend.Claimed);
            Assert.Empty(bench.Guard.Journal);
        }

        // ─────────────────────────────────────────────
        //  Set Chroma Color
        // ─────────────────────────────────────────────

        /// <summary>Set Chroma Color paints a Razer device on its virtual
        /// controller whether or not the device's Lighting tab takes
        /// control. With no claim behind it the macro claims the unit for
        /// its window and hands it back after. Over a claim it shows for
        /// its window and the claim's color returns. Two controllers'
        /// macros on one device: the smaller displayed number rules.</summary>
        [Fact]
        public void SetChromaColor_BeatsTheClaim_AndClaimsWithTheSwitchOff()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.OnSlot[2] = new HashSet<Guid> { mouse.Row };
            bench.OnSlot[5] = new HashSet<Guid> { mouse.Row };
            bench.OnSlot[7] = new HashSet<Guid> { Guid.NewGuid() };
            // Slot 5 shows Virtual Controller 1 and slot 2 shows 2.
            bench.Shown[5] = 1;
            bench.Shown[2] = 2;
            bench.StartAndWalk();
            bench.Link(mouse);
            string[] Painted(long after) => mouse.Sent(after, r => Is(r, 0x0F, 0x02) && r[P.ArgumentsAt + 2] == 0x01);

            // A macro on a controller that does not hold the device.
            PeripheralOutputs.AssertChromaMacro(7, 9, 9, 9, bench.Now);
            bench.Passes();
            Assert.Equal(0, mouse.Opens);

            // The switch is off. The macro claims the unit.
            PeripheralOutputs.AssertChromaMacro(2, 0x0A, 0x0B, 0x0C, bench.Now);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, 0x0A0B0C), P.ExtendedStatic(0x3F, 0x01, 0x0A0B0C)), Painted(0));
            Assert.Single(bench.Backend.Claimed);

            // Its window runs out: handed back.
            long mark = bench.Mark;
            bench.Advance(PeripheralOutputs.MacroAssertWindowMs);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);

            // The switch is on, red. A macro shows over it.
            bench.Light(mouse, Red);
            bench.Passes();
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            PeripheralOutputs.AssertChromaMacro(2, 0x0A, 0x0B, 0x0C, bench.Now);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, 0x0A0B0C), P.ExtendedStatic(0x3F, 0x01, 0x0A0B0C)), Painted(mark));

            // The controller with the smaller displayed number rules.
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            PeripheralOutputs.AssertChromaMacro(2, 0x0A, 0x0B, 0x0C, bench.Now);
            PeripheralOutputs.AssertChromaMacro(5, 0x01, 0x02, 0x03, bench.Now);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, 0x010203), P.ExtendedStatic(0x3F, 0x01, 0x010203)), Painted(mark));

            // Both run out: the claim's color returns, with no hand-back in
            // between.
            mark = bench.Mark;
            bench.Advance(PeripheralOutputs.MacroAssertWindowMs + 1);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)), mouse.Sent(mark));
            Assert.True(mouse.Open);
            bench.Verify();
        }

        /// <summary>Nothing wakes the worker when a Set Chroma Color stops
        /// being asserted, so while one is live its wait ends at the
        /// macro's lapse. The worker's own tick here is ten minutes, and
        /// the test reads the wait the worker chose.</summary>
        [Fact]
        public void TheWorkersWaitEndsAtALiveMacrosLapse()
        {
            using var bench = new Bench(idleTickMs: 600_000, reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.OnSlot[0] = new HashSet<Guid> { mouse.Row };
            bench.Backend.Start();
            bench.Walk();
            bench.Link(mouse);
            Assert.True(SpinWait.SpinUntil(() => bench.Backend.WaitMs == 600_000, 30000), "the worker went idle");

            PeripheralOutputs.AssertChromaMacro(0, 1, 2, 3, bench.Now);
            Assert.True(SpinWait.SpinUntil(() => mouse.Sent(0, r => Is(r, 0x0F, 0x02)).Length == 2, 30000), "the macro color was painted");
            Assert.Equal(PeripheralOutputs.MacroAssertWindowMs + 1,
                WorkerCadence.WaitAfterAFullPass(() => bench.Backend.PassCount, () => bench.Backend.WaitMs));
            bench.Advance(PeripheralOutputs.MacroAssertWindowMs);
            Assert.Equal(1, WorkerCadence.WaitAfterAFullPass(() => bench.Backend.PassCount, () => bench.Backend.WaitMs));
            Assert.True(mouse.Open);
            bench.Advance(1);
            Assert.True(SpinWait.SpinUntil(() => !mouse.Open, 30000), "the unit was let go at the lapse");
        }

        // ─────────────────────────────────────────────
        //  What a family reads, paints and gives back
        // ─────────────────────────────────────────────

        /// <summary>The device names its lit regions, and one the table does
        /// not paint gets a report of its own. Each painted LED is asked
        /// for its stored effect, and an answer that passes is written back
        /// at release, argument for argument and not stored, where an LED
        /// with no answer gets its spectrum effect.</summary>
        [Fact]
        public void TheRegionsADeviceNames_ArePainted_AndWhatItStoredIsWrittenBack()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = request =>
            {
                // Scroll wheel, logo and an underglow strip.
                if (Is(request, 0x0F, 0x80))
                    return Reply(request, P.StatusSuccess, 0x01, 0x19, 0x03, 0x01, 0x01, 0x04, 0x19, 0x03, 0x01, 0x01, 0x0A, 0x19, 0x03, 0x01, 0x0B);
                if (Is(request, 0x0F, 0x82))
                    return request[P.ArgumentsAt + 1] switch
                    {
                        // The logo breathes between green and red.
                        0x04 => Reply(request, P.StatusSuccess, 0x01, 0x04, 0x02, 0x02, 0x00, 0x02, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00),
                        // The strip runs the spectrum.
                        0x0A => Reply(request, P.StatusSuccess, 0x01, 0x0A, 0x03, 0x00, 0x00, 0x00),
                        // The wheel answers for another LED: not believed.
                        _ => Reply(request, P.StatusSuccess, 0x01, 0x05, 0x01, 0x00, 0x00, 0x01, 0x11, 0x22, 0x33),
                    };
                return Answers(request);
            };
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(
                P.ExtendedBrightnessRead(0x3F, 0x04), P.RegionListRead(0x3F),
                P.StoredEffectRead(0x3F, 0x04), P.StoredEffectRead(0x3F, 0x01), P.StoredEffectRead(0x3F, 0x0A),
                P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red), P.ExtendedStatic(0x3F, 0x0A, Red)), mouse.Sent());

            string[] handBack =
            {
                "3F 0F/02 size 0C [00 04 02 02 00 02 00 FF 00 FF 00 00]",
                "3F 0F/02 size 06 [00 0A 03 00 00 00]",
                "3F 0F/02 size 06 [00 01 03 00 00 00]",
            };
            Assert.Equal(handBack, Lines(bench.Backend.Claimed.Single().HandBack));
            long mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(handBack, mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>A classic LED says what it shows: its state, color and
        /// effect are read before the claim and written back at release as
        /// read. An LED whose read fails gets the spectrum effect. The
        /// claim turns each LED on, and an update is a color and an effect
        /// per LED. The reads go out under FF, the ID openrazer reads this
        /// mouse under, and the writes under 3F, the ID it paints it under.</summary>
        [Fact]
        public void AClassicLed_IsReadClaimedPainted_AndGivenBackAsItWas()
        {
            using var bench = new Bench();
            var mouse = bench.Add(0x0043);
            mouse.Answer = request =>
            {
                byte led = request[P.ArgumentsAt + 1];
                if (led == 0x01 && Is(request, 0x03, 0x80)) return Reply(request, P.StatusSuccess, 0x01, led, 0x01);
                if (led == 0x01 && Is(request, 0x03, 0x81)) return Reply(request, P.StatusSuccess, 0x01, led, 0xAA, 0xBB, 0xCC);
                if (led == 0x01 && Is(request, 0x03, 0x82)) return Reply(request, P.StatusSuccess, 0x01, led, 0x02);
                // The logo's color read is refused.
                if (led == 0x04 && Is(request, 0x03, 0x81)) return Reply(request, P.StatusNotSupported);
                return Answers(request);
            };
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(
                P.StandardBrightnessRead(0xFF, 0x01),
                P.ClassicStateRead(0xFF, 0x01), P.ClassicColorRead(0xFF, 0x01), P.ClassicEffectRead(0xFF, 0x01),
                P.ClassicStateRead(0xFF, 0x04), P.ClassicColorRead(0xFF, 0x04), P.ClassicEffectRead(0xFF, 0x04),
                P.ClassicState(0x3F, 0x01, true), P.ClassicState(0x3F, 0x04, true),
                P.ClassicColor(0x3F, 0x01, Red), P.ClassicEffect(0x3F, 0x01, 0x00),
                P.ClassicColor(0x3F, 0x04, Red), P.ClassicEffect(0x3F, 0x04, 0x00)), mouse.Sent());

            long mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(new[]
            {
                "3F 03/01 size 05 [00 01 AA BB CC]",
                "3F 03/02 size 03 [00 01 02]",
                "3F 03/00 size 03 [00 01 01]",
                "3F 03/02 size 03 [00 04 04]",
            }, mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>A keyboard painted by frame takes each color as its
        /// rows and the report that shows them. Its frame stores nothing
        /// and no report returns it to its own effect, so the release sends
        /// nothing and closes the handle.</summary>
        [Fact]
        public void AFramePaintedKeyboard_TakesItsRowsAndTheShowReport_AndIsSentNothingAtRelease()
        {
            using var bench = new Bench();
            var keyboard = bench.Add(BlackWidowChroma);
            bench.StartAndWalk();
            bench.Link(keyboard);
            bench.Light(keyboard, Red);
            bench.Passes();
            var frame = Enumerable.Range(0, 6).Select(r => P.StandardRow(0xFF, r, 22, Red)).Append(P.StandardShow(0xFF));
            Assert.Equal(Lines(new[] { P.StandardBrightnessRead(0xFF, 0x05), P.KeyboardInfoRead(0xFF) }.Concat(frame).ToArray()), keyboard.Sent());
            Assert.Empty(bench.Backend.Claimed.Single().HandBack);

            long mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(keyboard.Row, 0);
            bench.Passes();
            Assert.Empty(keyboard.Sent(mark));
            Assert.False(keyboard.Open);
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>A Naga painted by its one-row frame gets the mode report
        /// ahead of the row at the claim and at every re-assert, and the
        /// row alone for a new color. Its hand-back is the spectrum of its
        /// zones, since openrazer marks its whole-device one doubtful.</summary>
        [Fact]
        public void AOneRowFrameMouse_GetsItsModeReportAtTheClaimAndEachReassert()
        {
            using var bench = new Bench();
            var mouse = bench.Add(0x008F);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(
                P.ExtendedBrightnessRead(0x1F, 0x00), P.StoredEffectRead(0x1F, 0x00),
                P.ExtendedFrameMode(0x1F), P.ExtendedFrameRow(0x1F, 3, Red)), mouse.Sent());

            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Blue);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedFrameRow(0x1F, 3, Blue)), mouse.Sent(mark));

            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.ReassertMs);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedFrameMode(0x1F), P.ExtendedFrameRow(0x1F, 3, Blue)), mouse.Sent(mark));

            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x1F, 0x04), P.ExtendedSpectrum(0x1F, 0x01)), mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>A laptop keyboard goes back to its own lighting when the
        /// host goes quiet, so its claim read is sent whenever 2500 ms pass
        /// with nothing else, and its frame again every five seconds. The
        /// release sends it nothing.</summary>
        [Fact]
        public void ALaptop_IsKeptAwakeByItsClaimRead_AndSentNothingAtRelease()
        {
            using var bench = new Bench();
            // A Blade 16 2025: six rows of 17.
            var laptop = bench.Add(0x02C6);
            bench.StartAndWalk();
            bench.Link(laptop);
            bench.Light(laptop, Red);
            bench.Passes();
            Assert.Equal(8, laptop.Sent().Length);
            Assert.Equal(RazerDirectProtocolTests.Line(P.LaptopBrightnessRead(0xFF)), laptop.Sent()[0]);

            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.KeepAliveMs - 1);
            bench.Passes();
            Assert.Empty(laptop.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.LaptopBrightnessRead(0xFF)), laptop.Sent(mark));

            // The frame again at five seconds, which is a report sent, so
            // the next read is 2500 ms after that.
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.KeepAliveMs);
            bench.Passes();
            var frame = Enumerable.Range(0, 6).Select(r => P.StandardRow(0xFF, r, 17, Red)).Append(P.StandardShow(0xFF));
            Assert.Equal(Lines(frame.ToArray()), laptop.Sent(mark));
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.KeepAliveMs - 1);
            bench.Passes();
            Assert.Empty(laptop.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.LaptopBrightnessRead(0xFF)), laptop.Sent(mark));

            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(laptop.Row, 0);
            bench.Passes();
            Assert.Empty(laptop.Sent(mark));
            Assert.False(laptop.Open);
            bench.Verify();
        }

        /// <summary>A product with only the effect report, which may be
        /// stored: nothing is opened until its first color has held for a
        /// second, a color that keeps moving writes nothing, a color that
        /// then holds for a second writes once, the five-second clock
        /// writes nothing, and a resume writes once. Its hand-back is the
        /// spectrum effect.</summary>
        [Fact]
        public void AProductPaintedByTheEffectReport_TakesAColorOnlyOnceItHasHeld()
        {
            using var bench = new Bench();
            // An Orochi on its cable.
            var mouse = bench.Add(0x0048);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(0, mouse.Opens);
            bench.Advance(P.EffectHoldMs - 1);
            bench.Passes();
            Assert.Equal(0, mouse.Opens);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.StandardBrightnessRead(0xFF, 0x05), P.StandardStatic(0xFF, Red)), mouse.Sent());

            // A color that changes every 100 ms for three seconds.
            long mark = bench.Mark;
            for (int i = 1; i <= 30; i++)
            {
                bench.Advance(100);
                bench.Light(mouse, 0x000100 * i);
                bench.Passes();
            }
            Assert.Empty(mouse.Sent(mark));

            // It holds: nothing at 999 ms, one report at 1000.
            bench.Advance(P.EffectHoldMs - 1);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.StandardStatic(0xFF, 0x001E00)), mouse.Sent(mark));

            // Nothing on the five-second clock.
            mark = bench.Mark;
            bench.Advance(3 * RazerDirectBackend.ReassertMs);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));

            // A color that comes and goes inside the second writes nothing.
            bench.Light(mouse, Blue);
            bench.Passes();
            bench.Advance(P.EffectHoldMs - 1);
            bench.Light(mouse, 0x001E00);
            bench.Passes();
            bench.Advance(P.EffectHoldMs);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));

            // One report when Windows resumes.
            bench.Backend.NoteResume();
            bench.Passes();
            Assert.Equal(Lines(P.StandardStatic(0xFF, 0x001E00)), mouse.Sent(mark));

            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(Lines(P.StandardSpectrum(0xFF)), mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>A claim that comes and goes inside the hold opens
        /// nothing and writes nothing to a product painted by the effect
        /// report: ten presses of a Set Chroma Color, 200 ms each, send an
        /// Orochi nothing else lights no report at all. The hold of a color
        /// that comes back starts when it comes back.</summary>
        [Fact]
        public void AClaimThatComesAndGoesInsideTheHold_OpensNothing_OnAProductPaintedByTheEffectReport()
        {
            using var bench = new Bench();
            var mouse = bench.Add(0x0048);
            bench.StartAndWalk();
            bench.Link(mouse);
            for (int press = 0; press < 10; press++)
            {
                bench.Light(mouse, Red);
                bench.Passes();
                bench.Advance(200);
                bench.Passes();
                PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
                bench.Passes();
                bench.Advance(200);
                bench.Passes();
            }
            Assert.Equal(0, mouse.Opens);
            Assert.Empty(bench.Backend.Claimed);

            // The same color once more, and held: out a second after it
            // came back, though it was first seen four seconds ago.
            bench.Light(mouse, Red);
            bench.Passes();
            bench.Advance(P.EffectHoldMs - 1);
            bench.Passes();
            Assert.Equal(0, mouse.Opens);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.StandardBrightnessRead(0xFF, 0x05), P.StandardStatic(0xFF, Red)), mouse.Sent());

            // Let go after a color went out: the spectrum effect, once.
            long mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(Lines(P.StandardSpectrum(0xFF)), mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>The second a color has to hold runs from when the
        /// worker saw the color. A pass reads its clock once, at its start,
        /// and can see a color that changed well after that, behind another
        /// device's replies: counted from the pass's start, the color would
        /// be written before it had held.</summary>
        [Fact]
        public void TheHoldRunsFromWhenTheColorWasSeen_NotFromThePassThatSawIt()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            // The keyboard is served first, and the Orochi after it.
            var keyboard = bench.Add(BlackWidowChroma);
            var mouse = bench.Add(0x0048);
            bench.StartAndWalk();
            bench.Light(keyboard, Red);
            bench.Light(mouse, Red);
            bench.Link(keyboard, mouse);
            bench.Passes();
            // The mouse's first color goes out once it has held.
            bench.Advance(P.EffectHoldMs);
            bench.Passes();
            Assert.Equal(2, bench.Backend.Claimed.Count);

            // While the keyboard answers the first report of its next
            // paint, 100 ms pass and the mouse's color changes.
            int armed = 1;
            keyboard.Answer = request =>
            {
                if (Interlocked.Exchange(ref armed, 0) == 1)
                {
                    bench.Advance(100);
                    bench.Light(mouse, Green);
                }
                return Answers(request);
            };
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(keyboard, Blue);
            bench.Passes();
            Assert.Equal(7, keyboard.Sent(mark).Length);
            Assert.Empty(mouse.Sent(mark));

            // 999 ms after the change, which is 1099 ms after that pass
            // began: nothing. One more: the color.
            bench.Advance(P.EffectHoldMs - 1);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.StandardStatic(0xFF, Green)), mouse.Sent(mark));
            bench.Verify();
        }

        // ─────────────────────────────────────────────
        //  A first paint that is refused
        // ─────────────────────────────────────────────

        /// <summary>A laptop that answers 05 to the first row of its frame
        /// is painted by its fallback, the effect report, which is held
        /// like any effect paint: the claim ends at the refusal and is made
        /// again under the fallback once the color has held for a second.
        /// Nothing is handed back for a fallback.</summary>
        [Fact]
        public void ARefusedFirstPaint_MovesOnToTheFallback_WhichIsHeld()
        {
            using var bench = new Bench();
            // A Blade Stealth 2019: OpenRGB's rows, openrazer's effect.
            var laptop = bench.Add(0x0239);
            laptop.Answer = request => Is(request, 0x03, 0x0B) ? Reply(request, P.StatusNotSupported) : Answers(request);
            bench.StartAndWalk();
            bench.Link(laptop);
            bench.Light(laptop, Red);
            bench.Passes();
            Assert.Equal(Lines(P.LaptopBrightnessRead(0xFF), P.StandardRow(0xFF, 0, 16, Red)), laptop.Sent());
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(laptop.Open);
            Assert.False(bench.Unit(laptop).Asleep);
            long refused = bench.Mark;
            bench.Advance(P.EffectHoldMs - 1);
            bench.Passes();
            Assert.Empty(laptop.Sent(refused));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.LaptopBrightnessRead(0xFF), P.StandardStatic(0xFF, Red)), laptop.Sent(refused));
            Assert.Empty(bench.Backend.Claimed.Single().HandBack);
            Assert.False(bench.Unit(laptop).Asleep);

            // Held from here on: a moving color writes nothing.
            long mark = bench.Mark;
            bench.Advance(100);
            bench.Light(laptop, Green);
            bench.Passes();
            bench.Advance(100);
            bench.Light(laptop, Blue);
            bench.Passes();
            Assert.Empty(laptop.Sent(mark, r => !IsRead(r)));
            bench.Advance(P.EffectHoldMs);
            bench.Passes();
            Assert.Equal(Lines(P.StandardStatic(0xFF, Blue)), laptop.Sent(mark, r => !IsRead(r)));

            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(laptop.Row, 0);
            bench.Passes();
            Assert.Empty(laptop.Sent(mark));
            Assert.False(laptop.Open);

            // The fallback that worked is where the next claim starts,
            // once its color has held.
            bench.Light(laptop, Red);
            bench.Passes();
            Assert.Empty(laptop.Sent(mark));
            bench.Advance(P.EffectHoldMs);
            bench.Passes();
            Assert.Equal(Lines(P.LaptopBrightnessRead(0xFF), P.StandardStatic(0xFF, Red)), laptop.Sent(mark));
            bench.Verify();
        }

        /// <summary>A Naga Chroma that refuses its own family is tried with
        /// OpenRGB's frame, and with the effect report if it refuses that
        /// too: the fallbacks in the order the table gives them. The effect
        /// report waits for a color that has held, so the claim is made
        /// again under it a second later. The claim read goes out under FF,
        /// openrazer's ID for it on this mouse.</summary>
        [Fact]
        public void TheFallbacksAreTriedInTheTablesOrder()
        {
            using var bench = new Bench();
            var mouse = bench.Add(0x0053);
            mouse.Answer = request => Is(request, 0x03, 0x0D) || Is(request, 0x03, 0x0C)
                ? Reply(request, P.StatusNotSupported) : Answers(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(
                P.StandardBrightnessRead(0xFF, 0x05),
                P.MouseExtendedStatic(0x3F, 0x05, Red),
                P.StandardLine(0x3F, 3, Red)), mouse.Sent());
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(mouse.Open);
            long mark = bench.Mark;
            bench.Advance(P.EffectHoldMs - 1);
            bench.Passes();
            Assert.Empty(mouse.Sent(mark));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(Lines(P.StandardBrightnessRead(0xFF, 0x05), P.StandardStatic(0x3F, Red)), mouse.Sent(mark));
            Assert.Single(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>A DeathAdder V2 that answers 05 to its first paint has
        /// no fallback: the LED 00 form OpenRGB names is another form of
        /// the same family that nobody has seen light every zone, and is
        /// never tried. The unit is released and leaves the list, since the
        /// table cannot paint it, until its collection has left the walk
        /// and come back.</summary>
        [Fact]
        public void AProductThatRefusesEveryPaint_LeavesTheList_UntilItIsPluggedInAgain()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = request => Is(request, 0x0F, 0x02) ? Reply(request, P.StatusNotSupported) : Answers(request);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            // One paint report, refused, and no report with LED 00.
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red)), mouse.Sent(0, r => Is(r, 0x0F, 0x02)));
            Assert.Empty(bench.Backend.Snapshot.Units);
            Assert.True(bench.Backend.Snapshot.Scanned);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);

            // It stays out for as long as it stays plugged in.
            long mark = bench.Mark;
            bench.Walk();
            bench.Advance(RazerDirectBackend.RetryMs);
            bench.Passes();
            Assert.Empty(bench.Backend.Snapshot.Units);
            Assert.Equal(mark, bench.Mark);

            // Unplugged and plugged in again, it gets another try.
            mouse.Present = false;
            bench.Walk();
            bench.Passes();
            mouse.Present = true;
            mouse.Answer = Answers;
            bench.Walk();
            bench.Passes();
            Assert.NotNull(bench.Unit(mouse));
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)),
                mouse.Sent(mark, r => Is(r, 0x0F, 0x02)));
            bench.Verify();
        }

        /// <summary>A region the device named itself and then refuses a
        /// color for is left to the device: the product keeps its paint,
        /// the region is asked no more, and nothing is handed back to it.
        /// The product is refused as a whole only where its first color
        /// report is.</summary>
        [Fact]
        public void ARegionTheDeviceNamed_ThatTakesNoColor_IsLeftAlone_AndTheProductStaysPainted()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            mouse.Answer = request =>
            {
                // Scroll wheel, logo and an underglow strip, which has no
                // static effect.
                if (Is(request, 0x0F, 0x80))
                    return Reply(request, P.StatusSuccess, 0x01, 0x19, 0x03, 0x01, 0x01, 0x04, 0x19, 0x03, 0x01, 0x01, 0x0A, 0x19, 0x03, 0x01, 0x0B);
                if (Is(request, 0x0F, 0x02) && request[P.ArgumentsAt + 1] == 0x0A && request[P.ArgumentsAt + 2] == 0x01)
                    return Reply(request, P.StatusNotSupported);
                return Answers(request);
            };
            string diag = DiagMark.Write();
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red), P.ExtendedStatic(0x3F, 0x0A, Red)),
                mouse.Sent(0, r => Is(r, 0x0F, 0x02)));
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.True(mouse.Open);
            // The record hands back the two regions that took the color.
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)),
                Lines(bench.Backend.Claimed.Single().HandBack));
            Assert.Equal(1, DiagMark.LinesSince(diag, "PERIPHERAL Razer 'Razer DeathAdder V2' takes no color on its region 0A"));
            Assert.Equal(0, DiagMark.LinesSince(diag, "refuses every paint"));

            // The next color goes to the two alone.
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Green), P.ExtendedStatic(0x3F, 0x01, Green)), mouse.Sent(mark));

            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(mouse.Row, 0);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>A product whose first color report was taken has taken
        /// its family. An LED of its row that then refuses is one report
        /// lost, in the first paint as in any later one: the unit stays
        /// listed and painted, and the LED is asked again with every paint.</summary>
        [Fact]
        public void AnLedThatRefusesBehindOneThatTookItsColor_IsOneReportLost_InTheFirstPaintToo()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            // The scroll wheel answers 05 to the static effect.
            mouse.Answer = request => Is(request, 0x0F, 0x02) && request[P.ArgumentsAt + 1] == 0x01 && request[P.ArgumentsAt + 2] == 0x01
                ? Reply(request, P.StatusNotSupported) : Answers(request);
            string diag = DiagMark.Write();
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)), mouse.Sent(0, r => Is(r, 0x0F, 0x02)));
            Assert.False(bench.Unit(mouse).Asleep);
            Assert.Single(bench.Backend.Claimed);
            Assert.True(mouse.Open);
            Assert.Equal(0, DiagMark.LinesSince(diag, "refuses every paint"));

            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(mouse, Green);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Green), P.ExtendedStatic(0x3F, 0x01, Green)), mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>A claim report the device takes is no color taken. A
        /// DeathAdder Chroma that turns its LEDs on and then answers 05 to
        /// its first color does not take its family. It has no fallback,
        /// so it is handed back what it showed and leaves the list.</summary>
        [Fact]
        public void AClaimReportTheDeviceTakes_IsNoColorTaken_SoARefusedFirstColorRefusesTheFamily()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x0043);
            mouse.Answer = request => Is(request, 0x03, 0x01) ? Reply(request, P.StatusNotSupported) : Answers(request);
            string diag = DiagMark.Write();
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            // Both LEDs were turned on, one color was tried, and the paint
            // went no further.
            Assert.Equal(Lines(P.ClassicState(0x3F, 0x01, true), P.ClassicState(0x3F, 0x04, true)),
                mouse.Sent(0, r => Is(r, 0x03, 0x00) && r[P.ArgumentsAt + 2] == 0x01));
            Assert.Equal(Lines(P.ClassicColor(0x3F, 0x01, Red)), mouse.Sent(0, r => Is(r, 0x03, 0x01) && r[P.ArgumentsAt + 2] == 0xFF));
            Assert.Empty(bench.Backend.Snapshot.Units);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(1, DiagMark.LinesSince(diag, "refuses every paint"));
            // What it showed is written back: both LEDs off again.
            Assert.Equal(Lines(P.ClassicState(0x3F, 0x01, false), P.ClassicState(0x3F, 0x04, false)),
                mouse.Sent(0, r => Is(r, 0x03, 0x00) && r[P.ArgumentsAt + 2] == 0x00));
            bench.Verify();
        }

        /// <summary>A Tartarus Chroma is sent no state report, at the claim
        /// or at the hand-back: no reference sends its backlight one. What
        /// it showed is given back as its color and its effect, whatever
        /// its state read says.</summary>
        [Fact]
        public void ATartarusChroma_IsHandedBackItsColorAndItsEffect_AndNoState()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var keypad = bench.Add(0x0208);
            byte stateStatus = P.StatusSuccess;
            keypad.Answer = request =>
            {
                // The state read says off while the backlight is lit.
                if (Is(request, 0x03, 0x80)) return Reply(request, Volatile.Read(ref stateStatus), 0x01, 0x05, 0x00);
                if (Is(request, 0x03, 0x81)) return Reply(request, P.StatusSuccess, 0x01, 0x05, 0x00, 0xFF, 0x00);
                if (Is(request, 0x03, 0x82)) return Reply(request, P.StatusSuccess, 0x01, 0x05, 0x00);
                return Answers(request);
            };
            string[] ownLighting = { "FF 03/01 size 05 [00 05 00 FF 00]", "FF 03/02 size 03 [00 05 00]" };
            bench.StartAndWalk();
            bench.Link(keypad);
            bench.Light(keypad, Red);
            bench.Passes();
            Assert.Equal(Lines(P.ClassicColor(0xFF, 0x05, Red), P.ClassicEffect(0xFF, 0x05, 0x00)), keypad.Sent(0, r => !IsRead(r)));
            Assert.Equal(ownLighting, Lines(bench.Backend.Claimed.Single().HandBack));

            long mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(keypad.Row, 0);
            bench.Passes();
            Assert.Equal(ownLighting, keypad.Sent(mark));

            // A state read the firmware refuses changes nothing of it.
            Volatile.Write(ref stateStatus, P.StatusNotSupported);
            bench.Light(keypad, Red);
            bench.Passes();
            mark = bench.Mark;
            PeripheralOutputs.ReleaseLighting(keypad.Row, 0);
            bench.Passes();
            Assert.Equal(ownLighting, keypad.Sent(mark));
            Assert.DoesNotContain(keypad.Calls, c => c.Kind == "set" && Is(c.Bytes, 0x03, 0x00));
            bench.Verify();
        }

        /// <summary>Status 03 on all five attempts of a first paint counts
        /// as 05 does. After a first paint that worked, a report the device
        /// refuses is one report lost and the family stays.</summary>
        [Fact]
        public void FiveFailuresOfAFirstPaintCountAsARefusal_AndALaterOneDoesNot()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var laptop = bench.Add(0x0239);
            int refuse = 1;
            laptop.Answer = request => Is(request, 0x03, 0x0B) && Volatile.Read(ref refuse) != 0
                ? Reply(request, P.StatusFailure) : Answers(request);
            bench.StartAndWalk();
            bench.Link(laptop);
            bench.Light(laptop, Red);
            bench.Passes();
            Assert.Equal(5, laptop.Sent(0, r => Is(r, 0x03, 0x0B)).Length);
            // Its fallback is the effect report, sent once the color has held.
            Assert.Empty(laptop.Sent(0, r => Is(r, 0x03, 0x0A)));
            bench.Advance(P.EffectHoldMs);
            bench.Passes();
            Assert.Equal(Lines(P.StandardStatic(0xFF, Red)), laptop.Sent(0, r => Is(r, 0x03, 0x0A)));

            // Another laptop takes its frame, then refuses one row of a
            // later one: the frame goes on and stays the paint.
            var other = bench.Add(0x02C6);
            Volatile.Write(ref refuse, 0);
            bench.Walk();
            bench.Link(laptop, other);
            bench.Light(other, Red);
            bench.Passes();
            Assert.Equal(7, other.Sent(0, r => !IsRead(r)).Length);
            other.Answer = request => Is(request, 0x03, 0x0B) && request[P.ArgumentsAt + 1] == 0x03
                ? Reply(request, P.StatusNotSupported) : Answers(request);
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(other, Blue);
            bench.Passes();
            Assert.Equal(7, other.Sent(mark, r => !IsRead(r)).Length);
            mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(other, Green);
            bench.Passes();
            Assert.Equal(7, other.Sent(mark, r => !IsRead(r)).Length);
            bench.Verify();
        }

        // ─────────────────────────────────────────────
        //  A slow device
        // ─────────────────────────────────────────────

        /// <summary>A Viper Ultimate takes 59.9 ms to have its reply ready.
        /// The worker does not wait for it: it notes when the read is due
        /// and serves the keyboard beside it meanwhile, holding the lock
        /// for the mouse's request until its reply is read.</summary>
        [Fact]
        public void ASlowDevicesReply_IsNotWaitedForOnTheWorker_WhileAnotherDeviceIsServed()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(0x007A);
            var keyboard = bench.Add(BlackWidowChroma);
            bench.StartAndWalk();
            bench.Link(mouse, keyboard);
            bench.Light(mouse, Red);
            bench.Light(keyboard, Red);
            bench.Passes();

            // The keyboard is claimed and painted in one pass. The mouse's
            // first request is out and its reply not read yet.
            // The claim read, the layout read, six rows and the show report.
            Assert.Equal(9, keyboard.Sent().Length);
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            Assert.DoesNotContain(59900, bench.Waits);
            Assert.All(bench.Waits, w => Assert.Equal(600, w));

            // The keyboard repaints while the mouse's reply is owed.
            long mark = bench.Mark;
            bench.Advance(RazerDirectBackend.PaintMs);
            bench.Light(keyboard, Blue);
            bench.Passes();
            Assert.Equal(7, keyboard.Sent(mark).Length);
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            bench.Advance(26);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));

            // 60 ms after the request the reply is read and the next
            // request goes out, and so on through the claim.
            for (int step = 0; step < 6 && mouse.Sent(0, r => Is(r, 0x0F, 0x02)).Length == 0; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            bench.Advance(60);
            bench.Passes();
            Assert.Equal(Lines(
                P.ExtendedBrightnessRead(0x3F, 0x04), P.RegionListRead(0x3F), P.StoredEffectRead(0x3F, 0x04),
                P.ExtendedStatic(0x3F, 0x04, Red)), mouse.Sent());
            Assert.Equal(Enumerable.Repeat(new[] { "set", "get" }, 4).SelectMany(p => p), mouse.Calls.Select(c => c.Kind));
            Assert.Single(bench.Backend.Claimed, c => c.Path == mouse.Key);
            Assert.DoesNotContain(59900, bench.Waits);

            // The lock was held from each of the mouse's requests to the
            // read of its reply, with the keyboard's taken inside it.
            int depth = 0;
            var events = bench.Guard.Journal.Select(e => (e.What, e.Tick))
                .Concat(mouse.Calls.Select(c => (What: c.Kind, c.Tick))).OrderBy(e => e.Tick);
            foreach (var (what, _) in events)
            {
                if (what == "take") depth++;
                else if (what == "release") depth--;
                else Assert.True(depth > 0, "a call on the mouse was made without the lock");
            }
            Assert.Equal(0, depth);
            bench.Verify();
        }

        /// <summary>A lit unit whose collection leaves the walk is done
        /// with at once: its handle is closed, it is off the crash path's
        /// record, nothing more is sent to it, and a slow device's request
        /// in flight gives the lock back with its reply unread.</summary>
        [Fact]
        public void ALitUnitWhoseCollectionLeavesTheWalk_IsClosedAndOffTheRecord_AndGivesTheLockBack()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            // A Viper Ultimate, whose reply takes 59.9 ms: its request
            // stays in flight across passes, with the lock held for it.
            var slow = bench.Add(0x007A);
            bench.StartAndWalk();
            bench.Link(mouse, slow);
            bench.Light(mouse, Red);
            bench.Light(slow, Red);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed, c => c.Path == mouse.Key);
            Assert.True(mouse.Open);
            Assert.Equal(new[] { "set" }, slow.Calls.Select(c => c.Kind));
            Assert.True(slow.Open);
            int Held() => bench.Guard.Journal.Count(e => e.What == "take") - bench.Guard.Journal.Count(e => e.What == "release");
            Assert.Equal(1, Held());

            // Both are unplugged.
            long mark = bench.Mark;
            mouse.Present = false;
            slow.Present = false;
            bench.Walk();
            bench.Passes();
            Assert.Empty(bench.Backend.Snapshot.Units);
            Assert.False(mouse.Open);
            Assert.False(slow.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(0, Held());
            // Nothing was sent to either, and the reply owed was not read.
            Assert.DoesNotContain(mouse.Calls, c => c.Tick > mark);
            Assert.DoesNotContain(slow.Calls, c => c.Tick > mark);
            bench.Advance(120);
            bench.Passes();
            Assert.DoesNotContain(slow.Calls, c => c.Tick > mark);
            bench.Verify();
        }

        /// <summary>A device with two 91-byte collections is written
        /// through the first that answers the claim read. One that stays
        /// silent is closed and the next is opened. What answered is the
        /// collection on the crash path's record, and the one every later
        /// claim opens.</summary>
        [Fact]
        public void AClaimGoesThroughTheDevicesNextCollection_WhenTheFirstStaysSilent()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var keyboard = bench.Add(BlackWidowChroma, (2, 1, 0x01, 0x02), (3, 0, 0x0C, 0x01));
            string first = keyboard.Collections[0].Path, second = keyboard.Collections[1].Path;
            keyboard.AnswerAt = (path, request) => path == first ? Silence(request) : Answers(request);
            bench.StartAndWalk();
            Assert.Equal(first, bench.Unit(keyboard).Key);
            bench.Link(keyboard);
            bench.Light(keyboard, Red);
            bench.Passes();

            // Five attempts of the claim read on the first, then the whole
            // claim on the second.
            var onFirst = keyboard.Calls.Where(c => c.Kind == "set" && c.Path == first).ToList();
            Assert.Equal(5, onFirst.Count);
            Assert.All(onFirst, c => Assert.True(Is(c.Bytes, 0x03, 0x83)));
            var frame = Enumerable.Range(0, 6).Select(r => P.StandardRow(0xFF, r, 22, Red)).Append(P.StandardShow(0xFF));
            Assert.Equal(Lines(new[] { P.StandardBrightnessRead(0xFF, 0x05), P.KeyboardInfoRead(0xFF) }.Concat(frame).ToArray()),
                keyboard.Calls.Where(c => c.Kind == "set" && c.Path == second).Select(c => RazerDirectProtocolTests.Line(c.Bytes)));
            Assert.Equal(second, bench.Backend.Claimed.Single().Path);
            Assert.Equal(second, Assert.Single(keyboard.Channels, c => !c.Disposed).Path);
            Assert.False(bench.Unit(keyboard).Asleep);
            Assert.Equal(first, bench.Unit(keyboard).Key);

            // Let go and claimed again: the collection that answered, alone.
            PeripheralOutputs.ReleaseLighting(keyboard.Row, 0);
            bench.Passes();
            Assert.False(keyboard.Open);
            int opens = keyboard.Opens;
            long mark = bench.Mark;
            bench.Light(keyboard, Green);
            bench.Passes();
            Assert.Equal(opens + 1, keyboard.Opens);
            Assert.Contains(keyboard.Calls, c => c.Tick > mark);
            Assert.All(keyboard.Calls.Where(c => c.Tick > mark), c => Assert.Equal(second, c.Path));
            bench.Verify();
        }

        /// <summary>A slow device that stays silent is tried five times
        /// with no wait slept on the worker: each read comes a pass after
        /// its wait, a reply that is not the request's is read once more,
        /// and the next attempt goes out 10 ms later.</summary>
        [Fact]
        public void ASlowDevicesAttempts_AreSpacedOnTheClock_NotSleptFor()
        {
            using var bench = new Bench();
            var mouse = bench.Add(0x007A);
            mouse.Answer = Silence;
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            bench.Advance(60);
            bench.Passes();
            Assert.Equal(new[] { "set", "get" }, mouse.Calls.Select(c => c.Kind));
            bench.Advance(60);
            bench.Passes();
            Assert.Equal(new[] { "set", "get", "get" }, mouse.Calls.Select(c => c.Kind));
            bench.Advance(9);
            bench.Passes();
            Assert.Equal(3, mouse.Calls.Count);
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(new[] { "set", "get", "get", "set" }, mouse.Calls.Select(c => c.Kind));
            for (int step = 0; step < 20 && !bench.Unit(mouse).Asleep; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            Assert.True(bench.Unit(mouse).Asleep);
            Assert.Equal(Enumerable.Repeat(new[] { "set", "get", "get" }, 5).SelectMany(p => p), mouse.Calls.Select(c => c.Kind));
            Assert.Empty(bench.Waits);
            Assert.Equal(bench.Guard.Journal.Count(e => e.What == "take"), bench.Guard.Journal.Count(e => e.What == "release"));
        }

        /// <summary>A slow device's reply is due from when its request went
        /// out. A pass reads its clock once, at its start, and the request
        /// can go out much later, behind another device's replies: counted
        /// from the pass's start, the read would come before the device has
        /// its reply ready.</summary>
        [Fact]
        public void ASlowReplyIsDueFromWhenItsRequestWentOut_NotFromThePassThatSentIt()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            // The keyboard is served first, and its first reply takes 40 ms.
            var keyboard = bench.Add(BlackWidowChroma);
            var mouse = bench.Add(0x007A);
            int slow = 1;
            keyboard.Answer = request =>
            {
                if (Interlocked.Exchange(ref slow, 0) == 1) bench.Advance(40);
                return Answers(request);
            };
            bench.StartAndWalk();
            bench.Light(keyboard, Red);
            bench.Light(mouse, Red);
            bench.Link(keyboard, mouse);
            bench.Passes();
            // The claim read, the layout read, six rows and the show report.
            Assert.Equal(9, keyboard.Sent().Length);
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));

            // 59 ms after the request, 99 ms after that pass began: not yet.
            bench.Advance(59);
            bench.Passes();
            Assert.Equal(new[] { "set" }, mouse.Calls.Select(c => c.Kind));
            bench.Advance(1);
            bench.Passes();
            Assert.Equal(new[] { "set", "get", "set" }, mouse.Calls.Select(c => c.Kind));
            bench.Verify();
        }

        // ─────────────────────────────────────────────
        //  Stop, the crash path and calls after a stop
        // ─────────────────────────────────────────────

        /// <summary>Stopping the worker hands every unit it painted back on
        /// the worker's own handle before the handle is closed, and its
        /// snapshot goes back to "not scanned". A start after it is a new
        /// run: the host's walk lists the unit again and the standing claim
        /// paints.</summary>
        [Fact]
        public void StoppingTheWorker_HandsEveryPaintedUnitBack_AndAStartAfterItRunsAgain()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            var keyboard = bench.Add(BlackWidowChroma);
            bench.StartAndWalk();
            bench.Link(mouse, keyboard);
            bench.Light(mouse, Red);
            bench.Light(keyboard, Red);
            bench.Passes();
            long mark = bench.Mark;

            bench.Backend.Stop();
            Assert.False(bench.Backend.WorkerAlive);
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            Assert.Empty(keyboard.Sent(mark));
            Assert.False(mouse.Open);
            Assert.False(keyboard.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);
            Assert.Equal(bench.Guard.Journal.Count(e => e.What == "take"), bench.Guard.Journal.Count(e => e.What == "release"));

            mark = bench.Mark;
            bench.Backend.Start();
            bench.Passes();
            Assert.False(bench.Backend.Snapshot.Scanned);
            bench.Walk();
            bench.Passes();
            Assert.Equal(2, bench.Backend.Snapshot.Units.Count);
            Assert.Equal(6, mouse.Sent(mark).Length);
            bench.Verify();
        }

        /// <summary>The worker's last act has no later pass to ask again
        /// in. A hand-back the device does not take there is said so in
        /// the log, once, the unit is not called asleep, and no walk is
        /// asked for.</summary>
        [Fact]
        public void AHandBackTheDeviceDoesNotTake_AtAStop_IsSaidSo_AndNothingIsAskedFor()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            string diag = DiagMark.Write();
            mouse.FailWrites = true;
            bench.Backend.Stop();
            Assert.False(bench.Backend.WorkerAlive);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            Assert.Equal(0, Volatile.Read(ref bench.Walks));
            Assert.Equal(1, DiagMark.LinesSince(diag, "PERIPHERAL Razer 'Razer DeathAdder V2' did not take its hand-back"));
            Assert.Equal(0, DiagMark.LinesSince(diag, "asking again later"));
        }

        /// <summary>A backend takes calls after its stop and after its
        /// dispose and does nothing: a list it is handed is not the next
        /// run's, and it opens nothing, starts no thread once disposed and
        /// throws nothing.</summary>
        [Fact]
        public void CallsAfterAStopOrADispose_DoNothing()
        {
            using var bench = new Bench();
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            bench.Backend.Stop();
            IDirectUnitBackend backend = bench.Backend;

            void EveryCall()
            {
                long mark = bench.Mark;
                int passes = bench.Backend.PassCount;
                bench.Walk();
                backend.Rescan(null);
                backend.SignalStop();
                backend.ReleaseClaimedNow(bench.Now + 750);
                backend.WaitForSilence(250);
                bench.Backend.NoteResume();
                backend.Stop();
                Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
                Assert.False(bench.Backend.WorkerAlive);
                Assert.Equal(passes, bench.Backend.PassCount);
                Assert.Equal(mark, bench.Mark);
            }
            EveryCall();
            // The list a stopped backend was handed is not kept for the
            // next run.
            bench.Backend.Start();
            bench.Passes();
            Assert.False(bench.Backend.Snapshot.Scanned);
            bench.Backend.Stop();

            bench.Backend.Dispose();
            EveryCall();
            // A disposed backend starts no worker: none is alive after the
            // call, and none has made a pass by the end either.
            int passes = bench.Backend.PassCount;
            int workers = bench.Workers.Count;
            bench.Backend.Start();
            Assert.False(bench.Backend.WorkerAlive);
            bench.Backend.Dispose();
            EveryCall();
            Assert.Equal(passes, bench.Backend.PassCount);
            Assert.Equal(workers, bench.Workers.Count);
            Assert.Empty(bench.OneShots);
        }

        /// <summary>A stop that finds a slow device with a reply still owed
        /// gives that request its reply time and reads the reply before
        /// the hand-back goes out behind it, and waits for the hand-back's
        /// own reply in place, since the worker has no later pass to read
        /// it on.</summary>
        [Fact]
        public void AStopThatFindsAReplyStillOwed_ReadsItBeforeTheHandBackGoesOut()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            // A Viper Ultimate on its cable: 59.9 ms to a reply, one LED.
            var mouse = bench.Add(0x007A);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            // The claim is four requests, each read 60 ms after it went out.
            for (int step = 0; step < 8 && mouse.Calls.Count < 8; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            Assert.Equal(8, mouse.Calls.Count);
            Assert.Single(bench.Backend.Claimed);
            Assert.Empty(bench.Waits);

            // A new color is on its way to the mouse when the worker stops.
            bench.Light(mouse, Blue);
            bench.Passes();
            Assert.Equal("set", mouse.Calls.Last().Kind);
            long mark = bench.Mark;
            bench.Backend.Stop();

            Assert.Equal(new[] { "get", "set", "get" }, mouse.Calls.Where(c => c.Tick > mark).Select(c => c.Kind));
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04)), mouse.Sent(mark));
            // The reply time of the request in flight, then the hand-back's.
            Assert.Equal(new[] { 59900, 59900 }, bench.Waits);
            Assert.Equal(bench.Guard.Journal.Count(e => e.What == "take"), bench.Guard.Journal.Count(e => e.What == "release"));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>A stop that comes while a claim's first color report is
        /// being answered: the report went out and the device took it, so
        /// the worker's last act hands it back, though the claim never got
        /// to its second report.</summary>
        [Fact]
        public void AStopWhileTheFirstColorReportIsAnswered_HandsItBack()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            using var gate = new ManualResetEventSlim(false);
            int held = 0;
            // The reply to the first color report waits for the stop.
            mouse.Answer = request =>
            {
                if (Is(request, 0x0F, 0x02) && Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                return Answers(request);
            };
            try
            {
                bench.StartAndWalk();
                bench.Link(mouse);
                bench.Light(mouse, Red);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the worker is held in the first color's reply");
                var worker = Assert.Single(bench.Workers);
                long mark = bench.Mark;
                bench.Backend.SignalStop();
                gate.Set();
                Assert.True(worker.Join(30000));
                // No second color report, and the hand-back of both LEDs.
                Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
                Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red)), mouse.Sent(0, r => Is(r, 0x0F, 0x02) && r[P.ArgumentsAt + 2] == 0x01));
                Assert.False(mouse.Open);
                Assert.Empty(bench.Backend.Claimed);
            }
            finally
            {
                gate.Set();
            }
            bench.Verify();
        }

        /// <summary>The same on a slow device, whose reply to the first
        /// color report is still owed when the worker stops: the last act
        /// reads it, finds the report taken, and hands the color back.</summary>
        [Fact]
        public void AStopThatFindsTheFirstColorsReplyOwed_ReadsItAndHandsTheColorBack()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            // A Viper Ultimate on its cable: 59.9 ms to a reply, one LED.
            var mouse = bench.Add(0x007A);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            // The claim's three reads, each read 60 ms after it went out,
            // then the color.
            for (int step = 0; step < 8 && mouse.Sent(0, r => Is(r, 0x0F, 0x02)).Length == 0; step++)
            {
                bench.Advance(60);
                bench.Passes();
            }
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red)), mouse.Sent(0, r => Is(r, 0x0F, 0x02)));
            Assert.Equal("set", mouse.Calls.Last().Kind);

            long mark = bench.Mark;
            bench.Backend.Stop();
            Assert.Equal(new[] { "get", "set", "get" }, mouse.Calls.Where(c => c.Tick > mark).Select(c => c.Kind));
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04)), mouse.Sent(mark));
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);
            bench.Verify();
        }

        /// <summary>The crash path stops the worker before anything is
        /// handed back, so a worker a crash dialog keeps alive never paints
        /// the device again. The unit gets its hand-back on the worker's
        /// own handle, or through the crash path's writer where the worker
        /// took longer than the crash path waits. The record is empty after
        /// it, and a second call sends nothing.</summary>
        [Fact]
        public void TheCrashPath_StopsTheWorker_AndHandsTheUnitBackForGood()
        {
            using var bench = new Bench(reassertMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            long mark = bench.Mark;
            string[] handBack = Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01));

            bench.Backend.SignalStop();
            bench.Backend.ReleaseClaimedNow(bench.Now + 750);
            Assert.True(SpinWait.SpinUntil(() => !bench.Backend.WorkerAlive, 30000), "the worker stopped");
            var went = mouse.Sent(mark).Concat(bench.OneShots.Select(s => RazerDirectProtocolTests.Line(s.Report))).ToArray();
            Assert.All(went, line => Assert.Contains(line, handBack));
            Assert.All(handBack, line => Assert.Contains(line, went));
            Assert.Empty(bench.Backend.Claimed);
            Assert.False(mouse.Open);

            // Nothing claims or paints after it, whatever the colors and
            // the clock do, and a second call finds nothing on the record.
            mark = bench.Mark;
            int shots = bench.OneShots.Count;
            bench.Advance(1000);
            bench.Light(mouse, Blue);
            bench.Backend.ReleaseClaimedNow(bench.Now + 750);
            Assert.Equal(mark, bench.Mark);
            Assert.Equal(shots, bench.OneShots.Count);
            bench.Verify();
        }

        /// <summary>A worker held inside a device call cannot hand anything
        /// back. The crash path waits for it no longer than 250 ms, then
        /// writes each recorded hand-back through a handle of its own, with
        /// no read, each write bounded at 200 ms and at the time left, and
        /// none past the deadline. A lock that stays busy is waited for
        /// 25 ms and the report goes out all the same.</summary>
        [Fact]
        public void TheCrashPath_WritesThroughAHandleOfItsOwn_WhenTheWorkerIsHeld_AndStopsAtTheDeadline()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            // A Naga Chroma: three LEDs, so three hand-back reports.
            var mouse = bench.Add(0x0053);
            using var gate = new ManualResetEventSlim(false);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(3, bench.Backend.Claimed.Single().HandBack.Length);
            try
            {
                HoldTheWorkerInARead(bench, mouse, gate);

                // Each write of the crash path takes 125 ms of the clock,
                // against a deadline 250 ms away, and the lock stays busy.
                bench.OnOneShot = (_, _, _) => bench.Advance(125);
                bench.Guard.Busy = true;
                long mark = bench.Mark;
                bench.Backend.SignalStop();
                bench.Backend.ReleaseClaimedNow(bench.Now + 250);
                Assert.True(bench.Backend.WorkerAlive);

                // Two of the three: the first bounded at 200 ms, the second
                // at the 125 ms left, and the third not written at all.
                Assert.Equal(new[]
                {
                    (mouse.Key, RazerDirectProtocolTests.Line(P.MouseExtendedSpectrum(0x3F, 0x05)), RazerDirectBackend.OneShotMs),
                    (mouse.Key, RazerDirectProtocolTests.Line(P.MouseExtendedSpectrum(0x3F, 0x04)), 125),
                }, bench.OneShots.Select(s => (s.Path, RazerDirectProtocolTests.Line(s.Report), s.TimeoutMs)));
                Assert.Empty(bench.Backend.Claimed);
                // The lock was asked for before each, for no longer than a
                // paint waits, and each report went out without it.
                Assert.Equal(new[] { ("busy", RazerGuard.PaintWaitMs), ("busy", RazerGuard.PaintWaitMs) },
                    bench.Guard.Journal.Where(e => e.Tick > mark && e.Thread == Environment.CurrentManagedThreadId)
                        .Select(e => (e.What, e.WaitMs)));
                // No handle of the worker's was written to or read from.
                Assert.DoesNotContain(mouse.Calls, c => c.Tick > mark);
            }
            finally
            {
                gate.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !bench.Backend.WorkerAlive, 30000), "the worker ended once it came back");
            Assert.False(mouse.Open);
            // It came back to a stop: the paint it was held in went no
            // further than the one report.
            Assert.Single(mouse.Sent(0, r => Is(r, 0x03, 0x0D) && r[P.ArgumentsAt + 2] == 0x06 && r[P.ArgumentsAt + 5] == 0xFF));
        }

        /// <summary>OpenRGB takes the device with the worker held in a read,
        /// so no pass has let the unit go. Neither the crash path's one-shot
        /// nor the worker's way out sends a hand-back: it would land on
        /// OpenRGB's lighting, as on a running Synapse's.</summary>
        [Fact]
        public void TheCrashPathAndTheWorkersWayOut_SendNothing_ToAUnitOpenRgbHolds()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            using var gate = new ManualResetEventSlim(false);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            long mark;
            try
            {
                HoldTheWorkerInARead(bench, mouse, gate);
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks
                    {
                        Device = mouse.Row,
                        Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                        TakenByOpenRgb = new[] { mouse.Path },
                    },
                }));
                mark = bench.Mark;
                bench.Backend.SignalStop();
                bench.Backend.ReleaseClaimedNow(bench.Now + 750);
                Assert.Empty(bench.OneShots);
                Assert.Empty(bench.Backend.Claimed);
            }
            finally
            {
                gate.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !bench.Backend.WorkerAlive, 30000), "the worker ended once it came back");
            string[] handBack = Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01));
            Assert.DoesNotContain(mouse.Sent(mark), line => handBack.Contains(line));
            Assert.Empty(bench.OneShots);
        }

        /// <summary>A deadline that has passed by the time the crash path
        /// reaches this backend: nothing is written, and no lock is asked
        /// for.</summary>
        [Fact]
        public void TheCrashPath_WritesNothing_OnceTheDeadlineHasPassed()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            using var gate = new ManualResetEventSlim(false);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            try
            {
                HoldTheWorkerInARead(bench, mouse, gate);
                long mark = bench.Mark;
                bench.Backend.ReleaseClaimedNow(bench.Now - 1);
                Assert.Empty(bench.OneShots);
                Assert.Equal(mark, bench.Mark);
                Assert.True(bench.Backend.WorkerAlive);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>While a Razer program runs the crash path sends nothing
        /// either, and neither does the worker's last act: the program has
        /// the devices.</summary>
        [Fact]
        public void TheCrashPath_SendsNothing_WhileARazerProgramRuns()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            using var gate = new ManualResetEventSlim(false);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            try
            {
                HoldTheWorkerInARead(bench, mouse, gate);
                PeripheralOutputs.Presence = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
                long mark = bench.Mark;
                bench.Backend.ReleaseClaimedNow(bench.Now + 750);
                Assert.Empty(bench.OneShots);
                Assert.Empty(bench.Backend.Claimed);
                Assert.Equal(mark, bench.Mark);
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => !bench.Backend.WorkerAlive, 30000), "the worker ended");
                Assert.Empty(mouse.Sent(mark));
                Assert.False(mouse.Open);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A Razer program that starts in the moment before the
        /// worker stops: the worker's last act sends nothing either, and
        /// closes the handle. The program and the stop both come from the
        /// worker's own thread here, as a pass ends, so no pass of the
        /// worker's falls between them and its last act.</summary>
        [Fact]
        public void TheWorkersLastAct_SendsNothing_WhileARazerProgramRuns()
        {
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            bench.StartAndWalk();
            bench.Link(mouse);
            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Single(bench.Backend.Claimed);
            var worker = Assert.Single(bench.Workers);

            // The pass that lists a second device publishes what it found
            // as it ends. Synapse starts and the engine stops right there.
            int armed = 1;
            bench.Backend.SnapshotChanged += () =>
            {
                if (Interlocked.Exchange(ref armed, 0) != 1) return;
                PeripheralOutputs.Presence = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
                bench.Backend.SignalStop();
            };
            long mark = bench.Mark;
            bench.Add(BlackWidowChroma);
            bench.Walk();
            Assert.True(worker.Join(30000));
            Assert.Equal(mark, bench.Mark);
            Assert.False(mouse.Open);
        }

        /// <summary>A Stop whose join times out leaves the worker inside a
        /// device call. A Start after it runs one worker at a time: the new
        /// one waits for the old one before it does anything, and the old
        /// one reads its own stop when it comes back and ends with no
        /// further request. The new run starts from nothing: not from the
        /// snapshot of the run before it, and not from a list handed to
        /// that run which its worker never took.</summary>
        [Fact]
        public void ARestartAfterATimedOutStop_RunsOneWorkerAtATime_AndStartsFromNothing()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            using var gate = new ManualResetEventSlim(false);
            int held = 0;
            mouse.Answer = request =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                return Answers(request);
            };
            try
            {
                bench.StartAndWalk();
                Assert.True(bench.Backend.Snapshot.Scanned);
                bench.Link(mouse);
                bench.Light(mouse, Red);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the worker is held in its claim read");
                var first = Assert.Single(bench.Workers);
                // A walk comes in while the worker is held, and stays
                // untaken.
                bench.Walk();
                bench.Backend.Stop();
                Assert.Equal(1, mouse.Opens);
                Assert.True(mouse.Open);

                // The new run does nothing while the old worker lives.
                int passes = bench.Backend.PassCount;
                bench.Backend.Start();
                Assert.Same(DirectBackendSnapshot.NotScanned, bench.Backend.Snapshot);
                Assert.False(SpinWait.SpinUntil(() => bench.Backend.PassCount > passes, 300));
                Assert.Equal(1, mouse.Opens);

                // The old worker comes back to its stop: no request after
                // the claim read it was held in, and its handle closed.
                gate.Set();
                Assert.True(first.Join(30000));
                var handle = mouse.Channels.First();
                Assert.Equal(new[] { "set", "get" }, handle.Calls.Select(c => c.Kind));
                Assert.True(handle.Disposed);

                // The new worker runs now, and has scanned nothing: the
                // list handed to the run before it was not its own.
                bench.Passes();
                Assert.False(bench.Backend.Snapshot.Scanned);
                Assert.Equal(1, mouse.Opens);

                // Its own walk lists the unit, and the standing claim paints.
                bench.Walk();
                bench.Passes();
                Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)),
                    mouse.Sent(0, r => Is(r, 0x0F, 0x02)));
                Assert.Equal(2, mouse.Opens);
                Assert.Equal(2, bench.Workers.Count);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A dock and the mouse paired to it share one collection. The
        /// next run lights only the dock, so the old worker still hands the
        /// mouse back: a claim is on a device, and nobody else knows that mouse
        /// is lit.</summary>
        [Fact]
        public void AWorkerThatOutlivedItsRun_HandsBackAPairedMouseTheNextRunLeftAlone()
        {
            PeripheralLightMaps.ResetForTest();
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var dock = bench.Add(MouseDockPro);
            byte[] polled = null;
            int busy = 0, armed = 0;
            using var gate = new ManualResetEventSlim(false);
            using var heldNow = new ManualResetEventSlim(false);
            dock.Answer = request =>
            {
                if (Interlocked.CompareExchange(ref armed, 0, 1) == 1)
                {
                    heldNow.Set();
                    gate.Wait(30000);
                }
                if (Is(request, 0x00, 0xBF))
                    return Reply(request, P.StatusSuccess, 2, 0x01, 0x00, 0xAB, 0x01, 0x00, 0xA4);
                if (request[P.TransactionAt] != 0x1F) return Answers(request);
                if (!ReferenceEquals(request, polled))
                {
                    polled = request;
                    busy = 0;
                }
                return ++busy <= 3 ? Reply(request, P.StatusBusy) : Answers(request);
            };
            try
            {
                bench.StartAndWalk();
                bench.Link(dock);
                bench.Light(dock, Red);
                bench.Passes();
                string pairedKey = RazerDirectBackend.PairedKey(dock.Key, BasiliskV3ProWireless);
                bench.Passes();
                var pairedPath = new OutputPath(OutputFamily.RazerUnit, pairedKey);
                long painted = bench.Mark;
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks { Device = dock.Row, Lighting = new[] { pairedPath, dock.Path }, HasVendorProgram = true },
                }));
                LightKeys(dock, Red, Logo(Green));
                for (int step = 0; step < 400 && dock.Sent(painted, r => r[P.TransactionAt] == 0x1F).Length < 5; step++)
                {
                    bench.Advance(2);
                    bench.Passes(1);
                }
                for (int step = 0; step < 8; step++)
                {
                    bench.Advance(2);
                    bench.Passes(1);
                }
                bench.Passes();
                Assert.True(dock.Sent(painted, r => r[P.TransactionAt] == 0x1F).Length >= 5, "the first worker painted the mouse");
                var first = Assert.Single(bench.Workers);

                // The first worker is held in the next call it makes.
                Volatile.Write(ref armed, 1);
                bench.Advance(RazerDirectBackend.PaintMs);
                bench.Light(dock, Blue);
                Assert.True(heldNow.Wait(30000), "the first worker is held");
                bench.Backend.Dispose();

                // The next run lights the dock and leaves the mouse alone.
                bench.Link(dock);
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                Assert.True(SpinWait.SpinUntil(() => bench.Backend.Claimed.Any(c => c.Path == dock.Key), 30000), "the next run claimed the dock");
                bench.Passes();
                long mark = bench.Mark;

                gate.Set();
                Assert.True(first.Join(30000), "the first worker ended");
                Assert.NotEmpty(dock.Sent(mark, r => r[P.TransactionAt] == 0x1F));
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A worker that outlived its run, wedged in a device call
        /// past the wait its successor made for it, takes nothing of its
        /// successor's with it when it comes back. The successor claimed
        /// the same collection meanwhile: the crash path's record still
        /// holds that claim, the snapshot still lists the unit, and the old
        /// worker closes its handle without a report, since what the
        /// device shows is the successor's to say.</summary>
        [Fact]
        public void AWorkerThatOutlivedItsRun_LeavesItsSuccessorsClaimAlone()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            using var gate = new ManualResetEventSlim(false);
            int held = 0;
            // The first paint report of the first run gets no reply.
            mouse.Answer = request =>
            {
                if (Is(request, 0x0F, 0x02) && Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                return Answers(request);
            };
            try
            {
                // The row is linked and lit before the worker has a list, so
                // the pass that lists the unit is the pass that claims it,
                // and it is held before it has published what it found.
                bench.Link(mouse);
                bench.Light(mouse, Red);
                bench.Backend.Start();
                bench.Walk();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the first worker is held in its first paint");
                var first = Assert.Single(bench.Workers);
                Assert.Single(bench.Backend.Claimed);
                Assert.False(bench.Backend.Snapshot.Scanned);
                bench.Backend.Dispose();

                // The next run waits 100 ms for the worker before it, then
                // goes on: it lists the unit, claims it through a handle of
                // its own and paints it, on a backend of its own, as each
                // engine start has it.
                bench.Backend = bench.Make(reassertMs: int.MaxValue, predecessorJoinMs: 100, stopJoinMs: 50);
                bench.Backend.Start();
                bench.Walk();
                Assert.True(SpinWait.SpinUntil(() => mouse.Sent(0, r => Is(r, 0x0F, 0x02)).Length == 3, 30000), "the second worker painted");
                bench.Passes();
                Assert.True(first.IsAlive);
                var record = Assert.Single(bench.Backend.Claimed);
                var snapshot = bench.Backend.Snapshot;
                Assert.NotNull(bench.Unit(mouse));
                int changes = Volatile.Read(ref bench.Changes);
                int walks = Volatile.Read(ref bench.Walks);
                long mark = bench.Mark;

                // The first worker comes back and ends.
                gate.Set();
                Assert.True(first.Join(30000));
                Assert.True(mouse.Channels.First().Disposed);
                Assert.Empty(mouse.Sent(mark));
                Assert.Equal(record.Path, Assert.Single(bench.Backend.Claimed).Path);
                Assert.Same(snapshot, bench.Backend.Snapshot);
                Assert.Equal(changes, Volatile.Read(ref bench.Changes));
                Assert.Equal(walks, Volatile.Read(ref bench.Walks));
                Assert.True(mouse.Open);

                // The unit is still the second worker's to hand back.
                var second = bench.Workers.Last();
                bench.Backend.Stop();
                Assert.True(second.Join(30000));
                Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
                Assert.Empty(bench.Backend.Claimed);
                Assert.False(mouse.Open);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A predecessor wedged inside a device call holds the
        /// next worker only for its bounded wait. The new worker then says
        /// so and goes on.</summary>
        [Fact]
        public void AWedgedPredecessor_HoldsTheNextWorkerOnlyForItsBoundedWait()
        {
            using var bench = new Bench(reassertMs: int.MaxValue, stopJoinMs: 50);
            var mouse = bench.Add(DeathAdderV2);
            using var gate = new ManualResetEventSlim(false);
            int held = 0;
            mouse.Answer = request =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0) gate.Wait(30000);
                return Answers(request);
            };
            string gaveUp = "PERIPHERAL Razer previous worker still running after 200 ms, starting anyway";
            var next = bench.Make(predecessorJoinMs: 200, stopJoinMs: 50);
            try
            {
                bench.Backend.Start();
                bench.Walk();
                bench.Link(mouse);
                bench.Light(mouse, Red);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 30000), "the first worker is held");
                string mark = DiagMark.Write();
                next.Start();
                bench.Walk(next);
                Assert.True(SpinWait.SpinUntil(() => next.Snapshot.Scanned, 30000), "the new worker went on");
                Assert.True(bench.Backend.WorkerAlive);
                Assert.Equal(1, DiagMark.LinesSince(mark, gaveUp));
            }
            finally
            {
                gate.Set();
                next.Dispose();
            }
        }

        // ─────────────────────────────────────────────
        //  In the host
        // ─────────────────────────────────────────────

        /// <summary>The worker in the peripheral host, from the walk to the
        /// paint: the host hands it the collections, links the mouse row to
        /// the unit it lists, and the row's claim paints the mouse. Synapse
        /// starting is seen by the host's next pass, which withdraws the
        /// path, and the worker sends nothing more.</summary>
        [Fact]
        public void InTheHost_ARazerRowIsLinkedToItsUnit_Painted_AndLeftToSynapseWhenItStarts()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            using var bench = new Bench(reassertMs: int.MaxValue);
            var mouse = bench.Add(DeathAdderV2);
            var presence = new PeripheralPresence(false, false, true, false, false);
            var row = new UserDevice
            {
                InstanceGuid = mouse.Row, IsOnline = true, CapType = InputDeviceType.Mouse,
                VendorId = PeripheralLinker.RazerVid, ProdId = DeathAdderV2, DevicePath = mouse.Key,
            };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(row);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = mouse.Row, MapTo = 0 });

            using var host = new PeripheralOutputHost(
                new HidppBackend(() => Array.Empty<VendorHidCollection>(), (c, s) => null, _ => Guid.Empty, 1, 5),
                new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, 20),
                () => new SensaHapticsService(30000, 16, 1000, () => 0f) { PlatformCanLoadEngine = () => false },
                containerOf: path => path == mouse.Key ? mouse.Container : Guid.Empty,
                readPresence: () => Volatile.Read(ref presence),
                direct: new IDirectUnitBackend[] { bench.Backend },
                enumerate: () => bench.Devices.Where(d => d.Present).SelectMany(d => d.Collections).ToList(),
                ticks: () => bench.Now)
            {
                SlotHasController = _ => true,
            };
            void HostPasses()
            {
                for (int i = 0; i < 2; i++)
                {
                    int before = host.PassCount;
                    host.Nudge();
                    Assert.True(SpinWait.SpinUntil(() => host.PassCount > before, 30000), "a link pass ran");
                }
            }

            host.Start();
            Assert.True(SpinWait.SpinUntil(() => bench.Unit(mouse) != null, 30000), "the worker listed the unit from the host's walk");
            HostPasses();
            var links = PeripheralOutputs.Links.For(mouse.Row);
            Assert.Equal(new[] { mouse.Path }, links.Lighting);
            Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") }, links.Shadows);
            Assert.True(row.HasPeripheralLighting);
            Assert.Equal(0, mouse.Opens);

            bench.Light(mouse, Red);
            bench.Passes();
            Assert.Equal(Lines(P.ExtendedStatic(0x3F, 0x04, Red), P.ExtendedStatic(0x3F, 0x01, Red)),
                mouse.Sent(0, r => Is(r, 0x0F, 0x02)));
            Assert.True(PeripheralOutputs.WatchesVendorPrograms(bench.Now));

            // Synapse starts. The claim is held, so the host looks for it
            // on its very next pass.
            long mark = bench.Mark;
            Volatile.Write(ref presence, presence with { RazerSoftwareRunning = true });
            HostPasses();
            bench.Passes();
            Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") },
                PeripheralOutputs.Links.For(mouse.Row).Lighting);
            Assert.Equal(mark, bench.Mark);
            Assert.False(mouse.Open);
            Assert.Empty(bench.Backend.Claimed);

            // Synapse quits: the path comes back and the standing claim
            // paints, with no SetLighting call in between.
            Volatile.Write(ref presence, presence with { RazerSoftwareRunning = false });
            bench.Advance(PeripheralOutputHost.PresenceMs);
            HostPasses();
            bench.Passes();
            Assert.Equal(new[] { mouse.Path }, PeripheralOutputs.Links.For(mouse.Row).Lighting);
            Assert.Equal(2, mouse.Sent(mark, r => Is(r, 0x0F, 0x02)).Length);

            // The engine stops: the mouse gets its own lighting back.
            mark = bench.Mark;
            host.Stop();
            Assert.Equal(Lines(P.ExtendedSpectrum(0x3F, 0x04), P.ExtendedSpectrum(0x3F, 0x01)), mouse.Sent(mark));
            bench.Verify();
        }

        /// <summary>The engine's own list starts this worker, under the
        /// Razer heading, last of the lighting families.</summary>
        [Fact]
        public void TheEnginesBackends_HoldTheRazerWorker()
        {
            var backends = DirectBackends.Create();
            try
            {
                var razer = Assert.Single(backends, b => b.Family == OutputFamily.RazerUnit);
                Assert.IsType<RazerDirectBackend>(razer);
                Assert.Same(DirectBackendSnapshot.NotScanned, razer.Snapshot);
                Assert.All(backends.SkipWhile(b => !ReferenceEquals(b, razer)).Skip(1),
                    b => Assert.Equal(OutputFamily.TouchSenseUnit, b.Family));
            }
            finally
            {
                foreach (var backend in backends) backend.Dispose();
            }
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "DirectBackends.cs");
            int heading = source.IndexOf("// Razer. Last of the lighting families", StringComparison.Ordinal);
            int line = source.IndexOf("Add(backends, OutputFamily.RazerUnit, () => new RazerDirectBackend());", StringComparison.Ordinal);
            int next = source.IndexOf("// TouchSense.", StringComparison.Ordinal);
            Assert.True(heading > 0 && line > heading && next > line, "the Razer line sits under the Razer heading");
        }
    }

    /// <summary>The diagnostics lines written since a mark of the test's
    /// own. The ring keeps its newest 4,000 lines, so a count taken before
    /// a step and again after it comes up short whenever an older line
    /// that matched rolled out in between. A test writes a mark instead
    /// and counts what follows it.</summary>
    internal static class DiagMark
    {
        internal static string Write()
        {
            string mark = "TEST mark " + Guid.NewGuid().ToString("N");
            SdlDiagLog.WriteLine(mark);
            return mark;
        }

        internal static int LinesSince(string mark, string text)
        {
            var lines = SdlDiagLog.Snapshot().Split('\n');
            int at = Array.FindLastIndex(lines, line => line.Contains(mark));
            Assert.True(at >= 0, "the diagnostics ring rolled past the test's mark");
            return lines.Skip(at + 1).Count(line => line.Contains(text));
        }
    }

    /// <summary>The lock the Razer worker shares with third-party tools
    /// (#494), always on a mutex of the test's own: one with no name where
    /// the test needs none, and otherwise one in the session's namespace
    /// under a name nothing else knows, where the test is about a name or
    /// looks at the mutex from outside. No test here makes, opens or takes
    /// the mutex the tools share, which the test host refuses besides
    /// (<see cref="RazerMutexOffLimitsTests"/>). The classes that write or
    /// count that lock's diagnostics lines share a collection, so none runs
    /// beside another: they use lines of the one diagnostics log that start
    /// alike.</summary>
    [Collection("RazerGuardLog")]
    public class RazerGuardTests
    {
        private static string TestName(string tag) => $@"Local\PadForge-test-razer-guard-{tag}-{Environment.ProcessId}-{Guid.NewGuid():N}";

        /// <summary>A guard on a mutex with no name, which only the guard
        /// itself can reach.</summary>
        private static RazerGuard Unnamed() => new((string)null);

        /// <summary>Runs on a thread of its own and waits for it to end, so
        /// a mutex taken there belongs to that thread alone.</summary>
        private static T OnAThread<T>(Func<T> body)
        {
            T result = default;
            Exception fault = null;
            var thread = new Thread(() =>
            {
                try { result = body(); }
                catch (Exception ex) { fault = ex; }
            });
            thread.Start();
            Assert.True(thread.Join(30000));
            Assert.Null(fault);
            return result;
        }

        /// <summary>The name is OpenRGB's, and the waits are the ones the
        /// references wait: 25 ms for a paint, 250 ms for a claim or a
        /// hand-back. The name is read here and never opened.</summary>
        [Fact]
        public void TheLockIsOpenRgbsMutex_WithTheWaitsOfItsReferences()
        {
            Assert.Equal(@"Global\RazerLinkReadWriteGuardMutex", RazerGuard.Name);
            Assert.Equal(25, RazerGuard.PaintWaitMs);
            Assert.Equal(250, RazerGuard.CommandWaitMs);
        }

        /// <summary>A thread that holds the lock may take it again, and it
        /// is free for another thread only once every take is released.</summary>
        [Fact]
        public void TheLockIsTakenAgainByItsHolder_AndFreeOnceEveryTakeIsReleased()
        {
            using var guard = Unnamed();
            Assert.False(guard.Unavailable);
            Assert.True(guard.Take(RazerGuard.PaintWaitMs));
            Assert.False(guard.Unavailable);
            Assert.True(guard.Take(0));

            // Another thread, as another program's would be.
            bool Other(int waitMs) => OnAThread(() =>
            {
                bool taken = guard.Take(waitMs);
                if (taken) guard.Release();
                return taken;
            });
            Assert.False(Other(RazerGuard.PaintWaitMs));
            guard.Release();
            Assert.False(Other(0));
            guard.Release();
            Assert.True(Other(RazerGuard.CommandWaitMs));

            // A release with nothing held is no error.
            guard.Release();
            Assert.True(guard.Take(0));
            guard.Release();
        }

        /// <summary>No mutex is made until the lock is first taken: a
        /// device nobody lights costs no kernel object. Seen from outside,
        /// so on a name of the test's own.</summary>
        [Fact]
        public void TheMutexIsMadeOnFirstUse()
        {
            string name = TestName("lazy");
            using var guard = new RazerGuard(name);
            Assert.False(Mutex.TryOpenExisting(name, out _));
            Assert.True(guard.Take(0));
            Assert.True(Mutex.TryOpenExisting(name, out var existing));
            existing.Dispose();
            guard.Release();
        }

        /// <summary>A mutex whose owner died without releasing it belongs
        /// to the next waiter, which carries on with it.</summary>
        [Fact]
        public void AnAbandonedLock_IsTheNextWaiters()
        {
            using var guard = Unnamed();
            // A thread that takes the mutex and ends without releasing it.
            Assert.True(OnAThread(() => guard.Take(0)));
            Assert.True(guard.Take(RazerGuard.CommandWaitMs));
            // It is this thread's now: another thread cannot have it.
            Assert.False(OnAThread(() => guard.Take(0)));
            guard.Release();
            Assert.True(OnAThread(() =>
            {
                bool taken = guard.Take(0);
                if (taken) guard.Release();
                return taken;
            }));
        }

        /// <summary>The mutex is made so that every user may open it with
        /// every right, since PadForge runs elevated and OpenRGB, which
        /// asks for every right on a mutex that exists, may not.</summary>
        [Fact]
        public void EveryoneMayOpenTheLock_WithEveryRight()
        {
            string name = TestName("open");
            using var guard = new RazerGuard(name);
            Assert.True(guard.Take(0));
            guard.Release();
            using var opened = MutexAcl.OpenExisting(name, MutexRights.ReadPermissions | MutexRights.Synchronize);
            var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var rules = opened.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<MutexAccessRule>().ToList();
            Assert.Contains(rules, rule => rule.IdentityReference.Equals(world)
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.MutexRights & MutexRights.FullControl) == MutexRights.FullControl);
            Assert.DoesNotContain(rules, rule => rule.AccessControlType == AccessControlType.Deny);
        }

        /// <summary>A mutex another program made first is opened, with the
        /// rights to wait on it and release it where its maker gives no
        /// more. Where the name can be neither made nor opened the worker
        /// goes on without a lock: every take answers at once, and it is
        /// said once in the log.</summary>
        [Fact]
        public void ALockThatCanBeNeitherMadeNorOpened_IsDoneWithout()
        {
            // Another program made the mutex first.
            string shared = TestName("shared");
            using (var theirs = new Mutex(false, shared))
            using (var guard = new RazerGuard(shared))
            {
                Assert.True(guard.Take(0));
                Assert.False(guard.Unavailable);
                Assert.False(OnAThread(() => theirs.WaitOne(0)));
                guard.Release();
                Assert.True(OnAThread(() =>
                {
                    bool taken = theirs.WaitOne(0);
                    if (taken) theirs.ReleaseMutex();
                    return taken;
                }));
            }

            // Its maker lets others wait on it and release it, and no
            // more, so a request for every right is refused.
            string narrow = TestName("narrow");
            var waitOnly = new MutexSecurity();
            waitOnly.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            using (var theirs = MutexAcl.Create(false, narrow, out _, waitOnly))
            using (var guard = new RazerGuard(narrow))
            {
                Assert.Throws<UnauthorizedAccessException>(() => MutexAcl.OpenExisting(narrow, MutexRights.FullControl).Dispose());
                Assert.True(guard.Take(0));
                Assert.False(guard.Unavailable);
                Assert.False(OnAThread(() => theirs.WaitOne(0)));
                guard.Release();
            }

            // The name belongs to an object of another kind.
            string taken = TestName("taken");
            string mark = DiagMark.Write();
            using var squatter = new EventWaitHandle(false, EventResetMode.ManualReset, taken);
            using var without = new RazerGuard(taken);
            Assert.True(without.Take(RazerGuard.CommandWaitMs));
            Assert.True(without.Unavailable);
            Assert.True(without.Take(0));
            without.Release();
            without.Release();
            Assert.True(OnAThread(() => without.Take(0)));
            Assert.Equal(1, DiagMark.LinesSince(mark, "PERIPHERAL Razer lock not available"));
        }

        /// <summary>A disposed lock is done without too: a worker that
        /// outlived its backend takes nothing and throws nothing.</summary>
        [Fact]
        public void ADisposedLock_TakesNothingAndThrowsNothing()
        {
            string name = TestName("disposed");
            var guard = new RazerGuard(name);
            Assert.True(guard.Take(0));
            guard.Release();
            guard.Dispose();
            guard.Dispose();
            Assert.True(guard.Take(RazerGuard.CommandWaitMs));
            guard.Release();
            Assert.False(Mutex.TryOpenExisting(name, out _));

            // Never used, then disposed: no mutex was ever made.
            string unused = TestName("unused");
            var idle = new RazerGuard(unused);
            idle.Dispose();
            Assert.True(idle.Take(0));
            Assert.False(Mutex.TryOpenExisting(unused, out _));
        }
    }
}
