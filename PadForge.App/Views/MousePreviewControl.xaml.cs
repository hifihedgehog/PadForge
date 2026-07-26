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
        private Path _lmbPath, _rmbPath;
        private Rectangle _scrollWheelPill;
        private Polygon _scrollUpArrow, _scrollDownArrow;
        private Ellipse _movementDot, _moveCircle;

        private static bool IsDarkTheme =>
            Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        private static SolidColorBrush F(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        private static readonly Brush _dimD = F(0x40,0x40,0x40), _dimL = F(0xB0,0xB0,0xB0);
        private static readonly Brush _bodyD = F(0x50,0x50,0x50), _bodyL = F(0xC0,0xC0,0xC0);
        private static readonly Brush _btnD = F(0x60,0x60,0x60), _btnL = F(0xD0,0xD0,0xD0);
        private static readonly Brush _mmbD = F(0x55,0x55,0x55), _mmbL = F(0xC8,0xC8,0xC8);
        private static readonly Brush _swD = F(0x38,0x38,0x38), _swL = F(0xA8,0xA8,0xA8);
        private static readonly Brush _dotD = F(0x88,0x88,0x88), _dotL = F(0x70,0x70,0x70);

        private static Brush DimBrush => IsDarkTheme ? _dimD : _dimL;
        private static Brush MouseBodyBrush => IsDarkTheme ? _bodyD : _bodyL;
        private static Brush MouseButtonBrush => IsDarkTheme ? _btnD : _btnL;
        private static Brush MmbBrush => IsDarkTheme ? _mmbD : _mmbL;
        private static Brush ScrollWheelBrush => IsDarkTheme ? _swD : _swL;
        // Ember (#175): output preview surface.
        private static readonly Brush AccentBrush = F(0xFF,0x6B,0x2C);
        private static Brush DotBrush => IsDarkTheme ? _dotD : _dotL;

        // Ember bloom (#175 glow sweep): pressed visuals carry a static
        // DropShadowEffect, attached alongside the brush swap and detached
        // when unlit. Frozen and shared, never animated. Small variant for
        // glyphs 14px and under (movement dot, scroll arrows, side buttons).
        private static readonly System.Windows.Media.Effects.DropShadowEffect EmberGlow = MakeEmberGlow(12);
        private static readonly System.Windows.Media.Effects.DropShadowEffect EmberGlowSmall = MakeEmberGlow(8);

        private static System.Windows.Media.Effects.DropShadowEffect MakeEmberGlow(double blur)
        {
            var fx = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x6B, 0x2C),
                BlurRadius = blur,
                ShadowDepth = 0,
                Opacity = 0.5
            };
            fx.Freeze();
            return fx;
        }

        private static void SetGlow(UIElement element, System.Windows.Media.Effects.DropShadowEffect glow)
        {
            if (!ReferenceEquals(element.Effect, glow))
                element.Effect = glow;
        }

        private const double MC = 80;
        private const double MoveSize = 56;
        private const double MoveTop = 90;

        // Body silhouette, top-down: narrow across the front, widest over
        // the palm, tapering to a rounded base. The old shape was a rounded
        // rectangle, so the flanks ran dead straight and the buttons had to
        // trace those straight edges.
        private const string BodyPath =
            "M 80,0 C 102,0 126,20 130,58 C 135,96 134,142 122,170" +
            " C 112,184 96,188 80,188 C 64,188 48,184 38,170" +
            " C 26,142 25,96 30,58 C 34,20 58,0 80,0 Z";

        // The button/palm seam is a shallow curve, not a straight cut.
        private const string ButtonRegionPath =
            "M 0,0 L 160,0 L 160,66 C 116,78 44,78 0,66 Z";

        private Path _x1Rect, _x2Rect;
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

            const double swW = 15, swH = 38;
            double swL = MC - swW / 2, swR = MC + swW / 2;
            const double swTop = 12;
            const double swBot = swTop + swH;
            double gapL = swL - 1.5, gapR = swR + 1.5;
            const double mH = 188;

            var body = Geometry.Parse(BodyPath);
            body.Freeze();
            var buttonRegion = Geometry.Parse(ButtonRegionPath);
            buttonRegion.Freeze();

            // Body, with a shallow vertical gradient so the shell reads as a
            // curved shell rather than a flat cut-out. Built here, not
            // per-frame: BuildMouse only re-runs on a theme change.
            var shell = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            byte hi = IsDarkTheme ? (byte)0x5A : (byte)0xCB;
            byte lo = IsDarkTheme ? (byte)0x44 : (byte)0xB2;
            shell.GradientStops.Add(new GradientStop(Color.FromRgb(hi, hi, hi), 0));
            shell.GradientStops.Add(new GradientStop(Color.FromRgb(lo, lo, lo), 1));
            shell.Freeze();
            MouseCanvas.Children.Add(new Path { Data = body, Fill = shell });

            // Wheel channel: a recess between the two buttons.
            MouseCanvas.Children.Add(new Path
            {
                Data = new RectangleGeometry(new Rect(gapL, 0, gapR - gapL, 58), 3, 3),
                Clip = body,
                Fill = MmbBrush,
                IsHitTestVisible = false,
            });

            // LMB / RMB. Each is the button region cut to its own side of
            // the channel, then CLIPPED TO THE BODY, so the outer edge is
            // the shell's own curve instead of an approximation of it.
            Path Half(double x0, double x1) => new()
            {
                Data = new CombinedGeometry(GeometryCombineMode.Intersect, buttonRegion,
                           new RectangleGeometry(new Rect(x0, 0, x1 - x0, 200))),
                Clip = body,
                Fill = MouseButtonBrush,
                Stroke = DimBrush,
                StrokeThickness = 1,
            };
            _lmbPath = Half(0, gapL);
            _rmbPath = Half(gapR, 160);
            MouseCanvas.Children.Add(_lmbPath);
            MouseCanvas.Children.Add(_rmbPath);

            // Scroll wheel, with tread ridges.
            _scrollWheelPill = new Rectangle
            {
                Width = swW, Height = swH,
                RadiusX = swW / 2, RadiusY = swW / 2,
                Fill = ScrollWheelBrush, Stroke = DimBrush, StrokeThickness = 1
            };
            Canvas.SetLeft(_scrollWheelPill, swL);
            Canvas.SetTop(_scrollWheelPill, swTop);
            MouseCanvas.Children.Add(_scrollWheelPill);
            for (int k = 1; k < 5; k++)
            {
                double ty = swTop + k * swH / 5.0;
                MouseCanvas.Children.Add(new Line
                {
                    X1 = swL + 3, Y1 = ty, X2 = swR - 3, Y2 = ty,
                    Stroke = DimBrush, StrokeThickness = 0.6, Opacity = 0.7,
                    IsHitTestVisible = false,
                });
            }

            _scrollUpArrow = new Polygon
            {
                Points = new PointCollection { new Point(MC, swTop + 4), new Point(MC - 4, swTop + 10), new Point(MC + 4, swTop + 10) },
                Fill = DimBrush
            };
            MouseCanvas.Children.Add(_scrollUpArrow);
            _scrollDownArrow = new Polygon
            {
                Points = new PointCollection { new Point(MC, swBot - 4), new Point(MC - 4, swBot - 10), new Point(MC + 4, swBot - 10) },
                Fill = DimBrush
            };
            MouseCanvas.Children.Add(_scrollDownArrow);

            // Side buttons, sitting ON the left flank and cut to it, rather
            // than floating rectangles pinned outside the old straight edge.
            Path Side(double top) => new()
            {
                Data = new RectangleGeometry(new Rect(26, top, 11, 18), 3, 3),
                Clip = body,
                Fill = MouseButtonBrush,
                Stroke = DimBrush,
                StrokeThickness = 1,
            };
            _x1Rect = Side(72);
            _x2Rect = Side(94);
            MouseCanvas.Children.Add(_x1Rect);
            MouseCanvas.Children.Add(_x2Rect);

            // Movement ring with quadrant ticks, so deflection reads against
            // a reference instead of a bare circle.
            double moveX = MC - MoveSize / 2;
            _moveCircle = new Ellipse
            {
                Width = MoveSize, Height = MoveSize,
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x88, 0x88, 0x88)),
                Stroke = DimBrush, StrokeThickness = 1.4
            };
            Canvas.SetLeft(_moveCircle, moveX);
            Canvas.SetTop(_moveCircle, MoveTop);
            MouseCanvas.Children.Add(_moveCircle);

            double ringC = MoveTop + MoveSize / 2, ringR = MoveSize / 2;
            foreach (var (dx, dy) in new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
            {
                MouseCanvas.Children.Add(new Line
                {
                    X1 = MC + dx * ringR * 0.80, Y1 = ringC + dy * ringR * 0.80,
                    X2 = MC + dx * ringR * 0.98, Y2 = ringC + dy * ringR * 0.98,
                    Stroke = DimBrush, StrokeThickness = 1.2, IsHitTestVisible = false,
                });
            }

            _movementDot = new Ellipse { Width = 10, Height = 10, Fill = DotBrush, IsHitTestVisible = false };
            Canvas.SetLeft(_movementDot, moveX + MoveSize / 2 - 5);
            Canvas.SetTop(_movementDot, MoveTop + MoveSize / 2 - 5);
            MouseCanvas.Children.Add(_movementDot);

            // Shell outline last, so it reads as one continuous edge over
            // the buttons and the flank keys rather than being interrupted.
            MouseCanvas.Children.Add(new Path
            {
                Data = body, Fill = null, Stroke = DimBrush, StrokeThickness = 2,
                IsHitTestVisible = false,
            });

            MouseCanvas.Height = mH + 6;
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
            SetGlow(_lmbPath, lmb ? EmberGlow : null);
            _rmbPath.Fill = rmb ? AccentBrush : MouseButtonBrush;
            SetGlow(_rmbPath, rmb ? EmberGlow : null);
            _scrollWheelPill.Fill = mmb ? AccentBrush : ScrollWheelBrush;
            SetGlow(_scrollWheelPill, mmb ? EmberGlow : null);
            _x1Rect.Fill = x1 ? AccentBrush : MouseButtonBrush;
            SetGlow(_x1Rect, x1 ? EmberGlowSmall : null);
            _x2Rect.Fill = x2 ? AccentBrush : MouseButtonBrush;
            SetGlow(_x2Rect, x2 ? EmberGlowSmall : null);

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
                _scrollUpScale ??= new ScaleTransform(1, 1, MC, 7);
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
                _scrollDownScale ??= new ScaleTransform(1, 1, MC, swBotConst - 7);
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

        private const double swBotConst = 12 + 38; // swTop + swH
    }
}
