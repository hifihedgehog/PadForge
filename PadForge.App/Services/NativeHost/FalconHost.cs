using System;
using PadForge.Engine.Common.NativeHost;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine.Common.ForceDimension;
using PadForge.Engine.Common;

namespace PadForge.Services.NativeHost
{
    /// <summary>The Falcon's calls over the host's channel (#600): one
    /// operation per <see cref="IFalconLink"/> call, after a load that names
    /// the library.</summary>
    internal static class FalconOps
    {
        internal const int Load = 1, DeviceCount = 2, OpenIndex = 3, Close = 4, SystemType = 5, SerialNumber = 6,
            GetPosition = 7, ButtonMask = 8, EnableForce = 9, SetForce = 10, SetMaxForce = 11, SetGravityCompensation = 12,
            SetStandardGravity = 13, GetStatus = 14, ComFrequency = 15, LastError = 16, LastErrorText = 17;

        /// <summary>The bounds PadForge holds the host's answers to. A
        /// return code is the SDK's: -1 for a failure, small counts and
        /// flags otherwise (dhdc.h). A position is in meters, and the
        /// Falcon's workspace is a tenth of a meter across. The rate is
        /// in kHz.</summary>
        internal const int MinCode = -1000, MaxCode = 1000;
        internal const double MaxPosition = 1.0, MaxRateKhz = 100.0, MaxForce = 1000.0;
        internal const int MaxText = 512, MaxPath = 1024;
    }

    /// <summary>
    /// The host's side of the Falcon: Force Dimension's library loaded in
    /// the host (<see cref="ForceDimensionLibrary"/>), called on the
    /// channel's own thread, so the library's per-thread last error belongs
    /// to the PadForge thread behind the channel as it did in process.
    /// </summary>
    internal sealed class FalconHostService : IHostService
    {
        internal const string Name = "falcon";

        /// <summary>Test seam: loads a library for a picked path in place of
        /// Force Dimension's, and forgets the libraries loaded before. Null
        /// in production.</summary>
        internal static Func<string, (IFalconLink Link, string Path, string Version, string Reason)> LoadSeam
        {
            get => s_loadSeam;
            set
            {
                lock (s_gate)
                {
                    s_loadSeam = value;
                    s_libraries.Clear();
                }
            }
        }

        private static Func<string, (IFalconLink Link, string Path, string Version, string Reason)> s_loadSeam;

        // Libraries the host loaded, by the id PadForge names them with. A
        // library is never freed, the in-process rule: a haptic thread can
        // still be inside a call.
        private static readonly object s_gate = new();
        private static readonly List<(IFalconLink Link, string Path, string Version)> s_libraries = new();

