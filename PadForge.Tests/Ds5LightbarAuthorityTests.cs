using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Regression guards for the DS5 output report's authority bytes:
    /// validFlag2 (byte 38), lightFadeAnimation (byte 41, the field this
    /// codebase calls "lightbarSetup"), the mic-light enable bit, and the
    /// AllowAudioMute bit.
    ///
    /// <para>Every assertion here corresponds to a defect reported on
    /// hardware 2026-08-01: PadForge's own lightbar colour would not hold
    /// with nothing else running, and an external program's lightbar
    /// colour, brightness and microphone state would not hold either.
    /// Both devolved to a pitch-black bar.</para>
    ///
    /// <para>Ground truth for the byte layout is duaLib's
    /// dataStructures.h (DualSenseY-v2/thirdparty/duaLib), whose
    /// bitfield names byte 38 bit 0 AllowLightBrightnessChange, bit 1
    /// AllowColorLightFadeAnimation, bit 2 EnableImprovedRumbleEmulation,
    /// and bits 3-7 "UNKBITC : 5; // unused". OpenRGB's
    /// SonyDualSenseController.cpp maps onto the same struct: usb_buf[39]
    /// and BT buffer[41] are byte 38, BT buffer[44] is byte 41, and
    /// usb_buf[43] / BT buffer[45] are byte 42.</para>
    /// </summary>
    public class Ds5LightbarAuthorityTests
    {
        // duaLib LightFadeAnimation: Nothing = 0, FadeIn = 1 (black to
        // blue), FadeOut = 2 (blue to black).
        private const byte FadeNothing = 0x00;
        private const byte FadeOut = 0x02;

        private const byte AllowLightBrightnessChange = 0x01;
        private const byte AllowColorLightFadeAnimation = 0x02;
        private const byte EnableImprovedRumbleEmulation = 0x04;

        private static byte Vf2(System.Collections.Generic.Dictionary<string, object> f)
            => (byte)f["validFlag2"];

        private static byte Setup(System.Collections.Generic.Dictionary<string, object> f)
            => (byte)f["lightbarSetup"];

        private static byte Vf1(System.Collections.Generic.Dictionary<string, object> f)
            => (byte)f["validFlag1"];

        // An "idle" pad: no player number so the identity floor is
        // disarmed, no configured mode, nothing external. PadForge
        // authors nothing, so it must claim nothing.
        private static DeviceSlotConfig IdleConfig() => new DeviceSlotConfig();

        // ── The pitch-black cause ──
        //
        // lightbarSetup is LightFadeAnimation. It shipped defaulting to
        // 0x02 (FadeOut, "from blue to black") under the label "bypass BT
        // default blue", which is what a ONE-SHOT FadeOut does. PadForge
        // sends this report every dispatch, so with byte 38 bit 1 also
        // asserted it was a standing fade-to-black order and the bar
        // obeyed. Both references send FadeOut exactly once at connect
        // and only over Bluetooth: duaLib at enumeration (duaLib.cpp:915,
        // absent from its steady loop) and OpenRGB in its BT branch alone
        // (SonyDualSenseController.cpp:69, absent from the USB branch).

        [Fact]
        public void FadeAnimation_DefaultsToNothing_NeverFadeOut()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 3);
            Assert.Equal(FadeNothing, Setup(fields));
            Assert.NotEqual(FadeOut, Setup(fields));
        }

        [Fact]
        public void FadeAnimation_StaysNothing_AcrossEveryLightbarOwnershipState()
        {
            // Authoring the identity floor.
            Assert.Equal(FadeNothing,
                Setup(Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 3)));

            // Mirroring an external writer inside their grace window.
            var mirroring = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarRgb = new byte[] { 0x40, 0x00, 0x00 },
                LightbarEverExternal = true,
            };
            Assert.Equal(FadeNothing,
                Setup(Ds5EffectSynthesizer.BuildFields(
                    IdleConfig(), overrides: mirroring, playerNumber: 3)));

            // Stood down after their grace window closed.
            var stoodDown = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LastLightbarRgb = new byte[] { 0x40, 0x00, 0x00 },
                LightbarEverExternal = true,
            };
            Assert.Equal(FadeNothing,
                Setup(Ds5EffectSynthesizer.BuildFields(
                    IdleConfig(), overrides: stoodDown, playerNumber: 3)));
        }

        [Fact]
        public void FadeAnimation_CarriesAnExternalWritersOwnRequestVerbatim()
        {
            // A writer that genuinely wants a fade still gets one. Only
            // PadForge's unconditional default is gone.
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarSetup = FadeOut,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), overrides: ov, playerNumber: 3);
            Assert.Equal(FadeOut, Setup(fields));
        }

        // ── validFlag2 is composed per bit, never a blanket 0xFF ──

        [Fact]
        public void ValidFlag2_IsNeverBlanketFF()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 3);
            Assert.NotEqual((byte)0xFF, Vf2(fields));
        }

        [Fact]
        public void ValidFlag2_SetsNoUndefinedBits()
        {
            // duaLib declares bits 3-7 unused. Setting them was noise at
            // best; it is what "matched OpenRGB exactly" was really doing.
            const byte defined = AllowLightBrightnessChange
                | AllowColorLightFadeAnimation
                | EnableImprovedRumbleEmulation;

            var cases = new[]
            {
                Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 3),
                Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 0),
                Ds5EffectSynthesizer.BuildFields(
                    IdleConfig(),
                    overrides: new UserEffectsDispatcher.ExternalSubsystemOverrides
                    {
                        LightbarSetup = FadeOut,
                        LedBrightness = 1,
                    },
                    playerNumber: 3),
            };
            foreach (var f in cases)
                Assert.Equal(0, Vf2(f) & ~defined);
        }

        [Fact]
        public void ValidFlag2_AlwaysCarriesImprovedRumble()
        {
            // The old 0xFF set this bit too. Rumble behaviour is not in
            // scope for the lightbar fix, so it stays unconditional.
            Assert.Equal(EnableImprovedRumbleEmulation,
                Vf2(Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 3))
                    & EnableImprovedRumbleEmulation);
            Assert.Equal(EnableImprovedRumbleEmulation,
                Vf2(Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 0))
                    & EnableImprovedRumbleEmulation);
        }

        [Fact]
        public void ValidFlag2_ClaimsFadeControl_OnlyWhenExternalAsksForAFade()
        {
            var idle = Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 3);
            Assert.Equal(0, Vf2(idle) & AllowColorLightFadeAnimation);

            var asked = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(),
                overrides: new UserEffectsDispatcher.ExternalSubsystemOverrides
                {
                    LightbarSetup = FadeOut,
                },
                playerNumber: 3);
            Assert.Equal(AllowColorLightFadeAnimation,
                Vf2(asked) & AllowColorLightFadeAnimation);
        }

        [Fact]
        public void ValidFlag2_ClaimsBrightness_WhileAuthoringTheBar()
        {
            // Player 3 with an untouched config arms the identity floor,
            // which authors the bar. Claiming brightness alongside it is
            // what keeps a hot-plugged pad from locking dark.
            var authoring = Ds5EffectSynthesizer.BuildFields(IdleConfig(), playerNumber: 3);
            Assert.Equal(AllowLightBrightnessChange,
                Vf2(authoring) & AllowLightBrightnessChange);
        }

        [Fact]
        public void ValidFlag2_ReleasesBrightness_WhenPadForgeAuthorsNothing()
        {
            // The 2nd reported failure. Holding this bit every tick wrote
            // our own byte 42 over an external program's brightness 1.5 s
            // after their grace window closed, so theirs could never
            // stick. duaLib clears the same bit whenever brightness is
            // unchanged (duaLib.cpp:576).
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LastLightbarRgb = new byte[] { 0x40, 0x00, 0x00 },
                LightbarEverExternal = true,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), overrides: ov, playerNumber: 3);
            Assert.Equal(0, Vf2(fields) & AllowLightBrightnessChange);
        }

        [Fact]
        public void ValidFlag2_ClaimsBrightness_WhileMirroringTheirs()
        {
            // Mirroring only works if the gate is open for the byte we
            // are mirroring.
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LedBrightness = 1,
                LastLightbarRgb = new byte[] { 0x40, 0x00, 0x00 },
                LightbarEverExternal = true,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), overrides: ov, playerNumber: 3);
            Assert.Equal(AllowLightBrightnessChange,
                Vf2(fields) & AllowLightBrightnessChange);
            Assert.Equal((byte)1, (byte)fields["ledBrightness"]);
        }

        // ── The stand-down still carries their colour, not zeros ──

        [Fact]
        public void StandDown_CarriesTheLastExternalColour_NotZeros()
        {
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LastLightbarRgb = new byte[] { 0x40, 0x00, 0x00 },
                LightbarEverExternal = true,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), overrides: ov, playerNumber: 3);
            Assert.Equal(new byte[] { 0x40, 0x00, 0x00 }, (byte[])fields["lightbar"]);
        }

        // ── Mic light and AllowAudioMute are callable-gated ──
        //
        // validFlag1 bit 0 = mic light enable, bit 1 = AllowAudioMute.

        private const byte EnableMicLight = 0x01;
        private const byte EnableAudioMuteControl = 0x02;
        private const byte EnableMicVolume = 0x40;        // validFlag0 bit 6
        private const byte MicVolumeMax = 0x40;

        [Fact]
        public void MicLight_IsAssertedWhenTheCallerWantsIt()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertMicLightEnable: true);
            Assert.Equal(EnableMicLight, Vf1(fields) & EnableMicLight);
        }

        [Fact]
        public void MicLight_ReleasesWhenTheCallerStandsDown()
        {
            // Held every tick, this bit republished our MicLedMode over
            // an external program's mic-LED state once their grace window
            // closed, which is the "microphone doesn't hold" half of the
            // 2nd reported failure.
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertMicLightEnable: false);
            Assert.Equal(0, Vf1(fields) & EnableMicLight);
        }

        [Fact]
        public void AudioMuteControl_IsAssertedDuringTheClaimBurst()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertAudioHardwareClaim: true);
            Assert.Equal(EnableAudioMuteControl, Vf1(fields) & EnableAudioMuteControl);
        }

        // ── Player indicator: the pips are a mirrored subsystem too ──
        //
        // validFlag1 bit 4 gates byte 43. Held every tick it republished
        // our pips over an external writer's once their grace window
        // closed, which is what made a ds.daidr.me player-number change
        // snap back. SDL3 writes this row for any game that calls
        // SDL_SetGamepadPlayerIndex, and Steam Input assigns it too, so
        // the passthrough is a real case and not a curiosity.

        private const byte EnablePlayerIndicator = 0x10;

        [Fact]
        public void PlayerIndicator_IsAssertedWhenTheCallerWantsIt()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertPlayerIndicatorEnable: true);
            Assert.Equal(EnablePlayerIndicator, Vf1(fields) & EnablePlayerIndicator);
        }

        [Fact]
        public void PlayerIndicator_ReleasesWhenTheCallerStandsDown()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertPlayerIndicatorEnable: false);
            Assert.Equal(0, Vf1(fields) & EnablePlayerIndicator);
        }

        [Fact]
        public void PlayerIndicator_StillMirrorsAnExternalValueVerbatim()
        {
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                PlayerIndicator = 0x20 | 0x1F,
                PlayerIndicatorEverExternal = true,
            };
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), overrides: ov, playerNumber: 3,
                assertPlayerIndicatorEnable: true);
            Assert.Equal((byte)(0x20 | 0x1F), (byte)fields["playerIndicator"]);
        }

        // ── The gate that decides all four mirrored subsystems ──

        [Fact]
        public void Gate_AssertsWhilePadForgeAuthors()
        {
            var g = UserEffectsDispatcher.GateMirroredSubsystem(
                padForgeWants: true, externalMirroring: false, prevPadForgeWanted: false);
            Assert.True(g.Assert);
            Assert.True(g.NextPrev);
        }

        [Fact]
        public void Gate_AssertsWhileMirroringSoTheirBytesLand()
        {
            var g = UserEffectsDispatcher.GateMirroredSubsystem(
                padForgeWants: false, externalMirroring: true, prevPadForgeWanted: false);
            Assert.True(g.Assert);
        }

        [Fact]
        public void Gate_FiresExactlyOneTransitionFrameWhenPadForgeStopsWanting()
        {
            // User flips MicLedMode Solid to Off, or an AT mode to Off.
            var frame1 = UserEffectsDispatcher.GateMirroredSubsystem(
                padForgeWants: false, externalMirroring: false, prevPadForgeWanted: true);
            Assert.True(frame1.Assert);      // the disengage packet
            Assert.False(frame1.NextPrev);

            var frame2 = UserEffectsDispatcher.GateMirroredSubsystem(
                padForgeWants: false, externalMirroring: false, prevPadForgeWanted: frame1.NextPrev);
            Assert.False(frame2.Assert);     // and then silence
        }

        [Fact]
        public void Gate_NeverFiresATransitionFrameWhenOnlyTheMirrorEnded()
        {
            // THE defect. prev was fed `padForgeWants || externalMirroring`,
            // so the first frame after an external writer's grace window
            // expired looked like a PadForge transition and shipped a
            // packet carrying OUR value over the one they had just set.
            // The mic LED went dark and an external AT engagement was
            // released, both ~1.5 s after the writer stopped refreshing.
            bool prev = false;

            // Three frames of mirroring: PadForge wants nothing of its own.
            for (int i = 0; i < 3; i++)
            {
                var g = UserEffectsDispatcher.GateMirroredSubsystem(
                    padForgeWants: false, externalMirroring: true, prevPadForgeWanted: prev);
                Assert.True(g.Assert);
                prev = g.NextPrev;
            }

            // Their grace window closes. Every frame from here must stay
            // silent so the firmware retains what they set.
            for (int i = 0; i < 10; i++)
            {
                var g = UserEffectsDispatcher.GateMirroredSubsystem(
                    padForgeWants: false, externalMirroring: false, prevPadForgeWanted: prev);
                Assert.False(g.Assert, $"frame {i} after the mirror ended re-claimed the subsystem");
                prev = g.NextPrev;
            }
        }

        [Fact]
        public void Gate_OwnershipClaimSeedSendsExactlyOneStopFrame()
        {
            // A fresh claim seeds prev TRUE so a prior owner's latched
            // rumble / AT / LED state gets a mandatory stop packet. That
            // must not become a standing claim.
            var frame1 = UserEffectsDispatcher.GateMirroredSubsystem(
                padForgeWants: false, externalMirroring: false, prevPadForgeWanted: true);
            Assert.True(frame1.Assert);
            var frame2 = UserEffectsDispatcher.GateMirroredSubsystem(
                padForgeWants: false, externalMirroring: false, prevPadForgeWanted: frame1.NextPrev);
            Assert.False(frame2.Assert);
        }

        // ── Mic hardware volume (byte 6), the missing half ──
        //
        // Owner report 2026-08-01: the pad's mic was quiet, recovered on a
        // controller power cycle, and no PadForge change explained it. Every
        // Sony profile DECLARES micVolume, but PadForge never wrote it and
        // never asserted validFlag0 bit 6, so the pad kept whatever gain its
        // last owner left and nothing here could raise it. duaLib documents
        // the byte as "not linier, seems to max at 64, 0 is not fully muted",
        // which is why a zeroed register reads as QUIET rather than silent.
        //
        // Owner ruling: the hardware register stays at maximum. Attenuation
        // belongs to Windows and our own gain stage.

        [Fact]
        public void MicVolume_IsAlwaysCarriedAtMaximum()
        {
            // Written on EVERY packet, claim or not, so a stray enable bit
            // can never apply a zero from an undeclared field.
            foreach (bool claim in new[] { true, false })
            {
                var f = Ds5EffectSynthesizer.BuildFields(
                    IdleConfig(), playerNumber: 3, assertAudioHardwareClaim: claim);
                Assert.Equal(MicVolumeMax, (byte)f["micVolume"]);
            }
        }

        [Fact]
        public void MicVolume_EnableRidesTheClaim_NotEveryTick()
        {
            // Same retain-on-idle rule as every other authority bit. duaLib
            // asserts AllowMicVolume on a change or a reconnect and clears it
            // otherwise (duaLib.cpp:613).
            var claimed = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertAudioHardwareClaim: true);
            var idle = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertAudioHardwareClaim: false);

            Assert.Equal(EnableMicVolume, (byte)claimed["validFlag0"] & EnableMicVolume);
            Assert.Equal(0, (byte)idle["validFlag0"] & EnableMicVolume);
        }

        [Fact]
        public void MicVolume_AndUnmute_RideTheSameClaim()
        {
            // Both are 'put this pad's audio hardware into a known-good state
            // when we take it'. They must not drift apart.
            var f = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertAudioHardwareClaim: true);
            Assert.Equal(EnableAudioMuteControl, Vf1(f) & EnableAudioMuteControl);
            Assert.Equal(EnableMicVolume, (byte)f["validFlag0"] & EnableMicVolume);
        }

        [Fact]
        public void AudioMuteControl_ReleasesAfterTheBurst()
        {
            // Asserting AllowAudioMute over the zero-filled MuteControl
            // byte is an unmute. Held forever it reversed any deliberate
            // external mute within one dispatch frame. duaLib asserts it
            // in letGo alone (duaLib.cpp:180), never in its steady loop.
            var fields = Ds5EffectSynthesizer.BuildFields(
                IdleConfig(), playerNumber: 3, assertAudioHardwareClaim: false);
            Assert.Equal(0, Vf1(fields) & EnableAudioMuteControl);
        }
    }
}
