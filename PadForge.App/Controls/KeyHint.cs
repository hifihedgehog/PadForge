using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PadForge.Controls
{
    /// <summary>A hint at the foot of Search (#574): words with keys in
    /// them, each key drawn as a small key label ("[Enter] to open"). The
    /// format's {0} and {1} are <see cref="Key0"/> and <see cref="Key1"/>.
    /// A screen reader hears the hint as one sentence.</summary>
    public sealed class KeyHint : TextBlock
    {
        public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
            nameof(Format), typeof(string), typeof(KeyHint), new PropertyMetadata(string.Empty, (d, _) => ((KeyHint)d).Rebuild()));

        public static readonly DependencyProperty Key0Property = DependencyProperty.Register(
            nameof(Key0), typeof(string), typeof(KeyHint), new PropertyMetadata(string.Empty, (d, _) => ((KeyHint)d).Rebuild()));

        public static readonly DependencyProperty Key1Property = DependencyProperty.Register(
            nameof(Key1), typeof(string), typeof(KeyHint), new PropertyMetadata(string.Empty, (d, _) => ((KeyHint)d).Rebuild()));

        public string Format
        {
            get => (string)GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        public string Key0
        {
            get => (string)GetValue(Key0Property);
            set => SetValue(Key0Property, value);
        }

        public string Key1
        {
            get => (string)GetValue(Key1Property);
            set => SetValue(Key1Property, value);
        }

        private static readonly Regex Hole = new(@"\{(\d)\}");

        private void Rebuild()
        {
            Inlines.Clear();
            string format = Format ?? string.Empty;
            int at = 0;
            foreach (Match m in Hole.Matches(format))
            {
                if (m.Index > at) Inlines.Add(new Run(format.Substring(at, m.Index - at)));
                string key = m.Groups[1].Value == "0" ? Key0 : Key1;
                Inlines.Add(new InlineUIContainer(Chip(key)) { BaselineAlignment = BaselineAlignment.Center });
                at = m.Index + m.Length;
            }
            if (at < format.Length) Inlines.Add(new Run(format.Substring(at)));
            string spoken;
            try { spoken = string.Format(format, Key0 ?? string.Empty, Key1 ?? string.Empty); }
            catch (System.FormatException) { spoken = format; }
            AutomationProperties.SetName(this, spoken);
        }

        private static Border Chip(string key)
        {
            // The hint's own name holds the keys, so a key's label is out
            // of the control view: a listener heard the sentence and then
            // each key again.
            var text = new GlyphText { Text = key ?? string.Empty };
            text.SetResourceReference(StyleProperty, "KeyChipText");
            var chip = new Border { Child = text };
            chip.SetResourceReference(StyleProperty, "KeyChip");
            return chip;
        }
    }
}
