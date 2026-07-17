using System;
using System.Collections.Generic;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// Steam Input key names (the token after <c>key_press</c>) to Win32
    /// virtual-key codes, plus the closed set of VKs the KbM virtual
    /// controller actually fires.
    ///
    /// <para>The name vocabulary is grounded against the committed fixture
    /// corpus (letters, digits, F-keys, arrows, modifiers, KEYPAD_*,
    /// RETURN/SPACE/ESCAPE/TAB/BACKSPACE, PERIOD/COMMA/FORWARD_SLASH/
    /// BACK_TICK, PAGE_UP/PAGE_DOWN/HOME/END/INSERT/DELETE, CAPSLOCK) plus
    /// the standard Steam Input names for the remaining punctuation row.</para>
    ///
    /// <para><see cref="SupportedVks"/> mirrors the KbM output engine's
    /// closed key list (InputManager.Step3.UpdateOutputStates.cs static
    /// ctor, KbmKeyVkCodes): only those VKs are iterated by
    /// MapInputToKbmRaw, so a row targeting any other <c>KbmKey..</c> would
    /// sit in the mapping set and never fire. Keys that map to a VK outside
    /// this set ride SendInput macro forms instead of emitting a dead row
    /// (HoldKey pairs since translator v10).</para>
    /// </summary>
    public static class SteamInputVkTable
    {
        /// <summary>Resolves a Steam Input key name. Returns false for
        /// unknown names. <paramref name="vk"/> is the Win32 VK code;
        /// <paramref name="supported"/> is false when the KbM output engine
        /// has no channel for it.</summary>
        public static bool TryResolve(string steamKeyName, out byte vk, out bool supported)
        {
            vk = 0;
            supported = false;
            if (string.IsNullOrWhiteSpace(steamKeyName)) return false;
            if (!NameToVk.TryGetValue(steamKeyName.Trim(), out vk)) return false;
            supported = SupportedVks.Contains(vk);
            return true;
        }

        /// <summary>KbM mapping-row target name for a VK
        /// (<c>"KbmKey{vk:X2}"</c>, the PadViewModel/InputManager form).</summary>
        public static string KbmKeyTarget(byte vk) => $"KbmKey{vk:X2}";

        /// <summary>Steam Input mouse-button name to the KbM
        /// <c>KbmMBtn{0-4}</c> target (LMB, RMB, MMB, X1, X2).</summary>
        public static bool TryResolveMouseButton(string name, out string target)
        {
            bool ok = TryResolveMouseButtonIndex(name, out int idx);
            target = ok ? $"KbmMBtn{idx}" : null;
            return ok;
        }

        /// <summary>Steam Input mouse-button name to the shared 0..4 index
        /// (Left, Right, Middle, X1, X2), which is both the KbmMBtn{n}
        /// target index and the MacroMouseButton enum order the
        /// macro-backed forms use (v10 G6/G10).</summary>
        public static bool TryResolveMouseButtonIndex(string name, out int index)
        {
            switch ((name ?? "").Trim().ToUpperInvariant())
            {
                case "LEFT": index = 0; return true;
                case "RIGHT": index = 1; return true;
                case "MIDDLE": index = 2; return true;
                case "BACK": index = 3; return true;
                case "FORWARD": index = 4; return true;
                default: index = -1; return false;
            }
        }

        private static readonly Dictionary<string, byte> NameToVk = BuildNameTable();

        private static Dictionary<string, byte> BuildNameTable()
        {
            var d = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            // Letters and digits map to their ASCII VKs.
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = (byte)c;
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = (byte)c;

            // Function keys. F13+ resolve but land outside the supported set.
            for (int i = 1; i <= 24; i++) d["F" + i] = (byte)(0x6F + i);

            // Modifiers.
            d["LEFT_SHIFT"] = 0xA0; d["RIGHT_SHIFT"] = 0xA1;
            d["LEFT_CONTROL"] = 0xA2; d["RIGHT_CONTROL"] = 0xA3;
            d["LEFT_ALT"] = 0xA4; d["RIGHT_ALT"] = 0xA5;

            // Specials. ENTER / ESC / BREAK are the serializer's alias
            // spellings (steamclient.dll key token table: RETURN, ENTER,
            // ESCAPE, ESC, ... SCROLL_LOCK, BREAK, INSERT), and Valve's
            // own controller_base templates carry ENTER.
            d["SPACE"] = 0x20;
            d["RETURN"] = 0x0D;
            d["ENTER"] = 0x0D;
            d["ESCAPE"] = 0x1B;
            d["ESC"] = 0x1B;
            d["TAB"] = 0x09;
            d["BACKSPACE"] = 0x08;
            d["CAPSLOCK"] = 0x14;
            d["NUM_LOCK"] = 0x90;
            d["SCROLL_LOCK"] = 0x91;
            d["PRINT_SCREEN"] = 0x2C;
            d["PAUSE"] = 0x13;
            d["BREAK"] = 0x13;

            // Windows keys and the media row (steamclient.dll token table:
            // LEFT_WINDOWS ... RIGHT_CONTROL, VOLUME_UP, VOLUME_DOWN, MUTE,
            // PLAY, STOP, NEXT_TRACK, PREV_TRACK, and Valve's desktop
            // template binds VOLUME_UP / PLAY / NEXT_TRACK). All land
            // outside the KbM row engine's closed set, so they ride the
            // SendInput HoldKey macro channel (v10 G11).
            d["LEFT_WINDOWS"] = 0x5B;
            d["RIGHT_WINDOWS"] = 0x5C;
            d["VOLUME_UP"] = 0xAF;    // VK_VOLUME_UP
            d["VOLUME_DOWN"] = 0xAE;  // VK_VOLUME_DOWN
            d["MUTE"] = 0xAD;         // VK_VOLUME_MUTE
            d["PLAY"] = 0xB3;         // VK_MEDIA_PLAY_PAUSE
            d["STOP"] = 0xB2;         // VK_MEDIA_STOP
            d["NEXT_TRACK"] = 0xB0;   // VK_MEDIA_NEXT_TRACK
            d["PREV_TRACK"] = 0xB1;   // VK_MEDIA_PREV_TRACK

            // Navigation.
            d["UP_ARROW"] = 0x26; d["DOWN_ARROW"] = 0x28;
            d["LEFT_ARROW"] = 0x25; d["RIGHT_ARROW"] = 0x27;
            d["HOME"] = 0x24; d["END"] = 0x23;
            d["PAGE_UP"] = 0x21; d["PAGE_DOWN"] = 0x22;
            d["INSERT"] = 0x2D; d["DELETE"] = 0x2E;

            // Punctuation. OEM VKs, US layout: the same channels the KbM
            // picker labels ; = , - . / ` [ \ ] '.
            d["SEMICOLON"] = 0xBA;
            d["EQUALS"] = 0xBB;
            d["COMMA"] = 0xBC;
            d["DASH"] = 0xBD;
            d["PERIOD"] = 0xBE;
            d["FORWARD_SLASH"] = 0xBF;
            d["BACK_TICK"] = 0xC0;
            d["LEFT_BRACKET"] = 0xDB;
            d["BACK_SLASH"] = 0xDC;
            // The serializer's own spelling (steamclient.dll token table:
            // LEFT_BRACKET, RIGHT_BRACKET, BACKSLASH).
            d["BACKSLASH"] = 0xDC;
            d["RIGHT_BRACKET"] = 0xDD;
            d["SINGLE_QUOTE"] = 0xDE;

            // Keypad.
            for (int i = 0; i <= 9; i++) d["KEYPAD_" + i] = (byte)(0x60 + i);
            d["KEYPAD_ASTERISK"] = 0x6A;
            d["KEYPAD_PLUS"] = 0x6B;
            d["KEYPAD_DASH"] = 0x6D;
            d["KEYPAD_PERIOD"] = 0x6E;
            d["KEYPAD_FORWARD_SLASH"] = 0x6F;
            // Windows has no distinct numpad-Enter VK (it is VK_RETURN with
            // the extended-key flag, which SendInput-level mapping ignores).
            d["KEYPAD_ENTER"] = 0x0D;

            return d;
        }

        /// <summary>The KbM output engine's closed VK list. MUST mirror
        /// InputManager.Step3.UpdateOutputStates.cs (KbmKeyVkCodes).</summary>
        private static readonly HashSet<byte> SupportedVks = BuildSupportedSet();

        private static HashSet<byte> BuildSupportedSet()
        {
            var s = new HashSet<byte>();
            for (int i = 0; i < 26; i++) s.Add((byte)(0x41 + i));   // A-Z
            for (int i = 0; i <= 9; i++) s.Add((byte)(0x30 + i));   // 0-9
            for (int i = 0; i < 12; i++) s.Add((byte)(0x70 + i));   // F1-F12
            s.Add(0xA0); s.Add(0xA1);                               // L/R Shift
            s.Add(0xA2); s.Add(0xA3);                               // L/R Ctrl
            s.Add(0xA4); s.Add(0xA5);                               // L/R Alt
            s.Add(0x20); s.Add(0x0D); s.Add(0x1B);                  // Space, Enter, Esc
            s.Add(0x09); s.Add(0x08); s.Add(0x14);                  // Tab, Backspace, CapsLock
            s.Add(0x26); s.Add(0x28); s.Add(0x25); s.Add(0x27);     // arrows
            s.Add(0x24); s.Add(0x23); s.Add(0x21); s.Add(0x22);     // Home/End/PgUp/PgDn
            s.Add(0x2D); s.Add(0x2E);                               // Insert/Delete
            s.Add(0xBA); s.Add(0xBB); s.Add(0xBC); s.Add(0xBD);     // ; = , -
            s.Add(0xBE); s.Add(0xBF); s.Add(0xC0); s.Add(0xDB);     // . / ` [
            s.Add(0xDC); s.Add(0xDD); s.Add(0xDE);                  // \ ] '
            for (int i = 0; i <= 9; i++) s.Add((byte)(0x60 + i));   // Num 0-9
            s.Add(0x6A); s.Add(0x6B); s.Add(0x6D); s.Add(0x6E); s.Add(0x6F); // Num * + - . /
            return s;
        }
    }
}
