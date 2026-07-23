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
    }
}
