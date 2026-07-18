using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The PID force feedback LFE lane (#236 follow-up): every feedback
    /// source PadForge decodes feeds the bass shakers, which for Extended
    /// wheel slots means the game-authored pair HMaestroFfbDecoder.Apply
    /// computes from the uploaded PID effect set. Pins the decoder's
    /// LastComputedMotors tap and the descriptor probe the Bass Shakers
    /// tab gate rides.
    /// </summary>
    public class RumbleAudioFfbLaneTests
    {
        // ── LastComputedMotors mirrors Apply's game-authored pair ──

        [Fact]
        public void ConstantForce_Start_ExposesComputedPair()
        {
            var dec = new HMaestroFfbDecoder(null);

            // Set Effect: [EBI=1, type=0x01 Constant, duration=0xFFFF
            // (PID infinite)]. Short (<21 byte) report takes the
            // full-gain centered-direction default.
            dec.OnHidOutput(0x11, new byte[] { 1, 0x01, 0xFF, 0xFF });
            // Set Constant Force: [EBI=1, magnitude=+10000 canonical].
            dec.OnHidOutput(0x15, new byte[] { 1, 0x10, 0x27 });
            // Effect Operation: [EBI=1, EFF_START, loop=1].
            dec.OnHidOutput(0x1A, new byte[] { 1, 1, 1 });

            var vib = new Vibration();
            dec.Apply(vib);

            var (left, right) = dec.LastComputedMotors;
            // The tap must mirror exactly what Apply wrote to the motors.
            Assert.Equal(vib.LeftMotorSpeed, left);
            Assert.Equal(vib.RightMotorSpeed, right);
            // Full-magnitude centered constant force splits evenly:
            // 10000 * 0.5 scale * 65535/10000 = 32767 per motor.
            Assert.InRange(left, 32000, 33000);
            Assert.InRange(right, 32000, 33000);
        }

        [Fact]
        public void EffectStop_ZeroesComputedPair()
        {
            var dec = new HMaestroFfbDecoder(null);
            dec.OnHidOutput(0x11, new byte[] { 1, 0x01, 0xFF, 0xFF });
            dec.OnHidOutput(0x15, new byte[] { 1, 0x10, 0x27 });
            dec.OnHidOutput(0x1A, new byte[] { 1, 1, 1 });
            dec.Apply(new Vibration());
            Assert.NotEqual((ushort)0, dec.LastComputedMotors.Left);

            // Effect Operation EFF_STOP: the pack must fall silent with
            // the motors, never latch the last nonzero pair.
            dec.OnHidOutput(0x1A, new byte[] { 1, 3 });
            var vib = new Vibration();
            dec.Apply(vib);

            Assert.Equal((ushort)0, vib.LeftMotorSpeed);
            Assert.Equal(((ushort)0, (ushort)0), dec.LastComputedMotors);
        }

        [Fact]
        public void DeviceGain_ScalesComputedPair()
        {
            var dec = new HMaestroFfbDecoder(null);
            dec.OnHidOutput(0x11, new byte[] { 1, 0x01, 0xFF, 0xFF });
            dec.OnHidOutput(0x15, new byte[] { 1, 0x10, 0x27 });
            dec.OnHidOutput(0x1A, new byte[] { 1, 1, 1 });
            // Device Gain report 0x1D: half gain.
            dec.OnHidOutput(0x1D, new byte[] { 128 });

            dec.Apply(new Vibration());
            var (left, _) = dec.LastComputedMotors;
            Assert.InRange(left, 15500, 17000);
        }

        // ── The descriptor probe the Extended tab gate rides ──

        [Fact]
        public void PidBlockProbe_MatchesSetEffectCollection()
        {
            // AddPidFfbBlock-built and hand-authored descriptors both
            // carry Usage(Set Effect Report) + Collection(Logical).
            Assert.True(HMaestroVirtualController.DescriptorHasPidFfbBlock(
                "05010905a101050f0921a102c0c0"));
            // Case-insensitive: profile JSONs vary in hex casing.
            Assert.True(HMaestroVirtualController.DescriptorHasPidFfbBlock(
                "050F0921A102C0"));
            // A plain gamepad descriptor has no PID block.
            Assert.False(HMaestroVirtualController.DescriptorHasPidFfbBlock(
                "05010905a101a100c0c0"));
            Assert.False(HMaestroVirtualController.DescriptorHasPidFfbBlock(null));
            Assert.False(HMaestroVirtualController.DescriptorHasPidFfbBlock(""));
        }
    }
}
