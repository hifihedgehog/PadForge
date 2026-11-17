using System;
using System.Threading;
using PadForge.Services;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>
        /// Set by macro execution to request crosshair toggles (#585): a bit
        /// per crosshair's number, and bit 0 for every crosshair, as
        /// <see cref="ToggleScreenOverlayMask"/> carries the Screen Overlays.
        /// InputService takes and clears it on the UI thread.
        /// </summary>
        public int ToggleCrosshairMask;

        /// <summary>
        /// 1 after a Toggle Magnifier macro (#585), until InputService takes
        /// it on the UI thread and flips the magnifier's switch.
        /// </summary>
        public int ToggleMagnifierRequested;

        /// <summary>How long the magnifier stays up after Magnifier While
        /// Held's last beat: the 100 ms VoiceMacroService.NoteListenHeld gives
        /// Voice Listen While Held, many frames at any polling rate.</summary>
        internal const int MagnifierHoldMs = 100;

        private long _magnifierHeldUntil;

        /// <summary>A Toggle Crosshair macro, for the UI thread to carry out:
        /// a bit per crosshair's number, and bit 0 for every crosshair.</summary>
        private void RequestCrosshairToggle(int number)
            => Interlocked.Or(ref ToggleCrosshairMask, 1 << Math.Clamp(number, 0, CrosshairData.MaxCrosshairs));

        /// <summary>A Toggle Magnifier macro, for the UI thread to carry
        /// out.</summary>
        private void RequestMagnifierToggle() => Interlocked.Exchange(ref ToggleMagnifierRequested, 1);

        /// <summary>Magnifier While Held's beat, once a frame while its macro
        /// is held. Release needs no call: the hold lapses
        /// <see cref="MagnifierHoldMs"/> after the last beat.</summary>
        private void NoteMagnifierHeld()
            => Interlocked.Exchange(ref _magnifierHeldUntil, Environment.TickCount64 + MagnifierHoldMs);

        /// <summary>A Magnifier While Held macro is held.</summary>
        public bool MagnifierHeld => Environment.TickCount64 < Interlocked.Read(ref _magnifierHeldUntil);
    }
}
