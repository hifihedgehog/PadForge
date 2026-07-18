using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine.Menus;

namespace PadForge.Views
{
    /// <summary>
    /// On-screen radial / touch menu (#9 B-17). A click-through,
    /// never-activated HUD window (ShiftLayerFlyout's exact ex-style set:
    /// WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT plus the
    /// WM_MOUSEACTIVATE refusal) that renders the engaged menu's cells and
    /// highlights the hovered one. Owned by InputService, which pulls
    /// InputManager.ActiveMenuOverlay on the ~30 Hz UI timer and calls
    /// <see cref="UpdateFromSnapshot"/>; geometry rebuilds only when the
    /// menu identity or shape changes, per-tick work is the hover restyle.
    /// Theme-aware through the same dark / light brush pairs the flyout
    /// uses, re-applied on every rebuild.
    /// </summary>
    public partial class MenuOverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // Base sizes at 100% scale, in DIPs.
        private const double RingOuterRadius = 170;
        private const double RingInnerRadius = 62;
        private const double GridCellWidth = 112;
        private const double GridCellHeight = 76;
        private const double GridCellGap = 6;

        /// <summary>Rendering sanity cap. Steam's own configurator tops
        /// out at 20 radial buttons / 16 grid cells; a hand-hacked config
        /// past that still hovers and commits (the math is unbounded) but
        /// the window refuses to build an unbounded visual.</summary>
        private const int MaxRenderCells = 64;

        private MenuDefinitionEntry _menu;
        private string _geometrySig;
        private int _hovered = int.MinValue;
        private readonly Dictionary<int, Shape> _cellShapes = new();
        private readonly Dictionary<int, TextBlock> _cellLabels = new();

        // Theme brushes, refreshed on rebuild.
        private Brush _cellFill, _cellStroke, _hoverFill, _labelBrush, _hoverLabelBrush, _emptyFill;

