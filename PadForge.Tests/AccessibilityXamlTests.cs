using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The screen-reader guard over PadForge.App's XAML. A static audit finds
    /// each control a screen reader would still announce without a usable
    /// name once AccessibleNames has named what it can, and holds every file
    /// and class at a ceiling. A new offender fails the suite, and each phase
    /// of the accessibility work lowers the ceilings it clears.
    ///
    /// <para>WPF names a control from AutomationProperties.Name, then
    /// LabeledBy, then the element's plain text
    /// (FrameworkElementAutomationPeer.GetNameCore), so on its own a button
    /// holding a Segoe MDL2 glyph is named with the glyph's code, a tooltip is
    /// only help text (GetHelpTextCore), and a TextBox never takes its
    /// placeholder. An item is named by its container's peer and then by the
    /// item's ToString(), and DisplayMemberPath does not change that
    /// (ItemAutomationPeer.GetNameCore, ContentControl.PrepareContentControl).
    /// AccessibleNames fills those gaps as controls load, and the audit counts
    /// a control as named when one of its rules reaches it: a button that
    /// shows no text (a glyph, or text set in an icon font) takes a string or
    /// TextBlock tooltip, an input takes the label beside it, then a string
    /// tooltip, then a TextBox's placeholder, and the items of a ListBox,
    /// ListView, ComboBox or plain ItemsControl take the text they show. What
    /// stays counted is what no rule names.</para>
    ///
    /// <para>UI built in C# (the sidebar controller cards, the Add Controller
    /// popup) is invisible here. tools/a11y_uia_audit.ps1 covers it at
    /// runtime.</para>
    ///
    /// <para>Set PADFORGE_A11Y_AUDIT_OUT to a file path to write every finding
    /// as JSON, with the measured ceiling table, for the next phase's work
    /// list.</para>
    /// </summary>
    public class AccessibilityXamlTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        // The counts measured after the names phase (2026-10-23). Counts, not
        // lines, since lines drift. Each phase lowers the ones it clears, and a
        // file or class missing here holds at zero.
        private static readonly Dictionary<(string File, string Class), int> Ceilings = new()
        {
        };

        // Findings that stay, each with its reason, matched by file, class and
        // identity (the tag with its AutomationId, x:Name or binding).
        private static readonly (string File, string Class, string Identity, string Reason)[] Allowlist =
        {
            ("PadForge.App/Views/ConfirmDialog.xaml", XamlA11yAudit.BtnNoTooltip, "Button|ActionButton",
                "The dialog sets the button's text in code (ActionButton.Content = actionText)."),
            ("PadForge.App/Views/DreamcastScreenDialog.xaml", XamlA11yAudit.BtnNoTooltip, "Button|ChooseProfilePictureButton",
                "The dialog gives the button a TextBlock of text in code."),
            ("PadForge.App/Views/KeyboardScreenDialog.xaml", XamlA11yAudit.BtnNoTooltip, "Button|ChooseProfilePictureButton",
                "The dialog gives the button a TextBlock of text in code."),
            ("PadForge.App/Views/IconPicker.xaml", XamlA11yAudit.BtnNoTooltip, "Button|ClearButton",
                "The picker sets the button's text in code (ClearButton.Content = clearLabel)."),
            ("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.ItemsUnnamed, "DataGrid|MappingDataGrid",
                "MappingItem.ToString reads each row's target and its source."),
            ("PadForge.App/Controls/CurveEditor.xaml", XamlA11yAudit.MouseOnly, "Canvas|ChartCanvas",
                "The curve editor takes keys itself: the arrows select and move a point, Insert adds one, Delete removes one."),
            ("PadForge.App/MainWindow.xaml", XamlA11yAudit.MouseOnly, "Border|AppBrandingBorder",
                "Dragging the title area moves the window. Alt+Space opens the window menu, which moves it from the keyboard."),
            ("PadForge.App/Views/ColorPickerControl.xaml", XamlA11yAudit.MouseOnly, "Canvas|SvCanvas",
                "The R, G, B and hex fields beside the canvas set the same color."),
            ("PadForge.App/Views/ColorPickerControl.xaml", XamlA11yAudit.MouseOnly, "Canvas|HueCanvas",
                "The R, G, B and hex fields beside the canvas set the same color."),
            ("PadForge.App/Views/DevicesPage.xaml", XamlA11yAudit.MouseOnly, "Border|PreviewMouseLeftButtonDown:DeviceCard_MouseDown",
                "Dragging a device card onto a slot assigns it. The slot toggles in the device's details assign it from the keyboard."),
            ("PadForge.App/Views/DevicesPage.xaml", XamlA11yAudit.MouseOnly, "Border|PreviewMouseLeftButtonUp:DeviceCard_MouseUp",
                "Dragging a device card onto a slot assigns it. The slot toggles in the device's details assign it from the keyboard."),
            ("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.MouseOnly, "Border|ConstantForcePadBorder",
                "The X and Y sliders and boxes beside the pad set the same force."),
            ("PadForge.App/Views/TouchpadGestureRecorderDialog.xaml", XamlA11yAudit.MouseOnly, "Canvas|DrawingCanvas",
                "A gesture is a drawn path: a controller's touchpad, a mouse, a pen or a finger draws it."),
            ("PadForge.App/Views/TouchpadOverlay.xaml", XamlA11yAudit.MouseOnly, "Border|ClickBar",
                "The overlay is an on-screen touchpad, a surface for the mouse, a pen or a finger."),
            ("PadForge.App/Views/TouchpadOverlay.xaml", XamlA11yAudit.MouseOnly, "Path|ResizeGrip",
                "The grip resizes the on-screen touchpad, a surface for the mouse, a pen or a finger."),
            ("PadForge.App/Views/ScreenOverlay.xaml", XamlA11yAudit.MouseOnly, "Path|ResizeGrip",
                "The grip sizes an overlay that shows a picture over a game. Reset Position on the Screen Overlay page brings it back from the keyboard."),
            ("PadForge.App/Views/ScreenOverlay.xaml", XamlA11yAudit.MouseOnly, "Border|Surface",
                "Dragging moves a zoomed picture in its window. The Pan boxes in the overlay's dialog set the same place from the keyboard."),
            ("PadForge.App/Views/OnScreenKeyboardWindow.xaml", XamlA11yAudit.MouseOnly, "Path|ResizeGrip",
                "The grip sizes the On-Screen Keyboard, a window that never takes focus. Its menu's Size box on the Menus tab sets the same size from the keyboard."),
        };

        private static bool Allowed(XamlA11yAudit.Finding f)
            => Allowlist.Any(a => a.File == f.File && a.Class == f.Class && a.Identity == f.Identity);

        [Fact]
        public void EveryFileAndClass_StaysAtItsCeiling()
        {
            var findings = XamlA11yAudit.Measure(RepoRoot());
            var counted = findings.Where(f => !Allowed(f)).ToList();
            var measured = counted.GroupBy(f => (f.File, f.Class)).ToDictionary(g => g.Key, g => g.Count());

            string dump = Environment.GetEnvironmentVariable("PADFORGE_A11Y_AUDIT_OUT");
            if (!string.IsNullOrEmpty(dump))
                WriteDump(dump, findings, measured);

            var over = new StringBuilder();
            var under = new StringBuilder();
            foreach (var (key, count) in measured.OrderBy(k => k.Key.File, StringComparer.Ordinal).ThenBy(k => k.Key.Class, StringComparer.Ordinal))
            {
                int ceiling = Ceilings.TryGetValue(key, out int c) ? c : 0;
                if (count > ceiling)
                {
                    over.AppendLine($"  {key.File} {key.Class}: {count}, ceiling {ceiling}");
                    foreach (var f in counted.Where(f => f.File == key.File && f.Class == key.Class).Take(15))
                        over.AppendLine($"    line {f.Line}: {f.Tag} {f.Key ?? f.Detail}");
                }
                else if (count < ceiling)
                    under.AppendLine($"  {key.File} {key.Class}: {ceiling} -> {count}");
            }
            foreach (var (key, ceiling) in Ceilings)
                if (ceiling > 0 && !measured.ContainsKey(key))
                    under.AppendLine($"  {key.File} {key.Class}: {ceiling} -> 0");

            Assert.True(over.Length == 0 && under.Length == 0,
                (over.Length > 0 ? "New screen-reader offenders. Name each control, or allowlist one with its reason:\n" + over : "")
                + (under.Length > 0 ? "Fixed findings. Lower these ceilings:\n" + under : ""));
        }

        [Fact]
        public void EveryPlainList_StaysOutOfTheTabOrder()
        {
            // App.xaml's implicit ItemsControl style takes plain lists out of the
            // Tab order. A list with a style of its own misses it, so that style
            // or the list sets Focusable.
            var app = XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "App.xaml"));
            bool implicitStyle = app.Descendants().Any(e => e.Name.LocalName == "Style"
                && (string)e.Attribute("TargetType") == "ItemsControl"
                && !e.Attributes().Any(a => a.Name.LocalName == "Key")
                && SetsFocusableFalse(e));
            Assert.True(implicitStyle, "App.xaml lost the implicit ItemsControl style that sets Focusable to False.");

            var stops = new List<string>();
            foreach (var (rel, text) in XamlA11yAudit.Files(RepoRoot()))
            {
                foreach (var list in XDocument.Parse(text, LoadOptions.SetLineInfo).Descendants().Where(e => e.Name.LocalName == "ItemsControl"))
                {
                    if (list.Attributes().Any(a => a.Name.LocalName == "Focusable"))
                        continue;
                    var inline = list.Elements().FirstOrDefault(c => c.Name.LocalName == "ItemsControl.Style")?.Elements().FirstOrDefault();
                    bool styled = list.Attributes().Any(a => a.Name.LocalName == "Style") || inline != null;
                    if (styled && !(inline != null && SetsFocusableFalse(inline)))
                        stops.Add($"{rel}:{((IXmlLineInfo)list).LineNumber}");
                }
            }
            Assert.True(stops.Count == 0, "Plain lists left in the Tab order by a style of their own: " + string.Join(", ", stops));
        }

        private static bool SetsFocusableFalse(XElement style)
            => style.Elements().Any(s => s.Name.LocalName == "Setter"
                && (string)s.Attribute("Property") == "Focusable" && (string)s.Attribute("Value") == "False");

        [Fact]
        public void EveryDialogWindow_TellsScreenReadersItIsADialog()
        {
            // AutomationProperties.IsDialog on the window's root makes its peer
            // report a dialog (UIA IsDialog), which screen readers announce as one.
            var views = Path.Combine(RepoRoot(), "PadForge.App", "Views");
            var missing = Directory.EnumerateFiles(views, "*Dialog.xaml")
                .Where(p => !XDocument.Load(p).Root.Attributes()
                    .Any(a => a.Name.LocalName == "AutomationProperties.IsDialog" && a.Value == "True"))
                .Select(Path.GetFileName)
                .ToList();
            Assert.True(missing.Count == 0, "Dialog windows without AutomationProperties.IsDialog=\"True\": " + string.Join(", ", missing));
        }

        [Fact]
        public void EachSettingsUninstallButton_IsNamedForItsCard()
        {
            // Five buttons on the Settings page show "Uninstall". Named alike,
            // they read the same in a screen reader's list of buttons, and Axe
            // fails focusable siblings with one name and type, so each one's
            // name puts its card's title before the word.
            var page = XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "Views", "SettingsPage.xaml"), LoadOptions.SetLineInfo);
            var buttons = page.Descendants().Where(e => e.Name.LocalName == "Button"
                && ((string)e.Attribute("Content") ?? "").Contains("Common_Uninstall", StringComparison.Ordinal)).ToList();
            Assert.Equal(5, buttons.Count);
            var titles = new List<string>();
            foreach (var b in buttons)
            {
                var parts = b.Elements().FirstOrDefault(e => e.Name.LocalName == "AutomationProperties.Name")
                    ?.Descendants().Where(e => e.Name.LocalName == "Binding").Select(e => (string)e.Attribute("Path")).ToList();
                Assert.True(parts is { Count: 2 } && parts[1] == "Common_Uninstall",
                    $"SettingsPage.xaml:{((IXmlLineInfo)b).LineNumber} has no card title in its name");
                titles.Add(parts[0]);
            }
            Assert.Equal(titles.Count, titles.Distinct().Count());
        }

        [Fact]
        public void EveryDialogWindow_ClosesOnEscape()
        {
            // A cancel button (IsCancel) takes Escape: WPF registers it as
            // the Escape access key, runs its Click, then
            // Window.DialogCancelCommand (Button.OnClick). A dialog without
            // one handles Escape in its own key handler.
            var views = Path.Combine(RepoRoot(), "PadForge.App", "Views");
            var missing = Directory.EnumerateFiles(views, "*Dialog.xaml")
                .Where(p => !File.ReadAllText(p).Contains("IsCancel=\"True\"", StringComparison.Ordinal)
                    && !(File.Exists(p + ".cs") && File.ReadAllText(p + ".cs").Contains("Key.Escape", StringComparison.Ordinal)))
                .Select(Path.GetFileName)
                .ToList();
            Assert.True(missing.Count == 0, "Dialog windows that Escape does not close: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryDialogWindow_PutsTheFocusInside_WhenItOpens()
        {
            // A dialog that leaves the focus on its window sends the first key
            // press nowhere. InitialFocus.First moves it to the first control
            // when a key press opened the dialog, and the rest focus a field
            // in code.
            var views = Path.Combine(RepoRoot(), "PadForge.App", "Views");
            var focusCall = new Regex(@"\.Focus\(\)|MoveFocus\(|Keyboard\.Focus\(");
            var missing = Directory.EnumerateFiles(views, "*Dialog.xaml")
                .Where(p => !File.ReadAllText(p).Contains("InitialFocus.First=\"True\"", StringComparison.Ordinal)
                    && !(File.Exists(p + ".cs") && focusCall.IsMatch(File.ReadAllText(p + ".cs"))))
                .Select(Path.GetFileName)
                .ToList();
            Assert.True(missing.Count == 0, "Dialog windows that open with the focus on the window: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryDataGridTemplateColumn_NamesItsCells()
        {
            // WPF names a cell from its content's plain text, and a template
            // column's ContentPresenter has none, so a screen reader heard
            // "Item: ..., Column Display Index: N"
            // (DataGridCellItemAutomationPeer.GetNameCore).
            var missing = new List<string>();
            foreach (var (rel, text) in XamlA11yAudit.Files(RepoRoot()))
            {
                foreach (var column in XDocument.Parse(text, LoadOptions.SetLineInfo).Descendants()
                    .Where(e => e.Name.LocalName == "DataGridTemplateColumn"))
                {
                    bool named = column.Elements().Where(e => e.Name.LocalName == "DataGridTemplateColumn.CellStyle")
                        .Descendants().Any(s => s.Name.LocalName == "Setter"
                            && ((string)s.Attribute("Property") ?? "").EndsWith("AutomationProperties.Name", StringComparison.Ordinal));
                    if (!named)
                        missing.Add($"{rel}:{((IXmlLineInfo)column).LineNumber}");
                }
            }
            Assert.True(missing.Count == 0, "Template columns whose cells read WPF's default name: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryNameBoundOutsideTheStringTable_ReadsEmptyForNull()
        {
            // AutomationProperties.Name refuses null, so a name bound to a
            // value that starts out null logged a binding error on every
            // launch: the web controller QR images and the 2D/3D toggle did.
            // TargetNullValue gives such a name the empty string. A binding to
            // the string table never yields null, and a MultiBinding with a
            // StringFormat always yields a string.
            var missing = new List<string>();
            foreach (var (rel, text) in XamlA11yAudit.Files(RepoRoot()))
            {
                foreach (var e in XDocument.Parse(text, LoadOptions.SetLineInfo).Descendants())
                {
                    string binding = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name")?.Value;
                    if (binding == null && e.Name.LocalName == "Setter"
                        && ((string)e.Attribute("Property") ?? "").EndsWith("AutomationProperties.Name", StringComparison.Ordinal))
                        binding = (string)e.Attribute("Value");
                    if (binding == null || !binding.StartsWith("{Binding", StringComparison.Ordinal)
                        || binding.Contains("Strings.Instance", StringComparison.Ordinal)
                        || binding.Contains("TargetNullValue", StringComparison.Ordinal))
                        continue;
                    missing.Add($"{rel}:{((IXmlLineInfo)e).LineNumber}");
                }
            }
            Assert.True(missing.Count == 0, "Names bound with no value for null: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryCopiedWpfUiTemplate_NamesThePackageVersionInUse()
        {
            // App.xaml copies wpf-ui templates and says from which version. A
            // package bump fails here until each copy is checked against the
            // new version and its note updated.
            string root = RepoRoot();
            string csproj = File.ReadAllText(Path.Combine(root, "PadForge.App", "PadForge.App.csproj"));
            string version = Regex.Match(csproj, "<PackageReference Include=\"WPF-UI\" Version=\"([^\"]+)\"").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(version));
            string app = File.ReadAllText(Path.Combine(root, "PadForge.App", "App.xaml"));
            var cited = Regex.Matches(app, @"Wpf\.Ui (\d+\.\d+\.\d+)").Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(cited);
            Assert.All(cited, v => Assert.Equal(version, v));
        }

        [Fact]
        public void EveryIconGlyph_IsAGlyphText()
        {
            // A TextBlock in an icon font reads its private-use character to a
            // screen reader. GlyphText draws the same and leaves the control
            // view. A TextBlock inside a ControlTemplate is hidden already
            // (TextBlockAutomationPeer.IsControlElementCore). wpf-ui's FontIcon
            // and SymbolIcon draw with a TextBlock built in code, outside any
            // template, so PadForge places their Decorative subclasses.
            var glyphStyles = new[] { "SectionGlyph", "StageGlyphText" };
            static bool IconFont(string font)
                => font.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || font.Contains("Fluent Icons", StringComparison.Ordinal);
            var plain = new List<string>();
            foreach (var (rel, text) in XamlA11yAudit.Files(RepoRoot()))
            {
                foreach (var tb in XDocument.Parse(text, LoadOptions.SetLineInfo).Descendants().Where(e => e.Name.LocalName == "TextBlock"))
                {
                    if (tb.Ancestors().Any(a => a.Name.LocalName == "ControlTemplate"))
                        continue;
                    string font = tb.Attributes().FirstOrDefault(a => a.Name.LocalName == "FontFamily")?.Value ?? "";
                    string style = tb.Attributes().FirstOrDefault(a => a.Name.LocalName == "Style")?.Value ?? "";
                    // An inline style based on a glyph style, or setting an
                    // icon font, draws a glyph too.
                    var inline = tb.Elements().FirstOrDefault(c => c.Name.LocalName == "TextBlock.Style")?.Elements().FirstOrDefault();
                    string basedOn = inline?.Attributes().FirstOrDefault(a => a.Name.LocalName == "BasedOn")?.Value ?? "";
                    bool inlineFont = inline != null && inline.Descendants().Any(s => s.Name.LocalName == "Setter"
                        && ((string)s.Attribute("Property") ?? "").EndsWith("FontFamily", StringComparison.Ordinal)
                        && IconFont((string)s.Attribute("Value") ?? ""));
                    if (IconFont(font) || inlineFont || glyphStyles.Any(s => style.Contains(s, StringComparison.Ordinal) || basedOn.Contains(s, StringComparison.Ordinal)))
                        plain.Add($"{rel}:{((IXmlLineInfo)tb).LineNumber}");
                }
                foreach (var icon in XDocument.Parse(text, LoadOptions.SetLineInfo).Descendants()
                    .Where(e => e.Name.LocalName is "FontIcon" or "SymbolIcon"))
                {
                    if (!icon.Ancestors().Any(a => a.Name.LocalName == "ControlTemplate"))
                        plain.Add($"{rel}:{((IXmlLineInfo)icon).LineNumber}");
                }
            }
            // UI built in C#: a TextBlock initializer that sets an icon font,
            // by name or through a variable named for it (mdl2).
            var app = Path.Combine(RepoRoot(), "PadForge.App");
            foreach (var path in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(app, path);
                if (rel.StartsWith("obj", StringComparison.Ordinal) || rel.StartsWith("bin", StringComparison.Ordinal))
                    continue;
                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (Regex.IsMatch(lines[i], @"new (Wpf\.Ui\.Controls\.)?(FontIcon|SymbolIcon)\b"))
                    {
                        plain.Add($"PadForge.App/{rel.Replace(Path.DirectorySeparatorChar, '/')}:{i + 1}");
                        continue;
                    }
                    if (!Regex.IsMatch(lines[i], @"new (System\.Windows\.Controls\.)?TextBlock\b"))
                        continue;
                    for (int k = i; k < Math.Min(lines.Length, i + 12); k++)
                    {
                        if (Regex.Match(lines[k], @"\bFontFamily\s*=\s*(.*)$") is { Success: true } m && IconFont(m.Groups[1].Value))
                        {
                            plain.Add($"PadForge.App/{rel.Replace(Path.DirectorySeparatorChar, '/')}:{i + 1}");
                            break;
                        }
                        // The initializer ends at its closing brace, on the
                        // same line or on a line of its own.
                        if (k == i ? lines[k].Contains('}') : lines[k].TrimStart().StartsWith('}'))
                            break;
                    }
                }
            }
            Assert.True(plain.Count == 0,
                "Icon glyphs a screen reader reads as a private-use character. Use GlyphText, DecorativeFontIcon or DecorativeSymbolIcon: " + string.Join(", ", plain));
        }

        [Fact]
        public void EveryAllowlistEntry_StillMatchesAFinding()
        {
            var findings = XamlA11yAudit.Measure(RepoRoot());
            var stale = Allowlist.Where(a => !findings.Any(f => f.File == a.File && f.Class == a.Class && f.Identity == a.Identity))
                .Select(a => $"  {a.File} {a.Class} {a.Identity}").ToList();
            Assert.True(stale.Count == 0, "Allowlist entries that match nothing. Remove them:\n" + string.Join("\n", stale));
        }

        private static void WriteDump(string path, List<XamlA11yAudit.Finding> findings,
            Dictionary<(string File, string Class), int> measured)
        {
            var table = new StringBuilder();
            foreach (var (key, count) in measured.OrderBy(k => k.Key.File, StringComparer.Ordinal).ThenBy(k => k.Key.Class, StringComparer.Ordinal))
                table.AppendLine($"            [(\"{key.File}\", XamlA11yAudit.{XamlA11yAudit.ConstantName(key.Class)})] = {count},");
            var doc = new
            {
                summary = findings.GroupBy(f => f.Class).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                ceilings = table.ToString(),
                findings,
                labels = XamlA11yAudit.Labels(XamlA11yAudit.Files(RepoRoot())),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ── The audit itself, on XAML written here ──

        private static List<XamlA11yAudit.Finding> Audit(string body, string resources = "")
        {
            string xaml =
                "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
                "             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
                "             xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\"\n" +
                "             xmlns:reset=\"clr-namespace:PadForge.Controls\" xmlns:common=\"clr-namespace:PadForge.Common\"\n" +
                "             xmlns:strings=\"clr-namespace:PadForge.Resources.Strings\">\n" +
                "  <UserControl.Resources>\n" + resources + "\n  </UserControl.Resources>\n" +
                body + "\n</UserControl>";
            return XamlA11yAudit.Measure(new[] { ("PadForge.App/Views/Probe.xaml", xaml) });
        }

        private const string Key = "{Binding Common_Close, Source={x:Static strings:Strings.Instance}}";

        [Fact]
        public void AButtonThatShowsNoText_IsNamedByAStringOrTextBlockTooltip_AndOnlyAnotherTooltipElementOrNoneIsCounted()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <Button Content=\"&#xE711;\" ToolTip=\"" + Key + "\"/>\n" +
                "  <Button Content=\"&#xE711;\" ToolTip=\"{Binding StatusText}\"/>\n" +
                "  <Button><TextBlock Text=\"&#xE700;\"/></Button>\n" +
                "  <Button Content=\"Close\"/>\n" +
                "  <Button Content=\"{Binding Label}\"/>\n" +
                "  <Button Content=\"&#xE711;\" AutomationProperties.Name=\"" + Key + "\"/>\n" +
                "  <Button Content=\"&#xE711;\"><Button.ToolTip><StackPanel><TextBlock Text=\"" + Key + "\"/></StackPanel></Button.ToolTip></Button>\n" +
                "  <Button><StackPanel><TextBlock Text=\"&#xE700;\"/><TextBlock Text=\"" + Key + "\"/></StackPanel></Button>\n" +
                "  <Button><TextBlock Text=\"{Binding RecordIcon}\" FontFamily=\"Segoe MDL2 Assets\"/></Button>\n" +
                "  <Button><TextBlock Text=\"{Binding RecordIcon}\" FontFamily=\"Segoe MDL2 Assets\"/><Button.ToolTip><TextBlock Text=\"{Binding RecordText}\"/></Button.ToolTip></Button>\n" +
                "  <Button Content=\"&#xE711;\"><Button.ToolTip><TextBlock Text=\"" + Key + "\"/></Button.ToolTip></Button>\n" +
                "</StackPanel>");
            Assert.Equal(new[] { XamlA11yAudit.BtnNoTooltip, XamlA11yAudit.BtnTooltipElement, XamlA11yAudit.BtnNoTooltip }, f.Select(x => x.Class));
            Assert.Equal(new[] { 12, 16, 18 }, f.Select(x => x.Line));
        }

        [Fact]
        public void AStyleNamesAButtonThroughBasedOn_AsDoesATooltipItSets()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <Button Style=\"{StaticResource Tight}\" Content=\"&#xE711;\"/>\n" +
                "  <Button Style=\"{StaticResource Stateful}\" Content=\"&#xE7E8;\"/>\n" +
                "  <Button Style=\"{StaticResource Plain}\" Content=\"&#xE7E8;\"/>\n" +
                "</StackPanel>",
                "    <Style x:Key=\"Reset\" TargetType=\"Button\">\n" +
                "      <Setter Property=\"AutomationProperties.Name\" Value=\"{Binding ToolTip, RelativeSource={RelativeSource Self}}\"/>\n" +
                "    </Style>\n" +
                "    <Style x:Key=\"Tight\" TargetType=\"Button\" BasedOn=\"{StaticResource Reset}\"/>\n" +
                "    <Style x:Key=\"Stateful\" TargetType=\"Button\">\n" +
                "      <Style.Triggers><DataTrigger Binding=\"{Binding On}\" Value=\"True\"><Setter Property=\"ToolTip\" Value=\"{Binding OnText}\"/></DataTrigger></Style.Triggers>\n" +
                "    </Style>\n" +
                "    <Style x:Key=\"Plain\" TargetType=\"Button\"/>");
            var only = Assert.Single(f);
            Assert.Equal(XamlA11yAudit.BtnNoTooltip, only.Class);
            Assert.Equal(19, only.Line);
        }

        [Fact]
        public void ControlTemplateParts_AreLeftToTheControlThatOwnsThem()
        {
            var f = Audit(
                "<ContentControl>\n" +
                "  <ContentControl.Template><ControlTemplate TargetType=\"ContentControl\">\n" +
                "    <Button Content=\"&#xE711;\"/>\n" +
                "  </ControlTemplate></ContentControl.Template>\n" +
                "</ContentControl>");
            Assert.Empty(f);
        }

        [Fact]
        public void AnInput_IsNamedByTheLabelBesideIt_OrAboveItsRow_OrAResetButtonsSettingLabel()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <Grid>\n" +
                "    <TextBlock Grid.Row=\"0\" Text=\"{Binding Dash_Port, Source={x:Static strings:Strings.Instance}}\"/>\n" +
                "    <ui:NumberBox Grid.Row=\"0\" Value=\"{Binding Port}\"/>\n" +
                "    <ui:NumberBox Grid.Row=\"1\" Value=\"{Binding Other}\"/>\n" +
                "  </Grid>\n" +
                "  <StackPanel Orientation=\"Horizontal\">\n" +
                "    <ui:NumberBox Value=\"{Binding Range}\"/>\n" +
                "    <reset:SettingResetButton SettingLabel=\"{Binding Head_Range, Source={x:Static strings:Strings.Instance}}\"/>\n" +
                "  </StackPanel>\n" +
                "  <DockPanel>\n" +
                "    <reset:SettingResetButton DockPanel.Dock=\"Right\" SettingLabel=\"{Binding Macro_Name, Source={x:Static strings:Strings.Instance}}\"/>\n" +
                "    <ui:TextBox Text=\"{Binding Name}\"/>\n" +
                "  </DockPanel>\n" +
                "  <StackPanel Orientation=\"Horizontal\">\n" +
                "    <reset:SettingResetButton SettingLabel=\"{Binding Head_Range, Source={x:Static strings:Strings.Instance}}\"/>\n" +
                "    <Slider Value=\"{Binding Low}\"/>\n" +
                "    <Slider Value=\"{Binding High}\"/>\n" +
                "  </StackPanel>\n" +
                "  <StackPanel Orientation=\"Horizontal\">\n" +
                "    <TextBlock Text=\"R\"/>\n" +
                "    <Slider Value=\"{Binding Red}\"/>\n" +
                "    <TextBox Text=\"{Binding Red}\"/>\n" +
                "    <TextBlock Text=\"-\"/>\n" +
                "    <TextBox Text=\"{Binding Span}\"/>\n" +
                "  </StackPanel>\n" +
                "  <TextBox Text=\"{Binding After}\"/>\n" +
                "  <TextBlock Text=\"{Binding Pad_Range, Source={x:Static strings:Strings.Instance}, Converter={StaticResource Upper}}\"/>\n" +
                "  <StackPanel Orientation=\"Horizontal\">\n" +
                "    <Slider Value=\"{Binding Low2}\"/>\n" +
                "    <TextBox Text=\"{Binding Low2}\"/>\n" +
                "    <TextBlock Text=\"-\"/>\n" +
                "    <TextBox Text=\"{Binding High2}\"/>\n" +
                "  </StackPanel>\n" +
                "  <StackPanel Orientation=\"Horizontal\">\n" +
                "    <TextBox Text=\"{Binding Freq}\"/>\n" +
                "    <TextBlock Text=\"Hz\"/>\n" +
                "    <Slider Value=\"{Binding Gain}\"/>\n" +
                "  </StackPanel>\n" +
                "</StackPanel>");
            // The dash passes the R to the box after it. The box after the R
            // row follows a row holding inputs of its own, whose R names that
            // row, not the box. The range row's inputs take the label above
            // it. Hz is the frequency box's unit, not the gain slider's label.
            Assert.Equal(new[] { "{Binding Other}", "{Binding High}", "{Binding After}", "{Binding Freq}", "{Binding Gain}" },
                f.Select(x => x.Detail));
            Assert.All(f, x => Assert.Equal(XamlA11yAudit.InNothing, x.Class));
        }

        [Fact]
        public void AnInputWithoutALabel_IsNamedByAStringTooltipOrATextBoxPlaceholder()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <TextBox ToolTip=\"{Binding Help}\"/>\n" +
                "  <TextBox common:BidiText.ToolTip=\"{Binding Help}\"/>\n" +
                "  <ui:TextBox PlaceholderText=\"{Binding Hint}\"/>\n" +
                "  <ui:AutoSuggestBox PlaceholderText=\"{Binding Hint}\"/>\n" +
                "  <ComboBox SelectedItem=\"{Binding Mode}\"/>\n" +
                "  <Slider><Slider.ToolTip><TextBlock Text=\"{Binding Help}\"/></Slider.ToolTip></Slider>\n" +
                "  <Slider AutomationProperties.Name=\"" + Key + "\"/>\n" +
                "  <CheckBox/>\n" +
                "  <CheckBox ToolTip=\"" + Key + "\"/>\n" +
                "  <CheckBox Content=\"{Binding Label}\"/>\n" +
                "</StackPanel>");
            Assert.Equal(new[] { XamlA11yAudit.InPlaceholder, XamlA11yAudit.InNothing, XamlA11yAudit.InTooltipElement, XamlA11yAudit.ToggleNoText },
                f.Select(x => x.Class));
        }

        [Fact]
        public void OnlyListsWhoseItemsNoRuleReaches_AndImagesAndMouseHandlers_AreCounted()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <ListBox ItemsSource=\"{Binding Devices}\"/>\n" +
                "  <ComboBox ItemsSource=\"{Binding Peers}\" DisplayMemberPath=\"Name\" AutomationProperties.Name=\"" + Key + "\"/>\n" +
                "  <ItemsControl ItemsSource=\"{Binding Slots}\"/>\n" +
                "  <DataGrid ItemsSource=\"{Binding Rows}\"/>\n" +
                "  <TreeView ItemsSource=\"{Binding Nodes}\" DisplayMemberPath=\"Title\"/>\n" +
                "  <Image Source=\"logo.png\"/>\n" +
                "  <Image Source=\"qr.png\" AutomationProperties.Name=\"" + Key + "\"/>\n" +
                "  <Border MouseLeftButtonDown=\"Chip_Click\"/>\n" +
                "  <Button MouseLeftButtonDown=\"Ok_Down\" Content=\"OK\"/>\n" +
                "</StackPanel>");
            Assert.Equal(new[] { XamlA11yAudit.ItemsUnnamed, XamlA11yAudit.ItemsDisplayMemberPath, XamlA11yAudit.ImageNoName, XamlA11yAudit.MouseOnly },
                f.Select(x => x.Class));
            Assert.Equal("Border|MouseLeftButtonDown:Chip_Click", f[3].Identity);
        }
    }

    /// <summary>
    /// The static screen-reader audit: a port of the 2026-10-06 audit script
    /// (memory a11y-audit-tooling-scripts-2026-10-06). Keyed styles resolve
    /// through BasedOn, ControlTemplate parts are skipped, and each control
    /// without a name gets the fix class it needs and the Strings key to
    /// reuse.
    /// </summary>
    internal static class XamlA11yAudit
    {
        /// <summary>A button that shows no text and has no tooltip.</summary>
        internal const string BtnNoTooltip = "BTN_NO_TOOLTIP";
        /// <summary>A button that shows no text and whose tooltip is an
        /// element other than a TextBlock, which AccessibleNames does not
        /// read as a name.</summary>
        internal const string BtnTooltipElement = "BTN_TOOLTIP_ELEMENT";
        /// <summary>An input without a label beside it whose tooltip is an
        /// element.</summary>
        internal const string InTooltipElement = "IN_TOOLTIP_ELEMENT";
        /// <summary>An input without a label beside it whose placeholder
        /// AccessibleNames does not read: only a TextBox, NumberBox or
        /// PasswordBox gives its placeholder as a name.</summary>
        internal const string InPlaceholder = "IN_PLACEHOLDER";
        /// <summary>An input with no label beside it, no string tooltip and no
        /// placeholder.</summary>
        internal const string InNothing = "IN_NOTHING";
        /// <summary>A check box, radio button or switch with no text and no
        /// string tooltip.</summary>
        internal const string ToggleNoText = "TOGGLE_NO_TEXT";
        /// <summary>A DataGrid or TreeView bound to ItemsSource, whose items
        /// read their ToString(): AccessibleNames reaches ListBoxItem and
        /// ContentPresenter containers only.</summary>
        internal const string ItemsUnnamed = "ITEMS_UNNAMED";
        /// <summary>A DataGrid or TreeView with DisplayMemberPath: the items
        /// still read ToString().</summary>
        internal const string ItemsDisplayMemberPath = "ITEMS_DISPLAYMEMBERPATH";
        /// <summary>An image without a name.</summary>
        internal const string ImageNoName = "IMAGE_NO_NAME";
        /// <summary>A mouse handler on an element that is not a control.</summary>
        internal const string MouseOnly = "MOUSE_ONLY";

        internal sealed record Finding(string File, string Class, int Line, string Tag, string Key, string Detail, string Identity);

        internal static string ConstantName(string cls) => cls switch
        {
            BtnNoTooltip => nameof(BtnNoTooltip),
            BtnTooltipElement => nameof(BtnTooltipElement),
            InTooltipElement => nameof(InTooltipElement),
            InPlaceholder => nameof(InPlaceholder),
            InNothing => nameof(InNothing),
            ToggleNoText => nameof(ToggleNoText),
            ItemsUnnamed => nameof(ItemsUnnamed),
            ItemsDisplayMemberPath => nameof(ItemsDisplayMemberPath),
            ImageNoName => nameof(ImageNoName),
            MouseOnly => nameof(MouseOnly),
            _ => cls,
        };

        private static readonly HashSet<string> Buttons = new() { "Button", "ToggleButton", "RepeatButton", "HyperlinkButton", "Anchor", "SplitButton", "DropDownButton", "SegmentButton", "StateToggleButton", "AvailabilityButton" };
        private static readonly HashSet<string> Inputs = new() { "TextBox", "NumberBox", "PasswordBox", "ComboBox", "Slider", "AutoSuggestBox", "RichTextBox" };
        // The inputs the label scan passes over (AccessibleNames.IsInput): a
        // range slider sits between a label and the boxes that set its ends.
        private static readonly HashSet<string> ScanInputs = new(Inputs) { "RangeSlider" };
        // A panel or border, whose row the label scan climbs to when the input
        // inside it has no label of its own (AccessibleNames.FindLabel).
        private static readonly HashSet<string> RowParents = new() { "StackPanel", "DockPanel", "WrapPanel", "Grid", "UniformGrid", "Canvas", "VirtualizingStackPanel", "Border", "Viewbox", "SettingRow" };
        private static readonly HashSet<string> Toggles = new() { "CheckBox", "RadioButton", "ToggleSwitch" };
        // Lists whose item containers AccessibleNames.NameItem does not reach.
        private static readonly HashSet<string> Lists = new() { "DataGrid", "TreeView" };
        // The controls whose placeholder AccessibleNames.NameInput reads.
        private static readonly HashSet<string> PlaceholderNamed = new() { "TextBox", "NumberBox", "PasswordBox" };
        private static readonly string[] MouseEvents =
            { "MouseLeftButtonDown", "MouseLeftButtonUp", "MouseDown", "MouseUp", "PreviewMouseLeftButtonDown", "PreviewMouseDown", "PreviewMouseLeftButtonUp" };
        private static readonly string[] KeyEvents = { "KeyDown", "PreviewKeyDown", "KeyUp", "PreviewKeyUp" };
        private static readonly HashSet<string> Controlish = new(Buttons.Concat(Inputs).Concat(Toggles)
            .Concat(new[] { "MenuItem", "ListBoxItem", "ListViewItem", "TabItem", "Thumb", "ScrollBar" }));
        private static readonly Regex Pua = new(@"[\uE000-\uF8FF]");
        private static readonly Regex StrKey = new(@"\{Binding\s+(?:Path=)?(\w+),\s*Source=\{x:Static\s+\w+:Strings\.Instance\}");
        private static readonly Regex StyleRef = new(@"\{(?:StaticResource|DynamicResource)\s+(\w+)\}");

        private sealed class StyleInfo
        {
            public bool Name;
            public string Content;
            public string ToolTip;
            public bool ToolTipTrigger;
            public string BasedOn;
            public StyleInfo Copy() => (StyleInfo)MemberwiseClone();
        }

        /// <summary>Audits every XAML file under PadForge.App, build output
        /// left out, in ordinal path order.</summary>
        internal static List<Finding> Measure(string repoRoot) => Measure(Files(repoRoot));

        /// <summary>Every XAML file under PadForge.App, build output left
        /// out, in ordinal path order, with its repo-relative path.</summary>
        internal static List<(string Rel, string Text)> Files(string repoRoot)
        {
            string app = Path.Combine(repoRoot, "PadForge.App");
            return Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
                .Where(p => !Path.GetRelativePath(app, p).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(s => s == "obj" || s == "bin"))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (Path.GetRelativePath(repoRoot, p).Replace('\\', '/'), File.ReadAllText(p)))
                .ToList();
        }

        internal sealed record Labeled(string File, int Line, string Tag, string Label, bool Above);

        /// <summary>Every input without a name of its own that the label rules
        /// name, with the label it takes and whether that label sits above the
        /// input's row, for review in the audit dump.</summary>
        internal static List<Labeled> Labels(IReadOnlyList<(string Rel, string Text)> files)
        {
            var labeled = new List<Labeled>();
            foreach (var (rel, text) in files)
            {
                var root = XDocument.Parse(text, LoadOptions.SetLineInfo).Root;
                foreach (var n in root.DescendantsAndSelf())
                {
                    if (!Inputs.Contains(Local(n)) || n.Ancestors().Any(a => Local(a) == "ControlTemplate")
                        || AttrAuto(n, "Name") != null || AttrAuto(n, "LabeledBy") != null)
                        continue;
                    string own = SiblingLabel(n, out _);
                    string label = own ?? LabelOf(n);
                    if (label != null)
                        labeled.Add(new Labeled(rel, ((IXmlLineInfo)n).LineNumber, Local(n), label, own == null));
                }
            }
            return labeled;
        }

        internal static List<Finding> Measure(IReadOnlyList<(string Rel, string Text)> files)
        {
            var trees = files.Select(f => (f.Rel, Root: XDocument.Parse(f.Text, LoadOptions.SetLineInfo).Root)).ToList();

            // The first definition of a key wins, as the files are ordered.
            var keyed = new Dictionary<string, StyleInfo>(StringComparer.Ordinal);
            foreach (var (_, root) in trees)
                foreach (var e in root.DescendantsAndSelf())
                    if (Local(e) == "Style" && Attr(e, "Key") is { Length: > 0 } k && !keyed.ContainsKey(k))
                        keyed[k] = StyleInfoOf(e);

            var findings = new List<Finding>();
            foreach (var (rel, root) in trees)
            {
                foreach (var n in root.DescendantsAndSelf())
                {
                    if (n.Ancestors().Any(a => Local(a) == "ControlTemplate"))
                        continue;
                    string tag = Local(n);
                    int line = ((IXmlLineInfo)n).LineNumber;
                    StyleInfo st = null;
                    bool named = false;
                    if (Buttons.Contains(tag) || Inputs.Contains(tag) || Toggles.Contains(tag))
                    {
                        st = EffectiveStyle(n, keyed);
                        named = HasName(n, st);
                    }

                    void Add(string cls, string key, string detail, string extra)
                        => findings.Add(new Finding(rel, cls, line, tag, key, detail, Identity(n, extra)));

                    // A button that shows no text is named by a string tooltip
                    // (AccessibleNames.NameButton), so only no tooltip or a
                    // tooltip element leaves it unnamed.
                    if (Buttons.Contains(tag) && !named && string.IsNullOrEmpty(TextOf(n, st)))
                    {
                        var (tt, src) = ToolTipOf(n, st);
                        if (tt == null || src == "element")
                            Add(tt == null ? BtnNoTooltip : BtnTooltipElement, null, tt, Attr(n, "Click") ?? Attr(n, "Command"));
                    }

                    // An input is named by the label beside it, then a string
                    // tooltip, then a TextBox's placeholder
                    // (AccessibleNames.NameInput).
                    if (Inputs.Contains(tag) && !named && LabelOf(n) == null)
                    {
                        var (tt, src) = ToolTipOf(n, st);
                        string ph = Attr(n, "PlaceholderText");
                        bool placeholderNames = !string.IsNullOrEmpty(ph) && PlaceholderNamed.Contains(tag);
                        // NameInput reads a string tooltip only, so a TextBlock
                        // tooltip leaves an input unnamed.
                        if ((tt == null || src is "element" or "element-text") && !placeholderNames)
                        {
                            string cls = tt != null ? InTooltipElement
                                : !string.IsNullOrEmpty(ph) ? InPlaceholder
                                : InNothing;
                            string bound = Attr(n, "Text") ?? Attr(n, "Value") ?? Attr(n, "SelectedItem") ?? Attr(n, "SelectedValue");
                            Add(cls, KeyOf(ph), tt ?? ph ?? bound, bound);
                        }
                    }

                    if (Toggles.Contains(tag) && !named && string.IsNullOrEmpty(TextOf(n, st)))
                    {
                        var (tt, src) = ToolTipOf(n, st);
                        if (tt == null || src == "element")
                            Add(ToggleNoText, null, null, Attr(n, "IsChecked"));
                    }

                    if (Lists.Contains(tag) && Attr(n, "ItemsSource") is { Length: > 0 } items)
                    {
                        // A name setter in the row or item container style
                        // names the items. One in a column's cell style names
                        // only the cells.
                        bool setter = n.Descendants().Any(c => Local(c) == "Setter"
                            && (Attr(c, "Property") ?? "").EndsWith("AutomationProperties.Name", StringComparison.Ordinal)
                            && c.Ancestors().TakeWhile(a => a != n).Any(a => Local(a).EndsWith(".RowStyle", StringComparison.Ordinal)
                                || Local(a).EndsWith(".ItemContainerStyle", StringComparison.Ordinal)));
                        if (!setter)
                            Add(string.IsNullOrEmpty(Attr(n, "DisplayMemberPath")) ? ItemsUnnamed : ItemsDisplayMemberPath, null, items, items);
                    }

                    if (tag == "Image" && AttrAuto(n, "Name") == null)
                        Add(ImageNoName, null, Attr(n, "Source"), Attr(n, "Source"));

                    // An element with a key handler of its own has a keyboard
                    // path beside its mouse one (the Workshop shelf opens a game
                    // on Enter, the macro steps move on Alt+Up and Alt+Down).
                    if (!Controlish.Contains(tag) && !KeyEvents.Any(ev => Attr(n, ev) is { Length: > 0 }))
                        foreach (string ev in MouseEvents)
                            if (Attr(n, ev) is { Length: > 0 } handler)
                                Add(MouseOnly, null, ev + ":" + handler, ev + ":" + handler);
                }
            }
            return findings;
        }

        private static string Local(XElement e) => e.Name.LocalName;

        private static string Attr(XElement e, string name)
            => e.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.LocalName == name)?.Value;

        private static string AttrAuto(XElement e, string prop)
            => e.Attributes().FirstOrDefault(a => a.Name.LocalName.EndsWith("AutomationProperties." + prop, StringComparison.Ordinal))?.Value;

        private static string OwnText(XElement e) => string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));

        private static string Row(XElement e) => Attr(e, "Grid.Row") is { Length: > 0 } r ? r : "0";

        private static string KeyOf(string text)
            => text != null && StrKey.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

        /// <summary>A line-independent handle for the allowlist: the tag with
        /// the AutomationId, the x:Name, or the element's own binding.</summary>
        private static string Identity(XElement n, string extra)
            => Local(n) + "|" + (AttrAuto(n, "AutomationId") ?? Attr(n, "Name") ?? extra ?? "");

        private static StyleInfo StyleInfoOf(XElement style)
        {
            var info = new StyleInfo();
            if (Attr(style, "BasedOn") is string bo && StyleRef.Match(bo) is { Success: true } m)
                info.BasedOn = m.Groups[1].Value;
            foreach (var c in style.Elements())
            {
                if (Local(c) == "Setter")
                {
                    string prop = Attr(c, "Property") ?? "";
                    string val = Attr(c, "Value");
                    if (prop.EndsWith("AutomationProperties.Name", StringComparison.Ordinal))
                        info.Name = true;
                    else if (prop == "Content" && val != null)
                        info.Content = val;
                    else if (prop == "ToolTip" && val != null)
                        info.ToolTip = val;
                }
                if (Local(c).EndsWith("Triggers", StringComparison.Ordinal)
                    && c.DescendantsAndSelf().Any(t => Local(t) == "Setter" && Attr(t, "Property") == "ToolTip"))
                    info.ToolTipTrigger = true;
            }
            return info;
        }

        private static StyleInfo Resolve(StyleInfo info, Dictionary<string, StyleInfo> keyed, int depth = 0)
        {
            var o = info.Copy();
            if (info.BasedOn != null && depth < 10 && keyed.TryGetValue(info.BasedOn, out var b))
            {
                var bs = Resolve(b, keyed, depth + 1);
                o.Name = o.Name || bs.Name;
                o.Content ??= bs.Content;
                o.ToolTip ??= bs.ToolTip;
                o.ToolTipTrigger = o.ToolTipTrigger || bs.ToolTipTrigger;
            }
            return o;
        }

        private static StyleInfo EffectiveStyle(XElement n, Dictionary<string, StyleInfo> keyed)
        {
            if (Attr(n, "Style") is string s && StyleRef.Match(s) is { Success: true } m && keyed.TryGetValue(m.Groups[1].Value, out var ks))
                return Resolve(ks, keyed);
            foreach (var c in n.Elements())
                if (Local(c).EndsWith(".Style", StringComparison.Ordinal))
                    foreach (var st in c.Elements())
                        if (Local(st) == "Style")
                            return Resolve(StyleInfoOf(st), keyed);
            return new StyleInfo();
        }

        private static bool HasName(XElement n, StyleInfo st)
        {
            if (AttrAuto(n, "Name") != null || AttrAuto(n, "LabeledBy") != null)
                return true;
            if (n.Elements().Any(c => Local(c) is "AutomationProperties.Name" or "AutomationProperties.LabeledBy"))
                return true;
            return st.Name;
        }

        /// <summary>The text a screen reader would take from the content, or
        /// null for a glyph, a blank or no content. Only the first content
        /// child is read.</summary>
        private static string TextOf(XElement n, StyleInfo st)
        {
            string content = Attr(n, "Content") ?? st.Content;
            if (content != null)
            {
                if (content.StartsWith("{", StringComparison.Ordinal))
                    return Pua.IsMatch(content) ? null : content;
                return Pua.IsMatch(content) || content.Trim().Length == 0 ? null : content;
            }
            foreach (var c in n.Elements())
            {
                if (Local(c).Contains('.'))
                    continue;
                foreach (var d in c.DescendantsAndSelf())
                {
                    // Text set in an icon font is a glyph, a bound one too: the
                    // record buttons bind a Record or Stop glyph.
                    if ((Local(d) is "TextBlock" or "Run" or "AccessText" or "Label") && !InIconFont(d, n))
                    {
                        string t = Attr(d, "Text");
                        if (string.IsNullOrEmpty(t))
                            t = OwnText(d).Trim();
                        if (t.Length > 0 && !Pua.IsMatch(t))
                            return t;
                    }
                }
                return null;
            }
            return null;
        }

        /// <summary>Whether an element's text is drawn in an icon font set on
        /// it or on an element between it and the control.</summary>
        private static bool InIconFont(XElement d, XElement control)
        {
            for (XElement e = d; e != null && e != control; e = e.Parent)
                if ((Attr(e, "FontFamily") ?? Attr(e, "TextElement.FontFamily")) is string font
                    && (font.Contains("MDL2 Assets", StringComparison.Ordinal) || font.Contains("Fluent Icons", StringComparison.Ordinal)))
                    return true;
            return false;
        }

        private static (string Text, string Source) ToolTipOf(XElement n, StyleInfo st)
        {
            // BidiText.ToolTip draws a string as a TextBlock tooltip, and
            // AccessibleNames reads the string from it.
            if ((Attr(n, "ToolTip") ?? Attr(n, "BidiText.ToolTip")) is string t)
                return (t, "attr");
            if (n.Elements().FirstOrDefault(c => Local(c).EndsWith(".ToolTip", StringComparison.Ordinal)) is XElement tip)
            {
                // A TextBlock tooltip with text of its own, written or bound
                // to the data, a source or Strings, names a button
                // (AccessibleNames.TipText). A binding to another element
                // is not followed, nor is any other tooltip element.
                if (tip.Elements().FirstOrDefault() is XElement first && Local(first) == "TextBlock"
                    && Attr(first, "Text") is { Length: > 0 } text
                    && !text.Contains("ElementName", StringComparison.Ordinal) && !text.Contains("RelativeSource", StringComparison.Ordinal))
                    return (text, "element-text");
                return ("(element)", "element");
            }
            if (st.ToolTip != null || st.ToolTipTrigger)
                return (st.ToolTip ?? "(trigger)", "style");
            return (null, null);
        }

        /// <summary>An input's label: the one beside it, else, while it sits
        /// first in a panel or border, the one beside that panel, up to three
        /// levels. AccessibleNames.FindLabel is the runtime twin.</summary>
        private static string LabelOf(XElement n)
        {
            XElement e = n;
            for (int level = 0; level <= 3; level++)
            {
                if (SiblingLabel(e, out bool open) is string label)
                    return label;
                if (!open || e.Parent is not { } p || !RowParents.Contains(Local(p)))
                    return null;
                e = p;
            }
            return null;
        }

        /// <summary>The label beside an element in its Grid row: the
        /// SettingLabel of the row's reset button first, later in the row (past
        /// inputs and values, not past another label) or docked ahead of the
        /// element with no input between them, then the nearest earlier sibling
        /// that holds a label, skipping other inputs so a slider's value box
        /// finds the slider's label, and literal text without a letter. Literal
        /// text right after an input is its unit and labels nothing. In a Grid,
        /// a row above holding a label and no input labels it last. Open is
        /// whether nothing before the element stopped the search.
        /// AccessibleNames.FindLabelAt is the runtime twin.</summary>
        private static string SiblingLabel(XElement n, out bool open)
        {
            open = false;
            var p = n.Parent;
            if (p == null)
                return null;
            var sibs = p.Elements().ToList();
            int idx = sibs.IndexOf(n);
            string myRow = Row(n);
            bool grid = Local(p) == "Grid";
            string before = null;
            string textLabel = null;
            bool passedInput = false;
            open = true;
            for (int i = idx - 1; i >= 0; i--)
            {
                var s = sibs[i];
                string tag = Local(s);
                if (tag.Contains('.'))
                    continue;
                if (grid && Row(s) != myRow)
                    continue;
                if (ScanInputs.Contains(tag))
                {
                    passedInput = true;
                    continue;
                }
                if (tag == "SettingResetButton")
                {
                    if (!passedInput && before == null && Attr(s, "SettingLabel") is { Length: > 0 } earlier)
                        before = earlier;
                    continue;
                }
                if (IsSeparator(s))
                    continue;
                open = false;
                if (LiteralText(s) != null && EarlierInRow(sibs, i, grid, myRow) is { } prior && ScanInputs.Contains(Local(prior)))
                    break;
                // A panel holding an input of its own is another control's
                // row (AccessibleNames.HoldsInput).
                if (!s.Descendants().Any(d => ScanInputs.Contains(Local(d))))
                    textLabel = FirstLabelIn(s);
                break;
            }
            for (int i = idx + 1; i < sibs.Count; i++)
            {
                var s = sibs[i];
                string tag = Local(s);
                if (tag.Contains('.'))
                    continue;
                if (grid && Row(s) != myRow)
                    continue;
                if (tag == "SettingResetButton" && Attr(s, "SettingLabel") is { Length: > 0 } label)
                    return label;
                if (ScanInputs.Contains(tag))
                    continue;
                // A label starts another control's row.
                if (FirstLabelIn(s) != null)
                    break;
            }
            if (before != null)
                return before;
            if (textLabel != null)
                return textLabel;
            if (open && grid && int.TryParse(myRow, out int rowIndex) && rowIndex > 0)
            {
                // The row above (AccessibleNames.RowAbove).
                string upper = (rowIndex - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var above = sibs.Where(x => !Local(x).Contains('.') && Row(x) == upper).ToList();
                if (above.Count > 0)
                {
                    open = false;
                    if (above.Any(x => ScanInputs.Contains(Local(x)) || x.Descendants().Any(d => ScanInputs.Contains(Local(d)))))
                        return null;
                    foreach (var x in above)
                        if (FirstLabelIn(x) is string text)
                            return text;
                }
            }
            return null;
        }

        /// <summary>The first TextBlock or Label in an element, in document
        /// order, whose text is a Strings key or a literal with a letter in it
        /// (AccessibleNames.FirstStringsLabel).</summary>
        private static string FirstLabelIn(XElement s)
        {
            foreach (var d in s.DescendantsAndSelf())
            {
                if (Local(d) is "TextBlock" or "Label")
                {
                    string t = Attr(d, "Text");
                    if (string.IsNullOrEmpty(t))
                        t = Attr(d, "Content");
                    // Text written between a TextBlock's tags becomes inlines
                    // and leaves its Text unset, while a Label's becomes its
                    // Content.
                    bool literal = !string.IsNullOrEmpty(t) || Local(d) == "Label";
                    if (string.IsNullOrEmpty(t))
                        t = OwnText(d).Trim();
                    // A Strings key, or a literal with a letter in it
                    // (AccessibleNames.IsLabelSource).
                    if (t.Length > 0 && !Pua.IsMatch(t) && t != "%"
                        && (StrKey.IsMatch(t) || (literal && !t.StartsWith("{", StringComparison.Ordinal) && t.Any(char.IsLetter))))
                        return t;
                }
            }
            return null;
        }

        /// <summary>A TextBlock or Label whose text is written in the XAML
        /// without a letter, such as the dash between two range boxes
        /// (AccessibleNames.IsSeparator).</summary>
        private static bool IsSeparator(XElement e)
            => LiteralText(e) is string text && !text.Any(char.IsLetter);

        /// <summary>Text a TextBlock or Label is given in the XAML itself, not
        /// through a binding, or null (AccessibleNames.LiteralText).</summary>
        private static string LiteralText(XElement e)
        {
            string text = Local(e) switch
            {
                "TextBlock" => Attr(e, "Text"),
                "Label" => Attr(e, "Content") ?? OwnText(e).Trim(),
                _ => null,
            };
            return text != null && !text.StartsWith("{", StringComparison.Ordinal) ? text : null;
        }

        /// <summary>The element sibling before index in the same Grid row,
        /// property elements left out, or null.</summary>
        private static XElement EarlierInRow(List<XElement> sibs, int index, bool grid, string row)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                if (Local(sibs[i]).Contains('.'))
                    continue;
                if (!grid || Row(sibs[i]) == row)
                    return sibs[i];
            }
            return null;
        }
    }
}
