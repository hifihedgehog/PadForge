using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Parses and formats canonical keyboard-combo strings used for global
    /// hotkeys (e.g. <c>"Ctrl+Alt+T"</c>, <c>"Win+Shift+F12"</c>). Round-trips
    /// to / from arrays of Win32 virtual-key codes consumed by
    /// <see cref="InputHookManager"/>'s low-level keyboard hook.
    ///
    /// Recognized modifier tokens (case-insensitive): <c>Ctrl</c> / <c>Control</c>,
    /// <c>Alt</c>, <c>Shift</c>, <c>Win</c> / <c>Super</c> / <c>Meta</c>. The
    /// single non-modifier key may be a letter (<c>A</c>..<c>Z</c>), digit
    /// (<c>0</c>..<c>9</c>), F-key (<c>F1</c>..<c>F24</c>), or named key
    /// (<c>Space</c>, <c>Tab</c>, <c>Enter</c>, <c>Esc</c>, etc.). Modifier-only
    /// combos parse but are rejected at registration time by the hook (would
    /// fire on every modifier press).
    /// </summary>
    public static class GlobalHotkeyParser
    {
        // Modifier VK codes (Win32).
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;        // Alt
        private const int VK_LWIN = 0x5B;
        // Left/right specific (low-level hook reports these).
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_RWIN = 0x5C;

        /// <summary>
        /// Parse a combo string like <c>"Ctrl+Alt+T"</c> into an array of VK
        /// codes. The first N-1 codes are modifier sentinels (VK_CONTROL,
        /// VK_MENU, VK_SHIFT, VK_LWIN); the last is the non-modifier key.
        /// Returns null if the string is empty or unparseable.
        /// </summary>
        public static int[] Parse(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return null;
            var tokens = combo.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return null;

            var vks = new List<int>();
            bool hasNonModifier = false;
            foreach (var rawToken in tokens)
            {
                var t = rawToken.Trim();
                if (t.Length == 0) continue;
                int vk = TokenToVk(t);
                if (vk < 0) return null;
                if (IsModifierVk(vk))
                {
                    if (!vks.Contains(vk)) vks.Add(vk);
                }
                else
                {
                    if (hasNonModifier) return null; // only one non-modifier allowed
                    vks.Add(vk);
                    hasNonModifier = true;
                }
            }
            if (!hasNonModifier) return null;
            return vks.ToArray();
        }

        /// <summary>
        /// Format a VK-code array back into a canonical combo string (e.g.
        /// <c>"Ctrl+Alt+T"</c>). Returns empty string on null / empty input.
        /// </summary>
        public static string Format(int[] vkCodes)
        {
            if (vkCodes == null || vkCodes.Length == 0) return string.Empty;
            var parts = new List<string>();
            // Modifiers first, in canonical order: Ctrl, Alt, Shift, Win.
            if (vkCodes.Contains(VK_CONTROL) || vkCodes.Contains(VK_LCONTROL) || vkCodes.Contains(VK_RCONTROL))
                parts.Add("Ctrl");
            if (vkCodes.Contains(VK_MENU) || vkCodes.Contains(VK_LMENU) || vkCodes.Contains(VK_RMENU))
                parts.Add("Alt");
            if (vkCodes.Contains(VK_SHIFT) || vkCodes.Contains(VK_LSHIFT) || vkCodes.Contains(VK_RSHIFT))
                parts.Add("Shift");
            if (vkCodes.Contains(VK_LWIN) || vkCodes.Contains(VK_RWIN))
                parts.Add("Win");
            foreach (var vk in vkCodes)
            {
                if (IsModifierVk(vk)) continue;
                parts.Add(VkToToken(vk));
            }
            return string.Join("+", parts);
        }

        /// <summary>
        /// Returns true if the VK code is one of the recognized modifier keys
        /// (either general or left/right-specific).
        /// </summary>
        public static bool IsModifierVk(int vk)
        {
            return vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU ||
                   vk == VK_LSHIFT || vk == VK_RSHIFT ||
                   vk == VK_LCONTROL || vk == VK_RCONTROL ||
                   vk == VK_LMENU || vk == VK_RMENU ||
                   vk == VK_LWIN || vk == VK_RWIN;
        }

        /// <summary>
        /// Returns the modifier-sentinel VK that corresponds to a left/right
        /// VK (e.g. VK_LCONTROL → VK_CONTROL). Non-modifiers return -1.
        /// </summary>
        public static int NormalizeModifier(int vk)
        {
            switch (vk)
            {
                case VK_LSHIFT:
                case VK_RSHIFT:
                case VK_SHIFT:    return VK_SHIFT;
                case VK_LCONTROL:
                case VK_RCONTROL:
                case VK_CONTROL:  return VK_CONTROL;
                case VK_LMENU:
                case VK_RMENU:
                case VK_MENU:     return VK_MENU;
                case VK_LWIN:
                case VK_RWIN:     return VK_LWIN;
                default:          return -1;
            }
        }

        private static int TokenToVk(string token)
        {
            string up = token.ToUpperInvariant();
            switch (up)
            {
                case "CTRL":
                case "CONTROL":  return VK_CONTROL;
                case "ALT":      return VK_MENU;
                case "SHIFT":    return VK_SHIFT;
                case "WIN":
                case "SUPER":
                case "META":     return VK_LWIN;

                case "SPACE":     return 0x20;
                case "TAB":       return 0x09;
                case "ENTER":
                case "RETURN":    return 0x0D;
                case "ESC":
                case "ESCAPE":    return 0x1B;
                case "BACKSPACE": return 0x08;
                case "DELETE":
                case "DEL":       return 0x2E;
                case "INSERT":
                case "INS":       return 0x2D;
                case "HOME":      return 0x24;
                case "END":       return 0x23;
                case "PAGEUP":
                case "PGUP":      return 0x21;
                case "PAGEDOWN":
                case "PGDN":      return 0x22;
                case "UP":        return 0x26;
                case "DOWN":      return 0x28;
                case "LEFT":      return 0x25;
                case "RIGHT":     return 0x27;
                case "CAPSLOCK":  return 0x14;
                case "NUMLOCK":   return 0x90;
                case "SCROLLLOCK":return 0x91;
                case "PRINTSCREEN":
                case "PRTSC":     return 0x2C;
                case "PAUSE":     return 0x13;
            }

            // Single letter
            if (up.Length == 1)
            {
                char c = up[0];
                if (c >= 'A' && c <= 'Z') return c;       // VK A..Z = 0x41..0x5A == 'A'..'Z'
                if (c >= '0' && c <= '9') return c;       // VK 0..9 = 0x30..0x39 == '0'..'9'
            }
            // F-keys
            if (up.Length >= 2 && up[0] == 'F' && int.TryParse(up.Substring(1), out int fn))
            {
                if (fn >= 1 && fn <= 24) return 0x6F + fn; // VK_F1=0x70 ⇒ 0x6F + n
            }
            return -1;
        }

        private static string VkToToken(int vk)
        {
            switch (vk)
            {
                case 0x20: return "Space";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x08: return "Backspace";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x26: return "Up";
                case 0x28: return "Down";
                case 0x25: return "Left";
                case 0x27: return "Right";
                case 0x14: return "CapsLock";
                case 0x90: return "NumLock";
                case 0x91: return "ScrollLock";
                case 0x2C: return "PrintScreen";
                case 0x13: return "Pause";
            }
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            return "0x" + vk.ToString("X2");
        }
    }
}
