using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Delta audit 2026-09-03 (8fe63743..HEAD). Behavioral pins for the fixes
    /// that have a seam, and source pins for the ones that do not.
    /// </summary>
    public class AuditDelta20260903Tests
    {
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Src(string rel)
            => File.ReadAllText(Path.Combine(RepoRoot(), rel));

        // ── The multi-SZ reader stops at the terminator ──────────────────

        /// <summary>HidHide's Config.c HidHideCollectionToMultiString sums
        /// "neededSizeInCharacters += string.Length" over a UNICODE_STRING,
        /// whose Length is in BYTES, so the driver reports (2C + N + 1)
        /// characters while writing (C + N + 1). The completion hands that
        /// inflated count back, and the bytes past what the driver wrote are
        /// METHOD_BUFFERED system-buffer pool it never touched. Reading the
        /// whole reported length turned that pool into blacklist entries.
        /// The existing FakeDriver hides this because it copies into a
        /// zero-filled array; a real system buffer carries whatever was
        /// there. This fake fills the tail, which is the case that matters.
        /// </summary>
        [Fact]
        public void MultiSzRead_StopsAtTheTerminator_AndIgnoresTheOverReportedTail()
        {
            var entries = new List<string> { "HID\\VID_054C&PID_0CE6\\ABC" };

            // The driver's own arithmetic, bug included.
            int reportedChars = 0;
            foreach (var s in entries) reportedChars += s.Length * 2 + 1;
            reportedChars += 1;
            int reportedBytes = reportedChars * 2;

            // What it actually writes: each string, its terminator, then the
            // multi-string terminator.
            var written = new StringBuilder();
            foreach (var s in entries) { written.Append(s); written.Append('\0'); }
            written.Append('\0');
            byte[] real = Encoding.Unicode.GetBytes(written.ToString());

            HidHideController.IoSeam = (ioctl, inBuf, outBuf) =>
            {
                if (ioctl != IOCTL_GET_BLACKLIST) return (false, 0);
                if (outBuf == null || outBuf.Length == 0) return (true, reportedBytes);

                // Uninitialized pool first, then the driver's real write over
                // the front of it. The tail is what the old parser consumed.
                for (int i = 0; i < outBuf.Length; i++) outBuf[i] = 0x41;
                Array.Copy(real, outBuf, Math.Min(real.Length, outBuf.Length));
                return (true, reportedBytes);
            };

            try
            {
                var got = HidHideController.GetBlacklist();
                Assert.NotNull(got);
                Assert.Equal(entries, got);
            }
            finally { HidHideController.IoSeam = null; }
        }

        /// <summary>An empty list is a single terminator, two bytes, and must
        /// read as a successful read of nothing rather than as a failure.
        /// Calling it malformed broke hiding outright once already.</summary>
        [Fact]
        public void MultiSzRead_EmptyListIsTwoBytesAndReadsAsEmpty()
        {
            HidHideController.IoSeam = (ioctl, inBuf, outBuf) =>
            {
                if (ioctl != IOCTL_GET_BLACKLIST) return (false, 0);
                if (outBuf == null || outBuf.Length == 0) return (true, 2);
                outBuf[0] = 0; outBuf[1] = 0;
                return (true, 2);
            };

            try
            {
                var got = HidHideController.GetBlacklist();
                Assert.NotNull(got);
                Assert.Empty(got);
            }
            finally { HidHideController.IoSeam = null; }
        }

        // ── One POV sector rule, not two ─────────────────────────────────

        /// <summary>The mapping grid's preview kept its own copy of the sector
        /// rule and it had drifted on three points. PovMatches is the one
        /// implementation now, and the app's PovInDirection calls it.</summary>
        [Fact]
        public void PovMatches_CarriesAny_IsCaseInsensitive_AndIsInclusiveAtTheDiagonals()
        {
            // "Any" is a first-class direction. PhysicalSlotResolver emits
            // "POV 0 Any" for a Steam dpad's edge and click, so every Workshop
            // import depends on it.
            Assert.True(SourceCoercion.PovMatches(0, "Any"));
            Assert.True(SourceCoercion.PovMatches(18000, "Any"));
            Assert.True(SourceCoercion.PovMatches(0, "any"));
            Assert.False(SourceCoercion.PovMatches(-1, "Any"));

            // Case-insensitive, like both engine readers.
            Assert.True(SourceCoercion.PovMatches(0, "up"));
            Assert.True(SourceCoercion.PovMatches(9000, "RIGHT"));

            // Inclusive at the diagonal: the engine reports two directions
            // there, and the grid used to report one.
            Assert.True(SourceCoercion.PovMatches(4500, "Up"));
            Assert.True(SourceCoercion.PovMatches(4500, "Right"));
        }

        /// <summary>The app-side preview delegates rather than repeating the
        /// rule, so the two cannot drift again.</summary>
        [Fact]
        public void PovInDirection_DelegatesToTheEngineRule()
        {
            string svc = Src(Path.Combine("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains(
                "=> PadForge.Engine.Common.Mapping.SourceCoercion.PovMatches(centidegrees, dir);",
                svc);
            Assert.DoesNotContain("\"Up\"    => cd >= 31500 || cd <  4500,", svc);
        }

        // ── A parked row is adopted, never duplicated ────────────────────

        /// <summary>ApplyProfile parks a device the incoming profile does not
        /// assign at MapTo -1 and keeps its PadSetting. The load stopped
        /// deleting those rows (a5de3709), so the assign path had to stop
        /// creating a second row beside them: two persisted rows for one
        /// device, and DeviceService overwriting the slot with a default
        /// automap because the fresh row carried no PadSetting.</summary>
        [Fact]
        public void AssignDeviceToSlot_AdoptsAParkedRow()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var guid = Guid.NewGuid();
                var parked = new UserSetting { InstanceGuid = guid, MapTo = -1 };
                SettingsManager.UserSettings.Items.Add(parked);

                var got = SettingsManager.AssignDeviceToSlot(guid, 2);

                Assert.Same(parked, got);
                Assert.Equal(2, got.MapTo);
                Assert.Single(SettingsManager.UserSettings.Items);
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        /// <summary>The toggle path is the same shape and does not delegate to
        /// AssignDeviceToSlot, so it carries the same rule.</summary>
        [Fact]
        public void ToggleDeviceSlotAssignment_AdoptsAParkedRow()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var guid = Guid.NewGuid();
                var parked = new UserSetting { InstanceGuid = guid, MapTo = -1 };
                SettingsManager.UserSettings.Items.Add(parked);

                var (assigned, us) = SettingsManager.ToggleDeviceSlotAssignment(guid, 1);

                Assert.True(assigned);
                Assert.Same(parked, us);
                Assert.Equal(1, us.MapTo);
                Assert.Single(SettingsManager.UserSettings.Items);
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        /// <summary>A device already on the slot still returns that row, and a
        /// device with no parked row still gets a new one. The positive
        /// control for the two tests above.</summary>
        [Fact]
        public void AssignDeviceToSlot_StillCreatesARowWhenNothingIsParked()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var guid = Guid.NewGuid();

                var first = SettingsManager.AssignDeviceToSlot(guid, 0);
                Assert.NotNull(first);
                Assert.Equal(0, first.MapTo);

                // A second slot for the same device is a second row, which is
                // what multi-slot assignment means.
                var second = SettingsManager.AssignDeviceToSlot(guid, 3);
                Assert.NotSame(first, second);
                Assert.Equal(3, second.MapTo);
                Assert.Equal(2, SettingsManager.UserSettings.Items.Count);
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        // ── Source pins for the fixes with no seam ───────────────────────

        /// <summary>CycleIndex is engagement state. ShiftRuntime.Clear says so
        /// and clears it; the Switch Layer to Base partial clear did not, so a
        /// Cycle activator's cursor stayed on the layer just released.</summary>
        [Fact]
        public void SwitchLayerToBase_ClearsTheCycleCursor()
        {
            string src = Src(Path.Combine(
                "PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));
            Assert.Contains("System.Array.Clear(rt.CycleIndex, 0, rt.CycleIndex.Length);", src);
        }

        /// <summary>The combined-DPad read carries the slot and the device, so
        /// the hat follows the grip the way every individual DPad row does.
        /// Without them EvaluateForButtonTarget took a negative slot and
        /// GripPov was the identity.</summary>
        [Fact]
        public void CombinedDpad_ReadsThroughTheSlotAndDeviceSoTheGripApplies()
        {
            string src = Src(Path.Combine(
                "PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));
            Assert.Contains("EvalPovBool(state, src, \"Up\",    slotIndex, thisDeviceGuid)", src);
            Assert.Contains(
                "SourceCoercion.EvaluateForButtonTarget(state, synth, 50, slotIndex, evaluatedDeviceGuid)",
                src);
        }

        /// <summary>MenuTriggerTick has no other clear site, so the guard
        /// retires it once the release edge has been delivered. Left set, a
        /// macro fired once from a menu cell evaluated on every poll for the
        /// rest of the session. Both evaluator twins carry the rule.</summary>
        [Fact]
        public void MenuCellStamp_RetiresAfterTheReleaseEdge_InBothTwins()
        {
            string src = Src(Path.Combine(
                "PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs"));
            int retire = CountOf(src, "if (macro.MenuTriggerTick >= 0 && !macro.WasTriggerActive)");
            int reset = CountOf(src, "macro.LastEvaluatedUtc = DateTime.MinValue;");
            Assert.Equal(2, retire);
            Assert.True(reset >= 2, $"expected the skip to reset the edge fields in both twins, saw {reset}");
        }

        /// <summary>A device-pinned trigger entry outranks a device-free one on
        /// the same axis. Returning on the first match let "(Any device)" win
        /// on list order, and the two can carry opposite Invert.</summary>
        [Fact]
        public void PressureDirection_PrefersTheDevicePinnedEntry()
        {
            string src = Src(Path.Combine(
                "PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs"));
            Assert.Contains("if (e.DeviceGuid == action.SourceDeviceGuid) { pinned = e; break; }", src);
            Assert.Contains("var pick = pinned ?? anyDevice;", src);
        }

        /// <summary>Adoption belongs to ApplyDeviceHiding, which has eleven
        /// call sites, not to Start, which was one of them.</summary>
        [Fact]
        public void CloakAdoption_RunsFromTheApplyPathNotOnlyFromStart()
        {
            string svc = Src(Path.Combine("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("EnsureHidHideCloaksAdopted();", svc);
            Assert.Contains("private void EnsureHidHideCloaksAdopted()", svc);
        }

        /// <summary>The whitelist lane honors its write result and only then
        /// drops the path from the managed set, the way the blacklist lane
        /// was rewritten to.</summary>
        [Fact]
        public void WhitelistSync_HonorsTheWriteResultBeforeRetiringAPath()
        {
            string svc = Src(Path.Combine("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("if (changed && !HidHideController.SetWhitelist(currentWhitelist))", svc);
        }

        // ── The system container is a device fact, not "cannot tell" ─────

        /// <summary>GUID_CONTAINER_ID_SYSTEM means "built in and
        /// non-removable", and every built-in device on the machine shares it,
        /// so it cannot tell two pads apart. Treating it as unknown made
        /// a882ed19's chain rule and ffda86b5's sweep gate inert on exactly the
        /// handheld pads they were written for. The USB composite's VID and PID
        /// token bounds the walk instead. These are REAL instance ids read off
        /// this bench, where 21 of 33 HIDClass devices carry the system
        /// container.</summary>
        [Theory]
        [InlineData(@"HID\VID_048D&PID_C193&MI_00&COL01\9&11407328&0&0000", "VID_048D&PID_C193")]
        [InlineData(@"HID\VID_048D&PID_C193&MI_01\9&3517B0EA&0&0000", "VID_048D&PID_C193")]
        [InlineData(@"HID\VID_048D&PID_C197&COL05\8&11B458CB&0&0004", "VID_048D&PID_C197")]
        [InlineData(@"USB\VID_17EF&PID_61EB&MI_00\7&2f8b1c3d&0&0000", "VID_17EF&PID_61EB")]
        [InlineData(@"USB\VID_17EF&PID_61EB\0123456789", "VID_17EF&PID_61EB")]
        [InlineData(@"HID\VID_054C&PID_0CE6&MI_03\8&1e2f3a4b&0&0000", "VID_054C&PID_0CE6")]
        public void VidPidToken_ReadsTheCompositeScopeFromRealInstanceIds(string instanceId, string expected)
            => Assert.Equal(expected, HidHideController.VidPidToken(instanceId));

        /// <summary>Nodes with nothing to scope by return null, and the callers
        /// still read that as "cannot tell" and fall back to the old rule. The
        /// ACPI and hub nodes are the ones a walk must stop at.</summary>
        [Theory]
        [InlineData(@"ACPI\GXTP5100\3&62D7E73&0")]
        [InlineData(@"ACPI\IDEA5003\1")]
        [InlineData(@"USB\ROOT_HUB30\4&1a2b3c4d&0")]
        [InlineData(@"HID\IDEA5003&COL01\4&10D72E27&0&0000")]
        [InlineData("")]
        [InlineData(null)]
        public void VidPidToken_IsNullWhenThereIsNothingToScopeBy(string instanceId)
            => Assert.Null(HidHideController.VidPidToken(instanceId));

        /// <summary>The whole point of the token: interfaces of ONE composite
        /// share it and two different pads do not, which is the discrimination
        /// the system container cannot provide. The hub's differs from the
        /// composite's, so the parent walk stops there and can never climb to
        /// the ACPI root, which is the reason the old code gave up.</summary>
        [Fact]
        public void VidPidToken_UnitesOnePadsInterfacesAndStopsAtTheHub()
        {
            string padA0 = HidHideController.VidPidToken(@"USB\VID_17EF&PID_61EB&MI_00\7&aaa&0&0000");
            string padA3 = HidHideController.VidPidToken(@"HID\VID_17EF&PID_61EB&MI_03&COL02\7&bbb&0&0001");
            string padB0 = HidHideController.VidPidToken(@"USB\VID_17EF&PID_61EC&MI_00\7&ccc&0&0000");

            Assert.Equal(padA0, padA3);
            Assert.NotEqual(padA0, padB0);

            Assert.NotNull(HidHideController.VidPidToken(@"USB\VID_17EF&PID_61EB\0123456789"));
            Assert.Null(HidHideController.VidPidToken(@"USB\ROOT_HUB30\4&1a2b3c4d&0"));
        }

        /// <summary>The lean neutral is keyed by (device, slot) because the
        /// grip it is tagged with is per-(device, slot). Keyed by device
        /// alone, a device on two slots held two ways dropped and re-latched
        /// on every read and the lean families read a constant zero.</summary>
        [Fact]
        public void LeanNeutral_IsKeyedByDeviceAndSlot()
        {
            var key = typeof(SourceCoercion).GetMethod(
                "LeanNeutralKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(key);

            string guid = Guid.NewGuid().ToString("d");
            string slot0 = (string)key.Invoke(null, new object[] { guid, 0 });
            string slot3 = (string)key.Invoke(null, new object[] { guid, 3 });

            // The whole point: one device on two slots gets two latches,
            // because the grip each is tagged with is per-(device, slot).
            Assert.NotEqual(slot0, slot3);

            // Same-window positive control: the same slot is the same key, so
            // the difference above is the slot and not a fresh value each call.
            Assert.Equal(slot0, (string)key.Invoke(null, new object[] { guid, 0 }));

            // And two devices on one slot stay separate.
            string other = Guid.NewGuid().ToString("d");
            Assert.NotEqual(slot0, (string)key.Invoke(null, new object[] { other, 0 }));

            // The device half is still a prefix the per-device reset can match.
            var prefix = typeof(SourceCoercion).GetMethod(
                "LeanNeutralDevicePrefix",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(prefix);
            string p = (string)prefix.Invoke(null, new object[] { guid });
            Assert.StartsWith(p, slot0);
            Assert.StartsWith(p, slot3);
        }

        /// <summary>The source pin beside the behavioral one: both read sites
        /// pass the slot, so the key's slot component is actually reached.
        /// The behavioral test above proves the key separates slots; this one
        /// proves the callers hand it a slot to separate by.</summary>
        [Fact]
        public void LeanNeutral_BothReadSitesPassTheSlot()
        {
            string src = Src(Path.Combine(
                "PadForge.Engine", "Common", "Mapping", "SourceCoercion.cs"));
            Assert.Equal(3, CountOf(src, "LeanNeutralKey(deviceGuid, slotIndex)"));
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
    }
}
