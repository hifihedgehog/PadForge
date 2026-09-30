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
            // Stores the picture and then reports the transfer failed, as a
            // transfer that outlived its wait does.
            public bool StoreThenRefuseScreen;
            /// <summary>Reads that fail after a refused player command, a
            /// moment's trouble on a port that kept its number.</summary>
            public int FailReadsAfterRefusedPlayer;
            private int _failReads;
            /// <summary>Native chunks whose copy count went negative, which on a
            /// GPA runs the copy over its RAM (0x2D47).</summary>
            public int Overruns;
            /// <summary>A write this returns true for is refused.</summary>
            public Func<byte[], bool> RefuseWhen;
            /// <summary>Runs inside each write, before the adapter handles it:
            /// another thread's request landing while a write is in flight.</summary>
            public Action<byte[]> DuringWrite;
            /// <summary>The session's clock, for the Dreamcast motor timer.</summary>
            public Func<long> Clock = () => 0;
            /// <summary>The timeout each picture write was given.</summary>
            public readonly List<int> ScreenTimeouts = new();
            /// <summary>Report 22 never comes back ready, as while 3.0's main
            /// loop has not run the exchange.</summary>
            public bool NeverReady;
            public int NativeReads;
            /// <summary>GPA 4.86's Dreamcast motor state ("GPA 4.86_2.asm"):
            /// the strength both commands share (0x031A), each command's loop
            /// (0x031E for command 4, 0x0320 for command 5), and the owner of
            /// the one timer (0x0539).</summary>
            public byte DcStrength, DcLoop4, DcLoop5;
            public int DcTimerOwner;
            private long _dcTimerEnds;
            private byte[] _buffer;
            private int _size, _last = 40; // stale RAM on a 3.x adapter
            private byte[] _reply;
            private bool _answering;

            public int FeatureLength => 200;

            public bool SetFeature(byte[] report, int timeoutMs)
            {
                if (report[0] == BlissBoxProtocol.ReportScreen) ScreenTimeouts.Add(timeoutMs);
                return SetFeature(report);
            }

            public bool SetFeature(byte[] report)
            {
                Sent.Add((byte[])report.Clone());
                DuringWrite?.Invoke(report);
                if (RefuseWhen?.Invoke(report) == true) return false;
                if (report[0] == BlissBoxProtocol.ReportCommand && report[1] == RefuseCommand)
                {
                    if (report[1] == BlissBoxProtocol.CommandPlayer) _failReads = FailReadsAfterRefusedPlayer;
                    return false;
                }
                if (report[0] == BlissBoxProtocol.ReportScreen && RefuseScreen) return false;
                if (Major >= 4 && Type == BlissBoxControllers.TypeDreamcast) DcWrite(report);
                if (report[0] == BlissBoxProtocol.ReportScreen)
                {
                    Array.Copy(report, 4, Stored, 0, 192);
                    return !StoreThenRefuseScreen;
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

            /// <summary>The message is whole. On a 3.x adapter the first report
            /// 22 read after it finds the exchange not yet run, since 3.0 runs
            /// it from its main loop (0x30B9). A GPA runs it inside the
            /// transfer (0x2D32 to 0x2D44) and answers ready at once.</summary>
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
                if (_failReads > 0)
                {
                    _failReads--;
                    return -1;
                }
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
                        NativeReads++;
                        if (NeverReady)
                        {
                            buffer[1] = BlissBoxProtocol.NativeUse;
                            break;
                        }
                        if (Major < 4 && !_answering)
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

            /// <summary>Before any write but report 0x0F and commands 4, 5 and
            /// 0x25, command 5 at full power for 10 ms (0x2BEF to 0x2BF9).
            /// Then the motor commands (0x2D82 to 0x2D93): type 0 passes 0 and
            /// 0, type 1 the strength, full for 0, and a loop of 255.</summary>
            private void DcWrite(byte[] report)
            {
                DcExpire();
                bool motor = report[0] == BlissBoxProtocol.ReportCommand
                    && (report[1] == BlissBoxProtocol.CommandLargeMotor || report[1] == BlissBoxProtocol.CommandSmallMotor);
                if (!motor)
                {
                    if (report[0] != 0x0F && report[1] != BlissBoxProtocol.CommandNative) DcCommand5(10);
                    return;
                }
                bool on = report[4] != 0;
                byte loop = on ? (byte)255 : (byte)0;
                if (report[1] == BlissBoxProtocol.CommandLargeMotor)
                    DcCommand4(on ? (report[5] == 0 ? (byte)0xFF : report[5]) : (byte)0, loop);
                else DcCommand5(loop);
            }

            // 0x0C38: the strength and command 4's loop, and a loop takes the
            // timer.
            private void DcCommand4(byte strength, byte loop)
            {
                DcStrength = strength;
                DcLoop4 = loop;
                if (loop != 0) DcTimer(4, loop);
            }

            // 0x0C2A: full strength whatever is asked, and command 5's loop.
            private void DcCommand5(byte loop)
            {
                DcStrength = 0xFF;
                DcLoop5 = loop;
                if (loop != 0) DcTimer(5, loop);
            }

            private void DcTimer(int owner, int ms)
            {
                DcTimerOwner = owner;
                _dcTimerEnds = Clock() + ms;
            }

            // 0x29F7 to 0x2A14: the timer ends only the command that last took
            // it.
            private void DcExpire()
            {
                if (DcTimerOwner == 0 || Clock() < _dcTimerEnds) return;
                int owner = DcTimerOwner;
                DcTimerOwner = 0;
                if (owner == 4) DcCommand4(0, 0);
                else DcCommand5(0);
            }

            /// <summary>The power the poll gives the jump pack, 1 to 7 while
            /// either loop is set and 0 when none is (0x0E0A to 0x0E24).</summary>
            public int DcPower
            {
                get
                {
                    DcExpire();
                    return (DcLoop4 | DcLoop5) == 0 ? 0 : Math.Max(1, DcStrength >> 5);
                }
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
            // An N64 controller has one motor: command 5 carries only the stop
            // a GPA gets before command 4 is told its level again.
            Assert.All(adapter.Motor(BlissBoxProtocol.CommandSmallMotor),
                r => Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, r));

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
            foreach (byte major in new byte[] { 3, 4 })
            foreach (byte type in new[] { BlissBoxControllers.TypeNintendo64, BlissBoxControllers.TypeDreamcast, (byte)9 })
            {
                _now = 0;
                var adapter = new ScriptedAdapter { Type = type, Major = major };
                var session = Session(adapter);
                session.SetRumble(1000, 50000);
                session.Step();
                Assert.Equal(BlissBoxSession.Strength(50000), Assert.Single(adapter.Motor(BlissBoxProtocol.CommandLargeMotor))[5]);
                session.SetRumble(0, 0);
                // On 3.x the stop waits for the next paced write.
                _now = major == 3 ? BlissBoxSession.RumbleRefreshMs : 10;
                session.Step();
                Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, adapter.Motor(BlissBoxProtocol.CommandLargeMotor)[^1]);
                // Never a level on command 5. A GPA gets a stop there before
                // command 4 is told its level again, and 3.x nothing at all.
                var small = adapter.Motor(BlissBoxProtocol.CommandSmallMotor);
                if (major == 3) Assert.Empty(small);
                else Assert.All(small, r => Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, r));
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
        public void TheNativePollStopsOnceTheAdapterSendsTheArrows()
        {
            // Each request costs a 3.0 adapter polls of its own (0x090B,
            // 0x07D6, 0x30D9), so the poll runs only until the adapter's latch
            // sends the arrows itself, and again once report 17 shows a search
            // or another controller.
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypePlayStationDigital,
                Major = 3,
                Minor = 34,
                Controller = m => m.SequenceEqual(BlissBoxPsx.PollMessage())
                    ? new byte[] { 0xFF, 0x41, 0x5A, 0xFF, 0xFF } : null,
            };
            int NativeWrites() => adapter.Sent.Count(r => r[0] == BlissBoxProtocol.ReportCommand && r[1] == BlissBoxProtocol.CommandNative);
            var session = Session(adapter);
            session.NativeArrows = true;
            session.Step();
            Assert.True(NativeWrites() > 0);

            session.ArrowsLatched = true;
            Assert.False(session.NativeArrowsActive);
            int sent = NativeWrites();
            _now += 100; session.Step();
            Assert.Equal(sent, NativeWrites());
            Assert.Equal(-1, session.Arrows);

            adapter.Flags = BlissBoxProtocol.FlagSearching;
            _now += BlissBoxSession.InfoIntervalMs; session.Step();
            Assert.False(session.ArrowsLatched);
            adapter.Flags = 0;
            _now += BlissBoxSession.InfoIntervalMs; session.Step();
            Assert.True(session.NativeArrowsActive);
            Assert.True(NativeWrites() > sent);
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

            // The probe read of block 0 alike, which fails before any block.
            adapter.Controller = m => m[0] == BlissBoxControllerPak.CommandRead ? null : pak.Answer(m);
            var probe = new BlissBoxPakBackupJob();
            session.Enqueue(probe);
            session.Step();
            var probed = await probe.Completion;
            Assert.Equal(BlissBoxJobError.NoReply, probed.Error);
            Assert.Equal(-1, probed.Block);
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
            // Until the reopen reads report 17 the adapter may still run the
            // last level, and neither firmware ends every rumble on its own:
            // on a GPA this DualShock 2's command 5 took the timer last, and
            // the timer stops only that command (0x29F7), so command 4 would
            // run on. The crash path waits for the stop.
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

        [Theory]
        [InlineData(3, 2 + BlissBoxControllerPak.Blocks)]
        [InlineData(4, 0)]
        public async System.Threading.Tasks.Task A3xTalkMeetsANotReadyReplyFirst_AndAGpaAnswersAtOnce(byte major, int notReady)
        {
            // 3.0 answers not ready on the first read after each message, so
            // the retry path runs: the status, the probe read and 1024 block
            // reads.
            var pak = new Pak();
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = major, Controller = m => pak.Answer(m) };
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPakBackupJob();
            session.Enqueue(job);
            session.Step();
            Assert.True((await job.Completion).Ok);
            Assert.Equal(notReady, adapter.NotReadyReads);
        }

        [Fact]
        public void APortWithNoControllerHasNothingLeftToStop()
        {
            // The quiesce on a crash waited its whole 250 ms for every empty
            // port, whose first report 17 queued a resend it could never send.
            var adapter = new ScriptedAdapter { Flags = BlissBoxProtocol.FlagSearching };
            var session = Session(adapter);
            session.Step();
            Assert.True(session.MotorsAtRest);
            Assert.Empty(adapter.Motor(BlissBoxProtocol.CommandLargeMotor));
        }

        [Theory]
        [InlineData(4, true)]
        [InlineData(3, false)]
        public void APictureWriteOnAGpaTellsTheMotorsAgain_StartingWithACommand5Stop(byte major, bool gpa)
        {
            // GPA 4.86 runs a Dreamcast pad's command-5 routine at full power
            // before it stores a picture (0x2BEF, 0x0C2A), which takes command
            // 4's strength and its stop timer. 3.0 does not.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, Major = major };
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            int before = adapter.Sent.Count;
            session.SetScreen(Enumerable.Repeat((byte)0x33, 192).ToArray());
            _now = 10; session.Step();
            var after = adapter.Sent.Skip(before).ToList();
            Assert.Equal(BlissBoxProtocol.ReportScreen, after[0][0]);
            if (gpa)
            {
                Assert.Equal(3, after.Count);
                Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, after[1]);
                Assert.Equal(BlissBoxProtocol.Rumble(true, BlissBoxSession.Strength(40000)), after[2]);
            }
            else Assert.Single(after);
        }

        [Fact]
        public void TheHandOffTellsBothMotorsAgain()
        {
            // SDL's DirectInput effect can reach a one-motor pad's command-5
            // routine on a GPA (0x2E8C). The hand-off clears it.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            var session = Session(adapter);
            session.Step();
            int before = adapter.Sent.Count(r => r[0] == BlissBoxProtocol.ReportCommand);
            session.ResendMotors();
            _now = 10; session.Step();
            var motors = adapter.Sent.Where(r => r[0] == BlissBoxProtocol.ReportCommand).Skip(before).ToList();
            Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, motors[0]);
            Assert.Equal(new byte[] { 18, 4, 0, 0, 0, 0, 0, 0, 0 }, motors[1]);
        }

        [Fact]
        public async System.Threading.Tasks.Task AReadThatFailsOnceIsNotTakenForTheReset()
        {
            var adapter = new ScriptedAdapter { RefuseCommand = BlissBoxProtocol.CommandPlayer, FailReadsAfterRefusedPlayer = 1 };
            var session = Session(adapter);
            session.Step();
            var job = new BlissBoxPlayerJob(2);
            session.Enqueue(job);
            session.Step();
            Assert.Equal(BlissBoxJobError.PlayerUnchanged, (await job.Completion).Error);
        }

        [Fact]
        public async System.Threading.Tasks.Task APortThatStartsClosingDuringThePicturesWaitSendsNothingMore()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            BlissBoxSession session = null;
            session = new BlissBoxSession(adapter, adapter.Player, () => _now, ms =>
            {
                _now += ms;
                session.RequestStop();
            });
            session.Step();
            session.SetScreen(Enumerable.Repeat((byte)0x55, 192).ToArray());
            _now = 10; session.Step();
            int screens = adapter.ScreenWrites;
            var job = new BlissBoxPlayerJob(3, Enumerable.Repeat((byte)0xFF, 192).ToArray());
            session.Enqueue(job);
            _now = 20; session.Step();
            Assert.Equal(BlissBoxJobError.Closed, (await job.Completion).Error);
            Assert.Equal(screens, adapter.ScreenWrites);
            Assert.DoesNotContain(adapter.Sent, r => r.SequenceEqual(BlissBoxProtocol.SetPlayer(3)));
        }

        [Fact]
        public async System.Threading.Tasks.Task AMotorLevelAJobQueuesGoesOutWithoutWaiting()
        {
            // A job's picture write on a GPA owes the motors their level again
            // (AfterScreenWrite), and it goes out right after the write, inside
            // the job, rather than after it: a player change that keeps its
            // number sleeps between its reads of report 17 while the pulse's
            // timer lapses and leaves a running rumble at full strength. The
            // job here is a player change whose picture restore the adapter
            // refuses, so it neither waits out the EEPROM guard nor sleeps.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, RefuseScreen = true };
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            var job = new BlissBoxPlayerJob(3, Enumerable.Repeat((byte)0x55, 192).ToArray());
            session.Enqueue(job);
            int before = adapter.Sent.Count;
            _now = BlissBoxSession.RumbleRefreshMs;
            session.Step();
            Assert.Equal(BlissBoxJobError.PlayerUnchanged, (await job.Completion).Error);
            var after = adapter.Sent.Skip(before).ToList();
            int picture = after.FindIndex(r => r[0] == BlissBoxProtocol.ReportScreen);
            Assert.True(picture >= 0);
            Assert.Equal(new byte[] { 18, 5, 0, 0, 0, 0, 0, 0, 0 }, after[picture + 1]);
            Assert.Equal(BlissBoxProtocol.Rumble(true, BlissBoxSession.Strength(40000)), after[picture + 2]);
        }

        [Fact]
        public void APictureWriteOnAGpaLeavesTheJumpPackAtItsLevel()
        {
            // Against the model of GPA 4.86's Dreamcast motor routines: the
            // pulse before a picture forces full strength, takes the timer and
            // sets command 5's loop, which keeps the pack powered after the
            // game stops unless the stop on command 5 clears it.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, Clock = () => _now };
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            int level = BlissBoxSession.Strength(40000) >> 5;
            Assert.Equal(level, adapter.DcPower);
            session.SetScreen(Enumerable.Repeat((byte)0x33, 192).ToArray());
            _now = 10; session.Step();
            Assert.Equal(level, adapter.DcPower);
            Assert.Equal(0, adapter.DcLoop5);
            Assert.Equal(4, adapter.DcTimerOwner);
            session.SetRumble(0, 0);
            _now = 20; session.Step();
            Assert.Equal(0, adapter.DcPower);
        }

        [Fact]
        public void ALostCommand5StopIsSentAgain()
        {
            // Command 4's level alone leaves the pulse's loop on command 5 with
            // no timer to end it, and the pack keeps a weak buzz after the game
            // stops (0x0E0A to 0x0E24), so the level counts as delivered only
            // once the stop went through too.
            // The first stop on command 5 is the one the first report 17 owes,
            // before any picture. The second follows the picture's pulse.
            int stops = 0;
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeDreamcast,
                Clock = () => _now,
                RefuseWhen = r => r[0] == BlissBoxProtocol.ReportCommand
                                  && r[1] == BlissBoxProtocol.CommandSmallMotor && ++stops == 2,
            };
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            session.SetScreen(Enumerable.Repeat((byte)0x33, 192).ToArray());
            _now = 10; session.Step();
            Assert.Equal(2, stops);
            // Command 4 took the timer, so nothing ends the pulse's loop.
            Assert.Equal(4, adapter.DcTimerOwner);
            Assert.NotEqual(0, adapter.DcLoop5);
            _now = 10 + BlissBoxSession.RumbleRefreshMs; session.Step();
            Assert.Equal(0, adapter.DcLoop5);
            session.SetRumble(0, 0);
            _now += 10; session.Step();
            Assert.Equal(0, adapter.DcPower);
        }

        [Fact]
        public void AResendAskedDuringAWriteIsKept()
        {
            // The hand-off asks from the poll thread while the worker may be
            // mid-write. Cleared once the write went through, the request was
            // lost.
            BlissBoxSession session = null;
            bool asked = false;
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeDreamcast,
                DuringWrite = r =>
                {
                    if (asked || r[0] != BlissBoxProtocol.ReportCommand || r[1] != BlissBoxProtocol.CommandLargeMotor) return;
                    asked = true;
                    session.ResendMotors();
                },
            };
            session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            Assert.True(asked);
            int before = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count;
            _now = 10; session.Step();
            Assert.Equal(before + 1, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
        }

        [Fact]
        public void A3xAdapterPacesItsMotorWrites()
        {
            // Every write costs a 3.x adapter a controller poll (0x090B,
            // 0x31C6), and a pass lasts two host polls (0x35F5 to 0x364A), so
            // a game changing its rumble every frame froze the pad's input.
            // There both motors go out together at most once every 100 ms. A
            // GPA polls on and still gets each change at once.
            foreach (byte major in new byte[] { 3, 4 })
            {
                _now = 0;
                var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDualShock2, Major = major };
                var session = Session(adapter);
                for (int frame = 0; frame <= 30; frame++)
                {
                    _now = frame * 10;
                    session.SetRumble((ushort)(1000 * (frame + 1)), (ushort)(500 * (frame + 1)));
                    session.Step();
                }
                var writes = adapter.Sent
                    .Where(r => r[0] == BlissBoxProtocol.ReportCommand
                                && (r[1] == BlissBoxProtocol.CommandLargeMotor || r[1] == BlissBoxProtocol.CommandSmallMotor))
                    .ToList();
                // 3.x: at 0, 100, 200 and 300 ms, both motors each time.
                Assert.Equal(major == 3 ? 8 : 62, writes.Count);
            }
        }

        [Fact]
        public void A3xPulseBetweenTwoWritesIsStillFelt()
        {
            // Each 3.x write carries the strongest level asked for since the
            // last, so a hit that starts and ends between two writes reaches
            // the motor at the next one instead of never.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 3 };
            var session = Session(adapter);
            session.SetRumble(30000, 0);
            session.Step();
            _now = 100; session.SetRumble(0, 0); session.Step();
            _now = 120; session.SetRumble(50000, 0); session.Step();
            _now = 170; session.SetRumble(0, 0); session.Step();
            _now = 200; session.Step();
            _now = 300; session.Step();
            // A delivered pulse is not carried again.
            _now = 400; session.Step();
            _now = 500; session.Step();
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal(new byte[] { BlissBoxSession.Strength(30000), 0, BlissBoxSession.Strength(50000), 0 }, levels);
            Assert.True(session.MotorsAtRest);
        }

        [Fact]
        public void A3xSteadyLevelIsToldAgainOnceASecond()
        {
            // 3.0 counts a loop of 255 down once a poll, about 4 s (0x2500,
            // 0x0FF5, 0x29FD), so a refresh a second holds the rumble for a
            // tenth of the polls. A GPA's loop is a 255 ms timer (0x2A16), so
            // it is told every 100 ms.
            foreach (byte major in new byte[] { 3, 4 })
            {
                _now = 0;
                var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = major };
                var session = Session(adapter);
                session.SetRumble(40000, 0);
                for (int t = 0; t <= 2000; t += 100)
                {
                    _now = t;
                    session.Step();
                }
                Assert.Equal(major == 3 ? 3 : 21, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
            }
        }

        [Fact]
        public void AStopInFlightIsNotRest()
        {
            // A resend's flag clears as its write starts, so the crash path's
            // wait read the motors at rest while command 5's stop was still on
            // its way, and the dying process could cancel it.
            BlissBoxSession session = null;
            var seen = new List<bool>();
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeDualShock2,
                DuringWrite = r =>
                {
                    if (r[0] == BlissBoxProtocol.ReportCommand && r[1] == BlissBoxProtocol.CommandSmallMotor)
                        seen.Add(session.MotorsAtRest);
                },
            };
            session = Session(adapter);
            session.SetRumble(40000, 40000);
            session.Step();
            session.Forget();
            session.SetRumble(0, 0);
            seen.Clear();
            _now = 10; session.Step();
            Assert.Equal(new[] { false }, seen);
            Assert.True(session.MotorsAtRest);
        }

        [Fact]
        public async System.Threading.Tasks.Task APictureWriteGetsTimeToReachTheEeprom()
        {
            // Both firmwares store the picture before they end the transfer,
            // up to about 650 ms at 3.4 ms a byte, past the channel's 500 ms.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            var session = Session(adapter);
            session.Step();
            session.SetScreen(Enumerable.Repeat((byte)0x33, 192).ToArray());
            _now = 10; session.Step();
            var job = new BlissBoxPlayerJob(3, Enumerable.Repeat((byte)0xFF, 192).ToArray());
            session.Enqueue(job);
            _now = 2000; session.Step();
            await job.Completion;
            Assert.Equal(new[] { BlissBoxSession.ScreenWriteTimeoutMs, BlissBoxSession.ScreenWriteTimeoutMs }, adapter.ScreenTimeouts);
            Assert.True(BlissBoxSession.ScreenWriteTimeoutMs >= 192 * 34 / 10 * 2);
        }

        [Fact]
        public void AClosingPortStopsWaitingForAnAnswer()
        {
            // The native poll's reads ran all twenty on a closing port, so its
            // worker held the channel for up to twenty transfers.
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypePlayStationDigital,
                Major = 3,
                Minor = 34,
                NeverReady = true,
                Controller = m => new byte[] { 0xFF, 0x41, 0x5A, 0xFF, 0xFF },
            };
            BlissBoxSession session = null;
            session = new BlissBoxSession(adapter, adapter.Player, () => _now, ms =>
            {
                _now += ms;
                session.RequestStop();
            });
            session.NativeArrows = true;
            session.Step();
            Assert.Equal(2, adapter.NativeReads);
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
        public void A3xStopIsNotHeldBackByAPulseTheMotorAlreadyRan()
        {
            // A pulse at the running strength left the peak equal to what was
            // sent, so no write was owed and the stop waited for the hold a
            // second later, with a 3.0 N64, GameCube or PlayStation motor
            // running about 4 s more.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 3 };
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            _now = 10; session.SetRumble(0, 0);
            _now = 20; session.SetRumble(40000, 0);
            _now = 30; session.SetRumble(0, 0);
            Assert.Equal(70, session.Step());
            _now = 100; session.Step();
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal(new byte[] { BlissBoxSession.Strength(40000), 0 }, levels);
        }

        [Fact]
        public void TheCrashStopDropsAPulseNotYetSent()
        {
            // The crash path's zero left a pulse asked for since the last 3.x
            // write in place, so the motors read at rest while the worker still
            // owed a type-1 command with its loop of 0xFF, which a dying
            // process could leave running.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 3 };
            var session = Session(adapter);
            session.Step();
            _now = 120; session.SetRumble(50000, 0);
            _now = 150; session.SetRumble(0, 0);
            Assert.False(session.MotorsAtRest);
            _now = 160; session.StopRumble();
            Assert.True(session.MotorsAtRest);
            _now = 200; session.Step();
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal(new byte[] { 0 }, levels);
        }

        [Fact]
        public void TheCrashStopDropsAPulseARefusedWriteWouldPutBack()
        {
            // A stop that landed while a refused write carried a pulse found
            // the peak already taken, and the refusal put it back, so the next
            // write sent the pulse after the stop.
            BlissBoxSession session = null;
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeNintendo64,
                Major = 3,
                DuringWrite = r =>
                {
                    if (_now == 100 && r[0] == BlissBoxProtocol.ReportCommand) session.StopRumble();
                },
                RefuseWhen = r => _now == 100 && r[0] == BlissBoxProtocol.ReportCommand,
            };
            session = Session(adapter);
            session.Step();
            _now = 50; session.SetRumble(50000, 0);
            _now = 60; session.SetRumble(0, 0);
            _now = 100; session.Step();
            _now = 200; session.Step();
            _now = 300; session.Step();
            byte pulse = BlissBoxSession.Strength(50000);
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal(new byte[] { 0, pulse, 0 }, levels);
            Assert.True(session.MotorsAtRest);
        }

        [Fact]
        public void ARefusedWriteKeepsTheMotorsFromRest()
        {
            // The channel reports a transfer that outlived its wait as failed
            // although the adapter may have taken it, so the crash path read
            // rest over a refused write of 196 with a loop of 0xFF.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 3 };
            var session = Session(adapter);
            session.Step();
            adapter.RefuseWhen = r => _now == 100 && r[0] == BlissBoxProtocol.ReportCommand;
            _now = 50; session.SetRumble(50000, 0);
            _now = 100; session.Step();
            _now = 150; session.StopRumble();
            Assert.False(session.MotorsAtRest);
            _now = 200; session.Step();
            Assert.True(session.MotorsAtRest);
            Assert.Equal(0, adapter.Motor(BlissBoxProtocol.CommandLargeMotor)[^1][5]);
        }

        [Fact]
        public void AGpaTriesARefusedWriteAgainAtTheLevelItWanted()
        {
            // A GPA scheduled nothing once the wanted level matched what was
            // sent, so a refused write stayed in doubt for good.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64 };
            var session = Session(adapter);
            session.Step();
            adapter.RefuseWhen = r => _now == 10 && r[0] == BlissBoxProtocol.ReportCommand;
            _now = 10; session.SetRumble(40000, 0); session.Step();
            _now = 20; session.SetRumble(0, 0); session.Step();
            Assert.False(session.MotorsAtRest);
            Assert.Equal(90, session.Step());
            _now = 110; session.Step();
            Assert.True(session.MotorsAtRest);
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal(new byte[] { 0, BlissBoxSession.Strength(40000), 0 }, levels);
        }

        [Fact]
        public void TheCrashStopWaitsForAPulseOwedToEitherMotor()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDualShock2, Major = 3 };
            var session = Session(adapter);
            session.Step();
            _now = 120; session.SetRumble(0, 50000);
            _now = 150; session.SetRumble(0, 0);
            Assert.False(session.MotorsAtRest);
            session.StopRumble();
            Assert.True(session.MotorsAtRest);
        }

        [Fact]
        public void APulseOnANewlyFoundControllerIsSent()
        {
            // A controller change kept the old controller's sent level, so a
            // pulse at or below it on the new controller, whose motor never
            // ran, was taken as covered and never sent.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 3 };
            var session = Session(adapter);
            session.Step();
            _now = 450; session.SetRumble(45000, 0); session.Step();
            adapter.Type = 9;
            _now = 500; session.Step();
            _now = 510; session.SetRumble(30000, 0);
            _now = 520; session.SetRumble(0, 0);
            _now = 550; session.Step();
            _now = 650; session.Step();
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal(new byte[] { 0, BlissBoxSession.Strength(45000), BlissBoxSession.Strength(30000), 0 }, levels);
        }

        [Fact]
        public void APulseAskedForBeforeAControllerChangeIsNotSentToIt()
        {
            // A level asked for while a pad without motors was in the port,
            // during a search or while the channel was closed went out to the
            // next controller at full strength with the next 3.x write.
            var adapter = new ScriptedAdapter { Type = 27, Major = 3 };
            var session = Session(adapter);
            session.Step();
            session.SetRumble(65535, 65535);
            _now = 10; session.Step();
            session.SetRumble(0, 0);
            adapter.Type = BlissBoxControllers.TypeDualShock2;
            _now = 500; session.Step();
            var writes = adapter.Sent.Where(r => r[0] == BlissBoxProtocol.ReportCommand
                && (r[1] == BlissBoxProtocol.CommandLargeMotor || r[1] == BlissBoxProtocol.CommandSmallMotor)).ToList();
            Assert.Equal(2, writes.Count);
            Assert.All(writes, r => Assert.Equal(0, r[4]));
        }

        [Fact]
        public void ARefused3xWriteCarriesItsPulseAgain()
        {
            // The peaks were taken before a write the adapter refused, so the
            // pulse it carried was lost.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 3 };
            var session = Session(adapter);
            session.Step();
            adapter.RefuseWhen = r => _now == 100 && r[0] == BlissBoxProtocol.ReportCommand;
            _now = 50; session.SetRumble(50000, 0);
            _now = 60; session.SetRumble(0, 0);
            _now = 100; session.Step();
            _now = 200; session.Step();
            _now = 300; session.Step();
            byte pulse = BlissBoxSession.Strength(50000);
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal(new byte[] { 0, pulse, pulse, 0 }, levels);
        }

        [Fact]
        public void ARefused3xRefreshIsTriedAgainAtTheNextPacedWrite()
        {
            // A refused 3.x write was timed as a delivered one, so a refused
            // refresh waited a whole second, and three in a row outlast the
            // firmware's loop of about 4 s.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64, Major = 3 };
            var session = Session(adapter);
            session.SetRumble(40000, 0);
            session.Step();
            adapter.RefuseWhen = r => _now == 1000 && r[0] == BlissBoxProtocol.ReportCommand;
            for (int t = 100; t < 1000; t += 100)
            {
                _now = t;
                session.Step();
            }
            _now = 1000;
            Assert.Equal(100, session.Step());
            _now = 1100; session.Step();
            Assert.Equal(3, adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Count);
        }

        [Fact]
        public void TheQuiesceTakesNoLevelAfterIt()
        {
            // A writer that passed the engine's quiesce check before the crash
            // path set it could hand the port a level after the port's stop,
            // and the wait for rest could end before the worker sent it.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64 };
            var session = Session(adapter);
            session.Step();
            _now = 10; session.SetRumble(40000, 0); session.Step();
            _now = 20; session.Quiesce();
            Assert.False(session.SetRumble(50000, 0));
            Assert.False(session.MotorsAtRest);
            _now = 30; session.Step();
            Assert.True(session.MotorsAtRest);
            var levels = adapter.Motor(BlissBoxProtocol.CommandLargeMotor).Select(r => r[5]).ToList();
            Assert.Equal((byte)0, levels[^1]);
            Assert.DoesNotContain(BlissBoxSession.Strength(50000), levels);
        }

        [Fact]
        public void AQuiescedPortWritesNoPicture()
        {
            // A GPA runs the Dreamcast driver's command-5 routine at full power
            // before every picture write (0x2BEF to 0x2BF9), so a picture
            // written after the crash path's quiesce could start the pack again.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            var session = Session(adapter);
            session.Step();
            session.Quiesce();
            session.SetScreen(Enumerable.Range(0, 192).Select(i => (byte)i).ToArray());
            _now = 2000;
            // The picture it will not write is no reason to wake at once.
            Assert.True(session.Step() > 0);
            Assert.Equal(0, adapter.ScreenWrites);
            Assert.False(session.WriteScreenNow(new byte[192]));
            Assert.Equal(0, adapter.ScreenWrites);
        }

        [Fact]
        public void APictureWriteCountsAsInFlightUntilThePassAfterIt()
        {
            // On a GPA a picture write's pulse leaves a running jump pack at
            // full power until the pass after the write, so the crash stop
            // waits for both.
            BlissBoxSession session = null;
            bool duringPicture = false, duringResend = false;
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeDreamcast,
                DuringWrite = r =>
                {
                    if (r[0] == BlissBoxProtocol.ReportScreen) duringPicture = session.PictureInFlight;
                    else if (duringPicture && r[0] == BlissBoxProtocol.ReportCommand && r[1] == BlissBoxProtocol.CommandLargeMotor)
                        duringResend = session.PictureInFlight;
                },
            };
            session = Session(adapter);
            session.Step();
            _now = 10; session.SetRumble(40000, 0); session.Step();
            session.SetScreen(Enumerable.Range(0, 192).Select(i => (byte)i).ToArray());
            _now = 2000; session.Step();
            Assert.True(duringPicture);
            Assert.True(duringResend);
            Assert.False(session.PictureInFlight);
        }

        [Fact]
        public void AJobsPictureWriteCountsAsInFlightUntilThePassAfterIt()
        {
            BlissBoxSession session = null;
            bool duringPicture = false, duringResend = false;
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeDreamcast,
                DuringWrite = r =>
                {
                    if (r[0] == BlissBoxProtocol.ReportScreen) duringPicture = session.PictureInFlight;
                    else if (duringPicture && r[0] == BlissBoxProtocol.ReportCommand && r[1] == BlissBoxProtocol.CommandLargeMotor)
                        duringResend = session.PictureInFlight;
                },
            };
            session = Session(adapter);
            session.Step();
            _now = 10; session.SetRumble(40000, 0); session.Step();
            _now = 2000;
            Assert.True(session.WriteScreenNow(Enumerable.Range(0, 192).Select(i => (byte)i).ToArray()));
            Assert.True(duringPicture);
            Assert.True(duringResend);
            Assert.False(session.PictureInFlight);
        }

        [Fact]
        public void ARefusedPictureWriteReadsTheAdaptersPictureAgain()
        {
            // The channel reports a transfer that outlived its wait as failed
            // although the adapter may have stored the picture, so what it
            // holds is no longer known.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, RefuseScreen = true };
            var session = Session(adapter);
            session.Step();
            Assert.Equal(1, adapter.ScreenReads);
            session.SetScreen(Enumerable.Range(0, 192).Select(i => (byte)i).ToArray());
            _now = 2000; session.Step();
            Assert.Equal(1, adapter.ScreenWrites);
            Assert.Null(session.StoredScreen);
            _now = 2100; session.Step();
            Assert.Equal(2, adapter.ScreenReads);
        }

        [Fact]
        public void APictureTheAdapterStoredDespiteARefusalIsReadBack()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, StoreThenRefuseScreen = true };
            var session = Session(adapter);
            session.Step();
            var picture = Enumerable.Range(0, 192).Select(i => (byte)i).ToArray();
            session.SetScreen(picture);
            _now = 2000; session.Step();
            _now = 2100; session.Step();
            Assert.Equal(picture, session.StoredScreen);
        }

        [Fact]
        public void AJobsRefusedPictureWriteReadsTheAdaptersPictureAgain()
        {
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, RefuseScreen = true };
            var session = Session(adapter);
            session.Step();
            _now = 2000;
            Assert.False(session.WriteScreenNow(Enumerable.Range(0, 192).Select(i => (byte)i).ToArray()));
            Assert.Null(session.StoredScreen);
        }

        [Fact]
        public void ThePictureGuardCountsFromTheWriteItself()
        {
            // The step's start comes before its reads and its first motor
            // pass, so a guard stamped with it let the next write follow
            // sooner than a second after the last.
            bool slow = false;
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast };
            adapter.DuringWrite = r =>
            {
                if (slow && r[0] == BlissBoxProtocol.ReportCommand && r[1] == BlissBoxProtocol.CommandLargeMotor)
                {
                    slow = false;
                    _now += 400;
                }
            };
            var session = Session(adapter);
            session.Step();
            session.SetScreen(Enumerable.Range(0, 192).Select(i => (byte)i).ToArray());
            slow = true;
            _now = 1000; session.SetRumble(40000, 0); session.Step();
            Assert.Equal(1, adapter.ScreenWrites);
            session.SetScreen(Enumerable.Range(0, 192).Select(i => (byte)(i ^ 0x55)).ToArray());
            _now = 2100; session.Step();
            Assert.Equal(1, adapter.ScreenWrites);
            _now = 2450; session.Step();
            Assert.Equal(2, adapter.ScreenWrites);
        }

        [Fact]
        public void AFreshPortIsNotAtRestUntilItsFirstStopIsOut()
        {
            // A port that opened during the crash stop's wait read as at rest
            // before its first report 17, although its first identification
            // owes both motors their levels.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64 };
            var session = Session(adapter);
            Assert.False(session.MotorsAtRest);
            session.Step();
            Assert.True(session.MotorsAtRest);
        }

        [Fact]
        public void AQuiescedPortReadsNoPicture()
        {
            // The crash path wakes a quiesced port every 5 ms, and on 3.x each
            // picture read costs three controller polls.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeDreamcast, Major = 3 };
            var session = Session(adapter);
            session.Quiesce();
            session.Step();
            Assert.Equal(0, adapter.ScreenReads);
        }

        [Fact]
        public void AGpaReadsAtRestOnceItsStopIsOut()
        {
            // Only a 3.x write takes the peaks, so a GPA would keep every level
            // ever asked of it there and never read at rest.
            var adapter = new ScriptedAdapter { Type = BlissBoxControllers.TypeNintendo64 };
            var session = Session(adapter);
            session.Step();
            _now = 10; session.SetRumble(40000, 0); session.Step();
            _now = 20; session.SetRumble(0, 0); session.Step();
            Assert.True(session.MotorsAtRest);
        }

        [Fact]
        public async System.Threading.Tasks.Task APakJobOnAClosingPortEndsAsClosed()
        {
            // Talk answers null once the port starts closing, which the pak
            // check, a block's last read and a restore at its error limit took
            // for no answer or a bad block. A 3.x answer takes one wait each:
            // the status and the check's read are the first two.
            Assert.Equal(BlissBoxJobError.Closed, await CloseDuringWait(new BlissBoxPakBackupJob(), new Pak(), 1));
            Assert.Equal(BlissBoxJobError.Closed, await CloseDuringWait(new BlissBoxPakBackupJob(), new Pak(), 2));
            // The check's read and the block's first two fail their CRC, and
            // the port closes during the block's third and last.
            Assert.Equal(BlissBoxJobError.Closed, await CloseDuringWait(new BlissBoxPakBackupJob(), new Pak { BadReads = 3 }, 5));
            // Sixteen rejected writes, and the port closes during the next,
            // which would be one error past the limit.
            Assert.Equal(BlissBoxJobError.Closed, await CloseDuringWait(
                new BlissBoxPakRestoreJob(new byte[BlissBoxControllerPak.PakBytes]), new Pak { RejectedWrites = 16 }, 2 + 17));
        }

        /// <summary>Runs a pak job on a 3.x adapter whose port starts closing
        /// in the given wait between reads, after which no answer comes back
        /// ready.</summary>
        private async System.Threading.Tasks.Task<BlissBoxJobError> CloseDuringWait(BlissBoxJob job, Pak pak, int wait)
        {
            var adapter = new ScriptedAdapter
            {
                Type = BlissBoxControllers.TypeNintendo64, Major = 3, Minor = 34, Controller = pak.Answer,
            };
            int waits = 0;
            BlissBoxSession session = null;
            session = new BlissBoxSession(adapter, adapter.Player, () => _now, ms =>
            {
                _now += ms;
                if (++waits != wait) return;
                adapter.NeverReady = true;
                session.RequestStop();
            });
            session.Step();
            session.Enqueue(job);
            session.Step();
            return (await job.Completion).Error;
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
