using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services;

namespace PadForge.Tests
{
    public sealed class WebControllerStartupLifetimeTests
    {
        [Fact]
        public async Task CanceledPreparationReleasesItsBindingWithoutRemovingANewerOwner()
        {
            var cleanup = new ConcurrentQueue<Action>();
            int ensured = 0, removed = 0;
            var pool = new WebControllerBindingPool(_ => { ensured++; return true; },
                _ => removed++, cleanup.Enqueue);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int oldListeners = 0;
            using var old = new WebControllerServer(port =>
            {
                var lease = pool.Acquire(port);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) { lease.Dispose(); throw new TimeoutException(); }
                return lease;
            }, () => { oldListeners++; return new Listener(); });
            using var currentListener = new Listener();
            using var current = new WebControllerServer(pool.Acquire, () => currentListener);
            var start = Task.Run(() => old.Start(18080));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                old.Dispose();
                Assert.True(current.Start(18080));
            }
            finally { release.Set(); }
            Assert.False(await start.WaitAsync(TimeSpan.FromSeconds(5)));
            Drain(cleanup);
            Assert.Equal(0, oldListeners);
            Assert.Equal(0, removed);
            Assert.Equal(1, ensured);
            Assert.True(current.IsRunning);
            current.Stop();
            Drain(cleanup);
            Assert.Equal(1, removed);
        }

        [Fact]
        public void QueuedReleaseDoesNotRemoveABindingReacquiredOnTheSamePort()
        {
            var cleanup = new ConcurrentQueue<Action>();
            int removed = 0;
            var pool = new WebControllerBindingPool(_ => true, _ => removed++, cleanup.Enqueue);
            var old = pool.Acquire(18080);
            old.Dispose();
            var current = pool.Acquire(18080);
            Drain(cleanup);
            Assert.Equal(0, removed);
            current.Dispose();
            Drain(cleanup);
            Assert.Equal(1, removed);
            current.Dispose();
            Assert.Empty(cleanup);
        }

        [Fact]
        public void ThreadStartFailureClosesTheListenerAndReleasesItsBinding()
        {
            var cleanup = new ConcurrentQueue<Action>();
            int removed = 0;
            var pool = new WebControllerBindingPool(_ => true, _ => removed++, cleanup.Enqueue);
            using var listener = new Listener();
            using var server = new WebControllerServer(pool.Acquire, () => listener,
                _ => throw new InvalidOperationException("thread start"));
            Assert.False(server.Start(18080));
            Assert.False(server.IsRunning);
            Assert.Equal(1, listener.Closes);
            Drain(cleanup);
            Assert.Equal(1, removed);
        }

        [Fact]
        public void ThrowingStatusSubscriberDoesNotRejectASuccessfulStartOrStop()
        {
            using var listener = new Listener();
            using var server = new WebControllerServer(_ => null, () => listener);
            int delivered = 0;
            server.StatusChanged += (_, _) => throw new InvalidOperationException("subscriber");
            server.StatusChanged += (_, _) => delivered++;
            Assert.True(server.Start(18080));
            Assert.True(server.IsRunning);
            Assert.Equal(0, listener.Closes);
            server.Stop();
            Assert.False(server.IsRunning);
            Assert.Equal(1, listener.Closes);
            Assert.Equal(2, delivered);
        }

        [Fact]
        public async Task ADelayedOldAcceptLoopNeverReadsTheReplacementListener()
        {
            using var old = new Listener(ignoreClose: true);
            using var current = new Listener();
            Thread oldThread = null;
            int starts = 0;
            using var server = new WebControllerServer(_ => null,
                () => starts == 0 ? old : current,
                thread => { if (starts++ == 0) oldThread = thread; thread.Start(); },
                _ => { });
            try
            {
                Assert.True(server.Start(18080));
                Assert.True(old.ReadEntered.Wait(TimeSpan.FromSeconds(5)));
                server.Stop();
                Assert.True(server.Start(18081));
                Assert.True(current.ReadEntered.Wait(TimeSpan.FromSeconds(5)));
                old.Release();
                Assert.True(await Task.Run(() => oldThread.Join(5000)));
                Assert.Equal(1, old.Reads);
                Assert.Equal(1, current.Reads);
                Assert.True(server.IsRunning);
            }
            finally { old.Release(); current.Release(); }
        }

        [Fact]
        public async Task StopDuringBindClosesOnlyTheUnpublishedListener()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var old = new Listener(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            });
            using var current = new Listener();
            int created = 0;
            using var server = new WebControllerServer(_ => null,
                () => Interlocked.Increment(ref created) == 1 ? old : current);
            var start = Task.Run(() => server.Start(18080));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                server.Stop();
                Assert.True(server.Start(18081));
            }
            finally { release.Set(); }
            Assert.False(await start.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, old.Closes);
            Assert.Equal(0, current.Closes);
            Assert.True(server.IsRunning);
        }

        [Fact]
        public void StaleHandlerCannotAllocateCountersOrPublishRunningStatus()
        {
            using var first = new Listener();
            using var second = new Listener();
            int created = 0;
            using var server = new WebControllerServer(_ => null,
                () => ++created == 1 ? first : second);
            int statuses = 0;
            server.StatusChanged += (_, _) => statuses++;
            Assert.True(server.Start(18080));
            long old = server.Generation;
            Assert.Equal(1, server.AllocateClientNumber(old, "one", "gamepad"));
            server.Stop();
            Assert.True(server.Start(18080));
            long current = server.Generation;
            Assert.Equal(1, server.AllocateClientNumber(current, "new", "gamepad"));
            Assert.Null(server.AllocateClientNumber(old, "stale", "gamepad"));
            Assert.Equal(2, server.AllocateClientNumber(current, "next", "gamepad"));
            int before = statuses;
            server.PublishSessionStatus(old, true);
            server.PublishSessionStatus(current, false);
            Assert.Equal(before, statuses);
            server.PublishSessionStatus(current, true);
            Assert.Equal(before + 1, statuses);
            server.Stop();
            before = statuses;
            server.PublishSessionStatus(current, true);
            Assert.Equal(before, statuses);
        }

        [Fact]
        public void AStatusKeepsItsOriginGenerationWhenAnEarlierSubscriberStopsTheServer()
        {
            using var listener = new Listener();
            using var server = new WebControllerServer(_ => null, () => listener);
            bool stopNext = false;
            var received = new System.Collections.Generic.List<WebControllerServer.Status>();
            server.StatusChanged += (_, _) =>
            {
                if (!stopNext) return;
                stopNext = false;
                server.Stop();
            };
            server.StatusChanged += (_, status) => received.Add(status);
            Assert.True(server.Start(18080));
            long origin = server.Generation;
            received.Clear();
            stopNext = true;
            server.PublishSessionStatus(origin, true);
            Assert.False(server.IsRunning);
            Assert.Equal(2, received.Count);
            Assert.Equal(server.Generation, received[0].Generation);
            Assert.Equal(origin, received[1].Generation);
            Assert.NotEqual(server.Generation, received[1].Generation);
        }

        [Fact]
        public void FailedEnsureStillRemovesAnOwnedPartiallyInstalledBinding()
        {
            bool installed = false;
            int removed = 0;
            var cleanup = new ConcurrentQueue<Action>();
            var pool = new WebControllerBindingPool(_ => { installed = true; return false; },
                _ => { installed = false; removed++; }, cleanup.Enqueue);
            Assert.Null(pool.Acquire(18080));
            Assert.True(installed);
            Assert.Single(cleanup);
            Drain(cleanup);
            Assert.False(installed);
            Assert.Equal(1, removed);
        }

        [Fact]
        public void RemovalFailureDoesNotEscapeTheQueuedCleanupOrKeepTheReadyCache()
        {
            int ensured = 0, removed = 0;
            var cleanup = new ConcurrentQueue<Action>();
            var pool = new WebControllerBindingPool(_ => { ensured++; return true; },
                _ => { removed++; throw new InvalidOperationException("cleanup"); }, cleanup.Enqueue);
            var first = pool.Acquire(18080);
            first.Dispose();
            Drain(cleanup);
            Assert.Equal(1, removed);
            var next = pool.Acquire(18080);
            Assert.NotNull(next);
            Assert.Equal(2, ensured);
            next.Dispose();
            Drain(cleanup);
            Assert.Equal(2, removed);
        }

        [Fact]
        public void HttpsFallbackSurvivesFailureToCloseTheRejectedListener()
        {
            var cleanup = new ConcurrentQueue<Action>();
            var pool = new WebControllerBindingPool(_ => true, _ => { }, cleanup.Enqueue);
            using var rejected = new Listener(() => throw new HttpListenerException(),
                close: () => throw new InvalidOperationException("close"));
            using var fallback = new Listener();
            int created = 0;
            using var server = new WebControllerServer(pool.Acquire, () => ++created == 1 ? rejected : fallback);
            Assert.True(server.Start(18080));
            Assert.False(server.IsHttps);
            Assert.True(server.IsRunning);
            Assert.Equal(1, rejected.Closes);
            Assert.Equal(0, fallback.Closes);
            Drain(cleanup);
            server.Stop();
            Assert.Equal(1, fallback.Closes);
        }

        [Fact]
        public void WebSocketConnectNotificationRemainsInsideTheGenerationCheckedRegistration()
        {
            var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "PadForge.sln"))) root = root.Parent;
            Assert.NotNull(root);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName,
                "PadForge.App", "Services", "WebControllerServer.cs"));
            int guard = source.IndexOf("if (!IsServing(generation) || lifetime.IsCancellationRequested)", StringComparison.Ordinal);
            int register = source.IndexOf("_clients[compositeKey] = session;", guard, StringComparison.Ordinal);
            int connected = source.IndexOf("DeviceConnected?.Invoke(device)", register, StringComparison.Ordinal);
            int end = source.IndexOf("// Once the session is registered", connected, StringComparison.Ordinal);
            Assert.True(guard >= 0 && register > guard && connected > register && end > connected);
            string between = source.Substring(register, connected - register);
            Assert.DoesNotContain("}", between);
        }

        private static void Drain(ConcurrentQueue<Action> queue)
        {
            while (queue.TryDequeue(out var action)) action();
        }

        private sealed class Listener : IWebControllerListener, IDisposable
        {
            private readonly Action _start;
            private readonly Action _close;
            private readonly bool _ignoreClose;
            private readonly ManualResetEventSlim _release = new();
            public readonly ManualResetEventSlim ReadEntered = new();
            public Listener(Action start = null, bool ignoreClose = false, Action close = null)
            { _start = start; _ignoreClose = ignoreClose; _close = close; }
            public bool IsListening { get; private set; }
            public int Closes;
            public int Reads;
            public void Start(string prefix) { _start?.Invoke(); IsListening = true; }
            public HttpListenerContext GetContext()
            {
                Interlocked.Increment(ref Reads);
                ReadEntered.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
                throw new HttpListenerException();
            }
            public void Release() => _release.Set();
            public void Stop() { IsListening = false; if (!_ignoreClose) Release(); }
            public void Close() { Interlocked.Increment(ref Closes); Stop(); _close?.Invoke(); }
            public void Dispose() { Release(); }
        }
    }
}
