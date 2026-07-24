using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    // Locks the controller NFC descriptor family (issue #241): "Any NFC
    // Tag" and "NFC Tag N" resolve against CustomInputState.NfcTag, filled
    // by SdlDeviceWrapper from the fork's SDL_GetGamepadNfcTagUid. This is
    // the surface a macro trigger / mapping source binds, so a tap on a
    // Switch Pro fires the bound action.
    [Collection("SettingsManagerStatics")]
    public class NfcTagDescriptorTests
    {
        private static MappingSource Src(string descriptor)
            => new MappingSource { Descriptor = descriptor, DeviceGuid = "" };

        private static CustomInputState StateWithTags(int span, params int[] held)
        {
            var s = new CustomInputState { NfcTag = new bool[span] };
            foreach (int b in held) s.NfcTag[b] = true;
            return s;
        }

        [Fact]
        public void AnyNfcTag_FiresWhenButtonZeroHeld()
        {
            Assert.True(SourceCoercion.IsNfcTagDescriptor("Any NFC Tag"));
            var held = StateWithTags(4, 0);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(held, Src("Any NFC Tag"), 50, 0));
            var idle = StateWithTags(4);
            Assert.False(SourceCoercion.EvaluateForButtonTarget(idle, Src("Any NFC Tag"), 50, 0));
        }

        [Fact]
        public void NumberedTag_FiresOnlyForItsOwnButton()
        {
            var held = StateWithTags(5, 0, 3); // any + tag button 3
            Assert.True(SourceCoercion.EvaluateForButtonTarget(held, Src("NFC Tag 3"), 50, 0));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(held, Src("NFC Tag 2"), 50, 0));
        }

        [Fact]
        public void NoReader_ReadsFalse()
        {
            // Null NfcTag array (no reader / not armed) never fires.
            var s = new CustomInputState();
            Assert.Null(s.NfcTag);
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, Src("Any NFC Tag"), 50, 0));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, Src("NFC Tag 1"), 50, 0));
        }

        [Fact]
        public void DescriptorRoundTrip_ButtonToDescriptorAndBack()
        {
            Assert.Equal("Any NFC Tag", SourceCoercion.NfcTagDescriptorForButton(0));
            Assert.Equal("NFC Tag 7", SourceCoercion.NfcTagDescriptorForButton(7));
            Assert.True(SourceCoercion.TryGetNfcTagButton("NFC Tag 7", out int b));
            Assert.Equal(7, b);
            Assert.True(SourceCoercion.TryGetNfcTagButton("Any NFC Tag", out int any));
            Assert.Equal(0, any);
        }

        [Fact]
        public void NonNfcDescriptors_AreNotClaimed()
        {
            Assert.False(SourceCoercion.IsNfcTagDescriptor("Button 3"));
            Assert.False(SourceCoercion.IsNfcTagDescriptor("Gamepad LeftStickTouch"));
            Assert.False(SourceCoercion.IsNfcTagDescriptor("NFC Tag 0"));   // 0 is the Any form, not numbered
            Assert.False(SourceCoercion.IsNfcTagDescriptor("NFC Tag 256")); // out of the 1..255 range
        }

        [Fact]
        public void FiresAsTriggerAndAxisContribution()
        {
            var held = StateWithTags(4, 0);
            // Trigger-pull read: 0/1.
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(held, Src("Any NFC Tag"), 0), 3);
            // Bipolar-axis contribution: 0/1.
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(held, Src("Any NFC Tag"), 0), 3);
        }

        /// <summary>The param/gate readers must see NFC descriptors (#248
        /// audit finding 4): the pickers offer them for Incremental/Ramped
        /// ParamUp/Down and Invert-on-Hold modifiers, and Path A tags work
        /// there because the PC/SC reader exposes them as raw buttons, so
        /// controller tags must read identically.</summary>
        [Fact]
        public void NfcDescriptor_DrivesIncrementalParamUp()
        {
            var rt = new PadForge.Engine.Common.Mapping.SourceKindRuntime();
            var src = new PadForge.Engine.Data.MappingSource
            {
                Kind = "Incremental",
                Descriptor = "Button 0",
                ParamUp = "Any NFC Tag",
                ParamRate = 1.0,   // full range per second
                ParamMin = 0,
                ParamMax = 1,
            };
            var held = StateWithTags(4, 0);
            double v = 0;
            for (int i = 0; i < 10; i++)
                v = rt.TickIncremental(0, "LeftTrigger", 0, src, held, 0.05);
            Assert.True(v > 0.4, $"tag-held ParamUp never ramped (v={v})");

            // Tag absent: the accumulator stops climbing.
            var idle = StateWithTags(4);
            double after = rt.TickIncremental(0, "LeftTrigger", 0, src, idle, 0.05);
            Assert.Equal(v, after, 3);
        }

        [Fact]
        public void HardwareBoolDescriptor_ReadsNfcAndRejectsOthers()
        {
            var held = StateWithTags(4, 0, 2);
            Assert.True(SourceCoercion.ReadHardwareBoolDescriptor(held, "Any NFC Tag"));
            Assert.True(SourceCoercion.ReadHardwareBoolDescriptor(held, "NFC Tag 2"));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(held, "NFC Tag 1"));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(held, "Axis 0"));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(null, "Any NFC Tag"));
        }

        /// <summary>Controller NFC is DISABLED (owner decision 2026-07-24):
        /// the Switch HD-haptic family cannot read tags and rumble at the
        /// same time (an armed NFC MCU holds report mode 0x31 while HD
        /// rumble needs 0x30), and rumble wins. No controller may report a
        /// usable reader, so the mapping picker never offers tag sources
        /// that can never fire. Dedicated PC/SC readers carry the whole tag
        /// feature and are not gated by this property. Locks the decision
        /// against a well-meaning re-enable of the old PID list.</summary>
        [Theory]
        [InlineData(0x057E, 0x2007)]  // right Joy-Con: has hardware, not used
        [InlineData(0x057E, 0x2008)]  // combined pair: has hardware, not used
        [InlineData(0x057E, 0x2009)]  // Pro Controller: has hardware, not used
        [InlineData(0x057E, 0x2006)]  // left Joy-Con: no reader
        [InlineData(0x057E, 0x2069)]  // Switch 2 Pro: no PC NFC
        [InlineData(0x057E, 0x2066)]  // Joy-Con 2 R: no PC NFC
        [InlineData(0x045E, 0x2009)]  // wrong vendor, right PID
        public void HasNfcReader_IsAlwaysFalse_RumbleWins(int vid, int pid)
        {
            var ud = new PadForge.Engine.Data.UserDevice
            { VendorId = (ushort)vid, ProdId = (ushort)pid };
            Assert.False(ud.HasNfcReader);
        }
    }
}
