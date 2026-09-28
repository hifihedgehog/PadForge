using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The key code space analog keyboards publish into (issue #468), and the
    /// vendor tables that translate each protocol's own numbering into it.
    ///
    /// <para>Codes follow the AnalogSense convention its two MIT references
    /// share (Soup's AnalogueKeyboard.cpp and the AnalogSense JavaScript SDK):
    /// the HID usage on the keyboard page for ordinary keys, and Wooting's
    /// namespaced form for the rest, 0x3xx for consumer keys and 0x4xx for Fn
    /// and the vendor keys. Every table below is transcribed from those two
    /// files and cross-checked between them. Where they disagree the choice
    /// and its reason sit on the entry.</para>
    /// </summary>
    public static class AnalogKeyCodes
    {
        public const int None = 0;
        public const int A = 0x04, B = 0x05, C = 0x06, D = 0x07, E = 0x08, F = 0x09, G = 0x0A,
            H = 0x0B, I = 0x0C, J = 0x0D, K = 0x0E, L = 0x0F, M = 0x10, N = 0x11, O = 0x12,
            P = 0x13, Q = 0x14, R = 0x15, S = 0x16, T = 0x17, U = 0x18, V = 0x19, W = 0x1A,
            X = 0x1B, Y = 0x1C, Z = 0x1D;
        public const int D1 = 0x1E, D2 = 0x1F, D3 = 0x20, D4 = 0x21, D5 = 0x22, D6 = 0x23,
            D7 = 0x24, D8 = 0x25, D9 = 0x26, D0 = 0x27;
        public const int Enter = 0x28, Escape = 0x29, Backspace = 0x2A, Tab = 0x2B, Space = 0x2C,
            Minus = 0x2D, EqualSign = 0x2E, BracketLeft = 0x2F, BracketRight = 0x30,
            Backslash = 0x31, IntlHash = 0x32, Semicolon = 0x33, Quote = 0x34,
            Backquote = 0x35, Comma = 0x36, Period = 0x37, Slash = 0x38, CapsLock = 0x39;
        public const int F1 = 0x3A, F2 = 0x3B, F3 = 0x3C, F4 = 0x3D, F5 = 0x3E, F6 = 0x3F,
            F7 = 0x40, F8 = 0x41, F9 = 0x42, F10 = 0x43, F11 = 0x44, F12 = 0x45;
        public const int PrintScreen = 0x46, ScrollLock = 0x47, Pause = 0x48, Insert = 0x49,
            Home = 0x4A, PageUp = 0x4B, Delete = 0x4C, End = 0x4D, PageDown = 0x4E,
            ArrowRight = 0x4F, ArrowLeft = 0x50, ArrowDown = 0x51, ArrowUp = 0x52;
        public const int NumLock = 0x53, NumpadDivide = 0x54, NumpadMultiply = 0x55,
            NumpadSubtract = 0x56, NumpadAdd = 0x57, NumpadEnter = 0x58, Numpad1 = 0x59,
            Numpad2 = 0x5A, Numpad3 = 0x5B, Numpad4 = 0x5C, Numpad5 = 0x5D, Numpad6 = 0x5E,
            Numpad7 = 0x5F, Numpad8 = 0x60, Numpad9 = 0x61, Numpad0 = 0x62, NumpadDecimal = 0x63;
        public const int IntlBackslash = 0x64, ContextMenu = 0x65;
        public const int F13 = 0x68, F24 = 0x73;
        public const int VolumeMute = 0x7F, VolumeUp = 0x80, VolumeDown = 0x81;
        public const int LCtrl = 0xE0, LShift = 0xE1, LAlt = 0xE2, LMeta = 0xE3,
            RCtrl = 0xE4, RShift = 0xE5, RAlt = 0xE6, RMeta = 0xE7;

        // Consumer page keys, namespace 3 (Wooting's HidFunction namespace).
        public const int NextTrack = 0x3B5, PrevTrack = 0x3B6, StopMedia = 0x3B7,
            PlayPause = 0x3CD, ConsumerMute = 0x3E2, ConsumerVolumeUp = 0x3E9,
            ConsumerVolumeDown = 0x3EA;

        // Vendor keys, namespace 4 (Wooting's CustomFunction namespace). The
        // references name 0x403 to 0x405 KEY_OEM_1 to KEY_OEM_3 and 0x408
        // KEY_OEM_4, and reuse them for the extra keys on the Keychron boards.
        public const int Oem5 = 0x401, Oem6 = 0x402, Oem1 = 0x403, Oem2 = 0x404, Oem3 = 0x405,
            Oem4 = 0x408, Fn = 0x409;

        /// <summary>The "Extra Key N" number of a vendor key, the references'
        /// KEY_OEM_N, or 0 when the code is not one.</summary>
        public static int OemNumber(int code) => code switch
        {
            Oem1 => 1, Oem2 => 2, Oem3 => 3, Oem4 => 4, Oem5 => 5, Oem6 => 6,
            _ => 0,
        };

        /// <summary>Every code the picker lists for a keyboard with no known
        /// layout, in keyboard order: function row, number row, the letter
        /// rows, the bottom row, the navigation cluster, the number pad, then
        /// the media and vendor keys. The AnalogSense JavaScript SDK's key
        /// table order, extended with the codes Soup's Wooting table names.</summary>
        public static readonly int[] FullKeyboard =
        {
            Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
            Backquote, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, IntlHash, Enter,
            LShift, IntlBackslash, Z, X, C, V, B, N, M, Comma, Period, Slash, RShift,
            LCtrl, LMeta, LAlt, Space, RAlt, RMeta, Fn, ContextMenu, RCtrl,
            PrintScreen, ScrollLock, Pause, Insert, Home, PageUp, Delete, End, PageDown,
            ArrowUp, ArrowLeft, ArrowDown, ArrowRight,
            NumLock, NumpadDivide, NumpadMultiply, NumpadSubtract,
            Numpad7, Numpad8, Numpad9, NumpadAdd, Numpad4, Numpad5, Numpad6,
            Numpad1, Numpad2, Numpad3, NumpadEnter, Numpad0, NumpadDecimal,
            VolumeMute, VolumeDown, VolumeUp,
            PrevTrack, PlayPause, NextTrack, StopMedia,
            Oem1, Oem2, Oem3, Oem4, Oem5, Oem6,
        };

        /// <summary>The US-layout Windows virtual key a code carries, which is
        /// how the app names it (a key is named for its US legend, the
        /// references' convention). 0 when no virtual key exists for it.</summary>
        public static int UsVirtualKey(int code)
        {
            if (code >= A && code <= Z) return 0x41 + (code - A);
            if (code >= D1 && code <= D9) return 0x31 + (code - D1);
            if (code >= F1 && code <= F12) return 0x70 + (code - F1);
            if (code >= F13 && code <= F24) return 0x7C + (code - F13);
            if (code >= Numpad1 && code <= Numpad9) return 0x61 + (code - Numpad1);
            return code switch
            {
                D0 => 0x30,
                Enter => 0x0D,
                Escape => 0x1B,
                Backspace => 0x08,
                Tab => 0x09,
                Space => 0x20,
                Minus => 0xBD,
                EqualSign => 0xBB,
                BracketLeft => 0xDB,
                BracketRight => 0xDD,
                Backslash => 0xDC,
                Semicolon => 0xBA,
                Quote => 0xDE,
                Backquote => 0xC0,
                Comma => 0xBC,
                Period => 0xBE,
                Slash => 0xBF,
                CapsLock => 0x14,
                PrintScreen => 0x2C,
                ScrollLock => 0x91,
                Pause => 0x13,
                Insert => 0x2D,
                Home => 0x24,
                PageUp => 0x21,
                Delete => 0x2E,
                End => 0x23,
                PageDown => 0x22,
                ArrowRight => 0x27,
                ArrowLeft => 0x25,
                ArrowDown => 0x28,
                ArrowUp => 0x26,
                NumLock => 0x90,
                NumpadDivide => 0x6F,
                NumpadMultiply => 0x6A,
                NumpadSubtract => 0x6D,
                NumpadAdd => 0x6B,
                Numpad0 => 0x60,
                NumpadDecimal => 0x6E,
                ContextMenu => 0x5D,
                VolumeMute or ConsumerMute => 0xAD,
                VolumeDown or ConsumerVolumeDown => 0xAE,
                VolumeUp or ConsumerVolumeUp => 0xAF,
                NextTrack => 0xB0,
                PrevTrack => 0xB1,
                StopMedia => 0xB2,
                PlayPause => 0xB3,
                LCtrl => 0xA2,
                LShift => 0xA0,
                LAlt => 0xA4,
                LMeta => 0x5B,
                RCtrl => 0xA3,
                RShift => 0xA1,
                RAlt => 0xA5,
                RMeta => 0x5C,
                _ => 0,
            };
        }

        /// <summary>The PS/2 set 1 scan code of a code, 0xE0-prefixed in the
        /// high byte for the extended keys, 0 when there is none. Soup's
        /// Ps2Scancode.hpp values. The app turns it into the virtual key the
        /// current layout gives that physical key, which is what the polled
        /// keyboards' held-key check asks Windows about.</summary>
        public static int Ps2Scancode(int code) => code switch
        {
            A => 0x1E, B => 0x30, C => 0x2E, D => 0x20, E => 0x12, F => 0x21, G => 0x22,
            H => 0x23, I => 0x17, J => 0x24, K => 0x25, L => 0x26, M => 0x32, N => 0x31,
            O => 0x18, P => 0x19, Q => 0x10, R => 0x13, S => 0x1F, T => 0x14, U => 0x16,
            V => 0x2F, W => 0x11, X => 0x2D, Y => 0x15, Z => 0x2C,
            D1 => 0x02, D2 => 0x03, D3 => 0x04, D4 => 0x05, D5 => 0x06, D6 => 0x07,
            D7 => 0x08, D8 => 0x09, D9 => 0x0A, D0 => 0x0B,
            Enter => 0x1C, Escape => 0x01, Backspace => 0x0E, Tab => 0x0F, Space => 0x39,
            Minus => 0x0C, EqualSign => 0x0D, BracketLeft => 0x1A, BracketRight => 0x1B,
            Backslash => 0x2B, IntlHash => 0x2B, Semicolon => 0x27, Quote => 0x28,
            Backquote => 0x29, Comma => 0x33, Period => 0x34, Slash => 0x35, CapsLock => 0x3A,
            F1 => 0x3B, F2 => 0x3C, F3 => 0x3D, F4 => 0x3E, F5 => 0x3F, F6 => 0x40,
            F7 => 0x41, F8 => 0x42, F9 => 0x43, F10 => 0x44, F11 => 0x57, F12 => 0x58,
            PrintScreen => 0xE037, ScrollLock => 0x46,
            Insert => 0xE052, Home => 0xE047, PageUp => 0xE049, Delete => 0xE053,
            End => 0xE04F, PageDown => 0xE051,
            ArrowUp => 0xE048, ArrowLeft => 0xE04B, ArrowDown => 0xE050, ArrowRight => 0xE04D,
            NumLock => 0x45, NumpadDivide => 0xE035, NumpadMultiply => 0x37,
            NumpadSubtract => 0x4A, NumpadAdd => 0x4E, NumpadEnter => 0xE01C,
            Numpad7 => 0x47, Numpad8 => 0x48, Numpad9 => 0x49, Numpad4 => 0x4B,
            Numpad5 => 0x4C, Numpad6 => 0x4D, Numpad1 => 0x4F, Numpad2 => 0x50,
            Numpad3 => 0x51, Numpad0 => 0x52, NumpadDecimal => 0x53,
            IntlBackslash => 0x56, ContextMenu => 0xE05D,
            LCtrl => 0x1D, LShift => 0x2A, LAlt => 0x38, LMeta => 0xE05B,
            RCtrl => 0xE01D, RShift => 0x36, RAlt => 0xE038, RMeta => 0xE05C,
            _ => 0,
        };

        // ── Razer ──────────────────────────────────────────────────────────

        private static readonly Dictionary<int, int> RazerTable = new()
        {
            [0x6E] = Escape, [0x70] = F1, [0x71] = F2, [0x72] = F3, [0x73] = F4,
            [0x74] = F5, [0x75] = F6, [0x76] = F7, [0x77] = F8, [0x78] = F9,
            [0x79] = F10, [0x7A] = F11, [0x7B] = F12,
            [0x01] = Backquote, [0x02] = D1, [0x03] = D2, [0x04] = D3, [0x05] = D4,
            [0x06] = D5, [0x07] = D6, [0x08] = D7, [0x09] = D8, [0x0A] = D9, [0x0B] = D0,
            [0x0C] = Minus, [0x0D] = EqualSign, [0x0F] = Backspace, [0x10] = Tab,
            [0x11] = Q, [0x12] = W, [0x13] = E, [0x14] = R, [0x15] = T, [0x16] = Y,
            [0x17] = U, [0x18] = I, [0x19] = O, [0x1A] = P,
            [0x1B] = BracketLeft, [0x1C] = BracketRight, [0x2B] = Enter,
            [0x1E] = CapsLock, [0x1F] = A, [0x20] = S, [0x21] = D, [0x22] = F,
            [0x23] = G, [0x24] = H, [0x25] = J, [0x26] = K, [0x27] = L,
            [0x28] = Semicolon, [0x29] = Quote, [0x2A] = Backslash,
            [0x2C] = LShift, [0x2D] = IntlBackslash, [0x2E] = Z, [0x2F] = X, [0x30] = C,
            [0x31] = V, [0x32] = B, [0x33] = N, [0x34] = M, [0x35] = Comma,
            [0x36] = Period, [0x37] = Slash, [0x39] = RShift,
            [0x3A] = LCtrl, [0x7F] = LMeta, [0x3C] = LAlt, [0x3D] = Space, [0x3E] = RAlt,
            [0x3B] = Fn, [0x81] = ContextMenu, [0x40] = RCtrl,
            [0x7C] = PrintScreen, [0x7D] = Pause, [0x7E] = ScrollLock,
            [0x4B] = Insert, [0x50] = Home, [0x55] = PageUp, [0x4C] = Delete,
            [0x51] = End, [0x56] = PageDown,
            [0x53] = ArrowUp, [0x4F] = ArrowLeft, [0x54] = ArrowDown, [0x59] = ArrowRight,
            [0x5A] = NumLock, [0x5F] = NumpadDivide, [0x64] = NumpadMultiply,
            [0x69] = NumpadSubtract, [0x5B] = Numpad7, [0x60] = Numpad8, [0x65] = Numpad9,
            [0x6A] = NumpadAdd, [0x5C] = Numpad4, [0x61] = Numpad5, [0x66] = Numpad6,
            [0x5D] = Numpad1, [0x62] = Numpad2, [0x67] = Numpad3, [0x6C] = NumpadEnter,
            [0x63] = Numpad0, [0x68] = NumpadDecimal,
        };

        /// <summary>A Razer Huntsman report's key number to the shared code, 0
        /// when unknown. Soup's razer_scancode_to_soup_key and the "razer"
        /// column of AnalogSense.js, which agree entry for entry.</summary>
        public static int RazerToCode(int razer)
            => RazerTable.TryGetValue(razer, out int code) ? code : None;

        /// <summary>The Razer Tartarus Pro's 20 analog keys in report order,
        /// named for their factory assignments. The Tartarus Pro commit on
        /// DenkiSuki's Soup fork, and the same order HallJoy publishes as its
        /// kFactoryHids fact table.</summary>
        public static readonly int[] TartarusPro =
        {
            D1, D2, D3, D4, D5,
            Tab, Q, W, E, R,
            CapsLock, A, S, D, F,
            LShift, Z, X, C, Space,
        };

        // ── NuPhy ──────────────────────────────────────────────────────────

        /// <summary>A NuPhy report's key number to the shared code: HID usages
        /// below 0x100, and modifier bits above it. Soup's getActiveKeysNuphy
        /// and the "nuphy" column of AnalogSense.js agree on every modifier.</summary>
        public static int NuPhyToCode(int nuphy)
        {
            if ((nuphy >> 8) == 0) return nuphy;
            return nuphy switch
            {
                0x100 => LCtrl,
                0x200 => LShift,
                0x400 => LAlt,
                0x800 => LMeta,
                0x1000 => RCtrl,
                0x2000 => RShift,
                0x4000 => RAlt,
                0x8000 => RMeta,
                0xFF05 => Fn,
                _ => None,
            };
        }

        // ── DrunkDeer ──────────────────────────────────────────────────────

        /// <summary>Columns per row of the DrunkDeer key grid.</summary>
        public const int DrunkDeerColumns = 21;

        private static readonly Dictionary<int, int> DrunkDeerTable = BuildDrunkDeer();

        private static Dictionary<int, int> BuildDrunkDeer()
        {
            var t = new Dictionary<int, int>();
            void Add(int row, int col, int code) => t[row * DrunkDeerColumns + col] = code;
            Add(0, 0, Escape); Add(0, 2, F1); Add(0, 3, F2); Add(0, 4, F3); Add(0, 5, F4);
            Add(0, 6, F5); Add(0, 7, F6); Add(0, 8, F7); Add(0, 9, F8); Add(0, 10, F9);
            Add(0, 11, F10); Add(0, 12, F11); Add(0, 13, F12); Add(0, 14, Delete);
            Add(1, 0, Backquote); Add(1, 1, D1); Add(1, 2, D2); Add(1, 3, D3); Add(1, 4, D4);
            Add(1, 5, D5); Add(1, 6, D6); Add(1, 7, D7); Add(1, 8, D8); Add(1, 9, D9);
            Add(1, 10, D0); Add(1, 11, Minus); Add(1, 12, EqualSign); Add(1, 13, Backspace);
            Add(1, 15, Home);
            Add(2, 0, Tab); Add(2, 1, Q); Add(2, 2, W); Add(2, 3, E); Add(2, 4, R);
            Add(2, 5, T); Add(2, 6, Y); Add(2, 7, U); Add(2, 8, I); Add(2, 9, O);
            Add(2, 10, P); Add(2, 11, BracketLeft); Add(2, 12, BracketRight);
            Add(2, 13, Backslash); Add(2, 15, PageUp);
            Add(3, 0, CapsLock); Add(3, 1, A); Add(3, 2, S); Add(3, 3, D); Add(3, 4, F);
            Add(3, 5, G); Add(3, 6, H); Add(3, 7, J); Add(3, 8, K); Add(3, 9, L);
            Add(3, 10, Semicolon); Add(3, 11, Quote); Add(3, 13, Enter); Add(3, 15, PageDown);
            Add(4, 0, LShift); Add(4, 2, Z); Add(4, 3, X); Add(4, 4, C); Add(4, 5, V);
            Add(4, 6, B); Add(4, 7, N); Add(4, 8, M); Add(4, 9, Comma); Add(4, 10, Period);
            Add(4, 11, Slash); Add(4, 13, RShift); Add(4, 14, ArrowUp); Add(4, 15, End);
            Add(5, 0, LCtrl); Add(5, 1, LMeta); Add(5, 2, LAlt); Add(5, 6, Space);
            Add(5, 10, RAlt); Add(5, 11, Fn);
            // The key says Menu on it. AnalogSense.js maps it to the context
            // menu usage and Soup to its first vendor key. The legend decides:
            // it is the context menu key.
            Add(5, 12, ContextMenu);
            Add(5, 14, ArrowLeft); Add(5, 15, ArrowDown); Add(5, 16, ArrowRight);
            return t;
        }

        /// <summary>A position in the DrunkDeer key grid (row * 21 + column)
        /// to the shared code, 0 for an empty cell.</summary>
        public static int DrunkDeerToCode(int index)
            => DrunkDeerTable.TryGetValue(index, out int code) ? code : None;

        // ── Bytech (Redragon K709 HE) ──────────────────────────────────────

        private static readonly Dictionary<int, int> BytechTable = new()
        {
            [1] = Escape, [2] = F1, [3] = F2, [4] = F3, [5] = F4, [6] = F5, [7] = F6,
            [8] = F7, [9] = F8, [10] = F9, [11] = F10, [12] = F11, [13] = F12,
            [14] = Backquote, [15] = D1, [16] = D2, [17] = D3, [18] = D4, [19] = D5,
            [20] = D6, [21] = D7, [22] = D8, [23] = D9, [24] = D0, [25] = Minus,
            [26] = EqualSign, [27] = Backspace, [28] = Tab, [29] = Q, [30] = W, [31] = E,
            [32] = R, [33] = T, [34] = Y, [35] = U, [36] = I, [37] = O, [38] = P,
            [39] = BracketLeft, [40] = BracketRight, [41] = Backslash, [42] = CapsLock,
            [43] = A, [44] = S, [45] = D, [46] = F, [47] = G, [48] = H, [49] = J,
            [50] = K, [51] = L, [52] = Semicolon, [53] = Quote, [54] = Enter,
            [55] = LShift, [56] = Z, [57] = X, [58] = C, [59] = V, [60] = B, [61] = N,
            [62] = M, [63] = Comma, [64] = Period, [65] = Slash, [66] = RShift,
            [67] = LCtrl, [68] = LMeta, [69] = LAlt, [70] = Space, [71] = RAlt, [72] = Fn,
            [73] = RCtrl, [74] = ArrowUp, [75] = ArrowDown, [76] = ArrowLeft,
            [77] = ArrowRight, [99] = Delete, [100] = Home, [102] = PageUp, [103] = PageDown,
        };

        /// <summary>A Bytech key position to the shared code, 0 when unknown.
        /// The "bytech" column of AnalogSense.js, the one reference for it.</summary>
        public static int BytechToCode(int position)
            => BytechTable.TryGetValue(position, out int code) ? code : None;

        // ── Polled layouts ─────────────────────────────────────────────────

        /// <summary>A polled keyboard's key matrix, row-major. Keychron's
        /// stock firmware addresses a key by row and column, and the
        /// AnalogSense firmware and the Madlions protocol answer in this order.</summary>
        public sealed class Layout
        {
            public Layout(int rows, int columns, int[] keys)
            {
                Rows = rows;
                Columns = columns;
                Keys = keys;
            }

            public int Rows { get; }
            public int Columns { get; }
            public int[] Keys { get; }
            public int Size => Keys.Length;
        }

        public static readonly Layout KeychronQ1He = new(6, 15, new[]
        {
            Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Delete, None,
            Backquote, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace, PageUp,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash, PageDown,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, Enter, Home, None,
            LShift, None, Z, X, C, V, B, N, M, Comma, Period, None, Slash, RShift, ArrowUp,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RMeta, Fn, RCtrl, ArrowLeft, ArrowDown, ArrowRight,
        });

        public static readonly Layout KeychronQ3He = new(6, 16, new[]
        {
            Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, PrintScreen, Oem1, Oem2,
            Backquote, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace, Insert, Home,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash, Delete, End,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, None, Enter, PageUp, PageDown,
            LShift, None, Z, X, C, V, B, N, M, Comma, Period, None, Slash, RShift, None, ArrowUp,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RAlt, RMeta, Fn, RCtrl, ArrowLeft, ArrowDown, ArrowRight,
        });

        public static readonly Layout KeychronQ5He = new(6, 19, new[]
        {
            Escape, None, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Delete, Oem1, Oem2, Oem3, None,
            Backquote, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace, PageUp, NumLock, NumpadDivide, NumpadMultiply, NumpadSubtract,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash, PageDown, Numpad7, Numpad8, Numpad9, NumpadAdd,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, Enter, Home, None, Numpad4, Numpad5, Numpad6, None,
            LShift, None, Z, X, C, V, B, N, M, Comma, Period, None, Slash, RShift, ArrowUp, Numpad1, Numpad2, Numpad3, NumpadEnter,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RMeta, Fn, RCtrl, ArrowLeft, ArrowDown, ArrowRight, None, Numpad0, NumpadDecimal, None,
        });

        public static readonly Layout KeychronK2He = new(6, 16, new[]
        {
            Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, PrintScreen, Delete, Oem2,
            Backquote, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace, PageUp, None,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash, PageDown, None,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, Enter, Home, None, None,
            LShift, None, Z, X, C, V, B, N, M, Comma, Period, Slash, RShift, ArrowUp, End, None,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RAlt, Fn, RCtrl, ArrowLeft, ArrowDown, ArrowRight, None,
        });

        public static readonly Layout LemokeyP1HeAnsi = new(6, 15, new[]
        {
            Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Delete, None,
            Backquote, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace, Home,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash, PageUp,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, Enter, PageDown, None,
            LShift, None, Z, X, C, V, B, N, M, Comma, Period, None, Slash, RShift, ArrowUp,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RMeta, Fn, RCtrl, ArrowLeft, ArrowDown, ArrowRight,
        });

        /// <summary>Soup only: AnalogSense.js lists the ANSI board alone.</summary>
        public static readonly Layout LemokeyP1HeIso = new(6, 15, new[]
        {
            Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Delete, None,
            Backquote, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace, Home,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Enter, PageUp,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, Backslash, PageDown, None,
            LShift, IntlBackslash, Z, X, C, V, B, N, M, Comma, Period, None, Slash, RShift, ArrowUp,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RMeta, Fn, RCtrl, ArrowLeft, ArrowDown, ArrowRight,
        });

        public static readonly Layout MadlionsMad60He = new(5, 14, new[]
        {
            Escape, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, None, Enter,
            LShift, None, Z, X, C, V, B, N, M, Comma, Period, Slash, None, RShift,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RMeta, RAlt, ContextMenu, RCtrl, Fn,
        });

        public static readonly Layout MadlionsMad68He = new(5, 15, new[]
        {
            Escape, D1, D2, D3, D4, D5, D6, D7, D8, D9, D0, Minus, EqualSign, Backspace, Insert,
            Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash, Delete,
            CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, None, Enter, PageUp,
            LShift, None, Z, X, C, V, B, N, M, Comma, Period, Slash, RShift, ArrowUp, PageDown,
            LCtrl, LMeta, LAlt, None, None, None, Space, None, None, RAlt, Fn, RCtrl, ArrowLeft, ArrowDown, ArrowRight,
        });

        /// <summary>The distinct codes a layout carries, in layout order.</summary>
        public static int[] KeysOf(Layout layout)
        {
            var seen = new HashSet<int>();
            var list = new List<int>();
            foreach (int code in layout.Keys)
                if (code != None && seen.Add(code)) list.Add(code);
            return list.ToArray();
        }
    }
}
