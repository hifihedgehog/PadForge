using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The catalog and the signature PadForge makes for its own driver
    /// packages, in its own process (<see cref="DriverPackageCatalog"/>).
    /// Through HIDMaestro 2.2.0 the signing step ran the Inf2Cat.exe and
    /// signtool.exe HIDMaestro staged. HIDMaestro 2.3.0 carries neither, and
    /// with them every DualShock 3 and vendor USB driver setup would have
    /// stopped at "Driver signing tools are unavailable".
    ///
    /// <para>On 2026-11-19 the Inf2Cat of HIDMaestro 2.2.0 (10.0.26100) and
    /// this writer were run on the same files: the shipped DualShock 3 INF,
    /// both INFs VendorUsbDriverInstaller builds, a revision package and the
    /// package below, each for x64 and ARM64. Every catalog pair read back
    /// with the same elements. That release's signtool and
    /// <see cref="DriverPackageCatalog.Sign"/> then signed three of those
    /// catalogs with one certificate, and the signed files were the same,
    /// byte for byte. Windows added a package signed this way to its driver
    /// store and refused the same package unsigned.</para>
    /// </summary>
    public class DriverPackageCatalogTests
    {
        private readonly ITestOutputHelper _output;
        public DriverPackageCatalogTests(ITestOutputHelper output) => _output = output;

        private static string RepoRoot([CallerFilePath] string me = null)
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), ".."));

        private static string NewFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.Tests", "DriverCatalog", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void Remove(string root)
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        /// <summary>The package Inf2Cat's own catalog is held for, below. Its
        /// bytes are fixed here, so its digests are too: 1,098 bytes of
        /// ASCII with CRLF line ends.</summary>
        private static readonly string[] FrozenInf =
        {
            @"; Frozen_Package.inf, the package DriverPackageCatalogTests holds Inf2Cat's catalog for.",
            @"[Version]",
            @"Signature   = ""$Windows NT$""",
            @"Class       = USBDevice",
            @"ClassGUID   = {88BAE032-5A81-49F0-BC3D-A4FF138216D6}",
            @"Provider    = %ProviderName%",
            @"CatalogFile = Frozen_Package.cat",
            @"DriverVer   = 09/20/2026,1.1.0.0",
            @"PnpLockdown = 1",
            @"",
            @"[Manufacturer]",
            @"%ProviderName% = Standard,NTamd64,NTarm64",
            @"",
            @"[Standard.NTamd64]",
            @"%DeviceName%    = USB_Install, USB\VID_054C&PID_0268",
            @"%NavDeviceName% = USB_Install, USB\VID_054C&PID_042F, USB\VID_054C&PID_042F&MI_00 ; two IDs on one line",
            @"",
            @"[Standard.NTarm64]",
            @"%DeviceName%    = USB_Install, USB\VID_054C&PID_0268",
            @"%NavDeviceName% = USB_Install, USB\VID_054C&PID_042F",
            @"",
            @"[USB_Install]",
            @"Include = winusb.inf",
            @"Needs   = WINUSB.NT",
            @"",
            @"[USB_Install.Services]",
            @"Include = winusb.inf",
            @"Needs   = WINUSB.NT.Services",
            @"",
            @"[USB_Install.HW]",
            @"AddReg = Dev_AddReg",
            @"",
            @"[Dev_AddReg]",
            @"HKR,,DeviceInterfaceGUIDs,0x10000,""{B35924D6-3E16-4A9E-9782-5524A4B79BAC}""",
            @"",
            @"[Strings]",
            @"ProviderName = ""PadForge""",
            @"DeviceName    = ""DualShock 3 (WinUSB)""",
            @"NavDeviceName = ""Navigation Controller (WinUSB)""",
        };

        private const string FrozenSha1 = "C3D46EFEA588131D9EBC05BD0D951D82B9B29403";
        private const string FrozenSha256 = "120162C7CD51C28FEDAE59F013C09849C77567F0D366FBC7A1922C892A0C9E20";

        private static byte[] FrozenBytes() => Encoding.ASCII.GetBytes(string.Join("\r\n", FrozenInf) + "\r\n");

        /// <summary>Every element of the catalog Inf2Cat wrote for the frozen
        /// package with /os:10_X64 and /os:10_RS3_ARM64, as read back on
        /// 2026-11-19. The two catalogs differ in the OS attribute alone. A
        /// hardware ID is listed once, in lowercase, in the order the INF
        /// gives it, the comment after one left out. The catalog is named in
        /// lowercase, and so is the INF in its File attribute.</summary>
        [Theory]
        [InlineData(Architecture.X64, "_v100_X64")]
        [InlineData(Architecture.Arm64, "_v100_ARM64_RS3")]
        public void TheCatalog_IsTheOneInf2CatWroteForTheSamePackage(Architecture machine, string os)
        {
            string root = NewFolder();
            try
            {
                byte[] inf = FrozenBytes();
                Assert.Equal(1098, inf.Length);
                Assert.Equal(FrozenSha1, Convert.ToHexString(SHA1.HashData(inf)));
                Assert.Equal(FrozenSha256, Convert.ToHexString(SHA256.HashData(inf)));
                File.WriteAllBytes(Path.Combine(root, "Frozen_Package.inf"), inf);

                var written = DriverPackageCatalog.Write(root, machine);
                Assert.Single(written);
                Assert.Equal("frozen_package.cat", Path.GetFileName(written[0]));

                var lines = DriverCatalogReader.Read(written[0]).Lines();
                _output.WriteLine(string.Join("\n", lines));
                Assert.Equal(new[]
                {
                    "store version=0x200",
                    @"catattr HWID1 flags=0x10010001 value=usb\vid_054c&pid_0268",
                    @"catattr HWID2 flags=0x10010001 value=usb\vid_054c&pid_042f",
                    @"catattr HWID3 flags=0x10010001 value=usb\vid_054c&pid_042f&mi_00",
                    "catattr OS flags=0x10010001 value=" + os,
                    "member " + FrozenSha1 + " subject=de351a42-8e59-11d0-8c47-00c04fc295ee certVersion=0x200 indirect=",
                    "  attr File flags=0x10010001 value=frozen_package.inf",
                    "  attr OSAttr flags=0x10010001 value=2:10.0",
                    "member " + FrozenSha256 + " subject=de351a42-8e59-11d0-8c47-00c04fc295ee certVersion=0x200 indirect="
                        + "30453010060A2B060104018237020119A20280003031300D060960864801650304020105000420" + FrozenSha256,
                    "  attr File flags=0x10010001 value=frozen_package.inf",
                    "  attr OSAttr flags=0x10010001 value=2:10.0",
                }, lines);
                // The helper the package tests use expects the same catalog.
                Assert.Equal(lines, DriverCatalogReader.ExpectedLines("Frozen_Package.inf", inf, machine,
                    new[] { @"usb\vid_054c&pid_0268", @"usb\vid_054c&pid_042f", @"usb\vid_054c&pid_042f&mi_00" }));
            }
            finally { Remove(root); }
        }

        /// <summary>A catalog left by an earlier run is replaced, never
        /// added to: the second write, for the other architecture, holds that
        /// architecture's OS and the same two members.</summary>
        [Fact]
        public void AWrite_ReplacesTheCatalogThatWasThere()
        {
            string root = NewFolder();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "Frozen_Package.inf"), FrozenBytes());
                string cat = DriverPackageCatalog.Write(root, Architecture.X64).Single();
                DriverPackageCatalog.Write(root, Architecture.Arm64);
                var catalog = DriverCatalogReader.Read(cat);
                Assert.Equal("_v100_ARM64_RS3", catalog.Attributes.Single(a => a.Tag == "OS").Value);
                Assert.Equal(2, catalog.Members.Count);
                Assert.Single(Directory.GetFiles(root, "*.cat"));
            }
            finally { Remove(root); }
        }

        private static string With(string find, params string[] replacement)
        {
            Assert.Contains(find, FrozenInf);
            return string.Join("\r\n", FrozenInf.SelectMany(l => l == find ? replacement : new[] { l })) + "\r\n";
        }

        /// <summary>A file an INF copies would have to be a member of its
        /// catalog, and this writer makes the INF the only member. PadForge's
        /// packages copy nothing: each takes its driver from an INF of
        /// Windows' own. One that copies a file is refused here, where a
        /// catalog without that file would be refused by Windows at the
        /// install.</summary>
        [Fact]
        public void AnInfThatCopiesFiles_IsRefused()
        {
            string root = NewFolder();
            try
            {
                File.WriteAllText(Path.Combine(root, "copies.inf"),
                    With("Needs   = WINUSB.NT", "Needs   = WINUSB.NT", "CopyFiles = Driver_Files"), Encoding.ASCII);
                var ex = Assert.Throws<InvalidOperationException>(() => DriverPackageCatalog.Write(root, Architecture.X64));
                Assert.Contains("copies files", ex.Message, StringComparison.Ordinal);
                Assert.Empty(Directory.GetFiles(root, "*.cat"));
            }
            finally { Remove(root); }
        }

        /// <summary>Inf2Cat wrote no catalog for an INF whose [Manufacturer]
        /// names no models section for the architecture. Without the refusal
        /// an x64-only INF would get an ARM64 catalog for a driver it does
        /// not install there.</summary>
        [Fact]
        public void AnInfWithNoModelsForTheArchitecture_IsRefused()
        {
            string root = NewFolder();
            try
            {
                File.WriteAllText(Path.Combine(root, "x64only.inf"),
                    With("%ProviderName% = Standard,NTamd64,NTarm64", "%ProviderName% = Standard,NTamd64"), Encoding.ASCII);
                var ex = Assert.Throws<InvalidOperationException>(() => DriverPackageCatalog.Write(root, Architecture.Arm64));
                Assert.Contains("NTARM64", ex.Message, StringComparison.Ordinal);
                Assert.Empty(Directory.GetFiles(root, "*.cat"));
                // The same INF catalogs for the architecture it names.
                Assert.Single(DriverPackageCatalog.Write(root, Architecture.X64));
            }
            finally { Remove(root); }
        }

        [Fact]
        public void APackageWithAnInfBelowItsTopFolder_IsRefused()
        {
            string root = NewFolder();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "Frozen_Package.inf"), FrozenBytes());
                Directory.CreateDirectory(Path.Combine(root, "below"));
                File.WriteAllBytes(Path.Combine(root, "below", "other.inf"), FrozenBytes());
                Assert.Throws<InvalidOperationException>(() => DriverPackageCatalog.Write(root, Architecture.X64));
                Assert.Empty(Directory.GetFiles(root, "*.cat"));
            }
            finally { Remove(root); }
        }

        /// <summary>The signature, made with a certificate that exists in
        /// this process alone: no store of this PC holds it, before or after.
        /// Windows finds no signature before and a whole one after, which it
        /// stops at only for the root no PC trusts. A signature that did not
        /// cover the catalog would read as a bad digest instead. The signer
        /// is the certificate handed in, and signing leaves every element of
        /// the catalog as it was.</summary>
        [Fact]
        public void TheSignature_IsOneWindowsVerifiesUpToItsUntrustedRoot()
        {
            string root = NewFolder();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "Frozen_Package.inf"), FrozenBytes());
                string cat = DriverPackageCatalog.Write(root, Architecture.X64).Single();
                var before = DriverCatalogReader.Read(cat).Lines();
                Assert.Equal(DriverCatalogReader.TRUST_E_NOSIGNATURE, DriverCatalogReader.Verify(cat));

                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=PadForge test signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));

                DriverPackageCatalog.Sign(cat, certificate);

                Assert.Equal(DriverCatalogReader.CERT_E_UNTRUSTEDROOT, DriverCatalogReader.Verify(cat));
