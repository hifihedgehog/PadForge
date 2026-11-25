using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;

namespace PadForge.Services
{
    /// <summary>Why the problem recorder did not do what it was asked.</summary>
    internal enum RecorderRefusal
    {
        None,
        /// <summary>A recording already runs.</summary>
        AlreadyRecording,
        /// <summary>No recording runs.</summary>
        NotRecording,
        /// <summary>The last file is still being finished.</summary>
        Busy,
        /// <summary>The check at the start found a private value the
        /// scrubber let through. The detail names the kinds.</summary>
        Privacy,
        /// <summary>The file could not be made or written. The detail is
        /// the reason.</summary>
        File,
        /// <summary>Keep the Last 60 Seconds is off, so nothing is kept.</summary>
        NothingKept,
    }

    /// <summary>What the recorder answered: whether it did it, why not, and
    /// the file it concerns.</summary>
    internal readonly record struct RecorderResult(bool Ok, RecorderRefusal Refusal, string Detail, string Path)
    {
        internal static RecorderResult Done(string path) => new(true, RecorderRefusal.None, string.Empty, path);
        internal static RecorderResult No(RecorderRefusal why, string detail = "") => new(false, why, detail ?? string.Empty, null);
    }

    /// <summary>The recorder at one moment.</summary>
    internal readonly record struct RecorderState(
        bool Recording, bool Finishing, bool KeepingLastMinute, TimeSpan Elapsed, long Bytes, string File, string LastFile);

    /// <summary>
    /// The problem recorder (#597): a recording of everything PadForge read,
    /// sent and logged while a problem happened, scrubbed of private values,
    /// in one text file beside the exe that a reader can play back.
    ///
    /// <para>The poll thread hands changes to a ring
    /// (<see cref="RecordingCapture"/>). This class's own thread takes them
    /// out ten times a second, turns them into lines and writes them, so a
    /// freeze leaves the file whole up to that moment. Lines from other
    /// threads, the diagnostics ring's among them, wait in a queue with
    /// their own times, as <c>Ds5WriteTrace</c> queues its lines for a drain
    /// thread. At the stop the file is read back, its summary and scrub
    /// check are written above everything through a temp file beside it,
    /// and a hash of the body goes on the last line. A file PadForge did not
    /// finish is finished the same way at the next start.</para>
    ///
    /// <para>Every method here is safe from any thread but the poll thread,
    /// which uses <see cref="RecordingCapture"/> alone.</para>
    /// </summary>
    internal sealed partial class ProblemRecorder
    {
        /// <summary>The recorder of the running app.</summary>
        internal static ProblemRecorder Instance { get; } = new(new RecorderHost());

        internal const string FilePrefix = "PadForge-recording-";

        /// <summary>The finished recording's name inside its zip.</summary>
        internal const string FileSuffix = ".log";

        /// <summary>The plain text a recording writes while it runs. A
        /// PadForge that closes leaves it whole up to its last flush, and
        /// the next start finishes it.</summary>
        internal const string PartialSuffix = ".log.partial";

        /// <summary>The end of a zip's name while it is being made.</summary>
        internal const string TempSuffix = ".tmp";

        /// <summary>What a finished recording is: a zip of its one .log,
        /// which Explorer opens and GitHub takes as an attachment.</summary>
        internal const string ZipSuffix = ".zip";

        /// <summary>How hard the zip is packed. Optimal packed a minute of a
        /// heavy session 5.9 times and one moving DualSense 3.0 times, in
        /// 439 ms and 129 ms of the writer's time. SmallestSize packed 2%
        /// tighter for 2.4 to 3 times the time.</summary>
        internal const CompressionLevel Packing = CompressionLevel.Optimal;

        /// <summary>The zip a partial file becomes.</summary>
        internal static string ZipPathOf(string partialPath)
            => partialPath.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)
                ? partialPath.Substring(0, partialPath.Length - PartialSuffix.Length) + ZipSuffix
                : partialPath;

        /// <summary>What a file PadForge did not stop is said to have ended
        /// with.</summary>
        internal const string EndedByClosing = "PadForge closed before the recording was stopped";

        internal readonly RecorderHost Host;

        /// <summary>A recording stops by itself at this length, or at
        /// <see cref="MaxBytes"/>, whichever comes first (#600).</summary>
        internal TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

        /// <summary>How long a recording waits for its review before it is
        /// deleted, since it holds what was typed (#600).</summary>
        internal static readonly TimeSpan KeptUnsaved = TimeSpan.FromDays(7);

        /// <summary>A recording stops by itself when its zip would be this
        /// size, under the 25 MB GitHub takes as an attachment. The text
        /// written is counted as the zip packs it.</summary>
        internal long MaxBytes = 20L * 1024 * 1024;

        /// <summary>How far back Keep the Last 60 Seconds reaches.</summary>
        internal TimeSpan Kept = TimeSpan.FromSeconds(60);

        /// <summary>The most changes Keep the Last 60 Seconds holds. Past
        /// it the oldest go, and the file says the minute was cut short.</summary>
        internal int MaxKeptEntries = 1 << 20;

        private const int MaxQueued = 50_000;
        private const int FlushMs = 100;

        private readonly object _gate = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly ConcurrentQueue<Action> _commands = new();
        private readonly ConcurrentQueue<RecordingSideEvent> _side = new();
        private readonly Action<long, string> _taker;
        private Thread _thread;
        private int _queued;
        private long _queueLost;
        private int _boundaries, _boundariesSeen;

        // The recorder's thread owns these.
        private RecordingCapture _capture;
        private RecordingState _live;
        private RollingStore _rolling;
        private FileSink _file;
        private RecordingEntry[] _drain = Array.Empty<RecordingEntry>();
        private readonly List<RecordingSideEvent> _sideBatch = new();
        private long _lastFlushStart, _lostSeen, _untrackedSeen, _lastMaskRefresh;

        // Read from any thread.
        private volatile bool _recording, _finishing, _keep, _mask;
        private volatile RecordingParts _parts = RecordingParts.All;
        private volatile string _currentFile, _lastFile;
        private long _epochTicks, _bytes;
        private string _frontPath;
        private long _frontChecked;

        /// <summary>Raised when the state changes, and a few times a second
        /// while a recording runs. On whichever thread caused it.</summary>
        internal event Action Changed;

        /// <summary>Raised when a file is finished, with its path. On the
        /// recorder's thread.</summary>
        internal event Action<string> Finished;

