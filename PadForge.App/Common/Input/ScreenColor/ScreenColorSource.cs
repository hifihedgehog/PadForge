using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.ViewModels;

namespace PadForge.Common.Input.ScreenColor
{
    /// <summary>
    /// What the Screen Color lighting mode shows (#559): the color or the
    /// picture one device's settings ask of the screen right now. Reading is what
    /// keeps a display's capture running (<see cref="ScreenCaptureWorker"/>), and
    /// devices with the same settings share one result, so a slot's devices cost
    /// one calculation between them. Called from the lighting threads at up to 30
    /// a second, and it holds its lock only for arithmetic on a small picture, a
    /// few thousand cells at the most.
    /// </summary>
    internal static class ScreenColorSource
    {
        /// <summary>Set by the test assembly for the life of its process, never
        /// by the app. While this is set and no test seam is, no capture
        /// starts.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: how a display's grabber is made. Null in
        /// production.</summary>
        internal static Func<string, IScreenGrabber> GrabberSeam;

        /// <summary>Test seam: whether Windows lists a display as attached.
        /// Null in production.</summary>
        internal static Func<string, bool> ListedSeam;

        /// <summary>Test seam: whether a capture built now would read that
        /// display. Null in production.</summary>
        internal static Func<string, bool> ResolvesSeam;

        /// <summary>Test seam: the time in milliseconds. Null in production.</summary>
        internal static Func<long> ClockSeam;

        /// <summary>Test seam: how a new worker starts. Null in production,
        /// where it gets its own thread.</summary>
        internal static Action<ScreenCaptureWorker> StartSeam;

        /// <summary>How long a result no device has asked for stays.</summary>
        private const long ForgetMs = 10000;

        /// <summary>The most columns or rows a spread picture keeps. A keyboard
        /// has about 22 by 6 lights, and a light should show its part of the
        /// screen and not one cell's speck of it.</summary>
        internal const int PictureMaxCols = 32, PictureMaxRows = 16;

        private static readonly object s_gate = new();
        private static readonly Dictionary<string, ScreenCaptureWorker> s_workers = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ResultKey, ColorResult> s_colors = new();
        private static readonly Dictionary<ResultKey, PictureResult> s_pictures = new();

        private readonly record struct ResultKey(string Display, ScreenRect Area, ScreenColorMethod Method,
            int Saturation, int Brightness, int Smoothing);

        private sealed class ColorResult
        {
            public ScreenFrame Frame;
            public bool HasColor;
            public int Target;
            public float R, G, B;
            public long LastMs;
        }

        private sealed class PictureResult
        {
            public ScreenFrame Frame;
            public int Cols, Rows;
            public int[] Target;
            public float[] Now;
            public ScreenPicture Picture;
            public long LastMs;
        }

        private static long Now => ClockSeam?.Invoke() ?? Environment.TickCount64;

        private static ResultKey KeyOf(DeviceSlotConfig cfg) => new(cfg.ScreenColorDisplay ?? string.Empty,
            ScreenColorMath.RectOf(cfg.ScreenColorArea, cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight),
            cfg.ScreenColorMethod, cfg.ScreenColorSaturation, cfg.ScreenColorBrightness, cfg.ScreenColorSmoothing);

        /// <summary>The one color a device's settings ask for. Black until the
        /// display's first picture, and the last color while the capture waits
        /// on Windows.</summary>
        public static (byte R, byte G, byte B) ColorFor(DeviceSlotConfig cfg)
        {
            var key = KeyOf(cfg);
            long now = Now;
            lock (s_gate)
            {
                var frame = WorkerFor(key.Display)?.Latest;
                if (!s_colors.TryGetValue(key, out var result))
                {
                    Forget(s_colors, now, r => r.LastMs);
                    result = new ColorResult { LastMs = now };
                    s_colors[key] = result;
                }
                if (frame != null && !ReferenceEquals(frame, result.Frame))
                {
                    result.Frame = frame;
                    result.Target = Process(key, Pick(frame, key));
                    if (!result.HasColor)
                    {
                        result.HasColor = true;
                        result.R = (result.Target >> 16) & 0xFF;
                        result.G = (result.Target >> 8) & 0xFF;
                        result.B = result.Target & 0xFF;
                    }
                }
                float step = ScreenColorMath.SmoothingStep(now - result.LastMs, key.Smoothing);
                result.LastMs = now;
                result.R += (((result.Target >> 16) & 0xFF) - result.R) * step;
                result.G += (((result.Target >> 8) & 0xFF) - result.G) * step;
                result.B += ((result.Target & 0xFF) - result.B) * step;
                return (ToByte(result.R), ToByte(result.G), ToByte(result.B));
            }
        }

