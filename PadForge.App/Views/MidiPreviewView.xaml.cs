using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// MIDI preview showing a piano keyboard for notes and vertical sliders for CCs.
    /// Dynamically generated from MidiSlotConfig (StartNote, NoteCount, StartCc, CcCount).
    /// </summary>
    public partial class MidiPreviewView : UserControl
    {
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _vm;

        // Input mode (issue #128): the same control reused on the Devices
        // page to visualize a MIDI INPUT device's live state. No PadViewModel,
        // no MidiConfig window, no click-to-record — the full 0-127 namespace
        // is laid out wrapped into octave rows and driven from a live
        // MidiInputState supplied by the caller each render frame.
        /// <summary>Which slice of the MIDI namespace an input-mode preview
        /// renders. Split so the Devices page can place normal-size section
        /// headers OUTSIDE the Viewbox (an in-canvas label would scale down
        /// with the keys and become unreadable).</summary>
        public enum InputSection { Notes, Ccs }

        private bool _inputMode;
        private InputSection _inputSection;
        private Func<MidiInputState> _inputSource;

        private bool _dirty;
        private bool _layoutBuilt;
        private Wpf.Ui.Appearance.ApplicationTheme? _lastTheme;

        // Colors — pre-cached dark/light variants (zero per-frame allocation)
        private static bool IsDarkTheme =>
            Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;

        private static SolidColorBrush F(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        // Ember (#175): output preview surface, pressed states light ember.
        private static readonly Brush AccentBrush = F(0xFF,0x6B,0x2C);
        private static readonly Brush _dimD = F(0x60,0x60,0x60), _dimL = F(0xA0,0xA0,0xA0);
        private static readonly Brush _bgD = F(0x2D,0x2D,0x2D), _bgL = F(0xE0,0xE0,0xE0);
        private static readonly Brush _lblD = F(0xBB,0xBB,0xBB), _lblL = F(0x50,0x50,0x50);
        private static readonly Brush _wkD = F(0xF0,0xF0,0xF0), _wkL = F(0xFF,0xFF,0xFF);
        private static readonly Brush _bkD = F(0x20,0x20,0x20), _bkL = F(0x40,0x40,0x40);
        private static readonly Brush _kbD = F(0x40,0x40,0x40), _kbL = F(0xB0,0xB0,0xB0);

        private static Brush DimBrush => IsDarkTheme ? _dimD : _dimL;
        private static Brush BgBrush => IsDarkTheme ? _bgD : _bgL;
        private static Brush LabelBrush => IsDarkTheme ? _lblD : _lblL;
        private static Brush WhiteKeyBrush => IsDarkTheme ? _wkD : _wkL;
        private static readonly Brush WhiteKeyPressedBrush = F(0xFF,0xA2,0x4D);
        private static Brush BlackKeyBrush => IsDarkTheme ? _bkD : _bkL;
        private static readonly Brush BlackKeyPressedBrush = F(0xC4,0x3D,0x0C);
        private static Brush KeyBorderBrush => IsDarkTheme ? _kbD : _kbL;
        private static readonly Brush HoverBrush = F(0xFF,0xA2,0x4D);
        private static readonly Brush FlashBrush = F(0xFF,0xA5,0x00);
        // Relative-encoder pulse flash (input mode): the whole CC bar lights
        // green on an up detent, orange on a down detent (issue #128).
        private static readonly Brush CcUpPulseBrush = F(0x33,0xC0,0x55);
        private static readonly Brush CcDownPulseBrush = F(0xE0,0x88,0x2A);

        // Pulse latch (input mode): a detent's button pulse is only ~24 ms,
        // too brief to see, so the preview holds the flash this long.
        private const long PulseLatchMs = 180;
        private readonly long[] _ccUpLitUntil = new long[MidiInputState.CcCount];
        private readonly long[] _ccDownLitUntil = new long[MidiInputState.CcCount];

        // Layout constants
        private const double WhiteKeyWidth = 28;
        private const double WhiteKeyHeight = 120;
        private const double BlackKeyWidth = 18;
        private const double BlackKeyHeight = 75;
        private const double CcBarWidth = 20;
        private const double CcBarHeight = 100;
        private const double SectionGap = 20;
        private const double LabelHeight = 16;
        private const double LayoutPadding = 12;

        // Note layout: which notes in an octave are white keys
        // 0=C, 1=C#, 2=D, 3=D#, 4=E, 5=F, 6=F#, 7=G, 8=G#, 9=A, 10=A#, 11=B
        private static readonly bool[] IsBlackKey = { false, true, false, true, false, false, true, false, true, false, true, false };
        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        // Which chromatic positions are black: 1, 3, 6, 8, 10

        // Widget tracking
        private readonly List<PianoKeyWidget> _keyWidgets = new();
        private readonly List<CcSliderWidget> _ccWidgets = new();

        // Flash state
        private System.Windows.Threading.DispatcherTimer _flashTimer;
        private string _flashTarget;
        private bool _flashOn;

        public MidiPreviewView()
        {
            InitializeComponent();
            CompositionTarget.Rendering += OnRendering;
        }

        public void Bind(PadViewModel vm)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.MidiConfig.PropertyChanged -= OnMidiConfigPropertyChanged;
            }

            _vm = vm;

            if (_vm != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                _vm.PropertyChanged += OnVmPropertyChanged;
                _vm.MidiConfig.PropertyChanged += OnMidiConfigPropertyChanged;
                RebuildLayout();
            }
        }

        public void Unbind()
        {
            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.MidiConfig.PropertyChanged -= OnMidiConfigPropertyChanged;
            }
            _vm = null;
            _layoutBuilt = false;
        }

        /// <summary>Drives the preview from a live MIDI INPUT state (full
        /// 0-127 namespace, wrapped). Renders one <paramref name="section"/>
        /// (notes or CCs) with no in-canvas title. <paramref name="source"/>
        /// is polled each render frame and may return null until the first
        /// message.</summary>
        public void BindInput(Func<MidiInputState> source, InputSection section)
        {
            Unbind();
            _inputMode = true;
            _inputSection = section;
            _inputSource = source;
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
            RebuildLayout();
        }

        public void UnbindInput()
        {
            CompositionTarget.Rendering -= OnRendering;
            _inputMode = false;
            _inputSource = null;
            _layoutBuilt = false;
            MidiCanvas.Children.Clear();
            _keyWidgets.Clear();
            _ccWidgets.Clear();
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.MidiOutputSnapshot))
            {
                _dirty = true;
                return;
            }

            if (e.PropertyName == nameof(PadViewModel.OutputType))
            {
                Dispatcher.Invoke(RebuildLayout);
                return;
            }

            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget))
            {
                Dispatcher.Invoke(() => UpdateFlashTarget(_vm?.CurrentRecordingTarget));
                return;
            }
        }

        private void OnMidiConfigPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(RebuildLayout);
        }

        // ─────────────────────────────────────────────
        //  Layout construction
        // ─────────────────────────────────────────────

        private void RebuildLayout()
        {
            MidiCanvas.Children.Clear();
            _keyWidgets.Clear();
            _ccWidgets.Clear();
            _layoutBuilt = false;

            if (_inputMode)
            {
                BuildInputLayout();
                return;
            }

            if (_vm == null || _vm.OutputType != VirtualControllerType.Midi) return;
            var mc = _vm.MidiConfig;

            double x = LayoutPadding;
            double topY = LayoutPadding;

            // ── CC Sliders section ──
            if (mc.CcCount > 0)
            {
                var ccLabel = CreateLabel(Strings.Instance.Preview_CCOutputs, x, topY);
                MidiCanvas.Children.Add(ccLabel);
                topY += LabelHeight + 4;

                var ccNumbers = mc.GetCcNumbers();
                for (int i = 0; i < mc.CcCount; i++)
                {
                    var w = CreateCcSlider(i, ccNumbers[i], x, topY);
                    _ccWidgets.Add(w);
                    x += CcBarWidth + 6;
                }

                topY += CcBarHeight + LabelHeight + SectionGap;
            }

            // ── Piano Keyboard section ──
            if (mc.NoteCount > 0)
            {
                double pianoX = LayoutPadding;
                var pianoLabel = CreateLabel(Strings.Instance.Preview_NoteOutputs, pianoX, topY);
                MidiCanvas.Children.Add(pianoLabel);
                topY += LabelHeight + 4;

                var noteNumbers = mc.GetNoteNumbers();
                BuildPianoKeys(noteNumbers, pianoX, topY);

                // Calculate piano width for canvas sizing
                int whiteCount = 0;
                for (int i = 0; i < noteNumbers.Length; i++)
                    if (!IsBlackKey[noteNumbers[i] % 12]) whiteCount++;
                double pianoWidth = whiteCount * WhiteKeyWidth;
                x = Math.Max(x, pianoX + pianoWidth + LayoutPadding);
                topY += WhiteKeyHeight + LabelHeight + 4;
            }

            MidiCanvas.Width = x + LayoutPadding;
            MidiCanvas.Height = topY + LayoutPadding;
            _layoutBuilt = true;
            _lastTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Input-mode layout — full 0-127 namespace, wrapped
        // ─────────────────────────────────────────────

        private const int CcPerRow = 32;     // 128 CCs -> 4 rows
        private const int OctavesPerRow = 4;  // 11 octaves -> 3 rows

        private void BuildInputLayout()
        {
            double topY = LayoutPadding;
            double maxRight = 0;

            // Drop any stale encoder-pulse flashes from a previous binding.
            Array.Clear(_ccUpLitUntil, 0, _ccUpLitUntil.Length);
            Array.Clear(_ccDownLitUntil, 0, _ccDownLitUntil.Length);

            // No in-canvas section title — the Devices page draws a normal-
            // size header outside the Viewbox so it stays readable.
            if (_inputSection == InputSection.Ccs)
            {
                // ── CC sliders (all 128, wrapped) ──
                double ccRowH = CcBarHeight + LabelHeight + 8;
                for (int cc = 0; cc < 128; cc++)
                {
                    int row = cc / CcPerRow, col = cc % CcPerRow;
                    double cx = LayoutPadding + col * (CcBarWidth + 6);
                    double cy = topY + row * ccRowH;
                    _ccWidgets.Add(CreateCcSlider(cc, cc, cx, cy, inputMode: true));
                    maxRight = Math.Max(maxRight, cx + CcBarWidth);
                }
                int ccRows = (128 + CcPerRow - 1) / CcPerRow;
                topY += ccRows * ccRowH;
            }
            else
            {
                // ── Piano keyboard (all 11 octaves, wrapped by octave) ──
                double octaveWidth = 7 * WhiteKeyWidth;
                double pianoRowH = WhiteKeyHeight + LabelHeight + 8;
                int totalOctaves = (127 / 12) + 1; // 11 (octaves 0..10)
                for (int oct = 0; oct < totalOctaves; oct++)
                {
                    int row = oct / OctavesPerRow, colOct = oct % OctavesPerRow;
                    double rowX = LayoutPadding + colOct * octaveWidth;
                    double rowY = topY + row * pianoRowH;

                    int first = oct * 12;
                    int last = Math.Min(first + 11, 127);
                    var octNotes = new int[last - first + 1];
                    for (int n = first; n <= last; n++) octNotes[n - first] = n;

                    BuildPianoKeys(octNotes, rowX, rowY, inputMode: true);
                    maxRight = Math.Max(maxRight, rowX + octaveWidth);
                }
                int pianoRows = (totalOctaves + OctavesPerRow - 1) / OctavesPerRow;
                topY += pianoRows * pianoRowH;
            }

            MidiCanvas.Width = maxRight + LayoutPadding;
            MidiCanvas.Height = topY + LayoutPadding;
            _layoutBuilt = true;
            _lastTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  CC Slider widget
        // ─────────────────────────────────────────────

        private CcSliderWidget CreateCcSlider(int index, int ccNumber, double x, double y, bool inputMode = false)
        {
            // Background bar
            var bg = new Rectangle
            {
                Width = CcBarWidth,
                Height = CcBarHeight,
                Fill = BgBrush,
                Stroke = DimBrush,
                StrokeThickness = 1,
                RadiusX = 3, RadiusY = 3,
                Cursor = inputMode ? Cursors.Arrow : Cursors.Hand
            };
            Canvas.SetLeft(bg, x);
            Canvas.SetTop(bg, y);
            MidiCanvas.Children.Add(bg);

            // Fill bar (grows from bottom)
            var fill = new Rectangle
            {
                Width = CcBarWidth - 4,
                Height = 0,
                Fill = AccentBrush,
                RadiusX = 2, RadiusY = 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(fill, x + 2);
            Canvas.SetTop(fill, y + CcBarHeight - 2);
            MidiCanvas.Children.Add(fill);

            // CC number label below
            var label = CreateLabel($"{ccNumber}", x, y + CcBarHeight + 2);
            label.FontSize = 9;
            label.Width = CcBarWidth;
            label.TextAlignment = TextAlignment.Center;
            MidiCanvas.Children.Add(label);

            // Hover + click-to-record are output-mode affordances only.
            if (!inputMode)
            {
                bg.MouseEnter += (s, e) =>
                {
                    if (_flashTarget != null) return;
                    bg.Stroke = HoverBrush;
                    bg.StrokeThickness = 2;
                };
                bg.MouseLeave += (s, e) =>
                {
                    if (_flashTarget != null) return;
                    bg.Stroke = DimBrush;
                    bg.StrokeThickness = 1;
                };
                bg.MouseLeftButtonDown += (s, e) =>
                {
                    ControllerElementRecordRequested?.Invoke(this, $"MidiCC{index}");
                };
            }

            return new CcSliderWidget
            {
                CcIndex = index,
                CcNumber = ccNumber,
                Background = bg,
                Fill = fill,
                X = x,
                Y = y
            };
        }

        // ─────────────────────────────────────────────
        //  Piano keyboard
        // ─────────────────────────────────────────────

        private void BuildPianoKeys(int[] noteNumbers, double startX, double y, bool inputMode = false)
        {
            // First pass: identify which notes are white and black
            var whiteNotes = new List<int>(); // indices into noteNumbers
            var blackNotes = new List<int>();

            for (int i = 0; i < noteNumbers.Length; i++)
            {
                if (IsBlackKey[noteNumbers[i] % 12])
                    blackNotes.Add(i);
                else
                    whiteNotes.Add(i);
            }

            // Place white keys first (they go underneath)
            double wx = startX;
            var whiteKeyPositions = new Dictionary<int, double>(); // noteNumber -> x position
            foreach (int idx in whiteNotes)
            {
                int note = noteNumbers[idx];
                var key = CreatePianoKey(idx, note, wx, y, WhiteKeyWidth, WhiteKeyHeight,
                    WhiteKeyBrush, WhiteKeyPressedBrush, false, inputMode);
                _keyWidgets.Add(key);
                whiteKeyPositions[note] = wx;
                wx += WhiteKeyWidth;
            }

            // Place black keys on top (between white keys)
            foreach (int idx in blackNotes)
            {
                int note = noteNumbers[idx];
                int noteInOctave = note % 12;

                // Find the white key just before this black key
                int prevWhite = note - 1;
                while (prevWhite >= 0 && IsBlackKey[prevWhite % 12]) prevWhite--;

                double bx;
                if (whiteKeyPositions.TryGetValue(prevWhite, out double prevX))
                {
                    bx = prevX + WhiteKeyWidth - BlackKeyWidth / 2;
                }
                else
                {
                    // Edge case: no preceding white key in our range
                    // Find next white key and place before it
                    int nextWhite = note + 1;
                    while (nextWhite < 128 && IsBlackKey[nextWhite % 12]) nextWhite++;
                    if (whiteKeyPositions.TryGetValue(nextWhite, out double nextX))
                        bx = nextX - BlackKeyWidth / 2;
                    else
                        continue; // Can't place this black key
                }

                var key = CreatePianoKey(idx, note, bx, y, BlackKeyWidth, BlackKeyHeight,
                    BlackKeyBrush, BlackKeyPressedBrush, true, inputMode);
                _keyWidgets.Add(key);
            }
        }

        private PianoKeyWidget CreatePianoKey(int noteIndex, int midiNote, double x, double y,
            double width, double height, Brush normalBrush, Brush pressedBrush, bool isBlack, bool inputMode = false)
        {
            var rect = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = normalBrush,
                Stroke = KeyBorderBrush,
                StrokeThickness = 1,
                RadiusX = isBlack ? 2 : 3,
                RadiusY = isBlack ? 2 : 3,
                Cursor = inputMode ? Cursors.Arrow : Cursors.Hand
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            // Black keys need higher Z
            if (isBlack)
                Panel.SetZIndex(rect, 10);
            MidiCanvas.Children.Add(rect);

            // Note label at the bottom of white keys only
            TextBlock label = null;
            if (!isBlack)
            {
                int octave = (midiNote / 12) - 1;
                string name = NoteNames[midiNote % 12] + octave;
                label = new TextBlock
                {
                    Text = name,
                    FontSize = 8,
                    Foreground = DimBrush,
                    IsHitTestVisible = false,
                    TextAlignment = TextAlignment.Center,
                    Width = width
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, y + height + 2);
                MidiCanvas.Children.Add(label);
            }

            // Hover + click-to-record are output-mode affordances only.
            if (!inputMode)
            {
                rect.MouseEnter += (s, e) =>
                {
                    if (_flashTarget != null) return;
                    rect.Stroke = HoverBrush;
                    rect.StrokeThickness = 2;
                };
                rect.MouseLeave += (s, e) =>
                {
                    if (_flashTarget != null) return;
                    rect.Stroke = KeyBorderBrush;
                    rect.StrokeThickness = 1;
                };
                rect.MouseLeftButtonDown += (s, e) =>
                {
                    ControllerElementRecordRequested?.Invoke(this, $"MidiNote{noteIndex}");
                    e.Handled = true;
                };
            }

            return new PianoKeyWidget
            {
                NoteIndex = noteIndex,
                MidiNote = midiNote,
                IsBlack = isBlack,
                Rect = rect,
                NormalBrush = normalBrush,
                PressedBrush = pressedBrush
            };
        }

        // ─────────────────────────────────────────────
        //  Flash animation for recording target
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            if (_flashTimer != null)
            {
                _flashTimer.Stop();
                _flashTimer = null;
            }
            ApplyFlashState(false);
            _flashTarget = target;

            if (string.IsNullOrEmpty(target)) return;

            _flashOn = true;
            _flashTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _flashTimer.Tick += (s, e) =>
            {
                _flashOn = !_flashOn;
                ApplyFlashState(_flashOn);
            };
            _flashTimer.Start();
            ApplyFlashState(true);
        }

        private void ApplyFlashState(bool highlight)
        {
            if (string.IsNullOrEmpty(_flashTarget)) return;

            // Check CC sliders
            foreach (var w in _ccWidgets)
            {
                if (_flashTarget == $"MidiCC{w.CcIndex}" || _flashTarget == $"MidiCC{w.CcIndex}Neg")
                {
                    w.Fill.Fill = highlight ? FlashBrush : AccentBrush;
                    return;
                }
            }

            // Check piano keys
            foreach (var w in _keyWidgets)
            {
                if (_flashTarget == $"MidiNote{w.NoteIndex}")
                {
                    w.Rect.Fill = highlight ? FlashBrush : w.NormalBrush;
                    return;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Rendering
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            // Rebuild on theme change.
            var currentTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            if (_layoutBuilt && _lastTheme != currentTheme) RebuildLayout();

            if (!_layoutBuilt) return;

            // Input mode: poll the live MIDI input state every frame (notes
            // and CCs change continuously, so there's no dirty flag). Indexed
            // by actual note / CC number against the full 0-127 arrays.
            if (_inputMode)
            {
                // Skip the ~256-widget repaint when the Devices page is
                // collapsed — the control stays bound (it's a persistent
                // singleton), so without this the loop would run every frame
                // against a hidden canvas.
                if (!IsVisible) return;

                var midi = _inputSource?.Invoke();
                long now = Environment.TickCount64;
                foreach (var w in _ccWidgets)
                {
                    int cc = w.CcNumber;
                    double value = (midi?.Cc != null && cc < midi.Cc.Length)
                        ? midi.Cc[cc] / 127.0 : 0;
                    double fillH = Math.Clamp(value, 0, 1) * (CcBarHeight - 4);
                    w.Fill.Height = fillH;
                    Canvas.SetTop(w.Fill, w.Y + CcBarHeight - 2 - fillH);

                    // Relative-encoder pulses: latch the flash so a ~24 ms
                    // detent stays visible, then tint the WHOLE bar (an
                    // encoder's value sits near center, so a half-fill is
                    // meaningless — flood the cell). Green = clockwise,
                    // orange = counter-clockwise.
                    if (midi?.CcUp != null && cc < midi.CcUp.Length && midi.CcUp[cc])
                        { _ccUpLitUntil[cc] = now + PulseLatchMs; _ccDownLitUntil[cc] = 0; }
                    if (midi?.CcDown != null && cc < midi.CcDown.Length && midi.CcDown[cc])
                        { _ccDownLitUntil[cc] = now + PulseLatchMs; _ccUpLitUntil[cc] = 0; }
                    if (now < _ccUpLitUntil[cc])
                    {
                        w.Background.Fill = CcUpPulseBrush;
                        w.Fill.Fill = CcUpPulseBrush;
                    }
                    else if (now < _ccDownLitUntil[cc])
                    {
                        w.Background.Fill = CcDownPulseBrush;
                        w.Fill.Fill = CcDownPulseBrush;
                    }
                    else
                    {
                        w.Background.Fill = BgBrush;
                        w.Fill.Fill = AccentBrush;
                    }
                }
                foreach (var w in _keyWidgets)
                {
                    bool pressed = midi?.Notes != null && w.MidiNote < midi.Notes.Length && midi.Notes[w.MidiNote];
                    w.Rect.Fill = pressed ? w.PressedBrush : w.NormalBrush;
                }
                return;
            }

            if (!_dirty || _vm == null) return;
            _dirty = false;

            var raw = _vm.MidiOutputSnapshot;

            // Update CC sliders
            foreach (var w in _ccWidgets)
            {
                double value = 0;
                if (raw.CcValues != null && w.CcIndex < raw.CcValues.Length)
                    value = raw.CcValues[w.CcIndex] / 127.0;
                double fillH = Math.Clamp(value, 0, 1) * (CcBarHeight - 4);
                w.Fill.Height = fillH;
                Canvas.SetTop(w.Fill, w.Y + CcBarHeight - 2 - fillH);
            }

            // Update piano keys (skip the flashing key during recording).
            foreach (var w in _keyWidgets)
            {
                if (_flashTarget == $"MidiNote{w.NoteIndex}" && _flashOn)
                    continue; // Don't overwrite flash highlight

                bool pressed = raw.Notes != null && w.NoteIndex < raw.Notes.Length && raw.Notes[w.NoteIndex];
                w.Rect.Fill = pressed ? w.PressedBrush : w.NormalBrush;
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

        private struct CcSliderWidget
        {
            public int CcIndex;
            public int CcNumber;   // actual MIDI CC number (input mode indexes the live array by this)
            public Rectangle Background;
            public Rectangle Fill;
            public double X, Y;
        }

        private struct PianoKeyWidget
        {
            public int NoteIndex;
            public int MidiNote;   // actual MIDI note number (input mode indexes the live array by this)
            public bool IsBlack;
            public Rectangle Rect;
            public Brush NormalBrush;
            public Brush PressedBrush;
        }
    }
}