        internal ProblemRecorder(RecorderHost host)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            _taker = TakeLine;
        }

        internal RecorderState State
        {
            get
            {
                bool recording = _recording;
                long epoch = Volatile.Read(ref _epochTicks);
                return new RecorderState(recording, _finishing, _keep,
                    recording && epoch != 0 ? Stopwatch.GetElapsedTime(epoch, Ticks()) : TimeSpan.Zero,
                    recording ? Volatile.Read(ref _bytes) : 0, _currentFile, _lastFile);
            }
        }

        /// <summary>The recorder's time now, on the capture's clock.</summary>
        private long Ticks() => Volatile.Read(ref _capture)?.Ticks() ?? Stopwatch.GetTimestamp();

        /// <summary>How many changes the ring between the poll thread and
        /// this class's thread holds.</summary>
        internal int RingCapacity = 1 << 17;

        /// <summary>Test seam: the Stopwatch timestamp a capture's clock
        /// stands still at (<see cref="RecordingCapture.Frozen"/>), or zero
        /// for the real clock.</summary>
        internal long FrozenClock;

        /// <summary>Test seam: keeps this class's thread from taking anything
        /// out of the ring until <paramref name="release"/> is set. Returns
        /// once the thread is held.</summary>
        internal void HoldWriterForTest(ManualResetEventSlim release)
        {
            var held = new ManualResetEventSlim(false);
            Post(() =>
            {
                held.Set();
                release.Wait();
            });
            held.Wait(TimeSpan.FromMinutes(1));
        }

        /// <summary>Test seam: the capture the engine would write to.</summary>
        internal RecordingCapture CaptureForTest => Volatile.Read(ref _capture);

        /// <summary>Test seam: returns once everything handed over so far
        /// is in the file and in the minute kept.</summary>
        internal void WaitForFlush()
        {
            if (_thread == null) return;
            var done = new ManualResetEventSlim(false);
            Post(() =>
            {
                try { if (_capture != null && _live != null) Flush(); }
                finally { done.Set(); }
            });
            done.Wait(TimeSpan.FromSeconds(10));
        }

        // ─────────────────────────────────────────────
        //  The two switches
        // ─────────────────────────────────────────────

        /// <summary>Keep the Last 60 Seconds. While it is on the capture
        /// runs and its last minute stays in memory, for
        /// <see cref="SaveLastMinute"/>.</summary>
        internal bool KeepLastMinute
        {
            get => _keep;
            set
            {
                lock (_gate)
                {
                    if (_keep == value) return;
                    _keep = value;
                    if (value)
                    {
                        EnsureThread();
                        Arm();
                        var capture = _capture;
                        Post(() =>
                        {
                            if (_rolling != null || _live == null) return;
                            // What is already known of every device is where
                            // the minute starts from.
                            _rolling = new RollingStore(MaxKeptEntries, Ticks(), capture);
                            _rolling.Base.CopyFrom(_live);
                        });
                    }
                    else
                    {
                        Post(() =>
                        {
                            _rolling = null;
                            DisarmIfIdle();
                        });
                    }
                }
                Changed?.Invoke();
            }
        }

        /// <summary>Whether a keyboard's letter and number keys that no
        /// mapping and no macro reads are written as one key
        /// (<see cref="RecordingCapture.TypedKey"/>). Off unless the user
        /// turns it on.</summary>
        internal bool MaskTypedKeys
        {
            get => _mask;
            set
            {
                _mask = value;
                var capture = Volatile.Read(ref _capture);
                if (capture == null) return;
                if (value) RefreshKeyMasks(capture);
                capture.MaskTypedKeys = value;
            }
        }

        /// <summary>What to Record (#600): the parts the next recording
        /// holds, all of them unless the user leaves some out. A recording
        /// that runs keeps the parts it started with. The capture stops
        /// taking what no part holds, so a part left out costs the poll
        /// thread nothing.</summary>
        internal RecordingParts Parts
        {
            get => _parts;
            set
            {
                _parts = value & RecordingParts.All;
                if (!_recording && Volatile.Read(ref _capture) is { } capture) capture.Parts = _parts;
                Changed?.Invoke();
            }
        }

        // ─────────────────────────────────────────────
        //  Start, stop, mark, save
        // ─────────────────────────────────────────────

        /// <summary>Starts a recording. It runs when this returns.</summary>
        internal RecorderResult Start()
        {
            RecorderResult result;
            var attached = new ManualResetEventSlim(false);
            lock (_gate)
            {
                if (_recording) return RecorderResult.No(RecorderRefusal.AlreadyRecording);
                if (_finishing) return RecorderResult.No(RecorderRefusal.Busy);
                var sink = Prepare(kept: false, out result);
                if (sink == null) return result;
                _recording = true;
                _currentFile = sink.Path;
                Post(() =>
                {
                    try { AttachFile(sink); }
                    finally { attached.Set(); }
                });
                result = RecorderResult.Done(sink.Path);
            }
            // Waited for outside the gate, which the recorder's thread takes
            // as it lets go of a capture.
            attached.Wait(TimeSpan.FromSeconds(5));
            Changed?.Invoke();
            return result;
        }

        /// <summary>Stops the recording and finishes its file. With
        /// <paramref name="wait"/> it returns when the file is finished, and
        /// without it at once: <see cref="Finished"/> says when.</summary>
        internal RecorderResult Stop(bool wait = false, string how = HowUser)
        {
            string path;
            var done = new ManualResetEventSlim(false);
            lock (_gate)
            {
                if (!_recording) return RecorderResult.No(_finishing ? RecorderRefusal.Busy : RecorderRefusal.NotRecording);
                path = ZipPathOf(_currentFile);
                Post(() =>
                {
                    try
                    {
                        // What the poll thread handed over up to the stop
                        // goes in first. A flush can finish the file itself
                        // at its limit, which leaves nothing to stop.
                        if (_file != null && _capture != null && _live != null) Flush();
                        if (_file != null) FinishFile(_file, how);
                    }
                    finally { done.Set(); }
                });
            }
            if (wait) done.Wait(TimeSpan.FromMinutes(2));
            return RecorderResult.Done(path);
        }

        /// <summary>Starts a recording, or stops the one that runs, saying
        /// who stopped it.</summary>
        internal RecorderResult Toggle(string how = HowUser) => _recording ? Stop(how: how) : Start();

        // What a file's status line says ended it (#600). The review names
        // the stop from these words, so each caller says its own.
        internal const string HowUser = "the user stopped it";
        internal const string HowMacro = "a macro stopped it";
        internal const string HowCommand = "a program or --record stop stopped it";
        internal const string HowAssistant = "the program that started it stopped it";
        internal const string HowClosed = "PadForge closed";
        internal const string HowSnapshot = "a snapshot of how things stood";
        internal const string HowTime = "it stopped by itself at ";
        internal const string HowLastMinute = "saved the last ";

        /// <summary>What a program is told of a recording it stopped or a
        /// snapshot it asked for: that it waits for the user's review, never
        /// where (#600).</summary>
        internal const string WaitsWord = "waiting-for-review";

        /// <summary>
        /// Save Snapshot (#600): a recording with no events of its own, the
        /// state as it stands now and the diagnostics ring's lines, through
        /// the same scrub, summary and review as a recording. It runs beside a
        /// recording that runs, as the last minute does. With
        /// <paramref name="wait"/> it returns when the file is finished.
        /// </summary>
        internal RecorderResult Snapshot(bool wait = false)
        {
            RecorderResult result;
            var done = new ManualResetEventSlim(false);
            lock (_gate)
            {
                if (_finishing) return RecorderResult.No(RecorderRefusal.Busy);
                var sink = Prepare(kept: true, out result);
                if (sink == null) return result;
                _finishing = true;
                Post(() =>
                {
                    try { SaveSnapshot(sink); }
                    finally { done.Set(); }
                });
                result = RecorderResult.Done(sink.ZipPath);
            }
            Changed?.Invoke();
            if (wait) done.Wait(TimeSpan.FromMinutes(2));
            return result;
        }

        private void SaveSnapshot(FileSink sink)
        {
            try
            {
                if (_capture == null || _live == null)
                {
                    sink.Stream.Dispose();
                    sink.Deflate.Dispose();
                    File.Delete(sink.Path);
                    _finishing = false;
                    Changed?.Invoke();
                    return;
                }
                Flush();
                long now = Ticks();
                StartFormatter(sink, now, _live);
                WriteOut(sink);
                FinishFile(sink, HowSnapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                SdlDiagLog.WriteLine("RECORDER snapshot " + ex.GetType().Name + ": " + ex.Message);
                _finishing = false;
                Changed?.Invoke();
            }
        }

        /// <summary>Puts a marker into what is being captured, with words or
        /// without. False when nothing is being captured.</summary>
        internal bool Mark(string text = null)
        {
            if (Volatile.Read(ref _capture) == null) return false;
            Enqueue(new RecordingSideEvent
            {
                Ticks = Ticks(), Kind = SideKind.Marker, Layer = RecordingCodec.LayerMarker, Slot = -1, Text = text,
            });
            return true;
        }

        /// <summary>Writes the last minute Keep the Last 60 Seconds holds as
        /// a recording of its own, with the same summary and checks.</summary>
        internal RecorderResult SaveLastMinute(bool wait = false)
        {
            RecorderResult result;
            var done = new ManualResetEventSlim(false);
            lock (_gate)
            {
                if (!_keep) return RecorderResult.No(RecorderRefusal.NothingKept);
                if (_finishing) return RecorderResult.No(RecorderRefusal.Busy);
                var sink = Prepare(kept: true, out result);
                if (sink == null) return result;
                _finishing = true;
                Post(() =>
                {
                    try { SaveKept(sink); }
                    finally { done.Set(); }
                });
                result = RecorderResult.Done(sink.ZipPath);
            }
            Changed?.Invoke();
            if (wait) done.Wait(TimeSpan.FromMinutes(2));
            return result;
        }

        /// <summary>What PadForge does as it closes: a recording that runs
        /// is stopped and finished, and the capture ends.</summary>
        internal void Shutdown()
        {
            if (_recording) Stop(wait: true, how: HowClosed);
            KeepLastMinute = false;
        }

        // ─────────────────────────────────────────────
        //  Taps of other threads
        // ─────────────────────────────────────────────

        private void Enqueue(in RecordingSideEvent e)
        {
            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _queueLost);
                return;
            }
            _side.Enqueue(e);
        }

        /// <summary>Takes one line of the diagnostics ring, on whichever
        /// thread wrote it. A null line is the ring saying where a history
        /// it just handed out ends.</summary>
        internal void TakeLine(long ticks, string line)
        {
            if (line == null)
            {
                _side.Enqueue(new RecordingSideEvent { Ticks = ticks, Kind = SideKind.Typed, Layer = '\0', Slot = -1, A = ++_boundariesSeen });
                Interlocked.Increment(ref _queued);
                return;
            }
            Enqueue(new RecordingSideEvent { Ticks = ticks, Kind = SideKind.DiagnosticsLine, Layer = RecordingCodec.LayerLine, Slot = -1, Text = line });
        }

        /// <summary>Takes a line the recorder's caller wrote itself, with
        /// nothing private in it, for a virtual controller or for none.</summary>
        internal void TakeTyped(char layer, int slot, string text)
        {
            if (Volatile.Read(ref _capture) == null) return;
            Enqueue(new RecordingSideEvent { Ticks = Ticks(), Kind = SideKind.Typed, Layer = layer, Slot = slot, Text = text });
        }

        /// <summary>Takes the rumble written to a device that has a writer
        /// of its own, named by its path.</summary>
        internal void TakeDeviceWrite(string devicePath, int leftAndRight)
        {
            if (Volatile.Read(ref _capture) == null) return;
            Enqueue(new RecordingSideEvent
            {
                Ticks = Ticks(), Kind = SideKind.DeviceWrite, Layer = RecordingCodec.LayerFeedback, Slot = -1,
                Text = devicePath, A = leftAndRight,
            });
        }

        /// <summary>True while something is being captured, for a tap that
        /// would otherwise build a line nobody takes.</summary>
        internal bool IsCapturing => Volatile.Read(ref _capture) != null;

        /// <summary>Looks at the program in front and writes a line when it
        /// changed. Called on the UI thread, where the lookup lives, a few
        /// times a second at most.</summary>
        internal void UiTick()
        {
            if (Volatile.Read(ref _capture) == null) return;
            WatchSettings();
            long now = Environment.TickCount64;
            if (now - _frontChecked < 250) return;
            _frontChecked = now;
            if (!Host.Front(out string path, out bool named, out string profile)) return;
            if (string.Equals(path, _frontPath, StringComparison.OrdinalIgnoreCase)) return;
            _frontPath = path;
            Enqueue(new RecordingSideEvent
            {
                Ticks = Ticks(), Kind = SideKind.Front, Layer = RecordingCodec.LayerWindow, Slot = -1,
                Text = path, A = named ? 1 : 0, Extra = profile,
            });
        }

        // ─────────────────────────────────────────────
        //  Arming
        // ─────────────────────────────────────────────

        private void EnsureThread()
        {
            if (_thread != null) return;
            // Normal priority, above Ds5WriteTrace's drain, which may drop
            // lines by design: with every core busy at normal priority, a
            // game's threads among them, a writer below normal got no time
            // for seconds and the ring filled (133,526 changes of a heavy
            // ten-second session left out beside 32 spinning threads). The
            // poll thread stays above it.
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "PadForge problem recorder",
                Priority = ThreadPriority.Normal,
            };
            _thread.Start();
        }

        private void Post(Action command)
        {
            _commands.Enqueue(command);
            _wake.Set();
        }

        /// <summary>Makes the capture and points the engine at it. Caller
        /// holds the gate.</summary>
        private void Arm()
        {
            if (_capture != null) return;
            var capture = new RecordingCapture(RingCapacity) { MaskTypedKeys = _mask, Frozen = FrozenClock, Parts = _parts };
            // The devices of the list take their tracks in the list's order.
            foreach (var device in Host.Devices()) capture.Prepare(device);
            if (_mask) RefreshKeyMasks(capture);
            Volatile.Write(ref _capture, capture);
            Post(() =>
            {
                _live = new RecordingState();
                _drain = new RecordingEntry[capture.Ring.Capacity];
                _lostSeen = _untrackedSeen = 0;
                _lastFlushStart = Ticks();
            });
            Post(ResetWatch);
            if (!Host.AttachesToEngine) return;
            RecordingTaps.Reset();
            RecordingCapture.Active = capture;
            ForceTrace.Sink = _forceEar ??= new ForceEar(this);
            SdlDiagLog.Attach(_taker);
            Interlocked.Increment(ref _boundaries);
        }

        private ForceEar _forceEar;

        /// <summary>Ends the capture when no recording runs and no minute is
        /// kept. The recorder's thread.</summary>
        private void DisarmIfIdle()
        {
            lock (_gate)
            {
                if (_recording || _keep || _file != null || _capture == null) return;
                if (Host.AttachesToEngine)
                {
                    if (ReferenceEquals(RecordingCapture.Active, _capture)) RecordingCapture.Active = null;
                    if (ReferenceEquals(ForceTrace.Sink, _forceEar)) ForceTrace.Sink = null;
                    SdlDiagLog.Detach(_taker);
                }
                Volatile.Write(ref _capture, null);
                _live = null;
                _frontPath = null;
                while (_side.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
            }
        }

        private void RefreshKeyMasks(RecordingCapture capture)
        {
            int count = capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var track = capture.Track(i);
                var device = track.Device;
                if (device != null && device.IsKeyboard) track.KeysRead = Host.KeysRead(device);
            }
        }

        // ─────────────────────────────────────────────
        //  A file's start
        // ─────────────────────────────────────────────

        private sealed class FileSink
        {
            /// <summary>The plain partial file being written.</summary>
            public string Path;
            /// <summary>The zip it becomes at the stop.</summary>
            public string ZipPath;
            public FileStream Stream;
            /// <summary>The same bytes packed as the zip will pack them,
            /// kept only as a count: the size the 20 MB limit reads.</summary>
            public ByteCounter Packed;
            public DeflateStream Deflate;
            public PrivacyScrubber Scrubber;
            public RecordingFormatter Formatter;
            public List<(PrivateKind Kind, string Value)> Live;
            public List<string> Declared;
            public List<(long Ticks, string Line)> History;
            public int Boundary;
            public bool PastBoundary;
            public long Epoch;
            public long Bytes;
            public bool Kept;
            /// <summary>The parts the file holds.</summary>
            public RecordingParts Parts = RecordingParts.All;
            public byte[] Buffer = new byte[1 << 16];
            public readonly Encoder Encoder = new UTF8Encoding(false).GetEncoder();
        }

        /// <summary>Everything this PC could give away, for the check at the
        /// start and the search at the stop: what the host knows, and each
        /// device's serial number, instance and Bluetooth address.</summary>
        private List<(PrivateKind Kind, string Value)> LiveValues(PrivacyEnvironment environment, List<UserDevice> devices)
        {
            var live = new List<(PrivateKind, string)>
            {
                (PrivateKind.UserName, environment.UserName),
                (PrivateKind.PcName, environment.PcName),
                (PrivateKind.PcName, environment.DomainName),
                (PrivateKind.Folder, environment.ProfileFolder),
            };
            foreach (var device in devices)
            {
                if (!string.IsNullOrWhiteSpace(device.SerialNumber))
                    live.Add((PrivacyScrubber.BareBluetooth(device.SerialNumber) != null ? PrivateKind.BluetoothAddress : PrivateKind.SerialNumber, device.SerialNumber));
                string instance = PrivacyScrubber.InstancePartOf(device.DevicePath);
                if (!string.IsNullOrEmpty(instance)) live.Add((PrivateKind.DeviceInstance, instance));
                if (device.DevicePath != null && device.DevicePath.StartsWith(WebDevicePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    WebNames(device.ProductName, live);
                    WebNames(device.InstanceName, live);
                }
            }
            Host.LiveValues(live);
            live.RemoveAll(v => string.IsNullOrWhiteSpace(v.Item2));
            return live;
        }

        /// <summary>This PC's own values as they are now, which the review's
        /// scrub check searches the parts kept for (#600).</summary>
        internal List<(PrivateKind Kind, string Value)> CurrentLiveValues() => LiveValues(Host.Environment(), Host.Devices());

        /// <summary>How a web controller's device path starts.</summary>
        internal const string WebDevicePrefix = "web://";

        /// <summary>
        /// The names a person typed that a web controller's device name
        /// carries: the viewer's name after ": ", which is the phone's, and
        /// the name of a custom layout, which the user gave it. PadForge's
        /// own names for its layouts ("Xbox 360 Web Controller 2", "Browser
        /// Gamepad", "Web Touchpad", "Web Menus", "Custom Pad") hold
        /// neither.
        /// </summary>
        internal static void WebNames(string name, List<(PrivateKind Kind, string Value)> into)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            int colon = name.IndexOf(": ", StringComparison.Ordinal);
            if (colon >= 0 && colon + 2 < name.Length) into.Add((PrivateKind.Phone, name.Substring(colon + 2)));
            string stem = colon >= 0 ? name.Substring(0, colon) : name;
            if (stem.Contains(" Web Controller", StringComparison.Ordinal)
                || stem.StartsWith("Browser Gamepad", StringComparison.Ordinal)
                || stem.StartsWith("Web Touchpad", StringComparison.Ordinal)
                || stem.StartsWith("Web Menus", StringComparison.Ordinal))
                return;
            // A custom layout without a viewer is "name N".
            if (colon < 0)
            {
                int space = stem.LastIndexOf(' ');
                if (space > 0 && int.TryParse(stem.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    stem = stem.Substring(0, space);
            }
            if (stem.Length > 0 && stem != "Custom Pad") into.Add((PrivateKind.DeviceName, stem));
        }

        /// <summary>Makes a file with its parts above the events written,
        /// after the check of this PC's own values. Null, with the reason,
        /// when the check fails or the file cannot be made. Caller holds the
        /// gate.</summary>
        private FileSink Prepare(bool kept, out RecorderResult refusal)
        {
            refusal = default;
            var environment = Host.Environment();
            var devices = Host.Devices();
            var live = LiveValues(environment, devices);

            // The check: a scrubber told this PC's values has to keep every
            // one of them out of a line that holds it.
            var probe = new PrivacyScrubber(environment);
            foreach (var (kind, value) in live) probe.Know(kind, value);
            var check = Host.CheckScrub ?? probe.Text;
            var survivors = new List<PrivateKind>(PrivacyScrubber.Survivors(live, check));
            // The lines whose sources write a name or words in a known place
            // are tried too, each with a planted one.
            foreach (var kind in PrivacyScrubber.ShapeSurvivors(check))
                if (!survivors.Contains(kind)) survivors.Add(kind);
            if (survivors.Count > 0)
            {
                var kinds = new List<string>();
                foreach (var kind in survivors) kinds.Add(PrivacyScrubber.KindLabel(kind));
                refusal = RecorderResult.No(RecorderRefusal.Privacy, string.Join(", ", kinds));
                return null;
            }

            EnsureThread();
            Arm();
            var scrubber = new PrivacyScrubber(environment);
            var parts = _parts;
            var sink = new FileSink { Scrubber = scrubber, Live = live, Kept = kept, Declared = new List<string>(), Parts = parts };
            bool settings = (parts & RecordingParts.SettingsAndDevices) != 0;
            if (_capture != null) _capture.Parts = parts;

            var text = new StringBuilder(1 << 14);
            text.Append(RecordingCodec.FirstLine).Append(RecordingFormatter.NewLine);
            text.Append(RecordingCodec.StatusRecording).Append(RecordingFormatter.NewLine);
            text.Append(RecordingCodec.HoldsPrefix).Append(RecordingPartWords.Holds(parts)).Append('.').Append(RecordingFormatter.NewLine);
            text.Append(StartedLine(DateTimeOffset.Now)).Append(' ');
            text.Append(Notice(_mask)).Append(RecordingFormatter.NewLine);

            var lines = new List<string>();
            text.Append(RecordingCodec.SystemHeader).Append(RecordingFormatter.NewLine);
            Guarded(() => Host.SystemLines(scrubber, lines), lines, "system");
            // Without the settings and devices part, the system part says
            // only which PadForge wrote the file.
            if (!settings && lines.Count > 1) lines.RemoveRange(1, lines.Count - 1);
            Append(text, lines);

            // The devices part stays in every file, since every other line
            // names its device by it. Without the settings and devices part
            // a device's line says only what its controls are.
            text.Append(RecordingCodec.DevicesHeader).Append(RecordingFormatter.NewLine);
            foreach (var device in devices)
            {
                string alias = scrubber.DeviceAlias(device.InstanceGuidString);
                if (sink.Declared.Contains(alias)) continue;
                sink.Declared.Add(alias);
                text.Append(alias).Append(settings ? Describe(scrubber, device, alias) : RecorderHost.DescribeShape(device))
                    .Append(RecordingFormatter.NewLine);
            }
            // The typed fields are named first, so a serial number keeps the
            // stand-in that names its device. Then every other live value.
            foreach (var (kind, value) in live) scrubber.Know(kind, value);

            text.Append(RecordingCodec.ControllersHeader).Append(RecordingFormatter.NewLine);
            if (settings) Guarded(() => Host.ControllerLines(scrubber, lines), lines, "controllers");
            else { lines.Clear(); lines.Add(LeftOut); }
            Append(text, lines);

            text.Append(RecordingCodec.SettingsHeader).Append(RecordingFormatter.NewLine);
            if (settings) Guarded(() => Host.SettingsLines(scrubber, lines), lines, "settings");
            else { lines.Clear(); lines.Add(LeftOut); }
            Append(text, lines);
            text.Append(RecordingCodec.EventsHeader).Append(RecordingFormatter.NewLine);

            try
            {
                string folder = Host.Folder;
                if (string.IsNullOrEmpty(folder))
                    throw new IOException("PadForge found no temp folder of the signed-in user to write the recording into.");
                if (RecordingFolder.IsLink(folder))
                    throw new IOException("The recordings folder is a link to another place, which PadForge does not follow.");
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string name = System.IO.Path.Combine(folder, FilePrefix + stamp);
                for (int n = 2; File.Exists(name + PartialSuffix) || File.Exists(name + ZipSuffix); n++)
                    name = System.IO.Path.Combine(folder, FilePrefix + stamp + "-" + n.ToString(CultureInfo.InvariantCulture));
                sink.Path = name + PartialSuffix;
                sink.ZipPath = name + ZipSuffix;
                sink.Stream = new FileStream(sink.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
                sink.Packed = new ByteCounter();
                sink.Deflate = new DeflateStream(sink.Packed, Packing, leaveOpen: true);
                byte[] head = new UTF8Encoding(false).GetBytes(text.ToString());
                sink.Stream.Write(head, 0, head.Length);
                sink.Deflate.Write(head, 0, head.Length);
                sink.Stream.Flush();
                sink.Bytes = sink.Packed.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                sink.Stream?.Dispose();
                refusal = RecorderResult.No(RecorderRefusal.File, ex.Message);
                Post(DisarmIfIdle);
                return null;
            }

            // The diagnostics ring's lines up to now go into the file as its
            // history. The ring says where that history ends in the queue.
            if (Host.AttachesToEngine)
            {
                sink.Boundary = Interlocked.Increment(ref _boundaries);
                sink.History = SdlDiagLog.Attach(_taker);
            }
            else
            {
                sink.PastBoundary = true;
                sink.History = Host.History();
            }
            return sink;
        }

        /// <summary>A device's facts for its line. A device that changes
        /// under the read from another thread gets a line that says so, and
        /// the recording goes on.</summary>
        private string Describe(PrivacyScrubber scrubber, UserDevice device, string alias)
        {
            try { return Host.DescribeDevice(scrubber, device, alias); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                return " error=" + ex.GetType().Name;
            }
        }

        /// <summary>The sentence under a file's status that says what it
        /// holds, in the words the Settings row uses.</summary>
        internal static string Notice(bool maskTypedKeys)
            => "Every key and button pressed on every device while this recorded is in this file, keyboards and mice included."
             + (maskTypedKeys
                ? " Letter and number keys that no mapping or macro reads are written as "
                  + RecordingControlNames.Input(RecordingControlKind.Keyboard, RecordingField.Button, RecordingCapture.TypedKey)
                  + ", each press at its own time."
                : string.Empty)
             + " Read it before you share it.";

        /// <summary>What a part left out of the file says in its place.</summary>
        internal const string LeftOut = "Left out: What to Record leaves this part out.";

        /// <summary>When a recording started, in UTC and in local time with
        /// the time zone, so a reader in another zone reads it right
        /// (#600).</summary>
        internal static string StartedLine(DateTimeOffset now)
        {
            var zone = TimeZoneInfo.Local;
            var offset = now.Offset;
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            var magnitude = offset.Duration();
            return "Started " + now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC, "
                + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " local time ("
                + (zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.StandardName) + ", UTC" + sign
                + magnitude.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + magnitude.Minutes.ToString("00", CultureInfo.InvariantCulture) + ").";
        }

        private static void Guarded(Action fill, List<string> lines, string part)
        {
            lines.Clear();
            try { fill(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or IOException)
            {
                // Settings can change under a read from another thread. The
                // part then says so, and the recording goes on.
                lines.Add("The " + part + " part could not be read: " + ex.GetType().Name + ".");
            }
        }

        private static void Append(StringBuilder text, List<string> lines)
        {
            foreach (string line in lines)
            {
                foreach (char c in line) text.Append(c is '\r' or '\n' ? ' ' : c);
                text.Append(RecordingFormatter.NewLine);
            }
        }

        // ─────────────────────────────────────────────
        //  The recorder's thread
        // ─────────────────────────────────────────────

        private void Run()
        {
            while (true)
            {
                try
                {
                    bool capturing = Volatile.Read(ref _capture) != null;
                    long before = Stopwatch.GetTimestamp();
                    bool woken = _wake.WaitOne(capturing ? FlushMs : Timeout.Infinite);
                    // A wait that ran out long past its time is a PC that held
                    // every program up (#600).
                    if (capturing && !woken)
                        Woke((Stopwatch.GetTimestamp() - before) * 1000 / Stopwatch.Frequency - FlushMs);
                    while (_commands.TryDequeue(out var command)) command();
                    if (_capture != null && _live != null)
                    {
                        Flush();
                        Watch();
                    }
                }
                catch (Exception ex)
                {
                    // A file that can no longer be written ends its
                    // recording, and so does anything else that goes wrong
                    // on this thread: a diagnostics tool must never take
                    // PadForge down, as Ds5WriteTrace's drain keeps going
                    // whatever a line does. The diagnostics ring says what
                    // happened.
                    SdlDiagLog.WriteLine("RECORDER " + ex.GetType().Name + ": " + ex.Message);
                    AbandonFile();
                }
            }
        }

        private void AbandonFile()
        {
            var sink = _file;
            _file = null;
            try { sink?.Stream?.Dispose(); }
            catch (IOException) { }
            sink?.Deflate?.Dispose();
            _recording = false;
            _finishing = false;
            // The file stays as it was cut, its status saying it records,
            // and the next start finishes it. The capture ends unless the
            // minute is kept.
            DisarmIfIdle();
            Changed?.Invoke();
        }

        private void AttachFile(FileSink sink)
        {
            if (_capture == null || _live == null) return;
            Flush();
            // The watch says what it sees anew for this file (#600).
            ResetWatch();
            sink.Epoch = Ticks();
            StartFormatter(sink, sink.Epoch, _live);
            WriteOut(sink);
            _file = sink;
            Volatile.Write(ref _epochTicks, sink.Epoch);
            Volatile.Write(ref _bytes, sink.Bytes);
        }

        /// <summary>Gives a file its formatter, its history lines, its start
        /// marker and the full frame of every device and virtual controller
        /// already known.</summary>
        private void StartFormatter(FileSink sink, long epoch, RecordingState known)
        {
            var scrubber = sink.Scrubber;
            bool settings = (sink.Parts & RecordingParts.SettingsAndDevices) != 0;
            var formatter = new RecordingFormatter(_capture, scrubber, epoch,
                (device, alias) => settings ? Describe(scrubber, device, alias) : RecorderHost.DescribeShape(device))
            {
                Parts = sink.Parts,
            };
            foreach (string alias in sink.Declared) formatter.MarkDeclared(alias);
            sink.Formatter = formatter;

            if (sink.History != null)
            {
                foreach (var (ticks, line) in sink.History)
                {
                    if (ticks >= epoch) continue;
                    formatter.Side(new RecordingSideEvent { Ticks = ticks, Kind = SideKind.DiagnosticsLine, Slot = -1, Text = line });
                }
                sink.History = null;
            }
            formatter.Own(epoch, RecordingCodec.LayerMarker, "start");

            for (int i = 0; i < known.Devices.Length; i++)
                if (known.Devices[i] != null && known.Online[i]) formatter.Seed(i, known.Devices[i], online: true);
            for (int i = 0; i < known.Controllers.Length; i++)
            {
                if (known.Controllers[i] != null) formatter.Seed(i, known.Controllers[i]);
                if (known.Sent[i] >= 0)
                    formatter.Entry(new RecordingEntry
                    {
                        Ticks = epoch, Field = RecordingField.Sent, Subject = (byte)i, A = known.Sent[i], B = known.Controllers[i]?.Type ?? -1,
                    });
            }
            formatter.FullFramesDue(epoch);
            formatter.Close();
        }

        /// <summary>Takes everything out of the ring and the queue, in the
        /// order of its times, and hands it to the file, to the minute kept
        /// and to the state the recorder follows.</summary>
        private void Flush()
        {
            var capture = _capture;
            long horizon = _lastFlushStart;
            long start = Ticks();
            _lastFlushStart = start;

            int count = capture.Ring.Drain(_drain);
            _sideBatch.Clear();
            while (_side.TryDequeue(out var e))
            {
                Interlocked.Decrement(ref _queued);
                _sideBatch.Add(e);
            }
            // Lines of several threads arrive nearly in order. An insertion
            // sort keeps the ones that are.
            for (int i = 1; i < _sideBatch.Count; i++)
            {
                var moved = _sideBatch[i];
                int j = i - 1;
                while (j >= 0 && _sideBatch[j].Ticks > moved.Ticks)
                {
                    _sideBatch[j + 1] = _sideBatch[j];
                    j--;
                }
                _sideBatch[j + 1] = moved;
            }

            var file = _file;
            var rolling = _rolling;
            FileSink full = null;
            int at = 0, sideAt = 0;
            while (at < count || sideAt < _sideBatch.Count)
            {
                if (sideAt >= _sideBatch.Count || (at < count && _drain[at].Ticks <= _sideBatch[sideAt].Ticks))
                {
                    ref RecordingEntry e = ref _drain[at++];
                    _live.Fold(in e);
                    rolling?.Add(in e);
                    if (file == null) continue;
                    file.Formatter.Entry(in e);
                    // Written out as it grows, so a burst the size of the
                    // ring cannot carry the file far past its limit.
                    if (file.Formatter.Out.Length < 1 << 15) continue;
                    WriteOut(file);
                    if (file.Bytes < MaxBytes) continue;
                    full = file;
                    file = null;
                }
                else
                {
                    var e = _sideBatch[sideAt++];
                    if (e.Layer == '\0')
                    {
                        // The ring's word that a history ends here.
                        if (file != null && e.A == file.Boundary) file.PastBoundary = true;
                        continue;
                    }
                    rolling?.Add(in e);
                    if (file == null) continue;
                    if (e.Kind == SideKind.DiagnosticsLine && !file.PastBoundary) continue;
                    file.Formatter.Side(in e);
                }
            }

            long lost = capture.Ring.Lost + Interlocked.Read(ref _queueLost);
            long untracked = capture.Untracked;
            if (lost != _lostSeen)
            {
                string text = "lost=" + (lost - _lostSeen).ToString(CultureInfo.InvariantCulture);
                file?.Formatter.Own(start, RecordingCodec.LayerTiming, text);
                if (rolling != null) rolling.Lost += lost - _lostSeen;
                _lostSeen = lost;
            }
            if (untracked != _untrackedSeen)
            {
                file?.Formatter.Own(start, RecordingCodec.LayerTiming,
                    "untracked=" + (untracked - _untrackedSeen).ToString(CultureInfo.InvariantCulture) + " devices past " + RecordingCapture.MaxDevices.ToString(CultureInfo.InvariantCulture));
                _untrackedSeen = untracked;
            }

            if (_mask && start - _lastMaskRefresh > 2 * Stopwatch.Frequency)
            {
                _lastMaskRefresh = start;
                RefreshKeyMasks(capture);
            }

            rolling?.Trim(start - (long)(Kept.TotalSeconds * Stopwatch.Frequency));

            if (full != null)
            {
                Volatile.Write(ref _bytes, full.Bytes);
                FinishFile(full, HowTime + Words(MaxBytes) + " compressed");
                return;
            }
            if (file == null) return;
            file.Formatter.FullFramesDue(horizon);
            file.Formatter.Close();
            WriteOut(file);
            Volatile.Write(ref _bytes, file.Bytes);
            Changed?.Invoke();

            if (Stopwatch.GetElapsedTime(file.Epoch, start) >= MaxDuration)
                FinishFile(file, HowTime + Words(MaxDuration));
            else if (file.Bytes >= MaxBytes)
                FinishFile(file, HowTime + Words(MaxBytes) + " compressed");
        }

        /// <summary>A length as the file says it: whole minutes past one in
        /// minutes, anything else in seconds, so the minute kept reads as the
        /// 60 seconds its switch names.</summary>
        internal static string Words(TimeSpan span)
            => span.TotalMinutes > 1 && span.TotalMinutes == Math.Floor(span.TotalMinutes)
                ? span.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture) + " minutes"
                : span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds";

        internal static string Words(long bytes)
            => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0
                ? (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";

        /// <summary>Writes the lines the formatter made to the file and
        /// hands them to Windows, so they are the file's whatever comes
        /// next, and packs the same bytes to count what the zip will
        /// hold.</summary>
        private static void WriteOut(FileSink sink)
        {
            var text = sink.Formatter.Out;
            if (text.Length == 0) return;
            foreach (ReadOnlyMemory<char> chunk in text.GetChunks())
            {
                var chars = chunk.Span;
                while (!chars.IsEmpty)
                {
                    sink.Encoder.Convert(chars, sink.Buffer, flush: false, out int used, out int made, out _);
                    sink.Stream.Write(sink.Buffer, 0, made);
                    sink.Deflate.Write(sink.Buffer, 0, made);
                    chars = chars.Slice(used);
                }
            }
            text.Clear();
            sink.Stream.Flush();
            sink.Bytes = sink.Packed.Length;
        }

        /// <summary>Ends a file: its stop marker and end line, then the
        /// summary and scrub check written above everything. The recorder's
        /// thread.</summary>
        private void FinishFile(FileSink sink, string how)
        {
            long now = Ticks();
            sink.Formatter.Close();
            if (!sink.Kept) StillStalled(sink.Formatter, now);
            // The Activity part (#600): every call programs made of PadForge
            // this session, as Export Activity… writes it, scrubbed, at the
            // end of the file.
            if ((sink.Parts & RecordingParts.Activity) != 0)
                foreach (string line in (Host.ActivityText() ?? string.Empty).Split('\n'))
                {
                    string text = line.TrimEnd('\r');
                    if (text.Length > 0) sink.Formatter.Own(now, RecordingCodec.LayerActivity, sink.Scrubber.Text(text));
                }
            int leftOut = Interlocked.Exchange(ref _exceptionsLeftOut, 0);
            if (leftOut > 0)
                sink.Formatter.Own(now, RecordingCodec.LayerError, leftOut.ToString(CultureInfo.InvariantCulture)
                    + " more exceptions past the first " + MostExceptions.ToString(CultureInfo.InvariantCulture) + " were left out");
            sink.Formatter.Own(now, RecordingCodec.LayerMarker, "stop " + how);
            sink.Formatter.Out.Append(RecordingCodec.EndHeader).Append(RecordingFormatter.NewLine);
            WriteOut(sink);
            sink.Stream.Flush(flushToDisk: true);
            sink.Stream.Dispose();
            sink.Deflate.Dispose();
            if (ReferenceEquals(_file, sink)) _file = null;
            if (!sink.Kept) _recording = false;
            _finishing = true;
            Changed?.Invoke();

            bool zipped = false;
            try
            {
                // The search at the stop takes this PC's values as they are
                // now, the devices that came while the file ran among them.
                var liveNow = LiveValues(Host.Environment(), Host.Devices());
                Finish(sink.Path, sink.ZipPath, sink.Scrubber, liveNow, Capitalize(how), sink.Live.Count);
                zipped = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                SdlDiagLog.WriteLine("RECORDER finish " + ex.GetType().Name + ": " + ex.Message);
            }
            // A zip that could not take its name leaves the text, which the
            // next start finishes. A file of that name is never the
            // recording's.
            _lastFile = zipped ? sink.ZipPath : sink.Path;
            if (!sink.Kept) _currentFile = null;
            _finishing = false;
            DisarmIfIdle();
            Changed?.Invoke();
            Finished?.Invoke(_lastFile);
        }

        private static string Capitalize(string text)
            => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        // ─────────────────────────────────────────────
        //  Keep the Last 60 Seconds
        // ─────────────────────────────────────────────

        /// <summary>The last minute of what the capture handed over, as it
        /// was handed over, and the state of everything at the minute's
        /// start. The recorder's thread only.</summary>
        private sealed class RollingStore
        {
            private RecordingEntry[] _entries = new RecordingEntry[1 << 12];
            private int _start, _count;
            private readonly int _max;
            private readonly Queue<RecordingSideEvent> _side = new();
            public readonly RecordingState Base = new();
            public readonly long Armed;
            public readonly RecordingCapture Capture;
            public long Lost;
            /// <summary>True once changes were dropped for room, so less
            /// than the whole minute is held.</summary>
            public bool CutShort;
            public long OldestTicks => _count == 0 ? long.MaxValue : _entries[_start].Ticks;
            public int Count => _count;

            public RollingStore(int max, long armed, RecordingCapture capture)
            {
                _max = Math.Max(16, max);
                Armed = armed;
                Capture = capture;
            }

            public void Add(in RecordingEntry e)
            {
                if (_count == _entries.Length)
                {
                    if (_entries.Length >= _max)
                    {
                        Base.Fold(in _entries[_start]);
                        _start = (_start + 1) % _entries.Length;
                        _count--;
                        CutShort = true;
                    }
                    else
                    {
                        var grown = new RecordingEntry[Math.Min(_max, _entries.Length * 2)];
                        for (int i = 0; i < _count; i++) grown[i] = _entries[(_start + i) % _entries.Length];
                        _entries = grown;
                        _start = 0;
                    }
                }
                _entries[(_start + _count) % _entries.Length] = e;
                _count++;
            }

            public void Add(in RecordingSideEvent e)
            {
                if (_side.Count >= MaxQueued) _side.Dequeue();
                _side.Enqueue(e);
            }

            /// <summary>Lets go of everything older than
            /// <paramref name="cutoff"/>, folding each change into the state
            /// the minute starts from.</summary>
            public void Trim(long cutoff)
            {
                while (_count > 0 && _entries[_start].Ticks < cutoff)
                {
                    Base.Fold(in _entries[_start]);
                    _start = (_start + 1) % _entries.Length;
                    _count--;
                }
                while (_side.Count > 0 && _side.Peek().Ticks < cutoff) _side.Dequeue();
            }

            public ref RecordingEntry At(int index) => ref _entries[(_start + index) % _entries.Length];

            public RecordingSideEvent[] SideEvents() => _side.ToArray();
        }

        private void SaveKept(FileSink sink)
        {
            try
            {
                var rolling = _rolling;
                if (_capture == null || _live == null || rolling == null || !ReferenceEquals(rolling.Capture, _capture))
                {
                    sink.Stream.Dispose();
                    sink.Deflate.Dispose();
                    File.Delete(sink.Path);
                    _finishing = false;
                    Changed?.Invoke();
                    return;
                }
                Flush();
                long now = Ticks();
                long cut = Math.Max(now - (long)(Kept.TotalSeconds * Stopwatch.Frequency), rolling.Armed);
                rolling.Trim(cut);
                StartFormatter(sink, cut, rolling.Base);

                var formatter = sink.Formatter;
                var side = rolling.SideEvents();
                int sideAt = 0;
                for (int i = 0; i < rolling.Count; i++)
                {
                    ref RecordingEntry e = ref rolling.At(i);
                    while (sideAt < side.Length && side[sideAt].Ticks < e.Ticks) formatter.Side(in side[sideAt++]);
                    formatter.Entry(in e);
                    if (formatter.Out.Length > 1 << 16) WriteOut(sink);
                }
                while (sideAt < side.Length) formatter.Side(in side[sideAt++]);
                if (rolling.Lost > 0)
                    formatter.Own(now, RecordingCodec.LayerTiming, "lost=" + rolling.Lost.ToString(CultureInfo.InvariantCulture));
                formatter.FullFramesDue(now);

                string how = HowLastMinute + Words(Kept)
                    + (rolling.CutShort ? ", cut short because more changed than memory was kept for" : string.Empty);
                FinishFile(sink, how);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                SdlDiagLog.WriteLine("RECORDER save " + ex.GetType().Name + ": " + ex.Message);
                _finishing = false;
                Changed?.Invoke();
            }
        }

        // ─────────────────────────────────────────────
        //  A file's end
        // ─────────────────────────────────────────────

        /// <summary>True for a line of a recording that the search of the
        /// finished file reads: every line above the events, and among the
        /// events the lines that hold text: the window, errors, diagnostics
        /// lines, markers, Activity, settings changed, what a reader says of
        /// a device, and a force effect's failure (#600). The lines of input,
        /// output, whether it went out, what a game reads back, timing and
        /// rumble hold only numbers and words the recorder wrote itself, and
        /// a search of them would take a number for a code.</summary>
        internal static bool IsSearched(string line, bool inEvents)
        {
            if (!inEvents) return true;
            int space = line.IndexOf(' ');
            if (space <= 0 || space + 2 >= line.Length) return true;
            char layer = line[space + 1];
            if (layer is RecordingCodec.LayerWindow or RecordingCodec.LayerError or RecordingCodec.LayerLine or RecordingCodec.LayerMarker
                or RecordingCodec.LayerActivity or RecordingCodec.LayerSettingChange or RecordingCodec.LayerReader)
                return true;
            // A force effect's failure carries SDL's words, and a virtual
            // controller not fed its exception's.
            if (layer == RecordingCodec.LayerFeedback)
                return line.IndexOf(" force failed", space, StringComparison.Ordinal) > 0;
            if (layer == RecordingCodec.LayerSent)
                return line.IndexOf(" not fed", space, StringComparison.Ordinal) > 0;
            // A device that connected while the file ran says what it is on
            // an input line, and that line holds names.
            return layer == RecordingCodec.LayerInput && line.IndexOf(" device ", space, StringComparison.Ordinal) > 0
                && line.IndexOf("name=", StringComparison.Ordinal) > 0;
        }

        /// <summary>
        /// Finishes a recording: searches its partial file for this PC's own
        /// values, works out the summary, and writes the finished text, the
        /// status, the summary and the scrub check above everything and a
        /// hash of the body on the last line, as the one .log of a zip. The
        /// zip is made under a temp name beside it and takes its own name
        /// when whole, and the partial file is deleted after, so the text
        /// that was never searched leaves no copy. A partial file left by a
        /// PadForge that closed goes through here too, with no scrubber of
        /// its own.
        /// </summary>
        /// <param name="scrubber">The file's scrubber, or null for a file
        /// whose scrubber is gone.</param>
        /// <param name="startChecked">How many values the check at the
        /// start held, or -1 when that is not known.</param>
        /// <param name="note">For a file the review writes again with parts
        /// left out (#600): the scrub check's line that says so, in place of
        /// the line a file PadForge did not finish gets.</param>
        /// <param name="summary">False leaves the summary out.</param>
        /// <param name="entryName">The .log's name in the zip, when it is not
        /// the zip's own name.</param>
        internal static void Finish(string path, string zipPath, PrivacyScrubber scrubber, IReadOnlyList<(PrivateKind Kind, string Value)> live,
            string ending, int startChecked, PrivacyEnvironment environment = null, string note = null, bool summary = true, string entryName = null)
        {
            bool own = scrubber != null;
            scrubber ??= new PrivacyScrubber(environment);
            foreach (var (kind, value) in live) scrubber.Know(kind, value);

            // The search, counted. The lines themselves are replaced as the
            // file is written again below.
            using (var first = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
            {
                bool inEvents = false;
                string line;
                while ((line = first.ReadLine()) != null)
                {
                    if (line == RecordingCodec.EventsHeader) { inEvents = true; continue; }
                    if (line.StartsWith(RecordingCodec.HashPrefix, StringComparison.Ordinal)) continue;
                    if (line == RecordingCodec.EndHeader || line == RecordingCodec.FirstLine) continue;
                    if (!inEvents && (line.StartsWith("== ", StringComparison.Ordinal) || line.StartsWith(RecordingCodec.StatusPrefix, StringComparison.Ordinal))) continue;
                    if (IsSearched(line, inEvents)) scrubber.SearchFinished(line);
                }
            }

            List<string> summaryLines;
            using (var reader = RecordingReader.Open(path))
                summaryLines = RecordingSummary.Read(reader).Lines();

            var check = new List<string>
            {
                "Private values are replaced by stand-ins such as <bt 1>. One value is one stand-in all through this file.",
            };
            foreach (var count in scrubber.Counts())
            {
                var sb = new StringBuilder(PrivacyScrubber.KindLabel(count.Kind)).Append(": ");
                if (count.Values > 0) sb.Append(count.Values.ToString(CultureInfo.InvariantCulture)).Append(count.Values == 1 ? " value, " : " values, ");
                sb.Append(count.Places.ToString(CultureInfo.InvariantCulture)).Append(count.Places == 1 ? " place" : " places");
                if (count.FoundLate > 0) sb.Append(", ").Append(count.FoundLate.ToString(CultureInfo.InvariantCulture)).Append(" more found by the search at the stop");
                check.Add(sb.ToString());
            }
            if (note != null)
                check.Add(note);
            else if (!own)
                check.Add("PadForge closed while this recording ran, so the counts above are of the search alone.");
            else if (startChecked >= 0)
                check.Add("At the start, " + startChecked.ToString(CultureInfo.InvariantCulture)
                    + " of this PC's own values went through the scrubber as a test and none came through.");
            int found = scrubber.FoundLate;
            check.Add("At the stop, the file was searched for " + live.Count.ToString(CultureInfo.InvariantCulture) + " of this PC's own values: "
                + (found == 0 ? "none found." : found.ToString(CultureInfo.InvariantCulture) + (found == 1 ? " place" : " places") + " found and replaced."));
            check.Add("The event lines of the layers I, O, S, X and T, and the rumble lines of F, hold only numbers the recorder wrote and were not searched.");
            if (scrubber.TooShort > 0)
                check.Add(scrubber.TooShort.ToString(CultureInfo.InvariantCulture) + " of the values are one character long, which no search can tell from the text around it.");
            if (scrubber.LinesLeftOut > 0)
                check.Add(scrubber.LinesLeftOut.ToString(CultureInfo.InvariantCulture) + " lines could not be checked in time and were left out whole.");

            // The zip is made under a name of its own that nothing can hold
            // yet, and takes its name without writing over anything: the
            // folder is the user's, and a file planted there is left alone.
            string temp = zipPath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
            if (entryName == null)
            {
                entryName = System.IO.Path.GetFileName(zipPath);
                entryName = entryName.Substring(0, entryName.Length - ZipSuffix.Length) + FileSuffix;
            }
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var source = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
                using (var archive = new ZipArchive(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), ZipArchiveMode.Create))
                using (var target = new StreamWriter(archive.CreateEntry(entryName, Packing).Open(), new UTF8Encoding(false), 1 << 16))
                {
                    target.NewLine = RecordingFormatter.NewLine;
                    void Write(string text)
                    {
                        target.WriteLine(text);
                        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
                        hash.AppendData(bytes);
                    }

                    bool inEvents = false, headDone = false, skipping = false, ended = false, statusDone = false;
                    string line;
                    while ((line = source.ReadLine()) != null)
                    {
                        if (line.StartsWith(RecordingCodec.HashPrefix, StringComparison.Ordinal)) continue;
                        if (!inEvents && line.StartsWith(RecordingCodec.StatusPrefix, StringComparison.Ordinal))
                        {
                            if (!statusDone) Write(RecordingCodec.StatusFinishedStart + ". " + ending + ".");
                            statusDone = true;
                            continue;
                        }
                        if (!inEvents && line.StartsWith("== ", StringComparison.Ordinal))
                        {
                            // A summary and a scrub check an earlier finish
                            // wrote are written anew.
                            skipping = line is RecordingCodec.SummaryHeader or RecordingCodec.ScrubHeader;
                            if (skipping) continue;
                            if (!headDone)
                            {
                                headDone = true;
                                if (summary)
                                {
                                    Write(RecordingCodec.SummaryHeader);
                                    foreach (string s in summaryLines) Write(s);
                                }
                                Write(RecordingCodec.ScrubHeader);
                                foreach (string s in check) Write(s);
                            }
                            if (line == RecordingCodec.EventsHeader) inEvents = true;
                            Write(line);
                            continue;
                        }
                        if (skipping) continue;
                        // The file's own two fixed lines are written as they
                        // are: a search may not touch what tells a reader
                        // this is a recording and where it ends.
                        if (line == RecordingCodec.EndHeader || line == RecordingCodec.FirstLine)
                        {
                            ended |= line == RecordingCodec.EndHeader;
                            Write(line);
                            continue;
                        }
                        Write(IsSearched(line, inEvents) ? scrubber.SearchFinished(line, count: false) : line);
                    }
                    if (!ended) Write(RecordingCodec.EndHeader);
                    target.WriteLine(RecordingCodec.HashPrefix + Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant());
                }
            }
            try { File.Move(temp, zipPath, overwrite: false); }
            catch (IOException)
            {
                File.Delete(temp);
                throw;
            }
            if (!RecordingFolder.IsLink(path)) File.Delete(path);
        }

        /// <summary>
        /// Finishes and zips every partial recording in the folder that a
        /// PadForge which closed left, and returns the zips' paths. Called
        /// once as PadForge starts, before any recording of this run.
        /// </summary>
        internal List<string> FinishLeftOver()
        {
            var finished = new List<string>();
            string folder;
            string[] files;
            try
            {
                folder = Host.Folder;
                files = Directory.GetFiles(folder, FilePrefix + "*" + PartialSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return finished;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                if (string.Equals(path, _currentFile, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!IsUnfinished(path)) continue;
                    var environment = Host.Environment();
                    string zip = ZipPathOf(path);
                    Finish(path, zip, null, LiveValues(environment, Host.Devices()), EndedByClosing, -1, environment);
                    finished.Add(zip);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    SdlDiagLog.WriteLine("RECORDER left over " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (finished.Count > 0) _lastFile = finished[^1];
            return finished;
        }

        /// <summary>True for a recording whose status line still says it
        /// records.</summary>
        internal static bool IsUnfinished(string path)
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            var lines = RecordingStore.FirstLines(reader, 2);
            return lines.Count == 2 && lines[0].StartsWith(RecordingCodec.FirstLineStart, StringComparison.Ordinal)
                && lines[1].StartsWith(RecordingCodec.StatusRecording, StringComparison.Ordinal);
        }
    }
}
