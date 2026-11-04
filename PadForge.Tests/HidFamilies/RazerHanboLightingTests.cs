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
    /// <summary>The Hanbo Chroma against OpenRGB's RazerHanboController
    /// (#557).</summary>
    public class RazerHanboLightingTests
    {
        private static string R65(string head) => head + string.Concat(Enumerable.Repeat(" 00", 65 - head.Split(' ').Length));

        /// <summary>SetDirectMode with a 2 ms read after each report, then
        /// SetZoneLeds for the pump (32) and the three fans (40), green, red,
        /// blue, a 2 ms wait after each (RazerHanboController.cpp:73-105,
        /// 133-174 and 254-257).</summary>
        [Fact]
        public void TheFirstPicture_EntersDirectModeAndSendsFourZones()
        {
            var model = RazerHanboLighting.Instance.Model("Razer Hanbo Chroma", 0x0F35);
            var io = new RazerScriptChannel();
            var session = model.Begin(io, io.Wait);
            var colors = Enumerable.Range(0, 70).Select(i => 0x100000 * (i % 16) + 0x000100 * i + 0x01).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, model.Map.Lights, colors));
            Assert.Equal(new[]
            {
                R65("00 82 01"), R65("00 80 01 00 01"), R65("00 80 01 01 01"), R65("00 82 01"),
            }, io.Writes.Take(4).Select(w => Hex(w)));
            Assert.Equal(new[]
            {
                "O 00 82 01 00 00 00 00 00 00 00 00 00 00 00 00 00", "R 2",
                "O 00 80 01 00 01 00 00 00 00 00 00 00 00 00 00 00", "R 2",
                "O 00 80 01 01 01 00 00 00 00 00 00 00 00 00 00 00", "R 2",
                "O 00 82 01 00 00 00 00 00 00 00 00 00 00 00 00 00", "R 2",
            }, io.Log.Take(8));
            var zones = io.Writes.Skip(4).ToList();
            Assert.Equal(4, zones.Count);
            string Grb(int from, int count) => string.Join(" ", colors.Skip(from).Take(count)
                .SelectMany(c => new[] { (c >> 8) & 0xFF, (c >> 16) & 0xFF, c & 0xFF }).Select(b => b.ToString("X2")));
            Assert.Equal(R65("00 32 01 07 00 00 00 00 00 " + Grb(0, 16)), Hex(zones[0]));
            Assert.Equal(R65("00 40 01 07 00 00 00 00 01 " + Grb(16, 18)), Hex(zones[1]));
            Assert.Equal(R65("00 40 01 07 00 00 00 00 02 " + Grb(34, 18)), Hex(zones[2]));
            Assert.Equal(R65("00 40 01 07 00 00 00 00 03 " + Grb(52, 18)), Hex(zones[3]));
            Assert.Equal(new[] { "W 2", "W 2", "W 2", "W 2" }, io.Log.Skip(8).Where(l => l.StartsWith("W")));
            Assert.DoesNotContain(io.Log.Skip(8), l => l.StartsWith("R"));

            // The next picture sends the zones alone.
            io.Writes.Clear();
            session.Paint(io, io.Wait, model.Map.Lights, colors);
            Assert.Equal(4, io.Writes.Count);
            Assert.All(io.Writes, w => Assert.Contains(w[1], new byte[] { 0x32, 0x40 }));
        }

        /// <summary>The hand-back is SetModeSpectrumCycle, each report with
        /// its read (cpp:112-131).</summary>
        [Fact]
        public void TheHandBack_IsTheSpectrumCycle()
        {
            var model = RazerHanboLighting.Instance.Model("Razer Hanbo Chroma", 0x0F35);
            var io = new RazerScriptChannel();
            var session = model.Begin(io, io.Wait);
            Assert.Empty(session.CrashFrames);
            session.Paint(io, io.Wait, model.Map.Lights, new int[70]);
            var spectrum = new[] { R65("00 82 01"), R65("00 80 01"), R65("00 80 01 01") };
            Assert.Equal(spectrum, session.CrashFrames.Select(f => Hex(f)));
            Assert.False(session.Feature);
            io.Writes.Clear();
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(spectrum, io.Writes.Select(w => Hex(w)));
            Assert.Equal(3, io.Log.Count(l => l == "R 2"));
            Assert.Empty(session.CrashFrames);
        }
    }
}
