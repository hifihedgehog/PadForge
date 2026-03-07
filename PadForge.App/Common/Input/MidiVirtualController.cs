using System;
using System.Runtime.InteropServices;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual controller that sends MIDI messages (CC for axes, Note On/Off for buttons)
    /// to a Windows MIDI output port (e.g., loopMIDI virtual port).
    /// Uses winmm P/Invoke — no external dependencies.
    /// </summary>
    internal sealed class MidiVirtualController : IVirtualController
    {
        // winmm P/Invoke
        [DllImport("winmm.dll")]
        private static extern int midiOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int midiOutGetDevCapsW(uint uDeviceID, ref MIDIOUTCAPS lpMidiOutCaps, int cbMidiOutCaps);

        [DllImport("winmm.dll")]
        private static extern int midiOutOpen(out IntPtr lphmo, uint uDeviceID, IntPtr dwCallback, IntPtr dwCallbackInstance, int dwFlags);

        [DllImport("winmm.dll")]
        private static extern int midiOutClose(IntPtr hmo);

        [DllImport("winmm.dll")]
        private static extern int midiOutShortMsg(IntPtr hmo, uint dwMsg);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MIDIOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public ushort wTechnology;
            public ushort wVoices;
            public ushort wNotes;
            public ushort wChannelMask;
            public uint dwSupport;
        }

        // MIDI status bytes
        private const byte NoteOff = 0x80;
        private const byte NoteOn = 0x90;
        private const byte ControlChange = 0xB0;

        private IntPtr _handle;
        private readonly uint _deviceId;
        private readonly int _channel; // 0-15
        private bool _connected;
        private bool _disposed;

        // Change detection — only send messages when values actually change.
        private readonly byte[] _lastCcValues = new byte[6]; // LX, LY, LT, RX, RY, RT
        private ushort _lastButtons;

        // Default CC mappings: LX=1, LY=2, LT=3, RX=4, RY=5, RT=6
        // These can be customized via MidiSlotConfig.
        internal int[] CcNumbers { get; set; } = { 1, 2, 3, 4, 5, 6 };

        // Default note mappings: A=60, B=61, X=62, Y=63, LB=64, RB=65, Back=66, Start=67, LS=68, RS=69, Guide=70
        internal int[] NoteNumbers { get; set; } = { 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70 };

        // Note velocity for button presses.
        internal byte Velocity { get; set; } = 127;

        public VirtualControllerType Type => VirtualControllerType.Midi;
        public bool IsConnected => _connected;

        public MidiVirtualController(uint deviceId, int channel)
        {
            _deviceId = deviceId;
            _channel = Math.Clamp(channel, 0, 15);
        }

        public void Connect()
        {
            if (_connected) return;
            int result = midiOutOpen(out _handle, _deviceId, IntPtr.Zero, IntPtr.Zero, 0);
            if (result != 0)
                throw new InvalidOperationException($"midiOutOpen failed with error {result} for device {_deviceId}");
            _connected = true;

            // Initialize change detection to neutral values.
            for (int i = 0; i < _lastCcValues.Length; i++)
                _lastCcValues[i] = 64; // center for axes
            _lastButtons = 0;
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;

            // Send Note Off for any held buttons.
            for (int i = 0; i < 11 && i < NoteNumbers.Length; i++)
            {
                if ((_lastButtons & (1 << i)) != 0)
                    SendNoteOff(NoteNumbers[i]);
            }
            _lastButtons = 0;

            if (_handle != IntPtr.Zero)
            {
                midiOutClose(_handle);
                _handle = IntPtr.Zero;
            }
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (!_connected || _handle == IntPtr.Zero) return;

            // Axes → CC messages (convert to 0-127 range)
            // Stick axes: -32768..32767 → 0..127
            // Trigger axes: 0..65535 → 0..127
            byte lx = AxisToMidi(gp.ThumbLX);
            byte ly = AxisToMidi(gp.ThumbLY);
            byte lt = TriggerToMidi(gp.LeftTrigger);
            byte rx = AxisToMidi(gp.ThumbRX);
            byte ry = AxisToMidi(gp.ThumbRY);
            byte rt = TriggerToMidi(gp.RightTrigger);

            byte[] ccValues = { lx, ly, lt, rx, ry, rt };
            for (int i = 0; i < 6 && i < CcNumbers.Length; i++)
            {
                if (ccValues[i] != _lastCcValues[i])
                {
                    SendCC(CcNumbers[i], ccValues[i]);
                    _lastCcValues[i] = ccValues[i];
                }
            }

            // Buttons → Note On/Off
            ushort buttons = gp.Buttons;
            ushort changed = (ushort)(buttons ^ _lastButtons);
            for (int i = 0; i < 11 && i < NoteNumbers.Length; i++)
            {
                ushort mask = (ushort)(1 << i);
                if ((changed & mask) == 0) continue;

                if ((buttons & mask) != 0)
                    SendNoteOn(NoteNumbers[i], Velocity);
                else
                    SendNoteOff(NoteNumbers[i]);
            }
            _lastButtons = buttons;
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // MIDI has no rumble feedback — no-op.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ─────────────────────────────────────────────
        //  MIDI message helpers
        // ─────────────────────────────────────────────

        private void SendCC(int ccNumber, byte value)
        {
            // CC message: [status | channel] [cc#] [value]
            uint msg = (uint)((ControlChange | _channel) | (ccNumber << 8) | (value << 16));
            midiOutShortMsg(_handle, msg);
        }

        private void SendNoteOn(int note, byte velocity)
        {
            uint msg = (uint)((NoteOn | _channel) | (note << 8) | (velocity << 16));
            midiOutShortMsg(_handle, msg);
        }

        private void SendNoteOff(int note)
        {
            uint msg = (uint)((NoteOff | _channel) | (note << 8));
            midiOutShortMsg(_handle, msg);
        }

        // ─────────────────────────────────────────────
        //  Value conversion
        // ─────────────────────────────────────────────

        private static byte AxisToMidi(short value)
        {
            // -32768..32767 → 0..127
            return (byte)((value + 32768) * 127 / 65535);
        }

        private static byte TriggerToMidi(ushort value)
        {
            // 0..65535 → 0..127
            return (byte)(value * 127 / 65535);
        }

        // ─────────────────────────────────────────────
        //  Static helpers for port enumeration
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns available MIDI output port names.
        /// </summary>
        public static string[] GetOutputPortNames()
        {
            int count = midiOutGetNumDevs();
            var names = new string[count];
            for (uint i = 0; i < count; i++)
            {
                var caps = new MIDIOUTCAPS();
                if (midiOutGetDevCapsW(i, ref caps, Marshal.SizeOf<MIDIOUTCAPS>()) == 0)
                    names[i] = caps.szPname;
                else
                    names[i] = $"MIDI Out {i}";
            }
            return names;
        }

        /// <summary>
        /// Finds the device ID for a port by name. Returns null if not found.
        /// </summary>
        public static uint? FindDeviceIdByName(string portName)
        {
            if (string.IsNullOrEmpty(portName)) return null;
            int count = midiOutGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                var caps = new MIDIOUTCAPS();
                if (midiOutGetDevCapsW(i, ref caps, Marshal.SizeOf<MIDIOUTCAPS>()) == 0
                    && string.Equals(caps.szPname, portName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return null;
        }
    }
}
