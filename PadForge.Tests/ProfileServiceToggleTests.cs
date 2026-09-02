using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The three mirror toggles (Razer Chroma #373, Logitech LIGHTSYNC #382,
    /// Razer Sensa #374) ride profiles as NULLABLE legs. Null is "no
    /// opinion": the toggle keeps its current value, which is the global
    /// AppSettings leg or whatever the last opinionated profile set. A plain
    /// bool read as false in every pre-existing profile and the first switch
    /// turned the mirror off, which commit 087568bd answered by going
    /// global-only. The right shape is the #365 polling override's: a
    /// sentinel every old profile deserializes to, authored only by the user.
    ///
    /// <para>These drive the real SettingsService on the real MainViewModel,
    /// the pair MainWindow builds.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ProfileServiceToggleTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly ProfileData _savedPendingDefault;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;
        private readonly List<int> _savedPsOrder;
        private readonly List<int> _savedExtOrder;
        private readonly List<int> _savedKbmOrder;
        private readonly List<int> _savedMidiOrder;
        private readonly Action _savedAfterRefresh;

        public ProfileServiceToggleTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedProfiles = SettingsManager.Profiles;
            _savedActiveProfileId = SettingsManager.ActiveProfileId;
            _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedXboxOrder = SettingsManager.XboxSlotOrder;
            _savedPsOrder = SettingsManager.PlayStationSlotOrder;
            _savedExtOrder = SettingsManager.ExtendedSlotOrder;
            _savedKbmOrder = SettingsManager.KeyboardMouseSlotOrder;
            _savedMidiOrder = SettingsManager.MidiSlotOrder;
            _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPsOrder;
            SettingsManager.ExtendedSlotOrder = _savedExtOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
            SettingsManager.MidiSlotOrder = _savedMidiOrder;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>One created Xbox slot, no devices, no active profile, and
        /// the real service on the real view model.</summary>
        private static (MainViewModel vm, SettingsService ss) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            return (vm, ss);
        }

        private static ProfileData ArrangeActiveProfile(string id = "p1")
        {
            var p = new ProfileData { Id = id, Name = "Game " + id };
            SettingsManager.Profiles.Add(p);
            SettingsManager.ActiveProfileId = id;
            return p;
        }

        /// <summary>The sentinel on the wire: a profile saved before the
        /// fields existed deserializes to null on all three, and authored
        /// values round-trip, false included (false is an opinion, not the
        /// sentinel).</summary>
        [Fact]
        public void OldXmlReadsAsNull_AndAuthoredValuesRoundTrip()
        {
            var ser = new XmlSerializer(typeof(ProfileData));

            const string oldXml = "<ProfileData Id=\"abc\"><Name>Old</Name><EnableWebController>true</EnableWebController></ProfileData>";
            using (var r = new StringReader(oldXml))
            {
                var old = (ProfileData)ser.Deserialize(r);
                Assert.Null(old.EnableChromaLightbar);
                Assert.Null(old.EnableLightsyncLightbar);
                Assert.Null(old.EnableSensaHaptics);
                Assert.True(old.EnableWebController);   // the plain-bool sibling still reads
            }

            var p = new ProfileData
            {
                Name = "Authored",
                EnableChromaLightbar = true,
                EnableLightsyncLightbar = false,
                EnableSensaHaptics = null,
            };
            using var w = new StringWriter();
            ser.Serialize(w, p);
            using var r2 = new StringReader(w.ToString());
            var back = (ProfileData)ser.Deserialize(r2);
            Assert.True(back.EnableChromaLightbar);
            Assert.False(back.EnableLightsyncLightbar);
            Assert.Null(back.EnableSensaHaptics);
        }

        /// <summary>Apply: a null leg leaves the toggle where it is (both
        /// ways), true turns it on, false turns it off. Per toggle.</summary>
        [Fact]
        public void Apply_NullLeavesTheToggle_TrueTurnsOn_FalseTurnsOff()
        {
            var (vm, ss) = Arrange();
            var d = vm.Dashboard;

            // No opinion, toggles on: stay on.
            d.EnableChromaLightbar = true;
            d.EnableLightsyncLightbar = true;
            d.EnableSensaHaptics = true;
            ss.ApplyProfileServiceToggles(new ProfileData { Id = "x" });
            Assert.True(d.EnableChromaLightbar);
            Assert.True(d.EnableLightsyncLightbar);
            Assert.True(d.EnableSensaHaptics);

            // No opinion, toggles off: stay off.
            d.EnableChromaLightbar = false;
            d.EnableLightsyncLightbar = false;
            d.EnableSensaHaptics = false;
            ss.ApplyProfileServiceToggles(new ProfileData { Id = "x" });
            Assert.False(d.EnableChromaLightbar);
            Assert.False(d.EnableLightsyncLightbar);
            Assert.False(d.EnableSensaHaptics);

            // Opinions land, each leg independently.
            ss.ApplyProfileServiceToggles(new ProfileData
            {
                Id = "x", EnableChromaLightbar = true, EnableLightsyncLightbar = null, EnableSensaHaptics = true,
            });
            Assert.True(d.EnableChromaLightbar);
            Assert.False(d.EnableLightsyncLightbar);   // null leg: untouched
            Assert.True(d.EnableSensaHaptics);

            ss.ApplyProfileServiceToggles(new ProfileData
            {
                Id = "x", EnableChromaLightbar = false, EnableLightsyncLightbar = true, EnableSensaHaptics = false,
            });
            Assert.False(d.EnableChromaLightbar);
            Assert.True(d.EnableLightsyncLightbar);
            Assert.False(d.EnableSensaHaptics);
        }

        /// <summary>Author: a user change while a named profile is active
        /// becomes that profile's opinion at once, in memory, so a foreground
        /// switch inside the autosave window still carries it.</summary>
        [Fact]
        public void UserChange_AuthorsTheActiveProfile()
        {
            var (vm, _) = Arrange();
            var p1 = ArrangeActiveProfile();
            Assert.Null(p1.EnableChromaLightbar);

            vm.Dashboard.EnableChromaLightbar = true;
            Assert.True(p1.EnableChromaLightbar);
            vm.Dashboard.EnableChromaLightbar = false;
            Assert.False(p1.EnableChromaLightbar);

            vm.Dashboard.EnableLightsyncLightbar = true;
            Assert.True(p1.EnableLightsyncLightbar);

            vm.Dashboard.EnableSensaHaptics = true;
            Assert.True(p1.EnableSensaHaptics);
            vm.Dashboard.EnableSensaHaptics = false;
            Assert.False(p1.EnableSensaHaptics);
        }

        /// <summary>With no named profile active the change is the global
        /// value alone: no stored profile gains an opinion.</summary>
        [Fact]
        public void UserChange_WithNoNamedProfileActive_AuthorsNothing()
        {
            var (vm, _) = Arrange();
            var p1 = new ProfileData { Id = "p1", Name = "Idle" };
            SettingsManager.Profiles.Add(p1);
            SettingsManager.ActiveProfileId = null;

            vm.Dashboard.EnableChromaLightbar = true;
            vm.Dashboard.EnableLightsyncLightbar = true;
            vm.Dashboard.EnableSensaHaptics = true;

            Assert.Null(p1.EnableChromaLightbar);
            Assert.Null(p1.EnableLightsyncLightbar);
            Assert.Null(p1.EnableSensaHaptics);
        }

        /// <summary>The apply leg's own VM writes must not author: without
        /// the guard, applying an opinionated profile would record that
        /// value into whatever profile is active, turning a null into an
        /// opinion the user never gave. Same for the cold-path global load,
        /// pinned by source below since it needs a full AppSettings.</summary>
        [Fact]
        public void Apply_DoesNotAuthorIntoTheActiveProfile()
        {
            var (vm, ss) = Arrange();
            var p1 = ArrangeActiveProfile();
            // Positive control: the authoring hook is live for p1.
            vm.Dashboard.EnableChromaLightbar = true;
            Assert.True(p1.EnableChromaLightbar);
            p1.EnableChromaLightbar = null;   // then forget it, to expose a leak

            // Both applies change the VM (true to false, false to true), so
            // PropertyChanged fires under the guard both times.
            ss.ApplyProfileServiceToggles(new ProfileData { Id = "other", EnableChromaLightbar = false, EnableSensaHaptics = true });

            Assert.False(vm.Dashboard.EnableChromaLightbar);   // the apply landed
            Assert.True(vm.Dashboard.EnableSensaHaptics);
            Assert.Null(p1.EnableChromaLightbar);               // and authored nothing
            Assert.Null(p1.EnableSensaHaptics);

            string ss_src = RepoText("PadForge.App", "Services", "SettingsService.cs");
            int global = ss_src.IndexOf("_mainVm.Dashboard.EnableChromaLightbar = appSettings.EnableChromaLightbar;", StringComparison.Ordinal);
            Assert.True(global > 0);
            int guard = ss_src.LastIndexOf("_applyingServiceToggles = true;", global, StringComparison.Ordinal);
            Assert.True(guard > 0 && global - guard < 400, "the global load must sit inside the authoring guard");
        }

        /// <summary>Save's mirror (UpdateActiveProfileSnapshot) refreshes an
        /// existing opinion from the live toggle and never invents one: a
        /// null leg survives every autosave until the user authors it.</summary>
        [Fact]
        public void SnapshotSave_RefreshesAnOpinion_NeverInventsOne()
        {
            var (vm, ss) = Arrange();
            // Global on, set before any profile is active so nothing authors.
            vm.Dashboard.EnableChromaLightbar = true;
            vm.Dashboard.EnableLightsyncLightbar = true;
            vm.Dashboard.EnableSensaHaptics = true;
            var p1 = ArrangeActiveProfile();

            ss.UpdateActiveProfileSnapshot();
            Assert.Null(p1.EnableChromaLightbar);
            Assert.Null(p1.EnableLightsyncLightbar);
            Assert.Null(p1.EnableSensaHaptics);

            // A stale opinion is refreshed from the live value.
            p1.EnableChromaLightbar = false;
            p1.EnableSensaHaptics = false;
            ss.UpdateActiveProfileSnapshot();
            Assert.True(p1.EnableChromaLightbar);
            Assert.Null(p1.EnableLightsyncLightbar);
            Assert.True(p1.EnableSensaHaptics);
        }

        /// <summary>No runtime-state mirror invents an opinion: the two
        /// InputService lanes (SnapshotCurrentProfile feeds the default
        /// snapshot and Save As, SaveActiveProfileState runs on every
        /// switch-away) leave the three legs alone, so the default snapshot
        /// never stomps the toggles and a copied profile starts neutral.</summary>
        [Fact]
        public void RuntimeMirrorsNeverInventAnOpinion()
        {
            string src = RepoText("PadForge.App", "Services", "InputService.cs");
            foreach (var head in new[] { "public ProfileData SnapshotCurrentProfile()", "public void SaveActiveProfileState()" })
            {
                int at = src.IndexOf(head, StringComparison.Ordinal);
                Assert.True(at > 0, head);
                int end = src.IndexOf("\n        public ", at + head.Length, StringComparison.Ordinal);
                string body = src.Substring(at, end - at);
                Assert.DoesNotContain("EnableChromaLightbar", body);
                Assert.DoesNotContain("EnableLightsyncLightbar", body);
                Assert.DoesNotContain("EnableSensaHaptics", body);
            }
        }

        /// <summary>Dashboard rule: one glyph per section. Chroma and
        /// LIGHTSYNC forward the same lightbar color for two vendors, so they
        /// are two rows of one Lightbar Mirrors section under a single E781,
        /// each row keeping its own toggle, status line and footer. Sensa
        /// stays its own section under E877.</summary>
        [Fact]
        public void LightbarMirrors_OneSection_OneGlyph_TwoRows()
        {
            string page = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
            Assert.Equal(1, page.Split(new[] { "&#xE781;" }, StringSplitOptions.None).Length - 1);
            Assert.Equal(1, page.Split(new[] { "&#xE877;" }, StringSplitOptions.None).Length - 1);
            Assert.Contains("Binding Dashboard_LightbarMirrors,", page);
            Assert.DoesNotContain("Binding Dashboard_Chroma, Source={x:Static strings:Strings.Instance}, Converter={StaticResource UpperConverter}", page);
            Assert.DoesNotContain("Binding Dashboard_Lightsync, Source={x:Static strings:Strings.Instance}, Converter={StaticResource UpperConverter}", page);

            int section = page.IndexOf("Binding Dashboard_LightbarMirrors,", StringComparison.Ordinal);
            int sensa = page.IndexOf("Binding Dashboard_Sensa,", StringComparison.Ordinal);
            Assert.True(section > 0 && sensa > section);
            string card = page.Substring(section, sensa - section);
            foreach (var needle in new[]
            {
                "Binding Dashboard_Chroma,", "Binding EnableChromaLightbar", "Binding ChromaStatus", "Binding Dashboard_ChromaFooter",
                "Binding Dashboard_Lightsync,", "Binding EnableLightsyncLightbar", "Binding LightsyncStatus", "Binding Dashboard_LightsyncFooter",
            })
                Assert.Contains(needle, card);
            // One card: a single CardBorder between the section title and Sensa.
            Assert.Equal(1, card.Split(new[] { "StaticResource CardBorder" }, StringSplitOptions.None).Length - 1);

            string designer = RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            Assert.Contains("public string Dashboard_LightbarMirrors => Get(\"Dashboard_LightbarMirrors\");", designer);
        }
    }
}
