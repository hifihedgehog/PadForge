using System;
using System.Text;
using Microsoft.Win32;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// The machine's own environment variables, as Windows keeps them under
    /// HKEY_LOCAL_MACHINE, for a path PadForge loads a library from (#600).
    /// The process's variables come from the user's part of the registry too,
    /// and a user variable of the same name wins, so any program of the user
    /// could point a library path PadForge loads with its rights at a folder
    /// of its own.
    /// </summary>
    public static class MachineEnvironment
    {
        private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

        /// <summary>Test seam: a variable's machine value. Null in production.</summary>
        public static Func<string, string> GetSeam;

        /// <summary>The machine's value of a variable as written, or null.
        /// SystemRoot, SystemDrive, the Program Files and Common Files
        /// folders and ProgramData, which Windows sets for every process and
        /// keeps in no machine variable, are read from Windows' own folder
        /// paths.</summary>
        public static string Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (GetSeam != null) return GetSeam(name);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (name.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase) || name.Equals("windir", StringComparison.OrdinalIgnoreCase))
                return windows;
            if (name.Equals("SystemDrive", StringComparison.OrdinalIgnoreCase))
                return windows.Length >= 2 ? windows.Substring(0, 2) : null;
            if (name.Equals("ProgramFiles", StringComparison.OrdinalIgnoreCase) || name.Equals("ProgramW6432", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (name.Equals("ProgramFiles(x86)", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (name.Equals("CommonProgramFiles", StringComparison.OrdinalIgnoreCase) || name.Equals("CommonProgramW6432", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            if (name.Equals("CommonProgramFiles(x86)", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
            if (name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) || name.Equals("ALLUSERSPROFILE", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(Key);
                return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Fills in each %NAME% from the machine's values, a few
        /// levels deep. A name the machine does not have is left as it
        /// is.</summary>
        public static string Expand(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('%') < 0) return text;
            for (int depth = 0; depth < 4 && text.IndexOf('%') >= 0; depth++)
            {
                var result = new StringBuilder(text.Length);
                int at = 0;
                bool changed = false;
                while (at < text.Length)
                {
                    int open = text.IndexOf('%', at);
                    int close = open < 0 ? -1 : text.IndexOf('%', open + 1);
                    if (open < 0 || close < 0)
                    {
                        result.Append(text, at, text.Length - at);
                        break;
                    }
                    result.Append(text, at, open - at);
                    string value = close > open + 1 ? Get(text.Substring(open + 1, close - open - 1)) : null;
                    if (value != null)
                    {
                        result.Append(value);
                        changed = true;
                        at = close + 1;
                    }
                    else
                    {
                        result.Append('%');
                        at = open + 1;
                    }
                }
                text = result.ToString();
                if (!changed) break;
            }
            return text;
        }
    }
}
