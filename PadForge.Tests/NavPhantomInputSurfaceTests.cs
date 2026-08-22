using System;
using System.IO;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The PlayStation Move Navigation Controller wore the whole DualShock 3's
    /// input surface: eleven button circles and sixteen axes, on a pad with
    /// five standardized buttons and ten real axes.
    ///
    /// <para>The shape is not a mapping convenience. hid-sony calls the pad "a
    /// partial DS3 [that] uses the same HID report and hence the same keymap
    /// indices, however not all axes/buttons are physically present"
    /// (hid-sony.c:373-377), and moveonpc's Navigation report table lists a
    /// byte for the d-pad, L1, L2, Circle and X and nothing at all where the
    /// DS3 carries Square, Triangle and R1. Those slots read zero forever.</para>
    ///
    /// <para>PadForge's own virtual descriptor was already right: button_mask
    /// 0x7AA3 binds exactly Cross, Circle, PS, L3, L1 and the four d-pad
    /// directions, and axis_mask 0x13 binds the left stick and L2. What was
    /// wrong is that the capability lists never asked. The standardized
    /// eleven were emitted dense on the assumption that every recognized
    /// gamepad has all of them, and the generic extras past axis 5 were
    /// emitted dense on the assumption that an extra axis is always real. The
    /// Navigation is the counterexample to both, and the PS Move (three
    /// phantom buttons) and the VR controllers (five and ten) were already
    /// living with the first one.</para>
    /// </summary>
    public class NavPhantomInputSurfaceTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        /// <summary>The Navigation's real axis slots. 0 and 1 left stick and 2
        /// L2 from the standardized block, then the pressure axes it
        /// populates: 6 Cross, 7 Circle, 10 L1, 12-15 the d-pad. 3 and 4 right
        /// stick, 5 R2, 8 Square, 9 Triangle and 11 R1 are absent.</summary>
        private static readonly int[] NavAxes = { 0, 1, 2, 6, 7, 10, 12, 13, 14, 15 };

        /// <summary>The Navigation's real standardized buttons, in PadForge
        /// positions: 0 Cross, 1 Circle, 4 L1, 8 L3, 10 PS.</summary>
        private static readonly int[] NavButtons = { 0, 1, 4, 8, 10 };

        private static UserDevice Nav() => new UserDevice
        {
            CapType = InputDeviceType.Gamepad,
            VendorId = 0x054C,
            ProdId = 0x042F,
            CapAxeCount = NavAxes.Length,
            CapAxisIndices = NavAxes,
            CapButtonCount = NavButtons.Length,
            CapButtonIndices = NavButtons,
        };

        // ───────────────────────── the axis list ─────────────────────────

        /// <summary>THE BUG, axis half. Offline, the pad reports the slots it
        /// populates and not a dense range invented from the count.</summary>
        [Fact]
        public void OfflineNav_ReportsOnlyTheAxisSlotsItPopulates()
        {
            Assert.Equal(NavAxes, InputService.ResolveAxisIndices(Nav()));
        }

        /// <summary>The specific complaint, stated so a regression fails with
        /// the owner's own symptom: sixteen axis rows on a ten-axis pad.</summary>
        [Fact]
        public void OfflineNav_DoesNotSurfaceSixteenDenseAxes()
        {
            var got = InputService.ResolveAxisIndices(Nav());
            Assert.Equal(10, got.Length);
            Assert.NotEqual(Enumerable.Range(0, 16).ToArray(), got);
            foreach (int phantom in new[] { 3, 4, 5, 8, 9, 11 })
                Assert.DoesNotContain(phantom, got);
        }

        /// <summary>The numbering does NOT close its gaps. "Axis 10" has to
        /// keep meaning L1 on a Navigation and on a DualShock 3 alike, or a
        /// stored mapping would change meaning with the pad it was made
        /// on.</summary>
        [Fact]
        public void NavAxisNumbering_KeepsItsGapsRatherThanRenumbering()
        {
            var got = InputService.ResolveAxisIndices(Nav());
            Assert.Contains(10, got);
            Assert.Contains(15, got);
            Assert.True(got.SequenceEqual(got.OrderBy(x => x)));
        }

        /// <summary>Raw passthrough is the escape hatch on the axis side too,
        /// exactly as it is for buttons: it asks a different question and gets
        /// every native slot back.</summary>
        [Fact]
        public void RawJoystickMode_BypassesTheAxisGate()
        {
            var ud = Nav();
            ud.ForceRawJoystickMode = true;
            ud.RawAxisCount = 16;
            Assert.Equal(Enumerable.Range(0, 16).ToArray(), InputService.ResolveAxisIndices(ud));
        }

        /// <summary>A pad PadForge has never seen online has no observed
        /// positions to report, so the dense range stands. Mirrors the button
        /// side's fallback rather than inventing a different rule.</summary>
        [Fact]
        public void NeverSeenOnline_FallsBackToTheDenseRange()
        {
            var ud = new UserDevice { CapType = InputDeviceType.Gamepad, CapAxeCount = 6 };
            Assert.Equal(Enumerable.Range(0, 6).ToArray(), InputService.ResolveAxisIndices(ud));
        }

        // ──────────────────── the mapping picker ────────────────────

        private static int[] PickerSlots(UserDevice ud, string prefix)
            => PadForge.Common.MappingDisplayResolver.BuildInputChoices(ud)
                .Where(c => c.Descriptor != null
                            && c.Descriptor.StartsWith(prefix, StringComparison.Ordinal))
                .Select(c => c.Descriptor.Substring(prefix.Length))
                .Where(t => int.TryParse(t, out _))
                .Select(int.Parse)
                .ToArray();

        /// <summary>The picker offers what the pad has. This is the surface a
        /// dead input would otherwise be mapped from.</summary>
        [Fact]
        public void OfflinePicker_OffersOnlyTheRealAxes()
        {
            var slots = PickerSlots(Nav(), "Axis ");
            Assert.Equal(NavAxes, slots);
            foreach (int phantom in new[] { 3, 4, 5, 8, 9, 11 })
                Assert.DoesNotContain(phantom, slots);
        }

        /// <summary>And the buttons, the half the screenshot showed as eleven
        /// circles.</summary>
        [Fact]
        public void OfflinePicker_OffersOnlyTheRealButtons()
        {
            var slots = PickerSlots(Nav(), "Button ");
            Assert.Equal(NavButtons, slots);
            foreach (int phantom in new[] { 2, 3, 5, 6, 7, 9 })
                Assert.DoesNotContain(phantom, slots);
        }

        // ──────── the live capability lists (source-text locks) ────────
        //
        // Both gates sit behind SDL_GamepadHasButton and a live SDL_Gamepad
        // handle, so they have no in-process seam: a behaviour test would need
        // a real Navigation attached. The contract is locked in the source,
        // the pattern this repo already uses (NavPairingTransportTests),
        // rather than left unlocked because it is awkward to reach.

        private static string WrapperBody(string marker)
        {
            string src = Read("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            int at = src.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at > 0, "marker not found: " + marker);
            return src.Substring(at, Math.Min(2600, src.Length - at));
        }

        /// <summary>THE BUG, button half. The standardized eleven are asked
        /// about, not assumed. A regression that restores the bare
        /// <c>list.Add(i)</c> loop fails here.</summary>
        [Fact]
        public void ComputeSupportedButtonIndices_AsksSdlAboutTheStandardEleven()
        {
            string body = WrapperBody("private int[] ComputeSupportedButtonIndices()");
            int loop = body.IndexOf("for (int i = 0; i < 11 && i < max; i++)", StringComparison.Ordinal);
            Assert.True(loop > 0, "the standardized-block loop moved");
            int ext = body.IndexOf("_extButtonPresent = new bool[22];", StringComparison.Ordinal);
            Assert.True(ext > loop, "the extended block should still follow");
            Assert.Contains("SDL_GamepadHasButton", body.Substring(loop, ext - loop));
        }

        /// <summary>The live object list is the picker's source while the pad
        /// is connected, so it carries the same gate. Its old comment claimed
        /// positions 0-10 "are always present on any recognized gamepad",
        /// which is the assumption this fixes.</summary>
        [Fact]
        public void GetDeviceObjects_GatesEveryStandardizedButtonNotJustTheExtended()
        {
            string src = Read("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            Assert.Contains("if (isGamepad && i <= 21)", src);
            Assert.DoesNotContain("if (isGamepad && i >= 11 && i <= 21)", src);
        }

        /// <summary>Both extra-axis enumerations run through the phantom gate.
        /// The capability list and the object list have to agree, or the
        /// picker and the preview disagree the moment the pad connects.</summary>
        [Fact]
        public void BothExtraAxisEnumerations_RunThroughThePhantomGate()
        {
            string src = Read("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(src, "IsRealExtraAxis").Count);
            Assert.DoesNotContain("for (int i = 6; i < top; i++) list.Add(i);", src);
        }

        /// <summary>The gate names the Navigation by VID:PID and drops exactly
        /// the three slots the hardware lacks. Square, Triangle and R1 have no
        /// byte behind them in moveonpc's report table.</summary>
        [Fact]
        public void ThePhantomGate_DropsSquareTriangleAndR1OnTheNavigation()
        {
            string body = WrapperBody("private bool IsRealExtraAxis(int index)");
            Assert.Contains("VendorId == 0x054C && ProductId == 0x042F", body);
            Assert.Contains("index != 8 && index != 9 && index != 11", body);
        }

        /// <summary>Cross and Circle survive the gate deliberately. moveonpc
        /// records them as digital-only ("always either 0x00 or 0xFF but never
        /// anything in between"), so they are coarse rather than absent, and a
        /// working input is not removed for being two-valued.</summary>
        [Fact]
        public void TheNavigationsFaceButtonPressureAxes_AreKeptNotDropped()
        {
            var got = InputService.ResolveAxisIndices(Nav());
            Assert.Contains(6, got);
            Assert.Contains(7, got);
        }

        // ─────────── persistence and the online boundary ───────────

        /// <summary>The recorded positions have to reach disk, or the pad
        /// renumbers itself on the next launch. That is discussion #344's
        /// defect, on the axis side.</summary>
        [Fact]
        public void TheAxisPositions_AreCoveredByTheDeviceRegistrySignature()
        {
            string src = Read("PadForge.App", "Services", "InputService.cs");
            int at = src.IndexOf("internal static string BuildDeviceRegistrySignature(", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("d.CapAxisIndices", src.Substring(at, Math.Min(4000, src.Length - at)));
        }

        /// <summary>Every consumer that used to enumerate densely from
        /// CapAxeCount now prefers the sparse list. The count became the gated
        /// length, so a dense 0..count-1 loop would both invent slots the pad
        /// lacks and miss the ones it has.</summary>
        [Theory]
        [InlineData("PadForge.Engine", "Data", "PassthroughCloneGenerator.cs")]
        [InlineData("PadForge.App", "Views", "ProfilesPage.xaml.cs")]
        [InlineData("PadForge.App", "Common", "MappingDisplayResolver.cs")]
        public void DenseAxisConsumers_PreferTheSparseList(string a, string b, string c)
        {
            Assert.Contains("CapAxisIndices", Read(a, b, c));
        }
    }
}
