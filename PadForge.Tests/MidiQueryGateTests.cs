using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The MIDI endpoint query asked the MIDI service for every endpoint on
    /// every 2 s device pass. It runs after a device change, a new backend or
    /// a failed query, and the gate is not asked when one of those already
    /// asks for it.
    /// </summary>
    public class MidiQueryGateTests
    {
        [Fact]
        public void WithNothingNew_ThePassSkipsTheQuery()
            => Assert.False(InputManager.MidiQueryDue(again: false, changed: () => false));

        [Fact]
        public void ADeviceChange_RunsTheQuery()
            => Assert.True(InputManager.MidiQueryDue(again: false, changed: () => true));

        [Fact]
        public void ANewBackendOrAFailedQuery_RunsItWithoutAskingTheGate()
        {
            bool asked = false;
            Assert.True(InputManager.MidiQueryDue(again: true, changed: () => { asked = true; return false; }));
            Assert.False(asked);
        }
    }
}
