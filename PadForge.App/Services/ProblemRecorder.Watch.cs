using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;

namespace PadForge.Services
{
    /// <summary>
    /// The recorder's watch (#600): what its own thread looks at between
    /// flushes while something is captured, and the notes other threads hand
    /// it. Each finding is a line of its own, queued with its time like any
    /// other thread's line, so the file and the last minute kept both get it.
    /// </summary>
    internal sealed partial class ProblemRecorder
    {
        // ─────────────────────────────────────────────
        //  Notes from other threads
        // ─────────────────────────────────────────────

        /// <summary>A line in the caller's words with free text in it, for a
        /// device or a virtual controller or neither, scrubbed when it is
        /// written: a failed read with SDL's error, a virtual controller not
        /// fed and why, a force effect that failed. <paramref name="cut"/>,
        /// when above 0, is the most characters the line keeps once it is
        /// scrubbed.</summary>
        internal void TakeNote(char layer, int slot, UserDevice device, string text, int cut = 0)
        {
            if (Volatile.Read(ref _capture) == null) return;
            Enqueue(new RecordingSideEvent { Ticks = Ticks(), Kind = SideKind.Note, Layer = layer, Slot = slot, Device = device, Text = text, Cut = cut });
        }

        // A note that repeats every pass (a force effect refused again and
        // again, a virtual controller that throws on every hand-over) is
        // written the first time, and then once each RepeatEvery with how
        // many times it came, so it never floods the queue (#600).
        private readonly Dictionary<(char Layer, int Slot, UserDevice Device, string Key), (long Said, int Since)> _repeats = new();

        /// <summary>How often a note that repeats is written again.</summary>
        internal static readonly TimeSpan RepeatEvery = TimeSpan.FromSeconds(10);

        /// <summary>Whether a note of <paramref name="key"/> is to be written
        /// now, and how many like it went unwritten since the last one.</summary>
        internal bool SaysRepeat(char layer, int slot, UserDevice device, string key, out int since)
        {
            since = 0;
            long now = Environment.TickCount64;
            lock (_repeats)
            {
                var k = (layer, slot, device, key);
                if (_repeats.TryGetValue(k, out var was) && now - was.Said < (long)RepeatEvery.TotalMilliseconds)
                {
                    _repeats[k] = (was.Said, was.Since + 1);
                    return false;
                }
                since = _repeats.TryGetValue(k, out was) ? was.Since : 0;
                _repeats[k] = (now, 0);
                return true;
            }
        }

        /// <summary>A note that may repeat every pass: written as
        /// <see cref="TakeNote"/> writes it, at most once each
        /// <see cref="RepeatEvery"/> for one key, with the count of those left
        /// unwritten. <paramref name="text"/> is made only when it is
        /// written.</summary>
        internal void TakeRepeatedNote(char layer, int slot, UserDevice device, string key, Func<string> text)
        {
            if (Volatile.Read(ref _capture) == null || !SaysRepeat(layer, slot, device, key, out int since)) return;
            TakeNote(layer, slot, device, text() + RepeatTail(since));
        }

        /// <summary>What a repeated note ends with: how many like it went
        /// unwritten since it was last said, or nothing.</summary>
        private static string RepeatTail(int since)
            => since > 0 ? " (and " + since.ToString(CultureInfo.InvariantCulture) + " times more since it was last said)" : string.Empty;

        // Counts up each time the watch starts anew (ResetWatch), so the
        // force writer's ear forgets what it said for an earlier file.
        private int _repeatGeneration;

        /// <summary>Whether a failed read that makes a run
        /// <paramref name="inARow"/> long is written: the first of a run and
        /// each time its count doubles, so a device that fails every pass
        /// writes a line a doubling, never one a pass (#600).</summary>
        internal static bool WritesReadFailure(int inARow) => inARow > 0 && (inARow & (inARow - 1)) == 0;

        /// <summary>A failed read (#600): how many reads of the device have
        /// failed in a row, and the error's own text.</summary>
        internal void TakeReadFailed(UserDevice device, int inARow, string error)
        {
            if (!WritesReadFailure(inARow)) return;
            TakeNote(RecordingCodec.LayerReader, -1, device, "read failed " + inARow.ToString(CultureInfo.InvariantCulture)
                + (inARow == 1 ? " time" : " times") + " in a row" + (string.IsNullOrEmpty(error) ? string.Empty : ": " + error));
        }

        /// <summary>A good read that ends a run of failed ones (#600), with
        /// the run's length.</summary>
        internal void TakeReadsAgain(UserDevice device, int after)
        {
            if (after <= 0) return;
            TakeNote(RecordingCodec.LayerReader, -1, device, "reads again after " + after.ToString(CultureInfo.InvariantCulture)
                + (after == 1 ? " failed read" : " failed reads"));
        }

        /// <summary>The most exceptions one recording writes. Past it they
        /// are counted.</summary>
        internal const int MostExceptions = 40;

        private int _exceptions, _exceptionsLeftOut;
        [ThreadStatic] private static bool t_inException;

        /// <summary>
        /// An exception thrown anywhere in PadForge while something is
        /// captured, the window thread's among them, with its type and
        /// message, scrubbed whole when written and then cut at 300
        /// characters. Taken from the first-chance hook, so one that is
        /// caught and one that ends PadForge both show.
        /// </summary>
        internal void TakeException(Exception ex, bool windowThread)
        {
            if (ex == null || t_inException || Volatile.Read(ref _capture) == null) return;
            t_inException = true;
            try
            {
                if (Interlocked.Increment(ref _exceptions) > MostExceptions)
                {
                    Interlocked.Increment(ref _exceptionsLeftOut);
                    return;
                }
                string text = "exception " + ex.GetType().Name + (windowThread ? " on the window's thread" : string.Empty) + ": "
                    + (ex.Message ?? string.Empty);
                TakeNote(RecordingCodec.LayerError, -1, null, text, cut: 300);
            }
            catch
            {
                // A note about an exception never throws one of its own.
            }
            finally
            {
                t_inException = false;
            }
        }

