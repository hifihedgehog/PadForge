using System;
using PadForge.ViewModels;

namespace PadForge.Common.Input.ScreenColor
{
    /// <summary>One downscaled picture of a display (#559), in the display's
    /// own orientation: <see cref="Cells"/> row by row, each 0xRRGGBB. Made
    /// whole and never changed, so a capture thread can hand one to the
    /// lighting threads by reference.</summary>
    internal sealed class ScreenFrame
    {
        public int Cols { get; }
        public int Rows { get; }
        public int[] Cells { get; }

        public ScreenFrame(int cols, int rows, int[] cells)
        {
            Cols = cols;
            Rows = rows;
            Cells = cells;
        }
    }

    /// <summary>A part of a display, each edge from 0 to 1.</summary>
    internal readonly record struct ScreenRect(float Left, float Top, float Right, float Bottom);

    /// <summary>
    /// From a display's downscaled picture to the colors a light shows (#559):
    /// the area, then Average or Most Common, then saturation, brightness and
    /// smoothing. Nothing here holds state.
    /// </summary>
    internal static class ScreenColorMath
    {
        // Rec. 601 luminance weights as RazerAmbilight's ApplySaturation uses
        // them (Ambilight/Util/ImageManipulation.cs).
        private const float RedWeight = 0.3086f, GreenWeight = 0.6094f, BlueWeight = 0.0820f;

        // A color less colorful or darker than this counts as neutral in Most
        // Common, and a neutral cell weighs this little against a colorful
        // one at full strength. A scene that is mostly black or gray with one
        // strong color shows that color, and a scene with no such color shows
        // its gray.
        private const float NeutralSaturation = 0.2f, NeutralValue = 0.1f, NeutralWeight = 0.05f;
        private const int HueBuckets = 12;

        /// <summary>How long smoothing takes per percent of its setting, as the
        /// time constant of a fade: 100 percent is half a second.</summary>
        internal const float SmoothingMsPerPercent = 5f;

        /// <summary>The part of a display an area covers. The eight zones and
        /// the center are each a third of the display on a side, as the
        /// discussion asked for squares of the screen. A custom area's percents
        /// are held inside the display and never come to nothing.</summary>
        public static ScreenRect RectOf(ScreenColorArea area, int leftPercent, int topPercent, int widthPercent, int heightPercent)
        {
            const float A = 1f / 3f, B = 2f / 3f;
            switch (area)
            {
                case ScreenColorArea.TopLeft: return new ScreenRect(0, 0, A, A);
                case ScreenColorArea.Top: return new ScreenRect(A, 0, B, A);
                case ScreenColorArea.TopRight: return new ScreenRect(B, 0, 1, A);
                case ScreenColorArea.Left: return new ScreenRect(0, A, A, B);
                case ScreenColorArea.Center: return new ScreenRect(A, A, B, B);
                case ScreenColorArea.Right: return new ScreenRect(B, A, 1, B);
                case ScreenColorArea.BottomLeft: return new ScreenRect(0, B, A, 1);
                case ScreenColorArea.Bottom: return new ScreenRect(A, B, B, 1);
                case ScreenColorArea.BottomRight: return new ScreenRect(B, B, 1, 1);
                case ScreenColorArea.Custom:
                {
                    int left = Math.Clamp(leftPercent, 0, 99);
                    int top = Math.Clamp(topPercent, 0, 99);
                    int right = Math.Clamp(left + Math.Max(widthPercent, 1), left + 1, 100);
                    int bottom = Math.Clamp(top + Math.Max(heightPercent, 1), top + 1, 100);
                    return new ScreenRect(left / 100f, top / 100f, right / 100f, bottom / 100f);
                }
                default: return new ScreenRect(0, 0, 1, 1);
            }
        }

        /// <summary>The cells a rectangle touches, at least one. The right and
        /// bottom ends are past the last cell.</summary>
        public static void CellRange(ScreenFrame frame, in ScreenRect rect, out int left, out int top, out int right, out int bottom)
        {
            const float Slack = 0.0001f;
            left = Math.Clamp((int)MathF.Floor(rect.Left * frame.Cols + Slack), 0, frame.Cols - 1);
            top = Math.Clamp((int)MathF.Floor(rect.Top * frame.Rows + Slack), 0, frame.Rows - 1);
            right = Math.Clamp((int)MathF.Ceiling(rect.Right * frame.Cols - Slack), left + 1, frame.Cols);
            bottom = Math.Clamp((int)MathF.Ceiling(rect.Bottom * frame.Rows - Slack), top + 1, frame.Rows);
        }

