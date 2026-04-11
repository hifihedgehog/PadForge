using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

// ─────────────────────────────────────────────
//  Windows Core Audio COM interfaces for system volume control.
//  Used by the SystemVolume macro action type.
// ─────────────────────────────────────────────

namespace PadForge.Common.Input
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate([In] ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
    }

    // ─────────────────────────────────────────────
    //  Per-app audio session COM interfaces for AppVolume macro action.
    // ─────────────────────────────────────────────

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags, out IntPtr simpleVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionIndex, out IntPtr session);
    }

    // Flat layout — no inheritance. COM interop with InterfaceIsIUnknown + C#
    // interface inheritance + 'new' redeclarations doubles vtable entries,
    // causing method calls to hit wrong slots.
    [ComImport, Guid("BFB7B31D-7D78-4AF3-B235-E591A62B4B28"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // IAudioSessionControl methods (vtable slots 0–8).
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr notify);
        int UnregisterAudioSessionNotification(IntPtr notify);

        // IAudioSessionControl2 methods (vtable slots 9–13).
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionId);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceId);
        int GetProcessId(out uint processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid eventContext);
        int GetMute(out bool mute);
    }

    /// <summary>
    /// Enumerates process names that currently have active audio sessions
    /// on the default render device. Used by the macro editor UI to
    /// populate the AppVolume process name suggestions.
    /// </summary>
    internal static class AudioSessionHelper
    {
        // Direct vtable call delegate for IAudioSessionControl2::GetProcessId.
        // Slot 14 = IUnknown(3) + IAudioSessionControl(9) + GetSessionIdentifier(1) + GetSessionInstanceIdentifier(1) = 14.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetProcessIdFn(IntPtr @this, out uint processId);

        // Direct vtable call delegate for ISimpleAudioVolume::SetMasterVolume.
        // Slot 3 = IUnknown(3) + SetMasterVolume(0) = slot 3.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetMasterVolumeFn(IntPtr @this, float level, ref Guid eventContext);

        private static readonly Guid IID_SimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

        /// <summary>
        /// Calls GetProcessId directly through the COM vtable at slot 14,
        /// bypassing QueryInterface which fails from elevated processes.
        /// </summary>
        internal static bool TryGetSessionProcessId(IntPtr pSession, out uint pid)
        {
            pid = 0;
            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(pSession);
                IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
                var fn = Marshal.GetDelegateForFunctionPointer<GetProcessIdFn>(fnPtr);
                int hr = fn(pSession, out pid);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets volume on a session via direct vtable call to ISimpleAudioVolume::SetMasterVolume,
        /// obtained through QI for ISimpleAudioVolume (which IS supported from elevated processes).
        /// </summary>
        internal static bool TrySetSessionVolume(IntPtr pSession, float volume)
        {
            var iidVol = IID_SimpleAudioVolume;
            int hr = Marshal.QueryInterface(pSession, ref iidVol, out IntPtr pVol);
            if (hr != 0) return false;
            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(pVol);
                IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size); // slot 3 = SetMasterVolume
                var fn = Marshal.GetDelegateForFunctionPointer<SetMasterVolumeFn>(fnPtr);
                var empty = Guid.Empty;
                fn(pVol, volume, ref empty);
                return true;
            }
            catch { return false; }
            finally { Marshal.Release(pVol); }
        }

        public static List<string> GetActiveAudioProcessNames()
        {
            var names = new List<string>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
                enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
                var iid = typeof(IAudioSessionManager2).GUID;
                device.Activate(ref iid, 1, IntPtr.Zero, out var iface);
                var mgr = (IAudioSessionManager2)iface;

                mgr.GetSessionEnumerator(out var sessionEnum);
                sessionEnum.GetCount(out int count);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < count; i++)
                {
                    IntPtr pSession = IntPtr.Zero;
                    try
                    {
                        sessionEnum.GetSession(i, out pSession);
                        if (pSession == IntPtr.Zero) continue;

                        if (!TryGetSessionProcessId(pSession, out uint pid) || pid == 0)
                            continue;

                        using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                        if (seen.Add(proc.ProcessName))
                            names.Add(proc.ProcessName);
                    }
                    catch { }
                    finally
                    {
                        if (pSession != IntPtr.Zero)
                            Marshal.Release(pSession);
                    }
                }
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }
}

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
        /// Each element is a snapshot of MacroItem[] for that slot (0–15).
        /// Null means no macros for that slot.
        /// </summary>
        public MacroItem[][] MacroSnapshots { get; } = new MacroItem[MaxPads][];

        /// <summary>
        /// Step 4b: Evaluate macros for all pad slots.
        /// Called after CombineOutputStates and before VirtualDevices.
        /// </summary>
        private void EvaluateMacros()
        {
            EvaluateGlobalMacros();

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

                // Skip macros with no trigger configured (unless Always mode).
                bool hasButtons = macro.UsesRawTrigger || macro.TriggerButtons != 0;
                if (macro.TriggerMode != MacroTriggerMode.Always &&
                    !macro.UsesAxisTrigger && !macro.UsesPovTrigger && !hasButtons)
                    continue;

                // Determine trigger state — buttons, POVs, AND axes must all be active together.
                bool triggerActive;
                if (macro.TriggerMode == MacroTriggerMode.Always)
                    triggerActive = true;
                else
                {
                    bool buttonOk = true;
                    bool povOk = true;
                    bool axisOk = true;

                    if (hasButtons)
                    {
                        if (macro.UsesRawTrigger)
                            buttonOk = CheckRawButtonTrigger(macro);
                        else
                            buttonOk = (gp.Buttons & macro.TriggerButtons) == macro.TriggerButtons;
                    }
                    if (macro.UsesPovTrigger)
                        povOk = CheckRawPovTrigger(macro);
                    if (macro.UsesAxisTrigger)
                    {
                        float threshold = macro.TriggerAxisThreshold / 100f;
                        for (int ai = 0; ai < macro.TriggerAxisTargets.Length; ai++)
                        {
                            var axTarget = macro.TriggerAxisTargets[ai];
                            var dir = macro.GetAxisDirection(ai);
                            float val = ReadAxisAsVolume(in gp, axTarget); // 0→1

                            if (dir == MacroAxisDirection.Positive)
                            {
                                // Only fire when axis is in positive half (0.5→1 range).
                                if (val < 0.5f + threshold * 0.5f)
                                { axisOk = false; break; }
                            }
                            else if (dir == MacroAxisDirection.Negative)
                            {
                                // Only fire when axis is in negative half (0→0.5 range).
                                if (val > 0.5f - threshold * 0.5f)
                                { axisOk = false; break; }
                            }
                            else
                            {
                                // Any direction — existing behavior.
                                if (val < threshold)
                                { axisOk = false; break; }
                            }
                        }
                    }

                    triggerActive = buttonOk && povOk && axisOk;
                }

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
                    case MacroTriggerMode.Always:
                        shouldStart = !macro.IsExecuting;
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
                    ResetMouseAccumulators(macro);
                }

                // For WhileHeld + UntilRelease: stop when trigger is released.
                // Always mode never stops via trigger release.
                if (macro.IsExecuting &&
                    macro.TriggerMode != MacroTriggerMode.Always &&
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
        /// Checks whether all POV triggers are active on the target device.
        /// Each entry is "povIndex:centidegrees". The POV must be in the same
        /// 45-degree sector as the stored direction.
        /// </summary>
        private bool CheckRawPovTrigger(MacroItem macro)
        {
            var ud = FindOnlineDeviceByInstanceGuid(macro.TriggerDeviceGuid);
            if (ud == null || !ud.IsOnline || ud.InputState == null)
                return false;

            var povs = ud.InputState.Povs;
            if (povs == null) return false;

            foreach (var entry in macro.TriggerPovs)
            {
                if (!MacroItem.ParsePovTrigger(entry, out int idx, out int targetCd))
                    return false;
                if (idx < 0 || idx >= povs.Length || povs[idx] < 0)
                    return false;
                // Check same 45-degree sector (±2250 centidegrees).
                int diff = Math.Abs(povs[idx] - targetCd);
                if (diff > 18000) diff = 36000 - diff;
                if (diff > 2250) return false;
            }
            return true;
        }

        /// <summary>Returns true for action types that run every frame without advancing.</summary>
        private static bool IsContinuousAction(MacroActionType type) =>
            type is MacroActionType.SystemVolume or MacroActionType.AppVolume
                 or MacroActionType.MouseMove or MacroActionType.MouseScroll;

        /// <summary>
        /// Advances and executes the macro's action sequence.
        /// Continuous actions (MouseMove, MouseScroll, SystemVolume, AppVolume) all run
        /// every frame regardless of position — this allows e.g. MouseMove X + MouseMove Y
        /// in the same macro to both execute simultaneously.
        /// </summary>
        private void ExecuteMacroActions(ref Gamepad gp, MacroItem macro)
        {
            // 1. Always run ALL continuous actions every frame.
            for (int i = 0; i < macro.Actions.Count; i++)
            {
                var ca = macro.Actions[i];
                if (!IsContinuousAction(ca.Type)) continue;
                ExecuteSingleAction(ref gp, ca);
            }

            // 2. Process the current sequential action (skip over continuous ones).
            sequenceRestart:
            while (macro.CurrentActionIndex < macro.Actions.Count)
            {
                var action = macro.Actions[macro.CurrentActionIndex];
                if (IsContinuousAction(action.Type))
                {
                    // Already handled above — skip to next.
                    AdvanceAction(macro);
                    continue;
                }
                // Execute the sequential action.
                ExecuteSequentialAction(ref gp, macro, action);
                return;
            }

            // 3. Sequence complete — handle repeat or stop.
            // If all actions are continuous, we stay "executing" and keep running them.
            bool allContinuous = true;
            for (int i = 0; i < macro.Actions.Count; i++)
            {
                if (!IsContinuousAction(macro.Actions[i].Type))
                { allContinuous = false; break; }
            }
            if (allContinuous) return; // Keep running — continuous actions handled above.

            macro.RemainingRepeats--;
            if (macro.RemainingRepeats > 0 ||
                macro.RepeatMode == MacroRepeatMode.UntilRelease)
            {
                double elapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;
                if (elapsed >= macro.RepeatDelayMs)
                {
                    macro.CurrentActionIndex = 0;
                    macro.ActionStartTime = DateTime.UtcNow;
                    goto sequenceRestart; // Re-enter to execute first action this frame
                }
            }
            else
            {
                macro.IsExecuting = false;
                macro.CurrentActionIndex = 0;
            }
        }

        /// <summary>Executes a single continuous action (no advance logic).</summary>
        private void ExecuteSingleAction(ref Gamepad gp, MacroAction action)
        {
            bool useDevice = action.AxisSource == MacroAxisSource.InputDevice;
            switch (action.Type)
            {
                case MacroActionType.SystemVolume:
                {
                    float vol = useDevice ? ReadAxisFromDevice(action)
                        : ReadAxisAsVolume(in gp, action.AxisTarget);
                    if (action.InvertAxis) vol = 1f - vol;
                    SetSystemVolume(vol * (action.VolumeLimit / 100f), action.ShowVolumeOsd);
                    break;
                }
                case MacroActionType.AppVolume:
                    if (!string.IsNullOrEmpty(action.ProcessName))
                    {
                        float vol = useDevice ? ReadAxisFromDevice(action)
                            : ReadAxisAsVolume(in gp, action.AxisTarget);
                        if (action.InvertAxis) vol = 1f - vol;
                        SetAppVolume(vol * (action.VolumeLimit / 100f), action.ProcessName);
                    }
                    break;
                case MacroActionType.MouseMove:
                {
                    float deflection = useDevice ? ReadAxisFromDeviceAsMouse(action)
                        : ReadAxisAsMouse(in gp, action.AxisTarget);
                    if (action.InvertAxis) deflection = -deflection;
                    action.MouseAccumulator += deflection * action.MouseSensitivity;
                    int delta = (int)action.MouseAccumulator;
                    action.MouseAccumulator -= delta;
                    bool isY = useDevice
                        ? false // Device axis doesn't map to X/Y — user controls direction via axis index
                        : action.AxisTarget is MacroAxisTarget.LeftStickY or MacroAxisTarget.RightStickY;
                    SendMouseMoveInput(isY ? 0 : delta, isY ? -delta : 0);
                    break;
                }
                case MacroActionType.MouseScroll:
                {
                    float deflection = useDevice ? ReadAxisFromDeviceAsMouse(action)
                        : ReadAxisAsMouse(in gp, action.AxisTarget);
                    if (action.InvertAxis) deflection = -deflection;
                    action.MouseAccumulator += deflection * action.MouseSensitivity;
                    int delta = (int)action.MouseAccumulator;
                    action.MouseAccumulator -= delta;
                    if (delta != 0)
                        SendMouseScrollInput(delta * 120);
                    break;
                }
            }
        }

        /// <summary>Executes a sequential (non-continuous) action with advance logic.</summary>
        private void ExecuteSequentialAction(ref Gamepad gp, MacroItem macro, MacroAction action)
        {
            double actionElapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;

            switch (action.Type)
            {
                case MacroActionType.ButtonPress:
                    gp.Buttons |= action.ButtonFlags;
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.ButtonRelease:
                    gp.Buttons &= (ushort)~action.ButtonFlags;
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
                    ApplyAxisAction(ref gp, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.MouseButtonPress:
                    if (actionElapsed < 1)
                        SendMouseButtonInput(action.MouseButton, down: true);
                    if (actionElapsed >= action.DurationMs)
                    {
                        SendMouseButtonInput(action.MouseButton, down: false);
                        AdvanceAction(macro);
                    }
                    break;

                case MacroActionType.MouseButtonRelease:
                    SendMouseButtonInput(action.MouseButton, down: false);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleTouchpadOverlay:
                    ToggleTouchpadOverlayRequested = true;
                    AdvanceAction(macro);
                    break;
            }
        }

        /// <summary>Resets mouse accumulators on all actions when a macro starts/restarts.</summary>
        private static void ResetMouseAccumulators(MacroItem macro)
        {
            foreach (var action in macro.Actions)
                action.MouseAccumulator = 0f;
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
                    gp.LeftTrigger = (ushort)Math.Clamp((int)action.AxisValue, 0, 65535);
                    break;
                case MacroAxisTarget.RightTrigger:
                    gp.RightTrigger = (ushort)Math.Clamp((int)action.AxisValue, 0, 65535);
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

                // Skip macros with no trigger configured (unless Always mode).
                bool hasButtons = macro.UsesRawTrigger || macro.UsesCustomTrigger || macro.TriggerButtons != 0;
                if (macro.TriggerMode != MacroTriggerMode.Always &&
                    !macro.UsesAxisTrigger && !macro.UsesPovTrigger && !hasButtons)
                    continue;

                // Check trigger condition — buttons, POVs, AND axes must all be active together.
                bool triggerActive;
                if (macro.TriggerMode == MacroTriggerMode.Always)
                    triggerActive = true;
                else
                {
                    bool buttonOk = true;
                    bool povOk = true;
                    bool axisOk = true;

                    if (hasButtons)
                    {
                        if (macro.UsesRawTrigger)
                            buttonOk = CheckRawButtonTrigger(macro);
                        else if (macro.UsesCustomTrigger)
                            buttonOk = CheckCustomButtonTrigger(raw, macro);
                        else
                            buttonOk = false; // Xbox bitmask triggers don't apply to custom vJoy
                    }
                    if (macro.UsesPovTrigger)
                        povOk = CheckRawPovTrigger(macro);
                    if (macro.UsesAxisTrigger)
                    {
                        float threshold = macro.TriggerAxisThreshold / 100f;
                        foreach (var axTarget in macro.TriggerAxisTargets)
                        {
                            if (ReadAxisAsVolumeRaw(in raw, axTarget) < threshold)
                            { axisOk = false; break; }
                        }
                    }

                    triggerActive = buttonOk && povOk && axisOk;
                }

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
                    case MacroTriggerMode.Always:
                        shouldStart = !macro.IsExecuting;
                        break;
                }

                if (shouldStart && !macro.IsExecuting)
                {
                    macro.IsExecuting = true;
                    macro.CurrentActionIndex = 0;
                    macro.ActionStartTime = DateTime.UtcNow;
                    macro.RemainingRepeats = macro.RepeatMode == MacroRepeatMode.FixedCount
                        ? macro.RepeatCount : 1;
                    ResetMouseAccumulators(macro);
                }

                // Always mode never stops via trigger release.
                if (macro.IsExecuting &&
                    macro.TriggerMode != MacroTriggerMode.Always &&
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
        /// Same parallel-continuous pattern as ExecuteMacroActions.
        /// </summary>
        private void ExecuteMacroActionsCustomVJoy(ref VJoyRawState raw, MacroItem macro)
        {
            // 1. Always run ALL continuous actions every frame.
            for (int i = 0; i < macro.Actions.Count; i++)
            {
                var ca = macro.Actions[i];
                if (!IsContinuousAction(ca.Type)) continue;
                ExecuteSingleActionRaw(ref raw, ca);
            }

            // 2. Process the current sequential action (skip over continuous ones).
            sequenceRestartRaw:
            while (macro.CurrentActionIndex < macro.Actions.Count)
            {
                var action = macro.Actions[macro.CurrentActionIndex];
                if (IsContinuousAction(action.Type))
                {
                    AdvanceAction(macro);
                    continue;
                }
                ExecuteSequentialActionRaw(ref raw, macro, action);
                return;
            }

            // 3. Sequence complete — handle repeat or stop.
            bool allContinuous = true;
            for (int i = 0; i < macro.Actions.Count; i++)
            {
                if (!IsContinuousAction(macro.Actions[i].Type))
                { allContinuous = false; break; }
            }
            if (allContinuous) return;

            macro.RemainingRepeats--;
            if (macro.RemainingRepeats > 0 ||
                macro.RepeatMode == MacroRepeatMode.UntilRelease)
            {
                double elapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;
                if (elapsed >= macro.RepeatDelayMs)
                {
                    macro.CurrentActionIndex = 0;
                    macro.ActionStartTime = DateTime.UtcNow;
                    goto sequenceRestartRaw; // Re-enter to execute first action this frame
                }
            }
            else
            {
                macro.IsExecuting = false;
                macro.CurrentActionIndex = 0;
            }
        }

        /// <summary>Executes a single continuous action for vJoy raw state.</summary>
        private void ExecuteSingleActionRaw(ref VJoyRawState raw, MacroAction action)
        {
            bool useDevice = action.AxisSource == MacroAxisSource.InputDevice;
            switch (action.Type)
            {
                case MacroActionType.SystemVolume:
                {
                    float vol = useDevice ? ReadAxisFromDevice(action)
                        : ReadAxisAsVolumeRaw(in raw, action.AxisTarget);
                    if (action.InvertAxis) vol = 1f - vol;
                    SetSystemVolume(vol * (action.VolumeLimit / 100f), action.ShowVolumeOsd);
                    break;
                }
                case MacroActionType.AppVolume:
                    if (!string.IsNullOrEmpty(action.ProcessName))
                    {
                        float vol = useDevice ? ReadAxisFromDevice(action)
                            : ReadAxisAsVolumeRaw(in raw, action.AxisTarget);
                        if (action.InvertAxis) vol = 1f - vol;
                        SetAppVolume(vol * (action.VolumeLimit / 100f), action.ProcessName);
                    }
                    break;
                case MacroActionType.MouseMove:
                {
                    float deflection = useDevice ? ReadAxisFromDeviceAsMouse(action)
                        : ReadAxisAsMouseRaw(in raw, action.AxisTarget);
                    if (action.InvertAxis) deflection = -deflection;
                    action.MouseAccumulator += deflection * action.MouseSensitivity;
                    int delta = (int)action.MouseAccumulator;
                    action.MouseAccumulator -= delta;
                    bool isY = useDevice
                        ? false
                        : action.AxisTarget is MacroAxisTarget.LeftStickY or MacroAxisTarget.RightStickY;
                    SendMouseMoveInput(isY ? 0 : delta, isY ? -delta : 0);
                    break;
                }
                case MacroActionType.MouseScroll:
                {
                    float deflection = useDevice ? ReadAxisFromDeviceAsMouse(action)
                        : ReadAxisAsMouseRaw(in raw, action.AxisTarget);
                    if (action.InvertAxis) deflection = -deflection;
                    action.MouseAccumulator += deflection * action.MouseSensitivity;
                    int delta = (int)action.MouseAccumulator;
                    action.MouseAccumulator -= delta;
                    if (delta != 0)
                        SendMouseScrollInput(delta * 120);
                    break;
                }
            }
        }

        /// <summary>Executes a sequential action for vJoy raw state.</summary>
        private void ExecuteSequentialActionRaw(ref VJoyRawState raw, MacroItem macro, MacroAction action)
        {
            double actionElapsed = (DateTime.UtcNow - macro.ActionStartTime).TotalMilliseconds;

            switch (action.Type)
            {
                case MacroActionType.ButtonPress:
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
                    if (raw.Axes != null)
                        ApplyAxisActionRaw(ref raw, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.MouseButtonPress:
                    if (actionElapsed < 1)
                        SendMouseButtonInput(action.MouseButton, down: true);
                    if (actionElapsed >= action.DurationMs)
                    {
                        SendMouseButtonInput(action.MouseButton, down: false);
                        AdvanceAction(macro);
                    }
                    break;

                case MacroActionType.MouseButtonRelease:
                    SendMouseButtonInput(action.MouseButton, down: false);
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
        //  System volume control for SystemVolume macro action
        // ─────────────────────────────────────────────

        private IAudioEndpointVolume _audioEndpointVolume;
        private bool _audioEndpointFailed;
        private float _lastSetVolume = -1f;
        private DateTime _lastOsdTriggerTime;

        private const ushort VK_VOLUME_UP = 0xAF;
        private const ushort VK_VOLUME_DOWN = 0xAE;

        /// <summary>
        /// Sets the Windows system master volume. Uses change detection to avoid
        /// redundant COM calls every polling cycle. Triggers the modern volume
        /// flyout OSD via a net-zero volume key pair, rate-limited to ~5 Hz.
        /// </summary>
        private void SetSystemVolume(float volume, bool showOsd = true)
        {
            volume = Math.Clamp(volume, 0f, 1f);

            // Skip if the volume hasn't changed (within ~0.4% tolerance = 1/256).
            // After an OSD trigger, keep correcting for 150ms to counteract
            // the async VK_VOLUME key events that land after the COM correction.
            bool inCorrectionWindow = (DateTime.UtcNow - _lastOsdTriggerTime).TotalMilliseconds < 150;
            if (!inCorrectionWindow && Math.Abs(volume - _lastSetVolume) < 0.004f)
                return;
            _lastSetVolume = volume;

            if (_audioEndpointFailed) return;

            try
            {
                if (_audioEndpointVolume == null)
                {
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
                    enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var iface);
                    _audioEndpointVolume = (IAudioEndpointVolume)iface;
                }

                var emptyGuid = Guid.Empty;
                _audioEndpointVolume.SetMasterVolumeLevelScalar(volume, ref emptyGuid);

                // Trigger the modern Windows volume flyout OSD by sending a
                // net-zero VK_VOLUME_UP + VK_VOLUME_DOWN pair, then immediately
                // re-setting the exact target volume to correct any rounding.
                // Rate-limited to every 200ms (~5 Hz) to avoid input queue spam.
                if (showOsd)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastOsdTriggerTime).TotalMilliseconds >= 200)
                    {
                        SendKeyInput(VK_VOLUME_UP, keyUp: false);
                        SendKeyInput(VK_VOLUME_UP, keyUp: true);
                        SendKeyInput(VK_VOLUME_DOWN, keyUp: false);
                        SendKeyInput(VK_VOLUME_DOWN, keyUp: true);
                        // Re-set exact volume to undo the ±2% from the key events.
                        _audioEndpointVolume.SetMasterVolumeLevelScalar(volume, ref emptyGuid);
                        _lastOsdTriggerTime = now;
                    }
                }
            }
            catch
            {
                _audioEndpointFailed = true;
            }
        }

        // ─────────────────────────────────────────────
        //  Per-app volume control for AppVolume macro action
        // ─────────────────────────────────────────────

        private IAudioSessionManager2 _audioSessionManager;
        private bool _audioSessionFailed;

        /// <summary>
        /// Per-process change-detection: tracks the last volume set for each process name
        /// to avoid redundant COM enumeration every polling cycle.
        /// </summary>
        private readonly Dictionary<string, float> _lastAppVolumes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sets the volume for all audio sessions belonging to the specified process name
        /// in the Windows audio mixer. Enumerates sessions via IAudioSessionManager2.
        /// </summary>
        private void SetAppVolume(float volume, string processName)
        {
            volume = Math.Clamp(volume, 0f, 1f);

            // Change detection per process name.
            if (_lastAppVolumes.TryGetValue(processName, out float last) && Math.Abs(volume - last) < 0.004f)
                return;
            _lastAppVolumes[processName] = volume;

            if (_audioSessionFailed) return;

            try
            {
                if (_audioSessionManager == null)
                {
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
                    enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
                    var iid = typeof(IAudioSessionManager2).GUID;
                    device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var iface);
                    _audioSessionManager = (IAudioSessionManager2)iface;
                }

                _audioSessionManager.GetSessionEnumerator(out var sessionEnum);
                sessionEnum.GetCount(out int count);

                for (int i = 0; i < count; i++)
                {
                    IntPtr pSession = IntPtr.Zero;
                    try
                    {
                        sessionEnum.GetSession(i, out pSession);
                        if (pSession == IntPtr.Zero) continue;

                        // Direct vtable call — QI for IAudioSessionControl2 fails from elevated processes.
                        if (!AudioSessionHelper.TryGetSessionProcessId(pSession, out uint pid) || pid == 0)
                            continue;

                        string exeName;
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            exeName = proc.ProcessName;
                        }
                        catch { continue; }

                        if (!exeName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        AudioSessionHelper.TrySetSessionVolume(pSession, volume);
                    }
                    catch { }
                    finally
                    {
                        if (pSession != IntPtr.Zero)
                            Marshal.Release(pSession);
                    }
                }
            }
            catch
            {
                _audioSessionFailed = true;
            }
        }

        /// <summary>
        /// Reads the current value of a source axis from the Gamepad state
        /// and returns it as a 0.0–1.0 float suitable for volume.
        /// </summary>
        internal static float ReadAxisAsVolume(in Gamepad gp, MacroAxisTarget target)
        {
            return target switch
            {
                // Sticks: -32768..32767 → 0..1
                MacroAxisTarget.LeftStickX => (gp.ThumbLX + 32768f) / 65535f,
                MacroAxisTarget.LeftStickY => (gp.ThumbLY + 32768f) / 65535f,
                MacroAxisTarget.RightStickX => (gp.ThumbRX + 32768f) / 65535f,
                MacroAxisTarget.RightStickY => (gp.ThumbRY + 32768f) / 65535f,
                // Triggers: 0..65535 → 0..1
                MacroAxisTarget.LeftTrigger => gp.LeftTrigger / 65535f,
                MacroAxisTarget.RightTrigger => gp.RightTrigger / 65535f,
                _ => 0f
            };
        }

        /// <summary>
        /// Reads the current value of a source axis from a VJoyRawState
        /// and returns it as a 0.0–1.0 float suitable for volume.
        /// </summary>
        internal static float ReadAxisAsVolumeRaw(in VJoyRawState raw, MacroAxisTarget target)
        {
            int axisIndex = target switch
            {
                MacroAxisTarget.LeftStickX => 0,
                MacroAxisTarget.LeftStickY => 1,
                MacroAxisTarget.RightStickX => 3,
                MacroAxisTarget.RightStickY => 4,
                MacroAxisTarget.LeftTrigger => 2,
                MacroAxisTarget.RightTrigger => 5,
                _ => -1
            };
            if (axisIndex < 0 || raw.Axes == null || axisIndex >= raw.Axes.Length)
                return 0f;
            // Raw axes are short (-32768..32767) → 0..1
            return (raw.Axes[axisIndex] + 32768f) / 65535f;
        }

        /// <summary>
        /// Reads an axis value from a physical input device's raw InputState.
        /// Returns 0.0–1.0 (normalized from short -32768..32767).
        /// </summary>
        private float ReadAxisFromDevice(MacroAction action)
        {
            if (action.SourceDeviceGuid == Guid.Empty || action.SourceDeviceAxisIndex < 0)
                return 0f;
            var device = FindOnlineDeviceByInstanceGuid(action.SourceDeviceGuid);
            if (device == null || device.InputState == null || device.InputState.Axis == null
                || action.SourceDeviceAxisIndex >= device.InputState.Axis.Length)
                return 0f;
            return (device.InputState.Axis[action.SourceDeviceAxisIndex] + 32768f) / 65535f;
        }

        /// <summary>
        /// Reads an axis value from a physical input device as a -1..+1 deflection for mouse movement.
        /// </summary>
        private float ReadAxisFromDeviceAsMouse(MacroAction action)
        {
            float vol = ReadAxisFromDevice(action);
            // Convert 0..1 to -1..+1 for symmetric deflection
            return (vol - 0.5f) * 2f;
        }

        // ─────────────────────────────────────────────
        //  Mouse output for MouseMove / MouseButton / MouseScroll
        // ─────────────────────────────────────────────

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        private static void SendMouseMoveInput(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void SendMouseButtonInput(MacroMouseButton button, bool down)
        {
            uint flags;
            uint mouseData = 0;
            switch (button)
            {
                case MacroMouseButton.Left:   flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
                case MacroMouseButton.Right:  flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
                case MacroMouseButton.Middle: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
                case MacroMouseButton.X1:     flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; mouseData = 1; break;
                case MacroMouseButton.X2:     flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; mouseData = 2; break;
                default: return;
            }
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void SendMouseScrollInput(int amount)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { mouseData = (uint)amount, dwFlags = MOUSEEVENTF_WHEEL } }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        /// <summary>
        /// Reads a source axis as a signed float (-1.0..+1.0) for mouse delta calculation.
        /// Sticks: -32768..32767 → -1..+1. Triggers: 0..65535 → 0..+1 (unidirectional).
        /// </summary>
        private static float ReadAxisAsMouse(in Gamepad gp, MacroAxisTarget target) => target switch
        {
            MacroAxisTarget.LeftStickX   => gp.ThumbLX / 32767f,
            MacroAxisTarget.LeftStickY   => gp.ThumbLY / 32767f,
            MacroAxisTarget.RightStickX  => gp.ThumbRX / 32767f,
            MacroAxisTarget.RightStickY  => gp.ThumbRY / 32767f,
            MacroAxisTarget.LeftTrigger  => gp.LeftTrigger / 65535f,
            MacroAxisTarget.RightTrigger => gp.RightTrigger / 65535f,
            _ => 0f
        };

        private static float ReadAxisAsMouseRaw(in VJoyRawState raw, MacroAxisTarget target)
        {
            int axisIndex = target switch
            {
                MacroAxisTarget.LeftStickX   => 0,
                MacroAxisTarget.LeftStickY   => 1,
                MacroAxisTarget.RightStickX  => 3,
                MacroAxisTarget.RightStickY  => 4,
                MacroAxisTarget.LeftTrigger   => 2,
                MacroAxisTarget.RightTrigger  => 5,
                _ => -1
            };
            if (axisIndex < 0 || raw.Axes == null || axisIndex >= raw.Axes.Length) return 0f;
            return raw.Axes[axisIndex] / 32767f;
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

        // ─────────────────────────────────────────────
        //  Global macro evaluation (profile shortcuts)
        // ─────────────────────────────────────────────

        private void EvaluateGlobalMacros()
        {
            if (SuppressGlobalMacros) return;

            var globalMacros = SettingsManager.GlobalMacros;
            if (globalMacros == null || globalMacros.Length == 0)
                return;

            for (int m = 0; m < globalMacros.Length; m++)
            {
                var gm = globalMacros[m];
                if (!gm.HasTrigger) continue;

                bool triggerActive = CheckGlobalMacroTrigger(gm);
                bool wasTriggerActive = gm.WasTriggerActive;
                gm.WasTriggerActive = triggerActive;

                if (triggerActive && !wasTriggerActive)
                    QueueProfileSwitch(gm);
            }
        }

        /// <summary>
        /// Checks whether all buttons in the trigger combo are currently pressed.
        /// Supports cross-device combos: each button entry specifies its own device.
        /// For "Any Device" entries (DeviceInstanceGuid == Empty), checks all devices
        /// with matching product GUID.
        /// </summary>
        private bool CheckGlobalMacroTrigger(GlobalMacroData gm)
        {
            var entries = gm.TriggerEntries;
            if (entries == null || entries.Length == 0) return false;

            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return false;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    if (!IsEntryActive(entry, devices))
                        return false;
                }
            }
            return true;
        }

        private static bool IsEntryActive(TriggerButtonEntry entry, System.Collections.Generic.List<Engine.Data.UserDevice> devices)
        {
            if (entry.DeviceInstanceGuid != Guid.Empty)
            {
                // Specific device.
                for (int d = 0; d < devices.Count; d++)
                {
                    var ud = devices[d];
                    if (ud.InstanceGuid != entry.DeviceInstanceGuid) continue;
                    if (!ud.IsOnline || ud.InputState == null) return false;
                    return entry.IsAxis
                        ? CheckAxisActive(ud.InputState, entry.AxisIndex, entry.AxisThreshold)
                        : CheckButtonActive(ud.InputState, entry.ButtonIndex);
                }
                return false;
            }

            // "Any Device" — check all devices with matching product GUID.
            for (int d = 0; d < devices.Count; d++)
            {
                var ud = devices[d];
                if (!ud.IsOnline || ud.InputState == null) continue;
                if (ud.DevicePath != null && ud.DevicePath.StartsWith("aggregate://")) continue;
                if (entry.DeviceProductGuid != Guid.Empty && ud.ProductGuid != entry.DeviceProductGuid)
                    continue;
                bool active = entry.IsAxis
                    ? CheckAxisActive(ud.InputState, entry.AxisIndex, entry.AxisThreshold)
                    : CheckButtonActive(ud.InputState, entry.ButtonIndex);
                if (active) return true;
            }
            return false;
        }

        private static bool CheckButtonActive(Engine.CustomInputState state, int index)
        {
            var buttons = state.Buttons;
            return index >= 0 && index < buttons.Length && buttons[index];
        }

        private static bool CheckAxisActive(Engine.CustomInputState state, int index, float threshold)
        {
            var axes = state.Axis;
            if (index < 0 || index >= axes.Length) return false;
            // Axis values are 0–65535 (center=32767). Normalize to 0.0–1.0.
            float normalized = axes[index] / 65535f;
            return normalized >= threshold;
        }

        private void QueueProfileSwitch(GlobalMacroData gm)
        {
            string targetId;
            switch (gm.SwitchMode)
            {
                case SwitchProfileMode.Specific:
                    targetId = gm.TargetProfileId;
                    break;
                case SwitchProfileMode.Next:
                    targetId = GetNextProfileId(+1);
                    break;
                case SwitchProfileMode.Previous:
                    targetId = GetNextProfileId(-1);
                    break;
                default:
                    return;
            }

            PendingProfileSwitchId = targetId;
            PendingProfileSwitchIsManual = true;
        }

        private string GetNextProfileId(int direction)
        {
            var profiles = SettingsManager.Profiles;
            if (profiles == null || profiles.Count == 0) return null;

            string currentId = SettingsManager.ActiveProfileId;

            // Build ordered list: [null (default), profile0, profile1, ...]
            int currentIndex = 0; // default
            for (int i = 0; i < profiles.Count; i++)
            {
                if (profiles[i].Id == currentId)
                { currentIndex = i + 1; break; }
            }

            int totalCount = profiles.Count + 1; // +1 for default
            int nextIndex = (currentIndex + direction + totalCount) % totalCount;

            return nextIndex == 0 ? null : profiles[nextIndex - 1].Id;
        }
    }
}
