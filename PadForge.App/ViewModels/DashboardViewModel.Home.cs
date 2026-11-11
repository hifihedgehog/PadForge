using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What the Home page (#574) reads beside the engine's state and the
    /// controller cards: the word on the engine's button, the offer for a
    /// device that is connected and not in use, and which extras are on.
    /// </summary>
    public partial class DashboardViewModel
    {
        // ─────────────────────────────────────────────
        //  The engine's button
        // ─────────────────────────────────────────────

        /// <summary>The word on the engine's button: what a click does. Stop
        /// while the engine is on, idle and stopping included, as
        /// <see cref="IsEngineOn"/> counts them, and Start while it is
        /// stopped.</summary>
        public string EngineButtonText => IsEngineOn ? Strings.Instance.Common_Stop : Strings.Instance.Common_Start;

        /// <summary>The line under the engine's state: how many devices are
        /// connected and how many virtual controllers there are. Both are
        /// numbers after a label, so the line needs no plural forms.</summary>
        public string EngineLine => string.Format(Strings.Instance.Home_EngineLine_Format, OnlineDevices, SlotSummaries.Count);

        // ─────────────────────────────────────────────
        //  The offer (Settings > Assignment Prompts)
        // ─────────────────────────────────────────────

        private Guid _homeOfferGuid;
        private string _homeOfferName = string.Empty;
        private string _homeOfferText = string.Empty;

        /// <summary>The device Home is offering a virtual controller of its
        /// own, or Empty. Written by InputService for a device PadForge has
        /// just met that is connected and on no virtual controller. Cleared
        /// by Not Now, when the device goes offline, when it lands on a
        /// virtual controller by any route, and when the prompt's setting is
        /// turned off. Never saved: what is saved is the device's own
        /// HomeOfferPending.</summary>
        public Guid HomeOfferGuid => _homeOfferGuid;

        /// <summary>The offer's sentence, built by the writer so the device's
        /// name is the same localized name the Devices page shows.</summary>
        public string HomeOfferText
        {
            get => _homeOfferText;
            private set => SetProperty(ref _homeOfferText, value ?? string.Empty);
        }

        public bool HasHomeOffer => _homeOfferGuid != Guid.Empty;

        /// <summary>The offer's sentence in three parts, so the page can set
        /// the device's name apart from the words around it: what the
        /// language puts before the name, the name, and what comes after.
        /// A sentence with no place for the name is all lead.</summary>
        public string HomeOfferLead { get; private set; } = string.Empty;

        public string HomeOfferName => _homeOfferName;

        public string HomeOfferTail { get; private set; } = string.Empty;

        private void SetOfferParts()
        {
            string format = Strings.Instance.Home_Offer_Format;
            int at = format.IndexOf("{0}", StringComparison.Ordinal);
            if (!HasHomeOffer)
            {
                HomeOfferLead = string.Empty;
                HomeOfferTail = string.Empty;
            }
            else if (at < 0)
            {
                HomeOfferLead = _homeOfferText;
                HomeOfferTail = string.Empty;
            }
            else
            {
                HomeOfferLead = format.Substring(0, at);
                HomeOfferTail = format.Substring(at + 3);
            }
            OnPropertyChanged(nameof(HomeOfferLead));
            OnPropertyChanged(nameof(HomeOfferName));
            OnPropertyChanged(nameof(HomeOfferTail));
        }

        /// <summary>Raised with the device when the user clicks Not Now.
        /// InputService ends the offer for that device for good.</summary>
        public event EventHandler<Guid> HomeOfferDismissed;

        public void SetHomeOffer(Guid instanceGuid, string deviceName)
        {
            if (instanceGuid == Guid.Empty) { ClearHomeOffer(); return; }
            bool had = HasHomeOffer;
            _homeOfferGuid = instanceGuid;
            _homeOfferName = deviceName ?? string.Empty;
            HomeOfferText = string.Format(Strings.Instance.Home_Offer_Format, _homeOfferName);
            SetOfferParts();
            OnPropertyChanged(nameof(HomeOfferGuid));
            if (!had) OnPropertyChanged(nameof(HasHomeOffer));
        }

        public void ClearHomeOffer()
        {
            if (!HasHomeOffer) return;
            _homeOfferGuid = Guid.Empty;
            _homeOfferName = string.Empty;
            HomeOfferText = string.Empty;
            SetOfferParts();
            OnPropertyChanged(nameof(HomeOfferGuid));
            OnPropertyChanged(nameof(HasHomeOffer));
        }

        private RelayCommand _dismissHomeOfferCommand;

        /// <summary>Not Now. Use It has no command here: it opens the type
        /// bar under its own button, which the page does, and the offer
        /// stays until the device is on a virtual controller, so a type bar
        /// closed with nothing picked leaves it standing.</summary>
        public RelayCommand DismissHomeOfferCommand => _dismissHomeOfferCommand ??= new RelayCommand(() =>
        {
            var guid = _homeOfferGuid;
            if (guid == Guid.Empty) return;
            ClearHomeOffer();
            HomeOfferDismissed?.Invoke(this, guid);
        });

        // ─────────────────────────────────────────────
        //  Extras in use
        // ─────────────────────────────────────────────

        /// <summary>How many extras the Extras page lists. The page's own
        /// list is ExtrasPage.Keys, and a test holds the two to one number.</summary>
        internal const int ExtrasCount = 10;

        /// <summary>How many of the ten extras are on, each read through the
        /// switch its tile on the Extras page shows.</summary>
        public int ExtrasInUseCount =>
            (EnableWebController ? 1 : 0)
            + (HeadTrackingAnyEnabled ? 1 : 0)
            + (EnableGameTelemetry ? 1 : 0)
            + (EnableTwitchChat ? 1 : 0)
            + (EnableRemoteLink ? 1 : 0)
            + (EnableDsuMotionServer ? 1 : 0)
            + (EnableGameModServer ? 1 : 0)
            + (OverlaysAnyEnabled ? 1 : 0)
            + (EnableTouchpadOverlay ? 1 : 0)
            + (ScreenOverlaysAnyEnabled ? 1 : 0);

        /// <summary>Whether Home lists any extra: its Extras in Use heading
        /// and rows show.</summary>
        public bool HasExtrasInUse => ExtrasInUseCount > 0;

        /// <summary>Whether Home says where the other extras are: some are
        /// on and some are not.</summary>
        public bool ShowsExtrasRestLine
        {
            get
            {
                int on = ExtrasInUseCount;
                return on > 0 && on < ExtrasCount;
            }
        }

        /// <summary>Whether Home introduces the Extras page in place of the
        /// list: no extra is on.</summary>
        public bool ShowsExtrasIntro => ExtrasInUseCount == 0;

        /// <summary>The line under the tiles that says how many other extras
        /// there are, in three parts so the page can make the word that
        /// names the Extras page a link: what comes before that word, the
        /// word, and what comes after. The string marks the word with
        /// square brackets. A string with no brackets is all lead.</summary>
        public string ExtrasRestLead => ExtrasRestPart(0);

        public string ExtrasRestLink => ExtrasRestPart(1);

        public string ExtrasRestTail => ExtrasRestPart(2);

        private string ExtrasRestPart(int part)
            => BracketPart(string.Format(Strings.Instance.Home_MoreExtras, ExtrasCount - ExtrasInUseCount), part);

        /// <summary>One of the three parts of a text around its first pair
        /// of square brackets: 0 is what stands before the pair, 1 what the
        /// pair holds, 2 what stands after. A text with no closed pair is
        /// all part 0.</summary>
        internal static string BracketPart(string text, int part)
        {
            text ??= string.Empty;
            int open = text.IndexOf('[');
            int close = open < 0 ? -1 : text.IndexOf(']', open + 1);
            if (close < 0) return part == 0 ? text : string.Empty;
            return part switch
            {
                0 => text.Substring(0, open),
                1 => text.Substring(open + 1, close - open - 1),
                _ => text.Substring(close + 1),
            };
        }

        /// <summary>The Home properties that follow <paramref name="changed"/>.
        /// Called from the one OnPropertyChanged override, in the Extras
        /// part, and raised past it so nothing here calls itself.</summary>
        private void HomeFollows(string changed)
        {
            switch (changed)
            {
                case nameof(IsEngineOn):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(EngineButtonText)));
                    break;
                case nameof(OnlineDevices):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(EngineLine)));
                    break;
                case nameof(EnableWebController):
                case nameof(HeadTrackingEnabled):
                case nameof(HeadTrackingFreeTrack):
                case nameof(HeadTrackingOpenXr):
                case nameof(EnableGameTelemetry):
                case nameof(EnableTwitchChat):
                case nameof(EnableRemoteLink):
                case nameof(EnableDsuMotionServer):
                case nameof(EnableGameModServer):
                case nameof(EnableMenuOverlay):
                case nameof(EnableShiftLayerFlyout):
                case nameof(EnableProfileOverlay):
                case nameof(EnableTouchpadOverlay):
                case nameof(ScreenOverlays):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ExtrasInUseCount)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasExtrasInUse)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowsExtrasRestLine)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowsExtrasIntro)));
                    RaiseExtrasRestParts();
                    break;
            }
        }

        /// <summary>The offer's sentence and the button's word in the new
        /// language. Called from OnCultureChanged.</summary>
        private void RefreshHomeText()
        {
            if (HasHomeOffer)
                HomeOfferText = string.Format(Strings.Instance.Home_Offer_Format, _homeOfferName);
            SetOfferParts();
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(EngineButtonText)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(EngineLine)));
            RaiseExtrasRestParts();
        }

        private void RaiseExtrasRestParts()
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ExtrasRestLead)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ExtrasRestLink)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ExtrasRestTail)));
        }
    }
}
