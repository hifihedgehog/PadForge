using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PadForge.Views
{
    /// <summary>
    /// Lightweight always-on-top overlay window that surfaces the
    /// currently-engaged shift layer's name + color. Anchored top-right
    /// of the primary screen. Owned by InputService which polls the
    /// engine's shift-engagement state at ~30Hz and calls
    /// <see cref="Show(string, string)"/> when a layer is engaged or
    /// <see cref="HideOverlay"/> when the slot returns to Base.
    /// </summary>
    public partial class ShiftLayerOverlay : Window
    {
        public ShiftLayerOverlay()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Position top-right of primary screen. Defer to Loaded so the
            // window has a measured size for the offset calculation.
            var src = PresentationSource.FromVisual(this) as HwndSource;
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double screenW = SystemParameters.PrimaryScreenWidth;
            Left = screenW - ActualWidth - 16 / scale;
            Top = 48 / scale;

            // Click-through: ignore mouse hit tests so the overlay never
            // intercepts a click meant for whatever's behind it.
            if (src != null)
            {
                IntPtr hwnd = src.Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
            }
        }

        /// <summary>Updates the displayed layer name + dot color and shows
        /// the overlay if it isn't already visible.</summary>
        public void ShowLayer(string layerName, string colorHex)
        {
            LayerNameText.Text = string.IsNullOrEmpty(layerName) ? "" : layerName;
            ColorDot.Fill = ParseColor(colorHex) ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            if (Visibility != Visibility.Visible)
                Show();
        }

        public void HideOverlay()
        {
            if (Visibility == Visibility.Visible)
                Hide();
        }

        private static SolidColorBrush ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color c)
                    return new SolidColorBrush(c);
            }
            catch { }
            return null;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
