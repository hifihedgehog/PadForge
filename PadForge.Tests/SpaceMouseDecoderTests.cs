using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #288 (SpaceMouse): the pure wire decoder. The wire contract these tests
    /// lock is grounded against three cloned references (spacemouse-AndunHH
    /// SpaceNavigator.md, hid.spacemouse index.js, pyspacemouse devices.toml +
    /// device.py): report 1 = translation int16 LE at bytes 1-6 (plus rotation
    /// at 7-12 on combined-shape devices), report 2 = rotation, report 3 =
    /// buttons as 6 bytes x 8 bits, logical max 350, and PERSISTENT per-report
    /// frame assembly.
    /// </summary>
    public class SpaceMouseDecoderTests
    {
        private static byte[] Report(byte id, params short[] axes)
        {
            var b = new byte[1 + axes.Length * 2];
            b[0] = id;
            for (int i = 0; i < axes.Length; i++)
            {
                b[1 + i * 2] = (byte)(axes[i] & 0xFF);
                b[2 + i * 2] = (byte)((axes[i] >> 8) & 0xFF);
            }
            return b;
        }

        [Fact]
        public void SplitShape_FrameAssembly_RotationSurvivesTranslationReport()
        {
            // THE TRAP the recipe names: alternating reports must not clobber
            // the axes they do not carry. A decoder that resets state per
            // report publishes translation with stale-zero rotation at 125 Hz.
            var d = new SpaceMouseDecoder(combinedReport: false);
            var rot = Report(2, 100, -200, 350);
            Assert.True(d.Process(rot, rot.Length));
            var trans = Report(1, 10, 20, -30);
            Assert.True(d.Process(trans, trans.Length));

            Assert.Equal(10, d.TranslateX);
            Assert.Equal(20, d.TranslateY);
            Assert.Equal(-30, d.TranslateZ);
            Assert.Equal(100, d.RotateX);   // persisted across the report-1 frame
            Assert.Equal(-200, d.RotateY);
            Assert.Equal(350, d.RotateZ);
        }

        [Fact]
        public void SplitShape_ZeroTriplet_IsAGenuineCenterReturn()
        {
            // AndunHH SpaceNavigator.md:44: after the last non-zero motion each
            // triplet is sent as zeros. Those zeros are real state.
            var d = new SpaceMouseDecoder(combinedReport: false);
            var move = Report(1, 300, -300, 42);
            d.Process(move, move.Length);
            var center = Report(1, 0, 0, 0);
            d.Process(center, center.Length);

            Assert.Equal(0, d.TranslateX);
            Assert.Equal(0, d.TranslateY);
            Assert.Equal(0, d.TranslateZ);
        }

        [Fact]
        public void CombinedShape_Report1CarriesAllSixAxes()
        {
            // pyspacemouse devices.toml: modern 0x256F devices map pitch/roll/
            // yaw to report 1 bytes 7-12 (e.g. SpaceMouseWireless mappings).
            var d = new SpaceMouseDecoder(combinedReport: true);
            var frame = Report(1, 10, -20, 30, -111, 222, -333);
            Assert.True(d.Process(frame, frame.Length));

            Assert.Equal(10, d.TranslateX);
            Assert.Equal(-20, d.TranslateY);
            Assert.Equal(30, d.TranslateZ);
            Assert.Equal(-111, d.RotateX);
            Assert.Equal(222, d.RotateY);
            Assert.Equal(-333, d.RotateZ);
        }

        [Fact]
        public void SplitShape_PaddedReport1_DoesNotLeakZerosIntoRotation()
        {
            // Windows HIDClass pads reads to InputReportByteLength. On a
            // split-shape device whose max input report exceeds 7 bytes, a
            // translation report arrives with a zeroed tail; the shape flag
            // (from the descriptor, not the length) must keep those padding
            // bytes out of the rotation axes.
            var d = new SpaceMouseDecoder(combinedReport: false);
            var rot = Report(2, 50, 60, 70);
            d.Process(rot, rot.Length);

            var padded = new byte[13];               // 7 real bytes + 6 padding zeros
            var trans = Report(1, 1, 2, 3);
            System.Array.Copy(trans, padded, trans.Length);
            Assert.True(d.Process(padded, padded.Length));

            Assert.Equal(50, d.RotateX);             // untouched by the padding
            Assert.Equal(60, d.RotateY);
            Assert.Equal(70, d.RotateZ);
        }

        [Fact]
        public void Int16Decode_IsLittleEndianWithSignExtension()
        {
            // hid.spacemouse index.js:3-12 joinInt16; pyspacemouse _to_int16.
            var d = new SpaceMouseDecoder(combinedReport: false);
            var report = new byte[] { 1, 0x9C, 0xFE, 0x64, 0x01, 0xFF, 0xFF }; // -356, 356, -1
            Assert.True(d.Process(report, report.Length));
            Assert.Equal(-356, d.TranslateX);
            Assert.Equal(356, d.TranslateY);
            Assert.Equal(-1, d.TranslateZ);
        }

        [Fact]
        public void Buttons_SixBytesOfEightBits_LsbFirst()
        {
            // hid.spacemouse index.js:39-47.
            var d = new SpaceMouseDecoder(combinedReport: false);
            var report = new byte[] { 3, 0b0000_0011, 0, 0, 0, 0, 0b1000_0000 };
            Assert.True(d.Process(report, report.Length));

            Assert.True(d.GetButton(0));
            Assert.True(d.GetButton(1));
            Assert.False(d.GetButton(2));
            Assert.True(d.GetButton(47));

            // Release arrives as a fresh report 3 with the bits cleared.
            var release = new byte[] { 3, 0, 0, 0, 0, 0, 0 };
            d.Process(release, release.Length);
            Assert.False(d.GetButton(0));
            Assert.False(d.GetButton(47));
        }

        [Fact]
        public void ShortButtonReport_OnlyTouchesItsOwnBytes()
        {
            // A device with a 2-byte button payload must not clear buttons
            // beyond its payload from Windows' zero padding: only real payload
            // bytes are trusted (the device clears its own buttons through
            // them on release).
            var d = new SpaceMouseDecoder(combinedReport: false);
            var full = new byte[] { 3, 0, 0, 0, 0, 0, 0x01 };  // button 40 down
            d.Process(full, full.Length);
            Assert.True(d.GetButton(40));

            var shortReport = new byte[] { 3, 0x01, 0 };        // 2-byte payload device
            d.Process(shortReport, shortReport.Length);
            Assert.True(d.GetButton(0));
            Assert.True(d.GetButton(40));                        // out of payload, persists
        }

        [Fact]
        public void UnknownReportsAndRunts_AreIgnored()
        {
            var d = new SpaceMouseDecoder(combinedReport: false);
            Assert.False(d.Process(null, 0));
            Assert.False(d.Process(new byte[] { 1 }, 1));                   // runt
            Assert.False(d.Process(new byte[] { 1, 0, 0, 0 }, 4));          // short axis report
            Assert.False(d.Process(new byte[] { 9, 1, 2, 3, 4, 5, 6 }, 7)); // unknown id (LED report family)
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(350, 32767)]
        [InlineData(-350, -32767)]
        [InlineData(175, 16383)]
        [InlineData(400, 32767)]    // out-of-range clamps, never wraps
        [InlineData(-400, -32768)]
        public void ToSdlAxis_ScalesLogicalMax350ToFullRange(short raw, short expected)
        {
            Assert.Equal(expected, SpaceMouseDecoder.ToSdlAxis(raw));
        }
    }
}
