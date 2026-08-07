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
    }
}
