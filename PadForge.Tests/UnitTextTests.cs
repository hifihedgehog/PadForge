using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PadForge.Tests
{
    /// <summary>A unit after a value box reads as part of its row's phrase
    /// ("Notify at or Below [15] %"), so it takes the label's color, never
    /// the dimmer tertiary one (owner, 2026-10-30).</summary>
    public class UnitTextTests
    {
        private static readonly string[] UnitTexts = { "%", "×", "°/s", "ms" };

        private static readonly string[] UnitKeys =
        {
            "Common_Ms", "Common_Pixels", "Devices_IdleDisconnectMinutes", "Settings_SecondsZeroNever",
            "Pad_Falcon_NewtonsUnit", "GyroSensitivityH_DegPerTurn", "GyroSensitivityV_DegPerTurn",
        };

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void UnitsAfterValues_TakeTheLabelsColor()
        {
            string root = RepoRoot();
            var offenders = new List<string>();
            foreach (string path in Directory.GetFiles(Path.Combine(root, "PadForge.App"), "*.xaml", SearchOption.AllDirectories))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(path), @"<TextBlock\b[^>]*?/>"))
                {
                    if (!m.Value.Contains("TextFillColorTertiaryBrush", StringComparison.Ordinal)) continue;
                    var text = Regex.Match(m.Value, "\\bText=\"([^\"]*)\"");
                    if (!text.Success) continue;
                    string v = text.Groups[1].Value;
                    if (UnitTexts.Contains(v) || UnitKeys.Any(k => v.StartsWith("{Binding " + k, StringComparison.Ordinal)))
                        offenders.Add(Path.GetFileName(path) + ": " + v);
                }
            }
            Assert.Empty(offenders);

            // The unit style holds the color the label style holds,
            // whichever that is. Both are the app's, which every page's
            // rows share.
            string app = File.ReadAllText(Path.Combine(root, "PadForge.App", "App.xaml"));
            string Foreground(string xaml, string key)
            {
                string style = Regex.Match(xaml, "<Style x:Key=\"" + key + "\"[\\s\\S]*?</Style>").Value;
                return Regex.Match(style, "<Setter Property=\"Foreground\" Value=\"([^\"]*)\"").Groups[1].Value;
            }
            Assert.StartsWith("{DynamicResource TextFillColor", Foreground(app, "DzLabel"));
            Assert.Equal(Foreground(app, "DzLabel"), Foreground(app, "DzPercent"));
        }
    }
}
