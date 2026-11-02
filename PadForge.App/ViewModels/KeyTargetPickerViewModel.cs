using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Views;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What the key picture (<see cref="KeyLightsView"/>) draws and edits
    /// (#557): a device's lights, the selected ones, the color each shows,
    /// and where a click, a drag or a key press goes. The Lighting tab's Key
    /// Colors card and the key colors macro actions' picker each give it
    /// their own.
    /// </summary>
    internal interface IKeyLightsSource : INotifyPropertyChanged
    {
        LightMap Map { get; }
        IReadOnlyCollection<int> Selection { get; }

        /// <summary>The color the light shows of its own, or null.</summary>
        int? OwnColorOf(int index);

        /// <summary>The color the light shows from beneath, drawn faded, or
        /// null.</summary>
        int? BaseColorOf(int index);

        /// <summary>The name of what lies beneath, for the tooltip.</summary>
        string BaseLayerName { get; }

        /// <summary>The light's place in the order picked, from 1, drawn on
        /// it, or 0 for none.</summary>
        int OrderOf(int index);

        /// <summary>The light's legend when the source names it itself, or
        /// null for the picture's own.</summary>
        string LegendOf(int index);

        bool PickByPressing { get; }
        void Toggle(int index);
        void SetSelection(IEnumerable<int> indexes);
        bool TogglePressed(ushort usage);

        /// <summary>Raised when the selection or a light's color changed.</summary>
        event Action LightsChanged;
    }

    /// <summary>
    /// The key picker of the Set Key Colors and Clear Key Colors macro
    /// actions (#557) over the action's
    /// <see cref="MacroAction.KeyColorTargets"/>. A macro names keys by HID
    /// usage, so it lights the same keys on any keyboard, and the picture is
    /// a full-size keyboard as the input layout in use has it, with a chip
    /// for each group of lights a device can have: its edge, logo, status
    /// and other lights and every zone. The picked lights show in the
    /// action's color, numbered in the order picked, the order a countdown
    /// keeps. A saved target this picture has no light for is kept as it
    /// is.
    /// </summary>
    public sealed class KeyTargetPickerViewModel : ObservableObject, IKeyLightsSource
    {
        private readonly MacroAction _action;
        private readonly LightMap _map;
        private readonly List<int> _order = new();
        private readonly List<KeyColorTarget> _unplaced = new();
        private bool _all;
        private bool _writing;

        internal KeyTargetPickerViewModel(MacroAction action)
        {
            _action = action;
            _map = GenericMap();
            _action.PropertyChanged += OnActionChanged;
            SelectAllKeysCommand = new RelayCommand(() => SetSelection(Indexes(l => l.Kind == LightKind.Key)));
            SelectNumberRowCommand = new RelayCommand(() => SetSelection(UsageIndexes(KeyColorsCardViewModel.NumberRow)));
            SelectFunctionKeysCommand = new RelayCommand(() => SetSelection(UsageIndexes(KeyColorsCardViewModel.FunctionKeys)));
            SelectNumberPadCommand = new RelayCommand(() => SetSelection(UsageIndexes(KeyColorsCardViewModel.NumberPad)));
            SelectArrowsCommand = new RelayCommand(() => SetSelection(UsageIndexes(KeyColorsCardViewModel.Arrows)));
            SelectWasdCommand = new RelayCommand(() => SetSelection(UsageIndexes(KeyColorsCardViewModel.Wasd)));
            ClearSelectionCommand = new RelayCommand(() => SetSelection(Array.Empty<int>()));
            Reload();
        }

        public RelayCommand SelectAllKeysCommand { get; }
        public RelayCommand SelectNumberRowCommand { get; }
        public RelayCommand SelectFunctionKeysCommand { get; }
        public RelayCommand SelectNumberPadCommand { get; }
        public RelayCommand SelectArrowsCommand { get; }
        public RelayCommand SelectWasdCommand { get; }
        public RelayCommand ClearSelectionCommand { get; }

        public event Action LightsChanged;

        internal LightMap Map => _map;
        LightMap IKeyLightsSource.Map => _map;

        IReadOnlyCollection<int> IKeyLightsSource.Selection => _order;

        string IKeyLightsSource.BaseLayerName => string.Empty;

        /// <summary>The action names every light, every key and group
        /// alike.</summary>
        public bool AllLights
        {
            get => _all;
            set
            {
                if (_all == value) return;
                _all = value;
                if (value) _order.Clear();
                Write();
            }
        }

        private bool _pickByPressing;
        public bool PickByPressing
        {
            get => _pickByPressing;
            set => SetProperty(ref _pickByPressing, value);
        }

        int? IKeyLightsSource.OwnColorOf(int index)
            => _all || _order.Contains(index) ? (_action.LightbarR << 16) | (_action.LightbarG << 8) | _action.LightbarB : null;

        int? IKeyLightsSource.BaseColorOf(int index) => null;

        int IKeyLightsSource.OrderOf(int index) => _all ? 0 : _order.IndexOf(index) + 1;

        /// <summary>The Other chip stands for the group, not one light.</summary>
        string IKeyLightsSource.LegendOf(int index)
            => index >= 0 && index < _map.Lights.Count && _map.Lights[index].Kind == LightKind.Other
                ? Resources.Strings.Strings.Instance.Pad_Lighting_KeyColors_OtherGroup : null;

        void IKeyLightsSource.Toggle(int index)
        {
            if (index < 0 || index >= _map.Lights.Count) return;
            _all = false;
            if (!_order.Remove(index)) _order.Add(index);
            Write();
        }

        /// <summary>Picks <paramref name="indexes"/>, in their order, in
        /// place of what was picked.</summary>
        public void SetSelection(IEnumerable<int> indexes)
        {
            _all = false;
            _order.Clear();
            if (indexes != null)
                foreach (int i in indexes)
                    if (i >= 0 && i < _map.Lights.Count && !_order.Contains(i)) _order.Add(i);
            Write();
        }

        bool IKeyLightsSource.TogglePressed(ushort usage)
        {
            var indexes = _map.IndexesOf(usage);
            if (indexes.Count == 0) return false;
            _all = false;
            bool all = indexes.All(_order.Contains);
            foreach (int i in indexes)
            {
                if (all) _order.Remove(i);
                else if (!_order.Contains(i)) _order.Add(i);
            }
            Write();
            return true;
        }

        /// <summary>Reads the action's targets again, after it changed
        /// other than through this picker.</summary>
        internal void Reload()
        {
            if (_writing) return;
            _order.Clear();
            _unplaced.Clear();
            _all = false;
            foreach (var target in KeyColorTarget.Parse(_action.KeyColorTargets))
            {
                if (target.IsAll)
                {
                    _all = true;
                    continue;
                }
                int index = IndexOf(target);
                if (index >= 0) _order.Add(index);
                else _unplaced.Add(target);
            }
            if (_all) _order.Clear();
            OnPropertyChanged(nameof(AllLights));
            LightsChanged?.Invoke();
        }

        private void Write()
        {
            var targets = _all
                ? new[] { KeyColorTarget.All }
                : _order.Select(TargetOf).Concat(_unplaced).ToArray();
            _writing = true;
            try { _action.KeyColorTargets = KeyColorTarget.Format(targets); }
            finally { _writing = false; }
            OnPropertyChanged(nameof(AllLights));
            LightsChanged?.Invoke();
        }

        private void OnActionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MacroAction.LightbarR) or nameof(MacroAction.LightbarG)
                or nameof(MacroAction.LightbarB) or nameof(MacroAction.KeyColorStyle))
                LightsChanged?.Invoke();
        }

        private KeyColorTarget TargetOf(int index)
        {
            var light = _map.Lights[index];
            return light.Kind == LightKind.Key ? new KeyColorTarget(light.Usage, null) : new KeyColorTarget(0, light.Group);
        }

        private int IndexOf(KeyColorTarget target)
        {
            if (target.IsKey)
            {
                var indexes = _map.IndexesOf(target.Usage);
                return indexes.Count > 0 ? indexes[0] : -1;
            }
            for (int i = 0; i < _map.Lights.Count; i++)
                if (_map.Lights[i].Kind != LightKind.Key && _map.Lights[i].Group == target.Group) return i;
            return -1;
        }

        private IEnumerable<int> Indexes(Func<LightInfo, bool> which)
            => Enumerable.Range(0, _map.Lights.Count).Where(i => which(_map.Lights[i]));

        private IEnumerable<int> UsageIndexes(IEnumerable<ushort> usages)
            => usages.SelectMany(u => _map.IndexesOf(u));

        /// <summary>The fewest zones the picker offers, the most a laptop
        /// keyboard's zones come to.</summary>
        internal const int MinZones = 4;

        /// <summary>
        /// A full-size keyboard, ISO when the input layout in use is not US
        /// English, as the Keyboard + Mouse preview draws keyboards
        /// (KeyboardFamilies), then one light each for a device's edge, logo,
        /// status and other lights, and a zone for every zone any known
        /// device has, at least <see cref="MinZones"/>.
        /// </summary>
        internal static LightMap GenericMap()
        {
            ushort language = (ushort)((long)KbmHidKeyMap.UiLayout() & 0xFFFF);
            var keys = language == 0x0409 ? KeyboardFamilies.FullsizeAnsi : KeyboardFamilies.FullsizeIso;
            int zones = Math.Max(MinZones, PeripheralLightMaps.All.Select(m => m.ZoneCount).DefaultIfEmpty(0).Max());
            return GenericMap(keys, zones);
        }

        internal static LightMap GenericMap(IEnumerable<LayoutKey> keys, int zones)
        {
            var lights = new List<LightInfo>();
            var seen = new HashSet<ushort>();
            foreach (var key in keys)
                if (key.Page == 0x07 && key.Id != 0 && seen.Add(key.Id))
                    lights.Add(new LightInfo(lights.Count, LightKind.Key, key.Id, LightKeySource.Generated));
            foreach (var kind in new[] { LightKind.Edge, LightKind.Logo, LightKind.Status, LightKind.Other })
                lights.Add(new LightInfo(lights.Count, kind, 0, LightKeySource.None));
            for (int zone = 1; zone <= zones; zone++)
                lights.Add(new LightInfo(lights.Count, LightKind.Zone, 0, LightKeySource.None) { Zone = zone });
            return new LightMap(lights, "generic");
        }
    }
}
