using System;
using PadForge.Engine.Common;

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
    /// <para>Who may change a folder is judged by the one rule
    /// (<see cref="ChangeRule"/>) on the descriptor read here, at
    /// <see cref="ChangeRule.Level.Own"/> for the folders themselves and
    /// <see cref="ChangeRule.Level.Parent"/> for the folder that holds the
    /// root.</para>
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

        /// <summary>Owner Administrators. A protected list: Administrators
        /// and SYSTEM full control, Users read and execute, each inherited by
        /// every folder and file inside.</summary>
        internal const string Sddl = "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)";

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

        /// <summary>The parent's descriptor by the one rule at
        /// <see cref="ChangeRule.Level.Parent"/>.</summary>
        internal static Verdict JudgeParent(byte[] descriptor) => Of(ChangeRule.Judge(descriptor, ChangeRule.Level.Parent));

        /// <summary>Judges a security descriptor in its binary form by the one
        /// rule at <see cref="ChangeRule.Level.Own"/>: every right past
        /// reading counts, and so do the entries only what is made inside
        /// inherits. <paramref name="trustTrustedInstaller"/> lets
        /// TrustedInstaller own it too, as it owns the folders Windows sets
        /// up.</summary>
        internal static Verdict Judge(byte[] descriptor, bool trustTrustedInstaller)
            => Of(ChangeRule.Judge(descriptor, ChangeRule.Level.Own, trustTrustedInstaller));

        private static Verdict Of(ChangeRule.Verdict verdict) => verdict switch
        {
            ChangeRule.Verdict.Sound => Verdict.Sound,
            ChangeRule.Verdict.OtherOwner => Verdict.OtherOwner,
            ChangeRule.Verdict.NoList => Verdict.NoList,
            ChangeRule.Verdict.OthersMayChange => Verdict.OthersMayChange,
            _ => Verdict.Unreadable,
        };

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
