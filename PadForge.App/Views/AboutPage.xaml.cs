using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PadForge.Common;
using PadForge.Services;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// About (#574): PadForge with its version and update state, the
    /// invitation, the overview, the credits and the license. The credits
    /// show their first five rows until Show All Credits opens the list, and
    /// the field over them finds a credit by its name, description or
    /// license. The page's DataContext is the settings view model, which
    /// holds the version, the update state and the update commands.
    /// </summary>
    public partial class AboutPage : UserControl
    {
        /// <summary>The rows the credits show while the list is folded.</summary>
        internal const int FirstCredits = 5;

        internal const string DocumentationUrl = "https://padforge.org/docs/";

        /// <summary>Whether Show All Credits has opened the list. It lasts
        /// as long as the page.</summary>
        private bool _creditsOpen;

        public AboutPage()
        {
            InitializeComponent();
            BuildInviteLine();
            ApplyCreditsView();
            // Weak events: the page can be collected while subscribed.
            Strings.CultureChanged += BuildInviteLine;
            Strings.CultureChanged += ApplyCreditsView;
        }

        /// <summary>The invitation sentence with only ComeUntoChrist.org
        /// rendered as the ember link, the padforge.org invite panel's
        /// split. Built in code because inline runs cannot bind around a
        /// marker whose position moves per language, and rebuilt on
        /// culture change for the same reason.</summary>
        private void BuildInviteLine()
        {
            const string Marker = "ComeUntoChrist.org";
            string text = Strings.Instance.About_TestimonyInvite ?? "";
            InviteLine.Inlines.Clear();

            var link = new Hyperlink(new Run(Marker))
            {
                NavigateUri = new Uri("https://www.comeuntochrist.org"),
            };
            link.RequestNavigate += OnInviteNavigate;
            link.SetResourceReference(TextElement.ForegroundProperty, "EmberHotBrush");

            int at = text.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0)
            {
                // A translation without the literal marker still gets a
                // working link carrying the whole sentence.
                link.Inlines.Clear();
                link.Inlines.Add(new Run(text));
                InviteLine.Inlines.Add(link);
                return;
            }
            if (at > 0) InviteLine.Inlines.Add(new Run(text.Substring(0, at)));
            InviteLine.Inlines.Add(link);
            if (at + Marker.Length < text.Length)
                InviteLine.Inlines.Add(new Run(text.Substring(at + Marker.Length)));
        }

        private void OnInviteNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private void Documentation_Click(object sender, RoutedEventArgs e) => Open(DocumentationUrl);

        private void GitHub_Click(object sender, RoutedEventArgs e) => Open("https://github.com/" + UpdateService.Repo);

        /// <summary>Opens a web page in the browser.</summary>
        private static void Open(string url)
        {
            try { ShellLaunch.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Win32Exception)
            {
                // No program is registered for web pages, and the click does
                // nothing.
            }
        }

        private void ShowAllCredits_Click(object sender, RoutedEventArgs e)
        {
            _creditsOpen = !_creditsOpen;
            ApplyCreditsView();
        }

        private void CreditsSearch_TextChanged(object sender, RoutedEventArgs e) => ApplyCreditsView();

        /// <summary>Shows the credits the field and Show All Credits ask
        /// for. With a query, every row whose name, description or license
        /// holds it shows, the button's row hides, and a line says so when
        /// no row does. Without one, the first five rows show, or every row
        /// while the list is open, over the button that opens or folds
        /// it.</summary>
        internal void ApplyCreditsView()
        {
            string query = (CreditsSearch.Text ?? string.Empty).Trim();
            bool filtering = query.Length > 0;
            int shown = 0;
            for (int i = 0; i < CreditsList.Children.Count; i++)
            {
                var row = CreditsList.Children[i];
                bool show = filtering ? Holds(row, query) : _creditsOpen || i < FirstCredits;
                row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show) shown++;
            }

            ShowAllCreditsRow.Visibility = filtering ? Visibility.Collapsed : Visibility.Visible;
            ShowAllCreditsButton.Content = _creditsOpen ? Strings.Instance.About_ShowFewerCredits : Strings.Instance.About_ShowAllCredits;

            bool none = filtering && shown == 0;
            NoCreditMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            BidiText.SetText(NoCreditMatchText, none
                ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.About_NoCreditMatch_Format, query)
                : string.Empty);
        }

        /// <summary>Whether a text of the row, its name, its description or
        /// its license, holds the query.</summary>
        private static bool Holds(DependencyObject row, string query)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(row))
            {
                if (child is TextBlock text)
                {
                    if (TextMatch.Contains(BidiText.GetText(text), query)) return true;
                }
                else if (child is DependencyObject inner && Holds(inner, query))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
