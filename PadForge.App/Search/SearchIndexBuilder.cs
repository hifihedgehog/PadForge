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
    /// is a CardBorder with a CardTitle or a HeroCardTitle, or a FoldCard. An action is a button
    /// whose words are a string, less the shared verbs (Common_*), Reset All
    /// and the Extras pages' Back. Each extra's tile, each section of the
    /// controller page and each part of its Buttons and Macros &amp; Menus is
    /// one entry too.</para>
    ///
    /// <para>Left out on purpose: what a list of the user's own things draws
    /// (an item template whose root is no card), every keyed template but the
    /// mapping row's three, and the controller page's Macros &amp; Menus,
    /// which edits the picked macro or menu.</para>
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
        private static readonly HashSet<string> LabelStyles = new(StringComparer.Ordinal) { "DzLabel", "DzSubLabel", "RowOptionLabel", "EndsLabel" };
        private static readonly HashSet<string> Toggles = new(StringComparer.Ordinal) { "CheckBox", "ToggleSwitch", "RadioButton" };
        private static readonly HashSet<string> Inputs = new(StringComparer.Ordinal)
            { "Slider", "ComboBox", "CheckBox", "ToggleButton", "ToggleSwitch", "TextBox", "NumberBox", "RangeSlider", "CurveEditor", "RadioButton" };
        private static readonly HashSet<string> RowTemplates = new(StringComparer.Ordinal)
            { "MappingRowOptions", "MappingRowDeadzone", "MappingRowSource" };
        // Property elements whose content is part of the page. Every other
        // property element (styles, templates, triggers, columns, tooltips) is
        // read only for its reset buttons.
        private static readonly HashSet<string> ContentProperties = new(StringComparer.Ordinal)
            { "Header", "HeaderAction", "Content", "Child", "Children", "Items" };

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
            public SearchLabel ItemQualifier { get; init; }
            public IReadOnlyList<SearchGate> Gates { get; init; } = Array.Empty<SearchGate>();
            public CardState Card { get; init; }
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
            private int _order;

            public PageWalk(SearchPage page, IReadOnlyDictionary<string, int> tabOfGroupName, List<SearchEntry> entries,
                List<(SearchPage, int, ResetOutcome)> resets, XDocument doc)
            {
                _page = page;
                _tabOfGroupName = tabOfGroupName;
                _entries = entries;
                _resets = resets;
                _doc = doc;
                _extraKeys = new HashSet<string>(doc.Descendants().Where(e => e.Name.LocalName == "ExtraTile")
                    .Select(e => (string)e.Attribute("Key")).Where(k => !string.IsNullOrEmpty(k)), StringComparer.Ordinal);
            }

            public void Run() => Walk(_doc.Root, new Ctx());

            private static string Local(XElement e) => e.Name.LocalName;

            private static bool IsProperty(XElement e) => Local(e).Contains('.');

            private static string Attr(XElement e, string localName)
                => e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName && a.Name.Namespace != Xaml)?.Value;

            private static string NameOf(XElement e) => (string)e.Attribute(Xaml + "Name") ?? Attr(e, "Name");

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

            private static bool IsCard(XElement e) => (Local(e) == "Border" && StyleOf(e) == "CardBorder") || Local(e) == "FoldCard";

            private static bool IsReset(XElement e)
                => Local(e) == "SettingResetButton" || (Local(e) == "Button" && StyleOf(e) is { } s && ResetStyles.Contains(s));

            private static IEnumerable<XElement> Kids(XElement e) => e.Elements().Where(c => !IsProperty(c));

            private void Add(Ctx ctx, SearchKind kind, SearchLabel label, SearchLabel qualifier, SearchLabelSource source,
                SearchValueSource value = null, string target = null, string extraKey = null)
            {
                SearchLabel card = ctx.RowOptions ? null : ctx.Card?.Title;
                if (kind == SearchKind.Card || card == qualifier || (card != null && qualifier != null && card.Equals(qualifier)))
                    card = null;
                _entries.Add(new SearchEntry(kind, _page, label, qualifier, card, ctx.Section, ctx.Tab, extraKey ?? ctx.ExtraKey,
                    ctx.UnderMore, ctx.RowOptions, ctx.ScopeName, ctx.ItemsPath, ctx.Gates, value, target, source, _order++));
            }

            private Ctx Enter(XElement e, Ctx ctx)
            {
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

            private void Walk(XElement e, Ctx ctx)
            {
                string local = Local(e);
                if (IsProperty(e))
                {
                    string property = local.Substring(local.LastIndexOf('.') + 1);
                    if (property == "Resources")
                    {
                        foreach (var r in e.Elements())
                        {
                            string key = (string)r.Attribute(Xaml + "Key");
                            if (!ctx.LeftOut && Local(r) == "DataTemplate" && key != null && RowTemplates.Contains(key) && _page == SearchPage.Pad)
                                foreach (var c in Kids(r))
                                    Walk(c, new Ctx { Section = 0, Tab = ViewModels.PadViewModel.MappingsTabIndex, RowOptions = true, InTemplate = true });
                            else
                                Walk(r, ctx with { LeftOut = true });
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

                ctx = Enter(e, ctx);

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
                else if (!ctx.RowOptions && (local == "Button") && ActionKey(e) is { } action)
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
                        if (root != null && IsCard(root) && source != null && source.IsPlainPath)
                            Walk(root, ctx with { ItemsPath = source.Path, InTemplate = true, ItemQualifier = null, Gates = Array.Empty<SearchGate>() });
                        else
                            Walk(c, ctx with { LeftOut = true });
                        continue;
                    }
                    Walk(c, ctx);
                }
            }

            /// <summary>Macros &amp; Menus: its part switch, and nothing else.</summary>
            private void WalkMacros(XElement tabItem, Ctx ctx)
            {
                foreach (var e in tabItem.Descendants())
                {
                    if (IsReset(e)) _resets.Add((_page, LineOf(e), ResetOutcome.LeftOut));
                    else if (Local(e) == "RadioButton") StripEntry(e, ctx);
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
            /// or HeroCardTitle text for a part's card (a stick's, a
            /// trigger's, the sections pass's), before any card inside it.</summary>
            private static SearchLabel CardTitleOf(XElement card)
            {
                foreach (var e in Inside(card))
                    if (Local(e) == "TextBlock" && StyleOf(e) is "CardTitle" or "HeroCardTitle")
                        return TextLabel(e, true);
                return null;
            }

            /// <summary>An element's descendants in document order, stopping at
            /// nested cards and at property elements that are no content.</summary>
            private static IEnumerable<XElement> Inside(XElement e)
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
            private static string ActionKey(XElement e)
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
                if (key == null || key.StartsWith("Common_", StringComparison.Ordinal)) return null;
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
                    if (Local(k) == "TextBlock" && TextLabel(k, false) is { } text)
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
                Add(ctx, SearchKind.Setting, label, qualifier, source, value, TargetOf(row));
            }

            private static SearchLabel ActionQualifier(XElement button, Ctx ctx)
            {
                if (ctx.ItemQualifier != null) return ctx.ItemQualifier;
                var row = RowOf(button);
                if (row != null && MainRowLabel(row) is { } rowLabel) return rowLabel;
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
