using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// A part that is made when it first shows (#574). It holds nothing
    /// until <see cref="ShowProperty"/> is true. Then it presents its data
    /// by <see cref="PartTemplateProperty"/>, and it keeps what it made,
    /// collapsed, when the flag turns false again. A part that never shows
    /// costs nothing but itself.
    ///
    /// <para>A presenter hidden by a binding on its own Visibility cannot
    /// do this. A ContentPresenter's data is nothing until its template is
    /// applied, so the binding has no value until the presenter has been
    /// measured, and a presenter that is measured while it still shows has
    /// built its template by then. The fields of a macro's step are 52
    /// groups of which a step shows one or two, and building all of them
    /// took about 200 ms each time a step was picked.</para>
    /// </summary>
    public sealed class ShownPart : Decorator
    {
        public static readonly DependencyProperty ShowProperty = DependencyProperty.Register(
            nameof(Show), typeof(bool), typeof(ShownPart), new PropertyMetadata(false, OnChanged));

        public static readonly DependencyProperty PartTemplateProperty = DependencyProperty.Register(
            nameof(PartTemplate), typeof(DataTemplate), typeof(ShownPart), new PropertyMetadata(null, OnChanged));

        /// <summary>Whether the part shows. False until it is set.</summary>
        public bool Show
        {
            get => (bool)GetValue(ShowProperty);
            set => SetValue(ShowProperty, value);
        }

        /// <summary>What the part presents its data by.</summary>
        public DataTemplate PartTemplate
        {
            get => (DataTemplate)GetValue(PartTemplateProperty);
            set => SetValue(PartTemplateProperty, value);
        }

        public ShownPart()
        {
            // What it made follows the part's data.
            DataContextChanged += (_, e) =>
            {
                if (Child is ContentPresenter made) made.Content = e.NewValue;
            };
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var part = (ShownPart)d;
            if (part.Child is not ContentPresenter made)
            {
                if (!part.Show || part.PartTemplate == null) return;
                // Given its data as it is made, and through no binding, so
                // the first measure builds it filled.
                made = new ContentPresenter { Content = part.DataContext };
                part.Child = made;
            }
            made.ContentTemplate = part.PartTemplate;
            made.Visibility = part.Show ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
