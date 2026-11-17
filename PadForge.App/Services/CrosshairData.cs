using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Serialization;

namespace PadForge.Services
{
    /// <summary>How a crosshair is drawn (#585). The ordinals are saved, so
    /// the list only grows at its end.</summary>
    public enum CrosshairStyle
    {
        /// <summary>Four arms around the center with a gap, the CS:GO classic
        /// crosshair (Skarbo/CSGOCrosshair, javascript.js updateCrosshair).</summary>
        Cross = 0,
        /// <summary>A ring drawn as a polygon of 3 to 64 sides, Crosshair-Overlay's
        /// polygon style (Drawing.cpp STYLE_POLYGON).</summary>
        Circle = 1,
        /// <summary>A filled dot.</summary>
        Dot = 2,
        /// <summary>A picture with its transparency, Crosshair-Overlay's image style
        /// (Drawing.cpp STYLE_IMAGE).</summary>
        Image = 3,
    }

    /// <summary>What the magnifier shows (#585). Saved by ordinal.</summary>
    public enum MagnifierMode
    {
        /// <summary>A magnifier control in a lens over the area it magnifies
        /// (Windows-classic-samples, Magnification, MagnifierSample.cpp).</summary>
        Lens = 0,
        /// <summary>The whole display zoomed by Windows' full-screen magnifier
        /// (MagSetFullscreenTransform, FullscreenMagnifierSample.cpp).</summary>
        FullScreen = 1,
        /// <summary>The display dimmed except for a circle, Logi Options+'s spotlight.</summary>
        Spotlight = 2,
    }

    /// <summary>What the magnifier centers on (#585). Saved by ordinal.</summary>
    public enum MagnifierFollow
    {
        ScreenCenter = 0,
        Pointer = 1,
        /// <summary>The first crosshair that is on: its display's center plus
        /// its offset, where reWASD's magnifier centers ("zooms in on the area
        /// around your crosshair", reWASD help, Custom Crosshair). With none
        /// on, the display's center.</summary>
        Crosshair = 2,
    }

    /// <summary>The lens's outline (#585). Saved by ordinal.</summary>
    public enum LensShape
    {
        Circle = 0,
        Rectangle = 1,
    }

    /// <summary>
    /// One crosshair (#585): its switch, its display and its offset from that
    /// display's center, and how it is drawn. Kept in the settings file globally
    /// and per profile, as the Screen Overlays are. Sizes are pixels, as
    /// Crosshair-Overlay sets them (Configuration.hpp config_t), and the
    /// ranges are the ones <see cref="Normalize"/> holds them to.
    /// </summary>
    public class CrosshairData
    {
        /// <summary>The most crosshairs a list holds: reWASD's ten, which its
        /// actions 101 to 110 show.</summary>
        public const int MaxCrosshairs = 10;

        public const double MinOffset = -4000, MaxOffset = 4000;
        public const double MinLength = 0, MaxLength = 200;
        public const double MinThickness = 1, MaxThickness = 40;
        public const double MinGap = -20, MaxGap = 200;
        public const double MinOutline = 1, MaxOutline = 10;
        public const double MinRadius = 2, MaxRadius = 400;
        public const int MinSides = 3, MaxSides = 64;
        public const double MinDotSize = 1, MaxDotSize = 100;
        public const double MinImageSize = 1, MaxImageSize = 8000;

        [XmlAttribute] public int Number { get; set; } = 1;
        [XmlElement] public bool Enabled { get; set; }
        [XmlElement] public CrosshairStyle Style { get; set; } = CrosshairStyle.Cross;
        /// <summary>The display's Windows name (\\.\DISPLAY2), as Screen Color
        /// saves one (ScreenDisplayChoice). Empty is the primary display, and so
        /// is a display that is not connected.</summary>
        [XmlElement] public string Display { get; set; } = string.Empty;
        [XmlElement] public double OffsetX { get; set; }
        [XmlElement] public double OffsetY { get; set; }

