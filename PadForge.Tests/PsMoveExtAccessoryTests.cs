using System;
using PadForge.Common.Input;
using SDL3;
using Xunit;

namespace PadForge.Tests
{
    // The PS Move's EXT accessories (hifihedgehog/SDL#33 item 34): the Sharp
    // Shooter and the Racing Wheel. Each byte position is the one the
    // accessory's config tells the Move to merge into its input report
    // (moveonpc wiki, Extension devices, Sharp Shooter and Racing Wheel), in
    // the BT frame's numbering, where report offset N is frame byte N + 1.
    // The feature report bytes are psmoveapi's (psmove_get_ext_device_info,
    // psmove_send_ext_data) behind the Bluetooth HID headers bluepad32 and
    // the DS3 lane use.
    [Collection("SettingsManagerStatics")]
    public class PsMoveExtAccessoryTests
    {
        private static byte[] Frame()
        {
            var f = new byte[PsMoveDirectService.Zcm1BtReportSize];
            f[0] = 0xA1;
            f[1] = 0x01;
            return f;
        }

        private static uint Buttons(byte[] f) => PsMoveDirectService.DecodeButtons(f[2], f[3], f[4], f[5]);

        [Fact]
        public void TheExtBit_IsReportByteFourBitFour()
        {
            var f = Frame();
            Assert.False(PsMoveDirectService.ExtAttached(f));
            f[5] = 0x10;
            Assert.True(PsMoveDirectService.ExtAttached(f));
            // The sequence nibble below it and the Move and T bits above it
            // are not the EXT bit.
            f[5] = 0xCF;
            Assert.False(PsMoveDirectService.ExtAttached(f));
            // Nor does the EXT bit leak into the button word.
            f[5] = 0x10;
            Assert.Equal(0u, Buttons(f));
            Assert.False(PsMoveDirectService.ExtAttached(new byte[5]));
        }

        [Fact]
        public void TheIdRead_IsPsmoveapisSetupThenAGetReport()
        {
            byte[] setup = PsMoveDirectService.BuildExtInfoReadSetup();
            Assert.Equal(50, setup.Length);   // SET_REPORT header + the 49-byte report
            Assert.Equal(new byte[] { 0x53, 0xE0, 0x01, 0xA0, 0x00, 0xFF }, setup[..6]);
            Assert.All(setup[6..], v => Assert.Equal(0, v));
            Assert.Equal(new byte[] { 0x43, 0xE0 }, PsMoveDirectService.BuildExtInfoGetReport());
        }

        [Fact]
        public void TheIdReply_IsReadFromReportBytesNineAndTen()
        {
            // The wiki's Racing Wheel config starts 81 01, its Sharp Shooter's 80 81.
            var reply = new byte[50];
            reply[0] = 0xA3;
            reply[1] = 0xE0;
            reply[10] = 0x81;
            reply[11] = 0x01;
            Assert.Equal(0x8101, PsMoveDirectService.ParseExtInfoReply(reply, 50));
            reply[10] = 0x80;
            reply[11] = 0x81;
            Assert.Equal(0x8081, PsMoveDirectService.ParseExtInfoReply(reply, 50));

            // A handshake, another report, a short reply and a length past the
            // buffer are not the ID.
            Assert.Equal(-1, PsMoveDirectService.ParseExtInfoReply(new byte[] { 0x00 }, 1));
            reply[1] = 0xF2;
            Assert.Equal(-1, PsMoveDirectService.ParseExtInfoReply(reply, 50));
            reply[1] = 0xE0;
            Assert.Equal(-1, PsMoveDirectService.ParseExtInfoReply(reply, 11));
            Assert.Equal(-1, PsMoveDirectService.ParseExtInfoReply(reply, 51));
        }

        /// <summary>A control channel: the SET's handshake, then a queued
        /// handshake and the reply, as BthPS3's L2CAP queue hands them back.</summary>
        private sealed class ControlChannel
        {
            public int Writes, Reads;
            private int _reply;

            public bool Write(byte[] report)
            {
                Writes++;
                return true;
            }

            public int Read(byte[] buf)
            {
                Reads++;
                Array.Clear(buf);
                if (Writes == 1) return 1; // the SET's handshake, result 0
                if (_reply++ == 0) return 1; // a handshake still queued
                buf[0] = 0xA3;
                buf[1] = 0xE0;
                buf[10] = 0x81;
                buf[11] = 0x01;
                return 50;
            }
        }

