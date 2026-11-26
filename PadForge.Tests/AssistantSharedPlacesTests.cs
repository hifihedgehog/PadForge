using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Install;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// Assistant programs that read one place: Remove on one row leaves a
    /// skill folder or a settings file that another found program still has
    /// PadForge added in, and the row says which program keeps it. A seam
    /// answers every install, so no program starts.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class AssistantSharedPlacesTests
    {
        private sealed class Installs
        {
            public readonly List<InstallRequest> Runs = new();
            public readonly Dictionary<string, (string Entry, string Skill)> State = new();

            public Task<InstallRun> Run(InstallRequest request)
            {
                if (request.Check)
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(State.Select(p => new
                    {
                        key = p.Key, program = @"C:\Programs\" + p.Key + ".exe", entry = p.Value.Entry, skill = p.Value.Skill, settings = (string)null,
                    }));
                    return Task.FromResult(new InstallRun(UserInstall.Done, null, json));
                }
                Runs.Add(request);
                var host = AssistantHosts.Find(request.Target);
                if (request.Remove)
                {
                    // The file or folder goes for every program that reads it.
                    foreach (var other in AssistantHosts.All.Where(h => State.ContainsKey(h.Key)))
                    {
                        var (entry, skill) = State[other.Key];
                        if (request.What == InstallWhat.Assistant && (other == host || AssistantHosts.SharesEntry(other, host))) entry = "missing";
                        if (request.What == InstallWhat.Skill && (other == host || AssistantHosts.SharesSkills(other, host))) skill = "missing";
                        State[other.Key] = (entry, skill);
                    }
                }
                return Task.FromResult(new InstallRun(UserInstall.Done, null, "done"));
            }
        }

        private sealed class FakeConsent : IAutomationConsent
        {
            public readonly List<string> Said = new();
            public IReadOnlyList<AllowedProgramView> Allowed => Array.Empty<AllowedProgramView>();
            public event EventHandler Changed { add { } remove { } }
            public void Remove(string key) => Said.Add("remove " + key);
            public void AllowAssistant(string hostKey, string hostName, string program) => Said.Add("allow " + hostKey);
            public void ForgetAssistant(string hostKey, string program) => Said.Add("forget " + hostKey);
        }

        /// <summary>Remove on Visual Studio takes its entry out and leaves the
        /// skill folder GitHub Copilot CLI reads. Remove on VS Code leaves the
        /// settings file and the folder both, and a Remove on GitHub Copilot
        /// CLI after it takes both out, since the user removed PadForge from
        /// VS Code already.</summary>
        [Fact]
        public async Task Remove_LeavesWhatAnotherAddedProgramReads_AndSaysWhichKeepsIt()
        {
            var installs = new Installs();
            var vm = new DashboardViewModel { AssistantInstallSeam = installs.Run, AssistantIconSeam = _ => null };
            var consent = new FakeConsent();
            vm.AttachConsent(consent);
            installs.State["copilot-cli"] = ("current", "current");
            installs.State["vscode"] = ("current", "current");
            installs.State["visual-studio"] = ("current", "current");
            await vm.RefreshAssistantRowsAsync();
            AssistantRowItem Row(string key) => vm.AssistantRows.Single(r => r.Key == key);
            var s = Strings.Instance;

            await vm.PressAssistantAsync(Row("visual-studio"), AssistantRowButton.Remove);
            Assert.Equal(new[] { new InstallRequest(InstallWhat.Assistant, "visual-studio", Remove: true) }, installs.Runs);
            Assert.Equal("current", installs.State["copilot-cli"].Skill);
            Assert.Equal(string.Format(s.Extras_Automation_State_KeptSkill_Format, "GitHub Copilot CLI"), Row("visual-studio").Line);
            Assert.Equal("forget visual-studio", consent.Said.Last());

            installs.Runs.Clear();
            await vm.PressAssistantAsync(Row("vscode"), AssistantRowButton.Remove);
            Assert.Empty(installs.Runs);
            Assert.Equal(("current", "current"), installs.State["copilot-cli"]);
            Assert.Equal(string.Format(s.Extras_Automation_State_KeptBoth_Format, "GitHub Copilot CLI"), Row("vscode").Line);
            Assert.Equal("forget vscode", consent.Said.Last());

            await vm.PressAssistantAsync(Row("copilot-cli"), AssistantRowButton.Remove);
            Assert.Equal(new[]
            {
                new InstallRequest(InstallWhat.Skill, "copilot-cli", Remove: true),
                new InstallRequest(InstallWhat.Assistant, "copilot-cli", Remove: true),
            }, installs.Runs);
            Assert.Equal(("missing", "missing"), installs.State["copilot-cli"]);
            Assert.Equal(("missing", "missing"), installs.State["vscode"]);
        }
    }
}