        /// <summary>The color, CS:GO's classic green (#2EFA2E) to start.</summary>
        [XmlElement] public byte ColorR { get; set; } = 0x2E;
        [XmlElement] public byte ColorG { get; set; } = 0xFA;
        [XmlElement] public byte ColorB { get; set; } = 0x2E;
        [XmlElement] public double Opacity { get; set; } = 1.0;

        [XmlElement] public bool Outline { get; set; } = true;
        [XmlElement] public double OutlineThickness { get; set; } = 1;
        [XmlElement] public byte OutlineR { get; set; }
        [XmlElement] public byte OutlineG { get; set; }
        [XmlElement] public byte OutlineB { get; set; }

        /// <summary>Degrees clockwise, Crosshair-Overlay's flRotation.</summary>
        [XmlElement] public double Rotation { get; set; }
        /// <summary>Edges drawn smooth, Crosshair-Overlay's bAntiAlias. Off, each
        /// edge lands on whole pixels and stays sharp.</summary>
        [XmlElement] public bool SmoothEdges { get; set; }

        /// <summary>An arm's length past the gap (Cross).</summary>
        [XmlElement] public double Length { get; set; } = 10;
        /// <summary>An arm's width (Cross) or the ring's (Circle).</summary>
        [XmlElement] public double Thickness { get; set; } = 2;
        /// <summary>From the center to where an arm begins, past half the
        /// thickness (Cross). Negative overlaps the arms.</summary>
        [XmlElement] public double Gap { get; set; } = 4;
        /// <summary>A dot at the center (Cross, Circle).</summary>
        [XmlElement] public bool CenterDot { get; set; }
        /// <summary>No top arm (Cross).</summary>
        [XmlElement] public bool TShape { get; set; }

        /// <summary>From the center to the middle of the ring's line (Circle).</summary>
        [XmlElement] public double Radius { get; set; } = 12;
        [XmlElement] public int Sides { get; set; } = MaxSides;

        /// <summary>The dot's width (Dot).</summary>
        [XmlElement] public double DotSize { get; set; } = 4;

        /// <summary>The picture's file (Image). Empty with none chosen.</summary>
        [XmlElement] public string ImagePath { get; set; } = string.Empty;
        [XmlElement] public double ImageWidth { get; set; } = 64;
        [XmlElement] public double ImageHeight { get; set; } = 64;
        /// <summary>Width and height move together, in the picture's own
        /// proportions. Off, the picture stretches to both.</summary>
        [XmlElement] public bool KeepProportions { get; set; } = true;

        /// <summary>Every value back inside its range, as a file edited by hand
        /// or written by a later version can hold anything.</summary>
        public CrosshairData Normalize()
        {
            if (!Enum.IsDefined(Style)) Style = CrosshairStyle.Cross;
            Display ??= string.Empty;
            OffsetX = Clamp(OffsetX, MinOffset, MaxOffset, 0);
            OffsetY = Clamp(OffsetY, MinOffset, MaxOffset, 0);
            Opacity = Clamp(Opacity, 0, 1, 1);
            OutlineThickness = Clamp(OutlineThickness, MinOutline, MaxOutline, 1);
            Rotation = Clamp(Rotation, 0, 359, 0);
            Length = Clamp(Length, MinLength, MaxLength, 10);
            Thickness = Clamp(Thickness, MinThickness, MaxThickness, 2);
            Gap = Clamp(Gap, MinGap, MaxGap, 4);
            Radius = Clamp(Radius, MinRadius, MaxRadius, 12);
            Sides = Math.Clamp(Sides, MinSides, MaxSides);
            DotSize = Clamp(DotSize, MinDotSize, MaxDotSize, 4);
            ImagePath ??= string.Empty;
            ImageWidth = Clamp(ImageWidth, MinImageSize, MaxImageSize, 64);
            ImageHeight = Clamp(ImageHeight, MinImageSize, MaxImageSize, 64);
            return this;
        }

        internal static double Clamp(double value, double min, double max, double fallback)
            => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        public CrosshairData Clone() => (CrosshairData)MemberwiseClone();
    }

