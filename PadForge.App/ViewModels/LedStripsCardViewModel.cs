using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Lighting tab's LED Strips card (#557): how many LEDs each strip of
    /// the selected device has, for a device that drives strips of a length
    /// only its user knows (<see cref="LightMap.Strips"/>). The counts live in
    /// the device's config for this controller
    /// (<see cref="DeviceSlotConfig.LedStripCounts"/>).
    /// </summary>
    public sealed class LedStripsCardViewModel : ObservableObject
    {
        private DeviceSlotConfig _config;

        public ObservableCollection<LedStripRow> Rows { get; } = new();

        /// <summary>Attaches the card to a device's config and its strips. A
        /// count that goes out republishes the map and comes back here, so the
        /// rows of the same strips stay, and a box being typed in keeps its
        /// focus.</summary>
        internal void Attach(DeviceSlotConfig config, LightMap map)
        {
            var strips = map?.Strips ?? Array.Empty<LightStrip>();
            bool same = ReferenceEquals(_config, config) && Rows.Count == strips.Count
                && strips.Select((s, i) => Rows[i].Number == s.Number && Rows[i].Maximum == s.MaxLeds).All(match => match);
            if (same) return;
            Detach();
            _config = config;
            if (config == null) return;
            foreach (var strip in strips) Rows.Add(new LedStripRow(config, strip.Number, strip.MaxLeds));
            config.PropertyChanged += OnConfigChanged;
        }

        internal void Detach()
        {
            if (_config != null) _config.PropertyChanged -= OnConfigChanged;
            _config = null;
            Rows.Clear();
        }

        private void OnConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DeviceSlotConfig.LedStripCounts)) return;
            foreach (var row in Rows) row.Refresh();
        }
    }

    /// <summary>One strip on the LED Strips card.</summary>
    public sealed class LedStripRow : ObservableObject
    {
        private readonly DeviceSlotConfig _config;

        internal LedStripRow(DeviceSlotConfig config, int number, int maximum)
        {
            _config = config;
            Number = number;
            Maximum = maximum;
            ResetCountCommand = new RelayCommand(() => Leds = 0);
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged() => OnPropertyChanged(nameof(Label));

        public int Number { get; }

        public int Maximum { get; }

        public string Label => string.Format(CultureInfo.CurrentCulture,
            Strings.Instance.Pad_Lighting_LedStrips_Header_Format, Number);

        /// <summary>The count, 0 for none. The box's clear button sends null,
        /// which is none as well.</summary>
        public double? Leds
        {
            get => StripCounts.At(_config.LedStripCountsParsed, Number);
            set
            {
                int count = (int)Math.Clamp(Math.Round(value ?? 0), 0, Maximum);
                if (count != StripCounts.At(_config.LedStripCountsParsed, Number))
                    _config.SetLedStripCount(Number, count);
                OnPropertyChanged();
            }
        }

        public IRelayCommand ResetCountCommand { get; }

        internal void Refresh() => OnPropertyChanged(nameof(Leds));
    }
}
