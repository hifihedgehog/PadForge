using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using PadForge.Common.Input;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>
    /// Creates one virtual controller of each family through PadForge's own
    /// code path, submits a known state the way the poll loop does, and reads
    /// what Windows delivers.
    ///
    ///   OutputFamilyProbe --list
    ///       each family, its profile, its lane and how it is read. No rights.
    ///   OutputFamilyProbe --self-check
    ///       every family's comparison on what PadForge's own code makes for the
    ///       known state, with no device. No rights.
    ///   OutputFamilyProbe --create-devices [--out DIR] [--only NAME,...] [--skip-usbip]
    ///       the run. Needs administrator rights and no PadForge running from
    ///       C:\PadForge\PadForge.exe.
    ///
    /// Writes nothing anywhere unless --out names a folder, and then only
    /// OutputFamilyProbe.txt in it. Exit code 0 only when every check
    /// passed.
    /// </summary>
    internal static class Program
    {
        private const string PadForgeExe = @"C:\PadForge\PadForge.exe";

        private static int Main(string[] args)
        {
            // The diagnostic logs of HIDMaestro and PadForge write files of
            // their own when these are set, so the probe clears them for its
            // own process before either library reads them.
            foreach (var v in new[] { "HIDMAESTRO_DIAG", "HIDMAESTRO_DIAG_READS", "HIDMAESTRO_TIMING", "PADFORGE_DIAG" })
                Environment.SetEnvironmentVariable(v, null);

            bool list = args.Contains("--list");
            bool self = args.Contains("--self-check");
            bool create = args.Contains("--create-devices");
            bool skipUsbip = args.Contains("--skip-usbip");
            string outDir = ValueOf(args, "--out");
            string only = ValueOf(args, "--only");
            var known = new HashSet<string> { "--list", "--self-check", "--create-devices", "--skip-usbip", "--out", "--only" };
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "--out" or "--only") { i++; continue; }
                if (!known.Contains(args[i]))
                {
                    Console.Error.WriteLine($"Unknown argument {args[i]}.");
                    return 2;
                }
            }
            if (outDir != null && !Directory.Exists(outDir))
            {
                Console.Error.WriteLine($"--out names {outDir}, which is not an existing folder.");
                return 2;
            }

            var log = new StringBuilder();
            void Out(string line)
            {
                Console.WriteLine(line);
                log.AppendLine(line);
            }

            int code;
            if (list) code = List(Out);
            else if (self) code = SelfCheck.Run(Out) == 0 ? 0 : 1;
            else
            {
                // Device mode, behind every guard. A plain run lands here too
                // and refuses with the reason.
                code = Devices(create, skipUsbip, only, Out);
            }

            if (outDir != null)
                File.WriteAllText(Path.Combine(outDir, "OutputFamilyProbe.txt"), log.ToString());
            return code;
        }

        private static string ValueOf(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static int List(Action<string> log)
        {
            foreach (var f in Families.All())
            {
                string layout = f.Type is PadForge.Engine.VirtualControllerType.Extended or PadForge.Engine.VirtualControllerType.Nintendo
                    ? $" layout {f.Layout.Sticks} sticks, {f.Layout.Triggers} triggers, {f.Layout.Povs} hats, {f.Layout.Buttons} buttons{(f.Customize ? ", customized" : "")};"
                    : string.Empty;
                log($"{f.Name}: {f.Type} profile {f.ProfileId} ({f.Profile?.Name}){(f.Usbip ? ", USB/IP" : "")};{layout} lane {f.Lane}; read: {f.ReadHow}");
            }
            foreach (var f in Families.NotRead())
                log($"{f.Name}: {f.Type}; lane {f.Lane}; read: {f.ReadHow}");
            return 0;
        }

        /// <summary>The guards, then the run.</summary>
        private static int Devices(bool create, bool skipUsbip, string only, Action<string> log)
        {
            if (!create)
            {
                log("Refused: device mode needs --create-devices on the command line. Use --list or --self-check to run without rights.");
                return 2;
            }
            using (var id = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    log("Refused: device mode needs administrator rights.");
                    return 2;
                }
            }
            string running = PadForgeRunning();
            if (running != null)
            {
                log($"Refused: {running}.");
                return 2;
            }
            string missing = PadForgePath.Resolve();
            if (missing != null)
            {
                log($"Refused: PadForge no longer has {missing}. Run --self-check.");
                return 2;
            }

            var families = Families.All();
            if (skipUsbip) families = families.Where(f => !f.Usbip).ToList();
            if (!string.IsNullOrEmpty(only))
            {
                var names = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Xbox stays first as the positive control of the same run.
                families = families.Where((f, i) => i == 0 && f.Name == "xbox"
                    || names.Any(n => f.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
            }

            log("This run creates these virtual controllers, one at a time, each removed after its read:");
            foreach (var f in families)
                log($"  {f.Name}: {f.Type} {f.ProfileId}{(f.Usbip ? " (USB/IP)" : "")}, {f.Lane}, read by {f.ReadHow}");
            log($"XInput is read through {Native.ModulePath(Native.XInputModule)}.");
            log("It presses A and moves the sticks and the left trigger of each controller. The virtual keyboard presses and releases F24 only.");
            log("The virtual mouse moves one count right and one count back. Nothing else is pressed, typed or clicked.");
            log("PadForge's own create path runs first: HIDMaestro's sweep of controllers that ended programs left, and InstallDriver,");
            log("which takes its fast path when this build's driver is installed.");
            if (families.Any(f => f.Usbip))
                log("The USB/IP families run last. If usbip-win2 is not deployed yet, the first of them deploys it, and that restarts the USB root hubs once.");
            log(string.Empty);

            using var cts = new CancellationTokenSource();
            var path = new PadForgePath();
            using var rawInput = new RawInputReader();
            var run = new DeviceRun(path, rawInput, cts.Token, log);
            var results = new List<FamilyResult>();

            void TearDown()
            {
                var vc = run.Current;
                run.Current = null;
                PadForgePath.Destroy(vc);
                path.DisposeContext();
            }
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            EventHandler onExit = (_, _) => TearDown();
            Console.CancelKeyPress += onCancel;
            AppDomain.CurrentDomain.ProcessExit += onExit;
            try
            {
                foreach (var f in families)
                {
                    if (cts.IsCancellationRequested) break;
                    var r = run.Run(f);
                    results.Add(r);
                    log(r.ToString());
                    // Xbox is the positive control: a run whose Xbox pad
                    // delivered nothing says so before the rest.
                    if (f.Name == "xbox" && !r.Verdict.Pass)
                        log("The positive control failed: a failure below may be the harness or this PC, not the family.");
                }
            }
            finally
            {
                TearDown();
                Console.CancelKeyPress -= onCancel;
                AppDomain.CurrentDomain.ProcessExit -= onExit;
            }

            if (cts.IsCancellationRequested) log("Stopped by Ctrl+C. Every controller made was removed.");
            int passed = results.Count(r => r.Verdict.Pass);
            log($"{passed} of {families.Count} families passed.");
            return passed == families.Count && !cts.IsCancellationRequested ? 0 : 1;
        }

        /// <summary>Null when no PadForge runs from C:\PadForge\PadForge.exe,
        /// else the refusal. A PadForge whose path cannot be read is taken as
        /// running from there.</summary>
        private static string PadForgeRunning()
        {
            foreach (var p in Process.GetProcessesByName("PadForge"))
            {
                using (p)
                {
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) return $"a PadForge process {p.Id} runs and its path cannot be read";
                    try
                    {
                        var buf = new char[1024];
                        int size = buf.Length;
                        if (!Native.QueryFullProcessImageNameW(h, 0, buf, ref size))
                            return $"a PadForge process {p.Id} runs and its path cannot be read";
                        string image = new string(buf, 0, size);
                        if (string.Equals(image, PadForgeExe, StringComparison.OrdinalIgnoreCase))
                            return $"PadForge runs from {PadForgeExe} (process {p.Id})";
                    }
                    finally { Native.CloseHandle(h); }
                }
            }
            return null;
        }
    }
}
