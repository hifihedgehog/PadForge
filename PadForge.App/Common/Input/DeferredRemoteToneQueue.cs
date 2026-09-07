using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;

namespace PadForge.Common.Input
{
    internal sealed class RemoteToneRequest
    {
        internal readonly UserDevice Device;
        internal readonly ISdlInputDevice Source;
        internal readonly float Hz;
        internal readonly float Amplitude;
        internal readonly long UntilMs;
        internal readonly LinkEffectTicket Ticket;
        internal RemoteToneRequest(UserDevice device, float hz, float amplitude, LinkEffectTicket ticket)
        {
            Device = device;
            Source = device.Device;
            Hz = hz;
            Amplitude = Math.Clamp(amplitude, 0f, 1f);
            UntilMs = Environment.TickCount64 + 250;
            Ticket = ticket;
        }
        internal bool IsCurrent => Device.IsOnline && ReferenceEquals(Device.Device, Source)
            && (Ticket?.IsCurrent ?? true);
        internal bool TryPublish(Action action) => TryPublish(null, action);
        internal bool TryPublish(Func<RemoteToneRequest> latest, Action action)
        {
            lock (Device.OutputSync)
            {
                if (!IsCurrent || (latest != null && !ReferenceEquals(this, latest()))) return false;
                if (Ticket != null) return Ticket.TryCommit(action);
                action();
                return true;
            }
        }
    }

    internal sealed class DeferredRemoteToneQueue
    {
        private sealed class Pending { internal RemoteToneRequest Latest; }
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Pending> _pending = new();
        private readonly Action<RemoteToneRequest, Func<RemoteToneRequest>> _build;
        private readonly Action<Action> _schedule;
        internal DeferredRemoteToneQueue(Action<RemoteToneRequest, Func<RemoteToneRequest>> build,
            Action<Action> schedule = null)
        {
            _build = build;
            _schedule = schedule ?? (action => { _ = Task.Run(action); });
        }

        internal void Submit(RemoteToneRequest request, bool needsBuild)
        {
            Pending pending;
            Guid id = request.Device.InstanceGuid;
            lock (_gate)
            {
                if (_pending.TryGetValue(id, out pending))
                {
                    pending.Latest = request;
                    return;
                }
                if (!needsBuild) return;
                pending = new Pending { Latest = request };
                _pending.Add(id, pending);
            }
            _schedule(() =>
            {
                RemoteToneRequest Latest() { lock (_gate) return pending.Latest; }
                try
                {
                    while (true)
                    {
                        var current = Latest();
                        if (current.Amplitude > 0 && current.IsCurrent) _build(current, Latest);
                        lock (_gate)
                        {
                            if (!ReferenceEquals(current, pending.Latest)) continue;
                            _pending.Remove(id);
                            return;
                        }
                    }
                }
                finally
                {
                    lock (_gate)
                        if (_pending.TryGetValue(id, out var current) && ReferenceEquals(current, pending))
                            _pending.Remove(id);
                }
            });
        }
    }
}
