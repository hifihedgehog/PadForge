using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The real-host check of the web door (#600 item 52), run by hand: with
    /// PADFORGE_WEB_HARNESS set to a file, PadForge's host, its web door and
    /// the web helper run on 127.0.0.1 for PADFORGE_WEB_HARNESS_SECONDS
    /// (60 when not given), and the file gets the endpoint's address, so an
    /// assistant host on this PC can add it and run its health check against
    /// it. Without the variable it does nothing. It binds loopback only and
    /// starts no process.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebHostHarness
    {
        [Fact]
        public async Task ServeTheWebDoor_ForAHostOnThisPC()
        {
            string file = Environment.GetEnvironmentVariable("PADFORGE_WEB_HARNESS");
            if (string.IsNullOrEmpty(file)) return;
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("PADFORGE_WEB_HARNESS_SECONDS"), out int s) && s is > 0 and <= 600 ? s : 60;
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            door.TurnOn();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helper = Task.Run(() => WebHelper.RunAsync(launcher.Started[^1].Variables[WebHelper.KeyVariable], rig.PipeName, stop.Token,
                (url, ct) => Task.FromResult<string>(null), port => listening.TrySetResult(port),
                (asked, reply) =>
                {
                    // What the host asked and what it was answered, for the
                    // report: never a token, which no request here carries.
                    lock (file) File.AppendAllText(file + ".log", asked.Method + " " + asked.Path + " Host=" + asked.Header("Host")
                        + " Accept=" + asked.Header("Accept") + " MCP-Protocol-Version=" + asked.Header("MCP-Protocol-Version")
                        + " -> " + reply.Status + (reply.Headers.Find(h => h.Key == "WWW-Authenticate").Value is { } challenge ? " WWW-Authenticate: " + challenge : string.Empty)
                        + Environment.NewLine);
                }));
            int bound = await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.WriteAllText(file, WebAddress.Loopback(bound) + WebAddress.McpPath);
            try { await helper; } catch (OperationCanceledException) { }
            Assert.Empty(rig.Host.Approvals.Pending);
        }
    }
}
