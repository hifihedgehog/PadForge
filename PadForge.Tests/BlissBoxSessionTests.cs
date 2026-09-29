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
            public int InfoReads, PressureReads, ScreenReads, NotReadyReads;
            public Func<byte[], byte[]> Controller;
            /// <summary>A report 18 command the adapter refuses, or null. A
            /// refused player command never reaches the firmware, so the
            /// adapter stays as it is.</summary>
            public byte? RefuseCommand;
            /// <summary>The screen report is refused.</summary>
            public bool RefuseScreen;
            /// <summary>Native chunks whose copy count went negative, which on a
            /// GPA runs the copy over its RAM (0x2D47).</summary>
            public int Overruns;
            private byte[] _buffer;
            private int _size, _last = 40; // stale RAM on a 3.x adapter
            private byte[] _reply;
            private bool _answering;

            public int FeatureLength => 200;

            public bool SetFeature(byte[] report)
            {
                Sent.Add((byte[])report.Clone());
                if (report[0] == BlissBoxProtocol.ReportCommand && report[1] == RefuseCommand) return false;
                if (report[0] == BlissBoxProtocol.ReportScreen)
                {
                    if (RefuseScreen) return false;
                    Array.Copy(report, 4, Stored, 0, 192);
                    return true;
                }
                if (report[0] == BlissBoxProtocol.ReportCommand && report[1] == BlissBoxProtocol.CommandPlayer)
                {
                    // Both firmwares reset from inside the handler, before the
                    // status stage (3.0 0x08EB, GPA 0x2CE2): the write fails
                    // and the handle never answers again.
                    Gone = true;
                    return false;
                }
                if (report[0] != BlissBoxProtocol.ReportCommand || report[1] != BlissBoxProtocol.CommandNative)
                    return true;
                if (report[2] == 0)
                {
                    // Only the size's low byte counts (GPA 0x2D20, 3.0 0x0954).
                    _size = report[4];
                    _buffer = new byte[300];
                    _buffer[0] = report[6];
                    _buffer[1] = report[7];
                    // A GPA resets its position with each header (0x2D30),
                    // the 3.0 firmware never does.
                    if (Major >= 4) _last = 0;
                    if (_size <= 2) Complete();
                    return true;
                }
                // Each generation's placement and copy count, as in
                // BlissBoxProtocolTests' firmware models. Every chunk's
                // position becomes the last one on both (3.0 0x0983, GPA
                // 0x2DC1).
                int at, count = 5;
                if (report[2] != 0xFF) at = report[2];
                else if (Major < 4) at = (_last + 5) & 0xFF;
                else if (_last == 0) { at = 2; count = _size - 2; }
                else { at = _last + 5; count = _size - at; }
                if (count < 0)
                {
                    Overruns++;
                    return true;
                }
                var padded = new byte[FeatureLength];
                Array.Copy(report, padded, report.Length);
                Array.Copy(padded, 3, _buffer, at, count);
                _last = at;
                if (report[2] == 0xFF) Complete();
                return true;
            }

            /// <summary>The message is whole. The first report 22 read after it
            /// finds the exchange not yet run: 3.0 runs it from its main loop
            /// once a counter every SET resets has run out (0x090B), and a GPA
            /// answers not ready first.</summary>
            private void Complete()
            {
                var message = _buffer[.._size];
                _reply = Controller?.Invoke(message);
                // A 3.x adapter's Joybus routine returns early for a read
                // whose reply is not 33 bytes, leaving the message in the
                // buffer, and report 22 reads its command byte as the size:
                // the two address bytes come back (0x0FA8, 0x07D3).
                if (Major < 4 && message.Length >= 3 && message[0] == BlissBoxControllerPak.CommandRead && _reply?.Length != 33)
                    _reply = new[] { message[1], message[2] };
                _answering = false;
            }

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
                        if (!_answering)
                        {
                            // Not ready: the use byte, no size.
                            _answering = true;
                            NotReadyReads++;
                            buffer[1] = BlissBoxProtocol.NativeUse;
                            break;
                        }
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
        public void AOneMotorPadTakesTheStrongerLevelOnCommand4Alone()
        {
            // The API Tool drives one motor on an N64, GameCube or Dreamcast
            // controller, and GPA 4.86 runs command 5 on a Dreamcast pad at
            // full power whatever the strength (0x0C2A).
            foreach (byte type in new[] { BlissBoxControllers.TypeNintendo64, BlissBoxControllers.TypeDreamcast, (byte)9 })
            {
                _now = 0;
                var adapter = new ScriptedAdapter { Type = type };
                var session = Session(adapter);
                session.SetRumble(1000, 50000);
                session.Step();
                Assert.Equal(BlissBoxSession.Strength(50000), Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor))[5]);
                Assert.Empty(adapter.Motor(BlissBoxProtocol.CommandSmallMotor));
                session.SetRumble(0, 0);
                _now = 10; session.Step();
                Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, adapter.Motor(BlissBoxProtocol.CommandLargeMotor)[^1]);
                Assert.Empty(adapter.Motor(BlissBoxProtocol.CommandSmallMotor));
                Assert.True(session.MotorsAtRest);
            }
        }

        [Fact]
        public void APadWithoutMotorsIsSentNothing()
        {
            // Every write costs a 3.x adapter a controller poll (0x090B).
            var adapter = new ScriptedAdapter { Type = 3, Major = 3, Minor = 34 };
            var session = Session(adapter);
            session.SetRumble(40000, 40000);
            session.Step();
            _now = 300; session.Step();
            Assert.Empty(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
            Assert.Empty(adapter.Motor(BlissBoxProtocol.CommandSmallMotor));
            Assert.True(session.MotorsAtRest);
            // A DualShock 2 plugged in takes the levels waiting for it.
            adapter.Type = BlissBoxControllers.TypeDualShock2;
            _now = 500; session.Step();
            Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
            Assert.Single(adapter.Motor(BlissBoxProtocol.CommandSmallMotor));
        }

        [Fact]
        public void AControllerChangeTellsTheMotorsTheirLevelAgain()
        {
            // The new pad's motors are the adapter's to say, so even a stop
            // that matches what was last sent goes out once.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDualShock2 };
            var session = Session(adapter);
            // The first pad found is told too: a new port knows nothing of
            // what the adapter's motors run.
            session.Step();
            Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
            _now = 500; session.Step();
            Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
            adapter.Type = BlissBoxControllers.TypeDualShock;
            _now = 1000; session.Step();
            var large = adapter.Motor(BlissBoxProtocol.CommandLargeMotor);
            var small = adapter.Motor(BlissBoxProtocol.CommandSmallMotor);
            Assert.Equal(2, large.Count);
            Assert.Equal(2, small.Count);
            Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, large[^1]);
            Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, small[^1]);
            _now = 1500; session.Step();
            Assert.Equal(2, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
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
            Assert.True(adapter.Gone);
        }

        [Fact]
        public async System.Threading.Tasks.Task APlayerChangeTheAdapterNeverGotLeavesThePlayer()
        {
            // The write failed and the adapter still answers as this port's
            // player a moment later, so it never reset.
            var adapter = new ScriptedAdapter { RefuseCommand = BlissBoxProtocol.CommandPlayer };
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPlayerJob(2);
            session.Enqueue(job);
            session.Step();
            Assert.Equal(BlissBoxJobError.PlayerUnchanged, (await job.Completion).Error);
            Assert.False(adapter.Gone);
        }

        [Fact]
        public async System.Threading.Tasks.Task ARefusedPictureRestoreSendsNoPlayerChange()
        {
            // The adapter would come back still holding PadForge's picture, and
            // the port's copy of its own would be dropped with the old device.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, RefuseScreen = true };
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPlayerJob(3, Enumerable.Repeat((byte)0x33, 192).ToArray());
            session.Enqueue(job);
            _now = 10; session.Step();
            Assert.Equal(BlissBoxJobError.PlayerUnchanged, (await job.Completion).Error);
            Assert.DoesNotContain(adapter.Sent, r => r.SequenceEqual(BlissBoxProtocol.SetPlayer(3)));
            Assert.False(adapter.Gone);
        }

        [Fact]
        public async System.Threading.Tasks.Task APlayerChangeOnAClosingPortSendsNothing()
        {
            var adapter = new ScriptedAdapter();
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPlayerJob(2, new byte[192]);
            session.Enqueue(job);
            session.RequestStop();
            _now = 10; session.Step();
            Assert.Equal(BlissBoxJobError.Closed, (await job.Completion).Error);
            Assert.DoesNotContain(adapter.Sent, r => r[0] == BlissBoxProtocol.ReportScreen
                || (r[0] == BlissBoxProtocol.ReportCommand && r[1] == BlissBoxProtocol.CommandPlayer));
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
            // Queued before the stop, so the job runs and meets the request.
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.RequestStop();
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

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public async System.Threading.Tasks.Task AReadTheControllerNeverAnsweredFailsAsNoReply_OnBothGenerations(byte major)
        {
            // A 3.x adapter answers such a read with the two address bytes it
            // was sent, which once counted as an answer and failed the block
            // as a bad checksum.
            var pak = new Pak();
            int reads = 0;
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeNintendo64,
                Major = major,
                Controller = m => m[0] == BlissBoxControllerPak.CommandRead && ++reads > 5 ? null : pak.Answer(m),
            };
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            var result = await job.Completion;
            Assert.Equal(BlissBoxJobError.NoReply, result.Error);
            Assert.Equal(4, result.Block);

            // The probe read of block 0 alike.
            adapter.Controller = m => m[0] == BlissBoxControllerPak.CommandRead ? null : pak.Answer(m);
            var probe = new BlissBoxPakBackupJob();
            session.Enqueue(probe);
            session.Step();
            Assert.Equal(BlissBoxJobError.NoReply, (await probe.Completion).Error);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public async System.Threading.Tasks.Task APakRoundTripsOnBothFirmwareGenerations_WithNoOverrun(byte major)
        {
            // Every block read is a 3-byte message, the length the framing has
            // to get right per generation.
            var pak = new Pak();
            new Random(major).NextBytes(pak.Memory);
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = major, Controller = m => pak.Answer(m) };
            var session = Session(adapter);
            session.Step();
            var backup = new BlissBoxPakBackupJob();
            session.Enqueue(backup);
            session.Step();
            var result = await backup.Completion;
            Assert.True(result.Ok);
            Assert.Equal(pak.Memory, result.Data);

            var image = new byte[BlissBoxControllerPak.PakBytes];
            new Random(major + 10).NextBytes(image);
            var restore = new BlissBoxPakRestoreJob(image);
            session.Enqueue(restore);
            session.Step();
            Assert.True((await restore.Completion).Ok);
            Assert.Equal(image, pak.Memory);
            Assert.Equal(0, adapter.Overruns);
        }

        [Fact]
        public async System.Threading.Tasks.Task PakJobsNeedFirmware3()
        {
            // 2.x frames the native channel another way, and the API Tool's
            // memory manager refuses it (memManager.cs:150).
            var pak = new Pak();
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 2, Controller = m => pak.Answer(m) };
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            Assert.Equal(BlissBoxJobError.OldFirmware, (await job.Completion).Error);
            Assert.DoesNotContain(adapter.Sent, r => r[0] == BlissBoxProtocol.ReportCommand && r[1] == BlissBoxProtocol.CommandNative);
        }

        [Fact]
        public async System.Threading.Tasks.Task AJobQueuedOnAClosingPortEndsAtOnce()
        {
            // The worker ends the queue once as it leaves. A job the UI queues
            // after that, once its file dialog closes, used to wait forever.
            var session = Session(new ScriptedAdapter());
            session.RequestStop();
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            Assert.True(job.Completion.IsCompleted);
            Assert.Equal(BlissBoxJobError.Closed, (await job.Completion).Error);
            Assert.False(session.Busy);
        }

        [Fact]
        public void ARefusedMotorWriteWaitsBeforeTryingAgain()
        {
            var adapter = new ScriptedAdapter { RefuseCommand = BlissBoxProtocol.CommandLargeMotor };
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
            _now = 10;
            int wait = session.Step();
            Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
            Assert.InRange(wait, 1, BlissBoxSession.RumbleRefreshMs);
            _now = BlissBoxSession.RumbleRefreshMs;
            session.Step();
            Assert.Equal(2, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
        }

        [Fact]
        public void AReopenedChannelTellsBothMotorsTheirLevelAgain()
        {
            // The adapter may have kept the last level through the drop, and a
            // stop that happened meanwhile must still reach it.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDualShock2 };
            var session = Session(adapter);
            session.SetRumble(40000, 40000);
            session.Step();
            session.Forget();
            session.SetRumble(0, 0);
            Assert.False(session.MotorsAtRest);
            int before = adapter.Sent.Count;
            _now = 10;
            session.Step();
            Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, adapter.Motor(BlissBoxProtocol.CommandLargeMotor)[^1]);
            Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, adapter.Motor(BlissBoxProtocol.CommandSmallMotor)[^1]);
            Assert.True(adapter.Sent.Count > before);
            Assert.True(session.MotorsAtRest);
        }

        [Fact]
        public void ADeliveredMotorIsNotSentAgainWhileTheOtherKeepsFailing()
        {
            // After a reopen each motor is told its level once. The large one
            // was sent again on every step while the small one's write kept
            // failing, well above the 100 ms refresh.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDualShock2 };
            var session = Session(adapter);
            session.SetRumble(40000, 40000);
            session.Step();
            session.Forget();
            adapter.RefuseCommand = BlissBoxProtocol.CommandSmallMotor;
            _now = 10; session.Step();
            int large = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count;
            int small = adapter.Motor(BlissBoxProtocol.CommandSmallMotor).Count;
            _now = 20; session.Step();
            _now = 60; session.Step();
            Assert.Equal(large, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
            Assert.Equal(small, adapter.Motor(BlissBoxProtocol.CommandSmallMotor).Count);
            Assert.False(session.MotorsAtRest);
            // The running motor's refresh and the refused one's retry, both
            // 100 ms after the reopen's writes.
            _now = 110; session.Step();
            Assert.Equal(large + 1, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
            Assert.Equal(small + 1, adapter.Motor(BlissBoxProtocol.CommandSmallMotor).Count);
        }

        [Fact]
        public async System.Threading.Tasks.Task EveryTalkMeetsANotReadyReplyFirst_AndReadsAgain()
        {
            // The scripted adapter answers not ready on the first read after
            // each message, as both firmwares do, so the retry path runs.
            var (adapter, pak, session) = PakPort();
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            Assert.True((await job.Completion).Ok);
            // The status, the probe read and 1024 block reads.
            Assert.Equal(2 + BlissBoxControllerPak.Blocks, adapter.NotReadyReads);
        }

        [Fact]
        public void APendingPictureWithoutAScreenPadDoesNotSpinTheWorker()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            var session = Session(adapter);
            session.Step();
            var first = Enumerable.Repeat((byte)0x0F, 192).ToArray();
            session.SetScreen(first);
            _now = 10; session.Step();
            Assert.Equal(1, adapter.ScreenWrites);
            session.SetScreen(Enumerable.Repeat((byte)0xF0, 192).ToArray());
            // The Dreamcast pad leaves for an N64 controller before the next write.
            adapter.Type = BlissBoxControllers.TypeNintendo64;
            _now = 500; session.Step();
            _now = 1100;
            Assert.True(session.Step() > 0);
            Assert.Equal(1, adapter.ScreenWrites);
        }

        [Fact]
        public async System.Threading.Tasks.Task APlayerChangeThatResetsBeforeAnsweringIsDone()
        {
            // Both firmwares reset inside the command's handler, so Windows
            // reports the write as failed although it took. The scripted
            // adapter does the same.
            var adapter = new ScriptedAdapter();
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPlayerJob(2);
            session.Enqueue(job);
            session.Step();
            Assert.True((await job.Completion).Ok);
            Assert.Contains(adapter.Sent, r => r.SequenceEqual(BlissBoxProtocol.SetPlayer(2)));
            Assert.True(adapter.Gone);
        }

        [Fact]
        public async System.Threading.Tasks.Task APlayerChangePutsTheAdaptersPictureBackFirst()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            var session = Session(adapter);
            session.Step();
            var padforge = Enumerable.Repeat((byte)0x55, 192).ToArray();
            session.SetScreen(padforge);
            _now = 10; session.Step();
            Assert.Equal(padforge, adapter.Stored);

            var original = Enumerable.Repeat((byte)0xFF, 192).ToArray();
            var job = new BlissBoxPlayerJob(3, original);
            session.Enqueue(job);
            _now = 20; session.Step();
            Assert.True((await job.Completion).Ok);
            Assert.Equal(original, adapter.Stored);
            int screen = adapter.Sent.FindLastIndex(r => r[0] == BlissBoxProtocol.ReportScreen);
            int player = adapter.Sent.FindLastIndex(r => r.SequenceEqual(BlissBoxProtocol.SetPlayer(3)));
            Assert.True(screen >= 0 && player > screen);
            // The EEPROM guard held: the restore waited out the second since
            // PadForge's own write.
            Assert.True(_now >= 10 + BlissBoxSession.ScreenIntervalMs);
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
