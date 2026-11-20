using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Logitech wheel with several modes starts in its Driving Force mode,
    /// 0xC294 at 270 degrees, and a G923 for PlayStation in its PlayStation
    /// mode, 0xC267, until the host tells it to switch. The model, the
    /// conditions and the bytes follow new-lg4ff's hid-lg4ff.c:
    /// lg4ff_main_checklist and its ident infos for the model,
    /// lg4ff_handle_multimode_wheel for when a switch is sent,
    /// lg4ff_get_mode_switch_command and the lg4ff_mode_switch_* tables for
    /// the commands, and lg4ff_switch_from_ps_mode for report 0x30. The
    /// same commands are in the mainline kernel's hid-lg4ff.c, SDL's
    /// SDL_hidapi_lg4ff.c and lgff_wheel_adapter's wheel_commands.h.
    /// </summary>
    public class LogitechWheelModeSwitchTests
    {
        private const string Revert = "00F80A0000000000";

        private static string[] Hex(byte[][] reports) =>
            reports.Select(r => Convert.ToHexString(r)).ToArray();

        [Theory]
        // G29 in Driving Force mode, both of its ident infos.
        [InlineData(0xC294, 0x1350, new[] { Revert, "00F8090501010000" })]
        [InlineData(0xC294, 0x1357, new[] { Revert, "00F8090501010000" })]
        [InlineData(0xC294, 0x8900, new[] { Revert, "00F8090501010000" })]
        // Driving Force GT, checked after the G29 whose bcdDevice it shares a mask with.
        [InlineData(0xC294, 0x1300, new[] { Revert, "00F8090301000000" })]
        // G27, checked before the G25 whose mask it also matches.
        [InlineData(0xC294, 0x1230, new[] { Revert, "00F8090401000000" })]
        // G25 takes EXT_CMD16.
        [InlineData(0xC294, 0x1200, new[] { "00F8100000000000" })]
        // Driving Force Pro takes EXT_CMD1.
        [InlineData(0xC294, 0x1000, new[] { "00F8010000000000" })]
        // G923 in its PlayStation mode, in report 0x30.
        [InlineData(0xC267, 0x3800, new[] { "30F8090701010000" })]
        public void AWheelInACompatibilityModeIsToldToSwitchToItsOwn(int pid, int bcdDevice, string[] expected)
        {
            var reports = LogitechRawHidWriter.NativeModeReports(LogitechRawHidWriter.LogitechVid, (ushort)pid, (ushort)bcdDevice);
            Assert.Equal(expected, Hex(reports));
        }

        [Theory]
        // A Driving Force itself, and a bcdDevice no model lists.
        [InlineData(0x046D, 0xC294, 0x0000)]
        [InlineData(0x046D, 0xC294, 0x2100)]
        // A wheel already in its own mode.
        [InlineData(0x046D, 0xC24F, 0x1350)]
        [InlineData(0x046D, 0xC266, 0x3800)]
        // A G29 someone put in its Driving Force Pro mode stays there.
        [InlineData(0x046D, 0xC298, 0x1350)]
        // A G923 PlayStation id with a bcdDevice the G923 ident does not match.
        [InlineData(0x046D, 0xC267, 0x1200)]
        // Another vendor's device on Logitech's ids.
        [InlineData(0x044F, 0xC294, 0x1350)]
        public void AnyOtherWheelIsLeftInItsMode(int vid, int pid, int bcdDevice)
        {
            Assert.Empty(LogitechRawHidWriter.NativeModeReports((ushort)vid, (ushort)pid, (ushort)bcdDevice));
        }

        /// <summary>The switch writes its reports in order to the wheel's
        /// own path, and a wheel in its own mode gets none.</summary>
        [Fact]
        public void TheSwitchWritesItsReportsInOrderToTheWheel()
        {
            var written = new List<(string Path, string Report)>();
            using (RawHidOutput.CaptureForTest((path, report) =>
            {
                written.Add((path, Convert.ToHexString(report)));
                return true;
            }))
            {
                Assert.True(LogitechRawHidWriter.SwitchToNativeMode(@"\\?\hid#g29", 0x046D, 0xC294, 0x1352));
                Assert.False(LogitechRawHidWriter.SwitchToNativeMode(@"\\?\hid#g29native", 0x046D, 0xC24F, 0x1352));
            }
            Assert.Equal(new[]
            {
                (@"\\?\hid#g29", Revert),
                (@"\\?\hid#g29", "00F8090501010000"),
            }, written.ToArray());
        }
    }
}
