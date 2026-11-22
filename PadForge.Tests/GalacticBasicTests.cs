using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Converters;
using PadForge.Resources.Strings;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Galactic Basic (#590), English written in the Aurebesh alphabet. Its
    /// row reads the American English of Strings.resx, and the interface
    /// draws it in SilvinoR's Aurebesh font: while it is the interface
    /// language, App.xaml's two faces become the Aurebesh faces, and every
    /// other language keeps App.xaml's own. The language picker keeps each
    /// name readable whatever the interface language.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class GalacticBasicTests
    {
        private static readonly CultureInfo GalacticBasic = CultureInfo.GetCultureInfo("en-Qaaa");
        private static readonly CultureInfo AmericanEnglish = CultureInfo.GetCultureInfo("en-US");

        // dist/Aurebesh.ttf of silvinor/font-aurebesh at 1aeb227, as the
        // attribution file beside the font records it.
        private const string UpstreamFontSha256 = "c20656ee89c413817a1fea1bb4b1025a043d571c1008afce671e88bcc60c370c";

        private static string Repo(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
        }

        private static void WithApp(Action<App> body)
            => PageRenderDumpPages.WithApp((app, _) => body(app), TimeSpan.FromMinutes(2), "the Galactic Basic test timed out");

        [Fact]
        public void TheRowIsAmericanEnglishInTheAurebeshAlphabet()
        {
            LanguageInfo row = LanguageCatalog.Find("en-Qaaa");
            Assert.NotNull(row);
            Assert.Equal("Galactic Basic", row.NativeName);
            Assert.Equal("Galactic Basic (Aurebesh)", row.EnglishName);
            Assert.Equal(LanguageCatalog.AurebeshScript, row.Script);
            Assert.Equal("Qaaa", row.Script);
            Assert.True(row.IsAurebesh);
            Assert.True(row.IsComplete);
            Assert.False(row.IsRightToLeft);
            Assert.True(LocaleFiles.IsOtherEnglish(row));
            Assert.Equal("en", row.Culture.Parent.Name);
            Assert.Same(row, LanguageCatalog.Resolve(GalacticBasic));
            Assert.Same(row, LanguageCatalog.All.Single(l => l.IsAurebesh));

            // Every string is the American text: the file holds none, and a
            // lookup falls through to Strings.resx.
            string file = File.ReadAllText(Repo("PadForge.App", "Resources", "Strings", "Strings.en-Qaaa.resx"));
            Assert.DoesNotMatch("<data name=", file);
            Assert.Equal(Strings.ResourceManager.GetString("Common_Save", CultureInfo.InvariantCulture),
                Strings.ResourceManager.GetString("Common_Save", GalacticBasic));
            Assert.Equal(Strings.ResourceManager.GetString("About_AurebeshFontDesc", CultureInfo.InvariantCulture),
                Strings.ResourceManager.GetString("About_AurebeshFontDesc", GalacticBasic));
        }

        /// <summary>Galactic Basic sets the Aurebesh faces, a second call
        /// changes nothing, and every other language of the table, a language
        /// with no row and American English put App.xaml's own faces back,
        /// the same objects.</summary>
        [Fact]
        public void GalacticBasicDrawsInAurebesh_AndEveryOtherLanguageInAppXamlsFaces()
        {
            WithApp(app =>
            {
                object body = app.Resources[TextFaces.BodyKey], display = app.Resources[TextFaces.DisplayKey];
                Assert.Equal(TextFaces.StandardBody.Source, ((FontFamily)body).Source);
                Assert.Equal("Segoe UI Variable Display, Segoe UI", ((FontFamily)display).Source);
                try
                {
                    UiLanguage.ApplyFaces(app.Resources, GalacticBasic);
                    Assert.Same(TextFaces.AurebeshBody, app.Resources[TextFaces.BodyKey]);
                    Assert.Same(TextFaces.AurebeshDisplay, app.Resources[TextFaces.DisplayKey]);
                    UiLanguage.ApplyFaces(app.Resources, GalacticBasic);
                    Assert.Same(TextFaces.AurebeshBody, app.Resources[TextFaces.BodyKey]);

                    var others = LanguageCatalog.All.Where(l => !l.IsAurebesh).Select(l => l.Culture)
                        .Append(CultureInfo.GetCultureInfo("he")).Append(CultureInfo.InvariantCulture).ToList();
                    Assert.True(others.Count > 110);
                    foreach (CultureInfo culture in others)
                    {
                        UiLanguage.ApplyFaces(app.Resources, culture);
                        Assert.Same(body, app.Resources[TextFaces.BodyKey]);
                        Assert.Same(display, app.Resources[TextFaces.DisplayKey]);
                        UiLanguage.ApplyFaces(app.Resources, culture);
                        Assert.Same(body, app.Resources[TextFaces.BodyKey]);
                        UiLanguage.ApplyFaces(app.Resources, GalacticBasic);
                        Assert.Same(TextFaces.AurebeshBody, app.Resources[TextFaces.BodyKey]);
                    }
                }
                finally { UiLanguage.ApplyFaces(app.Resources, AmericanEnglish); }
                Assert.Same(body, app.Resources[TextFaces.BodyKey]);
                Assert.Same(display, app.Resources[TextFaces.DisplayKey]);
            });
        }

        /// <summary>Text already on a window redraws in the new face: text
        /// made in code through TextFaces, and markup that takes a face as a
        /// DynamicResource. A control that draws its own text holds the face
        /// it follows.</summary>
        [Fact]
        public void TextAlreadyBuiltRedrawsInTheNewFace()
        {
            WithApp(app =>
            {
                var made = TextFaces.Body(new TextBlock { Text = "Save" });
                var run = TextFaces.Numbers(new Run("120"));
                var inMarkup = new TextBlock();
                inMarkup.SetResourceReference(TextBlock.FontFamilyProperty, TextFaces.DisplayKey);
                var drawn = new Border();
                TextFaces.Follow(drawn, TextFaces.BodyKey);
                var window = new Window { Content = new StackPanel { Children = { made, new TextBlock(run), inMarkup, drawn } } };
                try
                {
                    FontFamily body = made.FontFamily;
                    Assert.Equal(TextFaces.StandardBody.Source, body.Source);
                    UiLanguage.ApplyFaces(app.Resources, GalacticBasic);
                    Assert.Same(TextFaces.AurebeshBody, made.FontFamily);
                    Assert.Same(TextFaces.AurebeshBody, TextElement.GetFontFamily(run));
                    Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(run));
                    Assert.Same(TextFaces.AurebeshDisplay, inMarkup.FontFamily);
                    Assert.Same(TextFaces.AurebeshBody, TextElement.GetFontFamily(drawn));
                    Assert.Same(TextFaces.AurebeshBody, TextFaces.TypefaceOf(drawn, FontWeights.Normal).FontFamily);
                    UiLanguage.ApplyFaces(app.Resources, AmericanEnglish);
                    Assert.Same(body, made.FontFamily);
                    Assert.Same(body, TextElement.GetFontFamily(drawn));
                }
                finally
                {
                    UiLanguage.ApplyFaces(app.Resources, AmericanEnglish);
                    window.Close();
                }
            });
        }

        /// <summary>The picker draws each language's own name in its own face
        /// and every English name in the standard body face, under every
        /// interface language.</summary>
        [Fact]
        public void ThePickerKeepsEveryNameReadable()
        {
            var converter = new LanguageFaceConverter();
            foreach (LanguageInfo language in LanguageCatalog.All)
                Assert.Same(language.IsAurebesh ? TextFaces.AurebeshBody : TextFaces.StandardBody,
                    converter.Convert(language, typeof(FontFamily), null, CultureInfo.InvariantCulture));
            Assert.Same(TextFaces.StandardBody, converter.Convert(null, typeof(FontFamily), null, CultureInfo.InvariantCulture));

            XDocument page = XDocument.Load(Repo("PadForge.App", "Views", "SettingsPage.xaml"));
            XElement picker = page.Descendants().Single(e => e.Name.LocalName == "ComboBox"
                && (string)e.Attribute("ItemsSource") == "{Binding AvailableLanguages}");
            var names = picker.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();
            Assert.Equal(2, names.Count);
            Assert.Equal("{Binding NativeName}", (string)names[0].Attribute("Text"));
            Assert.Equal("{Binding Converter={StaticResource LanguageFaceConverter}}", (string)names[0].Attribute("FontFamily"));
            Assert.Equal("{Binding EnglishNameIfDifferent}", (string)names[1].Attribute("Text"));
            Assert.Equal("{x:Static common:TextFaces.StandardBody}", (string)names[1].Attribute("FontFamily"));
            Assert.Contains("<conv:LanguageFaceConverter x:Key=\"LanguageFaceConverter\" />",
                File.ReadAllText(Repo("PadForge.App", "App.xaml")));
        }

        /// <summary>The face the picker keeps is the body face App.xaml
        /// declares.</summary>
        [Fact]
        public void TheStandardFaceIsAppXamlsBodyFace()
        {
            string app = File.ReadAllText(Repo("PadForge.App", "App.xaml"));
            Assert.Contains("<FontFamily x:Key=\"BodyFontFamily\">" + TextFaces.StandardBody.Source + "</FontFamily>", app);
            Assert.Contains("<FontFamily x:Key=\"DisplayFontFamily\">Segoe UI Variable Display, Segoe UI</FontFamily>", app);
        }

        /// <summary>A face read once would keep a page in the old face when
        /// the language changes. No markup reads either face as a
        /// StaticResource, and no code reads one by value.</summary>
        [Fact]
        public void NothingReadsAFaceOnce()
        {
            string app = Repo("PadForge.App");
            var bad = new List<string>();
            int dynamic = 0;
            foreach (string path in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
                         .Where(p => p.EndsWith(".xaml", StringComparison.Ordinal) || p.EndsWith(".cs", StringComparison.Ordinal)))
            {
                string rel = Path.GetRelativePath(app, path).Replace('\\', '/');
                if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal))
                    continue;
                string text = File.ReadAllText(path);
                foreach (string key in new[] { TextFaces.BodyKey, TextFaces.DisplayKey })
                {
                    if (text.Contains("{StaticResource " + key + "}", StringComparison.Ordinal)) bad.Add(rel + ": StaticResource " + key);
                    if (Regex.IsMatch(text, @"FindResource\(""" + key + @"""\)")) bad.Add(rel + ": FindResource " + key);
                    dynamic += Regex.Matches(text, Regex.Escape("{DynamicResource " + key + "}")).Count;
                }
            }
            Assert.Empty(bad);
            Assert.True(dynamic >= 55, dynamic + " DynamicResource references to the faces");
        }

        /// <summary>The font ships as SilvinoR published it. The Open Font
        /// License lets a font keep its Reserved Font Name only unmodified,
        /// and the license and the attribution sit beside it.</summary>
        [Fact]
        public void TheFontIsSilvinoRsAurebeshUnmodified()
        {
            string folder = Repo("PadForge.App", "Resources", "Fonts");
            Assert.Equal(UpstreamFontSha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "Aurebesh.ttf")))).ToLowerInvariant());
            string license = File.ReadAllText(Path.Combine(folder, "LICENSE.md"));
            Assert.Contains("SIL OPEN FONT LICENSE", license);
            Assert.Contains("Reserved Font Name AUREBESH", license);
            Assert.Contains("Copyright (c) 2022, SilvinoR", license);
            Assert.Contains(UpstreamFontSha256, File.ReadAllText(Path.Combine(folder, "ATTRIBUTION.txt")));
            Assert.Contains("<Resource Include=\"Resources\\Fonts\\Aurebesh.ttf\" />",
                File.ReadAllText(Repo("PadForge.App", "PadForge.App.csproj")));
        }

        /// <summary>The embedded font loads from the app's assembly as the
        /// Aurebesh family and draws every letter and digit. A character it
        /// lacks, such as an arrow, falls to the next face in the list.</summary>
        [Fact]
        public void TheEmbeddedFontLoadsAndDrawsTheLettersAndDigits()
        {
            WithApp(app =>
            {
                foreach (FontFamily face in new[] { TextFaces.AurebeshBody, TextFaces.AurebeshDisplay })
                {
                    var typeface = new Typeface(face, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    Assert.True(typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs), face.Source);
                    Assert.Equal("Aurebesh", glyphs.FamilyNames[CultureInfo.GetCultureInfo("en-US")]);
                    foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,:'’“”…")
                        Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(c), "Aurebesh lacks " + c);
                    Assert.False(glyphs.CharacterToGlyphMap.ContainsKey('→'));
                    Assert.Contains("Segoe UI Variable", face.Source);
                }
            });
        }
    }
}
