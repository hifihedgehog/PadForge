using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using PadForge.Common;
using PadForge.Converters;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #504 (2315506431): with the interface in Simplified
    /// Chinese, the Chinese text was drawn in Yu Gothic UI, the Japanese
    /// font. The reporter named the cause, and it was right: no window
    /// declared a language.
    ///
    /// <para>Chinese, Japanese and Korean share their characters. WPF's
    /// fallback font table lists a font for each of those languages and a
    /// last row for every other language, with the Japanese font first.
    /// Undeclared text is en-US, so it took the last row.</para>
    ///
    /// <para>A window's language is also the culture its bindings format and
    /// parse numbers in. The pad page reads and writes some 250 number boxes
    /// in the en-US notation, so a language is declared only where its
    /// numbers are written as en-US writes them. The second half of this
    /// file holds that against every number format in the markup.</para>
    ///
    /// <para>WPF elements demand STA, so those bodies run on their own STA
    /// thread and rethrow.</para>
    /// </summary>
    public class UiLanguageTests
    {
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string AppFile(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "PadForge.App" }.Concat(parts).ToArray()));

        private static IEnumerable<string> AppSources(string pattern)
        {
            string app = Path.Combine(RepoRoot(), "PadForge.App");
            foreach (string path in Directory.EnumerateFiles(app, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(app, path).Replace('\\', '/');
                if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal))
                    continue;
                yield return path;
            }
        }

        private static string Tag(CultureInfo culture) => UiLanguage.Declared(culture).IetfLanguageTag;

        // ─────────────────────────────────────────────
        //  The language a window declares
        // ─────────────────────────────────────────────

        [Theory]
        [InlineData("zh-Hans", "zh-Hans")]
        [InlineData("zh-CN", "zh-CN")]
        [InlineData("zh-SG", "zh-SG")]
        [InlineData("zh-Hant", "zh-Hant")]
        [InlineData("zh-TW", "zh-TW")]
        [InlineData("zh-HK", "zh-HK")]
        [InlineData("zh", "zh-Hans")]
        [InlineData("ja", "ja")]
        [InlineData("ja-JP", "ja-JP")]
        [InlineData("ko", "ko")]
        [InlineData("ko-KR", "ko-KR")]
        public void ChineseJapaneseAndKorean_AreDeclared(string culture, string declared)
            => Assert.Equal(declared, Tag(CultureInfo.GetCultureInfo(culture)), ignoreCase: true);

        [Theory]
        [InlineData("en")]
        [InlineData("en-GB")]
        [InlineData("de")]
        [InlineData("fr")]
        [InlineData("es")]
        [InlineData("it")]
        [InlineData("nl")]
        [InlineData("pt-BR")]
        [InlineData("ru")]
        [InlineData("ar")]
        [InlineData("th")]
        [InlineData("vi")]
        public void EveryOtherLanguage_StaysUndeclared(string culture)
            => Assert.Same(UiLanguage.Undeclared, UiLanguage.Declared(CultureInfo.GetCultureInfo(culture)));

        [Fact]
        public void NoCulture_IsUndeclared()
        {
            Assert.Same(UiLanguage.Undeclared, UiLanguage.Declared(null));
            Assert.Same(UiLanguage.Undeclared, UiLanguage.Declared(CultureInfo.InvariantCulture));
            Assert.Equal("en-US", UiLanguage.Undeclared.IetfLanguageTag, ignoreCase: true);
        }

        // ─────────────────────────────────────────────
        //  The font a declared language gets
        // ─────────────────────────────────────────────

        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

        /// <summary>The fonts WPF draws <paramref name="text"/> in, read back
        /// from the glyph runs of a laid-out TextBlock in the app's body font.</summary>
        private static string FontOf(string text, XmlLanguage language)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            if (language != null) block.Language = language;
            block.Measure(new Size(1000, 100));
            block.Arrange(new Rect(0, 0, 1000, 100));
            block.UpdateLayout();
            var fonts = new List<string>();
            Collect(VisualTreeHelper.GetDrawing(block), fonts);
            return string.Join("+", fonts.Distinct());
        }

        private static void Collect(Drawing drawing, List<string> fonts)
        {
            if (drawing is DrawingGroup group)
            {
                foreach (Drawing child in group.Children) Collect(child, fonts);
            }
            else if (drawing is GlyphRunDrawing run && run.GlyphRun != null)
            {
                var names = run.GlyphRun.GlyphTypeface.FamilyNames;
                fonts.Add(names.TryGetValue(EnUs, out string name) ? name : names.Values.First());
            }
        }

        private static bool Installed(string family)
            => Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase));

        /// <summary>直 is in every one of the four fonts, so the font it is
        /// drawn in is the choice of the language alone. Each font is asked
        /// about only where Windows has it.</summary>
        [Fact]
        public void SharedCharacters_TakeTheFontOfTheDeclaredLanguage()
        {
            RunSta(() =>
            {
                int asked = 0;
                void Expect(string culture, string font)
                {
                    if (!Installed(font)) return;
                    asked++;
                    Assert.Equal(font, FontOf("直", UiLanguage.Declared(CultureInfo.GetCultureInfo(culture))));
                }

                Expect("zh-Hans", "Microsoft YaHei UI");
                Expect("zh-CN", "Microsoft YaHei UI");
                Expect("zh-Hant", "Microsoft JhengHei UI");
                Expect("zh-TW", "Microsoft JhengHei UI");
                Expect("zh-HK", "Microsoft JhengHei UI");
                Expect("ja", "Yu Gothic UI");
                Expect("ko", "Malgun Gothic");
                Assert.True(asked > 0, "none of the four fonts is installed, so nothing was checked");

                // The defect itself: undeclared Chinese text takes the Japanese font.
                if (Installed("Yu Gothic UI") && Installed("Microsoft YaHei UI"))
                {
                    Assert.Equal("Yu Gothic UI", FontOf("直", null));
                    Assert.Equal("Yu Gothic UI", FontOf("直", UiLanguage.Undeclared));
                }
            });
        }

        // ─────────────────────────────────────────────
        //  Windows
        // ─────────────────────────────────────────────

        [Fact]
        public void AWindowDeclaresItsLanguage_AndItsContentTakesIt()
        {
            RunSta(() =>
            {
                var text = new TextBlock();
                var window = new Window { Content = text };
                Assert.Same(UiLanguage.Undeclared, window.Language);

                UiLanguage.Declare(window, XmlLanguage.GetLanguage("zh-Hans"), FlowDirection.LeftToRight);
                Assert.Equal("zh-Hans", window.Language.IetfLanguageTag, ignoreCase: true);
                Assert.Equal("zh-Hans", text.Language.IetfLanguageTag, ignoreCase: true);

                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.RightToLeft);
                Assert.Same(UiLanguage.Undeclared, text.Language);
                Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
                Assert.Equal(FlowDirection.RightToLeft, text.FlowDirection);

                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.LeftToRight);
                Assert.Equal(FlowDirection.LeftToRight, text.FlowDirection);
                UiLanguage.Declare(null, UiLanguage.Undeclared, FlowDirection.LeftToRight);
            });
        }

        [Fact]
        public void AWindowThatLoads_DeclaresTheCurrentLanguage()
        {
            RunSta(() =>
            {
                CultureInfo before = CultureInfo.CurrentUICulture;
                try
                {
                    var window = new Window();
                    var loaded = new RoutedEventArgs(FrameworkElement.LoadedEvent);

                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
                    UiLanguage.OnWindowLoaded(window, loaded);
                    Assert.Equal("zh-Hans", window.Language.IetfLanguageTag, ignoreCase: true);

                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
                    UiLanguage.OnWindowLoaded(window, loaded);
                    Assert.Equal("ja", window.Language.IetfLanguageTag, ignoreCase: true);

                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
                    UiLanguage.OnWindowLoaded(window, loaded);
                    Assert.Same(UiLanguage.Undeclared, window.Language);

                    // Not a window: nothing to declare on, and no throw.
                    UiLanguage.OnWindowLoaded(new TextBlock(), loaded);
                }
                finally { CultureInfo.CurrentUICulture = before; }
            });
        }

        /// <summary>The whole path, as the app runs it: once declaring has
        /// started, a window's own Loaded event declares the language, with
        /// no call from the window. Starting twice changes nothing.</summary>
        [Fact]
        public void OnceStarted_AWindowsLoadedEventDeclaresTheLanguage()
        {
            RunSta(() =>
            {
                CultureInfo before = CultureInfo.CurrentUICulture;
                try
                {
                    UiLanguage.Start();
                    UiLanguage.Start();
                    var text = new TextBlock();
                    var window = new Window { Content = text };

                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
                    Assert.Equal("zh-Hans", window.Language.IetfLanguageTag, ignoreCase: true);
                    Assert.Equal("zh-Hans", text.Language.IetfLanguageTag, ignoreCase: true);

                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
                    Assert.Same(UiLanguage.Undeclared, window.Language);
                }
                finally { CultureInfo.CurrentUICulture = before; }
            });
        }

        /// <summary>The update helper is the first thing startup can hand a
        /// window to, and it runs under the language of Windows, before the
        /// saved language is read. Declaring starts ahead of it. The
        /// known-folder lookup stays the first statement of all (see
        /// FirstKnownFolderLookup).</summary>
        [Fact]
        public void TheAppStartsDeclaring_BeforeItsFirstWindow()
        {
            string app = AppFile("App.xaml.cs");
            int startup = app.IndexOf("protected override void OnStartup(StartupEventArgs e)", StringComparison.Ordinal);
            int folder = app.IndexOf("Environment.GetFolderPath(Environment.SpecialFolder.Windows);", startup, StringComparison.Ordinal);
            int start = app.IndexOf("PadForge.Common.UiLanguage.Start();", StringComparison.Ordinal);
            int helper = app.IndexOf("Services.UpdateService.TryRunApplyMode(", startup, StringComparison.Ordinal);
            Assert.True(startup >= 0 && folder >= 0 && start >= 0 && helper >= 0);
            Assert.True(folder < start, "something now runs ahead of the first known-folder lookup");
            Assert.True(start < helper, "the update helper's windows open before the language is declared");
            Assert.Single(Regex.Matches(app, @"UiLanguage\.Start\(\);"));

            string source = AppFile("Common", "UiLanguage.cs");
            Assert.Contains("EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,", source);
            Assert.Contains("new RoutedEventHandler(OnWindowLoaded)", source);
            Assert.Contains("Strings.CultureChanged += Apply;", source);
        }

        /// <summary>One rule, in one place. A fixed language in markup, or a
        /// second caller of XmlLanguage, would declare a language whose
        /// numbers this file never compared.</summary>
        [Fact]
        public void NothingElseInTheAppDeclaresALanguage()
        {
            var offenders = new List<string>();
            foreach (string path in AppSources("*.cs"))
            {
                if (Path.GetFileName(path) == "UiLanguage.cs") continue;
                string text = File.ReadAllText(path);
                if (text.Contains("XmlLanguage.GetLanguage(") || text.Contains("LanguageProperty"))
                    offenders.Add(Path.GetFileName(path));
            }
            foreach (string path in AppSources("*.xaml"))
            {
                string text = File.ReadAllText(path);
                if (Regex.IsMatch(text, @"xml:lang=|\sLanguage="""))
                    offenders.Add(Path.GetFileName(path));
            }
            Assert.Empty(offenders);
        }

        // ─────────────────────────────────────────────
        //  The language picker
        // ─────────────────────────────────────────────

        [Fact]
        public void EachRowOfTheLanguagePicker_DeclaresItsOwnLanguage()
        {
            var converter = new CultureLanguageConverter();
            object Row(string culture)
                => converter.Convert(CultureInfo.GetCultureInfo(culture), typeof(XmlLanguage), null, CultureInfo.InvariantCulture);

            Assert.Equal("zh-Hans", ((XmlLanguage)Row("zh-Hans")).IetfLanguageTag, ignoreCase: true);
            Assert.Equal("ja", ((XmlLanguage)Row("ja")).IetfLanguageTag, ignoreCase: true);
            Assert.Equal("ko", ((XmlLanguage)Row("ko")).IetfLanguageTag, ignoreCase: true);
            Assert.Same(UiLanguage.Undeclared, Row("de"));
            Assert.Same(UiLanguage.Undeclared, converter.Convert(null, typeof(XmlLanguage), null, CultureInfo.InvariantCulture));

            string page = AppFile("Views", "SettingsPage.xaml");
            int picker = page.IndexOf("ItemsSource=\"{Binding AvailableLanguages}\"", StringComparison.Ordinal);
            Assert.True(picker >= 0);
            string markup = page.Substring(picker, page.IndexOf("</ComboBox>", picker, StringComparison.Ordinal) - picker);
            Assert.Contains("<Style TargetType=\"ComboBoxItem\" BasedOn=\"{StaticResource {x:Type ComboBoxItem}}\">", markup);
            Assert.Contains("<Setter Property=\"Language\" Value=\"{Binding Culture, Converter={StaticResource CultureLanguageConverter}, ConverterCulture=en-US}\"/>", markup);
            Assert.Contains("<conv:CultureLanguageConverter x:Key=\"CultureLanguageConverter\" />", AppFile("App.xaml"));
        }

        /// <summary>The picker's own markup, loaded as the page loads it. A
        /// row made the way the picker makes one (the culture as its data,
        /// the picker's row style) must come out declaring that culture's
        /// language, and its text must take it.</summary>
        [Fact]
        public void ThePickersOwnMarkup_GivesARowItsLanguage()
        {
            RunSta(() =>
            {
                string page = AppFile("Views", "SettingsPage.xaml");
                int open = page.LastIndexOf("<ComboBox",
                    page.IndexOf("ItemsSource=\"{Binding AvailableLanguages}\"", StringComparison.Ordinal), StringComparison.Ordinal);
                int close = page.IndexOf("</ComboBox>", open, StringComparison.Ordinal) + "</ComboBox>".Length;
                string assembly = typeof(CultureLanguageConverter).Assembly.GetName().Name;
                string markup =
                    "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                    + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""
                    + " xmlns:conv=\"clr-namespace:PadForge.Converters;assembly=" + assembly + "\""
                    + " xmlns:controls=\"clr-namespace:PadForge.Controls;assembly=" + assembly + "\">"
                    + "<Grid.Resources>"
                    + "<conv:CultureLanguageConverter x:Key=\"CultureLanguageConverter\"/>"
                    + "<Style x:Key=\"{x:Type ComboBoxItem}\" TargetType=\"ComboBoxItem\"/>"
                    + "</Grid.Resources>"
                    + page.Substring(open, close - open)
                    + "</Grid>";
                var grid = (Grid)XamlReader.Parse(markup);
                var picker = (ComboBox)grid.Children[0];
                Assert.NotNull(picker.ItemContainerStyle);
                // The rows are the catalog's, chosen by culture, and typing jumps by own name.
                Assert.Equal("Culture", picker.SelectedValuePath);
                Assert.Equal("NativeName", TextSearch.GetTextPath(picker));
                Assert.NotNull(picker.ItemTemplate);

                XmlLanguage RowLanguage(string culture, out XmlLanguage ofItsText)
                {
                    var text = new TextBlock();
                    LanguageInfo language = LanguageCatalog.Find(culture);
                    Assert.NotNull(language);
                    var row = new ComboBoxItem
                    {
                        DataContext = language,
                        Content = text,
                        Style = picker.ItemContainerStyle,
                    };
                    ofItsText = text.Language;
                    return row.Language;
                }

                Assert.Equal("zh-Hans", RowLanguage("zh-Hans", out XmlLanguage chinese).IetfLanguageTag, ignoreCase: true);
                Assert.Equal("zh-Hans", chinese.IetfLanguageTag, ignoreCase: true);
                Assert.Equal("ja", RowLanguage("ja", out XmlLanguage japanese).IetfLanguageTag, ignoreCase: true);
                Assert.Equal("ja", japanese.IetfLanguageTag, ignoreCase: true);
                Assert.Equal("ko", RowLanguage("ko", out _).IetfLanguageTag, ignoreCase: true);
                Assert.Same(UiLanguage.Undeclared, RowLanguage("de", out XmlLanguage german));
                Assert.Same(UiLanguage.Undeclared, german);
            });
        }

        // ─────────────────────────────────────────────
        //  Numbers stay in one notation
        // ─────────────────────────────────────────────

        /// <summary>Every culture .NET knows, with the language of each
        /// translation in the tree and the bare tags added.</summary>
        private static List<CultureInfo> EveryCulture()
        {
            var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures).ToList();
            var tags = new List<string> { "zh", "zh-Hans", "zh-Hant", "zh-CN", "zh-SG", "zh-TW", "zh-HK", "zh-MO", "ja", "ja-JP", "ko", "ko-KR" };
            string strings = Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings");
            foreach (string path in Directory.EnumerateFiles(strings, "Strings.*.resx"))
                tags.Add(Path.GetFileNameWithoutExtension(path).Substring("Strings.".Length));
            cultures.AddRange(tags.Select(CultureInfo.GetCultureInfo));
            return cultures;
        }

        /// <summary>Every culture a window can come to declare, as the
        /// specific culture its bindings then format and parse in.</summary>
        private static List<CultureInfo> DeclaredCultures()
        {
            var declared = new Dictionary<string, CultureInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (CultureInfo culture in EveryCulture())
            {
                XmlLanguage language = UiLanguage.Declared(culture);
                if (ReferenceEquals(language, UiLanguage.Undeclared)) continue;
                CultureInfo specific = language.GetSpecificCulture();
                declared[specific.Name] = specific;
            }
            return declared.Values.ToList();
        }

        [Fact]
        public void OfEveryCultureDotNetKnows_OnlyChineseJapaneseAndKoreanAreDeclared()
        {
            int declared = 0;
            foreach (CultureInfo culture in EveryCulture())
            {
                string tag = culture.IetfLanguageTag;
                string language = tag.Split('-')[0].ToLowerInvariant();
                // Chinese in Pinyin (zh-Latn) is Latin text. It takes the
                // fonts of undeclared text, and it has no culture of its own
                // to format numbers in, so it is not declared (#505).
                bool latin = tag.Split('-').Skip(1).Any(p => string.Equals(p, "Latn", StringComparison.OrdinalIgnoreCase));
                bool shares = (language == "zh" || language == "ja" || language == "ko") && !latin;
                XmlLanguage result = UiLanguage.Declared(culture);
                Assert.True(shares != ReferenceEquals(result, UiLanguage.Undeclared), tag + " is declared as " + result.IetfLanguageTag);
                if (!shares) continue;
                declared++;
                Assert.StartsWith(language, result.IetfLanguageTag, StringComparison.OrdinalIgnoreCase);
                // A binding asks the declared language for this, so it must not throw.
                Assert.False(string.IsNullOrEmpty(result.GetSpecificCulture().Name), tag + " has no specific culture");
            }
            Assert.True(declared >= 12, "only " + declared + " cultures came out declared");
        }

        /// <summary>The value of one StringFormat, given the markup that
        /// follows "StringFormat=". A quoted value ends at its quote. A
        /// composite one ({}...) ends where the binding does, and a bare one
        /// (F2) at the next comma or brace.</summary>
        internal static string FormatValue(string tail)
        {
            if (tail.Length == 0) return "";
            if (tail[0] == '\'' || tail[0] == '"')
            {
                int close = tail.IndexOf(tail[0], 1);
                return close < 0 ? tail.Substring(1) : tail.Substring(1, close - 1);
            }
            if (tail.StartsWith("{}", StringComparison.Ordinal))
            {
                int depth = 0, i = 2;
                while (i < tail.Length)
                {
                    char c = tail[i];
                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    else if ((c == ',' && depth == 0) || c == '"')
                    {
                        break;
                    }
                    i++;
                }
                return tail.Substring(0, i);
            }
            int end = tail.IndexOfAny(new[] { ',', '}', '"' });
            return end < 0 ? tail : tail.Substring(0, end);
        }

        /// <summary>The number formats the markup asks for: a bare one
        /// (StringFormat=F2) and every {0:...} inside a composite one.</summary>
        private static SortedSet<string> NumberFormatsInTheMarkup()
        {
            var formats = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in AppSources("*.xaml"))
            {
                string text = File.ReadAllText(path);
                foreach (Match m in Regex.Matches(text, "StringFormat="))
                {
                    int from = m.Index + m.Length;
                    string value = FormatValue(text.Substring(from, Math.Min(240, text.Length - from)));
                    if (value.StartsWith("{}", StringComparison.Ordinal)) value = value.Substring(2);
                    if (value.Contains("{"))
                    {
                        foreach (Match spec in Regex.Matches(value, @"\{\d+:([^}]+)\}"))
                            formats.Add(spec.Groups[1].Value);
                    }
                    else if (value.Trim().Length > 0)
                    {
                        formats.Add(value.Trim());
                    }
                }
            }
            return formats;
        }

        [Theory]
        [InlineData("F2, Converter={StaticResource X}}\"", "F2")]
        [InlineData("P0}\"", "P0")]
        [InlineData("0.##}\" Width=\"40\"", "0.##")]
        [InlineData("{}{0:0.00}}\"", "{}{0:0.00}")]
        [InlineData("{}Yaw {0:+0.0;-0.0;0.0}°/s}\"", "{}Yaw {0:+0.0;-0.0;0.0}°/s")]
        [InlineData("'{}{0:F3}'}\"", "{}{0:F3}")]
        [InlineData("' #{0}'}\"", " #{0}")]
        [InlineData("\"{}{0} / {1}\">", "{}{0} / {1}")]
        public void OneStringFormat_IsReadOutOfItsBinding(string markup, string value)
            => Assert.Equal(value, FormatValue(markup));

        private static readonly double[] Samples =
            { 0, 0.004, 0.123, 0.5, -0.25, 1, 12.345, 100, 1234.5, -1234.5, 1234567.891 };

        [Fact]
        public void TheMarkupsNumberFormats_AreFound()
        {
            SortedSet<string> formats = NumberFormatsInTheMarkup();
            Assert.True(formats.Count >= 10, "found only: " + string.Join(" | ", formats));
            foreach (string known in new[] { "F0", "F2", "F3", "P0", "0.00", "0.#", "+0.00;-0.00;0.00" })
                Assert.Contains(known, formats);
        }

        [Fact]
        public void ADeclaredLanguage_WritesEveryNumberAsEnUsDoes()
        {
            List<CultureInfo> cultures = DeclaredCultures();
            Assert.NotEmpty(cultures);
            SortedSet<string> formats = NumberFormatsInTheMarkup();
            foreach (CultureInfo culture in cultures)
            {
                foreach (double sample in Samples)
                {
                    // A binding with no format at all.
                    Assert.Equal(sample.ToString(EnUs), sample.ToString(culture));
                    Assert.Equal(((int)sample).ToString(EnUs), ((int)sample).ToString(culture));
                    foreach (string format in formats)
                    {
                        string expected = sample.ToString(format, EnUs);
                        string actual = sample.ToString(format, culture);
                        Assert.True(expected == actual,
                            $"{culture.Name} writes {sample.ToString(CultureInfo.InvariantCulture)} as \"{actual}\" under {format}, en-US as \"{expected}\"");
                    }
                }
            }
        }

        [Fact]
        public void ADeclaredLanguage_ReadsATypedNumberAsEnUsDoes()
        {
            // What a two-way binding to a number does with the text of its box.
            const NumberStyles typed = NumberStyles.Float | NumberStyles.AllowThousands;
            string[] entries = { "0.5", "-0.25", ".5", "1234", "1,234.5", "0,5", "+3", "1e3", " 7 ", "50%", "", "abc" };
            foreach (CultureInfo culture in DeclaredCultures())
            {
                foreach (string entry in entries)
                {
                    bool expectedOk = double.TryParse(entry, typed, EnUs, out double expected);
                    bool actualOk = double.TryParse(entry, typed, culture, out double actual);
                    Assert.True(expectedOk == actualOk && (!expectedOk || expected == actual),
                        $"{culture.Name} reads \"{entry}\" as {(actualOk ? actual.ToString(CultureInfo.InvariantCulture) : "nothing")}, en-US as {(expectedOk ? expected.ToString(CultureInfo.InvariantCulture) : "nothing")}");

                    bool expectedInt = int.TryParse(entry, NumberStyles.Integer, EnUs, out int wantInt);
                    bool actualInt = int.TryParse(entry, NumberStyles.Integer, culture, out int gotInt);
                    Assert.True(expectedInt == actualInt && (!expectedInt || wantInt == gotInt),
                        $"{culture.Name} reads the whole number \"{entry}\" differently from en-US");
                }
            }
        }

        /// <summary>The reason the rule is no wider: these languages are not
        /// declared, and each of them would change what a number box reads or
        /// writes if it were.</summary>
        [Theory]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        [InlineData("es-ES")]
        [InlineData("it-IT")]
        [InlineData("nl-NL")]
        [InlineData("pt-BR")]
        public void TheOtherShippedLanguages_WriteNumbersAnotherWay(string name)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(name);
            Assert.NotEqual(0.5.ToString("F2", EnUs), 0.5.ToString("F2", culture));
            Assert.Same(UiLanguage.Undeclared, UiLanguage.Declared(culture));
        }
    }
}
