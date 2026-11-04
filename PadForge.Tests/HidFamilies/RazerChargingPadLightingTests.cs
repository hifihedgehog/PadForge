using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Charging Pad Chroma (#557).</summary>
    /// <remarks>In the RazerGuardLog collection: these sessions take the
    /// Razer lock, whose guard writes a diagnostics line that
    /// <see cref="RazerMutexOffLimitsTests"/> counts.</remarks>
    [Collection("RazerGuardLog")]
    public class RazerChargingPadLightingTests
    {
        /// <summary>The first picture: device mode 03 under 1F
        /// (RazerController.cpp:68-77 and 832-840), then the row of ten,
        /// 0F/03 at size 35, and the custom frame effect, 0F/02 [00 00 08]
        /// at size 0C, each 1 ms after the one before (207-258, 793-810 and
        /// 931-940). The hand-back is openrazer's device mode 00 under 3F
        /// (razeraccessory_driver.c:112-121 and 704-707).</summary>
        [Fact]
        public void TheFirstPicture_SwitchesToSoftwareMode_AndTheHandBackSwitchesBack()
        {
            var model = RazerChargingPadLighting.Instance.Model("Razer Charging Pad Chroma", 0x0F26);
            var io = new RazerScriptChannel();
            var session = model.Begin(io, io.Wait);
            Assert.Empty(session.CrashFrames);
            var colors = Enumerable.Range(1, 10).Select(i => i * 0x010203).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, model.Map.Lights, colors));
            var row = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x09 }
                .Concat(colors.SelectMany(c => new[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c })).ToArray();
            Assert.Equal(new[]
            {
                RazerReport90.Of(0x1F, 0x00, 0x04, 0x02, 0x03, 0x00),
                RazerReport90.Of(0x1F, 0x0F, 0x03, 0x23, row),
                RazerReport90.Of(0x1F, 0x0F, 0x02, 0x0C, 0x00, 0x00, 0x08),
            }, io.Writes.Select(w => Hex(w)));
            Assert.Equal(new[] { "W 1", "W 1" }, io.Log.Where(l => l.StartsWith("W")));
            Assert.Equal(new[] { RazerReport90.Of(0x3F, 0x00, 0x04, 0x02, 0x00, 0x00) }, session.CrashFrames.Select(f => Hex(f)));
            Assert.True(session.Feature);

            io.Writes.Clear();
            session.Paint(io, io.Wait, model.Map.Lights, colors);
            Assert.Equal(2, io.Writes.Count);
            Assert.Equal(0x03, io.Writes[0][8]);

            io.Writes.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { RazerReport90.Of(0x3F, 0x00, 0x04, 0x02, 0x00, 0x00) }, io.Writes.Select(w => Hex(w)));
            Assert.Empty(session.CrashFrames);
            // Lit again, it switches to software mode again.
            io.Writes.Clear();
            session.Paint(io, io.Wait, model.Map.Lights, colors);
            Assert.Equal(3, io.Writes.Count);
        }

        [Fact]
        public void AFailedWrite_FailsThePicture()
        {
            var model = RazerChargingPadLighting.Instance.Model("Razer Charging Pad Chroma", 0x0F26);
            var io = new RazerScriptChannel();
            var session = model.Begin(io, io.Wait);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new RazerScriptChannel { Fails = r => r[8] == 0x03 }, io.Wait, model.Map.Lights, new int[10]));
            Assert.Single(session.CrashFrames);
            Assert.False(session.HandBack(new RazerScriptChannel { Fails = _ => true }, io.Wait));
        }
    }
}
