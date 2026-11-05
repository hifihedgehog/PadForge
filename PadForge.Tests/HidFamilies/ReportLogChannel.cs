using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A scripted HID device for the keyboard family tests (#557):
    /// it logs every report it is sent, in order, with its kind, and answers
    /// a feature read or an input read from what the test gives it.</summary>
    internal sealed class ReportLog
    {
        private readonly object _gate = new();
        private readonly List<(char Kind, byte[] Report)> _sent = new();
        private byte[] _lastFeature;
        private byte[] _lastOutput;

        /// <summary>The answer to a feature read of a report ID after the
        /// last feature report sent, or null for none.</summary>
        public Func<byte, byte[], byte[]> FeatureAnswer = (_, _) => null;

        /// <summary>The input report after an output report, or null.</summary>
        public Func<byte[], byte[]> InputAnswer = _ => null;

        /// <summary>Whether a feature report or an output report fails.</summary>
        public Func<byte[], bool> FeatureFails = _ => false;
        public Func<byte[], bool> OutputFails = _ => false;

        public bool SetFeature(byte[] report)
        {
            lock (_gate)
            {
                if (FeatureFails(report)) return false;
                _lastFeature = (byte[])report.Clone();
                _sent.Add(('F', _lastFeature));
                return true;
            }
        }

        public bool WriteOutput(byte[] report)
        {
            lock (_gate)
            {
                if (OutputFails(report)) return false;
                _lastOutput = (byte[])report.Clone();
                _sent.Add(('O', _lastOutput));
                return true;
            }
        }

        public byte[] Feature(byte reportId)
        {
            lock (_gate)
            {
                _sent.Add(('G', new[] { reportId }));
                return FeatureAnswer(reportId, _lastFeature);
            }
        }

        public byte[] Input()
        {
            lock (_gate)
            {
                var answer = _lastOutput == null ? null : InputAnswer(_lastOutput);
                _lastOutput = null;
                return answer;
            }
        }

        /// <summary>Every report sent, in order: F for a feature report, O
        /// for an output report, G for a feature read (its report ID).</summary>
        public List<(char Kind, byte[] Report)> Sent()
        {
            lock (_gate) return _sent.ToList();
        }

        public List<byte[]> Of(char kind)
        {
            lock (_gate) return _sent.Where(s => s.Kind == kind).Select(s => s.Report).ToList();
        }

        public void Clear()
        {
            lock (_gate) _sent.Clear();
        }

        public static string Hex(byte[] bytes, int count) => BitConverter.ToString(bytes, 0, Math.Min(count, bytes.Length)).Replace('-', ' ');

        public static string Hex(byte[] bytes, int from, int count) => BitConverter.ToString(bytes, from, count).Replace('-', ' ');
    }

    /// <summary>A channel onto a <see cref="ReportLog"/>, with the report
    /// lengths a test gives it.</summary>
    internal sealed class ReportLogChannel : IHidReportChannel
    {
        public ReportLog Device { get; init; }
        public string Path { get; init; }
        public bool FeatureOnly { get; init; }
        public int InputReportLength { get; init; } = 65;
        public int OutputReportLength { get; init; } = 65;
        public int FeatureReportLength { get; init; } = 65;
        public volatile bool Disposed;

        public bool SetFeature(byte[] report) => !Disposed && Device.SetFeature(report);

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            if (Disposed) return false;
            var answer = Device.Feature(reportId);
            if (answer == null) return false;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
            => !Disposed && !FeatureOnly && Device.WriteOutput(report);

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            if (Disposed) return -1;
            var answer = Device.Input();
            if (answer == null) return 0;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return Math.Min(answer.Length, buffer.Length);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>The shared HID worker with one family's collection on a
    /// <see cref="ReportLog"/>, a fake clock and the worker's own waits
    /// taken off the clock: what a claim makes the session send, what it
    /// records for the crash path, and what its hand-back sends.</summary>
    internal sealed class KeyboardFamilyRig : IDisposable
    {
        public static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        public static readonly Guid Device = new("00000000-0000-0000-0000-0000000000a1");

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private long _tick = 1_000_000;

        public readonly ReportLog Log = new();
        public readonly List<ReportLogChannel> Channels = new();
        public readonly List<(string Path, byte[] Frame, bool Feature)> OneShots = new();
        public readonly List<int> Waits = new();

        /// <summary>The report lengths the opened channel has.</summary>
        public int OutputLength = 65;
        public int FeatureLength = 65;
        public int InputLength = 65;

        public KeyboardFamilyRig()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true);
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest();
        }

        public long Tick() => Interlocked.Read(ref _tick);
        public void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private IHidReportChannel Open(string path)
        {
            var channel = new ReportLogChannel
            {
                Path = path, Device = Log, OutputReportLength = OutputLength, FeatureReportLength = FeatureLength,
                InputReportLength = InputLength,
            };
            lock (Channels) Channels.Add(channel);
            return channel;
        }

        public HidLightingBackend Started(IReadOnlyList<IHidLightingFamily> families, HidCollectionInfo present)
            => Started(families, new[] { present });

        /// <summary>A worker handed a walk of several collections.</summary>
        public HidLightingBackend Started(IReadOnlyList<IHidLightingFamily> families, HidCollectionInfo[] present)
        {
            var backend = new HidLightingBackend(families, Open, _ => Box, Tick,
                (path, frame, _) => { lock (OneShots) OneShots.Add((path, frame, true)); return true; },
                (path, frame, _) => { lock (OneShots) OneShots.Add((path, frame, false)); return true; },
                ms => { lock (Waits) Waits.Add(ms); Advance(ms); }, idleTickMs: 5);
            backend.Start();
            backend.Rescan(present);
            Passes(backend);
            return backend;
        }

        public static void Passes(HidLightingBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                backend.Wake();
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        public static OutputPath PathOf(HidCollectionInfo collection) => new(OutputFamily.HidLightingUnit, collection.Path);

        public static void Link(params OutputPath[] paths)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = paths } }));

        public static void Light(int rgb)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        public static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        /// <summary>The worker started on the collection, the collection
        /// linked and lit in one color, and the passes that send it.</summary>
        public HidLightingBackend Lit(IReadOnlyList<IHidLightingFamily> families, HidCollectionInfo collection,
            int rgb = 0x112233, int holdMs = 0)
        {
            var backend = Started(families, collection);
            Link(PathOf(collection));
            Light(rgb);
            Passes(backend);
            Advance(holdMs);
            Passes(backend);
            return backend;
        }

        /// <summary>The claim dropped and the passes that hand back.</summary>
        public void Release(HidLightingBackend backend)
        {
            Unlight();
            Passes(backend, 3);
        }

        public static HidCollectionInfo Collection(ushort vid, ushort pid, ushort page, ushort usage, int iface = -1,
            ushort feature = 0, ushort output = 0, ushort input = 0, string tail = "")
            => new()
            {
                Path = iface >= 0
                    ? $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_{iface:x2}#{page:x4}{usage:x4}{tail}"
                    : $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}#{page:x4}{usage:x4}{tail}",
                VendorId = vid, ProductId = pid, UsagePage = page, Usage = usage, InterfaceNumber = iface,
                FeatureReportLength = feature, OutputReportLength = output, InputReportLength = input,
            };
    }
}