        public void Serve(HostConnection channel)
        {
            int[] status = new int[ForceDimensionLibrary.StatusCount];
            while (channel.Receive(out var r))
            {
                int op = r.Int();
                if (op == FalconOps.Load)
                {
                    string picked = r.Text(FalconOps.MaxPath);
                    r.End();
                    int id = LoadForceDimension(picked, out string reason, out string path, out string version);
                    if (!channel.Send(w => w.Int(id).Text(reason).Text(path).Text(version))) return;
                    continue;
                }
                IFalconLink link = Library(r.Int());
                bool sent;
                switch (op)
                {
                    case FalconOps.DeviceCount: r.End(); sent = channel.Send(w => w.Int(link?.DeviceCount() ?? -1)); break;
                    case FalconOps.OpenIndex: { int i = r.Int(); r.End(); sent = channel.Send(w => w.Int(link?.OpenIndex(i) ?? -1)); break; }
                    case FalconOps.Close: { int d = r.Int(); r.End(); sent = channel.Send(w => w.Int(link?.Close(d) ?? -1)); break; }
                    case FalconOps.SystemType: { int d = r.Int(); r.End(); sent = channel.Send(w => w.Int(link?.SystemType(d) ?? -1)); break; }
                    case FalconOps.SerialNumber:
                    {
                        int d = r.Int();
                        r.End();
                        ushort serial = 0;
                        int code = link?.SerialNumber(d, out serial) ?? -1;
                        sent = channel.Send(w => w.Int(code).Int(serial));
                        break;
                    }
                    case FalconOps.GetPosition:
                    {
                        int d = r.Int();
                        r.End();
                        double x = 0, y = 0, z = 0;
                        int code = link?.GetPosition(d, out x, out y, out z) ?? -1;
                        sent = channel.Send(w => w.Int(code).Double(x).Double(y).Double(z));
                        break;
                    }
                    case FalconOps.ButtonMask: { int d = r.Int(); r.End(); sent = channel.Send(w => w.UInt(link?.ButtonMask(d) ?? 0)); break; }
                    case FalconOps.EnableForce: { int d = r.Int(); bool on = r.Bool(); r.End(); sent = channel.Send(w => w.Int(link?.EnableForce(d, on) ?? -1)); break; }
                    case FalconOps.SetForce:
                    {
                        int d = r.Int();
                        double x = r.Double(), y = r.Double(), z = r.Double();
                        r.End();
                        sent = channel.Send(w => w.Int(link?.SetForce(d, x, y, z) ?? -1));
                        break;
                    }
                    case FalconOps.SetMaxForce: { int d = r.Int(); double n = r.Double(); r.End(); sent = channel.Send(w => w.Int(link?.SetMaxForce(d, n) ?? -1)); break; }
                    case FalconOps.SetGravityCompensation: { int d = r.Int(); bool on = r.Bool(); r.End(); sent = channel.Send(w => w.Int(link?.SetGravityCompensation(d, on) ?? -1)); break; }
                    case FalconOps.SetStandardGravity: { int d = r.Int(); double g = r.Double(); r.End(); sent = channel.Send(w => w.Int(link?.SetStandardGravity(d, g) ?? -1)); break; }
                    case FalconOps.GetStatus:
                    {
                        int d = r.Int();
                        r.End();
                        Array.Clear(status);
                        int code = link?.GetStatus(d, status) ?? -1;
                        sent = channel.Send(w =>
                        {
                            w.Int(code);
                            foreach (int s in status) w.Int(s);
                        });
                        break;
                    }
                    case FalconOps.ComFrequency: { int d = r.Int(); r.End(); sent = channel.Send(w => w.Double(link?.ComFrequency(d) ?? 0)); break; }
                    case FalconOps.LastError: r.End(); sent = channel.Send(w => w.Int(link?.LastError() ?? -1)); break;
                    case FalconOps.LastErrorText:
                    {
                        r.End();
                        string text = link?.LastErrorText() ?? "the library is not loaded in the host";
                        if (text.Length > FalconOps.MaxText) text = text.Substring(0, FalconOps.MaxText);
                        sent = channel.Send(w => w.Text(text));
                        break;
                    }
                    default: throw new HostProtocolException("unknown Falcon call");
                }
                if (!sent) return;
            }
        }