        // ─────────────────────────────────────────────
        //  The force writer's ear
        // ─────────────────────────────────────────────

        /// <summary>Hands what the force writer does to the capture that
        /// runs: counts on the poll thread, and a note for a failure.</summary>
        private sealed class ForceEar : IForceTrace
        {
            private readonly ProblemRecorder _owner;
            public ForceEar(ProblemRecorder owner) => _owner = owner;

            // The failures said last, by device, what failed and the
            // effect's kind, read before any text is made: a refused effect
            // retries every pass, and a repeat the ring holds inside
            // RepeatEvery costs no allocation and no lock (the tap rule of
            // InputManager.Recording). A failure the ring does not hold goes
            // to SaysRepeat's dictionary. Only the poll thread calls Failed.
            private struct Said
            {
                public UserDevice Device;
                public string What;
                public uint Effect;
                public long At;
                public int Since;
                public int Generation;
                public bool Used;
            }

            private readonly Said[] _said = new Said[8];
            private int _next;

            public void Update(ISdlInputDevice device, uint effectType, bool made, bool started)
            {
                var capture = RecordingCapture.Active;
                if (capture == null) return;
                capture.CountForce(device, made, started);
                capture.ForceKind(device, effectType, refused: false);
            }

            public void Forcing(ISdlInputDevice device)
                => RecordingCapture.Active?.CallIs(RecordingCodec.CallKind.Force);

            public void Stopped(ISdlInputDevice device)
            {
                var capture = RecordingCapture.Active;
                if (capture == null || (capture.Parts & RecordingParts.Feedback) == 0) return;
                _owner.TakeRepeatedNote(RecordingCodec.LayerFeedback, -1, capture.DeviceOfTrack(capture.TrackOfLive(device)), "stopped",
                    () => "force effects stopped");
            }

            public void Failed(ISdlInputDevice device, string what, uint effectType)
            {
                var capture = RecordingCapture.Active;
                if (capture == null || (capture.Parts & RecordingParts.Feedback) == 0) return;
                // A kind the device would not make is one it refused. One it
                // made and could not start is a failure only.
                if (what == ForceTrace.Making) capture.ForceKind(device, effectType, refused: true);
                var owner = capture.DeviceOfTrack(capture.TrackOfLive(device));
                long now = Environment.TickCount64;
                int generation = Volatile.Read(ref _owner._repeatGeneration);
                int at = -1;
                for (int i = 0; i < _said.Length; i++)
                {
                    ref var s = ref _said[i];
                    if (s.Used && s.Generation == generation && ReferenceEquals(s.Device, owner) && s.Effect == effectType && s.What == what)
                    {
                        at = i;
                        break;
                    }
                }
                int since = 0;
                if (at >= 0)
                {
                    ref var s = ref _said[at];
                    if (now - s.At < (long)RepeatEvery.TotalMilliseconds)
                    {
                        s.Since++;
                        return;
                    }
                    since = s.Since;
                }
                // Past the ring, the repeat dictionary decides, so more
                // failures than the ring holds are still written once each
                // RepeatEvery. A skip it counts there, one the ring counted
                // stays in the ring, and each is said once at the next line.
                if (!_owner.SaysRepeat(RecordingCodec.LayerFeedback, -1, owner,
                    "failed " + what + " " + effectType.ToString(CultureInfo.InvariantCulture), out int counted)) return;
                since += counted;
                if (at < 0)
                {
                    at = _next;
                    _next = (_next + 1) % _said.Length;
                }
                _said[at] = new Said { Device = owner, What = what, Effect = effectType, At = now, Generation = generation, Used = true };
                string error = ErrorOfSdl();
                _owner.TakeNote(RecordingCodec.LayerFeedback, -1, owner, "force failed " + what + " a " + ForceTrace.KindWord(effectType) + " effect: "
                    + (string.IsNullOrEmpty(error) ? "no reason given" : error) + RepeatTail(since));
            }

            private static string ErrorOfSdl()
            {
                try { return SDL3.SDL.SDL_GetError(); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
            }
        }

        // ─────────────────────────────────────────────
        //  The watch
        // ─────────────────────────────────────────────

        /// <summary>How long the window, a device call or the engine may be
        /// silent before the file says so, and how often it says so again.</summary>
        internal static readonly TimeSpan SilentFor = TimeSpan.FromSeconds(1), SaidAgainEvery = TimeSpan.FromSeconds(10);

        /// <summary>How late the recorder's own thread may wake before the
        /// file says it woke late.</summary>
        internal const int HeldUpMs = 600;

        /// <summary>How often the health line, the other programs and Raw
        /// Input's list come. A test shortens it.</summary>
        internal TimeSpan HealthEvery = TimeSpan.FromSeconds(10);

        private long _stallSince, _stallSaid, _noPassSince, _noPassSaid, _healthAt;
        private readonly long[] _callSaid = new long[RecordingCapture.CallSlots], _callSaidFor = new long[RecordingCapture.CallSlots];
        private string _shownSaid, _programsSaid;
        // The last read of each XInput number, which the reader comparison
        // and the once-a-second gate of an empty number use whatever the
        // parts, and the last read the file was told of, which the Output
        // part says.
        private readonly XInputRead[] _reads = new XInputRead[XInputReadBack.Places];
        private readonly bool[] _readKnown = new bool[XInputReadBack.Places];
        private readonly XInputRead[] _readSaid = new XInputRead[XInputReadBack.Places];
        private readonly bool[] _readSaidKnown = new bool[XInputReadBack.Places];
        private readonly Dictionary<UserDevice, bool> _rawListed = new();
        private readonly Dictionary<UserDevice, (long Since, bool Said, string Text)> _disagree = new();
        private long _healthCpu = -1, _healthBusy = -1, _healthIdle = -1, _healthTicks;

