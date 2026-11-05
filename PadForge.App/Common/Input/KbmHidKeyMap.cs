using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Turns a Keyboard + Mouse slot's key index (its Windows virtual-key code,
    /// with Numpad Enter at <see cref="PadForge.Engine.Common.RawInputListener.NumpadEnterKey"/>)
    /// into what a virtual HID keyboard sends for it: a Keyboard/Keypad page
    /// usage, or a Consumer page usage for the media, browser and launch keys.
    ///
    /// <para>The slot's software path sends the virtual-key code with the scan
    /// code MapVirtualKey gives it (KeyboardMouseVirtualController.KeyEvent).
    /// A HID keyboard sends a usage, which Windows turns into a scan code and
    /// then into a virtual-key code through the foreground window's layout.
    /// So a character key goes through that same layout here: its virtual-key
    /// code becomes the scan code the layout types it from, and the scan code
    /// becomes the usage HID class sends that scan code for. "A" on a French
    /// AZERTY layout then presses the key that types "a" there, as the software
    /// path does, not the key in the US "A" position. The scan code to usage
    /// table is SDL's windows_scancode_table (src/events/scancodes_windows.h),
    /// whose SDL_Scancode values are the HID usage IDs.</para>
    ///
    /// <para>Keys whose meaning no layout changes (function keys, navigation,
    /// modifiers, the number pad, Enter, Escape) use a fixed table, the
    /// pairing DS4Windows' FakerInputMapping makes for its virtual HID
    /// keyboard. The media keys go to the Consumer page usages Windows reads
    /// from a consumer control collection, the ones PadForge's own
    /// ConsumerUsageTable reads back from real devices.</para>
    /// </summary>
    internal static class KbmHidKeyMap
    {
        /// <summary>A usage on one of the two pages, or none.</summary>
        internal readonly struct HidKey
        {
            public readonly ushort Page;
            public readonly ushort Usage;
            public HidKey(ushort page, ushort usage) { Page = page; Usage = usage; }
            public bool IsNone => Page == 0;
            public bool IsKeyboard => Page == 0x07;
            public bool IsConsumer => Page == 0x0C;
            public bool IsModifier => Page == 0x07 && Usage >= 0xE0 && Usage <= 0xE7;
            public override string ToString() => IsNone ? "none" : $"{Page:X2}:{Usage:X2}";
        }

        internal static readonly HidKey None = default;
        private static HidKey K(int usage) => new HidKey(0x07, (ushort)usage);
        private static HidKey C(int usage) => new HidKey(0x0C, (ushort)usage);

        /// <summary>Keys whose usage no layout changes, by key index.</summary>
        private static readonly HidKey[] s_fixed = BuildFixed();

        /// <summary>Usages of the character keys in the US layout's positions,
        /// used where the foreground layout has no key for the code and to
        /// answer whether a device can send the key at all (every keyboard
        /// profile reaches the whole main block or none of it).</summary>
        private static readonly HidKey[] s_usPositional = BuildUsPositional();

        private static HidKey[] BuildFixed()
        {
            var t = new HidKey[256];
            t[0x08] = K(0x2A);   // Backspace
            t[0x09] = K(0x2B);   // Tab
            t[0x0D] = K(0x28);   // Enter
            t[0x10] = K(0xE1);   // Shift, as the left one (FakerInputMapping)
            t[0x11] = K(0xE0);   // Control, as the left one
            t[0x12] = K(0xE2);   // Alt, as the left one
            t[0x13] = K(0x48);   // Pause
            t[0x14] = K(0x39);   // Caps Lock
            t[0x15] = K(0x88);   // Kana (JIS), International2, scan 0x70
            t[0x1B] = K(0x29);   // Escape
            t[0x1C] = K(0x8A);   // Convert (JIS Henkan), International4, scan 0x79
            t[0x1D] = K(0x8B);   // Nonconvert (JIS Muhenkan), International5, scan 0x7B
            t[0x20] = K(0x2C);   // Space
            t[0x21] = K(0x4B);   // Page Up
            t[0x22] = K(0x4E);   // Page Down
            t[0x23] = K(0x4D);   // End
            t[0x24] = K(0x4A);   // Home
            t[0x25] = K(0x50);   // Left
            t[0x26] = K(0x52);   // Up
            t[0x27] = K(0x4F);   // Right
            t[0x28] = K(0x51);   // Down
            t[0x2C] = K(0x46);   // Print Screen
            t[0x2D] = K(0x49);   // Insert
            t[0x2E] = K(0x4C);   // Delete
            t[0x5B] = K(0xE3);   // Left Windows
            t[0x5C] = K(0xE7);   // Right Windows
            t[0x5D] = K(0x65);   // Application (menu)
            t[0x60] = K(0x62);   // Keypad 0
            for (int i = 1; i <= 9; i++) t[0x60 + i] = K(0x58 + i);   // Keypad 1-9 = 0x59-0x61
            t[0x6A] = K(0x55);   // Keypad *
            t[0x6B] = K(0x57);   // Keypad +
            t[0x6D] = K(0x56);   // Keypad -
            t[0x6E] = K(0x63);   // Keypad .
            t[0x6F] = K(0x54);   // Keypad /
            for (int i = 0; i < 12; i++) t[0x70 + i] = K(0x3A + i);   // F1-F12
            for (int i = 0; i < 12; i++) t[0x7C + i] = K(0x68 + i);   // F13-F24
            t[0x88] = K(0x58);   // Numpad Enter (PadForge's index, RawInputListener.NumpadEnterKey)
            t[0x90] = K(0x53);   // Num Lock
            t[0x91] = K(0x47);   // Scroll Lock
            t[0xA0] = K(0xE1);   // Left Shift
            t[0xA1] = K(0xE5);   // Right Shift
            t[0xA2] = K(0xE0);   // Left Control
            t[0xA3] = K(0xE4);   // Right Control
            t[0xA4] = K(0xE2);   // Left Alt
            t[0xA5] = K(0xE6);   // Right Alt
            // Hankaku/Zenkaku sits where the Grave key does, scan 0x29, which
            // SDL's windows_scancode_table reads as SDL_SCANCODE_GRAVE.
            t[0xF3] = K(0x35);

            // Browser, media and launch keys: Consumer page (HID Usage
            // Tables 1.5, section 15), the usages ConsumerUsageTable names.
            t[0xA6] = C(0x224);  // Browser Back (AC Back)
            t[0xA7] = C(0x225);  // Browser Forward (AC Forward)
            t[0xA8] = C(0x227);  // Browser Refresh (AC Refresh)
            t[0xA9] = C(0x226);  // Browser Stop (AC Stop)
            t[0xAA] = C(0x221);  // Browser Search (AC Search)
            t[0xAB] = C(0x22A);  // Browser Favorites (AC Bookmarks)
            t[0xAC] = C(0x223);  // Browser Home (AC Home)
            t[0xAD] = C(0xE2);   // Mute
            t[0xAE] = C(0xEA);   // Volume Down (Volume Decrement)
            t[0xAF] = C(0xE9);   // Volume Up (Volume Increment)
            t[0xB0] = C(0xB5);   // Next Track (Scan Next Track)
            t[0xB1] = C(0xB6);   // Previous Track (Scan Previous Track)
            t[0xB2] = C(0xB7);   // Stop
            t[0xB3] = C(0xCD);   // Play/Pause
            t[0xB4] = C(0x18A);  // Mail (AL Email Reader)
            t[0xB5] = C(0x183);  // Media Select (AL Consumer Control Configuration)
            t[0xB6] = C(0x194);  // Launch App 1 (AL Local Machine Browser)
            t[0xB7] = C(0x192);  // Launch App 2 (AL Calculator)
            return t;
        }

        private static HidKey[] BuildUsPositional()
        {
            var t = new HidKey[256];
            for (int i = 0; i < 26; i++) t[0x41 + i] = K(0x04 + i);    // A-Z
            for (int i = 1; i <= 9; i++) t[0x30 + i] = K(0x1D + i);    // 1-9 = 0x1E-0x26
            t[0x30] = K(0x27);                                          // 0
            t[0xBA] = K(0x33);   // ;
            t[0xBB] = K(0x2E);   // =
            t[0xBC] = K(0x36);   // ,
            t[0xBD] = K(0x2D);   // -
            t[0xBE] = K(0x37);   // .
            t[0xBF] = K(0x38);   // /
            t[0xC0] = K(0x35);   // `
            t[0xDB] = K(0x2F);   // [
            t[0xDC] = K(0x31);   // \
            t[0xDD] = K(0x30);   // ]
            t[0xDE] = K(0x34);   // '
            t[0xE2] = K(0x64);   // the ISO key between left Shift and Z (Non-US \ |)
            return t;
        }

        /// <summary>True for a key whose usage follows the keyboard
        /// layout.</summary>
        internal static bool IsCharacterKey(int index) =>
            index >= 0 && index < 256 && !s_usPositional[index].IsNone;

        /// <summary>The usage that decides whether a device can send the key,
        /// independent of any layout. None for an index no row offers.</summary>
        internal static HidKey Nominal(int index)
        {
            if (index < 0 || index > 255) return None;
            var f = s_fixed[index];
            return !f.IsNone ? f : s_usPositional[index];
        }

        /// <summary>The usage to send for <paramref name="index"/> on the
        /// foreground layout, read now. Call it once per press and keep the
        /// answer for the release, so a layout switch while the key is down
        /// cannot strand it.</summary>
        internal static HidKey Resolve(int index) => Resolve(index, ForegroundLayout());

        /// <summary><see cref="Resolve(int)"/> on a given layout handle (0 is
        /// the caller's own layout), so a test can pin one.</summary>
        internal static HidKey Resolve(int index, IntPtr layout)
        {
            if (index < 0 || index > 255) return None;
            var f = s_fixed[index];
            if (!f.IsNone) return f;
            var us = s_usPositional[index];
            if (us.IsNone) return None;
            uint scan = LayoutScanCode((uint)index, layout);
            if (scan != 0)
            {
                var fromScan = UsageOfScanCode(scan);
                if (!fromScan.IsNone) return fromScan;
            }
            return us;
        }

        /// <summary>The usage HID class sends scan code <paramref name="scan"/>
        /// for, with the extended form in the high byte as MapVirtualKeyEx's
        /// MAPVK_VK_TO_VSC_EX returns it (0xE0nn). SDL's
        /// windows_scancode_table, the rows the slot's keys can reach.</summary>
        internal static HidKey UsageOfScanCode(uint scan)
        {
            bool e0 = (scan & 0xFF00) == 0xE000;
            uint code = scan & 0xFF;
            if (e0) return None;   // no character key is an E0 key
            return code switch
            {
                0x02 => K(0x1E), 0x03 => K(0x1F), 0x04 => K(0x20), 0x05 => K(0x21), 0x06 => K(0x22),
                0x07 => K(0x23), 0x08 => K(0x24), 0x09 => K(0x25), 0x0A => K(0x26), 0x0B => K(0x27),
                0x0C => K(0x2D), 0x0D => K(0x2E),
                0x10 => K(0x14), 0x11 => K(0x1A), 0x12 => K(0x08), 0x13 => K(0x15), 0x14 => K(0x17),
                0x15 => K(0x1C), 0x16 => K(0x18), 0x17 => K(0x0C), 0x18 => K(0x12), 0x19 => K(0x13),
                0x1A => K(0x2F), 0x1B => K(0x30),
                0x1E => K(0x04), 0x1F => K(0x16), 0x20 => K(0x07), 0x21 => K(0x09), 0x22 => K(0x0A),
                0x23 => K(0x0B), 0x24 => K(0x0D), 0x25 => K(0x0E), 0x26 => K(0x0F), 0x27 => K(0x33),
                0x28 => K(0x34), 0x29 => K(0x35), 0x2B => K(0x31),
                0x2C => K(0x1D), 0x2D => K(0x1B), 0x2E => K(0x06), 0x2F => K(0x19), 0x30 => K(0x05),
                0x31 => K(0x11), 0x32 => K(0x10), 0x33 => K(0x36), 0x34 => K(0x37), 0x35 => K(0x38),
                0x56 => K(0x64),   // NONUSBACKSLASH
                0x73 => K(0x87),   // INTERNATIONAL1 (JIS Ro)
                0x7D => K(0x89),   // INTERNATIONAL3 (JIS Yen)
                0x7E => K(0x85),   // KP_COMMA
                _ => None,
            };
        }

        /// <summary>The keyboard usages Windows reports scan key
        /// <paramref name="scanKey"/> for (0x100 for the E0 form), the form a
        /// window message's lParam carries: <see cref="ScanKeyOf"/> read the
        /// other way. Two usages share 0x2B, the ANSI backslash (0x31) and the
        /// ISO key left of Enter (0x32), so the caller tries each. Empty for a
        /// scan key no row sends.</summary>
        internal static IReadOnlyList<ushort> UsagesOfScanKey(int scanKey)
            => s_usagesByScanKey.Value.TryGetValue(scanKey, out var usages) ? usages : Array.Empty<ushort>();

        private static readonly Lazy<Dictionary<int, ushort[]>> s_usagesByScanKey = new(() =>
        {
            var lists = new Dictionary<int, List<ushort>>();
            for (int u = 1; u <= 0xFF; u++)
            {
                int scanKey = ScanKeyOf(K(u));
                if (scanKey < 0) continue;
                if (!lists.TryGetValue(scanKey, out var list)) lists[scanKey] = list = new List<ushort>();
                list.Add((ushort)u);
            }
            var table = new Dictionary<int, ushort[]>(lists.Count);
            foreach (var pair in lists) table[pair.Key] = pair.Value.ToArray();
            return table;
        });

        /// <summary>The scan code the low-level hook reports for a keyboard
        /// usage, as <see cref="PadForge.Engine.Common.OwnHidOutput.ScanKey"/>
        /// spells it (0x100 for the E0 form), or -1 for a consumer usage or
        /// one no row sends. SDL's windows_scancode_table read backwards: the
        /// form Windows reports for each usage.</summary>
        internal static int ScanKeyOf(HidKey key)
        {
            if (!key.IsKeyboard) return -1;
            int u = key.Usage;
            const int E0 = 0x100;
            if (u >= 0x04 && u <= 0x1D)
                return LetterScan[u - 0x04];
            if (u >= 0x1E && u <= 0x26) return 0x02 + (u - 0x1E);   // 1-9
            if (u >= 0x3A && u <= 0x43) return 0x3B + (u - 0x3A);   // F1-F10
            if (u >= 0x68 && u <= 0x72) return 0x64 + (u - 0x68);   // F13-F23
            if (u >= 0x59 && u <= 0x63) return KeypadScan[u - 0x59];
            return u switch
            {
                0x27 => 0x0B,         // 0
                0x28 => 0x1C,         // Enter
                0x29 => 0x01,         // Escape
                0x2A => 0x0E,         // Backspace
                0x2B => 0x0F,         // Tab
                0x2C => 0x39,         // Space
                0x2D => 0x0C, 0x2E => 0x0D, 0x2F => 0x1A, 0x30 => 0x1B,
                0x31 => 0x2B, 0x32 => 0x2B, 0x33 => 0x27, 0x34 => 0x28,
                0x35 => 0x29, 0x36 => 0x33, 0x37 => 0x34, 0x38 => 0x35,
                0x39 => 0x3A,         // Caps Lock
                0x44 => 0x57,         // F11
                0x45 => 0x58,         // F12
                0x46 => E0 | 0x37,    // Print Screen
                0x47 => 0x46,         // Scroll Lock
                // Pause and Num Lock trade forms here against SDL's table, which
                // holds the raw codes (Num Lock 45, Pause E1 1D 45). The low-level
                // hook reports Pause as 45 and Num Lock as 45 with the extended
                // flag, and that is the form a hook event is matched on.
                0x48 => 0x45,         // Pause
                0x49 => E0 | 0x52,    // Insert
                0x4A => E0 | 0x47,    // Home
                0x4B => E0 | 0x49,    // Page Up
                0x4C => E0 | 0x53,    // Delete
                0x4D => E0 | 0x4F,    // End
                0x4E => E0 | 0x51,    // Page Down
                0x4F => E0 | 0x4D,    // Right
                0x50 => E0 | 0x4B,    // Left
                0x51 => E0 | 0x50,    // Down
                0x52 => E0 | 0x48,    // Up
                0x53 => E0 | 0x45,    // Num Lock
                0x54 => E0 | 0x35,    // Keypad /
                0x55 => 0x37,         // Keypad *
                0x56 => 0x4A,         // Keypad -
                0x57 => 0x4E,         // Keypad +
                0x58 => E0 | 0x1C,    // Keypad Enter
                0x64 => 0x56,         // Non-US \ |
                0x65 => E0 | 0x5D,    // Application
                0x67 => 0x59,         // Keypad =
                0x73 => 0x76,         // F24
                0x85 => 0x7E,         // Keypad ,
                0x87 => 0x73,         // International1 (Ro)
                0x88 => 0x70,         // International2 (Kana)
                0x89 => 0x7D,         // International3 (Yen)
                0x8A => 0x79,         // International4 (Henkan)
                0x8B => 0x7B,         // International5 (Muhenkan)
                0xE0 => 0x1D,         // Left Control
                0xE1 => 0x2A,         // Left Shift
                0xE2 => 0x38,         // Left Alt
                0xE3 => E0 | 0x5B,    // Left Windows
                0xE4 => E0 | 0x1D,    // Right Control
                0xE5 => 0x36,         // Right Shift
                0xE6 => E0 | 0x38,    // Right Alt
                0xE7 => E0 | 0x5C,    // Right Windows
                _ => -1,
            };
        }

        // A..Z, usage 0x04 to 0x1D.
        private static readonly int[] LetterScan =
        {
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
        };

        // Keypad 1..9, 0, period, usage 0x59 to 0x63.
        private static readonly int[] KeypadScan =
        {
            0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52, 0x53,
        };

        // ── The other direction, for the preview's keyboard drawings ──────

        /// <summary>Keyboard usage to the key index whose fixed usage it is,
        /// the left and right modifiers over the generic ones the grid does
        /// not offer.</summary>
        private static readonly int[] s_fixedTarget = BuildFixedTargets();

        /// <summary>Keyboard usage to the key index whose US position it
        /// is.</summary>
        private static readonly int[] s_usTarget = BuildUsTargets();

        private static int[] BuildFixedTargets()
        {
            var t = new int[256];
            Array.Fill(t, -1);
            for (int vk = 255; vk >= 0; vk--)
            {
                // The generic Shift, Control and Alt share the left keys'
                // usages, and the grid offers only the left and right ones.
                if (vk is 0x10 or 0x11 or 0x12) continue;
                var k = s_fixed[vk];
                if (k.IsKeyboard && k.Usage <= 0xFF) t[k.Usage] = vk;
            }

            // Keys a keyboard read over WinUSB reports by usage that no fixed
            // row sends, each at the virtual-key code Windows names for it.
            // Linux gives every one of these a key (linux-hid hid-input.c,
            // hid_keyboard). Mute and the two volume keys take the row of
            // their Consumer page usages, the form most keyboards send.
            t[0x67] = 0x92;   // Keypad =, VK_OEM_NEC_EQUAL
            t[0x7F] = 0xAD;   // Mute
            t[0x80] = 0xAF;   // Volume Up
            t[0x81] = 0xAE;   // Volume Down
            t[0x85] = 0xC2;   // Keypad comma, VK_ABNT_C2
            t[0x90] = 0x15;   // LANG1 (Hangul), VK_HANGUL
            t[0x91] = 0x19;   // LANG2 (Hanja), VK_HANJA
            t[0x92] = 0xF1;   // LANG3 (Katakana), VK_OEM_FINISH
            t[0x93] = 0xF2;   // LANG4 (Hiragana), VK_OEM_COPY
            t[0x94] = 0xF3;   // LANG5 (Zenkaku/Hankaku), VK_OEM_AUTO
            return t;
        }

        private static int[] BuildUsTargets()
        {
            var t = new int[256];
            Array.Fill(t, -1);
            for (int vk = 0; vk < 256; vk++)
            {
                var k = s_usPositional[vk];
                if (k.IsKeyboard && k.Usage <= 0xFF) t[k.Usage] = vk;
            }
            return t;
        }

        /// <summary>True for a usage whose meaning the input layout decides:
        /// the letter, number and punctuation positions, the non-US keys and
        /// the Japanese Ro and Yen keys.</summary>
        internal static bool IsCharacterUsage(ushort usage) =>
            (usage >= 0x04 && usage <= 0x27) || (usage >= 0x2D && usage <= 0x38)
            || usage == 0x64 || usage == 0x87 || usage == 0x89;

        /// <summary>The key index a drawn key stands for on
        /// <paramref name="layout"/>: the code that layout types from the
        /// key's scan code when that code resolves back to the same scan code,
        /// else the key's fixed or US-position code. -1 for a key no row
        /// reaches. The test is the scan code and not the usage because
        /// Windows gives two usages one scan code: the ANSI backslash (0x31)
        /// and the ISO key left of Enter (0x32) both arrive as 0x2B, so a
        /// press of either is the same key to every program.</summary>
        internal static int TargetOf(HidKey key, IntPtr layout)
        {
            if (key.IsConsumer)
            {
                for (int vk = 0; vk < 256; vk++)
                    if (s_fixed[vk].IsConsumer && s_fixed[vk].Usage == key.Usage) return vk;
                return -1;
            }
            if (!key.IsKeyboard || key.Usage > 0xFF) return -1;
            if (IsCharacterUsage(key.Usage))
            {
                int scanKey = ScanKeyOf(key);
                if (scanKey >= 0)
                {
                    uint code = (uint)(scanKey & 0xFF) | ((scanKey & 0x100) != 0 ? 0xE000u : 0u);
                    uint vk;
                    try { vk = MapVirtualKeyExW(code, MAPVK_VSC_TO_VK_EX, layout); }
                    catch { vk = 0; }
                    // The Hankaku/Zenkaku key reports either of its two codes.
                    if (vk == 0xF4) vk = 0xF3;
                    if (vk > 0 && vk <= 0xFF)
                    {
                        var back = Resolve((int)vk, layout);
                        if (back.IsKeyboard && ScanKeyOf(back) == scanKey) return (int)vk;
                    }
                }
            }
            int fixedVk = s_fixedTarget[key.Usage];
            return fixedVk >= 0 ? fixedVk : s_usTarget[key.Usage];
        }

        /// <summary>The character <paramref name="vk"/> types unshifted on
        /// <paramref name="layout"/>, upper case for a letter, or null when it
        /// types none. MAPVK_VK_TO_CHAR leaves the keyboard state alone, which
        /// ToUnicodeEx would not for a dead key.</summary>
        internal static string CharacterLegend(int vk, IntPtr layout)
        {
            if (vk < 0 || vk > 0xFF) return null;
            uint ch;
            try { ch = MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_CHAR, layout) & 0x7FFFFFFFu; }
            catch { return null; }
            if (ch <= 0x20 || ch == 0x7F || ch > 0xFFFF) return null;
            return ((char)ch).ToString();
        }

        private const uint MAPVK_VK_TO_CHAR = 2;
        private const uint MAPVK_VSC_TO_VK_EX = 3;
        private const uint MAPVK_VK_TO_VSC_EX = 4;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        /// <summary>The scan code a layout types <paramref name="vk"/> from,
        /// 0 when it has none.</summary>
        private static uint LayoutScanCode(uint vk, IntPtr layout)
        {
            try { return MapVirtualKeyExW(vk, MAPVK_VK_TO_VSC_EX, layout); }
            catch { return 0; }
        }

        /// <summary>The calling thread's input layout, the one PadForge's own
        /// window types through, for the preview's legends.</summary>
        internal static IntPtr UiLayout()
        {
            try { return GetKeyboardLayout(0); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>The input layout of the window that has the keyboard focus,
        /// which is the layout Windows reads a HID keyboard's scan codes
        /// through. 0 (this thread's layout) when no window has it.</summary>
        internal static IntPtr ForegroundLayout()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint thread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
                return GetKeyboardLayout(thread);
            }
            catch { return IntPtr.Zero; }
        }
    }
}
