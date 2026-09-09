using System.Windows;

namespace PadForge.Views
{
    public partial class ShiftActivatorDialog
    {
        private void ResetSetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string field }) return;
            switch (field)
            {
                case "Kind": KindCombo.SelectedValue = "Button"; break;
                case "Mode": ModeCombo.SelectedValue = "Hold"; break;
                case "AxisThreshold": AxisThresholdSlider.Value = 0.5; break;
                case "HostLayer": HostLayerCombo.SelectedValue = ""; break;
                case "InheritUnmapped": InheritUnmappedBox.IsChecked = false; break;
                case "FireOnRelease": FireOnReleaseBox.IsChecked = false; break;
                case "PostponeMapping": PostponeMappingBox.IsChecked = false; break;
                case "CycleWrap": CycleWrapBox.IsChecked = true; break;
                case "CycleIncludeBase": CycleIncludeBaseBox.IsChecked = false; break;
                case "CycleLayers": CycleLayersList.SelectedItems.Clear(); break;
            }
        }
    }

    public partial class RegisterVoicePhraseDialog
    {
        private void ResetSetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string field }) return;
            switch (field)
            {
                case "Enabled": EnabledBox.IsChecked = false; break;
                case "Mode": ModeBox.SelectedIndex = 0; break;
                case "Confidence": ConfidenceSlider.Value = 0.80; break;
            }
        }
    }

    public partial class ProfileDialog
    {
        private void ResetPollingRate_Click(object sender, RoutedEventArgs e) => PollingRateBox.SelectedIndex = 0;
    }

    public partial class PairDeviceDialog
    {
        private void ResetSetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string field }) return;
            if (field == "Family" && FamilyCombo.IsEnabled) FamilyCombo.SelectedIndex = 0;
            if (field == "Temporary" && TemporaryCheck.IsEnabled) TemporaryCheck.IsChecked = false;
        }
    }

    public partial class RemoteLinkPairDialog
    {
        private void ResetSetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string field }) return;
            if (field == "GamepadOnly") GamepadOnlyCheck.IsChecked = false;
            if (field == "AllowAssignments") AssignmentsCheck.IsChecked = false;
        }
    }

    public partial class TouchpadGestureRecorderDialog
    {
        private void ResetSampleCount_Click(object sender, RoutedEventArgs e)
        {
            if (SampleCountBox.IsEnabled) SampleCountBox.SelectedIndex = 1;
        }
    }

    public partial class ControllerModelView
    {
        private void ResetAppearance_Click(object sender, RoutedEventArgs e) => AppearancePicker.SelectedIndex = 0;
    }

    public partial class ControllerModel2DView
    {
        private void ResetAppearance_Click(object sender, RoutedEventArgs e) => AppearancePicker.SelectedIndex = 0;
    }
}