    /// <summary>One line of a Toggle Crosshair macro's crosshair box (#585):
    /// what it says and the number it stands for, 0 for every
    /// crosshair.</summary>
    public sealed record CrosshairChoice(string Display, int Value)
    {
        /// <summary>What a screen reader says for the line.</summary>
        public override string ToString() => Display;

        /// <summary>Every crosshair, then each number a crosshair can
        /// have.</summary>
        internal static List<CrosshairChoice> All()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var choices = new List<CrosshairChoice> { new(s.Macro_EveryCrosshair, 0) };
            for (int number = 1; number <= CrosshairData.MaxCrosshairs; number++)
                choices.Add(new(string.Format(CultureInfo.CurrentCulture, s.Extras_Crosshair_Number_Format, number), number));
            return choices;
        }
    }

    /// <summary>
    /// The magnifier (#585): its switch, its mode, what it centers on and its
    /// zoom, the lens's shape, size and border, and the spotlight's circle and
    /// dimming. Kept globally and per profile.
    /// </summary>
    public class MagnifierData
    {
        /// <summary>Up to 16 times, the most Windows' Magnifier zooms and the
        /// most reWASD's magnifier does (reWASD help, Custom Crosshair:
        /// "from 1x to 16x").</summary>
        public const double MinZoom = 1.25, MaxZoom = 16;
        public const double MinLensSize = 80, MaxLensSize = 2000;
        public const double MinSpotlightSize = 40, MaxSpotlightSize = 2000;
        public const double MinDim = 0.1, MaxDim = 0.95;
        public const double MinBorder = 1, MaxBorder = 20;

        [XmlElement] public bool Enabled { get; set; }
        [XmlElement] public MagnifierMode Mode { get; set; } = MagnifierMode.Lens;
        [XmlElement] public MagnifierFollow Follow { get; set; } = MagnifierFollow.ScreenCenter;
        /// <summary>The display whose center the magnifier marks, by its Windows
        /// name. Empty is the primary display.</summary>
        [XmlElement] public string Display { get; set; } = string.Empty;
        [XmlElement] public double Zoom { get; set; } = 2;

        [XmlElement] public LensShape LensShape { get; set; } = LensShape.Circle;
        [XmlElement] public double LensWidth { get; set; } = 400;
        [XmlElement] public double LensHeight { get; set; } = 400;
        [XmlElement] public bool Border { get; set; } = true;
        [XmlElement] public byte BorderR { get; set; } = 0xFF;
        [XmlElement] public byte BorderG { get; set; } = 0xFF;
        [XmlElement] public byte BorderB { get; set; } = 0xFF;
        /// <summary>The border's width in pixels.</summary>
        [XmlElement] public double BorderThickness { get; set; } = 4;

        [XmlElement] public double SpotlightSize { get; set; } = 400;
        /// <summary>How dark the display goes outside the spotlight, 0.1 to 0.95.</summary>
        [XmlElement] public double SpotlightDim { get; set; } = 0.6;

        public MagnifierData Normalize()
        {
            if (!Enum.IsDefined(Mode)) Mode = MagnifierMode.Lens;
            if (!Enum.IsDefined(Follow)) Follow = MagnifierFollow.ScreenCenter;
            if (!Enum.IsDefined(LensShape)) LensShape = LensShape.Circle;
            Display ??= string.Empty;
            Zoom = CrosshairData.Clamp(Zoom, MinZoom, MaxZoom, 2);
            LensWidth = CrosshairData.Clamp(LensWidth, MinLensSize, MaxLensSize, 400);
            LensHeight = CrosshairData.Clamp(LensHeight, MinLensSize, MaxLensSize, 400);
            BorderThickness = CrosshairData.Clamp(BorderThickness, MinBorder, MaxBorder, 4);
            SpotlightSize = CrosshairData.Clamp(SpotlightSize, MinSpotlightSize, MaxSpotlightSize, 400);
            SpotlightDim = CrosshairData.Clamp(SpotlightDim, MinDim, MaxDim, 0.6);
            return this;
        }

        public MagnifierData Clone() => (MagnifierData)MemberwiseClone();
    }
}
