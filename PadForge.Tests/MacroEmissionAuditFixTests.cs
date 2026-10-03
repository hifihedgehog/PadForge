using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the macro emission findings in the 2026-09-15 audit:
    /// what a raw axis reads at rest, what an absent source reads, which keys
    /// carry the extended prefix, and which emissions a gamepad-only peer is
    /// allowed to make.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroEmissionAuditFixTests
    {
        private static string EvaluatorSource()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "PadForge.App", "Common", "Input",
                    "InputManager.Step4b.EvaluateMacros.cs");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("could not locate the evaluator source");
        }

        // C176: the raw mouse read treated a trigger as a centered stick.

        private static float ReadAxisAsMouseRaw(RawHidState raw, MacroAxisTarget target)
        {
            var m = typeof(InputManager).GetMethod("ReadAxisAsMouseRaw",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (float)m.Invoke(null, new object[] { raw, target });
        }

        private static RawHidState RawWithTriggersAtRest()
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Axes[2] = short.MinValue;   // LT rests at the bottom of the word
            raw.Axes[5] = short.MinValue;   // RT likewise
            return raw;
        }

        /// <summary>A trigger nobody is pulling must read as released. It rests
        /// at the bottom of the signed word on this surface, so dividing it as
        /// a centered stick reported FULL NEGATIVE deflection, and mouse move
        /// and scroll are continuous actions, so an untouched trigger drove
        /// the cursor at full rate for as long as the macro ran.</summary>
        [Theory]
        [InlineData(MacroAxisTarget.LeftTrigger)]
        [InlineData(MacroAxisTarget.RightTrigger)]
        public void AnUntouchedTriggerReadsAsReleasedOnTheRawMouseLane(MacroAxisTarget target)
        {
            Assert.Equal(0f, ReadAxisAsMouseRaw(RawWithTriggersAtRest(), target), 3);
        }

        /// <summary>A pulled trigger reads up the 0 to 1 pull scale, the same
        /// contract the Gamepad twin uses.</summary>
        [Fact]
        public void APulledTriggerReadsUpTheZeroToOneScale()
        {
            var raw = RawWithTriggersAtRest();
            raw.Axes[2] = 0;                       // halfway up the signed word
            Assert.Equal(0.5f, ReadAxisAsMouseRaw(raw, MacroAxisTarget.LeftTrigger), 2);
            raw.Axes[2] = short.MaxValue;
            Assert.Equal(1f, ReadAxisAsMouseRaw(raw, MacroAxisTarget.LeftTrigger), 2);
        }

        /// <summary>Positive control: a stick is still bipolar and still rests
        /// centered, so the trigger case did not flatten every channel.</summary>
        [Fact]
        public void AStickIsStillBipolarAndRestsCentered()
        {
            var raw = RawWithTriggersAtRest();
            Assert.Equal(0f, ReadAxisAsMouseRaw(raw, MacroAxisTarget.LeftStickX), 3);
            raw.Axes[0] = short.MaxValue;
            Assert.Equal(1f, ReadAxisAsMouseRaw(raw, MacroAxisTarget.LeftStickX), 2);
            raw.Axes[0] = short.MinValue;
            Assert.Equal(-1f, ReadAxisAsMouseRaw(raw, MacroAxisTarget.LeftStickX), 2);
        }

        // C175: an unavailable source read as full negative deflection.

        /// <summary>Absence and a real zero are the same number on the volume
        /// scale, so mapping an absent source onto the symmetric range turned
        /// "the device is gone" into full negative deflection. The reader now
        /// reports whether anything answered.</summary>
        [Fact]
        public void AnAbsentDeviceSourceRestsInsteadOfDeflecting()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.MouseMove,
                AxisSource = MacroAxisSource.InputDevice,
                SourceDeviceGuid = Guid.NewGuid(),   // no such device is online
                SourceDeviceAxisIndex = 0,
            };

            var m = typeof(InputManager).GetMethod("ReadAxisFromDeviceAsMouse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            float deflection = (float)m.Invoke(im, new object[] { action });

            Assert.Equal(0f, deflection, 4);
        }

        /// <summary>An unbound source rests too, so a half-configured action
        /// does not push the cursor either.</summary>
        [Fact]
        public void AnUnboundSourceRestsAsWell()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.MouseMove,
                AxisSource = MacroAxisSource.InputDevice,
                SourceDeviceAxisIndex = -1,
            };
            var m = typeof(InputManager).GetMethod("ReadAxisFromDeviceAsMouse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.Equal(0f, (float)m.Invoke(im, new object[] { action }), 4);
        }

        // C177: injected keys lost the extended-key prefix.

        /// <summary>The E0 keys need the extended flag or the OS types their
        /// numpad twin: Insert arrives as Numpad 0, Delete as Numpad period,
        /// and the arrows as the numpad digits.</summary>
        [Theory]
        [InlineData(0x2D)]  // Insert
        [InlineData(0x2E)]  // Delete
        [InlineData(0x24)]  // Home
        [InlineData(0x23)]  // End
        [InlineData(0x21)]  // PageUp
        [InlineData(0x22)]  // PageDown
        [InlineData(0x25)]  // Left
        [InlineData(0x26)]  // Up
        [InlineData(0x27)]  // Right
        [InlineData(0x28)]  // Down
        [InlineData(0x5B)]  // LWin
        [InlineData(0xA3)]  // RControl
        [InlineData(0xA5)]  // RAlt
        public void TheExtendedKeysAreRecognizedAsExtended(int vk)
        {
            Assert.True(PadForge.Engine.Common.InputHookManager.IsExtendedKey(vk),
                $"VK 0x{vk:X2} needs the E0 prefix and would type its numpad twin without it");
        }

        /// <summary>Positive control: an ordinary key is not extended, so the
        /// table is a real discriminator.</summary>
        [Theory]
        [InlineData(0x41)]  // A
        [InlineData(0x0D)]  // Return
        [InlineData(0x20)]  // Space
        public void AnOrdinaryKeyIsNotExtended(int vk)
        {
            Assert.False(PadForge.Engine.Common.InputHookManager.IsExtendedKey(vk));
        }

        /// <summary>The macro key emitter consults that table, through the
        /// output key that also sends Numpad Enter's index as VK_RETURN,
        /// rather than carrying a second copy that could drift from it.</summary>
        [Fact]
        public void TheMacroKeyEmitterAppliesTheExtendedFlag()
        {
            string src = EvaluatorSource();
            Assert.Contains("KEYEVENTF_EXTENDEDKEY", src);
            Assert.Contains("InputHookManager.OutputKey(virtualKeyCode);", src);
        }

        // C168: a gamepad-only peer may not drive the desktop cursor.

        /// <summary>A peer paired as gamepad-only may drive gamepad output and
        /// never keyboard, mouse or scroll. Every other emission in the macro
        /// engine consults that flag. The four cursor actions did not, so a
        /// restricted peer could recenter, pin, clamp and warp the desktop
        /// pointer through a macro.</summary>
        [Theory]
        [InlineData("RecenterCursor")]
        [InlineData("TogglePin")]
        [InlineData("MoveCursorTo")]
        public void EveryCursorActionHonorsTheGamepadOnlyRestriction(string call)
        {
            string src = EvaluatorSource();
            // Both evaluator twins carry the arm, and each must be guarded.
            int arms = System.Text.RegularExpressions.Regex.Matches(
                src, @"CursorControlService\.Active\?\." + call).Count;
            Assert.Equal(2, arms);

            int guarded = System.Text.RegularExpressions.Regex.Matches(
                src,
                @"if \(!_currentMacroSlotRestricted\)\s*\r?\n\s*CursorControlService\.Active\?\." + call).Count;
            Assert.Equal(arms, guarded);
        }

        /// <summary>The region clamp is the fourth cursor action and carries the
        /// same restriction. It reads the action's latch direction now, so its
        /// arm is a block rather than one call, and the restriction guards the
        /// whole block. No clamp write may sit outside one.</summary>
        [Fact]
        public void TheRegionClampHonorsTheGamepadOnlyRestriction()
        {
            string src = EvaluatorSource();

            // Both evaluator twins carry the arm.
            var arms = System.Text.RegularExpressions.Regex.Matches(
                src, @"case MacroActionType\.MouseLimitRegion:\s*\r?\n\s*\{");
            Assert.Equal(2, arms.Count);

            // Every clamp write lives inside a restriction-guarded block.
            int writes = System.Text.RegularExpressions.Regex.Matches(
                src, @"cursor\?\.(ToggleClamp|SetClamp)\(").Count;
            Assert.Equal(4, writes);
            Assert.DoesNotContain("CursorControlService.Active?.ToggleClamp(", src);
            Assert.DoesNotContain("CursorControlService.Active?.SetClamp(", src);

            foreach (System.Text.RegularExpressions.Match arm in arms)
            {
                string body = src.Substring(arm.Index, System.Math.Min(1200, src.Length - arm.Index));
                int guard = body.IndexOf("if (!_currentMacroSlotRestricted)", System.StringComparison.Ordinal);
                int firstWrite = body.IndexOf("cursor?.", System.StringComparison.Ordinal);
                Assert.True(guard > 0 && firstWrite > guard,
                    "a region clamp write reaches the cursor outside the restriction guard");
            }
        }

        /// <summary>The flag really is the engine's chokepoint, so the guard
        /// above is the established rule and not an invention.</summary>
        [Fact]
        public void TheKeystrokeEmitterAlreadyHonorsTheSameFlag()
        {
            string src = EvaluatorSource();
            Assert.Contains("if (_currentMacroSlotRestricted) return; // gamepad-only peer: no keystrokes", src);
        }
    }
}
