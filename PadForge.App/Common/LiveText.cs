using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace PadForge.Common
{
    /// <summary>
    /// Makes a status line a polite live region: a screen reader hears its new
    /// text each time it changes while the line is on screen and PadForge is
    /// the active window. WPF passes AutomationProperties.LiveSetting to UI
    /// Automation but raises no LiveRegionChanged of its own, so the event is
    /// raised here once the new text is on screen. NVDA speaks the region's
    /// text (NVDAObject.event_liveRegionChange) and drops a repeat within half
    /// a second. Nothing is raised while another window is active, so a
    /// status change never interrupts a game PadForge sits behind.
    ///
    /// <para>For lines that show a state (a server listening, an engine
    /// idle), a repeat of the same text is no change and is not spoken. The
    /// message line shows messages, which can repeat, so MainWindow calls
    /// Announce for each message.</para>
    /// </summary>
    public static class LiveText
    {
        public static readonly DependencyProperty PoliteProperty = DependencyProperty.RegisterAttached(
            "Polite", typeof(bool), typeof(LiveText), new PropertyMetadata(false, OnPoliteChanged));

        public static bool GetPolite(DependencyObject d) => (bool)d.GetValue(PoliteProperty);

        public static void SetPolite(DependencyObject d, bool value) => d.SetValue(PoliteProperty, value);

        // Marks the mirror before its binding gives it the line's first text.
        private static readonly object Unbound = new();

        /// <summary>The line's text, mirrored so that each change reaches a
        /// callback.</summary>
        private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
            "Shown", typeof(object), typeof(LiveText), new PropertyMetadata(Unbound, OnShownChanged));

        private static void OnPoliteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock line)
                return;
            if ((bool)e.NewValue)
            {
                AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
                BindingOperations.SetBinding(line, ShownProperty,
                    new Binding(nameof(TextBlock.Text)) { Source = line, Mode = BindingMode.OneWay });
            }
            else
            {
                BindingOperations.ClearBinding(line, ShownProperty);
                line.ClearValue(AutomationProperties.LiveSettingProperty);
            }
        }

        private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // The first text is what the line shows when it is bound, not a
            // change.
            if (ReferenceEquals(e.OldValue, Unbound) || ReferenceEquals(e.NewValue, Unbound) || d is not TextBlock line)
                return;
            line.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Announce(line)));
        }

        /// <summary>Raises LiveRegionChanged on a line that shows text, is
        /// on screen and sits in the active window, when a screen reader
        /// listens.</summary>
        internal static void Announce(TextBlock line)
        {
            if (string.IsNullOrEmpty(line.Text) || !line.IsVisible
                || Window.GetWindow(line) is not { IsActive: true }
                || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                return;
            UIElementAutomationPeer.CreatePeerForElement(line)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
