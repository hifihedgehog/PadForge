using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The helper's end of the pipe as the door sees it: every
    /// line the door writes is kept, and a read waits until the link is
    /// closed.</summary>
    internal sealed class RecordingLink : Stream
    {
        private readonly List<string> _lines = new();
        private readonly StringBuilder _partial = new();
        private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

        public IReadOnlyList<JsonObject> Messages => Lines.Select(l => JsonNode.Parse(l) as JsonObject).Where(m => m != null).ToList();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancel) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            await _closed.Task.WaitAsync(cancel).ConfigureAwait(false);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_lines)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RecordingLink));
                _partial.Append(Encoding.UTF8.GetString(buffer, offset, count));
                string text = _partial.ToString();
                int end;
                while ((end = text.IndexOf('\n')) >= 0)
                {
                    _lines.Add(text.Substring(0, end));
                    text = text.Substring(end + 1);
                }
                _partial.Clear().Append(text);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            lock (_lines) _disposed = true;
            _closed.TrySetResult(true);
            base.Dispose(disposing);
        }
    }

    /// <summary>Waits until the helper on a loopback port takes a sign-in:
    /// its authorization endpoint no longer answers that PadForge is not
    /// accepting a new web assistant.</summary>
    internal static class WebSignInWait
    {
        internal static async Task UntilTaking(int port, int ms = 5000)
        {
            using var http = new System.Net.Http.HttpClient();
            string refused = System.Net.WebUtility.HtmlEncode(PadForge.Resources.Strings.Strings.Instance.Web_SignIn_NotAccepting);
            await AssistantRig.Until(() =>
            {
                try
                {
                    using var answer = http.Send(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, WebAddress.Loopback(port) + "/authorize"));
                    return !answer.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains(refused, StringComparison.Ordinal);
                }
                catch (System.Net.Http.HttpRequestException)
                {
                    return false;
                }
            }, ms);
        }
    }

    /// <summary>A launcher that refuses its first starts, then starts.</summary>
    internal sealed class RefusingLauncher : IPluginLauncher
    {
        private int _refusals;
        public int Calls;
        public WebTestProcess Last;

        public RefusingLauncher(int refusals) => _refusals = refusals;

        public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
        {
            Interlocked.Increment(ref Calls);
            if (Interlocked.Decrement(ref _refusals) >= 0) return PluginLaunch.Refused(PluginRefusal.NoShell);
            Last = new WebTestProcess(Environment.ProcessId);
            return new PluginLaunch { Process = Last };
        }
    }

    /// <summary>
    /// The web door's flows the audit of 2026-11-26 found wrong (#600 items
    /// 11 and 53): the helper's restarts, the ChatGPT tunnel's copy, the
    /// state the helper is told after each switch and a reload, the
    /// sessions that close, the runtime key's failures, a grant's reach
    /// across a sign-in, the reservation's user, and the network port's
    /// pick off the caller's thread.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebDoorFlowTests
    {
        private const string Exe = @"C:\PadForge\PadForge.exe";

        private static JsonObject Found(params (string Key, string Path)[] tunnels)
            => new() { ["t"] = "found", ["tunnels"] = new JsonArray(tunnels.Select(t => (JsonNode)new JsonObject { ["key"] = t.Key, ["path"] = t.Path }).ToArray()) };

        private static Task<(string, string)> Offline(CancellationToken _) => Task.FromResult<(string, string)>((null, "offline"));

        /// <summary>A tunnel-client a program without rights put on the
        /// PATH is never run, and never allowed: the copy PadForge fetched
        /// and checked is the one.</summary>
        [Fact]
        public void TheChatGptTunnel_RunsTheKeptCopyAlone_NeverOneOnThePath()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            const string kept = @"C:\ProgramData\PadForge\Work\TunnelClient\v0.0.21\tunnel-client.exe";
            const string onPath = @"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\tunnel-client.exe";
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName)
            {
                KeptSeam = () => kept,
                DownloadSeam = Offline,
            };
            store.SetTunnel(TunnelClient.Key, null);
            door.Take(Found((TunnelClient.Key, onPath)));
            Assert.Equal(kept, door.TunnelClientPath);
            Assert.Equal(kept, door.State()["tunnelPath"].GetValue<string>());
            var consent = rig.Host.Sessions.Consent ?? AutomationConsent.Shared;
            Assert.DoesNotContain(consent.Programs, p => p.Name == "ChatGPT");
        }

        /// <summary>A helper that says hello and ends at once is a failure
        /// like any other: its waits grow, 2, 10 and 30 seconds, and stay at
        /// 30 while the switch is on.</summary>
        [Fact]
        public async Task AHelperThatSaysHelloAndEnds_KeepsItsCount_AndItsWaitsGrow()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            var waits = new List<int>();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName)
            {
                DelaySeam = wait => { lock (waits) waits.Add((int)wait.TotalSeconds); return Task.CompletedTask; },
            };
            door.TurnOn();
            for (int i = 0; i < 5; i++)
            {
                int started = i + 1;
                await AssistantRig.Until(() => launcher.Started.Count == started);
                using var link = new RecordingLink();
                _ = door.ServeHelperAsync(link, default);
                await AssistantRig.Until(() => link.Lines.Count > 0);
                launcher.Last.Exit(1);
            }
            await AssistantRig.Until(() => launcher.Started.Count == 6);
            lock (waits) Assert.Equal(new[] { 2, 10, 30, 30, 30 }, waits);
        }

        /// <summary>A start the launcher refuses is tried again after a
        /// pause, with no toggle.</summary>
        [Fact]
        public async Task ALaunchThatIsRefused_IsTriedAgain_WithNoToggle()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new RefusingLauncher(3);
            var waits = new List<int>();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName)
            {
                DelaySeam = wait => { lock (waits) waits.Add((int)wait.TotalSeconds); return Task.CompletedTask; },
            };
            door.TurnOn();
            await AssistantRig.Until(() => Volatile.Read(ref launcher.Calls) == 4);
            await AssistantRig.Until(() => door.HelperState == WebHelperState.Starting);
            lock (waits) Assert.Equal(new[] { 2, 10, 30 }, waits);
        }

        /// <summary>Two starts the launcher refuses, from two callers, keep
        /// one restart: the later pause stands, the earlier starts
        /// nothing.</summary>
        [Fact]
        public async Task RefusedStartsFromTwoCallers_KeepOneRestart()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new RefusingLauncher(int.MaxValue);
            var waits = new List<TaskCompletionSource<bool>>();
            using var door = new WebDoor(rig.Host, store, launcher, () => Exe, rig.PipeName, network: new NetworkDoor(_ => string.Empty))
            {
                DelaySeam = _ =>
                {
                    var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (waits) waits.Add(wait);
                    return wait.Task;
                },
            };
            door.TurnOn();
            door.TurnNetworkOn();
            Assert.Equal(2, Volatile.Read(ref launcher.Calls));
            List<TaskCompletionSource<bool>> pending;
            lock (waits) pending = waits.ToList();
            Assert.Equal(2, pending.Count);
            foreach (var wait in pending) wait.SetResult(true);
            await AssistantRig.Until(() => { lock (waits) return waits.Count >= 3; });
            await Task.Delay(300);
            Assert.Equal(3, Volatile.Read(ref launcher.Calls));
        }

        /// <summary>The kept copy is looked for once, outside the door's
        /// lock, however often the state is said.</summary>
        [Fact]
        public async Task TheKeptCopy_IsLookedForOnce()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            int looked = 0;
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName)
            {
                KeptSeam = () => { Interlocked.Increment(ref looked); return null; },
                DownloadSeam = Offline,
            };
            door.PickTunnel(TunnelClient.Key);
            await door.DownloadDone();
            door.Arm();
            door.Arm();
            Assert.Equal(1, Volatile.Read(ref looked));
        }

        /// <summary>A start with the ChatGPT tunnel picked and no copy on
        /// this PC fetches tunnel-client when the helper first says what it
        /// found.</summary>
        [Fact]
        public async Task AStartWithTheChatGptTunnelAndNoCopy_FetchesTunnelClient()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            int fetched = 0;
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName)
            {
                KeptSeam = () => null,
                DownloadSeam = _ => { Interlocked.Increment(ref fetched); return Task.FromResult<(string, string)>((null, "offline")); },
            };
            store.Load(new WebAccessData { On = true, Tunnel = TunnelClient.Key, RuntimeKey = "QkxPQg==", TunnelId = "tunnel_7f3c2a91e4b05d68" });
            door.Take(Found());
            await door.DownloadDone();
            Assert.Equal(1, Volatile.Read(ref fetched));
        }
    }
}
