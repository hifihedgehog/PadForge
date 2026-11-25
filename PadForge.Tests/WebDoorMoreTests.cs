using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The web door's edges (#600 item 11): it waits for Automation, every
    /// allowed web assistant gets its session through the one helper, a
    /// client that signs in again takes its own place, no card asks for one
    /// that could not be kept, the switch's reset turns it off without a
    /// question, its controls join the catalog as the user's alone, and the
    /// helper's code names no part of WPF.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebDoorMoreTests
    {
        private static WebSignIn SignIn(string clientId, string code = "123456")
            => new("sid", "claude.ai", "Claude", code, "quiet-lake.ngrok-free.app", false, clientId,
                "https://claude.ai/api/mcp/auth_callback", "https://quiet-lake.ngrok-free.app/mcp");

        private static async Task<string> AllowAsync(AssistantRig rig, WebDoor door, WebSignIn signIn)
        {
            door.Arm();
            var asking = door.AskAsync(signIn);
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
            rig.Host.Approvals.Answer(rig.Host.Approvals.Pending.Single().Id, true);
            return await asking;
        }

        [Fact]
        public void WithAutomationOff_TheHelperWaits_AndStartsOnceAutomationIsOn()
        {
            using var rig = new AssistantRig();
            rig.Host.Apply(false, AutomationLevel.ChangeSettings);
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            door.TurnOn();
            Assert.Empty(launcher.Started);
            Assert.Equal(WebHelperState.WaitsForAutomation, door.HelperState);
            var view = new WebAccessViewModel(door, store);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Web_Helper_AutomationOff, view.TunnelLine);

            rig.Host.Apply(true, AutomationLevel.ChangeSettings);
            Assert.Single(launcher.Started);
            Assert.Equal(WebHelperState.Starting, door.HelperState);
            rig.Host.Apply(false, AutomationLevel.ChangeSettings);
            Assert.True(launcher.Last.Stopped);
            Assert.Equal(WebHelperState.WaitsForAutomation, door.HelperState);
            rig.Host.Apply(true, AutomationLevel.ChangeSettings);
            Assert.Equal(2, launcher.Started.Count);
        }

        [Fact]
        public async Task EveryAllowedWebAssistant_GetsItsSession_ThroughTheOneHelper()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            var launcher = new WebTestLauncher();
            using var door = new WebDoor(rig.Host, store, launcher, () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            door.TurnOn();
            string key = launcher.Started.Single().Variables[WebHelper.KeyVariable];
            for (int i = 0; i < 6; i++)
                Assert.True(store.Add(new WebGrant { Id = "g" + i, Origin = "host" + i + ".example", Allowed = DateTimeOffset.UtcNow }));
            var pipes = new List<NamedPipeClientStream>();
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    var pipe = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(5000);
                    pipes.Add(pipe);
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes("hello kind=web token=" + key + " grant=g" + i + "\n"));
                    await pipe.FlushAsync();
                    string answer = await WebLink.ReadLineAsync(pipe, default);
                    Assert.StartsWith("ok padforge=", answer);
                }
                await AssistantRig.Until(() => rig.Host.Clients.Snapshot().Count(c => c.Kind == AutomationClientKind.Web) == 6);
                // A grant that is not one is refused.
                var stranger = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await stranger.ConnectAsync(5000);
                pipes.Add(stranger);
                await stranger.WriteAsync(Encoding.UTF8.GetBytes("hello kind=web token=" + key + " grant=nobody\n"));
                await stranger.FlushAsync();
                Assert.StartsWith("err denied", await WebLink.ReadLineAsync(stranger, default));
            }
            finally
            {
                foreach (var pipe in pipes) pipe.Dispose();
            }
        }

        [Fact]
        public async Task AClientThatSignsInAgain_TakesItsOwnPlace_AndPastTheMostKept_NoCardAsks()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            string first = await AllowAsync(rig, door, SignIn("https://claude.ai/oauth/doc"));
            string again = await AllowAsync(rig, door, SignIn("https://claude.ai/oauth/doc", "654321"));
            Assert.NotEqual(first, again);
            Assert.Equal(again, store.Grants.Single().Id);

            for (int i = store.Grants.Count; i < WebAccessStore.MostAssistants; i++)
                store.Add(new WebGrant { Id = "x" + i, Origin = "x" + i + ".example", ClientId = "c" + i, Allowed = DateTimeOffset.UtcNow });
            door.Arm();
            Assert.Null(await door.AskAsync(SignIn("https://other.example/doc")));
            Assert.Empty(rig.Host.Approvals.Pending);
        }

        [Fact]
        public void TheReset_TurnsTheSwitchOffWithoutAQuestion_AndTheControlsJoinTheCatalogAsTheUsersAlone()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            int asked = 0;
            var view = new WebAccessViewModel(door, store) { AskToTurnOn = () => { asked++; return true; } };
            Assert.Equal(new[] { "web/reach" }, view.CatalogItems().Select(i => i.Id));
            view.IsOn = true;
            store.Add(new WebGrant { Id = "g1", Origin = "claude.ai", Allowed = DateTimeOffset.UtcNow });
            view.Refresh();
            var items = view.CatalogItems();
            Assert.Equal(new[] { "web/reach", "web/tunnel", "web/address", "web/accept", "web/copy-command", "web/allowed/g1" }, items.Select(i => i.Id));
            Assert.All(items, i => Assert.Equal(PadForge.Common.ReachClass.UsersOwn, i.Reach));
            Assert.All(items, i => Assert.Null(i.Write));
            Assert.All(items, i => Assert.Null(i.Press));
            Assert.True(WebAccessViewModel.CanResetSetting(nameof(WebAccessViewModel.IsOn)));
            Assert.False(WebAccessViewModel.CanResetSetting("Address"));
            view.ResetSettingCommand.Execute(nameof(WebAccessViewModel.IsOn));
            Assert.False(store.On);
            Assert.Equal(1, asked);
        }

        /// <summary>The helper runs as a client of the command line, which
        /// loads no WPF: its files and the copy it stands behind each web
        /// assistant name no part of it.</summary>
        [Fact]
        public void TheHelpersCode_NamesNoPartOfWpf()
        {
            string root = AppContext.BaseDirectory;
            while (!Directory.Exists(Path.Combine(root, "PadForge.App"))) root = Path.GetDirectoryName(root);
            var files = Directory.GetFiles(Path.Combine(root, "PadForge.App", "Services", "Automation", "Web"), "*.cs").ToList();
            files.Add(Path.Combine(root, "PadForge.App", "Services", "Automation", "Assistant", "AssistantMirror.cs"));
            Assert.True(files.Count >= 11);
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                Assert.DoesNotMatch(new Regex(@"\bSystem\.Windows\b(?!\.Forms)|\bPadForge\.Views\b|\bPadForge\.ViewModels\b|\bDispatcher\b"), text);
            }
        }
    }
}
