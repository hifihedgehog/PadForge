using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Controllers On or Off row in Overlays, What Shows (#600 item 42)
    /// is on by default. A PadForge.xml from before it has no value for it,
    /// and a new note never turns itself on for a user who had them off: the
    /// row takes the file's own Overlays switch, on only where Overlays was
    /// on, for the app and for each profile. Loaded through the real
    /// settings load, from a file in the temp folder.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class ControllersNoteOldFileTests : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly List<string> _files = new();

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pendingDefault;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            SettingsManager.SlotMappingSets = _sets;
            foreach (string file in _files)
                try { File.Delete(file); } catch { }
        }

        private static (MainViewModel Vm, SettingsService Settings) Arrange()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            _ = new InputService(vm) { SettingsService = settings };
            return (vm, settings);
        }

        private static void Overlays(AppSettingsData app, bool on)
        {
            app.EnableMenuOverlay = on;
            app.EnableShiftLayerFlyout = on;
            app.EnableProfileOverlay = on;
            app.EnableDeviceDisconnectedNote = false;
            app.EnableDeviceConnectedNote = false;
        }

        private static void Overlays(ProfileData profile, bool on)
        {
            profile.EnableMenuOverlay = on;
            profile.EnableShiftLayerFlyout = on;
            profile.EnableProfileOverlay = on;
            profile.EnableDeviceDisconnectedNote = false;
            profile.EnableDeviceConnectedNote = false;
        }

        /// <summary>A PadForge.xml as a build before the row wrote it: the
        /// app's and two profiles' Overlays as given, and no
        /// EnableControllersNote anywhere.</summary>
        private string OldFile(bool appOverlays, bool gameOverlays, bool raceOverlays)
        {
            var app = new AppSettingsData();
            Overlays(app, appOverlays);
            var game = new ProfileData { Id = "game", Name = "Game" };
            Overlays(game, gameOverlays);
            var race = new ProfileData { Id = "race", Name = "Race" };
            Overlays(race, raceOverlays);
            var written = new StringWriter();
            new XmlSerializer(typeof(SettingsFileData)).Serialize(written,
                new SettingsFileData { AppSettings = app, Profiles = new[] { game, race } });
            string xml = Regex.Replace(written.ToString(), @"\s*<EnableControllersNote>\w+</EnableControllersNote>", "");
            Assert.DoesNotContain("EnableControllersNote", xml, StringComparison.Ordinal);
            // A StringWriter names UTF-16, and the file is written as UTF-8.
            xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
            string path = Path.Combine(Path.GetTempPath(), "PadForgeControllersNote_" + Guid.NewGuid().ToString("N") + ".xml");
            _files.Add(path);
            File.WriteAllText(path, xml);
            return path;
        }

        [Fact]
        public void AnOldFileWithOverlaysOff_KeepsOverlaysOff_AndEachProfileFollowsItsOwn()
        {
            string path = OldFile(appOverlays: false, gameOverlays: false, raceOverlays: true);
            var (vm, settings) = Arrange();
            settings.LoadFromFile(path);

            Assert.False(vm.Dashboard.EnableControllersNote);
            Assert.False(vm.Dashboard.OverlaysAnyEnabled);
            Assert.False(SettingsManager.Profiles.Single(p => p.Id == "game").EnableControllersNote);
            Assert.True(SettingsManager.Profiles.Single(p => p.Id == "race").EnableControllersNote);
            GC.KeepAlive(vm);
        }

        [Fact]
        public void AnOldFileWithOverlaysOn_ShowsTheNote()
        {
            string path = OldFile(appOverlays: true, gameOverlays: true, raceOverlays: false);
            var (vm, settings) = Arrange();
            settings.LoadFromFile(path);

            Assert.True(vm.Dashboard.EnableControllersNote);
            Assert.True(vm.Dashboard.OverlaysAnyEnabled);
            Assert.True(SettingsManager.Profiles.Single(p => p.Id == "game").EnableControllersNote);
            Assert.False(SettingsManager.Profiles.Single(p => p.Id == "race").EnableControllersNote);
            GC.KeepAlive(vm);
        }

        /// <summary>One row of Overlays on is Overlays on. A file that holds
        /// the row keeps what it holds, whatever its Overlays say, and every
        /// save writes the row.</summary>
        [Fact]
        public void OneRowOn_IsOverlaysOn_AndAFileThatHoldsTheRowKeepsIt()
        {
            var app = new AppSettingsData();
            Overlays(app, false);
            app.EnableDeviceConnectedNote = true;
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            var written = new StringWriter();
            serializer.Serialize(written, app);
            string old = Regex.Replace(written.ToString(), @"\s*<EnableControllersNote>\w+</EnableControllersNote>", "");
            var read = (AppSettingsData)serializer.Deserialize(new StringReader(old));
            read.MigrateLegacySchema();
            Assert.True(read.EnableControllersNote);

            // Overlays off, the row on by the user's own hand: it stays on.
            var held = new AppSettingsData { EnableControllersNote = true };
            Overlays(held, false);
            written = new StringWriter();
            serializer.Serialize(written, held);
            Assert.Contains("<EnableControllersNote>true</EnableControllersNote>", written.ToString(), StringComparison.Ordinal);
            read = (AppSettingsData)serializer.Deserialize(new StringReader(written.ToString()));
            read.MigrateLegacySchema();
            Assert.True(read.EnableControllersNote);
            // And off stays off with Overlays on.
            var off = new ProfileData { Id = "p", Name = "P", EnableControllersNote = false };
            var profileSerializer = new XmlSerializer(typeof(ProfileData));
            written = new StringWriter();
            profileSerializer.Serialize(written, off);
            var readProfile = (ProfileData)profileSerializer.Deserialize(new StringReader(written.ToString()));
            readProfile.MigrateLegacySchema();
            Assert.False(readProfile.EnableControllersNote);
        }
    }
}
