using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using PadForge.Engine.Common;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The plugins' part of the app (#597, #600 items 17 to 19), made by the
    /// window once the Automation host is: it runs the plugin manager and
    /// the two built-in plugins, gives the host the plugins' key check,
    /// starts and stops them with the Plugins switch and the engine, and
    /// keeps the Plugins page's cards, lines and log in step with them on
    /// the interface's thread. It mirrors the Automation page's part
    /// (DashboardViewModel.AttachAutomation): the work happens on any
    /// thread, and the page is drawn on its own.
    ///
    /// <para>Once a second while Plugins is on it looks after the plugins
    /// past their hello: a file that changed shows Approve and Restart, a
    /// plugin silent for <see cref="OfflineAfterSilenceMs"/> has its devices
    /// taken offline at rest, and one silent for
    /// <see cref="EndAfterSilenceMs"/> is ended and started again with the
    /// back-off unless a debugger is attached to it. A plugin that has not
    /// said hello by <see cref="PluginManager.HelloDeadlineMs"/> is ended
    /// the same way, and its card and Home's Plugins tile say it started but
    /// never connected.</para>
    /// </summary>
    internal sealed class PluginsController : IPluginsPageActions, IDisposable
    {
        internal const int OfflineAfterSilenceMs = 5000;
        internal const int EndAfterSilenceMs = 60000;

        /// <summary>The ID of the Test Controller in the catalog.</summary>
        internal const string TestControllerId = "plugin/test-controller";

        private readonly AutomationHost _host;
        private readonly DashboardViewModel _vm;
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<PluginRun, PluginRowItem> _rows = new();
        private readonly Func<long> _nowMs;
        private readonly Func<IReadOnlyList<LiveItem>> _catalog;
        private int _queued;
        private int _nextRow;
        private bool _disposed;

        public PluginManager Manager { get; }

        /// <summary>How many times the page was drawn, for a test that holds
        /// a flood of changes to a few draws.</summary>
        internal int Draws { get; private set; }
        public BuiltInPlugins BuiltIns { get; }

        /// <summary>The Test Controller's settings as its card draws
        /// them.</summary>
        internal PluginSettingsHost TestHost { get; }

        /// <summary>The controller the app runs, for the device panel's
        /// plugin card.</summary>
        internal static PluginsController Current { get; private set; }

        public PluginsController(AutomationHost host, DashboardViewModel vm, Dispatcher ui, PluginManager manager = null, Func<long> nowMs = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            Manager = manager ?? new PluginManager(new PluginLauncher(), host.AutomationPipe.Name);
            BuiltIns = new BuiltInPlugins(host.Sessions.Devices,
                () => Strings.Instance.Plugins_TestController,
                BuiltInPlugins.ReplayShownName);
            TestHost = new PluginSettingsHost(BuiltIns.TestSettings, null, "Plugins_TestController", ui);
            vm.TestControllerSettings = TestHost;

            host.AcceptPluginToken = Manager.AcceptToken;
            host.EngineChanged += OnEngine;
            Manager.Changed += Queue;
            Manager.SavedChanged += OnSaved;
            BuiltIns.ReplayEnded += Queue;
            BuiltIns.TestSettings.KeptChanged += OnTestValues;
            BuiltIns.TestSettings.Changed += Queue;
            vm.PluginActions = this;
            vm.PropertyChanged += OnVm;
            vm.PluginSettingsLoaded += OnLoaded;
            vm.SelectedLogPluginChanged += RefreshLog;
            Strings.CultureChanged += Refresh;
            // The built-ins' devices and settings are named in the language
            // in use.
            Strings.CultureChanged += BuiltIns.RefreshNames;

            // The status line, the restart pauses, Replay's place, the
            // files, the silent plugins and Reads per Second, once a second
            // while Plugins is on.
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Second(), ui);
            _timer.Stop();

            _catalog = CatalogItems;
            LiveCatalog.AddProvider(_catalog);
            Current = this;

            OnLoaded();
            Manager.SetEngine(host.EngineAttached);
            Apply();
        }

        // ── Following the settings, the engine and the plugins ──

        private void OnLoaded()
        {
            Manager.Load(_vm.PluginRecords);
            BuiltIns.LoadTestValues(_vm.TestControllerValues);
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

        private void OnTestValues()
        {
            if (_ui.CheckAccess()) _vm.SetTestControllerValues(BuiltIns.TestSettings.Save());
            else _ui.BeginInvoke(new Action(() => _vm.SetTestControllerValues(BuiltIns.TestSettings.Save())));
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

        /// <summary>The once-a-second look.</summary>
        internal void Second()
        {
            if (_disposed) return;
            Manager.CheckFiles();
            Manager.CheckHello();
            CheckSilence();
            BuiltIns.CountReads();
            Refresh();
        }

        /// <summary>
        /// Each plugin's session that holds devices or declared settings
        /// and has said nothing: after <see cref="OfflineAfterSilenceMs"/>
        /// its devices go offline at rest (the lease has rested them
        /// already), and after <see cref="EndAfterSilenceMs"/> the plugin is
        /// ended and started again with the back-off.
        /// </summary>
        internal void CheckSilence()
        {
            long now = _nowMs();
            foreach (var session in _host.Sessions.Snapshot())
            {
                if (!session.IsPlugin || session.Grant == null) continue;
                var run = Manager.RunOf(session.Grant);
                if (run == null) continue;
                var owned = _host.Sessions.Devices.OwnedBy(session);
                // A plugin with nothing to answer for is not watched. One
                // whose devices this look took offline still is.
                if (owned.Count == 0 && !run.Settings.HasDeclarations && !run.SilentOffline) continue;
                long silent = now - session.LastHeardMs;
                if (silent >= OfflineAfterSilenceMs && owned.Count > 0)
                {
                    foreach (var entry in owned) _host.Sessions.Devices.Gone(session, entry.Id);
                    run.SilentOffline = true;
                }
                if (silent >= EndAfterSilenceMs) Manager.EndSilent(run);
            }
        }

        /// <summary>
        /// The user removed a device's row on the Devices page: a plugin
        /// device's values go with it (#600 item 17). A row PadForge lets go
        /// of by itself, as it does for a device nobody assigned once it is
        /// offline, keeps them, since the plugin declares the device again
        /// at its next start.
        /// </summary>
        internal void DeviceRemoved(Guid instanceGuid)
        {
            foreach (var run in Manager.Snapshot())
                foreach (string key in run.Settings.Devices(withValues: true))
                    if (DeviceGuid(run, key) == instanceGuid)
                        run.Settings.DropDevice(key);
        }

        // ── A plugin's devices ──

        /// <summary>The name a plugin's session holds, which its devices'
        /// identity is made from: its own name, cut and cleaned as the hello
        /// cuts a program's name (AutomationSession), so a plugin whose
        /// file's name runs past 64 characters finds its devices.</summary>
        private static string SessionName(PluginRun run)
            => ProgramDeviceDeclaration.Clean(run.Identity, ProgramDeviceDeclaration.LongestName);

        /// <summary>Whether a device the registry keeps is one this plugin
        /// added.</summary>
        private static bool Added(ProgramDevices.Entry entry, PluginRun run)
            => entry.Kind == "plugin" && (string.Equals(entry.Program, SessionName(run), StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(entry.Program, run.Identity, StringComparison.OrdinalIgnoreCase));

        /// <summary>A plugin device's identity: the registry's while it keeps
        /// the device, else the one the plugin's session would make for the
        /// key, as its declaration makes it.</summary>
        internal Guid DeviceGuid(PluginRun run, string key)
        {
            foreach (var entry in _host.Sessions.Devices.All())
                if (Added(entry, run) && string.Equals(entry.Key, key, StringComparison.Ordinal)) return entry.Guid;
            return ProgramDeviceDeclaration.InstanceGuid("plugin", SessionName(run), key);
        }

        // ── The page ──

        /// <summary>Draws the cards, the status line, Replay's lines and the
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
                    var host = new PluginSettingsHost(run.Settings, null, "Plugins_" + LiveCatalog.ShortId(run.Path), _ui);
                    run.Settings.KeptChanged += OnSaved;
                    row = new PluginRowItem(run, ++_nextRow, this, host);
                    _rows[run] = row;
                    _vm.PluginRows.Insert(Math.Min(i, _vm.PluginRows.Count), row);
                }
                row.Show(run.ShownName, run.Path, StateLine(run), run.Enabled,
                    showStart: run.Status is PluginStatus.Failed or PluginStatus.Refused,
                    showApprove: run.Status == PluginStatus.Changed || run.FileChanged);
                // Trust New Versions From This Signer, offered while the
                // approved bytes' signing key is kept.
                row.ShowTrust(run.SignerKey != null && run.Signer != null, run.TrustSigner,
                    run.Signer == null ? null : string.Format(CultureInfo.CurrentCulture, s.Plugins_Trust_Note_Format, run.Signer));
                row.Devices = DeviceGuids(run);
                row.Settings.MissingLine = MissingLine(run);
            }
            _vm.PluginRowsChanged();

            // The status line: how many plugins run and how many devices they
            // added, the two built in among them, and each plugin that
            // stopped. Home's Plugins tile shows the same line.
            if (!_vm.EnablePlugins)
            {
                _vm.PluginsStatus = null;
                _vm.IsPluginsRunning = false;
            }
            else
            {
                int running = Manager.RunningCount + (BuiltIns.TestControllerLive ? 1 : 0) + (BuiltIns.ReplayDeviceCount > 0 ? 1 : 0);
                int devices = PluginDevices() + BuiltIns.TestControllerCount + BuiltIns.ReplayDeviceCount;
                string line = running == 0
                    ? s.Extras_Plugins_Status_None
                    : string.Format(CultureInfo.CurrentCulture, s.Plugins_Status_Format, running, devices);
                foreach (var run in runs)
                {
                    string problem = ProblemOf(run);
                    if (problem != null) line += " " + problem;
                }
                _vm.PluginsStatus = line;
                _vm.IsPluginsRunning = running > 0;
            }

            // The Test Controller's header line.
            _vm.TestControllerLine = !_vm.TestControllerOn ? s.Common_Off
                : !_vm.EnablePlugins || !BuiltIns.TestControllerLive ? s.Plugins_State_Waiting
                : BuiltIns.TestConfig.ByHand ? s.Plugins_TC_State_ByHand : s.Plugins_TC_State_Moving;

            // Replay.
            var file = BuiltIns.File;
            _vm.ReplayFileLine = file == null ? null
                : string.Format(CultureInfo.CurrentCulture, s.Plugins_Replay_File_Format, file.FileName, Clock(file.LengthMicros));
            _vm.ReplayPositionLine = file == null ? null
                : string.Format(CultureInfo.CurrentCulture, s.Plugins_Replay_Position_Format, Clock(BuiltIns.PositionMicros), Clock(file.LengthMicros));
            _vm.IsReplayPlaying = BuiltIns.Playing;

            RefreshLog();
        }

        /// <summary>A plugin that stopped, in a sentence the status line and
        /// Home's tile carry, or null while it runs or rests as it
        /// should.</summary>
        internal static string ProblemOf(PluginRun run)
        {
            var s = Strings.Instance;
            return run.Status switch
            {
                PluginStatus.Failed => string.Format(CultureInfo.CurrentCulture, s.Plugins_Problem_Failed_Format, run.ShownName),
                PluginStatus.Changed => string.Format(CultureInfo.CurrentCulture, s.Plugins_Problem_Changed_Format, run.ShownName),
                PluginStatus.Refused => string.Format(CultureInfo.CurrentCulture, s.Plugins_Problem_Refused_Format, run.ShownName),
                // Its last run never said hello: named from the first miss,
                // and while its next run waits for its hello.
                PluginStatus.Restarting or PluginStatus.Checking or PluginStatus.Running when run.NeverConnected && run.Refusal == PluginRefusal.None
                    => string.Format(CultureInfo.CurrentCulture, s.Plugins_Problem_NeverConnected_Format, run.ShownName),
                // It exited, or could not be started, while it should run,
                // and waits out its pause before the next start. A single
                // failure after a steady run is a one-second pause that its
                // row and log keep, so the line names it from the second
                // failure in a row.
                PluginStatus.Restarting when run.Failures >= 2 => string.Format(CultureInfo.CurrentCulture,
                    run.Refusal != PluginRefusal.None ? s.Plugins_Problem_Refused_Format : s.Plugins_Problem_Restarting_Format, run.ShownName),
                _ => null,
            };
        }

        /// <summary>The card's line: its state, the devices it added and
        /// its signer.</summary>
        internal string StateLine(PluginRun run)
        {
            var s = Strings.Instance;
            var parts = new List<string> { Sentence(StateOf(run)) };
            if (run.FileChanged) parts.Add(Sentence(s.Plugins_State_Changed));
            var names = DeviceNames(run);
            if (names.Count > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, s.Plugins_Row_Added_Format, string.Join(", ", names)));
            parts.Add(run.Signer != null ? string.Format(CultureInfo.CurrentCulture, s.Plugins_Row_Signed_Format, run.Signer) : s.Plugins_Row_NotSigned);
            return string.Join(" ", parts);
        }

        private static string Sentence(string text)
            => string.IsNullOrEmpty(text) || text.EndsWith('.') ? text : text + ".";

        /// <summary>The names of the devices a plugin added and PadForge
        /// keeps now, live or waiting.</summary>
        internal List<string> DeviceNames(PluginRun run)
        {
            var names = new List<string>();
            foreach (var entry in _host.Sessions.Devices.All())
                if (Added(entry, run))
                    names.Add(entry.Device.Name);
            return names;
        }

        /// <summary>The identities of the devices a plugin added and PadForge
        /// keeps now, in the order its card's line names them.</summary>
        private List<Guid> DeviceGuids(PluginRun run)
        {
            var guids = new List<Guid>();
            foreach (var entry in _host.Sessions.Devices.All())
                if (Added(entry, run))
                    guids.Add(entry.Guid);
            return guids;
        }

        /// <summary>Why a plugin's card shows no settings, or null.</summary>
        internal static string MissingLine(PluginRun run)
        {
            var s = Strings.Instance;
            if (run.Settings.Refused is { } why)
                return string.Format(CultureInfo.CurrentCulture, s.Plugins_Missing_Refused_Format, why);
            var mine = run.Settings.Declarations().FindAll(d => d.Device == null);
            if (mine.Count > 0) return null;
            if (run.Status != PluginStatus.Running) return s.Plugins_Missing_NotRunning;
            return run.Grant != null ? s.Plugins_Missing_None : s.Plugins_Missing_NotConnected;
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
                    : run.NeverConnected
                    ? string.Format(CultureInfo.CurrentCulture, Seconds(run.PauseMs) == 1 ? s.Plugins_State_NeverConnected_One_Format : s.Plugins_State_NeverConnected_Format, Seconds(run.PauseMs))
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
                PluginLogKind.Silent => s.Plugins_Log_Silent,
                PluginLogKind.ChangedWhileRunning => s.Plugins_Log_ChangedWhileRunning,
                PluginLogKind.NeverConnected => s.Plugins_Log_NeverConnected,
                PluginLogKind.TrustedVersion => s.Plugins_Log_TrustedVersion,
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

        // ── The device panel's plugin card ──

        /// <summary>
        /// The card of a device a plugin added, or a built-in's: which plugin
        /// added it, and its own settings and help. Null for any other
        /// device. Made each time the panel shows a device.
        /// </summary>
        internal PluginDeviceCard CardFor(Guid instanceGuid)
        {
            if (instanceGuid == Guid.Empty) return null;
            var s = Strings.Instance;
            for (int i = 0; i < TestControllerPattern.MostControllers; i++)
                if (BuiltInPlugins.TestControllerGuidOf(i) == instanceGuid)
                    return new PluginDeviceCard(s.Plugins_TestController, null);
            foreach (var run in Manager.Snapshot())
            {
                foreach (string device in DeviceKeys(run))
                {
                    if (DeviceGuid(run, device) != instanceGuid) continue;
                    var host = new PluginSettingsHost(run.Settings, device, "Device_Plugin_" + device, _ui, instanceGuid);
                    return new PluginDeviceCard(run.ShownName, host);
                }
            }
            // A device of a plugin whose run declared nothing for it: the
            // key is read from the registry's entry.
            foreach (var entry in _host.Sessions.Devices.All())
            {
                if (entry.Guid != instanceGuid || entry.Kind != "plugin") continue;
                foreach (var run in Manager.Snapshot())
                    if (Added(entry, run))
                        return new PluginDeviceCard(run.ShownName, new PluginSettingsHost(run.Settings, entry.Key, "Device_Plugin_" + entry.Key, _ui, instanceGuid));
            }
            return null;
        }

        private IEnumerable<string> DeviceKeys(PluginRun run)
        {
            var keys = run.Settings.Devices();
            foreach (var entry in _host.Sessions.Devices.All())
                if (Added(entry, run))
                    keys.Add(entry.Key);
            return keys;
        }

        // ── The catalog ──

        /// <summary>
        /// Every plugin's declarations, and the Test Controller's, as live
        /// items (#600 items 1 and 17): <c>plugin/&lt;id&gt;/&lt;key&gt;</c>
        /// for a plugin's own, and <c>device/&lt;short ID&gt;/&lt;key&gt;</c>
        /// for a device's, under the ID list_devices gives the device, so
        /// a device's settings read, set and press where the device does. A
        /// plugin's declarations are its own, which a program at Change
        /// Settings sets and presses as the user does, each with its help,
        /// and a help declaration is a paragraph. The plugin's switch,
        /// Remove, Start and Approve are the page's, and the user's alone.
        /// </summary>
        internal IReadOnlyList<LiveItem> CatalogItems()
        {
            var items = new List<LiveItem>();
            var s = Strings.Instance;
            // The Test Controller's controllers are built in and declare no
            // settings of their own.
            AddItems(items, TestControllerId, s.Plugins_TestController, BuiltIns.TestSettings, null);
            foreach (var run in Manager.Snapshot())
            {
                var plugin = run;
                AddItems(items, "plugin/" + LiveCatalog.ShortId(run.Path), run.ShownName, run.Settings,
                    key => "device/" + AutomationDevices.IdOf(DeviceGuid(plugin, key)));
            }
            return items;
        }

        private static void AddItems(List<LiveItem> items, string id, string plugin, PluginSettingSet set, Func<string, string> deviceIdOf)
        {
            var s = Strings.Instance;
            foreach (var d in set.Declarations())
            {
                string device = d.Device, key = d.Key;
                string owner = id, where = plugin;
                if (device != null)
                {
                    owner = deviceIdOf?.Invoke(device);
                    if (owner == null) continue;
                    where = s.Devices_Title + s.Search_WhereSeparator + string.Format(CultureInfo.CurrentCulture, s.Plugins_Device_Settings_Format, plugin);
                }
                bool number = d.Kind == PluginSettingKind.Number;
                var item = new LiveItem(owner + "/" + key, LiveKind.PluginDeclaration, d.Name.Length > 0 ? d.Name : d.Help, ReachClass.Free)
                {
                    Where = where,
                    Help = d.Help.Length > 0 ? d.Help : null,
                    Paragraph = d.Kind == PluginSettingKind.Help,
                    Takes = d.Kind switch
                    {
                        PluginSettingKind.Switch => "switch",
                        PluginSettingKind.Number => "number",
                        PluginSettingKind.Choice => "choice",
                        PluginSettingKind.Text or PluginSettingKind.Pad or PluginSettingKind.Input => "text",
                        _ => null,
                    },
                    Minimum = number ? d.Min : null,
                    Maximum = number ? d.Max : null,
                    Unit = d.Unit.Length > 0 ? d.Unit : null,
                    Choices = d.Kind == PluginSettingKind.Choice ? () => ChoiceWords(set, device, key) : null,
                    Read = d.Kind is PluginSettingKind.Button or PluginSettingKind.Help ? null : () => Shown(set, device, key),
                    Write = d.HoldsValue ? value =>
                    {
                        string error = set.Set(device, key, value);
                        if (error != null) throw new ArgumentException(error);
                    } : null,
                    Press = d.Kind == PluginSettingKind.Button ? () => set.Press(device, key) : null,
                };
                items.Add(item);
            }
        }

        /// <summary>A declared value as its row shows it: a choice by its
        /// entry's words, every other kind in its kept form.</summary>
        private static string Shown(PluginSettingSet set, string device, string key)
        {
            string value = set.ValueOf(device, key);
            if (set.Find(device, key) is { Kind: PluginSettingKind.Choice } d)
                foreach (var c in d.Choices)
                    if (string.Equals(c.Value, value, StringComparison.Ordinal)) return c.Words;
            return value;
        }

        /// <summary>A choice's entries as its list words them now: a plugin
        /// may keep the list current.</summary>
        private static IReadOnlyList<string> ChoiceWords(PluginSettingSet set, string device, string key)
        {
            var words = new List<string>();
            if (set.Find(device, key) is { } d)
                foreach (var c in d.Choices) words.Add(c.Words);
            return words;
        }

        // ── What the page asks ──

        public PluginApproval Describe(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch { return null; }
            // The key is read on the handle that was hashed, so it is the key
            // of the bytes the user approves, as the confirmation reads the
            // signer: with no revocation check and nothing fetched.
            string sha, key;
            using (var held = PluginFile.OpenHeld(full, out sha))
                key = PluginFile.KeyOf(full, held);
            return sha == null ? null : new PluginApproval(full, AuthenticodeSigner.Of(full), sha, key);
        }

        public bool Has(string path) => Manager.Has(path);

        public bool Add(PluginApproval approved) => approved != null && Manager.Add(approved.Path, approved.Sha256, approved.Signer, approved.Key) != null;

        public void Remove(PluginRowItem row)
        {
            if (row?.Run is PluginRun run)
            {
                run.Settings.KeptChanged -= OnSaved;
                Manager.Remove(run);
            }
        }

        public void SetEnabled(PluginRowItem row, bool on)
        {
            if (row?.Run is PluginRun run) Manager.SetEnabled(run, on);
        }

        public void Approve(PluginRowItem row, PluginApproval approved)
        {
            if (row?.Run is PluginRun run && approved != null) Manager.Approve(run, approved.Sha256, approved.Signer, approved.Key);
        }

        public void SetTrust(PluginRowItem row, bool on)
        {
            if (row?.Run is PluginRun run) Manager.SetTrust(run, on);
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
            LiveCatalog.RemoveProvider(_catalog);
            if (ReferenceEquals(Current, this)) Current = null;
            _host.EngineChanged -= OnEngine;
            if (_host.AcceptPluginToken == Manager.AcceptToken) _host.AcceptPluginToken = null;
            _vm.PropertyChanged -= OnVm;
            _vm.PluginSettingsLoaded -= OnLoaded;
            _vm.SelectedLogPluginChanged -= RefreshLog;
            BuiltIns.TestSettings.KeptChanged -= OnTestValues;
            BuiltIns.TestSettings.Changed -= Queue;
            Strings.CultureChanged -= Refresh;
            Strings.CultureChanged -= BuiltIns.RefreshNames;
            if (ReferenceEquals(_vm.PluginActions, this)) _vm.PluginActions = null;
            if (ReferenceEquals(_vm.TestControllerSettings, TestHost)) _vm.TestControllerSettings = null;
            Manager.Dispose();
            BuiltIns.Dispose();
        }
    }

    /// <summary>
    /// The device panel's card for a device a plugin added (#600 items 17
    /// and 18): the plugin that added it, its help, and its own settings
    /// with Reset All. A built-in's device has no settings of its own.
    /// </summary>
    public sealed class PluginDeviceCard
    {
        internal PluginDeviceCard(string plugin, PluginSettingsHost settings)
        {
            Plugin = plugin;
            Settings = settings;
            var s = Strings.Instance;
            AddedBy = string.Format(CultureInfo.CurrentCulture, s.Plugins_Device_AddedBy_Format, plugin);
            Title = string.Format(CultureInfo.CurrentCulture, s.Plugins_Device_Settings_Format, plugin);
        }

        public string Plugin { get; }
        public string AddedBy { get; }
        public string Title { get; }
        public PluginSettingsHost Settings { get; }
        public bool HasSettings => Settings != null && Settings.HasRows;
    }

    /// <summary>The device panel's way to its plugin card: the selected
    /// device's identity in, the card or null out.</summary>
    public sealed class PluginDeviceCardConverter : System.Windows.Data.IValueConverter
    {
        public static readonly PluginDeviceCardConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Guid guid ? PluginsController.Current?.CardFor(guid) : null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
