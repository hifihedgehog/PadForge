using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #393 (discussion #386, truskj): the pressure-scaled turbo's pressure
    /// takes its direction from the macro's trigger. A stick pushed into the
    /// trigger's half ramps 0 at center to 1 at full push whichever half it
    /// is, a full-axis or legacy Any trigger keeps the absolute read, and a
    /// pressure source that is not the trigger's axis is byte-identical to
    /// the #290 read.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PressureDirectionTests
    {
        private static readonly Guid Dev = new("13ea3b23-bb17-802d-f268-c194414535f8");
        private static readonly Guid Other = new("aaaa1111-2222-3333-4444-555566667777");

        private static MacroAction Source(int axisIndex = 1, Guid? device = null) => new MacroAction
        {
            Type = MacroActionType.RepeatKeyWhileHeld,
            PressureScaledRate = true,
            SourceDeviceGuid = device ?? Dev,
            SourceDeviceAxisIndex = axisIndex,
        };

        private static MacroItem WithEntry(MacroAxisTarget target, bool half, bool invert, bool either, Guid? device = null)
        {
            var m = new MacroItem();
            m.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new MacroItem.TriggerInputEntry
                {
                    DeviceGuid = device ?? Dev,
                    AxisTarget = target,
                    HalfAxis = half,
                    Invert = invert,
                    Bidirectional = either,
                },
            });
            return m;
        }

        private static float R(MacroItem m, MacroAction a, int raw) => InputManager.ResolveTurboPressure01(m, a, raw);

        /// <summary>The reporter's case: trigger on the stick's lower half
        /// (Half Axis plus Invert), pressure source the same stick axis. A
        /// gentle upward push must resolve LOWER than a hard one.</summary>
        [Fact]
        public void LowerHalfTrigger_RampsFromCenterDownward()
        {
            var m = WithEntry(MacroAxisTarget.LeftStickY, half: true, invert: true, either: false);
            var a = Source(1);
            Assert.Equal(0f, R(m, a, 32768), 3);
            Assert.Equal(0.5f, R(m, a, 16384), 3);
            Assert.Equal(1f, R(m, a, 0), 3);
            Assert.Equal(0f, R(m, a, 65535), 3);        // the other half floors at 0
            Assert.True(R(m, a, 24576) < R(m, a, 8192)); // gentle < hard
        }

        [Fact]
        public void UpperHalfTrigger_RampsFromCenterUpward()
        {
            var m = WithEntry(MacroAxisTarget.LeftStickY, half: true, invert: false, either: false);
            var a = Source(1);
            Assert.Equal(0f, R(m, a, 32768), 3);
            Assert.Equal(0.5f, R(m, a, 32768 + 16384), 3);
            Assert.Equal(1f, R(m, a, 65535), 3);
            Assert.Equal(0f, R(m, a, 0), 3);
        }

        [Fact]
        public void EitherHalfTrigger_ReadsAbsoluteDeflection()
        {
            var m = WithEntry(MacroAxisTarget.LeftStickY, half: true, invert: false, either: true);
            var a = Source(1);
            Assert.Equal(0.5f, R(m, a, 16384), 3);
            Assert.Equal(0.5f, R(m, a, 32768 + 16384), 3);
            Assert.Equal(1f, R(m, a, 0), 3);
            Assert.Equal(0f, R(m, a, 32768), 3);
        }

        [Fact]
        public void FullAxisTrigger_IsTriggerStyle_AndInvertFlips()
        {
            var m = WithEntry(MacroAxisTarget.LeftTrigger, half: false, invert: false, either: false);
            var a = Source(2);
            Assert.Equal(0.25f, R(m, a, 16384), 3);
            var mi = WithEntry(MacroAxisTarget.LeftTrigger, half: false, invert: true, either: false);
            Assert.Equal(0.75f, R(mi, a, 16384), 3);
        }

        [Fact]
        public void LegacySlotTrigger_HonorsPositiveNegativeAndAny()
        {
            var a = Source(1);
            var neg = new MacroItem { TriggerAxisTargets = new[] { MacroAxisTarget.LeftStickY }, TriggerAxisDirections = new[] { MacroAxisDirection.Negative } };
            Assert.Equal(0.5f, R(neg, a, 16384), 3);
            Assert.Equal(0f, R(neg, a, 49152), 3);
            var pos = new MacroItem { TriggerAxisTargets = new[] { MacroAxisTarget.LeftStickY }, TriggerAxisDirections = new[] { MacroAxisDirection.Positive } };
            Assert.Equal(0.5f, R(pos, a, 49152), 3);
            var any = new MacroItem { TriggerAxisTargets = new[] { MacroAxisTarget.LeftStickY }, TriggerAxisDirections = new[] { MacroAxisDirection.Any } };
            Assert.Equal(16384f / 65535f, R(any, a, 16384), 4);
        }

        /// <summary>A pressure source that is not the trigger's axis keeps
        /// the shipped absolute read: a different axis index, a different
        /// device, no macro at all.</summary>
        [Fact]
        public void NotTheTriggersAxis_IsByteIdenticalToTheAbsoluteRead()
        {
            var m = WithEntry(MacroAxisTarget.LeftStickY, half: true, invert: true, either: false);
            Assert.Equal(16384f / 65535f, R(m, Source(2), 16384), 4);          // other axis
            Assert.Equal(16384f / 65535f, R(m, Source(1, Other), 16384), 4);   // other device
            Assert.Equal(16384f / 65535f, R(null, Source(1), 16384), 4);       // no macro
            Assert.Equal(16384f / 65535f, R(new MacroItem(), Source(1), 16384), 4); // no trigger
        }

        [Fact]
        public void RawIsClampedToTheAxisRange()
        {
            var m = WithEntry(MacroAxisTarget.LeftStickY, half: true, invert: false, either: false);
            Assert.Equal(1f, R(m, Source(1), 70000), 3);
            Assert.Equal(0f, R(m, Source(1), -5), 3);
        }

        /// <summary>Every pressure read site in both evaluator twins carries
        /// the macro, and the volume and mouse actions keep the raw read.</summary>
        [Fact]
        public void ReadSites_CarryTheMacro_AndTheRawReadStays()
        {
            string ev = RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Contains("private float ReadTurboPressure(MacroItem macro, MacroAction action)", ev);
            Assert.Equal(0, CountOf(ev, "ReadTurboPressure(a)"));
            Assert.Equal(0, CountOf(ev, "ReadTurboPressure(action)"));
            Assert.Equal(8, CountOf(ev, "LatchPhaseOn(macro, a)"));
            Assert.Contains("private void ExecuteSingleAction(ref Gamepad gp, MacroItem macro, MacroAction action)", ev);
            Assert.Contains("private void ExecuteSingleActionRaw(ref RawHidState raw, MacroItem macro, MacroAction action)", ev);
            Assert.Contains("private void ExecuteRepeatKeyWhileHeld(MacroItem macro, MacroAction action)", ev);
            // The absolute read stays for the volume and mouse actions.
            Assert.Contains("return device.InputState.Axis[action.SourceDeviceAxisIndex] / 65535f;", ev);
            Assert.True(CountOf(ev, "ReadAxisFromDevice(action)") >= 4);
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        /// <summary>A device-free entry (Guid.Empty, the picker's "(Any
        /// device)" form) names the axis on whichever slot device
        /// supplies it, so it directs the pressure like a device-bound
        /// one. The exact-device match skipped it and fell through to the
        /// absolute read, which is backward for a lower-half trigger.</summary>
        [Fact]
        public void DeviceFreeEntry_DirectsThePressureToo()
        {
            var m = WithEntry(MacroAxisTarget.LeftStickY, half: true, invert: true, either: false, device: Guid.Empty);
            var a = Source(1);
            Assert.Equal(1f, R(m, a, 0), 3);
            Assert.Equal(0f, R(m, a, 65535), 3);
            Assert.Equal(0f, R(m, a, 32768), 3);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
