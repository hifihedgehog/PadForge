using System;
using System.Runtime.InteropServices;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual controller that translates KbmRawState into keyboard and mouse
    /// input via the Windows SendInput API. Always available (no driver required).
    ///
    /// Mapping targets are configured in the mapping page and stored in PadSetting
    /// as KBM dictionary entries. Step 3 maps physical inputs to KbmRawState,
    /// and this controller sends the appropriate key presses and mouse actions.
    /// </summary>
    internal sealed class KeyboardMouseVirtualController : IVirtualController
    {
        private bool _connected;
        private bool _disposed;
        private readonly int _padIndex;

        // Change detection: previous key states (4 × 64 bits = 256 VK codes)
        private ulong _prevKeys0, _prevKeys1, _prevKeys2, _prevKeys3;
        // Previous mouse button state
        private byte _prevMouseButtons;

        // Sub-pixel accumulators for mouse delta + scroll. Sources like
        // gyro-to-mouse produce per-frame deltas well under 1 pixel that
        // would truncate to 0 in a raw (int) cast and never move the
        // cursor. Accumulate the fractional residue across frames so the
        // cursor moves once the residue crosses an integer boundary.
        // Same pattern as the macro-action mouse-move path at
        // InputManager.Step4b.EvaluateMacros.cs:814-816.
        private float _mxAccumulator;
        private float _myAccumulator;
        private float _scrollAccumulator;
        private float _scrollAccumulatorH;

        // Mouse sensitivity: pixels per frame at full axis deflection.
        private const float MouseSensitivity = 15.0f;

        // Scroll sensitivity: lines per frame at full axis deflection.
        private const float ScrollSensitivity = 3.0f;

        // SOCD cleaner (discussion #205, Snap Tap). Transforms the logical
        // key bitset before change detection; no-op while mode is Off.
        private readonly SocdCleaner _socd = new();
        // Applied config references for the per-poll fast path: the UI thread
        // swaps whole strings on the slot config (reference writes are
        // atomic), so two reference compares detect an edit.
        private string _appliedSocdMode;
        private string _appliedSocdPairs;

        public VirtualControllerType Type => VirtualControllerType.KeyboardMouse;
        public bool IsConnected => _connected;
        public int FeedbackPadIndex { get; set; }

        public KeyboardMouseVirtualController(int padIndex)
        {
            _padIndex = padIndex;
        }

        public void Connect()
        {
            if (_connected) return;
            _connected = true;
            _prevKeys0 = _prevKeys1 = _prevKeys2 = _prevKeys3 = 0;
            _prevMouseButtons = 0;
            _socd.Reset();
        }

        /// <summary>
        /// Applies the slot's SOCD config. Called from the poll loop before
        /// each submit; reference-compare fast path keeps the steady-state
        /// cost at two compares.
        /// </summary>
        public void ApplySocdConfig(string mode, string pairs)
        {
            if (ReferenceEquals(mode, _appliedSocdMode)
                && ReferenceEquals(pairs, _appliedSocdPairs))
                return;
            _appliedSocdMode = mode;
            _appliedSocdPairs = pairs;
            _socd.Configure(mode, pairs);
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;
            ReleaseAll();
            _mxAccumulator = 0f;
            _myAccumulator = 0f;
            _scrollAccumulator = 0f;
            _scrollAccumulatorH = 0f;
        }

        /// <summary>
        /// No-op — KBM uses SubmitKbmState instead.
        /// </summary>
        public void SubmitGamepadState(Gamepad gp) { }

        /// <summary>
        /// Sends keyboard and mouse input based on the KBM raw state.
        /// Uses change detection to only send key down/up on transitions.
        /// </summary>
        public void SubmitKbmState(KbmRawState raw)
        {
            if (!_connected) return;

            // --- SOCD cleaning (discussion #205) ---
            // Transforms the logical bitset before change detection AND
            // before the _prevKeys assignment, so suppressing a loser emits
            // its key-up and a later release of the winner lets the still-
            // held loser's bit through again as a fresh key-down (Snap Tap
            // re-press, hitboxer's OPPOSITE mode).
            _socd.Apply(ref raw.Keys0, ref raw.Keys1, ref raw.Keys2, ref raw.Keys3);

            // --- Keyboard keys (change detection per VK) ---
            // Releases across ALL four words before any press: the
            // release-before-press discipline must be global, not
            // per-word, or a cross-word SOCD pair (arrow + letter) still
            // exposed both directions held between the two SendInput
            // calls when the winner's word processed first.
            ProcessKeyWord(raw.Keys0, _prevKeys0, 0, releases: true);
            ProcessKeyWord(raw.Keys1, _prevKeys1, 64, releases: true);
            ProcessKeyWord(raw.Keys2, _prevKeys2, 128, releases: true);
            ProcessKeyWord(raw.Keys3, _prevKeys3, 192, releases: true);
            ProcessKeyWord(raw.Keys0, _prevKeys0, 0, releases: false);
            ProcessKeyWord(raw.Keys1, _prevKeys1, 64, releases: false);
            ProcessKeyWord(raw.Keys2, _prevKeys2, 128, releases: false);
            ProcessKeyWord(raw.Keys3, _prevKeys3, 192, releases: false);
            _prevKeys0 = raw.Keys0;
            _prevKeys1 = raw.Keys1;
            _prevKeys2 = raw.Keys2;
            _prevKeys3 = raw.Keys3;

            // --- Mouse buttons (change detection) ---
            ProcessMouseButtons(raw.MouseButtons);
            _prevMouseButtons = raw.MouseButtons;

            // --- Mouse movement (deadzone already applied in Step 3) ---
            // Routed through the injector accumulator, never a SendInput on
            // this (the poll) thread: injected mouse movement traverses
            // every process's low-level mouse hook chain synchronously, and
            // a stick at full deflection crosses the accumulator nearly
            // every tick. The macro mouse path measured this exact
            // per-poll-SendInput mechanism collapsing the loop to ~200 Hz
            // and grew the injector thread; this lane rides the same one.
            if (raw.MouseDeltaX != 0 || raw.MouseDeltaY != 0)
            {
                _mxAccumulator += raw.MouseDeltaX / 32767.0f * MouseSensitivity;
                _myAccumulator += -(raw.MouseDeltaY / 32767.0f * MouseSensitivity);
                int dx = (int)_mxAccumulator;
                int dy = (int)_myAccumulator;
                _mxAccumulator -= dx;
                _myAccumulator -= dy;
                if (dx != 0 || dy != 0)
                    InputManager.AccumulateMouseMoveInput(dx, dy);
            }

            // --- Flick stick exact counts (#225) ---
            // Same injector lane, forwarded 1:1. NOT scaled by
            // MouseSensitivity and NOT run through the accumulator: the
            // engine tick already calibrated the value in mouse counts
            // (counts-per-360 on the source) and carries its own sub-count
            // residual. Scaling here would break the flick = exact camera
            // angle contract.
            if (raw.MouseFlickX != 0)
                InputManager.AccumulateMouseMoveInput(raw.MouseFlickX, 0);

            // --- Absolute pointer (Wii IR pointing, issue #146) ---
            // Touchmote idiom: position the OS cursor directly at the aim point
            // (Touchmote MouseSimulator.cs:154, SetCursorPos), mapped over the
            // primary screen. Only while the camera tracks the sensor bar; on
            // sight loss the cursor holds its last position instead of snapping,
            // exactly like Touchmote when the bar leaves the camera's view.
            if (raw.MouseAbsValid
                && PadForge.Services.CursorControlService.TryGetPrimarySize(out int absW, out int absH))
            {
                int px = (int)MathF.Round((raw.MouseAbsX * 0.5f + 0.5f) * (absW - 1));
                int py = (int)MathF.Round((raw.MouseAbsY * 0.5f + 0.5f) * (absH - 1));
                // Mixed mapping (IR on one mouse axis, a stick on the other):
                // only one coordinate is absolute-driven. The un-driven one
                // keeps the cursor's current position instead of recentering
                // to the 0f field default every poll, which pinned the
                // stick-driven axis. Producers that predate the per-axis
                // flags leave both false and take the both-axes path.
                if (raw.MouseAbsXValid != raw.MouseAbsYValid && GetCursorPos(out POINT cur))
                {
                    if (!raw.MouseAbsXValid) px = cur.X;
                    if (!raw.MouseAbsYValid) py = cur.Y;
                }
                SetCursorPos(Math.Clamp(px, 0, absW - 1), Math.Clamp(py, 0, absH - 1));
            }

            // --- Mouse scroll (deadzone already applied in Step 3) ---
            if (raw.ScrollDelta != 0)
            {
                _scrollAccumulator += raw.ScrollDelta / 32767.0f * ScrollSensitivity;
                int scroll = (int)_scrollAccumulator;
                _scrollAccumulator -= scroll;
                if (scroll != 0)
                    InputManager.AccumulateMouseScrollInput(scroll * 120); // 120 = WHEEL_DELTA
            }

            // --- Horizontal mouse scroll (issue #154, the office-mouse tilt
            //     wheel). Same accumulator idiom; positive = scroll right,
            //     matching MOUSEEVENTF_HWHEEL's positive direction. ---
            if (raw.ScrollDeltaH != 0)
            {
                _scrollAccumulatorH += raw.ScrollDeltaH / 32767.0f * ScrollSensitivity;
                int scrollH = (int)_scrollAccumulatorH;
                _scrollAccumulatorH -= scrollH;
                if (scrollH != 0)
                    InputManager.AccumulateMouseScrollHInput(scrollH * 120); // 120 = WHEEL_DELTA
            }
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // Keyboard/Mouse has no rumble feedback — no-op.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ─────────────────────────────────────────────
        //  Key/mouse state processing
        // ─────────────────────────────────────────────

        /// <summary>One phase of the frame's key flush. Callers run a
        /// full release pass over all four VK words, then a full press
        /// pass, hitboxer's structural discipline (windows.jai
        /// low_level_keyboard_proc: the loser's KEYUP is injected before
        /// the winner's keydown propagates). Without global two-phase
        /// ordering, a SOCD swap emitted the key-down first whenever the
        /// winner's VK bit or word sorted lower, and a game sampling
        /// between the two SendInput calls saw both opposite directions
        /// held, the exact state SOCD cleaning exists to make
        /// unobservable.</summary>
        private void ProcessKeyWord(ulong current, ulong previous, int baseVk, bool releases)
        {
            ulong changed = current ^ previous;
            if (changed == 0) return;

            ulong phaseBits = releases ? (changed & ~current) : (changed & current);
            for (int bit = 0; bit < 64; bit++)
            {
                if ((phaseBits & (1UL << bit)) == 0) continue;
                SendKeyboard((ushort)(baseVk + bit), !releases);
            }
        }

        private void ProcessMouseButtons(byte current)
        {
            byte changed = (byte)(current ^ _prevMouseButtons);
            if (changed == 0) return;

            // Bit 0 = LMB
            if ((changed & 1) != 0)
                SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, (current & 1) != 0);
            // Bit 1 = RMB
            if ((changed & 2) != 0)
                SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, (current & 2) != 0);
            // Bit 2 = MMB
            if ((changed & 4) != 0)
                SendMouseButton(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, (current & 4) != 0);
            // Bit 3 = X1
            if ((changed & 8) != 0)
                SendMouseButtonX(XBUTTON1, (current & 8) != 0);
            // Bit 4 = X2
            if ((changed & 16) != 0)
                SendMouseButtonX(XBUTTON2, (current & 16) != 0);
        }

        private void ReleaseAll()
        {
            // Release all held keys (current = 0, so only the release
            // phase has work).
            ProcessKeyWord(0, _prevKeys0, 0, releases: true);
            ProcessKeyWord(0, _prevKeys1, 64, releases: true);
            ProcessKeyWord(0, _prevKeys2, 128, releases: true);
            ProcessKeyWord(0, _prevKeys3, 192, releases: true);
            _prevKeys0 = _prevKeys1 = _prevKeys2 = _prevKeys3 = 0;

            // Release all mouse buttons
            ProcessMouseButtons(0);
            _prevMouseButtons = 0;
        }

        // ─────────────────────────────────────────────
        //  SendInput P/Invoke
        //
        //  The INPUT struct uses a union (MOUSEINPUT / KEYBDINPUT)
        //  that contains IntPtr (ULONG_PTR). On x64, IntPtr is 8 bytes
        //  with 8-byte alignment, so the union must start at offset 8
        //  (after DWORD type + 4 bytes padding). Using LayoutKind.Sequential
        //  with a separate Explicit union struct lets the CLR handle
        //  platform-correct alignment automatically.
        // ─────────────────────────────────────────────

        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;

        private const uint XBUTTON1 = 0x0001;
        private const uint XBUTTON2 = 0x0002;

        private const uint KEYEVENTF_KEYUP = 0x0002;

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

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // Absolute cursor positioning for the Wii IR pointer (issue #146),
        // the same call Touchmote's MouseSimulator uses (SetCursorPosition).
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // Current cursor position, read when a mixed mapping drives only
        // one absolute axis so the other coordinate passes through
        // unchanged (the delta-driven axis keeps its integrated position).
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        private static void SendKeyboard(ushort vk, bool down)
        {
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)MapVirtualKeyW(vk, 0),
                        dwFlags = down ? 0u : KEYEVENTF_KEYUP
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void SendMouseButton(uint downFlag, uint upFlag, bool down)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT { dwFlags = down ? downFlag : upFlag }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void SendMouseButtonX(uint xButton, bool down)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
                        mouseData = xButton
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

    }
}
