using System.Windows;

namespace PadForge.Controls
{
    /// <summary>
    /// How a check box drawn as a switch looks (#574). The switch is the
    /// SwitchCheckBox style: the control stays a CheckBox, with its binding,
    /// its name and its place in the tab order, and only its drawing
    /// changes.
    /// </summary>
    public static class SwitchLook
    {
        /// <summary>Whether the switch shows the word On or Off before its
        /// track. A page's own switch does. A catalog tile's does not,
        /// because the tile writes the word in its status line.</summary>
        public static readonly DependencyProperty ShowStateProperty = DependencyProperty.RegisterAttached(
            "ShowState", typeof(bool), typeof(SwitchLook), new FrameworkPropertyMetadata(false));

        public static bool GetShowState(DependencyObject element) => (bool)element.GetValue(ShowStateProperty);

        public static void SetShowState(DependencyObject element, bool value) => element.SetValue(ShowStateProperty, value);
    }
}
