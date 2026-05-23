using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine.Touchpad;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// Modal dialog that captures user gesture samples on a Canvas surface
    /// (WPF touch + stylus + mouse drag) and saves the result as a
    /// <see cref="TouchpadCustomGesture"/> in the active profile via
    /// <see cref="PadForge.Services.InputService.AddCustomTouchpadGesture"/>.
    ///
    /// Sampling rhythm: configurable 1 / 3 / 5 samples; the dialog requires
    /// every sample to use the same finger count (mismatch resets the
    /// stack). Multi-sample averaging happens at save time — each sample's
    /// paths are resampled + normalized via <see cref="PDollarRecognizer"/>,
    /// then averaged point-by-point and packed back into the canonical
    /// TouchpadCustomGesture shape.
    /// </summary>
    public partial class TouchpadGestureRecorderDialog : Window
    {
        // ─── Per-finger live state ──────────────────────

        private sealed class FingerTrack
        {
            public int ContactId;       // mouse=-1, stylus=-2, touch=TouchDevice.Id
            public Polyline Visual;
            public List<Vector2> Points = new();
            public List<long> Timestamps = new();
            public long StartedAtMs;
        }

        private readonly Dictionary<int, FingerTrack> _activeFingers = new();
        private readonly List<List<List<Vector2>>> _capturedSamples = new();
        private readonly List<List<List<long>>> _capturedTimestamps = new();
        private int _targetSampleCount = 3;
        private int _expectedFingerCount; // 0 until first sample lands
        private long _gestureStartMs;
        private bool _gestureActive;

        // Distinct colors per finger slot — 8 distinct hues; finger 6+
        // wraps. Matches the per-finger color convention used in the
        // Devices preview.
        private static readonly Brush[] _fingerBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0xF2, 0x7B, 0x35)),
            new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
            new SolidColorBrush(Color.FromRgb(0xBA, 0x68, 0xC8)),
            new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
            new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
        };

        public TouchpadGestureRecorderDialog()
        {
            InitializeComponent();
            UpdateUiText();
        }

        // ─── Sample-count UI ────────────────────────────

        private void SampleCountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SampleCountBox?.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Content?.ToString(), out int n))
            {
                _targetSampleCount = Math.Clamp(n, 1, 5);
                UpdateUiText();
            }
        }

        // ─── Drawing surface — touch ────────────────────

        private void DrawingCanvas_TouchDown(object sender, TouchEventArgs e)
        {
            var pt = e.GetTouchPoint(DrawingCanvas).Position;
            BeginFinger(e.TouchDevice.Id, pt);
            DrawingCanvas.CaptureTouch(e.TouchDevice);
            e.Handled = true;
        }

        private void DrawingCanvas_TouchMove(object sender, TouchEventArgs e)
        {
            var pt = e.GetTouchPoint(DrawingCanvas).Position;
            ContinueFinger(e.TouchDevice.Id, pt);
            e.Handled = true;
        }

        private void DrawingCanvas_TouchUp(object sender, TouchEventArgs e)
        {
            EndFinger(e.TouchDevice.Id);
            DrawingCanvas.ReleaseTouchCapture(e.TouchDevice);
            e.Handled = true;
        }

        // ─── Stylus (treat as one extra finger) ─────────

        private void DrawingCanvas_StylusDown(object sender, StylusDownEventArgs e)
        {
            var pt = e.GetPosition(DrawingCanvas);
            BeginFinger(-2, pt);
            e.Handled = true;
        }

        // ─── Mouse drag (single-finger fallback) ────────

        private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var pt = e.GetPosition(DrawingCanvas);
            BeginFinger(-1, pt);
            DrawingCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (!_activeFingers.ContainsKey(-1)) return;
            var pt = e.GetPosition(DrawingCanvas);
            ContinueFinger(-1, pt);
            e.Handled = true;
        }

        private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (_activeFingers.ContainsKey(-1)) EndFinger(-1);
            DrawingCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        // ─── Finger lifecycle ───────────────────────────

        private void BeginFinger(int contactId, Point pt)
        {
            if (_activeFingers.ContainsKey(contactId)) return;
            if (!_gestureActive)
            {
                _gestureActive = true;
                _gestureStartMs = Environment.TickCount64;
            }
            int slot = _activeFingers.Count;
            var brush = _fingerBrushes[slot % _fingerBrushes.Length];
            var poly = new Polyline { Stroke = brush, StrokeThickness = 3 };
            poly.Points.Add(pt);
            DrawingCanvas.Children.Add(poly);

            long t = Environment.TickCount64 - _gestureStartMs;
            _activeFingers[contactId] = new FingerTrack
            {
                ContactId = contactId,
                Visual = poly,
                Points = new List<Vector2> { new Vector2((float)pt.X, (float)pt.Y) },
                Timestamps = new List<long> { t },
                StartedAtMs = t,
            };
            UpdateUiText();
        }

        private void ContinueFinger(int contactId, Point pt)
        {
            if (!_activeFingers.TryGetValue(contactId, out var f)) return;
            // Coalesce same-position duplicates so the path stays sparse.
            if (f.Points.Count > 0)
            {
                var last = f.Points[^1];
                if (Math.Abs(last.X - pt.X) < 0.5 && Math.Abs(last.Y - pt.Y) < 0.5) return;
            }
            f.Visual.Points.Add(pt);
            f.Points.Add(new Vector2((float)pt.X, (float)pt.Y));
            f.Timestamps.Add(Environment.TickCount64 - _gestureStartMs);
        }

        private void EndFinger(int contactId)
        {
            if (!_activeFingers.TryGetValue(contactId, out _)) return;
            _activeFingers.Remove(contactId);
            // When all fingers up, close out the sample.
            if (_activeFingers.Count == 0 && _gestureActive)
                CommitSample();
        }

        private void CommitSample()
        {
            _gestureActive = false;
            var sample = new List<List<Vector2>>();
            var stamps = new List<List<long>>();
            foreach (var poly in DrawingCanvas.Children)
            {
                if (poly is not Polyline p) continue;
                var pts = new List<Vector2>();
                foreach (var pt in p.Points) pts.Add(new Vector2((float)pt.X, (float)pt.Y));
                sample.Add(pts);
            }
            // Above iteration captured visuals in canvas order, but we
            // dropped the per-finger timestamps when we cleared. Recover
            // by recomputing: each sub-path was added in finger-start
            // order, so timestamp 0 starts at the first contact. We
            // resample at the same fixed point count later, so the exact
            // pacing isn't preserved — just relative shape.
            foreach (var path in sample)
            {
                var st = new List<long>();
                for (int i = 0; i < path.Count; i++) st.Add(i);
                stamps.Add(st);
            }

            if (_expectedFingerCount == 0) _expectedFingerCount = sample.Count;
            else if (sample.Count != _expectedFingerCount)
            {
                // Finger-count mismatch resets the stack.
                _capturedSamples.Clear();
                _capturedTimestamps.Clear();
                _expectedFingerCount = sample.Count;
                ShowStatus(Strings.Instance.Recorder_Error_FingerCountMismatch);
            }

            _capturedSamples.Add(sample);
            _capturedTimestamps.Add(stamps);

            // Clear the canvas so the next sample draws fresh.
            DrawingCanvas.Children.Clear();
            UpdateUiText();
            ValidateSave();
        }

        private void TryAgainBtn_Click(object sender, RoutedEventArgs e)
        {
            _activeFingers.Clear();
            _capturedSamples.Clear();
            _capturedTimestamps.Clear();
            _expectedFingerCount = 0;
            _gestureActive = false;
            DrawingCanvas.Children.Clear();
            UpdateUiText();
            ValidateSave();
        }

        // ─── Save flow ──────────────────────────────────

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
            => ValidateSave();

        private void ValidateSave()
        {
            string name = NameBox?.Text?.Trim() ?? string.Empty;
            string err = ValidateName(name);
            if (err != null)
            {
                ValidationText.Text = err;
                SaveBtn.IsEnabled = false;
                return;
            }
            if (_capturedSamples.Count < _targetSampleCount)
            {
                ValidationText.Text = string.Empty;
                SaveBtn.IsEnabled = _capturedSamples.Count > 0; // allow save w/ fewer
                return;
            }
            ValidationText.Text = string.Empty;
            SaveBtn.IsEnabled = true;
        }

        private string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Strings.Instance.Recorder_Error_NameEmpty;
            if (name.Length > 64)
                return Strings.Instance.Recorder_Error_NameTooLong;
            foreach (var c in name)
            {
                if (c == '<' || c == '>' || c == '&' || c == '\"' || c == '\'')
                    return Strings.Instance.Recorder_Error_NameInvalidChar;
            }
            return null;
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (PadPage.InputService == null)
            {
                MessageBox.Show(Strings.Instance.Recorder_Error_NoEngine,
                    Strings.Instance.Recorder_Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            string name = NameBox.Text.Trim();
            if (_capturedSamples.Count == 0) return;

            var gesture = BuildGesture(name);
            if (gesture == null)
            {
                MessageBox.Show(Strings.Instance.Recorder_Error_BadSample,
                    Strings.Instance.Recorder_Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            PadPage.InputService.AddCustomTouchpadGesture(gesture);
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ─── Gesture build / averaging ──────────────────

        private TouchpadCustomGesture BuildGesture(string name)
        {
            if (_capturedSamples.Count == 0 || _expectedFingerCount <= 0) return null;
            // Per-sample: resample + normalize each finger path to the
            // recognizer's canonical point count.
            const int N = PDollarRecognizer.DefaultResampleCount;
            var perSampleNorm = new List<List<Vector2[]>>();
            foreach (var sample in _capturedSamples)
            {
                var fingers = new List<Vector2[]>();
                foreach (var path in sample)
                {
                    if (path == null || path.Count < 2) continue;
                    var resampled = PDollarRecognizer.Resample(path, N);
                    fingers.Add(PDollarRecognizer.NormalizeCloud(resampled));
                }
                if (fingers.Count == _expectedFingerCount) perSampleNorm.Add(fingers);
            }
            if (perSampleNorm.Count == 0) return null;

            // Average per-finger, per-point across samples. Finger ordering
            // is preserved (assumes the user drew in the same order across
            // samples — typical for shape gestures).
            var averagedFingers = new List<Vector2[]>();
            for (int f = 0; f < _expectedFingerCount; f++)
            {
                var avg = new Vector2[N];
                int count = 0;
                foreach (var sample in perSampleNorm)
                {
                    if (sample[f] == null || sample[f].Length != N) continue;
                    count++;
                    for (int i = 0; i < N; i++) avg[i] += sample[f][i];
                }
                if (count == 0) return null;
                for (int i = 0; i < N; i++) avg[i] /= count;
                averagedFingers.Add(avg);
            }

            // Pack the averaged normalized paths back into the gesture
            // schema. Coordinates are in normalized cloud space already.
            // Timestamp = i (synthetic monotonic), good enough for replay
            // and consistent with single-sample recordings.
            var gesture = new TouchpadCustomGesture
            {
                Name = name,
                DeviceClass = "any",
                TouchpadIndex = -1,
                Threshold = 0f,    // 0 = use global threshold
                Enabled = true,
                FingerPaths = new List<TouchpadCustomGesture.FingerPath>(),
            };
            foreach (var fingerPath in averagedFingers)
            {
                var fp = new TouchpadCustomGesture.FingerPath
                {
                    Points = new List<TouchpadCustomGesture.GesturePoint>(),
                };
                for (int i = 0; i < fingerPath.Length; i++)
                {
                    fp.Points.Add(new TouchpadCustomGesture.GesturePoint
                    {
                        X = fingerPath[i].X,
                        Y = fingerPath[i].Y,
                        T = i,
                    });
                }
                gesture.FingerPaths.Add(fp);
            }
            return gesture;
        }

        // ─── UI text updates ────────────────────────────

        private void UpdateUiText()
        {
            if (SamplesText == null) return;
            SamplesText.Text = string.Format(
                Strings.Instance.Recorder_Samples_Format,
                _capturedSamples.Count, _targetSampleCount);

            if (StatusText == null) return;
            if (_capturedSamples.Count == 0 && !_gestureActive)
                ShowStatus(Strings.Instance.Recorder_Waiting);
            else if (_gestureActive)
                ShowStatus(Strings.Instance.Recorder_Drawing);
            else if (_capturedSamples.Count >= _targetSampleCount)
                ShowStatus(Strings.Instance.Recorder_Complete);
            else
                ShowStatus(string.Format(
                    Strings.Instance.Recorder_NextSample_Format,
                    _capturedSamples.Count + 1, _targetSampleCount));
        }

        private void ShowStatus(string text)
        {
            if (StatusText != null) StatusText.Text = text ?? string.Empty;
        }
    }
}
