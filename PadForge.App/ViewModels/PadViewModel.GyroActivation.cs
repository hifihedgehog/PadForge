using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>One button in the Engage card's Gyro On Buttons list: a
    /// device-free input and whether the shown layer's activation uses
    /// it.</summary>
    public sealed class GyroActivationButton : ObservableObject
    {
        private readonly Action _changed;

        public GyroActivationButton(string descriptor, string displayName, bool isOn, Action changed)
        {
            Descriptor = descriptor;
            DisplayName = displayName;
            _isOn = isOn;
            _changed = changed;
        }

        public string Descriptor { get; }
        public string DisplayName { get; }

        private bool _isOn;
        public bool IsOn
        {
            get => _isOn;
            set { if (SetProperty(ref _isOn, value)) _changed?.Invoke(); }
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>A choice of a picker on the Engage card: the stored value and
    /// the words shown for it.</summary>
    public sealed record GyroActivationChoice(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    public partial class PadViewModel
    {
        /// <summary>The Gyro On value of a layer with no entry of its own,
        /// which follows Base's.</summary>
        public const string GyroActivationSameAsBase = "";

        /// <summary>The Gyro On value of an entry with no buttons: the gyro
        /// stays on in that layer.</summary>
        public const string GyroActivationAlways = "Always";

        private string _gyroActivation = "";
        /// <summary>The device's gyro activation by layer
        /// (<see cref="PadSetting.GyroActivation"/>), in its text form. The
        /// Engage card edits one layer of it at a time.</summary>
        public string GyroActivation
        {
            get => _gyroActivation;
            set
            {
                if (SetProperty(ref _gyroActivation, value ?? ""))
                    ShowGyroActivationLayer();
            }
        }

        private bool _gyroRecenterOnEngage;
        /// <summary>Re-centers the gyro each time it turns on
        /// (<see cref="PadSetting.GyroRecenterOnEngage"/>).</summary>
        public bool GyroRecenterOnEngage
        {
            get => _gyroRecenterOnEngage;
            set => SetProperty(ref _gyroRecenterOnEngage, value);
        }

        private string _gyroActivationLayer = "Base";
        /// <summary>The layer whose activation the card shows: Base or a
        /// shift layer's mask, from <see cref="SwitchLayerChoices"/>.</summary>
        public string GyroActivationLayer
        {
            get => _gyroActivationLayer;
            set
            {
                if (SetProperty(ref _gyroActivationLayer, string.IsNullOrEmpty(value) ? "Base" : value))
                    ShowGyroActivationLayer();
            }
        }

        private string _gyroActivationMode = GyroActivationSameAsBase;
        /// <summary>How the shown layer turns the gyro on: Same as Base (no
        /// entry), Always, Hold, Release or Toggle. Reads null while the
        /// pickers' lists are replaced (<see cref="ShowGyroActivationLayer"/>).
        /// A null written back is ignored: the picker writes one only when
        /// its pick falls out of its list, which never stands for a choice.</summary>
        public string GyroActivationMode
        {
            get => _clearingGyroActivationPicks ? null : _gyroActivationMode;
            set
            {
                if (value == null) return;
                if (SetProperty(ref _gyroActivationMode, value))
                {
                    OnPropertyChanged(nameof(GyroActivationUsesButtons));
                    StoreGyroActivationLayer();
                }
            }
        }

        private bool _gyroActivationAll;
        /// <summary>True when the shown layer needs every checked button held,
        /// false when any one does.</summary>
        public bool GyroActivationAll
        {
            get => _gyroActivationAll;
            set { if (SetProperty(ref _gyroActivationAll, value)) StoreGyroActivationLayer(); }
        }

        /// <summary>True while the shown layer's mode reads buttons, which is
        /// when the Buttons and Needs rows show.</summary>
        public bool GyroActivationUsesButtons
            => _gyroActivationMode is "Hold" or "Release" or "Toggle";

        /// <summary>Every device-free input the activation can use, each
        /// checked when the shown layer's entry holds it.</summary>
        public ObservableCollection<GyroActivationButton> GyroActivationButtons { get; } = new();

        /// <summary>The Gyro On choices for the shown layer. Base has nothing
        /// to follow, so its no-entry choice reads Always On.</summary>
        public IReadOnlyList<GyroActivationChoice> GyroActivationModeChoices
            => string.Equals(_gyroActivationLayer, "Base", StringComparison.Ordinal)
                ? new[]
                {
                    new GyroActivationChoice(GyroActivationSameAsBase, Strings.Instance.Pad_GyroActivation_Always),
                    new GyroActivationChoice("Hold", Strings.Instance.Pad_GyroActivation_Hold),
                    new GyroActivationChoice("Release", Strings.Instance.Pad_GyroActivation_Release),
                    new GyroActivationChoice("Toggle", Strings.Instance.Pad_GyroActivation_Toggle),
                }
                : new[]
                {
                    new GyroActivationChoice(GyroActivationSameAsBase, Strings.Instance.Pad_GyroActivation_SameAsBase),
                    new GyroActivationChoice(GyroActivationAlways, Strings.Instance.Pad_GyroActivation_Always),
                    new GyroActivationChoice("Hold", Strings.Instance.Pad_GyroActivation_Hold),
                    new GyroActivationChoice("Release", Strings.Instance.Pad_GyroActivation_Release),
                    new GyroActivationChoice("Toggle", Strings.Instance.Pad_GyroActivation_Toggle),
                };

        /// <summary>The Needs choices: any one checked button, or all of
        /// them.</summary>
        public IReadOnlyList<GyroActivationChoice> GyroActivationNeedsChoices => new[]
        {
            new GyroActivationChoice("Any", Strings.Instance.Pad_GyroActivation_NeedsAny),
            new GyroActivationChoice("All", Strings.Instance.Pad_GyroActivation_NeedsAll),
        };

        /// <summary>The Needs picker's value, "Any" or "All". Reads null and
        /// ignores a null as <see cref="GyroActivationMode"/> does.</summary>
        public string GyroActivationNeeds
        {
            get => _clearingGyroActivationPicks ? null : _gyroActivationAll ? "All" : "Any";
            set
            {
                if (value == null) return;
                GyroActivationAll = string.Equals(value, "All", StringComparison.Ordinal);
                OnPropertyChanged();
            }
        }

        private bool _showingGyroActivation;
        private bool _clearingGyroActivationPicks;

        /// <summary>Loads the shown layer's entry into the card's pickers and
        /// its button list.</summary>
        private void ShowGyroActivationLayer()
        {
            _showingGyroActivation = true;
            try
            {
                var entries = global::PadForge.Engine.Data.GyroActivation.Parse(_gyroActivation);
                GyroActivationEntry entry = null;
                entries?.TryGetValue(_gyroActivationLayer, out entry);
                string mode = entry == null ? GyroActivationSameAsBase
                    : entry.Buttons.Length == 0
                        ? (string.Equals(_gyroActivationLayer, "Base", StringComparison.Ordinal) ? GyroActivationSameAsBase : GyroActivationAlways)
                        : entry.Mode;
                // Each list is made anew, in the current language, so a
                // picker's pick is not in the list that replaces it. A pick
                // that falls out of the list writes null back, and the
                // picker takes the same value again from its binding without
                // picking an item, so it shows nothing. The picks are cleared
                // first, then the lists replaced, then the values raised,
                // each of which picks its item in the new list.
                _clearingGyroActivationPicks = true;
                try
                {
                    OnPropertyChanged(nameof(GyroActivationMode));
                    OnPropertyChanged(nameof(GyroActivationNeeds));
                    OnPropertyChanged(nameof(GyroActivationModeChoices));
                    OnPropertyChanged(nameof(GyroActivationNeedsChoices));
                }
                finally { _clearingGyroActivationPicks = false; }
                _gyroActivationMode = mode;
                OnPropertyChanged(nameof(GyroActivationMode));
                SetProperty(ref _gyroActivationAll, entry?.All ?? false, nameof(GyroActivationAll));
                OnPropertyChanged(nameof(GyroActivationNeeds));
                OnPropertyChanged(nameof(GyroActivationUsesButtons));

                var on = new HashSet<string>(entry?.Buttons ?? Array.Empty<string>(), StringComparer.Ordinal);
                GyroActivationButtons.Clear();
                var listed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in Common.MappingDisplayResolver.BuildDeviceAgnosticChoices())
                {
                    if (string.IsNullOrEmpty(c.Descriptor) || !listed.Add(c.Descriptor)) continue;
                    GyroActivationButtons.Add(new GyroActivationButton(c.Descriptor, c.DisplayName,
                        on.Contains(c.Descriptor), StoreGyroActivationLayer));
                }
                // A button the list does not offer (an import's trigger pull or
                // a raw button) still shows, checked, so it can be turned off.
                foreach (var d in entry?.Buttons ?? Array.Empty<string>())
                {
                    if (!listed.Add(d)) continue;
                    GyroActivationButtons.Add(new GyroActivationButton(d,
                        Common.MappingDisplayResolver.ResolveDescriptorText(d, Array.Empty<global::PadForge.Engine.DeviceObjectItem>(), padPrefixAlways: true) ?? d,
                        true, StoreGyroActivationLayer));
                }
            }
            finally { _showingGyroActivation = false; }
        }

        /// <summary>Writes the shown layer's pickers and checked buttons back
        /// into <see cref="GyroActivation"/>, keeping every other layer's
        /// entry as it was.</summary>
        private void StoreGyroActivationLayer()
        {
            if (_showingGyroActivation) return;
            var entries = global::PadForge.Engine.Data.GyroActivation.Parse(_gyroActivation)
                ?? new Dictionary<string, GyroActivationEntry>(StringComparer.Ordinal);
            var ordered = new List<KeyValuePair<string, GyroActivationEntry>>();
            if (entries.TryGetValue("Base", out var baseEntry))
                ordered.Add(new("Base", baseEntry));
            foreach (var kv in entries)
                if (!string.Equals(kv.Key, "Base", StringComparison.Ordinal))
                    ordered.Add(kv);
            int at = ordered.FindIndex(kv => string.Equals(kv.Key, _gyroActivationLayer, StringComparison.Ordinal));
            if (at >= 0) ordered.RemoveAt(at);

            GyroActivationEntry entry = _gyroActivationMode switch
            {
                GyroActivationAlways => new GyroActivationEntry(),
                "Hold" or "Release" or "Toggle" => new GyroActivationEntry
                {
                    Mode = _gyroActivationMode,
                    All = _gyroActivationAll,
                    Buttons = GyroActivationButtons.Where(b => b.IsOn).Select(b => b.Descriptor).ToArray(),
                },
                _ => null,
            };
            if (entry != null)
            {
                var kv = new KeyValuePair<string, GyroActivationEntry>(_gyroActivationLayer, entry);
                if (string.Equals(_gyroActivationLayer, "Base", StringComparison.Ordinal)) ordered.Insert(0, kv);
                else if (at >= 0) ordered.Insert(Math.Min(at, ordered.Count), kv);
                else ordered.Add(kv);
            }

            string text = global::PadForge.Engine.Data.GyroActivation.Encode(ordered);
            if (string.Equals(text, _gyroActivation, StringComparison.Ordinal)) return;
            _gyroActivation = text;
            OnPropertyChanged(nameof(GyroActivation));
        }

        private RelayCommand _resetGyroActivationLayerCommand;
        /// <summary>Clears the shown layer's entry: Base keeps the gyro on,
        /// a shift layer follows Base.</summary>
        public RelayCommand ResetGyroActivationLayerCommand =>
            _resetGyroActivationLayerCommand ??= new RelayCommand(() => GyroActivationMode = GyroActivationSameAsBase);

        private RelayCommand _resetGyroActivationNeedsCommand;
        public RelayCommand ResetGyroActivationNeedsCommand =>
            _resetGyroActivationNeedsCommand ??= new RelayCommand(() => GyroActivationNeeds = "Any");

        private RelayCommand _resetGyroActivationButtonsCommand;
        public RelayCommand ResetGyroActivationButtonsCommand =>
            _resetGyroActivationButtonsCommand ??= new RelayCommand(() =>
            {
                _showingGyroActivation = true;
                try { foreach (var b in GyroActivationButtons) b.IsOn = false; }
                finally { _showingGyroActivation = false; }
                StoreGyroActivationLayer();
            });

        private RelayCommand _resetGyroRecenterOnEngageCommand;
        public RelayCommand ResetGyroRecenterOnEngageCommand =>
            _resetGyroRecenterOnEngageCommand ??= new RelayCommand(() => GyroRecenterOnEngage = false);
    }
}
