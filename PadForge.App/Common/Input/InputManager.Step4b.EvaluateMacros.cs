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

            // Per-frame rebuild of the ToggleKey desired set (issue #9 wave
            // 1b): the slot evaluators below add every latched key from every
            // enabled macro, then the reconcile at the end diffs it against
            // what is currently held down. Rebuild-and-diff (instead of
            // sending inputs at flip time) is what releases a latched key
            // when its macro is disabled, deleted, or replaced by a profile
            // switch: the key simply stops appearing in the desired set.
            _desiredLatchedKeys.Clear();
            _desiredLatchedMouseButtons.Clear();

            // Menu direct bindings (#9 B-17) run BEFORE the macro pass so a
            // macro triggering on a virtual button can see and consume a
            // button a menu cell pressed this frame, exactly as it would a
            // physically-mapped button. (They previously ran after, so
            // menu-pressed buttons were invisible to same-frame macro
            // triggers, Codex audit 2026-07-16.) Keys still join the same
            // desired-set reconcile below.
            CollectMenuDirectOutputs();

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

            // Settle ToggleKey latches once per frame, after every slot has
            // contributed its desired keys. Restriction was enforced at
            // collection time (a restricted slot's latches never enter the
            // set); the emission itself must not be suppressed by whatever
            // restricted flag the LAST slot left behind, and a KeyUp must
            // always be deliverable.
            _currentMacroSlotRestricted = false;
            ReconcileLatchedKeys();
            ReconcileLatchedMouseButtons();
        }

        // ── ToggleKey latch reconciliation (issue #9 wave 1b) ──

        /// <summary>Keys the enabled macros' latched ToggleKey actions want
        /// held down this frame. Rebuilt every frame by the slot evaluators;
        /// internal for the PadForge.Tests dispatch pins. Poll thread only.</summary>
        internal readonly HashSet<ushort> _desiredLatchedKeys = new();

        /// <summary>Keys this engine currently holds logically down via the
        /// reconcile. Internal for the PadForge.Tests dispatch pins. Poll
        /// thread only (plus the engine-stop release).</summary>
        internal readonly HashSet<ushort> _latchedKeysDown = new();

        // Scratch for the removal pass (no per-frame alloc).
        private readonly List<ushort> _latchReleaseScratch = new();

        /// <summary>Diffs the desired latched-key set against what is held
        /// down and sends the boundary transitions: one KeyUp per key that
        /// left the set, one KeyDown per key that entered it. Steady-state
        /// frames send nothing (the OS keeps an injected key logically down
        /// until its KeyUp). Internal for the PadForge.Tests dispatch pins
        /// (audit #2 M4: the hold-pair engine-stop test drives the real
        /// reconcile with an inert VK).</summary>
        internal void ReconcileLatchedKeys()
        {
            if (_latchedKeysDown.Count > 0)
            {
                _latchReleaseScratch.Clear();
                foreach (var vk in _latchedKeysDown)
                    if (!_desiredLatchedKeys.Contains(vk))
                        _latchReleaseScratch.Add(vk);
                for (int i = 0; i < _latchReleaseScratch.Count; i++)
                {
                    SendKeyInput(_latchReleaseScratch[i], keyUp: true);
                    _latchedKeysDown.Remove(_latchReleaseScratch[i]);
                }
            }

            foreach (var vk in _desiredLatchedKeys)
            {
                if (_latchedKeysDown.Add(vk))
                    SendKeyInput(vk, keyUp: false);
            }
        }

        /// <summary>Releases every key the ToggleKey reconcile is holding
        /// down. Called when the polling loop exits so an engine stop (or
        /// app shutdown) never strands a latched key logically pressed in
        /// the OS.</summary>
        internal void ReleaseAllLatchedMacroKeys()
        {
            _currentMacroSlotRestricted = false; // the ups must always deliver
            if (_latchedKeysDown.Count > 0)
            {
                foreach (var vk in _latchedKeysDown)
                    SendKeyInput(vk, keyUp: true);
                _latchedKeysDown.Clear();
            }
            // v18: mouse-button latches release the same way so an engine
            // stop never strands an injected button logically down.
            if (_latchedMouseButtonsDown.Count > 0)
            {
                foreach (var b in _latchedMouseButtonsDown)
                    SendMouseButtonInput(b, down: false);
                _latchedMouseButtonsDown.Clear();
            }
            // Press fired-latches (M5) re-arm wholesale: the loop is gone,
            // so every in-flight press leg starts fresh on the next engine
            // start (and the set can't root dead actions across restarts).
            _pressDownSent.Clear();
            // #237: yield latches re-arm wholesale for the same reason
            // (and the set can't root dead actions across restarts).
            // Combo park positions are per-MacroItem volatiles; they die
            // with the VM state on profile switch / restart, and a parked
            // position surviving an idle wake is deliberate (the user's
            // combo does not lose its place because the engine idled).
            _axisYielded.Clear();
        }

        /// <summary>
        /// Evaluates all macros for a single pad slot.
        /// Instance method to allow raw button lookups via FindOnlineDeviceByInstanceGuid.
        /// Internal for the PadForge.Tests dispatch pins.
        /// </summary>
        // #237 yield gate baseline (audit 2026-07-18): the "physical
        // input" a yield-enabled write compares against is the state at
        // EVALUATOR ENTRY, before ANY macro (not just the current one)
        // wrote this frame. Captured once per slot per tick; poll-thread
        // confined. The earlier per-macro snapshot still let macro A's
        // latch write false-latch macro B's yield on a shared target.
        private Gamepad _preMacroGp;
        private readonly short[] _preMacroRawAxes = new short[6];

        internal void EvaluateSlotMacros(ref Gamepad gp, MacroItem[] macros)
        {
            _preMacroGp = gp;
            // Fresh slot-device resolution per evaluator call (#9 B-9):
            // device-free trigger entries resolve against the slot's
            // CURRENT online devices, refilled lazily on first need.
            _slotTriggerDeviceSlot = -1;

            for (int m = 0; m < macros.Length; m++)
            {
                var macro = macros[m];
                if (macro == null || !macro.IsEnabled)
                {
                    // #237: disabling a macro resets its combo park and
                    // yield latches, so a re-enable starts from the top.
                    if (macro != null && (macro.ComboResumeIndex != 0 || macro.AwaitReleaseAfterBreak
                        || macro.TriggerPressStreak != 0 || macro.TriggerHoldFired
                        || macro.TriggerHoldStartUtc != DateTime.MinValue
                        || macro.RunReleasedFireToCompletion))
                    {
                        macro.ComboResumeIndex = 0;
                        macro.AwaitReleaseAfterBreak = false;
                        // #238: a disabled macro's press chain resets too,
                        // so re-enable inside the window starts fresh. The
                        // HoldForMs transients are the same family: without
                        // the reset, disable mid-hold and re-enable while
                        // still held fired instantly, crediting the
                        // disabled span with no rising edge.
                        macro.TriggerPressStreak = 0;
                        macro.TriggerLastPressUtc = DateTime.MinValue;
                        macro.TriggerHoldStartUtc = DateTime.MinValue;
                        macro.TriggerHoldFired = false;
                        macro.RunReleasedFireToCompletion = false;
                        ClearAxisYields(macro);
                    }
                    continue;
                }

                // Skip macros with no trigger configured (unless Always /
                // CustomExpression mode. Custom always has a formula that
                // evaluates, even if the formula references no variables).
                bool hasButtons = macro.UsesRawTrigger || macro.TriggerButtons != 0;
                if (macro.TriggerMode != MacroTriggerMode.Always &&
                    macro.TriggerMode != MacroTriggerMode.CustomExpression &&
                    !macro.UsesAxisTrigger && !macro.UsesPovTrigger && !hasButtons &&
                    !macro.UsesGestureTrigger && !macro.UsesDescriptorTrigger)
                    continue;

                // Determine trigger state. Buttons, POVs, gestures, descriptors,
                // AND axes must all be active together.
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
                    bool descriptorOk = true;
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
                    if (macro.UsesDescriptorTrigger)
                        descriptorOk = CheckDescriptorTrigger(macro);
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
                    // No per-axis-target classification. Every axis index
                    // is evaluated uniformly with the entry's three flags.
                    if (axisOk)
                    {
                        var entries = macro.GetTriggerInputEntries();
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var e = entries[i];
                            if (e.AxisTarget == MacroAxisTarget.None) continue;
                            bool active;
                            if (e.DeviceGuid == Guid.Empty)
                            {
                                // Device-free entry (#9 B-9): satisfied when
                                // ANY online device on the macro's slot
                                // crosses the threshold, the macro-side
                                // mirror of the mapping engine's empty-
                                // DeviceGuid contract.
                                active = AnySlotDeviceAxisEntryActive(macro.PadIndex, e);
                            }
                            else
                            {
                                var ud = FindSlotDeviceByInstanceGuid(e.DeviceGuid, macro.PadIndex);
                                active = TriggerAxisEntryActive(ud, e);
                            }
                            if (!active) { axisOk = false; break; }
                        }
                    }

                    triggerActive = buttonOk && povOk && gestureOk && descriptorOk && axisOk;
                }

                // Shift-layer gate (translator v25, always_on_action): a
                // layer-scoped macro's trigger only counts while its layer
                // is engaged, so OnPress fires on the layer's ENGAGE edge
                // (the gated trigger rises there) and UntilRelease shapes
                // stop on disengage. Applied before the WasTriggerActive
                // latch so re-engaging the layer is a fresh rising edge.
                bool layerOpen = MacroLayerGateOpen(macro);
                if (!layerOpen) triggerActive = false;

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
                        shouldStart = !macro.IsExecuting && layerOpen;
                        break;
                    case MacroTriggerMode.CustomExpression:
                        // Rising edge of the formula result crossing 0.5,
                        // matching OnPress semantics for a synthetic boolean.
                        shouldStart = triggerActive && !wasTriggerActive;
                        break;
                    case MacroTriggerMode.HoldForMs:
                        shouldStart = EvaluateHoldForMsTrigger(macro, triggerActive, wasTriggerActive);
                        break;
                    case MacroTriggerMode.DoublePress:
                        shouldStart = EvaluateDoublePressTrigger(macro, triggerActive, wasTriggerActive);
                        break;
                    case MacroTriggerMode.TriplePress:
                        shouldStart = EvaluateTriplePressTrigger(macro, triggerActive, wasTriggerActive);
                        break;
                    case MacroTriggerMode.SinglePress:
                        // A closed shift layer voids the pending single
                        // outright: the LayerMask contract says the trigger
                        // only counts while the layer is engaged, and the
                        // deferred fire would otherwise land AFTER the
                        // layer disengaged.
                        if (!layerOpen)
                        {
                            macro.TriggerPressStreak = 0;
                            macro.TriggerLastPressUtc = DateTime.MinValue;
                            shouldStart = false;
                        }
                        else
                        {
                            shouldStart = EvaluateSinglePressTrigger(macro, triggerActive, wasTriggerActive);
                        }
                        break;
                }

                // #237 combo break: a parked sequence must not auto-resume
                // while a hold-shaped trigger is still down; the break
                // demands a fresh press. The guard opens on the first
                // inactive tick.
                if (macro.AwaitReleaseAfterBreak && !triggerActive)
                    macro.AwaitReleaseAfterBreak = false;

                // Start new execution if triggered and not already executing.
                if (shouldStart && !macro.IsExecuting && !macro.AwaitReleaseAfterBreak)
                {
                    // A starting hold-pair leg cancels its executing twin
                    // (audit #2 M6): a re-press during the twin's pending
                    // delay_end Delay would otherwise let the stale Clear
                    // fire mid-hold and release the NEW hold. Runs at
                    // start, not at the raw rising edge, so a HoldForMs
                    // press leg cancels at its threshold crossing and a
                    // pending release still legitimately ends the
                    // PREVIOUS hold while the next press sits under the
                    // threshold.
                    if (macro.PairId != 0)
                        CancelExecutingPairTwin(macros, macro);
                    macro.IsExecuting = true;
                    // A deferred single firing with the button already up
                    // must run its sequence ONE full pass: the UntilRelease
                    // stop below would otherwise kill it the same frame
                    // (the release already happened) and a quick tap ran
                    // zero actions. The flag suppresses the release-stop
                    // until the pass completes.
                    macro.RunReleasedFireToCompletion =
                        macro.TriggerMode == MacroTriggerMode.SinglePress && !triggerActive;
                    // #237: resume from a combo-break park (0 = the top).
                    macro.CurrentActionIndex = macro.ComboResumeIndex;
                    macro.ActionStartTime = DateTime.UtcNow;
                    macro.RemainingRepeats = macro.RepeatMode == MacroRepeatMode.FixedCount
                        ? macro.RepeatCount : 1;
                    ResetMouseAccumulators(macro);
                }

                // For WhileHeld + UntilRelease: stop when trigger is released.
                // Always mode never stops via trigger release.
                // Release linger (translator v22, Steam delay_end on
                // autofire): the pulse train keeps running ReleaseLingerMs
                // past the release; a re-press inside the window clears the
                // pending stop (the M6 cancel-on-re-press shape applied to
                // the stop leg).
                if (triggerActive)
                    macro.ReleaseLingerStartUtc = DateTime.MinValue;
                if (macro.IsExecuting &&
                    macro.TriggerMode != MacroTriggerMode.Always &&
                    macro.RepeatMode == MacroRepeatMode.UntilRelease &&
                    !triggerActive
                    && !macro.RunReleasedFireToCompletion
                    && !WithinReleaseLinger(macro))
                {
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                    // #237: an UntilRelease stop re-arms the combo from the
                    // top and releases any yield latches.
                    macro.ComboResumeIndex = 0;
                    ClearAxisYields(macro);
                    macro.ReleaseLingerStartUtc = DateTime.MinValue;
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

                // Toggle latches apply every frame while the macro is enabled
                // (issue #9 wave 1b), independent of IsExecuting, and AFTER
                // the consume so a latched button is never stripped as if it
                // were a trigger input.
                ApplyMacroLatches(ref gp, macro);
            }
        }

        /// <summary>Shift-layer gate for layer-scoped macros (translator
        /// v25, Steam's always_on_action). Open when the macro carries no
        /// mask (the default, every ordinary macro) or when ANY created
        /// slot's mapping set currently engages the mask. The any-slot
        /// walk covers split configs, where the twin activators live on
        /// whichever set has the layer's rows while the macro rides the
        /// Xbox slot; masks are per-import unique
        /// ("Layer_{fileId}_{presetId}"), so a cross-slot match can only
        /// be the same import's twin. Reads the engaged mask through the
        /// pure <see cref="GetEngagedLayerMask"/> (no activator re-tick).</summary>
        private static bool MacroLayerGateOpen(MacroItem macro)
        {
            string mask = macro.LayerMask;
            if (string.IsNullOrEmpty(mask)
                || string.Equals(mask, "Base", StringComparison.Ordinal))
                return true;
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null) return true; // no layer machinery: fail open
            for (int s = 0; s < sets.Length; s++)
            {
                var set = sets[s];
                if (set == null) continue;
                if (string.Equals(GetEngagedLayerMask(s, set), mask, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>Shared DoublePress trigger evaluation (translator v17,
        /// Steam's Double_Press activator) for both slot evaluators. Fires
        /// exactly once, on a rising edge whose predecessor rising edge lay
        /// within <see cref="MacroItem.TriggerDoublePressMs"/>. Press,
        /// release, press is inherent in two rising edges (the trigger must
        /// drop between them). A single press only arms. A second press
        /// outside the window re-arms as a fresh first press, and the
        /// armed pair is consumed on fire so a triple press starts a new
        /// sequence instead of firing twice. The trigger reads active
        /// through the second press's hold, so UntilRelease action shapes
        /// stop on its release, Valve's "if held on the second press, it
        /// will remain pressed" semantics.</summary>
        private static bool EvaluateDoublePressTrigger(MacroItem macro, bool triggerActive, bool wasTriggerActive)
        {
            if (!triggerActive || wasTriggerActive) return false;
            var now = DateTime.UtcNow;
            var last = macro.TriggerLastPressUtc;
            if (last != DateTime.MinValue
                && (now - last).TotalMilliseconds <= macro.TriggerDoublePressMs)
            {
                macro.TriggerLastPressUtc = DateTime.MinValue;
                return true;
            }
            macro.TriggerLastPressUtc = now;
            return false;
        }

        /// <summary>Shared TriplePress trigger evaluation (#238) for both
        /// slot evaluators. Counts rising edges whose successive presses
        /// each land within <see cref="MacroItem.TriggerDoublePressMs"/> of
        /// the previous one; the THIRD chained edge fires and consumes the
        /// chain (six fast taps fire twice, never four times). A slower
        /// press re-arms as a fresh first press. Shares the DoublePress
        /// timestamp field: a macro has exactly one of the two modes, and
        /// the streak counter is what distinguishes the chains.</summary>
        private static bool EvaluateTriplePressTrigger(MacroItem macro, bool triggerActive, bool wasTriggerActive)
        {
            if (!triggerActive || wasTriggerActive) return false;
            var now = DateTime.UtcNow;
            var last = macro.TriggerLastPressUtc;
            bool chained = last != DateTime.MinValue
                && (now - last).TotalMilliseconds <= macro.TriggerDoublePressMs;
            macro.TriggerPressStreak = chained ? macro.TriggerPressStreak + 1 : 1;
            macro.TriggerLastPressUtc = now;
            if (macro.TriggerPressStreak >= 3)
            {
                macro.TriggerPressStreak = 0;
                macro.TriggerLastPressUtc = DateTime.MinValue;
                return true;
            }
            return false;
        }

        /// <summary>Shared SinglePress trigger evaluation (#238): the
        /// DEFERRED single. Chains rising edges through the shared press
        /// window exactly like TriplePress; a chain of exactly ONE press
        /// fires once when its window expires with no follow-up (held or
        /// released), and a chain of two or more fires nothing here and
        /// resets once quiet. Lets one button carry Single + Double +
        /// Triple macros without the single firing on the chains.</summary>
        /// <summary>How far past the press window a deferred single may
        /// still fire. Live polling detects expiry within milliseconds
        /// (idle mode within ~50 ms); beyond this the arm predates an
        /// engine stop or process suspend and must not ghost-fire.</summary>
        private const int SinglePressStaleGraceMs = 250;

        private static bool EvaluateSinglePressTrigger(MacroItem macro, bool triggerActive, bool wasTriggerActive)
        {
            var now = DateTime.UtcNow;
            if (triggerActive && !wasTriggerActive)
            {
                bool chained = macro.TriggerLastPressUtc != DateTime.MinValue
                    && (now - macro.TriggerLastPressUtc).TotalMilliseconds <= macro.TriggerDoublePressMs;
                macro.TriggerPressStreak = chained ? macro.TriggerPressStreak + 1 : 1;
                macro.TriggerLastPressUtc = now;
                return false;
            }
            if (macro.TriggerLastPressUtc == DateTime.MinValue) return false;
            double elapsedMs = (now - macro.TriggerLastPressUtc).TotalMilliseconds;
            if (elapsedMs <= macro.TriggerDoublePressMs) return false;
            if (macro.TriggerPressStreak == 1)
            {
                macro.TriggerPressStreak = 0;
                macro.TriggerLastPressUtc = DateTime.MinValue;
                // Stale-fire guard: a live pipeline detects expiry within
                // a few ticks (idle mode within ~50 ms). A press whose
                // window expired long ago means the engine was stopped or
                // the process suspended mid-window; firing now would be a
                // ghost action with no input. Reset instead.
                return elapsedMs <= macro.TriggerDoublePressMs + SinglePressStaleGraceMs;
            }
            // Chain of 2+: reset without firing once the chain is quiet
            // (window expired) and the button is up, so a held third
            // press keeps its chain accounting intact.
            if (!triggerActive)
            {
                macro.TriggerPressStreak = 0;
                macro.TriggerLastPressUtc = DateTime.MinValue;
            }
            return false;
        }

        /// <summary>Shared HoldForMs trigger evaluation (issue #9 wave 1b,
        /// B-8b) for both slot evaluators. Arms the per-macro hold timer on
        /// the rising edge, fires exactly once when the continuous hold
        /// crosses <see cref="MacroItem.TriggerHoldMs"/>, and re-arms on the
        /// next press. A tap shorter than the threshold never fires.</summary>
        private static bool EvaluateHoldForMsTrigger(MacroItem macro, bool triggerActive, bool wasTriggerActive)
        {
            if (triggerActive && !wasTriggerActive)
            {
                // Rising edge: arm a fresh hold window.
                macro.TriggerHoldStartUtc = DateTime.UtcNow;
                macro.TriggerHoldFired = false;
            }
            if (triggerActive && !macro.TriggerHoldFired
                && macro.TriggerHoldStartUtc != DateTime.MinValue
                && (DateTime.UtcNow - macro.TriggerHoldStartUtc).TotalMilliseconds >= macro.TriggerHoldMs)
            {
                macro.TriggerHoldFired = true;
                return true;
            }
            return false;
        }

        /// <summary>Applies an enabled macro's volatile Toggle latches for
        /// this frame (issue #9 wave 1b): latched ToggleVcButton targets OR
        /// into the combined Gamepad exactly like a ButtonPress write, and
        /// latched ToggleKey actions contribute their parsed keys to the
        /// frame's desired latched-key set (a restricted peer's macros never
        /// contribute, the same gate every keystroke emission has).</summary>
        private void ApplyMacroLatches(ref Gamepad gp, MacroItem macro)
        {
            var actions = macro.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;
                if (a.Type == MacroActionType.ToggleVcButton)
                {
                    if (a.VcToggleLatched && LatchPhaseOn(a))
                        gp.Buttons |= a.ButtonFlags;
                }
                else if (a.Type == MacroActionType.ToggleKey)
                {
                    if (a.KeyToggleLatched && LatchPhaseOn(a) && !_currentMacroSlotRestricted)
                    {
                        var codes = a.ParsedKeyCodes;
                        for (int k = 0; k < codes.Length; k++)
                            _desiredLatchedKeys.Add((ushort)codes[k]);
                    }
                }
                else if (a.Type == MacroActionType.ToggleVcAxis)
                {
                    // v18: a latched axis target re-writes its assert each
                    // frame, the AxisHold shape. #237 yield gate applies:
                    // the latch stays latched, only the write yields, and
                    // unlatching re-arms the yield for the next latch.
                    if (a.VcAxisToggleLatched && LatchPhaseOn(a) && !AxisWriteYields(in _preMacroGp, a))
                        ApplyAxisHoldAction(ref gp, a);
                    if (!a.VcAxisToggleLatched)
                        _axisYielded.Remove(a);
                }
                else if (a.Type == MacroActionType.ToggleMouseButton)
                {
                    // LatchPhaseOn: PulseWhileLatched turbos the latched
                    // mouse button exactly like the key / VC latches. The
                    // OFF half drops the button from the desired set, so
                    // the reconcile releases and re-presses it (M3: this
                    // branch was the one latch that ignored the flag and
                    // held solid).
                    if (a.MouseToggleLatched && LatchPhaseOn(a) && !_currentMacroSlotRestricted)
                        _desiredLatchedMouseButtons.Add(a.MouseButton);
                }
                else if (a.Type == MacroActionType.ToggleWheel)
                {
                    // v18: a latched wheel reproduces the held KbmScroll
                    // row's continuous scroll as rate-limited detents.
                    if (a.WheelToggleLatched && !_currentMacroSlotRestricted)
                    {
                        var now = DateTime.UtcNow;
                        int interval = a.IntervalMs > 0 ? a.IntervalMs : 100;
                        if ((now - a.RepeatKeyLastFireUtc).TotalMilliseconds >= interval)
                        {
                            a.RepeatKeyLastFireUtc = now;
                            ExecuteMouseWheelTap(a);
                        }
                    }
                }
            }
        }

        /// <summary>The latch's contribution phase (v18): solid ON for a
        /// plain latch, the turbo square wave when
        /// <see cref="MacroAction.PulseWhileLatched"/> composes Steam's
        /// toggle + hold_repeats.</summary>
        private static bool LatchPhaseOn(MacroAction a)
            => !a.PulseWhileLatched || TickRepeatVcButtonPhase(a);

        /// <summary>Mouse buttons the enabled macros' latched
        /// ToggleMouseButton actions want held down this frame (v18).
        /// Rebuilt every frame beside <see cref="_desiredLatchedKeys"/>;
        /// poll thread only.</summary>
        internal readonly HashSet<ViewModels.MacroMouseButton> _desiredLatchedMouseButtons = new();

        /// <summary>Mouse buttons this engine currently holds down via the
        /// reconcile (v18).</summary>
        internal readonly HashSet<ViewModels.MacroMouseButton> _latchedMouseButtonsDown = new();

        /// <summary>Sequential press legs whose Down has been sent for the
        /// current pass (M5): the KeyPress / MouseButtonPress one-shot
        /// latch, the CycleTapList CycleInjectionFired pattern. The old
        /// actionElapsed &lt; 1 convention swallowed the Down whenever a
        /// loaded frame arrived later than 1 ms after the action became
        /// current (a Delay leg in front made that the common case), while
        /// the Up at DurationMs still fired. Keyed by action reference
        /// because the latch cannot live on MacroAction itself (that DTO's
        /// file belongs to the macro editor batch). Entries are removed on
        /// completion, on macro restart, and on engine stop. Poll thread
        /// only. Internal for the PadForge.Tests dispatch pins.</summary>
        internal readonly HashSet<ViewModels.MacroAction> _pressDownSent = new();

        private readonly List<ViewModels.MacroMouseButton> _mouseLatchReleaseScratch = new();

        /// <summary>Mouse-button twin of <see cref="ReconcileLatchedKeys"/>
        /// (v18): one up per button that left the desired set, one down per
        /// button that entered it.</summary>
        private void ReconcileLatchedMouseButtons()
        {
            if (_latchedMouseButtonsDown.Count > 0)
            {
                _mouseLatchReleaseScratch.Clear();
                foreach (var b in _latchedMouseButtonsDown)
                    if (!_desiredLatchedMouseButtons.Contains(b))
                        _mouseLatchReleaseScratch.Add(b);
                for (int i = 0; i < _mouseLatchReleaseScratch.Count; i++)
                {
                    SendMouseButtonInput(_mouseLatchReleaseScratch[i], down: false);
                    _latchedMouseButtonsDown.Remove(_mouseLatchReleaseScratch[i]);
                }
            }
            foreach (var b in _desiredLatchedMouseButtons)
            {
                if (_latchedMouseButtonsDown.Add(b))
                    SendMouseButtonInput(b, down: true);
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

        // ── Device-free trigger entries (#9 B-9) ──
        //
        // A TriggerInputEntry with Guid.Empty means "the device on the
        // macro's slot", the macro-side mirror of the mapping engine's
        // empty MappingSource.DeviceGuid contract (the Workshop translator
        // emits it on every binding). Where a mapping row evaluates against
        // every device feeding the slot and max/OR-combines, a device-free
        // entry is satisfied when ANY online device on the macro's slot
        // satisfies it. A slot with no online devices satisfies nothing,
        // matching the offline-concrete-device behavior.
        //
        // The slot's devices are resolved once per evaluator call into
        // scratch arrays (poll thread only, no steady-state allocation).
        // The guid snapshot happens under the UserSettings lock and the
        // device resolution OUTSIDE it: FindOnlineDeviceByInstanceGuid
        // takes UserDevices.SyncRoot, and the lock order is UserDevices
        // before UserSettings, never nested the other way.

        private Guid[] _slotTriggerGuidScratch = new Guid[8];
        private Engine.Data.UserDevice[] _slotTriggerDeviceScratch = new Engine.Data.UserDevice[8];
        private int _slotTriggerDeviceCount;
        private int _slotTriggerDeviceSlot = -1;

        /// <summary>Fills the scratch with the slot's online devices on
        /// first need per evaluator call (the evaluators reset
        /// <see cref="_slotTriggerDeviceSlot"/> on entry) and returns the
        /// count. Repeat calls for the same slot re-serve the fill.</summary>
        private int EnsureSlotTriggerDevices(int slotIndex)
        {
            if (_slotTriggerDeviceSlot == slotIndex) return _slotTriggerDeviceCount;
            _slotTriggerDeviceSlot = slotIndex;
            _slotTriggerDeviceCount = 0;

            var settings = SettingsManager.UserSettings;
            if (settings == null) return 0;
            int guidCount = 0;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us.MapTo != slotIndex) continue;
                    if (guidCount == _slotTriggerGuidScratch.Length)
                        Array.Resize(ref _slotTriggerGuidScratch, _slotTriggerGuidScratch.Length * 2);
                    _slotTriggerGuidScratch[guidCount++] = us.InstanceGuid;
                }
            }

            for (int i = 0; i < guidCount; i++)
            {
                var ud = FindOnlineDeviceByInstanceGuid(_slotTriggerGuidScratch[i]);
                if (ud == null || !ud.IsOnline || ud.InputState == null) continue;
                if (_slotTriggerDeviceCount == _slotTriggerDeviceScratch.Length)
                    Array.Resize(ref _slotTriggerDeviceScratch, _slotTriggerDeviceScratch.Length * 2);
                _slotTriggerDeviceScratch[_slotTriggerDeviceCount++] = ud;
            }
            return _slotTriggerDeviceCount;
        }

        /// <summary>Per-device axis trigger entry test, extracted from the
        /// Gamepad-path inline loop so the device-free resolution can run
        /// the SAME math against each slot device. Semantics unchanged:
        /// the entry's Invert / HalfAxis / Bidirectional / DeadZone flags
        /// applied uniformly to any axis index, exactly as before.</summary>
        private static bool TriggerAxisEntryActive(Engine.Data.UserDevice ud, MacroItem.TriggerInputEntry e)
        {
            if (ud == null || !ud.IsOnline || ud.InputState?.Axis == null) return false;
            int axIdx = AxisTargetToDeviceIndex(e.AxisTarget);
            if (axIdx < 0 || axIdx >= ud.InputState.Axis.Length) return false;

            int av = ud.InputState.Axis[axIdx];
            double thresh = Math.Max(e.DeadZone, 1) / 100.0;
            if (e.HalfAxis)
            {
                if (e.Bidirectional)
                {
                    // Either side of center past deadzone counts:
                    // |av − 32768| > 32767 * thresh. Invert is
                    // irrelevant here (mirroring around center
                    // covers both directions already).
                    int delta = av - 32768;
                    if (delta < 0) delta = -delta;
                    return delta > (int)(32767 * thresh);
                }
                if (e.Invert)
                    return av < (int)(32767 * (1.0 - thresh));
                return av > (int)(32768 + 32767 * thresh);
            }
            int hi = (int)(thresh * 65535);
            if (e.Invert)
                return av < 65535 - hi;
            return av > hi;
        }

        private bool AnySlotDeviceAxisEntryActive(int slotIndex, MacroItem.TriggerInputEntry e)
        {
            int n = EnsureSlotTriggerDevices(slotIndex);
            for (int i = 0; i < n; i++)
                if (TriggerAxisEntryActive(_slotTriggerDeviceScratch[i], e)) return true;
            return false;
        }

        private bool AnySlotDeviceButtonDown(int slotIndex, int rawButton)
        {
            int n = EnsureSlotTriggerDevices(slotIndex);
            for (int i = 0; i < n; i++)
            {
                var btns = _slotTriggerDeviceScratch[i].InputState?.Buttons;
                if (btns != null && rawButton >= 0 && rawButton < btns.Length && btns[rawButton])
                    return true;
            }
            return false;
        }

        private bool AnySlotDevicePovActive(int slotIndex, int povIdx, int targetCd)
        {
            int n = EnsureSlotTriggerDevices(slotIndex);
            for (int i = 0; i < n; i++)
            {
                var povs = _slotTriggerDeviceScratch[i].InputState?.Povs;
                if (povs == null || povIdx < 0 || povIdx >= povs.Length || povs[povIdx] < 0)
                    continue;
                int diff = Math.Abs(povs[povIdx] - targetCd);
                if (diff > 18000) diff = 36000 - diff;
                if (diff <= 2250) return true;
            }
            return false;
        }

        /// <summary>Device-free gesture entry test: the entry fires when
        /// the gesture is asserted for ANY online device on the slot. Uses
        /// the devices' cached guid strings so the 1 kHz path stays
        /// allocation-free like the concrete-guid path.</summary>
        private bool AnySlotDeviceGestureFired(int slotIndex, MacroItem.TriggerInputEntry e)
        {
            int n = EnsureSlotTriggerDevices(slotIndex);
            if (e.GestureDescriptor.StartsWith("Mouse Gesture ", StringComparison.Ordinal))
            {
                var mouseProvider = PadForge.Engine.Common.Mapping.SourceCoercion.MouseGestureFiredProvider;
                if (mouseProvider == null) return false;
                if (!TryResolveMouseGestureKey(e.GestureDescriptor, out string mgKey)) return false;
                for (int i = 0; i < n; i++)
                    if (mouseProvider(slotIndex, _slotTriggerDeviceScratch[i].InstanceGuidString, mgKey))
                        return true;
                return false;
            }

            var provider = PadForge.Engine.Common.Mapping.SourceCoercion.TouchpadGestureFiredProvider;
            if (provider == null) return false;
            if (!e.TryGetGestureParts(out int padIdx, out string gestureName)) return false;
            for (int i = 0; i < n; i++)
                if (provider(slotIndex, _slotTriggerDeviceScratch[i].InstanceGuidString, padIdx, gestureName))
                    return true;
            return false;
        }

        /// <summary>Resolves a "Mouse Gesture {buttonIndex} {Name}"
        /// descriptor to the recognizer's precomposed fired-set key, so
        /// the 1 kHz path never composes strings. Extracted from the
        /// gesture checker's inline parse so the device-free loop shares
        /// it.</summary>
        private static bool TryResolveMouseGestureKey(string desc, out string mgKey)
        {
            mgKey = null;
            const int prefixLen = 14; // "Mouse Gesture ".Length
            if (desc.Length < prefixLen + 3) return false;
            int btn = desc[prefixLen] - '0';
            if (btn < 0 || btn >= PadForge.Engine.Mouse.MouseGestureContext.ButtonCount
                || desc[prefixLen + 1] != ' ') return false;
            int gIdx =
                desc.EndsWith(" Left", StringComparison.Ordinal) ? 0 :
                desc.EndsWith(" Right", StringComparison.Ordinal) ? 1 :
                desc.EndsWith(" Up", StringComparison.Ordinal) ? 2 :
                desc.EndsWith(" Down", StringComparison.Ordinal) ? 3 :
                desc.EndsWith(" Click", StringComparison.Ordinal) ? 4 : -1;
            if (gIdx < 0) return false;
            mgKey = PadForge.Engine.Mouse.MouseGestureRecognizer.Keys[btn][gIdx];
            return true;
        }

        // ── Descriptor trigger entries (#9 B-9) ──

        /// <summary>Threshold percent handed to the descriptor-entry button
        /// read as the global fallback (the entry's cached MappingSource
        /// leaves DeadZone unset). 50 matches the mapping engine's
        /// MappingSource.DeadZone default and the legacy axis-trigger
        /// default, so a descriptor trigger fires where a mapping row with
        /// default deadzone would. Gyro ignores it and keeps the engine's
        /// own rate threshold.</summary>
        private const int DescriptorTriggerThresholdPercent = 50;

        /// <summary>
        /// Checks whether every descriptor entry on the macro's trigger is
        /// currently active (#9 B-9). Evaluated through the SAME reader the
        /// mapping grid uses (SourceCoercion.EvaluateForButtonTarget), so
        /// abstract "Gamepad ..." spellings canonicalize inside the read and
        /// the gyro / touchpad families get the identical per-(device, slot)
        /// tuning, thresholds, and engage gates a mapping row gets. Same
        /// multi-device AND shape as the button / POV / gesture checkers;
        /// device-free entries resolve per slot device.
        /// </summary>
        private bool CheckDescriptorTrigger(MacroItem macro)
        {
            var entries = macro.GetTriggerInputEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var src = e.DescriptorSource;
                if (src == null) continue;
                if (e.DeviceGuid == Guid.Empty)
                {
                    if (!AnySlotDeviceDescriptorActive(macro.PadIndex, src)) return false;
                }
                else
                {
                    var ud = FindSlotDeviceByInstanceGuid(e.DeviceGuid, macro.PadIndex);
                    if (ud == null || !ud.IsOnline || ud.InputState == null) return false;
                    if (!PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForButtonTarget(
                            ud.InputState, src, DescriptorTriggerThresholdPercent,
                            macro.PadIndex, ud.InstanceGuidString))
                        return false;
                }
            }
            return true;
        }

        private bool AnySlotDeviceDescriptorActive(int slotIndex, PadForge.Engine.Data.MappingSource src)
        {
            int n = EnsureSlotTriggerDevices(slotIndex);
            for (int i = 0; i < n; i++)
            {
                var ud = _slotTriggerDeviceScratch[i];
                if (PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForButtonTarget(
                        ud.InputState, src, DescriptorTriggerThresholdPercent,
                        slotIndex, ud.InstanceGuidString))
                    return true;
            }
            return false;
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
                    if (e.DeviceGuid == Guid.Empty)
                    {
                        // Device-free entry (#9 B-9): ANY online device on
                        // the macro's slot holding the button satisfies it.
                        if (!AnySlotDeviceButtonDown(macro.PadIndex, e.RawButton)) return false;
                        continue;
                    }
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
                    if (e.DeviceGuid == Guid.Empty)
                    {
                        // Device-free entry (#9 B-9): ANY online device on
                        // the macro's slot in the sector satisfies it.
                        if (!AnySlotDevicePovActive(macro.PadIndex, idx, targetCd)) return false;
                        continue;
                    }
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
                if (e.DeviceGuid == Guid.Empty)
                {
                    // Device-free entry (#9 B-9): the gesture must be
                    // asserted for ANY online device on the macro's slot.
                    // The provider lookups run with each candidate device's
                    // resolved guid, never the bare empty guid (the
                    // providers are keyed by a concrete (slot, device, pad)
                    // triple, so the bare form always missed; same seam the
                    // 1087f9bb mapping fix closed). Online gating is
                    // inherent: only online slot devices are enumerated.
                    if (!AnySlotDeviceGestureFired(macro.PadIndex, e)) return false;
                    continue;
                }
                // Same online/assignment gate the button and POV checkers
                // apply. Without it a disconnect mid-touch leaves the
                // device's gesture context frozen with the held spot key
                // still in the fired set (Step 2 stops ticking offline
                // devices, and release only happens inside
                // GestureRecognizer.Update), so the macro would latch
                // active forever on a controller that is gone.
                var ud = FindSlotDeviceByInstanceGuid(e.DeviceGuid, macro.PadIndex);
                if (ud == null || !ud.IsOnline) return false;
                // Mouse gestures (issue #200) share the entry shape but a
                // different provider and no pad index. Same online gate
                // above (a frozen offline context must not latch).
                if (e.GestureDescriptor.StartsWith("Mouse Gesture ", StringComparison.Ordinal))
                {
                    var mouseProvider = PadForge.Engine.Common.Mapping.SourceCoercion.MouseGestureFiredProvider;
                    if (mouseProvider == null) return false;
                    // "Mouse Gesture {buttonIndex} {Name}". The fired key is
                    // looked up from the recognizer's precomposed table so
                    // this 1 kHz path never composes strings.
                    if (!TryResolveMouseGestureKey(e.GestureDescriptor, out string mgKey)) return false;
                    if (!mouseProvider(macro.PadIndex, e.DeviceGuidString, mgKey))
                        return false;
                    continue;
                }
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
                 or MacroActionType.MouseMove or MacroActionType.MouseScroll
                 or MacroActionType.RepeatKeyWhileHeld
                 or MacroActionType.RepeatVcButtonWhileHeld
                 or MacroActionType.RepeatVcAxisWhileHeld;

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
                (macro.RepeatMode == MacroRepeatMode.UntilRelease
                 && !macro.RunReleasedFireToCompletion))
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
                // #237: normal completion re-arms the combo from the top
                // and releases any yield latches.
                macro.ComboResumeIndex = 0;
                macro.RunReleasedFireToCompletion = false;
                ClearAxisYields(macro);
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
                case MacroActionType.RepeatKeyWhileHeld:
                    ExecuteRepeatKeyWhileHeld(action);
                    break;
                case MacroActionType.RepeatVcButtonWhileHeld:
                    // Turbo for a VC button (issue #9 wave 1b): the ON half of
                    // the square wave ORs the target into the combined output
                    // exactly like a ButtonPress; the OFF half writes nothing,
                    // so the button reads released (gp is rebuilt per frame).
                    if (TickRepeatVcButtonPhase(action))
                        gp.Buttons |= action.ButtonFlags;
                    break;
                case MacroActionType.RepeatVcAxisWhileHeld:
                    // Axis turbo (v18): the same square wave asserting an
                    // axis-natured target (trigger pull, stick direction)
                    // on the ON half. gp is rebuilt per frame, so the OFF
                    // half reads released. #237 yield gate applies like
                    // the plain hold.
                    if (TickRepeatVcButtonPhase(action) && !AxisWriteYields(in _preMacroGp, action))
                        ApplyAxisHoldAction(ref gp, action);
                    break;
            }
        }

        /// <summary>Continuous autofire for RepeatKeyWhileHeld (issue #9): while the
        /// macro trigger is held this runs every frame, and once the per-action
        /// interval has elapsed it sends one full KeyDown+KeyUp pulse for each
        /// parsed key. The timing state lives on the action (default MinValue) so
        /// the first held frame fires immediately, then firing is rate-limited to
        /// one pulse per <see cref="MacroAction.IntervalMs"/>.</summary>
        private static void ExecuteRepeatKeyWhileHeld(MacroAction action)
        {
            var now = DateTime.UtcNow;
            if ((now - action.RepeatKeyLastFireUtc).TotalMilliseconds < action.IntervalMs)
                return;
            var keyCodes = action.ParsedKeyCodes;
            if (keyCodes.Length == 0) return;
            action.RepeatKeyLastFireUtc = now;
            for (int k = 0; k < keyCodes.Length; k++)
                SendKeyInput((ushort)keyCodes[k], keyUp: false);
            for (int k = keyCodes.Length - 1; k >= 0; k--)
                SendKeyInput((ushort)keyCodes[k], keyUp: true);
        }

        /// <summary>Advances the RepeatVcButtonWhileHeld square wave (issue #9
        /// wave 1b) and returns the current phase: true while the target
        /// button should be written this frame. 50 % duty cycle with period
        /// <see cref="MacroAction.IntervalMs"/>, so a game polling at any
        /// sane rate sees a full press AND a full release inside each
        /// interval (a 1-frame pulse at ~1 kHz would be invisible to a 60 Hz
        /// poll, which is why this is a phase, not a RepeatKey-style one-shot).
        /// Timing state lives on the action like <see cref="MacroAction.RepeatKeyLastFireUtc"/>;
        /// the MinValue default flips the phase ON on the first held frame.
        /// Internal for the PadForge.Tests dispatch pins.</summary>
        internal static bool TickRepeatVcButtonPhase(MacroAction action)
        {
            var now = DateTime.UtcNow;
            if ((now - action.RepeatVcLastToggleUtc).TotalMilliseconds >= action.IntervalMs * 0.5)
            {
                action.RepeatVcLastToggleUtc = now;
                action.RepeatVcPulseOn = !action.RepeatVcPulseOn;
            }
            return action.RepeatVcPulseOn;
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
                    // One-shot latch, not the actionElapsed < 1 convention
                    // (M5, the CycleTapList fired-latch pattern): a loaded
                    // frame can arrive later than 1 ms after the action
                    // became current, especially behind a Delay leg, which
                    // swallowed the Down while the Up still fired.
                    if (_pressDownSent.Add(action))
                    {
                        for (int k = 0; k < keyCodes.Length; k++)
                            SendKeyInput((ushort)keyCodes[k], keyUp: false);
                    }
                    if (actionElapsed >= action.DurationMs)
                    {
                        for (int k = keyCodes.Length - 1; k >= 0; k--)
                            SendKeyInput((ushort)keyCodes[k], keyUp: true);
                        _pressDownSent.Remove(action); // re-arm for the next pass
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

                case MacroActionType.TextBlock:
                    if (ExecuteTextBlockAction(action, actionElapsed))
                        AdvanceAction(macro);
                    break;

                case MacroActionType.Delay:
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.AxisSet:
                    ApplyAxisAction(ref gp, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.AxisHold:
                    // Timed axis assert (v15): the value is re-written every
                    // frame while the action is current, the ButtonPress
                    // shape. gp is rebuilt per frame, so once the duration
                    // elapses (or an UntilRelease macro stops) the axis
                    // reads released again like a lifted button. The #237
                    // yield gate runs BEFORE the write so the physical
                    // pipeline's value survives when the user moves.
                    if (!AxisWriteYields(in _preMacroGp, action))
                        ApplyAxisHoldAction(ref gp, action);
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.AxisAdd:
                    // Relative deflection (#237): summed with the mapped
                    // value every frame while current, the AxisHold
                    // duration shape. No yield gate: relative IS the
                    // compose-with-physical mode.
                    ApplyAxisAddAction(ref gp, action);
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.ComboBreak:
                    // #237: park the sequence after this fire; the next
                    // trigger press resumes from the following action.
                    // Hold-shaped triggers must release first (the start
                    // gate honors AwaitReleaseAfterBreak).
                    macro.ComboResumeIndex = macro.CurrentActionIndex + 1;
                    macro.AwaitReleaseAfterBreak = true;
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                    ClearAxisYields(macro);
                    break;

                case MacroActionType.MouseWheelTap:
                    // One discrete wheel detent per fire (v15): a single
                    // WHEEL_DELTA tick, horizontal when the action says so.
                    ExecuteMouseWheelTap(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.MouseNudge:
                    // One fixed-pixel cursor nudge per fire (v16): the
                    // signed delta joins the accumulate-and-flush mouse
                    // lane once, so the injector thread batches it with
                    // whatever else is pending and the poll thread stays
                    // syscall-free.
                    ExecuteMouseNudge(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.CycleTapList:
                    // Steam's Scroll Wheel List (v16): fire the NEXT step
                    // and advance the per-action index. VC-button / VC-axis
                    // parts assert for the action's DurationMs so a game
                    // poll sees them. Injection parts fire on the first
                    // frame. The executor owns the index advance (wrap /
                    // dead-end rules live there).
                    if (ExecuteCycleTapList(ref gp, action, actionElapsed))
                        AdvanceAction(macro);
                    break;

                case MacroActionType.MouseButtonPress:
                    // One-shot latch, the KeyPress rationale above (M5).
                    if (_pressDownSent.Add(action))
                        SendMouseButtonInput(action.MouseButton, down: true);
                    if (actionElapsed >= action.DurationMs)
                    {
                        SendMouseButtonInput(action.MouseButton, down: false);
                        _pressDownSent.Remove(action); // re-arm for the next pass
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
                        foreach (var devCfg in EnumerateSlotDeviceConfigs(slotIndex))
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
                        foreach (var devCfg in EnumerateSlotDeviceConfigs(slotIndex))
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

                case MacroActionType.PointerModeCycle:
                {
                    ApplyPointerModeCycleAction(macro, action);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.PointerModeSet:
                {
                    ApplyPointerModeSetAction(macro, action);
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.GuideLedBrightness:
                {
                    ApplyGuideLedBrightnessAction(macro, action);
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

                case MacroActionType.MoveMouseToScreenPosition:
                    // System-wide cursor warp (#9): one SetCursorPos to the fixed
                    // target on press. Coord is already clamped on-screen by the
                    // action's MouseX / MouseY setters.
                    CursorControlService.Active?.MoveCursorTo(action.MouseX, action.MouseY);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.DisconnectController:
                    ExecuteDisconnectControllerAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.RunProgram:
                    ExecuteRunProgramAction(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleVcButton:
                    // Flip the volatile latch (issue #9 wave 1b). The
                    // per-frame latch application in EvaluateSlotMacros
                    // writes the target button while latched, independent of
                    // the macro's execution state, so the button stays down
                    // across trigger releases until the next fire unlatches.
                    action.VcToggleLatched = !action.VcToggleLatched;
                    if (action.VcToggleLatched) ResetLatchPulsePhase(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleKey:
                    // Write the volatile key latch (issue #9 wave 1b;
                    // direction audit #2 M4): Toggle flips, the hold-pair
                    // legs Set / Clear.
                    ApplyKeyLatchWrite(macro, action);
                    AdvanceAction(macro);
                    break;

                // v18 latch family: mouse buttons, axis-natured VC targets,
                // and wheel detents write the same volatile latch shape; the
                // per-frame effect lives in ApplyMacroLatches plus the
                // mouse-button reconcile.
                case MacroActionType.ToggleMouseButton:
                    ApplyMouseLatchWrite(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleVcAxis:
                    action.VcAxisToggleLatched = !action.VcAxisToggleLatched;
                    if (action.VcAxisToggleLatched) ResetLatchPulsePhase(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleWheel:
                    action.WheelToggleLatched = !action.WheelToggleLatched;
                    if (action.WheelToggleLatched)
                        action.RepeatKeyLastFireUtc = DateTime.MinValue;
                    AdvanceAction(macro);
                    break;

                case MacroActionType.GyroRecenter:
                    ApplyGyroRecenterAction(macro);
                    AdvanceAction(macro);
                    break;
            }
        }

        /// <summary>Arms a latch's turbo phase so a fresh latch starts its
        /// pulse train on the ON half immediately (v18 pulse-while-latched:
        /// MinValue timing plus phase OFF makes the first ApplyMacroLatches
        /// tick flip it ON).</summary>
        private static void ResetLatchPulsePhase(MacroAction action)
        {
            if (!action.PulseWhileLatched) return;
            action.RepeatVcPulseOn = false;
            action.RepeatVcLastToggleUtc = DateTime.MinValue;
        }

        /// <summary>True while an UntilRelease macro's pending stop sits
        /// inside its release-linger window (translator v22, Steam's
        /// activator delay_end on autofire). Arms the window on the first
        /// released tick; the caller clears
        /// <see cref="MacroItem.ReleaseLingerStartUtc"/> on every
        /// trigger-active tick, which is the re-press cancel.</summary>
        private static bool WithinReleaseLinger(MacroItem macro)
        {
            if (macro.ReleaseLingerMs <= 0) return false;
            var now = DateTime.UtcNow;
            if (macro.ReleaseLingerStartUtc == DateTime.MinValue)
                macro.ReleaseLingerStartUtc = now;
            return (now - macro.ReleaseLingerStartUtc).TotalMilliseconds < macro.ReleaseLingerMs;
        }

        /// <summary>Stops any executing macro that shares the starting
        /// leg's nonzero <see cref="MacroItem.PairId"/> (audit #2 M6). The
        /// materializer's hold pairs are the only PairId authors: the
        /// press leg SETs the latch, the OnRelease twin runs Delay + Clear.
        /// Without the cancel, a re-press during that Delay leaves the
        /// stale Clear in flight and it releases the NEW hold when the
        /// delay elapses. Stopping the twin mid-sequence is the
        /// UntilRelease stop shape: execution state only, latches stay
        /// where they are.</summary>
        private static void CancelExecutingPairTwin(MacroItem[] macros, MacroItem self)
        {
            for (int i = 0; i < macros.Length; i++)
            {
                var twin = macros[i];
                if (twin == null || ReferenceEquals(twin, self)) continue;
                if (twin.PairId != self.PairId || !twin.IsExecuting) continue;
                twin.IsExecuting = false;
                twin.CurrentActionIndex = 0;
                // #237: a cancelled twin's combo state dies with it.
                twin.ComboResumeIndex = 0;
                twin.AwaitReleaseAfterBreak = false;
                twin.RunReleasedFireToCompletion = false;
                ClearAxisYields(twin);
            }
        }

        /// <summary>ToggleKey latch write shared by both dispatch twins
        /// (audit #2 M4): Toggle flips (issue #9 wave 1b behavior), On
        /// sets, Off clears. The per-frame reconcile in EvaluateMacros
        /// sends the actual KeyDown / KeyUp when the desired set changes.
        /// The hold pairs Set on press and Clear on release instead of
        /// flipping, because a two-macro flip decomposition alternates or
        /// sticks; Off also clears the twin's latches, since each leg's
        /// latch state lives on its own action instance and the press
        /// leg's Set is what holds the key down.</summary>
        private void ApplyKeyLatchWrite(MacroItem macro, MacroAction action)
        {
            switch (action.LatchDirection)
            {
                case MacroLatchDirection.On:
                    // Idempotent: a re-fire (the press leg's UntilRelease
                    // sequence restart) must not restart the pulse train.
                    if (!action.KeyToggleLatched)
                    {
                        action.KeyToggleLatched = true;
                        ResetLatchPulsePhase(action);
                    }
                    break;
                case MacroLatchDirection.Off:
                    action.KeyToggleLatched = false;
                    ClearPairTwinLatches(macro, MacroActionType.ToggleKey);
                    break;
                default:
                    action.KeyToggleLatched = !action.KeyToggleLatched;
                    if (action.KeyToggleLatched) ResetLatchPulsePhase(action);
                    break;
            }
        }

        /// <summary>ToggleMouseButton twin of
        /// <see cref="ApplyKeyLatchWrite"/> (audit #2 M4), shared by both
        /// dispatch switches. The per-frame mouse-button reconcile sends
        /// the boundary transitions.</summary>
        private void ApplyMouseLatchWrite(MacroItem macro, MacroAction action)
        {
            switch (action.LatchDirection)
            {
                case MacroLatchDirection.On:
                    if (!action.MouseToggleLatched)
                    {
                        action.MouseToggleLatched = true;
                        ResetLatchPulsePhase(action);
                    }
                    break;
                case MacroLatchDirection.Off:
                    action.MouseToggleLatched = false;
                    ClearPairTwinLatches(macro, MacroActionType.ToggleMouseButton);
                    break;
                default:
                    action.MouseToggleLatched = !action.MouseToggleLatched;
                    // Arm the turbo phase like every other latch (M3): a
                    // fresh latch must start its pulse train on the ON half.
                    if (action.MouseToggleLatched) ResetLatchPulsePhase(action);
                    break;
            }
        }

        /// <summary>Clears the given latch type on every macro sharing the
        /// firing leg's nonzero PairId (audit #2 M4). The next frame's
        /// desired-set rebuild stops seeing the key / button and the
        /// reconcile sends the release. Sweeps the slot's own snapshot:
        /// hold-pair legs always ride one slot.</summary>
        private void ClearPairTwinLatches(MacroItem self, MacroActionType type)
        {
            if (self.PairId == 0) return;
            int slot = self.PadIndex;
            if ((uint)slot >= (uint)MaxPads) return;
            var macros = MacroSnapshots[slot];
            if (macros == null) return;
            for (int i = 0; i < macros.Length; i++)
            {
                var twin = macros[i];
                if (twin == null || ReferenceEquals(twin, self) || twin.PairId != self.PairId)
                    continue;
                var actions = twin.Actions;
                for (int a = 0; a < actions.Count; a++)
                {
                    var act = actions[a];
                    if (act == null || act.Type != type) continue;
                    if (type == MacroActionType.ToggleKey)
                        act.KeyToggleLatched = false;
                    else
                        act.MouseToggleLatched = false;
                }
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

        /// <summary>Enumerates every per-device DeviceSlotConfig
        /// on the slot. Macro lightbar actions are slot-level: macro is
        /// to the left of the device dropdown, so a macro's color /
        /// mode / clear push uniformly to every assigned device. The
        /// Lighting tab (right of the dropdown) is per-device — a mode
        /// change pushed by a macro re-renders each device using its
        /// own LightbarMode / palette / colors. Falls back to the
        /// anchor slot config when the per-device dictionary hasn't
        /// been wired yet (early startup).</summary>
        private System.Collections.Generic.IEnumerable<DeviceSlotConfig> EnumerateSlotDeviceConfigs(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= MaxPads) yield break;
            var perDev = _perDeviceSlotConfigs[slotIndex];
            if (perDev != null && perDev.Count > 0)
            {
                foreach (var kvp in perDev)
                {
                    if (kvp.Value != null) yield return kvp.Value;
                }
                yield break;
            }
            var anchor = _deviceSlotConfigs[slotIndex];
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
            foreach (var psCfg in EnumerateSlotDeviceConfigs(slotIndex))
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
                    foreach (var devCfg in EnumerateSlotDeviceConfigs(slotIndex))
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

        /// <summary>PointerModeCycle applier hook (issue #203). InputService
        /// points this at a dispatcher hop that writes the target mode into
        /// every IR device's PadSetting on the slot, updates the Pointer-tab
        /// VM, and marks settings dirty. The write happens ON THE DISPATCHER
        /// deliberately: the 30 Hz UI tick copies the VM's PointerMode over
        /// PadSetting (SaveViewModelToPadSetting), so a poll-thread write
        /// here could be reverted by a tick already mid-execution. Keeping
        /// both writers on the dispatcher serializes them. The lightbar
        /// sibling has no such seam because it mutates reference-shared
        /// DeviceSlotConfig objects.</summary>
        internal static Action<int, string> PointerModeCycleApply;

        /// <summary>Advances the action's pointer-mode cycle (issue #203)
        /// and hands the target mode to
        /// <see cref="PointerModeCycleApply"/> for the dispatcher-side
        /// write. Advancing the volatile index here keeps the cycle
        /// deterministic even when fires outpace the dispatcher.</summary>
        private void ApplyPointerModeCycleAction(MacroItem macro, MacroAction action)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            var modes = action.ParsedPointerCycleModes();
            if (modes.Length == 0) return;

            int idx = ((action.PointerCycleIndex % modes.Length) + modes.Length) % modes.Length;
            action.PointerCycleIndex = idx + 1;
            PointerModeCycleApply?.Invoke(slotIndex, modes[idx]);
        }

        /// <summary>Writes one fixed pointer mode through the same
        /// dispatcher-side apply as the cycle action (issue #203
        /// follow-up): the delegate is mode-agnostic, so the set action
        /// is pure target selection.</summary>
        private void ApplyPointerModeSetAction(MacroItem macro, MacroAction action)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            PointerModeCycleApply?.Invoke(slotIndex, action.NormalizedPointerSetMode());
        }

        /// <summary>Hands a macro Guide LED brightness write (#209) to the
        /// app layer: (slot index, percent 0-100). The delegate walks the
        /// slot's mapped devices on the dispatcher and routes each through
        /// the Xbox GIP writer or the Steam home-LED hint. The write is
        /// transient, never persisted into DeviceSlotConfig, so a
        /// flash-on-engage macro doesn't dirty settings on every fire.</summary>
        internal static Action<int, int> GuideLedApply;

        /// <summary>Fires the slot-level Guide LED brightness apply for a
        /// GuideLedBrightness action (#209), the LED sibling of
        /// <see cref="ApplyPointerModeSetAction"/>.</summary>
        private void ApplyGuideLedBrightnessAction(MacroItem macro, MacroAction action)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            GuideLedApply?.Invoke(slotIndex, action.GuideLedPercent);
        }

        /// <summary>App-layer hop for the GyroRecenter action's gravity
        /// re-seed (issue #9 wave 1b, B-18): (slot index). InputService
        /// points this at a handler that drops the per-device gravity
        /// low-pass entries for every device mapped to the slot, so the
        /// estimator re-seeds from the instantaneous accelerometer sample on
        /// its next tick (the same fast-converge path a fresh connect takes).
        /// Invoked SYNCHRONOUSLY on the polling thread; the handler must only
        /// touch state guarded by its own lock.</summary>
        internal static Action<int> GyroRecenterApply;

        /// <summary>Executes a GyroRecenter action (issue #9 wave 1b, B-18):
        /// zeroes every accumulated gyro-aim reference the slot holds. The
        /// engine-side resets run inline because those caches are polling-
        /// thread-only (this IS the polling thread); the gravity estimator
        /// lives in the app layer behind a lock and rides the
        /// <see cref="GyroRecenterApply"/> hook. Concretely:
        /// SourceCoercion's dual-threshold smoothing window + EMA rate
        /// history for the slot, the slot runtime's captured MotionLean
        /// neutral orientation (re-captured from the next real gravity
        /// sample), and the app-side gravity filter re-seed.</summary>
        private void ApplyGyroRecenterAction(MacroItem macro)
        {
            int slotIndex = macro.PadIndex;
            if (slotIndex < 0 || slotIndex >= MaxPads) return;

            PadForge.Engine.Common.Mapping.SourceCoercion.ResetGyroAimStateForSlot(slotIndex);
            GetSlotSourceKindRuntime(slotIndex)?.ResetMotionNeutral();
            GyroRecenterApply?.Invoke(slotIndex);
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
            foreach (var psCfg in EnumerateSlotDeviceConfigs(slotIndex))
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

        /// <summary>Resets mouse accumulators on all actions when a macro
        /// starts/restarts. Instance method because the press fired-latch
        /// set lives on the manager (see <see cref="_pressDownSent"/>).</summary>
        private void ResetMouseAccumulators(MacroItem macro)
        {
            foreach (var action in macro.Actions)
            {
                action.MouseAccumulator = 0f;
                // Press fired-latch (M5): a run interrupted between a press
                // leg's Down and its DurationMs must re-arm, or the restart
                // would skip the Down entirely.
                _pressDownSent.Remove(action);
                // TextBlock emission cursor rides the same lifecycle: a run
                // interrupted mid-string (trigger released on an Until-Release
                // macro) must start over from the first character, never
                // resume from where it stopped.
                action.TextEmitCursor = 0;
                // RepeatVcButtonWhileHeld phase rides it too (issue #9 wave
                // 1b): each fresh hold starts at the ON phase immediately
                // (MinValue flips on the first tick) instead of resuming
                // mid-wave from the previous hold.
                action.RepeatVcLastToggleUtc = DateTime.MinValue;
                action.RepeatVcPulseOn = false;
                // CycleTapList injection latch (v16): a run interrupted
                // mid-hold must not swallow the next fire's one-shot
                // parts. The cycle POSITION deliberately survives (that
                // is the whole primitive). Only the latch re-arms.
                action.CycleInjectionFired = false;
            }
        }

        /// <summary>
        /// Advances to the next action in the macro sequence.
        /// </summary>
        private static void AdvanceAction(MacroItem macro)
        {
            // #237: leaving an action re-arms its yield latch so the next
            // activation starts un-yielded.
            if (macro.CurrentActionIndex >= 0 && macro.CurrentActionIndex < macro.Actions.Count)
                _axisYielded.Remove(macro.Actions[macro.CurrentActionIndex]);
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

        /// <summary>
        /// Applies an AxisHold action to the gamepad (v15). Sticks share
        /// AxisSet's signed-short write. Triggers read AxisValue on the
        /// PULL scale (0..32767 = 0..100%) and double it onto the 0..65535
        /// output so a full pull is expressible, which the short-typed
        /// AxisSet write cannot reach; AxisSet keeps its legacy raw write
        /// untouched.
        /// </summary>
        private static void ApplyAxisHoldAction(ref Gamepad gp, MacroAction action)
        {
            switch (action.AxisTarget)
            {
                case MacroAxisTarget.LeftTrigger:
                    gp.LeftTrigger = TriggerPullFromAxisValue(action.AxisValue);
                    break;
                case MacroAxisTarget.RightTrigger:
                    gp.RightTrigger = TriggerPullFromAxisValue(action.AxisValue);
                    break;
                default:
                    ApplyAxisAction(ref gp, action);
                    break;
            }
        }

        /// <summary>0..32767 pull scale to the 0..65535 trigger output;
        /// 32767 maps exactly to 65535 (full pull).</summary>
        private static ushort TriggerPullFromAxisValue(short value)
        {
            if (value <= 0) return 0;
            int scaled = value * 2 + 1;
            return (ushort)Math.Min(scaled, 65535);
        }

        // ── #237 absolute-deflection yield (reWASD "Absolute deflection") ──
        //
        // Actions whose write is currently suppressed because the physical
        // input moved their target past the yield threshold. LATCHED for
        // the remainder of the activation ("the combo will go back to
        // zero, and now your stick will have a higher priority"), cleared
        // when the action advances, the macro stops, or the engine stops.
        private static readonly HashSet<MacroAction> _axisYielded = new();

        /// <summary>~12.5% stick deflection: above any sane deadzone's
        /// noise floor, low enough that a deliberate push always yields.</summary>
        private const int YieldStickThreshold = 4096;

        /// <summary>~12.5% trigger pull on the 0..65535 output scale.</summary>
        private const int YieldTriggerThreshold = 8192;

        /// <summary>True when this frame's macro write must be suppressed:
        /// the action opts in via <see cref="MacroAction.AxisYieldToPhysical"/>
        /// and the target's ALREADY-MAPPED value (the physical pipeline's
        /// result, present in <paramref name="gp"/> because Step 4b runs
        /// after the mapping steps) exceeds the yield threshold, or did at
        /// any earlier frame of this activation (latched).</summary>
        private static bool AxisWriteYields(in Gamepad gp, MacroAction action)
        {
            if (!action.AxisYieldToPhysical) return false;
            if (_axisYielded.Contains(action)) return true;
            bool moved = action.AxisTarget switch
            {
                MacroAxisTarget.LeftStickX => Math.Abs((int)gp.ThumbLX) > YieldStickThreshold,
                MacroAxisTarget.LeftStickY => Math.Abs((int)gp.ThumbLY) > YieldStickThreshold,
                MacroAxisTarget.RightStickX => Math.Abs((int)gp.ThumbRX) > YieldStickThreshold,
                MacroAxisTarget.RightStickY => Math.Abs((int)gp.ThumbRY) > YieldStickThreshold,
                MacroAxisTarget.LeftTrigger => gp.LeftTrigger > YieldTriggerThreshold,
                MacroAxisTarget.RightTrigger => gp.RightTrigger > YieldTriggerThreshold,
                _ => false,
            };
            if (moved) _axisYielded.Add(action);
            return moved;
        }

        /// <summary>Extended twin of <see cref="AxisWriteYields"/>: the
        /// word-array frame is signed short on every index, so one stick
        /// threshold applies to all six canonical axes.</summary>
        /// <summary>Raw yield check against the per-tick entry snapshot
        /// (see _preMacroRawAxes): earlier macros' writes this frame are
        /// never mistaken for physical input.</summary>
        private bool AxisWriteYieldsRawValueAt(int axisIndex, MacroAction action)
        {
            if (axisIndex < 0 || axisIndex >= 6) return false;
            return AxisWriteYieldsRawValue(action, _preMacroRawAxes[axisIndex]);
        }

        /// <summary>Value-based core of the raw yield test, shared with the
        /// pre-latch snapshot path. Extended TRIGGER channels rest at
        /// short.MinValue (the signed word frame,
        /// Step3.UpdateOutputStates.cs:~1324), so deflection is measured
        /// from that rest point; sticks rest at 0 and keep the plain
        /// magnitude test. Without the split, |rest| = 32768
        /// instant-latched the yield on every trigger activation.</summary>
        private static bool AxisWriteYieldsRawValue(MacroAction action, short value)
        {
            if (!action.AxisYieldToPhysical) return false;
            if (_axisYielded.Contains(action)) return true;
            bool isTrigger = action.AxisTarget == MacroAxisTarget.LeftTrigger
                || action.AxisTarget == MacroAxisTarget.RightTrigger;
            // Deflection-from-rest is already on the same 0..65535 span
            // the Gamepad path compares (audit: the earlier *2 conflated
            // the AxisAdd pull scale with this normalized span and made
            // the raw yield trip at 25% instead of 12.5%).
            bool moved = isTrigger
                ? value + 32768 > YieldTriggerThreshold
                : Math.Abs((int)value) > YieldStickThreshold;
            if (moved) _axisYielded.Add(action);
            return moved;
        }

        /// <summary>Re-arms every yield latch of the macro's actions (macro
        /// stopped, completed, or was cancelled).</summary>
        private static void ClearAxisYields(MacroItem macro)
        {
            if (_axisYielded.Count == 0 || macro?.Actions == null) return;
            for (int i = 0; i < macro.Actions.Count; i++)
                _axisYielded.Remove(macro.Actions[i]);
        }

        /// <summary>Relative axis deflection (#237, reWASD "Relative
        /// deflection"): ADDS the signed value onto whatever the mapping
        /// pipeline already wrote, clamped to the target's range. Sticks
        /// add in the signed short frame; triggers add on the pull scale
        /// (AxisValue 32767 = +100% pull, negative subtracts).</summary>
        private static void ApplyAxisAddAction(ref Gamepad gp, MacroAction action)
        {
            switch (action.AxisTarget)
            {
                case MacroAxisTarget.LeftStickX:
                    gp.ThumbLX = (short)Math.Clamp(gp.ThumbLX + action.AxisValue, short.MinValue, short.MaxValue);
                    break;
                case MacroAxisTarget.LeftStickY:
                    gp.ThumbLY = (short)Math.Clamp(gp.ThumbLY + action.AxisValue, short.MinValue, short.MaxValue);
                    break;
                case MacroAxisTarget.RightStickX:
                    gp.ThumbRX = (short)Math.Clamp(gp.ThumbRX + action.AxisValue, short.MinValue, short.MaxValue);
                    break;
                case MacroAxisTarget.RightStickY:
                    gp.ThumbRY = (short)Math.Clamp(gp.ThumbRY + action.AxisValue, short.MinValue, short.MaxValue);
                    break;
                case MacroAxisTarget.LeftTrigger:
                    gp.LeftTrigger = (ushort)Math.Clamp(gp.LeftTrigger + action.AxisValue * 2, 0, 65535);
                    break;
                case MacroAxisTarget.RightTrigger:
                    gp.RightTrigger = (ushort)Math.Clamp(gp.RightTrigger + action.AxisValue * 2, 0, 65535);
                    break;
            }
        }

        /// <summary>One discrete mouse-wheel detent (v15): AxisValue is the
        /// signed tick count (0 reads as +1; positive = up / right), routed
        /// through the same accumulate-and-flush lanes the continuous
        /// scroll actions use so the poll thread stays syscall-free.</summary>
        private static void ExecuteMouseWheelTap(MacroAction action)
        {
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no scroll
            int ticks = action.AxisValue == 0 ? 1 : action.AxisValue;
            if (action.WheelHorizontal)
                AccumulateMouseScrollHInput(ticks * 120);
            else
                AccumulateMouseScrollInput(ticks * 120);
        }

        /// <summary>One fixed-pixel cursor nudge (v16): the signed
        /// NudgeDx/NudgeDy delta joins the same accumulate-and-flush lane
        /// the continuous MouseMove action feeds, exactly once per fire.
        /// The injector thread flushes it batched, so the poll thread
        /// stays syscall-free (the lane's whole point).</summary>
        private static void ExecuteMouseNudge(MacroAction action)
        {
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no mouse
            AccumulateMouseMoveInput(action.NudgeDx, action.NudgeDy);
        }

        /// <summary>Steam's Scroll Wheel List step (v16). Executes the
        /// current step of the parsed cycle and returns true when the
        /// action may advance. Injection parts (key tap, mouse click,
        /// wheel tick) fire on the first frame only. VC-button and VC-axis
        /// parts assert every frame until DurationMs elapses, the
        /// ButtonPress shape, so a 60 Hz game poll sees the tap. The
        /// per-action <see cref="MacroAction.CycleIndex"/> advances here:
        /// wrap-on returns to step 0 past the end, wrap-off parks the
        /// index past the end and later fires produce nothing (Steam's
        /// "Wrap List - Off": no further output past the end, and the
        /// forward-only lowering has no back-step to free it, which the
        /// translator's group note covers).</summary>
        private static bool ExecuteCycleTapList(ref Gamepad gp, MacroAction action, double actionElapsed)
        {
            var steps = action.ParsedCycleSteps;
            if (steps.Length == 0) return true;
            int idx = action.CycleIndex;
            if (idx >= steps.Length)
            {
                if (!action.CycleWrap) return true; // parked past the end
                idx = 0;
                action.CycleIndex = 0;
            }
            var parts = steps[idx];
            bool held = false;
            // One-shot latch, not the actionElapsed < 1 convention: a
            // loaded frame can arrive later than 1 ms after the trigger
            // stamp, which would swallow the injection parts entirely.
            bool first = !action.CycleInjectionFired;
            action.CycleInjectionFired = true;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                switch (p.Kind)
                {
                    case 'K':
                        // One full tap, the RepeatKeyWhileHeld pulse shape.
                        if (first)
                        {
                            SendKeyInput((ushort)p.Value, keyUp: false);
                            SendKeyInput((ushort)p.Value, keyUp: true);
                        }
                        break;
                    case 'M':
                    {
                        if (first)
                        {
                            var btn = (MacroMouseButton)Math.Clamp(p.Value, 0, 4);
                            SendMouseButtonInput(btn, down: true);
                            SendMouseButtonInput(btn, down: false);
                        }
                        break;
                    }
                    case 'W':
                        if (first && !_currentMacroSlotRestricted)
                            AccumulateMouseScrollInput((p.Value == 0 ? 1 : p.Value) * 120);
                        break;
                    case 'H':
                        if (first && !_currentMacroSlotRestricted)
                            AccumulateMouseScrollHInput((p.Value == 0 ? 1 : p.Value) * 120);
                        break;
                    case 'B':
                        gp.Buttons |= (ushort)p.Value;
                        held = true;
                        break;
                    case 'A':
                        WriteCycleAxisPart(ref gp, p);
                        held = true;
                        break;
                }
            }
            if (held && actionElapsed < action.DurationMs)
                return false; // keep asserting the held parts
            action.CycleInjectionFired = false; // re-arm for the next step
            action.CycleIndex = idx + 1;
            if (action.CycleWrap && action.CycleIndex >= steps.Length)
                action.CycleIndex = 0;
            return true;
        }

        /// <summary>Writes one 'A' cycle part (v16): the AxisHold write
        /// shape (triggers on the doubled pull scale, sticks signed).</summary>
        private static void WriteCycleAxisPart(ref Gamepad gp, CycleStepPart p)
        {
            switch ((MacroAxisTarget)p.Value)
            {
                case MacroAxisTarget.LeftStickX:
                    gp.ThumbLX = p.Value2;
                    break;
                case MacroAxisTarget.LeftStickY:
                    gp.ThumbLY = p.Value2;
                    break;
                case MacroAxisTarget.RightStickX:
                    gp.ThumbRX = p.Value2;
                    break;
                case MacroAxisTarget.RightStickY:
                    gp.ThumbRY = p.Value2;
                    break;
                case MacroAxisTarget.LeftTrigger:
                    gp.LeftTrigger = TriggerPullFromAxisValue(p.Value2);
                    break;
                case MacroAxisTarget.RightTrigger:
                    gp.RightTrigger = TriggerPullFromAxisValue(p.Value2);
                    break;
            }
        }

        // ─────────────────────────────────────────────
        //  Custom Extended macro evaluation
        //  Mirrors EvaluateSlotMacros but operates on ExtendedRawState
        //  with uint[] button words instead of ushort Gamepad.Buttons.
        // ─────────────────────────────────────────────

        // Internal for the PadForge.Tests dispatch pins.
        internal void EvaluateSlotMacrosExtended(ref ExtendedRawState raw, MacroItem[] macros)
        {
            for (int k = 0; k < 6; k++)
                _preMacroRawAxes[k] = raw.Axes != null && k < raw.Axes.Length ? raw.Axes[k] : (short)0;
            // Fresh slot-device resolution per evaluator call (#9 B-9),
            // mirroring the Gamepad path.
            _slotTriggerDeviceSlot = -1;

            for (int m = 0; m < macros.Length; m++)
            {
                var macro = macros[m];
                if (macro == null || !macro.IsEnabled)
                {
                    // #237: disabling a macro resets its combo park and
                    // yield latches, so a re-enable starts from the top.
                    if (macro != null && (macro.ComboResumeIndex != 0 || macro.AwaitReleaseAfterBreak
                        || macro.TriggerPressStreak != 0 || macro.TriggerHoldFired
                        || macro.TriggerHoldStartUtc != DateTime.MinValue
                        || macro.RunReleasedFireToCompletion))
                    {
                        macro.ComboResumeIndex = 0;
                        macro.AwaitReleaseAfterBreak = false;
                        // #238: a disabled macro's press chain resets too,
                        // so re-enable inside the window starts fresh. The
                        // HoldForMs transients are the same family: without
                        // the reset, disable mid-hold and re-enable while
                        // still held fired instantly, crediting the
                        // disabled span with no rising edge.
                        macro.TriggerPressStreak = 0;
                        macro.TriggerLastPressUtc = DateTime.MinValue;
                        macro.TriggerHoldStartUtc = DateTime.MinValue;
                        macro.TriggerHoldFired = false;
                        macro.RunReleasedFireToCompletion = false;
                        ClearAxisYields(macro);
                    }
                    continue;
                }

                // Skip macros with no trigger configured (unless Always /
                // CustomExpression mode).
                bool hasButtons = macro.UsesRawTrigger || macro.UsesCustomTrigger || macro.TriggerButtons != 0;
                if (macro.TriggerMode != MacroTriggerMode.Always &&
                    macro.TriggerMode != MacroTriggerMode.CustomExpression &&
                    !macro.UsesAxisTrigger && !macro.UsesPovTrigger && !hasButtons &&
                    !macro.UsesGestureTrigger && !macro.UsesDescriptorTrigger)
                    continue;

                // Check trigger condition. Buttons, POVs, gestures, descriptors,
                // AND axes must all be active together.
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
                    bool descriptorOk = true;
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
                    if (macro.UsesDescriptorTrigger)
                        descriptorOk = CheckDescriptorTrigger(macro);
                    if (macro.UsesAxisTrigger)
                    {
                        float threshold = macro.TriggerAxisThreshold / 100f;
                        foreach (var axTarget in macro.TriggerAxisTargets)
                        {
                            if (ReadAxisAsVolumeRaw(in raw, axTarget) < threshold)
                            { axisOk = false; break; }
                        }
                    }

                    triggerActive = buttonOk && povOk && gestureOk && descriptorOk && axisOk;
                }

                // Shift-layer gate (translator v25), mirroring the Gamepad
                // path: applied before the latch so re-engage is a fresh
                // rising edge.
                bool layerOpen = MacroLayerGateOpen(macro);
                if (!layerOpen) triggerActive = false;

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
                        shouldStart = !macro.IsExecuting && layerOpen;
                        break;
                    case MacroTriggerMode.CustomExpression:
                        shouldStart = triggerActive && !wasTriggerActive;
                        break;
                    case MacroTriggerMode.HoldForMs:
                        shouldStart = EvaluateHoldForMsTrigger(macro, triggerActive, wasTriggerActive);
                        break;
                    case MacroTriggerMode.DoublePress:
                        shouldStart = EvaluateDoublePressTrigger(macro, triggerActive, wasTriggerActive);
                        break;
                    case MacroTriggerMode.TriplePress:
                        shouldStart = EvaluateTriplePressTrigger(macro, triggerActive, wasTriggerActive);
                        break;
                    case MacroTriggerMode.SinglePress:
                        // A closed shift layer voids the pending single
                        // outright: the LayerMask contract says the trigger
                        // only counts while the layer is engaged, and the
                        // deferred fire would otherwise land AFTER the
                        // layer disengaged.
                        if (!layerOpen)
                        {
                            macro.TriggerPressStreak = 0;
                            macro.TriggerLastPressUtc = DateTime.MinValue;
                            shouldStart = false;
                        }
                        else
                        {
                            shouldStart = EvaluateSinglePressTrigger(macro, triggerActive, wasTriggerActive);
                        }
                        break;
                }

                // #237 combo break guard, the Gamepad-path twin.
                if (macro.AwaitReleaseAfterBreak && !triggerActive)
                    macro.AwaitReleaseAfterBreak = false;

                if (shouldStart && !macro.IsExecuting && !macro.AwaitReleaseAfterBreak)
                {
                    // Hold-pair twin cancel (audit #2 M6), mirroring the
                    // Gamepad-path start branch.
                    if (macro.PairId != 0)
                        CancelExecutingPairTwin(macros, macro);
                    macro.IsExecuting = true;
                    // A deferred single firing with the button already up
                    // must run its sequence ONE full pass: the UntilRelease
                    // stop below would otherwise kill it the same frame
                    // (the release already happened) and a quick tap ran
                    // zero actions. The flag suppresses the release-stop
                    // until the pass completes.
                    macro.RunReleasedFireToCompletion =
                        macro.TriggerMode == MacroTriggerMode.SinglePress && !triggerActive;
                    // #237: resume from a combo-break park (0 = the top).
                    macro.CurrentActionIndex = macro.ComboResumeIndex;
                    macro.ActionStartTime = DateTime.UtcNow;
                    macro.RemainingRepeats = macro.RepeatMode == MacroRepeatMode.FixedCount
                        ? macro.RepeatCount : 1;
                    ResetMouseAccumulators(macro);
                }

                // Always mode never stops via trigger release. Release
                // linger mirrors the Gamepad-path block (translator v22).
                if (triggerActive)
                    macro.ReleaseLingerStartUtc = DateTime.MinValue;
                if (macro.IsExecuting &&
                    macro.TriggerMode != MacroTriggerMode.Always &&
                    macro.RepeatMode == MacroRepeatMode.UntilRelease &&
                    !triggerActive
                    && !macro.RunReleasedFireToCompletion
                    && !WithinReleaseLinger(macro))
                {
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                    // #237: an UntilRelease stop re-arms the combo from the
                    // top and releases any yield latches.
                    macro.ComboResumeIndex = 0;
                    ClearAxisYields(macro);
                    macro.ReleaseLingerStartUtc = DateTime.MinValue;
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

                // Toggle latches apply every frame while the macro is enabled
                // (issue #9 wave 1b), after the consume, mirroring the
                // Gamepad-path ordering.
                ApplyMacroLatchesRaw(ref raw, macro);
            }
        }

        /// <summary>Extended twin of <see cref="ApplyMacroLatches"/> (issue #9
        /// wave 1b): latched ToggleVcButton targets OR their wide button words
        /// into the raw state, latched ToggleKey actions contribute to the
        /// frame's desired latched-key set.</summary>
        private void ApplyMacroLatchesRaw(ref ExtendedRawState raw, MacroItem macro)
        {
            var actions = macro.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;
                if (a.Type == MacroActionType.ToggleVcButton)
                {
                    if (a.VcToggleLatched && LatchPhaseOn(a) && raw.Buttons != null)
                    {
                        var cw = a.CustomButtonWords;
                        for (int w = 0; w < raw.Buttons.Length && w < cw.Length; w++)
                            raw.Buttons[w] |= cw[w];
                    }
                }
                else if (a.Type == MacroActionType.ToggleKey)
                {
                    if (a.KeyToggleLatched && LatchPhaseOn(a) && !_currentMacroSlotRestricted)
                    {
                        var codes = a.ParsedKeyCodes;
                        for (int k = 0; k < codes.Length; k++)
                            _desiredLatchedKeys.Add((ushort)codes[k]);
                    }
                }
                else if (a.Type == MacroActionType.ToggleVcAxis)
                {
                    // #237 yield gate, the Gamepad-path twin's rationale.
                    if (a.VcAxisToggleLatched && LatchPhaseOn(a) && raw.Axes != null)
                    {
                        int yIdx = MacroAxisTargetToRawIndex(a.AxisTarget);
                        bool yields = yIdx >= 0 && yIdx < 6
                            && AxisWriteYieldsRawValue(a, _preMacroRawAxes[yIdx]);
                        if (!yields)
                            ApplyAxisActionRaw(ref raw, a);
                    }
                    if (!a.VcAxisToggleLatched)
                        _axisYielded.Remove(a);
                }
                else if (a.Type == MacroActionType.ToggleMouseButton)
                {
                    // LatchPhaseOn: the Gamepad-path twin's rationale (M3).
                    if (a.MouseToggleLatched && LatchPhaseOn(a) && !_currentMacroSlotRestricted)
                        _desiredLatchedMouseButtons.Add(a.MouseButton);
                }
                else if (a.Type == MacroActionType.ToggleWheel)
                {
                    if (a.WheelToggleLatched && !_currentMacroSlotRestricted)
                    {
                        var now = DateTime.UtcNow;
                        int interval = a.IntervalMs > 0 ? a.IntervalMs : 100;
                        if ((now - a.RepeatKeyLastFireUtc).TotalMilliseconds >= interval)
                        {
                            a.RepeatKeyLastFireUtc = now;
                            ExecuteMouseWheelTap(a);
                        }
                    }
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
                (macro.RepeatMode == MacroRepeatMode.UntilRelease
                 && !macro.RunReleasedFireToCompletion))
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
                // #237: normal completion re-arms the combo from the top
                // and releases any yield latches.
                macro.ComboResumeIndex = 0;
                macro.RunReleasedFireToCompletion = false;
                ClearAxisYields(macro);
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
                case MacroActionType.RepeatKeyWhileHeld:
                    ExecuteRepeatKeyWhileHeld(action);
                    break;
                case MacroActionType.RepeatVcButtonWhileHeld:
                    // Extended twin of the Gamepad-path turbo (issue #9 wave
                    // 1b): the ON half ORs the action's wide button words in,
                    // mirroring the Extended ButtonPress case.
                    if (TickRepeatVcButtonPhase(action) && raw.Buttons != null)
                    {
                        var cw = action.CustomButtonWords;
                        for (int w = 0; w < raw.Buttons.Length && w < cw.Length; w++)
                            raw.Buttons[w] |= cw[w];
                    }
                    break;
                case MacroActionType.RepeatVcAxisWhileHeld:
                    // Extended twin of the axis turbo (v18). #237 yield
                    // gate applies like the plain hold.
                    if (TickRepeatVcButtonPhase(action) && raw.Axes != null)
                    {
                        if (!AxisWriteYieldsRawValueAt(
                                MacroAxisTargetToRawIndex(action.AxisTarget), action))
                            ApplyAxisActionRaw(ref raw, action);
                    }
                    break;
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
                    // One-shot latch (M5), the Gamepad-path executor's twin.
                    if (_pressDownSent.Add(action))
                    {
                        for (int k = 0; k < keyCodes.Length; k++)
                            SendKeyInput((ushort)keyCodes[k], keyUp: false);
                    }
                    if (actionElapsed >= action.DurationMs)
                    {
                        for (int k = keyCodes.Length - 1; k >= 0; k--)
                            SendKeyInput((ushort)keyCodes[k], keyUp: true);
                        _pressDownSent.Remove(action); // re-arm for the next pass
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

                case MacroActionType.TextBlock:
                    if (ExecuteTextBlockAction(action, actionElapsed))
                        AdvanceAction(macro);
                    break;

                case MacroActionType.Delay:
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.AxisSet:
                    if (raw.Axes != null)
                        ApplyAxisActionRaw(ref raw, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.AxisHold:
                    // Extended twin of the Gamepad-path timed assert (v15):
                    // straight signed write per frame (the Extended axis
                    // frame is -32768..32767 on every index, so no trigger
                    // rescale applies here). Same #237 yield gate as the
                    // Gamepad path, on the raw word frame.
                    if (raw.Axes != null)
                    {
                        if (!AxisWriteYieldsRawValueAt(
                                MacroAxisTargetToRawIndex(action.AxisTarget), action))
                            ApplyAxisActionRaw(ref raw, action);
                    }
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.AxisAdd:
                    // Extended twin (#237): signed add in the word frame,
                    // the AxisHold duration shape.
                    if (raw.Axes != null)
                        ApplyAxisAddActionRaw(ref raw, action);
                    if (actionElapsed >= action.DurationMs)
                        AdvanceAction(macro);
                    break;

                case MacroActionType.ComboBreak:
                    // Extended twin (#237): park + await re-press, exactly
                    // the Gamepad-path semantics.
                    macro.ComboResumeIndex = macro.CurrentActionIndex + 1;
                    macro.AwaitReleaseAfterBreak = true;
                    macro.IsExecuting = false;
                    macro.CurrentActionIndex = 0;
                    ClearAxisYields(macro);
                    break;

                case MacroActionType.MouseWheelTap:
                    ExecuteMouseWheelTap(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.MouseNudge:
                    // Extended twin (v16): the nudge is pure injection, so
                    // the Gamepad-path executor applies unchanged.
                    ExecuteMouseNudge(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.CycleTapList:
                {
                    // Extended twin (v16): injection parts fire the same.
                    // The VC-button ('B', an Xbox bitmask) and VC-axis
                    // ('A', the Xbox axis frame) parts address the Xbox
                    // output shape and have no meaning on an Extended
                    // slot's word array, so they no-op here via the
                    // scratch pad the executor writes into.
                    var scratch = new Gamepad();
                    if (ExecuteCycleTapList(ref scratch, action, actionElapsed))
                        AdvanceAction(macro);
                    break;
                }

                case MacroActionType.MouseButtonPress:
                    // One-shot latch (M5), the Gamepad-path executor's twin.
                    if (_pressDownSent.Add(action))
                        SendMouseButtonInput(action.MouseButton, down: true);
                    if (actionElapsed >= action.DurationMs)
                    {
                        SendMouseButtonInput(action.MouseButton, down: false);
                        _pressDownSent.Remove(action); // re-arm for the next pass
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
                        foreach (var devCfg in EnumerateSlotDeviceConfigs(slotIndex))
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

                case MacroActionType.MoveMouseToScreenPosition:
                    // System-wide cursor warp (#9), identical to the Gamepad path.
                    CursorControlService.Active?.MoveCursorTo(action.MouseX, action.MouseY);
                    AdvanceAction(macro);
                    break;

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
                        foreach (var devCfg in EnumerateSlotDeviceConfigs(slotIndex))
                            ApplyLightbarModeSetMigrated(devCfg, action.LightbarTargetMode);
                    }
                    AdvanceAction(macro);
                    break;
                }

                case MacroActionType.LightbarModeCycle:
                    ApplyLightbarModeCycleAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.PointerModeCycle:
                    ApplyPointerModeCycleAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.PointerModeSet:
                    ApplyPointerModeSetAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.GuideLedBrightness:
                    ApplyGuideLedBrightnessAction(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleVcButton:
                    // Extended twin of the Gamepad-path latch flip (issue #9
                    // wave 1b). Application happens per frame in
                    // EvaluateSlotMacrosExtended via the wide button words.
                    action.VcToggleLatched = !action.VcToggleLatched;
                    if (action.VcToggleLatched) ResetLatchPulsePhase(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleKey:
                    // Direction-aware latch write (audit #2 M4), the same
                    // helper the Gamepad path uses.
                    ApplyKeyLatchWrite(macro, action);
                    AdvanceAction(macro);
                    break;

                // v18 latch family, Extended twins.
                case MacroActionType.ToggleMouseButton:
                    ApplyMouseLatchWrite(macro, action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleVcAxis:
                    action.VcAxisToggleLatched = !action.VcAxisToggleLatched;
                    if (action.VcAxisToggleLatched) ResetLatchPulsePhase(action);
                    AdvanceAction(macro);
                    break;

                case MacroActionType.ToggleWheel:
                    action.WheelToggleLatched = !action.WheelToggleLatched;
                    if (action.WheelToggleLatched)
                        action.RepeatKeyLastFireUtc = DateTime.MinValue;
                    AdvanceAction(macro);
                    break;

                case MacroActionType.GyroRecenter:
                    ApplyGyroRecenterAction(macro);
                    AdvanceAction(macro);
                    break;
            }
        }

        /// <summary>Apply a macro LightbarModeSet target to one device's
        /// config, translating the legacy InputReactive* values into
        /// the v3.2+ overlay model: LightbarMode parked at the
        /// PlayerNumber default, InputReactiveMode = corresponding
        /// overlay variant. The parking mirrors the loader's LightingRev
        /// migration in SettingsService.ApplyDeviceSlotConfigData: the
        /// v4 idle base is the player floor, and a macro that wants the
        /// old black base can target the now-deliberate Off instead.
        /// Non-reactive base modes (including Off = hard black and
        /// PlayerNumber = the floor) set LightbarMode directly and
        /// leave the overlay alone, so users can layer macros. A macro
        /// can switch the base to Rainbow while the user's overlay
        /// continues to flash on each press.</summary>
        private static void ApplyLightbarModeSetMigrated(DeviceSlotConfig devCfg, LightbarMode target)
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
        /// <summary>The one canonical MacroAxisTarget → Extended word-array
        /// index map (LX0 LY1 LT2 RX3 RY4 RT5). Every raw-path axis write
        /// and the #237 yield gates resolve through this so the map can
        /// never drift between siblings. -1 = unmapped.</summary>
        internal static int MacroAxisTargetToRawIndex(MacroAxisTarget target) => target switch
        {
            MacroAxisTarget.LeftStickX => 0,
            MacroAxisTarget.LeftStickY => 1,
            MacroAxisTarget.RightStickX => 3,
            MacroAxisTarget.RightStickY => 4,
            MacroAxisTarget.LeftTrigger => 2,
            MacroAxisTarget.RightTrigger => 5,
            _ => -1
        };

        private static void ApplyAxisActionRaw(ref ExtendedRawState raw, MacroAction action)
        {
            int axisIndex = MacroAxisTargetToRawIndex(action.AxisTarget);
            if (axisIndex >= 0 && axisIndex < raw.Axes.Length)
                raw.Axes[axisIndex] = action.AxisValue;
        }

        /// <summary>Extended twin of <see cref="ApplyAxisAddAction"/>
        /// (#237): signed addition in the word-array frame, clamped. The
        /// Extended axis frame is -32768..32767 on every index, so no
        /// trigger rescale applies (the AxisHold raw-twin rationale).</summary>
        private static void ApplyAxisAddActionRaw(ref ExtendedRawState raw, MacroAction action)
        {
            int axisIndex = MacroAxisTargetToRawIndex(action.AxisTarget);
            if (axisIndex < 0 || axisIndex >= raw.Axes.Length) return;
            // Trigger channels span the full signed word from a MinValue
            // rest, so the add doubles onto that span exactly like the
            // Gamepad path's pull scale. Without it "+100%" only reached
            // the midpoint from rest and the UI's percent lied per slot
            // shape. Sticks add in the plain signed frame.
            bool isTrigger = action.AxisTarget == MacroAxisTarget.LeftTrigger
                || action.AxisTarget == MacroAxisTarget.RightTrigger;
            int add = isTrigger ? action.AxisValue * 2 : action.AxisValue;
            raw.Axes[axisIndex] = (short)Math.Clamp(
                raw.Axes[axisIndex] + add, short.MinValue, short.MaxValue);
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
        private const uint MOUSEEVENTF_HWHEEL = 0x1000;

        // Accumulated mouse-move delta (macro actions + the KBM virtual
        // controller's continuous lanes). Written by the poll thread
        // (~1000 Hz), drained by the dedicated mouse-injector
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
        private static int _pendingScrollH;
        private static readonly INPUT[] _mouseInjectBuf = new INPUT[1];

        /// <summary>Poll thread: accumulate the desired mouse delta. Lock-free and
        /// syscall-free, so a mouse-move macro adds no per-poll SendInput cost. The
        /// injector thread flushes the accumulated delta with one SendInput.</summary>
        private static void SendMouseMoveInput(int dx, int dy)
        {
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no mouse
            AccumulateMouseMoveInput(dx, dy);
        }

        /// <summary>Restriction-agnostic accumulate for the mouse-move lane.
        /// The KBM virtual controller feeds its continuous stick-to-mouse
        /// delta through here. Step 5 already zeroes a restricted slot's
        /// whole KbmRawState, so the macro-context flag above must not
        /// apply here (it holds whatever the LAST macro slot set).</summary>
        internal static void AccumulateMouseMoveInput(int dx, int dy)
        {
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

            int scrollH = Interlocked.Exchange(ref _pendingScrollH, 0);
            if (scrollH != 0)
            {
                _mouseInjectBuf[0] = new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion { mi = new MOUSEINPUT { mouseData = (uint)scrollH, dwFlags = MOUSEEVENTF_HWHEEL } }
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
            AccumulateMouseScrollInput(amount);
        }

        /// <summary>Restriction-agnostic accumulate for the vertical scroll
        /// lane, in WHEEL_DELTA units. Same contract as
        /// <see cref="AccumulateMouseMoveInput"/>: the KBM virtual
        /// controller's continuous scroll rides this, restriction handled
        /// upstream at the Step 5 submit.</summary>
        internal static void AccumulateMouseScrollInput(int amount)
        {
            if (amount == 0) return;
            Interlocked.Add(ref _pendingScroll, amount);
        }

        /// <summary>Horizontal-scroll twin of
        /// <see cref="AccumulateMouseScrollInput"/> (issue #154 tilt wheel).
        /// Flushed as MOUSEEVENTF_HWHEEL, positive = scroll right.</summary>
        internal static void AccumulateMouseScrollHInput(int amount)
        {
            if (amount == 0) return;
            Interlocked.Add(ref _pendingScrollH, amount);
        }

        /// <summary>Test pin (v15 MouseWheelTap): drains both pending
        /// scroll lanes without SendInput, returning (vertical,
        /// horizontal) in WHEEL_DELTA units. The injector thread normally
        /// flushes these; the tests observe them here instead.</summary>
        internal static (int Vertical, int Horizontal) DrainPendingScrollForTests()
            => (Interlocked.Exchange(ref _pendingScroll, 0),
                Interlocked.Exchange(ref _pendingScrollH, 0));

        /// <summary>Test pin (v16 MouseNudge): drains the pending
        /// mouse-move lane without SendInput, returning the batched
        /// (dx, dy) in pixels. Same contract as
        /// <see cref="DrainPendingScrollForTests"/>.</summary>
        internal static (int Dx, int Dy) DrainPendingMouseMoveForTests()
            => (Interlocked.Exchange(ref _pendingMouseDx, 0),
                Interlocked.Exchange(ref _pendingMouseDy, 0));

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

        // ─────────────────────────────────────────────
        //  Text Block emission (issue #201)
        // ─────────────────────────────────────────────

        /// <summary>Runs a TextBlock action's frame-paced Unicode typing. Returns
        /// true when the whole text has been emitted and the sequence can advance.
        /// Delay 0 emits the entire string as ONE batched SendInput call on the
        /// first tick; a per-character delay emits at most a few characters per
        /// tick. Never a blocking loop: macro execution runs on the ~1 kHz poll
        /// thread, and per-poll SendInput churn is exactly what dragged the loop
        /// to ~200 Hz before the mouse injector existed (see the injector's block
        /// comment in this file).</summary>
        private static bool ExecuteTextBlockAction(MacroAction action, double actionElapsed)
        {
            string text = action.TextContent;
            if (string.IsNullOrEmpty(text)) return true;

            int target = MacroAction.ComputeTextEmitTarget(text, action.TextPerCharDelayMs, actionElapsed);
            if (target > action.TextEmitCursor)
            {
                SendTextInput(text, action.TextEmitCursor, target);
                action.TextEmitCursor = target;
            }
            if (action.TextEmitCursor >= text.Length)
            {
                action.TextEmitCursor = 0; // re-arm for repeats and the next trigger
                return true;
            }
            return false;
        }

        /// <summary>Emits <paramref name="text"/>[from..to) as one batched SendInput
        /// call. Characters ride KEYEVENTF_UNICODE (wVk 0, wScan = UTF-16 code unit,
        /// the AutoHotkey SendText mechanism), which is layout-independent and needs
        /// no shift-state juggling. Surrogate halves emit as consecutive down/up
        /// pairs in the same batch and the target app's message loop reassembles
        /// them. Newlines press Enter (CRLF folds to one), tabs press Tab, so
        /// multiline blocks and forms work.</summary>
        private static void SendTextInput(string text, int from, int to)
        {
            if (_currentMacroSlotRestricted) return; // gamepad-only peer: no keystrokes

            var inputs = new List<INPUT>((to - from) * 2);
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    // CRLF folds to one Enter: skip the '\r' and let the '\n'
                    // emit it (the pair can straddle an emission boundary, so
                    // the lookahead checks the full text, not the slice). A
                    // bare '\r' still gets its own Enter.
                    if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                    AppendVkPair(inputs, VK_RETURN);
                }
                else if (c == '\n') AppendVkPair(inputs, VK_RETURN);
                else if (c == '\t') AppendVkPair(inputs, VK_TAB);
                else AppendUnicodePair(inputs, c);
            }

            // Chunk oversized batches. AutoHotkey (the reference for this
            // mechanism) caps its SendInput arrays because giant batches drop
            // tail keystrokes; 4096 events (2048 characters) per call stays far
            // under that scale while normal texts still go out in one call.
            const int maxInputsPerSend = 4096;
            for (int off = 0; off < inputs.Count; off += maxInputsPerSend)
            {
                int n = Math.Min(maxInputsPerSend, inputs.Count - off);
                SendInput((uint)n, inputs.GetRange(off, n).ToArray(), Marshal.SizeOf<INPUT>());
            }
        }

        private static void AppendUnicodePair(List<INPUT> inputs, char codeUnit)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = codeUnit, dwFlags = KEYEVENTF_UNICODE } }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = codeUnit, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            });
        }

        private static void AppendVkPair(List<INPUT> inputs, ushort vk)
        {
            ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = 0 } }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = KEYEVENTF_KEYUP } }
            });
        }

        // ── P/Invoke declarations ──

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint MAPVK_VK_TO_VSC = 0;
        private const ushort VK_TAB = 0x09;
        private const ushort VK_RETURN = 0x0D;

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
