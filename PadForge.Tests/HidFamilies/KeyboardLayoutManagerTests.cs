using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The port of OpenRGB's KeyboardLayoutManager and the tables generated
    /// from the fifteen families that use it (#557). The main test holds
    /// every layout a family builds, in every region its code can pick, to
    /// the dump OpenRGB's own KeyboardLayoutManager.cpp wrote for it when
    /// compiled with the families' own tables (tools/openrgb_klm), key by
    /// key. The rest pin each of the algorithm's rules with the outcome read
    /// from KeyboardLayoutManager.cpp at the pinned commit, never from the
    /// port.
    /// </summary>
    public class KeyboardLayoutManagerTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static KeyboardLed K(int row, int col, string name, KeyboardOpcode op, uint value = 0)
            => new(0, (byte)row, (byte)col, value, name, "", op);

        private static string Names(KeyboardLayoutManager k) => string.Join(",", k.Keys.Select(x => x.Name));

        private static string Places(KeyboardLayoutManager k) => string.Join(",", k.Keys.Select(x => $"{x.Name}@{x.Row}.{x.Col}"));

        // ── The dump ──

        private sealed class DumpCase
        {
            public string Family, Recipe, Name;
            public KeyboardLayout Layout;
            public int Rows, Cols, Count;
            public List<(int Row, int Col, uint Value, string Name, string Alt)> Keys = new();
            public int MapHeight, MapWidth;
            public long[] Map;
        }

        private static (string Commit, List<DumpCase> Cases, List<string[]> Models) ReadDump()
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "openrgb_klm", "klm_dump.tsv"), new UTF8Encoding(false));
            string commit = null;
            var cases = new List<DumpCase>();
            var models = new List<string[]>();
            DumpCase current = null;
            foreach (var line in lines)
            {
                var p = line.Split('\t');
                switch (p[0])
                {
                    case "openrgb":
                        commit = p[1];
                        break;
                    case "case":
                        current = new DumpCase
                        {
                            Family = p[1], Recipe = p[2], Layout = Enum.Parse<KeyboardLayout>(p[3]),
                            Rows = int.Parse(p[4]), Cols = int.Parse(p[5]), Count = int.Parse(p[6]), Name = p[7],
                        };
                        cases.Add(current);
                        break;
                    case "map":
                        current.MapHeight = int.Parse(p[1]);
                        current.MapWidth = int.Parse(p[2]);
                        current.Map = p.Skip(3).Select(long.Parse).ToArray();
                        break;
                    case "model":
                        models.Add(p.Skip(1).ToArray());
                        break;
                    default:
                        current.Keys.Add((int.Parse(p[0]), int.Parse(p[1]), uint.Parse(p[2]), p[3], p[4]));
                        break;
                }
            }
            return (commit, cases, models);
        }

        /// <summary>Every recipe in every region its family can build it in
        /// gives OpenRGB's own key list, row, column, value, name and
        /// alternate name of every key, its row and column counts, its size
        /// name, and its Count key map.</summary>
        [Fact]
        public void EveryLayoutAFamilyBuilds_MatchesOpenRgbsOwnBuild()
        {
            var (commit, cases, _) = ReadDump();
            Assert.Equal(KeyboardLayouts.OpenRgbCommit, commit);

            var expected = KeyboardLayouts.Recipes.SelectMany(r => r.Layouts.Select(l => (r.Name, l))).ToList();
            var dumped = cases.Select(c => (c.Recipe, c.Layout)).ToList();
            Assert.Equal(expected.Count, expected.Distinct().Count());
            Assert.Equal(expected.OrderBy(x => x.Name).ThenBy(x => x.l), dumped.OrderBy(x => x.Recipe).ThenBy(x => x.Layout));

            var failures = new List<string>();
            int keysCompared = 0;
            foreach (var c in cases)
            {
                var recipe = KeyboardLayouts.Recipe(c.Recipe);
                string at = $"{c.Recipe} {c.Layout}";
                if (recipe.Family != c.Family) failures.Add($"{at}: family {recipe.Family}, OpenRGB {c.Family}");
                var k = recipe.Build(c.Layout);
                if (k.GetKeyCount() != c.Count) failures.Add($"{at}: {k.GetKeyCount()} keys, OpenRGB {c.Count}");
                if (k.GetRowCount() != c.Rows || k.GetColumnCount() != c.Cols)
                    failures.Add($"{at}: {k.GetRowCount()}x{k.GetColumnCount()}, OpenRGB {c.Rows}x{c.Cols}");
                if (k.GetName() != c.Name) failures.Add($"{at}: name '{k.GetName()}', OpenRGB '{c.Name}'");
                for (int i = 0; i < Math.Min(k.GetKeyCount(), c.Keys.Count); i++)
                {
                    var mine = k.Keys[i];
                    var theirs = c.Keys[i];
                    keysCompared++;
                    if (mine.Row != theirs.Row || mine.Col != theirs.Col || mine.Value != theirs.Value
                        || mine.Name != theirs.Name || k.GetKeyAltNameAt(i) != theirs.Alt)
                        failures.Add($"{at} key {i}: {mine.Name}@{mine.Row}.{mine.Col}={mine.Value} '{mine.AltName}', "
                                     + $"OpenRGB {theirs.Name}@{theirs.Row}.{theirs.Col}={theirs.Value} '{theirs.Alt}'");
                }
                var map = k.GetKeyMap(KeyboardMapFill.Count);
                var mapCells = map.Map.Select(v => v == KeyboardLayoutManager.NoKey ? -1L : v).ToArray();
                if (map.Height != c.MapHeight || map.Width != c.MapWidth || !mapCells.SequenceEqual(c.Map))
                    failures.Add($"{at}: key map differs");
            }
            Assert.True(failures.Count == 0, failures.Count + " differences:\n" + string.Join("\n", failures.Take(25)));
            Assert.True(keysCompared > 30000, "compared only " + keysCompared + " keys");
        }

        /// <summary>The model rows of the families that keep their devices
        /// in tables (Razer, Cooler Master, Corsair, ASUS Aura Core and
        /// Sinowealth) are the rows the harness read out of those tables
        /// through the compiler.</summary>
        [Fact]
        public void TheModelRowsOfTableFamilies_MatchOpenRgbsOwnTables()
        {
            var (_, _, dumped) = ReadDump();
            var families = new HashSet<string> { "Razer", "CoolerMaster", "CorsairPeripheralV2", "AsusAuraCore", "Sinowealth" };
            string Row(KeyboardLayoutModel m) => string.Join("|", m.Family, m.Model, m.ProductId, m.Key, m.Recipe, m.Zone, m.Rows, m.Cols);
            var mine = KeyboardLayouts.Models.Where(m => families.Contains(m.Family)).Select(Row).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var theirs = dumped.Select(p => string.Join("|", p)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(theirs, mine);
        }

        // ── The tables ──

        /// <summary>Each enum value matches OpenRGB's, by name
        /// (KeyboardLayoutManager.h:28-73, read by the generator), and every
        /// member of the port's enums has one.</summary>
        [Fact]
        public void TheEnums_MatchOpenRgbs()
        {
            static string Pascal(string s) => string.Concat(s.Split('_').Select(w => w[..1] + w[1..].ToLowerInvariant()));
            var seen = new HashSet<string>();
            foreach (var (name, value) in KeyboardLayouts.OpenRgbEnums)
            {
                (Type type, string member) = name switch
                {
                    _ when name.StartsWith("KEYBOARD_ZONE_") => (typeof(KeyboardSize), "Zone" + Pascal(name["KEYBOARD_ZONE_".Length..])),
                    _ when name.StartsWith("KEYBOARD_SIZE_") => (typeof(KeyboardSize), Pascal(name["KEYBOARD_SIZE_".Length..])),
                    _ when name.StartsWith("KEYBOARD_LAYOUT_") => (typeof(KeyboardLayout), Pascal(name["KEYBOARD_LAYOUT_".Length..])),
                    _ when name.StartsWith("KEYBOARD_OPCODE_") => (typeof(KeyboardOpcode), Pascal(name["KEYBOARD_OPCODE_".Length..])),
                    _ when name.StartsWith("KEYBOARD_MAP_FILL_TYPE_") => (typeof(KeyboardMapFill), Pascal(name["KEYBOARD_MAP_FILL_TYPE_".Length..])),
                    _ => throw new InvalidOperationException(name),
                };
                Assert.True(Enum.TryParse(type, member, out var parsed), $"{name} has no {type.Name}.{member}");
                Assert.Equal(value, Convert.ToInt32(parsed));
                seen.Add(type.Name + "." + member);
            }
            foreach (var type in new[] { typeof(KeyboardSize), typeof(KeyboardLayout), typeof(KeyboardOpcode), typeof(KeyboardMapFill) })
                foreach (var member in Enum.GetNames(type))
                    Assert.Contains(type.Name + "." + member, seen);
        }

        /// <summary>The base blocks have OpenRGB's key counts: 63 main keys
        /// (KeyboardLayoutManager.cpp:63-125), 13 in the F-key row
        /// (133-145), 13 navigation keys (153-165) and 17 on the number pad
        /// (173-189).</summary>
        [Fact]
        public void TheBaseBlocks_HoldOpenRgbsKeyCounts()
        {
            Assert.Equal(63, KeyboardLayouts.ZoneMain.Length);
            Assert.Equal(13, KeyboardLayouts.ZoneFnRow.Length);
            Assert.Equal(13, KeyboardLayouts.ZoneExtras.Length);
            Assert.Equal(17, KeyboardLayouts.ZoneNumpad.Length);
            Assert.Equal(new KeyboardLed(0, 0, 0, 0, "Key: Escape", "", KeyboardOpcode.InsertShiftRight), KeyboardLayouts.ZoneFnRow[0]);
            Assert.Equal(new KeyboardLed(0, 5, 19, 0, "Key: Number Pad .", "", KeyboardOpcode.InsertShiftRight), KeyboardLayouts.ZoneNumpad[^1]);
        }

        /// <summary>Every array a recipe points at is in place when the
        /// recipes are built: static fields start in the order the file
        /// writes them, so an array written after the recipe table would
        /// still be null there.</summary>
        [Fact]
        public void EveryRecipe_HasItsTablesInPlace()
        {
            foreach (var recipe in KeyboardLayouts.Recipes)
            {
                Assert.NotNull(recipe.DefaultValues);
                Assert.NotNull(recipe.RegionalOverlay);
                Assert.All(recipe.RegionalOverlay, o => Assert.NotNull(o.Keys));
                Assert.NotNull(recipe.Edits);
                Assert.All(recipe.Edits, e => Assert.NotNull(e));
                Assert.NotEmpty(recipe.Layouts);
            }
            Assert.Contains(KeyboardLayouts.Recipes, r => r.DefaultValues.Length > 0);
        }

        /// <summary>Every model row names a recipe, and recipe names are
        /// unique.</summary>
        [Fact]
        public void EveryModelRow_NamesARecipe()
        {
            Assert.Equal(KeyboardLayouts.Recipes.Length, KeyboardLayouts.Recipes.Select(r => r.Name).Distinct().Count());
            foreach (var model in KeyboardLayouts.Models)
                Assert.NotNull(KeyboardLayouts.Recipe(model.Recipe));
            Assert.Null(KeyboardLayouts.Recipe("no such layout"));
            Assert.Null(KeyboardLayouts.Build("no such layout", KeyboardLayout.AnsiQwerty));
        }

        /// <summary>Rows read from the sources: the BlackWidow 2019,
        /// 1532:0241, a 6 by 22 Keyboard zone (RazerDevices.h:39,
        /// RazerDevices.cpp:1291-1317). The Tartarus V2, 1532:022B, a 4 by 6
        /// Keypad (RazerDevices.h:138, RazerDevices.cpp:7923-7929). The MK
        /// Pro S, 2516:003C (CMKeyboardDevices.h:102). The K57 RGB wired,
        /// 1B1C:1B6E, a 7 by 22 zone (CorsairPeripheralV2Devices.h:72,
        /// CorsairPeripheralV2Devices.cpp:882-897). The Kreo Swarm, model
        /// byte 0xBE (SinowealthKeyboard10cDevices.cpp:301). The G810,
        /// C331, its strip at full size when it does not enumerate, and the
        /// G Pro, C339, at TKL (RGBController_LogitechHIDPP20.cpp:869 and
        /// 877).</summary>
        [Fact]
        public void ModelRows_ReadFromTheSources()
        {
            Assert.Contains(new KeyboardLayoutModel("Razer", "Razer BlackWidow 2019", 0x0241, "", "razer_blackwidow_2019_layout", "Keyboard", 6, 22), KeyboardLayouts.Models);
            Assert.Contains(new KeyboardLayoutModel("Razer", "Razer Tartarus V2", 0x022B, "", "razer_tartarus_v2_layout", "Keypad", 4, 6), KeyboardLayouts.Models);
            Assert.Contains(new KeyboardLayoutModel("CoolerMaster", "mk_pro_s_device", 0x003C, "", "mk_pro_s_layout", "Keyboard", 0, 0), KeyboardLayouts.Models);
            Assert.Contains(new KeyboardLayoutModel("CorsairPeripheralV2", "k57_rgb_wired_device", 0x1B6E, "", "corsair_K57_layout", "Keyboard", 7, 22), KeyboardLayouts.Models);
            Assert.Contains(new KeyboardLayoutModel("Sinowealth", "Kreo Swarm", -1, "0xBE", "kreo_swarm75_layout", "", 0, 0), KeyboardLayouts.Models);
            Assert.Contains(new KeyboardLayoutModel("Logitech", "HID++ 0x8080 keyboard", 0xC331, "not enumerated", "logitech_8080_full_g810_top_strip", "", 0, 0), KeyboardLayouts.Models);
            Assert.Contains(new KeyboardLayoutModel("Logitech", "HID++ 0x8080 keyboard", 0xC339, "not enumerated", "logitech_8080_tkl_gpro_top_strip", "", 0, 0), KeyboardLayouts.Models);
            Assert.Equal("razer_blackwidow_2019_layout", KeyboardLayouts.ModelsOf("Razer", 0x0241).Single().Recipe);
        }

        /// <summary>The regions each family's code can pick: Razer AZERTY,
        /// ISO, QWERTZ or ANSI, a JIS board built as ANSI
        /// (RGBController_Razer.cpp:236-256). Cooler Master Default, ANSI,
        /// ISO or JIS (CMKeyboardV2Controller.cpp:35-80). Corsair ISO, JIS or
        /// ANSI (RGBController_CorsairV2Hardware.cpp:95-110). The VSG
        /// Mintaka ISO alone (RGBController_MintakaKeyboard.cpp:255), the
        /// HYTE keyboard Default alone (RGBController_HYTEKeyboard.cpp:89),
        /// the HyperX Alloy Origins Core ANSI or ABNT2
        /// (RGBController_HyperXAlloyOriginsCore.cpp:154-165).</summary>
        [Fact]
        public void EachRecipe_KeepsTheRegionsItsFamilyCanPick()
        {
            KeyboardLayout[] Of(string name) => KeyboardLayouts.Recipe(name).Layouts.OrderBy(l => l).ToArray();
            Assert.Equal(new[] { KeyboardLayout.AnsiQwerty, KeyboardLayout.IsoQwerty, KeyboardLayout.IsoQwertz, KeyboardLayout.IsoAzerty }, Of("razer_huntsman_v2_layout"));
            Assert.Equal(new[] { KeyboardLayout.Default, KeyboardLayout.AnsiQwerty, KeyboardLayout.IsoQwerty, KeyboardLayout.Jis }, Of("ck552_layout"));
            Assert.Equal(new[] { KeyboardLayout.AnsiQwerty, KeyboardLayout.IsoQwerty, KeyboardLayout.Jis }, Of("corsair_k70_layout"));
            Assert.Equal(new[] { KeyboardLayout.IsoQwerty }, Of("mintaka_offset_values"));
            Assert.Equal(new[] { KeyboardLayout.Default }, Of("hyte_keeb_tkl_layout"));
            Assert.Equal(new[] { KeyboardLayout.AnsiQwerty, KeyboardLayout.Abnt2 }, Of("hyperx_core_layout"));
        }

        // ── The algorithm, rule by rule ──

        /// <summary>The size names (KeyboardLayoutManager.cpp:25-28 and
        /// 434-464), "Size (n) " for any other mix of blocks, and the class
        /// name for an empty board, which returns before naming (line 331).</summary>
        [Fact]
        public void EachSize_HasOpenRgbsName()
        {
            Assert.Equal("Full 104 key ", new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Full).GetName());
            Assert.Equal("Tenkeyless ", new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Tkl).GetName());
            Assert.Equal("Seventy Five percent ", new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.SeventyFive).GetName());
            Assert.Equal("Sixty percent ", new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Sixty).GetName());
            Assert.Equal("Size (9) ", new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.ZoneMain | KeyboardSize.ZoneNumpad).GetName());
            Assert.Equal("KLM", new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty).GetName());
        }

        /// <summary>An empty board has no keys and no rows or columns, since
        /// its constructor returns before the dimensions are worked out
        /// (line 331). An edit list brings them up to date, and an empty key
        /// list then gives one row and one column (lines 1082-1104).</summary>
        [Fact]
        public void AnEmptyBoard_HasNoDimensionsUntilItsFirstEdit()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.AnsiQwerty, KeyboardSize.Empty);
            Assert.Equal(0, k.GetKeyCount());
            Assert.Equal(0, k.GetRowCount());
            Assert.Equal(0, k.GetColumnCount());
            k.ChangeKeys(Array.Empty<KeyboardLed>());
            Assert.Equal(1, k.GetRowCount());
            Assert.Equal(1, k.GetColumnCount());
        }

        /// <summary>A 60% board drops the empty F-key row and swaps Escape
        /// into the backquote's place with the F-key block's value for it, 0
        /// (lines 436-443 and 133), not the value list's first entry, 19 for
        /// the VSG Mintaka (RGBController_MintakaKeyboard.cpp:25).</summary>
        [Fact]
        public void ASixtyPercentBoard_TakesEscapeWithValueZero()
        {
            var k = KeyboardLayouts.Build("mintaka_offset_values", KeyboardLayout.IsoQwerty);
            var escape = k.Keys[0];
            Assert.Equal(("Key: Escape", 0, 0, 0u), (escape.Name, (int)escape.Row, (int)escape.Col, escape.Value));
            Assert.DoesNotContain(k.Keys, x => x.Name == "Key: `");
            Assert.Equal(20u, k.Keys[1].Value);
        }

        /// <summary>A swap with no key at its place inserts its key one place
        /// before the first key past it (lines 697-713): B at column 5 goes in
        /// ahead of A at column 0, not between A and C.</summary>
        [Fact]
        public void ASwapIntoAGap_LandsOnePlaceEarly()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 0, "A", KeyboardOpcode.SwapOnly),
                K(0, 9, "C", KeyboardOpcode.InsertShiftRight),
                K(0, 5, "B", KeyboardOpcode.SwapOnly),
            });
            Assert.Equal("B@0.5,A@0.0,C@0.9", Places(k));
        }

        /// <summary>A swap past every key adds nothing (lines 660-717), and a
        /// swap of the unused key removes the key at its place (674-677).</summary>
        [Fact]
        public void ASwapPastTheEnd_AddsNothing_AndAnUnusedSwapRemoves()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 0, "A", KeyboardOpcode.SwapOnly),
                K(0, 4, "B", KeyboardOpcode.InsertShiftRight),
                K(3, 3, "Z", KeyboardOpcode.SwapOnly),
                K(0, 0, "", KeyboardOpcode.SwapOnly),
            });
            Assert.Equal("B@0.4", Places(k));
        }

        /// <summary>A swap onto a key renames it and gives it the new value,
        /// keeping its alternate name (lines 682-686).</summary>
        [Fact]
        public void ASwapOntoAKey_TakesNameAndValueOnly()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                new KeyboardLed(0, 0, 0, 1, "A", "alt", KeyboardOpcode.SwapOnly),
                new KeyboardLed(3, 0, 0, 9, "B", "other", KeyboardOpcode.SwapOnly),
            });
            Assert.Equal(new KeyboardLed(0, 0, 0, 9, "B", "alt", KeyboardOpcode.SwapOnly), k.Keys.Single());
        }

        /// <summary>An insert at a key moves it and the rest of its row one
        /// column right, and an unused insert moves them without adding a
        /// key (lines 548-616).</summary>
        [Fact]
        public void AnInsert_ShiftsTheRestOfItsRow()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 0, "A", KeyboardOpcode.SwapOnly),
                K(0, 1, "B", KeyboardOpcode.InsertShiftRight),
                K(1, 0, "D", KeyboardOpcode.InsertShiftRight),
                K(0, 1, "X", KeyboardOpcode.InsertShiftRight),
                K(0, 0, "", KeyboardOpcode.InsertShiftRight),
            });
            Assert.Equal("A@0.1,X@0.2,B@0.3,D@1.0", Places(k));
        }

        /// <summary>An insert past the end adds its key whatever it is, the
        /// unused key too, and moves nothing (lines 574-579).</summary>
        [Fact]
        public void AnInsertPastTheEnd_AddsEvenAnUnusedKey()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 3, "A", KeyboardOpcode.SwapOnly),
                K(1, 0, "", KeyboardOpcode.InsertShiftRight),
            });
            Assert.Equal("A@0.3,@1.0", Places(k));
        }

        /// <summary>A remove at a key takes it out and moves the rest of its
        /// row one column left. With no key there it moves the keys past the
        /// place left and takes nothing out (lines 744-787).</summary>
        [Fact]
        public void ARemove_ShiftsTheRestOfItsRowLeft()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 0, "A", KeyboardOpcode.SwapOnly),
                K(0, 3, "B", KeyboardOpcode.InsertShiftRight),
                K(0, 6, "C", KeyboardOpcode.InsertShiftRight),
            });
            k.ChangeKeys(new[] { K(0, 1, "", KeyboardOpcode.RemoveShiftLeft) });
            Assert.Equal("A@0.0,B@0.2,C@0.5", Places(k));
            k.ChangeKeys(new[] { K(0, 2, "", KeyboardOpcode.RemoveShiftLeft) });
            Assert.Equal("A@0.0,C@0.4", Places(k));
        }

        /// <summary>Row and column are bytes (KeyboardLayoutManager.h:78-79).
        /// A column moved left from 0 wraps to 255, and 255 plus one columns
        /// wraps to 0 (lines 758 and 1103-1104). The out-of-order list a gap
        /// swap leaves sets it up.</summary>
        [Fact]
        public void AColumnBelowZero_WrapsAsAByte()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 0, "A", KeyboardOpcode.SwapOnly),
                K(0, 9, "C", KeyboardOpcode.InsertShiftRight),
                K(0, 5, "B", KeyboardOpcode.SwapOnly),
            });
            k.ChangeKeys(new[] { K(0, 5, "", KeyboardOpcode.RemoveShiftLeft) });
            Assert.Equal("A@0.255,C@0.8", Places(k));
            Assert.Equal(0, k.GetColumnCount());
        }

        /// <summary>A row insert checks the row count of the last update,
        /// which comes only at the end of an edit list (lines 795-799 and
        /// 540): a row the list itself has just added is not there yet, so
        /// C is never inserted.</summary>
        [Fact]
        public void ARowInsert_SeesTheRowCountOfTheLastList()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[] { K(0, 0, "A", KeyboardOpcode.SwapOnly) });
            k.ChangeKeys(new[]
            {
                K(1, 0, "B", KeyboardOpcode.InsertShiftRight),
                K(1, 0, "C", KeyboardOpcode.InsertRow),
            });
            Assert.Equal("A@0.0,B@1.0", Places(k));

            k.ChangeKeys(new[] { K(1, 0, "C", KeyboardOpcode.InsertRow) });
            Assert.Equal("C@1.0,A@0.0,B@2.0", Places(k));
        }

        /// <summary>A row remove takes out the row's keys and moves every
        /// key below it up a row (lines 845-871).</summary>
        [Fact]
        public void ARowRemove_MovesTheRowsBelowUp()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 0, "A", KeyboardOpcode.SwapOnly),
                K(1, 0, "B", KeyboardOpcode.InsertShiftRight),
                K(1, 1, "B2", KeyboardOpcode.InsertShiftRight),
                K(2, 0, "C", KeyboardOpcode.InsertShiftRight),
            });
            k.ChangeKeys(new[] { K(1, 0, "", KeyboardOpcode.RemoveRow) });
            Assert.Equal("A@0.0,C@1.0", Places(k));
            k.ChangeKeys(new[] { K(5, 0, "", KeyboardOpcode.RemoveRow) });
            Assert.Equal("A@0.0,C@1.0", Places(k));
        }

        /// <summary>An alternate name goes on the key at the place and
        /// nothing else changes (lines 886-900). The adjacent insert is a
        /// to-do in OpenRGB and does nothing (lines 514-517).</summary>
        [Fact]
        public void AnAltName_LandsOnItsKey_AndAnAdjacentInsertDoesNothing()
        {
            var k = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Empty);
            k.ChangeKeys(new[]
            {
                K(0, 0, "A", KeyboardOpcode.SwapOnly),
                K(0, 1, "B", KeyboardOpcode.InsertShiftRight),
                new KeyboardLed(0, 0, 1, 7, "ignored", "Key: ß", KeyboardOpcode.AddAltName),
                new KeyboardLed(0, 0, 4, 7, "ignored", "nowhere", KeyboardOpcode.AddAltName),
                K(0, 0, "Q", KeyboardOpcode.InsShftAdjacent),
            });
            Assert.Equal("A@0.0,B@0.1", Places(k));
            Assert.Equal("Key: ß", k.GetKeyAltNameAt(1));
            Assert.Equal(0u, k.Keys[1].Value);
            Assert.Equal("", k.GetKeyAltNameAt(0));
        }

        /// <summary>A family's regional fix list is swapped in key by key
        /// whatever its opcodes say (lines 422-429 and 720-731): an insert
        /// there renames the key at its place and moves nothing. The fixes
        /// for another region are left alone, and of two lists for one
        /// region the first counts, as std::map keeps it.</summary>
        [Fact]
        public void ARegionalFixList_IsSwappedIn()
        {
            var plain = new KeyboardLayoutManager(KeyboardLayout.AnsiQwerty, KeyboardSize.Tkl);
            var k = new KeyboardLayoutManager(KeyboardLayout.AnsiQwerty, KeyboardSize.Tkl, Array.Empty<uint>(), new[]
            {
                (KeyboardLayout.IsoQwerty, new[] { K(1, 3, "Key: ISO", KeyboardOpcode.SwapOnly, 5) }),
                (KeyboardLayout.AnsiQwerty, new[] { K(1, 1, "Key: X", KeyboardOpcode.InsertShiftRight, 7) }),
                (KeyboardLayout.AnsiQwerty, new[] { K(1, 1, "Key: Y", KeyboardOpcode.SwapOnly, 8) }),
            });
            Assert.Equal(plain.GetKeyCount(), k.GetKeyCount());
            Assert.Equal("Key: X", k.GetKeyNameAt(1, 1));
            Assert.Equal(7u, k.GetKeyValueAt(1, 1));
            Assert.Equal("Key: 2", k.GetKeyNameAt(1, 2));
            Assert.Equal("Key: 3", k.GetKeyNameAt(1, 3));
        }

        /// <summary>The value list fills the keys in key-list order before
        /// the region's changes (lines 364-367): on the AOC board Escape
        /// takes 90, F1 92 and F2 77 (RGBController_AOCKeyboard.cpp:33-34).
        /// A value list longer than the key list stops at its end.</summary>
        [Fact]
        public void TheValueList_FillsTheKeysInOrder()
        {
            var k = KeyboardLayouts.Build("aoc_keyboard_offset_values", KeyboardLayout.AnsiQwerty);
            Assert.Equal(90u, k.GetKeyValueAt(0, 0));
            Assert.Equal(92u, k.GetKeyValueAt(0, 2));
            Assert.Equal(77u, k.GetKeyValueAt(0, 3));

            var many = Enumerable.Range(1, 500).Select(i => (uint)i).ToArray();
            var sixty = new KeyboardLayoutManager(KeyboardLayout.Default, KeyboardSize.Sixty, many, null);
            Assert.Equal(2u, sixty.GetKeyValueAt(0, 1));
        }

        /// <summary>The key map (lines 1012-1075): NoKey where no key sits,
        /// the key's index for Count, the cell's index for Index, the key's
        /// value for Value, a key outside the asked size left out, and the
        /// layout's own size where 0 is asked. Reads past the end give "" and
        /// (unsigned)-1 (lines 923-990).</summary>
        [Fact]
        public void TheKeyMap_FillsEachCellByItsType()
        {
            var k = KeyboardLayouts.Build("aoc_keyboard_offset_values", KeyboardLayout.AnsiQwerty);
            Assert.Equal(new uint[] { 0, KeyboardLayoutManager.NoKey, 1 }, k.GetKeyMap(KeyboardMapFill.Count, 1, 3).Map);
            Assert.Equal(new uint[] { 90, KeyboardLayoutManager.NoKey, 92 }, k.GetKeyMap(KeyboardMapFill.Value, 1, 3).Map);
            Assert.Equal(new uint[] { 0, KeyboardLayoutManager.NoKey, 2, 3 }, k.GetKeyMap(KeyboardMapFill.Index, 2, 2).Map);
            var whole = k.GetKeyMap();
            Assert.Equal((6, 21), (whole.Height, whole.Width));
            Assert.Equal((uint)(5 * 21 + 19), whole.Map[5 * 21 + 19]);
            Assert.Equal(KeyboardLayoutManager.NoKey, whole.Map[5 * 21 + 20]);
            Assert.Equal("", k.GetKeyNameAt(k.GetKeyCount()));
            Assert.Equal("", k.GetKeyNameAt(-1));
            Assert.Equal(KeyboardLayoutManager.NoKey, k.GetKeyValueAt(k.GetKeyCount()));
            Assert.Equal("", k.GetKeyNameAt(9, 9));
            Assert.Equal(KeyboardLayoutManager.NoKey, k.GetKeyValueAt(9, 9));
            Assert.Equal("", k.GetKeyAltNameAt(9, 9));
        }

        /// <summary>A recipe applies its edit lists after the constructor, in
        /// order (the families' SetupZones): Mountain's no-numpad list puts
        /// "Edge 1" in a new top row and later inserts "Edge 44" ahead of it,
        /// which moves it one column right
        /// (RGBController_Mountain60Keyboard.cpp:75, 92 and 359-360).</summary>
        [Fact]
        public void ARecipe_AppliesItsEditListsAfterTheConstructor()
        {
            var recipe = KeyboardLayouts.Recipe("mountain60_layout");
            Assert.Single(recipe.Edits);
            var k = recipe.Build(KeyboardLayout.AnsiQwerty);
            var plain = new KeyboardLayoutManager(KeyboardLayout.AnsiQwerty, KeyboardSize.Sixty, recipe.DefaultValues, recipe.RegionalOverlay);
            Assert.DoesNotContain(plain.Keys, x => x.Name == "Edge 1");
            Assert.Equal("Edge 44", k.GetKeyNameAt(0, 0));
            Assert.Equal("Edge 1", k.GetKeyNameAt(0, 1));
            Assert.Equal(126u, k.GetKeyValueAt(0, 1));
        }

        // ── Lights ──

        /// <summary>A key OpenRGB names with a keyboard-page usage is a key
        /// light from a generated table, placed at its row and column. A
        /// G-key is a labeled light with its number. Any other light is an
        /// Other light for the family to sort.</summary>
        [Fact]
        public void Lights_CarryTheKeyUsageAndPlace()
        {
            var k = KeyboardLayouts.Build("logitech_8081_full_g915x_strip_and_gkeys", KeyboardLayout.AnsiQwerty);
            var lights = k.Lights((i, key) => 1000 + i);
            Assert.Equal(k.GetKeyCount(), lights.Count);
            int a = k.Keys.ToList().FindIndex(x => x.Name == "Key: A");
            var light = lights[a];
            Assert.Equal((1000 + a, LightKind.Key, (ushort)0x04, LightKeySource.Generated), (light.Address, light.Kind, light.Usage, light.KeySource));
            Assert.Equal((k.Keys[a].Col, k.Keys[a].Row, 1f, 1f, true), ((int)light.X, (int)light.Y, light.Width, light.Height, light.HasPosition));
            var g7 = lights[k.Keys.ToList().FindIndex(x => x.Name == "G7")];
            Assert.Equal((LightKind.Other, LightLabel.GKey, 7, (ushort)0), (g7.Kind, g7.Label, g7.LabelNumber, g7.Usage));
            var logo = lights[k.Keys.ToList().FindIndex(x => x.Name == "Logo")];
            Assert.Equal((LightKind.Other, LightLabel.None, LightKeySource.None), (logo.Kind, logo.Label, logo.KeySource));
            Assert.Equal((uint)186, k.Keys.First(x => x.Name == "G7").Value);
        }

        /// <summary>The names the families' edit lists add resolve to labels:
        /// G-keys with their numbers (CorsairPeripheralV2Devices.cpp:67-72,
        /// RGBController_LogitechHIDPP20.cpp:443-451), Logitech's strip lights
        /// (lines 423-456 and 794-830) and Cooler Master's lock lights
        /// (CMKeyboardDevices.cpp:675-677).</summary>
        [Fact]
        public void FamilyLightNames_ResolveToLabels()
        {
            (ushort, LightLabel, int) Of(string name)
            {
                Assert.True(OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number), name);
                return (usage, label, number);
            }
            Assert.Equal(((ushort)0, LightLabel.GKey, 7), Of("Key: G7"));
            Assert.Equal(((ushort)0, LightLabel.GKey, 1), Of("G1"));
            Assert.Equal(((ushort)0, LightLabel.GKey, 18), Of("Key: G18"));
            Assert.Equal(((ushort)0, LightLabel.Brightness, 0), Of("Lighting"));
            Assert.Equal(((ushort)0, LightLabel.Brightness, 0), Of("Brightness"));
            Assert.Equal(((ushort)0, LightLabel.PlayPause, 0), Of("Media Play/Pause"));
            Assert.Equal(((ushort)0, LightLabel.Previous, 0), Of("Media Previous"));
            Assert.Equal(((ushort)0, LightLabel.Next, 0), Of("Media Next"));
            Assert.Equal(((ushort)0, LightLabel.Mute, 0), Of("Media Mute"));
            Assert.Equal(((ushort)0, LightLabel.GameMode, 0), Of("Game Mode"));
            Assert.Equal(((ushort)0, LightLabel.GameMode, 0), Of("Game Mode Indicator"));
            Assert.Equal(((ushort)0, LightLabel.NumLock, 0), Of("Num Lock Indicator"));
            Assert.Equal(((ushort)0, LightLabel.CapsLock, 0), Of("Caps Lock Indicator"));
            Assert.Equal(((ushort)0, LightLabel.ScrollLock, 0), Of("Scroll Lock Indicator"));
            Assert.Equal(((ushort)0, LightLabel.NumLock, 0), Of("Indicator: N"));
            Assert.Equal(((ushort)0, LightLabel.CapsLock, 0), Of("Indicator: C"));
            Assert.Equal(((ushort)0, LightLabel.ScrollLock, 0), Of("Indicator: S"));
            Assert.Equal(((ushort)0x04, LightLabel.None, 0), Of("Key: A"));
            foreach (var name in new[] { "G", "G0", "G01", "Gx", "Key: G1234", "Key: M1", "Logo", null })
                Assert.False(OpenRgbKeys.TryGet(name, out _, out _, out _), name ?? "null");
            Assert.True(OpenRgbKeys.TryGet("Key: G3", out ushort u, out var l));
            Assert.Equal(((ushort)0, LightLabel.GKey), (u, l));
        }

        /// <summary>
        /// The key names in OpenRGB's layouts that name no keyboard-page key
        /// and no label, written out so a new one fails here until someone
        /// decides it. The Spanish and Nordic legends depend on the layout
        /// they sit in (OpenRgbKeys' own rule). Volume, the M and P macro
        /// keys, the Aim, Fan, ROG and dial keys have no keyboard-page usage
        /// and no LightLabel. The Tartarus keypad's numbered keys and the
        /// Skyloong's split space halves name a position, not a code.
        /// </summary>
        [Fact]
        public void TheKeyNamesWithNoUsageOrLabel_AreTheKnownOnes()
        {
            var unresolved = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var recipe in KeyboardLayouts.Recipes)
                foreach (var layout in recipe.Layouts)
                    foreach (var key in recipe.Build(layout).Keys)
                        if (key.Name.StartsWith("Key: ", StringComparison.Ordinal) && !OpenRgbKeys.TryGet(key.Name, out _, out _))
                            unresolved.Add(key.Name);
            var expected = new SortedSet<string>(StringComparer.Ordinal)
            {
                "Key: +", "Key: - _", "Key: < >", "Key: ¿/¡", "Key: ´/¨", "Key: Ñ",
                "Key: Media Volume", "Key: Media Volume +", "Key: Media Volume -",
                "Key: M1", "Key: M2", "Key: M3", "Key: M4", "Key: M5", "Key: M6",
                "Key: P1", "Key: P2", "Key: P3", "Key: P4",
                "Key: Aim +", "Key: Aim -", "Key: Aim <|>", "Key: Fan", "Key: ROG", "Key: Dial", "Key: Scroll Wheel",
                "Key: Cylinder?!", "Key: Left Space", "Key: Right Space",
            };
            for (int i = 1; i <= 20; i++) expected.Add($"Key: {i:00}");
            Assert.Equal(expected, unresolved);
        }
    }
}
