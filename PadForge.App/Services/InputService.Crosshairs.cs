using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Crosshairs;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        // ─────────────────────────────────────────────
        //  Crosshairs and the magnifier (#585)
        // ─────────────────────────────────────────────

        /// <summary>The crosshairs' windows by number, up or hidden.</summary>
        private readonly Dictionary<int, Views.CrosshairOverlay> _crosshairs = new();
        private bool _crosshairSyncPending;
        private bool _magnifierSyncPending;

        /// <summary>The engine stopped: a sync queued as it stopped opens
        /// nothing, as with the Screen Overlays.</summary>
        private bool _crosshairsStopped;

        private bool _magnifierHeld;
        private long _crosshairOnTopAt;
        private MagnifierService _magnifier;

        private MagnifierService Magnifier => _magnifier ??= new MagnifierService(m => PadForge.Engine.SdlDiagLog.WriteLine(m))
        {
            CrosshairWindows = CrosshairHandles,
            FirstCrosshair = FirstCrosshairOn,
        };

        /// <summary>The handles of the crosshair windows that are up, which
        /// the lens leaves out of what it magnifies.</summary>
        private IntPtr[] CrosshairHandles()
        {
            var handles = new List<IntPtr>();
            foreach (var window in _crosshairs.Values)
                if (window.IsVisible) handles.Add(window.Handle);
            return handles.ToArray();
        }

        /// <summary>The lowest-numbered crosshair that is on, for a magnifier
        /// that follows a crosshair.</summary>
        private (string Display, double OffsetX, double OffsetY)? FirstCrosshairOn()
        {
            foreach (var item in _mainVm.Dashboard.Crosshairs)
                if (item.Enabled) return (item.Display, item.OffsetX, item.OffsetY);
            return null;
        }

        private void ScheduleCrosshairSync()
        {
            if (_crosshairSyncPending) return;
            _crosshairSyncPending = true;
            _dispatcher.BeginInvoke(SyncCrosshairs);
        }

        private void ScheduleMagnifierSync()
        {
            if (_magnifierSyncPending) return;
            _magnifierSyncPending = true;
            _dispatcher.BeginInvoke(SyncMagnifier);
        }

        /// <summary>Brings the crosshair windows into line with the page's list,
        /// whatever changed: a switch, a look, a profile switch, a crosshair
        /// added or removed. Running it again changes nothing.</summary>
        private void SyncCrosshairs()
        {
            _crosshairSyncPending = false;
            if (_crosshairsStopped) return;
            var dash = _mainVm.Dashboard;

            var gone = new List<int>();
            foreach (int number in _crosshairs.Keys)
                if (dash.FindCrosshair(number) == null) gone.Add(number);
            foreach (int number in gone)
                if (_crosshairs.Remove(number, out var closing)) closing.Close();

            foreach (var item in dash.Crosshairs)
            {
                _crosshairs.TryGetValue(item.Number, out var window);
                if (!item.Enabled)
                {
                    if (window?.IsVisible == true) window.Hide();
                    item.IsRunning = false;
                    item.Problem = CrosshairProblem.None;
                    continue;
                }
                if (window == null)
                {
                    window = new Views.CrosshairOverlay(item.Title);
                    // A display change that hides or shows it brings the
                    // card's status back in line.
                    window.ShownOrHiddenByDisplays += ScheduleCrosshairSync;
                    _crosshairs[item.Number] = window;
                }
                window.Title = item.Title;
                window.ShowLook(item.Look);
                item.IsRunning = window.IsVisible;
                item.Problem = window.IsVisible ? CrosshairProblem.None
                    : item.IsImage && CrosshairPictures.Load(item.ImagePath) == null ? CrosshairProblem.NoPicture
                    : CrosshairProblem.OffDisplay;
            }

            // The lens leaves the new set out, and a magnifier that follows
            // a crosshair goes where it went.
            if (_magnifier?.IsShowing == true)
            {
                _magnifier.RefreshLeftOut();
                if (dash.Magnifier.Follow == MagnifierFollow.Crosshair) ScheduleMagnifierSync();
            }
        }

        /// <summary>Brings the magnifier into line with its page and its
        /// macros: up while its switch is on or a Magnifier While Held macro
        /// is held, down otherwise.</summary>
        private void SyncMagnifier()
        {
            _magnifierSyncPending = false;
            var item = _mainVm.Dashboard.Magnifier;
            if (_crosshairsStopped)
            {
                item.IsRunning = false;
                return;
            }
            if (item.Enabled || _magnifierHeld)
            {
                Magnifier.Show(item.ToData());
                // The crosshairs stay above the lens and the spotlight.
                foreach (var window in _crosshairs.Values)
                    if (window.IsVisible) window.Raise();
            }
            else _magnifier?.Hide();
            item.IsRunning = _magnifier?.IsShowing == true;
            item.Failure = _magnifier?.Failure;
        }

        /// <summary>Every UI tick while the engine runs, ungated by focus,
        /// since a game holds it: the macros' requests, Magnifier While
        /// Held's hold, and once a second the windows back above a game that
        /// went on top, the magnifier's first so the crosshairs stay over
        /// it.</summary>
        private void TickCrosshairs()
        {
            if (Volatile.Read(ref _inputManager.ToggleCrosshairMask) != 0)
                ToggleCrosshairs(Interlocked.Exchange(ref _inputManager.ToggleCrosshairMask, 0));
            if (Interlocked.Exchange(ref _inputManager.ToggleMagnifierRequested, 0) != 0)
                _mainVm.Dashboard.Magnifier.Enabled = !_mainVm.Dashboard.Magnifier.Enabled;
            bool held = _inputManager.MagnifierHeld;
            if (held != _magnifierHeld)
            {
                _magnifierHeld = held;
                SyncMagnifier();
            }

            long now = Environment.TickCount64;
            if (now - _crosshairOnTopAt < 1000) return;
            _crosshairOnTopAt = now;
            _magnifier?.Tend();
            foreach (var window in _crosshairs.Values) window.KeepOnTop();
        }

        /// <summary>Toggle Crosshair macros since the last tick: a bit per
        /// crosshair's number. Bit 0 is every crosshair: any that is on, and
        /// they all go off. None on, and they all come on. The rule
        /// ToggleScreenOverlays follows.</summary>
        internal void ToggleCrosshairs(int mask)
        {
            var dash = _mainVm.Dashboard;
            if ((mask & 1) != 0)
            {
                bool anyOn = false;
                foreach (var item in dash.Crosshairs) anyOn |= item.Enabled;
                foreach (var item in dash.Crosshairs) item.Enabled = !anyOn;
            }
            foreach (var item in dash.Crosshairs)
                if ((mask & (1 << item.Number)) != 0) item.Enabled = !item.Enabled;
            SyncCrosshairs();
        }

        /// <summary>The engine stopped: the windows close rather than hide,
        /// which prevents a shutdown hang, and a full-screen zoom goes back to
        /// 1.</summary>
        private void CloseCrosshairs()
        {
            _crosshairsStopped = true;
            foreach (var window in _crosshairs.Values) window.Close();
            _crosshairs.Clear();
            foreach (var item in _mainVm.Dashboard.Crosshairs)
            {
                item.IsRunning = false;
                item.Problem = CrosshairProblem.None;
            }
            _magnifier?.Close();
            _magnifierHeld = false;
            _mainVm.Dashboard.Magnifier.IsRunning = false;
            _mainVm.Dashboard.Magnifier.Failure = null;
        }

        /// <summary>The engine started: the crosshairs and the magnifier that
        /// are on come up.</summary>
        private void OpenCrosshairs()
        {
            _crosshairsStopped = false;
            ScheduleCrosshairSync();
            ScheduleMagnifierSync();
        }
    }
}
