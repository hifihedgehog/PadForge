using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// A finished recording opened for its review (#600): its text cut into
    /// the summary and the eight parts, each with its size as text and in a
    /// zip, its lines as they will be shared, the scrub check of the parts
    /// kept, and a copy written with the parts the user turned off left out.
    /// It needs no window, so a test opens one as the review does.
    ///
    /// <para>The zip's .log is unpacked once into a file of its own beside
    /// the zip, and each part is an index of its lines' places in that file,
    /// so the review shows every line of a part, however long, and holds
    /// only the places in memory. The file is deleted when the review
    /// closes, and the folder's sweep deletes one a crash left.</para>
    ///
    /// <para>The zip waits in a folder the user can write, so it is read as
    /// a file from anyone: within <see cref="BoundedLineReader"/>'s
    /// limits.</para>
    /// </summary>
    internal sealed class RecordingReview : IDisposable
    {
        /// <summary>The review's own part for the summary and the scrub
        /// check, which no recording leaves out but the review can.</summary>
        internal const RecordingParts Summary = (RecordingParts)(1 << 16);

        /// <summary>The parts in the order the review lists them.</summary>
        internal static readonly RecordingParts[] Order =
        {
            Summary, RecordingParts.SettingsAndDevices, RecordingParts.Input, RecordingParts.Output, RecordingParts.Feedback,
            RecordingParts.Timing, RecordingParts.WindowAndErrors, RecordingParts.Activity, RecordingParts.DiagnosticsLines,
        };

        /// <summary>What a line of the file belongs to.</summary>
        private enum Place : byte
        {
            /// <summary>The opening lines, part headers, markers: every copy
            /// has them.</summary>
            Always,
            /// <summary>A device's own line: every copy has it, shortened to
            /// its shape when settings and devices are left out.</summary>
            Device,
            /// <summary>The summary's or the scrub check's lines.</summary>
            Summary,
            /// <summary>The end line and the hash, which a copy writes anew.</summary>
            End,
            Part,
        }

        /// <summary>One part: whether the recording holds it, whether the
        /// user keeps it, its sizes and its lines.</summary>
        internal sealed class PartInfo
        {
            public RecordingParts Part { get; init; }
            /// <summary>Whether What to Record let the recording hold it.</summary>
            public bool Recorded { get; internal set; }
            public bool Keep { get; set; }
            public long TextBytes { get; internal set; }
            public long ZipBytes { get; internal set; }
            internal readonly List<long> Lines = new();
        }

        private readonly string _view;
        private readonly Dictionary<RecordingParts, PartInfo> _parts = new();
        private readonly List<(long Offset, Place Place)> _all = new();
        private FileStream _viewStream;

        /// <summary>The waiting zip.</summary>
        internal string Path { get; }

        internal IReadOnlyList<PartInfo> Parts { get; }

        /// <summary>The parts What to Record let the recording hold.</summary>
        internal RecordingParts Recorded { get; private set; } = RecordingParts.All;

        /// <summary>What the status line says ended it, as the file says it:
        /// "The user stopped it".</summary>
        internal string Ending { get; private set; } = string.Empty;

        /// <summary>When it started, in local time, or null.</summary>
        internal DateTime? Started { get; private set; }

        /// <summary>How long its events run, from the start marker to the
        /// stop marker.</summary>
        internal TimeSpan Length { get; private set; }

        /// <summary>The whole text's size, and the zip's on disk.</summary>
        internal long TextBytes { get; private set; }
        internal long ZipBytes { get; private set; }

        /// <summary>The summary's lines, for Copy Summary.</summary>
        internal IReadOnlyList<string> SummaryLines => _summaryLines;
        private readonly List<string> _summaryLines = new();

        private RecordingReview(string path, string view)
        {
            Path = path;
            _view = view;
            var parts = new List<PartInfo>();
            foreach (var part in Order)
            {
                var info = new PartInfo { Part = part, Recorded = true, Keep = true };
                _parts[part] = info;
                parts.Add(info);
            }
            Parts = parts;
        }

        /// <summary>The most lines a review takes. A recording the recorder
        /// made stops at a 20 MB zip with about 120 MB of text
        /// (BoundedLineReader), a few million lines, and each line costs the
        /// review an index entry, so a file planted in the folder with more
        /// is refused before it takes gigabytes.</summary>
        internal const int MostLines = 16 * 1024 * 1024;

        /// <summary>Opens a waiting recording: unpacks its text beside it
        /// and reads its parts. Slow for a large one, so off the window's
        /// thread.</summary>
        internal static RecordingReview Open(string zip) => Open(zip, MostLines);

        /// <summary><see cref="Open(string)"/> with a bound of its own on
        /// the lines, for a test.</summary>
        internal static RecordingReview Open(string zip, int mostLines)
        {
            string folder = System.IO.Path.GetDirectoryName(zip);
            string view = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(zip) + "." + Guid.NewGuid().ToString("N") + ProblemRecorder.TempSuffix);
            var review = new RecordingReview(zip, view);
            try
            {
                review.Load(mostLines);
                return review;
            }
            catch
            {
                review.Dispose();
                throw;
            }
        }

        private void Load(int mostLines)
        {
            ZipBytes = new FileInfo(Path).Length;
            var counters = new Dictionary<RecordingParts, (ByteCounter Count, DeflateStream Deflate)>();
            foreach (var part in Order)
            {
                var counter = new ByteCounter();
                counters[part] = (counter, new DeflateStream(counter, ProblemRecorder.Packing, leaveOpen: true));
            }
            var utf8 = new UTF8Encoding(false);
            using (var reader = new BoundedLineReader(RecordingReader.OpenText(Path)))
            using (var output = new FileStream(_view, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16))
            {
                string section = null;
                bool events = false;
                long offset = 0;
                long first = -1, stop = -1;
                string line;
                int at = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (++at > mostLines) throw new InvalidDataException("too-many-lines");
                    if (at == 1 && !line.StartsWith(RecordingCodec.FirstLineStart, StringComparison.Ordinal))
                        throw new InvalidDataException("not-a-recording");
                    var (place, part) = Classify(line, ref section, ref events);
                    if (!events && line.StartsWith(RecordingCodec.StatusPrefix, StringComparison.Ordinal))
                        Ending = EndingOf(line);
                    if (!events && line.StartsWith(RecordingCodec.HoldsPrefix, StringComparison.Ordinal))
                        Recorded = RecordingPartWords.ParseHolds(line.Substring(RecordingCodec.HoldsPrefix.Length));
                    if (place == Place.Summary && section == RecordingCodec.SummaryHeader && line != RecordingCodec.SummaryHeader)
                        _summaryLines.Add(line);
                    if (events && RecordingCodec.TrySplitEvent(line, out long micros, out char layer, out _, out var payload)
                        && layer == RecordingCodec.LayerMarker)
                    {
                        if (payload.SequenceEqual("start") && first < 0) first = micros;
                        if (payload.StartsWith("stop")) stop = micros;
                    }

                    byte[] bytes = utf8.GetBytes(line + "\r\n");
                    output.Write(bytes, 0, bytes.Length);
                    _all.Add((offset, place));
                    TextBytes += bytes.Length;
                    var key = place == Place.Summary ? Summary : place == Place.Part ? part : RecordingParts.None;
                    if (key != RecordingParts.None)
                    {
                        var info = _parts[key];
                        info.Lines.Add(offset);
                        info.TextBytes += bytes.Length;
                        counters[key].Deflate.Write(bytes, 0, bytes.Length);
                    }
                    else if (place == Place.Device)
                    {
                        // A device's line shows with the settings and devices
                        // part. Every copy keeps it, so it counts in no part's
                        // size.
                        _parts[RecordingParts.SettingsAndDevices].Lines.Add(offset);
                    }
                    else if (place == Place.Always && section != null && section != RecordingCodec.EventsHeader && SectionPart(section) is var owner && owner != RecordingParts.None)
                    {
                        // A part's own header line shows with the part.
                        _parts[owner].Lines.Add(offset);
                    }
                    offset += bytes.Length;
                }
                if (first >= 0 && stop >= first) Length = TimeSpan.FromTicks((stop - first) * 10);
            }
            foreach (var (part, (count, deflate)) in counters)
            {
                deflate.Dispose();
                _parts[part].ZipBytes = count.Length;
            }
            Started = RecordingStore.StartedOf(Path);
            foreach (var info in Parts)
            {
                if (info.Part == Summary) continue;
                info.Recorded = (Recorded & info.Part) != 0;
                if (!info.Recorded) info.Keep = false;
            }
            _viewStream = new FileStream(_view, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
        }

        private static string EndingOf(string status)
        {
            string text = status.Substring(RecordingCodec.StatusPrefix.Length).Trim();
            if (text.StartsWith("finished", StringComparison.Ordinal)) text = text.Substring("finished".Length).TrimStart('.', ' ');
            return text.TrimEnd('.');
        }

        /// <summary>The part a header section belongs to.</summary>
        private static RecordingParts SectionPart(string section) => section switch
        {
            RecordingCodec.SystemHeader or RecordingCodec.ControllersHeader or RecordingCodec.SettingsHeader => RecordingParts.SettingsAndDevices,
            RecordingCodec.SummaryHeader or RecordingCodec.ScrubHeader => Summary,
            _ => RecordingParts.None,
        };

        private static (Place, RecordingParts) Classify(string line, ref string section, ref bool events)
        {
            if (line.StartsWith(RecordingCodec.HashPrefix, StringComparison.Ordinal) || line == RecordingCodec.EndHeader)
                return (Place.End, RecordingParts.None);
            if (!events && line.StartsWith("== ", StringComparison.Ordinal))
            {
                section = line;
                events = line == RecordingCodec.EventsHeader;
                return section is RecordingCodec.SummaryHeader or RecordingCodec.ScrubHeader ? (Place.Summary, Summary) : (Place.Always, RecordingParts.None);
            }
            if (!events)
            {
                if (section == null) return (Place.Always, RecordingParts.None);
                if (section is RecordingCodec.SummaryHeader or RecordingCodec.ScrubHeader) return (Place.Summary, Summary);
                if (section == RecordingCodec.DevicesHeader) return (Place.Device, RecordingParts.None);
                return (Place.Part, RecordingParts.SettingsAndDevices);
            }
            if (!RecordingCodec.TrySplitEvent(line, out _, out char layer, out _, out var payload)) return (Place.Always, RecordingParts.None);
            if (layer == RecordingCodec.LayerInput && payload.StartsWith("device ")) return (Place.Device, RecordingParts.None);
            var part = RecordingPartWords.PartOf(layer);
            return part == RecordingParts.None ? (Place.Always, RecordingParts.None) : (Place.Part, part);
        }

        internal PartInfo PartOf(RecordingParts part) => _parts[part];

        // ─────────────────────────────────────────────
        //  Lines, as they will be shared
        // ─────────────────────────────────────────────

        /// <summary>A part's lines, read from the unpacked text as a list
        /// asks for them, so a part of millions of lines shows whole.</summary>
        internal IReadOnlyList<string> LinesOf(RecordingParts part) => new LineList(this, _parts[part].Lines);

        private readonly object _readLock = new();
        private readonly byte[] _lineBuffer = new byte[BoundedLineReader.LongestLine * 4 + 2];

        /// <summary>The line at a place of the unpacked text. Throws
        /// <see cref="ObjectDisposedException"/> once the review is
        /// closed.</summary>
        private string ReadAt(long offset)
        {
            lock (_readLock)
            {
                if (_viewStream == null) throw new ObjectDisposedException(nameof(RecordingReview));
                _viewStream.Position = offset;
                int n = 0;
                while (n < _lineBuffer.Length)
                {
                    int b = _viewStream.ReadByte();
                    if (b < 0 || b == '\n') break;
                    _lineBuffer[n++] = (byte)b;
                }
                if (n > 0 && _lineBuffer[n - 1] == '\r') n--;
                return Encoding.UTF8.GetString(_lineBuffer, 0, n);
            }
        }

        private sealed class LineList : IReadOnlyList<string>, IList
        {
            private readonly RecordingReview _review;
            private readonly List<long> _offsets;

            public LineList(RecordingReview review, List<long> offsets)
            {
                _review = review;
                _offsets = offsets;
            }

            // A list the window still shows after the review closed reads
            // empty lines.
            public string this[int index]
            {
                get
                {
                    try { return _review.ReadAt(_offsets[index]); }
                    catch (ObjectDisposedException) { return string.Empty; }
                }
            }
            public int Count => _offsets.Count;

            public IEnumerator<string> GetEnumerator()
            {
                for (int i = 0; i < _offsets.Count; i++) yield return this[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            // IList, which a virtualizing list asks for by index.
            object IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
            bool IList.IsFixedSize => true;
            bool IList.IsReadOnly => true;
            bool ICollection.IsSynchronized => false;
            object ICollection.SyncRoot => this;
            int IList.Add(object value) => throw new NotSupportedException();
            void IList.Clear() => throw new NotSupportedException();
            bool IList.Contains(object value) => false;
            int IList.IndexOf(object value) => -1;
            void IList.Insert(int index, object value) => throw new NotSupportedException();
            void IList.Remove(object value) => throw new NotSupportedException();
            void IList.RemoveAt(int index) => throw new NotSupportedException();
            void ICollection.CopyTo(Array array, int index)
            {
                for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
            }
        }

        // ─────────────────────────────────────────────
        //  The scrub check of the parts kept
        // ─────────────────────────────────────────────

        private static readonly Regex StandIn = new(
            @"<(?:i|serial|bt|lan-ip|public-ip|host|access-code|key|peer|pairing-code|id|email|device-name|profile|macro|layer|tag|phrase|word|audio|phone|program|user|pc|folder|channel) [0-9]+>|<serial of D[0-9]+>",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>What the scrub check found in the parts kept: the
        /// stand-ins, how many kinds they are of, and the lines that still
        /// hold one of this PC's own values.</summary>
        internal readonly record struct CheckResult(int StandIns, int Kinds, int Found);

        /// <summary>Runs the scrub check over what a copy with the kept
        /// parts holds: every line the finished file's search reads
        /// (ProblemRecorder.IsSearched) is searched again for this PC's own
        /// values, and its stand-ins are counted.</summary>
        internal CheckResult Check(IReadOnlyList<(PrivateKind Kind, string Value)> live) => Check(live, KeptNow);

        /// <summary>The parts kept now, the review's own summary among them,
        /// taken on the window's thread for a check that runs on
        /// another.</summary>
        internal RecordingParts KeptNow
        {
            get
            {
                var kept = RecordingParts.None;
                foreach (var info in Parts)
                    if (info.Keep) kept |= info.Part;
                return kept;
            }
        }

        /// <summary>The scrub check over the parts <paramref name="kept"/>
        /// names, as they were when it was asked, not as the switches stand
        /// while it runs.</summary>
        internal CheckResult Check(IReadOnlyList<(PrivateKind Kind, string Value)> live, RecordingParts kept)
        {
            bool Keeps(RecordingParts part) => part == RecordingParts.None || (kept & part) != 0;
            var standIns = new HashSet<string>(StringComparer.Ordinal);
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            int found = 0;
            var values = new List<string>();
            foreach (var (_, value) in live)
                if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > 1) values.Add(value.Trim());
            bool events = false;
            foreach (var (offset, place) in _all)
            {
                if (place == Place.End) continue;
                string line = ReadAt(offset);
                if (line == RecordingCodec.EventsHeader) { events = true; continue; }
                if (place == Place.Summary && !Keeps(Summary)) continue;
                if (place == Place.Part && !Keeps(PartOfLine(line, events))) continue;
                if (!ProblemRecorder.IsSearched(line, events)) continue;
                foreach (Match m in StandIn.Matches(line))
                {
                    standIns.Add(m.Value);
                    string word = m.Value.StartsWith("<serial of", StringComparison.Ordinal) ? "serial" : m.Value.Substring(1, m.Value.LastIndexOf(' ') - 1);
                    kinds.Add(word);
                }
                foreach (string value in values)
                {
                    if (line.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    found++;
                    break;
                }
            }
            return new CheckResult(standIns.Count, kinds.Count, found);
        }

        private static RecordingParts PartOfLine(string line, bool events)
        {
            if (!events) return RecordingParts.SettingsAndDevices;
            return RecordingCodec.TrySplitEvent(line, out _, out char layer, out _, out _) ? RecordingPartWords.PartOf(layer) : RecordingParts.None;
        }

        private bool Keeps(RecordingParts part) => part == RecordingParts.None || _parts.TryGetValue(part, out var info) && info.Keep;

        /// <summary>True when every part the recording holds is kept, so the
        /// zip is shared as it is.</summary>
        internal bool KeepsAll
        {
            get
            {
                foreach (var info in Parts)
                    if (info.Recorded && !info.Keep) return false;
                return true;
            }
        }

        /// <summary>The parts kept that the recording holds, as RecordingParts.</summary>
        internal RecordingParts KeptParts
        {
            get
            {
                var parts = RecordingParts.None;
                foreach (var info in Parts)
                    if (info.Part != Summary && info.Recorded && info.Keep) parts |= info.Part;
                return parts;
            }
        }

        /// <summary>The kept parts' size as text, and an estimate of their
        /// size in the zip: each part packed on its own.</summary>
        internal (long Text, long Zip) KeptSize()
        {
            if (KeepsAll) return (TextBytes, ZipBytes);
            long text = TextBytes, zip = 0;
            foreach (var info in Parts)
            {
                if (!info.Keep) text -= info.TextBytes;
                else zip += info.ZipBytes;
            }
            return (text, zip);
        }

        // ─────────────────────────────────────────────
        //  A copy with the parts kept
        // ─────────────────────────────────────────────

        /// <summary>The end of a copy's name. A copy waits for no review, so
        /// the folder's list of waiting recordings leaves it out, and its
        /// sweep deletes one left behind.</summary>
        internal const string CopySuffix = ".kept" + ProblemRecorder.ZipSuffix;

        /// <summary>
        /// Writes a copy of the recording with the parts turned off left out,
        /// beside it, and gives its path. The copy's Holds line names the
        /// parts kept, its summary is worked out again from the lines it
        /// keeps (or left out with the summary), its scrub check is the
        /// search of the finished file again with a line that names the parts
        /// left out, and its hash is its own. A device's line keeps only its
        /// shape when settings and devices are left out. With every part
        /// kept it is the zip itself.
        /// </summary>
        internal string WriteKept(IReadOnlyList<(PrivateKind Kind, string Value)> live, PrivacyEnvironment environment)
        {
            if (KeepsAll) return Path;
            string folder = System.IO.Path.GetDirectoryName(Path);
            string stem = System.IO.Path.GetFileNameWithoutExtension(Path);
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            string partial = System.IO.Path.Combine(folder, stem + "." + tag + ProblemRecorder.PartialSuffix + ProblemRecorder.TempSuffix);
            string copy = System.IO.Path.Combine(folder, stem + "." + tag + CopySuffix);
            bool settings = Keeps(RecordingParts.SettingsAndDevices);
            var left = new List<string>();
            foreach (var info in Parts)
                if (info.Recorded && !info.Keep) left.Add(PartWord(info.Part));

            var utf8 = new UTF8Encoding(false);
            using (var output = new StreamWriter(new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16), utf8, 1 << 16))
            {
                output.NewLine = "\r\n";
                bool events = false;
                string section = null;
                foreach (var (offset, place) in _all)
                {
                    if (place is Place.End or Place.Summary) continue;
                    string line = ReadAt(offset);
                    if (!events && line.StartsWith("== ", StringComparison.Ordinal))
                    {
                        section = line;
                        events = line == RecordingCodec.EventsHeader;
                    }
                    if (place == Place.Part && !Keeps(PartOfLine(line, events)))
                    {
                        continue;
                    }
                    if (!events && line.StartsWith(RecordingCodec.StatusPrefix, StringComparison.Ordinal))
                    {
                        output.WriteLine(RecordingCodec.StatusRecording);
                        continue;
                    }
                    if (!events && line.StartsWith(RecordingCodec.HoldsPrefix, StringComparison.Ordinal))
                    {
                        output.WriteLine(RecordingCodec.HoldsPrefix + RecordingPartWords.Holds(KeptParts) + ".");
                        continue;
                    }
                    if (place == Place.Device && !settings) line = ShapeOnly(line, events);
                    output.WriteLine(line);
                    // A part's header stays with nothing under it but the
                    // words that say it was left out.
                    if (!events && place == Place.Always && SectionPart(section) == RecordingParts.SettingsAndDevices && line == section && !settings)
                        output.WriteLine(ProblemRecorder.LeftOut);
                }
            }
            string note = "At the review, these parts were left out of this copy: " + string.Join(", ", left)
                + ". Its summary and this check were made again from what it keeps.";
            ProblemRecorder.Finish(partial, copy, null, live, Ending, -1, environment, note, summary: Keeps(Summary),
                entryName: stem + ProblemRecorder.FileSuffix);
            return copy;
        }

        private static string PartWord(RecordingParts part)
            => part == Summary ? "summary" : RecordingPartWords.Word(part);

        /// <summary>A device's line with only what a replay needs.</summary>
        private static string ShapeOnly(string line, bool events)
        {
            string head;
            string facts;
            if (events)
            {
                int at = line.IndexOf(" device ", StringComparison.Ordinal);
                if (at < 0) return line;
                head = line.Substring(0, at + " device".Length);
                facts = line.Substring(at + " device ".Length);
            }
            else
            {
                int space = line.IndexOf(' ');
                if (space < 0) return line;
                head = line.Substring(0, space);
                facts = line.Substring(space + 1);
            }
            var parsed = RecordingCodec.ParseFacts(facts);
            var sb = new StringBuilder(head);
            foreach (string key in new[] { "type", "controls", "axes", "sliders", "hats", "buttons", "touchpads", "fingers", "gyro", "accel" })
                if (parsed.TryGetValue(key, out string value)) RecordingCodec.AppendFact(sb, key, value);
            return sb.ToString();
        }

        public void Dispose()
        {
            // A read on another thread finishes its line first.
            lock (_readLock)
            {
                _viewStream?.Dispose();
                _viewStream = null;
            }
            try
            {
                if (File.Exists(_view) && !RecordingFolder.IsLink(_view)) File.Delete(_view);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
