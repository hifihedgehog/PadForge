using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.OpenRgbServerWrites;

namespace PadForge.Tests
{
    /// <summary>
    /// The link pass while OpenRGB holds a device (#557): the device is lit
    /// through the OpenRGB SDK server and through nothing else, for every
    /// family of path PadForge has of its own, and those paths are named as
    /// taken so their backends let the device go with no hand-back.
    /// </summary>
    public class OpenRgbLinkTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000c1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000c2");
        private static readonly Guid Box = new("22222222-3333-4444-5555-666666666666");

        private static DirectUnit OpenRgbUnit(ushort vid, ushort pid, Guid container, string key = "openrgb")
            => new(OutputFamily.OpenRgbDevice, key, container, vid, pid);

        private static LinkInputs Inputs(PeripheralPresence presence, params DirectUnit[] units)
            => new(HidppSnapshot.Empty, presence ?? PeripheralPresence.None) { Units = units };

        private static DeviceLinks Link(LinkRow row, LinkInputs inputs) => PeripheralLinker.Build(new[] { row }, inputs).For(row.Device);

        [Fact]
        public void ARazerDevice_IsLitThroughOpenRgb_EvenWhileSynapseRuns_AndItsUnitIsTaken()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x024E, Box, null);
            var razer = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x024E);
            var openRgb = OpenRgbUnit(PeripheralLinker.RazerVid, 0x024E, Box);
            var synapse = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            foreach (var presence in new[] { PeripheralPresence.None, synapse })
            {
                var links = Link(row, Inputs(presence, razer, openRgb));
                Assert.Equal(new[] { openRgb.Path }, links.Lighting);
                Assert.Equal(new[] { razer.Path }, links.TakenByOpenRgb);
                // The Chroma session does not paint the keyboard category
                // for this row's claim.
                Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "keyboard") }, links.Shadows);
                Assert.Null(links.LeftToProgram);
                Assert.Equal(0, links.MaxClaimLevel);
            }
            // Without OpenRGB the unit lights the row as before.
            Assert.Equal(new[] { razer.Path }, Link(row, Inputs(null, razer)).Lighting);
        }

        [Fact]
        public void ALogitechDevice_IsLitThroughOpenRgb_AndKeepsItsRumble()
        {
            var hidpp = PeripheralLinkingTests.Unit(1, 3, Box) with
            {
                RgbFeatureId = HidppUnitProtocol.ColorLedEffects,
                Zones = new[] { new HidppZone(0, 1) },
            };
            var row = new LinkRow(A, InputDeviceType.Mouse, PeripheralLinker.LogitechVid, 0xC539, Box, null);
            var openRgb = OpenRgbUnit(PeripheralLinker.LogitechVid, 0xC539, Box) with { Kind = PeripheralLinker.LampArrayKindMouse };
            var inputs = new LinkInputs(new HidppSnapshot(new[] { hidpp }, Array.Empty<Guid>()), PeripheralPresence.None)
            {
                Units = new[] { openRgb },
            };
            var path = new OutputPath(OutputFamily.HidppUnit, hidpp.Key);
            var links = Link(row, inputs);
            Assert.Equal(new[] { openRgb.Path }, links.Lighting);
            Assert.Equal(new[] { path }, links.TakenByOpenRgb);
            Assert.Equal(new[] { path }, links.Haptics);
            Assert.Equal(new[] { hidpp.Key }, links.HidppUnits);

            // A controller of the other kind behind the same receiver holds
            // nothing of this row.
            var keyboard = openRgb with { Kind = PeripheralLinker.LampArrayKindKeyboard };
            links = Link(row, inputs with { Units = new[] { keyboard } });
            Assert.Equal(new[] { path }, links.Lighting);
            Assert.Empty(links.TakenByOpenRgb);
        }

        [Fact]
        public void ASteelSeriesDevice_IsLitThroughOpenRgb()
        {
            var row = new LinkRow(A, InputDeviceType.Mouse, PeripheralLinker.SteelSeriesVid, 0x1824, Box, null);
            var unit = new DirectUnit(OutputFamily.SteelSeriesUnit, "ss", Box, PeripheralLinker.SteelSeriesVid, 0x1824);
            var openRgb = OpenRgbUnit(PeripheralLinker.SteelSeriesVid, 0x1824, Box);
            var links = Link(row, Inputs(null, unit, openRgb));
            Assert.Equal(new[] { openRgb.Path }, links.Lighting);
            Assert.Equal(new[] { unit.Path }, links.TakenByOpenRgb);
            Assert.Empty(links.Shadows);
        }

        /// <summary>A device of any other vendor, with its LampArray
        /// interface and a shared-worker unit, Windows lighting the first and
        /// a program of the model running: OpenRGB lights it, and both paths
        /// are taken.</summary>
        [Fact]
        public void ALampArrayAndASharedWorkerDevice_AreLitThroughOpenRgb_WhateverElseHoldsThem()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x048D, 0xC197, Box, null);
            var own = new DirectUnit(OutputFamily.HidLightingUnit, "own", Box, 0x048D, 0xC197)
            {
                Programs = VendorPrograms.LenovoSpectrum,
            };
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, 0x048D, 0xC197) { WindowsLit = true };
            var openRgb = OpenRgbUnit(0x048D, 0xC197, Box);
            var vantage = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "LenovoVantage.exe" });
            var links = Link(row, Inputs(vantage, own, lamp, openRgb));
            Assert.Equal(new[] { openRgb.Path }, links.Lighting);
            Assert.Equal(new[] { own.Path, lamp.Path }, links.TakenByOpenRgb);
            Assert.Null(links.LeftToProgram);
            Assert.False(links.WindowsLightsLampArray);
        }

        /// <summary>An ASUS TUF laptop's keyboard, whose OpenRGB location is
        /// the ACPI device the shared worker keys its unit by: the keyboard
        /// rows in the PC's own container, and no mouse row there.</summary>
        [Fact]
        public void ALaptopsOwnKeyboard_IsHeldOnItsKeyboardRows()
        {
            var pc = PeripheralLinker.LocalMachineContainer;
            var keyboard = new LinkRow(A, InputDeviceType.Keyboard, 0x0B05, 0x19B6, pc, null);
            var touchpad = new LinkRow(B, InputDeviceType.Mouse, 0x04F3, 0x3200, pc, null);
            var tuf = new DirectUnit(OutputFamily.HidLightingUnit, AsusTufLaptopLighting.Key, pc, 0, 0) { BuiltInKeyboard = true };
            var openRgb = new DirectUnit(OutputFamily.OpenRgbDevice, AsusTufLaptopLighting.Key, pc, 0, 0)
            {
                BuiltInKeyboard = true, HeldKey = AsusTufLaptopLighting.Key,
            };
            var table = PeripheralLinker.Build(new[] { keyboard, touchpad }, Inputs(null, tuf, openRgb));
            Assert.Equal(new[] { openRgb.Path }, table.For(A).Lighting);
            Assert.Equal(new[] { tuf.Path }, table.For(A).TakenByOpenRgb);
            Assert.Null(table.For(B));
        }

        /// <summary>A Dygma Raise, which OpenRGB finds on its serial port
        /// and names by it alone: held through the shared worker's unit of
        /// that port.</summary>
        [Fact]
        public void ASerialDevice_IsHeldThroughTheUnitOfItsPort()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x1209, 0x2201, Box, null);
            var raise = new DirectUnit(OutputFamily.HidLightingUnit, @"\\.\COM5", Box, 0x1209, 0x2201);
            var openRgb = new DirectUnit(OutputFamily.OpenRgbDevice, "COM: COM5", Guid.Empty, 0, 0) { HeldKey = @"\\.\COM5" };
            var links = Link(row, Inputs(null, raise, openRgb));
            Assert.Equal(new[] { openRgb.Path }, links.Lighting);
            Assert.Equal(new[] { raise.Path }, links.TakenByOpenRgb);
            // Another port holds nothing of it.
            links = Link(row, Inputs(null, raise, openRgb with { HeldKey = @"\\.\COM6" }));
            Assert.Equal(new[] { raise.Path }, links.Lighting);
        }

        /// <summary>The server stopped answering while OpenRGB runs: the
        /// row holds no path and keeps its record, its own paths stay
        /// taken, and its tab names the server it waits for.</summary>
        [Fact]
        public void WhileOpenRgbsControllerSleeps_TheRowHoldsNoPath_AndItsPathsStayTaken()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x024E, Box, null);
            var razer = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x024E);
            var openRgb = OpenRgbUnit(PeripheralLinker.RazerVid, 0x024E, Box) with { Asleep = true };
            var inputs = Inputs(null, razer, openRgb);
            var links = Link(row, inputs);
            Assert.NotNull(links);
            Assert.Empty(links.Lighting);
            Assert.Equal("OpenRGB", links.LeftToProgram);
            Assert.True(links.WaitsForOpenRgbServer);
            Assert.Equal(new[] { razer.Path }, links.TakenByOpenRgb);
            Assert.True(PeripheralLinker.StillLooking(row, inputs));
        }

        /// <summary>A controller with no Direct mode: OpenRGB still holds the
        /// device, PadForge does not color it, and the tab names
        /// OpenRGB.</summary>
        [Fact]
        public void AControllerWithNoDirectMode_LeavesTheDeviceToOpenRgb()
        {
            var row = new LinkRow(A, InputDeviceType.Mouse, PeripheralLinker.SteelSeriesVid, 0x1824, Box, null);
            var unit = new DirectUnit(OutputFamily.SteelSeriesUnit, "ss", Box, PeripheralLinker.SteelSeriesVid, 0x1824);
            var openRgb = OpenRgbUnit(PeripheralLinker.SteelSeriesVid, 0x1824, Box) with { NoDirectMode = true };
            var links = Link(row, Inputs(null, unit, openRgb));
            Assert.Empty(links.Lighting);
            Assert.Equal("OpenRGB", links.LeftToProgram);
            Assert.Equal(new[] { unit.Path }, links.TakenByOpenRgb);
            Assert.Equal(PeripheralOutputKinds.Lighting, links.Kinds);
        }

        /// <summary>A LampArray device with a row of its own, a headset, that
        /// OpenRGB holds: its row is lit through OpenRGB.</summary>
        [Fact]
        public void AUnitWithARowOfItsOwn_IsLitThroughOpenRgb()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", Box, 0x1532, 0x0527);
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x1532, 0x0527, Guid.Empty, null) { Unit = headset.Path };
            var openRgb = OpenRgbUnit(0x1532, 0x0527, Box);
            var links = Link(row, Inputs(null, headset, openRgb));
            Assert.Equal(new[] { openRgb.Path }, links.Lighting);
            Assert.Equal(new[] { headset.Path }, links.TakenByOpenRgb);
            Assert.True(links.UnitRow);

            // Another device's controller holds nothing of it.
            links = Link(row, Inputs(null, headset, OpenRgbUnit(0x1532, 0x0528, Box)));
            Assert.Equal(new[] { headset.Path }, links.Lighting);
            Assert.Empty(links.TakenByOpenRgb);
        }

        [Fact]
        public void TheTable_GathersEveryTakenPath_AndAChangeInThemRepublishesIt()
        {
            var taken = new OutputPath(OutputFamily.RazerUnit, "razer");
            var with = new LinkTable(new[] { new DeviceLinks { Device = A, TakenByOpenRgb = new[] { taken } } });
            var without = new LinkTable(new[] { new DeviceLinks { Device = A } });
            Assert.Contains(taken, with.TakenByOpenRgb);
            Assert.Empty(without.TakenByOpenRgb);
            Assert.Empty(LinkTable.Empty.TakenByOpenRgb);
            Assert.False(with.SameAs(without));
            Assert.False(without.SameAs(with));
        }

        /// <summary>ChooseLighting's step 0 comes ahead of every other.</summary>
        [Fact]
        public void OpenRgb_ComesAheadOfEveryOtherChoice()
        {
            var shared = new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") };
            var native = new List<PeripheralLinker.Candidate> { new(new OutputPath(OutputFamily.RazerUnit, "r"), 0, false) };
            var lamps = new List<PeripheralLinker.Candidate> { new(new OutputPath(OutputFamily.LampArrayUnit, "l"), 0, true) };
            var openRgb = new List<PeripheralLinker.Candidate> { new(new OutputPath(OutputFamily.OpenRgbDevice, "o"), 0, false) };
            foreach (bool program in new[] { false, true })
            {
                Assert.Equal(PeripheralLinker.LightingChoice.OpenRgb,
                    PeripheralLinker.ChooseLighting(program, shared, true, native, lamps, out bool closed, openRgb));
                Assert.False(closed);
                Assert.NotEqual(PeripheralLinker.LightingChoice.OpenRgb,
                    PeripheralLinker.ChooseLighting(program, shared, true, native, lamps, out _,
                        new List<PeripheralLinker.Candidate>()));
            }
        }

        [Fact]
        public void TheOpenRgbProgram_IsOpenRgbExe()
        {
            Assert.Equal("OpenRGB", VendorPrograms.OpenRgb.Name);
            Assert.Equal(new[] { "OpenRGB" }, VendorPrograms.OpenRgb.Processes);
            Assert.Contains(VendorPrograms.OpenRgb, VendorPrograms.All);
            var presence = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "OpenRGB.exe" });
            Assert.Equal(new[] { VendorPrograms.OpenRgb }, presence.Programs.Programs);
            Assert.True(new OutputPath(OutputFamily.OpenRgbDevice, "x").Shared == false);
        }
    }

    /// <summary>
    /// The OpenRGB route against a scripted server (#557): when it
    /// connects, what it lists, what goes out for a claim, and how a
    /// controller is handed back.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public partial class OpenRgbBackendTests : IDisposable
    {
        private static readonly Guid Box = new("33333333-4444-5555-6666-777777777777");
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000d1");

        private readonly FakeOpenRgbServer _server = new();
        private readonly List<FakeOpenRgbLink> _links = new();
        private volatile bool _runs = true;
        private volatile int _port = OpenRgbProtocol.DefaultPort;
        private volatile System.Net.IPAddress _host = System.Net.IPAddress.Loopback;

        /// <summary>Set: the worker's next look at whether OpenRGB runs
        /// holds until it is reset, and <see cref="_held"/> says it is
        /// there.</summary>
        private volatile ManualResetEventSlim _hold;
        private readonly ManualResetEventSlim _held = new(false);

        private bool Runs()
        {
            var hold = _hold;
            if (hold != null)
            {
                _held.Set();
                hold.Wait(30000);
            }
            return _runs;
        }
        private long _tick = 1_000_000;
        private int _connects;

        public OpenRgbBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            OpenRgbBackend.ForgetCarriedForTest();
            _server.Controllers.Add(OpenRgbSamples.Keyboard());
            _server.Controllers.Add(OpenRgbSamples.Mousepad());
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private IOpenRgbLink Connect(OpenRgbServerAddress address, int timeoutMs)
        {
            Interlocked.Increment(ref _connects);
            Assert.Equal(_port, address.Port);
            Assert.Equal(_host, address.Host);
            Assert.Equal(OpenRgbBackend.ConnectTimeoutMs, timeoutMs);
            if (_server.Down) return null;
            var link = _server.Link();
            lock (_links) _links.Add(link);
            return link;
        }

        private OpenRgbBackend Unstarted(int stopJoinMs = OpenRgbBackend.StopJoinMs)
            => new OpenRgbBackend(Connect, _ => Box, Tick, Runs, () => new OpenRgbServerAddress(_host, _port),
                idleTickMs: 5, stopJoinMs: stopJoinMs, versionWaitMs: 50, replyWaitMs: 50);

        private OpenRgbBackend Started(int stopJoinMs = OpenRgbBackend.StopJoinMs)
        {
            var backend = Unstarted(stopJoinMs);
            backend.Start();
            backend.Rescan(new[]
            {
                new HidCollectionInfo { Path = OpenRgbSamples.KeyboardPath.ToUpperInvariant(), VendorId = 0x1532, ProductId = 0x024E },
            });
            Passes(backend);
            return backend;
        }

        private static void Passes(OpenRgbBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                backend.Wake();
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        private static OutputPath KeyboardPath => new(OutputFamily.OpenRgbDevice, OpenRgbSamples.Keyboard().Location);

        private static void Link(params OutputPath[] paths)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = paths } }));

        private static void Light(int rgb, KeyFrame keys = null)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, keys: keys);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        /// <summary>The UpdateLEDs packet for the keyboard's eight LEDs,
        /// written out from RGBController_Network.cpp:522-561.</summary>
        private static string Leds(uint index, params int[] rgb)
        {
            var data = new List<byte>();
            U32(data, (uint)(6 + 4 * rgb.Length));
            U16(data, rgb.Length);
            foreach (int c in rgb) U32(data, (uint)(((c >> 16) & 0xFF) | (c & 0xFF00) | ((c & 0xFF) << 16)));
            return Hex(Packet(index, 1050, data.ToArray()));
        }

        /// <summary>The UpdateMode packet at version 5,
        /// RGBController_Network.cpp:565-613.</summary>
        private static string ModePacket(uint index, int modeIndex, OpenRgbMode mode)
        {
            var body = new List<byte>();
            Mode(body, mode, 5);
            var data = new List<byte>();
            U32(data, (uint)(8 + body.Count));
            U32(data, (uint)modeIndex);
            data.AddRange(body);
            return Hex(Packet(index, 1101, data.ToArray()));
        }

        private List<string> Writes()
            => _server.Packets().Where(p => BitConverter.ToUInt32(p, 8) is 1050 or 1101).Select(p => Hex(p)).ToList();

        private static int[] Repeat(int rgb, int count = 8) => Enumerable.Repeat(rgb, count).ToArray();

        private OpenRgbBackend Lit(int rgb = 0x112233, int stopJoinMs = OpenRgbBackend.StopJoinMs)
        {
            var backend = Started(stopJoinMs);
            Link(KeyboardPath);
            Light(rgb);
            Passes(backend);
            return backend;
        }

        [Fact]
        public void WhileOpenRgbDoesNotRun_NothingIsConnected_AndTheFirstPassSaysScanned()
        {
            _runs = false;
            using var backend = Started();
            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(0, Volatile.Read(ref _connects));
            Advance(OpenRgbBackend.RetryMs * 2);
            Passes(backend);
            Assert.Equal(0, Volatile.Read(ref _connects));
        }

        /// <summary>The server is asked for its list and nothing else: every
        /// controller is listed with the device its location names, and the
        /// colorable one's lights are published.</summary>
        [Fact]
        public void TheList_ListsEveryController_WithItsDevice_AndWritesNothing()
        {
            using var backend = Started();
            var units = backend.Snapshot.Units;
            Assert.Equal(2, units.Count);
            var keyboard = units[0];
            Assert.Equal(OutputFamily.OpenRgbDevice, keyboard.Family);
            Assert.Equal(OpenRgbSamples.Keyboard().Location, keyboard.Key);
            Assert.Equal(Box, keyboard.ContainerId);
            Assert.Equal(((ushort)0x1532, (ushort)0x024E), (keyboard.VendorId, keyboard.ProductId));
            Assert.Equal("Razer BlackWidow V3", keyboard.Name);
            Assert.False(keyboard.NoDirectMode || keyboard.Asleep);
            Assert.Equal(0, keyboard.Kind);
            Assert.True(units[1].NoDirectMode);
            Assert.Equal(8, PeripheralLightMaps.For(KeyboardPath).Lights.Count);
            Assert.Null(PeripheralLightMaps.For(new OutputPath(OutputFamily.OpenRgbDevice, units[1].Key)));
            Assert.Empty(Writes());
            Assert.Equal("PadForge", _server.ClientName);
        }

        /// <summary>A claim's first pass reads the controller again, sets
        /// its Direct mode as the server described it, and sends the
        /// picture. An unchanged picture is not sent again.</summary>
        [Fact]
        public void AClaim_SetsTheDirectMode_ThenSendsThePicture_Once()
        {
            using var backend = Lit(0x112233);
            Assert.Equal(new[] { ModePacket(0, 0, OpenRgbSamples.Direct()), Leds(0, Repeat(0x112233)) }, Writes());
            Assert.Single(backend.Recorded);
            Advance(1000);
            Passes(backend);
            Assert.Equal(2, Writes().Count);
        }

        /// <summary>Key colors reach their keys, the background the rest, and
        /// a change goes out no sooner than <see cref="OpenRgbBackend.RepaintMs"/>
        /// after the picture before.</summary>
        [Fact]
        public void KeyColors_ReachTheirKeys_AndChangesArePaced()
        {
            using var backend = Lit(0x112233);
            _server.Clear();
            var keys = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0xFF0000 },
                new Dictionary<string, int> { [LightGroups.Logo] = 0x00FF00 }, null, null);
            Light(0x112233, keys);
            Passes(backend);
            Assert.Empty(Writes());
            Advance(OpenRgbBackend.RepaintMs);
            Passes(backend);
            Assert.Equal(new[] { Leds(0, 0x112233, 0x112233, 0xFF0000, 0x112233, 0x112233, 0x00FF00, 0x112233, 0x112233) },
                Writes());
        }

        /// <summary>The claim ends: the mode the controller was in goes back,
        /// and for a mode with no color per LED, nothing more. Its own
        /// lighting returns.</summary>
        [Fact]
        public void AClaimThatEnds_SetsBackTheModeTheControllerWasIn()
        {
            using var backend = Lit();
            _server.Clear();
            Unlight();
            Passes(backend);
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A controller that was in Direct mode already gets its
        /// colors back after its mode.</summary>
        [Fact]
        public void AControllerThatWasInDirectMode_GetsItsColorsBack()
        {
            _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 0);
            using var backend = Lit();
            _server.Clear();
            Link();
            Passes(backend);
            var original = OpenRgbSamples.Keyboard().Colors.Select(OpenRgbProtocol.FromRgbColor).ToArray();
            Assert.Equal(new[] { ModePacket(0, 0, OpenRgbSamples.Direct()), Leds(0, original) }, Writes());
        }

        [Fact]
        public void AControllerWithNoDirectMode_IsNeverWritten()
        {
            using var backend = Started();
            var pad = new OutputPath(OutputFamily.OpenRgbDevice, OpenRgbSamples.Mousepad().Location);
            int reads = _server.Packets(OpenRgbProtocol.RequestControllerData).Count;
            Link(pad);
            Light(0x445566);
            Passes(backend, 3);
            Assert.Empty(Writes());
            Assert.Empty(backend.Recorded);
            // Nor read again for the claim: a claim never reaches it.
            Assert.Equal(reads, _server.Packets(OpenRgbProtocol.RequestControllerData).Count);
        }

        /// <summary>OpenRGB stops: its controllers leave the list and nothing
        /// is sent, since the server went with it.</summary>
        [Fact]
        public void WhenOpenRgbStops_ItsControllersLeave_WithNothingSent()
        {
            using var backend = Lit();
            _server.Clear();
            _runs = false;
            Passes(backend);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Empty(Writes());
            Assert.Empty(backend.Recorded);
            Assert.Null(PeripheralLightMaps.For(KeyboardPath));
            lock (_links) Assert.True(_links.Last().Disposed);
        }

        /// <summary>The server stops answering while OpenRGB runs: the
        /// controllers stay listed asleep, and when it answers again the
        /// hand-back it was owed goes out first.</summary>
        [Fact]
        public void AServerThatStopsAnswering_LeavesItsControllersAsleep_AndIsOwedItsHandBack()
        {
            using var backend = Lit();
            _server.Down = true;
            Passes(backend);
            Assert.All(backend.Snapshot.Units, u => Assert.True(u.Asleep));
            Assert.Empty(backend.Recorded);
            int connects = Volatile.Read(ref _connects);

            // Nothing is tried again before the retry interval.
            Passes(backend);
            Assert.Equal(connects, Volatile.Read(ref _connects));

            // The claim ended meanwhile, and the server answers again.
            Unlight();
            _server.Down = false;
            _server.Clear();
            Advance(OpenRgbBackend.RetryMs);
            Passes(backend, 3);
            Assert.All(backend.Snapshot.Units, u => Assert.False(u.Asleep));
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
            _server.Clear();
            Passes(backend);
            Assert.Empty(Writes());
        }

        /// <summary>A claim that comes back after the connection was lost
        /// takes the owed hand-back over: it is the one that sets the
        /// controller back when this claim ends.</summary>
        [Fact]
        public void AClaimAfterALostConnection_KeepsTheOriginalHandBack()
        {
            using var backend = Lit();
            _server.Down = true;
            Passes(backend);
            _server.Down = false;
            // The server now says the controller is in Direct mode, as
            // PadForge left it.
            _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 0);
            Advance(OpenRgbBackend.RetryMs);
            Passes(backend, 3);
            _server.Clear();
            Unlight();
            Passes(backend);
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
        }

        /// <summary>The worker connects where the address names, and the tab
        /// is told the port it tried.</summary>
        [Fact]
        public void TheWorker_ConnectsToTheAddressItFinds_AndPublishesThePort()
        {
            _host = System.Net.IPAddress.Parse("192.168.1.5");
            _port = 6800;
            using var backend = Started();
            Assert.Equal(1, Volatile.Read(ref _connects));
            Assert.Equal(6800, PeripheralOutputs.OpenRgbPortTried);
        }

        /// <summary>A hand-back that does not go out at the stop is logged
        /// and stays owed to the next run.</summary>
        [Fact]
        public void AHandBackThatFailsAtTheStop_IsLogged_AndPaidByTheNextRun()
        {
            using var backend = Lit();
            string line = "PERIPHERAL OpenRGB 'Razer BlackWidow V3' hand-back did not go out at the stop";
            int before = SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(line));
            lock (_links) _links.Last().RefuseSends = true;
            backend.Stop();
            Assert.Equal(before + 1, SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(line)));

            Unlight();
            _server.Clear();
            backend.Start();
            Passes(backend, 3);
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
        }

        /// <summary>The stop arrives in the pass that finds the server again,
        /// so that pass pays nothing. The worker's way out pays the owed
        /// hand-back on the session it holds.</summary>
        [Fact]
        public void AStopInThePassThatFindsTheServerAgain_PaysTheOwedHandBack()
        {
            var backend = Lit();
            _server.Down = true;
            Passes(backend);
            Unlight();
            _server.Down = false;
            _server.Clear();
            Advance(OpenRgbBackend.RetryMs);
            var hold = new ManualResetEventSlim(false);
            _held.Reset();
            _hold = hold;
            backend.Wake();
            Assert.True(_held.Wait(30000), "the worker is held");
            backend.SignalStop();
            _hold = null;
            hold.Set();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
            backend.Dispose();
        }

        /// <summary>A stop that comes during the read a claim starts with enters nothing:
        /// a worker a stop gave up on comes back from that read with the next run on the
        /// controller.</summary>
        [Fact]
        public void AStopDuringAClaimsRead_SetsNoMode()
        {
            using var backend = Started();
            _server.Clear();
            int asked = 0;
            _server.ControllerDataAsked = () =>
            {
                Interlocked.Increment(ref asked);
                backend.SignalStop();
            };
            Link(KeyboardPath);
            Light(0x112233);
            backend.Wake();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.True(Volatile.Read(ref asked) > 0, "the claim read the controller");
            Assert.Empty(Writes());
        }

        /// <summary>A worker a stop gave up on comes out while the next run's
        /// worker still waits for it, with no connection of its own. It
        /// sets the controller back, since nobody else knows it was
        /// entered.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_SetsTheControllerBack_WhileItsSuccessorWaitsForIt()
        {
            using var backend = Lit();
            _server.Clear();
            var hold = new ManualResetEventSlim(false);
            _held.Reset();
            _hold = hold;
            backend.Wake();
            Assert.True(_held.Wait(30000), "the worker is held");
            int connects;
            using var next = Unstarted();
            try
            {
                backend.Dispose();
                Unlight();
                connects = Volatile.Read(ref _connects);
                _hold = null;
                next.Start();
            }
            finally
            {
                hold.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _connects) > connects, 30000), "the new worker connected");
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
        }

        /// <summary>A worker a stop gave up on comes out after the next run's
        /// worker has left its wait and entered the controller on a backend of
        /// its own, as each engine start has it. The old worker sets nothing
        /// back and the map stays up.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_SendsTheControllerNothing_OnceTheNextRunEnteredIt()
        {
            using var backend = Lit(stopJoinMs: 50);
            var hold = new ManualResetEventSlim(false);
            _held.Reset();
            _hold = hold;
            backend.Wake();
            Assert.True(_held.Wait(30000), "the worker is held");
            OpenRgbBackend next = null;
            try
            {
                backend.Dispose();
                _hold = null;
                next = Started(stopJoinMs: 50);
                Passes(next, 3);
                _server.Clear();
            }
            finally
            {
                hold.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the old worker ended");
            Assert.Empty(Writes());
            Assert.NotNull(PeripheralLightMaps.For(KeyboardPath));
            next.Dispose();
        }

        /// <summary>The next run of a worker a stop gave up on finds its Direct mode on
        /// the controller, and sets back what that worker read, not that.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_LeavesWhatItReadToTheNextRun()
        {
            using var backend = Lit(stopJoinMs: 50);
            var hold = new ManualResetEventSlim(false);
            _held.Reset();
            _hold = hold;
            backend.Wake();
            Assert.True(_held.Wait(30000), "the worker is held");
            OpenRgbBackend next = null;
            try
            {
                backend.Dispose();
                _hold = null;
                _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 0);
                next = Started(stopJoinMs: 50);
                Passes(next, 3);
                _server.Clear();
                next.Dispose();
                Assert.Contains(ModePacket(0, 1, OpenRgbSamples.Static()), Writes());
            }
            finally
            {
                hold.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the old worker ended");
        }

        /// <summary>The engine stops with the server not answering, so the
        /// hand-back cannot go out. The next run pays it once the server
        /// answers, and does not read the Direct mode PadForge left as the
        /// controller's own.</summary>
        [Fact]
        public void AHandBackOwedAtAStopWithNoServer_IsPaidByTheNextRun()
        {
            using var backend = Lit();
            _server.Down = true;
            Passes(backend);
            backend.Dispose();
            Unlight();

            _server.Down = false;
            _server.Clear();
            _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 0);
            using var next = Started();
            Passes(next, 3);
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
        }

        /// <summary>A run that never reaches the server leaves what the run
        /// before it carried, and the run that reaches it pays that instead of
        /// reading the Direct mode PadForge left as the controller's own.</summary>
        [Fact]
        public void AHandBackCarried_SurvivesARunThatNeverReachesTheServer()
        {
            using var backend = Lit();
            _server.Down = true;
            Passes(backend);
            backend.Dispose();
            Unlight();

            using var neverReached = Started();
            Passes(neverReached);
            neverReached.Dispose();

            _server.Down = false;
            _server.Clear();
            _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 0);
            using var next = Started();
            Passes(next, 3);
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
        }

        /// <summary>A claim the next run finds standing takes the owed
        /// hand-back over: it sets the controller back when it ends.</summary>
        [Fact]
        public void AClaimInTheNextRun_TakesOverTheHandBackOwedAtTheStop()
        {
            using var backend = Lit();
            _server.Down = true;
            Passes(backend);
            backend.Dispose();

            _server.Down = false;
            _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 0);
            using var next = Started();
            Passes(next, 3);
            _server.Clear();
            Unlight();
            Passes(next);
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
        }

        /// <summary>OpenRGB stopped after the engine did: its hold went with
        /// it, so the next run owes nothing to the OpenRGB that starts
        /// later.</summary>
        [Fact]
        public void AHandBackOwedAtAStop_IsDropped_WhenOpenRgbStopsBeforeTheNextRunFindsIt()
        {
            using var backend = Lit();
            _server.Down = true;
            Passes(backend);
            backend.Stop();
            Unlight();

            _runs = false;
            backend.Start();
            Passes(backend);
            _runs = true;
            _server.Down = false;
            _server.Clear();
            Advance(OpenRgbBackend.RetryMs);
            Passes(backend, 3);
            Assert.Empty(Writes());
        }

        /// <summary>OpenRGB deleted and registered a lit controller again
        /// under the same location, with the list read landing after the new
        /// registration (DetectionManager.cpp:2167-2200). The new controller
        /// is in another mode: the worker sets its Direct mode again, paints
        /// the picture again, and keeps the hand-back it held.</summary>
        [Fact]
        public void AControllerListedInAnotherMode_IsSetToDirectAgain_AndKeepsItsHandBack()
        {
            using var backend = Lit(0x112233);
            _server.Clear();
            _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 2);
            lock (_links) _links.Last().Notice();
            Passes(backend, 3);
            Assert.Equal(new[] { ModePacket(0, 0, OpenRgbSamples.Direct()), Leds(0, Repeat(0x112233)) }, Writes());
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Assert.Single(backend.Recorded).Packets.Select(p => Hex(p)));
        }

        /// <summary>The server names the Direct mode the worker set, so a
        /// list read changes nothing on the device.</summary>
        [Fact]
        public void AControllerListedInItsDirectMode_IsNotSetAgain()
        {
            using var backend = Lit(0x112233);
            _server.Clear();
            _server.Controllers[0] = OpenRgbSamples.Keyboard(active: 0);
            lock (_links) _links.Last().Notice();
            Passes(backend, 3);
            Assert.Empty(Writes());
        }

        /// <summary>The list changed: a controller listed at another index
        /// keeps its claim and is addressed at its new index.</summary>
        [Fact]
        public void AListThatChanges_KeepsAControllersClaim_AtItsNewIndex()
        {
            using var backend = Lit(0x112233);
            _server.Controllers.Insert(0, OpenRgbSamples.Mouse());
            lock (_links) _links.Last().Notice();
            Passes(backend, 3);
            var keys = backend.Snapshot.Units.Select(u => u.Key).ToArray();
            Assert.Equal(3, keys.Length);
            Assert.Equal(OpenRgbSamples.Mouse().Location, keys[2]);
            _server.Clear();
            Advance(OpenRgbBackend.RepaintMs);
            Light(0x223344);
            Passes(backend);
            Assert.Equal(new[] { Leds(1, Repeat(0x223344)) }, Writes());
            // The crash path's record addresses it at its new index too.
            Assert.Equal(1u, BitConverter.ToUInt32(Assert.Single(backend.Recorded).Packets[0], 4));
        }

        /// <summary>The worker's finally hands back what it holds.</summary>
        [Fact]
        public void Stop_HandsBackWhatTheWorkerHolds()
        {
            var backend = Lit();
            _server.Clear();
            backend.Dispose();
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
            Assert.False(backend.Snapshot.Scanned);
            Assert.Null(PeripheralLightMaps.For(KeyboardPath));
        }

        /// <summary>The crash path sends each hand-back on record when the
        /// worker did not stop in time: here the worker is held inside a
        /// pass, so its finally cannot send first.</summary>
        [Fact]
        public void TheCrashPath_SendsWhatIsOnRecord()
        {
            using var backend = Lit();
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, record.Packets.Select(p => Hex(p)));
            _server.Clear();
            var hold = new ManualResetEventSlim(false);
            _held.Reset();
            _hold = hold;
            backend.Wake();
            Assert.True(_held.Wait(30000), "the worker is held");
            try
            {
                backend.ReleaseClaimedNow(Tick() + 1000);
                Assert.Equal(new[] { ModePacket(0, 1, OpenRgbSamples.Static()) }, Writes());
                Assert.Empty(backend.Recorded);
            }
            finally
            {
                _hold = null;
                hold.Set();
            }
        }

        /// <summary>A laptop's own keyboard sits in the PC's container and
        /// names the shared worker's key, and a serial device names its
        /// port's.</summary>
        [Fact]
        public void ALaptopKeyboardAndASerialDevice_NameTheUnitsTheyHold()
        {
            var tuf = OpenRgbSamples.Mouse();
            _server.Controllers.Clear();
            _server.Controllers.Add(new OpenRgbController
            {
                Type = OpenRgbProtocol.DeviceLaptop, Name = "ASUS TUF Laptop", Location = @"\\.\ATKACPI",
                Modes = new[] { OpenRgbSamples.Direct() }, Zones = tuf.Zones, Leds = tuf.Leds, Colors = tuf.Colors,
            });
            _server.Controllers.Add(new OpenRgbController
            {
                Type = OpenRgbProtocol.DeviceKeyboard, Name = "Dygma Raise", Location = "COM: COM7",
                Modes = new[] { OpenRgbSamples.Direct() }, Zones = tuf.Zones, Leds = tuf.Leds, Colors = tuf.Colors,
            });
            using var backend = Started();
            var laptop = backend.Snapshot.Units[0];
            Assert.Equal(PeripheralLinker.LocalMachineContainer, laptop.ContainerId);
            Assert.True(laptop.BuiltInKeyboard);
            Assert.Equal(AsusTufLaptopLighting.Key, laptop.HeldKey);
            Assert.Equal(((ushort)0, (ushort)0, 0), (laptop.VendorId, laptop.ProductId, laptop.Kind));
            var raise = backend.Snapshot.Units[1];
            Assert.Equal(Guid.Empty, raise.ContainerId);
            Assert.False(raise.BuiltInKeyboard);
            Assert.Equal(@"\\.\COM7", raise.HeldKey);
        }

        /// <summary>A mouse behind a receiver carries its kind, so the
        /// linker gives it the receiver's mouse rows alone.</summary>
        [Fact]
        public void AControllerBehindAReceiver_CarriesItsKind()
        {
            _server.Controllers.Add(OpenRgbSamples.Mouse());
            using var backend = Started();
            var mouse = backend.Snapshot.Units[2];
            Assert.Equal(PeripheralLinker.LampArrayKindMouse, mouse.Kind);
            Assert.Equal(((ushort)0x046D, (ushort)0xC539), (mouse.VendorId, mouse.ProductId));
        }

        /// <summary>The controller at the unit's index turned out to be
        /// another when the claim read it: nothing is written, and the list
        /// is read again.</summary>
        [Fact]
        public void AControllerThatTurnsOutToBeAnother_IsNotEntered()
        {
            using var backend = Started();
            _server.Controllers[0] = OpenRgbSamples.Mouse();
            Link(KeyboardPath);
            Light(0x112233);
            Passes(backend, 3);
            Assert.Empty(Writes());
            Assert.Empty(backend.Recorded);
            Assert.Contains(backend.Snapshot.Units, u => u.Key == OpenRgbSamples.Mouse().Location);
            Assert.DoesNotContain(backend.Snapshot.Units, u => u.Key == OpenRgbSamples.Keyboard().Location);
        }

        /// <summary>Two controllers with one location are told apart by a
        /// number after the first.</summary>
        [Fact]
        public void TwoControllersAtOneLocation_HaveKeysOfTheirOwn()
        {
            var keys = OpenRgbBackend.KeysOf(new[]
            {
                OpenRgbSamples.Keyboard(), OpenRgbSamples.Keyboard(), new OpenRgbController { Name = "Bridge" },
            });
            Assert.Equal(new[] { OpenRgbSamples.Keyboard().Location, OpenRgbSamples.Keyboard().Location + " #2", "name: Bridge" }, keys);
        }
    }

    /// <summary>The Lighting tab's line for a row lit through OpenRGB
    /// (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public partial class OpenRgbRouteLineTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000e1");

        public OpenRgbRouteLineTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralOutputs.SetRunning(true);
        }

        public void Dispose() => PeripheralOutputs.ResetForTest();

        /// <summary>A device only OpenRGB reaches is lit through its server
        /// and nothing more is promised. One whose own path OpenRGB took is
        /// lit on that path once OpenRGB closes, and the line says so, but
        /// for a LampArray unit Windows lights, which PadForge leaves
        /// alone.</summary>
        [Fact]
        public void ARowLitThroughOpenRgb_SaysTheColorsGoThroughOpenRgb()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var path = new OutputPath(OutputFamily.OpenRgbDevice, OpenRgbSamples.Keyboard().Location);
            var razer = new OutputPath(OutputFamily.RazerUnit, "razer");
            var lamps = new OutputPath(OutputFamily.LampArrayUnit, "lamps");
            void Link(params OutputPath[] taken) => PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = A, Lighting = new[] { path }, TakenByOpenRgb = taken },
            }));
            var ud = new PadForge.Engine.Data.UserDevice
            {
                InstanceGuid = A, IsOnline = true, CapType = InputDeviceType.Keyboard, VendorId = PeripheralLinker.RazerVid,
                PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
            };

            Link();
            Assert.Equal(s.Pad_Lighting_RouteThroughOpenRgbOnly, PeripheralRouteText.Lighting(ud, 0));
            Link(razer);
            Assert.Equal(s.Pad_Lighting_RouteThroughOpenRgb, PeripheralRouteText.Lighting(ud, 0));
            Assert.Null(PeripheralRouteText.Lighting(ud, 0, lightsHere: false));

            // A LampArray unit Windows lights is no path of PadForge's own.
            PeripheralOutputs.Direct = new DirectSnapshot(new[]
            {
                new DirectUnit(OutputFamily.LampArrayUnit, "lamps", Guid.Empty, 0x1532, 0x0001) { WindowsLit = true },
            }, new[] { OutputFamily.LampArrayUnit });
            Link(lamps);
            Assert.Equal(s.Pad_Lighting_RouteThroughOpenRgbOnly, PeripheralRouteText.Lighting(ud, 0));
            Link(lamps, razer);
            Assert.Equal(s.Pad_Lighting_RouteThroughOpenRgb, PeripheralRouteText.Lighting(ud, 0));
            // Once Windows lets it go, PadForge lights it.
            PeripheralOutputs.Direct = new DirectSnapshot(new[]
            {
                new DirectUnit(OutputFamily.LampArrayUnit, "lamps", Guid.Empty, 0x1532, 0x0001),
            }, new[] { OutputFamily.LampArrayUnit });
            Link(lamps);
            Assert.Equal(s.Pad_Lighting_RouteThroughOpenRgb, PeripheralRouteText.Lighting(ud, 0));
            // A unit no backend lists now is lit on its own when it comes back.
            PeripheralOutputs.Direct = DirectSnapshot.Empty;
            Assert.Equal(s.Pad_Lighting_RouteThroughOpenRgb, PeripheralRouteText.Lighting(ud, 0));
        }

        /// <summary>A device OpenRGB holds with no Direct mode is left to
        /// OpenRGB (PeripheralLinker.LinkOpenRgb), and its line says why,
        /// never that PadForge lights it once a program closes.</summary>
        [Fact]
        public void ARowLeftToOpenRgb_SaysItHasNoDirectMode()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = A, LeftToProgram = "OpenRGB" } }));
            var ud = new PadForge.Engine.Data.UserDevice
            {
                InstanceGuid = A, IsOnline = true, CapType = InputDeviceType.Keyboard, VendorId = PeripheralLinker.RazerVid,
                PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
            };
            Assert.Equal(s.Pad_Lighting_RouteLeftToOpenRgb, PeripheralRouteText.Lighting(ud, 0));
            Assert.Null(PeripheralRouteText.Lighting(ud, 0, lightsHere: false));
            Assert.Equal("OpenRGB", VendorPrograms.OpenRgb.Name);
        }
    }
}
