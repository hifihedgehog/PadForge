using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// The HID channel a Bliss-Box port's API runs over (issue #469). The App
    /// implements it over the analog keyboards' overlapped channel, and tests
    /// over a scripted adapter.
    /// </summary>
    public interface IBlissBoxTransport
    {
        /// <summary>Writes a feature report, report ID first, padded to the
        /// collection's feature report length. False when the write failed.</summary>
        bool SetFeature(byte[] report);

        /// <summary>Reads the feature report whose ID is in byte 0 of
        /// <paramref name="buffer"/> into it. The byte count, or -1.</summary>
        int GetFeature(byte[] buffer);

        /// <summary>The collection's feature report length, report ID byte
        /// included.</summary>
        int FeatureLength { get; }
    }

    /// <summary>
    /// One port's API work (issue #469): the polls, the motors, the VMU
    /// picture and the native-channel jobs, run one at a time by the port's
    /// worker through <see cref="Step"/>, as BBAPI.cs serializes every
    /// control transfer behind its CT_IO flag. Other threads only set what
    /// they want and read what was last seen.
    ///
    /// <para>The info and pressure polls run no faster than DeviceBuddy's
    /// (report 17 every 500 ms, report 21 every 50 ms for a DualShock 2),
    /// because BBAPI.cs warns that the adapter skips a controller poll for
    /// each control transfer. The pressure poll runs only while a DualShock 2
    /// is in the port. The native arrow poll is faster, and runs only while
    /// the port's choice asks for it and the adapter does not send the arrows
    /// itself.</para>
    /// </summary>
    public sealed class BlissBoxSession
    {
        public const int InfoIntervalMs = 500;
        public const int PressureIntervalMs = 50;

        /// <summary>A running motor is told again this often, the API Tool's
        /// own way of holding rumble on (rumble.cs, its 100 ms timer).</summary>
        public const int RumbleRefreshMs = 100;

        /// <summary>The VMU picture is rewritten at most once a second. Each
        /// write lands in the adapter's EEPROM (GPA 4.86 report 20 at 0x2D58
        /// runs eeprom_update_byte over 0x00A0 to 0x015F, and 3.0 writes the
        /// same range from command 0x24), which the ATmega32U4 rates at
        /// 100,000 writes a cell.</summary>
        public const int ScreenIntervalMs = 1000;

        /// <summary>The native poll of a 3.x PlayStation digital pad runs
        /// back to back with this much room for the rest.</summary>
        public const int NativeArrowsIntervalMs = 16;

        /// <summary>A native reply is asked for up to 20 times, 20 ms apart,
        /// as BBAPI.cs getData does.</summary>
        public const int ReplyAttempts = 20;
        public const int ReplyDelayMs = 20;

        private readonly IBlissBoxTransport _transport;
        private readonly Func<long> _clock;
        private readonly Action<int> _sleep;
        private readonly ConcurrentQueue<BlissBoxJob> _jobs = new();

        private volatile BlissBoxInfo _info;
        private volatile BlissBoxInfo _knownInfo;
        private volatile byte[] _pressure;
        private volatile byte[] _storedScreen;
        private volatile byte[] _wantedScreen;
        private volatile bool _nativeArrows;
        private volatile bool _arrowsLatched;
        private volatile int _arrows = -1;
        private int _wantedLarge, _wantedSmall;
        private volatile byte _sentLarge, _sentSmall;
        private long _lastLarge, _lastSmall;
        private long _lastMotorBurst = long.MinValue / 2;
        private bool _resendLarge, _resendSmall;
        private bool _largeFailed, _smallFailed;
        private long _nextInfo, _nextPressure, _nextArrows;
        private long _lastScreenWrite = long.MinValue / 2;
        private int _failedInfoReads;
        private volatile bool _stopRequested;
        private volatile bool _jobRunning;

        public BlissBoxSession(IBlissBoxTransport transport, int player, Func<long> clock = null, Action<int> sleep = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Player = player;
            _clock = clock ?? (() => Environment.TickCount64);
            _sleep = sleep ?? Thread.Sleep;
        }

        /// <summary>The port's player number, 1 to 4, from its product ID.</summary>
        public int Player { get; }

        /// <summary>Report 17 as last read, or null before the first read.</summary>
        public BlissBoxInfo Info => _info;

        /// <summary>A controller is in the port and identified: the info is
        /// known and the adapter is not searching.</summary>
        public BlissBoxInfo LiveInfo => _info is { Searching: false } info ? info : null;

        /// <summary>The last controller identified in the port, kept through a
        /// reopen and while the adapter searches, for the rest rule, which
        /// must not flicker while report 17 is unknown. Null until one is
        /// identified.</summary>
        public BlissBoxInfo KnownInfo => _knownInfo;

        /// <summary>The twelve pressure bytes, or null when no DualShock 2 is
        /// in the port.</summary>
        public byte[] Pressure => _pressure;

        /// <summary>The picture the adapter holds, in wire order, read once
        /// a Dreamcast pad is in the port and kept current as PadForge writes.</summary>
        public byte[] StoredScreen => _storedScreen;

        /// <summary>The four directions from the native poll (bit 0 up, 1
        /// down, 2 left, 3 right), or -1 while it is not running.</summary>
        public int Arrows => _arrows;

        /// <summary>Consecutive failed info reads, for the host's reopen.</summary>
        public int FailedInfoReads => _failedInfoReads;

        /// <summary>Raised on the worker when report 17 changes.</summary>
        public event Action<BlissBoxSession> InfoChanged;

        /// <summary>Whether a 3.x PlayStation digital pad is polled through
        /// the native channel for its four directions.</summary>
        public bool NativeArrows
        {
            get => _nativeArrows;
            set => _nativeArrows = value;
        }

        /// <summary>True while the native poll runs: asked for, a 3.x adapter
        /// reads a PlayStation digital pad, and the adapter does not send the
        /// arrows itself yet (<see cref="ArrowsLatched"/>). GPA publishes the
        /// four directions itself, and 2.x frames its native channel
        /// differently (<see cref="BlissBoxControllers.NativeChannelMajor"/>).</summary>
        public bool NativeArrowsActive
            => _nativeArrows && !_arrowsLatched && LiveInfo is { Major: 3 } info
               && BlissBoxControllers.IsPlayStationDigital(info.Type);

        /// <summary>The adapter sends the four arrows itself: its latch saw
        /// opposite directions (3.0 0x3295 to 0x32A9), so the native poll has
        /// nothing left to add and stops. Each of its requests costs the
        /// adapter polls of its own: the message's write and each read of the
        /// answer set the flag that skips one (0x090B, 0x07D6), and the
        /// exchange waits for a pass without it (0x30D9). Set by the merge,
        /// which sees the latch's buttons in the port's report, and cleared
        /// when report 17 shows another controller or a search, since the
        /// firmware clears the latch when it detects a controller (0x3163),
        /// or when the channel drops.</summary>
        public bool ArrowsLatched
        {
            get => _arrowsLatched;
            set => _arrowsLatched = value;
        }

        /// <summary>A job is queued or running, so the port's channel is
        /// spoken for until it ends. The queue is read first: the worker marks
        /// a job running before it takes it off the queue, so one of the two
        /// always shows it.</summary>
        public bool Busy => !_jobs.IsEmpty || _jobRunning;

        /// <summary>Nothing is left to send the motors: both were last told
        /// to stop or never started, the controller in the port has none, or
        /// the adapter is searching, with no controller in the port to stop.
        /// Until report 17 is read again after a reopen, a motor that ran
        /// before the channel dropped is not at rest: the adapter may still
        /// run it, and neither firmware ends every rumble on its own. The 3.0
        /// Dreamcast driver never counts down the loop of 0xFF a type-1
        /// command sets (0x0A89 to 0x0A97, 0x2858 to 0x285F), and a GPA's one
        /// timer stops only the command that last took it (0x29F7 to
        /// 0x2A14).</summary>
        public bool MotorsAtRest
            => _info switch
            {
                { Searching: true } => true,
                { } info when BlissBoxControllers.MotorCount(info.Type) == 0 => true,
                _ => _sentLarge == 0 && _sentSmall == 0
                     && !Volatile.Read(ref _resendLarge) && !Volatile.Read(ref _resendSmall),
            };

        /// <summary>The levels the motors should run at, PadForge's 0 to
        /// 65535: large is the low-frequency channel, small the high. True
        /// when either level changed, so the caller wakes the worker only
        /// then.</summary>
        public bool SetRumble(ushort large, ushort small)
        {
            int oldLarge = Interlocked.Exchange(ref _wantedLarge, large);
            int oldSmall = Interlocked.Exchange(ref _wantedSmall, small);
            return oldLarge != large || oldSmall != small;
        }

        /// <summary>The picture to show, in wire order, or null to leave the
        /// adapter's own. True when it differs from the one asked for before,
        /// so the caller wakes the worker only then.</summary>
        public bool SetScreen(byte[] wire)
        {
            if (wire != null && wire.Length != BlissBoxProtocol.ScreenBytes)
                throw new ArgumentException($"A VMU picture is {BlissBoxProtocol.ScreenBytes} bytes.", nameof(wire));
            var old = _wantedScreen;
            bool changed = wire == null ? old != null : old == null || !wire.AsSpan().SequenceEqual(old);
            if (changed) _wantedScreen = wire == null ? null : (byte[])wire.Clone();
            return changed;
        }

        /// <summary>From any thread: both motors are told their level again
        /// on the next step, as after a reopen. For the moment SDL's rumble
        /// leaves the port: on a GPA, DirectInput's effect block can reach a
        /// one-motor pad's command-5 routine (0x2E8C), whose rumble the
        /// session then clears.</summary>
        public void ResendMotors()
        {
            Volatile.Write(ref _resendLarge, true);
            Volatile.Write(ref _resendSmall, true);
        }

        /// <summary>Queues a job for the worker. A session whose port is
        /// closing ends it as closed at once: the worker ends the jobs it
        /// finds as it leaves, and one queued after that would never
        /// finish.</summary>
        public void Enqueue(BlissBoxJob job)
        {
            _jobs.Enqueue(job ?? throw new ArgumentNullException(nameof(job)));
            if (_stopRequested) CancelJobs();
        }

        /// <summary>The motor strength byte for a level: 0 off, else 1 to
        /// 255. Type 1 reads a strength of 0 as full, so a small nonzero level
        /// rounds up rather than down to it.</summary>
        public static byte Strength(int level)
            => level <= 0 ? (byte)0 : (byte)Math.Clamp((level + 255) >> 8, 1, 255);

        /// <summary>Does whatever is due, and returns how many milliseconds
        /// the worker may wait before the next call.</summary>
        public int Step()
        {
            long now = _clock();
            if (now >= _nextInfo)
            {
                PollInfo();
                _nextInfo = now + InfoIntervalMs;
            }

            var info = LiveInfo;
            if (info != null && BlissBoxControllers.HasPressure(info.Type))
            {
                if (now >= _nextPressure)
                {
                    PollPressure();
                    _nextPressure = now + PressureIntervalMs;
                }
            }
            else _pressure = null;

            WriteMotors(now);

            // On a GPA a picture write disturbs the motors (AfterScreenWrite),
            // which a second pass puts right in the same step.
            if (info != null && BlissBoxControllers.HasScreen(info.Type))
            {
                if (_storedScreen == null) ReadScreen();
                if (WriteScreen(now)) WriteMotors(now);
            }

            if (!_jobs.IsEmpty)
            {
                // Running before the job leaves the queue, so Busy never
                // reads false between the two.
                _jobRunning = true;
                try
                {
                    if (_jobs.TryDequeue(out var job)) job.Run(this);
                }
                finally { _jobRunning = false; }
            }

            if (NativeArrowsActive)
            {
                if (_clock() >= _nextArrows)
                {
                    PollArrows();
                    _nextArrows = _clock() + NativeArrowsIntervalMs;
                }
            }
            else _arrows = -1;

            now = _clock();
            long next = _nextInfo;
            if (info != null && BlissBoxControllers.HasPressure(info.Type)) next = Math.Min(next, _nextPressure);
            // On a GPA a running motor is due its refresh, and a level not yet
            // delivered its retry, 100 ms after the last attempt, and a level
            // queued during this step, by a job's picture write, goes out at
            // once unless its last attempt failed. On 3.x the motors wait for
            // the next of their paced writes.
            var (wantLarge, wantSmall, motors) = WantedStrengths();
            bool resendLarge = Volatile.Read(ref _resendLarge), resendSmall = Volatile.Read(ref _resendSmall);
            if (motors > 0 && _info is { IsAdvanced: true })
            {
                next = Math.Min(next, MotorWake(wantLarge, _sentLarge, _lastLarge, _largeFailed, resendLarge, now));
                if (motors == 2)
                    next = Math.Min(next, MotorWake(wantSmall, _sentSmall, _lastSmall, _smallFailed, resendSmall, now));
            }
            else if (motors > 0 && (MotorOwed(wantLarge, _sentLarge, resendLarge)
                                    || (motors == 2 && MotorOwed(wantSmall, _sentSmall, resendSmall))))
                next = Math.Min(next, Math.Max(now, _lastMotorBurst + RumbleRefreshMs));
            if (NativeArrowsActive) next = Math.Min(next, _nextArrows);
            // Only a port whose pad draws the picture writes one, so only
            // then is a pending picture a reason to wake.
            if (info != null && BlissBoxControllers.HasScreen(info.Type) && ScreenWritePending)
                next = Math.Min(next, Math.Max(now, _lastScreenWrite + ScreenIntervalMs));
            if (!_jobs.IsEmpty) next = now;
            return (int)Math.Clamp(next - now, 0, InfoIntervalMs);
        }

        /// <summary>Stops both motors now, whatever was sent before: the
        /// last write before the port closes.</summary>
        public void StopMotors()
        {
            SetRumble(0, 0);
            _transport.SetFeature(BlissBoxProtocol.Rumble(true, 0));
            _transport.SetFeature(BlissBoxProtocol.Rumble(false, 0));
            _sentLarge = _sentSmall = 0;
            _largeFailed = _smallFailed = false;
            Volatile.Write(ref _resendLarge, false);
            Volatile.Write(ref _resendSmall, false);
        }

        /// <summary>From any thread: a running job stops before its next
        /// message to the controller, so a closing port never waits out a
        /// Controller Pak's retries.</summary>
        public void RequestStop() => _stopRequested = true;

        /// <summary>For a job: writes a picture now, once the EEPROM guard's
        /// second since the last write has passed, whether or not a pad that
        /// draws it is in the port. False when the adapter refused it.</summary>
        internal bool WriteScreenNow(byte[] wire)
        {
            if (_storedScreen is { } stored && stored.AsSpan().SequenceEqual(wire)) return true;
            long wait = _lastScreenWrite + ScreenIntervalMs - _clock();
            if (wait > 0) _sleep((int)Math.Min(wait, ScreenIntervalMs));
            // A port that started closing during the wait sends nothing more.
            if (_stopRequested) return false;
            _lastScreenWrite = _clock();
            bool written = _transport.SetFeature(BlissBoxProtocol.Screen(wire));
            AfterScreenWrite();
            if (!written) return false;
            _storedScreen = (byte[])wire.Clone();
            return true;
        }

        /// <summary>How long the player job waits after its command before it
        /// asks whether the adapter is still there.</summary>
        internal const int PlayerSettleMs = 100;

        /// <summary>Reads of report 17 after a player command.</summary>
        internal const int PlayerChecks = 3;

        /// <summary>For the player job: true when the adapter still answers
        /// report 17 as this port's player after the command. Both firmwares
        /// reset from inside the command's handler, so an adapter that took it
        /// never answers on this handle again. Three reads, PlayerSettleMs
        /// apart, so one read that fails on a port that kept its number is
        /// not taken for the reset.</summary>
        internal bool StillAnswers()
        {
            for (int check = 0; check < PlayerChecks; check++)
            {
                _sleep(PlayerSettleMs);
                if (BlissBoxProtocol.ParseInfo(Get(BlissBoxProtocol.ReportInfo), Player) != null) return true;
            }
            return false;
        }

        public bool StopRequested => _stopRequested;

        /// <summary>Ends every queued job as closed.</summary>
        public void CancelJobs()
        {
            while (_jobs.TryDequeue(out var job)) job.Cancel();
        }

        /// <summary>Forgets everything read from the adapter, for a closed
        /// channel. A job still queued ends as closed, since it was asked of
        /// the controller that was in the port before the channel dropped.</summary>
        public void Forget()
        {
            bool had = _info != null;
            _info = null;
            _pressure = null;
            _storedScreen = null;
            _arrows = -1;
            _arrowsLatched = false;
            _sentLarge = _sentSmall = 0;
            // What the motors are doing is unknown once the channel dropped:
            // an adapter that stayed up may still run the last level. Both
            // are told their level again when it reopens, a stop included.
            Volatile.Write(ref _resendLarge, true);
            Volatile.Write(ref _resendSmall, true);
            _nextInfo = _nextPressure = _nextArrows = 0;
            _failedInfoReads = 0;
            CancelJobs();
            if (had) InfoChanged?.Invoke(this);
        }

        private byte[] NewBuffer(byte reportId)
        {
            var buffer = new byte[Math.Max(_transport.FeatureLength, BlissBoxProtocol.ScreenReportLength)];
            buffer[0] = reportId;
            return buffer;
        }

        private ReadOnlySpan<byte> Get(byte reportId)
        {
            var buffer = NewBuffer(reportId);
            int n = _transport.GetFeature(buffer);
            return n <= 0 ? ReadOnlySpan<byte>.Empty : buffer.AsSpan(0, Math.Min(n, buffer.Length));
        }

        private void PollInfo()
        {
            var data = Get(BlissBoxProtocol.ReportInfo);
            if (data.IsEmpty)
            {
                _failedInfoReads++;
                return;
            }
            _failedInfoReads = 0;
            // A read that answers for another player is thrown away, the
            // check BBAPI.cs makes on every read.
            var info = BlissBoxProtocol.ParseInfo(data, Player);
            if (info == null || info.Equals(_info)) return;
            var old = _info;
            _info = info;
            if (!info.Searching) _knownInfo = info;
            if (old == null || old.Type != info.Type || old.Searching != info.Searching)
            {
                _pressure = null;
                _arrows = -1;
                _arrowsLatched = false;
                // Another controller, or the same one back: its motors are
                // told their level again, a stop included, as after a reopen.
                Volatile.Write(ref _resendLarge, true);
                Volatile.Write(ref _resendSmall, true);
            }
            InfoChanged?.Invoke(this);
        }

        private void PollPressure()
        {
            var data = Get(BlissBoxProtocol.ReportPressure);
            var pressure = new byte[BlissBoxProtocol.PressureCount];
            if (BlissBoxProtocol.TryParsePressure(data, Player, pressure)) _pressure = pressure;
        }

        private void ReadScreen()
        {
            var data = Get(BlissBoxProtocol.ReportScreenRead);
            var wire = new byte[BlissBoxProtocol.ScreenBytes];
            if (BlissBoxProtocol.TryParseScreen(data, Player, wire)) _storedScreen = wire;
        }

        /// <summary>A picture is wanted that the adapter does not hold yet.</summary>
        private bool ScreenWritePending
            => _wantedScreen is { } wanted && _storedScreen is { } stored && !wanted.AsSpan().SequenceEqual(stored);

        /// <summary>Writes the picture when it differs from the one the
        /// adapter holds, and no sooner than a second after the last write.
        /// An unchanged picture is never sent at all, on top of
        /// eeprom_update_byte skipping the bytes that match. True when a write
        /// went out.</summary>
        private bool WriteScreen(long now)
        {
            if (!ScreenWritePending) return false;
            var wanted = _wantedScreen;
            if (wanted == null) return false;
            if (now - _lastScreenWrite < ScreenIntervalMs) return false;
            _lastScreenWrite = now;
            if (_transport.SetFeature(BlissBoxProtocol.Screen(wanted))) _storedScreen = wanted;
            AfterScreenWrite();
            return true;
        }

        /// <summary>GPA 4.86 calls the controller driver's command-5 routine
        /// at full power for 10 ms before it handles any feature report but
        /// the motor and native commands (0x2BEF to 0x2BF9). On a Dreamcast
        /// pad that routine also forces the strength command 4 runs at, and
        /// takes the adapter's one timer from command 4's rumble (0x0C2A,
        /// 0x2A16), which would then run at full power with nothing to stop
        /// it. Both motors are told their level again in the same step, a
        /// one-motor pad's starting with a stop on command 5, which ends the
        /// pulse and gives command 4 its strength and its timer back. The 3.0
        /// firmware's screen command calls no motor routine (0x091E to
        /// 0x0935).</summary>
        private void AfterScreenWrite()
        {
            if (_info is { IsAdvanced: true }) ResendMotors();
        }

        /// <summary>The strength each motor of the controller in the port
        /// should run at, and how many it has
        /// (<see cref="BlissBoxControllers.MotorCount"/>): each level on its
        /// own motor for a pad with two, the stronger level on command 4 for
        /// a pad with one, nothing for a pad with none or no pad.</summary>
        private (byte Large, byte Small, int Motors) WantedStrengths()
        {
            int motors = LiveInfo is { } info ? BlissBoxControllers.MotorCount(info.Type) : 0;
            int large = Volatile.Read(ref _wantedLarge), small = Volatile.Read(ref _wantedSmall);
            return motors switch
            {
                2 => (Strength(large), Strength(small), 2),
                1 => (Strength(Math.Max(large, small)), (byte)0, 1),
                _ => ((byte)0, (byte)0, 0),
            };
        }

        /// <summary>A motor whose level changed is told, type 0 when it
        /// stops, and a running one again every 100 ms. An attempt that
        /// failed waits those 100 ms before the next, so an adapter that
        /// refuses writes is not asked again on every step. After a reopen, a
        /// controller change, a hand-off or a GPA picture write each motor is
        /// told its level once, whatever the other's write does. A controller
        /// with no motors is sent nothing, and the levels wait for one that
        /// has them.
        ///
        /// <para>A 3.x adapter pays a controller poll for every write: its
        /// write handler sets a flag (0x090B), and the main loop then sends
        /// the last report again instead of polling the pad (0x31C6, 0x31DD).
        /// Each pass sends the report as two interrupt transfers and waits
        /// for the host to take each one (0x35F5 to 0x364A), so a game that
        /// changes its rumble every frame would hold the pad's input still.
        /// There the motors are written together, no more than once every
        /// 100 ms, the rate the API Tool writes them at, and a change waits
        /// for the next of those writes. GPA 4.86 handles the write in its USB
        /// control path and polls on, so it gets a change at once.</para></summary>
        private void WriteMotors(long now)
        {
            var (large, small, motors) = WantedStrengths();
            if (motors == 0) return;
            bool advanced = _info is { IsAdvanced: true };
            bool resendLarge = Volatile.Read(ref _resendLarge), resendSmall = Volatile.Read(ref _resendSmall);
            if (!advanced)
            {
                if (now - _lastMotorBurst < RumbleRefreshMs) return;
                bool wrote = false;
                if (MotorOwed(large, _sentLarge, resendLarge))
                {
                    WriteLarge(large, motors, advanced, now);
                    wrote = true;
                }
                if (motors == 2 && MotorOwed(small, _sentSmall, resendSmall))
                {
                    WriteSmall(small, now);
                    wrote = true;
                }
                if (wrote) _lastMotorBurst = now;
            }
            else
            {
                if (MotorDue(large, _sentLarge, _lastLarge, _largeFailed, resendLarge, now))
                    WriteLarge(large, motors, advanced, now);
                if (motors == 2 && MotorDue(small, _sentSmall, _lastSmall, _smallFailed, resendSmall, now))
                    WriteSmall(small, now);
            }
            if (motors == 1)
            {
                // Command 5 never carries a level to a one-motor pad, only a
                // GPA's stop, so there is no second level to keep.
                _sentSmall = 0;
                _smallFailed = false;
                Volatile.Write(ref _resendSmall, false);
            }
        }

        /// <summary>Command 4. A resend request is cleared before the write,
        /// so one the hand-off makes while it is in flight is kept, and set
        /// again when the write fails.</summary>
        private void WriteLarge(byte level, int motors, bool advanced, long now)
        {
            bool resend = Volatile.Read(ref _resendLarge);
            if (resend) Volatile.Write(ref _resendLarge, false);
            bool cleared = true;
            // A one-motor pad on a GPA may have a command-5 rumble running
            // that PadForge never sent: the pulse a picture write starts, or
            // one DirectInput's effect block started through SDL (0x2E8C calls
            // the same routine). The pulse leaves command 5's loop byte set
            // (0x0C2A), which keeps power on the pack at every poll once
            // command 4 has taken the timer (0x0E0A to 0x0E24). A stop on
            // command 5 clears it, and command 4 right after sets the strength
            // that stop forced to full again (0x0C2C, 0x0E3D), so the level
            // counts as delivered only when both went through. On 3.x command
            // 5 is command 4's alias for these pads (0x10BC, 0x2942, 0x270A),
            // so it needs no clearing.
            if (resend && motors == 1 && advanced)
                cleared = _transport.SetFeature(BlissBoxProtocol.Rumble(false, 0));
            _lastLarge = now;
            bool sent = _transport.SetFeature(BlissBoxProtocol.Rumble(true, level));
            if (sent) _sentLarge = level;
            _largeFailed = !(sent && cleared);
            if (_largeFailed && resend) Volatile.Write(ref _resendLarge, true);
        }

        /// <summary>Command 5, with the same resend rule as command 4.</summary>
        private void WriteSmall(byte level, long now)
        {
            bool resend = Volatile.Read(ref _resendSmall);
            if (resend) Volatile.Write(ref _resendSmall, false);
            _lastSmall = now;
            bool sent = _transport.SetFeature(BlissBoxProtocol.Rumble(false, level));
            if (sent) _sentSmall = level;
            _smallFailed = !sent;
            if (!sent && resend) Volatile.Write(ref _resendSmall, true);
        }

        /// <summary>When a GPA motor next needs the worker: at once for a
        /// level not yet delivered, 100 ms after the last attempt for a
        /// refused one or a running motor's refresh, and never for one at
        /// rest.</summary>
        private static long MotorWake(byte wanted, byte sent, long last, bool failed, bool resend, long now)
        {
            bool pending = resend || wanted != sent;
            if (pending && !failed) return now;
            return pending || sent != 0 ? last + RumbleRefreshMs : long.MaxValue;
        }

        private static bool MotorDue(byte wanted, byte sent, long last, bool failed, bool resend, long now)
        {
            bool elapsed = now - last >= RumbleRefreshMs;
            if (wanted != sent || resend) return !failed || elapsed;
            return wanted != 0 && elapsed;
        }

        /// <summary>A 3.x motor that goes into the next write: its level
        /// changed or is owed again, or it is running and due its
        /// refresh.</summary>
        private static bool MotorOwed(byte wanted, byte sent, bool resend)
            => resend || wanted != sent || wanted != 0;

        private void PollArrows()
        {
            var reply = Talk(BlissBoxPsx.PollMessage());
            _arrows = reply != null && BlissBoxPsx.TryDecodeArrows(reply, out byte arrows) ? arrows : 0;
        }

        /// <summary>Sends a message down the native channel and returns the
        /// controller's answer, or null when there was none. The framing
        /// follows the firmware generation report 17 gave, so nothing is sent
        /// before it is known.</summary>
        public byte[] Talk(byte[] message)
        {
            var info = _info;
            if (info == null) return null;
            foreach (var report in BlissBoxProtocol.NativeReports(message, info.IsAdvanced))
                if (!_transport.SetFeature(report)) return null;
            for (int attempt = 0; attempt < ReplyAttempts; attempt++)
            {
                if (attempt > 0) _sleep(ReplyDelayMs);
                var data = Get(BlissBoxProtocol.ReportNative);
                if (data.IsEmpty) continue;
                var state = BlissBoxProtocol.ParseNativeReply(data, Player, BlissBoxProtocol.NativeUse, out var reply);
                if (state == BlissBoxProtocol.NativeReplyState.Ready) return reply;
                if (state == BlissBoxProtocol.NativeReplyState.NoReply) return null;
            }
            return null;
        }

        /// <summary>Sends one command report, for the jobs.</summary>
        internal bool Send(byte[] report) => _transport.SetFeature(report);
    }

    public enum BlissBoxJobError
    {
        None,
        /// <summary>The port closed first.</summary>
        Closed,
        /// <summary>The job needs another controller in the port.</summary>
        WrongController,
        /// <summary>The adapter took no command or the controller gave no
        /// answer.</summary>
        NoReply,
        NoPak,
        RumblePak,
        /// <summary>A block kept failing its CRC.</summary>
        BadBlock,
        /// <summary>A restore met more rejected writes than the API Tool
        /// allows.</summary>
        TooManyErrors,
        /// <summary>The adapter's firmware frames the native channel
        /// another way (<see cref="BlissBoxControllers.NativeChannelMajor"/>).</summary>
        OldFirmware,
        /// <summary>A player change did not take: the adapter refused its own
        /// picture back, or still answers as the old player.</summary>
        PlayerUnchanged,
    }

    public sealed class BlissBoxJobResult
    {
        private BlissBoxJobResult(BlissBoxJobError error, byte[] data, int block)
        {
            Error = error;
            Data = data;
            Block = block;
        }

        public bool Ok => Error == BlissBoxJobError.None;
        public BlissBoxJobError Error { get; }

        /// <summary>A backup's pak image.</summary>
        public byte[] Data { get; }

        /// <summary>The block a failure happened at, or -1.</summary>
        public int Block { get; }

        public static BlissBoxJobResult Done(byte[] data = null) => new(BlissBoxJobError.None, data, -1);
        public static BlissBoxJobResult Fail(BlissBoxJobError error, int block = -1) => new(error, null, block);
    }

    /// <summary>Work that holds the port's channel until it finishes: a
    /// player change or a Controller Pak transfer.</summary>
    public abstract class BlissBoxJob
    {
        private readonly TaskCompletionSource<BlissBoxJobResult> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BlissBoxJobResult> Completion => _done.Task;

        internal void Run(BlissBoxSession session)
        {
            BlissBoxJobResult result;
            try { result = Execute(session); }
            catch { result = BlissBoxJobResult.Fail(BlissBoxJobError.NoReply); }
            _done.TrySetResult(result);
        }

        internal void Cancel() => _done.TrySetResult(BlissBoxJobResult.Fail(BlissBoxJobError.Closed));

        protected abstract BlissBoxJobResult Execute(BlissBoxSession session);
    }

    /// <summary>Makes the port another player. The adapter stores the
    /// number, resets and comes back under that player's product ID
    /// (BBAPI.cs setPlayer, sent with reset 1 as the API Tool's player
    /// wizard sends it).
    ///
    /// <para>Both firmwares reset from inside the command's handler, before
    /// the transfer completes (3.0 detaches USB at 0x08EB, GPA 4.86 jumps to
    /// its reset at 0x2CE2), so a write Windows reports as failed can still
    /// have taken. BBAPI.cs sends it without checking. This asks report 17 a
    /// moment later instead: an adapter that reset answers nothing on the old
    /// handle, and one that still answers as this port's player never got
    /// the command.</para>
    ///
    /// <para>The port returns as a new device, with none of the old one's
    /// choices. A picture PadForge put on the VMU in place of the adapter's
    /// own goes back first, so the adapter never keeps PadForge's picture
    /// with no record left of its own. When that write is refused, the
    /// command is never sent. A port that is closing, or starts closing while
    /// the picture waits out the EEPROM guard, sends neither.</para></summary>
    public sealed class BlissBoxPlayerJob : BlissBoxJob
    {
        public BlissBoxPlayerJob(int player, byte[] restoreScreen = null)
        {
            if (player < 1 || player > BlissBoxProtocol.MaxPlayer) throw new ArgumentOutOfRangeException(nameof(player));
            Player = player;
            if (restoreScreen != null && restoreScreen.Length == BlissBoxProtocol.ScreenBytes)
                RestoreScreen = (byte[])restoreScreen.Clone();
        }

        public int Player { get; }

        /// <summary>The adapter's own picture to write back first, in wire
        /// order, or null.</summary>
        public byte[] RestoreScreen { get; }

        protected override BlissBoxJobResult Execute(BlissBoxSession session)
        {
            if (session.StopRequested) return BlissBoxJobResult.Fail(BlissBoxJobError.Closed);
            if (RestoreScreen != null)
            {
                bool restored = session.WriteScreenNow(RestoreScreen);
                if (session.StopRequested) return BlissBoxJobResult.Fail(BlissBoxJobError.Closed);
                if (!restored) return BlissBoxJobResult.Fail(BlissBoxJobError.PlayerUnchanged);
            }
            session.Send(BlissBoxProtocol.SetPlayer(Player));
            return session.StillAnswers()
                ? BlissBoxJobResult.Fail(BlissBoxJobError.PlayerUnchanged)
                : BlissBoxJobResult.Done();
        }
    }

    /// <summary>Shared by the Controller Pak jobs: firmware that frames the
    /// native channel as PadForge does, an N64 controller, a pak in it, and
    /// not a Rumble Pak.
    ///
    /// <para>A read the controller leaves unanswered comes back short, not
    /// empty, from a 3.x adapter: its Joybus routine returns early for a read
    /// whose reply is not 33 bytes (0x0FA8), leaving the message in the
    /// buffer, and report 22 reads its command byte, 2, as the size and
    /// answers with the two address bytes after it (0x07D3). A GPA answers
    /// with no bytes (0x2874). Either way a read without its 33 bytes counts
    /// as no answer.</para></summary>
    public abstract class BlissBoxPakJob : BlissBoxJob
    {
        private readonly IProgress<double> _progress;

        protected BlissBoxPakJob(IProgress<double> progress) => _progress = progress;

        protected void Report(int blocksDone) => _progress?.Report((double)blocksDone / BlissBoxControllerPak.Blocks);

        protected static BlissBoxJobError CheckPak(BlissBoxSession session)
        {
            if (session.LiveInfo is not { } info || !BlissBoxControllers.HasControllerPak(info.Type))
                return BlissBoxJobError.WrongController;
            if (info.Major < BlissBoxControllers.NativeChannelMajor) return BlissBoxJobError.OldFirmware;
            if (session.StopRequested) return BlissBoxJobError.Closed;
            var status = session.Talk(BlissBoxControllerPak.StatusMessage());
            if (status == null) return BlissBoxJobError.NoReply;
            if (!BlissBoxControllerPak.IsPakPresent(status)) return BlissBoxJobError.NoPak;
            if (session.StopRequested) return BlissBoxJobError.Closed;
            var first = session.Talk(BlissBoxControllerPak.ReadMessage(0));
            if (first == null || first.Length < BlissBoxControllerPak.BlockBytes + 1) return BlissBoxJobError.NoReply;
            return BlissBoxControllerPak.IsRumblePak(first) ? BlissBoxJobError.RumblePak : BlissBoxJobError.None;
        }
    }

    /// <summary>Reads the whole pak, 1024 blocks of 32 bytes, each block
    /// read up to three times while its CRC fails. A block that drew no full
    /// answer at all fails as no reply, not as a bad block.</summary>
    public sealed class BlissBoxPakBackupJob : BlissBoxPakJob
    {
        public BlissBoxPakBackupJob(IProgress<double> progress = null) : base(progress) { }

        protected override BlissBoxJobResult Execute(BlissBoxSession session)
        {
            var check = CheckPak(session);
            if (check != BlissBoxJobError.None) return BlissBoxJobResult.Fail(check);
            var image = new byte[BlissBoxControllerPak.PakBytes];
            for (int block = 0; block < BlissBoxControllerPak.Blocks; block++)
            {
                var result = BlissBoxControllerPak.BlockResult.Short;
                bool answered = false;
                for (int attempt = 0; attempt < BlissBoxControllerPak.ReadAttempts; attempt++)
                {
                    if (session.StopRequested) return BlissBoxJobResult.Fail(BlissBoxJobError.Closed, block);
                    var reply = session.Talk(BlissBoxControllerPak.ReadMessage(block));
                    if (reply == null) continue;
                    result = BlissBoxControllerPak.ParseRead(reply,
                        image.AsSpan(block * BlissBoxControllerPak.BlockBytes, BlissBoxControllerPak.BlockBytes));
                    if (result != BlissBoxControllerPak.BlockResult.Short) answered = true;
                    if (result is BlissBoxControllerPak.BlockResult.Ok or BlissBoxControllerPak.BlockResult.NoPak) break;
                }
                if (result == BlissBoxControllerPak.BlockResult.NoPak) return BlissBoxJobResult.Fail(BlissBoxJobError.NoPak, block);
                if (!answered) return BlissBoxJobResult.Fail(BlissBoxJobError.NoReply, block);
                if (result != BlissBoxControllerPak.BlockResult.Ok) return BlissBoxJobResult.Fail(BlissBoxJobError.BadBlock, block);
                Report(block + 1);
            }
            return BlissBoxJobResult.Done(image);
        }
    }

    /// <summary>Writes a whole pak image, block by block, each checked by
    /// the CRC the pak answers with. Rejected writes are retried until more
    /// than 16 have failed, the API Tool's limit. A write that drew no answer
    /// counts toward the limit too, and when it is the one that reaches it,
    /// the job fails as no reply.</summary>
    public sealed class BlissBoxPakRestoreJob : BlissBoxPakJob
    {
        private readonly byte[] _image;

        public BlissBoxPakRestoreJob(byte[] image, IProgress<double> progress = null) : base(progress)
        {
            if (image == null || image.Length != BlissBoxControllerPak.PakBytes)
                throw new ArgumentException($"A Controller Pak image is {BlissBoxControllerPak.PakBytes} bytes.", nameof(image));
            _image = (byte[])image.Clone();
        }

        protected override BlissBoxJobResult Execute(BlissBoxSession session)
        {
            var check = CheckPak(session);
            if (check != BlissBoxJobError.None) return BlissBoxJobResult.Fail(check);
            int errors = 0;
            for (int block = 0; block < BlissBoxControllerPak.Blocks; block++)
            {
                var data = _image.AsSpan(block * BlissBoxControllerPak.BlockBytes, BlissBoxControllerPak.BlockBytes);
                while (true)
                {
                    if (session.StopRequested) return BlissBoxJobResult.Fail(BlissBoxJobError.Closed, block);
                    var reply = session.Talk(BlissBoxControllerPak.WriteMessage(block, data));
                    var result = reply == null
                        ? BlissBoxControllerPak.BlockResult.Short
                        : BlissBoxControllerPak.ParseWrite(reply, data);
                    if (result == BlissBoxControllerPak.BlockResult.Ok) break;
                    if (result == BlissBoxControllerPak.BlockResult.NoPak) return BlissBoxJobResult.Fail(BlissBoxJobError.NoPak, block);
                    if (++errors > BlissBoxControllerPak.WriteErrorLimit)
                        return BlissBoxJobResult.Fail(
                            reply == null ? BlissBoxJobError.NoReply : BlissBoxJobError.TooManyErrors, block);
                }
                Report(block + 1);
            }
            return BlissBoxJobResult.Done();
        }
    }
}
