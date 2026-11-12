using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Common.Input;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// The On-Screen Keyboard's window (#566). Owned by InputService, which
    /// reads <c>InputManager.OnScreenKeyboardView</c> on the UI timer and
    /// calls <see cref="UpdateFromView"/> with the keyboard menu that owns it,
    /// as it does for the menu overlay. A change of shape (style, page, rows,
    /// layout, theme, controller family) rebuilds the keys. Other ticks
    /// restyle the ones there. The window never activates, so the window being
    /// typed into keeps focus, but it does take clicks: a key click, the close
    /// button, a drag of the top bar and the corner grip go back to the poll
    /// thread or to the menu's Screen Position and Size through the callbacks.
    /// </summary>
    public partial class OnScreenKeyboardWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>A key unit and the gap between keys, in DIPs at 100%.</summary>
        private const double KeyUnit = 42, Gap = 5;
        private const double EdgeMargin = 12;

        private readonly Action<OskCommand> _post;
        private readonly Action<double, double> _moved;
        private readonly Action<int> _resized;

        private sealed class KeyVisual
        {
            public int Board, Row, Index;
            public Border Box;
            public TextBlock Label, Corner;
        }

        private readonly List<KeyVisual> _keys = new();
        private Ellipse[] _petals;
        private TextBlock[] _wheelText;
        private TextBlock _wheelCenter;
        private OskView _view;
        private string _shape;
        private (bool Dark, Brush Accent) _theme;
        private string _hintsKey;
        private (int X, int Y, double W, double H, Rect WorkArea) _placed;

        private Brush _fill, _stroke, _text, _muted, _accent, _accentRight, _latch, _onAccent;

        internal OnScreenKeyboardWindow(Action<OskCommand> post, Action<double, double> moved, Action<int> resized)
        {
            _post = post;
            _moved = moved;
            _resized = resized;
            InitializeComponent();
            Opacity = 0.95;
            // The keys keep a keyboard's physical order in every language.
            PadForge.Common.UiLanguage.KeepLeftToRight(this);
            Loaded += OnLoaded;
            Grip.MouseLeftButtonDown += DragStart;
            TitleText.MouseLeftButtonDown += DragStart;
            MouseMove += OnDragMove;
            MouseLeftButtonUp += DragEnd;
            CloseButton.MouseLeftButtonUp += (_, e) =>
            {
                _post(new OskCommand(OskCommandKind.Close));
                e.Handled = true;
            };
            CloseButton.ToolTip = Strings.Instance.Osk_CloseKeyboard;
            Grip.ToolTip = Strings.Instance.Osk_Drag;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            return IntPtr.Zero;
        }

        public void HideKeyboard()
        {
            if (Visibility == Visibility.Visible) Hide();
            _view = null;
        }

        /// <summary>Draws the snapshot for the keyboard menu that owns it. A
        /// closed or missing one hides the window. <paramref name="describe"/>
        /// names an input that is not a device-free gamepad name, from its
        /// descriptor and device, the way the slot's input lists name it
        /// ("Button 1" on a flight stick).</summary>
        public void UpdateFromView(OskView view, MenuDefinitionEntry menu, Func<string, string, string> describe)
        {
            if (view == null || !view.Open || menu == null)
            {
                HideKeyboard();
                return;
            }
            bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            var accent = Application.Current?.TryFindResource("SystemAccentColorPrimaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xF2, 0x5C, 0x1F));
            var theme = (dark, accent);
            string shape = view.Shape + "|" + view.Family + "|" + dark;
            if (shape != _shape || !Equals(theme, _theme))
            {
                _theme = theme;
                MakeBrushes(dark, accent);
                Root.Background = new SolidColorBrush(dark ? Color.FromArgb(0xEE, 0x1B, 0x1E, 0x24) : Color.FromArgb(0xF2, 0xF4, 0xF5, 0xF7));
                Root.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
                Grip.Foreground = _muted;
                TitleText.Foreground = _muted;
                CloseButton.BorderBrush = _stroke;
                CloseButton.Background = _fill;
                ((TextBlock)CloseButton.Child).Foreground = _text;
                Build(view);
                _shape = shape;
                _hintsKey = null;
            }
            _view = view;
            Restyle(view);
            string title = string.IsNullOrWhiteSpace(menu.Name) ? Strings.Instance.Osk_Title : menu.Name;
            TitleText.Text = view.Style == OskStyle.Wheel || string.IsNullOrEmpty(view.LayoutName)
                ? title
                : title + "  ·  " + view.LayoutName;
            BuildHints(view, describe);
            Opacity = Math.Clamp(menu.OpacityPercent, 5, 100) / 100.0;
            if (!_resizing)
            {
                double scale = Math.Clamp(menu.ScalePercent, MinScalePercent, MaxScalePercent) / 100.0;
                Scale.ScaleX = Scale.ScaleY = scale;
            }
            if (Visibility != Visibility.Visible) Show();
            Place(menu);
        }

        /// <summary>The Menu Overlay rows' Size range, which the corner grip
        /// keeps to (MenuEditorItem.ScalePercent).</summary>
        internal const int MinScalePercent = 10, MaxScalePercent = 400;

        private void MakeBrushes(bool dark, Brush accent)
        {
            _fill = Frozen(dark ? Color.FromRgb(0x2D, 0x31, 0x38) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            _stroke = Frozen(dark ? Color.FromRgb(0x14, 0x16, 0x1A) : Color.FromRgb(0xD0, 0xD4, 0xDA));
            _text = dark ? Brushes.White : Frozen(Color.FromRgb(0x1A, 0x1A, 0x1A));
            _muted = Frozen(dark ? Color.FromRgb(0x9A, 0xA3, 0xAF) : Color.FromRgb(0x5F, 0x6B, 0x7A));
            _accent = accent;
            _accentRight = Frozen(Color.FromRgb(0x2B, 0x9F, 0xBA));
            _latch = Frozen(Color.FromRgb(0xE8, 0xB8, 0x4D));
            _onAccent = Brushes.White;
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ── Building ────────────────────────────────────────────

        private void Build(OskView view)
        {
            _keys.Clear();
            _petals = null;
            _wheelText = null;
            _wheelCenter = null;
            if (view.Style == OskStyle.Wheel)
            {
                KeysHost.Content = BuildWheel();
                return;
            }
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            for (int b = 0; b < view.Boards.Length; b++)
            {
                var canvas = BuildBoard(view.Boards[b], b);
                if (b > 0) canvas.Margin = new Thickness(26, 0, 0, 0);
                panel.Children.Add(canvas);
            }
            KeysHost.Content = panel;
        }

        private Canvas BuildBoard(OskViewBoard board, int boardIndex)
        {
            var canvas = new Canvas
            {
                Width = board.MaxW * (KeyUnit + Gap) - Gap,
                Height = board.Rows.Length * (KeyUnit + Gap) - Gap,
            };
            for (int r = 0; r < board.Rows.Length; r++)
            {
                for (int i = 0; i < board.Rows[r].Length; i++)
                {
                    var k = board.Rows[r][i];
                    double w = k.W * KeyUnit + (k.W - 1) * Gap;
                    var label = new TextBlock
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false,
                    };
                    var corner = new TextBlock
                    {
                        FontSize = 10,
                        Margin = new Thickness(5, 2, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsHitTestVisible = false,
                    };
                    var grid = new Grid();
                    grid.Children.Add(label);
                    grid.Children.Add(corner);
                    var box = new Border
                    {
                        Width = w,
                        Height = KeyUnit,
                        CornerRadius = new CornerRadius(7),
                        BorderThickness = new Thickness(1.2),
                        Child = grid,
                        Cursor = Cursors.Hand,
                    };
                    Canvas.SetLeft(box, k.Start * (KeyUnit + Gap));
                    Canvas.SetTop(box, r * (KeyUnit + Gap));
                    var kv = new KeyVisual { Board = boardIndex, Row = r + board.RowOffset, Index = i, Box = box, Label = label, Corner = corner };
                    box.MouseLeftButtonUp += (_, e) =>
                    {
                        _post(new OskCommand(OskCommandKind.ClickKey, kv.Board, kv.Row, kv.Index, _view?.Shape));
                        e.Handled = true;
                    };
                    canvas.Children.Add(box);
                    _keys.Add(kv);
                }
            }
            return canvas;
        }

        private UIElement BuildWheel()
        {
            const double size = 330, c = size / 2, ring = 110, petal = 47, off = 21;
            var canvas = new Canvas { Width = size, Height = size };
            var back = new Ellipse { Width = size - 4, Height = size - 4, Stroke = _stroke, StrokeThickness = 1, Fill = Frozen(Color.FromArgb(0x10, 0x80, 0x80, 0x80)) };
            Canvas.SetLeft(back, 2);
            Canvas.SetTop(back, 2);
            canvas.Children.Add(back);
            _petals = new Ellipse[8];
            _wheelText = new TextBlock[32];
            (double X, double Y)[] spots = { (-off, 0), (0, -off), (off, 0), (0, off) };
            for (int p = 0; p < 8; p++)
            {
                double a = (p * 45 - 90) * Math.PI / 180;
                double x = c + ring * Math.Cos(a), y = c + ring * Math.Sin(a);
                var e = new Ellipse { Width = petal * 2, Height = petal * 2, StrokeThickness = 1.4 };
                Canvas.SetLeft(e, x - petal);
                Canvas.SetTop(e, y - petal);
                canvas.Children.Add(e);
                _petals[p] = e;
                for (int k = 0; k < 4; k++)
                {
                    int petalIndex = p, spot = k;
                    var t = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Background = Brushes.Transparent, Padding = new Thickness(3, 0, 3, 0) };
                    t.MouseLeftButtonUp += (_, ev) =>
                    {
                        _post(new OskCommand(OskCommandKind.ClickWheel, petalIndex, spot, 0, _view?.Shape));
                        ev.Handled = true;
                    };
                    t.Tag = (x + spots[k].X, y + spots[k].Y);
                    canvas.Children.Add(t);
                    _wheelText[p * 4 + k] = t;
                }
            }
            var hub = new Ellipse { Width = 100, Height = 100, Fill = _fill, Stroke = _stroke, StrokeThickness = 1 };
            Canvas.SetLeft(hub, c - 50);
            Canvas.SetTop(hub, c - 50);
            canvas.Children.Add(hub);
            _wheelCenter = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            _wheelCenter.Tag = (c, c);
            canvas.Children.Add(_wheelCenter);
            return canvas;
        }

        private static void Center(TextBlock t)
        {
            if (t.Tag is not ValueTuple<double, double> at) return;
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, at.Item1 - t.DesiredSize.Width / 2);
            Canvas.SetTop(t, at.Item2 - t.DesiredSize.Height / 2);
        }

        // ── Restyling ───────────────────────────────────────────

        private void Restyle(OskView view)
        {
            if (view.Style == OskStyle.Wheel)
            {
                RestyleWheel(view);
                return;
            }
            foreach (var kv in _keys)
            {
                if (kv.Board >= view.Boards.Length) continue;
                var board = view.Boards[kv.Board];
                int r = kv.Row - board.RowOffset;
                if (r < 0 || r >= board.Rows.Length || kv.Index >= board.Rows[r].Length) continue;
                var k = board.Rows[r][kv.Index];
                bool focus = r == board.FocusRow && kv.Index == board.FocusIndex;
                string label = LabelOf(k, view);
                kv.Label.Text = label ?? "";
                bool special = k.Kind == OskKeyKind.Special;
                kv.Label.FontSize = special ? (label != null && label.Length <= 2 ? 16 : 11.5) : (k.Kind == OskKeyKind.Suggestion ? 13.5 : 17);
                kv.Label.FontStyle = k.Dead ? FontStyles.Italic : FontStyles.Normal;
                kv.Corner.Text = k.Corner ?? "";
                Brush fill = _fill, fg = special ? _muted : _text, stroke = _stroke;
                if (k.Kind == OskKeyKind.Suggestion) { fill = Brushes.Transparent; fg = _text; }
                if (k.State == 1) { stroke = _latch; fg = _latch; }
                if (k.State == 2) { fill = _latch; fg = Frozen(Color.FromRgb(0x1A, 0x1A, 0x1A)); stroke = _latch; }
                if (focus)
                {
                    fill = view.Boards.Length > 1 && kv.Board == 1 ? _accentRight : _accent;
                    fg = _onAccent;
                    stroke = fill;
                }
                kv.Box.Background = fill;
                kv.Box.BorderBrush = stroke;
                kv.Box.BorderThickness = new Thickness(focus ? 2 : 1.2);
                kv.Label.Foreground = fg;
                kv.Corner.Foreground = focus ? _onAccent : _muted;
                kv.Box.Opacity = k.Enabled ? 1 : 0.35;
            }
        }

        private void RestyleWheel(OskView view)
        {
            var w = view.Wheel;
            if (w == null || _petals == null) return;
            for (int p = 0; p < 8; p++)
            {
                bool on = p == w.Petal;
                _petals[p].Fill = on ? WithAlpha(_accent, 0x55) : _fill;
                _petals[p].Stroke = on ? _accent : _stroke;
                for (int k = 0; k < 4; k++)
                {
                    int i = p * 4 + k;
                    var t = _wheelText[i];
                    string label = w.Labels[i];
                    if (label == null && w.Specials[i] != OskSpecial.None) label = SpecialName(w.Specials[i], view);
                    t.Text = label ?? "";
                    t.FontSize = label != null && label.Length > 2 ? 10.5 : 16;
                    t.Foreground = on ? FaceBrush(view.Family, (k switch { 0 => OskInput.West, 1 => OskInput.North, 2 => OskInput.East, _ => OskInput.South })) ?? _text : _text;
                    Center(t);
                }
            }
            string set = w.Set switch
            {
                2 => "123",
                3 => Strings.Instance.Osk_Wheel_Keys,
                _ => string.Concat(w.Labels.Take(3).Where(l => l != null)),
            };
            _wheelCenter.Text = set;
            _wheelCenter.Foreground = _text;
            Center(_wheelCenter);
        }

        private static Brush WithAlpha(Brush b, byte a)
        {
            var c = (b as SolidColorBrush)?.Color ?? Color.FromRgb(0xF2, 0x5C, 0x1F);
            return Frozen(Color.FromArgb(a, c.R, c.G, c.B));
        }

        private static string LabelOf(OskViewKey k, OskView view)
        {
            if (k.Kind != OskKeyKind.Special) return k.Label;
            return SpecialName(k.Special, view) ?? k.Label;
        }

        private static string SpecialName(OskSpecial s, OskView view)
        {
            var S = Strings.Instance;
            return s switch
            {
                OskSpecial.Backspace => "⌫",
                OskSpecial.Tab => S.Osk_Key_Tab,
                OskSpecial.CapsLock => S.Osk_Key_Caps,
                OskSpecial.Enter => S.Osk_Key_Enter,
                OskSpecial.Shift => S.Osk_Key_Shift,
                OskSpecial.Ctrl => S.Osk_Key_Ctrl,
                OskSpecial.Win => S.Osk_Key_Win,
                OskSpecial.Alt => S.Osk_Key_Alt,
                OskSpecial.AltGr => S.Osk_Key_AltGr,
                OskSpecial.Page => view.Page == 0 ? S.Osk_Key_Symbols : S.Osk_Key_Letters,
                OskSpecial.Space => S.Osk_Key_Space,
                OskSpecial.Left => "←",
                OskSpecial.Up => "↑",
                OskSpecial.Right => "→",
                OskSpecial.Down => "↓",
                OskSpecial.Esc => S.Osk_Key_Esc,
                OskSpecial.Delete => S.Osk_Key_Del,
                OskSpecial.Home => S.Osk_Key_Home,
                OskSpecial.End => S.Osk_Key_End,
                OskSpecial.PageUp => S.Osk_Key_PgUp,
                OskSpecial.PageDown => S.Osk_Key_PgDn,
                OskSpecial.Insert => S.Osk_Key_Ins,
                OskSpecial.PrintScreen => S.Osk_Key_PrtSc,
                OskSpecial.Fn => "Fn",
                // The style key names the style it switches to.
                OskSpecial.Layout => OskEngine.NextStyle(view.Style) switch
                {
                    OskStyle.Split => S.Osk_Style_Split,
                    OskStyle.Wheel => S.Osk_Style_Wheel,
                    _ => S.Osk_Style_Grid,
                },
                _ => null,
            };
        }

        // ── Hints ───────────────────────────────────────────────

        /// <summary>The hint bar: each control the layout uses, with the input
        /// the menu gives it. A control with no input is left out.</summary>
        private void BuildHints(OskView view, Func<string, string, string> describe)
        {
            var S = Strings.Instance;
            var items = new List<(KeyboardControlAction[] Actions, string Text, bool Chord)>();
            switch (view.Style)
            {
                case OskStyle.Split:
                    items.Add((new[] { KeyboardControlAction.TypeLeftHalf }, S.Osk_Hint_TypeLeft, false));
                    items.Add((new[] { KeyboardControlAction.TypeRightHalf }, S.Osk_Hint_TypeRight, false));
                    items.Add((new[] { KeyboardControlAction.Backspace }, S.Osk_Hint_Backspace, false));
                    items.Add((new[] { KeyboardControlAction.Space }, S.Osk_Hint_Space, false));
                    items.Add((new[] { KeyboardControlAction.ShiftNextKey, KeyboardControlAction.ShiftRightHand }, S.Osk_Hint_Shift, false));
                    items.Add((new[] { KeyboardControlAction.SymbolsPage }, S.Osk_Hint_Symbols, false));
                    items.Add((new[] { KeyboardControlAction.Enter }, S.Osk_Hint_Enter, false));
                    items.Add((new[] { KeyboardControlAction.Close }, S.Osk_Hint_Close, false));
                    break;
                case OskStyle.Wheel:
                    items.Add((new[] { KeyboardControlAction.PickWest, KeyboardControlAction.PickNorth, KeyboardControlAction.PickEast, KeyboardControlAction.PickSouth }, S.Osk_Hint_Type, false));
                    items.Add((new[] { KeyboardControlAction.Backspace }, S.Osk_Hint_Backspace, false));
                    items.Add((new[] { KeyboardControlAction.Space }, S.Osk_Hint_Space, false));
                    items.Add((new[] { KeyboardControlAction.Capitals }, S.Osk_Hint_Capitals, false));
                    items.Add((new[] { KeyboardControlAction.Numbers }, S.Osk_Hint_Numbers, false));
                    items.Add((new[] { KeyboardControlAction.Capitals, KeyboardControlAction.Numbers }, S.Osk_Hint_Keys, true));
                    items.Add((new[] { KeyboardControlAction.Enter }, S.Osk_Hint_Enter, false));
                    items.Add((new[] { KeyboardControlAction.Close }, S.Osk_Hint_Close, false));
                    break;
                default:
                    items.Add((new[] { KeyboardControlAction.TypeKey }, S.Osk_Hint_Type, false));
                    items.Add((new[] { KeyboardControlAction.Backspace }, S.Osk_Hint_Backspace, false));
                    items.Add((new[] { KeyboardControlAction.Space }, S.Osk_Hint_Space, false));
                    items.Add((new[] { KeyboardControlAction.Enter }, S.Osk_Hint_Enter, false));
                    items.Add((new[] { KeyboardControlAction.CursorLeft, KeyboardControlAction.CursorRight }, S.Osk_Hint_Cursor, false));
                    items.Add((new[] { KeyboardControlAction.ShiftNextKey }, S.Osk_Hint_Shift, false));
                    items.Add((new[] { KeyboardControlAction.SymbolsPage }, S.Osk_Hint_Symbols, false));
                    items.Add((new[] { KeyboardControlAction.Suggestion1, KeyboardControlAction.Suggestion2, KeyboardControlAction.Suggestion3 }, S.Osk_Hint_Suggestion, false));
                    items.Add((new[] { KeyboardControlAction.Close }, S.Osk_Hint_Close, false));
                    break;
            }
            items.Add((new[] { KeyboardControlAction.MoveKeyboard }, S.Osk_Hint_Move, false));
            var bindings = view.Bindings;
            var resolved = new List<(KeyboardInput[] Inputs, string Text, bool Chord)>();
            foreach (var it in items)
            {
                var inputs = new List<KeyboardInput>();
                foreach (var a in it.Actions)
                    if (bindings.TryGetValue(a, out var input) && !input.IsEmpty && !inputs.Exists(x => x.SameAs(input)))
                        inputs.Add(input);
                // A chord shows only when every part of it has an input.
                if (inputs.Count == 0 || (it.Chord && inputs.Count < it.Actions.Length)) continue;
                resolved.Add((inputs.ToArray(), it.Text, it.Chord));
            }
            var key = new System.Text.StringBuilder();
            key.Append(view.Style).Append('|').Append(view.Family).Append('|').Append(_theme.Dark);
            foreach (var r in resolved)
            {
                key.Append('|').Append(r.Text);
                foreach (var i in r.Inputs)
                    key.Append('/').Append(i.Descriptor).Append('@').Append(i.DeviceGuid)
                       .Append('+').Append(i.AndDescriptor).Append('@').Append(i.AndDeviceGuid);
            }
            string k = key.ToString();
            if (k == _hintsKey) return;
            _hintsKey = k;
            Hints.Children.Clear();
            foreach (var it in resolved)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 12, 2) };
                for (int i = 0; i < it.Inputs.Length; i++)
                {
                    if (i > 0 && it.Chord) row.Children.Add(Plus());
                    AddInput(row, it.Inputs[i], view.Family, describe);
                }
                row.Children.Add(new TextBlock { Text = it.Text, Foreground = _muted, FontSize = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                Hints.Children.Add(row);
            }
        }

        private TextBlock Plus() => new()
        {
            Text = "+",
            Foreground = _muted,
            FontSize = 11,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>One control's input on the hint bar. A combination shows
        /// both parts with a plus between them.</summary>
        private void AddInput(StackPanel row, KeyboardInput input, OskFamily family, Func<string, string, string> describe)
        {
            row.Children.Add(Glyph(input.Descriptor, input.DeviceGuid, family, describe));
            if (!input.HasAnd) return;
            row.Children.Add(Plus());
            row.Children.Add(Glyph(input.AndDescriptor, input.AndDeviceGuid, family, describe));
        }

        /// <summary>An input's glyph. A device-free gamepad name shows the
        /// controller's own label for that position, and an axis half adds its
        /// direction, in the SDL frame with +Y down. Any other input shows the
        /// name the slot's input lists give it.</summary>
        private Border Glyph(string descriptor, string deviceGuid, OskFamily family, Func<string, string, string> describe)
        {
            string clean = SourceCoercion.StripLegacyPrefix((descriptor ?? "").Trim(), out bool invert, out bool half);
            var input = InputOf(clean);
            if (input != OskInput.None && string.IsNullOrEmpty(deviceGuid))
            {
                string arrow = "";
                if ((input == OskInput.LeftStick || input == OskInput.RightStick) && half)
                    arrow = clean.EndsWith("X", StringComparison.Ordinal) ? (invert ? "←" : "→") : (invert ? "↑" : "↓");
                return Glyph(input, family, arrow);
            }
            string text = describe?.Invoke(descriptor, deviceGuid);
            return Pill(string.IsNullOrWhiteSpace(text) ? clean : text);
        }

        /// <summary>The gamepad position a device-free "Gamepad ..." name
        /// stands for (SourceCoercion's alias table), or None.</summary>
        internal static OskInput InputOf(string alias) => alias switch
        {
            "Gamepad ButtonA" => OskInput.South,
            "Gamepad ButtonB" => OskInput.East,
            "Gamepad ButtonX" => OskInput.West,
            "Gamepad ButtonY" => OskInput.North,
            "Gamepad LeftShoulder" => OskInput.LeftShoulder,
            "Gamepad RightShoulder" => OskInput.RightShoulder,
            "Gamepad LeftTrigger" => OskInput.LeftTrigger,
            "Gamepad RightTrigger" => OskInput.RightTrigger,
            "Gamepad ButtonBack" => OskInput.View,
            "Gamepad ButtonStart" => OskInput.Menu,
            "Gamepad LeftStick" => OskInput.LeftStickClick,
            "Gamepad RightStick" => OskInput.RightStickClick,
            "Gamepad DPadUp" => OskInput.DpadUp,
            "Gamepad DPadDown" => OskInput.DpadDown,
            "Gamepad DPadLeft" => OskInput.DpadLeft,
            "Gamepad DPadRight" => OskInput.DpadRight,
            "Gamepad LeftStickX" or "Gamepad LeftStickY" => OskInput.LeftStick,
            "Gamepad RightStickX" or "Gamepad RightStickY" => OskInput.RightStick,
            _ => OskInput.None,
        };

        private Border Pill(string text) => new()
        {
            MinWidth = 22,
            Height = 22,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(11),
            Background = _fill,
            BorderBrush = _stroke,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(1, 0, 1, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        private Border Glyph(OskInput input, OskFamily family, string suffix = "")
        {
            string text = GlyphText(input, family) + suffix;
            var face = FaceBrush(family, input);
            bool round = suffix.Length == 0
                && (input == OskInput.South || input == OskInput.East || input == OskInput.West || input == OskInput.North);
            bool filled = face != null && family == OskFamily.Xbox;
            return new Border
            {
                MinWidth = 22,
                Height = 22,
                Padding = new Thickness(round ? 0 : 6, 0, round ? 0 : 6, 0),
                CornerRadius = new CornerRadius(11),
                Background = filled ? face : _fill,
                BorderBrush = _stroke,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1, 0, 1, 0),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = filled ? Frozen(Color.FromRgb(0x10, 0x12, 0x14)) : face ?? _text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        /// <summary>A button's name in the family's own labels. A face
        /// button follows SDL's positions and labels: south is A on Xbox and
        /// GameCube, Cross on PlayStation and B on Nintendo, and GameCube's X
        /// sits east. A GameCube pad's triggers are L and R, Z is its right
        /// shoulder, and the Switch 2 one adds ZL on the left, as the SDL
        /// fork's GameCube mappings place them.</summary>
        internal static string GlyphText(OskInput input, OskFamily f)
        {
            bool ps = f == OskFamily.PlayStation, nin = f == OskFamily.Nintendo, gc = f == OskFamily.GameCube;
            return input switch
            {
                OskInput.South => ps ? "✕" : nin ? "B" : "A",
                OskInput.East => ps ? "○" : nin ? "A" : gc ? "X" : "B",
                OskInput.West => ps ? "□" : nin ? "Y" : gc ? "B" : "X",
                OskInput.North => ps ? "△" : nin ? "X" : "Y",
                OskInput.LeftShoulder => ps ? "L1" : nin ? "L" : gc ? "ZL" : "LB",
                OskInput.RightShoulder => ps ? "R1" : nin ? "R" : gc ? "Z" : "RB",
                OskInput.LeftTrigger => ps ? "L2" : nin ? "ZL" : gc ? "L" : "LT",
                OskInput.RightTrigger => ps ? "R2" : nin ? "ZR" : gc ? "R" : "RT",
                OskInput.View => ps ? "Create" : nin ? "−" : "View",
                OskInput.Menu => ps ? "Options" : nin ? "+" : gc ? "Start" : "Menu",
                OskInput.LeftStickClick => ps ? "L3" : "LS",
                OskInput.RightStickClick => ps ? "R3" : "RS",
                OskInput.DpadUp => "↑",
                OskInput.DpadDown => "↓",
                OskInput.DpadLeft => "←",
                OskInput.DpadRight => "→",
                OskInput.LeftStick => ps ? "L" : "LS",
                OskInput.RightStick => ps ? "R" : gc ? "C" : "RS",
                OskInput.Dpad => Strings.Instance.Osk_Input_Dpad,
                _ => "",
            };
        }

        private static Brush FaceBrush(OskFamily f, OskInput input)
        {
            if (f == OskFamily.Nintendo) return null;
            // GameCube: the green A and the red B. X and Y are gray.
            if (f == OskFamily.GameCube)
                return input switch
                {
                    OskInput.South => Frozen(Color.FromRgb(0x4B, 0xD0, 0x5F)),
                    OskInput.West => Frozen(Color.FromRgb(0xE8, 0x47, 0x4F)),
                    _ => null,
                };
            if (f == OskFamily.PlayStation)
                return input switch
                {
                    OskInput.South => Frozen(Color.FromRgb(0x86, 0xAF, 0xFF)),
                    OskInput.East => Frozen(Color.FromRgb(0xFF, 0x73, 0x73)),
                    OskInput.West => Frozen(Color.FromRgb(0xE8, 0x89, 0xC7)),
                    OskInput.North => Frozen(Color.FromRgb(0x4F, 0xD1, 0xA5)),
                    _ => null,
                };
            return input switch
            {
                OskInput.South => Frozen(Color.FromRgb(0x4B, 0xD0, 0x5F)),
                OskInput.East => Frozen(Color.FromRgb(0xE8, 0x47, 0x4F)),
                OskInput.West => Frozen(Color.FromRgb(0x4D, 0x86, 0xE0)),
                OskInput.North => Frozen(Color.FromRgb(0xE8, 0xB8, 0x4D)),
                _ => null,
            };
        }

        // ── Place ───────────────────────────────────────────────

        private bool _dragging;
        private Point _grab;

        /// <summary>Puts the window's center at the menu's Screen Position, in
        /// percent of the work area, where MenuOverlayWindow puts a menu, and
        /// keeps it a margin inside the work area. A keyboard larger than the
        /// work area shrinks to fit, so its close button stays on screen.</summary>
        private void Place(MenuDefinitionEntry menu)
        {
            if (_dragging || _resizing) return;
            var wa = SystemParameters.WorkArea;
            UpdateLayout();
            double w = ActualWidth, h = ActualHeight;
            double room = Math.Min((wa.Width - 2 * EdgeMargin) / Math.Max(1, w), (wa.Height - 2 * EdgeMargin) / Math.Max(1, h));
            if (room > 0 && room < 1)
            {
                double fit = Math.Max(MinScalePercent / 100.0, Scale.ScaleX * room);
                Scale.ScaleX = Scale.ScaleY = fit;
                UpdateLayout();
                w = ActualWidth;
                h = ActualHeight;
            }
            var key = (menu.PosXPercent, menu.PosYPercent, w, h, wa);
            if (key == _placed) return;
            _placed = key;
            double cx = wa.Left + wa.Width * Math.Clamp(menu.PosXPercent, 0, 100) / 100.0;
            double cy = wa.Top + wa.Height * Math.Clamp(menu.PosYPercent, 0, 100) / 100.0;
            double minL = wa.Left + EdgeMargin, minT = wa.Top + EdgeMargin;
            Left = Math.Clamp(cx - w / 2, minL, Math.Max(minL, wa.Right - EdgeMargin - w));
            Top = Math.Clamp(cy - h / 2, minT, Math.Max(minT, wa.Bottom - EdgeMargin - h));
        }

        /// <summary>A center in screen DIPs as the Screen Position percents of
        /// the work area, the inverse of <see cref="Place"/>.</summary>
        internal static (int X, int Y) PercentOf(double cx, double cy)
        {
            var wa = SystemParameters.WorkArea;
            int x = (int)Math.Round(Math.Clamp((cx - wa.Left) / Math.Max(1, wa.Width) * 100, 0, 100));
            int y = (int)Math.Round(Math.Clamp((cy - wa.Top) / Math.Max(1, wa.Height) * 100, 0, 100));
            return (x, y);
        }

        /// <summary>The window's center in screen DIPs, for a controller's
        /// hold-to-move to start from where it is.</summary>
        internal (double X, double Y) Center() => (Left + ActualWidth / 2, Top + ActualHeight / 2);

        private void DragStart(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _grab = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            Left += p.X - _grab.X;
            Top += p.Y - _grab.Y;
        }

        private void DragEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            _placed = default;
            var (cx, cy) = Center();
            _moved(cx, cy);
        }

        // ── Size (the grip in the bottom-right corner) ─────────
        // The Screen Overlay's grip (ScreenOverlay.xaml.cs), scaling the
        // keyboard where that one sizes the window.

        private bool _resizing;
        private Point _resizeStart;
        private double _resizeStartScale, _resizeStartWidth;

        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _resizing = true;
            _resizeStart = PointToScreen(e.GetPosition(this));
            _resizeStartScale = Scale.ScaleX;
            _resizeStartWidth = ActualWidth;
            ResizeGrip.CaptureMouse();
            e.Handled = true;
        }

        private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing || _resizeStartWidth <= 0) return;
            var current = PointToScreen(e.GetPosition(this));
            // Same px to DIP conversion as the Screen Overlay's grip.
            var dpi = VisualTreeHelper.GetDpi(this);
            double width = Math.Max(1, _resizeStartWidth + (current.X - _resizeStart.X) / dpi.DpiScaleX);
            double scale = Math.Clamp(_resizeStartScale * width / _resizeStartWidth,
                MinScalePercent / 100.0, MaxScalePercent / 100.0);
            Scale.ScaleX = Scale.ScaleY = scale;
        }

        private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizing) return;
            EndResize();
            ResizeGrip.ReleaseMouseCapture();
        }

        private void ResizeGrip_LostMouseCapture(object sender, MouseEventArgs e) => EndResize();

        private void EndResize()
        {
            if (!_resizing) return;
            _resizing = false;
            _placed = default;
            _resized((int)Math.Round(Scale.ScaleX * 100));
        }
    }
}
