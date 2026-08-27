using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>The Audio tab's DSP chain surface (#347): crossfeed, the
    /// parametric EQ and the limiter.
    ///
    /// <para>The band list lives on <c>DeviceConfig</c> as one encoded
    /// attribute, so it round trips with every other per-device audio setting
    /// instead of needing its own element shape. The grid needs rows, so this
    /// is the two-way bridge: rows are rebuilt when the selected device
    /// changes, and any row edit re-encodes straight back into the config,
    /// which is both what marks the profile dirty and what the engine's
    /// provider reads at its next refresh.</para></summary>
    public partial class PadViewModel
    {
        private readonly ObservableCollection<EqBandVm> _eqBands = new();
        public ObservableCollection<EqBandVm> EqBands => _eqBands;

        private bool _suppressEqPush;

        /// <summary>Rebuilds the grid rows from the selected device's config.
        /// Called on every device switch, because the bands are per device.</summary>
        public void RefreshEqBands()
        {
            // The import status describes the config that was bound when the
            // import ran, so it goes with the rows. Leaving it would have this
            // pad's card reporting what the previous pad imported, which is
            // the same stale-state shape the rebuild itself exists to prevent.
            SetEqImportStatus(string.Empty);
            _suppressEqPush = true;
            try
            {
                foreach (var r in _eqBands) r.Owner = null;
                _eqBands.Clear();
                var cfg = DeviceConfig;
                if (cfg == null) return;
                foreach (var b in EqBandCodec.Decode(cfg.AudioEqBands))
                    _eqBands.Add(new EqBandVm(b) { Owner = this });
            }
            finally { _suppressEqPush = false; }
        }

        /// <summary>Re-encodes the rows into the config. Every row edit and
        /// every add, remove, import or clear goes through here.</summary>
        internal void PushEqBands()
        {
            if (_suppressEqPush) return;
            var cfg = DeviceConfig;
            if (cfg == null) return;
            var list = new List<EqBand>(_eqBands.Count);
            foreach (var r in _eqBands) list.Add(r.ToBand());
            cfg.AudioEqBands = EqBandCodec.Encode(list);
        }

        private RelayCommand _addEqBandCommand;
        public RelayCommand AddEqBandCommand =>
            _addEqBandCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig == null) return;
                _eqBands.Add(new EqBandVm(new EqBand()) { Owner = this });
                PushEqBands();
            });

        private RelayCommand<EqBandVm> _removeEqBandCommand;
        public RelayCommand<EqBandVm> RemoveEqBandCommand =>
            _removeEqBandCommand ??= new RelayCommand<EqBandVm>(row =>
            {
                if (row == null) return;
                row.Owner = null;
                _eqBands.Remove(row);
                PushEqBands();
            });

        private RelayCommand _clearEqBandsCommand;
        public RelayCommand ClearEqBandsCommand =>
            _clearEqBandsCommand ??= new RelayCommand(() =>
            {
                foreach (var r in _eqBands) r.Owner = null;
                _eqBands.Clear();
                PushEqBands();
            });

        private string _eqImportStatus = string.Empty;
        /// <summary>What the last import did, shown under the buttons.
        ///
        /// <para>An import that finds nothing must SAY so. Declining to touch a
        /// tuned EQ is the right behavior and staying silent about it is not:
        /// AutoEq publishes three formats and only two of them carry Filter
        /// lines, so the likeliest reason an import does nothing is that the
        /// user grabbed the Graphic EQ one, and a silent no-op reads as a
        /// broken button.</para></summary>
        public string EqImportStatus
        {
            get => _eqImportStatus;
            private set => SetProperty(ref _eqImportStatus, value ?? string.Empty);
        }

        public bool HasEqImportStatus => !string.IsNullOrEmpty(_eqImportStatus);

        private void SetEqImportStatus(string s)
        {
            EqImportStatus = s;
            OnPropertyChanged(nameof(HasEqImportStatus));
        }

        /// <summary>Applies a parsed AutoEq profile. The single body behind
        /// BOTH import commands, so the file path and the clipboard path
        /// cannot drift.
        ///
        /// <para>It REPLACES the bands rather than appending. A profile is a
        /// complete correction for one set of headphones, and merging two of
        /// them produces something neither author intended.</para>
        ///
        /// <para>Nothing parseable leaves the current EQ untouched. Importing
        /// the wrong thing must not be a way to lose a tuned EQ.</para></summary>
        private void ApplyAutoEqText(string text, string sourceLabel)
        {
            var cfg = DeviceConfig;
            if (cfg == null) return;

            if (string.IsNullOrWhiteSpace(text))
            {
                SetEqImportStatus(Strings.Instance.Pad_Audio_EqImport_Empty);
                return;
            }

            var (bands, preamp) = AutoEqProfile.Parse(text);
            if (bands.Count == 0)
            {
                SetEqImportStatus(Strings.Instance.Pad_Audio_EqImport_NoFilters);
                return;
            }

            _suppressEqPush = true;
            try
            {
                foreach (var r in _eqBands) r.Owner = null;
                _eqBands.Clear();
                foreach (var b in bands) _eqBands.Add(new EqBandVm(b) { Owner = this });
            }
            finally { _suppressEqPush = false; }

            PushEqBands();
            // AutoEq ships its preamp precisely so the profile's boosts do not
            // clip, so importing the bands without it would be worse than not
            // importing at all.
            cfg.AudioEqPreampDb = preamp;
            cfg.AudioEqEnabled = true;
            SetEqImportStatus(string.Format(Strings.Instance.Pad_Audio_EqImport_Ok_Format,
                                            bands.Count, preamp, sourceLabel));
        }

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests). Both import
        /// commands end in a file dialog or the clipboard, neither of which a
        /// test can drive, so the body they share is reachable directly.</summary>
        internal void ImportAutoEqTextForTest(string text, string sourceLabel)
            => ApplyAutoEqText(text, sourceLabel);

        /// <summary>Imports the .txt file AutoEq downloads.
        ///
        /// <para>This is the gesture the site actually produces. autoeq.app's
        /// parametric export is a DOWNLOAD, not a copyable block, so an import
        /// that only read the clipboard asked for something the source never
        /// offers. The clipboard command below stays for the profiles that do
        /// arrive as text (the AutoEq repo, a forum post), but this is the one
        /// to reach for first.</para></summary>
        private RelayCommand _importAutoEqFileCommand;
        public RelayCommand ImportAutoEqFileCommand =>
            _importAutoEqFileCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig == null) return;
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Strings.Instance.Pad_Audio_EqImportFile,
                    // ParametricEQ.txt is what autoeq.app hands you, and
                    // FixedBandEQ.txt parses through the same Filter lines.
                    // GraphicEQ.txt does not, and is deliberately still
                    // selectable: picking it and being told why beats not
                    // seeing the file and assuming the dialog is broken.
                    Filter = "AutoEq profile (*.txt)|*.txt|All files|*.*",
                    CheckFileExists = true,
                };
                try { if (dlg.ShowDialog() != true) return; }
                catch { return; }

                string text;
                try { text = System.IO.File.ReadAllText(dlg.FileName); }
                catch (Exception ex)
                {
                    SetEqImportStatus(string.Format(
                        Strings.Instance.Pad_Audio_EqImport_ReadFailed_Format, ex.Message));
                    return;
                }
                ApplyAutoEqText(text, System.IO.Path.GetFileName(dlg.FileName));
            });

        /// <summary>Imports an AutoEq profile from the clipboard, for the
        /// profiles that arrive as text rather than as a download.</summary>
        private RelayCommand _importAutoEqCommand;
        public RelayCommand ImportAutoEqCommand =>
            _importAutoEqCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig == null) return;
                string text;
                try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
                catch (Exception ex)
                {
                    // The clipboard can be locked by another process. That used
                    // to be a silent return, which is the exact shape the status
                    // line exists to remove: the one button did nothing and said
                    // nothing. Same string the file path uses for a read it
                    // could not complete.
                    SetEqImportStatus(string.Format(
                        Strings.Instance.Pad_Audio_EqImport_ReadFailed_Format, ex.Message));
                    return;
                }
                ApplyAutoEqText(text, Strings.Instance.Pad_Audio_EqImport_ClipboardLabel);
            });

        private RelayCommand _resetCrossfeedCommand;
        public RelayCommand ResetCrossfeedCommand =>
            _resetCrossfeedCommand ??= new RelayCommand(() =>
            { if (DeviceConfig != null) DeviceConfig.AudioCrossfeedLevel = 0; });

        // The two custom knobs reset to libbs2b's own defaults (700 Hz and
        // 4.5 dB, BS2B_DEFAULT_CLEVEL), not to zero. Zero is outside the
        // library's accepted range, where its init() silently substitutes the
        // default anyway, so a reset to zero would show a value the DSP does
        // not use.
        private RelayCommand _resetCrossfeedCutCommand;
        public RelayCommand ResetCrossfeedCutCommand =>
            _resetCrossfeedCutCommand ??= new RelayCommand(() =>
            { if (DeviceConfig != null) DeviceConfig.AudioCrossfeedCutHz = 700; });

        private RelayCommand _resetCrossfeedFeedCommand;
        public RelayCommand ResetCrossfeedFeedCommand =>
            _resetCrossfeedFeedCommand ??= new RelayCommand(() =>
            { if (DeviceConfig != null) DeviceConfig.AudioCrossfeedFeedDb = 4.5d; });

        private RelayCommand _resetEqPreampCommand;
        public RelayCommand ResetEqPreampCommand =>
            _resetEqPreampCommand ??= new RelayCommand(() =>
            { if (DeviceConfig != null) DeviceConfig.AudioEqPreampDb = 0; });

        private RelayCommand _resetEqCommand;
        public RelayCommand ResetEqCommand =>
            _resetEqCommand ??= new RelayCommand(() =>
            {
                var cfg = DeviceConfig;
                if (cfg == null) return;
                cfg.AudioEqEnabled = false;
                cfg.AudioEqPreampDb = 0;
                ClearEqBandsCommand.Execute(null);
            });

        private RelayCommand _resetLimiterCommand;
        public RelayCommand ResetLimiterCommand =>
            _resetLimiterCommand ??= new RelayCommand(() =>
            { if (DeviceConfig != null) DeviceConfig.AudioLimiterEnabled = true; });

        private RelayCommand _resetLimiterCeilingCommand;
        public RelayCommand ResetLimiterCeilingCommand =>
            _resetLimiterCeilingCommand ??= new RelayCommand(() =>
            { if (DeviceConfig != null) DeviceConfig.AudioLimiterCeiling = 98; });
    }

    /// <summary>One editable EQ band row. Every setter re-encodes the whole
    /// list back into the device config, which is what makes the grid an
    /// editor of the saved setting rather than a view over a throwaway copy.
    ///
    /// <para>Clamps match the engine's. A band the UI accepts but the DSP
    /// silently rejects is a worse experience than one the UI refuses.</para></summary>
    public class EqBandVm : ObservableObject
    {
        internal PadViewModel Owner;

        public EqBandVm(EqBand b)
        {
            _enabled = b.Enabled;
            _type = b.Type;
            _frequencyHz = b.FrequencyHz;
            _gainDb = b.GainDb;
            _q = b.Q;
        }

        /// <summary>Public twin of <see cref="ToBand"/> for the curve control,
        /// which lives in another namespace and needs the band to compute the
        /// response it draws.</summary>
        public EqBand ToBandPublic() => ToBand();

        internal EqBand ToBand() => new EqBand
        {
            Enabled = _enabled,
            Type = _type,
            FrequencyHz = _frequencyHz,
            GainDb = _gainDb,
            Q = _q,
        };

        private void Push() => Owner?.PushEqBands();

        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set { if (SetProperty(ref _enabled, value)) Push(); }
        }

        private EqBandType _type;
        public EqBandType Type
        {
            get => _type;
            set { if (SetProperty(ref _type, value)) Push(); }
        }

        private float _frequencyHz;
        public float FrequencyHz
        {
            get => _frequencyHz;
            // The engine's own ceiling, not a rounder number near it. The old
            // 24000 let the editor accept a band the DSP then silently pulled
            // down to 21600, which is the exact thing this class's doc comment
            // says it does not do.
            set
            {
                if (SetProperty(ref _frequencyHz, Math.Clamp(value, 10f, EqBand.MaxFrequencyHz())))
                    Push();
            }
        }

        private float _gainDb;
        public float GainDb
        {
            get => _gainDb;
            set { if (SetProperty(ref _gainDb, Math.Clamp(value, -30f, 30f))) Push(); }
        }

        private float _q;
        public float Q
        {
            get => _q;
            set { if (SetProperty(ref _q, Math.Clamp(value, 0.05f, 20f))) Push(); }
        }
    }
}
