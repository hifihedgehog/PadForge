using System;
using System.Globalization;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PadForge.Common
{
    /// <summary>The app's one family for text made in code: the body face
    /// (App.xaml's BodyFontFamily), set on the text itself by resource
    /// reference, so text measured before it joins the visual tree measures as
    /// it draws, and a change of face reaches it where it already stands. A
    /// number also takes tabular figures, so each digit is as wide as the next
    /// and a readout keeps its width while its value changes.</summary>
    public static class TextFaces
    {
        /// <summary>The resource key of the body face.</summary>
        internal const string BodyKey = "BodyFontFamily";

        /// <summary>The resource key of the display face.</summary>
        internal const string DisplayKey = "DisplayFontFamily";

        /// <summary>The faces for an emoji a user picked as an icon: the
        /// color emoji face, the symbol face, then the app's body face for a
        /// character neither has. XAML takes it by x:Static, so a window
        /// built without the app's resources still draws it.</summary>
        public static readonly FontFamily Emoji = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI Variable Text, Segoe UI");

        /// <summary>The body face as App.xaml declares it. The language
        /// picker keeps it under every interface language, Galactic Basic
        /// included, so each language's English name stays readable.</summary>
        public static readonly FontFamily StandardBody = new("Segoe UI Variable Text, Segoe UI");

        /// <summary>The fonts PadForge ships, by a pack URI into its own
        /// assembly, as WPF-UI loads its icon font (Wpf.Ui 4.3.0
        /// Resources/Fonts.xaml). Reading the pack scheme's name registers the
        /// scheme with Uri where no Application has done it yet.</summary>
        private static Uri FontsFolder()
        {
            _ = PackUriHelper.UriSchemePack;
            return new Uri("pack://application:,,,/PadForge;component/Resources/Fonts/");
        }

        private static readonly Lazy<FontFamily> _aurebeshBody =
            new(() => new FontFamily(FontsFolder(), "./#Aurebesh, Segoe UI Variable Text, Segoe UI"));

        private static readonly Lazy<FontFamily> _aurebeshDisplay =
            new(() => new FontFamily(FontsFolder(), "./#Aurebesh, Segoe UI Variable Display, Segoe UI"));

        /// <summary>Galactic Basic's body face (#590): SilvinoR's Aurebesh
        /// font, then the Segoe UI Variable face for each character the font
        /// lacks, such as the arrows and the multiplication sign.</summary>
        public static FontFamily AurebeshBody => _aurebeshBody.Value;

        /// <summary>Galactic Basic's display face: the Aurebesh font, then
        /// Segoe UI Variable Display.</summary>
        public static FontFamily AurebeshDisplay => _aurebeshDisplay.Value;

        /// <summary>The face the device screens and the Screen Overlay draw
        /// text in under every language but Galactic Basic.</summary>
        public static readonly FontFamily StandardScreen = new("Segoe UI");

        private static readonly Lazy<FontFamily> _aurebeshScreen =
            new(() => new FontFamily(FontsFolder(), "./#Aurebesh, Segoe UI"));

        /// <summary>Galactic Basic's face on the device screens and the Screen
        /// Overlay: the Aurebesh font, then Segoe UI.</summary>
        public static FontFamily AurebeshScreen => _aurebeshScreen.Value;

        /// <summary>The face the device screens and the Screen Overlay draw
        /// text in under the interface language <paramref name="ui"/>.</summary>
        internal static FontFamily ScreenFace(CultureInfo ui)
            => LanguageCatalog.Resolve(ui)?.IsAurebesh == true ? AurebeshScreen : StandardScreen;

        /// <summary>The screens' face now. They draw on the UI thread, whose
        /// culture is the interface language (Strings.ChangeCulture), and read
        /// it each time they draw.</summary>
        internal static FontFamily Screen => ScreenFace(CultureInfo.CurrentUICulture);

        /// <summary>The face a language's own name is drawn in, in the
        /// language picker: the Aurebesh font for Galactic Basic, the standard
        /// body face for every other language.</summary>
        internal static FontFamily OwnNameFace(LanguageInfo language)
            => language?.IsAurebesh == true ? AurebeshBody : StandardBody;

        /// <summary>The body face, on a TextBlock or a Run.</summary>
        internal static T Body<T>(T text) where T : DependencyObject
        {
            switch (text)
            {
                case FrameworkElement element:
                    element.SetResourceReference(TextElement.FontFamilyProperty, BodyKey);
                    break;
                case FrameworkContentElement content:
                    content.SetResourceReference(TextElement.FontFamilyProperty, BodyKey);
                    break;
            }
            return text;
        }

        /// <summary>The body face with tabular figures, on a TextBlock or a
        /// Run, for a number that changes or stands in a column.</summary>
        internal static T Numbers<T>(T text) where T : DependencyObject
        {
            Body(text);
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            return text;
        }

        /// <summary>A control that draws its own text takes the face by
        /// resource reference on itself, and measures and draws with the
        /// family it then holds. TextElement.FontFamily affects measure and
        /// render (dotnet/wpf TextElement.cs), so a change of face redraws
        /// it.</summary>
        internal static void Follow(FrameworkElement control, string key)
            => control.SetResourceReference(TextElement.FontFamilyProperty, key);

        /// <summary>The face a control set up by <see cref="Follow"/> draws
        /// in, as a Typeface.</summary>
        internal static Typeface TypefaceOf(DependencyObject control, FontWeight weight)
            => new(TextElement.GetFontFamily(control), FontStyles.Normal, weight, FontStretches.Normal);
    }
}
