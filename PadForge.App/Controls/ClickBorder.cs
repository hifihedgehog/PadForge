using System;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// A Border that acts as a button: a chip, a swatch, a tile. It draws
    /// exactly as a Border does and raises Click at the moment the mouse
    /// handler it replaces ran (button up, or button down with ClickMode
    /// Press), so nothing changes for the mouse. It also raises Click from
    /// Enter or Space while it has keyboard focus, and from a screen reader's
    /// Invoke. Tab reaches it, and the app's focus ring shows only when the
    /// keyboard put the focus there.
    ///
    /// <para>IsSelected set (true or false) makes it one choice among several:
    /// a screen reader hears a radio button, checked on the current choice.
    /// IsChecked set makes it a toggle. Unset, it is a button.</para>
    /// </summary>
    public class ClickBorder : Border
    {
        public static readonly RoutedEvent ClickEvent = EventManager.RegisterRoutedEvent(
            nameof(Click), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ClickBorder));

        public event RoutedEventHandler Click
        {
            add => AddHandler(ClickEvent, value);
            remove => RemoveHandler(ClickEvent, value);
        }

        public static readonly DependencyProperty ClickModeProperty = DependencyProperty.Register(
            nameof(ClickMode), typeof(ClickMode), typeof(ClickBorder), new PropertyMetadata(ClickMode.Release));

        /// <summary>Release (the default) raises Click on the left button's
        /// release, Press on its press, as the replaced handler did.</summary>
        public ClickMode ClickMode
        {
            get => (ClickMode)GetValue(ClickModeProperty);
            set => SetValue(ClickModeProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            nameof(IsSelected), typeof(bool?), typeof(ClickBorder), new PropertyMetadata(null, OnStateChanged));

        /// <summary>Whether this choice is the current one. Null for a border
        /// that is no choice.</summary>
        public bool? IsSelected
        {
            get => (bool?)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
            nameof(IsChecked), typeof(bool?), typeof(ClickBorder), new PropertyMetadata(null, OnStateChanged));

        /// <summary>Whether the toggle is on. Null for a border that is no
        /// toggle.</summary>
        public bool? IsChecked
        {
            get => (bool?)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

        static ClickBorder()
        {
            FocusableProperty.OverrideMetadata(typeof(ClickBorder), new FrameworkPropertyMetadata(true));
        }

        protected override void OnInitialized(EventArgs e)
        {
            // The Border style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Border));
            base.OnInitialized(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (ClickMode == ClickMode.Press && !e.Handled)
                RaiseClick(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (ClickMode != ClickMode.Press && !e.Handled)
                RaiseClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || HeldModifiers.Now != ModifierKeys.None || e.OriginalSource != this)
                return;
            if (e.Key is Key.Enter or Key.Space)
            {
                RaiseClick(null);
                e.Handled = true;
            }
        }

        /// <summary>Raises Click. A handler that handles Click handles the
        /// mouse event behind it too, as it did when it ran on that event.</summary>
        private void RaiseClick(RoutedEventArgs cause)
        {
            if (!IsEnabled)
                return;
            var args = new RoutedEventArgs(ClickEvent, this);
            RaiseEvent(args);
            if (args.Handled && cause != null)
                cause.Handled = true;
        }

        internal void PerformClick() => RaiseClick(null);

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ClickBorder border || UIElementAutomationPeer.FromElement(border) is not ClickBorderPeer peer)
                return;
            if (e.Property == IsSelectedProperty && e.NewValue is bool selected)
            {
                peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !selected, selected);
                if (selected)
                    peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            }
            else if (e.Property == IsCheckedProperty && e.NewValue is bool on)
            {
                peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                    on ? ToggleState.Off : ToggleState.On, on ? ToggleState.On : ToggleState.Off);
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ClickBorderPeer(this);

        /// <summary>The text a border shows, its text pieces joined with a
        /// space and the hair spaces of a letter-spaced token removed
        /// ("A L L" reads "ALL"). Empty when it shows none.</summary>
        internal static string ShownText(DependencyObject root)
        {
            var text = new StringBuilder();
            Collect(root, text);
            return text.ToString().Trim();
        }

        private static void Collect(DependencyObject d, StringBuilder text)
        {
            if (d is UIElement { IsVisible: false } && d is not ClickBorder)
                return;
            if (d is TextBlock tb && tb is not GlyphText)
            {
                string piece = tb.Text?.Replace("\u200A", string.Empty).Trim();
                if (!string.IsNullOrEmpty(piece) && !HasGlyph(piece))
                {
                    if (text.Length > 0) text.Append(' ');
                    text.Append(piece);
                }
                return;
            }
            int count = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < count; i++)
                Collect(VisualTreeHelper.GetChild(d, i), text);
        }

        private static bool HasGlyph(string text)
        {
            foreach (char c in text)
                if (c >= 0xE000 && c <= 0xF8FF)
                    return true;
            return false;
        }

        /// <summary>A button, a radio button or a toggle, by which state the
        /// border carries. Named by AutomationProperties.Name, else the text
        /// it shows, else its tooltip string.</summary>
        private sealed class ClickBorderPeer : FrameworkElementAutomationPeer, IInvokeProvider, ISelectionItemProvider, IToggleProvider
        {
            public ClickBorderPeer(ClickBorder owner) : base(owner) { }

            private ClickBorder Border => (ClickBorder)Owner;

            protected override string GetClassNameCore() => nameof(ClickBorder);

            protected override AutomationControlType GetAutomationControlTypeCore()
                => Border.IsSelected != null ? AutomationControlType.RadioButton : AutomationControlType.Button;

            protected override string GetNameCore()
            {
                string name = base.GetNameCore();
                if (!string.IsNullOrEmpty(name))
                    return name;
                name = ShownText(Border);
                if (!string.IsNullOrEmpty(name))
                    return name;
                return Border.ToolTip as string ?? string.Empty;
            }

            public override object GetPattern(PatternInterface patternInterface)
            {
                if (Border.IsSelected != null)
                    return patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);
                if (Border.IsChecked != null)
                    return patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);
                return patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
            }

            private void Activate()
            {
                if (!IsEnabled())
                    throw new ElementNotEnabledException();
                Border.PerformClick();
            }

            void IInvokeProvider.Invoke() => Activate();

            bool ISelectionItemProvider.IsSelected => Border.IsSelected == true;

            IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => null;

            void ISelectionItemProvider.Select()
            {
                if (Border.IsSelected != true)
                    Activate();
            }

            void ISelectionItemProvider.AddToSelection()
            {
                if (Border.IsSelected != true)
                    throw new InvalidOperationException();
            }

            void ISelectionItemProvider.RemoveFromSelection()
            {
                if (Border.IsSelected == true)
                    throw new InvalidOperationException();
            }

            ToggleState IToggleProvider.ToggleState => Border.IsChecked == true ? ToggleState.On : ToggleState.Off;

            void IToggleProvider.Toggle() => Activate();
        }
    }
}
