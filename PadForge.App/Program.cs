using System;
using System.Runtime.CompilerServices;
using PadForge.Services;

namespace PadForge
{
    /// <summary>The process's entry. A give-back task's run is none of the app,
    /// and at a boot it runs in a session with no desktop
    /// (<see cref="GiveBackRun"/>), so it never reaches WPF. The rest is what
    /// WPF's generated entry does.</summary>
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
            RunTheApp();
        }

        // Apart, so the check above loads none of WPF's assemblies.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunTheApp()
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
