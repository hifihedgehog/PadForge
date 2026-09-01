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

            Assert.Contains("if (!ud.QuickChargeEnabled) return;", body);
            Assert.Contains("QuickChargeEdge(ud, state.BatteryCharging)", body);
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

        /// <summary>The persistence sibling set: the row fill and the row
        /// flush both carry the flag, like the idle-disconnect legs they sit
        /// beside, and the Devices page checkbox uses Click (never Checked,
        /// the #161 lesson) with both localized strings bound.</summary>
        [Fact]
        public void SiblingLegs_FillFlushAndCheckbox()
        {
            string fill = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("row.QuickChargeEnabled = ud.QuickChargeEnabled;", fill);

            string flush = RepoText("PadForge.App", "Services", "DeviceService.cs");
            Assert.Contains("ud.QuickChargeEnabled = row.QuickChargeEnabled;", flush);

            string page = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.Contains("Binding SelectedDevice.QuickChargeEnabled, Mode=TwoWay", page);
            Assert.Contains("Click=\"QuickCharge_Click\"", page);
            Assert.Contains("Devices_QuickCharge,", page);
            Assert.Contains("Devices_QuickChargeTooltip,", page);
            Assert.DoesNotContain("Checked=\"QuickCharge", page);
        }
    }
}
