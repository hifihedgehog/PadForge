using System;
using System.Runtime.CompilerServices;
using PadForge.Services;

namespace PadForge
{
    /// <summary>The process's entry. It is one of four things, decided here
    /// before any of WPF loads:
    /// <list type="number">
    /// <item>A give-back task's run, which is none of the app and at a boot
    /// runs in a session with no desktop (<see cref="GiveBackRun"/>).</item>
    /// <item>A client: a start that hands its switches to the running PadForge
    /// or serves an assistant, with its caller's rights
    /// (<see cref="ClientRun"/>).</item>
    /// <item>The app's start without the rights it needs, which starts the
    /// exe once more with rights through Windows' prompt
    /// (<see cref="StartWithRights"/>).</item>
    /// <item>The app. The rest is what WPF's generated entry does.</item>
    /// </list></summary>
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (GiveBackRun.IsRun(args))
            {
                Environment.ExitCode = GiveBackRun.Run(args);
                return;
            }
            if (ClientRun.TryRun(args, out int clientExit))
            {
                Environment.ExitCode = clientExit;
                return;
            }
            if (!Environment.IsPrivilegedProcess)
            {
                // An update's helper is replacing this exe and starts PadForge
                // again itself. This copy leaves before it asks for rights:
                // while Windows' prompt stood, it would hold the very file
                // being replaced. The app's own start takes the same way out
                // (App.OnStartup).
                if (UpdateService.IsUpdateInProgress(Environment.ProcessPath)) return;
                Environment.ExitCode = StartWithRights.Run(args);
                return;
            }
            RunTheApp();
        }

        // Apart, so the checks above load none of WPF's assemblies.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunTheApp()
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
