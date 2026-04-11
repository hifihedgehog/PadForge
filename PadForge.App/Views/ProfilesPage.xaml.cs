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

        private static void SaveShortcutsToSettings(SettingsViewModel vm)
        {
            SettingsManager.GlobalMacros = vm.ProfileShortcuts
                .Select(s => s.Data)
                .ToArray();
        }

        // ─────────────────────────────────────────────
        //  Shortcut button learning
        // ─────────────────────────────────────────────

        private ProfileShortcutViewModel _learningShortcut;
        private DispatcherTimer _learnTimer;
        private DateTime _learnStartTime;
        private const double LearnTimeoutSeconds = 5;

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
            _learnStartTime = DateTime.UtcNow;

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

            // Poll all online devices for any pressed buttons.
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var ud in devices)
                {
                    if (!ud.IsOnline || ud.InputState == null) continue;

                    var buttons = ud.InputState.Buttons;
                    var pressed = new System.Collections.Generic.List<int>();
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        if (buttons[i]) pressed.Add(i);
                    }

                    if (pressed.Count > 0)
                    {
                        _learnTimer.Stop();
                        _learningShortcut.SetLearnedButtons(pressed.ToArray(), ud.InstanceGuid);
                        _learningShortcut = null;
                        return;
                    }
                }
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
