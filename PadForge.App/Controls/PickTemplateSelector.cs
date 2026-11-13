using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// The two looks of a choice in a picker whose list says more than its
    /// closed box (#574). In the open list each choice shows its name over a
    /// line on what it does. The closed box shows the picked name alone, so
    /// the box is one line tall, as every other picker of the row.
    ///
    /// <para>A picker hands the same selector to its closed box and to each
    /// row of its list. The closed box's presenter belongs to the picker's
    /// own template, and a row's presenter to the row's.</para>
    /// </summary>
    public sealed class PickTemplateSelector : DataTemplateSelector
    {
        /// <summary>The picked choice in the closed box.</summary>
        public DataTemplate Closed { get; set; }

        /// <summary>A choice in the open list.</summary>
        public DataTemplate List { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
            => container is FrameworkElement { TemplatedParent: ComboBox } ? Closed : List;
    }
}
