using System;
using System.IO;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Flip Output checkbox: MappingSource.InvertOutput made
    /// user-authorable.
    ///
    /// <para>On a half-axis read of a centered axis the engine consumes the
    /// Invert flag INSIDE the read as the half selector, so the output flip
    /// rides InvertOutput instead (SourceCoercion, the
    /// InvertConsumedByHalfAxisRead contract). Until now only the Workshop
    /// translator and the legacy migrator wrote it, no control showed it, and
    /// a capture-and-reapply net carried it across save rebuilds. That net
    /// preserved values but also RESURRECTED them: it re-stamped the captured
    /// flag after the rebuild, so even once a checkbox existed, unchecking it
    /// would have been undone on the very save that should persist it. The
    /// field rides the VM round-trip now and the net is deleted.</para>
    /// </summary>
    public class InvertOutputCardTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // ── the round-trip that replaced the capture net ───────────────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SourceItemRoundTripsTheFlag(bool value)
        {
            // False matters as much as true: the net's defect was exactly that
            // a cleared flag could not survive a save.
            var src = new MappingSource
            {
                Descriptor = "Axis 2",
                HalfAxis = true,
                InvertOutput = value,
            };

            var item = MappingSourceItem.FromDomain(src);
            Assert.Equal(value, item.InvertOutput);

            var back = item.ToDomain();
            Assert.Equal(value, back.InvertOutput);
        }

        [Fact]
        public void UncheckingSurvivesTheRoundTrip()
        {
            // The resurrection defect, stated as a test: start from an
            // imported source with the flip on, uncheck in the VM, and the
            // domain object built from that VM must carry false.
            var imported = new MappingSource
            {
                Descriptor = "Axis 6",
                HalfAxis = true,
                InvertOutput = true,
            };
            var item = MappingSourceItem.FromDomain(imported);

            item.InvertOutput = false;                 // the user unchecks

            Assert.False(item.ToDomain().InvertOutput);
        }

        // ── applicability delegates to the engine's predicate ──────────────

        [Fact]
        public void ApplicabilityIsTheEnginesOwnPredicate()
        {
            // The predicate's doc names itself the ONE definition of "Invert
            // is spoken for on this source" and warns that a second copy in a
            // caller is how the two roles drift apart. So the VM property must
            // agree with it across the descriptor families, not re-derive it.
            foreach (var (descriptor, half) in new[]
            {
                ("Axis 2", true), ("Axis 2", false),
                ("Button 3", true), ("Button 3", false),
                ("Slider 0", true),
                ("Touchpad 0 Finger 0 X", true),
                ("Gyro Yaw", true),
                ("Mouse Motion X", true),
            })
            {
                var item = new MappingSourceItem { Descriptor = descriptor, HalfAxis = half };
                bool engine = SourceCoercion.InvertConsumedByHalfAxisRead(
                    new MappingSource { Descriptor = descriptor, HalfAxis = half });
                Assert.Equal(engine, item.IsInvertOutputApplicable);
            }
        }

        [Fact]
        public void PositiveControl_TheKeyCaseIsReallyApplicable()
        {
            // Without this, the delegation test above could pass with a
            // predicate that returns false for everything.
            Assert.True(new MappingSourceItem { Descriptor = "Axis 2", HalfAxis = true }
                .IsInvertOutputApplicable);
            Assert.False(new MappingSourceItem { Descriptor = "Axis 2", HalfAxis = false }
                .IsInvertOutputApplicable);
        }

        [Fact]
        public void ThePrimaryProbeStripsTheLegacyPrefix()
        {
            // The grid primary keeps its legacy I/H encoding in the raw
            // descriptor ("IHAxis 2"), and the engine predicate canonicalizes
            // clean bodies. A probe on the raw string would read the wrong
            // grammar, which is the exact trap MappingItem's own doc warns
            // about for every family predicate.
            var mi = new MappingItem("Test", "LeftThumbAxisX", MappingCategory.LeftStick);
            mi.LoadDescriptor("IHAxis 2");   // inverted + half, prefix-encoded

            Assert.True(mi.IsHalfAxis);
            Assert.True(mi.IsInvertOutputApplicable);

            mi.LoadDescriptor("Axis 2");     // no half: not applicable
            Assert.False(mi.IsInvertOutputApplicable);
        }

        // ── the wiring that cannot be unit-driven is pinned by inspection ──

        [Fact]
        public void TheHydrationAndRebuildLegsExist()
        {
            // Same guard style as the flick card's coverage test: these two
            // lines live deep inside service methods that need a running slot
            // to drive, so their presence is pinned textually and their
            // behaviour by the round-trip tests above.
            string input = Read("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("mapping.InvertOutput = primary.InvertOutput;", input);

            string settings = Read("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("InvertOutput = mapping.InvertOutput,", settings);
        }

        [Fact]
        public void TheCaptureNetIsGone()
        {
            // The net preserved InvertOutput across rebuilds while no control
            // could author it. With the field on the VM round-trip it became
            // actively harmful: the reapply re-stamped the captured value
            // AFTER the rebuild, so an uncheck would be silently undone on
            // save. If either name reappears, that defect is probably back.
            string settings = Read("PadForge.App", "Services", "SettingsService.cs");
            Assert.DoesNotContain("CaptureInvertOutputFlags", settings);
            Assert.DoesNotContain("ApplyInvertOutputFlagsToRow", settings);
        }

        [Fact]
        public void BothEditorsOfferTheCheckboxGatedOnApplicability()
        {
            // Two sites, deliberately: the grid row's primary and the
            // ExtraSources chips (which is also where a bipolar row's neg leg
            // hydrates). A checkbox without the visibility gate would render
            // on every source and silently do nothing on most of them.
            string xaml = Read("PadForge.App", "Views", "PadPage.xaml");

            int first = xaml.IndexOf("Binding InvertOutput,", StringComparison.Ordinal);
            int second = xaml.IndexOf("Binding InvertOutput,", first + 1, StringComparison.Ordinal);
            Assert.True(first > 0 && second > first, "expected the checkbox in BOTH editors");

            foreach (int site in new[] { first, second })
            {
                string around = xaml.Substring(site, Math.Min(600, xaml.Length - site));
                Assert.Contains("IsInvertOutputApplicable", around);
            }
        }
    }
}
