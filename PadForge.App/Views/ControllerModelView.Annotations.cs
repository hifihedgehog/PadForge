// Annotation overlay for the 3D controller preview (#175 roadmap 1).
//
// A 2D Canvas sibling of the HelixViewport3D carries one chip per mapped
// ButtonMap-backed row (steel chip at the canvas edge, 1px cold leader
// line to the control's projected anchor) plus two slim trigger level
// bars (cold raw beside ember out). Off by default, session-only state.
//
// Rules baked in: no storyboards, no Effects, all live state is plain
// brush/property swaps. Re-projection is timer-driven (150 ms), never
// per-frame; only bar heights update at render rate.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using HelixToolkit.Wpf;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class ControllerModelView
    {
        // ─────────────────────────────────────────────
        //  Annotation constants
        // ─────────────────────────────────────────────

        private const double AnnotationChipHeight = 20;   // CH
        private const double AnnotationChipGap = 6;       // G
        private const double AnnotationEdgeMargin = 8;    // M
        private const double AnnotationBarHeight = 36;    // trigger fill track
        private const double AnnotationBarTrackWidth = 12; // 1px border + 1px pad + 3+2+3

        // ─────────────────────────────────────────────
        //  Annotation state
        // ─────────────────────────────────────────────

        private bool _annotationsEnabled;
        private bool _annotationDragHidden;
        private bool _annotationRebuildQueued;
        private System.Collections.ObjectModel.ObservableCollection<MappingItem> _annotationHookedMappings;
        private DispatcherTimer _annotationTimer;
        private readonly List<AnnotationChip> _annotationChips = new();
        private readonly List<AnnotationTriggerBars> _annotationTriggerBars = new();
        private readonly List<MappingItem> _annotationSubscribedRows = new();

        /// <summary>Raised from the toggle button click only (user intent),
        /// never from the AnnotationsEnabled setter, so the owner-side
        /// write-back can't loop.</summary>
        public event EventHandler<bool> AnnotationsToggled;

        /// <summary>Raised on chip click. Payload is the owning row's
        /// MappingItem.TargetSettingName.</summary>
        public event EventHandler<string> AnnotationChipNavigateRequested;

        /// <summary>Annotation overlay on/off. Session-only: the hosting page
        /// pushes the ViewModel state in on bind and writes it back on
        /// AnnotationsToggled. Setter raises nothing.</summary>
        public bool AnnotationsEnabled
        {
            get => _annotationsEnabled;
            set
            {
                _annotationsEnabled = value;
                if (value)
                {
                    AnnotationCanvas.Visibility = _annotationDragHidden
                        ? Visibility.Hidden : Visibility.Visible;
                    if (_annotationTimer == null)
                    {
                        _annotationTimer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(150)
                        };
                        _annotationTimer.Tick += AnnotationTick;
                    }
                    _annotationTimer.Start();
                    RebuildAnnotations();
                }
                else
                {
                    _annotationTimer?.Stop();
                    ClearAnnotationVisuals();
                    AnnotationCanvas.Visibility = Visibility.Collapsed;
                }
                UpdateAnnotationToggleChrome();
            }
        }

        private void AnnotationToggle_Click(object sender, RoutedEventArgs e)
        {
            AnnotationsEnabled = !AnnotationsEnabled;
            AnnotationsToggled?.Invoke(this, AnnotationsEnabled);
        }

        /// <summary>Toggle chrome: cold glyph + cold ring while on (input-side
        /// readout per the icon-button domain-ring convention), inherited
        /// values while off. Property swaps only, no Effect.</summary>
        private void UpdateAnnotationToggleChrome()
        {
            if (AnnotationToggleButton == null || AnnotationToggleGlyph == null)
                return;
            if (_annotationsEnabled)
            {
                AnnotationToggleGlyph.SetResourceReference(TextBlock.ForegroundProperty, "ColdBrush");
                AnnotationToggleButton.SetResourceReference(Control.BorderBrushProperty, "ColdBrush");
            }
            else
            {
                AnnotationToggleGlyph.ClearValue(TextBlock.ForegroundProperty);
                AnnotationToggleButton.ClearValue(Control.BorderBrushProperty);
            }
        }

        // ─────────────────────────────────────────────
        //  Per-chip / per-bar bookkeeping
        // ─────────────────────────────────────────────

        private sealed class AnnotationChip
        {
            public MappingItem Row;
            public Model3DGroup Anchor;
            public Border Border;
            public TextBlock Text;
            public Line Leader;
            public Ellipse EmberDot;
            /// <summary>MinValue = no flash pending; MaxValue = static cold
            /// (reduced motion) held until the row goes inactive; anything
            /// else = transient flash expiry.</summary>
            public DateTime FlashUntil = DateTime.MinValue;
            public Point Projected;
            public bool LeftColumn;
            public bool Visible;
        }

        private sealed class AnnotationTriggerBars
        {
            public Model3DGroup Anchor;
            public Border Track;
            public Rectangle ColdBar;
            public Rectangle EmberBar;
            public bool LeftSide;
        }

        // ─────────────────────────────────────────────
        //  Build / teardown
        // ─────────────────────────────────────────────

        /// <summary>Rebuilds every chip and bar cluster from the current
        /// model + Mappings. Cheap no-op while disabled or unbound.</summary>
        private void RebuildAnnotations()
        {
            ClearAnnotationVisuals();

            if (!_annotationsEnabled || _vm == null || _currentModel == null)
                return;

            if (!ReferenceEquals(_annotationHookedMappings, _vm.Mappings))
            {
                if (_annotationHookedMappings != null)
                    _annotationHookedMappings.CollectionChanged -= OnAnnotationMappingsChanged;
                _annotationHookedMappings = _vm.Mappings;
                _annotationHookedMappings.CollectionChanged += OnAnnotationMappingsChanged;
            }

            foreach (var row in _vm.Mappings)
            {
                if (string.IsNullOrEmpty(row.TargetSettingName))
                    continue;
                if (!_currentModel.ButtonMap.TryGetValue(row.TargetSettingName, out var groups)
                    || groups == null || groups.Count == 0)
                    continue;

                // Subscribe every ButtonMap-backed row (mapped or not) so a
                // row gaining its first source rebuilds the chip set too.
                row.PropertyChanged += OnAnnotationRowPropertyChanged;
                _annotationSubscribedRows.Add(row);

                if (!row.IsMapped)
                    continue;
                _annotationChips.Add(CreateAnnotationChip(row, groups[0]));
            }

            if (_currentModel.LeftShoulderTrigger != null)
                _annotationTriggerBars.Add(CreateTriggerBars(_currentModel.LeftShoulderTrigger, leftSide: true));
            if (_currentModel.RightShoulderTrigger != null)
                _annotationTriggerBars.Add(CreateTriggerBars(_currentModel.RightShoulderTrigger, leftSide: false));

            ReprojectAnnotations();
        }

        /// <summary>Stops the timer, unsubscribes everything, clears the
        /// canvas, and drops the enabled flag. Idempotent. Called from
        /// Unbind, so state does not survive preview swaps by itself; the
        /// hosting page re-pushes the ViewModel state on the next bind.</summary>
        private void TeardownAnnotations()
        {
            _annotationTimer?.Stop();
            if (_annotationHookedMappings != null)
                _annotationHookedMappings.CollectionChanged -= OnAnnotationMappingsChanged;
            _annotationHookedMappings = null;
            ClearAnnotationVisuals();
            _annotationsEnabled = false;
            _annotationDragHidden = false;
            AnnotationCanvas.Visibility = Visibility.Collapsed;
            UpdateAnnotationToggleChrome();
        }

        private void ClearAnnotationVisuals()
        {
            foreach (var row in _annotationSubscribedRows)
                row.PropertyChanged -= OnAnnotationRowPropertyChanged;
            _annotationSubscribedRows.Clear();
            _annotationChips.Clear();
            _annotationTriggerBars.Clear();
            AnnotationCanvas.Children.Clear();
        }

        private void OnAnnotationMappingsChanged(object sender, NotifyCollectionChangedEventArgs e)
            => QueueAnnotationRebuild();

        private void OnAnnotationRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_annotationsEnabled)
                return;

            // Runs on the UI thread: InputService drives IsInputActive from
            // its 30 Hz DispatcherTimer (InputService.cs UpdateMappingLiveValues).
            if (e.PropertyName == nameof(MappingItem.IsInputActive))
            {
                if (sender is MappingItem row)
                {
                    var chip = FindAnnotationChip(row);
                    if (chip != null)
                        ApplyChipActivityState(chip);
                }
            }
            else if (e.PropertyName == nameof(MappingItem.SourceDescriptor)
                  || e.PropertyName == nameof(MappingItem.IsMapped))
            {
                QueueAnnotationRebuild();
            }
            else if (e.PropertyName == nameof(MappingItem.SourceDisplayText))
            {
                // Resolved source text arrives after the descriptor
                // (SetResolvedSourceText); refresh in place, no rebuild.
                if (sender is MappingItem row)
                {
                    var chip = FindAnnotationChip(row);
                    if (chip != null)
                        chip.Text.Text = $"{row.SourceDisplayText} -> {row.TargetLabel}";
                }
            }
        }

        private void QueueAnnotationRebuild()
        {
            if (_annotationRebuildQueued)
                return;
            _annotationRebuildQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _annotationRebuildQueued = false;
                RebuildAnnotations();
            }));
        }

        private AnnotationChip FindAnnotationChip(MappingItem row)
        {
            foreach (var chip in _annotationChips)
                if (chip.Row == row)
                    return chip;
            return null;
        }

        // ─────────────────────────────────────────────
        //  Element construction
        // ─────────────────────────────────────────────

        private AnnotationChip CreateAnnotationChip(MappingItem row, Model3DGroup anchor)
        {
            var text = new TextBlock
            {
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Text = $"{row.SourceDisplayText} -> {row.TargetLabel}",
            };
            if (Application.Current.Resources["TelemetryFontFamily"] is FontFamily telemetry)
                text.FontFamily = telemetry;
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            var border = new Border
            {
                Height = AnnotationChipHeight,
                MaxWidth = 180,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                Child = text,
                Tag = row.TargetSettingName,
                Visibility = Visibility.Collapsed,
            };
            border.SetResourceReference(Border.BackgroundProperty, "SteelRaisedBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "SteelLineSoftBrush");
            border.MouseLeftButtonUp += AnnotationChip_MouseLeftButtonUp;

            var leader = new Line
            {
                StrokeThickness = 1,
                Opacity = 0.40,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            leader.SetResourceReference(Shape.StrokeProperty, "ColdBrush");

            // Ember output dot, not a DropShadowEffect glow: dozens of chips
            // re-evaluated at 150 ms on the Atom Z8350 floor, and Effects are
            // never animated in this repo.
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            dot.SetResourceReference(Shape.FillProperty, "EmberBrush");

            AnnotationCanvas.Children.Add(leader);
            AnnotationCanvas.Children.Add(border);
            AnnotationCanvas.Children.Add(dot);

            var chip = new AnnotationChip
            {
                Row = row,
                Anchor = anchor,
                Border = border,
                Text = text,
                Leader = leader,
                EmberDot = dot,
            };
            if (row.IsInputActive)
                ApplyChipActivityState(chip);
            return chip;
        }

        private AnnotationTriggerBars CreateTriggerBars(Model3DGroup anchor, bool leftSide)
        {
            var cold = new Rectangle
            {
                Width = 3,
                Height = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
            };
            cold.SetResourceReference(Shape.FillProperty, "ColdBrush");

            var ember = new Rectangle
            {
                Width = 3,
                Height = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
            };
            ember.SetResourceReference(Shape.FillProperty, "EmberBrush");

            var inner = new Grid { Height = AnnotationBarHeight };
            inner.Children.Add(cold);
            inner.Children.Add(ember);

            var track = new Border
            {
                Width = AnnotationBarTrackWidth,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(1),
                Child = inner,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            track.SetResourceReference(Border.BorderBrushProperty, "SteelLineSoftBrush");

            AnnotationCanvas.Children.Add(track);

            return new AnnotationTriggerBars
            {
                Anchor = anchor,
                Track = track,
                ColdBar = cold,
                EmberBar = ember,
                LeftSide = leftSide,
            };
        }

        private void AnnotationChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string target)
            {
                AnnotationChipNavigateRequested?.Invoke(this, target);
                e.Handled = true;
            }
        }

        // ─────────────────────────────────────────────
        //  Projection + layout
        // ─────────────────────────────────────────────

        /// <summary>Projects a model group's bounds center to viewport-space
        /// DIPs. Local anchor via MeshCentroid, local→world via the same
        /// ModelVisual3D.Transform the hit-test path inverts in
        /// TransformToLocal, world→2D via Viewport3DHelper. Null when the
        /// point sits behind the camera or the projection is degenerate.</summary>
        private Point? ProjectAnnotationAnchor(Model3DGroup group)
        {
            if (group == null)
                return null;

            var c = MeshCentroid(group);
            var world = new Point3D(c.X, c.Y, c.Z);
            var transform = ModelVisual3D.Transform;
            if (transform != null && transform != Transform3D.Identity)
                world = transform.Transform(world);

            // Behind-camera cull: Point3DtoPoint2D happily projects points
            // behind the eye to mirrored 2D coordinates.
            if (ModelViewPort.Camera is ProjectionCamera cam)
            {
                if (Vector3D.DotProduct(world - cam.Position, cam.LookDirection) <= 0)
                    return null;
            }

            var p = Viewport3DHelper.Point3DtoPoint2D(ModelViewPort.Viewport, world);
            if (double.IsNaN(p.X) || double.IsNaN(p.Y)
                || double.IsInfinity(p.X) || double.IsInfinity(p.Y))
                return null;
            return p;
        }

        /// <summary>Runs the full projection + column layout pass and
        /// positions the trigger bar clusters. Timer/interaction driven,
        /// never per-frame.</summary>
        private void ReprojectAnnotations()
        {
            if (!_annotationsEnabled || _annotationDragHidden
                || _vm == null || _currentModel == null)
                return;

            double w = AnnotationCanvas.ActualWidth;
            double h = AnnotationCanvas.ActualHeight;
            if (w < 40 || h < 40)
                return;

            var leftColumn = new List<AnnotationChip>();
            var rightColumn = new List<AnnotationChip>();

            foreach (var chip in _annotationChips)
            {
                var p = ProjectAnnotationAnchor(chip.Anchor);
                bool visible = p.HasValue
                    && p.Value.X >= 0 && p.Value.X <= w
                    && p.Value.Y >= 0 && p.Value.Y <= h;
                chip.Visible = visible;
                chip.Border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                chip.Leader.Visibility = chip.Border.Visibility;
                if (!visible)
                {
                    chip.EmberDot.Visibility = Visibility.Collapsed;
                    continue;
                }

                chip.Projected = p.Value;
                // Side by projected position, not model X, so the split stays
                // correct after the user spins the model 180 degrees.
                chip.LeftColumn = p.Value.X < w / 2;
                (chip.LeftColumn ? leftColumn : rightColumn).Add(chip);
            }

            LayoutAnnotationColumn(leftColumn, leftSide: true, w, h);
            LayoutAnnotationColumn(rightColumn, leftSide: false, w, h);

            foreach (var bars in _annotationTriggerBars)
            {
                var p = ProjectAnnotationAnchor(bars.Anchor);
                bool visible = p.HasValue
                    && p.Value.X >= 0 && p.Value.X <= w
                    && p.Value.Y >= 0 && p.Value.Y <= h;
                bars.Track.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!visible)
                    continue;

                // Offset 10px toward the nearer canvas edge.
                double x = bars.LeftSide
                    ? p.Value.X - 10 - AnnotationBarTrackWidth
                    : p.Value.X + 10;
                Canvas.SetLeft(bars.Track, x);
                Canvas.SetTop(bars.Track, p.Value.Y - (AnnotationBarHeight + 4) / 2);
            }
        }

        /// <summary>Two-pass slot assignment: downward greedy from the sorted
        /// anchors, then an upward overflow fix. No overlaps, minimal
        /// displacement (axis-label staggering family).</summary>
        private void LayoutAnnotationColumn(List<AnnotationChip> chips, bool leftSide, double w, double h)
        {
            if (chips.Count == 0)
                return;

            chips.Sort((a, b) => a.Projected.Y.CompareTo(b.Projected.Y));

            var y = new double[chips.Count];
            double prevBottom = AnnotationEdgeMargin - AnnotationChipGap;
            for (int i = 0; i < chips.Count; i++)
            {
                y[i] = Math.Max(chips[i].Projected.Y - AnnotationChipHeight / 2,
                                prevBottom + AnnotationChipGap);
                prevBottom = y[i] + AnnotationChipHeight;
            }

            if (y[^1] + AnnotationChipHeight > h - AnnotationEdgeMargin)
            {
                y[^1] = h - AnnotationEdgeMargin - AnnotationChipHeight;
                for (int i = chips.Count - 2; i >= 0; i--)
                    y[i] = Math.Min(y[i], y[i + 1] - AnnotationChipHeight - AnnotationChipGap);
                y[0] = Math.Max(y[0], AnnotationEdgeMargin);
            }

            var measureBounds = new Size(double.PositiveInfinity, double.PositiveInfinity);
            for (int i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                Canvas.SetTop(chip.Border, y[i]);

                double nearEdgeX;
                chip.Border.Measure(measureBounds);
                double chipWidth = chip.Border.DesiredSize.Width;
                if (leftSide)
                {
                    chip.Border.ClearValue(Canvas.RightProperty);
                    Canvas.SetLeft(chip.Border, AnnotationEdgeMargin);
                    nearEdgeX = AnnotationEdgeMargin + chipWidth;
                    chip.EmberDot.ClearValue(Canvas.RightProperty);
                    Canvas.SetLeft(chip.EmberDot, AnnotationEdgeMargin - 3);
                }
                else
                {
                    chip.Border.ClearValue(Canvas.LeftProperty);
                    Canvas.SetRight(chip.Border, AnnotationEdgeMargin);
                    nearEdgeX = w - AnnotationEdgeMargin - chipWidth;
                    chip.EmberDot.ClearValue(Canvas.LeftProperty);
                    Canvas.SetRight(chip.EmberDot, AnnotationEdgeMargin - 3);
                }

                double midY = y[i] + AnnotationChipHeight / 2;
                chip.Leader.X1 = chip.Projected.X;
                chip.Leader.Y1 = chip.Projected.Y;
                chip.Leader.X2 = nearEdgeX;
                chip.Leader.Y2 = midY;
                Canvas.SetTop(chip.EmberDot, midY - 3);
            }
        }

        // ─────────────────────────────────────────────
        //  Live state (tick + render hooks)
        // ─────────────────────────────────────────────

        /// <summary>150 ms tick: re-project, evaluate ember dots, revert
        /// expired flashes. The timer is the PRIMARY re-projection trigger.
        /// Wheel zoom, pan and yaw/pitch write cam.Position / rotation
        /// angles directly and bypass Helix's CameraController, so
        /// CameraChanged may never fire.</summary>
        private void AnnotationTick(object sender, EventArgs e)
        {
            if (!_annotationsEnabled || _vm == null || _currentModel == null)
                return;

            if (!_annotationDragHidden)
                ReprojectAnnotations();

            var now = DateTime.UtcNow;
            foreach (var chip in _annotationChips)
            {
                if (chip.FlashUntil != DateTime.MinValue
                    && chip.FlashUntil != DateTime.MaxValue
                    && now >= chip.FlashUntil)
                    RevertChipFlash(chip);

                // Ember output dot: virtual-output state on the VM, same
                // shape as GetButtonState (every ButtonMap key is a button).
                bool outputOn = chip.Visible && GetButtonState(chip.Row.TargetSettingName);
                chip.EmberDot.Visibility = outputOn ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>Cold flash on row activity. Motion on: 250 ms transient
        /// reverted by the tick. Motion off (ClientAreaAnimation false): the
        /// cold border tracks IsInputActive statically. No storyboard, no
        /// Effect.</summary>
        private void ApplyChipActivityState(AnnotationChip chip)
        {
            if (chip.Row.IsInputActive)
            {
                chip.Border.SetResourceReference(Border.BorderBrushProperty, "ColdBrush");
                chip.Text.SetResourceReference(TextBlock.ForegroundProperty, "ColdBrush");
                chip.FlashUntil = SystemParameters.ClientAreaAnimation
                    ? DateTime.UtcNow + TimeSpan.FromMilliseconds(250)
                    : DateTime.MaxValue;
            }
            else
            {
                RevertChipFlash(chip);
            }
        }

        private void RevertChipFlash(AnnotationChip chip)
        {
            chip.Border.SetResourceReference(Border.BorderBrushProperty, "SteelLineSoftBrush");
            chip.Text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            chip.FlashUntil = DateTime.MinValue;
        }

        /// <summary>Bar heights only; positions move on re-projection.
        /// Called from OnRendering inside the existing dirty gate, so this
        /// runs at VM change rate, not frame rate. Cold = raw selected
        /// device, ember = combined slot output. When no device is selected
        /// the raw feed stops updating; the ember bar still moves.</summary>
        private void UpdateAnnotationLevelBars()
        {
            if (!_annotationsEnabled || _annotationDragHidden || _vm == null)
                return;
            foreach (var bars in _annotationTriggerBars)
            {
                double raw = bars.LeftSide ? _vm.DeviceLeftTrigger : _vm.DeviceRightTrigger;
                double output = bars.LeftSide ? _vm.LeftTrigger : _vm.RightTrigger;
                bars.ColdBar.Height = Math.Clamp(raw, 0.0, 1.0) * AnnotationBarHeight;
                bars.EmberBar.Height = Math.Clamp(output, 0.0, 1.0) * AnnotationBarHeight;
            }
        }

        /// <summary>Hides the overlay while a mouse/touch drag is active and
        /// restores it (plus one immediate re-project) when the drag ends.</summary>
        private void SetAnnotationsDragHidden(bool hidden)
        {
            _annotationDragHidden = hidden;
            if (!_annotationsEnabled)
                return;
            if (hidden)
            {
                AnnotationCanvas.Visibility = Visibility.Hidden;
            }
            else
            {
                AnnotationCanvas.Visibility = Visibility.Visible;
                ReprojectAnnotations();
            }
        }
    }
}
