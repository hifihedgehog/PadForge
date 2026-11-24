using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Common;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// The message a start with rights shows when it cannot go on safely
    /// (#597): it could not make its protected folder, or could not start
    /// itself again from it (<see cref="ProtectedStart"/>). Before, such a
    /// start left with its exit code alone and no word.
    ///
    /// <para>That start may not trust the folder its native libraries were
    /// unpacked into, and the runtime's own calls into Windows look there
    /// first. Measured with planted copies of System32's libraries in a
    /// single-file probe's unpack folder: reading one string through
    /// ResourceManager loaded the planted kernel32.dll, where System32Calls
    /// alone loaded none. So the start with rights reads no string itself.
    /// It starts PadForge once more without its rights
    /// (<see cref="System32Calls.StartWithoutRights"/>), with
    /// <see cref="Switch"/>, and waits for it. That copy runs at medium
    /// integrity, as any program of the user, so nothing it loads gains a
    /// right. It picks the language the way the update helper's windows do
    /// (App.OnStartup: the Windows display language through
    /// <see cref="LanguageCatalog.InterfaceCulture"/>) and shows the box
    /// through System32's user32. Only when that copy cannot be started does
    /// the start with rights show the English text itself, from constants.
    /// A run as SYSTEM, or in session 0, has no one to show a box to and
    /// shows none: a box there would wait for an answer forever.</para>
    /// </summary>
    internal static class StartFailure
    {
        internal const string Switch = "--start-failed";

        internal enum Kind
        {
            /// <summary>The protected folder could not be made.</summary>
            NoFolder,

            /// <summary>The copy that unpacks into it could not be
            /// started.</summary>
            NoRestart,
        }

        /// <summary>Test seam: takes the title and text in place of the box.
        /// Null in production.</summary>
        internal static Action<string, string> ShowSeam;

        internal const string FolderKey = "Start_NoProtectedFolder_Format";
        internal const string RestartKey = "Start_NoProtectedRestart_Format";

        /// <summary>The English text, for a key no translation carries and for
        /// a start that could not hand the message to a copy without
        /// rights.</summary>
        internal const string FolderEnglish =
            "PadForge could not make its protected folder, {0}. PadForge keeps the libraries it runs with administrator rights there, and it cannot start safely without it.";
        internal const string RestartEnglish =
            "PadForge could not start itself again from its protected folder, {0}. PadForge runs with administrator rights only from there, so it did not start.";

        private static string ArgumentOf(Kind kind) => kind == Kind.NoFolder ? "folder" : "restart";

        /// <summary>Whether the command line is the copy's: the switch and
        /// what failed.</summary>
        internal static bool IsRun(string[] args)
            => args is { Length: 2 } && args[0] == Switch && (args[1] == "folder" || args[1] == "restart");

        /// <summary>
        /// The start with rights: hands the message to a copy without rights
        /// and returns once it is closed. Every call here is one of
        /// <see cref="System32Calls"/>.
        /// </summary>
        internal static void Show(Kind kind)
        {
            if (System32Calls.IsSystem() || System32Calls.SessionId() is 0 or uint.MaxValue) return;
            string exe = System32Calls.OwnExe();
            string[] variables = System32Calls.Variables();
            int slash = exe == null ? -1 : exe.LastIndexOf('\\');
            if (variables != null && slash > 0)
            {
                IntPtr copy = System32Calls.StartWithoutRights(exe, "\"" + exe + "\" " + Switch + " " + ArgumentOf(kind),
                    CopyBlock(System32Calls.MachineVariables(), variables), exe.Substring(0, slash + 1));
                if (copy != IntPtr.Zero)
                {
                    System32Calls.WaitFor(copy);
                    System32Calls.Close(copy);
                    return;
                }
            }
            System32Calls.ErrorBox((kind == Kind.NoFolder ? FolderEnglish : RestartEnglish).Replace("{0}", ProtectedFolder.Root, StringComparison.Ordinal), "PadForge");
        }

        /// <summary>The copy's variables: those every start with rights gets
        /// (<see cref="RightsVariables.Build"/>), the system's from
        /// <paramref name="machine"/> and the user's and PadForge's from
        /// <paramref name="own"/>, this process's, but no unpack folder, so
        /// the copy unpacks where a start without rights does, and no
        /// handover. The copy runs without rights, and it still gets no
        /// variable that loads code. The block ends in two null
        /// characters.</summary>
        internal static string CopyBlock(IReadOnlyList<string> machine, IReadOnlyList<string> own)
            => RightsVariables.ToBlock(RightsVariables.Build(machine, own, natives: null));

        /// <summary>
        /// The copy without rights (Program.Main, before WPF): the box in the
        /// Windows display language. A process at high integrity, or SYSTEM,
        /// shows nothing: the text is never read with rights. 0 when shown.
        /// </summary>
        internal static int RunInCopy(string[] args)
        {
            if (!IsRun(args)) return 2;
            if (System32Calls.IsSystem() || System32Calls.IntegrityRid() >= System32Calls.HighIntegrityRid) return 3;
            var kind = args[1] == "folder" ? Kind.NoFolder : Kind.NoRestart;
            CultureInfo windows = CultureInfo.CurrentUICulture;
            var (title, text) = Words(kind, LanguageCatalog.InterfaceCulture(windows), ProtectedFolder.Root);
            if (ShowSeam != null) ShowSeam(title, text);
            else System32Calls.ErrorBox(text, title);
            return 0;
        }

        /// <summary>The box's title and text in <paramref name="culture"/>,
        /// with <paramref name="folder"/> named. English when the language
        /// has no translation of the text, or its translation lost its place
        /// for the folder.</summary>
        internal static (string Title, string Text) Words(Kind kind, CultureInfo culture, string folder)
        {
            string title = Lookup("Common_PadForge", culture) ?? "PadForge";
            string format = Lookup(kind == Kind.NoFolder ? FolderKey : RestartKey, culture);
            if (!HoldsOnePlace(format)) format = kind == Kind.NoFolder ? FolderEnglish : RestartEnglish;
            return (title, format.Replace("{0}", folder ?? string.Empty, StringComparison.Ordinal));
        }

        private static string Lookup(string key, CultureInfo culture)
        {
            try
            {
                string text = Strings.ResourceManager.GetString(key, culture ?? CultureInfo.InvariantCulture);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when a format has the folder's place once and no
        /// other brace.</summary>
        internal static bool HoldsOnePlace(string format)
        {
            if (string.IsNullOrEmpty(format)) return false;
            int at = format.IndexOf("{0}", StringComparison.Ordinal);
            if (at < 0 || format.IndexOf("{0}", at + 3, StringComparison.Ordinal) >= 0) return false;
            string rest = format.Remove(at, 3);
            return rest.IndexOf('{') < 0 && rest.IndexOf('}') < 0;
        }
    }
}
