using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>Where the OpenRGB worker looks for the SDK server: an address
    /// on this PC and a port.</summary>
    internal readonly record struct OpenRgbServerAddress(IPAddress Host, int Port)
    {
        /// <summary>The server's defaults, OPENRGB_SDK_HOST and
        /// OPENRGB_SDK_PORT (NetworkProtocol.h:30-33 and 40-44).</summary>
        public static OpenRgbServerAddress Default => new(IPAddress.Loopback, OpenRgbProtocol.DefaultPort);
    }

    /// <summary>
    /// The address OpenRGB's SDK server listens on (#557), read the way
    /// OpenRGB decides it (ResourceManager.cpp:1325-1352): the --server-host
    /// and --server-port arguments OpenRGB was started with, where they were
    /// given, else the "default_host" and "default_port" of the "Server"
    /// settings in OpenRGB.json, else the defaults, 127.0.0.1 and 6742
    /// (NetworkProtocol.h:30-33 and 40-44). OpenRGB.json is in the directory
    /// a --config argument names, in the working directory under
    /// --localconfig, else in OpenRGB under %APPDATA%
    /// (ResourceManager.cpp:1109-1153 and cli.cpp:1452-1475).
    ///
    /// <para>Windows gives another process's working directory only out of
    /// that process's memory, which PadForge does not read. Under
    /// --localconfig the program's own folder stands in for it, the folder
    /// Explorer and a shortcut start a portable OpenRGB in. A server started
    /// from anywhere else is not found, and the Lighting tab says which port
    /// was tried.</para>
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
        private const string LocalConfigOption = "--localconfig";
        private const string HostOption = "--server-host";

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

        /// <summary>The directory a --config argument names, or under
        /// --localconfig the folder of the program, the first argument, or
        /// null. OpenRGB reads its arguments in order, so the last of the two
        /// sets it (cli.cpp:1452-1475).</summary>
        public static string ConfigFromArguments(IReadOnlyList<string> args)
        {
            if (args == null) return null;
            string config = null;
            for (int i = 1; i < args.Count; i++)
            {
                if (string.Equals(args[i], LocalConfigOption, StringComparison.Ordinal))
                    config = Path.GetDirectoryName(args[0]);
                else if (i + 1 < args.Count && string.Equals(args[i], ConfigOption, StringComparison.Ordinal))
                    config = args[++i];
            }
            return string.IsNullOrWhiteSpace(config) ? null : config;
        }

        /// <summary>The host a --server-host argument names, or null
        /// (cli.cpp:1599-1614).</summary>
        public static string HostFromArguments(IReadOnlyList<string> args)
        {
            if (args == null) return null;
            string host = null;
            for (int i = 0; i + 1 < args.Count; i++)
                if (string.Equals(args[i], HostOption, StringComparison.Ordinal)) host = args[i + 1];
            return string.IsNullOrWhiteSpace(host) ? null : host;
        }

        /// <summary>The "default_host" of the "Server" settings, or null
        /// (ResourceManager.cpp:1330-1339).</summary>
        public static string HostFromSettings(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Server", out var server)
                    && server.ValueKind == JsonValueKind.Object
                    && server.TryGetProperty("default_host", out var host)
                    && host.ValueKind == JsonValueKind.String)
                    return string.IsNullOrWhiteSpace(host.GetString()) ? null : host.GetString();
            }
            catch (JsonException) { }
            return null;
        }

        /// <summary>The address to reach a server bound to
        /// <paramref name="host"/> at: loopback for an empty host and for a
        /// host that names no address of this PC. OpenRGB resolves its host
        /// with getaddrinfo and binds each answer (NetworkServer.cpp:492-510),
        /// so a wildcard takes a loopback connection of its own family.
        /// PadForge asks no name server and connects nowhere but this PC.</summary>
        public static IPAddress HostToConnect(string host, Func<IReadOnlyList<IPAddress>> own, string machineName)
        {
            if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return IPAddress.Loopback;
            var addresses = own?.Invoke() ?? Array.Empty<IPAddress>();
            if (string.Equals(host, machineName, StringComparison.OrdinalIgnoreCase))
                return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
            if (!IPAddress.TryParse(host, out var address)) return IPAddress.Loopback;
            if (address.Equals(IPAddress.Any)) return IPAddress.Loopback;
            if (address.Equals(IPAddress.IPv6Any)) return IPAddress.IPv6Loopback;
            return IPAddress.IsLoopback(address) || addresses.Contains(address) ? address : IPAddress.Loopback;
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

        /// <summary>The address OpenRGB listens on, from its command line and
        /// a reader of its settings file: each argument first, then the file
        /// in the directory --config names or in
        /// <paramref name="appData"/>\OpenRGB, then the default.
        /// <paramref name="own"/> lists this PC's addresses, asked only for a
        /// host that is none of the plain ones.</summary>
        public static OpenRgbServerAddress Resolve(string commandLine, string appData, Func<string, string> readFile,
            Func<IReadOnlyList<IPAddress>> own = null, string machineName = null)
        {
            var args = Split(commandLine);
            string directory = ConfigFromArguments(args)
                               ?? (string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "OpenRGB"));
            string text = null;
            if (directory != null)
            {
                try { text = readFile?.Invoke(Path.Combine(directory, "OpenRGB.json")); } catch { }
            }
            int port = PortFromArguments(args) ?? PortFromSettings(text) ?? OpenRgbProtocol.DefaultPort;
            return new OpenRgbServerAddress(HostToConnect(HostFromArguments(args) ?? HostFromSettings(text), own, machineName), port);
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

        /// <summary>The address of the first OpenRGB process that runs, read
        /// from its command line and its settings, or the default when none
        /// runs or nothing can be read.</summary>
        internal static OpenRgbServerAddress Find()
        {
            if (OffLimitsForTest) return OpenRgbServerAddress.Default;
            var running = RunningProcessList.Read();
            if (running == null) return OpenRgbServerAddress.Default;
            foreach (var process in running)
            {
                if (!VendorPrograms.OpenRgb.Owns(process.Name)) continue;
                return Resolve(CommandLineOf(process.Id), Environment.GetEnvironmentVariable("APPDATA"), ReadSettings,
                    OwnAddresses, Environment.MachineName);
            }
            return OpenRgbServerAddress.Default;
        }

        /// <summary>The addresses of this PC's network interfaces, a read of
        /// its own configuration that sends nothing.</summary>
        private static IReadOnlyList<IPAddress> OwnAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .ToList();
            }
            catch
            {
                return Array.Empty<IPAddress>();
            }
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