        /// <summary>Forgets what the watch said, for a new capture.</summary>
        private void ResetWatch()
        {
            _stallSaid = _noPassSince = _noPassSaid = 0;
            Array.Clear(_callSaid);
            Array.Clear(_callSaidFor);
            _healthAt = 0;
            _shownSaid = null;
            _programsSaid = null;
            Array.Clear(_readKnown);
            Array.Clear(_readSaidKnown);
            Array.Clear(_refusedSaid);
            _motorsNamed.Clear();
            Array.Clear(_placeCheckedAt);
            _rawListed.Clear();
            _disagree.Clear();
            ResetReaders();
            Interlocked.Exchange(ref _exceptions, 0);
            Interlocked.Exchange(ref _exceptionsLeftOut, 0);
            lock (_repeats) _repeats.Clear();
            Interlocked.Increment(ref _repeatGeneration);
            // The window's thread starts its settings and holds anew too.
            _restateSettings = true;
            // A window that does not answer as a file starts is said again in
            // it, from its first line.
            if (_stallSince != 0) _stallSince = 0;
        }

        private volatile bool _restateSettings;
        private readonly long[] _placeCheckedAt = new long[XInputReadBack.Places];

        private static long Seconds(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

        private void Line(char layer, string text, UserDevice device = null, int slot = -1)
            => Enqueue(new RecordingSideEvent { Ticks = Ticks(), Kind = SideKind.Typed, Layer = layer, Slot = slot, Device = device, Text = text });

        private static string S(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

        /// <summary>The recorder's thread woke <paramref name="lateMs"/> past
        /// when it meant to. The line says only that: one late wake of one
        /// thread of PadForge does not tell a PC that held every program up
        /// from a pause of PadForge alone.</summary>
        private void Woke(long lateMs)
        {
            var capture = _capture;
            if (capture == null || lateMs < HeldUpMs || (capture.Parts & RecordingParts.Timing) == 0) return;
            Line(RecordingCodec.LayerTiming, "the recording itself woke " + S(lateMs / 1000.0) + " late");
        }

        /// <summary>One look of the watch, on the recorder's thread after a
        /// flush.</summary>
        private void Watch()
        {
            var capture = _capture;
            if (capture == null) return;
            var parts = capture.Parts;
            long now = Stopwatch.GetTimestamp();
            if ((parts & RecordingParts.WindowAndErrors) != 0) WatchWindow(now);
            if ((parts & RecordingParts.Timing) != 0)
            {
                WatchCall(capture);
                WatchPasses(capture);
            }
            if ((parts & RecordingParts.Output) != 0 || (parts & RecordingParts.Input) != 0) WatchXInput(capture, parts);
            if ((parts & RecordingParts.Input) != 0) WatchReaders(capture);
            if ((parts & RecordingParts.Feedback) != 0)
            {
                WatchForceKinds(capture);
                WatchMotors(capture);
            }
            if (_healthAt == 0 || now - _healthAt >= Seconds(HealthEvery))
            {
                bool first = _healthAt == 0;
                _healthAt = now;
                if ((parts & RecordingParts.WindowAndErrors) != 0)
                {
                    string health = Health(now);
                    if (!first && health != null) Line(RecordingCodec.LayerWindow, health);
                    WatchPrograms();
                }
                if ((parts & RecordingParts.Input) != 0) WatchRawInput();
            }
        }

        private void WatchWindow(long now)
        {
            string shown = WindowWatch.Shown;
            if (shown != null && shown != _shownSaid)
            {
                _shownSaid = shown;
                Line(RecordingCodec.LayerWindow, "window " + shown);
            }
            long beat = WindowWatch.LastBeat;
            if (!WindowWatch.Installed || beat == 0) return;
            long age = now - beat;
            double seconds = (double)age / Stopwatch.Frequency;
            if (age > Seconds(SilentFor))
            {
                if (_stallSince == 0)
                {
                    _stallSince = beat;
                    _stallSaid = now;
                    Line(RecordingCodec.LayerWindow, "window stopped answering " + S(seconds) + " ago; " + WindowWatch.Describe(first: true, seconds));
                }
                else if (now - _stallSaid >= Seconds(SaidAgainEvery))
                {
                    _stallSaid = now;
                    Line(RecordingCodec.LayerWindow, "window still not answering after " + S(seconds) + "; " + WindowWatch.Describe(first: false, seconds));
                }
            }
            else if (_stallSince != 0)
            {
                double lasted = (double)(beat - _stallSince) / Stopwatch.Frequency;
                _stallSince = 0;
                Line(RecordingCodec.LayerWindow, "window answers again after " + S(lasted));
            }
        }

        /// <summary>Says the window still does not answer as the file ends.</summary>
        private void StillStalled(RecordingFormatter formatter, long now)
        {
            if (_stallSince == 0) return;
            formatter.Own(now, RecordingCodec.LayerWindow, "window still not answering as the recording ends");
        }

        /// <summary>Says each device call that has not returned after
        /// <see cref="SilentFor"/>, on this thread, never the one in the call:
        /// the poll thread's and every other thread's, again each
        /// <see cref="SaidAgainEvery"/> while it hangs.</summary>
        private void WatchCall(RecordingCapture capture)
        {
            long now = capture.Ticks();
            for (int slot = 0; slot < RecordingCapture.CallSlots; slot++)
            {
                var (track, kind, start) = capture.CallIn(slot);
                if (track < 0 || start == 0) continue;
                long age = now - start;
                if (age < Seconds(SilentFor)) continue;
                if (_callSaidFor[slot] == start && now - _callSaid[slot] < Seconds(SaidAgainEvery)) continue;
                _callSaidFor[slot] = start;
                _callSaid[slot] = now;
                Line(RecordingCodec.LayerTiming, "has been in its " + RecordingCodec.CallKind.Phrase(kind) + " for " + S((double)age / Stopwatch.Frequency),
                    capture.DeviceOfTrack(track));
            }
        }

        /// <summary>A call made on a thread other than the poll thread that
        /// took long (#600), written as the poll thread's are: with its
        /// device, how long it took and what it was.</summary>
        internal void TakeSlowCall(UserDevice device, int kind, long ms)
        {
            if (device == null || ms < RecordingCapture.SlowCallMs) return;
            TakeNote(RecordingCodec.LayerTiming, -1, device, "took ms=" + ms.ToString(CultureInfo.InvariantCulture)
                + " call=" + RecordingCodec.CallKind.Word(kind));
        }

        private void WatchPasses(RecordingCapture capture)
        {
            long last = capture.LastPass;
            if (last == 0 || capture.RestState == 2 || !Host.EngineRunning())
            {
                _noPassSince = 0;
                return;
            }
            long now = capture.Ticks();
            long gap = now - last;
            if (gap > Seconds(SilentFor))
            {
                if (_noPassSince == 0 || now - _noPassSaid >= Seconds(SaidAgainEvery))
                {
                    if (_noPassSince == 0) _noPassSince = last;
                    _noPassSaid = now;
                    Line(RecordingCodec.LayerTiming, "engine has run no pass for " + S((double)gap / Stopwatch.Frequency));
                }
            }
            else if (_noPassSince != 0)
            {
                double lasted = (double)(last - _noPassSince) / Stopwatch.Frequency;
                _noPassSince = 0;
                Line(RecordingCodec.LayerTiming, "engine runs again after " + S(lasted) + " without a pass");
            }
        }

        // ─────────────────────────────────────────────
        //  The effect kinds each device refused
        // ─────────────────────────────────────────────

        private readonly uint[] _refusedSaid = new uint[RecordingCapture.MaxDevices];

        /// <summary>Says when the effect kinds a device refuses change: the
        /// kinds it refuses now, or that it takes every effect asked of it
        /// once it takes the last kind it refused (#600).</summary>
        private void WatchForceKinds(RecordingCapture capture)
        {
            int count = capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                uint refused = capture.ForceKinds(i).Refused;
                if (refused == _refusedSaid[i]) continue;
                _refusedSaid[i] = refused;
                Line(RecordingCodec.LayerFeedback, refused != 0 ? "refused effects: " + KindWords(refused) : "takes every effect asked of it",
                    capture.DeviceOfTrack(i));
            }
        }

        /// <summary>Each device asked for an effect, as the file ends: the
        /// kinds asked, and the kinds it refused or that it takes every
        /// effect asked of it (#600).</summary>
        private void ForceKindsAtEnd(RecordingFormatter formatter, long now)
        {
            var capture = _capture;
            if (capture == null) return;
            int count = capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var (asked, refused) = capture.ForceKinds(i);
                var device = capture.DeviceOfTrack(i);
                if (asked == 0 || device == null) continue;
                formatter.Own(now, RecordingCodec.LayerFeedback, device, "effects asked: " + KindWords(asked)
                    + (refused != 0 ? ". Refused: " + KindWords(refused) + "." : ". Takes every effect asked of it."));
            }
        }

