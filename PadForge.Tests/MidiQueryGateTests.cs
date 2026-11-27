using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The MIDI endpoint query asked the MIDI service for every endpoint on
    /// every 2 s device pass. It runs after a device change, a new backend or
    /// a failed query, and on every pass while an endpoint is listed. The
    /// gate is not asked when one of those already
    /// asks for it.
    /// </summary>
    public class MidiQueryGateTests
    {
        [Fact]
        public void WithNothingNew_ThePassSkipsTheQuery()
            => Assert.False(InputManager.MidiQueryDue(again: false, listed: new(), changed: () => false));

        [Fact]
        public void ADeviceChange_RunsTheQuery()
            => Assert.True(InputManager.MidiQueryDue(again: false, listed: new(), changed: () => true));

        [Fact]
        public void ANewBackendOrAFailedQuery_RunsItWithoutAskingTheGate()
        {
            bool asked = false;
            Assert.True(InputManager.MidiQueryDue(again: true, listed: new(), changed: () => { asked = true; return false; }));
            Assert.False(asked);
        }

        /// <summary>The query check runs on every device pass (2 s, 5 s with no
        /// slot online), and a lambda that captured a local made a closure and
        /// a delegate on each. The gate's ask is made once.</summary>
        [Fact]
        public void TheMidiGateAskIsMadeOnce()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            Assert.Contains("MidiQueryDue(_midiEnumAgain, _cachedMidiEndpoints, _midiGateAsk ??= () => _midiScan.Due(Environment.TickCount64))", source);
            Assert.DoesNotContain("() => _midiScan.Due(midiEnumNow)", source);
        }
    }
}
