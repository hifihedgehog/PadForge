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
        //  Shortcut button learning
        // ─────────────────────────────────────────────

        private ProfileShortcutViewModel _learningShortcut;
        private DispatcherTimer _learnTimer;
        private DateTime _learnStartTime;
        private const double LearnTimeoutSeconds = 5;
        private string _learnCandidateSignature;
        private int _learnHoldFrames;
        private const int LearnHoldRequired = 3; // ~100ms at 30Hz

        private void ShortcutLearn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ProfileShortcutViewModel shortcut)
                return;

            if (shortcut.IsLearning)
            {
                CancelLearn();
                return;
            }

            // Cancel any in-progress learning on another shortcut.
            if (_learningShortcut != null)
                CancelLearn();

            _learningShortcut = shortcut;
            shortcut.IsLearning = true;
            shortcut.Data.TriggerEntries = null; // Clear previous.
            _learnStartTime = DateTime.UtcNow;
            _learnCandidateSignature = null;
            _learnHoldFrames = 0;

            _learnTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _learnTimer.Tick += LearnTimer_Tick;
            _learnTimer.Start();
        }

        private void LearnTimer_Tick(object sender, EventArgs e)
        {
            if (_learningShortcut == null)
            {
                _learnTimer?.Stop();
                return;
            }

            // Timeout.
            if ((DateTime.UtcNow - _learnStartTime).TotalSeconds > LearnTimeoutSeconds)
            {
                CancelLearn();
                return;
            }

            // Scan ALL devices for pressed buttons simultaneously.
            // Supports cross-device combos (e.g., Shift on keyboard + Start on gamepad).
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return;

            var candidateEntries = new System.Collections.Generic.List<TriggerButtonEntry>();

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var ud in devices)
                {
                    if (!ud.IsOnline || ud.InputState == null) continue;

                    var buttons = ud.InputState.Buttons;
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        if (buttons[i])
                        {
                            candidateEntries.Add(new TriggerButtonEntry
                            {
                                ButtonIndex = i,
                                DeviceInstanceGuid = ud.InstanceGuid,
                                DeviceProductGuid = ud.ProductGuid
                            });
                        }
                    }
                }
            }

            if (candidateEntries.Count > 0)
            {
                // Check stability: same set of buttons held for LearnHoldRequired frames.
                string signature = string.Join("|", candidateEntries.Select(
                    e => $"{e.DeviceInstanceGuid}:{e.ButtonIndex}"));

                if (signature == _learnCandidateSignature)
                {
                    _learnHoldFrames++;
                    if (_learnHoldFrames >= LearnHoldRequired)
                    {
                        _learnTimer.Stop();
                        _learningShortcut.SetLearnedButtons(candidateEntries.ToArray());
                        _learningShortcut = null;
                        return;
                    }
                }
                else
                {
                    _learnCandidateSignature = signature;
                    _learnHoldFrames = 1;
                }
            }
            else
            {
                _learnCandidateSignature = null;
                _learnHoldFrames = 0;
            }
        }

        private void CancelLearn()
        {
            _learnTimer?.Stop();
            _learningShortcut?.CancelLearn();
            _learningShortcut = null;
        }
    }
}
