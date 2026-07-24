using System.Text.RegularExpressions;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the hidapi ignore list (issue #235): pads whose HID interface
    /// wedges SDL's Sony third-party detection feature report forever on
    /// Windows, freezing the UI thread that runs enumeration. Entries must
    /// stay in SDL_HIDAPI_IGNORE_DEVICES format (comma-separated
    /// 0xVVVV/0xPPPP, SDL_hints.h:1263-1277) or SDL silently ignores the
    /// hint and the freeze returns.
    /// </summary>
    public class HidapiIgnoreListTests
    {
        [Fact]
        public void IgnoreList_MatchesSdlHintFormat()
        {
            Assert.Matches(
                new Regex(@"^0x[0-9a-f]{4}/0x[0-9a-f]{4}(,0x[0-9a-f]{4}/0x[0-9a-f]{4})*$"),
                InputManager.HidapiIgnoreDevices);
        }

        [Fact]
        public void IgnoreList_ContainsTheNaconCompact()
        {
            // The #235 pad: connect froze PadForge until unplug. Removing
            // this entry needs a deliberate decision, not a refactor slip.
            Assert.Contains("0x146b/0x0603", InputManager.HidapiIgnoreDevices);
        }
    }
}
