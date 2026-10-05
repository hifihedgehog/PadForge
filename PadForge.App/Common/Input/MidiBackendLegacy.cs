using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>The WinMM calls <see cref="MidiBackendLegacy"/> makes. A seam,
    /// so tests can stand in for the driver.</summary>
    internal interface IWinMmMidi
    {
        int InputCount();
        int OutputCount();

        /// <summary>The port's name, or null when Windows cannot describe it.</summary>
        string InputName(int id);
        string OutputName(int id);

        int OpenOutput(int id, out IntPtr handle);
        int SendShort(IntPtr handle, uint message);
        int CloseOutput(IntPtr handle);

        int OpenInput(int id, WinMmMidi.MidiInProc callback, out IntPtr handle);
        int StartInput(IntPtr handle);
        int StopInput(IntPtr handle);
        int ResetInput(IntPtr handle);
        int CloseInput(IntPtr handle);
    }

    /// <summary>winmm.dll's MIDI functions (mmeapi.h), declared as NAudio's
    /// MidiInterop declares them, with the device ID as the UINT the header
    /// gives it.</summary>
    internal sealed class WinMmMidi : IWinMmMidi
    {
        internal static readonly WinMmMidi Instance = new();

        internal const int MMSYSERR_NOERROR = 0;
        internal const int MMSYSERR_ALLOCATED = 4;
        internal const uint MIM_DATA = 0x3C3;
        private const uint CALLBACK_NULL = 0;
        private const uint CALLBACK_FUNCTION = 0x00030000;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MIDIINCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MIDIOUTCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public ushort wTechnology;
            public ushort wVoices;
            public ushort wNotes;
            public ushort wChannelMask;
            public uint dwSupport;
        }

        public int InputCount() => (int)midiInGetNumDevs();
        public int OutputCount() => (int)midiOutGetNumDevs();

        public string InputName(int id)
            => midiInGetDevCapsW((UIntPtr)(uint)id, out var caps, (uint)Marshal.SizeOf<MIDIINCAPSW>()) == MMSYSERR_NOERROR
                ? caps.szPname : null;

        public string OutputName(int id)
            => midiOutGetDevCapsW((UIntPtr)(uint)id, out var caps, (uint)Marshal.SizeOf<MIDIOUTCAPSW>()) == MMSYSERR_NOERROR
                ? caps.szPname : null;

        public int OpenOutput(int id, out IntPtr handle) => midiOutOpen(out handle, (uint)id, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        public int SendShort(IntPtr handle, uint message) => midiOutShortMsg(handle, message);
        public int CloseOutput(IntPtr handle) => midiOutClose(handle);

        public int OpenInput(int id, MidiInProc callback, out IntPtr handle)
            => midiInOpen(out handle, (uint)id, callback, IntPtr.Zero, CALLBACK_FUNCTION);
        public int StartInput(IntPtr handle) => midiInStart(handle);
        public int StopInput(IntPtr handle) => midiInStop(handle);
        public int ResetInput(IntPtr handle) => midiInReset(handle);
        public int CloseInput(IntPtr handle) => midiInClose(handle);

        [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
        [DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int midiInGetDevCapsW(UIntPtr uDeviceID, out MIDIINCAPSW pmic, uint cbmic);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int midiOutGetDevCapsW(UIntPtr uDeviceID, out MIDIOUTCAPSW pmoc, uint cbmoc);
        [DllImport("winmm.dll")] private static extern int midiOutOpen(out IntPtr phmo, uint uDeviceID, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] private static extern int midiOutShortMsg(IntPtr hmo, uint dwMsg);
        [DllImport("winmm.dll")] private static extern int midiOutClose(IntPtr hmo);
        [DllImport("winmm.dll")] private static extern int midiInOpen(out IntPtr phmi, uint uDeviceID, MidiInProc dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] private static extern int midiInStart(IntPtr hmi);
        [DllImport("winmm.dll")] private static extern int midiInStop(IntPtr hmi);
        [DllImport("winmm.dll")] private static extern int midiInReset(IntPtr hmi);
        [DllImport("winmm.dll")] private static extern int midiInClose(IntPtr hmi);
    }

    /// <summary>
    /// The legacy Windows MIDI API (WinMM) behind <see cref="IMidiBackend"/>,
    /// for a PC where neither Windows MIDI Services API runs. WinMM cannot
    /// create a port, so a MIDI slot opens an existing output port the user
    /// picked (<see cref="OpenOutputPort"/>) and
    /// <see cref="CreateVirtualEndpoint"/> is never called. The open, send
    /// and close sequences follow RtMidi (MidiInWinMM, MidiOutWinMM),
    /// PortMidi (pm_win/pmwinmm.c) and NAudio (MidiIn, MidiOut).
    ///
    /// <para>On the classic MIDI stack (Windows before 24H2 and Legacy API
    /// mode) a port serves one program at a time, so MIDI input opens only
    /// ports assigned to a slot (InputManager Phase 1e).</para>
    /// </summary>
    internal sealed class MidiBackendLegacy : IMidiBackend
    {
        /// <summary>Marks an input endpoint id as a WinMM port, so its
        /// device identity never collides with a Windows MIDI Services
        /// endpoint's.</summary>
        internal const string EndpointPrefix = "winmm:";

        private readonly IWinMmMidi _api;

        internal MidiBackendLegacy() : this(WinMmMidi.Instance) { }
        internal MidiBackendLegacy(IWinMmMidi api) => _api = api;

        public MidiApiKind Kind => MidiApiKind.Legacy;

        /// <summary>WinMM is part of every Windows. Counting the ports loads
        /// it and shows it answers.</summary>
        public bool Start()
        {
            _api.InputCount();
            _api.OutputCount();
            return true;
        }

        /// <summary>Nothing is held process-wide. Each port closes with the
        /// controller or input device that opened it.</summary>
        public void Stop(bool skipDispose) { }

        public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex)
            => throw new NotSupportedException("The legacy MIDI API cannot create a port.");

        public IMidiInputSession CreateInputSession(string name) => new InputSession(_api);

        /// <summary>The input ports, as endpoint ids
        /// (<see cref="EndpointPrefix"/> + port name) and names.</summary>
        public List<(string Id, string Name)> EnumerateNormalEndpoints()
        {
            var list = new List<(string Id, string Name)>();
            foreach (var (key, _) in NamePorts(Names(_api.InputCount(), _api.InputName)))
                list.Add((EndpointPrefix + key, key));
            return list;
        }

        /// <summary>The output ports, by the names the slot's port picker
        /// shows and saves.</summary>
        internal List<string> EnumerateOutputPorts()
        {
            var list = new List<string>();
            foreach (var (key, _) in NamePorts(Names(_api.OutputCount(), _api.OutputName)))
                list.Add(key);
            return list;
        }

        /// <summary>The output ports for the picker, read on a worker within
        /// <paramref name="timeoutMs"/>. On the new MIDI stack WinMM asks the
        /// MIDI service, which can hang, and the picker runs on the UI
        /// thread. A list that does not arrive in time comes back
        /// empty.</summary>
        internal static List<string> ListOutputPortsBounded(int timeoutMs = 2_000)
        {
            List<string> ports = null;
            var done = new ManualResetEventSlim(false);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { ports = new MidiBackendLegacy().EnumerateOutputPorts(); }
                catch { }
                finally { done.Set(); }
            });
            return done.Wait(timeoutMs) && ports != null ? ports : new List<string>();
        }

        /// <summary>Opens the output port saved as <paramref name="portKey"/>
        /// with no callback (RtMidi MidiOutWinMM::openPort). Throws
        /// <see cref="InvalidOperationException"/> saying why the slot cannot
        /// send: no port picked, the port is gone, or another program holds
        /// it.</summary>
        internal IMidiVirtualEndpoint OpenOutputPort(string portKey)
        {
            if (string.IsNullOrEmpty(portKey))
                throw new InvalidOperationException(
                    "Pick an output port on this MIDI slot. The legacy MIDI API cannot create a port.");
            int id = Resolve(portKey, _api.OutputCount(), _api.OutputName);
            if (id < 0)
                throw new InvalidOperationException($"MIDI output port '{portKey}' is not connected.");
            int r = _api.OpenOutput(id, out IntPtr handle);
            if (r == WinMmMidi.MMSYSERR_ALLOCATED)
                throw new InvalidOperationException($"MIDI output port '{portKey}' is in use by another program.");
            if (r != WinMmMidi.MMSYSERR_NOERROR || handle == IntPtr.Zero)
                throw new InvalidOperationException($"MIDI output port '{portKey}' did not open (MMRESULT {r}).");
            return new OutputEndpoint(_api, handle);
        }

        // ─────────────────────────────────────────────
        //  Port names
        // ─────────────────────────────────────────────

        private static List<string> Names(int count, Func<int, string> name)
        {
            var names = new List<string>(Math.Max(count, 0));
            for (int i = 0; i < count; i++) names.Add(name(i));
            return names;
        }

        /// <summary>
        /// Keys each port for the picker and for saved settings. WinMM names
        /// a port only by its index, which shifts when a device comes or
        /// goes, so PadForge keys a port by its name. A second port with the
        /// same name is "Name (2)", a third "Name (3)", in index order. A port
        /// Windows cannot describe gets no key.
        /// </summary>
        internal static List<(string Key, int Id)> NamePorts(IReadOnlyList<string> names)
        {
            var keys = new List<(string Key, int Id)>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int id = 0; id < names.Count; id++)
            {
                string name = names[id]?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                string key = name;
                for (int n = 2; !used.Add(key); n++)
                    key = $"{name} ({n})";
                keys.Add((key, id));
            }
            return keys;
        }

        /// <summary>The port's current index, or -1. Resolved at each open,
        /// since an index is only good until the next device change.</summary>
        private static int Resolve(string key, int count, Func<int, string> name)
        {
            foreach (var (k, id) in NamePorts(Names(count, name)))
                if (string.Equals(k, key, StringComparison.Ordinal)) return id;
            return -1;
        }

        // ─────────────────────────────────────────────
        //  Messages
        // ─────────────────────────────────────────────

        /// <summary>One channel voice message as midiOutShortMsg takes it:
        /// status byte low, then the two data bytes (RtMidi
        /// MidiOutWinMM::sendMessage).</summary>
        internal static uint PackShortMessage(Midi1Status status, int channel, int data1, int data2)
            => (uint)((((int)status << 4) | (channel & 0x0F))
                    | ((data1 & 0x7F) << 8)
                    | ((data2 & 0x7F) << 16));

        /// <summary>A received short message (MIM_DATA's dwParam1, status
        /// byte low) as a MIDI 1.0 channel voice UMP word, group 0, the form
        /// <see cref="MidiInputDevice.OnUmp"/> decodes. Returns 0 for anything
        /// that is not a channel voice message: a data byte where the status
        /// should be (PortMidi and RtMidi drop those too) and system
        /// messages. Program change and channel pressure carry one data
        /// byte, so the second is cleared.</summary>
        internal static uint ShortMessageToUmp(uint packed)
        {
            uint status = packed & 0xFF;
            if ((status & 0x80) == 0 || status >= 0xF0) return 0;
            uint data1 = (packed >> 8) & 0x7F;
            uint data2 = status >= 0xC0 && status < 0xE0 ? 0 : (packed >> 16) & 0x7F;
            return 0x20000000u | (status << 16) | (data1 << 8) | data2;
        }

        // ─────────────────────────────────────────────
        //  Output
        // ─────────────────────────────────────────────

        private sealed class OutputEndpoint : IMidiVirtualEndpoint
        {
            private readonly IWinMmMidi _api;
            private IntPtr _handle;

            internal OutputEndpoint(IWinMmMidi api, IntPtr handle)
            {
                _api = api;
                _handle = handle;
            }

            public void Send(Midi1Status status, int channel, int data1, int data2)
            {
                IntPtr handle = _handle;
                if (handle == IntPtr.Zero) return;
                int r = _api.SendShort(handle, PackShortMessage(status, channel, data1, data2));
                if (r != WinMmMidi.MMSYSERR_NOERROR)
                    throw new InvalidOperationException($"midiOutShortMsg failed (MMRESULT {r}).");
            }

            /// <summary>WinMM has no connection apart from the handle.</summary>
            public void DisconnectConnection() { }

            /// <summary>Closes the port without midiOutReset, which sends All
            /// Notes Off and Reset All Controllers on all 16 channels (RtMidi
            /// issue #222. PortMidi and NAudio close without it too). The
            /// controller has already sent a note off for every note it held.
            /// midiOutClose answers MIDIERR_STILLPLAYING only while long
            /// messages are queued, and this endpoint sends none.</summary>
            public void CloseSession()
            {
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero) _api.CloseOutput(handle);
            }
        }

        // ─────────────────────────────────────────────
        //  Input
        // ─────────────────────────────────────────────

        /// <summary>Tracks the open input ports so disposing the session
        /// closes them, the way a Windows MIDI Services session's
        /// connections end with it.</summary>
        private sealed class InputSession : IMidiInputSession
        {
            private readonly IWinMmMidi _api;
            private readonly object _lock = new();
            private readonly HashSet<InputConnection> _open = new();
            private bool _disposed;

            internal InputSession(IWinMmMidi api) => _api = api;

            public IMidiInputConnection CreateConnection(string endpointId, Action<uint, uint> onWords)
            {
                if (endpointId == null || !endpointId.StartsWith(EndpointPrefix, StringComparison.Ordinal))
                    return null;
                return new InputConnection(_api, this, endpointId.Substring(EndpointPrefix.Length), onWords);
            }

            internal bool Track(InputConnection connection)
            {
                lock (_lock)
                {
                    if (_disposed) return false;
                    _open.Add(connection);
                    return true;
                }
            }

            internal void Untrack(InputConnection connection)
            {
                lock (_lock) _open.Remove(connection);
            }

            public void Dispose()
            {
                InputConnection[] open;
                lock (_lock)
                {
                    _disposed = true;
                    open = new InputConnection[_open.Count];
                    _open.CopyTo(open);
                    _open.Clear();
                }
                foreach (var connection in open) connection.Close();
            }
        }

        private sealed class InputConnection : IMidiInputConnection
        {
            private readonly IWinMmMidi _api;
            private readonly InputSession _session;
            private readonly string _key;
            private readonly Action<uint, uint> _onWords;

            // WinMM keeps only the raw function pointer, so the delegate
            // lives as long as the port can call back (NAudio MidiIn holds
            // its callback in a field for the same reason).
            private readonly WinMmMidi.MidiInProc _callback;

            private IntPtr _handle;
            private volatile bool _detached;

            internal InputConnection(IWinMmMidi api, InputSession session, string key, Action<uint, uint> onWords)
            {
                _api = api;
                _session = session;
                _key = key;
                _onWords = onWords;
                _callback = OnMidiIn;
            }

            /// <summary>midiInOpen with a function callback, then midiInStart
            /// (RtMidi MidiInWinMM::openPort, PortMidi winmm_in_open). A
            /// start that fails resets and closes the port, as PortMidi
            /// does.</summary>
            public bool Open()
            {
                int id = Resolve(_key, _api.InputCount(), _api.InputName);
                if (id < 0) return false;
                if (_api.OpenInput(id, _callback, out IntPtr handle) != WinMmMidi.MMSYSERR_NOERROR
                    || handle == IntPtr.Zero)
                    return false;
                if (_api.StartInput(handle) != WinMmMidi.MMSYSERR_NOERROR)
                {
                    _api.ResetInput(handle);
                    _api.CloseInput(handle);
                    return false;
                }
                _handle = handle;
                if (!_session.Track(this))
                {
                    Close();
                    return false;
                }
                return true;
            }

            public void Detach() => _detached = true;

            public void Disconnect()
            {
                Close();
                _session.Untrack(this);
            }

            /// <summary>Stop, reset, close: PortMidi's winmm_in_close order.
            /// RtMidi resets before it stops, and both end in midiInClose.
            /// Never runs on the WinMM callback thread, where a MIDI call can
            /// deadlock (Microsoft's MidiInProc documentation).</summary>
            internal void Close()
            {
                _detached = true;
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle == IntPtr.Zero) return;
                _api.StopInput(handle);
                _api.ResetInput(handle);
                _api.CloseInput(handle);
            }

            private void OnMidiIn(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
            {
                if (wMsg != WinMmMidi.MIM_DATA || _detached) return;
                try
                {
                    uint word = ShortMessageToUmp((uint)(dwParam1.ToInt64() & 0xFFFFFFFF));
                    if (word != 0) _onWords(word, 0);
                }
                catch
                {
                    // Nothing may throw back into the driver's thread.
                }
            }
        }
    }
}
