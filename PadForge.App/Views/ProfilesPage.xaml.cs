using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
            // List rebuilds (import, delete, settings reload) discard the
            // container wearing the selection ring; re-attach once the new
            // containers are generated.
            ProfileListBox.ItemContainerGenerator.StatusChanged += ProfileContainers_StatusChanged;
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
        //  Selection pulse ring (user report 2026-07-05)
        // ─────────────────────────────────────────────

        /// <summary>Template-root card Border currently wearing the ring.</summary>
        private Border _selectionGlowCard;

        /// <summary>OS "animate controls" switch (#175 item 98): with motion
        /// off the breathe swaps for a static glow.</summary>
        private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

        private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The new selection's container may not be templated yet (initial
            // binding lands before layout), so resolve after this layout pass.
            Dispatcher.BeginInvoke(new Action(ApplySelectionGlow), DispatcherPriority.Loaded);
        }

        private void ProfileContainers_StatusChanged(object sender, EventArgs e)
        {
            if (ProfileListBox.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                Dispatcher.BeginInvoke(new Action(ApplySelectionGlow), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Moves the cold pulse ring to the selected card. Code-driven on
        /// purpose: animating Effect from style triggers is the startup-crash
        /// canon, so the DropShadowEffect is built and animated here, mirroring
        /// the MainWindow mini-card heat ring. Cold hue keeps the grammar
        /// (cold = selected, ember = active); on the active card the ember rim
        /// stays dominant with this ring breathing around it. Idempotent, since
        /// deferred dispatcher calls and generator churn land here repeatedly.
        /// </summary>
        private void ApplySelectionGlow()
        {
            Border card = null;
            if (ProfileListBox.SelectedItem != null &&
                ProfileListBox.ItemContainerGenerator.ContainerFromItem(ProfileListBox.SelectedItem)
                    is ListBoxItem container)
                card = FindCardBorder(container);

            if (ReferenceEquals(card, _selectionGlowCard)) return;

            // Stop the previous card's clock before detaching so no orphaned
            // animation keeps driving a dead effect.
            if (_selectionGlowCard != null)
            {
                if (_selectionGlowCard.Effect is DropShadowEffect old)
                    old.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                _selectionGlowCard.Effect = null;
                _selectionGlowCard = null;
            }
            if (card == null) return;

            var ring = new DropShadowEffect
            {
                Color = Color.FromRgb(0x58, 0xB6, 0xE4),
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.25,
            };
            card.Effect = ring;
            if (MotionEnabled)
            {
                var breathe = new DoubleAnimation
                {
                    From = 0.25,
                    To = 0.60,
                    Duration = TimeSpan.FromSeconds(1.6),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    // Phase-lock to the sidebar heat rings' 3.2 s clock so
                    // reselection doesn't visibly restart the cycle.
                    BeginTime = TimeSpan.FromMilliseconds(
                        -(DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 3200.0)),
                };
                ring.BeginAnimation(DropShadowEffect.OpacityProperty, breathe);
            }
            else
            {
                // Reduced motion (#175 item 98): static glow at the breathe
                // range's midpoint.
                ring.Opacity = 0.40;
            }
            _selectionGlowCard = card;
        }

        /// <summary>First Border under the retemplated (bare ContentPresenter)
        /// container: the DataTemplate's card root.</summary>
        private static Border FindCardBorder(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Border b) return b;
                if (FindCardBorder(child) is Border deeper) return deeper;
            }
            return null;
        }

        // ─────────────────────────────────────────────
        //  Drop-zone import (#175)
        // ─────────────────────────────────────────────

        /// <summary>Set by MainWindow: consumes a dropped .pfprofile through
        /// the same code path as the Import button.</summary>
        internal Action<string> ImportProfileFile { get; set; }

        /// <summary>True when the drag carries at least one .pfprofile.</summary>
        private static bool IsProfileFileDrag(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            return e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                   files.Any(f => f != null && f.EndsWith(
                       PadForge.Common.ProfileTransfer.FileExtension,
                       StringComparison.OrdinalIgnoreCase));
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
                if (file != null && file.EndsWith(
                        PadForge.Common.ProfileTransfer.FileExtension,
                        StringComparison.OrdinalIgnoreCase))
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
        private Dictionary<Guid, int[]> _recordAxisBaselines;
        private const float AxisRecordDeltaThreshold = 0.25f;
        private DateTime _recordStartTime;
        private const double RecordTimeoutSeconds = 5;

        private void ShortcutLearn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ProfileShortcutViewModel shortcut)
                return;

            if (shortcut.IsRecording)
                return; // Recording in progress — timeout will auto-stop.

            // Cancel any in-progress recording on another shortcut.
            if (_recordingShortcut != null)
                CancelRecording();

            _recordingShortcut = shortcut;
            _lastRecordedEntries = null;
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
                _recordingShortcut.SetLearnedButtons(_lastRecordedEntries);
            else
                _recordingShortcut?.CancelRecording();
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
                        int axisCount = ud.CapAxeCount > 0 ? Math.Min(ud.CapAxeCount, axes.Length) : axes.Length;
                        for (int i = 0; i < axisCount && i < baseline.Length; i++)
                        {
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
            _recordingShortcut?.CancelRecording();
            _recordingShortcut = null;
            _recordAxisBaselines = null;
            InputService?.SetSuppressGlobalMacros(false);
        }
    }
}
