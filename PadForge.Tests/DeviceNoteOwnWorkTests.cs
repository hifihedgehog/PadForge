using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// PadForge's own work that takes device rows offline (#600 item 42):
    /// the stamps it leaves (<see cref="OwnDrops"/>), each cause's stamp
    /// ahead of the drop, and the census of every site that takes a row
    /// offline, so a new one has to be classed on purpose.
    /// </summary>
    public class DeviceNoteOwnWorkTests
    {
        // ── the stamps ─────────────────────────────────────────────────

        [Fact]
        public void AStamp_NamesItsRow_OrCoversEveryRow_AndASettleStandsApart()
        {
            var mine = Guid.NewGuid();
            long before = Environment.TickCount64;
            OwnDrops.Row(mine);
            Assert.True(OwnDrops.RowAt(mine) >= before);
            Assert.Equal(OwnDrops.Never, OwnDrops.RowAt(Guid.NewGuid()));
            // An empty ID is no row.
            OwnDrops.Row(Guid.Empty);
            Assert.Equal(OwnDrops.Never, OwnDrops.RowAt(Guid.Empty));

            Thread.Sleep(20);
            long every = Environment.TickCount64;
            OwnDrops.Every();
            Assert.True(OwnDrops.EveryAt >= every);

            long settle = Environment.TickCount64;
            OwnDrops.Settle();
            Assert.True(OwnDrops.SettleAt >= settle);
        }

        /// <summary>Work that lasts, a virtual controller's bring-up: every
        /// row counts as stamped now for as long as it runs, however long,
        /// and is stamped again at its end.</summary>
        [Fact]
        public void WorkThatLasts_StampsEveryRowUntilItEnds()
        {
            OwnDrops.Begin();
            try
            {
                Thread.Sleep(30);
                long during = Environment.TickCount64;
                Assert.True(OwnDrops.EveryAt >= during);
            }
            finally { OwnDrops.End(); }
            long after = Environment.TickCount64;
            Thread.Sleep(30);
            // No longer now: the end's own stamp.
            Assert.True(OwnDrops.EveryAt <= after && OwnDrops.EveryAt >= after - 1000);
        }

        /// <summary>The causes no test host reaches, read from the source:
        /// each stamps before the work that drops the row, and work that
        /// lasts stamps again when it ends.</summary>
        [Theory]
        [InlineData("PadForge.App/Common/Input/InputManager.Step5.VirtualDevices.cs", "private IVirtualController CreateVirtualController(",
            "if (hid) PadForge.Engine.OwnDrops.Begin();", "vc.Connect();")]
        [InlineData("PadForge.App/Common/Input/InputManager.cs", "public void RescanWiiControllers()",
            "PadForge.Engine.OwnDrops.Every();", "SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_WII, \"0\");")]
        [InlineData("PadForge.App/Common/Input/InputManager.cs", "private static bool WriteFlydigiHintUnderJoystickLock(string value)",
            "PadForge.Engine.OwnDrops.Every();", "SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_FLYDIGI, value)")]
        [InlineData("PadForge.App/Common/Input/InputManager.cs", "public static bool HoldXbox360HidapiDriver(bool off)",
            "PadForge.Engine.OwnDrops.Every();", "WriteHintUnderJoystickLock(SDL_HINT_JOYSTICK_HIDAPI_XBOX_360")]
        [InlineData("PadForge.App/Common/Input/InputManager.cs", "internal static void AdvanceUsioReopen(long owner) =>",
            "PadForge.Engine.OwnDrops.Every();", "WriteHintUnderJoystickLock(SDL_HINT_JOYSTICK_HIDAPI_USIO, value)")]
        [InlineData("PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs", "public void ShutdownMidiInputs()",
            "PadForge.Engine.OwnDrops.Row(ud.InstanceGuid);", "ud.IsOnline = false;")]
        [InlineData("PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs", "private void DropMidiInputsForNewBackend()",
            "PadForge.Engine.OwnDrops.Row(ud.InstanceGuid);", "ud.IsOnline = false;")]
        [InlineData("PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs", "internal void CloseMidiInputsForEndpoint(string uniqueEndpointId)",
            "PadForge.Engine.OwnDrops.Row(ud.InstanceGuid);", "ud.IsOnline = false;")]
        [InlineData("PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs", "var dev = _openedAnalogKeyboards[path];",
            "if (!enabled) PadForge.Engine.OwnDrops.Row(ud.InstanceGuid);", "ud.IsOnline = false;")]
        [InlineData("PadForge.App/Common/Input/InputManager.Falcon.cs", "var dev = _falconOpen[id];",
            "if (!enabled) PadForge.Engine.OwnDrops.Row(dev.InstanceGuid);", "RetireFalconRow(dev);")]
        [InlineData("PadForge.App/Common/Input/InputManager.Touchpads.cs", "_touchpadTreeDue.Remove(path);",
            "PadForge.Engine.OwnDrops.Row(device.InstanceGuid);", "RetireTouchpadMouse(device);")]
        [InlineData("PadForge.App/Services/Ds3DriverInstaller.cs", "string hwid = @\"USB\\VID_054C&\" + pidToken;",
            "PadForge.Engine.OwnDrops.Settle();", "UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hwid,")]
        [InlineData("PadForge.Engine/Common/Tablets/TabletNative.cs", "internal static void RestartCollection(string instance, CancellationToken stop)",
            "OwnDrops.Every();", "CM_Disable_DevNode(node, 0);")]
        [InlineData("PadForge.App/Services/Ds3DriverInstaller.cs", "public static void CycleBluetoothRadio(Action<string> log)",
            "PadForge.Engine.OwnDrops.Settle();", "radio.ToUsbPnPDevice().CyclePort();")]
        [InlineData("PadForge.App/Services/Ds3DriverInstaller.cs", "internal static void RestartOffRadio(Action<string> log)",
            "PadForge.Engine.OwnDrops.Settle();", "radio.ToUsbPnPDevice().CyclePort();")]
        [InlineData("PadForge.App/Services/VendorUsbDriverInstaller.cs", "internal static bool Bind(BindPlan plan,",
            "PadForge.Engine.OwnDrops.Settle();", "bool xusb = plan.Driver == BindDriver.Xusb22;")]
        [InlineData("PadForge.App/Services/VendorUsbDriverInstaller.cs", "internal static bool Restore(string instanceId,",
            "PadForge.Engine.OwnDrops.Settle();", "bool present = ServiceOf(instanceId) != null;")]
        public void EachOwnCause_StampsBeforeItsDrop(string file, string from, string stamp, string drop)
        {
            string source = ExtrasPageTests.RepoText(file.Split('/')).Replace("\r", "");
            int at = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(at >= 0, from);
            int dropAt = source.IndexOf(drop, at, StringComparison.Ordinal);
            Assert.True(dropAt > at, drop + " is not after " + from);
            int stampAt = source.IndexOf(stamp, at, StringComparison.Ordinal);
            Assert.True(stampAt > at && stampAt < dropAt, stamp + " does not come before " + drop + " in " + file);
            // Work that lasts is stamped again when it ends: an HIDMaestro
            // bring-up takes seconds, and a collection comes back at its
            // enable.
            bool lasts = file.EndsWith("VirtualDevices.cs", StringComparison.Ordinal);
            string ends = lasts ? drop
                : file.EndsWith("TabletNative.cs", StringComparison.Ordinal) ? "CM_Enable_DevNode(node, 0);" : null;
            if (ends != null)
            {
                string endStamp = lasts ? "finally { if (hid) PadForge.Engine.OwnDrops.End(); }" : stamp;
                int end = source.IndexOf(ends, at, StringComparison.Ordinal);
                int again = source.IndexOf(endStamp, end, StringComparison.Ordinal);
                Assert.True(end > 0 && again > end && again - end < 250, "no stamp after " + ends);
            }
            // A fence of the test host is still asked first (TestHostFenceTests).
            foreach (string fence in new[] { "if (OffLimitsForTest)", "if (NoDriverChangesForTest)" })
            {
                int asked = source.IndexOf(fence, at, StringComparison.Ordinal);
                if (asked > 0 && asked < dropAt) Assert.True(asked < stampAt, "the stamp comes before the fence in " + from);
            }
        }

        // ── the census ─────────────────────────────────────────────────

        /// <summary>Every site that takes a device row offline or sets its
        /// online flag to anything but true, by file and
        /// method, with how many it holds there and what it is (the census
        /// in the lane report). A site added, moved or removed fails here
        /// until it is classed: a real removal is said, PadForge's own work
        /// is stamped (<see cref="OwnDrops"/>), and a row that raises no note
        /// needs neither.</summary>
        private static readonly Dictionary<string, (int Count, string Class)> Census = new()
        {
            ["PadForge.App/Common/Input/InputManager.Step2.UpdateInputStates.cs UpdateInputStates"] = (2, "real, or SDL reopening under own work stamped at its cause"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UpdateDevices"] = (4, "real: touchpad handle left, SDL debounce, Step 2's finish; merged row raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs NeutralizeMappedOutputsFor"] = (1, "helper"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs DetectDisconnectedHandles"] = (1, "real"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs ShutdownMidiInputs"] = (1, "own, stamped per row"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs DropMidiInputsForNewBackend"] = (1, "own, stamped per row"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs CloseMidiInputsForEndpoint"] = (1, "own, stamped per row"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UpdateMidiInputDevices"] = (1, "real"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UpdateMicrophoneDevices"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs ShutdownMicrophoneDevices"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs ShutdownNfcReaders"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UpdateNfcReaderDevices"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UpdateHeadsetMotionDevices"] = (1, "real"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs ShutdownHeadsetMotionInputs"] = (1, "own, app close after the reset"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UpdateHandheldDevices"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs RetireHandheldRows"] = (2, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs RetireHeadTrackerRow"] = (2, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs RetireLogitechGKeysRow"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UpdateAnalogKeyboardDevices"] = (1, "real, or own when switched off, stamped"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs ShutdownAnalogKeyboardInputs"] = (1, "own, app close after the reset"),
            ["PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs UnregisterExternalDevice"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.Falcon.cs RetireFalconRow"] = (1, "real, or own when switched off, stamped at its caller"),
            ["PadForge.App/Common/Input/InputManager.Tablets.cs UpdateTabletDevices"] = (1, "real"),
            ["PadForge.App/Common/Input/InputManager.TouchSenseMice.cs RetireTouchSenseMouse"] = (1, "real. A driver change of PadForge's settles"),
            ["PadForge.App/Common/Input/InputManager.WinUsbKeyboards.cs RetireWinUsbKeyboard"] = (1, "real. A driver change of PadForge's settles"),
            ["PadForge.App/Common/Input/InputManager.Touchpads.cs RetireTouchpadMouse"] = (1, "own, stamped at its caller"),
            ["PadForge.App/Common/Input/InputManager.GameTelemetry.cs RetireGameTelemetryRow"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.KeyboardScreens.cs RetireKeyboardScreenRow"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.PeripheralRows.cs RetirePeripheralRow"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.PeripheralRows.cs RetirePeripheralUnitRow"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.TwitchChat.cs RetireTwitchChatRow"] = (1, "raises none"),
            ["PadForge.App/Common/Input/InputManager.cs CloseAllDevices"] = (1, "engine stop after the reset"),
            ["PadForge.App/Common/Input/PsMoveDirectService.cs MintIdentityRow"] = (1, "a new record"),
            ["PadForge.App/Services/Automation/Assistant/AssistantTools.cs DeviceBy"] = (1, "a stand-in, not on the list"),
            ["PadForge.App/Services/Automation/AutomationSessions.cs Stand"] = (1, "a stand-in, not on the list"),
            ["PadForge.App/Services/InputService.Touchpads.cs AddOfflineRowOf"] = (1, "a new record"),
            ["PadForge.App/Services/InputService.cs Stop"] = (1, "the interface's rows at an engine stop"),
            ["PadForge.App/Services/InputService.cs PopulateDeviceRow"] = (1, "the Devices list's row copies its device"),
            ["PadForge.App/Services/InputService.cs RefreshSlotSummaryProperties"] = (1, "a controller card's roster copies its devices"),
            ["PadForge.App/Services/InputService.cs UpdatePadDeviceInfo"] = (1, "a controller's mapped-device list copies its devices"),
            ["PadForge.App/Services/InputService.cs SyncMappedDevices"] = (1, "the same list, updated in place"),
            ["PadForge.App/Services/SettingsService.cs CarryRuntimeState"] = (1, "a reloaded record takes its twin's state, no change"),
            ["PadForge.App/ViewModels/SettingsViewModel.cs UpdatePeerOnlineStatus"] = (1, "a Remote Link peer's dot, no device row"),
            ["PadForge.App/Services/PeripheralSwitchMigration.cs NewRowRecord"] = (1, "a new record"),
            ["PadForge.Engine/Data/UserDevice.cs ClearRuntimeState"] = (1, "helper"),
        };

        private static readonly Regex Offline = new(@"\bIsOnline\s*=(?!=)(?!\s*true\b)|\bMarkDeviceOffline\(|\bClearRuntimeState\(\)");
        private static readonly Regex Declaration = new(@"\bvoid\s+(MarkDeviceOffline|ClearRuntimeState)\(");
        private static readonly Regex Method = new(@"^\s*(?:public|private|internal|protected)\b[^=;]*?\b(\w+)\s*(?:<[^>]*>)?\s*\(");

        /// <summary>The sites the source holds now, as the census keys
        /// them.</summary>
        internal static Dictionary<string, int> SitesNow()
        {
            string root = OffLimitsSource.RepoRoot();
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
            {
                foreach (string path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                    string[] lines = File.ReadAllText(path).Replace("\r", "").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmed = lines[i].TrimStart();
                        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
                        if (!Offline.IsMatch(lines[i]) || Declaration.IsMatch(lines[i])) continue;
                        string method = "?";
                        for (int j = i; j >= 0; j--)
                        {
                            var m = Method.Match(lines[j]);
                            if (m.Success && !lines[j].TrimEnd().EndsWith(";", StringComparison.Ordinal)) { method = m.Groups[1].Value; break; }
                        }
                        string key = relative + " " + method;
                        found[key] = found.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                }
            }
            return found;
        }

        [Fact]
        public void EverySiteThatTakesARowOffline_IsInTheCensus()
        {
            var now = SitesNow();
            var unlisted = now.Where(s => !Census.TryGetValue(s.Key, out var c) || c.Count != s.Value)
                .Select(s => s.Key + " x" + s.Value).ToList();
            var gone = Census.Where(c => !now.ContainsKey(c.Key)).Select(c => c.Key).ToList();
            Assert.True(unlisted.Count == 0, "Sites not in the census, or with another count: " + string.Join("; ", unlisted));
            Assert.True(gone.Count == 0, "Census sites the source no longer has: " + string.Join("; ", gone));
        }

        /// <summary>The scan finds what it is meant to find: a site written
        /// three ways, and none in a comment or a declaration.</summary>
        [Fact]
        public void TheCensusScan_FindsEachFormOfASite()
        {
            Assert.Matches(Offline, "                ud.IsOnline = false;");
            Assert.Matches(Offline, "                IsOnline = false,");
            Assert.Matches(Offline, "                MarkDeviceOffline(ud);");
            Assert.Matches(Offline, "            ud.ClearRuntimeState();");
            Assert.Matches(Offline, "                ud.IsOnline = stillThere;");
            Assert.DoesNotMatch(Offline, "                ud.IsOnline = true;");
            Assert.DoesNotMatch(Offline, "                if (ud.IsOnline == false) return;");
            Assert.Matches(Declaration, "        private void MarkDeviceOffline(UserDevice ud)");
            Assert.Equal("RetireFalconRow", Method.Match("        private void RetireFalconRow(FalconDevice dev)").Groups[1].Value);
            Assert.Equal("At", Method.Match("        public static long At(Guid device)").Groups[1].Value);
        }
    }
}
