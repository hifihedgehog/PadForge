using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Razer Chroma worker's decisions (#494): which category shows
    /// which color, and what the session tells Synapse. The worker's loop
    /// only carries these decisions to the REST server.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class ChromaBackendTests : IDisposable
    {
        private static readonly Guid Mouse = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Keyboard = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid ChromaRow = new("00000000-0000-0000-0000-0000000000c3");

        public ChromaBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        private static OutputPath Category(string name) => new(OutputFamily.ChromaCategory, name);

        /// <summary>Each wanted category and its color, the light colors
        /// left out.</summary>
        private static List<KeyValuePair<string, int>> Wanted(Func<int, ISet<Guid>> devicesOnSlot = null,
            long? now = null, Func<int, int> slotNumber = null)
            => Wants(devicesOnSlot, now, slotNumber).Select(w => new KeyValuePair<string, int>(w.Category, w.Rgb)).ToList();

        private static List<ChromaBackend.Want> Wants(Func<int, ISet<Guid>> devicesOnSlot = null,
            long? now = null, Func<int, int> slotNumber = null)
            => ChromaBackend.Wanted(PeripheralOutputs.Links, now ?? Environment.TickCount64,
                devicesOnSlot ?? (_ => new HashSet<Guid>()), slotNumber ?? (slot => slot + 1));

        [Fact]
        public void TheColorIntegerIsBgr_AndTheInitListsTheSessionsCategories()
        {
            Assert.Equal(0x0000FF, ChromaLayouts.ToBgr(0xFF0000));
            Assert.Equal(0x00FF00, ChromaLayouts.ToBgr(0x00FF00));
            Assert.Equal(0xFF0000, ChromaLayouts.ToBgr(0x0000FF));
            Assert.Equal(0x563412, ChromaLayouts.ToBgr(0x123456));

            using var doc = JsonDocument.Parse(ChromaBackend.InitBody(new[] { "mouse", "keypad" }));
            var root = doc.RootElement;
            Assert.Equal("PadForge", root.GetProperty("title").GetString());
            Assert.Equal("application", root.GetProperty("category").GetString());
            Assert.Equal(new[] { "mouse", "keypad" },
                root.GetProperty("device_supported").EnumerateArray().Select(e => e.GetString()));
        }

        [Fact]
        public void AnEffectIsAcceptedOnlyByItsResult()
        {
            Assert.True(ChromaBackend.TryReadResult("{\"result\":0}", out int ok));
            Assert.Equal(0, ok);
            Assert.True(ChromaBackend.TryReadResult("{\"result\":1167}", out int absent));
            Assert.Equal(ChromaBackend.ResultDeviceNotConnected, absent);
            Assert.False(ChromaBackend.TryReadResult("[]", out _));
            Assert.False(ChromaBackend.TryReadResult("not json", out _));
            Assert.False(ChromaBackend.TryReadResult("{\"result\":\"0\"}", out _));
        }

        /// <summary>Nothing claimed, nothing wanted, so no session holds
        /// Synapse's lighting. Each claimed category carries its own color,
        /// in the canonical order the init lists them.</summary>
        [Fact]
        public void OnlyClaimedCategoriesAreWanted_EachInItsOwnColor()
        {
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            Assert.Empty(Wanted());

            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.Equal(new[] { new KeyValuePair<string, int>("mouse", 0x112233) }, Wanted());

            PeripheralOutputs.SetLighting(Keyboard, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            Assert.Equal(new[]
            {
                new KeyValuePair<string, int>("keyboard", 0x0000FF),
                new KeyValuePair<string, int>("mouse", 0x112233),
            }, Wanted());

            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            PeripheralOutputs.ReleaseLighting(Keyboard, 1);
            Assert.Empty(Wanted());
        }

        /// <summary>The Razer Chroma row fills every category no Razer device
        /// claims, and a device's own claim keeps its category whatever the
        /// player numbers.</summary>
        [Fact]
        public void TheChromaRowFillsTheRest_AndADeviceKeepsItsOwnCategory()
        {
            Link(new DeviceLinks
            {
                Device = ChromaRow,
                CatchAll = true,
                Lighting = PeripheralLinker.ChromaCategories.Select(Category).ToArray(),
            }, new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });

            PeripheralOutputs.SetLighting(ChromaRow, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Mouse, slot: 3, player: 4, 0x00, 0xFF, 0x00);
            var wanted = Wanted().ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(PeripheralLinker.ChromaCategories.Length, wanted.Count);
            Assert.Equal(0x00FF00, wanted["mouse"]);
            Assert.All(wanted.Where(p => p.Key != "mouse"), p => Assert.Equal(0xFF0000, p.Value));
        }

        /// <summary>Set Chroma Color (#468) paints the categories of the Razer
        /// devices assigned to its controller, whether or not their tabs take
        /// control, over any claim. Two controllers' macros on one category:
        /// the smaller displayed number rules, which here runs against the
        /// slot order. A slot with no Razer device of a category leaves that
        /// category alone. The assertions and the reads share one clock, so a
        /// stalled test thread never ages a macro out.</summary>
        [Fact]
        public void SetChromaColor_PaintsTheRazerDevicesOnItsController()
        {
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            var onSlot = new Dictionary<int, ISet<Guid>>
            {
                [2] = new HashSet<Guid> { Mouse },
                [5] = new HashSet<Guid> { Mouse, Keyboard },
            };
            ISet<Guid> DevicesOn(int slot) => onSlot.TryGetValue(slot, out var set) ? set : new HashSet<Guid>();
            // Slot 5 shows Virtual Controller 1 and slot 2 shows 2.
            int Shown(int slot) => slot switch { 5 => 1, 2 => 2, _ => slot + 10 };

            // No tab claims either device, and no macro is live.
            Assert.Empty(Wanted(DevicesOn, slotNumber: Shown));

            long now = Environment.TickCount64;
            PeripheralOutputs.AssertChromaMacro(2, 0x0A, 0x0B, 0x0C, now);
            var wanted = Wanted(DevicesOn, now, Shown).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(0x0A0B0C, wanted["mouse"]);
            Assert.False(wanted.ContainsKey("keyboard"));

            // Slot 5 has the larger index and the smaller displayed number.
            PeripheralOutputs.AssertChromaMacro(5, 0x01, 0x02, 0x03, now);
            wanted = Wanted(DevicesOn, now, Shown).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(0x010203, wanted["mouse"]);
            Assert.Equal(0x010203, wanted["keyboard"]);

            // A macro outranks a claim on its category.
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0x77, 0x77, 0x77);
            Assert.Equal(0x010203, Wanted(DevicesOn, now, Shown).Single(p => p.Key == "keyboard").Value);

            // Past the window the claim shows again.
            long later = now + PeripheralOutputs.MacroAssertWindowMs + 50;
            Assert.Equal(0x777777, Wanted(DevicesOn, later, Shown).Single(p => p.Key == "keyboard").Value);

            // A macro on a slot without a Razer device paints nothing.
            PeripheralOutputs.ResetForTest();
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            PeripheralOutputs.AssertChromaMacro(7, 0x01, 0x02, 0x03, now);
            Assert.Empty(Wanted(DevicesOn, now, Shown));
        }

        /// <summary>The lapse the worker waits for is the first tick past a
        /// live macro's window, and nothing while none is live.</summary>
        [Fact]
        public void TheNextLapseIsTheFirstTickPastAMacrosWindow()
        {
            long now = 1_000_000;
            Assert.Equal(long.MaxValue, PeripheralOutputs.NextChromaMacroLapse(now));
            PeripheralOutputs.AssertChromaMacro(2, 1, 2, 3, now);
            PeripheralOutputs.AssertChromaMacro(4, 1, 2, 3, now + 30);
            long lapse = now + PeripheralOutputs.MacroAssertWindowMs + 1;
            Assert.Equal(lapse, PeripheralOutputs.NextChromaMacroLapse(now + 10));
            Assert.True(PeripheralOutputs.TryGetChromaMacro(2, lapse - 1, out _));
            Assert.False(PeripheralOutputs.TryGetChromaMacro(2, lapse, out _));
            Assert.Equal(lapse + 30, PeripheralOutputs.NextChromaMacroLapse(lapse));
            Assert.Equal(long.MaxValue, PeripheralOutputs.NextChromaMacroLapse(lapse + 30));
        }

        // ─────────────────────────────────────────────
        //  A device lit on its own (#494)
        // ─────────────────────────────────────────────

        private static readonly OutputPath MouseUnit = new(OutputFamily.RazerUnit, @"\\?\hid#vid_1532&pid_0084&mi_00#mouse");
        private static readonly OutputPath KeyboardUnit = new(OutputFamily.RazerUnit, @"\\?\hid#vid_1532&pid_0203&mi_02#keyboard");

        /// <summary>The Razer Chroma row with every category, as the linker
        /// gives it.</summary>
        private static DeviceLinks ChromaRowLinks() => new()
        {
            Device = ChromaRow,
            CatchAll = true,
            Lighting = PeripheralLinker.ChromaCategories.Select(Category).ToArray(),
        };

        /// <summary>A Razer mouse lit on its own, through the direct worker,
        /// stands in for the mouse category. While its row holds a claim
        /// the Chroma row's session leaves that category out, so the mouse
        /// is never painted from two sides. Every other category is wanted
        /// as before, and the mouse category comes back when the mouse's
        /// own claim ends.</summary>
        [Fact]
        public void ACategoryADeviceLitOnItsOwnStandsInFor_IsLeftOut_AndEveryOtherIsWantedAsBefore()
        {
            Link(ChromaRowLinks(),
                 new DeviceLinks { Device = Mouse, Lighting = new[] { MouseUnit }, Shadows = new[] { Category("mouse") } });

            // The Chroma row is lit and the mouse is not: every category,
            // the mouse's too.
            PeripheralOutputs.SetLighting(ChromaRow, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            Assert.Equal(PeripheralLinker.ChromaCategories, Wanted().Select(p => p.Key));

            // The mouse is lit on its own: its category is left out, and
            // the others keep the row's color.
            PeripheralOutputs.SetLighting(Mouse, slot: 3, player: 4, 0x00, 0xFF, 0x00);
            var wanted = Wanted();
            Assert.Equal(PeripheralLinker.ChromaCategories.Where(c => c != "mouse"), wanted.Select(p => p.Key));
            Assert.All(wanted, p => Assert.Equal(0xFF0000, p.Value));

            // Its claim ends: the row's session takes the category again.
            PeripheralOutputs.ReleaseLighting(Mouse, 3);
            Assert.Equal(PeripheralLinker.ChromaCategories, Wanted().Select(p => p.Key));

            // With the Chroma row dark too, nothing is wanted and no
            // session is held.
            PeripheralOutputs.ReleaseLighting(ChromaRow, 0);
            Assert.Empty(Wanted());
        }

        /// <summary>A shadow counts only while its row is painted through a
        /// path of its own: a device that is linked and dark shadows
        /// nothing, a row lit through the category itself rules it as
        /// before, and another family's shadow is no Chroma category.</summary>
        [Fact]
        public void AShadowCountsOnlyWhileItsRowIsPaintedThroughAPathOfItsOwn()
        {
            PeripheralOutputs.SetLighting(ChromaRow, slot: 0, player: 1, 0xFF, 0x00, 0x00);

            // Two Razer devices with units of their own, and only the
            // keyboard is lit: the mouse category stays the Chroma row's.
            Link(ChromaRowLinks(),
                 new DeviceLinks { Device = Mouse, Lighting = new[] { MouseUnit }, Shadows = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { KeyboardUnit }, Shadows = new[] { Category("keyboard") } });
            PeripheralOutputs.SetLighting(Keyboard, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            Assert.Equal(PeripheralLinker.ChromaCategories.Where(c => c != "keyboard"), Wanted().Select(p => p.Key));

            // Both lit: both categories are left out.
            PeripheralOutputs.SetLighting(Mouse, slot: 3, player: 4, 0x00, 0xFF, 0x00);
            Assert.Equal(PeripheralLinker.ChromaCategories.Where(c => c != "keyboard" && c != "mouse"), Wanted().Select(p => p.Key));

            // Synapse runs: the mouse row is lit through its category, the
            // shared path, and stands in for nothing. Its claim rules the
            // category as before.
            Link(ChromaRowLinks(),
                 new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") }, DirectWhenClosed = true });
            var wanted = Wanted().ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(PeripheralLinker.ChromaCategories.Length, wanted.Count);
            Assert.Equal(0x00FF00, wanted["mouse"]);
            Assert.Equal(0xFF0000, wanted["keyboard"]);

            // A row that named the shared path it is lit through would not
            // hide it either: only a path that reaches one device counts.
            Link(ChromaRowLinks(),
                 new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") }, Shadows = new[] { Category("mouse") } });
            Assert.Equal(0x00FF00, Wanted().Single(p => p.Key == "mouse").Value);

            // A Logitech mouse lit on its own shadows its LED SDK type,
            // which is no Chroma category.
            Link(ChromaRowLinks(), new DeviceLinks
            {
                Device = Mouse,
                Lighting = new[] { new OutputPath(OutputFamily.HidppUnit, "unit") },
                Shadows = new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") },
            });
            Assert.Equal(PeripheralLinker.ChromaCategories, Wanted().Select(p => p.Key));
        }

        /// <summary>Set Chroma Color paints a Razer device on its virtual
        /// controller with the Lighting switch off. Where that device is
        /// linked to a unit of its own the direct worker paints it for the
        /// macro's window, so the session leaves its category out for that
        /// window and paints the others in the macro's color. A macro on a
        /// controller that does not hold the device shadows nothing.</summary>
        [Fact]
        public void SetChromaColor_OnTheControllerOfADeviceLitOnItsOwn_LeavesItsCategoryToTheDirectWorker()
        {
            Link(ChromaRowLinks(),
                 new DeviceLinks { Device = Mouse, Lighting = new[] { MouseUnit }, Shadows = new[] { Category("mouse") } });
            // Controller 2 holds the mouse and the Chroma row, and
            // controller 4 the Chroma row alone.
            ISet<Guid> DevicesOn(int slot) => slot switch
            {
                2 => new HashSet<Guid> { Mouse, ChromaRow },
                4 => new HashSet<Guid> { ChromaRow },
                _ => new HashSet<Guid>(),
            };
            long now = 1_000_000;
            Assert.Empty(Wanted(DevicesOn, now));

            PeripheralOutputs.AssertChromaMacro(2, 0x0A, 0x0B, 0x0C, now);
            var wanted = Wanted(DevicesOn, now);
            Assert.Equal(PeripheralLinker.ChromaCategories.Where(c => c != "mouse"), wanted.Select(p => p.Key));
            Assert.All(wanted, p => Assert.Equal(0x0A0B0C, p.Value));

            // Through the window's last tick, and not past it.
            long last = now + PeripheralOutputs.MacroAssertWindowMs;
            Assert.DoesNotContain(Wanted(DevicesOn, last), p => p.Key == "mouse");
            Assert.Empty(Wanted(DevicesOn, last + 1));

            // The macro of a controller without the mouse: the direct
            // worker does not paint the mouse for it, so the session does.
            PeripheralOutputs.AssertChromaMacro(4, 0x01, 0x02, 0x03, last + 1);
            wanted = Wanted(DevicesOn, last + 1);
            Assert.Equal(PeripheralLinker.ChromaCategories, wanted.Select(p => p.Key));
            Assert.All(wanted, p => Assert.Equal(0x010203, p.Value));
        }

        // ─────────────────────────────────────────────
        //  The worker against a local Chroma REST server
        // ─────────────────────────────────────────────

        /// <summary>A local stand-in for Synapse's REST server: the init
        /// answers with a session URI, each category PUT with the result its
        /// script gives, and every request is kept with its arrival tick.</summary>
        private sealed class FakeChromaServer : IDisposable
        {
            private readonly HttpListener _listener;
            private volatile bool _running = true;
            public readonly ConcurrentQueue<(long Tick, string Method, string Path)> Requests = new();
            /// <summary>Each category PUT and the result it was answered with.</summary>
            public readonly ConcurrentQueue<(string Category, int Result)> Puts = new();
            /// <summary>Each category PUT's body, in order.</summary>
            public readonly ConcurrentQueue<(string Category, string Body)> PutBodies = new();
            public string Endpoint { get; }
            public volatile int InitStatus = 200;
            public volatile int HeartbeatStatus = 200;
            public Func<string, int> PutResult = _ => 0;

            public FakeChromaServer()
            {
                var rng = new Random();
                for (int attempt = 0; ; attempt++)
                {
                    int port = 20000 + rng.Next(20000);
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    try { l.Start(); }
                    catch (HttpListenerException) when (attempt < 10) { continue; }
                    _listener = l;
                    Endpoint = $"http://127.0.0.1:{port}";
                    break;
                }
                new Thread(Serve) { IsBackground = true }.Start();
            }

            private void Serve()
            {
                while (_running)
                {
                    HttpListenerContext ctx;
                    try { ctx = _listener.GetContext(); }
                    catch { break; }
                    string requestBody;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) requestBody = reader.ReadToEnd();
                    string path = ctx.Request.Url.AbsolutePath;
                    string method = ctx.Request.HttpMethod;
                    Requests.Enqueue((Environment.TickCount64, method, path));
                    int status = 200;
                    string body = "{\"result\":0}";
                    if (method == "POST" && path == "/razer/chromasdk")
                    {
                        status = InitStatus;
                        body = "{\"sessionid\":7,\"uri\":\"" + Endpoint + "/chromasdk\"}";
                    }
                    else if (method == "PUT" && path.StartsWith("/chromasdk/", StringComparison.Ordinal)
                             && path != "/chromasdk/heartbeat")
                    {
                        string category = path.Substring("/chromasdk/".Length);
                        int result = PutResult(category);
                        PutBodies.Enqueue((category, requestBody));
                        Puts.Enqueue((category, result));
                        body = "{\"result\":" + result + "}";
                    }
                    else if (method == "PUT" && path == "/chromasdk/heartbeat")
                    {
                        status = HeartbeatStatus;
                    }
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.OutputStream.Close();
                    }
                    catch { }
                }
            }

            public int Count(string method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }

        private static ChromaBackend Backend(FakeChromaServer server, int retryMs = 30000, int pollMs = 20,
            Func<int, ISet<Guid>> devicesOnSlot = null, int heartbeatMs = 1000, Func<long> clock = null)
            => new(server.Endpoint, heartbeatMs, retryMs, pollMs, 2000, devicesOnSlot ?? (_ => new HashSet<Guid>()), slot => slot + 1,
                clock);

        private static int Lines(string text) => SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains(text));

        /// <summary>Two categories refused on every poll log one line each,
        /// not a pair per poll.</summary>
        [Fact]
        public void TwoRefusedCategories_LogOnceEach()
        {
            using var server = new FakeChromaServer { PutResult = _ => 87 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            int keyboard = Lines("PERIPHERAL Chroma effect rejected: keyboard"), mouse = Lines("PERIPHERAL Chroma effect rejected: mouse");
            using var backend = Backend(server);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 4, 5, 6);

            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 6, 30000), "the refused color was retried");
            Assert.Equal(keyboard + 1, Lines("PERIPHERAL Chroma effect rejected: keyboard"));
            Assert.Equal(mouse + 1, Lines("PERIPHERAL Chroma effect rejected: mouse"));
        }

        /// <summary>A category refused again after a color was accepted there
        /// logs again.</summary>
        [Fact]
        public void ARefusalAfterAnAcceptedColor_IsLoggedAgain()
        {
            int refuse = 1;
            using var server = new FakeChromaServer { PutResult = _ => Volatile.Read(ref refuse) != 0 ? 87 : 0 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma effect rejected: mouse";
            int lines = Lines(line);
            using var backend = Backend(server);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 3, 30000), "the refused color was retried");
            Assert.Equal(lines + 1, Lines(line));

            Volatile.Write(ref refuse, 0);
            Assert.True(SpinWait.SpinUntil(() => server.Puts.Any(p => p.Category == "mouse" && p.Result == 0), 3000),
                "the color was accepted");
            Volatile.Write(ref refuse, 1);
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 7, 8, 9);
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 2, 3000), "the refusal after the accepted color was logged");
        }

        /// <summary>A category refused again in a new session logs again.</summary>
        [Fact]
        public void ARefusalInANewSession_IsLoggedAgain()
        {
            using var server = new FakeChromaServer { PutResult = _ => 87 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma effect rejected: mouse";
            int lines = Lines(line);
            using var backend = Backend(server);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 3, 30000), "the refused color was retried");
            Assert.Equal(lines + 1, Lines(line));

            // The claim goes and its session ends. The next claim opens a
            // new one.
            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            Assert.True(SpinWait.SpinUntil(() => server.Count("DELETE", "/chromasdk") >= 1, 3000), "the session ended");
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 2, 3000), "the refusal in the new session was logged");
        }

        /// <summary>A Synapse that keeps refusing the session logs the wait
        /// once, not at every retry.</summary>
        [Fact]
        public void ARefusedSession_LogsTheWaitOnce()
        {
            using var server = new FakeChromaServer { InitStatus = 500 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma waiting for Synapse";
            int lines = Lines(line);
            using var backend = Backend(server, retryMs: 100);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            Assert.True(SpinWait.SpinUntil(() => server.Count("POST", "/razer/chromasdk") >= 4, 30000), "the session was retried");
            Assert.Equal(lines + 1, Lines(line));
        }

        /// <summary>Synapse runs and refuses the session: the tab reads that,
        /// not a wait for Synapse to start. A session that opens clears it.</summary>
        [Fact]
        public void ASessionRefused_WhileSynapseRuns_IsARefusal_AndASessionClearsIt()
        {
            using var server = new FakeChromaServer { InitStatus = 500 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            PeripheralOutputs.Presence = PeripheralPresence.None with { RazerSoftwareRunning = true };
            using var backend = Backend(server, retryMs: 50);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => PeripheralOutputs.RefusalOn(Category("mouse")) != null, 5000), "the refusal was recorded");
            server.InitStatus = 200;
            Assert.True(SpinWait.SpinUntil(() => PeripheralOutputs.StateOf(OutputFamily.ChromaCategory) == BackendState.Connected, 5000));
            Assert.Null(PeripheralOutputs.RefusalOn(Category("mouse")));
        }

        /// <summary>With Synapse not running, the wait stands.</summary>
        [Fact]
        public void ASessionRefused_WhileSynapseDoesNotRun_IsNoRefusal()
        {
            using var server = new FakeChromaServer { InitStatus = 500 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            using var backend = Backend(server, retryMs: 50);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => server.Count("POST", "/razer/chromasdk") >= 2, 5000), "the session was tried");
            Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.ChromaCategory));
            Assert.Null(PeripheralOutputs.RefusalOn(Category("mouse")));
        }

        /// <summary>The wait is logged again once the worker has moved on:
        /// after a spell with nothing claimed, and after a session that
        /// opened was lost.</summary>
        [Fact]
        public void ARefusedSession_LogsTheWaitAgain_AfterTheWorkerMovedOn()
        {
            using var server = new FakeChromaServer { InitStatus = 500 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            const string line = "PERIPHERAL Chroma waiting for Synapse";
            int lines = Lines(line);
            using var backend = Backend(server, retryMs: 100, heartbeatMs: 50);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => server.Count("POST", "/razer/chromasdk") >= 2, 30000), "the session was retried");
            Assert.Equal(lines + 1, Lines(line));

            // Nothing claimed, then claimed again: a new wait.
            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.ChromaCategory) == BackendState.Idle, 3000), "the worker went idle");
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 2, 3000), "the wait after the idle spell was logged");

            // A session opens, is lost, and the next one is refused: a new
            // wait.
            server.InitStatus = 200;
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.ChromaCategory) == BackendState.Connected, 3000), "the session opened");
            server.InitStatus = 500;
            server.HeartbeatStatus = 500;
            Assert.True(SpinWait.SpinUntil(() => Lines(line) == lines + 3, 3000), "the wait after the lost session was logged");
        }

        /// <summary>Set Chroma Color lets go within its window of the action
        /// ending: the worker's wait ends at the lapse, so the session ends
        /// then, not at the next poll. The worker reads this test's clock and
        /// its poll is ten minutes, so the test reads the wait the worker
        /// chose and times nothing, and a session that ends here ended on
        /// the lapse. A bound on the time the release took failed whenever
        /// the worker was held off.</summary>
        [Fact]
        public void SetChromaColor_LetsGoAtItsLapse_NotAtTheNextPoll()
        {
            using var server = new FakeChromaServer();
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            long now = 1_000_000;
            using var backend = Backend(server, retryMs: 0, pollMs: 600_000, devicesOnSlot: slot => slot == 0
                ? new HashSet<Guid> { Mouse } : new HashSet<Guid>(), clock: () => Volatile.Read(ref now));
            backend.Start();

            PeripheralOutputs.AssertChromaMacro(0, 0x10, 0x20, 0x30, now);
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 1, 30000), "the macro color was painted");
            // With the macro live the worker waits for its lapse, the first
            // tick past the window, and with the window's last tick left it
            // waits that tick.
            Assert.Equal(PeripheralOutputs.MacroAssertWindowMs + 1,
                WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));
            Volatile.Write(ref now, now + PeripheralOutputs.MacroAssertWindowMs);
            Assert.Equal(1, WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));
            Assert.Equal(0, server.Count("DELETE", "/chromasdk"));

            Volatile.Write(ref now, now + 1);
            Assert.True(SpinWait.SpinUntil(() => server.Count("DELETE", "/chromasdk") >= 1, 30000), "the session ended at the lapse");
        }

        /// <summary>A macro that lapses while its color is on the way to
        /// Synapse is let go on the next tick. The worker took the lapse from
        /// the tick the send ended at, found no macro live by then, and held
        /// the color for a whole poll.</summary>
        [Fact]
        public void SetChromaColor_ThatLapsesWhileItsColorIsSent_IsLetGoOnTheNextTick()
        {
            long now = 1_000_000;
            long lapse = now + PeripheralOutputs.MacroAssertWindowMs + 1;
            // The action ends while Synapse takes the color.
            using var server = new FakeChromaServer { PutResult = _ => { Volatile.Write(ref now, lapse); return 0; } };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Category("mouse") } });
            using var backend = Backend(server, retryMs: 0, pollMs: 600_000, devicesOnSlot: slot => slot == 0
                ? new HashSet<Guid> { Mouse } : new HashSet<Guid>(), clock: () => Volatile.Read(ref now));
            backend.Start();
            // The worker is in its idle wait before the macro starts, so the
            // macro's wake ends that wait and none is left over to end the
            // wait after the color.
            Assert.True(SpinWait.SpinUntil(() => backend.WaitMs == 600_000, 30000), "the worker went idle");

            PeripheralOutputs.AssertChromaMacro(0, 0x10, 0x20, 0x30, now);
            Assert.True(SpinWait.SpinUntil(() => server.Count("DELETE", "/chromasdk") >= 1, 30000),
                "the session ended without a poll passing");
            Assert.True(server.Count("PUT", "/chromasdk/mouse") >= 1, "the macro color was painted first");
        }

        /// <summary>The worker beside a mouse lit on its own (#494): its
        /// session paints the keyboard category for the Razer Chroma row
        /// and never the mouse's. When the mouse's own claim ends, the
        /// session is opened again with the mouse category and paints it.</summary>
        [Fact]
        public void TheSession_NeverPaintsTheCategoryOfADeviceLitOnItsOwn()
        {
            using var server = new FakeChromaServer();
            Link(new DeviceLinks { Device = ChromaRow, CatchAll = true, Lighting = new[] { Category("keyboard"), Category("mouse") } },
                 new DeviceLinks { Device = Mouse, Lighting = new[] { MouseUnit }, Shadows = new[] { Category("mouse") } });
            PeripheralOutputs.SetLighting(ChromaRow, slot: 0, player: 1, 4, 5, 6);
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);
            using var backend = Backend(server);
            backend.Start();
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/keyboard") >= 1, 30000), "the keyboard category was painted");
            // A pass that wanted the mouse category would have painted it
            // right behind the keyboard's. A whole pass more has run by the
            // time this is read.
            WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs);
            Assert.Equal(0, server.Count("PUT", "/chromasdk/mouse"));
            Assert.Equal(1, server.Count("POST", "/razer/chromasdk"));

            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            Assert.True(SpinWait.SpinUntil(() => server.Count("PUT", "/chromasdk/mouse") >= 1, 30000), "the mouse category came back");
            Assert.Equal(2, server.Count("POST", "/razer/chromasdk"));
        }

        // ─────────────────────────────────────────────
        //  Light by light (#557)
        // ─────────────────────────────────────────────

        private const int Red = 0xFF0000, Blue = 0x0000FF, Green = 0x00FF00;

        /// <summary>BGR of each, R + (G &lt;&lt; 8) + (B &lt;&lt; 16)
        /// (REST keyboard page, "Color value in BGR format").</summary>
        private const int RedBgr = 255, BlueBgr = 16711680, GreenBgr = 65280;

        /// <summary>The flag of a key effect: "a bit mask of 0x01000000
        /// (16777216)" (REST keyboard page, CHROMA_CUSTOM_KEY).</summary>
        private const int Flag = 16777216;

        private static KeyFrame Keys(params (ushort Usage, int Rgb)[] keys)
            => new(keys.ToDictionary(k => k.Usage, k => k.Rgb), null, null, null);

        private static int[][] Grid(JsonElement grid) => grid.EnumerateArray()
            .Select(row => row.EnumerateArray().Select(c => c.GetInt32()).ToArray()).ToArray();

        /// <summary>Without light colors, or on a category of one light, the
        /// body is CHROMA_STATIC exactly as the worker sent it before #557.</summary>
        [Fact]
        public void WithoutLightColors_TheEffectIsTheStaticBodyOfOld()
        {
            const string old = "{\"effect\":\"CHROMA_STATIC\",\"param\":{\"color\":255}}";
            foreach (var category in PeripheralLinker.ChromaCategories)
            {
                Assert.Equal(old, ChromaLayouts.EffectBody(category, Red, null));
                Assert.Equal(old, ChromaLayouts.EffectBody(category, Red, KeyFrame.Empty));
            }
            Assert.Equal(old, ChromaLayouts.EffectBody("unknown", Red, Keys((0x1A, Blue))));
        }

        /// <summary>
        /// W in red over a blue claim. CHROMA_CUSTOM_KEY carries two 6 by 22
        /// grids. The canvas has W's cell, RZKEY_W 0x0203 (row 2, column 3,
        /// RzChromaSDKTypes.h:241), red and every other cell blue. The key
        /// array carries the flag at every RZKEY cell, with red at W and blue
        /// elsewhere, and 0 at every cell no RZKEY names, the logo's
        /// RZLED_LOGO 0x0014 among them.
        /// </summary>
        [Fact]
        public void AKeyboardFrame_IsCustomKey_WithTheCanvasAndTheFlaggedKeys()
        {
            using var doc = JsonDocument.Parse(ChromaLayouts.EffectBody("keyboard", Blue, Keys((0x1A, Red))));
            var root = doc.RootElement;
            Assert.Equal("CHROMA_CUSTOM_KEY", root.GetProperty("effect").GetString());
            var color = Grid(root.GetProperty("param").GetProperty("color"));
            var key = Grid(root.GetProperty("param").GetProperty("key"));
            Assert.Equal(6, color.Length);
            Assert.All(color, row => Assert.Equal(22, row.Length));
            Assert.Equal(6, key.Length);
            Assert.All(key, row => Assert.Equal(22, row.Length));

            var rzkeyCells = SdkLightMapsTests.Pairs(SdkLightMapsTests.RgbNetRazerCells).Keys
                .Concat(SdkLightMapsTests.Pairs(SdkLightMapsTests.ChromaJapaneseCells).Keys)
                .Concat(new[] { 0x0100, 0x0200, 0x0300, 0x0400, 0x0500, 0x050C })
                .ToHashSet();
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 22; c++)
                {
                    int code = (r << 8) | c;
                    bool w = code == 0x0203;
                    Assert.Equal(w ? RedBgr : BlueBgr, color[r][c]);
                    Assert.Equal(rzkeyCells.Contains(code) ? Flag | (w ? RedBgr : BlueBgr) : 0, key[r][c]);
                }
            Assert.Equal(0, key[0][20]);
        }

        /// <summary>The logo, the media keys and a group reach their canvas
        /// cells: the logo group at RZLED_LOGO's cell, play and pause at row
        /// 0, column 19.</summary>
        [Fact]
        public void TheKeyboardLogoAndMediaKeys_RideTheCanvas()
        {
            var frame = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Logo] = Green, [LightGroups.Other] = Red },
                null, null);
            using var doc = JsonDocument.Parse(ChromaLayouts.EffectBody("keyboard", Blue, frame));
            var color = Grid(doc.RootElement.GetProperty("param").GetProperty("color"));
            var key = Grid(doc.RootElement.GetProperty("param").GetProperty("key"));
            Assert.Equal(GreenBgr, color[0][20]);
            Assert.Equal(RedBgr, color[0][18]);
            Assert.Equal(RedBgr, color[0][19]);
            Assert.Equal(0, key[0][19]);
            // Fn and the macro keys are "other" lights with RZKEY codes.
            Assert.Equal(Flag | RedBgr, key[5][12]);
            Assert.Equal(Flag | RedBgr, key[1][0]);
            Assert.Equal(BlueBgr, color[0][0]);
        }

        /// <summary>The mouse takes CHROMA_CUSTOM2 on 9 by 7 (REST mouse
        /// page): the logo group at RZLED2_LOGO 0x0703, zone 1 at the wheel
        /// RZLED2_SCROLLWHEEL 0x0203, and the background everywhere else.</summary>
        [Fact]
        public void AMouseFrame_IsCustom2_OnTheNineBySevenGrid()
        {
            var frame = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Logo] = Red, [LightGroups.Zone(1)] = Green },
                null, null);
            using var doc = JsonDocument.Parse(ChromaLayouts.EffectBody("mouse", Blue, frame));
            Assert.Equal("CHROMA_CUSTOM2", doc.RootElement.GetProperty("effect").GetString());
            var grid = Grid(doc.RootElement.GetProperty("param"));
            Assert.Equal(9, grid.Length);
            Assert.All(grid, row => Assert.Equal(7, row.Length));
            for (int r = 0; r < 9; r++)
                for (int c = 0; c < 7; c++)
                    Assert.Equal((r, c) switch { (7, 3) => RedBgr, (2, 3) => GreenBgr, _ => BlueBgr }, grid[r][c]);
        }

        /// <summary>The headset's 5, the mousepad's 15 and Chroma Link's 5
        /// take CHROMA_CUSTOM as a flat array, the keypad as 4 rows of 5
        /// (REST headset, keypad and Chroma Link pages, Colore's
        /// *CustomConverter.cs).</summary>
        [Theory]
        [InlineData("headset", 5)]
        [InlineData("mousepad", 15)]
        [InlineData("chromalink", 5)]
        public void ALedArrayFrame_IsCustom_OneColorPerLed(string category, int leds)
        {
            var frame = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Zone(2)] = Red }, null, null);
            using var doc = JsonDocument.Parse(ChromaLayouts.EffectBody(category, Blue, frame));
            Assert.Equal("CHROMA_CUSTOM", doc.RootElement.GetProperty("effect").GetString());
            var colors = doc.RootElement.GetProperty("param").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(Enumerable.Range(0, leds).Select(i => i == 1 ? RedBgr : BlueBgr), colors);
        }

        [Fact]
        public void AKeypadFrame_IsCustom_OnFourRowsOfFive()
        {
            var frame = new KeyFrame(null, new Dictionary<string, int> { [LightGroups.Zone(7)] = Red }, null, Green);
            using var doc = JsonDocument.Parse(ChromaLayouts.EffectBody("keypad", Blue, frame));
            Assert.Equal("CHROMA_CUSTOM", doc.RootElement.GetProperty("effect").GetString());
            var grid = Grid(doc.RootElement.GetProperty("param"));
            Assert.Equal(4, grid.Length);
            Assert.All(grid, row => Assert.Equal(5, row.Length));
            // Zone 7 is the seventh cell, row 1, column 1. The frame's own
            // background replaces the claim's color everywhere else.
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 5; c++)
                    Assert.Equal(r == 1 && c == 1 ? RedBgr : GreenBgr, grid[r][c]);
        }

        /// <summary>The claim's light colors reach the worker's want, and a
        /// Set Chroma Color carries none.</summary>
        [Fact]
        public void TheWantCarriesTheClaimsLightColors_AndAMacroNone()
        {
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            var frame = Keys((0x1A, Red));
            PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0, 0, 0xFF, keys: frame);
            var want = Assert.Single(Wants());
            Assert.Equal(("keyboard", Blue), (want.Category, want.Rgb));
            Assert.Equal(frame, want.Keys);

            long now = 1_000_000;
            PeripheralOutputs.AssertChromaMacro(0, 1, 2, 3, now);
            var macro = Assert.Single(Wants(slot => slot == 0 ? new HashSet<Guid> { Keyboard } : new HashSet<Guid>(), now));
            Assert.Equal(0x010203, macro.Rgb);
            Assert.Null(macro.Keys);
        }

        /// <summary>The worker paints a claim with light colors through its
        /// custom effect, does not send the same picture twice, sends a new
        /// one, and goes back to CHROMA_STATIC when the light colors go. The
        /// lights of every category are published while it runs and taken
        /// down when it stops.</summary>
        [Fact]
        public void TheWorker_SendsTheCustomEffect_OnceAPicture_AndStaticWithoutLightColors()
        {
            using var server = new FakeChromaServer();
            Link(new DeviceLinks { Device = Keyboard, Lighting = new[] { Category("keyboard") } });
            var backend = Backend(server);
            try
            {
                backend.Start();
                foreach (var category in PeripheralLinker.ChromaCategories)
                    Assert.Same(ChromaLayouts.MapOf(category), PeripheralLightMaps.For(Category(category)));

                string Bodies() => string.Join("\n", server.PutBodies.Where(p => p.Category == "keyboard").Select(p => p.Body));
                int Count() => server.PutBodies.Count(p => p.Category == "keyboard");

                PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0, 0, 0xFF, keys: Keys((0x1A, Red)));
                Assert.True(SpinWait.SpinUntil(() => Count() >= 1, 30000), "the keyboard was painted");
                Assert.Equal(ChromaLayouts.EffectBody("keyboard", Blue, Keys((0x1A, Red))), server.PutBodies.Last().Body);

                // The same picture from a new but equal frame: nothing more.
                PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0, 0, 0xFF, keys: Keys((0x1A, Red)));
                WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs);
                Assert.Equal(1, Count());

                PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0, 0, 0xFF, keys: Keys((0x1A, Green)));
                Assert.True(SpinWait.SpinUntil(() => Count() >= 2, 30000), "the new picture went out");
                Assert.Equal(ChromaLayouts.EffectBody("keyboard", Blue, Keys((0x1A, Green))), server.PutBodies.Last().Body);

                PeripheralOutputs.SetLighting(Keyboard, slot: 0, player: 1, 0, 0, 0xFF);
                Assert.True(SpinWait.SpinUntil(() => Count() >= 3, 30000), "the one color went out");
                Assert.Equal("{\"effect\":\"CHROMA_STATIC\",\"param\":{\"color\":16711680}}", server.PutBodies.Last().Body);
                Assert.DoesNotContain("CHROMA_STATIC", Bodies().Split('\n')[0]);
            }
            finally
            {
                backend.Dispose();
            }
            foreach (var category in PeripheralLinker.ChromaCategories)
                Assert.Null(PeripheralLightMaps.For(Category(category)));
        }
    }
}
