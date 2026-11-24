using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Recording;

namespace PadForge.Services.Plugins
{
    /// <summary>One device of a recording as Replay makes it again: its
    /// number in the file, its name and the shape its line in the devices
    /// part gives.</summary>
    internal sealed record ReplayDevice(int Number, string Name, string Key, ProgramDeviceShape Shape);

    /// <summary>
    /// A problem recording opened for Replay (#597): its devices and how
    /// long its events run, read once through <see cref="RecordingReader"/>
    /// (a .zip or a .log). A recording made on another PC opens the same,
    /// since its devices part carries each device's shape.
    /// </summary>
    internal sealed class ReplayFile
    {
        // A recording is a file from anyone: these bound what reading one
        // costs. The recorder stops itself at a 20 MB zip with about 120 MB
        // of text inside it, so a file it made is always within them.
        internal const long MostFileBytes = 64L * 1024 * 1024;
        internal const long MostTextChars = 512L * 1024 * 1024;
        internal const int LongestLine = 64 * 1024;
        internal const int MostDevices = 16;

        /// <summary>Opens a recording's text, bounded: a file or a zip's one
        /// entry no larger than the limits, read a line at a time with no
        /// line longer than <see cref="LongestLine"/>. Past a limit the read
        /// throws <see cref="InvalidDataException"/>.</summary>
        internal static RecordingReader OpenBounded(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException(path);
            if (info.Length > MostFileBytes) throw new InvalidDataException("too-large");
            using (var probe = File.OpenRead(path))
            {
                var head = new byte[2];
                if (probe.Read(head, 0, 2) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
                {
                    probe.Position = 0;
                    using var zip = new System.IO.Compression.ZipArchive(probe, System.IO.Compression.ZipArchiveMode.Read);
                    if (zip.Entries.Count != 1 || zip.Entries[0].Length > MostTextChars) throw new InvalidDataException("bad-zip");
                }
            }
            return new RecordingReader(new BoundedLines(RecordingReader.OpenText(path), LongestLine, MostTextChars));
        }

        /// <summary>A reader that hands out lines no longer than its limit
        /// and no more text in all than its other limit, reading a character
        /// at a time so an endless line is never held whole.</summary>
        private sealed class BoundedLines : TextReader
        {
            private readonly TextReader _inner;
            private readonly int _longest;
            private readonly long _most;
            private long _read;
            private readonly System.Text.StringBuilder _line = new();

            public BoundedLines(TextReader inner, int longest, long most)
            {
                _inner = inner;
                _longest = longest;
                _most = most;
            }

            public override int Peek() => _inner.Peek();

            public override int Read()
            {
                int c = _inner.Read();
                if (c >= 0 && ++_read > _most) throw new InvalidDataException("too-large");
                return c;
            }

            public override string ReadLine()
            {
                _line.Clear();
                while (true)
                {
                    int c = Read();
                    if (c < 0) return _line.Length == 0 ? null : _line.ToString();
                    if (c == '\n') return _line.ToString();
                    if (c == '\r')
                    {
                        if (_inner.Peek() == '\n') Read();
                        return _line.ToString();
                    }
                    if (_line.Length >= _longest) throw new InvalidDataException("line-too-long");
                    _line.Append((char)c);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        public string Path { get; private init; }
        public string FileName => System.IO.Path.GetFileName(Path);
        public IReadOnlyList<ReplayDevice> Devices { get; private init; }

        /// <summary>The time of the first event, which playback starts
        /// from.</summary>
        public long FirstMicros { get; private init; }

        /// <summary>From the first event to the last.</summary>
        public long LengthMicros { get; private init; }

        /// <summary>How many input lines the devices have.</summary>
        public long Frames { get; private init; }

        /// <summary>Reads a recording. Null with the reason when the file is
        /// no recording or holds no device's input.</summary>
        public static ReplayFile Read(string path, out string error)
        {
            error = null;
            try
            {
                using var reader = OpenBounded(path);
                if (!reader.IsRecordingFile)
                {
                    error = "not-a-recording";
                    return null;
                }
                long first = -1, last = 0, frames = 0;
                var withInput = new HashSet<int>();
                while (reader.Read())
                {
                    if (first < 0) first = reader.Microseconds;
                    if (reader.Microseconds > last) last = reader.Microseconds;
                    if (reader.Devices.Count > MostDevices)
                    {
                        error = "too-many-devices";
                        return null;
                    }
                    if (reader.Layer == RecordingCodec.LayerInput && reader.Device != null && !reader.Payload.StartsWith("device "))
                    {
                        frames++;
                        withInput.Add(reader.Device.Number);
                    }
                }
                var devices = new List<ReplayDevice>();
                foreach (var d in reader.Devices)
                    if (withInput.Contains(d.Number)) devices.Add(DeviceOf(d));
                devices.Sort((a, b) => a.Number.CompareTo(b.Number));
                if (devices.Count == 0)
                {
                    error = "no-devices";
                    return null;
                }
                if (first < 0) first = 0;
                return new ReplayFile
                {
                    Path = System.IO.Path.GetFullPath(path),
                    Devices = devices,
                    FirstMicros = first,
                    LengthMicros = Math.Max(0, last - first),
                    Frames = frames,
                };
            }
            catch (InvalidDataException)
            {
                error = "malformed";
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A file from anyone: whatever its bytes make the reader
                // throw, it is refused, never PadForge's problem.
                error = "unreadable";
                return null;
            }
        }

        /// <summary>The shape a device's facts give: its type, its counts of
        /// axes, sliders, buttons and hats, its sensors, touchpads and
        /// motors. Its state is whatever the file's frames say, so the
        /// shape is for the device list and the mapping pickers.</summary>
        internal static ReplayDevice DeviceOf(RecordedDevice d)
        {
            int type = TypeOf(d.Type);
            int axes = Math.Clamp(Math.Max(d.Count("axes"), d.Count("rawaxes")), 0, CustomInputState.MaxAxis);
            int sliders = Math.Clamp(d.Count("sliders"), 0, CustomInputState.MaxSliders);
            int buttons = Math.Clamp(Math.Max(d.Count("buttons"), d.Count("rawbuttons")), 0, CustomInputState.MaxButtons);
            int hats = Math.Clamp(d.Count("hats"), 0, CustomInputState.MaxPovs);
            bool gamepad = type == InputDeviceType.Gamepad;

            var axisList = new List<ProgramAxis>();
            for (int i = 0; i < axes; i++)
                axisList.Add(new ProgramAxis(i, "Axis " + i, gamepad && i is TestControllerPattern.LeftTrigger or TestControllerPattern.RightTrigger ? 0 : TestControllerPattern.Center));
            for (int i = 0; i < sliders; i++)
                axisList.Add(new ProgramAxis(ProgramAxis.FirstSlider + i, "Slider " + i, 0));
            var buttonList = new List<(int, string)>();
            for (int i = 0; i < buttons; i++) buttonList.Add((i, "Button " + i));
            var hatList = new List<string>();
            for (int i = 0; i < hats; i++) hatList.Add("Hat " + i);

            var fingers = new List<int>();
            int pads = Math.Clamp(d.Count("touchpads"), 0, 4);
            string[] counts = d.Fact("fingers").Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (int p = 0; p < pads; p++)
                fingers.Add(p < counts.Length && int.TryParse(counts[p], NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is > 0 and <= 10 ? f : 2);

            bool trig = d.Fact("triggers") == "1";
            string name = PadForge.Services.Automation.ProgramDeviceDeclaration.Clean(d.ProductName, PadForge.Services.Automation.ProgramDeviceDeclaration.LongestName);
            if (name.Length == 0) name = d.Alias;
            string key = d.Alias + "-" + d.VendorId.ToString("x4", CultureInfo.InvariantCulture) + d.ProductId.ToString("x4", CultureInfo.InvariantCulture);
            return new ReplayDevice(d.Number, name, key, new ProgramDeviceShape
            {
                Buttons = buttonList,
                Axes = axisList,
                Hats = hatList,
                Gyro = d.Fact("gyro") == "1",
                Accel = d.Fact("accel") == "1",
                TouchpadFingers = fingers,
                Battery = true,
                Rumble = d.Fact("rumble") == "1" || trig,
                TriggerRumble = trig,
                DeviceType = type,
            });
        }

        private static readonly Dictionary<string, int> TypeNumbers = BuildTypeNumbers();

        private static Dictionary<string, int> BuildTypeNumbers()
        {
            var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var field in typeof(InputDeviceType).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.IsLiteral && field.GetRawConstantValue() is int value) numbers[field.Name] = value;
            return numbers;
        }

        /// <summary>The type a recording names, by the name the recorder
        /// wrote (ProblemRecorderHost.TypeWord), or its number. A type this
        /// PadForge does not know is a program's device.</summary>
        internal static int TypeOf(string word)
        {
            if (!string.IsNullOrEmpty(word))
            {
                if (TypeNumbers.TryGetValue(word, out int type)) return type;
                if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && TypeNumbers.ContainsValue(number))
                    return number;
            }
            return InputDeviceType.Program;
        }
    }

    /// <summary>
    /// Plays a recording back as the devices that made it (#597): each
    /// input line of the file becomes its device's next state at the time
    /// it had, from the first event on. Lines whose time has passed are
    /// given at once, so a slow moment catches up instead of drifting.
    /// A device the file takes offline rests until its next line. With
    /// Repeat on, the file starts again at its end.
    /// </summary>
    internal sealed class ReplayPlayer : IDisposable
    {
        private readonly ReplayFile _file;
        private readonly Dictionary<int, ProgramDevice> _devices;
        private Thread _thread;
        private volatile bool _stop;
        private long _position;
        private volatile bool _playing;

        /// <param name="file">The recording.</param>
        /// <param name="devices">The device that plays each of the file's
        /// devices, by its number.</param>
        public ReplayPlayer(ReplayFile file, Dictionary<int, ProgramDevice> devices)
        {
            _file = file;
            _devices = devices;
        }

        public bool Repeat { get; set; }

        public bool Playing => _playing;

        /// <summary>How far into the file playback is.</summary>
        public long PositionMicros => Interlocked.Read(ref _position);

        /// <summary>Raised on the player's thread when playback stops by
        /// itself at the file's end.</summary>
        public event Action Ended;

        /// <summary>For a test: each line as it goes out, with the device's
        /// number, its time in the file and the time it left, in
        /// microseconds from the start of the pass.</summary>
        internal event Action<int, long, long> Sent;

        public void Play()
        {
            if (_thread != null) return;
            _stop = false;
            _playing = true;
            Interlocked.Exchange(ref _position, 0);
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge.Replay" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            var thread = Interlocked.Exchange(ref _thread, null);
            if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
            RestAll();
            _playing = false;
            Interlocked.Exchange(ref _position, 0);
        }

        private void Run()
        {
            bool ended = false;
            try
            {
                using var clock = new FeederClock();
                do
                {
                    if (!PlayOnce(clock)) break;
                    if (!Repeat || _stop) { ended = !_stop; break; }
                    RestAll();
                }
                while (!_stop);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The file went away, changed under playback or broke a
                // limit. It stops as at its end.
                ended = true;
            }
            RestAll();
            _playing = false;
            if (ended)
            {
                Interlocked.Exchange(ref _thread, null);
                Ended?.Invoke();
            }
        }

        /// <summary>One pass through the file. False when stopped.</summary>
        private bool PlayOnce(FeederClock clock)
        {
            using var reader = ReplayFile.OpenBounded(_file.Path);
            var watch = Stopwatch.StartNew();
            long origin = _file.FirstMicros;
            // A long wait is cut into slices, so Stop is heard soon.
            long slice = Stopwatch.Frequency / 20;
            while (!_stop && reader.Read())
            {
                long at = reader.Microseconds - origin;
                if (reader.Layer != RecordingCodec.LayerInput || reader.Device == null) continue;
                if (reader.Payload.StartsWith("device ")) continue;
                if (!_devices.TryGetValue(reader.Device.Number, out var device)) continue;

                long due = at * Stopwatch.Frequency / 1_000_000;
                while (!_stop)
                {
                    long left = due - watch.ElapsedTicks;
                    if (left <= 0) break;
                    clock.Sleep(Math.Min(left, slice));
                }
                if (_stop) return false;
                if (reader.Device.Online) device.Publish(reader.Device.State);
                else device.Rest();
                Interlocked.Exchange(ref _position, at);
                Sent?.Invoke(reader.Device.Number, at, watch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
            }
            if (!_stop) Interlocked.Exchange(ref _position, _file.LengthMicros);
            return !_stop;
        }

        private void RestAll()
        {
            foreach (var device in _devices.Values)
            {
                try { device.Rest(); } catch { }
            }
        }

        public void Dispose() => Stop();
    }
}