        /// <summary>The chosen area as a picture for a device's lights, or null
        /// until the display's first picture. The same instance while nothing on
        /// it changes, so a device with nothing new to show gets no write.</summary>
        public static ScreenPicture PictureFor(DeviceSlotConfig cfg)
        {
            var key = KeyOf(cfg);
            long now = Now;
            lock (s_gate)
            {
                var frame = WorkerFor(key.Display)?.Latest;
                if (!s_pictures.TryGetValue(key, out var result))
                {
                    Forget(s_pictures, now, r => r.LastMs);
                    result = new PictureResult { LastMs = now };
                    s_pictures[key] = result;
                }
                if (frame != null && !ReferenceEquals(frame, result.Frame))
                {
                    result.Frame = frame;
                    Crop(frame, key, out int cols, out int rows, out int[] target);
                    if (result.Now == null || cols != result.Cols || rows != result.Rows)
                    {
                        result.Cols = cols;
                        result.Rows = rows;
                        result.Now = new float[target.Length * 3];
                        for (int i = 0; i < target.Length; i++)
                        {
                            result.Now[i * 3] = (target[i] >> 16) & 0xFF;
                            result.Now[i * 3 + 1] = (target[i] >> 8) & 0xFF;
                            result.Now[i * 3 + 2] = target[i] & 0xFF;
                        }
                    }
                    result.Target = target;
                }
                if (result.Target == null) return null;
                float step = ScreenColorMath.SmoothingStep(now - result.LastMs, key.Smoothing);
                result.LastMs = now;
                var cells = new int[result.Target.Length];
                for (int i = 0; i < cells.Length; i++)
                {
                    int target = result.Target[i];
                    float r = result.Now[i * 3] += (((target >> 16) & 0xFF) - result.Now[i * 3]) * step;
                    float g = result.Now[i * 3 + 1] += (((target >> 8) & 0xFF) - result.Now[i * 3 + 1]) * step;
                    float b = result.Now[i * 3 + 2] += ((target & 0xFF) - result.Now[i * 3 + 2]) * step;
                    cells[i] = (ToByte(r) << 16) | (ToByte(g) << 8) | ToByte(b);
                }
                if (result.Picture == null || !result.Picture.SameCells(result.Cols, result.Rows, cells))
                    result.Picture = new ScreenPicture(result.Cols, result.Rows, cells);
                return result.Picture;
            }
        }

        /// <summary>What a display's capture is doing, without keeping it
        /// running.</summary>
        public static ScreenCaptureState StateOf(string display)
        {
            lock (s_gate)
                return s_workers.TryGetValue(display ?? string.Empty, out var worker) ? worker.State : ScreenCaptureState.Stopped;
        }

        /// <summary>The worker reading a display, started if none is. Null when
        /// the test host has no seam.</summary>
        private static ScreenCaptureWorker WorkerFor(string display)
        {
            if (OffLimitsForTest && GrabberSeam == null) return null;
            if (!s_workers.TryGetValue(display, out var worker))
            {
                worker = new ScreenCaptureWorker(display, GrabberSeam ?? MakeGrabber, () => Now, Retire, CanReadAgain);
                s_workers[display] = worker;
                if (StartSeam != null) StartSeam(worker);
                else worker.Start();
            }
            worker.Touch();
            return worker;
        }