        /// <summary>The mean of the cells a rectangle touches, rounded.</summary>
        public static int Average(ScreenFrame frame, in ScreenRect rect)
        {
            CellRange(frame, rect, out int left, out int top, out int right, out int bottom);
            long r = 0, g = 0, b = 0;
            for (int y = top; y < bottom; y++)
            {
                int row = y * frame.Cols;
                for (int x = left; x < right; x++)
                {
                    int cell = frame.Cells[row + x];
                    r += (cell >> 16) & 0xFF;
                    g += (cell >> 8) & 0xFF;
                    b += cell & 0xFF;
                }
            }
            long n = (right - left) * (bottom - top);
            return Pack((int)((r + n / 2) / n), (int)((g + n / 2) / n), (int)((b + n / 2) / n));
        }

        /// <summary>
        /// The color most of a rectangle has, counted by hue and weighed by how
        /// colorful each cell is. AlienFX Tools offers a Prevailing calculation
        /// beside its Medium one (alienfx-gui/CaptureHelper.cpp), a majority vote
        /// of exact colors. A small averaged picture rarely repeats an exact
        /// color, so this votes by hue instead. Hues within 15 degrees of a
        /// primary share a bucket, so a red does not split in two across the wrap
        /// at 0. The result is the weighted mean of the winning bucket.
        /// </summary>
        public static int MostCommon(ScreenFrame frame, in ScreenRect rect)
        {
            CellRange(frame, rect, out int left, out int top, out int right, out int bottom);
            Span<float> weights = stackalloc float[HueBuckets + 1];
            Span<float> reds = stackalloc float[HueBuckets + 1];
            Span<float> greens = stackalloc float[HueBuckets + 1];
            Span<float> blues = stackalloc float[HueBuckets + 1];
            weights.Clear();
            reds.Clear();
            greens.Clear();
            blues.Clear();
            for (int y = top; y < bottom; y++)
            {
                int row = y * frame.Cols;
                for (int x = left; x < right; x++)
                {
                    int cell = frame.Cells[row + x];
                    int r = (cell >> 16) & 0xFF, g = (cell >> 8) & 0xFF, b = cell & 0xFF;
                    int max = Math.Max(r, Math.Max(g, b));
                    int chroma = max - Math.Min(r, Math.Min(g, b));
                    float value = max / 255f;
                    float saturation = max == 0 ? 0f : chroma / (float)max;
                    int bucket;
                    float weight;
                    if (saturation < NeutralSaturation || value < NeutralValue)
                    {
                        bucket = HueBuckets;
                        weight = NeutralWeight;
                    }
                    else
                    {
                        float hue = max == r ? (g - b) / (float)chroma
                            : max == g ? (b - r) / (float)chroma + 2f
                            : (r - g) / (float)chroma + 4f;
                        if (hue < 0f) hue += 6f;
                        bucket = (int)((hue + 0.25f) * 2f) % HueBuckets;
                        weight = saturation * value;
                    }
                    weights[bucket] += weight;
                    reds[bucket] += r * weight;
                    greens[bucket] += g * weight;
                    blues[bucket] += b * weight;
                }
            }
            int best = 0;
            for (int i = 1; i <= HueBuckets; i++)
                if (weights[i] > weights[best]) best = i;
            float total = weights[best];
            if (total <= 0f) return 0;
            return Pack(RoundByte(reds[best] / total), RoundByte(greens[best] / total), RoundByte(blues[best] / total));
        }

        /// <summary>A color pushed from or toward its own gray, as
        /// RazerAmbilight's ApplySaturation does with its color matrix. 100 is
        /// the color itself and 0 its gray.</summary>
        public static int Saturate(int rgb, int percent)
        {
            if (percent == 100) return rgb & 0xFFFFFF;
            float s = Math.Max(percent, 0) / 100f;
            float r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            float gray = RedWeight * r + GreenWeight * g + BlueWeight * b;
            return Pack(RoundByte(gray + s * (r - gray)), RoundByte(gray + s * (g - gray)), RoundByte(gray + s * (b - gray)));
        }

