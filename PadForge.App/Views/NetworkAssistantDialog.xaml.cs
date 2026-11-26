using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;

namespace PadForge.Views
{
    /// <summary>
    /// Add a Network Assistant… (#600 item 53): the name and the level a new
    /// key is made with, then the key itself, shown once, with the line to
    /// paste for the host picked. A host gets a line of its own only once it
    /// was seen connecting to a network address on a plain HTTP URL with a
    /// Bearer header (<see cref="NetworkDoor.PasteHosts"/>). Every other host
    /// takes the address and the header.
    /// </summary>
    public partial class NetworkAssistantDialog : DialogWindow
    {
        private sealed record Choice(string Key, string Name)
        {
            public override string ToString() => Name;
        }

        private sealed record LevelChoice(AutomationLevel Level, string Name)
        {
            public override string ToString() => Name;
        }

        private readonly bool _full;
        private string _key, _address;

        private NetworkAssistantDialog(AutomationLevel cap, bool full)
        {
            InitializeComponent();
            _full = full;
            var s = Strings.Instance;
            Title = s.Net_Add_Title;
            Subtitle = s.Net_Add_Subtitle;
            var levels = Enum.GetValues<AutomationLevel>().Where(l => l <= cap)
                .Select(l => new LevelChoice(l, WebDoor.LevelName(l))).ToList();
            LevelBox.ItemsSource = levels;
            LevelBox.SelectedItem = levels.LastOrDefault();
            Loaded += (_, _) => NameBox.Focus();
        }

        /// <summary>The name typed, when Make Key was pressed.</summary>
        internal string KeyName { get; private set; }

        /// <summary>The level picked.</summary>
        internal AutomationLevel Level { get; private set; }

        /// <summary>Asks for a new key's name and a level up to
        /// <paramref name="cap"/>. Null when the user canceled.</summary>
        internal static (string Name, AutomationLevel Level)? Ask(Window owner, AutomationLevel cap, bool full)
        {
            var dialog = new NetworkAssistantDialog(cap, full) { Owner = owner };
            return dialog.ShowDialog() == true ? (dialog.KeyName, dialog.Level) : null;
        }

        /// <summary>Shows a new key once, with the line to paste.</summary>
        internal static void ShowKey(Window owner, string name, string key, string address)
        {
            var dialog = ForKey(name, key, address);
            dialog.Owner = owner;
            dialog.ShowDialog();
        }

        /// <summary>The window that shows a key, made and not shown.</summary>
        internal static NetworkAssistantDialog ForKey(string name, string key, string address)
        {
            var dialog = new NetworkAssistantDialog(AutomationLevel.Commands, false);
            dialog.ShowKeyPanel(name, key, address);
            return dialog;
        }

        private void ShowKeyPanel(string name, string key, string address)
        {
            var s = Strings.Instance;
            _key = key;
            _address = address;
            Title = string.Format(CultureInfo.CurrentCulture, s.Net_Key_Title_Format, name);
            Subtitle = s.Net_Key_Subtitle;
            AskPanel.Visibility = Visibility.Collapsed;
            KeyPanel.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            MakeButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Visible;
            DoneButton.IsDefault = true;
            DoneButton.IsCancel = true;
            KeyBox.Text = key;
            var hosts = NetworkDoor.PasteHosts.Select(h => new Choice(h.Key, h.Name)).Append(new Choice(null, s.Net_Key_Other)).ToList();
            HostBox.ItemsSource = hosts;
            HostBox.SelectedItem = hosts[0];
            ShowLine();
        }

        private void ShowLine()
        {
            var s = Strings.Instance;
            string host = (HostBox.SelectedItem as Choice)?.Key;
            LineBox.Text = NetworkDoor.PasteLine(host, _address, _key, s.Web_Address_Label, s.Net_Key_Header);
            LineHint.Text = host == null ? s.Net_Key_OtherHint : s.Net_Key_RunHint;
        }

        private void Host_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_key != null) ShowLine();
        }

        private void Make_Click(object sender, RoutedEventArgs e)
        {
            var s = Strings.Instance;
            string name = (NameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                Say(s.Net_Add_NoName);
                NameBox.Focus();
                return;
            }
            if (_full)
            {
                Say(string.Format(CultureInfo.CurrentCulture, Strings.CountFormat(nameof(Strings.Net_Add_Full_Format), NetworkDoor.MostKeys), NetworkDoor.MostKeys));
                return;
            }
            KeyName = name;
            Level = (LevelBox.SelectedItem as LevelChoice)?.Level ?? AutomationLevel.Commands;
            DialogResult = true;
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        private void CopyKey_Click(object sender, RoutedEventArgs e) => Copy(KeyBox.Text);

        private void CopyLine_Click(object sender, RoutedEventArgs e) => Copy(LineBox.Text);

        private void Copy(string text)
        {
            try
            {
                ShellLaunch.SetClipboardText(text);
                LineHint.Text = Strings.Instance.Web_Address_Copied;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                LineHint.Text = Strings.Instance.Extras_Automation_NotCopied;
            }
        }

        private void Say(string message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        protected override void OnClosed(EventArgs e)
        {
            // The key leaves the window's boxes when it closes.
            KeyBox.Text = string.Empty;
            LineBox.Text = string.Empty;
            _key = null;
            base.OnClosed(e);
        }
    }
}
