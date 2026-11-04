using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>The blocks of a keyboard and the sizes made of them
    /// (KEYBOARD_ZONE_BITS and KEYBOARD_SIZE, KeyboardLayoutManager.h:28-44).
    /// A size outside the four named ones is any mix of the blocks.</summary>
    [Flags]
    internal enum KeyboardSize
    {
        Empty = 0,
        ZoneMain = 1 << 0,
        ZoneFnRow = 1 << 1,
        ZoneExtra = 1 << 2,
        ZoneNumpad = 1 << 3,
        Full = ZoneMain | ZoneFnRow | ZoneExtra | ZoneNumpad,
        Tkl = ZoneMain | ZoneFnRow | ZoneExtra,
        SeventyFive = ZoneMain | ZoneFnRow,
        Sixty = ZoneMain,
    }

    /// <summary>KEYBOARD_LAYOUT, KeyboardLayoutManager.h:46-55.</summary>
    internal enum KeyboardLayout
    {
        Default = 0,
        AnsiQwerty = 1,
        IsoQwerty = 2,
        IsoQwertz = 3,
        IsoAzerty = 4,
        Jis = 5,
        Abnt2 = 6,
    }

    /// <summary>KEYBOARD_OPCODE, KeyboardLayoutManager.h:64-73: what an edit
    /// does with its key.</summary>
    internal enum KeyboardOpcode
    {
        InsertShiftRight = 0,
        SwapOnly = 1,
        RemoveShiftLeft = 2,
        /// <summary>Does nothing: OpenRGB leaves it as a to-do
        /// (KeyboardLayoutManager.cpp:514-517).</summary>
        InsShftAdjacent = 3,
        InsertRow = 4,
        RemoveRow = 5,
        AddAltName = 6,
    }

    /// <summary>KEYBOARD_MAP_FILL_TYPE, KeyboardLayoutManager.h:57-62: what
    /// a cell of <see cref="KeyboardLayoutManager.GetKeyMap(KeyboardMapFill, byte, byte)"/>
    /// holds.</summary>
    internal enum KeyboardMapFill
    {
        /// <summary>The key's index in the key list.</summary>
        Count = 0,
        /// <summary>The cell's own index, row times width plus column.</summary>
        Index = 1,
        /// <summary>The key's value.</summary>
        Value = 2,
    }

    /// <summary>One key of a layout or an edit (keyboard_led,
    /// KeyboardLayoutManager.h:75-84). <see cref="Value"/> is what the
    /// family puts there, most often the LED's index in its frames. An
    /// empty <see cref="Name"/> is OpenRGB's unused key.</summary>
    internal readonly record struct KeyboardLed(byte Zone, byte Row, byte Col, uint Value, string Name, string AltName, KeyboardOpcode Opcode)
    {
        /// <summary>The usage of the typing-block key whose place a regional
        /// fix gave this key's name, or 0 (<see cref="KeyboardLayoutManager.TryResolve"/>).
        /// Not OpenRGB's: OpenRGB keeps names only.</summary>
        public ushort PlaceUsage { get; init; }
    }

    /// <summary>A key map (matrix_map_type): <see cref="Height"/> rows of
    /// <see cref="Width"/> cells, row by row, <see cref="KeyboardLayoutManager.NoKey"/>
    /// where no key sits.</summary>
    internal sealed record KeyboardMatrix(int Height, int Width, uint[] Map);

    /// <summary>
    /// A port of OpenRGB's KeyboardLayoutManager
    /// (KeyboardLayoutManager/KeyboardLayoutManager.cpp at
    /// <see cref="KeyboardLayouts.OpenRgbCommit"/>, 1,105 lines), which
    /// builds a keyboard's key list from a size, a region, a list of LED
    /// values, regional fixes and edit lists (#557). Fifteen OpenRGB
    /// families build their keyboards with it, and the key a light sits
    /// under comes from it for every one of them, so this port keeps
    /// OpenRGB's behavior exactly, quirks included, and a test holds it to a
    /// dump of OpenRGB's own code for every layout those families build
    /// (tools/openrgb_klm).
    ///
    /// <para>The quirks it keeps: row and column are bytes, so a column
    /// shifted left from 0 wraps to 255 and the dimensions wrap past 255. A
    /// swap that finds no key where it points inserts its key one place
    /// before where it belongs (lines 706-713). <c>rows</c>, which
    /// <see cref="InsertRow"/> and <see cref="RemoveRow"/> check against, is
    /// brought up to date only at the end of an edit list, not between its
    /// edits. A regional fix list is swapped in key by key whatever its
    /// opcodes say (lines 422-429). A 60% board's Escape takes the value 0
    /// from the F-key block, not the value list's first entry (lines
    /// 436-443).</para>
    ///
    /// <para>Two reads in OpenRGB run past the end of the key list when a
    /// row is inserted or removed below every key (lines 815-819 and 863).
    /// That is undefined behavior there and never happens for a layout a
    /// family builds (the harness checks every subscript). Here the read
    /// finds no key and nothing moves.</para>
    /// </summary>
    internal sealed class KeyboardLayoutManager
    {
        /// <summary>A map cell with no key: (unsigned int)-1 (line 1016).</summary>
        public const uint NoKey = uint.MaxValue;

        private readonly List<KeyboardLed> _keymap = new();
        private byte _rows;
        private byte _cols;
        private string _name = "KLM";

        /// <summary>The size the layout was built with.</summary>
        public KeyboardSize PhysicalSize { get; }

        /// <summary>The keys in OpenRGB's order, which is the order a family
        /// numbers its LEDs in.</summary>
        public IReadOnlyList<KeyboardLed> Keys => _keymap;

        /// <summary>KeyboardLayoutManager(layout, size), with no values
        /// (lines 317-319).</summary>
        public KeyboardLayoutManager(KeyboardLayout layout, KeyboardSize size)
            : this(layout, size, Array.Empty<uint>(), Array.Empty<(KeyboardLayout, KeyboardLed[])>())
        {
        }

        /// <summary>KeyboardLayoutManager(layout, size, values), lines
        /// 321-472. <paramref name="defaultValues"/> and
        /// <paramref name="regionalOverlay"/> are layout_values. Of two
        /// overlays for one region the first counts, as std::map keeps it.</summary>
        public KeyboardLayoutManager(KeyboardLayout layout, KeyboardSize size, uint[] defaultValues,
                                     (KeyboardLayout Layout, KeyboardLed[] Keys)[] regionalOverlay)
        {
            PhysicalSize = size;
            if (size == KeyboardSize.Empty)
                return;

            if ((size & KeyboardSize.ZoneMain) != 0) InsertKeys(KeyboardLayouts.ZoneMain);
            if ((size & KeyboardSize.ZoneFnRow) != 0) InsertKeys(KeyboardLayouts.ZoneFnRow);
            if ((size & KeyboardSize.ZoneExtra) != 0) InsertKeys(KeyboardLayouts.ZoneExtras);
            if ((size & KeyboardSize.ZoneNumpad) != 0) InsertKeys(KeyboardLayouts.ZoneNumpad);

            defaultValues ??= Array.Empty<uint>();
            for (int i = 0; i < defaultValues.Length && i < _keymap.Count; i++)
                _keymap[i] = _keymap[i] with { Value = defaultValues[i] };

            switch (layout)
            {
                case KeyboardLayout.AnsiQwerty: ChangeKeys(KeyboardLayouts.AnsiQwertyOverlay); break;
                case KeyboardLayout.IsoQwerty: ChangeKeys(KeyboardLayouts.IsoQwertyOverlay); break;
                case KeyboardLayout.IsoAzerty: ChangeKeys(KeyboardLayouts.IsoAzertyOverlay); break;
                case KeyboardLayout.IsoQwertz: ChangeKeys(KeyboardLayouts.IsoQwertzOverlay); break;
                case KeyboardLayout.Jis: ChangeKeys(KeyboardLayouts.JisOverlay); break;
                case KeyboardLayout.Abnt2: ChangeKeys(KeyboardLayouts.Abnt2Overlay); break;
            }

            if (regionalOverlay != null)
                foreach (var (overlayLayout, keys) in regionalOverlay)
                    if (overlayLayout == layout)
                    {
                        foreach (var key in keys) SwapRegionalKey(key);
                        break;
                    }

            switch (size)
            {
                case KeyboardSize.Sixty:
                    _name = "Sixty percent ";
                    RemoveRow(0);
                    SwapKey(KeyboardLayouts.ZoneFnRow[0]);
                    break;
                case KeyboardSize.SeventyFive: _name = "Seventy Five percent "; break;
                case KeyboardSize.Tkl: _name = "Tenkeyless "; break;
                case KeyboardSize.Full: _name = "Full 104 key "; break;
                default: _name = "Size (" + ((int)size).ToString(System.Globalization.CultureInfo.InvariantCulture) + ") "; break;
            }

            UpdateDimensions();
        }

        /// <summary>ChangeKeys (lines 479-492): each edit by its opcode, then
        /// the dimensions brought up to date (OpCodeSwitch, lines 494-541).
        /// ChangeKeys on an overlay uses its edit list alone.</summary>
        public void ChangeKeys(IReadOnlyList<KeyboardLed> editKeys)
        {
            foreach (var key in editKeys)
            {
                switch (key.Opcode)
                {
                    case KeyboardOpcode.InsertShiftRight: InsertKey(key); break;
                    case KeyboardOpcode.SwapOnly: SwapKey(key); break;
                    case KeyboardOpcode.RemoveShiftLeft: RemoveKey(key); break;
                    case KeyboardOpcode.InsShftAdjacent: break;
                    case KeyboardOpcode.InsertRow:
                        if (InsertRow(key.Row)) SwapKey(key);
                        break;
                    case KeyboardOpcode.RemoveRow: RemoveRow(key.Row); break;
                    case KeyboardOpcode.AddAltName: AddAltName(key); break;
                }
            }
            UpdateDimensions();
        }

        /// <summary>Lines 543-617. The key goes in before the first key at or
        /// after its place, row first, and the keys after it on its row move
        /// one column right. An unused key in the middle adds nothing and
        /// moves the keys. A key past the end is added as it is, unused or
        /// not, and moves nothing.</summary>
        private void InsertKey(KeyboardLed key)
        {
            int idx = 0;
            for (; idx < _keymap.Count; idx++)
                if (key.Row < _keymap[idx].Row || (key.Row == _keymap[idx].Row && key.Col <= _keymap[idx].Col))
                    break;

            bool updateRow = true;
            if (idx == _keymap.Count)
            {
                _keymap.Add(key);
                updateRow = false;
            }
            else if (key.Name.Length == 0)
            {
            }
            else
            {
                _keymap.Insert(idx, key);
                idx++;
            }

            if (!updateRow)
                return;
            for (; idx < _keymap.Count; idx++)
            {
                if (_keymap[idx].Row == key.Row && _keymap[idx].Col >= key.Col)
                    _keymap[idx] = _keymap[idx] with { Col = unchecked((byte)(_keymap[idx].Col + 1)) };
                if (_keymap[idx].Row > key.Row)
                    break;
            }
        }

        /// <summary>Lines 619-635.</summary>
        private void InsertKeys(KeyboardLed[] keys)
        {
            foreach (var key in keys) InsertKey(key);
            UpdateDimensions();
        }

        /// <summary>Lines 637-718. The key at the place takes the new name
        /// and value, or goes when the new key is unused. With no key there,
        /// a used key is inserted one place before the first key past it
        /// (OpenRGB's own off-by-one, lines 706-713), and a key past every
        /// key is not added. An empty list takes the key whatever it is.</summary>
        private void SwapKey(KeyboardLed key)
        {
            if (_keymap.Count == 0)
            {
                _keymap.Add(key);
                return;
            }

            for (int idx = 0; idx < _keymap.Count; idx++)
            {
                if (key.Row == _keymap[idx].Row && key.Col == _keymap[idx].Col)
                {
                    if (key.Name.Length == 0)
                        _keymap.RemoveAt(idx);
                    else
                        _keymap[idx] = _keymap[idx] with { Name = key.Name, Value = key.Value };
                    break;
                }

                if (_keymap[idx].Row > key.Row || (_keymap[idx].Row == key.Row && _keymap[idx].Col > key.Col))
                {
                    if (key.Name.Length != 0)
                        _keymap.Insert(idx == 0 ? 0 : idx - 1, key);
                    break;
                }
            }
        }

        /// <summary>A key of a regional fix list, swapped in as OpenRGB does
        /// (lines 422-429, <see cref="SwapKey"/>). Where it takes the place
        /// of a typing-block key (<see cref="OpenRgbKeys.IsTypingUsage"/>),
        /// it keeps that key's usage beside its new name: the fix renames
        /// the key to the legend the region prints on it, and a key's usage
        /// names its place, not its legend. The Mintaka's Spanish fix names
        /// "Key: ¿/¡" where Key: = sits and "Key: Ñ" where Key: ; sits
        /// (RGBController_MintakaKeyboard.cpp:41-56), names OpenRGB gives no
        /// usage, and "Key: [" where the ' key sits, whose name alone would
        /// give the usage of another place.</summary>
        private void SwapRegionalKey(KeyboardLed key)
        {
            ushort place = 0;
            foreach (var existing in _keymap)
            {
                if (existing.Row != key.Row || existing.Col != key.Col) continue;
                place = existing.PlaceUsage;
                if (place == 0 && OpenRgbKeys.TryGet(existing.Name, out ushort usage, out _) && OpenRgbKeys.IsTypingUsage(usage))
                    place = usage;
                break;
            }
            SwapKey(key);
            if (place == 0 || key.Name.Length == 0) return;
            for (int idx = 0; idx < _keymap.Count; idx++)
                if (_keymap[idx].Row == key.Row && _keymap[idx].Col == key.Col)
                {
                    _keymap[idx] = _keymap[idx] with { PlaceUsage = place };
                    break;
                }
        }

        /// <summary>Lines 733-788. The key at the place goes, or with no key
        /// there the gap does, and the keys after it on its row move one
        /// column left.</summary>
        private void RemoveKey(KeyboardLed key)
        {
            for (int idx = 0; idx < _keymap.Count; idx++)
            {
                if (key.Row == _keymap[idx].Row && key.Col == _keymap[idx].Col)
                {
                    _keymap.RemoveAt(idx);
                    ShiftLeft(idx, key.Row);
                    break;
                }

                if (key.Row == _keymap[idx].Row && key.Col < _keymap[idx].Col)
                {
                    ShiftLeft(idx, key.Row);
                    break;
                }
            }
        }

        private void ShiftLeft(int idx, byte row)
        {
            for (; idx < _keymap.Count; idx++)
            {
                if (_keymap[idx].Row != row)
                    break;
                _keymap[idx] = _keymap[idx] with { Col = unchecked((byte)(_keymap[idx].Col - 1)) };
            }
        }

        /// <summary>Lines 790-829: every key from the row down moves one row
        /// down. False, and nothing moves, for a row at or past the row count
        /// of the last update.</summary>
        private bool InsertRow(byte row)
        {
            if (row >= _rows)
                return false;

            int idx = 0;
            for (; idx < _keymap.Count; idx++)
                if (row <= _keymap[idx].Row)
                    break;

            if (idx < _keymap.Count && row <= _keymap[idx].Row)
                for (; idx < _keymap.Count; idx++)
                    _keymap[idx] = _keymap[idx] with { Row = unchecked((byte)(_keymap[idx].Row + 1)) };
            return true;
        }

        /// <summary>Lines 831-872: the row's keys go and every key below
        /// moves one row up. Nothing happens for a row at or past the row
        /// count of the last update.</summary>
        private void RemoveRow(byte row)
        {
            if (row >= _rows)
                return;

            int idx = 0;
            while (idx < _keymap.Count && row >= _keymap[idx].Row)
            {
                if (row == _keymap[idx].Row)
                    _keymap.RemoveAt(idx);
                else
                    idx++;
            }

            if (idx < _keymap.Count && row < _keymap[idx].Row)
                for (; idx < _keymap.Count; idx++)
                    _keymap[idx] = _keymap[idx] with { Row = unchecked((byte)(_keymap[idx].Row - 1)) };
        }

        /// <summary>Lines 874-901: the alternate name of the key at the
        /// place, if one is there.</summary>
        private void AddAltName(KeyboardLed key)
        {
            for (int idx = 0; idx < _keymap.Count; idx++)
            {
                if (key.Row == _keymap[idx].Row && key.Col == _keymap[idx].Col)
                {
                    _keymap[idx] = _keymap[idx] with { AltName = key.AltName };
                    break;
                }
            }
        }

        /// <summary>Lines 1077-1105: one more than the highest row and column,
        /// as bytes. An empty list gives one row and one column.</summary>
        private void UpdateDimensions()
        {
            byte maxRow = 0, maxCol = 0;
            foreach (var key in _keymap)
            {
                if (key.Row > maxRow) maxRow = key.Row;
                if (key.Col > maxCol) maxCol = key.Col;
            }
            _rows = unchecked((byte)(maxRow + 1));
            _cols = unchecked((byte)(maxCol + 1));
        }

        /// <summary>The size's name, as OpenRGB gives it (lines 25-28, 434-464).</summary>
        public string GetName() => _name;

        public int GetKeyCount() => _keymap.Count;

        public int GetRowCount() => _rows;

        public int GetColumnCount() => _cols;

        /// <summary>Lines 923-931: the name of key <paramref name="index"/>, or
        /// "" past the end.</summary>
        public string GetKeyNameAt(int index) => index >= 0 && index < _keymap.Count ? _keymap[index].Name : "";

        /// <summary>Lines 933-944: the name of the first key at the place, or "".</summary>
        public string GetKeyNameAt(int row, int col)
        {
            foreach (var key in _keymap)
                if (key.Row == row && key.Col == col)
                    return key.Name;
            return "";
        }

        /// <summary>Lines 969-977.</summary>
        public string GetKeyAltNameAt(int index) => index >= 0 && index < _keymap.Count ? _keymap[index].AltName : "";

        /// <summary>Lines 979-990.</summary>
        public string GetKeyAltNameAt(int row, int col)
        {
            foreach (var key in _keymap)
                if (key.Row == row && key.Col == col)
                    return key.AltName;
            return "";
        }

        /// <summary>Lines 946-954: the value of key <paramref name="index"/>,
        /// or <see cref="NoKey"/> past the end.</summary>
        public uint GetKeyValueAt(int index) => index >= 0 && index < _keymap.Count ? _keymap[index].Value : NoKey;

        /// <summary>Lines 956-967.</summary>
        public uint GetKeyValueAt(int row, int col)
        {
            foreach (var key in _keymap)
                if (key.Row == row && key.Col == col)
                    return key.Value;
            return NoKey;
        }

        /// <summary>GetKeyMap() (lines 1002-1005): an Index map of the
        /// layout's own size.</summary>
        public KeyboardMatrix GetKeyMap() => GetKeyMap(KeyboardMapFill.Index, _rows, _cols);

        /// <summary>Lines 1007-1010.</summary>
        public KeyboardMatrix GetKeyMap(KeyboardMapFill fill) => GetKeyMap(fill, _rows, _cols);

        /// <summary>Lines 1012-1075: a map <paramref name="height"/> by
        /// <paramref name="width"/>, the layout's own row or column count
        /// where one is 0. A key outside it is left out, and a key at a cell
        /// another key holds overwrites it, the later key last.</summary>
        public KeyboardMatrix GetKeyMap(KeyboardMapFill fill, byte height, byte width)
        {
            int w = width == 0 ? _cols : width;
            int h = height == 0 ? _rows : height;
            var map = new uint[w * h];
            Array.Fill(map, NoKey);
            for (int i = 0; i < _keymap.Count; i++)
            {
                var key = _keymap[i];
                if (key.Row >= h || key.Col >= w)
                    continue;
                int offset = key.Row * w + key.Col;
                map[offset] = fill switch
                {
                    KeyboardMapFill.Count => (uint)i,
                    KeyboardMapFill.Value => key.Value,
                    _ => (uint)offset,
                };
            }
            return new KeyboardMatrix(h, w, map);
        }

        /// <summary>
        /// The lights of the layout in key order, each placed at its row and
        /// column in grid cells. A key whose name OpenRGB's key names give a
        /// keyboard-page usage (<see cref="OpenRgbKeys"/>) is a key light
        /// from a generated table. Any other light is an Other light with
        /// the label its name has, if any, for the family to make an edge,
        /// logo or status light where it knows which. <paramref name="address"/>
        /// gives each light's address from its index and key: the index for
        /// a family that numbers its LEDs in key order, the value for one
        /// that keeps its LED index there.
        /// </summary>
        public IReadOnlyList<LightInfo> Lights(Func<int, KeyboardLed, int> address)
        {
            var lights = new List<LightInfo>(_keymap.Count);
            for (int i = 0; i < _keymap.Count; i++)
            {
                var key = _keymap[i];
                TryResolve(key, out ushort usage, out var label, out int number);
                var light = usage != 0
                    ? new LightInfo(address(i, key), LightKind.Key, usage, LightKeySource.Generated)
                    : new LightInfo(address(i, key), LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
                lights.Add(light with { X = key.Col, Y = key.Row, Width = 1, Height = 1, HasPosition = true });
            }
            return lights;
        }

        /// <summary>The usage or label of a key of this layout: its name's
        /// (<see cref="OpenRgbKeys"/>), or the usage of the place a regional
        /// fix gave it (<see cref="KeyboardLed.PlaceUsage"/>) where its name
        /// is a typing-block key's or names nothing. False for a key that
        /// names neither.</summary>
        public static bool TryResolve(KeyboardLed key, out ushort usage, out LightLabel label, out int number)
        {
            bool named = OpenRgbKeys.TryGet(key.Name, out usage, out label, out number);
            if (key.PlaceUsage != 0 && (!named || (usage != 0 && OpenRgbKeys.IsTypingUsage(usage))))
            {
                usage = key.PlaceUsage;
                label = LightLabel.None;
                number = 0;
                return true;
            }
            return named;
        }

        /// <summary><see cref="TryResolve(KeyboardLed, out ushort, out LightLabel, out int)"/>
        /// for the first key at a place, as <see cref="GetKeyNameAt(int, int)"/>
        /// finds it. False for a place with no key.</summary>
        public bool TryResolveAt(int row, int col, out ushort usage, out LightLabel label, out int number)
        {
            foreach (var key in _keymap)
                if (key.Row == row && key.Col == col)
                    return TryResolve(key, out usage, out label, out number);
            usage = 0;
            label = LightLabel.None;
            number = 0;
            return false;
        }
    }

    /// <summary>
    /// One layout a family builds (#557): KeyboardLayoutManager with the
    /// family's size, LED values and regional fixes, then each edit list in
    /// turn, read from the family's code at <see cref="KeyboardLayouts.OpenRgbCommit"/>
    /// by tools/gen_openrgb_keyboard_layouts.py. <see cref="Layouts"/> holds
    /// every region the family's code can build it in. The family picks one,
    /// from the device where the device says it.
    /// </summary>
    internal sealed class KeyboardLayoutRecipe
    {
        public KeyboardLayoutRecipe(string family, string name, string source, KeyboardSize size, uint[] defaultValues,
                                    (KeyboardLayout Layout, KeyboardLed[] Keys)[] regionalOverlay, KeyboardLed[][] edits,
                                    KeyboardLayout[] layouts)
        {
            Family = family;
            Name = name;
            Source = source;
            Size = size;
            DefaultValues = defaultValues;
            RegionalOverlay = regionalOverlay;
            Edits = edits;
            Layouts = layouts;
        }

        /// <summary>The OpenRGB family folder's name without "Controller".</summary>
        public string Family { get; }

        /// <summary>OpenRGB's name for the layout, the variable that holds it.</summary>
        public string Name { get; }

        /// <summary>Where OpenRGB defines it, file and line.</summary>
        public string Source { get; }

        public KeyboardSize Size { get; }
        public uint[] DefaultValues { get; }
        public (KeyboardLayout Layout, KeyboardLed[] Keys)[] RegionalOverlay { get; }
        public KeyboardLed[][] Edits { get; }
        public KeyboardLayout[] Layouts { get; }

        /// <summary>The layout built in <paramref name="layout"/>.</summary>
        public KeyboardLayoutManager Build(KeyboardLayout layout)
        {
            var manager = new KeyboardLayoutManager(layout, Size, DefaultValues, RegionalOverlay);
            foreach (var edit in Edits)
                manager.ChangeKeys(edit);
            return manager;
        }
    }

    /// <summary>Which recipe a model builds (#557). <see cref="ProductId"/>
    /// is -1 where the family tells its models apart another way, and then
    /// <see cref="Key"/> holds that way: a laptop's DMI name, a model byte,
    /// a size. <see cref="Zone"/>, <see cref="Rows"/> and <see cref="Cols"/>
    /// are the matrix zone and size the family maps the layout into, empty
    /// and 0 where it maps it at its own size.</summary>
    internal sealed record KeyboardLayoutModel(string Family, string Model, int ProductId, string Key, string Recipe,
                                               string Zone, int Rows, int Cols);

    /// <summary>The layouts of OpenRGB's keyboard families (#557), generated
    /// into KeyboardLayouts.g.cs.</summary>
    internal static partial class KeyboardLayouts
    {
        private static Dictionary<string, KeyboardLayoutRecipe> s_byName;

        /// <summary>The recipe OpenRGB calls <paramref name="name"/>, or null.
        /// Safe from any thread: two first callers may each build the index,
        /// and each publishes a whole one.</summary>
        public static KeyboardLayoutRecipe Recipe(string name)
        {
            var byName = System.Threading.Volatile.Read(ref s_byName);
            if (byName == null)
            {
                byName = new Dictionary<string, KeyboardLayoutRecipe>(StringComparer.Ordinal);
                foreach (var recipe in Recipes) byName[recipe.Name] = recipe;
                System.Threading.Volatile.Write(ref s_byName, byName);
            }
            return name != null && byName.TryGetValue(name, out var found) ? found : null;
        }

        /// <summary>The model rows of a family for a product, in table order.</summary>
        public static IEnumerable<KeyboardLayoutModel> ModelsOf(string family, int productId)
        {
            foreach (var model in Models)
                if (model.Family == family && model.ProductId == productId)
                    yield return model;
        }

        /// <summary>The layout of a model in a region: the recipe its row
        /// names, built in <paramref name="layout"/>. Null for a name no
        /// recipe has.</summary>
        public static KeyboardLayoutManager Build(string recipe, KeyboardLayout layout) => Recipe(recipe)?.Build(layout);
    }
}
