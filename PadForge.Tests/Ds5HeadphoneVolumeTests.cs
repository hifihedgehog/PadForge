using System;
using System.IO;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Headphone jack hardware volume (DS5 output report byte 4,
    /// VolumeHeadphones, gated by validFlag0 bit 4 AllowHeadphoneVolume).
    ///
    /// <para>Ceiling settled from source: duaLib's scePad mapping writes
    /// <c>headsetVolume + 64</c>, so Sony's own runtime uses the
    /// 0x40..0x7F window and the struct comment agrees ("max 0x7f").
    /// DS4Windows-hbashton's 0x4F cap sits inside that window and was
    /// rejected as needlessly low. dualsense-tester writes the byte raw
    /// (slider 0-255), so the byte itself accepts anything; the window is
    /// about matching Sony's semantics, not device tolerance.</para>
    ///
    /// <para>The VALUE ships on every packet, like micVolume, so a stray
    /// enable bit can never apply HM's zero-fill. The ENABLE bit is
    /// change/claim-gated by the dispatcher, per duaLib.cpp:613-621 and
    /// the retain-on-idle rule in Ds5LightbarAuthorityTests.</para>
    /// </summary>
    public class Ds5HeadphoneVolumeTests
    {
        private const byte EnableHeadphoneVolume = 0x10;   // validFlag0 bit 4

        private static System.Collections.Generic.Dictionary<string, object> Build(
            int pct, bool assert1)
            => Ds5EffectSynthesizer.BuildFields(
                new DeviceSlotConfig(), playerNumber: 1,
                headphoneVolumePercent: pct, assertHeadphoneVolume: assert1);

        // ── The percent-to-byte window ──

        [Theory]
        [InlineData(0, 0x00)]     // 0% is the one value outside the window
        [InlineData(1, 0x40)]     // window floor, Sony's scePad minimum
        [InlineData(50, 0x5F)]    // 0x40 + 50*63/100
        [InlineData(100, 0x7F)]   // window ceiling, duaLib "max 0x7f"
        [InlineData(150, 0x7F)]   // clamped
        public void PercentMapsOntoSonysScePadWindow(int pct, byte expected)
        {
            Assert.Equal(expected, (byte)Build(pct, false)["headphoneVolume"]);
        }

        // ── Value always ships, enable bit only when asked ──

        [Fact]
        public void ValueShipsOnEveryPacket_EvenUnasserted()
        {
            Assert.True(Build(100, false).ContainsKey("headphoneVolume"));
        }

        [Fact]
        public void EnableBit_FollowsTheParameter()
        {
            Assert.Equal(0,
                (byte)Build(100, false)["validFlag0"] & EnableHeadphoneVolume);
            Assert.Equal(EnableHeadphoneVolume,
                (byte)Build(100, true)["validFlag0"] & EnableHeadphoneVolume);
        }

        [Fact]
        public void AudioHardwareClaim_DoesNotImplyTheHeadphoneBit()
        {
            // The claim burst covers mute + mic volume. Headphone gating is
            // the DISPATCHER's job (claim burst OR config change), so the
            // synthesizer must not couple them: a claim with no headphone
            // assert leaves the bit clear.
            var fields = Ds5EffectSynthesizer.BuildFields(
                new DeviceSlotConfig(), playerNumber: 1,
                assertAudioHardwareClaim: true, assertHeadphoneVolume: false);
            Assert.Equal(0, (byte)fields["validFlag0"] & EnableHeadphoneVolume);
        }

        // ── Config surface ──

        [Fact]
        public void ConfigClampsToPercentRange()
        {
            var cfg = new DeviceSlotConfig();
            Assert.Equal(100, cfg.HeadphoneVolume);   // default: full
            cfg.HeadphoneVolume = 150;
            Assert.Equal(100, cfg.HeadphoneVolume);
            cfg.HeadphoneVolume = -5;
            Assert.Equal(0, cfg.HeadphoneVolume);
        }

        [Fact]
        public void ResetCommandRestoresFull()
        {
            var cfg = new DeviceSlotConfig { HeadphoneVolume = 30 };
            cfg.ResetHeadphoneVolumeCommand.Execute(null);
            Assert.Equal(100, cfg.HeadphoneVolume);
        }

        [Fact]
        public void LegacyXmlWithoutTheAttribute_LoadsAsFullVolume()
        {
            // Pre-feature configs must not arrive at 0 (which would quiet
            // the jack); the DTO initializer is the migration.
            var ser = new XmlSerializer(typeof(DeviceSlotConfigData));
            using var rd = new StringReader(
                "<DeviceSlotConfigData SlotIndex=\"0\" />");
            var dto = (DeviceSlotConfigData)ser.Deserialize(rd);
            Assert.Equal(100, dto.HeadphoneVolume);
        }

        // ── Macro actions: append-only enum tail ──

        [Fact]
        public void MacroActionValues_ArePinnedAtTheTail()
        {
            // APPEND-ONLY contract: these numbers are persisted in user
            // XML. Reordering the enum corrupts every saved macro.
            Assert.Equal(51, (int)MacroActionType.AxisScale);
            Assert.Equal(52, (int)MacroActionType.HeadphoneVolumeUp);
            Assert.Equal(53, (int)MacroActionType.HeadphoneVolumeDown);
        }

        [Fact]
        public void MacroStep_ClampsAtBothEnds()
        {
            // The dispatch step is ±10 clamped 0..100; the config setter is
            // the clamp, so stepping past an end sticks at the end.
            var cfg = new DeviceSlotConfig { HeadphoneVolume = 95 };
            cfg.HeadphoneVolume = Math.Clamp(cfg.HeadphoneVolume + 10, 0, 100);
            Assert.Equal(100, cfg.HeadphoneVolume);
            cfg.HeadphoneVolume = 5;
            cfg.HeadphoneVolume = Math.Clamp(cfg.HeadphoneVolume - 10, 0, 100);
            Assert.Equal(0, cfg.HeadphoneVolume);
        }
    }
}
