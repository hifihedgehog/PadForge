using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Manages WH_KEYBOARD_LL and WH_MOUSE_LL low-level hooks for suppressing
    /// mapped inputs from keyboards and mice. Only suppresses inputs that are
    /// in the active suppression sets — non-mapped keys/buttons pass through normally.
    ///
    /// Hooks require a thread with a message pump. This class creates its own
    /// dedicated thread with a GetMessage loop.
    /// </summary>
    public class InputHookManager : IDisposable
    {
        // ─────────────────────────────────────────────
        //  P/Invoke
        // ─────────────────────────────────────────────

        private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int HC_ACTION = 0;
        private const uint WM_QUIT = 0x0012;

        // Keyboard messages
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        // Mouse messages
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int pt_x;
            public int pt_y;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ─────────────────────────────────────────────
        //  Fields
        // ─────────────────────────────────────────────

        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private Thread _hookThread;
        private uint _hookThreadId;
        private volatile bool _running;
        private bool _disposed;

        // Keep delegates alive to prevent GC collection.
        private LowLevelHookProc _keyboardProc;
        private LowLevelHookProc _mouseProc;

        // Suppression sets — volatile reference swap for thread safety.
        private volatile HashSet<int> _suppressedVKeys = new();
        private volatile HashSet<int> _suppressedMouseButtons = new();

        // ─────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────

        /// <summary>
        /// Installs the low-level keyboard and mouse hooks on a dedicated message pump thread.
        /// </summary>
        public void Start()
        {
            if (_running) return;
            _running = true;

            var ready = new ManualResetEventSlim();

            _hookThread = new Thread(() => HookThreadProc(ready))
            {
                Name = "InputHookManager",
                IsBackground = true
            };
            _hookThread.Start();

            // Wait for hooks to be installed before returning.
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Removes the hooks and stops the message pump thread.
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            // Post WM_QUIT to the hook thread's message loop.
            if (_hookThreadId != 0)
                PostThreadMessageW(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

            _hookThread?.Join(TimeSpan.FromSeconds(2));
            _hookThread = null;
            _hookThreadId = 0;
        }

        /// <summary>
        /// Updates the set of virtual key codes to suppress from keyboard hooks.
        /// Pass an empty set to stop suppressing keyboard input.
        /// </summary>
        public void SetSuppressedKeys(HashSet<int> vkCodes)
        {
            _suppressedVKeys = vkCodes ?? new HashSet<int>();
        }

        /// <summary>
        /// Updates the set of mouse button identifiers to suppress.
        /// Button IDs: 0=Left, 1=Right, 2=Middle, 3=XButton1, 4=XButton2.
        /// Pass an empty set to stop suppressing mouse input.
        /// </summary>
        public void SetSuppressedMouseButtons(HashSet<int> buttons)
        {
            _suppressedMouseButtons = buttons ?? new HashSet<int>();
        }

        /// <summary>
        /// Returns true if any keys or mouse buttons are being suppressed.
        /// </summary>
        public bool HasAnySuppression =>
            _suppressedVKeys.Count > 0 || _suppressedMouseButtons.Count > 0;

        // ─────────────────────────────────────────────
        //  Hook thread
        // ─────────────────────────────────────────────

        private void HookThreadProc(ManualResetEventSlim ready)
        {
            _hookThreadId = GetCurrentThreadId();

            IntPtr hModule = GetModuleHandleW(null);

            // Must keep delegate references alive.
            _keyboardProc = KeyboardHookCallback;
            _mouseProc = MouseHookCallback;

            _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, hModule, 0);
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, hModule, 0);

            ready.Set();

            // Run message pump until WM_QUIT.
            while (GetMessageW(out _, IntPtr.Zero, 0, 0))
            {
                // No dispatch needed — hooks don't require it.
            }

            // Clean up hooks.
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        // ─────────────────────────────────────────────
        //  Hook callbacks
        // ─────────────────────────────────────────────

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HC_ACTION)
            {
                int msg = (int)wParam;
                if (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (_suppressedVKeys.Contains((int)kb.vkCode))
                        return (IntPtr)1; // Suppress
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HC_ACTION)
            {
                int msg = (int)wParam;
                int buttonId = MouseMessageToButtonId(msg, lParam);
                if (buttonId >= 0 && _suppressedMouseButtons.Contains(buttonId))
                    return (IntPtr)1; // Suppress
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// Maps a mouse message to a button ID.
        /// Returns -1 for non-button messages (mouse move, wheel).
        /// </summary>
        private static int MouseMessageToButtonId(int msg, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                    return 0;
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                    return 1;
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                    return 2;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int xButton = (int)(ms.mouseData >> 16);
                    return xButton == 1 ? 3 : xButton == 2 ? 4 : -1;
                default:
                    return -1;
            }
        }

        // ─────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
