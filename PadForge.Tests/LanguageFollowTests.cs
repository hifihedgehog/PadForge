using System;
using System.Globalization;
using System.Reflection;
using PadForge.Common.Input.Twitch;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Lines and names that follow a change of the interface language.
    /// </summary>
    [Collection("CultureSwitching")]
    public class LanguageFollowTests
    {
        /// <summary>The Default profile has one name. Two keys named it,
        /// Common_Default and Profile_Default, and nine languages translate
        /// them apart: in Polish the profile pill read "Domyślne" after
        /// Revert to Default and "Domyślny" after an automatic switch, in the
        /// profile list and after a restart.</summary>
        [Fact]
        public void TheDefaultProfileHasOneNameInPolish()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("pl"));
                string name = Strings.Instance.Profile_Default;
                Assert.Equal("Domyślny", name);
                var vm = new SettingsViewModel();
                Assert.Equal(name, vm.ActiveProfileInfo);
                vm.ActiveProfileInfo = "Racing";
                vm.ActiveProfileInfo = null;
                Assert.Equal(name, vm.ActiveProfileInfo);
            }
            finally { Strings.ChangeCulture(before); }
        }

        private static readonly MethodInfo TwitchPanel = typeof(InputService)
            .GetMethod("UpdateTwitchChatDevicePanel", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>The Devices page's Twitch Chat line is built once per row
        /// and status, and kept while both stay. A language change keeps the
        /// row and its status, so the line stayed in the old language until
        /// the status moved. A change of language now drops the three status
        /// lines' cache, Twitch Chat's, Game Telemetry's and the head
        /// tracker's.</summary>
        [Fact]
        public void TheDevicesPagesStatusLinesFollowALanguageChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                var devices = new DevicesViewModel { IsTwitchChatDevice = true };
                var row = new TwitchChatDevice("", 0);
                var ud = new UserDevice { Device = row };
                TwitchPanel.Invoke(null, new object[] { devices, ud });
                string english = devices.TwitchChatStatus;
                Assert.Equal(InputService.BuildTwitchChatStatus(row.Channel, row.State, TwitchChatRuntime.Paused), english);

                devices.GameTelemetryStatusVersion = 3;
                devices.GameTelemetryStatusDevice = new object();
                devices.HeadTrackerStatusVersion = 4;
                devices.HeadTrackerStatusDevice = new object();

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                TwitchPanel.Invoke(null, new object[] { devices, ud });
                string german = InputService.BuildTwitchChatStatus(row.Channel, row.State, TwitchChatRuntime.Paused);
                Assert.NotEqual(english, german);
                Assert.Equal(german, devices.TwitchChatStatus);
                Assert.Null(devices.GameTelemetryStatusDevice);
                Assert.Equal(-1, devices.GameTelemetryStatusVersion);
                Assert.Null(devices.HeadTrackerStatusDevice);
                Assert.Equal(-1, devices.HeadTrackerStatusVersion);
            }
            finally { Strings.ChangeCulture(before); }
        }

        /// <summary>The names an import gives its macros are written in the
        /// interface's language, as the layers' names are: a release twin in
        /// German reads "K (loslassen)". It read "K (release)" in every
        /// language.</summary>
        [Fact]
        public void AnImportedReleaseTwin_IsNamedInTheInterfacesLanguage()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                using var f = new Ds4WindowsImportTests.Ds4Folder("<Actions><Action Name=\"K\"><Trigger>Cross</Trigger><Type>Key</Type><Details>65</Details>"
                    + "<UnloadTrigger>Circle</UnloadTrigger><UnloadStyle>Release</UnloadStyle></Action></Actions>");
                var r = f.Read("<ProfileActions>K</ProfileActions>");
                Assert.Equal("{0} (loslassen)", Strings.Instance.Common_ReleaseMacroName_Format);
                Assert.Contains(r.Profile.Macros, x => x.Name == "K");
                Assert.Contains(r.Profile.Macros, x => x.Name == "K (loslassen)");
                Assert.DoesNotContain(r.Profile.Macros, x => x.Name.Contains("(release)", StringComparison.Ordinal));
            }
            finally { Strings.ChangeCulture(before); }
        }

        /// <summary>Search runs its query again on a change of language. The
        /// places it shows and other controllers' values were read as text
        /// when Search opened, so the view model asks for them to be read
        /// again before it runs the query.</summary>
        [Fact]
        public void SearchReadsItsPlacesAgainBeforeALanguageRerun()
        {
            var before = CultureInfo.CurrentUICulture;
            var order = new System.Collections.Generic.List<string>();
            try
            {
                var vm = new SearchViewModel(q =>
                {
                    order.Add("query");
                    return Array.Empty<PadForge.Search.SearchResult>();
                }, () => order.Add("read"));
                vm.Query = "gyro";
                order.Clear();
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                GC.KeepAlive(vm);
                Assert.Equal(new[] { "read", "query" }, order);
            }
            finally { Strings.ChangeCulture(before); }
        }
    }
}
