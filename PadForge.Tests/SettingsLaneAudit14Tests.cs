using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Settings, Extras and credits fixes from the audit of
    /// bf5dc2135..aa64c9d64.</summary>
    [Collection("SettingsManagerStatics")]
    public class SettingsLaneAudit14Tests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Text(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static string English(string key)
        {
            var m = Regex.Match(Text("PadForge.App", "Resources", "Strings", "Strings.resx"),
                "<data name=\"" + Regex.Escape(key) + "\" xml:space=\"preserve\"><value>(.*?)</value>", RegexOptions.Singleline);
            Assert.True(m.Success, key + " is not in Strings.resx");
            return m.Groups[1].Value;
        }

        private static bool HasEnglish(string key) =>
            Regex.IsMatch(Text("PadForge.App", "Resources", "Strings", "Strings.resx"),
                "<data name=\"" + Regex.Escape(key) + "\" ");

        // X6-1 ---------------------------------------------------------

        /// <summary>ExtraRowNote starts at the page's label column plus its 12 px
        /// gap. The Head Tracking page widens its labels to 300, so its note
        /// starts at 312.</summary>
        [Fact]
        public void HeadTrackingNote_StartsAtItsOwnLabelColumn()
        {
            string page = Text("PadForge.App", "Views", "ExtrasPage.xaml");
            int from = page.IndexOf("<StackPanel x:Name=\"HeadTrackingPage\"", StringComparison.Ordinal);
            Assert.True(from > 0);
            int res = page.IndexOf("</StackPanel.Resources>", from, StringComparison.Ordinal);
            string resources = page.Substring(from, res - from);
            var width = Regex.Match(resources, "x:Key=\"DzLabel\".*?Property=\"Width\" Value=\"(\\d+)\"", RegexOptions.Singleline);
            Assert.True(width.Success);
            int label = int.Parse(width.Groups[1].Value);
            var note = Regex.Match(resources, "x:Key=\"ExtraRowNote\".*?Property=\"Margin\" Value=\"(\\d+),", RegexOptions.Singleline);
            Assert.True(note.Success, "Head Tracking shadows DzLabel but not ExtraRowNote");
            Assert.Equal(label + 12, int.Parse(note.Groups[1].Value));
        }

        // X6-3 ---------------------------------------------------------

        private static MethodInfo WakeMethod() =>
            typeof(InputManager).GetMethod("WakesPerSecond", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>The tooltip's figure is the wakes a second the timer grid
        /// gives: 2000 on a 0.5 ms grid, 1000 on a 1 ms grid.</summary>
        [Theory]
        [InlineData(2000)]
        [InlineData(1000)]
        [InlineData(3200)]
        public void WakesPerSecond_IsTheGridsRate(int wakes)
        {
            var method = WakeMethod();
            Assert.NotNull(method);
            Assert.Equal(wakes, (int)method.Invoke(null, new object[] { Stopwatch.Frequency / wakes }));
        }

        [Fact]
        public void PollingRateTooltip_TakesThisPcsWakeRate()
        {
            string page = Text("PadForge.App", "Views", "SettingsPage.xaml");
            Assert.Contains("Settings_PollingRateTooltip_Format", page);
            Assert.Contains("PollingWakesPerSecond", page);
            Assert.DoesNotContain("Binding Settings_PollingRateTooltip,", page);
            string format = English("Settings_PollingRateTooltip_Format");
            Assert.Contains("{0}", format);
            Assert.DoesNotContain("2000", format);
            Assert.False(HasEnglish("Settings_PollingRateTooltip"));
            Assert.NotNull(typeof(SettingsViewModel).GetProperty("PollingWakesPerSecond"));
        }

        // X6-6 and S1-1 -------------------------------------------------

        [Fact]
        public void ZeroUnits_ShowTheirMeaningOnTheRow()
        {
            Assert.Equal("seconds (0 = never)", English("Settings_SecondsZeroNever"));
            Assert.Equal("minutes (0 = never)", English("Common_MinutesZeroNever"));
            Assert.False(HasEnglish("Devices_IdleDisconnectMinutes"));

            string settings = Text("PadForge.App", "Views", "SettingsPage.xaml");
            int timeout = settings.IndexOf("HmInactivityDestroyTimeoutSeconds, Mode=TwoWay", StringComparison.Ordinal);
            Assert.True(timeout > 0);
            Assert.Contains("Settings_SecondsZeroNever", settings.Substring(timeout, 900));
            int repeat = settings.IndexOf("BatteryNotifyRepeatMinutes, Mode=TwoWay", StringComparison.Ordinal);
            Assert.True(repeat > 0);
            Assert.Contains("Common_MinutesZeroNever", settings.Substring(repeat, 900));
            Assert.Contains("Common_MinutesZeroNever", Text("PadForge.App", "Views", "DevicesPage.xaml"));
        }

        // X6-7 ---------------------------------------------------------

        /// <summary>openflydigi is licensed per file: MIT code, CC0-1.0
        /// PROTOCOL.md, GPL-3.0-or-later gui/. LICENSE and README say so.</summary>
        [Fact]
        public void OpenFlydigi_IsCreditedPerFile()
        {
            string license = Text("LICENSE");
            int at = license.IndexOf("openflydigi (https://github.com/mkaliaha/openflydigi)", StringComparison.Ordinal);
            Assert.True(at > 0);
            string entry = license.Substring(at, license.IndexOf("flydigi-space-station-mac", at, StringComparison.Ordinal) - at);
            Assert.Contains("CC0-1.0", entry);
            Assert.Contains("MIT", entry);
            string readme = Text("README.md");
            foreach (string line in readme.Split('\n').Where(l => l.Contains("mkaliaha/openflydigi", StringComparison.Ordinal)))
                Assert.Contains("CC0-1.0", line);
        }

        // S1-3 ---------------------------------------------------------

        [Fact]
        public void SdlNotFoundLine_SendsTheUserToDownloadPadForgeAgain()
        {
            Assert.False(HasEnglish("Status_SDL3NotFoundDetail"));
            string line = English("Status_SDL3NotFoundDownloadAgain");
            Assert.Contains("Download PadForge again", line);
            Assert.DoesNotContain("next to", line);
            Assert.DoesNotContain("libsdl-org", line);
            Assert.Contains("Status_SDL3NotFoundDownloadAgain", Text("PadForge.App", "MainWindow.xaml.cs"));
        }

        // S1-6 ---------------------------------------------------------

        /// <summary>Every gray hint of the Extras family is a sentence and ends
        /// with a period.</summary>
        [Fact]
        public void ExtrasHints_AllEndWithAPeriod()
        {
            var keys = Regex.Matches(Text("PadForge.App", "Resources", "Strings", "Strings.resx"),
                "<data name=\"(Extras_[A-Za-z_]*Hint)\" xml:space=\"preserve\"><value>(.*?)</value>", RegexOptions.Singleline);
            Assert.True(keys.Count >= 11);
            var bare = keys.Cast<Match>().Where(m => !m.Groups[2].Value.EndsWith(".", StringComparison.Ordinal))
                .Select(m => m.Groups[1].Value).ToList();
            Assert.Empty(bare);
        }

        // S1-O3 --------------------------------------------------------

        [Fact]
        public void ToggleActionNames_AreCarriedOnce()
        {
            foreach (string name in new[] { "TouchpadOverlay", "ScreenOverlay", "Crosshair", "Magnifier" })
            {
                Assert.False(HasEnglish("Macro_Toggle" + name), "Macro_Toggle" + name);
                Assert.True(HasEnglish("MacroAction_Toggle" + name));
            }
            string catalog = Text("PadForge.App", "ViewModels", "MacroTypeCatalog.cs");
            Assert.DoesNotContain("S.Macro_Toggle", catalog);
            Assert.Contains("ToggleTouchpadOverlay, S.MacroAction_ToggleTouchpadOverlay", catalog);
        }

        // X7-O1 --------------------------------------------------------

        [Fact]
        public void ChatCommands_UseTheBodyFace()
        {
            string css = Text("PadForge.App", "WebAssets", "css", "padforge.css");
            string display = Text("PadForge.App", "WebAssets", "display.html");
            Assert.DoesNotContain("monospace", css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--mono", display, StringComparison.OrdinalIgnoreCase);
        }

        // T1-3 ---------------------------------------------------------

        [Fact]
        public void ReadmeProfilePollingRate_NamesTheRealRange()
        {
            // A released "New in" block keeps the words of its release.
            string readme = Regex.Replace(Text("README.md"), @"<details>\s*<summary><b>New in [0-9.]+:</b>.*?</details>",
                string.Empty, RegexOptions.Singleline);
            Assert.DoesNotContain("62.5 Hz", readme);
            Assert.Contains("from 60 Hz to 10000 Hz", readme);
        }
    }

    [Collection("CultureSwitching")]
    public class SettingsLaneAudit14CultureTests
    {
        /// <summary>C20-3: the SDL row keeps the failure as a state and words it
        /// when read, so a language change reaches it.</summary>
        [Fact]
        public void SdlRow_FollowsALanguageChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                var vm = new SettingsViewModel();
                var state = typeof(SettingsViewModel).GetProperty("SdlVersionState");
                Assert.NotNull(state);
                var notFound = Enum.Parse(state.PropertyType, "NotFound");
                state.SetValue(vm, notFound);
                Assert.Equal(Strings.Instance.Status_SDL3NotFound, vm.SdlVersion);
                string english = vm.SdlVersion;
                var raised = new List<string>();
                vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));

                Assert.Equal(Strings.Instance.Status_SDL3NotFound, vm.SdlVersion);
                Assert.Contains(nameof(SettingsViewModel.SdlVersion), raised);

                state.SetValue(vm, Enum.Parse(state.PropertyType, "Unknown"));
                Assert.Equal(Strings.Instance.Common_Unknown, vm.SdlVersion);
                vm.SdlVersion = "3.5.0";
                Assert.Equal("3.5.0", vm.SdlVersion);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }
}
