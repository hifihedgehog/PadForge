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

        /// <summary>The list check runs on every device pass (2 s, 5 s with no
        /// slot online), and its lambda made a new delegate on each. The
        /// gate's ask is made once.</summary>
        [Fact]
        public void TheRawInputGateAskIsMadeOnce()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            Assert.Contains("RawInputListDue(_rawInputListAgain, _rawInputGateAsk ??= () => _rawInputScan.Due(Environment.TickCount64))", source);
        }
    }
}
