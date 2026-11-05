using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    // ─────────────────────────────────────────────
    //  A scripted LampArray device
    // ─────────────────────────────────────────────

    /// <summary>One report call a board saw: what it was, the tick it came
    /// at, and whether it came on a handle opened with no access right.</summary>
    internal readonly record struct LampCall(string Text, long Tick, bool QueryOnly);

    /// <summary>
    /// A scripted HID LampArray device behind <see cref="FakeReportChannel"/>
    /// (#494): the firmware side of the reports PadForge sends and reads,
    /// as the HID Usage Tables give it. It answers its attributes report,
    /// takes a lamp request, answers one lamp per response read and counts
    /// on by itself after each, and records every call on every handle a
    /// test opened on it. A test tells it to fail reads, to answer for the
    /// wrong lamp, or to fail every write.
    /// </summary>
    internal sealed class LampBoard
    {
        internal static readonly LampLevels OneLevel = new(true, 255, 255, 255, 1);

        private readonly object _sync = new();
        private readonly List<FakeReportChannel> _opened = new();
        private readonly Dictionary<FakeReportChannel, int> _read = new();
        private readonly Func<long> _ticks;
        private volatile bool _failWrites;
        private int _agree = 1;
        private int _current;
        private int _taken;

        public LampBoard(string path, Func<long> ticks, int lamps = 4)
        {
            Path = path;
            _ticks = ticks;
            Lamps = Enumerable.Repeat(OneLevel, lamps).ToArray();
            LampCount = lamps;
        }

        public string Path { get; }

        /// <summary>The row a test lights this board through.</summary>
        public Guid Row { get; } = Guid.NewGuid();
        public ushort VendorId { get; init; } = 0x1234;
        public ushort ProductId { get; init; } = 0x0001;

        // What the parser says of its descriptor.
        public volatile string Name = "Test Lamps";
        public volatile LampArrayLayout Layout = new(1, 2, 3, 5, 6, true, 51)
        {
            AttributesLink = 1, RequestLink = 2, ResponseLink = 3, RangeLink = 5, ControlLink = 6,
            MultiUpdateSlots = 8,
        };
        public volatile string Rejection;
        public volatile bool Unreadable;
        public int DescriptorReads;
        public int NameReads;
        public int AgreeChecks;

        /// <summary>Whether the parser reads the attributes report as the
        /// layout does. Null for "cannot tell now".</summary>
        public bool? Agree
        {
            get => Volatile.Read(ref _agree) switch { 1 => true, 0 => false, _ => null };
            set => Volatile.Write(ref _agree, value == null ? 2 : value.Value ? 1 : 0);
        }

        // What its attributes report says, and its lamps.
        public volatile int LampCount;

        /// <summary>Each lamp's purposes and InputBinding (#557). Null
        /// answers every lamp as a control lamp bound to A.</summary>
        public volatile uint[] Purposes;
        public volatile byte[] Bindings;
        public volatile uint Kind = 1;
        public volatile uint IntervalUs = 20000;
        public volatile LampLevels[] Lamps;

        // What a test makes it do wrong.
        /// <summary>False for firmware that answers for the same lamp until
        /// it is asked for another.</summary>
        public volatile bool CountsOn = true;
        public volatile bool Unopenable;
        public int FailAttributeReads;
        public int FailLampReads;
        public volatile int FailLampIndex = -1;

        /// <summary>How many more reads of a lamp fail, by lamp.</summary>
        public readonly ConcurrentDictionary<int, int> FailLampTimes = new();
        public volatile int MisreportLamp = -1;
        public volatile int MisreportAs;
        public int Misreports;

        /// <summary>Runs inside every call a handle records, on the
        /// worker's thread, so a test can look at the backend at that
        /// moment or hold the worker there.</summary>
        public volatile Action OnCall;

        public bool FailWrites
        {
            get => _failWrites;
            set
            {
                _failWrites = value;
                lock (_sync)
                    foreach (var channel in _opened) channel.FailWrites = value;
            }
        }

        public HidCollectionInfo Info => new()
        {
            Path = Path, VendorId = VendorId, ProductId = ProductId,
            UsagePage = LampArrayProtocol.UsagePage, Usage = LampArrayProtocol.LampArrayUsage,
            InterfaceNumber = 1, FeatureReportLength = (ushort)Layout.FeatureLength,
        };

        public OutputPath OutputPath => new(OutputFamily.LampArrayUnit, Path);

        /// <summary>Every handle opened on the board, in the order opened.</summary>
        public FakeReportChannel[] Opened
        {
            get
            {
                lock (_sync) return _opened.ToArray();
            }
        }

        public FakeReportChannel Open(bool queryOnly)
        {
            if (Unopenable) return null;
            var channel = new FakeReportChannel
            {
                Path = Path, FeatureOnly = queryOnly, FeatureReportLength = Layout.FeatureLength,
            };
            channel.Ticks = () =>
            {
                OnCall?.Invoke();
                return _ticks();
            };
            channel.Feature = id => Answer(channel, id);
            channel.FailWrites = _failWrites;
            lock (_sync)
            {
                _opened.Add(channel);
                _read[channel] = 0;
            }
            return channel;
        }

        /// <summary>Every call on every handle so far. The handles are used
        /// one after the other, so their calls in the order the handles
        /// were opened are the calls in the order they were made.</summary>
        public LampCall[] Traffic()
        {
            lock (_sync)
            {
                var all = new List<LampCall>();
                foreach (var channel in _opened)
                    foreach (var call in channel.Calls)
                        all.Add(new LampCall(call.Kind + " " + string.Join(" ", call.Bytes.Select(b => b.ToString("X2"))),
                            call.Tick, channel.FeatureOnly));
                return all.ToArray();
            }
        }

        /// <summary>The calls made since the last take.</summary>
        public LampCall[] TakeCalls()
        {
            var all = Traffic();
            var fresh = all.Skip(_taken).ToArray();
            _taken = all.Length;
            return fresh;
        }

        public string[] Take() => TakeCalls().Select(call => call.Text).ToArray();

        private static bool Spend(ref int counter)
        {
            if (Volatile.Read(ref counter) <= 0) return false;
            Interlocked.Decrement(ref counter);
            return true;
        }

        private byte[] Answer(FakeReportChannel channel, byte id)
        {
            var layout = Layout;
            var lamps = Lamps;
            // A request written since the last read moves the lamp the next
            // response names. An ID past the last lamp is lamp 0 (HID Usage
            // Tables 1.6, 26.8.1).
            var calls = channel.Calls.ToArray();
            int from;
            lock (_sync) from = _read[channel];
            for (int i = from; i < calls.Length; i++)
            {
                var call = calls[i];
                if (call.Kind != "set" || call.Bytes.Length < 3 || call.Bytes[0] != layout.RequestId) continue;
                int asked = call.Bytes[1] | call.Bytes[2] << 8;
                _current = asked < lamps.Length ? asked : 0;
            }
            lock (_sync) _read[channel] = calls.Length;

            if (id == layout.AttributesId)
            {
                if (Spend(ref FailAttributeReads)) return null;
                var report = new byte[LampArrayProtocol.AttributesLength];
                report[0] = id;
                BitConverter.TryWriteBytes(report.AsSpan(1), (ushort)LampCount);
                BitConverter.TryWriteBytes(report.AsSpan(3), 320000u);
                BitConverter.TryWriteBytes(report.AsSpan(7), 116000u);
                BitConverter.TryWriteBytes(report.AsSpan(11), 10000u);
                BitConverter.TryWriteBytes(report.AsSpan(15), Kind);
                BitConverter.TryWriteBytes(report.AsSpan(19), IntervalUs);
                return report;
            }
            if (id == layout.ResponseId && lamps.Length > 0)
            {
                int lamp = _current;
                if ((FailLampIndex < 0 || FailLampIndex == lamp) && Spend(ref FailLampReads)) return null;
                if (FailLampTimes.TryGetValue(lamp, out int left) && left > 0)
                {
                    FailLampTimes[lamp] = left - 1;
                    return null;
                }
                if (MisreportLamp == lamp && Spend(ref Misreports)) lamp = MisreportAs;
                // The device counts on from the lamp it answered for.
                if (CountsOn) _current = (lamp + 1) % lamps.Length;
                var report = new byte[LampArrayProtocol.ResponseLength];
                report[0] = id;
                BitConverter.TryWriteBytes(report.AsSpan(1), (ushort)lamp);
                BitConverter.TryWriteBytes(report.AsSpan(3), (uint)(lamp * 19000));
                BitConverter.TryWriteBytes(report.AsSpan(15), 4000u);
                BitConverter.TryWriteBytes(report.AsSpan(19), Purposes?[lamp] ?? 1u);
                report[23] = lamps[lamp].Red;
                report[24] = lamps[lamp].Green;
                report[25] = lamps[lamp].Blue;
                report[26] = lamps[lamp].Intensity;
                report[27] = lamps[lamp].Programmable ? (byte)1 : (byte)0;
                report[28] = Bindings?[lamp] ?? 0x04;
                return report;
            }
            // Nothing else is readable. Firmware stalls a read of its
            // control report, so no test may see one asked for.
            return null;
        }
    }

    // ─────────────────────────────────────────────
    //  Pure rules: no shared state
    // ─────────────────────────────────────────────

    /// <summary>The rules around the LampArray worker that need no device
    /// (#494): where a unit's container comes from, when Windows lights a
    /// unit, and which units get no row of their own.</summary>
    public class LampArrayRulesTests
    {
        private static readonly Guid Receiver = new("6e79ed28-0000-0000-0000-000000000001");
        private static readonly Guid Hub = new("6e79ed28-0000-0000-0000-000000000002");
        private static readonly Guid Pc = PeripheralLinker.LocalMachineContainer;

        private const string SlotNode = @"USB\VID_046D&PID_C54D&LAMPARRAY\8&12345678&0&000000000000_SLOT00";
        private const string ReceiverNode = @"USB\VID_046D&PID_C54D\000000000000";
        private const string HubNode = @"USB\VID_05E3&PID_0610\6&1234567&0&1";
        private const string RootHub = @"USB\ROOT_HUB30\5&1234567&0&0";

        private static LampArrayContainers.DeviceNode Node(string id, Guid container = default) => new(id, container);

        // ── The container ──

        /// <summary>The collection's own container is the answer wherever
        /// it has one that is not the PC's own, and nothing above it is
        /// looked at.</summary>
        [Fact]
        public void AUnitsContainer_IsItsCollectionsOwn_WhereItHasOne()
        {
            int asked = 0;
            Func<IReadOnlyList<LampArrayContainers.DeviceNode>> chain = () =>
            {
                asked++;
                return new[] { Node(SlotNode, Receiver), Node(ReceiverNode, Hub) };
            };
            Assert.Equal(Receiver, LampArrayContainers.Decide(Receiver, chain));
            Assert.Equal(0, asked);
            Assert.Equal(Receiver, LampArrayContainers.Decide(Receiver, null));
        }

        /// <summary>Logitech's LampArray driver puts a slot node under a
        /// LIGHTSPEED receiver for each paired device, and the collection
        /// Windows lights is a virtual device under that slot. One that
        /// reports no container, or the PC's own, takes the container of
        /// the first USB device above the slot, the receiver, so the unit
        /// is matched to the receiver's rows.</summary>
        [Fact]
        public void ALogitechVirtualUnit_WithNoContainerOfItsOwn_TakesItsReceivers()
        {
            var chain = new[] { Node(SlotNode, Pc), Node(ReceiverNode, Receiver), Node(HubNode, Hub), Node(RootHub) };
            Assert.Equal(Receiver, LampArrayContainers.Decide(Guid.Empty, () => chain));
            Assert.Equal(Receiver, LampArrayContainers.Decide(Pc, () => chain));

            // The slot need not be the node right above the collection, and
            // only a USB device counts above it: an interface of the
            // receiver is passed over.
            var deeper = new[]
            {
                Node(@"ROOT\VHF\0000", Pc), Node(SlotNode), Node(@"USB\VID_046D&PID_C54D&MI_02\8&12345678&0&0002", Hub),
                Node(ReceiverNode, Receiver), Node(HubNode, Hub),
            };
            Assert.Equal(Receiver, LampArrayContainers.Decide(Pc, () => deeper));

            // Only a USB device above the slot counts: one below it, nearer
            // the collection, is not the receiver.
            var below = new[] { Node(@"USB\VID_1234&PID_5678\1", Hub), Node(SlotNode), Node(ReceiverNode, Receiver) };
            Assert.Equal(Receiver, LampArrayContainers.Decide(Pc, () => below));

            // Nothing usable above the slot: the plain answer stands.
            Assert.Equal(Pc, LampArrayContainers.Decide(Pc, () => new[] { Node(SlotNode, Receiver) }));
            Assert.Equal(Guid.Empty, LampArrayContainers.Decide(Guid.Empty, () => new[] { Node(SlotNode), Node(RootHub, Hub) }));
            Assert.Equal(Pc, LampArrayContainers.Decide(Pc, () => new[] { Node(SlotNode), Node(ReceiverNode) }));
        }

        /// <summary>The rule is for that one virtual device. A laptop's own
        /// LampArray keeps the PC's container, which is what the linker
        /// matches it to the internal keyboard by, and a walk that fails
        /// changes nothing.</summary>
        [Fact]
        public void AnyOtherUnit_KeepsWhatItsCollectionReports()
        {
            var builtIn = new[]
            {
                Node(@"USB\VID_048D&PID_C193&MI_01\8&1234567&0&0001", Pc), Node(@"USB\VID_048D&PID_C193\7&1234567&0&2", Pc),
                Node(HubNode, Hub), Node(RootHub),
            };
            Assert.Equal(Pc, LampArrayContainers.Decide(Pc, () => builtIn));
            Assert.Equal(Guid.Empty, LampArrayContainers.Decide(Guid.Empty, () => builtIn));

            // Another vendor's node of the same shape is not Logitech's.
            var razer = new[] { Node(@"USB\VID_1532&PID_0084&LAMPARRAY\8&1&0&SLOT00"), Node(@"USB\VID_1532&PID_0084\1", Receiver) };
            Assert.Equal(Pc, LampArrayContainers.Decide(Pc, () => razer));

            Assert.Equal(Pc, LampArrayContainers.Decide(Pc, () => null));
            Assert.Equal(Pc, LampArrayContainers.Decide(Pc, () => throw new InvalidOperationException("scripted")));
            Assert.Equal(Pc, LampArrayContainers.Decide(Pc, () => Array.Empty<LampArrayContainers.DeviceNode>()));
            Assert.Equal(Guid.Empty, LampArrayContainers.Decide(Guid.Empty, null));
        }

        [Theory]
        [InlineData(SlotNode, true)]
        [InlineData(@"usb\vid_046d&pid_c547&lamparray\8&1&0&x_slot03", true)]
        [InlineData(ReceiverNode, false)]
        [InlineData(@"USB\VID_046D&PID_C54D&MI_02\8&12345678&0&0002", false)]
        [InlineData(@"USB\VID_1532&PID_0084&LAMPARRAY\8&1&0&SLOT00", false)]
        [InlineData(@"HID\VID_046D&PID_C54D&LAMPARRAY\8&1&0&0000", false)]
        [InlineData(@"USB\VID_046D&PID_C54D&LAMPARRAY", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ASlotNodeOfLogitechsLampArrayDriver_IsToldByItsId(string instanceId, bool expected)
            => Assert.Equal(expected, LampArrayContainers.IsLogitechLampArraySlot(instanceId));

        [Theory]
        [InlineData(ReceiverNode, true)]
        [InlineData(@"usb\vid_05e3&pid_0610\6&1234567&0&1", true)]
        [InlineData(SlotNode, false)]
        [InlineData(@"USB\VID_046D&PID_C54D&MI_02\8&12345678&0&0002", false)]
        [InlineData(RootHub, false)]
        [InlineData(@"HID\VID_046D&PID_C54D\8&1&0&0000", false)]
        [InlineData(@"USB\VID_046G&PID_C54D\1", false)]
        [InlineData(@"USB\VID_046D&PID_C54\12", false)]
        [InlineData(@"USB\VID_046D&PID_C54D", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void AUsbDeviceNode_IsOneWithAVendorAndAProductIdAndNothingMore(string instanceId, bool expected)
            => Assert.Equal(expected, LampArrayContainers.IsUsbDevice(instanceId));

        // ── Windows Dynamic Lighting ──

        /// <summary>The device's own switch decides where it has one, else
        /// the global one, else on, and nothing is lit on a Windows with
        /// no controller that lights devices in the background.</summary>
        [Theory]
        [InlineData(1, 1, true)]
        [InlineData(1, null, true)]
        [InlineData(0, 1, true)]
        [InlineData(null, null, true)]
        [InlineData(null, 1, true)]
        [InlineData(1, 0, false)]
        [InlineData(0, 0, false)]
        [InlineData(0, null, false)]
        [InlineData(null, 0, false)]
        [InlineData(5, null, true)]
        public void WindowsLightsAUnit_ByItsOwnSwitch_ElseTheGlobalOne_ElseItDoes(int? global, int? device, bool expected)
        {
            Assert.Equal(expected, WindowsLightingState.Lit(true, global, device));
            Assert.False(WindowsLightingState.Lit(false, global, device));
        }

        /// <summary>A device's key is named by its interface path without
        /// the prefix, the path the bench's own key has under Devices.</summary>
        [Fact]
        public void ADevicesKey_IsItsInterfacePathWithoutThePrefix()
        {
            const string id = "hid#vid_048d&pid_c193&mi_01#9&1234567&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            Assert.Equal(id, WindowsLightingState.DeviceKey(@"\\?\" + id));
            Assert.Equal(id, WindowsLightingState.DeviceKey(@"\\.\" + id));
            Assert.Equal(id, WindowsLightingState.DeviceKey(id));
            Assert.Null(WindowsLightingState.DeviceKey(null));
            Assert.Null(WindowsLightingState.DeviceKey(""));
            Assert.Null(WindowsLightingState.DeviceKey(@"\\?\"));
            // Never a path that would name a key further down.
            Assert.Null(WindowsLightingState.DeviceKey(@"\\?\hid#vid_048d\..\..\Other"));
            Assert.Equal(@"Software\Microsoft\Lighting", WindowsLightingState.LightingKey);
            Assert.Equal("Devices", WindowsLightingState.DevicesKey);
            Assert.Equal("AmbientLightingEnabled", WindowsLightingState.EnabledValue);
        }

        /// <summary>The device's value is read first and the global one
        /// only for a device with none of its own. Nothing is read on a
        /// Windows without the controller, and a read that cannot be made
        /// counts as lit.</summary>
        [Fact]
        public void TheSwitchesAreRead_ForTheDeviceFirst_AndTheGlobalOneOnlyWhenItHasNone()
        {
            const string path = @"\\?\hid#vid_048d&pid_c193&mi_01#bench#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            string key = WindowsLightingState.DeviceKey(path);
            var asked = new List<string>();
            int? device = 1, global = 0;
            var state = new WindowsLightingState(() => true, name =>
            {
                asked.Add(name ?? "(global)");
                return name == null ? global : device;
            });

            Assert.True(state.Lights(path));
            Assert.Equal(new[] { key }, asked);
            asked.Clear();
            device = 0;
            global = 1;
            Assert.False(state.Lights(path));
            Assert.Equal(new[] { key }, asked);

            asked.Clear();
            device = null;
            Assert.True(state.Lights(path));
            Assert.Equal(new[] { key, "(global)" }, asked);
            global = 0;
            Assert.False(state.Lights(path));
            global = null;
            Assert.True(state.Lights(path));

            // A path that names no key: the global switch alone.
            asked.Clear();
            global = 0;
            Assert.False(state.Lights(null));
            Assert.Equal(new[] { "(global)" }, asked);

            asked.Clear();
            var without = new WindowsLightingState(() => false, name =>
            {
                asked.Add(name ?? "(global)");
                return 1;
            });
            Assert.False(without.Lights(path));
            Assert.Empty(asked);

            var broken = new WindowsLightingState(() => true, _ => throw new InvalidOperationException("scripted"));
            Assert.True(broken.Lights(path));
            var unknown = new WindowsLightingState(() => throw new InvalidOperationException("scripted"), _ => 0);
            Assert.True(unknown.Lights(path));
        }

        /// <summary>The switches are the ones of the user whose desktop
        /// this is. Where a shell runs, its user's SID comes from the
        /// shell's own token, and that user's hive is loaded. Where none
        /// runs there is no SID, and the process's own hive is read.
        /// Either way the read answers without a fault. Read-only.</summary>
        [Fact]
        public void TheShellsUser_IsReadFromTheShellsToken_AndItsHiveIsThere()
        {
            string sid = WindowsLightingState.ShellUserSid();
            if (sid != null)
            {
                Assert.StartsWith("S-1-", sid);
                using var hive = Registry.Users.OpenSubKey(sid);
                Assert.NotNull(hive);
            }
            int? global = WindowsLightingState.ReadShellUser(null);
            Assert.True(global == null || global >= 0);
            Assert.Null(WindowsLightingState.ReadShellUser("hid#vid_0000&pid_0000#no-such-device#{00000000-0000-0000-0000-000000000000}"));
            // The default state reads them without a fault, whatever they say.
            _ = new WindowsLightingState().Lights(@"\\?\hid#vid_0000&pid_0000#no-such-device#{00000000-0000-0000-0000-000000000000}");
        }

        /// <summary>Whose hive answers: the shell user's where there is a
        /// shell and that user's hive is loaded, and then what it holds
        /// stands, a value that is not there included. With no shell, or
        /// no hive loaded for its user, the process's own hive is read.
        /// The key is Software\Microsoft\Lighting for every device and
        /// Devices\name under it for one.</summary>
        [Fact]
        public void TheSwitchesAreTheShellUsers_AndTheProcessesOwnOnlyWithNoShellOrNoHive()
        {
            const string shell = "S-1-5-21-1111111111-2222222222-3333333333-1001";
            const string device = "hid#vid_048d&pid_c193&mi_01#bench#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string global = @"Software\Microsoft\Lighting";
            const string own = null;
            var asked = new List<(string Sid, string Key)>();
            bool hiveThere = true;
            int? shellValue = 0, ownValue = 1;
            WindowsLightingState.HiveRead read = (sid, key) =>
            {
                asked.Add((sid, key));
                return sid == null ? (true, ownValue) : (hiveThere, hiveThere ? shellValue : null);
            };

            // The shell user's switch, whatever the account PadForge was
            // elevated with has in its own hive.
            Assert.Equal(0, WindowsLightingState.ReadFor(shell, null, read));
            Assert.Equal(new[] { (shell, global) }, asked);
            asked.Clear();
            Assert.Equal(0, WindowsLightingState.ReadFor(shell, device, read));
            Assert.Equal(new[] { (shell, global + @"\Devices\" + device) }, asked);

            // A switch the shell's user never touched is not there. The
            // other account's is not read in its place.
            asked.Clear();
            shellValue = null;
            Assert.Null(WindowsLightingState.ReadFor(shell, device, read));
            Assert.Single(asked);

            // No hive loaded for the shell's user, and no shell at all.
            asked.Clear();
            hiveThere = false;
            Assert.Equal(1, WindowsLightingState.ReadFor(shell, null, read));
            Assert.Equal(new[] { (shell, global), (own, global) }, asked);
            asked.Clear();
            Assert.Equal(1, WindowsLightingState.ReadFor(null, device, read));
            Assert.Equal(new[] { (own, global + @"\Devices\" + device) }, asked);

            // A read that cannot be made gives no value.
            Assert.Null(WindowsLightingState.ReadFor(shell, null, (_, _) => throw new InvalidOperationException("scripted")));

            // The first build with Dynamic Lighting, and the file that
            // comes with it.
            Assert.Equal(22621, WindowsLightingState.FirstBuild);
            Assert.Equal("SettingsHandlers_Lighting.dll", WindowsLightingState.SettingsHandler);
        }

        // ── Units that get no row of their own ──

        private static DirectUnit Lamp(string key, Guid container, ushort vid, int kind = 4)
            => new(OutputFamily.LampArrayUnit, key, container, vid, 0x0527) { Kind = kind };

        private static LinkInputs Inputs(PeripheralPresence presence, params DirectUnit[] units)
            => new(HidppSnapshot.Empty, presence) { Units = units };

        /// <summary>A Logitech, Razer or SteelSeries unit no row reaches
        /// gets no row of its own while that vendor's program runs: the
        /// program drives every device of its vendor then, and the vendor's
        /// row is how such a device is lit.</summary>
        [Fact]
        public void AVendorsUnit_IsNoOrphan_WhileThatVendorsProgramRuns()
        {
            var none = Array.Empty<LinkRow>();
            var logitech = Lamp("logitech", Receiver, PeripheralLinker.LogitechVid);
            var razer = Lamp("razer", Receiver, PeripheralLinker.RazerVid);
            var steelSeries = Lamp("steelseries", Hub, PeripheralLinker.SteelSeriesVid);
            var asus = Lamp("asus", Hub, 0x0B05);
            var all = new[] { logitech, razer, steelSeries, asus };
            var nothing = new PeripheralPresence(true, false, true, true, true);

            // Installed is not running: every unit is one.
            Assert.Equal(all, PeripheralLinker.Orphans(none, Inputs(nothing, all)));
            Assert.Equal(all, PeripheralLinker.Orphans(none, Inputs(PeripheralPresence.None, all)));
            Assert.Equal(all, PeripheralLinker.Orphans(none, new LinkInputs(HidppSnapshot.Empty, null) { Units = all }));

            // Each program takes its own vendor's units and no other's.
            Assert.Equal(new[] { razer, steelSeries, asus },
                PeripheralLinker.Orphans(none, Inputs(nothing with { LogitechGHubRunning = true }, all)));
            Assert.Equal(new[] { razer, steelSeries, asus },
                PeripheralLinker.Orphans(none, Inputs(nothing with { LogitechGHubRunning = true, LogitechGamingSoftware = true }, all)));
            Assert.Equal(new[] { logitech, steelSeries, asus },
                PeripheralLinker.Orphans(none, Inputs(nothing with { RazerSoftwareRunning = true }, all)));
            Assert.Equal(new[] { logitech, razer, asus },
                PeripheralLinker.Orphans(none, Inputs(nothing with { SteelSeriesGGRunning = true }, all)));
            Assert.Equal(new[] { asus }, PeripheralLinker.Orphans(none, Inputs(nothing with
            {
                LogitechGHubRunning = true, RazerSoftwareRunning = true, SteelSeriesGGRunning = true,
            }, all)));

            // Asleep or awake, lit by Windows or not.
            var running = nothing with { RazerSoftwareRunning = true };
            Assert.Empty(PeripheralLinker.Orphans(none, Inputs(running, razer with { Asleep = true })));
            Assert.Empty(PeripheralLinker.Orphans(none, Inputs(running, razer with { WindowsLit = true })));
        }

        /// <summary>The rule takes nothing from a row that reaches the
        /// unit: it was no orphan before, and the row's own family is
        /// decided by the same running flag in the linker.</summary>
        [Fact]
        public void AUnitARowReaches_IsNoOrphanEitherWay_AndARowOfItsOwnLetsGoWhenTheProgramStarts()
        {
            var keyboard = new LinkRow(Guid.NewGuid(), InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x024E, Receiver, null);
            var unit = Lamp("razer", Receiver, PeripheralLinker.RazerVid, kind: 1);
            var stopped = new PeripheralPresence(false, false, true, false, false);
            var running = stopped with { RazerSoftwareRunning = true };
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(stopped, unit)));
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(running, unit)));

            // The clock of a unit that stopped being an orphan starts
            // over, so its row comes back only after the whole hold.
            var headset = Lamp("headset", Hub, PeripheralLinker.RazerVid);
            var since = new Dictionary<OutputPath, long>();
            var none = Array.Empty<LinkRow>();
            DirectUnit[] Held(PeripheralPresence presence, long now)
                => PeripheralLinker.HeldOrphans(since, PeripheralLinker.Orphans(none, Inputs(presence, headset)), now);

            Assert.Empty(Held(stopped, 1000));
            Assert.Equal(new[] { headset }, Held(stopped, 1000 + PeripheralLinker.UnitRowHoldMs));
            Assert.Empty(Held(running, 7000));
            Assert.Empty(since);
            Assert.Empty(Held(running, 60000));
            Assert.Empty(Held(stopped, 60001));
            Assert.Empty(Held(stopped, 60001 + PeripheralLinker.UnitRowHoldMs - 1));
            Assert.Equal(new[] { headset }, Held(stopped, 60001 + PeripheralLinker.UnitRowHoldMs));
        }

        private static LinkRow UnitRow(DirectUnit unit)
            => new(Guid.NewGuid(), InputDeviceType.PeripheralLighting, 0x5046, PeripheralUnitRow.UnitProductId, Guid.Empty, null)
            {
                Unit = unit.Path,
            };

        /// <summary>A unit row holds no path while its unit's vendor's
        /// program runs, from the pass that sees the program. The row
        /// itself retires only at the input manager's next device scan,
        /// and its worker would paint the device until then. Such a row is
        /// one a program takes, so a claim on it is watched for like a
        /// claim on that vendor's mouse. The vendor is the unit's: the
        /// row's own record carries PadForge's.</summary>
        [Fact]
        public void AUnitRow_HoldsNoPath_WhileItsUnitsVendorsProgramRuns()
        {
            var stopped = new PeripheralPresence(true, false, true, true, true);
            var cases = new[]
            {
                (Vid: PeripheralLinker.LogitechVid, Running: stopped with { LogitechGHubRunning = true }),
                (Vid: PeripheralLinker.RazerVid, Running: stopped with { RazerSoftwareRunning = true }),
                (Vid: PeripheralLinker.SteelSeriesVid, Running: stopped with { SteelSeriesGGRunning = true }),
            };
            foreach (var (vid, running) in cases)
            {
                var unit = Lamp("unit", Hub, vid);
                var row = UnitRow(unit);
                var links = PeripheralLinker.Build(new[] { row }, Inputs(stopped, unit)).For(row.Device);
                Assert.Equal(new[] { unit.Path }, links.Lighting);
                Assert.True(links.UnitRow);
                Assert.True(links.HasVendorProgram, vid.ToString("X4"));
                Assert.True(PeripheralLinker.VendorProgramRuns(vid, running));
                Assert.Null(PeripheralLinker.Build(new[] { row }, Inputs(running, unit)).For(row.Device));

                // Another vendor's program takes nothing from it.
                foreach (var other in cases.Where(c => c.Vid != vid))
                {
                    Assert.False(PeripheralLinker.VendorProgramRuns(vid, other.Running));
                    Assert.Equal(new[] { unit.Path }, PeripheralLinker.Build(new[] { row }, Inputs(other.Running, unit)).For(row.Device).Lighting);
                }
                // Installed is not running.
                Assert.False(PeripheralLinker.VendorProgramRuns(vid, stopped));
            }

            // A unit of a vendor with no such program is lit whatever runs,
            // and no claim on its row is watched for.
            var asus = Lamp("asus", Hub, 0x0B05);
            var asusRow = UnitRow(asus);
            var everything = stopped with { LogitechGHubRunning = true, RazerSoftwareRunning = true, SteelSeriesGGRunning = true };
            var asusLinks = PeripheralLinker.Build(new[] { asusRow }, Inputs(everything, asus)).For(asusRow.Device);
            Assert.Equal(new[] { asus.Path }, asusLinks.Lighting);
            Assert.False(asusLinks.HasVendorProgram);
            Assert.False(PeripheralLinker.VendorProgramRuns(0x0B05, everything));

            // Asleep, the row holds nothing either way, as before.
            var razer = Lamp("razer", Hub, PeripheralLinker.RazerVid) with { Asleep = true };
            var asleepRow = UnitRow(razer);
            Assert.Null(PeripheralLinker.Build(new[] { asleepRow }, Inputs(stopped, razer)).For(asleepRow.Device));
        }
    }

    // ─────────────────────────────────────────────
    //  The worker: shared state
    // ─────────────────────────────────────────────

    /// <summary>
    /// The LampArray worker against scripted devices and a clock the test
    /// sets (#494). No LampArray device was written to. Every interval is
    /// measured on the test's clock, and a test waits for the worker by
    /// counting its passes, never for a length of time.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class LampArrayBackendTests : IDisposable
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private const string BoardPath = @"\\?\hid#vid_1234&pid_0001&mi_01#bench-a#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string OtherPath = @"\\?\hid#vid_1234&pid_0002&mi_01#bench-b#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const long Start = 1_000_000;

        private readonly List<IDisposable> _owned = new();
        private long _tick = Start;

        // What the seams saw.
        private readonly ConcurrentDictionary<string, LampBoard> _boards = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _windowsLit = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Guid> _containers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<(string Path, string Frame, int TimeoutMs)> _oneShots = new();
        private readonly ConcurrentQueue<int> _pauses = new();
        private readonly ConcurrentQueue<int> _joins = new();
        private readonly ConcurrentQueue<string> _descriptorAsks = new();
        private volatile bool _litFault;
        private volatile bool _claimUnopenable;
        private int _queryOpens;
        private int _claimOpens;
        private int _litReads;
        private int _containerLookups;
        private int _notices;
        private int _walkAsks;

        public LampArrayBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            LearnedKeyMaps.ResetForTest();
        }

        public void Dispose()
        {
            foreach (var owned in _owned)
            {
                try { owned.Dispose(); } catch { }
            }
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            LearnedKeyMaps.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        /// <summary>A name no other test's boards have. The diagnostics
        /// log keeps its newest lines only, so a count of lines that other
        /// tests write too would move as theirs fall out of it. A line
        /// that names this test's board is this test's own.</summary>
        private readonly string _name = "Lamps " + Guid.NewGuid().ToString("N").Substring(0, 12);

        private LampBoard Board(string path = BoardPath, int lamps = 4, ushort pid = 0x0001)
        {
            var board = new LampBoard(path, Tick, lamps) { ProductId = pid };
            board.Name = _name;
            _boards[path] = board;
            _containers[path] = Box;
            return board;
        }

        /// <summary>A worker on scripted seams and this test's clock.</summary>
        private LampArrayBackend Backend(int predecessorJoinMs = LampArrayBackend.PredecessorJoinMs,
            int stopJoinMs = LampArrayBackend.StopJoinMs)
        {
            LampBoard Of(string path) => _boards.TryGetValue(path, out var board) ? board : null;
            var backend = new LampArrayBackend(new LampArrayBackend.Seams
            {
                Descriptors = new ScriptedDescriptors(Of, _descriptorAsks.Enqueue),
                OpenQuery = path =>
                {
                    Interlocked.Increment(ref _queryOpens);
                    return Of(path)?.Open(queryOnly: true);
                },
                OpenClaim = path =>
                {
                    Interlocked.Increment(ref _claimOpens);
                    return _claimUnopenable ? null : Of(path)?.Open(queryOnly: false);
                },
                ContainerOf = path =>
                {
                    Interlocked.Increment(ref _containerLookups);
                    return _containers.TryGetValue(path, out var container) ? container : Guid.Empty;
                },
                WindowsLit = path =>
                {
                    Interlocked.Increment(ref _litReads);
                    if (_litFault) throw new InvalidOperationException("scripted");
                    return _windowsLit.TryGetValue(path, out bool lit) && lit;
                },
                Ticks = Tick,
                SetFeatureOnce = (path, frame, timeoutMs) =>
                {
                    _oneShots.Enqueue((path, string.Join(" ", frame.Select(b => b.ToString("X2"))), timeoutMs));
                    return true;
                },
                Pause = ms => _pauses.Enqueue(ms),
                Join = (thread, ms) =>
                {
                    _joins.Enqueue(ms);
                    return thread.Join(ms);
                },
                IdleTickMs = 1,
                PredecessorJoinMs = predecessorJoinMs,
                StopJoinMs = stopJoinMs,
            });
            backend.SnapshotChanged += () => Interlocked.Increment(ref _notices);
            backend.RescanRequested += () => Interlocked.Increment(ref _walkAsks);
            _owned.Add(backend);
            return backend;
        }

        private sealed class ScriptedDescriptors : ILampArrayDescriptors
        {
            private readonly Func<string, LampBoard> _of;
            private readonly Action<string> _asked;

            public ScriptedDescriptors(Func<string, LampBoard> of, Action<string> asked)
            {
                _of = of;
                _asked = asked;
            }

            public LampArrayLayout Read(string path, out string rejection)
            {
                rejection = null;
                // Every collection the worker asks about, a test's board
                // or not.
                _asked(path);
                var board = _of(path);
                if (board == null) return null;
                Interlocked.Increment(ref board.DescriptorReads);
                if (board.Unreadable) return null;
                rejection = board.Rejection;
                return rejection == null ? board.Layout : null;
            }

            public bool? AttributesAgree(string path, LampArrayLayout layout, byte[] report, LampArrayAttributes parsed)
            {
                var board = _of(path);
                if (board == null) return null;
                Interlocked.Increment(ref board.AgreeChecks);
                return board.Agree;
            }

            public string ProductName(string path)
            {
                var board = _of(path);
                if (board == null) return null;
                Interlocked.Increment(ref board.NameReads);
                return board.Name;
            }
        }

        /// <summary>Waits for worker passes to end. The first may have
        /// begun before the caller's last change, so two are asked for:
        /// the second began after the first ended, and so after the
        /// change.</summary>
        private static void Passes(LampArrayBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        private static void Walk(LampArrayBackend backend, params LampBoard[] present)
        {
            var list = new List<HidCollectionInfo>
            {
                // Collections that are no LampArray, which every walk holds:
                // other pages, the lighting page under another usage, and
                // the usage on another page. Then two entries no walk
                // should hold and the worker must pass over all the same,
                // one with no path and one that is not there.
                new() { Path = @"\\?\hid#vid_1234&pid_0001&mi_00#kbd", VendorId = 0x1234, ProductId = 0x0001, UsagePage = 0x01, Usage = 0x06 },
                new() { Path = @"\\?\hid#vid_1234&pid_0001&mi_02#vendor", VendorId = 0x1234, ProductId = 0x0001, UsagePage = 0xFF89, Usage = 0x08 },
                new() { Path = @"\\?\hid#vid_1234&pid_0001&mi_03#page59", VendorId = 0x1234, ProductId = 0x0001, UsagePage = 0x59, Usage = 0x02 },
                new() { Path = @"\\?\hid#vid_1234&pid_0001&mi_04#usage01", VendorId = 0x1234, ProductId = 0x0001, UsagePage = 0xFF00, Usage = 0x01 },
                new() { Path = "", VendorId = 0x1234, ProductId = 0x0001, UsagePage = 0x59, Usage = 0x01 },
                null,
            };
            list.AddRange(present.Select(board => board.Info));
            backend.Rescan(list);
        }

        private static void Link(params LampBoard[] boards)
            => PeripheralOutputs.PublishLinks(new LinkTable(boards.Select(board => new DeviceLinks
            {
                Device = board.Row, Lighting = new[] { board.OutputPath },
            })));

        private static void Color(LampBoard board, int rgb)
            => PeripheralOutputs.SetLighting(board.Row, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Dark(LampBoard board) => PeripheralOutputs.ReleaseLighting(board.Row, 0);

        private static DirectUnit Unit(LampArrayBackend backend, LampBoard board)
            => backend.Snapshot.Units.SingleOrDefault(unit => unit.Key == board.Path);

        private static string Paint(int rgb, byte intensity = 0x01, int lamps = 4, byte id = 0x05)
            => $"set {id:X2} 01 00 00 {(lamps - 1) & 0xFF:X2} {(lamps - 1) >> 8:X2} {(byte)(rgb >> 16):X2} {(byte)(rgb >> 8):X2} {(byte)rgb:X2} {intensity:X2}";

        private static string[] LampReads(int count) => Enumerable.Repeat("get 03", count).ToArray();

        private static int LogLines(string text)
            => SdlDiagLog.Snapshot().Split('\n').Count(line => line.Contains(text));

        /// <summary>A started worker that has described one board, with
        /// the board's row linked to it.</summary>
        private (LampArrayBackend Backend, LampBoard Board) Found(Action<LampBoard> setUp = null)
        {
            var board = Board();
            setUp?.Invoke(board);
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.NotNull(Unit(backend, board));
            Assert.Equal(new[] { "get 01" }, board.Take());
            Link(board);
            return (backend, board);
        }

        /// <summary>The same, claimed and painted one color, at
        /// <see cref="Start"/> plus the settle wait.</summary>
        private (LampArrayBackend Backend, LampBoard Board) Lit(int rgb = 0x112233, Action<LampBoard> setUp = null)
        {
            var (backend, board) = Found(setUp);
            Color(board, rgb);
            Passes(backend);
            Advance(20);
            Passes(backend);
            var calls = board.Take();
            Assert.Equal(Paint(rgb), calls.Last());
            return (backend, board);
        }

        // ── The scan (spec 11.2 step 1) ──

        /// <summary>The scan keeps the LampArray collections of a walk and
        /// describes each with one read of its attributes report, on a
        /// handle opened with no access right. Nothing is written, and
        /// nothing is opened for writing.</summary>
        [Fact]
        public void TheScan_ReadsEachUnitsAttributesOnce_AndListsIt()
        {
            var board = Board();
            board.Name = "Lenovo Lighting";
            board.LampCount = 318;
            board.IntervalUs = 32200;
            var backend = Backend();
            Assert.Equal(OutputFamily.LampArrayUnit, backend.Family);
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);

            backend.Start();
            Passes(backend);
            // No walk yet: nothing it has not looked for reads as missing.
            Assert.False(backend.Snapshot.Scanned);
            Assert.Equal(0, board.DescriptorReads);

            Walk(backend, board);
            Passes(backend);
            Assert.True(backend.Snapshot.Scanned);
            var unit = Assert.Single(backend.Snapshot.Units);
            Assert.Equal(new DirectUnit(OutputFamily.LampArrayUnit, BoardPath, Box, 0x1234, 0x0001)
            {
                Name = "Lenovo Lighting", Kind = 1,
            }, unit);
            Assert.Equal(0, unit.MaxClaimLevel);
            Assert.True(_notices >= 1);

            var call = Assert.Single(board.TakeCalls());
            Assert.Equal(new LampCall("get 01", Start, QueryOnly: true), call);
            var handle = Assert.Single(board.Opened);
            Assert.True(handle.FeatureOnly);
            Assert.True(handle.Disposed);
            Assert.Equal((1, 0), (_queryOpens, _claimOpens));
            Assert.Equal((1, 1, 1), (board.DescriptorReads, board.AgreeChecks, board.NameReads));
            // Of everything the walk held, the one LampArray collection
            // alone was asked about.
            Assert.Equal(new[] { BoardPath }, _descriptorAsks);

            // The same walk again, and time: nothing more is asked of it.
            Walk(backend, board);
            Advance(60000);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Equal((1, 0), (_queryOpens, _claimOpens));
            Assert.Equal(1, board.DescriptorReads);
            Assert.Equal(0, _walkAsks);

            // A walk that spells the path in other case names the same
            // collection, and the unit keeps the key it was listed under.
            backend.Rescan(new List<HidCollectionInfo> { board.Info with { Path = BoardPath.ToUpperInvariant() } });
            Passes(backend);
            Assert.Equal(BoardPath, Assert.Single(backend.Snapshot.Units).Key);
            Assert.Empty(board.Take());
            Assert.Equal(new[] { BoardPath }, _descriptorAsks);
        }

        /// <summary>Units are listed in the order of their paths, whatever
        /// order a walk gave them, so two snapshots of the same units read
        /// alike to the host.</summary>
        [Fact]
        public void TheUnitsAreListedInTheOrderOfTheirPaths_WhateverOrderTheWalkGaveThem()
        {
            var first = Board();
            var second = Board(OtherPath, pid: 0x0002);
            var backend = Backend();
            backend.Start();
            Walk(backend, second, first);
            Passes(backend);
            Assert.Equal(new[] { BoardPath, OtherPath }, backend.Snapshot.Units.Select(unit => unit.Key));
        }

        /// <summary>A second start while the worker runs starts nothing:
        /// one worker, and one read of each unit.</summary>
        [Fact]
        public void ASecondStartWhileItRuns_StartsNothing()
        {
            var board = Board();
            var backend = Backend(predecessorJoinMs: 60000);
            backend.Start();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.NotNull(Unit(backend, board));
            Assert.Equal(new[] { "get 01" }, board.Take());
            Assert.Equal(1, _queryOpens);
        }

        /// <summary>A walk with no LampArray collection is a scan all the
        /// same: the worker has looked, and found nothing.</summary>
        [Fact]
        public void AWalkWithNoLampArray_IsAScanThatFoundNothing()
        {
            var backend = Backend();
            backend.Start();
            Walk(backend);
            Passes(backend);
            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(1, _notices);
            Assert.Equal(0, _litReads);
            // A list never given changes nothing.
            backend.Rescan(null);
            Passes(backend);
            Assert.True(backend.Snapshot.Scanned);
        }

        /// <summary>The kind is the device's word, any value of 32 bits.
        /// The linker tells 1 and 2 from every other.</summary>
        [Theory]
        [InlineData(2u, 2)]
        [InlineData(7u, 7)]
        [InlineData(0x0Bu, 11)]
        [InlineData(0x10000u, 65536)]
        [InlineData(0x80000000u, int.MaxValue)]
        public void AUnitsKind_IsWhatItsAttributesSay(uint kind, int expected)
        {
            var board = Board();
            board.Kind = kind;
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.Equal(expected, Unit(backend, board).Kind);
        }

        // ── A device that is not driven ──

        /// <summary>A device whose descriptor is not the one PadForge
        /// drives gets one diagnostics line that says why, no read and no
        /// path, and is not asked again while it stays.</summary>
        [Fact]
        public void ADeviceThatIsNotDriven_IsNamedOnceWithTheReason_AndLeftAlone()
        {
            var board = Board(pid: 0xA101);
            board.Rejection = "its range update report (collection 0x60) is an Output report, and only Feature reports are driven"
                              + ". Its lighting fields: 05/60/55 8x1 out";
            string line = "PERIPHERAL LampArray 1234:A101 not used: " + board.Rejection;
            int before = LogLines(line);
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);

            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(before + 1, LogLines(line));
            Assert.Empty(board.Traffic());
            Assert.Equal((0, 0), (_queryOpens, _claimOpens));

            Walk(backend, board);
            Advance(120000);
            Passes(backend);
            Assert.Equal(1, board.DescriptorReads);
            Assert.Equal(before + 1, LogLines(line));

            // It leaves and comes back: a new arrival, read again.
            Walk(backend);
            Passes(backend);
            board.Rejection = null;
            Walk(backend, board);
            Passes(backend);
            Assert.Equal(2, board.DescriptorReads);
            Assert.NotNull(Unit(backend, board));
        }

        /// <summary>What the attributes report says can turn a device down
        /// too: a lamp count of none or past the limit, and a report
        /// Windows' own parser reads otherwise than the layout does.</summary>
        [Theory]
        [InlineData(0, true, "it declares 0 lamps")]
        [InlineData(4097, true, "it declares 4097 lamps")]
        [InlineData(4, false, "Windows reads its attributes report otherwise than the LampArray layout does: 01 04 00")]
        public void AnAttributesReportThatCannotBeTrusted_TurnsTheDeviceDown(int lampCount, bool agree, string why)
        {
            var board = Board(pid: (ushort)(0xA200 + lampCount % 0x100 + (agree ? 0 : 0x80)));
            board.LampCount = lampCount;
            board.Agree = agree;
            string line = $"PERIPHERAL LampArray 1234:{board.ProductId:X4} not used: {why}";
            int before = LogLines(line);
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);

            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(before + 1, LogLines(line));
            Assert.Equal(new[] { "get 01" }, board.Take());
            Assert.True(board.Opened.All(handle => handle.Disposed));
            Advance(120000);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Equal(1, board.DescriptorReads);
        }

        /// <summary>4096 lamps is the most a unit may declare, and is
        /// driven.</summary>
        [Fact]
        public void TheMostLampsAUnitMayDeclare_IsDriven()
        {
            var board = Board(lamps: 4096);
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.NotNull(Unit(backend, board));
            Assert.Equal(4096, LampArrayProtocol.MaxLamps);
        }

        // ── The claim (spec 11.2 steps 2 to 6) ──

        /// <summary>The first color for a unit: its lamps are read, then it
        /// is claimed, then the color follows after the device's own
        /// interval. A new color waits out the interval since the last
        /// one, the claim and the color go out again every four seconds,
        /// and a claim that ends hands the device back.</summary>
        [Fact]
        public void AUnitIsReadClaimedPaintedHeldAndHandedBack_InThatOrder()
        {
            var (backend, board) = Found();
            Assert.Empty(backend.ClaimedPaths);

            // Step 2: the first claim.
            Color(board, 0x112233);
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(4)).Append("set 06 00"), board.Take());
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            var handle = board.Opened.Last();
            Assert.False(handle.FeatureOnly);
            Assert.Equal(1, _claimOpens);
            // The device declares 20 ms between updates, and is promised
            // that long before the first one.
            Advance(19);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new LampCall(Paint(0x112233), Start + 20, false), Assert.Single(board.TakeCalls()));

            // Step 3: the same color again.
            Color(board, 0x112233);
            Passes(backend);
            Assert.Empty(board.Take());

            // Step 4: a new color, no sooner than 33 ms after the last frame.
            Color(board, 0x445566);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(32);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new LampCall(Paint(0x445566), Start + 53, false), Assert.Single(board.TakeCalls()));
            Advance(1000);
            Color(board, 0x445566);
            Passes(backend);
            Assert.Empty(board.Take());

            // Step 5: 4000 ms after the claim, the claim and the color again.
            Advance(LampArrayBackend.ReassertMs - 1053 - 1);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new[]
            {
                new LampCall("set 06 00", Start + 4000, false), new LampCall(Paint(0x445566), Start + 4000, false),
            }, board.TakeCalls());
            // And 4000 ms after that one.
            Advance(3999);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new[] { "set 06 00", Paint(0x445566) }, board.Take());

            // Step 6: the claim is released.
            Assert.False(handle.Disposed);
            Dark(board);
            Passes(backend);
            Assert.Equal(new[] { "set 06 01" }, board.Take());
            Assert.True(handle.Disposed);
            Assert.Empty(backend.ClaimedPaths);
            Assert.Equal(1, _claimOpens);
            Assert.Equal(4000, LampArrayBackend.ReassertMs);

            // Lit again: its lamps are read once per arrival, so the claim
            // comes at once.
            Color(board, 0x778899);
            Passes(backend);
            Assert.Equal(new[] { "set 06 00" }, board.Take());
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x778899) }, board.Take());
            // The log holds what the one read found, once.
            Assert.Equal(1, LogLines($"PERIPHERAL LampArray '{board.Name}' lamps read: 4, painted as 0-3 R255/G255/B255/I1"));

            // The control report is written and never read back.
            Assert.DoesNotContain(board.Traffic(), call => call.Text == "get 06");
        }

        /// <summary>The crash path learns of a claim before the claim goes
        /// out: by the time the device sees AutonomousMode off, the unit is
        /// on record with the report that hands it back. The lamp reads
        /// ahead of it claim nothing.</summary>
        [Fact]
        public void TheCrashRecordHoldsAUnit_ByTheTimeItsClaimIsSeen()
        {
            var (backend, board) = Found();
            var onRecord = new ConcurrentQueue<int>();
            board.OnCall = () => onRecord.Enqueue(backend.ClaimedPaths.Count);
            Color(board, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            board.OnCall = null;

            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(4)).Append("set 06 00").Append(Paint(0x112233)), board.Take());
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 1, 1 }, onRecord);
        }

        /// <summary>The claim keeps a clock of its own: a color that
        /// changes faster than the re-assert never holds it back, so a
        /// device that dropped the claim has it again within the interval.</summary>
        [Fact]
        public void TheClaimIsSentAgainOnItsInterval_WhileTheColorKeepsChanging()
        {
            var (backend, board) = Lit(0x010000);
            // A new color every 100 ms, each set before the clock moves, so
            // every step paints exactly its own.
            const int steps = 81;
            for (int step = 1; step <= steps; step++)
            {
                Color(board, 0x010000 + step);
                Advance(100);
                Passes(backend);
            }
            var calls = board.TakeCalls();
            Assert.Equal(steps, calls.Count(call => call.Text.StartsWith("set 05 ")));
            // The claim went out at the first update 4000 ms after the
            // claim before it: the unit was claimed at the start, and the
            // updates fall 20 ms past each 100.
            var claims = calls.Where(call => call.Text == "set 06 00").Select(call => call.Tick).ToArray();
            Assert.Equal(new[] { Start + 4020, Start + 8020 }, claims);
            // Each claim is followed by the color of that moment.
            int at = Array.FindIndex(calls, call => call.Text == "set 06 00");
            Assert.Equal(new LampCall(Paint(0x010000 + 40), Start + 4020, false), calls[at + 1]);
        }

        /// <summary>An update is sent no sooner than the device's interval
        /// after the last one, the re-assert's color included, which a
        /// device may otherwise ignore. The claim waits for that and no
        /// longer.</summary>
        [Fact]
        public void TheReassertsColor_WaitsOutTheUpdateInterval_LikeAnyOther()
        {
            var (backend, board) = Lit();
            // A new color 10 ms before the re-assert is due.
            Advance(LampArrayBackend.ReassertMs - 20 - 10);
            Color(board, 0x445566);
            Passes(backend);
            Assert.Equal(new LampCall(Paint(0x445566), Start + 3990, false), Assert.Single(board.TakeCalls()));
            Advance(10);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(22);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new[]
            {
                new LampCall("set 06 00", Start + 4023, false), new LampCall(Paint(0x445566), Start + 4023, false),
            }, board.TakeCalls());
        }

        /// <summary>No color is queued. Two that come inside one interval
        /// give one frame, the newer.</summary>
        [Fact]
        public void TwoColorsInsideOneInterval_GiveOneFrame_TheNewer()
        {
            var (backend, board) = Lit();
            Color(board, 0xAA0000);
            Passes(backend);
            Color(board, 0x00BB00);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(33);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x00BB00) }, board.Take());
            Assert.Equal(33, LampArrayProtocol.PaintIntervalMs(board.IntervalUs));
        }

        [Fact]
        public void ADeviceWithALongInterval_IsHeldToItsOwn()
        {
            var (backend, board) = Found(b => b.IntervalUs = 250000);
            Color(board, 0x112233);
            Passes(backend);
            board.Take();
            Advance(249);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());
            Color(board, 0x445566);
            Advance(249);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x445566) }, board.Take());
        }

        /// <summary>Black is a color: the claim stays, and the lamps go
        /// off through the same report, with every channel at zero.</summary>
        [Fact]
        public void Black_IsPaintedLikeAnyColor_AndKeepsTheClaim()
        {
            var (backend, board) = Lit();
            Color(board, 0x000000);
            Advance(33);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x000000, intensity: 0x00) }, board.Take());
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
        }

        /// <summary>Nothing is opened for a unit nobody lights: not while
        /// no row links its path, and not while a row links it with no
        /// claim on it.</summary>
        [Fact]
        public void NothingIsOpenedForWriting_WhileNoRowLightsTheUnit()
        {
            var board = Board();
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            board.Take();

            // A claim with no row linked to the path.
            Color(board, 0x112233);
            Advance(10000);
            Passes(backend);
            // A row linked to it, with no claim.
            Dark(board);
            Link(board);
            Advance(10000);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Equal(0, _claimOpens);
            Assert.Empty(backend.ClaimedPaths);

            // A claim on another row's path lights nothing here.
            var other = Board(OtherPath, pid: 0x0002);
            Color(other, 0x112233);
            Advance(10000);
            Passes(backend);
            Assert.Equal(0, _claimOpens);
        }

        /// <summary>The path leaves the table while a vendor's program
        /// runs: a LampArray unit is handed back all the same, since its
        /// hand-back only returns the device to its own effect, and
        /// nothing more is written after it.</summary>
        [Fact]
        public void WhenItsPathIsWithdrawn_TheUnitIsHandedBack_AndNothingMoreIsWritten()
        {
            var (backend, board) = Lit();
            // What the link pass publishes when a vendor's program starts:
            // the presence first, then the table without the unit's path.
            PeripheralOutputs.Presence = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks
                {
                    Device = board.Row, DirectWhenClosed = true, HasVendorProgram = true,
                    Lighting = new[] { new OutputPath(OutputFamily.ChromaCategory, "keyboard") },
                },
            }));
            Passes(backend);
            Assert.Equal(new[] { "set 06 01" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);
            Assert.True(board.Opened.Last().Disposed);

            Advance(3 * LampArrayBackend.ReassertMs);
            Passes(backend);
            Assert.Empty(board.Take());

            // The program stops and the path comes back: claimed again.
            PeripheralOutputs.Presence = PeripheralPresence.None;
            Link(board);
            Passes(backend);
            Assert.Equal(new[] { "set 06 00" }, board.Take());
        }

        // ── The lamp read (spec 11.2 steps 7 and 14 to 16) ──

        /// <summary>A response that names another lamp is asked for by
        /// name and read again, and the read goes on from there.</summary>
        [Fact]
        public void AResponseForTheWrongLamp_IsAskedForByName_AndReadAgain()
        {
            var (backend, board) = Found(b =>
            {
                b.MisreportLamp = 2;
                b.MisreportAs = 0;
                b.Misreports = 1;
            });
            Color(board, 0x112233);
            Passes(backend);
            Assert.Equal(new[]
            {
                "set 02 00 00", "get 03", "get 03", "get 03", "set 02 02 00", "get 03", "get 03", "set 06 00",
            }, board.Take());
            Advance(20);
            Passes(backend);
            // Its lamps were read, so it is painted at their own counts.
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());
        }

        /// <summary>Firmware that does not count on by itself answers for
        /// the same lamp until asked for the next. Each lamp is then asked
        /// for by name, and every one is read.</summary>
        [Fact]
        public void FirmwareThatDoesNotCountOn_IsAskedForEachLampByName()
        {
            var (backend, board) = Found(b =>
            {
                b.Lamps = new[] { LampBoard.OneLevel, LampBoard.OneLevel, new LampLevels(false, 0, 0, 0, 1) };
                b.LampCount = 3;
                b.CountsOn = false;
            });
            Color(board, 0x0A141E);
            Passes(backend);
            // Each read after the first answers for the lamp before, once,
            // and is asked for by name. One wrong answer a lamp is no
            // reason to give the read up.
            Assert.Equal(new[]
            {
                "set 02 00 00", "get 03", "get 03", "set 02 01 00", "get 03", "get 03", "set 02 02 00", "get 03", "set 06 00",
            }, board.Take());
            Advance(20);
            Passes(backend);
            // Two kinds of lamp in an array of three are more runs than a
            // device must take back to back, so one report at the smallest
            // counts paints them all.
            Assert.Equal(new[] { "set 05 01 00 00 02 00 0A 14 1E 01" }, board.Take());
        }

        /// <summary>A lamp read that fails is tried again after a pause
        /// that grows by 50 ms, and the read goes on when it answers. The
        /// channel stays open through it.</summary>
        [Fact]
        public void ALampReadThatFails_IsTriedAgainAfterAGrowingPause_AndTheReadGoesOn()
        {
            var (backend, board) = Found(b => b.FailLampReads = 2);
            Color(board, 0x112233);
            Passes(backend);
            Assert.Equal(new[] { new LampCall("set 02 00 00", Start, false), new LampCall("get 03", Start, false) }, board.TakeCalls());
            Advance(49);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new LampCall("get 03", Start + 50, false), Assert.Single(board.TakeCalls()));
            Advance(99);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            var calls = board.TakeCalls();
            Assert.Equal(LampReads(4).Append("set 06 00"), calls.Select(call => call.Text));
            Assert.All(calls, call => Assert.Equal(Start + 150, call.Tick));

            Assert.Equal(1, _claimOpens);
            Assert.False(board.Opened.Last().Disposed);
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());
            Assert.False(Unit(backend, board).Asleep);
            Assert.Equal(0, _walkAsks);
        }

        /// <summary>The tries are counted for each lamp apart: two lamps
        /// that each take five are both read, and the unit is painted at
        /// its lamps' own counts.</summary>
        [Fact]
        public void TheTriesOfALampRead_AreCountedForEachLampApart()
        {
            var (backend, board) = Found(b =>
            {
                b.FailLampTimes[0] = 4;
                b.FailLampTimes[2] = 4;
            });
            string line = $"PERIPHERAL LampArray '{board.Name}' lamps could not be read";
            int before = LogLines(line);
            Color(board, 0x112233);
            Passes(backend);
            foreach (int lamp in new[] { 0, 2 })
            {
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    Advance(LampArrayBackend.ReadRetryStepMs * attempt);
                    Passes(backend);
                }
            }
            // Eight failed reads in all, and no lamp failed eight times.
            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(4 + 8)).Append("set 06 00"), board.Take());
            Assert.Equal(before, LogLines(line));
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());
        }

        /// <summary>A lamp read that fails eight times ends the read. The
        /// unit is claimed all the same and painted with the frame that
        /// needs no level count. The channel is not closed, and the unit
        /// is not asleep.</summary>
        [Fact]
        public void ALampReadThatFailsEightTimes_LeavesTheUnitPaintedBlind()
        {
            var (backend, board) = Found(b =>
            {
                b.FailLampIndex = 2;
                b.FailLampReads = LampArrayBackend.ReadAttempts;
            });
            string line = $"PERIPHERAL LampArray '{board.Name}' lamps could not be read (lamp 2 did not answer)";
            int before = LogLines(line);
            Color(board, 0x112233);
            Passes(backend);
            // Lamps 0 and 1 answer, and lamp 2 does not.
            Assert.Equal(new[] { "set 02 00 00", "get 03", "get 03", "get 03" }, board.Take());
            long tick = Start;
            for (int attempt = 1; attempt < LampArrayBackend.ReadAttempts; attempt++)
            {
                int pause = LampArrayBackend.ReadRetryStepMs * attempt;
                Advance(pause - 1);
                Passes(backend);
                Assert.Empty(board.Take());
                Advance(1);
                Passes(backend);
                tick += pause;
                var calls = board.TakeCalls();
                Assert.Equal(new LampCall("get 03", tick, false), calls[0]);
                // The eighth failure ends the read, and the claim follows
                // in the same pass.
                if (attempt < LampArrayBackend.ReadAttempts - 1) Assert.Single(calls);
                else Assert.Equal(new[] { "get 03", "set 06 00" }, calls.Select(call => call.Text));
            }
            Assert.Equal(Start + 1400, tick);
            Assert.Equal(before + 1, LogLines(line));

            Advance(19);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233, intensity: 0xFF) }, board.Take());
            Assert.Equal(1, _claimOpens);
            Assert.False(board.Opened.Last().Disposed);
            Assert.False(Unit(backend, board).Asleep);

            // Blind for as long as it stays: black is all zeros, and a
            // release and a new claim read no lamp again.
            Color(board, 0x000000);
            Advance(33);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x000000, intensity: 0x00) }, board.Take());
            Dark(board);
            Passes(backend);
            Assert.Equal(new[] { "set 06 01" }, board.Take());
            Color(board, 0x445566);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { "set 06 00", Paint(0x445566, intensity: 0xFF) }, board.Take());
            Assert.Equal(before + 1, LogLines(line));
        }

        /// <summary>A second wrong answer for the same lamp ends the read
        /// the same way.</summary>
        [Fact]
        public void ALampThatAnswersForAnotherTwice_LeavesTheUnitPaintedBlind()
        {
            var (backend, board) = Found(b =>
            {
                b.MisreportLamp = 2;
                b.MisreportAs = 0;
                b.Misreports = 2;
            });
            string line = $"PERIPHERAL LampArray '{board.Name}' lamps could not be read (lamp 2 answered as lamp 0 twice)";
            int before = LogLines(line);
            Color(board, 0x112233);
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00", "get 03", "get 03", "get 03", "set 02 02 00", "get 03", "set 06 00" }, board.Take());
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233, intensity: 0xFF) }, board.Take());
            Assert.Equal(before + 1, LogLines(line));
            Assert.False(Unit(backend, board).Asleep);
        }

        /// <summary>A claim that ends while the lamps are still being read
        /// claimed nothing, so nothing is handed back. The channel closes,
        /// and the next claim reads from lamp 0 again.</summary>
        [Fact]
        public void AClaimThatEndsDuringTheLampRead_HandsNothingBack_AndTheNextOneStartsOver()
        {
            var (backend, board) = Found(b =>
            {
                b.FailLampIndex = 1;
                b.FailLampReads = 1;
            });
            Color(board, 0x112233);
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00", "get 03", "get 03" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);

            Dark(board);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.True(board.Opened.Last().Disposed);
            Assert.Empty(backend.ClaimedPaths);

            Advance(1000);
            Color(board, 0x445566);
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(4)).Append("set 06 00"), board.Take());
            Assert.Equal(2, _claimOpens);
        }

        /// <summary>Lamps that take different values are painted run by
        /// run where the device takes that many reports in a row, and in
        /// one report at the smallest counts where it does not: a device
        /// with no multi update report promises one report to an update.</summary>
        [Theory]
        [InlineData(8, new[] { "set 05 00 00 00 07 00 0A 14 1E 01", "set 05 01 08 00 0F 00 00 00 00 01" })]
        [InlineData(0, new[] { "set 05 01 00 00 0F 00 0A 14 1E 01" })]
        public void LampsThatDiffer_ArePaintedInAsManyReportsAsTheDeviceTakes(int slots, string[] expected)
        {
            var (backend, board) = Found(b =>
            {
                b.Lamps = Enumerable.Repeat(LampBoard.OneLevel, 8).Concat(Enumerable.Repeat(new LampLevels(false, 0, 0, 0, 1), 8)).ToArray();
                b.LampCount = 16;
                b.Layout = b.Layout with { MultiUpdateSlots = slots };
            });
            Color(board, 0x0A141E);
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(16)).Append("set 06 00"), board.Take());
            Advance(20);
            Passes(backend);
            // Both reports of one update at the same tick: nothing is
            // waited out between them.
            var calls = board.TakeCalls();
            Assert.Equal(expected, calls.Select(call => call.Text));
            Assert.All(calls, call => Assert.Equal(Start + 20, call.Tick));
        }

        /// <summary>A unit with many lamps is read 32 lamps to a pass, so
        /// the worker sees to its other units in between.</summary>
        [Fact]
        public void ALongLampRead_IsMadeAFewLampsToAPass()
        {
            const int count = 2 * LampArrayBackend.LampsPerPass + 6;
            var (backend, board) = Found(b =>
            {
                b.Lamps = Enumerable.Repeat(LampBoard.OneLevel, count).ToArray();
                b.LampCount = count;
            });
            var passOf = new ConcurrentQueue<int>();
            board.OnCall = () => passOf.Enqueue(backend.PassCount);
            Color(board, 0x112233);
            Assert.True(SpinWait.SpinUntil(() => backend.ClaimedPaths.Count == 1, 30000), "the read ended and the unit was claimed");
            Passes(backend);
            board.OnCall = null;

            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(count)).Append("set 06 00"), board.Take());
            // The request and 32 reads in one pass, 32 in the next, and
            // the last six with the claim in the one after.
            Assert.Equal(new[] { 33, 32, 7 }, passOf.GroupBy(pass => pass).Select(group => group.Count()));
            Assert.Equal(32, LampArrayBackend.LampsPerPass);
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233, lamps: count) }, board.Take());
        }

        // ── A device with no control report (spec 11.2 step 11) ──

        /// <summary>A device with no control report has no effect of its
        /// own to be claimed from: its lamps are read and painted, and a
        /// claim that ends turns them off, an update like any other that
        /// waits out the interval since the last.</summary>
        [Fact]
        public void ADeviceWithNoControlReport_IsPaintedWithNoClaimFrame_AndTurnedOffWhenLetGo()
        {
            var (backend, board) = Found(b => b.Layout = b.Layout with { HasControl = false, ControlId = 0, ControlLink = 0 });
            Color(board, 0x112233);
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(4)), board.Take());
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());

            // The re-assert is the color alone.
            Advance(LampArrayBackend.ReassertMs);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());

            Dark(board);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            Advance(32);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new[] { "set 05 01 00 00 03 00 00 00 00 00" }, board.Take());
            Assert.True(board.Opened.Last().Disposed);
            Assert.Empty(backend.ClaimedPaths);
            Assert.DoesNotContain(board.Traffic(), call => call.Text.StartsWith("set 06") || call.Text.StartsWith("set 00"));
        }

        /// <summary>A device with no control report that was never sent a
        /// color has nothing on it to undo.</summary>
        [Fact]
        public void ADeviceWithNoControlReport_ThatWasNeverPainted_IsSentNothingWhenLetGo()
        {
            var (backend, board) = Found(b => b.Layout = b.Layout with { HasControl = false, ControlId = 0, ControlLink = 0 });
            Color(board, 0x112233);
            Passes(backend);
            board.Take();
            Dark(board);
            Passes(backend);
            Advance(1000);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Empty(backend.ClaimedPaths);
            Assert.True(board.Opened.Last().Disposed);
        }

        // ── Windows Dynamic Lighting (spec 11.2 steps 8 and 9) ──

        /// <summary>A unit Windows lights is listed and never written to,
        /// whatever claims it.</summary>
        [Fact]
        public void AUnitWindowsLights_IsListedAndNeverWrittenTo()
        {
            var board = Board();
            _windowsLit[BoardPath] = true;
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.True(Unit(backend, board).WindowsLit);
            Link(board);
            Color(board, 0x112233);
            Passes(backend);
            Advance(3 * LampArrayBackend.ReassertMs);
            Passes(backend);
            Assert.Equal(new[] { "get 01" }, board.Take());
            Assert.Equal(0, _claimOpens);
            Assert.Empty(backend.ClaimedPaths);
        }

        /// <summary>A unit that turns Windows-lit while claimed is let go
        /// without a frame: a hand-back would switch Windows' own effect
        /// off. When Windows lets go of it, the standing claim paints it
        /// again.</summary>
        [Fact]
        public void AUnitThatTurnsWindowsLitWhileClaimed_IsLetGoWithNoFrame_AndPaintedAgainWhenWindowsLetsGo()
        {
            var (backend, board) = Lit();
            int notices = _notices;
            var handle = board.Opened.Last();

            _windowsLit[BoardPath] = true;
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.True(Unit(backend, board).WindowsLit);
            Assert.True(_notices > notices);
            Assert.Empty(board.Take());
            Assert.True(handle.Disposed);
            Assert.Empty(backend.ClaimedPaths);

            Color(board, 0x445566);
            Advance(3 * LampArrayBackend.ReassertMs);
            Passes(backend);
            Assert.Empty(board.Take());

            _windowsLit[BoardPath] = false;
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.False(Unit(backend, board).WindowsLit);
            Assert.Equal(new[] { "set 06 00" }, board.Take());
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x445566) }, board.Take());
        }

        /// <summary>OpenRGB holds the device (#557): the linker closes the
        /// unit's path and names it taken, and the worker lets go without a
        /// frame, as for Windows. The same table without the mark hands
        /// the unit back.</summary>
        [Fact]
        public void APathOpenRgbTakes_IsLetGoWithNoFrame()
        {
            var (backend, board) = Lit();
            var handle = board.Opened.Last();
            LinkTable ThroughOpenRgb(bool taken) => new(new[]
            {
                new DeviceLinks
                {
                    Device = board.Row,
                    Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                    TakenByOpenRgb = taken ? new[] { board.OutputPath } : Array.Empty<OutputPath>(),
                },
            });

            PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: true));
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.True(handle.Disposed);
            Assert.Empty(backend.ClaimedPaths);

            // OpenRGB lets go: claimed and painted again. The path then
            // goes with no mark: handed back.
            Link(board);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.Equal(Paint(0x112233), board.Take().Last());
            PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: false));
            Passes(backend);
            Assert.Contains("set 06 01", board.Take());
            Assert.Empty(backend.ClaimedPaths);
        }

        /// <summary>The switch is read again right before a hand-back,
        /// since the answer the worker holds can be a second old.</summary>
        [Fact]
        public void TheSwitchIsReadAgain_RightBeforeAHandBack()
        {
            var (backend, board) = Lit();
            Advance(10);
            Passes(backend);
            _windowsLit[BoardPath] = true;
            Dark(board);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.True(board.Opened.Last().Disposed);
            Assert.Empty(backend.ClaimedPaths);
            Assert.True(Unit(backend, board).WindowsLit);
        }

        /// <summary>A read of the switches that cannot be made counts as
        /// lit: standing down for nothing costs a color, and writing
        /// beside Windows makes two writers fight.</summary>
        [Fact]
        public void AReadOfTheSwitchesThatCannotBeMade_CountsAsLit()
        {
            var (backend, board) = Lit();
            var handle = board.Opened.Last();
            _litFault = true;
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.True(Unit(backend, board).WindowsLit);
            Assert.Empty(board.Take());
            Assert.True(handle.Disposed);
            Assert.Empty(backend.ClaimedPaths);

            // The read works again, and says Windows does not light it.
            _litFault = false;
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.False(Unit(backend, board).WindowsLit);
            Assert.Equal(new[] { "set 06 00" }, board.Take());
        }

        /// <summary>Windows' switches are read once a second while a unit
        /// is present, and not at all while none is.</summary>
        [Fact]
        public void TheSwitchesAreReadOnceASecond_WhileAUnitIsPresent()
        {
            var board = Board();
            var backend = Backend();
            backend.Start();
            Walk(backend);
            Passes(backend);
            Advance(5000);
            Passes(backend);
            Assert.Equal(0, _litReads);

            Walk(backend, board);
            Passes(backend);
            int reads = _litReads;
            Assert.True(reads >= 1);
            Advance(LampArrayBackend.LitReadMs - 1);
            Passes(backend);
            Assert.Equal(reads, _litReads);
            Advance(1);
            Passes(backend);
            Assert.Equal(reads + 1, _litReads);
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.Equal(reads + 2, _litReads);

            // A change is listed, with a notice.
            int notices = _notices;
            _windowsLit[BoardPath] = true;
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.True(Unit(backend, board).WindowsLit);
            Assert.Equal(notices + 1, _notices);
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.Equal(notices + 1, _notices);
            Assert.Equal(1000, LampArrayBackend.LitReadMs);
        }

        /// <summary>A unit found between two reads of the switches is
        /// asked about at once. It is listed with Windows' answer of that
        /// moment, and a unit Windows lights is not written to while the
        /// next read is still to come.</summary>
        [Fact]
        public void AUnitFoundBetweenTwoReadsOfTheSwitches_IsAskedAboutAtOnce()
        {
            var (backend, board) = Found();
            var other = Board(OtherPath, pid: 0x0002);
            _windowsLit[OtherPath] = true;
            Link(board, other);
            Color(other, 0x112233);
            Advance(LampArrayBackend.LitReadMs / 2);
            Walk(backend, board, other);
            Passes(backend);
            Assert.True(Unit(backend, other).WindowsLit);
            Assert.False(Unit(backend, board).WindowsLit);
            Assert.Equal(new[] { "get 01" }, other.Take());
            Assert.Equal(0, _claimOpens);
            Assert.Empty(backend.ClaimedPaths);
        }

        // ── A unit that cannot be written to (spec 11.2 step 10) ──

        /// <summary>A failed write closes the unit's channel, lists the
        /// unit asleep and asks for a walk once. Nothing is written to it
        /// while it sleeps. It is asked whether it answers again after 5
        /// seconds, then 10, then 15, and when it does it is listed awake
        /// and the standing claim reads, claims and paints it anew.</summary>
        [Fact]
        public void AFailedWrite_ListsTheUnitAsleep_AsksForAWalkOnce_AndTheUnitWakesWhenItAnswers()
        {
            var (backend, board) = Lit();
            var handle = board.Opened.Last();
            int notices = _notices;
            int lookups = _containerLookups;
            string line = $"PERIPHERAL LampArray '{board.Name}' is asleep: a color write failed";
            int before = LogLines(line);

            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            var asleep = Unit(backend, board);
            Assert.True(asleep.Asleep);
            Assert.Equal(Box, asleep.ContainerId);
            Assert.True(backend.Snapshot.Scanned);
            Assert.True(handle.Disposed);
            // Claimed when the write failed, so its hand-back is owed, and
            // the crash path's record keeps it.
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            Assert.Equal(notices + 1, _notices);
            Assert.Equal(1, _walkAsks);
            Assert.Equal(before + 1, LogLines(line));
            Assert.Empty(board.Take());

            // Asleep: the claim stands, the table still links the path, and
            // nothing is opened or written.
            board.FailWrites = false;
            board.Unreadable = true;
            long failedAt = Tick();
            int reads = board.DescriptorReads;
            string later = $"PERIPHERAL LampArray '{board.Name}' is asked again later: its descriptor could not be read";
            int laterBefore = LogLines(later);
            Advance(LampArrayBackend.FirstRetryMs - 1);
            Passes(backend);
            Assert.Equal(reads, board.DescriptorReads);
            Assert.Equal((1, 1), (_queryOpens, _claimOpens));

            // It is asked again after 5 seconds, then 10, then 15, then 15.
            Advance(1);
            Passes(backend);
            Assert.Equal(++reads, board.DescriptorReads);
            foreach (int wait in new[] { 10000, 15000, 15000 })
            {
                Advance(wait - 1);
                Passes(backend);
                Assert.Equal(reads, board.DescriptorReads);
                Advance(1);
                Passes(backend);
                Assert.Equal(++reads, board.DescriptorReads);
                Assert.True(Unit(backend, board).Asleep);
            }
            Assert.Equal(failedAt + 5000 + 10000 + 15000 + 15000, Tick());
            Assert.Empty(board.Take());
            Assert.Equal(1, _walkAsks);
            Assert.Equal(notices + 1, _notices);
            // Asked four times in vain, and named once.
            Assert.Equal(laterBefore + 1, LogLines(later));

            // It answers: one read of its attributes, and it is awake.
            board.Unreadable = false;
            Advance(LampArrayBackend.MaxRetryMs);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
            Assert.Equal(notices + 2, _notices);
            var calls = board.TakeCalls();
            Assert.Equal(new LampCall("get 01", Tick(), QueryOnly: true), calls[0]);
            // Its name and its container are the ones it was listed with.
            // Neither is asked for again.
            Assert.Equal(1, board.NameReads);
            Assert.Equal(lookups, _containerLookups);
            // The standing claim, on the same pass or the next: its lamps
            // are read again, since a device that came back may not be the
            // device that left.
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(4)).Append("set 06 00"),
                calls.Skip(1).Select(call => call.Text).Concat(board.Take()));
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x445566) }, board.Take());
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);

            // Painted, so the next failure starts the backoff over.
            board.FailWrites = true;
            Color(board, 0x778899);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(2, _walkAsks);
            board.FailWrites = false;
            reads = board.DescriptorReads;
            Advance(LampArrayBackend.FirstRetryMs - 1);
            Passes(backend);
            Assert.Equal(reads, board.DescriptorReads);
            Advance(1);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
        }

        /// <summary>An asleep unit whose answer cannot be trusted when it
        /// is asked again stays listed asleep, and is asked again after
        /// the backoff. Only a collection that left the walk takes its
        /// unit off the list.</summary>
        [Fact]
        public void AnAsleepUnitWhoseAnswerCannotBeTrusted_StaysListedAsleep_AndIsAskedAgain()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            board.FailWrites = false;

            // It answers, and Windows' parser reads the report otherwise
            // than the layout does.
            board.Agree = false;
            Advance(LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(new[] { "get 01" }, board.Take());
            // Then its descriptor is turned down outright.
            board.Agree = true;
            board.Rejection = "its range update report (collection 0x60) is an Output report, and only Feature reports are driven";
            Advance(2 * LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Empty(board.Take());

            // And then it answers as it did when it was found.
            board.Rejection = null;
            Advance(LampArrayBackend.MaxRetryMs);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
            Assert.Equal(Box, Unit(backend, board).ContainerId);
        }

        /// <summary>The unit leaves the list when its collection leaves
        /// the walk, asleep or awake, and not before.</summary>
        [Fact]
        public void AUnitLeavesTheList_WhenItsCollectionLeavesTheWalk()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);

            // Walks that still hold its collection keep it listed.
            Walk(backend, board);
            Advance(60000);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            int notices = _notices;

            Walk(backend);
            Passes(backend);
            Assert.Empty(backend.Snapshot.Units);
            Assert.True(backend.Snapshot.Scanned);
            Assert.Equal(notices + 1, _notices);
            // Nothing is owed to a device that left, and nothing was sent.
            Assert.Empty(backend.ClaimedPaths);
            Assert.Empty(_oneShots);

            // Back on the same path: found anew, and awake.
            board.FailWrites = false;
            Walk(backend, board);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
        }

        /// <summary>A claimed unit whose collection leaves the walk is
        /// forgotten with nothing sent: its handle leads nowhere.</summary>
        [Fact]
        public void AClaimedUnitWhoseCollectionLeft_IsForgottenWithNothingSent()
        {
            var (backend, board) = Lit();
            var handle = board.Opened.Last();
            Walk(backend);
            Passes(backend);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Empty(board.Take());
            Assert.True(handle.Disposed);
            Assert.Empty(backend.ClaimedPaths);
            Assert.Equal(0, _walkAsks);
        }

        /// <summary>A unit that cannot be opened for its claim, a lamp
        /// request that fails, a claim that fails and a hand-back that
        /// fails all follow the same rule. From the claim report on, the
        /// device may hold the claim, so the unit is owed its hand-back
        /// and stays on the crash path's record.</summary>
        [Theory]
        [InlineData("open", "it could not be opened", false)]
        [InlineData("request", "its lamp request failed", false)]
        [InlineData("claim", "its claim failed", true)]
        [InlineData("handback", "its hand-back failed", true)]
        [InlineData("reassert", "its claim failed", true)]
        [InlineData("rerequest", "its lamp request failed", false)]
        public void EveryWriteThatFails_FollowsTheSameRule(string what, string why, bool owed)
        {
            var (backend, board) = Found();
            string line = $"PERIPHERAL LampArray '{board.Name}' is asleep: " + why;
            int before = LogLines(line);
            switch (what)
            {
                case "rerequest":
                    // The third read answers for the wrong lamp, and the
                    // request that names the right one fails.
                    board.MisreportLamp = 2;
                    board.MisreportAs = 0;
                    board.Misreports = 1;
                    board.OnCall = () =>
                    {
                        if (board.Traffic().Count(call => call.Text == "get 03") == 2) board.FailWrites = true;
                    };
                    Color(board, 0x112233);
                    break;
                case "open":
                    board.Unopenable = true;
                    Color(board, 0x112233);
                    break;
                case "request":
                    board.FailWrites = true;
                    Color(board, 0x112233);
                    break;
                case "claim":
                    // The lamp reads answer, and the write after them fails.
                    board.OnCall = () =>
                    {
                        if (board.Traffic().Count(call => call.Text == "get 03") == 3) board.FailWrites = true;
                    };
                    Color(board, 0x112233);
                    break;
                case "handback":
                    Color(board, 0x112233);
                    Passes(backend);
                    Advance(20);
                    Passes(backend);
                    board.FailWrites = true;
                    Dark(board);
                    break;
                case "reassert":
                    Color(board, 0x112233);
                    Passes(backend);
                    Advance(20);
                    Passes(backend);
                    board.FailWrites = true;
                    Advance(LampArrayBackend.ReassertMs);
                    break;
            }
            Passes(backend);
            board.OnCall = null;

            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(1, _walkAsks);
            Assert.Equal(before + 1, LogLines(line));
            Assert.Equal(owed ? new[] { BoardPath } : Array.Empty<string>(), backend.ClaimedPaths);
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
            Assert.DoesNotContain(board.Traffic(), call => call.Text == "set 06 01");

            // The claim ends while the unit sleeps, and the unit answers
            // again: a hand-back that is owed goes out then, and a unit
            // that is owed none is sent nothing.
            board.FailWrites = false;
            board.Unopenable = false;
            Dark(board);
            board.Take();
            Advance(LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
            Assert.Equal(owed ? new[] { "get 01", "set 06 01" } : new[] { "get 01" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
        }

        /// <summary>A unit that fell asleep while claimed is owed its
        /// hand-back: a device that missed one report and stayed has
        /// AutonomousMode off, and keeps the last color until it loses
        /// power. While it sleeps nothing is sent to it. When it answers
        /// again with nothing claiming it, the hand-back goes out on a
        /// channel opened for it, once, and the crash path's record lets
        /// the unit go.</summary>
        [Fact]
        public void AUnitThatFellAsleepClaimed_IsHandedBackWhenItAnswersAgain_WithNothingClaimingIt()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            board.FailWrites = false;
            board.Take();

            // The claim ends while the unit sleeps: nothing is sent, and
            // the debt stands.
            Dark(board);
            Advance(LampArrayBackend.FirstRetryMs - 1);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            int claimOpens = _claimOpens;
            string line = $"PERIPHERAL LampArray '{board.Name}' lighting handed back";
            int before = LogLines(line);

            // It answers: described on the handle with no access right,
            // then handed back on one opened for that and closed again.
            Advance(1);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
            var calls = board.TakeCalls();
            Assert.Equal(new[] { "get 01", "set 06 01" }, calls.Select(call => call.Text));
            Assert.Equal(new[] { true, false }, calls.Select(call => call.QueryOnly));
            Assert.Equal(claimOpens + 1, _claimOpens);
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
            Assert.Empty(backend.ClaimedPaths);
            Assert.Equal(before + 1, LogLines(line));

            // Once. Nothing more goes to a unit nobody lights.
            Advance(60000);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Equal(claimOpens + 1, _claimOpens);
            Assert.Empty(_oneShots);
        }

        /// <summary>A device with no control report is owed its lamps'
        /// off, the report that hands it back.</summary>
        [Fact]
        public void ADeviceWithNoControlReport_ThatFellAsleepPainted_HasItsLampsTurnedOffWhenItAnswersAgain()
        {
            var (backend, board) = Lit(setUp: b => b.Layout = b.Layout with { HasControl = false, ControlId = 0, ControlLink = 0 });
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            board.FailWrites = false;
            Dark(board);
            board.Take();
            Advance(LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Assert.Equal(new[] { "get 01", "set 05 01 00 00 03 00 00 00 00 00" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);
            Assert.Empty(_pauses);
        }

        /// <summary>A hand-back that cannot be paid stands: the unit goes
        /// back to sleep, a walk is asked for, it is asked again after the
        /// next backoff, and it stays on the crash path's record until its
        /// device takes the report.</summary>
        [Theory]
        [InlineData("open")]
        [InlineData("write")]
        public void AnOwedHandBackThatFails_Stands_AndIsPaidWhenTheDeviceTakesIt(string what)
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Dark(board);
            board.Take();
            board.FailWrites = false;
            if (what == "open") _claimUnopenable = true;
            // The attributes read answers, and every report after it fails.
            else board.OnCall = () => board.FailWrites = true;
            string line = $"PERIPHERAL LampArray '{board.Name}' is asleep: "
                          + (what == "open" ? "it could not be opened" : "its hand-back failed");
            int before = LogLines(line);

            // It answers its attributes read, and the hand-back fails.
            Advance(LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(new[] { "get 01" }, board.Take());
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            Assert.Equal(2, _walkAsks);
            Assert.Equal(before + 1, LogLines(line));
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));

            // Asked again after ten seconds, the second step of the backoff,
            // and not before.
            board.OnCall = null;
            board.FailWrites = false;
            _claimUnopenable = false;
            Advance(2 * LampArrayBackend.FirstRetryMs - 1);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
            Assert.Equal(new[] { "get 01", "set 06 01" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);

            // Paid, so the next failure starts the backoff over: a unit
            // that cannot be opened for a new claim, with no color painted
            // in between, is asked again after five seconds.
            _claimUnopenable = true;
            Color(board, 0x778899);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Empty(backend.ClaimedPaths);
            _claimUnopenable = false;
            int reads = board.DescriptorReads;
            Advance(LampArrayBackend.FirstRetryMs - 1);
            Passes(backend);
            Assert.Equal(reads, board.DescriptorReads);
            Advance(1);
            Passes(backend);
            Assert.Equal(reads + 1, board.DescriptorReads);
        }

        /// <summary>A hand-back owed to a unit Windows lights by the time
        /// it answers again is dropped with no frame: Windows repaints the
        /// device, and AutonomousMode on would switch its effect off. It
        /// stays dropped when Windows lets the unit go.</summary>
        [Fact]
        public void AnOwedHandBack_IsDropped_WhenWindowsLightsTheUnitByThen()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            board.FailWrites = false;
            Dark(board);
            board.Take();
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);

            _windowsLit[BoardPath] = true;
            Advance(LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
            Assert.True(Unit(backend, board).WindowsLit);
            Assert.Equal(new[] { "get 01" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);

            _windowsLit[BoardPath] = false;
            Advance(LampArrayBackend.LitReadMs);
            Passes(backend);
            Assert.False(Unit(backend, board).WindowsLit);
            Assert.Empty(board.Take());
            Assert.Equal((2, 1), (_queryOpens, _claimOpens));
        }

        /// <summary>The switch is read again right before an owed
        /// hand-back too, since the cached answer can be a second old. Here
        /// the unit answers again under a standing claim, the claim ends
        /// while its lamps are still being read, and Windows took the unit
        /// since the worker last looked.</summary>
        [Fact]
        public void TheSwitchIsReadAgain_RightBeforeAnOwedHandBack()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            board.FailWrites = false;
            board.Take();

            // Awake under the standing claim, and held in the lamp read by
            // one read that fails, so no new claim has gone out yet.
            board.FailLampReads = 1;
            Advance(LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Assert.False(Unit(backend, board).Asleep);
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            var woke = board.Take();
            Assert.Equal("get 01", woke[0]);
            Assert.Contains("set 02 00 00", woke);
            Assert.DoesNotContain("set 06 00", woke);

            int litReads = _litReads;
            _windowsLit[BoardPath] = true;
            Dark(board);
            Passes(backend);
            Passes(backend);
            Assert.True(_litReads > litReads, "the switch was read for the hand-back");
            Assert.True(Unit(backend, board).WindowsLit);
            Assert.DoesNotContain(board.Traffic(), call => call.Text == "set 06 01");
            Assert.Empty(backend.ClaimedPaths);
        }

        /// <summary>A claim that takes the unit anew ends the debt: its own
        /// end hands the device back, once.</summary>
        [Fact]
        public void AClaimThatTakesTheUnitAnew_EndsTheDebt_AndItsOwnEndHandsBackOnce()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            board.FailWrites = false;
            board.Take();

            // Awake under the standing claim: read, claimed and painted.
            Advance(LampArrayBackend.FirstRetryMs);
            Passes(backend);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.Equal(Paint(0x445566), board.Take().Last());

            // Let go: one hand-back, on the claim's own channel.
            Dark(board);
            Passes(backend);
            Assert.Equal(new[] { "set 06 01" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);
            Advance(60000);
            Passes(backend);
            Assert.Empty(board.Take());
            Assert.Empty(_oneShots);
        }

        // ── A scan that gets no answer (spec 11.2 step 17) ──

        /// <summary>The scan's read is tried eight times on the handle
        /// with no access right and eight on the handle a claim uses, with
        /// the growing pause between tries. A collection that still does
        /// not answer is not listed, and is asked again after 5 seconds.
        /// A row keeps its record until the first scan has that answer.</summary>
        [Fact]
        public void AScanThatGetsNoAnswer_TriesBothHandlesEightTimes_AndAsksAgainLater()
        {
            var board = Board(pid: 0xA301);
            board.Name = "Quiet Lamps";
            board.FailAttributeReads = 1000;
            string line = "PERIPHERAL LampArray 1234:A301 is asked again later: it did not answer its attributes read";
            int before = LogLines(line);
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.Equal(new LampCall("get 01", Start, QueryOnly: true), Assert.Single(board.TakeCalls()));
            Assert.False(backend.Snapshot.Scanned);

            long tick = Start;
            foreach (bool queryOnly in new[] { true, false })
            {
                for (int attempt = 1; attempt < LampArrayBackend.ReadAttempts; attempt++)
                {
                    int pause = LampArrayBackend.ReadRetryStepMs * attempt;
                    Advance(pause - 1);
                    Passes(backend);
                    Assert.Empty(board.Take());
                    Advance(1);
                    Passes(backend);
                    tick += pause;
                    var calls = board.TakeCalls();
                    Assert.Equal(new LampCall("get 01", tick, queryOnly), calls[0]);
                    // The eighth failure on the first handle is followed at
                    // once by the first try on the second.
                    if (queryOnly && attempt == LampArrayBackend.ReadAttempts - 1)
                        Assert.Equal(new LampCall("get 01", tick, false), Assert.Single(calls.Skip(1)));
                    else Assert.Single(calls);
                }
                if (queryOnly) Assert.False(backend.Snapshot.Scanned);
            }
            Assert.Equal(Start + 2800, tick);

            // Both handles are closed, the unit is not listed, and the
            // scan is over.
            Assert.Equal((1, 1), (_queryOpens, _claimOpens));
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(before + 1, LogLines(line));
            Assert.Equal(0, _walkAsks);
            Assert.Equal(16, board.Traffic().Length);

            // Asked again after 5 seconds, and found when it answers.
            board.FailAttributeReads = 0;
            Advance(LampArrayBackend.FirstRetryMs - 1);
            Passes(backend);
            Assert.Empty(board.Take());
            Advance(1);
            Passes(backend);
            Assert.Equal(new LampCall("get 01", Start + 7800, QueryOnly: true), Assert.Single(board.TakeCalls()));
            Assert.Equal("Quiet Lamps", Unit(backend, board).Name);
            Assert.Equal(before + 1, LogLines(line));
        }

        /// <summary>The read on the second handle is what answers where
        /// the first cannot carry it.</summary>
        [Fact]
        public void TheScansRead_IsAnsweredOnTheSecondHandle_WhereTheFirstCannotCarryIt()
        {
            var board = Board();
            board.FailAttributeReads = LampArrayBackend.ReadAttempts;
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            for (int attempt = 1; attempt < LampArrayBackend.ReadAttempts; attempt++)
            {
                Advance(LampArrayBackend.ReadRetryStepMs * attempt);
                Passes(backend);
            }
            var calls = board.TakeCalls();
            Assert.Equal(9, calls.Length);
            Assert.All(calls.Take(8), call => Assert.True(call.QueryOnly));
            Assert.False(calls[8].QueryOnly);
            Assert.NotNull(Unit(backend, board));
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
        }

        /// <summary>A descriptor that cannot be read now says nothing
        /// about the device: it is asked again on the backoff, never
        /// turned down. So is a collection no handle opens on, with no
        /// pause between the two handles.</summary>
        [Theory]
        [InlineData("descriptor")]
        [InlineData("parser")]
        [InlineData("handles")]
        public void ACollectionThatCannotBeReadNow_IsAskedAgain_NeverTurnedDown(string what)
        {
            var board = Board();
            if (what == "descriptor") board.Unreadable = true;
            if (what == "parser") board.Agree = null;
            if (what == "handles") board.Unopenable = true;
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(1, board.DescriptorReads);
            Assert.Equal(what == "handles" ? (1, 1) : what == "parser" ? (1, 0) : (0, 0), (_queryOpens, _claimOpens));

            board.Unreadable = false;
            board.Agree = true;
            board.Unopenable = false;
            Advance(LampArrayBackend.FirstRetryMs - 1);
            Passes(backend);
            Assert.Empty(backend.Snapshot.Units);
            Advance(1);
            Passes(backend);
            Assert.NotNull(Unit(backend, board));
            Assert.Equal(2, board.DescriptorReads);
        }

        // ── Containers ──

        /// <summary>A container that is not known yet is no container: the
        /// unit is listed without one, which matches no row, and each walk
        /// asks again until it answers.</summary>
        [Fact]
        public void AContainerThatIsNotKnownYet_IsAskedForAgainAtEachWalk()
        {
            var board = Board();
            _containers.TryRemove(BoardPath, out _);
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Assert.Equal(Guid.Empty, Unit(backend, board).ContainerId);
            int lookups = _containerLookups;
            Assert.True(lookups >= 1);

            Advance(60000);
            Passes(backend);
            Assert.Equal(lookups, _containerLookups);
            Walk(backend, board);
            Passes(backend);
            Assert.Equal(lookups + 1, _containerLookups);
            Assert.Equal(Guid.Empty, Unit(backend, board).ContainerId);

            int notices = _notices;
            _containers[BoardPath] = Box;
            Walk(backend, board);
            Passes(backend);
            Assert.Equal(Box, Unit(backend, board).ContainerId);
            Assert.Equal(notices + 1, _notices);
            lookups = _containerLookups;
            Walk(backend, board);
            Passes(backend);
            Assert.Equal(lookups, _containerLookups);
        }

        // ── Several units ──

        /// <summary>A unit waiting out the pause of a failed read holds up
        /// no other unit: the worker comes back to it, and paints the
        /// other meanwhile.</summary>
        [Fact]
        public void AUnitThatIsSlowToAnswer_HoldsUpNoOtherUnit()
        {
            var slow = Board();
            slow.FailLampReads = 3;
            var quick = Board(OtherPath, lamps: 2, pid: 0x0002);
            var backend = Backend();
            backend.Start();
            Walk(backend, slow, quick);
            Passes(backend);
            Assert.Equal(new[] { OtherPath, BoardPath }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                backend.Snapshot.Units.Select(unit => unit.Key));
            slow.Take();
            quick.Take();
            Link(slow, quick);

            Color(slow, 0x112233);
            Color(quick, 0x445566);
            Passes(backend);
            Assert.Equal(new[] { "set 02 00 00", "get 03" }, slow.Take());
            Assert.Equal(new[] { "set 02 00 00", "get 03", "get 03", "set 06 00" }, quick.Take());
            Advance(20);
            Passes(backend);
            // The quick one is painted while the slow one still waits.
            Assert.Equal(new[] { Paint(0x445566, lamps: 2) }, quick.Take());
            Assert.Empty(slow.Take());
            Assert.Equal(new[] { OtherPath }, backend.ClaimedPaths);

            Advance(30);
            Passes(backend);
            Advance(100);
            Passes(backend);
            Advance(150);
            Passes(backend);
            Assert.Equal(new[] { "get 03", "get 03" }.Concat(LampReads(4)).Append("set 06 00"), slow.Take());
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, slow.Take());
            Assert.Equal(2, backend.ClaimedPaths.Count);
        }

        // ── Stop and the calls after it ──

        /// <summary>Stop hands back every unit the worker holds on its own
        /// channel before it closes it. A stopped backend takes every call
        /// and does nothing: it keeps or drops what it is handed, opens
        /// nothing, starts no thread and throws nothing. Started again, it
        /// scans afresh.</summary>
        [Fact]
        public void StopHandsBackAndCloses_AndAStoppedBackendTakesEveryCallAndDoesNothing()
        {
            var (backend, board) = Lit();
            var handle = board.Opened.Last();
            backend.Stop();
            Assert.False(backend.WorkerAlive);
            Assert.Equal(new[] { "set 06 01" }, board.Take());
            Assert.True(handle.Disposed);
            Assert.Empty(backend.ClaimedPaths);
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);

            int passes = backend.PassCount;
            int reads = board.DescriptorReads;
            Walk(backend, board);
            backend.Rescan(null);
            backend.SignalStop();
            backend.ReleaseClaimedNow(Tick() + 1000);
            backend.WaitForSilenceIsTheInterfacesOwn();
            backend.Stop();
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
            Assert.False(backend.WorkerAlive);
            Assert.Equal(passes, backend.PassCount);
            Assert.Equal(reads, board.DescriptorReads);
            Assert.Empty(board.Take());
            Assert.Empty(_oneShots);
            Assert.Equal((1, 1), (_queryOpens, _claimOpens));

            // A new run: nothing is known until the host's next walk, and
            // the walk handed over while it was stopped was dropped.
            backend.Start();
            Passes(backend);
            Assert.False(backend.Snapshot.Scanned);
            Assert.Equal(reads, board.DescriptorReads);
            Walk(backend, board);
            Passes(backend);
            Assert.NotNull(Unit(backend, board));
            Assert.Equal(reads + 1, board.DescriptorReads);
            // The standing claim: read, claimed and painted as for a new unit.
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.Equal(new[] { "get 01", "set 02 00 00" }.Concat(LampReads(4)).Append("set 06 00").Append(Paint(0x112233)),
                board.Take());

            // Disposed: handed back once more, and every call still does
            // nothing.
            backend.Dispose();
            Assert.Equal(new[] { "set 06 01" }, board.Take());
            backend.Start();
            Assert.False(backend.WorkerAlive);
            Walk(backend, board);
            backend.SignalStop();
            backend.ReleaseClaimedNow(Tick() + 1000);
            backend.Stop();
            backend.Dispose();
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
            Assert.Empty(board.Take());
            Assert.Empty(_oneShots);
        }

        /// <summary>A unit Windows lights and a unit that is asleep and
        /// owed nothing get nothing at stop either, a unit still reading
        /// its lamps was never claimed, and a device with no control
        /// report that no color has reached yet has nothing on it to turn
        /// off.</summary>
        [Theory]
        [InlineData("windows")]
        [InlineData("asleep")]
        [InlineData("reading")]
        [InlineData("unpainted")]
        public void StopSendsNothing_ToAUnitItDoesNotHold(string what)
        {
            var (backend, board) = what switch
            {
                "reading" => Found(b => b.FailLampReads = 1),
                "unpainted" => Found(b => b.Layout = b.Layout with { HasControl = false, ControlId = 0, ControlLink = 0 }),
                "asleep" => Found(),
                _ => Lit(),
            };
            switch (what)
            {
                case "unpainted":
                    // Claimed, and its first color still waits out the
                    // interval.
                    Color(board, 0x112233);
                    Passes(backend);
                    Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
                    break;
                case "windows":
                    // Windows took it since the worker last looked.
                    _windowsLit[BoardPath] = true;
                    break;
                case "asleep":
                    // It could not be opened for its claim, so no claim
                    // ever went out to it.
                    board.Unopenable = true;
                    Color(board, 0x445566);
                    Passes(backend);
                    Assert.True(Unit(backend, board).Asleep);
                    board.Unopenable = false;
                    break;
                case "reading":
                    Color(board, 0x112233);
                    Passes(backend);
                    break;
            }
            board.Take();
            backend.Stop();
            Assert.Empty(board.Take());
            Assert.Empty(_pauses);
            Assert.Empty(_oneShots);
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
            Assert.Empty(backend.ClaimedPaths);
        }

        /// <summary>At stop a unit that is owed its hand-back gets it
        /// through a handle of its own, bounded as on the crash path: the
        /// worker has no channel to a unit that is asleep. A unit Windows
        /// lights by then gets nothing.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AtStop_AUnitOwedItsHandBack_GetsItThroughAHandleOfItsOwn(bool windowsLit)
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            board.Take();
            _windowsLit[BoardPath] = windowsLit;

            backend.Stop();
            Assert.Empty(board.Take());
            Assert.Equal(
                windowsLit ? Array.Empty<(string, string, int)>() : new[] { (BoardPath, "06 01", LampArrayBackend.OneShotMs) },
                _oneShots);
            Assert.Empty(backend.ClaimedPaths);
            Assert.Empty(_pauses);
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
        }

        /// <summary>The table that names a unit's path taken by OpenRGB, the
        /// row lit through OpenRGB's own path.</summary>
        private static LinkTable TakenByOpenRgb(LampBoard board) => new(new[]
        {
            new DeviceLinks
            {
                Device = board.Row,
                Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                TakenByOpenRgb = new[] { board.OutputPath },
            },
        });

        /// <summary>A unit asleep and owed its hand-back loses the debt the
        /// pass after OpenRGB takes its device: the worker's pass walks an
        /// asleep unit for that alone, and nothing reaches OpenRGB's lighting
        /// at stop or on the crash path.</summary>
        [Fact]
        public void AnAsleepUnitOwedItsHandBack_LosesTheDebt_OnceOpenRgbTakesItsDevice()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);
            board.Take();

            PeripheralOutputs.PublishLinks(TakenByOpenRgb(board));
            Passes(backend);
            Assert.Empty(backend.ClaimedPaths);
            backend.Stop();
            Assert.Empty(_oneShots);
            Assert.Empty(board.Take());
        }

        /// <summary>OpenRGB takes the device while the worker is held inside
        /// a report, so no pass has let the unit go yet. The crash path's
        /// one-shot skips it, as HidLightingBackend.ReleaseClaimedNow does.</summary>
        [Fact]
        public void TheCrashPath_SendsNothing_ToAUnitOpenRgbHolds()
        {
            var (backend, board) = Lit();
            Advance(33);
            var release = HoldInTheNextReport(board);
            try
            {
                PeripheralOutputs.PublishLinks(TakenByOpenRgb(board));
                backend.ReleaseClaimedNow(Tick() + 100);
                Assert.Empty(_oneShots);
                Assert.Empty(backend.ClaimedPaths);
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
        }

        /// <summary>A stop that comes before any pass has seen OpenRGB's hold
        /// hands nothing back on the worker's own channel either.</summary>
        [Fact]
        public void AtStop_TheWorkersChannelSendsNothing_ToAUnitOpenRgbHolds()
        {
            var (backend, board) = Lit();
            Advance(33);
            var release = HoldInTheNextReport(board);
            try
            {
                board.Take();
                PeripheralOutputs.PublishLinks(TakenByOpenRgb(board));
                backend.SignalStop();
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.DoesNotContain("set 06 01", board.Take());
            Assert.Empty(_oneShots);
        }

        /// <summary>The worker's maps go when it stops, as every other direct
        /// backend's do, so a unit unplugged while the engine is stopped does
        /// not stay on the zone picker's list.</summary>
        [Fact]
        public void AtStop_AUnitsMapIsTakenDown()
        {
            var (backend, board) = Found(Describing);
            Color(board, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.NotNull(PeripheralLightMaps.For(board.OutputPath));
            backend.Stop();
            Assert.Null(PeripheralLightMaps.For(board.OutputPath));
        }

        /// <summary>A worker a stop gave up on leaves the map of its
        /// successor's unit where it is, and sends that unit nothing.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_LeavesItsSuccessorsMapUp_AndHandsNothingBack()
        {
            var board = Board();
            Describing(board);
            var backend = Backend(predecessorJoinMs: 1, stopJoinMs: 1);
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Link(board);
            Color(board, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Advance(33);
            var release = HoldInTheNextReport(board, out var old);
            var oldHandle = board.Opened.Last();
            try
            {
                backend.Dispose();
                // The old worker is inside its report already. The new one
                // goes through, on a backend of its own, as each engine start
                // has it.
                board.OnCall = null;
                var next = Backend(predecessorJoinMs: 1, stopJoinMs: 1);
                next.Start();
                Walk(next, board);
                Passes(next);
                Passes(next);
                Advance(20);
                Passes(next);
                Assert.NotNull(PeripheralLightMaps.For(board.OutputPath));
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(old.Join(30000), "the old worker came out and ended");
            Assert.DoesNotContain(oldHandle.Calls, call => call.Kind == "set" && call.Bytes.SequenceEqual(new byte[] { 0x06, 0x01 }));
            Assert.Empty(_oneShots);
            Assert.NotNull(PeripheralLightMaps.For(board.OutputPath));
        }

        /// <summary>A unit with a published map falls asleep, wakes, and its
        /// lamps can no longer be read. It is painted one color from then on,
        /// so the Key Colors card stops offering its keys.</summary>
        [Fact]
        public void AUnitThatGoesBlindAfterAWake_LosesItsPublishedMap()
        {
            var (backend, board) = Found(Describing);
            Color(board, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.NotNull(PeripheralLightMaps.For(board.OutputPath));

            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            board.FailWrites = false;
            board.FailLampIndex = 0;
            board.FailLampReads = LampArrayBackend.ReadAttempts;

            string line = $"PERIPHERAL LampArray '{board.Name}' lamps could not be read (lamp 0 did not answer)";
            int before = LogLines(line);
            for (int i = 0; i < 40 && LogLines(line) == before; i++)
            {
                Advance(5000);
                Passes(backend);
            }
            Assert.Equal(before + 1, LogLines(line));
            Assert.Null(PeripheralLightMaps.For(board.OutputPath));
        }

        /// <summary>The crash path hands an owed unit back too: it is on
        /// the record, and whichever of the worker and the crash path gets
        /// to it sends the unit's own hand-back through a bounded write.</summary>
        [Fact]
        public void TheCrashPath_HandsBackAUnitThatIsOwedItsHandBack()
        {
            var (backend, board) = Lit();
            board.FailWrites = true;
            Color(board, 0x445566);
            Advance(33);
            Passes(backend);
            Assert.True(Unit(backend, board).Asleep);
            board.Take();

            backend.ReleaseClaimedNow(Tick() + PeripheralOutputHost.PanicBudgetMs);
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.NotEmpty(_oneShots);
            Assert.All(_oneShots, shot => Assert.Equal((BoardPath, "06 01", LampArrayBackend.OneShotMs), shot));
            Assert.Empty(board.Take());
            Assert.Empty(backend.ClaimedPaths);
        }

        /// <summary>At stop a device with no control report has its lamps
        /// turned off after what is left of the interval since its last
        /// color, which the worker waits out, the interval at most.</summary>
        [Fact]
        public void AtStop_ADeviceWithNoControlReport_IsTurnedOffAfterWhatIsLeftOfItsInterval()
        {
            var (backend, board) = Lit(setUp: b => b.Layout = b.Layout with { HasControl = false, ControlId = 0, ControlLink = 0 });
            Advance(5);
            backend.Stop();
            Assert.Equal(new[] { 28 }, _pauses);
            Assert.Equal(new[] { "set 05 01 00 00 03 00 00 00 00 00" }, board.Take());

            // With the interval already over there is nothing to wait for.
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Passes(backend);
            Advance(20);
            Passes(backend);
            board.Take();
            Advance(33);
            backend.Stop();
            Assert.Equal(new[] { 28 }, _pauses);
            Assert.Equal(new[] { "set 05 01 00 00 03 00 00 00 00 00" }, board.Take());
        }

        // ── The crash path (spec 11.2 steps 12 and 13) ──

        /// <summary>The crash path stops the worker before anything else,
        /// so a worker a crash dialog keeps alive never claims the device
        /// again. A worker that stops in time hands back on its own
        /// channel, and nothing is left for a write through a handle of
        /// its own.</summary>
        [Fact]
        public void TheCrashPath_StopsTheWorker_WhichHandsBackOnItsOwnChannel()
        {
            var (backend, board) = Lit();
            // The first step, which the host takes on every backend before
            // it waits for any: the worker stops, and its finally hands
            // back.
            backend.SignalStop();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.Equal(new[] { "set 06 01" }, board.Take());
            Assert.Empty(backend.ClaimedPaths);
            backend.ReleaseClaimedNow(Tick() + PeripheralOutputHost.PanicBudgetMs);
            Assert.Empty(_oneShots);

            // For good: no claim follows, and a second call sends nothing.
            Color(board, 0x445566);
            Advance(3 * LampArrayBackend.ReassertMs);
            backend.ReleaseClaimedNow(Tick() + PeripheralOutputHost.PanicBudgetMs);
            Assert.Empty(board.Take());
            Assert.Empty(_oneShots);
        }

        /// <summary>Holds the worker inside its next report until the test
        /// lets it go, and returns once it is there.</summary>
        private static ManualResetEventSlim HoldInTheNextReport(LampBoard board) => HoldInTheNextReport(board, out _);

        /// <summary>The same, and names the thread that is held, for a
        /// test that waits for that worker to end.</summary>
        private static ManualResetEventSlim HoldInTheNextReport(LampBoard board, out Thread worker)
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            Thread held = null;
            board.OnCall = () =>
            {
                Volatile.Write(ref held, Thread.CurrentThread);
                entered.Set();
                release.Wait(30000);
            };
            Color(board, 0x445566);
            Assert.True(entered.Wait(30000), "the worker reached its next report");
            worker = Volatile.Read(ref held);
            return release;
        }

        /// <summary>A worker that is stuck inside a report cannot hand
        /// back. Each unit on record then goes back through a write that
        /// opens a handle of its own, bounded by what is left of the
        /// deadline and by 200 ms.</summary>
        [Fact]
        public void TheCrashPath_HandsBackThroughAHandleOfItsOwn_WhenTheWorkerIsStuck()
        {
            var (backend, board) = Lit();
            Advance(33);
            var release = HoldInTheNextReport(board);
            try
            {
                backend.ReleaseClaimedNow(Tick() + 100);
                // The worker is waited for no longer than the deadline leaves.
                Assert.Equal(new[] { 100 }, _joins);
                Assert.Equal(new[] { (BoardPath, "06 01", 100) }, _oneShots);
                Assert.Empty(backend.ClaimedPaths);
                // Once is enough.
                backend.ReleaseClaimedNow(Tick() + 100);
                Assert.Single(_oneShots);
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            // It claimed nothing more on its way out.
            Assert.DoesNotContain(board.Take(), text => text == "set 06 00");
        }

        /// <summary>A unit Windows lights at that moment is skipped, as the
        /// worker skips it, and nothing is written past the deadline.</summary>
        [Theory]
        [InlineData(true, 100)]
        [InlineData(false, 0)]
        [InlineData(false, -5)]
        public void TheCrashPath_SkipsAUnitWindowsLights_AndWritesNothingPastTheDeadline(bool windowsLit, int budgetMs)
        {
            var (backend, board) = Lit();
            Advance(33);
            var release = HoldInTheNextReport(board);
            try
            {
                _windowsLit[BoardPath] = windowsLit;
                backend.ReleaseClaimedNow(Tick() + budgetMs);
                Assert.Empty(_oneShots);
                // With nothing left of the deadline the worker is not
                // waited for at all.
                Assert.Equal(budgetMs > 0 ? new[] { budgetMs } : Array.Empty<int>(), _joins);
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
        }

        /// <summary>The record carries the report that hands each unit
        /// back: AutonomousMode on, or every lamp off for a device with no
        /// control report. The bound on each write is 200 ms where the
        /// deadline leaves more.</summary>
        [Fact]
        public void TheCrashPathsReport_IsTheUnitsOwnHandBack_BoundedAtTwoHundredMilliseconds()
        {
            var (backend, board) = Lit(setUp: b => b.Layout = b.Layout with { HasControl = false, ControlId = 0, ControlLink = 0 });
            Advance(33);
            var release = HoldInTheNextReport(board);
            try
            {
                backend.ReleaseClaimedNow(Tick() + PeripheralOutputHost.PanicBudgetMs);
                // The deadline leaves more than the worker may be waited for.
                Assert.True(PeripheralOutputHost.PanicBudgetMs > LampArrayBackend.PanicJoinMs);
                Assert.Equal(new[] { LampArrayBackend.PanicJoinMs }, _joins);
                Assert.Equal(new[] { (BoardPath, "05 01 00 00 03 00 00 00 00 00", LampArrayBackend.OneShotMs) }, _oneShots);
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.Equal(200, LampArrayBackend.OneShotMs);
            Assert.Equal(250, LampArrayBackend.PanicJoinMs);
        }

        /// <summary>The first step of the crash path alone: the worker
        /// claims nothing more.</summary>
        [Fact]
        public void AfterTheSignalToStop_NothingMoreIsClaimed()
        {
            var (backend, board) = Found();
            backend.SignalStop();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Color(board, 0x112233);
            Advance(10000);
            Assert.Empty(board.Take());
            Assert.Equal(0, _claimOpens);
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
        }

        /// <summary>The word to stop holds wherever in a pass it reaches
        /// the worker: a unit it has not opened yet stays closed, though
        /// a color waits for it.</summary>
        [Fact]
        public void AWorkerToldToStopInTheMiddleOfAPass_OpensNoOtherUnit()
        {
            var (backend, board) = Lit();
            var other = Board(OtherPath, pid: 0x0002);
            Walk(backend, board, other);
            Passes(backend);
            Assert.Equal(new[] { "get 01" }, other.Take());
            Link(board, other);
            Advance(33);
            var release = HoldInTheNextReport(board);
            try
            {
                // Wanted from here on, in the pass the worker is held in.
                Color(other, 0x112233);
                backend.SignalStop();
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
            Assert.Empty(other.Take());
            Assert.Equal(1, _claimOpens);
            Assert.Contains("set 06 01", board.Take());
        }

        /// <summary>A worker told to stop while it reads a unit's lamps
        /// reads no lamp more and claims nothing. The read is no claim, so
        /// nothing is handed back either.</summary>
        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        public void AWorkerToldToStopInsideALampRead_ReadsNoMore_AndClaimsNothing(int heldAt)
        {
            var (backend, board) = Found();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            board.OnCall = () =>
            {
                // The call being made is not on record yet.
                if (board.Traffic().Count(call => call.Text == "get 03") != heldAt - 1) return;
                entered.Set();
                release.Wait(30000);
            };
            Color(board, 0x112233);
            Assert.True(entered.Wait(30000), "the worker reached that lamp read");
            backend.SignalStop();
            board.OnCall = null;
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");

            Assert.Equal(new[] { "set 02 00 00" }.Concat(LampReads(heldAt)), board.Take());
            Assert.Empty(backend.ClaimedPaths);
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
        }

        /// <summary>A worker told to stop while it describes one unit asks
        /// nothing more: not that device's name, and nothing of the next
        /// collection.</summary>
        [Fact]
        public void AWorkerToldToStopWhileItDescribesAUnit_AsksNothingMore()
        {
            var first = Board();
            var second = Board(OtherPath, pid: 0x0002);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            first.OnCall = () =>
            {
                entered.Set();
                release.Wait(30000);
            };
            var backend = Backend();
            backend.Start();
            Walk(backend, first, second);
            Assert.True(entered.Wait(30000), "the worker reached the first unit's attributes read");
            backend.SignalStop();
            first.OnCall = null;
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");

            Assert.Equal(new[] { "get 01" }, first.Take());
            Assert.Equal(0, first.NameReads);
            Assert.Empty(second.Traffic());
            Assert.Equal(0, second.DescriptorReads);
            Assert.Equal(1, _queryOpens);
            Assert.All(first.Opened, handle => Assert.True(handle.Disposed));
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
        }

        /// <summary>A worker told to stop while its last try on the handle
        /// with no access right fails opens no second handle.</summary>
        [Fact]
        public void AWorkerToldToStopDuringTheLastReadOnTheFirstHandle_OpensNoSecondOne()
        {
            var board = Board();
            board.FailAttributeReads = LampArrayBackend.ReadAttempts;
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            board.OnCall = () =>
            {
                // The call being made is not on record yet: the eighth is
                // held.
                if (board.Traffic().Length != LampArrayBackend.ReadAttempts - 1) return;
                entered.Set();
                release.Wait(30000);
            };
            var backend = Backend();
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            for (int attempt = 1; attempt < LampArrayBackend.ReadAttempts - 1; attempt++)
            {
                Advance(LampArrayBackend.ReadRetryStepMs * attempt);
                Passes(backend);
            }
            Advance(LampArrayBackend.ReadRetryStepMs * (LampArrayBackend.ReadAttempts - 1));
            Assert.True(entered.Wait(30000), "the worker reached its eighth read");
            backend.SignalStop();
            board.OnCall = null;
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");

            Assert.Equal((1, 0), (_queryOpens, _claimOpens));
            Assert.Equal(LampArrayBackend.ReadAttempts, board.Traffic().Length);
            Assert.All(board.Traffic(), call => Assert.True(call.QueryOnly));
            Assert.All(board.Opened, handle => Assert.True(handle.Disposed));
        }

        /// <summary>A worker still inside a report holds its collections,
        /// and hands its units back when it comes out. A new worker waits
        /// for it before it opens anything. The walk the host hands over
        /// meanwhile is still taken once the wait is over: the new worker
        /// counts walks from its start, whenever its thread first runs.</summary>
        [Fact]
        public void ANewWorkerWaitsForTheOneBeforeIt_AndStillTakesTheWalkHandedOverMeanwhile()
        {
            var (first, board) = Lit();
            Advance(33);
            var release = HoldInTheNextReport(board);
            var other = Board(OtherPath, pid: 0x0002);
            var second = Backend(predecessorJoinMs: 60000);
            try
            {
                second.Start();
                Walk(second, other);
                // Held behind the first worker: no pass, nothing read.
                Assert.True(second.WorkerAlive);
                Assert.Equal(0, second.PassCount);
                Assert.Equal(0, other.DescriptorReads);
                Assert.Same(DirectBackendSnapshot.NotScanned, second.Snapshot);
            }
            finally
            {
                first.SignalStop();
                board.OnCall = null;
                release.Set();
            }
            Assert.True(SpinWait.SpinUntil(() => !first.WorkerAlive, 30000), "the first worker stopped");
            Assert.Contains("set 06 01", board.Take());

            Passes(second);
            Assert.Equal(other.Path, Assert.Single(second.Snapshot.Units).Key);
            Assert.True(second.Snapshot.Scanned);
            Assert.Equal(new[] { "get 01" }, other.Take());
        }

        /// <summary>A stop that gave up waiting for a worker inside a
        /// report leaves that worker to end on its own. The unit it holds
        /// stays on the crash path's record until then. When it does come
        /// out it hands its unit back, and publishes nothing over the run
        /// that was started since.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_HandsBackWhenItComesOut_AndPublishesNothingOverItsSuccessor()
        {
            var board = Board();
            var backend = Backend(predecessorJoinMs: 1, stopJoinMs: 1);
            backend.Start();
            Walk(backend, board);
            Passes(backend);
            Link(board);
            Color(board, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.Equal(Paint(0x112233), board.Take().Last());
            Advance(33);
            var release = HoldInTheNextReport(board, out var old);
            var held = board.Opened.Last();
            DirectBackendSnapshot published;
            try
            {
                backend.Stop();
                Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
                Assert.False(held.Disposed);
                Assert.Equal(new[] { BoardPath }, backend.ClaimedPaths);

                // A new run, which does not wait for the old worker past
                // its bound.
                var other = Board(OtherPath, pid: 0x0002);
                backend.Start();
                Walk(backend, other);
                Passes(backend);
                published = backend.Snapshot;
                Assert.Equal(other.Path, Assert.Single(published.Units).Key);
            }
            finally
            {
                board.OnCall = null;
                release.Set();
            }
            Assert.True(old.Join(30000), "the old worker came out and ended");
            Assert.True(held.Disposed);
            Assert.Contains("set 06 01", board.Take());
            Passes(backend);
            Assert.Same(published, backend.Snapshot);
            Assert.True(backend.WorkerAlive);
        }

        /// <summary>What a worker a stop gave up on still finds out on its
        /// way out stays its own. A write of its last pass that fails
        /// lists nothing, and neither that pass nor its end puts anything
        /// on the crash path's record or on the list over what its
        /// successor holds.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_ListsAndRecordsNothingOverItsSuccessor_WhenItsLastPassFails()
        {
            const string thirdPath = @"\\?\hid#vid_1234&pid_0003&mi_01#bench-c#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var first = Board();
            var second = Board(OtherPath, pid: 0x0002);
            var third = Board(thirdPath, pid: 0x0003);
            var backend = Backend(predecessorJoinMs: 1, stopJoinMs: 1);
            backend.Start();
            Walk(backend, first, second);
            Passes(backend);
            Link(first, second);
            Color(first, 0x112233);
            Color(second, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.Equal(2, backend.ClaimedPaths.Count);
            first.Take();
            second.Take();

            // The old worker is held inside the first unit's color, with
            // the second unit still to come in the same pass.
            Advance(33);
            var release = HoldInTheNextReport(first, out var old);
            try
            {
                backend.Stop();
                // What that pass has left to do will fail: the second
                // unit's row is gone by then, and its hand-back is not
                // taken.
                second.FailWrites = true;

                backend.Start();
                Walk(backend, third);
                Passes(backend);
                Link(third);
                Color(third, 0x445566);
                Passes(backend);
                Assert.Contains(thirdPath, backend.ClaimedPaths);
                Assert.Equal(new[] { thirdPath }, backend.Snapshot.Units.Select(unit => unit.Key));
            }
            finally
            {
                first.OnCall = null;
                release.Set();
            }
            Assert.True(old.Join(30000), "the old worker came out and ended");

            // It handed the unit it still held back on its way out, and
            // the list and the record are its successor's.
            Assert.Contains("set 06 01", first.Take());
            Assert.Empty(second.Take());
            Assert.All(first.Opened.Concat(second.Opened), handle => Assert.True(handle.Disposed));
            Passes(backend);
            Assert.Equal(new[] { thirdPath }, backend.Snapshot.Units.Select(unit => unit.Key));
            Assert.Equal(new[] { thirdPath }, backend.ClaimedPaths);
            Assert.True(backend.WorkerAlive);
        }

        // ── Where the engine makes it ──

        /// <summary>The engine's host starts the worker from the one list
        /// every direct backend is made in, under the LampArray heading
        /// and first, since its hand-back is one short report.</summary>
        [Fact]
        public void TheEnginesHost_StartsTheLampArrayWorker_FirstOfTheDirectBackends()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "DirectBackends.cs")
                .Replace("\r\n", "\n");
            Assert.Contains("            // HID LampArray. First, since its hand-back is one short report\n"
                            + "            // that returns a device to its own effect.\n"
                            + "            Add(backends, OutputFamily.LampArrayUnit, () => new LampArrayBackend());\n", source);

            var made = DirectBackends.Create();
            try
            {
                Assert.IsType<LampArrayBackend>(made[0]);
                Assert.Equal(OutputFamily.LampArrayUnit, made[0].Family);
                Assert.Same(DirectBackendSnapshot.NotScanned, made[0].Snapshot);
            }
            finally
            {
                foreach (var backend in made) backend.Dispose();
            }
        }


        // ── Lamp by lamp (#557) ──

        /// <summary>A board whose lamps describe themselves and take multi
        /// updates: Esc, A, a control lamp bound to no key, and an edge lamp.</summary>
        private static void Describing(LampBoard board)
        {
            board.Layout = board.Layout with { MultiId = 4, MultiLink = 4, InputBindingBits = 8, DescribesLamps = true };
            board.Purposes = new uint[] { 1, 1, 1, 2 };
            board.Bindings = new byte[] { 0x29, 0x04, 0x00, 0x00 };
        }

        private static KeyFrame Keys(int a, int edge = -1)
            => new(new Dictionary<ushort, int> { [0x04] = a },
                edge < 0 ? null : new Dictionary<string, int> { ["edge"] = edge }, null, null);

        private static void Color(LampBoard board, int rgb, KeyFrame keys)
            => PeripheralOutputs.SetLighting(board.Row, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb,
                keys: keys);

        private static string Multi(params (ushort Lamp, int Rgb)[] lamps)
        {
            var values = lamps.Select(l => new LampValues(l.Lamp, (byte)(l.Rgb >> 16), (byte)(l.Rgb >> 8), (byte)l.Rgb,
                (l.Rgb & 0xFFFFFF) != 0 ? (byte)1 : (byte)0)).ToArray();
            var report = LampArrayProtocol.MultiUpdate(4, 8, complete: true, values);
            return "set " + string.Join(" ", report.Select(b => b.ToString("X2")));
        }

        /// <summary>A key frame paints the board lamp by lamp, one multi
        /// update for its four lamps: each key in its own color, a key no
        /// source named and every key the frame leaves out in the claim's
        /// color, the edge lamp in its group's. The board's map is
        /// published for the Lighting tab.</summary>
        [Fact]
        public void AKeyFramePaintsLampByLamp_ByMultiUpdate_AndTheMapIsPublished()
        {
            var (backend, board) = Found(Describing);
            Color(board, 0x112233, Keys(0xFF0000, edge: 0x0000FF));
            Passes(backend);
            Advance(20);
            Passes(backend);
            var calls = board.Take();
            Assert.Equal(Multi((0, 0x112233), (1, 0xFF0000), (2, 0x112233), (3, 0x0000FF)), calls.Last());

            var map = PeripheralLightMaps.For(board.OutputPath);
            Assert.NotNull(map);
            Assert.Equal(new[] { LightKind.Key, LightKind.Key, LightKind.Key, LightKind.Edge }, map.Lights.Select(l => l.Kind));
            Assert.Equal(new ushort[] { 0x29, 0x04, 0, 0 }, map.Lights.Select(l => l.Usage));
            Assert.Equal(1, map.UnnamedKeyCount);
        }

        /// <summary>A change to one key sends that lamp alone, once the
        /// device's interval is over. A frame that goes away paints the
        /// board one color again, in the range report one color takes.</summary>
        [Fact]
        public void AChangedKeySendsOnlyItsLamp_AndNoFrameIsOneColorAgain()
        {
            var (backend, board) = Found(Describing);
            Color(board, 0x112233, Keys(0xFF0000));
            Passes(backend);
            Advance(20);
            Passes(backend);
            board.Take();

            Color(board, 0x112233, Keys(0x00FF00));
            Advance(40);
            Passes(backend);
            Assert.Equal(new[] { Multi((1, 0x00FF00)) }, board.Take());

            Color(board, 0x112233);
            Advance(40);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());
        }

        /// <summary>A name learned with Map Key Lights for the board's
        /// model reaches its map at the next pass, over the unnamed key the
        /// lamp descriptions gave, and leaves when it is forgotten.</summary>
        [Fact]
        public void ALearnedNameReachesTheMap_AndForgettingItTakesItBack()
        {
            var (backend, board) = Found(b =>
            {
                Describing(b);
                b.Bindings = new byte[] { 0x29, 0x00, 0x00, 0x00 };
            });
            Color(board, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            var map = PeripheralLightMaps.For(board.OutputPath);
            Assert.Equal("lamparray:1234:0001:1:4", map.Product);
            Assert.Equal(2, map.UnnamedKeyCount);

            LearnedKeyMaps.Set(map.Product, 1, 0x04);
            Passes(backend);
            map = PeripheralLightMaps.For(board.OutputPath);
            Assert.Equal(0x04, map.Lights[1].Usage);
            Assert.Equal(LightKeySource.Learned, map.Lights[1].KeySource);
            Assert.Equal(1, map.UnnamedKeyCount);
            Assert.Equal(new[] { 1 }, map.IndexesOf(0x04));

            LearnedKeyMaps.Set(map.Product, 1, 0);
            Passes(backend);
            map = PeripheralLightMaps.For(board.OutputPath);
            Assert.Equal(0, map.Lights[1].Usage);
            Assert.Equal(2, map.UnnamedKeyCount);
        }

        /// <summary>Map Key Lights' probe lights one lamp white and every
        /// other off, ahead of the claim, lamp by lamp. When it ends the
        /// claim's color comes back.</summary>
        [Fact]
        public void TheProbeLightsOneLamp_AheadOfTheClaim_AndThenTheClaimReturns()
        {
            var (backend, board) = Found(Describing);
            Color(board, 0x112233);
            Passes(backend);
            Advance(20);
            Passes(backend);
            board.Take();

            PeripheralOutputs.SetProbe(board.OutputPath,
                new KeyFrame(null, null, new Dictionary<int, int> { [2] = 0xFFFFFF }, 0));
            Advance(40);
            Passes(backend);
            Assert.Equal(new[] { Multi((0, 0), (1, 0), (2, 0xFFFFFF), (3, 0)) }, board.Take());

            PeripheralOutputs.SetProbe(board.OutputPath, null);
            Advance(40);
            Passes(backend);
            Assert.Equal(new[] { Paint(0x112233) }, board.Take());
        }

        /// <summary>A board whose lamp response holds its descriptions
        /// elsewhere has no map, so a key frame changes nothing on it: it
        /// is painted one color.</summary>
        [Fact]
        public void ABoardThatDoesNotDescribeItsLamps_IsPaintedOneColor_WhateverTheFrame()
        {
            var (backend, board) = Found(b =>
            {
                Describing(b);
                b.Layout = b.Layout with { DescribesLamps = false };
            });
            Color(board, 0x112233, Keys(0xFF0000));
            Passes(backend);
            Advance(20);
            Passes(backend);
            Assert.Equal(Paint(0x112233), board.Take().Last());
            Assert.Null(PeripheralLightMaps.For(board.OutputPath));
        }
    }

    internal static class LampArrayBackendTestExtensions
    {
        /// <summary>A lighting family leaves the silence wait to the
        /// interface, which does nothing.</summary>
        public static void WaitForSilenceIsTheInterfacesOwn(this LampArrayBackend backend)
        {
            ((IDirectUnitBackend)backend).WaitForSilence(250);
            Assert.Null(typeof(LampArrayBackend).GetMethod(nameof(IDirectUnitBackend.WaitForSilence),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        }
    }

    // ─────────────────────────────────────────────
    //  The row of a lit unit and the vendor's program: shared state
    // ─────────────────────────────────────────────

    /// <summary>The peripheral host's side of the orphan rule (#494),
    /// against a scripted backend and presence and a clock the test sets:
    /// the row of a lit unit retires when its vendor's program starts and
    /// opens again after the hold when the program stops.</summary>
    [Collection("PeripheralOutputStatics")]
    public class LampArrayUnitRowTests : IDisposable
    {
        private static readonly Guid Box = new("99999999-2222-3333-4444-555555555555");

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private PeripheralPresence _presence = new(false, false, true, false, false);
        private long _tick = 1_000_000;

        public LampArrayUnitRowTests()
        {
            PeripheralOutputs.ResetForTest();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private static void Passes(PeripheralOutputHost host, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = host.PassCount;
                host.Nudge();
                Assert.True(SpinWait.SpinUntil(() => host.PassCount > before, 30000), "a link pass ran");
            }
        }

        [Fact]
        public void TheRowOfALitUnit_RetiresWhenItsVendorsProgramStarts_AndOpensAgainAfterTheHoldWhenItStops()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", Box, PeripheralLinker.RazerVid, 0x0527)
            {
                Name = "Razer Headset", Kind = 0x0B,
            };
            var strip = new DirectUnit(OutputFamily.LampArrayUnit, "strip", Box, 0x0B05, 0x1A2B) { Name = "Light Strip", Kind = 4 };
            Guid headsetId = PeripheralUnitRow.IdentityFor(headset.Family, headset.Key);
            Guid stripId = PeripheralUnitRow.IdentityFor(strip.Family, strip.Key);
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            using var host = new PeripheralOutputHost(
                new HidppBackend(() => Array.Empty<VendorHidCollection>(), (c, s) => null, _ => Guid.Empty, 1, 5),
                new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, 20),
                () => new SensaHapticsService(30000, 16, 1000, () => 0f) { PlatformCanLoadEngine = () => false },
                containerOf: _ => Guid.Empty,
                readPresence: () => Volatile.Read(ref _presence),
                direct: new IDirectUnitBackend[] { lamp },
                enumerate: () => Array.Empty<HidCollectionInfo>(),
                ticks: Tick);
            var manager = new InputManager();
            var phase = typeof(InputManager).GetMethod("UpdatePeripheralRows", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(phase);
            bool Update() => (bool)phase.Invoke(manager, null);
            UserDevice Record(Guid id)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                    return SettingsManager.UserDevices.Items.SingleOrDefault(d => d.InstanceGuid == id);
            }

            try
            {
                host.Start();
                lamp.Found(headset, strip);
                Passes(host);
                Advance(PeripheralLinker.UnitRowHoldMs);
                Passes(host);
                Assert.Equal(new[] { headset, strip }, PeripheralOutputs.UnitRows);
                Update();
                var record = Record(headsetId);
                Assert.True(record.IsOnline);
                Assert.IsType<PeripheralUnitRow>(record.Device);
                Assert.True(Record(stripId).IsOnline);
                Passes(host);
                Assert.Equal(new[] { headset.Path }, PeripheralOutputs.Links.For(headsetId).Lighting);

                // Synapse starts. The host reads the running programs, and
                // the Razer unit is no orphan any more: its row retires.
                // The other vendor's unit keeps its row.
                Volatile.Write(ref _presence, _presence with { RazerSoftwareRunning = true });
                Advance(PeripheralOutputHost.PresenceMs);
                Passes(host);
                Assert.True(PeripheralOutputs.Presence.RazerSoftwareRunning);
                Assert.Equal(new[] { strip }, PeripheralOutputs.UnitRows);
                Assert.True(Update());
                Assert.False(record.IsOnline);
                Assert.Null(record.Device);
                Assert.True(Record(stripId).IsOnline);
                // A row that is offline is linked to nothing, so nothing
                // resolves on the unit's path and its worker hands it back.
                Passes(host);
                Assert.Null(PeripheralOutputs.Links.For(headsetId));
                Assert.False(PeripheralOutputs.Links.ByPath.ContainsKey(headset.Path));

                // For as long as it runs.
                Advance(3 * PeripheralLinker.UnitRowHoldMs);
                Passes(host);
                Assert.Equal(new[] { strip }, PeripheralOutputs.UnitRows);
                Assert.False(Update());

                // Synapse stops. The unit is an orphan again from that
                // pass, and gets its row back once the hold has passed.
                Volatile.Write(ref _presence, _presence with { RazerSoftwareRunning = false });
                Advance(PeripheralOutputHost.PresenceMs);
                Passes(host);
                Assert.False(PeripheralOutputs.Presence.RazerSoftwareRunning);
                Assert.Equal(new[] { strip }, PeripheralOutputs.UnitRows);
                Advance(PeripheralLinker.UnitRowHoldMs - 1);
                Passes(host);
                Assert.Equal(new[] { strip }, PeripheralOutputs.UnitRows);
                Assert.False(Update());
                Advance(1);
                Passes(host);
                Assert.Equal(new[] { headset, strip }, PeripheralOutputs.UnitRows);
                Assert.True(Update());
                Assert.Same(record, Record(headsetId));
                Assert.True(record.IsOnline);
                Passes(host);
                Assert.Equal(new[] { headset.Path }, PeripheralOutputs.Links.For(headsetId).Lighting);
            }
            finally
            {
                host.Stop();
                manager.ShutdownPeripheralRows();
            }
        }

        /// <summary>A lit unit row loses its path in the pass that sees its
        /// vendor's program start, with the row still online: it retires
        /// only at the input manager's next device scan. That pass comes
        /// at once, since a claim on such a row brings the read of the
        /// running programs on every pass, as a claim on the vendor's own
        /// mouse does. Until then the worker would go on painting, and
        /// sending its claim again, on a device the program is taking.</summary>
        [Fact]
        public void ALitUnitRowsPath_IsWithdrawnInThePassThatSeesItsVendorsProgramStart()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", Box, PeripheralLinker.RazerVid, 0x0527)
            {
                Name = "Razer Headset", Kind = 0x0B,
            };
            var strip = new DirectUnit(OutputFamily.LampArrayUnit, "strip", Box, 0x0B05, 0x1A2B) { Name = "Light Strip", Kind = 4 };
            Guid headsetId = PeripheralUnitRow.IdentityFor(headset.Family, headset.Key);
            Guid stripId = PeripheralUnitRow.IdentityFor(strip.Family, strip.Key);
            int runningReads = 0;
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            using var host = new PeripheralOutputHost(
                new HidppBackend(() => Array.Empty<VendorHidCollection>(), (c, s) => null, _ => Guid.Empty, 1, 5),
                new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, 20),
                () => new SensaHapticsService(30000, 16, 1000, () => 0f) { PlatformCanLoadEngine = () => false },
                containerOf: _ => Guid.Empty,
                readPresence: () => Volatile.Read(ref _presence),
                direct: new IDirectUnitBackend[] { lamp },
                enumerate: () => Array.Empty<HidCollectionInfo>(),
                readRunning: current =>
                {
                    Interlocked.Increment(ref runningReads);
                    return PeripheralPresence.CopyRunning(current, Volatile.Read(ref _presence));
                },
                ticks: Tick);
            host.SlotHasController = slot => slot == 2;
            var manager = new InputManager();
            var phase = typeof(InputManager).GetMethod("UpdatePeripheralRows", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(phase);
            UserDevice Record(Guid id)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                    return SettingsManager.UserDevices.Items.SingleOrDefault(d => d.InstanceGuid == id);
            }

            try
            {
                host.Start();
                lamp.Found(headset, strip);
                Passes(host);
                Advance(PeripheralLinker.UnitRowHoldMs);
                Passes(host);
                phase.Invoke(manager, null);
                Passes(host);
                Assert.Equal(new[] { headset.Path }, PeripheralOutputs.Links.For(headsetId).Lighting);
                Assert.True(PeripheralOutputs.Links.For(headsetId).HasVendorProgram);
                Assert.False(PeripheralOutputs.Links.For(stripId).HasVendorProgram);

                // Both rows are assigned. The strip alone, of a vendor with
                // no program to yield to, brings no read of who runs.
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = headsetId, MapTo = 2 });
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = stripId, MapTo = 2 });
                }
                PeripheralOutputs.SetLighting(stripId, slot: 2, player: 1, 1, 2, 3);
                Passes(host, 4);
                Assert.True(PeripheralOutputs.TryResolveColor(strip.Path, out _));
                Assert.Equal(0, Volatile.Read(ref runningReads));

                // The Razer headset is lit: every pass reads who runs.
                PeripheralOutputs.SetLighting(headsetId, slot: 2, player: 1, 1, 2, 3);
                Passes(host);
                Assert.True(PeripheralOutputs.TryResolveColor(headset.Path, out _));
                Assert.True(Volatile.Read(ref runningReads) >= 1, "a claim on the Razer unit's row is watched for");

                // Synapse starts. No presence interval passes, and the
                // row's retirement has not come.
                Volatile.Write(ref _presence, _presence with { RazerSoftwareRunning = true });
                Passes(host);
                Assert.True(PeripheralOutputs.Presence.RazerSoftwareRunning);
                Assert.Null(PeripheralOutputs.Links.For(headsetId));
                Assert.False(PeripheralOutputs.Links.ByPath.ContainsKey(headset.Path));
                Assert.True(Record(headsetId).IsOnline);
                Assert.IsType<PeripheralUnitRow>(Record(headsetId).Device);
                // The other vendor's row keeps its path.
                Assert.Equal(new[] { strip.Path }, PeripheralOutputs.Links.For(stripId).Lighting);
            }
            finally
            {
                host.Stop();
                manager.ShutdownPeripheralRows();
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Live hardware, opt-in and read-only
    // ─────────────────────────────────────────────

    /// <summary>The scan against this PC's own LampArray collections,
    /// when asked for (#494). Read-only: the descriptor through Windows'
    /// parser, one read of each attributes report, the container and the
    /// lighting switches. No report is written to any device, and the
    /// worker is not started. The scope that lifts the test host's refusal
    /// lifts it for every class, so this one runs with no other beside it.</summary>
    [Collection("PeripheralOutputStatics")]
    public class LampArrayLiveTests
    {
        private readonly ITestOutputHelper _output;

        public LampArrayLiveTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Runs only with PADFORGE_LIVE_LAMPARRAY=1. For each
        /// collection on usage page 0x59, usage 0x01: the descriptor's
        /// reports and the parser cross-check, with no device I/O, then
        /// one read of the attributes report, tried once on a handle
        /// opened with no access right and, only where that does not
        /// carry it, once on the handle a claim would use. Then what the
        /// parser makes of the same bytes, the product string, the
        /// container, and whether Windows Dynamic Lighting lights it.</summary>
        [Fact]
        public void Live_DescribesTheRealLampArrayCollections()
        {
            if (Environment.GetEnvironmentVariable("PADFORGE_LIVE_LAMPARRAY") != "1") return;
            // The one test of this file that means to reach a real device,
            // so the test host's refusal is lifted for it, past its gate.
            using var real = RealHidAccess.AllowForTest();
            var all = HidCollectionRuntime.Enumerate();
            Assert.NotNull(all);
            var lamps = all.Where(c => c.UsagePage == LampArrayProtocol.UsagePage && c.Usage == LampArrayProtocol.LampArrayUsage).ToList();
            _output.WriteLine($"{all.Count} HID collection(s), {lamps.Count} LampArray collection(s)");

            var descriptors = new LampArrayDescriptors();
            var lighting = new WindowsLightingState();
            string sid = WindowsLightingState.ShellUserSid();
            string own = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            _output.WriteLine(sid == null
                ? "no shell window: the lighting switches are read from this process's own hive"
                : "the lighting switches are read from the shell user's hive, "
                  + (sid == own ? "which is this process's own" : "another account than this process runs as"));
            _output.WriteLine($"global {WindowsLightingState.EnabledValue} = {Show(WindowsLightingState.ReadShellUser(null))}");

            foreach (var info in lamps)
            {
                _output.WriteLine($"--- {info.VendorId:X4}:{info.ProductId:X4} interface {info.InterfaceNumber}"
                                  + $" feature length {info.FeatureReportLength}");
                var layout = descriptors.Read(info.Path, out string rejection);
                if (layout == null)
                {
                    _output.WriteLine("not driven: " + (rejection ?? "its descriptor could not be read"));
                    continue;
                }
                _output.WriteLine($"report IDs: attributes {layout.AttributesId:X2}, request {layout.RequestId:X2},"
                                  + $" response {layout.ResponseId:X2}, range update {layout.RangeId:X2},"
                                  + $" control {(layout.HasControl ? layout.ControlId.ToString("X2") : "none")},"
                                  + $" lamps to a multi update {layout.MultiUpdateSlots}");
                _output.WriteLine("parser cross-check of the range update, control, request and response layouts: passed");

                var report = new byte[layout.FeatureLength];
                bool answered;
                string carriedBy = "the handle with no access right";
                using (var query = HidReportChannel.OpenFeatureOnly(info.Path))
                    answered = query != null && query.GetFeature(layout.AttributesId, report);
                if (!answered)
                {
                    _output.WriteLine("the handle with no access right did not carry the attributes read");
                    using var claim = HidReportChannel.Open(info.Path);
                    answered = claim != null && claim.GetFeature(layout.AttributesId, report);
                    carriedBy = "the read and write handle" + (claim?.FeatureOnly == true ? " (which opened with no access right)" : "");
                }
                Assert.True(answered, "the attributes report was read");
                _output.WriteLine($"attributes report, read on {carriedBy}: {LampArrayProtocol.Hex(report, LampArrayProtocol.AttributesLength)}");
                Assert.True(LampArrayProtocol.TryParseAttributes(report, out var attributes));
                _output.WriteLine($"lamps {attributes.LampCount}, box {attributes.WidthUm} x {attributes.HeightUm} x {attributes.DepthUm} um,"
                                  + $" kind {attributes.Kind}, minimum update interval {attributes.MinUpdateIntervalUs} us"
                                  + $" (first color {LampArrayProtocol.SettleMs(attributes.MinUpdateIntervalUs)} ms after the claim,"
                                  + $" repaint every {LampArrayProtocol.PaintIntervalMs(attributes.MinUpdateIntervalUs)} ms at most)");
                _output.WriteLine($"the parser reads the same attributes: {descriptors.AttributesAgree(info.Path, layout, report, attributes)}");
                _output.WriteLine($"product string: '{descriptors.ProductName(info.Path)}'");

                Guid plain = AudioPassthroughService.DevicePathContainerId(info.Path);
                Guid container = LampArrayContainers.For(info.Path);
                _output.WriteLine($"container {container}"
                                  + (container == PeripheralLinker.LocalMachineContainer ? " (the PC's own)" : "")
                                  + (container == plain ? "" : $", taken from an ancestor where the collection reports {plain}"));
                var chain = LampArrayContainers.Ancestors(info.Path);
                _output.WriteLine($"ancestors: {chain?.Count ?? 0}, a Logitech slot among them: "
                                  + (chain?.Any(node => LampArrayContainers.IsLogitechLampArraySlot(node.InstanceId)) == true));

                string key = WindowsLightingState.DeviceKey(info.Path);
                _output.WriteLine($"device {WindowsLightingState.EnabledValue} = {Show(key == null ? null : WindowsLightingState.ReadShellUser(key))}");
                _output.WriteLine($"Windows Dynamic Lighting lights it: {lighting.Lights(info.Path)}");
            }
        }

        private static string Show(int? value) => value?.ToString() ?? "(not there)";
    }

    // ─────────────────────────────────────────────
    //  The test host and the PC it runs on
    // ─────────────────────────────────────────────

    /// <summary>
    /// The LampArray lane's own ways of reaching the PC (#494), and that
    /// the test host is kept off each. This PC's keyboard is a LampArray
    /// device, and a build with a guard taken out to see whether a test
    /// notices is the build that would reach it. Every report goes through
    /// the shared channel, which the test host refuses
    /// (<see cref="RealHidAccess"/>). The descriptor reads open a
    /// collection themselves and ask the same question first. The lighting
    /// switches are read and never written. The worker's tests hand it its
    /// walks. One test lifts the refusal to count the opens it lets
    /// through, so the class runs with no other beside it.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class LampArrayOffLimitsTests
    {
        private static readonly string[] LaneFiles = { "LampArrayBackend.cs", "LampArrayProtocol.cs", "WindowsLightingState.cs" };

        private static string Source(string file)
            => AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", file);

        /// <summary>A source file without its comments, so a rule about
        /// what the code calls is not tripped by what a comment names.</summary>
        private static string Code(string source) => Regex.Replace(source, @"//[^\r\n]*", "");

        private static int Count(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        /// <summary>The descriptor reads open nothing in the test host. The
        /// one open behind them is counted here, and no read reaches it
        /// while the refusal holds: each answers as for a collection that
        /// cannot be read now, and throws nothing. With the refusal lifted
        /// each read reaches the open once. The path has the shape of the
        /// bench keyboard's own collection under an instance no device
        /// has.</summary>
        [Fact]
        public void TheTestHostRefusesTheDescriptorReads()
        {
            const string keyboard = @"\\?\hid#vid_048d&pid_c193&mi_01#padforge-test#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var asked = new ConcurrentQueue<string>();
            var descriptors = new LampArrayDescriptors(path =>
            {
                asked.Enqueue(path);
                return null;
            });
            var layout = new LampArrayLayout(1, 2, 3, 5, 6, true, 51);
            void ReadAll(LampArrayDescriptors reads)
            {
                Assert.Null(reads.Read(keyboard, out string rejection));
                Assert.Null(rejection);
                Assert.Null(reads.AttributesAgree(keyboard, layout, new byte[51], default));
                Assert.Null(reads.ProductName(keyboard));
            }

            Assert.True(RealHidAccess.Refused);
            ReadAll(descriptors);
            Assert.Empty(asked);

            using (RealHidAccess.AllowForTest())
            {
                ReadAll(descriptors);
                Assert.Equal(new[] { keyboard, keyboard, keyboard }, asked);
            }
            ReadAll(descriptors);
            Assert.Equal(3, asked.Count);

            // No path opens nothing either way, and the app's own reads
            // are refused like the scripted ones.
            Assert.Null(descriptors.ProductName(null));
            Assert.Null(descriptors.ProductName(""));
            Assert.Equal(3, asked.Count);
            ReadAll(new LampArrayDescriptors());
        }

        /// <summary>The lane has one open of its own, and it asks before
        /// it opens, in the same method. Each descriptor read takes its
        /// handle from there and from nowhere else.</summary>
        [Fact]
        public void TheLanesOneOpen_AsksBeforeItOpens()
        {
            string source = Source("LampArrayBackend.cs");
            const string signature = "private SafeFileHandle OpenQueryOnly(string path)";
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature);
            Assert.Equal(1, Count(source, "CreateFile("));
            int open = source.IndexOf("CreateFile(", StringComparison.Ordinal);
            int asks = source.IndexOf("RealHidAccess.Refused", at, StringComparison.Ordinal);
            Assert.True(asks > at && asks < open, "the open asks first");
            string before = source.Substring(at + signature.Length, asks - at - signature.Length);
            Assert.DoesNotContain(" static ", before);
            Assert.DoesNotContain("public ", before);
            Assert.DoesNotContain("private ", before);

            Assert.Equal(3, Count(source, "using var handle = OpenQueryOnly(path);"));
            Assert.Equal(4, Count(Code(source), "OpenQueryOnly("));
            // The open itself is reached from that method alone.
            Assert.Equal(1, Count(Code(source), "_open(path)"));
            Assert.Contains("_open = open ?? OpenCollection;", source);
            Assert.Equal(2, Count(Code(source), "OpenCollection"));
            Assert.Equal(0, Count(Code(Source("LampArrayProtocol.cs")), "CreateFile"));
            Assert.Equal(0, Count(Code(Source("WindowsLightingState.cs")), "CreateFile"));
        }

        /// <summary>Nothing in the lane sends or reads a report by itself,
        /// walks the HID collections, or starts a program or a service.
        /// Reports go through the channel the worker is handed, and the
        /// collections come in through Rescan.</summary>
        [Fact]
        public void TheLaneReachesADeviceThroughTheSharedChannelAlone()
        {
            foreach (string file in LaneFiles)
            {
                string code = Code(Source(file));
                foreach (string call in new[]
                         {
                             "HidD_SetFeature", "HidD_GetFeature", "HidD_SetOutputReport", "HidD_GetInputReport", "WriteFile",
                             "ReadFile", "DeviceIoControl", "SetupDi", "RawHidOutput", "VendorHidReader", "Enumerate(",
                             "Process.Start", "ServiceController",
                         })
                    Assert.False(code.Contains(call, StringComparison.Ordinal), file + " names " + call);
            }
            // The defaults a worker is made with in the app are the shared
            // helpers the test host refuses.
            string backend = Source("LampArrayBackend.cs");
            Assert.Contains("_openQuery = seams.OpenQuery ?? HidReportChannel.OpenFeatureOnly;", backend);
            Assert.Contains("_openClaim = seams.OpenClaim ?? HidReportChannel.Open;", backend);
            Assert.Contains("_setFeatureOnce = seams.SetFeatureOnce ?? HidReportChannel.SetFeatureOnce;", backend);
        }

        /// <summary>Every native call the lane declares, by name. Each one
        /// reads: the parser's own buffers, the device tree, the shell's
        /// window and token. A call added to the lane shows up here.</summary>
        [Fact]
        public void TheLanesNativeCalls_AreTheOnesNamedHere()
        {
            var declared = LaneFiles
                .SelectMany(file => Regex.Matches(Code(Source(file)), @"static extern \S+ (\w+)\(").Select(match => match.Groups[1].Value))
                .OrderBy(name => name, StringComparer.Ordinal);
            var expected = new[]
            {
                "HidP_InitializeReportForID", "HidP_SetUsageValue", "HidP_SetUsageValueArray", "HidP_SetData",
                "CM_Get_Device_Interface_PropertyW", "CM_Locate_DevNodeW", "CM_Get_Parent", "CM_Get_Device_IDW",
                "CM_Get_DevNode_PropertyW",
                "GetShellWindow", "GetWindowThreadProcessId", "OpenProcess", "OpenProcessToken", "GetTokenInformation",
                "CloseHandle",
            };
            Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), declared);
        }

        /// <summary>The lighting switches are Windows' and the user's.
        /// PadForge reads them and writes none: no call that sets or
        /// deletes a value or makes a key, and no key opened for writing.</summary>
        [Fact]
        public void TheLightingSwitchesAreReadAndNeverWritten()
        {
            string source = Source("WindowsLightingState.cs");
            foreach (string write in new[]
                     {
                         "RegSetValue", "SetValue", "CreateSubKey", "DeleteValue", "DeleteSubKey", "RegCreateKey", "RegDeleteKey",
                         "RegistryKeyPermissionCheck", "RegistryRights",
                     })
                Assert.False(source.Contains(write, StringComparison.Ordinal), "WindowsLightingState names " + write);
            // Each key is opened by its name alone, which opens it for
            // reading.
            var opens = Regex.Matches(Code(source), @"OpenSubKey\(([^()]*)\)");
            Assert.Equal(3, opens.Count);
            Assert.All(opens, open => Assert.DoesNotContain(",", open.Groups[1].Value));
            // Neither of the other two files of the lane touches the
            // registry at all.
            Assert.DoesNotContain("Registry", Code(Source("LampArrayBackend.cs")));
            Assert.DoesNotContain("Registry", Code(Source("LampArrayProtocol.cs")));
        }

        /// <summary>The worker's tests hand the worker its walks. One test
        /// walks the real collections and opens real ones, the live test,
        /// past its gate and inside the scope that lifts the refusal. The
        /// one other scope that lifts it counts the descriptor reads
        /// against an open of the test's own, which reaches no collection.</summary>
        [Fact]
        public void OnlyTheLiveTestReachesTheRealCollections()
        {
            string tests = AuditDelta20261002Tests.RepoText("PadForge.Tests", "LampArrayBackendTests.cs");
            int gate = tests.IndexOf("GetEnvironmentVariable(\"PADFORGE_LIVE_LAMPARRAY\") != \"1\") return;", StringComparison.Ordinal);
            Assert.True(gate >= 0, "the live test's gate");
            // Each name is put together here, so this test is not one of
            // the places it looks for.
            string lift = "RealHidAccess." + "AllowForTest()";
            Assert.Equal(2, Count(tests, lift));
            int scope = tests.IndexOf(lift, StringComparison.Ordinal);
            Assert.True(scope > gate, "the refusal is lifted past the gate");
            int counted = tests.IndexOf(lift, scope + lift.Length, StringComparison.Ordinal);
            int reads = tests.IndexOf("public void TheTestHostRefusesTheDescriptorReads()", StringComparison.Ordinal);
            int after = tests.IndexOf("public void TheLanesOneOpen_AsksBeforeItOpens()", StringComparison.Ordinal);
            Assert.True(reads > scope && counted > reads && after > counted, "the other scope is the descriptor reads' own");
            // Inside it the reads go through the open that test scripts.
            Assert.Equal(1, Count(tests.Substring(reads, after - reads), "new LampArray" + "Descriptors()"));
            Assert.True(tests.IndexOf("new LampArray" + "Descriptors()", reads, StringComparison.Ordinal)
                        > tests.IndexOf("Assert.Equal(3, asked.Count);", counted, StringComparison.Ordinal),
                "the app's own reads are made with the refusal back in force");
            foreach (string real in new[]
                     {
                         "HidCollectionRuntime." + "Enumerate()", "HidReportChannel." + "OpenFeatureOnly(",
                         "HidReportChannel." + "Open(", "LampArrayContainers." + "For(", "LampArrayContainers." + "Ancestors(",
                         "AudioPassthroughService." + "DevicePathContainerId(",
                     })
            {
                Assert.Equal(1, Count(tests, real));
                Assert.True(tests.IndexOf(real, StringComparison.Ordinal) > scope, real + " is reached inside the scope");
            }

            string protocolTests = AuditDelta20261002Tests.RepoText("PadForge.Tests", "LampArrayProtocolTests.cs");
            foreach (string real in new[] { "Enumerate(", "HidReportChannel.Open", "AllowForTest", "LampArrayDescriptors", "LampArrayContainers" })
                Assert.DoesNotContain(real, protocolTests);
        }
    }
}
