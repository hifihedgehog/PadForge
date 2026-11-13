using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PadForge.Controls
{
    /// <summary>
    /// A search field as the boards draw it (#574): a field 34 tall with a
    /// magnifier, a borderless input and a placeholder that shows while the
    /// field is empty. Its look is the style in Resources/Shell.xaml. The
    /// field makes no automation element of its own, so the input carries
    /// the placeholder as its name for a screen reader and the field's
    /// AutomationId for a client. Esc in a field with text empties it.
    /// </summary>
    public sealed class SearchField : Control
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(SearchField),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
            nameof(Placeholder), typeof(string), typeof(SearchField), new PropertyMetadata(string.Empty));

        public static readonly RoutedEvent TextChangedEvent = EventManager.RegisterRoutedEvent(
            nameof(TextChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SearchField));

        static SearchField()
        {
            // The input takes the keyboard. The field itself is no stop.
            FocusableProperty.OverrideMetadata(typeof(SearchField), new FrameworkPropertyMetadata(false));
            IsTabStopProperty.OverrideMetadata(typeof(SearchField), new FrameworkPropertyMetadata(false));
        }

        /// <summary>What the field holds.</summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>The hint the empty field shows, and the input's name.</summary>
        public string Placeholder
        {
            get => (string)GetValue(PlaceholderProperty);
            set => SetValue(PlaceholderProperty, value);
        }

        /// <summary>Raised after <see cref="Text"/> changed.</summary>
        public event RoutedEventHandler TextChanged
        {
            add => AddHandler(TextChangedEvent, value);
            remove => RemoveHandler(TextChangedEvent, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SearchField)d).RaiseEvent(new RoutedEventArgs(TextChangedEvent));

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(Text))
            {
                SetCurrentValue(TextProperty, string.Empty);
                e.Handled = true;
            }
            base.OnPreviewKeyDown(e);
        }
    }
}
