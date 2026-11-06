using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.ScreenColor;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Issue #559: the arithmetic from a display's small picture to a
    /// light's color. No state and no screen.</summary>
    public class ScreenColorMathTests
    {
        private const float Third = 1f / 3f, TwoThirds = 2f / 3f;

        [Theory]
        [InlineData(ScreenColorArea.WholeScreen, 0f, 0f, 1f, 1f)]
        [InlineData(ScreenColorArea.TopLeft, 0f, 0f, Third, Third)]
        [InlineData(ScreenColorArea.Top, Third, 0f, TwoThirds, Third)]
        [InlineData(ScreenColorArea.TopRight, TwoThirds, 0f, 1f, Third)]
        [InlineData(ScreenColorArea.Left, 0f, Third, Third, TwoThirds)]
        [InlineData(ScreenColorArea.Center, Third, Third, TwoThirds, TwoThirds)]
        [InlineData(ScreenColorArea.Right, TwoThirds, Third, 1f, TwoThirds)]
        [InlineData(ScreenColorArea.BottomLeft, 0f, TwoThirds, Third, 1f)]
        [InlineData(ScreenColorArea.Bottom, Third, TwoThirds, TwoThirds, 1f)]
        [InlineData(ScreenColorArea.BottomRight, TwoThirds, TwoThirds, 1f, 1f)]
        public void EachAreaIsItsThirdOfTheDisplay(ScreenColorArea area, float left, float top, float right, float bottom)
        {
            // A custom rectangle's numbers mean nothing to a named area.
            Assert.Equal(new ScreenRect(left, top, right, bottom), ScreenColorMath.RectOf(area, 7, 8, 9, 10));
        }

        [Theory]
        [InlineData(10, 20, 30, 40, 0.10f, 0.20f, 0.40f, 0.60f)]
        // Past an edge, the rectangle stops at the edge.
        [InlineData(90, 90, 50, 50, 0.90f, 0.90f, 1f, 1f)]
        // Nothing wide or tall is still one percent of the display.
        [InlineData(0, 0, 0, -5, 0f, 0f, 0.01f, 0.01f)]
        // The far corner is still one percent inside the display.
        [InlineData(500, 500, 10, 10, 0.99f, 0.99f, 1f, 1f)]
        [InlineData(-20, -20, 10, 10, 0f, 0f, 0.10f, 0.10f)]
        public void ACustomAreaStaysInsideTheDisplay_AndNeverComesToNothing(int l, int t, int w, int h, float left, float top, float right, float bottom)
        {
            Assert.Equal(new ScreenRect(left, top, right, bottom), ScreenColorMath.RectOf(ScreenColorArea.Custom, l, t, w, h));
        }

        [Fact]
        public void ARectangleTouchesTheCellsUnderIt_AndAlwaysAtLeastOne()
        {
            var frame = new ScreenFrame(6, 3, new int[18]);
            ScreenColorMath.CellRange(frame, new ScreenRect(0, 0, 1, 1), out int l, out int t, out int r, out int b);
            Assert.Equal((0, 0, 6, 3), (l, t, r, b));
            // Thirds of six columns land on cell edges, with no stray column
            // from the float's rounding.
            ScreenColorMath.CellRange(frame, ScreenColorMath.RectOf(ScreenColorArea.Top, 0, 0, 0, 0), out l, out t, out r, out b);
            Assert.Equal((2, 0, 4, 1), (l, t, r, b));
            ScreenColorMath.CellRange(frame, new ScreenRect(0.999f, 0.999f, 1f, 1f), out l, out t, out r, out b);
            Assert.Equal((5, 2, 6, 3), (l, t, r, b));
            ScreenColorMath.CellRange(frame, new ScreenRect(0.5f, 0.5f, 0.5f, 0.5f), out l, out t, out r, out b);
            Assert.Equal((3, 1, 4, 2), (l, t, r, b));
        }

        [Fact]
        public void AverageIsTheRoundedMeanOfTheCellsInTheArea()
        {
            var frame = new ScreenFrame(2, 2, new[] { 0x000000, 0x000001, 0x0A0B0C, 0xFFFFFF });
            Assert.Equal(0x000001, ScreenColorMath.Average(frame, new ScreenRect(0, 0, 1, 0.5f)));
            Assert.Equal(0x424343, ScreenColorMath.Average(frame, new ScreenRect(0, 0, 1, 1)));
            Assert.Equal(0xFFFFFF, ScreenColorMath.Average(frame, new ScreenRect(0.5f, 0.5f, 1, 1)));
        }

        [Fact]
        public void MostCommon_ShowsTheStrongColor_OnAMostlyBlackScene()
        {
            // The average of this scene is a dark, muddy red. Most Common is
            // the one strong color on it.
            var cells = Enumerable.Repeat(0x000000, 90).Concat(Enumerable.Repeat(0xFF0000, 10)).ToArray();
            var frame = new ScreenFrame(10, 10, cells);
            Assert.Equal(0xFF0000, ScreenColorMath.MostCommon(frame, new ScreenRect(0, 0, 1, 1)));
            Assert.Equal(0x1A0000, ScreenColorMath.Average(frame, new ScreenRect(0, 0, 1, 1)));
        }

        [Fact]
        public void MostCommon_ShowsTheGray_OfAScreneWithNoColor()
        {
            var frame = new ScreenFrame(2, 1, new[] { 0x404040, 0x606060 });
            Assert.Equal(0x505050, ScreenColorMath.MostCommon(frame, new ScreenRect(0, 0, 1, 1)));
            Assert.Equal(0x000000, ScreenColorMath.MostCommon(new ScreenFrame(1, 1, new[] { 0 }), new ScreenRect(0, 0, 1, 1)));
        }

        [Fact]
        public void MostCommon_DoesNotSplitARedInTwo_AcrossTheWrapAtZero()
        {
            // Hue 350 and hue 10 are one red. Green has more cells than either
            // alone and fewer than the two together.
            int warm = 0xFF2A00, cool = 0xFF002A, green = 0x00FF00;
            var cells = new[] { warm, warm, warm, cool, cool, cool, green, green, green, green };
            var result = ScreenColorMath.MostCommon(new ScreenFrame(10, 1, cells), new ScreenRect(0, 0, 1, 1));
            Assert.Equal(0xFF1515, result);
        }

        [Fact]
        public void MostCommon_WeighsAVividColorAboveAPaleOne()
        {
            // Six pale cells at a saturation of a quarter weigh 1.5, and two
            // full blues weigh 2.
            int pale = 0xFFC0C0;
            var cells = new[] { pale, pale, pale, pale, pale, pale, 0x0000FF, 0x0000FF };
            Assert.Equal(0x0000FF, ScreenColorMath.MostCommon(new ScreenFrame(8, 1, cells), new ScreenRect(0, 0, 1, 1)));
        }

        [Theory]
        [InlineData(0x804020, 100, 0x804020)]
        [InlineData(0x804020, 50, 0x694939)]
        [InlineData(0x804020, 0, 0x515151)]
        [InlineData(0xFF0000, 200, 0xFF0000)]
        [InlineData(0xFF0000, 0, 0x4F4F4F)]
        [InlineData(0x804020, 200, 0xAF2F00)]
        public void SaturationMovesAColorFromOrTowardItsGray(int rgb, int percent, int expected)
        {
            Assert.Equal(expected, ScreenColorMath.Saturate(rgb, percent));
        }

        [Theory]
        [InlineData(0xFF8040, 100, 0xFF8040)]
        [InlineData(0xFF8040, 50, 0x804020)]
        [InlineData(0xFF8040, 0, 0x000000)]
        public void BrightnessIsAShareOfTheColor(int rgb, int percent, int expected)
        {
            Assert.Equal(expected, ScreenColorMath.Dim(rgb, percent));
        }

        [Fact]
        public void SmoothingIsATimeConstant_SoItTakesAsLongAtAnyUpdateRate()
        {
            Assert.Equal(1f, ScreenColorMath.SmoothingStep(16, 0));
            Assert.Equal(0f, ScreenColorMath.SmoothingStep(0, 30));
            // 30 percent is 150 ms, and one time constant covers 1 - 1/e.
            Assert.Equal(1f - MathF.Exp(-1f), ScreenColorMath.SmoothingStep(150, 30), 5);

            // Two passes of 75 ms end where one pass of 150 ms does.
            float half = ScreenColorMath.SmoothingStep(75, 30);
            float twoSteps = 1f - (1f - half) * (1f - half);
            Assert.Equal(ScreenColorMath.SmoothingStep(150, 30), twoSteps, 5);
        }

        private static readonly int[] Surface = { 1, 2, 3, 4, 5, 6 };

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void AnUnrotatedSurfaceStaysAsItIs(int rotation)
        {
            var frame = ScreenColorMath.Oriented(3, 2, Surface, rotation);
            Assert.Equal((3, 2), (frame.Cols, frame.Rows));
            Assert.Equal(Surface, frame.Cells);
        }

        [Theory]
        [InlineData(2, 2, 3, new[] { 4, 1, 5, 2, 6, 3 })]
        [InlineData(3, 3, 2, new[] { 6, 5, 4, 3, 2, 1 })]
        [InlineData(4, 2, 3, new[] { 3, 6, 2, 5, 1, 4 })]
        public void ARotatedDisplaysSurfaceTurnsIntoTheOrientationOnScreen(int rotation, int cols, int rows, int[] expected)
        {
            var frame = ScreenColorMath.Oriented(3, 2, Surface, rotation);
            Assert.Equal((cols, rows), (frame.Cols, frame.Rows));
            Assert.Equal(expected, frame.Cells);
        }

        [Fact]
        public void AnHdrColor_ReadsAgainstTheDisplaysSdrWhite()
        {
            // scRGB's 1.0 is 80 nits, so at a white level of 80 nits it is white.
            Assert.Equal(0xFFFFFF, ScreenColorMath.ScRgbToRgb(1f, 1f, 1f, 80f));
            // At 200 nits, the same 80 nits is 40 percent of white, sRGB encoded.
            Assert.Equal(0xAAAAAA, ScreenColorMath.ScRgbToRgb(1f, 1f, 1f, 200f));
            Assert.Equal(0xFFFFFF, ScreenColorMath.ScRgbToRgb(2.5f, 2.5f, 2.5f, 200f));
            // Brighter than white and outside the sRGB gamut both clip.
            Assert.Equal(0xFF0000, ScreenColorMath.ScRgbToRgb(9f, -0.5f, float.NaN, 80f));
        }
    }

    /// <summary>A grabber a test scripts.</summary>
    internal sealed class ScriptedGrabber : IScreenGrabber
    {
        public readonly Queue<Func<(ScreenGrabResult Result, ScreenFrame Frame)>> Next = new();
        public int Disposed;
        public string Kind => "Scripted";
        public long LastWorkMs { get; set; }

        public void Say(ScreenGrabResult result, ScreenFrame frame = null) => Next.Enqueue(() => (result, frame));

        public ScreenGrabResult Grab(int timeoutMs, out ScreenFrame frame)
        {
            if (Next.Count == 0)
            {
                frame = null;
                return ScreenGrabResult.NoChange;
            }
            var (result, made) = Next.Dequeue()();
            frame = made;
            return result;
        }

        public void Dispose() => Disposed++;
    }

    /// <summary>Issue #559: the thread that reads one display, one pass at a
    /// time on a test's clock.</summary>
    public class ScreenCaptureWorkerTests
    {
        private long _now = 1000;
        private int _retires;
        private bool _refuseRetire;

        private ScreenCaptureWorker Worker(Func<string, IScreenGrabber> factory)
            => new("d", factory, () => _now, w =>
            {
                _retires++;
                return !_refuseRetire && w.IsIdle;
            });

        private static ScreenFrame OneCell(int rgb) => new(1, 1, new[] { rgb });

        [Fact]
        public void ItReadsWhileSomethingReadsIt_AndStopsAndLetsGoWhenNothingDoes()
        {
            var grabber = new ScriptedGrabber();
            int made = 0;
            var worker = Worker(_ => { made++; return grabber; });
            grabber.Say(ScreenGrabResult.Frame, OneCell(1));
            Assert.True(worker.Step() >= 0);
            Assert.Equal(1, made);
            Assert.Equal(ScreenCaptureState.Capturing, worker.State);
            Assert.Equal(1, worker.Latest.Cells[0]);

            // Up to the limit, the capture keeps running with no one asking.
            _now += ScreenCaptureWorker.IdleStopMs;
            Assert.True(worker.Step() >= 0);
            Assert.Equal(0, _retires);

            // A read at the last moment buys another full wait.
            worker.Touch();
            _now += ScreenCaptureWorker.IdleStopMs;
            Assert.True(worker.Step() >= 0);
            Assert.Equal(0, grabber.Disposed);

            _now += 1;
            Assert.Equal(-1, worker.Step());
            Assert.Equal(1, _retires);
            Assert.Equal(1, grabber.Disposed);
            Assert.Equal(ScreenCaptureState.Stopped, worker.State);
            Assert.Equal(1, made);
        }

        [Fact]
        public void AWorkerTheSourceWillNotRetire_KeepsCapturing()
        {
            // A read landed between the worker's look at the clock and the
            // source's lock, so the source says no.
            var grabber = new ScriptedGrabber();
            var worker = Worker(_ => grabber);
            _refuseRetire = true;
            _now += ScreenCaptureWorker.IdleStopMs + 1;
            Assert.True(worker.Step() >= 0);
            Assert.Equal(1, _retires);
            Assert.Equal(0, grabber.Disposed);
            Assert.Equal(ScreenCaptureState.Capturing, worker.State);
        }

        [Fact]
        public void ThePaceLeavesTheFrameTime_AndGivesReadingAQuarterOfTheTime()
        {
            var grabber = new ScriptedGrabber();
            var worker = Worker(_ => grabber);
            grabber.Say(ScreenGrabResult.Frame, OneCell(1));
            Assert.Equal(ScreenCaptureWorker.FrameMs, worker.Step());
            grabber.LastWorkMs = 3;
            grabber.Say(ScreenGrabResult.Frame, OneCell(2));
            Assert.Equal(ScreenCaptureWorker.FrameMs - 3, worker.Step());
            grabber.LastWorkMs = 40;
            grabber.Say(ScreenGrabResult.Frame, OneCell(3));
            Assert.Equal(120, worker.Step());
        }

        [Fact]
        public void NoChangeKeepsThePicture_AndAsksAgainAtOnce()
        {
            var grabber = new ScriptedGrabber();
            var worker = Worker(_ => grabber);
            grabber.Say(ScreenGrabResult.Frame, OneCell(7));
            worker.Step();
            var picture = worker.Latest;
            grabber.Say(ScreenGrabResult.NoChange);
            Assert.Equal(0, worker.Step());
            Assert.Same(picture, worker.Latest);
            Assert.Equal(ScreenCaptureState.Capturing, worker.State);
        }

        [Fact]
        public void ALostCaptureIsBuiltAgain_AndThePictureStaysMeanwhile()
        {
            var first = new ScriptedGrabber();
            var second = new ScriptedGrabber();
            var grabbers = new Queue<ScriptedGrabber>(new[] { first, second });
            var worker = Worker(_ => grabbers.Dequeue());
            first.Say(ScreenGrabResult.Frame, OneCell(1));
            worker.Step();
            first.Say(ScreenGrabResult.Lost);
            Assert.Equal(ScreenCaptureWorker.RetryMinMs, worker.Step());
            Assert.Equal(1, first.Disposed);
            Assert.Equal(ScreenCaptureState.Waiting, worker.State);
            Assert.Equal(1, worker.Latest.Cells[0]);

            second.Say(ScreenGrabResult.Frame, OneCell(2));
            worker.Step();
            Assert.Equal(ScreenCaptureState.Capturing, worker.State);
            Assert.Equal(2, worker.Latest.Cells[0]);
        }

        [Fact]
        public void AGrabThatThrows_IsALostCapture_NotADeadThread()
        {
            var first = new ScriptedGrabber();
            var second = new ScriptedGrabber();
            var grabbers = new Queue<ScriptedGrabber>(new[] { first, second });
            var worker = Worker(_ => grabbers.Dequeue());
            first.Next.Enqueue(() => throw new InvalidOperationException("the driver reset"));
            Assert.Equal(ScreenCaptureWorker.RetryMinMs, worker.Step());
            Assert.Equal(1, first.Disposed);
            Assert.Equal(ScreenCaptureState.Waiting, worker.State);
            second.Say(ScreenGrabResult.Frame, OneCell(2));
            worker.Step();
            Assert.Equal(ScreenCaptureState.Capturing, worker.State);
        }

        [Fact]
        public void AWaitOnWindowsRetries_MoreSlowlyEachTime_UpToTheLimit_AndAReturnResetsIt()
        {
            bool ready = false;
            var grabber = new ScriptedGrabber();
            var worker = Worker(_ => ready ? grabber : throw new ScreenGrabberWaiting("UAC"));
            var waits = new List<int>();
            for (int i = 0; i < 7; i++) waits.Add(worker.Step());
            Assert.Equal(new[] { 250, 500, 1000, 2000, 4000, 8000, 8000 }, waits);
            Assert.Equal(ScreenCaptureState.Waiting, worker.State);
            Assert.Null(worker.Latest);

            ready = true;
            grabber.Say(ScreenGrabResult.Frame, OneCell(1));
            worker.Step();
            Assert.Equal(ScreenCaptureState.Capturing, worker.State);

            // The next wait starts from the shortest again.
            grabber.Say(ScreenGrabResult.Lost);
            ready = false;
            Assert.Equal(ScreenCaptureWorker.RetryMinMs, worker.Step());
        }

        [Fact]
        public void NoWayOfReadingTheScreen_SaysSo_AndKeepsTrying()
        {
            bool ready = false;
            var grabber = new ScriptedGrabber();
            var worker = Worker(_ => ready ? grabber : throw new ScreenGrabberUnavailable("no way"));
            Assert.Equal(ScreenCaptureWorker.RetryMinMs, worker.Step());
            Assert.Equal(ScreenCaptureState.Unavailable, worker.State);
            ready = true;
            worker.Step();
            Assert.Equal(ScreenCaptureState.Capturing, worker.State);
        }

        [Fact]
        public void AnIdleWorkerStops_EvenWhileItCannotBuildACapture()
        {
            var worker = Worker(_ => throw new ScreenGrabberWaiting("UAC"));
            worker.Step();
            _now += ScreenCaptureWorker.IdleStopMs + 1;
            Assert.Equal(-1, worker.Step());
            Assert.Equal(ScreenCaptureState.Stopped, worker.State);
        }
    }

    /// <summary>The order of the two ways of reading a display.</summary>
    public class ScreenGrabberOrderTests
    {
        [Fact]
        public void TheFirstWayRuns_WhenItCan()
        {
            var first = new ScriptedGrabber();
            Assert.Same(first, ScreenColorSource.FirstThatRuns("d", _ => first, _ => throw new InvalidOperationException()));
        }

        [Fact]
        public void TheSecondWayRuns_WhenTheFirstCannotOnThisPC()
        {
            var second = new ScriptedGrabber();
            Assert.Same(second, ScreenColorSource.FirstThatRuns("d", _ => throw new ScreenGrabberUnavailable("hybrid GPU"), _ => second));
        }

        [Fact]
        public void AWaitOnWindowsIsNoReasonToChangeWays()
        {
            Assert.Throws<ScreenGrabberWaiting>(() => ScreenColorSource.FirstThatRuns("d",
                _ => throw new ScreenGrabberWaiting("UAC"),
                _ => throw new InvalidOperationException("the second way ran")));
        }

        [Fact]
        public void TheSecondWaysOwnRefusalReachesTheWorker()
        {
            var e = Assert.Throws<ScreenGrabberUnavailable>(() => ScreenColorSource.FirstThatRuns("d",
                _ => throw new ScreenGrabberUnavailable("first"),
                _ => throw new ScreenGrabberUnavailable("second")));
            Assert.Equal("second", e.Message);
        }
    }

    [CollectionDefinition("ScreenColorStatics", DisableParallelization = true)]
    public class ScreenColorStaticsCollection { }

    /// <summary>Issue #559: what a device's settings get from the screen, through
    /// the source's seams.</summary>
    [Collection("ScreenColorStatics")]
    public sealed class ScreenColorSourceTests : IDisposable
    {
        private const string Display = @"\\.\DISPLAY559";

        private long _now = 5000;
        private readonly ConcurrentDictionary<string, ScreenCaptureWorker> _started = new();
        private readonly ConcurrentDictionary<string, int> _starts = new();
        private readonly ScriptedGrabber _grabber = new();

        public ScreenColorSourceTests()
        {
            ScreenColorSource.ResetForTest();
            ScreenColorSource.ClockSeam = () => Volatile.Read(ref _now);
            ScreenColorSource.GrabberSeam = _ => _grabber;
            ScreenColorSource.StartSeam = w =>
            {
                _started[w.Display] = w;
                _starts.AddOrUpdate(w.Display, 1, (_, n) => n + 1);
            };
        }

        public void Dispose()
        {
            ScreenColorSource.GrabberSeam = null;
            ScreenColorSource.ClockSeam = null;
            ScreenColorSource.StartSeam = null;
            ScreenColorSource.ResetForTest();
        }

        private static DeviceSlotConfig Config(ScreenColorMethod method = ScreenColorMethod.Average, int smoothing = 0) => new()
        {
            LightbarMode = LightbarMode.ScreenColor,
            ScreenColorDisplay = Display,
            ScreenColorMethod = method,
            ScreenColorSmoothing = smoothing,
        };

        private void Show(ScreenFrame frame)
        {
            _grabber.Say(ScreenGrabResult.Frame, frame);
            _started[Display].Step();
        }

        private static ScreenFrame Solid(int rgb, int cols = 4, int rows = 4)
            => new(cols, rows, Enumerable.Repeat(rgb, cols * rows).ToArray());

        [Fact]
        public void ReadingStartsOneCapturePerDisplay_ForEveryDeviceThatReadsIt()
        {
            ScreenColorSource.ColorFor(Config());
            ScreenColorSource.ColorFor(Config());
            ScreenColorSource.PictureFor(Config());
            Assert.Equal(1, _starts[Display]);

            var other = Config();
            other.ScreenColorDisplay = @"\\.\DISPLAY560";
            ScreenColorSource.ColorFor(other);
            Assert.Equal(1, _starts[Display]);
            Assert.Equal(1, _starts[other.ScreenColorDisplay]);
        }

        [Fact]
        public void TheLightIsBlack_UntilTheFirstPicture_AndThenTakesItAtOnce()
        {
            var cfg = Config(smoothing: 100);
            Assert.Equal(((byte)0, (byte)0, (byte)0), ScreenColorSource.ColorFor(cfg));
            Show(Solid(0x204060));
            // With the slowest smoothing, the first color still arrives whole
            // rather than fading up from black.
            Assert.Equal(((byte)0x20, (byte)0x40, (byte)0x60), ScreenColorSource.ColorFor(cfg));
        }

        [Fact]
        public void SmoothingFadesOverTime_AndNotOverReads()
        {
            var cfg = Config(smoothing: 20);
            ScreenColorSource.ColorFor(cfg);
            Show(Solid(0x000000));
            ScreenColorSource.ColorFor(cfg);
            Show(Solid(0xFF0000));

            // No time has passed, so reading any number of times moves nothing.
            for (int i = 0; i < 5; i++)
                Assert.Equal(((byte)0, (byte)0, (byte)0), ScreenColorSource.ColorFor(cfg));

            // 20 percent is 100 ms, and one time constant is 1 - 1/e of the way.
            _now += 100;
            Assert.Equal(((byte)161, (byte)0, (byte)0), ScreenColorSource.ColorFor(cfg));
            _now += 2000;
            Assert.Equal(((byte)255, (byte)0, (byte)0), ScreenColorSource.ColorFor(cfg));
        }

        [Fact]
        public void TheMethodSaturationAndBrightness_AreAppliedInTheirOrder()
        {
            var scene = new ScreenFrame(10, 10, Enumerable.Repeat(0, 90).Concat(Enumerable.Repeat(0xFF0000, 10)).ToArray());
            var common = Config(ScreenColorMethod.MostCommon);
            ScreenColorSource.ColorFor(common);
            Show(scene);
            Assert.Equal(((byte)255, (byte)0, (byte)0), ScreenColorSource.ColorFor(common));

            var average = Config();
            Assert.Equal(((byte)0x1A, (byte)0, (byte)0), ScreenColorSource.ColorFor(average));

            var dim = Config(ScreenColorMethod.MostCommon);
            dim.ScreenColorBrightness = 50;
            Assert.Equal(((byte)128, (byte)0, (byte)0), ScreenColorSource.ColorFor(dim));

            var pale = Config(ScreenColorMethod.MostCommon);
            pale.ScreenColorSaturation = 0;
            Assert.Equal(((byte)0x4F, (byte)0x4F, (byte)0x4F), ScreenColorSource.ColorFor(pale));
        }

        [Fact]
        public void EachAreaReadsItsOwnPartOfTheDisplay()
        {
            // Nine cells with nine colors.
            var cells = Enumerable.Range(1, 9).Select(i => i * 0x101010).ToArray();
            ScreenColorSource.ColorFor(Config());
            Show(new ScreenFrame(3, 3, cells));
            var names = new[]
            {
                ScreenColorArea.TopLeft, ScreenColorArea.Top, ScreenColorArea.TopRight,
                ScreenColorArea.Left, ScreenColorArea.Center, ScreenColorArea.Right,
                ScreenColorArea.BottomLeft, ScreenColorArea.Bottom, ScreenColorArea.BottomRight,
            };
            for (int i = 0; i < 9; i++)
            {
                var cfg = Config();
                cfg.ScreenColorArea = names[i];
                byte v = (byte)((i + 1) * 0x10);
                Assert.Equal((v, v, v), ScreenColorSource.ColorFor(cfg));
            }
            var custom = Config();
            custom.ScreenColorArea = ScreenColorArea.Custom;
            custom.ScreenColorLeft = 34;
            custom.ScreenColorTop = 67;
            custom.ScreenColorWidth = 30;
            custom.ScreenColorHeight = 33;
            Assert.Equal(((byte)0x80, (byte)0x80, (byte)0x80), ScreenColorSource.ColorFor(custom));
        }

        [Fact]
        public void TheLastColorStays_WhileWindowsHoldsTheScreen()
        {
            var cfg = Config();
            ScreenColorSource.ColorFor(cfg);
            Show(Solid(0x336699));
            Assert.Equal(((byte)0x33, (byte)0x66, (byte)0x99), ScreenColorSource.ColorFor(cfg));
            _grabber.Say(ScreenGrabResult.Lost);
            _started[Display].Step();
            Assert.Equal(ScreenCaptureState.Waiting, ScreenColorSource.StateOf(Display));
            Assert.Equal(((byte)0x33, (byte)0x66, (byte)0x99), ScreenColorSource.ColorFor(cfg));
        }

        [Fact]
        public void AskingWhatACaptureIsDoing_NeitherStartsOneNorKeepsOneRunning()
        {
            Assert.Equal(ScreenCaptureState.Stopped, ScreenColorSource.StateOf(Display));
            Assert.False(_starts.ContainsKey(Display));

            ScreenColorSource.ColorFor(Config());
            var worker = _started[Display];
            for (int i = 0; i < 3; i++)
            {
                _now += ScreenCaptureWorker.IdleStopMs;
                Assert.NotEqual(ScreenCaptureState.Stopped, ScreenColorSource.StateOf(Display));
            }
            Assert.Equal(-1, worker.Step());
            Assert.Equal(ScreenCaptureState.Stopped, ScreenColorSource.StateOf(Display));
            Assert.Null(ScreenColorSource.WorkerForTest(Display));
        }

        [Fact]
        public void ACaptureNothingReads_RetiresItself_AndTheNextReadStartsAFreshOne()
        {
            ScreenColorSource.ColorFor(Config());
            var first = _started[Display];
            Show(Solid(0x112233));
            _now += ScreenCaptureWorker.IdleStopMs + 1;
            Assert.Equal(-1, first.Step());
            Assert.Null(ScreenColorSource.WorkerForTest(Display));
            Assert.Equal(1, _grabber.Disposed);

            ScreenColorSource.ColorFor(Config());
            Assert.Equal(2, _starts[Display]);
            Assert.NotSame(first, _started[Display]);
        }

        [Fact]
        public void AReadJustBeforeRetiring_KeepsTheCaptureRunning()
        {
            ScreenColorSource.ColorFor(Config());
            var worker = _started[Display];
            _now += ScreenCaptureWorker.IdleStopMs + 1;
            ScreenColorSource.ColorFor(Config());
            Assert.True(worker.Step() >= 0);
            Assert.Same(worker, ScreenColorSource.WorkerForTest(Display));
            Assert.Equal(1, _starts[Display]);
        }

        [Fact]
        public void APictureIsNullUntilTheFirstFrame_AndKeepsTheDisplaysShape()
        {
            var cfg = Config();
            Assert.Null(ScreenColorSource.PictureFor(cfg));
            var cells = new[] { 0x010101, 0x020202, 0x030303, 0x040404 };
            Show(new ScreenFrame(2, 2, cells));
            var picture = ScreenColorSource.PictureFor(cfg);
            Assert.Equal((2, 2), (picture.Cols, picture.Rows));
            Assert.Equal(0x010101, picture.At(0f, 0f));
            Assert.Equal(0x020202, picture.At(0.9f, 0f));
            Assert.Equal(0x030303, picture.At(0f, 0.9f));
            Assert.Equal(0x040404, picture.At(0.9f, 0.9f));
        }

        [Fact]
        public void APictureIsTheSameInstance_WhileNothingOnItChanges()
        {
            var cfg = Config();
            ScreenColorSource.PictureFor(cfg);
            Show(Solid(0x445566));
            var first = ScreenColorSource.PictureFor(cfg);
            _now += 100;
            Assert.Same(first, ScreenColorSource.PictureFor(cfg));
            // A new frame showing the same thing is still nothing new.
            Show(Solid(0x445566));
            Assert.Same(first, ScreenColorSource.PictureFor(cfg));
            Show(Solid(0x445567));
            Assert.NotSame(first, ScreenColorSource.PictureFor(cfg));
        }

        [Fact]
        public void APictureHoldsAtMostItsLimit_WithEachLightShowingItsWholePart()
        {
            var cfg = Config();
            ScreenColorSource.PictureFor(cfg);
            // The downscaler's largest picture on a 16:10 display.
            var cells = new int[64 * 40];
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 64; x++)
                    cells[y * 64 + x] = x % 2 == 0 ? 0x000000 : 0x640000;
            Show(new ScreenFrame(64, 40, cells));
            var picture = ScreenColorSource.PictureFor(cfg);
            Assert.Equal((ScreenColorSource.PictureMaxCols, ScreenColorSource.PictureMaxRows), (picture.Cols, picture.Rows));
            // Every picture cell is the mean of two columns, one dark and one
            // red, and not one of them by chance.
            for (int y = 0; y < picture.Rows; y++)
                for (int x = 0; x < picture.Cols; x++)
                    Assert.Equal(0x320000, picture.At((x + 0.5f) / picture.Cols, (y + 0.5f) / picture.Rows));
        }

        [Fact]
        public void APictureCoversTheWholeArea_ToTheLastRowAndColumn()
        {
            var cfg = Config();
            ScreenColorSource.PictureFor(cfg);
            // A size the limits do not divide evenly, lit only at its far corner.
            var cells = new int[63 * 39];
            cells[^1] = 0xFF0000;
            Show(new ScreenFrame(63, 39, cells));
            var picture = ScreenColorSource.PictureFor(cfg);
            Assert.NotEqual(0, picture.At(0.999f, 0.999f));
            Assert.Equal(0, picture.At(0.5f, 0.5f));
        }

        [Fact]
        public void APictureTakesTheAreaSaturationAndBrightness()
        {
            var cfg = Config();
            cfg.ScreenColorArea = ScreenColorArea.BottomRight;
            cfg.ScreenColorBrightness = 50;
            ScreenColorSource.PictureFor(cfg);
            var cells = Enumerable.Repeat(0, 8).Append(0xFF0000).ToArray();
            Show(new ScreenFrame(3, 3, cells));
            var picture = ScreenColorSource.PictureFor(cfg);
            Assert.Equal((1, 1), (picture.Cols, picture.Rows));
            Assert.Equal(0x800000, picture.At(0.5f, 0.5f));
        }

        [Fact]
        public void APicturesSmoothing_FadesEachCell()
        {
            var cfg = Config(smoothing: 20);
            ScreenColorSource.PictureFor(cfg);
            Show(new ScreenFrame(2, 1, new[] { 0x000000, 0xFF0000 }));
            var first = ScreenColorSource.PictureFor(cfg);
            Assert.Equal(0x000000, first.At(0f, 0f));
            Assert.Equal(0xFF0000, first.At(0.9f, 0f));
            Show(new ScreenFrame(2, 1, new[] { 0xFF0000, 0x000000 }));
            _now += 100;
            var fading = ScreenColorSource.PictureFor(cfg);
            Assert.Equal(0xA10000, fading.At(0f, 0f));
            Assert.Equal(0x5E0000, fading.At(0.9f, 0f));
        }

        [Fact]
        public void TheTestHostReadsNothing_WithoutASeam()
        {
            ScreenColorSource.GrabberSeam = null;
            var cfg = Config();
            Assert.Equal(((byte)0, (byte)0, (byte)0), ScreenColorSource.ColorFor(cfg));
            Assert.Null(ScreenColorSource.PictureFor(cfg));
            Assert.Null(ScreenColorSource.WorkerForTest(Display));
        }

        [Fact]
        public void TheModeGivesEveryLightbarTheScreensColor()
        {
            var cfg = Config();
            ScreenColorSource.ColorFor(cfg);
            Show(Solid(0x0A141E));

            // The core every lit device shares, and the DualSense's adapter.
            Ds4EffectSynthesizer.ResolveLightbarRgb(cfg, 0f, 0, 0, 0, 0f, 100, 1, out byte r, out byte g, out byte b);
            Assert.Equal(((byte)0x0A, (byte)0x14, (byte)0x1E), (r, g, b));
            Assert.Equal(((byte)0x0A, (byte)0x14, (byte)0x1E), Ds5EffectSynthesizer.ComputeLightbarColorPublic(cfg, 0f, 0, 0, 0, 0f, 100));
        }
    }

    /// <summary>Where a device's lights sit, for a picture to spread over.</summary>
    public class LightPlacementTests
    {
        [Fact]
        public void LightsWithPositions_SitWhereTheDeviceReports_FromEdgeToEdge()
        {
            var map = new LightMap(new[]
            {
                new LightInfo(0, LightKind.Key, 0x04, LightKeySource.Device) { X = 0, Y = 0, Width = 2, Height = 2, HasPosition = true },
                new LightInfo(1, LightKind.Key, 0x05, LightKeySource.Device) { X = 8, Y = 8, Width = 2, Height = 2, HasPosition = true },
                new LightInfo(2, LightKind.Key, 0x06, LightKeySource.Device) { X = 4, Y = 0, Width = 2, Height = 10, HasPosition = true },
                new LightInfo(3, LightKind.Logo, 0, LightKeySource.None),
            }, "positions");
            var places = LightPlacement.Of(map);
            Assert.Equal(new[] { 0.1f, 0.1f, 0.9f, 0.9f, 0.5f, 0.5f }, places.Take(6));
            Assert.True(float.IsNaN(places[6]) && float.IsNaN(places[7]));
        }

        [Fact]
        public void KeysWithoutPositions_SitWhereTheLayoutDrawsThem()
        {
            // Esc, A and the right arrow: enough of a keyboard for a layout.
            var map = new LightMap(new[]
            {
                new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Device),
                new LightInfo(1, LightKind.Key, 0x04, LightKeySource.Device),
                new LightInfo(2, LightKind.Key, 0x4F, LightKeySource.Device),
                new LightInfo(3, LightKind.Key, 0, LightKeySource.None),
            }, "layout");
            var places = LightPlacement.Of(map);
            // Esc is at the top left, A is left of center, the arrow is right of A.
            Assert.InRange(places[0], 0f, 0.1f);
            Assert.InRange(places[1], 0f, 0.2f);
            Assert.InRange(places[2], 0f, 0.25f);
            Assert.True(places[3] > places[1], "A sits below Esc");
            Assert.True(places[4] > places[2], "the arrow sits right of A");
            // A key no source named has no place on the layout.
            Assert.True(float.IsNaN(places[6]));
        }

        [Fact]
        public void ZonesAndEdges_SitInOrderFromLeftToRight()
        {
            var map = new LightMap(new[]
            {
                new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
                new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
                new LightInfo(2, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(3, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(4, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(5, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(6, LightKind.Logo, 0, LightKeySource.None),
            }, "zones");
            var places = LightPlacement.Of(map);
            Assert.Equal(0.75f, places[0]);
            Assert.Equal(0.25f, places[2]);
            Assert.Equal(0.125f, places[4]);
            Assert.Equal(0.375f, places[6]);
            Assert.Equal(0.625f, places[8]);
            Assert.Equal(0.875f, places[10]);
            Assert.Equal(0.5f, places[1]);
            Assert.True(float.IsNaN(places[12]), "a logo is nowhere in particular");
        }

        [Fact]
        public void ADeviceWithOneLightKindAndNoPlaces_HasNoPlaces()
        {
            var map = new LightMap(new[] { new LightInfo(0, LightKind.Logo, 0, LightKeySource.None) }, "logo");
            Assert.All(LightPlacement.Of(map), v => Assert.True(float.IsNaN(v)));
        }

        [Fact]
        public void TheBestLayoutIsTheOneHoldingTheMostKeys_AndTheSmallerOfATie()
        {
            var numpadKey = new LightMap(new[] { new LightInfo(0, LightKind.Key, 0x59, LightKeySource.Device) }, "numpad");
            var layout = LightPlacement.BestLayout(numpadKey);
            Assert.Contains(layout, k => k.Page == 0x07 && k.Id == 0x59);

            var onlyA = new LightMap(new[] { new LightInfo(0, LightKind.Key, 0x04, LightKeySource.Device) }, "a");
            int smallest = new[]
            {
                PadForge.Views.KeyboardFamilies.FullsizeAnsi, PadForge.Views.KeyboardFamilies.Tkl,
                PadForge.Views.KeyboardFamilies.SeventyFive, PadForge.Views.KeyboardFamilies.Sixty,
            }.Min(l => l.Length);
            Assert.True(LightPlacement.BestLayout(onlyA).Length <= smallest);

            Assert.Null(LightPlacement.BestLayout(new LightMap(new[] { new LightInfo(0, LightKind.Logo, 0, LightKeySource.None) }, "logo")));
        }
    }

    /// <summary>The renderer every backend calls, with a picture in the frame.</summary>
    public class ScreenPictureRenderTests
    {
        private static LightMap Map() => new(new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
            new LightInfo(2, LightKind.Logo, 0, LightKeySource.None),
        }, "render");

        private static readonly ScreenPicture LeftRight = new(2, 1, new[] { 0x110000, 0x220000 });

        [Fact]
        public void ALightWithAPlace_ShowsThePictureUnderIt_AndOneWithoutShowsTheDevicesColor()
        {
            var frame = KeyFrame.Empty.WithPicture(LeftRight);
            Assert.Equal(new[] { 0x110000, 0x220000, 0x0000AA }, LightRenderer.Render(Map(), 0x0000AA, frame));
        }

        [Fact]
        public void AColorSetOnALightKeyOrGroup_StaysOverThePicture()
        {
            var frame = new KeyFrame(null, new Dictionary<string, int> { ["zone1"] = 0x00FF00 }, null, null).WithPicture(LeftRight);
            Assert.Equal(new[] { 0x00FF00, 0x220000, 0x0000AA }, LightRenderer.Render(Map(), 0x0000AA, frame));
        }

        [Fact]
        public void ABackgroundAFrameSets_CoversThePicture_AtAnyDepth()
        {
            var off = new KeyFrame(null, null, null, background: 0).WithPicture(LeftRight);
            Assert.Equal(new[] { 0, 0, 0 }, LightRenderer.Render(Map(), 0x0000AA, off));

            var beneath = new KeyFrame(null, new Dictionary<string, int> { ["logo"] = 1 }, null, null,
                new KeyFrame(null, null, null, background: 0x050505)).WithPicture(LeftRight);
            Assert.Equal(new[] { 0x050505, 0x050505, 1 }, LightRenderer.Render(Map(), 0x0000AA, beneath));
        }

        [Fact]
        public void APictureIsPartOfAFramesEquality()
        {
            var a = KeyFrame.Empty.WithPicture(new ScreenPicture(1, 1, new[] { 1 }));
            var same = KeyFrame.Empty.WithPicture(new ScreenPicture(1, 1, new[] { 1 }));
            var other = KeyFrame.Empty.WithPicture(new ScreenPicture(1, 1, new[] { 2 }));
            Assert.Equal(a, same);
            Assert.Equal(a.GetHashCode(), same.GetHashCode());
            Assert.NotEqual(a, other);
            Assert.NotEqual(a, KeyFrame.Empty);
            Assert.False(a.IsEmpty);
        }

        [Fact]
        public void APictureSurvivesDroppingTheFramesSingleLights()
        {
            var frame = new KeyFrame(null, null, new Dictionary<int, int> { [0] = 1 }, null).WithPicture(LeftRight);
            Assert.Same(LeftRight, frame.WithoutLights.Picture);
            Assert.Empty(frame.WithoutLights.Lights);
        }

        [Fact]
        public void TwoPicturesWithTheSameCellsAreEqual_AndADifferentShapeIsNot()
        {
            var a = new ScreenPicture(2, 1, new[] { 1, 2 });
            Assert.Equal(a, new ScreenPicture(2, 1, new[] { 1, 2 }));
            Assert.NotEqual(a, new ScreenPicture(1, 2, new[] { 1, 2 }));
            Assert.NotEqual(a, new ScreenPicture(2, 1, new[] { 1, 3 }));
            Assert.True(a.SameCells(2, 1, new[] { 1, 2 }));
            Assert.False(a.SameCells(1, 2, new[] { 1, 2 }));
        }

        [Theory]
        [InlineData(-1f, -1f, 1)]
        [InlineData(0f, 0f, 1)]
        [InlineData(0.49f, 0.99f, 1)]
        [InlineData(0.5f, 0f, 2)]
        [InlineData(1f, 1f, 2)]
        [InlineData(5f, 5f, 2)]
        public void APlaceOnThePicture_FindsItsCell_AndStaysInside(float across, float down, int expected)
        {
            Assert.Equal(expected, new ScreenPicture(2, 1, new[] { 1, 2 }).At(across, down));
        }
    }

    /// <summary>Issue #559: the spread reaches a lit device's claim only while
    /// the base mode shows, through the pass that lights a peripheral.</summary>
    [Collection("PeripheralOutputStatics")]
    public sealed class ScreenColorSpreadClaimTests : IDisposable
    {
        private static readonly Guid Id = new("00000000-0000-0000-0000-000000000559");
        private const string Display = @"\\.\DISPLAY561";

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly List<int> _order = SettingsManager.XboxSlotOrder;
        private readonly Func<int, IReadOnlyDictionary<Guid, DeviceSlotConfig>> _configs = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        private readonly ConcurrentDictionary<string, ScreenCaptureWorker> _started = new();
        private readonly ScriptedGrabber _grabber = new();
        private readonly OutputPath _path = new(OutputFamily.HidppUnit, "rx|59");
        private readonly DeviceSlotConfig _cfg;
        private readonly UserEffectsDispatcher _dispatcher;

        public ScreenColorSpreadClaimTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralOutputs.SetRunning(true);
            GameLightbarCapture.ResetForTest();
            ScreenColorSource.ResetForTest();
            ScreenColorSource.GrabberSeam = _ => _grabber;
            ScreenColorSource.StartSeam = w => _started[w.Display] = w;

            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotCreated = new bool[16];
            SettingsManager.SlotEnabled = new bool[16];
            SettingsManager.SlotCreated[15] = SettingsManager.SlotEnabled[15] = true;
            SettingsManager.XboxSlotOrder = new List<int> { 15 };
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Id, IsOnline = true, IsEnabled = true, CapType = InputDeviceType.Mouse,
                VendorId = PeripheralLinker.LogitechVid, ProdId = 0xC548,
                PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
            });
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = Id, MapTo = 15 });
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Id, Lighting = new[] { _path } } }));

            _cfg = new DeviceSlotConfig
            {
                LightbarMode = LightbarMode.ScreenColor,
                ScreenColorDisplay = Display,
                ScreenColorSmoothing = 0,
                ScreenColorMethod = ScreenColorMethod.Average,
                PeripheralLightingEnabled = true,
            };
            var configs = new Dictionary<Guid, DeviceSlotConfig> { [Id] = _cfg };
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ => configs;
            _dispatcher = new UserEffectsDispatcher(15, _cfg, startTimer: false);
        }

        public void Dispose()
        {
            _dispatcher.Dispose();
            GameLightbarCapture.ResetForTest();
            PeripheralOutputs.ResetForTest();
            ScreenColorSource.GrabberSeam = null;
            ScreenColorSource.StartSeam = null;
            ScreenColorSource.ResetForTest();
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _configs;
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            SettingsManager.XboxSlotOrder = _order;
        }

        private void Show(int rgbLeft, int rgbRight)
        {
            _grabber.Say(ScreenGrabResult.Frame, new ScreenFrame(2, 1, new[] { rgbLeft, rgbRight }));
            _started[Display].Step();
        }

        private PeripheralOutputs.Claimant Lit(out int rgb)
        {
            _dispatcher.ApplyOnce();
            Assert.True(PeripheralOutputs.TryResolveColor(_path, out rgb, out var ruler));
            return ruler;
        }

        [Fact]
        public void TheClaimCarriesThePicture_AndTheColorForLightsWithNoPlace()
        {
            Lit(out _);
            Show(0x100000, 0x300000);
            var claim = Lit(out int rgb);
            Assert.Equal(0x200000, rgb);
            Assert.Equal(0x100000, claim.Keys.Picture.At(0f, 0f));
            Assert.Equal(0x300000, claim.Keys.Picture.At(0.9f, 0f));
        }

        [Fact]
        public void WithSpreadOff_TheDeviceShowsOneColor()
        {
            _cfg.ScreenColorSpread = false;
            Lit(out _);
            Show(0x100000, 0x300000);
            var claim = Lit(out int rgb);
            Assert.Equal(0x200000, rgb);
            Assert.True(claim.Keys == null || claim.Keys.Picture == null);
        }

        [Fact]
        public void AnotherMode_HasNoPicture()
        {
            Lit(out _);
            Show(0x100000, 0x300000);
            _cfg.LightbarMode = LightbarMode.Static;
            var claim = Lit(out _);
            Assert.True(claim.Keys == null || claim.Keys.Picture == null);
        }

        [Fact]
        public void BeforeTheFirstPicture_TheClaimHasNoPicture()
        {
            var claim = Lit(out int rgb);
            Assert.Equal(0, rgb);
            Assert.True(claim.Keys == null || claim.Keys.Picture == null);
        }

        [Fact]
        public void AGamesLightbar_CoversThePicture_AndThePictureComesBackAfterIt()
        {
            Lit(out _);
            Show(0x100000, 0x300000);
            var bar = new GameLightbar();
            GameLightbarCapture.Publish(15, bar);
            bar.Capture(0xAB, 0xCD, 0xEF, Environment.TickCount64);
            var claim = Lit(out int rgb);
            Assert.Equal(0xABCDEF, rgb);
            Assert.True(claim.Keys == null || claim.Keys.Picture == null);

            GameLightbarCapture.ResetForTest();
            Assert.NotNull(Lit(out _).Keys.Picture);
        }

        [Fact]
        public void AMacroColor_CoversThePicture_AndThePictureComesBackAfterIt()
        {
            Lit(out _);
            Show(0x100000, 0x300000);
            _cfg.MacroOverrideR = 1;
            _cfg.MacroOverrideG = 2;
            _cfg.MacroOverrideB = 3;
            _cfg.MacroOverrideHoldMode = MacroLightbarHoldMode.Sticky;
            _cfg.MacroOverrideExpiresAtUtc = DateTime.MaxValue;
            var claim = Lit(out int rgb);
            Assert.Equal(0x010203, rgb);
            Assert.True(claim.Keys == null || claim.Keys.Picture == null);

            _cfg.ClearMacroOverride();
            Assert.NotNull(Lit(out _).Keys.Picture);
        }

        [Fact]
        public void KeyColorsStayOverThePicture_InTheSameFrame()
        {
            _cfg.KeyLighting = "Base>k04=00FF00";
            Lit(out _);
            Show(0x100000, 0x300000);
            var claim = Lit(out _);
            Assert.Equal(0x00FF00, claim.Keys.Keys[0x04]);
            Assert.NotNull(claim.Keys.Picture);
        }

        [Fact]
        public void TheSamePicture_DoesNotWakeTheBackends_AndANewOneDoes()
        {
            Lit(out _);
            Show(0x100000, 0x300000);
            Lit(out _);
            int version = PeripheralOutputs.LightingVersion;
            Lit(out _);
            Assert.Equal(version, PeripheralOutputs.LightingVersion);
            Show(0x100000, 0x400000);
            Lit(out _);
            Assert.True(PeripheralOutputs.LightingVersion > version);
        }
    }

    /// <summary>The settings of the mode: saved, copied, reset and listed.</summary>
    public class ScreenColorSettingsTests
    {
        [Fact]
        public void TheModesNumbersNeverMove()
        {
            Assert.Equal(17, (int)LightbarMode.ScreenColor);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, Enum.GetValues<ScreenColorArea>().Select(a => (int)a));
            Assert.Equal(new[]
            {
                "WholeScreen", "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight", "Custom",
            }, Enum.GetNames<ScreenColorArea>());
            Assert.Equal(0, (int)ScreenColorMethod.MostCommon);
            Assert.Equal(1, (int)ScreenColorMethod.Average);
        }

        [Fact]
        public void TheDefaultsAreTheWholePrimaryDisplay_InItsMostCommonColor()
        {
            var cfg = new DeviceSlotConfig();
            Assert.Equal(string.Empty, cfg.ScreenColorDisplay);
            Assert.Equal(ScreenColorArea.WholeScreen, cfg.ScreenColorArea);
            Assert.Equal(ScreenColorMethod.MostCommon, cfg.ScreenColorMethod);
            Assert.Equal((25, 25, 50, 50), (cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight));
            Assert.Equal((100, 100, 30), (cfg.ScreenColorSaturation, cfg.ScreenColorBrightness, cfg.ScreenColorSmoothing));
            Assert.True(cfg.ScreenColorSpread);
        }

        private static DeviceSlotConfig Changed() => new()
        {
            LightbarMode = LightbarMode.ScreenColor,
            ScreenColorDisplay = @"\\.\DISPLAY2",
            ScreenColorArea = ScreenColorArea.Custom,
            ScreenColorLeft = 11,
            ScreenColorTop = 12,
            ScreenColorWidth = 13,
            ScreenColorHeight = 14,
            ScreenColorMethod = ScreenColorMethod.Average,
            ScreenColorSaturation = 150,
            ScreenColorBrightness = 40,
            ScreenColorSmoothing = 70,
            ScreenColorSpread = false,
        };

        private static void AssertChanged(DeviceSlotConfig cfg)
        {
            Assert.Equal(LightbarMode.ScreenColor, cfg.LightbarMode);
            Assert.Equal(@"\\.\DISPLAY2", cfg.ScreenColorDisplay);
            Assert.Equal(ScreenColorArea.Custom, cfg.ScreenColorArea);
            Assert.Equal((11, 12, 13, 14), (cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight));
            Assert.Equal(ScreenColorMethod.Average, cfg.ScreenColorMethod);
            Assert.Equal((150, 40, 70), (cfg.ScreenColorSaturation, cfg.ScreenColorBrightness, cfg.ScreenColorSmoothing));
            Assert.False(cfg.ScreenColorSpread);
        }

        [Fact]
        public void EverySettingSurvivesTheSettingsFile()
        {
            var data = SettingsService.BuildDeviceSlotConfigData(Changed(), 3, Guid.Empty);
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(DeviceSlotConfigData));
            using var text = new System.IO.StringWriter();
            ser.Serialize(text, data);
            using var reader = new System.IO.StringReader(text.ToString());
            var back = (DeviceSlotConfigData)ser.Deserialize(reader);
            var cfg = new DeviceSlotConfig();
            SettingsService.ApplyDeviceSlotConfigData(cfg, back);
            AssertChanged(cfg);
        }

        [Fact]
        public void ASettingsFileFromBeforeThisMode_ReadsAsTheDefaults()
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(DeviceSlotConfigData));
            using var reader = new System.IO.StringReader("<DeviceSlotConfigData SlotIndex=\"0\" />");
            var cfg = Changed();
            SettingsService.ApplyDeviceSlotConfigData(cfg, (DeviceSlotConfigData)ser.Deserialize(reader));
            Assert.Equal(string.Empty, cfg.ScreenColorDisplay);
            Assert.Equal(ScreenColorArea.WholeScreen, cfg.ScreenColorArea);
            Assert.Equal(ScreenColorMethod.MostCommon, cfg.ScreenColorMethod);
            Assert.True(cfg.ScreenColorSpread);
        }

        [Fact]
        public void TheModeCountsAsConfigured_ForACopy()
        {
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(new DeviceSlotConfigData { LightbarMode = LightbarMode.ScreenColor }));
        }

        [Fact]
        public void EachResetPutsItsOneSettingBack()
        {
            var cfg = Changed();
            cfg.ResetScreenColorDisplayCommand.Execute(null);
            Assert.Equal(string.Empty, cfg.ScreenColorDisplay);
            cfg.ResetScreenColorAreaCommand.Execute(null);
            Assert.Equal(ScreenColorArea.WholeScreen, cfg.ScreenColorArea);
            // The rectangle has resets of its own, so going to a named area
            // and back keeps it.
            Assert.Equal((11, 12, 13, 14), (cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight));
            cfg.ResetScreenColorLeftCommand.Execute(null);
            Assert.Equal((25, 12, 13, 14), (cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight));
            cfg.ResetScreenColorTopCommand.Execute(null);
            Assert.Equal((25, 25, 13, 14), (cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight));
            cfg.ResetScreenColorWidthCommand.Execute(null);
            Assert.Equal((25, 25, 50, 14), (cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight));
            cfg.ResetScreenColorHeightCommand.Execute(null);
            Assert.Equal((25, 25, 50, 50), (cfg.ScreenColorLeft, cfg.ScreenColorTop, cfg.ScreenColorWidth, cfg.ScreenColorHeight));
            cfg.ResetScreenColorMethodCommand.Execute(null);
            Assert.Equal(ScreenColorMethod.MostCommon, cfg.ScreenColorMethod);
            cfg.ResetScreenColorSaturationCommand.Execute(null);
            Assert.Equal(100, cfg.ScreenColorSaturation);
            cfg.ResetScreenColorBrightnessCommand.Execute(null);
            Assert.Equal(100, cfg.ScreenColorBrightness);
            cfg.ResetScreenColorSmoothingCommand.Execute(null);
            Assert.Equal(30, cfg.ScreenColorSmoothing);
            cfg.ResetScreenColorSpreadCommand.Execute(null);
            Assert.True(cfg.ScreenColorSpread);
        }

        [Theory]
        [InlineData(250, 200)]
        [InlineData(-5, 0)]
        public void SaturationStaysInItsRange(int given, int kept)
        {
            Assert.Equal(kept, new DeviceSlotConfig { ScreenColorSaturation = given }.ScreenColorSaturation);
        }

        [Theory]
        [InlineData(250, 100)]
        [InlineData(-5, 0)]
        public void BrightnessAndSmoothingStayInTheirRange(int given, int kept)
        {
            Assert.Equal(kept, new DeviceSlotConfig { ScreenColorBrightness = given }.ScreenColorBrightness);
            Assert.Equal(kept, new DeviceSlotConfig { ScreenColorSmoothing = given }.ScreenColorSmoothing);
        }
    }

    /// <summary>A macro's lighting cycle offers the mode by its name. These read
    /// localized text, so they run alone with the tests that set the
    /// culture.</summary>
    [Collection("CultureSwitching")]
    public class ScreenColorMacroTests
    {
        [Fact]
        public void AMacroCycleOffersTheMode_ByItsName()
        {
            var option = new MacroAction().CycleModeOptions.Single(o => o.Mode == LightbarMode.ScreenColor);
            Assert.Equal(Strings.Instance.Pad_Lighting_Mode_ScreenColor, option.Label);
            Assert.Equal(Strings.Instance.Pad_Lighting_Mode_ScreenColor, MacroAction.LightbarModeDisplayName(LightbarMode.ScreenColor));
        }
    }

    /// <summary>The Lighting tab's Display box. These tests set the process's
    /// culture, so they run alone with the others that do.</summary>
    [Collection("CultureSwitching")]
    public sealed class ScreenDisplayListTests : IDisposable
    {
        public ScreenDisplayListTests()
        {
            ScreenDisplayChoice.DisplaysSeam = () => new[]
            {
                (@"\\.\DISPLAY10", 800, 600),
                (@"\\.\DISPLAY2", 1920, 1080),
                (@"\\.\DISPLAY1", 2560, 1600),
            };
        }

        public void Dispose() => ScreenDisplayChoice.DisplaysSeam = null;

        private static string[] Ids(IEnumerable<ScreenDisplayChoice> list) => list.Select(c => c.Id).ToArray();

        [Fact]
        public void ThePrimaryComesFirst_ThenEachDisplayByItsNumber()
        {
            var s = Strings.Instance;
            var list = ScreenDisplayChoice.ListFor(string.Empty);
            Assert.Equal(new[] { string.Empty, @"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY10" }, Ids(list));
            Assert.Equal(s.Pad_Lighting_ScreenDisplayPrimary, list[0].Label);
            Assert.Equal(string.Format(s.Pad_Lighting_ScreenDisplayItem, "2", 1920, 1080), list[2].Label);
        }

        [Fact]
        public void ASavedDisplayThatIsGone_StaysInTheList_SaidToBeGone()
        {
            var list = ScreenDisplayChoice.ListFor(@"\\.\DISPLAY7");
            Assert.Equal(@"\\.\DISPLAY7", list[^1].Id);
            Assert.Equal(string.Format(Strings.Instance.Pad_Lighting_ScreenDisplayGone, "7"), list[^1].Label);
        }

        [Fact]
        public void ASavedSpellingThatDiffersOnlyInCase_IsThatDisplay()
        {
            Assert.Equal(new[] { string.Empty, @"\\.\DISPLAY1", @"\\.\display2", @"\\.\DISPLAY10" }, Ids(ScreenDisplayChoice.ListFor(@"\\.\display2")));
        }

        [Fact]
        public void TheListFollowsALanguageChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                string english = ScreenDisplayChoice.ListFor(string.Empty)[0].Label;
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                string german = ScreenDisplayChoice.ListFor(string.Empty)[0].Label;
                Assert.Equal(Strings.Instance.Pad_Lighting_ScreenDisplayPrimary, german);
                Assert.NotEqual(english, german);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData(@"\\.\DISPLAY2", 2)]
        [InlineData(@"\\.\DISPLAY7", 4)]
        public void TheBoxShowsTheSavedDisplay(string saved, int index)
        {
            RunSta(() =>
            {
                var box = new ComboBox();
                Assert.True(PadPage.ShowScreenDisplays(box, saved));
                Assert.Equal(index, box.SelectedIndex);
                Assert.Equal(saved, ((ScreenDisplayChoice)box.SelectedItem).Id);
            });
        }

        [Fact]
        public void ABoxThatAlreadySaysItAll_IsLeftAlone()
        {
            RunSta(() =>
            {
                var box = new ComboBox();
                PadPage.ShowScreenDisplays(box, @"\\.\DISPLAY2");
                var items = box.ItemsSource;
                Assert.False(PadPage.ShowScreenDisplays(box, @"\\.\DISPLAY2"));
                Assert.Same(items, box.ItemsSource);
            });
        }

        /// <summary>The line of the chosen display changes its words, as it does
        /// when that display changes resolution or the language changes. The new
        /// list has no line equal to the old one, and the box stays on it.</summary>
        [Fact]
        public void ANewListKeepsTheChosenDisplay_WhenItsLineChangesItsWords()
        {
            RunSta(() =>
            {
                var box = new ComboBox();
                PadPage.ShowScreenDisplays(box, @"\\.\DISPLAY2");
                ScreenDisplayChoice.DisplaysSeam = () => new[]
                {
                    (@"\\.\DISPLAY1", 2560, 1600),
                    (@"\\.\DISPLAY2", 3840, 2160),
                };
                Assert.True(PadPage.ShowScreenDisplays(box, @"\\.\DISPLAY2"));
                var line = (ScreenDisplayChoice)box.SelectedItem;
                Assert.Equal(@"\\.\DISPLAY2", line.Id);
                Assert.Contains("3840", line.Label);
            });
        }

        /// <summary>Picking another line while a gone display is saved drops the
        /// gone line, which swaps the list inside the box's own SelectionChanged,
        /// as the Lighting tab does.</summary>
        [Fact]
        public void APickAwayFromAGoneDisplay_DropsItsLine_AndStaysOnThePick()
        {
            RunSta(() =>
            {
                var box = new ComboBox();
                string saved = @"\\.\DISPLAY7";
                bool syncing = true;
                PadPage.ShowScreenDisplays(box, saved);
                syncing = false;
                box.SelectionChanged += (_, _) =>
                {
                    if (syncing || box.SelectedItem is not ScreenDisplayChoice picked) return;
                    saved = picked.Id;
                    syncing = true;
                    PadPage.ShowScreenDisplays(box, saved);
                    syncing = false;
                };
                box.SelectedIndex = 1;
                Assert.Equal(@"\\.\DISPLAY1", saved);
                Assert.Equal(@"\\.\DISPLAY1", ((ScreenDisplayChoice)box.SelectedItem).Id);
                Assert.DoesNotContain(@"\\.\DISPLAY7", Ids((IEnumerable<ScreenDisplayChoice>)box.ItemsSource));
            });
        }

        [Fact]
        public void AChangeOfSavedDisplay_MovesTheSelection()
        {
            RunSta(() =>
            {
                var box = new ComboBox();
                PadPage.ShowScreenDisplays(box, @"\\.\DISPLAY2");
                Assert.True(PadPage.ShowScreenDisplays(box, string.Empty));
                Assert.Equal(0, box.SelectedIndex);
            });
        }
    }
}
