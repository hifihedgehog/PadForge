using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.Services.Install;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Automation page's assistant rows, Allowed Programs card, status
    /// line and the title bar's connected mark (#600 items 9, 13 and 16).
    /// The rows come from one check as the user, which a seam answers here,
    /// and every install a row's button starts goes to the same seam. No
    /// program starts and no file of the bench's is read.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class AutomationAssistantRowsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Rows." + Guid.NewGuid().ToString("N"));

        public AutomationAssistantRowsTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private static AutomationHost NewHost(Func<DateTimeOffset> now = null)
            => new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                "PadForge.Tests.Rows." + Guid.NewGuid().ToString("N"), _ => AutomationProgram.Unknown, now);

        /// <summary>The install runs a row asked for, and the check's answer.</summary>
        private sealed class Installs
        {
            public readonly List<InstallRequest> Runs = new();
            public List<UserInstall.AssistantCheck> Checks = new();
            public Func<InstallRequest, InstallRun> Answer;

            public Task<InstallRun> Run(InstallRequest request)
            {
                Runs.Add(request);
                if (Answer?.Invoke(request) is { } answer) return Task.FromResult(answer);
                if (request.What == InstallWhat.Assistant && request.Target == null && request.Check)
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(Checks.Select(c => new
                    {
                        key = c.Key, program = c.Program, entry = c.Entry, skill = c.Skill, settings = c.Settings,
                    }));
                    return Task.FromResult(new InstallRun(UserInstall.Done, null, json));
                }
                return Task.FromResult(new InstallRun(UserInstall.Done, null, "done"));
            }

            public void Set(string key, string entry, string skill, string settings = null)
            {
                Checks.RemoveAll(c => c.Key == key);
                Checks.Add(new UserInstall.AssistantCheck(key, @"C:\Programs\" + key + ".exe", entry, skill, settings));
            }
        }

        private sealed class FakeConsent : IAutomationConsent
        {
            public readonly List<AllowedProgramView> List = new();
            public readonly List<string> Said = new();
            public IReadOnlyList<AllowedProgramView> Allowed => List.ToList();
            public event EventHandler Changed;
            public void Remove(string key) { Said.Add("remove " + key); List.RemoveAll(p => p.Key == key); Changed?.Invoke(this, EventArgs.Empty); }
            public void AllowAssistant(string hostKey, string hostName, string program) => Said.Add("allow " + hostKey + " " + hostName + " " + program);
            public void ForgetAssistant(string hostKey, string program) => Said.Add("forget " + hostKey + " " + program);
        }

        private static (DashboardViewModel Vm, Installs Installs) Page(AutomationHost host = null)
        {
            var installs = new Installs();
            var vm = new DashboardViewModel
            {
                AssistantInstallSeam = installs.Run,
                AssistantIconSeam = _ => null,
                AssistantFindSeam = key => installs.Checks.FirstOrDefault(c => c.Key == key)?.Program,
            };
            if (host != null) vm.AttachAutomation(host, Dispatcher.CurrentDispatcher);
            return (vm, installs);
        }

        // ── The rows ──

        /// <summary>A row for each program found, in the hosts' order, none
        /// for one that is not on this PC, each with its state and button:
        /// Not added with Add, Added with Remove, older with Update and
        /// Remove in its menu, newer with Remove. A program that takes skills
        /// only as an upload says so, and offers the ZIP in its menu.</summary>
        [Fact]
        public async Task EachProgramFound_GetsARow_WithItsStateAndOneButton()
        {
            var (vm, installs) = Page();
            vm.EnableAutomation = true;
            installs.Set("codex", "current", "older");
            installs.Set("claude-code", "missing", "missing");
            installs.Set("claude-desktop", "current", "upload");
            installs.Set("vscode", "older", "current");
            installs.Set("zed", "current", "newer");
            installs.Checks.Add(new UserInstall.AssistantCheck("gemini-cli", null, "current", "current", null));
            await vm.RefreshAssistantRowsAsync();
            var s = Strings.Instance;

            Assert.Equal(AssistantHosts.All.Select(h => h.Key).Where(k => k is "claude-desktop" or "claude-code" or "codex" or "vscode" or "zed"),
                vm.AssistantRows.Select(r => r.Key));
            AssistantRowItem Row(string key) => vm.AssistantRows.Single(r => r.Key == key);
            Assert.Equal(s.Extras_Automation_State_NotAdded, Row("claude-code").Line);
            Assert.Equal(AssistantRowButton.Add, Row("claude-code").Button);
            Assert.Equal(s.Common_Add, Row("claude-code").ButtonWords);
            Assert.Equal(s.Extras_Automation_State_SkillOlder, Row("codex").Line);
            Assert.Equal(AssistantRowButton.Update, Row("codex").Button);
            Assert.True(Row("codex").ShowMenuRemove);
            Assert.Equal(s.Extras_Automation_State_Added + " " + s.Extras_Automation_State_UploadOnly, Row("claude-desktop").Line);
            Assert.Equal(AssistantRowButton.Remove, Row("claude-desktop").Button);
            Assert.False(Row("claude-desktop").ShowMenuRemove);
            Assert.True(Row("claude-desktop").TakesUploadOnly);
            Assert.False(Row("codex").TakesUploadOnly);
            Assert.Equal(s.Extras_Automation_State_EntryOlder, Row("vscode").Line);
            Assert.Equal(AssistantRowButton.Update, Row("vscode").Button);
            Assert.Equal(s.Extras_Automation_State_SkillNewer, Row("zed").Line);
            Assert.Equal(AssistantRowButton.Remove, Row("zed").Button);
            Assert.False(vm.ShowsNoAssistants);

            // While Automation is off, the first row says added assistants
            // wait for it.
            vm.EnableAutomation = false;
            Assert.Equal(s.Extras_Automation_State_Added + " " + s.Extras_Automation_State_UploadOnly + " " + s.Extras_Automation_State_OffNote,
                vm.AssistantRows[0].Line);
            Assert.DoesNotContain(s.Extras_Automation_State_OffNote, vm.AssistantRows[1].Line);
            vm.EnableAutomation = true;
            Assert.DoesNotContain(s.Extras_Automation_State_OffNote, vm.AssistantRows[0].Line);

            // One check for every row, as the user.
            var check = Assert.Single(installs.Runs);
            Assert.Equal(new InstallRequest(InstallWhat.Assistant, null, Check: true), check);

            // A program gone from the PC loses its row, and none found says so.
            installs.Checks.Clear();
            await vm.RefreshAssistantRowsAsync();
            Assert.Empty(vm.AssistantRows);
            Assert.True(vm.ShowsNoAssistants);
        }

        /// <summary>A program whose settings file the check could not read
        /// says so, never "Not added."</summary>
        [Fact]
        public async Task ASettingsFileTheCheckCouldNotRead_SaysSo_AndNotNotAdded()
        {
            var (vm, installs) = Page();
            vm.EnableAutomation = true;
            const string file = @"C:\Users\u\.gemini\settings.json";
            installs.Set("gemini-cli", "unreadable", "current", file);
            await vm.RefreshAssistantRowsAsync();
            var row = vm.AssistantRows.Single(r => r.Key == "gemini-cli");
            Assert.Equal(string.Format(Strings.Instance.Extras_Automation_State_SettingsUnreadable_Format, file), row.Line);
            Assert.NotEqual(Strings.Instance.Extras_Automation_State_NotAdded, row.Line);

            // A skill folder the check could not read is no missing skill.
            installs.Set("codex", "current", "unreadable");
            await vm.RefreshAssistantRowsAsync();
            var codex = vm.AssistantRows.Single(r => r.Key == "codex");
            Assert.Equal(Strings.Instance.Extras_Automation_State_SkillUnreadable, codex.Line);
            Assert.Equal(AssistantRowButton.Update, codex.Button);
        }

        /// <summary>An Add that fails on a settings file PadForge cannot read
        /// says why in the user's language, never in the run's English.
        /// The culture is this test's thread's own, which
        /// flows through the awaits.</summary>
        [Fact]
        public async Task AFailedAdd_SaysItsReasonInTheUsersLanguage()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de");
            try
            {
                var (vm, installs) = Page();
                installs.Set("codex", "missing", "missing");
                await vm.RefreshAssistantRowsAsync();
                const string file = @"C:\Users\u\.codex\config.json";
                const string english = file + " is not JSON PadForge can read, so PadForge leaves it as it is: '{' is invalid.";
                installs.Answer = r => r.Check ? null : new InstallRun(UserInstall.Failed, english, english) { Reason = InstallReason.SettingsUnreadable, Detail = file };
                var codex = vm.AssistantRows.Single(r => r.Key == "codex");
                await vm.PressAssistantAsync(codex, AssistantRowButton.Add);
                var s = Strings.Instance;
                Assert.Equal(string.Format(s.Extras_Automation_State_Failed_Format, string.Format(s.Extras_Automation_Reason_SettingsUnreadable_Format, file)),
                    codex.Line);
                Assert.DoesNotContain("is not JSON", codex.Line);
                Assert.StartsWith("Es wurde nicht", codex.Line);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = before;
            }
        }

        /// <summary>A check that fails says why, and keeps the rows it had.</summary>
        [Fact]
        public async Task ACheckThatFails_SaysWhy()
        {
            var (vm, installs) = Page();
            installs.Set("codex", "current", "current");
            await vm.RefreshAssistantRowsAsync();
            installs.Answer = _ => new InstallRun(UserInstall.Failed, "Windows did not start the install with the user's rights (NoShell).", null)
                { Reason = InstallReason.NotStarted };
            await vm.RefreshAssistantRowsAsync();
            Assert.True(vm.HasAssistantsCheckFailed);
            Assert.Equal(string.Format(Strings.Instance.Extras_Automation_CheckFailed_Format, Strings.Instance.Extras_Automation_Reason_NotStarted),
                vm.AssistantsCheckFailed);
            Assert.Single(vm.AssistantRows);
            Assert.False(vm.ShowsNoAssistants);
        }

        /// <summary>Add writes the entry, then the skill, as the user, and is
        /// the user's consent for that assistant. The row then says the
        /// assistant must be reopened until it connects, and Connected now
        /// while it is. Remove takes the skill out, then the entry, and the
        /// consent with them.</summary>
        [Fact]
        public async Task Add_WritesTheEntryAndTheSkill_IsConsent_AndTheRowFollowsTheConnection()
        {
            var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
            using var host = NewHost(() => now);
            var (vm, installs) = Page(host);
            var consent = new FakeConsent();
            vm.AttachConsent(consent);
            vm.EnableAutomation = true;
            installs.Set("claude-code", "missing", "missing");
            await vm.RefreshAssistantRowsAsync();
            var row = Assert.Single(vm.AssistantRows);
            installs.Runs.Clear();
            installs.Answer = r =>
            {
                if (!r.Check) installs.Set("claude-code", r.Remove ? "missing" : "current", r.Remove ? "missing" : "current");
                return null;
            };

            row.PressCommand.Execute(null);
            Assert.Equal(new[]
            {
                new InstallRequest(InstallWhat.Assistant, "claude-code"),
                new InstallRequest(InstallWhat.Skill, "claude-code"),
                new InstallRequest(InstallWhat.Assistant, null, Check: true),
            }, installs.Runs);
            Assert.Equal(new[] { @"allow claude-code Claude Code C:\Programs\claude-code.exe" }, consent.Said);
            Assert.Equal(Strings.Instance.Extras_Automation_State_NotConnectedSince, row.Line);
            Assert.Equal(AssistantRowButton.Remove, row.Button);

            // It connects: Connected now. It leaves: Added, since it has
            // connected since.
            now = now.AddMinutes(1);
            var client = host.Clients.Add("claude-code (claude.exe)", AutomationClientKind.Assistant, 77, "PadForge.exe");
            Assert.Equal(Strings.Instance.Extras_Automation_State_ConnectedNow, row.Line);
            host.Clients.Remove(client.Id);
            Assert.Equal(Strings.Instance.Extras_Automation_State_Added, row.Line);
            // A script that gives the assistant's name as its own is no
            // assistant.
            host.Clients.Add("claude-code (claude.exe)", AutomationClientKind.Script, 78, "python.exe");
            Assert.Equal(Strings.Instance.Extras_Automation_State_Added, row.Line);

            installs.Runs.Clear();
            row.PressCommand.Execute(null);
            Assert.Equal(new[]
            {
                new InstallRequest(InstallWhat.Skill, "claude-code", Remove: true),
                new InstallRequest(InstallWhat.Assistant, "claude-code", Remove: true),
                new InstallRequest(InstallWhat.Assistant, null, Check: true),
            }, installs.Runs);
            Assert.Equal(@"forget claude-code C:\Programs\claude-code.exe", consent.Said.Last());
            Assert.Equal(Strings.Instance.Extras_Automation_State_NotAdded, row.Line);
        }

        /// <summary>A program that takes skills only as an upload gets its
        /// entry alone. A step that fails stops the rest, gives no consent
        /// and puts its reason on the row.</summary>
        [Fact]
        public async Task AFailedStep_StopsTheRest_AndTheRowSaysWhy()
        {
            var (vm, installs) = Page();
            vm.EnableAutomation = true;
            var consent = new FakeConsent();
            vm.AttachConsent(consent);
            installs.Set("claude-desktop", "missing", "upload");
            installs.Set("codex", "missing", "missing");
            await vm.RefreshAssistantRowsAsync();
            installs.Runs.Clear();
            await vm.PressAssistantAsync(vm.AssistantRows.Single(r => r.Key == "claude-desktop"), AssistantRowButton.Add);
            Assert.Equal(new[] { new InstallRequest(InstallWhat.Assistant, "claude-desktop"), new InstallRequest(InstallWhat.Assistant, null, Check: true) },
                installs.Runs);
            // A program whose client name PadForge has not read is never
            // said to have not connected since: PadForge cannot see it.
            installs.Set("claude-desktop", "current", "upload");
            await vm.RefreshAssistantRowsAsync();
            Assert.Equal(Strings.Instance.Extras_Automation_State_Added + " " + Strings.Instance.Extras_Automation_State_UploadOnly,
                vm.AssistantRows.Single(r => r.Key == "claude-desktop").Line);

            installs.Runs.Clear();
            installs.Answer = r => r.Check ? null : new InstallRun(UserInstall.Failed, "Codex's own command did not add PadForge (exit code 2): bad", null)
                { Reason = InstallReason.HostCommandFailed, Number = 2, Detail = "codex" };
            var codex = vm.AssistantRows.Single(r => r.Key == "codex");
            await vm.PressAssistantAsync(codex, AssistantRowButton.Add);
            Assert.Equal(new[] { new InstallRequest(InstallWhat.Assistant, "codex"), new InstallRequest(InstallWhat.Assistant, null, Check: true) },
                installs.Runs);
            Assert.Equal(string.Format(Strings.Instance.Extras_Automation_State_Failed_Format,
                    string.Format(Strings.Instance.Extras_Automation_Reason_HostCommandFailed_Format, AssistantHosts.Find("codex").Name, 2)),
                codex.Line);
            Assert.DoesNotContain(consent.Said, s => s.Contains("codex"));
            Assert.True(codex.IsIdle);
        }

        /// <summary>Save the Skill as a ZIP… writes the skill's folder as the
        /// ZIP's top level, where the user picks, and the row says where to
        /// add it.</summary>
        [Fact]
        public async Task SaveTheSkillAsAZip_PutsTheFolderAtItsTopLevel()
        {
            var (vm, installs) = Page();
            installs.Set("claude-desktop", "current", "upload");
            await vm.RefreshAssistantRowsAsync();
            string file = Path.Combine(_root, "padforge-skill.zip");
            vm.AssistantZipDialogSeam = _ => file;
            var row = vm.AssistantRows.Single();
            // As a copy without rights the shell lacks writes it, whatever
            // rights the test host has.
            PadForge.Services.UserSave.HasRightsSeam = () => false;
            try { await vm.SaveAssistantZipAsync(row); }
            finally { PadForge.Services.UserSave.HasRightsSeam = null; }
            using (var zip = ZipFile.OpenRead(file))
            {
                Assert.All(zip.Entries, e => Assert.StartsWith(AssistantSkill.Name + "/", e.FullName));
                Assert.Contains(zip.Entries, e => e.FullName == AssistantSkill.Name + "/SKILL.md");
                Assert.Equal(AssistantSkill.Files.Count, zip.Entries.Count);
            }
            Assert.Equal(string.Format(Strings.Instance.Extras_Automation_ZipSaved_Format, "Claude Desktop", "padforge-skill.zip", "Customize, Skills"), row.Line);
            // Canceled: nothing is written.
            File.Delete(file);
            vm.AssistantZipDialogSeam = _ => null;
            row.SaveZipCommand.Execute(null);
            Assert.False(File.Exists(file));
        }

        /// <summary>Open Its Settings File opens a .json or .toml file that is
        /// there, its folder while it is not, and never opens anything else
        /// as a file.</summary>
        [Fact]
        public async Task OpenItsSettingsFile_OpensTheFileOrItsFolder_AndNothingElseAsAFile()
        {
            var (vm, installs) = Page();
            string json = Path.Combine(_root, "mcp-config.json");
            File.WriteAllText(json, "{}");
            string exe = Path.Combine(_root, "evil.exe");
            File.WriteAllText(exe, "x");
            installs.Set("vscode", "current", "current", json);
            installs.Set("codex", "current", "current", Path.Combine(_root, "absent", "config.toml"));
            installs.Set("zed", "current", "current", exe);
            installs.Set("junie", "current", "current", Path.Combine(_root, "missing.json"));
            await vm.RefreshAssistantRowsAsync();
            var opened = new List<(string, bool)>();
            vm.AssistantOpenSeam = (target, asFile) => opened.Add((target, asFile));
            AssistantRowItem Row(string key) => vm.AssistantRows.Single(r => r.Key == key);
            Row("vscode").OpenSettingsCommand.Execute(null);
            Row("zed").OpenSettingsCommand.Execute(null);
            Row("junie").OpenSettingsCommand.Execute(null);
            Assert.Equal(new[] { (json, true), (_root, false), (_root, false) }, opened);
            // A file in a folder that is not there either: the row says so.
            Row("codex").OpenSettingsCommand.Execute(null);
            Assert.Equal(3, opened.Count);
            Assert.Equal(string.Format(Strings.Instance.Extras_Automation_NoSettingsFile_Format, Row("codex").Name), Row("codex").Line);
        }

        /// <summary>A path from the run without rights is used only when it is
        /// a full path on a drive of this PC: the app never reaches a share
        /// or a device path for an icon or a settings file.</summary>
        [Fact]
        public void APathFromTheRun_IsUsedOnlyOnALocalDrive()
        {
            Assert.True(ProgramIcon.IsLocalPath(@"C:\Programs\codex.exe"));
            Assert.True(ProgramIcon.IsLocalPath("D:/x/y.json"));
            Assert.False(ProgramIcon.IsLocalPath(@"\\server\share\codex.exe"));
            Assert.False(ProgramIcon.IsLocalPath(@"\\?\C:\codex.exe"));
            Assert.False(ProgramIcon.IsLocalPath(@"\\.\pipe\x"));
            Assert.False(ProgramIcon.IsLocalPath("codex.exe"));
            Assert.False(ProgramIcon.IsLocalPath(null));
            Assert.Null(ProgramIcon.Read(@"\\server\share\codex.exe"));
        }

        /// <summary>Each row joins the catalog through the one seam for live
        /// rows, its button and its menu the user's alone, Save the Skill as
        /// a ZIP… naming the Save dialog it opens.</summary>
        [Fact]
        public async Task TheRows_JoinTheCatalog_AsTheUsersAlone()
        {
            var (vm, installs) = Page();
            installs.Set("claude-desktop", "current", "upload");
            installs.Set("codex", "current", "older");
            await vm.RefreshAssistantRowsAsync();
            var items = vm.AssistantCatalogItems();
            Assert.Equal(new[] { "assistant/claude-desktop", "assistant/codex" }, items.Select(i => i.Id));
            Assert.All(items, i => Assert.Equal(ReachClass.UsersOwn, i.Reach));
            Assert.All(items.SelectMany(i => i.Menu), m => Assert.Equal(ReachClass.UsersOwn, m.Reach));
            Assert.Equal(new[] { "assistant/claude-desktop/zip", "assistant/claude-desktop/settings" }, items[0].Menu.Select(m => m.Id));
            Assert.Equal("SaveFileDialog", items[0].Menu[0].Opens);
            Assert.Equal(new[] { "assistant/codex/remove", "assistant/codex/settings" }, items[1].Menu.Select(m => m.Id));
            Assert.Equal(vm.AssistantRows[1].Line, items[1].Read());
            Assert.All(items.SelectMany(i => i.Menu), m => Assert.Equal(LiveKind.MenuItem, m.Kind));

            // Through the seam, as the window registers it.
            LiveCatalog.AddProvider(vm.AssistantCatalogItems);
            try { Assert.Contains(LiveCatalog.Items(), i => i.Id == "assistant/codex/remove"); }
            finally { LiveCatalog.RemoveProvider(vm.AssistantCatalogItems); }
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.Automation.cs");
            Assert.Contains("LiveCatalog.AddProvider(_viewModel.Dashboard.AssistantCatalogItems);", window);
        }

        // ── Allowed Programs ──

        [Fact]
        public void AllowedPrograms_ListWhatTheStoreAllowed_WithRemove()
        {
            var (vm, _) = Page();
            Assert.False(vm.HasAllowedPrograms);
            var consent = new FakeConsent();
            var used = new DateTimeOffset(2026, 11, 23, 14, 0, 0, TimeSpan.Zero);
            consent.List.Add(new AllowedProgramView("a", "Steer Assist", @"AutoHotkey64.exe running C:\Scripts\steer.ahk", AutomationLevel.SendInputs, used, true));
            consent.List.Add(new AllowedProgramView("b", "Pedal Curve", @"python.exe running C:\Scripts\pedals.py", AutomationLevel.Read, used, false));
            consent.List.Add(new AllowedProgramView("c", "New", "new.exe", AutomationLevel.ChangeSettings, null, false));
            vm.AttachConsent(consent);
            var s = Strings.Instance;
            Assert.True(vm.HasAllowedPrograms);
            Assert.Equal(new[] { "Steer Assist", "Pedal Curve", "New" }, vm.AllowedPrograms.Select(p => p.Name));
            Assert.Equal(string.Format(s.Automation_Allowed_Line_Format, @"AutoHotkey64.exe running C:\Scripts\steer.ahk", s.Extras_Automation_Level_SendInputs,
                s.Automation_Allowed_ConnectedNow), vm.AllowedPrograms[0].Line);
            Assert.EndsWith(string.Format(s.Automation_Allowed_LastUsed_Format,
                used.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture)), vm.AllowedPrograms[1].Line);
            Assert.EndsWith(s.Automation_Allowed_NeverUsed, vm.AllowedPrograms[2].Line);
            Assert.StartsWith("Automation_AllowedRemove_", vm.AllowedPrograms[0].RemoveId);
            vm.AllowedPrograms[1].RemoveCommand.Execute(null);
            Assert.Equal(new[] { "remove b" }, consent.Said);
            Assert.Equal(new[] { "Steer Assist", "New" }, vm.AllowedPrograms.Select(p => p.Name));
        }

        // ── The status line and the connected mark ──

        private static AutomationClient Client(int id, string name, AutomationClientKind kind = AutomationClientKind.Script)
            => new AutomationClient(id, name, kind, 100 + id, name + ".exe", DateTimeOffset.Now);

        /// <summary>The page's line: off says no program can reach PadForge,
        /// on and idle says so, and on with programs names them as a
        /// sentence does, an assistant by its program's own name, plugins
        /// left to their own page.</summary>
        [Fact]
        public void TheStatusLine_NamesTheProgramsAsASentenceDoes()
        {
            var s = Strings.Instance;
            var serving = ExternalControlService.PipeState.Serving;
            Assert.Equal(s.Automation_Status_Off, MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Stopped, serving, Array.Empty<AutomationClient>()));
            Assert.Equal(s.Automation_Status_NoPrograms, MainWindow.AutomationStatusLine(serving, serving, Array.Empty<AutomationClient>()));
            Assert.Equal(s.Automation_Status_NoPrograms, MainWindow.AutomationStatusLine(serving, serving, new[] { Client(1, "Wheel Bridge", AutomationClientKind.Plugin) }));
            var clients = new[]
            {
                Client(1, "claude-code (claude.exe)", AutomationClientKind.Assistant),
                Client(2, "Steer Assist"),
                Client(3, "Steer Assist"),
                Client(4, "Wheel Bridge", AutomationClientKind.Plugin),
            };
            Assert.Equal(string.Format(s.Automation_Status_Connected_Format, string.Format(s.Automation_Names_Two_Format, "Claude Code", "Steer Assist")),
                MainWindow.AutomationStatusLine(serving, serving, clients));
            Assert.Equal("A", MainWindow.JoinNames(new[] { "A" }));
            Assert.Equal(string.Format(s.Automation_Names_List_Format, "A", string.Format(s.Automation_Names_Two_Format, "B", "C")),
                MainWindow.JoinNames(new[] { "A", "B", "C" }));
            Assert.Equal(string.Empty, MainWindow.JoinNames(Array.Empty<string>()));
        }

        /// <summary>The mark shows the count while a program is connected, on
        /// every page but Home and Automation's, a globe while one came from
        /// the web, and names the programs for hover and a screen
        /// reader.</summary>
        [Fact]
        public void TheMark_ShowsTheCountOffHomeAndAutomation_AndTheGlobeForTheWeb()
        {
            var s = Strings.Instance;
            var none = MainWindow.ConnectedMarkState(Array.Empty<AutomationClient>(), _ => false, "Devices", null);
            Assert.False(none.Shows);
            Assert.False(MainWindow.ConnectedMarkState(new[] { Client(1, "Wheel Bridge", AutomationClientKind.Plugin) }, _ => false, "Devices", null).Shows);

            var clients = new[] { Client(1, "claude-code (claude.exe)", AutomationClientKind.Assistant), Client(2, "Steer Assist") };
            var mark = MainWindow.ConnectedMarkState(clients, _ => false, "Devices", null);
            Assert.True(mark.Shows);
            Assert.Equal(2, mark.Count);
            Assert.False(mark.Web);
            Assert.Equal(string.Format(s.Main_ConnectedMark_Format, string.Format(s.Automation_Names_Two_Format, "Claude Code", "Steer Assist")), mark.Words);
            Assert.False(MainWindow.ConnectedMarkState(clients, _ => false, "Home", null).Shows);
            Assert.False(MainWindow.ConnectedMarkState(clients, _ => false, "Extras", "Automation").Shows);
            Assert.True(MainWindow.ConnectedMarkState(clients, _ => false, "Extras", "Plugins").Shows);
            Assert.True(MainWindow.ConnectedMarkState(clients, _ => false, "Extras", null).Shows);
            Assert.True(MainWindow.ConnectedMarkState(clients, _ => false, "Pad1", null).Shows);

            var web = MainWindow.ConnectedMarkState(clients, c => c.Id == 2, "Settings", null);
            Assert.True(web.Web);
            Assert.Equal(2, web.Count);
            Assert.Equal(string.Format(s.Main_ConnectedMark_Web_Format, "Claude Code", "Steer Assist"), web.Words);
            // Two copies of one script count two, and are named once.
            var copies = MainWindow.ConnectedMarkState(new[] { Client(1, "Steer Assist"), Client(2, "Steer Assist") }, _ => false, "Devices", null);
            Assert.Equal(2, copies.Count);
            Assert.Equal(string.Format(s.Main_ConnectedMark_Format, "Steer Assist"), copies.Words);
            var onlyWeb = MainWindow.ConnectedMarkState(new[] { Client(2, "claude.ai") }, _ => true, "Settings", null);
            Assert.Equal(string.Format(s.Main_ConnectedMark_WebOnly_Format, "claude.ai"), onlyWeb.Words);
        }

        /// <summary>The mark sits between the profile switcher and the window
        /// buttons, the user's alone, its words set in code with the hover
        /// and the screen reader saying one thing, and a click opens
        /// Automation.</summary>
        [Fact]
        public void TheMark_SitsBetweenTheProfileSwitcherAndTheWindowButtons()
        {
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml");
            int pill = window.IndexOf("x:Name=\"TitleProfilePill\"", StringComparison.Ordinal);
            int mark = window.IndexOf("x:Name=\"ConnectedMark\"", StringComparison.Ordinal);
            int full = window.IndexOf("x:Name=\"FullScreenBtn\"", StringComparison.Ordinal);
            Assert.True(pill > 0 && mark > pill && full > mark);
            string markup = window.Substring(mark, full - mark);
            Assert.Contains("common:Reach.Class=\"UsersOwn\"", markup);
            Assert.Contains("WindowChrome.IsHitTestVisibleInChrome=\"True\"", markup);
            Assert.Contains("Visibility=\"Collapsed\"", markup);
            Assert.DoesNotContain("{Binding", markup);
            string code = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.Automation.cs");
            Assert.Contains("ConnectedMark.ToolTip = state.Words;", code);
            Assert.Contains("AutomationProperties.SetName(ConnectedMark, state.Words);", code);
            Assert.Contains("private void ConnectedMark_Click(object sender, RoutedEventArgs e) => OpenExtra(\"Automation\");", code);
        }
    }
}
