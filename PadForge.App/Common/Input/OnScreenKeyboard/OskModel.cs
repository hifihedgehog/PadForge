using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PadForge.Common.Input.OnScreenKeyboard
{
    /// <summary>How the On-Screen Keyboard is laid out and driven (#565).
    /// Saved by name in the settings file, so the names stay.</summary>
    public enum OskStyle
    {
        /// <summary>One selection moved across the whole keyboard, the layout
        /// Windows' gamepad keyboard and Steam's keyboard use.</summary>
        Grid = 0,
        /// <summary>The keyboard split in halves, a stick and a trigger for
        /// each (Wilson and Agrawala, CHI 2006).</summary>
        Split = 1,
        /// <summary>The daisy wheel: eight groups of four characters picked
        /// with the left stick and the face buttons (daisywheeljs).</summary>
        Wheel = 2,
    }

    /// <summary>A gamepad control by position in SDL's layout, for the
    /// hint bar's glyphs: a keyboard control bound to a device-free
    /// "Gamepad ..." input shows the controller's own label for it.</summary>
    public enum OskInput
    {
        None = 0,
        South = 1,
        East = 2,
        West = 3,
        North = 4,
        LeftShoulder = 5,
        RightShoulder = 6,
        LeftTrigger = 7,
        RightTrigger = 8,
        View = 9,
        Menu = 10,
        LeftStickClick = 11,
        RightStickClick = 12,
        DpadUp = 13,
        DpadDown = 14,
        DpadLeft = 15,
        DpadRight = 16,
        // Direction sources.
        LeftStick = 17,
        RightStick = 18,
        Dpad = 19,
    }

    /// <summary>The label set a controller's buttons go by, one per face
    /// style SDL_gamepad.c knows: ABXY, Sony, BAYX and AXBY.</summary>
    public enum OskFamily
    {
        Xbox = 0,
        PlayStation = 1,
        Nintendo = 2,
        /// <summary>A south, X east, B west, Y north, as on a GameCube pad.</summary>
        GameCube = 3,
    }

    /// <summary>Modifiers a key goes out with.</summary>
    [Flags]
    public enum OskMods
    {
        None = 0,
        Shift = 1,
        Ctrl = 2,
        Alt = 4,
        Win = 8,
        AltGr = 16,
    }

    /// <summary>What the engine takes from the keyboard menu that owns it
    /// (#566): the layout, the function row, word suggestions and the symbols
    /// page. The runtime builds a new one when the menu changes, so the engine
    /// never reads a half-made one. The window takes its place, size and
    /// opacity from the menu itself.</summary>
    public sealed class OskSettings
    {
        /// <summary>The corner grip's size range, in percent.</summary>
        public const int MinScale = 50, MaxScale = 200;

        public OskStyle Style { get; init; } = OskStyle.Grid;
        public bool FunctionRow { get; init; }
        /// <summary>Word suggestions from Windows' text prediction.</summary>
        public bool Suggestions { get; init; } = true;
        /// <summary>The symbols page's characters. Empty: the default set.</summary>
        public string Symbols { get; init; } = "";

        public static readonly OskSettings Default = new();
    }

    /// <summary>The text one physical key types at each level of a keyboard
    /// layout. A null level types nothing. <see cref="Dead"/> and the three
    /// after it mark the levels that are dead keys.</summary>
    public readonly record struct OskKeyText(string Base, string Shift, string AltGr, string ShiftAltGr, bool Dead = false,
        bool DeadShift = false, bool DeadAltGr = false, bool DeadShiftAltGr = false)
    {
        public bool IsEmpty => Base == null && Shift == null && AltGr == null && ShiftAltGr == null;

        /// <summary>True when the level is a dead key: Windows holds its
        /// accent and puts it on the next key typed. US-International's
        /// Shift+6 and Shift+' are dead, as are the base levels of its ` and '.</summary>
        public bool DeadAt(bool shift, bool altGr) => altGr ? (shift ? DeadShiftAltGr : DeadAltGr) : (shift ? DeadShift : Dead);

        /// <summary>True when Caps Lock picks the shifted level, which is the
        /// case for a letter whose shifted level is its capital.</summary>
        public bool CapsAffects => Base != null && Shift != null && Base.Length == 1 && Shift.Length == 1
            && char.IsLetter(Base[0]) && char.ToUpperInvariant(Base[0]) == Shift[0];
    }

    /// <summary>The keyboard layout of the window being typed into: the text
    /// each physical key types and Windows' Caps Lock state.</summary>
    public interface IOskLayout
    {
        /// <summary>The layout's name, for the keyboard's title bar.</summary>
        string Name { get; }
        /// <summary>The layout's language, for word suggestions ("en-US").</summary>
        string LanguageTag { get; }
        /// <summary>True when the layout has characters on its AltGr level.</summary>
        bool HasAltGr { get; }
        /// <summary>Windows' Caps Lock state.</summary>
        bool CapsOn { get; }
        /// <summary>The text of a physical key, by its set 1 scan code, with
        /// 0xE000 added for an extended key.</summary>
        OskKeyText Text(ushort scan);
    }

    /// <summary>Where keys go: the PC's input, or a test's record.</summary>
    public interface IOskOutput
    {
        /// <summary>Presses and releases one physical key with modifiers.
        /// <paramref name="scan"/> carries 0xE000 for an extended key.</summary>
        void TapKey(ushort scan, OskMods mods);
        /// <summary>Types text as Unicode characters.</summary>
        void TypeText(string text);
    }

    /// <summary>What kind of key a key on the board is.</summary>
    public enum OskKeyKind
    {
        /// <summary>A key of the Windows layout, typed by its scan code.</summary>
        Layout = 0,
        /// <summary>A character from the symbols page, typed as Unicode.</summary>
        Symbol = 1,
        /// <summary>A key with a job of its own (Backspace, Shift, an arrow).</summary>
        Special = 2,
        /// <summary>A word suggestion.</summary>
        Suggestion = 3,
    }

    public enum OskSpecial
    {
        None = 0,
        Backspace, Tab, CapsLock, Enter, Shift, Ctrl, Win, Alt, AltGr, Page, Space,
        Left, Up, Down, Right, Esc, Function, Delete, Home, End, PageUp, PageDown, Insert, PrintScreen,
        /// <summary>Shows or hides the function row.</summary>
        Fn,
        /// <summary>Switches to the next style: Grid, Split, Wheel.</summary>
        Layout,
    }

    /// <summary>One key on a board.</summary>
    public sealed class OskKey
    {
        public OskKeyKind Kind { get; init; }
        public OskSpecial Special { get; init; }
        /// <summary>Set 1 scan code, 0xE000 added for an extended key. 0 for
        /// a symbol or a suggestion.</summary>
        public ushort Scan { get; init; }
        /// <summary>Width in key units.</summary>
        public float W { get; init; } = 1;
        /// <summary>The character of a symbol key, or the word of a
        /// suggestion, or the name of a function key ("F5").</summary>
        public string Text { get; init; }
        /// <summary>The suggestion's index, 0 to 2.</summary>
        public int SuggestionIndex { get; init; }
        /// <summary>The layout's text for a layout key.</summary>
        public OskKeyText LayoutText { get; init; }

        public bool IsEnabled => Kind switch
        {
            OskKeyKind.Suggestion => !string.IsNullOrEmpty(Text),
            OskKeyKind.Symbol => !string.IsNullOrEmpty(Text),
            _ => true,
        };

        /// <summary>A key that keeps typing while held.</summary>
        public bool Repeats => Kind is OskKeyKind.Layout or OskKeyKind.Symbol
            || Special is OskSpecial.Backspace or OskSpecial.Space or OskSpecial.Left or OskSpecial.Right
                or OskSpecial.Up or OskSpecial.Down or OskSpecial.Delete or OskSpecial.Enter or OskSpecial.Tab;
    }

    /// <summary>
    /// The physical frame of the keyboard (#565): the main block of a PC
    /// keyboard by scan code, its rows aligned in columns so up and down move
    /// straight (Windows' gamepad keyboard aligns its keys the same way), and
    /// where each row splits for the Split style. The labels come from the
    /// layout in use, so the frame is the same for every language.
    /// </summary>
    public static class OskLayoutFrame
    {
        public const ushort Ext = 0xE000;

        /// <summary>The symbols page's characters, in reading order: 13, 13,
        /// 11 and 10, the character keys of the four rows.</summary>
        public static readonly string DefaultSymbols =
            "€£¥¢§¶©®™°±×÷" +
            "áéíóúñüçàèößø" +
            "…–" + (char)0x2014 + "«»“”‘’¿¡" +
            "½¼¾µ²³≠≈≤≥";

        /// <summary>The scan codes of the character keys of the four main
        /// rows, in order. The symbols page fills these places.</summary>
        internal static readonly ushort[][] CharRows =
        {
            new ushort[] { 0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D },
            new ushort[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x2B },
            new ushort[] { 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28 },
            new ushort[] { 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35 },
        };

        /// <summary>The ISO key beside left Shift, and the JIS keys.</summary>
        internal const ushort IsoKey = 0x56, JisYen = 0x7D, JisRo = 0x73;

        /// <summary>The last scan code of the left half, per main row (the
        /// keys the left hand types).</summary>
        private static readonly ushort[] SplitAfter = { 0x07, 0x14, 0x22, 0x30 };

        private static OskKey Sp(OskSpecial s, ushort scan, float w = 1, string text = null)
            => new() { Kind = OskKeyKind.Special, Special = s, Scan = scan, W = w, Text = text };

        /// <summary>The function row: Esc, F1 to F12 and Delete.</summary>
        internal static OskKey[] FunctionRow()
        {
            var keys = new List<OskKey> { Sp(OskSpecial.Esc, 0x01) };
            ushort[] f = { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58 };
            for (int i = 0; i < f.Length; i++)
                keys.Add(Sp(OskSpecial.Function, f[i], 1, "F" + (i + 1).ToString(CultureInfo.InvariantCulture)));
            keys.Add(Sp(OskSpecial.Delete, Ext | 0x53));
            return keys.ToArray();
        }

        /// <summary>The symbols page a menu asks for, without control
        /// characters, or the default set when it holds nothing else.</summary>
        public static string SymbolsOrDefault(string symbols)
        {
            if (string.IsNullOrWhiteSpace(symbols)) return DefaultSymbols;
            string v = new string(symbols.Where(c => !char.IsControl(c)).ToArray());
            return string.IsNullOrWhiteSpace(v) ? DefaultSymbols : v;
        }

        /// <summary>The page's rows. <paramref name="symbols"/> null builds
        /// the letters page from the layout. A string builds the symbols page,
        /// its characters in the character keys' places.</summary>
        public static List<OskKey[]> Rows(IOskLayout layout, string symbols, bool functionRow)
        {
            var rows = new List<OskKey[]>();
            if (functionRow) rows.Add(FunctionRow());
            int si = 0;
            OskKey Char(ushort scan)
            {
                if (symbols != null)
                {
                    string c = si < symbols.Length ? NextSymbol(symbols, ref si) : null;
                    return new OskKey { Kind = OskKeyKind.Symbol, Text = c };
                }
                return new OskKey { Kind = OskKeyKind.Layout, Scan = scan, LayoutText = layout?.Text(scan) ?? default };
            }
            // An extra key is drawn when the layout gives it something of its
            // own to type. Windows' US layouts give the ISO key the backslash
            // key's characters, for 102-key keyboards, and drawing it would
            // show a second backslash key.
            bool Has(ushort scan) => symbols == null && layout != null && !layout.Text(scan).IsEmpty
                && !CharRows.Any(row => row.Any(s => Duplicates(layout.Text(scan), layout.Text(s))));

            // Row 0: ` 1 to = (the JIS yen key when present), Backspace.
            var r0 = new List<OskKey>();
            foreach (var s in CharRows[0]) r0.Add(Char(s));
            if (Has(JisYen)) r0.Add(Char(JisYen));
            r0.Add(Sp(OskSpecial.Backspace, 0x0E));
            rows.Add(r0.ToArray());

            // Row 1: Tab, q to ], the backslash key (ISO's hash key sits here too).
            var r1 = new List<OskKey> { Sp(OskSpecial.Tab, 0x0F) };
            foreach (var s in CharRows[1]) r1.Add(Char(s));
            rows.Add(r1.ToArray());

            // Row 2: Caps Lock, a to ', Enter.
            var r2 = new List<OskKey> { Sp(OskSpecial.CapsLock, 0x3A) };
            foreach (var s in CharRows[2]) r2.Add(Char(s));
            r2.Add(Sp(OskSpecial.Enter, 0x1C, 2));
            rows.Add(r2.ToArray());

            // Row 3: Shift (the ISO key when present), z to /, (JIS ro), Shift.
            bool iso = Has(IsoKey);
            var r3 = new List<OskKey> { Sp(OskSpecial.Shift, 0x2A, iso ? 1 : 2) };
            if (iso) r3.Add(Char(IsoKey));
            foreach (var s in CharRows[3]) r3.Add(Char(s));
            if (Has(JisRo)) r3.Add(Char(JisRo));
            r3.Add(Sp(OskSpecial.Shift, 0x36, 2));
            rows.Add(r3.ToArray());

            // Row 4: Fn, Ctrl, Win, Alt, the page key, the style key, Space,
            // (AltGr), the arrows. Fn shows the function row, as the Windows
            // On-Screen Keyboard's Fn key does.
            bool altGr = layout?.HasAltGr == true;
            var r4 = new List<OskKey>
            {
                Sp(OskSpecial.Fn, 0), Sp(OskSpecial.Ctrl, 0x1D), Sp(OskSpecial.Win, Ext | 0x5B), Sp(OskSpecial.Alt, 0x38),
                Sp(OskSpecial.Page, 0), Sp(OskSpecial.Layout, 0), Sp(OskSpecial.Space, 0x39, altGr ? 3 : 4),
            };
            if (altGr) r4.Add(Sp(OskSpecial.AltGr, Ext | 0x38));
            r4.Add(Sp(OskSpecial.Left, Ext | 0x4B));
            r4.Add(Sp(OskSpecial.Up, Ext | 0x48));
            r4.Add(Sp(OskSpecial.Down, Ext | 0x50));
            r4.Add(Sp(OskSpecial.Right, Ext | 0x4D));
            rows.Add(r4.ToArray());
            return rows;
        }

        /// <summary>True when every character a key types, the twin types at
        /// the same level, dead or not alike.</summary>
        internal static bool Duplicates(OskKeyText key, OskKeyText twin)
        {
            static bool Same(string a, bool deadA, string b, bool deadB) => a == null || (a == b && deadA == deadB);
            return key.Base != null
                && Same(key.Base, key.Dead, twin.Base, twin.Dead)
                && Same(key.Shift, key.DeadShift, twin.Shift, twin.DeadShift)
                && Same(key.AltGr, key.DeadAltGr, twin.AltGr, twin.DeadAltGr)
                && Same(key.ShiftAltGr, key.DeadShiftAltGr, twin.ShiftAltGr, twin.DeadShiftAltGr);
        }

        /// <summary>One character of the symbols string, keeping a surrogate
        /// pair together.</summary>
        internal static string NextSymbol(string s, ref int i)
        {
            if (i >= s.Length) return null;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                string pair = s.Substring(i, 2);
                i += 2;
                return pair;
            }
            return s[i++].ToString();
        }

        /// <summary>Splits a page's rows into halves for the Split style. A
        /// main row splits after the left hand's last key. The bottom row
        /// splits its space bar in two, so either half can type a space.</summary>
        public static (List<OskKey[]> Left, List<OskKey[]> Right) SplitRows(List<OskKey[]> rows, bool functionRow)
        {
            var left = new List<OskKey[]>();
            var right = new List<OskKey[]>();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                int main = functionRow ? r - 1 : r;
                if (functionRow && r == 0)
                {
                    left.Add(row.Take(7).ToArray());
                    right.Add(row.Skip(7).ToArray());
                    continue;
                }
                if (main == 4)
                {
                    int space = Array.FindIndex(row, k => k.Special == OskSpecial.Space);
                    var sp = row[space];
                    float half = sp.W / 2f;
                    var l = row.Take(space).ToList();
                    l.Add(new OskKey { Kind = OskKeyKind.Special, Special = OskSpecial.Space, Scan = sp.Scan, W = (float)Math.Ceiling(half) });
                    var rr = new List<OskKey> { new OskKey { Kind = OskKeyKind.Special, Special = OskSpecial.Space, Scan = sp.Scan, W = (float)Math.Floor(half) } };
                    rr.AddRange(row.Skip(space + 1));
                    left.Add(l.ToArray());
                    right.Add(rr.ToArray());
                    continue;
                }
                ushort after = SplitAfter[Math.Clamp(main, 0, SplitAfter.Length - 1)];
                int cut = -1;
                for (int i = 0; i < row.Length; i++)
                    if (row[i].Kind != OskKeyKind.Special && ScanOfPlace(main, row, i) == after) { cut = i; break; }
                if (cut < 0) cut = row.Length / 2 - 1;
                left.Add(row.Take(cut + 1).ToArray());
                right.Add(row.Skip(cut + 1).ToArray());
            }
            return (left, right);
        }

        /// <summary>The scan code a character place stands for, on the
        /// letters page or the symbols page alike: the symbols page keeps the
        /// letters page's places.</summary>
        private static ushort ScanOfPlace(int mainRow, OskKey[] row, int index)
        {
            if (row[index].Kind == OskKeyKind.Layout) return row[index].Scan;
            // Count the character places before this one in the row.
            int n = 0;
            for (int i = 0; i < index; i++)
                if (row[i].Kind != OskKeyKind.Special) n++;
            var places = CharRows[Math.Clamp(mainRow, 0, CharRows.Length - 1)];
            // The ISO key sits before z on row 3 and takes no place of its own.
            return n < places.Length ? places[n] : (ushort)0;
        }

        /// <summary>
        /// The wheel's four sets: lowercase with punctuation, capitals with
        /// symbols, numbers with brackets, and the keys set. On a US layout
        /// the first three are daisywheeljs's sets character for character.
        /// Another layout fills the first two with its own letters in its
        /// language's order. Letters past the 32 places spill into the
        /// numbers set ahead of the digits.
        /// </summary>
        public static object[][] WheelSets(IOskLayout layout)
        {
            var lower = new List<string>();
            var upper = new List<string>();
            if (layout != null)
            {
                CultureInfo culture;
                try { culture = CultureInfo.GetCultureInfo(layout.LanguageTag ?? "en-US"); }
                catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
                // Every letter a key types at its base level: the letter keys,
                // and the accented letters a layout keeps elsewhere (AZERTY's
                // é è ç à on the number row). Its capital is the shifted level
                // when that is the capital, else the culture's capital, which
                // goes out as Unicode when no key types it.
                void Add(ushort scan)
                {
                    var t = layout.Text(scan);
                    if (t.Base == null || t.Base.Length != 1 || !char.IsLetter(t.Base[0]) || t.Dead) return;
                    if (lower.Contains(t.Base)) return;
                    // A script with no case (Hebrew, Arabic, Thai) keeps the
                    // same letter on both sets.
                    string cap = t.CapsAffects ? t.Shift : t.Base.ToUpper(culture);
                    lower.Add(t.Base);
                    upper.Add(cap);
                }
                foreach (var row in CharRows)
                    foreach (var scan in row) Add(scan);
                Add(IsoKey);
            }
            bool latin = lower.Count == 26 && lower.All(s => s[0] >= 'a' && s[0] <= 'z');
            if (lower.Count == 0 || latin)
            {
                return new object[][]
                {
                    Chars("abcdefghijklmnopqrstuvwxyz?!;\\&-"),
                    Chars("ABCDEFGHIJKLMNOPQRSTUVWXYZ+.@#$%"),
                    Chars("0123456789*,_=\"'()[]{}:~^<>|"),
                    KeysSet(),
                };
            }
            CompareInfo cmp;
            try { cmp = CultureInfo.GetCultureInfo(layout?.LanguageTag ?? "en-US").CompareInfo; }
            catch (CultureNotFoundException) { cmp = CultureInfo.InvariantCulture.CompareInfo; }
            var order = Enumerable.Range(0, lower.Count).OrderBy(i => lower[i], Comparer<string>.Create((a, b) => cmp.Compare(a, b))).ToList();
            var lo = order.Select(i => lower[i]).ToList();
            var up = order.Select(i => upper[i]).ToList();
            var spillLo = lo.Skip(32).ToList();
            var spillUp = up.Skip(32).ToList();
            lo = lo.Take(32).ToList();
            up = up.Take(32).ToList();
            foreach (var p in new[] { "?", "!", ".", ",", "'", "-" }) if (lo.Count < 32) lo.Add(p);
            foreach (var p in new[] { "@", "#", "$", "%", "+", "=" }) if (up.Count < 32) up.Add(p);
            var nums = new List<string>();
            nums.AddRange(spillLo);
            nums.AddRange(spillUp);
            nums.AddRange(Chars("0123456789*,_=\"'()[]{}:~^<>|").Cast<string>());
            return new object[][]
            {
                lo.Cast<object>().ToArray(),
                up.Cast<object>().ToArray(),
                nums.Take(32).Cast<object>().ToArray(),
                KeysSet(),
            };
        }

        private static object[] Chars(string s) => s.Select(c => (object)c.ToString()).ToArray();

        /// <summary>The wheel's keys set: modifiers, navigation, Esc, Tab,
        /// Delete, Caps Lock, F1 to F12 and the style key, in groups of four.
        /// Enter has no place here, since Menu types it in this style.</summary>
        private static object[] KeysSet() => new object[]
        {
            Sp(OskSpecial.Ctrl, 0x1D), Sp(OskSpecial.Alt, 0x38), Sp(OskSpecial.Win, Ext | 0x5B), Sp(OskSpecial.Shift, 0x2A),
            Sp(OskSpecial.Home, Ext | 0x47), Sp(OskSpecial.PageUp, Ext | 0x49), Sp(OskSpecial.End, Ext | 0x4F), Sp(OskSpecial.PageDown, Ext | 0x51),
            Sp(OskSpecial.Left, Ext | 0x4B), Sp(OskSpecial.Up, Ext | 0x48), Sp(OskSpecial.Right, Ext | 0x4D), Sp(OskSpecial.Down, Ext | 0x50),
            Sp(OskSpecial.Esc, 0x01), Sp(OskSpecial.Tab, 0x0F), Sp(OskSpecial.Delete, Ext | 0x53), Sp(OskSpecial.CapsLock, 0x3A),
            Sp(OskSpecial.Function, 0x3B, 1, "F1"), Sp(OskSpecial.Function, 0x3C, 1, "F2"), Sp(OskSpecial.Function, 0x3D, 1, "F3"), Sp(OskSpecial.Function, 0x3E, 1, "F4"),
            Sp(OskSpecial.Function, 0x3F, 1, "F5"), Sp(OskSpecial.Function, 0x40, 1, "F6"), Sp(OskSpecial.Function, 0x41, 1, "F7"), Sp(OskSpecial.Function, 0x42, 1, "F8"),
            Sp(OskSpecial.Function, 0x43, 1, "F9"), Sp(OskSpecial.Function, 0x44, 1, "F10"), Sp(OskSpecial.Function, 0x57, 1, "F11"), Sp(OskSpecial.Function, 0x58, 1, "F12"),
            Sp(OskSpecial.Insert, Ext | 0x52), Sp(OskSpecial.PrintScreen, Ext | 0x37), Sp(OskSpecial.Layout, 0), Sp(OskSpecial.Backspace, 0x0E),
        };
    }
}
