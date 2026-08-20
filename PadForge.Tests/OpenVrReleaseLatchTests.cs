using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Uninstalling SteamVR frees openvr_api.dll out of this process, and
    /// P/Invoke stubs bound before the free are never re-resolved: any
    /// OpenVR call after it is a native access violation waiting for a
    /// reinstall plus an engine restart to arrive. ReleaseRuntime therefore
    /// latches the consumer off for the rest of the process, and Start must
    /// honor the latch. There is deliberately no way to clear it: the
    /// dangling-stub hazard lasts until the process ends.
    ///
    /// <para>Order-dependent by nature (the latch is process-wide and
    /// one-way), so this fixture asserts the whole arc in ONE test.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class OpenVrReleaseLatchTests
    {
        [Fact]
        public void ReleaseRuntime_LatchesStartOff_ForTheProcessLifetime()
        {
            OpenVrConsumerService.ReleaseRuntime();
            Assert.True(OpenVrConsumerService.RuntimeReleased);

            // Start must refuse: a consumer polling after the free would
            // reach OpenVR.Init through a dangling stub.
            var svc = new OpenVrConsumerService();
            svc.Start();
            Assert.False(svc.IsRunning);

            // Releasing again is harmless (the module is already gone and
            // the guard keeps OpenVR.Shutdown from force-loading a new one).
            OpenVrConsumerService.ReleaseRuntime();
            Assert.True(OpenVrConsumerService.RuntimeReleased);
        }
    }
}
