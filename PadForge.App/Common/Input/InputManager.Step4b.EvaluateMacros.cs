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
        //  After Step 4 (CombineXiStates) merges all devices into a single
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
        /// Called after CombineXiStates and before VirtualDevices.
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
                    EvaluateSlotMacros(ref CombinedXiStates[i], macros);
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