#pragma warning disable SYSLIB0057
                using var signer = X509Certificate2.CreateFromSignedFile(cat);
#pragma warning restore SYSLIB0057
                Assert.Equal(certificate.GetCertHashString(), signer.GetCertHashString());
                Assert.Equal(before, DriverCatalogReader.Read(cat).Lines());
                // The check the bind makes of a signed package says no for
                // this one, since nothing on this PC trusts the signer.
                Assert.False(Ds3DriverInstaller.IsCatalogTrusted(cat, out string subject));
                Assert.Equal("CN=PadForge test signer", subject);

                // A second signature replaces the first.
                long length = new FileInfo(cat).Length;
                DriverPackageCatalog.Sign(cat, certificate);
                Assert.Equal(length, new FileInfo(cat).Length);
                Assert.Equal(DriverCatalogReader.CERT_E_UNTRUSTEDROOT, DriverCatalogReader.Verify(cat));
            }
            finally { Remove(root); }
        }

        /// <summary>The signing step is PadForge's own from end to end. It
        /// ran two programs HIDMaestro staged for its driver, reached through
        /// HIDMaestro's Internal namespace, and HIDMaestro 2.3.0 carries
        /// neither.</summary>
        [Fact]
        public void TheSigningStep_BorrowsNoToolFromHIDMaestro()
        {
            foreach (string file in new[] { "Ds3DriverInstaller.cs", "VendorUsbDriverInstaller.cs", "DriverPackageCatalog.cs" })
            {
                string src = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", file));
                Assert.DoesNotContain("HIDMaestro.Internal", src, StringComparison.Ordinal);
                Assert.DoesNotContain("EnsureExtracted", src, StringComparison.Ordinal);
                Assert.DoesNotContain("RunTool(inf2cat", src, StringComparison.Ordinal);
                Assert.DoesNotContain("RunTool(signtool", src, StringComparison.Ordinal);
            }
        }
    }
}
