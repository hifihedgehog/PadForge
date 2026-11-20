using System;
using System.IO;
using System.Runtime.CompilerServices;
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
        private readonly Xunit.Abstractions.ITestOutputHelper _output;
        public Ds3WinUsbSigningTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

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

        /// <summary>The shipped INF catalogs for both architectures, with
        /// both of its devices and the INF as the catalog's one file. Without
        /// an ARM64 catalog a docked DualShock 3 or Navigation controller
        /// could not leave HidUsb on ARM64 Windows.</summary>
        [Fact]
        public void Inf_CatalogsOnBothArchitectures()
        {
            string inf = Path.Combine(RepoRoot(), "PadForge.App", "Resources",
                "BthPS3", "WinUSB", "ds3_winusb.inf");
            DriverCatalogReader.AssertPackage("ds3_winusb.inf", File.ReadAllText(inf), "ds3_winusb.cat", _output,
                @"usb\vid_054c&pid_0268", @"usb\vid_054c&pid_042f");
        }

        /// <summary>The pipeline's wiring: the certificate is made or found,
        /// the catalog written and the signature made with that certificate,
        /// found again by its thumbprint. The pipeline itself is not run
        /// here. It adds a certificate to this PC's stores,
        /// LocalMachine\Root among them, which the test host does to no PC
        /// (Ds3DriverOffLimits). It ran in a test host with administrator
        /// rights before, and wrote that certificate on the PC the tests ran
        /// on. DriverPackageCatalogTests runs the catalog and the signature
        /// themselves, on a folder and a certificate of its own.</summary>
        [Fact]
        public void SigningPipeline_IsWiredFromCertificateToSignature()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3DriverInstaller.cs"));
            Assert.Contains("string thumb = EnsureSigningCertificate();", src, StringComparison.Ordinal);
            Assert.Contains("DriverPackageCatalog.Write(dir, RuntimeInformation.OSArchitecture)", src, StringComparison.Ordinal);
            Assert.Contains("using (var certificate = SigningCertificate(thumb))", src, StringComparison.Ordinal);
            Assert.Contains("DriverPackageCatalog.Sign(cat, certificate)", src, StringComparison.Ordinal);
            // The success path speaks. A log that only reports failures cannot
            // tell "it worked" from "it never ran", which is the state that
            // made #283 undiagnosable in the first place.
            Assert.Contains("signed with this PC's certificate", src, StringComparison.Ordinal);
        }

        /// <summary>Signing REGENERATES rather than trusting a catalog an
        /// earlier run left behind. A stale catalog is validly signed and
        /// still chains, so a presence check would skip regeneration after
        /// the INF changed and hand Windows a package whose hashes no longer
        /// cover it: the shipped-catalog bug one layer in. Read from the
        /// source, since the test host signs nothing
        /// (Ds3DriverOffLimits).</summary>
        [Fact]
        public void SigningRegenerates_EvenWhenATrustedCatalogAlreadyExists()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3DriverInstaller.cs"));
            int at = src.IndexOf("public static bool EnsureWinUsbBound", StringComparison.Ordinal);
            Assert.True(at > 0);
            // Semantic window (method start through the install call),
            // not a fixed length: a fixed 1600-char slice broke the
            // moment the bind grew its live-node preamble (#285).
            int end = src.IndexOf("InstallInf(infPath", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string body = src.Substring(at, end - at);
            // The bind must not gate signing on the catalog already being
            // trusted. That is the short-circuit being removed.
            Assert.DoesNotContain("!IsWinUsbPackageTrusted(out _) &&", body, StringComparison.Ordinal);
            Assert.Contains("if (!SignWinUsbPackage(winusb, log))", body, StringComparison.Ordinal);
            // Every earlier catalog is deleted before the new one is made.
            int sign = src.IndexOf("internal static bool SignDriverPackage", StringComparison.Ordinal);
            int delete = src.IndexOf("foreach (string stale in Directory.GetFiles(dir, \"*.cat\"))", sign, StringComparison.Ordinal);
            int generate = src.IndexOf("DriverPackageCatalog.Write(dir,", sign, StringComparison.Ordinal);
            Assert.True(sign > 0 && delete > sign && generate > delete, "the stale catalog is not deleted before the new one is generated");
        }

        /// <summary>Wiring the audit round locked, where no seam exists to
        /// call: the program runner (BthPS3's uninstaller goes through it)
        /// drains asynchronously and kills a timed-out child, signing is
        /// serialized against its two concurrent callers,
        /// and the pairing dialog reports the step that actually failed
        /// rather than blaming the certificate for all of them.</summary>
        [Fact]
        public void SigningPipeline_KeepsItsWiringContracts()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3DriverInstaller.cs"));
            int at = src.IndexOf("private static (int Code, string Output) RunTool", StringComparison.Ordinal);
            Assert.True(at > 0);
            string run = src.Substring(at, 1400);
            // Async drain: a synchronous ReadToEnd before WaitForExit
            // deadlocks when the child fills the other pipe.
            Assert.Contains("BeginOutputReadLine", run, StringComparison.Ordinal);
            Assert.Contains("BeginErrorReadLine", run, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadToEnd", run, StringComparison.Ordinal);
            // A timed-out child is killed, not orphaned holding the catalog.
            Assert.Contains("Kill(entireProcessTree: true)", run, StringComparison.Ordinal);

            Assert.Contains("lock (_signLock)", src, StringComparison.Ordinal);
            // Success is logged, not only failure. Asserted here as well as in
            // the elevated test so an unelevated run still locks it.
            Assert.Contains("signed with this PC's certificate", src, StringComparison.Ordinal);
            // Both certificate handles are released; CreateFromSignedFile
            // returns its own X509Certificate and this runs twice per bind.
            Assert.Contains("using var signed = X509Certificate2.CreateFromSignedFile", src,
                StringComparison.Ordinal);

            string pair = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3PairingService.cs"));
            Assert.Contains("Ds3DriverInstaller.LastWinUsbFailure", pair, StringComparison.Ordinal);
        }

        /// <summary>The #285 bind contract, source-anchored like the guards
        /// above. The old shape failed a reporter on two machines with two
        /// genuine pads: "an interface with our GUID exists" passed as
        /// "already bound" while the live pad rode HidUsb (the DS3 has no
        /// USB serial, so every port is its own devnode and a stale
        /// registration keeps the GUID present), and the non-forced
        /// DiInstallDriver lost the ranking contest to inbox WHQL HidUsb,
        /// so the pad could never leave it. The bind must decide on the
        /// LIVE nodes' bound service, force-apply the driver, and treat
        /// only an ACTIVE interface as proof.</summary>
        [Fact]
        public void WinUsbBind_DecidesOnLiveNodesAndForcesTheBind()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3DriverInstaller.cs"));

            // The naked GUID-presence fast path is gone: success requires
            // every live DS3 node on WINUSB plus an ACTIVE interface.
            Assert.Contains("ListDs3UsbNodes()", src, StringComparison.Ordinal);
            Assert.Contains("HasActiveDs3WinUsbInterface()", src, StringComparison.Ordinal);
            Assert.Contains("SPINT_ACTIVE", src, StringComparison.Ordinal);

            // The bind is forced and targeted, not left to driver ranking,
            // and it targets ONE pad per call. The package covers the DS3
            // and the Navigation controller, both of which hide their
            // sixpair reports from the HID descriptor; binding both at
            // once would drag whichever pad is merely plugged in off the
            // inbox driver as a side effect of the other one's ceremony.
            Assert.Contains(@"string hwid = @""USB\VID_054C&"" + pidToken;",
                src, StringComparison.Ordinal);
            Assert.Contains("UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hwid,",
                src, StringComparison.Ordinal);
            Assert.Contains(@"Ds3PidToken = ""PID_0268""", src, StringComparison.Ordinal);
            Assert.Contains(@"NavPidToken = ""PID_042F""", src, StringComparison.Ordinal);
            Assert.Contains("INSTALLFLAG_FORCE", src, StringComparison.Ordinal);

            // Coexistence survives the force: foreign function drivers
            // (DsHidMini's WUDFRd) are never stolen.
            Assert.Contains("not rebinding it", src, StringComparison.Ordinal);

            // Both interface-path consumers skip stale registrations.
            string pair = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "Ds3PairingService.cs"));
            Assert.Contains("did.Flags & SPINT_ACTIVE", pair, StringComparison.Ordinal);
            string direct = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "Ds3DirectService.cs"));
            Assert.Contains("did.Flags & SPINT_ACTIVE", direct, StringComparison.Ordinal);

            // The ceremony's failure telemetry splits dead-handle from
            // device-refusal through the same handle, so the next report
            // like #285 is diagnosable from its DIAG alone.
            Assert.Contains("WinUsb_GetDescriptor", pair, StringComparison.Ordinal);
            Assert.Contains("DS3 WinUSB interface: ", pair, StringComparison.Ordinal);
        }

        /// <summary>Every certificate a store hands out is a handle of its
        /// own. The lookup that finds the signing certificate closes each
        /// one it is handed, the one it picks among them, and returns the
        /// thumbprint: a certificate good for at least 30 more days, with
        /// its private key.</summary>
        [Fact]
        public void TheSigningLookup_ClosesEveryCertificateItIsHanded()
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            System.Security.Cryptography.X509Certificates.X509Certificate2 Make(int days)
                => new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=PadForge DS3 WinUSB", rsa,
                        System.Security.Cryptography.HashAlgorithmName.SHA256,
                        System.Security.Cryptography.RSASignaturePadding.Pkcs1)
                    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));
            var soon = Make(10);
            var good = Make(3650);
            var noKey = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(good.RawData);
            string expected = good.Thumbprint;
            Assert.True(good.HasPrivateKey);
            Assert.False(noKey.HasPrivateKey);

            var found = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection { soon, noKey, good };
            Assert.Equal(expected, Ds3DriverInstaller.UsableSigningThumbprint(found, DateTime.Now));
            Assert.All(new[] { soon, noKey, good }, c => Assert.Equal(IntPtr.Zero, c.Handle));

            var none = Make(10);
            Assert.Null(Ds3DriverInstaller.UsableSigningThumbprint(
                new System.Security.Cryptography.X509Certificates.X509Certificate2Collection { none }, DateTime.Now));
            Assert.Equal(IntPtr.Zero, none.Handle);
        }
    }
}
