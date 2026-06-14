using System;
using System.Text;
using System.Windows;
using Wpf.Ui.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// First-contact pairing approval (issue #138). Shows the short authentication
    /// string for the user to compare against the other PC's screen, the peer's
    /// identity fingerprint, and the consent warning. DialogResult is true on Pair.
    /// </summary>
    public partial class RemoteLinkPairDialog : FluentWindow
    {
        public RemoteLinkPairDialog(string sas, string fingerprintHex)
        {
            InitializeComponent();
            SasText.Text = FormatSas(sas);
            IdentityText.Text = FormatFingerprint(fingerprintHex);
        }

        /// <summary>True if the user chose to restrict this peer to gamepad-only output.</summary>
        public bool GamepadOnly => GamepadOnlyCheck.IsChecked == true;

        // "123456" -> "123 456" for easier eyeball comparison.
        private static string FormatSas(string sas)
        {
            if (string.IsNullOrEmpty(sas)) return "";
            return sas.Length == 6 ? $"{sas.Substring(0, 3)} {sas.Substring(3, 3)}" : sas;
        }

        // First 16 hex chars of the fingerprint, grouped in fours.
        private static string FormatFingerprint(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return "";
            string head = hex.Length > 16 ? hex.Substring(0, 16) : hex;
            var sb = new StringBuilder(head.Length + 4);
            for (int i = 0; i < head.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append(' ');
                sb.Append(head[i]);
            }
            return sb.ToString();
        }

        private void Pair_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
