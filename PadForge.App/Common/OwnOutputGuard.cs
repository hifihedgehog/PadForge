using System.Windows;
using System.Windows.Input;
using PadForge.Common.Input;

namespace PadForge.Common
{
    /// <summary>
    /// The second wall behind ProgramInputGate (#600): a button that answers
    /// a program's question ignores a click or a key that PadForge itself
    /// sent while a program drives PadForge. PadForge stamps its own output
    /// (InputHookManager.OutputTag), and Windows hands a message's stamp to
    /// the thread that handles it (GetMessageExtraInfo). WPF raises a mouse
    /// or key event while it handles that message, so the stamp read in the
    /// event is the event's own.
    ///
    /// <para>Set on the Allow and Don't Allow buttons of the request card and
    /// of the Automation page. A press and a release are each checked, so
    /// neither half of a click can come from PadForge.</para>
    /// </summary>
    public static class OwnOutputGuard
    {
        public static readonly DependencyProperty RefusesProperty = DependencyProperty.RegisterAttached(
            "Refuses", typeof(bool), typeof(OwnOutputGuard), new PropertyMetadata(false, OnRefusesChanged));

        public static bool GetRefuses(DependencyObject element) => (bool)element.GetValue(RefusesProperty);

        public static void SetRefuses(DependencyObject element, bool value) => element.SetValue(RefusesProperty, value);

        private static void OnRefusesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement element) return;
            element.PreviewMouseDown -= OnMouse;
            element.PreviewMouseUp -= OnMouse;
            element.PreviewKeyDown -= OnKey;
            element.PreviewKeyUp -= OnKey;
            if (e.NewValue is not true) return;
            element.PreviewMouseDown += OnMouse;
            element.PreviewMouseUp += OnMouse;
            element.PreviewKeyDown += OnKey;
            element.PreviewKeyUp += OnKey;
        }

        private static void OnMouse(object sender, MouseButtonEventArgs e)
        {
            if (ProgramInputGate.RefusesOwnOutputNow()) e.Handled = true;
        }

        private static void OnKey(object sender, KeyEventArgs e)
        {
            if (ProgramInputGate.RefusesOwnOutputNow()) e.Handled = true;
        }
    }
}
