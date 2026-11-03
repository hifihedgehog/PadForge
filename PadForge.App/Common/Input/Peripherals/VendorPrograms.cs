using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// A program that lights a device family of the shared HID lighting
    /// worker while it runs (#557), the per-family counterpart of the
    /// Logitech, Razer and SteelSeries programs the linker knows by vendor
    /// ID (<see cref="PeripheralLinker.HasVendorProgram"/>). A vendor ID
    /// cannot carry the check for these families: Lenovo's Legion keyboards
    /// use ITE's 0x048D, which other laptop makers' keyboards use too. So a
    /// model names its programs (<see cref="HidLightingModel.Programs"/>),
    /// and while one of them runs the linker leaves the device to it.
    /// </summary>
    internal sealed class VendorProgram
    {
        /// <summary>The program's product name, which the Lighting tab's
        /// route line names and no locale translates.</summary>
        public string Name { get; }

        /// <summary>The image names that mean the program runs, without
        /// ".exe". A running process whose image name starts with one of
        /// them counts, in any case.</summary>
        public IReadOnlyList<string> Processes { get; }

        public VendorProgram(string name, params string[] processes)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Processes = processes ?? Array.Empty<string>();
        }

        /// <summary>Whether a process of this image name, given without
        /// ".exe", is one of the program's.</summary>
        public bool Owns(string image)
        {
            if (string.IsNullOrEmpty(image)) return false;
            foreach (string process in Processes)
                if (image.StartsWith(process, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// Every program a family of the shared worker yields to, one line
    /// each, so the presence read finds them all in the one process
    /// snapshot it takes (<see cref="PeripheralPresence.WithRunning"/>). A
    /// family adds its programs here and names them on its models.
    ///
    /// <para>A program counts as running while any process it is known by
    /// runs, the rule Lenovo Legion Toolkit follows before it touches a
    /// Spectrum keyboard: a program is "Enabled" while one of its listed
    /// services or processes runs (LenovoLegionToolkit-Team
    /// LenovoLegionToolkit.Lib/SoftwareDisabler/AbstractSoftwareDisabler.cs:81-94),
    /// and a process is listed when its name starts with one of the
    /// program's (lines 210-230, StartsWith in any case). PadForge reads
    /// process names alone, so a program's service counts only where its
    /// image name starts with one of the names below.</para>
    /// </summary>
    internal static class VendorPrograms
    {
        /// <summary>Lenovo Vantage and its ImController. The Toolkit's
        /// process names (VantageDisabler.cs:26-34). Its two services are
        /// ImControllerService and LenovoVantageService (lines 20-24). The
        /// first runs Lenovo.Modern.ImController.exe, which the second name
        /// below takes in. The Toolkit's own controller refuses every
        /// Spectrum write while Vantage runs: "Can't manage Spectrum
        /// keyboard with Vantage enabled"
        /// (LenovoLegionToolkit-Team SpectrumKeyboardBacklightController.cs:475-479).</summary>
        public static readonly VendorProgram LenovoVantage = new("Lenovo Vantage",
            "BGHelper", "Lenovo.Modern.ImController", "Lenovo.Vantage", "LenovoVantage", "QSHelper",
            "ScheduleEventAction");

        /// <summary>Legion Space, the Toolkit's process names
        /// (LegionSpaceDisabler.cs:14-22). Its service is DAService (lines
        /// 9-12).</summary>
        public static readonly VendorProgram LegionSpace = new("Legion Space",
            "Bino3D", "LegionGameWidget", "LegionSpace", "LegionSpaceComponent", "LegionSpaceToast", "LSDaemon");

        /// <summary>Legion Zone, the Toolkit's process names
        /// (LegionZoneDisabler.cs:17-35), which hold its two services,
        /// LZService and StreamingService (lines 11-15).</summary>
        public static readonly VendorProgram LegionZone = new("Legion Zone",
            "BorderlessSpace", "ContextMenuInstaller", "DoudouAI", "LegionZone", "LZAgent", "LZInstall", "LZMain",
            "lzolhelp64", "LZService", "LZStrategy", "LZTray", "LZUpdate", "NvOcScanner", "StreamingDiagnosis",
            "StreamingHost", "StreamingService");

        /// <summary>The Lenovo programs that light a Spectrum keyboard. The
        /// Toolkit's Spectrum page stands down while any of the three runs
        /// (LenovoLegionToolkit-Team
        /// LenovoLegionToolkit.WPF/Controls/KeyboardBacklight/Spectrum/SpectrumKeyboardBacklightControl.xaml.cs:352-364).</summary>
        public static readonly IReadOnlyList<VendorProgram> LenovoSpectrum = new[] { LenovoVantage, LegionSpace, LegionZone };

        /// <summary>ASUS Armoury Crate, which lights a TUF laptop's
        /// keyboard. G-Helper counts it as running while either of these
        /// two processes runs (g-helper app/Helpers/AsusService.cs:44-50,
        /// IsArmouryRunning).</summary>
        public static readonly VendorProgram ArmouryCrate = new("Armoury Crate", "ArmouryCrate.Service", "LightingService");

        /// <summary>HP's OMEN Gaming Hub, which lights an Omen laptop's
        /// keyboard. Its background process, by the name HP's support forum
        /// gives it (HP Community threads "OmenCommandCenterBackground.exe
        /// Memory Error" and "Omen Command Center Background.exe Application
        /// Error"), and named there as the process that applies its RGB
        /// lighting settings. No source code names the hub's
        /// processes.</summary>
        public static readonly VendorProgram OmenGamingHub = new("OMEN Gaming Hub", "OmenCommandCenterBackground");

        /// <summary>Dygma's Bazecor, which drives a Raise over the same
        /// serial port: its executable is named by its package's
        /// productName (Bazecor package.json:3).</summary>
        public static readonly VendorProgram Bazecor = new("Bazecor", "Bazecor");

        /// <summary>Every program, in the order the presence lists the ones
        /// that run.</summary>
        public static readonly IReadOnlyList<VendorProgram> All = new[]
        {
            LenovoVantage, LegionSpace, LegionZone, ArmouryCrate, OmenGamingHub, Bazecor,
        };
    }

    /// <summary>The programs of <see cref="VendorPrograms"/> that run, from
    /// one process snapshot, compared by what they hold, so a presence read
    /// that finds the same programs equals the one before it and raises no
    /// status change.</summary>
    internal sealed class RunningPrograms : IEquatable<RunningPrograms>
    {
        public static readonly RunningPrograms None = new(Array.Empty<VendorProgram>());

        private readonly VendorProgram[] _programs;

        private RunningPrograms(VendorProgram[] programs) => _programs = programs;

        public IReadOnlyList<VendorProgram> Programs => _programs;

        /// <summary>The programs of <paramref name="known"/> that one of
        /// <paramref name="images"/> belongs to, in the order of
        /// <paramref name="known"/>. Image names come without ".exe".</summary>
        public static RunningPrograms Of(IReadOnlyCollection<string> images, IReadOnlyList<VendorProgram> known)
        {
            if (images == null || images.Count == 0 || known == null || known.Count == 0) return None;
            List<VendorProgram> found = null;
            foreach (var program in known)
            {
                if (program == null) continue;
                foreach (string image in images)
                {
                    if (!program.Owns(image)) continue;
                    (found ??= new List<VendorProgram>()).Add(program);
                    break;
                }
            }
            return found == null ? None : new RunningPrograms(found.ToArray());
        }

        /// <summary>The first of <paramref name="programs"/> that runs, or
        /// null.</summary>
        public VendorProgram FirstOf(IReadOnlyList<VendorProgram> programs)
        {
            if (programs == null || _programs.Length == 0) return null;
            foreach (var program in programs)
                if (Array.IndexOf(_programs, program) >= 0) return program;
            return null;
        }

        public bool Equals(RunningPrograms other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (other._programs.Length != _programs.Length) return false;
            for (int i = 0; i < _programs.Length; i++)
                if (!ReferenceEquals(other._programs[i], _programs[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => Equals(obj as RunningPrograms);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var program in _programs) hash.Add(program);
            return hash.ToHashCode();
        }
    }
}
