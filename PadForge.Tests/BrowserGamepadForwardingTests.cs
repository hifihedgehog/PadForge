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
            // The picker reads the device objects: two axes, twelve buttons, no hat.
            var objs = pad.GetDeviceObjects();
            Assert.Equal(new[] { 0, 1 }, objs.Where(o => o.ObjectType == DeviceObjectTypeFlags.AbsoluteAxis).Select(o => o.InputIndex).OrderBy(i => i).ToArray());
            Assert.Equal(Enumerable.Range(0, 12).ToArray(), objs.Where(o => o.ObjectType == DeviceObjectTypeFlags.PushButton).Select(o => o.InputIndex).OrderBy(i => i).ToArray());
            Assert.DoesNotContain(objs, o => o.ObjectType == DeviceObjectTypeFlags.PointOfViewController);
            // The state array stays six wide (a tracked limitation shared with
            // built pads: NumAxes and NumHats report the stock shape).
            Assert.Equal(6, pad.NumAxes);
        }

        [Fact]
        public void RawShape_RestsEveryAxisAtCenter_FromRegistration()
        {
            // The constructor puts the trigger axes at zero. For a raw pad that
            // is an endpoint, and the pad is registered before its first
            // snapshot arrives, so the raw path must center them itself.
            var pad = Pad();
            WebControllerServer.ConfigureGamepadShape(pad, "raw", "12", "6");
            var s = pad.GetCurrentState();
            for (int i = 0; i < 6; i++) Assert.Equal(32767, s.Axis[i]);
            Assert.True(pad.AxesCenterAtRest);
            var stock = Pad();
            WebControllerServer.ConfigureGamepadShape(stock, "standard", "17", "4");
            Assert.False(stock.AxesCenterAtRest);
            Assert.Equal(0, stock.GetCurrentState().Axis[2]);
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

        // ── Acknowledged freshness (the server's state machine) ──────────

        [Fact]
        public void Freshness_ATimelyEcho_KeepsTheSessionFresh()
        {
            var f = new WebControllerServer.GamepadFreshness(1000);
            for (long t = 2000; t <= 20000; t += 1000)
            {
                Assert.False(f.ShouldExpire(t));
                int n = f.NextPing(t);
                Assert.False(f.Ack(n, t + 40));             // fresh, no transition
                Assert.False(f.Expired);
            }
        }

        [Fact]
        public void Freshness_NoEcho_ExpiresAfterTheDeadline_Once()
        {
            var f = new WebControllerServer.GamepadFreshness(1000);
            f.NextPing(2000); f.NextPing(3000); f.NextPing(4000);
            Assert.False(f.ShouldExpire(4000));             // 3000 ms exactly is not over
            Assert.True(f.ShouldExpire(4001));
            f.Expired = true;
            Assert.False(f.ShouldExpire(9000));             // already expired: fires once
        }

        [Fact]
        public void Freshness_ASlowEcho_RenewsNothing()
        {
            // The echo of a ping that took longer than the round-trip bound is
            // as stale as the input queued in front of it.
            var f = new WebControllerServer.GamepadFreshness(1000);
            int n = f.NextPing(2000);
            Assert.False(f.Ack(n, 2000 + WebControllerServer.GamepadFreshness.MaxRoundTripMs + 1));
            Assert.Equal(1000, f.FreshTicks);
            Assert.True(f.ShouldExpire(4001));
        }

        [Fact]
        public void Freshness_AFreshEchoAfterExpiry_IsTheOneResyncSignal()
        {
            var f = new WebControllerServer.GamepadFreshness(1000);
            f.NextPing(2000); f.NextPing(3000); f.NextPing(4000);
            Assert.True(f.ShouldExpire(5000)); f.Expired = true;
            int n = f.NextPing(6000);
            Assert.True(f.Ack(n, 6050));                    // expired -> fresh: resync
            Assert.False(f.Expired);
            Assert.Equal(6050, f.FreshTicks);
            int m = f.NextPing(7000);
            Assert.False(f.Ack(m, 7050));                   // already fresh: no second resync
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(99)]
        public void Freshness_AnUnknownPingNumber_IsIgnored(int seq)
        {
            var f = new WebControllerServer.GamepadFreshness(1000);
            f.NextPing(2000);
            Assert.False(f.Ack(seq, 2010));
            Assert.Equal(1000, f.FreshTicks);
        }

        [Fact]
        public void Freshness_APingFallenOutOfTheRing_IsIgnored()
        {
            // The ring holds eight pings. Ping 1's slot is reused by ping 9 at
            // 9000. An echo of ping 1 arriving at 9050 sits within the round
            // trip of the REUSED slot's tick, so a missing eviction check would
            // accept it. The session stays expired and its fresh tick unmoved.
            var f = new WebControllerServer.GamepadFreshness(0);
            int first = f.NextPing(1000);
            for (int i = 0; i < 8; i++) f.NextPing(2000 + i * 1000);
            f.Expired = true;
            Assert.False(f.Ack(first, 9050));
            Assert.True(f.Expired);
            Assert.Equal(0, f.FreshTicks);
        }

        [Theory]
        [InlineData(1499, true)]
        [InlineData(1500, true)]
        [InlineData(1501, false)]
        public void Freshness_TheRoundTripBound_IsInclusive(int rtt, bool fresh)
        {
            var f = new WebControllerServer.GamepadFreshness(0);
            f.Expired = true;
            int n = f.NextPing(1000);
            Assert.Equal(fresh, f.Ack(n, 1000 + rtt));
            Assert.Equal(!fresh, f.Expired);
        }

        // ── Raw rest values ─────────────────────────────────────────────

        [Fact]
        public void RawRest_ComesFromThePage_AndAStockShapeIgnoresIt()
        {
            var raw = Pad();
            WebControllerServer.ConfigureGamepadShape(raw, "raw", "12", "6");
            Apply(raw, "{\"type\":\"caps\",\"vibrate\":true,\"mapping\":\"\",\"buttons\":12,\"axes\":6,\"rest\":{\"2\":0,\"5\":0,\"0\":32767,\"9\":1,\"x\":5}}");
            raw.UpdateAxis(2, 65535); raw.UpdateAxis(5, 40000); raw.UpdateAxis(1, 0);
            raw.NeutralizeAll();
            var s = raw.GetCurrentState();
            Assert.Equal(0, s.Axis[2]); Assert.Equal(0, s.Axis[5]);     // the pad's own rest
            Assert.Equal(32767, s.Axis[0]); Assert.Equal(32767, s.Axis[1]);
            raw.SetRawAxisRest(3, 70000);
            raw.NeutralizeAll();
            Assert.Equal(65535, raw.GetCurrentState().Axis[3]);        // clamped
            var stock = Pad();
            WebControllerServer.ConfigureGamepadShape(stock, "standard", "17", "4");
            stock.SetRawAxisRest(0, 0);
            stock.UpdateAxis(0, 65535);
            stock.NeutralizeAll();
            Assert.Equal(32767, stock.GetCurrentState().Axis[0]);      // stock rest is fixed
        }

        [Fact]
        public void RawRest_ArrivingAfterExpiry_LandsAtOnce()
        {
            // The deadline neutralized to the default center before caps came.
            // It fires once per expiry, so the rest must be applied on arrival.
            var raw = Pad();
            WebControllerServer.ConfigureGamepadShape(raw, "raw", "12", "6");
            var f = new WebControllerServer.GamepadFreshness(Environment.TickCount64 - 10_000);
            f.Expired = true;
            raw.NeutralizeAll();
            Assert.Equal(32767, raw.GetCurrentState().Axis[2]);
            Apply(raw, "{\"type\":\"caps\",\"vibrate\":true,\"mapping\":\"\",\"rest\":{\"2\":0}}", accept: false, f);
            Assert.Equal(0, raw.GetCurrentState().Axis[2]);
            // Fresh session: the rest is stored, the live state is left alone.
            var live = Pad();
            WebControllerServer.ConfigureGamepadShape(live, "raw", "12", "6");
            live.UpdateAxis(2, 65535);
            Apply(live, "{\"type\":\"caps\",\"vibrate\":true,\"mapping\":\"\",\"rest\":{\"2\":0}}", accept: true, new WebControllerServer.GamepadFreshness(Environment.TickCount64));
            Assert.Equal(65535, live.GetCurrentState().Axis[2]);
        }

        // ── Rumble that outlives its source ─────────────────────────────

        [Fact]
        public void LoadFromWebDevice_KeepsTheFeedbackCache_ForTheSameDevice_AndStartsCleanForANewOne()
        {
            var web = Pad();
            var ud = new PadForge.Engine.Data.UserDevice();
            ud.LoadFromWebDevice(web);
            var first = ud.ForceFeedbackState;
            Assert.NotNull(first);
            ud.LoadFromWebDevice(web);                                  // a capability refresh
            Assert.Same(first, ud.ForceFeedbackState);
            var replacement = new WebControllerDevice(web.DevicePath.Substring("web://".Length), web.Name, false, "gamepad");
            ud.LoadFromWebDevice(replacement);                          // a new connection
            Assert.NotSame(first, ud.ForceFeedbackState);
        }

        [Fact]
        public void TheFeedbackPass_StopsADeviceThatLeftItsLastSlot_AndTeardownIsOneStep()
        {
            // The teardown and the connect are pinned at the source: they sit
            // inside a live socket handler. The feedback pass itself runs for
            // real in BrowserGamepadFeedbackPassTests.
            string step2 = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs"));
            int at = step2.IndexOf("if (slotCount == 0)", StringComparison.Ordinal);
            Assert.True(at > 0);
            string block = step2.Substring(at, 2400);
            Assert.Contains("ud.ForceFeedbackState.StopDeviceForces(web);", block);
            Assert.Contains("is PadForge.Engine.WebControllerDevice web", block);          // web pads only
            Assert.Contains("!RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath)", block); // a peer's output stays
            Assert.Contains("!RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath)", block);   // past its lease too
            Assert.True(block.IndexOf("StopDeviceForces", StringComparison.Ordinal) < block.IndexOf("return;", StringComparison.Ordinal));
            string server = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "WebControllerServer.cs"));
            int fin = server.IndexOf("bool stillRegistered =", StringComparison.Ordinal);
            Assert.True(fin > 0);
            string before = server.Substring(Math.Max(0, fin - 120), 120);
            Assert.Contains("lock (_registrationLock)", before);
            // The connect callback is delivered inside the registration lock, right
            // after the install, so a superseded session cannot deliver it late.
            int install = server.IndexOf("_clients[compositeKey] = session;", StringComparison.Ordinal);
            Assert.True(install > 0);
            string after = server.Substring(install, 900);
            int connectAt = after.IndexOf("DeviceConnected?.Invoke(device)", StringComparison.Ordinal);
            int closeAt = after.IndexOf("\n                }", StringComparison.Ordinal);
            Assert.True(connectAt > 0 && connectAt < closeAt, "DeviceConnected must fire before the registration lock closes");
        }

        // ── ProcessMessage under the freshness policy ───────────────────

        private static bool Apply(WebControllerDevice pad, string json, bool accept = true, WebControllerServer.GamepadFreshness f = null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            return WebControllerServer.ProcessMessage(pad, bytes, bytes.Length, accept, f);
        }

        [Fact]
        public void ProcessMessage_DropsInputWhileExpired_AndAppliesItWhenFresh()
        {
            var pad = Pad();
            Assert.False(Apply(pad, "{\"type\":\"input\",\"kind\":\"button\",\"code\":0,\"value\":1}", accept: false));
            Assert.False(pad.GetCurrentState().Buttons[0]);
            Assert.False(Apply(pad, "{\"type\":\"input\",\"kind\":\"axis\",\"code\":0,\"value\":65535}", accept: false));
            Assert.Equal(32767, pad.GetCurrentState().Axis[0]);
            Assert.False(Apply(pad, "{\"type\":\"input\",\"kind\":\"button\",\"code\":0,\"value\":1}"));
            Assert.True(pad.GetCurrentState().Buttons[0]);
        }

        [Fact]
        public void ProcessMessage_AnEchoIsAppliedEvenWhileExpired_AndReportsTheTransition()
        {
            var pad = Pad();
            var f = new WebControllerServer.GamepadFreshness(Environment.TickCount64 - 10_000);
            f.Expired = true;
            int n = f.NextPing(Environment.TickCount64);
            Assert.True(Apply(pad, "{\"type\":\"hb\",\"n\":" + n + "}", accept: false, f));
            Assert.False(f.Expired);
            Assert.False(Apply(pad, "{\"type\":\"hb\"}", accept: true, f));           // no number: nothing
            Assert.False(Apply(pad, "{\"type\":\"hb\",\"n\":\"x\"}", accept: true, f)); // malformed: nothing
        }

        [Fact]
        public void ProcessMessage_RefusesTouchOnAForwardedPad_AndKeepsItForDrawnPads()
        {
            var pad = Pad();
            Assert.False(Apply(pad, "{\"type\":\"touchpad\",\"finger\":0,\"x\":0.5,\"y\":0.5,\"down\":true}"));
            Assert.False(pad.HasTouchpad);
            var ds4 = Pad("ds4");
            Apply(ds4, "{\"type\":\"touchpad\",\"finger\":0,\"x\":0.5,\"y\":0.5,\"down\":true}");
            Assert.True(ds4.HasTouchpad);
        }

        [Fact]
        public void ProcessMessage_CapsStillLandWhileExpired()
        {
            var pad = Pad();
            Assert.True(pad.HasRumble);
            Apply(pad, "{\"type\":\"caps\",\"vibrate\":false}", accept: false);
            Assert.False(pad.HasRumble);
        }

        [Fact]
        public void ThePage_SpeaksTheFreshnessAndResyncWire()
        {
            // The translator is not executed here (no JavaScript host). These pin
            // the wire the page speaks against the server's, and the identity
            // rules, by reading the script.
            string server = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "WebControllerServer.cs"));
            Assert.Contains("type = \"resync\"", server);
            Assert.Contains("type = \"ping\", n = seq", server);
            string js = Asset("js", "gamepad_client.js");
            Assert.Contains("msg.type === \"ping\"", js);
            Assert.Contains("type: \"hb\", n: msg.n", js);
            Assert.Contains("msg.type === \"resync\"", js);
            Assert.Contains("slot.sent = null; slot.needSnapshot = true;", js);
            // An echo needs a fresh poll and an uncongested socket.
            Assert.Contains("if (now() - lastPollTs > FRESH_SAMPLE_MS) return;", js);
            Assert.Contains("bufferedAmount > BUFFER_LIMIT_BYTES) return;", js);
            // Identity: per tab, and claimed across tabs so a copied tab mints its own.
            Assert.Contains("sessionStorage.getItem(clientIdKey)", js);
            Assert.DoesNotContain("localStorage.", js);        // no call, the comment may name it
            Assert.Contains("new BroadcastChannel(\"padforge_gamepad_identity\")", js);
            // Rumble is a lease the pings renew.
            Assert.Contains("RUMBLE_LEASE_MS", js);
            // A raw pad sends where its axes rest, and a late identity answer acts.
            Assert.Contains("caps.rest = translate(", js);
            Assert.Contains("else if (m.taken === clientId) onIdentityTaken();", js);
            // The vibrator bootstrap installs its timer before the first pulse.
            Assert.Contains("if (!phoneVibrate.timer) phoneVibrate.timer = setInterval(phonePulse, 150);\n        phonePulse();", js.Replace("\r\n", "\n"));
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
