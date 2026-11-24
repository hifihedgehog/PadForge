using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Who signed a program's file, as the request card and Activity name it
    /// (#597): the signer of an Authenticode signature Windows trusts, the
    /// one embedded in the file or the one of a catalog that lists it, as
    /// Windows' own files are signed. Null when the file is not signed or
    /// its signature does not hold.
    ///
    /// <para>The steps follow System Informer's phlib/verify.c
    /// (PhVerifyFile, PhVerifyFileEx, PhpVerifyFile,
    /// PhpVerifyFileFromCatalog, PhpGetSignaturesFromStateData) and
    /// PowerShell's Authenticode.cs (GetSignatureFromWinVerifyTrust,
    /// TryGetProviderSigner): the file is opened once, sharing reads and
    /// deletes only, so it cannot change between the checks. Its embedded
    /// signature is verified first. When it has none, the catalogs are
    /// searched for its SHA-256 hash, then its SHA-1 hash, and the catalog
    /// member is verified. The signer's certificate is read from the state
    /// WinVerifyTrust kept (the first signer's chain, its first
    /// certificate), then that state is closed. Revocation is not checked
    /// and nothing is fetched from the network (verify.c's
    /// PH_VERIFY_PREVENT_NETWORK_ACCESS): the name is for the user's eyes
    /// and decides nothing.</para>
    ///
    /// <para>An answer is kept for the file's path, size and time of last
    /// write, so a program that connects again costs no second check, and a
    /// file put in its place is checked anew.</para>
    ///
    /// <para>A vendor library PadForge loads into its own process is checked
    /// the same way, on the handle <see cref="TrustedLibrary"/> holds from
    /// the check through the load (<see cref="OfHeld"/>), with nothing
    /// kept. There the signer decides whether the library loads.</para>
    /// </summary>
    public static class AuthenticodeSigner
    {
        private const int MostKept = 64;

        private static readonly object Sync = new object();
        private static readonly Dictionary<(string Path, long Length, long Written), string> Kept = new();
        private static readonly Queue<(string Path, long Length, long Written)> KeptOrder = new();

        /// <summary>Test seam: stands in for the check. Null in the
        /// app.</summary>
        public static Func<string, string> Seam;

        /// <summary>The signer's name for the file at
        /// <paramref name="path"/>, or null.</summary>
        public static string Of(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var seam = Seam;
            if (seam != null) return seam(path);
            (string, long, long) key;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                key = (path.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
            lock (Sync)
            {
                if (Kept.TryGetValue(key, out string known)) return known;
            }
            string signer;
            try { signer = Read(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
                signer = null;
            }
            lock (Sync)
            {
                if (!Kept.ContainsKey(key))
                {
                    while (KeptOrder.Count >= MostKept) Kept.Remove(KeptOrder.Dequeue());
                    Kept[key] = signer;
                    KeptOrder.Enqueue(key);
                }
            }
            return signer;
        }

        private static string Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return Read(path, stream.SafeFileHandle.DangerousGetHandle());
        }

        /// <summary>The signer of the file the caller holds open at
        /// <paramref name="path"/> through <paramref name="held"/>, read
        /// on that handle, so the file checked is the file held. Null when
        /// it is not signed, its signature does not hold, or it cannot be
        /// read. Nothing is kept, and the test seam does not apply.</summary>
        public static string OfHeld(string path, Microsoft.Win32.SafeHandles.SafeFileHandle held)
        {
            if (string.IsNullOrEmpty(path) || held == null || held.IsInvalid || held.IsClosed) return null;
            bool added = false;
            try
            {
                held.DangerousAddRef(ref added);
                return Read(path, held.DangerousGetHandle());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException
                                           or ObjectDisposedException)
            {
                return null;
            }
            finally
            {
                if (added) held.DangerousRelease();
            }
        }

        private static string Read(string path, IntPtr handle)
        {
            string embedded = VerifyFile(path, handle, out bool noSignature);
            if (embedded != null || !noSignature) return embedded;
            return VerifyCatalogMember(path, handle);
        }

        // ── An embedded signature ──

        private static string VerifyFile(string path, IntPtr handle, out bool noSignature)
        {
            noSignature = false;
            IntPtr file = Marshal.StringToCoTaskMemUni(path);
            IntPtr fileInfo = IntPtr.Zero;
            try
            {
                var fi = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = file,
                    hFile = handle,
                };
                fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                Marshal.StructureToPtr(fi, fileInfo, false);
                var data = NewData(WTD_CHOICE_FILE, fileInfo);
                int result = Verify(ref data, out string signer);
                // A file of a kind no signature format covers (a script
                // without a SIP) may still be listed in a catalog, which
                // PowerShell's reading takes for any file
                // (GetSignatureFromMSSecurityExtensions), so it goes to the
                // catalogs as an unsigned file does.
                noSignature = result is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN;
                return result == 0 ? signer : null;
            }
            finally
            {
                if (fileInfo != IntPtr.Zero) Marshal.FreeHGlobal(fileInfo);
                Marshal.FreeCoTaskMem(file);
            }
        }

        // ── A catalog that lists the file ──

        private static string VerifyCatalogMember(string path, IntPtr handle)
        {
            foreach (string algorithm in new[] { "SHA256", "SHA1" })
            {
                if (!CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
                try
                {
                    uint size = 0;
                    CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0);
                    if (size == 0 || size > 256) continue;
                    var hash = new byte[size];
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0)) continue;
                    IntPtr catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                    if (catalog == IntPtr.Zero) continue;
                    try
                    {
                        var catInfo = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                        if (!CryptCATCatalogInfoFromContext(catalog, ref catInfo, 0)) continue;
                        string signer = VerifyMember(catInfo.wszCatalogFile, path, handle, hash, admin);
                        if (signer != null) return signer;
                    }
                    finally
                    {
                        CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                    }
                }
                finally
                {
                    CryptCATAdminReleaseContext(admin, 0);
                }
            }
            return null;
        }

        /// <summary>A catalog member is verified by its open handle, as
        /// verify.c's PhpVerifyFileFromCatalog does, and by its path, which
        /// wintrust.h names as required.</summary>
        private static string VerifyMember(string catalogPath, string path, IntPtr file, byte[] hash, IntPtr admin)
        {
            var tag = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) tag.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            IntPtr catalogFile = Marshal.StringToCoTaskMemUni(catalogPath);
            IntPtr memberTag = Marshal.StringToCoTaskMemUni(tag.ToString());
            IntPtr memberFile = Marshal.StringToCoTaskMemUni(path);
            IntPtr hashCopy = Marshal.AllocHGlobal(hash.Length);
            IntPtr info = IntPtr.Zero;
            try
            {
                Marshal.Copy(hash, 0, hashCopy, hash.Length);
                var ci = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                    pcwszCatalogFilePath = catalogFile,
                    pcwszMemberTag = memberTag,
                    pcwszMemberFilePath = memberFile,
                    hMemberFile = file,
                    pbCalculatedFileHash = hashCopy,
                    cbCalculatedFileHash = (uint)hash.Length,
                    hCatAdmin = admin,
                };
                info = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                Marshal.StructureToPtr(ci, info, false);
                var data = NewData(WTD_CHOICE_CATALOG, info);
                return Verify(ref data, out string signer) == 0 ? signer : null;
            }
            finally
            {
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                Marshal.FreeHGlobal(hashCopy);
                Marshal.FreeCoTaskMem(memberFile);
                Marshal.FreeCoTaskMem(memberTag);
                Marshal.FreeCoTaskMem(catalogFile);
            }
        }

        // ── WinVerifyTrust and its state ──

        private static WINTRUST_DATA NewData(uint choice, IntPtr info) => new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice,
            pInfo = info,
            dwStateAction = WTD_STATEACTION_VERIFY,
            // verify.c's PhpVerifyFile with network access prevented. MD2
            // and MD4 hashes are refused.
            dwProvFlags = WTD_SAFER_FLAG | WTD_DISABLE_MD2_MD4 | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };

        /// <summary>Verifies, reads the signer when the signature holds, and
        /// closes the state WinVerifyTrust kept whatever came of it.</summary>
        private static int Verify(ref WINTRUST_DATA data, out string signer)
        {
            signer = null;
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);
            try
            {
                if (result == 0 && data.hWVTStateData != IntPtr.Zero) signer = SignerOf(data.hWVTStateData);
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);
            }
            return result;
        }

        private static string SignerOf(IntPtr state)
        {
            IntPtr provider = WTHelperProvDataFromStateData(state);
            if (provider == IntPtr.Zero) return null;
            IntPtr provSigner = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (provSigner == IntPtr.Zero) return null;
            var ps = Marshal.PtrToStructure<CRYPT_PROVIDER_SGNR>(provSigner);
            if (ps.csCertChain == 0 || ps.pasCertChain == IntPtr.Zero) return null;
            var leaf = Marshal.PtrToStructure<CRYPT_PROVIDER_CERT>(ps.pasCertChain);
            if (leaf.pCert == IntPtr.Zero) return null;
            uint length = CertGetNameStringW(leaf.pCert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, IntPtr.Zero, null, 0);
            if (length <= 1) return null;
            var name = new StringBuilder((int)length);
            CertGetNameStringW(leaf.pCert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, IntPtr.Zero, name, length);
            string text = name.ToString().Trim();
            return text.Length == 0 ? null : text;
        }

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_CHOICE_CATALOG = 2;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_SAFER_FLAG = 0x00000100;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
        private const uint WTD_DISABLE_MD2_MD4 = 0x00002000;
        private const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;
        private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
        private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
        private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_CATALOG_INFO
        {
            public uint cbStruct;
            public uint dwCatalogVersion;
            public IntPtr pcwszCatalogFilePath;
            public IntPtr pcwszMemberTag;
            public IntPtr pcwszMemberFilePath;
            public IntPtr hMemberFile;
            public IntPtr pbCalculatedFileHash;
            public uint cbCalculatedFileHash;
            public IntPtr pcCatalogContext;
            public IntPtr hCatAdmin;
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
            public IntPtr pInfo;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CATALOG_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string wszCatalogFile;
        }

        // wintrust.h: CRYPT_PROVIDER_SGNR, read up to the chain it holds.
        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_PROVIDER_SGNR
        {
            public uint cbStruct;
            public System.Runtime.InteropServices.ComTypes.FILETIME sftVerifyAsOf;
            public uint csCertChain;
            public IntPtr pasCertChain;
        }

        // wintrust.h: CRYPT_PROVIDER_CERT, read up to the certificate.
        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_PROVIDER_CERT
        {
            public uint cbStruct;
            public IntPtr pCert;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIndex,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem, string hashAlgorithm,
            IntPtr strongHashPolicy, uint flags);

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, IntPtr file, ref uint hashSize,
            byte[] hash, uint flags);

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint hashSize, uint flags,
            IntPtr previousCatInfo);

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CATALOG_INFO info, uint flags);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

        [DllImport("crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint CertGetNameStringW(IntPtr certContext, uint type, uint flags, IntPtr typePara,
            StringBuilder name, uint nameLength);
    }
}
