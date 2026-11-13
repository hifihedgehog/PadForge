using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PadForge.Controls
{
    /// <summary>
    /// A dialog drawn inside the window over the page area (#574), as the
    /// Add a Controller and Search boards draw one: a ground over the pages,
    /// the frame at its top center, and the sidebar and title bar as they
    /// are. Escape asks the owner to close it wherever the keyboard focus is
    /// in the window, Tab cycles inside it, and a screen reader hears a
    /// dialog. The owner opens and closes it, and takes the focus back to
    /// what opened it.
    /// </summary>
    [TemplatePart(Name = FramePartName, Type = typeof(FrameworkElement))]
    public sealed class ShellDialog : ContentControl
    {
        private const string FramePartName = "PART_Frame";

        public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
            nameof(IsOpen), typeof(bool), typeof(ShellDialog), new PropertyMetadata(false));

        public static readonly DependencyProperty FrameMaxWidthProperty = DependencyProperty.Register(
            nameof(FrameMaxWidth), typeof(double), typeof(ShellDialog), new PropertyMetadata(760.0));

        public static readonly DependencyProperty ClosesOnGroundPressProperty = DependencyProperty.Register(
            nameof(ClosesOnGroundPress), typeof(bool), typeof(ShellDialog), new PropertyMetadata(false));

        /// <summary>Whether the dialog shows. Its style shows it while true.</summary>
        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            private set => SetValue(IsOpenProperty, value);
        }

        /// <summary>The widest the frame gets: 760 for Add a Controller, 660
        /// for Search, as their boards draw them.</summary>
        public double FrameMaxWidth
        {
            get => (double)GetValue(FrameMaxWidthProperty);
            set => SetValue(FrameMaxWidthProperty, value);
        }

        /// <summary>Whether a press on the ground around the frame asks the
        /// owner to close the dialog. Search closes so, as its board's dark
        /// page area invites. Add a Controller does not: its ground holds the
        /// scroll bar of a frame taller than the window.</summary>
        public bool ClosesOnGroundPress
        {
            get => (bool)GetValue(ClosesOnGroundPressProperty);
            set => SetValue(ClosesOnGroundPressProperty, value);
        }

        /// <summary>Raised by Escape, and by a press on the ground when
        /// <see cref="ClosesOnGroundPress"/> is set. The owner closes the
        /// dialog.</summary>
        public event EventHandler CloseRequested;

        private IInputElement _opener;
        private Window _window;
        private FrameworkElement _frame;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _frame = GetTemplateChild(FramePartName) as FrameworkElement;
        }

        /// <summary>Shows the dialog. <paramref name="opener"/> takes the
        /// focus back when it closes.</summary>
        public void Open(IInputElement opener)
        {
            if (IsOpen) return;
            _opener = opener;
            IsOpen = true;
            _window = Window.GetWindow(this);
            if (_window != null) _window.PreviewKeyDown += OnWindowKeyDown;
        }

        /// <summary>Hides the dialog, and gives the focus back to its opener
        /// when asked and the opener still shows.</summary>
        public void Close(bool returnFocus)
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (_window != null) _window.PreviewKeyDown -= OnWindowKeyDown;
            _window = null;
            var opener = _opener;
            _opener = null;
            if (returnFocus && (opener is ContentElement || opener is UIElement { IsVisible: true }))
                opener.Focus();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (!ClosesOnGroundPress || e.Handled || !IsOpen) return;
            // A press inside the frame, or on the scroll bar of a frame
            // taller than the page area, is not a press on the ground.
            for (var d = e.OriginalSource as DependencyObject; d != null && d != this; d = PadForge.Common.TreeWalk.Parent(d))
                if (d == _frame || d is ScrollBar) return;
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ShellDialogPeer(this);

        private sealed class ShellDialogPeer : FrameworkElementAutomationPeer
        {
            public ShellDialogPeer(ShellDialog owner) : base(owner) { }

            protected override string GetClassNameCore() => nameof(ShellDialog);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        }
    }
}
