using System.Reflection;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>PadForge's own create and submit path, on a real
    /// InputManager. The create runs Step 5's CreateVirtualController itself
    /// (in InputManager.Step5.VirtualDevices.cs), which calls
    /// EnsureHMaestroContext, resolves the profile and calls the same factory
    /// a slot of that type gets (CreateHMaestroController,
    /// CreateKeyboardMouseController), connects it and
    /// registers its feedback. Those members are private, so they are reached
    /// by name. <see cref="Resolve"/> finds every one of them, and the
    /// self-check fails when a rename moves one.</summary>
    internal sealed class PadForgePath
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static MethodInfo s_create, s_captureBuild, s_disposeContext;
        private static FieldInfo s_profileField;

        /// <summary>Finds every member reached by name, or says which one is
        /// missing.</summary>
        internal static string Resolve()
        {
            var t = typeof(InputManager);
            s_create = t.GetMethods(Any).FirstOrDefault(m => m.Name == "CreateVirtualController" && m.GetParameters().Length == 5);
            s_captureBuild = t.GetMethod("CaptureExtendedBuild", Any, null, new[] { typeof(int) }, null);
            s_disposeContext = t.GetMethod("DisposeHMaestroContextOnShutdown", Any, null,
                new[] { typeof(IReadOnlyList<System.Threading.Tasks.Task>) }, null);
            s_profileField = typeof(HMaestroVirtualController).GetField("_profile", Any);
            if (s_create == null) return "InputManager.CreateVirtualController(int, VirtualControllerType, string, in ExtendedBuild, in KbmBuild)";
            var p = s_create.GetParameters();
            if (p[0].ParameterType != typeof(int) || p[1].ParameterType != typeof(VirtualControllerType)
                || p[2].ParameterType != typeof(string) || !p[3].ParameterType.IsByRef || !p[4].ParameterType.IsByRef
                || p[4].ParameterType.GetElementType() != typeof(InputManager.KbmBuild))
                return "InputManager.CreateVirtualController's parameters";
            if (s_captureBuild == null || s_captureBuild.ReturnType != p[3].ParameterType.GetElementType())
                return "InputManager.CaptureExtendedBuild(int)";
            if (s_disposeContext == null || s_disposeContext.ReturnType != typeof(System.Threading.Tasks.Task))
                return "InputManager.DisposeHMaestroContextOnShutdown(IReadOnlyList<Task>) returning Task";
            if (s_profileField == null || s_profileField.FieldType != typeof(HMProfile)) return "HMaestroVirtualController._profile";
            return null;
        }

        public InputManager Engine { get; } = new InputManager();

        /// <summary>The errors the engine raised, newest last. A failed create
        /// returns null and says why only here (InputManager.Step5's
        /// RaiseCreateError).</summary>
        public List<string> Errors { get; } = new();

        public PadForgePath()
        {
            Engine.ErrorOccurred += (_, e) =>
            {
                lock (Errors) Errors.Add(e.Exception != null ? $"{e.Message}: {e.Exception.GetType().Name}: {e.Exception.Message}" : e.Message);
            };
        }

        /// <summary>Sets the slot as InputService sets it for a new slot of the
        /// family (InputService.Start for the profile,
        /// InputService.SyncExtendedConfigToSlot for the Extended and Nintendo
        /// fields), then creates its controller through
        /// Step 5's CreateVirtualController.</summary>
        public IVirtualController Create(Family f)
        {
            int pad = Families.Pad;
            Engine.SlotControllerTypes[pad] = f.Type;
            Engine.SlotProfileIds[pad] = f.Type == VirtualControllerType.KeyboardMouse ? null : f.ProfileId;
            bool raw = f.Type is VirtualControllerType.Extended or VirtualControllerType.Nintendo;
            Engine.SlotCustomLayouts[pad] = f.Layout;
            Engine.SlotRawHidSurface[pad] = raw;
            bool customize = f.Type == VirtualControllerType.Extended && f.Customize;
            Engine.SlotExtendedCustomize[pad] = customize;
            Engine.SlotOemOverrideEnabled[pad] = false;
            Engine.SlotOemOverrideLabel[pad] = string.Empty;
            // ExtendedSlotConfig.ForceFeedbackEnabled defaults to true, and an
            // uncustomized slot gets true (InputService.SyncExtendedConfigToSlot).
            Engine.SlotExtendedFfbEnabled[pad] = true;
            Engine.SlotExtendedVendorId[pad] = 0;
            Engine.SlotExtendedProductId[pad] = 0;

            object build = s_captureBuild.Invoke(Engine, new object[] { pad });
            var kbm = f.Route switch
            {
                Route.Keyboard => new InputManager.KbmBuild(f.ProfileId, null),
                Route.Mouse => new InputManager.KbmBuild(null, f.ProfileId),
                _ => default,
            };
            string profile = f.Type == VirtualControllerType.KeyboardMouse ? null : f.ProfileId;
            return (IVirtualController)s_create.Invoke(Engine, new object[] { pad, f.Type, profile, build, kbm });
        }

        /// <summary>The profile the controller was built from, which for a
        /// customized Extended slot is the one HMProfileBuilder made
        /// (Step 5's CreateHMaestroController, its override block).</summary>
        public static HMProfile EffectiveProfile(HMaestroVirtualController vc) => (HMProfile)s_profileField.GetValue(vc);

        /// <summary>Takes the controller down as Step 5's destroy does on the
        /// synchronous path (Step 5's DestroyVirtualController:
        /// UnregisterFeedback, then Disconnect and Dispose).</summary>
        public static void Destroy(IVirtualController vc)
        {
            if (vc == null) return;
            try
            {
                if (vc is HMaestroVirtualController hm) hm.UnregisterFeedback();
                vc.Disconnect();
                vc.Dispose();
            }
            catch { /* the run reports its own results */ }
        }

        /// <summary>Disposes the engine's HIDMaestro context as PadForge's
        /// shutdown does (Step5 DisposeHMaestroContextOnShutdown).</summary>
        public void DisposeContext()
        {
            // No lifecycle task outlives the probe's own synchronous destroys, so
            // an empty list disposes the context at once and the Task it
            // returns is already complete. It is waited on all the same.
            try
            {
                var done = (System.Threading.Tasks.Task)s_disposeContext.Invoke(Engine,
                    new object[] { Array.Empty<System.Threading.Tasks.Task>() });
                done?.Wait(TimeSpan.FromSeconds(60));
            }
            catch { }
        }
    }

    /// <summary>One pass of Step 5's Pass 3 for one slot, the branch its lane
    /// takes, with the slot's state replaced by the known state. The steps
    /// Pass 3 runs before a submit that change nothing for a new slot with
    /// no settings are left out and named: Keep Controller Awake
    /// (ApplyKeepAwake, no mapping set), button SOCD (ResolveSlotSocd on
    /// both surfaces, none configured), a game mod's trigger threshold
    /// (GameModEffects.ThresholdsForSlot on both surfaces, none held) and
    /// the problem recorder (Recording.RecordingCapture.Active, off).</summary>
    internal sealed class Submitter
    {
        private readonly Family _f;
        private readonly IVirtualController _vc;
        private readonly InputManager _engine;
        private readonly byte[] _sonyScratch = new byte[63];
        private readonly byte[] _deckScratch = new byte[ValveReportPackers.MaxReportSize];
        private readonly byte[] _sinputScratch = new byte[SInputReportPacker.ReportSize];
        private readonly byte[] _extendedScratch = new byte[ExtendedReportPacker.MaxReportSize];
        private long _sonyFrameCounter, _deckFrameCounter;

        public Submitter(Family f, IVirtualController vc, InputManager engine)
        {
            _f = f;
            _vc = vc;
            _engine = engine;
        }

        public void Pass(bool neutral, in KbmRawState kbm)
        {
            int pad = Families.Pad;
            var tp = default(TouchpadState);
            var motion = default(MotionSnapshot);
            switch (_f.Route)
            {
                case Route.Keyboard:
                case Route.Mouse:
                    // Step 5's KeyboardMouseVirtualController branch. The state is the person's own input.
                    ((KeyboardMouseVirtualController)_vc).SubmitKbmState(kbm, KbmCause.AllOwn);
                    return;

                case Route.Gamepad:
                    // Step 5's gpOut from CombinedOutputStates and its plain
                    // vc.SubmitGamepadState call.
                    _vc.SubmitGamepadState(KnownState.Pad(neutral));
                    return;

                case Route.PlayStationState:
                {
                    // Step 5's gpOut (no touchpad click), its battery pctByte
                    // and its hmExtState.SubmitGamepadState call.
                    var hm = (HMaestroVirtualController)_vc;
                    int pctNow = _engine.BatteryPercents[pad];
                    byte pctByte = pctNow < 0 ? (byte)100 : (byte)Math.Clamp(pctNow, 0, 100);
                    hm.SubmitGamepadState(KnownState.Pad(neutral), tp, motion, pctByte, _engine.BatteryCharging[pad], default);
                    return;
                }

                case Route.SonyPacked:
                {
                    // Step 5 skips the state leg when SonyReportPackers.ForProfile
                    // returns a packer, then packs and submits the frame.
                    var hm = (HMaestroVirtualController)_vc;
                    var packer = SonyReportPackers.ForProfile(hm.ProfileId);
                    var gp = KnownState.Pad(neutral);
                    int pct = _engine.BatteryPercents[pad];
                    byte battery = pct < 0 ? (byte)100 : (byte)Math.Clamp(pct, 0, 100);
                    bool carriesStatusBytes = SonyReportPackers.IsDualSenseProfile(hm.ProfileId);
                    var scratch = _sonyScratch.AsSpan();
                    bool due = true;
                    if (EveryPassFrameGate.Engaged)
                    {
                        var unclocked = motion;
                        unclocked.TimestampUs = 0;
                        packer(gp, tp, unclocked, battery, _engine.BatteryCharging[pad], 0, 0, scratch);
                        if (carriesStatusBytes) SonyReportPackers.ApplyDualSenseStatusBytes(scratch, _engine.Ds5StatusBytes[pad]);
                        due = hm.RawReportDue(scratch);
                    }
                    if (due)
                    {
                        packer(gp, tp, motion, battery, _engine.BatteryCharging[pad],
                            unchecked((uint)_sonyFrameCounter++), ValveReportPackers.MicrosNow(), scratch);
                        if (carriesStatusBytes) SonyReportPackers.ApplyDualSenseStatusBytes(scratch, _engine.Ds5StatusBytes[pad]);
                        hm.SubmitRawReport(scratch);
                    }
                    return;
                }

                case Route.ValvePacked:
                {
                    // Step 5's ValveReportPackers.ForProfile branch.
                    var hm = (HMaestroVirtualController)_vc;
                    var raw = KnownState.Raw(_f.ProfileId, _f.Layout, neutral);
                    var valvePacker = ValveReportPackers.ForProfile(hm.ProfileId);
                    var frame = new ReadOnlySpan<byte>(_deckScratch, 0, valvePacker.Size);
                    bool due = true;
                    if (EveryPassFrameGate.Engaged)
                    {
                        valvePacker.Pack(raw, tp, motion, 0, 0, _deckScratch);
                        due = hm.RawReportDue(frame);
                    }
                    if (due)
                    {
                        valvePacker.Pack(raw, tp, motion, unchecked((uint)_deckFrameCounter++), ValveReportPackers.MicrosNow(), _deckScratch);
                        hm.SubmitRawReport(frame);
                    }
                    return;
                }

                case Route.SInputPacked:
                {
                    // Step 5's SInputReportPacker.IsSInputProfile branch.
                    var hm = (HMaestroVirtualController)_vc;
                    var raw = KnownState.Raw(_f.ProfileId, _f.Layout, neutral);
                    int sinputPct = _engine.BatteryPercents[pad];
                    var frame = hm.RawFramesCarryReportId
                        ? new ReadOnlySpan<byte>(_sinputScratch, 0, SInputReportPacker.ReportSize)
                        : new ReadOnlySpan<byte>(_sinputScratch, 1, SInputReportPacker.ReportSize - 1);
                    bool due = true;
                    if (EveryPassFrameGate.Engaged)
                    {
                        SInputReportPacker.Pack(raw, tp, motion, sinputPct, _engine.BatteryCharging[pad], 0, _sinputScratch);
                        due = hm.RawReportDue(frame);
                    }
                    if (due)
                    {
                        SInputReportPacker.Pack(raw, tp, motion, sinputPct, _engine.BatteryCharging[pad], ValveReportPackers.MicrosNow(), _sinputScratch);
                        hm.SubmitRawReport(frame);
                    }
                    return;
                }

                case Route.ExtendedPacked:
                {
                    // Step 5's ExtendedReportPacker.NeedsRawReport branch.
                    var hm = (HMaestroVirtualController)_vc;
                    var raw = KnownState.Raw(_f.ProfileId, _f.Layout, neutral);
                    int packedLen = ExtendedReportPacker.Pack(raw, _f.Layout, _extendedScratch);
                    if (packedLen > 0)
                        hm.SubmitPackedExtendedReport(new ReadOnlySpan<byte>(_extendedScratch, 0, packedLen));
                    return;
                }

                case Route.RawState:
                {
                    // Step 5's hmExt.SubmitRawHidState call.
                    var hm = (HMaestroVirtualController)_vc;
                    var raw = KnownState.Raw(_f.ProfileId, _f.Layout, neutral);
                    hm.SubmitRawHidState(raw, _f.Layout.Sticks, _f.Layout.Triggers, motion);
                    return;
                }
            }
        }
    }
}
