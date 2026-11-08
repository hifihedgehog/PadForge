using System;
using System.Threading;
using System.Windows.Automation;

namespace PadForge.Common.Input.OnScreenKeyboard
{
    /// <summary>
    /// Reports whether keyboard focus is in a text box, for a keyboard menu's
    /// Open When a Text Box Is Selected (#566). UI Automation's focus event,
    /// subscribed on its own thread and only while a menu asks for it: a
    /// focus watcher costs every app that answers UI Automation a query per
    /// focus change, and Firefox answers through a slow proxy (Mozilla bug
    /// 1777875), so nothing listens while no menu wants it. Each focus change
    /// asks the focused element its control type, then whether its value, or
    /// else its text, is read-only.
    /// </summary>
    internal sealed class OskTextFocusWatcher : IDisposable
    {
        private readonly Func<bool> _wanted;
        private readonly Action<bool> _report;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private AutomationFocusChangedEventHandler _handler;
        private bool _disposed;

        /// <param name="wanted">True while a keyboard menu asks for text
        /// focus. Read twice a second.</param>
        /// <param name="report">Called on UI Automation's thread with whether
        /// focus is in a text box.</param>
        public OskTextFocusWatcher(Func<bool> wanted, Action<bool> report)
        {
            _wanted = wanted;
            _report = report;
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge text focus" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                while (!_stop.Wait(500))
                {
                    bool want = _wanted();
                    if (want && _handler == null) Subscribe();
                    else if (!want && _handler != null) Unsubscribe();
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"OSK text focus watcher stopped: {ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                if (_handler != null) Unsubscribe();
            }
        }

        private void Subscribe()
        {
            _handler = OnFocusChanged;
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(_handler);
            }
            catch (Exception ex)
            {
                _handler = null;
                PadForge.Engine.SdlDiagLog.WriteLine($"OSK text focus subscribe failed: {ex.GetType().Name} {ex.Message}");
                return;
            }
            // Where focus is now, so a text box focused before a menu asked
            // counts from its next change, not never.
            AutomationElement now = null;
            try { now = AutomationElement.FocusedElement; } catch { /* nothing focused, or it went away */ }
            _report(IsTextBox(now));
        }

        private void Unsubscribe()
        {
            var h = _handler;
            _handler = null;
            try { if (h != null) Automation.RemoveAutomationFocusChangedEventHandler(h); }
            catch { /* the watcher is going away */ }
            _report(false);
        }

        private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
        {
            if (_handler == null) return;
            _report(IsTextBox(sender as AutomationElement));
        }

        /// <summary>An element that takes typed text: an edit control or a
        /// document that is not read-only. UI Automation's control types
        /// require the Text pattern of both and give the Value pattern only to
        /// those that take a string, so the value's read-only flag answers
        /// first and the text's read-only attribute answers for the rest, a
        /// word processor's document among them.</summary>
        internal static bool IsTextBox(AutomationElement element)
        {
            if (element == null) return false;
            try
            {
                var type = element.Current.ControlType;
                bool edit = type == ControlType.Edit;
                if (!edit && type != ControlType.Document) return false;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern value)
                    return !value.Current.IsReadOnly;
                // A mixed or unsupported answer is no text box.
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var t) && t is TextPattern text)
                    return text.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is bool readOnly && !readOnly;
                return edit;
            }
            catch (ElementNotAvailableException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.Runtime.InteropServices.COMException) { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            _thread.Join(2000);
            _stop.Dispose();
        }
    }
}
