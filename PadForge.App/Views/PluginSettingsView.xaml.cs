using System.Windows;
using System.Windows.Controls;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// A plugin's declared settings, or one of its devices' (#600 item 17),
    /// drawn from a <see cref="PluginSettingsHost"/> its DataContext gives.
    /// The Plugins page's cards and the device panel's plugin card host it.
    /// </summary>
    public partial class PluginSettingsView : UserControl
    {
        public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
            nameof(LabelWidth), typeof(double), typeof(PluginSettingsView), new PropertyMetadata(260.0));

        public static readonly DependencyProperty CompactMarginProperty = DependencyProperty.Register(
            nameof(CompactMargin), typeof(Thickness), typeof(PluginSettingsView), new PropertyMetadata(new Thickness(272, 0, 0, 0)));

        public PluginSettingsView() => InitializeComponent();

        /// <summary>The label column's width: the Plugins page's 260, or the
        /// device panel's own.</summary>
        public double LabelWidth
        {
            get => (double)GetValue(LabelWidthProperty);
            set => SetValue(LabelWidthProperty, value);
        }

        /// <summary>Where a compact row starts: under the controls on the
        /// Plugins page, at the card's edge on the device panel.</summary>
        public Thickness CompactMargin
        {
            get => (Thickness)GetValue(CompactMarginProperty);
            set => SetValue(CompactMarginProperty, value);
        }

        /// <summary>A choice's list opens: the plugin is asked for its list
        /// as it is now, and sends it if it keeps it current.</summary>
        private void Choice_DropDownOpened(object sender, System.EventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PluginSettingItem item) item.OpenChoices();
        }
    }
}
