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
            TakeNote(layer, slot, device, text() + (since > 0 ? " (and " + since.ToString(CultureInfo.InvariantCulture) + " times more since it was last said)" : string.Empty));
        }

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
                _owner.TakeRepeatedNote(RecordingCodec.LayerFeedback, -1, capture.DeviceOfTrack(capture.TrackOfLive(device)),
                    "failed " + what + " " + effectType.ToString(CultureInfo.InvariantCulture), () =>
                    {
                        string error = ErrorOfSdl();
                        return "force failed " + what + " a " + ForceTrace.KindWord(effectType) + " effect: "
                            + (string.IsNullOrEmpty(error) ? "no reason given" : error);
                    });
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
        /// file says the whole PC was held up.</summary>
        internal const int HeldUpMs = 600;

        /// <summary>How often the health line, the other programs and Raw
        /// Input's list come. A test shortens it.</summary>
        internal TimeSpan HealthEvery = TimeSpan.FromSeconds(10);

        private long _stallSince, _stallSaid, _noPassSince, _noPassSaid, _healthAt;
        private readonly long[] _callSaid = new long[RecordingCapture.CallSlots], _callSaidFor = new long[RecordingCapture.CallSlots];
        private string _shownSaid, _programsSaid;
        private readonly XInputRead[] _reads = new XInputRead[XInputReadBack.Places];
        private readonly bool[] _readKnown = new bool[XInputReadBack.Places];
        private readonly int[] _placeOf = NewPlaces();
        private readonly Dictionary<UserDevice, bool> _rawListed = new();
        private readonly Dictionary<UserDevice, (long Since, bool Said, string Text)> _disagree = new();
        private long _healthCpu = -1, _healthBusy = -1, _healthIdle = -1, _healthTicks;

        private static int[] NewPlaces()
        {
            var places = new int[Common.Input.InputManager.MaxPads];
            Array.Fill(places, -1);
            return places;
        }

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
            Array.Clear(_refusedSaid);
            _motorsNamed.Clear();
            Array.Clear(_placeCheckedAt);
            Array.Fill(_placeOf, -1);
            _rawListed.Clear();
            _disagree.Clear();
            Interlocked.Exchange(ref _exceptions, 0);
            Interlocked.Exchange(ref _exceptionsLeftOut, 0);
            lock (_repeats) _repeats.Clear();
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
        /// when it meant to. Late by much, every program was held up.</summary>
        private void Woke(long lateMs)
        {
            var capture = _capture;
            if (capture == null || lateMs < HeldUpMs || (capture.Parts & RecordingParts.Timing) == 0) return;
            Line(RecordingCodec.LayerTiming, "the recording itself woke " + S(lateMs / 1000.0) + " late: the whole PC was held up");
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

        /// <summary>Names the axes each force-feedback device's motors act on
        /// that this file has not named yet, once a read made on a thread of
        /// its own kept them: a device the header could not name, and one
        /// first connected while recording (#600). A product not yet read is
        /// asked for, and one DirectInput did not list is let be.</summary>
        private void WatchMotors(RecordingCapture capture)
        {
            int count = capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var device = capture.DeviceOfTrack(i);
                if (device == null || !device.IsOnline || _motorsNamed.Contains(device) || !RecorderHost.HasMotors(device)) continue;
                var names = Host.KeptMotorAxisNames(device);
                if (names != null && names.Count > 0)
                {
                    _motorsNamed.Add(device);
                    Line(RecordingCodec.LayerFeedback, "motors act on " + ForceActuators.Join(names), device);
                }
                else if (!ForceActuators.Missed(device.VendorId, device.ProdId))
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
            // The places an XInput pad PadForge reads holds: a frame read
            // back there is that pad's own, never a virtual controller's.
            int padPlaces = 0;
            int count = capture.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var device = capture.DeviceOfTrack(i);
                if (device != null && device.IsOnline && XInputSlotIdentity.TryParseSlot(device.DevicePath, out int held) && held < 32)
                    padPlaces |= 1 << held;
            }
            for (int place = 0; place < XInputReadBack.Places; place++)
            {
                // An empty place, whose read is Windows' slow path, is asked
                // once a second.
                if (_readKnown[place] && !_reads[place].Connected && tick - _placeCheckedAt[place] < 1000) continue;
                _placeCheckedAt[place] = tick;
                var read = XInputReadBack.Read(place);
                if ((parts & RecordingParts.Output) != 0)
                {
                    if (!_readKnown[place] || !read.Equals(_reads[place]))
                    {
                        bool said = _readKnown[place];
                        _readKnown[place] = true;
                        _reads[place] = read;
                        if (said || read.Connected)
                            Line(RecordingCodec.LayerReadBack, "place=" + (place + 1).ToString(CultureInfo.InvariantCulture) + " " + XInputReadBack.Tokens(read));
                    }
                    if (live != null && (padPlaces & (1 << place)) == 0)
                        for (int slot = 0; slot < live.Controllers.Length; slot++)
                        {
                            if (_placeOf[slot] == place || !XInputReadBack.Matches(read, live.Controllers[slot])) continue;
                            _placeOf[slot] = place;
                            Line(RecordingCodec.LayerReadBack, "reads at XInput place " + (place + 1).ToString(CultureInfo.InvariantCulture), slot: slot);
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
                // SDL reads a pad at an XInput place through XInput too, a
                // little earlier than this read. A difference that holds the
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
                        Line(RecordingCodec.LayerReader, "XInput place " + (place + 1).ToString(CultureInfo.InvariantCulture) + " reads " + difference, device);
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

        private readonly int[] _healthCollections = new int[3];

        /// <summary>
        /// The health line every ten seconds: PadForge's memory, processor,
        /// handles, window and drawing objects, threads and .NET collections,
        /// and the PC's free memory, load and power. Numbers only. Null the
        /// first time, which only takes the counts to measure from.
        /// </summary>
        internal string Health(long now)
        {
            using var process = Process.GetCurrentProcess();
            var sb = new StringBuilder(200);
            sb.Append("health: PadForge ").Append(Mb(process.WorkingSet64)).Append(" MB in use, ").Append(Mb(process.PrivateMemorySize64)).Append(" MB its own");
            long cpu = process.TotalProcessorTime.Ticks;
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
            sb.Append(", ").Append(process.HandleCount.ToString(CultureInfo.InvariantCulture)).Append(" handles, ")
              .Append(GetGuiResources(GetCurrentProcess(), 1).ToString(CultureInfo.InvariantCulture)).Append(" window objects, ")
              .Append(GetGuiResources(GetCurrentProcess(), 0).ToString(CultureInfo.InvariantCulture)).Append(" drawing objects, ")
              .Append(process.Threads.Count.ToString(CultureInfo.InvariantCulture)).Append(" threads, .NET collections ");
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
            if ((_parts & RecordingParts.SettingsAndDevices) != 0)
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
            if ((_parts & RecordingParts.Input) != 0)
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
