using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class PadPage : UserControl
    {
        /// <summary>
        /// Raised when the user clicks a controller element to start recording.
        /// The string argument is the TargetSettingName (e.g., "ButtonA", "LeftTrigger").
        /// </summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _currentPadVm;

        public PadPage()
        {
            InitializeComponent();
            Loaded += PadPage_Loaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void PadPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyViewMode();
            SyncTabStripSelection();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_currentPadVm != null)
                _currentPadVm.PropertyChanged -= OnPadVmPropertyChanged;

            _currentPadVm = DataContext as PadViewModel;
            if (_currentPadVm != null)
                _currentPadVm.PropertyChanged += OnPadVmPropertyChanged;

            ApplyViewMode();
            SyncTabStripSelection();
            SyncVJoyConfigBar();
        }

        // ─────────────────────────────────────────────
        //  2D / 3D Model View
        // ─────────────────────────────────────────────

        private SettingsViewModel GetSettingsVm()
        {
            if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
                return mainVm.Settings;
            return null;
        }

        private void ViewModeToggle_Click(object sender, RoutedEventArgs e)
        {
            var settingsVm = GetSettingsVm();
            if (settingsVm != null)
                settingsVm.Use2DControllerView = !settingsVm.Use2DControllerView;
            ApplyViewMode();
        }

        private bool IsCustomVJoy()
        {
            if (DataContext is PadViewModel vm &&
                vm.OutputType == Engine.VirtualControllerType.VJoy &&
                !vm.VJoyConfig.IsGamepadPreset)
                return true;
            return false;
        }

        private void ApplyViewMode()
        {
            if (ControllerModel3D == null || ControllerModel2D == null || ControllerSchematic == null) return;

            bool isSchematic = IsCustomVJoy();
            bool is2D = GetSettingsVm()?.Use2DControllerView ?? false;

            if (isSchematic)
            {
                // Custom vJoy: always show schematic view, hide 2D/3D toggle
                ControllerModel3D.Visibility = Visibility.Collapsed;
                ControllerModel2D.Visibility = Visibility.Collapsed;
                ControllerSchematic.Visibility = Visibility.Visible;
                ViewModeToggle.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Gamepad preset: standard 2D/3D toggle
                ControllerSchematic.Visibility = Visibility.Collapsed;
                ControllerModel3D.Visibility = is2D ? Visibility.Collapsed : Visibility.Visible;
                ControllerModel2D.Visibility = is2D ? Visibility.Visible : Visibility.Collapsed;
                ViewModeToggle.Visibility = Visibility.Visible;

                // E8B9 = Photo/flat icon (shown in 3D mode, click to switch TO 2D)
                // F158 = 3D/cube icon (shown in 2D mode, click to switch TO 3D)
                ViewModeIcon.Text = is2D ? "\uF158" : "\uE8B9";
                ViewModeToggle.ToolTip = is2D ? "Switch to 3D view" : "Switch to 2D view";
            }

            BindActiveModelView();
        }

        private void BindActiveModelView()
        {
            bool isSchematic = IsCustomVJoy();
            bool is2D = GetSettingsVm()?.Use2DControllerView ?? false;

            // Unbind all first
            ControllerModel3D.Unbind();
            ControllerModel2D.Unbind();
            ControllerSchematic.Unbind();

            if (DataContext is not PadViewModel vm) return;

            if (isSchematic)
            {
                ControllerSchematic.ControllerElementRecordRequested -= OnModelRecordRequested;
                ControllerSchematic.ControllerElementRecordRequested += OnModelRecordRequested;
                ControllerSchematic.Bind(vm);
            }
            else if (is2D)
            {
                ControllerModel2D.ControllerElementRecordRequested -= OnModelRecordRequested;
                ControllerModel2D.ControllerElementRecordRequested += OnModelRecordRequested;
                ControllerModel2D.Bind(vm);
            }
            else
            {
                ControllerModel3D.ControllerElementRecordRequested -= OnModelRecordRequested;
                ControllerModel3D.ControllerElementRecordRequested += OnModelRecordRequested;
                ControllerModel3D.Bind(vm);
            }
        }

        private void OnModelRecordRequested(object sender, string targetName)
        {
            ControllerElementRecordRequested?.Invoke(this, targetName);
        }

        // ─────────────────────────────────────────────
        //  Custom tab strip
        // ─────────────────────────────────────────────

        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && TryGetTagIndex(rb, out int idx) && DataContext is PadViewModel vm)
                vm.SelectedConfigTab = idx;
        }

        private void SyncTabStripSelection()
        {
            if (DataContext is not PadViewModel vm) return;
            int selected = vm.SelectedConfigTab;

            foreach (var rb in FindVisualChildren<RadioButton>(this))
            {
                if (rb.GroupName == "PadTab" && TryGetTagIndex(rb, out int idx))
                    rb.IsChecked = idx == selected;
            }
        }

        private static bool TryGetTagIndex(FrameworkElement el, out int index)
        {
            if (el.Tag is int i) { index = i; return true; }
            if (el.Tag is string s && int.TryParse(s, out i)) { index = i; return true; }
            index = -1;
            return false;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var desc in FindVisualChildren<T>(child))
                    yield return desc;
            }
        }

        // ─────────────────────────────────────────────
        //  Motor test (click) + hover highlight
        // ─────────────────────────────────────────────

        private void Motor_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement el)
                el.Opacity = 0.7;
        }

        private void Motor_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement el)
                el.Opacity = 1.0;
        }

        private void LeftMotor_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is PadViewModel padVm)
                padVm.FireTestLeftMotor();
        }

        private void RightMotor_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is PadViewModel padVm)
                padVm.FireTestRightMotor();
        }

        // ─────────────────────────────────────────────
        //  Map All stop button
        // ─────────────────────────────────────────────

        private void MapAllStop_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PadViewModel padVm)
                padVm.StopMapAll();
        }

        // ─────────────────────────────────────────────
        //  ViewModel property changed
        // ─────────────────────────────────────────────

        private void OnPadVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.SelectedConfigTab))
                SyncTabStripSelection();
            else if (e.PropertyName == nameof(PadViewModel.OutputType))
            {
                SyncVJoyConfigBar();
                ApplyViewMode();
            }
        }

        // ─────────────────────────────────────────────
        //  vJoy configuration bar
        // ─────────────────────────────────────────────

        private bool _syncingVJoyConfig;

        private void SyncVJoyConfigBar()
        {
            if (DataContext is not PadViewModel vm) return;

            bool isVJoy = vm.OutputType == Engine.VirtualControllerType.VJoy;
            VJoyConfigBar.Visibility = isVJoy ? Visibility.Visible : Visibility.Collapsed;

            if (isVJoy)
            {
                _syncingVJoyConfig = true;
                VJoyPresetCombo.SelectedIndex = (int)vm.VJoyConfig.Preset;
                SyncVJoyCustomFields(vm);
                _syncingVJoyConfig = false;
            }
        }

        private void SyncVJoyCustomFields(PadViewModel vm)
        {
            bool isCustom = vm.VJoyConfig.Preset == VJoyPreset.Custom;
            VJoyCustomPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

            if (isCustom)
            {
                VJoyStickCountBox.Text = vm.VJoyConfig.ThumbstickCount.ToString();
                VJoyTriggerCountBox.Text = vm.VJoyConfig.TriggerCount.ToString();
                VJoyPovCountBox.Text = vm.VJoyConfig.PovCount.ToString();
                VJoyButtonCountBox.Text = vm.VJoyConfig.ButtonCount.ToString();
            }
        }

        private void VJoyPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingVJoyConfig) return;
            if (DataContext is not PadViewModel vm) return;
            if (VJoyPresetCombo.SelectedIndex < 0) return;

            vm.VJoyConfig.Preset = (VJoyPreset)VJoyPresetCombo.SelectedIndex;
            SyncVJoyCustomFields(vm);
            ApplyViewMode();
        }

        private void VJoyCustomValue_Changed(object sender, RoutedEventArgs e)
        {
            ApplyVJoyCustomValues();
        }

        private void VJoyCustomValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyVJoyCustomValues();
        }

        private void ApplyVJoyCustomValues()
        {
            if (DataContext is not PadViewModel vm) return;
            if (vm.VJoyConfig.Preset != VJoyPreset.Custom) return;

            if (int.TryParse(VJoyStickCountBox.Text, out int sticks))
                vm.VJoyConfig.ThumbstickCount = sticks;
            if (int.TryParse(VJoyTriggerCountBox.Text, out int triggers))
                vm.VJoyConfig.TriggerCount = triggers;
            if (int.TryParse(VJoyPovCountBox.Text, out int povs))
                vm.VJoyConfig.PovCount = povs;
            if (int.TryParse(VJoyButtonCountBox.Text, out int buttons))
                vm.VJoyConfig.ButtonCount = buttons;

            // Reflect clamped values back into text boxes
            VJoyStickCountBox.Text = vm.VJoyConfig.ThumbstickCount.ToString();
            VJoyTriggerCountBox.Text = vm.VJoyConfig.TriggerCount.ToString();
            VJoyPovCountBox.Text = vm.VJoyConfig.PovCount.ToString();
            VJoyButtonCountBox.Text = vm.VJoyConfig.ButtonCount.ToString();
        }
    }
}
