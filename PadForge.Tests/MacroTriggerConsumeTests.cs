using System;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>A key that alone triggers a macro with Consume Trigger
    /// Buttons on is eaten by the hook on a keyboard with Consume Mapped
    /// Inputs on, so a macro pad key no longer types itself before its macro
    /// runs (discussion #537).</summary>
    public class MacroTriggerConsumeTests
    {
        private static readonly Guid Pad = Guid.NewGuid();

        private static MacroItem KeyMacro(params int[] keys) => new MacroItem
        {
            TriggerDeviceGuid = Pad,
            TriggerRawButtons = keys,
        };

        [Fact]
        public void OneKeyTrigger_IsEaten()
        {
            Assert.True(InputService.MacroTriggerConsumesKey(KeyMacro(0x41), Pad, out int index));
            Assert.Equal(0x41, index);
        }

        [Fact]
        public void AChord_IsLeftAlone()
            => Assert.False(InputService.MacroTriggerConsumesKey(KeyMacro(0x11, 0x70), Pad, out _));

        [Fact]
        public void AnotherDevicesKey_IsLeftAlone()
            => Assert.False(InputService.MacroTriggerConsumesKey(KeyMacro(0x41), Guid.NewGuid(), out _));

        [Fact]
        public void ConsumeOff_LeavesTheKey()
        {
            var macro = KeyMacro(0x41);
            macro.ConsumeTriggerButtons = false;
            Assert.False(InputService.MacroTriggerConsumesKey(macro, Pad, out _));
        }

        [Fact]
        public void ADisabledMacro_LeavesTheKey()
        {
            var macro = KeyMacro(0x41);
            macro.IsEnabled = false;
            Assert.False(InputService.MacroTriggerConsumesKey(macro, Pad, out _));
        }

        [Theory]
        [InlineData(MacroTriggerMode.Always)]
        [InlineData(MacroTriggerMode.CustomExpression)]
        public void AModeThatReadsNoTrigger_LeavesTheKey(MacroTriggerMode mode)
        {
            var macro = KeyMacro(0x41);
            macro.TriggerMode = mode;
            Assert.False(InputService.MacroTriggerConsumesKey(macro, Pad, out _));
        }

        [Fact]
        public void AControllerButtonTrigger_IsNoKey()
            => Assert.False(InputService.MacroTriggerConsumesKey(new MacroItem { TriggerButtons = 0x1000 }, Pad, out _));
    }
}
