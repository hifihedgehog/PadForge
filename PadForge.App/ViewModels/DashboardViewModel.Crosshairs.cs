using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Crosshair and Magnifier pages' lists (#585), kept like the Screen
    /// Overlay list (#560): numbered crosshairs, at least one, and one
    /// magnifier, each saved globally and in every profile.
    /// </summary>
    public partial class DashboardViewModel
    {
        // ─────────────────────────────────────────────
        //  Crosshairs
        // ─────────────────────────────────────────────

        /// <summary>The crosshairs on the page. There is always at least one,
        /// so the page reads like a single extra's until someone adds
        /// another.</summary>
        public ObservableCollection<CrosshairItem> Crosshairs { get; } = new();

        private RelayCommand _addCrosshairCommand;
        public RelayCommand AddCrosshairCommand => _addCrosshairCommand ??= new RelayCommand(AddCrosshair);

        public bool CanAddCrosshair => Crosshairs.Count < CrosshairData.MaxCrosshairs;

        /// <summary>A new crosshair takes the lowest free number, so a macro
        /// that names a number finds the crosshair that took it.</summary>
        internal void AddCrosshair()
        {
            if (!CanAddCrosshair) return;
            int number = 1;
            while (FindCrosshair(number) != null) number++;
            int at = 0;
            while (at < Crosshairs.Count && Crosshairs[at].Number < number) at++;
            Crosshairs.Insert(at, new CrosshairItem(this, number));
            CrosshairListChanged();
        }

        internal void RemoveCrosshair(CrosshairItem item)
        {
            if (Crosshairs.Count <= 1) return;
            Crosshairs.Remove(item);
            CrosshairListChanged();
        }

        internal CrosshairItem FindCrosshair(int number)
        {
            foreach (var item in Crosshairs)
                if (item.Number == number) return item;
            return null;
        }

        /// <summary>The list for the settings file.</summary>
        internal CrosshairData[] SnapshotCrosshairs()
        {
            var data = new CrosshairData[Crosshairs.Count];
            for (int i = 0; i < data.Length; i++) data[i] = Crosshairs[i].ToData();
            return data;
        }

        /// <summary>Takes a list from the settings file. A file or profile from
        /// before crosshairs existed has none, which is one crosshair, off. An
        /// entry whose number is out of range or already taken is
        /// dropped.</summary>
        internal void LoadCrosshairs(CrosshairData[] data)
        {
            _loadingCrosshairs = true;
            try
            {
                Crosshairs.Clear();
                if (data != null)
                {
                    foreach (var entry in data)
                    {
                        if (entry == null || entry.Number < 1 || entry.Number > CrosshairData.MaxCrosshairs
                            || FindCrosshair(entry.Number) != null) continue;
                        int at = 0;
                        while (at < Crosshairs.Count && Crosshairs[at].Number < entry.Number) at++;
                        var item = new CrosshairItem(this, entry.Number);
                        Crosshairs.Insert(at, item);
                        item.Load(entry);
                    }
                }
                if (Crosshairs.Count == 0) Crosshairs.Add(new CrosshairItem(this, 1));
            }
            finally { _loadingCrosshairs = false; }
            CrosshairListChanged();
        }

        private bool _loadingCrosshairs;

        private void CrosshairListChanged()
        {
            foreach (var item in Crosshairs) item.RefreshOneOfSeveral();
            OnPropertyChanged(nameof(CanAddCrosshair));
            CrosshairEdited();
        }

        /// <summary>A saved value of a crosshair changed. The property change
        /// marks the settings dirty and tells InputService to bring the
        /// windows into line.</summary>
        internal void CrosshairEdited()
        {
            if (!_loadingCrosshairs) OnPropertyChanged(nameof(Crosshairs));
        }

        // ─────────────────────────────────────────────
        //  Magnifier
        // ─────────────────────────────────────────────

        private MagnifierItem _magnifier;

        /// <summary>The magnifier's settings.</summary>
        public MagnifierItem Magnifier => _magnifier ??= new MagnifierItem(this);

        /// <summary>The Extras switch of Magnifier and Home's: the magnifier's
        /// own switch, a property of this view model as every catalog switch
        /// is.</summary>
        public bool MagnifierEnabled
        {
            get => Magnifier.Enabled;
            set => Magnifier.Enabled = value;
        }

        private bool _loadingMagnifier;

        internal MagnifierData SnapshotMagnifier() => Magnifier.ToData();

        /// <summary>Takes the settings file's magnifier. Null, from a file or
        /// profile from before the magnifier, is the defaults: off.</summary>
        internal void LoadMagnifier(MagnifierData data)
        {
            _loadingMagnifier = true;
            try { Magnifier.Load(data); }
            finally { _loadingMagnifier = false; }
            MagnifierEdited();
        }

        /// <summary>A saved value of the magnifier changed: the settings are
        /// dirty and InputService brings the magnifier into line.</summary>
        internal void MagnifierEdited()
        {
            if (!_loadingMagnifier) OnPropertyChanged(nameof(Magnifier));
        }
    }
}
