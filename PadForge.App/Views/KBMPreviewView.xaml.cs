using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Keyboard + Mouse preview for the KeyboardMouse virtual controller type.
    /// Top half: QWERTY keyboard (reuses KeyboardKeyItem.BuildLayout) with bound keys highlighted.
    /// Bottom half: Simple mouse drawing showing LMB/RMB/movement state.
    /// </summary>
    public partial class KBMPreviewView : UserControl
    {
        private PadViewModel _vm;
        private bool _dirty;
        private bool _layoutBuilt;

        private ObservableCollection<KeyboardKeyItem> _keys;

        // Mouse visualization elements
        private Path _mouseBody;
        private Path _lmbPath;
        private Path _rmbPath;
        private Ellipse _movementDot;
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush MouseBodyBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly Brush MouseButtonBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush DotBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        public KBMPreviewView()
        {
            InitializeComponent();
            CompositionTarget.Rendering += OnRendering;
        }

        public void Bind(PadViewModel vm)
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = vm;

            if (_vm != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                _vm.PropertyChanged += OnVmPropertyChanged;
                RebuildLayout();
            }
        }

        public void Unbind()
        {
            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
            _layoutBuilt = false;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.OutputType))
            {
                Dispatcher.Invoke(RebuildLayout);
                return;
            }
            // Mark dirty for any gamepad state change (RetrievedOutputState updates trigger this)
            _dirty = true;
        }

        private void RebuildLayout()
        {
            _layoutBuilt = false;

            if (_vm == null || _vm.OutputType != VirtualControllerType.KeyboardMouse) return;

            // Build keyboard layout
            _keys = KeyboardKeyItem.BuildLayout();
            KeyboardItemsControl.ItemsSource = _keys;

            // Build mouse visualization
            BuildMouseCanvas();

            _layoutBuilt = true;
            _dirty = true;
        }

        private void BuildMouseCanvas()
        {
            MouseCanvas.Children.Clear();

            // Mouse body outline (rounded rectangle shape)
            _mouseBody = new Path
            {
                Data = Geometry.Parse("M 40,20 C 40,8 50,0 80,0 C 110,0 120,8 120,20 L 120,160 C 120,180 110,195 80,195 C 50,195 40,180 40,160 Z"),
                Fill = MouseBodyBrush,
                Stroke = DimBrush,
                StrokeThickness = 2
            };
            MouseCanvas.Children.Add(_mouseBody);

            // Left mouse button (left half of top)
            _lmbPath = new Path
            {
                Data = Geometry.Parse("M 42,20 C 42,10 52,2 78,2 L 78,70 L 42,70 Z"),
                Fill = MouseButtonBrush,
                Stroke = DimBrush,
                StrokeThickness = 1
            };
            MouseCanvas.Children.Add(_lmbPath);

            // Right mouse button (right half of top)
            _rmbPath = new Path
            {
                Data = Geometry.Parse("M 82,2 C 108,2 118,10 118,20 L 118,70 L 82,70 Z"),
                Fill = MouseButtonBrush,
                Stroke = DimBrush,
                StrokeThickness = 1
            };
            MouseCanvas.Children.Add(_rmbPath);

            // Divider line between buttons
            var divider = new Line
            {
                X1 = 80, Y1 = 2, X2 = 80, Y2 = 70,
                Stroke = DimBrush,
                StrokeThickness = 1
            };
            MouseCanvas.Children.Add(divider);

            // Scroll wheel indicator
            var scrollWheel = new Ellipse
            {
                Width = 10, Height = 18,
                Fill = Brushes.Transparent,
                Stroke = DimBrush,
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(scrollWheel, 75);
            Canvas.SetTop(scrollWheel, 28);
            MouseCanvas.Children.Add(scrollWheel);

            // Movement indicator dot (shows stick deflection)
            _movementDot = new Ellipse
            {
                Width = 12, Height = 12,
                Fill = DotBrush
            };
            Canvas.SetLeft(_movementDot, 74);
            Canvas.SetTop(_movementDot, 127);
            MouseCanvas.Children.Add(_movementDot);

            // LMB / RMB labels
            var lmbLabel = new TextBlock { Text = "LMB", FontSize = 9, Foreground = Brushes.Gray, IsHitTestVisible = false };
            Canvas.SetLeft(lmbLabel, 50); Canvas.SetTop(lmbLabel, 50);
            MouseCanvas.Children.Add(lmbLabel);

            var rmbLabel = new TextBlock { Text = "RMB", FontSize = 9, Foreground = Brushes.Gray, IsHitTestVisible = false };
            Canvas.SetLeft(rmbLabel, 90); Canvas.SetTop(rmbLabel, 50);
            MouseCanvas.Children.Add(rmbLabel);
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_dirty || _vm == null || !_layoutBuilt) return;
            _dirty = false;

            var kbm = _vm.KbmOutputSnapshot;

            // Update keyboard key highlights from KBM raw state
            if (_keys != null)
            {
                foreach (var key in _keys)
                {
                    if (key.VKeyIndex >= 0 && key.VKeyIndex <= 255)
                        key.IsPressed = kbm.GetKey((byte)key.VKeyIndex);
                    else
                        key.IsPressed = false;
                }
            }

            // Update mouse buttons from KBM raw state
            _lmbPath.Fill = kbm.GetMouseButton(0) ? AccentBrush : MouseButtonBrush;
            _rmbPath.Fill = kbm.GetMouseButton(1) ? AccentBrush : MouseButtonBrush;

            // Update movement dot position based on mouse delta axes
            const short deadZone = 7849;
            double dotX = 74, dotY = 127; // center
            short mx = kbm.MouseDeltaX, my = kbm.MouseDeltaY;
            if (Math.Abs(mx) > deadZone)
                dotX += (mx - Math.Sign(mx) * deadZone) / (double)(32767 - deadZone) * 20;
            if (Math.Abs(my) > deadZone)
                dotY -= (my - Math.Sign(my) * deadZone) / (double)(32767 - deadZone) * 20;
            Canvas.SetLeft(_movementDot, dotX);
            Canvas.SetTop(_movementDot, dotY);

            bool isMoving = Math.Abs(mx) > deadZone || Math.Abs(my) > deadZone;
            _movementDot.Fill = isMoving ? AccentBrush : DotBrush;
        }

        private void SetKeyPressed(int vk, bool pressed)
        {
            if (!pressed || _keys == null) return;
            foreach (var key in _keys)
            {
                if (key.VKeyIndex == vk)
                {
                    key.IsPressed = true;
                    return;
                }
            }
        }
    }
}
