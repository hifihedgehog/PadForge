using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A key table comes from a plugin's three lists, its names, its places and its LEDs,
    /// and a list that falls out of step puts keys on other keys' LEDs with nothing to show for it.
    /// Frames do not show it, since each light still gets a color. Each key's place against its
    /// neighbors' does: Q sits left of W on one row, and the rows run down in order.</summary>
    public class KeyTableOrderTests
    {
        // HID keyboard page usages, by the key in a US layout.
        private static ushort Letter(char c) => (ushort)(0x04 + (c - 'A'));
        private static ushort Digit(char c) => (ushort)(c == '0' ? 0x27 : 0x1E + (c - '1'));
        private const ushort Enter = 0x28, Esc = 0x29, Backspace = 0x2A, Tab = 0x2B, Space = 0x2C, Minus = 0x2D, Equal = 0x2E,
            LeftBracket = 0x2F, RightBracket = 0x30, Semicolon = 0x33, Quote = 0x34, Comma = 0x36, Dot = 0x37, Slash = 0x38,
            Caps = 0x39, F1 = 0x3A, LeftCtrl = 0xE0, LeftShift = 0xE1, RightShift = 0xE5;

        private static IEnumerable<(ushort Left, ushort Right)> Runs()
        {
            foreach (string row in new[] { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" })
                for (int i = 0; i + 1 < row.Length; i++) yield return (Letter(row[i]), Letter(row[i + 1]));
            string digits = "1234567890";
            for (int i = 0; i + 1 < digits.Length; i++) yield return (Digit(digits[i]), Digit(digits[i + 1]));
            for (int i = 0; i < 11; i++) yield return ((ushort)(F1 + i), (ushort)(F1 + i + 1));
            yield return (Esc, F1);
            yield return (Digit('0'), Minus);
            yield return (Minus, Equal);
            yield return (Tab, Letter('Q'));
            yield return (Letter('P'), LeftBracket);
            yield return (LeftBracket, RightBracket);
            yield return (Caps, Letter('A'));
            yield return (Letter('L'), Semicolon);
            yield return (Semicolon, Quote);
            yield return (Letter('M'), Comma);
            yield return (Comma, Dot);
            yield return (Dot, Slash);
            yield return (Slash, RightShift);
            yield return (LeftShift, Letter('Z'));
            yield return (LeftCtrl, Space);
        }

        private static IEnumerable<(ushort Upper, ushort Lower)> Columns()
        {
            yield return (Digit('1'), Letter('Q'));
            yield return (Letter('Q'), Letter('A'));
            yield return (Letter('A'), Letter('Z'));
            yield return (Letter('Z'), Space);
            yield return (F1, Digit('2'));
        }

        internal static IEnumerable<string> Faults(IReadOnlyList<LightInfo> lights)
        {
            var at = new Dictionary<ushort, LightInfo>();
            foreach (var light in lights)
                if (light.Kind == LightKind.Key && light.HasPosition && !at.ContainsKey(light.Usage)) at[light.Usage] = light;
            foreach (var (left, right) in Runs())
            {
                if (!at.TryGetValue(left, out var a) || !at.TryGetValue(right, out var b)) continue;
                // An Alice board bends its rows, so a key on the next row may sit over its neighbor.
                bool inOrder = a.Y == b.Y ? a.X < b.X : Math.Abs(a.Y - b.Y) <= 1 && a.X <= b.X;
                if (!inOrder) yield return $"{left:X2} at {a.X},{a.Y} and {right:X2} at {b.X},{b.Y}";
            }
            foreach (var (upper, lower) in Columns())
            {
                if (!at.TryGetValue(upper, out var a) || !at.TryGetValue(lower, out var b)) continue;
                if (a.Y >= b.Y) yield return $"{upper:X2} at {a.X},{a.Y} over {lower:X2} at {b.X},{b.Y}";
            }
            // One key, two lights apart: a list out of step, or a number pad's key named as the number row's.
            foreach (var same in lights.Where(l => l.Kind == LightKind.Key && l.HasPosition && l.Usage != 0 && l.Usage != Space).GroupBy(l => l.Usage))
            {
                var two = same.OrderBy(l => l.X).ToList();
                if (two.Count < 2) continue;
                bool wide = two[0].Y == two[^1].Y && two[^1].X - two[0].X <= 3;
                bool tall = two[0].X == two[^1].X && Math.Abs(two[^1].Y - two[0].Y) <= 2;
                if (!wide && !tall) yield return $"{same.Key:X2} at {two[0].X},{two[0].Y} and {two[^1].X},{two[^1].Y}";
            }
            // On a keyboard, Tab, Caps Lock and the left shift start their rows: any key left of one
            // is out of step. A number pad puts Tab where it likes.
            foreach (ushort first in new[] { Tab, Caps, LeftShift })
            {
                if (!at.ContainsKey(Letter('Q')) || !at.TryGetValue(first, out var start)) continue;
                var before = lights.FirstOrDefault(l => l.Kind == LightKind.Key && l.HasPosition && l.Usage != 0 && l.Y == start.Y && l.X < start.X);
                if (before.Usage != 0) yield return $"{before.Usage:X2} at {before.X},{before.Y} before {first:X2} at {start.X},{start.Y}";
            }
        }

        /// <summary>Each set of lights a plugin's lists give. A board whose keys come from its
        /// firmware is left out: its firmware's keymap says what each key is, and a split or
        /// staggered board breaks no rule by not having rows.</summary>
        internal static IEnumerable<(string Name, IReadOnlyList<LightInfo> Lights)> AllLists()
        {
            foreach (var device in PluginDeviceLighting.Devices)
            {
                bool fromFirmware = PluginQmkTables.Boards.Any(b => b.Firmware != null && b.Plugin == device.Plugin && b.Products.Contains(device.ProductId));
                if (!fromFirmware && device.Source == null && device.Lights != null) yield return ($"{device.Name} {device.ProductId:X4} ({device.Plugin})", device.Lights);
            }
            foreach (var board in PluginQmkTables.Boards)
                foreach (var (unique, variant) in board.Variants ?? Array.Empty<(byte?, Func<PluginKeyTable>)>())
                    yield return ($"{board.Name} {unique}", PluginKeyNames.Lights(variant()));
            foreach (var field in typeof(PluginLibraryTables).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                if (field.GetValue(null) is PluginBoard[] boards)
                    foreach (var board in boards) yield return ($"{field.Name} {board.Model}", PluginKeyNames.Lights(board.Table()));
        }

        /// <summary>The key picker draws a board's lights at their places, so no two share one.</summary>
        [Fact]
        public void AKeyboardsFirmwareTable_GivesEveryLightItsOwnPlace()
        {
            var boards = PluginQmkTables.Boards.Where(b => b.Firmware != null).ToList();
            Assert.True(boards.Count > 200, boards.Count.ToString());
            foreach (var board in boards)
            {
                var table = board.Table();
                var places = table.X.Zip(table.Y, (x, y) => (x, y)).ToList();
                Assert.True(places.Distinct().Count() == places.Count, board.Firmware);
                Assert.Equal(Enumerable.Range(0, table.Indexes.Length), table.Indexes);
            }
        }

        [Fact]
        public void EveryKeyList_KeepsItsKeysInKeyboardOrder()
        {
            var faults = new List<string>();
            foreach (var (name, lights) in AllLists())
            {
                var found = Faults(lights).ToList();
                if (found.Count > 0) faults.Add($"{name}: {found.Count}: {string.Join("; ", found.Take(4))}");
            }
            Assert.True(faults.Count == 0, faults.Count + " lists:\n" + string.Join("\n", faults));
        }
    }
}
