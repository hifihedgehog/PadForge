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
    /// MIDI preview showing a piano keyboard for notes and vertical sliders for CCs.
    /// Dynamically generated from MidiSlotConfig (StartNote, NoteCount, StartCc, CcCount).
    /// </summary>
    public partial class MidiPreviewView : UserControl
    {
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _vm;
        private bool _dirty;
        private bool _layoutBuilt;

        // Colors
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush AccentDimBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x50, 0x90));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
        private static readonly Brush WhiteKeyBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly Brush WhiteKeyPressedBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xA0, 0xE0));
        private static readonly Brush BlackKeyBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        private static readonly Brush BlackKeyPressedBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x60, 0xB0));
        private static readonly Brush KeyBorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xA0, 0xE0));
        private static readonly Brush FlashBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));

        // Layout constants
        private const double WhiteKeyWidth = 28;
        private const double WhiteKeyHeight = 120;
        private const double BlackKeyWidth = 18;
        private const double BlackKeyHeight = 75;
        private const double CcBarWidth = 20;
        private const double CcBarHeight = 100;
        private const double SectionGap = 20;
        private const double LabelHeight = 16;
        private const double Padding = 12;

        // Note layout: which notes in an octave are white keys
        // 0=C, 1=C#, 2=D, 3=D#, 4=E, 5=F, 6=F#, 7=G, 8=G#, 9=A, 10=A#, 11=B
        private static readonly bool[] IsBlackKey = { false, true, false, true, false, false, true, false, true, false, true, false };
        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        // Black key X offsets relative to preceding white key (as fraction of white key width)
        // Each black key sits between two white keys, slightly offset
        private static readonly double[] BlackKeyOffsets = { 0.65, 0.65, 0, 0.65, 0.65, 0.65 };
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

            if (_vm == null || _vm.OutputType != VirtualControllerType.Midi) return;
            var mc = _vm.MidiConfig;

            double x = Padding;
            double topY = Padding;

            // ── CC Sliders section ──
            if (mc.CcCount > 0)
            {
                var ccLabel = CreateLabel("CC Outputs", x, topY);
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
                double pianoX = Padding;
                var pianoLabel = CreateLabel("Note Outputs", pianoX, topY);
                MidiCanvas.Children.Add(pianoLabel);
                topY += LabelHeight + 4;

                var noteNumbers = mc.GetNoteNumbers();
                BuildPianoKeys(noteNumbers, pianoX, topY);

                // Calculate piano width for canvas sizing
                int whiteCount = 0;
                for (int i = 0; i < noteNumbers.Length; i++)
                    if (!IsBlackKey[noteNumbers[i] % 12]) whiteCount++;
                double pianoWidth = whiteCount * WhiteKeyWidth;
                x = Math.Max(x, pianoX + pianoWidth + Padding);
                topY += WhiteKeyHeight + LabelHeight + 4;
            }

            MidiCanvas.Width = x + Padding;
            MidiCanvas.Height = topY + Padding;
            _layoutBuilt = true;
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  CC Slider widget
        // ─────────────────────────────────────────────

        private CcSliderWidget CreateCcSlider(int index, int ccNumber, double x, double y)
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
                Cursor = Cursors.Hand
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

            // Hover
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

            // Click-to-record
            bg.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, $"MidiCC{index}");
            };

            return new CcSliderWidget
            {
                CcIndex = index,
                Background = bg,
                Fill = fill,
                X = x,
                Y = y
            };
        }

        // ─────────────────────────────────────────────
        //  Piano keyboard
        // ─────────────────────────────────────────────

        private void BuildPianoKeys(int[] noteNumbers, double startX, double y)
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
                    WhiteKeyBrush, WhiteKeyPressedBrush, false);
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
                    BlackKeyBrush, BlackKeyPressedBrush, true);
                _keyWidgets.Add(key);
            }
        }

        private PianoKeyWidget CreatePianoKey(int noteIndex, int midiNote, double x, double y,
            double width, double height, Brush normalBrush, Brush pressedBrush, bool isBlack)
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
                Cursor = Cursors.Hand
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

            // Hover
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

            // Click-to-record
            rect.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, $"MidiNote{noteIndex}");
                e.Handled = true;
            };

            return new PianoKeyWidget
            {
                NoteIndex = noteIndex,
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
                Interval = TimeSpan.FromMilliseconds(170)
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
            if (!_dirty || _vm == null || !_layoutBuilt) return;
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

            // Update piano keys
            foreach (var w in _keyWidgets)
            {
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
            public Rectangle Background;
            public Rectangle Fill;
            public double X, Y;
        }

        private struct PianoKeyWidget
        {
            public int NoteIndex;
            public bool IsBlack;
            public Rectangle Rect;
            public Brush NormalBrush;
            public Brush PressedBrush;
        }
    }
}
