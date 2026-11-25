using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// A square the user drags to set a position on two axes (#600 items 17
    /// and 19): a plugin's <c>pad</c> and the Test Controller's sticks under
    /// By Hand. X runs from -100 at the left to 100 at the right and Y from
    /// -100 at the top to 100 at the bottom, as a stick's raw axes do. A
    /// press sets the position under the pointer, a drag follows it, and a
    /// release takes it back to the center unless <see cref="Stay"/> is
    /// set. The arrow keys move it by 5, Home centers it. Drawn in the
    /// app's own brushes: a hairline frame and cross, and a dot that is
    /// ember while it is held off center.
    /// </summary>
    public sealed class PluginPad : FrameworkElement
    {
        public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
            nameof(Position), typeof(Point), typeof(PluginPad),
            new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StayProperty = DependencyProperty.Register(
            nameof(Stay), typeof(bool), typeof(PluginPad), new PropertyMetadata(false));

        private bool _dragging;

        public PluginPad()
        {
            Focusable = true;
            FocusVisualStyle = null;
            Width = 116;
            Height = 116;
            Cursor = Cursors.Cross;
        }

        /// <summary>The position, X from -100 at the left to 100 at the
        /// right, Y from -100 at the top to 100 at the bottom, set as one
        /// change.</summary>
        public Point Position
        {
            get => (Point)GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        public double X => Position.X;
        public double Y => Position.Y;

        /// <summary>Stays where the user lets go, instead of going back to
        /// the center.</summary>
        public bool Stay
        {
            get => (bool)GetValue(StayProperty);
            set => SetValue(StayProperty, value);
        }

        /// <summary>Sets both axes, each brought inside -100 to 100.</summary>
        private void Put(double x, double y)
        {
            SetCurrentValue(PositionProperty, new Point(Math.Round(Math.Clamp(x, -100, 100), 1), Math.Round(Math.Clamp(y, -100, 100), 1)));
        }

        private void PutAt(Point p)
        {
            double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
            Put(p.X / w * 200 - 100, p.Y / h * 200 - 100);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            _dragging = CaptureMouse();
            PutAt(e.GetPosition(this));
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) PutAt(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Release();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            Release();
        }

        private void Release()
        {
            if (!_dragging) return;
            _dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (!Stay) Put(0, 0);
            InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.Key)
            {
                case Key.Left: Put(X - 5, Y); break;
                case Key.Right: Put(X + 5, Y); break;
                case Key.Up: Put(X, Y - 5); break;
                case Key.Down: Put(X, Y + 5); break;
                case Key.Home: Put(0, 0); break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            InvalidateVisual();
        }

        private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 2 || h <= 2) return;
            var line = new Pen(Find("SteelLineSoftBrush", Brushes.Gray), 1);
            var frame = new Pen(IsKeyboardFocused ? Find("EmberBrush", Brushes.OrangeRed) : Find("CardStrokeColorDefaultBrush", Brushes.Gray), 1);
            dc.DrawRoundedRectangle(Find("CardBackgroundFillColorSecondaryBrush", Brushes.Transparent), frame, new Rect(0.5, 0.5, w - 1, h - 1), 6, 6);
            dc.DrawLine(line, new Point(Math.Round(w / 2) + 0.5, 1), new Point(Math.Round(w / 2) + 0.5, h - 1));
            dc.DrawLine(line, new Point(1, Math.Round(h / 2) + 0.5), new Point(w - 1, Math.Round(h / 2) + 0.5));
            double x = (X + 100) / 200 * w, y = (Y + 100) / 200 * h;
            bool off = _dragging || Math.Abs(X) > 0.05 || Math.Abs(Y) > 0.05;
            dc.DrawEllipse(off ? Find("EmberBrush", Brushes.OrangeRed) : Find("TextFillColorSecondaryBrush", Brushes.Gray), null,
                new Point(Math.Clamp(x, 6, w - 6), Math.Clamp(y, 6, h - 6)), 6, 6);
        }

        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PluginPadPeer(this);

        /// <summary>Gives a screen reader the position as the pad's value.</summary>
        private sealed class PluginPadPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
        {
            public PluginPadPeer(PluginPad owner) : base(owner) { }

            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
                => System.Windows.Automation.Peers.AutomationControlType.Custom;

            protected override string GetItemStatusCore()
            {
                var pad = (PluginPad)Owner;
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0}, {1:0}", pad.X, pad.Y);
            }

            protected override bool IsKeyboardFocusableCore() => true;
        }
    }
}
