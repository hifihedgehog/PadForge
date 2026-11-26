using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The trigger effects a program's device hears from the slots it is
    /// assigned to (#600 item 18), as the poll pass hands them on
    /// (InputManager.PassTriggerEffects).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class ProgramTriggerEffectPassTests : IDisposable
    {
        private readonly Func<int, MacroTriggerEffectState> _savedProvider = UserEffectsDispatcher.MacroTriggerEffectsProvider;
        private readonly Dictionary<int, MacroTriggerEffectState> _stores = new();
        private readonly List<(byte[] Left, byte[] Right)> _heard = new();

        public ProgramTriggerEffectPassTests()
        {
            UserEffectsDispatcher.MacroTriggerEffectsProvider = slot => _stores.TryGetValue(slot, out var s) ? s : null;
        }

        public void Dispose() => UserEffectsDispatcher.MacroTriggerEffectsProvider = _savedProvider;

        private ProgramDevice Device()
        {
            var device = new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://fx", "Fx",
                new ProgramDeviceShape { TriggerEffects = true });
            device.TriggerEffectsRequested += (l, r) => _heard.Add((l, r));
            return device;
        }

        private MacroTriggerEffectState Store(int slot)
        {
            var store = new MacroTriggerEffectState(slot);
            _stores[slot] = store;
            return store;
        }

        private static byte[] Effect(byte mode, byte strength)
        {
            var block = new byte[11];
            block[0] = mode;
            block[1] = strength;
            return block;
        }

        private static void Pass(ProgramDevice device, params int[] slots)
        {
            var settings = new UserSetting[slots.Length];
            for (int i = 0; i < slots.Length; i++) settings[i] = new UserSetting { MapTo = slots[i] };
            Span<byte> left = stackalloc byte[11];
            Span<byte> right = stackalloc byte[11];
            InputManager.PassTriggerEffects(device, settings, settings.Length, left, right);
        }

        /// <summary>The game stops writing the effect while the device stays
        /// on its slot: the program hears the off once, and nothing more
        /// after it.</summary>
        [Fact]
        public void AnEffectThatEnds_ReachesTheProgram_AsTheOffPair()
        {
            var device = Device();
            var store = Store(13);
            store.Play(Effect(0x26, 7), MacroTriggerEffectSide.Right, long.MaxValue, null);
            Pass(device, 13);
            Assert.Single(_heard);
            Assert.Equal(0x26, _heard[0].Right[0]);

            store.Clear();
            Pass(device, 13);
            Pass(device, 13);
            Assert.Equal(2, _heard.Count);
            Assert.All(_heard[1].Left, b => Assert.Equal(0, b));
            Assert.All(_heard[1].Right, b => Assert.Equal(0, b));
            var last = device.LastTriggerEffects;
            Assert.NotNull(last);
            Assert.All(last.Value.Right, b => Assert.Equal(0, b));
        }

        /// <summary>A device that never heard an effect hears no off
        /// either.</summary>
        [Fact]
        public void ADeviceThatHeardNoEffect_HearsNothing()
        {
            var device = Device();
            Pass(device, 13);
            Pass(device, 13);
            Assert.Empty(_heard);
            Assert.Null(device.LastTriggerEffects);
        }

        /// <summary>One device on two slots whose games write different
        /// effects hears the first slot's, once, as directional force
        /// feedback takes the first slot.</summary>
        [Fact]
        public void ADeviceOnTwoSlots_HearsTheFirstSlotsEffect_Once()
        {
            var device = Device();
            Store(13).Play(Effect(0x26, 3), MacroTriggerEffectSide.Right, long.MaxValue, null);
            Store(14).Play(Effect(0x21, 9), MacroTriggerEffectSide.Right, long.MaxValue, null);
            for (int i = 0; i < 10; i++) Pass(device, 13, 14);
            Assert.Single(_heard);
            Assert.Equal(0x26, _heard[0].Right[0]);
        }
    }
}
