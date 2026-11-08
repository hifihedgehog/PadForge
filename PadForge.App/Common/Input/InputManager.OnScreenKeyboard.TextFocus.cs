using System.Threading;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Open When a Text Box Is Selected (#566). The watcher reports on UI
    /// Automation's thread, and the tick reads the report. The watcher
    /// reports each focus change once (focus that stays in one element is not
    /// reported again), so every report of a text box is focus moving into
    /// one: it opens the keyboard for the first enabled keyboard menu that
    /// asks for it, in slot order and each slot's list order, whose layer is
    /// open and whose slot no restricted Remote Link peer feeds, also after
    /// the keyboard was closed in another text box. Focus leaving text boxes
    /// closes a keyboard text focus opened, and leaves one opened another way
    /// alone.
    /// </summary>
    public partial class InputManager
    {
        /// <summary>True while an enabled keyboard menu asks for text focus,
        /// read by the watcher's thread.</summary>
        private volatile bool _oskWantsTextFocus;
        internal bool OnScreenKeyboardWantsTextFocus => _oskWantsTextFocus;

        private volatile bool _oskFocusEditable;
        private int _oskFocusSeq;
        private int _oskFocusSeqSeen;
        private OskTextFocusWatcher _oskFocusWatcher;

        /// <summary>The watcher's report, from any thread.</summary>
        internal void NoteTextFocus(bool editable)
        {
            _oskFocusEditable = editable;
            Interlocked.Increment(ref _oskFocusSeq);
        }

        private bool TextFocusChanged => Volatile.Read(ref _oskFocusSeq) != _oskFocusSeqSeen;

        private void ReadTextFocus(long now)
        {
            int seq = Volatile.Read(ref _oskFocusSeq);
            if (seq == _oskFocusSeqSeen) return;
            _oskFocusSeqSeen = seq;
            // A new report is a focus change. Each one into a text box opens,
            // a move from one text box to another included.
            bool editable = _oskFocusEditable && _oskWantsTextFocus;
            if (editable)
            {
                if (_osk?.IsOpen == true) return;
                var sets = SettingsManager.SlotMappingSets;
                foreach (var m in _oskMenuScan)
                {
                    if (!m.Def.OpenOnTextFocus || !m.Def.Enabled) continue;
                    MappingSet set = sets != null && m.Slot < sets.Length ? sets[m.Slot] : null;
                    if (!IsMenuLayerOpen(m.Slot, set, m.Def) || IsSlotRestricted(m.Slot)) continue;
                    _oskRequests.Add(new OskRequest(m.Slot, m.Def.MenuId, OskRequestKind.Open, OskOpenReason.TextFocus));
                    return;
                }
            }
            else if (_osk?.IsOpen == true && _oskReason == OskOpenReason.TextFocus)
            {
                _oskRequests.Add(new OskRequest(_oskSlot, _oskMenuId, OskRequestKind.Close, OskOpenReason.TextFocus));
            }
        }

        private void ResetTextFocus()
        {
            _oskFocusSeqSeen = Volatile.Read(ref _oskFocusSeq);
        }

        /// <summary>Engine start: the watcher runs for the engine's life and
        /// listens only while a menu asks.</summary>
        private void StartTextFocusWatcher()
        {
            if (_oskFocusWatcher != null || PadForge.Engine.Common.InputHookManager.DesktopInputOffLimits) return;
            _oskFocusWatcher = new OskTextFocusWatcher(() => _oskWantsTextFocus, NoteTextFocus);
        }

        private void StopTextFocusWatcher()
        {
            var w = _oskFocusWatcher;
            _oskFocusWatcher = null;
            w?.Dispose();
            _oskWantsTextFocus = false;
        }
    }
}
