using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// A vendor library PadForge loads into its own process, which runs with
    /// administrator rights (#597). Only one that only administrators could
    /// have put in place is loaded: it sits in a folder no other account can
    /// add a file to, and that folder and every folder above it give no
    /// other account the right to delete or rename what they hold or to
    /// change who may. Its Authenticode signature, embedded or from a
    /// catalog, holds by the check Windows runs before it trusts a download
    /// (<see cref="AuthenticodeSigner"/>), and its signer is the vendor's. The
    /// file is held open with no write or delete sharing from before the
    /// checks until it is loaded, so the file checked is the file
    /// loaded.
    ///
    /// <para>A library found elsewhere, or that loads code from such a
    /// place, loads in the library host with the user's rights instead
    /// (PadForge.Services.NativeHost).</para>
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

        private static string Check(string full, FileStream hold, IReadOnlyList<string> signers)
        {
            string why = WhoMayChange(full);
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

        /// <summary>The accounts an access list may give a right to change
        /// with: Administrators, SYSTEM and TrustedInstaller.</summary>
        private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
        {
            "S-1-5-32-544", "S-1-5-18", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
        };

        /// <summary>One allow entry of an access list as the rule reads
        /// it.</summary>
        public readonly record struct Entry(string Sid, FileSystemRights Rights, bool InheritOnly);

        private const FileSystemRights ChangeWho = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000, GenericAll = (FileSystemRights)0x10000000;

        /// <summary>
        /// Whether another account may change what <paramref name="kind"/>
        /// names. The library's file: write, append or delete it. Its folder:
        /// add a file or a folder, delete one, or delete the folder. A folder
        /// above: delete or rename what it holds, or the folder itself. Any
        /// of them: change who may, or own it. An entry that applies only to
        /// what is made inside counts for none of them.
        /// </summary>
        public static bool OthersMayChange(string owner, IEnumerable<Entry> entries, Level kind)
        {
            if (owner == null || !Trusted.Contains(owner)) return true;
            FileSystemRights risky = ChangeWho | GenericAll | FileSystemRights.Delete | kind switch
            {
                Level.File => FileSystemRights.WriteData | FileSystemRights.AppendData | GenericWrite,
                Level.Folder => FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.DeleteSubdirectoriesAndFiles | GenericWrite,
                _ => FileSystemRights.DeleteSubdirectoriesAndFiles,
            };
            foreach (var entry in entries)
            {
                if (entry.InheritOnly || Trusted.Contains(entry.Sid)) continue;
                if ((entry.Rights & risky) != 0) return true;
            }
            return false;
        }

        public enum Level { File, Folder, Above }

        /// <summary>Why another account may change the library or where it
        /// sits, or null when none can.</summary>
        private static string WhoMayChange(string full)
        {
            var file = new FileInfo(full);
            if (Changeable(file.GetAccessControl(), Level.File)) return "others may change " + full;
            var folder = file.Directory;
            var level = Level.Folder;
            while (folder != null)
            {
                if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) return "a link in the path at " + folder.FullName;
                if (Changeable(folder.GetAccessControl(), level)) return "others may change " + folder.FullName;
                folder = folder.Parent;
                level = Level.Above;
            }
            return null;
        }

        private static bool Changeable(FileSystemSecurity security, Level level)
        {
            string owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
            var entries = new List<Entry>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                entries.Add(new Entry(((SecurityIdentifier)rule.IdentityReference).Value, rule.FileSystemRights,
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0));
            }
            return OthersMayChange(owner, entries, level);
        }
    }
}
