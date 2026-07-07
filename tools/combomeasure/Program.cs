using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Markup;
using System.Xml.Linq;
using IOPath = System.IO.Path;

// Exacting width measurement for the Indicator LEDs card combos.
// Renders the REAL WPF-UI ComboBox with the app's implicit style
// (BasedOn DefaultComboBoxStyle, Padding 8,4, MinHeight 30), the app
// font (Segoe UI Variable Text, 13, inherited from the window root),
// and the real 5-pip glyph prefix, then reads back ActualWidth per
// option per locale. No estimates: the number is what WPF lays out.

internal static class Program
{
    // Repo-relative: walk up from the running location (or CWD) until
    // the app's Strings folder is found, so the harness is portable and
    // committable under tools/combomeasure.
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
        throw new DirectoryNotFoundException(
            "Could not locate PadForge.App\\Resources\\Strings above the harness. " +
            "Run from inside the repo tree.");
    }

    // locale suffix -> resx file (empty suffix = invariant English).
    static readonly (string tag, string file)[] Locales =
    {
        ("en",      "Strings.resx"),
        ("de",      "Strings.de.resx"),
        ("es",      "Strings.es.resx"),
        ("fr",      "Strings.fr.resx"),
        ("it",      "Strings.it.resx"),
        ("ja",      "Strings.ja.resx"),
        ("ko",      "Strings.ko.resx"),
        ("nl",      "Strings.nl.resx"),
        ("pt-BR",   "Strings.pt-BR.resx"),
        ("zh-Hans", "Strings.zh-Hans.resx"),
    };

    // Per-combo option key lists. Pattern combo alone carries the pip prefix.
    static readonly string[] BrightnessKeys =
    {
        "Pad_Lighting_PlayerLed_Brightness_High",
        "Pad_Lighting_PlayerLed_Brightness_Medium",
        "Pad_Lighting_PlayerLed_Brightness_Low",
    };
    static readonly string[] PatternKeys =
    {
        "Pad_Lighting_PlayerLed_PlayerNumber",
        "Pad_Lighting_PlayerLed_Off",
        "Pad_Lighting_PlayerLed_P1", "Pad_Lighting_PlayerLed_P2",
        "Pad_Lighting_PlayerLed_P3", "Pad_Lighting_PlayerLed_P4",
        "Pad_Lighting_PlayerLed_All",
    };
    static readonly string[] MicKeys =
    {
        "Pad_Lighting_MicLed_Off", "Pad_Lighting_MicLed_Solid",
        "Pad_Lighting_MicLed_Pulse", "Pad_Lighting_MicLed_FollowDevice",
    };
    // Base Color (lightbar) section, combo 1: the mode picker (Width 320).
    static readonly string[] LightbarKeys =
    {
        "Pad_Lighting_Mode_PlayerNumber", "Pad_Lighting_Mode_Off",
        "Pad_Lighting_Mode_Static", "Pad_Lighting_Mode_Breathing",
        "Pad_Lighting_Mode_Strobe", "Pad_Lighting_Mode_Rainbow",
        "Pad_Lighting_Mode_ColorCycle", "Pad_Lighting_Mode_Battery",
        "Pad_Lighting_Mode_AudioPulse", "Pad_Lighting_Mode_AudioPulseRandom",
        "Pad_Lighting_Mode_AudioPulseRainbow", "Pad_Lighting_Mode_AudioThresholds",
        "Pad_Lighting_Mode_AudioGradient", "Pad_Lighting_Mode_AudioCrossFade",
    };
    // Base Color (lightbar) section, combo 2: the input-reactive overlay.
    static readonly string[] InputReactiveKeys =
    {
        "Pad_Lighting_InputReactive_Off", "Pad_Lighting_InputReactive_Random",
        "Pad_Lighting_InputReactive_Cycle", "Pad_Lighting_InputReactive_Fixed",
    };

    static Dictionary<string, Dictionary<string, string>> _strings; // key -> (localeTag -> text)

    // The player-pattern combo's ContentTemplate, byte-identical in
    // layout to PadPage.xaml: an inner StackPanel of five 6px ellipses
    // (each Margin 0,0,3,0) wrapped Margin 0,0,6,0, then the text.
    static readonly DataTemplate PipTemplate = (DataTemplate)XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <StackPanel Orientation='Horizontal'>
    <StackPanel Orientation='Horizontal' VerticalAlignment='Center' Margin='0,0,6,0'>
      <Ellipse Width='6' Height='6' Margin='0,0,3,0' VerticalAlignment='Center' Stroke='Orange' StrokeThickness='1' Fill='Transparent'/>
      <Ellipse Width='6' Height='6' Margin='0,0,3,0' VerticalAlignment='Center' Stroke='Orange' StrokeThickness='1' Fill='Transparent'/>
      <Ellipse Width='6' Height='6' Margin='0,0,3,0' VerticalAlignment='Center' Stroke='Orange' StrokeThickness='1' Fill='Transparent'/>
      <Ellipse Width='6' Height='6' Margin='0,0,3,0' VerticalAlignment='Center' Stroke='Orange' StrokeThickness='1' Fill='Transparent'/>
      <Ellipse Width='6' Height='6' Margin='0,0,3,0' VerticalAlignment='Center' Stroke='Orange' StrokeThickness='1' Fill='Transparent'/>
    </StackPanel>
    <TextBlock Text='{Binding}' VerticalAlignment='Center'/>
  </StackPanel>
