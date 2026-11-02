using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Controls;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// The picture on the Key Colors and Zone Colors card (#557): every
    /// light of the selected device in the color the layer being colored
    /// gives it, faded where it shows the Base layer's, the selected ones
    /// outlined in ember. Built in code like the Keyboard + Mouse preview's
    /// keyboard (KBMPreviewView.BuildDeviceKeyboard), with its legends and
    /// key fill.
    ///
    /// <para>Where the lights are drawn, best first: the positions the
    /// device reports (a LampArray's micrometers); the QMK layout PadForge
    /// draws keyboards from (KeyboardFamilies.g.cs) that holds the most of
    /// the device's keys, for a device that names its keys without
    /// positions; and a row of chips under the picture for every light
    /// with neither.</para>
    ///
    /// <para>A click on a light adds it to the selection or takes it out,
    /// and a drag across the picture adds every light it covers. The
    /// picture is one Tab stop. Inside it the arrow keys move between the
    /// lights, as WPF moves focus in a container that contains its
    /// directional navigation, and Space or Enter adds the focused light or
    /// takes it out, each light being a ClickBorder toggle a screen reader
    /// reads by name. With Select by Pressing Keys on, a key pressed while
    /// the picture has the focus adds that key's lights or takes them out,
    /// found by the scan code its window message carries, so the physical
    /// key is the one picked on any input layout. Ctrl+Tab and
    /// Ctrl+Shift+Tab leave the picture then, the way they leave a text box
    /// that takes Tab.</para>
    /// </summary>
    internal sealed class KeyLightsView : UserControl
    {
        /// <summary>The picture's width, inside the 780 the Base Color
        /// card's picker rows take.</summary>
        private const double TargetWidth = 720;

        /// <summary>Pixels per key unit on a QMK layout: a full-size board,
        /// 22.5 units wide, fills <see cref="TargetWidth"/>.</summary>
        private const double KeyUnit = 32, KeyGap = 3;

        private const double ChipHeight = 26, ChipGap = 6, ChipMinWidth = 56, LegendSize = 10;

        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09, VK_SNAPSHOT = 0x2C;

        private sealed class LightVisual
        {
            public int Index;
            public ClickBorder Border;
            public TextBlock Text;
            /// <summary>The light's place in the order picked.</summary>
            public TextBlock Order;
            public Rect Box;
            public string Name;
        }

        // A keyboard keeps its keys where they are in a right-to-left
        // language, as KBMPreviewView's keyboard does.
        private readonly Canvas _canvas = new() { Background = Brushes.Transparent, FlowDirection = FlowDirection.LeftToRight };
        private readonly Border _frame;
        private readonly Rectangle _band = new()
        {
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 }, Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        private readonly List<LightVisual> _visuals = new();
        private IKeyLightsSource _card;
        private LightMap _builtMap;
        private Point? _dragFrom;
        private bool _dragging;
        private int _focusIndex = -1;

        /// <summary>Picks made by a key still held, so Ctrl+Tab can take
        /// back the Ctrl or Shift it started with.</summary>
        private readonly List<(int ScanKey, ushort Usage)> _held = new();

        public KeyLightsView()
        {
            _canvas.SetResourceReference(TextElement.FontFamilyProperty, "TelemetryFontFamily");
            _band.SetResourceReference(Shape.StrokeProperty, "EmberBrush");
            Panel.SetZIndex(_band, 1);
            // Tab reaches the picture once, at the light that last had the
            // focus, and the arrow keys move between its lights.
            KeyboardNavigation.SetTabNavigation(_canvas, KeyboardNavigationMode.Once);
            KeyboardNavigation.SetDirectionalNavigation(_canvas, KeyboardNavigationMode.Contained);
            // The ember frame says the picture is listening for keys.
            _frame = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                BorderBrush = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    Child = _canvas,
                },
            };
            Content = _frame;
            _canvas.PreviewMouseLeftButtonDown += OnCanvasDown;
            _canvas.PreviewMouseMove += OnCanvasMove;
            _canvas.PreviewMouseLeftButtonUp += OnCanvasUp;
            _canvas.LostMouseCapture += (_, _) => EndDrag();
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            IsKeyboardFocusWithinChanged += (_, _) =>
            {
                if (!IsKeyboardFocusWithin) _held.Clear();
                SyncFrame();
            };
            Loaded += (_, _) =>
            {
                InputLanguageManager.Current.InputLanguageChanged -= OnInputLanguageChanged;
                InputLanguageManager.Current.InputLanguageChanged += OnInputLanguageChanged;
                Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnThemeChanged;
                Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnThemeChanged;
                Listen(true);
                Rebuild();
            };
            // A macro action's picker outlives the editor that draws it, so
            // the picture lets go of its source while it is out of the tree.
            Unloaded += (_, _) =>
            {
                InputLanguageManager.Current.InputLanguageChanged -= OnInputLanguageChanged;
                Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnThemeChanged;
                Listen(false);
            };
            // The names of lights that are not keys are words. Held weakly
            // by Strings.
            Strings.CultureChanged += OnCultureChanged;
        }

        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(object), typeof(KeyLightsView),
            new PropertyMetadata(null, (d, e) => ((KeyLightsView)d).Attach(e.NewValue as IKeyLightsSource)));

        /// <summary>What the picture draws and edits: the Key Colors card,
        /// or a key colors macro action's picker. Setting it redraws.</summary>
        public object Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private void Attach(IKeyLightsSource source)
        {
            if (ReferenceEquals(_card, source)) return;
            Listen(false);
            _card = source;
            _held.Clear();
            if (IsLoaded) Listen(true);
            SyncFrame();
            Rebuild();
        }

        private void Listen(bool on)
        {
            if (_card == null) return;
            _card.LightsChanged -= OnLightsChanged;
            _card.PropertyChanged -= OnCardPropertyChanged;
            if (!on) return;
            _card.LightsChanged += OnLightsChanged;
            _card.PropertyChanged += OnCardPropertyChanged;
        }

        private void OnLightsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnLightsChanged));
                return;
            }
            if (_card == null || !ReferenceEquals(_card.Map, _builtMap)) Rebuild();
            else Refresh();
        }

        private void OnCardPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(IKeyLightsSource.PickByPressing)) return;
            _held.Clear();
            SyncFrame();
            // Turning picking on from its switch sends the keys here.
            if (_card.PickByPressing && !IsKeyboardFocusWithin)
                Dispatcher.BeginInvoke(new Action(FocusPicture), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnInputLanguageChanged(object sender, InputLanguageEventArgs e) => Rebuild();

        private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, Color accent)
            => Dispatcher.BeginInvoke(new Action(Refresh));

        private void OnCultureChanged() => Dispatcher.BeginInvoke(new Action(Rebuild));

        private void SyncFrame()
        {
            if (_card?.PickByPressing == true && IsKeyboardFocusWithin)
                _frame.SetResourceReference(Border.BorderBrushProperty, "EmberBrush");
            else
                _frame.BorderBrush = Brushes.Transparent;
        }

        /// <summary>Puts the focus on the light that last had it, or the
        /// first.</summary>
        private void FocusPicture()
        {
            if (_visuals.Count == 0) return;
            (_visuals.FirstOrDefault(v => v.Index == _focusIndex) ?? _visuals[0]).Border.Focus();
        }

        // ─────────────────────────────────────────────
        //  Drawing
        // ─────────────────────────────────────────────

        private void Rebuild()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(Rebuild));
                return;
            }
            bool hadFocus = IsKeyboardFocusWithin;
            _canvas.Children.Clear();
            _canvas.Children.Add(_band);
            _visuals.Clear();
            _builtMap = _card?.Map;
            var map = _builtMap;
            if (map == null || map.Lights.Count == 0)
            {
                _canvas.Width = _canvas.Height = 0;
                return;
            }
            IntPtr layout = KbmHidKeyMap.UiLayout();
            var placed = map.Positioned ? PlaceByPosition(map) : PlaceByLayout(map);
            double width = 0, height = 0;
            foreach (var (index, box) in placed)
            {
                AddLight(index, map.Lights[index], box, chip: false, layout);
                width = Math.Max(width, box.Right);
                height = Math.Max(height, box.Bottom);
            }
            var onPicture = new HashSet<int>(placed.Select(p => p.Index));
            double x = 0, y = placed.Count > 0 ? height + ChipGap * 2 : 0;
            var typeface = new Typeface((FontFamily)FindResource("TelemetryFontFamily"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal);
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i < map.Lights.Count; i++)
            {
                if (onPicture.Contains(i)) continue;
                string legend = _card.LegendOf(i) ?? Legend(map.Lights[i], layout);
                double w = Math.Max(ChipMinWidth, new FormattedText(legend, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, LegendSize, Brushes.Black, dip).WidthIncludingTrailingWhitespace + 20);
                if (x > 0 && x + w > TargetWidth)
                {
                    x = 0;
                    y += ChipHeight + ChipGap;
                }
                var box = new Rect(x, y, w, ChipHeight);
                AddLight(i, map.Lights[i], box, chip: true, layout);
                x += w + ChipGap;
                width = Math.Max(width, box.Right);
                height = Math.Max(height, box.Bottom);
            }
            _canvas.Width = width;
            _canvas.Height = height;
            Refresh();
            if (hadFocus) FocusPicture();
        }

        /// <summary>Lights at the positions the device reports, scaled to
        /// the card's width. A light with a width and a height spans them.
        /// A point light, a LampArray's lamp, is drawn around its point:
        /// nine tenths of the usual gap between neighboring keys for a key
        /// or a zone, and a third of that for any other light.</summary>
        internal static List<(int Index, Rect Box)> PlaceByPosition(LightMap map)
        {
            var lights = map.Lights;
            double pitch = KeyPitch(lights);
            var spans = new Rect[lights.Count];
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                spans[i] = l.Width > 0 && l.Height > 0
                    ? new Rect(l.X, l.Y, l.Width, l.Height)
                    : new Rect(l.X - pitch / 2, l.Y - pitch / 2, pitch, pitch);
            }
            var bounds = spans[0];
            foreach (var span in spans) bounds.Union(span);
            double scale = TargetWidth / Math.Max(bounds.Width, pitch);
            double keySize = pitch * scale * 0.9;
            double smallSize = Math.Max(6, keySize * 0.35);
            var placed = new List<(int, Rect)>(lights.Count);
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                var span = spans[i];
                if (l.Width > 0 && l.Height > 0)
                {
                    placed.Add((i, new Rect((span.X - bounds.X) * scale, (span.Y - bounds.Y) * scale,
                        Math.Max(1, span.Width * scale - KeyGap), Math.Max(1, span.Height * scale - KeyGap))));
                    continue;
                }
                double size = l.Kind is LightKind.Key or LightKind.Zone ? keySize : smallSize;
                double cx = (l.X - bounds.X) * scale, cy = (l.Y - bounds.Y) * scale;
                placed.Add((i, new Rect(cx - size / 2, cy - size / 2, size, size)));
            }
            return placed;
        }

        /// <summary>The usual distance between the centers of a key and its
        /// nearest neighbor, the median over the keys, or a tenth of the
        /// device's width when it has fewer than two keys.</summary>
        internal static double KeyPitch(IReadOnlyList<LightInfo> lights)
        {
            static Point Center(LightInfo l) => l.Width > 0 && l.Height > 0
                ? new Point(l.X + l.Width / 2.0, l.Y + l.Height / 2.0) : new Point(l.X, l.Y);
            var keys = lights.Where(l => l.Kind is LightKind.Key or LightKind.Zone).Select(Center).ToList();
            if (keys.Count < 2)
                return Math.Max(1, (lights.Max(l => Center(l).X) - lights.Min(l => Center(l).X)) / 10.0);
            var nearest = new List<double>(keys.Count);
            for (int a = 0; a < keys.Count; a++)
            {
                double best = double.MaxValue;
                for (int b = 0; b < keys.Count; b++)
                {
                    if (a == b) continue;
                    double d = (keys[a] - keys[b]).Length;
                    if (d > 0 && d < best) best = d;
                }
                if (best < double.MaxValue) nearest.Add(best);
            }
            if (nearest.Count == 0) return 1;
            nearest.Sort();
            return Math.Max(1, nearest[nearest.Count / 2]);
        }

        /// <summary>Keys named by usage alone, on the QMK layout that holds
        /// the most of them, the smaller layout between two that hold as
        /// many. A light the layout has no key for joins the chips.</summary>
        internal static List<(int Index, Rect Box)> PlaceByLayout(LightMap map)
        {
            var placed = new List<(int, Rect)>();
            if (map.KeyCount == 0) return placed;
            var usages = new HashSet<ushort>(map.Usages);
            LayoutKey[] best = null;
            int bestHits = 0;
            foreach (var layout in Layouts)
            {
                int hits = layout.Count(k => k.Page == 0x07 && usages.Contains(k.Id));
                if (hits > bestHits || (hits == bestHits && hits > 0 && layout.Length < best.Length))
                {
                    best = layout;
                    bestHits = hits;
                }
            }
            if (best == null) return placed;
            var drawn = new HashSet<ushort>();
            foreach (var key in best)
            {
                // A layout that draws one usage twice puts its lights on
                // the first.
                if (key.Page != 0x07 || !drawn.Add(key.Id)) continue;
                foreach (int index in map.IndexesOf(key.Id))
                    placed.Add((index, new Rect(key.X * KeyUnit, key.Y * KeyUnit,
                        key.W * KeyUnit - KeyGap, key.H * KeyUnit - KeyGap)));
            }
            return placed;
        }

        private static readonly LayoutKey[][] Layouts =
        {
            KeyboardFamilies.FullsizeAnsi, KeyboardFamilies.FullsizeIso, KeyboardFamilies.FullsizeExtended,
            KeyboardFamilies.Tkl, KeyboardFamilies.SeventyFive, KeyboardFamilies.SixtyFive,
            KeyboardFamilies.SixtyFiveBlocker, KeyboardFamilies.Sixty, KeyboardFamilies.Hhkb, KeyboardFamilies.HhkbJis,
            KeyboardFamilies.KeychronQ6,
        };

        private void AddLight(int index, LightInfo light, Rect box, bool chip, IntPtr layout)
        {
            bool big = chip || light.Kind is LightKind.Key or LightKind.Zone;
            string legend = _card.LegendOf(index) ?? Legend(light, layout);
            var text = new TextBlock
            {
                Text = big ? legend : string.Empty,
                FontSize = LegendSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
            };
            var order = new TextBlock
            {
                FontSize = 8,
                Margin = new Thickness(3, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            var face = new Grid();
            face.Children.Add(text);
            face.Children.Add(order);
            var border = new ClickBorder
            {
                Width = box.Width,
                Height = box.Height,
                CornerRadius = new CornerRadius(big ? (chip ? 6 : 4) : box.Width / 2),
                Cursor = Cursors.Hand,
                IsChecked = false,
                Child = face,
            };
            border.Click += (_, e) =>
            {
                _card?.Toggle(index);
                e.Handled = true;
            };
            border.GotKeyboardFocus += (_, _) => _focusIndex = index;
            Canvas.SetLeft(border, box.X);
            Canvas.SetTop(border, box.Y);
            _canvas.Children.Add(border);
            _visuals.Add(new LightVisual
            {
                Index = index, Border = border, Text = text, Order = order, Box = box, Name = LightName(light, legend, layout),
            });
        }

        /// <summary>Paints every light from the card: its own color, the
        /// Base layer's faded, or the key fill, and the selection.</summary>
        private void Refresh()
        {
            if (_card == null) return;
            var selection = _card.Selection;
            var s = Strings.Instance;
            foreach (var v in _visuals)
            {
                int? own = _card.OwnColorOf(v.Index);
                int? inherited = own.HasValue ? null : _card.BaseColorOf(v.Index);
                bool selected = selection.Contains(v.Index);
                string state;
                if (own is int rgb)
                {
                    v.Border.Background = Solid(rgb, 0xFF);
                    v.Text.Foreground = v.Order.Foreground = Luma(rgb) > 140 ? Brushes.Black : Brushes.White;
                    state = string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_LightColor_Format, v.Name, Hex(rgb));
                }
                else if (inherited is int under)
                {
                    v.Border.Background = Solid(under, 0x66);
                    v.Text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                    v.Order.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                    state = string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_LightBaseLayer_Format,
                        v.Name, Hex(under), _card.BaseLayerName);
                }
                else
                {
                    v.Border.Background = KBMPreviewView.KeyNormalBrush;
                    v.Text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    v.Order.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    state = v.Name;
                }
                int place = _card.OrderOf(v.Index);
                v.Order.Text = place > 0 ? place.ToString(CultureInfo.CurrentCulture) : string.Empty;
                v.Order.Visibility = place > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (place > 0)
                    state = string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_LightOrder_Format, state, place);
                if (selected)
                {
                    v.Border.SetResourceReference(Border.BorderBrushProperty, "EmberBrush");
                    v.Border.BorderThickness = new Thickness(2);
                }
                else
                {
                    v.Border.BorderBrush = null;
                    v.Border.BorderThickness = new Thickness(0);
                }
                v.Border.IsChecked = selected;
                v.Border.ToolTip = state;
                AutomationProperties.SetName(v.Border, state);
            }
        }

        private static SolidColorBrush Solid(int rgb, byte alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }

        private static double Luma(int rgb)
            => 0.299 * ((rgb >> 16) & 0xFF) + 0.587 * ((rgb >> 8) & 0xFF) + 0.114 * (rgb & 0xFF);

        private static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

        /// <summary>The legend a light carries: a key's the way the
        /// Keyboard + Mouse preview writes it for the input layout in use, a
        /// zone's number, or the name of what the light is.</summary>
        internal static string Legend(LightInfo light, IntPtr layout)
        {
            var s = Strings.Instance;
            if (LabelText(light) is { } label) return label;
            return light.Kind switch
            {
                LightKind.Key when light.Usage != 0 => KeyLegend(light.Usage, layout),
                LightKind.Key => string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_UnnamedKey_Format, light.Address),
                LightKind.Zone => string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_Zone_Format, light.Zone),
                LightKind.Edge => s.Pad_Lighting_KeyColors_Edge,
                LightKind.Logo => s.Pad_Lighting_KeyColors_Logo,
                LightKind.Status => s.Pad_Lighting_KeyColors_Status,
                _ => string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_Light_Format, light.Address),
            };
        }

        /// <summary>The words for a light's label (<see cref="LightLabel"/>),
        /// or null for a light with none. A G-key reads as Logitech prints
        /// it, and the media keys and the lock lights take the names the
        /// macro editor gives those keys. The brightness key takes the
        /// Lighting tab's word for brightness.</summary>
        internal static string LabelText(LightInfo light)
        {
            var s = Strings.Instance;
            return light.Label switch
            {
                LightLabel.GKey => "G" + light.LabelNumber.ToString(CultureInfo.InvariantCulture),
                LightLabel.Brightness => s.Pad_Lighting_GuideLed_Brightness,
                LightLabel.PlayPause => s.Key_MediaPlayPause,
                LightLabel.Mute => s.Key_VolumeMute,
                LightLabel.Next => s.Key_MediaNext,
                LightLabel.Previous => s.Key_MediaPrev,
                LightLabel.Stop => s.Key_MediaStop,
                LightLabel.GameMode => s.Pad_Lighting_KeyColors_GameMode,
                LightLabel.CapsLock => s.Key_CapsLock,
                LightLabel.ScrollLock => s.Key_ScrollLock,
                LightLabel.NumLock => s.Key_NumLock,
                _ => null,
            };
        }

        /// <summary>A key's legend on <paramref name="layout"/>, as
        /// KBMPreviewView.BuildDeviceKeyboard writes it: blank on the space
        /// bar, as there.</summary>
        internal static string KeyLegend(ushort usage, IntPtr layout)
        {
            string legend = KeyboardFamilies.FixedLegend(new LayoutKey(0, 0, 1, 1, 0x070000 | usage), KeyboardFamilies.Legends.Pc);
            if (legend != null) return legend;
            int vk = KbmHidKeyMap.TargetOf(new KbmHidKeyMap.HidKey(0x07, usage), layout);
            if (KbmHidKeyMap.IsCharacterUsage(usage))
                legend = KbmHidKeyMap.CharacterLegend(vk, layout);
            return legend ?? KBMPreviewView.ShortLegend(vk);
        }

        /// <summary>What a screen reader and the tooltip call a light: a
        /// key by the character it types on <paramref name="layout"/> or the
        /// name the macro editor gives its key, and anything else by its
        /// legend.</summary>
        internal static string LightName(LightInfo light, string legend, IntPtr layout)
        {
            if (light.Kind != LightKind.Key || light.Usage == 0) return legend;
            ushort usage = light.Usage;
            if (usage == 0x58)
                return string.Format(CultureInfo.CurrentCulture, Strings.Instance.Key_Numpad, Strings.Instance.Key_Enter);
            int vk = KbmHidKeyMap.TargetOf(new KbmHidKeyMap.HidKey(0x07, usage), layout);
            if (KbmHidKeyMap.IsCharacterUsage(usage) && KbmHidKeyMap.CharacterLegend(vk, layout) is { } character)
                return character;
            if (vk > 0 && Enum.IsDefined(typeof(VirtualKey), vk))
                return MacroAction.VirtualKeyDisplayName((VirtualKey)vk);
            return string.IsNullOrEmpty(legend) ? usage.ToString("X2", CultureInfo.InvariantCulture) : legend;
        }

        // ─────────────────────────────────────────────
        //  Selecting by dragging
        // ─────────────────────────────────────────────

        private void OnCanvasDown(object sender, MouseButtonEventArgs e)
        {
            _dragFrom = e.GetPosition(_canvas);
            _dragging = false;
            // A click puts the focus in the picture, where picking by
            // pressing listens. The focus ring shows only for the keyboard.
            if (e.OriginalSource is DependencyObject source && FindLight(source) is { } light) light.Border.Focus();
            else if (_card?.PickByPressing == true) FocusPicture();
        }

        private LightVisual FindLight(DependencyObject source)
        {
            for (var d = source; d != null && d != _canvas; d = VisualTreeHelper.GetParent(d))
                if (d is ClickBorder border) return _visuals.FirstOrDefault(v => v.Border == border);
            return null;
        }

        private void OnCanvasMove(object sender, MouseEventArgs e)
        {
            if (!_dragFrom.HasValue) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }
            var now = e.GetPosition(_canvas);
            if (!_dragging)
            {
                if (Math.Abs(now.X - _dragFrom.Value.X) <= SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(now.Y - _dragFrom.Value.Y) <= SystemParameters.MinimumVerticalDragDistance)
                    return;
                _dragging = true;
                _canvas.CaptureMouse();
            }
            var rect = new Rect(_dragFrom.Value, now);
            _band.Width = rect.Width;
            _band.Height = rect.Height;
            Canvas.SetLeft(_band, rect.X);
            Canvas.SetTop(_band, rect.Y);
            _band.Visibility = Visibility.Visible;
        }

        private void OnCanvasUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
            {
                // A click: the light under it takes it.
                _dragFrom = null;
                return;
            }
            var rect = new Rect(_dragFrom.Value, e.GetPosition(_canvas));
            EndDrag();
            _card?.SetSelection(_card.Selection.Concat(_visuals.Where(v => rect.IntersectsWith(v.Box)).Select(v => v.Index)));
            // A drag that ends on a light is not a click on it.
            e.Handled = true;
        }

        private void EndDrag()
        {
            bool dragging = _dragging;
            _dragFrom = null;
            _dragging = false;
            _band.Visibility = Visibility.Collapsed;
            if (dragging && _canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        }

        // ─────────────────────────────────────────────
        //  Selecting by pressing keys
        // ─────────────────────────────────────────────

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_card?.PickByPressing != true) return;
            var msg = ComponentDispatcher.CurrentKeyboardMessage;
            e.Handled = true;
            if (msg.message is not (WM_KEYDOWN or WM_SYSKEYDOWN)) return;
            int vk = (int)(long)msg.wParam & 0xFF;
            if (vk == VK_TAB && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // The way out. The Ctrl and Shift held for it are not picks.
                foreach (var held in _held.Where(h => h.Usage is >= 0xE0 and <= 0xE7).ToList())
                    _card.TogglePressed(held.Usage);
                _held.Clear();
                var direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
                (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(direction));
                return;
            }
            if (e.IsRepeat || IsAltGrLeftControl(msg)) return;
            int scanKey = ScanKey(msg);
            if (Pick(scanKey, vk) is ushort usage) _held.Add((scanKey, usage));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

        /// <summary>Whether a key message is the Left Ctrl AltGr sends ahead
        /// of its Right Alt, which no key the user pressed made: a Left Ctrl
        /// down with the Right Alt down next in the queue at the same time,
        /// the test SDL's SkipAltGrLeftControl (SDL_windowsevents.c)
        /// makes.</summary>
        internal static bool IsAltGrLeftControl(MSG msg)
        {
            const int VK_CONTROL = 0x11, VK_MENU = 0x12, Extended = 0x01000000;
            if ((int)(long)msg.wParam != VK_CONTROL || ((int)(long)msg.lParam & Extended) != 0) return false;
            if (!PeekMessageW(out var next, IntPtr.Zero, 0, 0, 0)) return false;
            return next.message is WM_KEYDOWN or WM_SYSKEYDOWN && (int)(long)next.wParam == VK_MENU
                && ((int)(long)next.lParam & Extended) != 0 && next.time == msg.time;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (_card?.PickByPressing != true) return;
            e.Handled = true;
            var msg = ComponentDispatcher.CurrentKeyboardMessage;
            if (msg.message is not (WM_KEYUP or WM_SYSKEYUP)) return;
            int scanKey = ScanKey(msg);
            _held.RemoveAll(h => h.ScanKey == scanKey);
            // Windows sends Print Screen's release alone.
            int vk = (int)(long)msg.wParam & 0xFF;
            if (vk == VK_SNAPSHOT) Pick(scanKey, vk);
        }

        /// <summary>The scan key a key message's lParam carries, 0x100 for
        /// the E0 form, read the way HwndKeyboardInputProvider reads it, or
        /// 0 for input that carries none.</summary>
        internal static int ScanKey(MSG msg)
        {
            int data = (int)(long)msg.lParam;
            int scan = (data >> 16) & 0xFF;
            return scan == 0 ? 0 : scan | ((data & 0x01000000) != 0 ? 0x100 : 0);
        }

        /// <summary>Adds the lights of the key behind
        /// <paramref name="scanKey"/> to the selection or takes them out,
        /// and returns its usage, or null when the device has no light for
        /// it. Input that carries no scan code goes by its key code on
        /// PadForge's input layout.</summary>
        private ushort? Pick(int scanKey, int vk)
        {
            IEnumerable<ushort> usages = scanKey != 0
                ? KbmHidKeyMap.UsagesOfScanKey(scanKey)
                : new[] { KbmHidKeyMap.Resolve(vk, KbmHidKeyMap.UiLayout()) }.Where(k => k.IsKeyboard).Select(k => k.Usage);
            foreach (var usage in usages)
                if (_card.TogglePressed(usage)) return usage;
            return null;
        }
    }
}
