using System;
using System.Runtime.InteropServices;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 4b: EvaluateMacros
        //  After Step 4 (CombineOutputStates) merges all devices into a single
        //  Gamepad per slot, this step evaluates macro trigger conditions and
        //  injects macro actions into the Gamepad state.
        //
        //  The macro list per slot is provided by InputService via a snapshot
        //  array that is refreshed at 30Hz on the UI thread. The engine reads
        //  the reference atomically each cycle.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Per-slot macro snapshot arrays. Set by InputService at 30Hz.
        /// Each element is a snapshot of MacroItem[] for that slot (0–3).
        /// Null means no macros for that slot.
        /// </summary>
        public MacroItem[][] MacroSnapshots { get; } = new MacroItem[MaxPads][];

        /// <summary>
        /// Step 4b: Evaluate macros for all pad slots.
        /// Called after CombineOutputStates and before VirtualDevices.
        /// </summary>
        private void EvaluateMacros()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                var macros = MacroSnapshots[i];
                if (macros == null || macros.Length == 0)
                    continue;

                try
                {
                    if (SlotVJoyIsCustom[i])
                        EvaluateSlotMacrosCustomVJoy(ref CombinedVJoyRawStates[i], macros);
                    else
                        EvaluateSlotMacros(ref CombinedOutputStates[i], macros);
                }
                catch (Exception ex)
                {
                    RaiseError($"Macro error on pad {i}", ex);
                }
            }
        }

        /// <summary>
        /// Evaluates all macros for a single pad slot.
        /// Instance method to allow raw button lookups via FindOnlineDeviceByInstanceGuid.
        /// </summary>
        private void EvaluateSlotMacros(ref Gamepad gp, MacroItem[] macros)
        {
            for (int m = 0; m < macros.Length; m++)
            {
                var macro = macros[m];
                if (macro == null || !macro.IsEnabled)
                    continue;

                // Skip macros with no trigger configured.
                if (!macro.UsesRawTrigger && macro.TriggerButtons == 0)
                    continue;

                // Check trigger condition based on trigger type.
                bool triggerActive;
                if (macro.UsesRawTrigger)
                    triggerActive = CheckRawButtonTrigger(macro);
                else
                    triggerActive = (gp.Buttons & macro.TriggerButtons) == macro.TriggerButtons;

                bool wasTriggerActive = macro.WasTriggerActive;
                macro.WasTriggerActive = triggerActive;

                // Determine if we should start execution based on trigger mode.
                bool shouldStart = false;
                switch (macro.TriggerMode)
                {
                    case MacroTriggerMode.OnPress:
                        shouldStart = triggerActive && !wasTriggerActive;
                        break;
                    case MacroTriggerMode.OnRelease:
                        shouldStart = !triggerActive && wasTriggerActive;
                        break;
                    case MacroTriggerMode.WhileHeld:
                        shouldStart = triggerActive;
                        break;
                }

                // Start new execution if triggered and not already executing.
                if (shouldStart && !macro.IsExecuting)
                {
                    macro.IsExecuting = true;
                    macro.CurrentActionIndex = 0;
                    macro.ActionStartTime = DateTime.UtcNow;
                    macro.RemainingRepeats = macro.RepeatMode == MacroRepeatMode.FixedCount
                        ? macro.RepeatCount : 1;
                }

                // For WhileHeld + UntilRelease: stop when trigger is released.
                if (macro.IsExecuting &&
                    macro.RepeatMode == MacroRepeatMode.UntilRelease &&
                    !triggerActive)
                {
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                }

                // Execute current action if macro is running.
                if (macro.IsExecuting && macro.Actions.Count > 0)
                {
                    ExecuteMacroActions(ref gp, macro);
                }

                // Consume trigger buttons if configured (only for Xbox bitmask triggers;
                // raw device buttons aren't part of the combined Gamepad state).
                if (macro.ConsumeTriggerButtons && triggerActive && macro.IsExecuting
                    && !macro.UsesRawTrigger)
                {
                    gp.Buttons &= (ushort)~macro.TriggerButtons;
                }
            }
        }

        /// <summary>
        /// Checks whether all raw button indices specified by the macro's trigger
        /// are currently pressed on the target device.
        /// </summary>
        private bool CheckRawButtonTrigger(MacroItem macro)
        {
            var ud = FindOnlineDeviceByInstanceGuid(macro.TriggerDeviceGuid);
            if (ud == null || !ud.IsOnline || ud.InputState == null)
                return false;

            var buttons = ud.InputState.Buttons;
            var rawIndices = macro.TriggerRawButtons;
            for (int i = 0; i < rawIndices.Length; i++)
            {
                int idx = rawIndices[i];
                if (idx < 0 || idx >= buttons.Length || !buttons[idx])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Advances and executes the macro's action sequence.
        /// </summary>
        private static void ExecuteMacroActions(ref Gamepad gp, MacroItem macro)
        {
            if (macro.CurrentActionIndex >= macro.Actions.Count)
            {
                // Sequence complete — handle repeat.
                macro.RemainingRepeats--;
                if (macro.RemainingRepeats > 0 ||
                    macro.RepeatMode == MacroRepeatMode.UntilRelease)
                {
                    // Restart sequence after repeat delay.
                    double elapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;
                    if (elapsed >= macro.RepeatDelayMs)
                    {
                        macro.CurrentActionIndex = 0;
                        macro.ActionStartTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    // Done.
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                }
                return;
            }

            var action = macro.Actions[macro.CurrentActionIndex];
            double actionElapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;

            switch (action.Type)
            {
                case MacroActionType.ButtonPress:
                    // OR button flags into the gamepad for the specified duration.
                    gp.Buttons |= action.ButtonFlags;
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.ButtonRelease:
                    // AND-NOT: clear the specified button flags.
                    gp.Buttons &= (ushort)~action.ButtonFlags;
                    AdvanceAction(macro);
                    break;

                case MacroActionType.KeyPress:
                {
                    // Multi-key: press all keys in forward order, release in reverse.
                    var keyCodes = action.ParsedKeyCodes;
                    if (keyCodes.Length == 0) { AdvanceAction(macro); break; }
                    if (actionElapsed < 1)
                    {
                        for (int k = 0; k < keyCodes.Length; k++)
                            SendKeyInput((ushort)keyCodes[k], keyUp: false);
                    }
                    if (actionElapsed >= action.DurationMs)
                    {
                        for (int k = keyCodes.Length - 1; k >= 0; k--)
                            SendKeyInput((ushort)keyCodes[k], keyUp: true);
                        AdvanceAction(macro);
                    }
                    break;
                }

                case MacroActionType.KeyRelease:
                {
                    var keyCodes = action.ParsedKeyCodes;
                    for (int k = keyCodes.Length - 1; k >= 0; k--)
                        SendKeyInput((ushort)keyCodes[k], keyUp: true);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.Delay:
                    // Wait for the specified duration.
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.AxisSet:
                    // Set the specified axis to the given value.
                    ApplyAxisAction(ref gp, action);
                    AdvanceAction(macro);
                    break;
            }
        }

        /// <summary>
        /// Advances to the next action in the macro sequence.
        /// </summary>
        private static void AdvanceAction(MacroItem macro)
        {
            macro.CurrentActionIndex++;
            macro.ActionStartTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Applies an AxisSet action to the gamepad.
        /// </summary>
        private static void ApplyAxisAction(ref Gamepad gp, MacroAction action)
        {
            switch (action.AxisTarget)
            {
                case MacroAxisTarget.LeftStickX:
                    gp.ThumbLX = action.AxisValue;
                    break;
                case MacroAxisTarget.LeftStickY:
                    gp.ThumbLY = action.AxisValue;
                    break;
                case MacroAxisTarget.RightStickX:
                    gp.ThumbRX = action.AxisValue;
                    break;
                case MacroAxisTarget.RightStickY:
                    gp.ThumbRY = action.AxisValue;
                    break;
                case MacroAxisTarget.LeftTrigger:
                    gp.LeftTrigger = (byte)Math.Clamp((int)action.AxisValue, 0, 255);
                    break;
                case MacroAxisTarget.RightTrigger:
                    gp.RightTrigger = (byte)Math.Clamp((int)action.AxisValue, 0, 255);
                    break;
            }
        }

        // ─────────────────────────────────────────────
        //  Custom vJoy macro evaluation
        //  Mirrors EvaluateSlotMacros but operates on VJoyRawState
        //  with uint[] button words instead of ushort Gamepad.Buttons.
        // ─────────────────────────────────────────────

        private void EvaluateSlotMacrosCustomVJoy(ref VJoyRawState raw, MacroItem[] macros)
        {
            for (int m = 0; m < macros.Length; m++)
            {
                var macro = macros[m];
                if (macro == null || !macro.IsEnabled)
                    continue;

                // Skip macros with no trigger configured.
                if (!macro.UsesRawTrigger && !macro.UsesCustomTrigger && macro.TriggerButtons == 0)
                    continue;

                // Check trigger condition.
                bool triggerActive;
                if (macro.UsesRawTrigger)
                    triggerActive = CheckRawButtonTrigger(macro);
                else if (macro.UsesCustomTrigger)
                    triggerActive = CheckCustomButtonTrigger(raw, macro);
                else
                    triggerActive = false; // Xbox bitmask triggers don't apply to custom vJoy

                bool wasTriggerActive = macro.WasTriggerActive;
                macro.WasTriggerActive = triggerActive;

                bool shouldStart = false;
                switch (macro.TriggerMode)
                {
                    case MacroTriggerMode.OnPress:
                        shouldStart = triggerActive && !wasTriggerActive;
                        break;
                    case MacroTriggerMode.OnRelease:
                        shouldStart = !triggerActive && wasTriggerActive;
                        break;
                    case MacroTriggerMode.WhileHeld:
                        shouldStart = triggerActive;
                        break;
                }

                if (shouldStart && !macro.IsExecuting)
                {
                    macro.IsExecuting = true;
                    macro.CurrentActionIndex = 0;
                    macro.ActionStartTime = DateTime.UtcNow;
                    macro.RemainingRepeats = macro.RepeatMode == MacroRepeatMode.FixedCount
                        ? macro.RepeatCount : 1;
                }

                if (macro.IsExecuting &&
                    macro.RepeatMode == MacroRepeatMode.UntilRelease &&
                    !triggerActive)
                {
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                }

                if (macro.IsExecuting && macro.Actions.Count > 0)
                    ExecuteMacroActionsCustomVJoy(ref raw, macro);

                // Consume trigger buttons.
                if (macro.ConsumeTriggerButtons && triggerActive && macro.IsExecuting
                    && macro.UsesCustomTrigger)
                {
                    var tw = macro.TriggerCustomButtonWords;
                    if (raw.Buttons != null)
                        for (int w = 0; w < raw.Buttons.Length && w < tw.Length; w++)
                            raw.Buttons[w] &= ~tw[w];
                }
            }
        }

        /// <summary>
        /// Checks whether all custom trigger buttons are currently pressed in the raw state.
        /// </summary>
        private static bool CheckCustomButtonTrigger(in VJoyRawState raw, MacroItem macro)
        {
            var tw = macro.TriggerCustomButtonWords;
            if (raw.Buttons == null) return false;
            bool anyTriggerBit = false;
            for (int w = 0; w < tw.Length; w++)
            {
                if (tw[w] == 0) continue;
                anyTriggerBit = true;
                if (w >= raw.Buttons.Length) return false;
                if ((raw.Buttons[w] & tw[w]) != tw[w]) return false;
            }
            return anyTriggerBit;
        }

        /// <summary>
        /// Executes macro actions against a VJoyRawState (custom vJoy button words).
        /// </summary>
        private static void ExecuteMacroActionsCustomVJoy(ref VJoyRawState raw, MacroItem macro)
        {
            if (macro.CurrentActionIndex >= macro.Actions.Count)
            {
                macro.RemainingRepeats--;
                if (macro.RemainingRepeats > 0 ||
                    macro.RepeatMode == MacroRepeatMode.UntilRelease)
                {
                    double elapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;
                    if (elapsed >= macro.RepeatDelayMs)
                    {
                        macro.CurrentActionIndex = 0;
                        macro.ActionStartTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                }
                return;
            }

            var action = macro.Actions[macro.CurrentActionIndex];
            double actionElapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;

            switch (action.Type)
            {
                case MacroActionType.ButtonPress:
                    // OR custom button words into the raw state.
                    if (raw.Buttons != null)
                    {
                        var cw = action.CustomButtonWords;
                        for (int w = 0; w < raw.Buttons.Length && w < cw.Length; w++)
                            raw.Buttons[w] |= cw[w];
                    }
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.ButtonRelease:
                    // AND-NOT: clear custom button words from the raw state.
                    if (raw.Buttons != null)
                    {
                        var cw = action.CustomButtonWords;
                        for (int w = 0; w < raw.Buttons.Length && w < cw.Length; w++)
                            raw.Buttons[w] &= ~cw[w];
                    }
                    AdvanceAction(macro);
                    break;

                case MacroActionType.KeyPress:
                {
                    var keyCodes = action.ParsedKeyCodes;
                    if (keyCodes.Length == 0) { AdvanceAction(macro); break; }
                    if (actionElapsed < 1)
                    {
                        for (int k = 0; k < keyCodes.Length; k++)
                            SendKeyInput((ushort)keyCodes[k], keyUp: false);
                    }
                    if (actionElapsed >= action.DurationMs)
                    {
                        for (int k = keyCodes.Length - 1; k >= 0; k--)
                            SendKeyInput((ushort)keyCodes[k], keyUp: true);
                        AdvanceAction(macro);
                    }
                    break;
                }

                case MacroActionType.KeyRelease:
                {
                    var keyCodes = action.ParsedKeyCodes;
                    for (int k = keyCodes.Length - 1; k >= 0; k--)
                        SendKeyInput((ushort)keyCodes[k], keyUp: true);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.Delay:
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.AxisSet:
                    // Set axis on the raw state.
                    if (raw.Axes != null)
                        ApplyAxisActionRaw(ref raw, action);
                    AdvanceAction(macro);
                    break;
            }
        }

        /// <summary>Applies an AxisSet action to a VJoyRawState.</summary>
        private static void ApplyAxisActionRaw(ref VJoyRawState raw, MacroAction action)
        {
            int axisIndex = action.AxisTarget switch
            {
                MacroAxisTarget.LeftStickX => 0,
                MacroAxisTarget.LeftStickY => 1,
                MacroAxisTarget.RightStickX => 3,
                MacroAxisTarget.RightStickY => 4,
                MacroAxisTarget.LeftTrigger => 2,
                MacroAxisTarget.RightTrigger => 5,
                _ => -1
            };
            if (axisIndex >= 0 && axisIndex < raw.Axes.Length)
                raw.Axes[axisIndex] = action.AxisValue;
        }

        // ─────────────────────────────────────────────
        //  Win32 SendInput for keyboard macro actions
        // ─────────────────────────────────────────────

        private static void SendKeyInput(ushort virtualKeyCode, bool keyUp)
        {
            ushort scanCode = (ushort)MapVirtualKey(virtualKeyCode, MAPVK_VK_TO_VSC);

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKeyCode,
                        wScan = scanCode,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        // ── P/Invoke declarations ──

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MAPVK_VK_TO_VSC = 0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // The union must include all three input types so its size matches
        // the Win32 INPUT union (the largest member is MOUSEINPUT at 32 bytes
        // on 64-bit). Without this, Marshal.SizeOf<INPUT>() returns too small
        // a value and SendInput silently fails.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }
    }
}
