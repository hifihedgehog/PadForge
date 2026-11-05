using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// GameSense light by light (#557): the light event's handlers and posts
    /// field by field against gamesense-sdk's json-handlers-color.md, and the
    /// worker moving a type between its one-color event and its light event
    /// against a local stand-in for GG.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class GameSenseLightsTests : IDisposable
    {
        private static readonly Guid Keyboard = new("00000000-0000-0000-0000-0000000000b2");

        public GameSenseLightsTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static OutputPath Type(string type) => new(OutputFamily.GameSenseColor, type);

        private static KeyFrame Frame(Dictionary<ushort, int> keys = null, Dictionary<string, int> groups = null)
            => new(keys, groups, null, null);

        /// <summary>Each type's handlers name only device types that cover
        /// that kind of device alone, so one row's colors never reach a
        /// device of another kind: the keyboard's rgb-per-key-zones and
        /// rgb-5-zone (standard-zones.md:33, keyboards only) and the mouse's
        /// general type. rgb-2-zone and rgb-3-zone mix mice with headsets,
        /// mousepads and a keyboard (lines 29 and 31), and rgb-8-zone shares
        /// its mice with the mouse type's wheel and logo.</summary>
        [Fact]
        public void TheHandlers_NameOnlyDeviceTypesOfTheirOwnKind()
        {
            Assert.Equal(new[] { "rgb-5-zone", "rgb-per-key-zones" },
                GameSenseLights.HandlersOf("keyboard").Select(h => h.DeviceType).Distinct().OrderBy(t => t, StringComparer.Ordinal));
            Assert.Equal(new[] { "mouse" }, GameSenseLights.HandlersOf("mouse").Select(h => h.DeviceType).Distinct());
            Assert.Empty(GameSenseLights.HandlersOf("headset"));
        }

        /// <summary>
        /// The light event binds one context-color handler per light, run on
        /// every update (value_optional, sending-game-events.md:175). A key
        /// names its HID code in "custom-zone-keys" (json-handlers-color.md:96-107),
        /// the logo and the zones their fixed zone (lines 77-94), and each
        /// handler reads its own "context-frame-key" (lines 225-260).
        /// </summary>
        [Fact]
        public void TheLightEvent_BindsAContextColorHandlerPerLight()
        {
            using var doc = JsonDocument.Parse(GameSenseLights.BindBody(GameSenseClient.Game, "keyboard"));
            var root = doc.RootElement;
            Assert.Equal("PADFORGE", root.GetProperty("game").GetString());
            Assert.Equal("LIGHTS_KEYBOARD", root.GetProperty("event").GetString());
            Assert.True(root.GetProperty("value_optional").GetBoolean());
            var handlers = root.GetProperty("handlers").EnumerateArray().ToList();
            Assert.Equal(GameSenseLights.MapOf("keyboard").Lights.Count, handlers.Count);
            Assert.All(handlers, h => Assert.Equal("context-color", h.GetProperty("mode").GetString()));

            var w = Assert.Single(handlers, h => h.TryGetProperty("custom-zone-keys", out var k)
                                                 && k.EnumerateArray().Single().GetInt32() == 26);
            Assert.Equal("rgb-per-key-zones", w.GetProperty("device-type").GetString());
            Assert.Equal("key26", w.GetProperty("context-frame-key").GetString());
            Assert.False(w.TryGetProperty("zone", out _));

            var logo = Assert.Single(handlers, h => h.TryGetProperty("zone", out var z) && z.GetString() == "logo");
            Assert.Equal("rgb-per-key-zones", logo.GetProperty("device-type").GetString());
            var five = handlers.Where(h => h.GetProperty("device-type").GetString() == "rgb-5-zone").ToList();
            Assert.Equal(new[] { "one", "two", "three", "four", "five" }, five.Select(h => h.GetProperty("zone").GetString()));
            Assert.Equal(new[] { "zone1", "zone2", "zone3", "zone4", "zone5" },
                five.Select(h => h.GetProperty("context-frame-key").GetString()));

            using var mouse = JsonDocument.Parse(GameSenseLights.BindBody(GameSenseClient.Game, "mouse"));
            Assert.Equal(new[] { ("mouse", "wheel", "wheel"), ("mouse", "logo", "logo"), ("mouse", "base", "base") },
                mouse.RootElement.GetProperty("handlers").EnumerateArray()
                    .Select(h => (h.GetProperty("device-type").GetString(), h.GetProperty("zone").GetString(),
                        h.GetProperty("context-frame-key").GetString())));
        }

        /// <summary>A post carries each light's color under its frame key in
        /// the static color form, red, green and blue from 0 to 255
        /// (json-handlers-color.md:23-29).</summary>
        [Fact]
        public void ALightPost_CarriesEachLightsColorUnderItsFrameKey()
        {
            var map = GameSenseLights.MapOf("mouse");
            var colors = LightRenderer.Render(map, 0x0000FF, Frame(groups: new Dictionary<string, int> { [LightGroups.Logo] = 0x123456 }));
            using var doc = JsonDocument.Parse(GameSenseLights.EventBody(GameSenseClient.Game, "mouse", colors));
            Assert.Equal("LIGHTS_MOUSE", doc.RootElement.GetProperty("event").GetString());
            var frame = doc.RootElement.GetProperty("data").GetProperty("frame");
            int Rgb(string key)
            {
                var c = frame.GetProperty(key);
                return (c.GetProperty("red").GetInt32() << 16) | (c.GetProperty("green").GetInt32() << 8) | c.GetProperty("blue").GetInt32();
            }
            Assert.Equal((0x0000FF, 0x123456, 0x0000FF), (Rgb("wheel"), Rgb("logo"), Rgb("base")));
            Assert.Equal(3, frame.EnumerateObject().Count());
        }

        private static GameSenseBackend Backend(HidppHapticsTests.FakeGameSense server, Func<long> clock)
            => new(() => new GameSenseClient(server.WriteCoreProps(), 10000), _ => 0, 2, clock: clock);

        private static Dictionary<string, int> FrameOf(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("data").GetProperty("frame").EnumerateObject()
                .ToDictionary(p => p.Name, p => (p.Value.GetProperty("red").GetInt32() << 16)
                                                | (p.Value.GetProperty("green").GetInt32() << 8) | p.Value.GetProperty("blue").GetInt32());
        }

        private static string EventOf(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("event").GetString();
        }

        /// <summary>
        /// A keyboard claimed with light colors binds its light event and
        /// posts the picture. An equal picture is not posted again. When the
        /// light colors go, the light event is removed before the one-color
        /// event is bound and posted. The connect removes a light event a
        /// past session left, as it removes the one-color events.
        /// </summary>
        [Fact]
        public void TheWorker_MovesATypeBetweenItsLightAndOneColorEvents()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            Assert.Null(PeripheralLightMaps.For(Type("keyboard")));
            long now = 1_000_000;
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } } }));
            using var backend = Backend(server, () => Volatile.Read(ref now));
            backend.Start();
            Assert.Same(GameSenseLights.MapOf("keyboard"), PeripheralLightMaps.For(Type("keyboard")));
            Assert.Same(GameSenseLights.MapOf("mouse"), PeripheralLightMaps.For(Type("mouse")));

            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF,
                keys: Frame(new Dictionary<ushort, int> { [0x1A] = 0xFF0000 }));
            List<(string Path, string Body)> Lights() => server.Posts
                .Where(p => p.Path == "/game_event" && EventOf(p.Body) == "LIGHTS_KEYBOARD").ToList();
            Assert.True(SpinWait.SpinUntil(() => Lights().Count >= 1, 5000), "the picture was posted");
            var posts = server.Posts.ToList();
            int bind = posts.FindIndex(p => p.Path == "/bind_game_event" && EventOf(p.Body) == "LIGHTS_KEYBOARD");
            int removed = posts.FindIndex(p => p.Path == "/remove_game_event" && EventOf(p.Body) == "LIGHTS_KEYBOARD");
            Assert.True(removed >= 0 && removed < bind, "the connect removed a past session's light event first");
            Assert.DoesNotContain(posts, p => p.Path == "/bind_game_event" && EventOf(p.Body) == "COLOR_KEYBOARD");
            var frame = FrameOf(Lights()[0].Body);
            Assert.Equal(0xFF0000, frame["key26"]);
            Assert.Equal(0x0000FF, frame["key4"]);
            Assert.Equal(0x0000FF, frame["logo"]);

            // An equal frame, with the throttle run out: nothing new.
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF,
                keys: Frame(new Dictionary<ushort, int> { [0x1A] = 0xFF0000 }));
            Volatile.Write(ref now, now + GameSenseBackend.ColorMs);
            WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs);
            Assert.Single(Lights());

            // The light colors go.
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF);
            Volatile.Write(ref now, now + GameSenseBackend.ColorMs);
            Assert.True(SpinWait.SpinUntil(() => server.ColorValues("COLOR_KEYBOARD").Contains(0x0000FF), 5000), "the one color was posted");
            posts = server.Posts.ToList();
            int gone = posts.FindLastIndex(p => p.Path == "/remove_game_event" && EventOf(p.Body) == "LIGHTS_KEYBOARD");
            int colorBind = posts.FindIndex(p => p.Path == "/bind_game_event" && EventOf(p.Body) == "COLOR_KEYBOARD");
            Assert.True(gone > bind && gone < colorBind, "the light event went before the one-color event was bound");

            backend.Stop();
            Assert.Null(PeripheralLightMaps.For(Type("keyboard")));
        }

        /// <summary>GG runs and will not take PadForge's game: the tabs read
        /// that, not a wait for GG to start. With GG not running the wait
        /// stands. A connection clears it.</summary>
        [Fact]
        public void AnEngineThatRunsAndRefuses_IsARefusal_AndAConnectionClearsIt()
        {
            using var server = new HidppHapticsTests.FakeGameSense { Status = 500 };
            long now = 1_000_000;
            PeripheralOutputs.Presence = PeripheralPresence.None with { SteelSeriesGGRunning = true };
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } } }));
            using var backend = Backend(server, () => Volatile.Read(ref now));
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF);
            Assert.True(SpinWait.SpinUntil(() => PeripheralOutputs.RefusalOn(Type("keyboard")) != null, 5000), "the refusal was recorded");
            Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.GameSenseColor));

            PeripheralOutputs.Presence = PeripheralPresence.None;
            Assert.True(SpinWait.SpinUntil(() => PeripheralOutputs.RefusalOn(Type("keyboard")) == null, 5000), "GG is gone, so it refused nothing");

            PeripheralOutputs.Presence = PeripheralPresence.None with { SteelSeriesGGRunning = true };
            server.Status = 200;
            Volatile.Write(ref now, now + GameSenseBackend.RetryMs);
            Assert.True(SpinWait.SpinUntil(() => PeripheralOutputs.StateOf(OutputFamily.GameSenseColor) == BackendState.Connected, 5000));
            Assert.Null(PeripheralOutputs.RefusalOn(Type("keyboard")));
        }

        /// <summary>A bind that outlasts the post's timeout is no answer. It
        /// is not recorded as GG refusing the event: the connection drops, and
        /// the next one binds the light event and paints the keys.</summary>
        [Fact]
        public void ALightBindThatGetsNoAnswer_IsNotARefusal_AndTheNextConnectionBindsItAgain()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            int binds = 0;
            server.BeforeAnswer = path =>
            {
                if (path == "/bind_game_event" && server.Posts.LastOrDefault().Body?.Contains("\"LIGHTS_") == true
                    && Interlocked.Increment(ref binds) == 1)
                    Thread.Sleep(600);
            };
            const string refused = "PERIPHERAL GameSense refused the keyboard light event";
            int before = PadForge.Engine.SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(refused));
            long now = 1_000_000;
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } } }));
            using var backend = new GameSenseBackend(() => new GameSenseClient(server.WriteCoreProps(), 200), _ => 0, 2,
                clock: () => Volatile.Read(ref now));
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF,
                keys: Frame(new Dictionary<ushort, int> { [0x1A] = 0xFF0000 }));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref binds) >= 1, 5000), "the first bind went out");
            Thread.Sleep(700);
            Volatile.Write(ref now, now + GameSenseBackend.RetryMs);
            Assert.True(SpinWait.SpinUntil(() => server.Posts.Any(p => p.Path == "/game_event" && EventOf(p.Body) == "LIGHTS_KEYBOARD"), 5000),
                "the light event was bound on the next connection and the picture posted");
            Assert.Equal(before, PadForge.Engine.SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(refused)));
            Assert.DoesNotContain(server.Posts, p => p.Path == "/bind_game_event" && EventOf(p.Body) == "COLOR_KEYBOARD");
        }

        /// <summary>A worker a stop gave up on says nothing when it comes back. What the tabs
        /// show is the next run's.</summary>
        [Fact]
        public void AWorkerAStopGaveUpOn_SaysNothingOverTheNextRun()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            int held = 0;
            server.BeforeAnswer = _ =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0)
                {
                    entered.Set();
                    gate.Wait(30000);
                }
            };
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } } }));
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF);
            var first = new GameSenseBackend(() => new GameSenseClient(server.WriteCoreProps(), 30000), _ => 0, 2);
            GameSenseBackend next = null;
            try
            {
                first.Start();
                Assert.True(entered.Wait(30000), "the first worker is inside a request");
                first.Stop();

                next = new GameSenseBackend(() => new GameSenseClient(server.WriteCoreProps(), 30000), _ => 0, 2);
                next.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => PeripheralOutputs.StateOf(OutputFamily.GameSenseColor) == BackendState.Connected, 30000), "the next run connected");

                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => !first.WorkerAliveForTest, 30000), "the first worker ended");
                Assert.Equal(BackendState.Connected, PeripheralOutputs.StateOf(OutputFamily.GameSenseColor));
            }
            finally
            {
                gate.Set();
                next?.Dispose();
                first.Dispose();
            }
        }

        /// <summary>A bind that outlasts the timeout on every connection does
        /// not hold the device dark: the second miss paints one color, and
        /// the log says GG did not answer.</summary>
        [Fact]
        public void ALightBindThatNeverGetsAnAnswer_PaintsOneColor_AfterTheSecondMiss()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            server.BeforeAnswer = path =>
            {
                if (path == "/bind_game_event" && server.Posts.LastOrDefault().Body?.Contains("\"LIGHTS_") == true)
                    Thread.Sleep(400);
            };
            const string line = "PERIPHERAL GameSense did not answer the keyboard light event twice";
            int before = PadForge.Engine.SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(line));
            long now = 1_000_000;
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } } }));
            using var backend = new GameSenseBackend(() => new GameSenseClient(server.WriteCoreProps(), 200), _ => 0, 2,
                clock: () => Volatile.Read(ref now));
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF,
                keys: Frame(new Dictionary<ushort, int> { [0x1A] = 0xFF0000 }));
            bool Painted() => server.ColorValues("COLOR_KEYBOARD").Contains(0x0000FF);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Interlocked.Add(ref now, GameSenseBackend.RetryMs);
                Thread.Sleep(50);
                return Painted();
            }, 15000), "the one color was posted");
            Assert.Equal(before + 1, PadForge.Engine.SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(line)));
        }

        /// <summary>A GG that will not take the light event leaves the type
        /// on its one-color event, in the claim's color, and logs it once.</summary>
        [Fact]
        public void ARefusedLightEvent_PaintsOneColor()
        {
            using var server = new HidppHapticsTests.FakeGameSense
            {
                StatusFor = (path, body) => path == "/bind_game_event" && body.Contains("\"LIGHTS_") ? 400 : null,
            };
            long now = 1_000_000;
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } } }));
            const string line = "PERIPHERAL GameSense refused the keyboard light event";
            int lines = PadForge.Engine.SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(line));
            using var backend = Backend(server, () => Volatile.Read(ref now));
            backend.Start();
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0x00, 0xFF,
                keys: Frame(new Dictionary<ushort, int> { [0x1A] = 0xFF0000 }));
            Assert.True(SpinWait.SpinUntil(() => server.ColorValues("COLOR_KEYBOARD").Contains(0x0000FF), 5000), "the one color was posted");
            Assert.DoesNotContain(server.Posts, p => p.Path == "/game_event" && EventOf(p.Body) == "LIGHTS_KEYBOARD");

            // A new picture stays on the one-color event, with no second try.
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x00, 0xFF, 0x00,
                keys: Frame(new Dictionary<ushort, int> { [0x1A] = 0xFF0000 }));
            Volatile.Write(ref now, now + GameSenseBackend.ColorMs);
            Assert.True(SpinWait.SpinUntil(() => server.ColorValues("COLOR_KEYBOARD").Contains(0x00FF00), 5000));
            Assert.Equal(1, server.Posts.Count(p => p.Path == "/bind_game_event" && EventOf(p.Body) == "LIGHTS_KEYBOARD"));
            Assert.Equal(lines + 1, PadForge.Engine.SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(line)));
        }
    }
}
