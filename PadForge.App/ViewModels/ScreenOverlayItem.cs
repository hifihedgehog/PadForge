using System;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Views;

namespace PadForge.ViewModels
{
    /// <summary>
    /// One Screen Overlay's group on the Dashboard card (#560): its switch,
    /// opacity, place and size. What it shows lives with the keyboard screens'
    /// choices, by its number. The number is its identity, and it stays through
    /// removing another overlay, so a macro that names overlay 3 keeps meaning
    /// the same window.
    /// </summary>
    public sealed class ScreenOverlayItem : FiniteObservableObject
    {
        public const double DefaultOpacity = 0.8;
        public const double DefaultWidth = 320, DefaultHeight = 240;
        public const double MinWidth = 80, MinHeight = 60;

        private readonly DashboardViewModel _owner;

        internal ScreenOverlayItem(DashboardViewModel owner, int number)
        {
            _owner = owner;
            Number = number;
        }

        public int Number { get; }

        /// <summary>The card's title on the Screen Overlay page (#574), "Overlay
        /// 2": the page's own title names the feature. Macros, the dialog's
        /// title and the status line keep the full name
        /// (KeyboardScreenService.OverlayName), where the page is not in
        /// sight.</summary>
        public string Title => string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_ScreenOverlay_Number_Format, Number);

