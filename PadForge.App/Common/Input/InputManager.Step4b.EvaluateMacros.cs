using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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
            // Marshal.QueryInterface declares the IID as `in Guid` — passing
            // it bare lets the compiler insert the in-pass; an explicit `ref`
            // here was a CS9191 warning (silently downgraded to in-pass).
            int hr = Marshal.QueryInterface(pSession, in iidVol, out IntPtr pVol);
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

        // True while evaluating a slot fed by a gamepad-only-restricted peer (issue
        // #138). The keyboard/mouse/scroll macro emission helpers consult this and
        // suppress those actions, so a restricted peer can never inject keystrokes.
        // Single-threaded (the poll loop), so a plain field is safe. Static so the
        // static SendInput emission helpers can read it.
        private static bool _currentMacroSlotRestricted;

        /// <summary>
        /// Step 4b: Evaluate macros for all pad slots.
        /// Called after CombineOutputStates and before VirtualDevices.
        /// </summary>
        private void EvaluateMacros()
        {
            _currentMacroSlotRestricted = false; // global macros emit no keystrokes
            EvaluateGlobalMacros();

            for (int i = 0; i < MaxPads; i++)
            {
                var macros = MacroSnapshots[i];
                if (macros == null || macros.Length == 0)
                    continue;

                // Restricted if a restricted peer feeds this slot OR triggers any of
                // its macros (the macro engine resolves triggers by device GUID
                // independent of slot mapping, so a home-slot-only check left a hole).
                _currentMacroSlotRestricted = IsSlotRestricted(i) || AnyMacroTriggerRestricted(macros);
                try
                {
                    if (SlotExtendedIsCustom[i])
                        EvaluateSlotMacrosExtended(ref CombinedExtendedRawStates[i], macros);
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

                // Skip macros with no trigger configured (unless Always /
                // CustomExpression mode — Custom always has a formula that
                // evaluates, even if the formula references no variables).
                bool hasButtons = macro.UsesRawTrigger || macro.TriggerButtons != 0;
                if (macro.TriggerMode != MacroTriggerMode.Always &&
                    macro.TriggerMode != MacroTriggerMode.CustomExpression &&
                    !macro.UsesAxisTrigger && !macro.UsesPovTrigger && !hasButtons &&
                    !macro.UsesGestureTrigger)
                    continue;

                // Determine trigger state. Buttons, POVs, gestures, AND axes must all be active together.
                bool triggerActive;
                if (macro.TriggerMode == MacroTriggerMode.Always)
                    triggerActive = true;
                else if (macro.TriggerMode == MacroTriggerMode.CustomExpression)
                    triggerActive = EvaluateCustomExpressionTrigger(macro, in gp);
                else
                {
                    bool buttonOk = true;
                    bool povOk = true;
                    bool gestureOk = true;
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
                    if (macro.UsesGestureTrigger)
                        gestureOk = CheckGestureTrigger(macro);
                    if (macro.UsesAxisTrigger)
                    {
                        float threshold = macro.TriggerAxisThreshold / 100f;
                        // Legacy slot-combined axes (OutputController source).
                        for (int ai = 0; ai < macro.TriggerAxisTargets.Length; ai++)
                        {
                            var axTarget = macro.TriggerAxisTargets[ai];
                            var dir = macro.GetAxisDirection(ai);
                            float val = ReadAxisAsVolume(in gp, axTarget); // 0→1

                            if (dir == MacroAxisDirection.Positive)
                            {
                                if (val < 0.5f + threshold * 0.5f)
                                { axisOk = false; break; }
                            }
                            else if (dir == MacroAxisDirection.Negative)
                            {
                                if (val > 0.5f - threshold * 0.5f)
                                { axisOk = false; break; }
                            }
                            else
                            {
                                if (val < threshold)
                                { axisOk = false; break; }
                            }
                        }
                    }

                    // Per-device axis entries from multi-device combos. Uses
                    // the same Invert / HalfAxis / DeadZone semantics the
                    // merge-mapping engine uses for axis-to-button sources
                    // (see PadForge.Engine.Common.Mapping.SourceCoercion).
                    // No per-axis-target classification — every axis index
                    // is evaluated uniformly with the entry's three flags.
                    if (axisOk)
                    {
                        var entries = macro.GetTriggerInputEntries();
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var e = entries[i];
                            if (e.AxisTarget == MacroAxisTarget.None) continue;
                            var ud = FindSlotDeviceByInstanceGuid(e.DeviceGuid, macro.PadIndex);
                            if (ud == null || !ud.IsOnline || ud.InputState?.Axis == null)
                            { axisOk = false; break; }
                            int axIdx = e.AxisTarget switch
                            {
                                MacroAxisTarget.LeftStickX  => 0,
                                MacroAxisTarget.LeftStickY  => 1,
                                MacroAxisTarget.LeftTrigger => 2,
                                MacroAxisTarget.RightStickX => 3,
                                MacroAxisTarget.RightStickY => 4,
                                MacroAxisTarget.RightTrigger=> 5,
                                _ => -1
                            };
                            if (axIdx < 0 || axIdx >= ud.InputState.Axis.Length)
                            { axisOk = false; break; }

                            int av = ud.InputState.Axis[axIdx];
                            double thresh = Math.Max(e.DeadZone, 1) / 100.0;
                            bool active;
                            if (e.HalfAxis)
                            {
                                if (e.Bidirectional)
                                {
                                    // Either side of center past deadzone counts —
                                    // |av − 32768| > 32767 * thresh. Invert is
                                    // irrelevant here (mirroring around center
                                    // covers both directions already).
                                    int delta = av - 32768;
                                    if (delta < 0) delta = -delta;
                                    active = delta > (int)(32767 * thresh);
                                }
                                else if (e.Invert)
                                    active = av < (int)(32767 * (1.0 - thresh));
                                else
                                    active = av > (int)(32768 + 32767 * thresh);
                            }
                            else
                            {
                                int hi = (int)(thresh * 65535);
                                if (e.Invert)
                                    active = av < 65535 - hi;
                                else
                                    active = av > hi;
                            }

                            if (!active) { axisOk = false; break; }
                        }
                    }

                    triggerActive = buttonOk && povOk && gestureOk && axisOk;
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
                    case MacroTriggerMode.CustomExpression:
                        // Rising edge of the formula result crossing 0.5,
                        // matching OnPress semantics for a synthetic boolean.
                        shouldStart = triggerActive && !wasTriggerActive;
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
                    // Looping macro sounds are trigger-bound on this path:
                    // release stops them (one-shots play out).
                    SoundMacroService.StopLoopsForMacro(macro.PadIndex, macro);
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

        /// <summary>Evaluates a macro's <see cref="MacroTriggerMode.CustomExpression"/>
        /// trigger against the slot's combined virtual controller output
        /// (XInput-shaped <see cref="Gamepad"/>). Builds
        /// the variable values, runs the cached compiled formula, and reports
        /// "trigger active" as result ≥ 0.5. Returns false on any malformed input
        /// so the macro stays dormant rather than misfiring.</summary>
        // Polling-thread-local List<float> for custom-expression macro
        // triggers. Called per macro per cycle; pool removes the per-call
        // alloc. List<T> (not float[]) so its Count tracks populated length
        // for the IList<float> Evaluate overload.
        [System.ThreadStatic] private static List<float> _macroExprSourcesBuf;

        private bool EvaluateCustomExpressionTrigger(MacroItem macro, in Gamepad gp)
        {
            var compiled = macro.TriggerExpressionCompiled;
            if (compiled == null || !compiled.IsValid) return false;
            var vars = macro.TriggerExpressionVariables;
            int n = vars?.Count ?? 0;
            var sources = _macroExprSourcesBuf ??= new List<float>(8);
            sources.Clear();
            for (int i = 0; i < n; i++)
                sources.Add(ReadExpressionVariable(vars[i], in gp, macro.PadIndex));
            float result = compiled.Evaluate(sources);
            return result >= 0.5f;
        }

        /// <summary>Same as <see cref="EvaluateCustomExpressionTrigger"/> but for
        /// the Extended (custom controller) path. OutputController-bound variables
        /// resolve to 0 because there is no Xbox-shape gamepad on this code path.</summary>
        private bool EvaluateCustomExpressionTriggerExtended(MacroItem macro, in ExtendedRawState raw)
        {
            var compiled = macro.TriggerExpressionCompiled;
            if (compiled == null || !compiled.IsValid) return false;
            var vars = macro.TriggerExpressionVariables;
            int n = vars?.Count ?? 0;
            var sources = _macroExprSourcesBuf ??= new List<float>(8);
            sources.Clear();
            for (int i = 0; i < n; i++)
                sources.Add(ReadExpressionVariableRaw(vars[i], in raw, macro.PadIndex));
            float result = compiled.Evaluate(sources);
            return result >= 0.5f;
        }

        /// <summary>Reads one <see cref="MacroExpressionVariable"/>'s current value
        /// against the slot's combined <see cref="Gamepad"/>. Buttons → 0/1,
        /// triggers → 0..1, sticks → 0..1 (0.5 = rest), POVs → 1 when the live POV
        /// is in the same 45° sector as the stored direction.</summary>
        private float ReadExpressionVariable(MacroExpressionVariable v, in Gamepad gp, int slotIndex)
        {
            if (v == null || !v.IsBound) return 0f;

            if (v.Source == MacroTriggerSource.OutputController)
                return ReadOutputChannel(v.OutputChannel, in gp);

            var ud = FindSlotDeviceByInstanceGuid(v.DeviceGuid, slotIndex);
            if (ud == null || !ud.IsOnline || ud.InputState == null) return 0f;

            if (v.RawButton >= 0)
            {
                var btns = ud.InputState.Buttons;
                return (btns != null && v.RawButton < btns.Length && btns[v.RawButton]) ? 1f : 0f;
            }
            if (!string.IsNullOrEmpty(v.Pov))
            {
                if (!MacroItem.ParsePovTrigger(v.Pov, out int idx, out int targetCd)) return 0f;
                var povs = ud.InputState.Povs;
                if (povs == null || idx < 0 || idx >= povs.Length || povs[idx] < 0) return 0f;
                int diff = Math.Abs(povs[idx] - targetCd);
                if (diff > 18000) diff = 36000 - diff;
                return diff <= 2250 ? 1f : 0f;
            }
            if (v.AxisTarget != MacroAxisTarget.None)
            {
                int axisIndex = AxisTargetToDeviceIndex(v.AxisTarget);
                var axes = ud.InputState.Axis;
                if (axes == null || axisIndex < 0 || axisIndex >= axes.Length) return 0f;
                return (axes[axisIndex] + 32768f) / 65535f;
            }
            return 0f;
        }

        /// <summary>Same as <see cref="ReadExpressionVariable"/> but the
        /// OutputController arm reads 0 because Extended slots have no Xbox-shape
        /// combined state.</summary>
        private float ReadExpressionVariableRaw(MacroExpressionVariable v, in ExtendedRawState raw, int slotIndex)
        {
            if (v == null || !v.IsBound) return 0f;
            if (v.Source == MacroTriggerSource.OutputController) return 0f;

            var ud = FindSlotDeviceByInstanceGuid(v.DeviceGuid, slotIndex);
            if (ud == null || !ud.IsOnline || ud.InputState == null) return 0f;

            if (v.RawButton >= 0)
            {
                var btns = ud.InputState.Buttons;
                return (btns != null && v.RawButton < btns.Length && btns[v.RawButton]) ? 1f : 0f;
            }
            if (!string.IsNullOrEmpty(v.Pov))
            {
                if (!MacroItem.ParsePovTrigger(v.Pov, out int idx, out int targetCd)) return 0f;
                var povs = ud.InputState.Povs;
                if (povs == null || idx < 0 || idx >= povs.Length || povs[idx] < 0) return 0f;
                int diff = Math.Abs(povs[idx] - targetCd);
                if (diff > 18000) diff = 36000 - diff;
                return diff <= 2250 ? 1f : 0f;
            }
            if (v.AxisTarget != MacroAxisTarget.None)
            {
                int axisIndex = AxisTargetToDeviceIndex(v.AxisTarget);
                var axes = ud.InputState.Axis;
                if (axes == null || axisIndex < 0 || axisIndex >= axes.Length) return 0f;
                return (axes[axisIndex] + 32768f) / 65535f;
            }
            return 0f;
        }

        /// <summary>Reads one virtual controller output channel as a normalized 0..1 float.
        /// Buttons → 0 or 1, triggers → 0..1, sticks → 0..1 with 0.5 = rest
        /// (sign preserved by the off-center reading).</summary>
        private static float ReadOutputChannel(MacroOutputChannel ch, in Gamepad gp)
        {
            switch (ch)
            {
                case MacroOutputChannel.A:         return (gp.Buttons & Gamepad.A) != 0 ? 1f : 0f;
                case MacroOutputChannel.B:         return (gp.Buttons & Gamepad.B) != 0 ? 1f : 0f;
                case MacroOutputChannel.X:         return (gp.Buttons & Gamepad.X) != 0 ? 1f : 0f;
                case MacroOutputChannel.Y:         return (gp.Buttons & Gamepad.Y) != 0 ? 1f : 0f;
                case MacroOutputChannel.LB:        return (gp.Buttons & Gamepad.LEFT_SHOULDER) != 0 ? 1f : 0f;
                case MacroOutputChannel.RB:        return (gp.Buttons & Gamepad.RIGHT_SHOULDER) != 0 ? 1f : 0f;
                case MacroOutputChannel.LS:        return (gp.Buttons & Gamepad.LEFT_THUMB) != 0 ? 1f : 0f;
                case MacroOutputChannel.RS:        return (gp.Buttons & Gamepad.RIGHT_THUMB) != 0 ? 1f : 0f;
                case MacroOutputChannel.Back:      return (gp.Buttons & Gamepad.BACK) != 0 ? 1f : 0f;
                case MacroOutputChannel.Start:     return (gp.Buttons & Gamepad.START) != 0 ? 1f : 0f;
                case MacroOutputChannel.Guide:     return (gp.Buttons & Gamepad.GUIDE) != 0 ? 1f : 0f;
                case MacroOutputChannel.DpadUp:    return (gp.Buttons & Gamepad.DPAD_UP) != 0 ? 1f : 0f;
                case MacroOutputChannel.DpadDown:  return (gp.Buttons & Gamepad.DPAD_DOWN) != 0 ? 1f : 0f;
                case MacroOutputChannel.DpadLeft:  return (gp.Buttons & Gamepad.DPAD_LEFT) != 0 ? 1f : 0f;
                case MacroOutputChannel.DpadRight: return (gp.Buttons & Gamepad.DPAD_RIGHT) != 0 ? 1f : 0f;
                case MacroOutputChannel.LT:        return gp.LeftTrigger / 65535f;
                case MacroOutputChannel.RT:        return gp.RightTrigger / 65535f;
                case MacroOutputChannel.LX:        return (gp.ThumbLX + 32768f) / 65535f;
                case MacroOutputChannel.LY:        return (gp.ThumbLY + 32768f) / 65535f;
                case MacroOutputChannel.RX:        return (gp.ThumbRX + 32768f) / 65535f;
                case MacroOutputChannel.RY:        return (gp.ThumbRY + 32768f) / 65535f;
                default: return 0f;
            }
        }

        /// <summary>Maps a <see cref="MacroAxisTarget"/> to a raw-device axis
        /// index using the standard SDL gamepad layout (X/Y/Z = LX/LY/LT,
        /// rotZ/rotZ' approximated by rotX/rotY for RX/RY/RT).</summary>
        private static int AxisTargetToDeviceIndex(MacroAxisTarget t) => t switch
        {
            MacroAxisTarget.LeftStickX  => 0,
            MacroAxisTarget.LeftStickY  => 1,
            MacroAxisTarget.LeftTrigger => 2,
            MacroAxisTarget.RightStickX => 3,
            MacroAxisTarget.RightStickY => 4,
            MacroAxisTarget.RightTrigger=> 5,
            _ => -1
        };

        /// <summary>Resolves an online device by GUID only when it is assigned to
        /// <paramref name="slotIndex"/>. A macro must fire from its own virtual
        /// controller's devices, never from a device on another VC. Without this a
        /// macro copied to a slot that does not have its trigger device would still
        /// fire from that foreign device (#112).</summary>
        private Engine.Data.UserDevice FindSlotDeviceByInstanceGuid(Guid instanceGuid, int slotIndex)
        {
            if (instanceGuid == Guid.Empty) return null;
            if (SettingsManager.FindSettingByInstanceGuidAndSlot(instanceGuid, slotIndex) == null) return null;
            return FindOnlineDeviceByInstanceGuid(instanceGuid);
        }

        /// <summary>
        /// Checks whether every raw-button entry on the macro's trigger is
        /// currently pressed on its respective assigned device. Walks the
        /// multi-device <see cref="MacroItem.GetTriggerInputEntries"/> list
        /// first; falls back to the legacy single-device
        /// <see cref="MacroItem.TriggerDeviceGuid"/> + <c>TriggerRawButtons</c>
        /// path if the entry list is empty (e.g. macros saved by an older
        /// PadForge version that pre-dated multi-device support).
        /// </summary>
        private bool CheckRawButtonTrigger(MacroItem macro)
        {
            var entries = macro.GetTriggerInputEntries();
            if (entries.Count > 0)
            {
                // Multi-device path. EVERY button entry must be active on
                // its own device for the trigger to fire.
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.RawButton < 0) continue; // POV entries handled by CheckRawPovTrigger
                    var ud = FindSlotDeviceByInstanceGuid(e.DeviceGuid, macro.PadIndex);
                    if (ud == null || !ud.IsOnline || ud.InputState?.Buttons == null) return false;
                    var btns = ud.InputState.Buttons;
                    if (e.RawButton >= btns.Length || !btns[e.RawButton]) return false;
                }
                return true;
            }

            // Legacy single-device fallback.
            var udLegacy = FindSlotDeviceByInstanceGuid(macro.TriggerDeviceGuid, macro.PadIndex);
            if (udLegacy == null || !udLegacy.IsOnline || udLegacy.InputState == null)
                return false;

            var buttons = udLegacy.InputState.Buttons;
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
        /// Checks whether every POV-entry on the macro's trigger is currently
        /// active on its respective assigned device. Same multi-device-first
        /// fallback shape as <see cref="CheckRawButtonTrigger"/>. Each entry
        /// must match within ±45° of its stored centidegrees.
        /// </summary>
        private bool CheckRawPovTrigger(MacroItem macro)
        {
            var entries = macro.GetTriggerInputEntries();
            if (entries.Count > 0)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (string.IsNullOrEmpty(e.Pov)) continue; // button entries handled separately
                    if (!MacroItem.ParsePovTrigger(e.Pov, out int idx, out int targetCd)) return false;
                    var ud = FindSlotDeviceByInstanceGuid(e.DeviceGuid, macro.PadIndex);
                    if (ud == null || !ud.IsOnline || ud.InputState?.Povs == null) return false;
                    var povs = ud.InputState.Povs;
                    if (idx < 0 || idx >= povs.Length || povs[idx] < 0) return false;
                    int diff = Math.Abs(povs[idx] - targetCd);
                    if (diff > 18000) diff = 36000 - diff;
                    if (diff > 2250) return false;
                }
                return true;
            }

            // Legacy single-device fallback.
            var udLegacy = FindSlotDeviceByInstanceGuid(macro.TriggerDeviceGuid, macro.PadIndex);
            if (udLegacy == null || !udLegacy.IsOnline || udLegacy.InputState == null)
                return false;

            var legacyPovs = udLegacy.InputState.Povs;
            if (legacyPovs == null) return false;

            foreach (var entry in macro.TriggerPovs)
            {
                if (!MacroItem.ParsePovTrigger(entry, out int idx, out int targetCd))
                    return false;
                if (idx < 0 || idx >= legacyPovs.Length || legacyPovs[idx] < 0)
                    return false;
                int diff = Math.Abs(legacyPovs[idx] - targetCd);
                if (diff > 18000) diff = 36000 - diff;
                if (diff > 2250) return false;
            }
            return true;
        }

        /// <summary>
        /// Checks whether every touchpad-gesture entry on the macro's
        /// trigger is currently firing on its respective assigned device
        /// (#177). Evaluates through the SAME provider hook the mapping
        /// grid's gesture descriptors read
        /// (SourceCoercion.TouchpadGestureFiredProvider, wired by
        /// InputService to this InputManager's per-(slot, device, pad)
        /// GestureContexts), so the Touchpad tab's enable gates govern
        /// macros and mappings identically and held spots / one-shot
        /// swipes behave exactly as they do on a mapping row.
        /// </summary>
        private bool CheckGestureTrigger(MacroItem macro)
        {
            var provider = PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadGestureFiredProvider;
            if (provider == null) return false;

            var entries = macro.GetTriggerInputEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (string.IsNullOrEmpty(e.GestureDescriptor)) continue;
                // Same online/assignment gate the button and POV checkers
                // apply. Without it a disconnect mid-touch leaves the
                // device's gesture context frozen with the held spot key
                // still in the fired set (Step 2 stops ticking offline
                // devices, and release only happens inside
                // GestureRecognizer.Update), so the macro would latch
                // active forever on a controller that is gone.
                var ud = FindSlotDeviceByInstanceGuid(e.DeviceGuid, macro.PadIndex);
                if (ud == null || !ud.IsOnline) return false;
                // Parts are parsed once and cached on the entry: this
                // runs per macro per 1 kHz polling tick, and the button /
                // POV siblings are allocation-free on this path.
                if (!e.TryGetGestureParts(out int padIdx, out string gestureName))
                    return false;
                if (!provider(macro.PadIndex, e.DeviceGuidString, padIdx, gestureName))
                    return false;
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

                case MacroActionType.LightbarColor:
                {
                    // Single-frame fire — write the override into PSConfig
                    // and advance immediately. The fade (Reactive) or
                    // hold (Sticky) plays out via the synthesizer reading
                    // PSConfig on each subsequent dispatch tick, so the
                    // macro doesn't need to stay "current" while the
                    // lightbar is visibly transitioning.
                    ApplyLightbarColorAction(macro, action);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.Rumble:
                {
                    // Same single-frame-fire shape as LightbarColor: stamp
                    // the override and advance. The FFB pipeline reads
                    // MacroRumbleOverrides[slot] on each subsequent tick
                    // and combines via max() with the game's rumble.
                    ApplyRumbleAction(macro, action);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.RumbleStop:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                        MacroRumbleOverrides[slotIndex].Clear();
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.RumbleTrigger:
                {
                    // Trigger-channel sibling of Rumble (#102). Stamps the slot's
                    // MacroTriggerRumbleOverrides; the routing pass max-combines it
                    // into the trigger channel on each subsequent tick.
                    ApplyTriggerRumbleAction(macro, action);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.RumbleTriggerStop:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                        MacroTriggerRumbleOverrides[slotIndex].Clear();
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.PlaySound:
                {
                    // Single-frame fire like Rumble: hand the file to the
                    // sound service (non-blocking; uncached files decode on
                    // the thread pool) and advance. The macro object is the
                    // loop key so trigger release / SoundStop can stop what
                    // this macro started; looping starts are idempotent per
                    // (macro, file) so an Until-Release list restart can't
                    // stack instances.
                    SoundMacroService.Play(macro.PadIndex, macro,
                        action.SoundFilePath, action.SoundVolume, action.SoundLoop);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.SoundStop:
                {
                    SoundMacroService.StopSlot(macro.PadIndex);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.LightbarColorClear:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                    {
                        // Slot-level fan-out: every per-device config on
                        // the slot clears its override.
                        foreach (var devCfg in EnumerateSlotPlayStationConfigs(slotIndex))
                            devCfg.ClearMacroOverride();
                    }
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.LightbarModeSet:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                    {
                        // Slot-level fan-out: switch every device's mode
                        // to the action's target. Each device renders
                        // that mode using its OWN per-device config
                        // (its own RGB / palette / decay).
                        foreach (var devCfg in EnumerateSlotPlayStationConfigs(slotIndex))
                            ApplyLightbarModeSetMigrated(devCfg, action.LightbarTargetMode);
                    }
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.LightbarModeCycle:
                {
                    ApplyLightbarModeCycleAction(macro, action);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.SetGyroEngaged:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                    {
                        switch (action.SetGyroEngagedMode)
                        {
                            case MacroSetGyroEngagedMode.On:
                                GyroEngagedFromMacro[slotIndex] = true;
                                break;
                            case MacroSetGyroEngagedMode.Off:
                                GyroEngagedFromMacro[slotIndex] = false;
                                break;
                            case MacroSetGyroEngagedMode.Toggle:
                            default:
                                GyroEngagedFromMacro[slotIndex] = !GyroEngagedFromMacro[slotIndex];
                                break;
                        }
                    }
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.MouseRecenter:
                {
                    // System-wide cursor write (#108): snap the desktop cursor to
                    // the primary-monitor center on press. centerX = "not Y-only",
                    // centerY = "not X-only", so X+Y recenters both.
                    var m = action.CursorRecenterMode;
                    CursorControlService.Active?.RecenterCursor(
                        m != CursorRecenterMode.YOnly, m != CursorRecenterMode.XOnly);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.MouseFixPosition:
                {
                    // Toggle the sticky cursor pin (#109). The 200 Hz service
                    // thread enforces the target each tick while engaged.
                    CursorControlService.Active?.TogglePin(
                        action.CursorPinMode, action.CursorPinX, action.CursorPinY);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.MouseLimitRegion:
                {
                    // Toggle the cursor region clamp (#110). The service keeps the
                    // cursor inside the inset rectangle each tick while engaged.
                    CursorControlService.Active?.ToggleClamp(
                        action.CursorClampMode, action.CursorClampInsetX, action.CursorClampInsetY);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.DisconnectController:
                    ExecuteDisconnectControllerAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.RunProgram:
                    ExecuteRunProgramAction(action);
                    AdvanceAction(macro);
                    break;
            }
        }

        /// <summary>Resolves a Disconnect Controller action's victims (#162)
        /// and hands the radio I/O to the threadpool. Resolution stays on the
        /// polling thread (dictionary walks only); the IOCTL never runs here
        /// because the polling thread must not block on I/O. Victims are
        /// filtered to online, Bluetooth-pathed devices with a parseable
        /// serial. Unlike the DS4Windows DisconnectBT gate (synced +
        /// !isCharging + ConnectionType.BT), there is NO charging gate here
        /// (see the AddDisconnectCandidate note below for why).</summary>
        private void ExecuteDisconnectControllerAction(MacroItem macro, MacroAction action)
        {
            var targets = new System.Collections.Generic.List<DisconnectTarget>();

            switch (action.DisconnectTarget)
            {
                case MacroDisconnectTarget.SpecificDevice:
                    AddDisconnectCandidate(targets,
                        SettingsManager.FindDeviceByInstanceGuid(action.DisconnectDeviceGuid));
                    break;

                case MacroDisconnectTarget.SlotDevices:
                {
                    var guids = new System.Collections.Generic.List<Guid>();
                    var settings = SettingsManager.UserSettings;
                    if (settings != null)
                    {
                        lock (settings.SyncRoot)
                        {
                            foreach (var us in settings.Items)
                                if (us != null && us.MapTo == macro.PadIndex)
                                    guids.Add(us.InstanceGuid);
                        }
                    }
                    foreach (var g in guids)
                        AddDisconnectCandidate(targets, SettingsManager.FindDeviceByInstanceGuid(g));
                    break;
                }

                case MacroDisconnectTarget.AllDevices:
                {
                    var devices = SettingsManager.UserDevices;
                    if (devices != null)
                    {
                        UserDevice[] snapshot;
                        lock (devices.SyncRoot)
                        {
                            snapshot = new UserDevice[devices.Items.Count];
                            devices.Items.CopyTo(snapshot, 0);
                        }
                        foreach (var ud in snapshot)
                            AddDisconnectCandidate(targets, ud);
                    }
                    break;
                }

                case MacroDisconnectTarget.TriggeringDevice:
                default:
                {
                    var seen = new System.Collections.Generic.HashSet<Guid>();
                    foreach (var entry in macro.GetTriggerInputEntries())
                        if (entry.DeviceGuid != Guid.Empty) seen.Add(entry.DeviceGuid);
                    if (seen.Count == 0 && macro.TriggerDeviceGuid != Guid.Empty)
                        seen.Add(macro.TriggerDeviceGuid); // legacy single-device trigger
                    foreach (var g in seen)
                        AddDisconnectCandidate(targets, SettingsManager.FindDeviceByInstanceGuid(g));
                    break;
                }
            }

            if (targets.Count == 0)
                return; // nothing eligible (USB, or no trigger device)

            DisconnectTarget[] victims = targets.ToArray();
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var t in victims)
                    PadForge.Common.Input.BluetoothLinkHelper.TryDisconnectDevice(
                        t.VendorId, t.ProductId, t.DevicePath, t.Serial, t.BthInstanceIds, t.GamepadHandle);
            });
        }

        /// <summary>One disconnect victim (#162): everything the device-aware
        /// dispatch needs, captured on the polling thread.</summary>
        private readonly struct DisconnectTarget
        {
            public readonly ushort VendorId;
            public readonly ushort ProductId;
            public readonly string DevicePath;
            public readonly string Serial;
            public readonly string[] BthInstanceIds;
            public readonly IntPtr GamepadHandle;
            public DisconnectTarget(ushort vid, ushort pid, string path, string serial, string[] bthIds, IntPtr gamepadHandle)
            { VendorId = vid; ProductId = pid; DevicePath = path; Serial = serial; BthInstanceIds = bthIds; GamepadHandle = gamepadHandle; }
        }

        /// <summary>Applies the #162 eligibility gates (online, wireless per
        /// IsDisconnectTarget) and captures the disconnect dispatch fields.
        /// No serial gate: the Steam and Xbox lanes work without one, and the
        /// BR/EDR fallback rejects an empty serial itself. No charging gate
        /// either, a deliberate divergence from DS4Windows: a charging pad is
        /// exactly the pad sitting idle within reach of a power-off chord,
        /// and the user's explicit command should be obeyed (hardware round,
        /// 2026-07-02: a plugged-in DualSense was correctly reported charging
        /// and wrongly dropped from every target mode). Disconnecting
        /// Bluetooth does not interrupt charging.</summary>
        private static void AddDisconnectCandidate(
            System.Collections.Generic.List<DisconnectTarget> targets, UserDevice ud)
        {
            if (ud == null || !ud.IsOnline) return;
            if (!PadForge.Common.Input.BluetoothLinkHelper.IsDisconnectTarget(ud.DevicePath, ud.VendorId, ud.ProdId)) return;
            foreach (var t in targets)
                if (t.DevicePath == ud.DevicePath) return;
            targets.Add(new DisconnectTarget(ud.VendorId, ud.ProdId, ud.DevicePath,
                ud.SerialNumber ?? string.Empty, ud.HidHideInstanceIds?.ToArray(),
                ud.Device?.GamepadHandle ?? IntPtr.Zero));
        }

        /// <summary>Launches the action's external program (user request). Runs on the
        /// thread pool, NOT the ~1000 Hz macro/poll thread: ShellExecute can block for
        /// milliseconds and would collapse the poll rate. Fire and forget. The macro
        /// does not wait for exit. A bad path is swallowed so a mistyped command never
        /// tears down the input engine.</summary>
        private static void ExecuteRunProgramAction(MacroAction action)
        {
            string path = action.ProgramPath?.Trim();
            if (string.IsNullOrEmpty(path)) return;
            string args = action.ProgramArgs ?? string.Empty;
            string workDir = action.ProgramWorkingDir?.Trim() ?? string.Empty;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = args,
                        UseShellExecute = true,
                    };
                    if (!string.IsNullOrEmpty(workDir))
                        psi.WorkingDirectory = workDir;
                    System.Diagnostics.Process.Start(psi);
                }
                catch { /* bad path or blocked launch: the user owns this; never crash the engine */ }
            });
        }

        /// <summary>Enumerates every per-device PlayStationSlotConfig
        /// on the slot. Macro lightbar actions are slot-level: macro is
        /// to the left of the device dropdown, so a macro's color /
        /// mode / clear push uniformly to every assigned device. The
        /// Lighting tab (right of the dropdown) is per-device — a mode
        /// change pushed by a macro re-renders each device using its
        /// own LightbarMode / palette / colors. Falls back to the
        /// anchor slot config when the per-device dictionary hasn't
        /// been wired yet (early startup).</summary>
        private System.Collections.Generic.IEnumerable<PlayStationSlotConfig> EnumerateSlotPlayStationConfigs(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= MaxPads) yield break;
            var perDev = _perDevicePlayStationConfigs[slotIndex];
            if (perDev != null && perDev.Count > 0)
            {
                foreach (var kvp in perDev)
                {
                    if (kvp.Value != null) yield return kvp.Value;
                }
                yield break;
            }
            var anchor = _playStationConfigs[slotIndex];
            if (anchor != null) yield return anchor;
        }

        /// <summary>Pushes a LightbarColor action's override into every
        /// per-device PSConfig on the target slot. Resolves the color
        /// source (Fixed / RandomHue / PaletteStep) once and writes the
        /// same RGB + Hold/Fade timing to every device — macro is
        /// slot-level so all assigned devices flash uniformly. Sticky
        /// uses <c>DateTime.MaxValue</c> for the expiry; Reactive uses
        /// now + hold + fade.</summary>
        private void ApplyLightbarColorAction(MacroItem macro, MacroAction action)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            // Resolve the color ONCE for the whole slot so every device
            // gets the same flash. Sticky always uses Fixed; Reactive resolves
            // by the color source (PaletteStep falls back to the slot's palette
            // when the per-macro palette is empty).
            byte r, g, b;
            if (action.LightbarHoldMode == MacroLightbarHoldMode.Sticky)
            {
                r = action.LightbarR;
                g = action.LightbarG;
                b = action.LightbarB;
            }
            else
            {
                int ci = action.LightbarCycleIndex;
                (r, g, b) = ResolveOverrideLightbarColor(slotIndex, action.LightbarColorSource,
                    action.LightbarR, action.LightbarG, action.LightbarB, action.LightbarPaletteCsv,
                    ref ci, slotPaletteFallback: true);
                action.LightbarCycleIndex = ci;
            }

            DateTime now = DateTime.UtcNow;
            DateTime holdEnd, expiresAt;
            if (action.LightbarHoldMode == MacroLightbarHoldMode.Sticky)
            {
                holdEnd = DateTime.MaxValue;
                expiresAt = DateTime.MaxValue;
            }
            else
            {
                int holdMs = Math.Max(action.LightbarHoldMs, 0);
                int fadeMs = Math.Max(action.LightbarFadeMs, 0);
                // Force at least 1 ms so the override registers as active
                // — Hold=0/Fade=0 would otherwise expire on the same tick.
                if (holdMs == 0 && fadeMs == 0) holdMs = 1;
                holdEnd = now.AddMilliseconds(holdMs);
                expiresAt = holdEnd.AddMilliseconds(fadeMs);
            }

            // Slot-level fan-out: every per-device PSConfig gets the
            // same color and timing. Devices that aren't assigned to a
            // physical Sony device still receive the override write —
            // harmless, the dispatcher's device loop only writes to
            // online Sony devices.
            foreach (var psCfg in EnumerateSlotPlayStationConfigs(slotIndex))
            {
                psCfg.MacroOverrideR = r;
                psCfg.MacroOverrideG = g;
                psCfg.MacroOverrideB = b;
                psCfg.MacroOverrideHoldMode = action.LightbarHoldMode;
                psCfg.MacroOverrideStartUtc = now;
                psCfg.MacroOverrideHoldEndUtc = holdEnd;
                psCfg.MacroOverrideExpiresAtUtc = expiresAt;
            }
        }

        /// <summary>Resolves the RGB for an override lightbar fire from a color source,
        /// shared by the macro lightbar action and the steering-lock lightbar cue. Fixed →
        /// the given RGB; RandomHue → a fresh random hue; PaletteStep → the next entry of
        /// <paramref name="paletteCsv"/>, advancing <paramref name="cycleIndex"/>. When the
        /// palette is empty: macros (<paramref name="slotPaletteFallback"/> = true) fall back
        /// to the slot's Lighting-tab palette; a dedicated palette (steering) does not — it
        /// resolves to off so the empty selection is visibly inert.</summary>
        private (byte r, byte g, byte b) ResolveOverrideLightbarColor(
            int slotIndex, MacroLightbarColorSource source,
            byte fr, byte fg, byte fb, string paletteCsv, ref int cycleIndex, bool slotPaletteFallback)
        {
            if (source == MacroLightbarColorSource.RandomHue)
            {
                int h = _macroLightbarRng.Next(0, 360);
                HsvToRgb(h, 1.0, 1.0, out byte rr, out byte gg, out byte bb);
                return (rr, gg, bb);
            }
            if (source == MacroLightbarColorSource.PaletteStep)
            {
                var palette = ParseMacroPaletteCsv(paletteCsv);
                if (palette.Length == 0 && slotPaletteFallback)
                {
                    foreach (var devCfg in EnumerateSlotPlayStationConfigs(slotIndex))
                    {
                        palette = devCfg.SnapshotLightbarPalette();
                        if (palette.Length > 0) break;
                    }
                }
                if (palette.Length > 0)
                {
                    int idx = (cycleIndex % palette.Length + palette.Length) % palette.Length;
                    var entry = palette[idx];
                    cycleIndex = idx + 1;
                    return (entry.R, entry.G, entry.B);
                }
                return (0, 0, 0);
            }
            return (fr, fg, fb); // Fixed
        }

        /// <summary>Pushes a Rumble action's override into the slot's
        /// <see cref="MacroRumbleOverride"/>. Reactive holds use the
        /// action's <c>RumbleHoldMs</c> + <c>RumbleFadeMs</c> for the
        /// hold + decay-fade window; Sticky holds latch at full strength
        /// until a <see cref="MacroActionType.RumbleStop"/> action runs.
        /// Both motors are scaled per <c>RumbleStrengthLeft/Right</c>
        /// (0..100 percent).</summary>
        private void ApplyRumbleAction(MacroItem macro, MacroAction action)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            byte left = (byte)Math.Clamp(action.RumbleStrengthLeft, 0, 100);
            byte right = (byte)Math.Clamp(action.RumbleStrengthRight, 0, 100);
            var ovr = MacroRumbleOverrides[slotIndex];

            if (action.RumbleHoldMode == MacroRumbleHoldMode.Sticky)
            {
                ovr.FireSticky(left, right);
            }
            else
            {
                int holdMs = Math.Max(action.RumbleHoldMs, 0);
                int fadeMs = Math.Max(action.RumbleFadeMs, 0);
                // Mirror the lightbar Reactive minimum-1ms guard so a
                // Hold=0 / Fade=0 pulse still registers active for at
                // least one tick.
                if (holdMs == 0 && fadeMs == 0) holdMs = 1;
                ovr.FireReactive(left, right, holdMs, fadeMs);
            }
        }

        /// <summary>Trigger-channel sibling of <see cref="ApplyRumbleAction"/>
        /// (issue #102). Pushes the action's strength / hold-mode / duration into
        /// the slot's <see cref="MacroTriggerRumbleOverrides"/>, which the routing
        /// pass max-combines into the trigger channel. Reuses the same
        /// <c>RumbleStrengthLeft/Right</c> and hold fields (an action is either a
        /// main-motor or trigger rumble, never both).</summary>
        private void ApplyTriggerRumbleAction(MacroItem macro, MacroAction action)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            byte left = (byte)Math.Clamp(action.RumbleStrengthLeft, 0, 100);
            byte right = (byte)Math.Clamp(action.RumbleStrengthRight, 0, 100);
            var ovr = MacroTriggerRumbleOverrides[slotIndex];

            if (action.RumbleHoldMode == MacroRumbleHoldMode.Sticky)
            {
                ovr.FireSticky(left, right);
            }
            else
            {
                int holdMs = Math.Max(action.RumbleHoldMs, 0);
                int fadeMs = Math.Max(action.RumbleFadeMs, 0);
                if (holdMs == 0 && fadeMs == 0) holdMs = 1;
                ovr.FireReactive(left, right, holdMs, fadeMs);
            }
        }

        /// <summary>Parses "RRGGBB,RRGGBB,..." into palette entries.
        /// Mirrors <c>MacroAction.ParsePaletteCsv</c>; kept here so the
        /// Step 4b dispatch path doesn't reach into MacroAction's private
        /// parser.</summary>
        private static LightbarPaletteEntry[] ParseMacroPaletteCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<LightbarPaletteEntry>();
            var list = new System.Collections.Generic.List<LightbarPaletteEntry>();
            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Length != 6) continue;
                if (byte.TryParse(raw.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                 && byte.TryParse(raw.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                 && byte.TryParse(raw.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    list.Add(new LightbarPaletteEntry { R = r, G = g, B = b });
                }
            }
            return list.ToArray();
        }

        /// <summary>Advances the action's cycle position and writes the
        /// resulting <c>LightbarMode</c> into every per-device PSConfig
        /// on the slot. Slot-level fan-out: every assigned device
        /// switches to the same mode in lockstep, then renders that
        /// mode using its own per-device LightbarRed/G/B / palette /
        /// decay. No-op when the action's cycle list is empty.</summary>
        private void ApplyLightbarModeCycleAction(MacroItem macro, MacroAction action)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            var modes = action.ParsedCycleModes();
            if (modes.Length == 0) return;

            int idx = ((action.LightbarCycleIndex % modes.Length) + modes.Length) % modes.Length;
            LightbarMode target = modes[idx];
            foreach (var psCfg in EnumerateSlotPlayStationConfigs(slotIndex))
                psCfg.LightbarMode = target;
            action.LightbarCycleIndex = idx + 1;
        }

        // RNG for the RandomHue color source. Shared across slots —
        // the cost is one Next(0,360) per macro fire which is trivial.
        private static readonly Random _macroLightbarRng = new Random();

        /// <summary>HSV → RGB. Matches the converter in
        /// <see cref="UserEffectsDispatcher"/> so a Reactive RandomHue
        /// macro flash uses the same colour distribution as the
        /// existing InputReactive lightbar mode.</summary>
        private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double rp, gp, bp;
            if (h < 60)       { rp = c; gp = x; bp = 0; }
            else if (h < 120) { rp = x; gp = c; bp = 0; }
            else if (h < 180) { rp = 0; gp = c; bp = x; }
            else if (h < 240) { rp = 0; gp = x; bp = c; }
            else if (h < 300) { rp = x; gp = 0; bp = c; }
            else              { rp = c; gp = 0; bp = x; }
            r = (byte)Math.Round((rp + m) * 255);
            g = (byte)Math.Round((gp + m) * 255);
            b = (byte)Math.Round((bp + m) * 255);
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
        //  Custom Extended macro evaluation
        //  Mirrors EvaluateSlotMacros but operates on ExtendedRawState
        //  with uint[] button words instead of ushort Gamepad.Buttons.
        // ─────────────────────────────────────────────

        private void EvaluateSlotMacrosExtended(ref ExtendedRawState raw, MacroItem[] macros)
        {
            for (int m = 0; m < macros.Length; m++)
            {
                var macro = macros[m];
                if (macro == null || !macro.IsEnabled)
                    continue;

                // Skip macros with no trigger configured (unless Always /
                // CustomExpression mode).
                bool hasButtons = macro.UsesRawTrigger || macro.UsesCustomTrigger || macro.TriggerButtons != 0;
                if (macro.TriggerMode != MacroTriggerMode.Always &&
                    macro.TriggerMode != MacroTriggerMode.CustomExpression &&
                    !macro.UsesAxisTrigger && !macro.UsesPovTrigger && !hasButtons &&
                    !macro.UsesGestureTrigger)
                    continue;

                // Check trigger condition. Buttons, POVs, gestures, AND axes must all be active together.
                bool triggerActive;
                if (macro.TriggerMode == MacroTriggerMode.Always)
                    triggerActive = true;
                else if (macro.TriggerMode == MacroTriggerMode.CustomExpression)
                    triggerActive = EvaluateCustomExpressionTriggerExtended(macro, in raw);
                else
                {
                    bool buttonOk = true;
                    bool povOk = true;
                    bool gestureOk = true;
                    bool axisOk = true;

                    if (hasButtons)
                    {
                        if (macro.UsesRawTrigger)
                            buttonOk = CheckRawButtonTrigger(macro);
                        else if (macro.UsesCustomTrigger)
                            buttonOk = CheckCustomButtonTrigger(raw, macro);
                        else
                            buttonOk = false; // Xbox bitmask triggers don't apply to custom Extended
                    }
                    if (macro.UsesPovTrigger)
                        povOk = CheckRawPovTrigger(macro);
                    if (macro.UsesGestureTrigger)
                        gestureOk = CheckGestureTrigger(macro);
                    if (macro.UsesAxisTrigger)
                    {
                        float threshold = macro.TriggerAxisThreshold / 100f;
                        foreach (var axTarget in macro.TriggerAxisTargets)
                        {
                            if (ReadAxisAsVolumeRaw(in raw, axTarget) < threshold)
                            { axisOk = false; break; }
                        }
                    }

                    triggerActive = buttonOk && povOk && gestureOk && axisOk;
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
                    case MacroTriggerMode.CustomExpression:
                        shouldStart = triggerActive && !wasTriggerActive;
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
                    // Looping macro sounds are trigger-bound on this path:
                    // release stops them (one-shots play out).
                    SoundMacroService.StopLoopsForMacro(macro.PadIndex, macro);
                }

                if (macro.IsExecuting && macro.Actions.Count > 0)
                    ExecuteMacroActionsExtended(ref raw, macro);

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
        private static bool CheckCustomButtonTrigger(in ExtendedRawState raw, MacroItem macro)
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
        /// Executes macro actions against a ExtendedRawState (custom Extended button words).
        /// Same parallel-continuous pattern as ExecuteMacroActions.
        /// </summary>
        private void ExecuteMacroActionsExtended(ref ExtendedRawState raw, MacroItem macro)
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

        /// <summary>Executes a single continuous action for Extended raw state.</summary>
        private void ExecuteSingleActionRaw(ref ExtendedRawState raw, MacroAction action)
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

        /// <summary>Executes a sequential action for Extended raw state.</summary>
        private void ExecuteSequentialActionRaw(ref ExtendedRawState raw, MacroItem macro, MacroAction action)
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

                case MacroActionType.LightbarColor:
                    ApplyLightbarColorAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.LightbarColorClear:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                    {
                        // Slot-level fan-out: clear override on every device.
                        foreach (var devCfg in EnumerateSlotPlayStationConfigs(slotIndex))
                            devCfg.ClearMacroOverride();
                    }
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.Rumble:
                    ApplyRumbleAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.RumbleStop:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                        MacroRumbleOverrides[slotIndex].Clear();
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.RumbleTrigger:
                    ApplyTriggerRumbleAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.RumbleTriggerStop:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                        MacroTriggerRumbleOverrides[slotIndex].Clear();
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.MouseRecenter:
                {
                    var m = action.CursorRecenterMode;
                    CursorControlService.Active?.RecenterCursor(
                        m != CursorRecenterMode.YOnly, m != CursorRecenterMode.XOnly);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.MouseFixPosition:
                {
                    CursorControlService.Active?.TogglePin(
                        action.CursorPinMode, action.CursorPinX, action.CursorPinY);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.MouseLimitRegion:
                {
                    CursorControlService.Active?.ToggleClamp(
                        action.CursorClampMode, action.CursorClampInsetX, action.CursorClampInsetY);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.DisconnectController:
                    ExecuteDisconnectControllerAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.RunProgram:
                    ExecuteRunProgramAction(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.PlaySound:
                {
                    // Single-frame fire like Rumble: hand the file to the
                    // sound service (non-blocking; uncached files decode on
                    // the thread pool) and advance. The macro object is the
                    // loop key so trigger release / SoundStop can stop what
                    // this macro started; looping starts are idempotent per
                    // (macro, file) so an Until-Release list restart can't
                    // stack instances.
                    SoundMacroService.Play(macro.PadIndex, macro,
                        action.SoundFilePath, action.SoundVolume, action.SoundLoop);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.SoundStop:
                {
                    SoundMacroService.StopSlot(macro.PadIndex);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.LightbarModeSet:
                {
                    int slotIndex = macro.PadIndex;
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                    {
                        // Slot-level fan-out: switch every device's mode.
                        // Each device renders the new mode using its OWN
                        // per-device colors / palette.
                        foreach (var devCfg in EnumerateSlotPlayStationConfigs(slotIndex))
                            ApplyLightbarModeSetMigrated(devCfg, action.LightbarTargetMode);
                    }
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.LightbarModeCycle:
                    ApplyLightbarModeCycleAction(macro, action);
                    AdvanceAction(macro);
                    break;
            }
        }

        /// <summary>Apply a macro LightbarModeSet target to one device's
        /// config, translating the legacy InputReactive* values into
        /// the v3.2+ overlay model: LightbarMode parked at the
        /// PlayerNumber default, InputReactiveMode = corresponding
        /// overlay variant. The parking mirrors the loader's LightingRev
        /// migration in SettingsService.ApplyPlayStationConfigData: the
        /// v4 idle base is the player floor, and a macro that wants the
        /// old black base can target the now-deliberate Off instead.
        /// Non-reactive base modes (including Off = hard black and
        /// PlayerNumber = the floor) set LightbarMode directly and
        /// leave the overlay alone, so users can layer macros. A macro
        /// can switch the base to Rainbow while the user's overlay
        /// continues to flash on each press.</summary>
        private static void ApplyLightbarModeSetMigrated(PlayStationSlotConfig devCfg, LightbarMode target)
        {
            switch (target)
            {
                case LightbarMode.InputReactive:
                    devCfg.InputReactiveMode = InputReactiveMode.Random;
                    devCfg.LightbarMode = LightbarMode.PlayerNumber;
                    break;
                case LightbarMode.InputReactiveCycle:
                    devCfg.InputReactiveMode = InputReactiveMode.Cycle;
                    devCfg.LightbarMode = LightbarMode.PlayerNumber;
                    break;
                case LightbarMode.InputReactiveFixed:
                    devCfg.InputReactiveMode = InputReactiveMode.Fixed;
                    devCfg.LightbarMode = LightbarMode.PlayerNumber;
                    break;
                default:
                    devCfg.LightbarMode = target;
                    break;
            }
        }

        /// <summary>Applies an AxisSet action to a ExtendedRawState.</summary>
        private static void ApplyAxisActionRaw(ref ExtendedRawState raw, MacroAction action)
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
        /// Reads the current value of a source axis from a ExtendedRawState
        /// and returns it as a 0.0–1.0 float suitable for volume.
        /// </summary>
        internal static float ReadAxisAsVolumeRaw(in ExtendedRawState raw, MacroAxisTarget target)
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

        // Accumulated macro mouse-move delta. Written by the poll thread
        // (SendMouseMoveInput, ~1000 Hz), drained by the dedicated mouse-injector
        // thread (FlushPendingMouseMove). Keeping SendInput OFF the poll thread is
        // what stops a mouse-move macro from collapsing the 1000 Hz input loop:
        // injected mouse movement is processed synchronously (it traverses every
        // process's low-level mouse hook chain plus the cursor/DWM update), so a
        // per-poll SendInput can cost milliseconds and drag the loop to ~200 Hz.
        // Two MouseMove actions (X + Y) used to fire two SendInputs per poll; now
        // they just add into these fields and one flush injects the batched delta.
        private static int _pendingMouseDx;
        private static int _pendingMouseDy;
        private static int _pendingScroll;
        private static readonly INPUT[] _mouseInjectBuf = new INPUT[1];

        /// <summary>Poll thread: accumulate the desired mouse delta. Lock-free and
        /// syscall-free, so a mouse-move macro adds no per-poll SendInput cost. The
        /// injector thread flushes the accumulated delta with one SendInput.</summary>
        private static void SendMouseMoveInput(int dx, int dy)
        {
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no mouse
            if (dx == 0 && dy == 0) return;
            Interlocked.Add(ref _pendingMouseDx, dx);
            Interlocked.Add(ref _pendingMouseDy, dy);
        }

        /// <summary>Injector thread only: drain the accumulated mouse move + scroll
        /// deltas and inject them off the poll thread. Every MouseMove / MouseScroll
        /// action's contribution since the last flush is batched, so the expensive
        /// SendInput syscall runs here on its own cadence instead of N times per poll
        /// on the rate-holding thread. Reuses one INPUT[] (no per-flush alloc).
        /// Single-threaded (the injector loop), so the shared buffer is safe.</summary>
        internal static void FlushPendingMouseInput()
        {
            int dx = Interlocked.Exchange(ref _pendingMouseDx, 0);
            int dy = Interlocked.Exchange(ref _pendingMouseDy, 0);
            if (dx != 0 || dy != 0)
            {
                _mouseInjectBuf[0] = new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } }
                };
                SendInput(1, _mouseInjectBuf, Marshal.SizeOf<INPUT>());
            }

            int scroll = Interlocked.Exchange(ref _pendingScroll, 0);
            if (scroll != 0)
            {
                _mouseInjectBuf[0] = new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion { mi = new MOUSEINPUT { mouseData = (uint)scroll, dwFlags = MOUSEEVENTF_WHEEL } }
                };
                SendInput(1, _mouseInjectBuf, Marshal.SizeOf<INPUT>());
            }
        }

        private static void SendMouseButtonInput(MacroMouseButton button, bool down)
        {
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no mouse buttons
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

        /// <summary>Poll thread: accumulate the desired scroll amount. Like
        /// SendMouseMoveInput, this stays syscall-free on the poll thread; the
        /// injector thread flushes it with one SendInput so a MouseScroll macro
        /// (a continuous per-poll action) can't drop the poll rate while scrolling.</summary>
        private static void SendMouseScrollInput(int amount)
        {
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no scroll
            if (amount == 0) return;
            Interlocked.Add(ref _pendingScroll, amount);
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

        private static float ReadAxisAsMouseRaw(in ExtendedRawState raw, MacroAxisTarget target)
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
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no keystrokes
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
                    HandleGlobalMacroAction(gm);
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
                        ? CheckAxisActive(ud.InputState, entry.AxisIndex, entry.AxisThreshold, entry.AxisDirection)
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

        private static bool CheckAxisActive(Engine.CustomInputState state, int index, float threshold,
            AxisTriggerDirection direction = AxisTriggerDirection.Positive)
        {
            var axes = state.Axis;
            if (index < 0 || index >= axes.Length) return false;
            float normalized = axes[index] / 65535f;
            // Threshold is stored as the recorded position with margin.
            // Positive: axis must be above threshold (e.g., > 0.75 for stick right).
            // Negative: axis must be below threshold (e.g., < 0.25 for stick left).
            // The threshold itself already encodes the direction-appropriate value.
            return direction == AxisTriggerDirection.Positive
                ? normalized >= threshold
                : normalized <= threshold;
        }

        private void HandleGlobalMacroAction(GlobalMacroData gm)
        {
            if (gm.SwitchMode == SwitchProfileMode.ToggleWindow)
            {
                PendingToggleWindow = true;
                return;
            }

            if (gm.SwitchMode == SwitchProfileMode.ToggleVCsDisabled)
            {
                PendingToggleVCsDisabled = true;
                return;
            }

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
