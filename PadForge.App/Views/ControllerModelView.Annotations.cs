// Annotation overlay for the 3D controller preview (#175 roadmap 1).
//
// A 2D Canvas sibling of the HelixViewport3D carries one chip per mapped
// ButtonMap-backed row (steel chip at the canvas edge, 1px cold leader
// line to the control's projected anchor, the chip lit ember while its
// control's output is on) plus two slim trigger level bars (cold raw
// beside ember out). Off by default, session-only state.
//
// Rules baked in: no storyboards, no Effects, all live state is plain
// brush/property swaps. Re-projection is timer-driven (150 ms), never
// per-frame. Bar heights and chip lights update at render rate.

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

        private const double AnnotationChipHeight = LegendColumn.ChipHeight;
        private const double AnnotationEdgeMargin = LegendColumn.EdgeMargin;
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
        //  Per-chip bookkeeping
        // ─────────────────────────────────────────────

        private sealed class AnnotationChip
        {
            public MappingItem Row;
            public Model3DGroup Anchor;
            public Border Border;
            public TextBlock Text;
            public Line Leader;
            /// <summary>The step the tag is lit at, by its control's
            /// output: 0 at rest, <see cref="TagLight.Steps"/> fully on.</summary>
            public int Step;
            public Point Projected;
            public bool LeftColumn;
            public bool Visible;
        }

        // ─────────────────────────────────────────────
        //  Build / teardown
        // ─────────────────────────────────────────────

        /// <summary>Rebuilds every chip from the current model + Mappings.
        /// Cheap no-op while disabled or unbound.</summary>
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

                // HasAnySource, not IsMapped: a stateful primary (Ramp /
                // Incremental) keeps its feeds on PrimaryKindSource's
                // Up/Down/Modifier keys, not the row descriptor, so
                // IsMapped-gated chips hid those rows and their fan-in.
                if (!row.HasAnySource)
                    continue;
                _annotationChips.Add(CreateAnnotationChip(row, anchor));
            }

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
            AnnotationCanvas.Children.Clear();
        }

        private void OnAnnotationMappingsChanged(object sender, NotifyCollectionChangedEventArgs e)
            => QueueAnnotationRebuild();

        private void OnAnnotationRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_annotationsEnabled)
                return;

            if (e.PropertyName == nameof(MappingItem.SourceDescriptor)
                  || e.PropertyName == nameof(MappingItem.IsMapped)
                  || e.PropertyName == nameof(MappingItem.HasAnySource))
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
            // Nintendo rows are raw grid names (RawBtn0, RawAxis1); the
            // model's ButtonMap is keyed by the preview element grammar.
            // Translate first, mirroring the 2D ResolveAnnotationAnchor.
            if (targetSettingName.StartsWith("Raw", System.StringComparison.Ordinal))
            {
                targetSettingName = Models2D.NintendoPreviewMap.ToPreview(targetSettingName, _vm?.ProfileId);
                if (targetSettingName == null)
                    return null;
            }
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
            public Geometry DeviceShape;
            public string SourceName;
        }

        /// <summary>Device name + class glyph for a source. The stored
        /// label wins (it survives disconnects); the slot roster
        /// (_vm.MappedDevices) fills gaps and supplies the DeviceTypeGlyph
        /// vocabulary; the fallbacks mirror InputService.ResolveDeviceLabel
        /// (the localized "(Any device)" sentinel for unbound, truncated
        /// GUID for unknown).</summary>
        private void ResolveAnnotationDevice(string deviceGuid, string storedLabel,
            out string name, out string glyph, out Geometry shape)
        {
            glyph = "\uE7FC"; // controller class, the roster's own default
            shape = null;
            name = storedLabel ?? string.Empty;
            if (!string.IsNullOrEmpty(deviceGuid) && _vm != null
                && Guid.TryParse(deviceGuid, out var g))
            {
                foreach (var dev in _vm.MappedDevices)
                {
                    if (dev == null || dev.InstanceGuid != g)
                        continue;
                    glyph = dev.TypeGlyph;
                    shape = dev.TypeShape;
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
                // A Toggle (#461) or Rapid Trigger (#482) primary says so, the
                // way Inv. and Half mark an inverted or half-axis source.
                if (row.PrimaryKindSource?.IsToggleKind == true)
                    primary = PadForge.Resources.Strings.Strings.Instance.Pad_Mapping_Kind_Toggle + " " + primary;
                else if (row.PrimaryKindSource?.IsRapidTriggerKind == true)
                    primary = PadForge.Resources.Strings.Strings.Instance.Pad_Mapping_Kind_RapidTrigger + " " + primary;
                ResolveAnnotationDevice(row.PrimarySourceDeviceGuid,
                    (row.PrimarySourceDeviceLabel ?? string.Empty).Trim(),
                    out string dn, out string dg, out Geometry ds);
                rows.Add(new AnnotationWireRow
                {
                    DeviceKey = (row.PrimarySourceDeviceGuid ?? string.Empty).ToLowerInvariant(),
                    DeviceName = dn,
                    DeviceGlyph = dg,
                    DeviceShape = ds,
                    SourceName = primary,
                });
            }
            else if (!row.IsPrimaryDescriptor)
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
        /// Ramped read the Up/Down keys, InvertOnHold reads only its modifier
        /// key, everything else reads the descriptor. Inv/Half
        /// prefixes only apply to the descriptor feed; the param feeds are
        /// bare keys.</summary>
        private void AppendAnnotationSourceWires(List<AnnotationWireRow> rows, MappingSourceItem src)
        {
            if (src == null)
                return;
            if (src.UsesUpDownKeys)
            {
                AppendAnnotationParamWire(rows, src.ParamUp, src.ParamUpReadDevice, src.ParamUpInputChoice);
                AppendAnnotationParamWire(rows, src.ParamDown, src.ParamDownReadDevice, src.ParamDownInputChoice);
                return;
            }
            if (src.IsInvertOnHoldKind)
            {
                // The descriptor a modifier kept from its source is no input.
                AppendAnnotationParamWire(rows, src.ParamModifier, src.ParamModifierReadDevice,
                    src.ParamModifierInputChoice);
                return;
            }

            string name = (src.SelectedInput?.DisplayName ?? src.Descriptor ?? string.Empty).Trim();
            if (name.Length > 0)
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                if (src.Invert && src.HalfAxis) name = s.Mapping_InvHalf + " " + name;
                else if (src.Invert) name = s.Mapping_Inv + " " + name;
                else if (src.HalfAxis) name = s.Mapping_Half + " " + name;
                if (src.IsToggleKind) name = s.Pad_Mapping_Kind_Toggle + " " + name;
                else if (src.IsRapidTriggerKind) name = s.Pad_Mapping_Kind_RapidTrigger + " " + name;
                AppendAnnotationWire(rows, src.DeviceGuid,
                    src.DisplayDeviceLabel, name);
            }
        }

        /// <summary>A key's wire, under the controller the key reads
        /// (<paramref name="readDevice"/>, empty for any controller).</summary>
        private void AppendAnnotationParamWire(List<AnnotationWireRow> rows,
            string descriptor, string readDevice, InputChoice choice)
        {
            string name = (choice?.DisplayName ?? descriptor ?? string.Empty).Trim();
            if (name.Length == 0)
                return;
            AppendAnnotationWire(rows, readDevice, choice?.DeviceLabel, name);
        }

        private void AppendAnnotationWire(List<AnnotationWireRow> rows,
            string deviceGuid, string storedLabel, string sourceName)
        {
            ResolveAnnotationDevice(deviceGuid, (storedLabel ?? string.Empty).Trim(),
                out string dn, out string dg, out Geometry ds);
            rows.Add(new AnnotationWireRow
            {
                DeviceKey = (deviceGuid ?? string.Empty).ToLowerInvariant(),
                DeviceName = dn,
                DeviceGlyph = dg,
                DeviceShape = ds,
                SourceName = sourceName,
            });
        }

        /// <summary>The wiring readout's text at 12: the body face with
        /// tabular figures.</summary>
        private static TextBlock MakeAnnotationDetailText(string text, string brushKey)
        {
            var tb = PadForge.Common.TextFaces.Numbers(new TextBlock { FontSize = 12, Text = text });
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
                var glyph = new PadForge.Controls.KindIcon
                {
                    FontSize = 10,
                    Glyph = wire.DeviceGlyph,
                    Shape = wire.DeviceShape,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0),
                };
                glyph.SetResourceReference(PadForge.Controls.KindIcon.ForegroundProperty, "TextFillColorTertiaryBrush");
                // Source (cold) and its device (tertiary) share one
                // wrapping paragraph so a long pair folds as a unit.
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = srcBudget,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var srcRun = PadForge.Common.TextFaces.Numbers(new System.Windows.Documents.Run(wire.SourceName) { FontSize = 12 });
                srcRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "ColdBrush");
                var sep = new System.Windows.Documents.Run("  ·  ") { FontSize = 12 };
                sep.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
                var devRun = new System.Windows.Documents.Run(wire.DeviceName) { FontSize = 12 };
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
            PadForge.Common.TextFaces.Body(arrow);
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

        private AnnotationChip CreateAnnotationChip(MappingItem row, Model3DGroup anchor)
        {
            var text = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Text = CompactChipLabel(row),
            };
            PadForge.Common.TextFaces.Numbers(text);
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

            AnnotationCanvas.Children.Add(leader);
            AnnotationCanvas.Children.Add(border);

            var chip = new AnnotationChip
            {
                Row = row,
                Anchor = anchor,
                Border = border,
                Text = text,
                Leader = leader,
            };
            LightChip(chip, ChipStep(row.TargetSettingName));
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
                    continue;

                chip.Projected = p.Value;
                // Side by projected position, not model X, so the split stays
                // correct after the user spins the model 180 degrees.
                chip.LeftColumn = p.Value.X < w / 2;
                (chip.LeftColumn ? leftColumn : rightColumn).Add(chip);
            }

            LayoutAnnotationColumn(leftColumn, leftSide: true, w, h);
            LayoutAnnotationColumn(rightColumn, leftSide: false, w, h);
        }

        /// <summary>How far down the buttons in the picture's corners
        /// reach: this view's own at the right, and the page's view switch
        /// at the left, which stands in the same band. Nothing before the
        /// view is laid out.</summary>
        private double CornerBottom()
            => CornerTools.ActualHeight > 0 ? CornerTools.Margin.Top + CornerTools.ActualHeight : 0;

        /// <summary>Places a column's chips, each beside its control where
        /// it can stand, clear of the chips around it and of the buttons in
        /// the picture's corner (<see cref="LegendColumn.Tops"/>), and
        /// draws each chip's leader to its control.</summary>
        private void LayoutAnnotationColumn(List<AnnotationChip> chips, bool leftSide, double w, double h)
        {
            if (chips.Count == 0)
                return;

            chips.Sort((a, b) => a.Projected.Y.CompareTo(b.Projected.Y));

            var anchors = new double[chips.Count];
            for (int i = 0; i < anchors.Length; i++)
                anchors[i] = chips[i].Projected.Y;
            double[] y = LegendColumn.Tops(anchors, h, CornerBottom());

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
                }
                else
                {
                    chip.Border.ClearValue(Canvas.LeftProperty);
                    Canvas.SetRight(chip.Border, AnnotationEdgeMargin);
                    nearEdgeX = w - AnnotationEdgeMargin - chipWidth;
                }

                double midY = y[i] + AnnotationChipHeight / 2;
                chip.Leader.X1 = chip.Projected.X;
                chip.Leader.Y1 = chip.Projected.Y;
                chip.Leader.X2 = nearEdgeX;
                chip.Leader.Y2 = midY;
            }
        }

        // ─────────────────────────────────────────────
        //  Live state (tick + render hooks)
        // ─────────────────────────────────────────────

        /// <summary>150 ms tick: re-project. The timer is the PRIMARY
        /// re-projection trigger.
        /// Wheel zoom, pan and yaw/pitch write cam.Position / rotation
        /// angles directly and bypass Helix's CameraController, so
        /// CameraChanged may never fire.</summary>
        private void AnnotationTick(object sender, EventArgs e)
        {
            if (!_annotationsEnabled || _vm == null || _currentModel == null)
                return;
            // Retained visibility-toggled page: keep the 6.7 Hz projection
            // off hidden surfaces. Chips recompute from live state each
            // tick, so the first visible tick catches up. Iconic gate:
            // IsVisible stays TRUE while minimized.
            if (!IsVisible || PadForge.Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;

            if (!_annotationDragHidden)
                ReprojectAnnotations();
        }

        /// <summary>Lights each tag by its control's output, in the frame
        /// the picture lights the control: an ember edge and name while a
        /// button is on, a share of ember that grows with a trigger's pull
        /// and a stick axis's deflection (<see cref="TagLight"/>), and
        /// steel at rest. One light for one state: a press used to flash
        /// the tag blue for 250 ms while a dot beside it showed the output,
        /// and the flash went out with the button still held (owner,
        /// 2026-11-14). A tag's brushes change only when its step does.</summary>
        private void UpdateAnnotationChipLights()
        {
            if (!_annotationsEnabled || _vm == null)
                return;
            foreach (var chip in _annotationChips)
                LightChip(chip, ChipStep(chip.Row.TargetSettingName));
        }

        private static void LightChip(AnnotationChip chip, int step)
        {
            if (chip.Step == step)
                return;
            chip.Step = step;
            TagLight.Paint(chip.Border, chip.Text, step);
        }

        /// <summary>The step a tag is lit at, from its control at the
        /// virtual controller's output: a trigger's and a stick axis's
        /// level, and a button's state on the view model. A Nintendo row's
        /// raw name (RawBtn0) is read as the control it is drawn as, as its
        /// anchor is: unread, every tag of that slot stayed dark, as the 2D
        /// view's did before it read them.</summary>
        private int ChipStep(string target)
        {
            if (target == null || _vm == null)
                return 0;
            if (target.StartsWith("Raw", StringComparison.Ordinal))
            {
                target = Models2D.NintendoPreviewMap.ToPreview(target, _vm.ProfileId);
                if (target == null)
                    return 0;
            }
            return TagLight.StepOf(TagLight.AnalogLevel(_vm, target) ?? (GetButtonState(target) ? 1 : 0));
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
