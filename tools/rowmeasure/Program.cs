using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using IOPath = System.IO.Path;

// Width measurement for the fixed-size rows the 2026-09-15 audit flagged as
// possibly overflowing their containers. Renders the REAL controls with the
// app's implicit styles and the app fonts inside an off-screen window, then
// reads back each element's desired width, margin included, after the
// render. The sibling harness under tools/combomeasure does the same for the
// lighting pickers. This one covers the Profiles shortcut row, the raw hat
// strip, the equalizer row and its headings, the gesture recorder's hint and
// the pad page's first tier, in every language of Languages.tsv.
//
// No estimates. Every number printed is what the layout engine produced.

internal static class Program
{
    static readonly string ResxDir = FindResxDir();

    static string FindResxDir()
    {
        const string rel = @"PadForge.App\Resources\Strings";
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = IOPath.Combine(dir.FullName, rel);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Run from inside the repo tree.");
    }

    static readonly (string tag, string file)[] Locales = LoadLocales();

    // Every language in Languages.tsv, the table the app reads (#505), with
    // American English first as "en": the fallback for a string a partial
    // translation leaves out reads the English value, as the app shows it.
    static (string tag, string file)[] LoadLocales()
    {
        var tags = File.ReadAllLines(IOPath.Combine(ResxDir, "Languages.tsv"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('\t')[0])
            .ToList();
        if (!tags.Remove("en-US"))
            throw new InvalidOperationException("Languages.tsv has no en-US row.");
        return new[] { ("en", "Strings.resx") }
            .Concat(tags.Select(t => (t, "Strings." + t + ".resx")))
            .ToArray();
    }

    static Dictionary<string, Dictionary<string, string>> _strings;
    static FontFamily _bodyFont;

    // TelemetryFontFamily in App.xaml.
    static readonly FontFamily TelemetryFont = new("Cascadia Code, Consolas, Segoe UI");

    // The reset control is a fixed 28 by 28 with a 4 left margin (the shared
    // ember icon button style), so it costs 32 wherever it appears.
    const double ResetCost = 32;

    sealed class Probe
    {
        public string Group, Locale, Note;
        public FrameworkElement El;
        public double Declared;
    }

    static readonly List<Probe> _probes = new();

    static void LoadStrings()
    {
        _strings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (tag, file) in Locales)
        {
            string path = IOPath.Combine(ResxDir, file);
            if (!File.Exists(path)) continue;
            foreach (var d in XDocument.Load(path).Root.Elements("data"))
            {
                string key = (string)d.Attribute("name");
                string val = (string)d.Element("value");
                if (key == null || val == null) continue;
                if (!_strings.TryGetValue(key, out var byLocale))
                    _strings[key] = byLocale = new Dictionary<string, string>(StringComparer.Ordinal);
                byLocale[tag] = val;
            }
        }
    }

    // A string a partial translation leaves out shows in English.
    static string S(string key, string tag)
        => _strings.TryGetValue(key, out var m) && (m.TryGetValue(tag, out var v) || m.TryGetValue("en", out v)) ? v : null;

    static ComboBox Combo(IEnumerable<string> items)
    {
        var cb = new ComboBox();
        foreach (var i in items) cb.Items.Add(i);
        cb.SelectedIndex = 0;
        return cb;
    }

    static TextBlock Text(string s, double size = 13, FontWeight? weight = null)
        => new TextBlock { Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal };

    // The width a row pays for an element: its desired width, which carries
    // its margin. ActualWidth leaves the margin out.
    static double W(Probe p) => p.El.DesiredSize.Width;

    static void Add(string group, string locale, string note, FrameworkElement el, double declared)
    {
        // Left, not the vertical panel's default stretch: a stretched child
        // reports the PANEL's width back, which is not what any of these rows
        // would ask for.
        el.HorizontalAlignment = HorizontalAlignment.Left;
        el.VerticalAlignment = VerticalAlignment.Top;
        _probes.Add(new Probe { Group = group, Locale = locale, Note = note, El = el, Declared = declared });
    }

