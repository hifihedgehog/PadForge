using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The DualSense rumble bits on PadForge's 30 Hz Sony pass
    /// (#434). While a game drives the pad through the pass-through lane,
    /// the pass carries the game's last rumble bits and motor bytes
    /// verbatim, including a frame that cleared every rumble bit, which
    /// is how SDL3 stops rumble (SDL_hidapi_ps5.c
    /// HIDAPI_DriverPS5_UpdateEffects: "Leaving emulated rumble bits off
    /// will restore audio haptics"). While PadForge authors rumble it
    /// asserts its established set, haptics select plus both emulations,
    /// the pattern DS4Windows ships (DualSenseDevice.cs). An idle frame
    /// claims nothing. The virtual pad's motor gate follows the same
    /// release rule so no consumer keeps a stale motor pair.</summary>
    public class Ds5RumbleModeTests
    {
        private const byte Legacy = 0x01;          // validFlag0 bit 0
        private const byte HapticsSelect = 0x02;   // validFlag0 bit 1
        private const byte Improved = 0x04;        // validFlag2 bit 2
        private const byte RumbleMask0 = Legacy | HapticsSelect;

        private static DeviceSlotConfig IdleConfig() => new DeviceSlotConfig();
        private static byte Vf0(System.Collections.Generic.Dictionary<string, object> f) => (byte)f["validFlag0"];
        private static byte Vf2(System.Collections.Generic.Dictionary<string, object> f) => (byte)f["validFlag2"];

        private static byte[] GamePayload(byte vf0, byte vf2, byte right, byte left)
        {
            var p = new byte[47];
            p[0] = vf0;
            p[2] = right;
            p[3] = left;
            p[38] = vf2;
            return p;
        }

        // ── PadForge-authored rumble asserts its established set ──

        [Fact]
        public void OwnRumble_AssertsHapticsSelectAndBothEmulations()
        {
            var f = Ds5EffectSynthesizer.BuildFields(IdleConfig(),
                rumbleRight: 40, rumbleLeft: 60, assertRumbleEnable: true);
            Assert.Equal(HapticsSelect | Legacy, Vf0(f) & RumbleMask0);
            Assert.Equal(Improved, Vf2(f) & Improved);
            Assert.Equal((byte)40, (byte)f["rightMotor"]);
            Assert.Equal((byte)60, (byte)f["leftMotor"]);
        }

        [Fact]
        public void IdleFrame_ClaimsNoRumbleMode()
        {
            var f = Ds5EffectSynthesizer.BuildFields(IdleConfig(), assertRumbleEnable: false);
            Assert.Equal(0, Vf0(f) & RumbleMask0);
            Assert.Equal(0, Vf2(f) & Improved);
        }

        [Fact]
        public void OwnRumble_StopSequence_FlaggedZeroThenUnclaimedIdle()
        {
            var on = Ds5EffectSynthesizer.BuildFields(IdleConfig(), rumbleRight: 90, rumbleLeft: 90,
                assertRumbleEnable: true);
            var stop = Ds5EffectSynthesizer.BuildFields(IdleConfig(), rumbleRight: 0, rumbleLeft: 0,
                assertRumbleEnable: true);
            var idle = Ds5EffectSynthesizer.BuildFields(IdleConfig(), rumbleRight: 0, rumbleLeft: 0,
                assertRumbleEnable: false);
            Assert.Equal(HapticsSelect | Legacy, Vf0(on) & RumbleMask0);
            Assert.Equal(Improved, Vf2(on) & Improved);
            Assert.Equal(HapticsSelect | Legacy, Vf0(stop) & RumbleMask0);
            Assert.Equal(Improved, Vf2(stop) & Improved);
            Assert.Equal((byte)0, (byte)stop["rightMotor"]);
            Assert.Equal(0, Vf0(idle) & RumbleMask0);
            Assert.Equal(0, Vf2(idle) & Improved);
        }

        // ── A game's rumble is mirrored with the game's own bits ──

        [Fact]
        public void Mirrored_LegacyOnlyWriter_KeepsLegacyOnly()
        {
            // GTA V Enhanced's packets: validFlag0 0x0D (legacy rumble plus
            // both triggers), no haptics-select, no improved bit.
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                RumbleRight = 200, RumbleLeft = 100, RumbleFlag0Bits = Legacy, RumbleFlag2Bits = 0,
            };
            var f = Ds5EffectSynthesizer.BuildFields(IdleConfig(),
                rumbleRight: 0, rumbleLeft: 0, assertRumbleEnable: false, overrides: ov);
            Assert.Equal(Legacy, Vf0(f) & RumbleMask0);
            Assert.Equal(0, Vf2(f) & Improved);
            Assert.Equal((byte)200, (byte)f["rightMotor"]);
            Assert.Equal((byte)100, (byte)f["leftMotor"]);
        }

        [Fact]
        public void Mirrored_ImprovedWriter_KeepsImprovedAndHapticsSelect()
        {
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                RumbleRight = 10, RumbleLeft = 20, RumbleFlag0Bits = HapticsSelect, RumbleFlag2Bits = Improved,
            };
            var f = Ds5EffectSynthesizer.BuildFields(IdleConfig(),
                rumbleRight: 90, rumbleLeft: 90, assertRumbleEnable: true, overrides: ov);
            Assert.Equal(HapticsSelect, Vf0(f) & RumbleMask0);
            Assert.Equal(Improved, Vf2(f) & Improved);
            Assert.Equal((byte)10, (byte)f["rightMotor"]);
            Assert.Equal((byte)20, (byte)f["leftMotor"]);
        }

        [Fact]
        public void Mirrored_Release_CarriesNoBitsAndZeroMotors()
        {
            // The game cleared its rumble bits. The pass must not put any
            // of PadForge's own back, even while PadForge has rumble of
            // its own pending, because the pad is under the game's writer.
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                RumbleRight = 0, RumbleLeft = 0, RumbleFlag0Bits = 0, RumbleFlag2Bits = 0,
            };
            var f = Ds5EffectSynthesizer.BuildFields(IdleConfig(),
                rumbleRight: 90, rumbleLeft: 90, assertRumbleEnable: true, overrides: ov);
            Assert.Equal(0, Vf0(f) & RumbleMask0);
            Assert.Equal(0, Vf2(f) & Improved);
            Assert.Equal((byte)0, (byte)f["rightMotor"]);
            Assert.Equal((byte)0, (byte)f["leftMotor"]);
        }

        // ── The mirror captures the writer's state, claims and releases ──

        [Fact]
        public void Mirror_CapturesLegacyBitAndMotors()
        {
            const int pad = 13;
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0D, 0x00, 120, 80));
            var ov = UserEffectsDispatcher.PeekExternalOverrides(pad);
            Assert.True(ov.RumbleRight.HasValue && ov.RumbleLeft.HasValue);
            Assert.Equal((byte)120, ov.RumbleRight.Value);
            Assert.Equal((byte)80, ov.RumbleLeft.Value);
            Assert.Equal(Legacy, ov.RumbleFlag0Bits);
            Assert.Equal(0, ov.RumbleFlag2Bits);
        }

        [Fact]
        public void Mirror_CapturesImprovedBitAlone()
        {
            const int pad = 14;
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x00, Improved, 5, 6));
            var ov = UserEffectsDispatcher.PeekExternalOverrides(pad);
            Assert.True(ov.RumbleRight.HasValue);
            Assert.Equal(0, ov.RumbleFlag0Bits);
            Assert.Equal(Improved, ov.RumbleFlag2Bits);
        }

        [Fact]
        public void Mirror_FlaggedZero_IsAStop()
        {
            const int pad = 15;
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0D, 0x00, 200, 200));
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0D, 0x00, 0, 0));
            var ov = UserEffectsDispatcher.PeekExternalOverrides(pad);
            Assert.Equal((byte)0, ov.RumbleRight.Value);
            Assert.Equal((byte)0, ov.RumbleLeft.Value);
            Assert.Equal(Legacy, ov.RumbleFlag0Bits);
        }

        [Fact]
        public void Mirror_FlagsCleared_AfterClaim_IsARelease()
        {
            // SDL3's own stop: motors zero, every rumble bit clear. The
            // trigger bits stay set here the way a trigger-streaming game
            // sends them. The mirror holds the release, not the last
            // nonzero pair.
            const int pad = 12;
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0D, 0x00, 77, 66));
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0C, 0x00, 0, 0));
            var ov = UserEffectsDispatcher.PeekExternalOverrides(pad);
            Assert.True(ov.RumbleRight.HasValue && ov.RumbleLeft.HasValue);
            Assert.Equal((byte)0, ov.RumbleRight.Value);
            Assert.Equal((byte)0, ov.RumbleLeft.Value);
            Assert.Equal(0, ov.RumbleFlag0Bits);
            Assert.Equal(0, ov.RumbleFlag2Bits);
        }

        [Fact]
        public void Mirror_FlagsCleared_AfterClaim_IgnoresStaleMotorBytes()
        {
            // A release frame may still carry the old motor bytes. The
            // firmware ignores them without the bits, and so does the mirror.
            const int pad = 11;
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0D, 0x00, 77, 66));
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0C, 0x00, 77, 66));
            var ov = UserEffectsDispatcher.PeekExternalOverrides(pad);
            Assert.Equal((byte)0, ov.RumbleRight.Value);
            Assert.Equal((byte)0, ov.RumbleLeft.Value);
        }

        [Fact]
        public void Mirror_FlaglessFrames_WithoutAClaim_ClaimNothing()
        {
            // A game streaming trigger effects while its rumble is off
            // never puts rumble under the mirror, so PadForge's own rumble
            // (audio, macros, tests) keeps flowing to the pad.
            const int pad = 10;
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0C, 0x00, 0, 0));
            UserEffectsDispatcher.NotifyExternalSubsystems(pad, GamePayload(0x0C, 0x00, 0, 0));
            var ov = UserEffectsDispatcher.PeekExternalOverrides(pad);
            Assert.False(ov.RumbleRight.HasValue);
            Assert.False(ov.RumbleLeft.HasValue);
        }

        // ── The virtual pad's motor gate follows the same release rule ──

        [Fact]
        public void RumbleClaim_ReadsBothFlagBytes()
        {
            Assert.True(HMaestroVirtualController.SonyRumbleClaimed((byte)0x0D, 0x03, (byte)0x00));
            Assert.True(HMaestroVirtualController.SonyRumbleClaimed((byte)0x02, 0x03, null));
            Assert.True(HMaestroVirtualController.SonyRumbleClaimed((byte)0x00, 0x03, (byte)Improved));
            Assert.True(HMaestroVirtualController.SonyRumbleClaimed(null, 0x03, (byte)Improved));
            Assert.False(HMaestroVirtualController.SonyRumbleClaimed((byte)0x0C, 0x03, (byte)0x00));
            Assert.False(HMaestroVirtualController.SonyRumbleClaimed((byte)0x0C, 0x03, null));
            Assert.False(HMaestroVirtualController.SonyRumbleClaimed(null, 0x03, null));
            // Wrong field types never claim.
            Assert.False(HMaestroVirtualController.SonyRumbleClaimed(1, 0x03, 4));
        }

        [Fact]
        public void FrameTrust_IsLengthAndCrcOnly()
        {
            Assert.True(HMaestroVirtualController.SonyFrameValid(48, 48, true));
            Assert.True(HMaestroVirtualController.SonyFrameValid(257, 78, true));
            Assert.False(HMaestroVirtualController.SonyFrameValid(47, 48, true));
            Assert.False(HMaestroVirtualController.SonyFrameValid(48, 48, false));
            Assert.False(HMaestroVirtualController.SonyFrameValid(257, -1, true));
        }

        [Fact]
        public void MotorsValid_StillRequiresAClaim()
        {
            // The composed predicate the older gate tests pin keeps its
            // meaning: trusted frame AND a rumble claim.
            Assert.True(HMaestroVirtualController.SonyMotorsValid(48, 48, true, (byte)0x01, 0x03));
            Assert.False(HMaestroVirtualController.SonyMotorsValid(48, 48, true, (byte)0x0C, 0x03));
            Assert.False(HMaestroVirtualController.SonyMotorsValid(47, 48, true, (byte)0x01, 0x03));
        }
    }
}
