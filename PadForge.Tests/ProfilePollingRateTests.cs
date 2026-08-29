using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
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
        [InlineData(0, 1, 1)]     // sentinel: global wins
        [InlineData(0, 16, 16)]
        [InlineData(4, 1, 4)]     // override wins over a faster global
        [InlineData(2, 16, 2)]    // and over a slower one
        [InlineData(99, 1, 16)]   // out-of-range override clamps to the knob's range
        [InlineData(0, 0, 1)]     // degenerate global clamps up
        [InlineData(-3, 8, 8)]    // negative is no opinion, not a clamp target
        public void TheResolver_OverrideWinsWhenItHasAnOpinion(int overrideMs, int globalMs, int expected)
        {
            Assert.Equal(expected, InputService.ResolvePollingMs(overrideMs, globalMs));
        }

        /// <summary>The sentinel contract on the wire: a profile saved
        /// before the field existed deserializes to 0, and an authored
        /// override round-trips.</summary>
        [Fact]
        public void ProfileData_RoundTripsTheOverride_AndOldXmlReadsAsSentinel()
        {
            var ser = new XmlSerializer(typeof(ProfileData));

            var p = new ProfileData { Name = "VN", PollingRateOverrideMs = 8 };
            using var w = new StringWriter();
            ser.Serialize(w, p);
            using var r = new StringReader(w.ToString());
            var back = (ProfileData)ser.Deserialize(r);
            Assert.Equal(8, back.PollingRateOverrideMs);

            // A pre-#365 profile: no element at all.
            const string oldXml = "<ProfileData Id=\"abc\"><Name>Old</Name></ProfileData>";
            using var r2 = new StringReader(oldXml);
            var old = (ProfileData)ser.Deserialize(r2);
            Assert.Equal(0, old.PollingRateOverrideMs);
        }

        /// <summary>Every profile-switch lane funnels through
        /// ResetRuntimeStateForProfileSwitch, so the resolver rides it, and
        /// the two direct-write sites (engine start, the Settings slider)
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
            // The ONLY raw assignment to PollingIntervalMs left in the file
            // is the resolver's own.
            int direct = src.Split(new[] { "_inputManager.PollingIntervalMs =" }, StringSplitOptions.None).Length - 1;
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
            Assert.DoesNotContain("PollingRateOverrideMs", body);
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
            Assert.Contains("PollingOverrideNote = ms > 0 && activeName != null", body);
            Assert.Contains("Settings_PollingOverriddenBy_Format", body);
            Assert.Contains(": null", body);   // cleared when the global value rules

            string page = RepoText("PadForge.App", "Views", "SettingsPage.xaml");
            Assert.Contains("Binding PollingOverrideNote", page);
            Assert.Contains("Converter={StaticResource StringToVisibility}", page);
            Assert.Contains("Settings_PollingIntervalTooltip", page);
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
            Assert.Contains("PollingRateOverrideMs = pollingRateOverrideMs", body);
            Assert.Contains("ApplyEffectivePollingRate()", body);
        }
    }
}
