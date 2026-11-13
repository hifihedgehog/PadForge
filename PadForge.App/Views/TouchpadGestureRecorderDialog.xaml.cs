using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.Engine.Touchpad;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// Modal recorder dialog. Primary input path: while the dialog is
    /// open, <see cref="PadForge.Services.InputService.SetTouchpadRecordingTarget"/>
    /// streams the configured (device, pad)'s live finger positions on
    /// every poll. The canvas mirrors them in real time as colored
    /// polylines, so the user draws gestures on their actual touchpad
    /// and watches them appear here.
    ///
    /// Fallback input path: when no touchpad-capable device is wired
    /// (Guid.Empty target), the canvas accepts WPF mouse / touch /
    /// stylus on itself so the user can still record a gesture against
    /// the on-screen surface.
    ///
    /// Multi-sample averaging happens at save time: each sample's
    /// per-finger paths get resampled + normalized via
    /// <see cref="ShapeRecognizer"/>, then same-index points are
    /// averaged across samples per finger before packing back into the
    /// canonical <see cref="TouchpadCustomGesture"/> shape.
    ///
    /// A path is kept in the space it was drawn in. From a touchpad that
    /// is the touchpad's own 0 to 1 coordinates, which are what the
    /// recognizer is handed when the gesture is drawn again
    /// (GestureRecognizer's finger paths), so the shape kept is the shape
    /// matched whatever the proportions of the surface that mirrors it.
    /// Drawn on the surface, it is the surface's pixels.
    /// </summary>
    public partial class TouchpadGestureRecorderDialog : DialogWindow
    {
        private sealed class FingerTrack
        {
            public Polyline Visual;
            /// <summary>The mark at the finger's last point, while it is down.</summary>
            public Ellipse Dot;
            /// <summary>The path as the sample keeps it.</summary>
            public List<Vector2> Points = new();
        }

        /// <summary>The live mark's width and the stroke's.</summary>
        private const double DotSize = 16, StrokeWidth = 5;

        private readonly Dictionary<int, FingerTrack> _activeFingers = new();
        /// <summary>Every finger's path of the gesture being drawn, in the
        /// order the fingers came down, lifted fingers included.</summary>
        private readonly List<FingerTrack> _gestureTracks = new();
        private readonly List<List<List<Vector2>>> _capturedSamples = new();
        private int _targetSampleCount = 3;
        private int _expectedFingerCount;
        private bool _gestureActive;

        private readonly Guid _deviceGuid;
        private readonly int _padIndex;
        private readonly bool _hasLiveDevice;

        // A color per finger. The first three are the theme's own: the
        // cold blue the board draws its stroke in, then ember, then the
        // wait gold. A gesture of more fingers goes on to six hues of
        // middle saturation and brightness, which read on the dark
        // surface and on the light one.
        private readonly Brush[] _fingerBrushes;

        private static readonly Color[] MoreFingerColors =
        {
            Color.FromRgb(0xE6, 0x7E, 0x22), // orange
            Color.FromRgb(0x29, 0x80, 0xB9), // blue
            Color.FromRgb(0x27, 0xAE, 0x60), // green
            Color.FromRgb(0x8E, 0x44, 0xAD), // purple
            Color.FromRgb(0xD3, 0x5D, 0x0E), // amber
            Color.FromRgb(0xC0, 0x39, 0x2B), // red
        };

        public TouchpadGestureRecorderDialog() : this(Guid.Empty, 0, string.Empty) { }

        public TouchpadGestureRecorderDialog(Guid deviceGuid, int padIndex, string deviceName)
        {
            InitializeComponent();

            var brushes = new List<Brush>();
            foreach (string key in new[] { "ColdBrush", "EmberBrush", "WaitBrush" })
                if (TryFindResource(key) is Brush themed) brushes.Add(themed);
            foreach (var color in MoreFingerColors) brushes.Add(new SolidColorBrush(color));
            _fingerBrushes = brushes.ToArray();

            _deviceGuid = deviceGuid;
            _padIndex = padIndex;
            _hasLiveDevice = deviceGuid != Guid.Empty && PadPage.InputService != null;

            UpdateDeviceLabel(deviceName);
            UpdateUiText();
            Loaded += OnDialogLoaded;
            Closed += OnDialogClosed;
        }

        private void OnDialogLoaded(object sender, RoutedEventArgs e)
        {
            if (_hasLiveDevice)
            {
                PadPage.InputService.SetTouchpadRecordingTarget(
                    _deviceGuid, _padIndex, OnRecordingTickFromEngine);
            }
        }

        private void OnDialogClosed(object sender, EventArgs e)
        {
            if (_hasLiveDevice)
                PadPage.InputService?.ClearTouchpadRecordingTarget();
        }

        private void UpdateDeviceLabel(string deviceName)
        {
            if (DeviceLabel == null) return;
            if (_hasLiveDevice)
            {
                // Live device wired — show the "Recording from …" line
                // even when the caller couldn't supply a display name.
                // Falling through to the unavailable string here would
                // tell the user the touchpad is offline when in fact
                // their finger paths are about to stream into the canvas.
                string nameForLabel = string.IsNullOrWhiteSpace(deviceName)
                    ? Strings.Instance.Recorder_TargetDevice_UnknownName
                    : deviceName;
                // Pad number shown 1-based to match the rest of the UI;
                // _padIndex stays 0-based for the engine wiring above.
                DeviceLabel.Text = string.Format(Strings.Instance.Recorder_TargetDevice_Format,
                    nameForLabel, _padIndex + 1);
            }
            else
            {
                DeviceLabel.Text = Strings.Instance.Recorder_TargetDevice_None;
            }
        }

        // ─── Live engine tick path (real touchpad → canvas mirror) ─

        private readonly Dictionary<int, int> _engineContactToTrack = new();

        private void OnRecordingTickFromEngine(TouchpadInputState pad)
        {
            if (pad == null) return;
            // Marshal off the polling thread before touching visuals.
            Dispatcher.BeginInvoke(new Action(() => ApplyEngineTick(pad)));
        }

        private void ApplyEngineTick(TouchpadInputState pad)
        {
            if (pad == null || pad.FingerDown == null) return;
            double w = DrawingCanvas.ActualWidth;
            double h = DrawingCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Walk every finger slot. Down + non-zero contact id = active
            // finger; map by contact id so palm-on / palm-off doesn't
            // splice unrelated traces.
            for (int f = 0; f < pad.FingerDown.Length; f++)
            {
                bool down = pad.FingerDown[f];
                int cid = pad.FingerContactId != null && f < pad.FingerContactId.Length
                    ? pad.FingerContactId[f] : -1;
                if (down && cid >= 0)
                {
                    // The surface shows the finger where it is on the pad.
                    // The sample keeps the pad's own coordinates.
                    var pt = new Point(pad.FingerX[f] * w, pad.FingerY[f] * h);
                    var kept = new Vector2(pad.FingerX[f], pad.FingerY[f]);
                    int trackKey;
                    if (!_engineContactToTrack.TryGetValue(cid, out trackKey))
                    {
                        trackKey = 1000 + cid; // namespace away from mouse/stylus keys
                        _engineContactToTrack[cid] = trackKey;
                        BeginFinger(trackKey, pt, kept);
                    }
                    else
                    {
                        ContinueFinger(trackKey, pt, kept);
                    }
                }
            }
            // Detect fingers that lifted: any tracked contact whose
            // current slot is no longer down (or the contact id changed)
            // gets closed.
            var toRelease = new List<int>();
            foreach (var kv in _engineContactToTrack)
            {
                bool stillDown = false;
                for (int f = 0; f < pad.FingerDown.Length; f++)
                {
                    bool down = pad.FingerDown[f];
                    int cid = pad.FingerContactId != null && f < pad.FingerContactId.Length
                        ? pad.FingerContactId[f] : -1;
                    if (down && cid == kv.Key) { stillDown = true; break; }
                }
                if (!stillDown) toRelease.Add(kv.Key);
            }
            foreach (var cid in toRelease)
            {
                EndFinger(_engineContactToTrack[cid]);
                _engineContactToTrack.Remove(cid);
            }
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

        // ─── Fallback input path: WPF touch / mouse / stylus ────────
        //
        // These only matter when no live device is wired up (overlay-only
        // profile). With a live device, they no-op so the user can't
        // accidentally splice a mouse drag into a real-device sample.

        private void DrawingCanvas_TouchDown(object sender, TouchEventArgs e)
        {
            if (_hasLiveDevice) return;
            var pt = e.GetTouchPoint(DrawingCanvas).Position;
            BeginFinger(e.TouchDevice.Id, pt);
            DrawingCanvas.CaptureTouch(e.TouchDevice);
            e.Handled = true;
        }

        private void DrawingCanvas_TouchMove(object sender, TouchEventArgs e)
        {
            if (_hasLiveDevice) return;
            var pt = e.GetTouchPoint(DrawingCanvas).Position;
            ContinueFinger(e.TouchDevice.Id, pt);
            e.Handled = true;
        }

        private void DrawingCanvas_TouchUp(object sender, TouchEventArgs e)
        {
            if (_hasLiveDevice) return;
            EndFinger(e.TouchDevice.Id);
            DrawingCanvas.ReleaseTouchCapture(e.TouchDevice);
            e.Handled = true;
        }

        private void DrawingCanvas_StylusDown(object sender, StylusDownEventArgs e)
        {
            if (_hasLiveDevice) return;
            var pt = e.GetPosition(DrawingCanvas);
            BeginFinger(-2, pt);
            e.Handled = true;
        }

        private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_hasLiveDevice) return;
            if (e.ChangedButton != MouseButton.Left) return;
            var pt = e.GetPosition(DrawingCanvas);
            BeginFinger(-1, pt);
            DrawingCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_hasLiveDevice) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (!_activeFingers.ContainsKey(-1)) return;
            var pt = e.GetPosition(DrawingCanvas);
            ContinueFinger(-1, pt);
            e.Handled = true;
        }

        private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_hasLiveDevice) return;
            if (e.ChangedButton != MouseButton.Left) return;
            if (_activeFingers.ContainsKey(-1)) EndFinger(-1);
            DrawingCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        // ─── Finger lifecycle (shared by live and fallback paths) ──

        // A stroke drawn on the surface is kept in the surface's pixels.
        private void BeginFinger(int contactKey, Point pt) => BeginFinger(contactKey, pt, new Vector2((float)pt.X, (float)pt.Y));

        private void ContinueFinger(int contactKey, Point pt) => ContinueFinger(contactKey, pt, new Vector2((float)pt.X, (float)pt.Y));

        /// <summary>A finger comes down: its stroke starts at
        /// <paramref name="pt"/> on the surface with a mark at its end, and
        /// its path starts at <paramref name="kept"/>.</summary>
        private void BeginFinger(int contactKey, Point pt, Vector2 kept)
        {
            if (_activeFingers.ContainsKey(contactKey)) return;
            if (!_gestureActive)
            {
                _gestureActive = true;
            }
            int slot = _activeFingers.Count;
            var brush = _fingerBrushes[slot % _fingerBrushes.Length];
            var poly = new Polyline
            {
                Stroke = brush,
                StrokeThickness = StrokeWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            poly.Points.Add(pt);
            DrawingCanvas.Children.Add(poly);
            var dot = new Ellipse { Width = DotSize, Height = DotSize, Fill = brush, IsHitTestVisible = false };
            MoveDot(dot, pt);
            DrawingCanvas.Children.Add(dot);

            var track = new FingerTrack
            {
                Visual = poly,
                Dot = dot,
                Points = new List<Vector2> { kept },
            };
            _activeFingers[contactKey] = track;
            _gestureTracks.Add(track);
            UpdateUiText();
        }

        private static void MoveDot(Ellipse dot, Point pt)
        {
            Canvas.SetLeft(dot, pt.X - DotSize / 2);
            Canvas.SetTop(dot, pt.Y - DotSize / 2);
        }

        private void ContinueFinger(int contactKey, Point pt, Vector2 kept)
        {
            if (!_activeFingers.TryGetValue(contactKey, out var f)) return;
            // A move of less than half a pixel on the surface draws nothing.
            if (f.Visual.Points.Count > 0)
            {
                var last = f.Visual.Points[^1];
                if (Math.Abs(last.X - pt.X) < 0.5 && Math.Abs(last.Y - pt.Y) < 0.5) return;
            }
            f.Visual.Points.Add(pt);
            f.Points.Add(kept);
            MoveDot(f.Dot, pt);
        }

        private void EndFinger(int contactKey)
        {
            if (!_activeFingers.TryGetValue(contactKey, out var f)) return;
            _activeFingers.Remove(contactKey);
            // The stroke stays until the gesture ends. The mark of a finger
            // that lifted goes at once.
            DrawingCanvas.Children.Remove(f.Dot);
            if (_activeFingers.Count == 0 && _gestureActive)
                CommitSample();
        }

        private void CommitSample()
        {
            _gestureActive = false;
            var sample = new List<List<Vector2>>();
            foreach (var track in _gestureTracks)
                if (track.Points.Count >= 2) sample.Add(track.Points);
            _gestureTracks.Clear();
            // A tap draws no stroke, so it is no sample and changes nothing.
            // Read as a gesture of no fingers, it cleared every sample taken
            // so far for a touch that only rested on the pad.
            if (sample.Count == 0)
            {
                DrawingCanvas.Children.Clear();
                UpdateUiText();
                return;
            }

            if (_expectedFingerCount == 0) _expectedFingerCount = sample.Count;
            else if (sample.Count != _expectedFingerCount)
            {
                _capturedSamples.Clear();
                _expectedFingerCount = sample.Count;
                ShowStatus(Strings.Instance.Recorder_Error_FingerCountMismatch);
            }

            if (sample.Count > 0) _capturedSamples.Add(sample);

            // Clear visuals so the next sample draws fresh.
            DrawingCanvas.Children.Clear();
            UpdateUiText();
            ValidateSave();
        }

        private void TryAgainBtn_Click(object sender, RoutedEventArgs e)
        {
            _activeFingers.Clear();
            _gestureTracks.Clear();
            _engineContactToTrack.Clear();
            _capturedSamples.Clear();
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
                ShowProblem(err);
                SaveBtn.IsEnabled = false;
                return;
            }
            if (_capturedSamples.Count < _targetSampleCount)
            {
                ShowProblem(null);
                SaveBtn.IsEnabled = _capturedSamples.Count > 0;
                return;
            }
            ShowProblem(null);
            SaveBtn.IsEnabled = true;
        }

        /// <summary>The line under the name: why the gesture cannot be
        /// saved. Empty, it takes no room.</summary>
        private void ShowProblem(string problem)
        {
            ValidationText.Text = problem ?? string.Empty;
            ValidationText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
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
            // What stops the save is said on the line under the name, and
            // the dialog stays open with the samples it has.
            if (PadPage.InputService == null)
            {
                ShowProblem(Strings.Instance.Recorder_Error_NoEngine);
                return;
            }
            string name = NameBox.Text.Trim();
            if (_capturedSamples.Count == 0) return;

            var gesture = BuildGesture(name);
            if (gesture == null)
            {
                ShowProblem(Strings.Instance.Recorder_Error_BadSample);
                return;
            }
            PadPage.InputService.AddCustomTouchpadGesture(gesture);
            DialogResult = true;
            Close();
        }

        // ─── Gesture build / averaging ──────────────────

        private TouchpadCustomGesture BuildGesture(string name)
        {
            if (_capturedSamples.Count == 0 || _expectedFingerCount <= 0) return null;
            const int N = ShapeRecognizer.DefaultResampleCount;
            var perSampleNorm = new List<List<Vector2[]>>();
            foreach (var sample in _capturedSamples)
            {
                var fingers = new List<Vector2[]>();
                foreach (var path in sample)
                {
                    if (path == null || path.Count < 2) continue;
                    var resampled = ShapeRecognizer.Resample(path, N);
                    fingers.Add(ShapeRecognizer.NormalizeCloud(resampled));
                }
                if (fingers.Count == _expectedFingerCount) perSampleNorm.Add(fingers);
            }
            if (perSampleNorm.Count == 0) return null;

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

            var gesture = new TouchpadCustomGesture
            {
                Name = name,
                DeviceClass = "any",
                TouchpadIndex = -1,
                Threshold = 0f,
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
                ShowStatus(_hasLiveDevice
                    ? Strings.Instance.Recorder_Waiting_Device
                    : Strings.Instance.Recorder_Waiting);
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
