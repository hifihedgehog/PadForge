using System;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        private KeyboardScreenService _keyboardScreens;
        private string _keyboardScreensStatusLast;

        /// <summary>The keyboard screens (#519), made on first use.</summary>
        internal KeyboardScreenService KeyboardScreens
            => _keyboardScreens ??= new KeyboardScreenService(_mainVm.Settings,
                () => _settingsService?.MarkDirty(),
                m => PadForge.Engine.SdlDiagLog.WriteLine(m))
            {
                OverlayNumbers = () => _mainVm.Dashboard.ScreenOverlayNumbers(),
                OverlaySize = number => _mainVm.Dashboard.FindScreenOverlay(number) is { } overlay
                    ? new System.Windows.Size(overlay.Width, overlay.Height) : System.Windows.Size.Empty,
                // What an overlay shows is named on its card (#574).
                ChoicesChanged = row => _mainVm.Dashboard.ScreenOverlayChoicesChanged(row),
            };

        /// <summary>Every UI tick while the engine runs, ungated by focus: a
        /// clock or a show on a keyboard screen keeps running while PadForge
        /// sits behind a game, the Dreamcast screens' rule. The service holds
        /// itself to four passes a second. With the Keyboard Screens switch off and
        /// no Screen Overlay up, a service that ran lets go of what it held, which
        /// closes every screen's session, a macro's work still lands, and play time
        /// counts from the profile's switch for a window that comes up later. The
        /// switch off with an overlay up closes the sessions and leaves the overlay
        /// running.</summary>
        private void TickKeyboardScreens()
        {
            bool hardware = _mainVm.Settings.KeyboardScreensEnabled;
            bool anyUp = false;
            foreach (var window in _screenOverlays.Values)
            {
                if (!window.IsVisible) continue;
                anyUp = true;
                TendScreenOverlay(window);
            }
            if (!hardware && !anyUp)
            {
                try
                {
                    KeyboardScreens.TickWithNothingUp();
                }
                catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("Keyboard screens: tick failed: " + ex.Message); }
                if (_keyboardScreens?.Active == true) _keyboardScreens.ReleaseWhileIdle();
                return;
            }
            if (!hardware && _keyboardScreens?.Running == true) _keyboardScreens.CloseHost();
            try { KeyboardScreens.Tick(hardware); }
            catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("Keyboard screens: tick failed: " + ex.Message); }
        }

        // ─────────────────────────────────────────────
        //  Screen Overlay lifecycle (#560)
        // ─────────────────────────────────────────────

        /// <summary>The windows by overlay number, up or hidden.</summary>
        private readonly System.Collections.Generic.Dictionary<int, Views.ScreenOverlay> _screenOverlays = new();
        private bool _screenOverlaySyncPending;
        private bool _screenOverlaysStopped;
        private long _screenOverlayOnTopAt;

        /// <summary>While PadForge is in front an overlay takes the mouse so it can be
        /// moved and sized. While another program is, clicks pass through it, and
        /// once a second it goes back above a game that made itself topmost after
        /// it.</summary>
        private void TendScreenOverlay(Views.ScreenOverlay overlay)
        {
            overlay.SetArranging(IsPadForgeForeground());
            long now = Environment.TickCount64;
            if (now - _screenOverlayOnTopAt < 1000) return;
            _screenOverlayOnTopAt = now;
            foreach (var window in _screenOverlays.Values)
                if (window.IsVisible) window.KeepOnTop();
        }

        private bool AnyScreenOverlayUp()
        {
            foreach (var window in _screenOverlays.Values)
                if (window.IsVisible) return true;
            return false;
        }

        /// <summary>The Keyboard Screens switch went off: a window that is up keeps
        /// its pictures and its play time.</summary>
        private void LetGoOfScreens()
        {
            if (AnyScreenOverlayUp()) _keyboardScreens?.CloseHost();
            else _keyboardScreens?.ReleaseWhileIdle();
        }

        private void ScheduleScreenOverlaySync()
        {
            if (_screenOverlaySyncPending) return;
            _screenOverlaySyncPending = true;
            _dispatcher.BeginInvoke(SyncScreenOverlays);
        }

        /// <summary>Brings the windows into line with the Dashboard's list, whatever
        /// changed: a switch, an opacity, a place, a profile switch, an overlay added
        /// or removed. It changes only what differs, so running it again does
        /// nothing, and an overlay in the middle of a drag keeps its place until the
        /// drag ends.</summary>
        private void SyncScreenOverlays()
        {
            _screenOverlaySyncPending = false;
            // One queued as the engine stopped would open a window nothing ticks.
            if (_screenOverlaysStopped) return;
            var dash = _mainVm.Dashboard;

            var gone = new System.Collections.Generic.List<int>();
            foreach (int number in _screenOverlays.Keys)
                if (dash.FindScreenOverlay(number) == null) gone.Add(number);
            foreach (int number in gone) CloseScreenOverlay(number);

            foreach (var item in dash.ScreenOverlays)
            {
                _screenOverlays.TryGetValue(item.Number, out var window);
                if (!item.Enabled)
                {
                    if (window?.IsVisible == true)
                    {
                        window.Hide();
                        _keyboardScreens?.DetachOverlay(item.Number);
                    }
                    item.IsRunning = false;
                    continue;
                }

                if (window == null)
                {
                    int number = item.Number;
                    window = new Views.ScreenOverlay();
                    window.PositionChanged += () => OnScreenOverlayPositionChanged(number);
                    window.PanDragged += (moved, begin) => KeyboardScreens.DragOverlayPan(number, moved, begin);
                    _screenOverlays[number] = window;
                }
                window.SetContentOpacity(item.Opacity);
                if (!window.InGesture) PlaceScreenOverlay(window, item);
                if (!window.IsVisible)
                {
                    window.SetArranging(IsPadForgeForeground());
                    var shown = window;
                    int shownNumber = item.Number;
                    KeyboardScreens.AttachOverlay(shownNumber, content =>
                    {
                        shown.ShowContent(content);
                        if (_mainVm.Dashboard.FindScreenOverlay(shownNumber) is { } showing) showing.Blank = content.Blank;
                    }, () => new System.Windows.Size(shown.ActualWidth, shown.ActualHeight));
                    window.Show();
                }
                // Self-heal a saved place no monitor shows, e.g. a now-detached
                // display.
                if (!window.InGesture) Views.OverlayPlacement.EnsureOnScreen(window, item.Monitor);
                item.IsRunning = true;
            }
        }

        private static void PlaceScreenOverlay(Views.ScreenOverlay overlay, ScreenOverlayItem item)
        {
            overlay.Width = item.Width;
            overlay.Height = item.Height;
            if (Views.OverlayPlacement.HasPlace(item.Left, item.Top))
            {
                overlay.Left = item.Left;
                overlay.Top = item.Top;
            }
            else if (!overlay.IsVisible) CenterScreenOverlay(overlay, item);
        }

        /// <summary>Centered on its monitor, and stepped down and across by its number,
        /// so a new overlay does not open exactly over the one before it.</summary>
        private static void CenterScreenOverlay(Views.ScreenOverlay overlay, ScreenOverlayItem item)
        {
            const double step = 32;
            Views.OverlayPlacement.MoveToMonitor(overlay, item.Monitor);
            overlay.Left += (item.Number - 1) * step;
            overlay.Top += (item.Number - 1) * step;
        }

        private void CloseScreenOverlay(int number)
        {
            if (!_screenOverlays.Remove(number, out var window)) return;
            window.Close();
            _keyboardScreens?.DetachOverlay(number);
        }

        /// <summary>The engine stopped: the windows close rather than hide, which
        /// prevents a shutdown hang.</summary>
        private void CloseScreenOverlays()
        {
            _screenOverlaysStopped = true;
            foreach (int number in new System.Collections.Generic.List<int>(_screenOverlays.Keys))
                CloseScreenOverlay(number);
            foreach (var item in _mainVm.Dashboard.ScreenOverlays) item.IsRunning = false;
        }

        /// <summary>Toggle Screen Overlay macros since the last tick: a bit per
        /// overlay's number. Bit 0 is every overlay: any that is on, and they all go
        /// off. None on, and they all come on. The windows follow at once rather than
        /// on the next pass: the keyboard screens' tick comes next, and a macro that
        /// goes on to show a picture finds its window up.</summary>
        internal void ToggleScreenOverlays(int mask)
        {
            var dash = _mainVm.Dashboard;
            if ((mask & 1) != 0)
            {
                bool anyOn = false;
                foreach (var item in dash.ScreenOverlays) anyOn |= item.Enabled;
                foreach (var item in dash.ScreenOverlays) item.Enabled = !anyOn;
            }
            foreach (var item in dash.ScreenOverlays)
                if ((mask & (1 << item.Number)) != 0) item.Enabled = !item.Enabled;
            SyncScreenOverlays();
        }

        private void OnScreenOverlayPositionChanged(int number)
        {
            if (!_screenOverlays.TryGetValue(number, out var window)) return;
            var item = _mainVm.Dashboard.FindScreenOverlay(number);
            if (item == null) return;
            item.Left = window.Left;
            item.Top = window.Top;
            item.Width = window.Width;
            item.Height = window.Height;
            item.Monitor = Views.OverlayPlacement.GetCurrentMonitor(window);
        }

        private void OnResetScreenOverlayPosition(object sender, ScreenOverlayItem item)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_screenOverlays.TryGetValue(item.Number, out var window) && window.IsVisible)
                {
                    // Recenter live, then capture the new DIPs into settings.
                    CenterScreenOverlay(window, item);
                    OnScreenOverlayPositionChanged(item.Number);
                }
                else
                {
                    // No saved place: the next show centers on the monitor.
                    item.Left = Views.OverlayPlacement.NoPlace;
                    item.Top = Views.OverlayPlacement.NoPlace;
                }
            });
        }

        /// <summary>The switch went off: the screens' sessions close, and a
        /// G19 screen interface PadForge bound to WinUSB goes back to Windows.
        /// The restore takes seconds, so it runs off the UI thread.</summary>
        internal void KeyboardScreensSwitched(bool on)
        {
            if (on) return;
            LetGoOfScreens();
            Task.Run(() =>
            {
                try { G19UsbScreen.Restore(m => PadForge.Engine.SdlDiagLog.WriteLine(m)); }
                catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("Keyboard screens: G19 restore failed: " + ex.Message); }
            });
        }

        /// <summary>
        /// The keyboard screens' lines under the Settings switch (#519): only
        /// what belongs to the feature as a whole, the vendor libraries and no
        /// screen found. Each screen's own line is on its row's panel
        /// (<see cref="UpdateKeyboardScreenDeviceRow"/>). Empty while the
        /// feature is off or the engine is stopped, which collapses the lines,
        /// the Bliss-Box shape.
        /// </summary>
        private void UpdateKeyboardScreensStatus()
        {
            var settings = _mainVm.Settings;
            var im = _inputManager;
            string status = string.Empty;
            if (im != null && im.IsRunning && settings.KeyboardScreensEnabled && _keyboardScreens?.Running == true)
                status = _keyboardScreens.SubsystemLines();
            // A line means the service ran, so it is there to ask. Only no
            // screen found warns (#574): a library's line for a kind that is
            // absent while another screen is present is information.
            settings.KeyboardScreensStatusIsWarning = status.Length > 0 && _keyboardScreens.FoundNoScreen;
            if (string.Equals(_keyboardScreensStatusLast, status, StringComparison.Ordinal)) return;
            _keyboardScreensStatusLast = status;
            settings.KeyboardScreensStatus = status;
        }

        /// <summary>
        /// The selected row's keyboard screen line and button (#519), on the
        /// Devices page's tick: what draws the screen or why nothing does,
        /// and the button that opens its dialog, the Dreamcast Screen
        /// button's place. A row that is not a present screen shows
        /// neither.
        /// </summary>
        private void UpdateKeyboardScreenDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            var screen = _mainVm.Settings.KeyboardScreensEnabled && _keyboardScreens?.Running == true
                         && ud.CapType == InputDeviceType.KeyboardScreenKeys
                ? _keyboardScreens.ScreenOf(ud.InstanceGuid) : null;
            row.KeyboardScreenLine = screen == null ? string.Empty : _keyboardScreens.StatusLine(screen);
            row.KeyboardScreenButtonText = screen == null ? string.Empty : KeyboardScreenService.ButtonText(screen.Kind);
            row.ShowKeyboardScreen = screen != null && ud.IsOnline;
        }
    }
}
