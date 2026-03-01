using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernWpf.Controls;
using PadForge.Engine;

namespace PadForge.Views
{
    public partial class DashboardPage : UserControl
    {
        public DashboardPage()
        {
            InitializeComponent();
            Loaded += (_, _) => WireDragHandlers();
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
                DeleteSlotRequested?.Invoke(this, slotIndex);
        }

        private void PowerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                if (btn.DataContext is ViewModels.SlotSummary summary)
                    SlotEnabledToggled?.Invoke(this, (slotIndex, !summary.IsEnabled));
            }
        }

        private void XboxType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                if (DataContext is ViewModels.DashboardViewModel vm && !vm.IsViGEmInstalled) return;
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.Xbox360));
            }
        }

        private void DS4Type_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                if (DataContext is ViewModels.DashboardViewModel vm && !vm.IsViGEmInstalled) return;
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.DualShock4));
            }
        }

        private void VJoyType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int slotIndex)
            {
                if (DataContext is ViewModels.DashboardViewModel vm && !vm.IsVJoyInstalled) return;
                SlotTypeChangeRequested?.Invoke(this, (slotIndex, VirtualControllerType.VJoy));
            }
        }

        // ─────────────────────────────────────────────
        //  Dashboard card drag reordering
        // ─────────────────────────────────────────────

        private bool _isDragging;
        private Point _dragStartPoint;
        private Border _dragSourceCard;
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
            if (_isDragging) EndDrag(false);
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
            card.Opacity = 0;

            Mouse.Capture(SlotsItemsControl, CaptureMode.SubTree);

            _dragAdornerLayer = AdornerLayer.GetAdornerLayer(SlotsItemsControl);
            if (_dragAdornerLayer != null && snapshot != null)
            {
                _dragAdorner = new CardDragAdorner(SlotsItemsControl, snapshot,
                    new Size(card.ActualWidth, card.ActualHeight));
                _dragAdorner.UpdatePosition(startPos);
                _dragAdornerLayer.Add(_dragAdorner);

                var accent = TryFindResource("SystemControlHighlightAccentBrush") as Brush
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
            Mouse.Capture(null);
            _isDragging = false;

            if (_dragSourceCard != null)
                _dragSourceCard.Opacity = 1;

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
                    int targetVisualPos;
                    if (_dragDropIndex <= _dragSourceVisualPos)
                        targetVisualPos = _dragDropIndex;
                    else if (_dragDropIndex <= _dragSourceVisualPos + 1)
                        targetVisualPos = _dragSourceVisualPos; // no move
                    else
                        targetVisualPos = _dragDropIndex - 1;

                    if (targetVisualPos != _dragSourceVisualPos)
                    {
                        int srcPad = _dragSourcePadIndex;
                        int tgtPos = targetVisualPos;
                        Dispatcher.BeginInvoke(new Action(() =>
                            SlotMoveRequested?.Invoke(this, (srcPad, tgtPos))));
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
            return VirtualControllerType.Xbox360;
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
                    catch { }
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
            int w = (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            rtb.Render(card);
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
                    var accent = TryFindResource("SystemControlHighlightAccentBrush") as Brush
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
            _dragSwapHighlight.BorderBrush = Brushes.Transparent;
            _dragSwapHighlight = null;
        }

        // ── Adorners ──

        private class CardDragAdorner : Adorner
        {
            private readonly ImageBrush _brush;
            private readonly Size _size;
            private Point _position;

            public CardDragAdorner(UIElement adornedElement, ImageSource snapshot, Size cardSize)
                : base(adornedElement)
            {
                _brush = new ImageBrush(snapshot);
                _size = cardSize;
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
                        _position.X - _size.Width / 2,
                        _position.Y - _size.Height / 2,
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
