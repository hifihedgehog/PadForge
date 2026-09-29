using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Common.BlissBox;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Bliss-Box port's session (issue #469) against a scripted adapter:
    /// the poll cadence, the motor writer's timing, the EEPROM guard, the
    /// native arrow poll and the Controller Pak transfers.
    /// </summary>
    public class BlissBoxSessionTests
    {
        /// <summary>An adapter answering feature reports the way the firmware
        /// does, with a controller behind its native channel.</summary>
        private sealed class ScriptedAdapter : IBlissBoxTransport
        {
            public int Player = 1;
            public bool Gone;
            public byte Type = 19, Flags, Major = 4, Minor = 86;
            public byte[] Pressure = new byte[12];
            public byte[] Stored = Enumerable.Repeat((byte)0xFF, 192).ToArray();
            public readonly List<byte[]> Sent = new();
            public int InfoReads, PressureReads, ScreenReads;
            public Func<byte[], byte[]> Controller;
            private byte[] _buffer;
            private int _size, _last;
            private bool _positioned;
            private byte[] _reply;

            public int FeatureLength => 200;

            public bool SetFeature(byte[] report)
            {
                Sent.Add((byte[])report.Clone());
                if (report[0] == BlissBoxProtocol.ReportScreen)
                {
                    Array.Copy(report, 4, Stored, 0, 192);
                    return true;
                }
                if (report[0] != BlissBoxProtocol.ReportCommand || report[1] != BlissBoxProtocol.CommandNative)
                    return true;
                if (report[2] == 0)
                {
                    _size = (report[3] << 8) | report[4];
                    _buffer = new byte[300];
                    _buffer[0] = report[6];
                    _buffer[1] = report[7];
                    _positioned = false;
                    if (_size <= 2) Complete();
                    return true;
                }
                int at = report[2] == 0xFF ? (_positioned ? _last + 5 : 2) : report[2];
                if (report[2] != 0xFF) { _last = at; _positioned = true; }
                Array.Copy(report, 3, _buffer, at, 5);
                if (report[2] == 0xFF) Complete();
                return true;
            }

            private void Complete() => _reply = Controller?.Invoke(_buffer[.._size]);

            public int GetFeature(byte[] buffer)
            {
                if (Gone) return -1;
                byte id = buffer[0];
                Array.Clear(buffer);
                byte player = (byte)(Player + 3);
                switch (id)
                {
                    case BlissBoxProtocol.ReportInfo:
                        InfoReads++;
                        buffer[0] = Type; buffer[1] = Flags; buffer[2] = Major; buffer[3] = player; buffer[4] = Minor;
                        break;
                    case BlissBoxProtocol.ReportPressure:
                        PressureReads++;
                        buffer[0] = player;
                        Array.Copy(Pressure, 0, buffer, 1, 12);
                        break;
                    case BlissBoxProtocol.ReportScreenRead:
                        ScreenReads++;
                        buffer[0] = player;
                        Array.Copy(Stored, 0, buffer, 1, 192);
                        break;
                    case BlissBoxProtocol.ReportNative:
                        buffer[0] = player;
                        if (_reply != null)
                        {
                            buffer[1] = BlissBoxProtocol.NativeUse;
                            buffer[2] = (byte)_reply.Length;
                            Array.Copy(_reply, 0, buffer, 3, _reply.Length);
                            _reply = null;
                        }
                        break;
                }
                return buffer.Length;
            }

            public List<byte[]> Motor(byte command) => Sent.Where(r => r[0] == 18 && r[1] == command).ToList();
            public int ScreenWrites => Sent.Count(r => r[0] == BlissBoxProtocol.ReportScreen);
        }

        /// <summary>An N64 controller with a pak in it, as Joybus answers.</summary>
        private sealed class Pak
        {
            public byte[] Memory = new byte[BlissBoxControllerPak.PakBytes];
            public bool Present = true, Rumble;
            public int BadReads, RejectedWrites;

            public byte[] Answer(byte[] message)
            {
                switch (message[0])
                {
                    case BlissBoxControllerPak.CommandStatus:
                        return new byte[] { 0x05, 0x00, (byte)(Present ? 1 : 2) };
                    case BlissBoxControllerPak.CommandRead:
                    {
                        int address = Address(message);
                        var data = Rumble ? Enumerable.Repeat((byte)0x80, 32).ToArray() : Memory[address..(address + 32)];
                        byte crc = BlissBoxControllerPak.DataCrc(data);
                        if (!Present) crc = (byte)~crc;
                        if (BadReads > 0) { BadReads--; crc ^= 0x01; }
                        return data.Append(crc).ToArray();
                    }
                    case BlissBoxControllerPak.CommandWrite:
                    {
                        int address = Address(message);
                        var data = message[3..35];
                        byte crc = BlissBoxControllerPak.DataCrc(data);
                        if (RejectedWrites > 0) { RejectedWrites--; return new[] { (byte)(crc ^ 0x01) }; }
                        Array.Copy(data, 0, Memory, address, 32);
                        return new[] { crc };
                    }
                }
                return null;
            }

            private static int Address(byte[] message)
            {
                int field = (message[1] << 8) | message[2];
                int address = field & 0xFFE0;
                Assert.Equal(BlissBoxControllerPak.AddressChecksum(address), field & 0x1F);
                return address;
            }
        }

        private long _now;

        private BlissBoxSession Session(ScriptedAdapter adapter)
            => new BlissBoxSession(adapter, adapter.Player, () => _now, ms => _now += ms);

        [Fact]
        public void InfoIsReadEvery500Ms()
        {
            var adapter = new ScriptedAdapter();
            var session = Session(adapter);
            session.Step();
            Assert.Equal(1, adapter.InfoReads);
            Assert.Equal(19, session.Info.Type);
            _now = 499; session.Step();
            Assert.Equal(1, adapter.InfoReads);
            _now = 500; session.Step();
            Assert.Equal(2, adapter.InfoReads);
            Assert.NotNull(session.LiveInfo);

            // A searching adapter has no controller to name.
            adapter.Flags = BlissBoxProtocol.FlagSearching;
            _now = 1000; session.Step();
            Assert.NotNull(session.Info);
            Assert.Null(session.LiveInfo);
        }

        [Fact]
        public void FailedReadsCountUntilTheChannelIsForgotten()
        {
            var adapter = new ScriptedAdapter { Gone = true };
            var session = Session(adapter);
            for (int i = 0; i < 3; i++)
            {
                _now = i * BlissBoxSession.InfoIntervalMs;
                session.Step();
            }
            Assert.Equal(3, session.FailedInfoReads);
            session.Forget();
            Assert.Equal(0, session.FailedInfoReads);
            adapter.Gone = false;
            _now = 5000; session.Step();
            Assert.NotNull(session.Info);
        }

        [Fact]
        public void PressureIsReadEvery50Ms_OnlyWithADualShock2()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDualShock2 };
            adapter.Pressure[6] = 200;
            var session = Session(adapter);
            session.Step();
            Assert.Equal(1, adapter.PressureReads);
            Assert.Equal(200, session.Pressure[6]);
            _now = 49; session.Step();
            Assert.Equal(1, adapter.PressureReads);
            _now = 50; session.Step();
            Assert.Equal(2, adapter.PressureReads);

            adapter.Type = BlissBoxControllers.TypeNintendo64;
            _now = 500; session.Step();
            Assert.Null(session.Pressure);
            _now = 600; session.Step();
            Assert.Equal(2, adapter.PressureReads);
        }

        [Fact]
        public void AReadForAnotherPlayerIsThrownAway()
        {
            var adapter = new ScriptedAdapter { Player = 2 };
            var session = new BlissBoxSession(adapter, 1, () => _now, ms => _now += ms);
            session.Step();
            Assert.Null(session.Info);
        }

        [Fact]
        public void AMotorIsSentOnChange_RefreshedEvery100Ms_AndStoppedWithTypeZero()
        {
            var adapter = new ScriptedAdapter();
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            var large = adapter.Motor(BlissBoxProtocol.CommandLargeMotor);
            Assert.Equal(new byte[] { 18, 4, 0, 0, 1, BlissBoxSession.Strength(40000), 0xFF, 0, 0 }, Assert.Single(large));
            Assert.Empty(adapter.Motor(BlissBoxProtocol.CommandSmallMotor));

            _now = 60; session.Step();
            Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
            _now = 100; session.Step();
            Assert.Equal(2, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);

            session.SetRumble(0, 0);
            _now = 110; session.Step();
            Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, adapter.Motor(BlissBoxProtocol.CommandLargeMotor)[^1]);
            _now = 400; session.Step();
            Assert.Equal(3, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
        }

        [Fact]
        public void ASmallLevelNeverBecomesStrengthZero_WhichTypeOneReadsAsFull()
        {
            Assert.Equal(0, BlissBoxSession.Strength(0));
            Assert.Equal(1, BlissBoxSession.Strength(1));
            Assert.Equal(1, BlissBoxSession.Strength(256));
            Assert.Equal(255, BlissBoxSession.Strength(65535));
        }

        [Fact]
        public void TheScreenIsReadFirst_AndWrittenOnlyWhenItDiffersAndASecondHasPassed()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            var picture = Enumerable.Range(0, 192).Select(i => (byte)i).ToArray();
            adapter.Stored = (byte[])picture.Clone();
            var session = Session(adapter);

            session.SetScreen(picture);
            session.Step();
            Assert.Equal(1, adapter.ScreenReads);
            Assert.Equal(0, adapter.ScreenWrites);

            var other = picture.Select(b => (byte)~b).ToArray();
            session.SetScreen(other);
            _now = 10; session.Step();
            Assert.Equal(1, adapter.ScreenWrites);
            Assert.Equal(other, adapter.Stored);

            var third = picture.Select(b => (byte)(b ^ 0x55)).ToArray();
            session.SetScreen(third);
            _now = 600; session.Step();
            Assert.Equal(1, adapter.ScreenWrites);
            _now = 1010; session.Step();
            Assert.Equal(2, adapter.ScreenWrites);
            Assert.Equal(third, adapter.Stored);

            session.SetScreen(third);
            _now = 5000; session.Step();
            Assert.Equal(2, adapter.ScreenWrites);
        }

        [Fact]
        public void NothingTouchesTheScreenWithoutADreamcastPad()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64 };
            var session = Session(adapter);
            session.SetScreen(new byte[192]);
            session.Step();
            Assert.Equal(0, adapter.ScreenReads);
            Assert.Equal(0, adapter.ScreenWrites);
        }

        [Fact]
        public void TheNativePollFillsTheArrows_OnlyOnA3xDigitalPad()
        {
            byte upAndLeft = unchecked((byte)~(0x10 | 0x80));
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypePlayStationDigital,
                Major = 3,
                Minor = 24,
                Controller = m => m.SequenceEqual(BlissBoxPsx.PollMessage())
                    ? new byte[] { 0xFF, 0x41, 0x5A, upAndLeft, 0xFF } : null,
            };
            var session = Session(adapter);
            session.Step();
            Assert.Equal(-1, session.Arrows);

            session.NativeArrows = true;
            _now = 20; session.Step();
            Assert.Equal(0x01 | 0x04, session.Arrows);

            adapter.Major = 4;
            _now = 600; session.Step();
            Assert.False(session.NativeArrowsActive);
            Assert.Equal(-1, session.Arrows);
        }

        [Fact]
        public async System.Threading.Tasks.Task APlayerJobSendsTheStoredNumberWithAReset()
        {
            var adapter = new ScriptedAdapter();
            var session = Session(adapter);
            var job = new BlissBoxPlayerJob(3);
            session.Enqueue(job);
            session.Step();
            Assert.True((await job.Completion).Ok);
            Assert.Contains(adapter.Sent, r => r.SequenceEqual(BlissBoxProtocol.SetPlayer(3)));
        }

        private (ScriptedAdapter, Pak, BlissBoxSession) PakPort()
        {
            var pak = new Pak();
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Controller = m => pak.Answer(m) };
            var session = Session(adapter);
            session.Step(); // identify the controller
            return (adapter, pak, session);
        }

        [Fact]
        public async System.Threading.Tasks.Task ABackupReadsEveryBlock_RetryingABadCrc()
        {
            var (_, pak, session) = PakPort();
            new Random(32).NextBytes(pak.Memory);
            pak.BadReads = 2;
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            var result = (await job.Completion);
            Assert.True(result.Ok);
            Assert.Equal(pak.Memory, result.Data);
        }

        [Fact]
        public async System.Threading.Tasks.Task ABackupStopsOnABlockThatKeepsFailing()
        {
            var (_, pak, session) = PakPort();
            pak.BadReads = 1 + 3; // block 0's probe read, then three failures
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            var result = (await job.Completion);
            Assert.Equal(BlissBoxJobError.BadBlock, result.Error);
            Assert.Equal(0, result.Block);
        }

        [Fact]
        public async System.Threading.Tasks.Task ARumblePakAndAMissingPakAreRefused()
        {
            var (_, pak, session) = PakPort();
            pak.Rumble = true;
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            Assert.Equal(BlissBoxJobError.RumblePak, (await job.Completion).Error);

            pak.Rumble = false;
            pak.Present = false;
            var restore = new BlissBoxPakRestoreJob(new byte[BlissBoxControllerPak.PakBytes]);
            session.Enqueue(restore);
            session.Step();
            Assert.Equal(BlissBoxJobError.NoPak, (await restore.Completion).Error);
        }

        [Fact]
        public async System.Threading.Tasks.Task ARestoreWritesEveryBlock_RetryingRejectedWritesUpTo16()
        {
            var (_, pak, session) = PakPort();
            var image = new byte[BlissBoxControllerPak.PakBytes];
            new Random(16).NextBytes(image);
            pak.RejectedWrites = 16;
            var job = new BlissBoxPakRestoreJob(image);
            session.Enqueue(job);
            session.Step();
            Assert.True((await job.Completion).Ok);
            Assert.Equal(image, pak.Memory);

            pak.RejectedWrites = 17;
            var failing = new BlissBoxPakRestoreJob(image);
            session.Enqueue(failing);
            session.Step();
            Assert.Equal(BlissBoxJobError.TooManyErrors, (await failing.Completion).Error);
        }

        [Fact]
        public async System.Threading.Tasks.Task PakJobsNeedAnN64Controller()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDualShock2 };
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            Assert.Equal(BlissBoxJobError.WrongController, (await job.Completion).Error);
        }

        [Fact]
        public async System.Threading.Tasks.Task AClosingPortEndsAJobAtItsNextBlock()
        {
            var (_, pak, session) = PakPort();
            var job = new BlissBoxPakBackupJob(new Progress<double>(_ => session.RequestStop()));
            session.RequestStop();
            session.Enqueue(job);
            session.Step();
            Assert.Equal(BlissBoxJobError.Closed, (await job.Completion).Error);

            var queued = new BlissBoxPakBackupJob();
            session.Enqueue(queued);
            session.CancelJobs();
            Assert.Equal(BlissBoxJobError.Closed, (await queued.Completion).Error);
        }

        [Fact]
        public async System.Threading.Tasks.Task AClosingPortEndsARestoreBetweenRetries()
        {
            var (adapter, pak, session) = PakPort();
            pak.RejectedWrites = 10;
            int writes = 0;
            adapter.Controller = m =>
            {
                if (m[0] == BlissBoxControllerPak.CommandWrite && ++writes == 1) session.RequestStop();
                return pak.Answer(m);
            };
            var job = new BlissBoxPakRestoreJob(new byte[BlissBoxControllerPak.PakBytes]);
            session.Enqueue(job);
            session.Step();
            var result = await job.Completion;
            Assert.Equal(BlissBoxJobError.Closed, result.Error);
            Assert.Equal(0, result.Block);
            Assert.Equal(1, writes);
        }

        [Fact]
        public async System.Threading.Tasks.Task AJobStillQueuedEndsWhenTheChannelDrops()
        {
            var (_, _, session) = PakPort();
            var queued = new BlissBoxPakRestoreJob(new byte[BlissBoxControllerPak.PakBytes]);
            session.Enqueue(queued);
            session.Forget();
            Assert.Equal(BlissBoxJobError.Closed, (await queued.Completion).Error);
        }

        [Fact]
        public async System.Threading.Tasks.Task ABackupThatLosesTheAdapterFailsAsNoReply()
        {
            var (adapter, pak, session) = PakPort();
            int reads = 0;
            // The probe read and blocks 0 to 3 answer, then nothing does.
            adapter.Controller = m => m[0] == BlissBoxControllerPak.CommandRead && ++reads > 5 ? null : pak.Answer(m);
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            var result = await job.Completion;
            Assert.Equal(BlissBoxJobError.NoReply, result.Error);
            Assert.Equal(4, result.Block);
        }

        [Fact]
        public async System.Threading.Tasks.Task ARestoreThatLosesTheAdapterFailsAsNoReply()
        {
            var (adapter, pak, session) = PakPort();
            int writes = 0;
            // Blocks 0 to 2 are written, then nothing answers.
            adapter.Controller = m => m[0] == BlissBoxControllerPak.CommandWrite && ++writes > 3 ? null : pak.Answer(m);
            var job = new BlissBoxPakRestoreJob(new byte[BlissBoxControllerPak.PakBytes]);
            session.Enqueue(job);
            session.Step();
            var result = await job.Completion;
            Assert.Equal(BlissBoxJobError.NoReply, result.Error);
            Assert.Equal(3, result.Block);
            Assert.Equal(3 + BlissBoxControllerPak.WriteErrorLimit + 1, writes);
        }

        [Fact]
        public void StopMotorsSendsTypeZeroToBoth()
        {
            var adapter = new ScriptedAdapter();
            var session = Session(adapter);
            session.StopMotors();
            Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, adapter.Motor(BlissBoxProtocol.CommandLargeMotor)[^1]);
            Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, adapter.Motor(BlissBoxProtocol.CommandSmallMotor)[^1]);
        }
    }
}
