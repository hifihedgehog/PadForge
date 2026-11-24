using System.Security.Principal;
using System.Text;

namespace PadForge.PluginProbe
{
    /// <summary>
    /// The tests' plugin helper. <c>kit</c> runs the kit's plugin.
    /// <c>owner &lt;pipe&gt;</c> prints what the kit's check of who serves
    /// a pipe says of that pipe, by its own name and as if it bore
    /// PadForge's.
    /// <c>rights</c> writes, to the file its PADFORGE_PROBE_REPORT variable
    /// names, whether it has administrator rights, the key and pipe name
    /// PadForge gave it and the probe variable the test set in its own
    /// environment, then waits a minute, so the test sees it end only when
    /// its job is closed.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static class Probe
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "kit") return PadForgePluginKit.Program.Main();
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
                .Append("folder=").Append(Environment.CurrentDirectory).Append('\n')
                .Append("done\n");
            File.WriteAllText(report + ".part", text.ToString());
            File.Move(report + ".part", report, overwrite: true);
            Thread.Sleep(60000);
            return 0;
        }
    }
}
