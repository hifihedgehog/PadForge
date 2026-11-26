using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// Removes BthPS3 from the PC from Settings: the Bluetooth profile and
    /// PSM filter drivers PadForge installs the first time a PlayStation 3,
    /// Move or Navigation controller is paired, and the WinUSB package and
    /// signing certificate PadForge adds for the pairing. Nothing else on the
    /// PC removes them: PadForge installs BthPS3 without its setup, so
    /// Windows lists no program to uninstall.
    ///
    /// <para>The steps are BthPS3's own setup's removal, in its order
    /// (BthPS3Installer/CustomActions.cs UninstallDrivers). When that setup
    /// installed BthPS3, its own uninstaller runs first, and the same steps
    /// then find nothing left. That setup leaves the two driver services and
    /// their binaries under System32\drivers, and those go too.</para>
    ///
    /// <para>BthPS3 stays while DsHidMini is installed, which needs it. It
    /// stays while a PlayStation controller is connected over Bluetooth:
    /// the removal restarts the radio, and BthPS3 2.12.0 to 3.2.0 crash
    /// Windows when a connected controller is torn down that way
    /// (nefarius/BthPS3#182), the gate the driver update keeps
    /// (Ds3DriverInstaller.MayReplaceDriversNow). PadForge's engine stops
    /// first, which closes every PlayStation controller PadForge holds, and
    /// stays stopped.</para>
    /// </summary>
    internal static class BthPS3Removal
    {
        internal enum ItemKind { Setup, ClassFilter, Service, DriverFile, DriverPackage, WinUsbPackage, Certificate, Radio }

        internal enum ItemOutcome { WouldRemove, Removed, Kept, Failed }

        internal enum RemovalStatus { Previewed, Nothing, Blocked, Removed, RemovedRestartPending, Failed }

        internal enum Blocker { None, ControllerConnected, NotChecked }

        /// <summary>One thing a removal would take, took, keeps or could not
        /// take. Reason says why it stays or failed.</summary>
        internal sealed record Item(ItemKind Kind, string Location, ItemOutcome Outcome, string Reason = null)
        {
            /// <summary>BthPS3 itself, as against PadForge's WinUSB package,
            /// its certificate and the radio.</summary>
            internal bool IsStack => Kind is ItemKind.Setup or ItemKind.ClassFilter or ItemKind.Service
                or ItemKind.DriverFile or ItemKind.DriverPackage;
        }

        internal sealed record Report(bool IsPreview, RemovalStatus Status, Blocker Blocker,
            IReadOnlyList<Item> Items, string SetupProductCode);

        /// <summary>What a removal reads off the PC.</summary>
        internal sealed record MachineState(
            string SetupProductCode,
            string SetupName,
            bool ClassFilter,
            IReadOnlyList<string> Services,
            IReadOnlyList<string> DriverFiles,
            IReadOnlyList<Ds3DriverInstaller.DriverPackage> Packages,
            bool Certificate,
            bool DsHidMini,
            bool Probed,
            int Connected);

        /// <summary>The steps that change the PC, one call each, so a test can
        /// run a removal against a record of them.</summary>
        internal interface ISteps
        {
            int UninstallSetup(string productCode, Action<string> log);
            void RemoveClassFilter(Action<string> log);
            bool DisableService(Action<string> log);
            bool SetRadioOn(bool on, Action<string> log);
            void RestartOffRadio(Action<string> log);
            bool RadioIsOn(int timeoutMs);
            void RemoveBthPs3Package(Ds3DriverInstaller.DriverPackage package, Action<string> log);
            bool DeleteService(string name, Action<string> log);
            bool DeleteDriverFile(string path, Action<string> log);
            void DeletePadForgePackage(Ds3DriverInstaller.DriverPackage package, Action<string> log);
            void RemoveCertificate(Action<string> log);
        }

        /// <summary>Test seam for the read of the PC, null in production.</summary>
        internal static Func<MachineState> ReadSeam;

        /// <summary>Test seam for the steps, null in production.</summary>
        internal static ISteps StepsSeam;

        /// <summary>The binaries BthPS3's INFs copy to System32\drivers.</summary>
        internal static IEnumerable<string> DriverFilePaths()
            => Ds3DriverInstaller.BthPs3Services.Select(s => Path.Combine(Environment.SystemDirectory, "drivers", s + ".sys"));

        /// <summary>What a removal would take and keep, and what stops it.
        /// Changes nothing. A second or two on a PC with many driver
        /// packages, so callers run it off the UI thread.</summary>
        internal static Report Preview() => Plan(Read());

        /// <summary>Reads the PC. A part that cannot be read reads as absent,
        /// except the check for a connected controller, which reads as not
        /// checked and holds the removal.</summary>
        internal static MachineState Read()
        {
            if (ReadSeam != null) return ReadSeam();
            string code = Ds3DriverInstaller.FindBthPs3Setup(out string setupName);
            var services = new List<string>();
            foreach (string s in Ds3DriverInstaller.BthPs3Services)
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s);
                    // A service marked for deletion goes when Windows next
                    // starts, and the removal says so.
                    if (k != null && !Ds3DriverInstaller.IsServiceMarkedForDelete(s)) services.Add(s);
                }
                catch { }
            }
            bool probed = Ds3DriverInstaller.TryProbeBthPs3Children(out int connected, out _, out _);
            return new MachineState(code, setupName, Ds3DriverInstaller.IsBthPs3ClassFilterRegistered(), services,
                DriverFilePaths().Where(File.Exists).ToList(), Ds3DriverInstaller.ReadDriverPackages(),
                Ds3DriverInstaller.HasSigningCertificate(), Ds3DriverInstaller.IsDsHidMiniInstalled(),
                probed, connected);
        }

        private static bool IsBthPs3Package(Ds3DriverInstaller.DriverPackage p)
            => Ds3DriverInstaller.BthPs3Infs.Contains(p.InfName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        private static bool IsPadForgeWinUsbPackage(Ds3DriverInstaller.DriverPackage p)
            => string.Equals(p.InfName, Ds3DriverInstaller.Ds3WinUsbInf, StringComparison.OrdinalIgnoreCase)
               && string.Equals(p.Provider, "PadForge", StringComparison.Ordinal);

        private static string Name(Ds3DriverInstaller.DriverPackage p) => $"{p.Published} ({p.InfName})";

        /// <summary>The preview of a PC as read. BthPS3 stays while DsHidMini
        /// is installed. The certificate stays while another PadForge package
        /// it signs is in the driver store. A removal that would take BthPS3
        /// is held while a PlayStation controller is connected over
        /// Bluetooth, or while that could not be checked.</summary>
        internal static Report Plan(MachineState m)
        {
            var items = new List<Item>();
            string keep = m.DsHidMini ? Strings.Instance.BthPS3Uninstall_DsHidMini : null;
            void Stack(ItemKind kind, string location)
                => items.Add(new Item(kind, location, keep != null ? ItemOutcome.Kept : ItemOutcome.WouldRemove, keep));

            if (m.SetupProductCode != null) Stack(ItemKind.Setup, m.SetupName ?? Ds3DriverInstaller.BthPs3SetupName);
            if (m.ClassFilter) Stack(ItemKind.ClassFilter, "BthPS3PSM");
            foreach (string s in m.Services) Stack(ItemKind.Service, s);
            foreach (var p in m.Packages.Where(IsBthPs3Package)) Stack(ItemKind.DriverPackage, Name(p));
            foreach (string f in m.DriverFiles) Stack(ItemKind.DriverFile, f);

            foreach (var p in m.Packages.Where(IsPadForgeWinUsbPackage))
                items.Add(new Item(ItemKind.WinUsbPackage, Name(p), ItemOutcome.WouldRemove));
            if (m.Certificate)
            {
                bool signsAnother = m.Packages.Any(p => !IsPadForgeWinUsbPackage(p)
                    && string.Equals(p.Signer, Ds3DriverInstaller.PadForgeSigner, StringComparison.Ordinal));
                items.Add(signsAnother
                    ? new Item(ItemKind.Certificate, Ds3DriverInstaller.PadForgeSigner, ItemOutcome.Kept,
                        Strings.Instance.BthPS3Uninstall_CertificateKept)
                    : new Item(ItemKind.Certificate, Ds3DriverInstaller.PadForgeSigner, ItemOutcome.WouldRemove));
            }

            var blocker = Blocker.None;
            if (items.Any(i => i.IsStack && i.Outcome == ItemOutcome.WouldRemove))
                blocker = !m.Probed ? Blocker.NotChecked : m.Connected > 0 ? Blocker.ControllerConnected : Blocker.None;
            var status = blocker != Blocker.None ? RemovalStatus.Blocked
                : items.Any(i => i.Outcome == ItemOutcome.WouldRemove) ? RemovalStatus.Previewed
                : RemovalStatus.Nothing;
            return new Report(true, status, blocker, items, m.SetupProductCode);
        }

        /// <summary>Stops the engine with <paramref name="stopEngine"/>, reads
        /// the PC again, and removes what the read finds. Seconds to a few
        /// minutes, so callers run it on a worker. In the test host each step
        /// is refused and the report says what stayed.</summary>
        internal static Report Run(Action stopEngine)
        {
            try { return RunNow(stopEngine); }
            finally { Ds3DriverInstaller.ForgetBthPs3Packages(); }
        }

        private static Report RunNow(Action stopEngine)
        {
            stopEngine?.Invoke();
            var machine = Read();
            var plan = Plan(machine);
            if (plan.Status is RemovalStatus.Blocked or RemovalStatus.Nothing)
                return plan with { IsPreview = false };
            var packagesRead = machine.Packages;

            var steps = StepsSeam ?? RealSteps.Instance;
            var failed = new Dictionary<Item, string>();
            bool pending = false;
            var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Item radioItem = null;
            void Try(Item item, Action step)
            {
                try { step(); }
                catch (Exception ex) { failed[item] = ex.Message; }
            }
            static List<Item> Taking(Report r) => r.Items.Where(i => i.Outcome == ItemOutcome.WouldRemove).ToList();
            var go = Taking(plan);

            if (go.FirstOrDefault(i => i.Kind == ItemKind.Setup) is { } setup)
            {
                Try(setup, () =>
                {
                    int code = steps.UninstallSetup(plan.SetupProductCode, Log);
                    if (code == 3010) pending = true;
                    else if (code != 0 && code != 1605)
                        throw new InvalidOperationException($"BthPS3's own uninstaller exited with {code}.");
                });
                // That uninstaller took what it installed. The steps below act
                // on what it left.
                machine = Read();
                go = Taking(Plan(machine)).Where(i => i.Kind != ItemKind.Setup).Concat(go.Where(i => !i.IsStack)).Distinct().ToList();
            }
            // A package is looked up in the latest read, then in the first.
            Ds3DriverInstaller.DriverPackage PackageOf(Item item)
                => machine.Packages.Concat(packagesRead).First(p => Name(p) == item.Location);

            // The radio goes off and its device restarts only while the filter
            // is registered or a package is in the store: then the drivers can
            // be loaded. Services and files left without them are not.
            if (go.Any(i => i.Kind is ItemKind.ClassFilter or ItemKind.DriverPackage))
            {
                if (go.FirstOrDefault(i => i.Kind == ItemKind.ClassFilter) is { } filter)
                    Try(filter, () => steps.RemoveClassFilter(Log));
                // The service is withdrawn through a radio that is on, as
                // HostRadio's constructor switches an off radio on before
                // BthPS3's removal withdraws it. A radio the user switched off
                // is off again afterward.
                bool radioWasOn = steps.RadioIsOn(0);
                if (!radioWasOn && steps.SetRadioOn(true, Log)) steps.RadioIsOn(20000);
                steps.DisableService(Log);
                steps.SetRadioOn(false, Log);
                steps.RestartOffRadio(Log);
                foreach (var item in go.Where(i => i.Kind == ItemKind.DriverPackage))
                    Try(item, () => steps.RemoveBthPs3Package(PackageOf(item), Log));
                if (radioWasOn)
                {
                    steps.SetRadioOn(true, Log);
                    if (!steps.RadioIsOn(20000))
                        radioItem = new Item(ItemKind.Radio, "Bluetooth", ItemOutcome.Failed, Strings.Instance.BthPS3Uninstall_RadioOff);
                }
            }
            foreach (var item in go.Where(i => i.Kind == ItemKind.Service))
                Try(item, () => pending |= steps.DeleteService(item.Location, Log));
            foreach (var item in go.Where(i => i.Kind == ItemKind.DriverFile))
                Try(item, () =>
                {
                    if (steps.DeleteDriverFile(item.Location, Log))
                    {
                        pending = true;
                        scheduled.Add(item.Location);
                    }
                });
            foreach (var item in go.Where(i => i.Kind == ItemKind.WinUsbPackage))
                Try(item, () => steps.DeletePadForgePackage(PackageOf(item), Log));
            if (go.FirstOrDefault(i => i.Kind == ItemKind.Certificate) is { } certificate)
                Try(certificate, () => steps.RemoveCertificate(Log));

            // What the PC holds now decides each item's outcome, whatever a
            // step answered: an item gone is removed, one still there failed,
            // with the step's error when it threw. A file Windows deletes at
            // its next start counts as removed.
            var after = Plan(Read()).Items;
            bool StillThere(Item item) => !(item.Kind == ItemKind.DriverFile && scheduled.Contains(item.Location))
                && after.Any(a => a.Kind == item.Kind && string.Equals(a.Location, item.Location, StringComparison.OrdinalIgnoreCase));
            var items = new List<Item>();
            foreach (var item in plan.Items)
            {
                if (item.Outcome != ItemOutcome.WouldRemove) items.Add(item);
                else if (!StillThere(item)) items.Add(item with { Outcome = ItemOutcome.Removed });
                else items.Add(item with
                {
                    Outcome = ItemOutcome.Failed,
                    Reason = failed.TryGetValue(item, out string reason) ? reason : Strings.Instance.BthPS3Uninstall_StillThere,
                });
            }
            if (radioItem != null) items.Add(radioItem);
            var status = items.Any(i => i.Outcome == ItemOutcome.Failed) ? RemovalStatus.Failed
                : pending ? RemovalStatus.RemovedRestartPending
                : RemovalStatus.Removed;
            return new Report(false, status, Blocker.None, items, plan.SetupProductCode);
        }

        private static void Log(string message) => PadForge.Engine.SdlDiagLog.WriteLine("BTHPS3 removal: " + message);

        /// <summary>Writes the report to the diagnostics log in English, one
        /// line per item.</summary>
        internal static void Log(Report report)
        {
            if (report == null) return;
            Log($"{(report.IsPreview ? "preview" : "run")}: {report.Status}{(report.Blocker != Blocker.None ? ", " + report.Blocker : "")}, {report.Items.Count} item(s)");
            foreach (var item in report.Items)
                Log($"{item.Kind} {item.Location}: {item.Outcome}{(item.Reason != null ? " (" + item.Reason + ")" : "")}");
        }

        /// <summary>Why a removal is held, as the dialog and the status line
        /// say it.</summary>
        internal static string BlockerText(Blocker blocker) => blocker switch
        {
            Blocker.ControllerConnected => Strings.Instance.BthPS3Uninstall_Connected,
            Blocker.NotChecked => Strings.Instance.BthPS3Uninstall_NotChecked,
            _ => string.Empty,
        };

        /// <summary>The status line for a finished removal.</summary>
        internal static string StatusLine(Report report) => report.Status switch
        {
            RemovalStatus.Removed => Strings.Instance.Status_BthPS3Removed,
            RemovalStatus.RemovedRestartPending => Strings.Instance.Status_BthPS3RemovedRestart,
            RemovalStatus.Blocked => string.Format(Strings.Instance.Status_BthPS3RemovalBlocked_Format, BlockerText(report.Blocker)),
            RemovalStatus.Nothing => Strings.Instance.BthPS3Uninstall_Nothing,
            _ => Strings.Instance.Status_BthPS3RemovalFailed,
        };

        /// <summary>Each kind of item as the dialog names it.</summary>
        internal static string KindText(ItemKind kind) => kind switch
        {
            ItemKind.Setup => Strings.Instance.BthPS3Uninstall_KindSetup,
            ItemKind.ClassFilter => Strings.Instance.BthPS3Uninstall_KindFilter,
            ItemKind.Service => Strings.Instance.BthPS3Uninstall_KindService,
            ItemKind.DriverFile => Strings.Instance.BthPS3Uninstall_KindFile,
            ItemKind.DriverPackage => Strings.Instance.BthPS3Uninstall_KindPackage,
            ItemKind.WinUsbPackage => Strings.Instance.BthPS3Uninstall_KindWinUsb,
            ItemKind.Certificate => Strings.Instance.BthPS3Uninstall_KindCertificate,
            _ => Strings.Instance.BthPS3Uninstall_KindRadio,
        };

        /// <summary>The steps as Ds3DriverInstaller takes them, each refused
        /// in the test host.</summary>
        private sealed class RealSteps : ISteps
        {
            internal static readonly RealSteps Instance = new();
            public int UninstallSetup(string productCode, Action<string> log) => Ds3DriverInstaller.UninstallBthPs3Setup(productCode, log);
            public void RemoveClassFilter(Action<string> log) => Ds3DriverInstaller.RemoveBthPs3ClassFilter(log);
            public bool DisableService(Action<string> log) => Ds3DriverInstaller.DisableBthPs3Service(log);
            public bool SetRadioOn(bool on, Action<string> log) => Ds3DriverInstaller.SetRadioOn(on, log);
            public void RestartOffRadio(Action<string> log) => Ds3DriverInstaller.RestartOffRadio(log);
            public bool RadioIsOn(int timeoutMs) => Ds3DriverInstaller.WaitForBluetoothRadio(timeoutMs);
            public void RemoveBthPs3Package(Ds3DriverInstaller.DriverPackage package, Action<string> log) => Ds3DriverInstaller.RemoveBthPs3Package(package, log);
            public bool DeleteService(string name, Action<string> log) => Ds3DriverInstaller.DeleteDriverService(name, log);
            public bool DeleteDriverFile(string path, Action<string> log) => Ds3DriverInstaller.DeleteDriverFile(path, log);
            public void DeletePadForgePackage(Ds3DriverInstaller.DriverPackage package, Action<string> log) => Ds3DriverInstaller.DeletePadForgePackage(package, log);
            public void RemoveCertificate(Action<string> log) => Ds3DriverInstaller.RemoveSigningCertificate(log);
        }
    }
}
