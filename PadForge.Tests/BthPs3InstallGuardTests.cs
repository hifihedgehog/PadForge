using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The BthPS3 install guard, against a real registry but under HKCU so no
    /// elevation is needed and nothing system-wide is touched.
    ///
    /// The defect these lock, observed on a fresh machine 2026-08-06: writing
    /// BthPS3's Parameters with CreateSubKey materialised the parent
    /// Services\BthPS3 key before the driver was installed. A key-existence
    /// check then read that shell as an installed service, so EnsureInstalled
    /// short-circuited its eight-step install permanently. The profile service
    /// was never advertised, no PDO spawned, PSM patching could never arm, and
    /// the DualShock 3 sat flashing because the inbox HID stack refused its
    /// L2CAP connection. Get-Service said NOT INSTALLED the whole time, and
    /// the machine could not recover on its own.
    /// </summary>
    public sealed class BthPs3InstallGuardTests : IDisposable
    {
        private const string TestRoot = @"Software\PadForgeTests\ServicesProbe";
        private readonly RegistryKey _services;

        public BthPs3InstallGuardTests()
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
            _services = Registry.CurrentUser.CreateSubKey(TestRoot, writable: true);
        }

        public void Dispose()
        {
            _services?.Dispose();
            try { Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false); }
            catch { }
        }

        private void MakeShell(string name)
        {
            // Exactly what CreateSubKey on <service>\Parameters produced: a
            // parent key holding settings and nothing else.
            using var p = _services.CreateSubKey(name + @"\Parameters", writable: true);
            p.SetValue("RawPDO", 1, RegistryValueKind.DWord);
            p.SetValue("ExclusivePDO", 0, RegistryValueKind.DWord);
        }

        private void MakeRealService(string name)
        {
            MakeShell(name);
            using var k = _services.OpenSubKey(name, writable: true);
            k.SetValue("ImagePath", @"\SystemRoot\System32\drivers\BthPS3.sys",
                RegistryValueKind.ExpandString);
        }

        /// <summary>The shell must NOT read as installed. This is the whole
        /// bug: it did, and the install never ran again.</summary>
        [Fact]
        public void SettingsOnlyKey_IsNotAnInstalledService()
        {
            MakeShell("BthPS3");
            Assert.False(Ds3DriverInstaller.IsServiceInstalled(_services, "BthPS3"));
        }

        [Fact]
        public void KeyWithImagePath_IsAnInstalledService()
        {
            MakeRealService("BthPS3");
            Assert.True(Ds3DriverInstaller.IsServiceInstalled(_services, "BthPS3"));
        }

        [Fact]
        public void AbsentKey_IsNotAnInstalledService()
            => Assert.False(Ds3DriverInstaller.IsServiceInstalled(_services, "BthPS3"));

        /// <summary>The heal predicate fires on the damaged shape only. Absent
        /// is the normal first-run state and must not trigger a repair, and a
        /// real service must never be deleted.</summary>
        [Fact]
        public void OrphanDetection_FiresOnTheShellAlone()
        {
            Assert.False(Ds3DriverInstaller.HasOrphanedServiceKey(_services, "BthPS3"));

            MakeShell("BthPS3");
            Assert.True(Ds3DriverInstaller.HasOrphanedServiceKey(_services, "BthPS3"));

            using (var k = _services.OpenSubKey("BthPS3", writable: true))
                k.SetValue("ImagePath", "x", RegistryValueKind.ExpandString);
            Assert.False(Ds3DriverInstaller.HasOrphanedServiceKey(_services, "BthPS3"));
        }

        /// <summary>Orphaned and installed are not complements: exactly one of
        /// the three states is repairable, and conflating "not installed" with
        /// "needs repair" would delete nothing on a fresh machine and
        /// everything on a broken one.</summary>
        [Fact]
        public void TheThreeStates_AreDistinct()
        {
            Assert.False(Ds3DriverInstaller.IsServiceInstalled(_services, "Ghost"));
            Assert.False(Ds3DriverInstaller.HasOrphanedServiceKey(_services, "Ghost"));

            MakeShell("Shell");
            Assert.False(Ds3DriverInstaller.IsServiceInstalled(_services, "Shell"));
            Assert.True(Ds3DriverInstaller.HasOrphanedServiceKey(_services, "Shell"));

            MakeRealService("Real");
            Assert.True(Ds3DriverInstaller.IsServiceInstalled(_services, "Real"));
            Assert.False(Ds3DriverInstaller.HasOrphanedServiceKey(_services, "Real"));
        }

        // ── wiring the behavioural tests cannot reach ────────────────────

        private static string PairSrc([CallerFilePath] string me = null)
            => File.ReadAllText(Path.Combine(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), "..")),
                "PadForge.App", "Services", "Ds3PairingService.cs"));

        private static string Src([CallerFilePath] string me = null)
            => File.ReadAllText(Path.Combine(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), "..")),
                "PadForge.App", "Services", "Ds3DriverInstaller.cs"));

        /// <summary>The params writer must never fabricate the parent key. Its
        /// twin EnsurePadForgeOwnsPsmPatch has always opened rather than
        /// created and said so in its comment; this one diverged, and that
        /// divergence is what shipped the dead stack.</summary>
        [Fact]
        public void ConsumerParams_NeverFabricatesTheServiceKey()
        {
            string src = Src();
            int at = src.IndexOf("private static void EnsureConsumerParams", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, 1200);
            Assert.Contains("if (!IsServiceInstalled(\"BthPS3\")) return;", body, StringComparison.Ordinal);
        }

        /// <summary>The install heals the shell before installing, or a
        /// machine already in that state stays broken forever: nothing else
        /// removes it and no user action reaches it.</summary>
        [Fact]
        public void Install_HealsTheOrphanBeforeInstalling()
        {
            string src = Src();
            int guard = src.IndexOf("if (HasOrphanedBthPs3Key())", StringComparison.Ordinal);
            int install = src.IndexOf("Installing PlayStation Bluetooth drivers", StringComparison.Ordinal);
            Assert.True(guard > 0, "no orphan heal in EnsureInstalled");
            Assert.True(install > 0);
            Assert.True(guard < install, "the heal must run BEFORE the install it unblocks");
            Assert.Contains("DeleteSubKeyTree(", src, StringComparison.Ordinal);
        }

        // ── the install verdict waits for asynchronous PnP ───────────────

        /// <summary>The poll helper honours its timeout and its success path.
        /// Behavioural, since the helper is pure arithmetic over a probe.</summary>
        [Fact]
        public void WaitForCondition_PollsToSuccessAndToTimeout()
        {
            int calls = 0;
            Assert.True(Ds3DriverInstaller.WaitForCondition(
                () => ++calls >= 3, timeoutMs: 5000, pollMs: 1));
            Assert.Equal(3, calls);

            Assert.False(Ds3DriverInstaller.WaitForCondition(
                () => false, timeoutMs: 30, pollMs: 5));

            // An immediately-true probe returns without sleeping at all.
            Assert.True(Ds3DriverInstaller.WaitForCondition(
                () => true, timeoutMs: 0, pollMs: 1000));
        }

        /// <summary>Arming PSM patching is what routes the pad's reserved
        /// PSM to BthPS3, and every arm site sits immediately after an install
        /// or a radio cycle, which is exactly when the filter is detached and
        /// its control device absent. An arm with no wait therefore no-ops in
        /// silence. That is the first-pairing failure observed on the
        /// 2026-08-06 arcade-PC rehearsal: the pad was refused and flashed,
        /// and only a SECOND ceremony worked, because by then patching had
        /// been armed once and the filter restores its per-radio state across
        /// cycles.</summary>
        [Fact]
        public void PsmArming_WaitsForTheFilterAndIsCheckedForEffect()
        {
            string src = Src();
            // The arm helper waits rather than probing once.
            Assert.Contains("SetPsmPatching(true, log, 20000)", src, StringComparison.Ordinal);
            // The toggle reports how many radios took it, so zero is failure
            // rather than silence.
            Assert.Contains("public static int SetPsmPatching(", src, StringComparison.Ordinal);
            Assert.Contains("return count;", src, StringComparison.Ordinal);
            // The install verdict requires patching to have taken.
            Assert.Contains("if (EnsurePsmPatch(log) == 0)", src, StringComparison.Ordinal);

            string pair = PairSrc();
            // The ceremony does not invite the PS press until the filter is
            // back from the pairing radio cycle.
            int cycle = pair.IndexOf("CycleRadio();", StringComparison.Ordinal);
            int wait = pair.IndexOf("WaitForPsmControlDevice(20000)", cycle, StringComparison.Ordinal);
            int prompt = pair.IndexOf("Unplug the DS3 and press the PS button", cycle, StringComparison.Ordinal);
            Assert.True(wait > cycle, "no filter wait after the pairing radio cycle");
            Assert.True(prompt > wait, "the PS-button prompt must come AFTER the filter is back");
            Assert.Contains("wantPatching ? 20000 : 0", pair, StringComparison.Ordinal);
        }

        /// <summary>The install's verdict is polled, not one-shot: PnP creates
        /// the BthPS3 service asynchronously after the advertisement, so a
        /// synchronous check raced it and declared failure on installs that
        /// were seconds from succeeding (arcade-PC rehearsal, 2026-08-06). On
        /// timeout it re-enumerates the radio and waits again, because on
        /// hardware whose port cycle was refused the PDO never spawns without
        /// it: the same rehearsal only succeeded after a manual adapter
        /// toggle, which is the fallback CycleBluetoothRadio now performs
        /// itself (Disable/Enable when CyclePort throws).</summary>
        [Fact]
        public void InstallVerdict_WaitsAndRetriesThroughARadioCycle()
        {
            string src = Src();
            int at = src.IndexOf("public static bool EnsureInstalled", StringComparison.Ordinal);
            Assert.True(at > 0);
            // To the end of the method's happy path, not a guessed length: the
            // fixed window this replaced went stale the first time the method
            // grew and threw instead of failing with a message.
            int end = src.IndexOf("Bluetooth drivers installed.", at, StringComparison.Ordinal);
            Assert.True(end > at, "EnsureInstalled's verdict line not found");
            string body = src.Substring(at, end - at);
            Assert.Contains("WaitForCondition(() => IsServiceInstalled(\"BthPS3\")", body,
                StringComparison.Ordinal);
            int firstWait = body.IndexOf("WaitForCondition", StringComparison.Ordinal);
            // From the first wait onward: step 3 of the install is also a
            // radio cycle and sits before the waits, so an unanchored search
            // finds that one and misreads the order.
            int cycle = body.IndexOf("CycleBluetoothRadio(log)", firstWait, StringComparison.Ordinal);
            Assert.True(cycle > firstWait, "no radio-cycle retry after the first wait");
            int secondWait = body.IndexOf("WaitForCondition", cycle, StringComparison.Ordinal);
            Assert.True(secondWait > cycle, "no second wait after the radio cycle");

            int cyc = src.IndexOf("public static void CycleBluetoothRadio", StringComparison.Ordinal);
            Assert.True(cyc > 0);
            string cbody = src.Substring(cyc, 2200);
            Assert.Contains("CyclePort()", cbody, StringComparison.Ordinal);
            Assert.Contains("radio.Disable()", cbody, StringComparison.Ordinal);
            Assert.Contains("radio.Enable()", cbody, StringComparison.Ordinal);
        }
    }
}