        private readonly HashSet<UserDevice> _motorsNamed = new();

        /// <summary>The live reader each force-feedback device had when the
        /// watch last saw it, for the life of the recorder: a new one is a
        /// device of its product arriving, which lets a product DirectInput
        /// did not list be asked for again (ForceActuators.Arrived).</summary>
        private readonly Dictionary<UserDevice, ISdlInputDevice> _motorDevices = new();

        /// <summary>Names the axes each force-feedback device's motors act on
        /// that this file has not named yet, once a read made on a thread of
        /// its own kept them: a device the header could not name, and one
        /// first connected while recording (#600). A product not yet read is
        /// asked for, and one DirectInput did not list, or could not be asked
        /// about, is let be until a device of it arrives.</summary>
        private void WatchMotors(RecordingCapture capture)
        {
            int count = capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var device = capture.DeviceOfTrack(i);
                if (device == null || !device.IsOnline || !RecorderHost.HasMotors(device)) continue;
                if (!_motorDevices.TryGetValue(device, out var seen) || !ReferenceEquals(seen, device.Device))
                {
                    _motorDevices[device] = device.Device;
                    ForceActuators.Arrived(device.VendorId, device.ProdId);
                }
                if (_motorsNamed.Contains(device)) continue;
                var names = Host.KeptMotorAxisNames(device);
                if (names != null && names.Count > 0)
                {
                    _motorsNamed.Add(device);
                    Line(RecordingCodec.LayerFeedback, "motors act on " + ForceActuators.Join(names), device);
                }
                else if (!ForceActuators.Held(device.VendorId, device.ProdId))
                {
                    ForceActuators.ReadSoon();
                }
            }
        }

