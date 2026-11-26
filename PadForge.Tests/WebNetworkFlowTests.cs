using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The network door's reservation and port (#600 item 53), and the
    /// client document's fetch (#600 item 11): the reservation names the
    /// desktop's user whatever SID the helper reports, the port is picked
    /// off the thread that turned the row on, and a document whose body
    /// stalls is cut at the wait.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebNetworkFlowTests
    {
        private const string Exe = @"C:\PadForge\PadForge.exe";
        private const string Shell = "S-1-5-21-1004336348-1177238915-682003330-1001";

        [Fact]
        public async Task TheReservation_NamesTheDesktopsUser_NeverASidTheHelperReports()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var ran = new List<string>();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName,
                network: new NetworkDoor(a => { lock (ran) ran.Add(a); return string.Empty; }))
            {
                HelperUserSeam = () => Shell,
            };
            store.SetNetworkPort(47321);
            door.TurnNetworkOn();
            door.Take(new JsonObject { ["t"] = "who", ["sid"] = "S-1-1-0" });
            await door.NetshDone();
            lock (ran) Assert.DoesNotContain(ran, l => l.Contains("S-1-1-0", StringComparison.Ordinal));
            Assert.False(door.State()["network"]["ready"].GetValue<bool>());
            door.Take(new JsonObject { ["t"] = "who", ["sid"] = Shell });
            await door.NetshDone();
            lock (ran) Assert.Contains("http add urlacl url=http://+:47321/ sddl=D:(A;;GX;;;" + Shell + ")", ran);
        }

        [Fact]
        public async Task ThePortIsPicked_OffTheThreadThatTurnedTheRowOn()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            int caller = Environment.CurrentManagedThreadId;
            bool inside = false;
            var picks = new List<bool>();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => Exe, rig.PipeName,
                network: new NetworkDoor(a =>
                {
                    if (a == NetworkDoor.ExcludedRangesCommand)
                        lock (picks) picks.Add(Volatile.Read(ref inside) && Environment.CurrentManagedThreadId == caller);
                    return a == NetworkDoor.ExcludedRangesCommand ? NetworkDoorTests.ExcludedSample : string.Empty;
                }));
            Volatile.Write(ref inside, true);
            door.TurnNetworkOn();
            Volatile.Write(ref inside, false);
            await door.NetshDone();
            lock (picks) Assert.Equal(new[] { false }, picks);
            Assert.InRange(store.NetworkPort, NetworkDoor.LowestPort, NetworkDoor.HighestPort);
        }

        /// <summary>A body that gives one byte, then nothing until the test
        /// lets it go.</summary>
        private sealed class StallStream : Stream
        {
            private readonly CancellationToken _release;
            private bool _gave;
            public StallStream(CancellationToken release) => _release = release;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
            {
                if (!_gave)
                {
                    _gave = true;
                    buffer.Span[0] = (byte)'{';
                    return 1;
                }
                using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _release);
                await Task.Delay(Timeout.Infinite, both.Token).ConfigureAwait(false);
                return 0;
            }
        }

        private sealed class StallHandler : HttpMessageHandler
        {
            private readonly CancellationToken _release;
            public StallHandler(CancellationToken release) => _release = release;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallStream(_release)) });
        }

        [Fact]
        public async Task AClientDocumentWhoseBodyStalls_IsCutAtTheWait()
        {
            using var release = new CancellationTokenSource();
            using var handler = new StallHandler(release.Token);
            var clock = Stopwatch.StartNew();
            try
            {
                string text = await WebClientDocuments.FetchAsync(new Uri("https://localhost/oauth/doc"), handler, TimeSpan.FromMilliseconds(300), default)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(text);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "cut at the wait");
            }
            finally
            {
                release.Cancel();
            }
        }
    }
}
