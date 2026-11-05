using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.WinUsbKeyboards;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Engine.Tablets;
using PadForge.Engine.Touchpads;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A keyboard or mouse PadForge's driver serves stays on that
    /// driver through a reset, and its takeover is the only record that leads
    /// to its row and its restore.</summary>
    [Collection("SettingsManagerStatics")]
    public class TakenDeviceResetTests : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pendingDefault;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            SettingsManager.SlotMappingSets = _sets;
            KeyboardTakeovers.Apply(null);
        }

        [Fact]
        public void ResetToDefaults_KeepsEveryTakeover_ForTheRowAndTheRestore()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            _ = new InputService(vm) { SettingsService = settings };

            var row = Guid.NewGuid();
            vm.Settings.KeyboardTakeovers.Add(new KeyboardTakeoverEntry
            {
                NodeId = @"USB\VID_046D&PID_C31C\1", BindId = @"USB\VID_046D&PID_C31C", RowId = row.ToString(), Name = "Keyboard",
            });
            KeyboardTakeovers.Apply(vm.Settings.KeyboardTakeovers);

            settings.ResetToDefaults();

            Assert.Equal(row, Assert.Single(vm.Settings.KeyboardTakeovers).Row);
            Assert.True(KeyboardTakeovers.IsTakenOver(row));
        }
    }

    /// <summary>Every key of a keyboard read over WinUSB reaches a key index,
    /// the usages with no fixed or character key among them. The usages are
    /// the ones Linux gives a key to (linux-hid hid-input.c, hid_keyboard).</summary>
    public class TakenKeyboardKeyTests
    {
        private static KbmHidKeyMap.HidKey Kb(int usage) => new(0x07, (ushort)usage);

        [Theory]
        [InlineData(0x67, 0x92)]   // Keypad =, VK_OEM_NEC_EQUAL
        [InlineData(0x7F, 0xAD)]   // Mute, as the Consumer page's
        [InlineData(0x80, 0xAF)]   // Volume Up
        [InlineData(0x81, 0xAE)]   // Volume Down
        [InlineData(0x85, 0xC2)]   // Keypad comma, VK_ABNT_C2
        [InlineData(0x90, 0x15)]   // LANG1, VK_HANGUL
        [InlineData(0x91, 0x19)]   // LANG2, VK_HANJA
        [InlineData(0x92, 0xF1)]   // LANG3 Katakana, VK_OEM_FINISH
        [InlineData(0x93, 0xF2)]   // LANG4 Hiragana, VK_OEM_COPY
        [InlineData(0x94, 0xF3)]   // LANG5 Zenkaku/Hankaku, VK_OEM_AUTO
        public void AKeyWithNoCharacterOrFixedRow_StillReachesItsKeyIndex(int usage, int index)
            => Assert.Equal(index, KbmHidKeyMap.TargetOf(Kb(usage), KbmHidKeyMap.UiLayout()));
    }

    /// <summary>A precision touchpad's external buttons and settings as its
    /// mouse row reads them.</summary>
    public class TakenTouchpadButtonTests
    {
        private static PrecisionTouchpadReader.PtpDeviceState Pad() => new() { FeedsPointer = true };

        /// <summary>A held button sends no report until it changes, so 100 ms
        /// of quiet says nothing about it. Linux releases only the contacts on
        /// that timer (hid-multitouch.c mt_release_contacts).</summary>
        [Fact]
        public void AButtonHeldWithNoContact_StaysDown_PastTheContactsStaleLimit()
        {
            var pad = Pad();
            pad.PointerButtons = TouchpadPointer.PrimaryButton;
            pad.LastReportTicks = 1;
            PrecisionTouchpadReader.ConsumePointer(pad, TimeSpan.FromSeconds(5).Ticks, 5000, out _, out _, out _, out int buttons);
            Assert.Equal(1, buttons);
        }

        /// <summary>Buttons are read from a frame's first report alone
        /// (hid-multitouch.c:1272-1279), and a report that opens no frame, a
        /// button-only one with contact count 0, is a first report (:1329-1334).</summary>
        [Fact]
        public void AFramesContinuationReport_LeavesTheButtonsAlone()
        {
            var pad = Pad();
            PrecisionTouchpadReader.TakeButtons(pad, TouchpadPointer.PrimaryButton, 3);
            Assert.Equal(TouchpadPointer.PrimaryButton, pad.PointerButtons);

            pad.FrameExpected = 3;
            pad.FrameSeen = 2;
            PrecisionTouchpadReader.TakeButtons(pad, 0, 0);
            Assert.Equal(TouchpadPointer.PrimaryButton, pad.PointerButtons);

            pad.FrameSeen = 1;
            pad.FrameLifted = 1;
            PrecisionTouchpadReader.TakeButtons(pad, 0, 0);
            Assert.Equal(TouchpadPointer.PrimaryButton, pad.PointerButtons);

            // A count above zero always opens a frame.
            PrecisionTouchpadReader.TakeButtons(pad, TouchpadPointer.SecondaryButton, 2);
            Assert.Equal(TouchpadPointer.SecondaryButton, pad.PointerButtons);

            // Nothing under way: a button-only report.
            pad.FrameSeen = 0;
            pad.FrameLifted = 0;
            pad.FrameExpected = 0;
            PrecisionTouchpadReader.TakeButtons(pad, 0, 0);
            Assert.Equal(0, pad.PointerButtons);
        }

        /// <summary>A touchpad setting changed in Windows reaches a touchpad
        /// already taken within the refresh time, read off the thread that
        /// asked.</summary>
        [Fact]
        public void TheWindowsSettings_AreReadAgainOnASlowCadence_AwayFromThePoll()
        {
            var pad = Pad();
            pad.Mouse = new TouchpadMouse(TouchpadSettings.Default, 100, 60);
            pad.SettingsReadMs = 1000;
            var queued = new List<Action>();
            int reads = 0;
            var off = TouchpadSettings.Default with { Taps = false };
            TouchpadSettings Read() { reads++; return off; }
            var gate = new object();
            int due = 1000 + PrecisionTouchpadReader.TouchpadSettingsRefreshMs;

            PrecisionTouchpadReader.RefreshSettingsIfDue(pad, due - 1, gate, Read, queued.Add);
            Assert.Empty(queued);

            PrecisionTouchpadReader.RefreshSettingsIfDue(pad, due, gate, Read, queued.Add);
            PrecisionTouchpadReader.RefreshSettingsIfDue(pad, due, gate, Read, queued.Add);
            var work = Assert.Single(queued);
            Assert.Equal(0, reads);
            work();
            Assert.Equal(1, reads);

            // The answer reached the mouse: a quick touch no longer clicks.
            pad.Mouse.Frame(new (int, double, double)[] { (1, 50, 30) }, false, 0);
            pad.Mouse.Frame(ReadOnlySpan<(int, double, double)>.Empty, false, 80);
            Assert.Equal(0, pad.Mouse.Buttons(80));
        }

        [Fact]
        public void ASettingChanged_TakesEffectOnTheNextTouch()
        {
            var mouse = new TouchpadMouse(TouchpadSettings.Default, 100, 60);
            mouse.UseSettings(TouchpadSettings.Default with { Taps = false });
            mouse.Frame(new (int, double, double)[] { (1, 50, 30) }, false, 0);
            mouse.Frame(ReadOnlySpan<(int, double, double)>.Empty, false, 80);
            Assert.Equal(0, mouse.Buttons(80));
        }
    }

    /// <summary>How a keyboard or mouse on WinUSB is read: the read's length
    /// from the longest report, the configuration past 512 bytes, and the
    /// wheel by its Resolution Multiplier.</summary>
    public class TakenDeviceReadTests
    {
        private const string Path = @"\\?\usb#vid_046d&pid_c54d&mi_00#8&340f50e3&0&0000#{fd826c66-3556-4845-9436-0fe7580ded02}";

        /// <summary>Report 1: eight modifiers and a bitmap of 128 keys, 18
        /// bytes with its ID.</summary>
        private static readonly byte[] BitmapKeyboard =
        {
            0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,
            0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
            0x19, 0x00, 0x29, 0x7F, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x80, 0x81, 0x02,
            0xC0,
        };

        /// <summary>A mouse whose wheel and AC Pan each sit in a logical
        /// collection with a Resolution Multiplier of its own: the wheel's is
        /// 1 to 8 in the feature report's first byte, the pan's 1 to 4 in the
        /// second.</summary>
        private static readonly byte[] HiResMouse =
        {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00,
            0x05, 0x09, 0x19, 0x01, 0x29, 0x03, 0x15, 0x00, 0x25, 0x01, 0x95, 0x03, 0x75, 0x01, 0x81, 0x02,
            0x95, 0x01, 0x75, 0x05, 0x81, 0x01,
            0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x02, 0x81, 0x06,
            0xA1, 0x02,
            0x09, 0x48, 0x15, 0x00, 0x25, 0x01, 0x35, 0x01, 0x45, 0x08, 0x75, 0x02, 0x95, 0x01, 0xB1, 0x02,
            0x35, 0x00, 0x45, 0x00, 0x75, 0x06, 0xB1, 0x01,
            0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06,
            0xC0,
            0xA1, 0x02,
            0x09, 0x48, 0x15, 0x00, 0x25, 0x01, 0x35, 0x01, 0x45, 0x04, 0x75, 0x02, 0x95, 0x01, 0xB1, 0x02,
            0x35, 0x00, 0x45, 0x00, 0x75, 0x06, 0xB1, 0x01,
            0x05, 0x0C, 0x0A, 0x38, 0x02, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06,
            0xC0,
            0xC0, 0xC0,
        };

        private static FakeKeyboardUsb Usb(byte[] descriptor)
        {
            var configuration = (byte[])KeyboardDescriptors.TwoInterfaceConfiguration.Clone();
            configuration[25] = (byte)descriptor.Length;
            return new FakeKeyboardUsb { Number = 0, InPipe = 0x81, Configuration = configuration, Descriptor = descriptor };
        }

        /// <summary>usbhid sizes its read from the longest input report
        /// (usbhid/hid-core.c:1103,1157 with hid_find_max_report), so a report
        /// past one packet arrives whole. A pipe's packet stays the least a
        /// read asks for.</summary>
        [Fact]
        public void ARead_AsksForTheLongestReport_NeverLessThanAPacket()
        {
            var usb = Usb(BitmapKeyboard);
            using (var channel = WinUsbKeyboardChannel.Open(Path, usb))
            {
                Assert.NotNull(channel);
                Assert.Equal(18, channel.Reports.LongestInputReport);
                Assert.Equal(18, channel.ReadLength);
            }

            var wide = Usb(BitmapKeyboard);
            wide.PacketSize = 64;
            using var open = WinUsbKeyboardChannel.Open(Path, wide);
            Assert.Equal(64, open.ReadLength);

            Assert.Equal(8, HidKeyboardReports.Parse(KeyboardDescriptors.Boot).LongestInputReport);
            Assert.Equal(5, HidMouseReports.Parse(HiResMouse).LongestInputReport);
        }

        /// <summary>A configuration past the first read's 512 bytes is read
        /// again whole, so an interface past that point opens, as it does for
        /// the bind that was just made.</summary>
        [Fact]
        public void AConfigurationPast512Bytes_IsReadWhole()
        {
            var bytes = new List<byte> { 0x09, 0x02, 0x00, 0x00, 0x01, 0x01, 0x00, 0xA0, 0x32 };
            for (int block = 0; block < 3; block++)
            {
                bytes.Add(0xFF);
                bytes.Add(0x24);
                bytes.AddRange(new byte[0xFD]);
            }
            bytes.AddRange(new byte[] { 0x09, 0x04, 0x00, 0x00, 0x01, 0x03, 0x01, 0x01, 0x00 });
            bytes.AddRange(new byte[] { 0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, (byte)KeyboardDescriptors.Boot.Length, 0x00 });
            bytes.AddRange(new byte[] { 0x07, 0x05, 0x81, 0x03, 0x08, 0x00, 0x0A });
            bytes[2] = (byte)bytes.Count;
            bytes[3] = (byte)(bytes.Count >> 8);
            Assert.True(bytes.Count > WinUsbKeyboardChannel.MaxConfigurationLength);

            var usb = new FakeKeyboardUsb { Number = 0, InPipe = 0x81, Configuration = bytes.ToArray(), Descriptor = KeyboardDescriptors.Boot };
            using var channel = WinUsbKeyboardChannel.Open(Path, usb);
            Assert.NotNull(channel);
        }

        /// <summary>The wheel takes the multiplier of its own logical
        /// collection (hid-core.c:1162-1198), read from the mouse and never
        /// written.</summary>
        [Fact]
        public void TheWheelsMultiplier_IsReadFromItsOwnCollection()
        {
            var reports = HidMouseReports.Parse(HiResMouse);
            var feature = reports.WheelMultiplierFeature;
            Assert.Equal((0, 2, 2), (feature.BitOffset, feature.Size, feature.ReportLength));
            Assert.Equal(8, reports.EffectiveMultiplier(new byte[] { 0x01, 0x00 }));
            Assert.Equal(1, reports.EffectiveMultiplier(new byte[] { 0x00, 0x01 }));
            Assert.Equal(1, reports.EffectiveMultiplier(new byte[] { 0x01 }));
            Assert.Null(HidMouseReports.Parse(WinUsbMouseTests.BootMouse).WheelMultiplierFeature);

            var usb = Usb(HiResMouse);
            usb.FeatureReport = new byte[] { 0x01, 0x00 };
            using (var channel = WinUsbKeyboardChannel.Open(Path, usb, mouse: true))
            {
                Assert.Equal(8, channel.WheelMultiplier);
                Assert.Contains(usb.Journal, line => line == "in A1-01-00-03-00-00-02-00");
                Assert.DoesNotContain(usb.Journal, line => line.StartsWith("out 21-09", StringComparison.Ordinal));
            }

            // A mouse that refuses the read counts one to the notch.
            using var refused = WinUsbKeyboardChannel.Open(Path, Usb(HiResMouse), mouse: true);
            Assert.Equal(1, refused.WheelMultiplier);

            // No feature, no request.
            var plain = Usb(WinUsbMouseTests.BootMouse);
            using var boot = WinUsbKeyboardChannel.Open(Path, plain, mouse: true);
            Assert.Equal(1, boot.WheelMultiplier);
            Assert.DoesNotContain(plain.Journal, line => line.StartsWith("in A1-01", StringComparison.Ordinal));
        }

        /// <summary>Eight counts of a wheel set to eight are one notch, and a
        /// part of a notch waits for the rest (hid-input.c:1534-1538).</summary>
        [Fact]
        public void TheRow_DividesTheWheelByItsMultiplier()
        {
            var channel = new FakeKeyboardChannel(Path, WinUsbMouseTests.BootMouse, mouse: true) { WheelMultiplier = 8 };
            using var row = new WinUsbMouseDevice(new WinUsbKeyboard(Path, 1, 2, Guid.NewGuid(), "M", IsMouse: true), channel, start: false);
            for (int count = 0; count < 7; count++) row.Feed(new byte[] { 0, 0, 0, 0x01, 0 });
            Assert.Equal(0, row.PendingWheelForTest);
            row.Feed(new byte[] { 0, 0, 0, 0x01, 0 });
            Assert.Equal(1, row.PendingWheelForTest);

            var plain = new FakeKeyboardChannel(Path, WinUsbMouseTests.BootMouse, mouse: true);
            using var one = new WinUsbMouseDevice(new WinUsbKeyboard(Path, 1, 2, Guid.NewGuid(), "M", IsMouse: true), plain, start: false);
            one.Feed(new byte[] { 0, 0, 0, 0xFF, 0 });
            Assert.Equal(-1, one.PendingWheelForTest);
        }
    }

    /// <summary>A mouse row whose Raw Input handle left, and the mouse row a
    /// captured touchpad feeds.</summary>
    [Collection("SettingsManagerStatics")]
    public class TouchpadMouseRowTests : IDisposable
    {
        private const string MousePath = @"\\?\HID#GXTP5100&Col01#4&c834cd3&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
        private const string MouseId = @"HID\GXTP5100&Col01\4&c834cd3&0&0000";
        private const string TouchpadPath = @"\\?\HID#GXTP5100&Col02#4&c834cd3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string TouchpadId = @"HID\GXTP5100&Col02\4&c834cd3&0&0001";

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;

        public void Dispose() => SettingsManager.UserDevices = _devices;

        /// <summary>Only a touchpad's own mouse collection leaves the Raw
        /// Input list while its synthetic records keep arriving. A USB mouse
        /// that was unplugged goes offline, whatever the touchpad reader says.</summary>
        [Fact]
        public void OnlyATouchpadsMouseRow_StaysOnline_WhenItsHandleLeaves()
        {
            var row = new UserDevice { CapType = InputDeviceType.Mouse, DevicePath = MousePath };
            Assert.True(InputManager.StaysOnlineWhenHandleLeaves(true, row, _ => true));
            Assert.False(InputManager.StaysOnlineWhenHandleLeaves(true, row, _ => false));
            Assert.False(InputManager.StaysOnlineWhenHandleLeaves(false, row, _ => true));

            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            Assert.Contains("StaysOnlineWhenHandleLeaves(_ptpReader != null && _ptpReader.IsAvailable, ud, PadForge.Services.InputService.IsTouchpadMouseRow)", source);
        }

        private static TouchpadCapture Capture()
            => new(null, TouchpadPath, TouchpadId, "Touchpad", 1, 2, null, _ => throw new IOException(), _ => { }, () => true);

        /// <summary>The device tree read runs off the poll thread, the row is
        /// found on a later pass, and an empty answer is asked again after
        /// the retry time instead of staying for the capture's life.</summary>
        [Fact]
        public void TheTreeRead_RunsAwayFromThePoll_AndAnEmptyAnswerIsAskedAgain()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            var row = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Mouse, DevicePath = MousePath, IsOnline = true };
            SettingsManager.UserDevices.Items.Add(row);

            var queued = new Queue<Action>();
            long now = 1000;
            int reads = 0;
            var answer = new List<string>();
            var im = new InputManager
            {
                TouchpadMiceBeside = _ => { reads++; return answer; },
                TouchpadTreeRun = work => { queued.Enqueue(work); return Task.CompletedTask; },
                TouchpadTreeTicks = () => now,
            };
            using var capture = Capture();

            Assert.Null(im.FindTouchpadMouseRow(capture));
            Assert.Equal(0, reads);
            Assert.Single(queued);

            // Asked once while that read is due or under way.
            Assert.Null(im.FindTouchpadMouseRow(capture));
            Assert.Single(queued);

            queued.Dequeue()();
            Assert.Equal(1, reads);
            Assert.Null(im.FindTouchpadMouseRow(capture));
            Assert.Empty(queued);

            now += InputManager.TouchpadTreeRetryMs;
            answer = new List<string> { MouseId };
            Assert.Null(im.FindTouchpadMouseRow(capture));
            queued.Dequeue()();
            Assert.Same(row, im.FindTouchpadMouseRow(capture));
            Assert.Empty(queued);
        }
    }

    /// <summary>How a touchpad taken through HidHide goes back to Windows when
    /// the hide does not come off at the first try, was left by a session
    /// before, or has no row to go through.</summary>
    [Collection("SettingsManagerStatics")]
    public class TouchpadGiveBackTests : IDisposable
    {
        private const uint GetBlacklist = 0x80016008, SetBlacklist = 0x8001600C;
        private const string Path = @"\\?\HID#GXTP5100&Col02#4&c834cd3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string Id = @"HID\GXTP5100&Col02\4&c834cd3&0&0001";
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            HidHideController.IoSeam = null;
            HidHideController.ResetManagedForTests();
        }

        private sealed class Layout : ITouchpadLayout
        {
            public int ReportLength => 8;
            public void Dispose() { }
        }

        private sealed class Host : ITouchpadCaptureHost
        {
            internal Exception DescribeFails;
            public ITouchpadLayout Describe(TouchpadCapture capture) => DescribeFails != null ? throw DescribeFails : new Layout();
            public IntPtr Publish(TouchpadCapture capture, ITouchpadLayout layout) => new(-0x10001);
            public bool Feed(IntPtr key, ITouchpadLayout layout, IntPtr report, int length) => true;
            public void Withdraw(IntPtr key) { }
        }

        /// <summary>A stream that waits for its close.</summary>
        private sealed class Quiet : MemoryStream
        {
            private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken stop = default)
            {
                await _closed.Task.WaitAsync(stop);
                return 0;
            }
            protected override void Dispose(bool disposing)
            {
                _closed.TrySetResult(0);
                base.Dispose(disposing);
            }
        }

        private static async Task Until(Func<bool> condition)
        {
            using var timeout = new System.Threading.CancellationTokenSource(Bound);
            while (!condition()) await Task.Delay(5, timeout.Token);
        }

        private static TouchpadCapture Capture(Action restarted, Host host = null, Action<TouchpadCapture, int, bool, string> changed = null)
            => new(host ?? new Host(), Path, Id, "Touchpad", 1, 2, changed, _ => new Quiet(), _ => restarted(), () => true);

        private static UserDevice Row(bool taken) => new()
        {
            InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Touchpad, DevicePath = Path, TouchpadOnlyInPadForge = taken,
        };

        private static async Task<TouchpadCapture> ReadingThenPrepared(Action restarted)
        {
            var capture = Capture(restarted);
            capture.SetCapture(true);
            await Until(() => capture.IsReading);
            capture.PrepareForUnhide();
            return capture;
        }

        /// <summary>A restart under a hide that is still on leaves Windows'
        /// reopen refused, and with nothing to restart the collection once the
        /// hide does come off. So a refused sync restarts nothing, says so, and
        /// the next sync that lands restarts it once.</summary>
        [Fact]
        public async Task ARefusedGiveBack_RestartsNothingUntilTheHideIsOff()
        {
            int restarts = 0;
            using var capture = await ReadingThenPrepared(() => restarts++);
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path };

            Assert.True(InputService.StopCapturesAfterFailedSync(new[] { capture }, new[] { Row(false) }, requested));
            Assert.True(await Task.Run(() => capture.WaitForTransition(Bound)));
            Assert.Equal(0, restarts);
            Assert.Equal(TabletCaptureState.Failed, capture.CaptureState);

            capture.SetCapture(false);
            await Until(() => capture.CaptureState == TabletCaptureState.Shared);
            Assert.Equal(1, restarts);

            // A touchpad already given back is left alone by a later refusal.
            Assert.False(InputService.StopCapturesAfterFailedSync(new[] { capture }, new[] { Row(false) }, requested));
            Assert.Equal(TabletCaptureState.Shared, capture.CaptureState);
            capture.SetCapture(false);
            Assert.True(await Task.Run(() => capture.WaitForTransition(Bound)));
            Assert.Equal(1, restarts);
        }

        [Fact]
        public async Task ARefusedGiveBack_OfATablet_RestartsNothingUntilTheHideIsOff()
        {
            using var f = new TabletReportStateTests.Fixture();
            int restarts = 0;
            using var device = new PadForge.Engine.Tablets.WindowsTabletDevice(f.Decoder, null, () => new Quiet(), _ => restarts++, () => true);
            device.SetCapture(true);
            await Until(() => device.CaptureWasRequested && device.CaptureState == TabletCaptureState.WaitingForInput);
            device.PrepareForUnhide();

            var row = new UserDevice { InstanceGuid = device.InstanceGuid, HidHideEnabled = false };
            Assert.True(InputService.StopTabletsAfterFailedSync(new[] { device }, new[] { row }, new HashSet<Guid> { device.InstanceGuid }));
            await Task.Delay(50);
            Assert.Equal(0, restarts);
            Assert.Equal(TabletCaptureState.Failed, device.CaptureState);

            device.SetCapture(false);
            await Until(() => device.CaptureState == TabletCaptureState.Shared);
            Assert.Equal(1, restarts);
        }

        /// <summary>Reset to Defaults empties the rows and the blacklist with
        /// one write, so the capture of a touchpad whose row is gone gives it
        /// back itself. A hide still on holds that restart back.</summary>
        [Fact]
        public async Task ACaptureWithNoRow_GivesItsTouchpadBack_OnceItIsShown()
        {
            int restarts = 0;
            using var capture = await ReadingThenPrepared(() => restarts++);
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Id };

            InputService.GiveBackCapturesWithNoRow(new[] { capture }, Array.Empty<UserDevice>(), hidden, active: true);
            Assert.Equal(TabletCaptureState.Switching, capture.CaptureState);

            // A row still there is the row pass's to settle.
            InputService.GiveBackCapturesWithNoRow(new[] { capture }, new[] { Row(false) }, new HashSet<string>(), active: true);
            Assert.Equal(TabletCaptureState.Switching, capture.CaptureState);

            InputService.GiveBackCapturesWithNoRow(new[] { capture }, Array.Empty<UserDevice>(), new HashSet<string>(), active: true);
            await Until(() => capture.CaptureState == TabletCaptureState.Shared);
            Assert.Equal(1, restarts);
        }

        private sealed class Driver
        {
            internal readonly List<string> List = new();
            internal bool RefuseSet, RefuseGet;

            internal (bool ok, int bytes) Io(uint ioctl, byte[] input, byte[] output)
            {
                if (ioctl == GetBlacklist)
                {
                    if (RefuseGet) return (false, 0);
                    var text = System.Text.Encoding.Unicode.GetBytes(string.Concat(List.Select(e => e + "\0")) + "\0");
                    if (output == null || output.Length == 0) return (true, text.Length);
                    Array.Copy(text, output, text.Length);
                    return (true, text.Length);
                }
                if (ioctl == SetBlacklist)
                {
                    if (RefuseSet) return (false, 0);
                    List.Clear();
                    List.AddRange(System.Text.Encoding.Unicode.GetString(input).Split('\0', StringSplitOptions.RemoveEmptyEntries));
                    return (true, 0);
                }
                return (false, 0);
            }
        }

        /// <summary>A hide a session before left behind is in no managed set,
        /// and a give-back reports success only with it gone: its own entry
        /// off the list, the collection restarted, other entries left alone.</summary>
        [Fact]
        public void TakeHideOff_RemovesTheTouchpadsOwnEntry_WhoeverWroteIt()
        {
            var driver = new Driver();
            driver.List.AddRange(new[] { Id, @"HID\OTHER\1" });
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = driver.Io;
            var restarted = new List<string>();

            driver.RefuseSet = true;
            Assert.False(InputService.TakeHideOff(Id, restarted.Add));
            Assert.Empty(restarted);

            driver.RefuseSet = false;
            driver.RefuseGet = true;
            Assert.False(InputService.TakeHideOff(Id, restarted.Add));
            Assert.Empty(restarted);

            driver.RefuseGet = false;
            Assert.True(InputService.TakeHideOff(Id, restarted.Add));
            Assert.Equal(new[] { @"HID\OTHER\1" }, driver.List);
            Assert.Equal(new[] { Id }, restarted);

            // Nothing hidden, nothing to restart.
            Assert.True(InputService.TakeHideOff(Id, restarted.Add));
            Assert.Single(restarted);
        }

        /// <summary>A run's try: a hide off with a restart that failed leaves the
        /// restart owed, and the retry that finds no hide still pays it. A restart
        /// that went through owes nothing more. A refused write tries again. A
        /// control device that is not there answers apart from one that is
        /// busy.</summary>
        [Fact]
        public void ARunsTry_RemembersTheRestartItOwes()
        {
            var driver = new Driver();
            driver.List.AddRange(new[] { Id, @"HID\OTHER\1" });
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = driver.Io;
            var restarted = new List<string>();
            bool works = false;
            bool Restart(string node) { restarted.Add(node); return works; }
            bool owed = false;

            driver.RefuseSet = true;
            Assert.Equal(GiveBackRun.Attempt.TryAgain, GiveBackRun.TakeHideOffNow(Id, ref owed, Restart));
            Assert.False(owed);
            Assert.Empty(restarted);

            driver.RefuseSet = false;
            Assert.Equal(GiveBackRun.Attempt.TryAgain, GiveBackRun.TakeHideOffNow(Id, ref owed, Restart));
            Assert.True(owed);
            Assert.Equal(new[] { @"HID\OTHER\1" }, driver.List);

            works = true;
            Assert.Equal(GiveBackRun.Attempt.Done, GiveBackRun.TakeHideOffNow(Id, ref owed, Restart));
            Assert.False(owed);
            Assert.Equal(new[] { Id, Id }, restarted);

            // No hide and nothing owed: nothing restarts.
            Assert.Equal(GiveBackRun.Attempt.Done, GiveBackRun.TakeHideOffNow(Id, ref owed, Restart));
            Assert.Equal(2, restarted.Count);

            HidHideController.IoSeam = null;
            HidHideController.ControlOpenSeam = () => 2;
            try { Assert.Equal(GiveBackRun.Attempt.NothingAnswers, GiveBackRun.TakeHideOffNow(Id, ref owed, Restart)); }
            finally { HidHideController.ControlOpenSeam = null; }
            HidHideController.ControlOpenSeam = () => 5;
            try { Assert.Equal(GiveBackRun.Attempt.TryAgain, GiveBackRun.TakeHideOffNow(Id, ref owed, Restart)); }
            finally { HidHideController.ControlOpenSeam = null; }
        }

        /// <summary>No control device: no HidHide, so nothing is hidden. A
        /// control device another client holds is not an answer.</summary>
        [Fact]
        public void TakeHideOff_ReadsAPcWithNoHidHide_AsNothingHidden()
        {
            HidHideController.IoSeam = null;
            HidHideController.ControlOpenSeam = () => 2;
            try { Assert.True(InputService.TakeHideOff(Id, _ => throw new InvalidOperationException())); }
            finally { HidHideController.ControlOpenSeam = null; }

            HidHideController.ControlOpenSeam = () => 5;
            try { Assert.False(InputService.TakeHideOff(Id, _ => throw new InvalidOperationException())); }
            finally { HidHideController.ControlOpenSeam = null; }
        }

        /// <summary>The give-back asks HidHide before it reports, and a refused
        /// removal is retried on the status timer's lane.</summary>
        [Fact]
        public void TheGiveBack_ChecksTheHide_AndItsRetryRunsOnTheTimer()
        {
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int start = window.IndexOf("private async System.Threading.Tasks.Task<bool> SetTouchpadOnlyInPadForgeAsync", StringComparison.Ordinal);
            int apply = window.IndexOf("_inputService.ApplyAutomaticDeviceHiding();", start, StringComparison.Ordinal);
            int check = window.IndexOf("if (!on && !_inputService.TakeTouchpadHideOff(ud)) return false;", start, StringComparison.Ordinal);
            int wait = window.IndexOf("var timer = System.Diagnostics.Stopwatch.StartNew();", start, StringComparison.Ordinal);
            Assert.True(start > 0 && apply > start && check > apply && wait > check, $"{start} {apply} {check} {wait}");

            int timer = window.IndexOf("private void StartDriverStatusTimer()", StringComparison.Ordinal);
            int retry = window.IndexOf("_inputService?.RetryOwedGiveBacks();", timer, StringComparison.Ordinal);
            int stop = window.IndexOf("_driverStatusTimer.Start();", timer, StringComparison.Ordinal);
            Assert.True(retry > timer && stop > retry, $"{timer} {retry} {stop}");
        }

        private (PadForge.ViewModels.MainViewModel Vm, InputService Input, UserDevice Row) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            var ud = Row(true);
            SettingsManager.UserDevices.Items.Add(ud);
            var vm = new PadForge.ViewModels.MainViewModel();
            return (vm, new InputService(vm), ud);
        }

        private static async Task<(TouchpadCapture Capture, int Revision)> Failed()
        {
            int revision = -1;
            var capture = Capture(() => { }, new Host { DescribeFails = new IOException("gone") },
                (c, version, _, _) => { if (c.CaptureState == TabletCaptureState.Failed) revision = version; });
            capture.SetCapture(true);
            await Until(() => revision >= 0);
            return (capture, revision);
        }

        /// <summary>A USB or detachable touchpad that leaves fails its read,
        /// and that is no reason to clear its switch: it comes back taken. A
        /// touchpad still there that cannot be read goes back to Windows.</summary>
        [Fact]
        public async Task AFailedRead_KeepsTheSwitch_WhileTheNodeIsGone_AndRetriesOnceWhenItReturns()
        {
            var (_, input, ud) = Arrange();
            bool present = false;
            input.TouchpadNodePresent = _ => present;
            input.TouchpadGoneSettleMs = 0;
            var (capture, revision) = await Failed();
            using (capture)
            {
                await input.HandleTouchpadCaptureChanged(capture, revision, false, "gone");
                Assert.True(ud.TouchpadOnlyInPadForge);

                Assert.False(input.TakeTouchpadRetry(capture));
                present = true;
                Assert.True(input.TakeTouchpadRetry(capture));
                Assert.False(input.TakeTouchpadRetry(capture));

                await input.HandleTouchpadCaptureChanged(capture, revision, false, "unreadable");
                Assert.False(ud.TouchpadOnlyInPadForge);
            }
        }

        /// <summary>A killed session leaves the hide of a touchpad it kept on.
        /// A start with no engine takes it off, and a start with one leaves it
        /// for the capture to come. A nobody-kept touchpad goes back either
        /// way, and a refused removal is retried.</summary>
        [Fact]
        public void AStart_TakesOffAHideAKilledSessionLeft_UnlessAnEngineIsComing()
        {
            var (_, input, ud) = Arrange();
            var driver = new Driver();
            driver.List.Add(Id);
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = driver.Io;

            input.GiveBackTouchpadsNobodyKept(engineStarting: true);
            Assert.Contains(Id, driver.List);
            Assert.True(ud.TouchpadOnlyInPadForge);

            driver.RefuseSet = true;
            input.GiveBackTouchpadsNobodyKept(engineStarting: false);
            Assert.Contains(Id, input.TouchpadHidesOwed);
            Assert.True(ud.TouchpadOnlyInPadForge);

            driver.RefuseSet = false;
            input.GiveBackTouchpadsNobodyKept(engineStarting: false);
            Assert.DoesNotContain(Id, driver.List);
            Assert.True(ud.TouchpadOnlyInPadForge);

            driver.List.Add(Id);
            ud.TouchpadKeepPending = true;
            input.GiveBackTouchpadsNobodyKept(engineStarting: true);
            Assert.DoesNotContain(Id, driver.List);
            Assert.False(ud.TouchpadOnlyInPadForge || ud.TouchpadKeepPending);
        }

        /// <summary>A hide the release could not take off is taken off on the
        /// timer's lane, so a client that held the control device for a while
        /// does not leave the touchpad dead until the next boot.</summary>
        [Fact]
        public void AHideTheReleaseLeft_IsTakenOffOnARetry()
        {
            var (_, input, _) = Arrange();
            var driver = new Driver { RefuseSet = true };
            driver.List.Add(Id);
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = driver.Io;
            input.TouchpadHidesOwed.Add(Id);

            input.RetryOwedGiveBacks();
            Assert.Contains(Id, driver.List);
            Assert.Contains(Id, input.TouchpadHidesOwed);

            driver.RefuseSet = false;
            input.RetryOwedGiveBacks();
            Assert.DoesNotContain(Id, driver.List);
            Assert.Empty(input.TouchpadHidesOwed);
        }

        /// <summary>Whatever ends a touchpad's task ends this session's record
        /// of it, so the next hide registers a task of its own: a kill after
        /// that hide still has a boot task to give the touchpad back.</summary>
        [Fact]
        public void AGiveBackThatDeletesTheTask_LeavesTheNextHideToRegisterAnother()
        {
            var (_, input, ud) = Arrange();
            var driver = new Driver();
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = driver.Io;
            var deleted = new List<string>();
            GiveBackTask.RunSeam = args => { deleted.Add(args); return 0; };
            try
            {
                void AssertTheNextHideRegisters()
                {
                    long retryAt = 0;
                    int asks = 0;
                    Assert.True(InputService.CoverTouchpadHide(Id, input._touchpadGiveBackTasks, ref retryAt, 0,
                        _ => { asks++; return GiveBackTask.Outcome.Registered; }, new List<string>()));
                    Assert.Equal(1, asks);
                }
                string delete = $"/Delete /TN \"{GiveBackTask.NameOf(Id)}\" /F";

                // The retry of a hide a release left on.
                input._touchpadGiveBackTasks.Add(Id);
                driver.List.Add(Id);
                input.TouchpadHidesOwed.Add(Id);
                input.RetryOwedGiveBacks();
                Assert.Equal(delete, Assert.Single(deleted));
                AssertTheNextHideRegisters();

                // The check a give-back ends on.
                deleted.Clear();
                driver.List.Add(Id);
                Assert.True(input.TakeTouchpadHideOff(ud));
                Assert.Equal(delete, Assert.Single(deleted));
                AssertTheNextHideRegisters();

                // A start's give-back of a touchpad nobody kept.
                deleted.Clear();
                driver.List.Add(Id);
                ud.TouchpadKeepPending = true;
                input.GiveBackTouchpadsNobodyKept(engineStarting: true);
                Assert.Contains(delete, deleted);
                AssertTheNextHideRegisters();
            }
            finally
            {
                GiveBackTask.RunSeam = null;
            }
        }
    }

    /// <summary>The one-shot boot task that gives a device back when no
    /// PadForge is left to. The program itself never runs from here. These pin
    /// the exact command lines it would be handed. The commands' text ran by
    /// hand against fake packages, with stand-ins for pnputil, schtasks and
    /// the CLI, each exiting 0 and with an error.</summary>
    [Collection("SettingsManagerStatics")]
    public class GiveBackTaskTests : IDisposable
    {
        private const string Node = @"USB\VID_046D&PID_C31C\5&2A&0&3";
        private const string Bind = @"USB\VID_046D&PID_C31C";
        private const string Touchpad = @"HID\GXTP5100&Col02\4&c834cd3&0&0001";
        private const string System32 = @"C:\Windows\System32";

        public void Dispose()
        {
            // What a test registered holds a pipe of this process until its task
            // is deleted.
            GiveBackTask.RunSeam = _ => 0;
            GiveBackTask.Delete(Bind);
            GiveBackTask.Delete(Touchpad);
            GiveBackTask.RunSeam = null;
            GiveBackTask.FileExistsSeam = null;
            GiveBackTask.ExePathSeam = null;
            GiveBackTask.ExeSizeSeam = null;
            GiveBackTask.ExtractBaseSeam = null;
        }

        /// <summary>The action, whole: the wait, the question whether the task
        /// stands, the run, and what stands behind the run. A PC with no CLI has
        /// nothing behind it, and an exe that cannot be quoted has no run.</summary>
        [Fact]
        public void TheAction_WaitsOnThePipe_ThenRunsPadForge_ThenFallsBack()
        {
            const string exe = @"C:\PadForge\PadForge.exe";
            string name = GiveBackTask.NameOf(Touchpad);
            string pipe = @"\\.\pipe\" + name.Replace(' ', '_');
            string wait = @"C:\Windows\System32\find.exe /v """" < """ + pipe + @""" >nul 2>&1 "
                + @"& C:\Windows\System32\schtasks.exe /Query /TN """ + name + @""" >nul 2>&1 && ( ";
            string run = @"""" + exe + @""" --give-back-touchpad """ + Touchpad + @""" """ + name + @"""";
            GiveBackTask.ExeSizeSeam = _ => -1;
            GiveBackTask.ExtractBaseSeam = () => null;
            Assert.Equal(wait + run + " || ( the fallback ) )",
                GiveBackTask.Action(name, exe, GiveBackRun.TouchpadSwitch, Touchpad, "the fallback", System32));
            Assert.Equal(wait + run + " )",
                GiveBackTask.Action(name, exe, GiveBackRun.TouchpadSwitch, Touchpad, null, System32));

            // The run goes only to the exe that registered the task, and it starts from
            // what that exe already extracted.
            GiveBackTask.ExeSizeSeam = _ => 12345;
            GiveBackTask.ExtractBaseSeam = () => @"C:\Users\someone\Temp\.net";
            string guarded = @"for %I in (""" + exe + @""") do if ""%~zI""==""12345"" ( set ""DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\Users\someone\Temp\.net"" & " + run;
            Assert.Equal(wait + guarded + " || ( the fallback ) ) else ( the fallback ) )",
                GiveBackTask.Action(name, exe, GiveBackRun.TouchpadSwitch, Touchpad, "the fallback", System32));
            Assert.Equal(wait + guarded + " ) )",
                GiveBackTask.Action(name, exe, GiveBackRun.TouchpadSwitch, Touchpad, null, System32));
            GiveBackTask.ExeSizeSeam = _ => -1;
            GiveBackTask.ExtractBaseSeam = () => null;
            Assert.Equal(wait + "the fallback )",
                GiveBackTask.Action(name, @"C:\50%\PadForge.exe", GiveBackRun.TouchpadSwitch, Touchpad, "the fallback", System32));
            Assert.Equal(wait + "the fallback )",
                GiveBackTask.Action(name, null, GiveBackRun.TouchpadSwitch, Touchpad, "the fallback", System32));
        }

        /// <summary>A registration trigger starts the wait at once, only when the
        /// app holds the pipe for it, and the wait may last as long as the app.
        /// With no pipe the wait would end at once, and the device would go back
        /// under a running app.</summary>
        [Fact]
        public void TheDefinition_StartsTheWaitAtOnce_OnlyOnAPipeTheAppHolds()
        {
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            foreach (bool held in new[] { true, false })
            {
                var task = System.Xml.Linq.XDocument.Parse(GiveBackTask.TaskXml("x", System32, held)).Root;
                var triggers = task.Element(ns + "Triggers");
                Assert.NotNull(triggers.Element(ns + "BootTrigger"));
                Assert.Equal(held, triggers.Element(ns + "RegistrationTrigger") != null);
                Assert.Equal("PT0S", task.Element(ns + "Settings").Element(ns + "ExecutionTimeLimit").Value);
                // A registration starts its own wait whatever an earlier one is doing.
                Assert.Equal("Parallel", task.Element(ns + "Settings").Element(ns + "MultipleInstancesPolicy").Value);
                // A boot's instance has no wait to last, and an exe older than the
                // task starts the whole app.
                Assert.Equal("PT10M", triggers.Element(ns + "BootTrigger").Element(ns + "ExecutionTimeLimit").Value);
                Assert.True(GiveBackRun.RunLimitMs < 10 * 60 * 1000);
            }
        }

        /// <summary>The action as Windows would run it: from the definition, by a
        /// real cmd.exe and a real find.exe on a real pipe. Only schtasks,
        /// PadForge and the fallback are stand-ins, so no task, driver or hide is
        /// touched. These run by the hundred on one machine, so they have
        /// nothing to do with its devices.</summary>
        private sealed class RealAction : IDisposable
        {
            private readonly string dir = Path.Combine(Path.GetTempPath(), "PadForgeTests", "action-" + Guid.NewGuid().ToString("N"));
            private Process process;
            public string Exe => Path.Combine(dir, "padforge stand-in.cmd");
            public string Log => Path.Combine(dir, "log.txt");

            public RealAction(int schtasksExit, int exeExit, bool exeThere = true)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Schtasks, $"@echo schtasks %*>>\"{Log}\"\r\n@exit /b {schtasksExit}\r\n");
                if (exeThere) File.WriteAllText(Exe, $"@echo exe %*>>\"{Log}\"\r\n@exit /b {exeExit}\r\n");
            }

            private string Schtasks => Path.Combine(dir, "schtasks stand-in.cmd");

            public string[] Lines => File.Exists(Log) ? File.ReadAllLines(Log) : Array.Empty<string>();

            /// <summary>The definition's action, with the stand-ins put in. Refuses
            /// a line that could still reach the real programs.</summary>
            public void Start(string definition)
            {
                System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var exec = System.Xml.Linq.XDocument.Parse(definition).Root.Element(ns + "Actions").Element(ns + "Exec");
                string arguments = exec.Element(ns + "Arguments").Value
                    .Replace(System32 + @"\schtasks.exe", "\"" + Schtasks + "\"", StringComparison.OrdinalIgnoreCase)
                    .Replace(Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\schtasks.exe", "\"" + Schtasks + "\"", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"schtasks.exe", arguments, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"pnputil.exe", arguments, StringComparison.OrdinalIgnoreCase);
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = exec.Element(ns + "Command").Value,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }

            public bool Waiting(int ms) => !process.WaitForExit(ms);

            public void Finished()
            {
                Assert.True(process.WaitForExit(60000), "The action did not end. It logged: " + string.Join(" | ", Lines));
            }

            public void Dispose()
            {
                try { if (process is { HasExited: false }) process.Kill(); } catch { }
                process?.Dispose();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>Registers through the production path with no schtasks, and
        /// returns the definition schtasks would read.</summary>
        private static string RegisterForTheAction(Func<GiveBackTask.Outcome> register)
        {
            string definition = null;
            GiveBackTask.RunSeam = args =>
            {
                int at = args.IndexOf("/XML \"", StringComparison.Ordinal);
                if (at >= 0)
                {
                    at += 6;
                    // Past the byte order mark the definition carries.
                    definition = System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(args[at..args.IndexOf('"', at)])[2..]);
                }
                return 0;
            };
            Assert.Equal(GiveBackTask.Outcome.Registered, register());
            return definition;
        }

        /// <summary>The wait lasts as long as the app holds the pipe. The app's
        /// delete ends it, and with the task gone nothing of PadForge starts: a
        /// normal exit and a Keep cost one schtasks call.</summary>
        [Fact]
        public void TheAction_WaitsWhileThePipeIsHeld_AndAskedTaskGone_StartsNothing()
        {
            using var action = new RealAction(schtasksExit: 1, exeExit: 0);
            GiveBackTask.ExePathSeam = () => action.Exe;
            GiveBackTask.FileExistsSeam = _ => false;
            action.Start(RegisterForTheAction(() => GiveBackTask.RegisterTouchpad(Touchpad, null)));

            Assert.True(action.Waiting(1500), "The wait ended with the pipe held.");
            Assert.Empty(action.Lines);

            GiveBackTask.Delete(Touchpad);
            action.Finished();
            string query = "schtasks /Query /TN \"" + GiveBackTask.NameOf(Touchpad) + "\"";
            Assert.Equal(query, Assert.Single(action.Lines));
        }

        /// <summary>A task that stands when the wait ends owes a device. PadForge
        /// starts with the ID and the task's name.</summary>
        [Fact]
        public void TheAction_WithItsTaskStanding_RunsPadForge_AndNothingElse()
        {
            using var action = new RealAction(schtasksExit: 0, exeExit: 0);
            GiveBackTask.ExePathSeam = () => action.Exe;
            GiveBackTask.FileExistsSeam = _ => false;
            string definition = RegisterForTheAction(() => GiveBackTask.RegisterTouchpad(Touchpad, null));
            GiveBackTask.Delete(Touchpad);

            action.Start(definition);
            action.Finished();
            string name = GiveBackTask.NameOf(Touchpad);
            Assert.Equal(new[]
            {
                "schtasks /Query /TN \"" + name + "\"",
                "exe --give-back-touchpad \"" + Touchpad + "\" \"" + name + "\"",
            }, action.Lines);
        }

        /// <summary>A run that did not get through, and an exe that is gone, both
        /// fall to what the task holds behind them, and a good run does not. The
        /// real keyboard fallback's text runs here, nested in the action, against
        /// a package in a test folder.</summary>
        [Theory]
        [InlineData(true, 1)]
        [InlineData(false, 1)]
        [InlineData(true, 0)]
        public void TheAction_FallsBack_FromAFailedRunOrAMissingExe(bool exeThere, int exeExit)
        {
            using var action = new RealAction(schtasksExit: 0, exeExit, exeThere);
            string name = GiveBackTask.NameOf(Bind);
            string infs = Path.Combine(Path.GetDirectoryName(action.Log), "INF");
            Directory.CreateDirectory(infs);
            File.WriteAllText(Path.Combine(infs, "oem7.inf"), VendorUsbDriverInstaller.BuildInf(Bind, "Keyboard"));
            File.WriteAllText(Path.Combine(infs, "oem8.inf"), VendorUsbDriverInstaller.BuildInf(Bind + "&MI_00", "Keyboard"));
            string pnputil = Path.Combine(Path.GetDirectoryName(action.Log), "pnputil stand-in.cmd");
            File.WriteAllText(pnputil, $"@echo pnputil %*>>\"{action.Log}\"\r\n@exit /b 0\r\n");
            string fallback = GiveBackTask.KeyboardCommand(name, Bind, infs, System32)
                .Replace(System32 + @"\pnputil.exe", "\"" + pnputil + "\"");
            string command = GiveBackTask.Action(name, action.Exe, GiveBackRun.PackageSwitch, Bind, fallback, System32);

            action.Start(GiveBackTask.TaskXml(command, System32, pipeHeld: false));
            action.Finished();

            string query = "schtasks /Query /TN \"" + name + "\"";
            var expected = new List<string> { query };
            if (exeThere) expected.Add("exe --give-back-package \"" + Bind + "\" \"" + name + "\"");
            if (!exeThere || exeExit != 0)
            {
                expected.Add("pnputil /delete-driver \"oem7.inf\" /uninstall /force");
                expected.Add("schtasks /Delete /TN \"" + name + "\" /F");
            }
            Assert.Equal(expected, action.Lines);
        }

        /// <summary>A task outlives the exe that made it when the exe is replaced. An older
        /// one has no run mode and would start the whole app as SYSTEM, so any other file
        /// falls to what stands behind the run, and an exe that is gone does too.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheAction_RunsOnlyTheExeThatMadeIt(bool replaced)
        {
            using var action = new RealAction(schtasksExit: 0, exeExit: 0);
            string name = GiveBackTask.NameOf(Touchpad);
            string marker = Path.Combine(Path.GetDirectoryName(action.Log), "fallback ran.txt");
            string command = GiveBackTask.Action(name, action.Exe, GiveBackRun.TouchpadSwitch, Touchpad,
                $"echo fallback>\"{marker}\"", System32);
            if (replaced) File.AppendAllText(action.Exe, "@rem another build\r\n");
            else File.Delete(action.Exe);

            action.Start(GiveBackTask.TaskXml(command, System32, pipeHeld: false));
            action.Finished();
            Assert.DoesNotContain(action.Lines, line => line.StartsWith("exe ", StringComparison.Ordinal));
            Assert.True(File.Exists(marker), "the fallback ran");
        }

        /// <summary>The exe that made it runs, with what it extracted named for it.</summary>
        [Fact]
        public void TheAction_RunsTheExeThatMadeIt_FromWhatItExtracted()
        {
            using var action = new RealAction(schtasksExit: 0, exeExit: 0);
            string name = GiveBackTask.NameOf(Touchpad);
            string extract = Path.Combine(Path.GetDirectoryName(action.Log), "extracted");
            GiveBackTask.ExtractBaseSeam = () => extract;
            File.AppendAllText(action.Exe, $"\r\n");
            File.WriteAllText(action.Exe, $"@echo base %DOTNET_BUNDLE_EXTRACT_BASE_DIR%>>\"{action.Log}\"\r\n@echo exe %*>>\"{action.Log}\"\r\n@exit /b 0\r\n");
            string command = GiveBackTask.Action(name, action.Exe, GiveBackRun.TouchpadSwitch, Touchpad, null, System32);

            action.Start(GiveBackTask.TaskXml(command, System32, pipeHeld: false));
            action.Finished();
            Assert.Contains("base " + extract, action.Lines);
            Assert.Contains(action.Lines, line => line.StartsWith("exe ", StringComparison.Ordinal));
        }

        /// <summary>A PC with no CLI has nothing behind a failed run, and its task
        /// stays for the next boot. A run's exit never deletes it.</summary>
        [Fact]
        public void TheAction_WithNothingBehindAFailedRun_LeavesItsTask()
        {
            using var action = new RealAction(schtasksExit: 0, exeExit: 1);
            GiveBackTask.ExePathSeam = () => action.Exe;
            GiveBackTask.FileExistsSeam = _ => false;
            string definition = RegisterForTheAction(() => GiveBackTask.RegisterTouchpad(Touchpad, null));
            GiveBackTask.Delete(Touchpad);

            action.Start(definition);
            action.Finished();
            Assert.DoesNotContain(action.Lines, line => line.Contains("/Delete", StringComparison.Ordinal));
            Assert.Contains(action.Lines, line => line.StartsWith("exe ", StringComparison.Ordinal));
        }

        /// <summary>A registration holds its pipe, then waits for a run giving the
        /// device back, then registers. A run that takes its turn after the look sees
        /// the pipe. The real turn is a pipe of one instance.</summary>
        [Fact]
        public async Task ARunAndARegistration_TakeTurns()
        {
            string name = GiveBackTask.NameOf(Touchpad);
            using (var first = GiveBackRun.TakeTheTurn(name))
            {
                Assert.NotNull(first);
                Assert.Null(GiveBackRun.TakeTheTurn(name));
            }
            using (var again = GiveBackRun.TakeTheTurn(name)) Assert.NotNull(again);

            var turn = GiveBackRun.TakeTheTurn(name);
            GiveBackTask.ExePathSeam = () => @"C:\PadForge\PadForge.exe";
            GiveBackTask.FileExistsSeam = _ => false;
            int created = 0;
            GiveBackTask.RunSeam = _ => { Interlocked.Increment(ref created); return 0; };
            using var looked = new ManualResetEventSlim(false);
            using var finished = new ManualResetEventSlim(false);
            GiveBackTask.RunFinishSeam = task =>
            {
                Assert.True(GiveBackTask.AnAppHolds(task), "the pipe stands ahead of the look");
                Assert.Equal(0, Volatile.Read(ref created));
                looked.Set();
                Assert.True(finished.Wait(30000));
            };
            try
            {
                var registering = Task.Run(() => GiveBackTask.RegisterTouchpad(Touchpad, null));
                Assert.True(looked.Wait(30000));
                Assert.Equal(0, Volatile.Read(ref created));
                turn.Dispose();
                finished.Set();
                Assert.Equal(GiveBackTask.Outcome.Registered, await registering);
                Assert.Equal(1, Volatile.Read(ref created));
            }
            finally
            {
                GiveBackTask.RunFinishSeam = null;
            }
        }

        /// <summary>The look at a run's pipe is bounded, and ends as soon as the run
        /// does. It runs on the thread of a hide's cover.</summary>
        [Fact]
        public void ARegistration_WaitsForARunOnlySoLong()
        {
            string name = GiveBackTask.NameOf(Bind);
            GiveBackTask.ExePathSeam = () => @"C:\PadForge\PadForge.exe";
            GiveBackTask.RunSeam = _ => 0;
            var clock = Stopwatch.StartNew();
            Assert.True(GiveBackTask.RegisterKeyboard(Bind, null));
            Assert.True(clock.ElapsedMilliseconds < GiveBackTask.RunFinishWaitMs, "nothing to wait for, " + clock.ElapsedMilliseconds);
            GiveBackTask.Delete(Bind);

            using var turn = GiveBackRun.TakeTheTurn(name);
            clock.Restart();
            Assert.True(GiveBackTask.RegisterKeyboard(Bind, null));
            Assert.True(clock.ElapsedMilliseconds >= GiveBackTask.RunFinishWaitMs - 100, "waited " + clock.ElapsedMilliseconds);
        }

        /// <summary>Only schtasks saying no says a task is gone. One that did not
        /// answer says nothing: a wait's pipe stays, and a run goes on with the
        /// device it may owe.</summary>
        [Fact]
        public void ATaskStands_UnlessSchtasksSaysItDoesNot()
        {
            GiveBackTask.RunSeam = _ => 0;
            Assert.True(GiveBackTask.Stands("T"));
            GiveBackTask.RunSeam = _ => 1;
            Assert.False(GiveBackTask.Stands("T"));
            GiveBackTask.RunSeam = _ => -1;
            Assert.True(GiveBackTask.Stands("T"));

            string name = GiveBackTask.NameOf(Touchpad);
            GiveBackTask.ExePathSeam = () => @"C:\PadForge\PadForge.exe";
            GiveBackTask.FileExistsSeam = _ => false;
            GiveBackTask.RunSeam = _ => 0;
            Assert.Equal(GiveBackTask.Outcome.Registered, GiveBackTask.RegisterTouchpad(Touchpad, null));
            GiveBackTask.RunSeam = _ => -1;
            GiveBackTask.Delete(Touchpad);
            Assert.True(GiveBackTask.AnAppHolds(name));
        }

        /// <summary>A task that could not be deleted still stands, so its wait
        /// goes on until the app ends. A registration that failed takes back its
        /// own pipe, and one that stood before it keeps its own.</summary>
        [Fact]
        public void APipe_LastsAsLongAsItsTask()
        {
            string name = GiveBackTask.NameOf(Touchpad);
            GiveBackTask.ExePathSeam = () => @"C:\PadForge\PadForge.exe";
            GiveBackTask.FileExistsSeam = _ => false;
            Assert.False(GiveBackTask.AnAppHolds(name));

            GiveBackTask.RunSeam = _ => 1;
            Assert.Equal(GiveBackTask.Outcome.Failed, GiveBackTask.RegisterTouchpad(Touchpad, null));
            Assert.False(GiveBackTask.AnAppHolds(name));

            GiveBackTask.RunSeam = _ => 0;
            Assert.Equal(GiveBackTask.Outcome.Registered, GiveBackTask.RegisterTouchpad(Touchpad, null));
            Assert.True(GiveBackTask.AnAppHolds(name));

            GiveBackTask.RunSeam = _ => 1;
            Assert.Equal(GiveBackTask.Outcome.Failed, GiveBackTask.RegisterTouchpad(Touchpad, null));
            Assert.True(GiveBackTask.AnAppHolds(name));

            // schtasks cannot delete it and says it still stands.
            GiveBackTask.RunSeam = args => args.StartsWith("/Query", StringComparison.Ordinal) ? 0 : 1;
            GiveBackTask.Delete(Touchpad);
            Assert.True(GiveBackTask.AnAppHolds(name));

            // It could not delete a task that was not there.
            GiveBackTask.RunSeam = _ => 1;
            GiveBackTask.Delete(Touchpad);
            Assert.False(GiveBackTask.AnAppHolds(name));
        }

        private sealed class RunLog
        {
            public readonly List<string> Steps = new();
            public int HidesTried, NothingUntil = -1;
            public GiveBackRun.Attempt Answer = GiveBackRun.Attempt.Done;
            public bool Stands = true, First = true, AppHolds, Updating, Installed;

            public GiveBackRun.Seams Seams() => new()
            {
                AppHolds = task => { Steps.Add("holds " + task); return AppHolds; },
                TaskStands = task => { Steps.Add("stands " + task); return Stands; },
                UpdateInProgress = () => { Steps.Add("update"); return Updating; },
                TakeHideOff = id =>
                {
                    Steps.Add("hide " + id);
                    return HidesTried++ < NothingUntil ? GiveBackRun.Attempt.NothingAnswers : Answer;
                },
                RemovePackages = id => { Steps.Add("package " + id); return Answer; },
                DeleteTask = task => Steps.Add("delete " + task),
                HidHideInstalled = () => Installed,
                Wait = (ms, _) => Steps.Add("sleep " + ms),
                OneAtATime = _ => First ? new MemoryStream() : null,
            };
        }

        /// <summary>A run asks whether an app holds the device and whether its
        /// task stands, gives the device back and deletes the task, in that
        /// order.</summary>
        [Theory]
        [InlineData(GiveBackRun.TouchpadSwitch, "hide ")]
        [InlineData(GiveBackRun.PackageSwitch, "package ")]
        public void ARun_GivesBack_AndDeletesItsTask(string runSwitch, string step)
        {
            var log = new RunLog();
            Assert.Equal(0, GiveBackRun.Run(new[] { runSwitch, "ID", "T" }, log.Seams()));
            Assert.Equal(new[] { "holds T", "stands T", "update", step + "ID", "holds T", "delete T" }, log.Steps);
        }

        /// <summary>A Keep or a give-back deleted the task: nothing is owed, and
        /// nothing falls back.</summary>
        [Fact]
        public void ARun_WhoseTaskIsGone_GivesNothingBack()
        {
            var log = new RunLog { Stands = false, Updating = true };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.PackageSwitch, "ID", "T" }, log.Seams()));
            Assert.Equal(new[] { "holds T", "stands T" }, log.Steps);
        }

        /// <summary>An app started and took the device again before this run got
        /// through. It holds the pipe, and its registration started a wait of its own
        /// (TheDefinition: instances run in parallel), so this run takes nothing from
        /// under it and nothing falls back.</summary>
        [Fact]
        public void ARun_LeavesADeviceAnAppTookAgain()
        {
            var log = new RunLog { AppHolds = true };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, log.Seams()));
            Assert.Equal(new[] { "holds T" }, log.Steps);

            // Between tries too.
            var later = new RunLog { Answer = GiveBackRun.Attempt.TryAgain };
            var seams = later.Seams();
            seams.Wait = (_, _) => later.AppHolds = true;
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, seams));
            Assert.Equal(1, later.HidesTried);

            // During the attempt: the task is that app's now, and stays.
            var during = new RunLog();
            var seamsDuring = during.Seams();
            seamsDuring.TakeHideOff = _ =>
            {
                during.AppHolds = true;
                return GiveBackRun.Attempt.Done;
            };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, seamsDuring));
            Assert.DoesNotContain(during.Steps, s => s.StartsWith("delete", StringComparison.Ordinal));
        }

        /// <summary>An update's helper replaces this exe now, so a run holding it
        /// leaves at once, and the tools behind it take the device back.</summary>
        [Fact]
        public void ARun_LeavesTheExeToAnUpdate_AndSaysSo()
        {
            var log = new RunLog { Updating = true };
            Assert.Equal(1, GiveBackRun.Run(new[] { GiveBackRun.PackageSwitch, "ID", "T" }, log.Seams()));
            Assert.Equal(new[] { "holds T", "stands T", "update" }, log.Steps);
        }

        /// <summary>A give-back that does not get through is tried for two
        /// minutes, and its task stays for what falls back and the next boot.</summary>
        [Fact]
        public void ARun_ThatNeverGetsThrough_LeavesItsTask_AndSaysSo()
        {
            var log = new RunLog { Answer = GiveBackRun.Attempt.TryAgain };
            Assert.Equal(1, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, log.Seams()));
            Assert.Equal(GiveBackRun.Tries, log.HidesTried);
            Assert.Equal(GiveBackRun.Tries - 1, log.Steps.Count(s => s.StartsWith("sleep", StringComparison.Ordinal)));
            Assert.DoesNotContain(log.Steps, s => s.StartsWith("delete", StringComparison.Ordinal));
            Assert.Equal(120, GiveBackRun.Tries * GiveBackRun.TryEveryMs / 1000);
        }

        /// <summary>HidHide's control device can still be coming at a boot, so its
        /// absence ends a run only at the last try: a driver that is gone took its
        /// hides with it.</summary>
        [Fact]
        public void ARun_TakesNoControlDevice_AsNoHide_AtTheLastTryAlone()
        {
            var log = new RunLog { NothingUntil = GiveBackRun.Tries, Answer = GiveBackRun.Attempt.NothingAnswers };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, log.Seams()));
            Assert.Equal(GiveBackRun.Tries, log.HidesTried);
            Assert.Equal("delete T", log.Steps[^1]);

            var soon = new RunLog { NothingUntil = 2 };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, soon.Seams()));
            Assert.Equal(3, soon.HidesTried);

            // A driver still installed that never answered keeps its hides for when
            // it does, so its task stays and what stands behind the run tries.
            var installed = new RunLog { Installed = true, NothingUntil = GiveBackRun.Tries, Answer = GiveBackRun.Attempt.NothingAnswers };
            Assert.Equal(1, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, installed.Seams()));
            Assert.DoesNotContain(installed.Steps, s => s.StartsWith("delete", StringComparison.Ordinal));
        }

        /// <summary>What stands behind a run looks at nothing, so a run that ends with
        /// an app holding the device says it is done. The look between tries ends the
        /// wait as soon as an app holds it.</summary>
        [Fact]
        public void ARun_LeavesNothingToFallBackOn_ForADeviceAnAppTook()
        {
            var last = new RunLog { Answer = GiveBackRun.Attempt.TryAgain };
            var seams = last.Seams();
            int tries = 0;
            seams.TakeHideOff = _ =>
            {
                if (++tries == GiveBackRun.Tries) last.AppHolds = true;
                return GiveBackRun.Attempt.TryAgain;
            };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, seams));

            var thrown = new RunLog();
            var seamsThrown = thrown.Seams();
            seamsThrown.TaskStands = _ =>
            {
                thrown.AppHolds = true;
                throw new InvalidOperationException("no schtasks");
            };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, seamsThrown));

            string name = GiveBackTask.NameOf(Touchpad);
            GiveBackTask.ExePathSeam = () => @"C:\PadForge\PadForge.exe";
            GiveBackTask.FileExistsSeam = _ => false;
            GiveBackTask.RunSeam = _ => 0;
            Assert.Equal(GiveBackTask.Outcome.Registered, GiveBackTask.RegisterTouchpad(Touchpad, null));
            var clock = Stopwatch.StartNew();
            GiveBackRun.WaitUnlessAnAppHolds(30000, name);
            Assert.True(clock.ElapsedMilliseconds < 10000, "waited " + clock.ElapsedMilliseconds);
        }

        /// <summary>Two waits can end at one app's end. The second run leaves it to
        /// the first.</summary>
        [Fact]
        public void ASecondRun_LeavesItToTheFirst()
        {
            var log = new RunLog { First = false };
            Assert.Equal(0, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, log.Seams()));
            Assert.Empty(log.Steps);
        }

        /// <summary>A run that dies falls to what stands behind it.</summary>
        [Fact]
        public void ARunThatThrows_FallsBack()
        {
            var seams = new RunLog().Seams();
            seams.TaskStands = _ => throw new InvalidOperationException("no schtasks");
            Assert.Equal(1, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }, seams));
        }

        /// <summary>The test host gives nothing back with no seams.</summary>
        [Fact]
        public void TheTestHost_RunsNoGiveBack()
        {
            Assert.True(GiveBackTask.OffLimitsForTest);
            Assert.Equal(1, GiveBackRun.Run(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }));
        }

        /// <summary>A command line is a run's only with a switch, an ID and a name,
        /// and the process enters through the check for one. Nothing of WPF is
        /// reached first: the app's entry sits in a method of its own.</summary>
        [Fact]
        public void TheProcess_EntersThroughTheCheckForARun()
        {
            Assert.True(GiveBackRun.IsRun(new[] { GiveBackRun.TouchpadSwitch, "ID", "T" }));
            Assert.True(GiveBackRun.IsRun(new[] { GiveBackRun.PackageSwitch, "ID", "T" }));
            Assert.False(GiveBackRun.IsRun(new[] { GiveBackRun.TouchpadSwitch, "ID" }));
            Assert.False(GiveBackRun.IsRun(new[] { "--profile", "ID", "T" }));
            Assert.False(GiveBackRun.IsRun(Array.Empty<string>()));
            var entry = typeof(App).Assembly.EntryPoint;
            Assert.Equal("Program", entry.DeclaringType.Name);
            Assert.NotEmpty(entry.GetCustomAttributes(typeof(STAThreadAttribute), false));
            Assert.DoesNotContain("new App()", FenceSource.Body(
                AuditDelta20261002Tests.RepoText("PadForge.App", "Program.cs"), "public static void Main(string[] args)"));
        }

        /// <summary>What a run takes for a package: PadForge's catalog and the ID
        /// ending its line. An interface's package is not its device's, and a
        /// vendor's package is not PadForge's.</summary>
        [Fact]
        public void APackage_IsFoundByItsCatalogAndItsId()
        {
            string inf = VendorUsbDriverInstaller.BuildInf(Bind, "Keyboard");
            Assert.True(VendorUsbDriverInstaller.IsPackageOf(inf, Bind));
            Assert.True(VendorUsbDriverInstaller.IsPackageOf(inf, Bind.ToLowerInvariant()));
            Assert.False(VendorUsbDriverInstaller.IsPackageOf(VendorUsbDriverInstaller.BuildInf(Bind + "&MI_00", "Keyboard"), Bind));
            Assert.False(VendorUsbDriverInstaller.IsPackageOf(inf.Replace(VendorUsbDriverInstaller.CatalogName, "vendor.cat"), Bind));
        }

        /// <summary>A run is clear of a package only once none binds the ID. Under
        /// the test host's fence nothing is removed, so a package of the ID stays
        /// and answers as one that could not be removed. A file that cannot be read
        /// is not clear either.</summary>
        [Fact]
        public void APackageRemoval_IsClearOnlyWithNoneLeft()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForgeTests", "packages-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var log = new List<string>();
                Assert.True(VendorUsbDriverInstaller.RemovePackagesBinding(Bind, log.Add, dir));

                string other = Path.Combine(dir, "oem8.inf");
                File.WriteAllText(other, VendorUsbDriverInstaller.BuildInf(Bind + "&MI_00", "Keyboard"));
                Assert.True(VendorUsbDriverInstaller.RemovePackagesBinding(Bind, log.Add, dir));
                Assert.Empty(log);

                string ours = Path.Combine(dir, "oem7.inf");
                File.WriteAllText(ours, VendorUsbDriverInstaller.BuildInf(Bind, "Keyboard"));
                Assert.False(VendorUsbDriverInstaller.RemovePackagesBinding(Bind, log.Add, dir));
                Assert.True(File.Exists(ours) && File.Exists(other));
                Assert.Contains(log, line => line.StartsWith("oem7.inf binds", StringComparison.Ordinal));
                File.Delete(ours);

                using (new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.False(VendorUsbDriverInstaller.RemovePackagesBinding(Bind, log.Add, dir));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void TheKeyboardsAction_FindsPadForgesPackageForTheId_AndDeletesItsTask()
        {
            string name = GiveBackTask.NameOf(Bind);
            Assert.Equal(
                "set \"GIVE_BACK_FAILED=\" "
                + "& ( for /f \"delims=\" %f in ('C:\\Windows\\System32\\findstr.exe /m /l /e /c:\"USB_Install, USB\\\\VID_046D&PID_C31C\" \"C:\\Windows\\INF\\oem*.inf\"') do "
                + "( C:\\Windows\\System32\\findstr.exe /m /l /c:\"CatalogFile = padforge_vendorusb.cat\" \"%f\" >nul && ( C:\\Windows\\System32\\pnputil.exe /delete-driver \"%~nxf\" /uninstall /force || set \"GIVE_BACK_FAILED=1\" ) ) ) "
                + "& if not defined GIVE_BACK_FAILED C:\\Windows\\System32\\schtasks.exe /Delete /TN \"" + name + "\" /F",
                GiveBackTask.KeyboardCommand(name, Bind, @"C:\Windows\INF", System32));
            // What the scan looks for is what the package says.
            string inf = VendorUsbDriverInstaller.BuildInf(Bind, "Keyboard");
            Assert.Contains("USB_Install, " + Bind + "\r\n", inf);
            Assert.Contains("CatalogFile = " + VendorUsbDriverInstaller.CatalogName + "\r\n", inf);
        }

        [Fact]
        public void TheTouchpadsAction_TakesTheHideOff_RestartsTheCollection_AndDeletesItsTask()
        {
            string name = GiveBackTask.NameOf(Touchpad);
            const string cli = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
            string selfDelete = "C:\\Windows\\System32\\schtasks.exe /Delete /TN \"" + name + "\" /F";
            Assert.Equal(
                "if exist \"" + cli + "\" "
                + "( \"" + cli + "\" --dev-unhide \"" + Touchpad + "\" <NUL "
                + "&& ( C:\\Windows\\System32\\pnputil.exe /restart-device \"" + Touchpad + "\" & " + selfDelete + " ) ) "
                + "else ( " + selfDelete + " )",
                GiveBackTask.TouchpadCommand(name, Touchpad, cli, System32));
        }

        /// <summary>A boot trigger, as SYSTEM, on battery too: a laptop is what
        /// this is for. The action reaches cmd.exe unchanged through the XML.</summary>
        [Fact]
        public void TheDefinition_RunsAtBootAsSystem_OnBatteryToo()
        {
            string command = GiveBackTask.TouchpadCommand("T", Touchpad, @"C:\HidHideCLI.exe", System32);
            var xml = System.Xml.Linq.XDocument.Parse(GiveBackTask.TaskXml(command, System32, pipeHeld: true));
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var task = xml.Root;
            Assert.NotNull(task.Element(ns + "Triggers").Element(ns + "BootTrigger"));
            var principal = task.Element(ns + "Principals").Element(ns + "Principal");
            Assert.Equal("S-1-5-18", principal.Element(ns + "UserId").Value);
            Assert.Equal("HighestAvailable", principal.Element(ns + "RunLevel").Value);
            var settings = task.Element(ns + "Settings");
            Assert.Equal("false", settings.Element(ns + "DisallowStartIfOnBatteries").Value);
            Assert.Equal("false", settings.Element(ns + "StopIfGoingOnBatteries").Value);
            var actions = task.Element(ns + "Actions");
            Assert.Equal(principal.Attribute("id").Value, actions.Attribute("Context").Value);
            var exec = actions.Element(ns + "Exec");
            Assert.Equal(@"C:\Windows\System32\cmd.exe", exec.Element(ns + "Command").Value);
            Assert.Equal("/q /d /s /c \"" + command + "\"", exec.Element(ns + "Arguments").Value);
        }

        [Fact]
        public void ATasksName_IsTheSameInEverySession_AndTheIdsOwn()
        {
            Assert.Equal(GiveBackTask.NameOf(Bind), GiveBackTask.NameOf(Bind.ToLowerInvariant()));
            Assert.NotEqual(GiveBackTask.NameOf(Bind), GiveBackTask.NameOf(Touchpad));
            Assert.StartsWith("PadForge Give Back ", GiveBackTask.NameOf(Bind));
        }

        /// <summary>Two identical keyboards have one package, so one task. A
        /// Keep on either one ends it, and a boot task left from the other
        /// cannot take the kept keyboard off the driver at the next boot.</summary>
        [Fact]
        public void TwoDevicesOfOneId_ShareOneTask_AndAKeepSettlesBoth()
        {
            string flow = SwitchFlow();
            Assert.Contains("Services.GiveBackTask.RegisterKeyboard(bindId, log)", flow);
            Assert.Contains("string kept = offer.Bind.Value.BindId;", flow);
            Assert.Contains("Services.GiveBackTask.Delete(bindId, log)",
                FenceSource.Body(AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs"),
                    "private async System.Threading.Tasks.Task EndKeyboardTakeoverAsync(string nodeId, Action<string> log)"));

            var a = new KeyboardTakeoverEntry { NodeId = @"USB\VID_1&PID_2\A", BindId = @"USB\VID_1&PID_2", RowId = Guid.NewGuid().ToString(), Pending = true };
            var b = new KeyboardTakeoverEntry { NodeId = @"USB\VID_1&PID_2\B", BindId = @"usb\vid_1&pid_2", RowId = Guid.NewGuid().ToString(), Pending = true };
            var other = new KeyboardTakeoverEntry { NodeId = @"USB\VID_1&PID_3\C", BindId = @"USB\VID_1&PID_3", RowId = Guid.NewGuid().ToString(), Pending = true };
            var all = new[] { a, b, other };
            WinUsbKeyboardBinding.ConfirmKept(all, a.BindId);
            Assert.False(a.Pending);
            Assert.False(b.Pending);
            Assert.True(other.Pending);
            Assert.Same(other, Assert.Single(WinUsbKeyboardBinding.PendingToRestore(all, Array.Empty<VendorUsbDriverInstaller.UsbNode>())));
        }

        /// <summary>What schtasks is handed, with the definition it would read
        /// in the encoding it declares, gone again afterward.</summary>
        [Fact]
        public void Registering_HandsSchtasksTheDefinition_AndLeavesNoFile()
        {
            var calls = new List<string>();
            string read = null, path = null;
            GiveBackTask.RunSeam = args =>
            {
                calls.Add(args);
                int at = args.IndexOf("/XML \"", StringComparison.Ordinal) + 6;
                path = args[at..args.IndexOf('"', at)];
                byte[] bytes = File.ReadAllBytes(path);
                Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
                read = System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                return 0;
            };
            Assert.True(GiveBackTask.RegisterKeyboard(Bind, null));
            string name = GiveBackTask.NameOf(Bind);
            Assert.Equal($"/Create /TN \"{name}\" /XML \"{path}\" /RU SYSTEM /F", Assert.Single(calls));
            Assert.Contains("USB_Install, USB\\\\VID_046D&amp;PID_C31C", read);
            Assert.Contains("RegistrationTrigger", read);
            Assert.False(File.Exists(path));

            GiveBackTask.RunSeam = _ => 1;
            Assert.False(GiveBackTask.RegisterKeyboard(Bind, null));

            calls.Clear();
            GiveBackTask.RunSeam = args => { calls.Add(args); return 0; };
            GiveBackTask.Delete(Bind);
            Assert.Equal($"/Delete /TN \"{name}\" /F", Assert.Single(calls));
        }

        /// <summary>A PC with no HidHide CLI, as a HidHide PadForge installed on
        /// ARM64 has none, still has its task: the run talks to the driver itself.
        /// Only an exe path that cannot be quoted leaves no way to make one.</summary>
        [Fact]
        public void ATouchpadOnAPcWithNoCli_StillHasItsTask()
        {
            int made = 0;
            GiveBackTask.RunSeam = _ => { made++; return 0; };
            GiveBackTask.FileExistsSeam = _ => false;
            GiveBackTask.ExePathSeam = () => @"C:\PadForge\PadForge.exe";
            Assert.Equal(GiveBackTask.Outcome.Registered, GiveBackTask.RegisterTouchpad(Touchpad, null));
            Assert.Equal(1, made);

            GiveBackTask.RunSeam = _ => throw new InvalidOperationException("no CLI and no exe, no task");
            GiveBackTask.ExePathSeam = () => @"C:\50%\PadForge.exe";
            Assert.Equal(GiveBackTask.Outcome.NoTool, GiveBackTask.RegisterTouchpad(Touchpad, null));

            GiveBackTask.FileExistsSeam = p => p.EndsWith(@"HidHide\HidHideCLI.exe", StringComparison.Ordinal);
            Assert.Equal(@"C:\P\Nefarius Software Solutions\HidHide\HidHideCLI.exe", GiveBackTask.FindCli(@"C:\P"));
            GiveBackTask.FileExistsSeam = p => p.EndsWith(@"x64\HidHideCLI.exe", StringComparison.Ordinal);
            Assert.Equal(@"C:\P\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe", GiveBackTask.FindCli(@"C:\P"));
        }

        /// <summary>The fence: with no seam, nothing is started and nothing
        /// is registered.</summary>
        [Fact]
        public void TheTestHost_RegistersNoTask()
        {
            Assert.True(GiveBackTask.OffLimitsForTest);
            Assert.False(GiveBackTask.RegisterKeyboard(Bind, null));
            GiveBackTask.Delete(Bind);
        }

        [Fact]
        public void AnIdThatCannotBeQuoted_IsRefused()
        {
            int runs = 0;
            GiveBackTask.RunSeam = _ => { runs++; return 0; };
            GiveBackTask.FileExistsSeam = _ => true;
            Assert.False(GiveBackTask.RegisterKeyboard("USB\\\"x", null));
            Assert.False(GiveBackTask.RegisterKeyboard("a%b", null));
            Assert.Equal(GiveBackTask.Outcome.Failed, GiveBackTask.RegisterTouchpad("HID\\\"x", null));
            Assert.Equal(0, runs);
        }

        /// <summary>A touchpad's cover comes once per session. A refusal keeps
        /// the hide off and waits before it asks again. No HidHide CLI lets the
        /// hide through, with the crash and session-end release alone.</summary>
        [Fact]
        public void ACover_IsAskedOnce_ARefusalKeepsTheHideOff()
        {
            var covered = new HashSet<string>();
            var log = new List<string>();
            long retryAt = 0;
            int asks = 0;
            GiveBackTask.Outcome answer = GiveBackTask.Outcome.Failed;
            GiveBackTask.Outcome Register(string _) { asks++; return answer; }

            Assert.False(InputService.CoverTouchpadHide(Touchpad, covered, ref retryAt, 100, Register, log));
            Assert.False(InputService.CoverTouchpadHide(Touchpad, covered, ref retryAt, 101, Register, log));
            Assert.Equal(1, asks);

            answer = GiveBackTask.Outcome.Registered;
            Assert.True(InputService.CoverTouchpadHide(Touchpad, covered, ref retryAt, 100 + InputService.TouchpadCoverRetryMs, Register, log));
            Assert.True(InputService.CoverTouchpadHide(Touchpad, covered, ref retryAt, 200000, Register, log));
            Assert.Equal(2, asks);

            answer = GiveBackTask.Outcome.NoTool;
            Assert.True(InputService.CoverTouchpadHide("HID\\OTHER\\1", covered, ref retryAt, 200000, Register, log));
            Assert.Contains(log, line => line.Contains("no HidHide CLI"));
        }

        /// <summary>A task ends with its hide, and stays while a row still
        /// wants the touchpad hidden.</summary>
        [Fact]
        public void ATasksEnd_FollowsTheHide()
        {
            const string path = @"\\?\HID#GXTP5100&Col02#4&c834cd3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Touchpad };
            var deleted = new List<string>();
            var row = new UserDevice { CapType = InputDeviceType.Touchpad, DevicePath = path, TouchpadOnlyInPadForge = true };

            InputService.DropTasksOfTouchpadsShown(covered, new HashSet<string> { Touchpad }, new[] { row }, deleted.Add);
            InputService.DropTasksOfTouchpadsShown(covered, new HashSet<string>(), new[] { row }, deleted.Add);
            Assert.Empty(deleted);

            row.TouchpadOnlyInPadForge = false;
            InputService.DropTasksOfTouchpadsShown(covered, new HashSet<string> { Touchpad }, new[] { row }, deleted.Add);
            Assert.Empty(deleted);
            InputService.DropTasksOfTouchpadsShown(covered, new HashSet<string>(), new[] { row }, deleted.Add);
            Assert.Equal(new[] { Touchpad }, deleted);
            Assert.Empty(covered);
        }

        private static string SwitchFlow()
        {
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int start = window.IndexOf("private async System.Threading.Tasks.Task SwitchDriverAsync(bool bind)", StringComparison.Ordinal);
            int end = window.IndexOf("private void SetICadeMode(bool asPad)", start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            return window[start..end];
        }

        /// <summary>The mark and the task stand before the bind, and Keep ends
        /// both. A takeover nobody kept and a restore that failed keep theirs.</summary>
        [Fact]
        public void TheSwitch_MarksAndCoversTheTakeover_BeforeTheBind()
        {
            string flow = SwitchFlow();
            int pending = flow.IndexOf("Pending = true,", StringComparison.Ordinal);
            int add = flow.IndexOf("AddKeyboardTakeover(takeover);", StringComparison.Ordinal);
            int save = flow.IndexOf("_settingsService?.Save();", add, StringComparison.Ordinal);
            int cover = flow.IndexOf("Services.GiveBackTask.RegisterKeyboard(bindId, log)", StringComparison.Ordinal);
            int bind = flow.IndexOf("Services.VendorUsbDriverInstaller.Bind(", StringComparison.Ordinal);
            Assert.True(pending > 0 && add > pending && save > add && cover > save && bind > cover, $"{pending} {add} {save} {cover} {bind}");

            int prompt = flow.IndexOf("Views.KeepDriverDialog.Show(this, name, KeepDriverSeconds, mouse)", StringComparison.Ordinal);
            int delete = flow.IndexOf("Services.GiveBackTask.Delete(kept, log)", prompt, StringComparison.Ordinal);
            int confirm = flow.IndexOf("ConfirmKeyboardTakeovers(kept);", prompt, StringComparison.Ordinal);
            int restored = flow.IndexOf("if (restored) await EndKeyboardTakeoverAsync(offer.NodeId, log);", prompt, StringComparison.Ordinal);
            Assert.True(prompt > bind && delete > prompt && confirm > delete && restored > confirm, $"{prompt} {delete} {confirm} {restored}");
        }

        /// <summary>A bind that failed is taken back. When that fails too, the
        /// package can still take the device at the next start of Windows, so
        /// only the record goes. The boot task stays for that start.</summary>
        [Fact]
        public void AFailedBindWhoseRestoreFailedToo_KeepsTheBootTask()
        {
            string flow = SwitchFlow();
            Assert.Contains("takenBack = Services.VendorUsbDriverInstaller.Restore(offer.NodeId, log, default);", flow);
            int failed = flow.IndexOf("if (bind && !ok && takeover != null)", StringComparison.Ordinal);
            int check = flow.IndexOf("if (takenBack) await EndKeyboardTakeoverAsync(offer.NodeId, log);", failed, StringComparison.Ordinal);
            int recordOnly = flow.IndexOf("RemoveKeyboardTakeover(offer.NodeId);", failed, StringComparison.Ordinal);
            int prompt = flow.IndexOf("Views.KeepDriverDialog.Show(this, name, KeepDriverSeconds, mouse)", StringComparison.Ordinal);
            Assert.True(failed > 0 && check > failed && recordOnly > check && prompt > recordOnly, $"{failed} {check} {recordOnly} {prompt}");
        }

        /// <summary>A start that restored a pending takeover has said so, and
        /// the look eight seconds later does not say Windows did it. A pending
        /// takeover found on another driver loses its record and keeps its boot
        /// task, which deletes itself at the next boot, or removes a package
        /// still set for that boot.</summary>
        [Fact]
        public void AStart_ReportsARestoredTakeoverOnce_AndAMovedPendingOneKeepsItsTask()
        {
            string check = FenceSource.Body(AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs"), "private void CheckOptedInDrivers()");
            int restore = check.IndexOf("Services.VendorUsbDriverInstaller.Restore(entry.NodeId, log, default)", StringComparison.Ordinal);
            int taken = check.IndexOf("takeovers.Remove(entry);", StringComparison.Ordinal);
            int wait = check.IndexOf("Task.Delay(8000);", StringComparison.Ordinal);
            int look = check.IndexOf("WinUsbKeyboardBinding.MovedBack(takeovers, nodes)", StringComparison.Ordinal);
            Assert.True(restore > 0 && taken > restore && wait > taken && look > wait, $"{restore} {taken} {wait} {look}");

            Assert.True(check.IndexOf("DropMovedTakeovers(movedKeyboards);", look, StringComparison.Ordinal) > look);
            string drop = FenceSource.Body(AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs"),
                "private void DropMovedTakeovers(IReadOnlyCollection<Common.Input.WinUsbKeyboards.KeyboardTakeoverEntry> moved)");
            Assert.Contains("RemoveKeyboardTakeover(entry.NodeId);", drop);
            Assert.DoesNotContain("GiveBackTask", drop);
        }

        /// <summary>A takeover whose node never returned has a way out: its
        /// restore reads the node's stored properties, removes the package, and
        /// has nothing to wait for.</summary>
        [Fact]
        public void ARestore_ReachesANodeThatIsNotPresent()
        {
            string installer = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "VendorUsbDriverInstaller.cs");
            string restore = FenceSource.Body(installer, "internal static bool Restore(string instanceId, Action<string> log, CancellationToken ct)");
            int read = restore.IndexOf("present ? DeviceLocationFlags.Normal : DeviceLocationFlags.Phantom", StringComparison.Ordinal);
            int remove = restore.IndexOf("RemovePackage(inf, path);", StringComparison.Ordinal);
            int done = restore.IndexOf("if (!present)", StringComparison.Ordinal);
            int wait = restore.IndexOf("for (int i = 0; i < 20", StringComparison.Ordinal);
            Assert.True(read > 0 && remove > read && done > remove && wait > done, $"{read} {remove} {done} {wait}");
        }

        /// <summary>A restore looks at the other takeovers too. Each one it
        /// finds off PadForge's driver loses its record alone, as at a start: a
        /// pending one's package can still be set for the next start of Windows,
        /// which its boot task is there for.</summary>
        [Fact]
        public void ARestore_LeavesTheBootTaskOfAnotherTakeoverItFoundMoved()
        {
            string drop = FenceSource.Body(AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs"),
                "private async System.Threading.Tasks.Task DropTakeoversWindowsHasBackAsync()");
            Assert.Contains("DropMovedTakeovers(", drop);
            Assert.DoesNotContain("EndKeyboardTakeoverAsync", drop);
        }

        /// <summary>A restore moves every device of the package, and each one's
        /// takeover goes with it.</summary>
        [Fact]
        public void ARestore_DropsTheTakeoverOfAnotherDeviceItMoved()
        {
            string flow = SwitchFlow();
            Assert.Contains("await DropTakeoversWindowsHasBackAsync();", flow);

            var a = new KeyboardTakeoverEntry { NodeId = @"USB\VID_1&PID_2\A", BindId = @"USB\VID_1&PID_2", RowId = Guid.NewGuid().ToString() };
            var b = new KeyboardTakeoverEntry { NodeId = @"USB\VID_1&PID_2\B", BindId = @"USB\VID_1&PID_2", RowId = Guid.NewGuid().ToString() };
            var moved = WinUsbKeyboardBinding.MovedBack(new[] { a, b }, new[]
            {
                new VendorUsbDriverInstaller.UsbNode(b.NodeId, 1, 2, -1, "HidUsb", Array.Empty<string>(), Array.Empty<string>()),
            });
            Assert.Same(b, Assert.Single(moved));
        }

        /// <summary>A device a vendor's driver has, or no driver, gets no switch
        /// from the Devices page, so the status offers one only for a device on
        /// Windows' own driver.</summary>
        [Fact]
        public void TheStatus_OffersTheSwitchOnlyForADeviceOnWindowsDriver()
        {
            var entry = new KeyboardTakeoverEntry { NodeId = @"USB\VID_1&PID_2\A", BindId = @"USB\VID_1&PID_2", RowId = Guid.NewGuid().ToString() };
            VendorUsbDriverInstaller.UsbNode On(string service)
                => new(entry.NodeId, 1, 2, -1, service, Array.Empty<string>(), Array.Empty<string>());
            Assert.True(WinUsbKeyboardBinding.AllOnWindowsDriver(new[] { entry }, new[] { On("HidUsb") }));
            Assert.False(WinUsbKeyboardBinding.AllOnWindowsDriver(new[] { entry }, new[] { On("vendorkbd") }));
            Assert.False(WinUsbKeyboardBinding.AllOnWindowsDriver(new[] { entry }, new[] { On("") }));
            string main = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("AllOnWindowsDriver(movedKeyboards, nodes)", main);
            Assert.Contains("Status_DriverLeft_Format", main);
        }

        /// <summary>A pending takeover found on anything but PadForge's driver
        /// is not restored, so it is dropped here, or nothing ever ends it. A
        /// kept one goes when another driver holds the node. A kept node with
        /// no driver can still be on its way back to PadForge's, so it stays.</summary>
        [Theory]
        [InlineData(true, "HidUsb", true)]
        [InlineData(true, "", true)]
        [InlineData(true, "vendorkbd", true)]
        [InlineData(true, "WINUSB", false)]
        [InlineData(false, "HidUsb", true)]
        [InlineData(false, "vendorkbd", true)]
        [InlineData(false, "", false)]
        [InlineData(false, "WINUSB", false)]
        public void MovedBack_DropsATakeoverOnAnotherDriver(bool pending, string service, bool dropped)
        {
            var entry = new KeyboardTakeoverEntry { NodeId = @"USB\VID_1&PID_2\A", BindId = @"USB\VID_1&PID_2", RowId = Guid.NewGuid().ToString(), Pending = pending };
            var moved = WinUsbKeyboardBinding.MovedBack(new[] { entry },
                new[] { new VendorUsbDriverInstaller.UsbNode(entry.NodeId, 1, 2, -1, service, Array.Empty<string>(), Array.Empty<string>()) });
            Assert.Equal(dropped, moved.Contains(entry));
            Assert.Empty(WinUsbKeyboardBinding.MovedBack(new[] { entry }, Array.Empty<VendorUsbDriverInstaller.UsbNode>()));
        }

        /// <summary>Who goes back at a start: a pending takeover on PadForge's
        /// driver or unplugged. One on any other driver has been given back,
        /// and a kept one stays.</summary>
        [Fact]
        public void AtAStart_APendingTakeoverGoesBack()
        {
            KeyboardTakeoverEntry Entry(string node, bool pending) => new()
            {
                NodeId = node, BindId = @"USB\VID_1&PID_2", RowId = Guid.NewGuid().ToString(), Pending = pending,
            };
            VendorUsbDriverInstaller.UsbNode On(string node, string service)
                => new(node, 1, 2, -1, service, Array.Empty<string>(), Array.Empty<string>());
            var onWinUsb = Entry("A", true);
            var kept = Entry("B", false);
            var unplugged = Entry("C", true);
            var moved = Entry("D", true);
            var restore = WinUsbKeyboardBinding.PendingToRestore(new[] { onWinUsb, kept, unplugged, moved, null },
                new[] { On("A", "WINUSB"), On("B", "WINUSB"), On("D", "HidUsb") });
            Assert.Equal(new[] { onWinUsb, unplugged }, restore);

            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int check = window.IndexOf("private void CheckOptedInDrivers()", StringComparison.Ordinal);
            int scan = window.IndexOf("PendingToRestore(takeovers,", check, StringComparison.Ordinal);
            int wait = window.IndexOf("Task.Delay(8000);", check, StringComparison.Ordinal);
            Assert.True(check > 0 && scan > check && wait > scan, $"{check} {scan} {wait}");
        }

        [Fact]
        public void ThePendingMark_RoundTripsThroughTheFile_AndIsOnlyThereWhileTrue()
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(KeyboardTakeoverEntry));
            string Xml(KeyboardTakeoverEntry e)
            {
                using var writer = new StringWriter();
                serializer.Serialize(writer, e);
                return writer.ToString();
            }
            var entry = new KeyboardTakeoverEntry { NodeId = "n", BindId = "b", RowId = Guid.NewGuid().ToString(), Pending = true };
            Assert.Contains("Pending=\"true\"", Xml(entry));
            Assert.True(((KeyboardTakeoverEntry)serializer.Deserialize(new StringReader(Xml(entry)))).Pending);
            Assert.True(KeyboardTakeovers.Copy(entry).Pending);
            entry.Pending = false;
            Assert.DoesNotContain("Pending", Xml(entry));

            var devices = new System.Xml.Serialization.XmlSerializer(typeof(UserDevice));
            string Device(UserDevice d)
            {
                using var writer = new StringWriter();
                devices.Serialize(writer, d);
                return writer.ToString();
            }
            Assert.Contains("<TouchpadKeepPending>true</TouchpadKeepPending>", Device(new UserDevice { TouchpadKeepPending = true }));
            Assert.DoesNotContain("TouchpadKeepPending", Device(new UserDevice()));
        }

        /// <summary>A touchpad that was still waiting on its Keep prompt when
        /// the last session ended has its switch taken back at the start.</summary>
        [Fact]
        public void AtAStart_ATouchpadNobodyKept_LosesItsSwitch()
        {
            var waiting = new UserDevice { TouchpadOnlyInPadForge = true, TouchpadKeepPending = true };
            var kept = new UserDevice { TouchpadOnlyInPadForge = true };
            Assert.Same(waiting, Assert.Single(InputService.EndKeepsLeftPending(new[] { waiting, kept })));
            Assert.False(waiting.TouchpadOnlyInPadForge || waiting.TouchpadKeepPending);
            Assert.True(kept.TouchpadOnlyInPadForge);

            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int give = window.IndexOf("_inputService.GiveBackTouchpadsNobodyKept(_viewModel.Settings.AutoStartEngine);", StringComparison.Ordinal);
            int start = window.IndexOf("_inputService.Start();", give, StringComparison.Ordinal);
            Assert.True(give > 0 && start > give && start - give < 200, $"{give} {start}");

            int set = window.IndexOf("private async System.Threading.Tasks.Task<bool> SetTouchpadOnlyInPadForgeAsync", StringComparison.Ordinal);
            int mark = window.IndexOf("ud.TouchpadKeepPending = on;", set, StringComparison.Ordinal);
            int save = window.IndexOf("if (on) _settingsService?.Save();", set, StringComparison.Ordinal);
            int apply = window.IndexOf("_inputService.ApplyAutomaticDeviceHiding();", set, StringComparison.Ordinal);
            Assert.True(mark > set && save > mark && apply > save, $"{mark} {save} {apply}");

            int switchAt = window.IndexOf("private async System.Threading.Tasks.Task SwitchTouchpadAsync(bool take)", StringComparison.Ordinal);
            int keep = window.IndexOf("ud.TouchpadKeepPending = false;", switchAt, StringComparison.Ordinal);
            Assert.True(keep > switchAt && keep < set, $"{switchAt} {keep} {set}");
        }

        /// <summary>The cover goes on before the hide in the apply.</summary>
        [Fact]
        public void TheApply_CoversATouchpad_BeforeItsHide()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.Touchpads.cs");
            int method = source.IndexOf("private void AddTouchpadHides(", StringComparison.Ordinal);
            int gate = source.IndexOf("_touchpadsReleasing) return;", method, StringComparison.Ordinal);
            int cover = source.IndexOf("CoverTouchpadHide(id, _touchpadGiveBackTasks,", method, StringComparison.Ordinal);
            int hide = source.IndexOf("desiredIds.Add(id);", method, StringComparison.Ordinal);
            Assert.True(gate > method && cover > gate && hide > cover, $"{gate} {cover} {hide}");
        }

        /// <summary>A crash and a session's end run the release themselves:
        /// WPF does not wait for the async close at a session's end (wpf
        /// Application.cs:2125-2127, Window.cs:4408-4412).</summary>
        [Fact]
        public void ACrashAndASessionsEnd_ReleaseTheTouchpads()
        {
            string service = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("try { ReleaseTouchpadsNow(); } catch { }", FenceSource.Body(service, "public void PanicQuiesceOutputs()"));
            string app = AuditDelta20261002Tests.RepoText("PadForge.App", "App.xaml.cs");
            string ending = FenceSource.Body(app, "protected override void OnSessionEnding(SessionEndingCancelEventArgs e)");
            int release = ending.IndexOf("ReleaseTouchpadsForSessionEnd();", StringComparison.Ordinal);
            int exit = ending.IndexOf("PrepareForExit();", StringComparison.Ordinal);
            Assert.True(release > 0 && exit > release, $"{release} {exit}");
        }

        private sealed class Layout : ITouchpadLayout
        {
            public int ReportLength => 8;
            public void Dispose() { }
        }

        private sealed class Host : ITouchpadCaptureHost
        {
            public ITouchpadLayout Describe(TouchpadCapture capture) => new Layout();
            public IntPtr Publish(TouchpadCapture capture, ITouchpadLayout layout) => new(-0x10001);
            public bool Feed(IntPtr key, ITouchpadLayout layout, IntPtr report, int length) => true;
            public void Withdraw(IntPtr key) { }
        }

        private sealed class Quiet : MemoryStream
        {
            private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken stop = default)
            {
                await _closed.Task.WaitAsync(stop);
                return 0;
            }
            protected override void Dispose(bool disposing)
            {
                _closed.TrySetResult(0);
                base.Dispose(disposing);
            }
        }

        /// <summary>The release itself: PadForge's handle closes, the touchpad's
        /// own hide comes off and no other entry does, the collection restarts
        /// for Windows, the task goes, and the capture takes no further
        /// request.</summary>
        [Fact]
        public async Task TheRelease_TakesOnlyTheTouchpadsHideOff_AndRestartsIt()
        {
            const string path = @"\\?\HID#GXTP5100&Col02#4&c834cd3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string other = @"HID\VID_054C&PID_0CE6\1";
            var saved = SettingsManager.UserDevices;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                var list = new List<string> { Touchpad, other };
                HidHideController.ResetManagedForTests();
                HidHideController.IoSeam = (ioctl, input, output) =>
                {
                    if (ioctl == 0x80016008)
                    {
                        var text = System.Text.Encoding.Unicode.GetBytes(string.Concat(list.Select(e => e + "\0")) + "\0");
                        if (output == null || output.Length == 0) return (true, text.Length);
                        Array.Copy(text, output, text.Length);
                        return (true, text.Length);
                    }
                    if (ioctl == 0x8001600C)
                    {
                        list = System.Text.Encoding.Unicode.GetString(input).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
                        return (true, 0);
                    }
                    return (false, 0);
                };
                Assert.True(HidHideController.SyncManagedDevices(new HashSet<string> { Touchpad, other }));

                int restarts = 0;
                var quiet = new Quiet();
                using var capture = new TouchpadCapture(new Host(), path, Touchpad, "Touchpad", 1, 2, null, _ => quiet, _ => restarts++, () => true);
                capture.SetCapture(true);
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!capture.IsReading) await Task.Delay(5, timeout.Token);

                var deletes = new List<string>();
                GiveBackTask.RunSeam = args => { deletes.Add(args); return 0; };

                var reader = new PrecisionTouchpadReader();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                ((Dictionary<string, TouchpadCapture>)typeof(PrecisionTouchpadReader).GetField("_captures", flags).GetValue(reader)).Add(path, capture);
                var manager = (InputManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputManager));
                GC.SuppressFinalize(manager);
                typeof(InputManager).GetField("_ptpReader", flags).SetValue(manager, reader);
                var vm = new MainViewModel();
                var service = new InputService(vm);
                typeof(InputService).GetField("_inputManager", flags).SetValue(service, manager);
                try
                {
                    await Task.Run(() => service.ReleaseTouchpadsNow(30000));

                    Assert.Equal(new[] { other }, list);
                    Assert.Equal(1, restarts);
                    Assert.False(capture.IsReading);
                    Assert.Equal($"/Delete /TN \"{GiveBackTask.NameOf(Touchpad)}\" /F", Assert.Single(deletes));

                    capture.SetCapture(true);
                    Assert.False(capture.CaptureState == TabletCaptureState.WaitingForInput);
                }
                finally
                {
                    typeof(InputService).GetField("_inputManager", flags).SetValue(service, null);
                }
            }
            finally
            {
                SettingsManager.UserDevices = saved;
                HidHideController.IoSeam = null;
                HidHideController.ResetManagedForTests();
            }
        }
    }

    /// <summary>A keyboard or mouse left on PadForge's WinUSB package with no
    /// takeover, after PadForge.xml was deleted or replaced. Windows has no
    /// driver for it and PadForge no row for it, unless the sweep adopts it.</summary>
    [Collection("SettingsManagerStatics")]
    public class OrphanTakeoverTests : IDisposable
    {
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            KeyboardTakeovers.Apply(null);
        }

        private const string Guid_ = "{fd826c66-3556-4845-9436-0fe7580ded02}";

        private static string PathOf(string node) => @"\\?\" + node.ToLowerInvariant().Replace('\\', '#') + "#" + Guid_;

        private static VendorUsbDriverInstaller.UsbNode Node(string id, ushort vid, ushort pid, int mi, int protocol, string[] hardware = null)
            => new(id, vid, pid, mi, "WINUSB", hardware ?? Array.Empty<string>(), new[]
            {
                $@"USB\Class_03&SubClass_0{(protocol == 0 ? 0 : 1)}&Prot_0{protocol}",
                $@"USB\Class_03&SubClass_0{(protocol == 0 ? 0 : 1)}",
                @"USB\Class_03",
            });

        private static List<KeyboardTakeoverEntry> Adopt(VendorUsbDriverInstaller.UsbNode node,
            IReadOnlyList<KeyboardTakeoverEntry> takeovers = null, IEnumerable<string> optIns = null, string name = "Das Keyboard")
            => WinUsbKeyboardBinding.Orphans(new[] { PathOf(node.InstanceId) }, new[] { node },
                takeovers ?? Array.Empty<KeyboardTakeoverEntry>(), optIns, _ => name);

        /// <summary>A takeover built from the node, as a takeover made on this
        /// PC reads: its node, the ID its package names, its name from the
        /// tree, its kind from the boot protocol, and the row its WinUSB row
        /// takes.</summary>
        [Fact]
        public void AKeyboardOnThePackageWithNoTakeover_GetsOneFromItsNode()
        {
            const string id = @"USB\VID_24F0&PID_0140\5&2A&0&3";
            var entry = Assert.Single(Adopt(Node(id, 0x24F0, 0x0140, -1, 1)));
            Assert.Equal(id, entry.NodeId);
            Assert.Equal(@"USB\VID_24F0&PID_0140", entry.BindId);
            Assert.Equal("Das Keyboard", entry.Name);
            Assert.Equal((ushort)0x24F0, entry.VendorId);
            Assert.Equal((ushort)0x0140, entry.ProductId);
            Assert.False(entry.IsMouse);
            Assert.False(entry.Pending);
            Assert.Equal(WinUsbKeyboardDevice.IdentityFor(PathOf(id)), entry.Row);
            Assert.True(KeyboardTakeovers.IsValid(entry));
        }

        [Fact]
        public void AMouseOnThePackage_IsAdoptedAsAMouse_AndAnInterfaceKeepsItsMi()
        {
            const string id = @"USB\VID_046D&PID_C54D&MI_01\8&340F50E3&0&0001";
            var entry = Assert.Single(Adopt(Node(id, 0x046D, 0xC54D, 1, 2)));
            Assert.True(entry.IsMouse);
            Assert.Equal(@"USB\VID_046D&PID_C54D&MI_01", entry.BindId);
            Assert.Equal(WinUsbMouseDevice.IdentityFor(PathOf(id)), entry.Row);
        }

        /// <summary>A non-boot interface, an NKRO keyboard's for one, names no
        /// protocol. Only the take switch puts such a node on the package, so
        /// it is adopted as the keyboard most of them are. Its Restore works
        /// whichever it is.</summary>
        [Fact]
        public void AHidInterfaceWithNoBootProtocol_IsAdoptedAsAKeyboard()
            => Assert.False(Assert.Single(Adopt(Node(@"USB\VID_1234&PID_5678\1", 0x1234, 0x5678, -1, 0))).IsMouse);

        /// <summary>The name falls back to the kind's when the tree has none.</summary>
        [Fact]
        public void ANodeWithNoNameInTheTree_TakesItsKindsName()
            => Assert.Equal(VendorUsbDriverInstaller.NameOf(VendorUsbDriverInstaller.OptInKind.Mouse),
                Assert.Single(Adopt(Node(@"USB\VID_1234&PID_5678\1", 0x1234, 0x5678, -1, 2), name: null)).Name);

        /// <summary>Everything else on the package keeps its own path: a device
        /// a table binds or offers, whatever its class, and an opted-in ID.</summary>
        [Theory]
        [InlineData(@"USB\VID_057E&PID_0337\1", 0x057E, 0x0337, -1)]   // Wii U GameCube adapter, bound on sight
        [InlineData(@"USB\VID_046D&PID_C030\1", 0x046D, 0xC030, -1)]   // iFeel Mouse, the TouchSense table
        [InlineData(@"USB\VID_8086&PID_C013&MI_00\1", 0x8086, 0xC013, 0)]   // Intel base station
        [InlineData(@"USB\VID_041E&PID_2801&MI_01\1", 0x041E, 0x2801, 1)]   // Prodikeys
        [InlineData(@"USB\VID_045E&PID_0202\1", 0x045E, 0x0202, -1)]   // original Xbox controller
        [InlineData(@"USB\VID_045E&PID_0291\1", 0x045E, 0x0291, -1)]   // clone Xbox 360 receiver
        [InlineData(@"USB\VID_1603&PID_0002&MI_01\1", 0x1603, 0x0002, 1)]   // Ergodex DX1's pad interface
        [InlineData(@"USB\VID_046D&PID_C229&MI_00\1", 0x046D, 0xC229, 0)]   // G19's screen
        public void ADeviceATableClaims_IsNotAdopted(string id, int vid, int pid, int mi)
            => Assert.Empty(Adopt(Node(id, (ushort)vid, (ushort)pid, mi, 1)));

        /// <summary>The same device's keyboard interface, which no table binds,
        /// is the take switch's.</summary>
        [Fact]
        public void AnInterfaceNoTableNames_OfADeviceATableNames_IsAdopted()
            => Assert.Single(Adopt(Node(@"USB\VID_1603&PID_0002&MI_00\1", 0x1603, 0x0002, 0, 1)));

        [Fact]
        public void AnOptedInId_IsNotAdopted()
        {
            var node = Node(@"USB\VID_1234&PID_5678\1", 0x1234, 0x5678, -1, 1, new[] { @"USB\VID_1234&PID_5678&REV_0100", @"USB\VID_1234&PID_5678" });
            Assert.Empty(Adopt(node, optIns: new[] { @"USB\VID_1234&PID_5678" }));
            Assert.Single(Adopt(node, optIns: new[] { @"USB\VID_1234&PID_9999" }));
        }

        [Fact]
        public void ANodeATakeoverNames_IsLeftToIt()
        {
            var node = Node(@"USB\VID_1234&PID_5678\1", 0x1234, 0x5678, -1, 1);
            Assert.Empty(Adopt(node, new[] { new KeyboardTakeoverEntry { NodeId = node.InstanceId, BindId = "b", RowId = Guid.NewGuid().ToString() } }));
        }

        /// <summary>No class ID read means nothing known, a read that failed
        /// included: nothing is adopted, and the next sweep asks again. A class
        /// that is not HID is no keyboard, and a path with no node or no parse
        /// is nothing to act on.</summary>
        [Fact]
        public void WithNoHidClassRead_NothingIsAdopted()
        {
            const string id = @"USB\VID_1234&PID_5678\1";
            var unread = new VendorUsbDriverInstaller.UsbNode(id, 0x1234, 0x5678, -1, "WINUSB", Array.Empty<string>(), Array.Empty<string>());
            Assert.Empty(Adopt(unread));
            var vendor = unread with { CompatibleIds = new[] { @"USB\Class_FF&SubClass_00&Prot_00", @"USB\Class_FF" } };
            Assert.Empty(Adopt(vendor));
            var hidUsb = Node(id, 0x1234, 0x5678, -1, 1) with { Service = "HidUsb" };
            Assert.Empty(Adopt(hidUsb));
            Assert.Empty(WinUsbKeyboardBinding.Orphans(new[] { PathOf(id) }, Array.Empty<VendorUsbDriverInstaller.UsbNode>(), Array.Empty<KeyboardTakeoverEntry>(), null, _ => "x"));
            Assert.Empty(WinUsbKeyboardBinding.Orphans(new[] { "not a path" }, new[] { unread }, Array.Empty<KeyboardTakeoverEntry>(), null, _ => "x"));
            Assert.Empty(WinUsbKeyboardBinding.Orphans(null, null, null, null, null));
        }

        /// <summary>The look runs off the poll thread on a PC with no takeover,
        /// at its own slow cadence, and a failed look says nothing and leaves
        /// the next one due. The shutdown ends it.</summary>
        [Fact]
        public void TheLook_RunsAwayFromThePoll_OnItsCadence_AndAFailedOneAsksAgain()
        {
            KeyboardTakeovers.Apply(null);
            long now = 1000;
            int looks = 0;
            IReadOnlyList<KeyboardTakeoverEntry> answer = null;
            var queued = new Queue<Action>();
            var got = new List<KeyboardTakeoverEntry>();
            var im = new InputManager
            {
                WinUsbKeyboardTicks = () => now,
                WinUsbOrphanFind = () => { looks++; return answer; },
                WinUsbOrphanRun = work => { queued.Enqueue(work); return Task.CompletedTask; },
            };
            im.WinUsbTakeoversFound += list => got.AddRange(list);

            im.SyncWinUsbKeyboards();
            Assert.Equal(0, looks);
            queued.Dequeue()();
            Assert.Equal(1, looks);
            Assert.Empty(got);

            im.SyncWinUsbKeyboards();
            Assert.Empty(queued);

            now += InputManager.WinUsbOrphanLookMs;
            var entry = new KeyboardTakeoverEntry { NodeId = "n", BindId = "b", RowId = Guid.NewGuid().ToString() };
            answer = new[] { entry };
            im.SyncWinUsbKeyboards();
            queued.Dequeue()();
            Assert.Same(entry, Assert.Single(got));

            im.RetireWinUsbKeyboards();
            now += InputManager.WinUsbOrphanLookMs;
            im.SyncWinUsbKeyboards();
            Assert.Empty(queued);
        }

        /// <summary>The adoption: on the list and in the file, handed to the
        /// device pass, with an offline row of the right kind, so the Devices
        /// page has its Restore while the engine is off or the channel never
        /// opens. A second report changes nothing.</summary>
        [Fact]
        public void AnAdoptedTakeover_GetsARowAndItsRestore()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            var vm = new MainViewModel();
            var input = new InputService(vm);
            const string id = @"USB\VID_1234&PID_5678\1";
            var row = WinUsbMouseDevice.IdentityFor(PathOf(id));
            var entry = new KeyboardTakeoverEntry
            {
                NodeId = id, BindId = @"USB\VID_1234&PID_5678", RowId = row.ToString(), Name = "Trackball",
                VendorId = 0x1234, ProductId = 0x5678, Kind = KeyboardTakeoverEntry.MouseKind,
            };
            input.AdoptTakeovers(new[] { entry });
            input.AdoptTakeovers(new[] { entry });

            Assert.Same(entry, Assert.Single(vm.Settings.KeyboardTakeovers));
            Assert.Equal(row, Assert.Single(KeyboardTakeovers.Current).Row);
            var ud = Assert.Single(SettingsManager.UserDevices.Items);
            Assert.Equal(row, ud.InstanceGuid);
            Assert.Equal(InputDeviceType.Mouse, ud.CapType);
            Assert.Equal("Trackball", ud.InstanceName);
            Assert.False(ud.IsOnline);
            Assert.Equal((ushort)0x1234, ud.VendorId);
            Assert.Contains(vm.Devices.Devices, r => r.InstanceGuid == row && r.DeviceTypeKey == "Mouse");

            var offer = WinUsbKeyboardBinding.RestoreOffer(KeyboardTakeovers.ForRow(row),
                new[] { Node(id, 0x1234, 0x5678, -1, 2) });
            Assert.True(offer.Restore);
            Assert.Equal(VendorUsbDriverInstaller.OptInKind.Mouse, offer.Kind);

            string service = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.Touchpads.cs");
            Assert.Contains("_settingsService?.Save();", FenceSource.Body(service, "    internal void AdoptTakeovers(".TrimStart()));
        }

        /// <summary>Both ways in: the running engine's sweep, and each start,
        /// where an engine that does not start would otherwise leave nothing
        /// to look.</summary>
        [Fact]
        public void TheAdoption_RunsAtEveryStart_AndFromTheEngine()
        {
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            string check = FenceSource.Body(window, "private void CheckOptedInDrivers()");
            int look = check.IndexOf("Services.WinUsbKeyboardBinding.FindOrphans(ids)", StringComparison.Ordinal);
            int adopt = check.IndexOf("_inputService.AdoptTakeovers(", StringComparison.Ordinal);
            int none = check.IndexOf("if (ids.Count == 0 && takeovers.Count == 0) return;", StringComparison.Ordinal);
            Assert.True(look > 0 && adopt > look && none > adopt, $"{look} {adopt} {none}");

            string service = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("_inputManager.WinUsbTakeoversFound += OnWinUsbTakeoversFound;", service);
            Assert.Contains("_inputManager.WinUsbTakeoversFound -= OnWinUsbTakeoversFound;", service);
            Assert.Contains("WinUsbKeyboardBinding.FindOrphans(_mainVm.Settings.WinUsbOptIns.ToArray())", service);
        }
    }
}