        /// <summary>Effect kinds as their words, in SDL's order.</summary>
        internal static string KindWords(uint kinds)
        {
            var sb = new StringBuilder();
            for (int bit = 0; bit < 32; bit++)
            {
                uint kind = 1u << bit;
                if ((kinds & kind) == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(ForceTrace.KindWord(kind));
            }
            return sb.ToString();
        }

        // ─────────────────────────────────────────────
        //  XInput: what a game reads back, and a second reader of a pad
        // ─────────────────────────────────────────────

        /// <summary>How far two readers of one stick may differ, and of one
        /// trigger in XInput's bytes, a sixteenth of the travel each, before
        /// the file says they disagree.</summary>
        internal const int DisagreeAxis = 4096, DisagreeTrigger = 16;

        /// <summary>
        /// How long a difference must last before the file says the two
        /// readers disagree. The watch looks once a flush, every 100 ms, so
        /// this is the same difference at two looks running.
        ///
        /// <para>Measured on 2026-11-25 on a 32-processor PC: both readers of a
        /// pad at XInput place 1 fed from the Test Controller's fastest sweep
        /// (Move Time 0.5 s, no rest, every button pressed for 50 ms, the
        /// D-pad turning), PadForge's reader at 1,000 passes a second through
        /// the real capture, flush and watch. No line in 60 s of the sweep,
        /// 60 s of the circle, or 60 s of the sweep with every processor
        /// busy, while XInput holding Back down was said 9 to 10 times in the
        /// 10 s after each (RecorderReaderComparisonTests).</para>
        /// </summary>
        internal static readonly TimeSpan DisagreeFor = TimeSpan.FromMilliseconds(100);

        /// <summary>How long the engine may run no pass before PadForge's
        /// reader's last state is too old to compare with XInput's.</summary>
        internal static readonly TimeSpan ReaderStaleAfter = TimeSpan.FromMilliseconds(200);

        private void WatchXInput(RecordingCapture capture, RecordingParts parts)
        {
            var live = _live;
            long tick = Environment.TickCount64;
            int count = capture.DeviceCount;
            // Each number PadForge's own XInput library reads, the numbers
            // SDL's "XInput#N" paths carry. The library gives no virtual
            // controller a number (XInputReadBack), so nothing here says where
            // a game reads one.
            for (int place = 0; place < XInputReadBack.Places; place++)
            {
                // An empty number, whose read is the library's slow path, is
                // asked once a second.
                if (_readKnown[place] && !_reads[place].Connected && tick - _placeCheckedAt[place] < 1000) continue;
                _placeCheckedAt[place] = tick;
                var read = XInputReadBack.Read(place);
                _reads[place] = read;
                _readKnown[place] = true;
                if ((parts & RecordingParts.Output) != 0)
                {
                    if (!_readSaidKnown[place] || !read.Equals(_readSaid[place]))
                    {
                        bool said = _readSaidKnown[place];
                        _readSaidKnown[place] = true;
                        _readSaid[place] = read;
                        if (said || read.Connected)
                            Line(RecordingCodec.LayerReadBack, "xinput=" + (place + 1).ToString(CultureInfo.InvariantCulture) + " " + XInputReadBack.Tokens(read));
                    }
                }
            }
            if ((parts & RecordingParts.Input) == 0 || live == null) return;
            // PadForge's reader is read on the poll thread. Suspended behind
            // another window, or with no pass for a while, its last state is
            // old, and a difference from XInput's read now is no
            // disagreement. An idle engine still reads, 20 passes a second.
            if (capture.RestState == 2 || capture.Ticks() - capture.LastPass > Seconds(ReaderStaleAfter)) return;
            for (int i = 0; i < count; i++)
            {
                var device = capture.DeviceOfTrack(i);
                if (device == null || !XInputSlotIdentity.TryParseSlot(device.DevicePath, out int place) || place >= XInputReadBack.Places) continue;
                var state = live.Devices[i];
                if (state == null || !live.Online[i]) continue;
                // SDL reads a pad at an XInput number through the same library
                // and number, a little earlier than this read. A difference that holds the
                // same while both stand still is a disagreement. One that
                // changes is a pad in motion between the two reads.
                string difference = Difference(state, _reads[place]);
                _disagree.TryGetValue(device, out var was);
                if (difference != null)
                {
                    long now = capture.Ticks();
                    if (was.Since == 0 || was.Text != difference) _disagree[device] = (now, false, difference);
                    else if (!was.Said && now - was.Since >= (long)(DisagreeFor.TotalSeconds * Stopwatch.Frequency))
                    {
                        _disagree[device] = (was.Since, true, difference);
                        Line(RecordingCodec.LayerReader, "XInput number " + (place + 1).ToString(CultureInfo.InvariantCulture) + " reads " + difference, device);
                    }
                }
                else if (was.Since != 0)
                {
                    if (was.Said)
                        Line(RecordingCodec.LayerReader, "XInput and PadForge's reader agree again after "
                            + S((double)(capture.Ticks() - was.Since) / Stopwatch.Frequency), device);
                    _disagree.Remove(device);
                }
            }
        }

        private static readonly (int Bit, int Button)[] XInputOfGamepad =
        {
            (Gamepad.A, 0), (Gamepad.B, 1), (Gamepad.X, 2), (Gamepad.Y, 3), (Gamepad.LEFT_SHOULDER, 4), (Gamepad.RIGHT_SHOULDER, 5),
            (Gamepad.BACK, 6), (Gamepad.START, 7), (Gamepad.LEFT_THUMB, 8), (Gamepad.RIGHT_THUMB, 9),
        };

        /// <summary>How XInput's reading of a pad differs from PadForge's own
        /// reader's (the gamepad layout SDL gives), or null when they agree:
        /// a button either side has and the other has not, the D-pad at
        /// another angle, a stick more than <see cref="DisagreeAxis"/> apart,
        /// or a trigger more than <see cref="DisagreeTrigger"/>.</summary>
        internal static string Difference(CustomInputState state, XInputRead read)
        {
            if (!read.Connected) return null;
            var sb = new StringBuilder();
            foreach (var (bit, button) in XInputOfGamepad)
            {
                bool x = (read.Buttons & bit) != 0, own = state.Buttons[button];
                if (x == own) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(RecordingControlNames.GamepadButtons[button]).Append(x ? " down" : " up").Append(" while PadForge's reader reads it ").Append(own ? "down" : "up");
            }
            // XInput's four D-pad bits as SDL turns them into an angle
            // (SdlDeviceWrapper.DpadToCentidegrees), in hundredths of a
            // degree or -1, as the reader's hat is written.
            int hat = SdlDeviceWrapper.DpadToCentidegrees((read.Buttons & Gamepad.DPAD_UP) != 0, (read.Buttons & Gamepad.DPAD_DOWN) != 0,
                (read.Buttons & Gamepad.DPAD_LEFT) != 0, (read.Buttons & Gamepad.DPAD_RIGHT) != 0);
            if (hat != state.Povs[0])
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("DPad ").Append(hat.ToString(CultureInfo.InvariantCulture))
                  .Append(" while PadForge's reader reads ").Append(state.Povs[0].ToString(CultureInfo.InvariantCulture));
            }
            void Stick(string name, int own, int x)
            {
                if (Math.Abs(own - x) <= DisagreeAxis) return;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name).Append(' ').Append(x.ToString(CultureInfo.InvariantCulture))
                  .Append(" while PadForge's reader reads ").Append(own.ToString(CultureInfo.InvariantCulture));
            }
            // SDL's sticks read down as positive, XInput's up.
            Stick("LeftX", state.Axis[0] - 32768, read.LeftX);
            Stick("LeftY", 32767 - state.Axis[1], read.LeftY);
            Stick("RightX", state.Axis[3] - 32768, read.RightX);
            Stick("RightY", 32767 - state.Axis[4], read.RightY);
            // XInput's triggers are bytes, the reader's run 0 to 65535.
            void Trigger(string name, int own, int x)
            {
                if (Math.Abs(own - x) <= DisagreeTrigger) return;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name).Append(' ').Append(x.ToString(CultureInfo.InvariantCulture))
                  .Append(" while PadForge's reader reads ").Append(own.ToString(CultureInfo.InvariantCulture));
            }
            Trigger("LeftTrigger", state.Axis[2] >> 8, read.LeftTrigger);
            Trigger("RightTrigger", state.Axis[5] >> 8, read.RightTrigger);
            return sb.Length == 0 ? null : sb.ToString();
        }

        // ─────────────────────────────────────────────
        //  Raw Input: a second reader of every other device
        // ─────────────────────────────────────────────

        /// <summary>How long a device may send reports that name none of its
        /// compared controls before the file says Raw Input's reports of it
        /// name none of them now: a Bluetooth PlayStation pad once SDL turns
        /// on its full reports (SDL_hints.h:1518-1528).</summary>
        internal static readonly TimeSpan UnnamedAfter = TimeSpan.FromSeconds(1);

        /// <summary>How many reports naming none of a device's compared
        /// controls must come a second for them to count as kept coming. A
        /// slow one alone says nothing: a SpaceMouse Wireless also sends a
        /// battery report of ID 23 (spacenavd dev_usb_freebsd.c:173-175)
        /// while the puck rests and sends no axis report.</summary>
        internal const int OthersPerSecond = 10;

        /// <summary>How long PadForge's reader may see a device move while Raw
        /// Input hands over no report of it before the file says so.</summary>
        internal static readonly TimeSpan NoReportAfter = TimeSpan.FromSeconds(1);

        /// <summary>A device's second reader while a file is written.</summary>
        private sealed class ReaderWatch
        {
            public ISdlInputDevice Live;
            public ReaderLayout Layout;
            public bool Settled, Unnamed, NoReportSaid, HasShape;
            public readonly ReaderReading Reading = new();
            public long Since, MovedSince, LastNamed;
            public bool Said;
            public string Text;
            public int Shape;
            // Where the run of reports naming none of its controls starts:
            // the newest named report, or the first other one when no named
            // report came, and the count of others then.
            public bool Marked;
            public long MarkNamed, MarkAt;
            public int MarkCount;
        }

        private readonly Dictionary<UserDevice, ReaderWatch> _readers = new();

        /// <summary>Lets every device's second reader go, for a new file.</summary>
        private void ResetReaders()
        {
            var source = RecorderReaders.Source;
            _readers.Clear();
            source.Watch(Array.Empty<ReaderLayout>());
            source.ReleaseAll();
        }

        /// <summary>
        /// Raw Input's reading of every device at no XInput place against
        /// PadForge's reader's (#600 item 32): a device SDL reads through
        /// DirectInput, a pad SDL reads through a HIDAPI driver whose meaning is
        /// proven, a SpaceMouse. The same gap, window and stale gate as the
        /// XInput comparison above. Once per device and file a line says how
        /// its controls are matched, or why it has no second reader.
        /// </summary>
        private void WatchReaders(RecordingCapture capture)
        {
            var live = _live;
            if (live == null) return;
            var source = RecorderReaders.Source;
            long now = capture.Ticks();
            // PadForge's reader is read on the poll thread. Suspended, or with
            // no pass for a while, its last state is old (see WatchXInput).
            bool stale = capture.RestState == 2 || now - capture.LastPass > Seconds(ReaderStaleAfter);
            List<ReaderLayout> watching = null;
            int count = capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var device = capture.DeviceOfTrack(i);
                // An SDL device of this PC: keyboards, mice, touchpads, a
                // peer's devices and PadForge's own readers say their second
                // reader in the header alone.
                if (device?.Device is not ISdlInputDevice sdl || RecorderReaderPlan.Signature(sdl.SdlGuid) < 0
                    || sdl is PadForge.Engine.RemoteLink.RemotePeerDevice
                    || device.IsKeyboard || device.IsMouse || device.IsTouchpad) continue;
                if (!device.IsOnline || !live.Online[i] || live.Devices[i] == null) continue;
                if (XInputSlotIdentity.TryParseSlot(device.DevicePath, out int place) && place < XInputReadBack.Places) continue;
                if (!_readers.TryGetValue(device, out var w) || !ReferenceEquals(w.Live, sdl))
                {
                    if (w != null) source.Release(w.Layout);
                    _readers[device] = w = new ReaderWatch { Live = sdl };
                }
                // A layout of a DirectInput pad waits on SDL's mapping, which
                // the engine does not answer while it is suspended or has no
                // pass: none is asked for then, so none is made only to be
                // thrown away at every look.
                if (w.Layout == null && !stale) w.Layout = source.Layout(device);
                var layout = w.Layout;
                if (layout == null) continue;
                if (!w.Settled)
                {
                    w.Settled = true;
                    if (layout.Plan == null)
                    {
                        Line(RecordingCodec.LayerReader, "has no second reader: " + layout.Reason, device);
                        continue;
                    }
                    Line(RecordingCodec.LayerReader, "is compared with its own reports through Raw Input: " + RecorderReaderPlan.MatchWords(layout.Plan), device);
                    foreach (string left in layout.Plan.LeftOut) Line(RecordingCodec.LayerReader, "is not compared on its " + left, device);
                }
                if (layout.Plan == null) continue;
                (watching ??= new List<ReaderLayout>()).Add(layout);
                if (stale) continue;

                var state = live.Devices[i];
                var reading = w.Reading;
                bool read = source.Read(layout, reading);
                if (NoReport(w, device, reading, state, now)) continue;

                // The device keeps sending reports that name none of its
                // compared controls: what Raw Input last named is old.
                bool unnamed = Unnamed(w, reading);
                if (unnamed != w.Unnamed)
                {
                    w.Unnamed = unnamed;
                    Line(RecordingCodec.LayerReader, unnamed ? "the reports Raw Input hands over now name none of its controls" : "Raw Input names its controls again", device);
                }
                if (unnamed || !read)
                {
                    w.Since = 0;
                    w.Said = false;
                    w.Text = null;
                    continue;
                }

                string difference = RecorderReaderMatch.Difference(RecorderReaderPlan.Expected(layout.Plan, reading.Values, reading.Known), state, layout.Plan.Kind);
                if (difference != null)
                {
                    if (w.Since == 0 || w.Text != difference)
                    {
                        w.Since = now;
                        w.Said = false;
                        w.Text = difference;
                    }
                    else if (!w.Said && now - w.Since >= (long)(DisagreeFor.TotalSeconds * Stopwatch.Frequency))
                    {
                        w.Said = true;
                        Line(RecordingCodec.LayerReader, "Raw Input reads " + difference, device);
                    }
                }
                else if (w.Since != 0)
                {
                    if (w.Said)
                        Line(RecordingCodec.LayerReader, "Raw Input and PadForge's reader agree again after "
                            + S((double)(now - w.Since) / Stopwatch.Frequency), device);
                    w.Since = 0;
                    w.Said = false;
                    w.Text = null;
                }
            }
            source.Watch(watching ?? (IReadOnlyList<ReaderLayout>)Array.Empty<ReaderLayout>());
        }

        /// <summary>
        /// Whether reports naming none of a device's compared controls have
        /// kept coming for <see cref="UnnamedAfter"/> past the newest that
        /// named any, at <see cref="OthersPerSecond"/> or more. Times are the
        /// pump's, so the run is measured on the reports alone.
        /// </summary>
        private static bool Unnamed(ReaderWatch w, ReaderReading reading)
        {
            if (!w.Marked || reading.NamedTicks != w.MarkNamed)
            {
                w.Marked = true;
                w.MarkNamed = reading.NamedTicks;
                w.MarkAt = reading.NamedTicks;
                w.MarkCount = reading.OtherCount;
            }
            if (reading.OtherTicks == 0) return false;
            if (w.MarkAt == 0)
            {
                // No named report came: the run starts at the first other.
                w.MarkAt = reading.OtherTicks;
                w.MarkCount = reading.OtherCount;
                return false;
            }
            long span = reading.OtherTicks - w.MarkAt;
            if (span <= Seconds(UnnamedAfter)) return false;
            long others = unchecked(reading.OtherCount - w.MarkCount);
            return others * Stopwatch.Frequency >= OthersPerSecond * span;
        }

        /// <summary>
        /// Whether PadForge's reader has seen a device move for
        /// <see cref="NoReportAfter"/> while Raw Input handed over no new report
        /// naming its controls. Said once per device and file, PadForge's own
        /// change being the positive control in the same window, as Raw Input
        /// handing PadForge nothing of it. HidHide cannot block Raw Input
        /// (docs.nefarius.at, docs/projects/HidHide/FAQ.md:11), so a device
        /// PadForge's setting hides gets the same line, with that setting
        /// said beside it.
        /// </summary>
        private bool NoReport(ReaderWatch w, UserDevice device, ReaderReading reading, CustomInputState state, long now)
        {
            int shape = ShapeOf(state);
            bool moved = w.HasShape && shape != w.Shape;
            w.Shape = shape;
            w.HasShape = true;
            if (reading.NamedTicks != w.LastNamed)
            {
                w.LastNamed = reading.NamedTicks;
                w.MovedSince = 0;
                return false;
            }
            if (!moved) return w.NoReportSaid;
            if (w.MovedSince == 0)
            {
                w.MovedSince = now;
                return w.NoReportSaid;
            }
            if (!w.NoReportSaid && now - w.MovedSince >= Seconds(NoReportAfter))
            {
                w.NoReportSaid = true;
                Line(RecordingCodec.LayerReader, "Raw Input hands PadForge no report of it while PadForge's reader sees it move"
                    + (w.Layout.Hidden ? RecorderReaders.HiddenFact : string.Empty), device);
            }
            return w.NoReportSaid;
        }

        /// <summary>A number that changes when PadForge's reading of a device
        /// changes.</summary>
        private static int ShapeOf(CustomInputState s)
        {
            var hash = new HashCode();
            foreach (int a in s.Axis) hash.Add(a);
            foreach (int a in s.Sliders) hash.Add(a);
            foreach (int p in s.Povs) hash.Add(p);
            foreach (bool b in s.Buttons) hash.Add(b);
            return hash.ToHashCode();
        }

        // ─────────────────────────────────────────────
        //  Raw Input, other programs, health
        // ─────────────────────────────────────────────

        private void WatchRawInput()
        {
            var listing = Host.RawInputListing();
            if (listing == null) return;
            foreach (var (device, listed) in listing)
            {
                bool known = _rawListed.TryGetValue(device, out bool was);
                _rawListed[device] = listed;
                if (known ? was == listed : listed) continue;
                Line(RecordingCodec.LayerReader, listed ? "is back in Raw Input" : "is missing from Raw Input", device);
            }
        }

        private void WatchPrograms()
        {
            string now = Host.ControllerProgramsNow();
            if (now == null) return;
            if (_programsSaid == null)
            {
                _programsSaid = now;
                return;
            }
            if (now == _programsSaid) return;
            _programsSaid = now;
            Line(RecordingCodec.LayerWindow, "other controller programs running now: " + now);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public int Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerStatus
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out PowerStatus status);

        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, int flags);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        // PROCESS_MEMORY_COUNTERS, as dotnet/runtime's Interop.GetProcessMemoryInfo.cs
        // declares it and Environment.WorkingSet reads it.
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint Cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

        private readonly int[] _healthCollections = new int[3];

        /// <summary>
        /// The health line every ten seconds: PadForge's memory, processor,
        /// handles, window and drawing objects and .NET collections, and the
        /// PC's free memory, load and power. Numbers only. Null the first
        /// time, which only takes the counts to measure from. PadForge's own
        /// numbers are read through the handle GetCurrentProcess gives, never
        /// through a Process object, whose memory, handle and thread counts
        /// copy every process and thread record of the PC. No call reads the
        /// thread count without that walk, so the line has none.
        /// </summary>
        internal string Health(long now)
        {
            IntPtr self = GetCurrentProcess();
            var counters = new MemoryCounters { Cb = (uint)Marshal.SizeOf<MemoryCounters>() };
            bool own = GetProcessMemoryInfo(self, ref counters, counters.Cb);
            var sb = new StringBuilder(200);
            sb.Append("health: PadForge ");
            if (own)
                sb.Append(Mb((long)(ulong)counters.WorkingSetSize)).Append(" MB in use, ").Append(Mb((long)(ulong)counters.PagefileUsage)).Append(" MB its own");
            else
                sb.Append("memory not read");
            long cpu = Environment.CpuUsage.TotalTime.Ticks;
            bool times = GetSystemTimes(out long idle, out long kernel, out long user);
            bool first = _healthCpu < 0;
            if (!first && times)
            {
                double wall = (double)(now - _healthTicks) / Stopwatch.Frequency * 1e7 * Environment.ProcessorCount;
                long total = kernel + user - _healthBusy;
                long idleDelta = idle - _healthIdle;
                if (wall > 0) sb.Append(", processor ").Append(Percent((cpu - _healthCpu) / wall));
                if (total > 0) sb.Append(", PC processors ").Append(Percent((total - idleDelta) / (double)total)).Append(" busy");
            }
            _healthCpu = cpu;
            if (times)
            {
                _healthBusy = kernel + user;
                _healthIdle = idle;
            }
            _healthTicks = now;
            if (GetProcessHandleCount(self, out uint handles)) sb.Append(", ").Append(handles.ToString(CultureInfo.InvariantCulture)).Append(" handles");
            sb.Append(", ").Append(GetGuiResources(self, 1).ToString(CultureInfo.InvariantCulture)).Append(" window objects, ")
              .Append(GetGuiResources(self, 0).ToString(CultureInfo.InvariantCulture)).Append(" drawing objects, .NET collections ");
            for (int g = 0; g < 3; g++)
            {
                int count = GC.CollectionCount(g);
                if (g > 0) sb.Append('/');
                sb.Append((count - _healthCollections[g]).ToString(CultureInfo.InvariantCulture));
                _healthCollections[g] = count;
            }
            var memory = new MemoryStatus { Length = Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
                sb.Append(", PC memory ").Append(Gb(memory.AvailPhys)).Append(" of ").Append(Gb(memory.TotalPhys)).Append(" GB free");
            if (GetSystemPowerStatus(out var power))
                sb.Append(power.ACLineStatus == 1 ? ", on mains"
                    : power.ACLineStatus == 0 ? ", on battery" + (power.BatteryLifePercent <= 100 ? " " + power.BatteryLifePercent.ToString(CultureInfo.InvariantCulture) + " %" : string.Empty)
                    : ", power source unknown");
            return first ? null : sb.ToString();
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture);
        private static string Gb(ulong bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture);
        private static string Percent(double share) => Math.Max(0, Math.Min(100, share * 100)).ToString("0", CultureInfo.InvariantCulture) + " %";

        // ─────────────────────────────────────────────
        //  The window's thread: settings changed, how devices are held
        // ─────────────────────────────────────────────

        private Dictionary<string, string> _settingsSaid;
        private readonly Dictionary<UserDevice, string> _heldSaid = new();
        private long _settingsAt;

        /// <summary>Once a second on the window's thread, where the settings
        /// live: every setting changed since the last look, and a device whose
        /// hold changed (shared, exclusive, hidden).</summary>
        private void WatchSettings()
        {
            var capture = _capture;
            if (capture == null) return;
            long now = Environment.TickCount64;
            if (now - _settingsAt < 1000) return;
            _settingsAt = now;
            if (_restateSettings)
            {
                // A new file: what stands now is its start, so a change made
                // between two recordings is never said as made in the second.
                _restateSettings = false;
                _settingsSaid = null;
                _heldSaid.Clear();
            }
            if ((capture.Parts & RecordingParts.SettingsAndDevices) != 0)
            {
                Dictionary<string, string> state;
                try { state = Host.SettingsState(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException) { state = null; }
                if (state != null)
                {
                    if (_settingsSaid != null)
                    {
                        foreach (var (name, value) in state)
                            if (!_settingsSaid.TryGetValue(name, out string was) || was != value)
                                TakeNote(RecordingCodec.LayerSettingChange, -1, null, "setting " + name + " = " + value);
                        foreach (var name in _settingsSaid.Keys)
                            if (!state.ContainsKey(name))
                                TakeNote(RecordingCodec.LayerSettingChange, -1, null, "setting " + name + " gone");
                    }
                    _settingsSaid = state;
                }
            }
            if ((capture.Parts & RecordingParts.Input) != 0)
            {
                foreach (var device in Host.Devices())
                {
                    string held = RecorderHost.HeldWord(device);
                    if (_heldSaid.TryGetValue(device, out string was) && was == held) continue;
                    bool first = was == null;
                    _heldSaid[device] = held;
                    if (!first) Line(RecordingCodec.LayerReader, "held " + held, device);
                }
            }
        }
    }
}
