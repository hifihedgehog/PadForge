using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Owner report 2026-07-13: an imported community profile showed a concrete
    /// controller on the SECONDARY sources of multi-source rows instead of
    /// "(Any device)". Storage-side guard: the translator must emit EVERY source
    /// (primary and secondary) with an empty DeviceGuid, the documented "device
    /// on this slot" form, so no device snapshot is baked at import time. The
    /// wrong device was a display-only regression in the App layer; this pins the
    /// invariant that layer relies on.
    /// </summary>
    public class WorkshopSourceDeviceGuidTests
    {
        // Factorio Steam Deck (3353173512): multi-source KbM rows (e.g. KbmKey43
        // fans in two "Gamepad ..." sources), the exact shape the owner hit.
        [Fact]
        public void FactorioDeck_EverySourceHasEmptyDeviceGuid()
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(TestFixtures.Read(3353173512)));
            var translated = new ConfigTranslator()
                .Translate(config, new TranslationOptions { FileId = 3353173512 });

            var allRows = translated.XboxMappingSet.Rows
                .Concat(translated.KbmMappingSet.Rows)
                .ToList();

            // The bug only surfaces on multi-source rows, so the fixture must
            // actually carry some or this guard proves nothing.
            Assert.Contains(allRows, r => r.Sources.Count > 1);

            foreach (var row in allRows)
                foreach (var src in row.Sources)
                    Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                        $"{row.LayerMask}/{row.Target} source '{src.Descriptor}' baked a DeviceGuid '{src.DeviceGuid}'");
        }
    }
}
