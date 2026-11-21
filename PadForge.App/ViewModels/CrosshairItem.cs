using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Crosshairs;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>Why a crosshair that is on shows nothing (#585).</summary>
    internal enum CrosshairProblem
    {
        None,
        /// <summary>An Image crosshair whose picture cannot be read.</summary>
        NoPicture,
        /// <summary>The offset puts the whole crosshair off its display.</summary>
        OffDisplay,
    }

    /// <summary>
    /// One crosshair's card on the Crosshair page (#585): its switch, its
    /// style, its display and offset, and how it is drawn. The number is its
    /// identity and stays through removing another crosshair, so a macro that
    /// names crosshair 3 keeps meaning the same one, as a Screen Overlay's
    /// number does (ScreenOverlayItem). Sizes are whole pixels: the window
    /// places the crosshair on whole pixels (CrosshairPlace), as
    /// Crosshair-Overlay floors its offset and rotation.
    /// </summary>
    public sealed class CrosshairItem : FiniteObservableObject
    {
        private readonly DashboardViewModel _owner;
        private readonly CrosshairData _d = new();
        private CrosshairData _look;

        internal CrosshairItem(DashboardViewModel owner, int number)
        {
            _owner = owner;
            Number = number;
            _d.Number = number;
            _look = _d.Clone();
        }

        public int Number { get; }

        /// <summary>The card's title, "Crosshair 2". Macros name it the same
        /// way.</summary>
        public string Title => string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Crosshair_Number_Format, Number);

        /// <summary>Whether there is more than one crosshair: each card shows
        /// its Remove button only then.</summary>
        public bool OneOfSeveral => _owner.Crosshairs.Count > 1;

        /// <summary>A copy of the settings for the card's preview and the
        /// window, new after each change.</summary>
        public CrosshairData Look => _look;

        private void Changed([CallerMemberName] string name = null)
        {
            OnPropertyChanged(name);
            _look = _d.Clone();
            OnPropertyChanged(nameof(Look));
            OnPropertyChanged(nameof(Status));
            _owner.CrosshairEdited();
        }

        private static int Whole(double value, double min, double max) => (int)Math.Round(Math.Clamp(value, min, max));

        // ─────────────────────────────────────────────
        //  Switch, style and place
        // ─────────────────────────────────────────────

        public bool Enabled
        {
            get => _d.Enabled;
            set { if (_d.Enabled == value) return; _d.Enabled = value; Changed(); }
        }

        public CrosshairStyle Style
        {
            get => _d.Style;
            set
            {
                if (!Enum.IsDefined(value)) value = CrosshairStyle.Cross;
                if (_d.Style == value) return;
                _d.Style = value;
                Changed();
                OnPropertyChanged(nameof(StyleIndex));
                OnPropertyChanged(nameof(IsCross));
                OnPropertyChanged(nameof(IsCircle));
                OnPropertyChanged(nameof(IsDot));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(IsDrawn));
                OnPropertyChanged(nameof(HasThickness));
            }
        }

        /// <summary>The style as the style box's index, in the enum's
        /// order.</summary>
        public int StyleIndex
        {
            get => (int)_d.Style;
            set { if (value >= 0) Style = (CrosshairStyle)value; }
        }

        /// <summary>The style box's lines, in <see cref="CrosshairStyle"/>'s
        /// order.</summary>
        public IReadOnlyList<string> StyleNames => new[]
        {
            Strings.Instance.Extras_Crosshair_Style_Cross,
            Strings.Instance.Extras_Crosshair_Style_Circle,
            Strings.Instance.Extras_Crosshair_Style_Dot,
            Strings.Instance.Extras_Crosshair_Style_Image,
        };

        public bool IsCross => _d.Style == CrosshairStyle.Cross;
        public bool IsCircle => _d.Style == CrosshairStyle.Circle;
        public bool IsDot => _d.Style == CrosshairStyle.Dot;
        public bool IsImage => _d.Style == CrosshairStyle.Image;

        /// <summary>A style drawn from shapes, which takes a color and an
        /// outline. A picture has its own colors.</summary>
        public bool IsDrawn => !IsImage;

        /// <summary>A style with a line's thickness and a center dot: Cross
        /// and Circle.</summary>
        public bool HasThickness => IsCross || IsCircle;

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
        /// group's list (ScreenDisplayChoice.ListFor).</summary>
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

        public int OffsetX
        {
            get => (int)_d.OffsetX;
            set { int v = Whole(value, CrosshairData.MinOffset, CrosshairData.MaxOffset); if (v == (int)_d.OffsetX) return; _d.OffsetX = v; Changed(); }
        }

        public int OffsetY
        {
            get => (int)_d.OffsetY;
            set { int v = Whole(value, CrosshairData.MinOffset, CrosshairData.MaxOffset); if (v == (int)_d.OffsetY) return; _d.OffsetY = v; Changed(); }
        }

        // ─────────────────────────────────────────────
        //  Color and outline
        // ─────────────────────────────────────────────

        public byte ColorR
        {
            get => _d.ColorR;
            set { if (_d.ColorR == value) return; _d.ColorR = value; Changed(); OnPropertyChanged(nameof(ColorHex)); }
        }

        public byte ColorG
        {
            get => _d.ColorG;
            set { if (_d.ColorG == value) return; _d.ColorG = value; Changed(); OnPropertyChanged(nameof(ColorHex)); }
        }

        public byte ColorB
        {
            get => _d.ColorB;
            set { if (_d.ColorB == value) return; _d.ColorB = value; Changed(); OnPropertyChanged(nameof(ColorHex)); }
        }

        /// <summary>The color as #RRGGBB. Text that is no color changes
        /// nothing.</summary>
        public string ColorHex
        {
            get => Hex(_d.ColorR, _d.ColorG, _d.ColorB);
            set
            {
                if (TryHex(value, out byte r, out byte g, out byte b)) { ColorR = r; ColorG = g; ColorB = b; }
                else OnPropertyChanged();
            }
        }

        public double Opacity
        {
            get => _d.Opacity;
            set
            {
                double v = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : _d.Opacity;
                if (v == _d.Opacity) return;
                _d.Opacity = v;
                Changed();
                OnPropertyChanged(nameof(OpacityPercent));
            }
        }

        /// <summary>The opacity as a whole percentage, for the box beside the
        /// slider.</summary>
        public int OpacityPercent
        {
            get => (int)Math.Round(_d.Opacity * 100);
            set => Opacity = value / 100.0;
        }

        public bool Outline
        {
            get => _d.Outline;
            set { if (_d.Outline == value) return; _d.Outline = value; Changed(); }
        }

        public int OutlineThickness
        {
            get => (int)Math.Round(_d.OutlineThickness);
            set { int v = Whole(value, CrosshairData.MinOutline, CrosshairData.MaxOutline); if (v == _d.OutlineThickness) return; _d.OutlineThickness = v; Changed(); }
        }

        public byte OutlineR
        {
            get => _d.OutlineR;
            set { if (_d.OutlineR == value) return; _d.OutlineR = value; Changed(); OnPropertyChanged(nameof(OutlineHex)); }
        }

        public byte OutlineG
        {
            get => _d.OutlineG;
            set { if (_d.OutlineG == value) return; _d.OutlineG = value; Changed(); OnPropertyChanged(nameof(OutlineHex)); }
        }

        public byte OutlineB
        {
            get => _d.OutlineB;
            set { if (_d.OutlineB == value) return; _d.OutlineB = value; Changed(); OnPropertyChanged(nameof(OutlineHex)); }
        }

        public string OutlineHex
        {
            get => Hex(_d.OutlineR, _d.OutlineG, _d.OutlineB);
            set
            {
                if (TryHex(value, out byte r, out byte g, out byte b)) { OutlineR = r; OutlineG = g; OutlineB = b; }
                else OnPropertyChanged();
            }
        }

        /// <summary>Degrees clockwise, 0 to 359, whole degrees as
        /// Crosshair-Overlay floors its rotation.</summary>
        public int Rotation
        {
            get => (int)Math.Round(_d.Rotation);
            set { int v = Whole(value, 0, 359); if (v == _d.Rotation) return; _d.Rotation = v; Changed(); }
        }

        public bool SmoothEdges
        {
            get => _d.SmoothEdges;
            set { if (_d.SmoothEdges == value) return; _d.SmoothEdges = value; Changed(); }
        }

        // ─────────────────────────────────────────────
        //  Cross, Circle and Dot
        // ─────────────────────────────────────────────

        public int Length
        {
            get => (int)Math.Round(_d.Length);
            set { int v = Whole(value, CrosshairData.MinLength, CrosshairData.MaxLength); if (v == _d.Length) return; _d.Length = v; Changed(); }
        }

        public int Thickness
        {
            get => (int)Math.Round(_d.Thickness);
            set { int v = Whole(value, CrosshairData.MinThickness, CrosshairData.MaxThickness); if (v == _d.Thickness) return; _d.Thickness = v; Changed(); }
        }

        public int Gap
        {
            get => (int)Math.Round(_d.Gap);
            set { int v = Whole(value, CrosshairData.MinGap, CrosshairData.MaxGap); if (v == _d.Gap) return; _d.Gap = v; Changed(); }
        }

        public bool CenterDot
        {
            get => _d.CenterDot;
            set { if (_d.CenterDot == value) return; _d.CenterDot = value; Changed(); }
        }

        public bool TShape
        {
            get => _d.TShape;
            set { if (_d.TShape == value) return; _d.TShape = value; Changed(); }
        }

        public int Radius
        {
            get => (int)Math.Round(_d.Radius);
            set { int v = Whole(value, CrosshairData.MinRadius, CrosshairData.MaxRadius); if (v == _d.Radius) return; _d.Radius = v; Changed(); }
        }

        /// <summary>The ring's sides, 3 to 64. At 64 it is round.</summary>
        public int Sides
        {
            get => _d.Sides;
            set { int v = Math.Clamp(value, CrosshairData.MinSides, CrosshairData.MaxSides); if (v == _d.Sides) return; _d.Sides = v; Changed(); }
        }

        public int DotSize
        {
            get => (int)Math.Round(_d.DotSize);
            set { int v = Whole(value, CrosshairData.MinDotSize, CrosshairData.MaxDotSize); if (v == _d.DotSize) return; _d.DotSize = v; Changed(); }
        }

        // ─────────────────────────────────────────────
        //  Image
        // ─────────────────────────────────────────────

        public string ImagePath
        {
            get => _d.ImagePath;
            set
            {
                value ??= string.Empty;
                if (_d.ImagePath == value) return;
                _d.ImagePath = value;
                Changed();
                OnPropertyChanged(nameof(ImageName));
            }
        }

        /// <summary>The picture's file name, or that there is none.</summary>
        public string ImageName => _d.ImagePath.Length == 0 ? Strings.Instance.Extras_Crosshair_NoPictureChosen : Path.GetFileName(_d.ImagePath);

        public int ImageWidth
        {
            get => (int)Math.Round(_d.ImageWidth);
            set
            {
                int v = Whole(value, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
                if (v == _d.ImageWidth) return;
                double ratio = HeightPerWidth();
                _d.ImageWidth = v;
                if (_d.KeepProportions) _d.ImageHeight = Whole(v * ratio, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
                Changed();
                OnPropertyChanged(nameof(ImageHeight));
            }
        }

        public int ImageHeight
        {
            get => (int)Math.Round(_d.ImageHeight);
            set
            {
                int v = Whole(value, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
                if (v == _d.ImageHeight) return;
                double ratio = HeightPerWidth();
                _d.ImageHeight = v;
                if (_d.KeepProportions) _d.ImageWidth = Whole(v / ratio, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
                Changed();
                OnPropertyChanged(nameof(ImageWidth));
            }
        }

        /// <summary>Width and height move together, in the picture's own
        /// proportions. Off, the picture stretches to both.</summary>
        public bool KeepProportions
        {
            get => _d.KeepProportions;
            set { if (_d.KeepProportions == value) return; _d.KeepProportions = value; Changed(); }
        }

        /// <summary>The picture's own height over its width, or the drawn
        /// size's while the picture cannot be read.</summary>
        private double HeightPerWidth()
        {
            var picture = CrosshairPictures.Load(_d.ImagePath);
            if (picture != null && picture.PixelWidth > 0 && picture.PixelHeight > 0)
                return (double)picture.PixelHeight / picture.PixelWidth;
            return _d.ImageWidth > 0 ? _d.ImageHeight / _d.ImageWidth : 1;
        }

        /// <summary>Takes a picture chosen on the page, at its own size.</summary>
        internal void UsePicture(string path)
        {
            ImagePath = path;
            UseOwnSize();
        }

        /// <summary>The drawn size back to the picture's own, in pixels.</summary>
        internal void UseOwnSize()
        {
            var picture = CrosshairPictures.Load(_d.ImagePath);
            if (picture == null) return;
            _d.ImageWidth = Whole(picture.PixelWidth, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
            _d.ImageHeight = Whole(picture.PixelHeight, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
            Changed(nameof(ImageWidth));
            OnPropertyChanged(nameof(ImageHeight));
        }

        private RelayCommand _ownSizeCommand;
        public RelayCommand OwnSizeCommand => _ownSizeCommand ??= new RelayCommand(UseOwnSize);

        // ─────────────────────────────────────────────
        //  Status
        // ─────────────────────────────────────────────

        private bool _isRunning;

        /// <summary>The crosshair's window is up.</summary>
        public bool IsRunning
        {
            get => _isRunning;
            set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(Status)); }
        }

        private CrosshairProblem _problem;

        internal CrosshairProblem Problem
        {
            get => _problem;
            set { if (_problem == value) return; _problem = value; OnPropertyChanged(nameof(Status)); }
        }

        public string Status
        {
            get
            {
                var s = Strings.Instance;
                if (_isRunning) return s.Common_Running;
                if (!_d.Enabled) return s.Common_Stopped;
                return _problem switch
                {
                    CrosshairProblem.NoPicture => s.Extras_Crosshair_Status_NoPicture,
                    CrosshairProblem.OffDisplay => s.Extras_Crosshair_Status_OffDisplay,
                    _ => s.Common_Stopped,
                };
            }
        }

        // Named by their number, so a script can tell two crosshairs'
        // controls apart.
        public string EnableId => "Crosshair_Enable_" + Number;
        public string StyleId => "Crosshair_Style_" + Number;
        public string DisplayId => "Crosshair_Display_" + Number;
        public string PictureId => "Crosshair_Picture_" + Number;
        public string RemoveId => "Crosshair_Remove_" + Number;
        public string ResetAllId => "Crosshair_ResetAll_" + Number;

        private RelayCommand _removeCommand;
        public RelayCommand RemoveCommand => _removeCommand ??= new RelayCommand(() => _owner.RemoveCrosshair(this));

        private RelayCommand _resetAllCommand;

        /// <summary>Reset All on the crosshair's card: off, and every value
        /// back to its default.</summary>
        public RelayCommand ResetAllCommand => _resetAllCommand ??= new RelayCommand(() => Load(new CrosshairData { Number = Number }));

        private RelayCommand<string> _resetSettingCommand;
        public RelayCommand<string> ResetSettingCommand =>
            _resetSettingCommand ??= new RelayCommand<string>(ResetSetting, CanResetSetting);

        /// <summary>The rows the card's reset buttons name.</summary>
        internal static bool CanResetSetting(string name) => name is
            nameof(Enabled) or nameof(StyleIndex) or nameof(Display) or nameof(OffsetX) or
            nameof(ColorHex) or nameof(OpacityPercent) or nameof(Outline) or nameof(OutlineThickness) or
            nameof(OutlineHex) or nameof(Rotation) or nameof(SmoothEdges) or nameof(Length) or
            nameof(Thickness) or nameof(Gap) or nameof(CenterDot) or nameof(TShape) or nameof(Radius) or
            nameof(Sides) or nameof(DotSize) or nameof(ImagePath) or nameof(ImageWidth) or nameof(KeepProportions);

        private static readonly CrosshairData Defaults = new();

        private void ResetSetting(string name)
        {
            switch (name)
            {
                case nameof(Enabled): Enabled = Defaults.Enabled; break;
                case nameof(StyleIndex): Style = Defaults.Style; break;
                case nameof(Display): Display = Defaults.Display; break;
                // The offset row resets both of its boxes.
                case nameof(OffsetX): OffsetX = (int)Defaults.OffsetX; OffsetY = (int)Defaults.OffsetY; break;
                case nameof(ColorHex): ColorR = Defaults.ColorR; ColorG = Defaults.ColorG; ColorB = Defaults.ColorB; break;
                case nameof(OpacityPercent): Opacity = Defaults.Opacity; break;
                case nameof(Outline): Outline = Defaults.Outline; break;
                case nameof(OutlineThickness): OutlineThickness = (int)Defaults.OutlineThickness; break;
                case nameof(OutlineHex): OutlineR = Defaults.OutlineR; OutlineG = Defaults.OutlineG; OutlineB = Defaults.OutlineB; break;
                case nameof(Rotation): Rotation = (int)Defaults.Rotation; break;
                case nameof(SmoothEdges): SmoothEdges = Defaults.SmoothEdges; break;
                case nameof(Length): Length = (int)Defaults.Length; break;
                case nameof(Thickness): Thickness = (int)Defaults.Thickness; break;
                case nameof(Gap): Gap = (int)Defaults.Gap; break;
                case nameof(CenterDot): CenterDot = Defaults.CenterDot; break;
                case nameof(TShape): TShape = Defaults.TShape; break;
                case nameof(Radius): Radius = (int)Defaults.Radius; break;
                case nameof(Sides): Sides = Defaults.Sides; break;
                case nameof(DotSize): DotSize = (int)Defaults.DotSize; break;
                case nameof(ImagePath): ImagePath = Defaults.ImagePath; break;
                // The size row's reset is the picture's own size, or the
                // default size with no picture.
                case nameof(ImageWidth):
                    if (CrosshairPictures.Load(_d.ImagePath) != null) UseOwnSize();
                    else
                    {
                        _d.ImageWidth = Defaults.ImageWidth;
                        _d.ImageHeight = Defaults.ImageHeight;
                        Changed(nameof(ImageWidth));
                        OnPropertyChanged(nameof(ImageHeight));
                    }
                    break;
                case nameof(KeepProportions): KeepProportions = Defaults.KeepProportions; break;
            }
        }

        // ─────────────────────────────────────────────
        //  Settings file
        // ─────────────────────────────────────────────

        /// <summary>The settings file's copy.</summary>
        internal CrosshairData ToData() => _d.Clone();

        /// <summary>Takes the settings file's values, held to their
        /// ranges.</summary>
        internal void Load(CrosshairData data)
        {
            var values = (data ?? new CrosshairData()).Clone().Normalize();
            Enabled = values.Enabled;
            Style = values.Style;
            Display = values.Display;
            OffsetX = (int)Math.Floor(values.OffsetX);
            OffsetY = (int)Math.Floor(values.OffsetY);
            ColorR = values.ColorR;
            ColorG = values.ColorG;
            ColorB = values.ColorB;
            Opacity = values.Opacity;
            Outline = values.Outline;
            OutlineThickness = (int)Math.Round(values.OutlineThickness);
            OutlineR = values.OutlineR;
            OutlineG = values.OutlineG;
            OutlineB = values.OutlineB;
            Rotation = (int)Math.Round(values.Rotation);
            SmoothEdges = values.SmoothEdges;
            Length = (int)Math.Round(values.Length);
            Thickness = (int)Math.Round(values.Thickness);
            Gap = (int)Math.Round(values.Gap);
            CenterDot = values.CenterDot;
            TShape = values.TShape;
            Radius = (int)Math.Round(values.Radius);
            Sides = values.Sides;
            DotSize = (int)Math.Round(values.DotSize);
            ImagePath = values.ImagePath;
            // Both sizes as saved: the proportions they hold were kept when
            // they were set.
            _d.ImageWidth = Whole(values.ImageWidth, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
            _d.ImageHeight = Whole(values.ImageHeight, CrosshairData.MinImageSize, CrosshairData.MaxImageSize);
            Changed(nameof(ImageWidth));
            OnPropertyChanged(nameof(ImageHeight));
            KeepProportions = values.KeepProportions;
        }

        internal void RefreshText()
        {
            if (_displays != null) RefreshDisplays();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StyleNames));
            OnPropertyChanged(nameof(StyleIndex));
            OnPropertyChanged(nameof(ImageName));
        }

        internal void RefreshOneOfSeveral() => OnPropertyChanged(nameof(OneOfSeveral));

        internal static string Hex(byte r, byte g, byte b) => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

        /// <summary>#RRGGBB or RRGGBB, any case.</summary>
        internal static bool TryHex(string text, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            text = text?.Trim() ?? string.Empty;
            if (text.StartsWith('#')) text = text.Substring(1);
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)) return false;
            r = (byte)(value >> 16);
            g = (byte)(value >> 8);
            b = (byte)value;
            return true;
        }
    }
}
