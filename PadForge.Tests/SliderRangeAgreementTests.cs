using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A slider's ceiling and the value its model accepts have to agree.
    ///
    /// <para>A slider coerces its value into its own range, and these bind two
    /// way with the default update trigger, so a stored or imported value
    /// above the slider's ceiling was clamped the moment the card realized and
    /// the clamped number was written back to the model. The text box beside
    /// each slider accepts the model's full range, so a user could type a
    /// value, see it accepted, and lose it on the next visit to the card.</para>
    /// </summary>
    public class SliderRangeAgreementTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static double SliderMaximum(string boundProperty)
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Views", "PadPage.xaml"));
            var m = Regex.Match(xaml,
                @"<Slider\s+Value=""\{Binding " + Regex.Escape(boundProperty)
                + @", Mode=TwoWay(, UpdateSourceTrigger=PropertyChanged)?\}""[^>]*?Maximum=""(?<max>[0-9.]+)""",
                RegexOptions.Singleline);
            Assert.True(m.Success, $"no slider found for {boundProperty}");
            return double.Parse(m.Groups["max"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>What the model keeps when handed a very large value.</summary>
        private static double ModelCeiling(Action<PadViewModel, double> set, Func<PadViewModel, double> get)
        {
            var vm = new PadViewModel(0);
            set(vm, 1e6);
            return get(vm);
        }

        /// <summary>The tilt range agrees on all three surfaces now: the
        /// reading can express 90 degrees, the slider stops there, and the
        /// model no longer lets a hand-edited profile past it.</summary>
        [Fact]
        public void TheTiltRangeSliderAndModelAgree()
        {
            double ceiling = ModelCeiling((vm, v) => vm.GyroTiltRangeDeg = v, vm => vm.GyroTiltRangeDeg);
            Assert.Equal(90, ceiling);
            Assert.Equal(ceiling, SliderMaximum("GyroTiltRangeDeg"));
        }

        /// <summary>The tilt inner deadzone sits just under the range's cap,
        /// and its slider reaches the same place.</summary>
        [Fact]
        public void TheTiltInnerDeadzoneSliderAndModelAgree()
        {
            double ceiling = ModelCeiling((vm, v) => vm.GyroTiltInnerDz = v, vm => vm.GyroTiltInnerDz);
            Assert.Equal(89, ceiling);
            Assert.Equal(ceiling, SliderMaximum("GyroTiltInnerDz"));
        }

        /// <summary>The flick time's slider reaches the value the model
        /// accepts, so a stored two-second flick survives the card.</summary>
        [Fact]
        public void TheFlickTimeSliderAndModelAgree()
        {
            double ceiling = ModelCeiling((vm, v) => vm.FlickTime = v, vm => vm.FlickTime);
            Assert.Equal(2.0, ceiling);
            Assert.Equal(ceiling, SliderMaximum("FlickTime"));
        }

        /// <summary>The pitch and roll simulation's smoothing slider stops
        /// where the model does (#474), so a stored 250 ms survives the card.</summary>
        [Fact]
        public void TheSimulationSmoothingSliderAndModelAgree()
        {
            double ceiling = ModelCeiling((vm, v) => vm.GyroSimulationSmoothingMs = v, vm => vm.GyroSimulationSmoothingMs);
            Assert.Equal(250, ceiling);
            Assert.Equal(ceiling, SliderMaximum("GyroSimulationSmoothingMs"));
        }

        /// <summary>The model really does clamp, so the comparisons above are
        /// against a ceiling and not against an unbounded property.</summary>
        [Fact]
        public void TheModelsReallyClamp()
        {
            var vm = new PadViewModel(0);
            vm.GyroTiltRangeDeg = 1e6;
            Assert.True(vm.GyroTiltRangeDeg < 1e6);
            vm.FlickTime = 1e6;
            Assert.True(vm.FlickTime < 1e6);
        }
    }
}
