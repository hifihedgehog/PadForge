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
