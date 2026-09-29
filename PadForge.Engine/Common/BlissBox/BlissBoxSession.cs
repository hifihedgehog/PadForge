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
    /// <para>Nothing polls faster than DeviceBuddy does (report 17 every
    /// 500 ms, report 21 every 50 ms for a DualShock 2), because BBAPI.cs
    /// warns that the adapter skips a controller poll for each control
    /// transfer. The pressure poll runs only while a DualShock 2 is in the
    /// port.</para>
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
        private volatile byte[] _pressure;
        private volatile byte[] _storedScreen;
        private volatile byte[] _wantedScreen;
        private volatile bool _nativeArrows;
        private volatile int _arrows = -1;
        private int _wantedLarge, _wantedSmall;
        private byte _sentLarge, _sentSmall;
        private long _lastLarge, _lastSmall;
        private long _nextInfo, _nextPressure, _nextArrows;
        private long _lastScreenWrite = long.MinValue / 2;
        private int _failedInfoReads;
        private volatile bool _stopRequested;

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

        /// <summary>True while the native poll runs: asked for, and a 3.x
        /// adapter reads a PlayStation digital pad. GPA publishes the four
        /// directions itself, and 2.x has no native channel (memManager.cs:
        /// "3.0 is required for this feature!").</summary>
        public bool NativeArrowsActive
            => _nativeArrows && LiveInfo is { Major: 3 } info && BlissBoxControllers.IsPlayStationDigital(info.Type);

        /// <summary>The levels the motors should run at, PadForge's 0 to
        /// 65535: large is the low-frequency channel, small the high.</summary>
        public void SetRumble(ushort large, ushort small)
        {
            Volatile.Write(ref _wantedLarge, large);
            Volatile.Write(ref _wantedSmall, small);
        }

        /// <summary>The picture to show, in wire order, or null to leave the
        /// adapter's own.</summary>
        public void SetScreen(byte[] wire)
        {
            if (wire != null && wire.Length != BlissBoxProtocol.ScreenBytes)
                throw new ArgumentException($"A VMU picture is {BlissBoxProtocol.ScreenBytes} bytes.", nameof(wire));
            _wantedScreen = wire == null ? null : (byte[])wire.Clone();
        }

        public void Enqueue(BlissBoxJob job) => _jobs.Enqueue(job ?? throw new ArgumentNullException(nameof(job)));

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

            if (info != null && BlissBoxControllers.HasScreen(info.Type))
            {
                if (_storedScreen == null) ReadScreen();
                WriteScreen(now);
            }

            if (_jobs.TryDequeue(out var job)) job.Run(this);

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
            if (_sentLarge != 0) next = Math.Min(next, _lastLarge + RumbleRefreshMs);
            if (_sentSmall != 0) next = Math.Min(next, _lastSmall + RumbleRefreshMs);
            if (NativeArrowsActive) next = Math.Min(next, _nextArrows);
            if (ScreenWritePending) next = Math.Min(next, Math.Max(now, _lastScreenWrite + ScreenIntervalMs));
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
        }

        /// <summary>From any thread: a running job stops at its next block,
        /// so a closing port never waits out a whole Controller Pak.</summary>
        public void RequestStop() => _stopRequested = true;

        public bool StopRequested => _stopRequested;

        /// <summary>Ends every queued job as closed.</summary>
        public void CancelJobs()
        {
            while (_jobs.TryDequeue(out var job)) job.Cancel();
        }

        /// <summary>Forgets everything read from the adapter, for a closed
        /// channel.</summary>
        public void Forget()
        {
            bool had = _info != null;
            _info = null;
            _pressure = null;
            _storedScreen = null;
            _arrows = -1;
            _sentLarge = _sentSmall = 0;
            _nextInfo = _nextPressure = _nextArrows = 0;
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
            if (old == null || old.Type != info.Type || old.Searching != info.Searching)
            {
                _pressure = null;
                _arrows = -1;
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
        /// eeprom_update_byte skipping the bytes that match.</summary>
        private void WriteScreen(long now)
        {
            if (!ScreenWritePending) return;
            var wanted = _wantedScreen;
            if (wanted == null) return;
            if (now - _lastScreenWrite < ScreenIntervalMs) return;
            _lastScreenWrite = now;
            if (_transport.SetFeature(BlissBoxProtocol.Screen(wanted))) _storedScreen = wanted;
        }

        /// <summary>A motor whose level changed is told at once, type 0 when
        /// it stops. A running one is told again every 100 ms.</summary>
        private void WriteMotors(long now)
        {
            byte large = Strength(Volatile.Read(ref _wantedLarge));
            byte small = Strength(Volatile.Read(ref _wantedSmall));
            if (large != _sentLarge || (large != 0 && now - _lastLarge >= RumbleRefreshMs))
            {
                if (_transport.SetFeature(BlissBoxProtocol.Rumble(true, large)))
                {
                    _sentLarge = large;
                    _lastLarge = now;
                }
            }
            if (small != _sentSmall || (small != 0 && now - _lastSmall >= RumbleRefreshMs))
            {
                if (_transport.SetFeature(BlissBoxProtocol.Rumble(false, small)))
                {
                    _sentSmall = small;
                    _lastSmall = now;
                }
            }
        }

        private void PollArrows()
        {
            var reply = Talk(BlissBoxPsx.PollMessage());
            _arrows = reply != null && BlissBoxPsx.TryDecodeArrows(reply, out byte arrows) ? arrows : 0;
        }

        /// <summary>Sends a message down the native channel and returns the
        /// controller's answer, or null when there was none.</summary>
        public byte[] Talk(byte[] message)
        {
            foreach (var report in BlissBoxProtocol.NativeReports(message))
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
    /// wizard sends it).</summary>
    public sealed class BlissBoxPlayerJob : BlissBoxJob
    {
        public BlissBoxPlayerJob(int player)
        {
            if (player < 1 || player > BlissBoxProtocol.MaxPlayer) throw new ArgumentOutOfRangeException(nameof(player));
            Player = player;
        }

        public int Player { get; }

        protected override BlissBoxJobResult Execute(BlissBoxSession session)
            => session.Send(BlissBoxProtocol.SetPlayer(Player))
                ? BlissBoxJobResult.Done()
                : BlissBoxJobResult.Fail(BlissBoxJobError.NoReply);
    }

    /// <summary>Shared by the Controller Pak jobs: an N64 controller, a pak
    /// in it, and not a Rumble Pak.</summary>
    public abstract class BlissBoxPakJob : BlissBoxJob
    {
        private readonly IProgress<double> _progress;

        protected BlissBoxPakJob(IProgress<double> progress) => _progress = progress;

        protected void Report(int blocksDone) => _progress?.Report((double)blocksDone / BlissBoxControllerPak.Blocks);

        protected static BlissBoxJobError CheckPak(BlissBoxSession session)
        {
            if (session.LiveInfo is not { } info || !BlissBoxControllers.HasControllerPak(info.Type))
                return BlissBoxJobError.WrongController;
            var status = session.Talk(BlissBoxControllerPak.StatusMessage());
            if (status == null) return BlissBoxJobError.NoReply;
            if (!BlissBoxControllerPak.IsPakPresent(status)) return BlissBoxJobError.NoPak;
            var first = session.Talk(BlissBoxControllerPak.ReadMessage(0));
            if (first == null) return BlissBoxJobError.NoReply;
            return BlissBoxControllerPak.IsRumblePak(first) ? BlissBoxJobError.RumblePak : BlissBoxJobError.None;
        }
    }

    /// <summary>Reads the whole pak, 1024 blocks of 32 bytes, each block
    /// retried up to three times on a bad CRC.</summary>
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
                if (session.StopRequested) return BlissBoxJobResult.Fail(BlissBoxJobError.Closed, block);
                var result = BlissBoxControllerPak.BlockResult.Short;
                for (int attempt = 0; attempt < BlissBoxControllerPak.ReadAttempts; attempt++)
                {
                    var reply = session.Talk(BlissBoxControllerPak.ReadMessage(block));
                    if (reply == null) continue;
                    result = BlissBoxControllerPak.ParseRead(reply,
                        image.AsSpan(block * BlissBoxControllerPak.BlockBytes, BlissBoxControllerPak.BlockBytes));
                    if (result is BlissBoxControllerPak.BlockResult.Ok or BlissBoxControllerPak.BlockResult.NoPak) break;
                }
                if (result == BlissBoxControllerPak.BlockResult.NoPak) return BlissBoxJobResult.Fail(BlissBoxJobError.NoPak, block);
                if (result != BlissBoxControllerPak.BlockResult.Ok) return BlissBoxJobResult.Fail(BlissBoxJobError.BadBlock, block);
                Report(block + 1);
            }
            return BlissBoxJobResult.Done(image);
        }
    }

    /// <summary>Writes a whole pak image, block by block, each checked by
    /// the CRC the pak answers with. Rejected writes are retried until more
    /// than 16 have failed, the API Tool's limit.</summary>
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
                if (session.StopRequested) return BlissBoxJobResult.Fail(BlissBoxJobError.Closed, block);
                var data = _image.AsSpan(block * BlissBoxControllerPak.BlockBytes, BlissBoxControllerPak.BlockBytes);
                while (true)
                {
                    var reply = session.Talk(BlissBoxControllerPak.WriteMessage(block, data));
                    var result = reply == null
                        ? BlissBoxControllerPak.BlockResult.Short
                        : BlissBoxControllerPak.ParseWrite(reply, data);
                    if (result == BlissBoxControllerPak.BlockResult.Ok) break;
                    if (result == BlissBoxControllerPak.BlockResult.NoPak) return BlissBoxJobResult.Fail(BlissBoxJobError.NoPak, block);
                    if (++errors > BlissBoxControllerPak.WriteErrorLimit)
                        return BlissBoxJobResult.Fail(BlissBoxJobError.TooManyErrors, block);
                }
                Report(block + 1);
            }
            return BlissBoxJobResult.Done();
        }
    }
}
