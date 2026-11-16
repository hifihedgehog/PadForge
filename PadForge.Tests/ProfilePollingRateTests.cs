using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Per-profile polling rate override (#365, asked in discussion #362).
    ///
    /// <para>The loop stays global. The profile retunes it while active
    /// through ONE resolver, with 0 as the documented "no opinion, follow
    /// the global setting" sentinel that every pre-#365 profile
    /// deserializes to.</para>
    /// </summary>
    public class ProfilePollingRateTests
    {
        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        [Theory]
        [InlineData(0, 1000, 1000)]      // sentinel: global wins
        [InlineData(0, 60, 60)]
        [InlineData(250, 1000, 250)]     // override wins over a faster global
        [InlineData(4000, 60, 4000)]     // and over a slower one
        [InlineData(99999, 1000, 10000)] // out-of-range override clamps to the loop's range
        [InlineData(0, 0, 60)]           // degenerate global clamps up
        [InlineData(-3, 125, 125)]       // negative is no opinion, not a clamp target
        public void TheResolver_OverrideWinsWhenItHasAnOpinion(int overrideHz, int globalHz, int expected)
        {
            Assert.Equal(expected, PollingRates.Resolve(overrideHz, globalHz));
        }

        /// <summary>The sentinel contract on the wire: a profile saved
        /// before the field existed deserializes to 0, and an authored
        /// override round-trips.</summary>
        [Fact]
        public void ProfileData_RoundTripsTheOverride_AndOldXmlReadsAsSentinel()
        {
            var ser = new XmlSerializer(typeof(ProfileData));

            var p = new ProfileData { Name = "VN", PollingRateOverrideHz = 125 };
            using var w = new StringWriter();
            ser.Serialize(w, p);
            using var r = new StringReader(w.ToString());
            var back = (ProfileData)ser.Deserialize(r);
            Assert.Equal(125, back.PollingRateOverrideHz);
            // The interval a profile held before PadForge 6 is never written.
            Assert.DoesNotContain("PollingRateOverrideMs", w.ToString());

            // A pre-#365 profile: no element at all.
            const string oldXml = "<ProfileData Id=\"abc\"><Name>Old</Name></ProfileData>";
            using var r2 = new StringReader(oldXml);
            var old = (ProfileData)ser.Deserialize(r2);
            Assert.Equal(0, old.PollingRateOverrideHz);
        }

        /// <summary>A profile saved before PadForge 6 holds its override as
        /// an interval in milliseconds. The load turns it into the rate, and
        /// the next save writes the rate alone.</summary>
        [Theory]
        [InlineData(1, 1000)]
        [InlineData(2, 500)]
        [InlineData(4, 250)]
        [InlineData(8, 125)]
        [InlineData(16, 63)]
        public void AProfilesOldIntervalBecomesItsRate(int intervalMs, int expectedHz)
        {
            var ser = new XmlSerializer(typeof(ProfileData));
            string oldXml = "<ProfileData Id=\"abc\"><Name>Old</Name><PollingRateOverrideMs>" + intervalMs + "</PollingRateOverrideMs></ProfileData>";
            using var r = new StringReader(oldXml);
            var old = (ProfileData)ser.Deserialize(r);
            old.MigrateLegacySchema();
            Assert.Equal(expectedHz, old.PollingRateOverrideHz);

            using var w = new StringWriter();
            ser.Serialize(w, old);
            Assert.Contains("<PollingRateOverrideHz>" + expectedHz + "</PollingRateOverrideHz>", w.ToString());
            Assert.DoesNotContain("PollingRateOverrideMs", w.ToString());
        }

        /// <summary>The same for the global setting: a settings file saved
        /// before PadForge 6 holds an interval, a file saved since holds the
        /// rate, and a file that holds neither reads as none, which the
        /// load takes as the default.</summary>
        [Theory]
        [InlineData("<PollingRateMs>1</PollingRateMs>", 1000)]
        [InlineData("<PollingRateMs>3</PollingRateMs>", 333)]
        [InlineData("<PollingRateMs>16</PollingRateMs>", 63)]
        [InlineData("<PollingRateHz>4000</PollingRateHz>", 4000)]
        [InlineData("", 0)]
        public void TheGlobalSettingsOldIntervalBecomesItsRate(string element, int expectedHz)
        {
            var ser = new XmlSerializer(typeof(AppSettingsData));
            using var r = new StringReader("<AppSettingsData>" + element + "</AppSettingsData>");
            var settings = (AppSettingsData)ser.Deserialize(r);
            settings.MigrateLegacySchema();
            Assert.Equal(expectedHz, settings.PollingRateHz);

            using var w = new StringWriter();
            ser.Serialize(w, settings);
            Assert.DoesNotContain("PollingRateMs", w.ToString());
        }

        /// <summary>Every profile-switch lane funnels through
        /// ResetRuntimeStateForProfileSwitch, so the resolver rides it, and
        /// the two direct-write sites (engine start, the Settings box)
        /// go through the resolver rather than assigning the global value
        /// raw. Source contract, since the service needs a live engine to
        /// construct.</summary>
        [Fact]
        public void TheResolverOwnsEveryRateWrite()
        {
            string src = RepoText("PadForge.App", "Services", "InputService.cs");

            int reset = src.IndexOf("private void ResetRuntimeStateForProfileSwitch()", StringComparison.Ordinal);
            Assert.True(reset > 0);
            Assert.Contains("ApplyEffectivePollingRate();", src.Substring(reset, 600));

            // The engine-start and settings-changed writes both resolve.
            // The ONLY raw assignment to PollingRateHz left in the file
            // is the resolver's own.
            int direct = src.Split(new[] { "_inputManager.PollingRateHz =" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(1, direct);
        }

        /// <summary>The override is AUTHORED: SaveActiveProfileState's
        /// field-copy block must not assign it, or every profile switch
        /// would clobber the user's choice with the snapshot's default.</summary>
        [Fact]
        public void StateSavesNeverClobberTheAuthoredOverride()
        {
            string src = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = src.IndexOf("public void SaveActiveProfileState()", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = src.IndexOf("public ", at + 40, StringComparison.Ordinal);
            string body = src.Substring(at, end - at);
            Assert.DoesNotContain("PollingRateOverride", body);
        }

        /// <summary>The Settings page says who is in charge of the knob:
        /// the resolver writes the override note beside the rate itself
        /// (set while an override rules, cleared when the global value
        /// does), and the page binds it under the knob. A global setting a
        /// profile silently outranks reads as broken, which is the owner's
        /// exact complaint this follow-up answers.</summary>
        [Fact]
        public void TheSettingsPageNamesTheOverridingProfile()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("internal void ApplyEffectivePollingRate()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = svc.Substring(at, 1800);
            Assert.Contains("PollingOverrideNote = overrideHz > 0 && activeName != null", body);
            Assert.Contains("Settings_PollingOverriddenBy_Format", body);
            Assert.Contains(": null", body);   // cleared when the global value rules

            string page = RepoText("PadForge.App", "Views", "SettingsPage.xaml");
            Assert.Contains("Binding PollingOverrideNote", page);
            Assert.Contains("Converter={StaticResource StringToVisibility}", page);
            Assert.Contains("Settings_PollingRateTooltip", page);
        }

        /// <summary>Editing the ACTIVE profile's override retunes the live
        /// loop immediately rather than at the next switch.</summary>
        [Fact]
        public void EditingTheActiveProfileReappliesTheRate()
        {
            string src = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = src.IndexOf("public ProfileData EditProfile(", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, 900);
            Assert.Contains("PollingRateOverrideHz = pollingRateOverrideHz", body);
            Assert.Contains("ApplyEffectivePollingRate()", body);
        }

        /// <summary>The dialog always renders the polling picker, so every
        /// flow that shows it must thread the choice: Save As, Edit, AND New.
        /// The New flow shipped dropping it (audit C3).</summary>
        [Fact]
        public void AllThreeDialogFlowsThreadTheOverride()
        {
            string mw = RepoText("PadForge.App", "MainWindow.xaml.cs");
            int count = mw.Split(new[] { "dialog.PollingOverrideHz" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(3, count);

            // All three creators carry the parameter: snapshot, edit, empty.
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int creators = svc.Split(new[] { "int pollingRateOverrideHz = 0)" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(3, creators);
        }
    }
}
