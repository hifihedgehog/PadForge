using System;
using System.IO;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Delta-audit 2026-08-29 contracts (4287822d..HEAD, the Valve
    /// profile and preview delta).</summary>
    public class AuditDelta20260829Tests
    {
        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        // ── K4: the preview bridge's trigger scale ──────────────────────

        private static PadViewModel ValveSlot() => new(0)
        {
            OutputType = VirtualControllerType.Extended,
            ProfileId = "steam-deck-composite",
        };

        private static RawHidState Rest()
        {
            var raw = RawHidState.Create(18, 32, 1);
            raw.Povs[0] = -1;
            raw.Axes[2] = raw.Axes[5] = short.MinValue;
            return raw;
        }

        /// <summary>A half-pulled Valve trigger fills half the preview. The
        /// bridge clamped the bipolar raw slot at zero, the exact defect the
        /// frame packers shipped and fixed: the lower half of the travel
        /// read as zero fill, then the upper half swept the whole bar.</summary>
        [Fact]
        public void AHalfPulledValveTriggerFillsHalfThePreview()
        {
            var vm = ValveSlot();
            var raw = Rest();
            raw.Axes[2] = 0;                    // bipolar midpoint = half pull
            vm.UpdateFromRawHidState(raw);
            Assert.InRange(vm.LeftTrigger, 0.45, 0.55);
            Assert.InRange(vm.RightTrigger, 0.0, 0.01);   // still at rest
        }

        /// <summary>Rest is empty and full pull is full, on both ends of the
        /// rescale.</summary>
        [Fact]
        public void ValveTriggerEndpointsSurviveTheRescale()
        {
            var vm = ValveSlot();
            var raw = Rest();
            vm.UpdateFromRawHidState(raw);
            Assert.InRange(vm.LeftTrigger, 0.0, 0.01);

            raw = Rest();
            raw.Axes[2] = short.MaxValue;
            vm.UpdateFromRawHidState(raw);
            Assert.InRange(vm.LeftTrigger, 0.99, 1.0);
        }

        /// <summary>The normalized right-stick doubles resolve their slots
        /// through the axis table. Hardcoded 2/3 read the LEFT TRIGGER as
        /// the right stick's X on every Valve wire, which interleaves the
        /// triggers at 2 and 5.</summary>
        [Fact]
        public void TheNormalizedRightStickReadsTheValveSlots()
        {
            var vm = ValveSlot();
            var raw = Rest();
            raw.Axes[3] = short.MaxValue;       // RX hard right
            vm.UpdateFromRawHidState(raw);
            Assert.InRange(vm.ThumbRX, 0.99, 1.0);

            // And the left trigger's axis does NOT bleed into it.
            vm = ValveSlot();
            raw = Rest();
            raw.Axes[2] = short.MaxValue;       // LT full pull
            vm.UpdateFromRawHidState(raw);
            Assert.InRange(vm.ThumbRX, 0.45, 0.55);   // stick at center
        }

        /// <summary>A Nintendo slot's digital triggers keep their two-state
        /// preview: no analog trigger axis exists on that wire, so the
        /// button loop owns the value.</summary>
        [Fact]
        public void NintendoDigitalTriggersKeepTheirTwoStates()
        {
            var vm = new PadViewModel(0)
            {
                OutputType = VirtualControllerType.Nintendo,
                ProfileId = "switch-pro",
            };
            var raw = RawHidState.Create(14, 32, 1);
            raw.Povs[0] = -1;
            raw.Buttons[0] = 1u << 6;           // ZL on the switch-pro wire
            vm.UpdateFromRawHidState(raw);
            Assert.Equal(1.0, vm.LeftTrigger, 3);
            Assert.Equal(0.0, vm.RightTrigger, 3);
        }

        // ── K1: captured Workshop fixtures stay upstream bytes ──────────

        /// <summary>A captured Steam config is an UPSTREAM artifact: its
        /// author's own spelling is part of the capture. The American
        /// English sweep rewrote two of them, and one moved a fixed-length
        /// truncation and held a golden red for two days.</summary>
        [Theory]
        [InlineData("789818086.vdf", "labelled")
]
        [InlineData("2790927974.vdf", "Centre Viewscreen")]
        public void CapturedFixturesKeepTheirUpstreamSpelling(string file, string upstream)
        {
            string vdf = RepoText("PadForge.SteamWorkshop.Tests", "Fixtures", file);
            Assert.Contains(upstream, vdf);
        }

        // ── K2 / K3: the web client's layout-driven gates ───────────────

        /// <summary>A stick ring the layout marks "none" gets no nipplejs
        /// zone. The 2015's right-pad ghost ring pulled a relative joystick
        /// twice the pad's size over the pad's own surface zone.</summary>
        [Fact]
        public void TheWebStickZoneHonorsTheNoneOverride()
        {
            string js = RepoText("PadForge.App", "WebAssets", "js", "controller_client.js");
            int at = js.IndexOf("function setupOneStick", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = js.Substring(at, 1400);
            Assert.Contains("stickOv.inputKind === \"none\"", body);
        }

        /// <summary>No synthetic rest-trigger copy on a layout whose base
        /// draws the trigger at rest: the Valve layouts' trigger art is
        /// press blue, and the clone painted a dim blue trigger on an idle
        /// controller.</summary>
        [Fact]
        public void TheWebRestTriggerSynthesisSkipsBaseDrawnLayouts()
        {
            string js = RepoText("PadForge.App", "WebAssets", "js", "controller_client.js");
            Assert.Contains("baseDrawsTriggers", js);
            Assert.Contains("!baseFor[ov.target] && !baseDrawsTriggers", js);

            string server = RepoText("PadForge.App", "Services", "WebControllerServer.cs");
            // The source spells the JSON key ESCAPED, backslash-quote.
            Assert.Contains("baseDrawsTriggers\\\":", server);
            foreach (var key in new[] { "steamdeck", "steamcontroller", "steamcontroller2" })
                Assert.Contains($"TypeKey = \"{key}\", BaseDrawsTriggers = true", server);
        }
    }
}
