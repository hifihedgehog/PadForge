using System.IO;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #271 item 1: the DualSense authored-haptics render on actuator
    /// devices. The DSP is the existing HapticToneReducer chain; what is
    /// new and testable here is the channel extraction that bridges the
    /// persona's interleaved multi-channel window into the sinks'
    /// stereo buffers, and the per-(slot, device) setting surface.
    /// </summary>
    public class PersonaHapticsRendererTests
    {
        // ── ExtractStereoPairs ──

        private static byte[] Window4Ch(params (short spkL, short spkR, short hapL, short hapR)[] frames)
        {
            var b = new byte[frames.Length * 8];
            for (int i = 0; i < frames.Length; i++)
            {
                void W(int off, short v) { b[i * 8 + off] = (byte)(v & 0xFF); b[i * 8 + off + 1] = (byte)((v >> 8) & 0xFF); }
                W(0, frames[i].spkL); W(2, frames[i].spkR); W(4, frames[i].hapL); W(6, frames[i].hapR);
            }
            return b;
        }

        [Fact]
        public void Extract_PullsTheHapticPair_NotTheSpeakerPair()
        {
            // Distinct values per channel so a swapped or shifted offset
            // cannot produce the same bytes.
            var pcm = Window4Ch((100, 200, 300, 400), (101, 201, 301, 401));
            byte[] scratch = null;
            int pairs = HapticToneService.ExtractStereoPairs(pcm, stride: 8, lOff: 4, rOff: 6, ref scratch);
            Assert.Equal(2, pairs);
            short L0 = (short)(scratch[0] | (scratch[1] << 8));
            short R0 = (short)(scratch[2] | (scratch[3] << 8));
            short L1 = (short)(scratch[4] | (scratch[5] << 8));
            short R1 = (short)(scratch[6] | (scratch[7] << 8));
            Assert.Equal(300, L0);
            Assert.Equal(400, R0);
            Assert.Equal(301, L1);
            Assert.Equal(401, R1);
        }

        [Fact]
        public void Extract_DropsATruncatedFinalFrame()
        {
            var full = Window4Ch((1, 2, 3, 4), (5, 6, 7, 8));
            var truncated = new byte[full.Length - 3];
            System.Array.Copy(full, truncated, truncated.Length);
            byte[] scratch = null;
            int pairs = HapticToneService.ExtractStereoPairs(truncated, 8, 4, 6, ref scratch);
            Assert.Equal(1, pairs);
        }

        [Fact]
        public void Extract_FailsClosedOnBadGeometry()
        {
            var pcm = Window4Ch((1, 2, 3, 4));
            byte[] scratch = null;
            Assert.Equal(0, HapticToneService.ExtractStereoPairs(pcm, 0, 4, 6, ref scratch));
            Assert.Equal(0, HapticToneService.ExtractStereoPairs(pcm, 8, -1, 6, ref scratch));
            Assert.Equal(0, HapticToneService.ExtractStereoPairs(pcm, 8, 4, -1, ref scratch));
            // Offsets past the stride would read the NEXT frame's bytes.
            Assert.Equal(0, HapticToneService.ExtractStereoPairs(pcm, 8, 4, 8, ref scratch));
            Assert.Equal(0, HapticToneService.ExtractStereoPairs(System.ReadOnlySpan<byte>.Empty, 8, 4, 6, ref scratch));
        }

        [Fact]
        public void Extract_GrowsTheScratchAndReusesIt()
        {
            byte[] scratch = new byte[1];
            var pcm = Window4Ch((1, 2, 3, 4), (5, 6, 7, 8), (9, 10, 11, 12));
            int pairs = HapticToneService.ExtractStereoPairs(pcm, 8, 4, 6, ref scratch);
            Assert.Equal(3, pairs);
            Assert.True(scratch.Length >= 12);
            var same = scratch;
            HapticToneService.ExtractStereoPairs(pcm, 8, 4, 6, ref scratch);
            Assert.Same(same, scratch);
        }

        // ── Setting surface ──

        [Fact]
        public void DeviceSlotConfigData_Defaults_AreOffAndUnity()
        {
            var d = new DeviceSlotConfigData();
            Assert.False(d.AudioPersonaHapticsEnabled);
            Assert.Equal(100, d.AudioPersonaHapticsGain);
        }

        [Fact]
        public void DeviceSlotConfigData_RoundTripsThroughXml()
        {
            var d = new DeviceSlotConfigData { AudioPersonaHapticsEnabled = true, AudioPersonaHapticsGain = 250 };
            var ser = new XmlSerializer(typeof(DeviceSlotConfigData));
            using var sw = new StringWriter();
            ser.Serialize(sw, d);
            using var sr = new StringReader(sw.ToString());
            var back = (DeviceSlotConfigData)ser.Deserialize(sr);
            Assert.True(back.AudioPersonaHapticsEnabled);
            Assert.Equal(250, back.AudioPersonaHapticsGain);
        }

        [Fact]
        public void DeviceSlotConfig_Vm_Defaults_MatchTheDto()
        {
            var vm = new DeviceSlotConfig();
            Assert.False(vm.AudioPersonaHapticsEnabled);
            Assert.Equal(100, vm.AudioPersonaHapticsGain);
        }
    }
}
