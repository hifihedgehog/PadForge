using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Reads a driver catalog back through wintrust.dll, element by element,
    /// and checks the catalogs <see cref="DriverPackageCatalog"/> writes.
    /// Nothing is signed with a certificate of this PC and nothing is
    /// installed: a catalog is a file in a folder of the test's own.
    /// </summary>
    internal static class DriverCatalogReader
    {
        internal sealed record Attribute(string Tag, uint Flags, string Value);

        internal sealed record Member(string Tag, Guid Subject, uint CertVersion, string IndirectData,
            IReadOnlyList<Attribute> Attributes);

        internal sealed record Catalog(uint Version, IReadOnlyList<Attribute> Attributes, IReadOnlyList<Member> Members)
        {
            /// <summary>One line per element, in the order the catalog lists
            /// them.</summary>
            public IReadOnlyList<string> Lines()
            {
                var lines = new List<string> { $"store version=0x{Version:X}" };
                foreach (var a in Attributes)
                    lines.Add($"catattr {a.Tag} flags=0x{a.Flags:X8} value={a.Value}");
                foreach (var m in Members)
                {
                    lines.Add($"member {m.Tag} subject={m.Subject} certVersion=0x{m.CertVersion:X} indirect={m.IndirectData}");
                    foreach (var a in m.Attributes)
                        lines.Add($"  attr {a.Tag} flags=0x{a.Flags:X8} value={a.Value}");
                }
                return lines;
            }
        }

        /// <summary>The subject type of a file a catalog lists by its plain
        /// digest, an INF among them (CryptSIPRetrieveSubjectGuidForCatalogFile).</summary>
        internal static readonly Guid FlatFileSubject = new("DE351A42-8E59-11D0-8C47-00C04FC295EE");

        /// <summary>The encoded SPC_INDIRECT_DATA of a SHA-256 flat-file
        /// member, up to its 32 digest bytes.</summary>
        internal const string Sha256IndirectPrefix =
            "30453010060A2B060104018237020119A20280003031300D060960864801650304020105000420";

        internal static Catalog Read(string path)
        {
            IntPtr cat = CryptCATOpen(path, CRYPTCAT_OPEN_EXISTING, IntPtr.Zero, 0, 0x10001);
            Assert.True(cat != IntPtr.Zero && cat != new IntPtr(-1), "CryptCATOpen failed for " + path);
            try
            {
                var store = Marshal.PtrToStructure<CRYPTCATSTORE>(CryptCATStoreFromHandle(cat));
                var attributes = new List<Attribute>();
                for (IntPtr a = CryptCATEnumerateCatAttr(cat, IntPtr.Zero); a != IntPtr.Zero; a = CryptCATEnumerateCatAttr(cat, a))
                    attributes.Add(ReadAttribute(a));
                var members = new List<Member>();
                for (IntPtr m = CryptCATEnumerateMember(cat, IntPtr.Zero); m != IntPtr.Zero; m = CryptCATEnumerateMember(cat, m))
                {
                    var member = Marshal.PtrToStructure<CRYPTCATMEMBER>(m);
                    var own = new List<Attribute>();
                    for (IntPtr a = CryptCATEnumerateAttr(cat, m, IntPtr.Zero); a != IntPtr.Zero; a = CryptCATEnumerateAttr(cat, m, a))
                        own.Add(ReadAttribute(a));
                    members.Add(new Member(Marshal.PtrToStringUni(member.pwszReferenceTag), member.gSubjectType,
                        member.dwCertVersion, Hex(member.sEncodedIndirectData), own));
                }
                return new Catalog(store.dwPublicVersion, attributes, members);
            }
            finally { CryptCATClose(cat); }
        }

        private static Attribute ReadAttribute(IntPtr a)
        {
            var attribute = Marshal.PtrToStructure<CRYPTCATATTRIBUTE>(a);
            var value = new byte[attribute.cbValue];
            if (value.Length > 0) Marshal.Copy(attribute.pbValue, value, 0, value.Length);
            return new Attribute(Marshal.PtrToStringUni(attribute.pwszReferenceTag), attribute.dwAttrTypeAndAction,
                Encoding.Unicode.GetString(value).TrimEnd('\0'));
        }

        private static string Hex(CRYPT_ATTR_BLOB blob)
        {
            if (blob.cbData == 0) return "";
            var bytes = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, bytes, 0, bytes.Length);
            return Convert.ToHexString(bytes);
        }

        /// <summary>The lines of the catalog one INF alone gets on a machine:
        /// what Inf2Cat wrote for such a package, with the INF's own digests
        /// worked out here.</summary>
        internal static IReadOnlyList<string> ExpectedLines(string infName, byte[] inf, Architecture machine,
            IReadOnlyList<string> hardwareIds)
        {
            string sha1 = Convert.ToHexString(SHA1.HashData(inf));
            string sha256 = Convert.ToHexString(SHA256.HashData(inf));
            var lines = new List<string> { "store version=0x200" };
            for (int i = 0; i < hardwareIds.Count; i++)
                lines.Add($"catattr HWID{i + 1} flags=0x10010001 value={hardwareIds[i]}");
            lines.Add("catattr OS flags=0x10010001 value=" + (machine == Architecture.Arm64 ? "_v100_ARM64_RS3" : "_v100_X64"));
            // A catalog is stored sorted by member tag and reads back from
            // the greatest tag down, Inf2Cat's catalogs as these. The SHA-1
            // member reads back with no encoded data of its own.
            foreach (var (tag, indirect) in new[] { (sha1, ""), (sha256, Sha256IndirectPrefix + sha256) }
                         .OrderByDescending(m => m.Item1, StringComparer.Ordinal))
            {
                lines.Add($"member {tag} subject={FlatFileSubject} certVersion=0x200 indirect={indirect}");
                lines.Add($"  attr File flags=0x10010001 value={infName.ToLowerInvariant()}");
                lines.Add("  attr OSAttr flags=0x10010001 value=2:10.0");
            }
            return lines;
        }

        /// <summary>Writes the package's catalog for each architecture and
        /// checks every element of it.</summary>
        internal static void AssertPackage(string infName, string infText, string catalogName,
            ITestOutputHelper output, params string[] hardwareIds)
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.Tests", "DriverCatalog", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                byte[] inf = Encoding.ASCII.GetBytes(infText);
                File.WriteAllBytes(Path.Combine(root, infName), inf);
                foreach (var machine in new[] { Architecture.X64, Architecture.Arm64 })
                {
                    var written = DriverPackageCatalog.Write(root, machine);
                    string cat = Path.Combine(root, catalogName);
                    Assert.Equal(new[] { cat }, written, StringComparer.OrdinalIgnoreCase);
                    var lines = Read(cat).Lines();
                    output.WriteLine($"{machine}\n{string.Join("\n", lines)}");
                    Assert.Equal(ExpectedLines(infName, inf, machine, hardwareIds), lines);
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        // ── WinVerifyTrust, for the signature ────────────────────────────

        internal const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
        internal const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);

        /// <summary>Windows' verdict on a file's Authenticode signature,
        /// with no window and no revocation check.</summary>
        internal static int Verify(string path)
        {
            var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(file, pFile, false);
            try
            {
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwProvFlags = 0x10,      // WTD_REVOCATION_CHECK_NONE
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }

        // ── Interop ──────────────────────────────────────────────────────

        private const uint CRYPTCAT_OPEN_EXISTING = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPTCATSTORE
        {
            public uint cbStruct;
            public uint dwPublicVersion;
            public IntPtr pwszP7File;
            public IntPtr hProv;
            public uint dwEncodingType;
            public uint fdwStoreFlags;
            public IntPtr hReserved;
            public IntPtr hAttrs;
            public IntPtr hCryptMsg;
            public IntPtr hSorted;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPTCATATTRIBUTE
        {
            public uint cbStruct;
            public IntPtr pwszReferenceTag;
            public uint dwAttrTypeAndAction;
            public uint cbValue;
            public IntPtr pbValue;
            public uint dwReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_ATTR_BLOB
        {
            public uint cbData;
            public IntPtr pbData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPTCATMEMBER
        {
            public uint cbStruct;
            public IntPtr pwszReferenceTag;
            public IntPtr pwszFileName;
            public Guid gSubjectType;
            public uint fdwMemberFlags;
            public IntPtr pIndirectData;
            public uint dwCertVersion;
            public uint dwReserved;
            public IntPtr hReserved;
            public CRYPT_ATTR_BLOB sEncodedIndirectData;
            public CRYPT_ATTR_BLOB sEncodedMemberInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CryptCATOpen(string pwszFileName, uint fdwOpenFlags, IntPtr hProv, uint dwPublicVersion, uint dwEncodingType);

        [DllImport("wintrust.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATClose(IntPtr hCatalog);

        [DllImport("wintrust.dll")]
        private static extern IntPtr CryptCATStoreFromHandle(IntPtr hCatalog);

        [DllImport("wintrust.dll")]
        private static extern IntPtr CryptCATEnumerateCatAttr(IntPtr hCatalog, IntPtr pPrevAttr);

        [DllImport("wintrust.dll")]
        private static extern IntPtr CryptCATEnumerateMember(IntPtr hCatalog, IntPtr pPrevMember);

        [DllImport("wintrust.dll")]
        private static extern IntPtr CryptCATEnumerateAttr(IntPtr hCatalog, IntPtr pCatMember, IntPtr pPrevAttr);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
    }
}
