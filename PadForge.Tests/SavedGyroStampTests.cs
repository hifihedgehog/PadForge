using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A settings file saved while the engine still read the three
    /// gyro stamps keeps them on slots whose devices were assigned long ago.
    /// Assignment, the fold's other seam, never runs for those devices again,
    /// so the load folds them onto the devices' Gyro cards: the live slots,
    /// every stored profile and the Default profile's snapshot.</summary>
    public class SavedGyroStampTests
    {
        /// <summary>The old shape: slot 0's set carries the stamps, one device
        /// is assigned there, and a second device on slot 1 shares the same
        /// PadSetting by checksum.</summary>
        private const string OldFile = @"<?xml version=""1.0"" encoding=""utf-8""?>
<PadForgeSettings>
  <UserSettings>
    <Setting><InstanceGuid>11111111-1111-1111-1111-111111111111</InstanceGuid><MapTo>0</MapTo><PadSettingChecksum>AAAA0001</PadSettingChecksum></Setting>
    <Setting><InstanceGuid>22222222-2222-2222-2222-222222222222</InstanceGuid><MapTo>1</MapTo><PadSettingChecksum>AAAA0001</PadSettingChecksum></Setting>
  </UserSettings>
  <PadSettings>
    <PadSetting><PadSettingChecksum>AAAA0001</PadSettingChecksum></PadSetting>
  </PadSettings>
  <SlotMappingSets>
    <MappingSet Authoritative=""true"" WorkshopGyroRatchetDescriptors=""Gamepad Paddle4|Gamepad Paddle2"" WorkshopGyroRecenterOnEngage=""true"" />
    <MappingSet />
  </SlotMappingSets>
  <Profiles>
    <Profile>
      <Name>Old</Name>
      <Entries>
        <Entry><InstanceGuid>11111111-1111-1111-1111-111111111111</InstanceGuid><MapTo>0</MapTo><PadSettingChecksum>BBBB0002</PadSettingChecksum></Entry>
      </Entries>
      <ProfilePadSettings>
        <PadSetting><PadSettingChecksum>BBBB0002</PadSettingChecksum></PadSetting>
      </ProfilePadSettings>
      <ProfileSlotMappingSets>
        <MappingSet Authoritative=""true"" WorkshopGyroActivation=""Base=Hold,All,Gamepad ButtonA|Gamepad ButtonB;Layer_1=Toggle,Any,Gamepad ButtonX"" />
      </ProfileSlotMappingSets>
    </Profile>
  </Profiles>
</PadForgeSettings>";

        private static SettingsFileData Load(string xml)
        {
            var data = (SettingsFileData)new XmlSerializer(typeof(SettingsFileData)).Deserialize(new StringReader(xml));
            Assert.NotNull(data);
            return data;
        }

        [Fact]
        public void TheLiveSlots_FoldOntoTheAssignedDevice_AndTheSharedCopyStays()
        {
            var data = Load(OldFile);
            data.FoldCarriedGyroStamps();

            var slot0 = data.Settings.Single(s => s.MapTo == 0);
            var slot1 = data.Settings.Single(s => s.MapTo == 1);
            var ps0 = data.PadSettings.Single(p => p.PadSettingChecksum == slot0.PadSettingChecksum);
            var ps1 = data.PadSettings.Single(p => p.PadSettingChecksum == slot1.PadSettingChecksum);
            Assert.Equal("Base=Release,Any,Gamepad Paddle4|Gamepad Paddle2", ps0.GyroActivation);
            Assert.Equal("1", ps0.GyroRecenterOnEngage);
            // Slot 1's device shared the template and has no stamps of its own.
            Assert.NotSame(ps0, ps1);
            Assert.Equal("", ps1.GyroActivation);
            Assert.Equal("0", ps1.GyroRecenterOnEngage);
            Assert.Equal("", data.SlotMappingSets[0].WorkshopGyroRatchetDescriptors);
            Assert.False(data.SlotMappingSets[0].WorkshopGyroRecenterOnEngage);
        }

        [Fact]
        public void AStoredProfile_FoldsToo()
        {
            var data = Load(OldFile);
            data.FoldCarriedGyroStamps();

            var p = Assert.Single(data.Profiles);
            var ps = Assert.Single(p.PadSettings);
            Assert.Equal("Base=Hold,All,Gamepad ButtonA|Gamepad ButtonB;Layer_1=Toggle,Any,Gamepad ButtonX", ps.GyroActivation);
            Assert.Equal("", p.SlotMappingSets[0].WorkshopGyroActivation);
        }

        [Fact]
        public void ASlotWithNoDevice_KeepsItsStampsForTheAssignment()
        {
            var data = Load(OldFile.Replace("<MapTo>0</MapTo><PadSettingChecksum>AAAA0001", "<MapTo>2</MapTo><PadSettingChecksum>AAAA0001"));
            data.FoldCarriedGyroStamps();
            Assert.Equal("Gamepad Paddle4|Gamepad Paddle2", data.SlotMappingSets[0].WorkshopGyroRatchetDescriptors);
        }

        /// <summary>The load runs the fold on every file it reads.</summary>
        [Fact]
        public void TheLoad_RunsTheFold()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string svc = File.ReadAllText(Path.Combine(dir!.FullName, "PadForge.App", "Services", "SettingsService.cs"));
            int load = svc.IndexOf("public void LoadFromFile(", System.StringComparison.Ordinal);
            int migrate = svc.IndexOf("data.MigrateLegacySchema();", load, System.StringComparison.Ordinal);
            int fold = svc.IndexOf("data.FoldCarriedGyroStamps();", load, System.StringComparison.Ordinal);
            int link = svc.IndexOf("us.SetPadSetting(ps);", load, System.StringComparison.Ordinal);
            Assert.True(migrate > 0 && fold > migrate && link > fold, "the fold must run after the migration and before the devices take their PadSettings");
            string transfer = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Common", "ProfileTransfer.cs"));
            Assert.Contains("profile.FoldCarriedGyroStamps();", transfer);
        }
    }
}
