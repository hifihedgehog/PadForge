using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Common.NativeHost;
using PadForge.Engine.Common;

namespace PadForge.Services.NativeHost
{
    /// <summary>The ASUS Aura SDK's calls over the host's channel
    /// (#600).</summary>
    internal static class AuraOps
    {
        internal const int Open = 1, Paint = 2, Close = 3;

        internal const int MaxDevices = 64, MaxLights = 4096, MaxKeys = 1024, MaxChanges = 16384, MaxName = 256, MaxText = 512;
        internal const int SetLight = 0, SetKey = 1;
    }

    /// <summary>
    /// The host's side of Aura: Armoury Crate's AuraSdk COM class
    /// (<see cref="AuraSdkNative"/>), made in the host. Its registration
    /// can name a server in the user's own part of the registry, and where
    /// it names a library, that library would load in the process that makes
    /// the object, so it is made here, with the user's rights.
    /// </summary>
    internal sealed class AuraHostService : IHostService
    {
        internal const string Name = "aura";

        /// <summary>Test seam: the SDK in place of Armoury Crate's. Null in
        /// production.</summary>
        internal static Func<IAuraSdkNative> NativeSeam;

        public void Serve(HostConnection channel)
        {
            var native = NativeSeam?.Invoke() ?? new AuraSdkNative();
            try
            {
                while (channel.Receive(out var r))
                {
                    int op = r.Int();
                    bool sent;
                    switch (op)
                    {
                        case AuraOps.Open:
                        {
                            r.End();
                            bool open = native.Open(out string detail);
                            var devices = open ? native.Devices : Array.Empty<AuraDevice>();
                            sent = channel.Send(w =>
                            {
                                w.Bool(open).Text(Cut(detail)).Int(Math.Min(devices.Count, AuraOps.MaxDevices));
                                for (int i = 0; i < devices.Count && i < AuraOps.MaxDevices; i++)
                                {
                                    var d = devices[i];
                                    // The lights past the bound PadForge reads stay
                                    // unaddressed, as the keys past theirs do.
                                    w.Int(d.Index).UInt(d.Type).Text(Cut(d.Name)).Int(Math.Clamp(d.LightCount, 0, AuraOps.MaxLights)).Int(Math.Min(d.KeyCodes.Count, AuraOps.MaxKeys));
                                    for (int k = 0; k < d.KeyCodes.Count && k < AuraOps.MaxKeys; k++) w.Int(d.KeyCodes[k]);
                                }
                            });
                            break;
                        }
                        case AuraOps.Paint:
                        {
                            int device = r.Int(0, AuraOps.MaxDevices - 1);
                            int count = r.Int(0, AuraOps.MaxChanges);
                            bool done = true;
                            try
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    int kind = r.Int(AuraOps.SetLight, AuraOps.SetKey);
                                    int at = r.Int(0, ushort.MaxValue);
                                    int rgb = r.Int(0, 0xFFFFFF);
                                    if (kind == AuraOps.SetLight) native.SetLight(device, at, rgb);
                                    else native.SetKey(device, (ushort)at, rgb);
                                }
                                r.End();
                                native.Apply(device);
                            }
                            catch (HostProtocolException)
                            {
                                throw;
                            }
                            catch (Exception)
                            {
                                // A server that went away fails the call, as in
                                // process.
                                done = false;
                            }
                            sent = channel.Send(w => w.Bool(done));
                            break;
                        }
                        case AuraOps.Close:
                            r.End();
                            native.Close();
                            sent = channel.Send(w => { });
                            break;
                        default:
                            throw new HostProtocolException("unknown Aura call");
                    }
                    if (!sent) return;
                }
            }
            finally
            {
                native.Close();
            }
        }

        private static string Cut(string text) => text == null || text.Length <= AuraOps.MaxName ? text ?? string.Empty : text.Substring(0, AuraOps.MaxName);
    }

    /// <summary>
    /// PadForge's side of Aura (<see cref="IAuraSdkNative"/>) over the host.
    /// The colors a paint sets wait here, and <see cref="Apply"/> sends them
    /// with the apply in one call, in their order. A failed call throws, as
    /// a server that went away made the in-process calls throw, and the
    /// backend then closes and opens again.
    /// </summary>
    internal sealed class RemoteAuraSdk : IAuraSdkNative
    {
        private readonly NativeHost _host;
        private readonly object _gate = new();
        private HostConnection _channel;
        private IReadOnlyList<AuraDevice> _devices = Array.Empty<AuraDevice>();
        private readonly List<(int Kind, int At, int Rgb)>[] _pending = new List<(int, int, int)>[AuraOps.MaxDevices];

        internal RemoteAuraSdk(NativeHost host) => _host = host;

        public IReadOnlyList<AuraDevice> Devices => _devices;

        public bool Open(out string detail)
        {
            lock (_gate)
            {
                CloseChannel();
                _channel = _host.Open(AuraHostService.Name, out detail);
                if (_channel == null) return false;
                bool open = false;
                string said = null;
                var devices = new List<AuraDevice>();
                if (!_channel.Call(w => w.Int(AuraOps.Open), r =>
                    {
                        open = r.Bool();
                        said = ShownText.Clean(r.Text(AuraOps.MaxText)) ?? string.Empty;
                        int count = r.Int(0, AuraOps.MaxDevices);
                        for (int i = 0; i < count; i++)
                        {
                            int index = r.Int(0, AuraOps.MaxDevices - 1);
                            uint type = r.UInt();
                            string name = ShownText.Clean(r.Text(AuraOps.MaxName)) ?? string.Empty;
                            int lights = r.Int(0, AuraOps.MaxLights);
                            int keys = r.Int(0, AuraOps.MaxKeys);
                            var codes = new List<ushort>(keys);
                            for (int k = 0; k < keys; k++) codes.Add((ushort)r.Int(0, ushort.MaxValue));
                            if (index != i) throw new HostProtocolException("devices out of order");
                            devices.Add(new AuraDevice(index, type, name, lights, codes));
                        }
                    }))
                {
                    detail = "the library host ended";
                    CloseChannel();
                    return false;
                }
                detail = said;
                if (!open)
                {
                    CloseChannel();
                    return false;
                }
                _devices = devices;
                return true;
            }
        }

        public void SetLight(int device, int index, int rgb)
        {
            var d = Device(device);
            if (index < 0 || index >= d.LightCount) throw new ArgumentOutOfRangeException(nameof(index));
            Queue(device).Add((AuraOps.SetLight, index, rgb & 0xFFFFFF));
        }

        public void SetKey(int device, ushort code, int rgb) => Queue(device).Add((AuraOps.SetKey, code, rgb & 0xFFFFFF));

        public void Apply(int device)
        {
            var changes = Queue(device);
            lock (_gate)
            {
                bool done = false;
                var list = changes.ToArray();
                changes.Clear();
                if (_channel == null || !_channel.Call(w =>
                    {
                        w.Int(AuraOps.Paint).Int(device).Int(list.Length);
                        foreach (var c in list) w.Int(c.Kind).Int(c.At).Int(c.Rgb);
                    }, r => done = r.Bool()) || !done)
                    throw new InvalidOperationException("the Aura SDK did not take the colors");
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _channel?.Call(w => w.Int(AuraOps.Close), null);
                CloseChannel();
            }
        }

        private void CloseChannel()
        {
            _channel?.Dispose();
            _channel = null;
            _devices = Array.Empty<AuraDevice>();
            for (int i = 0; i < _pending.Length; i++) _pending[i]?.Clear();
        }

        private AuraDevice Device(int device)
        {
            var devices = _devices;
            if (device < 0 || device >= devices.Count) throw new ArgumentOutOfRangeException(nameof(device));
            return devices[device];
        }

        private List<(int Kind, int At, int Rgb)> Queue(int device)
        {
            Device(device);
            var queue = _pending[device] ??= new List<(int, int, int)>();
            if (queue.Count >= AuraOps.MaxChanges) throw new InvalidOperationException("too many colors before an apply");
            return queue;
        }
    }
}
