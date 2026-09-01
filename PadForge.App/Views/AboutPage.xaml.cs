using System;
using System.Windows.Controls;
using System.Windows.Documents;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Views
{
    public partial class AboutPage : UserControl
    {
        public AboutPage()
        {
            InitializeComponent();
            BuildInviteLine();
            // Weak event: the page can be collected while subscribed.
            Strings.CultureChanged += BuildInviteLine;
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
            link.SetResourceReference(TextElement.ForegroundProperty, "EmberBrush");

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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
    }
}
