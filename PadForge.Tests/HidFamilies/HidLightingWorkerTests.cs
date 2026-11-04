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
    /// takes every collection of <see cref="VendorId"/> and answers
    /// <see cref="Model"/>, whose sessions log what they are given.</summary>
    internal sealed class FakeWorkerFamily : IHidLightingFamily
    {
        public const ushort VendorId = 0xF00D;

        public readonly List<string> Log = new();
        public volatile HidLightingModel Model;

        public string Name => "Fake worker";

        public HidLightingModel Match(HidCollectionInfo collection)
            => collection?.VendorId == VendorId ? Model : null;

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
    /// the host is quiet.
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

        private HidLightingBackend Started(params HidCollectionInfo[] present)
        {
            var backend = new HidLightingBackend(new IHidLightingFamily[] { _family }, Open, _ => Box, Tick,
                (_, _, _) => true, (_, _, _) => true, ms => Advance(ms), idleTickMs: 5);
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
            Assert.Empty(_family.Calls().Where(c => c.StartsWith("08")));
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
