using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PadForge.PluginProbe
{
    /// <summary>
    /// The tests' plugin helper. <c>kit</c> runs the kit's plugin.
    /// <c>owner &lt;pipe&gt;</c> prints what the kit's check of who serves
    /// a pipe says of that pipe, by its own name and as if it bore
    /// PadForge's.
    /// <c>rights</c> writes, to the file its PADFORGE_PROBE_REPORT variable
    /// names, whether it has administrator rights, the key and pipe name
    /// PadForge gave it, the probe variable the test set in its own
    /// environment (which a plugin never gets), its Path and its folder,
    /// whether it may open its own process again by its ID, as
    /// Process.SafeHandle does, and whether its token, read through the
    /// handle GetCurrentProcess gives, is elevated, then waits a minute, so
    /// the test sees it end only when its job is closed.
    /// <c>serve &lt;pipe&gt;</c> stands in for PadForge's side of its web
    /// helper's pipe, under a test's own name (<see cref="Serve"/>).
    /// <c>linger &lt;ms&gt;</c> leaves a child that holds its output open
    /// for that long and ends at once (<see cref="Linger"/>), and
    /// <c>sleep &lt;ms&gt;</c> is that child.
    /// <c>ask &lt;pipe&gt; &lt;line&gt;</c> sends one line to a pipe of a
    /// test's own name, as a second start without rights asks the running
    /// copy over its signal pipe, and writes whether the kit's check takes
    /// the pipe's server and the answer to the file its
    /// PADFORGE_PROBE_REPORT variable names (<see cref="Ask"/>).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static class Probe
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "kit") return PadForgePluginKit.Program.Main();
            if (args.Length > 1 && args[0] == "serve") return Serve(args[1]);
            if (args.Length > 1 && args[0] == "linger" && int.TryParse(args[1], out int lingerMs)) return Linger(lingerMs);
            if (args.Length > 1 && args[0] == "sleep" && int.TryParse(args[1], out int sleepMs))
            {
                Thread.Sleep(sleepMs);
                return 0;
            }
            if (args.Length > 2 && args[0] == "ask") return Ask(args[1], args[2]);
            if (args.Length > 1 && args[0] == "owner")
            {
                // The kit's check of who serves a pipe: for the pipe's own
                // name, and as if it bore PadForge's.
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", args[1], System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                pipe.Connect(5000);
                Console.WriteLine("own=" + (PadForge.Services.Automation.PipeOwner.IsServer(pipe.SafePipeHandle, args[1]) ? 1 : 0)
                    + " padforge=" + (PadForge.Services.Automation.PipeOwner.IsServer(pipe.SafePipeHandle, "PadForge.Automation") ? 1 : 0));
                return 0;
            }

            string? report = Environment.GetEnvironmentVariable("PADFORGE_PROBE_REPORT");
            if (string.IsNullOrEmpty(report)) return 3;
            using var identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            var text = new StringBuilder()
                .Append("privileged=").Append(Environment.IsPrivilegedProcess ? 1 : 0).Append('\n')
                .Append("administrator=").Append(admin ? 1 : 0).Append('\n')
                .Append("token=").Append(Environment.GetEnvironmentVariable("PADFORGE_PLUGIN_TOKEN")).Append('\n')
                .Append("pipe=").Append(Environment.GetEnvironmentVariable("PADFORGE_PIPE")).Append('\n')
                .Append("mine=").Append(Environment.GetEnvironmentVariable("PADFORGE_PROBE_MINE")).Append('\n')
                .Append("path=").Append(Environment.GetEnvironmentVariable("Path")).Append('\n')
                .Append("folder=").Append(Environment.CurrentDirectory).Append('\n')
                .Append("selfopen=").Append(SelfOpen()).Append('\n')
                .Append("owntoken=").Append(OwnToken()).Append('\n')
                .Append("done\n");
            File.WriteAllText(report + ".part", text.ToString());
            File.Move(report + ".part", report, overwrite: true);
            Thread.Sleep(60000);
            return 0;
        }

        /// <summary>0 when this process opens its own process again by its
        /// ID, as Process.SafeHandle does, or the error Windows gave.</summary>
        private static int SelfOpen()
        {
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                return self.SafeHandle.IsInvalid ? -1 : 0;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return e.NativeErrorCode;
            }
        }

        /// <summary>1 or 0, whether this process's token is elevated, read
        /// through the handle GetCurrentProcess gives, which needs no rights
        /// on the process. "error n" when Windows refused.</summary>
        private static string OwnToken()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out SafeAccessTokenHandle token))
                return "error " + Marshal.GetLastWin32Error();
            using (token)
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)
                    ? (elevated != 0 ? "1" : "0")
                    : "error " + Marshal.GetLastWin32Error();
            }
        }

        private const uint TokenQuery = 0x0008;
        private const int TokenElevation = 20;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int information,
            int length, out int returned);

        /// <summary>
        /// PadForge's side of its web helper's pipe, as far as the helper
        /// needs it to run: it serves one connection under a test's own name,
        /// answers the helper's hello, and sends it a state. It writes
        /// <c>serving pid=&lt;n&gt; handle=&lt;n&gt;</c>, its process and
        /// its end of the pipe, before the helper connects, then
        /// <c>connected</c>, then each line the helper sends, until the pipe
        /// closes or the test ends it. It never serves a pipe of PadForge's
        /// own names.
        /// </summary>
        private static int Serve(string name)
        {
            if (PadForge.Services.Automation.PipeOwner.IsOwnName(name)) return 2;
            // Opened for overlapped calls, as PadForge opens its own. Windows
            // runs the calls on a pipe opened for synchronous ones one at a
            // time, so the read that waits for the helper would hold every
            // other call on it, a copy of the handle in another process
            // included, until the helper wrote.
            using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            Console.WriteLine("serving pid=" + Environment.ProcessId + " handle=" + server.SafePipeHandle.DangerousGetHandle().ToInt64());
            server.WaitForConnection();
            string? hello = ReadLine(server);
            if (hello == null || !hello.StartsWith("hello kind=web-helper ", StringComparison.Ordinal)) return 3;
            Write(server, "ok padforge=0 protocol=1 web=helper\n");
            Write(server, "{\"t\":\"state\",\"armedUntil\":0,\"grants\":[],\"port\":0,\"language\":\"en-US\",\"words\":{}}\n");
            Console.WriteLine("connected");
            string? line;
            while ((line = ReadLine(server)) != null) Console.WriteLine(line);
            return 0;
        }

        /// <summary>One line to a pipe of a test's own name, and its answer,
        /// written as <c>owner=&lt;0|1&gt; reply=&lt;answer&gt;</c>. It never
        /// asks a pipe of PadForge's own names.</summary>
        private static int Ask(string name, string line)
        {
            if (PadForge.Services.Automation.PipeOwner.IsOwnName(name)) return 2;
            string? report = Environment.GetEnvironmentVariable("PADFORGE_PROBE_REPORT");
            if (string.IsNullOrEmpty(report)) return 3;
            string answer;
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.None, TokenImpersonationLevel.Identification);
                pipe.Connect(5000);
                bool taken = PadForge.Services.Automation.PipeOwner.IsServer(pipe.SafePipeHandle, name);
                Write(pipe, line + "\n");
                answer = "owner=" + (taken ? 1 : 0) + " reply=" + (ReadLine(pipe) ?? string.Empty);
            }
            catch (Exception e)
            {
                answer = "error=" + e.GetType().Name;
            }
            File.WriteAllText(report + ".part", answer + "\n");
            File.Move(report + ".part", report, overwrite: true);
            return 0;
        }

        private static string? ReadLine(Stream stream)
        {
            var line = new List<byte>();
            while (true)
            {
                int b;
                try { b = stream.ReadByte(); }
                catch (IOException) { return null; }
                if (b < 0) return null;
                if (b == '\n') return Encoding.UTF8.GetString(line.ToArray());
                line.Add((byte)b);
            }
        }

        /// <summary>
        /// A tool that leaves a child behind: it starts itself again to sleep
        /// <paramref name="ms"/> milliseconds with its own standard output and
        /// error, which the child inherits (Process.Start hands a child that
        /// redirects nothing the parent's GetStdHandle handles,
        /// Process.Windows.cs), writes one line and ends with code 0 at once.
        /// A reader of its output sees the end of it only when the child ends.
        /// </summary>
        private static int Linger(int ms)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("sleep");
            psi.ArgumentList.Add(ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var child = System.Diagnostics.Process.Start(psi);
            Console.WriteLine("left a child");
            return 0;
        }

        private static void Write(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
