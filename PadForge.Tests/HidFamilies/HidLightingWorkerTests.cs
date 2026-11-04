using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A HID family scripted for the worker's own rules (#557): it
    /// takes every collection of <see cref="VendorId"/> on page 0xFF00 and answers
    /// <see cref="Model"/>, whose sessions log what they are given.</summary>
    internal sealed class FakeWorkerFamily : IHidLightingFamily
    {
        public const ushort VendorId = 0xF00D;

        public readonly List<string> Log = new();
        public volatile HidLightingModel Model;

        public string Name => "Fake worker";

        public HidLightingModel Match(HidCollectionInfo collection)
            => collection?.VendorId == VendorId && collection.UsagePage == 0xFF00 ? Model : null;

        public void Add(string line)
        {
            lock (Log) Log.Add(line);
        }

        public List<string> Calls()
        {
            lock (Log) return Log.ToList();
        }

        public static LightMap Map() => new(new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None),
            new LightInfo(1, LightKind.Key, 0x29, LightKeySource.Generated),
        }, "test") { Product = "fake:worker" };

        /// <summary>A model of the family with what a test sets on top.</summary>
        public HidLightingModel With(Func<HidLightingModel, HidLightingModel> change = null)
        {
            var model = new HidLightingModel
            {
                Family = this,
                Name = "Fake worker",
                Map = Map(),
                Begin = (io, _) => new FakeWorkerSession(this, io),
            };
            Model = change == null ? model : change(model);
            return Model;
        }
    }

    /// <summary>A session that logs each call with the colors it was
    /// given. Its keep-alive is the base class's unless
    /// <see cref="KeepAliveLine"/> is set.</summary>
    internal class FakeWorkerSession : HidLightingSession
    {
        protected readonly FakeWorkerFamily Family;
        protected readonly IHidReportChannel Io;

        public FakeWorkerSession(FakeWorkerFamily family, IHidReportChannel io)
        {
            Family = family;
            Io = io;
        }

        /// <summary>Set: the keep-alive logs this and paints nothing.</summary>
        public string KeepAliveLine { get; init; }

        public bool KeepAliveFails { get; init; }

        /// <summary>The session's own bounds, or null for the model's.</summary>
        public int? Hold { get; init; }
        public int? Repaint { get; init; }

        public override int? HoldMs => Hold;

        public override int? RepaintMs => Repaint;

        public override bool Feature => false;

        public override byte[][] CrashFrames => Array.Empty<byte[]>();

        public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
            IReadOnlyList<int> colors)
        {
            Family.Add("paint " + string.Join(",", colors.Select(c => c.ToString("X6"))));
            return HidLightingPaint.Sent;
        }

        public override bool KeepAlive(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
            IReadOnlyList<int> colors)
        {
            if (KeepAliveLine == null) return base.KeepAlive(io, wait, lights, colors);
            Family.Add(KeepAliveLine + " " + string.Join(",", colors.Select(c => c.ToString("X6"))));
            return !KeepAliveFails;
        }

        public override bool HandBack(IHidReportChannel io, Action<int> wait)
        {
            Family.Add("handback");
            return true;
        }
    }

    /// <summary>
    /// What the shared worker does for the families beyond one channel and
    /// one picture per change (#557): the keep-alive a device needs while
    /// the host is quiet, the sibling collections a device talks through,
    /// the handle with no access right a collection Windows holds gives,
    /// and the strings a family tells its devices from others by.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidLightingWorkerTests : IDisposable
    {
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000d1");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        private readonly FakeWorkerFamily _family = new();
        private readonly List<FakeReportChannel> _opened = new();
        private long _tick = 1_000_000;

        public HidLightingWorkerTests()
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

        internal static HidCollectionInfo Collection(int iface = 1, ushort page = 0xFF00, ushort pid = 0x0001, string tail = "1")
            => new()
            {
                Path = $@"\\?\hid#vid_f00d&pid_{pid:x4}&mi_{iface:x2}#7&{tail}&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = FakeWorkerFamily.VendorId,
                ProductId = pid,
                UsagePage = page,
                Usage = 1,
                InterfaceNumber = iface,
                OutputReportLength = 65,
            };

        /// <summary>The channel the worker gets for a path: read and write,
        /// unless the test says otherwise.</summary>
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

        /// <summary>The channel the scan reads strings through, with no
        /// access right, or null when it does not open.</summary>
        private Func<string, FakeReportChannel> _queryFor = path => new FakeReportChannel { Path = path, FeatureOnly = true };
        private readonly List<string> _queried = new();

        private IHidReportChannel Query(string path)
        {
            lock (_queried) _queried.Add(path);
            return _queryFor(path);
        }

        private HidLightingBackend Started(params HidCollectionInfo[] present)
        {
            var backend = new HidLightingBackend(new IHidLightingFamily[] { _family }, Open, _ => Box, Tick,
                (_, _, _) => true, (_, _, _) => true, ms => Advance(ms), idleTickMs: 5, query: Query);
            backend.Start();
            backend.Rescan(present);
            Passes(backend);
            return backend;
        }

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
                new DeviceLinks { Device = Device, Lighting = units.Select(u => new OutputPath(OutputFamily.HidLightingUnit, u.Path)).ToArray() },
            }));

        private static void Light(int rgb) => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1,
            (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private HidLightingBackend Lit(HidCollectionInfo unit, int rgb = 0x112233)
        {
            var backend = Started(unit);
            Link(unit);
            Light(rgb);
            Passes(backend);
            return backend;
        }

        private int Count(string line) => _family.Calls().Count(c => c == line);

        // ─────────────────────────────────────────────
        //  The keep-alive
        // ─────────────────────────────────────────────

        /// <summary>A keep-alive whose idle time is its interval, HyperX's
        /// and Corsair's: the picture again once that long passed with
        /// nothing sent, the resend included
        /// (RGBController_HyperXAlloyElite2.cpp:287 and 308-323).</summary>
        [Fact]
        public void TheKeepAlive_SendsThePictureAgain_OnceTheIntervalPassesWithNothingSent()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin, KeepAliveMs = 50, KeepAliveIdleMs = 50,
            });
            using var backend = Lit(Collection());
            Assert.Equal(1, Count("paint 112233,112233"));
            Advance(49);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            Advance(1);
            Passes(backend);
            Assert.Equal(2, Count("paint 112233,112233"));
            Advance(49);
            Passes(backend);
            Assert.Equal(2, Count("paint 112233,112233"));
            Advance(1);
            Passes(backend);
            Assert.Equal(3, Count("paint 112233,112233"));
            // A new picture puts the next one off by the interval.
            Advance(30);
            Light(0x445566);
            Passes(backend);
            Assert.Equal(1, Count("paint 445566,445566"));
            Advance(49);
            Passes(backend);
            Assert.Equal(1, Count("paint 445566,445566"));
            Advance(1);
            Passes(backend);
            Assert.Equal(2, Count("paint 445566,445566"));
        }

        /// <summary>A session that notes when it paints.</summary>
        private sealed class PaintNotingSession : FakeWorkerSession
        {
            private readonly Action _painting;

            public PaintNotingSession(FakeWorkerFamily family, IHidReportChannel io, Action painting)
                : base(family, io) => _painting = painting;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
                IReadOnlyList<int> colors)
            {
                _painting();
                return base.Paint(io, wait, lights, colors);
            }
        }

        /// <summary>The pass that sends a picture wakes the worker for the
        /// first keep-alive after it. Without that the worker sleeps its idle
        /// tick, which is longer than HyperX's 50 ms
        /// (RGBController_HyperXAlloyElite2.cpp:287 and 308-323).</summary>
        [Fact]
        public void ThePassThatSendsAPicture_WakesTheWorkerForTheFirstKeepAlive()
        {
            HidLightingBackend backend = null;
            int paintedIn = -1;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, KeepAliveMs = 50, KeepAliveIdleMs = 50,
                Begin = (io, _) => new PaintNotingSession(_family, io,
                    () => Interlocked.CompareExchange(ref paintedIn, backend.PassCount, -1)),
            });
            var unit = Collection();
            using (backend = new HidLightingBackend(new IHidLightingFamily[] { _family }, Open, _ => Box, Tick,
                (_, _, _) => true, (_, _, _) => true, ms => Advance(ms), idleTickMs: 1000, query: Query))
            {
                backend.Start();
                backend.Rescan(new[] { unit });
                Passes(backend);
                Link(unit);
                Passes(backend);
                // The claim wakes the worker once, and nothing wakes it
                // again before the wait the painting pass chose runs out.
                Light(0x112233);
                // The pass that painted has ended once the count passes the
                // count it painted at, and its wait is the last one written.
                Assert.True(SpinWait.SpinUntil(
                    () => Volatile.Read(ref paintedIn) >= 0 && backend.PassCount > Volatile.Read(ref paintedIn), 30000));
                Assert.Equal(1, Count("paint 112233,112233"));
                Assert.InRange(backend.WaitMs, 1, 50);
            }
        }

        /// <summary>Fnatic's: 500 ms idle after a picture, then one on every
        /// 100 ms (RGBController_FnaticStreak.cpp:480, 537 and 683-699), the
        /// session's own frame and never the picture.</summary>
        [Fact]
        public void TheKeepAlive_WaitsItsIdleTimeAfterAPicture_ThenGoesOutOnItsInterval()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, KeepAliveMs = 100, KeepAliveIdleMs = 500,
                Begin = (io, _) => new FakeWorkerSession(_family, io) { KeepAliveLine = "07" },
            });
            using var backend = Lit(Collection());
            Advance(499);
            Passes(backend);
            Assert.Equal(0, Count("07 112233,112233"));
            Advance(1);
            Passes(backend);
            Assert.Equal(1, Count("07 112233,112233"));
            Advance(99);
            Passes(backend);
            Assert.Equal(1, Count("07 112233,112233"));
            Advance(1);
            Passes(backend);
            Assert.Equal(2, Count("07 112233,112233"));
            Assert.Equal(1, Count("paint 112233,112233"));
        }

        /// <summary>The Everest 60's: every 500 ms from the session's start,
        /// whatever pictures go out between
        /// (RGBController_Mountain60Keyboard.cpp:434-442).</summary>
        [Fact]
        public void AKeepAliveWithNoIdleTime_IsNotPutOffByPictures()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, KeepAliveMs = 500,
                Begin = (io, _) => new FakeWorkerSession(_family, io) { KeepAliveLine = "08" },
            });
            using var backend = Lit(Collection());
            Advance(400);
            Light(0x445566);
            Passes(backend);
            Assert.Equal(1, Count("paint 445566,445566"));
            Advance(99);
            Passes(backend);
            Assert.DoesNotContain(_family.Calls(), c => c.StartsWith("08"));
            Advance(1);
            Passes(backend);
            Assert.Equal(1, Count("08 445566,445566"));
            Advance(500);
            Passes(backend);
            Assert.Equal(2, Count("08 445566,445566"));
        }

        /// <summary>While a changed picture waits out its hold, the device
        /// still shows the one before, and that is what is kept alive.</summary>
        [Fact]
        public void AWaitingPicture_DoesNotStopTheKeepAliveOfTheOneShown()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin, HoldMs = 1000,
                KeepAliveMs = 100, KeepAliveIdleMs = 100,
            });
            var unit = Collection();
            using var backend = Started(unit);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Advance(1000);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            Light(0x445566);
            Passes(backend);
            Advance(100);
            Passes(backend);
            Assert.Equal(2, Count("paint 112233,112233"));
            Assert.Equal(0, Count("paint 445566,445566"));
            Advance(900);
            Passes(backend);
            Assert.Equal(1, Count("paint 445566,445566"));
        }

        /// <summary>No keep-alive without a picture shown, none from a model
        /// that names none, and one that fails lists the unit asleep.</summary>
        [Fact]
        public void TheKeepAlive_NeedsAPictureShown_AndAFailedOneListsTheUnitAsleep()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, KeepAliveMs = 100, KeepAliveIdleMs = 100,
                Begin = (io, _) => new FakeWorkerSession(_family, io) { KeepAliveLine = "keep", KeepAliveFails = true },
            });
            var unit = Collection();
            using var backend = Started(unit);
            Advance(1000);
            Passes(backend);
            Assert.Empty(_family.Calls());

            Link(unit);
            Light(0x112233);
            Passes(backend);
            Advance(100);
            Passes(backend);
            Assert.Equal(1, Count("keep 112233,112233"));
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
        }

        // ─────────────────────────────────────────────
        //  Collections Windows holds
        // ─────────────────────────────────────────────

        private int _repairs;

        private HidLightingModel FeatureOnlyModel(bool heldByWindows) => _family.With(m => new HidLightingModel
        {
            Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin, HeldByWindows = heldByWindows,
            Owed = () => true,
            Repair = (io, _) =>
            {
                Interlocked.Increment(ref _repairs);
                return io.FeatureOnly;
            },
        });

        /// <summary>A handle with no access right means another program
        /// holds the collection, and nothing is written to it: the unit is
        /// listed asleep, the handle closes, and no repair goes out.</summary>
        [Fact]
        public void AHandleWithNoAccessRight_IsNotWritten_ForAnOrdinaryModel()
        {
            FeatureOnlyModel(heldByWindows: false);
            _channelFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, FeatureReportLength = 65 };
            var unit = Collection();
            using var backend = Started(unit);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(0, Volatile.Read(ref _repairs));
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Empty(_family.Calls());
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            lock (_opened) Assert.All(_opened, c => Assert.True(c.Disposed));
        }

        /// <summary>A model whose lighting lives on a mouse or keyboard
        /// collection keeps that handle, as hidapi hands OpenRGB one
        /// (hidapi windows/hid.c:1009-1026): it opens, paints, and an owed
        /// repair goes out on it.</summary>
        [Fact]
        public void AHandleWithNoAccessRight_IsKept_ForAModelWhoseCollectionWindowsHolds()
        {
            FeatureOnlyModel(heldByWindows: true);
            _channelFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, FeatureReportLength = 65 };
            var unit = Collection();
            using var backend = Started(unit);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.True(Volatile.Read(ref _repairs) > 0);
            Link(unit);
            Light(0x112233);
            Passes(backend);
            Assert.Equal(new[] { "paint 112233,112233" }, _family.Calls());
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
        }

        /// <summary>A sibling that does not open, or a session that does not
        /// begin, leaves no channel open, and a unit that fails closes its
        /// siblings with its own.</summary>
        [Fact]
        public void SiblingChannels_CloseWithTheUnit_OnEveryPath()
        {
            bool begins = false;
            IReadOnlyList<IHidReportChannel> given = null;
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, KeepAliveMs = 100, KeepAliveIdleMs = 100,
                Siblings = new[] { new HidSibling(3, 0xFF01) },
                BeginWith = (io, siblings, _) =>
                {
                    given = siblings;
                    return begins ? new FakeWorkerSession(_family, io) { KeepAliveLine = "keep", KeepAliveFails = true } : null;
                },
            });
            var unit = Collection();
            var sibling = Collection(iface: 3, page: 0xFF01, tail: "2");
            // Collections ahead of it that differ in one of the page, the
            // interface or the product are not the sibling.
            using var backend = Started(unit, Collection(iface: 3, page: 0xFF02, tail: "3"),
                Collection(iface: 2, page: 0xFF01, tail: "4"), Collection(iface: 3, page: 0xFF01, pid: 0x0002, tail: "5"),
                sibling);
            Assert.Equal(new[] { unit.Path }, backend.Snapshot.Units.Select(u => u.Key));
            Link(unit);
            Light(0x112233);
            Passes(backend);
            // The session did not begin: both channels closed.
            Assert.Equal(sibling.Path, Assert.Single(given).Path);
            lock (_opened)
            {
                Assert.Equal(new[] { unit.Path, sibling.Path }, _opened.Select(c => c.Path));
                Assert.All(_opened, c => Assert.True(c.Disposed));
            }

            // It begins, and a failed keep-alive closes both.
            begins = true;
            Advance(HidLightingBackend.RetryMs);
            Passes(backend, 3);
            Assert.Contains("paint 112233,112233", _family.Calls());
            Advance(100);
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            lock (_opened) Assert.All(_opened, c => Assert.True(c.Disposed));
        }

        // ─────────────────────────────────────────────
        //  Strings
        // ─────────────────────────────────────────────

        private HidLightingModel IdentifiedModel(List<string> asked) => _family.With(m => new HidLightingModel
        {
            Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin,
            Identify = io =>
            {
                lock (asked) asked.Add($"{io.ManufacturerString}|{io.ProductString}|{io.SerialNumber}|{io.FeatureOnly}");
                return io.SerialNumber == "ours";
            },
        });

        private void Walk(HidLightingBackend backend, params HidCollectionInfo[] present)
        {
            backend.Rescan(present);
            Passes(backend);
        }

        /// <summary>A model that tells its devices by their strings gets a
        /// unit only for a collection whose strings it takes, read once per
        /// appearance through a handle with no access right, which closes
        /// again. Nothing else is opened.</summary>
        [Fact]
        public void AModelsIdentityCheck_ListsOnlyTheCollectionsItTakes_AskedOncePerAppearance()
        {
            var asked = new List<string>();
            IdentifiedModel(asked);
            var ours = Collection(tail: "1");
            var theirs = Collection(tail: "2");
            var queried = new List<FakeReportChannel>();
            _queryFor = path =>
            {
                var channel = new FakeReportChannel
                {
                    Path = path, FeatureOnly = true, ManufacturerString = "Maker", ProductString = "Product",
                    SerialNumber = path == ours.Path ? "ours" : "theirs",
                };
                lock (queried) queried.Add(channel);
                return channel;
            };
            using var backend = Started(ours, theirs);
            Assert.Equal(new[] { ours.Path }, backend.Snapshot.Units.Select(u => u.Key));
            lock (asked) Assert.Equal(new[] { "Maker|Product|ours|True", "Maker|Product|theirs|True" }, asked);
            lock (queried) Assert.All(queried, c => Assert.True(c.Disposed));
            lock (_opened) Assert.Empty(_opened);

            // The next walk asks nothing again.
            Walk(backend, ours, theirs);
            lock (asked) Assert.Equal(2, asked.Count);
            Assert.Single(backend.Snapshot.Units);

            // A collection that left and came back is asked again.
            Walk(backend, ours);
            Walk(backend, ours, theirs);
            lock (asked) Assert.Equal(3, asked.Count);
            Assert.Single(backend.Snapshot.Units);
        }

        /// <summary>Strings that could not be read say nothing: the
        /// collection is not listed, and the next walk asks again.</summary>
        [Fact]
        public void ACollectionWhoseStringsCannotBeRead_IsNotListed_AndAskedAgain()
        {
            var asked = new List<string>();
            IdentifiedModel(asked);
            var ours = Collection();
            _queryFor = _ => null;
            using var backend = Started(ours);
            Assert.Empty(backend.Snapshot.Units);
            _queryFor = path => new FakeReportChannel { Path = path, FeatureOnly = true, SerialNumber = "ours" };
            Walk(backend, ours);
            Assert.Single(backend.Snapshot.Units);
            lock (_queried) Assert.Equal(2, _queried.Count);
        }

        /// <summary>The scan's own opener with no access right is
        /// HidReportChannel.OpenFeatureOnly, which opens nothing in the test
        /// host (RealHidAccess): a model with an identity check lists
        /// nothing there, and no string is read from a device of this
        /// machine.</summary>
        [Fact]
        public void TheDefaultStringOpener_OpensNothingInTheTestHost()
        {
            Assert.True(RealHidAccess.Refused);
            var asked = new List<string>();
            IdentifiedModel(asked);
            using var backend = new HidLightingBackend(new IHidLightingFamily[] { _family }, Open, _ => Box, Tick,
                (_, _, _) => true, (_, _, _) => true, ms => Advance(ms), idleTickMs: 5);
            backend.Start();
            backend.Rescan(new[] { Collection() });
            Passes(backend);
            Assert.Empty(backend.Snapshot.Units);
            lock (asked) Assert.Empty(asked);
            Assert.Null(HidReportChannel.OpenFeatureOnly(Collection().Path));
        }

        /// <summary>A model with no identity check reads no strings.</summary>
        [Fact]
        public void AModelWithNoIdentityCheck_ReadsNoStrings()
        {
            _family.With();
            using var backend = Started(Collection());
            Assert.Single(backend.Snapshot.Units);
            lock (_queried) Assert.Empty(_queried);
        }

        // ─────────────────────────────────────────────
        //  A session's own bounds
        // ─────────────────────────────────────────────

        /// <summary>A session that names its own hold and repaint interval
        /// paints by them, not the model's.</summary>
        [Fact]
        public void ASessionsOwnBounds_WinOverTheModels()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, HoldMs = 150, RepaintMs = 500,
                Begin = (io, _) => new FakeWorkerSession(_family, io) { Hold = 0, Repaint = 33 },
            });
            using var backend = Lit(Collection());
            // No hold: the first picture went out at once.
            Assert.Equal(1, Count("paint 112233,112233"));
            Light(0x445566);
            Advance(32);
            Passes(backend);
            Assert.Equal(0, Count("paint 445566,445566"));
            Advance(1);
            Passes(backend);
            Assert.Equal(1, Count("paint 445566,445566"));
        }

        /// <summary>A session that names none keeps the model's.</summary>
        [Fact]
        public void ASessionWithNoBounds_KeepsTheModels()
        {
            _family.With(m => new HidLightingModel
            {
                Family = m.Family, Name = m.Name, Map = m.Map, Begin = m.Begin, HoldMs = 150, RepaintMs = 500,
            });
            using var backend = Lit(Collection());
            Assert.Empty(_family.Calls());
            Advance(149);
            Passes(backend);
            Assert.Empty(_family.Calls());
            Advance(1);
            Passes(backend);
            Assert.Equal(1, Count("paint 112233,112233"));
            Light(0x445566);
            Passes(backend);
            Advance(499);
            Passes(backend);
            Assert.Equal(0, Count("paint 445566,445566"));
            Advance(1);
            Passes(backend);
            Assert.Equal(1, Count("paint 445566,445566"));
        }

        [Fact]
        public void AModelWithNoKeepAlive_SendsNothingWhileThePictureHolds()
        {
            _family.With();
            using var backend = Lit(Collection());
            Advance(60_000);
            Passes(backend, 3);
            Assert.Equal(new[] { "paint 112233,112233" }, _family.Calls());
        }
    }
}
