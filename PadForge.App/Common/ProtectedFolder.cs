using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PadForge.Common
{
    /// <summary>
    /// The folders only Administrators and SYSTEM can change, where PadForge
    /// with rights keeps what it unpacks, stages or starts (#597): its native
    /// libraries, a downloaded update, the DSX stand-in, a driver package
    /// before it is installed, a task's definition before schtasks reads it.
    ///
    /// <para>The root is <c>%ProgramData%\PadForge</c>, owned by Administrators,
    /// with a protected access list: Administrators and SYSTEM have full
    /// control, Users read and execute, and every folder and file made inside
    /// inherits it. Any account can make a folder in %ProgramData%, so one
    /// that is already there is judged before it is used (<see cref="Judge"/>).
    /// One owned by another account, or one that lets another account change
    /// it or what is in it, is renamed aside and a new one is made in its
    /// place with the list above, in one step (<see cref="MakeSound"/>).
    /// Administrators may rename any folder in %ProgramData% (their full
    /// control there includes deleting a child, Microsoft, File Security and
    /// Access Rights), so a folder whose own list shuts them out is moved all
    /// the same, and nothing that was in it is ever opened.</para>
    ///
    /// <para>Every member works through <see cref="System32Calls"/>, since
    /// <see cref="PadForge.Services.ProtectedStart"/> uses them before the
    /// process may make a <c>DllImport</c> call, and none of them
    /// throws.</para>
    /// </summary>
    internal static class ProtectedFolder
    {
        internal const string AdministratorsSid = "S-1-5-32-544";
        internal const string SystemSid = "S-1-5-18";
        internal const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        internal const string CreatorOwnerSid = "S-1-3-0";
        internal const string OwnerRightsSid = "S-1-3-4";

        /// <summary>Owner Administrators. A protected list: Administrators
        /// and SYSTEM full control, Users read and execute, each inherited by
        /// every folder and file inside.</summary>
        internal const string Sddl = "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)";

        /// <summary>The rights that only read: read data and attributes and
        /// extended attributes, execute or traverse, read the access list,
        /// wait on the object, and the generic read and execute. Every other
        /// bit changes the folder, what is in it, or who may.</summary>
        internal const int ReadOnlyRights = unchecked((int)0xA01200A9);

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. While it is set and no <see cref="RootSeam"/>
        /// is given, the root is a folder that cannot be made, so no test
        /// touches %ProgramData%.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: the root. Null in production.</summary>
        internal static string RootSeam;

        internal enum Verdict
        {
            /// <summary>Owned by Administrators or SYSTEM, and no other
            /// account may change it or what is made in it.</summary>
            Sound,

            /// <summary>Nothing of that name.</summary>
            NotThere,

            /// <summary>A file, or a link that points elsewhere.</summary>
            NotAFolder,

            /// <summary>Its access list could not be read.</summary>
            Unreadable,

            /// <summary>Owned by another account, which can always change
            /// its list.</summary>
            OtherOwner,

            /// <summary>It has no access list, which lets everyone do
            /// everything.</summary>
            NoList,

            /// <summary>Its list lets another account change it, or what
            /// is made in it.</summary>
            OthersMayChange,
        }

        /// <summary>One entry of an access list, as <see cref="Judge(string, bool, IReadOnlyList{Entry})"/>
        /// reads it.</summary>
        internal readonly record struct Entry(bool Allows, int Rights, string Sid, bool InheritOnly);

        private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        /// <summary>
        /// <c>%ProgramData%\PadForge</c>. %ProgramData% is read as Windows
        /// keeps it, in HKEY_LOCAL_MACHINE's profile list, with its own
        /// %SystemDrive% and %SystemRoot% filled in from Windows' folder: the
        /// process's variables come from the user's part of the registry,
        /// which the user's programs can write, and one of those set to
        /// another folder would move the root to where they can write. Empty
        /// when Windows' folder cannot be read.
        /// </summary>
        internal static string Root
        {
            get
            {
                if (RootSeam != null) return RootSeam;
                if (OffLimitsForTest) return string.Empty;
                return RootUnder(System32Calls.WindowsFolder, System32Calls.MachineText(ProfileList, "ProgramData"));
            }
        }

        /// <summary>The root from Windows' folder and the profile list's
        /// ProgramData value as written. A value that names any other
        /// variable, or none, falls back to Windows' default,
        /// <c>%SystemDrive%\ProgramData</c>.</summary>
        internal static string RootUnder(string windowsFolder, string programDataValue)
        {
            if (string.IsNullOrEmpty(windowsFolder) || windowsFolder.Length < 3 || windowsFolder[1] != ':') return string.Empty;
            string drive = windowsFolder.Substring(0, 2);
            string programData = drive + @"\ProgramData";
            if (!string.IsNullOrEmpty(programDataValue))
            {
                string filled = Replace(Replace(programDataValue, "%SystemDrive%", drive), "%SystemRoot%", windowsFolder);
                if (filled.IndexOf('%') < 0 && filled.Length > 3 && filled[1] == ':' && filled[2] == '\\')
                    programData = filled.TrimEnd('\\');
            }
            return programData + @"\PadForge";
        }

        /// <summary>Replaces a variable's name, upper and lower case alike,
        /// with ASCII casing alone.</summary>
        private static string Replace(string text, string name, string value)
        {
            for (int at = 0; at + name.Length <= text.Length; at++)
            {
                bool same = true;
                for (int i = 0; i < name.Length && same; i++)
                    same = AsciiUpper(text[at + i]) == AsciiUpper(name[i]);
                if (same) return text.Substring(0, at) + value + text.Substring(at + name.Length);
            }
            return text;
        }

        private static char AsciiUpper(char c) => c >= 'a' && c <= 'z' ? (char)(c - 32) : c;

        /// <summary>Where a start with rights has its native libraries
        /// unpacked: the value of DOTNET_BUNDLE_EXTRACT_BASE_DIR. The host
        /// adds the exe's name and the bundle's ID below it (extractor.cpp,
        /// extraction_dir).</summary>
        internal static string Natives => Under("Natives");

        /// <summary>Where an update is downloaded and waits to be
        /// installed.</summary>
        internal static string Updates => Under("Update");

        /// <summary>Where a file PadForge writes and then starts, installs or
        /// hands to a tool goes, each in a folder of its own.</summary>
        internal static string Work => Under("Work");

        private static string Under(string name)
        {
            string root = Root;
            return root.Length == 0 ? string.Empty : root + @"\" + name;
        }

        /// <summary>Judges a folder as it stands now. A link is judged as the
        /// link, and a link is never sound.</summary>
        internal static Verdict Judge(string path)
        {
            if (string.IsNullOrEmpty(path)) return Verdict.Unreadable;
            byte[] descriptor = System32Calls.ReadDescriptor(path, out uint attributes);
            if (descriptor == null)
                return System32Calls.Attributes(path) == System32Calls.InvalidAttributes ? Verdict.NotThere : Verdict.Unreadable;
            return Judge(descriptor, attributes, trustTrustedInstaller: false);
        }

        /// <summary>Judges what was read from one object: its attributes and
        /// its security descriptor.</summary>
        internal static Verdict Judge(byte[] descriptor, uint attributes, bool trustTrustedInstaller)
        {
            if ((attributes & System32Calls.AttributeDirectory) == 0 || (attributes & System32Calls.AttributeReparsePoint) != 0)
                return Verdict.NotAFolder;
            return Judge(descriptor, trustTrustedInstaller);
        }

        /// <summary>The rights over a folder that let an account take away
        /// or replace a folder inside it: delete a child, change the list or
        /// the owner, all rights, and the most it may be granted.</summary>
        internal const int ReplaceChildRights = 0x40 | 0x40000 | 0x80000 | 0x10000000 | 0x02000000;

        /// <summary>
        /// Judges the folder that holds <paramref name="path"/>: sound when it
        /// is owned by Administrators, SYSTEM or TrustedInstaller and no other
        /// account may delete what is in it or change who may. A sound folder
        /// in a parent like that cannot be renamed and replaced by another
        /// account between the check and the use. Entries inherited only by
        /// what is made inside do not apply to the parent itself. A link is
        /// followed, so a %ProgramData% that points elsewhere is judged where
        /// it points.
        /// </summary>
        internal static Verdict JudgeParent(string path)
        {
            int slash = string.IsNullOrEmpty(path) ? -1 : path.TrimEnd('\\').LastIndexOf('\\');
            if (slash < 2) return Verdict.Unreadable;
            // A drive's root keeps its separator.
            string parent = path.Substring(0, slash == 2 ? 3 : slash);
            byte[] descriptor = System32Calls.ReadDescriptor(parent, false, out uint attributes);
            if (descriptor == null) return Verdict.Unreadable;
            if ((attributes & System32Calls.AttributeDirectory) == 0) return Verdict.NotAFolder;
            return JudgeParent(descriptor);
        }

        internal static Verdict JudgeParent(byte[] descriptor)
        {
            RawSecurityDescriptor sd;
            try
            {
                sd = new RawSecurityDescriptor(descriptor, 0);
            }
            catch
            {
                return Verdict.Unreadable;
            }
            if (!IsTrustedOwner(sd.Owner?.ToString(), trustTrustedInstaller: true)) return Verdict.OtherOwner;
            RawAcl acl = sd.DiscretionaryAcl;
            if ((sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || acl == null) return Verdict.NoList;
            foreach (GenericAce ace in acl)
            {
                if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
                if (ace is not QualifiedAce qualified) return Verdict.OthersMayChange;
                if (qualified.AceQualifier != AceQualifier.AccessAllowed) continue;
                if ((qualified.AccessMask & ReplaceChildRights) == 0) continue;
                string sid = qualified.SecurityIdentifier.ToString();
                if (IsTrustedAccount(sid) || sid == OwnerRightsSid) continue;
                return Verdict.OthersMayChange;
            }
            return Verdict.Sound;
        }

        /// <summary>Judges a security descriptor in its binary form.
        /// <paramref name="trustTrustedInstaller"/> lets TrustedInstaller own
        /// it too, as it owns the folders Windows sets up.</summary>
        internal static Verdict Judge(byte[] descriptor, bool trustTrustedInstaller)
        {
            RawSecurityDescriptor sd;
            try
            {
                sd = new RawSecurityDescriptor(descriptor, 0);
            }
            catch
            {
                return Verdict.Unreadable;
            }
            var entries = new List<Entry>();
            RawAcl acl = sd.DiscretionaryAcl;
            bool hasList = (sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && acl != null;
            if (hasList)
            {
                foreach (GenericAce ace in acl)
                {
                    bool inheritOnly = (ace.AceFlags & AceFlags.InheritOnly) != 0;
                    switch (ace)
                    {
                        case QualifiedAce qualified:
                            bool allows = qualified.AceQualifier == AceQualifier.AccessAllowed;
                            if (qualified.AceQualifier == AceQualifier.AccessDenied || qualified.AceQualifier == AceQualifier.SystemAudit
                                || qualified.AceQualifier == AceQualifier.SystemAlarm)
                                allows = false;
                            entries.Add(new Entry(allows, qualified.AccessMask, qualified.SecurityIdentifier.ToString(), inheritOnly));
                            break;
                        default:
                            // A kind of entry this does not read could allow
                            // anything to anyone.
                            entries.Add(new Entry(true, -1, string.Empty, inheritOnly));
                            break;
                    }
                }
            }
            return Judge(sd.Owner?.ToString(), hasList, entries, trustTrustedInstaller);
        }

        /// <summary>The rule itself. The owner is Administrators or SYSTEM
        /// (or TrustedInstaller, when it is trusted), there is a list, and
        /// every entry that allows more than reading goes to Administrators,
        /// SYSTEM or TrustedInstaller. An entry for CREATOR OWNER or OWNER
        /// RIGHTS gives its rights to whoever owns what it covers, who is one
        /// of those when the rest holds. Entries inherited only by what is
        /// made inside count as well, since what PadForge unpacks there would
        /// carry them.</summary>
        internal static Verdict Judge(string ownerSid, bool hasList, IReadOnlyList<Entry> entries, bool trustTrustedInstaller = false)
        {
            if (!IsTrustedOwner(ownerSid, trustTrustedInstaller)) return Verdict.OtherOwner;
            if (!hasList) return Verdict.NoList;
            foreach (var entry in entries)
            {
                if (!entry.Allows) continue;
                if ((entry.Rights & ~ReadOnlyRights) == 0) continue;
                if (IsTrustedAccount(entry.Sid)) continue;
                if (entry.Sid == CreatorOwnerSid || entry.Sid == OwnerRightsSid) continue;
                return Verdict.OthersMayChange;
            }
            return Verdict.Sound;
        }

        private static bool IsTrustedOwner(string sid, bool trustTrustedInstaller)
            => sid == AdministratorsSid || sid == SystemSid || (trustTrustedInstaller && sid == TrustedInstallerSid);

        /// <summary>Administrators, SYSTEM and TrustedInstaller.</summary>
        internal static bool IsTrustedAccount(string sid)
            => sid == AdministratorsSid || sid == SystemSid || sid == TrustedInstallerSid;

        /// <summary>
        /// Makes <paramref name="path"/> a sound folder and says whether it
        /// is one now. A sound folder is left as it is. Anything else of that
        /// name is renamed to <c>&lt;name&gt;.untrusted-&lt;process&gt;-&lt;time&gt;</c>
        /// beside it, unopened, and a new folder is made with
        /// <see cref="Sddl"/>. A folder that another start with rights made
        /// in the meantime is judged and taken when sound. Only a process
        /// with rights can do it.
        /// </summary>
        internal static bool MakeSound(string path) => MakeSound(path, FolderOps.Real);

        /// <summary>The steps <see cref="MakeSound(string)"/> takes on the
        /// disk, each a test's to replace.</summary>
        internal sealed class FolderOps
        {
            internal static readonly FolderOps Real = new();

            public Func<string, Verdict> Judge = ProtectedFolder.Judge;
            public Func<string, string, bool> Rename = System32Calls.Rename;
            public Func<string, string, bool> Create = System32Calls.CreateFolder;
            public Func<string> Stamp = () => Hex(System32Calls.ProcessId()) + "-" + Hex(System32Calls.TickCount());
        }

        /// <summary>The name a folder that is not sound is moved to, beside
        /// it.</summary>
        internal static string AsideName(string path, string stamp, int attempt)
            => path + ".untrusted-" + stamp + "-" + Hex((ulong)attempt);

        internal static bool MakeSound(string path, FolderOps ops)
        {
            if (string.IsNullOrEmpty(path)) return false;
            const int Attempts = 4;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                Verdict verdict = ops.Judge(path);
                if (verdict == Verdict.Sound) return true;
                if (verdict != Verdict.NotThere && !ops.Rename(path, AsideName(path, ops.Stamp(), attempt))) continue;
                // Made here, or by another start with rights in the meantime.
                ops.Create(path, Sddl);
                if (ops.Judge(path) == Verdict.Sound) return true;
            }
            return false;
        }

        /// <summary>A number in hex digits, written here since number
        /// formatting reads the user's culture through a <c>DllImport</c>
        /// call.</summary>
        internal static string Hex(ulong value)
        {
            Span<char> digits = stackalloc char[16];
            int at = digits.Length;
            do
            {
                int d = (int)(value & 0xF);
                digits[--at] = (char)(d < 10 ? '0' + d : 'a' + d - 10);
                value >>= 4;
            } while (value != 0);
            return new string(digits.Slice(at));
        }

        /// <summary>Whether the folders a start with rights unpacks into are
        /// sound now: the root and its Natives folder.</summary>
        internal static bool NativesAreSound()
        {
            string root = Root, natives = Natives;
            return natives.Length != 0 && JudgeParent(root) == Verdict.Sound
                && Judge(root) == Verdict.Sound && Judge(natives) == Verdict.Sound;
        }

        /// <summary>Makes the root and its Natives folder sound, in that
        /// order, and says whether both are.</summary>
        internal static bool MakeNativesSound()
        {
            string root = Root, natives = Natives;
            return natives.Length != 0 && JudgeParent(root) == Verdict.Sound && MakeSound(root) && MakeSound(natives);
        }
    }
}
