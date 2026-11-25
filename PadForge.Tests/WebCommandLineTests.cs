using System;
using PadForge.Common;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// <c>--web</c> (#600 item 11, the command-line table): the web helper
    /// PadForge starts itself. With no copy running it gives 4, and with one
    /// it runs the helper, which that copy takes only when it started it
    /// (WebEndToEndTests.AHelperPadForgeDidNotStart_IsRefused).
    /// </summary>
    public class WebCommandLineTests
    {
        private static (bool Done, int Code, int Served) Go(bool copy)
        {
            int served = 0;
            var seams = new ClientRun.Seams
            {
                CopyRuns = () => copy,
                Exchange = _ => throw new InvalidOperationException("--web sends no line"),
                ShowWindow = () => throw new InvalidOperationException("--web shows no window"),
                Say = _ => { },
                SayError = _ => { },
                Tell = _ => { },
                TellError = _ => { },
                ServeWeb = () => { served++; return 0; },
                ServeAssistant = () => throw new InvalidOperationException("--web is not --mcp"),
            };
            bool done = ClientRun.TryRun(new[] { "--web" }, out int code, seams);
            return (done, code, served);
        }

        [Fact]
        public void Web_WithNoCopyRunning_Gives4_AndWithOne_RunsTheHelper()
        {
            Assert.Equal((true, ClientRun.NotRunning, 0), Go(copy: false));
            Assert.Equal((true, ClientRun.Done, 1), Go(copy: true));
        }

        [Fact]
        public void Web_IsASwitchOfItsOwn_TakesNoValue_AndTheHelpNamesIt()
        {
            var request = CommandLineSwitches.Parse(new[] { "--web" });
            Assert.Null(request.Error);
            Assert.True(request.Web);
            Assert.False(request.Empty);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--web=1" }).Error);
            Assert.Contains("  --web ", CommandLineSwitches.HelpText, StringComparison.Ordinal);
        }

        /// <summary>A web assistant connected puts the globe in the title
        /// bar's mark (#600 item 13), by the window's own rule.</summary>
        [Fact]
        public void AWebAssistant_PutsTheGlobeInTheTitleBarsMark()
        {
            var web = new AutomationClient(1, "claude.ai", AutomationClientKind.Web, 100, "PadForge.exe", DateTimeOffset.Now);
            var local = new AutomationClient(2, "Steer Assist", AutomationClientKind.Script, 101, "python.exe", DateTimeOffset.Now);
            var mark = MainWindow.ConnectedMarkState(new[] { web, local }, MainWindow.ClientIsFromWeb, "Devices", null);
            Assert.True(mark.Shows);
            Assert.True(mark.Web);
            Assert.Equal(2, mark.Count);
            Assert.Contains("claude.ai", mark.Words);
            Assert.False(MainWindow.ConnectedMarkState(new[] { local }, MainWindow.ClientIsFromWeb, "Devices", null).Web);
        }

        /// <summary>The client kinds are append-only: a web assistant's is
        /// the last, after the three before it.</summary>
        [Fact]
        public void TheClientKinds_KeepTheirNumbers_WithWebAtTheTail()
        {
            Assert.Equal(0, (int)AutomationClientKind.Script);
            Assert.Equal(1, (int)AutomationClientKind.Assistant);
            Assert.Equal(2, (int)AutomationClientKind.Plugin);
            Assert.Equal(3, (int)AutomationClientKind.Web);
            Assert.Equal(4, Enum.GetValues<AutomationClientKind>().Length);
            Assert.Equal(3, (int)ApprovalKind.WebSignIn);
            Assert.Equal(4, Enum.GetValues<ApprovalKind>().Length);
        }
    }
}
