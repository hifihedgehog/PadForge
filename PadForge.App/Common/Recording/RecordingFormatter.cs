using System;
using System.Diagnostics;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>What a line from a thread other than the poll thread is.</summary>
    internal enum SideKind : byte
    {
        /// <summary>A line of the diagnostics ring: free text, scrubbed when
        /// it is written.</summary>
        DiagnosticsLine,
        /// <summary>A marker, with the words its maker gave it or none.</summary>
        Marker,
        /// <summary>The program in front changed. The text is its path, A
        /// says whether a profile rule names it, and Extra is that profile's
        /// name.</summary>
        Front,
        /// <summary>A line whose text holds only what the recorder wrote
        /// itself, numbers and its own words, written as it is.</summary>
        Typed,
        /// <summary>Rumble written to a device that has its own writer: the
        /// device's path and the two motors in A.</summary>
        DeviceWrite,
        /// <summary>A line in the recorder's words with free text in it
        /// (#600), on the layer it names, scrubbed when it is written: a
        /// failed read with SDL's error, an exception, a setting changed.</summary>
        Note,
    }

    /// <summary>One event from a thread other than the poll thread, queued
    /// with its own capture time.</summary>
    internal struct RecordingSideEvent
    {
        public long Ticks;
        public SideKind Kind;
        public char Layer;
        /// <summary>The virtual controller's slot, or -1.</summary>
        public int Slot;
        public UserDevice Device;
        public string Text, Extra;
        public int A;
    }

    /// <summary>What the recorder's thread knows of the devices and virtual
    /// controllers right now, or at the start of the last minute it keeps:
    /// each change is folded in as it goes by, so a file that starts in the
    /// middle of a session can open with every state whole.</summary>
    internal sealed class RecordingState
    {
        internal readonly CustomInputState[] Devices = new CustomInputState[RecordingCapture.MaxDevices];
        internal readonly bool[] Online = new bool[RecordingCapture.MaxDevices];
        internal readonly RecordedOutputState[] Controllers = new RecordedOutputState[Input.InputManager.MaxPads];
        internal readonly int[] Sent = new int[Input.InputManager.MaxPads];

        internal RecordingState() => Array.Fill(Sent, -1);

        /// <summary>Takes a copy of everything <paramref name="other"/>
        /// knows, for a minute that starts being kept in the middle of a
        /// session.</summary>
        internal void CopyFrom(RecordingState other)
        {
            for (int i = 0; i < Devices.Length; i++)
            {
                Online[i] = other.Online[i];
                Devices[i] = other.Devices[i]?.Clone();
            }
            for (int i = 0; i < Controllers.Length; i++)
            {
                Sent[i] = other.Sent[i];
                if (other.Controllers[i] == null) { Controllers[i] = null; continue; }
                Controllers[i] ??= new RecordedOutputState();
                other.Controllers[i].CopyInto(Controllers[i]);
            }
        }

        internal void Fold(in RecordingEntry e)
        {
            char layer = RecordingCodec.LayerOf(e.Field);
            if (layer == RecordingCodec.LayerInput)
            {
                if (e.Subject >= Devices.Length) return;
                var state = Devices[e.Subject];
                if (state == null)
                {
                    Devices[e.Subject] = state = new CustomInputState();
                    RecordingCodec.Reset(state);
                }
                if (e.Field == RecordingField.Online)
                {
                    Online[e.Subject] = e.A != 0;
                    if (e.A != 0) RecordingCodec.Reset(state);
                    return;
                }
                // The counts of one pass belong to their pass alone.
                if (!RecordingCodec.IsPerPass(e.Field)) RecordingCodec.Apply(state, e.Field, e.Index, e.A, e.B, e.C);
            }
            else if (layer == RecordingCodec.LayerOutput)
            {
                if (e.Subject >= Controllers.Length) return;
                var state = Controllers[e.Subject] ??= new RecordedOutputState();
                if (!RecordingCodec.IsPerPass(e.Field)) RecordingCodec.Apply(state, e.Field, e.Index, e.A, e.B, e.C);
            }
            else if (e.Field == RecordingField.Sent && e.Subject < Sent.Length)
            {
                Sent[e.Subject] = e.A;
            }
        }
    }

    /// <summary>
    /// Turns what the capture handed over into the lines of a recording's
    /// events (#597): one line for all that changed on one subject at one
    /// time, a full frame when a device comes online and for each device and
    /// virtual controller every five seconds, and a line of its own for each
    /// event of another thread. Free text goes through the scrubber here.
    /// The recorder's thread only.
    /// </summary>
    internal sealed class RecordingFormatter
    {
        private sealed class DeviceLine
        {
            public string Alias;
            /// <summary>How its controls are named (format 2).</summary>
            public RecordingControlKind Kind;
            public readonly CustomInputState Mirror = new();
            public bool Online;
            public long NextFull = long.MaxValue;
        }

        private sealed class ControllerLine
        {
            public readonly RecordedOutputState Mirror = new();
            public bool Has;
            public long NextFull = long.MaxValue;
        }

        private readonly RecordingCapture _capture;
        private readonly PrivacyScrubber _scrubber;
        private readonly Func<UserDevice, string, string> _describe;
        private readonly long _epoch;
        private readonly DeviceLine[] _devices = new DeviceLine[RecordingCapture.MaxDevices];
        private readonly ControllerLine[] _controllers = new ControllerLine[Input.InputManager.MaxPads];
        private readonly System.Collections.Generic.HashSet<string> _declared = new(StringComparer.Ordinal);
        private static readonly long FullEvery = RecordingCodec.KeyframeSeconds * Stopwatch.Frequency;
        private long _nextFullAny = long.MaxValue;

        // The line under way.
        private bool _open, _full, _online;
        private long _ticks;
        private char _layer;
        private int _subjectKind, _subject;
        private readonly StringBuilder _tokens = new(256);
        private readonly StringBuilder _perPass = new(64);

        /// <summary>The lines made since the caller last took them.</summary>
        internal readonly StringBuilder Out = new(1 << 15);

        /// <summary>The parts this file holds (#600). A line of a part it
        /// leaves out is not written. A device's own line and a marker are
        /// written in every file.</summary>
        internal RecordingParts Parts = RecordingParts.All;

        private bool Holds(char layer)
        {
            var part = RecordingPartWords.PartOf(layer);
            return part == RecordingParts.None || (Parts & part) != 0;
        }

        /// <summary>The event lines written.</summary>
        internal long Lines { get; private set; }

        internal const string NewLine = "\r\n";

        /// <param name="epoch">The Stopwatch timestamp the file counts its
        /// seconds from.</param>
        /// <param name="describe">Gives a device's facts as the text of its
        /// line in the devices part, already scrubbed, from the device and
        /// its name in this file.</param>
        internal RecordingFormatter(RecordingCapture capture, PrivacyScrubber scrubber, long epoch, Func<UserDevice, string, string> describe)
        {
            _capture = capture;
            _scrubber = scrubber;
            _epoch = epoch;
            _describe = describe;
        }

        /// <summary>A Stopwatch timestamp as this file's microseconds.</summary>
        internal long Micros(long ticks)
        {
            long delta = ticks - _epoch;
            return delta >= 0
                ? delta / Stopwatch.Frequency * 1_000_000 + delta % Stopwatch.Frequency * 1_000_000 / Stopwatch.Frequency
                : -((-delta) / Stopwatch.Frequency * 1_000_000 + (-delta) % Stopwatch.Frequency * 1_000_000 / Stopwatch.Frequency);
        }

        /// <summary>Says a device's line already stands in the file's
        /// devices part, so it is not written again among the events.</summary>
        internal void MarkDeclared(string alias) => _declared.Add(alias);

        /// <summary>Starts a device from a state the recorder already has,
        /// for a file that begins in the middle of a session. Its first line
        /// is then a full frame at the file's start.</summary>
        internal void Seed(int track, CustomInputState state, bool online)
        {
            var line = Device(track, _epoch);
            if (line == null || state == null) return;
            state.CopyInto(line.Mirror);
            line.Online = online;
            line.NextFull = online ? _epoch : long.MaxValue;
            if (online && _epoch < _nextFullAny) _nextFullAny = _epoch;
        }

        /// <summary>Starts a virtual controller the same way.</summary>
        internal void Seed(int slot, RecordedOutputState state)
        {
            if (state == null) return;
            var line = _controllers[slot] ??= new ControllerLine();
            state.CopyInto(line.Mirror);
            line.Has = true;
            line.NextFull = _epoch;
            if (_epoch < _nextFullAny) _nextFullAny = _epoch;
        }

        private DeviceLine Device(int track, long ticks)
        {
            if ((uint)track >= (uint)_devices.Length) return null;
            var line = _devices[track];
            if (line != null) return line;
            var device = _capture.Track(track).Device;
            if (device == null) return null;
            line = _devices[track] = new DeviceLine { Kind = RecordingKinds.Of(device) };
            RecordingCodec.Reset(line.Mirror);
            line.Alias = Declare(device, ticks);
            return line;
        }

        /// <summary>A device's name in this file. A device the file has not
        /// described yet gets its line first, among the events.</summary>
        private string Declare(UserDevice device, long ticks)
        {
            string alias = _scrubber.DeviceAlias(device.InstanceGuidString);
            if (!_declared.Add(alias)) return alias;
            Close();
            RecordingCodec.AppendTime(Out, Micros(ticks));
            Out.Append(' ').Append(RecordingCodec.LayerInput).Append(' ').Append(alias).Append(" device");
            Out.Append(_describe(device, alias)).Append(NewLine);
            Lines++;
            return alias;
        }

        private static string ControllerAlias(int slot) => PrivacyScrubber.ControllerAlias(slot);

        // ─────────────────────────────────────────────
        //  The capture's entries
        // ─────────────────────────────────────────────

        internal void Entry(in RecordingEntry e)
        {
            char layer = RecordingCodec.LayerOf(e.Field);
            if (!Holds(layer)) return;
            if (e.Ticks >= _nextFullAny) FullFramesDue(e.Ticks);
            int kind = layer == RecordingCodec.LayerInput || RecordingCodec.NamesDevice(e.Field) ? 1
                : layer is RecordingCodec.LayerTiming or RecordingCodec.LayerMarker ? 0 : 2;

            DeviceLine device = null;
            ControllerLine controller = null;
            if (kind == 1)
            {
                device = Device(e.Subject, e.Ticks);
                if (device == null) return;
            }
            else if (kind == 2)
            {
                if (e.Subject >= _controllers.Length) return;
                controller = _controllers[e.Subject] ??= new ControllerLine();
            }

            if (!_open || _ticks != e.Ticks || _layer != layer || _subjectKind != kind || _subject != e.Subject)
            {
                Close();
                _open = true;
                _ticks = e.Ticks;
                _layer = layer;
                _subjectKind = kind;
                _subject = e.Subject;
            }

            if (layer == RecordingCodec.LayerInput)
            {
                if (e.Field == RecordingField.Online)
                {
                    device.Online = e.A != 0;
                    if (e.A != 0)
                    {
                        // What follows on this line is the whole state.
                        RecordingCodec.Reset(device.Mirror);
                        _full = _online = true;
                        device.NextFull = e.Ticks + FullEvery;
                        if (device.NextFull < _nextFullAny) _nextFullAny = device.NextFull;
                    }
                    else
                    {
                        device.NextFull = long.MaxValue;
                        Token(in e);
                    }
                    return;
                }
                RecordingCodec.Apply(device.Mirror, e.Field, e.Index, e.A, e.B, e.C);
                if (RecordingCodec.IsPerPass(e.Field)) PerPass(in e);
                else if (!_full) Token(in e);
            }
            else if (layer == RecordingCodec.LayerOutput)
            {
                if (!controller.Has)
                {
                    controller.Has = true;
                    _full = true;
                    controller.NextFull = e.Ticks + FullEvery;
                    if (controller.NextFull < _nextFullAny) _nextFullAny = controller.NextFull;
                }
                RecordingCodec.Apply(controller.Mirror, e.Field, e.Index, e.A, e.B, e.C);
                if (RecordingCodec.IsPerPass(e.Field)) PerPass(in e);
                else if (!_full) Token(in e);
            }
            else
            {
                Token(in e);
            }
        }

        /// <summary>How the line under way names its controls.</summary>
        private RecordingControlKind KindOfLine
            => _subjectKind == 1 && _devices[_subject] != null ? _devices[_subject].Kind : RecordingControlKind.Generic;

        private void Token(in RecordingEntry e)
        {
            _tokens.Append(' ');
            RecordingCodec.AppendNamedToken(_tokens, KindOfLine, e.Field, e.Index, e.A, e.B, e.C);
        }

        private void PerPass(in RecordingEntry e)
        {
            _perPass.Append(' ');
            RecordingCodec.AppendNamedToken(_perPass, KindOfLine, e.Field, e.Index, e.A, e.B, e.C);
        }

        /// <summary>Ends the line under way and writes it.</summary>
        internal void Close()
        {
            if (!_open) return;
            _open = false;
            RecordingCodec.AppendTime(Out, Micros(_ticks));
            Out.Append(' ').Append(_layer).Append(' ');
            if (_subjectKind == 1) Out.Append(_devices[_subject].Alias);
            else if (_subjectKind == 2) Out.Append(ControllerAlias(_subject));
            else Out.Append('-');

            if (_full)
            {
                Out.Append(' ').Append(RecordingCodec.FullToken);
                if (_subjectKind == 1)
                {
                    if (_online) Out.Append(" online");
                    RecordingCodec.AppendFull(Out, _devices[_subject].Mirror, _devices[_subject].Kind);
                }
                else RecordingCodec.AppendFull(Out, _controllers[_subject].Mirror);
            }
            else Out.Append(_tokens);
            Out.Append(_perPass).Append(NewLine);
            _tokens.Clear();
            _perPass.Clear();
            _full = _online = false;
            Lines++;
        }

        /// <summary>Writes the full frame of every device and virtual
        /// controller whose five seconds are up at <paramref name="upTo"/>.
        /// A frame is stamped with the time it was due, and says the state
        /// as it stood then.</summary>
        internal void FullFramesDue(long upTo)
        {
            if (upTo < _nextFullAny) return;
            Close();
            long next = long.MaxValue;
            bool input = Holds(RecordingCodec.LayerInput), output = Holds(RecordingCodec.LayerOutput);
            for (int i = 0; i < _devices.Length; i++)
            {
                var line = _devices[i];
                if (line == null || !line.Online || !input) continue;
                while (line.NextFull <= upTo)
                {
                    RecordingCodec.AppendTime(Out, Micros(line.NextFull));
                    Out.Append(' ').Append(RecordingCodec.LayerInput).Append(' ').Append(line.Alias)
                       .Append(' ').Append(RecordingCodec.FullToken).Append(" online");
                    RecordingCodec.AppendFull(Out, line.Mirror, line.Kind);
                    Out.Append(NewLine);
                    Lines++;
                    line.NextFull += FullEvery;
                }
                if (line.NextFull < next) next = line.NextFull;
            }
            for (int i = 0; i < _controllers.Length; i++)
            {
                var line = _controllers[i];
                if (line == null || !line.Has || !output) continue;
                while (line.NextFull <= upTo)
                {
                    RecordingCodec.AppendTime(Out, Micros(line.NextFull));
                    Out.Append(' ').Append(RecordingCodec.LayerOutput).Append(' ').Append(ControllerAlias(i))
                       .Append(' ').Append(RecordingCodec.FullToken);
                    RecordingCodec.AppendFull(Out, line.Mirror);
                    Out.Append(NewLine);
                    Lines++;
                    line.NextFull += FullEvery;
                }
                if (line.NextFull < next) next = line.NextFull;
            }
            _nextFullAny = next;
        }

        // ─────────────────────────────────────────────
        //  Other threads' events
        // ─────────────────────────────────────────────

        /// <summary>True for a diagnostics line that reports a failure, which
        /// the file carries on its error layer.</summary>
        internal static bool IsErrorLine(string line)
            => line.StartsWith("ERROR ", StringComparison.Ordinal)
            || line.StartsWith("RECOVERED ", StringComparison.Ordinal)
            || line.StartsWith("BINDERR", StringComparison.Ordinal);

        internal void Side(in RecordingSideEvent e)
        {
            if (e.Ticks >= _nextFullAny) FullFramesDue(e.Ticks);
            Close();
            string subject = "-";
            if (e.Kind == SideKind.DeviceWrite)
            {
                // A device with a writer of its own is named by its path.
                var device = DeviceOfPath(e.Text);
                if (device == null) return;
                subject = _scrubber.DeviceAlias(device.InstanceGuidString);
                if (!_declared.Contains(subject)) Declare(device, e.Ticks);
            }
            else if (e.Device != null)
            {
                subject = Declare(e.Device, e.Ticks);
            }
            else if (e.Slot >= 0)
            {
                subject = ControllerAlias(e.Slot);
            }

            char layer = e.Layer;
            string text;
            switch (e.Kind)
            {
                case SideKind.DiagnosticsLine:
                    layer = IsErrorLine(e.Text ?? string.Empty) ? RecordingCodec.LayerError : RecordingCodec.LayerLine;
                    text = _scrubber.Text(e.Text ?? string.Empty);
                    break;
                case SideKind.Note:
                    text = _scrubber.Text(e.Text ?? string.Empty);
                    break;
                case SideKind.Marker:
                    layer = RecordingCodec.LayerMarker;
                    text = string.IsNullOrWhiteSpace(e.Text) ? "mark" : "mark " + _scrubber.Text(e.Text.Trim());
                    break;
                case SideKind.Front:
                    layer = RecordingCodec.LayerWindow;
                    text = FrontText(e.Text, e.A != 0, e.Extra);
                    break;
                case SideKind.DeviceWrite:
                    layer = RecordingCodec.LayerFeedback;
                    _tokens.Clear();
                    RecordingCodec.AppendToken(_tokens, RecordingField.DeviceRumble, 0, e.A, 0, 0);
                    text = _tokens.ToString();
                    _tokens.Clear();
                    break;
                default:
                    text = e.Text ?? string.Empty;
                    break;
            }
            if (!Holds(layer)) return;

            RecordingCodec.AppendTime(Out, Micros(e.Ticks));
            Out.Append(' ').Append(layer).Append(' ').Append(subject).Append(' ');
            foreach (char c in text) Out.Append(c is '\r' or '\n' ? ' ' : c);
            Out.Append(NewLine);
            Lines++;
        }

        /// <summary>The program in front, by its file name (#600), which
        /// tells a game from a launcher: never its folder, and never a window
        /// title. A file name that holds a private value, a user's name among
        /// them, is scrubbed like any text.</summary>
        private string FrontText(string path, bool named, string profile)
        {
            if (string.IsNullOrEmpty(path)) return "front=none";
            string file = System.IO.Path.GetFileName(path);
            string front = string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ? "PadForge"
                : _scrubber.Text(file);
            _tokens.Clear();
            RecordingCodec.AppendFact(_tokens, "front", front);
            if (named && !string.IsNullOrEmpty(profile))
                RecordingCodec.AppendFact(_tokens, "profile", _scrubber.Alias(PrivateKind.ProfileName, profile));
            string text = _tokens.ToString(1, _tokens.Length - 1);
            _tokens.Clear();
            return text;
        }

        private UserDevice DeviceOfPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int count = _capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var device = _capture.Track(i).Device;
                if (device != null && string.Equals(device.DevicePath, path, StringComparison.OrdinalIgnoreCase)) return device;
            }
            return null;
        }

        /// <summary>Writes a line the recorder makes itself about one
        /// device, at a time of the file, its text scrubbed: what it says of
        /// a device as the file ends (#600).</summary>
        internal void Own(long ticks, char layer, UserDevice device, string text)
        {
            Close();
            string alias = Declare(device, ticks);
            if (!Holds(layer)) return;
            RecordingCodec.AppendTime(Out, Micros(ticks));
            Out.Append(' ').Append(layer).Append(' ').Append(alias).Append(' ').Append(_scrubber.Text(text ?? string.Empty)).Append(NewLine);
            Lines++;
        }

        /// <summary>Writes a line the recorder makes itself, at a time of
        /// the file: its start, its stop, the changes it had no room for.</summary>
        internal void Own(long ticks, char layer, string text)
        {
            Close();
            RecordingCodec.AppendTime(Out, Micros(ticks));
            Out.Append(' ').Append(layer).Append(" - ").Append(text).Append(NewLine);
            Lines++;
        }
    }
}