        [Fact]
        public void TheIdExchange_ReturnsTheId_SkippingQueuedHandshakes()
        {
            var channel = new ControlChannel();
            int id = PsMoveDirectService.ExchangeExtDeviceId(channel.Write, channel.Read, () => false);
            Assert.Equal(0x8101, id);
            Assert.Equal(2, channel.Writes);
            Assert.Equal(3, channel.Reads);
        }

        [Fact]
        public void TheIdExchange_StartsNoTransferOnceItsDeadlinePasses()
        {
            // CancelIoEx aborts only I/O already pending, so a read that
            // started after the one cancel waited with nothing to end it.
            // Past the deadline, or once a teardown lowered the run flag, the
            // exchange starts nothing: here the deadline passes after the SET.
            var channel = new ControlChannel();
            int id = PsMoveDirectService.ExchangeExtDeviceId(channel.Write, channel.Read, () => channel.Writes >= 1);
            Assert.Equal(-1, id);
            Assert.Equal(1, channel.Writes);
            Assert.Equal(0, channel.Reads);

            // Expired from the start: not even the SET goes out.
            var idle = new ControlChannel();
            Assert.Equal(-1, PsMoveDirectService.ExchangeExtDeviceId(idle.Write, idle.Read, () => true));
            Assert.Equal(0, idle.Writes);

            // Past the deadline between the GET and its reply: no read.
            var late = new ControlChannel();
            Assert.Equal(-1, PsMoveDirectService.ExchangeExtDeviceId(late.Write, late.Read, () => late.Writes >= 2));
            Assert.Equal(2, late.Writes);
            Assert.Equal(1, late.Reads);
        }

        [Fact]
        public void TheIds_NameTheTwoAccessories()
        {
            Assert.Equal(PsMoveDirectService.ExtAccessory.SharpShooter, PsMoveDirectService.AccessoryFromId(0x8081));
            Assert.Equal(PsMoveDirectService.ExtAccessory.RacingWheel, PsMoveDirectService.AccessoryFromId(0x8101));
            Assert.Equal(PsMoveDirectService.ExtAccessory.Unknown, PsMoveDirectService.AccessoryFromId(0x1234));
        }

        /// <summary>Without the ID, the wheel's paddle byte gives it away: its
        /// config reports bits 0x3C in it at all times, and the Sharp Shooter
        /// never writes that byte.</summary>
        [Theory]
        [InlineData(0x3C, true)]
        [InlineData(0x3F, true)]
        [InlineData(0x3D, true)]
        [InlineData(0x00, false)]
        [InlineData(0x1C, false)]
        public void WithoutTheId_ThePaddleByteTellsTheAccessory(int paddleByte, bool wheel)
        {
            Assert.Equal(wheel ? PsMoveDirectService.ExtAccessory.RacingWheel : PsMoveDirectService.ExtAccessory.SharpShooter,
                PsMoveDirectService.AccessoryFromData((byte)paddleByte));
        }

        [Fact]
        public void TheWheel_DecodesFromTheMovesBytesAndTheExtBytes()
        {
            var f = Frame();
            f[2] = 0x10 | 0x80;   // report 0x01: D-pad up and left
            f[3] = 0x04 | 0x08;   // report 0x02: L1 and R1
            f[45] = 200;          // report 0x2C: throttle
            f[46] = 17;           // report 0x2D: L2
            f[47] = 255;          // report 0x2E: R2
            f[48] = 0x3C | 0x02;  // report 0x2F: right paddle, and the bits always set
            var w = PsMoveDirectService.DecodeRacingWheel(f, Buttons(f));
            Assert.True(w.L1);
            Assert.True(w.R1);
            Assert.True(w.Up);
            Assert.True(w.Left);
            Assert.False(w.Down);
            Assert.False(w.Right);
            Assert.False(w.LeftPaddle);
            Assert.True(w.RightPaddle);
            Assert.Equal(17, w.L2);
            Assert.Equal(255, w.R2);
            Assert.Equal(200, w.Throttle);

            f[2] = 0x20 | 0x40;
            f[48] = 0x3C | 0x01;
            w = PsMoveDirectService.DecodeRacingWheel(f, Buttons(f));
            Assert.True(w.Right);
            Assert.True(w.Down);
            Assert.True(w.LeftPaddle);
            Assert.False(w.RightPaddle);
        }

