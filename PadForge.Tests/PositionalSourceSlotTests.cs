using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard: the formula editor's letter slots must match the ones
    /// the engine actually builds.
    ///
    /// <para>BuildCustomContribsForBipolarAxis skips two source classes with
    /// NO placeholder, so they shift every later letter: the bipolar Neg
    /// pair (Sources[1] on the primary's device with Invert flipped, which
    /// merges into the primary's own slot) and any InvertOnHold source
    /// (a row modifier that never enters the combine). Null and
    /// postpone-suppressed sources DO get a 0f placeholder there, precisely
    /// so the letters stay stable.</para>
    ///
    /// <para>The UI counted the primary plus ExtraSources.Count flat. The
    /// load path materializes Sources[1..] as visible ExtraSources rows,
    /// including the promoted Neg pair, so that count ran ahead of the
    /// engine's: a formula referencing the last letter validated green and
    /// silently read nothing at runtime, and the chips named the wrong
    /// input.</para>
    /// </summary>
    public class PositionalSourceSlotTests
    {
        private const string Dev = "11111111-2222-3333-4444-555555555555";
        private const string Other = "99999999-8888-7777-6666-555555555555";

        private static MappingItem Row(string target = "LeftThumbAxisX")
        {
            var m = new MappingItem(target, target, MappingCategory.LeftStick)
            {
                SourceDescriptor = "Button 4",
                PrimarySourceDeviceGuid = Dev,
            };
            return m;
        }

        private static MappingSourceItem Src(string guid, string desc, bool invert = false,
            string kind = "Direct")
            => new MappingSourceItem { DeviceGuid = guid, Descriptor = desc, Invert = invert, Kind = kind };

        [Fact]
        public void PlainRow_CountsPrimaryPlusExtras()
        {
            var m = Row();
            m.ExtraSources.Add(Src(Other, "Axis 2"));
            Assert.Equal(2, m.PositionalSourceCount);
        }

        [Fact]
        public void BipolarNegPair_DoesNotTakeItsOwnLetter()
        {
            // The exact reported case: primary + promoted neg + a real
            // second source. Engine slots are a and b, not a, b and c.
            var m = Row();
            m.ExtraSources.Add(Src(Dev, "Button 5", invert: true));   // the neg pair
            m.ExtraSources.Add(Src(Other, "Axis 2"));
            Assert.Equal(2, m.PositionalSourceCount);

            // And b must name the real second source, not the neg half.
            Assert.Contains("Axis 2", m.VariableBLabel);
        }

        [Fact]
        public void InvertOnHoldSource_DoesNotTakeALetter()
        {
            var m = Row();
            m.ExtraSources.Add(Src(Other, "Button 9", kind: "InvertOnHold"));
            m.ExtraSources.Add(Src(Other, "Axis 2"));
            Assert.Equal(2, m.PositionalSourceCount);
            Assert.Contains("Axis 2", m.VariableBLabel);
        }

        [Fact]
        public void NegPairAndModifierTogether_BothDrop()
        {
            var m = Row();
            m.ExtraSources.Add(Src(Dev, "Button 5", invert: true));           // neg pair
            m.ExtraSources.Add(Src(Other, "Button 9", kind: "InvertOnHold")); // modifier
            m.ExtraSources.Add(Src(Other, "Axis 2"));
            Assert.Equal(2, m.PositionalSourceCount);
        }

        [Fact]
        public void SameDeviceSameInvert_IsNotANegPair()
        {
            // Two ordinary sources that happen to share a device must keep
            // their own letters. Only a FLIPPED Invert makes it the pair.
            var m = Row();
            m.ExtraSources.Add(Src(Dev, "Button 5", invert: false));
            Assert.Equal(2, m.PositionalSourceCount);
        }

        [Fact]
        public void NonBipolarTarget_NeverMergesANegPair()
        {
            // The engine only looks for a neg pair on a bipolar-axis target.
            var m = Row("ButtonA");
            m.ExtraSources.Add(Src(Dev, "Button 5", invert: true));
            Assert.Equal(2, m.PositionalSourceCount);
        }

        [Fact]
        public void OutOfRangeReference_IsNowWarned()
        {
            // The user-visible consequence: "c" on a row the engine gives
            // two slots is a dead reference, and the editor has to say so.
            var m = Row();
            m.ExtraSources.Add(Src(Dev, "Button 5", invert: true));   // neg pair
            m.ExtraSources.Add(Src(Other, "Axis 2"));
            m.CombineMode = "Custom";
            m.CombineExpression = "c*0.5";
            Assert.True(m.IsCombineExpressionWarning,
                "a reference past the engine's slot count must warn");

            m.CombineExpression = "b*0.5";
            Assert.False(m.IsCombineExpressionWarning,
                "b is in range on this row and must not warn");
        }

        [Fact]
        public void EmptyRow_HasNoSlots()
        {
            var m = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick);
            Assert.Equal(0, m.PositionalSourceCount);
            Assert.Equal("", m.VariableALabel);
        }
    }
}
