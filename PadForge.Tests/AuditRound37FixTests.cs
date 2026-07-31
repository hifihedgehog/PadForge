using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round 37: defects the earlier audit rounds shipped, plus the repairs to
    /// tests those rounds left unable to fail. Every test here is
    /// mutation-checked, and every source-scanning test carries a POSITIVE
    /// CONTROL so a window that stops matching fails loudly instead of
    /// asserting nothing.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditRound37FixTests
    {
        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "PadForge.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("PadForge.sln not found above " + AppContext.BaseDirectory);
        }

        private static string Src(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

        // ── A pinned activator must not suppress another pad's button ──

        /// <summary>The postpone suppression set carries an activator's own
        /// key plus a twin so it also reaches an "(Any device)" ROW. Round 35
        /// spelled that twin as the empty guid, which is ALSO the authored
        /// value for "this activator pins no device", and the lookup's
        /// fallback for that case matches a row on any device. Composed, a
        /// shift activator pinned to pad A suppressed pad B's identically
        /// spelled button on the same slot. Descriptors are device-agnostic
        /// strings ("Button 4"), so the collision is the normal case on a
        /// multi-device slot.</summary>
        [Fact]
        public void PinnedActivator_DoesNotSuppressAnotherDevicesRow()
        {
            const string padA = "11111111-1111-1111-1111-111111111111";
            const string padB = "22222222-2222-2222-2222-222222222222";

            var set = new HashSet<(string Guid, string Desc)>();
            InputManager.AddPostponeKey(set, padA, "Button 4");

            // Positive control: the activator's OWN device is suppressed, so a
            // "not suppressed" result below cannot be vacuously true.
            Assert.Contains((padA, "Button 4"), set);
            // And the any-row twin exists, so direction 1 still works.
            Assert.Contains((InputManager.AnyRowTwinGuid, "Button 4"), set);

            // The regression: pad B must be reachable, not swallowed.
            Assert.DoesNotContain((padB, "Button 4"), set);
            Assert.DoesNotContain(("", "Button 4"), set);
        }

        /// <summary>Direction 2 stays intact: an activator that pins NO device
        /// authors the empty key, which the lookup deliberately matches for a
        /// row pinned to any device.</summary>
        [Fact]
        public void AnyDeviceActivator_StillAuthorsTheEmptyKey()
        {
            var set = new HashSet<(string Guid, string Desc)>();
            InputManager.AddPostponeKey(set, "", "Button 4");
            Assert.Contains(("", "Button 4"), set);
            Assert.Single(set);
        }

        // ── Serialless pads must not cross-bind during a disconnect debounce ──

        /// <summary>With an empty serial the rebind scan's row test degenerates
        /// to "" == "", so admitting it on "exact != null" let two same-model
        /// serialless pads cross-bind. Deleting the disjunct outright is the
        /// WRONG fix: it also breaks
        /// FlappedTwin_InsideTheDebounce_RebindsToItsOwnRow, the round-seven
        /// contract that one unit re-identifying inside its debounce must find
        /// its own row, because both cases arrive with an empty serial.
        ///
        /// <para>liveTwinCollision separates them. Re-identifying, the exact
        /// row is held by the live sibling, so the scan is the only way home.
        /// Cross-binding, the exact row is the arriving pad's OWN row with
        /// nobody on it, so the scan can only do harm.</para></summary>
        [Fact]
        public void FlappedUnitRebind_IsGatedOnALiveTwinCollision()
        {
            string src = Src("PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs");

            // Positive control: the guard must still exist to be checked.
            Assert.Contains("livePresentSdlIds != null", src);

            // The bare "exact != null" escape is gone.
            Assert.DoesNotContain("&& (exact != null || !string.IsNullOrEmpty(incomingSerial))", src);

            // Replaced by the collision test, which must be computed BEFORE the
            // scan to be usable by it.
            Assert.Contains("(liveTwinCollision || !string.IsNullOrEmpty(incomingSerial))", src);
            Assert.True(
                src.IndexOf("bool liveTwinCollision", StringComparison.Ordinal)
                    < src.IndexOf("(liveTwinCollision || !string.IsNullOrEmpty", StringComparison.Ordinal),
                "liveTwinCollision must be declared above the scan that gates on it.");
        }

        // ── A vanished mouse must not pin the merged handle's buttons ──

        /// <summary>Raw Input synthesizes no button-up when a mouse disappears
        /// mid-click, and nothing shrinks the per-device map at runtime. Round
        /// 35 changed the merged handle from a copy of a last-writer-wins
        /// bitmask into an OR over every entry, which removed the accidental
        /// self-heal (a click on any surviving mouse used to clear the bit) and
        /// left the mapped output stuck ON for the process lifetime.</summary>
        [Fact]
        public void MouseEnumeration_ReleasesButtonsHeldByVanishedDevices()
        {
            string src = Src("PadForge.Engine/Common/RawInputListener.cs");

            // Positive control: both halves must be present, or this asserts
            // nothing about the pairing.
            Assert.Contains("EnumerateMice()", src);
            Assert.Contains("_mouseStatesValues", src);

            var body = Regex.Match(src,
                @"public static DeviceInfo\[\] EnumerateMice\(\)\s*\r?\n\s*\{(.*?)\r?\n        \}",
                RegexOptions.Singleline);
            Assert.True(body.Success, "EnumerateMice moved; update this test with it.");

            string b = body.Groups[1].Value;
            Assert.Contains("_mouseStates", b);
            Assert.Contains("Array.Clear", b);
            // IntPtr.Zero is the synthetic injected-input key and never
            // enumerates, so clearing it would break PadForge's own injection.
            Assert.Contains("IntPtr.Zero", b);

            // And the sweep must NOT run on an empty enumeration. This was a
            // regression the fix itself introduced: EnumerateDevicesByType
            // returns an empty array on a failed GetRawInputDeviceList as well
            // as on a genuine zero, and those are indistinguishable, so an
            // unguarded sweep treated every live mouse as absent and released
            // a button the user was holding on any transient API hiccup.
            Assert.Contains("devices.Length > 0", b);
        }

        // ── The KBM mouse floor documents what it can actually deliver ──

        /// <summary>The floor's original comment promised "an explicit value in
        /// the Sticks tab still wins in both directions; only 'unset' is
        /// floored". That is unimplementable: the backing property initializes
        /// to the string "0" and XmlSerializer leaves an absent element at its
        /// initializer, so unset and a deliberate zero are byte-identical. The
        /// floor stays (an owner-accepted safety tradeoff); the false promise
        /// does not.</summary>
        [Fact]
        public void KbmMouseDeadZoneFloor_DoesNotPromiseAnEscapeItCannotDeliver()
        {
            string src = Src("PadForge.App/Common/Input/InputManager.Step3.UpdateOutputStates.cs");

            // Positive control: the floor must still be there.
            Assert.Contains("KbmMouseDefaultDeadZonePercent", src);

            // Anchors are single-line phrases: the comment wraps, so a
            // multi-word search spanning a line break silently never matches
            // and the assertion becomes decorative.
            Assert.DoesNotContain("still wins in both directions", src);
            Assert.Contains("wins only when it is ABOVE the floor", src);
            Assert.Contains("There is no value that turns the floor", src);
            // The scope note matters as much as the retraction: gyro shares
            // this lane and must keep the floor.
            Assert.Contains("this lane and MUST keep the floor", src);
        }

        // ── Engine start clears every RUNTIME latch, and only those ──

        /// <summary>The two tests that named this contract hand-copied the
        /// field list into their own bodies and never called Start(), so the
        /// suite stayed green with Start's reset loop deleted. The real
        /// discriminator is the ACCESS MODIFIER: runtime latches are internal,
        /// while PulseWhileLatched is a public serialized user setting that
        /// must NOT be reset. This pins the parity in both directions, so a
        /// sixth runtime latch added without a matching clear fails on
        /// arrival.</summary>
        [Fact]
        public void EngineStart_ClearsEveryRuntimeLatchOnMacroActions()
        {
            string vm = Src("PadForge.App/ViewModels/MacroItem.cs");
            string svc = Src("PadForge.App/Services/InputService.cs");

            var declared = Regex.Matches(vm, @"internal bool ([A-Za-z]+Latched)\s*\{\s*get;\s*set;\s*\}")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            // Positive control: if the declaration shape changes this must fail
            // loudly rather than compare an empty set to an empty set.
            Assert.True(declared.Count >= 5,
                $"expected at least 5 internal runtime latches, found {declared.Count}.");

            var cleared = Regex.Matches(svc, @"act\.([A-Za-z]+Latched) = false;")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            Assert.Equal(declared.OrderBy(s => s), cleared.OrderBy(s => s));

            // The serialized user setting must never be swept in with them.
            Assert.DoesNotContain("PulseWhileLatched", cleared);
            Assert.Contains("public bool PulseWhileLatched", vm);
        }

        // ── The macro recorder must not read another slot's axis layout ──

        /// <summary>MacroAxisTargetToRawIndex resolved through a static that
        /// EvaluateSlotMacrosExtended stamps from the first non-null macro's
        /// PadIndex and never clears afterwards. That is correct for callers
        /// running inside that evaluation, and wrong for the macro RECORDER,
        /// which reads these axes from the UI thread for an unrelated pad. With
        /// two Extended slots on differing layouts the recorder resolved the
        /// other slot's interleave and captured a different channel than the
        /// engine later evaluates. The UI path now passes its own layout.</summary>
        [Fact]
        public void MacroAxisRecorder_ResolvesItsOwnSlotsLayout()
        {
            string svc = Src("PadForge.App/Services/InputService.cs");
            string ev = Src("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs");

            // Positive control: both halves must exist, or the pairing below
            // asserts nothing.
            Assert.Contains("ReadAxisAsVolumeRaw", svc);
            Assert.Contains("_currentRawLayout", ev);

            // A layout-explicit overload exists for off-poll-thread callers.
            Assert.Contains("MacroAxisTargetToRawIndex(MacroAxisTarget target,", ev);

            // And the recorder uses it, resolving the layout from ITS pad.
            Assert.Contains("_inputManager.SlotCustomLayouts[padIndex]", svc);
            Assert.Contains("ReadAxisAsVolumeRaw(in rawState, axes[i], layoutOpt)", svc);
        }

        // ── A gesture template is judged against its OWN threshold ──

        /// <summary>CloudMatch's prune floor was another template's running
        /// best. A template whose true distance exceeded that floor but sat
        /// inside its own looser ThresholdOverride abandoned the sweep and
        /// returned MaxValue, so the caller dropped a legitimate match. The
        /// floor is now the LOOSER of the two (Max, not Min: Min prunes harder
        /// and loses more), with improvement tracked separately so raising the
        /// floor cannot resurrect the borrowed-score bug that shape replaced.</summary>
        [Fact]
        public void GesturePruneFloor_IsNeverTighterThanTheTemplatesOwnThreshold()
        {
            string src = Src("PadForge.Engine/Touchpad/ShapeRecognizer.cs");

            // Positive control: the prune must still exist.
            Assert.Contains("float minSoFar", src);

            // The template's own gate reaches CloudMatch.
            Assert.Contains("float effThreshold = 0f", src);
            Assert.Contains("CloudMatch(candidate, candidateLut, tpl, bestScore, effThreshold)", src);

            // Looser of the two wins, and improvement is tracked separately.
            Assert.Contains("effThreshold > minSoFar ? effThreshold : minSoFar", src);
            Assert.Contains("return improved ? best : float.MaxValue;", src);
        }

        // ── Bluetooth Sony effect frames must not be dropped wholesale ──

        /// <summary>The Sony trust gate tested the arriving byte count for
        /// EQUALITY against the profile's declared extended-output-report size.
        /// On Bluetooth that is unsatisfiable, so every effect frame was
        /// dropped: no rumble, no lightbar, no adaptive triggers, and no
        /// VibrationStates write for any device on the slot.
        ///
        /// <para>Numbers are from primary sources, not inferred. The BT
        /// DualSense native descriptor declares nine output reports
        /// (0x31..0x39) with a 546-byte maximum payload, so Windows sizes every
        /// host write to OutputReportByteLength = 547 whichever report is being
        /// sent. The driver forwards wrSize-1 = 546 clamped to its 256-byte
        /// slot cap (HM_OUTPUT_SLOT_DATA_CAP), so RawBytes arrives at 257
        /// against a declaredSize of 78.</para>
        ///
        /// <para>The leg's real job is to prove the declared report's bytes are
        /// present so the CRC footer is in range, which AT LEAST satisfies and
        /// EQUALS does not.</para></summary>
        [Theory]
        // BT DualSense: capped 257-byte buffer, 78-byte declared report.
        [InlineData(257, 78, true)]
        // USB DualSense: exact fit, unaffected by the change.
        [InlineData(48, 48, true)]
        // Genuinely SHORT report: the footer is out of range, still rejected.
        [InlineData(40, 78, false)]
        // No declared extended output report: must fail CLOSED, not open.
        [InlineData(257, -1, false)]
        public void SonyMotorTrust_AcceptsACappedButCompleteReport(
            int rawByteCount, int declaredSize, bool expected)
        {
            // validFlag0 with the DS5 motor mask asserted, and a valid CRC, so
            // the LENGTH leg is the only thing under test here.
            bool got = HMaestroVirtualController.SonyMotorsValid(
                rawByteCount, declaredSize, crcValid: true,
                validFlag0: (byte)0x03, motorMask: 0x03);

            Assert.Equal(expected, got);
        }

        /// <summary>Positive control for the theory above: with the length leg
        /// satisfied, the OTHER legs must still be able to reject. Otherwise
        /// "accepts a capped report" would be indistinguishable from "accepts
        /// everything".</summary>
        [Fact]
        public void SonyMotorTrust_StillRejectsBadCrcAndClearedFlag()
        {
            Assert.False(HMaestroVirtualController.SonyMotorsValid(
                257, 78, crcValid: false, validFlag0: (byte)0x03, motorMask: 0x03));

            Assert.False(HMaestroVirtualController.SonyMotorsValid(
                257, 78, crcValid: true, validFlag0: (byte)0x00, motorMask: 0x03));
        }

        // ── Removing a whitelisted app must actually unwhitelist it ──

        /// <summary>Settings &gt; Whitelisted Applications lets the user add an
        /// app that may still see hidden devices, and remove it again. Removal
        /// only strips entries PadForge considers its own, and that record is a
        /// per-process set that starts empty and is never seeded from disk,
        /// while the driver's whitelist persists across restarts.
        ///
        /// <para>Round 34 narrowed the ownership claim to "only what we
        /// inserted this run". After any restart the path is already present,
        /// so nothing is inserted, nothing is claimed, and removal silently
        /// stopped working: the app vanished from PadForge's list and stayed
        /// whitelisted in the driver forever. The claim is unconditional again,
        /// which is what it was before round 34 and what the owner remembers
        /// working.</para></summary>
        [Fact]
        public void WhitelistOwnership_IsClaimedEverySync_NotOnlyOnInsert()
        {
            string src = Src("PadForge.App/Services/InputService.cs");

            // Positive control: both halves of the sync must still exist.
            Assert.Contains("_managedWhitelistDosPaths", src);
            Assert.Contains("desiredDosPaths", src);

            // The claim happens for every desired path, BEFORE and outside the
            // "is it already present" test. Matching the pair verbatim keeps
            // this specific.
            Assert.Contains(
                "foreach (var dosPath in desiredDosPaths)\r\n            {\r\n                _managedWhitelistDosPaths.Add(dosPath);",
                src.Replace("\n", "\n").Replace("\r\n", "\r\n"));

            // And it is NOT nested inside the insert branch any more.
            Assert.DoesNotContain(
                "currentWhitelist.Add(dosPath);\r\n                    _managedWhitelistDosPaths.Add(dosPath);",
                src);
        }

        // ── Every string key has a property, and vice versa ──

        /// <summary>Strings.Instance exposes each resx key as an explicit
        /// property, and XAML binds those properties by name. A binding to a
        /// property that does not exist fails SILENTLY at runtime, so the build
        /// stays green and the label simply renders blank. Get() returns the
        /// key name on a resource miss, so the mirror failure in the other
        /// direction renders the raw key on screen instead.
        ///
        /// <para>Both directions shipped. Four keys added for the Overlays
        /// card had no properties and the labels came up empty, which is how
        /// this test exists. The reverse had been shipping for longer: a label
        /// bound Pad_RawSticks, which no resx carried, so the Extended layout
        /// row displayed the literal text "Pad_RawSticks". Its siblings in the
        /// same row all bound Pad_Extended*, and Pad_ExtendedSticks was sitting
        /// there localized and unused.</para></summary>
        [Fact]
        public void EveryStringKey_HasAProperty_AndEveryPropertyHasAKey()
        {
            string resx = Src("PadForge.App/Resources/Strings/Strings.resx");
            string designer = Src("PadForge.App/Resources/Strings/Strings.Designer.cs");

            var keys = Regex.Matches(resx, @"<data name=""([A-Za-z_0-9]+)""")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var props = Regex.Matches(designer, @"public string ([A-Za-z_0-9]+) => Get\(")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            // Positive control: both sides must be populated, or the set
            // differences below are trivially empty.
            Assert.True(keys.Count > 2000, $"only {keys.Count} resx keys parsed; the format changed.");
            Assert.True(props.Count > 2000, $"only {props.Count} designer properties parsed; the format changed.");

            var missing = keys.Except(props).OrderBy(s => s).ToList();
            Assert.True(missing.Count == 0,
                "resx keys with no Strings property (any binding to these renders BLANK): "
                + string.Join(", ", missing));

            var orphaned = props.Except(keys).OrderBy(s => s).ToList();
            Assert.True(orphaned.Count == 0,
                "Strings properties with no resx key (these render the KEY NAME on screen): "
                + string.Join(", ", orphaned));
        }

        // ── Every overlay toggle crosses every persistence leg ──

        /// <summary>The three overlay toggles are a family, and a setting that
        /// misses one leg of the profile pipeline is the classic
        /// mirror-completeness defect: it works until you switch profiles, then
        /// silently reverts or leaks the outgoing profile's value.
        ///
        /// <para>EnableMenuOverlay is the established member, so its footprint
        /// is the spec. This diffs the newer two against it per file rather
        /// than per line, which is what catches a missed snapshot, apply, save,
        /// load, dirty-marker or XAML binding.</para></summary>
        [Fact]
        public void EveryOverlayToggle_CrossesTheSameLegsAsItsSibling()
        {
            var files = new[]
            {
                "PadForge.App/Services/SettingsService.cs",
                "PadForge.App/Services/InputService.cs",
                "PadForge.App/ViewModels/DashboardViewModel.cs",
                "PadForge.App/MainWindow.xaml.cs",
                "PadForge.App/Views/DashboardPage.xaml",
            };

            int Count(string file, string key) =>
                Regex.Matches(Src(file), Regex.Escape(key)).Count;

            foreach (var file in files)
            {
                int spec = Count(file, "EnableMenuOverlay");

                // Positive control: the spec member must actually appear here,
                // or "the others match it" is satisfied by 0 == 0 == 0.
                Assert.True(spec > 0,
                    $"{file} no longer references EnableMenuOverlay; update this test with the family.");

                Assert.Equal(spec, Count(file, "EnableShiftLayerFlyout"));
                Assert.Equal(spec, Count(file, "EnableProfileOverlay"));
            }
        }

        // ── An empty HidHide list is a successful read, not a failure ──

        /// <summary>Round 35 made the HidHide list reader return null on a
        /// failed read so consumers stop wiping the user's lists, and drew the
        /// malformed line at "under 4 bytes". The driver serializes an EMPTY
        /// list as exactly one UTF-16 null, 2 bytes: Logic.c's
        /// OnControlDeviceIoGetBlacklist completes with
        /// neededSizeInCharacters * sizeof(WCHAR), and Config.c's
        /// HidHideCollectionToMultiString computes 0 + 1 characters for an
        /// empty collection. So every successful read of an empty blacklist
        /// was misread as a failure, every consumer bailed before
        /// SetBlacklist, and nothing was ever hidden. Engine start makes the
        /// empty state routine, because the stale-cloak purge clears the
        /// blacklist. The owner's DualSense stayed visible to games with
        /// "hide" flagged on.</summary>
        [Fact]
        public void HidHideEmptyListReply_IsAnEmptyList_NotAFailedRead()
        {
            string src = Src("PadForge.App/Common/HidHideController.cs");

            // Positive control: the malformed-reply guard must still exist.
            Assert.Contains("bytesReturned % 2 != 0", src);

            // The threshold admits the 2-byte empty reply.
            Assert.Contains("bytesReturned < 2", src);
            Assert.DoesNotContain("bytesReturned < 4", src);

            // And the null-for-failure contract this round rightly added must
            // stay: an unreadable driver still returns null, never empty.
            Assert.Contains("return null;", src);
        }

        // ── Each finger-count checkbox is its own opt-in ──

        /// <summary>The In-Box Gestures card renders every gesture family as a
        /// flat list of peer checkboxes. Nothing on screen says that
        /// "Three-Finger Gestures" also needs "Two-Finger Swipes" and
        /// "Tap / Double Tap / Triple Tap" ticked, but the recognizer required
        /// exactly that, so ticking the three-finger box alone enabled nothing.
        /// Round 35 then made the picker mirror those gates honestly, which
        /// turned "listed but dead" into "missing from the dropdown" and is how
        /// the owner hit it.
        ///
        /// <para>The count toggle is now the whole opt-in in BOTH places. The
        /// family's own gate still suppresses everything when unticked.</para>
        /// </summary>
        [Fact]
        public void MultiFingerGestures_AreNotCrossGatedOnOtherFamilies()
        {
            string rec = Src("PadForge.Engine/Touchpad/GestureRecognizer.cs");
            string pick = Src("PadForge.App/Common/MappingDisplayResolver.cs");

            // Positive control: both files must still contain the families, or
            // the absence checks below pass vacuously.
            Assert.Contains("EnableThreeFingerGestures", rec);
            Assert.Contains("ThreeFingerSwipeUp", pick);
            Assert.Contains("ThreeFingerTap", pick);

            // The recognizer's multi-finger emit arms must not consult the
            // one/two-finger switches. Those names still appear legitimately in
            // the 1- and 2-finger blocks, so scope the check to the multi-finger
            // region that starts at the "fingerCount >= 3" gate.
            int at = rec.IndexOf("if (fingerCount >= 3)", StringComparison.Ordinal);
            Assert.True(at > 0, "the multi-finger block moved; update this test with it.");
            string multi = rec.Substring(at);
            Assert.DoesNotContain("settings.EnableTwoFingerSwipes", multi);
            Assert.DoesNotContain("settings.EnableTaps", multi);

            // The rule is written down at BOTH gate sites, so a future edit
            // reads it before touching them. Deleting the contract is itself a
            // regression: the cross-gating looked perfectly reasonable in code
            // and was only wrong from the user's side of the screen.
            Assert.Contains("GATING CONTRACT", rec);
            Assert.Contains("never gate one", rec);
            Assert.Contains("MIRROR GestureRecognizer's gating contract", pick);

            // And the picker must not hide them behind those same switches.
            Assert.DoesNotContain("max >= 5 && gateFive && gateTaps", pick);
            int three = pick.IndexOf("max >= 3 && gateThree", StringComparison.Ordinal);
            Assert.True(three > 0, "the three-finger picker block moved.");
            int four = pick.IndexOf("max >= 4 && gateFour", StringComparison.Ordinal);
            string threeBlock = pick.Substring(three, four - three);
            Assert.DoesNotContain("gateTwoSwipe", threeBlock);
            Assert.DoesNotContain("gateTaps", threeBlock);
        }

        // ── A laptop trackpad has no pressure sensor ──

        /// <summary>The HID PTP spec carries a tip switch, contact ID and X/Y,
        /// and no pressure usage, so PrecisionTouchpadReader synthesizes
        /// pressure as "1.0 while down, else 0.0". Offering it as an analog axis
        /// shipped an exact duplicate of "Finger N Down" under a different name,
        /// plus nine windowed pressure zones that can only ever read fully-in or
        /// fully-out. Real analog pressure is SDL-backed only.</summary>
        [Fact]
        public void PrecisionTouchpads_AreNotOfferedAPressureAxis()
        {
            string src = Src("PadForge.App/Common/MappingDisplayResolver.cs");

            // Positive control: the pressure descriptors must still exist for
            // the devices that DO have a sensor.
            Assert.Contains("Finger {f} Pressure", src);

            // Gated on the same discriminator the Click block already uses for
            // laptop trackpads.
            Assert.Contains("bool ptpNoPressure = ud.IsTouchpad && ud.Device == null;", src);
            Assert.Contains("if (!ptpNoPressure)", src);
        }

        // ── The unsigned-axis contract, guarded on the WRITER side ──

        /// <summary>CustomInputState.Axis is UNSIGNED 0..65535 with 32768 at
        /// rest. The file that claimed to pin this reimplemented the
        /// conversion in its own test body and named the writer only in a
        /// comment, so switching the writer to signed storage left all twelve
        /// tests green. This reads the real writer.</summary>
        [Fact]
        public void SdlDeviceWrapper_StoresEveryAxisUnsigned()
        {
            string src = Src("PadForge.Engine/Common/SdlDeviceWrapper.cs");

            var writes = Regex.Matches(src, @"\.Axis\[[^\]]+\]\s*=\s*([^;]+);")
                .Select(m => m.Groups[1].Value.Trim()).ToList();

            // Positive control: an empty match set would make the loop below
            // vacuously true, which is exactly how the old file passed.
            Assert.True(writes.Count >= 6,
                $"expected at least 6 Axis writes in SdlDeviceWrapper, found {writes.Count}.");

            foreach (var rhs in writes)
            {
                bool stickShaped = rhs.Contains("short.MinValue");
                bool triggerShaped = rhs.Contains("65535L / 32767");
                // One site assigns a local that was itself converted; accept it
                // only when that local's own definition does the conversion.
                bool viaProvenLocal = Regex.IsMatch(rhs, @"^[A-Za-z_][A-Za-z0-9_]*$")
                    && Regex.IsMatch(src, @"\b" + Regex.Escape(rhs) + @"\s*=[^;]*short\.MinValue");

                Assert.True(stickShaped || triggerShaped || viaProvenLocal,
                    "an Axis write stores a value that is not provably unsigned: " + rhs);
            }
        }
    }
}
