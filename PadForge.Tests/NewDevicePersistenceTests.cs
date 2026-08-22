using System.Threading.Tasks;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A controller that connects, gets listed, and is unplugged again used
    /// to leave nothing behind. Its UserDevice row lived only in memory:
    /// nothing on the arrival path marked the settings dirty, and the
    /// shutdown save is dirty-gated, so the row was gone by the next launch
    /// unless the user happened to change a setting while it was connected.
    /// Owner-reported for a Switch 2 Pro and measured: the pad was listed in
    /// PadForge for eleven minutes while the config file went untouched.
    ///
    /// <para>The signal has to be a row BEING CREATED. Hooking the
    /// DevicesUpdated event instead looks equivalent and is not: that event
    /// fires roughly every two seconds whether or not the device set
    /// changed (measured at twelve firings in twenty-five seconds with
    /// nothing plugged or unplugged), so a dirty mark there rewrote the
    /// whole config on a two-second loop for as long as PadForge ran.</para>
    /// </summary>
    public class NewDevicePersistenceTests
    {
        /// <summary>A pending registration is reported once and then gone.
        /// Reporting it twice would mark dirty on the next refresh as well,
        /// which is the two-second rewrite loop in slow motion.</summary>
        [Fact]
        public void APendingRegistration_IsReportedExactlyOnce()
        {
            InputManager.MarkNewDeviceRegisteredForTest();
            Assert.True(InputManager.ConsumeNewDeviceRegistered());
            Assert.False(InputManager.ConsumeNewDeviceRegistered());
        }

        /// <summary>With nothing registered the answer is no, so a refresh
        /// that found no new device writes nothing.</summary>
        [Fact]
        public void WithNothingPending_NothingIsReported()
        {
            InputManager.ConsumeNewDeviceRegistered();
            Assert.False(InputManager.ConsumeNewDeviceRegistered());
        }

        /// <summary>Several devices arriving between two refreshes collapse
        /// into one save. The flag answers "is a write owed", not "how
        /// many", and one write covers every row.</summary>
        [Fact]
        public void SeveralArrivalsBetweenRefreshes_CollapseToOneSave()
        {
            InputManager.ConsumeNewDeviceRegistered();
            for (int i = 0; i < 5; i++)
                InputManager.MarkNewDeviceRegisteredForTest();
            Assert.True(InputManager.ConsumeNewDeviceRegistered());
            Assert.False(InputManager.ConsumeNewDeviceRegistered());
        }

        /// <summary>The poll thread sets the flag while the UI thread takes
        /// it, so the exchange has to be atomic. Every set must surface as
        /// at least one take: a set that lands mid-take and is lost is a
        /// device row that never reaches disk.</summary>
        [Fact]
        public async Task ConcurrentSetsAndTakes_LoseNothing()
        {
            InputManager.ConsumeNewDeviceRegistered();

            const int Sets = 20000;
            int taken = 0;
            var setter = Task.Run(() =>
            {
                for (int i = 0; i < Sets; i++)
                    InputManager.MarkNewDeviceRegisteredForTest();
            });
            var taker = Task.Run(() =>
            {
                while (!setter.IsCompleted)
                    if (InputManager.ConsumeNewDeviceRegistered()) taken++;
            });
            await Task.WhenAll(setter, taker);
            if (InputManager.ConsumeNewDeviceRegistered()) taken++;

            // Sets collapse, so the count is bounded above by Sets, but a
            // run that took nothing means every set was dropped.
            Assert.InRange(taken, 1, Sets);
            Assert.False(InputManager.ConsumeNewDeviceRegistered());
        }
    }
}
