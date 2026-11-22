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

        /// <summary>Windows draws its message box in its own font, never the
        /// interface's, so every question and notice a page or dialog shows is
        /// PadForge's own ConfirmDialog. Four message boxes stay in App.xaml.cs:
        /// the update helper's report and the already-running notice open
        /// before PadForge reads its saved language, and the two crash dialogs
        /// must open from any thread while PadForge fails.</summary>
        [Fact]
        public void OnlyTheStartupAndCrashDialogsAreWindowsMessageBoxes()
        {
            string app = Repo("PadForge.App");
            var calls = new List<string>();
            foreach (string path in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(app, path).Replace('\\', '/');
                if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal))
                    continue;
                foreach (string line in File.ReadLines(path))
                    if (line.Contains("MessageBox.Show(", StringComparison.Ordinal) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                        calls.Add(rel);
            }
            Assert.Equal(new[] { "App.xaml.cs", "App.xaml.cs", "App.xaml.cs", "App.xaml.cs" }, calls);

            string text = File.ReadAllText(Repo("PadForge.App", "App.xaml.cs"));
            int alreadyRunning = text.IndexOf("MessageBox.Show(Strings.Instance.App_AlreadyRunning,", StringComparison.Ordinal);
            int savedLanguage = text.IndexOf("PadForge.Common.UiLanguage.Apply();", StringComparison.Ordinal);
            Assert.True(alreadyRunning > 0 && savedLanguage > alreadyRunning, "the already-running notice opens before the saved language is read");

            foreach (string dialog in new[] { "DreamcastScreenDialog.xaml.cs", "FlydigiScreenDialog.xaml.cs", "KeyboardScreenDialog.xaml.cs" })
                Assert.Contains("ConfirmDialog.", File.ReadAllText(Repo("PadForge.App", "Views", dialog)));
        }

        /// <summary>The shared confirm, which the device screens' notices and
        /// questions are, draws its title and its line in the interface's face:
        /// Aurebesh under Galactic Basic, and App.xaml's face again after. The
        /// host keeps its stand-in faces in a merged dictionary, where App.xaml
        /// keeps the real ones.</summary>
        [Fact]
        public void TheSharedConfirmDrawsInTheInterfacesFace()
        {
            DialogTestHost.Run(() =>
            {
                ResourceDictionary resources = Application.Current.Resources;
                var faces = new ResourceDictionary();
                foreach (string key in new[] { TextFaces.BodyKey, TextFaces.DisplayKey })
                {
                    faces[key] = resources[key];
                    resources.Remove(key);
                }
                resources.MergedDictionaries.Add(faces);
                var dialog = (PadForge.Views.ConfirmDialog)typeof(PadForge.Views.ConfirmDialog)
                    .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                        new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(PadForge.Views.ConfirmKind), typeof(string) }, null)!
                    .Invoke(new object[] { Strings.Instance.FlydigiScreen_Title, Strings.Instance.DreamcastScreen_ImportFailed, null, null,
                        PadForge.Views.ConfirmKind.Destructive, null });
                try
                {
                    dialog.ApplyTemplate();
                    var title = (TextBlock)dialog.Template.FindName("PART_Title", dialog);
                    var line = (ContentPresenter)dialog.Template.FindName("PART_Subtitle", dialog);
                    Assert.Same(faces[TextFaces.BodyKey], title.FontFamily);
                    Assert.Same(faces[TextFaces.BodyKey], TextElement.GetFontFamily(line));
                    UiLanguage.ApplyFaces(resources, GalacticBasic);
                    Assert.Same(TextFaces.AurebeshBody, dialog.FontFamily);
                    Assert.Same(TextFaces.AurebeshBody, title.FontFamily);
                    Assert.Same(TextFaces.AurebeshBody, TextElement.GetFontFamily(line));
                    UiLanguage.ApplyFaces(resources, AmericanEnglish);
                    Assert.Same(faces[TextFaces.BodyKey], title.FontFamily);
                    Assert.Same(faces[TextFaces.BodyKey], TextElement.GetFontFamily(line));
                }
                finally { dialog.Close(); }
            });
        }

        /// <summary>The device screens and the Screen Overlay draw their text
        /// in Segoe UI, and under Galactic Basic in Aurebesh. Each renderer
        /// draws other pixels in Galactic Basic and the same pixels again once
        /// the language goes back. They read the language of the thread they
        /// draw on, the UI thread's in PadForge.</summary>
        [Fact]
        public void TheScreensDrawTheirTextInAurebeshUnderGalacticBasic()
        {
            DialogTestHost.Run(() =>
            {
                CultureInfo was = CultureInfo.CurrentUICulture;
                try
                {
                    foreach (LanguageInfo language in LanguageCatalog.All)
                        Assert.Same(language.IsAurebesh ? TextFaces.AurebeshScreen : TextFaces.StandardScreen,
                            TextFaces.ScreenFace(language.Culture));
                    Assert.Equal("Segoe UI", TextFaces.StandardScreen.Source);

                    System.Threading.Thread.CurrentThread.CurrentUICulture = AmericanEnglish;
                    Assert.Same(TextFaces.StandardScreen, TextFaces.Screen);
                    List<byte[]> standard = ScreenText("Racing 7");
                    System.Threading.Thread.CurrentThread.CurrentUICulture = GalacticBasic;
                    Assert.Same(TextFaces.AurebeshScreen, TextFaces.Screen);
                    List<byte[]> aurebesh = ScreenText("Racing 7");
                    System.Threading.Thread.CurrentThread.CurrentUICulture = AmericanEnglish;
                    List<byte[]> back = ScreenText("Racing 7");

                    Assert.Equal(standard.Count, aurebesh.Count);
                    for (int i = 0; i < standard.Count; i++)
                    {
                        Assert.False(standard[i].SequenceEqual(aurebesh[i]), "screen " + i + " drew Galactic Basic in Segoe UI");
                        Assert.True(standard[i].SequenceEqual(back[i]), "screen " + i + " drew other pixels after the change back");
                    }
                }
                finally { System.Threading.Thread.CurrentThread.CurrentUICulture = was; }
            });
        }

        /// <summary>A screen drawn before the language changed is drawn again
        /// in the new face: the keyboard screens' last picture, the VMU's
        /// store of drawn text and the Screen Overlay's last picture each
        /// keep the face they were drawn in.</summary>
        [Fact]
        public void AScreenDrawnBeforeAChangeOfLanguageIsDrawnAgain()
        {
            DialogTestHost.Run(() =>
            {
                CultureInfo was = CultureInfo.CurrentUICulture;
                try
                {
                    System.Threading.Thread.CurrentThread.CurrentUICulture = AmericanEnglish;
                    var keyboard = new PadForge.Services.KeyboardScreenService(new PadForge.ViewModels.SettingsViewModel(), null);
                    var screen = new PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo("test-galactic-basic",
                        PadForge.Common.Input.KeyboardScreens.KeyboardScreenKind.Color, "Test Screen", "Test");
                    var data = new PadForge.Services.KeyboardScreenData
                        { Device = screen.RowGuid.ToString("D"), Mode = PadForge.Services.KeyboardScreenMode.PlayTime };
                    byte[] keyboardBefore = keyboard.Compose(screen, data, 1000, out _);
                    Assert.Same(keyboardBefore, keyboard.Compose(screen, data, 1000, out _));

                    var vmu = new PadForge.Services.DreamcastScreenService(new PadForge.ViewModels.SettingsViewModel(), null);
                    var vmuText = typeof(PadForge.Services.DreamcastScreenService)
                        .GetMethod("Text", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var vmuBefore = (byte[])vmuText.Invoke(vmu, new object[] { "0:00" });
                    Assert.Same(vmuBefore, vmuText.Invoke(vmu, new object[] { "0:00" }));

                    var overlay = new PadForge.Views.ScreenOverlayContentView();
                    overlay.Show(new PadForge.Services.ScreenOverlayContent("0:00"));
                    byte[] overlayBefore = OverlayPixels(overlay);

                    System.Threading.Thread.CurrentThread.CurrentUICulture = GalacticBasic;
                    Assert.False(keyboardBefore.SequenceEqual(keyboard.Compose(screen, data, 1000, out _)), "the keyboard screen kept its Segoe UI picture");
                    Assert.False(vmuBefore.SequenceEqual((byte[])vmuText.Invoke(vmu, new object[] { "0:00" })), "the VMU kept its Segoe UI text");
                    overlay.Show(new PadForge.Services.ScreenOverlayContent("0:00"));
                    Assert.False(overlayBefore.SequenceEqual(OverlayPixels(overlay)), "the Screen Overlay kept its Segoe UI picture");
                }
                finally { System.Threading.Thread.CurrentThread.CurrentUICulture = was; }
            });
        }

        /// <summary>The text as each device screen and the Screen Overlay draw
        /// it, in the thread's interface language.</summary>
        private static List<byte[]> ScreenText(string text)
        {
            var drawn = PadForge.Common.Input.KeyboardScreens.KeyboardScreenFormats.Kinds
                .Select(kind => PadForge.Common.Input.KeyboardScreens.KeyboardScreenRenderer.RenderText(text, kind)).ToList();
            drawn.Add(PadForge.Services.DreamcastScreenService.RenderText(text));
            drawn.Add(PadForge.Common.Input.Flydigi.FlydigiScreenPictures.FromText(text).Frames[0]);
            var overlay = new PadForge.Views.ScreenOverlayContentView();
            overlay.Show(new PadForge.Services.ScreenOverlayContent(text));
            drawn.Add(OverlayPixels(overlay));
            return drawn;
        }

        private static byte[] OverlayPixels(PadForge.Views.ScreenOverlayContentView overlay)
        {
            const int width = 300, height = 150;
            overlay.Measure(new Size(width, height));
            overlay.Arrange(new Rect(0, 0, width, height));
            overlay.UpdateLayout();
            var target = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(overlay);
            var pixels = new byte[width * height * 4];
            target.CopyPixels(pixels, width * 4, 0);
            return pixels;
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
