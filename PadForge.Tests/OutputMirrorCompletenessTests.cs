using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Mirror-completeness guards for outputs that live OUTSIDE the 16-bit
    /// Gamepad.Buttons mask, each as its own bool field. Every such output
    /// needs its own arm in each surface that reads button state, and the
    /// compiler cannot notice a missing one. Share was the first; the
    /// DualSense mute and the Edge's back and Fn pairs are the rest, and
    /// each of the three surfaces below was found carrying Share alone.
    ///
    /// These read source text because the surfaces are a private switch, a
    /// private reader and a lambda captured into a static provider, with no
    /// seam a unit test can call. That is the same shape, and the same
    /// justification, as BthPs3PsmIoctlTests. A source-shape test is weaker
    /// than a behavioural one and is used only where no seam exists.
    /// </summary>
    public class OutputMirrorCompletenessTests
    {
        private static readonly string[] ExtraOutputs =
            { "ButtonMute", "LeftPaddle", "RightPaddle", "LeftFunction", "RightFunction" };

        private static readonly string[] ExtraFields =
            { "MicMute", "LeftPaddle", "RightPaddle", "LeftFunction", "RightFunction" };

        private static string Read(string rel, [CallerFilePath] string me = null)
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), ".."));
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), path);
            return File.ReadAllText(path);
        }

        /// <summary>Drops whole-line comments. A source-shape assertion that
        /// matches raw substrings passes on a commented-out line, which is
        /// exactly how the code would be disabled: mutation-testing these
        /// guards by commenting a line was the thing that showed it.</summary>
        private static string Live(string body)
            => string.Join("\n", body.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        /// <summary>The 2D annotation chip's output ember. Without an arm the
        /// chip renders, its controller sprite lights on press, and the ember
        /// beside it stays dark.</summary>
        [Fact]
        public void AnnotationOutputDots_CoverEveryExtraOutput()
        {
            string src = Read("PadForge.App/Views/ControllerModel2DView.Annotations.cs");
            int at = src.IndexOf("GetAnnotationButtonState", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = Live(src.Substring(at));
            foreach (var t in ExtraOutputs)
                Assert.Contains($"\"{t}\" => _vm.", body, StringComparison.Ordinal);
        }

        /// <summary>The mapping grid's live value column. Without an arm the
        /// reader returns null and the caller falls back to the selected
        /// device's raw primary source, so a secondary source or a shift
        /// layer driving the output reads zero.</summary>
        [Fact]
        public void CombinedOutputValueReader_CoversEveryExtraOutput()
        {
            string src = Read("PadForge.App/Services/InputService.cs");
            int at = src.IndexOf("private int? ReadCombinedOutputValue", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = Live(src.Substring(at, 6000));
            foreach (var t in ExtraOutputs)
                Assert.Contains($"\"{t}\"", body, StringComparison.Ordinal);
        }

        /// <summary>The input-reactive lighting mask. Gamepad.Buttons is full
        /// at 16 bits, so each extra output has to be folded onto a spare bit
        /// or pressing it produces no rising edge and no pulse.</summary>
        [Fact]
        public void ReactiveLightingMask_CoversEveryExtraOutput()
        {
            string src = Read("PadForge.App/Services/InputService.cs");
            int at = src.IndexOf("UserEffectsDispatcher.SlotButtonsProvider", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = Live(src.Substring(at, 1800));
            foreach (var f in ExtraFields)
                Assert.Contains($"gp.{f}", body, StringComparison.Ordinal);

            // Each on its OWN bit: two outputs sharing one would make the
            // dispatcher read a press of either as the same edge.
            var bits = System.Text.RegularExpressions.Regex
                .Matches(body, @"mask \|= (0x[0-9A-Fa-f]+)u")
                .Select(m => m.Groups[1].Value.ToLowerInvariant())
                .ToList();
            Assert.Equal(bits.Count, bits.Distinct().Count());
        }

        /// <summary>A mono persona mic downmixes its endpoint's channels
        /// instead of taking channel 0, matching the Bluetooth mic path. A
        /// headset whose capture sits on the other channel came through
        /// silent.</summary>
        [Fact]
        public void PersonaMicCapture_DownmixesRatherThanTruncates()
        {
            string src = Read("PadForge.App/Common/Input/AudioPassthroughService.cs");
            // Anchor on the DEFINITION: the call site appears first.
            int at = src.IndexOf("private static void StartPersonaMic", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = Live(src.Substring(at, 4000));
            Assert.Contains("outCh == 1 && inCh > 1", body, StringComparison.Ordinal);
            Assert.Contains("sum / inCh", body, StringComparison.Ordinal);
        }

        // ── DualShock 3 driver-stack guards (discussion #283) ────────────

        /// <summary>The BthPS3 stack is two drivers and the install guard
        /// asks about both. Asking only whether the profile service exists
        /// left a half-installed machine that way permanently, with the PSM
        /// filter absent and Bluetooth silently never working.</summary>
        [Fact]
        public void DriverInstallGuard_ChecksThePsmFilterToo()
        {
            string src = Read("PadForge.App/Services/Ds3DriverInstaller.cs");
            int at = src.IndexOf("public static bool EnsureInstalled", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = Live(src.Substring(at, 1800));
            Assert.Contains("IsPsmFilterPresent()", body, StringComparison.Ordinal);
            Assert.Contains("RepairPsmFilter", body, StringComparison.Ordinal);
        }

        /// <summary>The WinUSB bind checks the package's signature trust
        /// before attempting. Windows refuses an untrusted catalog with a
        /// generic error, so the unchecked path reported nothing a user
        /// could act on and sent #283 chasing a USB cable.</summary>
        [Fact]
        public void WinUsbBind_ChecksPackageTrustFirst()
        {
            string src = Read("PadForge.App/Services/Ds3DriverInstaller.cs");
            int at = src.IndexOf("public static bool EnsureWinUsbBound", StringComparison.Ordinal);
            Assert.True(at > 0);
            // Semantic window (method start through the install call), not a
            // fixed length: a fixed slice broke when the bind grew its
            // live-node preamble (#285).
            int end = src.IndexOf("InstallInf(infPath", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string body = Live(src.Substring(at, end - at));
            Assert.Contains("IsWinUsbPackageTrusted", body, StringComparison.Ordinal);

            string pair = Read("PadForge.App/Services/Ds3PairingService.cs");
            Assert.Contains("driver-untrusted", pair, StringComparison.Ordinal);
        }

        /// <summary>The pairing dialog keeps the failing step visible. Its
        /// own failure text says the step is shown above, and one TextBlock
        /// carried both, so the verdict erased the only clue a bug report
        /// could have carried.</summary>
        [Fact]
        public void PairingDialog_PreservesTheFailingStep()
        {
            string xaml = Read("PadForge.App/Views/PairDeviceDialog.xaml");
            Assert.Contains("LastStepText", xaml, StringComparison.Ordinal);

            string cs = Read("PadForge.App/Views/PairDeviceDialog.xaml.cs");
            Assert.Contains("LastStepText.Text = StatusText.Text", cs, StringComparison.Ordinal);
            // The caught exception must reach the screen, not be replaced by
            // the generic verdict one statement later.
            Assert.Contains("fault ?? result?.Error switch", cs, StringComparison.Ordinal);
        }

        /// <summary>
        /// EVERY HIDMaestro sweep call site passes preserveInstall.
        ///
        /// <para>The parameterless overload is uninstall-grade: it deletes
        /// the whole HKLM\SOFTWARE\HIDMaestro tree, which carries the HID
        /// manifest hash (the same-version fast path), the SteamVRPath hint
        /// that locates a steamcmd SteamVR, and the VR driver's registration
        /// gate. Losing that gate makes the next VR slot re-extract
        /// driver_hidmaestro.dll into a running vrserver.exe, which holds it
        /// loaded, so slot creation dies on a sharing violation. The
        /// preserving overload still evicts devices and orphans, which is
        /// the only thing any of these call sites actually wants.</para>
        ///
        /// <para>This is a COUNT, not a spot check, because the class shipped
        /// twice from being fixed point-wise: the startup sweep was corrected
        /// on its own while the context preflight and the process-exit hook
        /// kept nuking the tree, so a session that mixed a conventional HM
        /// slot with a VR slot re-armed the very bug the first fix closed.
        /// A new call site added later fails here instead of in the field.</para>
        /// </summary>
        [Fact]
        public void EveryHidMaestroSweep_PreservesTheInstall()
        {
            string[] files =
            {
                "PadForge.App/App.xaml.cs",
                "PadForge.App/Common/Input/InputManager.Step5.VirtualDevices.cs",
            };

            int callSites = 0;
            foreach (string f in files)
                foreach (string line in Live(Read(f)).Split('\n'))
                {
                    int at = line.IndexOf("RemoveAllVirtualControllers(", StringComparison.Ordinal);
                    if (at < 0) continue;
                    callSites++;
                    string args = line.Substring(at + "RemoveAllVirtualControllers(".Length);
                    Assert.True(args.StartsWith("preserveInstall", StringComparison.Ordinal),
                        $"{f}: sweep call site must pass preserveInstall, got: {line.Trim()}");
                }

            // A guard that finds nothing to guard is not a guard. Both known
            // sites plus the startup one must be present.
            Assert.Equal(3, callSites);
        }
    }
}
