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
                m => PadForge.Engine.SdlDiagLog.WriteLine(m));

        /// <summary>Every UI tick while the engine runs, ungated by focus: a
        /// clock or a show on a keyboard screen keeps running while PadForge
        /// sits behind a game, the Dreamcast screens' rule. The service holds
        /// itself to four passes a second. With the Keyboard Screens switch and
        /// the Screen Overlay both off, a service that ran is reset, which closes
        /// every screen's session. The switch off with the overlay up closes the
        /// sessions and leaves the overlay running.</summary>
        private void TickKeyboardScreens()
        {
            bool hardware = _mainVm.Settings.KeyboardScreensEnabled;
            var overlay = _screenOverlay?.IsVisible == true ? _screenOverlay : null;
            if (!hardware && overlay == null)
            {
                if (_keyboardScreens?.Active == true) _keyboardScreens.Reset();
                else KeyboardScreenService.DropRequests();
                return;
            }
            if (!hardware && _keyboardScreens?.Running == true) _keyboardScreens.CloseHost();
            if (overlay != null) TendScreenOverlay(overlay);
            try { KeyboardScreens.Tick(hardware, overlay != null); }
            catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("Keyboard screens: tick failed: " + ex.Message); }
        }

        // ─────────────────────────────────────────────
        //  Screen Overlay lifecycle (#560)
        // ─────────────────────────────────────────────

        private Views.ScreenOverlay _screenOverlay;
        private long _screenOverlayOnTopAt;

        /// <summary>While PadForge is in front the overlay takes the mouse so it
        /// can be moved and sized. While another program is, clicks pass through
        /// it, and once a second it goes back above a game that made itself
        /// topmost after it.</summary>
        private void TendScreenOverlay(Views.ScreenOverlay overlay)
        {
            overlay.SetArranging(IsPadForgeForeground());
            long now = Environment.TickCount64;
            if (now - _screenOverlayOnTopAt < 1000) return;
            _screenOverlayOnTopAt = now;
            overlay.KeepOnTop();
        }

        private void ShowScreenOverlay()
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_screenOverlay == null)
                {
                    _screenOverlay = new Views.ScreenOverlay();
                    _screenOverlay.PositionChanged += OnScreenOverlayPositionChanged;
                }

                var dash = _mainVm.Dashboard;
                _screenOverlay.SetContentOpacity(dash.ScreenOverlayOpacity);
                _screenOverlay.SetArranging(IsPadForgeForeground());
                KeyboardScreens.OverlaySink = _screenOverlay.ShowContent;
                PlaceScreenOverlay();
                _screenOverlay.Show();
                // Self-heal a saved place no monitor shows, e.g. a now-detached
                // display.
                Views.OverlayPlacement.EnsureOnScreen(_screenOverlay, dash.ScreenOverlayMonitor);
                dash.IsScreenOverlayRunning = true;
            });
        }

        private void HideScreenOverlay()
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_screenOverlay == null) return;
                _screenOverlay.Hide();
                _mainVm.Dashboard.IsScreenOverlayRunning = false;
            });
        }

        /// <summary>The engine stopped: the window closes rather than hides, which
        /// prevents a shutdown hang.</summary>
        private void CloseScreenOverlay()
        {
            if (_screenOverlay == null) return;
            _screenOverlay.PositionChanged -= OnScreenOverlayPositionChanged;
            _screenOverlay.Close();
            _screenOverlay = null;
            if (_keyboardScreens != null) _keyboardScreens.OverlaySink = null;
            _mainVm.Dashboard.IsScreenOverlayRunning = false;
        }

        /// <summary>Shows or hides the Screen Overlay (for the macro action).</summary>
        internal void ToggleScreenOverlay()
        {
            _dispatcher.BeginInvoke(() =>
            {
                var dash = _mainVm.Dashboard;
                dash.EnableScreenOverlay = !dash.EnableScreenOverlay;
            });
        }

        private void PlaceScreenOverlay()
        {
            var overlay = _screenOverlay;
            if (overlay == null) return;
            var dash = _mainVm.Dashboard;
            overlay.Width = dash.ScreenOverlayWidth;
            overlay.Height = dash.ScreenOverlayHeight;
            if (Views.OverlayPlacement.HasPlace(dash.ScreenOverlayLeft, dash.ScreenOverlayTop))
            {
                overlay.Left = dash.ScreenOverlayLeft;
                overlay.Top = dash.ScreenOverlayTop;
            }
            else Views.OverlayPlacement.MoveToMonitor(overlay, dash.ScreenOverlayMonitor);
        }

        /// <summary>A profile switch brings the profile's own place, and the window
        /// goes there, once all of the place's values have landed. The window's own
        /// drag writes these values too, which places it where it already is.</summary>
        private void ScreenOverlayPlaceChanged()
        {
            if (_screenOverlay == null) return;
            _dispatcher.BeginInvoke(() =>
            {
                PlaceScreenOverlay();
                if (_screenOverlay?.IsVisible == true)
                    Views.OverlayPlacement.EnsureOnScreen(_screenOverlay, _mainVm.Dashboard.ScreenOverlayMonitor);
            });
        }

        private void OnScreenOverlayPositionChanged()
        {
            if (_screenOverlay == null) return;
            var dash = _mainVm.Dashboard;
            dash.ScreenOverlayLeft = _screenOverlay.Left;
            dash.ScreenOverlayTop = _screenOverlay.Top;
            dash.ScreenOverlayWidth = _screenOverlay.Width;
            dash.ScreenOverlayHeight = _screenOverlay.Height;
            dash.ScreenOverlayMonitor = Views.OverlayPlacement.GetCurrentMonitor(_screenOverlay);
        }

        private void OnResetScreenOverlayPosition(object sender, EventArgs e)
        {
            _dispatcher.BeginInvoke(() =>
            {
                var dash = _mainVm.Dashboard;
                if (_screenOverlay != null && _screenOverlay.IsVisible)
                {
                    // Recenter live, then capture the new DIPs into settings.
                    Views.OverlayPlacement.MoveToMonitor(_screenOverlay, dash.ScreenOverlayMonitor);
                    OnScreenOverlayPositionChanged();
                }
                else
                {
                    // No saved place: the next show centers on the monitor.
                    dash.ScreenOverlayLeft = Views.OverlayPlacement.NoPlace;
                    dash.ScreenOverlayTop = Views.OverlayPlacement.NoPlace;
                }
            });
        }

        /// <summary>The switch went off: the screens' sessions close, and a
        /// G19 screen interface PadForge bound to WinUSB goes back to Windows.
        /// The restore takes seconds, so it runs off the UI thread.</summary>
        internal void KeyboardScreensSwitched(bool on)
        {
            if (on) return;
            if (_mainVm.Dashboard.EnableScreenOverlay) _keyboardScreens?.CloseHost();
            else
            {
                _keyboardScreens?.Reset();
                KeyboardScreenService.DropRequests();
            }
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
