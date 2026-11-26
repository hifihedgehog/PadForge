using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The active profile's line in a recording's settings part (#600): its
    /// name and the programs that switch to it are written, scrubbed like
    /// every value, since they show a wrong profile or a missed switch.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RecorderProfileLineTests
    {
        [Fact]
        public void TheActiveProfilesNameAndPrograms_AreWritten_Scrubbed()
        {
            var profiles = SettingsManager.Profiles;
            string active = SettingsManager.ActiveProfileId;
            var profile = new ProfileData
            {
                Name = "Sunday League",
                ExecutableNames = @"C:\Users\" + RecorderTestHost.User + @"\Games\Codemasters\wrc.exe|D:\Steam\dirt5.exe",
            };
            SettingsManager.Profiles = new List<ProfileData> { profile };
            SettingsManager.ActiveProfileId = profile.Id;
            try
            {
                using var host = new RecorderTestHost();
                var scrubber = new PrivacyScrubber(host.Environment());
                // As a recording learns them: every profile's name.
                host.LearnTypedNames(scrubber);
                var lines = new List<string>();
                host.MoreSettingsLines(scrubber, lines);

                string line = Assert.Single(lines, l => l.StartsWith("profile.settings ", StringComparison.Ordinal));
                var facts = RecordingCodec.ParseFacts(line.AsSpan("profile.settings".Length));
                Assert.Equal("<profile 1>", facts["Name"]);
                Assert.Equal(@"<folder>\wrc.exe|<folder>\dirt5.exe", facts["ExecutableNames"]);
                foreach (string value in facts.Values)
                    foreach (string word in new[] { "Sunday", "League", "Codemasters", "Steam", RecorderTestHost.User })
                        Assert.DoesNotContain(word, value, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                SettingsManager.Profiles = profiles;
                SettingsManager.ActiveProfileId = active;
            }
        }
    }
}
