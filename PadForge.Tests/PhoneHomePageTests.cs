using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The phone's home page (#574, board WebController): the layout this
    /// phone used last is one button, four common layouts sit under it, and
    /// the rest open under More Layouts. Every card of the old grid is a tile
    /// that opens the page it opened, every finish dot is a finish the server
    /// draws, and the page's script stores and reads its pick with care. The
    /// script's pure functions run in Jint, as the touchpad page's do.
    /// </summary>
    public class PhoneHomePageTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Web(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "PadForge.App", "WebAssets" }.Concat(parts).ToArray()));

        // The data-key values of one grid, in order. A tile holds no div, so the
        // grid's markup ends at the first closing div.
        private static string[] Keys(string html, string gridId)
        {
            var grid = Regex.Match(html, "<div id=\"" + gridId + "\"[^>]*>(.*?)</div>", RegexOptions.Singleline);
            Assert.True(grid.Success, gridId + " is missing");
            return Regex.Matches(grid.Groups[1].Value, "data-key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        }

        // The server's layouts and their finishes, read the way
        // BrowserGamepadForwardingTests reads _targetInputMap.
        private static Dictionary<string, string[]> LayoutDefs()
        {
            var field = typeof(WebControllerServer).GetField("LayoutDefs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var served = new Dictionary<string, string[]>();
            foreach (object def in (Array)field.GetValue(null))
                served[(string)def.GetType().GetField("TypeKey").GetValue(def)] = (string[])def.GetType().GetField("Finishes").GetValue(def);
            return served;
        }

        // The page script with its start line swapped for an export, run in Jint
        // as TouchpadConnectionLifetimeTests runs touchpad.html.
        private const string StartLine = "    document.addEventListener(\"DOMContentLoaded\", start);";
        private const string ExportLine = "    window.homeTest = { FINISHES: FINISHES, knownFinish: knownFinish, continueHref: continueHref, parseLast: parseLast, lastRecord: lastRecord, summaryText: summaryText };";

        private static Jint.Engine PageScript()
        {
            string source = Web("js", "home_client.js");
            Assert.Single(Regex.Matches(source, Regex.Escape(StartLine)));
            var engine = new Jint.Engine(o => { o.TimeoutInterval(TimeSpan.FromSeconds(5)); o.MaxStatements(100_000); });
            engine.Execute("var window = {}; var document = {};");
            engine.Execute(source.Replace(StartLine, ExportLine), "home_client.js");
            return engine;
        }

        private static string Js(Jint.Engine engine, string expression) => engine.Evaluate(expression).ToString();

        // A CSS rule's body. The newline and the brace keep ".go" off ".go-name".
        private static string Rule(string html, string selector)
        {
            var m = Regex.Match(html, @"\n\s*" + Regex.Escape(selector) + @"\s*\{([^}]*)\}");
            Assert.True(m.Success, selector + " has no rule");
            return m.Groups[1].Value;
        }

        [Fact]
        public void TheFourCommonLayoutsComeFirstAndTheRestFoldUnderMore()
        {
            string html = Web("index.html");
            Assert.Equal(new[] { "xboxseries", "dualsense", "switchpro", "touchpad" }, Keys(html, "layoutGrid"));
            Assert.Equal(new[] { "xbox360", "xboxone", "ds4", "dualsenseedge", "switch2pro", "steamdeck", "steamcontroller", "gamepad", "menus", "build" },
                Keys(html, "moreGrid"));
            Assert.Matches("<div id=\"moreGrid\" class=\"grid\" hidden>", html);
        }

        [Fact]
        public void EveryTileOpensThePageItsCardOpened()
        {
            string html = Web("index.html");
            var pairs = Regex.Matches(html, "data-key=\"([^\"]+)\" data-href=\"([^\"]+)\"")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            foreach (string layout in new[] { "xboxseries", "dualsense", "switchpro", "xbox360", "xboxone", "ds4", "dualsenseedge", "switch2pro", "steamdeck", "steamcontroller" })
                Assert.Equal("/controller.html?layout=" + layout, pairs[layout]);
            Assert.Equal("/touchpad.html", pairs["touchpad"]);
            Assert.Equal("/gamepad.html", pairs["gamepad"]);
            Assert.Equal("/menus.html", pairs["menus"]);
            Assert.Equal("/custom.html?id=new", pairs["build"]);
            Assert.Equal(14, pairs.Count);
        }

        [Fact]
        public void EveryControllerTileIsALayoutTheServerDraws()
        {
            string html = Web("index.html");
            var served = LayoutDefs();
            var tiles = Regex.Matches(html, "data-key=\"([^\"]+)\" data-href=\"/controller.html\\?layout=([^\"]+)\">\\s*<span class=\"preview\" data-layout=\"([^\"]+)\"");
            Assert.Equal(10, tiles.Count);
            foreach (Match tile in tiles)
            {
                Assert.Equal(tile.Groups[1].Value, tile.Groups[2].Value);
                Assert.Equal(tile.Groups[1].Value, tile.Groups[3].Value);
                Assert.True(served.ContainsKey(tile.Groups[1].Value), tile.Groups[1].Value + " is no layout the server draws");
            }
        }

        [Fact]
        public void TheFinishDotsAreTheFinishesTheServerDraws()
        {
            var served = LayoutDefs();
            // Positive control: four layouts have finishes of their own.
            Assert.Equal(4, served.Count(s => s.Value.Length > 0));
            var finishes = JsonDocument.Parse(Js(PageScript(), "JSON.stringify(window.homeTest.FINISHES)")).RootElement;
            foreach (var (key, keys) in served)
            {
                if (keys.Length == 0)
                {
                    Assert.False(finishes.TryGetProperty(key, out _), key + " has dots but the server draws no finish of it");
                    continue;
                }
                Assert.True(finishes.TryGetProperty(key, out var dots), key + " has no dots");
                var rows = dots.EnumerateArray().ToList();
                Assert.Equal(new[] { "" }.Concat(keys).ToArray(), rows.Select(r => r[0].GetString()).ToArray());
                foreach (var row in rows)
                {
                    Assert.False(string.IsNullOrEmpty(row[1].GetString()));
                    Assert.False(string.IsNullOrEmpty(row[2].GetString()));
                }
            }
            foreach (var layout in finishes.EnumerateObject())
                Assert.True(served.ContainsKey(layout.Name), layout.Name + " is no layout the server draws");
        }

        [Fact]
        public void ContinueGoesWhereTheCardsWent()
        {
            var page = PageScript();
            string Href(string href, string key, string finish)
                => Js(page, $"window.homeTest.continueHref({JsonSerializer.Serialize(href)}, {JsonSerializer.Serialize(key)}, {JsonSerializer.Serialize(finish)})");
            Assert.Equal("/controller.html?layout=xboxseries&finish=Carbon", Href("/controller.html?layout=xboxseries", "xboxseries", "Carbon"));
            Assert.Equal("/controller.html?layout=xboxseries", Href("/controller.html?layout=xboxseries", "xboxseries", ""));
            Assert.Equal("/controller.html?layout=xboxseries", Href("/controller.html?layout=xboxseries", "xboxseries", "Gold"));
            Assert.Equal("/controller.html?layout=xbox360&finish=WLG", Href("/controller.html?layout=xbox360", "xbox360", "WLG"));
            Assert.Equal("/touchpad.html", Href("/touchpad.html", "touchpad", "WLG"));
            Assert.Equal("/custom.html?id=a1b2", Href("/custom.html?id=a1b2", "custom:a1b2", ""));
        }

        [Fact]
        public void TheStoredPickIsReadWithCare()
        {
            var page = PageScript();
            foreach (string junk in new[] { "null", "\"\"", "\"junk\"", "\"[1]\"", "'{\"key\":\"\"}'" })
                Assert.Equal("null", Js(page, "JSON.stringify(window.homeTest.parseLast(" + junk + "))"));
            Assert.Equal("{\"key\":\"ds4\",\"finish\":\"Gold\",\"name\":\"DualShock 4\"}",
                Js(page, "JSON.stringify(window.homeTest.parseLast('{\"key\":\"ds4\",\"finish\":\"Gold\",\"name\":\"DualShock 4\"}'))"));
            Assert.Equal("{\"key\":\"ds4\",\"finish\":\"\",\"name\":\"\"}",
                Js(page, "JSON.stringify(window.homeTest.parseLast('{\"key\":\"ds4\",\"finish\":7,\"name\":5}'))"));
            Assert.Equal("null", Js(page, "JSON.stringify(window.homeTest.lastRecord(\"build\", \"\", \"Build Your Own\"))"));
            Assert.Equal("{\"key\":\"xboxseries\",\"finish\":\"\",\"name\":\"Xbox Series X|S\"}",
                Js(page, "window.homeTest.lastRecord(\"xboxseries\", \"Bogus\", \"Xbox Series X|S\")"));
            Assert.Equal("{\"key\":\"ds4\",\"finish\":\"Gold\",\"name\":\"DualShock 4\"}",
                Js(page, "window.homeTest.lastRecord(\"ds4\", \"Gold\", \"DualShock 4\")"));
            Assert.Single(Regex.Matches(Web("js", "home_client.js"), Regex.Escape("localStorage.setItem(LAST_KEY")));
        }

        [Fact]
        public void TheMoreLineNamesTheFirstFourAndCountsTheRest()
        {
            string html = Web("index.html");
            var more = Regex.Match(html, "<div id=\"moreGrid\"[^>]*>(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;
            var names = Regex.Matches(more, "<span class=\"name\">([^<]+)</span>").Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(10, names.Length);
            var page = PageScript();
            string line = Js(page, "window.homeTest.summaryText(" + JsonSerializer.Serialize(names) + ")");
            Assert.Equal("Xbox 360, Xbox One, DualShock 4, DualSense Edge and 6 more", line);
            Assert.Equal("A, B", Js(page, "window.homeTest.summaryText([\"A\", \"B\"])"));
            Assert.Contains("id=\"moreSummary\" class=\"more-summary\">" + line + "</span>", html);
        }

        [Fact]
        public void TheNameFieldKeepsItsRules()
        {
            string html = Web("index.html");
            var field = Regex.Match(html, "<input id=\"viewerName\"[^>]*>").Value;
            Assert.Contains("maxlength=\"" + WebControllerServer.MaxViewerNameLength + "\"", field);
            Assert.Contains("autocomplete=\"nickname\"", field);
            Assert.Contains("placeholder=\"Shown to the streamer\"", field);
            string home = Web("js", "home_client.js");
            Assert.Contains("var NAME_KEY = \"padforge_viewer_name\";", home);
            Assert.Contains("localStorage.setItem(NAME_KEY, input.value.trim())", home);
        }

        [Fact]
        public void ThePageShowsTheAppsOwnIcon()
        {
            Assert.Equal("Resources/PadForge-icon.png", WebControllerServer.HomeMarkPath);
            Assert.Contains("src=\"/img/Resources/PadForge-icon.png\"", Web("index.html"));
            string server = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "WebControllerServer.cs"));
            int load = server.IndexOf("private static Dictionary<string, byte[]> LoadImageCache()", StringComparison.Ordinal);
            int end = server.IndexOf("return cache;", load, StringComparison.Ordinal);
            Assert.True(load > 0 && end > load);
            Assert.Contains("Load(HomeMarkPath);", server.Substring(load, end - load));
            Assert.Contains("<Resource Include=\"Resources\\PadForge-icon.png\" />",
                File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "PadForge.App.csproj")));
        }

        [Fact]
        public void ThePageDrawsEachLayoutWithTheSharedView()
        {
            string html = Web("index.html");
            int view = html.IndexOf("<script src=\"/js/layout_view.js\"></script>", StringComparison.Ordinal);
            int home = html.IndexOf("<script src=\"/js/home_client.js\"></script>", StringComparison.Ordinal);
            Assert.True(view > 0 && home > view, "the page does not load the shared view before its own script");
            string script = Web("js", "home_client.js");
            Assert.Contains("PadForgeLayoutView.fetchLayout(key, finish, function (layout) {", script);
            Assert.Contains("if (box.getAttribute(\"data-want\") !== want) return;", script);
            Assert.Contains("PadForgeLayoutView.draw(pad, layout);", script);
            Assert.Contains("new ResizeObserver(refitAll)", script);
        }

        [Fact]
        public void EveryTouchTargetIsAtLeast44Pixels()
        {
            string html = Web("index.html");
            Assert.Contains("width: 44px;", Rule(html, ".dotb"));
            Assert.Contains("height: 44px;", Rule(html, ".dotb"));
            Assert.Contains("min-height: 64px;", Rule(html, ".go"));
            Assert.Contains("min-height: 118px;", Rule(html, ".lay"));
            Assert.Contains("min-height: 52px;", Rule(html, ".more"));
            Assert.Contains("height: 46px;", Rule(html, ".field input"));
        }
    }
}