        [Fact]
        public void TheSharpShooter_DecodesItsOneByte()
        {
            var f = Frame();
            f[45] = 0x02;   // weapon 2 selected
            var g = PsMoveDirectService.DecodeSharpShooter(f);
            Assert.False(g.Weapon1);
            Assert.True(g.Weapon2);
            Assert.False(g.Weapon3);
            Assert.False(g.Fire);
            Assert.False(g.Reload);

            f[45] = 0x04 | 0x40 | 0x80;   // weapon 3, trigger pulled, reload held
            g = PsMoveDirectService.DecodeSharpShooter(f);
            Assert.True(g.Weapon3);
            Assert.True(g.Fire);
            Assert.True(g.Reload);
        }

        [Fact]
        public void TheWheelsMotors_AreControlByte0x20ThenRightThenLeft()
        {
            byte[] o = PsMoveDirectService.BuildWheelRumble(left: 0x40, right: 0x80);
            Assert.Equal(50, o.Length);
            Assert.Equal(new byte[] { 0x53, 0xE0, 0x00, 0xA0, 0x20, 0x02 }, o[..6]);
            Assert.Equal(0x80, o[10]);
            Assert.Equal(0x40, o[11]);
            Assert.All(o[12..], v => Assert.Equal(0, v));
        }

        /// <summary>SDL numbers a virtual gamepad's buttons in enum order over
        /// the mask. The Move's own pad posts its Move button at 7, the index
        /// the bench proved, and the helper agrees.</summary>
        [Fact]
        public void VirtualButtonIndices_FollowTheMaskInEnumOrder()
        {
            Assert.Equal(7, PsMoveDirectService.VirtualButtonIndex(0x047F, SDL.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER));

            uint wheel = PsMoveDirectService.WheelButtonMask;
            Assert.Equal(0, PsMoveDirectService.VirtualButtonIndex(wheel, SDL.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER));
            Assert.Equal(1, PsMoveDirectService.VirtualButtonIndex(wheel, SDL.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER));
            Assert.Equal(2, PsMoveDirectService.VirtualButtonIndex(wheel, SDL.SDL_GAMEPAD_BUTTON_DPAD_UP));
            Assert.Equal(5, PsMoveDirectService.VirtualButtonIndex(wheel, SDL.SDL_GAMEPAD_BUTTON_DPAD_RIGHT));
            Assert.Equal(6, PsMoveDirectService.VirtualButtonIndex(wheel, SDL.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1));
            Assert.Equal(7, PsMoveDirectService.VirtualButtonIndex(wheel, SDL.SDL_GAMEPAD_BUTTON_LEFT_PADDLE1));
            Assert.Equal(8, System.Numerics.BitOperations.PopCount(wheel));

            uint shooter = PsMoveDirectService.ShooterButtonMask;
            Assert.Equal(0, PsMoveDirectService.VirtualButtonIndex(shooter, SDL.SDL_GAMEPAD_BUTTON_WEST));
            Assert.Equal(1, PsMoveDirectService.VirtualButtonIndex(shooter, SDL.SDL_GAMEPAD_BUTTON_MISC2));
            Assert.Equal(3, PsMoveDirectService.VirtualButtonIndex(shooter, SDL.SDL_GAMEPAD_BUTTON_MISC4));
            Assert.Equal(4, System.Numerics.BitOperations.PopCount(shooter));
        }

        /// <summary>The wheel's triggers are joystick axes 0 and 1 and its
        /// throttle axis 6, where PadForge reads a gamepad's extra axes.</summary>
        [Fact]
        public void TheWheelsAxes_AreTheTriggersThenAxisSix()
        {
            Assert.Equal((1u << SDL.SDL_GAMEPAD_AXIS_LEFT_TRIGGER) | (1u << SDL.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER),
                PsMoveDirectService.WheelAxisMask);
            Assert.Equal(6, PsMoveDirectService.WheelThrottleAxis);
            Assert.Equal(1u << SDL.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, PsMoveDirectService.ShooterAxisMask);
        }
    }
}
