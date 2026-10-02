using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The bundled BthPS3 package is the signed 3.2.0 build, the latest
    /// release. Its filter attaches to BTHX radios (PCIe and UART Bluetooth)
    /// as well as USB ones, which arrived in 3.0.0, and 3.2.0 adds ETW events
    /// for connection and PSM diagnostics. Issue #204 brought
    /// the 2.12.0 build that closed the remote-disconnect use-after-free, and
    /// a machine carrying any older bundle is upgraded in place the next time
    /// the pairing flow runs. The INF is the only version source, and the
    /// bundle's layout follows what the INFs say about where the binaries sit.
    /// </summary>
    public class BthPs3DriverUpgradeTests
    {
        [Theory]
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
            // 4.5.3 bundle (3.0.0) or on the one before it (2.12.0) moves to
            // 3.2.0.
            var installed = new Version(2, 12, 0, 2037);
            var bundled = new Version(3, 2, 0, 2107);
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
            Assert.True(v >= new Version(3, 2, 0, 2107), v.ToString());
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
        // fails. These are the bytes of the setup-v3.2.0 release MSI that
        // signtool verified, so a bundle update changes them with the files.
        [Theory]
        [InlineData("BthPS3/BthPS3.inf", "0C23A58DACE997E7281C59059BE3C7A806167B431C5FAAC5FB5867FB36444108")]
        [InlineData("BthPS3/BthPS3_PDO_NULL_Device.inf", "6EA2E93A1C272CF6485DB589C134ABA534D6977B7AF8528F717A175B4CD3E94D")]
        [InlineData("BthPS3/bthps3.cat", "151BC8D11E28AEA571DA2FEE3C309BCE7F771460EBB279E586CA7D8CBD91D9B4")]
        [InlineData("BthPS3/bthps3_pdo_null_device.cat", "D0734867EA06A24C33182CE73025E70CEF3447DD2BB287A4E7C4E7EE439E6028")]
        [InlineData("BthPS3/x64/BthPS3.sys", "D5B4104A3640BAAE6A5F3E96410495A25E3A0C71A84141F19728772AEA0EF7E1")]
        [InlineData("BthPS3/ARM64/BthPS3.sys", "27966B6D2E743EF326B4424142C9D87D95AACC9B46B0A2DE837D896936833BB1")]
        [InlineData("BthPS3PSM/BthPS3PSM.inf", "9DEC305929591CD0100C31080E4AE956FAFAA31193D9042B7D2F1EC53A2BD0B4")]
        [InlineData("BthPS3PSM/bthps3psm.cat", "6715B811D7404306471A32E7D4046A9F30C647B77A97D9E7CEF8108F8896B718")]
        [InlineData("BthPS3PSM/x64/BthPS3PSM.sys", "6C6A1862455CE5992394CA2D4E621B0CC4531A6BF4B63BB121C8264DD67F7507")]
        [InlineData("BthPS3PSM/ARM64/BthPS3PSM.sys", "742A3E0DD687BECB12A6716D67903914ECBAE11F521D1BDB3184EE2422EF83AC")]
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

        private static string Root()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }
    }
}