        /// <summary>A color at a percent of its strength, 100 being itself.</summary>
        public static int Dim(int rgb, int percent)
        {
            if (percent >= 100) return rgb & 0xFFFFFF;
            float s = Math.Max(percent, 0) / 100f;
            return Pack(RoundByte(((rgb >> 16) & 0xFF) * s), RoundByte(((rgb >> 8) & 0xFF) * s), RoundByte((rgb & 0xFF) * s));
        }

        /// <summary>How far a color moves toward its target over
        /// <paramref name="elapsedMs"/>, from 0 to 1. A time constant and not a
        /// share per frame, so the fade takes as long at any update rate. The
        /// setting does the job of OpenRGB's Smoothness
        /// (OpenRGBEffectsPlugin Effects/Ambient/Ambient.cpp, Smooth).</summary>
        public static float SmoothingStep(long elapsedMs, int smoothingPercent)
        {
            if (smoothingPercent <= 0) return 1f;
            if (elapsedMs <= 0) return 0f;
            return 1f - MathF.Exp(-elapsedMs / (smoothingPercent * SmoothingMsPerPercent));
        }

        /// <summary>A picture read from a surface in the display's own
        /// orientation. A rotated display's surface stays unrotated (Microsoft,
        /// Desktop Duplication API, Rotating the desktop image), and this turns
        /// it the way ScreenCapture.NET's CopyRotate90, 180 and 270 do.
        /// <paramref name="dxgiRotation"/> is DXGI_MODE_ROTATION: 2 is 90
        /// degrees, 3 is 180 and 4 is 270. Any other value leaves it alone.</summary>
        public static ScreenFrame Oriented(int surfaceCols, int surfaceRows, int[] surface, int dxgiRotation)
        {
            switch (dxgiRotation)
            {
                case 2:
                {
                    int cols = surfaceRows, rows = surfaceCols;
                    var cells = new int[surface.Length];
                    for (int sr = 0; sr < surfaceRows; sr++)
                        for (int sc = 0; sc < surfaceCols; sc++)
                            cells[sc * cols + (cols - sr - 1)] = surface[sr * surfaceCols + sc];
                    return new ScreenFrame(cols, rows, cells);
                }
                case 3:
                {
                    var cells = new int[surface.Length];
                    for (int sr = 0; sr < surfaceRows; sr++)
                        for (int sc = 0; sc < surfaceCols; sc++)
                            cells[(surfaceRows - sr - 1) * surfaceCols + (surfaceCols - sc - 1)] = surface[sr * surfaceCols + sc];
                    return new ScreenFrame(surfaceCols, surfaceRows, cells);
                }
                case 4:
                {
                    int cols = surfaceRows, rows = surfaceCols;
                    var cells = new int[surface.Length];
                    for (int sr = 0; sr < surfaceRows; sr++)
                        for (int sc = 0; sc < surfaceCols; sc++)
                            cells[(rows - sc - 1) * cols + sr] = surface[sr * surfaceCols + sc];
                    return new ScreenFrame(cols, rows, cells);
                }
                default:
                    return new ScreenFrame(surfaceCols, surfaceRows, surface);
            }
        }

        /// <summary>A linear scRGB color from an HDR desktop as an 8-bit sRGB
        /// color. scRGB's 1.0 is 80 nits, so the display's SDR white level is
        /// what maps to full white, which is what a desktop's ordinary colors
        /// are drawn against. Anything brighter clips, and a color outside
        /// sRGB's gamut clips at 0.</summary>
        public static int ScRgbToRgb(float red, float green, float blue, float sdrWhiteNits)
        {
            float scale = 80f / Math.Max(sdrWhiteNits, 1f);
            return Pack(Encode(red * scale), Encode(green * scale), Encode(blue * scale));
        }

        private static int Encode(float linear)
        {
            if (float.IsNaN(linear) || linear <= 0f) return 0;
            if (linear >= 1f) return 255;
            float s = linear <= 0.0031308f ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
            return RoundByte(s * 255f);
        }

        private static int RoundByte(float value) => Math.Clamp((int)MathF.Round(value), 0, 255);

        private static int Pack(int r, int g, int b) => (r << 16) | (g << 8) | b;
    }
}
