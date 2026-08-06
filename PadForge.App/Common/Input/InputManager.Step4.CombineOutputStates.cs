using System;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>PER-SLOT buffer for the multi-device MIDI combine. Per slot,
        /// not shared: one buffer across slots would make every slot's combine
        /// write the same arrays, so a later slot would overwrite an earlier
        /// one's result. Never published either, because the loop copies out of
        /// it into CombinedMidiRawStates rather than assigning it, so the UI
        /// thread never sees this buffer at all.</summary>
        private readonly MidiRawState[] _midiCombineScratch = new MidiRawState[MaxPads];

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

                    bool isExtended = SlotControllerTypes[padIndex] is VirtualControllerType.Extended
                                         or VirtualControllerType.Nintendo
                                     && SlotRawHidSurface[padIndex];
                    bool isMidi = SlotControllerTypes[padIndex] == VirtualControllerType.Midi;
                    bool isKbm = SlotControllerTypes[padIndex] == VirtualControllerType.KeyboardMouse;
                    bool isDs4 = SlotControllerTypes[padIndex] == VirtualControllerType.PlayStation;

                    if (slotCount == 0)
                    {
                        CombinedOutputStates[padIndex].Clear();
                        if (isExtended) CombinedRawHidStates[padIndex].Clear();
                        if (isMidi) CombinedMidiRawStates[padIndex].Clear();
                        if (isKbm) CombinedKbmRawStates[padIndex].Clear();
                        if (isDs4) CombinedTouchpadStates[padIndex] = default;
                        continue;
                    }

                    if (slotCount == 1)
                    {
                        // Single device — no combination needed, direct copy.
                        CombinedOutputStates[padIndex] = _padIndexBuffer[0].OutputState;
                        if (isExtended)
                        {
                            // COPY, never alias: the SOCD cleaner and the
                            // inactive-transition Clear write into this
                            // state, and a bare struct assign would point
                            // them at the device's published arrays.
                            var singleRaw = _padIndexBuffer[0].RawHidOutputState;
                            CopyRawInto(ref CombinedRawHidStates[padIndex], ref singleRaw);
                        }
                        if (isMidi)
                        {
                            // COPY, never alias, for the same reason the raw
                            // lane above documents. MidiRawState carries a
                            // byte[] and a bool[], so a bare struct assign
                            // shares them with the DEVICE's published state,
                            // and the slotCount == 0 branch at the top of this
                            // loop calls Clear() on the combined state, which
                            // writes 64 into every CC and false into every
                            // note. Unassigning the last MIDI device from a
                            // slot therefore reached back and wiped that
                            // device's own live arrays.
                            //
                            // KbmRawState needs no such copy: it is all value
                            // fields, so its struct assign already copies.
                            var singleMidi = _padIndexBuffer[0].MidiRawOutputState;
                            CopyMidiInto(ref CombinedMidiRawStates[padIndex], ref singleMidi);
                        }
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
                            var rawState = us.RawHidOutputState;
                            // An offline assigned device never had Step 3
                            // populate its raw state — all arrays null. If it
                            // lands first in the buffer and seeds the combine,
                            // every later merge silently no-ops against the
                            // null destination (MergeRawHid's null guards)
                            // and the slot's combined output dies while each
                            // online device's own state stays live. Skip
                            // never-populated states entirely.
                            if (rawState.Axes == null && rawState.Buttons == null && rawState.Povs == null)
                            {
                                // nothing to contribute
                            }
                            else if (firstRaw)
                            {
                                // Seed the pad's OWN arrays. Seeding with a
                                // bare assign made every subsequent
                                // MergeRawHid store land in THIS device's
                                // published state, so device A's card showed
                                // device B's presses on a two-device slot.
                                CopyRawInto(ref CombinedRawHidStates[padIndex], ref rawState);
                                firstRaw = false;
                            }
                            else
                            {
                                // Pass the slot's layout so the merge can
                                // distinguish trigger slots from stick slots
                                // and use the right comparison rule per slot
                                // (pressed-wins for triggers, magnitude-wins
                                // for sticks). See MergeRawHid docstring.
                                MergeRawHid(ref CombinedRawHidStates[padIndex], ref rawState,
                                    SlotCustomLayouts[padIndex]);
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
                                // Reuses this slot's buffer. Safe to pass as
                                // the destination while it is also the left
                                // operand: the combine reads index i of both
                                // inputs before writing index i of the result.
                                combinedMidi = MidiRawState.CombineInto(
                                    combinedMidi, us.MidiRawOutputState, _midiCombineScratch[padIndex]);
                                _midiCombineScratch[padIndex] = combinedMidi;
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
                    // The contributing devices wrote straight into the pad's
                    // own arrays above. When NONE contributed (every assigned
                    // device offline, so all their arrays are null) the slot
                    // must keep reading as absent rather than as a stale
                    // frame, which is what the old `= combinedRaw` default
                    // did here.
                    if (isExtended && firstRaw) CombinedRawHidStates[padIndex] = default;
                    // Copy here too. combinedMidi is a fresh Combine result
                    // only when TWO OR MORE devices on this slot contributed
                    // MIDI. With several devices assigned but only one of them
                    // MIDI, no Combine ever runs and combinedMidi is still that
                    // device's published state, so a bare assign aliases it and
                    // the empty-slot Clear() writes through.
                    if (isMidi) CopyMidiInto(ref CombinedMidiRawStates[padIndex], ref combinedMidi);
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
            dest.MicMute |= src.MicMute;
            dest.LeftPaddle |= src.LeftPaddle;
            dest.RightPaddle |= src.RightPaddle;
            dest.LeftFunction |= src.LeftFunction;
            dest.RightFunction |= src.RightFunction;

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
        /// Merges a source RawHidState into a destination, layout-aware
        /// so stick axes and trigger axes use different comparison rules.
        /// Buttons: OR. POVs: first non-centered.
        ///
        /// <para><b>Why per-axis-type rules.</b> RawHidState stores both
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
        /// keyboard key mapped to <c>RawAxis2</c> that the user
        /// pressed — the merge picked the joystick's released
        /// <c>-32768</c> (magnitude <c>32768</c>) over the keyboard's
        /// pressed <c>+32767</c> (magnitude <c>32767</c>). The wire-side
        /// trigger appeared stuck at 0% no matter how hard the user hit
        /// the keyboard key. Joystick-button-to-trigger and
        /// joystick-axis-to-trigger paths happened to work because only
        /// one device populated <c>RawAxis2</c> in those cases, so
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
        private static void MergeRawHid(
            ref RawHidState dest,
            ref RawHidState src,
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

        /// <summary>Copies <paramref name="src"/> into <paramref name="dst"/>
        /// so that <paramref name="dst"/> OWNS its arrays, reusing them
        /// whenever the layout is unchanged.
        ///
        /// <para>The combine used to assign the struct directly, which
        /// copies only the array REFERENCES: RawHidState is a struct whose
        /// Axes / Buttons / Povs / HardwareAxes are arrays. Every later
        /// write in the slot pipeline landed in the contributing device's
        /// PUBLISHED RawHidOutputState, which UserSetting and Step 3 both
        /// document as immutable after publish and which the UI reads
        /// cross-thread at 30 Hz. The writers were MergeRawHid's per-axis
        /// and per-button stores, the Step 4b macro pass, the Step 5 SOCD
        /// cleaner, and both inactive-transition Clear calls.</para>
        ///
        /// <para>Lengths and values are reproduced exactly, so every
        /// consumer downstream sees precisely what it saw before. Null
        /// stays null: a never-populated state must keep reading as
        /// absent rather than as an all-zero frame.</para></summary>
        internal static void CopyRawInto(ref RawHidState dst, ref RawHidState src)
        {
            CopyArray(ref dst.Axes, src.Axes);
            CopyArray(ref dst.Buttons, src.Buttons);
            CopyArray(ref dst.Povs, src.Povs);
            CopyArray(ref dst.HardwareAxes, src.HardwareAxes);
        }

        /// <summary>Midi twin of <see cref="CopyRawInto"/>. See the call site
        /// for why the combined MIDI state must not alias a device's.</summary>
        internal static void CopyMidiInto(ref MidiRawState dst, ref MidiRawState src)
        {
            CopyArray(ref dst.CcValues, src.CcValues);
            CopyArray(ref dst.Notes, src.Notes);
        }

        private static void CopyArray<T>(ref T[] dst, T[] src)
        {
            if (src == null) { dst = null; return; }
            if (dst == null || dst.Length != src.Length) dst = new T[src.Length];
            Array.Copy(src, dst, src.Length);
        }
    }
}
