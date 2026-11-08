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
    /// else its text, is read-only. Focus that stays in one element (UI
    /// Automation raises the event again when its window comes back to the
    /// front) is reported once, so a report always means focus moved.
    /// </summary>
    internal sealed class OskTextFocusWatcher : IDisposable
    {
        private readonly Func<bool> _wanted;
        private readonly Action<bool> _report;
        private readonly Func<(int[] Id, bool Editable)> _focusNow;
        private readonly object _lastGate = new();
        /// <summary>The element and answer last reported, under _lastGate:
        /// UI Automation's thread and this one both report.</summary>
        private int[] _lastId;
        private bool _lastEditable, _hasLast;
        private readonly Action<AutomationFocusChangedEventHandler> _add, _remove;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private AutomationFocusChangedEventHandler _handler;
        private bool _disposed;

        /// <param name="wanted">True while a keyboard menu asks for text
        /// focus. Read twice a second.</param>
        /// <param name="report">Called on UI Automation's thread with whether
        /// focus is in a text box.</param>
        public OskTextFocusWatcher(Func<bool> wanted, Action<bool> report)
            : this(wanted, report, FocusedNow,
                Automation.AddAutomationFocusChangedEventHandler, Automation.RemoveAutomationFocusChangedEventHandler)
        {
        }

        /// <summary>Test seam: <paramref name="focusNow"/> answers which
        /// element has focus now (its UI Automation runtime id) and whether
        /// it is a text box, and <paramref name="add"/> and
        /// <paramref name="remove"/> stand in for UI Automation's focus
        /// subscription.</summary>
        internal OskTextFocusWatcher(Func<bool> wanted, Action<bool> report, Func<(int[] Id, bool Editable)> focusNow,
            Action<AutomationFocusChangedEventHandler> add, Action<AutomationFocusChangedEventHandler> remove)
        {
            _wanted = wanted;
            _report = report;
            _focusNow = focusNow;
            _add = add;
            _remove = remove;
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge text focus" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>Each pass is its own try: a failure in one (UI
        /// Automation answering a hung window with UIA_E_TIMEOUT, which .NET
        /// throws as a TimeoutException) is logged and the next pass runs, so
        /// the watcher never stops for the engine's life.</summary>
        private void Run()
        {
            try
            {
                while (!_stop.Wait(500))
                {
                    try
                    {
                        bool want = _wanted();
                        if (want && _handler == null) Subscribe();
                        else if (!want && _handler != null) Unsubscribe();
                    }
                    catch (Exception ex) when (!IsCritical(ex))
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine($"OSK text focus watcher pass failed: {ex.GetType().Name} {ex.Message}");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Dispose gave up on the join and disposed the stop event.
            }
            finally
            {
                if (_handler != null)
                {
                    try { Unsubscribe(); }
                    catch (Exception ex) when (!IsCritical(ex)) { /* the watcher is going away */ }
                }
            }
        }

        /// <summary>An exception no catch here may swallow.</summary>
        private static bool IsCritical(Exception ex)
            => ex is OutOfMemoryException or AccessViolationException or ThreadAbortException;

        private void Subscribe()
        {
            _handler = OnFocusChanged;
            try
            {
                _add(_handler);
            }
            catch (Exception ex)
            {
                _handler = null;
                PadForge.Engine.SdlDiagLog.WriteLine($"OSK text focus subscribe failed: {ex.GetType().Name} {ex.Message}");
                return;
            }
            // Where focus is now, so a text box focused before a menu asked
            // counts from its next change, not never.
            var (id, editable) = _focusNow();
            Report(id, editable);
        }

        /// <summary>The focused element's runtime id and whether it is a text box.</summary>
        private static (int[] Id, bool Editable) FocusedNow()
        {
            AutomationElement now = null;
            try { now = AutomationElement.FocusedElement; } catch { /* nothing focused, or it went away */ }
            return (RuntimeIdOf(now), IsTextBox(now));
        }

        /// <summary>An element's UI Automation runtime id, or null when it
        /// has none to give.</summary>
        private static int[] RuntimeIdOf(AutomationElement element)
        {
            if (element == null) return null;
            try { return element.GetRuntimeId(); }
            catch (Exception ex) when (!IsCritical(ex)) { return null; }
        }

        /// <summary>Reports a focus change, unless focus is still in the
        /// element last reported with the same answer. An element with no
        /// runtime id is reported every time.</summary>
        internal void Report(int[] id, bool editable)
        {
            lock (_lastGate)
            {
                if (_hasLast && id != null && _lastId != null && editable == _lastEditable
                    && id.AsSpan().SequenceEqual(_lastId))
                    return;
                _hasLast = true;
                _lastId = id;
                _lastEditable = editable;
                _report(editable);
            }
        }

        private void Unsubscribe()
        {
            var h = _handler;
            _handler = null;
            try { if (h != null) _remove(h); }
            catch { /* the watcher is going away */ }
            lock (_lastGate)
            {
                _hasLast = false;
                _lastId = null;
                _report(false);
            }
        }

        private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
        {
            if (_handler == null) return;
            var element = sender as AutomationElement;
            Report(RuntimeIdOf(element), IsTextBox(element));
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
            // Any answer UI Automation fails to give is no text box: an
            // element gone (ElementNotAvailableException), a hung window
            // (UIA_E_TIMEOUT, a TimeoutException), a pattern refused (a
            // COMException), and the rest of what UiaCoreApi.CheckError or
            // Marshal.ThrowExceptionForHR can throw.
            catch (Exception ex) when (!IsCritical(ex)) { return false; }
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
