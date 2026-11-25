using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// How a recording says a device is held (#600 item 32): shared with
    /// every program, exclusive to PadForge (Use Only in PadForge), or hidden
    /// by HidHide, which wins when both are on.
    /// </summary>
    public class RecorderHoldTests
    {
        [Fact]
        public void ADevice_IsHeldShared_Exclusive_OrHidden()
        {
            var pad = RecorderTestKit.Pad();
            Assert.Equal("shared", RecorderHost.HeldWord(pad));
            pad.ConsumeInputEnabled = true;
            Assert.Equal("exclusive", RecorderHost.HeldWord(pad));
            pad.HidHideEnabled = true;
            Assert.Equal("hidden", RecorderHost.HeldWord(pad));
        }
    }
}
