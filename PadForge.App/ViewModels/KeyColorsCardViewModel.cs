using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>One shift layer chip on the Key Colors card (#557). The
    /// card keeps its own pick, apart from the Mappings tab's, so choosing a
    /// layer to color never changes the layer being mapped.</summary>
    public sealed class KeyColorLayerItem : ObservableObject
    {
        public string LayerMask { get; init; }
        public string LayerName { get; init; }
        public string Color { get; init; }
        public bool IsBase => LayerMask == KeyLightingSettings.BaseMask;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>One choice of the Other Lights picker: what the lights the
    /// layer gives no color show.</summary>
    public sealed class KeyBackgroundChoice
    {
        public KeyBackgroundRule Rule { get; init; }
        public string Label { get; init; }
    }

    /// <summary>
    /// The Lighting tab's Key Colors card, Zone Colors for a device whose
    /// lights are zones (#557): the lights of the selected device, the
    /// shift layer being colored, the selection, and the colors the
    /// selection takes. Every change is written straight into the device's
    /// config for this controller (<see cref="DeviceSlotConfig.KeyLighting"/>),
    /// which saves it and has the dispatcher paint it.
    ///
    /// <para>A color goes where it lasts best. A key goes by its HID usage,
    /// so the colors carry to any keyboard. A selection that holds every
    /// light of a group (all the edge lights, the logo, one zone) colors the
    /// group by name. Anything else, a single edge light or a key no source
    /// has named, goes by the device's own light number.</para>
    ///
    /// <para>A shift layer shows its own colors over the Base layer's until
    /// its Other Lights rule says otherwise, the way a target a shift layer
    /// leaves unmapped falls through to Base on the Mappings tab. The
    /// picture shows the Base layer's colors on such a layer faded.</para>
    /// </summary>
    public sealed class KeyColorsCardViewModel : ObservableObject, IKeyLightsSource
    {
        // HID usages of the selection buttons (HID Usage Tables 1.6, 10, the
        // Keyboard/Keypad page).
        internal static readonly ushort[] NumberRow = { 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27 };
        internal static readonly ushort[] FunctionKeys = Enumerable.Range(0x3A, 12).Select(u => (ushort)u)
            .Concat(Enumerable.Range(0x68, 12).Select(u => (ushort)u)).ToArray();
        internal static readonly ushort[] NumberPad = Enumerable.Range(0x53, 0x63 - 0x53 + 1).Select(u => (ushort)u)
            .Concat(new ushort[] { 0x67, 0x85, 0x86 }).ToArray();
        internal static readonly ushort[] Arrows = { 0x4F, 0x50, 0x51, 0x52 };
        internal static readonly ushort[] Wasd = { 0x1A, 0x04, 0x16, 0x07 };

        private DeviceSlotConfig _config;
        private LightMap _map = LightMap.Empty;
        private readonly HashSet<int> _selection = new();
        private bool _writing;

        public KeyColorsCardViewModel()
        {
            SelectAllKeysCommand = new RelayCommand(() => SelectWhere(l => l.Kind == LightKind.Key));
            SelectNumberRowCommand = new RelayCommand(() => SelectUsages(NumberRow));
            SelectFunctionKeysCommand = new RelayCommand(() => SelectUsages(FunctionKeys));
            SelectNumberPadCommand = new RelayCommand(() => SelectUsages(NumberPad));
            SelectArrowsCommand = new RelayCommand(() => SelectUsages(Arrows));
            SelectWasdCommand = new RelayCommand(() => SelectUsages(Wasd));
            SelectEdgeCommand = new RelayCommand(() => SelectWhere(l => l.Kind == LightKind.Edge));
            SelectLogoCommand = new RelayCommand(() => SelectWhere(l => l.Kind == LightKind.Logo));
            SelectAllLightsCommand = new RelayCommand(() => SelectWhere(_ => true));
            ClearSelectionCommand = new RelayCommand(() => SetSelection(Array.Empty<int>()));
            ClearSelectedColorsCommand = new RelayCommand(ClearSelectedColors);
            ClearLayerCommand = new RelayCommand(ClearLayer);
            ApplyColorCommand = new RelayCommand(() => ColorSelection(_paint));
        }

        // ─────────────────────────────────────────────
        //  What the card shows
        // ─────────────────────────────────────────────

        /// <summary>The selected device's lights, for the picture.</summary>
        internal LightMap Map => _map;
        LightMap IKeyLightsSource.Map => _map;
        IReadOnlyCollection<int> IKeyLightsSource.Selection => _selection;
        int? IKeyLightsSource.OwnColorOf(int index) => OwnColorOf(index);
        int? IKeyLightsSource.BaseColorOf(int index) => BaseColorOf(index);
        string IKeyLightsSource.BaseLayerName => BaseLayerName;
        int IKeyLightsSource.OrderOf(int index) => 0;
        string IKeyLightsSource.LegendOf(int index) => null;
        void IKeyLightsSource.Toggle(int index) => Toggle(index);
        void IKeyLightsSource.SetSelection(IEnumerable<int> indexes) => SetSelection(indexes);
        bool IKeyLightsSource.TogglePressed(ushort usage) => TogglePressed(usage);

        /// <summary>The device the card colors, the one the picture belongs
        /// to, or null.</summary>
        internal DeviceSlotConfig Config => _config;

        public bool IsVisible => _config != null && _map.PerLight;

        /// <summary>Key Colors for a device with keys, Zone Colors for one
        /// whose lights are zones.</summary>
        public string Title => _map.HasKeys ? Strings.Instance.Pad_Lighting_KeyColors : Strings.Instance.Pad_Lighting_ZoneColors;

        public string Description => _map.HasKeys
            ? Strings.Instance.Pad_Lighting_KeyColors_Description
            : Strings.Instance.Pad_Lighting_ZoneColors_Description;

        public bool HasKeys => _map.HasKeys;
        public bool HasEdge => _map.Lights.Any(l => l.Kind == LightKind.Edge);
        public bool HasLogo => _map.Lights.Any(l => l.Kind == LightKind.Logo);

        /// <summary>Keys no source has named, which Map Key Lights can name.</summary>
        public bool HasUnnamedKeys => _map.UnnamedKeyCount > 0;

        public ObservableCollection<KeyColorLayerItem> Layers { get; } = new();

        public bool HasLayers => Layers.Count > 1;

        private string _selectedLayerMask = KeyLightingSettings.BaseMask;
        public string SelectedLayerMask
        {
            get => _selectedLayerMask;
            set
            {
                value = string.IsNullOrEmpty(value) ? KeyLightingSettings.BaseMask : value;
                if (!Layers.Any(l => l.LayerMask == value)) return;
                if (!SetProperty(ref _selectedLayerMask, value)) return;
                foreach (var item in Layers) item.IsSelected = item.LayerMask == value;
                RaiseLayer();
            }
        }

        public bool IsBaseLayer => _selectedLayerMask == KeyLightingSettings.BaseMask;

        /// <summary>The Base layer's name as the Mappings tab shows it.</summary>
        internal string BaseLayerName => Layers.FirstOrDefault(l => l.IsBase)?.LayerName
            ?? Strings.Instance.Pad_Shift_BaseTabLabel;

        private KeyLightingSettings Settings => _config?.KeyLightingParsed ?? KeyLightingSettings.Empty;

        /// <summary>The layer being colored as saved, or the empty one it
        /// starts as.</summary>
        internal KeyLightingLayer Layer => Settings.LayerOrDefault(_selectedLayerMask);

        private KeyBackgroundChoice[] _backgroundChoices;

        /// <summary>The Other Lights choices of the layer being colored. A
        /// shift layer can show the Base layer, and the Base layer has
        /// none to show.
        ///
        /// <para>One list for as long as its labels hold. A ComboBox handed
        /// a new list drops its selection and writes the empty selection
        /// back, and a list made on every read did that on every refresh:
        /// the box went blank, with WPF's red error border drawn on the
        /// window's adorner layer, where it stayed put while the card
        /// scrolled.</para></summary>
        public IReadOnlyList<KeyBackgroundChoice> BackgroundChoices
        {
            get
            {
                var s = Strings.Instance;
                var choices = new List<KeyBackgroundChoice>(4);
                if (!IsBaseLayer)
                    choices.Add(new KeyBackgroundChoice
                    {
                        Rule = KeyBackgroundRule.BaseLayer,
                        Label = string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_Others_BaseLayer_Format, BaseLayerName),
                    });
                choices.Add(new KeyBackgroundChoice { Rule = KeyBackgroundRule.Same, Label = s.Pad_Lighting_KeyColors_Others_Mode });
                choices.Add(new KeyBackgroundChoice { Rule = KeyBackgroundRule.Off, Label = s.Pad_Lighting_KeyColors_Others_Off });
                choices.Add(new KeyBackgroundChoice { Rule = KeyBackgroundRule.Color, Label = s.Pad_Lighting_KeyColors_Others_Color });
                var held = _backgroundChoices;
                if (held != null && held.Length == choices.Count
                    && held.Zip(choices).All(pair => pair.First.Rule == pair.Second.Rule && pair.First.Label == pair.Second.Label))
                    return held;
                return _backgroundChoices = choices.ToArray();
            }
        }

        /// <summary>What the lights the layer gives no color show. Null
        /// from the ComboBox while it has no selection, which changes
        /// nothing.</summary>
        public KeyBackgroundRule? BackgroundRule
        {
            get => Layer.Background;
            set
            {
                if (value is not KeyBackgroundRule rule || !Enum.IsDefined(rule)
                    || (rule == KeyBackgroundRule.BaseLayer && IsBaseLayer) || rule == Layer.Background)
                    return;
                Write(Copy(Layer, background: rule));
            }
        }

        public bool BackgroundIsColor => Layer.Background == KeyBackgroundRule.Color;

        public byte BackgroundRed { get => (byte)(Layer.BackgroundRgb >> 16); set => SetBackground(WithChannel(Layer.BackgroundRgb, 0, value)); }
        public byte BackgroundGreen { get => (byte)(Layer.BackgroundRgb >> 8); set => SetBackground(WithChannel(Layer.BackgroundRgb, 1, value)); }
        public byte BackgroundBlue { get => (byte)Layer.BackgroundRgb; set => SetBackground(WithChannel(Layer.BackgroundRgb, 2, value)); }

        /// <summary>The background color as RRGGBB. A value that is not a
        /// six-digit hex color, with or without "#", changes nothing.</summary>
        public string BackgroundHex
        {
            get => Hex(Layer.BackgroundRgb);
            set
            {
                if (TryHex(value, out int rgb)) SetBackground(rgb);
                else OnPropertyChanged();
            }
        }

        private void SetBackground(int rgb)
        {
            rgb &= 0xFFFFFF;
            if (rgb == Layer.BackgroundRgb && BackgroundIsColor) return;
            Write(Copy(Layer, backgroundRgb: rgb, background: KeyBackgroundRule.Color));
        }

        // ─────────────────────────────────────────────
        //  Selection and its color
        // ─────────────────────────────────────────────

        /// <summary>The selected lights, as indexes into the map's lights.</summary>
        internal IReadOnlyCollection<int> Selection => _selection;

        /// <summary>Raised when the selection or a light's color changed,
        /// so the picture redraws.</summary>
        public event Action LightsChanged;

        public bool HasSelection => _selection.Count > 0;

        public string SelectionSummary => _selection.Count == 0
            ? Strings.Instance.Pad_Lighting_KeyColors_NoneSelected
            : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Pad_Lighting_KeyColors_Selected_Format, _selection.Count);

        private int _paint = 0xFF0000;

        /// <summary>The color the selection takes. Setting a channel colors
        /// every selected light at once, and Apply Color gives the selection
        /// the color as it stands, for lights picked after it.</summary>
        public byte PaintRed { get => (byte)(_paint >> 16); set => SetPaint(WithChannel(_paint, 0, value)); }
        public byte PaintGreen { get => (byte)(_paint >> 8); set => SetPaint(WithChannel(_paint, 1, value)); }
        public byte PaintBlue { get => (byte)_paint; set => SetPaint(WithChannel(_paint, 2, value)); }

        /// <summary>The selection's color as RRGGBB, for the hex box and the
        /// swatches. A value that is not a six-digit hex color, with or
        /// without "#", changes nothing.</summary>
        public string PaintHex
        {
            get => Hex(_paint);
            set
            {
                if (TryHex(value, out int rgb)) SetPaint(rgb);
                else OnPropertyChanged();
            }
        }

        /// <summary>When on, a key pressed on the keyboard while the
        /// picture has the focus is added to the selection or taken out of
        /// it.</summary>
        private bool _pickByPressing;
        public bool PickByPressing
        {
            get => _pickByPressing;
            set => SetProperty(ref _pickByPressing, value);
        }

        public RelayCommand SelectAllKeysCommand { get; }
        public RelayCommand SelectNumberRowCommand { get; }
        public RelayCommand SelectFunctionKeysCommand { get; }
        public RelayCommand SelectNumberPadCommand { get; }
        public RelayCommand SelectArrowsCommand { get; }
        public RelayCommand SelectWasdCommand { get; }
        public RelayCommand SelectEdgeCommand { get; }
        public RelayCommand SelectLogoCommand { get; }
        public RelayCommand SelectAllLightsCommand { get; }
        public RelayCommand ClearSelectionCommand { get; }
        public RelayCommand ClearSelectedColorsCommand { get; }
        public RelayCommand ClearLayerCommand { get; }
        public RelayCommand ApplyColorCommand { get; }

        // ─────────────────────────────────────────────
        //  Inputs
        // ─────────────────────────────────────────────

        /// <summary>Points the card at a device's config on this controller,
        /// its lights and the slot's shift layers (the Mappings tab's layer
        /// tabs, Base first). A new device or a new map clears the
        /// selection. The layer pick is kept while the layer still
        /// exists.</summary>
        internal void Attach(DeviceSlotConfig config, LightMap map, IEnumerable<ShiftLayerInfo> layers)
        {
            map ??= LightMap.Empty;
            bool deviceChanged = !ReferenceEquals(config, _config) || !ReferenceEquals(map, _map);
            if (!ReferenceEquals(config, _config))
            {
                if (_config != null) _config.PropertyChanged -= OnConfigChanged;
                _config = config;
                if (_config != null) _config.PropertyChanged += OnConfigChanged;
            }
            _map = map;
            bool layersChanged = SyncLayers(layers);
            // The page attaches on every pass that could change the tab.
            // One that changes nothing raises nothing.
            if (!deviceChanged && !layersChanged) return;
            if (deviceChanged) _selection.Clear();
            OnPropertyChanged(string.Empty);
            // Once more for the Other Lights rule, now that its ComboBox
            // holds the list the refresh above may have replaced.
            OnPropertyChanged(nameof(BackgroundRule));
            LightsChanged?.Invoke();
        }

        /// <summary>Lets go of the device, when the device the tab shows
        /// changes, until the page attaches the card to the new one.</summary>
        internal void Detach() => Attach(null, null, null);

        /// <summary>Takes the slot's shift layers again after the Mappings
        /// tab added, renamed or removed one.</summary>
        internal void UpdateLayers(IEnumerable<ShiftLayerInfo> layers)
        {
            string was = _selectedLayerMask;
            SyncLayers(layers);
            OnPropertyChanged(nameof(HasLayers));
            if (was != _selectedLayerMask)
            {
                OnPropertyChanged(nameof(SelectedLayerMask));
                RaiseLayer();
            }
            else
            {
                // The rule after its list, so the ComboBox finds the
                // selection in the list it holds by then.
                OnPropertyChanged(nameof(BackgroundChoices));
                OnPropertyChanged(nameof(BackgroundRule));
            }
        }

        /// <summary>True when the layer list or the layer being colored
        /// changed.</summary>
        private bool SyncLayers(IEnumerable<ShiftLayerInfo> layers)
        {
            var given = layers?.Where(l => l != null).ToList() ?? new List<ShiftLayerInfo>();
            var baseInfo = given.FirstOrDefault(l => l.LayerMask == KeyLightingSettings.BaseMask);
            var wanted = new List<KeyColorLayerItem>
            {
                new()
                {
                    LayerMask = KeyLightingSettings.BaseMask,
                    LayerName = string.IsNullOrEmpty(baseInfo?.LayerName) ? Strings.Instance.Pad_Shift_BaseTabLabel : baseInfo.LayerName,
                    Color = baseInfo?.Color ?? string.Empty,
                },
            };
            foreach (var layer in given)
                if (layer.LayerMask != KeyLightingSettings.BaseMask && KeyLightingSettings.ValidMask(layer.LayerMask)
                    && !wanted.Any(w => w.LayerMask == layer.LayerMask))
                    wanted.Add(new KeyColorLayerItem { LayerMask = layer.LayerMask, LayerName = layer.LayerName, Color = layer.Color });
            bool same = wanted.Count == Layers.Count;
            for (int i = 0; same && i < wanted.Count; i++)
                same = wanted[i].LayerMask == Layers[i].LayerMask && wanted[i].LayerName == Layers[i].LayerName
                       && wanted[i].Color == Layers[i].Color;
            if (!same)
            {
                Layers.Clear();
                foreach (var item in wanted) Layers.Add(item);
            }
            bool moved = !Layers.Any(l => l.LayerMask == _selectedLayerMask);
            if (moved) _selectedLayerMask = KeyLightingSettings.BaseMask;
            foreach (var item in Layers) item.IsSelected = item.LayerMask == _selectedLayerMask;
            return !same || moved;
        }

        private void OnConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_writing || e.PropertyName != nameof(DeviceSlotConfig.KeyLighting)) return;
            RaiseLayer();
        }

        private void RaiseLayer()
        {
            OnPropertyChanged(nameof(IsBaseLayer));
            OnPropertyChanged(nameof(BackgroundChoices));
            OnPropertyChanged(nameof(BackgroundRule));
            OnPropertyChanged(nameof(BackgroundIsColor));
            OnPropertyChanged(nameof(BackgroundRed));
            OnPropertyChanged(nameof(BackgroundGreen));
            OnPropertyChanged(nameof(BackgroundBlue));
            OnPropertyChanged(nameof(BackgroundHex));
            LightsChanged?.Invoke();
        }

        // ─────────────────────────────────────────────
        //  Reading a light's color
        // ─────────────────────────────────────────────

        /// <summary>The color the layer being colored gives light
        /// <paramref name="index"/> of its own, or null.</summary>
        internal int? OwnColorOf(int index)
            => index >= 0 && index < _map.Lights.Count ? ColorIn(Layer, _map.Lights[index]) : null;

        /// <summary>The color light <paramref name="index"/> shows from the
        /// Base layer, on a shift layer that shows the Base layer under its
        /// own colors and gives this light none, or null.</summary>
        internal int? BaseColorOf(int index)
        {
            if (IsBaseLayer || index < 0 || index >= _map.Lights.Count) return null;
            var layer = Layer;
            if (layer.Background != KeyBackgroundRule.BaseLayer || ColorIn(layer, _map.Lights[index]).HasValue) return null;
            var baseLayer = Settings.LayerOf(KeyLightingSettings.BaseMask);
            return baseLayer == null ? null : ColorIn(baseLayer, _map.Lights[index]);
        }

        /// <summary>The color a layer gives a light of its own: the light's,
        /// its key's, then its group's, the renderer's order.</summary>
        private static int? ColorIn(KeyLightingLayer layer, in LightInfo light)
            => LightRenderer.ColorOf(layer.ToFrame(), light);

        // ─────────────────────────────────────────────
        //  Selecting
        // ─────────────────────────────────────────────

        internal void SetSelection(IEnumerable<int> indexes)
        {
            _selection.Clear();
            if (indexes != null)
                foreach (int i in indexes)
                    if (i >= 0 && i < _map.Lights.Count) _selection.Add(i);
            SelectionMoved();
        }

        internal void Toggle(int index)
        {
            if (index < 0 || index >= _map.Lights.Count) return;
            if (!_selection.Remove(index)) _selection.Add(index);
            SelectionMoved();
        }

        /// <summary>A key pressed while picking by pressing: every light of
        /// that key joins the selection, or leaves it when it was in. False
        /// when the device has no light for the key.</summary>
        internal bool TogglePressed(ushort usage)
        {
            var indexes = _map.IndexesOf(usage);
            if (indexes.Count == 0) return false;
            bool all = indexes.All(_selection.Contains);
            foreach (int i in indexes)
            {
                if (all) _selection.Remove(i);
                else _selection.Add(i);
            }
            SelectionMoved();
            return true;
        }

        private void SelectUsages(IEnumerable<ushort> usages)
            => SetSelection(usages.SelectMany(u => _map.IndexesOf(u)));

        private void SelectWhere(Func<LightInfo, bool> which)
            => SetSelection(Enumerable.Range(0, _map.Lights.Count).Where(i => which(_map.Lights[i])));

        private void SelectionMoved()
        {
            // One light picked shows its color in the picker, as OpenRGB's
            // device page does for a single LED. A selection of several
            // keeps the color chosen, so it can be applied to them.
            if (_selection.Count == 1 && (OwnColorOf(_selection.First()) ?? BaseColorOf(_selection.First())) is int shown
                && shown != _paint)
            {
                _paint = shown;
                RaisePaint();
            }
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionSummary));
            LightsChanged?.Invoke();
        }

        // ─────────────────────────────────────────────
        //  Coloring
        // ─────────────────────────────────────────────

        private void SetPaint(int rgb)
        {
            rgb &= 0xFFFFFF;
            if (rgb == _paint) return;
            _paint = rgb;
            RaisePaint();
            if (_selection.Count > 0) ColorSelection(rgb);
        }

        private void RaisePaint()
        {
            OnPropertyChanged(nameof(PaintRed));
            OnPropertyChanged(nameof(PaintGreen));
            OnPropertyChanged(nameof(PaintBlue));
            OnPropertyChanged(nameof(PaintHex));
        }

        /// <summary>Gives every selected light <paramref name="rgb"/> on the
        /// layer being colored.</summary>
        internal void ColorSelection(int rgb)
        {
            if (_config == null || _selection.Count == 0) return;
            var layer = Layer;
            var keys = new Dictionary<ushort, int>(layer.Keys);
            var groups = new Dictionary<string, int>(layer.Groups, StringComparer.Ordinal);
            var lights = new Dictionary<int, int>(layer.Lights);
            var whole = WholeGroups();
            foreach (var group in whole)
            {
                groups[group] = rgb;
                // The group's color is the one its lights show now.
                foreach (var light in _map.Lights)
                    if (light.Group == group) lights.Remove(light.Address);
            }
            foreach (int i in _selection)
            {
                var light = _map.Lights[i];
                if (light.Kind == LightKind.Key && light.Usage != 0)
                {
                    keys[light.Usage] = rgb;
                    lights.Remove(light.Address);
                }
                else if (light.Group is not { } group || !whole.Contains(group))
                    lights[light.Address] = rgb;
            }
            Write(Copy(layer, keys: keys, groups: groups, lights: lights));
        }

        /// <summary>Takes the selected lights' own colors away, so they
        /// show what the layer's other lights show. A group the selection
        /// holds only part of keeps its color on the lights left out.</summary>
        internal void ClearSelectedColors()
        {
            if (_config == null || _selection.Count == 0) return;
            var layer = Layer;
            var keys = new Dictionary<ushort, int>(layer.Keys);
            var groups = new Dictionary<string, int>(layer.Groups, StringComparer.Ordinal);
            var lights = new Dictionary<int, int>(layer.Lights);
            var selected = new HashSet<int>(_selection);
            foreach (int i in selected)
            {
                var light = _map.Lights[i];
                lights.Remove(light.Address);
                if (light.Kind == LightKind.Key && light.Usage != 0)
                {
                    keys.Remove(light.Usage);
                    continue;
                }
                if (light.Group is not { } group || !groups.TryGetValue(group, out int groupRgb)) continue;
                // Part of a colored group: its other lights keep the color
                // one by one, and the group's own color goes.
                groups.Remove(group);
                for (int j = 0; j < _map.Lights.Count; j++)
                {
                    var other = _map.Lights[j];
                    if (other.Group == group && !selected.Contains(j) && !lights.ContainsKey(other.Address))
                        lights[other.Address] = groupRgb;
                }
            }
            Write(Copy(layer, keys: keys, groups: groups, lights: lights));
        }

        /// <summary>Takes every color off the layer being colored and gives
        /// it back the Other Lights rule it starts with.</summary>
        internal void ClearLayer()
        {
            if (_config == null) return;
            Write(new KeyLightingLayer { Mask = _selectedLayerMask });
        }

        /// <summary>The groups every light of which is selected.</summary>
        private HashSet<string> WholeGroups()
        {
            var whole = new HashSet<string>(StringComparer.Ordinal);
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < _map.Lights.Count; i++)
            {
                var group = _map.Lights[i].Group;
                if (group == null) continue;
                bool selected = _selection.Contains(i);
                seen[group] = seen.TryGetValue(group, out bool all) ? all && selected : selected;
            }
            foreach (var pair in seen)
                if (pair.Value) whole.Add(pair.Key);
            return whole;
        }

        private void Write(KeyLightingLayer layer)
        {
            if (_config == null) return;
            _writing = true;
            try { _config.KeyLighting = Settings.With(layer).Format(); }
            finally { _writing = false; }
            RaiseLayer();
        }

        private KeyLightingLayer Copy(KeyLightingLayer from, KeyBackgroundRule? background = null, int? backgroundRgb = null,
            IReadOnlyDictionary<ushort, int> keys = null, IReadOnlyDictionary<string, int> groups = null,
            IReadOnlyDictionary<int, int> lights = null) => new()
            {
                Mask = _selectedLayerMask,
                Background = background ?? from.Background,
                BackgroundRgb = backgroundRgb ?? from.BackgroundRgb,
                Keys = keys ?? from.Keys,
                Groups = groups ?? from.Groups,
                Lights = lights ?? from.Lights,
            };

        private static int WithChannel(int rgb, int channel, byte value)
        {
            int shift = 16 - channel * 8;
            return (rgb & ~(0xFF << shift) & 0xFFFFFF) | (value << shift);
        }

        private static string Hex(int rgb) => (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

        /// <summary>RRGGBB or #RRGGBB, the Lighting tab's hex boxes' rule.</summary>
        internal static bool TryHex(string text, out int rgb)
        {
            rgb = 0;
            string s = (text ?? string.Empty).Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            return s.Length == 6
                && int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
        }
    }
}
