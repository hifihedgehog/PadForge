using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PadForge.Resources.Strings;

namespace PadForge.Controls
{
    /// <summary>A reset button with the existing icon style and a localized setting label.</summary>
    public sealed class SettingResetButton : Wpf.Ui.Controls.Button
    {
        public static readonly DependencyProperty SettingLabelProperty = DependencyProperty.Register(
            nameof(SettingLabel), typeof(string), typeof(SettingResetButton), new PropertyMetadata("", RefreshTooltip));

        private static readonly DependencyProperty ResetFormatProperty = DependencyProperty.Register(
            "ResetFormat", typeof(string), typeof(SettingResetButton), new PropertyMetadata("", RefreshTooltip));

        public string SettingLabel
        {
            get => (string)GetValue(SettingLabelProperty);
            set => SetValue(SettingLabelProperty, value);
        }

        public SettingResetButton()
        {
            SetResourceReference(StyleProperty, "SettingResetButton");
            // Binding the format separately also refreshes user-supplied labels
            // when the UI language changes and the label itself stays the same.
            SetBinding(ResetFormatProperty, new Binding(nameof(Strings.Pad_ResetSection_Format)) { Source = Strings.Instance });
        }

        private static void RefreshTooltip(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var button = (SettingResetButton)sender;
            button.SetCurrentValue(ToolTipProperty, FormatTooltip((string)button.GetValue(ResetFormatProperty), button.SettingLabel));
        }

        internal static string FormatTooltip(string format, string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return Strings.Instance.Common_Reset;
            label = label.Trim().TrimEnd(':', '\uFF1A', '\u2026').TrimEnd();
            if (label.EndsWith("...", StringComparison.Ordinal)) label = label[..^3].TrimEnd();
            return string.Format(CultureInfo.CurrentUICulture,
                string.IsNullOrEmpty(format) ? Strings.Instance.Pad_ResetSection_Format : format, label);
        }
    }
}
