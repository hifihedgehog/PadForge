using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// Which program opens a script plugin's kind of file for the user whose
    /// desktop this is, read in that user's part of the registry by the
    /// shell's SID (as WindowsLightingState reads its switches). PadForge
    /// runs with rights, and a standard user who typed an administrator's
    /// password at its prompt runs it as that administrator, whose
    /// HKEY_CURRENT_USER, and so whose AssocQueryString, names the
    /// administrator's programs. With no shell, or no hive loaded for its
    /// user, this process's own hive is read.
    ///
    /// <para>The open command comes from the user's choice
    /// (Explorer\FileExts\&lt;ext&gt;\UserChoiceLatest or UserChoice
    /// ProgId), else the kind's
    /// default ProgID in the user's classes, else the machine's, and then
    /// the ProgID's shell\open\command in the user's classes, else the
    /// machine's: the order the merged HKEY_CLASSES_ROOT gives the user's
    /// own key over the machine's. A Python file with no program is run
    /// with the py launcher, as CPython's "Using Python on Windows"
    /// directs: the Python install manager's alias in the user's
    /// WindowsApps folder, the legacy launcher installed for the user, or
    /// the one installed for every user in the Windows folder.</para>
    /// </summary>
    internal sealed class PluginPrograms
    {
        /// <summary>Reads one string value: in the user's hive when
        /// <c>user</c> is true, else in HKEY_LOCAL_MACHINE, under
        /// <c>subKey</c>, <c>name</c> null for the key's default. Null when
        /// it is not there. Throws when the registry cannot be read.</summary>
        internal delegate string RegistryRead(bool user, string subKey, string name);

        private readonly RegistryRead _read;
        private readonly Func<string, bool> _exists;
        private readonly Func<string, string> _machineVariable;

        internal PluginPrograms(RegistryRead read, Func<string, bool> exists = null, Func<string, string> machineVariable = null)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _exists = exists ?? File.Exists;
            _machineVariable = machineVariable ?? Environment.GetEnvironmentVariable;
        }

        /// <summary>The desktop's user's programs, read in that user's hive
        /// by the shell's SID.</summary>
        internal static PluginPrograms OfShellUser()
        {
            string sid = PadForge.Common.Input.Peripherals.WindowsLightingState.ShellUserSid();
            return new PluginPrograms((user, subKey, name) =>
            {
                if (!user)
                {
                    using var machine = Registry.LocalMachine.OpenSubKey(subKey);
                    return Text(machine, name);
                }
                if (sid != null)
                {
                    using var hive = Registry.Users.OpenSubKey(sid);
                    if (hive != null)
                    {
                        using var key = hive.OpenSubKey(subKey);
                        return Text(key, name);
                    }
                }
                using var own = Registry.CurrentUser.OpenSubKey(subKey);
                return Text(own, name);
            });
        }

        /// <summary>A string value as it is kept, a variable in it left for
        /// <see cref="Expand"/>, or null.</summary>
        private static string Text(RegistryKey key, string name)
            => key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

        /// <summary>The command that opens a file of <paramref name="extension"/>
        /// for the user, its variables expanded, or null when none is
        /// registered.</summary>
        internal string OpenCommand(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return null;
            string choices = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension;
            string progId = Value(_read(true, choices + @"\UserChoiceLatest", "ProgId"))
                            ?? Value(_read(true, choices + @"\UserChoice", "ProgId"))
                            ?? Value(_read(true, @"Software\Classes\" + extension, null))
                            ?? Value(_read(false, @"Software\Classes\" + extension, null));
            if (progId == null) return null;
            string command = Value(_read(true, @"Software\Classes\" + progId + @"\shell\open\command", null))
                             ?? Value(_read(false, @"Software\Classes\" + progId + @"\shell\open\command", null));
            return command == null ? null : Expand(command);
        }

        /// <summary>The py launcher the user can run, <c>pyw.exe</c> for a
        /// windowed script, or null when none is there.</summary>
        internal string PyLauncher(bool windowed)
        {
            string name = windowed ? "pyw.exe" : "py.exe";
            string local = Variable("LOCALAPPDATA");
            string windows = _machineVariable("SystemRoot");
            foreach (string folder in new[]
                     {
                         local == null ? null : Path.Combine(local, "Microsoft", "WindowsApps"),
                         local == null ? null : Path.Combine(local, "Programs", "Python", "Launcher"),
                         windows,
                     })
            {
                if (folder == null) continue;
                string path = Path.Combine(folder, name);
                if (_exists(path)) return path;
            }
            return null;
        }

        /// <summary>Whether a program's file is there.</summary>
        internal bool Exists(string path) => !string.IsNullOrEmpty(path) && _exists(path);

        private static string Value(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        /// <summary>A variable as the user has it: the logon's own
        /// (Volatile Environment), the user's (Environment), then the
        /// machine's.</summary>
        private string Variable(string name)
            => Value(_read(true, "Volatile Environment", name))
               ?? Value(_read(true, "Environment", name))
               ?? Value(_machineVariable(name));

        /// <summary>A command's <c>%NAME%</c> variables, the user's first.
        /// A name with none is left as it is.</summary>
        internal string Expand(string text)
        {
            var sb = new StringBuilder(text.Length);
            int at = 0;
            while (at < text.Length)
            {
                int open = text.IndexOf('%', at);
                int close = open < 0 ? -1 : text.IndexOf('%', open + 1);
                if (close < 0)
                {
                    sb.Append(text, at, text.Length - at);
                    break;
                }
                string name = text.Substring(open + 1, close - open - 1);
                string value = name.Length > 0 && IsName(name) ? Variable(name) : null;
                if (value == null)
                {
                    // Not a variable, such as %1: kept, and the second % is
                    // looked at again as an opening one.
                    sb.Append(text, at, close - at);
                    at = close;
                    continue;
                }
                sb.Append(text, at, open - at).Append(value);
                at = close + 1;
            }
            return sb.ToString();
        }

        private static bool IsName(string name)
        {
            if (char.IsDigit(name[0]) || name[0] == '*' || name[0] == '~') return false;
            foreach (char c in name)
                if (!(char.IsLetterOrDigit(c) || c is '_' or '(' or ')')) return false;
            return true;
        }
    }
}
