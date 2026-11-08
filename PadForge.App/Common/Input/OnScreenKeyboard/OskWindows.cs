using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Common.Input.OnScreenKeyboard
{
    /// <summary>
    /// The Windows keyboard layout of the window being typed into (#566),
    /// read once per layout with <c>ToUnicodeEx</c>. Flag 4 asks Windows not
    /// to change the keyboard state (Windows 10 1607 and later), so reading a
    /// dead key leaves no pending accent behind for the user's next key.
    /// Built on the UI thread, which owns the foreground window question.
    /// The texts never change after the read. Caps Lock is refreshed in place.
    /// </summary>
    public sealed class OskWindowsLayout : IOskLayout
    {
        private const uint MAPVK_VSC_TO_VK_EX = 3;
        private const uint ToUnicodeNoStateChange = 4;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const int VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_RMENU = 0xA5;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        private readonly Dictionary<ushort, OskKeyText> _texts = new();

        public IntPtr Handle { get; }
        public string Name { get; }
        public string LanguageTag { get; }
        public bool HasAltGr { get; }

        private volatile bool _capsOn;
        public bool CapsOn => _capsOn;
        public void SetCaps(bool on) => _capsOn = on;

        public OskWindowsLayout(IntPtr hkl)
        {
            Handle = hkl;
            int langId = (int)((long)hkl & 0xFFFF);
            CultureInfo culture = null;
            try { if (langId != 0) culture = CultureInfo.GetCultureInfo(langId); }
            catch (CultureNotFoundException) { }
            LanguageTag = culture?.Name ?? "en-US";
            Name = culture?.DisplayName ?? LanguageTag;
            var scans = OskLayoutFrame.CharRows.SelectMany(r => r)
                .Concat(new[] { OskLayoutFrame.IsoKey, OskLayoutFrame.JisYen, OskLayoutFrame.JisRo });
            foreach (var scan in scans)
                _texts[scan] = Read(hkl, scan);
            HasAltGr = _texts.Values.Any(t => t.AltGr != null || t.ShiftAltGr != null);
        }

        public OskKeyText Text(ushort scan) => _texts.TryGetValue(scan, out var t) ? t : default;

        private static OskKeyText Read(IntPtr hkl, ushort scan)
        {
            uint vk = MapVirtualKeyExW(scan, MAPVK_VSC_TO_VK_EX, hkl);
            if (vk == 0) return default;
            var (b, db) = Level(hkl, vk, scan, shift: false, altGr: false);
            var (s, ds) = Level(hkl, vk, scan, shift: true, altGr: false);
            var (a, da) = Level(hkl, vk, scan, shift: false, altGr: true);
            var (sa, dsa) = Level(hkl, vk, scan, shift: true, altGr: true);
            return new OskKeyText(b, s, a, sa, db, ds, da, dsa);
        }

        private static (string Text, bool Dead) Level(IntPtr hkl, uint vk, ushort scan, bool shift, bool altGr)
        {
            var state = new byte[256];
            if (shift) { state[VK_SHIFT] = 0x80; state[VK_LSHIFT] = 0x80; }
            if (altGr) { state[VK_CONTROL] = 0x80; state[VK_LCONTROL] = 0x80; state[VK_MENU] = 0x80; state[VK_RMENU] = 0x80; }
            var buf = new StringBuilder(8);
            int n = ToUnicodeEx(vk, scan, state, buf, buf.Capacity, ToUnicodeNoStateChange, hkl);
            if (n == 0) return (null, false);
            bool dead = n < 0;
            string text = dead ? (buf.Length > 0 ? buf.ToString(0, 1) : null) : buf.ToString(0, Math.Min(n, buf.Length));
            if (string.IsNullOrEmpty(text) || text.Any(c => c < 0x20 || c == 0x7F)) return (null, false);
            return (text, dead);
        }
    }

    /// <summary>The foreground window's keyboard layout and Windows' key
    /// repeat settings.</summary>
    public static class OskWindowsKeyboard
    {
        private const uint SPI_GETKEYBOARDSPEED = 0x000A;
        private const uint SPI_GETKEYBOARDDELAY = 0x0016;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);

        /// <summary>The keyboard layout of the thread that owns the foreground
        /// window, which is where the keys land.</summary>
        public static IntPtr ForegroundLayout()
        {
            IntPtr hwnd = GetForegroundWindow();
            uint tid = hwnd == IntPtr.Zero ? 0 : GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            return GetKeyboardLayout(tid);
        }

        /// <summary>Windows' repeat delay (0 to 3, a quarter second each from
        /// 250 ms) and repeat rate (0 to 31, about 2.5 to 30 a second).</summary>
        public static (int DelayMs, int IntervalMs) RepeatTiming()
        {
            int delay = 500, interval = 33;
            if (SystemParametersInfo(SPI_GETKEYBOARDDELAY, 0, out uint d, 0))
                delay = (int)(Math.Min(d, 3) + 1) * 250;
            if (SystemParametersInfo(SPI_GETKEYBOARDSPEED, 0, out uint s, 0))
            {
                double perSecond = 2.5 + Math.Min(s, 31) * (30.0 - 2.5) / 31.0;
                interval = (int)Math.Round(1000.0 / perSecond);
            }
            return (delay, interval);
        }
    }

    /// <summary>
    /// Sends the keyboard's keys to Windows (#566). A key goes out as a
    /// physical key does: the scan code of its place and the virtual key the
    /// foreground window's layout gives that place, with the extended flag
    /// for an E0 key. Every event carries <c>InputHookManager.OutputTag</c>,
    /// so PadForge's own hooks and Raw Input reader pass it by, as they pass
    /// the mapped keys. Text the layout cannot type goes out as Unicode, the
    /// Text Block's way.
    /// </summary>
    public sealed class OskWindowsOutput : IOskOutput
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint MAPVK_VSC_TO_VK_EX = 3;
        private const ushort VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B;
        private const ushort VK_RETURN = 0x0D, VK_TAB = 0x09;

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")]
        private static extern uint SendInputNative(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>The one way the keyboard injects a key, as the macros'
        /// wrapper does. The test host injects none
        /// (InputHookManager.DesktopInputOffLimits) and is answered no event
        /// sent, as when the input is blocked.</summary>
        private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize)
            => PadForge.Engine.Common.InputHookManager.DesktopInputOffLimits ? 0 : SendInputNative(nInputs, pInputs, cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // All three members, so the union is as large as Win32's and
        // Marshal.SizeOf<INPUT>() matches what SendInput expects.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private readonly Func<IntPtr> _layout;
        private readonly Func<bool> _allowed;

        /// <param name="layout">The foreground layout, for the virtual keys.</param>
        /// <param name="allowed">False while typing must not happen (a slot fed
        /// by a restricted Remote Link peer).</param>
        public OskWindowsOutput(Func<IntPtr> layout, Func<bool> allowed)
        {
            _layout = layout;
            _allowed = allowed;
        }

        private static INPUT Key(ushort vk, ushort scan, bool extended, bool up) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0),
                    dwExtraInfo = PadForge.Engine.Common.InputHookManager.OutputTag,
                },
            },
        };

        /// <summary>The events of a tap: modifiers down, the key down and up,
        /// the modifiers up in reverse. AltGr is left Ctrl with right Alt,
        /// what Windows turns into AltGr.</summary>
        internal static List<(ushort Vk, ushort Scan, bool Extended, bool Up)> TapEvents(ushort vk, ushort scan, OskMods mods)
        {
            var mod = new List<(ushort Vk, ushort Scan, bool Ext)>();
            if ((mods & (OskMods.Ctrl | OskMods.AltGr)) != 0) mod.Add((VK_LCONTROL, 0x1D, false));
            if ((mods & OskMods.Alt) != 0) mod.Add((VK_LMENU, 0x38, false));
            if ((mods & OskMods.AltGr) != 0) mod.Add((VK_RMENU, 0x38, true));
            if ((mods & OskMods.Win) != 0) mod.Add((VK_LWIN, 0x5B, true));
            if ((mods & OskMods.Shift) != 0) mod.Add((VK_LSHIFT, 0x2A, false));
            bool ext = (scan & OskLayoutFrame.Ext) != 0;
            ushort sc = (ushort)(scan & 0xFF);
            var events = new List<(ushort, ushort, bool, bool)>();
            foreach (var m in mod) events.Add((m.Vk, m.Scan, m.Ext, false));
            events.Add((vk, sc, ext, false));
            events.Add((vk, sc, ext, true));
            for (int i = mod.Count - 1; i >= 0; i--) events.Add((mod[i].Vk, mod[i].Scan, mod[i].Ext, true));
            return events;
        }

        /// <summary>The virtual key a layout gives a place, by its scan code
        /// with 0xE000 for an extended key: the key Windows itself reports for
        /// that physical key. 0 when the layout gives it none.</summary>
        internal static ushort VirtualKeyOf(ushort scan, IntPtr hkl)
        {
            uint code = (scan & OskLayoutFrame.Ext) != 0 ? 0xE000u | (uint)(scan & 0xFF) : scan;
            return (ushort)MapVirtualKeyExW(code, MAPVK_VSC_TO_VK_EX, hkl);
        }

        public void TapKey(ushort scan, OskMods mods)
        {
            if (!_allowed()) return;
            ushort vk = VirtualKeyOf(scan, _layout());
            if (vk == 0) return;
            var events = TapEvents(vk, scan, mods);
            var inputs = new INPUT[events.Count];
            for (int i = 0; i < events.Count; i++)
                inputs[i] = Key(events[i].Vk, events[i].Scan, events[i].Extended, events[i].Up);
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text) || !_allowed()) return;
            var inputs = new List<INPUT>(text.Length * 2);
            foreach (char c in text)
            {
                if (c == '\n' || c == '\t')
                {
                    ushort vk = c == '\n' ? VK_RETURN : VK_TAB;
                    ushort sc = c == '\n' ? (ushort)0x1C : (ushort)0x0F;
                    inputs.Add(Key(vk, sc, false, false));
                    inputs.Add(Key(vk, sc, false, true));
                    continue;
                }
                var down = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = PadForge.Engine.Common.InputHookManager.OutputTag } },
                };
                var up = down;
                up.u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                inputs.Add(down);
                inputs.Add(up);
            }
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }
}
