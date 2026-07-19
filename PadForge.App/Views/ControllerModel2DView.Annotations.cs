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
using PadForge.Models2D;
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
        private const int AnnotationDetailMaxRows = 12;   // wiring rows before the +N tail

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
            // Nintendo rows are raw grid names (RawBtn0, RawAxis1); anchors
            // are keyed by the preview element grammar. Translate first.
            // Raw row names only occur here for Nintendo slots (Extended
            // uses the schematic view, never this canvas).
            if (targetSettingName.StartsWith("Raw", System.StringComparison.Ordinal))
            {
                targetSettingName = NintendoPreviewMap.ToPreview(targetSettingName);
                if (targetSettingName == null)
                    return null;
            }
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

                // HasAnySource, not IsMapped: a stateful primary (Ramp /
                // Incremental) keeps its feeds on PrimaryKindSource's
                // Up/Down/Modifier keys, not the row descriptor, so
                // IsMapped-gated chips hid those rows and their fan-in.
                if (!row.HasAnySource)
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
                  || e.PropertyName == nameof(MappingItem.IsMapped)
                  || e.PropertyName == nameof(MappingItem.HasAnySource))
            {
                QueueAnnotationRebuild();
            }
            else if (e.PropertyName == nameof(MappingItem.SourceDisplayText))
            {
                // Resolved source text arrives after the descriptor
                // (SetResolvedSourceText); refresh in place, no rebuild.
                // Re-layout because the chip width feeds the leader endpoint.
                // The tooltip rebuilds too so it never shows stale wiring.
                if (sender is MappingItem row)
                {
                    var chip = FindAnnotationChip(row);
                    if (chip != null)
                    {
                        chip.Text.Text = CompactChipLabel(row);
                        chip.Border.ToolTip = BuildAnnotationDetailContent(row);
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

        /// <summary>One wiring row for the hover surfaces: identical
        /// contract to the 3D view's AnnotationWireRow. DeviceKey ("" =
        /// unbound / any device) groups rows per device.</summary>
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
        /// (the localized "(Any device)" sentinel for unbound, truncated
        /// GUID for unknown).</summary>
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
                    ? PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice
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
            else if (!row.IsPrimaryDirect)
            {
                // Stateful primary (Ramp / Incremental / InvertOnHold): its
                // feeds are the Up/Down/Modifier keys on PrimaryKindSource,
                // not the row descriptor.
                AppendAnnotationSourceWires(rows, row.PrimaryKindSource);
            }

            foreach (var src in row.ExtraSources)
                AppendAnnotationSourceWires(rows, src);
            return rows;
        }

        /// <summary>Appends one wire row per input this source actually
        /// reads, mirroring the engine's per-kind dispatch (the
        /// collect-descriptors switch in InputService): Incremental and
        /// Ramped read the Up/Down keys, InvertOnHold reads its input plus
        /// the modifier key, everything else reads the descriptor. Inv/Half
        /// prefixes only apply to the descriptor feed; the param feeds are
        /// bare keys.</summary>
        private void AppendAnnotationSourceWires(List<AnnotationWireRow> rows, MappingSourceItem src)
        {
            if (src == null)
                return;
            if (src.UsesUpDownKeys)
            {
                AppendAnnotationParamWire(rows, src, src.ParamUp, src.ParamUpInputChoice);
                AppendAnnotationParamWire(rows, src, src.ParamDown, src.ParamDownInputChoice);
                return;
            }

            string name = (src.SelectedInput?.DisplayName ?? src.Descriptor ?? string.Empty).Trim();
            if (name.Length > 0)
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                if (src.Invert && src.HalfAxis) name = s.Mapping_InvHalf + " " + name;
                else if (src.Invert) name = s.Mapping_Inv + " " + name;
                else if (src.HalfAxis) name = s.Mapping_Half + " " + name;
                AppendAnnotationWire(rows, src.DeviceGuid,
                    src.DisplayDeviceLabel, name);
            }
            if (src.IsInvertOnHoldKind)
                AppendAnnotationParamWire(rows, src, src.ParamModifier, src.ParamModifierInputChoice);
        }

        private void AppendAnnotationParamWire(List<AnnotationWireRow> rows,
            MappingSourceItem src, string descriptor, InputChoice choice)
        {
            string name = (choice?.DisplayName ?? descriptor ?? string.Empty).Trim();
            if (name.Length == 0)
                return;
            AppendAnnotationWire(rows,
                string.IsNullOrEmpty(choice?.DeviceGuid) ? src.DeviceGuid : choice.DeviceGuid,
                choice?.DeviceLabel, name);
        }

        private void AppendAnnotationWire(List<AnnotationWireRow> rows,
            string deviceGuid, string storedLabel, string sourceName)
        {
            ResolveAnnotationDevice(deviceGuid, (storedLabel ?? string.Empty).Trim(),
                out string dn, out string dg);
            rows.Add(new AnnotationWireRow
            {
                DeviceKey = (deviceGuid ?? string.Empty).ToLowerInvariant(),
                DeviceName = dn,
                DeviceGlyph = dg,
                SourceName = sourceName,
            });
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
        /// and the hover detail strip (user reports 2026-07-05): a fan-in
        /// diagram. Sources stack on the left, one row per assigned
        /// source with its device attributed inline, and ONE arrow at the
        /// stack's vertical midpoint points left-to-right into the
        /// output. Cold sources, ember output, per the Ember color
        /// grammar. Caps at AnnotationDetailMaxRows rows with a
        /// locale-neutral "+N" tail; long names wrap, never truncate.</summary>
        private FrameworkElement BuildAnnotationDetailContent(MappingItem row, double maxContentWidth = 480)
        {
            var wires = BuildAnnotationWireRows(row);
            string target = (row.TargetLabel ?? string.Empty).Trim();

            var grid = new Grid { MaxWidth = maxContentWidth };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (wires.Count == 0)
            {
                // Descriptor-less rows (a stateful primary kind): bare
                // output name, nothing to list yet.
                var bare = MakeAnnotationDetailText(target, "TextFillColorSecondaryBrush");
                Grid.SetColumnSpan(bare, 3);
                grid.Children.Add(bare);
                return grid;
            }

            // Width split: the output keeps a bounded right column, the
            // arrow a fixed lane, and the source stack wraps inside the
            // remainder (Auto columns measure infinite, so explicit caps
            // are what make Wrap engage).
            double outBudget = Math.Min(200, Math.Max(70, maxContentWidth * 0.35));
            double srcBudget = Math.Max(80, maxContentWidth - outBudget - 60);

            var stack = new StackPanel();
            int shown = 0;
            foreach (var wire in wires)
            {
                if (shown >= AnnotationDetailMaxRows)
                    break;
                var line = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, shown == 0 ? 0 : 2, 0, 0),
                };
                var glyph = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Text = wire.DeviceGlyph,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0),
                };
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                // Source (cold mono) and its device (Body, tertiary) share
                // one wrapping paragraph so a long pair folds as a unit.
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = srcBudget,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var srcRun = new System.Windows.Documents.Run(wire.SourceName) { FontSize = 11 };
                if (Application.Current.Resources["TelemetryFontFamily"] is FontFamily telemetry)
                    srcRun.FontFamily = telemetry;
                srcRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "ColdBrush");
                var sep = new System.Windows.Documents.Run("  ·  ") { FontSize = 10 };
                sep.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
                var devRun = new System.Windows.Documents.Run(wire.DeviceName) { FontSize = 10 };
                devRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
                text.Inlines.Add(srcRun);
                text.Inlines.Add(sep);
                text.Inlines.Add(devRun);
                line.Children.Add(glyph);
                line.Children.Add(text);
                stack.Children.Add(line);
                shown++;
            }
            if (shown < wires.Count)
            {
                var tail = MakeAnnotationDetailText("+" + (wires.Count - shown), "TextFillColorTertiaryBrush");
                tail.Margin = new Thickness(15, 2, 0, 0);
                stack.Children.Add(tail);
            }

            // ONE arrow from the source stack's vertical midpoint into
            // the output, left to right (the maintainer's spec).
            var arrow = new TextBlock
            {
                Text = "→",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
            };
            if (Application.Current.Resources["TelemetryFontFamily"] is FontFamily mono)
                arrow.FontFamily = mono;
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            var outCell = MakeAnnotationDetailText(target, "EmberBrush");
            outCell.TextWrapping = TextWrapping.Wrap;
            outCell.MaxWidth = outBudget;
            outCell.VerticalAlignment = VerticalAlignment.Center;

            Grid.SetColumn(stack, 0);
            Grid.SetColumn(arrow, 1);
            Grid.SetColumn(outCell, 2);
            grid.Children.Add(stack);
            grid.Children.Add(arrow);
            grid.Children.Add(outCell);
            return grid;
        }

        /// <summary>Compact chip face: the OUTPUT name, nothing else
        /// (user report 2026-07-05: the +N badge read as part of the
        /// name and confused the chip; the fan-in readouts in the
        /// tooltip and detail strip own the source detail).</summary>
        private static string CompactChipLabel(MappingItem row)
        {
            return (row.TargetLabel ?? string.Empty).Trim();
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
