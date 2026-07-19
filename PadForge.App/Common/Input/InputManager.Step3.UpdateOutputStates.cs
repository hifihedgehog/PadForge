using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 3: UpdateOutputStates
        //  Maps each device's CustomInputState to a Gamepad struct
        //  based on the PadSetting mapping rules configured for that device.
        //
        //  Each UserSetting links a device (InstanceGuid) to a pad slot (MapTo 0–15)
        //  and references a PadSetting that contains the mapping rules.
        //
        //  PadSetting string fields like "ButtonA", "LeftThumbAxisX", etc. contain
        //  mapping descriptors in the format: "{MapType} {Index}" or "IH{MapType} {Index}"
        //  (IH prefix = inverted/half-axis). Examples:
        //    "Button 0"  → Button index 0
        //    "Axis 1"    → Axis index 1
        //    "IHAxis 2"  → Axis index 2, inverted half
        //    "POV 0 Up"  → POV 0, up direction
        //    "Slider 0"  → Slider index 0
        // ─────────────────────────────────────────────

        /// <summary>
        /// Step 3: For each device with a valid UserSetting + PadSetting, map its
        /// <see cref="CustomInputState"/> to a <see cref="Gamepad"/> and store the
        /// result on the UserSetting for later combination in Step 4.
        /// </summary>
        private void UpdateOutputStates()
        {
            var settings = SettingsManager.UserSettings?.Items;
            if (settings == null) return;

            // New Step-3 pass: arm the per-pass device-state memo so
            // multi-source foreign-device lookups lock UserDevices once per
            // unique GUID per pass instead of once per source per row.
            BeginDeviceStateMemo();

            // Reset per-slot multi-source row evaluation tracking so
            // the new frame's first device pass triggers fresh cross-
            // device evaluation. Every multi-source row (Sum, Average,
            // AND, XOR, Custom, etc.) evaluates row.Sources once
            // across all devices; subsequent device passes in the
            // same frame skip rows already written.
            BeginFrameMultiSourceTracking();

            // Clear the per-slot raw touchpad-click flags; they're re-OR'd per device below.
            System.Array.Clear(SlotRawTouchpadClick, 0, SlotRawTouchpadClick.Length);

            // Snapshot settings into pre-allocated buffer (no LINQ allocation).
            int snapshotCount;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                if (_settingSnapshotBuffer.Length < settings.Count)
                    _settingSnapshotBuffer = new UserSetting[settings.Count];

                snapshotCount = 0;
                for (int i = 0; i < settings.Count; i++)
                    _settingSnapshotBuffer[snapshotCount++] = settings[i];
            }

            for (int si = 0; si < snapshotCount; si++)
            {
                var us = _settingSnapshotBuffer[si];
                try
                {
                    // Find the device for this setting.
                    UserDevice ud = FindOnlineDeviceByInstanceGuid(us.InstanceGuid);
                    if (ud == null)
                    {
                        us.OutputState = default;
                        us.RawMappedState = default; // preview must not freeze on a removed device
                        continue;
                    }
                    // Device exists but input temporarily unavailable — keep
                    // last valid OutputState to prevent transient zero glitches
                    // (e.g. output controller reading during state refresh).
                    if (!ud.IsOnline || ud.InputState == null)
                        continue;

                    // Get the PadSetting with mapping rules.
                    PadSetting ps = us.GetPadSetting();
                    if (ps == null)
                        continue;

                    // Map the input state to a gamepad. Phase 1c-2 routes
                    // descriptor reading through MappingSet when one is
                    // available for this slot; tuning still comes from
                    // PadSetting until Phase 1c-3.
                    int slotIndex = us.MapTo;
                    MappingSet ms = (slotIndex >= 0 && slotIndex < SettingsManager.SlotMappingSets.Length)
                        ? SettingsManager.SlotMappingSets[slotIndex]
                        : null;
                    Gamepad rawMapped;
                    if (ms != null && ms.Rows != null && ms.Rows.Count > 0)
                    {
                        us.OutputState = MapInputToGamepadFromMappingSet(
                            ud.InputState, ms, us.InstanceGuidString, ps, slotIndex, out rawMapped);
                    }
                    else
                    {
                        us.OutputState = MapInputToGamepad(ud.InputState, ps,
                            us.InstanceGuidString, slotIndex, out rawMapped);
                    }
                    us.RawMappedState = rawMapped;

                    // Raw physical touchpad click (SDL_GAMEPAD_BUTTON_TOUCHPAD = Buttons[16]),
                    // OR'd into the per-slot flag for the InputReactive lightbar so the press
                    // flashes regardless of virtual-controller type or click mapping. Done here
                    // (not via TouchpadOutputState) because that's only computed for PlayStation
                    // slots and reflects the click's mapping, not the physical press.
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                    {
                        var rawButtons = ud.InputState.Buttons;
                        if (rawButtons != null && rawButtons.Length > 16 && rawButtons[16])
                            SlotRawTouchpadClick[slotIndex] = true;
                    }

                    // Steering at-lock haptic feedback (#94). The MappingSet eval above
                    // updated this frame's lock state; fire the opt-in channels now.
                    if (ms != null && ms.Rows != null && ms.Rows.Count > 0)
                        ApplySteeringLockFeedback(ms, slotIndex, ps, ud);

                    // All non-gamepad output paths route per-target descriptor
                    // reads through the per-VC MappingSet when a Base-layer row
                    // exists (multi-source, combine-mode, Custom-formula aware).
                    // ps + the legacy single-source PadSetting fields stay live
                    // as the fallback for configs that haven't been resaved
                    // since the multi-source UI shipped.
                    string deviceGuidStr = us.InstanceGuidString;

                    // For custom Extended slots, also produce the raw Extended output state.
                    int slot = slotIndex;
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] is VirtualControllerType.Extended
                            or VirtualControllerType.Nintendo &&
                        SlotRawHidSurface[slot])
                    {
                        var cfg = SlotCustomLayouts[slot];
                        us.RawHidOutputState = MapInputToExtendedRaw(
                            ud.InputState, ps, cfg, ms, deviceGuidStr, slot);
                    }

                    // For MIDI slots, produce the raw MIDI output state.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.Midi)
                    {
                        var mc = _midiConfigs[slot];
                        if (mc != null)
                            us.MidiRawOutputState = MapInputToMidiRaw(
                                ud.InputState, ps, mc.CcCount, mc.NoteCount,
                                ms, deviceGuidStr, slot);
                    }

                    // For KeyboardMouse slots, produce the raw KBM output state.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.KeyboardMouse)
                    {
                        us.KbmRawOutputState = MapInputToKbmRaw(
                            ud.InputState, ps, ms, deviceGuidStr, slot);
                    }

                    // For PlayStation slots, produce touchpad state from input device.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.PlayStation)
                    {
                        us.TouchpadOutputState = MapInputToTouchpad(
                            ud.InputState, ps, us.TouchpadOutputState,
                            ms, deviceGuidStr, slot);
                    }
                }
                catch (Exception ex)
                {
                    // Don't zero OutputState. Keep last valid state to prevent
                    // transient glitches from propagating through the pipeline.
                    RaiseError($"Error mapping device {us.InstanceGuid}", ex);
                }
            }

            // Disarm the memo so nothing that runs on this thread after the
            // pass (Step 4 macros, SOCD, tests driving eval helpers directly)
            // can observe entries from a finished pass. The only path that
            // skips this is an uncaught throw above, and the next pass's
            // BeginDeviceStateMemo clears before arming anyway.
            EndDeviceStateMemo();
        }

        // ─────────────────────────────────────────────
        //  Mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a <see cref="CustomInputState"/> to a <see cref="Gamepad"/> using
        /// the mapping rules defined in a <see cref="PadSetting"/>.
        /// </summary>
        /// <param name="state">The device's current input state.</param>
        /// <param name="ps">The PadSetting containing mapping rules.</param>
        /// <param name="deviceGuid">Instance GUID of the device being mapped;
        /// stamped on the synthetic sources the engine-owned delegation
        /// builds so per-device tuning / debounce state keys correctly.</param>
        /// <param name="slotIndex">Slot the mapping evaluates for; carries
        /// per-(device, slot) tuning into the engine-owned evaluators.</param>
        /// <returns>A populated Gamepad struct.</returns>
        private static Gamepad MapInputToGamepad(CustomInputState state, PadSetting ps,
            string deviceGuid, int slotIndex, out Gamepad rawMapped)
        {
            rawMapped = default;
            var gp = new Gamepad();
            int gt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // ── Buttons ──
            if (MapToButtonPressed(state, ps.ButtonA, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonA"), 0), gt, ps.GetMappingBidirectional("ButtonA") == "1"))
                gp.SetButton(Gamepad.A, true);
            if (MapToButtonPressed(state, ps.ButtonB, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonB"), 0), gt, ps.GetMappingBidirectional("ButtonB") == "1"))
                gp.SetButton(Gamepad.B, true);
            if (MapToButtonPressed(state, ps.ButtonX, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonX"), 0), gt, ps.GetMappingBidirectional("ButtonX") == "1"))
                gp.SetButton(Gamepad.X, true);
            if (MapToButtonPressed(state, ps.ButtonY, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonY"), 0), gt, ps.GetMappingBidirectional("ButtonY") == "1"))
                gp.SetButton(Gamepad.Y, true);

            if (MapToButtonPressed(state, ps.LeftShoulder, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("LeftShoulder"), 0), gt, ps.GetMappingBidirectional("LeftShoulder") == "1"))
                gp.SetButton(Gamepad.LEFT_SHOULDER, true);
            if (MapToButtonPressed(state, ps.RightShoulder, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("RightShoulder"), 0), gt, ps.GetMappingBidirectional("RightShoulder") == "1"))
                gp.SetButton(Gamepad.RIGHT_SHOULDER, true);

            if (MapToButtonPressed(state, ps.ButtonBack, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonBack"), 0), gt, ps.GetMappingBidirectional("ButtonBack") == "1"))
                gp.SetButton(Gamepad.BACK, true);
            if (MapToButtonPressed(state, ps.ButtonStart, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonStart"), 0), gt, ps.GetMappingBidirectional("ButtonStart") == "1"))
                gp.SetButton(Gamepad.START, true);

            if (MapToButtonPressed(state, ps.LeftThumbButton, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("LeftThumbButton"), 0), gt, ps.GetMappingBidirectional("LeftThumbButton") == "1"))
                gp.SetButton(Gamepad.LEFT_THUMB, true);
            if (MapToButtonPressed(state, ps.RightThumbButton, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("RightThumbButton"), 0), gt, ps.GetMappingBidirectional("RightThumbButton") == "1"))
                gp.SetButton(Gamepad.RIGHT_THUMB, true);

            if (MapToButtonPressed(state, ps.ButtonGuide, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonGuide"), 0), gt, ps.GetMappingBidirectional("ButtonGuide") == "1"))
                gp.SetButton(Gamepad.GUIDE, true);

            // Xbox Series Share button — sits outside the 16-bit Buttons
            // mask. HM drops the bit on profiles whose descriptor doesn't
            // declare button 13, so always-mapping is safe even if the
            // active profile isn't Xbox Series.
            if (MapToButtonPressed(state, ps.ButtonShare, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("ButtonShare"), 0), gt, ps.GetMappingBidirectional("ButtonShare") == "1"))
                gp.Share = true;

            // ── D-Pad ──
            // Individual direction mappings take priority. Only fall back to
            // the combined DPad descriptor if no individual directions are set.
            bool hasIndividualDPad = !string.IsNullOrEmpty(ps.DPadUp)
                                 || !string.IsNullOrEmpty(ps.DPadDown)
                                 || !string.IsNullOrEmpty(ps.DPadLeft)
                                 || !string.IsNullOrEmpty(ps.DPadRight);

            if (hasIndividualDPad)
            {
                if (MapToButtonPressed(state, ps.DPadUp, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("DPadUp"), 0), gt, ps.GetMappingBidirectional("DPadUp") == "1"))
                    gp.SetButton(Gamepad.DPAD_UP, true);
                if (MapToButtonPressed(state, ps.DPadDown, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("DPadDown"), 0), gt, ps.GetMappingBidirectional("DPadDown") == "1"))
                    gp.SetButton(Gamepad.DPAD_DOWN, true);
                if (MapToButtonPressed(state, ps.DPadLeft, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("DPadLeft"), 0), gt, ps.GetMappingBidirectional("DPadLeft") == "1"))
                    gp.SetButton(Gamepad.DPAD_LEFT, true);
                if (MapToButtonPressed(state, ps.DPadRight, deviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone("DPadRight"), 0), gt, ps.GetMappingBidirectional("DPadRight") == "1"))
                    gp.SetButton(Gamepad.DPAD_RIGHT, true);
            }
            else
            {
                // Legacy/combined: extract all 4 directions from a single POV hat.
                MapDPadFromPov(state, ps.DPad, ref gp);
            }

            // ── Triggers ──
            gp.LeftTrigger = MapToTrigger(state, ps.LeftTrigger);
            gp.RightTrigger = MapToTrigger(state, ps.RightTrigger);

            // ── Thumbsticks ──
            gp.ThumbLX = MapToThumbAxisWithNeg(state, ps.LeftThumbAxisX, ps.LeftThumbAxisXNeg, deviceGuid, slotIndex);
            gp.ThumbLY = NegateAxis(MapToThumbAxisWithNeg(state, ps.LeftThumbAxisY, ps.LeftThumbAxisYNeg, deviceGuid, slotIndex));
            gp.ThumbRX = MapToThumbAxisWithNeg(state, ps.RightThumbAxisX, ps.RightThumbAxisXNeg, deviceGuid, slotIndex);
            gp.ThumbRY = NegateAxis(MapToThumbAxisWithNeg(state, ps.RightThumbAxisY, ps.RightThumbAxisYNeg, deviceGuid, slotIndex));

            // Snapshot raw mapped state (after axis selection, before DZ processing)
            // for the UI preview so it can apply its own pipeline without double-processing.
            rawMapped = gp;

            ApplyPadSettingTuning(ref gp, ps, slotIndex);
            return gp;
        }

        /// <summary>
        /// Issue #61 multi-source/shift Phase 1c-2 entry point. Reads
        /// descriptors from a per-VC <see cref="MappingSet"/> instead of
        /// the legacy per-(VC × Device) <see cref="PadSetting"/> mapping
        /// fields. Per-source <c>DeviceGuid</c> filters which row sources
        /// contribute on this pass; sources for other devices are ignored
        /// here and contribute via Step 4's cross-device combine. Tuning
        /// (deadzones, curves, center offsets) still comes from
        /// <paramref name="ps"/> until Phase 1c-3 moves them to
        /// <see cref="DeviceTuning"/>.
        /// </summary>
        private static Gamepad MapInputToGamepadFromMappingSet(
            CustomInputState state,
            MappingSet mappingSet,
            string thisDeviceGuid,
            PadSetting ps,
            int slotIndex,
            out Gamepad rawMapped)
        {
            rawMapped = default;
            var gp = new Gamepad();

            int gt = TryParseIntStatic(ps?.AxisToButtonThreshold, 50);
            ApplyMappingSetToGamepad(state, mappingSet, thisDeviceGuid, gt, slotIndex, ref gp);

            rawMapped = gp;
            ApplyPadSettingTuning(ref gp, ps, slotIndex);
            return gp;
        }

        /// <summary>
        /// Applies the per-(VC × Device) tuning shared by both Step 3
        /// paths: trigger deadzones / curves, stick center offsets,
        /// stick deadzones / curves / shape. Today these read from
        /// <see cref="PadSetting"/>; Phase 1c-3 will move them to
        /// <see cref="DeviceTuning"/> while keeping this signature.
        /// <paramref name="slotIndex"/> keys the Workshop deadzone-shape
        /// overlay (v18); -1 keeps the plain PadSetting read.
        /// </summary>
        private static void ApplyPadSettingTuning(ref Gamepad gp, PadSetting ps, int slotIndex = -1)
        {
            if (ps == null) return;

            // ── Trigger deadzones ──
            gp.LeftTrigger = ApplyTriggerDeadZone(gp.LeftTrigger,
                TryParseDoubleStatic(ps.LeftTriggerDeadZone, 0),
                TryParseDoubleStatic(ps.LeftTriggerAntiDeadZone, 0),
                TryParseDoubleStatic(ps.LeftTriggerMaxRange, 100),
                Common.CurveLut.GetOrBuild(ps.LeftTriggerSensitivityCurve));
            gp.RightTrigger = ApplyTriggerDeadZone(gp.RightTrigger,
                TryParseDoubleStatic(ps.RightTriggerDeadZone, 0),
                TryParseDoubleStatic(ps.RightTriggerAntiDeadZone, 0),
                TryParseDoubleStatic(ps.RightTriggerMaxRange, 100),
                Common.CurveLut.GetOrBuild(ps.RightTriggerSensitivityCurve));

            // ── Center offsets (applied before deadzone) ──
            gp.ThumbLX = ApplyCenterOffset(gp.ThumbLX, TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0));
            gp.ThumbLY = ApplyCenterOffset(gp.ThumbLY, TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0));
            gp.ThumbRX = ApplyCenterOffset(gp.ThumbRX, TryParseDoubleStatic(ps.RightThumbCenterOffsetX, 0));
            gp.ThumbRY = ApplyCenterOffset(gp.ThumbRY, TryParseDoubleStatic(ps.RightThumbCenterOffsetY, 0));

            // ── Circular reshaping (#174): warp the measured boundary onto a
            //    unit circle BEFORE the dead zone, so the dead-zone and curve
            //    chain below operates on circle-true values at every angle.
            //    Null LUT (no calibration) is a no-op. ──
            Common.StickBoundary.Reshape(ref gp.ThumbLX, ref gp.ThumbLY,
                Common.StickBoundary.GetOrBuild(ps.LeftThumbBoundaryMap));
            Common.StickBoundary.Reshape(ref gp.ThumbRX, ref gp.ThumbRY,
                Common.StickBoundary.GetOrBuild(ps.RightThumbBoundaryMap));

            // ── Dead zones ──
            ApplyDeadZone(ref gp.ThumbLX, ref gp.ThumbLY,
                TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbLinear, 0),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeXNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100)),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeYNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100)),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveX),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveY),
                ResolveThumbDeadZoneShape(slotIndex, left: true, ps));

            ApplyDeadZone(ref gp.ThumbRX, ref gp.ThumbRY,
                TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0),
                TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0),
                TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0),
                TryParseDoubleStatic(ps.RightThumbLinear, 0),
                TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100),
                TryParseDoubleStatic(ps.RightThumbMaxRangeXNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100)),
                TryParseDoubleStatic(ps.RightThumbMaxRangeYNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100)),
                Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveX),
                Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveY),
                ResolveThumbDeadZoneShape(slotIndex, left: false, ps));
        }

        /// <summary>Effective per-thumb deadzone shape (v18): the Workshop
        /// slot-level stamp wins on an Authoritative slot (Steam's
        /// deadzone_shape, carried on the imported MappingSet because an
        /// imported profile has no device PadSetting to stamp), else the
        /// device PadSetting's own shape. Same overlay contract as the
        /// touchpad gesture auto-arm.</summary>
        private static DeadZoneShape ResolveThumbDeadZoneShape(int slotIndex, bool left, PadSetting ps)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (slotIndex >= 0 && sets != null && slotIndex < sets.Length)
            {
                var set = sets[slotIndex];
                if (set != null && set.Authoritative)
                {
                    string stamp = left
                        ? set.WorkshopLeftStickDeadZoneShape
                        : set.WorkshopRightStickDeadZoneShape;
                    if (!string.IsNullOrEmpty(stamp)) return ParseDeadZoneShape(stamp);
                }
            }
            return ParseDeadZoneShape(left ? ps.LeftThumbDeadZoneShape : ps.RightThumbDeadZoneShape);
        }

        /// <summary>
        /// Negates a signed short axis value. Clamps short.MinValue to short.MaxValue
        /// to avoid overflow (since -(-32768) overflows short).
        /// Used to correct Y-axis orientation: the unsigned pipeline produces 0=up
        /// which maps to negative signed values, but XInput convention is positive Y = up.
        /// </summary>
        private static short NegateAxis(short value)
            => value == short.MinValue ? short.MaxValue : (short)-value;

        // ─────────────────────────────────────────────
        //  Mapping descriptor parser
        // ─────────────────────────────────────────────

        /// <summary>
        /// Parsing result for a PadSetting mapping descriptor string.
        /// </summary>
        private struct MappingDescriptor
        {
            public MapType Type;
            public int Index;
            public bool Inverted;
            public bool HalfAxis;
            public string PovDirection; // "Up", "Down", "Left", "Right" (for POV)
            public bool IsValid;
        }

        /// <summary>
        /// Parses a mapping descriptor string like "Button 0", "Axis 1", "IHAxis 2",
        /// "POV 0 Up", "Slider 0" into its components. Memoized: descriptors
        /// are immutable config vocabulary, so the parse is a pure function
        /// of the string, and the legacy grid re-parses ~30 fields per
        /// device per poll on un-resaved configs. Capped so a pathological
        /// config cannot grow the cache unbounded; past the cap the parse
        /// runs uncached.
        /// </summary>
        private const int DescriptorCacheCap = 4096;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string, MappingDescriptor> _descriptorCache = new();

        private static MappingDescriptor ParseDescriptor(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor) || descriptor == "0")
                return default;
            if (_descriptorCache.TryGetValue(descriptor, out var cached))
                return cached;
            var result = ParseDescriptorCore(descriptor);
            if (_descriptorCache.Count < DescriptorCacheCap)
                _descriptorCache[descriptor] = result;
            return result;
        }

        private static MappingDescriptor ParseDescriptorCore(string descriptor)
        {
            var result = new MappingDescriptor();

            string s = descriptor.Trim();

            // Check for invert/half prefix.
            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            {
                result.Inverted = true;
                result.HalfAxis = true;
                s = s.Substring(2);
            }
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            {
                result.HalfAxis = true;
                s = s.Substring(1);
            }
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
            {
                result.Inverted = true;
                s = s.Substring(1);
            }

            // Split remaining into parts.
            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return result;

            // Parse type.
            string typeName = parts[0].ToLowerInvariant();
            switch (typeName)
            {
                case "axis":
                    result.Type = MapType.Axis;
                    break;
                case "button":
                    result.Type = MapType.Button;
                    break;
                case "slider":
                    result.Type = MapType.Slider;
                    break;
                case "pov":
                    result.Type = MapType.POV;
                    break;
                default:
                    return result;
            }

            // Parse index.
            if (!int.TryParse(parts[1], out int index))
                return result;

            result.Index = index;

            // Parse POV direction if present.
            if (result.Type == MapType.POV && parts.Length >= 3)
            {
                result.PovDirection = parts[2];
            }

            result.IsValid = true;
            return result;
        }

        // ─────────────────────────────────────────────
        //  Button mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a descriptor (or pipe-separated list of descriptors) to a boolean button press.
        /// Multiple descriptors are OR'd: if ANY source is active, the button is pressed.
        /// 
        /// For buttons: returns true if the button is pressed.
        /// For axes: returns true if the axis exceeds a threshold (75%).
        /// For POV: returns true if the POV matches the specified direction.
        /// 
        /// Examples:
        ///   "Button 0"             → single source
        ///   "Button 0|Button 5"    → pressed if either Button 0 OR Button 5 is pressed
        ///   "Button 3|Axis 2"      → pressed if Button 3 is pressed OR Axis 2 exceeds threshold
        /// </summary>
        private static bool MapToButtonPressed(CustomInputState state, string descriptor,
            string deviceGuid, int slotIndex,
            int deadZonePercent = 0, int globalThresholdPercent = 50, bool bidirectional = false)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return false;

            // Support multiple descriptors separated by '|' (OR logic).
            if (descriptor.Contains('|'))
            {
                foreach (string part in descriptor.Split('|'))
                {
                    if (MapToButtonPressedSingle(state, part.Trim(), deviceGuid, slotIndex, deadZonePercent, globalThresholdPercent, bidirectional))
                        return true;
                }
                return false;
            }

            return MapToButtonPressedSingle(state, descriptor, deviceGuid, slotIndex, deadZonePercent, globalThresholdPercent, bidirectional);
        }

        // ── Wii pointer modes (issue #203) ──────────────

        // FPS Mouse constants, grounded in the Touchmote lineage
        // (Suegrini-4IR MouseHandler.cs): circular deadzone 0.021 of the
        // margin half-range = 0.042 in the [-1..+1] aim range, and the
        // three-segment response curve 0.65x / x-0.14 / 1.56x-0.56 with
        // breakpoints 0.4 and 0.75 (continuous, reaches 1 at 1). The
        // delta-accel and region-easing stacks are deliberate v2 tuning.
        private const float FpsAimDeadzone = 0.042f;

        // Per-(device, slot) last-update timestamps for FPS Mouse, so the
        // synthesized velocity is pixels-per-second rather than
        // pixels-per-poll (the poll loop targets 1 kHz but the velocity
        // must not scale with its actual rate). Stopwatch-based for
        // sub-millisecond resolution.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            (string Dev, int Slot), long> _fpsLastTick = new();

        /// <summary>The lineage's three-segment fpsmouse response curve on a
        /// deadzone-normalized magnitude in [0..1]. Shared with the unit
        /// tests.</summary>
        internal static float FpsResponseCurve(float n)
        {
            if (n <= 0f) return 0f;
            if (n <= 0.4f) return 0.65f * n;
            if (n <= 0.75f) return n - 0.14f;
            return Math.Min(1f, 1.56f * n - 0.56f);
        }

        /// <summary>Aspect-region half-extents for a border mode on a screen
        /// (Ryochan7 ScreenPositionCalculator.cs:760-797): pillarbox when the
        /// screen is wider than the target (dead band on X), letterbox when
        /// narrower (dead band on Y), full extent on the matching axis.
        /// Returned in normalized [0..1] screen space. Shared with the unit
        /// tests.</summary>
        internal static (float HalfW, float HalfH) ComputeAspectRegion(
            float screenW, float screenH, float targetAspect)
        {
            float sourceAspect = screenW / screenH;
            if (sourceAspect > targetAspect)
            {
                float dead = (1f - targetAspect / sourceAspect) * 0.5f;
                return (0.5f - dead, 0.5f);
            }
            else
            {
                float dead = (1f - sourceAspect / targetAspect) * 0.5f;
                return (0.5f, 0.5f - dead);
            }
        }

        /// <summary>Border-mode transform on one region-space aim point
        /// (region space: [-1..+1] per axis spans the region). Inside the
        /// region the point passes through; outside it pins to the border
        /// along the ray from the region center through the aim point
        /// (max-norm scale, equivalent to the lineage's atan2 edge
        /// intersection). Shared with the unit tests.</summary>
        internal static (float Rx, float Ry, bool Inside) TransformBorderAim(float rx, float ry)
        {
            float m = Math.Max(Math.Abs(rx), Math.Abs(ry));
            if (m <= 1f) return (rx, ry, true);
            float s = 1f / m;
            return (rx * s, ry * s, false);
        }

        /// <summary>Applies the per-(device, slot) Wii pointer mode (issue
        /// #203) to the KBM pointer lanes after Step 3 evaluated them.
        /// Mouse (0) is a no-op. FpsMouse (1) converts the absolute aim
        /// into relative velocity on the existing delta lane. Mouse43 /
        /// Mouse169 (2/3) confine the absolute cursor to an aspect region
        /// of the primary screen, pinning to the region border along the
        /// aim direction while tracked aim is outside the region. Sight
        /// loss drives nothing, so the cursor freezes at its last driven
        /// position: the same Touchmote lastPos idiom plain Mouse mode
        /// ships (ScreenPositionCalculator.cs:153-160 returns lastPos on
        /// !foundMidpoint; KeyboardMouseVirtualController.cs:112-124). A
        /// deliberate off-screen flick still parks the cursor ON the
        /// border, because live samples ride it there through
        /// TransformBorderAim before tracking dies. The rejected
        /// alternative (project the remembered aim to the border on loss,
        /// Ryochan7 lightbar MouseHandler.cs:1069-1130) assumes lightgun
        /// geometry where tracking outlives the calibrated bounds; on this
        /// hardware it snapped the cursor border-ward whenever tracking
        /// ended inside the region, and oscillated on boundary dot flicker
        /// (owner bench, 2026-07-11).</summary>
        internal static void ApplyPointerMode(ref KbmRawState raw,
            string thisDeviceGuid, int slotIndex, bool irPointerDrivesMouse,
            bool irDroveMouseX, bool irDroveMouseY)
        {
            if (!irPointerDrivesMouse)
                return;

            var pm = PadForge.Engine.Common.Mapping.SourceCoercion.IrPointerModeProvider?
                .Invoke(thisDeviceGuid ?? "", slotIndex);
            int mode = pm?.mode ?? 0;
            if (mode == 0)
                return;

            if (mode == 1)
            {
                var key = (thisDeviceGuid ?? "", slotIndex);
                // FPS Mouse: aim offset from center becomes velocity on the
                // relative lane. The evaluated aim already carries the
                // Pointer-tab smoothing and per-source sensitivity (the
                // slot-scoped tuned read), and the KBM VC's sub-pixel
                // accumulator provides the lineage's remainder carry. Speed
                // is normalized so the lineage default (35 px per 10 ms
                // report) lands at the same pixels-per-second here: the VC
                // moves MouseSensitivity(15) px per poll at full deflection,
                // polls run ~1 kHz, so full-scale delta * (speed / 150)
                // yields speed * 100 px/s.
                float ax = raw.MouseAbsValid && irDroveMouseX ? raw.MouseAbsX : 0f;
                float ay = raw.MouseAbsValid && irDroveMouseY ? raw.MouseAbsY : 0f;
                raw.MouseAbsX = 0f; raw.MouseAbsY = 0f; raw.MouseAbsValid = false;
                raw.MouseAbsXValid = raw.MouseAbsYValid = false;

                float mag = MathF.Sqrt(ax * ax + ay * ay);
                if (mag <= FpsAimDeadzone)
                {
                    // Only silence the axes IR owns: a mixed mapping (IR on
                    // one mouse axis, a stick on the other) keeps its
                    // stick-driven delta.
                    if (irDroveMouseX) raw.MouseDeltaX = 0;
                    if (irDroveMouseY) raw.MouseDeltaY = 0;
                    return;
                }
                float norm = Math.Min(1f, (mag - FpsAimDeadzone) / (1f - FpsAimDeadzone));
                float outMag = FpsResponseCurve(norm);
                float speed = Math.Clamp(pm?.fpsSpeed ?? 35f, 1f, 200f);
                // Time-based velocity: the speed knob is the lineage's
                // pixels per 10 ms report, i.e. speed * 100 px/s at full
                // deflection. The VC applies MouseSensitivity (15 px at
                // full scale) per submit, so per-update full-scale counts
                // = 32767 * (speed * 100 / 15) * dtSeconds. At the poll
                // loop's nominal 1 kHz this reduces to the lineage-matched
                // speed / 150 per tick, but measuring dt keeps the cursor
                // speed correct if the poll cadence ever varies.
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                long prevTicks = _fpsLastTick.TryGetValue(key, out var pt) ? pt : 0;
                _fpsLastTick[key] = nowTicks;
                float dtSec = prevTicks > 0
                    ? Math.Clamp((float)(nowTicks - prevTicks) / System.Diagnostics.Stopwatch.Frequency, 0f, 0.05f)
                    : 0.001f;
                float scale = outMag * 32767f * (speed * 100f / 15f) * dtSec / mag;
                if (irDroveMouseX)
                    raw.MouseDeltaX = (short)Math.Clamp((int)(ax * scale), short.MinValue, short.MaxValue);
                // The VC negates MouseDeltaY into screen-Y (positive = up
                // convention on the lane); aim Y is screen-aligned
                // (positive = down), so negate here for aim-down = cursor-down.
                if (irDroveMouseY)
                    raw.MouseDeltaY = (short)Math.Clamp(-(int)(ay * scale), short.MinValue, short.MaxValue);
                return;
            }

            // Border modes: confine the absolute cursor to the aspect region.
            // Sight loss = no drive = the cursor freezes where it was (see
            // the summary above). Checked before the screen query so the
            // freeze path stays pure.
            if (!raw.MouseAbsValid)
                return;

            float targetAspect = mode == 2 ? 4f / 3f : 16f / 9f;
            if (!PadForge.Services.CursorControlService.TryGetPrimarySize(out int scrW, out int scrH)
                || scrW <= 0 || scrH <= 0)
                return;
            var (halfW, halfH) = ComputeAspectRegion(scrW, scrH, targetAspect);
            if (halfW <= 0f || halfH <= 0f) return;

            // Screen-normalized aim [0..1] -> region space [-1..+1].
            float u = raw.MouseAbsX * 0.5f + 0.5f;
            float v = raw.MouseAbsY * 0.5f + 0.5f;
            float rx = (u - 0.5f) / halfW;
            float ry = (v - 0.5f) / halfH;

            var (px, py, _) = TransformBorderAim(rx, ry);
            float outU = 0.5f + px * halfW;
            float outV = 0.5f + py * halfH;
            raw.MouseAbsX = outU * 2f - 1f;
            raw.MouseAbsY = outV * 2f - 1f;
            raw.MouseAbsValid = true;
        }

        /// <summary>Finds the "IR Pointer" source feeding a KBM mouse target, so
        /// the Wii pointer can be routed as an ABSOLUTE cursor position
        /// (Touchmote-style) instead of a velocity delta (issue #146). Checks
        /// the mapping-set row's sources first (only ones owned by
        /// <paramref name="thisDeviceGuid"/>, since Step 3 runs per assigned
        /// device and state.Ir belongs to that device), then the legacy per-key
        /// descriptor. Returns null when the target is not IR-driven, which
        /// keeps the existing delta path untouched for every other source.</summary>
        /// <summary>
        /// Flick stick (#225): ticks every "Flick Stick ..." source on the
        /// ACTIVE KbmMouseX row and returns the summed mouse X counts for
        /// this frame. Layer-aware on purpose, unlike
        /// <see cref="FindIrPointerSource"/>'s Base-only walk: #225's
        /// headline is flick stick hosted on a shift layer, so the row
        /// resolution must ride <see cref="FindActiveRowForTarget"/>. While
        /// the hosting layer is off the row never evaluates, the tick's
        /// frame-sequence gap detection re-arms on the next engage, and no
        /// residual counts are emitted. No legacy per-key descriptor leg:
        /// the family is newer than the MappingSet grid, so no pre-grid
        /// config can carry it.
        /// </summary>
        private static int TickFlickStickSources(
            CustomInputState state, MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var row = FindActiveRowForTarget(mappingSet, "KbmMouseX", slotIndex, out _);
            var sources = row?.Sources;
            if (sources == null || sources.Count == 0) return 0;

            var runtime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            if (runtime == null) return 0;
            double dt = ComputeAndAdvanceDelta(slotIndex);

            int counts = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                if (src == null
                    || !PadForge.Engine.Common.Mapping.SourceCoercion.IsFlickStickDescriptor(src.Descriptor))
                    continue;
                // Same cross-device resolution the per-target evaluators use:
                // the source's own DeviceGuid wins; empty = this pass's device.
                CustomInputState devState;
                if (string.IsNullOrEmpty(src.DeviceGuid))
                    devState = state;
                else
                {
                    devState = LookupDeviceState(src.DeviceGuid);
                    // Offline-contributes-zero: don't tick flick state
                    // from another device's axes.
                    if (devState == null) continue;
                }
                counts += runtime.TickFlickStick(slotIndex, "KbmMouseX", i, src, devState,
                    dt, _stickTrimFrameSeq);
            }
            return counts;
        }

        private static PadForge.Engine.Data.MappingSource FindIrPointerSource(
            MappingSet mappingSet, string targetName, string legacyDesc, string thisDeviceGuid)
        {
            var row = FindBaseRowForTarget(mappingSet, targetName);
            if (row?.Sources != null)
            {
                foreach (var src in row.Sources)
                {
                    if (src?.Descriptor == null) continue;
                    if (!src.Descriptor.StartsWith("IR Pointer ", StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrEmpty(src.DeviceGuid)
                        && !string.Equals(src.DeviceGuid, thisDeviceGuid, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return src;
                }
            }
            // Legacy per-key descriptor: parse through the engine-owned
            // grammar so the I/IH/H prefix forms a legacy invert toggle
            // persists ("IIR Pointer X") still route to the absolute-aim
            // lane instead of diverging onto the velocity path. DeviceGuid
            // is stamped so the slot-scoped tuned read (bar offset,
            // smoothing, EMA key) attributes to this device rather than "".
            if (!string.IsNullOrEmpty(legacyDesc)
                && TryGetEngineOwnedSource(legacyDesc, out string legacyClean, out bool legacyInv, out bool legacyHalf)
                && legacyClean.StartsWith("IR Pointer ", StringComparison.Ordinal))
                return new PadForge.Engine.Data.MappingSource
                {
                    Descriptor = legacyClean,
                    Invert = legacyInv,
                    HalfAxis = legacyHalf,
                    DeviceGuid = thisDeviceGuid,
                };
            return null;
        }

        /// <summary>Finds an ENGAGED "Touchpad N Pointer ..." source feeding
        /// a KBM mouse target (#9 B-15), so the absolute touchpad pointer
        /// routes to the absolute cursor channel (KbmRawState.MouseAbs*)
        /// exactly like the Wii IR pointer above. LAYER-AWARE on purpose,
        /// unlike FindIrPointerSource's Base-only walk: the Workshop
        /// translator hosts mouse_region groups on action-set and
        /// mode-shift layers, so the row resolution must ride
        /// FindActiveRowForTarget (the flick-stick precedent).
        /// <para>ENGAGEMENT-GATED, unlike the IR finder: a corpus row can
        /// mix relative sources with pointer sources (gyro + stick + a
        /// mouse_region pad summed onto KbmMouseX, fixture 3456927474), and
        /// an unconditional absolute claim would silence the relative
        /// sources even with no finger on the pad. While no pointer source
        /// is engaged the caller falls through to the delta lane, where the
        /// pointer family reads 0 (a position is not a delta), so gyro aim
        /// stays live; the moment a finger lands in a source's window the
        /// row routes absolute and warps the cursor, Steam's mouse_region
        /// behavior. First engaged source wins when several pads' regions
        /// share the row. A lifted finger leaves MouseAbsValid unset and
        /// contributes no delta, so the cursor freezes either way.</para>
        /// Same cross-device discipline as the IR finder: only sources
        /// owned by <paramref name="thisDeviceGuid"/> (or the empty "device
        /// on this slot" guid) match, because the engagement gate reads
        /// THIS device's touchpad state. No legacy per-key leg: the family
        /// is newer than the MappingSet grid, so no pre-grid config can
        /// carry it.</summary>
        private static PadForge.Engine.Data.MappingSource FindEngagedTouchpadPointerSource(
            CustomInputState state, MappingSet mappingSet, string targetName,
            int slotIndex, string thisDeviceGuid)
        {
            var row = FindActiveRowForTarget(mappingSet, targetName, slotIndex, out _);
            if (row?.Sources == null) return null;
            foreach (var src in row.Sources)
            {
                if (src?.Descriptor == null) continue;
                if (!PadForge.Engine.Common.Mapping.SourceCoercion
                        .IsTouchpadPointerDescriptor(src.Descriptor)) continue;
                if (!string.IsNullOrEmpty(src.DeviceGuid)
                    && !string.Equals(src.DeviceGuid, thisDeviceGuid, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!PadForge.Engine.Common.Mapping.SourceCoercion
                        .IsTouchpadPointerEngaged(state, src.Descriptor)) continue;
                return src;
            }
            return null;
        }

        /// <summary>
        /// Engine-evaluated source families ("IR Pointer X/Y", "IR Brightness",
        /// "Balance ...", "Mouse Position X/Y", "Midi ...") are owned by
        /// SourceCoercion and are not part of the legacy Axis/Button/Slider/POV
        /// grammar, so ParseDescriptor silently drops them. Found on first Wii IR
        /// hardware contact (2026-07-01): "IR Pointer X" assigned to KbmMouseX
        /// tracked perfectly at the driver yet the mouse never moved, because the
        /// per-key KBM path fell through to ParseDescriptor. Same pre-parse
        /// delegation shape as the Touchpad branch in MapToButtonPressedSingle.
        /// Per-slot-tuned families (gyro, touchpad gestures) stay on the
        /// mapping-set path, which carries the slot context they need.
        /// SIBLING: SourceCoercion.IsPrefixExemptDescriptor is the OTHER
        /// grammar guarding the same I/H collision (the migrator + settings
        /// prefix-strip sites). A new engine-owned family added here must
        /// also join that allow-list if its name is 'I'/'H'-leading, or the
        /// migrator will mangle a descriptor this path accepts.
        /// </summary>
        private static bool IsEngineOwnedDescriptor(string s) =>
            s.StartsWith("IR Pointer ", StringComparison.Ordinal) ||
            s.Equals("IR Offscreen", StringComparison.Ordinal) ||
            s.Equals("IR Brightness", StringComparison.Ordinal) ||
            s.StartsWith("Balance ", StringComparison.Ordinal) ||
            s.StartsWith("Mouse Position ", StringComparison.Ordinal) ||
            s.StartsWith("Mouse Motion ", StringComparison.Ordinal) ||
            s.StartsWith("Midi ", StringComparison.Ordinal);

        /// <summary>
        /// Recognizes an engine-owned descriptor including its legacy I/IH/H
        /// invert/half-axis prefix form. A legacy row's invert toggle rebuilds
        /// the descriptor as "IMouse Motion X" (MappingItem.RebuildDescriptor),
        /// which fails the plain IsEngineOwnedDescriptor check AND the legacy
        /// Axis/Button grammar, so the row silently evaluated dead. The strip
        /// is REMAINDER-GATED: a prefix letter comes off only when what remains
        /// is itself engine-owned, which makes the parse self-exempting for the
        /// 'I'-leading families ("IR Pointer X" keeps its I because "R Pointer
        /// X" is not engine-owned) with no reliance on the exemption predicate.
        /// </summary>
        private static bool TryGetEngineOwnedSource(string descriptor,
            out string clean, out bool inverted, out bool halfAxis)
        {
            inverted = false;
            halfAxis = false;
            clean = descriptor?.Trim() ?? "";
            if (clean.Length == 0) return false;

            if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase)
                && IsEngineOwnedDescriptor(clean.Substring(2)))
            {
                inverted = true; halfAxis = true; clean = clean.Substring(2);
            }
            else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase)
                && clean.Length > 1 && IsEngineOwnedDescriptor(clean.Substring(1)))
            {
                inverted = true; clean = clean.Substring(1);
            }
            else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase)
                && clean.Length > 1 && IsEngineOwnedDescriptor(clean.Substring(1)))
            {
                halfAxis = true; clean = clean.Substring(1);
            }

            return IsEngineOwnedDescriptor(clean);
        }

        /// <summary>
        /// Maps a single descriptor to a boolean button press.
        /// </summary>
        private static bool MapToButtonPressedSingle(CustomInputState state, string descriptor,
            string deviceGuid, int slotIndex,
            int deadZonePercent = 0, int globalThresholdPercent = 50, bool bidirectional = false)
        {
            // Touchpad-typed descriptors that resolve to a bool. Parallel to the
            // "Touchpad N Finger M X/Y/Down" descriptors consumed by Step 3's
            // touchpad output path; here we recognize:
            //   "Touchpad N Click"          → state.Buttons[16] (SDL_GAMEPAD_BUTTON_TOUCHPAD;
            //                                  N>0 returns false until a per-device
            //                                  multi-touchpad-click extension lands)
            //   "Touchpad N Finger M Down"  → state.TouchpadDown[M] for finger M
            // Resolved BEFORE ParseDescriptor because that parser only knows
            // Axis / Button / Slider / POV — adding a fifth MapType would touch
            // many call sites, but bool-yielding touchpad descriptors only
            // need to flow through MapToButtonPressed.
            if (!string.IsNullOrEmpty(descriptor)
                && descriptor.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                return MapTouchpadButton(state, descriptor.Trim());
            }

            // Engine-owned families (IR Pointer / IR Brightness / Balance /
            // Mouse Position / Mouse Motion / Midi): threshold like the mapping
            // grid does. Accepts the legacy I/IH/H prefix form so an inverted
            // legacy row still evaluates.
            if (!string.IsNullOrEmpty(descriptor)
                && TryGetEngineOwnedSource(descriptor, out string engClean, out bool engInv, out bool engHalf))
            {
                var engineSrc = new PadForge.Engine.Data.MappingSource
                {
                    Descriptor = engClean,
                    DeviceGuid = deviceGuid,
                    DeadZone = deadZonePercent,
                    Invert = engInv,
                    HalfAxis = engHalf,
                };
                return PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForButtonTarget(
                    state, engineSrc, globalThresholdPercent, slotIndex);
            }

            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid)
                return false;

            switch (desc.Type)
            {
                case MapType.Button:
                    if (desc.Index >= 0 && desc.Index < state.Buttons.Length)
                        return state.Buttons[desc.Index];
                    return false;

                case MapType.Axis:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxAxis)
                    {
                        int value = state.Axis[desc.Index];
                        double t = Math.Max(deadZonePercent > 0 ? deadZonePercent : globalThresholdPercent, 1) / 100.0;
                        if (desc.HalfAxis)
                        {
                            if (bidirectional)
                            {
                                // Either side of center past deadzone counts.
                                int delta = value - 32768;
                                if (delta < 0) delta = -delta;
                                return delta > (int)(32767 * t);
                            }
                            if (desc.Inverted)
                                return value < (int)(32767 * (1.0 - t));
                            else
                                return value > (int)(32768 + 32767 * t);
                        }
                        int hi = (int)(t * 65535);
                        if (desc.Inverted)
                            return value < 65535 - hi;
                        else
                            return value > hi;
                    }
                    return false;

                case MapType.Slider:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxSliders)
                    {
                        int value = state.Sliders[desc.Index];
                        double t = Math.Max(deadZonePercent > 0 ? deadZonePercent : globalThresholdPercent, 1) / 100.0;
                        if (desc.HalfAxis)
                        {
                            if (bidirectional)
                            {
                                int delta = value - 32768;
                                if (delta < 0) delta = -delta;
                                return delta > (int)(32767 * t);
                            }
                            if (desc.Inverted)
                                return value < (int)(32767 * (1.0 - t));
                            else
                                return value > (int)(32768 + 32767 * t);
                        }
                        int hi = (int)(t * 65535);
                        if (desc.Inverted)
                            return value < 65535 - hi;
                        else
                            return value > hi;
                    }
                    return false;

                case MapType.POV:
                    if (desc.Index >= 0 && desc.Index < state.Povs.Length)
                    {
                        return IsPovDirectionActive(state.Povs[desc.Index], desc.PovDirection);
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Checks if a POV value matches a specified direction string.
        /// </summary>
        private static bool IsPovDirectionActive(int povValue, string direction)
        {
            if (povValue < 0) return false; // Centered
            if (string.IsNullOrEmpty(direction)) return povValue >= 0; // Any direction

            // Centidegrees: 0=Up, 4500=UpRight, 9000=Right, 13500=DownRight,
            // 18000=Down, 22500=DownLeft, 27000=Left, 31500=UpLeft.
            // Cardinals use ±67.5° tolerance; diagonals use ±22.5° (exact sector).
            switch (direction.ToLowerInvariant())
            {
                case "up":
                    return povValue >= 29250 || povValue <= 6750;
                case "right":
                    return povValue >= 2250 && povValue <= 15750;
                case "down":
                    return povValue >= 11250 && povValue <= 24750;
                case "left":
                    return povValue >= 20250 && povValue <= 33750;
                case "upright":
                    return povValue >= 2250 && povValue <= 6750;
                case "downright":
                    return povValue >= 11250 && povValue <= 15750;
                case "downleft":
                    return povValue >= 20250 && povValue <= 24750;
                case "upleft":
                    return povValue >= 29250 && povValue <= 33750;
                default:
                    return false;
            }
        }

        // ─────────────────────────────────────────────
        //  D-Pad from POV mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// If the DPad mapping descriptor points to a POV hat (or pipe-separated list),
        /// extracts the directional components and sets the corresponding D-pad button flags.
        /// </summary>
        private static void MapDPadFromPov(CustomInputState state, string descriptor, ref Gamepad gp)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return;

            // Support multiple descriptors separated by '|'.
            if (descriptor.Contains('|'))
            {
                foreach (string part in descriptor.Split('|'))
                {
                    MapDPadFromPovSingle(state, part.Trim(), ref gp);
                }
                return;
            }

            MapDPadFromPovSingle(state, descriptor, ref gp);
        }

        /// <summary>
        /// Maps a single POV descriptor to D-pad button flags.
        /// </summary>
        private static void MapDPadFromPovSingle(CustomInputState state, string descriptor, ref Gamepad gp)
        {
            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid || desc.Type != MapType.POV)
                return;

            if (desc.Index < 0 || desc.Index >= state.Povs.Length)
                return;

            int pov = state.Povs[desc.Index];
            if (pov < 0) return; // Centered

            if (IsPovDirectionActive(pov, "Up"))
                gp.SetButton(Gamepad.DPAD_UP, true);
            if (IsPovDirectionActive(pov, "Down"))
                gp.SetButton(Gamepad.DPAD_DOWN, true);
            if (IsPovDirectionActive(pov, "Left"))
                gp.SetButton(Gamepad.DPAD_LEFT, true);
            if (IsPovDirectionActive(pov, "Right"))
                gp.SetButton(Gamepad.DPAD_RIGHT, true);
        }

        // ─────────────────────────────────────────────
        //  Trigger mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a descriptor (or pipe-separated list) to a trigger value (0–65535).
        /// Multiple descriptors: the highest value wins.
        ///
        /// Examples:
        ///   "Axis 4"               → single source
        ///   "Axis 4|Button 8"      → max of axis value or button (0 or 65535)
        /// </summary>
        private static ushort MapToTrigger(CustomInputState state, string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return 0;

            // Support multiple descriptors separated by '|' (max value wins).
            if (descriptor.Contains('|'))
            {
                ushort best = 0;
                foreach (string part in descriptor.Split('|'))
                {
                    ushort val = MapToTriggerSingle(state, part.Trim());
                    if (val > best)
                        best = val;
                }
                return best;
            }

            return MapToTriggerSingle(state, descriptor);
        }

        /// <summary>
        /// Maps a single descriptor to a trigger value (0–65535).
        /// </summary>
        private static ushort MapToTriggerSingle(CustomInputState state, string descriptor)
        {
            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid)
                return 0;

            // POV → trigger needs digital semantics, not the axis-style
            // 32767-at-rest baseline GetRawValue uses for stick mapping.
            // A POV mapped to a trigger should sit at 0 when the POV is
            // centered and at 65535 when the configured direction is
            // active; without this, a centered POV reports ~50% trigger
            // pull at rest.
            if (desc.Type == MapType.POV
                && desc.Index >= 0 && desc.Index < state.Povs.Length)
            {
                bool active = IsPovDirectionActive(state.Povs[desc.Index], desc.PovDirection);
                if (desc.Inverted) active = !active;
                return active ? (ushort)65535 : (ushort)0;
            }

            int rawValue = GetRawValue(state, desc);

            // Keep full unsigned 16-bit range (0–65535) for trigger precision.
            if (desc.Inverted)
                rawValue = 65535 - rawValue;

            if (desc.HalfAxis)
            {
                // Half-axis: only use the upper half (32768–65535 → 0–65535).
                rawValue = Math.Max(0, rawValue - 32768);
                return (ushort)Math.Clamp(rawValue * 65535 / 32767, 0, 65535);
            }

            // Full axis: already 0–65535.
            return (ushort)Math.Clamp(rawValue, 0, 65535);
        }

        // ─────────────────────────────────────────────
        //  Thumbstick axis mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a descriptor (or pipe-separated list) to a signed thumbstick axis value (-32768 to 32767).
        /// Multiple descriptors: the source with the largest absolute magnitude wins.
        /// 
        /// Examples:
        ///   "Axis 1"           → single source
        ///   "Axis 1|Axis 3"    → whichever axis has larger magnitude
        /// </summary>
        private static short MapToThumbAxis(CustomInputState state, string descriptor,
            string deviceGuid, int slotIndex)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return 0;

            // Support multiple descriptors separated by '|' (largest magnitude wins).
            if (descriptor.Contains('|'))
            {
                short best = 0;
                foreach (string part in descriptor.Split('|'))
                {
                    short val = MapToThumbAxisSingle(state, part.Trim(), deviceGuid, slotIndex);
                    if (Math.Abs(val) > Math.Abs(best))
                        best = val;
                }
                return best;
            }

            return MapToThumbAxisSingle(state, descriptor, deviceGuid, slotIndex);
        }

        /// <summary>
        /// Maps a single descriptor to a signed thumbstick axis value.
        /// </summary>
        private static short MapToThumbAxisSingle(CustomInputState state, string descriptor,
            string deviceGuid, int slotIndex)
        {
            // Engine-owned families (IR Pointer / IR Brightness / Balance /
            // Mouse Position / Midi): bipolar [-1..+1] scaled to the signed
            // axis range, same evaluator the mapping grid uses.
            if (!string.IsNullOrWhiteSpace(descriptor)
                && TryGetEngineOwnedSource(descriptor, out string engClean, out bool engInv, out bool engHalf))
            {
                float v = PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForBipolarAxisTarget(
                    state,
                    new PadForge.Engine.Data.MappingSource { Descriptor = engClean, DeviceGuid = deviceGuid, Invert = engInv, HalfAxis = engHalf },
                    slotIndex);
                return (short)Math.Clamp((int)(v * short.MaxValue), short.MinValue, short.MaxValue);
            }

            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid)
                return 0;

            int rawValue = GetRawValue(state, desc);

            // Convert unsigned (0–65535) to signed (-32768 to 32767).
            int signed = rawValue - 32768;

            if (desc.Inverted)
                signed = -signed;

            // Clamp to short range.
            return (short)Math.Clamp(signed, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Maps a thumbstick axis using both positive and negative descriptors.
        /// When negDescriptor is empty, delegates to MapToThumbAxis (existing behavior).
        /// When both are set (typically buttons), pos pressed → +32767, neg pressed → -32768,
        /// both pressed → 0 (cancel out).
        /// </summary>
        private static short MapToThumbAxisWithNeg(CustomInputState state, string posDescriptor, string negDescriptor,
            string deviceGuid, int slotIndex)
        {
            if (string.IsNullOrWhiteSpace(negDescriptor))
                return MapToThumbAxis(state, posDescriptor, deviceGuid, slotIndex);

            // Both descriptors exist. Treat as digital directions.
            bool posActive = MapToButtonPressed(state, posDescriptor, deviceGuid, slotIndex);
            bool negActive = MapToButtonPressed(state, negDescriptor, deviceGuid, slotIndex);

            if (posActive && negActive) return 0;
            if (posActive) return short.MaxValue;
            if (negActive) return short.MinValue;
            return 0;
        }

        /// <summary>
        /// Maps a Custom Extended trigger-axis input descriptor pair to a
        /// signed short suitable for a trigger slot in
        /// <see cref="RawHidState.Axes"/>. The companion to
        /// <see cref="MapToThumbAxisWithNeg"/> for the trigger half of the
        /// dispatch in <see cref="MapInputToExtendedRaw"/>.
        ///
        /// <para><b>Why a separate mapper.</b> The signed-short axis
        /// representation puts the unsigned 16-bit zero point at
        /// <c>short.MinValue</c> (-32768). A stick with no input rests at
        /// signed <c>0</c> (= wire 50%, centered) — that's the correct rest
        /// for a stick. A trigger with no input rests at signed
        /// <c>short.MinValue</c> (= wire 0%, released) — that's the correct
        /// rest for a trigger. The thumbstick mapper inherits "0 = rest"
        /// from <see cref="MapToThumbAxisSingle"/>'s
        /// <c>if (!desc.IsValid) return 0;</c> guard, which is wrong for
        /// trigger slots; an unmapped Custom Extended trigger axis routed
        /// through the stick mapper paints the wire at 50% (centered)
        /// instead of 0% (released). This mapper substitutes
        /// <see cref="short.MinValue"/> in every "no measurable input" path
        /// so a trigger slot rests at released regardless of how the user
        /// configured (or didn't configure) the descriptor pair.</para>
        ///
        /// <para><b>Valid-descriptor paths are unchanged.</b> A mapped
        /// physical trigger axis goes through the same
        /// <c>MapToThumbAxis</c> codepath the stick mapper uses (raw 0..65535
        /// shifted to signed -32768..+32767), which the trigger slot already
        /// expects: released physical trigger (raw 0) lands at
        /// short.MinValue, fully pressed (raw 65535) at short.MaxValue.
        /// Buttons mapped to a trigger axis behave identically — released
        /// button (raw 0) → short.MinValue, pressed (raw 65535) →
        /// short.MaxValue. Only the rest-when-empty fallback differs.</para>
        ///
        /// <para><b>Pos+neg digital pair.</b> Trigger Mappings don't expose
        /// a negative-direction descriptor (triggers are unidirectional),
        /// so the pair branch shouldn't fire in normal use. Handled defensively
        /// for users who set both via XML edit or a future feature: only
        /// posActive presses the trigger; negActive alone or neither active
        /// reads as released. There's no "negative trigger" to push the
        /// value below released, unlike a stick's left/right pair.</para>
        /// </summary>
        private static short MapToRawTriggerAxis(CustomInputState state, string posDescriptor, string negDescriptor,
            string deviceGuid, int slotIndex)
        {
            if (string.IsNullOrWhiteSpace(negDescriptor))
            {
                // Single descriptor: empty → released; valid → analog read;
                // invalid → released (the stick mapper would have returned
                // 0 here, which is wrong for trigger rest).
                if (string.IsNullOrWhiteSpace(posDescriptor))
                    return short.MinValue;
                // Engine-owned families read unipolar [0..1] for a trigger
                // target and map to the wire range (rest = short.MinValue).
                if (TryGetEngineOwnedSource(posDescriptor, out string engClean, out bool engInv, out bool engHalf))
                {
                    float t = PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForTriggerTarget(
                        state,
                        new PadForge.Engine.Data.MappingSource { Descriptor = engClean, DeviceGuid = deviceGuid, Invert = engInv, HalfAxis = engHalf },
                        slotIndex);
                    return (short)Math.Clamp((int)(t * 65535f) - 32768, short.MinValue, short.MaxValue);
                }
                var desc = ParseDescriptor(posDescriptor);
                if (!desc.IsValid)
                    return short.MinValue;
                return MapToThumbAxis(state, posDescriptor, deviceGuid, slotIndex);
            }

            // Pos+neg digital pair. Triggers are unidirectional, so neg
            // doesn't push below released; it only fails to press.
            bool posActive = MapToButtonPressed(state, posDescriptor, deviceGuid, slotIndex);
            if (posActive) return short.MaxValue;
            return short.MinValue;
        }

        // ─────────────────────────────────────────────
        //  Raw value extraction
        // ─────────────────────────────────────────────

        /// <summary>
        /// Gets the raw unsigned value (0–65535) from the input state based
        /// on the mapping descriptor's type and index.
        /// For buttons, returns 0 or 65535.
        /// For POV, returns axis-equivalent based on direction.
        /// </summary>
        private static int GetRawValue(CustomInputState state, MappingDescriptor desc)
        {
            switch (desc.Type)
            {
                case MapType.Axis:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxAxis)
                        return state.Axis[desc.Index];
                    return 0;

                case MapType.Slider:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxSliders)
                        return state.Sliders[desc.Index];
                    return 0;

                case MapType.Button:
                    if (desc.Index >= 0 && desc.Index < state.Buttons.Length)
                        return state.Buttons[desc.Index] ? 65535 : 0;
                    return 0;

                case MapType.POV:
                    // Map POV direction to axis value.
                    if (desc.Index >= 0 && desc.Index < state.Povs.Length)
                    {
                        int pov = state.Povs[desc.Index];
                        return PovDirectionToAxisValue(pov, desc.PovDirection);
                    }
                    return 32767; // Center

                default:
                    return 0;
            }
        }

        /// <summary>
        /// Converts a POV direction to an axis-equivalent value (0–65535).
        /// For Up/Left directions: active → 0, inactive → 32767.
        /// For Down/Right directions: active → 65535, inactive → 32767.
        /// </summary>
        private static int PovDirectionToAxisValue(int povValue, string direction)
        {
            if (string.IsNullOrEmpty(direction))
                return 32767;

            bool active = IsPovDirectionActive(povValue, direction);

            switch (direction.ToLowerInvariant())
            {
                case "up":
                case "left":
                    return active ? 0 : 32767;

                case "down":
                case "right":
                    return active ? 65535 : 32767;

                default:
                    return 32767;
            }
        }

        // ─────────────────────────────────────────────
        //  Dead zone processing
        // ─────────────────────────────────────────────

        /// <summary>
        /// Applies deadzone, anti-deadzone, and linear scaling to a pair
        /// of thumbstick axes (X and Y) using the specified deadzone shape algorithm.
        /// </summary>
        private static void ApplyDeadZone(ref short axisX, ref short axisY,
            double deadZoneX, double deadZoneY,
            double antiDeadZoneX, double antiDeadZoneY, double linear,
            double maxRangeX, double maxRangeY,
            double maxRangeXNeg, double maxRangeYNeg,
            double[] lutX, double[] lutY,
            DeadZoneShape shape)
        {
            // Axial: existing independent per-axis behavior.
            if (shape == DeadZoneShape.Axial)
            {
                axisX = ApplySingleDeadZone(axisX, deadZoneX, antiDeadZoneX, linear, maxRangeX, maxRangeXNeg, lutX);
                axisY = ApplySingleDeadZone(axisY, deadZoneY, antiDeadZoneY, linear, maxRangeY, maxRangeYNeg, lutY);
                return;
            }

            // ── Common normalization to [-1, 1] ──
            double nx = axisX / 32768.0;
            double ny = axisY / 32768.0;
            double signX = Math.Sign(nx), signY = Math.Sign(ny);
            double magX = Math.Abs(nx), magY = Math.Abs(ny);
            double dzXn = deadZoneX / 100.0, dzYn = deadZoneY / 100.0;
            // Pick max range based on direction of input.
            double mrXn = (nx >= 0 ? maxRangeX : maxRangeXNeg) / 100.0;
            double mrYn = (ny >= 0 ? maxRangeY : maxRangeYNeg) / 100.0;
            if (mrXn <= dzXn) mrXn = Math.Min(dzXn + 0.01, 1.0);
            if (mrYn <= dzYn) mrYn = Math.Min(dzYn + 0.01, 1.0);

            double remX, remY;

            switch (shape)
            {
                case DeadZoneShape.Radial:
                    ComputeRadial(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: false, out remX, out remY);
                    break;
                case DeadZoneShape.ScaledRadial:
                    ComputeRadial(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: true, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedAxial:
                    ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: false, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedScaledAxial:
                    ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: true, out remX, out remY);
                    break;
                case DeadZoneShape.Hybrid:
                    ComputeHybrid(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                        out remX, out remY, out signX, out signY);
                    break;
                default:
                    remX = magX; remY = magY;
                    break;
            }

            // ── Post-DZ per-axis pipeline: curve → anti-DZ → linear → output ──
            axisX = ApplyPostDeadZone(remX, signX, antiDeadZoneX, linear, lutX);
            axisY = ApplyPostDeadZone(remY, signY, antiDeadZoneY, linear, lutY);
        }

        /// <summary>
        /// Post-deadzone per-axis processing: sensitivity curve, anti-deadzone, linear.
        /// Input remapped is [0,1], sign is ±1.
        /// </summary>
        private static short ApplyPostDeadZone(double remapped, double sign,
            double antiDeadZone, double linear, double[] lut)
        {
            if (remapped <= 0 && antiDeadZone <= 0)
                return 0;

            if (lut != null)
                remapped = Common.CurveLut.Lookup(lut, Math.Clamp(remapped, 0, 1));

            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);

            if (linear > 0)
            {
                double linearFactor = linear / 100.0;
                output = remapped * linearFactor + output * (1.0 - linearFactor);
            }

            double result = sign * output * 32767.0;
            return (short)Math.Clamp(result, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Radial / Scaled Radial deadzone with elliptical support.
        /// </summary>
        internal static void ComputeRadial(double nx, double ny,
            double magX, double magY,
            double dzXn, double dzYn, double mrXn, double mrYn,
            bool rescale, out double remX, out double remY)
        {
            // If both DZs are zero, no deadzone gating needed.
            if (dzXn <= 0 && dzYn <= 0)
            {
                remX = Math.Min(magX / mrXn, 1.0);
                remY = Math.Min(magY / mrYn, 1.0);
                return;
            }

            // Elliptical distance: (nx/dzX)² + (ny/dzY)² < 1 means inside DZ.
            const double eps = 1e-10;
            double effDzX = Math.Max(dzXn, eps);
            double effDzY = Math.Max(dzYn, eps);
            double edx = nx / effDzX;
            double edy = ny / effDzY;
            double ellipDist = Math.Sqrt(edx * edx + edy * edy);

            if (ellipDist < 1.0)
            {
                remX = 0; remY = 0;
                return;
            }

            if (!rescale)
            {
                // Radial (no rescale): pass through raw magnitudes, clamped at max range.
                remX = Math.Min(magX / mrXn, 1.0);
                remY = Math.Min(magY / mrYn, 1.0);
                return;
            }

            // Scaled Radial: rescale magnitude from [dzR, mrR] to [0, 1].
            double rawMag = Math.Sqrt(nx * nx + ny * ny);
            if (rawMag < eps) { remX = 0; remY = 0; return; }

            double ux = nx / rawMag, uy = ny / rawMag; // unit direction

            // DZ ellipse radius in this direction.
            double dxu = ux / effDzX, dyu = uy / effDzY;
            double dzR = 1.0 / Math.Sqrt(dxu * dxu + dyu * dyu);

            // Max-range ellipse radius in this direction.
            double mxu = ux / mrXn, myu = uy / mrYn;
            double mrR = 1.0 / Math.Sqrt(mxu * mxu + myu * myu);
            if (mrR <= dzR) mrR = dzR + 0.01;

            double scaledMag = Math.Clamp((rawMag - dzR) / (mrR - dzR), 0, 1);

            // Project back to per-axis, maintaining direction.
            remX = scaledMag * Math.Abs(ux);
            remY = scaledMag * Math.Abs(uy);
        }

        /// <summary>
        /// Sloped Axial / Sloped Scaled Axial deadzone.
        /// DZ on each axis scales with the other axis magnitude.
        /// </summary>
        internal static void ComputeSloped(double magX, double magY,
            double dzXn, double dzYn, double mrXn, double mrYn,
            bool rescale, out double remX, out double remY)
        {
            // Effective DZ: when other axis is large, DZ grows → easier cardinal lock.
            // When both are small (near center), DZ shrinks → less center filtering.
            double effDzX = dzXn * magY;
            double effDzY = dzYn * magX;

            if (magX < effDzX)
                remX = 0;
            else if (rescale)
            {
                double range = mrXn - effDzX;
                remX = range > 0 ? Math.Min((magX - effDzX) / range, 1.0) : 0;
            }
            else
                remX = Math.Min(magX / mrXn, 1.0);

            if (magY < effDzY)
                remY = 0;
            else if (rescale)
            {
                double range = mrYn - effDzY;
                remY = range > 0 ? Math.Min((magY - effDzY) / range, 1.0) : 0;
            }
            else
                remY = Math.Min(magY / mrYn, 1.0);
        }

        /// <summary>
        /// Hybrid: Scaled Radial first (center noise), then Sloped Scaled Axial (cardinal precision).
        /// </summary>
        internal static void ComputeHybrid(double nx, double ny,
            double magX, double magY,
            double dzXn, double dzYn, double mrXn, double mrYn,
            out double remX, out double remY, out double signX, out double signY)
        {
            // Stage 1: Scaled Radial
            ComputeRadial(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                rescale: true, out double srX, out double srY);

            // Stage 2: Sloped Scaled Axial on the radial output
            // Signs are from the original input.
            signX = Math.Sign(nx);
            signY = Math.Sign(ny);
            ComputeSloped(srX, srY, dzXn, dzYn, 1.0, 1.0,
                rescale: true, out remX, out remY);
        }

        /// <summary>
        /// Parses a DeadZoneShape from a string. Returns ScaledRadial for null/empty/invalid.
        /// </summary>
        internal static DeadZoneShape ParseDeadZoneShape(string value)
        {
            if (string.IsNullOrEmpty(value)) return DeadZoneShape.ScaledRadial;
            if (int.TryParse(value, out int v) && Enum.IsDefined(typeof(DeadZoneShape), v))
                return (DeadZoneShape)v;
            return DeadZoneShape.ScaledRadial;
        }

        /// <summary>
        /// Applies a center offset correction to a single axis. The offset is a percentage
        /// of the full axis range (-100 to 100). Applied before deadzone processing.
        /// </summary>
        private static short ApplyCenterOffset(short value, double offsetPercent)
        {
            if (offsetPercent == 0) return value;
            int offsetRaw = (int)(offsetPercent / 100.0 * 32768);
            return (short)Math.Clamp(value + offsetRaw, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Applies deadzone processing to a single axis.
        /// </summary>
        private static short ApplySingleDeadZone(short value, double deadZone, double antiDeadZone, double linear, double maxRangePos = 100, double maxRangeNeg = 100, double[] lut = null)
        {
            if (deadZone <= 0 && antiDeadZone <= 0 && maxRangePos >= 100 && maxRangeNeg >= 100 && lut == null)
                return value;

            // Normalize to float (-1.0 to 1.0).
            double norm = value / 32768.0;
            double sign = Math.Sign(norm);
            double magnitude = Math.Abs(norm);

            // Deadzone: values within the deadzone are zeroed.
            double dzNorm = deadZone / 100.0;
            if (magnitude < dzNorm)
                return 0;

            // Max range: cap the input ceiling so full output is reached at this %.
            // Pick positive or negative direction max range based on input sign.
            double maxNorm = (norm >= 0 ? maxRangePos : maxRangeNeg) / 100.0;
            if (maxNorm <= dzNorm)
                maxNorm = Math.Min(dzNorm + 0.01, 1.0);

            // Remap from [dzNorm, maxNorm] to [0, 1].
            double remapped = Math.Min((magnitude - dzNorm) / (maxNorm - dzNorm), 1.0);

            // Sensitivity curve: spline LUT lookup.
            if (lut != null)
                remapped = Common.CurveLut.Lookup(lut, remapped);

            // Anti-deadzone: offset the output minimum.
            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);

            // Linear adjustment (simplified: 0 = default curve, 100 = fully linear).
            if (linear > 0)
            {
                double linearFactor = linear / 100.0;
                output = remapped * linearFactor + output * (1.0 - linearFactor);
            }

            // Apply sign and clamp to short range.
            double result = sign * output * 32767.0;
            return (short)Math.Clamp(result, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Applies deadzone, anti-deadzone, and max range processing to a trigger value (0–65535).
        /// Deadzone: values below the threshold percentage are zeroed.
        /// Max range: caps the input so full physical press maps to this percentage ceiling.
        /// Anti-deadzone: remaps the output so small presses register past the game's deadzone.
        /// </summary>
        private static ushort ApplyTriggerDeadZone(ushort value, double deadZone, double antiDeadZone, double maxRange, double[] lut = null)
        {
            if (deadZone <= 0 && antiDeadZone <= 0 && maxRange >= 100 && lut == null)
                return value;

            // Normalize to 0.0–1.0.
            double norm = value / 65535.0;

            // Dead zone: values below threshold are zeroed.
            double dzNorm = deadZone / 100.0;
            if (norm < dzNorm)
                return 0;

            // Max range: cap the input ceiling.
            double maxNorm = maxRange / 100.0;
            if (maxNorm <= dzNorm)
                maxNorm = dzNorm + 0.01;

            // Remap from [dzNorm, maxNorm] to [0, 1].
            double remapped = Math.Clamp((norm - dzNorm) / (maxNorm - dzNorm), 0.0, 1.0);

            // Sensitivity curve: spline LUT lookup.
            if (lut != null)
                remapped = Common.CurveLut.Lookup(lut, remapped);

            // Anti-deadzone: offset the output minimum.
            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);

            return (ushort)Math.Clamp((int)(output * 65535.0), 0, 65535);
        }

        // Poll-hot parse memo: ApplyPadSettingTuning re-parses ~26 tuning
        // strings per device per tick, and the profiler put
        // System.Number.TryParseFloat on the poll thread inside it. The
        // distinct-string population is tiny (user-entered tuning values), so
        // cache the parse outcome per string. Same shape as the shipped
        // SourceCoercion.s_typeIndexCache. The null sentinel marks an
        // unparseable string, so each call site still gets ITS OWN default.
        //
        // Capped: profile imports deserialize arbitrary strings into these
        // fields, and an uncapped cache would root every distinct key until
        // process exit. Past the cap, values still parse; they just stop
        // being remembered. Ints parse invariant to match the double policy
        // (memoizing a CurrentCulture-sensitive parse would make the first
        // culture win globally).
        private const int ParseCacheCap = 4096;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double?>
            s_doubleParseCache = new(StringComparer.Ordinal);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?>
            s_intParseCache = new(StringComparer.Ordinal);

        private static double TryParseDoubleStatic(string value, double defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            if (!s_doubleParseCache.TryGetValue(value, out var parsed))
            {
                parsed = double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double result)
                    ? result : (double?)null;
                if (s_doubleParseCache.Count < ParseCacheCap)
                    s_doubleParseCache[value] = parsed;
            }
            return parsed ?? defaultValue;
        }

        private static int TryParseIntStatic(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            if (!s_intParseCache.TryGetValue(value, out var parsed))
            {
                parsed = int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int result)
                    ? result : (int?)null;
                if (s_intParseCache.Count < ParseCacheCap)
                    s_intParseCache[value] = parsed;
            }
            return parsed ?? defaultValue;
        }

        // ─────────────────────────────────────────────
        //  Extended Custom mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a CustomInputState to a RawHidState using the PadSetting's Extended
        /// dictionary-based mappings. Used for custom Extended configurations with
        /// arbitrary numbers of axes, buttons, and POVs.
        /// </summary>
        private static RawHidState MapInputToExtendedRaw(CustomInputState state, PadSetting ps,
            CustomControllerLayout cfg,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var raw = RawHidState.Create(cfg.Axes, cfg.Buttons, cfg.Povs);
            raw.Clear(); // POVs need to start centered
            int vgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // ── Axes ── (MappingSet-first; fall back to legacy single-source)
            // Raw Extended axes use signed short internally. SubmitRawState converts to unsigned
            // HID range via (signed + 32768) / 2, preserving the natural direction:
            //   signed negative → HID low (0 = up/left)
            //   signed positive → HID high (32767 = down/right)
            // Stick slots rest at signed 0 (= wire 50%); trigger slots rest at
            // short.MinValue (= wire 0%). Different MappingSet evaluator per slot
            // type so an unmapped trigger doesn't sit at 50% on the wire.
            for (int i = 0; i < cfg.Axes && i < raw.Axes.Length; i++)
            {
                string axisKey = CachedName(ref _extAxisNames, i, "RawAxis");
                bool isTrigger = cfg.IsTriggerSlot(i);
                short axisValue;
                if (isTrigger
                    ? TryEvaluateMappingSetRawTrigger(state, mappingSet, thisDeviceGuid, slotIndex, axisKey, out axisValue)
                    : TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid, slotIndex, axisKey, out axisValue))
                {
                    raw.Axes[i] = axisValue;
                }
                else
                {
                    string posDesc = ps.GetRawMapping(axisKey);
                    string negDesc = ps.GetRawMapping(CachedName(ref _extAxisNegNames, i, "RawAxis", "Neg"));
                    raw.Axes[i] = isTrigger
                        ? MapToRawTriggerAxis(state, posDesc, negDesc, thisDeviceGuid, slotIndex)
                        : MapToThumbAxisWithNeg(state, posDesc, negDesc, thisDeviceGuid, slotIndex);
                }
            }

            // ── Buttons ──
            for (int i = 0; i < cfg.Buttons; i++)
            {
                string key = CachedName(ref _extBtnNames, i, "RawBtn");
                bool pressed;
                if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, vgt, out pressed))
                {
                    if (pressed) raw.SetButton(i, true);
                }
                else
                {
                    string desc = ps.GetRawMapping(key);
                    if (MapToButtonPressed(state, desc, thisDeviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), vgt, ps.GetMappingBidirectional(key) == "1"))
                        raw.SetButton(i, true);
                }
            }

            // ── POVs ──
            for (int p = 0; p < cfg.Povs && p < raw.Povs.Length; p++)
            {
                string upKey = CachedName(ref _extPovUpNames, p, "RawPov", "Up"),
                       downKey = CachedName(ref _extPovDownNames, p, "RawPov", "Down");
                string leftKey = CachedName(ref _extPovLeftNames, p, "RawPov", "Left"),
                       rightKey = CachedName(ref _extPovRightNames, p, "RawPov", "Right");
                bool up = EvalRawDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, upKey, vgt);
                bool down = EvalRawDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, downKey, vgt);
                bool left = EvalRawDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, leftKey, vgt);
                bool right = EvalRawDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, rightKey, vgt);

                raw.Povs[p] = DirectionToContinuousPov(up, down, left, right);
            }

            // Pre-tuning frame for calibration capture / preview cold dot:
            // everything below mutates raw.Axes in place.
            raw.HardwareAxes = (short[])raw.Axes.Clone();

            // ── Deadzones ──
            // Apply stick/trigger deadzones using the same axis layout as
            // ExtendedSlotConfig.ComputeAxisLayout (interleaved groups of X,Y,T).
            int interleave = Math.Min(cfg.Sticks, cfg.Triggers);
            for (int g = 0; g < cfg.Sticks; g++)
            {
                int xi = g < interleave ? g * 3 : interleave * 3 + (g - interleave) * 2;
                int yi = xi + 1;
                if (xi >= raw.Axes.Length || yi >= raw.Axes.Length) break;

                double dzX, dzY, adzX, adzY, lin, cofX = 0, cofY = 0, mrX = 100, mrY = 100, mrXN = 100, mrYN = 100;
                double[] lutX = null, lutY = null;
                string boundaryMap = null; // #174: sticks 0/1 only; custom sticks deferred.
                DeadZoneShape dzShape;
                switch (g)
                {
                    case 0:
                        dzShape = ParseDeadZoneShape(ps.LeftThumbDeadZoneShape);
                        boundaryMap = ps.LeftThumbBoundaryMap;
                        dzX = TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0);
                        dzY = TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0);
                        adzX = TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneX, 0);
                        adzY = TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneY, 0);
                        lin = TryParseDoubleStatic(ps.LeftThumbLinear, 0);
                        lutX = Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveX);
                        lutY = Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveY);
                        cofX = TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0);
                        cofY = TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0);
                        mrX = TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100);
                        mrY = TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100);
                        mrXN = TryParseDoubleStatic(ps.LeftThumbMaxRangeXNeg, mrX);
                        mrYN = TryParseDoubleStatic(ps.LeftThumbMaxRangeYNeg, mrY);
                        break;
                    case 1:
                        dzShape = ParseDeadZoneShape(ps.RightThumbDeadZoneShape);
                        boundaryMap = ps.RightThumbBoundaryMap;
                        dzX = TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0);
                        dzY = TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0);
                        adzX = TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0);
                        adzY = TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0);
                        lin = TryParseDoubleStatic(ps.RightThumbLinear, 0);
                        lutX = Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveX);
                        lutY = Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveY);
                        cofX = TryParseDoubleStatic(ps.RightThumbCenterOffsetX, 0);
                        cofY = TryParseDoubleStatic(ps.RightThumbCenterOffsetY, 0);
                        mrX = TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100);
                        mrY = TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100);
                        mrXN = TryParseDoubleStatic(ps.RightThumbMaxRangeXNeg, mrX);
                        mrYN = TryParseDoubleStatic(ps.RightThumbMaxRangeYNeg, mrY);
                        break;
                    default:
                        // Custom Extended sticks 2+: read all settings from
                        // Extended dictionary (key order documented at
                        // ExtStickKeys).
                        var sk = ExtStickKeys(g);
                        dzShape = ParseDeadZoneShape(ps.GetRawMapping(sk[0]));
                        dzX = TryParseDoubleStatic(ps.GetRawMapping(sk[1]), 0);
                        dzY = TryParseDoubleStatic(ps.GetRawMapping(sk[2]), 0);
                        adzX = TryParseDoubleStatic(ps.GetRawMapping(sk[3]), 0);
                        adzY = TryParseDoubleStatic(ps.GetRawMapping(sk[4]), 0);
                        lin = TryParseDoubleStatic(ps.GetRawMapping(sk[5]), 0);
                        lutX = Common.CurveLut.GetOrBuild(ps.GetRawMapping(sk[6]));
                        lutY = Common.CurveLut.GetOrBuild(ps.GetRawMapping(sk[7]));
                        cofX = TryParseDoubleStatic(ps.GetRawMapping(sk[8]), 0);
                        cofY = TryParseDoubleStatic(ps.GetRawMapping(sk[9]), 0);
                        mrX = TryParseDoubleStatic(ps.GetRawMapping(sk[10]), 100);
                        mrY = TryParseDoubleStatic(ps.GetRawMapping(sk[11]), 100);
                        mrXN = TryParseDoubleStatic(ps.GetRawMapping(sk[12]), mrX);
                        mrYN = TryParseDoubleStatic(ps.GetRawMapping(sk[13]), mrY);
                        break;
                }
                raw.Axes[xi] = ApplyCenterOffset(raw.Axes[xi], cofX);
                raw.Axes[yi] = ApplyCenterOffset(raw.Axes[yi], cofY);
                // #174: circular reshape before the dead zone, same order and
                // no-op-when-null semantics as the main gamepad path above.
                Common.StickBoundary.Reshape(ref raw.Axes[xi], ref raw.Axes[yi],
                    Common.StickBoundary.GetOrBuild(boundaryMap));
                ApplyDeadZone(ref raw.Axes[xi], ref raw.Axes[yi],
                    dzX, dzY, adzX, adzY, lin, mrX, mrY, mrXN, mrYN, lutX, lutY, dzShape);
            }

            for (int g = 0; g < cfg.Triggers; g++)
            {
                int ti = g < interleave ? g * 3 + 2
                       : interleave * 3 + Math.Max(0, cfg.Sticks - interleave) * 2 + (g - interleave);
                if (ti >= raw.Axes.Length) break;

                double dz, adz, maxR;
                double[] tlut;
                switch (g)
                {
                    case 0:
                        dz = TryParseDoubleStatic(ps.LeftTriggerDeadZone, 0);
                        adz = TryParseDoubleStatic(ps.LeftTriggerAntiDeadZone, 0);
                        maxR = TryParseDoubleStatic(ps.LeftTriggerMaxRange, 100);
                        tlut = Common.CurveLut.GetOrBuild(ps.LeftTriggerSensitivityCurve);
                        break;
                    case 1:
                        dz = TryParseDoubleStatic(ps.RightTriggerDeadZone, 0);
                        adz = TryParseDoubleStatic(ps.RightTriggerAntiDeadZone, 0);
                        maxR = TryParseDoubleStatic(ps.RightTriggerMaxRange, 100);
                        tlut = Common.CurveLut.GetOrBuild(ps.RightTriggerSensitivityCurve);
                        break;
                    default:
                        // Custom Extended triggers 2+: read from Extended
                        // dictionary (key order documented at ExtTriggerKeys).
                        var tk = ExtTriggerKeys(g);
                        dz = TryParseDoubleStatic(ps.GetRawMapping(tk[0]), 0);
                        adz = TryParseDoubleStatic(ps.GetRawMapping(tk[1]), 0);
                        maxR = TryParseDoubleStatic(ps.GetRawMapping(tk[2]), 100);
                        tlut = Common.CurveLut.GetOrBuild(ps.GetRawMapping(tk[3]));
                        break;
                }
                // Triggers use signed short in raw path; convert to unsigned 16-bit range,
                // apply trigger deadzone, then convert back.
                ushort asUshort = (ushort)(raw.Axes[ti] - short.MinValue);
                asUshort = ApplyTriggerDeadZone(asUshort, dz, adz, maxR, tlut);
                // Back to signed short range
                raw.Axes[ti] = (short)(asUshort + short.MinValue);
            }

            return raw;
        }

        /// <summary>Evaluates one Extended POV-direction button, preferring
        /// the per-VC MappingSet row when present.</summary>
        private static bool EvalRawDirection(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex,
            string key, int globalThreshold)
        {
            if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                    slotIndex, key, globalThreshold, out bool pressed))
                return pressed;
            return MapToButtonPressed(state, ps.GetRawMapping(key),
                thisDeviceGuid, slotIndex,
                TryParseIntStatic(ps.GetMappingDeadZone(key), 0), globalThreshold,
                ps.GetMappingBidirectional(key) == "1");
        }

        /// <summary>
        /// Converts 4 direction booleans to a continuous POV value (0-35900, -1=centered).
        /// </summary>
        private static int DirectionToContinuousPov(bool up, bool down, bool left, bool right)
        {
            if (up && right) return 4500;
            if (right && down) return 13500;
            if (down && left) return 22500;
            if (left && up) return 31500;
            if (up) return 0;
            if (right) return 9000;
            if (down) return 18000;
            if (left) return 27000;
            return -1; // Centered
        }

        // ─────────────────────────────────────────────
        //  MIDI mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a CustomInputState to a MidiRawState using Midi dictionary-based
        /// mappings. CC values are mapped from signed axis range to 0-127 MIDI range.
        /// Notes are mapped as boolean on/off.
        /// </summary>
        private static MidiRawState MapInputToMidiRaw(CustomInputState state, PadSetting ps,
            int ccCount, int noteCount,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var raw = MidiRawState.Create(ccCount, noteCount);
            raw.Clear();
            int mgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // CCs — map each from input axis to 0-127. Prefer the per-VC
            // MappingSet (multi-source, combine-mode, Custom-formula aware)
            // when a row exists for the target; fall back to legacy
            // single-source PadSetting fields for un-resaved configs.
            for (int i = 0; i < ccCount; i++)
            {
                string ccKey = CachedName(ref _midiCcNames, i, "MidiCC");
                short axisValue;
                if (!TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, ccKey, out axisValue))
                {
                    string posDesc = ps.GetMidiMapping(ccKey);
                    string negDesc = ps.GetMidiMapping(CachedName(ref _midiCcNegNames, i, "MidiCC", "Neg"));
                    axisValue = MapToThumbAxisWithNeg(state, posDesc, negDesc, thisDeviceGuid, slotIndex);
                }
                // Convert signed short (-32768..32767) to MIDI range (0..127)
                raw.CcValues[i] = (byte)((axisValue + 32768) * 127 / 65535);
            }

            // Notes — map each as boolean. Same MappingSet-first dispatch.
            for (int i = 0; i < noteCount; i++)
            {
                string key = CachedName(ref _midiNoteNames, i, "MidiNote");
                bool pressed;
                if (!TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, mgt, out pressed))
                {
                    string desc = ps.GetMidiMapping(key);
                    pressed = MapToButtonPressed(state, desc, thisDeviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), mgt, ps.GetMappingBidirectional(key) == "1");
                }
                raw.Notes[i] = pressed;
            }

            return raw;
        }

        // ─────────────────────────────────────────────
        //  KBM raw state mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Virtual key codes used for KBM mapping targets.
        /// Order matches InitializeKeyboardMouseMappings() in PadViewModel.
        /// </summary>
        private static readonly byte[] KbmKeyVkCodes;
        private static readonly int KbmKeyCount;

        // ── Interpolated-key name tables (audit 1n) ──
        // The mapping dictionaries are keyed by a closed, index-driven
        // vocabulary ("KbmKey41", "RawAxis3", "MidiCC7", ...).
        // Interpolating those keys per poll allocated ~100k strings/s of
        // gen0 on the poll thread; these tables cache each name on first
        // use. Poll-thread only, like the mappers that read them.
        private static readonly string[] KbmKeyNames;
        private static readonly string[] KbmMBtnNames =
            { "KbmMBtn0", "KbmMBtn1", "KbmMBtn2", "KbmMBtn3", "KbmMBtn4" };
        private static string[] _extAxisNames = System.Array.Empty<string>();
        private static string[] _extAxisNegNames = System.Array.Empty<string>();
        private static string[] _extBtnNames = System.Array.Empty<string>();
        private static string[] _extPovUpNames = System.Array.Empty<string>();
        private static string[] _extPovDownNames = System.Array.Empty<string>();
        private static string[] _extPovLeftNames = System.Array.Empty<string>();
        private static string[] _extPovRightNames = System.Array.Empty<string>();
        private static string[][] _extStickKeys = System.Array.Empty<string[]>();
        private static string[][] _extTriggerKeys = System.Array.Empty<string[]>();
        private static string[] _midiCcNames = System.Array.Empty<string>();
        private static string[] _midiCcNegNames = System.Array.Empty<string>();
        private static string[] _midiNoteNames = System.Array.Empty<string>();

        private static string CachedName(ref string[] table, int index, string prefix, string suffix = "")
        {
            var t = table;
            if ((uint)index < (uint)t.Length) return t[index];
            var grown = new string[Math.Max(index + 1, Math.Max(8, t.Length * 2))];
            System.Array.Copy(t, grown, t.Length);
            for (int i = t.Length; i < grown.Length; i++)
                grown[i] = prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
            table = grown;
            return grown[index];
        }

        /// <summary>Per-stick Extended tuning keys, order: DzShape, DzX, DzY,
        /// AdzX, AdzY, Linear, CurveX, CurveY, CofX, CofY, MrX, MrY, MrXN,
        /// MrYN (indices 0-13).</summary>
        private static string[] ExtStickKeys(int g)
        {
            var t = _extStickKeys;
            if ((uint)g < (uint)t.Length) return t[g];
            var grown = new string[g + 1][];
            System.Array.Copy(t, grown, t.Length);
            for (int i = t.Length; i < grown.Length; i++)
            {
                string p = "RawStick" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                grown[i] = new[]
                {
                    p + "DzShape", p + "DzX", p + "DzY", p + "AdzX", p + "AdzY",
                    p + "Linear", p + "CurveX", p + "CurveY", p + "CofX", p + "CofY",
                    p + "MrX", p + "MrY", p + "MrXN", p + "MrYN",
                };
            }
            _extStickKeys = grown;
            return grown[g];
        }

        /// <summary>Per-trigger Extended tuning keys, order: Dz, Adz, Mr,
        /// Curve (indices 0-3).</summary>
        private static string[] ExtTriggerKeys(int g)
        {
            var t = _extTriggerKeys;
            if ((uint)g < (uint)t.Length) return t[g];
            var grown = new string[g + 1][];
            System.Array.Copy(t, grown, t.Length);
            for (int i = t.Length; i < grown.Length; i++)
            {
                string p = "ExtendedTrigger" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                grown[i] = new[] { p + "Dz", p + "Adz", p + "Mr", p + "Curve" };
            }
            _extTriggerKeys = grown;
            return grown[g];
        }

        static InputManager()
        {
            // Build the full list of VK codes that KBM supports
            var vks = new System.Collections.Generic.List<byte>(128);

            // Letters A-Z (0x41-0x5A)
            for (int i = 0; i < 26; i++) vks.Add((byte)(0x41 + i));
            // Numbers 0-9 (0x30-0x39)
            for (int i = 0; i <= 9; i++) vks.Add((byte)(0x30 + i));
            // Function keys F1-F12 (0x70-0x7B)
            for (int i = 0; i < 12; i++) vks.Add((byte)(0x70 + i));
            // Modifiers
            vks.Add(0xA0); vks.Add(0xA1); // L/R Shift
            vks.Add(0xA2); vks.Add(0xA3); // L/R Ctrl
            vks.Add(0xA4); vks.Add(0xA5); // L/R Alt
            // Special keys
            vks.Add(0x20); vks.Add(0x0D); vks.Add(0x1B); vks.Add(0x09); vks.Add(0x08); vks.Add(0x14);
            // Navigation
            vks.Add(0x26); vks.Add(0x28); vks.Add(0x25); vks.Add(0x27); // arrows
            vks.Add(0x24); vks.Add(0x23); vks.Add(0x21); vks.Add(0x22); // home/end/pgup/pgdn
            vks.Add(0x2D); vks.Add(0x2E); // insert/delete
            // Punctuation
            vks.Add(0xBA); vks.Add(0xBB); vks.Add(0xBC); vks.Add(0xBD);
            vks.Add(0xBE); vks.Add(0xBF); vks.Add(0xC0); vks.Add(0xDB);
            vks.Add(0xDC); vks.Add(0xDD); vks.Add(0xDE);
            // Numpad 0-9
            for (int i = 0; i <= 9; i++) vks.Add((byte)(0x60 + i));
            // Numpad operators
            vks.Add(0x6A); vks.Add(0x6B); vks.Add(0x6D); vks.Add(0x6E); vks.Add(0x6F);

            KbmKeyVkCodes = vks.ToArray();
            KbmKeyCount = KbmKeyVkCodes.Length;

            KbmKeyNames = new string[KbmKeyCount];
            for (int i = 0; i < KbmKeyCount; i++)
                KbmKeyNames[i] = $"KbmKey{KbmKeyVkCodes[i]:X2}";
        }

        /// <summary>
        /// Maps a CustomInputState to a KbmRawState using KBM dictionary-based mappings.
        /// Keys are mapped as button presses, mouse axes as signed deltas.
        /// </summary>
        private static KbmRawState MapInputToKbmRaw(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var raw = new KbmRawState();
            int kgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // Map keyboard keys — MappingSet-first, fall back to legacy
            // single-source for un-resaved configs.
            for (int i = 0; i < KbmKeyCount; i++)
            {
                byte vk = KbmKeyVkCodes[i];
                string key = KbmKeyNames[i];
                bool pressed;
                if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, kgt, out pressed))
                {
                    if (pressed) raw.SetKey(vk, true);
                }
                else
                {
                    string desc = ps.GetKbmMapping(key);
                    if (!string.IsNullOrEmpty(desc) && MapToButtonPressed(state, desc, thisDeviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), kgt, ps.GetMappingBidirectional(key) == "1"))
                        raw.SetKey(vk, true);
                }
            }

            // Map mouse buttons (0=LMB, 1=RMB, 2=MMB, 3=X1, 4=X2)
            for (int i = 0; i < 5; i++)
            {
                string key = KbmMBtnNames[i];
                bool pressed;
                if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, kgt, out pressed))
                {
                    if (pressed) raw.SetMouseButton(i, true);
                }
                else
                {
                    string desc = ps.GetKbmMapping(key);
                    if (!string.IsNullOrEmpty(desc) && MapToButtonPressed(state, desc, thisDeviceGuid, slotIndex, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), kgt, ps.GetMappingBidirectional(key) == "1"))
                        raw.SetMouseButton(i, true);
                }
            }

            bool irPointerDrivesMouse = false;
            bool irDroveMouseX = false, irDroveMouseY = false;

            // Flick stick (#225): exact-counts mouse X lane, additive and
            // independent of the velocity/absolute chain below. Evaluated
            // here (not in the bipolar combine) because its output is
            // calibrated counts, not a [-1..+1] deflection; the same
            // sources read as 0 through the coercion path, so a mixed
            // gyro+flick row still sums its other sources normally.
            raw.MouseFlickX = TickFlickStickSources(state, mappingSet, thisDeviceGuid, slotIndex);

            // Map mouse X axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmMouseX");
                string negDesc = ps.GetKbmMapping("KbmMouseXNeg");
                var irSrcX = FindIrPointerSource(mappingSet, "KbmMouseX", posDesc, thisDeviceGuid);
                if (irSrcX != null)
                {
                    // Wii IR pointing is ABSOLUTE aim (Touchmote-style): the
                    // cursor goes where the remote points, not where a velocity
                    // integral drifts. Route the evaluated [-1..+1] aim to the
                    // absolute-pointer channel; the KBM VC positions the OS
                    // cursor (Touchmote MouseSimulator.cs:154 SetCursorPos).
                    raw.MouseAbsX = PadForge.Engine.Common.Mapping.SourceCoercion
                        .EvaluateForBipolarAxisTarget(state, irSrcX, slotIndex,
                            evaluatedDeviceGuid: thisDeviceGuid);
                    if (state.Ir.Detected) { raw.MouseAbsValid = true; raw.MouseAbsXValid = true; }
                    irPointerDrivesMouse = true;
                    irDroveMouseX = true;
                }
                else if (FindEngagedTouchpadPointerSource(state, mappingSet, "KbmMouseX",
                        slotIndex, thisDeviceGuid) is { } tpSrcX)
                {
                    // Absolute touchpad pointer (#9 B-15): the finger's pad
                    // position IS the cursor position, the same absolute
                    // channel the IR pointer drives above. The finder
                    // already gated on engagement (finger in contact inside
                    // the source's half window), so the value is live by
                    // construction; a lifted finger falls through to the
                    // delta lane below, produces no delta, and the cursor
                    // freezes at its last position (the Wii sight-loss
                    // convention, and Steam's mouse_region behavior with
                    // teleport_stop off). Never sets irPointerDrivesMouse:
                    // the Wii pointer modes (FPS Mouse / borders) stay an
                    // IR-only feature.
                    raw.MouseAbsX = PadForge.Engine.Common.Mapping.SourceCoercion
                        .EvaluateForBipolarAxisTarget(state, tpSrcX, slotIndex,
                            evaluatedDeviceGuid: thisDeviceGuid);
                    raw.MouseAbsValid = true; raw.MouseAbsXValid = true;
                }
                else if (TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, "KbmMouseX", out short msxValue))
                {
                    raw.MouseDeltaX = msxValue;
                }
                else if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                    raw.MouseDeltaX = MapToThumbAxisWithNeg(state, posDesc, negDesc, thisDeviceGuid, slotIndex);
            }

            // Map mouse Y axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmMouseY");
                string negDesc = ps.GetKbmMapping("KbmMouseYNeg");
                var irSrcY = FindIrPointerSource(mappingSet, "KbmMouseY", posDesc, thisDeviceGuid);
                if (irSrcY != null)
                {
                    // Absolute aim, same as the X block. state.Ir.Y is already
                    // screen-aligned (+1 = bottom, Touchmote convention applied
                    // at the wrapper), so no velocity-convention negation here.
                    raw.MouseAbsY = PadForge.Engine.Common.Mapping.SourceCoercion
                        .EvaluateForBipolarAxisTarget(state, irSrcY, slotIndex,
                            evaluatedDeviceGuid: thisDeviceGuid);
                    if (state.Ir.Detected) { raw.MouseAbsValid = true; raw.MouseAbsYValid = true; }
                    irPointerDrivesMouse = true;
                    irDroveMouseY = true;
                }
                else if (FindEngagedTouchpadPointerSource(state, mappingSet, "KbmMouseY",
                        slotIndex, thisDeviceGuid) is { } tpSrcY)
                {
                    // Absolute pointer Y (#9 B-15), same as the X block. SDL
                    // touchpad Y is already screen-aligned (0 = top edge, so
                    // the tuned bipolar's +1 = bottom), matching the
                    // MouseAbsY convention the VC consumes; no
                    // velocity-convention negation here.
                    raw.MouseAbsY = PadForge.Engine.Common.Mapping.SourceCoercion
                        .EvaluateForBipolarAxisTarget(state, tpSrcY, slotIndex,
                            evaluatedDeviceGuid: thisDeviceGuid);
                    raw.MouseAbsValid = true; raw.MouseAbsYValid = true;
                }
                else if (TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, "KbmMouseY", out short msyValue))
                {
                    // KbmMouseY convention is positive = UP (the VC negates it to
                    // screen-Y, which is positive = down). The MappingSet evaluator
                    // returns SDL convention (positive = down), so negate to default
                    // physical-up → cursor-up — matching the legacy single-descriptor
                    // path below and the gamepad ThumbLY path. A per-source Invert
                    // still flips direction from there.
                    raw.MouseDeltaY = NegateAxis(msyValue);
                }
                else if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                {
                    raw.MouseDeltaY = MapToThumbAxisWithNeg(state, posDesc, negDesc, thisDeviceGuid, slotIndex);
                    // For a full analog axis (no neg descriptor), SDL Y convention has
                    // positive=down. KBM convention: KbmMouseY positive=UP (matching
                    // gamepad path's NegateAxis on ThumbLY). Negate so the VC's
                    // screen-Y negation produces correct cursor direction.
                    if (string.IsNullOrWhiteSpace(negDesc))
                        raw.MouseDeltaY = NegateAxis(raw.MouseDeltaY);
                }
            }

            // Snapshot pre-deadzone values for stick tab preview.
            raw.PreDzMouseDeltaX = raw.MouseDeltaX;
            raw.PreDzMouseDeltaY = raw.MouseDeltaY;

            // ── Center offsets (applied before deadzone, same as gamepad path) ──
            raw.MouseDeltaX = ApplyCenterOffset(raw.MouseDeltaX, TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0));
            raw.MouseDeltaY = ApplyCenterOffset(raw.MouseDeltaY, TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0));

            // ── Mouse movement deadzone + sensitivity (uses Left Thumb settings) ──
            ApplyDeadZone(ref raw.MouseDeltaX, ref raw.MouseDeltaY,
                TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbLinear, 0),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeXNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100)),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeYNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100)),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveX),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveY),
                ParseDeadZoneShape(ps.LeftThumbDeadZoneShape));

            // ── Wii pointer modes (issue #203) ──
            // Placed AFTER the relative lane's deadzone so FPS Mouse's
            // synthesized velocity is not re-deadzoned by the Left Thumb
            // settings (its own circular deadzone already ran). Only runs
            // when the IR pointer actually feeds the KBM mouse target;
            // "IR Pointer X/Y" mapped to sticks stays raw by design.
            ApplyPointerMode(ref raw, thisDeviceGuid, slotIndex,
                irPointerDrivesMouse, irDroveMouseX, irDroveMouseY);

            // Map scroll axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmScroll");
                string negDesc = ps.GetKbmMapping("KbmScrollNeg");
                if (TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, "KbmScroll", out short scrollValue))
                {
                    // KbmScroll convention is positive = UP; the MappingSet evaluator
                    // returns SDL positive = down, so negate to default physical-up →
                    // scroll-up (same fix as MouseDeltaY). Per-source Invert overrides.
                    raw.ScrollDelta = NegateAxis(scrollValue);
                }
                else if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                {
                    raw.ScrollDelta = MapToThumbAxisWithNeg(state, posDesc, negDesc, thisDeviceGuid, slotIndex);
                    // Full analog axis: SDL Y positive=down, but KbmScroll positive=UP.
                    // Negate so physical up → scroll up (same fix as MouseDeltaY).
                    if (string.IsNullOrWhiteSpace(negDesc))
                        raw.ScrollDelta = NegateAxis(raw.ScrollDelta);
                }
            }

            // Snapshot pre-deadzone scroll for stick preview.
            raw.PreDzScrollDelta = raw.ScrollDelta;

            // ── Scroll deadzone + sensitivity (uses Right Thumb settings, scroll on Y axis) ──
            // Scroll is a signed bidirectional axis. Use stick deadzone with X=0.
            {
                short scrollX = 0;
                ApplyDeadZone(ref scrollX, ref raw.ScrollDelta,
                    TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0),
                    TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0),
                    TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0),
                    TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0),
                    TryParseDoubleStatic(ps.RightThumbLinear, 0),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeXNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100)),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeYNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100)),
                    Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveX),
                    Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveY),
                    ParseDeadZoneShape(ps.RightThumbDeadZoneShape));
            }

            // ── Horizontal scroll (issue #154, office-mouse tilt wheel) ──
            // Same shape as the vertical block above. Positive = scroll RIGHT,
            // which is both the SDL X-axis positive direction and
            // MOUSEEVENTF_HWHEEL's positive, so no sign correction is needed on
            // either path (the vertical negation exists only because scroll-up
            // opposes SDL's Y-positive-down).
            {
                string posDesc = ps.GetKbmMapping("KbmScrollH");
                string negDesc = ps.GetKbmMapping("KbmScrollHNeg");
                if (TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, "KbmScrollH", out short scrollHValue))
                {
                    raw.ScrollDeltaH = scrollHValue;
                }
                else if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                {
                    raw.ScrollDeltaH = MapToThumbAxisWithNeg(state, posDesc, negDesc, thisDeviceGuid, slotIndex);
                }
            }

            raw.PreDzScrollDeltaH = raw.ScrollDeltaH;

            // Horizontal scroll deadzone: same Right Thumb settings, H on the X axis.
            {
                short scrollHy = 0;
                ApplyDeadZone(ref raw.ScrollDeltaH, ref scrollHy,
                    TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0),
                    TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0),
                    TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0),
                    TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0),
                    TryParseDoubleStatic(ps.RightThumbLinear, 0),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeXNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100)),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeYNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100)),
                    Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveX),
                    Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveY),
                    ParseDeadZoneShape(ps.RightThumbDeadZoneShape));
            }

            return raw;
        }

        /// <summary>
        /// Maps touchpad input from CustomInputState to a TouchpadState.
        /// Multi-source / combine modes / Custom formulas apply to every
        /// touchpad target (X/Y, contact, click) — same as every other VC
        /// type. The primary source's descriptor pattern picks the OUTPUT
        /// mode for the X/Y position:
        ///
        ///   PASSTHROUGH — Sources[0] starts with "Touchpad" (a finger-X
        ///     or finger-Y descriptor). The combined per-frame value is
        ///     written as ABSOLUTE position; extra sources contribute via
        ///     the row's combine mode (e.g. Average two physical
        ///     touchpads, MaxAbs whichever finger is furthest from
        ///     center). Passthrough sources are routed through
        ///     SourceCoercion's touchpad-axis reader (added with this
        ///     pass) so they coexist with stick / button sources in the
        ///     same row.
        ///
        ///   STICK-TO-CURSOR — Sources[0] is anything else (axis, button,
        ///     POV). The combined value is integrated per frame as
        ///     cursor velocity, exactly like the legacy single-source
        ///     stick-to-cursor path.
        ///
        /// Mode detection looks at the FIRST source's descriptor (or the
        /// legacy ps.TouchpadX1/X2 string when no MappingSet row exists)
        /// so a user's choice of "physical touchpad" vs "stick-to-cursor"
        /// stays expressed in the source they pick first.
        /// </summary>
        private static TouchpadState MapInputToTouchpad(CustomInputState state, PadSetting ps, TouchpadState prev,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var tp = new TouchpadState { PacketCounter = prev.PacketCounter };
            int gt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            EvalTouchpadFinger(state, ps, mappingSet, thisDeviceGuid, slotIndex, gt,
                xKey: "TouchpadX1", yKey: "TouchpadY1", contactKey: "TouchpadContact1",
                legacyXDesc: ps.TouchpadX1, legacyYDesc: ps.TouchpadY1,
                legacyContactDesc: ps.TouchpadContact1,
                prevX: prev.X0, prevY: prev.Y0,
                physicalFingerIdx: 0,
                out tp.X0, out tp.Y0, out tp.Down0);

            EvalTouchpadFinger(state, ps, mappingSet, thisDeviceGuid, slotIndex, gt,
                xKey: "TouchpadX2", yKey: "TouchpadY2", contactKey: "TouchpadContact2",
                legacyXDesc: ps.TouchpadX2, legacyYDesc: ps.TouchpadY2,
                legacyContactDesc: ps.TouchpadContact2,
                prevX: prev.X1, prevY: prev.Y1,
                physicalFingerIdx: 1,
                out tp.X1, out tp.Y1, out tp.Down1);

            // ── Touchpad click ──
            // Empty descriptor = no click output (matches finger
            // semantics above). CreateDefaultPadSetting fills this in
            // for Sony source devices so the default DualSense →
            // PlayStation flow still surfaces the physical click.
            tp.Click = EvalTouchpadButton(state, ps, mappingSet, thisDeviceGuid, slotIndex,
                "TouchpadClick", ps.TouchpadClick, gt, out _);

            // Increment packet counter on finger state transitions.
            if (tp.Down0 != prev.Down0 || tp.Down1 != prev.Down1)
                tp.PacketCounter++;

            return tp;
        }

        /// <summary>Evaluates a touchpad button-class target (Click /
        /// ContactN). MappingSet-first; falls back to the legacy
        /// per-device descriptor on PadSetting when no row is present.
        /// <paramref name="found"/> tells the caller whether ANY mapping
        /// exists (so the velocity-implicit-contact fallback in the
        /// stick-to-touchpad path knows whether to kick in).</summary>
        private static bool EvalTouchpadButton(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex,
            string targetName, string legacyDescriptor, int globalThreshold,
            out bool found)
        {
            if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                    slotIndex, targetName, globalThreshold, out bool pressed))
            {
                found = true;
                return pressed;
            }
            if (!string.IsNullOrEmpty(legacyDescriptor))
            {
                found = true;
                return MapToButtonPressed(state, legacyDescriptor, thisDeviceGuid, slotIndex);
            }
            found = false;
            return false;
        }

        /// <summary>Evaluates one virtual touchpad finger (X/Y/contact).
        /// Mode is determined by the primary source's descriptor pattern —
        /// a touchpad-passthrough descriptor produces absolute position,
        /// anything else produces stick-to-cursor velocity. Both modes are
        /// fully multi-source-capable through the MappingSet path; the
        /// legacy per-device PadSetting descriptors are kept as a fallback
        /// for configs that haven't been resaved since the per-VC
        /// MappingSet shipped.</summary>
        private static void EvalTouchpadFinger(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex, int globalThreshold,
            string xKey, string yKey, string contactKey,
            string legacyXDesc, string legacyYDesc, string legacyContactDesc,
            float prevX, float prevY,
            int physicalFingerIdx,
            out float outX, out float outY, out bool outDown)
        {
            outX = prevX;
            outY = prevY;
            outDown = false;

            // Resolve the primary source's descriptor — MappingSet first
            // (Sources[0]) for the X target, legacy ps.TouchpadX? as the
            // fallback. The mode (passthrough vs velocity) keys off this
            // ONE descriptor: extra sources on the row contribute via the
            // row's combine mode, but they don't switch the output mode
            // out from under the primary.
            string primaryXDesc = ResolvePrimaryDescriptor(mappingSet, xKey, legacyXDesc);
            bool isPassthroughMode = IsTouchpadDescriptor(primaryXDesc);

            // Does the row exist in the MappingSet? Drives whether we use
            // the multi-source/combine evaluator or the legacy single-
            // descriptor reader. In passthrough mode the X/Y eval is
            // GATED — touchpad-class sources contribute only while their
            // paired TouchpadDown is true. The gated evaluator returns
            // false (no active source) so we can hold the previous
            // position — see TryEvaluateMappingSetTouchpadAxis.
            bool xViaMappingSet, yViaMappingSet;
            short xCombined, yCombined;
            if (isPassthroughMode)
            {
                xViaMappingSet = TryEvaluateMappingSetTouchpadAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, xKey, physicalFingerIdx, out xCombined);
                yViaMappingSet = TryEvaluateMappingSetTouchpadAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, yKey, physicalFingerIdx, out yCombined);
            }
            else
            {
                // Velocity / stick-to-cursor: ungated. Sticks at rest
                // already read 0 so the "stale finger position" pollution
                // can't happen here.
                xViaMappingSet = TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, xKey, out xCombined);
                yViaMappingSet = TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, yKey, out yCombined);
            }
            bool anyXMapping = xViaMappingSet || !string.IsNullOrEmpty(legacyXDesc);

            if (!anyXMapping)
                return;  // No mapping at all → no output for this finger.

            if (isPassthroughMode)
            {
                if (xViaMappingSet)
                {
                    // Combined bipolar [-1..+1] → absolute position [0..1].
                    float xBipolar = xCombined / 32767f;
                    outX = Math.Clamp((xBipolar + 1f) * 0.5f, 0f, 1f);
                }
                else
                {
                    // Gated evaluator returned false (no active source) AND
                    // legacy fallback path didn't yield a Touchpad descriptor
                    // either — hold the previous position. This is the
                    // sticky-touchpad semantic: when no finger is touching,
                    // the cursor stays where the last finger left it.
                    outX = prevX;
                }

                if (yViaMappingSet)
                {
                    float yBipolar = yCombined / 32767f;
                    outY = Math.Clamp((yBipolar + 1f) * 0.5f, 0f, 1f);
                }
                else
                {
                    outY = prevY;
                }

                // If the MappingSet had no row at all for X (xViaMappingSet
                // was false AND no row found), but a legacy descriptor was
                // present, fall through to the legacy passthrough reader.
                // The gated evaluator never returns false for "row exists
                // with active sources" so this branch is for legacy
                // pre-MappingSet configs only.
                if (!xViaMappingSet && !string.IsNullOrEmpty(legacyXDesc)
                    && IsTouchpadDescriptor(legacyXDesc))
                {
                    float xBipolar = MapPassthroughLegacyAxisToBipolar(state, legacyXDesc, physicalFingerIdx, isY: false,
                        thisDeviceGuid, slotIndex);
                    outX = Math.Clamp((xBipolar + 1f) * 0.5f, 0f, 1f);
                }
                if (!yViaMappingSet && !string.IsNullOrEmpty(legacyYDesc)
                    && IsTouchpadDescriptor(legacyYDesc))
                {
                    float yBipolar = MapPassthroughLegacyAxisToBipolar(state, legacyYDesc, physicalFingerIdx, isY: true,
                        thisDeviceGuid, slotIndex);
                    outY = Math.Clamp((yBipolar + 1f) * 0.5f, 0f, 1f);
                }

                // Contact: MappingSet-first; falls back to legacy descriptor;
                // falls back to the physical finger's Down bit so the
                // out-of-the-box DualSense→PlayStation passthrough lights up
                // without the user authoring a contact mapping explicitly.
                bool contactPressed = EvalTouchpadButton(state, ps, mappingSet, thisDeviceGuid, slotIndex,
                    contactKey, legacyContactDesc, globalThreshold, out bool contactFound);
                outDown = contactFound
                    ? contactPressed
                    : (state.Touchpads != null && state.Touchpads.Length > 0
                       && state.Touchpads[0] != null
                       && physicalFingerIdx >= 0
                       && physicalFingerIdx < state.Touchpads[0].MaxFingers
                       && state.Touchpads[0].FingerDown[physicalFingerIdx]);
            }
            else
            {
                // Stick-to-cursor: combined bipolar value is integrated as
                // per-frame cursor velocity. Same sensitivity as the legacy
                // path so existing configs feel identical after the upgrade.
                float stickX = xViaMappingSet
                    ? xCombined / 32768f
                    : MapToThumbAxisWithNeg(state, legacyXDesc, null, thisDeviceGuid, slotIndex) / 32768f;
                float stickY = yViaMappingSet
                    ? yCombined / 32768f
                    : MapToThumbAxisWithNeg(state, legacyYDesc, null, thisDeviceGuid, slotIndex) / 32768f;
                const float sensitivity = 0.015f;
                outX = Math.Clamp(prevX + stickX * sensitivity, 0f, 1f);
                outY = Math.Clamp(prevY + stickY * sensitivity, 0f, 1f);

                // Contact: explicit mapping wins; else implicit-on-deflection
                // (matches legacy stick-to-cursor behavior).
                bool contactPressed = EvalTouchpadButton(state, ps, mappingSet, thisDeviceGuid, slotIndex,
                    contactKey, legacyContactDesc, globalThreshold, out bool contactFound);
                outDown = contactFound
                    ? contactPressed
                    : (Math.Abs(stickX) > 0.1f || Math.Abs(stickY) > 0.1f);
            }
        }

        /// <summary>Returns the primary source's descriptor for a target.
        /// Reads the Base-layer MappingSet row's <c>Sources[0].Descriptor</c>
        /// when present; otherwise returns the legacy
        /// PadSetting-stored descriptor.</summary>
        private static string ResolvePrimaryDescriptor(MappingSet mappingSet, string targetName, string legacyDescriptor)
        {
            // Race-safe snapshot. The save path mutates Rows on the UI
            // thread; a raw indexed iteration here on the polling thread
            // could read past Count and surface as the "Error mapping
            // device" error in Step 3's outer try/catch.
            var rows = SnapshotRows(mappingSet, out int rowsCount);
            for (int i = 0; i < rowsCount; i++)
            {
                var r = rows[i];
                if (r == null) continue;
                if (!string.Equals(r.LayerMask ?? "Base", "Base", StringComparison.Ordinal)) continue;
                if (!string.Equals(r.Target, targetName, StringComparison.Ordinal)) continue;
                var srcs = SnapshotSources(r, out int srcsCount);
                if (srcsCount == 0) break;
                return srcs[0]?.Descriptor ?? legacyDescriptor;
            }
            return legacyDescriptor;
        }

        /// <summary>Legacy-fallback reader for passthrough mode when no
        /// MappingSet row exists yet. Reads state.TouchpadFingers for a
        /// "Touchpad N Finger M X|Y" descriptor and returns it as bipolar
        /// [-1..+1]; non-touchpad descriptors fall back to
        /// <see cref="MapToThumbAxisWithNeg"/>-style stick reading scaled
        /// to [-1..+1]. <paramref name="physicalFingerIdx"/> is the index
        /// used when the descriptor's finger / axis aren't explicit (e.g.
        /// a bare "Touchpad" prefix from older configs).</summary>
        private static float MapPassthroughLegacyAxisToBipolar(CustomInputState state,
            string descriptor, int physicalFingerIdx, bool isY,
            string thisDeviceGuid, int slotIndex)
        {
            if (string.IsNullOrEmpty(descriptor)) return 0f;
            if (descriptor.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Try a "Touchpad N Finger M X|Y" parse; default to the
                // expected slot (physicalFingerIdx, X or Y).
                var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int padIdx = 0;
                int fingerIdx = physicalFingerIdx;
                int axisOffset = isY ? 1 : 0;
                if (parts.Length >= 2 && int.TryParse(parts[1], out int parsedPad))
                    padIdx = parsedPad;
                if (parts.Length == 5
                    && parts[2].Equals("Finger", StringComparison.Ordinal)
                    && int.TryParse(parts[3], out int parsedFinger))
                {
                    fingerIdx = parsedFinger;
                    axisOffset = parts[4] switch { "X" => 0, "Y" => 1, "Pressure" => 2, _ => axisOffset };
                }
                if (state.Touchpads == null || padIdx < 0 || padIdx >= state.Touchpads.Length) return 0f;
                var pad = state.Touchpads[padIdx];
                if (pad == null || fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return 0f;
                float raw = axisOffset switch
                {
                    0 => pad.FingerX[fingerIdx],
                    1 => pad.FingerY[fingerIdx],
                    2 => pad.FingerPressure[fingerIdx],
                    _ => 0f
                };
                return Math.Clamp((raw - 0.5f) * 2f, -1f, 1f);
            }
            // Non-touchpad descriptor in passthrough mode (shouldn't happen
            // — IsTouchpadDescriptor gates this branch — but fall through
            // safely just in case).
            return MapToThumbAxisWithNeg(state, descriptor, null, thisDeviceGuid, slotIndex) / 32768f;
        }

        /// <summary>Returns true if the descriptor is a touchpad-specific source (not a generic axis).</summary>
        private static bool IsTouchpadDescriptor(string descriptor) =>
            !string.IsNullOrEmpty(descriptor) &&
            descriptor.StartsWith("Touchpad", StringComparison.Ordinal);

        /// <summary>
        /// Resolves bool-yielding touchpad descriptors against a
        /// CustomInputState by delegating to
        /// <see cref="SourceCoercion.ReadTouchpadBool"/>, the single owner
        /// of the touchpad bool grammar ("Touchpad N Click" incl. the v18
        /// windowed click, "Touchpad N Finger M Down" with every window
        /// token, and the 7-token quadrant-in-half compose). This used to
        /// be a hand-kept mirror of that reader and diverged on every v18
        /// window token, so the same descriptor read true on a mapping-set
        /// row and false on the legacy per-key path (audit 2026-07-17 G1).
        /// X/Y descriptors still resolve false: a finger position has no
        /// bool reading, so a stick X can't quietly become a button.
        /// Internal for the PadForge.Tests twin-sync pins.
        /// </summary>
        internal static bool MapTouchpadButton(CustomInputState state, string descriptor)
            => SourceCoercion.ReadTouchpadBool(state, descriptor);
    }
}
