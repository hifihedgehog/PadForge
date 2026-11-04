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

        /// <summary>Corsair iCUE, by the process name Aurora looks for
        /// before it lights a Corsair device through iCUE
        /// (Aurora AuroraDeviceManager/Devices/RGBNet/Implementations/CorsairRgbNetDevice.cs:46).</summary>
        public static readonly VendorProgram CorsairIcue = new("Corsair iCUE", "iCUE");

        /// <summary>The program that lights a Lenovo 4-zone keyboard. Lenovo
        /// Legion Toolkit refuses every 4-zone write while Lenovo Vantage
        /// runs and checks no other program
        /// (LenovoLegionToolkit-Team RGBKeyboardBacklightController.cs:214-218,
        /// RGBKeyboardBacklightControl.xaml.cs:186-200).</summary>
        public static readonly IReadOnlyList<VendorProgram> Lenovo4Zone = new[] { LenovoVantage };

        /// <summary>The programs that light the per-key keyboards of the
        /// Legion Y740 to the Legion 7 Gen 6: Lenovo Vantage, which
        /// Lenovo's Legion user guides name for keyboard lighting effects
        /// (download.lenovo.com legion_slim_7 explore_lightingEffects.html),
        /// and Corsair iCUE, which Lenovo's support video for Legion
        /// keyboard lighting uses (support.lenovo.com vid500120).</summary>
        public static readonly IReadOnlyList<VendorProgram> LenovoLegionUsb = new[] { LenovoVantage, CorsairIcue };

        /// <summary>Legion Accessory Central, Lenovo's program for its
        /// Legion accessories (support.lenovo.com acc500148), by the three
        /// programs its installer puts in its app folder: legion_hid.exe,
        /// whose main window and device modules (Lenny.Module.K500,
        /// Lenny.Module.M300) light the K500 keyboard and the M300 mouse
        /// through ldm_k500.dll and ldm_m300.dll, legion_haw.exe, which
        /// starts legion_hid.exe, and legion_api.exe. Read from installer
        /// 2.0.6.06161 (setup_2.0.6.06161.exe, SHA-256 6834205E...43D5, the
        /// winget package Lenovo.LegionAccessoryCentral), file names and
        /// strings only.</summary>
        public static readonly VendorProgram LegionAccessoryCentral = new("Legion Accessory Central",
            "legion_hid", "legion_haw", "legion_api");

        /// <summary>The program that lights the K500 keyboard and the M300
        /// mouse.</summary>
        public static readonly IReadOnlyList<VendorProgram> LenovoAccessories = new[] { LegionAccessoryCentral };

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

        /// <summary>Cooler Master's MasterPlus, which lights the family's
        /// keyboards, mice, mousepads and controllers. Its executables, by
        /// the file list Advanced Uninstaller PRO gives for MasterPlus PER
        /// 2.0.4 ("MasterPlus(PER. Only).exe" and cm-blackhawk.exe, the
        /// background component, in Program Files (x86)\CoolerMaster\PORTAL).
        /// No source code names the program's processes.</summary>
        public static readonly VendorProgram MasterPlus = new("MasterPlus", "MasterPlus", "cm-blackhawk");

        /// <summary>HyperX NGENUITY, which lights HyperX keyboards. Its
        /// executable is NGenuity.exe (FreeFixer's file entry "What is
        /// NGenuity.exe?", and the file list Advanced Uninstaller PRO gives
        /// for HyperX NGenuity Software), and its Microsoft Store build runs
        /// NGenuity2Helper.exe, which the same prefix takes in. No source code
        /// names the program's processes.</summary>
        public static readonly VendorProgram Ngenuity = new("NGENUITY", "NGenuity");

        /// <summary>MSI's Mystic Light, which lights MSI's Mystic Light
        /// keyboards. Aurora counts it as running while
        /// Mystic_Light_Service.exe runs (Aurora
        /// AuroraDeviceManager/Devices/RGBNet/Implementations/MsiDevice.cs:22-26).</summary>
        public static readonly VendorProgram MsiMysticLight = new("Mystic Light", "Mystic_Light_Service");

        /// <summary>SteelSeries GG and the Engine before it, which light the
        /// SteelSeries controllers of MSI laptops (msi-perkeyrgb
        /// README.md:101-105), by the process names the linker counts GG by
        /// (<see cref="PeripheralPresence.SteelSeriesHostPrefixes"/>).</summary>
        public static readonly VendorProgram SteelSeriesGG = new("SteelSeries GG", PeripheralPresence.SteelSeriesHostPrefixes);

        /// <summary>Alienware Command Center, which lights Dell's G series
        /// laptops and Alienware's keyboards. alienfx-tools stops its
        /// service, AWCCService, before it drives the lights itself
        /// (alienfx-tools Common/Common.cpp:98-103), and Dell's forums name
        /// the program's own process AWCC.exe and its service's
        /// AWCC.Service.exe. The one prefix takes in both.</summary>
        public static readonly VendorProgram AlienwareCommandCenter = new("Alienware Command Center", "AWCC");

        /// <summary>ROCCAT Swarm, which lights Roccat's and Turtle Beach's
        /// Roccat-made keyboards, mice, mousepads and headsets. Its main
        /// executable is ROCCAT_Swarm.exe and its background monitor
        /// ROCCAT_Swarm_Monitor.exe, both in C:\Program Files (x86)\ROCCAT\ROCCAT Swarm
        /// (file.net's entry for roccat_swarm_monitor.exe, Advanced
        /// Uninstaller's entries for ROCCAT Swarm). The one prefix takes in
        /// both. No source code names Turtle Beach's Swarm II
        /// processes.</summary>
        public static readonly VendorProgram RoccatSwarm = new("ROCCAT Swarm", "ROCCAT_Swarm");

        /// <summary>The programs that light a Roccat device.</summary>
        public static readonly IReadOnlyList<VendorProgram> Roccat = new[] { RoccatSwarm };

        /// <summary>XPG Prime, which lights the XPG Summoner. SignalRGB's
        /// plugin for the keyboard stands down while XPG-Prime.exe runs
        /// (signal-plugins XPG_Summoner_Keyboard.js:20-22).</summary>
        public static readonly VendorProgram XpgPrime = new("XPG Prime", "XPG-Prime");

        /// <summary>Wootility, Wooting's program for its keyboards. Its main
        /// executable is Wootility.exe, in a per-user folder,
        /// %LocalAppData%\Programs\wootility (the file list of Wootility
        /// 5.0.6 at advanceduninstaller.com, Wootility-5_0_6). No source
        /// code names its processes.</summary>
        public static readonly VendorProgram WootingWootility = new("Wootility", "Wootility");

        /// <summary>The program that lights the Wooting keyboards.</summary>
        public static readonly IReadOnlyList<VendorProgram> Wooting = new[] { WootingWootility };

        /// <summary>OpenRGB, which holds the devices of every family it
        /// finds and serves them over its SDK server (#557,
        /// <see cref="OpenRgbBackend"/>). No model names it: the OpenRGB
        /// worker asks the server which devices it holds while this runs.
        /// Its executable is OpenRGB.exe (OpenRGB OpenRGB.pro:59, TARGET),
        /// the name Aurora asks for before it connects to the server
        /// (AuroraDeviceManager/Devices/RGBNet/Implementations/OpenRgbNetDevice.cs:27-35).</summary>
        public static readonly VendorProgram OpenRgb = new("OpenRGB", "OpenRGB");

        /// <summary>Logitech G HUB, by its agent, and Logitech Gaming
        /// Software, by its two hosts: the process names every Logitech
        /// reference gates on, which the linker counts the Logitech program
        /// by (<see cref="PadForge.Services.LogiLedEngineNative.HostProcesses"/>).
        /// They light the Logitech devices of the shared worker, such as
        /// the G600 (<see cref="LogitechG600Lighting"/>).</summary>
        public static readonly VendorProgram LogitechGHub = new("Logitech G HUB",
            PadForge.Services.LogiLedEngineNative.HostProcesses[0]);
        public static readonly VendorProgram LogitechGamingSoftware = new("Logitech Gaming Software",
            PadForge.Services.LogiLedEngineNative.HostProcesses[1], PadForge.Services.LogiLedEngineNative.HostProcesses[2]);

        /// <summary>The programs that light a Logitech device.</summary>
        public static readonly IReadOnlyList<VendorProgram> Logitech = new[] { LogitechGHub, LogitechGamingSoftware };

        /// <summary>Glorious CORE, which lights the Glorious Model I and
        /// every Glorious product but the GMMK 1 and the first wired Model O
        /// and Model D (Glorious's download page, gloriousgaming.com/pages/downloads).
        /// Its executable is "Glorious Core.exe" in C:\Program Files (x86)\Glorious Core\Glorious Core
        /// (Advanced Uninstaller's entry for Glorious Core,
        /// Glorious-Core-a0a4910c42cad74a562cef2629742a21). No source code
        /// names the program's processes.</summary>
        public static readonly VendorProgram GloriousCore = new("Glorious CORE", "Glorious Core");

        /// <summary>The program that lights the Glorious Model I.</summary>
        public static readonly IReadOnlyList<VendorProgram> Glorious = new[] { GloriousCore };

        /// <summary>Gigabyte's AORUS ENGINE, which Gigabyte's support page
        /// for the Aorus M2 offers as "AORUS ENGINE (RGB Fusion 2.0
        /// included)". Its main executable is AORUS.exe in
        /// C:\Program Files (x86)\GIGABYTE\AORUS ENGINE (Advanced
        /// Uninstaller's entry for AORUS ENGINE 1.8.9.0,
        /// AORUS-ENGINE-418ba20628737ec515dfdd887fa85aae). No source code
        /// names the program's processes.</summary>
        public static readonly VendorProgram AorusEngine = new("AORUS ENGINE", "AORUS");

        /// <summary>Gigabyte's RGB Fusion, the lighting part of AORUS ENGINE
        /// and a program of its own. Its main executable is RGBFusion.exe
        /// and its service GvLedServices.exe, in C:\Program Files (x86)\GIGABYTE\RGBFusion
        /// (Advanced Uninstaller's entry for RGB Fusion 3.20.1019.1,
        /// RGB-Fusion-154d6185f959b02303f1d8916dc67a60). No source code
        /// names the program's processes.</summary>
        public static readonly VendorProgram RgbFusion = new("RGB Fusion", "RGBFusion", "GvLedServices");

        /// <summary>The programs that light the Gigabyte Aorus M2.</summary>
        public static readonly IReadOnlyList<VendorProgram> GigabyteMice = new[] { AorusEngine, RgbFusion };

        /// <summary>The program that lights the MSI Clutch GM11: Mystic
        /// Light, in Dragon Center, which MSI's product page for the GM11
        /// names for its lighting.</summary>
        public static readonly IReadOnlyList<VendorProgram> MsiClutch = new[] { MsiMysticLight };

        /// <summary>NZXT CAM, which lights NZXT's mice. Its executables are
        /// "NZXT CAM.exe" and cam_helper.exe, among others, in
        /// C:\Program Files\NZXT CAM (Advanced Uninstaller's entry for NZXT
        /// CAM 4.41.4, NZXT-CAM-4_41_4-e666c00b3c05311abb700f01950ea266). No
        /// source code names the program's processes.</summary>
        public static readonly VendorProgram NzxtCam = new("NZXT CAM", "NZXT CAM", "cam_helper");

        /// <summary>The program that lights the NZXT Lift.</summary>
        public static readonly IReadOnlyList<VendorProgram> Nzxt = new[] { NzxtCam };

        /// <summary>Patriot's Viper gaming mouse software, whose main
        /// executable is "PATRIOT VIPER MOUSE.exe" in
        /// C:\Program Files (x86)\PATRIOT VIPER GAMING MOUSE (Advanced
        /// Uninstaller's entry for VIPER GAMING MOUSE 1.6,
        /// VIPER-GAMING-MOUSE-7dd9ec9fad1b307f54432a407d85a1ce). The entry
        /// names no mouse model. No source code names the program's
        /// processes.</summary>
        public static readonly VendorProgram PatriotViperMouse = new("Viper Gaming Mouse", "PATRIOT VIPER MOUSE");

        /// <summary>The program that lights the Patriot Viper V550.</summary>
        public static readonly IReadOnlyList<VendorProgram> PatriotViper = new[] { PatriotViperMouse };

        /// <summary>Turtle Beach Swarm II, which lights the Command Series
        /// KP7 keypad (Turtle Beach's product listing: "customize RGB
        /// lighting" through Swarm II). Its executables are
        /// "Turtle Beach Swarm II.exe" and "Turtle Beach Device Service.exe",
        /// among others, in C:\Program Files\Turtle Beach Swarm II (Advanced
        /// Uninstaller's entry for Turtle Beach Swarm II 1.0.0.24,
        /// Turtle-Beach-Swarm-II-ff8854d8bfe58b79c9af9e93eaac5666). No
        /// source code names the program's processes.</summary>
        public static readonly VendorProgram TurtleBeachSwarmII = new("Turtle Beach Swarm II",
            "Turtle Beach Swarm II", "Turtle Beach Device Service");

        /// <summary>Thermaltake's TT iTAKE, which lights its Level 20 desk
        /// (ConflictingProcesses, ThermalTake_Level20_Desk.js).</summary>
        public static readonly VendorProgram ThermaltakeItake = new("TT iTAKE", "TT iTAKE Engine");

        /// <summary>The program of KBM!'s keyboards
        /// (ConflictingProcesses, KBM_Keyboard_Controller.js).</summary>
        public static readonly VendorProgram KbmTeclado = new("Teclado Mecanico Gamer", "TECLADO MECANICO GAMER");

        /// <summary>The program that lights the KP7.</summary>
        public static readonly IReadOnlyList<VendorProgram> TurtleBeach = new[] { TurtleBeachSwarmII };

        /// <summary>Skydimo's app, which lights its headset stand and its
        /// light strips. Its executable is SkyDimo.exe, in
        /// C:\Program Files (x86)\Skydimo (the file lists Advanced Uninstaller
        /// PRO gives for Skydimo's Windows installs, among them version
        /// 1.0.0.240202). No source code names its processes.</summary>
        public static readonly VendorProgram Skydimo = new("Skydimo", "SkyDimo");

        /// <summary>HYTE Nexus, which lights the CNVS mousepad. Its main
        /// executable is "HYTE Nexus.exe" and its service's
        /// "HYTE Nexus Service.exe" in version 1.0.204 and
        /// HYTE.Nexus.Service.exe from version 1.3.134 on (the file lists
        /// Advanced Uninstaller PRO gives for HYTE Nexus 1.0.204, 1.3.134 and
        /// 1.5.560). The two prefixes take in all three. No source code names
        /// its processes.</summary>
        public static readonly VendorProgram HyteNexus = new("HYTE Nexus", "HYTE Nexus", "HYTE.Nexus");

        /// <summary>Luxafor's app, which lights the Luxafor Flag.
        /// LuxaforOnAir counts it as running while LuxaforApp.exe runs, and
        /// stands aside, since the app overrides its colors (LuxaforOnAir
        /// LuxOnAir/Properties/Settings.Designer.cs:39, ConflictChecker.cs:28
        /// and 75-85).</summary>
        public static readonly VendorProgram LuxaforApp = new("Luxafor", "LuxaforApp");

        /// <summary>The amBX software that drives the Mad Catz Cyborg Gaming
        /// Light: its engine service, C:\Program Files\amBX\System\amBX_Engine.exe
        /// (the LASSH service entry for the amBX Engine at sysinfo.org), and
        /// its effect generator amBXFxGen.exe, which holds the lights until it
        /// is stopped (a MediaPortal forum thread on amBX lights, "Amb i
        /// Light Support", page 5). The one prefix takes in both. No source
        /// code names its processes.</summary>
        public static readonly VendorProgram AmBx = new("amBX", "amBX");

        /// <summary>Blink1Control2, ThingM's program for the blink(1). Its
        /// executable is named by its package's productName, Blink1Control2
        /// (Blink1Control2 package.json:19), and the prefix takes in the
        /// Blink1Control before it.</summary>
        public static readonly VendorProgram Blink1Control = new("Blink1Control", "Blink1Control");

        /// <summary>Elgato's Stream Deck app, which draws the Stream Deck's
        /// keys while it runs. Elgato's own CLI counts it as running while
        /// StreamDeck.exe runs on Windows (elgatosf/cli src/stream-deck.ts:61-63,
        /// isStreamDeckRunning).</summary>
        public static readonly VendorProgram ElgatoStreamDeck = new("Stream Deck", "StreamDeck");

        /// <summary>Every program, in the order the presence lists the ones
        /// that run.</summary>
        public static readonly IReadOnlyList<VendorProgram> All = new[]
        {
            LenovoVantage, LegionSpace, LegionZone, ArmouryCrate, OmenGamingHub, Bazecor, CorsairIcue,
            LegionAccessoryCentral, MasterPlus, Ngenuity, MsiMysticLight, SteelSeriesGG, AlienwareCommandCenter,
            RoccatSwarm, XpgPrime, WootingWootility, OpenRgb, LogitechGHub, LogitechGamingSoftware, GloriousCore,
            AorusEngine, RgbFusion, NzxtCam, PatriotViperMouse, TurtleBeachSwarmII, Skydimo, HyteNexus, LuxaforApp,
            AmBx, Blink1Control, ElgatoStreamDeck,
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
