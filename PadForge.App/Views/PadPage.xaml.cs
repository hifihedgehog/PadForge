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
            BindModelView();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_currentPadVm != null)
                _currentPadVm.PropertyChanged -= OnPadVmPropertyChanged;

            _currentPadVm = DataContext as PadViewModel;
            if (_currentPadVm != null)
                _currentPadVm.PropertyChanged += OnPadVmPropertyChanged;

            BindModelView();
        }

        // ─────────────────────────────────────────────
        //  3D Model View binding
        // ─────────────────────────────────────────────

        private void BindModelView()
        {
            if (ControllerModel3D == null) return;

            // Wire click-to-record from 3D view
            ControllerModel3D.ControllerElementRecordRequested -= OnModel3DRecordRequested;
            ControllerModel3D.ControllerElementRecordRequested += OnModel3DRecordRequested;

            if (DataContext is PadViewModel vm)
                ControllerModel3D.Bind(vm);
        }

        private void OnModel3DRecordRequested(object sender, string targetName)
        {
            ControllerElementRecordRequested?.Invoke(this, targetName);
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
        //  Map All flash (delegated to 3D view)
        // ─────────────────────────────────────────────

        private void OnPadVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // The 3D view handles CurrentRecordingTarget flash internally
            // via its own PropertyChanged subscription in Bind().
            // No additional handling needed here.
        }
    }
}
