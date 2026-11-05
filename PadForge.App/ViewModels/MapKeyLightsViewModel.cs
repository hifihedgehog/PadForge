using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Map Key Lights (#557): names the key each light of a device sits
    /// under, for a keyboard whose own description leaves keys unnamed or
    /// names one wrong. PadForge lights one key light alone, white over
    /// black, ahead of every controller's claim
    /// (<see cref="PeripheralOutputs.SetProbe"/>), and the key pressed next
    /// names it, found by its scan code as the Key Colors card's picker
    /// finds a key. The names are kept for the device's model
    /// (<see cref="LearnedKeyMaps"/>), so every unit of the model takes
    /// them, and an export carries them to another PC.
    /// </summary>
    public sealed class MapKeyLightsViewModel : ObservableObject, IKeyLightsSource, IDisposable
    {
        private readonly OutputPath _path;
        private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        private LightMap _map;
        private int _current = -1;
        private bool _disposed;

        internal MapKeyLightsViewModel(OutputPath path, string deviceName)
        {
            _path = path;
            DeviceName = deviceName ?? string.Empty;
            _map = PeripheralLightMaps.For(path) ?? LightMap.Empty;
            PeripheralLightMaps.Changed += OnMapsChanged;
        }

        public string DeviceName { get; }

        /// <summary>The model the names are kept for.</summary>
        internal string Product => _map.Product;

        public bool CanLearn => !string.IsNullOrEmpty(_map.Product) && KeyIndexes().Any();

        /// <summary>The light lit now, or -1.</summary>
        internal int Current => _current;

        public bool IsListening => _current >= 0;

        public event Action LightsChanged;

        LightMap IKeyLightsSource.Map => _map;
        IReadOnlyCollection<int> IKeyLightsSource.Selection => _current >= 0 ? new[] { _current } : Array.Empty<int>();
        int? IKeyLightsSource.OwnColorOf(int index) => index == _current ? 0xFFFFFF : null;
        int? IKeyLightsSource.BaseColorOf(int index) => null;
        string IKeyLightsSource.BaseLayerName => string.Empty;
        int IKeyLightsSource.OrderOf(int index) => 0;
        string IKeyLightsSource.LegendOf(int index) => null;
        bool IKeyLightsSource.PickByPressing => false;
        bool IKeyLightsSource.TogglePressed(ushort usage) => false;

        /// <summary>A click on a key light asks about it next.</summary>
        void IKeyLightsSource.Toggle(int index)
        {
            if (IsKey(index)) Ask(index);
        }

        /// <summary>A drag asks about the first key it covered. The picture hands
        /// over the light lit now ahead of those, and a drag that covered no key
        /// asks about nothing.</summary>
        void IKeyLightsSource.SetSelection(IEnumerable<int> indexes)
        {
            if (indexes == null) return;
            foreach (int index in indexes)
            {
                if (index == _current || !IsKey(index)) continue;
                Ask(index);
                return;
            }
        }

        /// <summary>How many key lights have a name, and how many there
        /// are.</summary>
        public string Progress
        {
            get
            {
                var keys = KeyIndexes().ToList();
                int named = keys.Count(i => _map.Lights[i].Usage != 0);
                return string.Format(CultureInfo.CurrentCulture, Strings.Instance.MapKeyLights_Progress_Format, named, keys.Count);
            }
        }

        /// <summary>What to do now.</summary>
        public string Status
        {
            get
            {
                var s = Strings.Instance;
                if (!CanLearn) return s.MapKeyLights_NoLights;
                if (_current < 0) return s.MapKeyLights_Ready;
                var light = _map.Lights[_current];
                return string.Format(CultureInfo.CurrentCulture,
                    light.Usage != 0 ? s.MapKeyLights_AskNamed_Format : s.MapKeyLights_Ask_Format,
                    light.Address, light.Usage != 0 ? Views.KeyLightsView.KeyLegend(light.Usage, KbmHidKeyMap.UiLayout()) : null);
            }
        }

        /// <summary>Starts with the first key light that has no name, or
        /// the first key light when every one has.</summary>
        public void Start()
        {
            if (!CanLearn) return;
            var keys = KeyIndexes().ToList();
            Ask(keys.FirstOrDefault(i => _map.Lights[i].Usage == 0, keys[0]));
        }

        /// <summary>Lights off, the device back to its claims.</summary>
        public void Stop()
        {
            _current = -1;
            PeripheralOutputs.SetProbe(_path, null);
            Raise();
        }

        /// <summary>The next key light without a name after the one lit,
        /// or the next key light when every one has a name.</summary>
        public void Skip()
        {
            if (_current < 0) { Start(); return; }
            Ask(NextAfter(_current, unnamedOnly: KeyIndexes().Any(i => _map.Lights[i].Usage == 0 && i != _current)));
        }

        /// <summary>The key light before the one lit.</summary>
        public void Back()
        {
            var keys = KeyIndexes().ToList();
            if (keys.Count == 0 || _current < 0) return;
            int at = keys.IndexOf(_current);
            Ask(keys[(at - 1 + keys.Count) % keys.Count]);
        }

        /// <summary>The key with <paramref name="usage"/> sits under the
        /// light lit: kept for the model, and the next unnamed light lit.
        /// A key named for another light moves here, since naming a key
        /// again corrects it.</summary>
        internal void Name(ushort usage)
        {
            if (_current < 0 || usage == 0 || string.IsNullOrEmpty(_map.Product)) return;
            var light = _map.Lights[_current];
            LearnedKeyMaps.Set(_map.Product, light.Address, usage, moveFromOthers: true);
            _map = LearnedKeyMaps.Apply(_map);
            int named = _current;
            bool unnamedLeft = KeyIndexes().Any(i => _map.Lights[i].Usage == 0);
            if (unnamedLeft) Ask(NextAfter(named, unnamedOnly: true));
            else Stop();
        }

        /// <summary>Forgets the name learned for the light lit.</summary>
        public void Forget()
        {
            if (_current < 0 || string.IsNullOrEmpty(_map.Product)) return;
            LearnedKeyMaps.Set(_map.Product, _map.Lights[_current].Address, 0);
        }

        /// <summary>Forgets every name learned for the model.</summary>
        public void ForgetAll()
        {
            if (!string.IsNullOrEmpty(_map.Product)) LearnedKeyMaps.Replace(_map.Product, null);
        }

        /// <summary>
        /// Which of the usages Windows reports for one scan key the device
        /// has: two share 0x2B, the ANSI backslash and the ISO key left of
        /// Enter. The one the device names elsewhere is not the one pressed.
        /// Failing that, the input layout decides: US English keyboards are
        /// ANSI.
        /// </summary>
        internal ushort Choose(IReadOnlyList<ushort> usages)
        {
            if (usages == null || usages.Count == 0) return 0;
            if (usages.Count == 1) return usages[0];
            var named = new HashSet<ushort>(_map.Usages);
            var free = usages.Where(u => !named.Contains(u)).ToList();
            if (free.Count == 1) return free[0];
            ushort language = (ushort)((long)KbmHidKeyMap.UiLayout() & 0xFFFF);
            return language == 0x0409 ? usages[0] : usages[^1];
        }

        private void Ask(int index)
        {
            if (!IsKey(index)) return;
            _current = index;
            var lights = new Dictionary<int, int> { [_map.Lights[index].Address] = 0xFFFFFF };
            PeripheralOutputs.SetProbe(_path, new KeyFrame(null, null, lights, 0));
            Raise();
        }

        private int NextAfter(int index, bool unnamedOnly)
        {
            var keys = KeyIndexes().ToList();
            if (keys.Count == 0) return -1;
            int at = Math.Max(0, keys.IndexOf(index));
            for (int step = 1; step <= keys.Count; step++)
            {
                int candidate = keys[(at + step) % keys.Count];
                if (!unnamedOnly || _map.Lights[candidate].Usage == 0) return candidate;
            }
            return keys[(at + 1) % keys.Count];
        }

        private bool IsKey(int index) => index >= 0 && index < _map.Lights.Count && _map.Lights[index].Kind == LightKind.Key;

        private IEnumerable<int> KeyIndexes() => Enumerable.Range(0, _map.Lights.Count).Where(IsKey);

        /// <summary>A backend published the device's lights again, with a
        /// name just learned. Raised on the backend's thread.</summary>
        private void OnMapsChanged()
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(OnMapsChanged));
                return;
            }
            if (_disposed) return;
            var map = PeripheralLightMaps.For(_path);
            if (map == null || ReferenceEquals(map, _map)) return;
            int address = _current >= 0 && _current < _map.Lights.Count ? _map.Lights[_current].Address : -1;
            _map = map;
            if (address >= 0)
            {
                int index = Enumerable.Range(0, map.Lights.Count).FirstOrDefault(i => map.Lights[i].Address == address, -1);
                _current = IsKey(index) ? index : -1;
                if (_current < 0) PeripheralOutputs.SetProbe(_path, null);
            }
            Raise();
        }

        private void Raise()
        {
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(CanLearn));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(Status));
            LightsChanged?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PeripheralLightMaps.Changed -= OnMapsChanged;
            PeripheralOutputs.SetProbe(_path, null);
        }
    }
}
