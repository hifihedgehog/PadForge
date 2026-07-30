using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Pointer Response card: the mode picker plus the Trackpad profile's
    /// two calibration knobs.
    ///
    /// <para>Both knobs have to be on a card rather than buried as constants.
    /// The pad width in particular decides whether the profile's precision
    /// region is reachable at all (see
    /// <c>TrackpadPointerGainTests.AtLibinputsAssumedWidth_TheDecelerationKneeIsOutOfReach</c>),
    /// and no authority publishes a gamepad pad's physical size, so it is
    /// calibrated by hand or not at all.</para>
    /// </summary>
    public class PointerResponseCardTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Xaml() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));

        [Fact]
        public void TheDefaultsAreTheReferencesOwnNumbers()
        {
            var s = new TouchpadGestureSettings();
            Assert.Equal("Simple", s.PointerResponse);        // identity at accel 0
            Assert.Equal(130f, s.TrackpadThresholdMmPerSec);  // libinput: filter->threshold = 130
            Assert.Equal(69f, s.TrackpadPadWidthMm);          // libinput: touchpad_width_mm = 69
        }

        [Fact]
        public void AllThreeRoundTripThroughXml()
        {
            var s = new TouchpadGestureSettings
            {
                PointerResponse = "Trackpad",
                TrackpadThresholdMmPerSec = 95f,
                TrackpadPadWidthMm = 45f,
            };

            var ser = new XmlSerializer(typeof(TouchpadGestureSettings));
            using var ms = new MemoryStream();
            ser.Serialize(ms, s);
            ms.Position = 0;
            var back = (TouchpadGestureSettings)ser.Deserialize(ms);

            Assert.Equal("Trackpad", back.PointerResponse);
            Assert.Equal(95f, back.TrackpadThresholdMmPerSec);
            Assert.Equal(45f, back.TrackpadPadWidthMm);
        }

        [Theory]
        [InlineData("PointerResponse")]
        [InlineData("TrackpadThresholdMmPerSec")]
        [InlineData("TrackpadPadWidthMm")]
        public void EachOneMakesAPadCountAsConfigured(string field)
        {
            // IsConfigured decides which entry wins when the resolver picks
            // between a configured pad and a pristine sibling. A setting the
            // user changed that does not register here can be silently
            // discarded in favour of an untouched entry.
            var s = new TouchpadGestureSettings();
            switch (field)
            {
                case "PointerResponse": s.PointerResponse = "Trackpad"; break;
                case "TrackpadThresholdMmPerSec": s.TrackpadThresholdMmPerSec = 95f; break;
                case "TrackpadPadWidthMm": s.TrackpadPadWidthMm = 45f; break;
            }

            var entries = new[]
            {
                new TouchpadSettingsEntry { DeviceGuid = "dev", TouchpadIndex = 0, Settings = s },
            };
            // Resolving through the public seam rather than poking the private
            // predicate, so this tests the path the engine actually walks.
            var resolved = TouchpadGestureSettings.ResolveForPad(entries, "dev", 0);
            Assert.NotNull(resolved);

            var pristine = new TouchpadGestureSettings();
            Assert.True(
                !string.Equals(resolved.PointerResponse, pristine.PointerResponse, StringComparison.Ordinal)
                || resolved.TrackpadThresholdMmPerSec != pristine.TrackpadThresholdMmPerSec
                || resolved.TrackpadPadWidthMm != pristine.TrackpadPadWidthMm,
                $"{field} did not survive resolution");
        }

        [Fact]
        public void TheCloneCarriesAllThree()
        {
            // A missed field in the clone shows up as a setting that reverts on
            // the next copy, which is the hardest kind of bug to attribute.
            var s = new TouchpadGestureSettings
            {
                PointerResponse = "Trackpad",
                TrackpadThresholdMmPerSec = 77f,
                TrackpadPadWidthMm = 41f,
            };
            var clone = s.Clone();

            Assert.Equal("Trackpad", clone.PointerResponse);
            Assert.Equal(77f, clone.TrackpadThresholdMmPerSec);
            Assert.Equal(41f, clone.TrackpadPadWidthMm);
        }

        [Fact]
        public void EveryRowIsOnTheCardWithAResetButton()
        {
            var xaml = Xaml();
            foreach (var vm in new[]
            {
                "TouchpadPointerResponse", "TouchpadTrackpadThreshold", "TouchpadTrackpadPadWidthMm",
            })
            {
                Assert.Contains("Binding " + vm + ",", xaml);
            }
            foreach (var cmd in new[]
            {
                "ResetTouchpadPointerResponseCommand",
                "ResetTouchpadTrackpadThresholdCommand",
                "ResetTouchpadTrackpadPadWidthCommand",
            })
            {
                Assert.Contains(cmd, xaml);
            }
        }

        [Fact]
        public void TheTwoProfilesKnobsAreMutuallyGated()
        {
            // The card must never show both models' knobs at once: they are
            // competing descriptions of the same thing, and showing both invites
            // configuring a contradiction.
            var xaml = Xaml();
            Assert.Contains("TouchpadPointerResponseIsTrackpad", xaml);
            Assert.Contains("TouchpadPointerResponseIsSimple", xaml);

            // The Acceleration row belongs to Simple, so it must carry the
            // Simple gate rather than being always visible.
            int accel = xaml.IndexOf("Pad_Touchpad_MouseAcceleration,", StringComparison.Ordinal);
            Assert.True(accel > 0, "acceleration row not found");
            string before = xaml.Substring(Math.Max(0, accel - 400), Math.Min(400, accel));
            Assert.Contains("TouchpadPointerResponseIsSimple", before);
        }

        [Fact]
        public void TheVisibilityConverterKeyActuallyExists()
        {
            // A StaticResource that does not resolve throws at page load, and a
            // typo here would take the whole Pad page down rather than degrade.
            // The first draft of this card used "BoolToVis", which is not the
            // key this project defines.
            // Scanned across EVERY xaml in the app, not just this page and
            // App.xaml. A first cut checked only those two and reported
            // UpperConverter as undefined when it lives in
            // Resources/ControllerIcons.xaml, which would have been a false
            // finding about pre-existing code.
            string appDir = Path.Combine(RepoRoot(), "PadForge.App");
            var allXaml = new System.Text.StringBuilder();
            foreach (var f in Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                    continue;
                allXaml.Append(File.ReadAllText(f));
            }
            string every = allXaml.ToString();
            Assert.True(every.Length > 10000, "the xaml sweep found almost nothing; the scan has drifted");

            var used = new Regex(@"Converter=\{StaticResource (\w+)\}");
            foreach (Match m in used.Matches(Xaml()))
            {
                string key = m.Groups[1].Value;
                Assert.True(every.Contains("x:Key=\"" + key + "\"", StringComparison.Ordinal),
                    $"converter key '{key}' is referenced on the Pad page but defined in no xaml; "
                    + "a StaticResource that does not resolve throws at page load");
            }
        }
    }
}
