using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What the desk-light, speaker and light-strip families of
    /// #557 share: a report built from its leading bytes, the color channels
    /// of a light, and the look for a USB serial port by its vendor and
    /// product ID.</summary>
    internal static class LightReports
    {
        /// <summary>A report of <paramref name="length"/> bytes, zero but for
        /// <paramref name="head"/> at its start, the way OpenRGB's
        /// controllers memset a buffer and fill its first bytes.</summary>
        public static byte[] Report(int length, params byte[] head)
        {
            var report = new byte[length];
            if (head != null) Array.Copy(head, report, Math.Min(head.Length, length));
            return report;
        }

        /// <summary>The first registration of <paramref name="family"/> a
        /// collection answers to by OpenRGB's rules
        /// (<see cref="OpenRgbRegistrations.Match(string, HidCollectionInfo, int)"/>),
        /// or null. A collection with none of the family's IDs is turned
        /// away before the walk of the whole table, since every collection
        /// of every scan is offered to every family.</summary>
        public static OpenRgbRegistration Registered(string family, HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var own = s_registrations.GetOrAdd(family, f => OpenRgbRegistrations.Of(f).ToArray());
            foreach (var registration in own)
            {
                if (!OpenRgbRegistrations.MatchingId(registration, collection)) continue;
                var matches = OpenRgbRegistrations.Match(family, collection);
                return matches.Count > 0 ? matches[0] : null;
            }
            return null;
        }

        private static readonly ConcurrentDictionary<string, OpenRgbRegistration[]> s_registrations = new(StringComparer.Ordinal);

        public static byte Red(int rgb) => (byte)(rgb >> 16);

        public static byte Green(int rgb) => (byte)(rgb >> 8);

        public static byte Blue(int rgb) => (byte)rgb;

        /// <summary>The hardware ID prefix OpenRGB's serial look compares:
        /// USB\VID_ and the vendor ID, then the ampersand, PID_ and the
        /// product ID, in upper-case hex, its first 21 characters
        /// (find_usb_serial_port_win.cpp:54-59 and 95).</summary>
        public static string SerialPrefix(ushort vendorId, ushort productId)
            => $@"USB\VID_{vendorId:X4}&PID_{productId:X4}";

        /// <summary>The ports of <paramref name="ports"/> whose instance ID
        /// starts with the prefix of <paramref name="vendorId"/> and
        /// <paramref name="productId"/>, as OpenRGB's look takes every
        /// present device whose hardware ID starts so
        /// (find_usb_serial_port_win.cpp:62-104), each keyed \\.\COMn with
        /// the model <paramref name="modelOf"/> gives it and the name
        /// <paramref name="nameOf"/> gives it, or the model's without one. A port
        /// whose container cannot be read yet is left for the next look, since
        /// the key is no interface path to look it up by, as DygmaRaiseLighting
        /// leaves it. <paramref name="modelOf"/> answers null for a port the
        /// family does not list.</summary>
        public static List<OwnDevice> SerialPorts(IReadOnlyList<ComPort> ports, Func<string, Guid> containerOf,
            ushort vendorId, ushort productId, Func<ComPort, HidLightingModel> modelOf, Func<ComPort, string> nameOf = null)
        {
            var found = new List<OwnDevice>();
            if (ports == null) return found;
            string prefix = SerialPrefix(vendorId, productId);
            foreach (var port in ports)
            {
                if (port?.InstanceId == null || port.PortName == null
                    || !port.InstanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var model = modelOf(port);
                if (model == null) continue;
                Guid container;
                try { container = containerOf(port.InstanceId); }
                catch { container = Guid.Empty; }
                if (container == Guid.Empty) continue;
                found.Add(new OwnDevice(@"\\.\" + port.PortName, model)
                {
                    VendorId = vendorId, ProductId = productId, Container = container, Name = nameOf?.Invoke(port),
                });
            }
            return found;
        }
    }

    /// <summary>
    /// A session whose every picture is a list of reports, feature or
    /// output, built from the colors by light address, after reports that
    /// go out once before the first picture. It has nothing to hand back:
    /// the families that use it are those for which OpenRGB names no way
    /// back to the device's own lighting, which each family's class comment
    /// says.
    /// </summary>
    internal sealed class ReportsSession : HidLightingSession
    {
        private readonly bool _feature;
        private readonly int _size;
        private readonly Func<int[], IReadOnlyList<byte[]>> _frames;
        private readonly IReadOnlyList<byte[]> _opening;
        private bool _opened;

        /// <param name="feature">Feature reports, else output reports.</param>
        /// <param name="size">The light addresses the colors are laid out by.</param>
        /// <param name="frames">The reports of one picture, from its colors by address.</param>
        /// <param name="opening">The reports that go out once, before the first picture.</param>
        public ReportsSession(bool feature, int size, Func<int[], IReadOnlyList<byte[]>> frames,
            IReadOnlyList<byte[]> opening = null)
        {
            _feature = feature;
            _size = size;
            _frames = frames;
            _opening = opening ?? Array.Empty<byte[]>();
        }

        public override bool Feature => _feature;

        public override byte[][] CrashFrames => Array.Empty<byte[]>();

        public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
            IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            if (io == null) return HidLightingPaint.Failed;
            if (!_opened)
            {
                foreach (var report in _opening)
                    if (!Send(io, report)) return HidLightingPaint.Failed;
                _opened = true;
            }
            foreach (var report in _frames(KeyboardLedNames.ByAddress(lights, colors, _size)))
                if (!Send(io, report)) return HidLightingPaint.Failed;
            return HidLightingPaint.Sent;
        }

        private bool Send(IHidReportChannel io, byte[] report) => _feature ? io.SetFeature(report) : io.WriteOutput(report);

        /// <summary>Nothing to send: see the family's class comment.</summary>
        public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
    }

    /// <summary>
    /// A session on a USB serial port whose every picture is one write of
    /// bytes built from the colors by light address, after bytes that go out
    /// once before the first picture, and whose hand-back is a write of its
    /// own, which is also its one crash frame from the first picture on. A
    /// family with nothing to hand back passes none, and its hand-back sends
    /// nothing. The worker hands it no HID channel.
    /// </summary>
    internal sealed class SerialFramesSession : HidLightingSession
    {
        private readonly ISerialLineChannel _port;
        private readonly int _size;
        private readonly Func<int[], byte[]> _frame;
        private readonly byte[] _opening;
        private readonly byte[] _handBack;
        private readonly LightMap _map;
        private readonly string _deviceName;
        private bool _opened;
        private byte[][] _crash = Array.Empty<byte[]>();

        /// <param name="port">The port the session writes to.</param>
        /// <param name="size">The light addresses the colors are laid out by.</param>
        /// <param name="frame">The bytes of one picture, from its colors by address.</param>
        /// <param name="opening">Bytes that go out once, before the first picture, or null.</param>
        /// <param name="handBack">The bytes that give the device back, or null for none.</param>
        /// <param name="map">The map of the device the session opened, or null for the model's.</param>
        /// <param name="deviceName">The name the device answered to, or null.</param>
        public SerialFramesSession(ISerialLineChannel port, int size, Func<int[], byte[]> frame,
            byte[] opening = null, byte[] handBack = null, LightMap map = null, string deviceName = null)
        {
            _deviceName = deviceName;
            _port = port;
            _size = size;
            _frame = frame;
            _opening = opening;
            _handBack = handBack;
            _map = map;
        }

        public override bool Feature => false;

        public override LightMap Map => _map;

        public override string DeviceName => _deviceName;

        /// <summary>The hand-back, once a picture may have gone out.</summary>
        public override byte[][] CrashFrames => _crash;

        public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
            IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            if (_port == null) return HidLightingPaint.Failed;
            // Marked first: a write that reports a failure may still have
            // landed.
            if (_handBack != null && _crash.Length == 0) _crash = new[] { _handBack };
            if (!_opened)
            {
                if (_opening != null && !_port.Write(_opening)) return HidLightingPaint.Failed;
                _opened = true;
            }
            return _port.Write(_frame(KeyboardLedNames.ByAddress(lights, colors, _size)))
                ? HidLightingPaint.Sent
                : HidLightingPaint.Failed;
        }

        public override bool HandBack(IHidReportChannel io, Action<int> wait)
        {
            if (_crash.Length == 0) return true;
            if (_port == null || !_port.Write(_handBack)) return false;
            _crash = Array.Empty<byte[]>();
            return true;
        }
    }
}