        public MenuOverlayWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            return IntPtr.Zero;
        }

        /// <summary>Renders the snapshot: null hides the overlay, a new /
        /// reshaped menu rebuilds the geometry, and a same-menu tick only
        /// restyles the hover highlight.</summary>
        public void UpdateFromSnapshot(Common.Input.InputManager.MenuOverlayState snap)
        {
            if (snap?.Menu == null)
            {
                if (Visibility == Visibility.Visible) Hide();
                _menu = null;
                _geometrySig = null;
                return;
            }

            var menu = snap.Menu;
            string sig = GeometrySig(menu);
            if (!ReferenceEquals(menu, _menu) || sig != _geometrySig)
            {
                _menu = menu;
                _geometrySig = sig;
                _hovered = int.MinValue;
                RefreshThemeBrushes();
                if (menu.Kind == MenuKind.Radial) BuildRadial(menu);
                else BuildGrid(menu);
                Opacity = Math.Clamp(menu.OpacityPercent, 5, 100) / 100.0;
                PositionOnWorkArea(menu);
            }

            if (Visibility != Visibility.Visible)
            {
                Show();
                PositionOnWorkArea(menu);
            }

            SetHovered(snap.HoveredIndex);
        }

        private static string GeometrySig(MenuDefinitionEntry m)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append((int)m.Kind).Append('|').Append(m.CellCount).Append('|')
              .Append(m.HasCenter).Append('|').Append(m.ShowLabels).Append('|')
              .Append(m.ScalePercent).Append('|').Append(m.PosXPercent).Append('|')
              .Append(m.PosYPercent).Append('|').Append(m.OpacityPercent).Append('|')
              .Append(m.Name);
            if (m.Items != null)
            {
                for (int i = 0; i < m.Items.Count; i++)
                {
                    var it = m.Items[i];
                    if (it != null)
                        sb.Append('|').Append(it.Index).Append('=').Append(it.Label)
                          .Append('~').Append(it.Icon);
                }
            }
            return sb.ToString();
        }

        private void RefreshThemeBrushes()
        {
            bool isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                == Wpf.Ui.Appearance.ApplicationTheme.Dark;

            // Hover uses the app accent when resolvable, ember orange
            // otherwise (EmberTheme pins the accent, so this resolves in
            // practice; the fallback keeps the overlay correct standalone).
            var accent = Application.Current?.TryFindResource("SystemAccentColorPrimaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xF2, 0x5C, 0x1F));

            if (isDark)
            {
                _cellFill = WithOpacity(Color.FromRgb(0x2D, 0x2E, 0x2E), 0.92);
                _emptyFill = WithOpacity(Color.FromRgb(0x2D, 0x2E, 0x2E), 0.45);
                _cellStroke = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x16));
                _labelBrush = Brushes.White;
            }
            else
            {
                _cellFill = WithOpacity(Color.FromRgb(0xEF, 0xEF, 0xEF), 0.94);
                _emptyFill = WithOpacity(Color.FromRgb(0xEF, 0xEF, 0xEF), 0.5);
                _cellStroke = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
                _labelBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            }
            _hoverFill = accent;
            _hoverLabelBrush = Brushes.White;
        }

        private static Brush WithOpacity(Color c, double opacity)
        {
            var b = new SolidColorBrush(c) { Opacity = opacity };
            b.Freeze();
            return b;
        }

        // ── Radial ──────────────────────────────────────────────

        private void BuildRadial(MenuDefinitionEntry menu)
        {
            _cellShapes.Clear();
            _cellLabels.Clear();
            MenuCanvas.Children.Clear();

            double scale = Math.Clamp(menu.ScalePercent, 10, 400) / 100.0;
            double outer = RingOuterRadius * scale;
            double inner = RingInnerRadius * scale;
            double size = outer * 2 + 8;
            MenuCanvas.Width = size;
            MenuCanvas.Height = size;
            double cx = size / 2, cy = size / 2;

            var bound = BoundItems(menu);
            int slots = Math.Clamp(menu.CellCount, 0, MaxRenderCells);

            for (int slot = 1; slot <= slots; slot++)
            {
                bool has = bound.TryGetValue(slot, out var item);
                var path = new Path
                {
                    Data = BuildWedgeGeometry(cx, cy, inner, outer, slot, slots),
                    Fill = has ? _cellFill : _emptyFill,
                    Stroke = _cellStroke,
                    StrokeThickness = 1.5,
                };
                MenuCanvas.Children.Add(path);
                _cellShapes[slot] = path;

                if (has)
                {
                    double mid = (slot - 1) * 2.0 * Math.PI / slots; // clockwise from top
                    double lr = (inner + outer) / 2.0;
                    PlaceCellContent(item, menu.ShowLabels, slot,
                        cx + Math.Sin(mid) * lr, cy - Math.Cos(mid) * lr,
                        outer * 0.62, 13 * Math.Max(scale, 0.7), scale);
                }
            }

            // Center cell (Steam's "Radial Menu Center Button",
            // touch_menu_button_0), selected while inside the deadzone.
            if (menu.HasCenter)
            {
                bool has = bound.TryGetValue(0, out var center);
                var dot = new Ellipse
                {
                    Width = inner * 2 - 10,
                    Height = inner * 2 - 10,
                    Fill = has ? _cellFill : _emptyFill,
                    Stroke = _cellStroke,
                    StrokeThickness = 1.5,
                };
                Canvas.SetLeft(dot, cx - (inner - 5));
                Canvas.SetTop(dot, cy - (inner - 5));
                MenuCanvas.Children.Add(dot);
                _cellShapes[0] = dot;

                if (has)
                {
                    PlaceCellContent(center, menu.ShowLabels, 0, cx, cy,
                        inner * 1.6, 13 * Math.Max(scale, 0.7), scale);
                }
            }
        }

        /// <summary>Annular sector for ring slot <paramref name="slot"/>
        /// (1-based) of <paramref name="slots"/>: wedge centered at
        /// (slot-1) * 360/N degrees clockwise from the top, the shipped
        /// radial-zone convention. A single slot renders as the full
        /// donut.</summary>
        private static Geometry BuildWedgeGeometry(double cx, double cy,
            double inner, double outer, int slot, int slots)
        {
            if (slots <= 1)
            {
                var full = new GeometryGroup { FillRule = FillRule.EvenOdd };
                full.Children.Add(new EllipseGeometry(new Point(cx, cy), outer, outer));
                full.Children.Add(new EllipseGeometry(new Point(cx, cy), inner, inner));
                return full;
            }

            double span = 2.0 * Math.PI / slots;
            double a0 = (slot - 1) * span - span / 2.0;
            double a1 = a0 + span;
            const double gap = 0.012; // radians of visual separation
            a0 += gap;
            a1 -= gap;

            Point P(double a, double r) => new(cx + Math.Sin(a) * r, cy - Math.Cos(a) * r);

            var fig = new PathFigure { StartPoint = P(a0, outer), IsClosed = true };
            fig.Segments.Add(new ArcSegment(P(a1, outer), new Size(outer, outer), 0,
                isLargeArc: span > Math.PI, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(a1, inner), true));
            fig.Segments.Add(new ArcSegment(P(a0, inner), new Size(inner, inner), 0,
                isLargeArc: span > Math.PI, SweepDirection.Counterclockwise, true));

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        // ── Grid ────────────────────────────────────────────────

        private void BuildGrid(MenuDefinitionEntry menu)
        {
            _cellShapes.Clear();
            _cellLabels.Clear();
            MenuCanvas.Children.Clear();

            double scale = Math.Clamp(menu.ScalePercent, 10, 400) / 100.0;
            double cw = GridCellWidth * scale;
            double ch = GridCellHeight * scale;
            double gap = GridCellGap * scale;

            int cellCount = Math.Clamp(menu.CellCount, 0, MaxRenderCells);
            var (cols, rows) = MenuSelectionMath.GridShape(cellCount);
            if (cols <= 0) { MenuCanvas.Width = 0; MenuCanvas.Height = 0; return; }
            MenuCanvas.Width = cols * cw + (cols - 1) * gap;
            MenuCanvas.Height = rows * ch + (rows - 1) * gap;

            var bound = BoundItems(menu);
            for (int idx = 0; idx < cellCount; idx++)
            {
                int col = idx % cols, row = idx / cols;
                bool has = bound.TryGetValue(idx, out var item);
                var rect = new Rectangle
                {
                    Width = cw,
                    Height = ch,
                    RadiusX = 8,
                    RadiusY = 8,
                    Fill = has ? _cellFill : _emptyFill,
                    Stroke = _cellStroke,
                    StrokeThickness = 1.5,
                };
                Canvas.SetLeft(rect, col * (cw + gap));
                Canvas.SetTop(rect, row * (ch + gap));
                MenuCanvas.Children.Add(rect);
                _cellShapes[idx] = rect;

                if (has)
                {
                    PlaceCellContent(item, menu.ShowLabels, idx,
                        col * (cw + gap) + cw / 2, row * (ch + gap) + ch / 2,
                        cw - 12, 13 * Math.Max(scale, 0.7), scale);
                }
            }
        }

        // ── Shared bits ─────────────────────────────────────────

        private static Dictionary<int, MenuItemDefinition> BoundItems(MenuDefinitionEntry menu)
        {
            var map = new Dictionary<int, MenuItemDefinition>();
            if (menu.Items == null) return map;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                var it = menu.Items[i];
                if (it != null) map[it.Index] = it;
            }
            return map;
        }

        /// <summary>Renders one bound cell's content centered on (cx, cy):
        /// the Steam icon glyph when the item carries a name the local
        /// Steam install resolves (translator v21, never shipped, read at
        /// display time), the text label per ShowLabels, both stacked when
        /// both exist. An icon that fails to resolve (no Steam, file
        /// absent, bad name) degrades to exactly the pre-icon rendering:
        /// the label alone. Icons never restyle on hover, so only labels
        /// register in <see cref="_cellLabels"/>.</summary>
        private void PlaceCellContent(MenuItemDefinition item, bool showLabels, int index,
            double cx, double cy, double maxLabelWidth, double fontSize, double scale)
        {
            ImageSource iconSrc = string.IsNullOrEmpty(item.Icon)
                ? null : Common.MenuIconResolver.Resolve(item.Icon);
            bool labelShown = showLabels && !string.IsNullOrEmpty(item.Label);
            double iconSize = 30 * Math.Max(scale, 0.7);

            if (iconSrc != null)
            {
                var icon = new Image
                {
                    Source = iconSrc,
                    Width = iconSize,
                    Height = iconSize,
                    IsHitTestVisible = false,
                };
                double iconCy = labelShown ? cy - iconSize * 0.45 : cy;
                Canvas.SetLeft(icon, cx - iconSize / 2);
                Canvas.SetTop(icon, iconCy - iconSize / 2);
                MenuCanvas.Children.Add(icon);
            }

            if (labelShown)
            {
                var label = MakeLabel(item.Label, maxLabelWidth, fontSize);
                PlaceLabel(label, cx, iconSrc != null ? cy + iconSize * 0.55 : cy);
                _cellLabels[index] = label;
            }
        }

        private TextBlock MakeLabel(string text, double maxWidth, double fontSize)
            => new()
            {
                Text = text,
                Foreground = _labelBrush,
                FontSize = fontSize,
                FontFamily = new FontFamily("Segoe UI"),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(maxWidth, 24),
                MaxHeight = fontSize * 2.9,
                IsHitTestVisible = false,
            };

        private void PlaceLabel(TextBlock label, double cx, double cy)
        {
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, cx - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, cy - label.DesiredSize.Height / 2);
            MenuCanvas.Children.Add(label);
        }

        private void SetHovered(int hovered)
        {
            if (hovered == _hovered) return;

            if (_cellShapes.TryGetValue(_hovered, out var prev))
            {
                bool prevBound = _cellLabels.ContainsKey(_hovered)
                    || (_menu?.Items != null && _menu.Items.Exists(i => i != null && i.Index == _hovered));
                prev.Fill = prevBound ? _cellFill : _emptyFill;
            }
            if (_cellLabels.TryGetValue(_hovered, out var prevLabel))
                prevLabel.Foreground = _labelBrush;

            _hovered = hovered;

            if (_cellShapes.TryGetValue(hovered, out var cur))
                cur.Fill = _hoverFill;
            if (_cellLabels.TryGetValue(hovered, out var curLabel))
                curLabel.Foreground = _hoverLabelBrush;
        }

        /// <summary>Centers the overlay at the menu's configured work-area
        /// position (Steam's touch_menu_position_x/_y percents; 50/50 =
        /// centered), clamped fully on screen.</summary>
        private void PositionOnWorkArea(MenuDefinitionEntry menu)
        {
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            double w = MenuCanvas.Width, h = MenuCanvas.Height;
            double cx = wa.Left + wa.Width * Math.Clamp(menu.PosXPercent, 0, 100) / 100.0;
            double cy = wa.Top + wa.Height * Math.Clamp(menu.PosYPercent, 0, 100) / 100.0;
            Left = Math.Clamp(cx - w / 2, wa.Left, Math.Max(wa.Left, wa.Right - w));
            Top = Math.Clamp(cy - h / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        }
    }
}
