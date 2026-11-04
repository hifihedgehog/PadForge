using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The port OpenRGB's SDK server listens on (#557), read the way OpenRGB
    /// decides it (ResourceManager.cpp:1325-1352): the --server-port
    /// argument OpenRGB was started with, where one was given, else the
    /// "default_port" of the "Server" settings in OpenRGB.json, else the
    /// default, 6742 (NetworkProtocol.h:40-44). OpenRGB.json is in the
    /// directory a --config argument names, else in OpenRGB under
    /// %APPDATA% (ResourceManager.cpp:1109-1153 and cli.cpp:1461-1475).
    ///
    /// <para>Both are reads. The running process is opened with
    /// PROCESS_QUERY_LIMITED_INFORMATION alone, the right
    /// <see cref="RunningProcessList"/> opens every process with, and asked
    /// for its command line. Nothing of its memory is read.</para>
    /// </summary>
    internal static class OpenRgbServerPort
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app: OpenRGB's process is not opened and its
        /// settings file is not read.</summary>
        internal static bool OffLimitsForTest;

        private const string PortOption = "--server-port";
        private const string ConfigOption = "--config";

        /// <summary>The port an --server-port argument sets, or null. OpenRGB
        /// takes the argument after the option and keeps it only from 1024
        /// to 65535 (cli.cpp:1558-1590).</summary>
        public static int? PortFromArguments(IReadOnlyList<string> args)
        {
            if (args == null) return null;
            int? port = null;
            for (int i = 0; i + 1 < args.Count; i++)
            {
                if (!string.Equals(args[i], PortOption, StringComparison.Ordinal)) continue;
                if (int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int value)
                    && value >= 1024 && value <= 65535)
                    port = value;
            }
            return port;
        }

        /// <summary>The directory a --config argument names, or null
        /// (cli.cpp:1461-1475).</summary>
        public static string ConfigFromArguments(IReadOnlyList<string> args)
        {
            if (args == null) return null;
            string config = null;
            for (int i = 0; i + 1 < args.Count; i++)
                if (string.Equals(args[i], ConfigOption, StringComparison.Ordinal)) config = args[i + 1];
            return string.IsNullOrWhiteSpace(config) ? null : config;
        }

        /// <summary>The "default_port" of the "Server" settings, or null
        /// when the text holds none OpenRGB would listen on. OpenRGB's
        /// settings schema allows 0 to 65535 (ResourceManager.cpp:305-309),
        /// and a port of 0 is no port a client can reach.</summary>
        public static int? PortFromSettings(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Server", out var server)
                    && server.ValueKind == JsonValueKind.Object
                    && server.TryGetProperty("default_port", out var port)
                    && port.ValueKind == JsonValueKind.Number
                    && port.TryGetInt32(out int value)
                    && value > 0 && value <= 65535)
                    return value;
            }
            catch (JsonException) { }
            return null;
        }

        /// <summary>The port OpenRGB listens on, from its command line and
        /// a reader of its settings file: the argument first, then the
        /// file in the directory --config names or in
        /// <paramref name="appData"/>\OpenRGB, then the default.</summary>
        public static int Resolve(string commandLine, string appData, Func<string, string> readFile)
        {
            var args = Split(commandLine);
            if (PortFromArguments(args) is int fromArguments) return fromArguments;
            string directory = ConfigFromArguments(args)
                               ?? (string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "OpenRGB"));
            if (directory != null)
            {
                string text = null;
                try { text = readFile?.Invoke(Path.Combine(directory, "OpenRGB.json")); } catch { }
                if (PortFromSettings(text) is int fromSettings) return fromSettings;
            }
            return OpenRgbProtocol.DefaultPort;
        }

        /// <summary>A command line split into arguments by Windows' own
        /// rules (CommandLineToArgvW), the program's path first. Empty for
        /// none.</summary>
        public static List<string> Split(string commandLine)
        {
            var args = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return args;
            IntPtr argv = CommandLineToArgvW(commandLine, out int count);
            if (argv == IntPtr.Zero) return args;
            try
            {
                for (int i = 0; i < count; i++)
                    args.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty);
            }
            finally
            {
                LocalFree(argv);
            }
            return args;
        }

        /// <summary>The port of the first OpenRGB process that runs, read
        /// from its command line and its settings, or the default when none
        /// runs or nothing can be read.</summary>
        internal static int Find()
        {
            if (OffLimitsForTest) return OpenRgbProtocol.DefaultPort;
            var running = RunningProcessList.Read();
            if (running == null) return OpenRgbProtocol.DefaultPort;
            foreach (var process in running)
            {
                if (!VendorPrograms.OpenRgb.Owns(process.Name)) continue;
                return Resolve(CommandLineOf(process.Id), Environment.GetEnvironmentVariable("APPDATA"), ReadSettings);
            }
            return OpenRgbProtocol.DefaultPort;
        }

        /// <summary>A settings file's text, or null when it cannot be
        /// read.</summary>
        internal static string ReadSettings(string path)
        {
            if (OffLimitsForTest) return null;
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        /// <summary>A process's command line, asked of Windows with a
        /// limited query (ProcessCommandLineInformation, Windows 8.1 and
        /// later), or null.</summary>
        internal static string CommandLineOf(uint processId)
        {
            if (OffLimitsForTest) return null;
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return null;
            try
            {
                int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
                if (needed <= 0 || needed > 1 << 20 || (status != StatusInfoLengthMismatch && status != 0)) return null;
                IntPtr buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, needed, out _);
                    if (status != 0) return null;
                    // A UNICODE_STRING: its length in bytes, its capacity,
                    // and a pointer to the text, which follows it in the
                    // buffer.
                    int length = (ushort)Marshal.ReadInt16(buffer);
                    IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessCommandLineInformation = 60;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information,
            int length, out int returnLength);

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
