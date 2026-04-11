using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class ProfilesPage : UserControl
    {
        public ProfilesPage()
        {
            InitializeComponent();
        }

        private void ProfileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is SettingsViewModel vm &&
                vm.LoadProfileCommand.CanExecute(null))
            {
                vm.LoadProfileCommand.Execute(null);
            }
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

        // ─────────────────────────────────────────────
        //  Shortcut button recording
        // ─────────────────────────────────────────────

        private ProfileShortcutViewModel _recordingShortcut;
        private DispatcherTimer _recordTimer;
        private TriggerButtonEntry[] _lastRecordedEntries;

        private void ShortcutLearn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ProfileShortcutViewModel shortcut)
                return;

            if (shortcut.IsRecording)
            {
                // Stop recording — commit whatever was last captured.
                StopRecording();
                return;
            }

            // Cancel any in-progress recording on another shortcut.
            if (_recordingShortcut != null)
                CancelRecording();

            _recordingShortcut = shortcut;
            _lastRecordedEntries = null;
            shortcut.IsRecording = true;
            shortcut.Data.TriggerEntries = null;

            InputService?.SetSuppressGlobalMacros(true);

            _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _recordTimer.Tick += RecordTimer_Tick;
            _recordTimer.Start();
        }

        private void StopRecording()
        {
            _recordTimer?.Stop();
            if (_recordingShortcut != null && _lastRecordedEntries != null && _lastRecordedEntries.Length > 0)
                _recordingShortcut.SetLearnedButtons(_lastRecordedEntries);
            else
                _recordingShortcut?.CancelRecording();
            _recordingShortcut = null;
            _lastRecordedEntries = null;
            InputService?.SetSuppressGlobalMacros(false);
        }

        private void RecordTimer_Tick(object sender, EventArgs e)
        {
            if (_recordingShortcut == null)
            {
                _recordTimer?.Stop();
                return;
            }

            // Scan devices for pressed buttons and update the live display.
            // Nothing is committed until the user clicks stop.
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
            _recordingShortcut?.CancelRecording();
            _recordingShortcut = null;
            InputService?.SetSuppressGlobalMacros(false);
        }
    }
}
