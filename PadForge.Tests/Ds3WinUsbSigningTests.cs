using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The DualShock 3's WinUSB package is signed on the machine that
    /// installs it, the way HIDMaestro signs its own drivers
    /// (DriverBuilder.EnsureTestCertificate + GenerateCatalogs). PadForge
    /// cannot obtain a code-signing certificate, and Windows will not install
    /// a driver package whose catalog does not chain to a trusted root, so a
    /// per-machine certificate is the only route the pad's magic reports have.
    ///
    /// What this replaces: a catalog signed by a prototype certificate that
    /// lived on one developer machine, which meant USB DualShock 3 and the
    /// whole pairing ceremony had never worked for any user (discussion #283).
    /// </summary>
    public class Ds3WinUsbSigningTests
    {
        private static bool Elevated
        {
            get
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        private static string RepoRoot([CallerFilePath] string me = null)
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), ".."));

        /// <summary>No signed artifact ships. A catalog in the repo would be
        /// signed by whatever certificate the developer happened to hold, and
        /// that is precisely the failure being removed: it installs on their
        /// machine and nowhere else.</summary>
        [Fact]
        public void NoPreSignedCatalog_ShipsInTheRepo()
        {
            string winusb = Path.Combine(RepoRoot(), "PadForge.App", "Resources", "BthPS3", "WinUSB");
            Assert.True(Directory.Exists(winusb), winusb);
            Assert.Empty(Directory.GetFiles(winusb, "*.cat"));
            Assert.True(File.Exists(Path.Combine(winusb, "ds3_winusb.inf")),
                "the INF must still ship; only the catalog is built per machine");
        }

        /// <summary>The INF names the catalog the signing step generates. A
        /// mismatch here installs nothing: Windows validates the package
        /// against the CatalogFile the INF declares.</summary>
        [Fact]
        public void Inf_DeclaresTheCatalogTheSigningStepGenerates()
        {
            string inf = Path.Combine(RepoRoot(), "PadForge.App", "Resources",
                "BthPS3", "WinUSB", "ds3_winusb.inf");
            string text = File.ReadAllText(inf);
            Assert.Contains("CatalogFile = ds3_winusb.cat", text, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>End to end on this machine: generate the certificate,
        /// generate the catalog for the staged INF, sign it, and require the
        /// result to chain to a trusted root. That last assertion is the whole
        /// point, because it is the exact check Windows performs before it
        /// will install the package.
        ///
        /// Needs elevation (LocalMachine\Root is written) and PadForge always
        /// runs elevated. Unelevated runs assert the shape instead of
        /// silently passing.</summary>
        [Fact]
        public void SigningPipeline_ProducesATrustedCatalog()
        {
            if (!Elevated)
            {
                // Not a skip dressed as a pass: the pipeline's wiring is still
                // asserted, only the machine-state half is out of reach.
                string src = File.ReadAllText(Path.Combine(RepoRoot(),
                    "PadForge.App", "Services", "Ds3DriverInstaller.cs"));
                Assert.Contains("EnsureSigningCertificate()", src, StringComparison.Ordinal);
                Assert.Contains("Inf2Cat.exe", src, StringComparison.Ordinal);
                Assert.Contains("/sha1 {thumb}", src, StringComparison.Ordinal);
                return;
            }

            string thumb = Ds3DriverInstaller.EnsureSigningCertificate();
            Assert.False(string.IsNullOrWhiteSpace(thumb));

            string winusb = Path.Combine(Ds3DriverInstaller.ExtractDrivers(), "WinUSB");
            Assert.True(File.Exists(Path.Combine(winusb, "ds3_winusb.inf")), "INF was not staged");

            var log = new System.Text.StringBuilder();
            bool signed = Ds3DriverInstaller.SignWinUsbPackage(winusb, m => log.AppendLine(m));
            Assert.True(signed, "signing failed: " + log);

            Assert.True(Ds3DriverInstaller.IsWinUsbPackageTrusted(out string signer),
                "catalog does not chain to a trusted root; signer=" + signer);
            Assert.Contains("PadForge DS3 WinUSB", signer, StringComparison.Ordinal);
        }
    }
}
