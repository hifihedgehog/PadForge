using System.Diagnostics;
using System.Threading;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;
using PadForge.Services;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  The problem recorder's taps (#597)
        //
        //  Each tap reads RecordingCapture.Active once, a volatile read, and
        //  does nothing more while no recording runs and no last minute is
        //  kept. While one does, the capture compares and pushes plain
        //  values into its ring: it allocates nothing and takes no lock.
        //  One pass in sixty-four is timed, and the mean is written on each
        //  second's timing line.
        // ─────────────────────────────────────────────

        /// <summary>A Problem Recording macro's Start or Stop Recording.</summary>
        internal const int PendingRecordingToggle = 1;

        /// <summary>A Problem Recording macro's Save the Last 60 Seconds.</summary>
        internal const int PendingRecordingSave = 2;

        /// <summary>What Problem Recording macros asked for since the UI
        /// thread last looked: a set of the two bits above. The poll thread
        /// sets bits, and InputService takes and clears them on the UI
        /// thread, as it takes <see cref="PendingToggleWindow"/>.</summary>
        internal int PendingProblemRecording;

        /// <summary>A Problem Recording macro action (#597). Starting,
        /// stopping and saving make a file, so they are left for the UI
        /// thread. A marker is written here, at the time the macro fired.</summary>
        private void RunProblemRecordingAction(PadForge.ViewModels.MacroAction action)
        {
            switch (action.ProblemRecordingMode)
            {
                case PadForge.ViewModels.ProblemRecordingMode.Mark:
                    RecordingCapture.Active?.Mark();
                    break;
                case PadForge.ViewModels.ProblemRecordingMode.SaveLastMinute:
                    Interlocked.Or(ref PendingProblemRecording, PendingRecordingSave);
                    break;
                default:
                    Interlocked.Or(ref PendingProblemRecording, PendingRecordingToggle);
                    break;
            }
        }

        private static void RecordInput(UserDevice device, CustomInputState state)
        {
            var rec = RecordingCapture.Active;
            if (rec == null) return;
            if (!rec.Timed)
            {
                rec.Input(device, state);
                return;
            }
            long before = Stopwatch.GetTimestamp();
            rec.Input(device, state);
            rec.AddCost(Stopwatch.GetTimestamp() - before);
        }

        /// <summary>What became of a slot's frame before any hand-over:
        /// everything but a hand-over that went through, which is said once
        /// it has, and one that threw.</summary>
        private int RecordSentState(RecordingCapture rec, int padIndex, IVirtualController vc)
        {
            int state;
            if (!SettingsManager.SlotCreated[padIndex] || !SettingsManager.SlotEnabled[padIndex])
                state = RecordingCodec.SentState.Off;
            else if (vc == null)
                state = _slotInactiveCounter[padIndex] == 0 ? RecordingCodec.SentState.Absent : RecordingCodec.SentState.Resting;
            else if (!vc.IsConnected)
                state = RecordingCodec.SentState.Disconnected;
            else
                state = _slotInactiveCounter[padIndex] <= 1 ? RecordingCodec.SentState.Ok : RecordingCodec.SentState.Resting;
            if (state != RecordingCodec.SentState.Ok) rec.Sent(padIndex, state, (int)SlotControllerTypes[padIndex]);
            return state;
        }

        /// <summary>A created slot's frame on a pass that hands nothing
        /// over, by the lane its type takes, so a frame that changes while
        /// the virtual controller is absent is in the file.</summary>
        private void RecordUnsent(RecordingCapture rec, int padIndex)
        {
            if (!SettingsManager.SlotCreated[padIndex]) return;
            long before = rec.Timed ? Stopwatch.GetTimestamp() : 0;
            switch (SlotControllerTypes[padIndex])
            {
                case VirtualControllerType.Midi:
                    rec.OutMidi(padIndex, in CombinedMidiRawStates[padIndex]);
                    break;
                case VirtualControllerType.KeyboardMouse:
                    rec.OutKbm(padIndex, in CombinedKbmRawStates[padIndex]);
                    break;
                case VirtualControllerType.Vr:
                    rec.OutVr(padIndex, in CombinedVrRawStates[padIndex]);
                    break;
                case VirtualControllerType.Extended or VirtualControllerType.Nintendo when SlotRawHidSurface[padIndex]:
                    rec.OutRaw(padIndex, in CombinedRawHidStates[padIndex]);
                    break;
                default:
                    rec.OutGamepad(padIndex, in CombinedOutputStates[padIndex]);
                    break;
            }
            if (before != 0) rec.AddCost(Stopwatch.GetTimestamp() - before);
        }

        private static void RecordOut(RecordingCapture rec, int padIndex, in MidiRawState midi)
        {
            long before = rec.Timed ? Stopwatch.GetTimestamp() : 0;
            rec.OutMidi(padIndex, in midi);
            if (before != 0) rec.AddCost(Stopwatch.GetTimestamp() - before);
        }

        private static void RecordOut(RecordingCapture rec, int padIndex, in KbmRawState kbm)
        {
            long before = rec.Timed ? Stopwatch.GetTimestamp() : 0;
            rec.OutKbm(padIndex, in kbm);
            if (before != 0) rec.AddCost(Stopwatch.GetTimestamp() - before);
        }

        private static void RecordOut(RecordingCapture rec, int padIndex, in VrRawState vr)
        {
            long before = rec.Timed ? Stopwatch.GetTimestamp() : 0;
            rec.OutVr(padIndex, in vr);
            if (before != 0) rec.AddCost(Stopwatch.GetTimestamp() - before);
        }

        private static void RecordOut(RecordingCapture rec, int padIndex, in RawHidState raw, in TouchpadState touch, in MotionSnapshot motion)
        {
            long before = rec.Timed ? Stopwatch.GetTimestamp() : 0;
            rec.OutRaw(padIndex, in raw);
            rec.OutTouch(padIndex, in touch);
            rec.OutMotion(padIndex, in motion);
            if (before != 0) rec.AddCost(Stopwatch.GetTimestamp() - before);
        }

        private static void RecordOut(RecordingCapture rec, int padIndex, in Gamepad gamepad, in TouchpadState touch, in MotionSnapshot motion, bool carriesTouchAndMotion)
        {
            long before = rec.Timed ? Stopwatch.GetTimestamp() : 0;
            rec.OutGamepad(padIndex, in gamepad);
            if (carriesTouchAndMotion)
            {
                rec.OutTouch(padIndex, in touch);
                rec.OutMotion(padIndex, in motion);
            }
            if (before != 0) rec.AddCost(Stopwatch.GetTimestamp() - before);
        }

        /// <summary>What an active pass ends with: the rumble games ask of
        /// each slot, a stall or a late start, and once a second the timing
        /// the poll loop summed.</summary>
        private void RecordPass(RecordingCapture rec, long passGap, int rateHz, long sdlMs, long enumMs, long cycleMs)
        {
            bool timed = rec.Timed;
            long before = timed ? Stopwatch.GetTimestamp() : 0;
            rec.Rest(0);

            bool controllersOn = VirtualControllersEnabled;
            for (int slot = 0; slot < MaxPads; slot++)
            {
                if (!SettingsManager.SlotCreated[slot]) continue;
                rec.GameRumble(slot, VibrationStates[slot]);
                // With the virtual controllers switched off Step 5 hands
                // nothing over and says nothing, so it is said here.
                if (!controllersOn) rec.Sent(slot, RecordingCodec.SentState.Off, (int)SlotControllerTypes[slot]);
            }

            if (sdlMs >= 25 || enumMs >= 25 || cycleMs >= 50) rec.Stall(sdlMs, enumMs, cycleMs);
            // A pass that began more than three periods and five
            // milliseconds after the one before it began late.
            if (passGap > 3 * (Stopwatch.Frequency / rateHz) && passGap > Stopwatch.Frequency / 200) rec.Gap(passGap, rateHz);

            // The capture's own clock, which every other line of the pass is
            // stamped by.
            long now = rec.Ticks();
            int second = rec.SecondPhase(now);
            if (second != 0) _pollLoad.TakeRecorded(second == 2 ? rec : null, now, rateHz);

            rec.EndPass();
            if (timed) rec.AddPassCost(Stopwatch.GetTimestamp() - before);
        }

        /// <summary>What a pass ends with while the engine rests: idle with
        /// no slot in use (1), or suspended behind another window (2).</summary>
        private static void RecordRestingPass(int rest)
        {
            var rec = RecordingCapture.Active;
            if (rec == null) return;
            rec.Rest(rest);
            rec.EndPass();
        }
    }
}
