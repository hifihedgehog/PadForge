using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Automap-vs-imported-profile double mapping (#9 follow-up): a Workshop
    /// import spells out every binding as explicit rows, so when the user
    /// assigned a gamepad the legacy-automap merge added the device's
    /// auto-mapped descriptors on top and every input fired twice. The fix is
    /// <see cref="MappingSet.Authoritative"/>: the materializer stamps it on
    /// imported sets and MergeMappingSetsFromLegacy contributes nothing to a
    /// flagged set. These tests pin the flag's whole life cycle: merge gate,
    /// deep clone, materializer stamp, and XML round-trip.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuthoritativeMappingSetTests : IDisposable
    {
        private static readonly Guid PadGuid = new("33333333-3333-3333-3333-333333333333");
        private static readonly Guid DepartedGuid = new("44444444-4444-4444-4444-444444444444");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly MappingSet[] _savedMappingSets;
        private readonly Action _savedAfterRefresh;

        public AuthoritativeMappingSetTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
            SettingsService.AfterMappingSetsRefreshed = null;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        /// <summary>Seeds slot 0 with one online gamepad whose PadSetting
        /// carries auto-mapped legacy descriptors for ButtonA and ButtonB,
        /// which is exactly what the merge rebuilds rows from.</summary>
        private static void ArrangeGamepadOnSlot0()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];

            var ud = new UserDevice
            {
                InstanceGuid = PadGuid,
                ProductName = "Test Gamepad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var us = new UserSetting { InstanceGuid = PadGuid, MapTo = 0 };
            us.SetPadSetting(new PadSetting
            {
                ButtonA = "Button 0",
                ButtonB = "Button 1",
            });
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);
        }

        /// <summary>An imported-style set: one explicit row with an abstract
        /// empty-guid source, plus one row owned by a device that is not on
        /// the slot (the departed-cleanup probe).</summary>
        private static MappingSet BuildImportedStyleSet(bool authoritative)
        {
            var ms = new MappingSet { Authoritative = authoritative };
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources = { new MappingSource { Descriptor = "Gamepad ButtonA", DeviceGuid = "" } },
            });
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonX",
                Sources = { new MappingSource
                {
                    Descriptor = "Button 2",
                    DeviceGuid = DepartedGuid.ToString().ToLowerInvariant(),
                } },
            });
            return ms;
        }

        [Fact]
        public void LegacyMerge_AuthoritativeSet_GetsNoInjectedSourcesAndNoAppendedRows()
        {
            ArrangeGamepadOnSlot0();
            SettingsManager.SlotMappingSets[0] = BuildImportedStyleSet(authoritative: true);

            SettingsService.RefreshMappingSetsFromLegacy();

            var merged = SettingsManager.SlotMappingSets[0];
            Assert.True(merged.Authoritative);

            // No injected source on the imported row and no appended
            // automap row: the set owns its slot's mappings completely.
            var rowA = Assert.Single(merged.Rows);
            Assert.Equal("ButtonA", rowA.Target);
            var src = Assert.Single(rowA.Sources);
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            Assert.True(string.IsNullOrEmpty(src.DeviceGuid));
            Assert.DoesNotContain(merged.Rows, r => r.Target == "ButtonB");

            // Departed-device cleanup and the empty-row drop still ran:
            // the row owned by the device that left the slot is gone.
            Assert.DoesNotContain(merged.Rows, r => r.Target == "ButtonX");
        }

        [Fact]
        public void LegacyMerge_UnflaggedSet_StillGetsAutomapMerge()
        {
            // Same-window positive control: the identical set without the
            // flag DOES pick up the device's auto-mapped legacy descriptors
            // (this is the double-mapping behavior the flag suppresses, and
            // the proof the merge machinery ran in the test above).
            ArrangeGamepadOnSlot0();
            SettingsManager.SlotMappingSets[0] = BuildImportedStyleSet(authoritative: false);

            SettingsService.RefreshMappingSetsFromLegacy();

            var merged = SettingsManager.SlotMappingSets[0];
            Assert.False(merged.Authoritative);

            // The imported-style row gained the device's automap source ...
            var rowA = merged.Rows.Single(r => r.Target == "ButtonA");
            Assert.Equal(2, rowA.Sources.Count);
            Assert.Contains(rowA.Sources, s => s.Descriptor == "Gamepad ButtonA");
            Assert.Contains(rowA.Sources, s =>
                s.Descriptor == "Button 0"
                && string.Equals(s.DeviceGuid, PadGuid.ToString(),
                    StringComparison.OrdinalIgnoreCase));

            // ... and the automap-only target arrived as an appended row.
            var rowB = merged.Rows.Single(r => r.Target == "ButtonB");
            Assert.Contains(rowB.Sources, s => s.Descriptor == "Button 1");
        }

        [Fact]
        public void CloneMappingSetDeep_CarriesAuthoritative()
        {
            var flagged = InputService.CloneMappingSetDeep(new MappingSet { Authoritative = true });
            Assert.True(flagged.Authoritative);

            var unflagged = InputService.CloneMappingSetDeep(new MappingSet());
            Assert.False(unflagged.Authoritative);
        }

        [Fact]
        public void Materializer_StampsAuthoritativeOnBothImportedSets()
        {
            var translated = new TranslatedProfile
            {
                Name = "Community Config",
                NeedsXboxSlot = true,
                NeedsKbmSlot = true,
            };

            var profile = WorkshopProfileMaterializer.Materialize(translated);

            Assert.True(profile.SlotMappingSets[0].Authoritative);
            Assert.True(profile.SlotMappingSets[1].Authoritative);
            // Unclaimed slots automap normally when the user creates them.
            for (int i = 2; i < profile.SlotMappingSets.Length; i++)
                Assert.False(profile.SlotMappingSets[i].Authoritative);
        }

        [Fact]
        public void MappingSetXml_RoundTripsAuthoritative_AndOldXmlReadsFalse()
        {
            var serializer = new XmlSerializer(typeof(MappingSet));

            var ms = new MappingSet { Authoritative = true };
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources = { new MappingSource { Descriptor = "Gamepad ButtonA" } },
            });

            string xml;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, ms);
                xml = writer.ToString();
            }
            using (var reader = new StringReader(xml))
            {
                var roundTripped = (MappingSet)serializer.Deserialize(reader);
                Assert.True(roundTripped.Authoritative);
                Assert.Equal("ButtonA", Assert.Single(roundTripped.Rows).Target);
            }

            // Pre-flag XML carries no attribute and must deserialize false.
            using (var reader = new StringReader("<MappingSet><Row Target=\"ButtonA\" /></MappingSet>"))
            {
                var old = (MappingSet)serializer.Deserialize(reader);
                Assert.False(old.Authoritative);
                Assert.Equal("ButtonA", Assert.Single(old.Rows).Target);
            }
        }
    }
}
