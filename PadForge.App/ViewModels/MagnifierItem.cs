using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The magnifier's page (#585): its switch, its mode, what it follows,
    /// its display and zoom, the lens's shape, size and border, and the
    /// spotlight's circle and shade. The zoom and the shade are whole
    /// percentages, as Windows' Magnifier shows its zoom.
    /// </summary>
    public sealed class MagnifierItem : FiniteObservableObject
    {
        private readonly DashboardViewModel _owner;
        private readonly MagnifierData _d = new();
        private static readonly MagnifierData Defaults = new();

        internal MagnifierItem(DashboardViewModel owner) => _owner = owner;

        private void Changed([CallerMemberName] string name = null)
        {
            OnPropertyChanged(name);
            OnPropertyChanged(nameof(Status));
            _owner.MagnifierEdited();
        }

        private static int Whole(double value, double min, double max) => (int)Math.Round(Math.Clamp(value, min, max));

        public bool Enabled
        {
            get => _d.Enabled;
            set { if (_d.Enabled == value) return; _d.Enabled = value; Changed(); }
        }

        public MagnifierMode Mode
        {
            get => _d.Mode;
            set
            {
                if (!Enum.IsDefined(value)) value = MagnifierMode.Lens;
                if (_d.Mode == value) return;
                _d.Mode = value;
                Changed();
                OnPropertyChanged(nameof(ModeIndex));
                OnPropertyChanged(nameof(IsLens));
                OnPropertyChanged(nameof(IsSpotlight));
                OnPropertyChanged(nameof(HasZoom));
            }
        }

        public int ModeIndex
        {
            get => (int)_d.Mode;
            set { if (value >= 0) Mode = (MagnifierMode)value; }
        }

        public IReadOnlyList<string> ModeNames => new[]
        {
            Strings.Instance.Extras_Magnifier_Mode_Lens,
            Strings.Instance.Extras_Magnifier_Mode_FullScreen,
            Strings.Instance.Extras_Magnifier_Mode_Spotlight,
        };

        public bool IsLens => _d.Mode == MagnifierMode.Lens;
        public bool IsSpotlight => _d.Mode == MagnifierMode.Spotlight;

        /// <summary>Lens and Full Screen zoom. Spotlight only shades.</summary>
        public bool HasZoom => _d.Mode != MagnifierMode.Spotlight;

        public MagnifierFollow Follow
        {
            get => _d.Follow;
            set
            {
                if (!Enum.IsDefined(value)) value = MagnifierFollow.ScreenCenter;
                if (_d.Follow == value) return;
                _d.Follow = value;
                Changed();
                OnPropertyChanged(nameof(FollowIndex));
                OnPropertyChanged(nameof(FollowsCenter));
            }
        }

        public int FollowIndex
        {
            get => (int)_d.Follow;
            set { if (value >= 0) Follow = (MagnifierFollow)value; }
        }

        public IReadOnlyList<string> FollowNames => new[]
        {
            Strings.Instance.Extras_Magnifier_Follow_Center,
            Strings.Instance.Extras_Magnifier_Follow_Pointer,
            Strings.Instance.Extras_Magnifier_Follow_Crosshair,
        };

        /// <summary>The display row counts only while the magnifier marks a
        /// display's center. Following the pointer, it is on the display the
        /// pointer is on, and following a crosshair, on the crosshair's.</summary>
        public bool FollowsCenter => _d.Follow == MagnifierFollow.ScreenCenter;

        /// <summary>The display's Windows name, or empty for the primary
        /// display. Null is the Display box's selection cleared by a new list,
        /// and changes nothing.</summary>
        public string Display
        {
            get => _d.Display;
            set
            {
                if (value == null || _d.Display == value) return;
                _d.Display = value;
                Changed();
                if (_displays != null) RefreshDisplays();
            }
        }

        /// <summary>The Display box's lines: the primary display, each display by
        /// its number, and the saved one while it is not connected, the Lighting
        /// tab's list (ScreenDisplayChoice.ListFor).</summary>
        public IReadOnlyList<ScreenDisplayChoice> DisplayChoices => _displays ??= ScreenDisplayChoice.ListFor(_d.Display);

        private List<ScreenDisplayChoice> _displays;

        /// <summary>A display plugged in or out, or a new language: a new list
        /// when it says anything new. The new list clears the box's selection,
        /// which writes nothing (see <see cref="Display"/>), so the display goes
        /// back to the box after it, by value, as MacroItem's overlay list
        /// does.</summary>
        internal void RefreshDisplays()
        {
            var now = ScreenDisplayChoice.ListFor(_d.Display);
            if (_displays != null && _displays.SequenceEqual(now)) return;
            _displays = now;
            OnPropertyChanged(nameof(DisplayChoices));
            OnPropertyChanged(nameof(Display));
        }

        /// <summary>The zoom as a whole percentage, 125 to 1600.</summary>
        public int ZoomPercent
        {
            get => (int)Math.Round(_d.Zoom * 100);
            set
            {
                double v = Whole(value, MagnifierData.MinZoom * 100, MagnifierData.MaxZoom * 100) / 100.0;
                if (v == _d.Zoom) return;
                _d.Zoom = v;
                Changed();
            }
        }

        public LensShape LensShape
        {
            get => _d.LensShape;
            set
            {
                if (!Enum.IsDefined(value)) value = LensShape.Circle;
                if (_d.LensShape == value) return;
                _d.LensShape = value;
                Changed();
                OnPropertyChanged(nameof(LensShapeIndex));
                OnPropertyChanged(nameof(IsRound));
            }
        }

        public int LensShapeIndex
        {
            get => (int)_d.LensShape;
            set { if (value >= 0) LensShape = (LensShape)value; }
        }

        public IReadOnlyList<string> LensShapeNames => new[]
        {
            Strings.Instance.Extras_Magnifier_Shape_Circle,
            Strings.Instance.Extras_Magnifier_Shape_Rectangle,
        };

        /// <summary>A round lens has one size, its width. A rectangle has a
        /// width and a height.</summary>
        public bool IsRound => _d.LensShape == LensShape.Circle;

        public int LensWidth
        {
            get => (int)Math.Round(_d.LensWidth);
            set
            {
                int v = Whole(value, MagnifierData.MinLensSize, MagnifierData.MaxLensSize);
                if (v == _d.LensWidth && (!IsRound || v == _d.LensHeight)) return;
                _d.LensWidth = v;
                // A circle stays a circle.
                if (IsRound) _d.LensHeight = v;
                Changed();
                OnPropertyChanged(nameof(LensHeight));
            }
        }

        public int LensHeight
        {
            get => (int)Math.Round(_d.LensHeight);
            set
            {
                int v = Whole(value, MagnifierData.MinLensSize, MagnifierData.MaxLensSize);
                if (v == _d.LensHeight) return;
                _d.LensHeight = v;
                Changed();
            }
        }

        public bool Border
        {
            get => _d.Border;
            set { if (_d.Border == value) return; _d.Border = value; Changed(); }
        }

        public byte BorderR
        {
            get => _d.BorderR;
            set { if (_d.BorderR == value) return; _d.BorderR = value; Changed(); OnPropertyChanged(nameof(BorderHex)); }
        }

        public byte BorderG
        {
            get => _d.BorderG;
            set { if (_d.BorderG == value) return; _d.BorderG = value; Changed(); OnPropertyChanged(nameof(BorderHex)); }
        }

        public byte BorderB
        {
            get => _d.BorderB;
            set { if (_d.BorderB == value) return; _d.BorderB = value; Changed(); OnPropertyChanged(nameof(BorderHex)); }
        }

        public string BorderHex
        {
            get => CrosshairItem.Hex(_d.BorderR, _d.BorderG, _d.BorderB);
            set
            {
                if (CrosshairItem.TryHex(value, out byte r, out byte g, out byte b)) { BorderR = r; BorderG = g; BorderB = b; }
                else OnPropertyChanged();
            }
        }

        public int BorderThickness
        {
            get => (int)Math.Round(_d.BorderThickness);
            set { int v = Whole(value, MagnifierData.MinBorder, MagnifierData.MaxBorder); if (v == _d.BorderThickness) return; _d.BorderThickness = v; Changed(); }
        }

        /// <summary>The spotlight circle's width in pixels.</summary>
        public int SpotlightSize
        {
            get => (int)Math.Round(_d.SpotlightSize);
            set { int v = Whole(value, MagnifierData.MinSpotlightSize, MagnifierData.MaxSpotlightSize); if (v == _d.SpotlightSize) return; _d.SpotlightSize = v; Changed(); }
        }

        /// <summary>How dark the display goes outside the spotlight, 10 to 95
        /// percent.</summary>
        public int SpotlightDimPercent
        {
            get => (int)Math.Round(_d.SpotlightDim * 100);
            set
            {
                double v = Whole(value, MagnifierData.MinDim * 100, MagnifierData.MaxDim * 100) / 100.0;
                if (v == _d.SpotlightDim) return;
                _d.SpotlightDim = v;
                Changed();
            }
        }

        // ─────────────────────────────────────────────
        //  Status
        // ─────────────────────────────────────────────

        private bool _isRunning;

        public bool IsRunning
        {
            get => _isRunning;
            set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(Status)); }
        }

        private string _failure;

        /// <summary>What Windows refused, in the user's words, or null.</summary>
        internal string Failure
        {
            get => _failure;
            set { if (_failure == value) return; _failure = value; OnPropertyChanged(nameof(Status)); }
        }

        public string Status => _failure ?? (_isRunning ? Strings.Instance.Common_Running : Strings.Instance.Common_Stopped);

        private RelayCommand _resetAllCommand;

        /// <summary>Reset All: off, and every value back to its
        /// default.</summary>
        public RelayCommand ResetAllCommand => _resetAllCommand ??= new RelayCommand(() => Load(new MagnifierData()));

        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is
            nameof(Enabled) or nameof(ModeIndex) or nameof(FollowIndex) or nameof(Display) or
            nameof(ZoomPercent) or nameof(LensShapeIndex) or nameof(LensWidth) or nameof(LensHeight) or
            nameof(Border) or nameof(BorderHex) or nameof(BorderThickness) or nameof(SpotlightSize) or
            nameof(SpotlightDimPercent);

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(Enabled): Enabled = Defaults.Enabled; break;
                case nameof(ModeIndex): Mode = Defaults.Mode; break;
                case nameof(FollowIndex): Follow = Defaults.Follow; break;
                case nameof(Display): Display = Defaults.Display; break;
                case nameof(ZoomPercent): ZoomPercent = (int)Math.Round(Defaults.Zoom * 100); break;
                case nameof(LensShapeIndex): LensShape = Defaults.LensShape; break;
                case nameof(LensWidth): LensWidth = (int)Defaults.LensWidth; break;
                case nameof(LensHeight): LensHeight = (int)Defaults.LensHeight; break;
                case nameof(Border): Border = Defaults.Border; break;
                case nameof(BorderHex): BorderR = Defaults.BorderR; BorderG = Defaults.BorderG; BorderB = Defaults.BorderB; break;
                case nameof(BorderThickness): BorderThickness = (int)Defaults.BorderThickness; break;
                case nameof(SpotlightSize): SpotlightSize = (int)Defaults.SpotlightSize; break;
                case nameof(SpotlightDimPercent): SpotlightDimPercent = (int)Math.Round(Defaults.SpotlightDim * 100); break;
            }
        }

        /// <summary>The settings file's copy.</summary>
        internal MagnifierData ToData() => _d.Clone();

        /// <summary>Takes the settings file's values, held to their ranges.
        /// Null, from a file or profile from before the magnifier, is the
        /// defaults.</summary>
        internal void Load(MagnifierData data)
        {
            var values = (data ?? new MagnifierData()).Clone().Normalize();
            Enabled = values.Enabled;
            Mode = values.Mode;
            Follow = values.Follow;
            Display = values.Display;
            ZoomPercent = (int)Math.Round(values.Zoom * 100);
            // The shape first, so a round lens's width does not overwrite a
            // saved rectangle's height.
            LensShape = values.LensShape;
            LensHeight = (int)Math.Round(values.LensHeight);
            if (IsRound) LensWidth = (int)Math.Round(values.LensWidth);
            else
            {
                _d.LensWidth = Whole(values.LensWidth, MagnifierData.MinLensSize, MagnifierData.MaxLensSize);
                Changed(nameof(LensWidth));
            }
            Border = values.Border;
            BorderR = values.BorderR;
            BorderG = values.BorderG;
            BorderB = values.BorderB;
            BorderThickness = (int)Math.Round(values.BorderThickness);
            SpotlightSize = (int)Math.Round(values.SpotlightSize);
            SpotlightDimPercent = (int)Math.Round(values.SpotlightDim * 100);
        }

        internal void RefreshText()
        {
            if (_displays != null) RefreshDisplays();
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(ModeNames));
            OnPropertyChanged(nameof(ModeIndex));
            OnPropertyChanged(nameof(FollowNames));
            OnPropertyChanged(nameof(FollowIndex));
            OnPropertyChanged(nameof(LensShapeNames));
            OnPropertyChanged(nameof(LensShapeIndex));
        }
    }
}
