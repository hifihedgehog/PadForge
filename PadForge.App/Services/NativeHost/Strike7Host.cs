using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine.Common.MadCatz;
using PadForge.Engine.Common.NativeHost;
using PadForge.Engine.Common;

namespace PadForge.Services.NativeHost
{
    /// <summary>The Strike 7 applet library's calls over the host's channel
    /// (#597).</summary>
    internal static class Strike7Ops
    {
        internal const int Load = 1, Open = 2, Close = 3, Draw = 4, TakeEvents = 5;

        internal const int MaxEvents = 256, MaxPath = 1024, MaxName = 64, MaxIcon = 256 * 256 * 4;
        internal const int ScreenBytes = Strike7Library.ScreenWidth * Strike7Library.ScreenHeight * 4;
    }

    /// <summary>
    /// The host's side of the Strike 7: Mad Catz's Strike7API.dll, from the
    /// folder the user picked or PadForge's download, loaded in the host
    /// (<see cref="Strike7Library.TryLoad"/>). The library calls its events
    /// on a thread of its own, and they wait here until PadForge takes
    /// them.
    /// </summary>
    internal sealed class Strike7HostService : IHostService
    {
        internal const string Name = "strike7";

        /// <summary>Test seam: loads a library for a path in place of Mad
        /// Catz's. Null in production.</summary>
        internal static Func<string, (IStrike7Library Library, string Reason)> LoadSeam;

        // A library is never freed while the host runs, the in-process rule.
        private static readonly object s_gate = new();
        private static readonly Dictionary<string, IStrike7Library> s_libraries = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentQueue<Strike7Event> _events = new();

        public void Serve(HostConnection channel)
        {
            IStrike7Library library = null;
            while (channel.Receive(out var r))
            {
                int op = r.Int();
                bool sent;
                switch (op)
                {
                    case Strike7Ops.Load:
                    {
                        string path = r.Text(Strike7Ops.MaxPath);
                        r.End();
                        string reason;
                        lock (s_gate)
                        {
                            if (path != null && s_libraries.TryGetValue(path, out library)) reason = string.Empty;
                            else
                            {
                                (library, reason) = LoadSeam != null ? LoadSeam(path) : (Strike7Library.TryLoad(path, out string why), why);
                                if (library != null)
                                {
                                    s_libraries[path] = library;
                                    reason = string.Empty;
                                }
                            }
                        }
                        string answer = reason ?? string.Empty;
                        bool loaded = library != null;
                        sent = channel.Send(w => w.Bool(loaded).Text(answer));
                        break;
                    }
                    case Strike7Ops.Open:
                    {
                        string name = r.Text(Strike7Ops.MaxName);
                        int width = r.Int(1, 256), height = r.Int(1, 256);
                        byte[] icon = r.Bytes(Strike7Ops.MaxIcon);
                        r.End();
                        ushort applet = library?.Open(name, width, height, icon, e => _events.Enqueue(e)) ?? 0;
                        sent = channel.Send(w => w.Int(applet));
                        break;
                    }
                    case Strike7Ops.Close:
                    {
                        ushort applet = (ushort)r.Int(0, ushort.MaxValue);
                        r.End();
                        library?.Close(applet);
                        sent = channel.Send(w => { });
                        break;
                    }
                    case Strike7Ops.Draw:
                    {
                        ushort applet = (ushort)r.Int(0, ushort.MaxValue);
                        ushort endpoint = (ushort)r.Int(0, ushort.MaxValue);
                        byte[] frame = r.Bytes(Strike7Ops.ScreenBytes);
                        r.End();
                        library?.DrawScreen(applet, endpoint, frame);
                        sent = channel.Send(w => { });
                        break;
                    }
                    case Strike7Ops.TakeEvents:
                    {
                        r.End();
                        var taken = new List<Strike7Event>();
                        while (taken.Count < Strike7Ops.MaxEvents && _events.TryDequeue(out var e)) taken.Add(e);
                        sent = channel.Send(w =>
                        {
                            w.Int(taken.Count);
                            foreach (var e in taken)
                                w.Int((int)e.Kind).Int(e.AppletId).Int(e.EndpointId).Int(e.X).Int(e.Y).Bool(e.Pressed);
                        });
                        break;
                    }
                    default:
                        throw new HostProtocolException("unknown Strike 7 call");
                }
                if (!sent) return;
            }
        }
    }

    /// <summary>
    /// PadForge's side of the Strike 7 (<see cref="IStrike7Library"/>) over
    /// the host. While an applet is open its events are taken every
    /// <see cref="EventPollMs"/>, the screen loop's own pace, and handed to
    /// the subscriber, each field held to the screen's bounds. A host that
    /// ended is started again at the next poll, the library loaded there
    /// and the applet opened again under the ID the screen already holds,
    /// and the screen hears the keyboard's reset, after which the user picks
    /// the applet again as after an unplug.
    /// </summary>
    internal sealed class RemoteStrike7Library : IStrike7Library
    {
        internal const int EventPollMs = 20;

        private readonly NativeHost _host;
        private readonly object _gate = new();
        private HostConnection _channel;
        private Timer _poll;
        private volatile Action<Strike7Event> _subscriber;
        private string _name;
        private int _iconWidth, _iconHeight;
        private byte[] _icon;
        private ushort _screenApplet, _hostApplet;

        public string Path { get; }

        private RemoteStrike7Library(NativeHost host, string path)
        {
            _host = host;
            Path = path;
        }

