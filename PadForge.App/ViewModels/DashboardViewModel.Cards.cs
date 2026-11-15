namespace PadForge.ViewModels
{
    /// <summary>The size of Home's controller cards (the approved "keep the
    /// frame" canvas). Auto follows the window: detailed cards while they
    /// fit it, compact ones once they would run past it.</summary>
    public enum HomeCardSize
    {
        Auto = 0,
        Detailed = 1,
        Compact = 2,
    }

    internal static class HomeCardSizes
    {
        /// <summary>The choice a settings file holds. Anything else is
        /// Auto, the choice of a file that holds none.</summary>
        internal static HomeCardSize FromStored(int stored)
            => stored is (int)HomeCardSize.Detailed or (int)HomeCardSize.Compact ? (HomeCardSize)stored : HomeCardSize.Auto;

        /// <summary>Whether detailed cards run past the window: their
        /// grid starts <paramref name="cardsTop"/> down the page, is
        /// <paramref name="detailedHeight"/> tall, and the window shows
        /// <paramref name="windowHeight"/> of the page. A grid that ends
        /// on the window's edge fits.</summary>
        internal static bool RunPast(double cardsTop, double detailedHeight, double windowHeight)
            => cardsTop + detailedHeight > windowHeight + 0.5;

        /// <summary>Whether the cards are compact for a choice.</summary>
        internal static bool IsCompact(HomeCardSize choice, bool detailedRunPast)
            => choice == HomeCardSize.Compact || (choice == HomeCardSize.Auto && detailedRunPast);
    }

    public partial class DashboardViewModel
    {
        private HomeCardSize _homeCards;

        /// <summary>The size of the controller cards, the user's choice.
        /// Kept with the program's settings.</summary>
        public HomeCardSize HomeCards
        {
            get => _homeCards;
            set
            {
                if (SetProperty(ref _homeCards, value)) OnPropertyChanged(nameof(IsCardsCompact));
            }
        }

        private bool _detailedCardsRunPast;

        /// <summary>Whether detailed cards would run past the window as
        /// it is now. The page says so, since only it knows its size.</summary>
        public bool DetailedCardsRunPast
        {
            get => _detailedCardsRunPast;
            set
            {
                if (SetProperty(ref _detailedCardsRunPast, value)) OnPropertyChanged(nameof(IsCardsCompact));
            }
        }

        /// <summary>Whether the cards show compact now.</summary>
        public bool IsCardsCompact => HomeCardSizes.IsCompact(_homeCards, _detailedCardsRunPast);
    }
}
