using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The row of a TouchSense mouse bound to WinUSB (#494): a mouse row
    /// in shape and scale, read from the mouse's own reports, that lives
    /// as long as its channel. Scripted channels only, and a clock the
    /// test sets for the motion window.
    /// </summary>
    public class TouchSenseMouseDeviceTests
    {
        private const string InterfaceGuid = "{fd826c66-3556-4845-9436-0fe7580ded02}";
        internal const string Key = @"\\?\usb#vid_046d&pid_c030#5&2e1a4c7b&0&2#" + InterfaceGuid;
        internal static readonly TouchSenseMouse IFeel = new(Key, 0x046D, 0xC030);
        private static readonly long MsTicks = Stopwatch.Frequency / 1000;

        // A mouse row's scales (PadForge.Engine Common/SdlMouseWrapper.cs:22-24).
        private const int Center = 32767;
        private const float MotionScale = 2048f, ScrollScale = 128f;

        private static TouchSenseMouseDevice Row(ITouchSenseChannel channel, Func<long> timestamp = null)
            => new(IFeel, channel, start: false, timestamp);

        [Fact]
        public void TheRow_IsAMouseRow_WithTheMousesIdsAndItsInterfacePath()
        {
            var channel = new FakeTouchSenseChannel(Key);
            using var row = Row(channel);

            Assert.Equal(InputDeviceType.Mouse, row.GetInputDeviceType());
            Assert.Equal((3, 5, 0, 0), (row.NumAxes, row.NumButtons, row.NumHats, row.RawButtonCount));
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, row.SupportedButtonIndices);
            Assert.Equal(((ushort)0x046D, (ushort)0xC030), (row.VendorId, row.ProductId));
            Assert.Equal(Key, row.DevicePath);
            Assert.Equal("Logitech iFeel Mouse", row.Name);
            Assert.Equal((string.Empty, string.Empty), (row.SerialNumber, row.SdlGuid));
            Assert.Same(channel, row.Channel);
            Assert.True(row.IsAttached);

            // Its objects are a mouse's, name for name, so a mapping names
            // its inputs as it names any mouse's.
            Assert.Equal(SdlMouseWrapper.MouseObjects().Select(o => (o.InputIndex, o.Name, o.ObjectTypeGuid, o.ObjectType, o.Offset)),
                row.GetDeviceObjects().Select(o => (o.InputIndex, o.Name, o.ObjectTypeGuid, o.ObjectType, o.Offset)));
            // And its product is "a mouse", the id every mouse row carries.
            using var mouse = new SdlMouseWrapper();
            Assert.True(mouse.Open(new RawInputListener.DeviceInfo { DevicePath = @"\\?\hid#vid_046d&pid_c077#bench", Name = "Mouse" }));
            Assert.Equal(mouse.ProductGuid, row.ProductGuid);
            Assert.Equal((mouse.NumAxes, mouse.NumButtons, mouse.NumHats, mouse.RawButtonCount),
                (row.NumAxes, row.NumButtons, row.NumHats, row.RawButtonCount));
            Assert.Equal(mouse.SupportedButtonIndices, row.SupportedButtonIndices);

            // Rumble reaches the motor through the mouse's TouchSense unit
            // and the peripheral outputs, never through the row: a mouse
            // row gets its Force Feedback group only as a haptic peripheral.
            Assert.False(row.HasRumble || row.HasRumbleTriggers || row.HasHaptic);
            Assert.False(row.SetRumble(65535, 65535));
            Assert.False(row.StopRumble());
            Assert.Equal(0, channel.Attempts);
            Assert.False(row.HasGyro || row.HasAccel || row.HasTouchpad);
            Assert.Equal((IntPtr.Zero, IntPtr.Zero), (row.GamepadHandle, row.HapticHandle));
        }

        /// <summary>The row's id is its interface path's, in one case, so
        /// a session never mints a second row for a mouse the user already
        /// assigned, and its instance id sits in the band no SDL device
        /// uses.</summary>
        [Fact]
        public void TheRowsIdentity_IsStable_AndIsThePortsNotTheModels()
        {
            using var row = Row(new FakeTouchSenseChannel(Key));
            using (var md5 = System.Security.Cryptography.MD5.Create())
                Assert.Equal(new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("pftouchsense:" + Key.ToUpperInvariant()))),
                    row.InstanceGuid);
            Assert.Equal(row.InstanceGuid, TouchSenseMouseDevice.IdentityFor(Key));
            // Windows compares interface paths without regard to case.
            Assert.Equal(row.InstanceGuid, TouchSenseMouseDevice.IdentityFor(Key.ToUpperInvariant()));
            // The same model on another port is another device node, and another row.
            Assert.NotEqual(row.InstanceGuid, TouchSenseMouseDevice.IdentityFor(Key.Replace("&0&2#", "&0&3#")));
            Assert.NotEqual(row.InstanceGuid, row.ProductGuid);

            Assert.Equal(SyntheticInstanceId.From(Key), row.SdlInstanceId);
            Assert.True(SyntheticInstanceId.IsSynthetic(row.SdlInstanceId));
            // A sibling with no name on the list still gets one.
            using var unlisted = new TouchSenseMouseDevice(new TouchSenseMouse(Key, 0x046D, 0xC033), new FakeTouchSenseChannel(Key), start: false);
            Assert.Equal("TouchSense mouse 046D:C033", unlisted.Name);
            Assert.Throws<ArgumentNullException>(() => new TouchSenseMouseDevice(IFeel, null));
        }

        /// <summary>On the device list the row is a mouse with the bound
        /// mouse's IDs, so the Devices page finds the offer that gives
        /// Windows its driver back, and with the WinUSB interface path the
        /// peripheral host reads the container from.</summary>
        [Fact]
        public void OnTheDeviceList_TheRowCarriesWhatTheRestoreAndTheLinkerNeed()
        {
            using var row = Row(new FakeTouchSenseChannel(Key));
            var record = new UserDevice();
            record.LoadFromExternalDevice(row);

            Assert.Equal(InputDeviceType.Mouse, record.CapType);
            Assert.Equal((3, 5), (record.CapAxeCount, record.CapButtonCount));
            Assert.Equal(((ushort)0x046D, (ushort)0xC030), (record.VendorId, record.ProdId));
            Assert.Equal(Key, record.DevicePath);
            Assert.Equal(Key, PeripheralOutputHost.ContainerPath(record));
            Assert.Equal(row.InstanceGuid, record.InstanceGuid);
            Assert.Same(row, record.Device);
            // No rumble of the row's own, so no force feedback state of the gamepad kind.
            Assert.Null(record.ForceFeedbackState);

            Assert.True(VendorUsbDriverInstaller.IsOptInRow(record.VendorId, record.ProdId));
            var node = new VendorUsbDriverInstaller.UsbNode(@"USB\VID_046D&PID_C030\5&2E1A4C7B&0&2", 0x046D, 0xC030, -1, "WINUSB",
                new[] { @"USB\VID_046D&PID_C030&REV_0101", @"USB\VID_046D&PID_C030" }, Array.Empty<string>());
            var offer = VendorUsbDriverInstaller.QueryOptIn(record.VendorId, record.ProdId, new[] { node }, _ => true, _ => false);
            Assert.True(offer?.Restore);
            Assert.Equal(VendorUsbDriverInstaller.OptInKind.TouchSenseMouse, offer.Kind);
            // The node's instance ID is the one in the row's own path.
            Assert.Contains(node.InstanceId.Replace('\\', '#'), row.DevicePath, StringComparison.OrdinalIgnoreCase);
        }

        // ── State ──

        /// <summary>The counts gathered since the last poll go through the
        /// motion window a Raw Input mouse's go through, with its scales:
        /// the axes are compared with that window fed the same counts.</summary>
        [Fact]
        public void TheRowsMotion_IsAMouseRows_ScaleForScale()
        {
            long now = 1000 * MsTicks;
            using var row = Row(new FakeTouchSenseChannel(Key), () => now);
            var window = new RelativeVelocityWindow();
            static int Axis(float value, float scale) => Math.Clamp(Center + (int)(value * scale), 0, 65535);

            // A still mouse rests at center with nothing pressed.
            var state = row.GetCurrentState();
            window.Add(now, 0, 0, 0);
            Assert.Equal(new[] { Center, Center, Center }, state.Axis.Take(3));
            Assert.All(state.Buttons.Take(5), pressed => Assert.False(pressed));
            Assert.Equal((0, 0), (state.MouseRawDX, state.MouseRawDY));

            for (int poll = 0; poll < 40; poll++)
            {
                now += MsTicks;
                // A report every fourth poll, as a 250 Hz mouse polled at 1 kHz.
                int dx = poll % 4 == 0 ? 12 : 0, dy = poll % 4 == 0 ? -7 : 0;
                if (poll % 4 == 0) row.Feed(new byte[] { 0x00, (byte)dx, unchecked((byte)dy), 0x00 });
                state = row.GetCurrentState();
                window.Add(now, dx, dy, 0);
                window.CountsPerSecond(out float vx, out float vy, out _);
                Assert.Equal(Axis(vx, MotionScale / 1000f), state.Axis[0]);
                Assert.Equal(Axis(vy, MotionScale / 1000f), state.Axis[1]);
                Assert.Equal(Center, state.Axis[2]);
                // The exact counts ride beside the axes for the gesture reader.
                Assert.Equal((dx, dy), (state.MouseRawDX, state.MouseRawDY));
            }
            Assert.True(state.Axis[0] > Center && state.Axis[1] < Center);
        }

        /// <summary>One wheel count is one notch, the 120 Windows reports
        /// for it, so a notch reads 15,360 above center as it does on any
        /// mouse row, and holds for the motion window.</summary>
        [Fact]
        public void TheWheel_IsANotchPerCount_AsOnAnyMouseRow()
        {
            long now = 1000 * MsTicks;
            using var row = Row(new FakeTouchSenseChannel(Key), () => now);
            Assert.Equal(120, TouchSenseMouseDevice.WheelDelta);

            row.Feed(new byte[] { 0x00, 0x00, 0x00, 0x01 });
            Assert.Equal(Center + (int)(120 * ScrollScale), row.GetCurrentState().Axis[2]);
            Assert.Equal(Center + 15360, Center + (int)(120 * ScrollScale));
            // Held while the notch is inside the window, then back to rest.
            now += 10 * MsTicks;
            Assert.Equal(Center + 15360, row.GetCurrentState().Axis[2]);
            now += (RelativeVelocityWindow.WindowMs + 5) * MsTicks;
            Assert.Equal(Center, row.GetCurrentState().Axis[2]);

            // Down, and two notches in one poll.
            row.Feed(new byte[] { 0x00, 0x00, 0x00, 0xFF });
            row.Feed(new byte[] { 0x00, 0x00, 0x00, 0xFF });
            Assert.Equal(Center - 2 * 15360, row.GetCurrentState().Axis[2]);
        }

        [Fact]
        public void TheButtons_AreInAMouseRowsOrder()
        {
            using var row = Row(new FakeTouchSenseChannel(Key));
            // Left, middle, right, then the two side buttons: the report's
            // own bits are left, right, middle, side, extra.
            foreach (var (bits, pressed) in new (byte, int[])[]
            {
                (0x01, new[] { 0 }), (0x02, new[] { 2 }), (0x04, new[] { 1 }), (0x08, new[] { 3 }), (0x10, new[] { 4 }),
                (0x1F, new[] { 0, 1, 2, 3, 4 }), (0x00, Array.Empty<int>()),
                // The three bits above them are no button of a mouse row.
                (0xE0, Array.Empty<int>()),
            })
            {
                row.Feed(new byte[] { bits, 0, 0, 0 });
                var state = row.GetCurrentState();
                Assert.Equal(pressed, Enumerable.Range(0, 5).Where(i => state.Buttons[i]));
            }
        }

        [Fact]
        public void CountsAddUpBetweenTwoPolls_AndAreReadOnce()
        {
            using var row = Row(new FakeTouchSenseChannel(Key));
            row.Feed(new byte[] { 0x01, 3, 0xFE, 0 });
            row.Feed(new byte[] { 0x01, 4, 0xFD, 0 });
            var state = row.GetCurrentState();
            Assert.Equal((7, -5), (state.MouseRawDX, state.MouseRawDY));
            Assert.True(state.Buttons[0]);

            // The next poll finds no new counts, and the button still held.
            state = row.GetCurrentState();
            Assert.Equal((0, 0), (state.MouseRawDX, state.MouseRawDY));
            Assert.True(state.Buttons[0]);

            // A report of another length is no report of this mouse.
            row.Feed(new byte[] { 0x00, 9, 9 });
            row.Feed(new byte[] { 0x00, 9, 9, 9, 9 });
            row.Feed(ReadOnlySpan<byte>.Empty);
            state = row.GetCurrentState();
            Assert.Equal((0, 0), (state.MouseRawDX, state.MouseRawDY));
            Assert.True(state.Buttons[0]);
        }

        /// <summary>A sibling whose input report is not the iFeel Mouse's
        /// is read as no pointer at all: its row is there, for the restore
        /// and for rumble, and nothing on it ever moves.</summary>
        [Fact]
        public void AMouseWhoseInputReportIsNotTheIFeelMouses_GivesNoPointer()
        {
            using var row = Row(new FakeTouchSenseChannel(Key) { ReadsPointer = false });
            row.Feed(new byte[] { 0x1F, 0x7F, 0x7F, 0x7F });
            var state = row.GetCurrentState();
            Assert.Equal(new[] { Center, Center, Center }, state.Axis.Take(3));
            Assert.All(state.Buttons.Take(5), pressed => Assert.False(pressed));
            Assert.Equal((0, 0), (state.MouseRawDX, state.MouseRawDY));
            Assert.True(row.IsAttached);
        }

        // ── The reader and the channel ──

        [Fact]
        public void TheReader_FeedsTheRowFromTheChannel_AndClosesTheChannelWhenItStops()
        {
            var channel = new FakeTouchSenseChannel(Key);
            using var row = new TouchSenseMouseDevice(IFeel, channel);
            channel.Deliver(0x01, 0x05, 0xFB, 0x00);
            channel.Deliver(0x01, 0x05, 0xFB, 0x00);
            var state = row.GetCurrentState();
            Assert.Equal((10, -10), (state.MouseRawDX, state.MouseRawDY));
            Assert.True(state.Buttons[0]);

            // The mouse leaves: the read ends, and the reader closes the channel.
            channel.FailReads = true;
            Assert.True(SpinWait.SpinUntil(() => channel.Disposals >= 1, 30000), "the reader closed the channel");
            Assert.False(row.IsAttached);
            // A row that is not attached reads as no state, which takes it offline.
            Assert.Null(row.GetCurrentState());
        }

        /// <summary>The channel is one object the row and the backend
        /// share. When the backend closes it after a failed write, the row
        /// reads as gone at once, which is how the device pass learns to
        /// retire it and open the mouse again.</summary>
        [Fact]
        public void AChannelClosedBySomeoneElse_TakesTheRowWithIt()
        {
            var channel = new FakeTouchSenseChannel(Key);
            using var row = new TouchSenseMouseDevice(IFeel, channel);
            Assert.True(row.IsAttached);
            channel.Dispose();
            Assert.False(row.IsAttached);
            Assert.Null(row.GetCurrentState());
        }

        /// <summary>Retiring a row on the poll thread returns at once and
        /// leaves the close to the reader. The shutdown's close returns
        /// only with the channel closed. A row with no reader closes its
        /// channel itself.</summary>
        [Fact]
        public void RetiringARow_NeverBlocksThePollThread_AndTheShutdownsCloseIsDoneWhenItReturns()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var channel = new FakeTouchSenseChannel(Key);
            // The reader sits inside its read, as it does in a 100 ms pipe wait.
            channel.BeforeRead = () =>
            {
                held.Set();
                release.Wait(30000);
            };
            var row = new TouchSenseMouseDevice(IFeel, channel);
            try
            {
                Assert.True(held.Wait(30000), "the reader is inside its read");
                row.Dispose();
                // Back at once, with the reader still in its read and the channel still open.
                Assert.False(row.IsAttached);
                Assert.Equal(0, channel.Disposals);
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => channel.Disposals >= 1, 30000), "the reader closed the channel as it left");
            }
            finally
            {
                release.Set();
            }

            var second = new FakeTouchSenseChannel(Key);
            var closing = new TouchSenseMouseDevice(IFeel, second);
            closing.Close();
            Assert.False(second.IsOpen);
            Assert.False(closing.IsAttached);
            closing.Close();
            closing.Dispose();

            var third = new FakeTouchSenseChannel(Key);
            var readerless = Row(third);
            readerless.Dispose();
            Assert.False(third.IsOpen);
            Assert.Equal(1, third.Disposals);
        }
    }

    /// <summary>
    /// The device pass's phase for TouchSense mice (#494): a worker looks
    /// for the bound mice and opens them, and the poll thread registers the
    /// rows it finished and retires the ones whose channel closed. The
    /// look, the open, the clock and the worker are the test's, and no
    /// device is opened.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class TouchSenseMiceLaneTests : IDisposable
    {
        private const string Key = TouchSenseMouseDeviceTests.Key;
        private const string OtherKey = @"\\?\usb#vid_046d&pid_c032#5&2e1a4c7b&0&3#{fd826c66-3556-4845-9436-0fe7580ded02}";
        private static readonly TouchSenseMouse IFeel = TouchSenseMouseDeviceTests.IFeel;
        private static readonly TouchSenseMouse MouseMan = new(OtherKey, 0x046D, 0xC032);
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;

        public TouchSenseMiceLaneTests()
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

        /// <summary>An input manager whose TouchSense phase runs on the
        /// test's parts. A sweep the phase starts waits until the test
        /// runs it, as the worker's does in its own time.</summary>
        private sealed class Lane : IDisposable
        {
            private long _now = 1_000_000;
            private readonly ConcurrentQueue<Task> _sweeps = new();
            private int _looks;

            public readonly InputManager Manager = new();
            public readonly TouchSenseChannels Table = new();
            public readonly List<FakeTouchSenseChannel> Opened = new();

            /// <summary>What the look finds, or null for a look that failed.</summary>
            public volatile TouchSenseMouse[] Bound = Array.Empty<TouchSenseMouse>();

            /// <summary>Makes the channel of an open, or null for one that fails.</summary>
            public Func<TouchSenseMouse, FakeTouchSenseChannel> Opener;

            /// <summary>Set when an open found an earlier channel of the same mouse still open.</summary>
            public volatile bool OpenedBesideAnOpenChannel;

            public Lane()
            {
                Opener = mouse => new FakeTouchSenseChannel(mouse.Path);
                Manager.TouchSenseFind = () =>
                {
                    Interlocked.Increment(ref _looks);
                    return Bound;
                };
                Manager.TouchSenseOpen = mouse =>
                {
                    lock (Opened)
                    {
                        if (Opened.Any(c => c.IsOpen && string.Equals(c.Path, mouse.Path, StringComparison.OrdinalIgnoreCase)))
                            OpenedBesideAnOpenChannel = true;
                        var channel = Opener(mouse);
                        if (channel != null) Opened.Add(channel);
                        return channel;
                    }
                };
                Manager.TouchSenseTable = Table;
                Manager.TouchSenseTicks = () => Interlocked.Read(ref _now);
                Manager.TouchSenseRun = sweep =>
                {
                    var task = new Task(sweep);
                    _sweeps.Enqueue(task);
                    return task;
                };
            }

            public int Looks => Volatile.Read(ref _looks);
            public int Opens { get { lock (Opened) return Opened.Count; } }
            public void Advance(long ms) => Interlocked.Add(ref _now, ms);

            /// <summary>The poll thread's half. True when the device list changed.</summary>
            public bool Pass() => Manager.SyncTouchSenseMice();

            /// <summary>Runs the sweep the last pass started, if it started one.</summary>
            public bool RunSweep()
            {
                if (!_sweeps.TryDequeue(out var sweep)) return false;
                sweep.RunSynchronously();
                return true;
            }

            /// <summary>A pass, the sweep it started, and the pass that registers what the sweep opened.</summary>
            public bool Cycle()
            {
                bool changed = Pass();
                RunSweep();
                return Pass() | changed;
            }

            public UserDevice Record(Guid id)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                    return SettingsManager.UserDevices.Items.SingleOrDefault(d => d.InstanceGuid == id);
            }

            public void Dispose()
            {
                // A sweep the test left waiting must not hold the shutdown up.
                while (RunSweep()) { }
                Manager.RetireTouchSenseMice();
            }
        }

        private static readonly Guid RowId = TouchSenseMouseDevice.IdentityFor(Key);

        [Fact]
        public void ABoundMouse_GetsARowOfItsOwn_OnThePassAfterTheSweepThatOpenedIt()
        {
            using var lane = new Lane();
            int listed = 0;
            lane.Table.Opened += () => Interlocked.Increment(ref listed);
            Assert.Same(TouchSenseChannels.Shared, new InputManager().TouchSenseTable);

            // No bound mouse: the sweep looks and opens nothing.
            Assert.False(lane.Cycle());
            Assert.Equal((1, 0), (lane.Looks, lane.Opens));
            Assert.Empty(lane.Manager.TouchSenseMice);

            // The user bound one. The next sweep, no sooner than the
            // interval, finds it and opens it.
            lane.Bound = new[] { IFeel };
            Assert.False(lane.Pass());
            Assert.False(lane.RunSweep());
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.False(lane.Pass());
            Assert.True(lane.RunSweep());
            Assert.Equal((2, 1), (lane.Looks, lane.Opens));
            // Listed once its row exists, which is what wakes the backend.
            Assert.Same(lane.Opened[0], lane.Table.Find(Key));
            Assert.Equal(1, listed);
            Assert.Null(lane.Record(RowId));

            // The poll thread registers the finished row on its next pass.
            Assert.True(lane.Pass());
            var record = lane.Record(RowId);
            Assert.True(record.IsOnline);
            var row = Assert.IsType<TouchSenseMouseDevice>(record.Device);
            Assert.Same(lane.Opened[0], row.Channel);
            Assert.Equal(InputDeviceType.Mouse, record.CapType);
            Assert.Equal(((ushort)0x046D, (ushort)0xC030, Key), (record.VendorId, record.ProdId, record.DevicePath));
            Assert.Equal("Logitech iFeel Mouse", record.ResolvedName);
            Assert.Same(row, Assert.Single(lane.Manager.TouchSenseMice));

            // Nothing more changes while the mouse stays, and it is opened once.
            Assert.False(lane.Pass());
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.False(lane.Cycle());
            Assert.Equal((3, 1), (lane.Looks, lane.Opens));
            Assert.False(lane.OpenedBesideAnOpenChannel);
            Assert.Equal((1000, 5000), (InputManager.TouchSenseSweepMs, InputManager.TouchSenseReopenMs));
        }

        /// <summary>The row reads its mouse through the channel the sweep
        /// opened: reports reach the record's device as mouse input.</summary>
        [Fact]
        public void TheRegisteredRow_ReadsItsMouse()
        {
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var row = Assert.Single(lane.Manager.TouchSenseMice);
            lane.Opened[0].Deliver(0x04, 0x02, 0x03, 0x00);
            var state = lane.Record(RowId).Device.GetCurrentState();
            Assert.Equal((2, 3), (state.MouseRawDX, state.MouseRawDY));
            Assert.True(state.Buttons[1]);
            Assert.True(row.IsAttached);
        }

        /// <summary>A channel that closed, here as the backend closes it
        /// after a failed write, takes its row offline with its level
        /// zeroed. The mouse is opened again only after five seconds, and
        /// the new row comes back on the same record, which is what its
        /// assignment names.</summary>
        [Fact]
        public void ARowWhoseChannelClosed_IsRetired_AndItsMouseIsOpenedAgainAfterFiveSeconds()
        {
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var record = lane.Record(RowId);
            var first = lane.Opened[0];
            PeripheralOutputs.SetMotors(RowId, 65535, 0);

            first.Dispose();
            Assert.True(lane.Pass());
            Assert.False(record.IsOnline);
            Assert.Null(record.Device);
            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(RowId));
            Assert.Null(lane.Table.Find(Key));
            Assert.Empty(lane.Manager.TouchSenseMice);

            // The sweeps before the wait is over find the mouse and leave it.
            for (int ms = InputManager.TouchSenseSweepMs; ms < InputManager.TouchSenseReopenMs; ms += InputManager.TouchSenseSweepMs)
            {
                lane.Advance(InputManager.TouchSenseSweepMs);
                Assert.False(lane.Cycle());
            }
            lane.Advance(InputManager.TouchSenseSweepMs - 1);
            Assert.False(lane.Cycle());
            Assert.Equal(1, lane.Opens);

            lane.Advance(1);
            Assert.True(lane.Cycle());
            Assert.Equal(2, lane.Opens);
            Assert.False(lane.OpenedBesideAnOpenChannel);
            Assert.Same(record, lane.Record(RowId));
            Assert.True(record.IsOnline);
            var row = Assert.IsType<TouchSenseMouseDevice>(record.Device);
            Assert.Same(lane.Opened[1], row.Channel);
            Assert.Same(lane.Opened[1], lane.Table.Find(Key));
        }

        /// <summary>WinUSB gives a device one handle, so a mouse is opened
        /// again only once the channel it had is closed. The sweep closes
        /// one whose reader has not got to it yet, here a reader held
        /// inside its read, before it opens.</summary>
        [Fact]
        public void TheSweep_ClosesTheChannelTheMouseHad_BeforeItOpensTheMouseAgain()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var first = lane.Opened[0];
            var row = Assert.Single(lane.Manager.TouchSenseMice);
            try
            {
                first.BeforeRead = () =>
                {
                    held.Set();
                    release.Wait(30000);
                };
                Assert.True(held.Wait(30000), "the reader is inside its read");
                // The row is retired with its reader still in the read.
                row.Dispose();
                Assert.True(lane.Pass());
                Assert.True(first.IsOpen);

                lane.Advance(InputManager.TouchSenseReopenMs);
                Assert.True(lane.Cycle());
                Assert.Equal(2, lane.Opens);
                Assert.False(lane.OpenedBesideAnOpenChannel);
                Assert.False(first.IsOpen);
            }
            finally
            {
                release.Set();
            }
        }

        /// <summary>The device node of the mouse at <see cref="Key"/>, as
        /// the driver switch names it.</summary>
        private const string Node = @"USB\VID_046D&PID_C030\5&2E1A4C7B&0&2";

        /// <summary>A driver switch holds the mouse's node for as long as
        /// it takes (VendorUsbDriverInstaller.Bind and Restore). The
        /// sweeps that run meanwhile, one a second, open nothing on that
        /// node and open any other mouse as ever. The first sweep after
        /// the hold is let go opens the mouse.</summary>
        [Fact]
        public void WhileADriverSwitchHoldsAMousesNode_TheSweepOpensNothingOnIt_AndOpensItOnceTheHoldIsLetGo()
        {
            using var lane = new Lane();
            lane.Bound = new[] { IFeel, MouseMan };
            var hold = lane.Table.Hold(Node);
            Assert.True(lane.Cycle());
            Assert.Equal(OtherKey, Assert.Single(lane.Opened).Path);
            Assert.Null(lane.Table.Find(Key));

            // Every later sweep of the switch finds the mouse and leaves it.
            for (int sweep = 0; sweep < 6; sweep++)
            {
                lane.Advance(InputManager.TouchSenseSweepMs);
                Assert.False(lane.Cycle());
            }
            Assert.Equal(1, lane.Opens);
            Assert.Null(lane.Record(RowId));

            hold.Dispose();
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.True(lane.Cycle());
            Assert.Equal(2, lane.Opens);
            Assert.Same(lane.Opened[1], lane.Table.Find(Key));
            Assert.True(lane.Record(RowId).IsOnline);
            Assert.False(lane.OpenedBesideAnOpenChannel);
        }

        /// <summary>A driver switch that begins while the sweep is inside
        /// an open of that mouse waits for the open to be over, and the
        /// channel the open brings is closed and never listed, so no
        /// handle of PadForge's is on the node when the switch goes on to
        /// ask Windows. The row made for the channel retires on the next
        /// pass, as any row whose channel closed does, and the mouse is
        /// opened again once the hold is let go.</summary>
        [Fact]
        public void ADriverSwitchThatBeginsInsideAnOpen_WaitsForIt_AndItsChannelIsClosedUnlisted()
        {
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            IDisposable hold = null;
            Thread switching = null;
            bool inForce = false, waited = false;
            lane.Opener = mouse =>
            {
                switching = new Thread(() => hold = lane.Table.Hold(Node)) { IsBackground = true };
                switching.Start();
                // In force once no other open of the mouse may begin, and
                // not back yet: the open it waits for is this one.
                inForce = SpinWait.SpinUntil(() => !lane.Table.MayOpen(mouse.Path), 30000);
                waited = !switching.Join(100);
                return new FakeTouchSenseChannel(mouse.Path);
            };
            Assert.False(lane.Pass());
            Assert.True(lane.RunSweep());
            Assert.True(inForce, "the hold was in force inside the open");
            Assert.True(waited, "the hold waited for the open");
            // Back as soon as the sweep says the open is over, well inside its own wait.
            Assert.True(switching.Join(TouchSenseChannels.DefaultHoldWaitMs / 2), "the hold came back as soon as the open was over");
            Assert.NotNull(hold);
            // Closed by the table. The row's reader closes it once more as
            // it leaves, in its own time, so the closes are not counted.
            var crossed = Assert.Single(lane.Opened);
            Assert.False(crossed.IsOpen);
            Assert.Null(lane.Table.Find(Key));
            Assert.Equal(0, lane.Table.Count);

            // The row made for that channel is registered and retired by
            // the one pass.
            Assert.True(lane.Pass());
            var record = lane.Record(RowId);
            Assert.False(record.IsOnline);
            Assert.Null(record.Device);
            Assert.Empty(lane.Manager.TouchSenseMice);

            // The wait after a closed channel is over, and the node is
            // still held.
            lane.Opener = mouse => new FakeTouchSenseChannel(mouse.Path);
            lane.Advance(InputManager.TouchSenseReopenMs);
            Assert.False(lane.Cycle());
            Assert.Equal(1, lane.Opens);

            hold.Dispose();
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.True(lane.Cycle());
            Assert.Equal(2, lane.Opens);
            Assert.False(lane.OpenedBesideAnOpenChannel);
            Assert.Same(record, lane.Record(RowId));
            Assert.True(record.IsOnline);
            Assert.Same(lane.Opened[1], lane.Table.Find(Key));
        }

        /// <summary>The same row retired a moment before the shutdown: the
        /// shutdown closes the channel its reader has not got to yet, so
        /// that handle too is gone when it returns.</summary>
        [Fact]
        public void AShutdownRightAfterARowWasRetired_ClosesThatRowsChannelToo()
        {
            using var held = new ManualResetEventSlim(false);
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var first = lane.Opened[0];
            var row = Assert.Single(lane.Manager.TouchSenseMice);
            try
            {
                // The reader leaves its read only once the channel is closed.
                first.BeforeRead = () =>
                {
                    held.Set();
                    SpinWait.SpinUntil(() => !first.IsOpen, 30000);
                };
                Assert.True(held.Wait(30000), "the reader is inside its read");
                row.Dispose();
                Assert.True(lane.Pass());
                Assert.True(first.IsOpen);

                lane.Manager.RetireTouchSenseMice();
                Assert.False(first.IsOpen);
                Assert.Equal(1, lane.Opens);
            }
            finally
            {
                first.Dispose();
            }
        }

        [Fact]
        public void AMouseThatCannotBeOpened_IsTriedAgainEveryFiveSeconds_AndSaidOnce()
        {
            const string stuck = @"\\?\usb#vid_046d&pid_c031#5&2e1a4c7b&0&7#{fd826c66-3556-4845-9436-0fe7580ded02}";
            static int Said() => SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains("TouchSense: open failed for 046D:C031 at " + stuck));
            using var lane = new Lane();
            int tries = 0;
            bool fails = true, throws = false;
            lane.Opener = mouse =>
            {
                tries++;
                if (throws) throw new InvalidOperationException("scripted");
                return fails ? null : new FakeTouchSenseChannel(mouse.Path);
            };
            lane.Bound = new[] { new TouchSenseMouse(stuck, 0x046D, 0xC031) };
            int said = Said();

            Assert.False(lane.Cycle());
            Assert.Equal(1, tries);
            Assert.Equal(said + 1, Said());
            for (int ms = InputManager.TouchSenseSweepMs; ms < InputManager.TouchSenseReopenMs; ms += InputManager.TouchSenseSweepMs)
            {
                lane.Advance(InputManager.TouchSenseSweepMs);
                Assert.False(lane.Cycle());
            }
            Assert.Equal(1, tries);

            // An open that throws is an open that failed.
            throws = true;
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.False(lane.Cycle());
            Assert.Equal(2, tries);
            throws = false;
            lane.Advance(InputManager.TouchSenseReopenMs);
            Assert.False(lane.Cycle());
            Assert.Equal(3, tries);
            // Three failures, one line.
            Assert.Equal(said + 1, Said());

            fails = false;
            lane.Advance(InputManager.TouchSenseReopenMs);
            Assert.True(lane.Cycle());
            Assert.Equal(4, tries);
            Assert.True(lane.Record(TouchSenseMouseDevice.IdentityFor(stuck)).IsOnline);
            // The second iFeel Mouse ID is named as Logitech's own package names it.
            Assert.Equal("Logitech iFeel Mouse", lane.Record(TouchSenseMouseDevice.IdentityFor(stuck)).ResolvedName);
        }

        /// <summary>Removed from the Devices page while the mouse is still
        /// there: the row comes back on the next pass with the channel it
        /// has, since closing and opening the mouse again would gain
        /// nothing.</summary>
        [Fact]
        public void ARowTheUserRemoved_ComesBackOnTheNextPass_WithTheChannelItHas()
        {
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var record = lane.Record(RowId);
            var row = record.Device;

            Assert.True(SettingsManager.RemoveDevice(RowId));
            Assert.Null(lane.Record(RowId));
            Assert.True(lane.Pass());
            var again = lane.Record(RowId);
            Assert.NotNull(again);
            Assert.NotSame(record, again);
            Assert.True(again.IsOnline);
            Assert.Same(row, again.Device);
            Assert.Equal(1, lane.Opens);
            Assert.True(lane.Opened[0].IsOpen);
            Assert.False(lane.Pass());
        }

        /// <summary>A look that fails says nothing about the mice, so the
        /// rows stay. A mouse that left has nothing to wait for: when it
        /// is back it is opened at the next sweep.</summary>
        [Fact]
        public void AFailedLookKeepsTheRows_AndAMouseThatLeftIsOpenedAtOnceWhenItIsBack()
        {
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var record = lane.Record(RowId);

            lane.Bound = null;
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.False(lane.Cycle());
            Assert.True(record.IsOnline);
            Assert.Equal(1, lane.Opens);

            // Unplugged: its reader ends, and its row is retired.
            lane.Bound = Array.Empty<TouchSenseMouse>();
            lane.Opened[0].FailReads = true;
            Assert.True(SpinWait.SpinUntil(() => !lane.Opened[0].IsOpen, 30000), "the reader closed the channel");
            Assert.True(lane.Pass());
            Assert.False(record.IsOnline);
            // A sweep while it is away forgets the wait.
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.False(lane.Cycle());

            lane.Bound = new[] { IFeel };
            lane.Advance(InputManager.TouchSenseSweepMs);
            Assert.True(lane.Cycle());
            Assert.Equal(2, lane.Opens);
            Assert.True(record.IsOnline);
        }

        /// <summary>The shutdown closes every mouse itself, the one whose
        /// row is registered and the one still waiting to be, and both
        /// handles are gone when it returns: the next engine start may
        /// open the same mouse a moment later. The registered mouse's
        /// reader sits inside its read here, as it does in a pipe's wait,
        /// and comes out only when the channel is closed under it.</summary>
        [Fact]
        public void TheShutdown_ClosesEveryMouse_AndNothingOpensAfterIt()
        {
            using var held = new ManualResetEventSlim(false);
            using var lane = new Lane();
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var first = lane.Opened[0];
            try
            {
                first.BeforeRead = () =>
                {
                    held.Set();
                    SpinWait.SpinUntil(() => !first.IsOpen, 30000);
                };
                Assert.True(held.Wait(30000), "the reader is inside its read");
                // A second mouse the sweep opened and the poll thread has not registered yet.
                lane.Bound = new[] { IFeel, MouseMan };
                lane.Advance(InputManager.TouchSenseSweepMs);
                Assert.False(lane.Pass());
                Assert.True(lane.RunSweep());
                Assert.Equal(2, lane.Opens);
                var record = lane.Record(RowId);
                PeripheralOutputs.SetMotors(RowId, 65535, 0);

                lane.Manager.RetireTouchSenseMice();
                Assert.All(lane.Opened, channel => Assert.False(channel.IsOpen));
                Assert.False(record.IsOnline);
                Assert.Null(record.Device);
                Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(RowId));
                Assert.Equal(0, lane.Table.Count);
                Assert.Empty(lane.Manager.TouchSenseMice);
                Assert.Null(lane.Record(TouchSenseMouseDevice.IdentityFor(OtherKey)));

                // A pass that outlived the stop looks for nothing and opens nothing.
                int looks = lane.Looks;
                lane.Advance(10L * InputManager.TouchSenseReopenMs);
                Assert.False(lane.Cycle());
                Assert.Equal(looks, lane.Looks);
                Assert.Equal(2, lane.Opens);
                lane.Manager.RetireTouchSenseMice();
            }
            finally
            {
                // Lets the reader out whatever happened above.
                first.Dispose();
            }
        }

        /// <summary>A sweep in the middle of an open when the shutdown
        /// comes finishes the open, sees the shutdown and closes what it
        /// opened, so no handle is left behind and no row is made.</summary>
        [Fact]
        public void AShutdownWhileASweepIsOpeningAMouse_LeavesNoHandleBehind()
        {
            using var opening = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var lane = new Lane();
            lane.Manager.TouchSenseRun = Task.Run;
            lane.Opener = mouse =>
            {
                opening.Set();
                release.Wait(30000);
                return new FakeTouchSenseChannel(mouse.Path);
            };
            lane.Bound = new[] { IFeel };
            try
            {
                Assert.False(lane.Pass());
                Assert.True(opening.Wait(30000), "the sweep is inside its open");

                var shutdown = new Thread(lane.Manager.RetireTouchSenseMice) { IsBackground = true };
                shutdown.Start();
                // The shutdown waits for the sweep, with its latch already set.
                Assert.True(SpinWait.SpinUntil(() => !shutdown.IsAlive
                        || (shutdown.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 30000),
                    "the shutdown waits for the sweep");
                release.Set();
                Assert.True(shutdown.Join(30000), "the shutdown ended");

                Assert.Equal(1, lane.Opens);
                Assert.False(lane.Opened[0].IsOpen);
                Assert.Empty(lane.Manager.TouchSenseMice);
                Assert.Null(lane.Record(RowId));
                Assert.Null(lane.Table.Find(Key));
                Assert.False(lane.Pass());
            }
            finally
            {
                release.Set();
            }
        }

        /// <summary>A shutdown whose wait for the sweep ran out leaves the
        /// sweep inside its open. The sweep finishes the open after the
        /// shutdown is over, sees it, and closes what it opened: no row
        /// is made, and no handle is left with a manager that will never
        /// look at it again.</summary>
        [Fact]
        public void AShutdownThatGaveUpOnASweep_StillLeavesNoHandleBehind()
        {
            using var opening = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var lane = new Lane();
            Task sweep = null;
            lane.Manager.TouchSenseRun = action => sweep = Task.Run(action);
            lane.Manager.TouchSenseShutdownWait = 1;
            lane.Opener = mouse =>
            {
                opening.Set();
                release.Wait(30000);
                return new FakeTouchSenseChannel(mouse.Path);
            };
            lane.Bound = new[] { IFeel };
            try
            {
                Assert.False(lane.Pass());
                Assert.True(opening.Wait(30000), "the sweep is inside its open");
                lane.Manager.RetireTouchSenseMice();
                Assert.False(sweep.IsCompleted);

                release.Set();
                Assert.True(SpinWait.SpinUntil(() => sweep.IsCompleted, 30000), "the sweep ended");
                Assert.Equal(1, lane.Opens);
                Assert.False(lane.Opened[0].IsOpen);
                Assert.Equal(0, lane.Table.Count);
                Assert.Empty(lane.Manager.TouchSenseMice);
                Assert.False(lane.Pass());
                Assert.Null(lane.Record(RowId));
                Assert.Equal(3000, InputManager.TouchSenseShutdownWaitMs);
            }
            finally
            {
                release.Set();
            }
        }

        // ── From the bound mouse to the buzz ──

        /// <summary>The whole path on scripted parts: the lane opens the
        /// mouse and registers its row, the backend lists the mouse as a
        /// unit, the host's link pass ties the row to the unit by the
        /// container of the interface path both carry, and the level set
        /// on the row goes out as a frame on the one channel the lane
        /// opened. A write that fails takes the unit to sleep and the row
        /// offline, and both come back on the mouse's next channel.</summary>
        [Fact]
        public void FromTheBoundMouseToTheBuzz_ThroughTheRowTheHostTheLinkerAndTheBackend()
        {
            const string half = "11 0A 9E 0A 00 1E 00", stop = "13 00 00 00 00 00 00", stopAll = "F3 00 00 00 00 00 00";
            long tick = 1_000_000;
            using var lane = new Lane();
            lane.Opener = mouse => new FakeTouchSenseChannel(mouse.Path, () => Interlocked.Read(ref tick));
            lane.Bound = new[] { IFeel };
            Assert.True(lane.Cycle());
            var record = lane.Record(RowId);
            var first = lane.Opened[0];

            var backend = new TouchSenseBackend(lane.Table, find: () => lane.Bound,
                containerOf: path => path == Key ? Box : Guid.Empty, ticks: () => Interlocked.Read(ref tick),
                activeTickMs: 1, idleTickMs: 1);
            using var host = new PeripheralOutputHost(
                new HidppBackend(() => Array.Empty<VendorHidCollection>(), (c, s) => null, _ => Guid.Empty, 1, 5),
                new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, 20),
                () => new SensaHapticsService(30000, 16, 1000, () => 0f) { PlatformCanLoadEngine = () => false },
                containerOf: path => path == Key ? Box : Guid.Empty,
                readPresence: () => PeripheralPresence.None,
                direct: new IDirectUnitBackend[] { backend },
                enumerate: () => Array.Empty<HidCollectionInfo>(),
                ticks: () => Interlocked.Read(ref tick));
            void Until(Func<bool> condition, string what)
                => Assert.True(SpinWait.SpinUntil(() =>
                {
                    host.Nudge();
                    backend.Nudge();
                    return condition();
                }, 30000), what);
            var path = new OutputPath(OutputFamily.TouchSenseUnit, Key);

            host.Start();
            Until(() => PeripheralOutputs.Links.For(RowId)?.Haptics.Contains(path) == true, "the row is linked to its unit");
            Assert.Empty(PeripheralOutputs.Links.For(RowId).Lighting);
            // On record, so the row has its Force Feedback group and its rumble chip.
            Until(() => record.HasPeripheralHaptics, "the row's haptic output is on record");
            Assert.True(PeripheralOutputs.IsHapticPeripheral(record));

            PeripheralOutputs.SetMotors(RowId, 32768, 0);
            Until(() => first.Frames.Length >= 1, "the level went out");
            Assert.Equal(half, first.Frames[0]);
            // On the channel the lane opened: the backend opened nothing.
            Assert.Equal(1, lane.Opens);

            // The next frame fails. The unit sleeps, the link goes, and the
            // row keeps its record while its mouse does not answer.
            first.FailSends = true;
            Interlocked.Add(ref tick, TouchSenseProtocol.ResendMs);
            Until(() => !first.IsOpen, "the backend closed the channel");
            Until(() => PeripheralOutputs.Links.For(RowId) == null, "an asleep unit links nothing");
            Assert.True(record.HasPeripheralHaptics);
            Assert.True(lane.Pass());
            Assert.False(record.IsOnline);

            // Five seconds on the lane opens the mouse again and lists the
            // channel, which wakes the unit, and the row comes back.
            lane.Advance(InputManager.TouchSenseReopenMs);
            Assert.True(lane.Cycle());
            Assert.Equal(2, lane.Opens);
            Assert.False(lane.OpenedBesideAnOpenChannel);
            var second = lane.Opened[1];
            Assert.True(record.IsOnline);
            host.DevicesChanged();
            Until(() => PeripheralOutputs.Links.For(RowId)?.Haptics.Contains(path) == true, "the row is linked again");
            // Its level went to zero with the row. Step 2 writes the game's again.
            Assert.Equal(0, second.Attempts);
            PeripheralOutputs.SetMotors(RowId, 32768, 0);
            Until(() => second.Frames.Length >= 1, "the level went out on the new channel");
            Assert.Equal(half, second.Frames[0]);

            // The engine stops: the host first, whose backend sends its last
            // frames on the channel, then the manager, which closes it.
            host.Stop();
            Assert.Equal(new[] { stop, stopAll }, second.Frames.Skip(second.Frames.Length - 2));
            Assert.True(second.IsOpen);
            lane.Manager.RetireTouchSenseMice();
            Assert.False(second.IsOpen);
        }

        // ── The hooks ──

        [Fact]
        public void TheLane_FillsInTheTwoPartialMethodsTheBaseDeclares_WithTheRealParts()
        {
            string lane = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.TouchSenseMice.cs");
            Assert.Contains("partial void UpdateTouchSenseMice(ref bool changed) => changed |= SyncTouchSenseMice();", lane);
            Assert.Contains("partial void ShutdownTouchSenseMice() => RetireTouchSenseMice();", lane);
            // The look, the open and the table of the app itself: the one
            // place a bound mouse is opened, and the table the backend reads.
            Assert.Contains("internal Func<IReadOnlyList<TouchSenseMouse>> TouchSenseFind = TouchSenseBinding.FindBound;", lane);
            Assert.Contains("internal Func<TouchSenseMouse, ITouchSenseChannel> TouchSenseOpen = mouse => TouchSenseChannel.Open(mouse.Path);", lane);
            Assert.Contains("internal TouchSenseChannels TouchSenseTable = TouchSenseChannels.Shared;", lane);
            string backend = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "TouchSenseBackend.cs");
            Assert.Contains(": this(TouchSenseChannels.Shared) { }", backend);
            // The backend opens no mouse: no call of the channel's open is in it.
            Assert.DoesNotContain("TouchSenseChannel.Open(", backend);
            Assert.DoesNotContain("WinUsbTouchSenseUsb", backend);
        }
    }
}
