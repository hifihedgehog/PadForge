using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PadForge.Common
{
    /// <summary>
    /// Gives every window the three things it takes from the interface
    /// language: the language its text is declared in, as xml:lang does in
    /// markup, the direction it is laid out in, and the faces its text is
    /// drawn in.
    ///
    /// <para>The declared language is for Chinese, Japanese and Korean
    /// (#504). The three share their characters, and WPF picks the font for
    /// one from the language its text is declared in: Microsoft YaHei UI
    /// under zh-Hans, Microsoft JhengHei UI under zh-Hant, Yu Gothic UI
    /// under ja, Malgun Gothic under ko. Text with no declared language is
    /// en-US, and for en-US the Japanese font comes first. A Simplified
    /// Chinese interface was drawn in Yu Gothic UI.</para>
    ///
    /// <para>No other language is declared. A window's language is also the
    /// culture its bindings format and parse numbers in, and the pad page
    /// has some 250 number boxes that read and write the en-US notation.
    /// Declared German would turn a typed "0.5" into 5. So a language is
    /// declared only when its culture writes numbers as en-US does, which
    /// <see cref="WritesNumbersAsEnUsDoes"/> checks and UiLanguageTests
    /// holds against every number format in the markup.</para>
    ///
    /// <para>The direction is for Arabic, Persian, Urdu and Central Kurdish
    /// (#505). A window laid out right to left mirrors its panels. Drawings
    /// pin themselves left to right, so a controller's left stick stays on
    /// the left.</para>
    ///
    /// <para>The faces are for Galactic Basic (#590), English written in the
    /// Aurebesh alphabet: App.xaml's two faces become the Aurebesh font, and
    /// every other language has App.xaml's own.</para>
    /// </summary>
    internal static class UiLanguage
    {
        /// <summary>The language WPF gives text that declares none.</summary>
        internal static readonly XmlLanguage Undeclared = XmlLanguage.GetLanguage("en-US");

        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private static readonly ConcurrentDictionary<XmlLanguage, bool> _numbers = new();
        private static bool _started;

        /// <summary>The language a window declares while the interface is in
        /// <paramref name="ui"/>. A specific culture is passed through as it
        /// is (zh-TW, ja-JP): WPF walks its parents to the font table.</summary>
        internal static XmlLanguage Declared(CultureInfo ui)
        {
            string name = ui?.IetfLanguageTag ?? "";
            int dash = name.IndexOf('-');
            string language = (dash < 0 ? name : name.Substring(0, dash)).ToLowerInvariant();
            if (language != "zh" && language != "ja" && language != "ko") return Undeclared;
            // WPF has a table for zh-Hans and one for zh-Hant, none for bare zh.
            string bare = language == "zh" ? "zh-Hans" : language;
            XmlLanguage declared;
            try { declared = XmlLanguage.GetLanguage(dash < 0 ? bare : name); }
            catch (ArgumentException) { declared = XmlLanguage.GetLanguage(bare); }
            return WritesNumbersAsEnUsDoes(declared) ? declared : Undeclared;
        }

        /// <summary>True when bindings under <paramref name="language"/>
        /// format and parse a number exactly as they do under en-US. A tag
        /// with no culture of its own (zh-Latn) falls to the invariant
        /// culture, which writes a percentage another way, so it is false.</summary>
        internal static bool WritesNumbersAsEnUsDoes(XmlLanguage language)
            => _numbers.GetOrAdd(language, static l =>
            {
                CultureInfo culture;
                try { culture = l.GetSpecificCulture(); }
                catch (InvalidOperationException) { return false; }
                if (culture.Name.Length == 0) return false;
                NumberFormatInfo a = culture.NumberFormat, b = EnUs.NumberFormat;
                return a.NumberDecimalSeparator == b.NumberDecimalSeparator
                    && a.NumberGroupSeparator == b.NumberGroupSeparator
                    && a.NumberGroupSizes.AsSpan().SequenceEqual(b.NumberGroupSizes)
                    && a.NumberNegativePattern == b.NumberNegativePattern
                    && a.NegativeSign == b.NegativeSign
                    && a.PositiveSign == b.PositiveSign
                    && a.PercentSymbol == b.PercentSymbol
                    && a.PercentDecimalSeparator == b.PercentDecimalSeparator
                    && a.PercentGroupSeparator == b.PercentGroupSeparator
                    && a.PercentPositivePattern == b.PercentPositivePattern
                    && a.PercentNegativePattern == b.PercentNegativePattern
                    && a.DigitSubstitution == DigitShapes.None;
            });

        /// <summary>The direction a window is laid out in while the
        /// interface is in <paramref name="ui"/>. Only a language in the
        /// catalog reads right to left: Hebrew Windows with no Hebrew
        /// translation shows English, left to right.</summary>
        internal static FlowDirection Direction(CultureInfo ui)
            => LanguageCatalog.IsRightToLeft(ui) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        /// <summary>Declares the language and direction on every window
        /// that opens from here on, and again on all of them whenever the
        /// language changes. Called once, at the top of startup. A window
        /// reads the language as it loads, so the saved one is in place by
        /// then.</summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
            TextDirection.Start();
            PadForge.Resources.Strings.Strings.CultureChanged += Apply;
            Apply();
        }

        /// <summary>A window takes its language and direction as it loads,
        /// before its first frame. Menus, tooltips and drop-downs take both
        /// from the window they open from.</summary>
        internal static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            CultureInfo ui = CultureInfo.CurrentUICulture;
            Declare(sender as Window, Declared(ui), Direction(ui));
        }

        /// <summary>Declares the current language and direction on every open window.</summary>
        internal static void Apply()
        {
            // Read on the calling thread, which is the one whose culture just changed.
            CultureInfo ui = CultureInfo.CurrentUICulture;
            XmlLanguage language = Declared(ui);
            FlowDirection direction = Direction(ui);
            Application app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) ApplyOnAll(app, ui, language, direction);
            else app.Dispatcher.BeginInvoke(new Action(() => ApplyOnAll(app, ui, language, direction)));
        }

        private static void ApplyOnAll(Application app, CultureInfo ui, XmlLanguage language, FlowDirection direction)
        {
            ApplyFaces(app.Resources, ui);
            foreach (Window window in app.Windows) Declare(window, language, direction);
        }

        /// <summary>The faces the interface's text is drawn in under
        /// <paramref name="ui"/>. Galactic Basic sets BodyFontFamily and
        /// DisplayFontFamily to the Aurebesh faces in the application's own
        /// resources, over the merged dictionary in App.xaml that declares
        /// them. Every other language takes them out again, and App.xaml's
        /// own faces, the same objects, answer once more. ResourceDictionary's
        /// indexer reads its own entries before its merged ones, and its
        /// setter and Remove act on its own entries and tell every resource
        /// reference (dotnet/wpf ResourceDictionary.cs). Markup and code take
        /// both faces by resource reference, so a page already built redraws
        /// in the new face.</summary>
        internal static void ApplyFaces(ResourceDictionary resources, CultureInfo ui)
        {
            if (resources == null) return;
            bool aurebesh = LanguageCatalog.Resolve(ui)?.IsAurebesh == true;
            SetFace(resources, TextFaces.BodyKey, TextFaces.AurebeshBody, aurebesh);
            SetFace(resources, TextFaces.DisplayKey, TextFaces.AurebeshDisplay, aurebesh);
        }

        private static void SetFace(ResourceDictionary resources, string key, FontFamily aurebesh, bool on)
        {
            bool set = ReferenceEquals(resources[key], aurebesh);
            if (on && !set) resources[key] = aurebesh;
            else if (!on && set) resources.Remove(key);
        }

        internal static void Declare(Window window, XmlLanguage language, FlowDirection direction)
        {
            if (window == null) return;
            if ((bool)window.GetValue(KeepsLeftToRightProperty)) direction = FlowDirection.LeftToRight;
            if (!Equals(window.Language, language)) window.Language = language;
            if (window.FlowDirection != direction) window.FlowDirection = direction;
            // Each TextBlock in a right-to-left window takes the direction its own text needs.
            TextDirection.SetWatch(window, direction == FlowDirection.RightToLeft);
        }

        /// <summary>Set on a window that is itself a picture of a physical
        /// thing: the touchpad overlay, the on-screen menus, the light gun's
        /// calibration targets. Such a window stays left to right in every
        /// language. Mirrored, a target drawn at the top left would sit at
        /// the top right while the calibration still read it as the left.</summary>
        internal static readonly DependencyProperty KeepsLeftToRightProperty = DependencyProperty.RegisterAttached(
            "KeepsLeftToRight", typeof(bool), typeof(UiLanguage), new PropertyMetadata(false));

        internal static void KeepLeftToRight(Window window) => window?.SetValue(KeepsLeftToRightProperty, true);
    }
}
