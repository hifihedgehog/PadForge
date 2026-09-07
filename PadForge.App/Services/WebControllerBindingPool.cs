using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services
{
    internal sealed class WebControllerBindingPool
    {
        private sealed class PortState
        {
            public readonly object Gate = new();
            public int Owners;
            public bool Ready;
        }

        private readonly ConcurrentDictionary<int, PortState> _ports = new();
        private readonly Func<int, bool> _ensure;
        private readonly Action<int> _remove;
        private readonly Action<Action> _queue;

        internal WebControllerBindingPool(Func<int, bool> ensure, Action<int> remove,
            Action<Action> queue = null)
        {
            _ensure = ensure;
            _remove = remove;
            _queue = queue ?? (action => { _ = Task.Run(action); });
        }

        internal IDisposable Acquire(int port)
        {
            if (port < 1 || port > 65535) return null;
            var state = _ports.GetOrAdd(port, _ => new PortState());
            Interlocked.Increment(ref state.Owners);
            bool ready = false;
            try
            {
                lock (state.Gate)
                {
                    if (!state.Ready) state.Ready = _ensure(port);
                    ready = state.Ready;
                }
                return ready ? new Lease(this, state, port) : null;
            }
            finally
            {
                if (!ready) Release(state, port);
            }
        }

        private void Release(PortState state, int port)
        {
            if (Interlocked.Decrement(ref state.Owners) != 0) return;
            // Stop does not wait for netsh or another startup's certificate work.
            _queue(() =>
            {
                lock (state.Gate)
                {
                    if (Volatile.Read(ref state.Owners) != 0) return;
                    try { _remove(port); }
                    catch { /* Binding cleanup is best effort. */ }
                    finally { state.Ready = false; }
                }
            });
        }

        private sealed class Lease : IDisposable
        {
            private WebControllerBindingPool _owner;
            private readonly PortState _state;
            private readonly int _port;
            public Lease(WebControllerBindingPool owner, PortState state, int port)
            { _owner = owner; _state = state; _port = port; }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_state, _port);
        }
    }
}
