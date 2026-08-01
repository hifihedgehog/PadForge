using System.IO;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Output Path picker (DS5 output report byte 7 bits 4-5,
    /// OutputPathSelect, gated by validFlag0 bit 7 AllowAudioControl).
    ///
    /// <para>Enum values 1-4 map to firmware paths 0-3, duaLib's scePad
    /// names: SCE_PAD_AUDIO_PATH_STEREO_HEADSET (0),
    /// MONO_LEFT_HEADSET (1), MONO_LEFT_HEADSET_AND_SPEAKER (2),
    /// ONLY_SPEAKER (3). Automatic (0) authors nothing: byte ships 0 with
    /// the enable bit clear, and the #83 macro-speaker routing stays the
    /// byte's owner, exactly the pre-feature behaviour.</para>
    ///
    /// <para>An explicit user path OWNS the byte: the dispatcher's
    /// macro-speaker block keeps only its speaker-volume half while a
    /// path is forced. That precedence lives in the dispatcher and is
    /// documented there; these tests pin the synthesizer surface.</para>
    /// </summary>
    public class Ds5AudioOutputPathTests
    {
        private const byte EnableAudioControl = 0x80;   // validFlag0 bit 7

        private static System.Collections.Generic.Dictionary<string, object> Build(
            int path, bool assert1)
            => Ds5EffectSynthesizer.BuildFields(
                new DeviceSlotConfig(), playerNumber: 1,
                audioOutputPath: path, assertAudioControl: assert1);

        // ── Enum-to-firmware mapping (byte 7 bits 4-5) ──

        [Theory]
        [InlineData((int)AudioOutputPath.StereoHeadset, 0x00)]      // path 0
        [InlineData((int)AudioOutputPath.MonoHeadset, 0x10)]        // path 1
        [InlineData((int)AudioOutputPath.HeadsetAndSpeaker, 0x20)]  // path 2
        [InlineData((int)AudioOutputPath.SpeakerOnly, 0x30)]        // path 3
        public void EnumMapsToFirmwarePathBits(int enumVal, byte expected)
        {
            Assert.Equal(expected, (byte)Build(enumVal, false)["audioControlFlags"]);
        }

        [Fact]
        public void Automatic_ShipsZeroAndNeverAssertsTheBit()
        {
            var fields = Build((int)AudioOutputPath.Automatic, assert1: true);
            Assert.Equal((byte)0, (byte)fields["audioControlFlags"]);
            // Even an asserting caller cannot claim byte 7 for Automatic:
            // there is nothing authored to protect.
            Assert.Equal(0, (byte)fields["validFlag0"] & EnableAudioControl);
        }

        [Fact]
        public void EnableBit_FollowsTheParameter_ForForcedPaths()
        {
            Assert.Equal(0,
                (byte)Build((int)AudioOutputPath.SpeakerOnly, false)["validFlag0"]
                    & EnableAudioControl);
            Assert.Equal(EnableAudioControl,
                (byte)Build((int)AudioOutputPath.SpeakerOnly, true)["validFlag0"]
                    & EnableAudioControl);
        }

        // ── Config surface ──

        [Fact]
        public void DefaultIsAutomatic_AndResetRestoresIt()
        {
            var cfg = new DeviceSlotConfig();
            Assert.Equal(AudioOutputPath.Automatic, cfg.AudioOutputPath);
            cfg.AudioOutputPath = AudioOutputPath.SpeakerOnly;
            cfg.ResetAudioOutputPathCommand.Execute(null);
            Assert.Equal(AudioOutputPath.Automatic, cfg.AudioOutputPath);
        }

        [Fact]
        public void LegacyXmlWithoutTheAttribute_LoadsAsAutomatic()
        {
            var ser = new XmlSerializer(typeof(DeviceSlotConfigData));
            using var rd = new StringReader(
                "<DeviceSlotConfigData SlotIndex=\"0\" />");
            var dto = (DeviceSlotConfigData)ser.Deserialize(rd);
            Assert.Equal(AudioOutputPath.Automatic, dto.AudioOutputPath);
        }

        [Fact]
        public void EnumOrdinals_ArePinned_TheyPersistNumerically()
        {
            // Combo index binding (EnumIndexConverter) AND XML persistence
            // both ride these ordinals. APPEND-ONLY.
            Assert.Equal(0, (int)AudioOutputPath.Automatic);
            Assert.Equal(1, (int)AudioOutputPath.StereoHeadset);
            Assert.Equal(2, (int)AudioOutputPath.MonoHeadset);
            Assert.Equal(3, (int)AudioOutputPath.HeadsetAndSpeaker);
            Assert.Equal(4, (int)AudioOutputPath.SpeakerOnly);
        }
    }
}
