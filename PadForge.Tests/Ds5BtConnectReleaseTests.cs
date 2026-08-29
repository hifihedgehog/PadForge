using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #334, the second report: a DualSense powered on over Bluetooth sat on
    /// its firmware default blue and ignored every color PadForge wrote,
    /// whatever the Lighting tab said, until an app restart happened to
    /// re-open the device.
    ///
    /// <para>Over BT the pad's wireless firmware owns the LEDs at connect
    /// and keeps running its own animation until the host RELEASES them:
    /// validFlag1 bit 3, duaLib's ResetLights, "Release the LEDs from
    /// Wireless firmware control" (dataStructures.h:259), together with a
    /// flag2-gated FadeOut of the default animation. Both references send
    /// exactly that once per BT connect and never in the steady loop:
    /// duaLib at enumeration (duaLib.cpp:198-218, gated DUALSENSE +
    /// HID_API_BUS_BLUETOOTH), OpenRGB in its Bluetooth branch alone
    /// ("bypass default blue color when connected to bluetooth",
    /// SonyDualSenseController.cpp:68).</para>
    ///
    /// <para>PadForge had been delivering the release BY ACCIDENT since the
    /// Lighting tab shipped: the standing every-tick FadeOut that d4c011f5
    /// correctly removed (it was also a standing fade-to-black order) was
    /// doubling as the connect release, and d4c011f5 removed it without
    /// adding the one-shot the references use. Worked for many releases,
    /// broke in 4.2.0, exactly the owner's recollection.</para>
    /// </summary>
    public class Ds5BtConnectReleaseTests
    {
        private const byte ResetLightsBit = 0x08;              // validFlag1 bit 3
        private const byte AllowColorLightFadeAnimation = 0x02; // validFlag2 bit 1
        private const byte FadeOut = 0x02;
        private const byte FadeIn = 0x01;

        private static byte Vf1(System.Collections.Generic.Dictionary<string, object> f)
            => (byte)f["validFlag1"];
        private static byte Vf2(System.Collections.Generic.Dictionary<string, object> f)
            => (byte)f["validFlag2"];
        private static byte Setup(System.Collections.Generic.Dictionary<string, object> f)
            => (byte)f["lightbarSetup"];

        private static DeviceSlotConfig OffConfig() => new DeviceSlotConfig
        {
            LightbarMode = LightbarMode.Off,
        };

        /// <summary>THE FIX. The one-shot carries all three halves of the
        /// release in one packet: the LED handoff bit, the fade-animation
        /// gate, and the fade-out of the default blue.</summary>
        [Fact]
        public void ConnectRelease_CarriesResetLightsFadeGateAndFadeOut()
        {
            var f = Ds5EffectSynthesizer.BuildFields(
                OffConfig(), playerNumber: 1, btConnectRelease: true);
            Assert.Equal(ResetLightsBit, (byte)(Vf1(f) & ResetLightsBit));
            Assert.Equal(AllowColorLightFadeAnimation, (byte)(Vf2(f) & AllowColorLightFadeAnimation));
            Assert.Equal(FadeOut, Setup(f));
        }

        /// <summary>The one-shot also RELEASES THE TRIGGERS (#350). Idle
        /// retention leaves the trigger enable bits clear so an external
        /// writer's effect survives, and that same retention preserved a
        /// STALE adaptive-trigger effect across reconnects and PadForge
        /// restarts: the pad stayed stiff until a profile switch happened
        /// to send a release. The connect frame's trigger bytes are the
        /// cfg encode (Off ships zeros), so asserting the enables here is
        /// the release, in the same packet as the light handoff.</summary>
        [Fact]
        public void ConnectRelease_ReleasesTheTriggersToo()
        {
            const byte EnableRightTrigger = 0x04;   // validFlag0 bit 2
            const byte EnableLeftTrigger = 0x08;    // validFlag0 bit 3

            // The dispatcher's idle gates pass assert*TriggerEnable false
            // (retention). The one-shot must override them.
            var f = Ds5EffectSynthesizer.BuildFields(
                OffConfig(), playerNumber: 1,
                assertRightTriggerEnable: false, assertLeftTriggerEnable: false,
                btConnectRelease: true);
            byte vf0 = (byte)f["validFlag0"];
            Assert.Equal(EnableRightTrigger, vf0 & EnableRightTrigger);
            Assert.Equal(EnableLeftTrigger, vf0 & EnableLeftTrigger);
            // And the payload is a RELEASE: mode byte 0 on both triggers.
            Assert.Equal(0, ((byte[])f["rightTriggerEffect"])[0]);
            Assert.Equal(0, ((byte[])f["leftTriggerEffect"])[0]);

            // The steady idle loop keeps retention: no enables, so an
            // external writer's live effect is preserved.
            f = Ds5EffectSynthesizer.BuildFields(
                OffConfig(), playerNumber: 1,
                assertRightTriggerEnable: false, assertLeftTriggerEnable: false,
                btConnectRelease: false);
            vf0 = (byte)f["validFlag0"];
            Assert.Equal(0, vf0 & EnableRightTrigger);
            Assert.Equal(0, vf0 & EnableLeftTrigger);
        }

        /// <summary>THE d4c011f5 INVARIANT, restated: the steady loop sends
        /// none of it. A standing release is the standing fade-to-black that
        /// commit removed.</summary>
        [Fact]
        public void SteadyLoop_SendsNoReleaseAndNoFade()
        {
            var f = Ds5EffectSynthesizer.BuildFields(
                OffConfig(), playerNumber: 1, btConnectRelease: false);
            Assert.Equal(0, Vf1(f) & ResetLightsBit);
            Assert.Equal(0, Vf2(f) & AllowColorLightFadeAnimation);
            Assert.Equal(0x00, Setup(f));
        }

        /// <summary>The configured color rides the SAME packet as the
        /// release, so there is no blue-then-black-then-color staircase:
        /// the firmware hands the LEDs over and applies our bytes in one
        /// step. Off paints black, which is the second reporter's exact
        /// configuration.</summary>
        [Fact]
        public void ConnectRelease_StillCarriesTheConfiguredBar()
        {
            var f = Ds5EffectSynthesizer.BuildFields(
                OffConfig(), playerNumber: 1, btConnectRelease: true);
            Assert.Equal(0x04, Vf1(f) & 0x04);   // lightbar enable
            var rgb = (byte[])f["lightbar"];
            Assert.Equal(new byte[] { 0, 0, 0 }, rgb);
        }

        /// <summary>An external writer's own fade request wins the byte if
        /// both land on one tick; the gate bit serves either.</summary>
        [Fact]
        public void ExternalFadeRequest_WinsTheByteOverTheOneShot()
        {
            var ov = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                LightbarSetup = FadeIn,
            };
            var f = Ds5EffectSynthesizer.BuildFields(
                OffConfig(), overrides: ov, playerNumber: 1, btConnectRelease: true);
            Assert.Equal(FadeIn, Setup(f));
            Assert.Equal(AllowColorLightFadeAnimation, (byte)(Vf2(f) & AllowColorLightFadeAnimation));
        }
    }
}
