using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round 36 regression audit: the audit rounds themselves shipped defects.
    /// Every test here reddens when its fix is reverted, which is checked by
    /// mutation rather than assumed. Where a contract is structural (call
    /// ordering, reset parity) the test reads source, and every source test
    /// carries a POSITIVE CONTROL so a window that stops matching fails loudly
    /// instead of asserting nothing. Round 35 shipped two source tests whose
    /// windows had slid off their target and were silently green.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditRound36FixTests
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

        private static string Src(string relPath) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relPath));

        // ── PTP frame assembly: the lift tally is part of the frame counter ──

        /// <summary>Round 35 made frame completion
        /// <c>FrameSeen + FrameLifted &gt;= FrameExpected</c> but reset
        /// FrameLifted at only two of the three sites that reset FrameSeen.
        /// The commit path was the one it missed, so a lift from the frame just
        /// committed counted toward the NEXT frame and completed it early: on a
        /// pad carrying two contacts per report, a three-finger frame committed
        /// two fingers and the third arrived as a frame of its own.
        ///
        /// <para>The commit message claimed the opposite ("The tally resets
        /// everywhere FrameSeen resets"), which is the tell this pins.</para>
        /// </summary>
        [Fact]
        public void PtpLiftTally_ResetsWhereverTheFrameCounterResets()
        {
            string src = Src("PadForge.Engine/Common/PrecisionTouchpadReader.cs");

            int seenResets = Regex.Matches(src, @"\bFrameSeen\s*=\s*0\s*;").Count;
            int liftResets = Regex.Matches(src, @"\bFrameLifted\s*=\s*0\s*;").Count;

            // Positive control: if the fields are renamed away, this test must
            // fail rather than compare 0 to 0 and pass.
            Assert.True(seenResets >= 3,
                $"expected at least 3 FrameSeen resets, found {seenResets}. "
                + "The frame-assembly block moved or was renamed; update this test with it.");

            Assert.Equal(seenResets, liftResets);
        }

        // ── Slot delete must not mutate a list the poll thread walks ──

        /// <summary>ShiftActivators is enumerated by the ~1 kHz poll thread
        /// without our lock (ResolveActiveLayerMask, ApplyMappingSetToGamepad),
        /// so the codebase's rule is swap the reference, never mutate in place.
        /// PadPage and ApplyShiftLayerSnapshot both document and follow it.
        /// Round 35's slot-delete reset used Clear(), and DeleteSlot runs
        /// ResetAllSettings BEFORE unassigning the slot's devices, so the poll
        /// thread is still walking that very list. A tick inside the foreach
        /// throws "collection was modified" and costs the device its whole
        /// mapping pass for that frame.</summary>
        [Fact]
        public void SlotDeleteReset_SwapsShiftActivators_NeverClearsInPlace()
        {
            string src = Src("PadForge.App/ViewModels/PadViewModel.cs");

            // Positive control: the reset block must still exist.
            Assert.Contains("delSet.BaseLayerName", src);

            Assert.DoesNotMatch(
                new Regex(@"delSet\.ShiftActivators\s*\?\?\s*\.\s*Clear\s*\(|delSet\.ShiftActivators\s*\?\.\s*Clear\s*\(|delSet\.ShiftActivators\.Clear\s*\("),
                src);
            Assert.Matches(new Regex(@"delSet\.ShiftActivators\s*=\s*new\b"), src);
        }

        // ── Test pulses: motors are per-lane, the target filter is shared ──

        /// <summary>The two test lanes write DISJOINT motor fields (main rumble
        /// owns Left/RightMotorSpeed, impulse owns the trigger twins) but shared
        /// one slot-wide generation counter. "Test Left Motor" then "Test Right
        /// Motor" within 500 ms therefore left the LEFT motor at 65535 forever:
        /// the first timer bailed at the generation check before its own clear,
        /// and the second never touched a field it had not set.
        ///
        /// <para>Motors must be stamped per field. The target filter and the
        /// directional block stay on the slot-wide counter, which is correct for
        /// state both lanes genuinely share.</para></summary>
        [Fact]
        public void TestPulseMotorClears_AreStampedPerFieldNotPerSlot()
        {
            string src = Src("PadForge.App/Services/InputService.cs");

            // Positive control: both lanes must still exist.
            Assert.Contains("SendTestImpulseTrigger", src);
            Assert.Contains("_testPulseGeneration", src);

            // All four motor fields carry their own generation slot.
            foreach (var field in new[]
            {
                "PulseFieldMainLeft", "PulseFieldMainRight",
                "PulseFieldTriggerLeft", "PulseFieldTriggerRight",
            })
            {
                Assert.True(
                    Regex.Matches(src, @"_testPulseMotorGeneration\[padIndex,\s*" + field + @"\]").Count >= 2,
                    $"{field} must be both stamped at pulse time and compared at clear time.");
            }

            // And no motor clear sits behind the slot-wide gate any more. Each
            // of the four zeroing assignments must be guarded by its own
            // per-field generation comparison on the same line-pair.
            foreach (var motor in new[]
            {
                "LeftMotorSpeed", "RightMotorSpeed",
                "LeftTriggerMotorSpeed", "RightTriggerMotorSpeed",
            })
            {
                var clear = Regex.Match(src,
                    @"_testPulseMotorGeneration\[padIndex,\s*PulseField\w+\]\s*==\s*my\w+Gen\)\s*\r?\n\s*vib\."
                    + motor + @"\s*=\s*0;");
                Assert.True(clear.Success,
                    $"vib.{motor} = 0 must be gated on its own per-field generation, "
                    + "not on the slot-wide _testPulseGeneration.");
            }
        }

        // ── The rail reads a cached MIDI probe, so the probe must run first ──

        /// <summary>BuildNavigationItems reads the cached
        /// Settings.IsMidiServicesInstalled rather than probing the registry per
        /// card. That property starts false, and the constructor called
        /// RefreshMidiServicesStatus 200 lines AFTER the rail build, so a machine
        /// with Windows MIDI Services installed painted its first rail without
        /// the MIDI segment. The 5 s driver timer then updates the property but
        /// deliberately skips a rebuild on its baseline sweep, so the segment
        /// stayed missing until an unrelated rebuild.
        ///
        /// <para>Two separate comments asserted the correct ordering while the
        /// code did the opposite.</para></summary>
        [Fact]
        public void MidiServicesProbe_RunsBeforeTheRailIsBuilt()
        {
            string src = Src("PadForge.App/MainWindow.xaml.cs");

            int build = src.IndexOf("BuildNavigationItems();", StringComparison.Ordinal);

            // Positive control: the rail build must exist.
            Assert.True(build > 0, "BuildNavigationItems() call not found.");
            Assert.Contains("RefreshMidiServicesStatus();", src);

            // A plain "first probe precedes first build" comparison is NOT
            // enough, and this test shipped that way for one mutation round:
            // an unrelated RefreshMidiServicesStatus() call sits far earlier in
            // the file, so deleting the constructor's pre-rail call left the
            // comparison satisfied and the test green. Require the probe
            // IMMEDIATELY before the rail build instead.
            int windowStart = Math.Max(0, build - 1200);
            string justBefore = src.Substring(windowStart, build - windowStart);

            Assert.True(justBefore.Contains("RefreshMidiServicesStatus();"),
                "RefreshMidiServicesStatus() must be called immediately before "
                + "BuildNavigationItems(): the rail reads the cached probe result, and "
                + "that cache starts false.");
        }

        // ── The descriptor clear must not blank what it is about to rewrite ──

        /// <summary>ClearMappingDescriptors blanks the standard descriptor
        /// properties, and the ~1 kHz poll thread reads them directly
        /// (Step3.UpdateOutputStates reads ButtonA and the same triple for every
        /// target). Round 35 copied the clear+rewrite onto
        /// UpdatePadSettingsFromViewModels, whose only caller is SaveToFile,
        /// which the 250 ms autosave debounce drives after ANY MarkDirty. The
        /// source it was copied from documents exactly why that is unsafe and
        /// only clears on an explicit sync.
        ///
        /// <para>Seeding closes the window: every property is assigned its final
        /// value once, so none is ever transiently empty while a tick could read
        /// it.</para></summary>
        [Fact]
        public void SeededClear_AssignsFinalValues_NeverBlanksThemFirst()
        {
            var ps = new PadSetting
            {
                ButtonA = "Button 1",
                ButtonB = "Button 2",
                LeftThumbAxisX = "Axis 0",
            };

            var seed = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ButtonA"] = "Button 9",
                ["LeftThumbAxisX"] = "Axis 3",
            };

            ps.ClearMappingDescriptors(seed);

            // Seeded properties land on their FINAL value, never "".
            Assert.Equal("Button 9", ps.ButtonA);
            Assert.Equal("Axis 3", ps.LeftThumbAxisX);

            // Unseeded ones still clear, which is what the clear is for.
            Assert.Equal("", ps.ButtonB);
        }

        /// <summary>Positive control for the test above: with no seed the clear
        /// must still blank everything, or the seeded assertions prove nothing
        /// about seeding specifically.</summary>
        [Fact]
        public void UnseededClear_StillBlanksEveryDescriptor()
        {
            var ps = new PadSetting
            {
                ButtonA = "Button 1",
                ButtonB = "Button 2",
                LeftThumbAxisX = "Axis 0",
            };

            ps.ClearMappingDescriptors();

            Assert.Equal("", ps.ButtonA);
            Assert.Equal("", ps.ButtonB);
            Assert.Equal("", ps.LeftThumbAxisX);
        }

        /// <summary>The autosave caller must pass a seed. A future edit that
        /// drops it silently reopens the poll-thread window, and nothing else
        /// would catch that.</summary>
        [Fact]
        public void AutosaveDescriptorWrite_PassesASeedToTheClear()
        {
            string src = Src("PadForge.App/Services/SettingsService.cs");

            // Positive control: the per-device clear loop must still be there.
            Assert.Contains("ClearMappingDescriptors(", src);

            Assert.DoesNotMatch(
                new Regex(@"devPs\.ClearMappingDescriptors\(\s*\)"),
                src);
            Assert.Matches(
                new Regex(@"devPs\.ClearMappingDescriptors\(\s*\r?\n?\s*seeded\."),
                src);
        }
    }
}
