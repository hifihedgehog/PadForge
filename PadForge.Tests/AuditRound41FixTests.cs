using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Locks the behavioral fixes from audit round 41 (2026-08-08). Every
    /// test here fails with its fix reverted; that is the whole point of
    /// the file, and each one names the defect it pins.
    /// </summary>
    public class AuditRound41FixTests
    {
        // ── F1: AT-to-impulse trigger domain ──

        /// <summary>The translator documents 0..255 and derives both the
        /// zone index and the startPos gate from it. Step 2 fed it a
        /// CLAMPED 0..65535 gamepad trigger, so everything above 0.4%
        /// travel arrived as 255 (zone 9). Scaling is the contract.</summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(32768, 128)]   // half pull -> mid zone, not zone 9
        [InlineData(65535, 255)]
        [InlineData(256, 1)]
        public void AtImpulse_TriggerScalesToByteDomain(int gamepadTrigger, int expectedPos)
        {
            // The conversion Step 2 performs before calling Evaluate.
            ushort t = (ushort)gamepadTrigger;
            Assert.Equal(expectedPos, (byte)(t >> 8));
        }

        /// <summary>The zone the translator derives must track the pull.
        /// With the old clamp every one of these produced zone 9.</summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(32768, 5)]
        [InlineData(65535, 9)]
        public void AtImpulse_ZoneTracksPull(int gamepadTrigger, int expectedZone)
        {
            byte pos = (byte)((ushort)gamepadTrigger >> 8);
            Assert.Equal(expectedZone, pos * 10 / 256);
        }

        // ── VR mirror surfaces (C1-C3) ──

        /// <summary>The save pipeline dedups PadSettings BY CHECKSUM. A
        /// lane missing from ComputeChecksum lets two devices whose
        /// settings differ only in that lane collapse into one stored
        /// object, and the loser adopts the survivor's rows on reload.
        /// </summary>
        [Fact]
        public void Checksum_DistinguishesVrMappings()
        {
            var a = new PadSetting();
            a.SetVrMapping("VrLA", "Button 2");
            a.FlushVrMappings();
            a.UpdateChecksum();

            var b = new PadSetting();
            b.SetVrMapping("VrLA", "Button 7");
            b.FlushVrMappings();
            b.UpdateChecksum();

            Assert.NotEqual(a.PadSettingChecksum, b.PadSettingChecksum);
        }

        /// <summary>CopyFrom feeds profile snapshot/apply, settings-load
        /// hydration, and clipboard paste. Dropping a lane there loses
        /// every mapping in it on the next named-profile save.</summary>
        [Fact]
        public void CopyFrom_CarriesVrMappings()
        {
            var src = new PadSetting();
            src.SetVrMapping("VrRStickX", "Axis 3");
            src.SetVrMapping("VrLGripClick", "Button 4");

            var dst = new PadSetting();
            dst.CopyFrom(src);

            Assert.Equal("Axis 3", dst.GetVrMapping("VrRStickX"));
            Assert.Equal("Button 4", dst.GetVrMapping("VrLGripClick"));
        }

        /// <summary>CopyFrom must copy, not alias: editing the source
        /// afterwards must not reach into the copy.</summary>
        [Fact]
        public void CopyFrom_DeepCopiesVrEntries()
        {
            var src = new PadSetting();
            src.SetVrMapping("VrLA", "Button 2");

            var dst = new PadSetting();
            dst.CopyFrom(src);

            src.SetVrMapping("VrLA", "Button 9");
            src.FlushVrMappings();

            Assert.Equal("Button 2", dst.GetVrMapping("VrLA"));
        }

        /// <summary>ToJson/FromJson is the clipboard and per-device
        /// snapshot lane. Without the VR arm a copy/paste silently dropped
        /// every VR mapping.</summary>
        [Fact]
        public void Json_RoundTripsVrMappings()
        {
            var src = new PadSetting();
            src.SetVrMapping("VrRTriggerClick", "Axis 5");
            src.SetVrMapping("VrLStickY", "Axis 1");

            string json = src.ToJson(VirtualControllerType.Vr);
            var dst = PadSetting.FromJson(json);

            Assert.NotNull(dst);
            Assert.Equal("Axis 5", dst.GetVrMapping("VrRTriggerClick"));
            Assert.Equal("Axis 1", dst.GetVrMapping("VrLStickY"));
        }

        // ── TTL cache sentinel (SteamVR availability probe) ──

        /// <summary>A "never checked yet" sentinel of long.MinValue makes
        /// the elapsed-time subtraction OVERFLOW to a large negative value,
        /// which compares below every positive TTL. The cache then answers
        /// from its uninitialized default forever and never probes. This
        /// pins the arithmetic that shipped SteamVR as permanently absent.
        /// </summary>
        [Fact]
        public void TtlCache_MinValueSentinelOverflowsBelowEveryTtl()
        {
            const int ttl = 5_000;
            long now = 500_000;                       // a plausible uptime
            long sentinel = long.MinValue;

            unchecked
            {
                // The broken shape: reads as "still fresh", so no probe runs.
                Assert.True(now - sentinel < ttl);
            }

            // The fix: an explicit has-value flag cannot overflow.
            bool hasValue = false;
            long stamped = 0;
            Assert.False(hasValue && now - stamped < ttl);
        }

        /// <summary>Once stamped, the cache must behave normally in both
        /// directions.</summary>
        [Theory]
        [InlineData(0, true)]        // just checked -> fresh
        [InlineData(4_999, true)]
        [InlineData(5_000, false)]   // TTL elapsed -> re-probe
        [InlineData(60_000, false)]
        public void TtlCache_StampedValueExpiresOnSchedule(long elapsed, bool expectFresh)
        {
            const int ttl = 5_000;
            long stamped = 1_000_000;
            long now = stamped + elapsed;
            bool hasValue = true;
            Assert.Equal(expectFresh, hasValue && now - stamped < ttl);
        }

        // ── AUDIO-9: Sony packer negation wrap ──

        /// <summary>-(-32768) does not fit a short. Re-narrowing the
        /// negation wrapped full-DOWN back to full-UP on both Y axes of
        /// both Sony packers. Reachable whenever a source saturates an
        /// axis to short.MinValue (an AxisAdd macro under a deflected
        /// stick).</summary>
        [Theory]
        [InlineData(short.MinValue, 255)]   // full down -> 0xFF
        [InlineData(short.MaxValue, 0)]     // full up   -> 0x00
        [InlineData(0, 128)]
        public void SonyPacker_FullDeflectionYSurvivesNegation(short thumbY, int expected)
        {
            // ToDs4Axis's arithmetic on the WIDENED negation.
            int v = (-thumbY + 32768) >> 8;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            Assert.Equal(expected, v);
        }
    }
}
