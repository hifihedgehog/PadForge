using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PadForge.Common;
using PadForge.Search;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// PadForge's skill for assistants (#600), kept in the repository at
    /// skills/padforge: its generated files are what the catalog, the tools
    /// and the documentation table make now, SKILL.md stays under 500 lines
    /// and carries the version, and the skill, the server's instructions and
    /// the prompts name only tools, arguments and IDs that exist, and none
    /// of the retired tools.
    ///
    /// <para>To write the generated files again after a deliberate change,
    /// run this class with PADFORGE_WRITE_SKILL=1 set and commit the
    /// diff.</para>
    /// </summary>
    public class AutomationSkillTests
    {
        internal const string WriteVariable = "PADFORGE_WRITE_SKILL";

        private static string SkillRoot => Path.Combine(OffLimitsSource.RepoRoot(), "skills", "padforge");

        internal static readonly string[] Retired =
            { "find_settings", "get_setting", "set_setting", "save_snapshot", "get_system", "get_profile", "list_macros", "clear_mappings" };

        // ─────────────────────────────────────────────
        //  The generated files
        // ─────────────────────────────────────────────

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static string LevelName(AutomationLevel level) => level switch
        {
            AutomationLevel.Read => "Read",
            AutomationLevel.SendInputs => "Send Inputs",
            AutomationLevel.ChangeSettings => "Change Settings",
            _ => "Commands",
        };

        /// <summary>references/tools.md: the tools by level, with their
        /// titles and arguments.</summary>
        internal static string ToolsMarkdown()
        {
            var sb = new StringBuilder("# PadForge's tools by level\n\nGenerated from the tools PadForge serves. Each level allows what the ones above it allow. An argument with * is needed.\n");
            foreach (var group in AssistantTools.All.GroupBy(t => t.Level))
            {
                sb.Append("\n## ").Append(LevelName(group.Key)).Append("\n\n| Tool | What it does | Arguments |\n|---|---|---|\n");
                foreach (var tool in group)
                    sb.Append("| `").Append(tool.Name).Append("` | ").Append(tool.Title).Append(" | ")
                      .Append(string.Join(", ", AssistantJson.ArgumentNames(tool.Arguments).Select(a => "`" + a + "`"))).Append(" |\n");
            }
            sb.Append("\n## Only the user\n\nNo tool turns Automation on or off, changes its level, allows a program, approves a plugin or reads another program's Activity. A place whose reach is `yours-alone` is refused by `set` and `press` and its value is not read.\n");
            return sb.ToString();
        }

        /// <summary>references/docs.md: PadForge's documentation pages.</summary>
        internal static string DocsMarkdown()
        {
            var sb = new StringBuilder("# PadForge's documentation\n\nThe pages people read, by what they cover. A read names the page a place comes from, in `docs`.\n\n");
            foreach (var (path, covers) in AssistantDocs.Pages)
                sb.Append("- ").Append(covers).Append(": ").Append(AssistantDocs.Root).Append(path).Append('\n');
            return sb.ToString();
        }

        private static string PageTitle(SearchPage page) => page switch
        {
            SearchPage.Pad => "A controller's page",
            SearchPage.Settings => "Settings",
            SearchPage.Extras => "Extras",
            SearchPage.Devices => "Devices",
            SearchPage.Profiles => "Profiles",
            SearchPage.Home => "Home",
            SearchPage.About => "About",
            SearchPage.Window => "The window",
            _ => "Dialogs",
        };

        private static string English(SearchLabel label) => SearchLocator.EnglishText(label);

        private static string KindOf(SearchEntry e) => e.Kind switch
        {
            SearchKind.Setting => e.Value?.Kind switch
            {
                SearchValueKind.Number => "number",
                SearchValueKind.Switch => "switch",
                SearchValueKind.Choice => "choice",
                _ => "setting",
            },
            SearchKind.MenuItem => "menu item",
            _ => e.Kind.ToString().ToLowerInvariant(),
        };

        private static string ReachOf(SearchEntry e)
        {
            var cls = AssistantCatalog.ReachOf(e);
            string word = AutomationReadScrub.ReachWord(cls);
            return cls == ReachClass.Asks && e.Why != ReachReason.None ? word + " (" + e.Why + ")" : word;
        }

        /// <summary>references/pages.md and references/pages.json: every
        /// place of the catalog's markup, by page, section or extra and card,
        /// in English, with its ID, kind and reach.</summary>
        internal static (string Markdown, string Json) Pages()
        {
            var pages = SearchIndexBuilder.Build(SearchSources.Load());
            var catalog = SearchIndexBuilder.BuildCatalog(SearchSources.LoadCatalog());
            var ids = SearchIds.Assign(pages);
            foreach (var (entry, id) in SearchIds.Assign(catalog)) ids[entry] = id;
            var tray = PadForge.Services.Catalog.TrayMenu.Entries();
            foreach (var (entry, id) in SearchIds.Assign(tray)) ids[entry] = id;
            var all = pages.Concat(catalog).Concat(tray).Where(e => English(e.Label) != null || e.Kind == SearchKind.Dialog).ToList();

            var md = new StringBuilder();
            md.Append("# PadForge's pages\n\n")
              .Append("Generated from PadForge's catalog: every setting, button, card, section, extra, dialog and menu item, by page, in English. ")
              .Append("`PadForge.exe --describe <folder>` writes this file and pages.json. Each place has its ID, which `find`, `read`, `set`, `press` and `show` take. ")
              .Append("A place on a controller's page takes `@c` and the controller's number after its ID, a card made per item `@i` and its position, ")
              .Append("and a place of the Devices page `@d` and the device's short ID. `find` gives the full ID of what shows now.\n\n")
              .Append("Each line: the words, then the kind (number, switch, choice, setting, action, card, section, part, extra, dialog or menu item) and the reach ")
              .Append("(free, asks with its reason, or yours-alone).\n\n")
              .Append("Live places have IDs of their own: `page/Home` and the other pages, `controller/<number>`, `device/<short ID>`, `profile/<ID>`, ")
              .Append("`macro/c<number>/<position>`, `plugin/<ID>` with what a plugin declares under `plugin/<ID>/<key>`, and Home's cards under `home/c<number>`. ")
              .Append("An open dialog's buttons are its ID, `#` and the button's, as `read` gives them.\n");
            var json = new JsonArray();
            foreach (var pageGroup in all.GroupBy(e => e.Page).OrderBy(g => g.Key))
            {
                md.Append("\n## ").Append(PageTitle(pageGroup.Key)).Append('\n');
                foreach (var placeGroup in pageGroup.GroupBy(e => PlaceOf(e, pages)))
                {
                    if (!string.IsNullOrEmpty(placeGroup.Key)) md.Append("\n### ").Append(placeGroup.Key).Append('\n');
                    md.Append('\n');
                    foreach (var e in placeGroup.OrderBy(x => x.Order))
                    {
                        string words = English(e.Label) ?? e.Dialog;
                        string qualifier = English(e.Qualifier);
                        if (!string.IsNullOrEmpty(qualifier)) words += " " + qualifier;
                        string card = e.Kind is SearchKind.Card ? null : English(e.CardTitle);
                        md.Append("- ").Append(words);
                        if (!string.IsNullOrEmpty(card)) md.Append(" (").Append(card).Append(')');
                        md.Append(": ").Append(KindOf(e)).Append(", ").Append(ReachOf(e));
                        if (e.UnderMore) md.Append(", under More");
                        md.Append(". `").Append(ids[e]).Append("`\n");
                        json.Add(new JsonObject
                        {
                            ["id"] = ids[e],
                            ["page"] = PageTitle(e.Page),
                            ["place"] = placeGroup.Key,
                            ["card"] = card,
                            ["words"] = words,
                            ["kind"] = KindOf(e),
                            ["reach"] = AutomationReadScrub.ReachWord(AssistantCatalog.ReachOf(e)),
                            ["why"] = e.Why == ReachReason.None ? null : e.Why.ToString(),
                            ["under_more"] = e.UnderMore,
                        });
                    }
                }
            }
            return (md.ToString(), json.ToJsonString(Json) + "\n");
        }

        /// <summary>Where an entry sits on its page: the extra, the
        /// controller's section, the dialog, or nothing.</summary>
        private static readonly string[] SectionKeys =
            { null, "Pad_Section_Sticks", "Pad_Section_Rumble", "Pad_Section_Motion", "Pad_Section_Lights", "Pad_Section_Macros" };

        private static string PlaceOf(SearchEntry e, IReadOnlyList<SearchEntry> pages)
        {
            if (e.Dialog != null) return e.Dialog;
            if (e.ExtraKey != null)
            {
                var tile = pages.FirstOrDefault(x => x.Kind == SearchKind.Extra && x.ExtraKey == e.ExtraKey);
                return (tile == null ? null : English(tile.Label)) ?? e.ExtraKey;
            }
            if (e.Page == SearchPage.Pad && e.Section >= 0)
                return e.Section == 0 || e.Section >= SectionKeys.Length
                    ? "Buttons (Keys & Mouse, Notes & Controls or Hands by the controller's type)"
                    : English(new SearchLabel(SectionKeys[e.Section], null));
            if (e.Page == SearchPage.Window && e.ScopeName == "Tray") return "Tray menu";
            return string.Empty;
        }

        private static string Lf(string text) => text.Replace("\r\n", "\n");

        [Fact]
        public void TheGeneratedFiles_AreWhatTheCatalogTheToolsAndTheDocsMakeNow()
        {
            var (pages, pagesJson) = Pages();
            var want = new Dictionary<string, string>
            {
                ["references/tools.md"] = ToolsMarkdown(),
                ["references/docs.md"] = DocsMarkdown(),
                ["references/pages.md"] = pages,
                ["references/pages.json"] = pagesJson,
            };
            bool write = Environment.GetEnvironmentVariable(WriteVariable) == "1";
            foreach (var (path, text) in want)
            {
                string file = Path.Combine(SkillRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (write) File.WriteAllText(file, Lf(text), new UTF8Encoding(false));
                Assert.True(File.Exists(file), path + " is missing. Run this class with " + WriteVariable + "=1.");
                Assert.True(Lf(File.ReadAllText(file)) == Lf(text), path + " is not what PadForge makes now. Run this class with " + WriteVariable + "=1 and commit the diff.");
            }
            // The page description holds the places of every page.
            Assert.Contains("## Settings", pages);
            Assert.Contains("## Dialogs", pages);
            Assert.Contains("Tray menu", pages);
            Assert.Contains("`settings/", pages);
        }

        [Fact]
        public void TheSkill_IsTheRepositorysFolder_StaysUnder500Lines_AndCarriesTheVersion()
        {
            // What PadForge serves and installs is the repository's folder.
            var onDisk = Directory.GetFiles(SkillRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(SkillRoot, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(onDisk, AssistantSkill.Files.Select(f => f.Path).OrderBy(f => f, StringComparer.Ordinal));
            string skill = File.ReadAllText(Path.Combine(SkillRoot, "SKILL.md"));
            Assert.InRange(Lf(skill).Split('\n').Length, 40, 499);
            Assert.Contains(AssistantSkill.VersionLine, skill);
            // The copy served and installed carries this PadForge's version.
            var version = AssistantSkill.Frontmatter().First(f => f.Key == "version").Value;
            Assert.Equal(PadForge.Services.BuildIdentity.VersionText(PadForge.Services.BuildIdentity.Version), version);
            Assert.DoesNotContain("version: dev", AssistantSkill.Files[0].Text);
            // The Automation page compares an installed copy's line with
            // this one as a version.
            Assert.True(Version.TryParse(version, out _), version);
            // Every file of the skill but its own is read in full by its
            // test, and the folder holds nothing else.
            Assert.Contains("references/pages.md", onDisk);
            Assert.Contains("references/pages.json", onDisk);
        }

        /// <summary>Every name the guidance can call on: the tools, their
        /// arguments, the properties of their results, and the words of
        /// kinds, reaches and advances.</summary>
        private static HashSet<string> KnownNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<Type>();
            void Walk(Type type)
            {
                if (type == null || !seen.Add(type) || type.Namespace?.StartsWith("PadForge", StringComparison.Ordinal) != true) return;
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var json = p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
                    names.Add(json?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name));
                    var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Walk(a);
                    Walk(t);
                }
            }
            foreach (var tool in AssistantTools.All)
            {
                names.Add(tool.Name);
                Walk(tool.Arguments);
                Walk(tool.Result);
            }
            foreach (string word in AssistantCatalog.Kinds) names.Add(word);
            foreach (string word in new[] { "free", "asks", "yours-alone", "click", "seconds", "next", "outside_text", "padforge" }) names.Add(word);
            return names;
        }

        [Fact]
        public void TheSkillTheInstructionsAndThePrompts_NameOnlyToolsArgumentsAndIdsThatExist()
        {
            var names = KnownNames();
            var index = SearchIndexBuilder.Build(SearchSources.Load());
            var ids = new HashSet<string>(SearchIds.Assign(index).Values, StringComparer.Ordinal);
            foreach (var id in SearchIds.Assign(SearchIndexBuilder.BuildCatalog(SearchSources.LoadCatalog())).Values) ids.Add(id);
            string[] livePrefixes = { "page/", "controller/", "device/", "profile/", "macro/", "plugin/", "home/" };
            string[] pageWords = { "pad", "settings", "extras", "devices", "profiles", "dialog", "window", "home", "about" };
            var snake = new Regex("^[a-z]+(_[a-z]+)*$");
            var problems = new List<string>();
            foreach (var file in AssistantSkill.Files.Where(f => f.Path.EndsWith(".md", StringComparison.Ordinal) && f.Path != "references/pages.md"))
            {
                foreach (Match m in Regex.Matches(file.Text, "`([^`\\n]+)`"))
                {
                    string token = m.Groups[1].Value.Trim();
                    string bare = token.Split(':')[0].TrimEnd('*');
                    if (snake.IsMatch(bare))
                    {
                        if (!names.Contains(bare)) problems.Add(file.Path + ": " + token);
                        continue;
                    }
                    if (bare.Contains('/') && !bare.Contains(' ') && pageWords.Any(w => bare.StartsWith(w + "/", StringComparison.Ordinal) || bare.StartsWith(w + ".", StringComparison.Ordinal)))
                    {
                        string id = bare.Split('@')[0];
                        if (!ids.Contains(id) && !livePrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal))) problems.Add(file.Path + ": " + token);
                    }
                }
            }
            // The instructions and the prompts name tools and arguments in
            // plain words: every word joined by an underscore is one.
            var texts = new List<(string Where, string Text)> { ("instructions", AssistantGuidance.Instructions) };
            foreach (var prompt in AssistantGuidance.Prompts) texts.Add(("prompt " + prompt.Name, prompt.Body));
            foreach (var tool in AssistantTools.All) texts.Add(("tool " + tool.Name, tool.Description));
            foreach (var (where, text) in texts)
                foreach (Match m in Regex.Matches(text, "\\b[a-z]+(_[a-z]+)+\\b"))
                    if (!names.Contains(m.Value)) problems.Add(where + ": " + m.Value);
            Assert.True(problems.Count == 0, "names that are no tool, argument or ID: " + string.Join("; ", problems));

            // The retired tools are named nowhere: skill, instructions,
            // prompts, the tools' words, and the app's own text.
            var everything = string.Join("\n", AssistantSkill.Files.Select(f => f.Text).Concat(texts.Select(t => t.Text)));
            foreach (string gone in Retired) Assert.DoesNotContain(gone, everything);
            foreach (string source in Directory.GetFiles(Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App"), "*.cs", SearchOption.AllDirectories))
            {
                if (source.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                string text = File.ReadAllText(source);
                foreach (string gone in Retired) Assert.False(text.Contains(gone, StringComparison.Ordinal), gone + " is named in " + source);
            }
            // The rule against driving the screen, and show for show me.
            Assert.Contains("Never drive the screen", AssistantGuidance.Instructions);
            Assert.Contains("computer use", AssistantGuidance.Instructions);
            Assert.Contains("\"show me\"", AssistantGuidance.Instructions);
            Assert.InRange(AssistantGuidance.Instructions.Length, 1000, AssistantGuidance.LongestInstructions - 1);
        }

        [Fact]
        public void TheReadme_PointsAReaderOfTheRepository_AtTheSkillAndTheSources()
        {
            string readme = File.ReadAllText(Path.Combine(OffLimitsSource.RepoRoot(), "README.md"));
            Assert.Contains("skills/padforge", readme);
            Assert.Contains("--mcp", readme);
            Assert.Contains("PadForge.App/Services/Automation/Assistant", readme);
        }
    }
}
