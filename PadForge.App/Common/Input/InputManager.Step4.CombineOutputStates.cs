using System;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 4: CombineOutputStates
        //  Merges the mapped Gamepad states from all devices assigned to
        //  each virtual controller slot (0–15) into a single combined state.
        //
        //  Combination rules:
        //    - Buttons: OR (any device pressing a button activates it)
        //    - Triggers: MAX (highest trigger value wins)
        //    - Thumbsticks: largest-magnitude wins per axis
        // ─────────────────────────────────────────────

        /// <summary>
        /// Step 4: For each of the 16 virtual controller slots, find all UserSettings
        /// mapped to that slot and combine their output gamepads into a single
        /// <see cref="CombinedOutputStates"/> entry.
        /// </summary>
        private void CombineOutputStates()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null)
                return;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                try
                {
                    // Empty pad — no VC means nothing downstream reads the
                    // combined output. Skip the FindByPadIndex lock+scan.
                    if (!SettingsManager.SlotCreated[padIndex]) continue;

                    // Use non-allocating overload with pre-allocated buffer.
                    int slotCount = settings.FindByPadIndex(padIndex, _padIndexBuffer);

                    bool isExtended = SlotControllerTypes[padIndex] == VirtualControllerType.Extended
                                     && SlotExtendedIsCustom[padIndex];
                    bool isMidi = SlotControllerTypes[padIndex] == VirtualControllerType.Midi;
                    bool isKbm = SlotControllerTypes[padIndex] == VirtualControllerType.KeyboardMouse;
                    bool isDs4 = SlotControllerTypes[padIndex] == VirtualControllerType.PlayStation;

                    if (slotCount == 0)
                    {
                        CombinedOutputStates[padIndex].Clear();
                        if (isExtended) CombinedExtendedRawStates[padIndex].Clear();
                        if (isMidi) CombinedMidiRawStates[padIndex].Clear();
                        if (isKbm) CombinedKbmRawStates[padIndex].Clear();
                        if (isDs4) CombinedTouchpadStates[padIndex] = default;
                        continue;
                    }

                    if (slotCount == 1)
                    {
                        // Single device — no combination needed, direct copy.
                        CombinedOutputStates[padIndex] = _padIndexBuffer[0].OutputState;
                        if (isExtended) CombinedExtendedRawStates[padIndex] = _padIndexBuffer[0].ExtendedRawOutputState;
                        if (isMidi) CombinedMidiRawStates[padIndex] = _padIndexBuffer[0].MidiRawOutputState;
                        if (isKbm) CombinedKbmRawStates[padIndex] = _padIndexBuffer[0].KbmRawOutputState;
                        if (isDs4)
                        {
                            CombinedTouchpadStates[padIndex] = _padIndexBuffer[0].TouchpadOutputState;
                            // Bake the touchpad-click into the combined Gamepad
                            // bitmap so every downstream consumer (Step 5
                            // virtual-controller submit, Step 6 retrieved-
                            // states copy, UserEffectsDispatcher InputReactive
                            // pulse detection) sees the press uniformly.
                            // Without this, only Step 5's local copy + the
                            // packer saw the click; the dispatcher's
                            // SlotButtonsProvider read missed it and the
                            // touchpad couldn't trigger InputReactive flashes.
                            if (CombinedTouchpadStates[padIndex].Click)
                            {
                                var gp = CombinedOutputStates[padIndex];
                                gp.Buttons |= Gamepad.TOUCHPAD;
                                CombinedOutputStates[padIndex] = gp;
                            }
                        }
                        continue;
                    }

                    // Multiple devices — merge all states.
                    var combined = new Gamepad();
                    ExtendedRawState combinedRaw = default;
                    bool firstRaw = true;
                    MidiRawState combinedMidi = default;
                    bool firstMidi = true;
                    KbmRawState combinedKbm = default;
                    bool firstKbm = true;

                    for (int si = 0; si < slotCount; si++)
                    {
                        var us = _padIndexBuffer[si];
                        if (us == null)
                            continue;

                        var gp = us.OutputState;
                        MergeGamepad(ref combined, ref gp);

                        if (isExtended)
                        {
                            var rawState = us.ExtendedRawOutputState;
                            // An offline assigned device never had Step 3
                            // populate its raw state — all arrays null. If it
                            // lands first in the buffer and seeds the combine,
                            // every later merge silently no-ops against the
                            // null destination (MergeExtendedRaw's null guards)
                            // and the slot's combined output dies while each
                            // online device's own state stays live. Skip
                            // never-populated states entirely.
                            if (rawState.Axes == null && rawState.Buttons == null && rawState.Povs == null)
                            {
                                // nothing to contribute
                            }
                            else if (firstRaw)
                            {
                                combinedRaw = rawState;
                                firstRaw = false;
                            }
                            else
                            {
                                // Pass the slot's layout so the merge can
                                // distinguish trigger slots from stick slots
                                // and use the right comparison rule per slot
                                // (pressed-wins for triggers, magnitude-wins
                                // for sticks). See MergeExtendedRaw docstring.
                                MergeExtendedRaw(ref combinedRaw, ref rawState, SlotCustomLayouts[padIndex]);
                            }
                        }

                        if (isMidi)
                        {
                            if (firstMidi)
                            {
                                combinedMidi = us.MidiRawOutputState;
                                firstMidi = false;
                            }
                            else
                            {
                                combinedMidi = MidiRawState.Combine(combinedMidi, us.MidiRawOutputState);
                            }
                        }

                        if (isKbm)
                        {
                            if (firstKbm)
                            {
                                combinedKbm = us.KbmRawOutputState;
                                firstKbm = false;
                            }
                            else
                            {
                                combinedKbm = KbmRawState.Combine(combinedKbm, us.KbmRawOutputState);
                            }
                        }
                    }

                    CombinedOutputStates[padIndex] = combined;
                    if (isExtended) CombinedExtendedRawStates[padIndex] = combinedRaw;
                    if (isMidi) CombinedMidiRawStates[padIndex] = combinedMidi;
                    if (isKbm) CombinedKbmRawStates[padIndex] = combinedKbm;

                    // Touchpad: first device with active finger wins (single-source).
                    if (isDs4)
                    {
                        var combinedTp = default(TouchpadState);
                        for (int si = 0; si < slotCount; si++)
                        {
                            var us = _padIndexBuffer[si];
                            if (us == null) continue;
                            var tp = us.TouchpadOutputState;
                            if (tp.Down0 || tp.Down1 || tp.Click)
                            {
                                combinedTp = tp;
                                break;
                            }
                        }
                        CombinedTouchpadStates[padIndex] = combinedTp;

                        // Bake the touchpad-click into the combined Gamepad
                        // bitmap — see the slotCount==1 branch for rationale.
                        if (combinedTp.Click)
                        {
                            var gp = CombinedOutputStates[padIndex];
                            gp.Buttons |= Gamepad.TOUCHPAD;
                            CombinedOutputStates[padIndex] = gp;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RaiseError($"Error combining states for pad {padIndex}", ex);
                    CombinedOutputStates[padIndex].Clear();
                }
            }
        }

        /// <summary>
        /// Merges a source Gamepad into a destination Gamepad using combination rules:
        ///   Buttons  → OR
        ///   Triggers → MAX
        ///   Thumbs   → largest magnitude per axis
        /// </summary>
        /// <param name="dest">Destination gamepad (accumulated result).</param>
        /// <param name="src">Source gamepad to merge in.</param>
        private static void MergeGamepad(ref Gamepad dest, ref Gamepad src)
        {
            // Buttons: OR combination — any device can activate any button.
            dest.Buttons |= src.Buttons;
            // Share lives outside the 16-bit Buttons mask but combines
            // with the same OR semantics.
            dest.Share |= src.Share;

            // Triggers: take the higher value.
            if (src.LeftTrigger > dest.LeftTrigger)
                dest.LeftTrigger = src.LeftTrigger;
            if (src.RightTrigger > dest.RightTrigger)
                dest.RightTrigger = src.RightTrigger;

            // Thumbsticks: largest absolute magnitude wins per axis.
            // This allows, e.g., one device to control the left stick and another
            // to control the right stick without interference.
            if (Math.Abs((int)src.ThumbLX) > Math.Abs((int)dest.ThumbLX))
                dest.ThumbLX = src.ThumbLX;
            if (Math.Abs((int)src.ThumbLY) > Math.Abs((int)dest.ThumbLY))
                dest.ThumbLY = src.ThumbLY;
            if (Math.Abs((int)src.ThumbRX) > Math.Abs((int)dest.ThumbRX))
                dest.ThumbRX = src.ThumbRX;
            if (Math.Abs((int)src.ThumbRY) > Math.Abs((int)dest.ThumbRY))
                dest.ThumbRY = src.ThumbRY;
        }

        /// <summary>
        /// <summary>
        /// Merges a source ExtendedRawState into a destination, layout-aware
        /// so stick axes and trigger axes use different comparison rules.
        /// Buttons: OR. POVs: first non-centered.
        ///
        /// <para><b>Why per-axis-type rules.</b> ExtendedRawState stores both
        /// stick axes and trigger axes in the same <c>Axes</c> array, with
        /// values centered at different points:</para>
        ///
        /// <list type="bullet">
        /// <item>Stick axis: signed short, <b>centered at 0</b>, range
        /// <c>-32768..+32767</c>. "Most-deflected wins" is the natural
        /// merge — a stick fully right (<c>+32767</c>, magnitude
        /// <c>32767</c>) beats a stick centered (<c>0</c>, magnitude
        /// <c>0</c>), regardless of which physical device produced it.</item>
        /// <item>Trigger axis: signed short, <b>released at -32768</b>,
        /// range <c>-32768</c> (released) <c>..+32767</c> (fully pressed).
        /// The "pressed" direction is one-sided. "Most-deflected wins"
        /// would be wrong: a released trigger has magnitude
        /// <c>|-32768| = 32768</c>, which is larger than any pressed value's
        /// magnitude <c>|32767| = 32767</c>, so a released trigger would
        /// always beat a pressed one. The correct rule is "highest value
        /// wins" — pressed (<c>+32767</c>) numerically beats released
        /// (<c>-32768</c>) and beats any partial press.</item>
        /// </list>
        ///
        /// <para><b>The bug this fixes.</b> Pre-fix, the merge applied
        /// magnitude-wins to every axis index. When two devices were
        /// mapped to the same Custom Extended slot — e.g. a joystick whose
        /// auto-mapped Axis 5 (LT) sat at released <c>-32768</c> and a
        /// keyboard key mapped to <c>ExtendedAxis2</c> that the user
        /// pressed — the merge picked the joystick's released
        /// <c>-32768</c> (magnitude <c>32768</c>) over the keyboard's
        /// pressed <c>+32767</c> (magnitude <c>32767</c>). The wire-side
        /// trigger appeared stuck at 0% no matter how hard the user hit
        /// the keyboard key. Joystick-button-to-trigger and
        /// joystick-axis-to-trigger paths happened to work because only
        /// one device populated <c>ExtendedAxis2</c> in those cases, so
        /// no race occurred.</para>
        ///
        /// <para><b>Layout-aware indexing.</b> Trigger axis indices are
        /// computed from <see cref="CustomControllerLayout"/>'s sticks +
        /// triggers counts using the same interleaved formula as
        /// <c>ExtendedSlotConfig.ComputeAxisLayout</c> — groups of
        /// <c>(stickX, stickY, trigger)</c> while both are available, then
        /// trailing sticks pack pairwise, then trailing triggers one at a
        /// time. Any axis index not in the trigger set is treated as a
        /// stick axis. Layout passed via <paramref name="layout"/>; the
        /// caller (<c>CombineOutputStates</c>) reads it from
        /// <c>SlotCustomLayouts[padIndex]</c>.</para>
        /// </summary>
        private static void MergeExtendedRaw(
            ref ExtendedRawState dest,
            ref ExtendedRawState src,
            CustomControllerLayout layout)
        {
            if (src.Axes != null && dest.Axes != null)
            {
                int len = Math.Min(src.Axes.Length, dest.Axes.Length);
                for (int i = 0; i < len; i++)
                {
                    if (layout.IsTriggerSlot(i))
                    {
                        // Pressed-wins (highest value). Released is at
                        // short.MinValue, so any partial / full press
                        // anywhere in [-32767, +32767] wins over it; the
                        // most-pressed press wins among multiple devices.
                        if (src.Axes[i] > dest.Axes[i])
                            dest.Axes[i] = src.Axes[i];
                    }
                    else
                    {
                        // Stick axis: most-deflected wins.
                        if (Math.Abs((int)src.Axes[i]) > Math.Abs((int)dest.Axes[i]))
                            dest.Axes[i] = src.Axes[i];
                    }
                }
            }

            if (src.Buttons != null && dest.Buttons != null)
            {
                int len = Math.Min(src.Buttons.Length, dest.Buttons.Length);
                for (int i = 0; i < len; i++)
                    dest.Buttons[i] |= src.Buttons[i];
            }

            if (src.Povs != null && dest.Povs != null)
            {
                int len = Math.Min(src.Povs.Length, dest.Povs.Length);
                for (int i = 0; i < len; i++)
                {
                    if (dest.Povs[i] < 0 && src.Povs[i] >= 0)
                        dest.Povs[i] = src.Povs[i];
                }
            }
        }
    }
}
