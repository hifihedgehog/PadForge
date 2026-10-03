using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The bundled BthPS3 package is the signed 3.2.1 build. It keeps the
    /// L2CAP indication callback context alive past the PDO's teardown
    /// (nefarius/BthPS3 PR #184), which closes the 0x10D bugcheck a DualShock 3
    /// disconnecting over Bluetooth hit through nefarius/BthPS3#182. Its
    /// filter attaches to BTHX radios (PCIe and UART Bluetooth) as well as USB
    /// ones, which arrived in 3.0.0, and 3.2.0 added ETW events for
    /// connection and PSM diagnostics. Issue #204 brought
    /// the 2.12.0 build that closed the remote-disconnect use-after-free, and
    /// a machine carrying any older bundle is upgraded in place at the next
    /// launch with no PlayStation controller connected over Bluetooth, or the
    /// next pairing. The INF is the only version source, and the
    /// bundle's layout follows what the INFs say about where the binaries sit.
    /// </summary>
    public class BthPs3DriverUpgradeTests
    {
        [Theory]
        [InlineData("DriverVer = 10/02/2026,3.2.1.2117", "3.2.1.2117")]
        [InlineData("DriverVer = 09/26/2026,3.2.0.2107", "3.2.0.2107")]
        [InlineData("DriverVer = 09/18/2026,3.0.0.2082", "3.0.0.2082")]
        [InlineData("DriverVer=02/22/2025,2.10.470.0 ; trailing comment", "2.10.470.0")]
        [InlineData("  driverver = 01/01/2020,1.2.3.4", "1.2.3.4")]
        public void ParsesTheDriverVerLine(string line, string expected)
        {
            string inf = "[Version]\nSignature=\"$WINDOWS NT$\"\n" + line + "\n[Strings]\n";
            Assert.Equal(Version.Parse(expected), Ds3DriverInstaller.ParseInfDriverVersion(inf));
        }

        [Fact]
        public void NoDriverVerMeansNoVersion()
        {
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion("[Version]\nSignature=x\n"));
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion(""));
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion(null));
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion("DriverVer = 09/15/2026,notaversion"));
        }

        [Fact]
        public void UpgradesOnlyWhenTheBundleIsStrictlyNewer()
        {
            // The upgrades this release actually performs: a machine on the
            // 3.2.0 build, on the 4.5.3 bundle (3.0.0) or on the one before
            // it (2.12.0) moves to 3.2.1.
            var installed = new Version(2, 12, 0, 2037);
            var bundled = new Version(3, 2, 1, 2117);
            Assert.True(Ds3DriverInstaller.ShouldUpgrade(new Version(3, 2, 0, 2107), bundled));
            Assert.True(Ds3DriverInstaller.ShouldUpgrade(new Version(3, 0, 0, 2082), bundled));
            Assert.True(Ds3DriverInstaller.ShouldUpgrade(installed, bundled));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(bundled, bundled));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(bundled, installed));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(null, bundled));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(installed, null));
        }

        [Fact]
        public void BundledInfsAgreeOnOneVersionAndPlaceTheBinariesWhereTheyPoint()
        {
            string root = Path.Combine(Root(), "PadForge.App", "Resources", "BthPS3");
            string profile = File.ReadAllText(Path.Combine(root, "BthPS3", "BthPS3.inf"));
            string nullPdo = File.ReadAllText(Path.Combine(root, "BthPS3", "BthPS3_PDO_NULL_Device.inf"));
            string filter = File.ReadAllText(Path.Combine(root, "BthPS3PSM", "BthPS3PSM.inf"));
            var v = Ds3DriverInstaller.ParseInfDriverVersion(profile);
            Assert.NotNull(v);
            Assert.True(v >= new Version(3, 2, 1, 2117), v.ToString());
            Assert.Equal(v, Ds3DriverInstaller.ParseInfDriverVersion(nullPdo));
            Assert.Equal(v, Ds3DriverInstaller.ParseInfDriverVersion(filter));

            // [SourceDisksFiles.amd64] BthPS3.sys = 1,x64 means the binary
            // lives in an x64 folder beside the INF, and the catalog beside it.
            // The same INF carries an arm64 section pointing at an ARM64
            // folder, and Windows installs whichever matches the machine. A
            // bundle holding the INF without the binary one of its sections
            // names fails on exactly the machines that section is for, so both
            // are pinned, including the one this bench cannot run.
            foreach (var (inf, folder, sys, cat) in new[]
            {
                (profile, "BthPS3", "BthPS3.sys", "bthps3.cat"),
                (filter, "BthPS3PSM", "BthPS3PSM.sys", "bthps3psm.cat"),
            })
            {
                foreach (string arch in new[] { "amd64", "arm64" })
                {
                    var m = Regex.Match(inf, @"\[SourceDisksFiles\." + arch + @"\]\s*" + Regex.Escape(sys) + @"\s*=\s*1\s*,\s*(\w+)", RegexOptions.IgnoreCase);
                    Assert.True(m.Success, sys + " has no " + arch + " source entry");
                    Assert.True(File.Exists(Path.Combine(root, folder, m.Groups[1].Value, sys)), sys + " missing under " + m.Groups[1].Value);
                }
                Assert.True(File.Exists(Path.Combine(root, folder, cat)), cat + " missing");
            }
            Assert.True(File.Exists(Path.Combine(root, "BthPS3", "bthps3_pdo_null_device.cat")));
        }

        // A catalog hashes the exact bytes of every file it covers, and
        // upstream's BthPS3.inf and BthPS3_PDO_NULL_Device.inf each carry one
        // bare LF among CRLF lines. A checkout that rewrites line endings
        // yields INFs the catalogs reject, and then every driver install
        // fails. These are the bytes of the Partner Center signed 3.2.1.2117
        // build (buildbot.nefarius.at, BthPS3 v3.2.1 build 117,
        // Signed_1152921505702031188.zip) that signtool verified, so a bundle
        // update changes them with the files.
        [Theory]
        [InlineData("BthPS3/BthPS3.inf", "A5B729340A70D276FBD52F40215021AFD203660F8D7D2A5D344C636698F62B9B")]
        [InlineData("BthPS3/BthPS3_PDO_NULL_Device.inf", "3EB0C447DDD08B7CA0DADF65C81DED4AA18F8C5DF35C1DB6FE94383CE16179D4")]
        [InlineData("BthPS3/bthps3.cat", "779FAE9E7575056D4DD43FAF23C01EB8DB57C0447D98738D96854BC696F5C779")]
        [InlineData("BthPS3/bthps3_pdo_null_device.cat", "40AE27C2483DA0EA97DFEAA63D52DF83A270E7F38C36C8D94F73EF82C7C0F657")]
        [InlineData("BthPS3/x64/BthPS3.sys", "D0AC6895E5C09A7867975EDB3605C7B152F1414BB4AC2A03E77439F2969EE6C6")]
        [InlineData("BthPS3/ARM64/BthPS3.sys", "613BE004A3C5ADDBB408349E45D0DA857F7AC7222ED4524BBE8EA97C14D241BA")]
        [InlineData("BthPS3PSM/BthPS3PSM.inf", "C7160BB4160AA2F89DB608EC7F8CECF88CF4B75D293510760389151E43C4D706")]
        [InlineData("BthPS3PSM/bthps3psm.cat", "29AC9EFD8E4BD281327CEFC3D95B9F1C45AD509E758B170BE9A0551B8F66804F")]
        [InlineData("BthPS3PSM/x64/BthPS3PSM.sys", "9FD31DB41FA4165B21559C08F099771135669C4CAD48EAAAC252A07BEF90420D")]
        [InlineData("BthPS3PSM/ARM64/BthPS3PSM.sys", "953D4E0EC80D71E9384B77F9AEAE5B2BE59C0E819BD200992E292F1F00168B0F")]
        public void BundledDriverFilesAreTheSignedReleaseBytes(string relative, string sha256)
        {
            string path = Path.Combine(Root(), "PadForge.App", "Resources", "BthPS3", relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        [Fact]
        public void InstalledBranchUpgradesBeforeArmingTheFilter()
        {
            string src = File.ReadAllText(Path.Combine(Root(), "PadForge.App", "Services", "Ds3DriverInstaller.cs")).Replace("\r\n", "\n");
            int at = src.IndexOf("public static bool EnsureInstalled", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, src.IndexOf("log(\"Installing PlayStation Bluetooth drivers (one time)...\");", at, StringComparison.Ordinal) - at);
            int upgrade = body.IndexOf("UpgradeInstalledDriversIfOlder(log);", StringComparison.Ordinal);
            int arm = body.IndexOf("EnsurePsmPatch(log);", StringComparison.Ordinal);
            Assert.True(upgrade > 0 && arm > upgrade, "the upgrade must run inside the installed branch, before the patch is armed");
            string method = src.Substring(src.IndexOf("private static void UpgradeInstalledDriversIfOlder(", StringComparison.Ordinal));
            method = method.Substring(0, method.IndexOf("\n        }\n", StringComparison.Ordinal));
            Assert.Contains("BthPS3PSM.inf", method);
            Assert.Contains("BthPS3_PDO_NULL_Device.inf", method);
            Assert.Contains("CycleBluetoothRadio(log);", method);
            Assert.Contains("if (!ShouldUpgrade(installed, bundled)) return;", method);
        }

        [Theory]
        [InlineData(true, false, "3.2.0.2107", true)]
        [InlineData(true, false, "2.12.0.2037", true)]
        [InlineData(true, false, "3.2.1.2117", false)]
        [InlineData(false, false, "3.2.0.2107", false)]
        [InlineData(true, true, "3.2.0.2107", false)]
        public void LaunchUpgradesAnOlderStackThatIsNotDsHidMinis(bool service, bool dsHidMini, string installed, bool expected)
            => Assert.Equal(expected, Ds3DriverInstaller.ShouldUpgradeAtStartup(service, dsHidMini,
                Version.Parse(installed), new Version(3, 2, 1, 2117)));

        [Theory]
        [InlineData(true, 0, true)]
        [InlineData(true, 1, false)]
        [InlineData(false, 0, false)]
        public void TheStackIsReplacedOnlyWithNoPlayStationControllerOnBluetooth(bool probed, int children, bool expected)
            => Assert.Equal(expected, Ds3DriverInstaller.MayReplaceDriversNow(probed, children));

        [Fact]
        public void LaunchReadsTheBundledVersionFromTheEmbeddedInf()
            => Assert.Equal(new Version(3, 2, 1, 2117), Ds3DriverInstaller.BundledBthPs3Version());

        /// <summary>The launch upgrade runs before the startup reconcile, which
        /// arms the filter, and under the radio gate the ceremonies take, as the
        /// ceremonies' own install step now does. It refuses new PlayStation
        /// links before replacing the stack, and the replacement checks for a
        /// connected controller before its first install.</summary>
        [Fact]
        public void LaunchReplacesTheStackUnderTheRadioGateBeforeTheReconcile()
        {
            string app = Read("PadForge.App", "App.xaml.cs");
            int upgrade = app.IndexOf("Ds3PairingService.UpgradeDriversAtStartup();", StringComparison.Ordinal);
            int reconcile = app.IndexOf("Ds3PairingService.ReconcilePsmPatchForCrashSafety(\"startup\");", StringComparison.Ordinal);
            Assert.True(upgrade > 0 && reconcile > upgrade, "the launch upgrade must run before the startup reconcile");

            string svc = Read("PadForge.App", "Services", "Ds3PairingService.cs");
            Assert.Contains("lock (_radioGate) Ds3DriverInstaller.UpgradeAtStartupIfOlder(LogLine);", svc);
            Assert.Contains("lock (_radioGate) return Ds3DriverInstaller.EnsureInstalled(_log);", svc);

            string inst = Read("PadForge.App", "Services", "Ds3DriverInstaller.cs");
            string launch = Method(inst, "internal static void UpgradeAtStartupIfOlder(");
            int should = launch.IndexOf("ShouldUpgradeAtStartup(", StringComparison.Ordinal);
            int refuse = launch.IndexOf("RequestPsmPatching(false, log);", StringComparison.Ordinal);
            int replace = launch.IndexOf("UpgradeInstalledDriversIfOlder(log);", StringComparison.Ordinal);
            Assert.True(should > 0 && refuse > should && replace > refuse, "check, refuse new links, then replace");

            string body = Method(inst, "private static void UpgradeInstalledDriversIfOlder(");
            int guard = body.IndexOf("if (!MayReplaceDriversNow(probed, children))", StringComparison.Ordinal);
            int install = body.IndexOf("InstallInf(", StringComparison.Ordinal);
            Assert.True(guard > 0 && install > guard, "the connected-controller check must come before the first install");
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(Root(), Path.Combine(parts))).Replace("\r\n", "\n");

        private static string Method(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, signature + " moved");
            return src.Substring(at, src.IndexOf("\n        }\n", at, StringComparison.Ordinal) - at);
        }

        private static string Root()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }
    }
}
