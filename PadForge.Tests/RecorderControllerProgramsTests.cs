using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The other controller programs a recording looks for (#600 item 32):
    /// PadRevive is among them, matched by the start of its process name as
    /// the others are.
    /// </summary>
    public class RecorderControllerProgramsTests
    {
        [Fact]
        public void PadRevive_IsAmongTheOtherControllerPrograms()
        {
            var running = RecorderHost.RunningOf(new[] { "PadRevive", "notepad" }, RecorderHost.ControllerPrograms);
            Assert.Equal(new[] { "PadRevive" }, running);
        }
    }
}
