using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Engine.Recording
{
    /// <summary>Where a finding of a recording's summary points.</summary>
    public enum FindingPlace
    {
        /// <summary>The device or its driver.</summary>
        Device,
        /// <summary>The settings: a mapping row.</summary>
        Settings,
        /// <summary>The virtual controller.</summary>
        VirtualController,
        /// <summary>Feedback: what a game sent back and what reached a device.</summary>
        Feedback,
        /// <summary>Timing.</summary>
        Timing,
    }

    /// <summary>One thing a summary found, with the time it was first seen.</summary>
    public sealed record RecordingFinding(FindingPlace Place, long Microseconds, string Text);

    /// <summary>
    /// What a problem recording says went wrong (#600), read from its own
    /// lines. One pass over the events fills it, and <see cref="Lines"/>
    /// writes the summary that stands first in the file.
    ///
    /// <para>The rules follow the path an input takes, and the first finding
    /// is the first place on that path where something was off: a control
    /// that never came to rest, a failed read or a device that went offline
    /// (the device or its driver), an input that moved while the row mapped
    /// to it never changed its target (the settings), a frame that changed
    /// while the virtual controller was absent or the send failed (the
    /// virtual controller), a game's rumble no device was written after
    /// (feedback), and a stall or a low rate near a marker (timing).</para>
    /// </summary>
    public sealed class RecordingSummary
    {
        /// <summary>How far each side of a marker its window reaches.</summary>
        public const long MarkerWindowMicros = 5_000_000;

        /// <summary>A control counts as never at rest only on a device that
        /// was online at least this long.</summary>
        public const long ShortestJudgedMicros = 5_000_000;

        /// <summary>How near a rest position an axis has to come to count as
        /// having rested: a twentieth of its travel.</summary>
        public const int RestBand = 3277;

        /// <summary>How far an axis has to travel to count as moved: half its
        /// range.</summary>
        public const int MovedSpan = 32768;

        /// <summary>How long after a game's rumble a write to one of the
        /// controller's devices is looked for.</summary>
        public const long RumbleWaitMicros = 1_000_000;

        /// <summary>A second counts as slow when its passes fall under this
        /// share of the set rate.</summary>
        public const double SlowShare = 0.9;

        /// <summary>Two changes of a device further apart than this belong
        /// to two runs of motion, and the time between them is rest, no gap.</summary>
        public const long RunBreakMicros = 250_000;

        /// <summary>The most markers the summary takes, as it writes at most
        /// 40 findings. A line says how many more the file holds.</summary>
        public const int MostMarkers = 40;

        /// <summary>The most mapping rows the summary judges: 1,024 for each
        /// of the 16 virtual controllers. A row past it is counted as not
        /// checked.</summary>
        public const int MostRows = 16 * 1024;

        private sealed class AxisStat
        {
            public bool Seen, Rested;
            public int Min = int.MaxValue, Max = int.MinValue, Current;
            public long Since = -1;
            public int RestValue;
            public long RestHeld = -1;
        }

        private sealed class DeviceStats
        {
            public RecordedDevice Device;
            public long OnlineSince = -1, OnlineMicros;
            public readonly AxisStat[] Axes = new AxisStat[CustomInputState.MaxAxis];
            public readonly AxisStat[] Sliders = new AxisStat[CustomInputState.MaxSliders];
            public readonly long[] ButtonDownSince = new long[CustomInputState.MaxButtons];
            public readonly long[] ButtonDownMicros = new long[CustomInputState.MaxButtons];
            public readonly int[] ButtonPresses = new int[CustomInputState.MaxButtons];
            public readonly long[] HatOffSince = new long[CustomInputState.MaxPovs];
            public readonly long[] HatOffMicros = new long[CustomInputState.MaxPovs];
            public readonly int[] HatDirections = new int[CustomInputState.MaxPovs];
            public readonly bool[] WasDown = new bool[CustomInputState.MaxButtons];
            public readonly int[] WasHat = { -1, -1, -1, -1 };
            public long Changes, LastChange = -1, LongestGap, BusiestSecond = -1;
            public int BusiestCount, SecondCount;
            public long Second = -1;
            public readonly int[] IntervalBuckets = new int[IntervalEdges.Length + 1];
            public int Offlines;

            public DeviceStats()
            {
                Array.Fill(ButtonDownSince, -1L);
                Array.Fill(HatOffSince, -1L);
            }
        }

        // The upper edges, in microseconds, of the buckets the time between
        // two changes of a run falls in.
        private static readonly long[] IntervalEdges = { 250, 500, 750, 1_000, 1_500, 2_000, 3_000, 4_000, 6_000, 8_000, 12_000, 16_000, 24_000, 33_000, 50_000, 100_000, 250_000 };

        private sealed class ControllerStats
        {
            public RecordedController Controller;
            public readonly RecordedOutputState Before = new();
            public bool HasBefore;
            public long Changes, ChangesNotSent, FirstNotSent = -1;
            public int NotSentState;
            public int Sent = -1;
            public long SentSince = -1;
            public readonly long[] SentMicros = new long[6];
            // What changed at least once after the first full frame.
            public int ButtonsChanged, ExtraChanged, MouseButtonsChanged;
            public bool LeftTrigger, RightTrigger, LeftX, LeftY, RightX, RightY, MoveX, MoveY, Scroll, ScrollH;
            public readonly bool[] RawAxes = new bool[RecordedOutputState.RawAxisCount];
            public readonly uint[] RawButtons = new uint[RecordedOutputState.RawButtonWords];
            public readonly bool[] RawHats = new bool[RecordedOutputState.RawHatCount];
            public readonly ulong[] Keys = new ulong[RecordedOutputState.KeyWords];
            public readonly bool[] MidiCc = new bool[RecordedOutputState.MidiCount];
            public readonly bool[] MidiNotes = new bool[RecordedOutputState.MidiCount];
            public bool VrLeft, VrRight;
            public bool Rumbling;
            public int RumbleRises, RumbleUnanswered;
            public long FirstUnanswered = -1;
            /// <summary>The times of the rumble asked for that waits for a
            /// write, oldest first.</summary>
            public readonly Queue<long> Waiting = new();
        }

        private sealed class MarkerWindow
        {
            public long At;
            public string Text;
            public readonly SortedDictionary<string, WindowStat> Before = new(StringComparer.Ordinal);
            public readonly SortedDictionary<string, WindowStat> After = new(StringComparer.Ordinal);
            public readonly List<string> Notable = new();
        }

        private sealed class WindowStat
        {
            public int Count;
            public bool HasRange;
            public int Min = int.MaxValue, Max = int.MinValue;

            public void Add(int count, bool hasRange, int min, int max)
            {
                Count += count;
                if (!hasRange) return;
                HasRange = true;
                if (min < Min) Min = min;
                if (max > Max) Max = max;
            }
        }

        // The last five seconds in tenths of a second, for the part of a
        // marker's window that came before it.
        private const int SlotMicros = 100_000;
        private const int SlotCount = (int)(MarkerWindowMicros / SlotMicros);

        private sealed class Slot
        {
            public long Index = -1;
            public readonly Dictionary<string, WindowStat> Stats = new(StringComparer.Ordinal);
            public readonly List<(long At, string Text)> Notable = new();
        }

        private readonly Slot[] _slots = new Slot[SlotCount];
        private readonly Dictionary<int, DeviceStats> _devices = new();
        private readonly Dictionary<int, ControllerStats> _controllers = new();
        private readonly List<MarkerWindow> _markers = new();
        private long _markersLeftOut;
        private readonly List<RecordingFinding> _findings = new();
        private readonly List<(long At, string Text)> _stalls = new();
        private readonly List<(long At, string Text)> _slowSeconds = new();
        private readonly List<(long At, string Text)> _errors = new();
        private readonly Dictionary<int, HashSet<int>> _devicesOfController = new();
        private readonly List<Dictionary<string, string>> _rows = new();

        private long _last;
        private long _lines, _inputLines, _outputLines, _diagLines, _windowChanges;
        private long _dropped;
        private int _errorCount;
        private double _lowestRate = double.MaxValue, _setRate;
        private long _longestPass, _passes;
        private double _captureSum;
        private int _timingSeconds, _gaps;
        private long _worstGap;
        private bool _resting;
        private int _rowsChecked, _rowsSkipped;
        private string _ending = string.Empty;

        /// <summary>What the summary found, in the order of the path an
        /// input takes.</summary>
        public IReadOnlyList<RecordingFinding> Findings => _findings;

        /// <summary>The time of the last event, which is how long the
        /// recording ran.</summary>
        public long DurationMicros => _last > 0 ? _last : 0;

        public long EventLines => _lines;

        /// <summary>Reads every event of <paramref name="reader"/> and works
        /// the summary out. The reader stands at its end afterwards.</summary>
        public static RecordingSummary Read(RecordingReader reader)
        {
            var summary = new RecordingSummary();
            for (int i = 0; i < SlotCount; i++) summary._slots[i] = new Slot();
            summary.ReadSettings(reader.Settings);
            while (reader.Read()) summary.Take(reader);
            summary.Finish(reader);
            return summary;
        }

        // ─────────────────────────────────────────────
        //  Settings
        // ─────────────────────────────────────────────

        private void ReadSettings(IReadOnlyList<string> settings)
        {
            foreach (string line in settings)
            {
                if (line.StartsWith("assign ", StringComparison.Ordinal))
                {
                    var facts = RecordingCodec.ParseFacts(line.AsSpan(7));
                    int device = Number(facts, "device", 'D'), controller = Number(facts, "controller", 'C');
                    // A file from anyone holds as many assign lines as it
                    // likes: only the controllers and devices a reader keeps
                    // are taken.
                    if (device <= 0 || controller <= 0 || controller > RecordingReader.MostControllers) continue;
                    if (!_devicesOfController.TryGetValue(controller, out var set))
                        _devicesOfController[controller] = set = new HashSet<int>();
                    if (set.Count < RecordingReader.MostDevices) set.Add(device);
                }
                else if (line.StartsWith("row ", StringComparison.Ordinal))
                {
                    // A file from anyone holds as many row lines as it likes.
                    if (_rows.Count < MostRows) _rows.Add(RecordingCodec.ParseFacts(line.AsSpan(4)));
                    else _rowsSkipped++;
                }
            }
        }

        private static int Number(Dictionary<string, string> facts, string key, char letter)
            => facts.TryGetValue(key, out string value) ? RecordingCodec.SubjectNumber(value, letter) : 0;

        // ─────────────────────────────────────────────
        //  One event
        // ─────────────────────────────────────────────

        private void Take(RecordingReader reader)
        {
            long at = reader.Microseconds;
            _lines++;
            // Lines kept from before the start have times under zero. They
            // are context and are judged by no rule.
            if (at < 0)
            {
                if (reader.Layer == RecordingCodec.LayerLine) _diagLines++;
                return;
            }
            if (at > _last) _last = at;

            // Rumble a game asked for more than a second ago and no device
            // was written after has gone unanswered.
            // The oldest waits first, so a line looks at no more than the
            // rumble that ran out of time.
            foreach (var c in _controllers.Values)
            {
                while (c.Waiting.Count > 0 && at - c.Waiting.Peek() > RumbleWaitMicros)
                {
                    long asked = c.Waiting.Dequeue();
                    if (c.FirstUnanswered < 0) c.FirstUnanswered = asked;
                    c.RumbleUnanswered++;
                }
            }

            switch (reader.Layer)
            {
                case RecordingCodec.LayerInput: TakeInput(reader, at); break;
                case RecordingCodec.LayerOutput: TakeOutput(reader, at); break;
                case RecordingCodec.LayerSent: TakeSent(reader, at); break;
                case RecordingCodec.LayerFeedback: TakeFeedback(reader, at); break;
                case RecordingCodec.LayerTiming: TakeTiming(reader, at); break;
                case RecordingCodec.LayerWindow: TakeWindow(reader); break;
                case RecordingCodec.LayerError:
                    _errorCount++;
                    if (_errors.Count < 5) _errors.Add((at, Clip(reader.Payload.ToString(), 160)));
                    Note(at, "error: " + Clip(reader.Payload.ToString(), 100));
                    break;
                case RecordingCodec.LayerLine: _diagLines++; break;
                case RecordingCodec.LayerMarker: TakeMarker(reader, at); break;
            }
        }

        /// <summary>The program in front changed when a front line names
        /// another than the line before it. The first names where it stood,
        /// and the window's other lines (one that stopped answering) are no
        /// change of program.</summary>
        private void TakeWindow(RecordingReader reader)
        {
            var payload = reader.Payload;
            if (!payload.StartsWith("front=")) return;
            string front = payload.ToString();
            if (_front != null && front != _front) _windowChanges++;
            _front = front;
        }

        private string _front;

        private static string Clip(string text, int length)
            => text.Length <= length ? text : text.Substring(0, length) + "...";

        private DeviceStats StatsOf(RecordedDevice device)
        {
            if (!_devices.TryGetValue(device.Number, out var stats))
                _devices[device.Number] = stats = new DeviceStats { Device = device };
            return stats;
        }

        private ControllerStats StatsOf(RecordedController controller)
        {
            if (!_controllers.TryGetValue(controller.Number, out var stats))
                _controllers[controller.Number] = stats = new ControllerStats { Controller = controller };
            return stats;
        }

        private void TakeInput(RecordingReader reader, long at)
        {
            var device = reader.Device;
            if (device == null) return;
            var stats = StatsOf(device);
            var payload = reader.Payload;
            if (payload.StartsWith("device ")) return;
            _inputLines++;

            // Online and offline, as the line says.
            if (payload.Contains("offline=", StringComparison.Ordinal))
            {
                CloseOnline(stats, at);
                stats.Offlines++;
                string why = RecordingCodec.OfflineReason.Word(device.OfflineReason);
                string text = device.Alias + " went offline at " + Time(at) + " (" + why.Replace('-', ' ') + ").";
                if (stats.Offlines == 1)
                    _findings.Add(new RecordingFinding(FindingPlace.Device, at, text + " Look at the device or its driver."));
                Note(at, device.Alias + " went offline (" + why.Replace('-', ' ') + ")");
            }
            else if (device.Online && stats.OnlineSince < 0)
            {
                stats.OnlineSince = at;
                // A device that comes online starts its controls anew.
                for (int i = 0; i < stats.WasDown.Length; i++)
                    if (stats.WasDown[i]) stats.ButtonDownSince[i] = at;
                for (int i = 0; i < stats.WasHat.Length; i++)
                    if (stats.WasHat[i] != -1) stats.HatOffSince[i] = at;
            }

            var state = device.State;
            bool changed = false;
            for (int i = 0; i < CustomInputState.MaxAxis; i++)
                changed |= TakeAxis(ref stats.Axes[i], state.Axis[i], at, reader.IsFullFrame, device.Alias, ControlName(device, RecordingField.Axis, i));
            for (int i = 0; i < CustomInputState.MaxSliders; i++)
                changed |= TakeAxis(ref stats.Sliders[i], state.Sliders[i], at, reader.IsFullFrame, device.Alias, ControlName(device, RecordingField.Slider, i));
            for (int i = 0; i < CustomInputState.MaxButtons; i++)
            {
                bool down = state.Buttons[i];
                if (down == stats.WasDown[i]) continue;
                stats.WasDown[i] = down;
                changed = true;
                if (down)
                {
                    stats.ButtonPresses[i]++;
                    stats.ButtonDownSince[i] = at;
                    Window(at, device.Alias + " " + ControlName(device, RecordingField.Button, i), 1, false, 0, 0);
                }
                else if (stats.ButtonDownSince[i] >= 0)
                {
                    stats.ButtonDownMicros[i] += at - stats.ButtonDownSince[i];
                    stats.ButtonDownSince[i] = -1;
                }
            }
            for (int i = 0; i < CustomInputState.MaxPovs; i++)
            {
                int hat = state.Povs[i];
                if (hat == stats.WasHat[i]) continue;
                if (stats.WasHat[i] != -1 && stats.HatOffSince[i] >= 0)
                {
                    stats.HatOffMicros[i] += at - stats.HatOffSince[i];
                    stats.HatOffSince[i] = -1;
                }
                stats.WasHat[i] = hat;
                changed = true;
                if (hat != -1)
                {
                    stats.HatOffSince[i] = at;
                    stats.HatDirections[i] |= HatBits(hat);
                    Window(at, device.Alias + " " + ControlName(device, RecordingField.Hat, i), 1, false, 0, 0);
                }
            }

            // A full frame restates the state and is no report of its own.
            if (reader.IsFullFrame) return;
            if (!changed) Window(at, device.Alias + " other", 1, false, 0, 0);
            stats.Changes++;
            long second = at / 1_000_000;
            if (second != stats.Second)
            {
                stats.Second = second;
                stats.SecondCount = 0;
            }
            if (++stats.SecondCount > stats.BusiestCount)
            {
                stats.BusiestCount = stats.SecondCount;
                stats.BusiestSecond = second;
            }
            if (stats.LastChange >= 0)
            {
                long gap = at - stats.LastChange;
                if (gap <= RunBreakMicros)
                {
                    if (gap > stats.LongestGap) stats.LongestGap = gap;
                    int bucket = 0;
                    while (bucket < IntervalEdges.Length && gap > IntervalEdges[bucket]) bucket++;
                    stats.IntervalBuckets[bucket]++;
                }
            }
            stats.LastChange = at;
        }

        private bool TakeAxis(ref AxisStat stat, int value, long at, bool full, string alias, string name)
        {
            if (stat == null)
            {
                // An axis that has never left zero is one the device may not
                // have at all.
                if (value == 0) return false;
                stat = new AxisStat { Current = value, Since = at, Seen = true };
                // On a line that is no full frame the axis was at zero until
                // now, which is a rest position.
                if (!full) Range(stat, 0);
                Range(stat, value);
                return !full;
            }
            if (value == stat.Current) return false;
            Hold(stat, at);
            stat.Current = value;
            stat.Since = at;
            Range(stat, value);
            Window(at, alias + " " + name, 1, true, value, value);
            return true;
        }

        private static void Range(AxisStat stat, int value)
        {
            if (value < stat.Min) stat.Min = value;
            if (value > stat.Max) stat.Max = value;
            // In long: a file from anyone can hold any int, and the distance
            // of int.MinValue from the center is no int.
            if (Math.Abs((long)value - 32768) <= RestBand || value <= RestBand || value >= 65535 - RestBand) stat.Rested = true;
        }

        private static void Hold(AxisStat stat, long until)
        {
            long held = until - stat.Since;
            if (held <= stat.RestHeld) return;
            stat.RestHeld = held;
            stat.RestValue = stat.Current;
        }

        /// <summary>A hat's value in hundredths of a degree as its
        /// directions: bit 0 up, 1 right, 2 down, 3 left.</summary>
        private static int HatBits(int hundredths)
        {
            if (hundredths < 0) return 0;
            int eighth = (hundredths + 2250) % 36000 / 4500;
            return eighth switch
            {
                0 => 1, 1 => 1 | 2, 2 => 2, 3 => 2 | 4, 4 => 4, 5 => 4 | 8, 6 => 8, _ => 8 | 1,
            };
        }

        private static void CloseOnline(DeviceStats stats, long at)
        {
            if (stats.OnlineSince < 0) return;
            stats.OnlineMicros += at - stats.OnlineSince;
            stats.OnlineSince = -1;
            for (int i = 0; i < stats.ButtonDownSince.Length; i++)
            {
                if (stats.ButtonDownSince[i] < 0) continue;
                stats.ButtonDownMicros[i] += at - stats.ButtonDownSince[i];
                stats.ButtonDownSince[i] = -1;
            }
            for (int i = 0; i < stats.HatOffSince.Length; i++)
            {
                if (stats.HatOffSince[i] < 0) continue;
                stats.HatOffMicros[i] += at - stats.HatOffSince[i];
                stats.HatOffSince[i] = -1;
            }
        }

        private void TakeOutput(RecordingReader reader, long at)
        {
            var controller = reader.Controller;
            if (controller == null) return;
            var stats = StatsOf(controller);
            _outputLines++;
            var now = controller.Output;
            var was = stats.Before;
            if (!stats.HasBefore)
            {
                now.CopyInto(was);
                stats.HasBefore = true;
                return;
            }

            bool changed = false;
            string alias = controller.Alias;
            if (now.Buttons != was.Buttons)
            {
                stats.ButtonsChanged |= now.Buttons ^ was.Buttons;
                Window(at, alias + " bt", 1, false, 0, 0);
                changed = true;
            }
            if (now.Extra != was.Extra) { stats.ExtraChanged |= now.Extra ^ was.Extra; changed = true; }
            changed |= Moved(ref stats.LeftTrigger, now.LeftTrigger, was.LeftTrigger, at, alias, "lt");
            changed |= Moved(ref stats.RightTrigger, now.RightTrigger, was.RightTrigger, at, alias, "rt");
            changed |= Moved(ref stats.LeftX, now.LeftX, was.LeftX, at, alias, "lx");
            changed |= Moved(ref stats.LeftY, now.LeftY, was.LeftY, at, alias, "ly");
            changed |= Moved(ref stats.RightX, now.RightX, was.RightX, at, alias, "rx");
            changed |= Moved(ref stats.RightY, now.RightY, was.RightY, at, alias, "ry");
            for (int i = 0; i < RecordedOutputState.RawAxisCount; i++)
                changed |= Moved(ref stats.RawAxes[i], now.RawAxes[i], was.RawAxes[i], at, alias, "ra" + i.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < RecordedOutputState.RawButtonWords; i++)
            {
                if (now.RawButtons[i] == was.RawButtons[i]) continue;
                stats.RawButtons[i] |= now.RawButtons[i] ^ was.RawButtons[i];
                Window(at, alias + " rb", 1, false, 0, 0);
                changed = true;
            }
            for (int i = 0; i < RecordedOutputState.RawHatCount; i++)
                changed |= Moved(ref stats.RawHats[i], now.RawHats[i], was.RawHats[i], at, alias, "rh" + i.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < RecordedOutputState.KeyWords; i++)
            {
                if (now.Keys[i] == was.Keys[i]) continue;
                stats.Keys[i] |= now.Keys[i] ^ was.Keys[i];
                Window(at, alias + " keys", 1, false, 0, 0);
                changed = true;
            }
            if (now.MouseButtons != was.MouseButtons)
            {
                stats.MouseButtonsChanged |= now.MouseButtons ^ was.MouseButtons;
                Window(at, alias + " mouse buttons", 1, false, 0, 0);
                changed = true;
            }
            changed |= Moved(ref stats.MoveX, now.MoveX, was.MoveX, at, alias, "mouse x");
            changed |= Moved(ref stats.MoveY, now.MoveY, was.MoveY, at, alias, "mouse y");
            changed |= Moved(ref stats.Scroll, now.Scroll, was.Scroll, at, alias, "scroll");
            changed |= Moved(ref stats.ScrollH, now.ScrollH, was.ScrollH, at, alias, "scroll h");
            if (reader.HasPerPassCounts)
            {
                // Exact mouse counts are motion on both mouse axes' lane.
                stats.MoveX = stats.MoveY = true;
                Window(at, alias + " mouse counts", 1, false, 0, 0);
                changed = true;
            }
            for (int i = 0; i < RecordedOutputState.MidiCount; i++)
            {
                if (now.MidiCc[i] != was.MidiCc[i]) { stats.MidiCc[i] = true; changed = true; }
                if (now.MidiNotes[i] != was.MidiNotes[i]) { stats.MidiNotes[i] = true; changed = true; }
            }
            if (!Same(in now.Left, in was.Left)) { stats.VrLeft = true; changed = true; }
            if (!Same(in now.Right, in was.Right)) { stats.VrRight = true; changed = true; }
            if (now.TouchDown0 != was.TouchDown0 || now.TouchDown1 != was.TouchDown1 || now.TouchClick != was.TouchClick
                || now.TouchX0 != was.TouchX0 || now.TouchY0 != was.TouchY0 || now.TouchX1 != was.TouchX1 || now.TouchY1 != was.TouchY1)
                changed = true;
            now.CopyInto(was);
            if (!changed || reader.IsFullFrame) return;

            stats.Changes++;
            if (stats.Sent is RecordingCodec.SentState.Absent or RecordingCodec.SentState.Disconnected or RecordingCodec.SentState.Failed)
            {
                stats.ChangesNotSent++;
                if (stats.FirstNotSent < 0)
                {
                    stats.FirstNotSent = at;
                    stats.NotSentState = stats.Sent;
                }
            }
        }

        private static bool Same(in VrHandRaw a, in VrHandRaw b)
            => a.Buttons == b.Buttons && a.Trigger == b.Trigger && a.Grip == b.Grip && a.StickX == b.StickX && a.StickY == b.StickY;

        private bool Moved(ref bool flag, int now, int was, long at, string alias, string name)
        {
            if (now == was) return false;
            flag = true;
            Window(at, alias + " " + name, 1, true, now, now);
            return true;
        }

        private void TakeSent(RecordingReader reader, long at)
        {
            var controller = reader.Controller;
            if (controller == null) return;
            var stats = StatsOf(controller);
            if (stats.Sent >= 0 && stats.SentSince >= 0) stats.SentMicros[stats.Sent] += at - stats.SentSince;
            if (stats.Sent >= 0 && stats.Sent != controller.Sent)
                Note(at, controller.Alias + " send: " + RecordingCodec.SentState.Word(controller.Sent));
            stats.Sent = Math.Clamp(controller.Sent, 0, stats.SentMicros.Length - 1);
            stats.SentSince = at;
        }

        private static bool Four(ReadOnlySpan<char> value, out bool any)
        {
            any = false;
            int numbers = 0;
            while (!value.IsEmpty)
            {
                int comma = value.IndexOf(',');
                var part = comma < 0 ? value : value.Slice(0, comma);
                value = comma < 0 ? default : value.Slice(comma + 1);
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
                if (n != 0) any = true;
                numbers++;
            }
            return numbers == 4;
        }

        private void TakeFeedback(RecordingReader reader, long at)
        {
            var payload = reader.Payload;
            if (reader.Controller != null && RecordingCodec.TryGetValue(payload, "rumble", out var asked) && Four(asked, out bool any))
            {
                var stats = StatsOf(reader.Controller);
                if (any && !stats.Rumbling)
                {
                    stats.RumbleRises++;
                    if (HasRumbleDevice(reader, reader.Controller.Number)) stats.Waiting.Enqueue(at);
                    Note(at, reader.Controller.Alias + " game rumble");
                }
                stats.Rumbling = any;
            }
            else if (reader.Device != null && RecordingCodec.TryGetValue(payload, "wrote", out var wrote) && Four(wrote, out bool nonzero) && nonzero)
            {
                foreach (var pair in _devicesOfController)
                {
                    if (!pair.Value.Contains(reader.Device.Number)) continue;
                    if (_controllers.TryGetValue(pair.Key, out var stats)) stats.Waiting.Clear();
                }
            }
        }

        /// <summary>Whether a controller has a device assigned that is online
        /// and has a motor: only then is a write to expect.</summary>
        private bool HasRumbleDevice(RecordingReader reader, int controller)
        {
            if (!_devicesOfController.TryGetValue(controller, out var devices)) return false;
            foreach (int number in devices)
            {
                var device = reader.DeviceByNumber(number);
                if (device != null && device.Online && device.Fact("rumble") == "1") return true;
            }
            return false;
        }

        private void TakeTiming(RecordingReader reader, long at)
        {
            var payload = reader.Payload;
            if (payload.StartsWith("stall "))
            {
                _stalls.Add((at, payload.ToString()));
                Note(at, payload.ToString());
                return;
            }
            if (RecordingCodec.TryGetValue(payload, "rest", out var rest))
            {
                _resting = !rest.SequenceEqual("none");
                return;
            }
            if (RecordingCodec.TryGetValue(payload, "gap", out var gap) && long.TryParse(gap, NumberStyles.None, CultureInfo.InvariantCulture, out long gapMicros))
            {
                _gaps++;
                if (gapMicros > _worstGap) _worstGap = gapMicros;
                Note(at, "a pass came " + Millis(gapMicros) + " after the one before it");
                return;
            }
            if (RecordingCodec.TryGetValue(payload, "lost", out var lost) && long.TryParse(lost, NumberStyles.None, CultureInfo.InvariantCulture, out long lostCount))
            {
                _dropped += lostCount;
                return;
            }
            if (RecordingCodec.TryGetValue(payload, "passes", out var passes) && long.TryParse(passes, NumberStyles.None, CultureInfo.InvariantCulture, out long passCount))
            {
                _passes += passCount;
                _timingSeconds++;
                double set = 0, rate = 0;
                if (RecordingCodec.TryGetValue(payload, "set", out var setText)) double.TryParse(setText, NumberStyles.Float, CultureInfo.InvariantCulture, out set);
                if (RecordingCodec.TryGetValue(payload, "rate", out var rateText)) double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out rate);
                if (set > 0) _setRate = set;
                if (RecordingCodec.TryGetValue(payload, "longest", out var longest) && long.TryParse(longest, NumberStyles.None, CultureInfo.InvariantCulture, out long longestMicros)
                    && longestMicros > _longestPass) _longestPass = longestMicros;
                if (RecordingCodec.TryGetValue(payload, "capture", out var capture) && double.TryParse(capture, NumberStyles.Float, CultureInfo.InvariantCulture, out double captureMicros))
                    _captureSum += captureMicros;
                if (_resting || rate <= 0) return;
                if (rate < _lowestRate) _lowestRate = rate;
                if (set > 0 && rate < set * SlowShare)
                {
                    string text = rate.ToString("F0", CultureInfo.InvariantCulture) + " passes in a second with " + set.ToString("F0", CultureInfo.InvariantCulture) + " set";
                    _slowSeconds.Add((at, text));
                    Note(at, text);
                }
            }
        }

        private void TakeMarker(RecordingReader reader, long at)
        {
            var payload = reader.Payload;
            if (payload.StartsWith("stop "))
            {
                _ending = payload.Slice(5).ToString();
                return;
            }
            if (!payload.StartsWith("mark")) return;
            if (_markers.Count >= MostMarkers)
            {
                _markersLeftOut++;
                return;
            }
            var marker = new MarkerWindow { At = at, Text = payload.Length > 5 ? payload.Slice(5).ToString() : string.Empty };
            // What the five seconds before it held.
            long slotNow = at / SlotMicros;
            foreach (var slot in _slots)
            {
                if (slot.Index < 0 || slot.Index > slotNow || slotNow - slot.Index >= SlotCount) continue;
                foreach (var pair in slot.Stats)
                {
                    if (!marker.Before.TryGetValue(pair.Key, out var stat)) marker.Before[pair.Key] = stat = new WindowStat();
                    stat.Add(pair.Value.Count, pair.Value.HasRange, pair.Value.Min, pair.Value.Max);
                }
                foreach (var (noted, text) in slot.Notable)
                    if (at - noted <= MarkerWindowMicros) marker.Notable.Add(Offset(noted - at) + " " + text);
            }
            _markers.Add(marker);
        }

        // ─────────────────────────────────────────────
        //  Marker windows
        // ─────────────────────────────────────────────

        private Slot SlotAt(long at)
        {
            long index = at / SlotMicros;
            var slot = _slots[(int)(index % SlotCount)];
            if (slot.Index != index)
            {
                slot.Index = index;
                slot.Stats.Clear();
                slot.Notable.Clear();
            }
            return slot;
        }

        private void Window(long at, string key, int count, bool hasRange, int min, int max)
        {
            var slot = SlotAt(at);
            if (!slot.Stats.TryGetValue(key, out var stat)) slot.Stats[key] = stat = new WindowStat();
            stat.Add(count, hasRange, min, max);
            for (int i = _markers.Count - 1; i >= 0; i--)
            {
                var marker = _markers[i];
                if (at - marker.At > MarkerWindowMicros) break;
                if (!marker.After.TryGetValue(key, out var after)) marker.After[key] = after = new WindowStat();
                after.Add(count, hasRange, min, max);
            }
        }

        private void Note(long at, string text)
        {
            var slot = SlotAt(at);
            if (slot.Notable.Count < 20) slot.Notable.Add((at, text));
            for (int i = _markers.Count - 1; i >= 0; i--)
            {
                var marker = _markers[i];
                if (at - marker.At > MarkerWindowMicros) break;
                if (marker.Notable.Count < 40) marker.Notable.Add(Offset(at - marker.At) + " " + text);
            }
        }

        // ─────────────────────────────────────────────
        //  The end
        // ─────────────────────────────────────────────

        private void Finish(RecordingReader reader)
        {
            long end = _last;
            foreach (var stats in _devices.Values)
            {
                CloseOnline(stats, end);
                foreach (var axis in stats.Axes) if (axis != null) Hold(axis, end);
                foreach (var axis in stats.Sliders) if (axis != null) Hold(axis, end);
            }
            foreach (var stats in _controllers.Values)
            {
                if (stats.Sent >= 0 && stats.SentSince >= 0) stats.SentMicros[stats.Sent] += end - stats.SentSince;
                // Rumble asked for in the last second has had no time to be
                // answered, so it is not counted.
                stats.Waiting.Clear();
            }

            var device = new List<RecordingFinding>();
            foreach (var stats in Sorted(_devices))
            {
                if (stats.OnlineMicros < ShortestJudgedMicros) continue;
                string alias = stats.Device.Alias;
                for (int i = 0; i < stats.Axes.Length; i++) NeverRested(device, stats.Axes[i], alias, ControlName(stats.Device, RecordingField.Axis, i));
                for (int i = 0; i < stats.Sliders.Length; i++) NeverRested(device, stats.Sliders[i], alias, ControlName(stats.Device, RecordingField.Slider, i));
                for (int i = 0; i < stats.ButtonDownMicros.Length; i++)
                    if (stats.ButtonDownMicros[i] >= stats.OnlineMicros && stats.ButtonPresses[i] <= 1 && stats.ButtonDownMicros[i] > 0)
                        device.Add(new RecordingFinding(FindingPlace.Device, 0,
                            alias + " " + ControlName(stats.Device, RecordingField.Button, i) + " was down the whole time it was online. Look at the device or its driver."));
                for (int i = 0; i < stats.HatOffMicros.Length; i++)
                    if (stats.HatOffMicros[i] >= stats.OnlineMicros && stats.HatOffMicros[i] > 0)
                        device.Add(new RecordingFinding(FindingPlace.Device, 0,
                            alias + " " + ControlName(stats.Device, RecordingField.Hat, i) + " was off center the whole time it was online. Look at the device or its driver."));
            }
            // Controls first, then the offline lines the events already added.
            _findings.InsertRange(0, device);

            CheckRows(reader);

            foreach (var stats in Sorted(_controllers))
            {
                if (stats.FirstNotSent < 0) continue;
                string why = stats.NotSentState switch
                {
                    RecordingCodec.SentState.Absent => "it had no virtual controller",
                    RecordingCodec.SentState.Disconnected => "its virtual controller was not connected",
                    _ => "the send failed",
                };
                _findings.Add(new RecordingFinding(FindingPlace.VirtualController, stats.FirstNotSent,
                    stats.Controller.Alias + " changed " + Count(stats.ChangesNotSent, "time") + " while " + why + ", first at "
                    + Time(stats.FirstNotSent) + ". Look at the virtual controller."));
            }

            foreach (var stats in Sorted(_controllers))
            {
                if (stats.RumbleUnanswered == 0) continue;
                _findings.Add(new RecordingFinding(FindingPlace.Feedback, stats.FirstUnanswered,
                    "A game asked " + stats.Controller.Alias + " to rumble " + Count(stats.RumbleUnanswered, "time")
                    + " and none of its devices was written within a second, first at " + Time(stats.FirstUnanswered) + ". Look at feedback."));
            }

            foreach (var marker in _markers)
            {
                foreach (var (at, text) in _stalls)
                    if (Math.Abs(at - marker.At) <= MarkerWindowMicros)
                        _findings.Add(new RecordingFinding(FindingPlace.Timing, at,
                            "The engine stalled at " + Time(at) + ", " + Offset(at - marker.At) + " from the marker at " + Time(marker.At) + " (" + text + "). Look at timing."));
                foreach (var (at, text) in _slowSeconds)
                    if (Math.Abs(at - marker.At) <= MarkerWindowMicros)
                        _findings.Add(new RecordingFinding(FindingPlace.Timing, at,
                            "The engine ran slow at " + Time(at) + ", " + Offset(at - marker.At) + " from the marker at " + Time(marker.At) + " (" + text + "). Look at timing."));
            }
        }

        /// <summary>A control's name in the summary: the name the file's
        /// events give it (#600), from how the device's line says its
        /// controls are named.</summary>
        private static string ControlName(RecordedDevice device, RecordingField field, int index)
            => RecordingControlNames.Input(KindOf(device), field, index);

        /// <summary>How a device's controls are named, from its line's
        /// controls fact. A format 1 file has none, and names them by
        /// number.</summary>
        public static RecordingControlKind KindOf(RecordedDevice device) => device?.Fact("controls") switch
        {
            "gamepad" => RecordingControlKind.Gamepad,
            "mouse" => RecordingControlKind.Mouse,
            "keyboard" => RecordingControlKind.Keyboard,
            _ => RecordingControlKind.Generic,
        };

        private static void NeverRested(List<RecordingFinding> findings, AxisStat axis, string alias, string name)
        {
            if (axis == null || !axis.Seen || axis.Rested) return;
            findings.Add(new RecordingFinding(FindingPlace.Device, 0,
                alias + " " + name + " never came to rest: it stayed between "
                + axis.Min.ToString(CultureInfo.InvariantCulture) + " and " + axis.Max.ToString(CultureInfo.InvariantCulture)
                + " the whole time it was online. Look at the device or its driver."));
        }

        private static List<T> Sorted<T>(Dictionary<int, T> map)
        {
            var keys = new List<int>(map.Keys);
            keys.Sort();
            var list = new List<T>(keys.Count);
            foreach (int key in keys) list.Add(map[key]);
            return list;
        }

        // ─────────────────────────────────────────────
        //  Mapping rows
        // ─────────────────────────────────────────────

        private void CheckRows(RecordingReader reader)
        {
            foreach (var row in _rows)
            {
                int controller = Number(row, "controller", 'C');
                row.TryGetValue("target", out string target);
                row.TryGetValue("sources", out string sources);
                row.TryGetValue("layer", out string layer);
                row.TryGetValue("combine", out string combine);
                // Only a row that always applies and has one plain source is
                // judged: a layer's row waits for its layer, and a row with
                // several sources or a formula can rest while one moves.
                bool plain = controller > 0 && sources == "1" && string.IsNullOrEmpty(combine)
                    && (string.IsNullOrEmpty(layer) || layer == "Base")
                    && row.TryGetValue("kind1", out string kind) && kind == "Direct";
                int device = plain ? Number(row, "src1", 'D') : 0;
                row.TryGetValue("in1", out string descriptor);
                if (device <= 0 || !_devices.TryGetValue(device, out var source)
                    || !_controllers.TryGetValue(controller, out var stats) || !stats.HasBefore
                    || !SourceMoved(source, descriptor, out string moved)
                    || !TryTargetChanged(stats, target, out bool targetChanged))
                {
                    _rowsSkipped++;
                    continue;
                }
                _rowsChecked++;
                if (targetChanged) continue;
                // A slot that sends nothing explains itself in its own rule.
                if (stats.SentMicros[RecordingCodec.SentState.Ok] == 0) continue;
                row.TryGetValue("label", out string label);
                string name = string.IsNullOrEmpty(label) ? target : label + " (" + target + ")";
                _findings.Add(new RecordingFinding(FindingPlace.Settings, 0,
                    source.Device.Alias + " " + moved + " and " + stats.Controller.Alias + " " + name
                    + " never changed. Look at the settings: the mapping row " + name + " of " + stats.Controller.Alias + "."));
            }
        }

        /// <summary>Whether the input a plain descriptor names moved enough
        /// that its row had to answer: a button pressed, an axis or slider
        /// over half its travel, a hat pushed the way the row names.</summary>
        private static bool SourceMoved(DeviceStats device, string descriptor, out string moved)
        {
            moved = null;
            if (string.IsNullOrEmpty(descriptor)) return false;
            string[] words = descriptor.Split(' ');
            if (words.Length < 2 || !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return false;
            switch (words[0])
            {
                case "Button" when words.Length == 2 && index < device.ButtonPresses.Length:
                    if (device.ButtonPresses[index] == 0) return false;
                    moved = descriptor + " was pressed " + Count(device.ButtonPresses[index], "time");
                    return true;
                case "Axis" when words.Length == 2 && index < device.Axes.Length:
                    return AxisMoved(device.Axes[index], descriptor, out moved);
                case "Slider" when words.Length == 2 && index < device.Sliders.Length:
                    return AxisMoved(device.Sliders[index], descriptor, out moved);
                case "POV" when words.Length == 3 && index < device.HatDirections.Length:
                    int bit = words[2] switch { "Up" => 1, "Right" => 2, "Down" => 4, "Left" => 8, _ => 0 };
                    if (bit == 0 || (device.HatDirections[index] & bit) == 0) return false;
                    moved = descriptor + " was pushed";
                    return true;
            }
            return false;
        }

        private static bool AxisMoved(AxisStat axis, string descriptor, out string moved)
        {
            moved = null;
            if (axis == null || !axis.Seen || (long)axis.Max - axis.Min < MovedSpan) return false;
            moved = descriptor + " moved from " + axis.Min.ToString(CultureInfo.InvariantCulture) + " to " + axis.Max.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Whether the part of a controller's frame a target key
        /// names changed at least once. False for a key this summary does
        /// not know, whose row is then not judged.</summary>
        private static bool TryTargetChanged(ControllerStats c, string target, out bool changed)
        {
            changed = false;
            if (string.IsNullOrEmpty(target)) return false;
            if (target.EndsWith("Neg", StringComparison.Ordinal)) target = target.Substring(0, target.Length - 3);
            switch (target)
            {
                case "ButtonA": changed = (c.ButtonsChanged & Gamepad.A) != 0; return true;
                case "ButtonB": changed = (c.ButtonsChanged & Gamepad.B) != 0; return true;
                case "ButtonX": changed = (c.ButtonsChanged & Gamepad.X) != 0; return true;
                case "ButtonY": changed = (c.ButtonsChanged & Gamepad.Y) != 0; return true;
                case "LeftShoulder": changed = (c.ButtonsChanged & Gamepad.LEFT_SHOULDER) != 0; return true;
                case "RightShoulder": changed = (c.ButtonsChanged & Gamepad.RIGHT_SHOULDER) != 0; return true;
                case "ButtonBack": changed = (c.ButtonsChanged & Gamepad.BACK) != 0; return true;
                case "ButtonStart": changed = (c.ButtonsChanged & Gamepad.START) != 0; return true;
                case "LeftThumbButton": changed = (c.ButtonsChanged & Gamepad.LEFT_THUMB) != 0; return true;
                case "RightThumbButton": changed = (c.ButtonsChanged & Gamepad.RIGHT_THUMB) != 0; return true;
                case "ButtonGuide": changed = (c.ButtonsChanged & Gamepad.GUIDE) != 0; return true;
                case "DPadUp": changed = (c.ButtonsChanged & Gamepad.DPAD_UP) != 0; return true;
                case "DPadDown": changed = (c.ButtonsChanged & Gamepad.DPAD_DOWN) != 0; return true;
                case "DPadLeft": changed = (c.ButtonsChanged & Gamepad.DPAD_LEFT) != 0; return true;
                case "DPadRight": changed = (c.ButtonsChanged & Gamepad.DPAD_RIGHT) != 0; return true;
                case "ButtonShare": changed = (c.ExtraChanged & 1) != 0; return true;
                case "ButtonMute": changed = (c.ExtraChanged & 2) != 0; return true;
                case "LeftPaddle": changed = (c.ExtraChanged & 4) != 0; return true;
                case "RightPaddle": changed = (c.ExtraChanged & 8) != 0; return true;
                case "LeftFunction": changed = (c.ExtraChanged & 16) != 0; return true;
                case "RightFunction": changed = (c.ExtraChanged & 32) != 0; return true;
                case "LeftThumbAxisX": changed = c.LeftX; return true;
                case "LeftThumbAxisY": changed = c.LeftY; return true;
                case "RightThumbAxisX": changed = c.RightX; return true;
                case "RightThumbAxisY": changed = c.RightY; return true;
                case "LeftTrigger": changed = c.LeftTrigger; return true;
                case "RightTrigger": changed = c.RightTrigger; return true;
                case "KbmMouseX": changed = c.MoveX; return true;
                case "KbmMouseY": changed = c.MoveY; return true;
                case "KbmScroll": changed = c.Scroll; return true;
                case "KbmScrollH": changed = c.ScrollH; return true;
            }
            if (Indexed(target, "RawBtn", NumberStyles.None, out int button) && button < RecordedOutputState.RawButtonWords * 32)
            {
                changed = (c.RawButtons[button / 32] & (1u << (button % 32))) != 0;
                return true;
            }
            if (Indexed(target, "RawAxis", NumberStyles.None, out int axis) && axis < c.RawAxes.Length)
            {
                changed = c.RawAxes[axis];
                return true;
            }
            if (target.StartsWith("RawPov", StringComparison.Ordinal) && target.Length > 6 && char.IsAsciiDigit(target[6]) && target[6] - '0' < c.RawHats.Length)
            {
                changed = c.RawHats[target[6] - '0'];
                return true;
            }
            if (Indexed(target, "KbmKey", NumberStyles.AllowHexSpecifier, out int key) && key < 256)
            {
                changed = (c.Keys[key / 64] & (1UL << (key % 64))) != 0;
                return true;
            }
            if (Indexed(target, "KbmMBtn", NumberStyles.None, out int mouse) && mouse < 8)
            {
                changed = (c.MouseButtonsChanged & (1 << mouse)) != 0;
                return true;
            }
            if (Indexed(target, "MidiNote", NumberStyles.None, out int note) && note < c.MidiNotes.Length)
            {
                changed = c.MidiNotes[note];
                return true;
            }
            if (Indexed(target, "MidiCC", NumberStyles.None, out int cc) && cc < c.MidiCc.Length)
            {
                changed = c.MidiCc[cc];
                return true;
            }
            if (target.StartsWith("VrL", StringComparison.Ordinal)) { changed = c.VrLeft; return true; }
            if (target.StartsWith("VrR", StringComparison.Ordinal)) { changed = c.VrRight; return true; }
            return false;
        }

        private static bool Indexed(string target, string prefix, NumberStyles style, out int index)
        {
            index = 0;
            return target.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(target.AsSpan(prefix.Length), style, CultureInfo.InvariantCulture, out index) && index >= 0;
        }

        // ─────────────────────────────────────────────
        //  The summary's text
        // ─────────────────────────────────────────────

        private static string Time(long micros)
            => (micros / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture) + " s";

        private static string Millis(long micros)
            => (micros / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " ms";

        private static string Offset(long micros)
            => (micros >= 0 ? "+" : "-") + (Math.Abs(micros) / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture) + " s";

        private static string Count(long n, string noun, string plural = null)
            => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? noun : plural ?? noun + "s");

        private static string PlaceName(FindingPlace place) => place switch
        {
            FindingPlace.Device => "the device or its driver",
            FindingPlace.Settings => "the settings",
            FindingPlace.VirtualController => "the virtual controller",
            FindingPlace.Feedback => "feedback",
            _ => "timing",
        };

        /// <summary>The summary as the lines of the file's summary part.</summary>
        public List<string> Lines()
        {
            var lines = new List<string>();
            long duration = DurationMicros;
            lines.Add("Length: " + Time(duration) + ", " + Count(_lines, "event") + ", " + Count(_inputLines, "input line") + ", "
                + Count(_outputLines, "output line") + "." + (_ending.Length > 0 ? " Ended: " + _ending + "." : string.Empty));

            if (_findings.Count == 0)
            {
                lines.Add("First place to look: nothing stood out in what was recorded.");
            }
            else
            {
                lines.Add("First place to look: " + PlaceName(_findings[0].Place) + ".");
                lines.Add("What PadForge found, in the order an input travels:");
                int shown = 0;
                foreach (var finding in _findings)
                {
                    if (++shown > 40)
                    {
                        lines.Add("  and " + Count(_findings.Count - 40, "more finding") + ".");
                        break;
                    }
                    lines.Add("  " + shown.ToString(CultureInfo.InvariantCulture) + ". " + finding.Text);
                }
            }

            foreach (var marker in _markers)
            {
                lines.Add("Marker at " + Time(marker.At) + (marker.Text.Length > 0 ? ": " + marker.Text : string.Empty));
                lines.Add("  Before, 5 s: " + WindowText(marker.Before));
                lines.Add("  After, 5 s: " + WindowText(marker.After));
                foreach (string note in marker.Notable) lines.Add("  " + note);
            }
            if (_markersLeftOut > 0)
                lines.Add(Count(_markersLeftOut, "more marker") + " past the first " + MostMarkers.ToString(CultureInfo.InvariantCulture)
                    + (_markersLeftOut == 1 ? " was left out." : " were left out."));

            foreach (var stats in Sorted(_devices))
            {
                var sb = new StringBuilder(160);
                sb.Append(stats.Device.Alias);
                if (stats.Device.ProductName.Length > 0) sb.Append(' ').Append(stats.Device.ProductName);
                sb.Append(": online ").Append(Time(stats.OnlineMicros)).Append(", ").Append(Count(stats.Changes, "report")).Append(" with a change");
                if (stats.Changes > 1)
                {
                    long median = MedianInterval(stats);
                    if (median > 0)
                        sb.Append(", about ").Append((1_000_000.0 / median).ToString("F0", CultureInfo.InvariantCulture)).Append(" a second while moving");
                    sb.Append(", most in one second ").Append(stats.BusiestCount.ToString("N0", CultureInfo.InvariantCulture));
                    sb.Append(", longest gap while moving ").Append(Millis(stats.LongestGap));
                }
                if (stats.Offlines > 0) sb.Append(", went offline ").Append(Count(stats.Offlines, "time"));
                sb.Append('.');
                lines.Add(sb.ToString());
                string rest = RestText(stats);
                if (rest.Length > 0) lines.Add("  Rest: " + rest);
            }

            foreach (var stats in Sorted(_controllers))
            {
                var sb = new StringBuilder(120);
                sb.Append(stats.Controller.Alias).Append(": ").Append(Count(stats.Changes, "change"));
                for (int state = 0; state < stats.SentMicros.Length; state++)
                    if (stats.SentMicros[state] > 0)
                        sb.Append(", ").Append(RecordingCodec.SentState.Word(state)).Append(' ').Append(Time(stats.SentMicros[state]));
                if (stats.RumbleRises > 0) sb.Append(", game rumble ").Append(Count(stats.RumbleRises, "time"));
                sb.Append('.');
                lines.Add(sb.ToString());
            }

            if (_timingSeconds > 0)
            {
                var sb = new StringBuilder(160);
                sb.Append("Timing: ").Append(_setRate.ToString("F0", CultureInfo.InvariantCulture)).Append(" passes a second set");
                if (_lowestRate < double.MaxValue)
                    sb.Append(", slowest second ").Append(_lowestRate.ToString("F0", CultureInfo.InvariantCulture));
                sb.Append(", longest pass ").Append(Millis(_longestPass));
                sb.Append(", ").Append(Count(_stalls.Count, "stall"));
                if (_gaps > 0) sb.Append(", ").Append(Count(_gaps, "late pass", "late passes")).Append(", the latest ").Append(Millis(_worstGap));
                sb.Append(", recording cost ").Append((_captureSum / _timingSeconds).ToString("F1", CultureInfo.InvariantCulture)).Append(" us a pass.");
                lines.Add(sb.ToString());
            }

            lines.Add("Also: " + Count(_errorCount, "error") + ", " + Count(_diagLines, "diagnostics line") + ", the program in front changed "
                + Count(_windowChanges, "time") + ", " + Count(_rowsChecked, "mapping row") + " checked and " + _rowsSkipped.ToString("N0", CultureInfo.InvariantCulture)
                + " not." + (_dropped > 0 ? " " + Count(_dropped, "change") + " did not fit and were left out." : string.Empty));
            foreach (var (at, text) in _errors) lines.Add("  error at " + Time(at) + ": " + text);
            return lines;
        }

        /// <summary>The middle of the times between two changes of a run,
        /// from the buckets: the upper edge of the bucket the middle one
        /// falls in.</summary>
        private static long MedianInterval(DeviceStats stats)
        {
            long total = 0;
            foreach (int n in stats.IntervalBuckets) total += n;
            if (total == 0) return 0;
            long seen = 0;
            for (int i = 0; i < stats.IntervalBuckets.Length; i++)
            {
                seen += stats.IntervalBuckets[i];
                if (seen * 2 >= total) return i < IntervalEdges.Length ? IntervalEdges[i] : RunBreakMicros;
            }
            return 0;
        }

        private static string RestText(DeviceStats stats)
        {
            var sb = new StringBuilder(120);
            void Add(AxisStat[] axes, RecordingField field)
            {
                for (int i = 0; i < axes.Length; i++)
                {
                    var axis = axes[i];
                    if (axis == null || !axis.Seen) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(ControlName(stats.Device, field, i)).Append(' ')
                      .Append(axis.RestValue.ToString(CultureInfo.InvariantCulture));
                    if (axis.Min != axis.Max)
                        sb.Append(" (").Append(axis.Min.ToString(CultureInfo.InvariantCulture)).Append(" to ")
                          .Append(axis.Max.ToString(CultureInfo.InvariantCulture)).Append(')');
                }
            }
            Add(stats.Axes, RecordingField.Axis);
            Add(stats.Sliders, RecordingField.Slider);
            return sb.ToString();
        }

        private static string WindowText(SortedDictionary<string, WindowStat> window)
        {
            if (window.Count == 0) return "nothing changed.";
            var sb = new StringBuilder(200);
            int shown = 0;
            foreach (var pair in window)
            {
                if (++shown > 24)
                {
                    sb.Append(", and ").Append((window.Count - 24).ToString(CultureInfo.InvariantCulture)).Append(" more");
                    break;
                }
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(pair.Key);
                if (pair.Value.HasRange && pair.Value.Min != pair.Value.Max)
                    sb.Append(' ').Append(pair.Value.Min.ToString(CultureInfo.InvariantCulture)).Append("..")
                      .Append(pair.Value.Max.ToString(CultureInfo.InvariantCulture));
                else if (pair.Value.HasRange)
                    sb.Append(' ').Append(pair.Value.Min.ToString(CultureInfo.InvariantCulture));
                sb.Append(" x").Append(pair.Value.Count.ToString(CultureInfo.InvariantCulture));
            }
            return sb.Append('.').ToString();
        }
    }
}
