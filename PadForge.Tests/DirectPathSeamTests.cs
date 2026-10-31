using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    // ─────────────────────────────────────────────
    //  Test doubles the direct backends share
    // ─────────────────────────────────────────────

    /// <summary>A direct backend that finds what a test tells it and writes
    /// every call the host makes into a journal, which several backends can
    /// share so the order across them shows (#494).</summary>
    internal sealed class FakeDirectBackend : IDirectUnitBackend
    {
        private readonly string _name;
        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private IReadOnlyList<HidCollectionInfo> _present;
        private int _rescans;
        private int _snapshotReads;

        public FakeDirectBackend(OutputFamily family, string name = null, ConcurrentQueue<string> journal = null)
        {
            Family = family;
            _name = name ?? family.ToString();
            Journal = journal ?? new ConcurrentQueue<string>();
        }

        public OutputFamily Family { get; }

        public DirectBackendSnapshot Snapshot
        {
            get
            {
                Interlocked.Increment(ref _snapshotReads);
                SnapshotRead?.Invoke();
                var fault = SnapshotFault;
                if (fault != null) throw fault();
                return NoSnapshot ? null : Volatile.Read(ref _snapshot);
            }
        }

        public event Action SnapshotChanged;
        public event Action RescanRequested;

        /// <summary>The backend asks the host for a walk, as a worker does
        /// after a failed write.</summary>
        public void AskForRescan() => RescanRequested?.Invoke();

        public ConcurrentQueue<string> Journal { get; }
        public int Rescans => Volatile.Read(ref _rescans);
        public IReadOnlyList<HidCollectionInfo> Present => Volatile.Read(ref _present);

        /// <summary>How often the host read the snapshot.</summary>
        public int SnapshotReads => Volatile.Read(ref _snapshotReads);

        /// <summary>Runs inside each read of the snapshot.</summary>
        public Action SnapshotRead;

        /// <summary>A backend whose snapshot read throws what this makes,
        /// while it is set.</summary>
        public volatile Func<Exception> SnapshotFault;

        /// <summary>A backend whose snapshot read gives null, while set.</summary>
        public volatile bool NoSnapshot;

        /// <summary>Runs inside WaitForSilence, for a test that moves its
        /// clock while a backend waits.</summary>
        public Action<int> Waiting;

        /// <summary>A backend whose start throws.</summary>
        public bool FailStart { get; init; }

        /// <summary>What a scan found, published the way a worker does:
        /// one new snapshot, then the notice.</summary>
        public void Found(params DirectUnit[] units)
        {
            Volatile.Write(ref _snapshot, new DirectBackendSnapshot(units));
            SnapshotChanged?.Invoke();
        }

        public void Start()
        {
            Journal.Enqueue("start " + _name);
            if (FailStart) throw new InvalidOperationException("scripted");
        }

        public void Stop() => Journal.Enqueue("stop " + _name);

        public void Rescan(IReadOnlyList<HidCollectionInfo> present)
        {
            Volatile.Write(ref _present, present);
            Interlocked.Increment(ref _rescans);
            Journal.Enqueue("rescan " + _name);
        }

        public void SignalStop() => Journal.Enqueue("signal " + _name);
        public void ReleaseClaimedNow(long deadline) => Journal.Enqueue($"release {_name} {deadline}");

        public void WaitForSilence(int waitMs)
        {
            Journal.Enqueue($"silence {_name} {waitMs}");
            Waiting?.Invoke(waitMs);
        }

        public void Dispose() => Journal.Enqueue("dispose " + _name);
    }

    /// <summary>A scripted HID report channel for a backend that speaks
    /// feature and output reports (#494). It records each call as its kind
    /// ("set", "get", "out", "in"), the caller's bytes before any padding
    /// and the tick it came at, answers a feature or input read from a
    /// script, and fails every write while <see cref="FailWrites"/> is
    /// set.</summary>
    internal sealed class FakeReportChannel : IHidReportChannel
    {
        public string Path { get; init; } = @"\\?\hid#vid_1532&pid_0084&mi_00#fake";
        public bool FeatureOnly { get; init; }
        public int InputReportLength { get; init; }
        public int OutputReportLength { get; init; }
        public int FeatureReportLength { get; init; } = 91;

        public Func<long> Ticks = () => Environment.TickCount64;
        public volatile bool FailWrites;
        public volatile bool Disposed;
        public readonly ConcurrentQueue<(string Kind, byte[] Bytes, long Tick)> Calls = new();

        /// <summary>The feature report to answer for an ID, or null for a
        /// read that fails.</summary>
        public Func<byte, byte[]> Feature = _ => null;

        /// <summary>The next input report, or null when none comes in time.</summary>
        public Func<byte[]> Input = () => null;

        public bool SetFeature(byte[] report)
        {
            if (FailWrites || Disposed || report == null) return false;
            Calls.Enqueue(("set", (byte[])report.Clone(), Ticks()));
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            if (FailWrites || Disposed || buffer == null) return false;
            Calls.Enqueue(("get", new[] { reportId }, Ticks()));
            byte[] answer = Feature(reportId);
            if (answer == null) return false;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (FeatureOnly || FailWrites || Disposed || report == null) return false;
            Calls.Enqueue(("out", (byte[])report.Clone(), Ticks()));
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            if (FeatureOnly || Disposed || buffer == null) return -1;
            Calls.Enqueue(("in", Array.Empty<byte>(), Ticks()));
            byte[] answer = Input();
            if (answer == null) return 0;
            int copied = Math.Min(answer.Length, buffer.Length);
            Array.Copy(answer, buffer, copied);
            return copied;
        }

        public void Dispose() => Disposed = true;
    }

    // ─────────────────────────────────────────────
    //  The linker: pure rules, no shared state
    // ─────────────────────────────────────────────

    /// <summary>How a row comes by a direct path (#494): which family lights
    /// it, which units are its own, and which lit units no row reaches.</summary>
    public class DirectPathLinkingTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid C = new("00000000-0000-0000-0000-0000000000c3");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid OtherBox = new("99999999-2222-3333-4444-555555555555");
        private static readonly Guid Pc = PeripheralLinker.LocalMachineContainer;

        private const ushort Logitech = PeripheralLinker.LogitechVid;
        private const ushort Razer = PeripheralLinker.RazerVid;
        private const ushort SteelSeries = PeripheralLinker.SteelSeriesVid;
        private const ushort Lenovo = 0x048D;

        private static LinkRow Row(Guid device, int capType, ushort vid, ushort pid, Guid container)
            => new(device, capType, vid, pid, container, null);

        private static DirectUnit Lamp(string key, Guid container, int kind = PeripheralLinker.LampArrayKindKeyboard,
            bool windowsLit = false, ushort vid = 0x1234, ushort pid = 0x0001)
            => new(OutputFamily.LampArrayUnit, key, container, vid, pid) { Kind = kind, WindowsLit = windowsLit };

        private static PeripheralPresence Presence(bool ledSdk = false, bool gHub = false, bool synapse = false,
            bool gg = false, bool razerRuns = false, bool ggRuns = false)
            => new PeripheralPresence(ledSdk, gHub, synapse, gg, false)
            {
                RazerSoftwareRunning = razerRuns, SteelSeriesGGRunning = ggRuns,
            };

        private static LinkInputs Inputs(PeripheralPresence presence, HidppSnapshot hidpp, params DirectUnit[] units)
            => new(hidpp ?? HidppSnapshot.Empty, presence) { Units = units };

        private static HidppSnapshot Snapshot(params HidppUnit[] units) => new(units, Array.Empty<Guid>());

        private static OutputPath P(OutputFamily family, string key) => new(family, key);
        private static OutputPath P(HidppUnit unit) => new(OutputFamily.HidppUnit, unit.Key);

        /// <summary>The class the tree lights today: a zone-only 0x8070
        /// unit.</summary>
        private static HidppUnit ZoneOnly(byte index, int type, Guid container, bool haptics = false) => new()
        {
            ChannelPath = "rx", ContainerId = container, DeviceIndex = index, DeviceType = type,
            RgbFeatureId = HidppUnitProtocol.ColorLedEffects, RgbIndex = 0x0E, Zones = new[] { new HidppZone(0, 1) },
            HapticIndex = haptics ? (byte)0x0B : (byte)0, WaveformMask = haptics ? 0x7FFFu : 0u,
        };

        /// <summary>A 0x8071 unit as a later probe describes it: its effect
        /// feature, one cluster to paint and the holds it offers.</summary>
        private static HidppUnit Planned(byte index, int type, Guid container, int level) => new()
        {
            ChannelPath = "rx", ContainerId = container, DeviceIndex = index, DeviceType = type,
            RgbFeatureId = HidppUnitProtocol.RgbEffects, RgbIndex = 0x0C,
            EffectFeatureId = HidppUnitProtocol.RgbEffects, EffectIndex = 0x0C,
            Zones = new[] { new HidppZone(0, 1) }, MaxClaimLevel = level,
        };

        /// <summary>A 0x8071 unit as the probe describes it today: RGB it
        /// cannot paint yet.</summary>
        private static HidppUnit Unplanned(byte index, int type, Guid container) => new()
        {
            ChannelPath = "rx", ContainerId = container, DeviceIndex = index, DeviceType = type,
            RgbFeatureId = HidppUnitProtocol.RgbEffects, RgbIndex = 0x0C,
        };

        // ── The families ──

        /// <summary>The backend states are an array indexed by the family,
        /// and a unit row's id is hashed from its number, so no member ever
        /// moves.</summary>
        [Fact]
        public void EveryOutputFamilyKeepsItsNumber()
        {
            Assert.Equal(0, (int)OutputFamily.HidppUnit);
            Assert.Equal(1, (int)OutputFamily.ChromaCategory);
            Assert.Equal(2, (int)OutputFamily.LedSdkType);
            Assert.Equal(3, (int)OutputFamily.GameSenseColor);
            Assert.Equal(4, (int)OutputFamily.GameSenseTactile);
            Assert.Equal(5, (int)OutputFamily.Interhaptics);
            Assert.Equal(6, (int)OutputFamily.RazerUnit);
            Assert.Equal(7, (int)OutputFamily.SteelSeriesUnit);
            Assert.Equal(8, (int)OutputFamily.LampArrayUnit);
            Assert.Equal(9, (int)OutputFamily.TouchSenseUnit);
            Assert.Equal(10, Enum.GetValues(typeof(OutputFamily)).Length);

            Assert.Equal(1, (int)PeripheralOutputKinds.Haptics);
            Assert.Equal(2, (int)PeripheralOutputKinds.Lighting);
            Assert.Equal(4, (int)PeripheralOutputKinds.LightingLevel1);
            Assert.Equal(8, (int)PeripheralOutputKinds.LightingLevel2);
        }

        /// <summary>A backend hands the link pass its units and whether it
        /// has scanned in one object, so no pass reads the units of one
        /// scan beside the scan state of another.</summary>
        [Fact]
        public void ABackendsSnapshot_HoldsItsUnitsAndItsScanStateTogether()
        {
            Assert.False(DirectBackendSnapshot.NotScanned.Scanned);
            Assert.Empty(DirectBackendSnapshot.NotScanned.Units);

            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, Razer, 0x0084);
            var found = new DirectBackendSnapshot(new[] { unit });
            Assert.True(found.Scanned);
            Assert.Equal(new[] { unit }, found.Units);
            // A scan that found nothing is still a scan, and a list never
            // given reads as an empty one.
            Assert.True(new DirectBackendSnapshot(Array.Empty<DirectUnit>()).Scanned);
            Assert.Empty(new DirectBackendSnapshot(null).Units);
            Assert.False(new DirectBackendSnapshot(new[] { unit }, scanned: false).Scanned);

            // The contract carries the one object and neither of its parts.
            Assert.Equal(new[] { nameof(IDirectUnitBackend.Family), nameof(IDirectUnitBackend.Snapshot) },
                typeof(IDirectUnitBackend).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(typeof(DirectBackendSnapshot), typeof(IDirectUnitBackend).GetProperty(nameof(IDirectUnitBackend.Snapshot)).PropertyType);
        }

        [Fact]
        public void ARowsKinds_CarryTheHoldsItsUnitsOffer()
        {
            var unit = P(OutputFamily.HidppUnit, "rx|01");
            Assert.Equal(PeripheralOutputKinds.Lighting,
                new DeviceLinks { Device = A, Lighting = new[] { unit } }.Kinds);
            Assert.Equal(PeripheralOutputKinds.Lighting | PeripheralOutputKinds.LightingLevel1,
                new DeviceLinks { Device = A, Lighting = new[] { unit }, MaxClaimLevel = 1 }.Kinds);
            Assert.Equal(PeripheralOutputKinds.Lighting | PeripheralOutputKinds.LightingLevel1 | PeripheralOutputKinds.LightingLevel2,
                new DeviceLinks { Device = A, Lighting = new[] { unit }, MaxClaimLevel = 2 }.Kinds);

            // The record keeps them, so the choice stays on the tab while the
            // device sleeps.
            var links = new DeviceLinks { Device = A, Lighting = new[] { unit }, MaxClaimLevel = 2 };
            Assert.Equal(14, PeripheralLinker.Capabilities(0, links, stillLooking: false));
            Assert.Equal(14, PeripheralLinker.Capabilities(14, null, stillLooking: true));
            Assert.Equal(0, PeripheralLinker.Capabilities(14, null, stillLooking: false));
        }

        [Fact]
        public void TheTableRepublishes_WhenOnlyAHoldAWaitingPathOrAShadowMoves()
        {
            var unit = P(OutputFamily.RazerUnit, "razer");
            var shared = P(OutputFamily.ChromaCategory, "mouse");
            LinkTable Table(DeviceLinks row) => new(new[] { row });
            var plain = new DeviceLinks { Device = A, Lighting = new[] { unit } };

            Assert.True(Table(plain).SameAs(Table(new DeviceLinks { Device = A, Lighting = new[] { unit } })));
            Assert.False(Table(plain).SameAs(Table(new DeviceLinks { Device = A, Lighting = new[] { unit }, MaxClaimLevel = 1 })));
            Assert.False(Table(plain).SameAs(Table(new DeviceLinks { Device = A, Lighting = new[] { unit }, DirectWhenClosed = true })));
            Assert.False(Table(plain).SameAs(Table(new DeviceLinks { Device = A, Lighting = new[] { unit }, Shadows = new[] { shared } })));
            Assert.False(Table(plain).SameAs(Table(new DeviceLinks { Device = A, Lighting = new[] { unit }, UnitRow = true })));
            Assert.False(Table(plain).SameAs(Table(new DeviceLinks { Device = A, Lighting = new[] { unit }, HasVendorProgram = true })));

            // A shadow is no link: nothing resolves a color on it for this row.
            var shadowed = Table(new DeviceLinks { Device = A, Lighting = new[] { unit }, Shadows = new[] { shared } });
            Assert.False(shadowed.ByPath.ContainsKey(shared));
            Assert.True(shadowed.ByPath.ContainsKey(unit));
        }

        /// <summary>A vendor row and a unit row are the rows with no input
        /// of their own. A unit row still ranks as a device.</summary>
        [Fact]
        public void AVendorRowAndAUnitRow_AreOutputOnly()
        {
            Assert.False(new DeviceLinks { Device = A }.OutputOnly);
            Assert.True(new DeviceLinks { Device = A, CatchAll = true }.OutputOnly);
            var unitRow = new DeviceLinks { Device = A, UnitRow = true };
            Assert.True(unitRow.OutputOnly);
            Assert.False(unitRow.CatchAll);
            Assert.True(typeof(IOutputOnlyRow).IsAssignableFrom(typeof(PeripheralOutputRow)));
            Assert.True(typeof(IOutputOnlyRow).IsAssignableFrom(typeof(PeripheralUnitRow)));
        }

        // ── The order every vendor is lit in ──

        [Fact]
        public void TheVendorsProgram_ComesFirst_ThenWindows_ThenTheDevicesOwnProtocol_ThenALampArrayUnit()
        {
            var shared = new[] { P(OutputFamily.ChromaCategory, "mouse") };
            var none = Array.Empty<OutputPath>();
            var native = new[] { new PeripheralLinker.Candidate(P(OutputFamily.RazerUnit, "r"), 0, false) };
            var lamp = new[] { new PeripheralLinker.Candidate(P(OutputFamily.LampArrayUnit, "l"), 0, false) };
            var litLamp = new[] { new PeripheralLinker.Candidate(P(OutputFamily.LampArrayUnit, "l"), 0, true) };
            var nothing = Array.Empty<PeripheralLinker.Candidate>();

            // 1. The program runs: its path or none, never a direct one.
            Assert.Equal(PeripheralLinker.LightingChoice.Shared,
                PeripheralLinker.ChooseLighting(true, shared, true, native, lamp, out bool waits));
            Assert.True(waits);
            Assert.Equal(PeripheralLinker.LightingChoice.None,
                PeripheralLinker.ChooseLighting(true, none, true, native, lamp, out waits));
            Assert.False(waits);
            Assert.Equal(PeripheralLinker.LightingChoice.Shared,
                PeripheralLinker.ChooseLighting(true, shared, true, nothing, nothing, out waits));
            Assert.False(waits);
            // A unit Windows lights is no path of PadForge's own to wait for.
            Assert.Equal(PeripheralLinker.LightingChoice.Shared,
                PeripheralLinker.ChooseLighting(true, shared, true, native, litLamp, out waits));
            Assert.False(waits);

            // 2. Windows lights a unit of the device: that unit, ahead of the
            // device's own protocol.
            Assert.Equal(PeripheralLinker.LightingChoice.LampArray,
                PeripheralLinker.ChooseLighting(false, shared, true, native, litLamp, out waits));
            Assert.False(waits);

            // 3 and 4. Its own protocol, then a LampArray unit.
            Assert.Equal(PeripheralLinker.LightingChoice.Native,
                PeripheralLinker.ChooseLighting(false, shared, true, native, lamp, out _));
            Assert.Equal(PeripheralLinker.LightingChoice.LampArray,
                PeripheralLinker.ChooseLighting(false, shared, true, nothing, lamp, out _));

            // 5. The shared path waits for its program where it waits at all.
            Assert.Equal(PeripheralLinker.LightingChoice.Shared,
                PeripheralLinker.ChooseLighting(false, shared, true, nothing, nothing, out _));
            Assert.Equal(PeripheralLinker.LightingChoice.None,
                PeripheralLinker.ChooseLighting(false, shared, false, nothing, nothing, out _));
            Assert.Equal(PeripheralLinker.LightingChoice.None,
                PeripheralLinker.ChooseLighting(false, none, true, nothing, nothing, out _));
        }

        /// <summary>Logitech, Razer and SteelSeries each have a program
        /// that drives every device of theirs while it runs. A row of one
        /// of them says so, whichever path lights it, so the link pass
        /// knows whose paths a starting program would take. A vendor row
        /// stands for the program itself. A unit row says so when its unit
        /// is a device of one of the three, whatever its own record
        /// carries.</summary>
        [Fact]
        public void ARowOfAVendorWhoseProgramDrivesItsDevices_SaysSo()
        {
            var lamp = Lamp("lamp", Box);
            foreach (ushort vendor in new[] { Logitech, Razer, SteelSeries })
            {
                var row = Row(A, InputDeviceType.Keyboard, vendor, 0x1234, Box);
                var links = PeripheralLinker.Build(new[] { row }, Inputs(Presence(), null, lamp)).For(A);
                Assert.Equal(new[] { lamp.Path }, links.Lighting);
                Assert.True(links.HasVendorProgram, vendor.ToString("X4"));
            }
            // Through the program's own path too.
            var category = PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Mouse, Razer, 0x0084, Box) },
                Inputs(Presence(synapse: true), null)).For(A);
            Assert.Equal(OutputFamily.ChromaCategory, category.Lighting.Single().Family);
            Assert.True(category.HasVendorProgram);

            var other = PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Keyboard, 0x0B05, 0x1A2B, Box) },
                Inputs(Presence(), null, lamp)).For(A);
            Assert.Equal(new[] { lamp.Path }, other.Lighting);
            Assert.False(other.HasVendorProgram);

            var chroma = new LinkRow(B, InputDeviceType.PeripheralLighting, 0x5046, 0x4348, Guid.Empty, PeripheralRowKind.RazerChroma);
            Assert.False(PeripheralLinker.Build(new[] { chroma }, Inputs(Presence(synapse: true), null)).For(B).HasVendorProgram);
            var unitRow = new LinkRow(C, InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = lamp.Path,
            };
            Assert.False(PeripheralLinker.Build(new[] { unitRow }, Inputs(Presence(), null, lamp)).For(C).HasVendorProgram);
            foreach (ushort vendor in new[] { Logitech, Razer, SteelSeries })
            {
                var theirs = Lamp("theirs", OtherBox, vid: vendor);
                var theirRow = unitRow with { Unit = theirs.Path };
                Assert.True(PeripheralLinker.Build(new[] { theirRow }, Inputs(Presence(), null, theirs)).For(C).HasVendorProgram,
                    vendor.ToString("X4"));
            }
        }

        // ── Logitech ──

        [Fact]
        public void ALogitechRow_WhileGHubRuns_TakesItsLedSdkType_AndSaysWhatWaitsBehindIt()
        {
            var unit = ZoneOnly(1, 3, Box);
            var row = Row(A, InputDeviceType.Mouse, Logitech, 0xC548, Box);
            var gHub = Presence(ledSdk: true, gHub: true);

            var lit = PeripheralLinker.Build(new[] { row }, Inputs(gHub, Snapshot(unit))).For(A);
            Assert.Equal(new[] { P(OutputFamily.LedSdkType, "mouse") }, lit.Lighting);
            Assert.True(lit.DirectWhenClosed);
            Assert.Equal(0, lit.MaxClaimLevel);
            Assert.Empty(lit.Shadows);

            // RGB the worker cannot paint and no LampArray unit: nothing waits.
            var unplanned = Unplanned(1, 3, Box);
            var none = PeripheralLinker.Build(new[] { row }, Inputs(gHub, Snapshot(unplanned))).For(A);
            Assert.Equal(new[] { P(OutputFamily.LedSdkType, "mouse") }, none.Lighting);
            Assert.False(none.DirectWhenClosed);

            // A LampArray unit behind it waits, unless Windows lights it.
            var lamp = Lamp("lamp", Box, kind: 2, vid: Logitech, pid: 0xC548);
            Assert.True(PeripheralLinker.Build(new[] { row }, Inputs(gHub, Snapshot(unplanned), lamp)).For(A).DirectWhenClosed);
            var windows = lamp with { WindowsLit = true };
            var behind = PeripheralLinker.Build(new[] { row }, Inputs(gHub, Snapshot(unit), windows)).For(A);
            Assert.Equal(new[] { P(OutputFamily.LedSdkType, "mouse") }, behind.Lighting);
            Assert.False(behind.DirectWhenClosed);

            // G HUB runs and its LED SDK is not installed: it holds the device
            // all the same, so nothing lights the row.
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Presence(gHub: true), Snapshot(unit), lamp)).For(A));
        }

        /// <summary>The gate for OpenLogi issue 1129: Windows repaints a
        /// Logitech device through its LampArray driver, so while Windows
        /// lights the unit the HID++ worker gets no lighting path. The
        /// rumble path stays.</summary>
        [Fact]
        public void ALogitechRow_WhoseLampArrayUnitWindowsLights_TakesThatUnit_AndNoHidppLightingPath()
        {
            var unit = ZoneOnly(1, 3, Box, haptics: true);
            var lamp = Lamp("lamp", Box, kind: 2, windowsLit: true, vid: Logitech, pid: 0xC548);
            var row = Row(A, InputDeviceType.Mouse, Logitech, 0xC548, Box);

            var gated = PeripheralLinker.Build(new[] { row }, Inputs(Presence(ledSdk: true), Snapshot(unit), lamp)).For(A);
            Assert.Equal(new[] { lamp.Path }, gated.Lighting);
            Assert.Equal(new[] { P(unit) }, gated.Haptics);
            Assert.Equal(new[] { unit.Key }, gated.HidppUnits);

            // Dynamic Lighting off for the device: the unit the tree lights
            // today, as before.
            var open = PeripheralLinker.Build(new[] { row },
                Inputs(Presence(ledSdk: true), Snapshot(unit), lamp with { WindowsLit = false })).For(A);
            Assert.Equal(new[] { P(unit) }, open.Lighting);
            Assert.Equal(new[] { P(unit) }, open.Haptics);
        }

        /// <summary>A Logitech device is lit by its own protocol ahead of
        /// any LampArray unit, and its row offers the highest hold among the
        /// units that light it.</summary>
        [Fact]
        public void ALogitechUnitWithALightPlan_ComesBeforeALampArrayUnit_AndGivesItsRowItsHold()
        {
            var first = Planned(1, 0, Box, level: 1);
            var second = Planned(2, 0, Box, level: 2);
            var lamp = Lamp("lamp", Box, vid: Logitech, pid: 0xC547);
            var row = Row(A, InputDeviceType.Keyboard, Logitech, 0xC547, Box);

            var lit = PeripheralLinker.Build(new[] { row }, Inputs(Presence(), Snapshot(first, second), lamp)).For(A);
            Assert.Equal(new[] { P(first), P(second) }, lit.Lighting);
            Assert.Equal(2, lit.MaxClaimLevel);
            Assert.True(lit.Kinds.HasFlag(PeripheralOutputKinds.LightingLevel1));
            Assert.True(lit.Kinds.HasFlag(PeripheralOutputKinds.LightingLevel2));
            Assert.False(lit.DirectWhenClosed);

            // The zone-only class offers no hold.
            var zoneOnly = PeripheralLinker.Build(new[] { row }, Inputs(Presence(), Snapshot(ZoneOnly(1, 0, Box)), lamp)).For(A);
            Assert.Equal(0, zoneOnly.MaxClaimLevel);
            Assert.Equal(OutputFamily.HidppUnit, zoneOnly.Lighting.Single().Family);
        }

        [Fact]
        public void ALogitechUnitWithNoPlan_LeavesItsRowToALampArrayUnit_OrToNothing()
        {
            var unplanned = Unplanned(1, 0, Box);
            var lamp = Lamp("lamp", Box, vid: Logitech, pid: 0xC547);
            var row = Row(A, InputDeviceType.Keyboard, Logitech, 0xC547, Box);

            var lit = PeripheralLinker.Build(new[] { row }, Inputs(Presence(), Snapshot(unplanned), lamp)).For(A);
            Assert.Equal(new[] { lamp.Path }, lit.Lighting);
            Assert.Equal(0, lit.MaxClaimLevel);
            // Battery mode still reads the HID++ unit the row matches.
            Assert.Equal(new[] { unplanned.Key }, lit.HidppUnits);

            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Presence(), Snapshot(unplanned))).For(A));
            // The LED SDK path is given only while G HUB runs.
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Presence(ledSdk: true), Snapshot(unplanned))).For(A));
        }

        // ── Razer ──

        [Fact]
        public void ARazerRow_TakesItsCategoryWhileSynapseRuns_AndItsOwnUnitWhileItDoesNot()
        {
            var row = Row(A, InputDeviceType.Mouse, Razer, 0x0084, Box);
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, Razer, 0x0084) { Name = "DeathAdder V2" };
            var category = P(OutputFamily.ChromaCategory, "mouse");

            // 1. Synapse runs: the category, with the unit waiting behind it.
            var running = PeripheralLinker.Build(new[] { row }, Inputs(Presence(synapse: true, razerRuns: true), null, unit)).For(A);
            Assert.Equal(new[] { category }, running.Lighting);
            Assert.True(running.DirectWhenClosed);
            Assert.Empty(running.Shadows);
            // It runs with no Chroma SDK installed: nothing, the unit included.
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Presence(razerRuns: true), null, unit)).For(A));

            // 3. Synapse does not run: the unit, standing in for the category.
            var direct = PeripheralLinker.Build(new[] { row }, Inputs(Presence(synapse: true), null, unit)).For(A);
            Assert.Equal(new[] { unit.Path }, direct.Lighting);
            Assert.Equal(new[] { category }, direct.Shadows);
            Assert.False(direct.DirectWhenClosed);
            Assert.False(direct.CatchAll);

            // 2. Windows lights a LampArray unit of the device: that unit,
            // ahead of the device's own, and no shadow.
            var lamp = Lamp("lamp", Box, kind: 2, windowsLit: true, vid: Razer, pid: 0x0084);
            var windows = PeripheralLinker.Build(new[] { row }, Inputs(Presence(synapse: true), null, unit, lamp)).For(A);
            Assert.Equal(new[] { lamp.Path }, windows.Lighting);
            Assert.Empty(windows.Shadows);

            // 4. No unit of its own: a LampArray unit.
            var lampOnly = PeripheralLinker.Build(new[] { row },
                Inputs(Presence(synapse: true), null, lamp with { WindowsLit = false })).For(A);
            Assert.Equal(new[] { lamp.Path }, lampOnly.Lighting);

            // 5. Neither: the category, which waits for Synapse, as before.
            var waiting = PeripheralLinker.Build(new[] { row }, Inputs(Presence(synapse: true), null)).For(A);
            Assert.Equal(new[] { category }, waiting.Lighting);
            Assert.False(waiting.DirectWhenClosed);
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Presence(), null)).For(A));
        }

        /// <summary>Every collection of one USB device reports one vendor
        /// and product ID, so two Razer devices in one container each take
        /// the unit that is theirs, and a unit elsewhere is nobody's.</summary>
        [Fact]
        public void ARazerKeyboardAndMouseInOneContainer_EachTakeTheUnitWithTheirProductId()
        {
            var keyboard = Row(A, InputDeviceType.Keyboard, Razer, 0x024E, Box);
            var mouse = Row(B, InputDeviceType.Mouse, Razer, 0x0084, Box);
            var elsewhere = Row(C, InputDeviceType.Mouse, Razer, 0x0084, OtherBox);
            var keyboardUnit = new DirectUnit(OutputFamily.RazerUnit, "kbd", Box, Razer, 0x024E);
            var mouseUnit = new DirectUnit(OutputFamily.RazerUnit, "mouse", Box, Razer, 0x0084);

            var table = PeripheralLinker.Build(new[] { keyboard, mouse, elsewhere },
                Inputs(Presence(), null, keyboardUnit, mouseUnit));
            Assert.Equal(new[] { keyboardUnit.Path }, table.For(A).Lighting);
            Assert.Equal(new[] { P(OutputFamily.ChromaCategory, "keyboard") }, table.For(A).Shadows);
            Assert.Equal(new[] { mouseUnit.Path }, table.For(B).Lighting);
            Assert.Null(table.For(C));

            Assert.False(PeripheralLinker.MatchesDevice(mouseUnit, mouse with { Container = Guid.Empty }));
            Assert.False(PeripheralLinker.MatchesDevice(mouseUnit with { ContainerId = Guid.Empty }, mouse with { Container = Guid.Empty }));
            Assert.False(PeripheralLinker.MatchesDevice(mouseUnit, mouse with { CapType = InputDeviceType.Gamepad }));
        }

        // ── SteelSeries ──

        [Fact]
        public void ASteelSeriesRow_FollowsTheSameOrder_WithNoShadow_AndATactileRivalKeepsItsRumble()
        {
            var rival = Row(A, InputDeviceType.Mouse, SteelSeries, 0x1700, Box);
            var unit = new DirectUnit(OutputFamily.SteelSeriesUnit, "rival", Box, SteelSeries, 0x1700);
            var type = P(OutputFamily.GameSenseColor, "mouse");

            var running = PeripheralLinker.Build(new[] { rival }, Inputs(Presence(gg: true, ggRuns: true), null, unit)).For(A);
            Assert.Equal(new[] { type }, running.Lighting);
            Assert.True(running.DirectWhenClosed);
            Assert.Equal(new[] { PeripheralLinker.GameSenseTactilePath }, running.Haptics);

            // GG does not run: its own unit, and the tactile path all the same.
            var direct = PeripheralLinker.Build(new[] { rival }, Inputs(Presence(gg: true), null, unit)).For(A);
            Assert.Equal(new[] { unit.Path }, direct.Lighting);
            Assert.Empty(direct.Shadows);
            Assert.Equal(new[] { PeripheralLinker.GameSenseTactilePath }, direct.Haptics);

            var lamp = Lamp("lamp", Box, kind: 2, windowsLit: true, vid: SteelSeries, pid: 0x1700);
            Assert.Equal(new[] { lamp.Path },
                PeripheralLinker.Build(new[] { rival }, Inputs(Presence(gg: true), null, unit, lamp)).For(A).Lighting);
            Assert.Equal(new[] { lamp.Path },
                PeripheralLinker.Build(new[] { rival }, Inputs(Presence(gg: true), null, lamp with { WindowsLit = false })).For(A).Lighting);
            Assert.Equal(new[] { type },
                PeripheralLinker.Build(new[] { rival }, Inputs(Presence(gg: true), null)).For(A).Lighting);

            // GG is not installed: the unit lights it, and nothing rumbles it.
            var bare = PeripheralLinker.Build(new[] { rival }, Inputs(Presence(), null, unit)).For(A);
            Assert.Equal(new[] { unit.Path }, bare.Lighting);
            Assert.Empty(bare.Haptics);
        }

        // ── Any other vendor, vendor rows, TouchSense ──

        [Fact]
        public void ARowOfAnyOtherVendor_TakesItsLampArrayUnit_WhoeverLightsIt()
        {
            var row = Row(A, InputDeviceType.Keyboard, 0x0B05, 0x1A2B, Box);
            var lamp = Lamp("lamp", Box, vid: 0x0B05, pid: 0x1A2B);

            var lit = PeripheralLinker.Build(new[] { row }, Inputs(Presence(), null, lamp)).For(A);
            Assert.Equal(new[] { lamp.Path }, lit.Lighting);
            Assert.False(lit.CatchAll);
            Assert.False(lit.UnitRow);
            // Windows lights it: the row keeps the path, and its tab, while
            // the backend stands down.
            Assert.Equal(new[] { lamp.Path },
                PeripheralLinker.Build(new[] { row }, Inputs(Presence(), null, lamp with { WindowsLit = true })).For(A).Lighting);
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Presence(), null)).For(A));
            // A gamepad in the same container is no row the linker reads.
            Assert.Null(PeripheralLinker.Build(new[] { row with { CapType = InputDeviceType.Gamepad } },
                Inputs(Presence(), null, lamp)).For(A));
        }

        [Fact]
        public void TheVendorRows_TakeWhatTheyTook_WhateverTheDirectBackendsFound()
        {
            var chroma = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma);
            var row = new LinkRow(chroma, InputDeviceType.PeripheralLighting, 0x5046, 0x4348, Guid.Empty, PeripheralRowKind.RazerChroma);
            var units = new[]
            {
                new DirectUnit(OutputFamily.RazerUnit, "razer", Box, Razer, 0x0084),
                Lamp("lamp", Box),
            };
            foreach (bool runs in new[] { false, true })
            {
                var links = PeripheralLinker.Build(new[] { row },
                    Inputs(Presence(synapse: true, razerRuns: runs), null, units)).For(chroma);
                Assert.Equal(PeripheralLinker.ChromaCategories, links.Lighting.Select(p => p.Key));
                Assert.True(links.CatchAll);
                Assert.False(links.UnitRow);
                Assert.False(links.DirectWhenClosed);
                Assert.Empty(links.Shadows);
            }
        }

        [Fact]
        public void ARowThatMatchesATouchSenseUnit_TakesItsPathForRumble_AndNoLightingFromIt()
        {
            var mouse = Row(A, InputDeviceType.Mouse, Logitech, 0xC030, Box);
            var keyboard = Row(B, InputDeviceType.Keyboard, 0x1234, 0x0001, Box);
            var unit = new DirectUnit(OutputFamily.TouchSenseUnit, "winusb", Box, Logitech, 0xC030) { Name = "iFeel Mouse" };

            var table = PeripheralLinker.Build(new[] { mouse, keyboard }, Inputs(Presence(), null, unit));
            Assert.Equal(new[] { unit.Path }, table.For(A).Haptics);
            Assert.Empty(table.For(A).Lighting);
            Assert.Equal(PeripheralOutputKinds.Haptics, table.For(A).Kinds);
            Assert.Null(table.For(B));
            // It is no lit unit left without a row.
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(Presence(), null, unit)));
        }

        // ── LampArray units and the rows they light ──

        /// <summary>Every device built into the PC shares one container. Its
        /// only LampArray unit is the keyboard's whatever kind it declares,
        /// and the touchpad's mouse row is left alone, the shape of the
        /// bench: keyboard 048D:C197, lighting 048D:C193.</summary>
        [Fact]
        public void InThePcsOwnContainer_TheOnlyUnitLightsTheKeyboardRows_WhateverKindItDeclares()
        {
            var keyboard = Row(A, InputDeviceType.Keyboard, Lenovo, 0xC197, Pc);
            var touchpad = Row(B, InputDeviceType.Mouse, 0x06CB, 0xCFD7, Pc);
            foreach (int kind in new[] { 1, 7, 0 })
            {
                var lamp = Lamp("lamp", Pc, kind, vid: Lenovo, pid: 0xC193);
                var table = PeripheralLinker.Build(new[] { keyboard, touchpad }, Inputs(Presence(), null, lamp));
                Assert.Equal(new[] { lamp.Path }, table.For(A).Lighting);
                Assert.Null(table.For(B));
                Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard, touchpad }, Inputs(Presence(), null, lamp)));
            }
        }

        [Fact]
        public void InThePcsOwnContainer_SeveralUnitsGoByKind_AndAnyOtherKindToNoRow()
        {
            var keyboard = Row(A, InputDeviceType.Keyboard, Lenovo, 0xC197, Pc);
            var touchpad = Row(B, InputDeviceType.Mouse, 0x06CB, 0xCFD7, Pc);
            var keys = Lamp("keys", Pc, kind: 1, vid: Lenovo, pid: 0xC193);
            var pad = Lamp("pad", Pc, kind: 2, vid: Lenovo, pid: 0xC194);
            var chassis = Lamp("chassis", Pc, kind: 7, vid: Lenovo, pid: 0xC195 + 1);
            var inputs = Inputs(Presence(), null, keys, pad, chassis);

            var table = PeripheralLinker.Build(new[] { keyboard, touchpad }, inputs);
            Assert.Equal(new[] { keys.Path }, table.For(A).Lighting);
            Assert.Equal(new[] { pad.Path }, table.For(B).Lighting);
            Assert.Equal(new[] { chassis }, PeripheralLinker.Orphans(new[] { keyboard, touchpad }, inputs));
        }

        [Fact]
        public void OutsideThePc_TheOnlyUnitLightsEveryRowOfItsDevice_AndSeveralGoByKind()
        {
            var keyboard = Row(A, InputDeviceType.Keyboard, 0x0B05, 0x1A2B, Box);
            var mouse = Row(B, InputDeviceType.Mouse, 0x0B05, 0x1A2B, Box);
            var analog = Row(C, InputDeviceType.AnalogKeyboard, 0x0B05, 0x1A2B, Box);

            // One unit, of a kind that is neither: the device is one thing.
            var peripheral = Lamp("lamp", Box, kind: 4);
            var one = PeripheralLinker.Build(new[] { keyboard, mouse, analog }, Inputs(Presence(), null, peripheral));
            Assert.All(new[] { A, B, C }, id => Assert.Equal(new[] { peripheral.Path }, one.For(id).Lighting));

            // A receiver, or a device with several lit parts.
            var keys = Lamp("keys", Box, kind: 1);
            var pointer = Lamp("pointer", Box, kind: 2);
            var strip = Lamp("strip", Box, kind: 4);
            var inputs = Inputs(Presence(), null, keys, pointer, strip);
            var several = PeripheralLinker.Build(new[] { keyboard, mouse, analog }, inputs);
            Assert.Equal(new[] { keys.Path }, several.For(A).Lighting);
            Assert.Equal(new[] { pointer.Path }, several.For(B).Lighting);
            Assert.Equal(new[] { keys.Path }, several.For(C).Lighting);
            Assert.Equal(new[] { strip }, PeripheralLinker.Orphans(new[] { keyboard, mouse, analog }, inputs));

            // A unit in another container, or a row whose container is not
            // known yet, never.
            Assert.Null(PeripheralLinker.Build(new[] { keyboard }, Inputs(Presence(), null, Lamp("far", OtherBox))).For(A));
            Assert.Null(PeripheralLinker.Build(new[] { keyboard with { Container = Guid.Empty } },
                Inputs(Presence(), null, Lamp("lost", Guid.Empty))).For(A));
        }

        /// <summary>The Legion Pro 5's keyboard takes a raw host's frames and
        /// keeps painting over them (hid-rgb-ctl issue 10), so it lights no
        /// row and gets none. It is still there, so a second unit beside it
        /// is told from it by kind.</summary>
        [Fact]
        public void AUnitThatIgnoresARawHost_LightsNoRow_GetsNoRow_AndStillCounts()
        {
            Assert.Contains(((ushort)0x048D, (ushort)0xC195), PeripheralLinker.LampArrayIgnoresRawHost);
            var keyboard = Row(A, InputDeviceType.Keyboard, Lenovo, 0xC197, Pc);
            var legion = Lamp("legion", Pc, kind: 1, vid: Lenovo, pid: 0xC195);
            Assert.True(PeripheralLinker.IgnoresRawHost(legion));
            Assert.False(PeripheralLinker.IgnoresRawHost(legion with { ProductId = 0xC193 }));
            // The same IDs in another family mean nothing.
            Assert.False(PeripheralLinker.IgnoresRawHost(new DirectUnit(OutputFamily.RazerUnit, "r", Pc, Lenovo, 0xC195)));

            var alone = Inputs(Presence(), null, legion);
            Assert.Null(PeripheralLinker.Build(new[] { keyboard }, alone).For(A));
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, alone));
            Assert.Empty(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), alone));

            // A chassis light beside it is one of two units, so its kind
            // decides, and kind 7 goes to no row.
            var chassis = Lamp("chassis", Pc, kind: 7, vid: Lenovo, pid: 0xC193);
            var both = Inputs(Presence(), null, legion, chassis);
            Assert.Null(PeripheralLinker.Build(new[] { keyboard }, both).For(A));
            Assert.Equal(new[] { chassis }, PeripheralLinker.Orphans(new[] { keyboard }, both));

            // A unit row made for it earlier links nothing.
            var unitRow = new LinkRow(B, InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = legion.Path,
            };
            Assert.Null(PeripheralLinker.Build(new[] { unitRow }, alone).For(B));
        }

        // ── Units no row reaches ──

        [Fact]
        public void ALitUnitNoRowReaches_IsAnOrphan_UnlessItsContainerIsNotKnownYet()
        {
            var keyboard = Row(A, InputDeviceType.Keyboard, 0x0B05, 0x1A2B, Box);
            var headset = Lamp("headset", OtherBox, kind: 4);
            var unknown = Lamp("unknown", Guid.Empty, kind: 4);
            var razer = new DirectUnit(OutputFamily.RazerUnit, "razer", OtherBox, Razer, 0x0084);
            var inputs = Inputs(Presence(), null, headset, unknown, razer, Lamp("keys", Box));

            Assert.Equal(new[] { headset }, PeripheralLinker.Orphans(new[] { keyboard }, inputs));
            // Windows lighting it changes nothing about who reaches it.
            Assert.Equal(new[] { headset with { WindowsLit = true } },
                PeripheralLinker.Orphans(new[] { keyboard }, Inputs(Presence(), null, headset with { WindowsLit = true })));
            // A row that matched it keeps it from being one, whichever path
            // the row ended up lit through.
            var logitech = Row(B, InputDeviceType.Mouse, Logitech, 0xC548, Box);
            var hidpp = Snapshot(ZoneOnly(1, 3, Box));
            var lamp = Lamp("lamp", Box, kind: 2, vid: Logitech, pid: 0xC548);
            var table = PeripheralLinker.Build(new[] { logitech }, Inputs(Presence(), hidpp, lamp));
            Assert.Equal(OutputFamily.HidppUnit, table.For(B).Lighting.Single().Family);
            Assert.Empty(PeripheralLinker.Orphans(new[] { logitech }, Inputs(Presence(), hidpp, lamp)));
        }

        /// <summary>A keyboard's row can come online a moment after its
        /// LampArray collection. The unit gets a row only once it has gone
        /// unreached for the whole hold, with no break.</summary>
        [Fact]
        public void AnOrphanGetsARow_OnlyAfterItHasBeenOneForTheWholeHold()
        {
            var since = new Dictionary<OutputPath, long>();
            var headset = Lamp("headset", OtherBox, kind: 4);
            var strip = Lamp("strip", Box, kind: 4);
            var none = Array.Empty<DirectUnit>();

            Assert.Empty(PeripheralLinker.HeldOrphans(since, new[] { headset }, 1000));
            Assert.Empty(PeripheralLinker.HeldOrphans(since, new[] { headset }, 1000 + PeripheralLinker.UnitRowHoldMs - 1));
            Assert.Equal(new[] { headset }, PeripheralLinker.HeldOrphans(since, new[] { headset }, 1000 + PeripheralLinker.UnitRowHoldMs));

            // A second orphan keeps its own clock.
            long later = 1000 + PeripheralLinker.UnitRowHoldMs + 10;
            Assert.Equal(new[] { headset }, PeripheralLinker.HeldOrphans(since, new[] { headset, strip }, later));
            Assert.Equal(new[] { headset, strip },
                PeripheralLinker.HeldOrphans(since, new[] { headset, strip }, later + PeripheralLinker.UnitRowHoldMs));

            // A break starts the wait over: a row reached it, then let go.
            Assert.Equal(new[] { strip }, PeripheralLinker.HeldOrphans(since, new[] { strip }, later + 6000));
            Assert.False(since.ContainsKey(headset.Path));
            Assert.Equal(new[] { strip }, PeripheralLinker.HeldOrphans(since, new[] { strip, headset }, later + 6001));
            Assert.Equal(new[] { strip, headset },
                PeripheralLinker.HeldOrphans(since, new[] { strip, headset }, later + 6001 + PeripheralLinker.UnitRowHoldMs));

            Assert.Empty(PeripheralLinker.HeldOrphans(since, none, later + 20000));
            Assert.Empty(since);
            Assert.Equal(5000, PeripheralLinker.UnitRowHoldMs);
        }

        /// <summary>A unit row holds its own unit and nothing else, for as
        /// long as its backend finds the unit, and ranks as a device.</summary>
        [Fact]
        public void AUnitRow_LinksExactlyItsOwnUnit_WhileTheUnitIsThere()
        {
            var headset = Lamp("headset", OtherBox, kind: 4) with { Name = "Headset" };
            var strip = Lamp("strip", Box, kind: 4);
            var row = new LinkRow(A, InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = headset.Path,
            };

            var links = PeripheralLinker.Build(new[] { row }, Inputs(Presence(), null, headset, strip)).For(A);
            Assert.Equal(new[] { headset.Path }, links.Lighting);
            Assert.Empty(links.Haptics);
            Assert.True(links.UnitRow);
            Assert.True(links.OutputOnly);
            Assert.False(links.CatchAll);
            Assert.Equal(0, links.MaxClaimLevel);

            // The unit left: no path, and so no row in the table.
            Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(Presence(), null, strip)).For(A));
            // The row of one family never takes a unit of another by its key.
            Assert.Null(PeripheralLinker.Build(new[] { row },
                Inputs(Presence(), null, new DirectUnit(OutputFamily.RazerUnit, "headset", OtherBox, Razer, 1))).For(A));
            // A unit row is no row a LampArray unit is matched to, so its own
            // unit stays the orphan that keeps it open.
            Assert.Equal(new[] { headset }, PeripheralLinker.Orphans(new[] { row }, Inputs(Presence(), null, headset)));
        }

        [Fact]
        public void ARowKeepsItsRecord_WhileItsDeviceMayStillBeFound()
        {
            var scanned = new HidppSnapshot(Array.Empty<HidppUnit>(), Array.Empty<Guid>());
            var pending = new HidppSnapshot(Array.Empty<HidppUnit>(), new[] { Box });
            var logitech = Row(A, InputDeviceType.Mouse, Logitech, 0xC548, Box);
            var razer = Row(B, InputDeviceType.Mouse, Razer, 0x0084, Box);
            var vendor = new LinkRow(C, InputDeviceType.PeripheralLighting, 0x5046, 0x4348, Guid.Empty, PeripheralRowKind.RazerChroma);
            var unitRow = new LinkRow(C, InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = P(OutputFamily.LampArrayUnit, "lamp"),
            };

            // The rule the tree had: a Logitech row before the first HID++
            // scan or while its container has a slot still to answer.
            Assert.True(PeripheralLinker.StillLooking(logitech, new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None)));
            Assert.True(PeripheralLinker.StillLooking(logitech, new LinkInputs(pending, PeripheralPresence.None)));
            Assert.False(PeripheralLinker.StillLooking(logitech, new LinkInputs(scanned, PeripheralPresence.None)));
            Assert.False(PeripheralLinker.StillLooking(razer, new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None)));
            Assert.False(PeripheralLinker.StillLooking(unitRow, new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None)));

            // And any device row until every direct backend has scanned once.
            var early = new LinkInputs(scanned, PeripheralPresence.None) { DirectScanned = false };
            Assert.True(PeripheralLinker.StillLooking(logitech, early));
            Assert.True(PeripheralLinker.StillLooking(razer, early));
            Assert.True(PeripheralLinker.StillLooking(unitRow, early));
            // A vendor row is found by no scan.
            Assert.False(PeripheralLinker.StillLooking(vendor, early));
            Assert.True(new LinkInputs(scanned, PeripheralPresence.None).DirectScanned);
        }

        // ── Units that are there and do not answer ──

        /// <summary>A unit whose channel closed after a failed write, or
        /// that does not answer, stays on its backend's list, asleep. No
        /// path is linked to it, and the rows that match it keep what they
        /// had on record, so their tabs stay up until it answers.</summary>
        [Fact]
        public void AnAsleepUnit_IsLinkedToNoRow_AndTheRowsThatMatchItKeepTheirRecord()
        {
            var mouse = Row(A, InputDeviceType.Mouse, Razer, 0x0084, Box);
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, Razer, 0x0084);
            var asleep = unit with { Asleep = true };
            int lit = (int)PeripheralOutputKinds.Lighting;

            // Awake: linked, and nothing is waited for.
            var awake = Inputs(Presence(), null, unit);
            Assert.Equal(new[] { unit.Path }, PeripheralLinker.Build(new[] { mouse }, awake).For(A).Lighting);
            Assert.False(PeripheralLinker.StillLooking(mouse, awake));

            // Asleep: no path, and the record stays.
            var sleeping = Inputs(Presence(), null, asleep);
            Assert.Null(PeripheralLinker.Build(new[] { mouse }, sleeping).For(A));
            Assert.True(PeripheralLinker.StillLooking(mouse, sleeping));
            Assert.Equal(lit, PeripheralLinker.Capabilities(lit, null, PeripheralLinker.StillLooking(mouse, sleeping)));

            // Gone from the list, its collection left the walk: nothing is
            // waited for, and the record goes once the hold a row gets
            // after its last unit left is over.
            var gone = Inputs(Presence(), null);
            Assert.False(PeripheralLinker.StillLooking(mouse, gone));
            Assert.Equal(0, PeripheralLinker.Capabilities(lit, null, PeripheralLinker.StillLooking(mouse, gone)));

            // A row the unit is not a unit of waits for nothing.
            Assert.False(PeripheralLinker.StillLooking(mouse with { ProductId = 0x024E }, sleeping));
            Assert.False(PeripheralLinker.StillLooking(mouse with { Container = OtherBox }, sleeping));
            Assert.False(PeripheralLinker.StillLooking(mouse with { CapType = InputDeviceType.Gamepad }, sleeping));
            var chroma = new LinkRow(C, InputDeviceType.PeripheralLighting, 0x5046, 0x4348, Guid.Empty, PeripheralRowKind.RazerChroma);
            Assert.False(PeripheralLinker.StillLooking(chroma, sleeping));
        }

        /// <summary>The family that lights a row is chosen as if every unit
        /// answered, and only the units that do are linked. A row whose own
        /// unit sleeps never falls to the path that waits for the vendor's
        /// program, whose line would name the wrong wait, and the Windows
        /// gate holds for a unit that sleeps.</summary>
        [Fact]
        public void AnAsleepUnit_StillDecidesWhichFamilyLightsItsRow()
        {
            var mouse = Row(A, InputDeviceType.Mouse, Razer, 0x0084, Box);
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, Razer, 0x0084) { Asleep = true };
            var category = P(OutputFamily.ChromaCategory, "mouse");

            // Synapse is installed and does not run: nothing, where a model
            // with no unit of its own would wait on the category.
            Assert.Null(PeripheralLinker.Build(new[] { mouse }, Inputs(Presence(synapse: true), null, unit)).For(A));
            Assert.Equal(new[] { category },
                PeripheralLinker.Build(new[] { mouse }, Inputs(Presence(synapse: true), null)).For(A).Lighting);

            // Synapse runs: its category, and the device's own path still
            // waits behind it.
            var running = PeripheralLinker.Build(new[] { mouse }, Inputs(Presence(synapse: true, razerRuns: true), null, unit)).For(A);
            Assert.Equal(new[] { category }, running.Lighting);
            Assert.True(running.DirectWhenClosed);

            // An asleep unit beside one that answers: the one that answers.
            var keyboard = Row(B, InputDeviceType.Keyboard, 0x0B05, 0x1A2B, OtherBox);
            var keys = Lamp("keys", OtherBox, kind: 1);
            var spare = Lamp("spare", OtherBox, kind: 1) with { Asleep = true };
            var both = Inputs(Presence(), null, keys, spare, Lamp("strip", OtherBox, kind: 4));
            Assert.Equal(new[] { keys.Path }, PeripheralLinker.Build(new[] { keyboard }, both).For(B).Lighting);
            Assert.True(PeripheralLinker.StillLooking(keyboard, both));

            // The gate for a Logitech device Windows lights does not open
            // because the LampArray unit sleeps: no HID++ lighting path.
            var hidpp = ZoneOnly(1, 3, Box, haptics: true);
            var logitech = Row(C, InputDeviceType.Mouse, Logitech, 0xC548, Box);
            var lamp = Lamp("lamp", Box, kind: 2, windowsLit: true, vid: Logitech, pid: 0xC548) with { Asleep = true };
            var gated = PeripheralLinker.Build(new[] { logitech }, Inputs(Presence(), Snapshot(hidpp), lamp)).For(C);
            Assert.Empty(gated.Lighting);
            Assert.Equal(new[] { P(hidpp) }, gated.Haptics);
            // Windows does not light it: the device's own protocol lights
            // the row, whatever its LampArray unit does.
            var open = PeripheralLinker.Build(new[] { logitech },
                Inputs(Presence(), Snapshot(hidpp), lamp with { WindowsLit = false })).For(C);
            Assert.Equal(new[] { P(hidpp) }, open.Lighting);
        }

        [Fact]
        public void AnAsleepLampArrayOrTouchSenseUnit_KeepsItsRowsRecords_AndAUnitRowItsRow()
        {
            int lit = (int)PeripheralOutputKinds.Lighting;
            var keyboard = Row(A, InputDeviceType.Keyboard, 0x0B05, 0x1A2B, Box);
            var lamp = Lamp("lamp", Box, vid: 0x0B05, pid: 0x1A2B) with { Asleep = true };
            var sleeping = Inputs(Presence(), null, lamp);
            Assert.Null(PeripheralLinker.Build(new[] { keyboard }, sleeping).For(A));
            Assert.True(PeripheralLinker.StillLooking(keyboard, sleeping));
            // It is reached by a row, so it is no unit left without one.
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, sleeping));

            // A rumble-only unit: no haptic path, and the row keeps its tab.
            var mouse = Row(B, InputDeviceType.Mouse, Logitech, 0xC030, OtherBox);
            var buzz = new DirectUnit(OutputFamily.TouchSenseUnit, "winusb", OtherBox, Logitech, 0xC030) { Asleep = true };
            var quiet = Inputs(Presence(), null, buzz);
            Assert.Null(PeripheralLinker.Build(new[] { mouse }, quiet).For(B));
            Assert.True(PeripheralLinker.StillLooking(mouse, quiet));
            Assert.Equal((int)PeripheralOutputKinds.Haptics,
                PeripheralLinker.Capabilities((int)PeripheralOutputKinds.Haptics, null, PeripheralLinker.StillLooking(mouse, quiet)));

            // A unit with a row of its own: the row links nothing, keeps its
            // record, and the unit stays the orphan that keeps the row open.
            var headset = Lamp("headset", OtherBox, kind: 4) with { Asleep = true };
            var unitRow = new LinkRow(C, InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = headset.Path,
            };
            var alone = Inputs(Presence(), null, headset);
            Assert.Null(PeripheralLinker.Build(new[] { unitRow }, alone).For(C));
            Assert.True(PeripheralLinker.StillLooking(unitRow, alone));
            Assert.Equal(lit, PeripheralLinker.Capabilities(lit, null, PeripheralLinker.StillLooking(unitRow, alone)));
            Assert.Equal(new[] { headset }, PeripheralLinker.Orphans(new[] { unitRow }, alone));
            // Another unit asleep is none of this row's business.
            Assert.False(PeripheralLinker.StillLooking(unitRow with { Unit = P(OutputFamily.LampArrayUnit, "strip") }, alone));

            // A unit that ignores a raw host lights nothing awake or asleep,
            // so nothing is waited for.
            var legion = Lamp("legion", Pc, kind: 1, vid: Lenovo, pid: 0xC195) with { Asleep = true };
            var builtIn = Row(A, InputDeviceType.Keyboard, Lenovo, 0xC197, Pc);
            Assert.False(PeripheralLinker.StillLooking(builtIn, Inputs(Presence(), null, legion)));
            Assert.False(PeripheralLinker.StillLooking(unitRow with { Unit = legion.Path }, Inputs(Presence(), null, legion)));
        }

        // ── Units that left ──

        /// <summary>A device that is unplugged loses its unit at a walk and
        /// its row at the input manager's next device scan, in either
        /// order. A row whose last unit left the list is held for the hold,
        /// from the last pass that saw the unit, so the scan has taken the
        /// row offline before its record could go.</summary>
        [Fact]
        public void ARowWhoseLastUnitLeft_IsHeldForTheHold_AndNoLonger()
        {
            var seen = new Dictionary<Guid, long>();
            var mouse = Row(A, InputDeviceType.Mouse, Razer, 0x0084, Box);
            var other = Row(B, InputDeviceType.Mouse, Razer, 0x024E, Box);
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, Razer, 0x0084);
            var rows = new[] { mouse, other };
            var none = Array.Empty<DirectUnit>();
            int hold = PeripheralLinker.UnitGoneHoldMs;

            // Matched: nothing is held, and the row's clock starts.
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, new[] { unit }, 1000));
            Assert.Equal(new[] { A }, seen.Keys);

            // The unit left: the row is held to the end of the hold, counted
            // from the last pass that saw it matched, and no further.
            Assert.Equal(new[] { A }, PeripheralLinker.HeldRows(seen, rows, none, 1000));
            Assert.Equal(new[] { A }, PeripheralLinker.HeldRows(seen, rows, none, 1000 + hold - 1));
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, none, 1000 + hold));
            Assert.Empty(seen);
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, none, 1000 + hold + 1));

            // A unit that sleeps is still listed, so the clock runs on to
            // the last pass that listed it.
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, new[] { unit }, 20000));
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, new[] { unit with { Asleep = true } }, 23000));
            Assert.Equal(new[] { A }, PeripheralLinker.HeldRows(seen, rows, none, 23000 + hold - 1));
            // It comes back inside the hold: matched again, held no more,
            // and the next hold counts from here.
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, new[] { unit }, 23000 + hold - 1));
            Assert.Equal(new[] { A }, PeripheralLinker.HeldRows(seen, rows, none, 23000 + 2 * hold - 2));
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, none, 23000 + 2 * hold - 1));

            // The row that no unit ever matched was never on the record.
            Assert.Empty(seen);
            Assert.Equal(5000, hold);
        }

        /// <summary>The hold counts on while the row is away: the scan took
        /// it offline, so no pass reads it. It ends by the clock alone, and
        /// a row that comes back inside it is still held.</summary>
        [Fact]
        public void TheHoldAfterAUnitLeft_RunsOnWhileTheRowIsNotRead()
        {
            var seen = new Dictionary<Guid, long>();
            var mouse = Row(A, InputDeviceType.Mouse, Razer, 0x0084, Box);
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, Razer, 0x0084);
            var noRows = Array.Empty<LinkRow>();
            var none = Array.Empty<DirectUnit>();
            int hold = PeripheralLinker.UnitGoneHoldMs;

            Assert.Empty(PeripheralLinker.HeldRows(seen, new[] { mouse }, new[] { unit }, 1000));
            // Unit and row both gone from the pass's sight.
            Assert.Equal(new[] { A }, PeripheralLinker.HeldRows(seen, noRows, none, 1500));
            // Back online inside the hold, its unit not found yet.
            Assert.Equal(new[] { A }, PeripheralLinker.HeldRows(seen, new[] { mouse }, none, 1000 + hold - 1));
            // Away again, the hold runs out, and the record of it goes.
            Assert.Empty(PeripheralLinker.HeldRows(seen, noRows, none, 1000 + hold));
            Assert.Empty(seen);

            // With no unit listed and nothing on record there is nothing
            // to do, which is every pass of a host with no direct backend.
            var untouched = new Dictionary<Guid, long>();
            Assert.Empty(PeripheralLinker.HeldRows(untouched, new[] { mouse }, none, 99999));
            Assert.Empty(PeripheralLinker.HeldRows(untouched, new[] { mouse }, null, 99999));
            Assert.Empty(untouched);
        }

        /// <summary>The rows a unit holds after it left are the rows it was
        /// a unit of while it was there: the rows of its own device, the
        /// rows a LampArray unit lights, and a row of its own. A vendor row
        /// and a unit that lights no row hold nothing.</summary>
        [Fact]
        public void TheRowsHeldAfterAUnitLeft_AreTheRowsItWasAUnitOf()
        {
            var none = Array.Empty<DirectUnit>();
            var mouse = Row(A, InputDeviceType.Mouse, Logitech, 0xC030, Box);
            var keyboard = Row(B, InputDeviceType.Keyboard, 0x0B05, 0x1A2B, OtherBox);
            var chroma = new LinkRow(C, InputDeviceType.PeripheralLighting, 0x5046, 0x4348, Guid.Empty, PeripheralRowKind.RazerChroma);
            var buzz = new DirectUnit(OutputFamily.TouchSenseUnit, "winusb", Box, Logitech, 0xC030);
            var lamp = Lamp("lamp", OtherBox, vid: 0x0B05, pid: 0x1A2B);
            var rows = new[] { mouse, keyboard, chroma };

            Assert.True(PeripheralLinker.MatchesAnyUnit(mouse, new[] { buzz }));
            Assert.True(PeripheralLinker.MatchesAnyUnit(mouse, new[] { buzz with { Asleep = true } }));
            Assert.True(PeripheralLinker.MatchesAnyUnit(keyboard, new[] { lamp }));
            Assert.False(PeripheralLinker.MatchesAnyUnit(mouse, new[] { lamp }));
            Assert.False(PeripheralLinker.MatchesAnyUnit(chroma, new[] { buzz, lamp }));
            Assert.False(PeripheralLinker.MatchesAnyUnit(mouse, null));

            var seen = new Dictionary<Guid, long>();
            Assert.Empty(PeripheralLinker.HeldRows(seen, rows, new[] { buzz, lamp }, 1000));
            // One unit left and the other stayed: only its own row is held.
            Assert.Equal(new[] { A }, PeripheralLinker.HeldRows(seen, rows, new[] { lamp }, 1001));
            Assert.Equal(new[] { A, B }, PeripheralLinker.HeldRows(seen, rows, none, 1002).OrderBy(id => id));
            Assert.DoesNotContain(C, seen.Keys);

            // A row of the unit's own is held like any other.
            var headset = Lamp("headset", OtherBox, kind: 4);
            var unitRow = new LinkRow(C, InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = headset.Path,
            };
            var own = new Dictionary<Guid, long>();
            Assert.Empty(PeripheralLinker.HeldRows(own, new[] { unitRow }, new[] { headset }, 1000));
            Assert.Equal(new[] { C }, PeripheralLinker.HeldRows(own, new[] { unitRow }, none, 1001));
            // Another unit's being there is not its unit's.
            Assert.Equal(new[] { C }, PeripheralLinker.HeldRows(own, new[] { unitRow }, new[] { lamp }, 1002));

            // A unit that ignores a raw host, or whose container is not
            // known yet, was never a unit of any row, so it holds none.
            var legion = Lamp("legion", Pc, kind: 1, vid: Lenovo, pid: 0xC195);
            var builtIn = Row(A, InputDeviceType.Keyboard, Lenovo, 0xC197, Pc);
            var unknown = new DirectUnit(OutputFamily.RazerUnit, "razer", Guid.Empty, Razer, 0x0084);
            var razerMouse = Row(B, InputDeviceType.Mouse, Razer, 0x0084, Guid.Empty);
            var unlit = new Dictionary<Guid, long>();
            Assert.Empty(PeripheralLinker.HeldRows(unlit, new[] { builtIn, razerMouse }, new[] { legion, unknown }, 1000));
            Assert.Empty(PeripheralLinker.HeldRows(unlit, new[] { builtIn, razerMouse }, none, 1001));
            Assert.Empty(unlit);
        }

        /// <summary>The hold is long enough for the input manager's scan to
        /// take an unplugged device's row offline: the scan's interval and
        /// one link pass, twice over.</summary>
        [Fact]
        public void TheHoldAfterAUnitLeft_CoversTheInputManagersScan_TwiceOver()
        {
            var interval = typeof(InputManager).GetField("EnumerationIntervalMs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(interval);
            int scan = (int)interval.GetRawConstantValue();
            Assert.Equal(2000, scan);
            Assert.True(PeripheralLinker.UnitGoneHoldMs >= 2 * (scan + PeripheralOutputHost.LinkMs),
                $"{PeripheralLinker.UnitGoneHoldMs} against a scan every {scan} ms");
        }

        // ── Over generated inputs ──

        private static readonly ushort[] Vendors = { Logitech, Razer, SteelSeries, 0x0B05 };
        private static readonly ushort[] Products = { 0x0084, 0x1700, 0x024E, 0xC548, 0x0244 };
        private static readonly int[] RowTypes = { InputDeviceType.Mouse, InputDeviceType.Keyboard, InputDeviceType.AnalogKeyboard, InputDeviceType.Gamepad };
        private static readonly Guid[] Containers = { Box, OtherBox, PeripheralLinker.LocalMachineContainer, Guid.Empty };

        private static T Pick<T>(Random random, T[] from) => from[random.Next(from.Length)];

        private static PeripheralPresence RandomPresence(Random random)
            => new PeripheralPresence(random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, false)
            {
                RazerSoftwareRunning = random.Next(2) == 0,
                SteelSeriesGGRunning = random.Next(2) == 0,
                LogitechGamingSoftware = random.Next(4) == 0,
            };

        private static LinkRow[] RandomRows(Random random)
        {
            var rows = new LinkRow[random.Next(1, 6)];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = Row(new Guid(i + 1, 0, 0, new byte[8]), Pick(random, RowTypes), Pick(random, Vendors),
                    Pick(random, Products), Pick(random, Containers));
            return rows;
        }

        private static HidppUnit[] RandomHidpp(Random random, bool withPlans)
        {
            var units = new HidppUnit[random.Next(0, 4)];
            for (int i = 0; i < units.Length; i++)
            {
                byte index = random.Next(3) == 0 ? HidppHapticProtocol.DirectDeviceIndex : (byte)(i + 1);
                int type = Pick(random, new[] { -1, 0, 3, 6 });
                var container = Pick(random, Containers);
                units[i] = random.Next(3) switch
                {
                    0 => ZoneOnly(index, type, container, haptics: random.Next(2) == 0),
                    1 when withPlans => Planned(index, type, container, random.Next(3)),
                    1 => Unplanned(index, type, container),
                    _ => new HidppUnit
                    {
                        ChannelPath = "rx", ContainerId = container, DeviceIndex = index, DeviceType = type,
                        HapticIndex = 0x0B, WaveformMask = 0x7FFF,
                    },
                };
            }
            return units;
        }

        private static DirectUnit[] RandomUnits(Random random)
        {
            var families = new[] { OutputFamily.LampArrayUnit, OutputFamily.RazerUnit, OutputFamily.SteelSeriesUnit, OutputFamily.TouchSenseUnit };
            var units = new DirectUnit[random.Next(0, 6)];
            for (int i = 0; i < units.Length; i++)
                units[i] = new DirectUnit(Pick(random, families), "unit" + i, Pick(random, Containers),
                    Pick(random, new ushort[] { Logitech, Razer, SteelSeries, 0x0B05, Lenovo }),
                    Pick(random, new ushort[] { 0x0084, 0x1700, 0x024E, 0xC548, 0xC195 }))
                {
                    Kind = random.Next(0, 5), WindowsLit = random.Next(3) == 0, Asleep = random.Next(4) == 0,
                };
            return units;
        }

        /// <summary>One family lights a row at a time, whatever the PC holds:
        /// never a direct path beside a shared one, never two direct
        /// families. Two backends would paint one device from two rulings.</summary>
        [Fact]
        public void NoRowEverHoldsTwoLightingFamilies_OverGeneratedInputs()
        {
            var random = new Random(494);
            for (int round = 0; round < 4000; round++)
            {
                var rows = RandomRows(random);
                var presence = RandomPresence(random);
                var inputs = new LinkInputs(Snapshot(RandomHidpp(random, withPlans: true)), presence) { Units = RandomUnits(random) };
                var table = PeripheralLinker.Build(rows, inputs);
                foreach (var row in rows)
                {
                    var links = table.For(row.Device);
                    if (links == null) continue;
                    var families = links.Lighting.Select(p => p.Family).Distinct().ToList();
                    Assert.True(families.Count <= 1, $"round {round}: {string.Join(", ", families)}");
                    Assert.Equal(links.Lighting.Length, links.Lighting.Distinct().Count());
                    bool shared = families.Count == 1 && new OutputPath(families[0], "k").Shared;
                    // A waiting direct path is only ever behind a shared one,
                    // and a shadow only ever beside a Razer unit.
                    if (links.DirectWhenClosed) Assert.True(shared, $"round {round}");
                    if (links.Shadows.Length > 0)
                    {
                        Assert.Equal(OutputFamily.RazerUnit, families.Single());
                        Assert.Equal(OutputFamily.ChromaCategory, links.Shadows.Single().Family);
                    }
                    if (links.MaxClaimLevel > 0) Assert.Equal(OutputFamily.HidppUnit, families.Single());
                    // No lighting comes through a rumble-only unit.
                    Assert.DoesNotContain(OutputFamily.TouchSenseUnit, families);
                    Assert.False(links.CatchAll || links.UnitRow);
                    // No path of either kind leads to a unit that sleeps.
                    foreach (var path in links.Lighting.Concat(links.Haptics))
                        Assert.DoesNotContain(inputs.Units, u => u.Asleep && u.Path == path);
                }
            }
        }

        /// <summary>What the linker gave before any direct unit existed,
        /// written out again here from that rule.</summary>
        private static (OutputPath[] Lighting, OutputPath[] Haptics) BeforeDirectUnits(LinkRow row, HidppUnit[] hidpp, PeripheralPresence presence)
        {
            var lighting = new List<OutputPath>();
            var haptics = new List<OutputPath>();
            if (!PeripheralLinker.IsLinkable(row.CapType)) return (lighting.ToArray(), haptics.ToArray());
            if (row.VendorId == Logitech)
            {
                foreach (var unit in hidpp)
                {
                    if (!PeripheralLinker.Matches(unit, row)) continue;
                    if (unit.HasHaptics) haptics.Add(P(unit));
                    if (!unit.HasRgb) continue;
                    if (presence.LogitechGHubRunning)
                    {
                        var type = P(OutputFamily.LedSdkType, PeripheralLinker.LedSdkTypeOf(unit, row));
                        if (presence.LogitechLedSdk && !lighting.Contains(type)) lighting.Add(type);
                    }
                    else if (unit.DirectRgb)
                    {
                        lighting.Add(P(unit));
                    }
                }
            }
            else if (row.VendorId == Razer)
            {
                if (presence.RazerSynapse) lighting.Add(P(OutputFamily.ChromaCategory, PeripheralLinker.RazerCategory(row)));
            }
            else if (row.VendorId == SteelSeries && presence.SteelSeriesGG)
            {
                bool keyboard = PeripheralProductIds.LightsAsKeyboard(row.ProductId, row.CapType,
                    PeripheralProductIds.SteelSeriesMice, PeripheralProductIds.SteelSeriesKeyboards);
                lighting.Add(P(OutputFamily.GameSenseColor, keyboard ? "keyboard" : "mouse"));
                if (!keyboard && Array.IndexOf(PeripheralLinker.RivalTactilePids, row.ProductId) >= 0)
                    haptics.Add(PeripheralLinker.GameSenseTactilePath);
            }
            return (lighting.ToArray(), haptics.ToArray());
        }

        /// <summary>With no direct backend, every row is linked to the paths
        /// it had before one existed, whichever vendor programs run. The
        /// one thing a row gains is the note that a path of its own waits
        /// behind G HUB, for the zone-only Logitech device that is lit on
        /// its own once G HUB stops. And with direct units, the rumble paths
        /// are still those, beside any TouchSense unit the row matches.</summary>
        [Fact]
        public void WithNoDirectUnits_EveryRowIsLinkedAsBefore_AndRumbleNeverMoves_OverGeneratedInputs()
        {
            var random = new Random(4940);
            for (int round = 0; round < 4000; round++)
            {
                var rows = RandomRows(random);
                var presence = RandomPresence(random);
                var hidpp = RandomHidpp(random, withPlans: false);

                var bare = PeripheralLinker.Build(rows, Snapshot(hidpp), presence);
                Assert.True(bare.SameAs(PeripheralLinker.Build(rows, new LinkInputs(Snapshot(hidpp), presence))));
                var direct = RandomUnits(random);
                var withUnits = PeripheralLinker.Build(rows, new LinkInputs(Snapshot(hidpp), presence) { Units = direct });
                foreach (var row in rows)
                {
                    var (lighting, haptics) = BeforeDirectUnits(row, hidpp, presence);
                    var links = bare.For(row.Device);
                    Assert.Equal(lighting, links?.Lighting ?? Array.Empty<OutputPath>());
                    Assert.Equal(haptics, links?.Haptics ?? Array.Empty<OutputPath>());
                    if (links != null)
                    {
                        Assert.Equal(0, links.MaxClaimLevel);
                        Assert.Empty(links.Shadows);
                        bool waits = row.VendorId == Logitech && presence.LogitechGHubRunning && presence.LogitechLedSdk
                                     && hidpp.Any(u => PeripheralLinker.Matches(u, row) && u.DirectRgb);
                        Assert.Equal(waits, links.DirectWhenClosed);
                        if (waits) Assert.All(links.Lighting, path => Assert.Equal(OutputFamily.LedSdkType, path.Family));
                    }

                    var after = withUnits.For(row.Device)?.Haptics ?? Array.Empty<OutputPath>();
                    Assert.Equal(haptics, after.Where(p => p.Family != OutputFamily.TouchSenseUnit));
                    Assert.All(after.Where(p => p.Family == OutputFamily.TouchSenseUnit), path =>
                    {
                        var unit = direct.Single(u => u.Family == path.Family && u.Key == path.Key);
                        Assert.True(PeripheralLinker.MatchesDevice(unit, row));
                        Assert.False(unit.Asleep);
                    });
                }
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Presence, the collection walk, the channel, the unit row: no shared state
    // ─────────────────────────────────────────────

    /// <summary>The parts under the direct backends (#494): who runs, which
    /// HID collections are present, how one is opened and written, and the
    /// row a lone lit unit gets. One test lifts the test host's refusal to
    /// ask the real open about a path no collection is at, so the class
    /// runs with no other beside it.</summary>
    [Collection("PeripheralOutputStatics")]
    public class DirectPathPlumbingTests
    {
        private static readonly PeripheralPresence Installed = new(true, false, true, true, true);

        [Fact]
        public void TheRunningFlags_ComeFromImageNames_WithoutTheirExtensionOrTheirCase()
        {
            var none = PeripheralPresence.WithRunning(Installed, new[] { "explorer.exe", "PadForge.exe" });
            Assert.False(none.LogitechGHubRunning || none.LogitechGamingSoftware || none.RazerSoftwareRunning || none.SteelSeriesGGRunning);
            // What is installed is not theirs to change.
            Assert.True(none.LogitechLedSdk && none.RazerSynapse && none.SteelSeriesGG && none.SensaPlatform);
            Assert.Equal(Installed, PeripheralPresence.WithRunning(Installed, null));

            Assert.True(PeripheralPresence.WithRunning(Installed, new[] { "LGHUB_AGENT.EXE" }).LogitechGHubRunning);
            Assert.False(PeripheralPresence.WithRunning(Installed, new[] { "lghub_agent.exe" }).LogitechGamingSoftware);
            // Logitech Gaming Software is named only while G HUB does not run.
            foreach (string lgs in new[] { "lgs.exe", "LCore.exe" })
            {
                var old = PeripheralPresence.WithRunning(Installed, new[] { lgs });
                Assert.True(old.LogitechGHubRunning && old.LogitechGamingSoftware);
                Assert.False(PeripheralPresence.WithRunning(Installed, new[] { lgs, "lghub_agent.exe" }).LogitechGamingSoftware);
            }

            foreach (string name in PeripheralPresence.RazerHostProcesses)
                Assert.True(PeripheralPresence.WithRunning(Installed, new[] { name.ToUpperInvariant() + ".exe" }).RazerSoftwareRunning, name);
            // The Chroma SDK services outlive Synapse and stay out.
            Assert.False(PeripheralPresence.WithRunning(Installed,
                new[] { "RzSDKService.exe", "RzSDKServer.exe", "RzChromaStreamServer.exe" }).RazerSoftwareRunning);
            Assert.Equal(new[] { "RazerAppEngine", "Razer Synapse 3", "Razer Synapse Service", "Razer Synapse Service Process", "RazerCentralService" },
                PeripheralPresence.RazerHostProcesses);

            // For Logitech and Razer a name is the whole name: no prefix, no
            // extension but ".exe".
            var near = PeripheralPresence.WithRunning(Installed,
                new[] { "lgs2.exe", "lghub_agent_helper.exe", "Razer Synapse 3" + ".dll", "RazerAppEngineHost.exe" });
            Assert.False(near.LogitechGHubRunning || near.RazerSoftwareRunning || near.SteelSeriesGGRunning);
        }

        /// <summary>SteelSeries GG is several processes, and no source
        /// lists them all, so it is found by the start of a name: GG, its
        /// engine and Prism, and with them the client processes the vendor
        /// names. A wider match can only close the direct path.</summary>
        [Fact]
        public void SteelSeriesGG_IsSeenByTheStartOfAProcessName_WithoutCase()
        {
            Assert.Equal(new[] { "SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesPrism" }, PeripheralPresence.SteelSeriesHostPrefixes);
            foreach (string image in new[]
            {
                "SteelSeriesGG.exe", "SteelSeriesEngine.EXE", "SteelSeriesPrism.exe",
                // The two client processes SteelSeries lists beside them.
                "SteelSeriesGGClient.exe", "steelseriesggezclient.exe",
                // A process with no extension is named as it is.
                "STEELSERIESENGINE",
            })
            {
                var seen = PeripheralPresence.WithRunning(Installed, new[] { "explorer.exe", image });
                Assert.True(seen.SteelSeriesGGRunning, image);
                Assert.False(seen.RazerSoftwareRunning || seen.LogitechGHubRunning, image);
            }

            // Another SteelSeries program, and names that only start alike
            // or hold the name further in.
            Assert.False(PeripheralPresence.WithRunning(Installed,
                new[] { "SteelSeriesSonar.exe", "Steel.exe", "SteelSeries.exe", "NotSteelSeriesGG.exe" }).SteelSeriesGGRunning);
            Assert.False(PeripheralPresence.WithRunning(Installed, Array.Empty<string>()).SteelSeriesGGRunning);
        }

        /// <summary>A snapshot of the processes that could not be taken
        /// says nothing about what runs, so the flags read before it stand:
        /// read as nothing running, it would hand a running program's
        /// devices to a direct worker. Only a read with none before it has
        /// nothing to keep.</summary>
        [Fact]
        public void AFailedProcessSnapshot_KeepsTheRunningFlagsLastRead()
        {
            var last = Installed with { LogitechGHubRunning = true, RazerSoftwareRunning = true, SteelSeriesGGRunning = true };
            var installedNow = new PeripheralPresence(false, false, true, false, false);

            var kept = PeripheralPresence.WithSnapshot(installedNow, null, last);
            Assert.True(kept.LogitechGHubRunning && kept.RazerSoftwareRunning && kept.SteelSeriesGGRunning);
            // What is installed is this read's own, never the last one's.
            Assert.False(kept.LogitechLedSdk || kept.SteelSeriesGG);
            Assert.True(kept.RazerSynapse);

            // No read before it: nothing to keep.
            var first = PeripheralPresence.WithSnapshot(installedNow, null, null);
            Assert.False(first.LogitechGHubRunning || first.RazerSoftwareRunning || first.SteelSeriesGGRunning);
            // A snapshot that was taken is the answer, whatever came before.
            var fresh = PeripheralPresence.WithSnapshot(installedNow, new[] { "explorer.exe" }, last);
            Assert.False(fresh.LogitechGHubRunning || fresh.RazerSoftwareRunning || fresh.SteelSeriesGGRunning);
            Assert.True(PeripheralPresence.WithSnapshot(installedNow, new[] { "RazerAppEngine.exe" }, null).RazerSoftwareRunning);
            Assert.Null(PeripheralPresence.WithSnapshot(null, null, last));

            // The host's full read, every five seconds, goes through it.
            var watch = new VendorProgramWatch(() => installedNow, () => new uint[] { 4, 8 }, () => null);
            var read = watch.Read(last);
            Assert.True(read.LogitechGHubRunning && read.RazerSoftwareRunning && read.SteelSeriesGGRunning);
            Assert.False(read.LogitechLedSdk);
            Assert.False(watch.Read(null).RazerSoftwareRunning);
            // And so does the read of the running programs alone.
            Assert.Same(last, watch.ReadRunning(last));
            Assert.Null(watch.ReadRunning(null));
        }

        /// <summary>While a device is lit on its own the running programs
        /// are read on every pass. A process snapshot is the costly part,
        /// so a pass first reads the list of process IDs and takes a
        /// snapshot only when that list differs from the one seen at the
        /// last snapshot: no process came or went, so no flag moved. The
        /// read every five seconds takes one whatever the list says.</summary>
        [Fact]
        public void TheFastRead_TakesASnapshotOnlyWhenTheProcessListMoved_AndTheFullReadAlways()
        {
            uint[] ids = { 4, 100, 200 };
            var names = new List<string> { "explorer.exe" };
            var order = new List<string>();
            var watch = new VendorProgramWatch(() => Installed,
                () => { order.Add("ids"); return ids; },
                () => { order.Add("names"); return names; });

            // Nothing seen yet: the first fast read takes a snapshot, the
            // IDs first and the names after.
            var current = watch.ReadRunning(Installed);
            Assert.Equal(1, watch.Snapshots);
            Assert.Equal(new[] { "ids", "names" }, order);
            Assert.False(current.RazerSoftwareRunning);

            // The same list: no snapshot, and the same presence back.
            order.Clear();
            Assert.Same(current, watch.ReadRunning(current));
            Assert.Same(current, watch.ReadRunning(current));
            Assert.Equal(1, watch.Snapshots);
            Assert.Equal(new[] { "ids", "ids" }, order);

            // A process started: the list differs, so the names are read.
            ids = new uint[] { 4, 100, 200, 300 };
            names = new List<string> { "explorer.exe", "RazerAppEngine.exe" };
            current = watch.ReadRunning(current);
            Assert.Equal(2, watch.Snapshots);
            Assert.True(current.RazerSoftwareRunning);
            Assert.Same(current, watch.ReadRunning(current));
            Assert.Equal(2, watch.Snapshots);

            // One ended and another took its place in the list, the same
            // count: still a list that differs.
            ids = new uint[] { 4, 100, 204, 300 };
            names = new List<string> { "explorer.exe" };
            current = watch.ReadRunning(current);
            Assert.Equal(3, watch.Snapshots);
            Assert.False(current.RazerSoftwareRunning);

            // The full read takes one although the list has not moved, and
            // leaves the fast read its baseline.
            var full = watch.Read(current);
            Assert.Equal(4, watch.Snapshots);
            Assert.Same(full, watch.ReadRunning(full));
            Assert.Equal(4, watch.Snapshots);
        }

        /// <summary>The edges of the same rule, each erring toward one more
        /// snapshot and never toward a program that started unseen.</summary>
        [Fact]
        public void TheFastRead_LooksAgain_WheneverItCannotTellThatNothingMoved()
        {
            uint[] ids = { 4, 100 };
            List<string> names = new() { "explorer.exe" };
            Action duringSnapshot = null;
            var watch = new VendorProgramWatch(() => Installed, () => ids,
                () => { duringSnapshot?.Invoke(); return names; });
            var current = watch.ReadRunning(Installed);
            Assert.Equal(1, watch.Snapshots);

            // The ID list cannot be read: a snapshot, every time.
            var held = ids;
            ids = null;
            watch.ReadRunning(current);
            watch.ReadRunning(current);
            Assert.Equal(3, watch.Snapshots);
            // Nor is a list that could not be read a baseline afterward.
            ids = held;
            watch.ReadRunning(current);
            Assert.Equal(4, watch.Snapshots);
            watch.ReadRunning(current);
            Assert.Equal(4, watch.Snapshots);

            // A snapshot that fails keeps the flags, and is no baseline: the
            // next read takes one again although the list is the same.
            var running = current with { SteelSeriesGGRunning = true };
            ids = new uint[] { 4, 100, 300 };
            names = null;
            Assert.Same(running, watch.ReadRunning(running));
            Assert.Equal(5, watch.Snapshots);
            names = new List<string> { "explorer.exe" };
            Assert.False(watch.ReadRunning(running).SteelSeriesGGRunning);
            Assert.Equal(6, watch.Snapshots);

            // A process that starts while the snapshot is taken is in the
            // names and was not in the IDs read before it. The baseline is
            // that earlier list, so the next read sees a list that moved.
            duringSnapshot = () => ids = new uint[] { 4, 100, 300, 400 };
            ids = new uint[] { 4, 100, 300, 350 };
            watch.ReadRunning(current);
            Assert.Equal(7, watch.Snapshots);
            duringSnapshot = null;
            watch.ReadRunning(current);
            Assert.Equal(8, watch.Snapshots);
            watch.ReadRunning(current);
            Assert.Equal(8, watch.Snapshots);

            // A reader that throws is one that failed.
            var broken = new VendorProgramWatch(() => Installed, () => throw new InvalidOperationException(),
                () => throw new InvalidOperationException());
            Assert.Same(running, broken.ReadRunning(running));
            Assert.True(broken.Read(running).SteelSeriesGGRunning);

            // So is a list with nothing in it: Windows always runs
            // something, and two empty lists would compare equal.
            var empty = new VendorProgramWatch(() => Installed, () => Array.Empty<uint>(),
                () => new List<string> { "explorer.exe" });
            empty.ReadRunning(Installed);
            empty.ReadRunning(Installed);
            empty.ReadRunning(Installed);
            Assert.Equal(3, empty.Snapshots);
        }

        /// <summary>The list of process IDs is real: it names this test's
        /// own process, and no process twice.</summary>
        [Fact]
        public void TheProcessIdList_SeesThisProcess()
        {
            var ids = RunningProcesses.ProcessIds();
            Assert.NotNull(ids);
            Assert.Contains((uint)Environment.ProcessId, ids);
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }

        [Fact]
        public void AScriptedHost_TakesItsRunningFlagsFromItsOwnPresenceRead()
        {
            var running = PeripheralPresence.None with
            {
                LogitechGHubRunning = true, LogitechGamingSoftware = true, RazerSoftwareRunning = true, SteelSeriesGGRunning = true,
            };
            var copied = PeripheralPresence.CopyRunning(Installed, running);
            Assert.True(copied.LogitechGHubRunning && copied.LogitechGamingSoftware && copied.RazerSoftwareRunning && copied.SteelSeriesGGRunning);
            Assert.True(copied.LogitechLedSdk && copied.RazerSynapse && copied.SteelSeriesGG && copied.SensaPlatform);
            Assert.Equal(Installed, PeripheralPresence.CopyRunning(Installed, null));
            Assert.Null(PeripheralPresence.CopyRunning(null, running));
        }

        /// <summary>The snapshot is real: it names this test's own process,
        /// and a read of the running flags leaves what is installed alone.</summary>
        [Fact]
        public void TheProcessSnapshot_SeesThisProcess()
        {
            var names = RunningProcesses.ImageNames();
            Assert.NotNull(names);
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            Assert.Contains(names, n => string.Equals(n, me.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(n, me.ProcessName, StringComparison.OrdinalIgnoreCase));

            // The host's own watch reads the PC's processes when nothing is
            // scripted, and takes its first snapshot at its first read.
            var watch = new VendorProgramWatch();
            var read = watch.ReadRunning(Installed);
            Assert.Equal(1, watch.Snapshots);
            Assert.True(read.LogitechLedSdk && read.RazerSynapse && read.SteelSeriesGG && read.SensaPlatform);
            Assert.Equal(PeripheralPresence.WithRunning(Installed, names).LogitechGHubRunning, read.LogitechGHubRunning);
        }

        [Theory]
        [InlineData(@"\\?\hid#vid_048d&pid_c193&mi_01#9&3517b0ea&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 1)]
        [InlineData(@"\\?\HID#VID_046D&PID_C548&MI_02&Col02#8&b41415e&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}", 2)]
        // A keyboard collection's path as Windows lists it, reference string and all.
        [InlineData(@"\\?\hid#vid_046d&pid_c54d&mi_01&col01#9&372bce97&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}\kbd", 1)]
        [InlineData(@"\\?\hid#vid_1532&pid_0084&mi_0a#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 10)]
        [InlineData(@"\\?\hid#vid_1532&pid_0084#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", -1)]
        [InlineData(@"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b042&col03#9&1&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}", -1)]
        // The token in an instance ID is no interface number.
        [InlineData(@"\\?\hid#vid_1532&pid_0084#7&mi_03&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", -1)]
        [InlineData(@"\\?\hid#vid_1532&pid_0084&mi_#x#{4d1e55b2-f16f-11cf-88cb-001111000030}", -1)]
        [InlineData(@"\\?\hid#vid_1532&pid_0084&mi_12345#x#{4d1e55b2-f16f-11cf-88cb-001111000030}", -1)]
        [InlineData("vid_1532&pid_0084&mi_03", 3)]
        [InlineData("", -1)]
        [InlineData(null, -1)]
        public void TheInterfaceNumber_IsReadFromTheDeviceSegmentOfThePath(string path, int expected)
        {
            Assert.Equal(expected, HidCollectionRuntime.InterfaceNumberOf(path));
        }

        /// <summary>A collection is read once per appearance, a read that
        /// failed is made again on the next walk, and a path that left is
        /// read again when it comes back.</summary>
        [Fact]
        public void TheWalk_KeepsAVerdictPerPath_AsksAgainAfterAFailedRead_AndForgetsWhatLeft()
        {
            var cache = new Dictionary<string, HidCollectionInfo>(StringComparer.OrdinalIgnoreCase);
            var probes = new List<string>();
            bool readable = false;
            HidCollectionInfo Probe(string path)
            {
                probes.Add(path);
                if (path == "locked" && !readable) return null;
                return new HidCollectionInfo { Path = path, UsagePage = 0x59, Usage = 1, FeatureReportLength = 51 };
            }

            var first = HidCollectionRuntime.Collect(cache, new[] { "lamp", "locked", "LAMP", "" }, Probe);
            Assert.Equal(new[] { "lamp" }, first.Select(c => c.Path));
            Assert.Equal(new[] { "lamp", "locked" }, probes);
            Assert.Equal(-1, first[0].InterfaceNumber);

            readable = true;
            var second = HidCollectionRuntime.Collect(cache, new[] { "lamp", "locked" }, Probe);
            Assert.Equal(new[] { "lamp", "locked" }, second.Select(c => c.Path));
            Assert.Equal(new[] { "lamp", "locked", "locked" }, probes);
            Assert.Same(first[0], second[0]);

            // Nothing new to read, and nothing read.
            HidCollectionRuntime.Collect(cache, new[] { "lamp", "locked" }, Probe);
            Assert.Equal(3, probes.Count);

            // "lamp" left, then came back: read again.
            Assert.Equal(new[] { "locked" }, HidCollectionRuntime.Collect(cache, new[] { "locked" }, Probe).Select(c => c.Path));
            HidCollectionRuntime.Collect(cache, new[] { "lamp", "locked" }, Probe);
            Assert.Equal(new[] { "lamp", "locked", "locked", "lamp" }, probes);

            // A probe that throws is a read that failed, and a walk that
            // could not be made is no empty list.
            Assert.Empty(HidCollectionRuntime.Collect(cache, new[] { "gone" }, _ => throw new InvalidOperationException()));
            Assert.Null(HidCollectionRuntime.Collect(cache, null, Probe));
        }

        /// <summary>The filters the existing walk has, which this one drops:
        /// the usage page floor and the input report.</summary>
        [Fact]
        public void TheWalk_KeepsCollectionsBelowTheVendorPages_AndOnesWithNoInputReport()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "HidCollectionRuntime.cs");
            Assert.DoesNotContain("VendorPageMin", source);
            Assert.DoesNotContain("InputReportByteLength == 0", source);
            // It asks the device for nothing: no string, no report.
            Assert.DoesNotContain("ReadProductString", source);
            Assert.DoesNotContain("HidD_GetProductString", source);
            Assert.DoesNotContain("HidD_GetFeature", source);
            Assert.DoesNotContain("HidD_SetFeature", source);
            Assert.DoesNotContain("WriteFile", source);
            Assert.DoesNotContain("ReadFile", source);
            Assert.Contains("SonyHeadsetHid.CreateFile(path, 0,", source);
        }

        // ── The channel ──

        /// <summary>The open is tried with read and write access, and with
        /// no access right only after Windows refused the first for a
        /// collection it holds itself.</summary>
        [Fact]
        public void TheOpenLadder_FallsBackToNoAccess_OnlyOnAccessDeniedOrASharingViolation()
        {
            const uint readWrite = 0xC0000000;
            var asked = new List<uint>();
            (string Handle, int Error) Open(uint access, int firstError)
            {
                asked.Add(access);
                if (access == readWrite && firstError != 0) return (null, firstError);
                return ("handle:" + access, 0);
            }

            Assert.Equal("handle:" + readWrite, HidReportChannel.OpenLadder(a => Open(a, 0), out bool featureOnly));
            Assert.False(featureOnly);
            Assert.Equal(new[] { readWrite }, asked);

            foreach (int refused in new[] { HidReportChannel.ErrorAccessDenied, HidReportChannel.ErrorSharingViolation })
            {
                asked.Clear();
                Assert.Equal("handle:0", HidReportChannel.OpenLadder(a => Open(a, refused), out featureOnly));
                Assert.True(featureOnly);
                Assert.Equal(new uint[] { readWrite, 0 }, asked);
            }
            Assert.Equal(5, HidReportChannel.ErrorAccessDenied);
            Assert.Equal(32, HidReportChannel.ErrorSharingViolation);

            // The collection is gone: one open, not two.
            asked.Clear();
            Assert.Null(HidReportChannel.OpenLadder(a => Open(a, 2), out featureOnly));
            Assert.False(featureOnly);
            Assert.Equal(new[] { readWrite }, asked);

            // Both rungs refused.
            Assert.Null(HidReportChannel.OpenLadder<string>(a => (null, 5), out featureOnly));
            Assert.False(featureOnly);
        }

        /// <summary>The LampArray range report of the bench unit, 10 bytes on
        /// a collection whose feature report is 51: Windows gets 51, the 10
        /// then zeros.</summary>
        [Fact]
        public void AShortReport_GoesOutPaddedToTheCollectionsLength_AndALongOneAsItIs()
        {
            byte[] range = { 0x05, 0x01, 0x00, 0x00, 0x3D, 0x01, 0x1E, 0x90, 0xFF, 0x01 };
            byte[] scratch = null;
            byte[] padded = HidReportChannel.Pad(range, 51, ref scratch);
            Assert.Equal(51, padded.Length);
            Assert.Equal(range, padded.Take(10));
            Assert.All(padded.Skip(10), b => Assert.Equal(0, b));
            Assert.Same(scratch, padded);

            // The scratch is reused, and a shorter report leaves no tail of
            // the one before it.
            byte[] control = { 0x06, 0x01 };
            byte[] again = HidReportChannel.Pad(control, 51, ref scratch);
            Assert.Same(padded, again);
            Assert.Equal(new byte[] { 0x06, 0x01 }, again.Take(2));
            Assert.All(again.Skip(2), b => Assert.Equal(0, b));

            // The right length or more goes out as it is.
            byte[] exact = new byte[91];
            Assert.Same(exact, HidReportChannel.Pad(exact, 91, ref scratch));
            byte[] longer = new byte[92];
            Assert.Same(longer, HidReportChannel.Pad(longer, 91, ref scratch));
        }

        /// <summary>The crash path's call ends on time whatever the device
        /// does: a call that never returns is waited for no longer than the
        /// bound, and is left to finish on its own thread.</summary>
        [Fact]
        public void ABoundedCall_ReturnsItsResultInTime_AndGivesUpOnOneThatDoesNotReturn()
        {
            Assert.True(HidReportChannel.RunBounded(() => true, 30000));
            Assert.False(HidReportChannel.RunBounded(() => false, 30000));
            Assert.False(HidReportChannel.RunBounded(() => throw new InvalidOperationException(), 30000));
            Assert.False(HidReportChannel.RunBounded(null, 30000));

            // A call that returns only once the test lets it can never come
            // back inside its bound, however slow the machine is.
            using var started = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var finished = new ManualResetEventSlim(false);
            Thread helper = null;
            bool inTime = HidReportChannel.RunBounded(() =>
            {
                helper = Thread.CurrentThread;
                started.Set();
                release.Wait();
                finished.Set();
                return true;
            }, 50);
            Assert.False(inTime);
            Assert.True(started.Wait(30000), "the call ran");
            // It ran on a thread of its own, which holds no process open.
            Assert.NotEqual(Environment.CurrentManagedThreadId, helper.ManagedThreadId);
            Assert.True(helper.IsBackground);
            Assert.False(finished.IsSet);
            release.Set();
            Assert.True(finished.Wait(30000), "the call was left to finish");
            // The events outlive the helper's last use of them.
            Assert.True(helper.Join(30000));
        }

        /// <summary>A path that names nothing opens nothing, on either rung,
        /// and its one-shots fail inside their bound. No device is touched:
        /// the path matches no interface.</summary>
        [Fact]
        public void ACollectionThatIsNotThere_OpensNothing()
        {
            const string nowhere = @"\\?\hid#vid_0000&pid_0000&mi_7f#padforge-test-no-such-collection#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            // The open itself is under test here, so the test host's
            // refusal is lifted for it. The path names no device.
            using var real = RealHidAccess.AllowForTest();
            Assert.Null(HidReportChannel.Open(nowhere));
            Assert.Null(HidReportChannel.OpenFeatureOnly(nowhere));
            Assert.Null(HidReportChannel.Open(null));
            Assert.Null(HidReportChannel.Open(string.Empty));
            Assert.False(HidReportChannel.SetFeatureOnce(nowhere, new byte[] { 0x06, 0x01 }, 2000));
            Assert.False(HidReportChannel.WriteOutputOnce(nowhere, new byte[] { 0x00, 0x05 }, 2000));
        }

        /// <summary>The channel's overlapped write and read, run against a
        /// pipe, which takes them as a HID collection does. A short report
        /// goes out at the collection's length. A read with nothing to read
        /// gives up at its bound and leaves the channel good for the next
        /// one. A write nothing takes fails at its bound. No device is
        /// involved, and every outcome holds however slow the machine is.</summary>
        [Fact]
        public async Task TheChannelsWriteAndRead_MoveWholeReports_AndGiveUpAtTheirBound()
        {
            var patience = TimeSpan.FromSeconds(30);
            using var pipe = PipeEnd.Open(out var handle);
            using var channel = HidReportChannel.ForHandle("pipe", handle, featureOnly: false,
                inputLength: 8, outputLength: 8, featureLength: 0);

            // A three-byte report goes out as the collection's eight.
            var taken = new byte[8];
            var take = ReadWhole(pipe.Server, taken);
            Assert.True(channel.WriteOutput(new byte[] { 0x00, 0x05, 0x01 }, 30000));
            Assert.Equal(8, await take.WaitAsync(patience));
            Assert.Equal(new byte[] { 0x00, 0x05, 0x01, 0, 0, 0, 0, 0 }, taken);

            // Nothing to read: no report, and no failure either.
            var report = new byte[8];
            Assert.Equal(0, channel.ReadInput(report, 50));

            // The read that gave up left the channel good for the next one.
            var send = pipe.Server.WriteAsync(new byte[] { 0x00, 0x61, 0x01, 2, 3, 4, 5, 6 }, 0, 8);
            Assert.Equal(8, channel.ReadInput(report, 30000));
            await send.WaitAsync(patience);
            Assert.Equal(new byte[] { 0x00, 0x61, 0x01, 2, 3, 4, 5, 6 }, report);

            // A shorter buffer takes the head of the report.
            send = pipe.Server.WriteAsync(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 }, 0, 8);
            var head = new byte[3];
            Assert.Equal(3, channel.ReadInput(head, 30000));
            await send.WaitAsync(patience);
            Assert.Equal(new byte[] { 9, 8, 7 }, head);

            // And a write still goes through after all of it.
            take = ReadWhole(pipe.Server, taken);
            Assert.True(channel.WriteOutput(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 30000));
            Assert.Equal(8, await take.WaitAsync(patience));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, taken);

            channel.Dispose();
            Assert.False(channel.WriteOutput(new byte[] { 0x00 }, 50));
            Assert.Equal(-1, channel.ReadInput(report, 50));

            // A report nothing takes: far more than a pipe holds, and
            // nobody reads the other end.
            using var stuck = PipeEnd.Open(out var stuckHandle);
            using var wide = HidReportChannel.ForHandle("pipe", stuckHandle, featureOnly: false,
                inputLength: 8, outputLength: 60000, featureLength: 0);
            Assert.False(wide.WriteOutput(new byte[] { 0x00, 0x05 }, 50));

            // A handle that carries feature reports alone writes and reads
            // none, and a collection with no feature report takes none.
            using var closed = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, ownsHandle: false);
            using var limited = HidReportChannel.ForHandle("none", closed, featureOnly: true, 8, 8, 0);
            Assert.True(limited.FeatureOnly);
            Assert.False(limited.WriteOutput(new byte[] { 0x00 }, 50));
            Assert.Equal(-1, limited.ReadInput(report, 50));
            Assert.False(limited.SetFeature(new byte[] { 0x00 }));
            Assert.False(limited.GetFeature(0x00, report));
        }

        private static async Task<int> ReadWhole(System.IO.Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total)).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        /// <summary>One end of a pipe for the channel and the other for
        /// the test, both overlapped, with no buffer of the pipe's own
        /// asked for.</summary>
        private sealed class PipeEnd : IDisposable
        {
            public System.IO.Pipes.NamedPipeServerStream Server { get; private set; }

            public static PipeEnd Open(out Microsoft.Win32.SafeHandles.SafeFileHandle client)
            {
                string name = "padforge-test-" + Guid.NewGuid().ToString("N");
                var end = new PipeEnd
                {
                    Server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 0),
                };
                client = SonyHeadsetHid.CreateFile(@"\\.\pipe\" + name,
                    SonyHeadsetHid.GENERIC_READ | SonyHeadsetHid.GENERIC_WRITE, 0, IntPtr.Zero,
                    SonyHeadsetHid.OPEN_EXISTING, SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                Assert.False(client.IsInvalid, "the pipe opened");
                end.Server.WaitForConnection();
                return end;
            }

            public void Dispose() => Server?.Dispose();
        }

        /// <summary>The scripted channel the direct backends' tests share
        /// records what a backend sent, in order, before any padding.</summary>
        [Fact]
        public void TheScriptedChannel_RecordsEachCall_AnswersFromItsScript_AndFailsOnItsFlag()
        {
            long tick = 100;
            var channel = new FakeReportChannel
            {
                Ticks = () => tick, InputReportLength = 64, OutputReportLength = 65,
                Feature = id => id == 0x00 ? new byte[] { 0x00, 0x02, 0x1F } : null,
                Input = () => new byte[] { 0x00, 0x61, 0x01 },
            };
            IHidReportChannel io = channel;
            byte[] sent = { 0x00, 0x00, 0x1F };
            Assert.True(io.SetFeature(sent));
            sent[2] = 0xFF;
            tick = 104;
            var reply = new byte[91];
            Assert.True(io.GetFeature(0x00, reply));
            Assert.Equal(new byte[] { 0x00, 0x02, 0x1F }, reply.Take(3));
            Assert.False(io.GetFeature(0x07, reply));
            Assert.True(io.WriteOutput(new byte[] { 0x00, 0x21, 0x01 }));
            var read = new byte[64];
            Assert.Equal(3, io.ReadInput(read, 100));
            Assert.Equal(0x61, read[1]);

            var calls = channel.Calls.ToArray();
            Assert.Equal(new[] { "set", "get", "get", "out", "in" }, calls.Select(c => c.Kind));
            // The bytes are a copy: what was sent, not what the caller did
            // with its buffer afterward.
            Assert.Equal(new byte[] { 0x00, 0x00, 0x1F }, calls[0].Bytes);
            Assert.Equal(100, calls[0].Tick);
            Assert.Equal(104, calls[1].Tick);

            channel.FailWrites = true;
            Assert.False(io.SetFeature(sent));
            Assert.False(io.WriteOutput(sent));
            Assert.Equal(5, channel.Calls.Count);

            // A mouse or keyboard collection carries feature reports alone.
            var featureOnly = new FakeReportChannel { FeatureOnly = true };
            Assert.True(featureOnly.SetFeature(sent));
            Assert.False(featureOnly.WriteOutput(sent));
            Assert.Equal(-1, featureOnly.ReadInput(read, 10));
            featureOnly.Dispose();
            Assert.False(featureOnly.SetFeature(sent));
        }

        // ── The unit row ──

        [Fact]
        public void AUnitRow_IsALightingRowWithNoInputs_NamedForItsUnit()
        {
            var unit = new DirectUnit(OutputFamily.LampArrayUnit,
                @"\\?\hid#vid_048d&pid_c193&mi_01#9&3517b0ea&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                PeripheralLinker.LocalMachineContainer, 0x048D, 0xC193)
            {
                Name = " Lenovo Lighting ", Kind = 1,
            };
            var row = new PeripheralUnitRow(unit);

            Assert.Equal("Lenovo Lighting", row.Name);
            Assert.Equal(InputDeviceType.PeripheralLighting, row.GetInputDeviceType());
            Assert.Equal(0x5046, row.VendorId);
            Assert.Equal(0x4C55, row.ProductId);
            Assert.Equal(0x4C55, PeripheralUnitRow.UnitProductId);
            Assert.Equal(OutputFamily.LampArrayUnit, row.Family);
            Assert.Equal(unit.Key, row.Key);
            Assert.Equal(unit.Path, row.Unit);
            Assert.StartsWith("lightunit://", row.DevicePath);
            Assert.True(SyntheticInstanceId.IsSynthetic(row.SdlInstanceId));
            Assert.Equal(0, row.NumAxes + row.NumButtons + row.NumHats + row.RawButtonCount);
            Assert.Empty(row.SupportedButtonIndices);
            Assert.Empty(row.GetDeviceObjects());
            Assert.False(row.HasRumble || row.HasHaptic || row.HasRumbleTriggers || row.SetRumble(1, 1) || row.StopRumble());
            Assert.IsAssignableFrom<IOutputOnlyRow>(row);
            Assert.NotNull(row.GetCurrentState());
            row.Dispose();
            Assert.False(row.IsAttached);
            Assert.Null(row.GetCurrentState());

            // The record a row loads: a Lighting row, like a vendor row.
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(new PeripheralUnitRow(unit));
            Assert.Equal(InputDeviceType.PeripheralLighting, ud.CapType);
            Assert.Equal(row.InstanceGuid, ud.InstanceGuid);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(ud.CapType));

            // A unit that gave no name is named for what it is.
            Assert.Equal("LampArray 048D:C193", PeripheralUnitRow.NameFor(unit with { Name = null }));
            Assert.Equal("LampArray 048D:C193", new PeripheralUnitRow(unit with { Name = "   " }).Name);
            Assert.Equal("Device 1532:0084",
                PeripheralUnitRow.NameFor(new DirectUnit(OutputFamily.RazerUnit, "k", Guid.Empty, 0x1532, 0x0084)));
            Assert.Throws<ArgumentNullException>(() => new PeripheralUnitRow(null));
        }

        /// <summary>A unit row stands for a lit device under a path of
        /// PadForge's own, which names no HID instance to cloak, and it has
        /// no input to hide: the Devices page offers it no Input Hiding, as
        /// it offers a vendor row none.</summary>
        [Fact]
        public void AUnitRow_OffersNoInputHiding()
        {
            using var row = new PeripheralUnitRow(new DirectUnit(OutputFamily.LampArrayUnit,
                @"\\?\hid#vid_17ef&pid_a5b5&mi_02#8&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                Guid.NewGuid(), 0x17EF, 0xA5B5));
            var vm = new DeviceRowViewModel { DeviceTypeKey = "PeripheralLighting", DevicePath = row.DevicePath };
            Assert.True(vm.IsInternalVirtual);
            Assert.False(vm.ShowHidHideToggle);
            Assert.False(vm.ShowInputHidingSection);
        }

        /// <summary>The same device on the same port is the same row in
        /// every session, so its assignment holds. The product id is the
        /// row's own too: a record found by product alone is adopted by the
        /// next device of that product, and two lit units would then trade
        /// assignments.</summary>
        [Fact]
        public void AUnitRowsIds_AreHashedFromItsFamilyAndKey_AndNeverSharedBetweenUnits()
        {
            const string key = @"\\?\hid#vid_17ef&pid_a5b5&mi_02#8&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var unit = new DirectUnit(OutputFamily.LampArrayUnit, key, Guid.NewGuid(), 0x17EF, 0xA5B5);
            var row = new PeripheralUnitRow(unit);

            Assert.Equal(PeripheralUnitRow.IdentityFor(OutputFamily.LampArrayUnit, key), row.InstanceGuid);
            // The container, the name and what Windows does are no part of it.
            Assert.Equal(row.InstanceGuid,
                new PeripheralUnitRow(unit with { ContainerId = Guid.Empty, Name = "Monitor", WindowsLit = true }).InstanceGuid);
            // Windows compares interface paths without regard to case.
            Assert.Equal(row.InstanceGuid, PeripheralUnitRow.IdentityFor(OutputFamily.LampArrayUnit, key.ToUpperInvariant()));
            // The id is pinned, so a session never mints a new row for a
            // device the user already assigned: MD5 of "pfunit:8:" and the
            // key in upper case.
            using (var md5 = System.Security.Cryptography.MD5.Create())
                Assert.Equal(new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("pfunit:8:" + key.ToUpperInvariant()))),
                    row.InstanceGuid);

            var otherPort = new PeripheralUnitRow(unit with { Key = key.Replace("8&1&0", "8&2&0") });
            var otherFamily = new PeripheralUnitRow(unit with { Family = OutputFamily.RazerUnit });
            Assert.NotEqual(row.InstanceGuid, otherPort.InstanceGuid);
            Assert.NotEqual(row.InstanceGuid, otherFamily.InstanceGuid);
            Assert.NotEqual(row.ProductGuid, otherPort.ProductGuid);
            Assert.NotEqual(row.ProductGuid, row.InstanceGuid);
            Assert.NotEqual(row.DevicePath, otherPort.DevicePath);
            Assert.NotEqual(row.SdlInstanceId, otherPort.SdlInstanceId);
            // No unit row is ever taken for a vendor row.
            Assert.Null(PeripheralOutputRow.KindOf(row.InstanceGuid));
        }

        // ── The TouchSense mouse's opt-in binding ──

        /// <summary>The binding that hands a TouchSense mouse to WinUSB has
        /// its kind, its name, its two Devices page texts and an arm of its
        /// own in every switch over the kind. The mice that offer it are
        /// the ones the backend that drives them lists
        /// (TouchSenseBindingTests).</summary>
        [Fact]
        public void TheTouchSenseBinding_HasItsKindItsNameAndItsTexts()
        {
            var kinds = Enum.GetValues(typeof(VendorUsbDriverInstaller.OptInKind))
                .Cast<VendorUsbDriverInstaller.OptInKind>().ToArray();
            // Appended: the kinds before it keep their numbers, and the
            // keyboard takeover (#549) came after it.
            Assert.Equal(new[] { "Xbox360Pad", "Xbox360Receiver", "IntelBaseStation", "Prodikeys", "TouchSenseMouse", "Keyboard" },
                kinds.Select(k => k.ToString()));
            var touchSense = VendorUsbDriverInstaller.OptInKind.TouchSenseMouse;
            Assert.Equal(4, (int)touchSense);
            Assert.Equal(5, (int)VendorUsbDriverInstaller.OptInKind.Keyboard);

            // Its devices are the TouchSense mice and nothing else.
            Assert.All(VendorUsbDriverInstaller.OptIns.Where(target => target.Kind == touchSense),
                target => Assert.True(TouchSenseProtocol.IsMouse(target.Vid, target.Pid)));
            Assert.True(VendorUsbDriverInstaller.IsOptInRow(0x046D, 0xC030));
            Assert.Equal(touchSense, VendorUsbDriverInstaller.KindOfId(@"USB\VID_046D&PID_C030"));

            var s = Strings.Instance;
            var vm = new DevicesViewModel();
            var offer = typeof(DevicesViewModel).GetField("_driverOffer", BindingFlags.NonPublic | BindingFlags.Instance);
            var cost = typeof(PadForge.MainWindow).GetMethod("DriverCost", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(offer);
            Assert.NotNull(cost);
            string BindText(VendorUsbDriverInstaller.OptInKind kind)
            {
                offer.SetValue(vm, new VendorUsbDriverInstaller.OptInOffer(kind, null, Array.Empty<string>(), "node", false));
                return vm.DriverBindText;
            }
            string Cost(VendorUsbDriverInstaller.OptInKind kind) => (string)cost.Invoke(null, new object[] { kind });

            // Every kind has a name, a button text and a cost text of its
            // own, in each of the three switches.
            foreach (var texts in new[]
            {
                kinds.Select(VendorUsbDriverInstaller.NameOf).ToList(),
                kinds.Select(BindText).ToList(),
                kinds.Select(Cost).ToList(),
            })
            {
                Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
                Assert.Equal(texts.Count, texts.Distinct().Count());
            }
            Assert.Equal("TouchSense mouse", VendorUsbDriverInstaller.NameOf(touchSense));
            Assert.Equal(s.Devices_DriverBind_TouchSenseMouse, BindText(touchSense));
            Assert.Equal(s.Devices_DriverConfirm_TouchSenseMouse, Cost(touchSense));
            // The kinds there before keep what they had.
            Assert.Equal("Creative Prodikeys PC-MIDI", VendorUsbDriverInstaller.NameOf(VendorUsbDriverInstaller.OptInKind.Prodikeys));
            Assert.Equal(s.Devices_DriverBind_Prodikeys, BindText(VendorUsbDriverInstaller.OptInKind.Prodikeys));
            Assert.Equal(s.Devices_DriverConfirm_Prodikeys, Cost(VendorUsbDriverInstaller.OptInKind.Prodikeys));

            // A kind none of them lists is refused, never given the text of
            // another kind.
            var unlisted = (VendorUsbDriverInstaller.OptInKind)99;
            Assert.Throws<ArgumentOutOfRangeException>(() => VendorUsbDriverInstaller.NameOf(unlisted));
            var refused = Assert.Throws<TargetInvocationException>(() => cost.Invoke(null, new object[] { unlisted }));
            Assert.IsType<ArgumentOutOfRangeException>(refused.InnerException);
            Assert.Null(BindText(unlisted));
        }

        /// <summary>The last sentence of a text, in the Latin scripts and in
        /// Japanese and Chinese, which end a sentence with their own mark.</summary>
        private static string LastSentence(string text)
        {
            string body = text.TrimEnd();
            int latin = body.LastIndexOf(". ", StringComparison.Ordinal);
            int cjk = body.Length > 1 ? body.LastIndexOf('。', body.Length - 2) : -1;
            return body.Substring(Math.Max(latin >= 0 ? latin + 2 : 0, cjk >= 0 ? cjk + 1 : 0));
        }

        /// <summary>Marks that end a sentence: the full stop and its
        /// counterparts in Devanagari and Bengali, Urdu, Armenian, Burmese,
        /// Ethiopic and Khmer, the CJK full stop, and the question and
        /// exclamation marks.</summary>
        private static readonly char[] SentenceEnds = { '.', '!', '?', '。', '।', '॥', '۔', '։', '။', '።', '។', '؟' };

        /// <summary>The closing sentence two texts share, in any script: the
        /// longest ending they have in common, from the first point inside it
        /// where a sentence starts in both. A sentence starts after a
        /// sentence end and a space, or right after a CJK full stop. Thai
        /// marks no sentence end, so in a text with no such mark a space
        /// starts one. Empty when the two share no whole sentence.</summary>
        internal static string SharedClosingSentence(string a, string b)
        {
            a = a.TrimEnd();
            b = b.TrimEnd();
            int n = 0;
            while (n < a.Length && n < b.Length && a[a.Length - 1 - n] == b[b.Length - 1 - n])
                n++;
            bool marked = a.Length > 1 && a.Substring(0, a.Length - 1).IndexOfAny(SentenceEnds) >= 0;
            int p = 0;
            while (p < n)
            {
                if (!char.IsWhiteSpace(a[a.Length - n + p])
                    && StartsSentence(a, a.Length - n + p, marked) && StartsSentence(b, b.Length - n + p, marked))
                    return a.Substring(a.Length - n + p);
                p++;
            }
            return "";
        }

        private static bool StartsSentence(string text, int at, bool marked)
        {
            if (at == 0) return true;
            char before = text[at - 1];
            if (before == '。') return true;
            if (!char.IsWhiteSpace(before)) return false;
            return !marked || (at >= 2 && Array.IndexOf(SentenceEnds, text[at - 2]) >= 0);
        }

        /// <summary>The shared closing sentence is found in each script the
        /// translations use, and a shared tail that is only part of a
        /// sentence is not one.</summary>
        [Fact]
        public void TheSharedClosingSentenceIsFoundInEveryScript()
        {
            Assert.Equal("Restore it here.", SharedClosingSentence("The keys move. Restore it here.", "The mouse moves. Restore it here."));
            Assert.Equal("इसे यहाँ से पुनर्स्थापित करें।", SharedClosingSentence("कुंजियाँ चलती हैं। इसे यहाँ से पुनर्स्थापित करें।", "माउस चलता है। इसे यहाँ से पुनर्स्थापित करें।"));
            Assert.Equal("随时在此还原。", SharedClosingSentence("按键会移动。随时在此还原。", "鼠标会移动。随时在此还原。"));
            Assert.Equal("กู้คืนได้จากหน้านี้ทุกเมื่อ", SharedClosingSentence("คีย์จะย้าย กู้คืนได้จากหน้านี้ทุกเมื่อ", "เมาส์จะย้าย กู้คืนได้จากหน้านี้ทุกเมื่อ"));
            // The same last words in two different sentences.
            Assert.Equal("", SharedClosingSentence("The keys move. Restore the driver here at any time.", "The mouse moves. Bring the driver back here at any time."));
        }

        /// <summary>The binding's two texts read as written, in every
        /// locale. The cost text says, before the user agrees, what the
        /// switch takes: Windows stops using the mouse as a pointer until
        /// its driver is restored, and PadForge moves the pointer from it
        /// only while PadForge runs and the mouse is on a Keyboard + Mouse
        /// virtual controller. It ends on the way back, as every other
        /// binding's cost text does.</summary>
        [Fact]
        public void TheTouchSenseBindingsTexts_SayWhatTheSwitchCosts_InEveryLocale()
        {
            string En(string key) => Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture);
            Assert.Equal("Play Rumble on This Mouse", En("Devices_DriverBind_TouchSenseMouse"));
            Assert.Equal("PadForge moves this mouse to Windows' WinUSB driver so it can play rumble on it. Windows then stops using this mouse as a pointer until its driver is restored. PadForge moves the pointer from it only while PadForge runs and the mouse is assigned to a Keyboard + Mouse virtual controller. Restore the Windows driver from this page at any time.",
                En("Devices_DriverConfirm_TouchSenseMouse"));
            Assert.Contains(En("ControllerType_KeyboardMouse"), En("Devices_DriverConfirm_TouchSenseMouse"));
            Assert.EndsWith(LastSentence(En("Devices_DriverConfirm_Prodikeys")), En("Devices_DriverConfirm_TouchSenseMouse"));
            // Nothing here asks for a restart of anything.
            Assert.DoesNotContain("restart", En("Devices_DriverConfirm_TouchSenseMouse"), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Restore the Windows driver from this page at any time.", LastSentence(En("Devices_DriverConfirm_Prodikeys")));

            foreach (string locale in LocaleFiles.CompleteTranslations)
            {
                var set = Strings.ResourceManager.GetResourceSet(new CultureInfo(locale), true, false);
                string bind = set.GetString("Devices_DriverBind_TouchSenseMouse");
                string confirm = set.GetString("Devices_DriverConfirm_TouchSenseMouse");
                Assert.False(string.IsNullOrWhiteSpace(bind), locale);
                Assert.False(string.IsNullOrWhiteSpace(confirm), locale);
                // A translation, never the English text left in place.
                Assert.NotEqual(En("Devices_DriverBind_TouchSenseMouse"), bind);
                Assert.NotEqual(En("Devices_DriverConfirm_TouchSenseMouse"), confirm);
                Assert.DoesNotContain("{0}", bind + confirm);
                Assert.NotEqual(set.GetString("Devices_DriverBind_Prodikeys"), bind);
                // The virtual controller is named as the type picker names
                // it, and the driver and the way back as the other
                // bindings' cost texts name them.
                Assert.Contains(set.GetString("ControllerType_KeyboardMouse"), confirm);
                Assert.Contains("WinUSB", confirm);
                // The way back is the closing sentence both cost texts share,
                // found in any script.
                string wayBack = SharedClosingSentence(set.GetString("Devices_DriverConfirm_Prodikeys"), confirm);
                Assert.True(wayBack.Length > 10 && wayBack.Length < confirm.Length,
                    locale + ": the cost text does not end on the way back the other bindings' texts end on");
            }
        }

        /// <summary>The row of a TouchSense mouse bound to WinUSB is opened
        /// and retired by the lane that drives the mouse, in a file of its
        /// own. Step 1 and the manager's shutdown each call a partial
        /// method for it, which compiles to nothing until that file fills
        /// it in.</summary>
        [Fact]
        public void Step1AndTheShutdown_CallTheTouchSenseLanesPartialMethods()
        {
            string rows = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.PeripheralRows.cs");
            Assert.Contains("partial void UpdateTouchSenseMice(ref bool changed);", rows);
            Assert.Contains("partial void ShutdownTouchSenseMice();", rows);

            string step1 = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            int peripheral = step1.IndexOf("changed |= UpdatePeripheralRows();", StringComparison.Ordinal);
            Assert.True(peripheral > 0, "Step 1 opens the peripheral rows");
            int touch = step1.IndexOf("UpdateTouchSenseMice(ref changed);", peripheral, StringComparison.Ordinal);
            int phase2 = step1.IndexOf("// --- Phase 2: Detect disconnected SDL devices", peripheral, StringComparison.Ordinal);
            Assert.True(touch > peripheral && phase2 > touch, $"{peripheral} {touch} {phase2}");

            string manager = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            int dispose = manager.IndexOf("public void Dispose()", StringComparison.Ordinal);
            Assert.True(dispose > 0, "the manager's Dispose is there");
            int rowsDown = manager.IndexOf("ShutdownPeripheralRows();", dispose, StringComparison.Ordinal);
            int touchDown = manager.IndexOf("ShutdownTouchSenseMice();", dispose, StringComparison.Ordinal);
            int sdl = manager.IndexOf("ShutdownSdl();", dispose, StringComparison.Ordinal);
            Assert.True(rowsDown > dispose && touchDown > rowsDown && sdl > touchDown, $"{dispose} {rowsDown} {touchDown} {sdl}");
        }

        // ── One name for one mode ──

        /// <summary>Logitech's on-board memory mode has one name in each
        /// locale, English among them. The three texts that say what the
        /// highest lighting hold switches off and the two texts of the
        /// G-keys setting, which tell a G600 or G300 owner to leave the
        /// same mode, use the same words for it, and none keeps a second
        /// name.</summary>
        [Theory]
        [InlineData("", "on-board memory mode", "on-board mode")]
        [InlineData("de", "Onboard-Modus", "Speichermodus")]
        [InlineData("es", "modo de memoria integrada", null)]
        [InlineData("fr", "mode mémoire intégrée", "Mémoire interne")]
        [InlineData("it", "modalità memoria integrata", "On-Board Memory Mode")]
        [InlineData("ja", "オンボードモード", "オンボードメモリ")]
        [InlineData("ko", "온보드 모드", "온보드 메모리")]
        [InlineData("nl", "onboardmodus", "On-Board Memory Mode")]
        [InlineData("pt-BR", "modo de memória integrada", "Modo Memória Integrada")]
        [InlineData("zh-Hans", "板载模式", "板载内存")]
        public void TheOnBoardMemoryMode_HasOneNameInEachLocale(string locale, string name, string former)
        {
            var set = Strings.ResourceManager.GetResourceSet(new CultureInfo(locale), true, false);
            foreach (string key in new[]
            {
                // The setting that named the mode first, in its tooltip
                // and in the line it shows while no key has come in.
                "Settings_GKeysTooltip", "Settings_GKeysStatus_NoKeysYet",
                "Pad_Lighting_ClaimLevel_HostMode", "Pad_Lighting_ClaimLevel_Tooltip", "Pad_Lighting_RouteDirectHostMode",
            })
            {
                string text = set.GetString(key);
                Assert.Contains(name, text, StringComparison.OrdinalIgnoreCase);
                if (former != null) Assert.DoesNotContain(former, text, StringComparison.OrdinalIgnoreCase);
            }
            // The other strings of the control never named the mode.
            foreach (string key in new[] { "Pad_Lighting_ClaimLevel", "Pad_Lighting_ClaimLevel_Colors", "Pad_Lighting_ClaimLevel_Power" })
                Assert.DoesNotContain(name, set.GetString(key), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>English names the mode as Logitech's own programs do.
        /// G HUB's switch reads ON-BOARD MEMORY MODE (Logitech support
        /// article 360057433714), and Logitech Gaming Software, which the
        /// G-keys setting needs, puts a G600 in On-Board Memory mode
        /// (article 360023411353). The G-key SDK's manual shortens it to
        /// on-board mode, which the two G-keys texts used to repeat. A
        /// sentence writes the name in lower case and an option name in
        /// title case, as every other option name is.</summary>
        [Fact]
        public void EnglishNamesTheOnBoardMemoryMode_AsLogitechsOwnProgramsDo()
        {
            static string En(string key) => Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture);
            const string rule = "A G600 or G300 mouse also has to be out of on-board memory mode.";
            Assert.EndsWith(rule, En("Settings_GKeysTooltip"), StringComparison.Ordinal);
            Assert.EndsWith(rule, En("Settings_GKeysStatus_NoKeysYet"), StringComparison.Ordinal);
            Assert.Equal("Colors, Lighting Power, and On-Board Memory Mode Off", En("Pad_Lighting_ClaimLevel_HostMode"));
            Assert.Contains("Its on-board memory mode is off while it is lit.", En("Pad_Lighting_RouteDirectHostMode"), StringComparison.Ordinal);
            Assert.Contains("Its on-board memory mode then goes off", En("Pad_Lighting_ClaimLevel_Tooltip"), StringComparison.Ordinal);
        }

        [Fact]
        public void TheRecordedOutputs_NameTheHoldsADeviceOffers()
        {
            Assert.False(new UserDevice { PeripheralOutputs = 3 }.HasPeripheralLightingLevel1);
            Assert.True(new UserDevice { PeripheralOutputs = 6 }.HasPeripheralLightingLevel1);
            Assert.False(new UserDevice { PeripheralOutputs = 6 }.HasPeripheralLightingLevel2);
            var both = new UserDevice { PeripheralOutputs = 14 };
            Assert.True(both.HasPeripheralLighting && both.HasPeripheralLightingLevel1 && both.HasPeripheralLightingLevel2);
            Assert.False(both.HasPeripheralHaptics);
        }

        /// <summary>The HID++ unit carries what a later probe fills, and a
        /// unit as the probe describes it today is lit by the zone-only
        /// class alone.</summary>
        [Fact]
        public void AHidppUnit_HasALightPlan_AsAZoneOnlyDevice_OrWithAnEffectFeatureAndAZone()
        {
            var bare = new HidppUnit();
            Assert.Equal(0, bare.EffectFeatureId + bare.EffectIndex + bare.PerKeyFeatureId + bare.PerKeyIndex
                            + bare.PerKeyMin + bare.PerKeyMax + bare.ModeFeatureId + bare.ModeIndex + bare.MaxClaimLevel);
            Assert.False(bare.HasLightPlan);

            var zoneOnly = new HidppUnit { RgbFeatureId = HidppUnitProtocol.ColorLedEffects, Zones = new[] { new HidppZone(0, 1) } };
            Assert.True(zoneOnly.DirectRgb && zoneOnly.HasLightPlan);

            var effects = new HidppUnit { RgbFeatureId = HidppUnitProtocol.RgbEffects, EffectFeatureId = HidppUnitProtocol.RgbEffects };
            Assert.False(effects.HasLightPlan);
            Assert.True((effects with { Zones = new[] { new HidppZone(0, 1) } }).HasLightPlan);
            Assert.False((effects with { Zones = new[] { new HidppZone(0, 1) } }).DirectRgb);
            // Zones with no effect feature paint nothing.
            Assert.False(new HidppUnit { RgbFeatureId = HidppUnitProtocol.PerKeyLighting, Zones = new[] { new HidppZone(0, 1) } }.HasLightPlan);
        }
    }
}
