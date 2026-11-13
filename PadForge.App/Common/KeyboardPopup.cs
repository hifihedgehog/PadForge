using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PadForge.Common
{
    /// <summary>
    /// The keyboard path of a popup built in code (the type bar, the profile
    /// switcher). Opened by a key press, it takes the keyboard focus on its
    /// first control, Tab and the arrows cycle inside it, Escape closes it,
    /// and the focus returns to what opened it. Opened by the mouse, nothing
    /// changes: the focus stays where it was, as before.
    ///
    /// <para>The focus can sit in a popup without the main window losing
    /// activation: WPF creates a popup's window with WS_EX_NOACTIVATE and
    /// routes key presses to the element with WPF keyboard focus whichever
    /// window holds Win32 focus (HwndKeyboardInputProvider.AcquireFocus), as
    /// menus and combo boxes do. So the Deactivated handler that closes these
    /// popups never fires for it.</para>
    /// </summary>
    internal static class KeyboardPopup
    {
        /// <summary>Whether the input that is being handled came from the
        /// keyboard.</summary>
        internal static bool FromKeyboard => InputManager.Current.MostRecentInputDevice is KeyboardDevice;

        internal static void Wire(Popup popup, UIElement opener, FrameworkElement content)
        {
            bool fromKeyboard = FromKeyboard;
            KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(content, KeyboardNavigationMode.Cycle);
            content.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    popup.IsOpen = false;
                    e.Handled = true;
                }
            };
            if (!fromKeyboard)
                return;
            popup.Opened += (s, e) => content.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
            popup.Closed += (s, e) =>
            {
                // The focus was in the popup: hand it back to the opener, if
                // a choice did not move the page away from it. An opener that
                // takes no focus itself (the profile pill's UserControl)
                // hands it to the first control inside it.
                if (opener.IsVisible && (Keyboard.FocusedElement is not DependencyObject focused || TreeWalk.IsWithin(content, focused))
                    && !opener.Focus())
                    opener.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            };
        }
    }
}
