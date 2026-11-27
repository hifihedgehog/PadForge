using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// A vendor library PadForge loads into its own process, which runs with
    /// administrator rights (#600). Only one that only administrators could
    /// have put in place is loaded: it sits in a folder no other account can
    /// add a file to, and that folder and every folder above it give no
    /// other account the right to delete or rename what they hold or to
    /// change who may, by the one rule PadForge's own folders are judged by
    /// (<see cref="ChangeRule"/>). Its Authenticode signature, embedded or from a
    /// catalog, holds by the check Windows runs before it trusts a download
    /// (<see cref="AuthenticodeSigner"/>), and its signer is the vendor's. The
    /// file is held open with no write or delete sharing from before the
    /// checks until it is loaded, so the file checked is the file
    /// loaded.
    /// </summary>
    public static class TrustedLibrary
    {
        /// <summary>The start of each vendor's signer name, as each signs
        /// its software: "Logitech Inc" and "Logitech Europe S.A.", "Corsair
        /// Memory, Inc.", "Micro-Star International CO., LTD." and "Razer USA
        /// Ltd.".</summary>
        public static readonly string[] Logitech = { "Logitech" };
        public static readonly string[] Corsair = { "Corsair" };
        public static readonly string[] MicroStar = { "Micro-Star International" };
        public static readonly string[] Razer = { "Razer" };

        /// <summary>Test seam: judges a path in place of its access lists and
        /// signature, answering null for a library to trust or the reason.
        /// Null in production.</summary>
        public static Func<string, IReadOnlyList<string>, string> CheckSeam;

        /// <summary>
        /// Holds <paramref name="path"/> open against writes and deletes and
        /// checks it, or answers null with <paramref name="reason"/>. The
        /// caller loads the library, or lets the code that loads it run,
        /// while it keeps the hold. With <paramref name="signers"/> null the
        /// signature is not read, for a library its vendor ships unsigned,
        /// and the place alone decides.
        /// </summary>
        public static IDisposable Hold(string path, IReadOnlyList<string> signers, out string reason)
        {
            reason = "missing";
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return null; }
            FileStream hold;
            try { hold = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (Exception) { return null; }
            try
            {
                reason = CheckSeam != null ? CheckSeam(full, signers) : Check(full, hold, signers);
                if (reason == null) return hold;
            }
            catch (Exception ex)
            {
                reason = "check failed: " + ex.GetType().Name;
            }
            hold.Dispose();
            return null;
        }

        /// <summary>Loads <paramref name="path"/> through
        /// <paramref name="load"/> once it passes the checks, under the hold.
        /// The module, or zero with <paramref name="reason"/>.</summary>
        public static IntPtr Load(string path, IReadOnlyList<string> signers, Func<string, IntPtr> load, out string reason)
        {
            using var hold = Hold(path, signers, out reason);
            if (hold == null) return IntPtr.Zero;
            IntPtr module = load(Path.GetFullPath(path));
            if (module == IntPtr.Zero) reason = "load";
            return module;
        }

        /// <summary>
        /// What a loader that tries its candidates in turn reports once one
        /// more did not end in a library: <paramref name="reason"/> so far,
        /// "missing" before any, and <paramref name="why"/> for this one, the
        /// reason <see cref="Load"/> gave or "exports" for a library that
        /// loaded and is not the one wanted. A candidate is a file that
        /// exists, so one that could not be opened ("missing") or loaded is
        /// "load", and one the checks refused is "refused". The worse stands:
        /// "exports" over "load" over "refused" over "missing", so the words
        /// for the reason are true of at least one candidate.
        /// </summary>
        /// <summary>The diagnostics log's line for a candidate that did not
        /// load, as <paramref name="who"/> names the library: the reason
        /// <see cref="Load"/> gave, a reason of "missing" as could not be
        /// opened (a candidate is a file that exists) and "load" as would
        /// not load. Every such candidate is written, so a status line that
        /// sends the user to the log finds it there.</summary>
        public static string NotLoadedLine(string who, string candidate, string why)
            => who + " " + candidate + " not loaded: " + why switch
            {
                null or "missing" => "could not be opened",
                "load" => "would not load",
                _ => why,
            };

        public static string Outcome(string reason, string why)
        {
            string now = why switch
            {
                null or "missing" or "load" => "load",
                "exports" => "exports",
                _ => "refused",
            };
            return Rank(now) > Rank(reason) ? now : reason ?? "missing";
        }

        private static int Rank(string reason) => reason switch
        {
            "exports" => 3,
            "load" => 2,
            "refused" => 1,
            _ => 0,
        };

        private static string Check(string full, FileStream hold, IReadOnlyList<string> signers)
        {
            string why = WhoMayChange(full, hold.SafeFileHandle);
            if (why != null) return why;
            if (signers == null) return null;
            // The signature, embedded or from a catalog that lists the file,
            // is verified and its signer read on the held handle
            // (AuthenticodeSigner.OfHeld). None that holds names no signer.
            string signer = AuthenticodeSigner.OfHeld(full, hold.SafeFileHandle);
            if (signer == null) return "not signed";
            if (!SignedBy(signer, signers)) return "signed by " + signer;
            return null;
        }

        /// <summary>Whether a signer's name starts with one of the
        /// vendor's.</summary>
        public static bool SignedBy(string signer, IReadOnlyList<string> signers)
        {
            if (string.IsNullOrEmpty(signer) || signers == null) return false;
            foreach (string vendor in signers)
                if (!string.IsNullOrEmpty(vendor) && signer.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── Who may change it ──

        /// <summary>
        /// Why another account may change the library or where it sits, or
        /// null when none can, by <see cref="ChangeRule"/> on each one's own
        /// security descriptor: the file's read through the held handle, its
        /// folder's at <see cref="ChangeRule.Level.VendorFolder"/>, and each
        /// folder above up to the drive's root at
        /// <see cref="ChangeRule.Level.VendorAbove"/>. Each folder is opened
        /// as itself, so a link in the path is seen and refused, and its
        /// descriptor is read from the handle that was checked.
        /// </summary>
        private static string WhoMayChange(string full, SafeFileHandle file)
        {
            byte[] own = DescriptorOf(file);
            if (own == null) return "cannot read who may change " + full;
            if (ChangeRule.Judge(own, ChangeRule.Level.VendorFile) != ChangeRule.Verdict.Sound) return "others may change " + full;
            var level = ChangeRule.Level.VendorFolder;
            for (string folder = Path.GetDirectoryName(full); folder != null; folder = Path.GetDirectoryName(folder))
            {
                using var handle = CreateFileW(folder, ReadControl, ShareAll, IntPtr.Zero, OpenExisting,
                    FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
                    return "cannot read who may change " + folder;
                if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) return "a link in the path at " + folder;
                byte[] descriptor = DescriptorOf(handle);
                if (descriptor == null) return "cannot read who may change " + folder;
                if (ChangeRule.Judge(descriptor, level) != ChangeRule.Verdict.Sound) return "others may change " + folder;
                level = ChangeRule.Level.VendorAbove;
            }
            return null;
        }

        /// <summary>The owner and access list of what
        /// <paramref name="handle"/> holds, as the self-relative security
        /// descriptor GetSecurityInfo gives, or null.</summary>
        private static byte[] DescriptorOf(SafeFileHandle handle)
        {
            uint error = GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation,
                out _, out _, out _, out _, out IntPtr descriptor);
            if (error != 0 || descriptor == IntPtr.Zero) return null;
            try
            {
                uint length = GetSecurityDescriptorLength(descriptor);
                if (length == 0) return null;
                byte[] bytes = new byte[length];
                Marshal.Copy(descriptor, bytes, 0, (int)length);
                return bytes;
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        private const int SeFileObject = 1;
        private const uint OwnerSecurityInformation = 0x1, DaclSecurityInformation = 0x4;
        private const uint ReadControl = 0x00020000, ShareAll = 0x7, OpenExisting = 3;
        private const uint FlagBackupSemantics = 0x02000000, FlagOpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
    }
}
