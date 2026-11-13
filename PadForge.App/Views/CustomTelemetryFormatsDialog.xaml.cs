using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using PadForge.Common.Input.Telemetry;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// The custom telemetry formats (issue #531): a list on the left, the
    /// selected format's settings and fields on the right. Works on copies,
    /// so Cancel keeps the saved list. <see cref="Result"/> holds the edited
    /// list after Save.
    /// </summary>
    public partial class CustomTelemetryFormatsDialog : DialogWindow
    {
        private readonly ObservableCollection<TelemetryFormatEdit> _formats = new();

        /// <summary>The formats as saved, set when the dialog closes on Save.</summary>
        public List<CustomTelemetryFormat> Result { get; private set; }

        public CustomTelemetryFormatsDialog(IEnumerable<CustomTelemetryFormat> formats)
        {
            InitializeComponent();
            if (formats != null)
                foreach (var format in formats)
                    if (format != null) _formats.Add(TelemetryFormatEdit.From(format));
            FormatList.ItemsSource = _formats;
            if (_formats.Count > 0) FormatList.SelectedIndex = 0;
            UpdateEditor();
        }

        private TelemetryFormatEdit Selected => FormatList.SelectedItem as TelemetryFormatEdit;

        private void UpdateEditor()
        {
            var selected = Selected;
            EditorPanel.DataContext = selected;
            EditorScroll.Visibility = selected != null ? Visibility.Visible : Visibility.Hidden;
            DuplicateFormatButton.IsEnabled = selected != null;
            DeleteFormatButton.IsEnabled = selected != null;
            // More opens for a format that uses what it holds, and is left
            // as the user has it for one that does not.
            if (UsesMore(selected)) FormatMore.IsExpanded = true;
        }

        /// <summary>Whether a format uses what More holds: a gear field's
        /// own numbers for reverse and neutral, or the key a JSON packet
        /// must carry. More opens by itself for such a format, so a setting
        /// that is in use is never out of sight.</summary>
        internal static bool UsesMore(TelemetryFormatEdit format)
            => format != null && (format.UseGearValues || (format.IsJson && !string.IsNullOrWhiteSpace(format.Key)));

        private void FormatList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
            => UpdateEditor();

        /// <summary>A name no other format has: "Format 1", "Format 2" and on.</summary>
        internal static string UniqueName(IEnumerable<string> taken, string stem)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in taken) if (name != null) names.Add(name.Trim());
            for (int n = 1; ; n++)
            {
                string candidate = string.Format(CultureInfo.CurrentCulture, stem, n);
                if (!names.Contains(candidate)) return candidate;
            }
        }

        private IEnumerable<string> Names()
        {
            foreach (var format in _formats) yield return format.Name;
        }

        private void AddFormatButton_Click(object sender, RoutedEventArgs e)
        {
            var format = new TelemetryFormatEdit
            {
                Name = UniqueName(Names(), Strings.Instance.Telemetry_Formats_NewName_Format),
            };
            format.Fields.Add(new TelemetryFieldEdit { Target = CustomTelemetryTarget.Speed, Kind = format.Kind });
            _formats.Add(format);
            FormatList.SelectedItem = format;
            ProblemText.Text = string.Empty;
        }

        private void DuplicateFormatButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Selected;
            if (selected == null) return;
            // Braces in the name are text, not format items.
            string stem = selected.Name.Trim().Replace("{", "{{").Replace("}", "}}") + " {0}";
            var copy = selected.Copy(UniqueName(Names(), stem));
            _formats.Insert(_formats.IndexOf(selected) + 1, copy);
            FormatList.SelectedItem = copy;
        }

        private void DeleteFormatButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Selected;
            if (selected == null) return;
            int index = _formats.IndexOf(selected);
            _formats.Remove(selected);
            if (_formats.Count > 0) FormatList.SelectedIndex = Math.Min(index, _formats.Count - 1);
            UpdateEditor();
            ProblemText.Text = string.Empty;
        }

        private void AddFieldButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Selected;
            if (selected == null) return;
            selected.Fields.Add(new TelemetryFieldEdit { Kind = selected.Kind });
        }

        private void RemoveFieldButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TelemetryFieldEdit field) return;
            Selected?.Fields.Remove(field);
        }

        /// <summary>The first problem across the list, or null. Two formats
        /// may not share a name, since the picker and the profiles name a
        /// format by it.</summary>
        internal static string Problem(IReadOnlyList<TelemetryFormatEdit> formats)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < formats.Count; i++)
            {
                string problem = formats[i].Problem(i + 1);
                if (problem != null) return problem;
                string name = formats[i].Name.Trim();
                if (!seen.Add(name))
                    return string.Format(CultureInfo.CurrentCulture, Strings.Instance.Telemetry_Formats_ErrorDuplicate_Format, name);
            }
            return null;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // A text box that still has focus has not pushed its value.
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox box)
                box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            // Nor has a NumberBox, which reads what was typed only when it
            // loses focus (wpfui NumberBox.OnLostFocus) or on Enter's key up,
            // and Enter on the default button or a screen reader's Invoke
            // clicks Save with the box still focused. Taking focus to Save
            // commits it. The second call moves the window's logical focus
            // when the window does not have the keyboard.
            SaveButton.Focus();
            System.Windows.Input.FocusManager.SetFocusedElement(this, SaveButton);
            string problem = Problem(_formats);
            if (problem != null)
            {
                ProblemText.Text = problem;
                return;
            }
            var result = new List<CustomTelemetryFormat>(_formats.Count);
            foreach (var format in _formats) result.Add(format.ToModel());
            Result = result;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
