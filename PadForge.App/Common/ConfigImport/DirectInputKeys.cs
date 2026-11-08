using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Common.ConfigImport
{
    /// <summary>A reWASD keyboard <c>buttonId</c> is a DirectInput scan code
    /// (DIK_A = 30), the key's place on the keyboard. PadForge's keyboard rows
    /// and macros speak Windows virtual keys, and which virtual key a place
    /// types is the layout's to say: the bottom-left letter is Z on a US
    /// keyboard and Y on a German one. So a key that types a character takes
    /// its virtual key from the PC's active layout, Windows' own scan code
    /// table for it (kbd.h). The table below is the US layout, from the
    /// published dinput.h and winuser.h constants, and gives the rest: the
    /// modifiers sided (VK_LSHIFT, not VK_SHIFT) as the keyboard row engine
    /// and the Raw Input keyboard read both use them, the numpad with its
    /// own codes, and every E0 key.</summary>
    internal static class DirectInputKeys
    {
        private static readonly Dictionary<int, byte> Table = new()
        {
            [0x01] = 0x1B, // ESCAPE
            [0x02] = 0x31, [0x03] = 0x32, [0x04] = 0x33, [0x05] = 0x34, [0x06] = 0x35,
            [0x07] = 0x36, [0x08] = 0x37, [0x09] = 0x38, [0x0A] = 0x39, [0x0B] = 0x30,
            [0x0C] = 0xBD, // MINUS
            [0x0D] = 0xBB, // EQUALS
            [0x0E] = 0x08, // BACK
            [0x0F] = 0x09, // TAB
            [0x10] = 0x51, [0x11] = 0x57, [0x12] = 0x45, [0x13] = 0x52, [0x14] = 0x54,
            [0x15] = 0x59, [0x16] = 0x55, [0x17] = 0x49, [0x18] = 0x4F, [0x19] = 0x50,
            [0x1A] = 0xDB, // LBRACKET
            [0x1B] = 0xDD, // RBRACKET
            [0x1C] = 0x0D, // RETURN
            [0x1D] = 0xA2, // LCONTROL
            [0x1E] = 0x41, [0x1F] = 0x53, [0x20] = 0x44, [0x21] = 0x46, [0x22] = 0x47,
            [0x23] = 0x48, [0x24] = 0x4A, [0x25] = 0x4B, [0x26] = 0x4C,
            [0x27] = 0xBA, // SEMICOLON
            [0x28] = 0xDE, // APOSTROPHE
            [0x29] = 0xC0, // GRAVE
            [0x2A] = 0xA0, // LSHIFT
            [0x2B] = 0xDC, // BACKSLASH
            [0x2C] = 0x5A, [0x2D] = 0x58, [0x2E] = 0x43, [0x2F] = 0x56, [0x30] = 0x42,
            [0x31] = 0x4E, [0x32] = 0x4D,
            [0x33] = 0xBC, // COMMA
            [0x34] = 0xBE, // PERIOD
            [0x35] = 0xBF, // SLASH
            [0x36] = 0xA1, // RSHIFT
            [0x37] = 0x6A, // MULTIPLY
            [0x38] = 0xA4, // LMENU
            [0x39] = 0x20, // SPACE
            [0x3A] = 0x14, // CAPITAL
            [0x3B] = 0x70, [0x3C] = 0x71, [0x3D] = 0x72, [0x3E] = 0x73, [0x3F] = 0x74,
            [0x40] = 0x75, [0x41] = 0x76, [0x42] = 0x77, [0x43] = 0x78, [0x44] = 0x79,
            [0x45] = 0x90, // NUMLOCK
            [0x46] = 0x91, // SCROLL
            [0x47] = 0x67, [0x48] = 0x68, [0x49] = 0x69,
            [0x4A] = 0x6D, // SUBTRACT
            [0x4B] = 0x64, [0x4C] = 0x65, [0x4D] = 0x66,
            [0x4E] = 0x6B, // ADD
            [0x4F] = 0x61, [0x50] = 0x62, [0x51] = 0x63, [0x52] = 0x60,
            [0x53] = 0x6E, // DECIMAL
            [0x56] = 0xE2, // OEM_102
            [0x57] = 0x7A, [0x58] = 0x7B, // F11, F12
            [0x64] = 0x7C, [0x65] = 0x7D, [0x66] = 0x7E, // F13 to F15
            [0x70] = 0x15, // KANA
            [0x79] = 0x1C, // CONVERT
            [0x7B] = 0x1D, // NOCONVERT
            [0x90] = 0xB1, // PREVTRACK
            [0x99] = 0xB0, // NEXTTRACK
            [0x9C] = 0x88, // NUMPADENTER, PadForge's own index (RawInputListener.NumpadEnterKey)
            [0x9D] = 0xA3, // RCONTROL
            [0xA0] = 0xAD, // MUTE
            [0xA1] = 0xB7, // CALCULATOR
            [0xA2] = 0xB3, // PLAYPAUSE
            [0xA4] = 0xB2, // MEDIASTOP
            [0xAE] = 0xAE, // VOLUMEDOWN
            [0xB0] = 0xAF, // VOLUMEUP
            [0xB2] = 0xAC, // WEBHOME
            [0xB5] = 0x6F, // DIVIDE
            [0xB7] = 0x2C, // SYSRQ
            [0xB8] = 0xA5, // RMENU
            [0xC5] = 0x13, // PAUSE
            [0xC7] = 0x24, // HOME
            [0xC8] = 0x26, // UP
            [0xC9] = 0x21, // PRIOR
            [0xCB] = 0x25, // LEFT
            [0xCD] = 0x27, // RIGHT
            [0xCF] = 0x23, // END
            [0xD0] = 0x28, // DOWN
            [0xD1] = 0x22, // NEXT
            [0xD2] = 0x2D, // INSERT
            [0xD3] = 0x2E, // DELETE
            [0xDB] = 0x5B, // LWIN
            [0xDC] = 0x5C, // RWIN
            [0xDD] = 0x5D, // APPS
            [0xDF] = 0x5F, // SLEEP
            [0xE5] = 0xAA, // WEBSEARCH
            [0xE6] = 0xAB, // WEBFAVORITES
            [0xE7] = 0xA8, // WEBREFRESH
            [0xE8] = 0xA9, // WEBSTOP
            [0xE9] = 0xA7, // WEBFORWARD
            [0xEA] = 0xA6, // WEBBACK
            [0xEB] = 0xB6, // MYCOMPUTER
            [0xEC] = 0xB4, // MAIL
            [0xED] = 0xB5, // MEDIASELECT
        };

        /// <summary>The virtual key a scan code types on this PC.</summary>
        public static bool TryGetVirtualKey(int scanCode, out byte vk) => TryGetVirtualKey(scanCode, ActiveLayout, out vk);

        /// <summary>The virtual key a scan code types under
        /// <paramref name="layout"/>, which gives a scan code's virtual key or
        /// 0 for none. A key with no E0 prefix that the table lacks or lists
        /// as a character asks the layout first. Everything else, and a key
        /// the layout has nothing for, reads the table.</summary>
        internal static bool TryGetVirtualKey(int scanCode, Func<int, int> layout, out byte vk)
        {
            bool listed = Table.TryGetValue(scanCode, out vk);
            if (scanCode is > 0 and < 0x80 && (!listed || IsCharacterKey(vk)))
            {
                int fromLayout = layout?.Invoke(scanCode) ?? 0;
                if (fromLayout is > 0 and < 0xFF)
                {
                    vk = (byte)fromLayout;
                    return true;
                }
            }
            return listed;
        }

        /// <summary>Digits, letters and the OEM punctuation keys: the keys a
        /// layout moves.</summary>
        private static bool IsCharacterKey(byte vk)
            => vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

        /// <summary>Set by the test host: the PC's own layout is not read, so
        /// an import there reads every key through the US table, whatever
        /// layout the test machine has.</summary>
        internal static bool OffLimitsForTest;

        private const uint MAPVK_VSC_TO_VK_EX = 3;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        /// <summary>The importing thread's keyboard layout, the one the user
        /// types with on the PC that will play the profile.</summary>
        private static int ActiveLayout(int scanCode)
        {
            if (OffLimitsForTest) return 0;
            return (int)MapVirtualKeyEx((uint)scanCode, MAPVK_VSC_TO_VK_EX, GetKeyboardLayout(0));
        }
    }
}
