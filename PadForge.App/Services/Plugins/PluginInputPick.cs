using System;
using System.Collections.Generic;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// A plugin's <c>input</c> setting picked by pressing it (#600 item 17),
    /// the way a mapping row records its source (RecorderService): each
    /// device's state when the pick starts is its baseline, and the first
    /// button pressed, hat turned or axis moved past half its travel from
    /// there wins. The words are the recorder's: <c>Button 3</c>,
    /// <c>Axis 1</c>, <c>Slider 0</c>, <c>POV 0</c>. A device's own setting
    /// listens to that device alone, a plugin's to every connected device.
    /// Ten seconds with no input ends the pick with nothing. One pick at a
    /// time: a new one ends the old one with nothing. It only reads the
    /// states the poll loop keeps, so it sends nothing anywhere. On the
    /// interface's thread.
    /// </summary>
    internal static class PluginInputPick
    {
        internal const int TimeoutMs = 10_000;
        internal const int PollMs = 30;

        /// <summary>The devices to listen to: connected ones with a state.
        /// A test gives its own.</summary>
        internal static Func<IReadOnlyList<(Guid Guid, CustomInputState State)>> Source = Connected;

        private static DispatcherTimer _timer;
        private static Action<string, string> _done;

        /// <summary>Starts a pick. <paramref name="done"/> gets the device's
        /// short ID and the control's name, or two nulls when nothing was
        /// pressed.</summary>
        internal static void Start(Guid? device, Action<string, string> done)
        {
            Stop(null, null);
            var baselines = new Dictionary<Guid, CustomInputState>();
            foreach (var (guid, state) in Source())
                if (state != null && (device == null || guid == device.Value)) baselines[guid] = state.Clone();
            if (baselines.Count == 0)
            {
                done?.Invoke(null, null);
                return;
            }
            _done = done;
            var started = Environment.TickCount64;
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(PollMs) };
            _timer.Tick += (_, _) =>
            {
                foreach (var (guid, state) in Source())
                {
                    if (state == null || !baselines.TryGetValue(guid, out var before)) continue;
                    string control = Changed(before, state);
                    if (control != null)
                    {
                        Stop(AutomationDevices.IdOf(guid), control);
                        return;
                    }
                }
                if (Environment.TickCount64 - started >= TimeoutMs) Stop(null, null);
            };
            _timer.Start();
        }

        /// <summary>The first control that moved from the baseline, or
        /// null.</summary>
        internal static string Changed(CustomInputState before, CustomInputState now)
        {
            for (int i = 0; i < now.Buttons.Length && i < before.Buttons.Length; i++)
                if (now.Buttons[i] && !before.Buttons[i]) return "Button " + i;
            for (int i = 0; i < now.Povs.Length && i < before.Povs.Length; i++)
                if (now.Povs[i] != before.Povs[i] && now.Povs[i] >= 0) return "POV " + i;
            for (int i = 0; i < now.Axis.Length && i < before.Axis.Length; i++)
                if (Math.Abs(now.Axis[i] - before.Axis[i]) > 32767) return "Axis " + i;
            for (int i = 0; i < now.Sliders.Length && i < before.Sliders.Length; i++)
                if (Math.Abs(now.Sliders[i] - before.Sliders[i]) > 32767) return "Slider " + i;
            return null;
        }

        private static void Stop(string device, string control)
        {
            _timer?.Stop();
            _timer = null;
            var done = _done;
            _done = null;
            done?.Invoke(device, control);
        }

        private static IReadOnlyList<(Guid, CustomInputState)> Connected()
        {
            var list = new List<(Guid, CustomInputState)>();
            var devices = SettingsManager.UserDevices;
            if (devices?.Items == null) return list;
            lock (devices.SyncRoot)
                foreach (UserDevice ud in devices.Items)
                    if (ud != null && ud.IsOnline && ud.InputState != null) list.Add((ud.InstanceGuid, ud.InputState.Clone()));
            return list;
        }
    }
}
