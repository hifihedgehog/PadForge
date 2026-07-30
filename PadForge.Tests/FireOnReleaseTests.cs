using System;
using System.IO;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Fire on Release: the edge-driven activator modes fire when the input
    /// is let go instead of when it goes down, matching Steam's
    /// release-hosted activators.
    ///
    /// <para>The whole feature hangs on one function:
    /// <c>InputManager.ComputeActivatorFire</c> is the single edge read every
    /// press-driven mode consumes (Toggle / Latch / Cycle / Sticky), so the
    /// release branch lives there and these tests drive it directly, the same
    /// way ShiftLongPressTests pins the press branch.</para>
    /// </summary>
    public class FireOnReleaseTests
    {
        // ── the release edge ────────────────────────────────────────────────

        [Fact]
        public void FiresOnTheFallingEdgeOnly()
        {
            bool latch = false;

            // Press frame: nothing. The whole point is that the down-stroke
            // no longer triggers.
            Assert.False(InputManager.ComputeActivatorFire(true, false, 0, 0, ref latch, fireOnRelease: true));
            // Held frames: nothing.
            Assert.False(InputManager.ComputeActivatorFire(true, true, 200, 0, ref latch, fireOnRelease: true));
            // The release frame: fire.
            Assert.True(InputManager.ComputeActivatorFire(false, true, 200, 0, ref latch, fireOnRelease: true));
            // Idle frames after: nothing. Without the wasDown guard this
            // would fire every frame the button sits untouched.
            Assert.False(InputManager.ComputeActivatorFire(false, false, 0, 0, ref latch, fireOnRelease: true));
        }

        [Fact]
        public void DelayGatesThePressThatArmsTheRelease()
        {
            // "Long-press, then let go": a release only counts if the hold it
            // ends lasted at least DelayMs. heldMs at the release frame is
            // the ended hold's length (the caller carries it across).
            bool latch = false;

            // Short press released: no fire.
            Assert.False(InputManager.ComputeActivatorFire(false, true, 300, 500, ref latch, fireOnRelease: true));
            // Long press released: fire.
            Assert.True(InputManager.ComputeActivatorFire(false, true, 600, 500, ref latch, fireOnRelease: true));
            // Exactly at the threshold: fire (>= like the press branch).
            Assert.True(InputManager.ComputeActivatorFire(false, true, 500, 500, ref latch, fireOnRelease: true));
        }

        [Fact]
        public void ThePressBranchIsUntouchedByTheFlagDefault()
        {
            // The default parameter keeps every existing caller and test on
            // the press branch. One sample from ShiftLongPressTests' own
            // contract, re-asserted through the defaulted signature.
            bool latch = false;
            Assert.True(InputManager.ComputeActivatorFire(true, false, 0, 0, ref latch));
            Assert.False(InputManager.ComputeActivatorFire(false, true, 0, 0, ref latch));
        }

        [Fact]
        public void HoldingWithTheFlagNeverFires()
        {
            // A long uninterrupted hold in release mode: no frame fires until
            // the lift, however long the hold. (The press branch fires at
            // DelayMs; the release branch must not inherit that.)
            bool latch = false;
            for (long ms = 0; ms <= 5000; ms += 250)
            {
                Assert.False(InputManager.ComputeActivatorFire(
                    true, ms > 0, ms, 500, ref latch, fireOnRelease: true),
                    $"fired while still held at {ms} ms");
            }
            Assert.True(InputManager.ComputeActivatorFire(false, true, 5000, 500, ref latch, fireOnRelease: true));
        }

        // ── the Cycle mode's own edge (round 40) ────────────────────────────
        //
        // Cycle deliberately ignores DelayMs, so it never rode
        // ComputeActivatorFire. The first Fire on Release cut therefore
        // missed it entirely: release-hosted remove_layer imports were
        // stamped onto Cycle activators, reported exact, and still stepped
        // on the press. The mode has its own edge read now, and these pin it.

        [Theory]
        // press mode: rising edge only
        [InlineData(false, true, false, true)]
        [InlineData(false, true, true, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, false)]
        // release mode: falling edge only
        [InlineData(true, true, false, false)]
        [InlineData(true, true, true, false)]
        [InlineData(true, false, true, true)]
        [InlineData(true, false, false, false)]
        public void CycleStepEdge_PicksTheRightEdge(bool fireOnRelease, bool down, bool wasDown, bool expected)
        {
            Assert.Equal(expected, InputManager.CycleStepEdge(down, wasDown, fireOnRelease));
        }

        [Fact]
        public void BothCycleLegsRideTheSharedEdge()
        {
            // Next AND Previous: a release-mode cycle whose Previous button
            // still stepped on the press would walk the ring in opposite
            // directions on opposite edges of the same interaction.
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));
            Assert.Contains("CycleStepEdge(inputDown, rt.WasDown[actIdx], act.FireOnRelease)", src);
            Assert.Contains("CycleStepEdge(prevDown, rt.CyclePrevWasDown[actIdx], act.FireOnRelease)", src);
        }

        // ── persistence ─────────────────────────────────────────────────────

        [Fact]
        public void TheFlagRoundTripsThroughXml()
        {
            var act = new ShiftActivator
            {
                Descriptor = "Button 3",
                Mode = "Toggle",
                LayerMask = "Shift",
                FireOnRelease = true,
            };

            var ser = new XmlSerializer(typeof(ShiftActivator));
            using var ms = new MemoryStream();
            ser.Serialize(ms, act);
            ms.Position = 0;
            var back = (ShiftActivator)ser.Deserialize(ms);

            Assert.True(back.FireOnRelease);
            Assert.Equal("Toggle", back.Mode);
        }

        // ── wiring that cannot be unit-driven, pinned by inspection ────────

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void TheRuntimeCallSitePassesTheFlag()
        {
            // UpdateActivatorState needs a live slot to drive, so the wiring
            // is pinned textually: the one call site must pass the
            // activator's flag, and must pass the release-frame hold length
            // so DelayMs can gate the arming press.
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));
            Assert.Contains("ref rt.LongPressFired[actIdx], act.FireOnRelease);", src);
            // The full arming expression, not just the variable name: the
            // release-frame hold must be measured from EngageStartTicks with
            // the zero-tick guard, or a delay-gated release mis-times.
            Assert.Contains("(!inputDown && rt.WasDown[actIdx] && rt.EngageStartTicks[actIdx] > 0)", src);
        }

        [Fact]
        public void TheDialogRoundTripsTheFlag()
        {
            // Load leg, save leg, mode gating, and the copy back onto the
            // existing activator. WPF dialogs cannot run headless, so each
            // leg is pinned where it lives; the behaviour half is covered by
            // the engine and translator tests.
            string dlg = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Views", "ShiftActivatorDialog.xaml.cs"));
            Assert.Contains("FireOnReleaseBox.IsChecked = existing.FireOnRelease;", dlg);
            Assert.Contains("&& FireOnReleaseBox.IsChecked == true,", dlg);
            Assert.Contains("FireOnReleaseBox.Visibility = edgeMode", dlg);

            string page = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Views", "PadPage.xaml.cs"));
            Assert.Contains("existing.FireOnRelease = dlg.Result.FireOnRelease;", page);
        }
    }
}
