using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Common;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// What the interface says about the folder PadForge runs from (#600):
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

        /// <summary>The lines this state formats in code follow a language
        /// change (Strings.CultureChanged, which holds this weakly).</summary>
        public ExeFolderState()
        {
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged()
        {
            OnPropertyChanged(nameof(RunsFromText));
            OnPropertyChanged(nameof(HomeCardText));
            OnPropertyChanged(nameof(StartAtLoginTurnedOffText));
            if (_moveResult != null)
            {
                _moveError = ErrorFor(_moveResult);
                OnPropertyChanged(nameof(MoveError));
            }
        }

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
        private ExeFolder.Standing _standing = ExeFolder.Standing.Own;
        private bool _isShared;
        private bool _startAtLoginTurnedOff;
        private bool _moving;
        private string _moveError;
        private ExeFolder.MoveResult _moveResult;

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

        /// <summary>Whether the folder holds other files, could not be
        /// protected or could not be read, so no task with rights may run
        /// PadForge from it.</summary>
        public bool IsShared
        {
            get => _isShared;
            private set
            {
                if (SetProperty(ref _isShared, value)) OnPropertyChanged(nameof(CanMove));
            }
        }

        /// <summary>The folder's last judgment. Each line the interface shows
        /// for a folder that is not safe states only what this one read: a
        /// folder that holds other files, or one PadForge could not keep
        /// other programs from changing.</summary>
        private ExeFolder.Standing Standing
        {
            get => _standing;
            set
            {
                if (_standing == value) return;
                _standing = value;
                IsShared = value != ExeFolder.Standing.Own;
                OnPropertyChanged(nameof(HomeCardText));
                OnPropertyChanged(nameof(HoldsOtherFiles));
                OnPropertyChanged(nameof(CouldNotProtect));
            }
        }

        /// <summary>Whether the folder holds files that are not
        /// PadForge's.</summary>
        public bool HoldsOtherFiles => _standing == ExeFolder.Standing.Shared;

        /// <summary>Whether the folder is PadForge's own but its access list
        /// could not be made sound, or the folder could not be read.</summary>
        public bool CouldNotProtect => _standing is ExeFolder.Standing.Unprotected or ExeFolder.Standing.Unread;

        /// <summary>Home's card while the folder is not safe, in the words
        /// of what its judgment read: a folder that holds other files, one of
        /// PadForge's own that other accounts may change, or one PadForge
        /// could not read.</summary>
        public string HomeCardText
            => string.Format(_standing switch
            {
                ExeFolder.Standing.Shared => Strings.Instance.Home_SharedFolderOthers_Format,
                ExeFolder.Standing.Unread => Strings.Instance.Home_SharedFolderUnread_Format,
                _ => Strings.Instance.Home_SharedFolder_Format,
            }, System.IO.Path.GetDirectoryName(ExePath) ?? ExePath);

        /// <summary>Whether the move's button can be pressed now.</summary>
        public bool CanMove => _isShared && !_moving;

        /// <summary>Whether this start removed the logon task
        /// (<see cref="StartupHelper.AuditTask"/>).</summary>
        public bool StartAtLoginTurnedOff
        {
            get => _startAtLoginTurnedOff;
            private set => SetProperty(ref _startAtLoginTurnedOff, value);
        }

        private StartupHelper.TaskRemoval _removal;

        /// <summary>Settings' line under Start at Login once this start
        /// removed the logon task: why it did.</summary>
        public string StartAtLoginTurnedOffText => LineFor(_removal);

        /// <summary>The line for a removal's cause.</summary>
        internal static string LineFor(StartupHelper.TaskRemoval removal) => removal switch
        {
            StartupHelper.TaskRemoval.ExeGone => Strings.Instance.ExeFolder_StartAtLoginTurnedOff_ExeGone,
            StartupHelper.TaskRemoval.Unprotected => Strings.Instance.ExeFolder_StartAtLoginTurnedOff_Unprotected,
            StartupHelper.TaskRemoval.Unreadable => Strings.Instance.ExeFolder_StartAtLoginTurnedOff_Unreadable,
            StartupHelper.TaskRemoval.FolderUnread => Strings.Instance.ExeFolder_StartAtLoginTurnedOff_FolderUnread,
            _ => Strings.Instance.ExeFolder_StartAtLoginTurnedOff,
        };

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
            // The move copies the bytes that run, read from this hold.
            ExeFolder.HoldRunningImage();
            ExePath = exe;
            Standing = Judge(exe);
            if (IsShared && CheckSeam == null) Watch(exe);
            var dispatcher = Application.Current?.Dispatcher;
            Task.Run(() =>
            {
                var removed = StartupHelper.TaskRemoval.None;
                try { removed = StartupHelper.AuditTask(); }
                catch { }
                try { GiveBackTask.AuditTasks(line => PadForge.Engine.SdlDiagLog.WriteLine("GIVEBACK " + line)); }
                catch { }
                if (removed != StartupHelper.TaskRemoval.None)
                    dispatcher?.BeginInvoke(new Action(() =>
                    {
                        _removal = removed;
                        OnPropertyChanged(nameof(StartAtLoginTurnedOffText));
                        StartAtLoginTurnedOff = true;
                    }));
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
        /// ends. One that is still not safe keeps the line of its new
        /// judgment.</summary>
        internal void Recheck()
        {
            string exe = ExePath;
            if (exe.Length == 0 || !IsShared) return;
            ExeFolder.Standing standing;
            try { standing = Judge(exe); }
            catch { return; }
            if (standing == ExeFolder.Standing.Own) StopWatching();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Standing = standing;
            else dispatcher.BeginInvoke(new Action(() => Standing = standing));
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

        /// <summary>A move that did not happen: its line, which follows a
        /// language change.</summary>
        internal void MoveFailed(ExeFolder.MoveResult result)
        {
            _moveResult = result;
            MoveError = ErrorFor(result);
        }

        /// <summary>One whole sentence for each reason a move did not
        /// happen.</summary>
        internal static string ErrorFor(ExeFolder.MoveResult result)
        {
            if (result == null) return null;
            var s = Strings.Instance;
            return result.Why switch
            {
                ExeFolder.MoveRefusal.NoProgramFiles => s.ExeFolder_NoProgramFiles,
                ExeFolder.MoveRefusal.NoImage => s.ExeFolder_NoImage,
                ExeFolder.MoveRefusal.NotMade => string.Format(s.ExeFolder_NotMade_Format, result.Error),
                ExeFolder.MoveRefusal.Unreadable => string.Format(s.ExeFolder_Unreadable_Format, result.Error),
                ExeFolder.MoveRefusal.TargetTaken => string.Format(s.ExeFolder_TargetTaken_Format, result.Error),
                ExeFolder.MoveRefusal.NotProtected => string.Format(s.ExeFolder_NotProtected_Format, result.Error),
                ExeFolder.MoveRefusal.CopyDiffers => string.Format(s.ExeFolder_CopyDiffers_Format, result.Error),
                ExeFolder.MoveRefusal.NotSound => string.Format(s.ExeFolder_NotSound_Format, result.Error),
                _ => string.Format(s.ExeFolder_MoveFailed_Format, result.Error),
            };
        }
    }
}
