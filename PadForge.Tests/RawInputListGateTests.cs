using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Raw Input lists opened every keyboard, mouse and Consumer Control
    /// collection on every 2 s device pass. They are read after a device
    /// change or a list that failed, and the gate is not asked when a failed
    /// list already asks for a read.
    /// </summary>
    public class RawInputListGateTests
    {
        [Fact]
        public void WithNothingNew_ThePassReadsNoList()
            => Assert.False(InputManager.RawInputListDue(again: false, changed: () => false));

        [Fact]
        public void ADeviceChange_ReadsTheLists()
            => Assert.True(InputManager.RawInputListDue(again: false, changed: () => true));

        [Fact]
        public void AFailedList_ReadsAgainWithoutAskingTheGate()
        {
            bool asked = false;
            Assert.True(InputManager.RawInputListDue(again: true, changed: () => { asked = true; return false; }));
            Assert.False(asked);
        }
    }
}
