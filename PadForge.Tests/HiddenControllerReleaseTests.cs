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
    /// controller that just connected is not cycled a second time. A cycle
    /// the cooldown holds back runs once the cooldown ends, for a controller
    /// still hidden then. Every batch, held or not, decides from HidHide's
    /// list and PadForge's controller rows as they stand when it runs.
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

            /// <summary>Held cycles the release asked to run later, with their
            /// waits. A test runs one by calling <see cref="RunDue"/>.</summary>
            public readonly List<(long ms, Action action)> Scheduled = new();

            /// <summary>Ids shown again since they were hidden. HidHide's
            /// list names every other id a test has asked a release for.</summary>
            public readonly HashSet<string> Shown = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>What each Resolve call was told about the controller
            /// rows on the USB device it resolved to.</summary>
            public readonly List<(string id, int rows)> RowsSeen = new();

            /// <summary>PadForge's controller rows per USB device as they
            /// stand now. A test changes them between batches.</summary>
            public readonly Dictionary<string, int> Rows = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>How often HidHide's list was asked, whether it reads
            /// at all, and whether its driver has gone with it.</summary>
            public int ListReads;
            public bool ListUnreadable;
            public bool DriverGone;

            /// <summary>The engine has started and not yet listed its
            /// devices.</summary>
            public bool EngineListing;

            /// <summary>The wall clock, which stamps a hand-over and a
            /// cycle. A test moves it with <see cref="Clock"/> where both
            /// matter.</summary>
            public DateTime Wall = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

            /// <summary>When each id Resolve was asked about had been handed
            /// to the release.</summary>
            public readonly Dictionary<string, DateTime> HandedOver = new(StringComparer.OrdinalIgnoreCase);

            public Harness()
            {
                Release.Resolve = (id, rowsOn, handedOverUtc) =>
                {
                    var r = Map.TryGetValue(id, out var found) ? found : (null, "not present");
                    lock (RowsSeen)
                    {
                        RowsSeen.Add((id, rowsOn(r.usb)));
                        HandedOver[id] = handedOverUtc;
                    }
                    return r;
                };
                Release.CyclePort = usb => Cycled.Add(usb);
                Release.Now = () => Clock;
                Release.UtcNow = () => Wall;
                Release.Log = line => { lock (Lines) Lines.Add(line); };
                Release.Pause = ms => Pauses.Add(ms);
                Release.Schedule = (ms, action) => { lock (Scheduled) Scheduled.Add((ms, action)); };
                Release.ReadBlacklist = () =>
                {
                    System.Threading.Interlocked.Increment(ref ListReads);
                    if (ListUnreadable) return (null, !DriverGone);
                    lock (_asked) return (_asked.Where(id => !Shown.Contains(id)).ToList(), true);
                };
                Release.ControllerRows = () => new Dictionary<string, int>(Rows, StringComparer.OrdinalIgnoreCase);
                Release.RowsPending = () => EngineListing;
            }

            public readonly List<int> Pauses = new();

            public void Run(params string[] ids)
            {
                Queue(ids);
                WaitForBatches();
            }

            /// <summary>Waits for every batch queued so far. False when one
            /// is still running after the wait.</summary>
            public bool WaitForBatches() => Release.Pending.Wait(TimeSpan.FromSeconds(30));

            /// <summary>Queues a release and returns without waiting for its
            /// batch.</summary>
            public void Queue(params string[] ids)
            {
                lock (_asked) _asked.UnionWith(ids);
                Release.Release(ids);
            }

            /// <summary>Runs the held cycle scheduled at the index, as its
            /// timer would, and waits for the batch it queues.</summary>
            public void RunDue(int index)
            {
                Action action;
                lock (Scheduled) action = Scheduled[index].action;
                action();
                WaitForBatches();
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

        /// <summary>Shown and hidden again inside the cooldown, a controller
        /// is still taken from a program that opened it in between: the held
        /// cycle runs when the cooldown ends, and the cooldown still allows
        /// one cycle per thirty seconds.</summary>
        [Fact]
        public void AHeldCycleRunsWhenTheCooldownEnds()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);

            h.Run(id);
            h.Clock += HiddenControllerRelease.CooldownMs - 1;
            h.Run(id);
            Assert.Single(h.Cycled);
            var due = Assert.Single(h.Scheduled);
            Assert.Equal(1, due.ms);

            h.Clock += 1;
            h.RunDue(0);
            Assert.Equal(new[] { usb, usb }, h.Cycled);

            // The cycle that came due starts a new cooldown of its own.
            h.Clock += 1;
            h.Run(id);
            Assert.Equal(2, h.Cycled.Count);
        }

        /// <summary>A controller shown again before its held cycle comes due
        /// is left connected.</summary>
        [Fact]
        public void AHeldCycleLeavesAControllerShownAgain()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);

            h.Run(id);
            h.Clock += 10_000;
            h.Run(id);
            h.Shown.Add(id);
            h.Clock += HiddenControllerRelease.CooldownMs;
            h.RunDue(0);

            Assert.Single(h.Cycled);
        }

        /// <summary>A device waits on one held cycle however often it is
        /// held, and that cycle takes every id held for it.</summary>
        [Fact]
        public void ADeviceWaitsOnOneHeldCycle()
        {
            var h = new Harness();
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[@"HID\VID_054C&PID_0CE6&MI_03\1"] = (usb, null);
            h.Map[@"USB\VID_054C&PID_0CE6&MI_03\2"] = (usb, null);

            h.Run(@"HID\VID_054C&PID_0CE6&MI_03\1");
            h.Clock += 1_000;
            h.Run(@"HID\VID_054C&PID_0CE6&MI_03\1");
            h.Clock += 1_000;
            h.Run(@"USB\VID_054C&PID_0CE6&MI_03\2");
            Assert.Single(h.Scheduled);

            h.Clock += HiddenControllerRelease.CooldownMs;
            h.RunDue(0);
            Assert.Equal(2, h.Cycled.Count);
            Assert.Contains(@"HID\VID_054C&PID_0CE6&MI_03\1", h.Release.TakeCycledIds().Keys);
        }

        /// <summary>Dispose drops held work. A held cycle that comes due
        /// after it queues nothing, reads nothing and cycles nothing.</summary>
        [Fact]
        public void AHeldCycleDoesNotRunAfterDispose()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);

            h.Run(id);
            h.Run(id);
            Assert.Single(h.Scheduled);
            int reads = h.ListReads;
            int resolves = h.RowsSeen.Count;
            var queued = h.Release.Pending;

            h.Release.Dispose();
            h.Clock += HiddenControllerRelease.CooldownMs;
            h.RunDue(0);

            Assert.Single(h.Cycled);
            Assert.Same(queued, h.Release.Pending);
            Assert.Equal(reads, h.ListReads);
            Assert.Equal(resolves, h.RowsSeen.Count);
        }

        /// <summary>Dispose stops the work in hand. A batch already running
        /// cycles nothing after it, and the batch queued behind it reads
        /// nothing: not HidHide's list, not the device tree.</summary>
        [Fact]
        public void DisposeStopsABatchInFlightAndTheOneBehindIt()
        {
            var h = new Harness();
            h.Map[@"HID\A\1"] = (@"USB\A\1", null);
            h.Map[@"HID\B\1"] = (@"USB\B\1", null);
            using var resolving = new System.Threading.ManualResetEventSlim(false);
            using var go = new System.Threading.ManualResetEventSlim(false);
            var resolve = h.Release.Resolve;
            h.Release.Resolve = (id, rowsOn, handedOverUtc) =>
            {
                var found = resolve(id, rowsOn, handedOverUtc);
                resolving.Set();
                go.Wait();
                return found;
            };

            h.Queue(@"HID\A\1");
            Assert.True(resolving.Wait(TimeSpan.FromSeconds(30)));
            h.Queue(@"HID\B\1");
            int reads = h.ListReads;
            h.Release.Dispose();
            go.Set();
            Assert.True(h.WaitForBatches());

            Assert.Empty(h.Cycled);
            Assert.Equal(reads, h.ListReads);
            lock (h.RowsSeen) Assert.Equal(new[] { @"HID\A\1" }, h.RowsSeen.Select(r => r.id));
        }

        /// <summary>The controllers on a USB device are counted when its
        /// cycle runs, not when the cycle was queued. A second pad that
        /// joined a dongle while its cycle was held keeps the dongle
        /// connected when the cycle comes due.</summary>
        [Fact]
        public void AHeldCycleCountsTheControllersWhenItComesDue()
        {
            var h = new Harness();
            const string id = @"HID\VID_28DE&PID_1142&MI_01\8&1&0&0000";
            const string dongle = @"USB\VID_28DE&PID_1142\6&3&0&2";
            var chain = new List<HidHideController.ReleaseHop>
            {
                Hop(id, Hid, Pad),
                Hop(@"USB\VID_28DE&PID_1142&MI_01\7&2&0&0001", Hid, Pad, "HidUsb"),
                Hop(dongle, Usb, Pad, "usbccgp"),
                Hop(@"USB\ROOT_HUB30\4&5&0&0", Usb, SystemContainer, "USBHUB3"),
            };
            h.Release.Resolve = (hid, rowsOn, _) =>
            {
                string usb = HidHideController.PickUsbDeviceToCycle(chain, 0, rowsOn, LongAgo, out string reason);
                return (usb, reason);
            };

            // One pad on the dongle: hidden and cycled, then shown and hidden
            // again five seconds later, which the cooldown holds.
            h.Rows[dongle] = 1;
            h.Run(id);
            Assert.Single(h.Cycled);
            h.Clock += 5_000;
            h.Run(id);
            Assert.Single(h.Scheduled);
            Assert.Single(h.Cycled);

            // A second pad joins the dongle before the held cycle comes due.
            h.Clock += 5_000;
            h.Rows[dongle] = 2;
            h.Clock += HiddenControllerRelease.CooldownMs;
            h.RunDue(0);

            Assert.Single(h.Cycled);
            Assert.Single(h.Lines, l => l == $"HIDHIDE release skipped {id}: the USB device carries more than one controller");
        }

        /// <summary>HidHide's list is read when a batch runs, held or not. A
        /// controller shown again between its hide and its batch is left
        /// connected.</summary>
        [Fact]
        public void AControllerShownAgainBeforeItsBatchRunsIsLeftConnected()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);

            h.Shown.Add(id);
            h.Run(id);
            Assert.Empty(h.Cycled);
            lock (h.RowsSeen) Assert.Empty(h.RowsSeen);

            h.Shown.Clear();
            h.Run(id);
            Assert.Single(h.Cycled);
        }

        /// <summary>HidHide's control device takes one client at a time, so
        /// the list does not read while another program holds it. A batch
        /// whose read fails cycles nothing, keeps its ids and reads again
        /// after <see cref="HiddenControllerRelease.RetryMs"/>, as often as
        /// it takes, and then decides as any batch does. It says once that it
        /// waits.</summary>
        [Fact]
        public void ABatchWhoseListDoesNotReadAsksAgain()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);

            h.ListUnreadable = true;
            h.Run(id);
            Assert.Empty(h.Cycled);
            lock (h.RowsSeen) Assert.Empty(h.RowsSeen);
            Assert.Equal(HiddenControllerRelease.RetryMs, Assert.Single(h.Scheduled).ms);

            // The other program still holds the control device.
            h.RunDue(0);
            Assert.Empty(h.Cycled);
            Assert.Equal(2, h.Scheduled.Count);
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release waits: HidHide's blacklist did not read"));

            h.ListUnreadable = false;
            h.RunDue(1);
            Assert.Equal(new[] { usb }, h.Cycled);
            Assert.Equal(2, h.Scheduled.Count);
        }

        /// <summary>A held cycle whose list read fails when it comes due is
        /// not lost: the controller it was held for is reconnected once the
        /// list reads. It once was dropped, and the program that opened the
        /// controller in between kept it.</summary>
        [Fact]
        public void AHeldCycleWhoseListDoesNotReadStillRuns()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);

            h.Run(id);
            h.Clock += 5_000;
            h.Run(id);
            Assert.Single(h.Scheduled);
            Assert.Single(h.Cycled);

            h.ListUnreadable = true;
            h.Clock += HiddenControllerRelease.CooldownMs;
            h.RunDue(0);
            Assert.Single(h.Cycled);
            Assert.Equal(2, h.Scheduled.Count);

            h.ListUnreadable = false;
            h.RunDue(1);
            Assert.Equal(new[] { usb, usb }, h.Cycled);
        }

        /// <summary>A controller shown again while its batch waited for the
        /// list is left connected once the list reads.</summary>
        [Fact]
        public void AControllerShownAgainWhileItsBatchWaitsIsLeftConnected()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);

            h.ListUnreadable = true;
            h.Run(id);
            h.Shown.Add(id);
            h.ListUnreadable = false;
            h.RunDue(0);

            Assert.Empty(h.Cycled);
            lock (h.RowsSeen) Assert.Empty(h.RowsSeen);
        }

        /// <summary>Batches that wait share one timer, and the first batch
        /// that can decide takes every waiting id along, so a controller
        /// whose nodes were handed over in two batches is cycled once.</summary>
        [Fact]
        public void WaitingBatchesAreDecidedTogether()
        {
            var h = new Harness();
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[@"HID\VID_054C&PID_0CE6&MI_03\1"] = (usb, null);
            h.Map[@"USB\VID_054C&PID_0CE6&MI_03\2"] = (usb, null);

            h.ListUnreadable = true;
            h.Run(@"HID\VID_054C&PID_0CE6&MI_03\1");
            h.Run(@"USB\VID_054C&PID_0CE6&MI_03\2");
            Assert.Single(h.Scheduled);

            // The list reads again when a third release arrives, ahead of the
            // timer. That batch takes the two waiting ids with it.
            h.ListUnreadable = false;
            h.Map[@"HID\VID_F00D&PID_0001\9"] = (@"USB\VID_F00D&PID_0001\9", null);
            h.Run(@"HID\VID_F00D&PID_0001\9");
            Assert.Equal(new[] { usb, @"USB\VID_F00D&PID_0001\9" }, h.Cycled.OrderBy(d => d, StringComparer.Ordinal));
            Assert.Contains(@"HID\VID_054C&PID_0CE6&MI_03\1", h.Release.TakeCycledIds().Keys);

            // The timer finds nothing left to ask about.
            int reads = h.ListReads;
            var queued = h.Release.Pending;
            h.RunDue(0);
            Assert.Same(queued, h.Release.Pending);
            Assert.Equal(reads, h.ListReads);
            Assert.Equal(2, h.Cycled.Count);
        }

        /// <summary>Dispose drops the ids that wait for the list. The timer
        /// that comes due after it queues nothing and reads nothing.</summary>
        [Fact]
        public void AWaitingBatchDoesNotRunAfterDispose()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);

            h.ListUnreadable = true;
            h.Run(id);
            Assert.Single(h.Scheduled);
            int reads = h.ListReads;
            var queued = h.Release.Pending;

            h.Release.Dispose();
            h.ListUnreadable = false;
            h.RunDue(0);

            Assert.Same(queued, h.Release.Pending);
            Assert.Equal(reads, h.ListReads);
            Assert.Empty(h.Cycled);
        }

        /// <summary>A list that does not read because HidHide's driver does
        /// not answer at all hides nothing, so the batch ends there and says
        /// so. Nothing waits, and a later release is decided by itself.</summary>
        [Fact]
        public void ABatchEndsWhenHidHidesDriverDoesNotAnswer()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            h.Map[@"HID\VID_F00D&PID_0001\9"] = (@"USB\VID_F00D&PID_0001\9", null);

            h.ListUnreadable = true;
            h.DriverGone = true;
            h.Run(id);

            Assert.Empty(h.Cycled);
            Assert.Empty(h.Scheduled);
            Assert.Single(h.Lines, l => l == "HIDHIDE release dropped: HidHide's driver does not answer, so nothing is hidden");
            Assert.DoesNotContain(h.Lines, l => l.StartsWith("HIDHIDE release waits"));

            h.ListUnreadable = false;
            h.DriverGone = false;
            h.Run(@"HID\VID_F00D&PID_0001\9");
            Assert.Equal(new[] { @"USB\VID_F00D&PID_0001\9" }, h.Cycled);
        }

        /// <summary>The apply that follows an engine start runs before the
        /// engine has listed its devices, when every row still reads offline.
        /// Its batch waits for the listing, reading nothing meanwhile, and
        /// then counts the controllers on the USB device. A dongle whose
        /// second pad the engine found in that listing stays connected.</summary>
        [Fact]
        public void ABatchWaitsForTheEngineToListItsDevices()
        {
            var h = new Harness();
            const string id = @"HID\VID_28DE&PID_1142&MI_01\8&1&0&0000";
            const string dongle = @"USB\VID_28DE&PID_1142\6&3&0&2";
            var chain = new List<HidHideController.ReleaseHop>
            {
                Hop(id, Hid, Pad),
                Hop(@"USB\VID_28DE&PID_1142&MI_01\7&2&0&0001", Hid, Pad, "HidUsb"),
                Hop(dongle, Usb, Pad, "usbccgp"),
                Hop(@"USB\ROOT_HUB30\4&5&0&0", Usb, SystemContainer, "USBHUB3"),
            };
            h.Release.Resolve = (hid, rowsOn, _) =>
            {
                lock (h.RowsSeen) h.RowsSeen.Add((hid, rowsOn(dongle)));
                string usb = HidHideController.PickUsbDeviceToCycle(chain, 0, rowsOn, LongAgo, out string reason);
                return (usb, reason);
            };

            h.EngineListing = true;
            h.Run(id);
            Assert.Empty(h.Cycled);
            Assert.Equal(0, h.ListReads);
            lock (h.RowsSeen) Assert.Empty(h.RowsSeen);
            Assert.Equal(HiddenControllerRelease.RetryMs, Assert.Single(h.Scheduled).ms);
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release waits: PadForge's engine has not listed its devices yet"));

            // The listing found two pads on the dongle.
            h.EngineListing = false;
            h.Rows[dongle] = 2;
            h.RunDue(0);
            Assert.Empty(h.Cycled);
            Assert.Equal((id, 2), Assert.Single(h.RowsSeen));

            // The same wait with one pad there ends in its cycle.
            h.Rows[dongle] = 1;
            h.EngineListing = true;
            h.Run(id);
            h.EngineListing = false;
            h.RunDue(1);
            Assert.Equal(new[] { dongle }, h.Cycled);
        }

        /// <summary>The wait for the listing is bounded. An engine that
        /// starts suspended in the background lists nothing until PadForge
        /// has the focus again, so once
        /// <see cref="HiddenControllerRelease.ListingWaitMs"/> have passed
        /// the batch decides from the rows as they stand, as it does while
        /// the engine is stopped, and says so once. The wait runs on the
        /// release's tick clock, so releases that arrive during it neither
        /// shorten it nor start it over.</summary>
        [Fact]
        public void ABatchDoesNotWaitOnTheListingForever()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);
            h.Map[@"HID\VID_F00D&PID_0001\9"] = (@"USB\VID_F00D&PID_0001\9", null);
            int waits = (int)(HiddenControllerRelease.ListingWaitMs / HiddenControllerRelease.RetryMs);

            h.EngineListing = true;
            h.Run(id);
            // More releases than the wait has tries, all in its first moment.
            for (int more = 0; more <= waits; more++) h.Run(@"HID\VID_F00D&PID_0001\9");
            Assert.Single(h.Scheduled);
            Assert.Empty(h.Cycled);

            for (int wait = 1; wait < waits; wait++)
            {
                h.Clock += HiddenControllerRelease.RetryMs;
                h.RunDue(wait - 1);
                Assert.Equal(wait + 1, h.Scheduled.Count);
                Assert.Empty(h.Cycled);
            }

            h.Clock += HiddenControllerRelease.RetryMs;
            h.RunDue(waits - 1);
            Assert.Equal(new[] { usb, @"USB\VID_F00D&PID_0001\9" }, h.Cycled.OrderBy(d => d, StringComparer.Ordinal));
            Assert.Equal(waits, h.Scheduled.Count);
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release waits: PadForge's engine has not listed its devices yet"));
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release goes ahead"));

            // The next release starts its own wait.
            h.Clock += HiddenControllerRelease.CooldownMs;
            h.Run(id);
            Assert.Equal(waits + 1, h.Scheduled.Count);
            Assert.Equal(2, h.Cycled.Count);
        }

        /// <summary>A wait for the listing that ran out is not taken up
        /// again. A batch that then waits on HidHide's list reads the list
        /// at each try, and says once that it went ahead without the
        /// listing.</summary>
        [Fact]
        public void AListingWaitThatRanOutIsNotTakenUpAgain()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);

            h.EngineListing = true;
            h.ListUnreadable = true;
            h.Run(id);
            Assert.Equal(0, h.ListReads);

            h.Clock += HiddenControllerRelease.ListingWaitMs;
            h.RunDue(0);
            h.RunDue(1);
            Assert.Equal(2, h.ListReads);
            Assert.Empty(h.Cycled);
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release goes ahead"));
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release waits: HidHide's blacklist did not read"));

            h.ListUnreadable = false;
            h.RunDue(2);
            Assert.Equal(new[] { usb }, h.Cycled);
        }

        /// <summary>A wait for the listing belongs to one engine run. A
        /// batch that waited for it, saw the engine list its devices and
        /// then waited on HidHide's list while the engine stopped and
        /// started again waits for the new run's listing. It once took the
        /// first run's wait as run out, went ahead a moment after the new
        /// start, and counted the controllers from rows the new run had not
        /// marked yet.</summary>
        [Fact]
        public void ANewEngineRunStartsItsOwnListingWait()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);

            h.EngineListing = true;
            h.Run(id);
            Assert.Single(h.Scheduled);

            // The engine lists its devices, and HidHide's list does not read.
            h.EngineListing = false;
            h.ListUnreadable = true;
            h.Clock += HiddenControllerRelease.RetryMs;
            h.RunDue(0);
            Assert.Equal(2, h.Scheduled.Count);

            // The engine stops and starts again, longer after the first wait
            // began than a wait for the listing lasts.
            h.EngineListing = true;
            h.ListUnreadable = false;
            h.Clock += HiddenControllerRelease.ListingWaitMs;
            h.RunDue(1);
            Assert.Empty(h.Cycled);
            Assert.DoesNotContain(h.Lines, l => l.StartsWith("HIDHIDE release goes ahead"));
            Assert.Equal(3, h.Scheduled.Count);

            h.EngineListing = false;
            h.Clock += HiddenControllerRelease.RetryMs;
            h.RunDue(2);
            Assert.Equal(new[] { usb }, h.Cycled);
        }

        /// <summary>The line that says a batch went ahead without the
        /// listing is printed once for each engine run that listed nothing
        /// in time, the second run's too.</summary>
        [Fact]
        public void EachEngineRunThatListsNothingInTimeIsSaidOnce()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            int WentAhead() => h.Lines.Count(l => l.StartsWith("HIDHIDE release goes ahead"));

            h.EngineListing = true;
            h.ListUnreadable = true;
            h.Run(id);
            h.Clock += HiddenControllerRelease.ListingWaitMs;
            h.RunDue(0);
            Assert.Equal(1, WentAhead());

            // The engine lists its devices, stops and starts again.
            h.EngineListing = false;
            h.Clock += HiddenControllerRelease.RetryMs;
            h.RunDue(1);
            h.EngineListing = true;
            h.Clock += HiddenControllerRelease.RetryMs;
            h.RunDue(2);
            Assert.Equal(1, WentAhead());

            h.Clock += HiddenControllerRelease.ListingWaitMs;
            h.RunDue(3);
            Assert.Equal(2, WentAhead());
            Assert.Empty(h.Cycled);
        }

        /// <summary>The one line a wait on the list ends with, which names
        /// the ids it drops.</summary>
        private static string ListGaveUp(params string[] ids) =>
            $"HIDHIDE release dropped [{string.Join(" | ", ids)}]: HidHide's blacklist did not read for 60 s, so no USB port is cycled for these ids. Their hide is left as it is";

        /// <summary>The wait on a blacklist that does not read has a limit,
        /// on the release's own clock. HidHide's control device takes one
        /// client at a time, and a program can hold it for hours. A batch
        /// that waited without a limit reconnected its controller whenever
        /// that program let go, in the middle of whatever the controller was
        /// then used for. One millisecond short of
        /// <see cref="HiddenControllerRelease.ListWaitMs"/> the batch still
        /// asks again, and its cycle runs once the list reads.</summary>
        [Fact]
        public void ABatchStillWaitsOnTheListJustShortOfItsLimit()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);

            h.ListUnreadable = true;
            h.Run(id);
            h.Clock += HiddenControllerRelease.ListWaitMs - 1;
            h.RunDue(0);
            Assert.Equal(2, h.Scheduled.Count);
            Assert.DoesNotContain(h.Lines, l => l.StartsWith("HIDHIDE release dropped"));

            h.ListUnreadable = false;
            h.RunDue(1);
            Assert.Equal(new[] { usb }, h.Cycled);
        }

        /// <summary>At the limit the batch ends without a cycle, with one
        /// line that names its ids, and leaves no timer behind. The hide is
        /// not the release's to change. The controller is not reconnected
        /// later either, when the list reads again and another controller
        /// is hidden.</summary>
        [Fact]
        public void ABatchThatWaitedOutTheListEndsWithoutACycle()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string parent = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (parent, null);
            h.Map[parent] = (parent, null);
            h.Map[@"HID\VID_F00D&PID_0001\9"] = (@"USB\VID_F00D&PID_0001\9", null);

            h.ListUnreadable = true;
            h.Run(parent, id);
            h.Clock += HiddenControllerRelease.ListWaitMs;
            h.RunDue(0);
            Assert.Empty(h.Cycled);
            Assert.Single(h.Scheduled);
            Assert.Equal(ListGaveUp(id, parent), Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release dropped")));

            h.ListUnreadable = false;
            h.Run(@"HID\VID_F00D&PID_0001\9");
            Assert.Equal(new[] { @"USB\VID_F00D&PID_0001\9" }, h.Cycled);
            lock (h.RowsSeen) Assert.Equal(@"HID\VID_F00D&PID_0001\9", Assert.Single(h.RowsSeen).id);
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release dropped"));
        }

        /// <summary>The next wait on the list is a wait of its own, with its
        /// own line and its own sixty seconds, after one that ended at its
        /// limit.</summary>
        [Fact]
        public void AWaitOnTheListThatEndedIsNotTakenUpAgain()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);

            h.ListUnreadable = true;
            h.Run(id);
            h.Clock += HiddenControllerRelease.ListWaitMs;
            h.RunDue(0);
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release dropped"));

            // Shown and hidden again while the list still does not read.
            h.Wall = h.Wall.AddMinutes(5);
            h.Clock += 300_000;
            h.Run(id);
            Assert.Equal(2, h.Scheduled.Count);
            Assert.Equal(2, h.Lines.Count(l => l.StartsWith("HIDHIDE release waits: HidHide's blacklist did not read")));
            Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release dropped"));

            h.Clock += HiddenControllerRelease.ListWaitMs - 1;
            h.ListUnreadable = false;
            h.RunDue(1);
            Assert.Equal(new[] { usb }, h.Cycled);
        }

        /// <summary>Each id has its own limit, counted from the first time
        /// its batch found the list unread. One that joined a wait already
        /// forty seconds old is not dropped with the first.</summary>
        [Fact]
        public void EachWaitingIdHasItsOwnLimit()
        {
            var h = new Harness();
            const string first = @"HID\VID_054C&PID_0CE6\1";
            const string second = @"HID\VID_F00D&PID_0001\9";
            h.Map[first] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            h.Map[second] = (@"USB\VID_F00D&PID_0001\9", null);

            h.ListUnreadable = true;
            h.Run(first);
            h.Clock += 40_000;
            h.Run(second);
            Assert.Single(h.Scheduled);

            h.Clock += HiddenControllerRelease.ListWaitMs - 40_000;
            h.RunDue(0);
            Assert.Equal(ListGaveUp(first), Assert.Single(h.Lines, l => l.StartsWith("HIDHIDE release dropped")));
            Assert.Equal(2, h.Scheduled.Count);

            h.ListUnreadable = false;
            h.RunDue(1);
            Assert.Equal(new[] { @"USB\VID_F00D&PID_0001\9" }, h.Cycled);
        }

        /// <summary>An id handed over again while it waits was hidden again,
        /// and that hide starts its own wait on the list and carries its own
        /// time. Ten seconds short of the first hand-over's limit, the second
        /// one's wait is ten seconds old at the first hand-over's limit, and
        /// the cycle that follows counts the grace up to the second
        /// hand-over.</summary>
        [Fact]
        public void AnIdHandedOverAgainWhileItWaitsStartsItsOwnWait()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            const string usb = @"USB\VID_054C&PID_0CE6\SERIAL01";
            h.Map[id] = (usb, null);
            DateTime first = h.Wall;

            h.ListUnreadable = true;
            h.Run(id);
            h.Wall = first.AddSeconds(50);
            h.Clock += HiddenControllerRelease.ListWaitMs - 10_000;
            h.Run(id);

            h.Clock += 10_000;
            h.RunDue(0);
            Assert.DoesNotContain(h.Lines, l => l.StartsWith("HIDHIDE release dropped"));
            Assert.Equal(2, h.Scheduled.Count);

            h.ListUnreadable = false;
            h.RunDue(1);
            Assert.Equal(new[] { usb }, h.Cycled);
            Assert.Equal(first.AddSeconds(50), h.HandedOver[id]);
        }

        /// <summary>A controller that connected moments before its hide is
        /// left alone: the hide came from the arrival itself, before any
        /// program could open it. Those moments are counted up to the
        /// hand-over of its ids, not up to the batch's decision, which a
        /// wait for the engine's listing puts seconds later. A pad that
        /// arrived two seconds before its hide was once ten seconds old by
        /// the time its batch decided, and was reconnected.</summary>
        [Fact]
        public void TheArrivalGraceIsCountedUpToTheHandOver()
        {
            var h = new Harness();
            var chain = UsbDualSense(Pad);
            string id = chain[0].InstanceId;
            DateTime arrived = h.Wall.AddSeconds(-2);
            h.Release.Resolve = (hid, rowsOn, handedOverUtc) =>
            {
                string usb = HidHideController.PickUsbDeviceToCycle(chain, 0, rowsOn,
                    HidHideController.ArrivalAge(arrived, handedOverUtc), out string reason);
                return (usb, reason);
            };

            h.EngineListing = true;
            h.Run(id);

            // The engine lists its devices eight seconds later.
            h.Wall = h.Wall.AddSeconds(8);
            h.Clock += 8_000;
            h.EngineListing = false;
            h.RunDue(0);

            Assert.Empty(h.Cycled);
            Assert.Single(h.Lines, l => l == $"HIDHIDE release skipped {id}: connected moments ago");
        }

        /// <summary>The time of a hand-over is read when the ids are handed
        /// over, on the thread that hid them. A batch queued behind another
        /// one runs later and still carries it.</summary>
        [Fact]
        public void ABatchQueuedBehindAnotherKeepsTheTimeOfItsHandOver()
        {
            var h = new Harness();
            const string slow = @"HID\VID_F00D&PID_0001\9";
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[slow] = (null, "test");
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            using var resolving = new System.Threading.ManualResetEventSlim(false);
            using var go = new System.Threading.ManualResetEventSlim(false);
            var resolve = h.Release.Resolve;
            h.Release.Resolve = (hid, rowsOn, handedOverUtc) =>
            {
                if (hid == slow)
                {
                    resolving.Set();
                    go.Wait();
                }
                return resolve(hid, rowsOn, handedOverUtc);
            };
            DateTime handedOver = h.Wall;

            h.Queue(slow);
            Assert.True(resolving.Wait(TimeSpan.FromSeconds(30)));
            h.Queue(id);
            h.Wall = handedOver.AddSeconds(20);
            go.Set();
            Assert.True(h.WaitForBatches());

            Assert.Equal(handedOver, h.HandedOver[id]);
            Assert.Single(h.Cycled);
        }

        /// <summary>A held cycle keeps the hand-over time of the hide it was
        /// held for, which is not the time its batch comes due.</summary>
        [Fact]
        public void AHeldCycleKeepsTheHandOverTimeOfItsHide()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            DateTime first = h.Wall;

            h.Run(id);
            Assert.Equal(first, h.HandedOver[id]);

            // Shown and hidden again four seconds later: held by the cooldown.
            h.Wall = first.AddSeconds(4);
            h.Clock += 4_000;
            h.Run(id);
            Assert.Single(h.Scheduled);

            h.Wall = first.AddSeconds(30);
            h.Clock += HiddenControllerRelease.CooldownMs - 4_000;
            h.RunDue(0);
            Assert.Equal(2, h.Cycled.Count);
            Assert.Equal(first.AddSeconds(4), h.HandedOver[id]);
        }

        /// <summary>An id held twice is held for its latest hide. The cycle
        /// that comes due counts the grace up to that hide, the one a
        /// program may have opened the controller before.</summary>
        [Fact]
        public void AHeldIdHandedOverAgainTakesTheLaterTime()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            DateTime first = h.Wall;
            h.Run(id);

            h.Wall = first.AddSeconds(4);
            h.Clock += 4_000;
            h.Run(id);
            h.Wall = first.AddSeconds(10);
            h.Clock += 6_000;
            h.Run(id);
            Assert.Single(h.Scheduled);

            lock (h.RowsSeen) h.HandedOver.Clear();
            h.Wall = first.AddSeconds(30);
            h.Clock += HiddenControllerRelease.CooldownMs - 10_000;
            h.RunDue(0);
            Assert.Equal(2, h.Cycled.Count);
            Assert.Equal(first.AddSeconds(10), h.HandedOver[id]);
        }

        /// <summary>A held cycle that comes due while its id waits under a
        /// later hand-over joins that wait and leaves the later time in
        /// place.</summary>
        [Fact]
        public void AHeldCycleThatComesDueKeepsALaterHandOverThatWaits()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            DateTime first = h.Wall;
            h.Run(id);

            // Hidden again four seconds later: held by the cooldown.
            h.Wall = first.AddSeconds(4);
            h.Clock += 4_000;
            h.Run(id);
            Assert.Single(h.Scheduled);

            // Hidden once more at twenty seconds, when the list does not
            // read: that hand-over waits on the list.
            h.ListUnreadable = true;
            h.Wall = first.AddSeconds(20);
            h.Clock += 16_000;
            h.Run(id);
            Assert.Equal(2, h.Scheduled.Count);

            // The held cycle comes due and joins the wait.
            h.Wall = first.AddSeconds(30);
            h.Clock += HiddenControllerRelease.CooldownMs - 20_000;
            h.RunDue(0);
            Assert.Equal(2, h.Scheduled.Count);
            Assert.Single(h.Cycled);

            lock (h.RowsSeen) h.HandedOver.Clear();
            h.ListUnreadable = false;
            h.RunDue(1);
            Assert.Equal(2, h.Cycled.Count);
            Assert.Equal(first.AddSeconds(20), h.HandedOver[id]);
        }

        /// <summary>With no count of controller rows a receiver cannot be told
        /// from a single controller, so a count that does not read cycles
        /// nothing and says so.</summary>
        [Fact]
        public void ABatchIsDroppedWhenTheRowsDoNotRead()
        {
            var h = new Harness();
            const string id = @"HID\VID_054C&PID_0CE6\1";
            h.Map[id] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            h.Release.ControllerRows = () => throw new InvalidOperationException("rows");

            h.Run(id);

            Assert.Empty(h.Cycled);
            lock (h.RowsSeen) Assert.Empty(h.RowsSeen);
            Assert.Single(h.Lines, l => l == "HIDHIDE release dropped: PadForge's controller rows did not read (InvalidOperationException), so no USB device is known to carry one controller");
        }

        /// <summary>A controller cycled comes back on a new device stack, and
        /// the apply opens it there once more, so the release names its HID
        /// ids once, each with the time of the cycle, stamped before the
        /// cycle is asked for. A cycle that threw or was held names
        /// nothing.</summary>
        [Fact]
        public void ACycledControllerIsNamedOnceForTheNextCheck()
        {
            var h = new Harness();
            h.Map[@"HID\VID_054C&PID_0CE6&MI_03\1"] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            h.Map[@"USB\VID_054C&PID_0CE6&MI_03\2"] = (@"USB\VID_054C&PID_0CE6\SERIAL01", null);
            h.Map[@"HID\VID_F00D&PID_0001\1"] = (@"USB\VID_F00D&PID_0001\1", null);
            var stamp = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
            h.Release.UtcNow = () => stamp;
            h.Release.CyclePort = usb =>
            {
                if (usb.Contains("F00D")) throw new InvalidOperationException("hub refused");
                h.Cycled.Add(usb);
                // A clock read after the cycle would stamp this later time.
                stamp = stamp.AddSeconds(5);
            };

            h.Run(@"HID\VID_054C&PID_0CE6&MI_03\1", @"USB\VID_054C&PID_0CE6&MI_03\2", @"HID\VID_F00D&PID_0001\1");

            var named = h.Release.TakeCycledIds();
            Assert.Equal(new[] { @"HID\VID_054C&PID_0CE6&MI_03\1" }, named.Keys);
            Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), named[@"hid\vid_054c&pid_0ce6&mi_03\1"]);
            Assert.Empty(h.Release.TakeCycledIds());

            h.Run(@"HID\VID_054C&PID_0CE6&MI_03\1");
            Assert.Empty(h.Release.TakeCycledIds());
        }

        /// <summary>The count of controller rows reaches the device decision
        /// for the USB device each id resolves to.</summary>
        [Fact]
        public void TheControllerRowsReachTheDecision()
        {
            var h = new Harness();
            h.Map[@"HID\VID_28DE&PID_1142&MI_01\1"] = (@"USB\VID_28DE&PID_1142\DONGLE", null);

            h.Rows[@"usb\vid_28de&pid_1142\dongle"] = 2;
            h.Run(@"HID\VID_28DE&PID_1142&MI_01\1");

            Assert.Equal((@"HID\VID_28DE&PID_1142&MI_01\1", 2), Assert.Single(h.RowsSeen));
        }

        /// <summary>The Hide from Games tooltip promises the reconnect, so it
        /// names the receiver the release leaves alone, in every language.
        /// Read from the resx sources, the way LocaleParityTests reads
        /// them.</summary>
        [Theory]
        [InlineData("Strings.resx", "receiver")]
        [InlineData("Strings.de.resx", "Empfänger")]
        [InlineData("Strings.es.resx", "receptor")]
        [InlineData("Strings.fr.resx", "récepteur")]
        [InlineData("Strings.it.resx", "ricevitore")]
        [InlineData("Strings.ja.resx", "レシーバー")]
        [InlineData("Strings.ko.resx", "수신기")]
        [InlineData("Strings.nl.resx", "ontvanger")]
        [InlineData("Strings.pt-BR.resx", "receptor")]
        [InlineData("Strings.zh-Hans.resx", "接收器")]
        public void TheHideTooltipNamesTheReceiverItLeavesAlone(string file, string receiver)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string xml = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Resources", "Strings", file));
            var m = System.Text.RegularExpressions.Regex.Match(xml,
                @"<data name=""Devices_HideTooltip""[^>]*>\s*<value>(.*?)</value>", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(m.Success, file);
            Assert.Contains(receiver, m.Groups[1].Value);
        }

        /// <summary>A receiver whose pads are HID interfaces carries no XUSB
        /// node, and a Steam Controller dongle carries up to four pads on
        /// interfaces 1 to 4 (SDL_hidapi_steam.c). PadForge's controller rows
        /// on the USB device count, so a second pad on the dongle keeps it
        /// connected, and a dongle with one pad is cycled as before.</summary>
        [Fact]
        public void AReceiverWithASecondControllerRowIsLeftAlone()
        {
            var chain = new List<HidHideController.ReleaseHop>
            {
                Hop(@"HID\VID_28DE&PID_1142&MI_01\8&1&0&0000", Hid, Pad),
                Hop(@"USB\VID_28DE&PID_1142&MI_01\7&2&0&0001", Hid, Pad, "HidUsb"),
                Hop(@"USB\VID_28DE&PID_1142\6&3&0&2", Usb, Pad, "usbccgp"),
                Hop(@"USB\ROOT_HUB30\4&5&0&0", Usb, SystemContainer, "USBHUB3"),
            };
            int Rows(string usb, int n) => usb == @"USB\VID_28DE&PID_1142\6&3&0&2" ? n : 0;

            Assert.Null(HidHideController.PickUsbDeviceToCycle(chain, 0, usb => Rows(usb, 2), LongAgo, out string reason));
            Assert.Equal("the USB device carries more than one controller", reason);

            Assert.Equal(@"USB\VID_28DE&PID_1142\6&3&0&2",
                HidHideController.PickUsbDeviceToCycle(chain, 0, usb => Rows(usb, 1), LongAgo, out _));
            Assert.Equal(@"USB\VID_28DE&PID_1142\6&3&0&2",
                HidHideController.PickUsbDeviceToCycle(chain, 0, null, LongAgo, out _));
            // The Xbox node count still decides where it is the larger.
            Assert.Null(HidHideController.PickUsbDeviceToCycle(chain, 2, usb => Rows(usb, 1), LongAgo, out _));
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

        /// <summary>The release says when it last cycled a port, on its tick
        /// clock. The device notes (#597) read it, so a controller that
        /// PadForge's own cycle drops and brings back raises no note. A
        /// controller that is skipped, or held back by the cooldown, moves
        /// nothing.</summary>
        [Fact]
        public void TheReleaseSaysWhenItLastCycledAPort()
        {
            var h = new Harness();
            h.Map[@"HID\A\1"] = (@"USB\A\1", null);
            h.Map[@"HID\BT\1"] = (null, "Bluetooth");
            Assert.Equal(-1, h.Release.LastCycleAt);

            h.Run(@"HID\BT\1");
            Assert.Empty(h.Cycled);
            Assert.Equal(-1, h.Release.LastCycleAt);

            h.Clock = 2_000_000;
            h.Run(@"HID\A\1");
            Assert.Equal(new[] { @"USB\A\1" }, h.Cycled);
            Assert.Equal(2_000_000, h.Release.LastCycleAt);

            // Inside the cooldown the cycle is held, and nothing was cycled.
            h.Clock += HiddenControllerRelease.CooldownMs - 1;
            h.Run(@"HID\A\1");
            Assert.Single(h.Cycled);
            Assert.Equal(2_000_000, h.Release.LastCycleAt);

            // The held cycle runs once the cooldown ends, and is the last.
            h.Clock += 1;
            h.RunDue(0);
            Assert.Equal(2, h.Cycled.Count);
            Assert.Equal(2_000_000 + HiddenControllerRelease.CooldownMs, h.Release.LastCycleAt);
        }

        /// <summary>A release asked for after Dispose queues nothing.</summary>
        [Fact]
        public void NothingRunsAfterDispose()
        {
            var h = new Harness();
            h.Map[@"HID\A\1"] = (@"USB\A\1", null);
            h.Release.Dispose();
            var queued = h.Release.Pending;
            h.Run(@"HID\A\1");
            Assert.Same(queued, h.Release.Pending);
            Assert.Equal(0, h.ListReads);
            Assert.Empty(h.Cycled);
        }
    }
}
