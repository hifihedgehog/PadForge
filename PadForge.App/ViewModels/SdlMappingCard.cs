using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>One line of the SDL Mapping card's table (#600 item 45).</summary>
    public sealed class SdlPlaceRow
    {
        public string Target { get; init; }
        public string Input { get; init; }
        public string RowSays { get; init; }
        public bool Differs { get; init; }

        /// <summary>The line as a screen reader reads it.</summary>
        public string ReadName => Target + ", " + Input + ", " + RowSays;
    }

    /// <summary>A controller the device is on, for the card's picker.</summary>
    public sealed record SdlControllerChoice(int Slot, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// The SDL Mapping card on the device panel (#600 item 45): how SDL reads
    /// the selected device, under what name and ID, from which source, the
    /// row, and beside each row of the picked controller the place SDL's row
    /// gives it. Its buttons run through the controller database's
    /// controller (<see cref="ControllerDatabaseController"/>), which reaches
    /// SDL on the poll thread. Interface thread only.
    /// </summary>
    public sealed class SdlMappingCard : ViewModelBase
    {
        private readonly DevicesViewModel _devices;
        private DeviceRowViewModel _tracked;
        private int _generation;
        private SdlCardFacts _facts = SdlCardFacts.Nothing;

        internal SdlMappingCard(DevicesViewModel devices)
        {
            _devices = devices;
            devices.PropertyChanged += OnDevicesChanged;
        }

        private ControllerDatabaseController _host;

        /// <summary>The controller database's controller, set when it
        /// starts.</summary>
        internal ControllerDatabaseController Host
        {
            get => _host;
            set
            {
                _host = value;
                Refresh();
            }
        }

        private void OnDevicesChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DevicesViewModel.SelectedDevice)) return;
            if (_tracked != null) _tracked.PropertyChanged -= OnDeviceChanged;
            _tracked = _devices.SelectedDevice;
            if (_tracked != null) _tracked.PropertyChanged += OnDeviceChanged;
            _picked = -1;
            Result = null;
            Refresh();
        }

        private void OnDeviceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceRowViewModel.IsOnline) or nameof(DeviceRowViewModel.SdlGuid)
                or nameof(DeviceRowViewModel.ForceRawJoystickMode) or nameof(DeviceRowViewModel.DeviceType))
                Refresh();
        }

        /// <summary>The device the card is for.</summary>
        internal Guid Instance => _tracked?.InstanceGuid ?? Guid.Empty;

        /// <summary>Reads the card's facts again.</summary>
        public void Refresh() => _ = RefreshAsync();

        internal async Task RefreshAsync()
        {
            int generation = ++_generation;
            var host = _host;
            Guid instance = Instance;
            var facts = host == null || instance == Guid.Empty ? SdlCardFacts.Nothing : await host.ReadAsync(instance);
            if (generation != _generation) return;
            Show(facts, host?.SlotsOf(instance).Select(s => new SdlControllerChoice(s.Slot, s.Pad.OutputTypeDisplayName + " #" + s.Pad.TypeInstanceLabel)).ToList()
                ?? new List<SdlControllerChoice>());
        }

        /// <summary>Puts facts on the card. Tests call it directly.</summary>
        internal void Show(SdlCardFacts facts, IReadOnlyList<SdlControllerChoice> controllers)
        {
            _facts = facts ?? SdlCardFacts.Nothing;
            Controllers.Clear();
            foreach (var c in controllers) Controllers.Add(c);
            if (!controllers.Any(c => c.Slot == _picked)) _picked = controllers.Count > 0 ? controllers[0].Slot : -1;
            OnPropertyChanged(nameof(PickedController));
            FillPlaces();
            OnPropertyChanged(string.Empty);
        }

        private void FillPlaces()
        {
            Places.Clear();
            string name = Controllers.FirstOrDefault(c => c.Slot == _picked)?.Name;
            foreach (var (controller, lines) in _facts.Places)
            {
                if (!string.Equals(controller, name, StringComparison.Ordinal)) continue;
                foreach (var line in lines)
                    Places.Add(new SdlPlaceRow
                    {
                        Target = line.Target,
                        Input = line.Input,
                        RowSays = line.RowSays == null ? Strings.Instance.SdlMapping_NotInRow
                            : line.Differs ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_Differs_Format, line.RowSays)
                            : line.RowSays,
                        Differs = line.Differs,
                    });
            }
        }

        protected override void OnCultureChanged() => Refresh();

        // ─────────────────────────────────────────────
        //  What the card shows
        // ─────────────────────────────────────────────

        public bool ShowCard => _facts.Reading != SdlReading.None;

        internal SdlReading Reading => _facts.Reading;

        public string ReadAsText => _facts.Reading switch
        {
            SdlReading.Gamepad => string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_Gamepad_Format, _facts.Name ?? string.Empty),
            SdlReading.Joystick => Strings.Instance.SdlMapping_Joystick,
            SdlReading.ReadRaw => Strings.Instance.SdlMapping_ReadRaw,
            SdlReading.PadForgeDevice => _facts.Mine != null ? Strings.Instance.SdlMapping_PadForgeDeviceMapped : Strings.Instance.SdlMapping_Joystick,
            SdlReading.Stopped => Strings.Instance.SdlMapping_Stopped,
            SdlReading.Away => Strings.Instance.SdlMapping_Away,
            SdlReading.NoAnswer => Strings.Instance.SdlMapping_NoAnswer,
            _ => string.Empty,
        };

        public string FromText => ControllerDatabaseController.SourceWords(_facts.Source, _facts.SourceDate);

        public bool ShowFrom => !string.IsNullOrEmpty(FromText);

        /// <summary>The source is the user's own row, drawn in the accent.</summary>
        public bool FromIsYours => _facts.Source is SdlMappingSource.Yours or SdlMappingSource.YoursPadForge;

        public string IdText => _facts.Id ?? string.Empty;

        public bool ShowId => !string.IsNullOrEmpty(_facts.Id) && _facts.Reading != SdlReading.PadForgeDevice;

        /// <summary>The row in the community database's form.</summary>
        public string RowText => _facts.RowText ?? string.Empty;

        public bool ShowRow => !string.IsNullOrEmpty(_facts.RowText);

        /// <summary>The fields of the row no row of the device's controllers
        /// reads.</summary>
        public string UnusedText => _facts.Unused.Count == 0 ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_Unused_Format, string.Join(", ", _facts.Unused));

        public bool ShowUnused => _facts.Unused.Count > 0;

        public ObservableCollection<SdlPlaceRow> Places { get; } = new();

        public bool ShowPlaces => Places.Count > 0;

        public ObservableCollection<SdlControllerChoice> Controllers { get; } = new();

        public bool ShowControllerPicker => Controllers.Count > 1;

        private int _picked = -1;

        /// <summary>The controller whose rows the table and Copy read.</summary>
        public SdlControllerChoice PickedController
        {
            get => Controllers.FirstOrDefault(c => c.Slot == _picked);
            set
            {
                if (value == null || value.Slot == _picked) return;
                _picked = value.Slot;
                FillPlaces();
                // The last Copy's line was for the controller picked before.
                Result = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowPlaces));
                OnPropertyChanged(nameof(PlacesHeader));
                OnPropertyChanged(nameof(MoreHint));
                OnPropertyChanged(nameof(ShowMore));
            }
        }

        /// <summary>The slot of the controller picked, for tests.</summary>
        internal int PickedSlot => _picked;

        public string PlacesHeader => string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_Col_Row_Format,
            PickedController?.Name ?? string.Empty);

        /// <summary>The More row's hint: what it holds, and which controller
        /// it compares.</summary>
        public string MoreHint => PickedController == null ? Strings.Instance.SdlMapping_More_NoController
            : string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_More_Format, PickedController.Name);

        /// <summary>The More row, which holds the row, the table and the
        /// Controller picker: shown while any of them has something, so a
        /// picked controller with no rows the table reads leaves the picker
        /// there to pick another.</summary>
        public bool ShowMore => ShowRow || ShowPlaces || ShowControllerPicker;

        public bool HasMine => _facts.Mine != null;

        public bool ShowCopy => _facts.Reading is SdlReading.Gamepad or SdlReading.ReadRaw || Controllers.Count > 0 || HasMine;

        public bool ShowPaste => !HasMine;

        public bool ShowGuess => !HasMine && _facts.Reading is SdlReading.Joystick or SdlReading.PadForgeDevice;

        public bool ShowMapAgain => Controllers.Count > 0 && (_facts.Reading == SdlReading.Gamepad
            || (_facts.Reading == SdlReading.PadForgeDevice && HasMine));

        /// <summary>The name the dialogs give the device.</summary>
        internal string DeviceName => _tracked?.DeviceName ?? string.Empty;

        /// <summary>The device's raw counts, for Guess a Layout.</summary>
        internal SdlCardFacts Facts => _facts;

        private string _result;

        /// <summary>What the last button came to, under the buttons.</summary>
        public string Result
        {
            get => _result;
            set
            {
                if (SetProperty(ref _result, value)) OnPropertyChanged(nameof(ShowResult));
            }
        }

        public bool ShowResult => !string.IsNullOrEmpty(_result);

        // ─────────────────────────────────────────────
        //  The buttons that need no dialog
        // ─────────────────────────────────────────────

        private AsyncRelayCommand _copy, _removeMine;
        private RelayCommand _mapAgain;

        /// <summary>Copy SDL Mapping (#600 item 46).</summary>
        public AsyncRelayCommand CopyCommand => _copy ??= new AsyncRelayCommand(CopyAsync);

        /// <summary>Remove Mine (#600 item 47).</summary>
        public AsyncRelayCommand RemoveMineCommand => _removeMine ??= new AsyncRelayCommand(async () =>
        {
            if (_host == null) return;
            await _host.RemoveMineAsync(Instance);
            Result = Strings.Instance.SdlMapping_Removed;
            await RefreshAsync();
        });

        /// <summary>Map Again (#600 item 45).</summary>
        public RelayCommand MapAgainCommand => _mapAgain ??= new RelayCommand(() =>
        {
            if (_host == null) return;
            _host.Remap(Instance);
            Result = string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_MappedAgain_Format,
                string.Join(", ", Controllers.Select(c => c.Name)));
            Refresh();
        });

        /// <summary>Applies a row a dialog returned (Paste, Find, Guess) as the
        /// user's own, and says what it came to.</summary>
        internal async Task ApplyAsync(SdlMappingRow row)
        {
            if (_host == null || row == null) return;
            var result = await _host.ApplyMineAsync(Instance, row);
            Result = Outcome(result);
            await RefreshAsync();
        }

        /// <summary>Where the clipboard is written, replaceable in tests.</summary>
        internal Action<string> SetClipboard = PadForge.Common.ShellLaunch.SetClipboardText;

        private async Task CopyAsync()
        {
            if (_host == null) return;
            var (text, leftOut, empty) = await _host.CopyAsync(Instance, _picked);
            if (text == null)
            {
                Result = NothingWords(empty, PickedController?.Name, leftOut);
                return;
            }
            try { SetClipboard(text); }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program holds the clipboard. Nothing was copied.
                Result = Strings.Instance.SdlMapping_ClipboardBusy;
                return;
            }
            Result = LeftOutWords(leftOut) is { } words
                ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_CopiedLeftOut_Format, words)
                : Strings.Instance.SdlMapping_Copied;
        }

        /// <summary>Why Copy had nothing to copy, in words: the picked
        /// controller's rows and what they read the device for, when they
        /// are the reason.</summary>
        internal static string NothingWords(SdlCopyEmpty empty, string controller, SdlLeftOut rowsLeft)
        {
            var s = Strings.Instance;
            if (empty == SdlCopyEmpty.Unreadable) return s.SdlMapping_CopyUnreadable;
            if (empty == SdlCopyEmpty.NotRead)
                return controller == null ? s.SdlMapping_CopyNotRead
                    : string.Format(CultureInfo.CurrentCulture, s.SdlMapping_CopyNotReadInRows_Format, controller);
            if (empty != SdlCopyEmpty.NothingInRows || controller == null) return s.SdlMapping_CopyNothing;
            return LeftOutWords(rowsLeft) is { } words
                ? string.Format(CultureInfo.CurrentCulture, s.SdlMapping_CopyOnlyLeftOut_Format, controller, words)
                : string.Format(CultureInfo.CurrentCulture, s.SdlMapping_CopyNothingInRows_Format, controller);
        }

        /// <summary>What SDL's form cannot hold, in words, or null.</summary>
        internal static string LeftOutWords(SdlLeftOut leftOut)
        {
            var s = Strings.Instance;
            var parts = new List<string>();
            if (leftOut.HasFlag(SdlLeftOut.Macros)) parts.Add(s.SdlMapping_LeftOut_Macros);
            if (leftOut.HasFlag(SdlLeftOut.Curves)) parts.Add(s.SdlMapping_LeftOut_Curves);
            if (leftOut.HasFlag(SdlLeftOut.DeadZones)) parts.Add(s.SdlMapping_LeftOut_DeadZones);
            if (leftOut.HasFlag(SdlLeftOut.KeyboardOutputs)) parts.Add(s.SdlMapping_LeftOut_Keys);
            if (leftOut.HasFlag(SdlLeftOut.Actions)) parts.Add(s.SdlMapping_LeftOut_Actions);
            if (parts.Count == 0) return null;
            if (parts.Count == 1) return parts[0];
            return string.Format(CultureInfo.CurrentCulture, s.SdlMapping_ListAnd_Format,
                string.Join(", ", parts.Take(parts.Count - 1)), parts[^1]);
        }

        /// <summary>What applying a row came to, in words.</summary>
        internal static string Outcome(SdlApplyResult result) => result switch
        {
            SdlApplyResult.InUse => Strings.Instance.SdlMapping_Applied,
            SdlApplyResult.InPadForge => Strings.Instance.SdlMapping_Applied,
            SdlApplyResult.AtStart => Strings.Instance.SdlMapping_AppliesAtStart,
            SdlApplyResult.Full => string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_Full_Format,
                PadForge.Common.Input.ControllerDatabaseMine.MaxRows),
            _ => Strings.Instance.SdlMapping_SdlRefused,
        };
    }
}
