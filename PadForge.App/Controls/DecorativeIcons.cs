using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;

namespace PadForge.Controls
{
    /// <summary>A wpf-ui FontIcon that only decorates what sits beside it:
    /// the icon of a navigation entry, a symbol inside a named button. It
    /// draws exactly as a FontIcon does. FontIcon draws its glyph with a
    /// TextBlock it builds in code (FontIcon.InitializeChildren), outside any
    /// template, so TextBlockAutomationPeer exposed the glyph's private-use
    /// character to screen readers. This icon's peer is neither a control nor
    /// a content element and lists no children, so the glyph leaves the
    /// control view with it.</summary>
    public sealed class DecorativeFontIcon : Wpf.Ui.Controls.FontIcon
    {
        protected override void OnInitialized(EventArgs e)
        {
            // The FontIcon style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Wpf.Ui.Controls.FontIcon));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeIconPeer(this);
    }

    /// <summary>A wpf-ui SymbolIcon that only decorates, hidden from screen
    /// readers as DecorativeFontIcon is.</summary>
    public sealed class DecorativeSymbolIcon : Wpf.Ui.Controls.SymbolIcon
    {
        protected override void OnInitialized(EventArgs e)
        {
            // The SymbolIcon style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Wpf.Ui.Controls.SymbolIcon));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeIconPeer(this);
    }

    /// <summary>A line icon that only decorates, hidden from screen readers
    /// as DecorativeFontIcon is: one geometry on a 24 by 24 grid, drawn at
    /// <see cref="Size"/> with a round stroke in the icon's Foreground. The
    /// Foreground is inherited, so the sidebar's entry template colors the
    /// icon of the selected entry by setting it on the icon's presenter
    /// (Resources/Shell.xaml).</summary>
    public sealed class DecorativeStrokeIcon : Wpf.Ui.Controls.IconElement
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(System.Windows.Media.Geometry), typeof(DecorativeStrokeIcon),
            new PropertyMetadata(null, (d, _) => ((DecorativeStrokeIcon)d).Apply()));

        /// <summary>The drawing, on a 24 by 24 grid.</summary>
        public System.Windows.Media.Geometry Data
        {
            get => (System.Windows.Media.Geometry)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(DecorativeStrokeIcon),
            new PropertyMetadata(18.0, (d, _) => ((DecorativeStrokeIcon)d).Apply()));

        /// <summary>The side of the square the drawing is scaled to.</summary>
        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
            nameof(StrokeWidth), typeof(double), typeof(DecorativeStrokeIcon),
            new PropertyMetadata(1.7, (d, _) => ((DecorativeStrokeIcon)d).Apply()));

        /// <summary>The line's width on the 24 by 24 grid.</summary>
        public double StrokeWidth
        {
            get => (double)GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        private System.Windows.Shapes.Path _path;
        private System.Windows.Controls.Viewbox _box;

        protected override UIElement InitializeChildren()
        {
            _path = new System.Windows.Shapes.Path
            {
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            };
            var grid = new System.Windows.Controls.Canvas { Width = 24, Height = 24 };
            grid.Children.Add(_path);
            // A drawing keeps its left and right in a right-to-left language.
            _box = new System.Windows.Controls.Viewbox { Child = grid, FlowDirection = FlowDirection.LeftToRight };
            Apply();
            return _box;
        }

        protected override void OnForegroundChanged(DependencyPropertyChangedEventArgs args) => Apply();

        private void Apply()
        {
            if (_path == null) return;
            _path.Data = Data;
            _path.Stroke = Foreground;
            _path.StrokeThickness = StrokeWidth;
            _box.Width = Size;
            _box.Height = Size;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeIconPeer(this);
    }

    /// <summary>A peer outside the control and content views, with no
    /// children, so nothing the icon draws reaches a screen reader.</summary>
    internal sealed class DecorativeIconPeer : FrameworkElementAutomationPeer
    {
        public DecorativeIconPeer(FrameworkElement owner) : base(owner) { }

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override List<AutomationPeer> GetChildrenCore() => null;
    }
}
