using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Quick Charge (#372, asked in discussion #367, reworked after
    /// @Jobima1st's bench showed the first pass's USB-twin scan never
    /// firing): the trigger is the Bluetooth-connected pad's OWN charging
    /// report, a rising-edge one-shot on the same record the checkbox
    /// lives on, dropped through the idle timeout's hardware-confirmed
    /// disconnect lane.
    /// </summary>
    public class QuickChargeTests
    {
        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>The opt-in persists per device, and the edge memory
        /// never does: a restart reads the live charging state fresh.</summary>
        [Fact]
        public void FlagPersists_EdgeMemoryDoesNot()
        {
            var ser = new XmlSerializer(typeof(UserDevice));
            var ud = new UserDevice { QuickChargeEnabled = true, QuickChargePrevCharging = true };
            using var w = new StringWriter();
            ser.Serialize(w, ud);
            string xml = w.ToString();
            Assert.Contains("QuickChargeEnabled", xml);
            Assert.DoesNotContain("QuickChargePrevCharging", xml);

            using var r = new StringReader(xml);
            var back = (UserDevice)ser.Deserialize(r);
            Assert.True(back.QuickChargeEnabled);
            Assert.False(back.QuickChargePrevCharging);
        }

        /// <summary>The pure edge decision: fires exactly once when charging
        /// goes false to true, stays quiet while charging persists, and a
        /// false read re-arms it for the next plug.</summary>
        [Fact]
        public void Edge_FiresOncePerPlugCycle()
        {
            var ud = new UserDevice();

            Assert.False(InputManager.QuickChargeEdge(ud, false));   // resting
            Assert.True(InputManager.QuickChargeEdge(ud, true));     // plug: FIRE
            Assert.False(InputManager.QuickChargeEdge(ud, true));    // still plugged
            Assert.False(InputManager.QuickChargeEdge(ud, true));
            Assert.False(InputManager.QuickChargeEdge(ud, false));   // unplug: re-arm
            Assert.True(InputManager.QuickChargeEdge(ud, true));     // next plug: FIRE
        }

        /// <summary>The shipped promise: a user who re-links Bluetooth while
        /// the cable stays in is left alone. The edge memory survives the
        /// reconnect (no reset anywhere in the fresh-connection stamp), so
        /// the resumed charging=true reads carry no edge.</summary>
        [Fact]
        public void RelinkWhilePlugged_IsLeftAlone()
        {
            var ud = new UserDevice();
            Assert.True(InputManager.QuickChargeEdge(ud, true));     // plug fired, link dropped

            // The reconnect creates a fresh wrapper, reads resume, cable
            // still in. Prev stayed true across the gap, so no edge.
            Assert.False(InputManager.QuickChargeEdge(ud, true));
            Assert.False(InputManager.QuickChargeEdge(ud, true));

            // The fresh-connection stamp must NOT touch the edge memory.
            string src = RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            int idle = src.IndexOf("private static void UpdateIdleDisconnect(", StringComparison.Ordinal);
            Assert.True(idle > 0);
            string stamp = src.Substring(idle, src.IndexOf("if (ud.IdleDisconnectSeconds <= 0)", idle) - idle);
            Assert.DoesNotContain("QuickChargePrevCharging = ", stamp);
            Assert.DoesNotContain("QuickChargeHandled", stamp);
            Assert.Contains("CheckQuickCharge(ud, state, now);", stamp);
        }

        /// <summary>The trigger's source contract: opt-in gated, driven by
        /// the state's charging read through the pure edge, then BOTH
        /// post-edge shapes. A Bluetooth-pathed record (wall charger, or a
        /// pad SDL does not de-dup) drops through FireIdleDisconnect, the
        /// idle timeout's own lane. A wired-rebound record (the cable
        /// rewrote DevicePath because identity keys on the shared MAC
        /// serial, so no twin record ever exists) drops the radio by its
        /// own serial. Every branch logs a QUICKCHARGE line, so the next
        /// trace speaks whichever way it goes. The old cross-record scan
        /// stays gone.</summary>
        [Fact]
        public void TriggerContract_BothPostEdgeShapes()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            int at = src.IndexOf("private static void CheckQuickCharge", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = src.IndexOf("internal static bool QuickChargeEdge", at);
            Assert.True(end > at);
            string body = src.Substring(at, end - at);

            Assert.Contains("if (!ud.QuickChargeEnabled)", body);
            // Off forgets the observation, so the next enable seeds afresh.
            Assert.Contains("if (ud.LastQuickChargeCheckTick != 0) ud.LastQuickChargeCheckTick = 0;", body);
            Assert.Contains("QuickChargeStep(ud, state.BatteryCharging, now)", body);
            // Bluetooth-pathed shape: the #162 lane.
            Assert.Contains("DeviceTransport.IsBluetooth(ud.DevicePath", body);
            Assert.Contains("BluetoothLinkHelper.IsDisconnectTarget(ud.DevicePath", body);
            Assert.Contains("FireIdleDisconnect(ud);", body);
            // Wired-rebind shape: radio drop by the record's own MAC.
            Assert.Contains("BluetoothLinkHelper.TryParseAddress(ud.SerialNumber", body);
            Assert.Contains("BluetoothLinkHelper.TryDisconnect(qcSerial)", body);
            // Diagnosability: all four outcomes print.
            Assert.Equal(4, body.Split("QUICKCHARGE ").Length - 1);
            // The cross-record scan stays gone, and so does its latch.
            Assert.DoesNotContain("ReferenceEquals(d, ud)", body);
            Assert.DoesNotContain("QuickChargeHandled", src);
        }

        /// <summary>THE F24 BUG. Both edge fields are in-memory, so after
        /// an app restart the memory is the default false, and a pad that
        /// was plugged in with its radio deliberately re-linked read its
        /// first charging=true as a plug edge and lost the link the user
        /// chose to keep. The first observation now seeds the memory and
        /// is never an edge. Then the cadence, then the real edge.</summary>
        [Fact]
        public void Step_FirstObservationSeedsNeverFires()
        {
            var ud = new UserDevice();

            // Restart with the cable in: seed, no drop.
            Assert.False(InputManager.QuickChargeStep(ud, true, 1000));
            Assert.True(ud.QuickChargePrevCharging);
            Assert.Equal(1000, ud.LastQuickChargeCheckTick);
            // Still plugged a second later: no edge.
            Assert.False(InputManager.QuickChargeStep(ud, true, 2100));
            // Unplug re-arms.
            Assert.False(InputManager.QuickChargeStep(ud, false, 3200));
            // Within the cadence window: not even looked at.
            Assert.False(InputManager.QuickChargeStep(ud, true, 3500));
            // The next real plug edge fires.
            Assert.True(InputManager.QuickChargeStep(ud, true, 4300));
            Assert.False(InputManager.QuickChargeStep(ud, true, 5400));

            // The same seed rule covers turning the checkbox on while
            // already plugged: the first read is a seed, not a drop.
            var enabledWhilePlugged = new UserDevice();
            Assert.False(InputManager.QuickChargeStep(enabledWhilePlugged, true, 9000));
            Assert.False(InputManager.QuickChargeStep(enabledWhilePlugged, true, 10100));

            // A fresh record whose first read is unplugged: the seed is
            // false and the first plug fires as before.
            var unplugged = new UserDevice();
            Assert.False(InputManager.QuickChargeStep(unplugged, false, 1000));
            Assert.True(InputManager.QuickChargeStep(unplugged, true, 2100));
        }

        /// <summary>The persistence sibling set: the row fill and the row
        /// flush both carry the flag, like the idle-disconnect legs they sit
        /// beside, and the Devices page checkbox uses Click (never Checked,
        /// the #161 lesson) with both localized strings bound.</summary>
        [Fact]
        public void SiblingLegs_FillFlushAndCheckbox()
        {
            string fill = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("row.QuickChargeEnabled = ud.QuickChargeEnabled;", fill);
            Assert.Contains("row.ShowQuickCharge = DeviceRowViewModel.ComputeShowQuickCharge(ud.DevicePath, ud.VendorId, ud.ProdId, ud.SerialNumber);", fill);

            string flush = RepoText("PadForge.App", "Services", "DeviceService.cs");
            Assert.Contains("ud.QuickChargeEnabled = row.QuickChargeEnabled;", flush);

            string page = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.Contains("Binding SelectedDevice.QuickChargeEnabled, Mode=TwoWay", page);
            Assert.Contains("Click=\"QuickCharge_Click\"", page);
            Assert.Contains("Devices_QuickCharge,", page);
            Assert.Contains("Devices_QuickChargeTooltip,", page);
            Assert.DoesNotContain("Checked=\"QuickCharge", page);
            // F23: the checkbox sits in its own container on its own gate,
            // the section on the union, the idle row on its own.
            Assert.Contains("Binding SelectedDevice.ShowPowerSection, Converter", page);
            Assert.Contains("Binding SelectedDevice.ShowQuickCharge, Converter", page);
            Assert.Contains("Binding SelectedDevice.ShowIdleDisconnect, Converter", page);
            int quickGate = page.IndexOf("Binding SelectedDevice.ShowQuickCharge, Converter", StringComparison.Ordinal);
            int checkbox = page.IndexOf("Click=\"QuickCharge_Click\"", StringComparison.Ordinal);
            Assert.True(quickGate > 0 && quickGate < checkbox);
        }

        /// <summary>THE F23 BUG. The checkbox lived behind ShowIdleDisconnect,
        /// which is false for a USB path, and the USB cable rebinds a Sony
        /// record to the USB path (identity keys on the shared MAC serial),
        /// so the checkbox vanished exactly while the pad was plugged in.
        /// ShowQuickCharge is the disconnect gate OR the wired-path gate
        /// CheckQuickCharge fires on: a serial that parses as a non-zero
        /// Bluetooth address.</summary>
        [Fact]
        public void ShowQuickCharge_UsbReboundSonyWithMacSerial()
        {
            const string usb = @"\\?\hid#vid_054c&pid_0ce6&mi_03#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string bt = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

            // The idle gate says no to the USB path. The Quick Charge gate says yes.
            Assert.False(BluetoothLinkHelper.IsDisconnectTarget(usb, 0x054C, 0x0CE6, "aa:bb:cc:dd:ee:ff"));
            Assert.True(PadForge.ViewModels.DeviceRowViewModel.ComputeShowQuickCharge(usb, 0x054C, 0x0CE6, "aa:bb:cc:dd:ee:ff"));
            Assert.True(PadForge.ViewModels.DeviceRowViewModel.ComputeShowQuickCharge(usb, 0x054C, 0x0CE6, "aabbccddeeff"));
            // No parseable address, no wired-path drop, no checkbox.
            Assert.False(PadForge.ViewModels.DeviceRowViewModel.ComputeShowQuickCharge(usb, 0x054C, 0x0CE6, "1234"));
            Assert.False(PadForge.ViewModels.DeviceRowViewModel.ComputeShowQuickCharge(usb, 0x054C, 0x0CE6, ""));
            Assert.False(PadForge.ViewModels.DeviceRowViewModel.ComputeShowQuickCharge(usb, 0x054C, 0x0CE6, null));
            // A zero address is what the wired path refuses too.
            Assert.False(PadForge.ViewModels.DeviceRowViewModel.ComputeShowQuickCharge(usb, 0x054C, 0x0CE6, "00:00:00:00:00:00"));
            // The Bluetooth-pathed shape shows with any serial.
            Assert.True(PadForge.ViewModels.DeviceRowViewModel.ComputeShowQuickCharge(bt, 0x054C, 0x0CE6, ""));

            // The view model: Quick Charge alone still draws the section
            // and the rule under it.
            var vm = new PadForge.ViewModels.DeviceRowViewModel
            {
                DeviceTypeKey = "Gamepad",
                DevicePath = usb,
                ShowIdleDisconnect = false,
                ShowQuickCharge = true,
            };
            Assert.True(vm.ShowPowerSection);
            Assert.True(vm.ShowRawInputDivider);
            vm.ShowQuickCharge = false;
            Assert.False(vm.ShowPowerSection);
        }
    }
}
