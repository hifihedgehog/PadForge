using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Programmatic schematic view for custom vJoy controllers.
    /// Displays stick position circles, trigger bars, POV compasses, and button grids.
    /// </summary>
    public partial class ControllerSchematicView : UserControl
    {
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _vm;
        private bool _dirty;
        private bool _layoutBuilt;

        // Accent color for pressed/active elements
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
        private static readonly Brush DotBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

        // Widget tracking
        private readonly List<StickWidget> _stickWidgets = new();
        private readonly List<TriggerWidget> _triggerWidgets = new();
        private readonly List<PovWidget> _povWidgets = new();
        private readonly List<ButtonWidget> _buttonWidgets = new();

        // Layout constants
        private const double StickSize = 100;
        private const double TriggerWidth = 24;
        private const double TriggerHeight = 80;
        private const double PovSize = 60;
        private const double ButtonSize = 22;
        private const double SectionGap = 24;
        private const double LabelHeight = 18;
        private const double Padding = 12;
        private const int ButtonsPerRow = 8;

        public ControllerSchematicView()
        {
            InitializeComponent();
            CompositionTarget.Rendering += OnRendering;
        }

        // ─────────────────────────────────────────────
        //  ViewModel binding (same interface as 2D/3D views)
        // ─────────────────────────────────────────────

        public void Bind(PadViewModel vm)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.VJoyConfig.PropertyChanged -= OnVJoyConfigPropertyChanged;
            }

            _vm = vm;

            if (_vm != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                _vm.PropertyChanged += OnVmPropertyChanged;
                _vm.VJoyConfig.PropertyChanged += OnVJoyConfigPropertyChanged;
                RebuildLayout();
            }
        }

        public void Unbind()
        {
            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.VJoyConfig.PropertyChanged -= OnVJoyConfigPropertyChanged;
            }
            _vm = null;
            _layoutBuilt = false;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.VJoyOutputSnapshot))
            {
                _dirty = true;
                return;
            }

            if (e.PropertyName == nameof(PadViewModel.OutputType))
            {
                Dispatcher.Invoke(RebuildLayout);
                return;
            }
        }

        private void OnVJoyConfigPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Rebuild layout when config counts change
            Dispatcher.Invoke(RebuildLayout);
        }

        // ─────────────────────────────────────────────
        //  Layout construction
        // ─────────────────────────────────────────────

        private void RebuildLayout()
        {
            SchematicCanvas.Children.Clear();
            _stickWidgets.Clear();
            _triggerWidgets.Clear();
            _povWidgets.Clear();
            _buttonWidgets.Clear();

            if (_vm == null) return;
            var cfg = _vm.VJoyConfig;
            if (cfg == null || cfg.IsGamepadPreset) return;

            cfg.ComputeAxisLayout(out var stickAxisX, out var stickAxisY, out var triggerAxis);

            double x = Padding;
            double topY = Padding + LabelHeight;

            // ── Sticks ──
            for (int i = 0; i < cfg.ThumbstickCount; i++)
            {
                var w = CreateStickWidget(i, stickAxisX[i], stickAxisY[i], x, topY);
                _stickWidgets.Add(w);
                x += StickSize + SectionGap;
            }

            // ── Triggers ──
            for (int i = 0; i < cfg.TriggerCount; i++)
            {
                var w = CreateTriggerWidget(i, triggerAxis[i], x, topY);
                _triggerWidgets.Add(w);
                x += TriggerWidth + SectionGap;
            }

            // ── POVs ──
            for (int i = 0; i < cfg.PovCount; i++)
            {
                var w = CreatePovWidget(i, x, topY);
                _povWidgets.Add(w);
                x += PovSize + SectionGap;
            }

            // ── Buttons ── (wrap to rows)
            double btnStartX = Padding;
            double btnStartY = topY + Math.Max(StickSize, TriggerHeight) + SectionGap + LabelHeight;

            // Section label
            var btnSectionLabel = CreateLabel("Buttons", btnStartX, btnStartY - LabelHeight - 2);
            SchematicCanvas.Children.Add(btnSectionLabel);

            for (int i = 0; i < cfg.ButtonCount; i++)
            {
                int col = i % ButtonsPerRow;
                int row = i / ButtonsPerRow;
                double bx = btnStartX + col * (ButtonSize + 6);
                double by = btnStartY + row * (ButtonSize + 6);
                var w = CreateButtonWidget(i, bx, by);
                _buttonWidgets.Add(w);
            }

            // Set canvas size for Viewbox scaling
            double totalWidth = Math.Max(x, btnStartX + ButtonsPerRow * (ButtonSize + 6)) + Padding;
            int btnRows = cfg.ButtonCount > 0 ? ((cfg.ButtonCount - 1) / ButtonsPerRow + 1) : 0;
            double totalHeight = btnStartY + btnRows * (ButtonSize + 6) + Padding;

            SchematicCanvas.Width = totalWidth;
            SchematicCanvas.Height = totalHeight;
            _layoutBuilt = true;
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Stick widget
        // ─────────────────────────────────────────────

        private StickWidget CreateStickWidget(int index, int axisXIdx, int axisYIdx, double x, double y)
        {
            // Outer circle (dead zone ring)
            var outer = new Ellipse
            {
                Width = StickSize,
                Height = StickSize,
                Stroke = DimBrush,
                StrokeThickness = 1.5,
                Fill = BgBrush,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(outer, x);
            Canvas.SetTop(outer, y);
            SchematicCanvas.Children.Add(outer);

            // Crosshair lines
            var hLine = new Line
            {
                X1 = x + 4, Y1 = y + StickSize / 2,
                X2 = x + StickSize - 4, Y2 = y + StickSize / 2,
                Stroke = DimBrush, StrokeThickness = 0.5, Opacity = 0.5
            };
            var vLine = new Line
            {
                X1 = x + StickSize / 2, Y1 = y + 4,
                X2 = x + StickSize / 2, Y2 = y + StickSize - 4,
                Stroke = DimBrush, StrokeThickness = 0.5, Opacity = 0.5
            };
            SchematicCanvas.Children.Add(hLine);
            SchematicCanvas.Children.Add(vLine);

            // Position dot
            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = AccentBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, x + StickSize / 2 - 5);
            Canvas.SetTop(dot, y + StickSize / 2 - 5);
            SchematicCanvas.Children.Add(dot);

            // Label
            var label = CreateLabel($"Stick {index + 1}", x, y - LabelHeight);
            SchematicCanvas.Children.Add(label);

            // Click-to-record: quadrant detection
            outer.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(outer);
                double cx = pos.X - StickSize / 2;
                double cy = pos.Y - StickSize / 2;
                string target;
                if (Math.Abs(cx) > Math.Abs(cy))
                    target = cx > 0 ? $"VJoyAxis{axisXIdx}" : $"VJoyAxis{axisXIdx}Neg";
                else
                    target = cy > 0 ? $"VJoyAxis{axisYIdx}Neg" : $"VJoyAxis{axisYIdx}";
                ControllerElementRecordRequested?.Invoke(this, target);
            };

            return new StickWidget
            {
                AxisXIndex = axisXIdx,
                AxisYIndex = axisYIdx,
                Dot = dot,
                X = x,
                Y = y
            };
        }

        // ─────────────────────────────────────────────
        //  Trigger widget
        // ─────────────────────────────────────────────

        private TriggerWidget CreateTriggerWidget(int index, int axisIdx, double x, double y)
        {
            // Background bar
            var bg = new Rectangle
            {
                Width = TriggerWidth,
                Height = TriggerHeight,
                Fill = BgBrush,
                Stroke = DimBrush,
                StrokeThickness = 1,
                RadiusX = 3, RadiusY = 3,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(bg, x);
            Canvas.SetTop(bg, y);
            SchematicCanvas.Children.Add(bg);

            // Fill bar (grows from bottom)
            var fill = new Rectangle
            {
                Width = TriggerWidth - 4,
                Height = 0,
                Fill = AccentBrush,
                RadiusX = 2, RadiusY = 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(fill, x + 2);
            Canvas.SetTop(fill, y + TriggerHeight - 2);
            SchematicCanvas.Children.Add(fill);

            // Label
            var label = CreateLabel($"T{index + 1}", x, y - LabelHeight);
            SchematicCanvas.Children.Add(label);

            // Click-to-record
            bg.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, $"VJoyAxis{axisIdx}");
            };

            return new TriggerWidget
            {
                AxisIndex = axisIdx,
                Fill = fill,
                X = x,
                Y = y
            };
        }

        // ─────────────────────────────────────────────
        //  POV widget
        // ─────────────────────────────────────────────

        private PovWidget CreatePovWidget(int index, double x, double y)
        {
            // Outer circle
            var outer = new Ellipse
            {
                Width = PovSize,
                Height = PovSize,
                Stroke = DimBrush,
                StrokeThickness = 1.5,
                Fill = BgBrush
            };
            Canvas.SetLeft(outer, x);
            Canvas.SetTop(outer, y);
            SchematicCanvas.Children.Add(outer);

            // Direction arrow (initially hidden)
            var arrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(0, -PovSize * 0.35),
                    new Point(-6, -PovSize * 0.15),
                    new Point(6, -PovSize * 0.15)
                },
                Fill = AccentBrush,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            Canvas.SetLeft(arrow, x + PovSize / 2);
            Canvas.SetTop(arrow, y + PovSize / 2);
            SchematicCanvas.Children.Add(arrow);

            // Label
            string povLabel = _vm.VJoyConfig.PovCount == 1 ? "D-Pad" : $"POV {index + 1}";
            var label = CreateLabel(povLabel, x, y - LabelHeight);
            SchematicCanvas.Children.Add(label);

            // Clickable direction quadrants (overlay 4 invisible rectangles)
            double qSize = PovSize / 2;
            CreatePovClickZone(x, y, qSize, "Up", index, 0, 0, qSize * 2, qSize);          // top half
            CreatePovClickZone(x, y + qSize, qSize, "Down", index, 0, 0, qSize * 2, qSize); // bottom half
            CreatePovClickZone(x, y, qSize, "Left", index, 0, 0, qSize, qSize * 2);          // left half
            CreatePovClickZone(x + qSize, y, qSize, "Right", index, 0, 0, qSize, qSize * 2); // right half

            return new PovWidget
            {
                PovIndex = index,
                Arrow = arrow,
                CenterX = x + PovSize / 2,
                CenterY = y + PovSize / 2
            };
        }

        private void CreatePovClickZone(double x, double y, double qSize, string dir, int povIdx,
            double offX, double offY, double width, double height)
        {
            var zone = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(zone, x + offX);
            Canvas.SetTop(zone, y + offY);
            zone.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, $"VJoyPov{povIdx}{dir}");
                e.Handled = true;
            };
            SchematicCanvas.Children.Add(zone);
        }

        // ─────────────────────────────────────────────
        //  Button widget
        // ─────────────────────────────────────────────

        private ButtonWidget CreateButtonWidget(int index, double x, double y)
        {
            var circle = new Ellipse
            {
                Width = ButtonSize,
                Height = ButtonSize,
                Stroke = DimBrush,
                StrokeThickness = 1.5,
                Fill = BgBrush,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(circle, x);
            Canvas.SetTop(circle, y);
            SchematicCanvas.Children.Add(circle);

            var text = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 9,
                Foreground = LabelBrush,
                IsHitTestVisible = false,
                TextAlignment = TextAlignment.Center
            };
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(text, x + (ButtonSize - text.DesiredSize.Width) / 2);
            Canvas.SetTop(text, y + (ButtonSize - text.DesiredSize.Height) / 2);
            SchematicCanvas.Children.Add(text);

            circle.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, $"VJoyBtn{index}");
            };

            return new ButtonWidget { ButtonIndex = index, Circle = circle };
        }

        // ─────────────────────────────────────────────
        //  Rendering (30Hz update)
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_dirty || _vm == null || !_layoutBuilt) return;
            _dirty = false;

            var raw = _vm.VJoyOutputSnapshot;

            // Update sticks
            foreach (var w in _stickWidgets)
            {
                double nx = 0.5, ny = 0.5;
                if (raw.Axes != null)
                {
                    if (w.AxisXIndex < raw.Axes.Length)
                        nx = (raw.Axes[w.AxisXIndex] - (double)short.MinValue) / 65535.0;
                    if (w.AxisYIndex < raw.Axes.Length)
                        ny = 1.0 - ((raw.Axes[w.AxisYIndex] - (double)short.MinValue) / 65535.0);
                }
                double dotX = w.X + nx * (StickSize - 10);
                double dotY = w.Y + ny * (StickSize - 10);
                Canvas.SetLeft(w.Dot, dotX);
                Canvas.SetTop(w.Dot, dotY);
            }

            // Update triggers
            foreach (var w in _triggerWidgets)
            {
                double value = 0;
                if (raw.Axes != null && w.AxisIndex < raw.Axes.Length)
                    value = (raw.Axes[w.AxisIndex] - (double)short.MinValue) / 65535.0;
                double fillH = Math.Clamp(value, 0, 1) * (TriggerHeight - 4);
                w.Fill.Height = fillH;
                Canvas.SetTop(w.Fill, w.Y + TriggerHeight - 2 - fillH);
            }

            // Update POVs
            foreach (var w in _povWidgets)
            {
                int povValue = -1;
                if (raw.Povs != null && w.PovIndex < raw.Povs.Length)
                    povValue = raw.Povs[w.PovIndex];

                if (povValue < 0 || povValue > 36000)
                {
                    w.Arrow.Visibility = Visibility.Collapsed;
                }
                else
                {
                    w.Arrow.Visibility = Visibility.Visible;
                    w.Arrow.RenderTransform = new RotateTransform(povValue / 100.0);
                }
            }

            // Update buttons
            foreach (var w in _buttonWidgets)
            {
                bool pressed = raw.IsButtonPressed(w.ButtonIndex);
                w.Circle.Fill = pressed ? AccentBrush : BgBrush;
            }
        }

        // ─────────────────────────────────────────────
        //  Helper
        // ─────────────────────────────────────────────

        private static TextBlock CreateLabel(string text, double x, double y)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = LabelBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }

        // ─────────────────────────────────────────────
        //  Widget structs
        // ─────────────────────────────────────────────

        private struct StickWidget
        {
            public int AxisXIndex, AxisYIndex;
            public Ellipse Dot;
            public double X, Y;
        }

        private struct TriggerWidget
        {
            public int AxisIndex;
            public Rectangle Fill;
            public double X, Y;
        }

        private struct PovWidget
        {
            public int PovIndex;
            public Polygon Arrow;
            public double CenterX, CenterY;
        }

        private struct ButtonWidget
        {
            public int ButtonIndex;
            public Ellipse Circle;
        }
    }
}
