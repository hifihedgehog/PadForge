using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A controller PadForge hides is taken from every program that opened it
    /// before the hide. HidHide decides only at open, so for a USB controller
    /// that was already connected PadForge cycles its hub port. Bluetooth is
    /// never cycled, a receiver that carries other pads is left alone, and a
    /// controller that just connected is not cycled a second time.
    /// </summary>
    public class HiddenControllerReleaseTests
    {
        private static readonly Guid Hid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        private static readonly Guid Usb = new("36fc9e60-c465-11cf-8056-444553540000");
        private static readonly Guid Xusb = new("d61ca365-5af4-4486-998b-9db4734c6ca3");
        private static readonly Guid Pad = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid Dock = new("99999999-2222-3333-4444-555555555555");
        private static readonly Guid SystemContainer = new("00000000-0000-0000-ffff-ffffffffffff");
        private static readonly TimeSpan LongAgo = TimeSpan.FromMinutes(5);

        private static HidHideController.ReleaseHop Hop(string id, Guid cls, Guid container, string service = null)
            => new(id, cls, container, service);

        /// <summary>A USB DualSense: the HID collection, its interface, the
        /// composite device and the root hub.</summary>
        private static List<HidHideController.ReleaseHop> UsbDualSense(Guid topContainer) => new()
        {
            Hop(@"HID\VID_054C&PID_0CE6&MI_03\7&1A2B&0&0000", Hid, Pad),
            Hop(@"USB\VID_054C&PID_0CE6&MI_03\6&3C4D&0&0003", Hid, Pad, "HidUsb"),
            Hop(@"USB\VID_054C&PID_0CE6\SERIAL01", Usb, topContainer, "usbccgp"),
            Hop(@"USB\ROOT_HUB30\4&5E6F&0&0", Usb, SystemContainer, "USBHUB3"),
        };

        [Fact]
        public void AWiredControllerConnectedEarlierHasItsPortCycled()
        {
            string usb = HidHideController.PickUsbDeviceToCycle(UsbDualSense(Pad), 0, LongAgo, out string reason);
            Assert.Equal(@"USB\VID_054C&PID_0CE6\SERIAL01", usb);
            Assert.Null(reason);

            // An unreadable arrival time does not hold it back.
            Assert.NotNull(HidHideController.PickUsbDeviceToCycle(UsbDualSense(Pad), 0, null, out _));
        }

        [Fact]
        public void AControllerThatJustConnectedIsLeftAlone()
        {
            Assert.Null(HidHideController.PickUsbDeviceToCycle(UsbDualSense(Pad), 0, TimeSpan.FromSeconds(2), out string reason));
            Assert.Equal("connected moments ago", reason);
            Assert.NotNull(HidHideController.PickUsbDeviceToCycle(UsbDualSense(Pad), 0, HidHideController.ReleaseArrivalGrace, out _));
        }

        /// <summary>Cycling the radio's port would drop every Bluetooth
        /// device, and dropping the controller's own link turns it
        /// off.</summary>
        [Fact]
        public void BluetoothIsNeverCycled()
        {
            var chain = new List<HidHideController.ReleaseHop>
            {
                Hop(@"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\8&1&0&0000", Hid, Pad),
                Hop(@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\7&2&0&A0B1C2D3E4F5_C00000000", Hid, Pad, "HidBth"),
                Hop(@"BTH\MS_BTHBRB\6&3&0&1", Hid, SystemContainer, "BthEnum"),
                Hop(@"USB\VID_8087&PID_0033\5&4&0&14", Usb, SystemContainer, "BTHUSB"),
                Hop(@"USB\ROOT_HUB30\4&5&0&0", Usb, SystemContainer, "USBHUB3"),
            };
            Assert.Null(HidHideController.PickUsbDeviceToCycle(chain, 0, LongAgo, out string reason));
            Assert.Equal("Bluetooth", reason);

            var le = new List<HidHideController.ReleaseHop>
            {
                Hop(@"HID\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13\9&1&0&0000", Hid, Pad),
                Hop(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13\8&2", Hid, Pad, "HidBthLE"),
            };
            Assert.Null(HidHideController.PickUsbDeviceToCycle(le, 0, LongAgo, out reason));
            Assert.Equal("Bluetooth", reason);
        }

        [Fact]
        public void AReceiverCarryingMorePadsIsLeftAlone()
        {
            var chain = new List<HidHideController.ReleaseHop>
            {
                Hop(@"USB\VID_045E&PID_0719&MI_00\6&1&0&0000", Xusb, Pad, "xusb22"),
                Hop(@"USB\VID_045E&PID_0719\E02F1950", Usb, Pad, "usbccgp"),
                Hop(@"USB\ROOT_HUB30\4&5&0&0", Usb, SystemContainer, "USBHUB3"),
            };
            Assert.Null(HidHideController.PickUsbDeviceToCycle(chain, 2, LongAgo, out string reason));
            Assert.Equal("the USB device carries more than one controller", reason);
            Assert.Equal(@"USB\VID_045E&PID_0719\E02F1950", HidHideController.PickUsbDeviceToCycle(chain, 1, LongAgo, out _));
        }

        [Fact]
        public void AUsbDeviceThatIsNotTheControllerIsLeftAlone()
        {
            Assert.Null(HidHideController.PickUsbDeviceToCycle(UsbDualSense(Dock), 0, LongAgo, out string reason));
            Assert.Equal("the USB device is not the controller", reason);
        }

        /// <summary>A handheld's built-in pad sits in the SYSTEM container
        /// beside every other built-in device, so the VID and PID decide, the
        /// way the hide's own expansion decides.</summary>
        [Fact]
        public void ABuiltInPadIsMatchedByVidAndPid()
        {
            List<HidHideController.ReleaseHop> Chain(string topId) => new()
            {
                Hop(@"USB\VID_17EF&PID_6182&MI_00\7&1&0&0000", Xusb, SystemContainer, "xusb22"),
                Hop(topId, Usb, SystemContainer, "usbccgp"),
                Hop(@"USB\ROOT_HUB30\4&5&0&0", Usb, SystemContainer, "USBHUB3"),
            };
            Assert.Equal(@"USB\VID_17EF&PID_6182\6&2&0&3", HidHideController.PickUsbDeviceToCycle(Chain(@"USB\VID_17EF&PID_6182\6&2&0&3"), 1, LongAgo, out _));
            Assert.Null(HidHideController.PickUsbDeviceToCycle(Chain(@"USB\VID_0BDA&PID_5411\6&2&0&3"), 1, LongAgo, out string reason));
            Assert.Equal("the USB device is not the controller", reason);
        }

        [Fact]
        public void ANodeOffAnyUsbHubIsLeftAlone()
        {
            var chain = new List<HidHideController.ReleaseHop>
            {
                Hop(@"HID\VID_F00D&PID_0001\1", Hid, Pad),
                Hop(@"ROOT\SYSTEM\0001", Usb, SystemContainer, "swenum"),
            };
            Assert.Null(HidHideController.PickUsbDeviceToCycle(chain, 0, LongAgo, out string reason));
            Assert.Equal("not on a USB port", reason);
            Assert.Null(HidHideController.PickUsbDeviceToCycle(new List<HidHideController.ReleaseHop>(), 0, LongAgo, out reason));
            Assert.Equal("not present", reason);
        }

        private sealed class Harness
        {
            public readonly HiddenControllerRelease Release = new();
            public readonly List<string> Cycled = new();
            public readonly List<string> Lines = new();
            public long Clock = 1_000_000;
            public Dictionary<string, (string usb, string reason)> Map = new(StringComparer.OrdinalIgnoreCase);

            public Harness()
            {
                Release.Resolve = id => Map.TryGetValue(id, out var r) ? r : (null, "not present");
                Release.CyclePort = usb => Cycled.Add(usb);
                Release.Now = () => Clock;
                Release.Log = line => { lock (Lines) Lines.Add(line); };
                Release.Pause = ms => Pauses.Add(ms);
            }

            public readonly List<int> Pauses = new();

            public void Run(params string[] ids)
            {
                Release.Release(ids);
                Release.Pending.Wait(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>Every node of one controller resolves to the same USB
        /// device, which is cycled once. A Bluetooth collection says why it
        /// was skipped, once.</summary>
        [Fact]
        public void OneCyclePerUsbDeviceAndOneLinePerSkippedCollection()
        {
            var h = new Harness();
            h.Map[@"HID\VID_054C&PID_0CE6&MI_03\7&1"] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            h.Map[@"USB\VID_054C&PID_0CE6&MI_03\6&2"] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            h.Map[@"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002057e_PID&0306\8&3"] = (null, "Bluetooth");
            h.Map[@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002057e_PID&0306\7&4"] = (null, "Bluetooth");

            h.Run(@"HID\VID_054C&PID_0CE6&MI_03\7&1", @"USB\VID_054C&PID_0CE6&MI_03\6&2",
                  @"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002057e_PID&0306\8&3",
                  @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002057e_PID&0306\7&4");

            Assert.Equal(new[] { @"USB\VID_054C&PID_0CE6\SERIAL01" }, h.Cycled);
            Assert.Single(h.Lines, l => l.StartsWith(@"HIDHIDE released USB\VID_054C&PID_0CE6\SERIAL01"));
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release skipped") && l.EndsWith(": Bluetooth"));
            Assert.DoesNotContain(h.Lines, l => l.StartsWith("HIDHIDE release held"));
        }

        /// <summary>A device's return runs the apply again, so the same USB
        /// device is cycled at most once in thirty seconds.</summary>
        [Fact]
        public void APortIsCycledAtMostOnceInThirtySeconds()
        {
            var h = new Harness();
            h.Map[@"HID\VID_054C&PID_0CE6\1"] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);

            h.Run(@"HID\VID_054C&PID_0CE6\1");
            h.Clock += HiddenControllerRelease.CooldownMs - 1;
            h.Run(@"HID\VID_054C&PID_0CE6\1");
            Assert.Single(h.Cycled);
            Assert.Single(h.Lines, l => l.StartsWith(@"HIDHIDE release held USB\VID_054C&PID_0CE6\SERIAL01"));

            h.Clock += 1;
            h.Run(@"HID\VID_054C&PID_0CE6\1");
            Assert.Equal(2, h.Cycled.Count);
        }

        [Fact]
        public void ARefusedCycleIsLoggedAndTheNextDeviceStillRuns()
        {
            var h = new Harness();
            h.Map[@"HID\A\1"] = (@"USB\A\1", null);
            h.Map[@"HID\B\1"] = (@"USB\B\1", null);
            h.Release.CyclePort = usb =>
            {
                if (usb == @"USB\A\1") throw new InvalidOperationException("hub refused");
                h.Cycled.Add(usb);
            };

            h.Run(@"HID\A\1", @"HID\B\1");

            Assert.Equal(new[] { @"USB\B\1" }, h.Cycled);
            Assert.Single(h.Lines, l => l == @"HIDHIDE release failed USB\A\1: InvalidOperationException: hub refused");
        }

        /// <summary>Two cycles in one batch wait 500 ms between them, the way
        /// HandheldCompanion spaces its cycles, and a single cycle waits for
        /// nothing.</summary>
        [Fact]
        public void CyclesInOneBatchAreSpacedApart()
        {
            var h = new Harness();
            h.Map[@"HID\A\1"] = (@"USB\A\1", null);
            h.Map[@"HID\B\1"] = (@"USB\B\1", null);

            h.Run(@"HID\A\1");
            Assert.Empty(h.Pauses);

            h.Clock += HiddenControllerRelease.CooldownMs;
            h.Run(@"HID\A\1", @"HID\B\1");
            Assert.Equal(new[] { HiddenControllerRelease.PauseBetweenCyclesMs }, h.Pauses);
        }

        [Fact]
        public void NothingRunsAfterDispose()
        {
            var h = new Harness();
            h.Map[@"HID\A\1"] = (@"USB\A\1", null);
            h.Release.Dispose();
            h.Run(@"HID\A\1");
            Assert.Empty(h.Cycled);
        }
    }
}
