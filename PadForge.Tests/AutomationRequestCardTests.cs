using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The request card (#597): the small window above other windows that
    /// asks the user to allow or refuse what a program asked. It shows the
    /// oldest question of the Automation page's own list, so an answer in
    /// either place ends the question in both. A card made here has no
    /// anchor, so it never shows itself on the test PC's screen.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationRequestCardTests
    {
        /// <summary>The answer a question ended with. It has ended.</summary>
        private static ApprovalAnswer Answer(Task<ApprovalAnswer> asked)
        {
            Assert.True(asked.IsCompleted, "the question has not ended");
            return asked.Result;
        }

        private static AutomationHost NewHost(Func<DateTimeOffset> now)
            => new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                "PadForge.Tests.Card." + Guid.NewGuid().ToString("N"), _ => AutomationProgram.Unknown, now);

        /// <summary>The card shows the oldest question with its place in
        /// the queue and its time left, six detail lines and the count of
        /// the rest. An answer on the card ends the question on the page,
        /// and an answer on the page moves the card on.</summary>
        [Fact]
        public void TheCard_ShowsTheOldestQuestion_AndAnAnswerInEitherPlaceEndsIt()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var now = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
                using var host = NewHost(() => now);
                var vm = new DashboardViewModel();
                vm.AttachAutomation(host, dispatcher);
                var card = new AutomationRequestCard(vm.AutomationRequests, null, () => now.AddSeconds(8));
                try
                {
                    Assert.Null(card.Shown);
                    var lines = Enumerable.Range(1, 8).Select(i => "Starts C:\\Tools\\step" + i + ".exe").ToArray();
                    var first = host.Approvals.AskAsync(new ApprovalRequest(new AutomationProgram("Playnite", 9, "Playnite.DesktopApp.exe"),
                        "Run programs when a profile starts", lines));
                    Assert.Same(vm.AutomationRequests[0], card.Shown);
                    Assert.Equal(string.Empty, ((TextBlock)card.FindName("PositionText")).Text);

                    var second = host.Approvals.AskAsync(new ApprovalRequest(AutomationProgram.Unknown, "Import a profile", null));
                    var s = Strings.Instance;
                    Assert.Same(vm.AutomationRequests[0], card.Shown);
                    Assert.Equal(string.Format(s.Automation_Request_Position_Format, 1, 2), ((TextBlock)card.FindName("PositionText")).Text);
                    // Two minutes from the question, eight seconds on.
                    Assert.Equal(string.Format(s.Automation_Request_TimeLeft_Format, "1:52"), ((TextBlock)card.FindName("TimeText")).Text);
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal(6, ((ItemsControl)card.FindName("DetailList")).Items.Count);
                    Assert.Equal(string.Format(s.Automation_Request_MoreDetail_Format, 2), card.Shown.CardDetailRest);

                    // Allow on the card: the program's wait ends with yes, and the
                    // page no longer lists the question.
                    var allow = (Button)card.FindName("AllowButton");
                    allow.Command.Execute(null);
                    Assert.Equal(ApprovalAnswer.Allowed, Answer(first));
                    Assert.Single(vm.AutomationRequests);
                    Assert.Same(vm.AutomationRequests[0], card.Shown);
                    Assert.Equal(string.Empty, ((TextBlock)card.FindName("PositionText")).Text);

                    // Don't Allow on the page: the card has nothing left to ask.
                    vm.AutomationRequests[0].DenyCommand.Execute(null);
                    Assert.Equal(ApprovalAnswer.Denied, Answer(second));
                    Assert.Null(card.Shown);

                    // The card's Don't Allow ends a question with no.
                    var third = host.Approvals.AskAsync(new ApprovalRequest(AutomationProgram.Unknown, "Turn on the Web Controller", null));
                    ((Button)card.FindName("DenyButton")).Command.Execute(null);
                    Assert.Equal(ApprovalAnswer.Denied, Answer(third));
                    Assert.Null(card.Shown);
                }
                finally { card.Close(); }
            }, TimeSpan.FromMinutes(2), "The request card test timed out.");
        }

        /// <summary>The Activity list as drawn: a refused call's row reads
        /// the phrase for its error word, and no text in the list carries
        /// the protocol's words.</summary>
        [Fact]
        public void TheActivityRow_ShowsARefusalInPlainWords()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var now = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
                using var host = NewHost(() => now);
                var vm = new DashboardViewModel();
                vm.AttachAutomation(host, dispatcher);
                foreach (var (kind, answer) in new[]
                {
                    (AutomationAskKind.EngineOff, "error not-allowed"),
                    (AutomationAskKind.ActivateProfile, "error unknown-profile"),
                    (AutomationAskKind.HideWindow, "error not-available"),
                    (AutomationAskKind.Unknown, "error never-said"),
                })
                    host.Activity.Add(new AutomationActivityEntry(now, new AutomationProgram("powershell", 7, "powershell.exe"),
                        new AutomationAsk(kind, "Racing"), answer, false));
                var page = new ExtrasPage { DataContext = vm };
                var ground = new System.Windows.Controls.Border { Child = page };
                page.Show("Automation");
                for (int pass = 0; pass < 2; pass++)
                {
                    PageRenderDump.Flush(ground);
                    ground.Measure(new System.Windows.Size(1176, 2400));
                    ground.Arrange(new System.Windows.Rect(0, 0, 1176, 2400));
                    ground.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                }
                var list = (ItemsControl)page.FindName("AutomationActivityList");
                var texts = new System.Collections.Generic.List<string>();
                void Walk(System.Windows.DependencyObject node)
                {
                    if (node is TextBlock block && !string.IsNullOrEmpty(block.Text)) texts.Add(block.Text);
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                        Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                }
                Walk(list);
                var s = Strings.Instance;
                foreach (string phrase in new[] { s.Automation_Result_NotAllowed, s.Automation_Result_UnknownProfile,
                             s.Automation_Result_NotAvailable, s.Automation_Result_NotDone })
                    Assert.Contains(phrase, texts);
                Assert.DoesNotContain(texts, t => t.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("not-", StringComparison.Ordinal) || t.Contains("unknown-", StringComparison.Ordinal)
                    || t.Contains("never-said", StringComparison.Ordinal));
            }, TimeSpan.FromMinutes(2), "The Activity row test timed out.");
        }

        /// <summary>The time left counts down in minutes and seconds and
        /// never reads 0:00 while the question waits.</summary>
        [Fact]
        public void TheTimeLeft_CountsDownInMinutesAndSeconds()
        {
            string Line(string t) => string.Format(Strings.Instance.Automation_Request_TimeLeft_Format, t);
            Assert.Equal(Line("2:00"), AutomationRequestCard.TimeLeftLine(TimeSpan.FromMinutes(2)));
            Assert.Equal(Line("1:52"), AutomationRequestCard.TimeLeftLine(TimeSpan.FromSeconds(111.2)));
            Assert.Equal(Line("0:01"), AutomationRequestCard.TimeLeftLine(TimeSpan.FromMilliseconds(30)));
            Assert.Equal(Line("0:00"), AutomationRequestCard.TimeLeftLine(TimeSpan.FromSeconds(-3)));
        }

        /// <summary>The card's corner stands its gap in from the work area's
        /// bottom right, in the display's own pixels.</summary>
        [Fact]
        public void TheCard_StandsAtTheBottomRightOfTheWorkArea()
        {
            Assert.Equal((1920 - 8 - 404, 1040 - 8 - 300), AutomationRequestCard.Corner(1920, 1040, 1.0, 404, 300));
            // At 150%: a card of 404 by 300 is 606 by 450 pixels, the gap 12.
            Assert.Equal((2560 - 12 - 606, 1528 - 12 - 450), AutomationRequestCard.Corner(2560, 1528, 1.5, 404, 300));
            // A work area that starts left of the primary display.
            Assert.Equal((-8 - 404, 1040 - 8 - 300), AutomationRequestCard.Corner(0, 1040, 1.0, 404, 300));
        }

        /// <summary>The window recipe: never activated, a tool window, clicks
        /// taken (no WS_EX_TRANSPARENT), no owner, buttons that take no
        /// focus, Don't Allow first in the plain look and Allow in the ember
        /// one, the flyouts' own surface, hidden while a modal dialog of
        /// PadForge is open, and placed on the display PadForge's window is
        /// on.</summary>
        [Fact]
        public void TheCard_TakesNoFocus_TakesClicks_AndNeverCoversADialog()
        {
            string xaml = ExtrasPageTests.RepoText("PadForge.App", "Views", "AutomationRequestCard.xaml");
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "AutomationRequestCard.xaml.cs");
            foreach (string attribute in new[] { "Topmost=\"True\"", "ShowInTaskbar=\"False\"", "ShowActivated=\"False\"", "Focusable=\"False\"", "AllowsTransparency=\"True\"" })
                Assert.Contains(attribute, xaml);
            Assert.Contains("exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW", code);
            Assert.Contains("if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }", code);
            Assert.DoesNotContain("TRANSPARENT", code);
            Assert.DoesNotContain("Owner", Regex.Replace(code, "///.*", ""));
            Assert.DoesNotContain("Activate()", code);

            int deny = xaml.IndexOf("x:Name=\"DenyButton\" Style=\"{StaticResource DialogButton}\" Focusable=\"False\" IsTabStop=\"False\"", StringComparison.Ordinal);
            int allow = xaml.IndexOf("x:Name=\"AllowButton\" Style=\"{StaticResource DialogPrimaryButton}\" Focusable=\"False\" IsTabStop=\"False\"", StringComparison.Ordinal);
            Assert.True(deny > 0 && allow > deny, "Don't Allow is not first in the plain look");
            Assert.Equal(2, Regex.Matches(xaml, "Style=\"\\{StaticResource FlyoutSurface\\}\"").Count);
            Assert.DoesNotContain("#2D2E2E", xaml);

            Assert.Contains("ComponentDispatcher.EnterThreadModal += OnThreadModalChanged;", code);
            Assert.Contains("ComponentDispatcher.LeaveThreadModal += OnThreadModalChanged;", code);
            Assert.Contains("if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return true;", code);
            Assert.Contains("return main != IntPtr.Zero && !IsWindowEnabled(main);", code);
            Assert.Contains("? MonitorFromWindow(main, MONITOR_DEFAULTTONEAREST)", code);
            // A window never shown yet, as in a start in the tray: the primary display.
            Assert.Contains(": MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);", code);
            Assert.Contains("Corner(info.rcWork.Right, info.rcWork.Bottom, scale, ActualWidth, ActualHeight)", code);
        }
    }
}
