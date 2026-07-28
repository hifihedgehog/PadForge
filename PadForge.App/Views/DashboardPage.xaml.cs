using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using PadForge.Engine;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    public partial class DashboardPage : UserControl
    {
        public DashboardPage()
        {
            InitializeComponent();
            Loaded += (_, _) => WireDragHandlers();
            // The Add Controller tile shares the slot WrapPanel through a
            // CompositeCollection (#175 phase 2 item 3), and its
            // CollectionContainer sits outside the visual tree, so a
            // DataContext binding can't reach SlotSummaries. Hand it the
            // collection directly whenever the VM lands.
            DataContextChanged += (_, e) =>
            {
                if (e.NewValue is ViewModels.DashboardViewModel vm)
                    SlotsCollectionContainer.Collection = vm.SlotSummaries;
            };
        }

        /// <summary>Exposes the "Add Controller" card Border for popup placement.</summary>
        public Border AddControllerCardElement => AddControllerCard;

        /// <summary>Raised when the user clicks the "Add Controller" card.</summary>
        public event EventHandler AddControllerRequested;

        /// <summary>Raised when the user clicks delete on a slot card, carrying the slot index.</summary>
        public event EventHandler<int> DeleteSlotRequested;

        /// <summary>Raised when the user clicks the power button to toggle enabled state.</summary>
        public event EventHandler<(int SlotIndex, bool IsEnabled)> SlotEnabledToggled;

        /// <summary>Raised when the user clicks a type icon to change controller type.</summary>
        public event EventHandler<(int SlotIndex, VirtualControllerType Type)> SlotTypeChangeRequested;

        /// <summary>Raised when the user drags a card to swap with another card (padIndexA, padIndexB).</summary>
        public event EventHandler<(int PadIndexA, int PadIndexB)> SlotSwapRequested;

        /// <summary>Raised when the user drags a card to insert at a new position (sourcePadIndex, targetVisualPos).</summary>
        public event EventHandler<(int SourcePadIndex, int TargetVisualPos)> SlotMoveRequested;

        /// <summary>Raised when the user clicks a slot card to navigate to that controller's page.</summary>
        public event EventHandler<int> SlotCardClicked;

        /// <summary>Raised when the user clicks the engine power toggle.</summary>
        public event EventHandler EngineToggleRequested;

        private void EngineToggle_Click(object sender, RoutedEventArgs e)
        {
            EngineToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        private void AddControllerCard_Click(object sender, MouseButtonEventArgs e)
        {
            AddControllerRequested?.Invoke(this, EventArgs.Empty);
        }

        private void DeleteSlot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                // Destructive-verb guard (#175 phase 2 item 1a): the X asks
                // before the slot and its device assignments go away. The
                // mono detail line names the slot; the body carries the
                // mapped-device count the SlotSummary already holds.
                var summary = btn.DataContext as ViewModels.SlotSummary;
                bool confirmed = ConfirmDialog.Show(
                    Window.GetWindow(this),
                    Strings.Instance.Main_DeleteVC,
                    string.Format(Strings.Instance.Dashboard_DeleteSlotConfirm_Format,
                        summary?.MappedDeviceCount ?? 0),
                    Strings.Instance.Common_Delete,
                    summary?.SlotLabel);
                if (confirmed)
                    DeleteSlotRequested?.Invoke(this, slotIndex);
            }
        }

        private void PowerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                // Invert GROUND TRUTH, not the SlotSummary VM: the summary
                // refreshes on the (now 1 Hz-gated while backgrounded)
                // dashboard lane, and a controller shortcut can flip
                // SlotEnabled while this window is inactive. A click that
                // also activates the window lands before the next publish,
                // and inverting the stale VM value would re-write the
                // current state instead of toggling it.
                SlotEnabledToggled?.Invoke(this,
                    (slotIndex, !PadForge.Common.Input.SettingsManager.SlotEnabled[slotIndex]));
            }
        }

        private void XboxType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.Xbox));
        }

        private void DS4Type_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.PlayStation));
        }

        private void NintendoType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.Nintendo));
        }

        private void ExtendedType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.Extended));
        }

        private void KeyboardMouseType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.KeyboardMouse));
        }

        private void MidiType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                if (DataContext is ViewModels.DashboardViewModel vm && !vm.IsMidiServicesInstalled) return;
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.Midi));
            }
        }

        // ─────────────────────────────────────────────
        //  Dashboard card drag reordering
        // ─────────────────────────────────────────────

        private bool _isDragging;
        private Point _dragStartPoint;
        private Border _dragSourceCard;
        // The template-root element BeginDrag hid (card + aura + wrappers);
        // EndDrag restores exactly this element instead of re-deriving it
        // from a parent chain that the cache wrappers have since changed.
        private FrameworkElement _dragHiddenRoot;

        /// <summary>Topmost element of the slot item's template under its
        /// ContentPresenter, climbing from the card through any wrapper
        /// grids. This is the element that contains BOTH the card and its
        /// glow-host aura sibling.</summary>
        private static FrameworkElement FindDragTemplateRoot(FrameworkElement card)
        {
            FrameworkElement root = null;
            DependencyObject cur = card;
            // Depth cap: the real chain is card -> cache wrapper -> template
            // root -> ContentPresenter. If the tree ever changes shape, stop
            // climbing rather than risk hiding an ancestor panel.
            for (int depth = 0; depth < 4; depth++)
            {
                if (System.Windows.Media.VisualTreeHelper.GetParent(cur) is not FrameworkElement p
                    || p is ContentPresenter)
                    break;
                root = p;
                cur = p;
            }
            return root;
        }
        private int _dragSourcePadIndex;
        private int _dragSourceVisualPos;
        private bool _dragIsSwapMode;
        private int _dragSwapTargetPadIndex = -1;
        private int _dragDropIndex = -1;
        private Border _dragSwapHighlight;
        private CardDragAdorner _dragAdorner;
        private InsertionLineAdorner _insertionAdorner;
        private AdornerLayer _dragAdornerLayer;

        private void WireDragHandlers()
        {
            SlotsItemsControl.PreviewMouseMove += OnDragMove;
            SlotsItemsControl.PreviewMouseLeftButtonUp += OnDragEnd;
            SlotsItemsControl.PreviewKeyDown += OnDragKeyDown;
        }

        private void SlotCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border card)
                card.PreviewMouseLeftButtonDown += OnCardMouseDown;
        }

        private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border card || card.Tag is not int) return;
            if (IsInsideButton(e.OriginalSource as DependencyObject, card)) return;
            _dragStartPoint = e.GetPosition(SlotsItemsControl);
            _dragSourceCard = card;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragSourceCard = null;
                return;
            }

            if (!_isDragging && _dragSourceCard != null)
            {
                var pos = e.GetPosition(SlotsItemsControl);
                if (Math.Abs(pos.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(pos.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    BeginDrag(_dragSourceCard, pos);
                }
                return;
            }

            if (_isDragging)
                UpdateDragPosition(e.GetPosition(SlotsItemsControl));
        }

        private void OnDragEnd(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                EndDrag(false);
            }
            else if (_dragSourceCard != null && _dragSourceCard.Tag is int padIndex)
            {
                // No drag occurred — treat as a click to navigate.
                SlotCardClicked?.Invoke(this, padIndex);
            }
            _dragSourceCard = null;
        }

        private void OnDragKeyDown(object sender, KeyEventArgs e)
        {
            if (_isDragging && e.Key == Key.Escape)
            {
                EndDrag(true);
                e.Handled = true;
            }
        }

        private void BeginDrag(Border card, Point startPos)
        {
            if (card.Tag is not int padIndex) return;

            var cards = GetCardBounds();
            int visualPos = -1;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].PadIndex == padIndex) { visualPos = i; break; }
            }
            if (visualPos < 0) return;

            _isDragging = true;
            _dragSourcePadIndex = padIndex;
            _dragSourceVisualPos = visualPos;
            _dragIsSwapMode = false;
            _dragSwapTargetPadIndex = -1;
            _dragDropIndex = -1;

            // Capture bitmap before hiding the card.
            var snapshot = CaptureCardVisual(card);
            // Hide the whole template root: hiding only the card leaves the
            // glow-host sibling painting a silhouette in the old spot. The
            // card's IMMEDIATE parent is no longer that root (a BitmapCache
            // wrapper Grid sits between them since the perf work), so climb
            // to the topmost element under the item's ContentPresenter and
            // remember it for EndDrag.
            _dragHiddenRoot = FindDragTemplateRoot(card);
            if (_dragHiddenRoot != null) _dragHiddenRoot.Opacity = 0;
            else card.Opacity = 0;

            Mouse.Capture(SlotsItemsControl, CaptureMode.SubTree);

            // Compute mouse offset from card top-left so the adorner doesn't jump.
            var cardTopLeft = card.TranslatePoint(new Point(0, 0), SlotsItemsControl);
            var grabOffset = new Point(startPos.X - cardTopLeft.X, startPos.Y - cardTopLeft.Y);

            _dragAdornerLayer = AdornerLayer.GetAdornerLayer(SlotsItemsControl);
            if (_dragAdornerLayer != null && snapshot != null)
            {
                _dragAdorner = new CardDragAdorner(SlotsItemsControl, snapshot,
                    new Size(card.ActualWidth, card.ActualHeight), grabOffset);
                _dragAdorner.UpdatePosition(startPos);
                _dragAdornerLayer.Add(_dragAdorner);

                var accent = TryFindResource("SystemAccentColorSecondaryBrush") as Brush
                          ?? Brushes.DodgerBlue;
                _insertionAdorner = new InsertionLineAdorner(SlotsItemsControl, accent);
                _dragAdornerLayer.Add(_insertionAdorner);
            }
        }

        private void UpdateDragPosition(Point pos)
        {
            _dragAdorner?.UpdatePosition(pos);

            var cards = GetCardBounds();
            if (cards.Count < 2) return;

            const double edgeFraction = 0.25;

            bool isSwap = false;
            int swapCardIndex = -1;
            int dropIndex = -1;

            // Check if cursor is over any card.
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (pos.X >= c.Left && pos.X <= c.Right && pos.Y >= c.Top && pos.Y <= c.Bottom)
                {
                    double width = c.Right - c.Left;
                    double edgeSize = width * edgeFraction;

                    if (i != _dragSourceVisualPos)
                    {
                        if (pos.X < c.Left + edgeSize)
                        {
                            // Left edge zone — insert before this card.
                            dropIndex = i;
                        }
                        else if (pos.X > c.Right - edgeSize)
                        {
                            // Right edge zone — insert after this card.
                            dropIndex = i + 1;
                        }
                        else
                        {
                            // Middle zone — swap.
                            isSwap = true;
                            swapCardIndex = i;
                        }
                    }
                    else
                    {
                        // Cursor still over the source card. Pin dropIndex
                        // to source's own visual position so the insertion
                        // math resolves to "no move" and the closest-edge
                        // fallback below doesn't pick a far card.
                        dropIndex = _dragSourceVisualPos;
                    }
                    break;
                }
            }

            // If not over any card and not swap, check if between rows or past end.
            if (!isSwap && dropIndex < 0)
            {
                // Find closest insertion point by horizontal distance within the same row,
                // or default to end.
                double bestDist = double.MaxValue;
                dropIndex = cards.Count; // default: append to end

                for (int i = 0; i < cards.Count; i++)
                {
                    var c = cards[i];
                    // Check left edge of each card.
                    double dy = Math.Max(0, Math.Max(c.Top - pos.Y, pos.Y - c.Bottom));
                    double dx = Math.Abs(c.Left - pos.X);
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < bestDist) { bestDist = dist; dropIndex = i; }

                    // Check right edge of each card.
                    dx = Math.Abs(c.Right - pos.X);
                    dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < bestDist) { bestDist = dist; dropIndex = i + 1; }
                }
            }

            // ── Type-group validation ──
            // Block cross-type reordering: only allow swap/insert within the same type group.
            var sourceType = GetSlotOutputType(_dragSourcePadIndex);

            if (isSwap)
            {
                if (GetSlotOutputType(cards[swapCardIndex].PadIndex) != sourceType)
                    isSwap = false;
            }

            if (!isSwap && dropIndex >= 0)
            {
                if (!IsInsertionInSameTypeGroup(dropIndex, sourceType, cards))
                    dropIndex = -1;
            }

            _dragIsSwapMode = isSwap;

            if (isSwap)
            {
                _dragDropIndex = -1;
                _dragSwapTargetPadIndex = cards[swapCardIndex].PadIndex;
                _insertionAdorner?.Update(0, 0, 0, 0, false);
                SetSwapHighlight(cards[swapCardIndex].PadIndex);
            }
            else
            {
                _dragDropIndex = dropIndex;
                _dragSwapTargetPadIndex = -1;
                ClearSwapHighlight();

                bool noMove = dropIndex < 0 || dropIndex == _dragSourceVisualPos || dropIndex == _dragSourceVisualPos + 1;
                if (noMove || _insertionAdorner == null)
                {
                    _insertionAdorner?.Update(0, 0, 0, 0, false);
                }
                else
                {
                    // Draw a vertical insertion line between cards.
                    double lineX, lineTop, lineBottom;

                    if (dropIndex <= 0)
                    {
                        var first = cards[0];
                        lineX = first.Left - 2;
                        lineTop = first.Top;
                        lineBottom = first.Bottom;
                    }
                    else if (dropIndex >= cards.Count)
                    {
                        var last = cards[cards.Count - 1];
                        lineX = last.Right + 2;
                        lineTop = last.Top;
                        lineBottom = last.Bottom;
                    }
                    else
                    {
                        var prev = cards[dropIndex - 1];
                        var next = cards[dropIndex];

                        // Same row? Vertical line between them.
                        if (Math.Abs(prev.Top - next.Top) < 10)
                        {
                            lineX = (prev.Right + next.Left) / 2;
                            lineTop = Math.Min(prev.Top, next.Top);
                            lineBottom = Math.Max(prev.Bottom, next.Bottom);
                        }
                        else
                        {
                            // Different rows — line at end of previous row.
                            lineX = prev.Right + 2;
                            lineTop = prev.Top;
                            lineBottom = prev.Bottom;
                        }
                    }

                    _insertionAdorner?.Update(lineX, lineTop, lineBottom - lineTop, 3, true);
                }
            }
        }

        private void EndDrag(bool cancel)
        {
            // Drop the drag flag BEFORE releasing capture: Mouse.Capture(null)
            // can deliver a pending mouse-up synchronously, re-entering
            // OnDragEnd, which then nulls _dragSourceCard under our feet.
            // The old order plus a missing brace on the guard below made the
            // resumed outer call dereference the nulled card (user-reported
            // NullReferenceException at mouse-up, 2026-07-04).
            _isDragging = false;
            Mouse.Capture(null);

            if (_dragSourceCard != null)
            {
                if (_dragHiddenRoot != null) _dragHiddenRoot.Opacity = 1;
                _dragSourceCard.Opacity = 1;
            }
            _dragHiddenRoot = null;

            ClearSwapHighlight();

            if (_dragAdornerLayer != null)
            {
                if (_dragAdorner != null) _dragAdornerLayer.Remove(_dragAdorner);
                if (_insertionAdorner != null) _dragAdornerLayer.Remove(_insertionAdorner);
            }
            _dragAdorner = null;
            _insertionAdorner = null;
            _dragAdornerLayer = null;

            if (!cancel)
            {
                if (_dragIsSwapMode && _dragSwapTargetPadIndex >= 0)
                {
                    int srcPad = _dragSourcePadIndex;
                    int tgtPad = _dragSwapTargetPadIndex;
                    Dispatcher.BeginInvoke(new Action(() =>
                        SlotSwapRequested?.Invoke(this, (srcPad, tgtPad))));
                }
                else if (!_dragIsSwapMode && _dragDropIndex >= 0)
                {
                    int targetGlobalPos;
                    if (_dragDropIndex <= _dragSourceVisualPos)
                        targetGlobalPos = _dragDropIndex;
                    else if (_dragDropIndex <= _dragSourceVisualPos + 1)
                        targetGlobalPos = _dragSourceVisualPos; // no move
                    else
                        targetGlobalPos = _dragDropIndex - 1;

                    if (targetGlobalPos != _dragSourceVisualPos)
                    {
                        // Translate global dashboard position to group-local
                        // (count cards in earlier groups). InputService.MoveSlot
                        // expects an index into the source group's order list.
                        var sourceType = GetSlotOutputType(_dragSourcePadIndex);
                        var cardsAtDrop = GetCardBounds();
                        int startOfGroup = -1;
                        for (int i = 0; i < cardsAtDrop.Count; i++)
                        {
                            if (GetSlotOutputType(cardsAtDrop[i].PadIndex) == sourceType)
                            {
                                startOfGroup = i;
                                break;
                            }
                        }
                        if (startOfGroup >= 0)
                        {
                            int srcPad = _dragSourcePadIndex;
                            int tgtGroupLocalPos = targetGlobalPos - startOfGroup;
                            Dispatcher.BeginInvoke(new Action(() =>
                                SlotMoveRequested?.Invoke(this, (srcPad, tgtGroupLocalPos))));
                        }
                    }
                }
            }

            _dragSourceCard = null;
        }

        // ── Helpers ──

        private VirtualControllerType GetSlotOutputType(int padIndex)
        {
            foreach (var item in SlotsItemsControl.Items)
                if (item is ViewModels.SlotSummary s && s.PadIndex == padIndex)
                    return s.OutputType;
            return VirtualControllerType.Xbox;
        }

        private bool IsInsertionInSameTypeGroup(int insertionVisualPos, VirtualControllerType sourceType, List<CardBounds> cards)
        {
            if (insertionVisualPos < 0) return false;
            if (insertionVisualPos > 0 && GetSlotOutputType(cards[insertionVisualPos - 1].PadIndex) == sourceType)
                return true;
            if (insertionVisualPos < cards.Count && GetSlotOutputType(cards[insertionVisualPos].PadIndex) == sourceType)
                return true;
            return false;
        }

        private record struct CardBounds(int PadIndex, double Left, double Top, double Right, double Bottom);

        private List<CardBounds> GetCardBounds()
        {
            var result = new List<CardBounds>();
            // Walk the ItemsControl visual tree to find card Borders with Tag.
            for (int i = 0; i < SlotsItemsControl.Items.Count; i++)
            {
                var container = SlotsItemsControl.ItemContainerGenerator.ContainerFromIndex(i);
                var card = FindChildBorder(container);
                if (card != null && card.Tag is int padIndex)
                {
                    try
                    {
                        var transform = card.TransformToVisual(SlotsItemsControl);
                        var topLeft = transform.Transform(new Point(0, 0));
                        result.Add(new CardBounds(padIndex, topLeft.X, topLeft.Y,
                            topLeft.X + card.ActualWidth, topLeft.Y + card.ActualHeight));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Dashboard] GetCardBounds error: {ex.Message}");
                    }
                }
            }
            return result;
        }

        private static Border FindChildBorder(DependencyObject parent)
        {
            if (parent == null) return null;
            if (parent is Border b && b.Tag is int) return b;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var found = FindChildBorder(VisualTreeHelper.GetChild(parent, i));
                if (found != null) return found;
            }
            return null;
        }

        private static ImageSource CaptureCardVisual(Border card)
        {
            if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return null;
            var dpi = VisualTreeHelper.GetDpi(card);
            // Add 2 physical pixels of padding to prevent edge clipping at high DPI.
            int w = (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX) + 4;
            int h = (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY) + 4;
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            // Render through a VisualBrush: RenderTargetBitmap.Render(card)
            // bakes in the card's RenderTransform, and the hover lift is at
            // -2px when a drag starts, which cut the ghost's top edge
            // (user-reported). The brush normalizes the content bounds.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(card) { Stretch = Stretch.None },
                    null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            }
            rtb.Render(dv);
            return rtb;
        }

        private static bool IsInsideButton(DependencyObject source, DependencyObject boundary)
        {
            var current = source;
            while (current != null && current != boundary)
            {
                if (current is Button) return true;
                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        private void SetSwapHighlight(int padIndex)
        {
            if (_dragSwapHighlight != null && _dragSwapHighlight.Tag is int prevPad && prevPad != padIndex)
                ClearSwapHighlight();

            for (int i = 0; i < SlotsItemsControl.Items.Count; i++)
            {
                var container = SlotsItemsControl.ItemContainerGenerator.ContainerFromIndex(i);
                var card = FindChildBorder(container);
                if (card != null && card.Tag is int idx && idx == padIndex)
                {
                    var accent = TryFindResource("SystemAccentColorSecondaryBrush") as Brush
                              ?? Brushes.DodgerBlue;
                    card.BorderBrush = accent;
                    _dragSwapHighlight = card;
                    break;
                }
            }
        }

        private void ClearSwapHighlight()
        {
            if (_dragSwapHighlight == null) return;
            // ClearValue, not a hardcoded Transparent. A local value set this
            // way outranks the Style setter and every trigger on the card for
            // the rest of its life, so the border a theme or hover state was
            // supposed to draw stayed invisible after the first drag. Clearing
            // the local value hands control back to the style.
            _dragSwapHighlight.ClearValue(Border.BorderBrushProperty);
            _dragSwapHighlight = null;
        }

        // ── Adorners ──

        private class CardDragAdorner : Adorner
        {
            private readonly ImageBrush _brush;
            private readonly Size _size;
            private readonly Point _grabOffset;
            private Point _position;

            public CardDragAdorner(UIElement adornedElement, ImageSource snapshot, Size cardSize, Point grabOffset)
                : base(adornedElement)
            {
                _brush = new ImageBrush(snapshot);
                _size = cardSize;
                _grabOffset = grabOffset;
                IsHitTestVisible = false;
            }

            public void UpdatePosition(Point pos)
            {
                _position = pos;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(_brush, null,
                    new Rect(
                        _position.X - _grabOffset.X,
                        _position.Y - _grabOffset.Y,
                        _size.Width, _size.Height));
            }
        }

        /// <summary>Draws a vertical accent line at the insertion point between cards.</summary>
        private class InsertionLineAdorner : Adorner
        {
            private readonly Brush _brush;
            private double _x, _y, _height, _width;
            private bool _visible;

            public InsertionLineAdorner(UIElement adornedElement, Brush accentBrush)
                : base(adornedElement)
            {
                _brush = accentBrush;
                IsHitTestVisible = false;
            }

            public void Update(double x, double y, double height, double width, bool visible)
            {
                _x = x; _y = y; _height = height; _width = width; _visible = visible;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (!_visible) return;
                dc.DrawRectangle(_brush, null, new Rect(_x - _width / 2, _y, _width, _height));
            }
        }
    }
}