        /// <summary>Lets a worker go if nothing has read it since it asked, so a
        /// read that came in at the last moment finds a live worker.</summary>
        private static bool Retire(ScreenCaptureWorker worker)
        {
            lock (s_gate)
            {
                if (!worker.IsIdle) return false;
                if (s_workers.TryGetValue(worker.Display, out var held) && ReferenceEquals(held, worker))
                    s_workers.Remove(worker.Display);
                return true;
            }
        }

        /// <summary>Whether a capture built now would read <paramref name="display"/>
        /// and not fall back to the primary. The display list answers first,
        /// since it costs no DXGI factory while the display stays away. A display
        /// it lists as attached is then asked of DXGI, the walk the rebuild makes
        /// (<see cref="ScreenDxgi.Resolves"/>): an indirect or virtual display the
        /// list holds and DXGI does not enumerate would otherwise rebuild the
        /// capture onto the primary every second.</summary>
        private static bool CanReadAgain(string display)
            => (ListedSeam ?? ScreenDxgi.IsAttached)(display) && (ResolvesSeam ?? ScreenDxgi.Resolves)(display);

        private static IScreenGrabber MakeGrabber(string display)
            => FirstThatRuns(display, d => new DesktopDuplicationGrabber(d), d => new WindowsCaptureGrabber(d));

        /// <summary>The second way of reading a display, only when the first
        /// cannot run on this PC. A desktop Windows holds for now is no reason to
        /// change ways, so that wait reaches the worker.</summary>
        internal static IScreenGrabber FirstThatRuns(string display, Func<string, IScreenGrabber> first, Func<string, IScreenGrabber> second)
        {
            try
            {
                return first(display);
            }
            catch (ScreenGrabberUnavailable e)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN Desktop Duplication is unavailable: {e.Message}");
                return second(display);
            }
        }

        private static int Pick(ScreenFrame frame, in ResultKey key) => key.Method == ScreenColorMethod.MostCommon
            ? ScreenColorMath.MostCommon(frame, key.Area)
            : ScreenColorMath.Average(frame, key.Area);

        private static int Process(in ResultKey key, int rgb)
            => ScreenColorMath.Dim(ScreenColorMath.Saturate(rgb, key.Saturation), key.Brightness);

        /// <summary>The chosen area's cells, brought down to
        /// <see cref="PictureMaxCols"/> by <see cref="PictureMaxRows"/> at the
        /// most by the device's Color choice, Most Common or Average, each with
        /// the device's saturation and brightness. Every cell of the area lands
        /// in one cell of the result, so no edge of the screen goes missing.</summary>
        private static void Crop(ScreenFrame frame, in ResultKey key, out int cols, out int rows, out int[] cells)
        {
            ScreenColorMath.CellRange(frame, key.Area, out int left, out int top, out int right, out int bottom);
            int width = right - left, height = bottom - top;
            cols = Math.Min(width, PictureMaxCols);
            rows = Math.Min(height, PictureMaxRows);
            cells = new int[cols * rows];
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int x0 = left + x * width / cols, x1 = left + (x + 1) * width / cols;
                    int y0 = top + y * height / rows, y1 = top + (y + 1) * height / rows;
                    var part = new ScreenRect(x0 / (float)frame.Cols, y0 / (float)frame.Rows, x1 / (float)frame.Cols, y1 / (float)frame.Rows);
                    cells[y * cols + x] = Process(key, Pick(frame, key with { Area = part }));
                }
        }

        private static void Forget<T>(Dictionary<ResultKey, T> results, long now, Func<T, long> lastUsed)
        {
            if (results.Count == 0) return;
            foreach (var stale in results.Where(p => now - lastUsed(p.Value) > ForgetMs).Select(p => p.Key).ToList())
                results.Remove(stale);
        }

        private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

        /// <summary>Test seam: forgets every worker and result.</summary>
        internal static void ResetForTest()
        {
            lock (s_gate)
            {
                s_workers.Clear();
                s_colors.Clear();
                s_pictures.Clear();
            }
        }

        /// <summary>Test seam: the worker reading a display now, or null.</summary>
        internal static ScreenCaptureWorker WorkerForTest(string display)
        {
            lock (s_gate)
                return s_workers.TryGetValue(display ?? string.Empty, out var worker) ? worker : null;
        }
    }
}
