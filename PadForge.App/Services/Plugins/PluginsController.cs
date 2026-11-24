using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.ViewModels;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The plugins' part of the app (#597), made by the window once the
    /// Automation host is: it runs the plugin manager and the two built-in
    /// plugins, gives the host the plugins' key check, starts and stops
    /// them with the Plugins switch and the engine, and keeps the Plugins
    /// page's rows, lines and log in step with them on the interface's
    /// thread. It mirrors the Automation page's part
    /// (DashboardViewModel.AttachAutomation): the work happens on any
    /// thread, and the page is drawn on its own.
    /// </summary>
    internal sealed class PluginsController : IPluginsPageActions, IDisposable
    {
        private readonly AutomationHost _host;
        private readonly DashboardViewModel _vm;
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<PluginRun, PluginRowItem> _rows = new();
        private int _queued;
        private int _nextRow;
        private bool _disposed;

        public PluginManager Manager { get; }

        /// <summary>How many times the page was drawn, for a test that holds
        /// a flood of changes to a few draws.</summary>
        internal int Draws { get; private set; }
        public BuiltInPlugins BuiltIns { get; }

        public PluginsController(AutomationHost host, DashboardViewModel vm, Dispatcher ui, PluginManager manager = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            Manager = manager ?? new PluginManager(new PluginLauncher(), host.AutomationPipe.Name);
            BuiltIns = new BuiltInPlugins(host.Sessions.Devices,
                () => Strings.Instance.Plugins_TestController,
                BuiltInPlugins.ReplayShownName);

            host.AcceptPluginToken = Manager.AcceptToken;
            host.EngineChanged += OnEngine;
            Manager.Changed += Queue;
            Manager.SavedChanged += OnSaved;
            BuiltIns.ReplayEnded += Queue;
            vm.PluginActions = this;
            vm.PropertyChanged += OnVm;
            vm.PluginSettingsLoaded += OnLoaded;
            vm.SelectedLogPluginChanged += RefreshLog;
            Strings.CultureChanged += Refresh;
            // The built-ins' devices are named in the language in use.
            Strings.CultureChanged += BuiltIns.RefreshNames;

            // The status line, the restart pauses and Replay's place, once a
            // second while Plugins is on.
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), ui);
            _timer.Stop();

            OnLoaded();
            Manager.SetEngine(host.EngineAttached);
            Apply();
        }

        // ── Following the settings, the engine and the plugins ──

        private void OnLoaded()
        {
            Manager.Load(_vm.PluginRecords);
            BuiltIns.TestControllerSpeed = _vm.TestControllerSpeed;
            BuiltIns.TestControllerOn = _vm.TestControllerOn;
            BuiltIns.Repeat = _vm.ReplayRepeat;
            BuiltIns.ReplayOn = _vm.ReplayOn;
            Refresh();
        }

        private void OnVm(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DashboardViewModel.EnablePlugins): Apply(); break;
                case nameof(DashboardViewModel.TestControllerOn): BuiltIns.TestControllerOn = _vm.TestControllerOn; Refresh(); break;
                case nameof(DashboardViewModel.TestControllerSpeed): BuiltIns.TestControllerSpeed = _vm.TestControllerSpeed; break;
                case nameof(DashboardViewModel.ReplayOn): BuiltIns.ReplayOn = _vm.ReplayOn; Refresh(); break;
                case nameof(DashboardViewModel.ReplayRepeat): BuiltIns.Repeat = _vm.ReplayRepeat; break;
            }
        }

        /// <summary>The Plugins switch, as the page holds it.</summary>
        private void Apply()
        {
            bool on = _vm.EnablePlugins;
            SettingsManager.EnablePlugins = on;
            _host.ApplyPlugins(on);
            Manager.SetPluginsOn(on);
            BuiltIns.SetPluginsOn(on);
            if (on) _timer.Start();
            else _timer.Stop();
            Refresh();
        }

        /// <summary>Called on the thread that started or stopped the
        /// engine.</summary>
        private void OnEngine(bool running)
        {
            Manager.SetEngine(running);
            Queue();
        }

        private void OnSaved()
        {
            if (_ui.CheckAccess()) _vm.SetPluginRecords(Manager.Save());
            else _ui.BeginInvoke(new Action(() => _vm.SetPluginRecords(Manager.Save())));
        }

        /// <summary>A change from any thread, drawn once on the interface's:
        /// a burst of log lines lands in one update.</summary>
        private void Queue()
        {
            if (System.Threading.Interlocked.Exchange(ref _queued, 1) != 0) return;
            _ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                System.Threading.Volatile.Write(ref _queued, 0);
                Refresh();
            }));
        }

        // ── The page ──

        /// <summary>Draws the rows, the status line, Replay's lines and the
        /// picked log. On the interface's thread.</summary>
        internal void Refresh()
        {
            if (_disposed) return;
            Draws++;
            var s = Strings.Instance;
            var runs = Manager.Snapshot();

            // The rows, in the list's order, each kept while its plugin is.
            var keep = new HashSet<PluginRun>(runs);
            for (int i = _vm.PluginRows.Count - 1; i >= 0; i--)
                if (_vm.PluginRows[i].Run is PluginRun run && !keep.Contains(run))
                {
                    _rows.Remove(run);
                    _vm.PluginRows.RemoveAt(i);
                }
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                if (!_rows.TryGetValue(run, out var row))
                {
                    row = new PluginRowItem(run, ++_nextRow, this);
                    _rows[run] = row;
                    _vm.PluginRows.Insert(Math.Min(i, _vm.PluginRows.Count), row);
                }
                row.Show(run.ShownName, run.Path, StateOf(run), run.Enabled,
                    showStart: run.Status is PluginStatus.Failed or PluginStatus.Refused,
                    showApprove: run.Status == PluginStatus.Changed);
            }
            _vm.PluginRowsChanged();

            // The status line: how many plugins run and how many devices they
            // added, the two built in among them.
            if (!_vm.EnablePlugins)
            {
                _vm.PluginsStatus = null;
                _vm.IsPluginsRunning = false;
            }
            else
            {
                int running = Manager.RunningCount + (BuiltIns.TestControllerLive ? 1 : 0) + (BuiltIns.ReplayDeviceCount > 0 ? 1 : 0);
                int devices = PluginDevices() + (BuiltIns.TestControllerLive ? 1 : 0) + BuiltIns.ReplayDeviceCount;
                _vm.PluginsStatus = running == 0
                    ? s.Extras_Plugins_Status_None
                    : string.Format(CultureInfo.CurrentCulture, s.Plugins_Status_Format, running, devices);
                _vm.IsPluginsRunning = running > 0;
            }

            // Replay.
            var file = BuiltIns.File;
            _vm.ReplayFileLine = file == null ? null
                : string.Format(CultureInfo.CurrentCulture, s.Plugins_Replay_File_Format, file.FileName, Clock(file.LengthMicros));
            _vm.ReplayPositionLine = file == null ? null
                : string.Format(CultureInfo.CurrentCulture, s.Plugins_Replay_Position_Format, Clock(BuiltIns.PositionMicros), Clock(file.LengthMicros));
            _vm.IsReplayPlaying = BuiltIns.Playing;

            RefreshLog();
        }

        /// <summary>The devices plugins' sessions own now.</summary>
        private int PluginDevices()
        {
            int n = 0;
            foreach (var entry in _host.Sessions.Devices.All())
                if (entry.Owner is { IsPlugin: true }) n++;
            return n;
        }

        private void RefreshLog()
        {
            var lines = _vm.PluginLogLines;
            if (_vm.SelectedLogPlugin?.Run is not PluginRun run)
            {
                if (lines.Count > 0) lines.Clear();
                return;
            }
            var log = Manager.LogOf(run);
            var text = new List<string>(log.Count);
            foreach (var line in log) text.Add(LogText(line));
            // The list is redrawn only when it changed, and by its new lines
            // when it only grew.
            bool grew = text.Count >= lines.Count;
            for (int i = 0; grew && i < lines.Count; i++) grew = lines[i] == text[i];
            if (grew)
            {
                for (int i = lines.Count; i < text.Count; i++) lines.Add(text[i]);
                return;
            }
            lines.Clear();
            foreach (string line in text) lines.Add(line);
        }

        /// <summary>A plugin's state in a few words.</summary>
        internal static string StateOf(PluginRun run)
        {
            var s = Strings.Instance;
            return run.Status switch
            {
                PluginStatus.Running => s.Common_Running,
                PluginStatus.Waiting => s.Plugins_State_Waiting,
                PluginStatus.Checking => s.Plugins_State_Checking,
                PluginStatus.Restarting => run.Refusal != PluginRefusal.None
                    ? string.Format(CultureInfo.CurrentCulture, s.Plugins_State_Retry_Format, Seconds(run.PauseMs))
                    : string.Format(CultureInfo.CurrentCulture, s.Plugins_State_Exited_Format, run.ExitCode ?? 0, Seconds(run.PauseMs)),
                PluginStatus.Changed => s.Plugins_State_Changed,
                PluginStatus.Failed => s.Plugins_State_Failed,
                PluginStatus.Refused => RefusalText(run.Refusal, run.Error),
                _ => s.Common_Stopped,
            };
        }

        private static int Seconds(int ms) => Math.Max(1, (ms + 999) / 1000);

        /// <summary>Why a plugin was not started, in a few words.</summary>
        internal static string RefusalText(PluginRefusal why, int error)
        {
            var s = Strings.Instance;
            return why switch
            {
                PluginRefusal.Missing => s.Plugins_Refused_Missing,
                PluginRefusal.NoProgram => s.Plugins_Refused_NoProgram,
                PluginRefusal.NoShell => s.Plugins_Refused_NoShell,
                PluginRefusal.ShellElevated => s.Plugins_Refused_ShellElevated,
                PluginRefusal.NeedsAdministrator => s.Plugins_Refused_NeedsAdministrator,
                PluginRefusal.Failed when error != 0 => string.Format(CultureInfo.CurrentCulture, s.Plugins_Refused_Failed_Format, error),
                _ => s.Plugins_Refused_Other,
            };
        }

        /// <summary>A line of a plugin's log: its time, then what
        /// happened.</summary>
        internal static string LogText(PluginLogLine line)
        {
            var s = Strings.Instance;
            string text = line.Kind switch
            {
                PluginLogKind.Started => s.Plugins_Log_Started,
                PluginLogKind.Exited => string.Format(CultureInfo.CurrentCulture, s.Plugins_Log_Exited_Format, line.Code),
                PluginLogKind.NotStarted => RefusalText(line.Refusal, line.Code),
                PluginLogKind.Changed => s.Plugins_State_Changed,
                PluginLogKind.Failed => s.Plugins_State_Failed,
                PluginLogKind.ForeignKey => string.Format(CultureInfo.CurrentCulture, s.Plugins_Log_ForeignKey_Format, line.Code),
                _ => line.Text ?? string.Empty,
            };
            return line.Time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) + "  " + text;
        }

        /// <summary>A time in the file as minutes and seconds.</summary>
        internal static string Clock(long micros)
        {
            long seconds = Math.Max(0, micros) / 1_000_000;
            return (seconds / 60).ToString(CultureInfo.CurrentCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.CurrentCulture);
        }

        // ── What the page asks ──

        public PluginApproval Describe(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch { return null; }
            string sha = PluginFile.Sha256(full);
            return sha == null ? null : new PluginApproval(full, PluginFile.Signer(full), sha);
        }

        public bool Has(string path) => Manager.Has(path);

        public bool Add(PluginApproval approved) => approved != null && Manager.Add(approved.Path, approved.Sha256) != null;

        public void Remove(PluginRowItem row)
        {
            if (row?.Run is PluginRun run) Manager.Remove(run);
        }

        public void SetEnabled(PluginRowItem row, bool on)
        {
            if (row?.Run is PluginRun run) Manager.SetEnabled(run, on);
        }

        public void Approve(PluginRowItem row, PluginApproval approved)
        {
            if (row?.Run is PluginRun run && approved != null) Manager.Approve(run, approved.Sha256);
        }

        public void StartAgain(PluginRowItem row)
        {
            if (row?.Run is PluginRun run) Manager.StartAgain(run);
        }

        public string OpenRecording(string path)
        {
            string error = BuiltIns.Open(path);
            Refresh();
            return error;
        }

        public void Play()
        {
            BuiltIns.Play();
            Refresh();
        }

        public void Stop()
        {
            BuiltIns.Stop();
            Refresh();
        }

        public string SaveKit(string parent) => PluginKit.Save(parent);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _host.EngineChanged -= OnEngine;
            if (_host.AcceptPluginToken == Manager.AcceptToken) _host.AcceptPluginToken = null;
            _vm.PropertyChanged -= OnVm;
            _vm.PluginSettingsLoaded -= OnLoaded;
            _vm.SelectedLogPluginChanged -= RefreshLog;
            Strings.CultureChanged -= Refresh;
            Strings.CultureChanged -= BuiltIns.RefreshNames;
            if (ReferenceEquals(_vm.PluginActions, this)) _vm.PluginActions = null;
            Manager.Dispose();
            BuiltIns.Dispose();
        }
    }
}
