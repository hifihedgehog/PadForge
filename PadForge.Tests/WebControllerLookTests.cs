using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The web controller's pages wear the program's own look (#574). The
    /// phone's home page was redesigned with PadForge 6 and the pages it
    /// opens were not: the page that plays, the browser gamepad, the custom
    /// pad, the touchpad, the stream picture and the menus kept the navy
    /// ground and the orange and blue of 5.x (owner, 2026-11-14). Every page
    /// now takes its colors from one sheet, css/padforge.css, whose values
    /// are the program's dark brushes, and the parts the pages share (the
    /// bar, the link's state, the small buttons, the covering messages) are
    /// that sheet's.
    /// </summary>
    public class WebControllerLookTests
    {
        private static string Web(params string[] parts)
            => ExtrasPageTests.RepoText(new[] { "PadForge.App", "WebAssets" }.Concat(parts).ToArray());

        private static readonly string[] Pages =
        {
            "index.html", "controller.html", "gamepad.html", "custom.html", "touchpad.html", "display.html", "menus.html",
        };

        private static readonly string[] Scripts =
        {
            "controller_client.js", "custom_client.js", "display_client.js", "fullscreen.js", "gamepad_client.js",
            "home_client.js", "layout_view.js", "pad_fit.js",
        };

        // The 5.x pages' colors: their navy grounds, their steel blue edges,
        // their red, green, orange and blue.
        private static readonly string[] OldColors =
        {
            "#1a1a2e", "#16213e", "#0f3460", "#e94560", "#101020", "#4ade80", "#ff6600", "#0066ff", "#1f5cae",
            "#0b0e14", "#111623", "#1b2333", "#ffa24d", "#c43d0c",
        };

        [Fact]
        public void EveryPageTakesItsColorsFromTheOneSheet()
        {
            foreach (string page in Pages)
            {
                string html = Web(page);
                Assert.Contains("<link rel=\"stylesheet\" href=\"/css/padforge.css\">", html);
                foreach (string old in OldColors)
                    Assert.False(html.Contains(old, StringComparison.OrdinalIgnoreCase), page + " still holds " + old);
                // The sheet comes before the page's own rules, which use it.
                int sheet = html.IndexOf("/css/padforge.css", StringComparison.Ordinal);
                int own = html.IndexOf("<style>", StringComparison.Ordinal);
                if (own >= 0) Assert.True(sheet < own, page + " links the sheet after its own rules");
                // The browser's own bars take the ground's color. The stream
                // picture has no ground, so it names none.
                Assert.Equal(page != "display.html", html.Contains("<meta name=\"theme-color\" content=\"#0a0e15\">"));
            }
            foreach (string sheet in new[] { "controller.css", "layout_view.css", "padforge.css" })
                foreach (string old in OldColors)
                    Assert.False(Web("css", sheet).Contains(old, StringComparison.OrdinalIgnoreCase), sheet + " still holds " + old);
            foreach (string script in Scripts)
                foreach (string old in OldColors)
                    Assert.False(Web("js", script).Contains(old, StringComparison.OrdinalIgnoreCase), script + " still holds " + old);

            // A page defines no color of its own that the sheet defines: one
            // place holds each. The home page's own list is gone.
            var shared = Regex.Matches(Web("css", "padforge.css"), @"(--[a-z0-9-]+)\s*:").Select(m => m.Groups[1].Value).ToHashSet();
            Assert.True(shared.Count >= 28, shared.Count + " shared values");
            foreach (string page in Pages)
            {
                var own = Regex.Matches(Web(page), @"(--[a-z0-9-]+)\s*:").Select(m => m.Groups[1].Value).ToList();
                Assert.DoesNotContain(own, shared.Contains);
            }
            Assert.DoesNotContain(":root", Web("index.html"));

            // Every value a page or a sheet reads is defined, by the sheet or by the page.
            foreach (string page in Pages)
            {
                string html = Web(page);
                var own = Regex.Matches(html, @"(--[a-z0-9-]+)\s*:").Select(m => m.Groups[1].Value).ToHashSet();
                foreach (Match use in Regex.Matches(html, @"var\((--[a-z0-9-]+)"))
                    Assert.True(shared.Contains(use.Groups[1].Value) || own.Contains(use.Groups[1].Value), page + " reads " + use.Groups[1].Value);
            }
            foreach (Match use in Regex.Matches(Web("css", "controller.css"), @"var\((--[a-z0-9-]+)"))
                Assert.Contains(use.Groups[1].Value, shared);
            Assert.Contains("<link rel=\"stylesheet\" href=\"/css/padforge.css\">\r\n    <link rel=\"stylesheet\" href=\"/css/controller.css\">",
                Web("controller.html").Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }

        /// <summary>The program's dark colors by brush name, each as eight
        /// hex digits with the alpha first: App.xaml's brushes, and the ones
        /// the theme sets in code, where a brush set for both grounds names
        /// the dark color first.</summary>
        private static Dictionary<string, List<string>> DarkBrushes()
        {
            var brushes = new Dictionary<string, List<string>>();
            void Add(string key, string color)
            {
                if (!brushes.TryGetValue(key, out var colors)) brushes[key] = colors = new List<string>();
                colors.Add((color.Length == 6 ? "ff" + color : color).ToLowerInvariant());
            }

            string app = ExtrasPageTests.RepoText("PadForge.App", "App.xaml");
            foreach (Match m in Regex.Matches(app, "<SolidColorBrush x:Key=\"(\\w+)\" Color=\"#([0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\""))
                Add(m.Groups[1].Value, m.Groups[2].Value);

            string theme = ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs");
            const string Rgb = @"Color\.FromRgb\(0x([0-9A-Fa-f]{2}), 0x([0-9A-Fa-f]{2}), 0x([0-9A-Fa-f]{2})\)";
            var named = Regex.Matches(theme, @"static readonly Color (\w+) = " + Rgb)
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value);
            foreach (Match m in Regex.Matches(theme, @"SetBrush\(""(\w+)"", (?:dark \? )?(?:" + Rgb + @"|(\w+))"))
            {
                if (m.Groups[2].Success) Add(m.Groups[1].Value, m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value);
                else if (named.TryGetValue(m.Groups[5].Value, out string known)) Add(m.Groups[1].Value, known);
            }
            return brushes;
        }

        /// <summary>A color of the sheet as eight hex digits, alpha first.</summary>
        private static string Argb(string css)
        {
            if (css.StartsWith("#", StringComparison.Ordinal)) return "ff" + css.Substring(1).ToLowerInvariant();
            decimal[] parts = Regex.Matches(css, @"[0-9.]+").Select(m => decimal.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(4, parts.Length);
            int alpha = (int)Math.Round(parts[3] * 255, MidpointRounding.AwayFromZero);
            return (alpha.ToString("x2") + ((int)parts[0]).ToString("x2") + ((int)parts[1]).ToString("x2") + ((int)parts[2]).ToString("x2"));
        }

        /// <summary>Each of the sheet's colors that names brushes of the
        /// program beside it is each of those brushes' dark color, as
        /// App.xaml declares it and as the theme sets it in code. A name the
        /// program does not set fails: the first sheet named the page's
        /// ground brush for a color that is the steel ground's.</summary>
        [Fact]
        public void TheSheetsColorsAreTheProgramsOwnBrushes()
        {
            var brushes = DarkBrushes();
            string sheet = Web("css", "padforge.css");

            int held = 0;
            foreach (Match row in Regex.Matches(sheet, @"(--[a-z0-9-]+):\s*(#[0-9a-fA-F]{6}|rgba\([^)]*\));\s*/\*\s*([^*]+?)\s*\*/"))
            {
                string[] names = row.Groups[3].Value.Split(',').Select(n => n.Trim()).ToArray();
                // A note in words says what a made color is. Names say whose it is.
                if (!names.All(n => Regex.IsMatch(n, @"^[A-Z]\w+$"))) continue;
                string color = Argb(row.Groups[2].Value);
                foreach (string name in names)
                {
                    Assert.True(brushes.TryGetValue(name, out var colors), row.Groups[1].Value + " names " + name + ", which the program does not set");
                    Assert.All(colors, c => Assert.True(c == color, row.Groups[1].Value + " is #" + color + " and " + name + " is #" + c));
                    held++;
                }
            }
            Assert.True(held >= 28, held + " names were held against a brush");
            Assert.Equal(new[] { "ff0b1018" }, brushes["ApplicationBackgroundBrush"]);
            Assert.DoesNotContain("ApplicationBackgroundBrush", sheet);

            // The ones the pages lean on most, by name.
            foreach (string pair in new[] { "--ground: #0a0e15;", "--card: #121925;", "--ember: #ff6b2c;", "--ember-ink: #160a03;", "--cold: #7cc7ea;", "--wait: #f2b84b;" })
                Assert.Contains(pair, sheet);
        }

        /// <summary>The small buttons that stand over a page, and the
        /// messages that cover one, are the sheet's: a script gives an
        /// element the sheet's class and writes no color of its own. Motion
        /// is ember while the phone's motion is sent, as every live thing in
        /// the program is. It was lawn green.</summary>
        [Fact]
        public void TheSmallButtonsAndTheCoversAreTheSheets()
        {
            string sheet = Web("css", "padforge.css");
            foreach (string part in new[] { ".pf-bar {", ".pf-title {", ".pf-status {", ".pf-status.live {", ".pf-chip {", ".pf-chip.on {", ".pf-chip.pf-corner {", ".pf-cover > h2 {" })
                Assert.Contains(part, sheet);
            Assert.Contains(".pf-chip.on { color: var(--ember); border-color: var(--ember); }", sheet);

            string full = Web("js", "fullscreen.js");
            Assert.Contains("btn.className = \"pf-chip\";", full);
            Assert.DoesNotContain("btn.style.cssText", full);

            string play = Web("js", "controller_client.js");
            Assert.Contains("motionBtn.className = \"pf-chip pf-corner\";", play);
            Assert.Contains("note.className = \"pf-chip pf-corner\";", play);
            Assert.Contains("motionBtn.classList.add(\"on\");", play);
            Assert.Contains("motionBtn.classList.remove(\"on\");", play);
            Assert.DoesNotContain("motionBtn.style", play);
            Assert.DoesNotContain("note.style", play);
            Assert.Contains("zone.className = \"touch-zone touchpad-zone\";", play);
            Assert.DoesNotContain("zone.style.border", play);
            Assert.DoesNotContain("zone.style.background", play);
            // The one color the script still writes is hardware: the lit
            // indicator LEDs drawn on a DualSense's art.
            Assert.Single(Regex.Matches(play, "#7CFC00"));
            Assert.Contains("pipOnColor = \"#7CFC00\"", play);

            string controller = Web("controller.html");
            Assert.Contains("<div id=\"warning-portrait\" class=\"pf-cover\">", controller);
            Assert.Contains("<div id=\"disconnect-message\" class=\"pf-cover\">", controller);
            string css = Web("css", "controller.css");
            Assert.Contains(".touch-zone.touchpad-zone {", css);
            Assert.Contains(".touchpad-dot.f0 { background: var(--ember-dot); border: 2px solid var(--ember); }", css);
            Assert.Contains(".touchpad-dot.f1 { background: var(--cold-dot); border: 2px solid var(--cold); }", css);
            // What the page has to say is a pill at the top's middle. A
            // strip across the top lay over the fullscreen button.
            Assert.Matches(@"#statusBar \{[^}]*left: 50%;[^}]*transform: translateX\(-50%\);[^}]*width: max-content;", css);
            Assert.Contains("#statusBar:empty { display: none; }", css);

            string custom = Web("custom.html");
            Assert.Contains("<button id=\"editBtn\" class=\"pf-chip\">", custom);
            Assert.Contains("<div id=\"disconnect-message\" class=\"pf-cover\"><h2>Disconnected. Tap to reconnect.</h2></div>", custom);
            Assert.DoesNotContain("style=\"", custom.Substring(custom.IndexOf("<body>", StringComparison.Ordinal)));
            Assert.Contains(".w-button.pressed { background: var(--ember); border-color: var(--ember); color: var(--ember-ink); }", custom);
        }

        /// <summary>The pages with a bar have the sheet's: the way back to
        /// the layouts, the page's name, and the link's state as a dot and a
        /// word that turn ember while the page is live.</summary>
        [Fact]
        public void TheBarAndTheLinksStateAreTheSheets()
        {
            string gamepad = Web("gamepad.html");
            Assert.Contains("<div class=\"pf-bar\">", gamepad);
            Assert.Contains("<a href=\"/\">&larr; Layouts</a>", gamepad);
            Assert.Contains("<span class=\"pf-title\">Browser Gamepad</span>", gamepad);
            Assert.Contains("<span id=\"status\" class=\"pf-status\">Connecting...</span>", gamepad);
            Assert.Contains("if (state && state.classList) state.classList.toggle(\"live\", open > 0);", Web("js", "gamepad_client.js"));
            Assert.Contains(".pad-note { font-size: 13px; color: var(--wait); margin-top: 4px; }", gamepad);

            string touchpad = Web("touchpad.html");
            Assert.Contains("<div class=\"pf-bar\">", touchpad);
            Assert.Contains("<a href=\"/\">&larr; Layouts</a>", touchpad);
            Assert.Contains("<span class=\"pf-title\">Touchpad</span>", touchpad);
            // The page's script sets the state's whole class, so it sets the
            // sheet's names. The page had a copy of the look under its own.
            Assert.Contains("<span id=\"status\" class=\"pf-status\">Connecting...</span>", touchpad);
            Assert.Contains("statusEl.className = 'pf-status live';", touchpad);
            Assert.Contains("statusEl.className = 'pf-status';", touchpad);
            Assert.Matches(@"\.click-button\.active \{\s*background: var\(--ember\);\s*border-color: var\(--ember\);\s*color: var\(--ember-ink\);", touchpad);

            // The menus' bar is the sheet's too, with the device's name, its
            // slot and its profile at its middle.
            string menus = Web("menus.html");
            Assert.Contains("<div class=\"pf-bar bar\">", menus);
            Assert.Contains("<a href=\"/\">&larr; Layouts</a>", menus);
            Assert.Contains("<span class=\"pf-status\" id=\"status\" role=\"status\">", menus);
            Assert.Contains("statusEl.className = 'pf-status live';", menus);
            Assert.Contains("statusEl.className = 'pf-status';", menus);
            Assert.Contains(".bar { position: relative; }", menus);
            foreach (string page in new[] { touchpad, menus })
            {
                Assert.DoesNotContain("className = 'status", page);
                Assert.DoesNotMatch(@"\.status\b[^;(]*\{", page);
            }

            // A lit tab and a pressed tile of the menus are plain ember with
            // the program's dark ink, as the home page's Continue is.
            Assert.Contains("--on-ember: var(--ember-ink);", menus);
            Assert.Matches(@"\.tab\[aria-selected=""true""\] \{\s*color: var\(--on-ember\); background: var\(--ember\);", menus);
            Assert.Matches(@"\.tile\.down \{\s*color: var\(--on-ember\); border-color: var\(--ember\);\s*background: var\(--ember\);", menus);
            // The page's one gradient runs from a color to itself: a tile
            // that is on wears the ember tint over its own ground.
            Assert.Single(Regex.Matches(menus, "linear-gradient"));
            Assert.Contains("background: linear-gradient(var(--ember-tint), var(--ember-tint)), var(--raised);", menus);
        }

        /// <summary>What a page links is in the program. The server answers
        /// a path with the embedded file of that name, so a sheet or a
        /// script a page names and the build leaves out is a 404, and the
        /// page has no look. The sheet in the build is the repo's.</summary>
        [Fact]
        public void EverySheetAndScriptAPageLinks_IsInTheBuild()
        {
            var assembly = typeof(PadForge.Services.WebControllerServer).Assembly;
            var built = assembly.GetManifestResourceNames().ToHashSet();
            int links = 0;
            foreach (string page in Pages)
            {
                Assert.Contains("PadForge.WebAssets." + page, built);
                foreach (Match link in Regex.Matches(Web(page), "(?:href|src)=\"/((?:css|js)/[^\"]+)\""))
                {
                    Assert.Contains("PadForge.WebAssets." + link.Groups[1].Value.Replace('/', '.'), built);
                    links++;
                }
            }
            Assert.True(links >= 18, links + " links");

            using var stream = assembly.GetManifestResourceStream("PadForge.WebAssets.css.padforge.css");
            Assert.NotNull(stream);
            using var reader = new System.IO.StreamReader(stream);
            Assert.Equal(Web("css", "padforge.css"), reader.ReadToEnd());
        }

        /// <summary>The stream picture is a browser source over a
        /// broadcast, so it has no ground. The sheet names a dark page,
        /// which gives a page with no ground of its own a dark one, and
        /// this page takes that back before its own rules.</summary>
        [Fact]
        public void TheStreamPictureKeepsNoGround()
        {
            string display = Web("display.html");
            int sheet = display.IndexOf("/css/padforge.css", StringComparison.Ordinal);
            int normal = display.IndexOf(":root { color-scheme: normal; }", StringComparison.Ordinal);
            Assert.True(sheet > 0 && normal > sheet, "the page does not take the dark ground back after the sheet");
            Assert.Matches(@"html, body \{[^}]*background: transparent;", display);
            Assert.DoesNotContain("theme-color", display);
            Assert.Contains("color-scheme: dark;", Web("css", "padforge.css"));
            Assert.Contains("#chat .paused { color: var(--wait); }", display);
            Assert.Contains("background: var(--veil);", display);
        }
    }
}
