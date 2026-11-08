using System;
using PadForge.Common.Input;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine.Menus;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        // ─────────────────────────────────────────────
        //  On-Screen Keyboard (#566)
        // ─────────────────────────────────────────────

        private Views.OnScreenKeyboardWindow _oskWindow;
        private IntPtr _oskLayoutHkl;
        private OskWindowsLayout _oskLayout;
        private long _oskLayoutCheckedMs = long.MinValue / 2;
        private long _oskRepeatCheckedMs = long.MinValue / 2;
        private Common.Input.InputManager _oskPushedTo;
        private (int Slot, int MenuId) _oskSeenOwner = (-1, -1);
        private OskStyle? _oskStyleSeen;
        private bool? _oskFunctionRowSeen;
        /// <summary>Hold-to-move below a whole percent, kept for the next tick.</summary>
        private float _oskMoveCarryX, _oskMoveCarryY;
        private int _oskOwnerSlot = -1;
        private MenuDefinitionEntry _oskOwnerMenu;

        /// <summary>UI-timer leg of the On-Screen Keyboard, the menu overlay's
        /// shape: hands Windows' repeat timing and the layout of the window
        /// being typed into to the poll thread, saves to the keyboard menu
        /// what the keyboard's layout key, Fn key, top bar, grip and
        /// hold-to-move changed, and draws the poll thread's snapshot. A
        /// snapshot the poll thread stopped refreshing (the engine stopped)
        /// hides the window.</summary>
        private void UpdateOnScreenKeyboard()
        {
            var im = _inputManager;
            if (im == null)
            {
                _oskWindow?.HideKeyboard();
                return;
            }
            // Each engine start makes a new InputManager, which has none of
            // what the last one was handed: the timing and the layout.
            if (!ReferenceEquals(im, _oskPushedTo))
            {
                _oskPushedTo = im;
                _oskLayoutHkl = IntPtr.Zero;
                _oskLayout = null;
                _oskLayoutCheckedMs = _oskRepeatCheckedMs = long.MinValue / 2;
                _oskSeenOwner = (-1, -1);
                _oskStyleSeen = null;
                _oskFunctionRowSeen = null;
            }
            long now = Environment.TickCount64;
            var view = im.OnScreenKeyboardView;
            bool open = view != null && view.Open && now - view.StampMs <= 500;
            var menu = open ? FindKeyboardMenu(view.Slot, view.MenuId) : null;
            open &= menu != null;
            _oskOwnerSlot = open ? view.Slot : -1;
            _oskOwnerMenu = open ? menu : null;

            // The keyboard's own keys switch its layout and function row:
            // save what a key changed. A change counts, not a difference, so a
            // menu edited while the keyboard is open keeps the edit.
            if (open)
            {
                if (_oskSeenOwner != (view.Slot, view.MenuId))
                {
                    _oskSeenOwner = (view.Slot, view.MenuId);
                    _oskStyleSeen = null;
                    _oskFunctionRowSeen = null;
                }
                if (_oskStyleSeen is { } style && style != view.Style)
                {
                    var layout = (KeyboardLayout)view.Style;
                    EditKeyboardMenu(view.Slot, menu, e => e.KeyboardLayoutIndex = (int)layout, m => m.KeyboardLayout = layout);
                }
                if (_oskFunctionRowSeen is { } row && row != view.FunctionRow)
                {
                    bool fn = view.FunctionRow;
                    EditKeyboardMenu(view.Slot, menu, e => e.FunctionRow = fn, m => m.FunctionRow = fn);
                }
                _oskStyleSeen = view.Style;
                _oskFunctionRowSeen = view.FunctionRow;
            }
            else
            {
                _oskSeenOwner = (-1, -1);
                _oskStyleSeen = null;
                _oskFunctionRowSeen = null;
            }

            if (now - _oskRepeatCheckedMs >= 5000)
            {
                _oskRepeatCheckedMs = now;
                var (delay, interval) = OskWindowsKeyboard.RepeatTiming();
                im.SetOnScreenKeyboardRepeat(delay, interval);
            }

            // The layout of the window that has focus, which is where the keys
            // land. Read on this thread, which owns the foreground question,
            // and handed over whole: the poll thread never sees half a read.
            if ((open || _oskLayout == null) && now - _oskLayoutCheckedMs >= 250)
            {
                _oskLayoutCheckedMs = now;
                var hkl = OskWindowsKeyboard.ForegroundLayout();
                if (hkl != IntPtr.Zero && hkl != _oskLayoutHkl)
                {
                    _oskLayoutHkl = hkl;
                    _oskLayout = new OskWindowsLayout(hkl);
                    im.SetOnScreenKeyboardLayout(_oskLayout);
                }
            }
            _oskLayout?.SetCaps(System.Windows.Input.Keyboard.IsKeyToggled(System.Windows.Input.Key.CapsLock));

            if (!open)
            {
                _oskMoveCarryX = _oskMoveCarryY = 0;
                _oskWindow?.HideKeyboard();
                return;
            }

            // Hold-to-move comes in percent of the work area, the unit of the
            // menu's Screen Position, which keeps whole percents.
            var move = im.TakeOnScreenKeyboardMove();
            if (move.X != 0 || move.Y != 0)
            {
                _oskMoveCarryX += move.X;
                _oskMoveCarryY += move.Y;
                int dx = (int)_oskMoveCarryX, dy = (int)_oskMoveCarryY;
                _oskMoveCarryX -= dx;
                _oskMoveCarryY -= dy;
                if (dx != 0 || dy != 0)
                {
                    int x = Math.Clamp(menu.PosXPercent + dx, 0, 100), y = Math.Clamp(menu.PosYPercent + dy, 0, 100);
                    SetKeyboardPlace(view.Slot, menu, x, y);
                }
            }

            int slot = view.Slot;
            _oskWindow ??= new Views.OnScreenKeyboardWindow(
                c => _inputManager?.PostOnScreenKeyboardCommand(c),
                (cx, cy) =>
                {
                    if (_oskOwnerMenu == null) return;
                    var (x, y) = Views.OnScreenKeyboardWindow.PercentOf(cx, cy);
                    SetKeyboardPlace(_oskOwnerSlot, _oskOwnerMenu, x, y);
                },
                scale =>
                {
                    if (_oskOwnerMenu == null) return;
                    int v = Math.Clamp(scale, Views.OnScreenKeyboardWindow.MinScalePercent, Views.OnScreenKeyboardWindow.MaxScalePercent);
                    EditKeyboardMenu(_oskOwnerSlot, _oskOwnerMenu, e => e.ScalePercent = v, m => m.ScalePercent = v);
                });
            _oskWindow.UpdateFromView(view, menu, (d, g) => DescribeKeyboardInput(slot, d, g));
        }

        private void SetKeyboardPlace(int slot, MenuDefinitionEntry menu, int x, int y)
        {
            if (menu.PosXPercent != x) EditKeyboardMenu(slot, menu, e => e.PosXPercent = x, m => m.PosXPercent = x);
            if (menu.PosYPercent != y) EditKeyboardMenu(slot, menu, e => e.PosYPercent = y, m => m.PosYPercent = y);
        }

        /// <summary>The keyboard menu a snapshot names, if it is still on its
        /// slot.</summary>
        private static MenuDefinitionEntry FindKeyboardMenu(int slot, int menuId)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || slot < 0 || slot >= sets.Length) return null;
            var menus = sets[slot]?.Menus;
            if (menus == null) return null;
            for (int i = 0; i < menus.Count; i++)
            {
                MenuDefinitionEntry def;
                try { def = menus[i]; } catch { break; }
                if (def != null && def.MenuId == menuId && def.Kind == MenuKind.Keyboard) return def;
            }
            return null;
        }

        /// <summary>Saves a change the keyboard made to its menu through the
        /// menu's editor, so the Menus tab shows it and the settings save. A
        /// menu with no editor (none built yet) takes it directly.</summary>
        private void EditKeyboardMenu(int slot, MenuDefinitionEntry menu, Action<MenuEditorItem> viaEditor, Action<MenuDefinitionEntry> direct)
        {
            MenuEditorItem item = null;
            if (slot >= 0 && slot < _mainVm.Pads.Count)
                foreach (var m in _mainVm.Pads[slot].Menus)
                    if (ReferenceEquals(m.Entry, menu)) { item = m; break; }
            if (item != null)
            {
                viaEditor(item);
                return;
            }
            direct(menu);
            _settingsService?.MarkDirty();
        }

        /// <summary>The name the slot's input lists give an input on a device.</summary>
        private string DescribeKeyboardInput(int slot, string descriptor, string deviceGuid)
            => slot >= 0 && slot < _mainVm.Pads.Count
                ? _mainVm.Pads[slot].DescribeSlotInput(descriptor, deviceGuid)
                : descriptor;
    }
}
