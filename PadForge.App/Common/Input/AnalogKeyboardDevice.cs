using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.AnalogKeyboard;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One analog keyboard as a mappable device row (issue #468, asked for in
    /// discussion #463). Every key's press depth lands in
    /// <see cref="CustomInputState.AnalogKeys"/> and maps through the
    /// <c>"Analog Key N"</c> descriptor, the way a MIDI endpoint publishes its
    /// namespace on its own sub-state (<see cref="MidiInputDevice"/>).
    ///
    /// <para>A reader thread owns the HID channel. Families that push their
    /// state get a blocking read and a parse per report. Families that answer
    /// requests get their poller's passes back to back. Either way the thread
    /// writes the live key set under a lock and the poll thread copies it
    /// out, the <see cref="LogitechGKeysDevice"/> discipline.</para>
    /// </summary>
    internal sealed class AnalogKeyboardDevice : ISdlInputDevice
    {
        /// <summary>Consecutive failed reads a Razer collection may have
        /// before the row is given up. Soup counts ten empty reports before
        /// it marks a Razer keyboard disconnected, because Synapse switching
        /// the keyboard's mode can fail a read without the keyboard leaving.
        /// Every other family stops at the first.</summary>
        private const int RazerReadErrorsTolerated = 10;

        /// <summary>Passes in a row that go unanswered before a polled
        /// keyboard is given up, and the sweep's retry cooldown takes over.</summary>
        private const int PollMissesTolerated = 20;

        /// <summary>Shortest time between the starts of two polling passes.
        /// A keyboard answers in a few milliseconds, and the vendor's own
        /// configurator shares this channel.</summary>
        private const int PollPeriodMs = 2;

        /// <summary>How often a Razer row checks that Synapse still runs.</summary>
        private const int SynapseCheckIntervalMs = 2000;

        private readonly AnalogKeyboardCandidate _candidate;
        private readonly object _stateLock = new();
        private readonly AnalogKeyInputState _live = new();
        private readonly AnalogKeyboardPoller _poller;
        private readonly Dictionary<int, int> _heldVirtualKeys = new();
        private AnalogKeyboardHidChannel _channel;
        private Thread _reader;
        private volatile bool _attached;
        private volatile bool _disposed;
        private volatile bool _synapseRunning = true;
        private long _reports;
        private PooledInputStatePair _statePool;

        public AnalogKeyboardDevice(AnalogKeyboardCandidate candidate)
        {
            _candidate = candidate;
            Name = candidate.Name;
            HidPath = candidate.Path;
            DevicePath = "analogkb://" + candidate.IdentityKey.ToLowerInvariant();
            InstanceGuid = Md5Guid("pfanalogkb:" + candidate.IdentityKey.ToLowerInvariant());
            ushort identityPid = AnalogKeyboardCatalog.IdentityProductId(candidate.VendorId, candidate.ProductId);
            ProductGuid = Md5Guid($"pfanalogkb-product:{candidate.VendorId:X4}:{identityPid:X4}");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            _poller = AnalogKeyboardPoller.Create(candidate.Protocol, candidate.VendorId, candidate.ProductId);
            if (_poller != null)
            {
                foreach (int code in AnalogKeyboardCatalog.KeysFor(candidate.VendorId, candidate.ProductId))
                {
                    int vk = VirtualKeyForCode(code);
                    if (vk != 0) _heldVirtualKeys[code] = vk;
                }
            }
        }

        /// <summary>The HID collection this row reads, the sweep's key.</summary>
        public string HidPath { get; }

        public AnalogKeyboardProtocol Protocol => _candidate.Protocol;

        /// <summary>False while a Razer row waits for Synapse, which is the
        /// only thing that makes its keyboard send analog reports.</summary>
        public bool SynapseRunning => _synapseRunning;

        public bool NeedsSynapse => AnalogKeyboardCatalog.NeedsSynapse(_candidate.Protocol);

        /// <summary>Reports parsed or passes answered, which tells a user
        /// "found" from "found and reading".</summary>
        public long ReportCount => Interlocked.Read(ref _reports);

        // ─── ISdlInputDevice identity / capabilities ───
        // The keys live on CustomInputState.AnalogKeys, not in the numbered
        // arrays, so the row reports none of those. Its picker entries come
        // from MappingDisplayResolver's analog key block, the MIDI pattern.
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
        public int[] SupportedAxisIndices => Array.Empty<int>();
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => _candidate.VendorId;
        public ushort ProductId => _candidate.ProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => _candidate.Serial;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.AnalogKeyboard;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;
        public DeviceObjectItem[] GetDeviceObjects() => Array.Empty<DeviceObjectItem>();

        // ─── Lifecycle ───

        /// <summary>Opens the collection and starts the reader. Blocking I/O,
        /// so the sweep worker calls it, never the poll thread.</summary>
        public bool Open()
        {
            if (_disposed) return false;
            var channel = AnalogKeyboardHidChannel.Open(_candidate, writable: _poller != null);
            if (channel == null) return false;
            _channel = channel;
            _attached = true;
            try
            {
                _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "PadForge.AnalogKeyboard" };
                _reader.Start();
                return true;
            }
            catch
            {
                _attached = false;
                _channel = null;
                channel.Close();
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            var reader = _reader;
            _reader = null;
            _channel?.Abort();
            // The reader closes the channel on its way out. A reader still
            // stuck in a native call owns the channel's buffers, so they leak
            // rather than being freed under it (the headset rule).
            reader?.Join(2000);
        }

        private void ReaderLoop()
        {
            var channel = _channel;
            try
            {
                if (_poller == null) PushedLoop(channel);
                else PolledLoop(channel);
            }
            catch
            {
                // A vanishing device surfaces as a throw from any native
                // call. The sweep retires the row off IsAttached.
            }
            finally
            {
                _attached = false;
                lock (_stateLock) _live.ResetForReuse();
                try { channel.Close(); } catch { }
            }
        }

        private void PushedLoop(AnalogKeyboardHidChannel channel)
        {
            var buffer = new byte[Math.Max(_candidate.InputReportLength, (ushort)1)];
            int tolerated = NeedsSynapse ? RazerReadErrorsTolerated : 1;
            int errors = 0;
            long nextSynapseCheck = 0;
            while (!_disposed)
            {
                if (NeedsSynapse)
                {
                    long now = Environment.TickCount64;
                    if (now >= nextSynapseCheck)
                    {
                        nextSynapseCheck = now + SynapseCheckIntervalMs;
                        bool running = AnalogKeyboardHidRuntime.IsSynapseRunning();
                        _synapseRunning = running;
                        // Without Synapse the keyboard stops reporting, and a
                        // key held at that moment would stay down forever.
                        if (!running) lock (_stateLock) _live.ResetForReuse();
                    }
                }

                int n = channel.Receive(buffer, 250);
                if (_disposed) break;
                if (n < 0)
                {
                    if (++errors >= tolerated) break;
                    Thread.Sleep(50);
                    continue;
                }
                if (n == 0) continue;
                errors = 0;
                lock (_stateLock)
                {
                    if (AnalogKeyboardParsers.ParsePushed(_candidate.Protocol, buffer.AsSpan(0, n), _live,
                            _candidate.VendorId, _candidate.ProductId))
                        Interlocked.Increment(ref _reports);
                }
            }
        }

        private void PolledLoop(AnalogKeyboardHidChannel channel)
        {
            var pass = new AnalogKeyInputState();
            Func<int, bool> isHeld = IsHeldByWindows;
            int misses = 0;
            while (!_disposed)
            {
                long started = Environment.TickCount64;
                var result = _poller.Pass(channel, pass, isHeld);
                if (_disposed) break;
                if (result == AnalogPollResult.Failed) break;
                if (result == AnalogPollResult.NoAnswer)
                {
                    // An unanswered pass releases every key, as Soup's reader
                    // does, so a key held when the keyboard stops answering
                    // does not stay down while the misses add up.
                    lock (_stateLock) _live.ResetForReuse();
                    if (++misses >= PollMissesTolerated) break;
                    continue;
                }
                misses = 0;
                lock (_stateLock) pass.CopyInto(_live);
                Interlocked.Increment(ref _reports);
                long spent = Environment.TickCount64 - started;
                if (spent < PollPeriodMs) Thread.Sleep((int)(PollPeriodMs - spent));
            }
        }

        /// <summary>Whether Windows sees the key down, the polled families'
        /// cue to read it this pass. Soup asks DirectInput for the same fact
        /// (DigitalKeyboard), here through the virtual key the current layout
        /// gives the key's scan code.</summary>
        private bool IsHeldByWindows(int code)
            => _heldVirtualKeys.TryGetValue(code, out int vk) && (GetAsyncKeyState(vk) & 0x8000) != 0;

        private static int VirtualKeyForCode(int code)
        {
            int scan = AnalogKeyCodes.Ps2Scancode(code);
            if (scan == 0) return 0;
            return (int)MapVirtualKeyW((uint)scan, MapvkVscToVkEx);
        }

        // ─── State read (poll thread) ───

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            var s = _statePool.Next();
            s.AnalogKeys ??= new AnalogKeyInputState();
            lock (_stateLock) _live.CopyInto(s.AnalogKeys);
            return s;
        }

        /// <summary>Test seam: sets a key's depth as a report would.</summary>
        internal void InjectForTest(int code, float depth)
        {
            lock (_stateLock) _live.Set(code, depth);
        }

        /// <summary>Test seam: mark live without opening a device.</summary>
        internal void AttachForTest() => _attached = true;

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }

        private const uint MapvkVscToVkEx = 3;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
    }
}