</DataTemplate>");

    [STAThread]
    static void Main()
    {
        LoadStrings();

        var app = new Application();
        var themes = new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark };
        var controls = new Wpf.Ui.Markup.ControlsDictionary();
        app.Resources.MergedDictionaries.Add(themes);
        app.Resources.MergedDictionaries.Add(controls);

        // App's implicit ComboBox style: BasedOn WPF-UI default + the
        // width-relevant overrides (Padding 8,4, MinHeight 30).
        var baseStyle = (Style)app.Resources["DefaultComboBoxStyle"];
        var comboStyle = new Style(typeof(ComboBox), baseStyle);
        comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        comboStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
        app.Resources[typeof(ComboBox)] = comboStyle;

        var bodyFont = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var records = new List<Rec>();
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        void AddCombo(string combo, string localeTag, string text, bool pips)
        {
            var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left };
            var item = new ComboBoxItem { Content = text };
            if (pips)
            {
                // Match the app EXACTLY: pips live in a ContentTemplate
                // (DataTemplate), not as element Content, so the closed
                // combo's selection box instantiates its own copy and
                // sizes to pips + text. Text="{Binding}" resolves to the
                // ComboBoxItem's string Content.
                item.ContentTemplate = PipTemplate;
            }
            cb.Items.Add(item);
            cb.SelectedIndex = 0;
            panel.Children.Add(cb);
            records.Add(new Rec { Combo = combo, Locale = localeTag, Text = text, Cb = cb });
        }

        foreach (var (tag, _) in Locales)
        {
            foreach (var k in BrightnessKeys)    AddCombo("Brightness",    tag, _strings[k][tag], false);
            foreach (var k in PatternKeys)       AddCombo("Pattern",       tag, _strings[k][tag], true);
            foreach (var k in MicKeys)           AddCombo("Mic",           tag, _strings[k][tag], false);
            foreach (var k in LightbarKeys)      AddCombo("Lightbar",      tag, _strings[k][tag], false);
            foreach (var k in InputReactiveKeys) AddCombo("InputReactive", tag, _strings[k][tag], false);
        }

        var win = new Window
        {
            Content = panel,
            // Match the app's inherited typography exactly: FontSize 13
            // from the MainWindow root, BodyFontFamily. Window is a
            // Control, so these inherit down to every ComboBox.
            FontFamily = bodyFont,
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
            Report(records);
            app.Shutdown();
        };
        win.Show();
        app.Run();
    }

    static void Report(List<Rec> records)
    {
        var sb = new StringBuilder();
        foreach (var combo in new[] { "Brightness", "Pattern", "Mic", "Lightbar", "InputReactive" })
        {
            var group = records.Where(r => r.Combo == combo).ToList();
            double max = group.Max(r => r.Cb.ActualWidth);
            var driver = group.First(r => Math.Abs(r.Cb.ActualWidth - max) < 0.01);
            sb.AppendLine($"== {combo} ==");
            foreach (var r in group.OrderByDescending(r => r.Cb.ActualWidth).Take(6))
                sb.AppendLine($"   {r.Cb.ActualWidth,7:F2}  [{r.Locale,-7}] {Trunc(r.Text)}");
            sb.AppendLine($"   MAX = {max:F2}  (locale {driver.Locale}: \"{Trunc(driver.Text)}\")");
            sb.AppendLine();
        }
        void SharedMax(string title, params string[] combos)
        {
            var g = records.Where(r => combos.Contains(r.Combo)).ToList();
            double m = g.Max(r => r.Cb.ActualWidth);
            var d = g.First(r => Math.Abs(r.Cb.ActualWidth - m) < 0.01);
            sb.AppendLine($"{title} = {m:F2}");
            sb.AppendLine($"   driven by {d.Combo}/{d.Locale}: \"{Trunc(d.Text)}\"");
            sb.AppendLine($"   ceil = {Math.Ceiling(m)}   +8 safety = {Math.Ceiling(m) + 8}");
            sb.AppendLine();
        }
        // Indicator-LEDs card: three enum combos + the device picker.
        SharedMax("INDICATOR-LEDS SHARED MAX", "Brightness", "Pattern", "Mic");
        // Base Color (lightbar) card: mode combo + input-reactive overlay.
        SharedMax("LIGHTBAR SECTION SHARED MAX", "Lightbar", "InputReactive");

        var outPath = IOPath.Combine(AppContext.BaseDirectory, "..", "..", "..", "measure_out.txt");
        outPath = IOPath.GetFullPath(outPath);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
        Console.WriteLine("written: " + outPath);
    }

    static string Trunc(string s) => s.Length <= 40 ? s : s.Substring(0, 39) + "…";

    static void LoadStrings()
    {
        var needed = BrightnessKeys.Concat(PatternKeys).Concat(MicKeys)
            .Concat(LightbarKeys).Concat(InputReactiveKeys).ToHashSet();
        _strings = needed.ToDictionary(k => k, _ => new Dictionary<string, string>());
        foreach (var (tag, file) in Locales)
        {
            var doc = XDocument.Load(IOPath.Combine(ResxDir, file));
            var map = doc.Root.Elements("data")
                .Where(e => (string)e.Attribute("name") != null)
                .ToDictionary(e => (string)e.Attribute("name"), e => (string)e.Element("value") ?? "");
            foreach (var k in needed)
            {
                // Fall back to invariant English if a locale lacks the key.
                string v = map.TryGetValue(k, out var t) ? t
                         : _strings[k].TryGetValue("en", out var en) ? en : k;
                _strings[k][tag] = v;
            }
        }
    }

    class Rec
    {
        public string Combo, Locale, Text;
        public ComboBox Cb;
    }
}
