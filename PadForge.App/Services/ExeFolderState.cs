using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Common;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// What the interface says about the folder PadForge runs from (#597):
    /// where it runs from, whether that folder is shared, and the move to a
    /// folder of its own. Start at Login and Use Only in PadForge show the
    /// shared folder's line and the move on their rows, Settings shows where
    /// PadForge runs from, and Home shows a card while the folder is shared
    /// (<see cref="HomeCardText"/>).
    ///
    /// <para>A shared folder is watched: once the files that are not
    /// PadForge's leave it, it is judged again, made PadForge's own
    /// (<see cref="ExeFolder.Check"/>), and the card goes.</para>
    /// </summary>
    public sealed class ExeFolderState : ObservableObject
    {
        public static ExeFolderState Instance { get; } = new();

        /// <summary>Test seam: the judgment of a folder in place of
        /// <see cref="ExeFolder.Check"/>. Null in production.</summary>
        internal static Func<string, ExeFolder.Standing> CheckSeam;

        /// <summary>How long the folder stays quiet before it is judged
        /// again.</summary>
        internal const int RecheckQuietMs = 2000;

        private System.IO.FileSystemWatcher _watcher;
        private System.Threading.Timer _recheck;
        private readonly object _watchSync = new();

        private string _exePath = string.Empty;
        private bool _isShared;
        private bool _startAtLoginTurnedOff;
        private bool _moving;
        private string _moveError;

        /// <summary>Asked for by the move's button. MainWindow saves the
        /// settings, copies, points the tasks at the copy and closes.</summary>
        public event EventHandler MoveRequested;

        /// <summary>The exe this copy runs as.</summary>
        public string ExePath
        {
            get => _exePath;
            private set
            {
                if (SetProperty(ref _exePath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(RunsFromText));
                    OnPropertyChanged(nameof(HomeCardText));
                }
            }
        }

        /// <summary>"PadForge runs from …", shown in Settings.</summary>
        public string RunsFromText => ExePath.Length == 0 ? string.Empty : string.Format(Strings.Instance.ExeFolder_RunsFrom_Format, ExePath);

        /// <summary>Whether the folder holds other files, or could not be
        /// protected, so no task with rights may run PadForge from
        /// it.</summary>
        public bool IsShared
        {
            get => _isShared;
            private set
            {
                if (SetProperty(ref _isShared, value)) OnPropertyChanged(nameof(CanMove));
            }
        }

        /// <summary>Home's card while the folder is shared: PadForge runs
        /// with administrator rights from a folder other programs can change,
        /// so they can change its settings there.</summary>
        public string HomeCardText
            => string.Format(Strings.Instance.Home_SharedFolder_Format, System.IO.Path.GetDirectoryName(ExePath) ?? ExePath);

        /// <summary>Whether the move's button can be pressed now.</summary>
        public bool CanMove => _isShared && !_moving;

        /// <summary>Whether this start removed the logon task, because the
        /// exe it ran sat in a shared folder.</summary>
        public bool StartAtLoginTurnedOff
        {
            get => _startAtLoginTurnedOff;
            private set => SetProperty(ref _startAtLoginTurnedOff, value);
        }

        /// <summary>Why the last move did not happen, or null.</summary>
        public string MoveError
        {
            get => _moveError;
            private set
            {
                if (SetProperty(ref _moveError, value)) OnPropertyChanged(nameof(HasMoveError));
            }
        }

        public bool HasMoveError => !string.IsNullOrEmpty(_moveError);

        /// <summary>A start with rights: judges and protects the exe's
        /// folder at once, then looks at the tasks PadForge registered, off
        /// the start's path.</summary>
        internal void Start(string exe)
        {
            ExePath = exe;
            IsShared = Judge(exe) != ExeFolder.Standing.Own;
            if (IsShared && CheckSeam == null) Watch(exe);
            var dispatcher = Application.Current?.Dispatcher;
            Task.Run(() =>
            {
                bool removed = false;
                try { removed = StartupHelper.AuditTask(); }
                catch { }
                try { GiveBackTask.AuditTasks(line => PadForge.Engine.SdlDiagLog.WriteLine("GIVEBACK " + line)); }
                catch { }
                if (removed) dispatcher?.BeginInvoke(new Action(() => StartAtLoginTurnedOff = true));
            });
        }

        private static ExeFolder.Standing Judge(string exe) => CheckSeam != null ? CheckSeam(exe) : ExeFolder.Check(exe);

        /// <summary>Watches the exe's folder for files that come and go, and
        /// judges it again once it has been quiet for
        /// <see cref="RecheckQuietMs"/>.</summary>
        private void Watch(string exe)
        {
            string folder = System.IO.Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(folder)) return;
            lock (_watchSync)
            {
                if (_watcher != null) return;
                try
                {
                    _recheck = new System.Threading.Timer(_ => Recheck(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    var watcher = new System.IO.FileSystemWatcher(folder)
                    {
                        NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName,
                        IncludeSubdirectories = false,
                    };
                    void Soon(object sender, System.IO.FileSystemEventArgs e) => _recheck?.Change(RecheckQuietMs, System.Threading.Timeout.Infinite);
                    watcher.Created += Soon;
                    watcher.Deleted += Soon;
                    watcher.Renamed += (s, e) => Soon(s, e);
                    watcher.EnableRaisingEvents = true;
                    _watcher = watcher;
                }
                catch
                {
                    StopWatching();
                }
            }
        }

        private void StopWatching()
        {
            lock (_watchSync)
            {
                _watcher?.Dispose();
                _watcher = null;
                _recheck?.Dispose();
                _recheck = null;
            }
        }

        /// <summary>Judges the folder again. One that has become PadForge's
        /// own is protected by the judgment, its card goes, and the watch
        /// ends.</summary>
        internal void Recheck()
        {
            string exe = ExePath;
            if (exe.Length == 0 || !IsShared) return;
            bool shared;
            try { shared = Judge(exe) != ExeFolder.Standing.Own; }
            catch { return; }
            if (shared) return;
            StopWatching();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) IsShared = false;
            else dispatcher.BeginInvoke(new Action(() => IsShared = false));
        }

        /// <summary>The move's button.</summary>
        public void RequestMove()
        {
            if (!CanMove) return;
            MoveRequested?.Invoke(this, EventArgs.Empty);
        }

        internal void Moving(bool moving)
        {
            _moving = moving;
            OnPropertyChanged(nameof(CanMove));
        }

        internal void MoveFailed(ExeFolder.MoveResult result)
            => MoveError = string.Format(result.TargetTaken ? Strings.Instance.ExeFolder_TargetTaken_Format : Strings.Instance.ExeFolder_MoveFailed_Format,
                result.Error);
    }
}
