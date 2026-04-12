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
                    // Use non-allocating overload with pre-allocated buffer.
                    int slotCount = settings.FindByPadIndex(padIndex, _padIndexBuffer);

                    bool isCustomVJoy = SlotControllerTypes[padIndex] == VirtualControllerType.Extended
                                     && SlotExtendedIsCustom[padIndex];
                    bool isMidi = SlotControllerTypes[padIndex] == VirtualControllerType.Midi;
                    bool isKbm = SlotControllerTypes[padIndex] == VirtualControllerType.KeyboardMouse;
                    bool isDs4 = SlotControllerTypes[padIndex] == VirtualControllerType.Sony;

                    if (slotCount == 0)
                    {
                        CombinedOutputStates[padIndex].Clear();
                        if (isCustomVJoy) CombinedVJoyRawStates[padIndex].Clear();
                        if (isMidi) CombinedMidiRawStates[padIndex].Clear();
                        if (isKbm) CombinedKbmRawStates[padIndex].Clear();
                        if (isDs4) CombinedTouchpadStates[padIndex] = default;
                        continue;
                    }

                    if (slotCount == 1)
                    {
                        // Single device — no combination needed, direct copy.
                        CombinedOutputStates[padIndex] = _padIndexBuffer[0].OutputState;
                        if (isCustomVJoy) CombinedVJoyRawStates[padIndex] = _padIndexBuffer[0].VJoyRawOutputState;
                        if (isMidi) CombinedMidiRawStates[padIndex] = _padIndexBuffer[0].MidiRawOutputState;
                        if (isKbm) CombinedKbmRawStates[padIndex] = _padIndexBuffer[0].KbmRawOutputState;
                        if (isDs4) CombinedTouchpadStates[padIndex] = _padIndexBuffer[0].TouchpadOutputState;
                        continue;
                    }

                    // Multiple devices — merge all states.
                    var combined = new Gamepad();
                    VJoyRawState combinedRaw = default;
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

                        if (isCustomVJoy)
                        {
                            var rawState = us.VJoyRawOutputState;
                            if (firstRaw)
                            {
                                combinedRaw = rawState;
                                firstRaw = false;
                            }
                            else
                            {
                                MergeVJoyRaw(ref combinedRaw, ref rawState);
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
                    if (isCustomVJoy) CombinedVJoyRawStates[padIndex] = combinedRaw;
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
        /// Merges a source VJoyRawState into a destination.
        /// Axes: largest magnitude wins. Buttons: OR. POVs: first non-centered.
        /// </summary>
        private static void MergeVJoyRaw(ref VJoyRawState dest, ref VJoyRawState src)
        {
            if (src.Axes != null && dest.Axes != null)
            {
                int len = Math.Min(src.Axes.Length, dest.Axes.Length);
                for (int i = 0; i < len; i++)
                {
                    if (Math.Abs((int)src.Axes[i]) > Math.Abs((int)dest.Axes[i]))
                        dest.Axes[i] = src.Axes[i];
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
