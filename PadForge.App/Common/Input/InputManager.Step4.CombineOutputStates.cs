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
        //  each virtual controller slot (0–3) into a single combined state.
        //
        //  Combination rules:
        //    - Buttons: OR (any device pressing a button activates it)
        //    - Triggers: MAX (highest trigger value wins)
        //    - Thumbsticks: largest-magnitude wins per axis
        // ─────────────────────────────────────────────

        /// <summary>
        /// Step 4: For each of the 4 virtual controller slots, find all UserSettings
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

                    if (slotCount == 0)
                    {
                        CombinedOutputStates[padIndex].Clear();
                        continue;
                    }

                    if (slotCount == 1)
                    {
                        // Single device — no combination needed, direct copy.
                        CombinedOutputStates[padIndex] = _padIndexBuffer[0].OutputState;
                        continue;
                    }

                    // Multiple devices — merge all states.
                    var combined = new Gamepad();

                    for (int si = 0; si < slotCount; si++)
                    {
                        var us = _padIndexBuffer[si];
                        if (us == null)
                            continue;

                        var gp = us.OutputState;
                        MergeGamepad(ref combined, ref gp);
                    }

                    CombinedOutputStates[padIndex] = combined;
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
    }
}
