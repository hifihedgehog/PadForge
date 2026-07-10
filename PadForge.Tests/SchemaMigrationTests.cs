using System.IO;
using System.Xml.Serialization;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Tests
{
    /// <summary>Covers the v4.x schema rename of the per-(slot, device)
    /// config bag: settings files and profile exports written before the
    /// rename spell the arrays PlayStationConfigs / ProfilePlayStationConfigs
    /// and the Copy / Paste payload key __SlotPlayStationConfigs. Loads must
    /// migrate the old spellings forward; saves must emit only the new
    /// names.</summary>
    public class SchemaMigrationTests
    {
        private static SettingsFileData Deserialize(string xml)
        {
            var ser = new XmlSerializer(typeof(SettingsFileData));
            using var reader = new StringReader(xml);
            return (SettingsFileData)ser.Deserialize(reader);
        }

        private static string Serialize(SettingsFileData data)
        {
            var ser = new XmlSerializer(typeof(SettingsFileData));
            var sw = new StringWriter();
            ser.Serialize(sw, data);
            return sw.ToString();
        }

        [Fact]
        public void Legacy_Slot_Array_Migrates_And_Resaves_Under_The_New_Name()
        {
            var data = Deserialize(
                "<PadForgeSettings><AppSettings><PlayStationConfigs>" +
                "<Config SlotIndex=\"3\" LightbarRed=\"17\" AudioToneFilterMode=\"Fold\" AudioToneLimitHz=\"650\" />" +
                "</PlayStationConfigs></AppSettings></PadForgeSettings>");
            data.MigrateLegacySchema();

            Assert.NotNull(data.AppSettings.DeviceSlotConfigs);
            Assert.Single(data.AppSettings.DeviceSlotConfigs);
            Assert.Equal(3, data.AppSettings.DeviceSlotConfigs[0].SlotIndex);
            Assert.Equal(17, data.AppSettings.DeviceSlotConfigs[0].LightbarRed);
            Assert.Equal("Fold", data.AppSettings.DeviceSlotConfigs[0].AudioToneFilterMode);
            Assert.Null(data.AppSettings.LegacyDeviceSlotConfigs);

            string xml = Serialize(data);
            Assert.Contains("<DeviceSlotConfigs>", xml);
            Assert.DoesNotContain("PlayStationConfigs", xml);
        }

        [Fact]
        public void Legacy_Profile_Array_Migrates_And_Resaves_Under_The_New_Name()
        {
            var data = Deserialize(
                "<PadForgeSettings><Profiles><Profile><ProfilePlayStationConfigs>" +
                "<PlayStationConfig SlotIndex=\"1\" LightbarBlue=\"9\" />" +
                "</ProfilePlayStationConfigs></Profile></Profiles></PadForgeSettings>");
            data.MigrateLegacySchema();

            var profile = data.Profiles[0];
            Assert.NotNull(profile.DeviceSlotConfigs);
            Assert.Single(profile.DeviceSlotConfigs);
            Assert.Equal(1, profile.DeviceSlotConfigs[0].SlotIndex);
            Assert.Equal(9, profile.DeviceSlotConfigs[0].LightbarBlue);
            Assert.Null(profile.LegacyDeviceSlotConfigs);

            string xml = Serialize(data);
            Assert.Contains("<ProfileDeviceSlotConfigs>", xml);
            Assert.Contains("<Config ", xml);
            Assert.DoesNotContain("ProfilePlayStationConfigs", xml);
            Assert.DoesNotContain("<PlayStationConfig ", xml);
        }

        [Fact]
        public void Legacy_Array_Inside_DefaultProfileSnapshot_Migrates()
        {
            // The default-profile snapshot is a full ProfileData nested in
            // AppSettings, so an old file's snapshot carries the legacy
            // profile-level spelling too. Found by adversarial review: the
            // first migration sweep visited AppSettings and Profiles[] but
            // not the snapshot, stranding (and on the next save, deleting)
            // its configs.
            var data = Deserialize(
                "<PadForgeSettings><AppSettings><DefaultProfileSnapshot>" +
                "<ProfilePlayStationConfigs><PlayStationConfig SlotIndex=\"6\" LightbarGreen=\"33\" />" +
                "</ProfilePlayStationConfigs></DefaultProfileSnapshot></AppSettings></PadForgeSettings>");
            data.MigrateLegacySchema();

            var snap = data.AppSettings.DefaultProfileSnapshot;
            Assert.NotNull(snap.DeviceSlotConfigs);
            Assert.Single(snap.DeviceSlotConfigs);
            Assert.Equal(6, snap.DeviceSlotConfigs[0].SlotIndex);
            Assert.Equal(33, snap.DeviceSlotConfigs[0].LightbarGreen);
            Assert.Null(snap.LegacyDeviceSlotConfigs);

            string xml = Serialize(data);
            Assert.Contains("<ProfileDeviceSlotConfigs>", xml);
            Assert.DoesNotContain("ProfilePlayStationConfigs", xml);
        }

        [Fact]
        public void Both_Spellings_Present_New_Wins()
        {
            // A hand-edited or merge-damaged file carrying both: the new
            // spelling is authoritative and the legacy copy is discarded.
            var data = Deserialize(
                "<PadForgeSettings><AppSettings>" +
                "<DeviceSlotConfigs><Config SlotIndex=\"5\" /></DeviceSlotConfigs>" +
                "<PlayStationConfigs><Config SlotIndex=\"9\" /></PlayStationConfigs>" +
                "</AppSettings></PadForgeSettings>");
            data.MigrateLegacySchema();

            Assert.Single(data.AppSettings.DeviceSlotConfigs);
            Assert.Equal(5, data.AppSettings.DeviceSlotConfigs[0].SlotIndex);
            Assert.Null(data.AppSettings.LegacyDeviceSlotConfigs);
        }

        [Fact]
        public void Migration_Is_A_NoOp_On_Modern_And_Empty_Files()
        {
            var data = Deserialize("<PadForgeSettings />");
            data.MigrateLegacySchema();
            Assert.Null(data.AppSettings);

            data = Deserialize(
                "<PadForgeSettings><AppSettings><DeviceSlotConfigs>" +
                "<Config SlotIndex=\"2\" /></DeviceSlotConfigs></AppSettings></PadForgeSettings>");
            data.MigrateLegacySchema();
            Assert.Single(data.AppSettings.DeviceSlotConfigs);
            Assert.Equal(2, data.AppSettings.DeviceSlotConfigs[0].SlotIndex);
        }

        [Fact]
        public void Copy_Payload_Writes_The_New_Sentinel()
        {
            var ps = new PadSetting { SlotDeviceConfigsJson = "[{\"SlotIndex\":4}]" };
            string json = ps.ToJson();
            Assert.Contains("__SlotDeviceConfigs", json);
            Assert.DoesNotContain("__SlotPlayStationConfigs", json);

            var back = PadSetting.FromJson(json);
            Assert.Equal("[{\"SlotIndex\":4}]", back.SlotDeviceConfigsJson);
        }

        [Fact]
        public void Copy_Payload_Reads_The_Legacy_Sentinel()
        {
            // A payload copied to the clipboard (or nested in a per-device
            // settings entry) by a pre-rename build.
            var ps = new PadSetting { SlotDeviceConfigsJson = "[{\"SlotIndex\":7}]" };
            string legacyJson = ps.ToJson().Replace("__SlotDeviceConfigs", "__SlotPlayStationConfigs");

            var back = PadSetting.FromJson(legacyJson);
            Assert.Equal("[{\"SlotIndex\":7}]", back.SlotDeviceConfigsJson);
        }
    }
}