    [STAThread]
    static void Main()
    {
        LoadStrings();

        var app = new Application();
        app.Resources.MergedDictionaries.Add(
            new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

        var baseStyle = (Style)app.Resources["DefaultComboBoxStyle"];
        var comboStyle = new Style(typeof(ComboBox), baseStyle);
        comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        comboStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
        app.Resources[typeof(ComboBox)] = comboStyle;

        _bodyFont = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var panel = new StackPanel { Orientation = Orientation.Vertical };

        BuildProfilesRow();
        BuildHatStrip();
        BuildEqRow();
        BuildRecorderHint();
        BuildPadHeader();

        foreach (var p in _probes) panel.Children.Add(p.El);

        var win = new Window
        {
            Content = panel,
            FontFamily = _bodyFont,
            FontSize = 13.0,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000, Top = -32000,
            Width = 400, Height = 400,
        };
        win.ContentRendered += (_, __) =>
        {
            panel.UpdateLayout();
            Report();
            app.Shutdown();
        };
        win.Show();
        app.Run();
    }

    // ── C226: the Profiles page shortcut row ────────────────────────────
    //
    // ProfilesPage.xaml: star columns of 3*, 2*, 3* and * with floors of 120,
    // 120, 120 and 40, then the action cluster. The row sits in a card (16
    // padding, 1 border) inside the page scroller's 24 side padding. A mode
    // name wider than its picker is trimmed, with the full name on hover.
    const double ProfilesRowAtMinimum = 900 - 244 - 2 * 24 - 2 * 17;
    static Grid _profilesRow;

    static void BuildProfilesRow()
    {
        var modeKeys = new[]
        {
            "Profiles_ShortcutMode_Next", "Profiles_ShortcutMode_Previous",
            "Profiles_ShortcutMode_Specific", "Profiles_ShortcutMode_ToggleWindow",
            "Profiles_ShortcutMode_ToggleVCsDisabled",
        };
        // One picker per option. A closed picker is as wide as its selected
        // item, not its widest.
        foreach (var (tag, _) in Locales)
        {
            foreach (string key in modeKeys)
            {
                string mode = S(key, tag);
                if (mode != null) Add("profiles-mode", tag, key, Combo(new[] { mode }), 0);
            }
        }

        // The row itself at the 900 window minimum with the navigation pane
        // open, so the star columns resolve as WPF resolves them.
        _profilesRow = new Grid { Width = ProfilesRowAtMinimum };
        foreach (var (star, floor, cap) in new[]
                 { (3.0, 120.0, 328.0), (2.0, 120.0, 178.0), (3.0, 120.0, 328.0), (1.0, 40.0, double.PositiveInfinity) })
        {
            _profilesRow.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(star, GridUnitType.Star), MinWidth = floor, MaxWidth = cap,
            });
        }
        _profilesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new Border { Width = 3 * ResetCost };
        Grid.SetColumn(actions, 4);
        _profilesRow.Children.Add(actions);
        Add("profiles-row", "en", "the row at the window minimum", _profilesRow, 0);
    }

    // ── C228: the raw hat strip in the 340-wide device detail pane ───────
    //
    // DevicesPage.xaml: a horizontal stack of cells, each a 36 compass, then
    // the POV label and the hat's number in the telemetry font at 10 inside a
    // 6 left margin, the cell inside a 12 right margin.
    static void BuildHatStrip()
    {
        foreach (var (tag, _) in Locales)
        {
            string pov = S("Devices_POV", tag);
            if (pov == null) continue;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            label.Children.Add(new TextBlock { Text = pov, FontSize = 10, FontFamily = TelemetryFont });
            label.Children.Add(new TextBlock { Text = "3", FontSize = 10, FontFamily = TelemetryFont });
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            cell.Children.Add(new Canvas { Width = 36, Height = 36 });
            cell.Children.Add(label);
            Add("hat-cell", tag, "one hat cell plus its right margin", cell, 0);
        }
    }

    // ── C285: the equalizer row's fixed columns ──────────────────────────
    //
    // PadPage.xaml: columns of 78, 150, 108, 100 and 102, the same in the
    // heading grid and in each band's row. A band's first column holds a
    // checkbox and a reset, and each of the next four a control with a reset
    // docked right inside a 6 right margin. The headings are 11 px text with
    // no trimming, so a heading wider than its column is cut off.
    static readonly (string Key, double Width)[] EqColumns =
    {
        ("Pad_Audio_EqCol_On", 78), ("Pad_Audio_EqCol_Type", 150), ("Pad_Audio_EqCol_Freq", 108),
        ("Pad_Audio_EqCol_Gain", 100), ("Pad_Audio_EqCol_Q", 102),
    };

    static void BuildEqRow()
    {
        var checkAndReset = new StackPanel { Orientation = Orientation.Horizontal };
        checkAndReset.Children.Add(new CheckBox());
        Add("eq-col0", "en", "checkbox (a 32 reset sits beside it)", checkAndReset, 0);

        var typeKeys = new[]
        {
            "Pad_Audio_EqType_Peaking", "Pad_Audio_EqType_LowShelf",
            "Pad_Audio_EqType_HighShelf", "Pad_Audio_EqType_LowPass",
            "Pad_Audio_EqType_HighPass", "Pad_Audio_EqType_Notch",
        };
        // One picker per option, so an option too wide for its column is
        // named with its language, and its text alone, so the report can say
        // how much shorter it has to be.
        foreach (var (tag, _) in Locales)
        {
            foreach (string key in typeKeys)
            {
                string type = S(key, tag);
                if (type == null) continue;
                Add("eq-col1", tag, key, Combo(new[] { type }), 0);
                Add("eq-col1-text", tag, key, Text(type), 0);
            }
        }

        foreach (var (name, sample) in new[]
                 { ("eq-col2 frequency", "20000"), ("eq-col3 gain", "-24.0"), ("eq-col4 Q", "0.707") })
            Add(name, "en", "editor (a 32 reset docks right, 6 panel margin)", new TextBox { Text = sample }, 0);

        foreach (var (tag, _) in Locales)
        {
            foreach (var (key, column) in EqColumns)
            {
                string heading = S(key, tag);
                if (heading != null) Add("eq-head", tag, key, Text(heading, 11), column);
            }
        }
    }

    // ── C286: the recorder dialog's sample-count hint ────────────────────
    static void BuildRecorderHint()
    {
        foreach (var (tag, _) in Locales)
        {
            string label = S("Recorder_SampleCount_Label", tag);
            if (label != null) Add("recorder-label", tag, "label", Text(label), 0);
            string hint = S("Recorder_SampleCount_Hint", tag);
            if (hint != null) Add("recorder-hint", tag, "hint on one line", Text(hint, 12), 0);
        }
    }

    // ── C275 and #574: the pad page's header ────────────────────────────
    //
    // Line 1 is a grid of two columns. The type chip, the title and the
    // Preset chip wrap in the first, and the power button with the slot's
    // state word takes what it needs at the right. Line 2 holds Driven by,
    // the device picker and Add Device. The six sections wrap in a row of
    // their own. Buttons switches between its picture and its list, and
    // Macros & Menus between its two parts, each with a strip in the
    // content. Each piece is measured with the real font and the real
    // chrome. The header's own margin is 12 a side.
    static readonly string[] SectionKeys =
    {
        "Pad_Section_Buttons", "Pad_Section_Sticks", "Pad_Section_Rumble",
        "Pad_Section_Motion", "Pad_Section_Lights", "Pad_Section_Macros",
    };

    static readonly string[] ButtonsPartKeys = { "Pad_View_Picture", "Pad_View_List" };
    static readonly string[] MacrosPartKeys = { "Pad_Macros", "Pad_Menus" };

    // The words the power button shows (InputService sets the slot's status
    // text to one of these).
    static readonly string[] StateKeys =
    {
        "Common_Disabled", "Main_Initializing", "Main_VcFailed",
        "Status_NoMapping", "Main_Active", "Common_Idle",
    };

    // DisplayFontFamily in App.xaml.
    static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    static void BuildPadHeader()
    {
        // Type chip and title: a 34 square in a 10 right margin, then the
        // output type's name at 26 in the display face at weight 650 with
        // its instance number at 15 in the telemetry face. The longest type
        // name is the one that sizes it.
        var typeKeys = new[]
        {
            "ControllerType_Xbox", "ControllerType_PlayStation",
            "ControllerType_Nintendo", "ControllerType_Extended",
            "ControllerType_KeyboardMouse", "ControllerType_MIDI",
            "ControllerType_VR",
        };
        foreach (var (tag, _) in Locales)
        {
            foreach (string key in typeKeys)
            {
                string name = S(key, tag);
                if (name == null) continue;
                var title = new TextBlock
                {
                    FontFamily = DisplayFont,
                    FontSize = 26,
                    FontWeight = FontWeight.FromOpenTypeWeight(650),
                };
                title.Inlines.Add(new System.Windows.Documents.Run(name));
                title.Inlines.Add(new System.Windows.Documents.Run(" #16")
                {
                    FontFamily = TelemetryFont,
                    FontSize = 15,
                    FontWeight = FontWeights.Normal,
                });
                Add("hdr-title", tag, key, title, 0);
            }
        }

        // Preset chip: ChipGhost (9,3 padding, 1 border) in a 10,4,4,4
        // margin, holding the word Preset in a 6 right margin, the profile
        // combo at its 380 cap and a reset in a 4,0,8,0 margin.
        foreach (var (tag, _) in Locales)
        {
            string label = S("Pad_Preset", tag);
            if (label == null) continue;
            var word = Text(label, 12.5);
            word.Margin = new Thickness(0, 0, 6, 0);
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(word);
            inner.Children.Add(new Border { Width = 380 });
            inner.Children.Add(new Border { Width = 28, Margin = new Thickness(4, 0, 8, 0) });
            Add("hdr-preset", tag, "preset chip at the combo's 380 cap", new Border
            {
                Margin = new Thickness(10, 4, 4, 4),
                Padding = new Thickness(9, 3, 9, 3),
                BorderThickness = new Thickness(1),
                Child = inner,
            }, 0);
            // The same chip with its combo at nothing: the label, the reset
            // and the chrome, which is what the chip keeps when its line is
            // short and the combo gives way.
            var floor = new StackPanel { Orientation = Orientation.Horizontal };
            var floorWord = Text(label, 12.5);
            floorWord.Margin = new Thickness(0, 0, 6, 0);
            floor.Children.Add(floorWord);
            floor.Children.Add(new Border { Width = 28, Margin = new Thickness(4, 0, 8, 0) });
            Add("hdr-preset-floor", tag, "preset chip without its combo", new Border
            {
                Margin = new Thickness(10, 4, 4, 4),
                Padding = new Thickness(9, 3, 9, 3),
                BorderThickness = new Thickness(1),
                Child = floor,
            }, 0);
        }

        // Power button: a pill with a 1 border and 10,0,12,0 padding in a
        // 12 left margin, holding a 14 flame in an 8 right margin and the
        // state word at 13 SemiBold.
        foreach (var (tag, _) in Locales)
        {
            foreach (string key in StateKeys)
            {
                string word = S(key, tag);
                if (word == null) continue;
                var inner = new StackPanel { Orientation = Orientation.Horizontal };
                inner.Children.Add(new Canvas { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0) });
                inner.Children.Add(Text(word, 13, FontWeights.SemiBold));
                Add("hdr-power", tag, key, new Border
                {
                    Margin = new Thickness(12, 0, 0, 0),
                    Padding = new Thickness(10, 0, 12, 0),
                    BorderThickness = new Thickness(1),
                    Child = inner,
                }, 0);
            }
        }

        // Driven by at 13 in a 4 right margin, and the real Add Device
        // button in its 8,2,0,2 margin. The picker between them has a floor
        // of 200 and a ceiling of 320 in a 4,2 margin.
        foreach (var (tag, _) in Locales)
        {
            string driven = S("Pad_DrivenBy", tag);
            if (driven != null)
            {
                var label = Text(driven, 13);
                label.Margin = new Thickness(0, 0, 4, 0);
                Add("hdr-driven", tag, "Driven by", label, 0);
            }
            string add = S("Pad_AddDevice", tag);
            if (add != null)
                Add("hdr-add", tag, "Add Device", new Wpf.Ui.Controls.Button
                {
                    Content = add,
                    Margin = new Thickness(8, 2, 0, 2),
                }, 0);
        }

        // Six sections. Each is a RadioButton whose text has a 0,10 margin,
        // in a 26 right margin. The checked section's text is SemiBold, so
        // each is measured both ways.
        foreach (var (tag, _) in Locales)
        {
            foreach (string key in SectionKeys)
            {
                string t = S(key, tag);
                if (t == null) continue;
                Add("hdr-section-part", tag, key, Section(t, FontWeights.Normal), 0);
                Add("hdr-section-bold", tag, key, Section(t, FontWeights.SemiBold), 0);
            }
        }

        // The part switches of Buttons and of Macros & Menus. Each part is a
        // RadioButton with a 12,5,12,7 content margin and a 2,4 outer
        // margin, its text at 13.
        foreach (var (tag, _) in Locales)
        {
            foreach (var (group, keys) in new[] { ("hdr-parts-buttons", ButtonsPartKeys), ("hdr-parts-macros", MacrosPartKeys) })
            {
                foreach (string key in keys)
                {
                    string t = S(key, tag);
                    if (t == null) continue;
                    Add(group, tag, key, Part(t, FontWeights.Normal), 0);
                    Add(group + "-bold", tag, key, Part(t, FontWeights.SemiBold), 0);
                }
            }
        }
    }

    static Border Section(string text, FontWeight weight) => new Border
    {
        Margin = new Thickness(0, 0, 26, 0),
        Child = new Border { Margin = new Thickness(0, 10, 0, 10), Child = Text(text, 14, weight) },
    };

    static Border Part(string text, FontWeight weight) => new Border
    {
        Padding = new Thickness(12, 5, 12, 7),
        Margin = new Thickness(2, 4, 2, 4),
        Child = Text(text, 13, weight),
    };

    static void Report()
    {
        double Max(string group) => _probes.Where(p => p.Group == group)
                                           .Select(W).DefaultIfEmpty(0).Max();
        string Worst(string group)
        {
            double m = Max(group);
            return _probes.Where(p => p.Group == group && Math.Abs(W(p) - m) < 0.01)
                          .Select(p => p.Locale).FirstOrDefault() ?? "";
        }
        string Verdict(bool fits) => fits ? "  fits" : "  OVERFLOWS";

        Console.WriteLine("== Profiles shortcut row (window minimum 900, pane open) ==");
        var columns = _profilesRow.ColumnDefinitions;
        double floors = columns.Take(4).Sum(c => c.MinWidth) + 3 * ResetCost;
        double modePicker = columns[0].ActualWidth - 6 - ResetCost;
        double widestMode = Max("profiles-mode");
        Console.WriteLine($"  row width                      : {ProfilesRowAtMinimum:F1}");
        Console.WriteLine($"  column floors plus actions     : {floors:F1}" + Verdict(floors <= ProfilesRowAtMinimum));
        Console.WriteLine($"  mode picker                    : {modePicker:F1}");
        Console.WriteLine($"  widest mode option             : {widestMode:F1} (worst {Worst("profiles-mode")})"
                          + (widestMode > modePicker ? "  trimmed, full name on hover" : "  fits"));
        Console.WriteLine();

        // DevicesPage.xaml wraps the cells, so a row holds what fits and the
        // rest start a new row. The last cell's 12 right margin is empty, so it
        // may pass the edge.
        Console.WriteLine("== Raw hat strip (detail pane 340) ==");
        const double usablePane = 340 - 14 - 14 - 14;
        double one = Max("hat-cell");
        Console.WriteLine($"  one hat cell                   : {one:F1} (worst {Worst("hat-cell")})" + Verdict(one - 12 <= usablePane));
        Console.WriteLine($"  usable content width           : {usablePane:F1}");
        Console.WriteLine($"  hats on one row                : {Math.Floor((usablePane + 12) / one)}, the rest wrap");
        Console.WriteLine();

        Console.WriteLine("== Equalizer row columns ==");
        double col0 = Max("eq-col0") + ResetCost;
        Console.WriteLine($"  col 0 checkbox plus reset      : {col0:F1}  column 78" + Verdict(col0 <= 78));
        double col1 = Max("eq-col1") + ResetCost + 6;
        Console.WriteLine($"  col 1 type picker plus reset   : {col1:F1}  column 150 (worst {Worst("eq-col1")})"
                          + Verdict(col1 <= 150));
        foreach (var (group, column) in new[]
                 { ("eq-col2 frequency", 108.0), ("eq-col3 gain", 100.0), ("eq-col4 Q", 102.0) })
        {
            double need = Max(group) + ResetCost + 6;
            Console.WriteLine($"  {group,-22}         : {need:F1}  column {column}" + Verdict(need <= column));
        }
        foreach (var (key, column) in EqColumns)
        {
            var heads = _probes.Where(p => p.Group == "eq-head" && p.Note == key).ToList();
            double widest = heads.Max(W);
            string worst = heads.First(p => Math.Abs(W(p) - widest) < 0.01).Locale;
            Console.WriteLine($"  heading {key,-22} : {widest:F1}  column {column} (worst {worst})"
                              + Verdict(widest <= column));
        }
        // Every option and heading wider than its column: the strings to
        // shorten, each with the width of its text alone and the share of it
        // that has to go.
        double TextOf(Probe picker) => W(_probes.First(p => p.Group == "eq-col1-text"
                                                            && p.Locale == picker.Locale && p.Note == picker.Note));
        var wide = _probes.Where(p => p.Group == "eq-col1" && W(p) + ResetCost + 6 > 150)
            .Select(p => $"[{p.Locale}] {p.Note}  {W(p) + ResetCost + 6:F1} > 150"
                         + $"  text {TextOf(p):F1}, {100 * (W(p) + ResetCost + 6 - 150) / TextOf(p):F0} percent too wide")
            .Concat(_probes.Where(p => p.Group == "eq-head" && W(p) > p.Declared)
                .Select(p => $"[{p.Locale}] {p.Note}  {W(p):F1} > {p.Declared}"))
            .ToList();
        Console.WriteLine($"  strings wider than their column: {wide.Count}");
        foreach (string w in wide) Console.WriteLine("    " + w);
        Console.WriteLine();

        Console.WriteLine("== Gesture recorder sample-count row (dialog 640, not resizable) ==");
        const double usableRow = 640 - 32;
        double fixedPart = Max("recorder-label") + 8 + 60 + ResetCost + 8 + 12;
        Console.WriteLine($"  label plus picker plus reset   : {fixedPart:F1} (worst {Worst("recorder-label")})"
                          + Verdict(fixedPart < usableRow));
        Console.WriteLine($"  hint on one line               : {Max("recorder-hint"):F1} (worst {Worst("recorder-hint")})");
        Console.WriteLine($"  width the hint wraps into      : {usableRow - fixedPart:F1}");
        Console.WriteLine();

        // ── C275 and #574 ──
        // The header's margin takes 12 a side. At the 900 window minimum
        // with the navigation pane open (244) a line holds 632, with the
        // compact pane (48) 828, and in the default 1100 window with the
        // pane open 832.
        Console.WriteLine("== Pad page header (window minimum 900) ==");
        const double chip = 34 + 10;
        double title = Max("hdr-title"), preset = Max("hdr-preset"), presetFloor = Max("hdr-preset-floor");
        double power = Max("hdr-power");
        double widestSection = Max("hdr-section-bold");
        Console.WriteLine($"  type chip                      : {chip:F1}");
        Console.WriteLine($"  title                          : {title:F1} (worst {Worst("hdr-title")})");
        Console.WriteLine($"  preset chip at its 380 cap     : {preset:F1} (worst {Worst("hdr-preset")})");
        Console.WriteLine($"  preset chip without its combo  : {presetFloor:F1} (worst {Worst("hdr-preset-floor")})");
        Console.WriteLine($"  power button, word uncapped    : {power:F1} (worst {Worst("hdr-power")})");
        // The button's word is capped at 196 and trims past it. The chrome
        // around the word is the button's width less the word's.
        const double wordCap = 196, powerChrome = 12 + 2 + 22 + 22;
        foreach (string key in StateKeys)
        {
            var over = _probes.Where(p => p.Group == "hdr-power" && p.Note == key && W(p) - powerChrome > wordCap)
                              .Select(p => p.Locale).ToList();
            double widest = _probes.Where(p => p.Group == "hdr-power" && p.Note == key).Select(p => W(p) - powerChrome).DefaultIfEmpty(0).Max();
            Console.WriteLine($"    {key,-18} widest word {widest,6:F1}, trims in {over.Count}"
                              + (over.Count > 0 ? "  " + string.Join(" ", over) : ""));
        }
        power = Math.Min(power, wordCap + powerChrome);
        Console.WriteLine($"  power button at the word's cap : {power:F1}");
        Console.WriteLine($"  Driven by                      : {Max("hdr-driven"):F1} (worst {Worst("hdr-driven")})");
        Console.WriteLine($"  Add Device                     : {Max("hdr-add"):F1} (worst {Worst("hdr-add")})");
        Console.WriteLine($"  widest single section, SemiBold: {widestSection:F1} (worst {Worst("hdr-section-bold")})");

        // Per language, a strip's buttons side by side, the widest one
        // SemiBold as when it is the checked one.
        List<(string Tag, double Width)> Strips(string group, int count)
        {
            var strips = new List<(string Tag, double Width)>();
            foreach (var (tag, _) in Locales)
            {
                var regular = _probes.Where(p => p.Group == group && p.Locale == tag).ToList();
                var bold = _probes.Where(p => p.Group == group + "-bold" && p.Locale == tag).ToList();
                if (regular.Count != count) continue;
                double extra = regular.Zip(bold, (r, b) => W(b) - W(r)).Max();
                strips.Add((tag, regular.Sum(W) + extra));
            }
            return strips;
        }
        // The section probes are named part and bold, the part probes plain
        // and bold.
        var sections = new List<(string Tag, double Width)>();
        foreach (var (tag, _) in Locales)
        {
            var regular = _probes.Where(p => p.Group == "hdr-section-part" && p.Locale == tag).ToList();
            var bold = _probes.Where(p => p.Group == "hdr-section-bold" && p.Locale == tag).ToList();
            if (regular.Count != SectionKeys.Length) continue;
            double extra = regular.Zip(bold, (r, b) => W(b) - W(r)).Max();
            sections.Add((tag, regular.Sum(W) + extra));
        }
        var widestSections = sections.OrderByDescending(s => s.Width).First();
        Console.WriteLine($"  six sections on one line       : {widestSections.Width:F1} (worst {widestSections.Tag})");
        var buttonsParts = Strips("hdr-parts-buttons", ButtonsPartKeys.Length).OrderByDescending(s => s.Width).First();
        var macrosParts = Strips("hdr-parts-macros", MacrosPartKeys.Length).OrderByDescending(s => s.Width).First();
        Console.WriteLine($"  Buttons parts on one line      : {buttonsParts.Width:F1} (worst {buttonsParts.Tag})");
        Console.WriteLine($"  Macros & Menus parts, one line : {macrosParts.Width:F1} (worst {macrosParts.Tag})");

        // Line 2 at its floor: Driven by, the picker at 200 in its 4,2
        // margin, Add Device.
        double deviceLine = Max("hdr-driven") + 200 + 8 + Max("hdr-add");
        foreach (var (name, line) in new[]
                 { ("900, pane open", 900.0 - 244 - 24), ("900, pane compact", 900.0 - 48 - 24), ("1100, pane open", 1100.0 - 244 - 24) })
        {
            // Line 1's first column is what the power button leaves.
            double first = line - power;
            var twoLines = sections.Where(s => s.Width > line).Select(s => s.Tag).ToList();
            Console.WriteLine($"  line at {name,-17}      : {line:F1}");
            Console.WriteLine($"    first column of line 1       : {first:F1}");
            Console.WriteLine($"    title fits it                :" + Verdict(title <= first));
            // The title trims and the chip's combo narrows where they do not
            // fit whole. Neither runs past the edge.
            Console.WriteLine($"    title beside the chip        :" + (chip + title <= first ? "  whole" : "  trims"));
            Console.WriteLine($"    preset chip, combo at its cap:" + (preset <= first ? "  whole" : "  narrows"));
            Console.WriteLine($"    preset chip's floor fits     :" + Verdict(presetFloor <= first));
            Console.WriteLine($"    device line at its floor     : {deviceLine:F1}" + Verdict(deviceLine <= line));
            Console.WriteLine($"    widest section fits          :" + Verdict(widestSection <= line));
            Console.WriteLine($"    sections wrap to a second line in: {twoLines.Count}"
                              + (twoLines.Count > 0 ? "  " + string.Join(" ", twoLines) : ""));
        }
    }
}
