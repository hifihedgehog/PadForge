using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Controls;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>The size of Home's controller cards (the approved "keep the
    /// frame" canvas): Detailed and Compact hold a size, and Auto follows
    /// the window. On Auto the cards are detailed while their grid ends
    /// inside the window, and compact once it would run past it.
    ///
    /// <para>While detailed cards are on the page, their own height
    /// answers. While compact ones are, the detailed cards are measured
    /// off the page, at the width the grid has, and only when they could
    /// fit: where the compact grid itself runs past the window, the
    /// detailed one does too, since its cards are taller and fewer to a
    /// row.</para></summary>
    public partial class HomePage
    {
        /// <summary>The least width of a detailed card and of a compact
        /// one, and the room between two cards.</summary>
        public const double DetailedCardMinWidth = 470, CompactCardMinWidth = 300, CardGap = 16;

        /// <summary>The least height of the Add Controller tile.</summary>
        public const double AddTileMinHeight = 132;

        private DashboardViewModel _cardsVm;
        private readonly List<SlotSummary> _followedSlots = new();
        private DispatcherTimer _fitTimer;
        private bool _fitPosted;

        /// <summary>Counts every change of the cards themselves: one more
        /// or fewer, a device more or fewer on one, another choice.</summary>
        private int _cardsVersion;

        /// <summary>The window and the cards for which the detailed cards
        /// were last seen to run past. Until one of them changes they still
        /// do, so the page does not measure them again, and a measurement
        /// that differs from the page by a pixel cannot switch the cards
        /// back and forth.</summary>
        private (double Width, double Height, int Version) _ranPastAt = (double.NaN, double.NaN, -1);

        private void WireCardsFit()
        {
            // A pause before the detailed cards are measured off the page:
            // a window being dragged asks many times a second.
            _fitTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _fitTimer.Tick += (_, _) =>
            {
                _fitTimer.Stop();
                ReadCardsFit(mayMeasure: true);
            };
            // The scroll area says when the window shows more or less of the
            // page and when the page grows or shrinks. It says so once the
            // height it shows is the new one, which its SizeChanged is too
            // early for. A scroll alone changes neither.
            HomeScroll.ScrollChanged += (_, e) =>
            {
                if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0
                    || e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0)
                    ReadCardsFit(mayMeasure: false);
            };
            IsVisibleChanged += (_, _) => PostCardsFit();
            DataContextChanged += (_, e) => FollowCards(e.NewValue as DashboardViewModel);
        }

        private void FollowCards(DashboardViewModel vm)
        {
            if (_cardsVm != null)
            {
                _cardsVm.PropertyChanged -= OnCardsChoiceChanged;
                _cardsVm.SlotSummaries.CollectionChanged -= OnCardsListChanged;
            }
            _cardsVm = vm;
            if (vm != null)
            {
                vm.PropertyChanged += OnCardsChoiceChanged;
                vm.SlotSummaries.CollectionChanged += OnCardsListChanged;
            }
            FollowSlots();
            CardsChanged();
        }

        private void FollowSlots()
        {
            foreach (var slot in _followedSlots) slot.PropertyChanged -= OnCardChanged;
            _followedSlots.Clear();
            if (_cardsVm == null) return;
            foreach (var slot in _cardsVm.SlotSummaries)
            {
                slot.PropertyChanged += OnCardChanged;
                _followedSlots.Add(slot);
            }
        }

        private void OnCardsListChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            FollowSlots();
            CardsChanged();
        }

        private void OnCardChanged(object sender, PropertyChangedEventArgs e)
        {
            // What makes a detailed card taller or shorter.
            if (e.PropertyName is nameof(SlotSummary.MappedDeviceCount) or nameof(SlotSummary.WaitHint)) CardsChanged();
        }

        private void OnCardsChoiceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DashboardViewModel.HomeCards) or nameof(DashboardViewModel.ShowAddController)) CardsChanged();
        }

        private void CardsChanged()
        {
            _cardsVersion++;
            PostCardsFit();
        }

        /// <summary>Asks once, after the page is next laid out.</summary>
        private void PostCardsFit()
        {
            if (_fitPosted) return;
            _fitPosted = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _fitPosted = false;
                ReadCardsFit(mayMeasure: false);
            }));
        }

        private void Cards_Checked(object sender, RoutedEventArgs e)
        {
            if (DataContext is DashboardViewModel vm && sender is FrameworkElement { Tag: string name }
                && Enum.TryParse(name, out HomeCardSize size))
                vm.HomeCards = size;
        }

        /// <summary>On Auto, says whether detailed cards run past the
        /// window as the page is now.</summary>
        private void ReadCardsFit(bool mayMeasure)
        {
            var vm = _cardsVm;
            if (vm == null || vm.HomeCards != HomeCardSize.Auto || !CardsAreShown()) return;
            double window = HomeScroll.ViewportHeight, width = SlotsItemsControl.ActualWidth;
            if (!(window > 0) || !(width > 0)) return;
            double top = SlotsItemsControl.TranslatePoint(new Point(0, 0), HomeGround).Y;
            var now = (HomeScroll.ActualWidth, HomeScroll.ActualHeight, _cardsVersion);

            if (!vm.IsCardsCompact)
            {
                if (!HomeCardSizes.RunPast(top, SlotsItemsControl.ActualHeight, window)) return;
                _ranPastAt = now;
                SetRunPast(vm, true);
                return;
            }
            if (now == _ranPastAt || HomeCardSizes.RunPast(top, SlotsItemsControl.ActualHeight, window)) return;
            if (!mayMeasure)
            {
                _fitTimer.Stop();
                _fitTimer.Start();
                return;
            }
            if (!HomeCardSizes.RunPast(top, DetailedCardsHeight(vm, width), window)) SetRunPast(vm, false);
        }

        /// <summary>Whether the cards' grid is shown: the page and every
        /// panel between it and the grid. A page that is hidden is not
        /// laid out, so its sizes are those of the last time it showed.
        /// Each one's own setting is asked, which is the same in a window
        /// and in a picture drawn off the screen.</summary>
        private bool CardsAreShown()
        {
            for (DependencyObject at = SlotsItemsControl; at != null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: not Visibility.Visible }) return false;
                if (ReferenceEquals(at, this)) return true;
            }
            return false;
        }

        /// <summary>The height the grid has with detailed cards at a
        /// width: the cards are made, measured in the grid's own panel
        /// and let go. They stand in no window, so they are given what a
        /// card on the page takes from the page and is measured by.</summary>
        private double DetailedCardsHeight(DashboardViewModel vm, double width)
        {
            if (SlotsItemsControl.Resources["SlotCardDetailed"] is not DataTemplate detailed) return 0;
            var grid = new TileGridPanel { MinItemWidth = DetailedCardMinWidth, Gap = CardGap, FillRows = true };
            TextElement.SetFontFamily(grid, TextElement.GetFontFamily(SlotsItemsControl));
            TextElement.SetFontSize(grid, TextElement.GetFontSize(SlotsItemsControl));
            TextElement.SetFontWeight(grid, TextElement.GetFontWeight(SlotsItemsControl));
            TextElement.SetFontStyle(grid, TextElement.GetFontStyle(SlotsItemsControl));
            TextElement.SetFontStretch(grid, TextElement.GetFontStretch(SlotsItemsControl));
            TextOptions.SetTextFormattingMode(grid, TextOptions.GetTextFormattingMode(SlotsItemsControl));
            grid.FlowDirection = SlotsItemsControl.FlowDirection;
            grid.Language = SlotsItemsControl.Language;
            foreach (var slot in vm.SlotSummaries)
                grid.Children.Add(new ContentPresenter { Content = slot, ContentTemplate = detailed });
            if (vm.ShowAddController) grid.Children.Add(new Border { MinHeight = AddTileMinHeight });
            grid.Measure(new Size(width, double.PositiveInfinity));
            double height = grid.DesiredSize.Height;
            // A card that keeps its controller keeps listening to it.
            foreach (UIElement card in grid.Children)
                if (card is ContentPresenter made) made.Content = null;
            grid.Children.Clear();
            return height;
        }

        /// <summary>Changes the cards' size, and gives the keyboard's
        /// place back to the same part of the same card: the cards are
        /// made again at the other size, and the part that had the
        /// keyboard goes with the old ones.</summary>
        private void SetRunPast(DashboardViewModel vm, bool runPast)
        {
            if (vm.DetailedCardsRunPast == runPast) return;
            bool held = TryGetFocusedCardPart(out int pad, out string part);
            vm.DetailedCardsRunPast = runPast;
            if (held)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusCardPart(pad, part)));
        }

        private bool TryGetFocusedCardPart(out int pad, out string part)
        {
            pad = -1;
            part = null;
            if (Keyboard.FocusedElement is not DependencyObject focused) return false;
            part = AutomationProperties.GetAutomationId(focused);
            for (DependencyObject at = focused; at != null && !ReferenceEquals(at, SlotsItemsControl);
                 at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            {
                if (at is Border { Tag: int index } card && FindCardRoot(index) == card)
                {
                    pad = index;
                    return true;
                }
            }
            return false;
        }

        /// <summary>A controller's card on the page: the Border tagged
        /// with its slot.</summary>
        private Border FindCardRoot(int padIndex)
        {
            for (int i = 0; i < SlotsItemsControl.Items.Count; i++)
            {
                var card = FindChildBorder(SlotsItemsControl.ItemContainerGenerator.ContainerFromIndex(i));
                if (card != null && card.Tag is int index && index == padIndex) return card;
            }
            return null;
        }

        private void FocusCardPart(int padIndex, string part)
        {
            var card = FindCardRoot(padIndex);
            if (card == null) return;
            var target = FindPart(card, part) ?? FindPart(card, "Dash_SlotStage");
            target?.Focus();
        }

        private static UIElement FindPart(DependencyObject parent, string part)
        {
            if (parent == null || string.IsNullOrEmpty(part)) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement { Focusable: true, IsVisible: true } element
                    && AutomationProperties.GetAutomationId(child) == part)
                    return element;
                var found = FindPart(child, part);
                if (found != null) return found;
            }
            return null;
        }
    }
}
