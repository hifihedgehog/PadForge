using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A family whose devices are no HID collections, scripted: its
    /// look answers <see cref="Found"/>, and its model opens a
    /// <see cref="FakeOwnChannel"/> whose session logs what it is
    /// given.</summary>
    internal sealed class FakeOwnFamily : IOwnLookFamily
    {
        public readonly List<string> Log = new();
        public readonly List<FakeOwnChannel> Channels = new();
        public volatile IReadOnlyList<OwnDevice> Found = Array.Empty<OwnDevice>();
        public volatile bool OpenFails;
        public volatile bool PaintFails;
        public volatile bool BeginFails;
        public volatile bool HandBackFails;

        /// <summary>The map a session reads from the device, or null.</summary>
        public volatile LightMap SessionMap;

        /// <summary>The name a session gives its device, or null.</summary>
        public volatile string SessionName;

        /// <summary>Set: the next paint waits on it, after it sets
        /// <see cref="Held"/>.</summary>
        public volatile ManualResetEventSlim Hold;
        public readonly ManualResetEventSlim Held = new(false);
        public int Looks;
        public readonly List<(string Key, byte[] Frame, int TimeoutMs)> OneShots = new();

        public HidLightingModel Model { get; }

        public FakeOwnFamily(IReadOnlyList<VendorProgram> programs = null)
        {
            Model = new HidLightingModel
            {
                Family = this,
                Name = "Fake own",
                Map = new LightMap(new[]
                {
                    new LightInfo(0, LightKind.Zone, 0, LightKeySource.None),
                    new LightInfo(1, LightKind.Key, 0x29, LightKeySource.Generated),
                }, "test") { Product = "fake:own" },
                Programs = programs ?? Array.Empty<VendorProgram>(),
                Open = key =>
                {
                    lock (Log) Log.Add("open " + key);
                    if (OpenFails) return null;
                    var channel = new FakeOwnChannel(this, key);
                    lock (Channels) Channels.Add(channel);
                    return channel;
                },
                BeginOwn = (handle, _) => BeginFails || handle is not FakeOwnChannel channel ? null : new FakeOwnSession(this, channel),
                WriteOnce = (key, frame, timeoutMs) =>
                {
                    lock (OneShots) OneShots.Add((key, frame, timeoutMs));
                    return true;
                },
            };
        }

        public string Name => "Fake own";

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        public IReadOnlyList<OwnDevice> Look()
        {
            Interlocked.Increment(ref Looks);
            return Found;
        }

        public List<string> Calls()
        {
            lock (Log) return Log.ToList();
        }
    }

    internal sealed class FakeOwnChannel : IDisposable
    {
        public FakeOwnFamily Family { get; }
        public string Key { get; }
        public volatile bool Disposed;

        public FakeOwnChannel(FakeOwnFamily family, string key)
        {
            Family = family;
            Key = key;
        }

        public void Dispose()
        {
            Disposed = true;
            lock (Family.Log) Family.Log.Add("close " + Key);
        }
    }

    internal sealed class FakeOwnSession : HidLightingSession
    {
        private readonly FakeOwnFamily _family;
        private readonly FakeOwnChannel _channel;
        private byte[][] _crash = Array.Empty<byte[]>();

        public FakeOwnSession(FakeOwnFamily family, FakeOwnChannel channel)
        {
            _family = family;
            _channel = channel;
        }

        public override bool Feature => false;

        public override LightMap Map => _family.SessionMap;

        public override string DeviceName => _family.SessionName;

        public override byte[][] CrashFrames => _crash;

        public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
            IReadOnlyList<int> colors)
        {
            lock (_family.Log)
                _family.Log.Add($"paint {_channel.Key} io={(io == null ? "null" : "hid")} {string.Join(",", colors.Select(c => c.ToString("X6")))}");
            if (_crash.Length == 0) _crash = new[] { new byte[] { 0xBB } };
            var hold = _family.Hold;
            if (hold != null)
            {
                _family.Held.Set();
                hold.Wait(30000);
            }
            return _family.PaintFails ? HidLightingPaint.Failed : HidLightingPaint.Sent;
        }

        public override bool HandBack(IHidReportChannel io, Action<int> wait)
        {
            lock (_family.Log) _family.Log.Add($"handback {_channel.Key} io={(io == null ? "null" : "hid")}");
            if (_family.HandBackFails) return false;
            _crash = Array.Empty<byte[]>();
            return true;
        }
    }

    /// <summary>
    /// The shared worker with a family whose devices are no HID collections
    /// (#557): its units come from the family's own look at each walk, are
    /// opened and painted through the family's own channel, and follow the
    /// worker's claim, hand-back, failure, crash and program rules as a
    /// collection's unit does.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class OwnLookWorkerTests : IDisposable
    {
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000c1");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private const string Key = @"\\.\FAKE0";

        private readonly FakeOwnFamily _family = new();
        private readonly List<(string Path, byte[] Frame, int TimeoutMs)> _hidShots = new();
        private int _hidOpens;
        private long _tick = 1_000_000;

        public OwnLookWorkerTests()
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

        private HidLightingBackend Started(FakeOwnFamily family = null, Func<string, Guid> containerOf = null)
        {
            family ??= _family;
            var backend = new HidLightingBackend(new IHidLightingFamily[] { family },
                _ => { Interlocked.Increment(ref _hidOpens); return null; },
                containerOf ?? (_ => Box), Tick,
                (path, frame, timeoutMs) => { lock (_hidShots) _hidShots.Add((path, frame, timeoutMs)); return true; },
                (path, frame, timeoutMs) => { lock (_hidShots) _hidShots.Add((path, frame, timeoutMs)); return true; },
                ms => Advance(ms), idleTickMs: 5);
            backend.Start();
            backend.Rescan(new HidCollectionInfo[0]);
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

        private static void Walk(HidLightingBackend backend)
        {
            backend.Rescan(new HidCollectionInfo[0]);
            Passes(backend);
        }

        private OwnDevice Laptop(bool builtIn = true)
            => new(Key, _family.Model) { Container = PeripheralLinker.LocalMachineContainer, BuiltInKeyboard = builtIn };

        private static readonly OutputPath Path = new(OutputFamily.HidLightingUnit, Key);

        private static void Link() => PeripheralOutputs.PublishLinks(new LinkTable(new[]
        {
            new DeviceLinks { Device = Device, Lighting = new[] { Path } },
        }));

        private static void Light(int rgb) => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1,
            (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        [Fact]
        public void TheFamilysLook_ListsItsUnits_WithTheContainerItGave_AndOpensNothing()
        {
            _family.Found = new[] { Laptop() };
            using var backend = Started(containerOf: _ => throw new InvalidOperationException("no lookup"));
            var unit = Assert.Single(backend.Snapshot.Units);
            Assert.Equal(OutputFamily.HidLightingUnit, unit.Family);
            Assert.Equal(Key, unit.Key);
            Assert.Equal(PeripheralLinker.LocalMachineContainer, unit.ContainerId);
            Assert.True(unit.BuiltInKeyboard);
            Assert.NotNull(PeripheralLightMaps.For(Path));
            Assert.Empty(_family.Calls());
            Assert.Equal(0, Volatile.Read(ref _hidOpens));
        }

        [Fact]
        public void AUnitWithNoContainer_IsLookedUpByItsKey()
        {
            var keys = new List<string>();
            _family.Found = new[] { new OwnDevice(Key, _family.Model) { VendorId = 0x1209, ProductId = 0x2201 } };
            using var backend = Started(containerOf: k => { lock (keys) keys.Add(k); return Box; });
            var unit = Assert.Single(backend.Snapshot.Units);
            Assert.Equal(Box, unit.ContainerId);
            Assert.Equal((ushort)0x1209, unit.VendorId);
            Assert.False(unit.BuiltInKeyboard);
            lock (keys) Assert.Contains(Key, keys);
        }

        [Fact]
        public void TheLook_IsMadeAtEveryWalk_AndAUnitLeavesWhenItIsNoLongerFound_ButNotWhenTheLookFailed()
        {
            _family.Found = new[] { Laptop() };
            using var backend = Started();
            int looks = Volatile.Read(ref _family.Looks);
            Walk(backend);
            Assert.True(Volatile.Read(ref _family.Looks) > looks);

            // A look that could not be made keeps the unit.
            _family.Found = null;
            Walk(backend);
            Assert.Single(backend.Snapshot.Units);

            // A look that finds nothing takes it off.
            _family.Found = Array.Empty<OwnDevice>();
            Walk(backend);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Null(PeripheralLightMaps.For(Path));
        }

        [Fact]
        public void AClaim_OpensThroughTheFamilysChannel_AndPaintsWithNoHidChannel()
        {
            _family.Found = new[] { Laptop() };
            using var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Assert.Equal(new[] { "open " + Key, "paint " + Key + " io=null 112233,112233" }, _family.Calls());
            Assert.Equal(0, Volatile.Read(ref _hidOpens));
            // The crash path's record carries the family's own writer.
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(Key, record.Path);
            Assert.NotNull(record.Write);
        }

        [Fact]
        public void TheClaimsEnd_HandsBackOnTheFamilysChannel_AndClosesIt()
        {
            _family.Found = new[] { Laptop() };
            using var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Unlight();
            Passes(backend);
            Assert.Equal(new[] { "handback " + Key + " io=null", "close " + Key }, _family.Calls().Skip(2));
            Assert.True(_family.Channels.Single().Disposed);
            Assert.Empty(backend.Recorded);
        }

        [Fact]
        public void AChannelThatDoesNotOpen_OrASessionThatDoesNotBegin_ListsTheUnitAsleep_AndItIsTriedAgain()
        {
            _family.Found = new[] { Laptop() };
            _family.OpenFails = true;
            using var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);

            // It opens again: listed awake, then painted.
            _family.OpenFails = false;
            Advance(HidLightingBackend.RetryMs);
            Passes(backend, 3);
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Contains("paint " + Key + " io=null 112233,112233", _family.Calls());

            // A session that does not begin closes the channel it opened.
            var family = new FakeOwnFamily { BeginFails = true };
            family.Found = new[] { new OwnDevice(Key, family.Model) { Container = Box } };
            backend.Dispose();
            using var second = Started(family);
            Link();
            Passes(second);
            Assert.True(Assert.Single(second.Snapshot.Units).Asleep);
            Assert.All(family.Channels, c => Assert.True(c.Disposed));
        }

        [Fact]
        public void AFailedPaint_ClosesTheChannel_AndListsTheUnitAsleep()
        {
            _family.Found = new[] { Laptop() };
            _family.PaintFails = true;
            using var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.True(_family.Channels.Single().Disposed);
            // What the lost session would have handed back stays owed, on the
            // crash record until the unit answers (IDirectUnitBackend, Owe).
            Assert.Single(backend.Recorded);
        }

        [Fact]
        public void Stopping_HandsAnOwnUnitBackOnItsChannel()
        {
            _family.Found = new[] { Laptop() };
            var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            // Stop with the unit open: the hand-back goes out on the channel.
            backend.Stop();
            Assert.Contains("handback " + Key + " io=null", _family.Calls());
            backend.Dispose();
            lock (_family.OneShots) Assert.Empty(_family.OneShots);
            lock (_hidShots) Assert.Empty(_hidShots);
        }

        /// <summary>A hand-back that did not go out on the channel as the
        /// worker stops goes out through the family's own writer, as a
        /// collection's goes out through a report of its own.</summary>
        [Fact]
        public void AHandBackThatFailsAtStop_GoesOutThroughTheFamilysWriter()
        {
            _family.Found = new[] { Laptop() };
            var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            _family.HandBackFails = true;
            backend.Stop();
            backend.Dispose();
            lock (_family.OneShots)
            {
                var shot = Assert.Single(_family.OneShots);
                Assert.Equal(Key, shot.Key);
                Assert.Equal(new byte[] { 0xBB }, shot.Frame);
            }
            lock (_hidShots) Assert.Empty(_hidShots);
        }

        [Fact]
        public void TheCrashPath_WritesAnOwnUnitsFramesThroughItsFamilysWriter()
        {
            _family.Found = new[] { Laptop() };
            var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Assert.Single(backend.Recorded);
            // The next picture holds the worker inside the session, so the
            // crash path stops waiting for it and turns to the record.
            _family.Hold = new ManualResetEventSlim(false);
            Light(0x445566);
            backend.Wake();
            Assert.True(_family.Held.Wait(30000), "the worker reached the paint");

            backend.ReleaseClaimedNow(Environment.TickCount64 + 2000);
            lock (_family.OneShots)
            {
                var shot = Assert.Single(_family.OneShots);
                Assert.Equal(Key, shot.Key);
                Assert.Equal(new byte[] { 0xBB }, shot.Frame);
            }
            lock (_hidShots) Assert.Empty(_hidShots);
            _family.Hold.Set();
            _family.Hold = null;
            backend.Dispose();
        }

        /// <summary>The repair of colors an earlier run left takes a HID
        /// channel (HidLightingModel.Repair), so an own unit never gets one,
        /// even from a model that says it owes some.</summary>
        [Fact]
        public void AnOwnUnit_IsNeverRepairedThroughAHidChannel()
        {
            int repairs = 0;
            var family = new FakeOwnFamily();
            var model = new HidLightingModel
            {
                Family = family, Name = family.Model.Name, Map = family.Model.Map,
                Open = family.Model.Open, BeginOwn = family.Model.BeginOwn, WriteOnce = family.Model.WriteOnce,
                Owed = _ => true,
                Repair = (_, _) => { Interlocked.Increment(ref repairs); return true; },
            };
            family.Found = new[] { new OwnDevice(Key, model) { Container = Box } };
            using var backend = Started(family);
            Advance(HidLightingBackend.RepairGraceMs + 10_000);
            Passes(backend, 3);
            Assert.Equal(0, Volatile.Read(ref repairs));
            Assert.Equal(0, Volatile.Read(ref _hidOpens));
            Assert.Empty(family.Calls());
        }

        /// <summary>A board the session read three lights from: an ISO
        /// board's key the model lacks among them.</summary>
        private static LightMap DeviceMap() => new(new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None),
            new LightInfo(1, LightKind.Key, 0x29, LightKeySource.Device),
            new LightInfo(7, LightKind.Key, 0, LightKeySource.None),
        }, "device") { Product = "fake:own" };

        /// <summary>A session that read the device's own map: the worker
        /// publishes it and renders by it, and the map stays after the
        /// session closes, for as long as the device stays.</summary>
        [Fact]
        public void ASessionsOwnMap_IsPublishedAndPainted_AndStaysAfterTheSessionCloses()
        {
            _family.Found = new[] { Laptop() };
            _family.SessionMap = DeviceMap();
            using var backend = Started();
            Assert.Same(_family.Model.Map, PeripheralLightMaps.For(Path));

            Link();
            Light(0x112233);
            Passes(backend);
            Assert.Same(_family.SessionMap, PeripheralLightMaps.For(Path));
            // Three colors, the session map's lights in its order.
            Assert.Contains("paint " + Key + " io=null 112233,112233,112233", _family.Calls());

            Unlight();
            Passes(backend);
            Assert.Contains("close " + Key, _family.Calls());
            Assert.Same(_family.SessionMap, PeripheralLightMaps.For(Path));
        }

        /// <summary>A later session that reads no map of its own leaves the
        /// map an earlier session read while the device stays.</summary>
        [Fact]
        public void ALaterSessionThatReadsNoMap_LeavesTheMapAnEarlierOneRead()
        {
            _family.Found = new[] { Laptop() };
            var read = DeviceMap();
            _family.SessionMap = read;
            using var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Unlight();
            Passes(backend);

            _family.SessionMap = null;
            Light(0x445566);
            Passes(backend);
            Assert.Same(read, PeripheralLightMaps.For(Path));
            Assert.Contains("paint " + Key + " io=null 445566,445566,445566", _family.Calls());
        }

        [Fact]
        public void ASessionWithNoMapOfItsOwn_KeepsTheModels()
        {
            _family.Found = new[] { Laptop() };
            using var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Assert.Same(_family.Model.Map, PeripheralLightMaps.For(Path));
            Assert.Contains("paint " + Key + " io=null 112233,112233", _family.Calls());
        }

        /// <summary>Names Map Key Lights learned apply to the session's map,
        /// at once and when they change, and the pictures follow them.</summary>
        [Fact]
        public void LearnedNames_ApplyToTheSessionsMap()
        {
            LearnedKeyMaps.ResetForTest();
            try
            {
                _family.Found = new[] { Laptop() };
                _family.SessionMap = DeviceMap();
                using var backend = Started();
                Link();
                Light(0x112233);
                Passes(backend);
                Assert.Equal(0, PeripheralLightMaps.For(Path).Lights[2].Usage);

                // Light 7 learned as A, and a key frame that colors A.
                LearnedKeyMaps.Set("fake:own", 7, 0x04);
                PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, 0x11, 0x22, 0x33,
                    keys: new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0xFF0000 }, null, null, null));
                Passes(backend, 3);
                var published = PeripheralLightMaps.For(Path);
                Assert.Equal(3, published.Lights.Count);
                Assert.Equal((ushort)0x04, published.Lights[2].Usage);
                Assert.Equal(LightKeySource.Learned, published.Lights[2].KeySource);
                Assert.Contains("paint " + Key + " io=null 112233,112233,FF0000", _family.Calls());
            }
            finally
            {
                LearnedKeyMaps.ResetForTest();
            }
        }

        /// <summary>A unit that fails keeps the map its session read, and one
        /// that leaves the walk takes its map down: a device that comes back
        /// starts from the model's map.</summary>
        [Fact]
        public void AFailedUnit_KeepsItsSessionMap_AndAGoneUnitLeavesNoneBehind()
        {
            _family.Found = new[] { Laptop() };
            _family.SessionMap = DeviceMap();
            using var backend = Started();
            Link();
            Light(0x112233);
            Passes(backend);
            Assert.Same(_family.SessionMap, PeripheralLightMaps.For(Path));

            _family.PaintFails = true;
            Light(0x445566);
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Same(_family.SessionMap, PeripheralLightMaps.For(Path));

            _family.Found = Array.Empty<OwnDevice>();
            Walk(backend);
            Assert.Null(PeripheralLightMaps.For(Path));

            // Back in the walk with no claim on it: the model's map until a
            // session reads one.
            Unlight();
            _family.Found = new[] { Laptop() };
            Walk(backend);
            Assert.Same(_family.Model.Map, PeripheralLightMaps.For(Path));
        }

        [Fact]
        public void AProgramOfTheModel_TakesAnOwnUnit_ByTheSameRules()
        {
            var program = new VendorProgram("Fake hub", "FakeHub");
            var family = new FakeOwnFamily(new[] { program });
            family.Found = new[] { new OwnDevice(Key, family.Model) { Container = Box } };
            using var backend = Started(family);
            Assert.Equal(new[] { program }, Assert.Single(backend.Snapshot.Units).Programs);
            Link();
            Light(0x112233);
            Passes(backend);
            int before = family.Calls().Count;

            PeripheralOutputs.Presence = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "FakeHub.exe" }, new[] { program });
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, LeftToProgram = "Fake hub" } }));
            Passes(backend);
            // Closed with no hand-back.
            Assert.Equal(new[] { "close " + Key }, family.Calls().Skip(before));
            Assert.Empty(backend.Recorded);
        }
    }

    /// <summary>A unit that lights the PC's own keyboard through its
    /// firmware links to the keyboard rows in the PC's own container
    /// (#557).</summary>
    public class BuiltInKeyboardLinkTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        private static DirectUnit Firmware(Guid container) => new(OutputFamily.HidLightingUnit, @"\\.\ATKACPI", container, 0, 0)
        {
            BuiltInKeyboard = true,
        };

        [Fact]
        public void TheUnit_GoesToTheKeyboardRowsOfThePc_WhateverTheirIds()
        {
            var pc = PeripheralLinker.LocalMachineContainer;
            var unit = Firmware(pc);
            Assert.True(PeripheralLinker.MatchesDevice(unit, new LinkRow(A, InputDeviceType.Keyboard, 0x0B05, 0x1866, pc, null)));
            Assert.True(PeripheralLinker.MatchesDevice(unit, new LinkRow(A, InputDeviceType.Keyboard, 0, 0, pc, null)));
            Assert.True(PeripheralLinker.MatchesDevice(unit, new LinkRow(A, InputDeviceType.AnalogKeyboard, 0, 0, pc, null)));
            // Not the touchpad or a mouse, and not a keyboard plugged in.
            Assert.False(PeripheralLinker.MatchesDevice(unit, new LinkRow(A, InputDeviceType.Mouse, 0, 0, pc, null)));
            Assert.False(PeripheralLinker.MatchesDevice(unit, new LinkRow(A, InputDeviceType.Keyboard, 0, 0, Box, null)));
            // A unit that says it is in another container is in no PC row.
            Assert.False(PeripheralLinker.MatchesDevice(Firmware(Box), new LinkRow(A, InputDeviceType.Keyboard, 0, 0, Box, null)));
            // An ordinary unit still needs its IDs.
            var usb = unit with { BuiltInKeyboard = false };
            Assert.False(PeripheralLinker.MatchesDevice(usb, new LinkRow(A, InputDeviceType.Keyboard, 0x0B05, 0x1866, pc, null)));
            Assert.True(PeripheralLinker.MatchesDevice(usb, new LinkRow(A, InputDeviceType.Keyboard, 0, 0, pc, null)));
        }

        [Fact]
        public void ALaptopsKeyboardRow_IsLitThroughTheFirmwareUnit()
        {
            var pc = PeripheralLinker.LocalMachineContainer;
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0, 0, pc, null);
            var unit = Firmware(pc);
            var links = PeripheralLinker.Build(new[] { row },
                new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None) { Units = new[] { unit } }).For(A);
            Assert.Equal(new[] { unit.Path }, links.Lighting);
        }
    }
}
