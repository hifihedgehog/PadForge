using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PadForge.Search
{
    /// <summary>What became of one reset button of a page as the index was
    /// built: an entry, a part the rules leave out on purpose, or a row with
    /// no label of its own, which its card stands for.</summary>
    internal enum ResetOutcome { Entry, LeftOut, NoLabel }

    /// <summary>The entries of the five pages, and what became of each reset
    /// button, for the test that holds every setting to an entry.</summary>
    internal sealed record SearchIndex(IReadOnlyList<SearchEntry> Entries,
        IReadOnlyList<(SearchPage Page, int Line, ResetOutcome Outcome)> Resets);

    /// <summary>
    /// Builds Search's index from the pages' own markup (#574), so the index
    /// follows a page as it changes and nothing lists settings by hand. Pure:
    /// it reads the five texts with System.Xml.Linq and touches no element of
    /// WPF, so it runs on a worker thread. The rules:
    ///
    /// <para>A setting is a reset button and the row around it, named by the
    /// row's label: a label TextBlock, else the row's first TextBlock bound to
    /// a string, else a switch's words, else the reset's SettingLabel. A card
    /// is a CardBorder with a CardTitle or a HeroCardTitle, or a FoldCard. A
    /// style of the page's own that is based on CardBorder is a card too
    /// (the Extras pages' ExtraCard), and one based on CardTitle is its
    /// title (ExtraCardTitle). An action is a button
    /// whose words are a string, less the shared verbs (Common_*), Reset All
    /// and the Extras pages' Back. Each extra's tile, each section of the
    /// controller page and each part of its Buttons and Macros &amp; Menus is
    /// one entry too.</para>
    ///
    /// <para>Left out on purpose: what a list of the user's own things draws
    /// (an item template whose root is no card), every keyed template but the
    /// open mapping row's, and the controller page's Macros &amp; Menus,
    /// which edits the picked macro or menu.</para>
    ///
    /// <para>The open mapping row is read as the page builds it for the
    /// row's first source: its template, and each part of it that a template
    /// of its own draws, in its place and on the data the part gives that
    /// template. So the entries stand in the order their elements do, and a
    /// rule under a part is read through the part's data, as WPF reads it.
    /// The added sources' lines are the user's own and are not read: they
    /// repeat the first source's.</para>
    /// </summary>
    internal static class SearchIndexBuilder
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly Regex StringKey = new(@"^\{Binding\s+(?:Path=)?(\w+),\s*Source=\{x:Static\s+\w+:Strings\.Instance\}");
        private static readonly Regex StyleKey = new(@"^\{(?:StaticResource|DynamicResource)\s+(\w+)\}$");

        private static readonly HashSet<string> ResetStyles = new(StringComparer.Ordinal)
            { "ResetButton", "ResetButtonTight", "QuietResetButton", "SettingResetButton" };
        // EndsLabel is the label of a row whose switch stands at its end
        // (SettingRow.Ends, the sections pass of #574).
        private static readonly HashSet<string> LabelStyles = new(StringComparer.Ordinal) { "DzLabel", "DzSubLabel", "RowOptionLabel", "EndsLabel", "LineLabel" };
        // Words on a source's line that are no label to land on. A switch's
        // name shows in a narrow box only, and the head over the sources
        // says the same word: the switch's own words are its label. A key's
        // short name ("Up") stands for a setting its reset names in full
        // ("Up Input"): the reset's label is the entry's.
        private static readonly HashSet<string> PassedOverLabels = new(StringComparer.Ordinal) { "LineSwitchName", "LineKeyLabel" };
        private static readonly HashSet<string> Toggles = new(StringComparer.Ordinal) { "CheckBox", "ToggleSwitch", "RadioButton" };
        private static readonly HashSet<string> Inputs = new(StringComparer.Ordinal)
            { "Slider", "ComboBox", "CheckBox", "ToggleButton", "ToggleSwitch", "TextBox", "NumberBox", "RangeSlider", "CurveEditor", "RadioButton" };
        /// <summary>The open mapping row's template, on the row.</summary>
        internal const string RowTemplate = "MappingRowPanel";
        /// <summary>The templates that draw a part of the open row. Each is
        /// read where a ContentControl of the row shows it.</summary>
        private static readonly HashSet<string> RowPartTemplates = new(StringComparer.Ordinal)
            { "SourceLine", "SourceModeSettings", "SourceDials" };
        // Property elements whose content is part of the page. Every other
        // property element (styles, templates, triggers, columns, tooltips) is
        // read only for its reset buttons.
        private static readonly HashSet<string> ContentProperties = new(StringComparer.Ordinal)
            { "Header", "HeaderAction", "Content", "Child", "Children", "Items", "Foot", "Subtitle", "TrailingContent", "LeadingContent" };

        /// <summary>The page of the catalog a source beyond Search's five
        /// pages stands for (#600): Home, About, the window's own markup
        /// (its title bar and sidebar), or a dialog.</summary>
        internal static SearchPage CatalogPageOf(string name) => name switch
        {
            "HomePage" => SearchPage.Home,
            "AboutPage" => SearchPage.About,
            "MainWindow" => SearchPage.Window,
            _ => SearchPage.Dialog,
        };

        /// <summary>The catalog's markup beyond Search's five pages, by name
        /// (#600): every dialog, read as a page of its own whose entries name
        /// the dialog, and Home, About and the window's own markup.</summary>
        internal static IReadOnlyList<SearchEntry> BuildCatalog(IReadOnlyDictionary<string, string> sources)
        {
            var entries = new List<SearchEntry>();
            var resets = new List<(SearchPage, int, ResetOutcome)>();
            foreach (var (name, text) in sources.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
                var page = CatalogPageOf(name);
                new PageWalk(page, Views.PadPage.TabOfGroupName, entries, resets, doc, page == SearchPage.Dialog ? name : null).Run();
            }
            return entries;
        }

        /// <summary>The index of the pages given, with the controller page's
        /// groups placed by <see cref="Views.PadPage.TabOfGroupName"/>.</summary>
        internal static IReadOnlyList<SearchEntry> Build(IReadOnlyDictionary<SearchPage, string> markup)
            => Read(markup, Views.PadPage.TabOfGroupName).Entries;

        internal static SearchIndex Read(IReadOnlyDictionary<SearchPage, string> markup, IReadOnlyDictionary<string, int> tabOfGroupName)
        {
            var entries = new List<SearchEntry>();
            var resets = new List<(SearchPage, int, ResetOutcome)>();
            foreach (var (page, text) in markup.OrderBy(p => p.Key))
            {
                var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
                new PageWalk(page, tabOfGroupName, entries, resets, doc).Run();
            }
            return new SearchIndex(entries, resets);
        }

        /// <summary>The card a part sits in: its title, and the last
        /// subsection heading read inside it so far.</summary>
        private sealed class CardState
        {
            public SearchLabel Title;
            public string LastSubsection;
        }

        private sealed record Ctx
        {
            public bool LeftOut { get; init; }
            public int Section { get; init; } = -1;
            public int Tab { get; init; } = -1;
            public string ExtraKey { get; init; }
            public bool UnderMore { get; init; }
            public bool RowOptions { get; init; }
            public string ScopeName { get; init; }
            public bool InTemplate { get; init; }
            public string ItemsPath { get; init; }
            /// <summary>The named list whose item template the walk is in.</summary>
            public string ItemsList { get; init; }
            public SearchLabel ItemQualifier { get; init; }
            public IReadOnlyList<SearchGate> Gates { get; init; } = Array.Empty<SearchGate>();
            public CardState Card { get; init; }
            /// <summary>The reach class and reason from the nearest
            /// Reach.Class on the way down (#600).</summary>
            public PadForge.Common.ReachClass Reach { get; init; }
            public PadForge.Common.ReachReason Why { get; init; }
            /// <summary>The label of the button a context menu belongs
            /// to, which qualifies its items.</summary>
            public SearchLabel MenuOwner { get; init; }
            /// <summary>The path from the item, or from the page's data, to
            /// the data an element on the way down set for itself
            /// (DataContext): a rule, a value or a title under it is read
            /// through it, as WPF reads them.</summary>
            public string DataPath { get; init; }
            /// <summary>An element on the way down set data the index cannot
            /// follow (x:Null, a resource, a binding to an element): nothing
            /// under it is read on the data.</summary>
            public bool DataUnread { get; init; }
        }

        /// <summary>A path on the data under <paramref name="ctx"/>: through the
        /// data an element on the way down set for itself.</summary>
        private static string OnData(Ctx ctx, string path)
            => string.IsNullOrEmpty(ctx.DataPath) || string.IsNullOrEmpty(path) ? path : ctx.DataPath + "." + path;

        private sealed class PageWalk
        {
            private readonly SearchPage _page;
            private readonly IReadOnlyDictionary<string, int> _tabOfGroupName;
            private readonly List<SearchEntry> _entries;
            private readonly List<(SearchPage, int, ResetOutcome)> _resets;
            private readonly XDocument _doc;
            private readonly HashSet<string> _extraKeys;
            private readonly Dictionary<string, XElement> _rowParts = new(StringComparer.Ordinal);
            private readonly HashSet<string> _cardStyles;
            private readonly HashSet<string> _cardTitleStyles;
            private readonly string _dialog;
            private int _order;

            public PageWalk(SearchPage page, IReadOnlyDictionary<string, int> tabOfGroupName, List<SearchEntry> entries,
                List<(SearchPage, int, ResetOutcome)> resets, XDocument doc, string dialog = null)
            {
                _page = page;
                _dialog = dialog;
                _tabOfGroupName = tabOfGroupName;
                _entries = entries;
                _resets = resets;
                _doc = doc;
                _extraKeys = new HashSet<string>(doc.Descendants().Where(e => e.Name.LocalName == "ExtraTile")
                    .Select(e => (string)e.Attribute("Key")).Where(k => !string.IsNullOrEmpty(k)), StringComparer.Ordinal);
                _cardStyles = StylesBasedOn(doc, "CardBorder");
                _cardTitleStyles = StylesBasedOn(doc, "CardTitle", "HeroCardTitle");
                if (page == SearchPage.Pad)
                    foreach (var template in doc.Descendants().Where(e => e.Name.LocalName == "DataTemplate"))
                        if ((string)template.Attribute(Xaml + "Key") is { } key && RowPartTemplates.Contains(key))
                            _rowParts[key] = template;
            }

            /// <summary>The style keys given and every style of the page based
            /// on one of them, through any number of steps: a page's own card
            /// look is a card as CardBorder is.</summary>
            private static HashSet<string> StylesBasedOn(XDocument doc, params string[] roots)
            {
                var keys = new HashSet<string>(roots, StringComparer.Ordinal);
                var styles = doc.Descendants().Where(e => e.Name.LocalName == "Style")
                    .Select(e => (Key: (string)e.Attribute(Xaml + "Key"), Base: StyleKey.Match(Attr(e, "BasedOn") ?? string.Empty)))
                    .Where(x => !string.IsNullOrEmpty(x.Key) && x.Base.Success)
                    .Select(x => (x.Key, Base: x.Base.Groups[1].Value))
                    .ToList();
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var (key, basedOn) in styles)
                        if (keys.Contains(basedOn) && keys.Add(key)) grew = true;
                }
                return keys;
            }

            /// <summary>The template a ContentControl of the open row shows
            /// its content with, when it is one of the row's parts.</summary>
            private XElement RowPartOf(XElement e)
            {
                var m = StyleKey.Match(Attr(e, "ContentTemplate") ?? string.Empty);
                return m.Success && _rowParts.TryGetValue(m.Groups[1].Value, out var template) ? template : null;
            }

            /// <summary>A source of the catalog alone (a dialog, Home, About,
            /// the window): its buttons are entries whatever their words,
            /// an icon button's by its screen-reader name.</summary>
            private bool CatalogOnly => _page is SearchPage.Dialog or SearchPage.Window or SearchPage.Home or SearchPage.About;

            public void Run()
            {
                var ctx = Enter(_doc.Root, new Ctx());
                if (_dialog != null)
                {
                    // The dialog itself: its title, or its name when the
                    // title is set in code.
                    string title = KeyIn(Attr(_doc.Root, "Title"));
                    _current = _doc.Root;
                    Add(ctx, SearchKind.Dialog, title != null ? new SearchLabel(title, null) : new SearchLabel(null, "=" + _dialog), null, SearchLabelSource.CardTitle);
                }
                Walk(_doc.Root, ctx, entered: true);
            }

            /// <summary>The element an entry being added stands for, whose
            /// screen-reader words and AutomationId the entry carries.</summary>
            private XElement _current;

            private static string Local(XElement e) => e.Name.LocalName;

            private static bool IsProperty(XElement e) => Local(e).Contains('.');

            private static string Attr(XElement e, string localName)
                => e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName && a.Name.Namespace != Xaml)?.Value;

            private static string NameOf(XElement e) => (string)e.Attribute(Xaml + "Name") ?? Attr(e, "Name");

            /// <summary>Whether an element is a list of items: it has an
            /// ItemsSource, set as an attribute or as a property
            /// element.</summary>
            private static bool HoldsItems(XElement e)
                => e != null && (Attr(e, "ItemsSource") != null || e.Elements().Any(c => IsProperty(c) && Local(c).EndsWith(".ItemsSource", StringComparison.Ordinal)));

            private static string StyleOf(XElement e)
            {
                var m = StyleKey.Match(Attr(e, "Style") ?? string.Empty);
                return m.Success ? m.Groups[1].Value : null;
            }

            private static int LineOf(XElement e) => ((IXmlLineInfo)e).LineNumber;

            /// <summary>The string key an attribute binds, or null.</summary>
            private static string KeyIn(string value)
            {
                var m = StringKey.Match(value ?? string.Empty);
                return m.Success ? m.Groups[1].Value : null;
            }

            /// <summary>A text's label: the string key of Text or BidiText.Text,
            /// else a plain path binding on the data, else the first Run's.</summary>
            private static SearchLabel TextLabel(XElement e, bool allowPath)
            {
                foreach (string name in new[] { "Text", "BidiText.Text" })
                {
                    string value = Attr(e, name);
                    if (value == null) continue;
                    string key = KeyIn(value);
                    if (key != null) return new SearchLabel(key, null);
                    if (allowPath && Binding.Parse(value) is { } b && b.IsPlainPath) return new SearchLabel(null, b.Path);
                }
                if (allowPath)
                {
                    var run = e.Elements().FirstOrDefault(c => Local(c) == "Run");
                    if (run != null) return TextLabel(run, true);
                }
                return null;
            }

            private bool IsCard(XElement e) => (Local(e) == "Border" && StyleOf(e) is { } style && _cardStyles.Contains(style)) || Local(e) == "FoldCard";

            private static bool IsReset(XElement e)
                => Local(e) == "SettingResetButton" || (Local(e) == "Button" && StyleOf(e) is { } s && ResetStyles.Contains(s));

            private static IEnumerable<XElement> Kids(XElement e) => e.Elements().Where(c => !IsProperty(c));

            private void Add(Ctx ctx, SearchKind kind, SearchLabel label, SearchLabel qualifier, SearchLabelSource source,
                SearchValueSource value = null, string target = null, string extraKey = null, XElement element = null)
            {
                SearchLabel card = ctx.RowOptions ? null : ctx.Card?.Title;
                if (kind == SearchKind.Card || card == qualifier || (card != null && qualifier != null && card.Equals(qualifier)))
                    card = null;
                element ??= _current;
                _entries.Add(new SearchEntry(kind, _page, label, qualifier, card, ctx.Section, ctx.Tab, extraKey ?? ctx.ExtraKey,
                    ctx.UnderMore, ctx.RowOptions, ctx.ScopeName, ctx.ItemsPath, ctx.Gates, value, target, source, _order++)
                {
                    Name = element == null ? null : WordsOf(element, "AutomationProperties.Name"),
                    Help = element == null ? null : WordsOf(element, "AutomationProperties.HelpText") ?? WordsOf(element, "ToolTip"),
                    AutomationId = element == null ? null : PlainAttr(element, "AutomationProperties.AutomationId"),
                    Reach = ReachOf(element, ctx),
                    Why = ReachOf(element, ctx) == PadForge.Common.ReachClass.Asks ? WhyOf(element, ctx) : PadForge.Common.ReachReason.None,
                    Dialog = _dialog,
                    Handler = element == null ? null : HandlerOf(element),
                    ItemsList = kind == SearchKind.MenuItem ? ctx.ItemsList : null,
                });
            }

            /// <summary>A string key or a plain data path an attribute
            /// binds, as a label, or null.</summary>
            private static SearchLabel WordsOf(XElement e, string attribute)
            {
                string value = Attr(e, attribute);
                if (value == null) return null;
                if (KeyIn(value) is { } key) return new SearchLabel(key, null);
                return Binding.Parse(value) is { IsPlainPath: true } b ? new SearchLabel(null, b.Path) : null;
            }

            /// <summary>An attribute's text when it is no binding.</summary>
            private static string PlainAttr(XElement e, string attribute)
                => Attr(e, attribute) is { } v && !v.StartsWith("{", StringComparison.Ordinal) ? v : null;

            /// <summary>The class an element carries: its own, else the one on
            /// the way down.</summary>
            private static PadForge.Common.ReachClass ReachOf(XElement e, Ctx ctx)
                => e != null && Enum.TryParse(Attr(e, "Reach.Class"), out PadForge.Common.ReachClass own) && own > ctx.Reach ? own : ctx.Reach;

            private static PadForge.Common.ReachReason WhyOf(XElement e, Ctx ctx)
            {
                if (e != null && Enum.TryParse(Attr(e, "Reach.Class"), out PadForge.Common.ReachClass own) && own > ctx.Reach)
                    return Enum.TryParse(Attr(e, "Reach.Why"), out PadForge.Common.ReachReason why) ? why : PadForge.Common.ReachReason.None;
                return ctx.Why;
            }

            /// <summary>An action's Click handler or Command binding.</summary>
            private static string HandlerOf(XElement e)
            {
                if (PlainAttr(e, "Click") is { } click) return click;
                return Binding.Parse(Attr(e, "Command"))?.Path;
            }

            private Ctx Enter(XElement e, Ctx ctx)
            {
                // The reach class on the way down (#600): the strictest so
                // far, as Reach.Of reads it. A stricter class takes its own
                // reason or none.
                string reach = Attr(e, "Reach.Class");
                if (reach != null && Enum.TryParse(reach, out PadForge.Common.ReachClass cls) && cls > ctx.Reach)
                    ctx = ctx with
                    {
                        Reach = cls,
                        Why = Enum.TryParse(Attr(e, "Reach.Why"), out PadForge.Common.ReachReason why) ? why : PadForge.Common.ReachReason.None,
                    };
                // Data the element sets for itself is what its own bindings,
                // and every binding under it, read. The attribute in no
                // namespace: the designer's d:DataContext is not data.
                string data = (string)e.Attribute("DataContext");
                if (data != null && !ctx.DataUnread)
                {
                    var d = Binding.Parse(data);
                    if (d == null || d.Elsewhere) ctx = ctx with { DataUnread = true };
                    else if (!string.IsNullOrEmpty(d.Path) && d.Path != ".") ctx = ctx with { DataPath = OnData(ctx, d.Path) };
                }
                // A gate on the way down to an item's row.
                if ((ctx.ItemsPath != null || ctx.RowOptions) && !ctx.DataUnread)
                {
                    var b = Binding.Parse(Attr(e, "Visibility"));
                    if (b != null && b.IsPlainPath)
                        ctx = ctx with { Gates = ctx.Gates.Append(new SearchGate(OnData(ctx, b.Path), b.ConverterKey, b.ConverterParameter)).ToList() };
                }
                string name = NameOf(e);
                if (!string.IsNullOrEmpty(name) && !ctx.InTemplate)
                {
                    ctx = ctx with { ScopeName = name };
                    if (_page == SearchPage.Pad)
                    {
                        if (ctx.Section is >= 1 and <= 4 && _tabOfGroupName.TryGetValue(name, out int tab)) ctx = ctx with { Tab = tab };
                        else if (ctx.Section == 0 && name == "OutputGroup") ctx = ctx with { Tab = 17 };
                        else if (ctx.Section == 0 && name == "MappingDataGrid") ctx = ctx with { Tab = 2 };
                    }
                    else if (_page == SearchPage.Extras && Local(e) == "StackPanel" && name.EndsWith("Page", StringComparison.Ordinal)
                             && _extraKeys.Contains(name.Substring(0, name.Length - 4)))
                        ctx = ctx with { ExtraKey = name.Substring(0, name.Length - 4) };
                }
                if (Local(e) == "MoreRow") ctx = ctx with { UnderMore = true };
                return ctx;
            }

            private void Walk(XElement e, Ctx ctx, bool entered = false)
            {
                string local = Local(e);
                if (IsProperty(e))
                {
                    string property = local.Substring(local.LastIndexOf('.') + 1);
                    // A button's menu (#600): each item is an entry, under
                    // the button's words.
                    // Every button's menu, a template's and the macro
                    // editor's included: the class on the way down is the
                    // menu's.
                    if (property == "ContextMenu")
                    {
                        foreach (var item in e.Descendants().Where(d => Local(d) == "MenuItem"))
                            if (KeyIn(Attr(item, "Header")) is { } header)
                                Add(Enter(item, ctx), SearchKind.MenuItem, new SearchLabel(header, null), ctx.MenuOwner, SearchLabelSource.Button, element: item);
                        return;
                    }
                    if (property == "Resources")
                    {
                        foreach (var r in e.Elements())
                        {
                            string key = (string)r.Attribute(Xaml + "Key");
                            if (!ctx.LeftOut && Local(r) == "DataTemplate" && key == RowTemplate && _page == SearchPage.Pad)
                                foreach (var c in Kids(r))
                                    Walk(c, new Ctx { Section = 0, Tab = ViewModels.PadViewModel.MappingsTabIndex, RowOptions = true, InTemplate = true, Reach = ctx.Reach, Why = ctx.Why });
                            else if (Local(r) == "DataTemplate" && key != null && _rowParts.ContainsKey(key))
                                continue; // read in its place in the open row
                            else
                            {
                                // A typed template in a list's own resources
                                // draws the list's items, Profiles' cards: a
                                // menu in it is each item's own.
                                bool items = Local(r) == "DataTemplate" && Attr(r, "DataType") != null && HoldsItems(e.Parent);
                                Walk(r, ctx with { LeftOut = true, ItemsList = items ? NameOf(e.Parent) : ctx.ItemsList });
                            }
                        }
                        return;
                    }
                    var inner = ContentProperties.Contains(property) ? ctx : ctx with { LeftOut = true };
                    foreach (var c in e.Elements()) Walk(c, inner);
                    return;
                }

                if (ctx.LeftOut)
                {
                    if (IsReset(e)) _resets.Add((_page, LineOf(e), ResetOutcome.LeftOut));
                    foreach (var c in e.Elements()) Walk(c, ctx);
                    return;
                }

                // The controller page's sections: the tab control bound to the
                // selected section holds one TabItem per section, in order.
                if (_page == SearchPage.Pad && (Attr(e, "SelectedIndex") ?? string.Empty).Contains("SelectedSection", StringComparison.Ordinal))
                {
                    int section = 0;
                    foreach (var c in e.Elements())
                    {
                        if (Local(c) != "TabItem") { Walk(c, ctx with { LeftOut = IsProperty(c) && !ContentProperties.Contains(Local(c).Substring(Local(c).LastIndexOf('.') + 1)) }); continue; }
                        var inSection = ctx with
                        {
                            Section = section,
                            Tab = section == 0 ? 0 : -1,
                            // Macros & Menus edits the picked macro or menu: only
                            // its two parts are entries.
                            LeftOut = false,
                        };
                        if (section == ViewModels.PadViewModel.MacrosSection) WalkMacros(c, inSection);
                        else foreach (var k in c.Elements()) Walk(k, inSection);
                        section++;
                    }
                    return;
                }

                if (!entered) ctx = Enter(e, ctx);
                _current = e;
                if (Local(e) is "Button" or "ToggleButton" or "SplitButton" or "DropDownButton" or "RadioButton" || IsReset(e))
                    ctx = ctx with { MenuOwner = MenuOwnerOf(e) };

                // A part of the open row that a template of its own draws:
                // the template is read here, on the content the part gives
                // it, which is that template's data.
                if (ctx.RowOptions && local == "ContentControl" && RowPartOf(e) is { } part)
                {
                    var content = Binding.Parse(Attr(e, "Content"));
                    var inPart = ctx;
                    if (!ctx.DataUnread)
                    {
                        if (content == null || content.Elsewhere) inPart = ctx with { DataUnread = true };
                        else if (!string.IsNullOrEmpty(content.Path) && content.Path != ".") inPart = ctx with { DataPath = OnData(ctx, content.Path) };
                    }
                    foreach (var c in Kids(part)) Walk(c, inPart);
                    return;
                }

                if (IsCard(e))
                {
                    var title = local == "FoldCard"
                        ? (KeyIn(Attr(e, "Header")) is { } header ? new SearchLabel(header, null) : null)
                        : CardTitleOf(e);
                    if (title?.Path != null) title = ctx.DataUnread ? null : title with { Path = OnData(ctx, title.Path) };
                    ctx = ctx with { Card = new CardState { Title = title } };
                    if (title != null && !ctx.RowOptions)
                    {
                        if (ctx.ItemsPath != null && ctx.ItemQualifier == null && title.Path != null)
                            ctx = ctx with { ItemQualifier = title };
                        Add(ctx, SearchKind.Card, title, null, local == "FoldCard" ? SearchLabelSource.FoldHeader : SearchLabelSource.CardTitle);
                    }
                }
                else if (local == "TextBlock" && StyleOf(e) == "CardSubsectionTitle" && ctx.Card != null && TextLabel(e, false) is { Key: { } sub })
                {
                    ctx.Card.LastSubsection = sub;
                }

                if (IsReset(e))
                {
                    Setting(e, ctx);
                }
                else if (!ctx.RowOptions && (local == "Button" || (CatalogOnly && local.EndsWith("Button", StringComparison.Ordinal) && !IsReset(e)))
                         && ActionKey(e, CatalogOnly) is { } action)
                {
                    Add(ctx, SearchKind.Action, new SearchLabel(action, null), ActionQualifier(e, ctx), SearchLabelSource.Button);
                }
                else if (local == "ExtraTile" && _page == SearchPage.Extras && KeyIn(Attr(e, "Title")) is { } extraTitle)
                {
                    Add(ctx, SearchKind.Extra, new SearchLabel(extraTitle, null), null, SearchLabelSource.Tile, extraKey: Attr(e, "Key"));
                }
                else if (local == "RadioButton" && _page == SearchPage.Pad && StripEntry(e, ctx))
                {
                }

                // An item template: a card per item is read per item at query
                // time. Any other item template draws the user's own things.
                var itemTemplate = e.Elements().FirstOrDefault(c => IsProperty(c) && Local(c).EndsWith(".ItemTemplate", StringComparison.Ordinal));
                foreach (var c in e.Elements())
                {
                    if (c == itemTemplate)
                    {
                        var template = c.Elements().FirstOrDefault(t => Local(t) == "DataTemplate");
                        var root = template == null ? null : Kids(template).FirstOrDefault();
                        var source = Binding.Parse(Attr(e, "ItemsSource"));
                        // A menu in the template is each item's own.
                        string list = NameOf(e);
                        if (root != null && IsCard(root) && source != null && source.IsPlainPath)
                            Walk(root, ctx with { ItemsPath = source.Path, InTemplate = true, ItemQualifier = null, Gates = Array.Empty<SearchGate>(), ItemsList = list });
                        else
                            Walk(c, ctx with { LeftOut = true, ItemsList = list });
                        continue;
                    }
                    Walk(c, ctx);
                }
            }

            /// <summary>Macros &amp; Menus: its part switch, and nothing else.</summary>
            private void WalkMacros(XElement tabItem, Ctx ctx)
            {
                // The editor's own menus are entries, under the section's
                // class (#600). Everything else edits the picked macro.
                var asks = Enter(tabItem, ctx);
                foreach (var e in tabItem.Descendants())
                {
                    if (IsReset(e)) _resets.Add((_page, LineOf(e), ResetOutcome.LeftOut));
                    else if (Local(e) == "RadioButton") StripEntry(e, ctx);
                    else if (IsProperty(e) && Local(e).EndsWith(".ContextMenu", StringComparison.Ordinal))
                    {
                        // Each menu's items sit in their button's scope: the
                        // list's More and the open macro's More both hold a
                        // Copy of one command.
                        var owner = asks with { MenuOwner = MenuOwnerOf(e.Parent), ScopeName = NameOf(e.Parent) ?? asks.ScopeName };
                        foreach (var item in e.Descendants().Where(d => Local(d) == "MenuItem"))
                            if (KeyIn(Attr(item, "Header")) is { } header)
                                Add(Enter(item, owner), SearchKind.MenuItem, new SearchLabel(header, null), owner.MenuOwner, SearchLabelSource.Button, element: item);
                    }
                }
            }

            /// <summary>A section button of the header, or a part button of
            /// Buttons or Macros &amp; Menus.</summary>
            private bool StripEntry(XElement e, Ctx ctx)
            {
                if (!int.TryParse(Attr(e, "Tag"), out int tag)) return false;
                var text = e.Descendants().FirstOrDefault(c => Local(c) == "TextBlock");
                var label = text == null ? null : TextLabel(text, true);
                if (label == null) return false;
                if (StyleOf(e) == "SectionStripButton")
                {
                    Add(ctx with { Section = tag, Tab = -1, ScopeName = null }, SearchKind.Section, label, null, SearchLabelSource.Strip);
                    return true;
                }
                string group = Attr(e, "GroupName");
                if (group is "PadButtonsPart" or "PadMacrosPart")
                {
                    Add(ctx with { Section = ViewModels.PadViewModel.SectionOfTab(tag), Tab = tag, ScopeName = null, Card = null }, SearchKind.Part, label, null, SearchLabelSource.Strip);
                    return true;
                }
                return false;
            }

            /// <summary>The title of a CardBorder: its first CardTitle text,
            /// HeroCardTitle text for a part's card (a stick's, a trigger's,
            /// the sections pass's), or text in a title style the page bases
            /// on one of them, before any card inside it.</summary>
            private SearchLabel CardTitleOf(XElement card)
            {
                foreach (var e in Inside(card))
                    if (Local(e) == "TextBlock" && StyleOf(e) is { } style && _cardTitleStyles.Contains(style))
                        return TextLabel(e, true);
                return null;
            }

            /// <summary>An element's descendants in document order, stopping at
            /// nested cards and at property elements that are no content.</summary>
            private IEnumerable<XElement> Inside(XElement e)
            {
                foreach (var c in e.Elements())
                {
                    if (IsProperty(c) && !ContentProperties.Contains(Local(c).Substring(Local(c).LastIndexOf('.') + 1))) continue;
                    if (IsCard(c)) continue;
                    yield return c;
                    foreach (var d in Inside(c)) yield return d;
                }
            }

            /// <summary>A button's words when they are a string: its Content, or
            /// the first Content setter of its own style. Shared verbs, Reset
            /// All and the Extras pages' Back say nothing alone.</summary>
            /// <summary>The words a button's context menu is read under: its
            /// own words, its screen-reader name or its tooltip.</summary>
            private static SearchLabel MenuOwnerOf(XElement e)
            {
                if (KeyIn(Attr(e, "Content")) is { } key) return new SearchLabel(key, null);
                foreach (string attribute in new[] { "AutomationProperties.Name", "ToolTip" })
                    if (KeyIn(Attr(e, attribute)) is { } other) return new SearchLabel(other, null);
                return null;
            }

            private static string ActionKey(XElement e, bool inDialog = false)
            {
                if (StyleOf(e) == "ResetAllButton") return null;
                if ((Attr(e, "AutomationProperties.AutomationId") ?? string.Empty).StartsWith("Extras_Back_", StringComparison.Ordinal)) return null;
                string key = KeyIn(Attr(e, "Content"));
                if (key == null)
                {
                    var setter = e.Elements().Where(c => Local(c) == "Button.Style").SelectMany(s => s.Descendants())
                        .FirstOrDefault(s => Local(s) == "Setter" && (string)s.Attribute("Property") == "Content");
                    key = KeyIn((string)setter?.Attribute("Value"));
                }
                // An icon button of the catalog's own sources is named by its
                // screen-reader name (#600): the title bar's buttons.
                if (key == null && inDialog) key = KeyIn(Attr(e, "AutomationProperties.Name"));
                // A dialog's OK and Cancel are its answers, and are entries
                // of the dialog (#600).
                if (key == null || (!inDialog && key.StartsWith("Common_", StringComparison.Ordinal))) return null;
                return key;
            }

            private static XElement RowOf(XElement e)
            {
                var row = e.Parent;
                while (row != null && IsProperty(row)) row = row.Parent;
                return row;
            }

            /// <summary>A row's label: a label TextBlock bound to a string, else
            /// its first TextBlock bound to one, else a switch's words, else the
            /// reset's own label.</summary>
            private static (SearchLabel Label, SearchLabelSource Source, bool Sub) RowLabel(XElement row, XElement reset)
            {
                var kids = Kids(row).ToList();
                foreach (var k in kids)
                    if (Local(k) == "TextBlock" && StyleOf(k) is { } s && LabelStyles.Contains(s) && TextLabel(k, false) is { } styled)
                        return (styled, SearchLabelSource.Text, s == "DzSubLabel");
                foreach (var k in kids)
                    if (Local(k) == "TextBlock" && !(StyleOf(k) is { } passed && PassedOverLabels.Contains(passed)) && TextLabel(k, false) is { } text)
                        return (text, SearchLabelSource.Text, false);
                foreach (var k in kids)
                    if (Toggles.Contains(Local(k)) && KeyIn(Attr(k, "Content")) is { } toggle)
                        return (new SearchLabel(toggle, null), SearchLabelSource.Toggle, false);
                if (reset != null && KeyIn(Attr(reset, "SettingLabel")) is { } own)
                    return (new SearchLabel(own, null), SearchLabelSource.ResetLabel, false);
                return (null, SearchLabelSource.Text, false);
            }

            /// <summary>The label of a main row: a row whose label is styled
            /// DzLabel.</summary>
            private static SearchLabel MainRowLabel(XElement row)
            {
                foreach (var k in Kids(row))
                    if (Local(k) == "TextBlock" && StyleOf(k) is "DzLabel" or "RowOptionLabel" && TextLabel(k, false) is { } label)
                        return label;
                return null;
            }

            private void Setting(XElement reset, Ctx ctx)
            {
                var row = RowOf(reset);
                var (label, source, sub) = row == null ? (null, SearchLabelSource.Text, false) : RowLabel(row, reset);
                if (label == null)
                {
                    _resets.Add((_page, LineOf(reset), ResetOutcome.NoLabel));
                    return;
                }
                _resets.Add((_page, LineOf(reset), ResetOutcome.Entry));
                SearchLabel qualifier = ctx.ItemQualifier;
                if (qualifier == null && sub)
                {
                    for (var before = row.ElementsBeforeSelf().Reverse().FirstOrDefault(); before != null; before = before.ElementsBeforeSelf().LastOrDefault())
                    {
                        if (IsProperty(before)) continue;
                        if (MainRowLabel(before) is { } main) { qualifier = main; break; }
                    }
                }
                if (qualifier == null && ctx.Card?.LastSubsection is { } subsection)
                    qualifier = new SearchLabel(subsection, null);
                var value = ctx.DataUnread ? null : ValueOf(row);
                if (value?.Path != null) value = value with { Path = OnData(ctx, value.Path) };
                // The row's input carries the setting's screen-reader words
                // (#600). Where it has no help, its label's tooltip is it.
                var input = Kids(row).FirstOrDefault(k => Inputs.Contains(Local(k))) ?? reset;
                int at = _entries.Count;
                Add(ctx, SearchKind.Setting, label, qualifier, source, value, TargetOf(row), element: input);
                if (_entries[at].Help == null)
                {
                    var labelText = Kids(row).FirstOrDefault(k => Local(k) == "TextBlock" && TextLabel(k, false) is { } l && l.Equals(label));
                    if (labelText != null && WordsOf(labelText, "ToolTip") is { } tip) _entries[at] = _entries[at] with { Help = tip };
                }
            }

            private static SearchLabel ActionQualifier(XElement button, Ctx ctx)
            {
                if (ctx.ItemQualifier != null) return ctx.ItemQualifier;
                var row = RowOf(button);
                if (row != null && MainRowLabel(row) is { } rowLabel) return rowLabel;
                // A line that stands its words over its buttons, its label in
                // the grid's first column (the Controller Database row's More,
                // #600): the label of the grid the buttons sit in.
                for (var up = row?.Parent; up != null && Local(up) != "SettingItem"; up = up.Parent)
                    if (Local(up) == "Grid" && MainRowLabel(up) is { } gridLabel) return gridLabel;
                if (row != null && Kids(row).FirstOrDefault(k => Local(k) == "TextBlock" && StyleOf(k) == "DzSubLabel") is { } subLabel
                    && TextLabel(subLabel, false) is { } sub)
                    return sub;
                return ctx.Card?.LastSubsection is { } subsection ? new SearchLabel(subsection, null) : null;
            }

            /// <summary>How the row's value is read: its value box and unit,
            /// else its switch, else its picker.</summary>
            private static SearchValueSource ValueOf(XElement row)
            {
                var kids = Kids(row).ToList();
                var box = kids.FirstOrDefault(k => Local(k) == "TextBox" && StyleOf(k) == "DzValueEdit");
                if (box != null && Binding.Parse(Attr(box, "Text")) is { } b && b.IsPlainPath)
                {
                    var unit = kids.FirstOrDefault(k => Local(k) == "TextBlock" && StyleOf(k) == "DzPercent");
                    string unitKey = unit == null ? null : KeyIn(Attr(unit, "Text"));
                    string unitText = unit == null || unitKey != null ? null : Attr(unit, "Text");
                    if (unitText != null && unitText.StartsWith("{", StringComparison.Ordinal)) unitText = null;
                    return new SearchValueSource(b.Path, b.StringFormat, b.ConverterKey, unitKey, unitText, SearchValueKind.Number);
                }
                // A number box holds its number itself (#597: the settings
                // catalog reads and writes it, and Search shows it).
                var numberBox = kids.FirstOrDefault(k => Local(k) == "NumberBox");
                if (numberBox != null && Binding.Parse(Attr(numberBox, "Value")) is { } n && n.IsPlainPath)
                {
                    var unit = kids.FirstOrDefault(k => Local(k) == "TextBlock" && StyleOf(k) == "DzPercent");
                    string unitKey = unit == null ? null : KeyIn(Attr(unit, "Text"));
                    string unitText = unit == null || unitKey != null ? null : Attr(unit, "Text");
                    if (unitText != null && unitText.StartsWith("{", StringComparison.Ordinal)) unitText = null;
                    return new SearchValueSource(n.Path, n.StringFormat, n.ConverterKey, unitKey, unitText, SearchValueKind.Number);
                }
                foreach (var k in kids)
                {
                    if (Local(k) is "CheckBox" or "ToggleSwitch" && Binding.Parse(Attr(k, "IsChecked")) is { } on && on.IsPlainPath)
                        return new SearchValueSource(on.Path, null, on.ConverterKey, null, null, SearchValueKind.Switch);
                }
                var combo = kids.FirstOrDefault(k => Local(k) == "ComboBox");
                if (combo != null)
                {
                    var picked = Binding.Parse(Attr(combo, "SelectedItem") ?? Attr(combo, "SelectedValue") ?? Attr(combo, "SelectedIndex"));
                    return new SearchValueSource(picked?.Path, null, null, null, null, SearchValueKind.Choice);
                }
                return null;
            }

            private static string TargetOf(XElement row)
            {
                foreach (var k in Kids(row))
                {
                    if (!Inputs.Contains(Local(k))) continue;
                    string id = Attr(k, "AutomationProperties.AutomationId");
                    if (!string.IsNullOrEmpty(id) && !id.StartsWith("{", StringComparison.Ordinal)) return id;
                }
                return null;
            }
        }

        /// <summary>A binding written as markup, read without WPF: its path,
        /// its string format, its converter and the converter's parameter, and
        /// whether it reads the data (no Source, ElementName or
        /// RelativeSource).</summary>
        internal sealed class Binding
        {
            public string Path;
            public string StringFormat;
            public string ConverterKey;
            public string ConverterParameter;
            public bool IsPlainPath;
            /// <summary>The binding names its source: Source, ElementName or
            /// RelativeSource.</summary>
            public bool Elsewhere;

            internal static Binding Parse(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                text = text.Trim();
                if (!text.StartsWith("{Binding", StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal)) return null;
                string body = text.Substring("{Binding".Length, text.Length - "{Binding".Length - 1);
                var b = new Binding { IsPlainPath = true };
                foreach (string raw in SplitTop(body))
                {
                    string part = raw.Trim();
                    if (part.Length == 0) continue;
                    int eq = IndexOfTop(part, '=');
                    if (eq < 0) { b.Path ??= part; continue; }
                    string name = part.Substring(0, eq).Trim();
                    string value = part.Substring(eq + 1).Trim();
                    if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value.Substring(1, value.Length - 2);
                    switch (name)
                    {
                        case "Path": b.Path = value; break;
                        case "StringFormat": b.StringFormat = value.StartsWith("{}", StringComparison.Ordinal) ? value.Substring(2) : value; break;
                        case "Converter":
                            var m = StyleKey.Match(value);
                            b.ConverterKey = m.Success ? m.Groups[1].Value : null;
                            break;
                        case "ConverterParameter": b.ConverterParameter = value; break;
                        case "Source":
                        case "ElementName":
                        case "RelativeSource":
                            b.IsPlainPath = false;
                            b.Elsewhere = true;
                            break;
                    }
                }
                if (string.IsNullOrEmpty(b.Path)) b.IsPlainPath = false;
                return b;
            }

            private static IEnumerable<string> SplitTop(string s)
            {
                int depth = 0, start = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        yield return s.Substring(start, i - start);
                        start = i + 1;
                    }
                }
                yield return s.Substring(start);
            }

            private static int IndexOfTop(string s, char target)
            {
                int depth = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    else if (c == target && depth == 0) return i;
                }
                return -1;
            }
        }
    }
}
