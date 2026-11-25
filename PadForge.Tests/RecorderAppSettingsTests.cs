using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The settings a recording writes (#600 item 32): every simple app
    /// setting from the view model that holds it, the Dashboard's included,
    /// the profile's own settings and every tuning value, and the same lists
    /// for the watch that writes a change while recording. A switch, a
    /// number or a choice is written whatever its name. Text is left out
    /// when a whole word of its name says it holds something private, but
    /// for the profile's name and programs, which are written scrubbed.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecorderAppSettingsTests
    {
        /// <summary>The simple app settings no holder keeps while PadForge
        /// runs, each with the reason it has no value to write.</summary>
        private static readonly Dictionary<string, string> HeldByNone = new()
        {
            ["LegacyPollingIntervalMs"] = "read once from an older file, never written",
            ["LegacyEnableExternalControl"] = "read once from an older file, never written",
            ["EnableChromaLightbar"] = "an older file's switch, moved to a peripheral row and never written",
            ["EnableSensaHaptics"] = "an older file's switch, moved to a peripheral row and never written",
            ["EnableLightsyncLightbar"] = "an older file's switch, moved to a peripheral row and never written",
            ["HeadTrackingIndependentInputs"] = "always written true, a mark of the file's form",
            ["ActiveProfileId"] = "the profile line names the active profile by its stand-in",
        };

        private static bool IsText(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            return t == typeof(string) || t == typeof(Guid) || t == typeof(char);
        }

        private static bool IsSimple(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid);
        }

        private static List<(string Name, string Value)> AppValuesOf(MainViewModel main)
        {
            var before = RecorderHost.Main;
            RecorderHost.Main = main;
            try { return RecorderHost.AppValues().ToList(); }
            finally { RecorderHost.Main = before; }
        }

        [Fact]
        public void EveryAppSetting_IsWritten_OrLeftOutForAReason()
        {
            var written = AppValuesOf(new MainViewModel()).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
            var missing = new List<string>();
            var leaked = new List<string>();
            var properties = typeof(AppSettingsData).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var p in properties)
            {
                if (!p.CanRead || p.GetIndexParameters().Length != 0 || !IsSimple(p.PropertyType)) continue;
                if (p.GetCustomAttribute<System.Xml.Serialization.XmlIgnoreAttribute>() != null) continue;
                bool kept = IsText(p.PropertyType) && RecorderHost.IsPrivateName(p.Name);
                if (kept || HeldByNone.ContainsKey(p.Name))
                {
                    if (written.Contains(p.Name)) leaked.Add(p.Name);
                }
                else if (!written.Contains(p.Name)) missing.Add(p.Name);
            }
            Assert.True(missing.Count == 0, "App settings not written: " + string.Join(", ", missing));
            Assert.True(leaked.Count == 0, "App settings written that stay out: " + string.Join(", ", leaked));
            foreach (string name in HeldByNone.Keys)
                Assert.Contains(properties, p => p.Name == name);
        }

        [Fact]
        public void TheNamedSettings_AreWritten_AndPrivateTextStaysOut()
        {
            var main = new MainViewModel();
            main.Settings.ProblemRecordingSaveFolder = @"C:\Users\" + RecorderTestHost.User + @"\Desktop";
            var app = AppValuesOf(main).ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

            string Text(object value) => value is bool b ? (b ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture);
            Assert.Equal(Text(main.Settings.GKeysEnabled), app["GKeysEnabled"]);
            Assert.Equal(Text(main.Settings.InstallUpdatesAutomatically), app["InstallUpdatesAutomatically"]);
            Assert.Equal(Text(main.Settings.StartAtLogin), app["StartAtLogin"]);
            Assert.Equal(Text(main.Dashboard.EnableDsuMotionServer), app["EnableDsuMotionServer"]);
            Assert.Equal(Text(main.Dashboard.DsuMotionServerPort), app["DsuMotionServerPort"]);
            Assert.Equal(Text(main.Dashboard.EnableGameModServer), app["EnableGameModServer"]);
            Assert.Equal(Text(main.Dashboard.GameModServerPort), app["GameModServerPort"]);
            Assert.Equal(Text(main.Dashboard.WebControllerPort), app["WebControllerPort"]);
            Assert.Equal(Text(main.Dashboard.RemoteLinkPort), app["RemoteLinkPort"]);
            Assert.Equal(Text(main.Dashboard.EnableProfileOverlay), app["EnableProfileOverlay"]);
            Assert.Equal(Text(main.Dashboard.EnableAutomation), app["EnableAutomation"]);
            Assert.Equal(main.Dashboard.AutomationLevel.ToString(), app["AutomationLevel"]);
            Assert.Equal(Text(main.Settings.SelectedThemeIndex), app["ThemeIndex"]);
            Assert.Equal(main.Settings.LanguageCode, app["Language"]);
            Assert.Equal(Text(main.Dashboard.AutoReconnect), app["RemoteLinkAutoReconnect"]);
            Assert.True(app.ContainsKey("VoiceMacrosEnabled"));

            // A text setting whose name holds Folder or Path stays out.
            foreach (string kept in new[] { "ProblemRecordingSaveFolder", "Strike7LibraryPath", "FalconLibraryPath" })
                Assert.False(app.ContainsKey(kept), kept);
            // The chat's channel is a personal handle and stays out. Its
            // switch is what a report needs.
            Assert.False(app.ContainsKey("TwitchChatChannel"));
            Assert.Equal(Text(main.Dashboard.EnableTwitchChat), app["EnableTwitchChat"]);

            // The profile's own settings, its name and the programs that
            // switch to it among them. A line scrubs them like every value.
            const string programs = @"C:\Games\Codemasters\WRC\wrc.exe|D:\Steam\dirt5.exe";
            var profile = new ProfileData { EnableDsuMotionServer = true, DsuMotionServerPort = 26761, EnableGameModServer = true, EnableProfileOverlay = true,
                Name = "Sunday League", ExecutableNames = programs };
            var own = RecorderHost.SimpleValues(profile).ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
            Assert.Equal("1", own["EnableDsuMotionServer"]);
            Assert.Equal("26761", own["DsuMotionServerPort"]);
            Assert.Equal("1", own["EnableGameModServer"]);
            Assert.True(own.ContainsKey("GameModServerPort"));
            Assert.Equal("1", own["EnableProfileOverlay"]);
            Assert.Equal("Sunday League", own["Name"]);
            Assert.Equal(programs, own["ExecutableNames"]);

            // Every tuning value of a controller and of a row's source.
            var pad = new PadSetting { CenteringDamping = "150", GyroEngageStickDirection = "Up" };
            var tuning = RecorderHost.SimpleValues(pad).ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
            Assert.Equal("150", tuning["CenteringDamping"]);
            Assert.Equal("Up", tuning["GyroEngageStickDirection"]);
            var sb = new StringBuilder();
            RecorderHost.AppendSourceTuning(sb, new MappingSource { Bidirectional = true }, "1", new PrivacyScrubber(new PrivacyEnvironment()));
            Assert.Contains(" Bidirectional1=1", sb.ToString());
        }

        [Fact]
        public void TheWholeWordsOfAName_DecideWhetherItsTextIsPrivate()
        {
            Assert.True(RecorderHost.IsPrivateName("ProblemRecordingSaveFolder"));
            Assert.True(RecorderHost.IsPrivateName("HeadsetTrackerAddresses"));
            Assert.True(RecorderHost.IsPrivateName("HandheldMachineKey"));
            Assert.True(RecorderHost.IsPrivateName("URLPath"));
            Assert.False(RecorderHost.IsPrivateName("CenteringDamping"));
            Assert.False(RecorderHost.IsPrivateName("GyroEngageStickDirection"));
            Assert.False(RecorderHost.IsPrivateName("KeyboardScreenPicture"));
            Assert.False(RecorderHost.IsPrivateName("EnableProfileOverlay"));
        }

        /// <summary>A path over 100 characters under a folder named for a
        /// person: cut before it is scrubbed, it could end inside that
        /// folder's name, which the scrubber would keep as the file's.</summary>
        private static string LongPath(string file)
            => @"C:\Users\" + RecorderTestHost.User + @"\Pictures\Grandma Rosa's Ninetieth Birthday Party Photos and Videos From The Lake House\" + file;

        private static void AssertNoFolderWord(string text)
        {
            foreach (string word in new[] { "Grandma", "Rosa", "Ninetieth", "Birthday", "Lake", RecorderTestHost.User })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ALongPathInASetting_IsScrubbedWhole_BeforeTheLineCutsIt()
        {
            var main = new MainViewModel();
            main.Settings.DefaultProfileDreamcastPicture = LongPath("cake.png");
            string runtime = PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest;
            PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest = LongPath("runtime.json");
            var before = RecorderHost.Main;
            RecorderHost.Main = main;
            try
            {
                using var host = new RecorderTestHost();
                var lines = new List<string>();
                host.MoreSettingsLines(new PrivacyScrubber(host.Environment()), lines);
                string app = Assert.Single(lines, l => l.StartsWith("app ", StringComparison.Ordinal));
                Assert.Contains("cake.png", app);
                Assert.Contains("runtime.json", app);
                AssertNoFolderWord(app);
            }
            finally
            {
                RecorderHost.Main = before;
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest = runtime;
            }
        }

        [Fact]
        public void ALongPathChangedWhileRecording_LeavesNoFolderWord()
        {
            var main = new MainViewModel();
            var before = RecorderHost.Main;
            RecorderHost.Main = main;
            using var host = new RecorderTestHost { RealSettings = true };
            host.DeviceList.Add(RecorderTestKit.Pad());
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            try
            {
                Assert.True(recorder.Start().Ok);
                recorder.UiTick();
                Thread.Sleep(1100);
                main.Settings.DefaultProfileKeyboardScreenPicture = LongPath("screen.png");
                recorder.UiTick();
                string path = recorder.Stop(wait: true).Path;
                var change = Assert.Single(RecorderTestKit.Events(path), e => e.Layer == 'C' && e.Payload.Contains("DefaultProfileKeyboardScreenPicture", StringComparison.Ordinal));
                Assert.Contains("screen.png", change.Payload);
                AssertNoFolderWord(change.Payload);
            }
            finally
            {
                RecorderHost.Main = before;
            }
        }

        [Fact]
        public void ADashboardSettingChangedWhileRecording_IsWritten_AndPrivateTextIsNot()
        {
            var main = new MainViewModel();
            var before = RecorderHost.Main;
            RecorderHost.Main = main;
            using var host = new RecorderTestHost { RealSettings = true };
            host.DeviceList.Add(RecorderTestKit.Pad());
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            try
            {
                Assert.True(recorder.Start().Ok);
                recorder.UiTick();
                Thread.Sleep(1100);
                Assert.NotEqual(0.75, main.Dashboard.TouchpadOverlayOpacity);
                main.Dashboard.TouchpadOverlayOpacity = 0.75;
                main.Settings.ProblemRecordingSaveFolder = @"C:\Users\" + RecorderTestHost.User + @"\Desktop";
                recorder.UiTick();
                string path = recorder.Stop(wait: true).Path;
                var changes = RecorderTestKit.Events(path).Where(e => e.Layer == 'C').Select(e => e.Payload).ToList();
                Assert.Contains("setting app TouchpadOverlayOpacity = 0.75", changes);
                Assert.DoesNotContain(changes, c => c.Contains("ProblemRecordingSaveFolder", StringComparison.Ordinal));
            }
            finally
            {
                RecorderHost.Main = before;
            }
        }

        /// <summary>A layout as the web controller's store keeps it, under a
        /// name the user typed.</summary>
        private static string Layouts(string name)
            => "[{\"id\":\"c1a2\",\"name\":\"" + name + "\",\"widgets\":[{\"kind\":\"button\",\"x\":0.1,\"y\":0.2,\"w\":0.12,\"h\":0.18,\"code\":0,\"label\":\"A\"}]}]";

        /// <summary>The web controller's layouts are a setting the user
        /// changes in the browser, so the app line writes them like any value:
        /// scrubbed whole, then cut, so a name the user typed keeps no
        /// private word.</summary>
        [Fact]
        public void TheWebControllersLayouts_AreWritten_Scrubbed()
        {
            string layouts = Layouts(RecorderTestHost.User + "'s couch pad");
            var seam = RecorderHost.WebLayoutsSeam;
            RecorderHost.WebLayoutsSeam = () => layouts;
            var before = RecorderHost.Main;
            RecorderHost.Main = new MainViewModel();
            try
            {
                var app = RecorderHost.AppValues().ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
                Assert.Equal(layouts, app["WebCustomLayoutsJson"]);

                using var host = new RecorderTestHost();
                var lines = new List<string>();
                host.MoreSettingsLines(new PrivacyScrubber(host.Environment()), lines);
                string line = Assert.Single(lines, l => l.StartsWith("app ", StringComparison.Ordinal));
                string written = PadForge.Engine.Recording.RecordingCodec.ParseFacts(line.AsSpan(3))["WebCustomLayoutsJson"];
                Assert.StartsWith("[{\"id\":\"c1a2\"", written);
                Assert.Contains("couch pad", written);
                Assert.DoesNotContain(RecorderTestHost.User, written, StringComparison.OrdinalIgnoreCase);
                Assert.True(written.Length <= 100, written);
            }
            finally
            {
                RecorderHost.Main = before;
                RecorderHost.WebLayoutsSeam = seam;
            }
        }

        /// <summary>A layout saved in the browser while recording is written
        /// as a setting that changed.</summary>
        [Fact]
        public void ALayoutSavedWhileRecording_IsWritten()
        {
            string layouts = "[]";
            var seam = RecorderHost.WebLayoutsSeam;
            RecorderHost.WebLayoutsSeam = () => layouts;
            var main = new MainViewModel();
            var before = RecorderHost.Main;
            RecorderHost.Main = main;
            using var host = new RecorderTestHost { RealSettings = true };
            host.DeviceList.Add(RecorderTestKit.Pad());
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            try
            {
                Assert.True(recorder.Start().Ok);
                recorder.UiTick();
                Thread.Sleep(1100);
                layouts = Layouts("Couch pad");
                recorder.UiTick();
                string path = recorder.Stop(wait: true).Path;
                var change = Assert.Single(RecorderTestKit.Events(path), e => e.Layer == 'C' && e.Payload.Contains("WebCustomLayoutsJson", StringComparison.Ordinal));
                Assert.StartsWith("setting app WebCustomLayoutsJson = ", change.Payload);
                Assert.Contains("Couch pad", change.Payload);
            }
            finally
            {
                RecorderHost.Main = before;
                RecorderHost.WebLayoutsSeam = seam;
            }
        }
    }
}
