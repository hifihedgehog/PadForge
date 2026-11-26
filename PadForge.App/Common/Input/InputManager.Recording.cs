using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
        //  The problem recorder's taps (#600)
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

        /// <summary>A Problem Recording macro action (#600). Starting,
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

        /// <summary>Before a recorded read (#600): the call is named with its
        /// device, and SDL's error and Windows' are cleared, so the text a
        /// failed read leaves is its own. SDL keeps a thread's last error
        /// until it is cleared (SDL_error.h, SDL_GetError).</summary>
        private static void BeginRecordedRead(RecordingCapture rec, UserDevice device, ISdlInputDevice live)
        {
            rec.BeginCall(device, RecordingCodec.CallKind.Read);
            if (live is SdlDeviceWrapper && !_noSdlClear)
            {
                try { SdlClearError(); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _noSdlClear = true; }
            }
            Marshal.SetLastPInvokeError(0);
        }

        private static bool _noSdlClear;

        [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_ClearError")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SdlClearError();

        /// <summary>A failed read (#600): the device goes offline in the
        /// file at once, and a line says how many reads of it failed in a row
        /// and the error's own text, made only when the line is written.</summary>
        private static void RecordReadFailed(UserDevice device, ISdlInputDevice live)
        {
            var rec = RecordingCapture.Active;
            if (rec == null) return;
            rec.InputLost(device, RecordingCodec.OfflineReason.ReadFailed);
            if ((rec.Parts & PadForge.Engine.Recording.RecordingParts.Input) == 0) return;
            int inARow = rec.ReadFailed(device);
            if (ProblemRecorder.WritesReadFailure(inARow)) ProblemRecorder.Instance.TakeReadFailed(device, inARow, ReadError(live));
        }

        /// <summary>A read that threw (#600): a failed read in the recording
        /// as one that gave no state is, with the exception as its error.
        /// Always false, so the pass's own handler takes the exception.</summary>
        private static bool RecordReadThrew(RecordingCapture rec, UserDevice device, Exception ex)
        {
            if (rec == null || (rec.Parts & PadForge.Engine.Recording.RecordingParts.Input) == 0) return false;
            int inARow = rec.ReadFailed(device);
            if (ProblemRecorder.WritesReadFailure(inARow)) ProblemRecorder.Instance.TakeReadFailed(device, inARow, ThrownReadError(ex));
            return false;
        }

        /// <summary>
        /// The error's own text for a read that gave no state (#600). For a
        /// device SDL reads: SDL's text, with any code it carries, or why the
        /// wrapper refused the read when SDL set none. For another reader:
        /// the last Windows error its read left, by number and words, which
        /// a call that worked may have left too, or that it gave no reason.
        /// </summary>
        internal static string ReadError(ISdlInputDevice live)
        {
            if (live is SdlDeviceWrapper sdl)
            {
                try
                {
                    string text = SDL3.SDL.SDL_GetError();
                    if (!string.IsNullOrEmpty(text)) return text;
                    if (sdl.Joystick == IntPtr.Zero) return "its SDL handle is closed";
                    return SDL3.SDL.SDL_JoystickConnected(sdl.Joystick) ? "SDL gave no reason" : "SDL says it is no longer attached";
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    return "SDL could not be asked why";
                }
            }
            int code = Marshal.GetLastPInvokeError();
            if (code != 0)
                return "the read's last Windows error, " + code.ToString(CultureInfo.InvariantCulture) + " (0x" + code.ToString("X8", CultureInfo.InvariantCulture) + "): "
                    + new Win32Exception(code).Message;
            return (live == null ? "its reader" : live.GetType().Name) + " gave no reason";
        }

        /// <summary>A read's exception as its error text: its type and its
        /// message.</summary>
        internal static string ThrownReadError(Exception ex) => ex.GetType().Name + ": " + (ex.Message ?? string.Empty);

        /// <summary>
        /// Hands a device its feedback for this pass. A problem recording
        /// names the write with its device (#600): a rumble write, which is a
        /// force write once it reaches the device's force effects or a
        /// wheel's force reports, so one that takes long is written and one
        /// that does not return is said while it hangs.
        /// </summary>
        private void WriteFeedback(UserDevice ud)
        {
            var callWatch = RecordingCapture.Active;
            if (callWatch == null || ud == null)
            {
                ApplyForceFeedback(ud);
                return;
            }
            callWatch.BeginCall(ud, RecordingCodec.CallKind.Rumble);
            try { ApplyForceFeedback(ud); }
            finally { callWatch.EndCall(); }
        }

        /// <summary>A virtual controller that was not fed, and why: the
        /// exception its hand-over threw (#600).</summary>
        private static void RecordNotFed(RecordingCapture rec, int padIndex, Exception ex)
        {
            if (rec == null || (rec.Parts & PadForge.Engine.Recording.RecordingParts.Output) == 0) return;
            ProblemRecorder.Instance.TakeRepeatedNote(RecordingCodec.LayerSent, padIndex, null, ex.GetType().Name,
                () => "not fed: " + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
        }

        private static void RecordInput(UserDevice device, CustomInputState state)
        {
            var rec = RecordingCapture.Active;
            if (rec == null) return;
            int ended;
            if (!rec.Timed) ended = rec.Input(device, state);
            else
            {
                long before = Stopwatch.GetTimestamp();
                ended = rec.Input(device, state);
                rec.AddCost(Stopwatch.GetTimestamp() - before);
            }
            // A good read that ends a run of failed ones (#600).
            if (ended > 0) ProblemRecorder.Instance.TakeReadsAgain(device, ended);
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
            // HIDMaestro refused a frame of it, and nothing reaches it until
            // it is made again (HMaestroRefusalLatch).
            else if (vc is HMaestroVirtualController { SubmitsRefused: true })
                state = RecordingCodec.SentState.Failed;
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