        private static int LoadForceDimension(string picked, out string reason, out string path, out string version)
        {
            path = version = string.Empty;
            lock (s_gate)
            {
                IFalconLink link;
                if (LoadSeam != null)
                {
                    var loaded = LoadSeam(picked);
                    link = loaded.Link;
                    reason = loaded.Reason ?? string.Empty;
                    path = loaded.Path ?? string.Empty;
                    version = loaded.Version ?? string.Empty;
                }
                else
                {
                    var library = ForceDimensionLibrary.TryLoad(picked, out reason);
                    link = library;
                    if (library != null)
                    {
                        reason = string.Empty;
                        path = library.Path;
                        version = library.SdkVersion;
                    }
                }
                if (link == null) return 0;
                for (int i = 0; i < s_libraries.Count; i++)
                    if (string.Equals(s_libraries[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i + 1;
                s_libraries.Add((link, path, version));
                return s_libraries.Count;
            }
        }

        private static IFalconLink Library(int id)
        {
            lock (s_gate) return id >= 1 && id <= s_libraries.Count ? s_libraries[id - 1].Link : null;
        }
    }

    /// <summary>
    /// PadForge's side of the Falcon: <see cref="IFalconLink"/> over the
    /// host. Each PadForge thread that calls gets a channel of its own, so the
    /// sweep and each Falcon's haptic thread run side by side as they did
    /// against the library in process, each with its own last error. A
    /// thread's channel closes with its last <see cref="Close"/>, as a haptic
    /// thread's does on its way out. A host that ended is started again by
    /// the next call, which loads the same library there first. Every answer
    /// is bounded (<see cref="FalconOps"/>), and one out of bounds counts as
    /// a failed call.
    /// </summary>
    internal sealed class RemoteFalconLink : IFalconLink
    {
        private sealed class Channel
        {
            public HostConnection Connection;
            public int Library;
        }

        private readonly NativeHost _host;
        private readonly string _picked;
        private readonly ThreadLocal<Channel> _channels = new();

        public string Path { get; }
        public string Version { get; }

        private RemoteFalconLink(NativeHost host, string picked, string path, string version)
        {
            _host = host;
            _picked = picked;
            Path = path;
            Version = version;
        }

        /// <summary>Loads Force Dimension's library in the host: the link,
        /// or null with the host's reason, its <see cref="ForceDimensionLibrary.TryLoad"/>
        /// reason or why the host could not be reached.</summary>
        internal static RemoteFalconLink Load(NativeHost host, string picked, out string reason)
        {
            var connection = host.Open(FalconHostService.Name, out reason);
            if (connection == null)
            {
                // The Settings card says the library did not load, and the
                // log says why.
                PadForge.Engine.SdlDiagLog.WriteLine("Falcon: " + reason);
                reason = "load";
                return null;
            }
            int id = 0;
            string why = null, path = null, version = null;
            if (!connection.Call(w => w.Int(FalconOps.Load).Text(picked), r =>
                {
                    id = r.Int(0, 1 << 16);
                    why = r.Text(FalconOps.MaxText);
                    path = r.Text(FalconOps.MaxPath);
                    version = r.Text(FalconOps.MaxText);
                }))
            {
                PadForge.Engine.SdlDiagLog.WriteLine("Falcon: the library host ended");
                reason = "load";
                return null;
            }
            if (id == 0)
            {
                connection.Dispose();
                // Only the reasons ForceDimensionLibrary.TryLoad gives.
                reason = why is "gate" or "platform" or "missing" or "exports" ? why : "load";
                return null;
            }
            reason = string.Empty;
            var link = new RemoteFalconLink(host, picked, path ?? string.Empty, version ?? string.Empty);
            link._channels.Value = new Channel { Connection = connection, Library = id };
            return link;
        }

        /// <summary>This thread's channel, opened, and the library loaded in
        /// the host, when it has none or its host ended. Null when the host
        /// cannot be reached or no longer loads the library.</summary>
        private Channel Current()
        {
            var channel = _channels.Value;
            if (channel != null && !channel.Connection.Broken && channel.Connection.Generation == _host.Generation) return channel;
            channel?.Connection.Dispose();
            _channels.Value = null;
            var connection = _host.Open(FalconHostService.Name, out _);
            if (connection == null) return null;
            int id = 0;
            string path = null;
            if (!connection.Call(w => w.Int(FalconOps.Load).Text(_picked), r =>
                {
                    id = r.Int(0, 1 << 16);
                    r.Text(FalconOps.MaxText);
                    path = r.Text(FalconOps.MaxPath);
                    r.Text(FalconOps.MaxText);
                }) || id == 0 || !string.Equals(path, Path, StringComparison.OrdinalIgnoreCase))
            {
                connection.Dispose();
                return null;
            }
            channel = new Channel { Connection = connection, Library = id };
            _channels.Value = channel;
            return channel;
        }

        private int Code(int op, int device)
        {
            var c = Current();
            int code = -1;
            if (c == null || !c.Connection.Call(w => w.Int(op).Int(c.Library).Int(device), r => code = r.Int(FalconOps.MinCode, FalconOps.MaxCode)))
                return -1;
            return code;
        }

        public int DeviceCount()
        {
            var c = Current();
            int count = -1;
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.DeviceCount).Int(c.Library), r => count = r.Int(-1, sbyte.MaxValue)))
                return -1;
            return count;
        }

