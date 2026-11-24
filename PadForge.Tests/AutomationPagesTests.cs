using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Automation and Plugins extras (#597): the fourth group of the
    /// catalog, the two pages in the frame every extra's page shares, the
    /// level as a choice of four with its one line, the block an assistant's
    /// settings take, the questions that wait for the user, the record of
    /// what programs asked, and the buttons of the parts later builds
    /// provide, absent until they do. The host here never opens its pipe.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationPagesTests
    {
        /// <summary>The answer a question ended with. It has ended.</summary>
        private static ApprovalAnswer Answer(Task<ApprovalAnswer> asked)
        {
            Assert.True(asked.IsCompleted, "the question has not ended");
            return asked.Result;
        }

        private static string Page() => ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml");

        private static string PageOf(string key)
        {
            string page = Page();
            var keys = ExtrasPage.Keys.ToList();
            int from = page.IndexOf("<StackPanel x:Name=\"" + key + "Page\"", StringComparison.Ordinal);
            Assert.True(from > 0, key + " has no page");
            int next = keys.IndexOf(key) + 1;
            int to = next < keys.Count
                ? page.IndexOf("<StackPanel x:Name=\"" + keys[next] + "Page\"", StringComparison.Ordinal)
                : page.Length;
            return page.Substring(from, to - from);
        }

        private static AutomationHost NewHost(Func<DateTimeOffset> now = null, TimeSpan? wait = null)
            => new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                "PadForge.Tests.Pages." + Guid.NewGuid().ToString("N"), _ => AutomationProgram.Unknown, now, wait);

        private static DashboardViewModel Attached(AutomationHost host)
        {
            var vm = new DashboardViewModel();
            vm.AttachAutomation(host, Dispatcher.CurrentDispatcher);
            return vm;
        }

        /// <summary>Runs the test thread's queue until <paramref name="done"/>
        /// holds: a change a worker made reaches the page through it.</summary>
        private static void PumpUntil(Func<bool> done, int timeoutMs = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(clock.ElapsedMilliseconds < timeoutMs, "the wait ran out");
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
        }

        private static AutomationActivityEntry Entry(int minute, string program, AutomationAskKind kind, bool ok, string argument = null)
            => new AutomationActivityEntry(new DateTimeOffset(2026, 11, 23, 14, minute, 0, TimeSpan.Zero),
                new AutomationProgram(program, 100 + minute, program + ".exe"), new AutomationAsk(kind, argument),
                ok ? "ok" : "error not-allowed", ok);

        // ── The catalog ────────────────────────────────────────────────

        /// <summary>Fourteen extras in the decided order, the last two in
        /// a fourth group, With Other Programs, each with the glyph its page
        /// wears.</summary>
        [Fact]
        public void TheCatalog_HasFourteenExtras_TheLastTwoInAFourthGroup()
        {
            Assert.Equal(14, DashboardViewModel.ExtrasCount);
            Assert.Equal(new[]
            {
                "WebController", "HeadTracking", "GameTelemetry", "TwitchChat",
                "RemoteLink", "MotionServer", "GameMods",
                "Overlays", "TouchpadOverlay", "ScreenOverlay", "Crosshair", "Magnifier",
                "Automation", "Plugins",
            }, ExtrasPage.Keys);

            var doc = XDocument.Parse(Page());
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var catalog = doc.Descendants().Single(e => (string)e.Attribute(x + "Name") == "CatalogView");
            var groups = catalog.Elements().Where(e => e.Name.LocalName == "TileGridPanel").ToList();
            Assert.Equal(4, groups.Count);
            var programs = groups[3];
            Assert.Equal("{Binding Extras_Group_Programs, Source={x:Static strings:Strings.Instance}}",
                (string)programs.ElementsBeforeSelf().Last().Attribute("Text"));
            var tiles = programs.Elements().ToList();
            Assert.Equal(new[] { "AutomationTile", "PluginsTile" }, tiles.Select(t => (string)t.Attribute(x + "Name")));
            Assert.Equal("&#xE9F5;", Regex.Match(Page(), "x:Name=\"AutomationTile\" Key=\"Automation\" Glyph=\"([^\"]+)\"").Groups[1].Value);
            Assert.Equal("&#xECF0;", Regex.Match(Page(), "x:Name=\"PluginsTile\" Key=\"Plugins\" Glyph=\"([^\"]+)\"").Groups[1].Value);
            Assert.Equal("{Binding EnableAutomation}", (string)tiles[0].Attribute("IsOn"));
            Assert.Equal("{Binding AutomationStatus}", (string)tiles[0].Attribute("Status"));
            Assert.Equal("{Binding EnablePlugins}", (string)tiles[1].Attribute("IsOn"));
            Assert.Equal("{Binding PluginsStatus}", (string)tiles[1].Attribute("Status"));

            // The English the tiles carry, from the file.
            string resx = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx");
            Assert.Contains("<data name=\"Extras_Group_Programs\" xml:space=\"preserve\"><value>With Other Programs</value></data>", resx);
            Assert.Contains("<data name=\"Extras_Tile_Automation\" xml:space=\"preserve\"><value>Let launchers, scripts and assistants work with PadForge.</value></data>", resx);
            Assert.Contains("<data name=\"Extras_Tile_Plugins\" xml:space=\"preserve\"><value>Add devices that programs provide.</value></data>", resx);
        }

        // ── The Automation page ────────────────────────────────────────

        /// <summary>The level is a choice of four with one line under it
        /// that says what the chosen one allows. A pick sets the level, the
        /// line follows, and the reset puts Commands back.</summary>
        [Fact]
        public void TheLevelChoice_SetsTheLevel_AndTheLineUnderIt()
        {
            var vm = new DashboardViewModel();
            var s = Strings.Instance;
            Assert.Equal(new[] { s.Extras_Automation_Level_Commands, s.Extras_Automation_Level_Read, s.Extras_Automation_Level_SendInputs, s.Extras_Automation_Level_ChangeSettings },
                vm.AutomationLevelNames);
            Assert.Equal(0, vm.AutomationLevelIndex);
            Assert.Equal(s.Extras_Automation_LevelLine_Commands, vm.AutomationLevelLine);

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            var lines = new[]
            {
                s.Extras_Automation_LevelLine_Commands, s.Extras_Automation_LevelLine_Read,
                s.Extras_Automation_LevelLine_SendInputs, s.Extras_Automation_LevelLine_ChangeSettings,
            };
            Assert.Equal(4, lines.Distinct().Count());
            for (int i = 3; i >= 0; i--)
            {
                changed.Clear();
                vm.AutomationLevelIndex = i;
                Assert.Equal((AutomationLevel)i, vm.AutomationLevel);
                Assert.Equal(lines[i], vm.AutomationLevelLine);
                if (i != 0 || changed.Count > 0)
                {
                    Assert.Contains(nameof(DashboardViewModel.AutomationLevel), changed);
                    Assert.Contains(nameof(DashboardViewModel.AutomationLevelLine), changed);
                    Assert.Contains(nameof(DashboardViewModel.AutomationLevelIndex), changed);
                }
            }
            // A place outside the four changes nothing.
            vm.AutomationLevelIndex = 2;
            vm.AutomationLevelIndex = -1;
            vm.AutomationLevelIndex = 4;
            Assert.Equal(AutomationLevel.SendInputs, vm.AutomationLevel);
            vm.ResetSettingCommand.Execute(nameof(DashboardViewModel.AutomationLevel));
            Assert.Equal(AutomationLevel.Commands, vm.AutomationLevel);
            Assert.Equal(s.Extras_Automation_LevelLine_Commands, vm.AutomationLevelLine);

            // The page shows one line, the chosen level's, under the choice.
            string body = PageOf("Automation");
            Assert.Single(Regex.Matches(body, "\\{Binding AutomationLevelLine\\}"));
            Assert.DoesNotContain("Extras_Automation_LevelLine_", body);
            Assert.Contains("SelectedIndex=\"{Binding AutomationLevelIndex, Mode=TwoWay}\"", body);
            Assert.Contains("ItemsSource=\"{Binding AutomationLevelNames}\"", body);
        }

        /// <summary>The block an assistant's settings take names this exe as
        /// the command with --mcp, as JSON in the issue's form, and the same
        /// two facts in TOML under the table mcp_servers.padforge. A path
        /// with a backslash, a quote and a letter outside ASCII comes
        /// through both whole.</summary>
        [Theory]
        [InlineData(@"C:\PadForge\PadForge.exe")]
        [InlineData(@"D:\Tools\Pad ""Forge""\Café\PadForge.exe")]
        public void TheAssistantBlock_NamesThisExe_WithTheMcpSwitch(string exe)
        {
            var vm = new DashboardViewModel { AutomationExePath = exe };
            using (var json = JsonDocument.Parse(vm.AutomationAssistantJson))
            {
                var server = json.RootElement.GetProperty("mcpServers").GetProperty("padforge");
                Assert.Equal(exe, server.GetProperty("command").GetString());
                Assert.Equal(new[] { "--mcp" }, server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
                Assert.Single(json.RootElement.EnumerateObject());
            }
            // The form the issue shows: the server on one line.
            Assert.StartsWith("{\n  \"mcpServers\": {\n    \"padforge\": { \"command\": \"", vm.AutomationAssistantJson);
            Assert.EndsWith("\", \"args\": [\"--mcp\"] }\n  }\n}", vm.AutomationAssistantJson);
            // A letter outside ASCII stays itself, as a person reads the path.
            Assert.DoesNotContain("\\u00", vm.AutomationAssistantJson);

            var toml = vm.AutomationAssistantToml.Split('\n');
            Assert.Equal("[mcp_servers.padforge]", toml[0]);
            var command = Regex.Match(toml[1], "^command = \"((?:[^\"\\\\]|\\\\.)*)\"$");
            Assert.True(command.Success, toml[1]);
            Assert.Equal(exe, Regex.Unescape(command.Groups[1].Value));
            Assert.Equal("args = [\"--mcp\"]", toml[2]);
            Assert.Equal(3, toml.Length);

            // The app's own: the exe that runs.
            Assert.Equal(Environment.ProcessPath, new DashboardViewModel().AutomationExePath);
            // Copy puts the block on the clipboard, which the test host leaves alone.
            vm.CopyAutomationJsonCommand.Execute(null);
            vm.CopyAutomationTomlCommand.Execute(null);
        }

        /// <summary>The quoted string escapes what JSON and a TOML basic
        /// string both escape, and leaves every other letter as it is.</summary>
        [Fact]
        public void TheQuotedString_EscapesTheBackslashTheQuoteAndControls()
        {
            Assert.Equal("\"C:\\\\a\\\"b\\tc\\u0001\"", DashboardViewModel.QuotedString("C:\\a\"b\tc\u0001"));
            Assert.Equal("\"plain\"", DashboardViewModel.QuotedString("plain"));
            Assert.Equal("\"Café\"", DashboardViewModel.QuotedString("Café"));
        }

        /// <summary>A question a program puts shows on the page while it
        /// waits, oldest first, with who asks, what and the detail. Allow
        /// and Don't Allow each end it with their answer, and it leaves the
        /// page.</summary>
        [Fact]
        public void AQuestion_ShowsOnThePage_AndEitherAnswerEndsIt()
        {
            using var host = NewHost();
            var vm = Attached(host);
            Assert.False(vm.HasAutomationRequests);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            var who = new AutomationProgram("Playnite", 42, "Playnite.DesktopApp.exe");
            var first = host.Approvals.AskAsync(new ApprovalRequest(who, "Run a program when a profile starts", new[] { @"C:\Tools\run.exe", "--fast" }));
            var second = host.Approvals.AskAsync(new ApprovalRequest(new AutomationProgram("powershell", 7, "powershell.exe"), "Import a profile", Array.Empty<string>()));
            Assert.True(vm.HasAutomationRequests);
            Assert.Contains(nameof(DashboardViewModel.HasAutomationRequests), raised);
            Assert.Equal(2, vm.AutomationRequests.Count);
            var item = vm.AutomationRequests[0];
            Assert.Equal("Run a program when a profile starts", item.Title);
            Assert.Equal(new[] { @"C:\Tools\run.exe", "--fast" }, item.Detail);
            Assert.Equal(string.Format(Strings.Instance.Automation_Request_FromClaimed_Format, "Playnite",
                string.Format(Strings.Instance.Automation_Program_NotSigned_Format, "Playnite.DesktopApp.exe")), item.From);
            Assert.Equal(string.Format(Strings.Instance.Automation_Request_From_Format,
                string.Format(Strings.Instance.Automation_Program_NotSigned_Format, "powershell.exe")), vm.AutomationRequests[1].From);
            Assert.Equal("Automation_Allow_" + item.Id, item.AllowId);
            Assert.Equal("Automation_Deny_" + item.Id, item.DenyId);

            item.AllowCommand.Execute(null);
            Assert.True(first.IsCompleted);
            Assert.Equal(ApprovalAnswer.Allowed, Answer(first));
            Assert.Single(vm.AutomationRequests);
            Assert.False(second.IsCompleted);

            vm.AutomationRequests[0].DenyCommand.Execute(null);
            Assert.Equal(ApprovalAnswer.Denied, Answer(second));
            Assert.Empty(vm.AutomationRequests);
            Assert.False(vm.HasAutomationRequests);
            Assert.Empty(host.Approvals.Pending);

            // An answer to a question gone already changes nothing.
            item.DenyCommand.Execute(null);
            Assert.Equal(ApprovalAnswer.Allowed, Answer(first));

            // The page lists them in a card that shows only while one waits,
            // Don't Allow first.
            string body = PageOf("Automation");
            Assert.Contains("x:Name=\"AutomationWaitingCard\" Style=\"{StaticResource ExtraCard}\"\n                        Visibility=\"{Binding HasAutomationRequests, Converter={StaticResource BoolToVisibilityConverter}}\"",
                body.Replace("\r\n", "\n"));
            int deny = body.IndexOf("Command=\"{Binding DenyCommand}\"", StringComparison.Ordinal);
            int allow = body.IndexOf("Command=\"{Binding AllowCommand}\"", StringComparison.Ordinal);
            Assert.True(deny > 0 && allow > deny, "Don't Allow does not stand first");
            Assert.Contains("ItemsSource=\"{Binding Detail}\"", body);
        }

        /// <summary>A question nobody answers ends as no when its time runs
        /// out, and leaves the page.</summary>
        [Fact]
        public void AQuestionNobodyAnswers_CountsAsNo_AndLeavesThePage()
        {
            using var host = NewHost(wait: TimeSpan.FromMilliseconds(150));
            var vm = Attached(host);
            var asked = host.Approvals.AskAsync(new ApprovalRequest(AutomationProgram.Unknown, "Turn on Remote Link", null));
            Assert.Single(vm.AutomationRequests);
            Assert.Equal(Strings.Instance.Automation_Request_FromUnknown, vm.AutomationRequests[0].From);
            Assert.Empty(vm.AutomationRequests[0].Detail);
            PumpUntil(() => asked.IsCompleted && vm.AutomationRequests.Count == 0);
            Assert.Equal(ApprovalAnswer.TimedOut, Answer(asked));
            Assert.False(vm.HasAutomationRequests);
        }

        /// <summary>Who asks: Windows' image name, and the program's own name
        /// marked as its claim where it gave one that is not the image's.</summary>
        [Fact]
        public void WhoAsks_MarksTheProgramsOwnName_AsItsClaim()
        {
            var s = Strings.Instance;
            string NotSigned(string image) => string.Format(s.Automation_Program_NotSigned_Format, image);
            Assert.Equal(string.Format(s.Automation_Request_From_Format, NotSigned("powershell.exe")),
                AutomationRequestItem.FromLine(new AutomationProgram("powershell", 1, "powershell.exe")));
            Assert.Equal(string.Format(s.Automation_Request_From_Format, NotSigned("powershell.exe")),
                AutomationRequestItem.FromLine(new AutomationProgram(null, 1, "powershell.exe")));
            Assert.Equal(string.Format(s.Automation_Request_FromClaimed_Format, "My Launcher", NotSigned("python.exe")),
                AutomationRequestItem.FromLine(new AutomationProgram("My Launcher", 1, "python.exe")));
            // The full path and the signer, where Windows gave them.
            Assert.Equal(string.Format(s.Automation_Request_From_Format,
                    string.Format(s.Automation_Program_Signed_Format, @"C:\Program Files\PowerShell\pwsh.exe", "Microsoft Corporation")),
                AutomationRequestItem.FromLine(new AutomationProgram("pwsh", 1, "pwsh.exe", @"C:\Program Files\PowerShell\pwsh.exe", "Microsoft Corporation")));
            Assert.Equal(string.Format(s.Automation_Request_FromClaimedOnly_Format, "My Launcher"),
                AutomationRequestItem.FromLine(new AutomationProgram("My Launcher", 1, null)));
            Assert.Equal(s.Automation_Request_FromUnknown, AutomationRequestItem.FromLine(AutomationProgram.Unknown));
            Assert.Equal(s.Automation_Request_FromUnknown, AutomationRequestItem.FromLine(null));
            Assert.Contains("“{0}”", s.Automation_Request_FromClaimed_Format);
        }

        /// <summary>The card shows six detail lines and counts the rest. The
        /// page shows every line.</summary>
        [Fact]
        public void TheCard_ShowsSixDetailLines_AndCountsTheRest()
        {
            var lines = Enumerable.Range(1, 9).Select(i => "Line " + i).ToArray();
            var item = new AutomationRequestItem(new PendingApproval(5, new ApprovalRequest(AutomationProgram.Unknown, "Import", lines),
                DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(2)), (_, _) => true);
            Assert.Equal(9, item.Detail.Count);
            Assert.Equal(lines.Take(6), item.CardDetail);
            Assert.Equal(string.Format(Strings.Instance.Automation_Request_MoreDetail_Format, 3), item.CardDetailRest);
            var six = new AutomationRequestItem(new PendingApproval(6, new ApprovalRequest(AutomationProgram.Unknown, "Import", lines.Take(6).Append(" ").ToArray()),
                DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(2)), (_, _) => true);
            Assert.Equal(6, six.CardDetail.Count);
            Assert.Null(six.CardDetailRest);
        }

        /// <summary>Activity lists the calls newest first, each with its
        /// time, program, what it asked and what came of it. The oldest go
        /// as the record drops them, and Clear empties the list.</summary>
        [Fact]
        public void Activity_ListsNewestFirst_AndClearEmptiesIt()
        {
            using var host = NewHost();
            var vm = Attached(host);
            Assert.False(vm.HasAutomationActivity);
            host.Activity.Add(Entry(1, "playnite", AutomationAskKind.ActivateProfile, true, "Forza Wheel"));
            host.Activity.Add(Entry(2, "powershell", AutomationAskKind.EngineOn, false));
            host.Activity.Add(Entry(3, "ahk", AutomationAskKind.State, true));
            Assert.True(vm.HasAutomationActivity);
            Assert.Equal(new[] { "ahk", "powershell", "playnite" }, vm.AutomationActivity.Select(a => a.Program));
            var engine = vm.AutomationActivity[1];
            Assert.Equal(Strings.Instance.Automation_Asked_EngineOn, engine.Asked);
            Assert.Equal(Strings.Instance.Automation_Result_NotAllowed, engine.Result);
            Assert.Equal("error not-allowed", engine.Entry.Result);
            Assert.Equal(Strings.Instance.Automation_Result_Done, vm.AutomationActivity[0].Result);
            Assert.Equal(string.Format(Strings.Instance.Automation_Asked_Activate_Format, "Forza Wheel"), vm.AutomationActivity[2].Asked);
            Assert.Equal(new DateTimeOffset(2026, 11, 23, 14, 2, 0, TimeSpan.Zero).ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentUICulture), engine.Time);
            Assert.Contains(engine.Program, engine.ToString());

            // A full record takes no newcomer's read, and the list follows.
            for (int i = 0; i < AutomationActivity.Capacity; i++)
                host.Activity.Add(Entry(4, "script" + i, AutomationAskKind.Version, true));
            Assert.Equal(AutomationActivity.Capacity, vm.AutomationActivity.Count);
            Assert.Equal("script" + (AutomationActivity.Capacity - 4), vm.AutomationActivity[0].Program);
            Assert.Equal("playnite", vm.AutomationActivity[^1].Program);
            // An ask the program made before merges with it, and moves to
            // the top with its count.
            var ahk = vm.AutomationActivity.Single(a => a.Program == "ahk");
            host.Activity.Add(Entry(3, "ahk", AutomationAskKind.State, true));
            Assert.Equal(AutomationActivity.Capacity, vm.AutomationActivity.Count);
            Assert.Equal("ahk", vm.AutomationActivity[0].Program);
            Assert.Equal(2, vm.AutomationActivity[0].Entry.Asked.Count);
            Assert.DoesNotContain(ahk, vm.AutomationActivity);
            Assert.Equal("playnite", vm.AutomationActivity[^1].Program);

            vm.ClearAutomationActivityCommand.Execute(null);
            Assert.Equal(0, host.Activity.Count);
            Assert.Empty(vm.AutomationActivity);
            Assert.False(vm.HasAutomationActivity);
            host.Activity.Add(Entry(5, "after", AutomationAskKind.ShowWindow, true));
            Assert.Equal(new[] { "after" }, vm.AutomationActivity.Select(a => a.Program));
        }

        /// <summary>Every error word the code answers a program with has a
        /// phrase in the interface's words, which never carries the word
        /// itself. An answer with no phrase reads Not done. The record keeps
        /// the answer as it went out.</summary>
        [Fact]
        public void EveryErrorWord_TheCodeAnswers_HasAPhrase()
        {
            var words = new SortedSet<string>(StringComparer.Ordinal);
            var pipeCodes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string file in System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(ExtrasPageTestsRoot(), "PadForge.App"), "*.cs", System.IO.SearchOption.AllDirectories))
            {
                string code = System.IO.File.ReadAllText(file);
                foreach (Match m in Regex.Matches(code, "\"error ([a-z][a-z-]*)"))
                    words.Add(m.Groups[1].Value);
                // The Automation pipe's codes, in its own folder: err and a
                // code, the code a refusal or failure is given, and the
                // request's own error.
                if (!file.Replace('\\', '/').Contains("/Services/Automation/", StringComparison.Ordinal)) continue;
                foreach (string pattern in new[]
                {
                    @"""err ([a-z][a-z-]*)",
                    @"Error\(""([a-z][a-z-]*)""",
                    @"[Ee]rror = ""([a-z][a-z-]*)""",
                    @"Fail\(session, r, AutomationAskKind\.\w+, [^,]+, ""([a-z][a-z-]*)""",
                })
                    foreach (Match m in Regex.Matches(code, pattern))
                        pipeCodes.Add(m.Groups[1].Value);
            }
            // Every code the Automation pipe gives words for in its log.
            string line = System.IO.File.ReadAllText(System.IO.Path.Combine(ExtrasPageTestsRoot(), "PadForge.App", "Services", "Automation", "AutomationLine.cs"));
            string table = line.Substring(line.IndexOf("internal static string WordsFor(", StringComparison.Ordinal));
            table = table.Substring(0, table.IndexOf("};", StringComparison.Ordinal));
            foreach (Match m in Regex.Matches(table, "\"([a-z][a-z-]*)\" =>"))
                pipeCodes.Add(m.Groups[1].Value);
            // Positive controls: the command set's own words and the pipe's
            // are found.
            foreach (string known in new[] { "not-allowed", "not-available", "busy", "bad-argument", "unknown-profile",
                         "unknown-slot", "unknown-command", "empty", "internal" })
                Assert.Contains(known, words);
            foreach (string known in new[] { "refused", "denied", "engine-stopped", "busy", "unknown-device", "not-owner",
                         "rate-limited", "too-long", "hello-first", "bad-tag" })
                Assert.Contains(known, pipeCodes);
            words.UnionWith(pipeCodes);
            var s = Strings.Instance;
            foreach (string word in words)
            {
                Assert.True(AutomationActivityItem.RefusalPhrases.ContainsKey(word), "no phrase for error " + word);
                string phrase = AutomationActivityItem.RefusalWords("error " + word);
                Assert.False(string.IsNullOrWhiteSpace(phrase));
                Assert.NotEqual(s.Automation_Result_NotDone, phrase);
                // A hyphenated word is the protocol's and never shows. A plain
                // English word (busy, empty) may sit inside a sentence, never
                // stand alone as the result.
                if (word.Contains('-')) Assert.DoesNotContain(word, phrase, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(word, phrase, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain("error", phrase, StringComparison.OrdinalIgnoreCase);
                // A word with more after it reads the same.
                Assert.Equal(phrase, AutomationActivityItem.RefusalWords("error " + word + " more"));
                // The Automation pipe's form, with its words for a log, reads
                // the same.
                Assert.Equal(phrase, AutomationActivityItem.RefusalWords("err " + word + " the request failed"));
            }
            Assert.Equal(words.Count, AutomationActivityItem.RefusalPhrases.Values.Select(p => p(s)).Distinct().Count());
            foreach (string other in new[] { "error never-said", "error ", "nonsense", "", null })
                Assert.Equal(s.Automation_Result_NotDone, AutomationActivityItem.RefusalWords(other));
        }

        private static string ExtrasPageTestsRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        /// <summary>Calls made on a worker reach the list on the page's
        /// thread, all of them, in one pass for a burst.</summary>
        [Fact]
        public void Activity_FromAWorker_ReachesThePagesThread()
        {
            using var host = NewHost();
            var vm = Attached(host);
            var worker = new Thread(() =>
            {
                for (int i = 0; i < 50; i++) host.Activity.Add(Entry(i % 60, "worker" + i, AutomationAskKind.QueryProfile, true));
            });
            worker.Start();
            Assert.True(worker.Join(5000));
            PumpUntil(() => vm.AutomationActivity.Count == 50);
            Assert.Equal("worker49", vm.AutomationActivity[0].Program);
        }

        /// <summary>Undo shows on a call only while the record's owner says
        /// it can be undone, and only once the host has the call that undoes
        /// it.</summary>
        [Fact]
        public void Undo_ShowsOnlyOnACallTheHostCanUndo()
        {
            using var host = NewHost();
            var vm = Attached(host);
            var kept = Entry(1, "assistant", AutomationAskKind.ControllerOff, true, "2");
            var other = Entry(2, "assistant", AutomationAskKind.State, true);
            host.Activity.Add(kept);
            host.Activity.Add(other);
            Assert.All(vm.AutomationActivity, a => Assert.False(a.CanUndo));

            // A rule with no undo call yet offers nothing.
            host.CanUndo = e => ReferenceEquals(e, kept);
            Assert.All(vm.AutomationActivity, a => Assert.False(a.CanUndo));

            var undone = new List<AutomationActivityEntry>();
            host.Undo = e => { undone.Add(e); host.CanUndo = _ => false; };
            var row = vm.AutomationActivity.Single(a => a.CanUndo);
            Assert.Same(kept, row.Entry);
            vm.AutomationActivity.Single(a => !a.CanUndo).UndoCommand.Execute(null);
            Assert.Empty(undone);
            row.UndoCommand.Execute(null);
            Assert.Equal(new[] { kept }, undone);
            Assert.All(vm.AutomationActivity, a => Assert.False(a.CanUndo));

            // The row's button is bound to the rule and the call.
            string body = PageOf("Automation");
            Assert.Contains("Command=\"{Binding UndoCommand}\"", body);
            Assert.Contains("Visibility=\"{Binding CanUndo, Converter={StaticResource BoolToVisibilityConverter}}\"", body);
        }

        /// <summary>Save a Library, Save the Skill and Undo are calls later
        /// parts give the host. Each button is absent until its call is
        /// there, and the More row's hint names only what More holds.</summary>
        [Fact]
        public void TheProvidedButtons_ShowOnlyOnceTheHostHasTheirCalls()
        {
            using var host = NewHost();
            var vm = Attached(host);
            Assert.False(vm.HasAutomationLibraries);
            Assert.False(vm.CanSaveAutomationSkill);
            Assert.Equal(Strings.Instance.Extras_More_Automation, vm.AutomationMoreHint);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            var libraries = AutomationLibraries.Instance;
            host.Libraries = libraries;
            Assert.True(vm.HasAutomationLibraries);
            Assert.Same(libraries, vm.AutomationLibraries);
            Assert.Contains(nameof(DashboardViewModel.HasAutomationLibraries), raised);

            string folder = null;
            host.SaveSkill = f => folder = f;
            Assert.True(vm.CanSaveAutomationSkill);
            Assert.Equal(Strings.Instance.Extras_More_Automation_WithSkill, vm.AutomationMoreHint);
            vm.AutomationSaveSkill(@"C:\Skills");
            Assert.Equal(@"C:\Skills", folder);
            host.SaveSkill = null;
            Assert.False(vm.CanSaveAutomationSkill);

            // The dialog's file types are the libraries', in order.
            var s = Strings.Instance;
            Assert.Equal(s.Automation_Library_AutoHotkey_Filter + "|" + s.Automation_Library_Python_Filter + "|" + s.Automation_Library_PowerShell_Filter,
                ExtrasPage.LibraryFilter(((IAutomationLibraries)libraries).All));

            // A view model with no host offers none of them.
            var bare = new DashboardViewModel();
            Assert.False(bare.HasAutomationLibraries);
            Assert.False(bare.CanSaveAutomationSkill);
            Assert.Null(bare.AutomationLibraries);
            Assert.Null(bare.AutomationSaveSkill);

            string body = PageOf("Automation");
            Assert.Contains("x:Name=\"AutomationScriptsCard\" Style=\"{StaticResource ExtraCard}\"\n                        Visibility=\"{Binding HasAutomationLibraries, Converter={StaticResource BoolToVisibilityConverter}}\"",
                body.Replace("\r\n", "\n"));
            Assert.Contains("<controls:SettingRow MinHeight=\"44\" Visibility=\"{Binding CanSaveAutomationSkill, Converter={StaticResource BoolToVisibilityConverter}}\">", body);
            Assert.Contains("Click=\"SaveAutomationLibrary_Click\"", body);
            Assert.Contains("Click=\"SaveAutomationSkill_Click\"", body);
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            Assert.Contains("private void SaveAutomationLibrary_Click(object sender, RoutedEventArgs e)", code);
            Assert.Contains("var picked = all[Math.Clamp(dialog.FilterIndex, 1, all.Count) - 1];", code);
            Assert.Contains("libraries.Save(picked.Kind, dialog.FileName);", code);
            Assert.Contains("new Microsoft.Win32.OpenFolderDialog", code);
            Assert.Contains("save(dialog.FolderName);", code);
        }

        /// <summary>More holds Copy as TOML and Save the Skill, and the
        /// note follows it. The record's list grows to one height and then
        /// scrolls inside its card.</summary>
        [Fact]
        public void ThePage_KeepsTomlAndTheSkillUnderMore_AndTheRecordAtOneHeight()
        {
            string body = PageOf("Automation");
            int more = body.IndexOf("<controls:MoreRow x:Name=\"AutomationMore\" Style=\"{StaticResource MoreCardRow}\" StateKey=\"Extras.Automation.More\"", StringComparison.Ordinal);
            Assert.True(more > 0);
            int end = body.IndexOf("</controls:MoreRow>", more, StringComparison.Ordinal);
            string kept = body.Substring(more, end - more), shown = body.Substring(0, more);
            Assert.Contains("Command=\"{Binding CopyAutomationTomlCommand}\"", kept);
            Assert.Contains("AutomationProperties.AutomationId=\"Automation_SaveSkill\"", kept);
            Assert.Contains("Hint=\"{Binding AutomationMoreHint}\"", kept);
            Assert.DoesNotContain("CopyAutomationTomlCommand", shown);
            Assert.Contains("Command=\"{Binding CopyAutomationJsonCommand}\"", shown);
            Assert.Contains("Text=\"{Binding AutomationAssistantJson}\"", shown);
            Assert.Matches(new Regex("</controls:FoldList>\\s*<TextBlock Text=\"\\{Binding Extras_Automation_Note, Source=\\{x:Static strings:Strings.Instance\\}\\}\" Style=\"\\{StaticResource ExtraNote\\}\"/>\\s*</StackPanel>\\s*$"),
                Regex.Replace(body.Substring(0, body.LastIndexOf("<!-- ═══ Plugins", StringComparison.Ordinal)), "\\s+$", ""));

            var list = Regex.Match(body, "<ItemsControl x:Name=\"AutomationActivityList\"[^>]*>", RegexOptions.Singleline).Value;
            Assert.Contains("MaxHeight=\"288\"", list);
            Assert.Contains("ItemsSource=\"{Binding AutomationActivity}\"", list);
            Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", list);
            Assert.Contains("Command=\"{Binding ClearAutomationActivityCommand}\"", body);
        }

        // ── The Plugins page ───────────────────────────────────────────

        /// <summary>The Plugins switch is off by default and its line says
        /// stopped, then that no plugin runs, until the plugin manager gives
        /// a line of its own. The page's frame holds the switch, the status
        /// card, the plugin manager's card area and the note.</summary>
        [Fact]
        public void ThePluginsSwitch_IsOffByDefault_AndItsLineFollowsIt()
        {
            var vm = new DashboardViewModel();
            var s = Strings.Instance;
            Assert.False(vm.EnablePlugins);
            Assert.False(vm.IsPluginsRunning);
            Assert.Equal(s.Common_Stopped, vm.PluginsStatus);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.EnablePlugins = true;
            Assert.Equal(s.Extras_Plugins_Status_None, vm.PluginsStatus);
            Assert.Contains(nameof(DashboardViewModel.PluginsStatus), raised);
            Assert.Contains(nameof(DashboardViewModel.ExtrasInUseCount), raised);
            vm.PluginsStatus = "Two plugins run.";
            Assert.Equal("Two plugins run.", vm.PluginsStatus);
            vm.PluginsStatus = null;
            Assert.Equal(s.Extras_Plugins_Status_None, vm.PluginsStatus);
            vm.ResetSettingCommand.Execute(nameof(DashboardViewModel.EnablePlugins));
            Assert.False(vm.EnablePlugins);
            Assert.Equal(s.Common_Stopped, vm.PluginsStatus);

            string body = PageOf("Plugins");
            Assert.Contains("IsLive=\"{Binding IsPluginsRunning}\"", body);
            Assert.Contains("Text=\"{Binding PluginsStatus}\"", body);
            Assert.Contains("<StackPanel x:Name=\"PluginsCards\">", body);
        }

        // ── Saved, reset, searched ─────────────────────────────────────

        /// <summary>EnablePlugins has EnableAutomation's footprint: the
        /// settings file's element, off by default, loaded, saved, put back
        /// by a factory reset, its reset and its dirty mark.</summary>
        [Fact]
        public void ThePluginsSwitch_IsSaved_AsTheAutomationSwitchIs()
        {
            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("_mainVm.Dashboard.EnablePlugins = appSettings.EnablePlugins;", service);
            Assert.Contains("SettingsManager.EnablePlugins = appSettings.EnablePlugins;", service);
            Assert.Contains("SettingsManager.EnablePlugins = _mainVm.Dashboard.EnablePlugins;", service);
            Assert.Contains("EnablePlugins = _mainVm.Dashboard.EnablePlugins,", service);
            Assert.Contains("SettingsManager.EnablePlugins = false;", service);
            Assert.False(new PadForge.Services.AppSettingsData().EnablePlugins);
            var xml = new System.Xml.Serialization.XmlSerializer(typeof(PadForge.Services.AppSettingsData));
            using (var write = new System.IO.StringWriter())
            {
                xml.Serialize(write, new PadForge.Services.AppSettingsData { EnablePlugins = true });
                Assert.Contains("<EnablePlugins>true</EnablePlugins>", write.ToString());
                var back = (PadForge.Services.AppSettingsData)xml.Deserialize(new System.IO.StringReader(write.ToString()));
                Assert.True(back.EnablePlugins);
            }
            Assert.Contains("or nameof(DashboardViewModel.EnablePlugins)", ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs"));
        }

        /// <summary>Search finds both extras, the switches, the level, and
        /// each row and action of the Automation page.</summary>
        [Fact]
        public void Search_FindsTheExtras_TheirSwitches_TheLevel_AndEachRow()
        {
            var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var index = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName).Entries.Where(e => e.Page == SearchPage.Extras).ToList();
            bool Has(SearchKind kind, string key, string extra) =>
                index.Any(e => e.Kind == kind && e.Label.Key == key && e.ExtraKey == extra);

            Assert.True(index.Any(e => e.Kind == SearchKind.Extra && e.Label.Key == "Extras_Automation_Title"), "no Automation extra");
            Assert.True(index.Any(e => e.Kind == SearchKind.Extra && e.Label.Key == "Extras_Plugins_Title"), "no Plugins extra");
            Assert.True(Has(SearchKind.Setting, "Extras_Automation_Switch", "Automation"), "no Automation switch");
            Assert.True(Has(SearchKind.Setting, "Extras_Automation_Level", "Automation"), "no level");
            Assert.True(Has(SearchKind.Setting, "Extras_Plugins_Switch", "Plugins"), "no Plugins switch");
            Assert.True(Has(SearchKind.Action, "Extras_Automation_SaveLibrary", "Automation"), "no Save a Library");
            Assert.True(Has(SearchKind.Action, "Extras_Automation_CopyToml", "Automation"), "no Copy as TOML");
            Assert.True(Has(SearchKind.Action, "Extras_Automation_SaveSkill", "Automation"), "no Save the Skill");
            var level = index.Single(e => e.Kind == SearchKind.Setting && e.Label.Key == "Extras_Automation_Level");
            Assert.Equal("AutomationLevelIndex", level.Value?.Path);
            Assert.Equal(SearchValueKind.Choice, level.Value?.Kind);
            Assert.Equal("Automation_Level", level.TargetAutomationId);
            Assert.True(index.Single(e => e.Kind == SearchKind.Action && e.Label.Key == "Extras_Automation_CopyToml").UnderMore);
        }

        /// <summary>The External Control card on the Profiles page opens the
        /// Extras page on Automation, which has a page now.</summary>
        [Fact]
        public void TheProfilesCard_OpensAutomationsPage()
        {
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("ProfilesPageView.OpenAutomationRequested += (s, e) => OpenExtra(\"Automation\");", window);
            string automation = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.Automation.cs");
            Assert.Contains("if (!ExtrasPageView.HasPage(key)) return;", automation);
            Assert.Contains("<StackPanel x:Name=\"AutomationPage\" Visibility=\"Collapsed\">", Page());
            Assert.Contains("Click=\"OpenAutomation_Click\"", ExtrasPageTests.RepoText("PadForge.App", "Views", "ProfilesPage.xaml"));
        }

        /// <summary>The window hands the page the host, and the card the
        /// page's own list of questions, so the two answer the same
        /// questions. The card closes with the window.</summary>
        [Fact]
        public void TheWindow_GivesThePageAndTheCardOneListOfQuestions()
        {
            string automation = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.Automation.cs");
            Assert.Contains("_viewModel.Dashboard.AttachAutomation(_automation, Dispatcher);", automation);
            Assert.Contains("_requestCard = new Views.AutomationRequestCard(_viewModel.Dashboard.AutomationRequests,", automation);
            Assert.Contains("_automation.Clients.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(ShowAutomationState));", automation);
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int dispose = window.IndexOf("_automation?.Dispose();", StringComparison.Ordinal);
            int close = window.IndexOf("_requestCard?.Close();", StringComparison.Ordinal);
            Assert.True(dispose > 0 && close > dispose, "the card does not close with the window");
        }
    }
}
