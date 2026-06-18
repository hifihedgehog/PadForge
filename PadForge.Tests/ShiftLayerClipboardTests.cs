using System.Collections.Generic;
using System.Text.Json;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Covers the shift-layer copy/paste serialization (#119): the
    /// clipboard must carry the ShiftActivator definitions (including the new
    /// Cycle Previous button + wrap/Base fields) and the Base flyout appearance,
    /// so Copy/Paste matches Copy From. The bug was that shift layers were
    /// dropped from copy/paste while Copy From kept them.</summary>
    public class ShiftLayerClipboardTests
    {
        [Fact]
        public void ShiftActivator_CycleFields_RoundTripThroughSystemTextJson()
        {
            var a = new ShiftActivator
            {
                DeviceGuid = "dev-next",
                Descriptor = "Button 5",
                Mode = "Cycle",
                LayerMask = "Weapons",
                LayerName = "Weapon Cycle",
                CycleLayers = "Shift 1|Shift 2|Shift 3",
                CyclePrevDeviceGuid = "dev-prev",
                CyclePrevDescriptor = "Button 4",
                CycleWrap = false,
                CycleIncludeBase = true,
                Icon = "🔫",
            };

            string json = JsonSerializer.Serialize(a);
            var back = JsonSerializer.Deserialize<ShiftActivator>(json);

            Assert.Equal("Cycle", back.Mode);
            Assert.Equal("Shift 1|Shift 2|Shift 3", back.CycleLayers);
            // The fields the copy path used to drop:
            Assert.Equal("dev-prev", back.CyclePrevDeviceGuid);
            Assert.Equal("Button 4", back.CyclePrevDescriptor);
            Assert.False(back.CycleWrap);
            Assert.True(back.CycleIncludeBase);
            Assert.Equal("🔫", back.Icon);
        }

        [Fact]
        public void PadSetting_ToJson_FromJson_PreservesSlotShiftActivatorsJson()
        {
            var activators = new List<ShiftActivator>
            {
                new ShiftActivator { LayerMask = "Shift 1", Mode = "Passive" },
                new ShiftActivator { LayerMask = "Weapons", Mode = "Cycle",
                    CyclePrevDescriptor = "Button 4", CycleWrap = false },
            };
            string innerJson = JsonSerializer.Serialize(activators);

            var ps = new PadSetting { SlotShiftActivatorsJson = innerJson };
            string outer = ps.ToJson(VirtualControllerType.Xbox, false);
            var back = PadSetting.FromJson(outer);

            Assert.NotNull(back);
            Assert.Equal(innerJson, back.SlotShiftActivatorsJson);

            var roundTripped = JsonSerializer.Deserialize<List<ShiftActivator>>(back.SlotShiftActivatorsJson);
            Assert.Equal(2, roundTripped.Count);
            Assert.Equal("Passive", roundTripped[0].Mode);
            Assert.Equal("Button 4", roundTripped[1].CyclePrevDescriptor);
            Assert.False(roundTripped[1].CycleWrap);
        }
    }
}
