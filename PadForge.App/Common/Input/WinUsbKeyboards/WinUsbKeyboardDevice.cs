using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input.WinUsbKeyboards
{
    /// <summary>One keyboard bound to PadForge's WinUSB driver, as the look
    /// for bound keyboards finds it: the interface path Windows gives the
    /// bound node, its IDs, and the row and name its takeover recorded.</summary>
    internal readonly record struct WinUsbKeyboard(string Path, ushort VendorId, ushort ProductId, Guid RowId, string Name);

    /// <summary>
    /// The row of a keyboard bound to WinUSB (#549). Once bound, the
    /// keyboard is no keyboard to Windows: it has no Raw Input row and types
    /// nothing anywhere. This reads its input reports from the interrupt IN
    /// pipe on a thread of its own and presents them as any keyboard row
    /// does, 256 keys by Windows virtual-key code with the keyboard row's
    /// names (<see cref="SdlKeyboardWrapper"/>), so its mappings and macros
    /// read it as they read the keyboard before.
    ///
    /// <para>The shape is the bound TouchSense mouse's row
    /// (<see cref="TouchSenseMouseDevice"/>): the reader owns the channel's
    /// life and closes it when it stops, and a row that is no longer
    /// attached returns no state, which takes it offline.</para>
    ///
    /// <para>The row takes the ID its takeover recorded, the keyboard's
    /// row as a Raw Input keyboard, so its slots, mappings and macros carry
    /// over. A pressed usage becomes a key index once, at the press, on
    /// the foreground window's layout (<see cref="KbmHidKeyMap.TargetOf"/>),
    /// and the release clears that index, so a layout switch while a key is
    /// down cannot strand it, the rule <see cref="KbmHidKeyMap.Resolve(int)"/>
    /// keeps for the other direction. Nothing the low-level hook holds is
    /// merged in: no key of this keyboard ever reaches the hook.</para>
    /// </summary>
    internal sealed class WinUsbKeyboardDevice : ISdlInputDevice
    {
        /// <summary>How long a blocking close waits for the reader to
        /// leave, above the read's own timeout.</summary>
        internal const int ReaderJoinMs = TouchSenseMouseDevice.ReaderJoinMs;

        private readonly IKeyboardChannel _channel;
        private readonly Func<IntPtr> _layout;
        private readonly int _numKeys = Math.Min(256, CustomInputState.MaxButtons);
        private readonly object _sync = new();
        private PooledInputStatePair _statePool;
        private readonly Thread _reader;
        private volatile bool _stop;
        private volatile bool _ended;

        // The keys down now, how many held usages press each one, and the
        // key each held usage pressed (-1 for a usage no key reads).
        // Guarded by _sync.
        private readonly bool[] _keys = new bool[256];
        private readonly int[] _holds = new int[256];
        private readonly Dictionary<uint, int> _held = new();

        // The reader's scratch.
        private readonly HashSet<uint> _pressed = new();
        private readonly List<uint> _released = new();

        /// <param name="start">False leaves the reader off, for a test that
        /// feeds reports itself (<see cref="Feed"/>).</param>
        /// <param name="layout">The input layout a press resolves on, by
        /// default the foreground window's.</param>
        public WinUsbKeyboardDevice(WinUsbKeyboard keyboard, IKeyboardChannel channel, bool start = true,
            Func<IntPtr> layout = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _layout = layout ?? KbmHidKeyMap.ForegroundLayout;
            VendorId = keyboard.VendorId;
            ProductId = keyboard.ProductId;
            DevicePath = keyboard.Path ?? string.Empty;
            Name = string.IsNullOrWhiteSpace(keyboard.Name)
                ? $"Keyboard {keyboard.VendorId:X4}:{keyboard.ProductId:X4}"
                : keyboard.Name;
            InstanceGuid = keyboard.RowId != Guid.Empty ? keyboard.RowId : IdentityFor(DevicePath);
            ProductGuid = Md5Guid("Keyboard");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            var all = new int[_numKeys];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            SupportedButtonIndices = all;
            if (start)
            {
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "WinUsbKeyboard" };
                _reader.Start();
            }
        }

        /// <summary>The row's ID when its takeover recorded none: hashed
        /// from the interface path in one case, as the TouchSense mouse's
        /// row is (<see cref="TouchSenseMouseDevice.IdentityFor"/>).</summary>
        public static Guid IdentityFor(string interfacePath)
            => Md5Guid("pfwinusbkeyboard:" + (interfacePath ?? string.Empty).ToUpperInvariant());

        /// <summary>The channel this row reads.</summary>
        internal IKeyboardChannel Channel => _channel;

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => _numKeys;
        public int RawButtonCount => 0;
        public int NumHats => 0;

        /// <summary>Every key, densely, as a keyboard row lists them
        /// (SdlKeyboardWrapper.SupportedButtonIndices).</summary>
        public int[] SupportedButtonIndices { get; }

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
        public ushort VendorId { get; }
        public ushort ProductId { get; }
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        /// <summary>False once the row was retired, its reader left or its
        /// channel was closed, which is how the device pass learns that
        /// the row is to be retired and the keyboard opened again.</summary>
        public bool IsAttached => !_stop && !_ended && _channel.IsOpen;

        public int GetInputDeviceType() => InputDeviceType.Keyboard;

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>A keyboard's key objects, named as every keyboard row's
        /// are (<see cref="SdlKeyboardWrapper.KeyObjects"/>).</summary>
        public DeviceObjectItem[] GetDeviceObjects() => SdlKeyboardWrapper.KeyObjects(SupportedButtonIndices);

        /// <summary>The keys down now. Null once the row is not attached,
        /// which takes it offline.</summary>
        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (!IsAttached) return null;
            var state = _statePool.Next();
            int n = Math.Min(_numKeys, state.Buttons.Length);
            lock (_sync) Array.Copy(_keys, state.Buttons, n);
            return state;
        }

        /// <summary>One input report as the pipe delivered it. A report the
        /// layout does not know is passed over.</summary>
        internal void Feed(ReadOnlySpan<byte> report)
        {
            if (!_channel.Reports.Decode(report, _pressed)) return;
            lock (_sync)
            {
                _released.Clear();
                foreach (var pair in _held)
                    if (!_pressed.Contains(pair.Key)) _released.Add(pair.Key);
                foreach (uint usage in _released)
                {
                    int key = _held[usage];
                    _held.Remove(usage);
                    if (key >= 0 && --_holds[key] <= 0)
                    {
                        _holds[key] = 0;
                        _keys[key] = false;
                    }
                }

                IntPtr layout = IntPtr.Zero;
                bool layoutRead = false;
                foreach (uint usage in _pressed)
                {
                    if (_held.ContainsKey(usage)) continue;
                    if (!layoutRead)
                    {
                        layout = _layout();
                        layoutRead = true;
                    }
                    int key = KbmHidKeyMap.TargetOf(new KbmHidKeyMap.HidKey((ushort)(usage >> 16), (ushort)usage), layout);
                    if (key < 0 || key >= _numKeys) key = -1;
                    _held[usage] = key;
                    if (key >= 0)
                    {
                        _holds[key]++;
                        _keys[key] = true;
                    }
                }
            }
        }

        private void ReadLoop()
        {
            var report = new byte[Math.Max(1, _channel.ReadLength)];
            try
            {
                while (!_stop)
                {
                    if (!_channel.TryRead(report, out int length)) break;
                    if (length > 0) Feed(report.AsSpan(0, length));
                }
            }
            catch
            {
            }
            finally
            {
                _ended = true;
                // On this thread, where waiting for the handle to close
                // holds nobody up.
                try { _channel.Dispose(); } catch { }
            }
        }

        /// <summary>Retires the row. It returns at once, since the poll
        /// thread calls it: the reader sees the flag within one read
        /// timeout and closes the channel as it leaves. A row with no
        /// reader closes its channel here.</summary>
        public void Dispose()
        {
            _stop = true;
            if (_reader != null) return;
            try { _channel.Dispose(); } catch { }
        }

        /// <summary>Retires the row and returns only once the keyboard's
        /// handle is closed, for the manager's shutdown, where the next
        /// engine start may open the same keyboard a moment later.</summary>
        internal void Close()
        {
            _stop = true;
            try { _channel.Dispose(); } catch { }
            var reader = _reader;
            if (reader != null && reader != Thread.CurrentThread)
            {
                try { reader.Join(ReaderJoinMs); } catch { }
            }
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