        public int OpenIndex(int index)
        {
            var c = Current();
            int id = -1;
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.OpenIndex).Int(c.Library).Int(index), r => id = r.Int(-1, sbyte.MaxValue)))
                return -1;
            return id;
        }

        /// <summary>Closes the device, then this thread's channel: a haptic
        /// thread closes its Falcon last, and a sweep that calls again opens
        /// another.</summary>
        public int Close(int id)
        {
            int code = Code(FalconOps.Close, id);
            var channel = _channels.Value;
            _channels.Value = null;
            channel?.Connection.Dispose();
            return code;
        }

        public int SystemType(int id) => Code(FalconOps.SystemType, id);

        public int SerialNumber(int id, out ushort serial)
        {
            serial = 0;
            var c = Current();
            int code = -1, value = 0;
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.SerialNumber).Int(c.Library).Int(id), r =>
                {
                    code = r.Int(FalconOps.MinCode, FalconOps.MaxCode);
                    value = r.Int(0, ushort.MaxValue);
                }))
                return -1;
            serial = (ushort)value;
            return code;
        }

        public int GetPosition(int id, out double x, out double y, out double z)
        {
            x = y = z = 0;
            var c = Current();
            int code = -1;
            double px = 0, py = 0, pz = 0;
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.GetPosition).Int(c.Library).Int(id), r =>
                {
                    code = r.Int(FalconOps.MinCode, FalconOps.MaxCode);
                    px = r.Double(-FalconOps.MaxPosition, FalconOps.MaxPosition);
                    py = r.Double(-FalconOps.MaxPosition, FalconOps.MaxPosition);
                    pz = r.Double(-FalconOps.MaxPosition, FalconOps.MaxPosition);
                }))
                return -1;
            x = px;
            y = py;
            z = pz;
            return code;
        }

        public uint ButtonMask(int id)
        {
            var c = Current();
            uint mask = 0;
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.ButtonMask).Int(c.Library).Int(id), r => mask = r.UInt()))
                return 0;
            // Bits past the grip's buttons mean nothing to PadForge.
            return mask & 0xFFFF;
        }

        public int EnableForce(int id, bool on) => CodeWith(FalconOps.EnableForce, id, w => w.Bool(on));

        public int SetForce(int id, double x, double y, double z) => CodeWith(FalconOps.SetForce, id, w => w.Double(x).Double(y).Double(z));

        public int SetMaxForce(int id, double newtons) => CodeWith(FalconOps.SetMaxForce, id, w => w.Double(newtons));

        public int SetGravityCompensation(int id, bool on) => CodeWith(FalconOps.SetGravityCompensation, id, w => w.Bool(on));

        public int SetStandardGravity(int id, double metersPerSecondSquared) => CodeWith(FalconOps.SetStandardGravity, id, w => w.Double(metersPerSecondSquared));

        private int CodeWith(int op, int id, Action<MessageWriter> arguments)
        {
            var c = Current();
            int code = -1;
            if (c == null || !c.Connection.Call(w => arguments(w.Int(op).Int(c.Library).Int(id)), r => code = r.Int(FalconOps.MinCode, FalconOps.MaxCode)))
                return -1;
            return code;
        }

        public int GetStatus(int id, int[] status)
        {
            if (status == null || status.Length < ForceDimensionLibrary.StatusCount) return -1;
            var c = Current();
            int code = -1;
            int[] read = new int[ForceDimensionLibrary.StatusCount];
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.GetStatus).Int(c.Library).Int(id), r =>
                {
                    code = r.Int(FalconOps.MinCode, FalconOps.MaxCode);
                    for (int i = 0; i < read.Length; i++) read[i] = r.Int(FalconOps.MinCode, FalconOps.MaxCode);
                }))
                return -1;
            Array.Copy(read, status, read.Length);
            return code;
        }

        public double ComFrequency(int id)
        {
            var c = Current();
            double rate = 0;
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.ComFrequency).Int(c.Library).Int(id), r => rate = r.Double(0, FalconOps.MaxRateKhz)))
                return 0;
            return rate;
        }

        public int LastError()
        {
            var c = Current();
            int code = -1;
            if (c == null || !c.Connection.Call(w => w.Int(FalconOps.LastError).Int(c.Library), r => code = r.Int(-1, 1 << 16)))
                return -1;
            return code;
        }

        public string LastErrorText()
        {
            var c = _channels.Value;
            if (c == null || c.Connection.Broken) return "the library host ended";
            string text = null;
            if (!c.Connection.Call(w => w.Int(FalconOps.LastErrorText).Int(c.Library), r => text = r.Text(FalconOps.MaxText)))
                return "the library host ended";
            return ShownText.Clean(text) ?? string.Empty;
        }
    }
}
