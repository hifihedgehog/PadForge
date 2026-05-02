using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PadForge.Views.Controls
{
    public enum LabeledShapeKind
    {
        Stick,
        TriggerLeft,
        TriggerRight
    }

    public partial class LabeledShapeIcon : UserControl
    {
        private static readonly FontFamily LabelFontFamily =
            new FontFamily("Bahnschrift SemiBold Condensed, Bahnschrift, Arial Narrow, Segoe UI");

        public static readonly DependencyProperty ShapeProperty =
            DependencyProperty.Register(nameof(Shape), typeof(LabeledShapeKind), typeof(LabeledShapeIcon),
                new PropertyMetadata(LabeledShapeKind.Stick, OnShapeChanged));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(LabeledShapeIcon),
                new PropertyMetadata(string.Empty, OnLabelChanged));

        public LabeledShapeKind Shape
        {
            get => (LabeledShapeKind)GetValue(ShapeProperty);
            set => SetValue(ShapeProperty, value);
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public LabeledShapeIcon()
        {
            InitializeComponent();
            Loaded += (_, _) => { UpdateShape(); UpdateLabel(); };
        }

        private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((LabeledShapeIcon)d).UpdateShape();

        private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((LabeledShapeIcon)d).UpdateLabel();

        private void UpdateShape()
        {
            object resource = Shape switch
            {
                LabeledShapeKind.TriggerRight => TryFind("TabTriggersIconRight") ?? TryFind("TabTriggersIcon"),
                LabeledShapeKind.TriggerLeft => TryFind("TabTriggersIcon"),
                _ => TryFind("TabSticksIcon")
            };

            if (resource is ImageSource img)
                ShapeImage.Source = img;
        }

        private object TryFind(string key)
            => Application.Current?.TryFindResource(key);

        private void UpdateLabel()
        {
            string text = Label ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                LabelHost.Visibility = Visibility.Collapsed;
                LabelPath.Data = null;
                return;
            }
            LabelHost.Visibility = Visibility.Visible;

            int len = text.Length;
            double fontSize = Shape == LabeledShapeKind.Stick
                ? (len <= 1 ? 220 : 170)
                : (len <= 1 ? 230 : 180);

            // Build the label as a real glyph geometry. Bounds give the
            // exact visible-glyph rectangle, avoiding all of the line-box
            // / ascender / descender ambiguity that comes with TextBlock
            // VerticalAlignment="Center".
            var typeface = new Typeface(LabelFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var ft = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White,
                pixelsPerDip: 1.0);

            var geometry = ft.BuildGeometry(new Point(0, 0));
            if (geometry == null || geometry.IsEmpty())
            {
                LabelPath.Data = null;
                return;
            }

            var bounds = geometry.Bounds;
            double cx = 256;
            double cy = Shape == LabeledShapeKind.Stick ? 256 : 240;
            double tx = cx - (bounds.X + bounds.Width / 2.0);
            double ty = cy - (bounds.Y + bounds.Height / 2.0);

            // Flatten and translate. Cloning preserves the original geometry
            // so subsequent rebuilds aren't affected by accumulated transforms.
            var flat = geometry.GetFlattenedPathGeometry();
            flat.Transform = new TranslateTransform(tx, ty);
            LabelPath.Data = flat;
        }
    }
}
