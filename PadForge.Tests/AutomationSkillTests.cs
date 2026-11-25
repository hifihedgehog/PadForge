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
            var sb = new StringBuilder("# PadForge's documentation\n\n")
                .Append("The pages people read, by what they cover, each on the web and as this skill carries it. ")
                .Append("A read names the page a place comes from, in `docs`, and links both. A copy's links to pages this skill does not carry, and its pictures, go to the site.\n\n");
            foreach (var (path, covers) in AssistantDocs.Pages)
                sb.Append("- ").Append(covers).Append(": ").Append(AssistantDocs.Root).Append(path)
                  .Append(", `").Append(AssistantDocs.CopyPathOf(path)).Append("`\n");
            return sb.ToString();
        }

        // ─────────────────────────────────────────────
        //  The documentation pages the skill carries
        // ─────────────────────────────────────────────

        /// <summary>The variable that names the documentation site's
        /// Markdown folder, padforge.org's <c>wiki</c>, for writing the copies
        /// again. Read only with <see cref="WriteVariable"/> set. Without it
        /// the folder beside the repository's own is taken.</summary>
        internal const string WikiVariable = "PADFORGE_DOCS_WIKI";

        /// <summary>A page's path in the site's Markdown folder.</summary>
        private static string SourceOf(string pagePath) => pagePath.TrimEnd('/') + ".md";

        /// <summary>The first line of each copy: the page it is and where it
        /// came from.</summary>
        internal static string CopyHead(string pagePath)
            => "<!-- " + AssistantDocs.Root + pagePath + " as PadForge's skill carries it, from padforge.org's wiki/" + SourceOf(pagePath) + ". -->\n";

        private static readonly Regex MarkdownTarget = new(@"(!?\[[^\]\n]*\]\()([^)\s]+)((?:\s+""[^""]*"")?\))", RegexOptions.Compiled);
        private static readonly Regex HtmlTarget = new(@"\b((?:src|href)="")([^""]+)("")", RegexOptions.Compiled);
        private static readonly Regex Scheme = new(@"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.Compiled);

        /// <summary>A link of the page at <paramref name="pagePath"/> as its
        /// copy gives it. A link to another page the skill carries stays as
        /// it is, since the copies keep the site's folders. A link to a page
        /// it does not carry, a picture or another file goes to the site, and
        /// one from the site's root to padforge.org.</summary>
        internal static string Retarget(string pagePath, string target, ISet<string> carried)
        {
            if (target.StartsWith('#') || Scheme.IsMatch(target)) return target;
            if (target.StartsWith('/')) return "https://padforge.org" + target;
            int hash = target.IndexOf('#');
            string path = hash < 0 ? target : target.Substring(0, hash);
            string fragment = hash < 0 ? string.Empty : target.Substring(hash);
            var parts = new List<string>(SourceOf(pagePath).Split('/'));
            parts.RemoveAt(parts.Count - 1);
            foreach (string part in path.Split('/'))
            {
                if (part == "..") { if (parts.Count == 0) return target; parts.RemoveAt(parts.Count - 1); }
                else if (part != "." && part.Length > 0) parts.Add(part);
            }
            string resolved = string.Join("/", parts);
            if (resolved.EndsWith(".md", StringComparison.Ordinal))
            {
                string page = resolved.Substring(0, resolved.Length - 3);
                if (carried.Contains(page)) return target;
                string web = page == "index" ? string.Empty : page.EndsWith("/index", StringComparison.Ordinal) ? page.Substring(0, page.Length - 5) : page + "/";
                return AssistantDocs.Root + web + fragment;
            }
            return AssistantDocs.Root + resolved + (path.EndsWith('/') && resolved.Length > 0 ? "/" : string.Empty) + fragment;
        }

        /// <summary>A page's copy: its head line, then the site's Markdown
        /// with each link outside a code block as <see cref="Retarget"/> gives
        /// it, with LF line ends.</summary>
        internal static string DocsCopy(string pagePath, string markdown, ISet<string> carried)
        {
            var sb = new StringBuilder(CopyHead(pagePath));
            bool fenced = false;
            foreach (string raw in Lf(markdown).Split('\n'))
            {
                string line = raw;
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) fenced = !fenced;
                else if (!fenced)
                {
                    line = MarkdownTarget.Replace(line, m => m.Groups[1].Value + Retarget(pagePath, m.Groups[2].Value, carried) + m.Groups[3].Value);
                    line = HtmlTarget.Replace(line, m => m.Groups[1].Value + Retarget(pagePath, m.Groups[2].Value, carried) + m.Groups[3].Value);
                }
                sb.Append(line).Append('\n');
            }
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        /// <summary>The pages the skill carries, by their path without the
        /// slash: <c>features/devices</c>.</summary>
        private static HashSet<string> Carried() => AssistantDocs.Pages.Select(p => p.Path.TrimEnd('/')).ToHashSet(StringComparer.Ordinal);

        /// <summary>The copies made again from the site's Markdown folder,
        /// with stale ones removed. Only with <see cref="WriteVariable"/>
        /// set.</summary>
        private static void WriteDocsCopies()
        {
            string wiki = Environment.GetEnvironmentVariable(WikiVariable);
            if (string.IsNullOrWhiteSpace(wiki)) wiki = Path.Combine(OffLimitsSource.RepoRoot(), "..", "padforge.org", "wiki");
            Assert.True(Directory.Exists(wiki), "The documentation site's Markdown folder is not at " + wiki + ". Name it with " + WikiVariable + ".");
            var carried = Carried();
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, _) in AssistantDocs.Pages)
            {
                string source = Path.Combine(wiki, SourceOf(path).Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(source), "The site has no page " + SourceOf(path) + ", which AssistantDocs links.");
                string file = Path.Combine(SkillRoot, AssistantDocs.CopyPathOf(path).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, DocsCopy(path, File.ReadAllText(source), carried), new UTF8Encoding(false));
                written.Add(Path.GetFullPath(file));
            }
            string folder = Path.Combine(SkillRoot, AssistantDocs.CopyFolder.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            foreach (string stale in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(f => !written.Contains(Path.GetFullPath(f))))
                File.Delete(stale);
        }

        /// <summary>Every page a read links is in the skill (#600 item 14,
        /// AS-59), made from the site's own Markdown, and nothing else is:
        /// an assistant with no web access reads the page from PadForge. A
        /// copy's links reach other copies or the site, never a file the skill
        /// does not hold. The site is read only to write the copies
        /// again.</summary>
        [Fact]
        public void EveryLinkedDocsPage_IsInTheSkill()
        {
            if (Environment.GetEnvironmentVariable(WriteVariable) == "1") WriteDocsCopies();
            var carried = Carried();
            var files = Directory.GetFiles(SkillRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(SkillRoot, f).Replace('\\', '/'))
                .Where(f => f.StartsWith(AssistantDocs.CopyFolder, StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(AssistantDocs.Pages.Select(p => AssistantDocs.CopyPathOf(p.Path)).OrderBy(f => f, StringComparer.Ordinal), files);
            var broken = new List<string>();
            foreach (var (path, _) in AssistantDocs.Pages)
            {
                var file = AssistantSkill.Files.SingleOrDefault(f => f.Path == AssistantDocs.CopyPathOf(path));
                Assert.True(file != null, AssistantDocs.CopyPathOf(path) + " is not in the executable's skill.");
                string text = Lf(file.Text);
                Assert.StartsWith(CopyHead(path), text);
                Assert.True(text.Split('\n').Length > 10, path + "'s copy is empty.");
                Assert.Contains("\n# ", "\n" + text);
                // The copy a read links is this file.
                Assert.Equal(AssistantSkill.Root + AssistantDocs.CopyPathOf(path), AssistantDocs.CopyUri(AssistantDocs.Root + path));
                bool fenced = false;
                foreach (string line in text.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
                    if (fenced) continue;
                    foreach (Match m in MarkdownTarget.Matches(line).Concat(HtmlTarget.Matches(line)))
                    {
                        string target = m.Groups[2].Value;
                        if (target.StartsWith('#') || Scheme.IsMatch(target)) continue;
                        // A relative link left in a copy is a page the skill
                        // carries.
                        if (Retarget(path, target, carried) != target || !target.Split('#')[0].EndsWith(".md", StringComparison.Ordinal))
                            broken.Add(path + ": " + target);
                    }
                }
            }
            Assert.True(broken.Count == 0, "links a copy cannot follow: " + string.Join("; ", broken.Take(20)));
        }

        /// <summary>The links a copy gets, one of each kind.</summary>
        [Fact]
        public void ACopysLinks_ReachTheOtherCopiesOrTheSite()
        {
            var carried = new HashSet<string>(StringComparer.Ordinal) { "features/devices", "guides/macros", "troubleshooting" };
            Assert.Equal("../guides/macros.md#steps", Retarget("features/devices/", "../guides/macros.md#steps", carried));
            Assert.Equal("../troubleshooting.md", Retarget("features/devices/", "../troubleshooting.md", carried));
            Assert.Equal(AssistantDocs.Root + "features/wheel/#lock", Retarget("features/devices/", "wheel.md#lock", carried));
            Assert.Equal(AssistantDocs.Root + "features/", Retarget("features/devices/", "index.md", carried));
            Assert.Equal(AssistantDocs.Root + "images/devices.png", Retarget("features/devices/", "../images/devices.png", carried));
            Assert.Equal("https://padforge.org/assets/video/x.mp4", Retarget("features/devices/", "/assets/video/x.mp4", carried));
            Assert.Equal("#raw-input", Retarget("features/devices/", "#raw-input", carried));
            Assert.Equal("https://github.com/x", Retarget("features/devices/", "https://github.com/x", carried));
            string copy = DocsCopy("features/devices/", "# Devices\r\n\r\nSee [macros](../guides/macros.md), ![shot](../images/a.png) and <a href=\"/assets/t.txt\">t</a>.\r\n```\n[not](a.md)\n```\n", carried);
            Assert.Equal(CopyHead("features/devices/") + "# Devices\n\nSee [macros](../guides/macros.md), ![shot](" + AssistantDocs.Root + "images/a.png) and <a href=\"https://padforge.org/assets/t.txt\">t</a>.\n```\n[not](a.md)\n```\n", copy);
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
              .Append("A place on a controller's page takes `@c` and the controller's number after its ID, a card made per item, or an item of a profile card's or a layer tab's own menu, `@i` and its position, ")
              .Append("and a place of the Devices page `@d` and the device's short ID. `find` gives the full ID of what shows now.\n\n")
              .Append("Each line: the words, then the kind (number, switch, choice, setting, action, card, section, part, extra, dialog or menu item) and the reach ")
              .Append("(free, asks with its reason, or yours-alone).\n\n")
              .Append("Live places have IDs of their own: `page/Home` and the other pages, `controller/<number>`, `device/<short ID>`, `profile/<ID>`, ")
              .Append("`macro/c<number>/<position>`, `plugin/<ID>` with what a plugin declares under `plugin/<ID>/<key>` and what it declares for one of its devices under `device/<short ID>/<key>`, and Home's cards under `home/c<number>`. ")
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
                // A documentation page's copy is the site's text, where a
                // word in code is a function, a switch or a language's code
                // as often as a tool: it is held to the IDs it names and to
                // the retired tools below, never to snake_case words.
                bool docsCopy = file.Path.StartsWith(AssistantDocs.CopyFolder, StringComparison.Ordinal);
                foreach (Match m in Regex.Matches(file.Text, "`([^`\\n]+)`"))
                {
                    string token = m.Groups[1].Value.Trim();
                    string bare = token.Split(':')[0].TrimEnd('*');
                    if (snake.IsMatch(bare))
                    {
                        if (!docsCopy && !names.Contains(bare)) problems.Add(file.Path + ": " + token);
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
