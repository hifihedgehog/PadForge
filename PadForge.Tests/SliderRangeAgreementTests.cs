using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A slider's range and the range its model accepts have to agree.
    ///
    /// <para>A slider coerces only its own displayed value. WPF keeps the
    /// coerced number on the slider and never writes it to the bound
    /// property (RangeBase's coercion is stored with SetCoercedValue, and a
    /// TwoWay source is written only through SetValue or SetCurrentValue),
    /// so a value outside the slider's range stays in the model. The slider
    /// pegs at its end beside a text box that shows the real number, the
    /// first touch of the slider replaces it with one inside the slider's
    /// range, and the slider can never set the rest of the model's
    /// range.</para>
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

        /// <summary>Every slider bound to <paramref name="boundProperty"/> on
        /// the Pad page, with its Minimum and Maximum.</summary>
        private static System.Collections.Generic.List<(double Min, double Max)> Sliders(string boundProperty)
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Views", "PadPage.xaml"));
            var found = new System.Collections.Generic.List<(double, double)>();
            foreach (Match m in Regex.Matches(xaml, @"<Slider\b[^>]*>", RegexOptions.Singleline))
            {
                if (!Regex.IsMatch(m.Value, @"Value=""\{Binding " + Regex.Escape(boundProperty) + @"[,}]")) continue;
                string tag = m.Value;
                double Read(string attr) => double.Parse(
                    Regex.Match(tag, attr + "=\"(?<v>[0-9.]+)\"").Groups["v"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                found.Add((Read("Minimum"), Read("Maximum")));
            }
            Assert.NotEmpty(found);
            return found;
        }

        /// <summary>The model's floor and ceiling: what it keeps when handed a
        /// very small and a very large value.</summary>
        private static (double Min, double Max) ModelRange(Action<double> set, Func<double> get)
        {
            set(-1e6);
            double min = get();
            set(1e6);
            return (min, get());
        }

        /// <summary>The Motion Pitch, Yaw and Roll rows' speeds (#475), the
        /// stick trim rate (#155) and the Ramped attack and release times
        /// (#111), at every site that binds them: each slider covers the
        /// range its model accepts. Top Speed stopped at 10 where the model
        /// takes 1, Start Speed at 200 where it takes 1,600, the trim rate at
        /// 10 to 400 against 1 to 1,000, and the ramp times at 2 against
        /// 5.</summary>
        [Theory]
        [InlineData("MotionSpeed")]
        [InlineData("MotionMinSpeed")]
        [InlineData("TrimRate")]
        [InlineData("ParamAttackTime")]
        [InlineData("ParamReleaseTime")]
        public void EveryRowSliderCoversItsModelsRange(string property)
        {
            var row = new MappingItem("Motion Pitch", PadForge.Engine.Data.MappingSetMigrator.MotionPitchTarget,
                MappingCategory.Motion, null, includeInMapAll: false);
            var source = new MappingSourceItem();
            var range = property switch
            {
                "MotionSpeed" => ModelRange(v => row.MotionSpeed = (int)v, () => row.MotionSpeed),
                "MotionMinSpeed" => ModelRange(v => row.MotionMinSpeed = (int)v, () => row.MotionMinSpeed),
                "TrimRate" => ModelRange(v => row.TrimRate = (int)v, () => row.TrimRate),
                "ParamAttackTime" => ModelRange(v => source.ParamAttackTime = v, () => source.ParamAttackTime),
                _ => ModelRange(v => source.ParamReleaseTime = v, () => source.ParamReleaseTime),
            };
            Assert.All(Sliders(property), s => Assert.Equal(range, s));
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