        /// <summary>The library loaded in the host, or null with the host's
        /// reason (<see cref="Strike7Library.TryLoad"/>'s, or why the host
        /// could not be reached, as "load").</summary>
        internal static (IStrike7Library Library, string Reason) Load(NativeHost host, string path)
        {
            var library = new RemoteStrike7Library(host, path);
            string reason;
            lock (library._gate) reason = library.Connect();
            return reason == null ? (library, string.Empty) : (null, reason);
        }

        /// <summary>Opens a channel and loads the library there. Null when
        /// it loaded, else why not. Caller holds the gate.</summary>
        private string Connect()
        {
            _channel?.Dispose();
            _channel = _host.Open(Strike7HostService.Name, out _);
            if (_channel == null) return "load";
            bool loaded = false;
            string reason = null;
            if (!_channel.Call(w => w.Int(Strike7Ops.Load).Text(Path), r =>
                {
                    loaded = r.Bool();
                    reason = r.Text(64);
                }))
                return "load";
            if (loaded) return null;
            _channel.Dispose();
            _channel = null;
            reason = ShownText.Clean(reason);
            return reason is "gate" or "platform" or "missing" or "runtime" or "exports" ? reason : "load";
        }

        private bool Live()
        {
            if (_channel != null && !_channel.Broken && _channel.Generation == _host.Generation) return true;
            return Connect() == null;
        }

        public ushort Open(string name, int iconWidth, int iconHeight, byte[] iconBgra, Action<Strike7Event> subscriber)
        {
            if (iconBgra == null || iconWidth <= 0 || iconHeight <= 0 || iconBgra.Length != iconWidth * iconHeight * 4) return 0;
            lock (_gate)
            {
                if (!Live()) return 0;
                ushort applet = OpenInHost(name, iconWidth, iconHeight, iconBgra);
                if (applet == 0) return 0;
                _name = name;
                _iconWidth = iconWidth;
                _iconHeight = iconHeight;
                _icon = iconBgra;
                _screenApplet = _hostApplet = applet;
                _subscriber = subscriber;
                _poll ??= new Timer(_ => TakeEvents(), null, EventPollMs, EventPollMs);
                return applet;
            }
        }

        /// <summary>Opens an applet in the host: its ID there, or 0. Caller
        /// holds the gate.</summary>
        private ushort OpenInHost(string name, int iconWidth, int iconHeight, byte[] icon)
        {
            int applet = 0;
            if (!_channel.Call(w => w.Int(Strike7Ops.Open).Text(name).Int(iconWidth).Int(iconHeight).Bytes(icon),
                    r => applet = r.Int(0, ushort.MaxValue)))
                return 0;
            return (ushort)applet;
        }

        public void Close(ushort appletId)
        {
            lock (_gate)
            {
                _subscriber = null;
                _poll?.Dispose();
                _poll = null;
                ushort hostApplet = appletId == _screenApplet ? _hostApplet : appletId;
                _screenApplet = _hostApplet = 0;
                if (hostApplet == 0 || _channel == null || _channel.Broken) return;
                _channel.Call(w => w.Int(Strike7Ops.Close).Int(hostApplet), null);
            }
        }

        public void DrawScreen(ushort appletId, ushort endpointId, byte[] bgra)
        {
            if (appletId == 0 || bgra == null || bgra.Length != Strike7Ops.ScreenBytes) return;
            lock (_gate)
            {
                if (_channel == null || _channel.Broken || appletId != _screenApplet) return;
                ushort hostApplet = _hostApplet;
                _channel.Call(w => w.Int(Strike7Ops.Draw).Int(hostApplet).Int(endpointId).Bytes(bgra), null);
            }
        }

        /// <summary>Takes the events the library raised since the last poll.
        /// A host that ended raises the keyboard's reset, so the screen opens
        /// its applet again.</summary>
        private void TakeEvents()
        {
            var events = new List<Strike7Event>();
            lock (_gate)
            {
                if (_subscriber == null || _channel == null) return;
                if (!_channel.Call(w => w.Int(Strike7Ops.TakeEvents), r =>
                    {
                        int count = r.Int(0, Strike7Ops.MaxEvents);
                        for (int i = 0; i < count; i++)
                            events.Add(new Strike7Event(
                                (Strike7EventKind)r.Int(0, (int)Strike7EventKind.Reset),
                                (ushort)r.Int(0, ushort.MaxValue),
                                (ushort)r.Int(0, ushort.MaxValue),
                                r.Int(0, Strike7Library.ScreenWidth),
                                r.Int(0, Strike7Library.ScreenHeight),
                                r.Bool()));
                    }))
                {
                    // The host ended: the applet is opened again in a new
                    // one, and the screen hears a reset.
                    events.Clear();
                    events.Add(new Strike7Event(Strike7EventKind.Reset, _screenApplet, 0, 0, 0, false));
                    _hostApplet = Live() ? OpenInHost(_name, _iconWidth, _iconHeight, _icon) : (ushort)0;
                }
                // The screen knows its applet by the ID it was first given.
                for (int i = 0; i < events.Count; i++)
                    if (events[i].AppletId == _hostApplet && _hostApplet != 0) events[i] = events[i] with { AppletId = _screenApplet };
            }
            var subscriber = _subscriber;
            foreach (var e in events)
            {
                try { subscriber?.Invoke(e); }
                catch (Exception) { }
            }
        }
    }
}