        /// <summary>One overlay needs no title or Remove button of its own.</summary>
        public bool OneOfSeveral => _owner.ScreenOverlays.Count > 1;

        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set { if (SetProperty(ref _enabled, value)) _owner.ScreenOverlayEdited(); }
        }

        private double _opacity = DefaultOpacity;

        /// <summary>Opacity of what the overlay shows (0.0–1.0).</summary>
        public double Opacity
        {
            get => _opacity;
            set
            {
                if (!SetProperty(ref _opacity, Math.Clamp(value, 0.0, 1.0))) return;
                OnPropertyChanged(nameof(OpacityPercent));
                _owner.ScreenOverlayEdited();
            }
        }

        /// <summary>Opacity as 0–100 integer percentage for NumberBox binding.</summary>
        public int OpacityPercent
        {
            get => (int)Math.Round(_opacity * 100);
            set => Opacity = value / 100.0;
        }

        private int _monitor;

        /// <summary>Monitor index for the overlay (0 = primary).</summary>
        public int Monitor
        {
            get => _monitor;
            set { if (SetProperty(ref _monitor, value)) _owner.ScreenOverlayEdited(); }
        }

        private double _left = OverlayPlacement.NoPlace;

        /// <summary>The window's left edge, or <see cref="OverlayPlacement.NoPlace"/>
        /// with no saved place yet.</summary>
        public double Left
        {
            get => _left;
            set { if (SetProperty(ref _left, value)) _owner.ScreenOverlayEdited(); }
        }

        private double _top = OverlayPlacement.NoPlace;

        /// <summary>The window's top edge, or <see cref="OverlayPlacement.NoPlace"/>
        /// with no saved place yet.</summary>
        public double Top
        {
            get => _top;
            set { if (SetProperty(ref _top, value)) _owner.ScreenOverlayEdited(); }
        }

        private double _width = DefaultWidth;
        public double Width
        {
            get => _width;
            set { if (SetProperty(ref _width, Math.Max(MinWidth, value))) _owner.ScreenOverlayEdited(); }
        }

        private double _height = DefaultHeight;
        public double Height
        {
            get => _height;
            set { if (SetProperty(ref _height, Math.Max(MinHeight, value))) _owner.ScreenOverlayEdited(); }
        }

        private bool _isRunning;

        /// <summary>True when the overlay's window is currently shown. Drives the
        /// localized <see cref="Status"/> text below.</summary>
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                    OnPropertyChanged(nameof(Status));
            }
        }

        private KeyboardScreenService.Blank _blank;

        /// <summary>Why the running overlay shows nothing, for <see cref="Status"/>.</summary>
        internal KeyboardScreenService.Blank Blank
        {
            get => _blank;
            set
            {
                if (_blank == value) return;
                _blank = value;
                OnPropertyChanged(nameof(Status));
            }
        }

        /// <summary>Stopped, running, or running and why it shows nothing, in the
        /// keyboard screens' words.</summary>
        public string Status
        {
            get
            {
                var s = Strings.Instance;
                if (!_isRunning) return s.Common_Stopped;
                return _blank switch
                {
                    KeyboardScreenService.Blank.NoPicture => string.Format(CultureInfo.CurrentCulture, s.KeyboardScreens_Status_NoPicture_Format, s.Common_Running),
                    KeyboardScreenService.Blank.NoLightbar => string.Format(CultureInfo.CurrentCulture, s.KeyboardScreens_Status_NoLightbar_Format, s.Common_Running),
                    _ => s.Common_Running,
                };
            }
        }

        /// <summary>What the overlay shows now, in the keyboard screens' words
        /// (Clock, Lightbar Color).</summary>
        public string ShowsText => KeyboardScreenService.ModeName(_owner.ScreenOverlayModeOf?.Invoke(Number) ?? KeyboardScreenData.DefaultMode);

        internal void RefreshShows() => OnPropertyChanged(nameof(ShowsText));

        // Named by their number, so a script can tell two overlays' controls apart.
        public string EnableId => "ScreenOverlay_Enable_" + Number;
        public string OpacityId => "ScreenOverlay_Opacity_" + Number;
        public string ChooseId => "ScreenOverlay_Choose_" + Number;
        public string ResetPositionId => "ScreenOverlay_ResetPosition_" + Number;
        public string RemoveId => "ScreenOverlay_Remove_" + Number;
        public string ResetAllId => "ScreenOverlay_ResetAll_" + Number;

        private RelayCommand _resetEnabledCommand;
        public RelayCommand ResetEnabledCommand => _resetEnabledCommand ??= new RelayCommand(() => Enabled = false);

        private RelayCommand _resetOpacityCommand;
        public RelayCommand ResetOpacityCommand => _resetOpacityCommand ??= new RelayCommand(() => Opacity = DefaultOpacity);

        private RelayCommand _resetPositionCommand;
        public RelayCommand ResetPositionCommand => _resetPositionCommand ??= new RelayCommand(() => _owner.RequestScreenOverlayResetPosition(this));

        private RelayCommand _chooseCommand;
        public RelayCommand ChooseCommand => _chooseCommand ??= new RelayCommand(() => _owner.RequestScreenOverlayChoose(this));

        private RelayCommand _removeCommand;
        public RelayCommand RemoveCommand => _removeCommand ??= new RelayCommand(() => _owner.RemoveScreenOverlay(this));

        private RelayCommand _resetAllCommand;
        /// <summary>Reset All on the overlay's card (#574): off, what it shows,
        /// its opacity and its place, each to its default.</summary>
        public RelayCommand ResetAllCommand => _resetAllCommand ??= new RelayCommand(() =>
        {
            Enabled = false;
            Opacity = DefaultOpacity;
            _owner.RequestScreenOverlayResetPosition(this);
            _owner.RequestScreenOverlayResetChoices(this);
        });

        /// <summary>The settings file's copy.</summary>
        internal ScreenOverlayData ToData() => new()
        {
            Number = Number,
            Enabled = _enabled,
            Opacity = _opacity,
            Monitor = _monitor,
            Left = _left,
            Top = _top,
            Width = _width,
            Height = _height,
        };

        /// <summary>Takes the settings file's values, through the setters' bounds.</summary>
        internal void Load(ScreenOverlayData data)
        {
            Enabled = data.Enabled;
            Opacity = data.Opacity;
            Monitor = data.Monitor;
            Left = data.Left;
            Top = data.Top;
            Width = data.Width > 0 ? data.Width : DefaultWidth;
            Height = data.Height > 0 ? data.Height : DefaultHeight;
        }

        internal void RefreshText()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(ShowsText));
        }

        internal void RefreshOneOfSeveral() => OnPropertyChanged(nameof(OneOfSeveral));
    }
}
