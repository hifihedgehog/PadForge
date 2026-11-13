using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class ProfilesPage : UserControl
    {
        public ProfilesPage()
        {
            InitializeComponent();
            // The drop tile shares the cards' grid through a
            // CompositeCollection, and its CollectionContainer sits outside
            // the visual tree, so a DataContext binding cannot reach the
            // profiles. Hand it the collection directly whenever the view
            // model lands, as Home does for its cards.
            DataContextChanged += (_, e) =>
            {
                if (e.NewValue is SettingsViewModel vm)
                    ProfilesCollectionContainer.Collection = vm.ProfileItems;
            };
            ShowStarters();
            Loaded += (_, _) =>
            {
                Strings.CultureChanged -= ShowStarters;
                Strings.CultureChanged += ShowStarters;
                ShowStarters();
            };
            Unloaded += (_, _) => Strings.CultureChanged -= ShowStarters;
        }

        // ─────────────────────────────────────────────
        //  Starter profiles (#574)
        // ─────────────────────────────────────────────

        /// <summary>How many starter profiles the page shows: the catalog's
        /// first four, one row of tiles on the board. Get Profiles lists
        /// them all.</summary>
        internal const int StarterTileCount = 4;

        /// <summary>Set by MainWindow: adds a starter profile to the user's
        /// profiles, as Save Profile does in Get Profiles.</summary>
        internal Action<StarterProfileInfo> AddStarter { get; set; }

        private string _lastStarterKey;
        private long _lastStarterAt;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        /// <summary>A tile reads its words from the strings as it is made,
        /// so a change of language makes the tiles again.</summary>
        private void ShowStarters()
            => StarterTiles.ItemsSource = StarterProfileCatalog.All.Take(StarterTileCount).ToArray();

        /// <summary>Whether a click on a starter's tile adds its profile.
        /// The second click of a double click on one tile does not, nor does
        /// a held Enter's repeat: each would add the profile again under a
        /// numbered name.</summary>
        internal static bool AddsStarter(string key, string lastKey, long now, long lastAt, long doubleClickMs)
            => !string.Equals(key, lastKey, StringComparison.Ordinal) || now - lastAt > doubleClickMs;

        private void StarterTile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not StarterProfileInfo info) return;
            long now = Environment.TickCount64;
            bool adds = AddsStarter(info.Key, _lastStarterKey, now, _lastStarterAt, GetDoubleClickTime());
            _lastStarterKey = info.Key;
            _lastStarterAt = now;
            if (adds) AddStarter?.Invoke(info);
        }

        // ─────────────────────────────────────────────
        //  The cards (#574)
        // ─────────────────────────────────────────────

        /// <summary>A double click on a card loads its profile, as a double click
        /// on the list's row did. The card's Load button does it from the keyboard.</summary>
        private void ProfileCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || DataContext is not SettingsViewModel vm) return;
            if ((sender as FrameworkElement)?.DataContext is not ProfileListItem item) return;
            if (!vm.LoadProfileCommand.CanExecute(item)) return;
            vm.LoadProfileCommand.Execute(item);
            e.Handled = true;
        }

        /// <summary>Opens a card's More menu under its button, on the card's own
        /// profile. A context menu is outside the page's tree, so it is handed
        /// the card's item for its gates and its items' clicks.</summary>
        private void CardMore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu == null) return;
            button.ContextMenu.DataContext = button.DataContext;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        /// <summary>A More menu's item runs its command on the card's profile.</summary>
        private void ProfileMenu_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm) return;
            if (sender is not FrameworkElement { DataContext: ProfileListItem item, Tag: string action }) return;
            ICommand command = action switch
            {
                "Edit" => vm.EditProfileCommand,
                "SaveAs" => vm.SaveAsProfileCommand,
                "SaveHere" => vm.SaveSetupHereCommand,
                "Export" => vm.ExportProfileCommand,
                "Delete" => vm.DeleteProfileCommand,
                _ => null,
            };
            if (command?.CanExecute(item) == true) command.Execute(item);
        }

        /// <summary>The drop tile opens Get Profiles on Import a File, as the board links it.</summary>
        private void DropTile_Click(object sender, RoutedEventArgs e)
            => (DataContext as SettingsViewModel)?.GetProfilesCommand.Execute("File");

        // ─────────────────────────────────────────────
        //  Drop-zone import (#175)
        // ─────────────────────────────────────────────

        /// <summary>Set by MainWindow: consumes a dropped profile file through
        /// the same code path as Get Profiles' Import a File.</summary>
        internal Action<string> ImportProfileFile { get; set; }

        /// <summary>A file Import a File takes: a .pfprofile, or another
        /// tool's config (#561), whose content then decides. Get Profiles'
        /// drop zone takes the same files.</summary>
        internal static bool IsImportableFile(string file)
            => file != null
               && (file.EndsWith(PadForge.Common.ProfileTransfer.FileExtension, StringComparison.OrdinalIgnoreCase)
                   || PadForge.Common.ConfigImport.ForeignConfigImporter.HandlesFile(file));

        /// <summary>True when the drag carries at least one importable file.</summary>
        internal static bool IsProfileFileDrag(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            return e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                   files.Any(IsImportableFile);
        }

        private void ProfileList_DragEnter(object sender, DragEventArgs e)
        {
            bool valid = IsProfileFileDrag(e);
            e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
            ProfileDropOverlay.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void ProfileList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsProfileFileDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ProfileList_DragLeave(object sender, DragEventArgs e)
        {
            ProfileDropOverlay.Visibility = Visibility.Collapsed;
        }

        private void ProfileList_Drop(object sender, DragEventArgs e)
        {
            ProfileDropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var file in files)
            {
                if (IsImportableFile(file))
                    ImportProfileFile?.Invoke(file);
            }
            e.Handled = true;
        }

        // ─────────────────────────────────────────────
        //  Profile shortcuts
        // ─────────────────────────────────────────────

        /// <summary>Set by MainWindow to enable shortcut recording.</summary>
        internal InputService InputService { get; set; }

        /// <summary>Set by MainWindow to trigger settings save on shortcut changes.</summary>
        internal Action OnShortcutsChanged { get; set; }

        private void AddShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm) return;

            var data = new GlobalMacroData { SwitchMode = SwitchProfileMode.Next };
            var shortcut = new ProfileShortcutViewModel(data, OnDeleteShortcut, OnShortcutChanged);
            vm.ProfileShortcuts.Add(shortcut);
            SaveShortcutsToSettings(vm);
        }

        private void OnDeleteShortcut(ProfileShortcutViewModel shortcut)
        {
            if (DataContext is not SettingsViewModel vm) return;
            vm.ProfileShortcuts.Remove(shortcut);
            SaveShortcutsToSettings(vm);
        }

        private void OnShortcutChanged(ProfileShortcutViewModel _)
        {
            if (DataContext is SettingsViewModel vm)
                SaveShortcutsToSettings(vm);
        }

        private void SaveShortcutsToSettings(SettingsViewModel vm)
        {
            SettingsManager.GlobalMacros = vm.ProfileShortcuts
                .Select(s => s.Data)
                .ToArray();
            OnShortcutsChanged?.Invoke();
        }

        // Persistent ProfileChoices / DeviceChoices stay alive across the
        // shortcut row's lifetime; rebuild on DropDownOpened so newly-saved
        // profiles / newly-connected devices surface without needing a
        // shortcut row teardown. Mirrors the pattern used elsewhere (e.g.
        // MicLedDevicePicker_DropDownOpened in PadPage.xaml.cs).
        private void ProfileChoices_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ProfileShortcutViewModel shortcut)
                shortcut.RebuildProfileChoices();
        }

        private void DeviceChoices_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ProfileShortcutViewModel shortcut)
                shortcut.RebuildDeviceChoices();
        }

        // ─────────────────────────────────────────────
        //  Shortcut button recording
        // ─────────────────────────────────────────────

        private ProfileShortcutViewModel _recordingShortcut;
        private DispatcherTimer _recordTimer;
        private TriggerButtonEntry[] _lastRecordedEntries;
        /// <summary>The combo that was on the shortcut before recording began.
        /// Starting a Learn clears TriggerEntries so the capture starts from
        /// empty, and without this the existing combo was simply destroyed:
        /// canceling or timing out left the shortcut with no trigger at all.</summary>
        private TriggerButtonEntry[] _preRecordEntries;
        private Dictionary<Guid, int[]> _recordAxisBaselines;
        private const float AxisRecordDeltaThreshold = 0.25f;
        private DateTime _recordStartTime;
        private const double RecordTimeoutSeconds = 5;

        private void ShortcutLearn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ProfileShortcutViewModel shortcut)
                return;

            if (shortcut.IsRecording)
            {
                // Stop control (#175 item 8): a click during recording ends
                // the capture through the same path the timeout takes, so it
                // keeps the last-held combo (or cancels when nothing was
                // pressed yet) instead of dead-returning.
                if (_recordingShortcut == shortcut)
                    StopRecording();
                else
                    shortcut.CancelRecording(); // Stale flag, no live session.
                return;
            }

            // Cancel any in-progress recording on another shortcut.
            if (_recordingShortcut != null)
                CancelRecording();

            _recordingShortcut = shortcut;
            _lastRecordedEntries = null;
            _preRecordEntries = shortcut.Data.TriggerEntries;
            shortcut.IsRecording = true;
            shortcut.Data.TriggerEntries = null;

            // Capture axis baselines for all devices so we detect movement, not resting position.
            _recordAxisBaselines = new Dictionary<Guid, int[]>();
            var devices = SettingsManager.UserDevices?.Items;
            if (devices != null)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in devices)
                    {
                        if (ud.IsOnline && ud.InputState?.Axis != null)
                            _recordAxisBaselines[ud.InstanceGuid] = (int[])ud.InputState.Axis.Clone();
                    }
                }
            }

            _recordStartTime = DateTime.UtcNow;
            InputService?.SetSuppressGlobalMacros(true);

            _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _recordTimer.Tick += RecordTimer_Tick;
            _recordTimer.Start();
        }

        private void StopRecording()
        {
            _recordTimer?.Stop();
            if (_recordingShortcut != null && _lastRecordedEntries != null && _lastRecordedEntries.Length > 0)
            {
                _recordingShortcut.SetLearnedButtons(_lastRecordedEntries);
            }
            else
            {
                // Sibling of the restore in CancelRecording, and the one that
                // actually fires most often: this is the TIMEOUT path, where
                // the user pressed nothing. Learn cleared TriggerEntries at
                // start, so without putting the backup back the shortcut is
                // left with no trigger at all.
                if (_recordingShortcut != null
                    && (_recordingShortcut.Data.TriggerEntries == null
                        || _recordingShortcut.Data.TriggerEntries.Length == 0))
                {
                    _recordingShortcut.Data.TriggerEntries = _preRecordEntries;
                }
                _recordingShortcut?.CancelRecording();
            }
            _preRecordEntries = null;
            _recordingShortcut = null;
            _lastRecordedEntries = null;
            _recordAxisBaselines = null;
            InputService?.SetSuppressGlobalMacros(false);
        }

        private void RecordTimer_Tick(object sender, EventArgs e)
        {
            if (_recordingShortcut == null)
            {
                _recordTimer?.Stop();
                return;
            }

            // Auto-stop after timeout — saves last-held combo.
            double elapsed = (DateTime.UtcNow - _recordStartTime).TotalSeconds;
            if (elapsed >= RecordTimeoutSeconds)
            {
                StopRecording();
                return;
            }

            // Show countdown in the combo display.
            int remaining = (int)Math.Ceiling(RecordTimeoutSeconds - elapsed);
            _recordingShortcut.RecordingCountdown = remaining;

            // Scan devices for pressed buttons and update the live display.
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return;

            var filterGuid = _recordingShortcut.Data.TriggerDeviceGuid;
            var entries = new System.Collections.Generic.List<TriggerButtonEntry>();

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var ud in devices)
                {
                    if (!ud.IsOnline || ud.InputState == null) continue;
                    if (filterGuid != Guid.Empty && ud.InstanceGuid != filterGuid) continue;
                    // Skip merged/aggregate devices when scanning "Any Device" — they
                    // duplicate child device buttons. But allow if explicitly selected.
                    if (filterGuid == Guid.Empty && ud.DevicePath != null && ud.DevicePath.StartsWith("aggregate://")) continue;

                    // Detect pressed buttons.
                    var buttons = ud.InputState.Buttons;
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        if (buttons[i])
                        {
                            entries.Add(new TriggerButtonEntry
                            {
                                ButtonIndex = i,
                                DeviceInstanceGuid = ud.InstanceGuid,
                                DeviceProductGuid = ud.ProductGuid
                            });
                        }
                    }

                    // Detect axes that moved significantly from their baseline.
                    var axes = ud.InputState.Axis;
                    if (axes != null && _recordAxisBaselines != null
                        && _recordAxisBaselines.TryGetValue(ud.InstanceGuid, out var baseline))
                    {
                        // Scan the slots the pad actually populates. The count
                        // alone stopped being a valid upper bound once the axis
                        // list went sparse: a Move Navigation reports ten axes
                        // whose highest slot is 15, so a dense 0..count-1 scan
                        // would miss its L1 and every d-pad pressure axis.
                        var axisSlots = ud.CapAxisIndices;
                        int axisCount = axisSlots != null && axisSlots.Length > 0
                            ? axisSlots.Length
                            : (ud.CapAxeCount > 0 ? Math.Min(ud.CapAxeCount, axes.Length) : axes.Length);
                        for (int n = 0; n < axisCount; n++)
                        {
                            int i = axisSlots != null && axisSlots.Length > 0 ? axisSlots[n] : n;
                            if (i >= axes.Length || i >= baseline.Length) continue;
                            float rawDelta = (axes[i] - baseline[i]) / 65535f;
                            float absDelta = Math.Abs(rawDelta);
                            if (absDelta >= AxisRecordDeltaThreshold)
                            {
                                // Determine direction from baseline movement.
                                var direction = rawDelta > 0
                                    ? AxisTriggerDirection.Positive
                                    : AxisTriggerDirection.Negative;

                                float currentNormalized = axes[i] / 65535f;
                                // For positive: threshold is slightly below recorded position.
                                // For negative: threshold is slightly above recorded position.
                                // This ensures the center rest position (~0.5) doesn't trigger.
                                float triggerThreshold = direction == AxisTriggerDirection.Positive
                                    ? Math.Max(0.6f, currentNormalized - 0.05f)   // Must be well above center
                                    : Math.Min(0.4f, currentNormalized + 0.05f);  // Must be well below center

                                entries.Add(new TriggerButtonEntry
                                {
                                    IsAxis = true,
                                    AxisIndex = i,
                                    AxisThreshold = triggerThreshold,
                                    AxisDirection = direction,
                                    DeviceInstanceGuid = ud.InstanceGuid,
                                    DeviceProductGuid = ud.ProductGuid
                                });
                            }
                        }
                    }
                }
            }

            // Update live display if buttons are pressed.
            if (entries.Count > 0)
            {
                _lastRecordedEntries = entries.ToArray();
                // Temporarily set entries for display, but don't save yet.
                _recordingShortcut.Data.TriggerEntries = _lastRecordedEntries;
                _recordingShortcut.NotifyComboChanged();
            }
        }

        private void CancelRecording()
        {
            _recordTimer?.Stop();
            // Put back what Learn cleared. Both exits from a recording that
            // captured nothing, the explicit cancel and the timeout, land
            // here, and neither restored the combo the user already had.
            if (_recordingShortcut != null
                && (_recordingShortcut.Data.TriggerEntries == null
                    || _recordingShortcut.Data.TriggerEntries.Length == 0))
            {
                _recordingShortcut.Data.TriggerEntries = _preRecordEntries;
            }
            _preRecordEntries = null;
            _recordingShortcut?.CancelRecording();
            _recordingShortcut = null;
            _recordAxisBaselines = null;
            InputService?.SetSuppressGlobalMacros(false);
        }
    }
}
