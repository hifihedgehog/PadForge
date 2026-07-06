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
        private const int AnnotationDetailMaxRows = 12;   // wiring rows before the +N tail

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
            // Both gates must hold for the track to show: anchor projects
            // on-canvas AND a level is nonzero. An idle empty track read as
            // a stray box on the trigger (user report, 2026-07-04).
            public bool ProjectionValid;
            public bool LevelsLive;
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
                var anchor = ResolveAnnotationAnchor(row.TargetSettingName);
                if (anchor == null)
                    continue;

                // Subscribe every anchored row (mapped or not) so a row
                // gaining its first source rebuilds the chip set too.
                row.PropertyChanged += OnAnnotationRowPropertyChanged;
                _annotationSubscribedRows.Add(row);

                if (!row.IsMapped)
                    continue;
                _annotationChips.Add(CreateAnnotationChip(row, anchor));
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
                // The tooltip rebuilds too so it never shows stale wiring.
                if (sender is MappingItem row)
                {
                    var chip = FindAnnotationChip(row);
                    if (chip != null)
                    {
                        chip.Text.Text = CompactChipLabel(row);
                        chip.Border.ToolTip = BuildAnnotationDetailContent(row);
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

        /// <summary>Anchor for a mapping target: ButtonMap first, then the
        /// named stick/trigger groups. Stick axes and triggers are not
        /// ButtonMap keys, which left them without chips (user report,
        /// 2026-07-04).</summary>
        private Model3DGroup ResolveAnnotationAnchor(string targetSettingName)
        {
            if (_currentModel.ButtonMap.TryGetValue(targetSettingName, out var groups)
                && groups != null && groups.Count > 0)
                return groups[0];
            return targetSettingName switch
            {
                "LeftThumbAxisX" or "LeftThumbAxisY" => _currentModel.LeftThumb,
                "RightThumbAxisX" or "RightThumbAxisY" => _currentModel.RightThumb,
                "LeftTrigger" => _currentModel.LeftShoulderTrigger,
                "RightTrigger" => _currentModel.RightShoulderTrigger,
                _ => null,
            };
        }

        /// <summary>One wiring row for the hover surfaces: a single source
        /// feeding this chip's output, tagged with the device it lives on.
        /// DeviceKey ("" = unbound / any device) groups rows per device.</summary>
        private sealed class AnnotationWireRow
        {
            public string DeviceKey;
            public string DeviceName;
            public string DeviceGlyph;
            public string SourceName;
        }

        /// <summary>Device name + class glyph for a source. The stored
        /// label wins (it survives disconnects); the slot roster
        /// (_vm.MappedDevices) fills gaps and supplies the DeviceTypeGlyph
        /// vocabulary; the fallbacks mirror InputService.ResolveDeviceLabel
        /// ("(Any device)" for unbound, truncated GUID for unknown).</summary>
        private void ResolveAnnotationDevice(string deviceGuid, string storedLabel,
            out string name, out string glyph)
        {
            glyph = "\uE7FC"; // controller class, the roster's own default
            name = storedLabel ?? string.Empty;
            if (!string.IsNullOrEmpty(deviceGuid) && _vm != null
                && Guid.TryParse(deviceGuid, out var g))
            {
                foreach (var dev in _vm.MappedDevices)
                {
                    if (dev == null || dev.InstanceGuid != g)
                        continue;
                    glyph = dev.TypeGlyph;
                    if (name.Length == 0)
                        name = dev.Name ?? string.Empty;
                    break;
                }
            }
            if (name.Length == 0)
            {
                name = string.IsNullOrEmpty(deviceGuid)
                    ? "(Any device)"
                    : (deviceGuid.Length > 8 ? deviceGuid.Substring(0, 8) + "…" : deviceGuid);
            }
        }

        /// <summary>Every source feeding the row, primary first, extras in
        /// Sources order (user report 2026-07-04: secondary mappings were
        /// invisible; user report 2026-07-05: extra sources carried no
        /// device name (FromDomain never sets DeviceLabel), so multi-
        /// device wiring read as a single controller). Extras take the
        /// same Inv/Half prefix labels the primary's resolved text uses.</summary>
        private List<AnnotationWireRow> BuildAnnotationWireRows(MappingItem row)
        {
            var rows = new List<AnnotationWireRow>();

            string primary = (row.SourceDisplayText ?? string.Empty).Trim();
            if (row.IsMapped && primary.Length > 0)
            {
                ResolveAnnotationDevice(row.PrimarySourceDeviceGuid,
                    (row.PrimarySourceDeviceLabel ?? string.Empty).Trim(),
                    out string dn, out string dg);
                rows.Add(new AnnotationWireRow
                {
                    DeviceKey = (row.PrimarySourceDeviceGuid ?? string.Empty).ToLowerInvariant(),
                    DeviceName = dn,
                    DeviceGlyph = dg,
                    SourceName = primary,
                });
            }

            foreach (var src in row.ExtraSources)
            {
                string name = (src.SelectedInput?.DisplayName ?? src.Descriptor ?? string.Empty).Trim();
                if (name.Length == 0)
                    continue;
                var s = PadForge.Resources.Strings.Strings.Instance;
                if (src.Invert && src.HalfAxis) name = s.Mapping_InvHalf + " " + name;
                else if (src.Invert) name = s.Mapping_Inv + " " + name;
                else if (src.HalfAxis) name = s.Mapping_Half + " " + name;

                ResolveAnnotationDevice(src.DeviceGuid,
                    (src.SelectedInput?.DeviceLabel ?? src.DeviceLabel ?? string.Empty).Trim(),
                    out string dn, out string dg);
                rows.Add(new AnnotationWireRow
                {
                    DeviceKey = (src.DeviceGuid ?? string.Empty).ToLowerInvariant(),
                    DeviceName = dn,
                    DeviceGlyph = dg,
                    SourceName = name,
                });
            }
            return rows;
        }

        /// <summary>Appends a full-width row (device header, fallback
        /// line, +N tail) to the readout grid.</summary>
        private static void AddAnnotationDetailSpan(Grid grid, FrameworkElement el, ref int gridRow)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(el, gridRow);
            Grid.SetColumnSpan(el, 3);
            grid.Children.Add(el);
            gridRow++;
        }

        /// <summary>11px mono TextBlock for the wiring readout
        /// (TelemetryFontFamily per the font canon).</summary>
        private static TextBlock MakeAnnotationDetailText(string text, string brushKey)
        {
            var tb = new TextBlock { FontSize = 11, Text = text };
            if (Application.Current.Resources["TelemetryFontFamily"] is FontFamily telemetry)
                tb.FontFamily = telemetry;
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        /// <summary>Structured wiring readout shared by the chip tooltip
        /// and the hover detail strip (user report 2026-07-05: the joined
        /// single line was unreadable and named at most one device). One
        /// mono row per source (OUTPUT ← SOURCE), grouped under a header
        /// per contributing device (class glyph + name). Caps at
        /// AnnotationDetailMaxRows rows with a locale-neutral "+N" tail;
        /// long names wrap, never truncate.</summary>
        private FrameworkElement BuildAnnotationDetailContent(MappingItem row, double maxContentWidth = 480)
        {
            var wires = BuildAnnotationWireRows(row);
            string target = (row.TargetLabel ?? string.Empty).Trim();

            // Column grid: output | arrow | source. Auto-sized so the
            // arrow lines up across every row and device group.
            var grid = new Grid { MaxWidth = maxContentWidth };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int gridRow = 0;
            if (wires.Count == 0)
            {
                // Descriptor-less rows (a stateful primary kind): bare
                // output name, nothing to list yet.
                AddAnnotationDetailSpan(grid,
                    MakeAnnotationDetailText(target, "TextFillColorSecondaryBrush"), ref gridRow);
                return grid;
            }

            // Distinct devices in first-appearance order; sources keep
            // their row order within each device group.
            var deviceKeys = new List<string>();
            foreach (var wire in wires)
                if (!deviceKeys.Contains(wire.DeviceKey))
                    deviceKeys.Add(wire.DeviceKey);

            int shown = 0;
            foreach (var key in deviceKeys)
            {
                if (shown >= AnnotationDetailMaxRows)
                    break;
                bool headerEmitted = false;
                foreach (var wire in wires)
                {
                    if (!string.Equals(wire.DeviceKey, key, StringComparison.Ordinal))
                        continue;
                    if (shown >= AnnotationDetailMaxRows)
                        break;

                    if (!headerEmitted)
                    {
                        headerEmitted = true;
                        var header = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Margin = new Thickness(0, gridRow == 0 ? 0 : 5, 0, 1),
                        };
                        var glyph = new TextBlock
                        {
                            FontFamily = new FontFamily("Segoe MDL2 Assets"),
                            FontSize = 11,
                            Text = wire.DeviceGlyph,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 5, 0),
                        };
                        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                        // Device name is prose, not telemetry: Body font.
                        // Explicit MaxWidth: the horizontal StackPanel
                        // measures with infinite width, so Wrap alone
                        // never engages. Wrap, never truncate.
                        var devName = new TextBlock
                        {
                            FontSize = 11,
                            Text = wire.DeviceName,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = Math.Max(80, maxContentWidth - 40),
                        };
                        devName.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                        header.Children.Add(glyph);
                        header.Children.Add(devName);
                        AddAnnotationDetailSpan(grid, header, ref gridRow);
                    }

                    var outCell = MakeAnnotationDetailText(target, "TextFillColorSecondaryBrush");
                    outCell.Margin = new Thickness(14, 0, 8, 0);
                    var arrow = MakeAnnotationDetailText("←", "TextFillColorTertiaryBrush");
                    arrow.Margin = new Thickness(0, 0, 8, 0);
                    var srcCell = MakeAnnotationDetailText(wire.SourceName, "TextFillColorSecondaryBrush");
                    // Explicit MaxWidth: Auto grid columns measure with
                    // infinite width, so Wrap alone never engages.
                    srcCell.TextWrapping = TextWrapping.Wrap;
                    srcCell.MaxWidth = Math.Max(80, maxContentWidth - 160);

                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(outCell, gridRow); Grid.SetColumn(outCell, 0);
                    Grid.SetRow(arrow, gridRow); Grid.SetColumn(arrow, 1);
                    Grid.SetRow(srcCell, gridRow); Grid.SetColumn(srcCell, 2);
                    grid.Children.Add(outCell);
                    grid.Children.Add(arrow);
                    grid.Children.Add(srcCell);
                    gridRow++;
                    shown++;
                }
            }

            if (shown < wires.Count)
            {
                var tail = MakeAnnotationDetailText("+" + (wires.Count - shown), "TextFillColorTertiaryBrush");
                tail.Margin = new Thickness(14, 2, 0, 0);
                AddAnnotationDetailSpan(grid, tail, ref gridRow);
            }
            return grid;
        }

        /// <summary>Compact chip face (user report 2026-07-04: full wiring
        /// text made the stage unreadably busy). The chip carries the
        /// output name plus a +N badge for additional sources; the
        /// structured wiring readout appears in the detail strip on hover
        /// and in the tooltip.</summary>
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

        /// <summary>Bottom-docked callout: hovering a chip mounts the
        /// structured wiring readout here, where width is unlimited. Same
        /// steel container as the chips; content swaps per hovered chip.</summary>
        private void EnsureAnnotationDetailStrip()
        {
            if (_annotationDetailStrip != null)
            {
                if (!AnnotationCanvas.Children.Contains(_annotationDetailStrip))
                    AnnotationCanvas.Children.Add(_annotationDetailStrip);
                return;
            }
            _annotationDetailStrip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                // 6px vertical (was 4): the readout is multi-row now.
                Padding = new Thickness(10, 6, 10, 6),
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
            // Same width budget the positioner will clamp to, minus the
            // strip chrome, so cells wrap inside the border instead of
            // overflowing into the canvas clip on narrow panes.
            double detailBudget = Math.Max(
                104, Math.Max(120, AnnotationCanvas.ActualWidth - 2 * AnnotationEdgeMargin) - 16);
            _annotationDetailStrip.Child = BuildAnnotationDetailContent(row, detailBudget);
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
            // Wrap instead of spilling off a narrow canvas; names never
            // truncate, they wrap inside this cap.
            _annotationDetailStrip.MaxWidth = Math.Max(120, w - 2 * AnnotationEdgeMargin);
            _annotationDetailStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double sw = _annotationDetailStrip.DesiredSize.Width;
            Canvas.SetLeft(_annotationDetailStrip, Math.Max(8, (w - sw) / 2));
            Canvas.SetTop(_annotationDetailStrip, Math.Max(8, h - _annotationDetailStrip.DesiredSize.Height - 10));
        }

        // ─────────────────────────────────────────────
        //  Element construction
        // ─────────────────────────────────────────────

        private AnnotationChip CreateAnnotationChip(MappingItem row, Model3DGroup anchor)
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
            border.ToolTip = BuildAnnotationDetailContent(row);
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
                bars.ProjectionValid = visible;
                bars.Track.Visibility = visible && bars.LevelsLive
                    ? Visibility.Visible : Visibility.Collapsed;
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
                bars.LevelsLive = raw > 0.02 || output > 0.02;
                bars.Track.Visibility = bars.LevelsLive && bars.ProjectionValid
                    ? Visibility.Visible : Visibility.Collapsed;
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
