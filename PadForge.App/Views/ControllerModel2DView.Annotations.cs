// Annotation overlay for the 2D controller preview (#175).
//
// Same design contract as ControllerModelView.Annotations.cs (the 3D
// implementation): one steel chip per mapped row at the canvas edge, 1px
// cold leader line to the control's anchor, cold flash on input activity,
// ember output dot, chip click navigates to the Mappings row. Off by
// default, session-only state pushed in by the hosting page.
//
// Differences from the 3D file are all projection-shaped: the 2D layout
// tables give every control a fixed rectangle in ModelCanvas coordinates,
// so anchors translate through the Viewbox transform instead of a camera,
// there is no drag-hide, and re-layout is event-driven (size change,
// rebuild, label refresh) rather than timer-driven. The 150 ms timer
// remains only for flash expiry and ember-dot evaluation.
//
// Rules baked in: no storyboards, no Effects, all live state is plain
// brush/property swaps.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class ControllerModel2DView
    {
        // ─────────────────────────────────────────────
        //  Annotation constants (identical to the 3D view)
        // ─────────────────────────────────────────────

        private const double AnnotationChipHeight = 20;   // CH
        private const double AnnotationChipGap = 6;       // G
        private const double AnnotationEdgeMargin = 8;    // M

        // ─────────────────────────────────────────────
        //  Annotation state
        // ─────────────────────────────────────────────

        private bool _annotationsEnabled;
        private bool _annotationRebuildQueued;
        private System.Collections.ObjectModel.ObservableCollection<MappingItem> _annotationHookedMappings;
        private DispatcherTimer _annotationTimer;
        private readonly List<AnnotationChip> _annotationChips = new();
        private readonly List<MappingItem> _annotationSubscribedRows = new();
        /// <summary>Control anchor centers in ModelCanvas coordinates, keyed
        /// by overlay TargetName. Rebuilt by BuildCanvas from the active
        /// layout table (the same position source the model draws with).</summary>
        private readonly Dictionary<string, Point> _annotationAnchors = new();

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
                    AnnotationCanvas.Visibility = Visibility.Visible;
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
        //  Per-chip bookkeeping
        // ─────────────────────────────────────────────

        private sealed class AnnotationChip
        {
            public MappingItem Row;
            /// <summary>Anchor center in ModelCanvas coordinates.</summary>
            public Point Anchor;
            public Border Border;
            public TextBlock Text;
            public Line Leader;
            public Ellipse EmberDot;
            /// <summary>MinValue = no flash pending; MaxValue = static cold
            /// (reduced motion) held until the row goes inactive; anything
            /// else = transient flash expiry.</summary>
            public DateTime FlashUntil = DateTime.MinValue;
            /// <summary>Anchor translated to AnnotationCanvas coordinates.</summary>
            public Point Translated;
            public bool LeftColumn;
            public bool Visible;
        }

        // ─────────────────────────────────────────────
        //  Anchor table (fed by BuildCanvas)
        // ─────────────────────────────────────────────

        /// <summary>Called by BuildCanvas with the active layout table.
        /// TriggerBase rows are skipped (rest-state silhouettes, never a
        /// mapping target); everything else anchors at its rect center.</summary>
        private void SetAnnotationAnchors(PadForge.Models2D.OverlayElement[] overlays)
        {
            _annotationAnchors.Clear();
            foreach (var ov in overlays)
            {
                if (ov.ElementType == PadForge.Models2D.OverlayElementType.TriggerBase)
                    continue;
                _annotationAnchors[ov.TargetName] =
                    new Point(ov.X + ov.Width / 2, ov.Y + ov.Height / 2);
            }
        }

        /// <summary>Anchor for a mapping target: direct overlay first, then
        /// the stick-axis names, which are not overlay TargetNames (same
        /// fallback shape as the 3D ResolveAnnotationAnchor).</summary>
        private Point? ResolveAnnotationAnchor(string targetSettingName)
        {
            if (_annotationAnchors.TryGetValue(targetSettingName, out var p))
                return p;
            string ring = targetSettingName switch
            {
                "LeftThumbAxisX" or "LeftThumbAxisY" => "LeftThumbRing",
                "RightThumbAxisX" or "RightThumbAxisY" => "RightThumbRing",
                _ => null,
            };
            if (ring != null && _annotationAnchors.TryGetValue(ring, out var rp))
                return rp;
            return null;
        }

        // ─────────────────────────────────────────────
        //  Build / teardown
        // ─────────────────────────────────────────────

        /// <summary>Rebuilds every chip from the current layout + Mappings.
        /// Cheap no-op while disabled or unbound.</summary>
        private void RebuildAnnotations()
        {
            ClearAnnotationVisuals();

            if (!_annotationsEnabled || _vm == null || _loadedModel == null
                || _annotationAnchors.Count == 0)
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
                var anchor = ResolveAnnotationAnchor(row.TargetSettingName);
                if (anchor == null)
                    continue;

                // Subscribe every anchored row (mapped or not) so a row
                // gaining its first source rebuilds the chip set too.
                row.PropertyChanged += OnAnnotationRowPropertyChanged;
                _annotationSubscribedRows.Add(row);

                if (!row.IsMapped)
                    continue;
                _annotationChips.Add(CreateAnnotationChip(row, anchor.Value));
            }

            LayoutAnnotations();
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
            AnnotationCanvas.Visibility = Visibility.Collapsed;
            UpdateAnnotationToggleChrome();
        }

        private void ClearAnnotationVisuals()
        {
            foreach (var row in _annotationSubscribedRows)
                row.PropertyChanged -= OnAnnotationRowPropertyChanged;
            _annotationSubscribedRows.Clear();
            _annotationChips.Clear();
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
                // Re-layout because the chip width feeds the leader endpoint.
                if (sender is MappingItem row)
                {
                    var chip = FindAnnotationChip(row);
                    if (chip != null)
                    {
                        chip.Text.Text = CompactChipLabel(row);
                        LayoutAnnotations();
                    }
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

        /// <summary>Chip text: identical contract to the 3D view. Device
        /// prefix, then source -> output with both ends always shown even
        /// when the names match, so the wiring stays explicit.</summary>
        private static string ChipLabel(MappingItem row)
        {
            string target = (row.TargetLabel ?? string.Empty).Trim();
            // Every source, not just the primary (user report 2026-07-04:
            // secondary mappings were invisible). Each part reads
            // "device: control"; parts join with " + " ahead of the output.
            var parts = new System.Collections.Generic.List<string>();
            string primary = (row.SourceDisplayText ?? string.Empty).Trim();
            string primaryDev = (row.PrimarySourceDeviceLabel ?? string.Empty).Trim();
            if (primary.Length > 0)
                parts.Add(primaryDev.Length > 0 ? primaryDev + ": " + primary : primary);
            foreach (var src in row.ExtraSources)
            {
                string name = (src.SelectedInput?.DisplayName ?? src.Descriptor ?? string.Empty).Trim();
                if (name.Length == 0)
                    continue;
                string dev = (src.SelectedInput?.DeviceLabel ?? src.DeviceLabel ?? string.Empty).Trim();
                parts.Add(dev.Length > 0 ? dev + ": " + name : name);
            }
            if (parts.Count == 0)
                return target;
            return string.Join(" + ", parts) + " → " + target;
        }

        /// <summary>Compact chip face (user report 2026-07-04: full wiring
        /// text made the stage unreadably busy). The chip carries the
        /// output name plus a +N badge for additional sources; the full
        /// ChipLabel line appears in the detail strip on hover and stays
        /// in the tooltip.</summary>
        private static string CompactChipLabel(MappingItem row)
        {
            string target = (row.TargetLabel ?? string.Empty).Trim();
            int sources = string.IsNullOrWhiteSpace(row.SourceDisplayText) ? 0 : 1;
            foreach (var src in row.ExtraSources)
                if (!string.IsNullOrWhiteSpace(src.SelectedInput?.DisplayName ?? src.Descriptor))
                    sources++;
            return sources > 1 ? target + " +" + (sources - 1) : target;
        }

        private Border _annotationDetailStrip;
        private TextBlock _annotationDetailText;

        /// <summary>Bottom-docked mono readout: hovering a chip prints the
        /// full wiring line here, where width is unlimited.</summary>
        private void EnsureAnnotationDetailStrip()
        {
            if (_annotationDetailStrip != null)
            {
                if (!AnnotationCanvas.Children.Contains(_annotationDetailStrip))
                    AnnotationCanvas.Children.Add(_annotationDetailStrip);
                return;
            }
            _annotationDetailText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources["TelemetryFontFamily"] is FontFamily mono)
                _annotationDetailText.FontFamily = mono;
            _annotationDetailText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _annotationDetailStrip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 4),
                Child = _annotationDetailText,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _annotationDetailStrip.SetResourceReference(Border.BackgroundProperty, "SteelRaisedBrush");
            _annotationDetailStrip.SetResourceReference(Border.BorderBrushProperty, "SteelLineSoftBrush");
            Panel.SetZIndex(_annotationDetailStrip, 60);
            AnnotationCanvas.Children.Add(_annotationDetailStrip);
        }

        private void ShowAnnotationDetail(MappingItem row)
        {
            EnsureAnnotationDetailStrip();
            _annotationDetailText.Text = ChipLabel(row);
            _annotationDetailStrip.Visibility = Visibility.Visible;
            PositionAnnotationDetailStrip();
        }

        private void HideAnnotationDetail()
        {
            if (_annotationDetailStrip != null)
                _annotationDetailStrip.Visibility = Visibility.Collapsed;
        }

        private void PositionAnnotationDetailStrip()
        {
            if (_annotationDetailStrip == null)
                return;
            double w = AnnotationCanvas.ActualWidth;
            double h = AnnotationCanvas.ActualHeight;
            _annotationDetailStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double sw = _annotationDetailStrip.DesiredSize.Width;
            Canvas.SetLeft(_annotationDetailStrip, Math.Max(8, (w - sw) / 2));
            Canvas.SetTop(_annotationDetailStrip, Math.Max(8, h - _annotationDetailStrip.DesiredSize.Height - 10));
        }

        // ─────────────────────────────────────────────
        //  Element construction
        // ─────────────────────────────────────────────

        private AnnotationChip CreateAnnotationChip(MappingItem row, Point anchor)
        {
            var text = new TextBlock
            {
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Text = CompactChipLabel(row),
            };
            if (Application.Current.Resources["TelemetryFontFamily"] is FontFamily telemetry)
                text.FontFamily = telemetry;
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            var border = new Border
            {
                Height = AnnotationChipHeight,
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
            border.ToolTip = ChipLabel(row);
            border.MouseLeftButtonUp += AnnotationChip_MouseLeftButtonUp;
            border.MouseEnter += (_, _) => ShowAnnotationDetail(row);
            border.MouseLeave += (_, _) => HideAnnotationDetail();

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

        private void AnnotationChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string target)
            {
                AnnotationChipNavigateRequested?.Invoke(this, target);
                e.Handled = true;
            }
        }

        // ─────────────────────────────────────────────
        //  Layout
        // ─────────────────────────────────────────────

        /// <summary>Translates every anchor through the Viewbox transform and
        /// runs the column layout. Event-driven (size change, rebuild, label
        /// refresh), never per-frame: the 2D anchors only move when the
        /// Viewbox rescales.</summary>
        private void LayoutAnnotations()
        {
            if (!_annotationsEnabled || _vm == null || _loadedModel == null)
                return;

            double w = AnnotationCanvas.ActualWidth;
            double h = AnnotationCanvas.ActualHeight;
            if (w < 40 || h < 40)
                return;

            var leftColumn = new List<AnnotationChip>();
            var rightColumn = new List<AnnotationChip>();

            foreach (var chip in _annotationChips)
            {
                // ModelCanvas -> AnnotationCanvas through the live Viewbox
                // scale + letterbox offset (both are children of the same
                // Grid, so TranslatePoint sees the full transform chain).
                var p = ModelCanvas.TranslatePoint(chip.Anchor, AnnotationCanvas);
                bool visible = p.X >= 0 && p.X <= w && p.Y >= 0 && p.Y <= h;
                chip.Visible = visible;
                chip.Border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                chip.Leader.Visibility = chip.Border.Visibility;
                if (!visible)
                {
                    chip.EmberDot.Visibility = Visibility.Collapsed;
                    continue;
                }

                chip.Translated = p;
                chip.LeftColumn = p.X < w / 2;
                (chip.LeftColumn ? leftColumn : rightColumn).Add(chip);
            }

            LayoutAnnotationColumn(leftColumn, leftSide: true, w, h);
            LayoutAnnotationColumn(rightColumn, leftSide: false, w, h);
        }

        /// <summary>Two-pass slot assignment: downward greedy from the sorted
        /// anchors, then an upward overflow fix. No overlaps, minimal
        /// displacement (same algorithm as the 3D view).</summary>
        private void LayoutAnnotationColumn(List<AnnotationChip> chips, bool leftSide, double w, double h)
        {
            if (chips.Count == 0)
                return;

            chips.Sort((a, b) => a.Translated.Y.CompareTo(b.Translated.Y));

            var y = new double[chips.Count];
            double prevBottom = AnnotationEdgeMargin - AnnotationChipGap;
            for (int i = 0; i < chips.Count; i++)
            {
                y[i] = Math.Max(chips[i].Translated.Y - AnnotationChipHeight / 2,
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
                chip.Leader.X1 = chip.Translated.X;
                chip.Leader.Y1 = chip.Translated.Y;
                chip.Leader.X2 = nearEdgeX;
                chip.Leader.Y2 = midY;
                Canvas.SetTop(chip.EmberDot, midY - 3);
            }
        }

        // ─────────────────────────────────────────────
        //  Live state
        // ─────────────────────────────────────────────

        /// <summary>150 ms tick: revert expired flashes and evaluate ember
        /// dots. Layout is NOT re-run here; anchors are static between
        /// size/rebuild events.</summary>
        private void AnnotationTick(object sender, EventArgs e)
        {
            if (!_annotationsEnabled || _vm == null)
                return;

            var now = DateTime.UtcNow;
            foreach (var chip in _annotationChips)
            {
                if (chip.FlashUntil != DateTime.MinValue
                    && chip.FlashUntil != DateTime.MaxValue
                    && now >= chip.FlashUntil)
                    RevertChipFlash(chip);

                // Ember output dot: virtual-output state on the VM, same
                // shape as the 3D GetButtonState switch.
                bool outputOn = chip.Visible && GetAnnotationButtonState(chip.Row.TargetSettingName);
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

        /// <summary>Virtual-output state for the ember dot. Mirrors the 3D
        /// GetButtonState switch, plus TouchpadClick, which the 2D layouts
        /// anchor (PlayStation slots) and the VM already exposes for the
        /// touchpad preview. Axis and trigger targets return false, same as
        /// the 3D view.</summary>
        private bool GetAnnotationButtonState(string prop)
        {
            if (_vm == null) return false;
            return prop switch
            {
                "ButtonA" => _vm.ButtonA,
                "ButtonB" => _vm.ButtonB,
                "ButtonX" => _vm.ButtonX,
                "ButtonY" => _vm.ButtonY,
                "LeftShoulder" => _vm.LeftShoulder,
                "RightShoulder" => _vm.RightShoulder,
                "ButtonBack" => _vm.ButtonBack,
                "ButtonStart" => _vm.ButtonStart,
                "ButtonGuide" => _vm.ButtonGuide,
                "ButtonShare" => _vm.ButtonShare,
                "DPadUp" => _vm.DPadUp,
                "DPadDown" => _vm.DPadDown,
                "DPadLeft" => _vm.DPadLeft,
                "DPadRight" => _vm.DPadRight,
                "LeftThumbButton" => _vm.LeftThumbButton,
                "RightThumbButton" => _vm.RightThumbButton,
                "TouchpadClick" => _vm.TouchpadClickPressed,
                _ => false
            };
        }
    }
}
