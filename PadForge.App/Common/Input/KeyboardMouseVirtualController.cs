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

        // Mouse sensitivity: pixels per frame at full axis deflection.
        private const float MouseSensitivity = 15.0f;

        // Scroll sensitivity: lines per frame at full axis deflection.
        private const float ScrollSensitivity = 3.0f;

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
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;
            ReleaseAll();
            _mxAccumulator = 0f;
            _myAccumulator = 0f;
            _scrollAccumulator = 0f;
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

            // --- Keyboard keys (change detection per VK) ---
            ProcessKeyWord(raw.Keys0, _prevKeys0, 0);
            ProcessKeyWord(raw.Keys1, _prevKeys1, 64);
            ProcessKeyWord(raw.Keys2, _prevKeys2, 128);
            ProcessKeyWord(raw.Keys3, _prevKeys3, 192);
            _prevKeys0 = raw.Keys0;
            _prevKeys1 = raw.Keys1;
            _prevKeys2 = raw.Keys2;
            _prevKeys3 = raw.Keys3;

            // --- Mouse buttons (change detection) ---
            ProcessMouseButtons(raw.MouseButtons);
            _prevMouseButtons = raw.MouseButtons;

            // --- Mouse movement (deadzone already applied in Step 3) ---
            if (raw.MouseDeltaX != 0 || raw.MouseDeltaY != 0)
            {
                _mxAccumulator += raw.MouseDeltaX / 32767.0f * MouseSensitivity;
                _myAccumulator += -(raw.MouseDeltaY / 32767.0f * MouseSensitivity);
                int dx = (int)_mxAccumulator;
                int dy = (int)_myAccumulator;
                _mxAccumulator -= dx;
                _myAccumulator -= dy;
                if (dx != 0 || dy != 0)
                    SendMouseMove(dx, dy);
            }

            // --- Mouse scroll (deadzone already applied in Step 3) ---
            if (raw.ScrollDelta != 0)
            {
                _scrollAccumulator += raw.ScrollDelta / 32767.0f * ScrollSensitivity;
                int scroll = (int)_scrollAccumulator;
                _scrollAccumulator -= scroll;
                if (scroll != 0)
                    SendMouseWheel(scroll * 120); // 120 = WHEEL_DELTA
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

        private void ProcessKeyWord(ulong current, ulong previous, int baseVk)
        {
            ulong changed = current ^ previous;
            if (changed == 0) return;

            for (int bit = 0; bit < 64; bit++)
            {
                ulong mask = 1UL << bit;
                if ((changed & mask) == 0) continue;
                bool pressed = (current & mask) != 0;
                SendKeyboard((ushort)(baseVk + bit), pressed);
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
            // Release all held keys
            ProcessKeyWord(0, _prevKeys0, 0);
            ProcessKeyWord(0, _prevKeys1, 64);
            ProcessKeyWord(0, _prevKeys2, 128);
            ProcessKeyWord(0, _prevKeys3, 192);
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

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

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

        private static void SendMouseMove(int dx, int dy)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        dwFlags = MOUSEEVENTF_MOVE
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

        private static void SendMouseWheel(int delta)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = MOUSEEVENTF_WHEEL,
                        mouseData = (uint)delta
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
    }
}
