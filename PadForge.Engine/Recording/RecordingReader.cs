using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine.Recording
{
    /// <summary>A device of a problem recording: what its line in the
    /// devices part says of it, and its state at the line the reader is
    /// on.</summary>
    public sealed class RecordedDevice
    {
        /// <summary>1 for D1.</summary>
        public int Number { get; internal set; }
        public string Alias => "D" + Number.ToString(CultureInfo.InvariantCulture);

        /// <summary>The device's facts as written: name, vid, pid, bus, type,
        /// guid, the counts, the sensors, the mapping in force.</summary>
        public Dictionary<string, string> Facts { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>The state after the line the reader is on, exact to the
        /// integer. It holds nothing until the device's first full frame.</summary>
        public CustomInputState State { get; } = Fresh();

        /// <summary>True once a full frame of this device was read, from
        /// which the state is whole.</summary>
        public bool HasFullFrame { get; internal set; }

        public bool Online { get; internal set; }

        /// <summary>Why it last went offline
        /// (<see cref="RecordingCodec.OfflineReason"/>).</summary>
        public int OfflineReason { get; internal set; }

        private static CustomInputState Fresh()
        {
            var state = new CustomInputState();
            RecordingCodec.Reset(state);
            return state;
        }

        public string Fact(string key) => Facts.TryGetValue(key, out string value) ? value : string.Empty;

        public int Count(string key)
            => int.TryParse(Fact(key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : 0;

        public string ProductName => Fact("name");
        public ushort VendorId => Hex(Fact("vid"));
        public ushort ProductId => Hex(Fact("pid"));
        public string SdlGuid => Fact("guid");
        public string Type => Fact("type");
        public string Mapping => Fact("mapping");
        public bool OpenedAsGamepad => Fact("gamepad") == "1";

        private static ushort Hex(string text)
            => ushort.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort n) ? n : (ushort)0;
    }

    /// <summary>A virtual controller of a problem recording, and its frame
    /// at the line the reader is on.</summary>
    public sealed class RecordedController
    {
        /// <summary>1 for C1.</summary>
        public int Number { get; internal set; }
        public string Alias => "C" + Number.ToString(CultureInfo.InvariantCulture);
        public Dictionary<string, string> Facts { get; internal set; } = new(StringComparer.Ordinal);
        public RecordedOutputState Output { get; } = new();
        public bool HasFullFrame { get; internal set; }

        /// <summary>What became of its frames
        /// (<see cref="RecordingCodec.SentState"/>).</summary>
        public int Sent { get; internal set; }

        public string Fact(string key) => Facts.TryGetValue(key, out string value) ? value : string.Empty;
    }

    /// <summary>One device's state at one line, copied out of a reader.</summary>
    public sealed class RecordedInputFrame
    {
        public long Microseconds;
        public int Device;
        /// <summary>True for a full frame, which restates the state and is
        /// no new report.</summary>
        public bool Full;
        public bool Online;
        public CustomInputState State;
    }

    /// <summary>
    /// Reads a problem recording (#600) back: its parts, its devices and
    /// virtual controllers, and its events one line at a time, with each
    /// device's state and each controller's frame kept exact to the integer
    /// as the lines go by. It needs no running engine and no window, so a
    /// test and a replay both call it.
    ///
    /// <para>Lines are read in the order they were written. Each carries its
    /// own capture time, and lines of two threads can be a little out of
    /// order by their times.</para>
    ///
    /// <para>A file cut short reads as far as it goes:
    /// <see cref="Ended"/> and <see cref="HashMatches"/> say what the reader
    /// found at its end.</para>
    /// </summary>
    public sealed class RecordingReader : IDisposable
    {
        /// <summary>The most devices a reader keeps. The devices part names
        /// every device of PadForge's list, which has no limit of its own,
        /// and the events name at most the 256 the recorder tracks
        /// (RecordingCapture.MaxDevices). A line of a device past this is
        /// unreadable, so a file from anyone cannot make a reader hold a
        /// device for every line.</summary>
        public const int MostDevices = 1024;

        /// <summary>The virtual controllers a file names: C1 to C16, one for
        /// each slot (InputManager.MaxPads). A line of any other is
        /// unreadable.</summary>
        public const int MostControllers = 16;

        private readonly TextReader _reader;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly List<RecordedDevice> _devices = new();
        private readonly List<RecordedController> _controllers = new();
        private readonly Dictionary<int, RecordedDevice> _deviceOf = new();
        private readonly Dictionary<int, RecordedController> _controllerOf = new();
        private string _line;
        private int _payloadStart;
        private bool _inEvents;
        private bool _done;

        private readonly List<string> _summary = new(), _scrub = new(), _system = new(), _settings = new();

        public string FirstLine { get; private set; } = string.Empty;
        public string StatusLine { get; private set; } = string.Empty;
        public bool IsRecording => StatusLine.StartsWith(RecordingCodec.StatusRecording, StringComparison.Ordinal);
        public bool IsRecordingFile => FirstLine.StartsWith("PadForge problem recording", StringComparison.Ordinal);

        /// <summary>The parts the file says it holds. A format 1 file, which
        /// has no Holds line, holds every part.</summary>
        public RecordingParts Parts { get; private set; } = RecordingParts.All;

        public IReadOnlyList<string> Summary => _summary;
        public IReadOnlyList<string> ScrubCheck => _scrub;
        public IReadOnlyList<string> SystemPart => _system;
        public IReadOnlyList<string> Settings => _settings;
        public IReadOnlyList<RecordedDevice> Devices => _devices;
        public IReadOnlyList<RecordedController> Controllers => _controllers;

        // The event the reader is on.
        public long Microseconds { get; private set; }
        public double Seconds => Microseconds / 1_000_000.0;
        public char Layer { get; private set; }
        public string Subject { get; private set; } = string.Empty;
        public RecordedDevice Device { get; private set; }
        public RecordedController Controller { get; private set; }
        /// <summary>True when the line is a full frame.</summary>
        public bool IsFullFrame { get; private set; }
        /// <summary>True when the line carries counts of one pass, a
        /// mouse's or a Joy-Con 2's, which a replay plays once.</summary>
        public bool HasPerPassCounts { get; private set; }
        /// <summary>The tokens of the line this version does not know.</summary>
        public int UnknownTokens { get; private set; }
        public ReadOnlySpan<char> Payload => _line == null ? default : _line.AsSpan(Math.Min(_payloadStart, _line.Length));
        public string Line => _line ?? string.Empty;

        // What the end of the file said.
        /// <summary>True when the events part closed with its end line.</summary>
        public bool Ended { get; private set; }
        public bool HasHash { get; private set; }
        /// <summary>True when the last line's hash is the hash of every line
        /// before it: the file is whole and unchanged.</summary>
        public bool HashMatches { get; private set; }
        public long EventLines { get; private set; }
        public long UnreadableLines { get; private set; }

        /// <summary>Reads a recording from a file: a finished one's zip, or
        /// the plain text of a .log or of a recording that still runs.</summary>
        public static RecordingReader Open(string path) => new(OpenText(path));

        /// <summary>A recording's text from a file. A zip, which a finished
        /// recording is, is read through its one .log, and anything else as
        /// plain text.</summary>
        public static TextReader OpenText(string path)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try
            {
                Span<byte> head = stackalloc byte[4];
                int read = file.Read(head);
                file.Position = 0;
                if (read == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4)
                {
                    var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
                    ZipArchiveEntry log = null;
                    foreach (var entry in archive.Entries)
                        if (entry.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) { log = entry; break; }
                    if (log == null)
                    {
                        archive.Dispose();
                        throw new InvalidDataException("The zip holds no .log.");
                    }
                    return new ArchiveReader(archive, log.Open());
                }
                return new StreamReader(file, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>The text of a zip's entry, which lets go of the zip with
        /// itself.</summary>
        private sealed class ArchiveReader : StreamReader
        {
            private readonly ZipArchive _archive;

            public ArchiveReader(ZipArchive archive, Stream entry)
                : base(entry, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16)
                => _archive = archive;

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) _archive.Dispose();
            }
        }

        /// <summary>Reads the parts above the events. The reader then
        /// stands before the first event.</summary>
        public RecordingReader(TextReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            ReadHeader();
        }

        private string NextLine()
        {
            string line = _reader.ReadLine();
            if (line == null) return null;
            Hash(line);
            return line;
        }

        private void Hash(string line)
        {
            int max = Encoding.UTF8.GetMaxByteCount(line.Length) + 1;
            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int n = Encoding.UTF8.GetBytes(line, 0, line.Length, rented, 0);
                rented[n++] = (byte)'\n';
                _hash.AppendData(rented, 0, n);
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
        }

        private void ReadHeader()
        {
            List<string> part = null;
            bool devices = false, controllers = false;
            string line;
            int at = 0;
            while ((line = NextLine()) != null)
            {
                if (at == 0) FirstLine = line;
                at++;
                if (line.StartsWith(RecordingCodec.StatusPrefix, StringComparison.Ordinal)) { StatusLine = line; continue; }
                if (part == null && line.StartsWith(RecordingCodec.HoldsPrefix, StringComparison.Ordinal))
                {
                    Parts = RecordingPartWords.ParseHolds(line.Substring(RecordingCodec.HoldsPrefix.Length));
                    continue;
                }
                if (line == RecordingCodec.EventsHeader) { _inEvents = true; return; }
                if (line.StartsWith("== ", StringComparison.Ordinal))
                {
                    devices = line == RecordingCodec.DevicesHeader;
                    controllers = line == RecordingCodec.ControllersHeader;
                    part = line == RecordingCodec.SummaryHeader ? _summary
                         : line == RecordingCodec.ScrubHeader ? _scrub
                         : line == RecordingCodec.SystemHeader ? _system
                         : line == RecordingCodec.SettingsHeader ? _settings
                         : null;
                    continue;
                }
                if (devices) ReadDeviceLine(line);
                else if (controllers) ReadControllerLine(line);
                else part?.Add(line);
            }
            _done = true;
        }

        private void ReadDeviceLine(string line)
        {
            int space = line.IndexOf(' ');
            int number = RecordingCodec.SubjectNumber(space < 0 ? line : line.AsSpan(0, space), 'D');
            if (number <= 0) return;
            var device = DeviceByNumber(number, make: true);
            if (device != null) device.Facts = RecordingCodec.ParseFacts(space < 0 ? default : line.AsSpan(space + 1));
        }

        private void ReadControllerLine(string line)
        {
            int space = line.IndexOf(' ');
            int number = RecordingCodec.SubjectNumber(space < 0 ? line : line.AsSpan(0, space), 'C');
            if (number <= 0) return;
            var controller = ControllerByNumber(number, make: true);
            if (controller != null) controller.Facts = RecordingCodec.ParseFacts(space < 0 ? default : line.AsSpan(space + 1));
        }

        /// <summary>The device a number names, or null. D1 is 1.</summary>
        public RecordedDevice DeviceByNumber(int number) => DeviceByNumber(number, make: false);

        /// <summary>The device a number names, made when it is new, or null
        /// past <see cref="MostDevices"/>.</summary>
        private RecordedDevice DeviceByNumber(int number, bool make)
        {
            if (_deviceOf.TryGetValue(number, out var known)) return known;
            if (!make || _devices.Count >= MostDevices) return null;
            var made = new RecordedDevice { Number = number };
            _devices.Add(made);
            _deviceOf.Add(number, made);
            return made;
        }

        /// <summary>The virtual controller a number names, or null. C1 is 1.</summary>
        public RecordedController ControllerByNumber(int number) => ControllerByNumber(number, make: false);

        /// <summary>The controller a number names, made when it is new, or
        /// null past <see cref="MostControllers"/>.</summary>
        private RecordedController ControllerByNumber(int number, bool make)
        {
            if (_controllerOf.TryGetValue(number, out var known)) return known;
            if (!make || number > MostControllers) return null;
            var made = new RecordedController { Number = number };
            _controllers.Add(made);
            _controllerOf.Add(number, made);
            return made;
        }

        /// <summary>Moves to the next event and applies it. False at the end
        /// of the events, after which <see cref="Ended"/>,
        /// <see cref="HasHash"/> and <see cref="HashMatches"/> are set.</summary>
        public bool Read()
        {
            if (_done || !_inEvents) return false;
            while (true)
            {
                // The hash covers every line before the hash line, so it is
                // taken before the line that may be it is added.
                string line = _reader.ReadLine();
                if (line == null) { _done = true; return false; }
                if (line.StartsWith(RecordingCodec.HashPrefix, StringComparison.Ordinal))
                {
                    HasHash = true;
                    string expected = Convert.ToHexString(_hash.GetCurrentHash());
                    HashMatches = string.Equals(expected, line.Substring(RecordingCodec.HashPrefix.Length).Trim(), StringComparison.OrdinalIgnoreCase);
                    // Anything after the hash line means the file was added to.
                    if (_reader.ReadLine() != null) HashMatches = false;
                    _done = true;
                    return false;
                }
                Hash(line);
                if (line == RecordingCodec.EndHeader) { Ended = true; continue; }
                if (line.Length == 0) continue;
                // A line after the end line belongs to no part.
                if (Ended) { Ended = false; UnreadableLines++; continue; }

                if (!RecordingCodec.TrySplitEvent(line, out long micros, out char layer, out var subject, out var payload))
                {
                    UnreadableLines++;
                    continue;
                }
                // A device or controller past the ones a reader keeps makes
                // the line unreadable.
                int deviceNumber = RecordingCodec.SubjectNumber(subject, 'D');
                int controllerNumber = RecordingCodec.SubjectNumber(subject, 'C');
                var device = deviceNumber > 0 ? DeviceByNumber(deviceNumber, make: true) : null;
                var controller = controllerNumber > 0 ? ControllerByNumber(controllerNumber, make: true) : null;
                if ((deviceNumber > 0 && device == null) || (controllerNumber > 0 && controller == null))
                {
                    UnreadableLines++;
                    continue;
                }
                _line = line;
                _payloadStart = payload.IsEmpty ? line.Length : line.Length - payload.Length;
                Microseconds = micros;
                Layer = layer;
                Subject = subject.ToString();
                Device = device;
                Controller = controller;
                IsFullFrame = false;
                HasPerPassCounts = false;
                UnknownTokens = 0;
                EventLines++;

                if (layer == RecordingCodec.LayerInput && Device != null) ApplyInput(payload);
                else if (layer == RecordingCodec.LayerOutput && Controller != null) ApplyOutput(payload);
                else if (layer == RecordingCodec.LayerSent && Controller != null
                    && RecordingCodec.TryGetValue(payload, "sent", out var sent))
                    Controller.Sent = RecordingCodec.SentState.Parse(sent);
                return true;
            }
        }

        private void ApplyInput(ReadOnlySpan<char> payload)
        {
            // A device that connected while the recording ran says what it is
            // on a line of its own.
            if (payload.StartsWith("device "))
            {
                Device.Facts = RecordingCodec.ParseFacts(payload.Slice(7));
                return;
            }
            var state = Device.State;
            while (!payload.IsEmpty)
            {
                int space = payload.IndexOf(' ');
                var token = space < 0 ? payload : payload.Slice(0, space);
                payload = space < 0 ? default : payload.Slice(space + 1);
                if (token.IsEmpty) continue;
                if (token.SequenceEqual(RecordingCodec.FullToken))
                {
                    RecordingCodec.Reset(state);
                    IsFullFrame = true;
                    Device.HasFullFrame = true;
                    continue;
                }
                if (!RecordingCodec.TryParseInputToken(token, out var field, out int index, out int a, out int b, out int c))
                {
                    UnknownTokens++;
                    continue;
                }
                if (field == RecordingField.Online)
                {
                    Device.Online = a != 0;
                    if (a == 0) Device.OfflineReason = b;
                    continue;
                }
                if (RecordingCodec.IsPerPass(field)) HasPerPassCounts = true;
                RecordingCodec.Apply(state, field, index, a, b, c);
            }
        }

        private void ApplyOutput(ReadOnlySpan<char> payload)
        {
            var state = Controller.Output;
            while (!payload.IsEmpty)
            {
                int space = payload.IndexOf(' ');
                var token = space < 0 ? payload : payload.Slice(0, space);
                payload = space < 0 ? default : payload.Slice(space + 1);
                if (token.IsEmpty) continue;
                if (token.SequenceEqual(RecordingCodec.FullToken))
                {
                    state.Reset();
                    IsFullFrame = true;
                    Controller.HasFullFrame = true;
                    continue;
                }
                if (!RecordingCodec.TryParseOutputToken(token, out var field, out int index, out int a, out int b, out int c))
                {
                    UnknownTokens++;
                    continue;
                }
                if (RecordingCodec.IsPerPass(field)) HasPerPassCounts = true;
                RecordingCodec.Apply(state, field, index, a, b, c);
            }
        }

        /// <summary>Every input line of a file as a copy of its device's
        /// state after it, for a test or a short replay. A long file is read
        /// with <see cref="Read"/>, which keeps one state per device.</summary>
        public static List<RecordedInputFrame> ReadInputFrames(TextReader text)
        {
            var frames = new List<RecordedInputFrame>();
            using var reader = new RecordingReader(text);
            while (reader.Read())
            {
                if (reader.Layer != RecordingCodec.LayerInput || reader.Device == null) continue;
                if (reader.Payload.StartsWith("device ")) continue;
                frames.Add(new RecordedInputFrame
                {
                    Microseconds = reader.Microseconds,
                    Device = reader.Device.Number,
                    Full = reader.IsFullFrame,
                    Online = reader.Device.Online,
                    State = reader.Device.State.Clone(),
                });
            }
            return frames;
        }

        public void Dispose()
        {
            _hash.Dispose();
            _reader.Dispose();
        }
    }
}
