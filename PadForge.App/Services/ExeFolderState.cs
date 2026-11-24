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
    /// shared folder's line and the move on their rows, and Settings shows
    /// where PadForge runs from.
    /// </summary>
    public sealed class ExeFolderState : ObservableObject
    {
        public static ExeFolderState Instance { get; } = new();

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
                if (SetProperty(ref _exePath, value ?? string.Empty)) OnPropertyChanged(nameof(RunsFromText));
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
            IsShared = ExeFolder.Check(exe) != ExeFolder.Standing.Own;
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
