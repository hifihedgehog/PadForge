using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Read-only mouse graphic for the Devices page detail pane.
    /// Shows LMB, RMB, MMB, scroll wheel with intensity arrows,
    /// movement circle with deflection dot, and side buttons.
    /// </summary>
    public partial class MousePreviewControl : UserControl
    {
        private Shape _lmbPath, _rmbPath;
        private Shape _scrollWheelPill;
        private Polygon _scrollUpArrow, _scrollDownArrow;
        private Ellipse _movementDot;

        private static bool IsDarkTheme =>
            Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        private static SolidColorBrush F(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        private static readonly Brush _dimD = F(0x40,0x40,0x40), _dimL = F(0xB0,0xB0,0xB0);
        private static readonly Brush _btnD = F(0x60,0x60,0x60), _btnL = F(0xD0,0xD0,0xD0);
        private static readonly Brush _swD = F(0x38,0x38,0x38), _swL = F(0xA8,0xA8,0xA8);
        private static readonly Brush _dotD = F(0x88,0x88,0x88), _dotL = F(0x70,0x70,0x70);

        private static Brush DimBrush => IsDarkTheme ? _dimD : _dimL;
        private static Brush MouseButtonBrush => IsDarkTheme ? _btnD : _btnL;
        private static Brush ScrollWheelBrush => IsDarkTheme ? _swD : _swL;
        // Cold (#175): the Devices page is the INPUT preview surface, and
        // every other device preview there lights pressed elements in the
        // cold blue (#58B6E4) with the ColdDotGlow treatment. Ember belongs
        // to the OUTPUT previews (the KBM virtual-controller view keeps it).
        private static readonly Brush AccentBrush = F(0x58,0xB6,0xE4);
        private static Brush DotBrush => IsDarkTheme ? _dotD : _dotL;

        // Ember bloom (#175 glow sweep): pressed visuals carry a static
        // DropShadowEffect, attached alongside the brush swap and detached
        // when unlit. Frozen and shared, never animated. Small variant for
        // glyphs 14px and under (movement dot, scroll arrows, side buttons).
        private static readonly System.Windows.Media.Effects.DropShadowEffect EmberGlowSmall = MakeEmberGlow(8);

        private static System.Windows.Media.Effects.DropShadowEffect MakeEmberGlow(double blur)
        {
            var fx = new System.Windows.Media.Effects.DropShadowEffect
            {
                // Matches DevicesPage's ColdDotGlow (#58B6E4 @ 0.45).
                Color = Color.FromRgb(0x58, 0xB6, 0xE4),
                BlurRadius = blur,
                ShadowDepth = 0,
                Opacity = 0.45
            };
            fx.Freeze();
            return fx;
        }

        private static void SetGlow(UIElement element, System.Windows.Media.Effects.DropShadowEffect glow)
        {
            if (!ReferenceEquals(element.Effect, glow))
                element.Effect = glow;
        }

        private const double MC = MouseGlyph.CenterX;
        private const double MoveSize = MouseGlyph.MoveSize;
        private const double MoveTop = MouseGlyph.MoveTop;

        private Shape _x1Rect, _x2Rect;
        private bool _built;

        private Wpf.Ui.Appearance.ApplicationTheme? _lastTheme;

        public MousePreviewControl()
        {
            InitializeComponent();
            // Rendering rides tree presence: a ctor-lifetime subscription
            // kept the per-frame callback running for the whole app even
            // while the hosting page was swapped out. The -= before +=
            // guards repeated Loaded without an intervening Unloaded.
            Loaded += (s, e) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                BuildMouse();
            };
            Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        private void BuildMouse()
        {
            var currentTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            if (_built && _lastTheme == currentTheme) return;
            _built = true;
            _lastTheme = currentTheme;
            MouseCanvas.Children.Clear();

            var parts = MouseGlyph.Build(MouseCanvas, IsDarkTheme, DimBrush,
                MouseButtonBrush, ScrollWheelBrush, DotBrush);
            _lmbPath = parts.Lmb; _rmbPath = parts.Rmb;
            _x1Rect = parts.X1;   _x2Rect = parts.X2;
            _scrollWheelPill = parts.Wheel;
            _scrollUpArrow = parts.ScrollUp; _scrollDownArrow = parts.ScrollDown;
            _movementDot = parts.MoveDot;
            MouseGlyph.AddOutline(MouseCanvas, DimBrush);

            MouseCanvas.Height = MouseGlyph.BodyH + 6;
        }

        // Retained scroll-arrow transforms (a fresh ScaleTransform per
        // rendered frame while scrolling was Freezable churn).
        private ScaleTransform _scrollUpScale, _scrollDownScale;

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_built) return;
            // Pages are retained and visibility-toggled, not unloaded, so this
            // per-frame handler keeps firing after navigating away. Skip the
            // widget rewrite while hidden (same guard as MidiPreviewView's
            // input path); the next visible frame repaints from live state.
            // Iconic gate: IsVisible stays TRUE while minimized.
            if (!IsVisible || PadForge.Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
            // Rebuild on theme change.
            var currentTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            if (_lastTheme != currentTheme) BuildMouse();
            var vm = DataContext as DevicesViewModel;
            if (vm == null || !vm.IsMouseDevice) return;

            // Buttons: LMB=0, MMB=1, RMB=2, X1=3, X2=4
            bool lmb = vm.RawButtons.Count > 0 && vm.RawButtons[0].IsPressed;
            bool mmb = vm.RawButtons.Count > 1 && vm.RawButtons[1].IsPressed;
            bool rmb = vm.RawButtons.Count > 2 && vm.RawButtons[2].IsPressed;
            bool x1 = vm.RawButtons.Count > 3 && vm.RawButtons[3].IsPressed;
            bool x2 = vm.RawButtons.Count > 4 && vm.RawButtons[4].IsPressed;

            _lmbPath.Fill = lmb ? AccentBrush : MouseButtonBrush;
            _rmbPath.Fill = rmb ? AccentBrush : MouseButtonBrush;
            _scrollWheelPill.Fill = mmb ? AccentBrush : ScrollWheelBrush;
            _x1Rect.Fill = x1 ? AccentBrush : MouseButtonBrush;
            _x2Rect.Fill = x2 ? AccentBrush : MouseButtonBrush;

            // Movement dot
            double moveX = MC - MoveSize / 2;
            double centerX = moveX + MoveSize / 2 - 5;
            double centerY = MoveTop + MoveSize / 2 - 5;
            double maxDeflect = MoveSize / 2 - 8;

            double mx = vm.MouseMotionX;
            double my = vm.MouseMotionY;
            Canvas.SetLeft(_movementDot, centerX + mx * maxDeflect);
            Canvas.SetTop(_movementDot, centerY - my * maxDeflect);
            bool moving = Math.Abs(mx) > 0.01 || Math.Abs(my) > 0.01;
            _movementDot.Fill = moving ? AccentBrush : DotBrush;
            SetGlow(_movementDot, moving ? EmberGlowSmall : null);

            // Scroll arrows — intensity varies with scroll magnitude
            double scroll = vm.MouseScrollIntensity;
            double absScroll = Math.Min(Math.Abs(scroll), 1.0);
            if (scroll > 0.01)
            {
                _scrollUpArrow.Fill = AccentBrush;
                _scrollUpArrow.Opacity = 0.3 + 0.7 * absScroll;
                // Anchor on the arrow itself. These were literals from the
                // hand-drawn glyph; the vendored art moved the arrows, so a
                // pulse slid them down the wheel instead of growing in place.
                _scrollUpScale ??= new ScaleTransform(1, 1, MC, MouseGlyph.WheelTop + 9);
                _scrollUpScale.ScaleX = _scrollUpScale.ScaleY = 1.0 + 0.4 * absScroll;
                _scrollUpArrow.RenderTransform = _scrollUpScale;
                SetGlow(_scrollUpArrow, EmberGlowSmall);
                _scrollDownArrow.Fill = DimBrush;
                _scrollDownArrow.Opacity = 1.0;
                _scrollDownArrow.RenderTransform = null;
                SetGlow(_scrollDownArrow, null);
            }
            else if (scroll < -0.01)
            {
                _scrollDownArrow.Fill = AccentBrush;
                _scrollDownArrow.Opacity = 0.3 + 0.7 * absScroll;
                _scrollDownScale ??= new ScaleTransform(1, 1, MC, MouseGlyph.WheelBottom - 9);
                _scrollDownScale.ScaleX = _scrollDownScale.ScaleY = 1.0 + 0.4 * absScroll;
                _scrollDownArrow.RenderTransform = _scrollDownScale;
                SetGlow(_scrollDownArrow, EmberGlowSmall);
                _scrollUpArrow.Fill = DimBrush;
                _scrollUpArrow.Opacity = 1.0;
                _scrollUpArrow.RenderTransform = null;
                SetGlow(_scrollUpArrow, null);
            }
            else
            {
                _scrollUpArrow.Fill = DimBrush;
                _scrollUpArrow.Opacity = 1.0;
                _scrollUpArrow.RenderTransform = null;
                SetGlow(_scrollUpArrow, null);
                _scrollDownArrow.Fill = DimBrush;
                _scrollDownArrow.Opacity = 1.0;
                _scrollDownArrow.RenderTransform = null;
                SetGlow(_scrollDownArrow, null);
            }
        }

        private const double swBotConst = MouseGlyph.WheelBottom;
    }
}
