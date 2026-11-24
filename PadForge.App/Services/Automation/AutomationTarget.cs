using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using PadForge.Engine;

namespace PadForge.Services.Automation
{
    /// <summary>How a request to start or stop the engine ended.</summary>
    internal enum EngineChange
    {
        /// <summary>The engine is on its way to the state asked for, or
        /// already in it.</summary>
        Done,

        /// <summary>A stop is still under way, and a start has to wait for
        /// it.</summary>
        Busy,

        /// <summary>The engine was started and did not come up. The message
        /// line in the window says why.</summary>
        Failed,
    }

    /// <summary>The engine as the state line names it.</summary>
    internal enum AutomationEngineState
    {
        Stopped,
        Stopping,

        /// <summary>Running with no virtual controller that has a connected
        /// device, so it rests between looks.</summary>
        Idle,
        Running,
    }

    /// <summary>Where the main window is.</summary>
    internal enum AutomationWindowState
    {
        Shown,
        Minimized,

        /// <summary>Hidden, with the tray icon as the way back.</summary>
        Tray,
    }

    /// <summary>One created virtual controller, for the state line.</summary>
    /// <param name="Slot">The number the interface shows it by.</param>
    /// <param name="Name">Its preset as its page names it.</param>
    /// <param name="Type">Its type.</param>
    /// <param name="On">Whether it is turned on.</param>
    /// <param name="Devices">How many devices are assigned to it.</param>
    /// <param name="Online">How many of those are connected.</param>
    internal sealed record AutomationControllerState(
        int Slot, string Name, VirtualControllerType Type, bool On, int Devices, int Online);

    /// <summary>What the state line says of the app, read in one go on the
    /// interface's thread.</summary>
    internal sealed class AutomationState
    {
        public AutomationEngineState Engine { get; init; }

        /// <summary>The active profile's name and ID, both null for the
        /// default profile.</summary>
        public string ProfileName { get; init; }
        public string ProfileId { get; init; }

        /// <summary>Whether a program activated the profile, which holds it
        /// against switching by game.</summary>
        public bool ProfilePinned { get; init; }

        public AutomationWindowState Window { get; init; }

        /// <summary>The created virtual controllers in the interface's
        /// order.</summary>
        public IReadOnlyList<AutomationControllerState> Controllers { get; init; } = Array.Empty<AutomationControllerState>();
    }

    /// <summary>
    /// What the command set acts on (#597): PadForge as the interface drives
    /// it. The main window is the one that implements it for the app, and a
    /// test stands a recorder in its place. Every member is called on the
    /// interface's thread, and each does what the control named in its
    /// summary does, through the same code.
    /// </summary>
    internal interface IAutomationTarget
    {
        /// <summary>Switches to a profile by ID or by name, without regard
        /// to case, and holds it. Answers the pipe's own words, which are a
        /// contract since #366: <c>ok &lt;name&gt;</c> or
        /// <c>error unknown-profile</c>.</summary>
        string ActivateProfile(string nameOrId);

        /// <summary>Releases the hold and returns to the default profile.
        /// Answers <c>ok default</c>.</summary>
        string DeactivateProfile();

        /// <summary>Answers <c>ok &lt;name or default&gt; pinned</c> or
        /// <c>unpinned</c>.</summary>
        string QueryProfile();

        /// <summary>Shows the main window and brings it to the front, as the
        /// tray icon's Show does.</summary>
        void ShowWindow();

        /// <summary>Sends the window to the tray with the tray icon up, as
        /// Close to System Tray does. False when there is no tray icon to
        /// come back through, and the window stays.</summary>
        bool HideToTray();

        /// <summary>Minimizes the window, as its own button does.</summary>
        void MinimizeWindow();

        /// <summary>Closes PadForge, as the tray icon's Exit does.</summary>
        void Exit();

        /// <summary>Starts or stops the engine, as Home's button does. A
        /// stop is begun and not waited for.</summary>
        EngineChange SetEngine(bool on);

        /// <summary>Turns every created virtual controller on or off.</summary>
        void SetAllControllers(bool on);

        /// <summary>Turns one virtual controller on or off, by the number
        /// the interface shows. False when no controller has that
        /// number.</summary>
        bool SetController(int number, bool on);

        /// <summary>Saves a diagnostics snapshot, as Save Snapshot does, and
        /// gives the file's name without its folder.</summary>
        string SaveSnapshot();

        /// <summary>Reads what the state line says, from the engine and the
        /// settings and never from what a hidden window stopped
        /// drawing.</summary>
        AutomationState ReadState();
    }

    /// <summary>The interface's thread, as the command set reaches it.</summary>
    internal interface IAutomationUi
    {
        /// <summary>Runs <paramref name="work"/> on the interface's thread
        /// and gives its answer. A caller already on that thread runs it in
        /// place. Canceling <paramref name="stopping"/> ends the wait, and
        /// work that has not begun is never run.</summary>
        string Invoke(Func<string> work, CancellationToken stopping);

        /// <summary>Queues <paramref name="work"/> for the interface's
        /// thread and returns at once.</summary>
        void Post(Action work);
    }

    /// <summary>The app's interface thread: a WPF dispatcher.</summary>
    internal sealed class DispatcherAutomationUi : IAutomationUi
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherAutomationUi(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public string Invoke(Func<string> work, CancellationToken stopping)
        {
            if (_dispatcher.CheckAccess()) return work();
            // Queued and waited for with the stop signal in hand. The pipe's
            // Stop runs on this very thread and waits for the command in
            // flight, so a plain blocking call here would hold both until
            // that wait gave up.
            DispatcherOperation<string> queued = _dispatcher.InvokeAsync(work);
            try
            {
                queued.Task.Wait(stopping);
            }
            catch (OperationCanceledException)
            {
                queued.Abort();
                throw;
            }
            catch (AggregateException ex) when (ex.InnerException != null)
            {
                // The work's own failure, as a direct call would give it.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
            return queued.Result;
        }

        public void Post(Action work) => _dispatcher.BeginInvoke(work);
    }
}
