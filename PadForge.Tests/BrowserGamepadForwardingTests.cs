using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #415 (discussion #402): a controller paired to a phone, or built
    /// into a handheld, forwarded through the browser's Gamepad API as a
    /// "Browser Gamepad" web device. The server-side arithmetic, the device
    /// shape both modes declare, the neutralize used by the input deadline,
    /// and the page's translation tables held against the server's slot map.
    /// The tables are pinned by reading the script's literals, because the
    /// test project carries no JavaScript host, and that limit is stated in
    /// the close.
    /// </summary>
    public class BrowserGamepadForwardingTests
    {
        // ── Slot arithmetic ─────────────────────────────────────────────

        [Theory]
        [InlineData(0, new int[0])]
        [InlineData(-3, new int[0])]
        [InlineData(1, new[] { 11 })]
        [InlineData(5, new[] { 11, 12, 13, 14, 15 })]
        [InlineData(6, new[] { 11, 12, 13, 14, 15, 17 })]       // 16 is the touchpad click, skipped
        [InlineData(10, new[] { 11, 12, 13, 14, 15, 17, 18, 19, 20, 21 })]
        [InlineData(14, new[] { 11, 12, 13, 14, 15, 17, 18, 19, 20, 21 })] // capped
        public void ExtendedSlots_FollowTheFixedOrder_AndSkipSixteen(int extras, int[] expected)
            => Assert.Equal(expected, WebControllerServer.GamepadExtendedSlots(extras));

        [Fact]
        public void RawSurface_IsIndexToSlot_WithSixteenSkipped_AndTwentyOneTheCeiling()
        {
            WebControllerServer.GamepadRawSurface(20, 8, out var axes, out var buttons);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, axes);                      // six axes at most
            Assert.Equal(Enumerable.Range(0, 16).Concat(new[] { 17, 18, 19, 20 }).ToArray(), buttons);
            WebControllerServer.GamepadRawSurface(30, 2, out axes, out buttons);
            Assert.Equal(new[] { 0, 1 }, axes);
            Assert.Equal(21, buttons.Max());                                   // 21 is the ceiling
            Assert.DoesNotContain(16, buttons);
            WebControllerServer.GamepadRawSurface(0, 0, out axes, out buttons);
            Assert.Empty(axes); Assert.Empty(buttons);
        }

        // ── Device shape ────────────────────────────────────────────────

        private static WebControllerDevice Pad(string key = "gamepad")
            => new WebControllerDevice("test-" + Guid.NewGuid().ToString("N"), "Browser Gamepad 1", false, key);

        [Fact]
        public void StandardShape_SeventeenButtons_IsTheStandardEleven_NoExtras()
        {
            var pad = Pad();
            WebControllerServer.ConfigureGamepadShape(pad, "standard", "17", "4");
            Assert.Equal(11, pad.NumButtons);
            Assert.Equal(Enumerable.Range(0, 11).ToArray(), pad.SupportedButtonIndices);
            Assert.Equal(6, pad.NumAxes);
            Assert.Equal(1, pad.NumHats);
        }

        [Fact]
        public void StandardShape_ExtrasLandOnTheExtendedSlots()
        {
            var pad = Pad();
            WebControllerServer.ConfigureGamepadShape(pad, "standard", "20", "4");   // three extras
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }, pad.SupportedButtonIndices);
            Assert.Equal(14, pad.NumButtons);
            var wide = Pad();
            WebControllerServer.ConfigureGamepadShape(wide, "standard", "40", "4");  // capped at ten extras
            Assert.Equal(22, wide.NumButtons);
            Assert.Equal(21, wide.SupportedButtonIndices.Length);
            Assert.DoesNotContain(16, wide.SupportedButtonIndices);
        }

        [Fact]
        public void RawShape_DeclaresExactlyThePadsControls_AndNoHat()
        {
            var pad = Pad();
            WebControllerServer.ConfigureGamepadShape(pad, "raw", "12", "2");
            Assert.Equal(Enumerable.Range(0, 12).ToArray(), pad.SupportedButtonIndices);
            // The custom surface reports the pad's real axes through the device
            // objects the picker reads; the state array stays six wide.
            Assert.Equal(6, pad.NumAxes);
        }

        [Fact]
        public void MalformedShapeQuery_FallsBackToTheStandardPad()
        {
            var pad = Pad();
            WebControllerServer.ConfigureGamepadShape(pad, "standard", "-5", "zzz");
            Assert.Equal(11, pad.NumButtons);
            var pad2 = Pad();
            WebControllerServer.ConfigureGamepadShape(pad2, null, null, null);
            Assert.Equal(11, pad2.NumButtons);
        }

        [Fact]
        public void ProductGuid_IsDistinctFromEveryDrawnLayoutAndTheTouchpad()
        {
            var gp = Pad().ProductGuid;
            foreach (var other in new[] { "xbox360", "ds4", "dualsense", "xboxseries", "switchpro", "custom:abc" })
                Assert.NotEqual(gp, Pad(other).ProductGuid);
            Assert.NotEqual(gp, new WebControllerDevice("t-" + Guid.NewGuid().ToString("N"), "Web Touchpad 1", true, "touchpad").ProductGuid);
        }

        // ── Neutralize (the input deadline's action) ────────────────────

        [Fact]
        public void NeutralizeAll_ReleasesButtons_CentersSticks_ZeroesTriggers_CentersTheHat()
        {
            var pad = Pad();
            pad.UpdateButton(0, true); pad.UpdateButton(12, true);
            pad.UpdateAxis(0, 65535); pad.UpdateAxis(2, 40000); pad.UpdateAxis(4, 0); pad.UpdateAxis(5, 65535);
            pad.UpdatePov(9000);
            pad.NeutralizeAll();
            var s = pad.GetCurrentState();
            Assert.False(s.Buttons[0]); Assert.False(s.Buttons[12]);
            Assert.Equal(32767, s.Axis[0]); Assert.Equal(32767, s.Axis[4]);
            Assert.Equal(0, s.Axis[2]); Assert.Equal(0, s.Axis[5]);
            Assert.Equal(-1, s.Povs[0]);
        }

        [Fact]
        public void NeutralizeAll_OnARawSurface_RestsEveryAxisAtCenter()
        {
            // A raw forwarded pad has no trigger semantics: the page's own
            // neutral sends 32767 on every axis, and the deadline must agree,
            // or a raw pad reads two axes at the far end after a timeout.
            var pad = Pad();
            WebControllerServer.ConfigureGamepadShape(pad, "raw", "12", "6");
            pad.UpdateAxis(2, 65535); pad.UpdateAxis(5, 0); pad.UpdateButton(3, true);
            pad.NeutralizeAll();
            var s = pad.GetCurrentState();
            Assert.False(s.Buttons[3]);
            for (int i = 0; i < 6; i++) Assert.Equal(32767, s.Axis[i]);
        }

        [Fact]
        public void Deadline_TellsThePageToResync_AndThePageObeys()
        {
            // After a neutralize the page's change cache still holds its last
            // sent values, so an unchanged hold would stay released until it
            // changed. The server asks for a resend and the page drops its cache.
            string server = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "WebControllerServer.cs"));
            Assert.Contains("type = \"resync\"", server);
            string js = Asset("js", "gamepad_client.js");
            Assert.Contains("msg.type === \"resync\"", js);
            Assert.Contains("slot.sent = null; slot.needSnapshot = true;", js);
            // Identity is per tab, like the drawn controller pages, so two tabs
            // never replace each other's server sessions under one key.
            Assert.Contains("sessionStorage.getItem(clientIdKey)", js);
            Assert.DoesNotContain("localStorage.", js);        // no call, the comment may name it
            // A heartbeat needs a fresh poll and an uncongested socket.
            Assert.Contains("if (now() - lastPollTs > FRESH_SAMPLE_MS) return;", js);
            Assert.Contains("bufferedAmount > BUFFER_LIMIT_BYTES) return;", js);
        }

        // ── The page's tables against the server's slot map ─────────────

        private static string Asset(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "PadForge.App", "WebAssets" }.Concat(parts).ToArray()));

        private static Dictionary<string, (string kind, int code)> ServerMap()
        {
            var f = typeof(WebControllerServer).GetField("_targetInputMap", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f);
            return (Dictionary<string, (string kind, int code)>)f.GetValue(null);
        }

        [Fact]
        public void PageTables_MatchTheServerSlots()
        {
            string js = Asset("js", "gamepad_client.js");
            var map = ServerMap();

            // Browser standard buttons -> PadForge slots, by the server's own names.
            var buttonTable = Regex.Match(js, @"var STD_BUTTON_TO_SLOT = \{([^}]*)\};").Groups[1].Value;
            var pairs = Regex.Matches(buttonTable, @"(\d+):\s*(\d+)").Cast<Match>()
                .ToDictionary(m => int.Parse(m.Groups[1].Value), m => int.Parse(m.Groups[2].Value));
            var expected = new Dictionary<int, string>
            {
                [0] = "ButtonA", [1] = "ButtonB", [2] = "ButtonX", [3] = "ButtonY",
                [4] = "LeftShoulder", [5] = "RightShoulder", [8] = "ButtonBack", [9] = "ButtonStart",
                [10] = "LeftThumbButton", [11] = "RightThumbButton", [16] = "ButtonGuide",
            };
            Assert.Equal(expected.Keys.OrderBy(k => k), pairs.Keys.OrderBy(k => k));
            foreach (var kv in expected)
            {
                Assert.Equal("button", map[kv.Value].kind);
                Assert.Equal(map[kv.Value].code, pairs[kv.Key]);
            }
            Assert.DoesNotContain(16, pairs.Values);          // never the touchpad click

            // Triggers are the trigger axes.
            var trig = Regex.Match(js, @"var STD_TRIGGER_TO_AXIS = \{([^}]*)\};").Groups[1].Value;
            var tpairs = Regex.Matches(trig, @"(\d+):\s*(\d+)").Cast<Match>()
                .ToDictionary(m => int.Parse(m.Groups[1].Value), m => int.Parse(m.Groups[2].Value));
            Assert.Equal(map["LeftTrigger"].code, tpairs[6]);
            Assert.Equal(map["RightTrigger"].code, tpairs[7]);

            // Sticks: browser 0,1,2,3 -> PadForge 0,1 and 3,4.
            var axes = Regex.Match(js, @"var STD_AXIS_TO_SERVER = \[([^\]]*)\];").Groups[1].Value
                .Split(',').Select(s => int.Parse(s.Trim())).ToArray();
            Assert.Equal(new[] { map["LeftThumbRing"].code, map["LeftThumbRing"].code + 1, map["RightThumbRing"].code, map["RightThumbRing"].code + 1 }, axes);

            // Extras: the page's order is the server's order.
            var extras = Regex.Match(js, @"var EXTRA_SLOTS = \[([^\]]*)\];").Groups[1].Value
                .Split(',').Select(s => int.Parse(s.Trim())).ToArray();
            Assert.Equal(WebControllerServer.GamepadExtraSlotOrder, extras);

            // The D-pad goes out as one hat with code 0, integers only.
            Assert.Contains("kind: \"pov\", code: 0, value:", js);
            Assert.Contains("type: \"hb\"", js);
            Assert.Contains("layout=gamepad&mode=", js);
        }

        [Fact]
        public void Pages_MountTheFullscreenToggle_AndTheLandingLinksTheGamepadPage()
        {
            foreach (var page in new[] { "controller.html", "touchpad.html", "custom.html", "gamepad.html" })
            {
                string html = Asset(page);
                Assert.Contains("/js/fullscreen.js", html);
                Assert.Contains("id=\"fsMount\"", html);
            }
            Assert.Contains("href=\"/gamepad.html\"", Asset("index.html"));
            Assert.Contains("/js/gamepad_client.js", Asset("gamepad.html"));
            string fs = Asset("js", "fullscreen.js");
            Assert.Contains("fullscreenEnabled", fs);
            Assert.Contains("requestFullscreen", fs);
            Assert.Contains("fullscreenchange", fs);
        }

        [Fact]
        public void PageClient_RefusesToGuessANonStandardMapping()
        {
            string js = Asset("js", "gamepad_client.js");
            Assert.Contains("gp.mapping === \"standard\" ? \"standard\" : \"raw\"", js);
            // The raw translator maps index to slot, skipping the touchpad click.
            Assert.Contains("var slot = i < 16 ? i : i + 1;", js);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
