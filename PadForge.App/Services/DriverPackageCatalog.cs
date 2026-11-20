using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PadForge.Services
{
    /// <summary>
    /// Writes the catalog of a driver package PadForge stages and signs it, in
    /// this process, through the catalog functions of wintrust.dll and
    /// SignerSignEx2 of mssign32.dll.
    ///
    /// <para>Through HIDMaestro 2.2.0 the signing step ran the Inf2Cat.exe and
    /// signtool.exe that HIDMaestro staged for its own driver. HIDMaestro 2.3.0
    /// carries neither tool, and writes its catalogs and signatures with these
    /// same Windows functions (its Internal/DriverCatalog.cs and
    /// Internal/AuthenticodeSigner.cs, which this class follows). Inf2Cat's
    /// catalog library is itself a wrapper over CryptCATOpen,
    /// CryptCATPutCatAttrInfo, CryptCATPutMemberInfo, CryptCATPutAttrInfo and
    /// CryptCATPersistStore, with each member's indirect data from
    /// CryptSIPCreateIndirectData. For PadForge's three packages the catalog
    /// written here equaled the one Inf2Cat 10.0.26100 wrote, entry for entry,
    /// when both were run on the same files on 2026-11-19.
    /// DriverPackageCatalogTests holds what that tool wrote for one frozen
    /// test package and checks this writer against it.</para>
    ///
    /// <list type="bullet">
    /// <item>One catalog per name an INF's CatalogFile entry gives
    /// (CatalogFile.NTAMD64 or CatalogFile.NTARM64, then CatalogFile.NT, then
    /// CatalogFile), written beside the INF, its name in lowercase.</item>
    /// <item>Catalog version 2, so the member list algorithm is
    /// 1.3.6.1.4.1.311.12.1.3.</item>
    /// <item>Its members: each INF, twice, as a SHA-1 member and a SHA-256
    /// member, each tagged with its digest in uppercase hex, each with the
    /// attributes File (the INF's name in lowercase) and OSAttr ("2:10.0").</item>
    /// <item>Its attributes: HWID1, HWID2 and on, the hardware IDs of every
    /// models section of every driver INF in the folder, in lowercase, in the
    /// order the INFs and their lines come, each once, then OS: "_v100_X64"
    /// for x64 and "_v100_ARM64_RS3" for ARM64.</item>
    /// <item>Every attribute authenticated, its name and value text
    /// (0x10010001).</item>
    /// </list>
    ///
    /// <para>PadForge's packages are an INF alone: each takes its driver from
    /// an INF Windows ships (Include and Needs of winusb.inf or xusb22.inf)
    /// and copies no file. An INF with a CopyFiles directive is refused, since
    /// the files it copies would have to be members and this class reads
    /// none. As in Inf2Cat, an INF with no models section decorated for the
    /// machine's architecture is refused, and so is a package with an INF
    /// below its top folder.</para>
    ///
    /// <para>Inf2Cat also refused an INF that failed its signability tests,
    /// one whose models line names a USB class ID with no VID and PID, for
    /// one. This class makes no such test. PadForge's INFs passed them when
    /// the tool last ran on them, on 2026-11-19.</para>
    /// </summary>
    internal static unsafe class DriverPackageCatalog
    {
        /// <summary>The catalog attribute OS for the machine, as Inf2Cat wrote
        /// it for /os:10_X64 and /os:10_RS3_ARM64. The catalog names the OS it
        /// is for, and that is the machine doing the install, not the
        /// architecture this process was built for: an x64 PadForge running
        /// emulated on ARM64 Windows still installs an ARM64 driver, and
        /// RuntimeInformation.OSArchitecture reports the real machine under
        /// emulation.</summary>
        internal static string OsAttribute(Architecture machine)
            => machine == Architecture.Arm64 ? "_v100_ARM64_RS3" : "_v100_X64";

        /// <summary>The member attribute OSAttr, the same on both
        /// architectures.</summary>
        internal const string MemberOsAttr = "2:10.0";

        // mscat.h: CRYPTCAT_ATTR_AUTHENTICATED | CRYPTCAT_ATTR_DATAASCII |
        // CRYPTCAT_ATTR_NAMEASCII, Inf2Cat's attribute type.
        internal const uint AttributeFlags = 0x10010001;

        // mscat.h
        private const uint CRYPTCAT_OPEN_CREATENEW = 0x1, CRYPTCAT_VERSION_2 = 0x200;
        // wincrypt.h: PKCS_7_ASN_ENCODING | X509_ASN_ENCODING
        private const uint Encoding = 0x10001;
        // mssip.h: SPC_EXC_PE_PAGE_HASHES_FLAG, SPC_INC_PE_IMPORT_ADDR_TABLE_FLAG,
        // SPC_INC_PE_RESOURCES_FLAG, MSSIP_FLAGS_PROHIBIT_RESIZE_ON_CREATE: the
        // flags Inf2Cat sets for a member without page hashes.
        private const uint SipFlags = 0x10 | 0x20 | 0x80 | 0x10000;

        internal const string Sha1Oid = "1.3.14.3.2.26", Sha256Oid = "2.16.840.1.101.3.4.2.1";

        /// <summary>One catalog to write: its path, its hardware IDs and OS,
        /// and its members, each a file and the name its File attribute
        /// carries.</summary>
        internal sealed record CatalogPlan(string Path, IReadOnlyList<string> HardwareIds, string Os,
            IReadOnlyList<(string File, string Name)> Members);

        /// <summary>Writes every catalog the driver INFs in
        /// <paramref name="packageDir"/> name, for <paramref name="machine"/>,
        /// and returns their paths. A catalog of the same name is
        /// replaced.</summary>
        /// <exception cref="InvalidOperationException">An INF copies files,
        /// has no models section for the architecture, or a catalog function
        /// failed.</exception>
        internal static IReadOnlyList<string> Write(string packageDir, Architecture machine)
        {
            var plan = Plan(packageDir, machine);
            var written = new List<string>(plan.Count);
            foreach (var cat in plan)
            {
                WriteOne(cat);
                written.Add(cat.Path);
            }
            return written;
        }

        /// <summary>What <see cref="Write"/> writes, read from the INFs, with
        /// no file written.</summary>
        internal static IReadOnlyList<CatalogPlan> Plan(string packageDir, Architecture machine)
        {
            string root = Path.GetFullPath(packageDir);
            if (Directory.EnumerateDirectories(root).Any(d => Directory.EnumerateFiles(d, "*.inf", SearchOption.AllDirectories).Any()))
                throw new InvalidOperationException($"{root} holds an INF below its top folder. PadForge catalogs flat packages only.");

            // Inf2Cat walks the folder in the order Windows lists it, which on
            // NTFS is the order of the names in uppercase.
            var infs = Directory.GetFiles(root, "*.inf")
                .OrderBy(f => Path.GetFileName(f).ToUpperInvariant(), StringComparer.Ordinal)
                .Select(f => (Path: f, Inf: InfFile.Read(f)))
                .Where(x => x.Inf.IsDriverInf)
                .ToList();

            var hwids = new List<string>();
            foreach (var (_, inf) in infs)
                foreach (string id in inf.HardwareIds())
                    if (!hwids.Contains(id, StringComparer.Ordinal)) hwids.Add(id);

            string decoration = machine == Architecture.Arm64 ? "ntarm64" : "ntamd64";
            var catalogs = new List<(string Path, List<(string File, string Name)> Members)>();
            foreach (var (path, inf) in infs)
            {
                string infName = Path.GetFileName(path);
                // Inf2Cat refuses a driver INF whose [Manufacturer] names no
                // models section decorated for the target architecture, and
                // writes no catalog at all.
                if (!inf.HasModelsDecoratedFor(decoration))
                    throw new InvalidOperationException(
                        $"{infName} does not have {decoration.ToUpperInvariant()} decorated model sections, so it has no catalog for {machine}.");
                if (inf.CopiesFiles)
                    throw new InvalidOperationException(
                        $"{infName} copies files. PadForge catalogs packages that are an INF alone.");

                string name = inf.CatalogFileFor(decoration);
                if (string.IsNullOrEmpty(name)) continue;
                string catPath = Path.Combine(root, name.TrimStart('\\'));
                var entry = catalogs.FirstOrDefault(c => c.Path.Equals(catPath, StringComparison.OrdinalIgnoreCase));
                if (entry.Members == null)
                {
                    entry = (catPath, new List<(string, string)>());
                    catalogs.Add(entry);
                }
                if (!entry.Members.Any(m => m.File.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    entry.Members.Add((path, infName.ToLowerInvariant()));
            }

            string os = OsAttribute(machine);
            // Inf2Cat adds the members in the order the folder lists the
            // files. CryptCATPersistStore sorts them by tag either way.
            return catalogs.Select(c => new CatalogPlan(c.Path, hwids, os,
                    c.Members.OrderBy(m => Path.GetFileName(m.File).ToUpperInvariant(), StringComparer.Ordinal).ToList()))
                .ToList();
        }

        private static void WriteOne(CatalogPlan cat)
        {
            if (File.Exists(cat.Path)) File.Delete(cat.Path);
            IntPtr store = CryptCATOpen(cat.Path, CRYPTCAT_OPEN_CREATENEW, IntPtr.Zero, CRYPTCAT_VERSION_2, Encoding);
            if (store == IntPtr.Zero || store == new IntPtr(-1))
                throw Failed("CryptCATOpen", cat.Path);
            try
            {
                for (int i = 0; i < cat.HardwareIds.Count; i++)
                    PutCatAttr(store, "HWID" + (i + 1).ToString(CultureInfo.InvariantCulture), cat.HardwareIds[i], cat.Path);
                PutCatAttr(store, "OS", cat.Os, cat.Path);

                CRYPTCATSTORE* info = CryptCATStoreFromHandle(store);
                if (info == null) throw Failed("CryptCATStoreFromHandle", cat.Path);
                foreach (var (file, name) in cat.Members)
                {
                    Guid subject;
                    if (!CryptSIPRetrieveSubjectGuidForCatalogFile(file, IntPtr.Zero, &subject))
                        throw Failed("CryptSIPRetrieveSubjectGuidForCatalogFile", file);
                    // Inf2Cat adds the SHA-1 member first, then for a version
                    // 2 catalog the SHA-256 one.
                    AddMember(store, info, file, name, subject, Sha1Oid);
                    AddMember(store, info, file, name, subject, Sha256Oid);
                }
                if (!CryptCATPersistStore(store)) throw Failed("CryptCATPersistStore", cat.Path);
            }
            finally
            {
                CryptCATClose(store);
            }
        }

        private static void AddMember(IntPtr store, CRYPTCATSTORE* info, string file, string name, Guid subject, string digestOid)
        {
            byte[] oid = System.Text.Encoding.ASCII.GetBytes(digestOid + "\0");
            fixed (byte* pOid = oid)
            fixed (char* pFile = file)
            {
                var si = new SIP_SUBJECTINFO
                {
                    cbSize = (uint)sizeof(SIP_SUBJECTINFO),
                    pgSubjectType = &subject,
                    hFile = IntPtr.Zero,
                    pwsFileName = pFile,
                    hProv = info->hProv,
                    DigestAlgorithm = new CRYPT_ALGORITHM_IDENTIFIER { pszObjId = pOid },
                    dwFlags = SipFlags,
                    dwEncodingType = info->dwEncodingType,
                };
                uint cb = 0;
                if (!CryptSIPCreateIndirectData(&si, &cb, null) || cb == 0)
                    throw Failed("CryptSIPCreateIndirectData", file);
                byte[] buffer = new byte[cb];
                fixed (byte* pData = buffer)
                {
                    if (!CryptSIPCreateIndirectData(&si, &cb, pData))
                        throw Failed("CryptSIPCreateIndirectData", file);
                    var indirect = (SIP_INDIRECT_DATA*)pData;
                    string tag = Convert.ToHexString(new ReadOnlySpan<byte>(indirect->Digest.pbData, (int)indirect->Digest.cbData));
                    IntPtr member = CryptCATPutMemberInfo(store, file, tag, &subject, si.dwIntVersion, cb, pData);
                    if (member == IntPtr.Zero) throw Failed("CryptCATPutMemberInfo", file);
                    PutAttr(store, member, "File", name, file);
                    PutAttr(store, member, "OSAttr", MemberOsAttr, file);
                }
            }
        }

        private static void PutCatAttr(IntPtr store, string tag, string value, string path)
        {
            fixed (char* p = value + "\0")
                if (CryptCATPutCatAttrInfo(store, tag, AttributeFlags, (uint)((value.Length + 1) * 2), (byte*)p) == IntPtr.Zero)
                    throw Failed("CryptCATPutCatAttrInfo " + tag, path);
        }

        private static void PutAttr(IntPtr store, IntPtr member, string tag, string value, string path)
        {
            fixed (char* p = value + "\0")
                if (CryptCATPutAttrInfo(store, member, tag, AttributeFlags, (uint)((value.Length + 1) * 2), (byte*)p) == IntPtr.Zero)
                    throw Failed("CryptCATPutAttrInfo " + tag, path);
        }

        private static InvalidOperationException Failed(string call, string path)
            => new($"{call} failed for {path} (error 0x{Marshal.GetLastPInvokeError():X8}).");

        // ── The signature ────────────────────────────────────────────────

        // mssign32 structures and constants, from the documentation of
        // SignerSignEx2 and the structures it takes.
        private const uint SIGNER_SUBJECT_FILE = 1, SIGNER_CERT_STORE = 2, SIGNER_CERT_POLICY_CHAIN = 2,
            SIGNER_AUTHCODE_ATTR = 1, CALG_SHA_256 = 0x800C;

        /// <summary>Signs <paramref name="path"/> in place with
        /// <paramref name="certificate"/>, replacing any signature it carries.
        /// The signature signtool made with
        /// <c>sign /sm /s My /sha1 thumbprint /fd SHA256</c>: a SHA-256
        /// digest, RSA PKCS#1 v1.5, the certificate chain in the signature,
        /// the authenticated attributes SpcSpOpusInfo (empty) and
        /// SpcStatementType (individual), no signing time and no
        /// timestamp.</summary>
        /// <exception cref="InvalidOperationException">SignerSignEx2
        /// failed.</exception>
        internal static void Sign(string path, X509Certificate2 certificate)
        {
            uint index = 0;
            fixed (char* file = path)
            {
                var fileInfo = new SIGNER_FILE_INFO { cbSize = (uint)sizeof(SIGNER_FILE_INFO), pwszFileName = file };
                var subject = new SIGNER_SUBJECT_INFO
                {
                    cbSize = (uint)sizeof(SIGNER_SUBJECT_INFO),
                    pdwIndex = &index,
                    dwSubjectChoice = SIGNER_SUBJECT_FILE,
                    pSignerFileInfo = &fileInfo,
                };
                var storeInfo = new SIGNER_CERT_STORE_INFO
                {
                    cbSize = (uint)sizeof(SIGNER_CERT_STORE_INFO),
                    pSigningCert = certificate.Handle,
                    dwCertPolicy = SIGNER_CERT_POLICY_CHAIN,
                };
                var cert = new SIGNER_CERT
                {
                    cbSize = (uint)sizeof(SIGNER_CERT),
                    dwCertChoice = SIGNER_CERT_STORE,
                    pCertStoreInfo = &storeInfo,
                };
                var authcode = new SIGNER_ATTR_AUTHCODE
                {
                    cbSize = (uint)sizeof(SIGNER_ATTR_AUTHCODE),
                    fCommercial = 0,
                    fIndividual = 1,
                };
                var signature = new SIGNER_SIGNATURE_INFO
                {
                    cbSize = (uint)sizeof(SIGNER_SIGNATURE_INFO),
                    algidHash = CALG_SHA_256,
                    dwAttrChoice = SIGNER_AUTHCODE_ATTR,
                    pAttrAuthcode = &authcode,
                };
                SIGNER_CONTEXT* context = null;
                int hr = SignerSignEx2(0, &subject, &cert, &signature, null, 0, null, null, null, null, &context, null, null);
                if (context != null) SignerFreeSignerContext(context);
                GC.KeepAlive(certificate);
                if (hr != 0)
                    throw new InvalidOperationException(
                        $"Signing {path} failed (SignerSignEx2 0x{hr:X8}: {Marshal.GetExceptionForHR(hr)?.Message.TrimEnd('.')}).");
            }
        }

        // ── The INF ──────────────────────────────────────────────────────

        /// <summary>An INF read as Inf2Cat reads it: every name and value in
        /// lowercase, a line's fields after its key (or all of them on a line
        /// with no key), sections in file order.</summary>
        internal sealed class InfFile
        {
            internal sealed record Line(string Key, List<string> Fields);

            private readonly List<(string Name, List<Line> Lines)> _sections = new();
            private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

            internal static InfFile Read(string path) => Parse(Decode(File.ReadAllBytes(path)));

            /// <summary>UTF-16 with its byte-order mark, UTF-8 with its mark,
            /// or with no mark the system's ANSI code page, which is how
            /// Inf2Cat converts an INF without a mark.</summary>
            internal static string Decode(byte[] bytes)
            {
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                return Ansi(bytes);
            }

            private static string Ansi(byte[] bytes)
            {
                if (bytes.Length == 0) return "";
                fixed (byte* b = bytes)
                {
                    int n = MultiByteToWideChar(CP_ACP, 0, b, bytes.Length, null, 0);
                    if (n <= 0) throw new InvalidOperationException($"The INF could not be read in the ANSI code page (error {Marshal.GetLastPInvokeError()}).");
                    var chars = new char[n];
                    fixed (char* c = chars)
                        if (MultiByteToWideChar(CP_ACP, 0, b, bytes.Length, c, n) != n)
                            throw new InvalidOperationException($"The INF could not be read in the ANSI code page (error {Marshal.GetLastPInvokeError()}).");
                    return new string(chars);
                }
            }

            internal static InfFile Parse(string text)
            {
                var inf = new InfFile();
                List<Line> current = null;
                var logical = new StringBuilder();
                foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = StripComment(raw).TrimEnd();
                    // A backslash that ends a line joins the next one to it.
                    if (line.EndsWith('\\'))
                    {
                        logical.Append(line, 0, line.Length - 1);
                        continue;
                    }
                    logical.Append(line);
                    string l = logical.ToString().Trim();
                    logical.Clear();
                    if (l.Length == 0) continue;
                    if (l[0] == '[')
                    {
                        int close = l.IndexOf(']');
                        string name = (close > 0 ? l[1..close] : l[1..]).Trim().ToLowerInvariant();
                        current = new List<Line>();
                        inf._sections.Add((name, current));
                        continue;
                    }
                    if (current == null) continue;
                    int eq = IndexOutsideQuotes(l, '=');
                    string key = eq >= 0 ? Unquote(l[..eq].Trim()).ToLowerInvariant() : null;
                    string rest = eq >= 0 ? l[(eq + 1)..] : l;
                    var fields = SplitOutsideQuotes(rest).Select(f => Unquote(f.Trim()).ToLowerInvariant()).ToList();
                    current.Add(new Line(key, fields));
                }
                foreach (var line in inf.Section("strings"))
                    if (line.Key != null && line.Fields.Count > 0)
                        inf._strings.TryAdd(line.Key, line.Fields[0]);
                return inf;
            }

            private static string StripComment(string s)
            {
                bool quoted = false;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '"') quoted = !quoted;
                    else if (s[i] == ';' && !quoted) return s[..i];
                }
                return s;
            }

            private static int IndexOutsideQuotes(string s, char c)
            {
                bool quoted = false;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '"') quoted = !quoted;
                    else if (s[i] == c && !quoted) return i;
                }
                return -1;
            }

            private static List<string> SplitOutsideQuotes(string s)
            {
                var parts = new List<string>();
                bool quoted = false;
                int start = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '"') quoted = !quoted;
                    else if (s[i] == ',' && !quoted)
                    {
                        parts.Add(s[start..i]);
                        start = i + 1;
                    }
                }
                parts.Add(s[start..]);
                return parts;
            }

            // A quoted field loses its quotes, and a doubled quote inside one
            // stands for one quote.
            private static string Unquote(string s)
            {
                if (s.IndexOf('"') < 0) return s;
                var sb = new StringBuilder(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] != '"') { sb.Append(s[i]); continue; }
                    if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; }
                }
                return sb.ToString().Trim();
            }

            /// <summary>Every line of every section of that name, in file
            /// order.</summary>
            internal IEnumerable<Line> Section(string name)
                => _sections.Where(s => s.Name == name).SelectMany(s => s.Lines);

            internal bool Has(string name) => _sections.Any(s => s.Name == name);

            /// <summary><paramref name="s"/> with each %key% replaced by its
            /// [Strings] value and %% by a percent sign. A key [Strings] lacks
            /// stays as written.</summary>
            internal string Resolve(string s)
            {
                if (s.IndexOf('%') < 0) return s;
                var sb = new StringBuilder(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    int end;
                    if (s[i] == '%' && (end = s.IndexOf('%', i + 1)) > i)
                    {
                        string key = s[(i + 1)..end];
                        if (key.Length == 0) sb.Append('%');
                        else if (_strings.TryGetValue(key, out string v)) sb.Append(v);
                        else sb.Append(s, i, end - i + 1);
                        i = end;
                    }
                    else sb.Append(s[i]);
                }
                return sb.ToString();
            }

            private string VersionValue(string key)
                => Section("version").FirstOrDefault(l => l.Key == key) is { Fields.Count: > 0 } line ? line.Fields[0] : null;

            /// <summary>A driver INF as Inf2Cat counts one: [Version] carries
            /// Signature and Class or ClassGuid, and the INF has
            /// [Manufacturer] or a DefaultInstall section.</summary>
            internal bool IsDriverInf =>
                VersionValue("signature") != null
                && (VersionValue("class") != null || VersionValue("classguid") != null)
                && (Has("manufacturer") || new[] { "", ".nt", ".ntx86", ".ntia64", ".ntamd64", ".ntarm", ".ntarm64" }.Any(d => Has("defaultinstall" + d)));

            /// <summary>The catalog this INF names for an architecture's
            /// decoration.</summary>
            internal string CatalogFileFor(string decoration)
            {
                string name = VersionValue("catalogfile." + decoration) ?? VersionValue("catalogfile.nt") ?? VersionValue("catalogfile");
                return name == null ? null : Resolve(name);
            }

            /// <summary>Whether any section carries a CopyFiles directive. The
            /// files it names would be catalog members.</summary>
            internal bool CopiesFiles => _sections.Any(s => s.Lines.Any(l => l.Key == "copyfiles"));

            /// <summary>The models sections [Manufacturer] lists: each entry's
            /// section undecorated and with each of its decorations, for every
            /// architecture.</summary>
            private HashSet<string> ModelsSections()
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in Section("manufacturer"))
                {
                    if (line.Fields.Count == 0) continue;
                    string baseName = Resolve(line.Fields[0]);
                    set.Add(baseName);
                    foreach (string d in line.Fields.Skip(1)) set.Add(baseName + "." + Resolve(d));
                }
                return set;
            }

            /// <summary>The hardware and compatible IDs of every models line,
            /// in file order, with no asterisk, each once.</summary>
            internal IEnumerable<string> HardwareIds()
            {
                var models = ModelsSections();
                var ids = new List<string>();
                foreach (var (name, lines) in _sections)
                {
                    if (!models.Contains(name)) continue;
                    foreach (var line in lines)
                        for (int i = 1; i < line.Fields.Count; i++)
                        {
                            string id = Resolve(line.Fields[i].Replace("*", ""));
                            if (IsHardwareIdKept(id) && !ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
                        }
                }
                return ids;
            }

            // The legacy PnP IDs of keyboards, mice and serial devices: the
            // only bare or ACPI and BIOS PnP IDs Inf2Cat writes as HWIDn
            // attributes.
            private static readonly HashSet<string> LegacyPnpIds = new(
                "pnp0300,pnp0301,pnp0302,pnp0303,pnp0304,pnp0305,pnp0306,pnp0307,pnp0308,pnp0309,pnp030a,pnp0320,pnp0321,pnp0322,pnp0323,pnp0324,pnp0325,pnp0326,pnp0327,pnp0340,pnp0341,pnp0342,pnp0343,pnp0344,pnp0f00,pnp0f01,pnp0f02,pnp0f03,pnp0f04,pnp0f05,pnp0f06,pnp0f07,pnp0f08,pnp0f09,pnp0f0a,pnp0f0b,pnp0f0c,pnp0f0d,pnp0f0e,pnp0f0f,pnp0f10,pnp0f11,pnp0f12,pnp0f13,pnp0f14,pnp0f15,pnp0f16,pnp0f17,pnp0f18,pnp0f19,pnp0f1a,pnp0f1b,pnp0f1c,pnp0f1d,pnp0f1e,pnp0f1f,pnp0f20,pnp0f21,pnp0f22,pnp0f23".Split(','), StringComparer.OrdinalIgnoreCase);

            /// <summary>Whether an ID is written as a HWIDn attribute: every
            /// ID but a six-character PnP ID, or a PnP ID under ACPI or BIOS,
            /// unless it is one of the legacy IDs, matched whole.</summary>
            internal static bool IsHardwareIdKept(string id)
            {
                if (id.Length == 0) return false;
                if (LegacyPnpIds.Contains(id)) return true;
                int slash = id.IndexOf('\\');
                if (slash > -1)
                {
                    string bus = id[..slash];
                    if (bus is "acpi" or "bios")
                    {
                        string rest = slash + 1 < id.Length ? id[(slash + 1)..] : "";
                        return !rest.StartsWith("pnp", StringComparison.OrdinalIgnoreCase) || LegacyPnpIds.Contains(rest);
                    }
                    return true;
                }
                return !(id.Length == 6 && id.StartsWith("pnp", StringComparison.OrdinalIgnoreCase));
            }

            /// <summary>Whether a manufacturer line names a models section
            /// decorated for the architecture (its decoration, such as
            /// ntarm64 or ntamd64.10.0...16299, starts with
            /// <paramref name="decoration"/>), the test Inf2Cat makes before
            /// it catalogs an INF. An INF with no [Manufacturer] lines passes,
            /// as there.</summary>
            internal bool HasModelsDecoratedFor(string decoration)
            {
                var lines = Section("manufacturer").ToList();
                if (lines.Count == 0) return true;
                return lines.Any(l => l.Fields.Skip(1).Any(d => Resolve(d).StartsWith(decoration, StringComparison.OrdinalIgnoreCase)));
            }
        }

        // ── Interop ──────────────────────────────────────────────────────

        // mscat.h: CRYPTCATSTORE.
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

        // wincrypt.h: CRYPT_DATA_BLOB, CRYPT_ALGORITHM_IDENTIFIER and
        // CRYPT_ATTRIBUTE_TYPE_VALUE.
        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_DATA_BLOB
        {
            public uint cbData;
            public byte* pbData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_ALGORITHM_IDENTIFIER
        {
            public byte* pszObjId;
            public CRYPT_DATA_BLOB Parameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_ATTRIBUTE_TYPE_VALUE
        {
            public byte* pszObjId;
            public CRYPT_DATA_BLOB Value;
        }

        // mssip.h: SIP_SUBJECTINFO and SIP_INDIRECT_DATA.
        [StructLayout(LayoutKind.Sequential)]
        private struct SIP_SUBJECTINFO
        {
            public uint cbSize;
            public Guid* pgSubjectType;
            public IntPtr hFile;
            public char* pwsFileName;
            public char* pwsDisplayName;
            public uint dwReserved1;
            public uint dwIntVersion;
            public IntPtr hProv;
            public CRYPT_ALGORITHM_IDENTIFIER DigestAlgorithm;
            public uint dwFlags;
            public uint dwEncodingType;
            public uint dwReserved2;
            public uint fdwCAPISettings;
            public uint fdwSecuritySettings;
            public uint dwIndex;
            public uint dwUnionChoice;
            public IntPtr psFlat;
            public IntPtr pClientData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIP_INDIRECT_DATA
        {
            public CRYPT_ATTRIBUTE_TYPE_VALUE Data;
            public CRYPT_ALGORITHM_IDENTIFIER DigestAlgorithm;
            public CRYPT_DATA_BLOB Digest;
        }

        // mssign32: SIGNER_FILE_INFO, SIGNER_SUBJECT_INFO,
        // SIGNER_CERT_STORE_INFO, SIGNER_CERT, SIGNER_ATTR_AUTHCODE,
        // SIGNER_SIGNATURE_INFO and SIGNER_CONTEXT.
        [StructLayout(LayoutKind.Sequential)]
        private struct SIGNER_FILE_INFO
        {
            public uint cbSize;
            public char* pwszFileName;
            public IntPtr hFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIGNER_SUBJECT_INFO
        {
            public uint cbSize;
            public uint* pdwIndex;
            public uint dwSubjectChoice;
            public SIGNER_FILE_INFO* pSignerFileInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIGNER_CERT_STORE_INFO
        {
            public uint cbSize;
            public IntPtr pSigningCert;
            public uint dwCertPolicy;
            public IntPtr hCertStore;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIGNER_CERT
        {
            public uint cbSize;
            public uint dwCertChoice;
            public SIGNER_CERT_STORE_INFO* pCertStoreInfo;
            public IntPtr hwnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIGNER_ATTR_AUTHCODE
        {
            public uint cbSize;
            public int fCommercial;
            public int fIndividual;
            public char* pwszName;
            public char* pwszInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIGNER_SIGNATURE_INFO
        {
            public uint cbSize;
            public uint algidHash;
            public uint dwAttrChoice;
            public SIGNER_ATTR_AUTHCODE* pAttrAuthcode;
            public IntPtr psAuthenticated;
            public IntPtr psUnauthenticated;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIGNER_CONTEXT
        {
            public uint cbSize;
            public uint cbBlob;
            public byte* pbBlob;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CryptCATOpen(string pwszFileName, uint fdwOpenFlags, IntPtr hProv, uint dwPublicVersion, uint dwEncodingType);

        [DllImport("wintrust.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATClose(IntPtr hCatalog);

        [DllImport("wintrust.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern CRYPTCATSTORE* CryptCATStoreFromHandle(IntPtr hCatalog);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CryptCATPutCatAttrInfo(IntPtr hCatalog, string pwszReferenceTag, uint dwAttrTypeAndAction, uint cbData, byte* pbData);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CryptCATPutMemberInfo(IntPtr hCatalog, string pwszFileName, string pwszReferenceTag,
            Guid* pgSubjectType, uint dwCertVersion, uint cbSIPIndirectData, byte* pbSIPIndirectData);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CryptCATPutAttrInfo(IntPtr hCatalog, IntPtr pCatMember, string pwszReferenceTag,
            uint dwAttrTypeAndAction, uint cbData, byte* pbData);

        [DllImport("wintrust.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATPersistStore(IntPtr hCatalog);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptSIPRetrieveSubjectGuidForCatalogFile(string FileName, IntPtr hFileIn, Guid* pgSubject);

        [DllImport("crypt32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptSIPCreateIndirectData(SIP_SUBJECTINFO* pSubjectInfo, uint* pcbIndirectData, byte* pIndirectData);

        // winnls.h
        private const uint CP_ACP = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int MultiByteToWideChar(uint CodePage, uint dwFlags, byte* lpMultiByteStr, int cbMultiByte,
            char* lpWideCharStr, int cchWideChar);

        [DllImport("mssign32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SignerSignEx2(uint dwFlags, SIGNER_SUBJECT_INFO* pSubjectInfo, SIGNER_CERT* pSignerCert,
            SIGNER_SIGNATURE_INFO* pSignatureInfo, void* pProviderInfo, uint dwTimestampFlags, byte* pszTimestampAlgorithmOid,
            char* pwszHttpTimeStamp, void* psRequest, void* pSipData, SIGNER_CONTEXT** ppSignerContext, void* pCryptoPolicy,
            void* pReserved);

        [DllImport("mssign32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SignerFreeSignerContext(SIGNER_CONTEXT* pSignerContext);
    }
}
