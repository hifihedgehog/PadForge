using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// Parts a panel makes from a list and stands before its own (#574), as
    /// the inputs of a macro's trigger stand on one line with Record and Add
    /// from List after the last of them. One part is made for each item from
    /// <see cref="TemplateProperty"/>, with a part from
    /// <see cref="BetweenProperty"/> between two when that is set. Each part
    /// has its item as its data.
    ///
    /// <para>The panel's own parts are never taken out and put back, so one
    /// that holds the keyboard focus keeps it when the list changes. A list
    /// control cannot do that: its panel holds its items and nothing else,
    /// and a list of items and fixed parts together takes every part out and
    /// puts it back when the items change.</para>
    ///
    /// <para>The list is read when <see cref="SourceProperty"/> gets a new
    /// value. A list that changes in place is not followed.</para>
    /// </summary>
    public static class LeadItems
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(IEnumerable), typeof(LeadItems), new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty TemplateProperty = DependencyProperty.RegisterAttached(
            "Template", typeof(DataTemplate), typeof(LeadItems), new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty BetweenProperty = DependencyProperty.RegisterAttached(
            "Between", typeof(DataTemplate), typeof(LeadItems), new PropertyMetadata(null, OnChanged));

        // How many of the panel's first parts were made from the list.
        private static readonly DependencyProperty MadeProperty = DependencyProperty.RegisterAttached(
            "Made", typeof(int), typeof(LeadItems), new PropertyMetadata(0));

        public static IEnumerable GetSource(Panel panel) => (IEnumerable)panel.GetValue(SourceProperty);

        public static void SetSource(Panel panel, IEnumerable value) => panel.SetValue(SourceProperty, value);

        public static DataTemplate GetTemplate(Panel panel) => (DataTemplate)panel.GetValue(TemplateProperty);

        public static void SetTemplate(Panel panel, DataTemplate value) => panel.SetValue(TemplateProperty, value);

        public static DataTemplate GetBetween(Panel panel) => (DataTemplate)panel.GetValue(BetweenProperty);

        public static void SetBetween(Panel panel, DataTemplate value) => panel.SetValue(BetweenProperty, value);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Panel panel) return;
            var parts = panel.Children;
            parts.RemoveRange(0, (int)panel.GetValue(MadeProperty));
            int made = 0;
            var template = GetTemplate(panel);
            var between = GetBetween(panel);
            if (template != null && GetSource(panel) is IEnumerable source)
            {
                foreach (object item in source)
                {
                    if (made > 0 && between != null)
                        parts.Insert(made++, new ContentPresenter { Content = item, ContentTemplate = between });
                    parts.Insert(made++, new ContentPresenter { Content = item, ContentTemplate = template });
                }
            }
            panel.SetValue(MadeProperty, made);
        }
    }
}
