using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Reach PadForge From the Web and the Assistants on the Web card (#600
    /// item 11), through their view model: the switch asks before it turns
    /// on, the tunnels found are listed with what each does with its
    /// address, the address has Copy and Add to Claude, Accept a New Web
    /// Assistant arms for five minutes, Revoke takes an assistant out, and
    /// the stdio command for an assistant's own tunnel is copied whole.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebPageTests
    {
        private sealed class Page : IDisposable
        {
            public readonly AssistantRig Rig = new();
            public readonly WebAccessStore Store = new();
            public readonly WebTestLauncher Launcher = new() { Id = 5150 };
            public readonly WebDoor Door;
            public readonly WebAccessViewModel View;
            public readonly List<string> Copied = new(), Opened = new();
            public bool Yes;
            public int Asked;
            public DateTimeOffset Now = new(2026, 11, 24, 12, 44, 0, TimeSpan.Zero);

            public Page()
            {
                Door = new WebDoor(Rig.Host, Store, Launcher, () => @"C:\PadForge\PadForge.exe", Rig.PipeName, () => Now);
                View = new WebAccessViewModel(Door, Store, () => Now)
                {
                    AskToTurnOn = () => { Asked++; return Yes; },
                    ClipboardSeam = Copied.Add,
                    OpenLinkSeam = link => { Opened.Add(link); return true; },
                };
            }

            public void Dispose()
            {
                Door.Dispose();
                Rig.Dispose();
            }
        }

        [Fact]
        public void TheSwitch_IsOffUntilTurnedOn_AndTurningItOnAsks_ANoLeavesItOffAndStartsNothing()
        {
            using var page = new Page();
            Assert.False(page.View.IsOn);
            page.View.IsOn = true;
            Assert.Equal(1, page.Asked);
            Assert.False(page.View.IsOn);
            Assert.Empty(page.Launcher.Started);
            page.Yes = true;
            page.View.IsOn = true;
            Assert.True(page.View.IsOn);
            Assert.True(page.Store.On);
            Assert.Single(page.Launcher.Started);
            // Off asks nothing and ends the helper.
            page.View.IsOn = false;
            Assert.Equal(2, page.Asked);
            Assert.False(page.Store.On);
            Assert.True(page.Launcher.Last.Stopped);
        }

        [Fact]
        public void TheTunnelList_NamesWhatIsOnThisPC_AndTheLineSaysWhichKeepTheirAddress()
        {
            using var page = new Page();
            var s = Strings.Instance;
            page.Yes = true;
            page.View.IsOn = true;
            // Before the helper answers: it is starting.
            Assert.Equal(s.Web_Helper_Starting, page.View.TunnelLine);
            // The ChatGPT Secure MCP Tunnel is always listed: PadForge fetches it.
            Assert.Equal(new[] { s.Web_Tunnel_None, "ChatGPT Secure MCP Tunnel", s.Web_Tunnel_Paste }, page.View.Tunnels.Select(t => t.Name));

            // The helper runs and reports two tunnel programs.
            SetRunning(page);
            Found(page, ("ngrok", @"C:\Tools\ngrok.exe"), ("cloudflared", @"C:\Program Files (x86)\cloudflared\cloudflared.exe"));
            page.View.Refresh();
            Assert.Equal(new[] { s.Web_Tunnel_None, "ngrok", "cloudflared", "ChatGPT Secure MCP Tunnel", s.Web_Tunnel_Paste }, page.View.Tunnels.Select(t => t.Name));
            Assert.StartsWith(s.Web_Tunnel_Choose, page.View.TunnelLine);

            page.View.SelectedTunnel = page.View.Tunnels.Single(t => t.Key == "ngrok");
            Assert.Equal("ngrok", page.Store.Tunnel);
            Assert.Equal(s.Web_Tunnel_Starting + " " + string.Format(s.Web_Tunnel_AlsoFound_Format, string.Format(s.Web_Tunnel_Changes_Format, "cloudflared")),
                page.View.TunnelLine);
            Assert.Equal(@"C:\Tools\ngrok.exe", page.Door.State()["tunnelPath"].GetValue<string>());
            Report(page, "ngrok", "running", "https://quiet-lake-4b2c.ngrok-free.app");
            page.View.Refresh();
            Assert.StartsWith(s.Web_Tunnel_Running_Keeps, page.View.TunnelLine);
            Assert.Equal("https://quiet-lake-4b2c.ngrok-free.app/mcp", page.View.Address);

            // Copy, and Add to Claude with the address filled in.
            page.View.CopyAddressCommand.Execute(null);
            Assert.Equal("https://quiet-lake-4b2c.ngrok-free.app/mcp", page.Copied.Single());
            Assert.Equal(s.Web_Address_Copied, page.View.AddressNote);
            page.View.AddToClaudeCommand.Execute(null);
            Assert.Equal("https://claude.ai/customize/connectors?modal=add-custom-connector&connectorName=PadForge&connectorUrl="
                         + "https%3A%2F%2Fquiet-lake-4b2c.ngrok-free.app%2Fmcp", page.Opened.Single());

            // A quick tunnel says its address changes each run.
            page.View.SelectedTunnel = page.View.Tunnels.Single(t => t.Key == "cloudflared");
            Report(page, "cloudflared", "running", "https://seasonal-deck.trycloudflare.com");
            page.View.Refresh();
            Assert.StartsWith(s.Web_Tunnel_Running_Changes, page.View.TunnelLine);
            Assert.Contains(string.Format(s.Web_Tunnel_Keeps_Format, "ngrok"), page.View.TunnelLine);

            // A pasted address: the port to point the user's own tunnel at.
            page.View.SelectedTunnel = page.View.Tunnels.Single(t => t.Key == "paste");
            page.View.PastedAddress = "http://not-https.example";
            Assert.True(page.View.HasPasteNote);
            Assert.Null(page.View.Address);
            page.View.PastedAddress = "https://box.tail1.ts.net";
            Assert.False(page.View.HasPasteNote);
            page.View.Refresh();
            Assert.Equal("https://box.tail1.ts.net/mcp", page.View.Address);
            Assert.Equal(string.Format(s.Web_Tunnel_Paste_Hint_Format, page.Store.Port), page.View.TunnelLine);
        }

        [Fact]
        public void Accept_ArmsForFiveMinutes_StopAccepting_Disarms_AndAllowedLists_WithRevoke()
        {
            using var page = new Page();
            var s = Strings.Instance;
            Assert.Equal(s.Web_Accept_Off, page.View.AcceptLine);
            Assert.Equal(s.Web_Accept_Start, page.View.AcceptWords);
            page.View.ArmCommand.Execute(null);
            Assert.True(page.Door.Armed);
            page.View.Refresh();
            Assert.Equal(s.Web_Accept_Stop, page.View.AcceptWords);
            Assert.Equal(string.Format(s.Web_Accept_Until_Format, page.Door.ArmedUntil.ToLocalTime().ToString("t")), page.View.AcceptLine);
            page.View.ArmCommand.Execute(null);
            Assert.False(page.Door.Armed);

            Assert.False(page.View.HasAllowed);
            page.Store.Add(new WebGrant { Id = "g1", Origin = "claude.ai", Name = "Claude", Allowed = page.Now.AddMinutes(-3), LastUsed = page.Now });
            page.View.Refresh();
            var row = page.View.Allowed.Single();
            Assert.Equal("claude.ai", row.Origin);
            Assert.StartsWith(string.Format(s.Web_Allowed_ReturnsTo_Format, "claude.ai") + " " + string.Format(s.Web_Allowed_CallsItself_Format, "Claude"), row.Line);
            Assert.Equal("Web_Revoke_g1", row.RevokeId);
            row.RevokeCommand.Execute(null);
            page.View.Refresh();
            Assert.Empty(page.Store.Grants);
            Assert.False(page.View.HasAllowed);
        }

        // ── The helper's reports, as its pipe carries them ──

        private static void SetRunning(Page page)
        {
            // What the helper's first lines do on its pipe.
            page.Door.Take(new JsonObject { ["t"] = "port", ["port"] = 51234 });
            page.Door.HelperState = WebHelperState.Running;
        }

        private static void Found(Page page, params (string Key, string Path)[] tunnels)
        {
            page.Door.Take(new JsonObject { ["t"] = "found", ["tunnels"] = new JsonArray(tunnels.Select(t => (JsonNode)new JsonObject { ["key"] = t.Key, ["path"] = t.Path }).ToArray()) });
        }

        private static void Report(Page page, string key, string state, string address)
        {
            page.Door.Take(new JsonObject { ["t"] = "tunnel", ["key"] = key, ["state"] = state, ["address"] = address });
        }
    }
}
