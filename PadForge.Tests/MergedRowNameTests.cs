using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The merged rows and the touchpad overlay are named by PadForge, not by
    /// a device, so their names come from the string table and follow the UI
    /// language. Whatever name a row was created with, which for some of
    /// them is an English one from the engine,
    /// <see cref="InputService.LocalizedDeviceName"/> is where the mapping
    /// list and the recording status get the name for the language in use.
    ///
    /// <para>"All Consumer Controls (Merged)" was added to the engine and
    /// never to that switch, so it stayed English in all ten languages beside
    /// three siblings that were translated.</para>
    /// </summary>
    public class MergedRowNameTests
    {
        [Fact]
        public void EveryMergedRowAndTheOverlay_TakesItsNameFromTheStringTable()
        {
            var s = Strings.Instance;
            Assert.Equal(s.Devices_AllKeyboardsMerged, NameFor("aggregate://keyboards"));
            Assert.Equal(s.Devices_AllMiceMerged, NameFor("aggregate://mice"));
            Assert.Equal(s.Devices_AllTouchpadsMerged, NameFor("aggregate://touchpads"));
            Assert.Equal(s.Devices_AllConsumerControlsMerged, NameFor("aggregate://consumercontrols"));
            Assert.Equal(s.Dashboard_TouchpadOverlay, NameFor("overlay://touchpad"));
        }

        /// <summary>A real device keeps the name it reported. Without this the
        /// test above would pass against a method that ignored the path.</summary>
        [Fact]
        public void ARealDevice_KeepsItsOwnName()
        {
            Assert.Equal(EngineName, NameFor(@"\\?\hid#vid_046d&pid_c33f"));
        }

        // What the engine calls the row, and deliberately not any string the
        // table holds, so an arm that fell through to it is caught.
        private const string EngineName = "engine-side name";

        private static string NameFor(string devicePath)
        {
            var ud = new UserDevice { DevicePath = devicePath, InstanceName = EngineName };
            return InputService.LocalizedDeviceName(ud);
        }
    }
}
