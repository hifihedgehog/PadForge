using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A slider that snaps stands only on Minimum plus a whole number of
    /// ticks, and its arrow keys move by SmallChange only where SmallChange
    /// is a whole number of ticks. The page's DzSlider snaps at 0.1, so the
    /// Constant Force and Constant Trigger Force sliders moved in steps of
    /// 0.1 beside boxes that show 0.01, and Flick Time, from 0.01, stood on
    /// 0.01, 0.11 and so on, never on its own default of 0.1 (seen
    /// 2026-11-20).
    /// </summary>
    public class SliderStepTests
    {
        private static readonly HashSet<string> Snapping = new(StringComparer.Ordinal)
        {
            "DzSlider", "OffsetSlider", "RowOptionDeadzoneSlider", "LineDeadzoneSlider", "DzRgbSlider",
        };

        [Theory]
        [InlineData("PadPage.xaml")]
        [InlineData("ExtrasPage.xaml")]
        [InlineData("ShiftActivatorDialog.xaml")]
        [InlineData("RegisterVoicePhraseDialog.xaml")]
        public void ASnappingSlider_ReachesItsMinimumAndItsSmallChange(string file)
        {
            string text = RepoText("PadForge.App", "Views", file);
            var off = new List<string>();
            foreach (Match m in Regex.Matches(text, @"<Slider\b[^>]*?>", RegexOptions.Singleline))
            {
                string tag = m.Value;
                string style = Regex.Match(tag, @"Style=""\{StaticResource (\w+)\}""").Groups[1].Value;
                var snap = Regex.Match(tag, @"IsSnapToTickEnabled=""(\w+)""");
                bool snaps = snap.Success ? snap.Groups[1].Value == "True" : Snapping.Contains(style);
                if (!snaps) continue;
                double tick = Number(tag, "TickFrequency") ?? (Snapping.Contains(style) ? 0.1 : 1.0);
                double minimum = Number(tag, "Minimum") ?? (style == "OffsetSlider" ? -100.0 : 0.0);
                double? small = Number(tag, "SmallChange");
                string name = Regex.Match(tag, @"Value=""\{Binding ([\w\.]+)").Groups[1].Value;
                if (!Whole(minimum, tick)) off.Add($"{name}: Minimum {minimum} is off its tick {tick}");
                if (small is double s && !Whole(s, tick)) off.Add($"{name}: SmallChange {s} is off its tick {tick}");
            }
            Assert.Empty(off);
        }

        private static double? Number(string tag, string attribute)
        {
            var m = Regex.Match(tag, attribute + @"=""([-0-9.]+)""");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        private static bool Whole(double value, double tick)
            => Math.Abs(value / tick - Math.Round(value / tick)) < 1e-6;

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
