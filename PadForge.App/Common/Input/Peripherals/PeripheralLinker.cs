using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>Which vendor software is installed or running, read cheaply
    /// (registry, files, process names) and never by opening an SDK, so an
    /// unassigned device costs nothing (#494).</summary>
    internal sealed record PeripheralPresence(
        bool LogitechLedSdk, bool LogitechGHubRunning, bool RazerSynapse, bool SteelSeriesGG, bool SensaPlatform)
    {
        public static readonly PeripheralPresence None = new(false, false, false, false, false);

        /// <summary>Logitech Gaming Software runs and G HUB does not, so the
        /// Lighting tab names it as the program lighting Logitech
        /// devices.</summary>
        public bool LogitechGamingSoftware { get; init; }

        /// <summary>A Razer Synapse or Razer Central process runs, so Synapse drives the Razer devices.</summary>
        public bool RazerSoftwareRunning { get; init; }

        /// <summary>SteelSeries GG or its engine runs, so GG drives the SteelSeries devices.</summary>
        public bool SteelSeriesGGRunning { get; init; }

        /// <summary>The programs of <see cref="VendorPrograms.All"/> that
        /// run, each of which drives the devices of the shared-worker models
        /// that name it (<see cref="HidLightingModel.Programs"/>).</summary>
        public RunningPrograms Programs { get; init; } = RunningPrograms.None;

        /// <summary>The Chroma SDK runtime Synapse installs, which Colore's
        /// native layer loads by this name.</summary>
        internal const string ChromaRuntimeDll = "RzChromaSDK64.dll";
        internal const string ChromaSdkKey = @"SOFTWARE\Razer Chroma SDK";

        /// <summary>The processes of Razer Synapse and Razer Central, by
        /// image name without the extension: Synapse 4's engine, Synapse 3
        /// and its two services, and Razer Central's service (uncoil
        /// apps/uncoild/src/conflicts.rs:20-24). The first two are the names
        /// the analog keyboard reader already asks for
        /// (AnalogKeyboardCatalog.cs:405). The Chroma SDK services of the
        /// same table (conflicts.rs:25-27) stay out. They carry another
        /// program's lighting and keep running after Synapse is quit, so
        /// with them here a PC with Synapse installed would never light a
        /// Razer device on its own.</summary>
        internal static readonly string[] RazerHostProcesses =
        {
            "RazerAppEngine", "Razer Synapse 3", "Razer Synapse Service", "Razer Synapse Service Process",
            "RazerCentralService",
        };

        /// <summary>The processes of SteelSeries GG, each by the start of
        /// its image name: a process whose name begins with one of these
        /// counts, in any case. The vendor names SteelSeriesEngine.exe,
        /// SteelSeriesGG.exe and SteelSeriesGGClient.exe as GG's own files
        /// (SteelSeries help center article 48552122093965) and
        /// SteelSeriesGGEZClient.exe beside them (article 360060998272).
        /// Aurora asks for SteelSeriesEngine and SteelSeriesGG before it
        /// trusts the GameSense SDK (Aurora
        /// AuroraDeviceManager/Devices/RGBNet/Implementations/SteelSeriesRgbNetDevice.cs:10-11
        /// and 23-24), and a firmware tool for the Apex 3 TKL refuses to
        /// run beside steelseriesengine.exe, steelseriesprism.exe or
        /// steelseriesgg.exe (apex-3-tkl apex3_tkl/updater.py:33). No
        /// source lists every process of GG, so the match is by prefix,
        /// which covers all of those: a name it takes in by mistake only
        /// closes the direct path, the safe side to be wrong on.</summary>
        internal static readonly string[] SteelSeriesHostPrefixes = { "SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesPrism" };

        /// <summary>What is installed, from the registry and files alone.
        /// No process is looked at, and no running flag is set: what runs
        /// is read by the host's <see cref="VendorProgramWatch"/>, from one
        /// snapshot of the running processes.</summary>
        internal static PeripheralPresence ReadInstalled()
        {
            bool ledSdk = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(LogiLedEngineNative.ServerBinaryKey);
                string path = key?.GetValue(null)?.ToString();
                ledSdk = !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            catch { }

            bool synapse = false;
            try
            {
                synapse = File.Exists(Path.Combine(Environment.SystemDirectory, ChromaRuntimeDll));
                if (!synapse)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(ChromaSdkKey);
                    synapse = key != null;
                }
            }
            catch { }

            bool gg = false;
            try
            {
                gg = File.Exists(GameSenseClient.DefaultCorePropsPath)
                     || Directory.Exists(Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteelSeries", "GG"));
            }
            catch { }

            return new PeripheralPresence(ledSdk, false, synapse, gg, PlatformSupport.SensaAvailable);
        }

        /// <summary>A presence with its running flags from one snapshot of
        /// the running processes, or, when the snapshot could not be taken
        /// (<paramref name="imageNames"/> is null), from
        /// <paramref name="last"/>, the presence read before it. A failed
        /// snapshot says nothing about what runs, so the last answer
        /// stands wherever there is one: read as every program stopped, it
        /// would hand a running program's devices to a direct worker. With
        /// no read before it the presence comes back as it was given, which
        /// at a host's first full read is with nothing running.</summary>
        internal static PeripheralPresence WithSnapshot(PeripheralPresence presence, IEnumerable<string> imageNames,
            PeripheralPresence last)
        {
            if (presence == null) return null;
            if (imageNames != null) return WithRunning(presence, imageNames);
            if (last == null || ReferenceEquals(last, presence)) return presence;
            return CopyRunning(presence, last);
        }

        /// <summary>A presence with the running flags of another, for a
        /// host whose presence is scripted: it reads its running flags
        /// through the same script.</summary>
        internal static PeripheralPresence CopyRunning(PeripheralPresence current, PeripheralPresence source)
        {
            if (current == null || source == null) return current;
            return current with
            {
                LogitechGHubRunning = source.LogitechGHubRunning,
                LogitechGamingSoftware = source.LogitechGamingSoftware,
                RazerSoftwareRunning = source.RazerSoftwareRunning,
                SteelSeriesGGRunning = source.SteelSeriesGGRunning,
                Programs = source.Programs,
            };
        }

        /// <summary>A presence with every running flag set from a list of
        /// process image names, where null or an empty list reads as
        /// nothing running. A name is compared without its ".exe" and
        /// without regard to case, the way Process.GetProcessesByName
        /// compared the names this replaces. The Logitech host is the
        /// first of <see cref="LogiLedEngineNative.HostProcesses"/> that
        /// runs, as <see cref="LogiLedEngineNative.RunningHost"/> answers
        /// it. The Logitech and Razer programs are whole names. SteelSeries
        /// GG is any name that starts with one of
        /// <see cref="SteelSeriesHostPrefixes"/>. The programs of
        /// <paramref name="known"/>, <see cref="VendorPrograms.All"/> unless
        /// a test names others, are matched by their own rule
        /// (<see cref="VendorProgram.Owns"/>), from the same names, so the
        /// check costs no second snapshot.</summary>
        internal static PeripheralPresence WithRunning(PeripheralPresence current, IEnumerable<string> imageNames,
            IReadOnlyList<VendorProgram> known = null)
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string image in imageNames ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(image)) continue;
                running.Add(image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? image.Substring(0, image.Length - 4) : image);
            }
            string host = null;
            foreach (string name in LogiLedEngineNative.HostProcesses)
            {
                if (!running.Contains(name)) continue;
                host = name;
                break;
            }
            bool steelSeries = false;
            foreach (string name in running)
            {
                foreach (string prefix in SteelSeriesHostPrefixes)
                    steelSeries |= name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                if (steelSeries) break;
            }
            return current with
            {
                LogitechGHubRunning = host != null,
                LogitechGamingSoftware = host != null && host != LogiLedEngineNative.HostProcesses[0],
                RazerSoftwareRunning = running.Overlaps(RazerHostProcesses),
                SteelSeriesGGRunning = steelSeries,
                Programs = RunningPrograms.Of(running, known ?? VendorPrograms.All),
            };
        }
    }

    /// <summary>
    /// One peripheral host's look at the running vendor programs (#494):
    /// the full read every <see cref="PeripheralOutputHost.PresenceMs"/>,
    /// and the read of the running flags alone that the link pass makes on
    /// every pass while a vendor's program could take a device PadForge
    /// lights on its own.
    ///
    /// <para>What that read costs is the snapshot of process names. A
    /// Toolhelp snapshot took 14 ms on one bench PC with 500 to 590
    /// processes, and 358 ms of CPU on a PC with 404 processes and 8,884
    /// threads, where one every 5 s held the link thread at 7 percent of a
    /// core. The snapshot now names each process from the list of process
    /// IDs (<see cref="RunningProcessList"/>), 7 ms on that same PC. The
    /// list of process IDs alone (K32EnumProcesses) takes 0.02 to
    /// 0.05 ms.</para>
    ///
    /// <para>So the fast read asks for that list first and compares it
    /// with the one seen at the last snapshot. The same IDs in the same
    /// order mean no process came or went, so no running flag moved, and
    /// no snapshot is taken. Any difference, or a list that cannot be read,
    /// takes one. The IDs are read before the snapshot they go with: a
    /// process that starts between the two is in the names and not in the
    /// IDs, so the next pass sees a list that moved and looks again, where
    /// the other order could hide it. A process that ends and another that
    /// takes the same ID in the same place between two passes is not seen
    /// by the comparison, and is left to the full read, which takes a
    /// snapshot whatever the list says.</para>
    /// </summary>
    internal sealed class VendorProgramWatch
    {
        private readonly Func<PeripheralPresence> _installed;
        private readonly Func<uint[]> _processIds;
        private readonly Func<List<string>> _imageNames;

        /// <summary>The link thread makes every read but the one at the
        /// host's start, and a pass that outlived a stop can still be in
        /// one when the next start reads, so the baseline is kept under a
        /// lock.</summary>
        private readonly object _sync = new();

        /// <summary>The process IDs read right before the last snapshot
        /// that could be taken, or null when there is none to compare
        /// with: nothing read yet, a list that could not be read, or a
        /// snapshot that failed.</summary>
        private uint[] _seen;
        private int _snapshots;

        public VendorProgramWatch()
            : this(null, null, null) { }

        /// <summary>Test seam: the read of what is installed, the list of
        /// process IDs and the snapshot of process names, each of which
        /// answers null when it fails.</summary>
        internal VendorProgramWatch(Func<PeripheralPresence> installed, Func<uint[]> processIds,
            Func<List<string>> imageNames)
        {
            _installed = installed ?? PeripheralPresence.ReadInstalled;
            _processIds = processIds ?? RunningProcesses.ProcessIds;
            _imageNames = imageNames ?? RunningProcesses.ImageNames;
        }

        /// <summary>Snapshots taken, for the tests of when one is.</summary>
        internal int Snapshots => Volatile.Read(ref _snapshots);

        /// <summary>The full read: what is installed, from the registry and
        /// files, and what runs, from a snapshot it always takes.
        /// <paramref name="last"/> is the presence read before this one,
        /// whose running flags stand when the snapshot fails
        /// (<see cref="PeripheralPresence.WithSnapshot"/>).</summary>
        public PeripheralPresence Read(PeripheralPresence last)
        {
            var installed = _installed();
            lock (_sync) return PeripheralPresence.WithSnapshot(installed, Snapshot(Ids()), last);
        }

        /// <summary>The fast read: the same presence with its running
        /// flags read again, from a snapshot taken only when the list of
        /// process IDs differs from the one seen at the last snapshot. A
        /// snapshot that fails leaves the flags as they were.</summary>
        public PeripheralPresence ReadRunning(PeripheralPresence current)
        {
            if (current == null) return null;
            lock (_sync)
            {
                uint[] ids = Ids();
                if (ids != null && _seen != null && ids.AsSpan().SequenceEqual(_seen)) return current;
                return PeripheralPresence.WithSnapshot(current, Snapshot(ids), current);
            }
        }

        /// <summary>The list of process IDs, or null when it could not be
        /// read. An empty list is one that could not be read: Windows
        /// always runs something, and two empty lists would compare equal.</summary>
        private uint[] Ids()
        {
            try
            {
                uint[] ids = _processIds();
                return ids == null || ids.Length == 0 ? null : ids;
            }
            catch { return null; }
        }

        /// <summary>One snapshot of the process names, with
        /// <paramref name="ids"/>, the list read just before it, kept as
        /// what the next fast read compares with. Caller holds the lock.</summary>
        private List<string> Snapshot(uint[] ids)
        {
            List<string> names;
            try { names = _imageNames(); }
            catch { names = null; }
            Interlocked.Increment(ref _snapshots);
            _seen = names == null ? null : ids;
            return names;
        }
    }

    /// <summary>The image names of the running processes, from
    /// <see cref="RunningProcessList"/>, in place of one
    /// Process.GetProcessesByName call per name, each of which walks every
    /// process on the PC and opens a handle to each match. Only the names
    /// are kept.</summary>
    internal static class RunningProcesses
    {
        /// <summary>Every running process's image name, "lghub_agent.exe",
        /// or null when the list could not be read.</summary>
        internal static List<string> ImageNames()
        {
            var running = RunningProcessList.Read();
            if (running == null) return null;
            var names = new List<string>(running.Count);
            foreach (var process in running)
            {
                string name = process.FileName;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names;
        }

        /// <summary>Every running process's ID, in the order Windows lists
        /// them, or null when the list could not be read
        /// (<see cref="RunningProcessList.Ids"/>).</summary>
        internal static uint[] ProcessIds() => RunningProcessList.Ids();
    }

    /// <summary>One device row as the linker sees it: an online mouse,
    /// keyboard or analog keyboard with its container ID, a vendor row, or a
    /// unit row.</summary>
    internal readonly record struct LinkRow(Guid Device, int CapType, ushort VendorId, ushort ProductId,
        Guid Container, PeripheralRowKind? VendorRow)
    {
        /// <summary>For a unit row (<see cref="PeripheralUnitRow"/>), the
        /// one unit it stands for. Null for every other row.</summary>
        public OutputPath? Unit { get; init; }
    }

    /// <summary>One device a direct backend found, as the linker needs it:
    /// its family, its key (the interface path its backend writes to), the
    /// container it sits in, and its vendor and product ID. A unit whose
    /// container is not known yet carries Guid.Empty there. It matches no
    /// row and gets no row of its own until its backend has looked the
    /// container up.</summary>
    internal sealed record DirectUnit(OutputFamily Family, string Key, Guid ContainerId, ushort VendorId, ushort ProductId)
    {
        /// <summary>The device's product name, or null when it gave none.</summary>
        public string Name { get; init; }

        /// <summary>LampArrayKind for a LampArray unit (1 keyboard, 2 mouse), 0 where the family has none.</summary>
        public int Kind { get; init; }

        /// <summary>Windows Dynamic Lighting lights it (LampArray units only).</summary>
        public bool WindowsLit { get; init; }

        /// <summary>The device's own controller stays in charge of its
        /// lights while Windows Dynamic Lighting holds a LampArray interface
        /// of the same device (<see cref="HidLightingModel.LitBesideWindows"/>).</summary>
        public bool LitBesideWindows { get; init; }

        /// <summary>The device's own controller keeps still pictures only,
        /// and a LampArray interface of the same device carries the colors
        /// that change (<see cref="HidLightingModel.LiveThroughLampArray"/>).</summary>
        public bool LiveThroughLampArray { get; init; }

        /// <summary>The programs that light the unit's device while they
        /// run, for a unit of the shared worker
        /// (<see cref="HidLightingModel.Programs"/>). The model's own list,
        /// the same one on every snapshot, so two snapshots of one unit stay
        /// equal. Empty for every other family, whose programs the linker
        /// knows by vendor ID (<see cref="PeripheralLinker.HasVendorProgram"/>).</summary>
        public IReadOnlyList<VendorProgram> Programs { get; init; } = Array.Empty<VendorProgram>();

        /// <summary>The unit lights the PC's own keyboard through the PC's
        /// firmware, as an ASUS TUF or HP Omen laptop's is, so it names no
        /// USB device: it belongs to the keyboard rows in the PC's own
        /// container (<see cref="PeripheralLinker.MatchesDevice"/>).</summary>
        public bool BuiltInKeyboard { get; init; }

        /// <summary>The highest claim level the unit offers: 0 colors
        /// alone, 1 its lighting power too, 2 its on-board memory mode off
        /// as well. 0 in every family but HidppUnit, whose units carry
        /// theirs (<see cref="HidppUnit.MaxClaimLevel"/>).</summary>
        public int MaxClaimLevel { get; init; }

        /// <summary>The unit is there and cannot be written to now: its
        /// channel was closed after a failed write, or it does not answer,
        /// a mouse asleep behind its dongle. Its backend lists it like this
        /// for as long as its collection is present, and never takes it off
        /// the list for a failed write. No path is linked to an asleep
        /// unit, and the rows that match it keep the outputs they had on
        /// record (<see cref="PeripheralLinker.StillLooking"/>), so their
        /// tabs stay up until it answers again.</summary>
        public bool Asleep { get; init; }

        public OutputPath Path => new(Family, Key);
    }

    /// <summary>What one link pass links from: the HID++ units, the vendor
    /// software present, and the units of every direct backend.</summary>
    internal sealed record LinkInputs(HidppSnapshot Hidpp, PeripheralPresence Presence)
    {
        public IReadOnlyList<DirectUnit> Units { get; init; } = Array.Empty<DirectUnit>();

        /// <summary>False until every direct backend has scanned once.</summary>
        public bool DirectScanned { get; init; } = true;
    }

    /// <summary>
    /// Matches device rows to output paths (#494). Pure, so every rule is
    /// testable without a device.
    ///
    /// <para>Lighting. A device is lit one of two ways: through its vendor's
    /// software, which lights every device of its kind at once (a Chroma
    /// category, an LED SDK device type, a GameSense device type, each named
    /// by the device's product ID where <see cref="PeripheralProductIds"/>
    /// lists it, so every row of one device lights as that device), or on
    /// its own, through a unit a direct backend found: a HID++ unit, a Razer
    /// or SteelSeries unit, or a HID LampArray unit. A row takes one family
    /// at a time, in the order <see cref="ChooseLighting"/> states, because
    /// two families would paint one device from two rulings. The vendor rows
    /// take every path of their family, which a device claiming the path on
    /// its own outranks (<see cref="PeripheralOutputs.Rules"/>).</para>
    ///
    /// <para>A mouse or keyboard row is one Raw Input interface. Its vendor
    /// channel is another collection of the same physical device, and the
    /// two share a container ID: on the bench's LIGHTSPEED receiver the mouse
    /// (MI_00), keyboard and consumer (MI_01) and HID++ (MI_02) collections
    /// all report one. A receiver gives one mouse row and one keyboard row
    /// however many devices are paired to it, so a row there takes the units
    /// of its own kind, from feature 0x0005's device type. A wired or
    /// Bluetooth device is the only unit in its container, so every row of
    /// it takes it, as every row of a Razer or SteelSeries device lights that
    /// device. A unit that gave no type goes to every row in its
    /// container.</para>
    /// </summary>
    internal static class PeripheralLinker
    {
        internal const ushort LogitechVid = 0x046D;
        internal const ushort RazerVid = 0x1532;
        internal const ushort SteelSeriesVid = 0x1038;

        /// <summary>The tactile mice GameSense drives: the Rival 500, 700 and
        /// 710 (gamesense-sdk standard-zones.md:49-52), by the product IDs
        /// rivalcfg lists (rival500.py:22, rival700.py:21 and 27).</summary>
        internal static readonly ushort[] RivalTactilePids = { 0x170E, 0x1700, 0x1730 };

        internal static readonly OutputPath GameSenseTactilePath = new(OutputFamily.GameSenseTactile, "tactile");
        internal static readonly OutputPath SensaPath = new(OutputFamily.Interhaptics, "sensa");

        /// <summary>The Chroma REST categories (Colore Rest/RestApi.cs, the
        /// official REST docs), in the order the init lists them.</summary>
        internal static readonly string[] ChromaCategories =
            { "keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink" };

        /// <summary>The LED SDK path for whole devices: the Logitech devices
        /// with no zones, the manual's G710+, G600, G510, G110, G19, G105,
        /// G300, G11, G13 and G15 (LogitechGamingLEDSDK.pdf pp. 9-18), lit
        /// with one color on the RGB and monochrome targets. Only the
        /// LIGHTSYNC row takes it, as the lightbar mirror lit them.</summary>
        internal const string LedSdkWholeDevices = "device";

        /// <summary>The LED SDK paths in paint order: the whole devices
        /// first, since that call reaches zonal devices too, then the device
        /// types a zone call addresses (the 9.00 header's LogiLed::DeviceType,
        /// logitech-led-sdk-rs bindings-x86_64.rs:136-142).</summary>
        internal static readonly string[] LedSdkTypes = { LedSdkWholeDevices, "keyboard", "mouse", "mousemat", "headset", "speaker" };

        /// <summary>The GameSense general device types a color reaches
        /// (gamesense-sdk standard-zones.md:5-17). The fourth, indicator, is
        /// a single status light such as the Sims 4 Plumbob, and stays
        /// out.</summary>
        internal static readonly string[] GameSenseColorTypes = { "mouse", "keyboard", "headset" };

        /// <summary>Razer keypads, which Windows lists as keyboards and Chroma
        /// lights as its keypad category: the Nostromo, the Tartarus family
        /// and the Orbweaver family (openrazer keyboards.py:74-273). Any row
        /// of one, its mouse collection included, lights the keypad.</summary>
        internal static readonly ushort[] RazerKeypadPids = { 0x0111, 0x0201, 0x0208, 0x022B, 0x0244, 0x0113, 0x0207 };

        /// <summary>The container every device built into the PC reports,
        /// the internal keyboard, its lighting and the touchpad alike, so a
        /// shared container says nothing about which of them a unit lights.</summary>
        internal static readonly Guid LocalMachineContainer = new("00000000-0000-0000-ffff-ffffffffffff");

        /// <summary>LampArrayKind 1 and 2, the two kinds a row can be told
        /// by (HID Usage Tables 1.6, 26.2.1).</summary>
        internal const int LampArrayKindKeyboard = 1;
        internal const int LampArrayKindMouse = 2;

        /// <summary>How long a lit unit must go unreached by any mouse
        /// or keyboard row before it gets a row of its own
        /// (<see cref="HeldOrphans"/>).</summary>
        internal const int UnitRowHoldMs = 5000;

        /// <summary>How long a row keeps what it had on record after the
        /// last unit of its own left every backend's list
        /// (<see cref="HeldRows"/>). A device that is unplugged loses its
        /// unit at the next walk of the present collections and its row at
        /// the input manager's next device scan, and either can come
        /// first. The link pass does not read an offline row, so a row
        /// that goes offline inside this time keeps its record and its
        /// tabs, as an unplugged gamepad's row does, whichever came first.
        /// The scan runs every 2000 ms (InputManager.EnumerationIntervalMs),
        /// and the last pass that saw the unit can be one pass,
        /// <see cref="PeripheralOutputHost.LinkMs"/>, older than the
        /// unplug, so the row is offline no later than 2500 ms after that
        /// pass plus the time the scan itself takes. Twice 2500 ms leaves
        /// the scan as long again on a slow PC.</summary>
        internal const int UnitGoneHoldMs = 5000;

        /// <summary>LampArray units whose firmware takes a raw host's frames
        /// and keeps painting its own effect over them, by vendor and product
        /// ID. Such a unit is linked to no row and gets no row of its own,
        /// since a color sent to it never shows. The Lenovo Legion Pro 5
        /// 16IAX10's keyboard, 048D:C195, is the one on record: "color
        /// writes accepted but firmware overrides" (hid-rgb-ctl issue 10).
        /// The entry goes if a run on that model holds a color.</summary>
        internal static readonly (ushort VendorId, ushort ProductId)[] LampArrayIgnoresRawHost =
        {
            (0x048D, 0xC195),
        };

        public static LinkTable Build(IEnumerable<LinkRow> rows, HidppSnapshot hidpp, PeripheralPresence presence)
            => Build(rows, new LinkInputs(hidpp, presence));

        public static LinkTable Build(IEnumerable<LinkRow> rows, LinkInputs inputs)
        {
            var hidpp = inputs.Hidpp ?? HidppSnapshot.Empty;
            var presence = inputs.Presence ?? PeripheralPresence.None;
            var direct = inputs.Units ?? Array.Empty<DirectUnit>();
            var lampArrays = LampArraysPerContainer(direct);
            var result = new List<DeviceLinks>();
            // A unit row's link reads every other row and what they link,
            // so the rows are listed once, the unit rows go last, and the
            // device census is taken at the first of them, from the links
            // of every other row.
            var rowList = rows as IReadOnlyList<LinkRow> ?? new List<LinkRow>(rows);
            var ordered = new List<LinkRow>(rowList.Count);
            foreach (var row in rowList)
                if (row.VendorRow != null || row.Unit == null) ordered.Add(row);
            foreach (var row in rowList)
                if (row.VendorRow == null && row.Unit != null) ordered.Add(row);
            DeviceFamilies families = null;
            foreach (var row in ordered)
            {
                var haptics = new List<OutputPath>();
                var lighting = new List<OutputPath>();
                var units = new List<string>();
                var shadows = Array.Empty<OutputPath>();
                int maxLevel = 0;
                bool directWhenClosed = false;
                bool windowsLightsLampArray = false;
                // The program a device of the shared worker is left to,
                // and whether the device's model names any.
                string leftToProgram = null;
                bool ownPrograms = false;
                // For a unit row, the vendor of the unit it stands for.
                ushort unitVendor = 0;
                if (row.VendorRow is PeripheralRowKind kind)
                {
                    switch (kind)
                    {
                        case PeripheralRowKind.RazerSensa when presence.SensaPlatform:
                            haptics.Add(SensaPath);
                            break;
                        case PeripheralRowKind.RazerChroma when presence.RazerSynapse:
                            foreach (var category in ChromaCategories)
                                lighting.Add(new OutputPath(OutputFamily.ChromaCategory, category));
                            break;
                        case PeripheralRowKind.LogitechLightsync when presence.LogitechLedSdk:
                            foreach (var type in LedSdkTypes)
                                lighting.Add(new OutputPath(OutputFamily.LedSdkType, type));
                            break;
                        case PeripheralRowKind.SteelSeriesGG when presence.SteelSeriesGG:
                            foreach (var type in GameSenseColorTypes)
                                lighting.Add(new OutputPath(OutputFamily.GameSenseColor, type));
                            break;
                    }
                }
                else if (row.Unit is OutputPath unit)
                {
                    // A unit row holds its own unit and nothing else, for as
                    // long as the unit is there and answers, its device's
                    // program does not run, and the device is not a device
                    // row's. The program drives the device from the pass
                    // that sees it, and a device row that reaches the unit,
                    // or lights its device through a unit of another family
                    // (DeviceFamilies), takes it from the pass that reaches
                    // it, so no path is held by two rows. The row itself
                    // retires only at the input manager's next device scan
                    // (Orphans).
                    if (unit.Family == OutputFamily.HidppUnit)
                    {
                        // A HID++ unit is listed by the HID++ worker, not
                        // among the direct units. Its row reads the unit's
                        // charge for the Battery mode as a mouse row does.
                        if (FindHidppUnit(unit.Key, hidpp) is HidppUnit own)
                        {
                            unitVendor = LogitechVid;
                            units.Add(own.Key);
                            families ??= DeviceFamilies.Of(direct, hidpp, result);
                            if (LightsUnitRow(own) && !VendorProgramRuns(LogitechVid, presence)
                                && !ReachedByAnyRow(own, rowList) && !families.Loses(UnitOf(own)))
                                lighting.Add(unit);
                        }
                    }
                    else if (FindUnit(unit, direct) is DirectUnit own)
                    {
                        unitVendor = own.VendorId;
                        ownPrograms = own.Programs is { Count: > 0 };
                        families ??= DeviceFamilies.Of(direct, hidpp, result);
                        if (LightsUnitRow(own) && !UnitProgramRuns(own, presence)
                            && !ReachedByAnyRow(own, rowList, lampArrays) && !families.Loses(own))
                            lighting.Add(unit);
                    }
                }
                else if (IsLinkable(row.CapType))
                {
                    var lamps = new List<Candidate>();
                    foreach (var candidate in direct)
                        if (candidate.Family == OutputFamily.LampArrayUnit && MatchesLampArray(candidate, row, lampArrays))
                            lamps.Add(new Candidate(candidate.Path, candidate.MaxClaimLevel, candidate.WindowsLit)
                            {
                                Asleep = candidate.Asleep,
                            });

                    // What the vendor's software would light the row
                    // through, what its own protocol reaches, whether the
                    // software runs, and whether its shared path waits for
                    // it while it does not.
                    var shared = new List<OutputPath>();
                    var native = new List<Candidate>();
                    bool programRuns = false, sharedWaits = false;
                    VendorProgram holder = null;
                    OutputPath? shadow = null;
                    if (row.VendorId == LogitechVid)
                    {
                        programRuns = presence.LogitechGHubRunning;
                        foreach (var hidppUnit in hidpp.Units)
                        {
                            if (!Matches(hidppUnit, row)) continue;
                            units.Add(hidppUnit.Key);
                            var path = new OutputPath(OutputFamily.HidppUnit, hidppUnit.Key);
                            if (hidppUnit.HasHaptics) haptics.Add(path);
                            if (!hidppUnit.HasRgb) continue;
                            // The unit's own kind where it gave one, so every
                            // row of a device lights that device's type.
                            if (presence.LogitechLedSdk)
                                AddOnce(shared, new OutputPath(OutputFamily.LedSdkType, LedSdkTypeOf(hidppUnit, row)));
                            if (hidppUnit.HasLightPlan)
                                native.Add(new Candidate(path, hidppUnit.MaxClaimLevel, false));
                        }
                    }
                    else if (row.VendorId == RazerVid)
                    {
                        programRuns = presence.RazerSoftwareRunning;
                        sharedWaits = true;
                        var category = new OutputPath(OutputFamily.ChromaCategory, RazerCategory(row));
                        if (presence.RazerSynapse) shared.Add(category);
                        AddOwnUnits(native, direct, OutputFamily.RazerUnit, row);
                        // The Chroma session must not paint the category of a
                        // mouse this row lights on its own.
                        shadow = category;
                    }
                    else if (row.VendorId == SteelSeriesVid)
                    {
                        programRuns = presence.SteelSeriesGGRunning;
                        sharedWaits = true;
                        bool keyboard = PeripheralProductIds.LightsAsKeyboard(row.ProductId, row.CapType,
                            PeripheralProductIds.SteelSeriesMice, PeripheralProductIds.SteelSeriesKeyboards);
                        if (presence.SteelSeriesGG)
                        {
                            shared.Add(new OutputPath(OutputFamily.GameSenseColor, keyboard ? "keyboard" : "mouse"));
                            // Every row of a tactile Rival is that mouse, so
                            // each one takes its rumble, whichever path
                            // carries its color.
                            if (!keyboard && Array.IndexOf(RivalTactilePids, row.ProductId) >= 0)
                                haptics.Add(GameSenseTactilePath);
                        }
                        AddOwnUnits(native, direct, OutputFamily.SteelSeriesUnit, row);
                    }

                    // A device of an OpenRGB family the shared worker
                    // lights, whatever its vendor: a Lenovo Legion keyboard,
                    // and under the three vendors above a Logitech G600 or
                    // the SteelSeries controller of an MSI laptop's keyboard
                    // (OpenRGB LogitechControllerDetect.cpp:1547,
                    // MSILaptopControllerDetect.cpp:330-333), which the
                    // vendor's program rule above holds too. Its model names
                    // the programs that light it, and while one of them runs
                    // the device is that program's, by the rule the three
                    // vendors follow.
                    AddOwnUnits(native, direct, OutputFamily.HidLightingUnit, row);
                    holder = ProgramHolding(direct, row, presence, out ownPrograms);
                    programRuns |= holder != null;
                    // A program with an SDK route of PadForge's own (#557
                    // item 11) adds that route's path to shared here. The row
                    // is then lit through it while the program runs, as a
                    // Razer row is lit through Synapse, and its line says the
                    // device is lit on its own once the program is closed
                    // (directWhenClosed). With no such path the device is left
                    // to the program (leftToProgram below).

                    // The family is chosen as if every unit answered, and
                    // only the units that do are linked. A row whose own
                    // unit sleeps then holds no path, where falling through
                    // to the path that waits for the vendor's program would
                    // name the wrong wait on its tab, and a unit Windows
                    // lights keeps the device's own protocol closed whether
                    // or not it answers.
                    var chosen = ChooseLighting(programRuns, shared, sharedWaits, native, lamps, out directWhenClosed);
                    // A program of the device's model runs and PadForge
                    // has no path through it: the row holds no path, keeps
                    // its Lighting tab, and the tab names the program.
                    if (chosen == LightingChoice.None && holder != null) leftToProgram = holder.Name;
                    if (chosen == LightingChoice.Shared) lighting.AddRange(shared);
                    else if (chosen != LightingChoice.None)
                    {
                        // The device's own units go first, so the Key Colors
                        // card shows their lights (PeripheralLightMaps.FirstPerLight).
                        int own = 0;
                        if (chosen is LightingChoice.Native or LightingChoice.NativeAndLampArray)
                            own = LinkAwake(native, lighting, ref maxLevel);
                        // The LampArray units ride with the device's own: a
                        // row whose own unit sleeps holds no path, as under
                        // Native, and the card never moves to their lights.
                        if (chosen == LightingChoice.LampArray || (chosen == LightingChoice.NativeAndLampArray && own > 0))
                            LinkAwake(lamps, lighting, ref maxLevel);
                        if (own > 0 && shadow is OutputPath stoodInFor)
                            shadows = new[] { stoodInFor };
                        windowsLightsLampArray = chosen == LightingChoice.Native
                                                 && lamps.Exists(l => l.WindowsLit) && native.Exists(c => c.BesideWindows);
                    }

                    // A TouchSense mouse bound to WinUSB rumbles through its
                    // own unit, and nothing lights through it.
                    foreach (var candidate in direct)
                        if (candidate.Family == OutputFamily.TouchSenseUnit && !candidate.Asleep && MatchesDevice(candidate, row))
                            AddOnce(haptics, candidate.Path);
                }

                if (haptics.Count == 0 && lighting.Count == 0 && leftToProgram == null) continue;
                result.Add(new DeviceLinks
                {
                    Device = row.Device,
                    Haptics = haptics.ToArray(),
                    Lighting = lighting.ToArray(),
                    HidppUnits = units.ToArray(),
                    CatchAll = row.VendorRow != null,
                    UnitRow = row.Unit != null,
                    MaxClaimLevel = maxLevel,
                    DirectWhenClosed = directWhenClosed,
                    WindowsLightsLampArray = windowsLightsLampArray,
                    Shadows = shadows,
                    HasVendorProgram = row.VendorRow == null
                                       && (ownPrograms || HasVendorProgram(row.Unit == null ? row.VendorId : unitVendor)),
                    LeftToProgram = leftToProgram,
                });
            }
            return new LinkTable(result);
        }

        /// <summary>Whether a vendor has a program that drives every device
        /// of that vendor while it runs: Logitech (G HUB and Logitech
        /// Gaming Software), Razer (Synapse) and SteelSeries (GG), the three
        /// whose running flag decides a row's path
        /// (<see cref="ChooseLighting"/>). A path that lights one of their
        /// devices on its own is withdrawn when that program starts.</summary>
        internal static bool HasVendorProgram(ushort vendorId)
            => vendorId == LogitechVid || vendorId == RazerVid || vendorId == SteelSeriesVid;

        /// <summary>Whether the program of a unit's vendor runs now, for
        /// the three vendors that have one (<see cref="HasVendorProgram"/>).
        /// The one read behind a unit's row and its path
        /// (<see cref="Orphans"/> and
        /// <see cref="Build(IEnumerable{LinkRow}, LinkInputs)"/>).</summary>
        internal static bool VendorProgramRuns(ushort vendorId, PeripheralPresence presence) => vendorId switch
        {
            LogitechVid => presence.LogitechGHubRunning,
            RazerVid => presence.RazerSoftwareRunning,
            SteelSeriesVid => presence.SteelSeriesGGRunning,
            _ => false,
        };

        /// <summary>Whether a program that drives a direct unit's device
        /// runs now: its vendor's, for the three vendors that have one
        /// (<see cref="VendorProgramRuns"/>), or one its shared-worker
        /// model names (<see cref="DirectUnit.Programs"/>,
        /// <see cref="ProgramHolding"/>). The one read behind a unit row's
        /// path and behind whether the unit gets a row
        /// (<see cref="Orphans"/>).</summary>
        internal static bool UnitProgramRuns(DirectUnit unit, PeripheralPresence presence)
        {
            if (unit == null || presence == null) return false;
            if (VendorProgramRuns(unit.VendorId, presence)) return true;
            return unit.Programs is { Count: > 0 } programs
                   && (presence.Programs ?? RunningPrograms.None).FirstOf(programs) != null;
        }

        /// <summary>One path a row could be lit through on its own, with the
        /// claim level its unit offers and whether Windows lights it.</summary>
        internal readonly record struct Candidate(OutputPath Path, int Level, bool WindowsLit)
        {
            /// <summary>The unit does not answer now
            /// (<see cref="DirectUnit.Asleep"/>). It still counts toward
            /// which family lights the row, and no path is linked to it.</summary>
            public bool Asleep { get; init; }

            /// <summary>A native unit whose controller stays in charge of
            /// the device's lights while Windows Dynamic Lighting holds its
            /// LampArray interface (<see cref="DirectUnit.LitBesideWindows"/>).</summary>
            public bool BesideWindows { get; init; }

            /// <summary>A native unit whose controller keeps still pictures
            /// only, so a LampArray unit of the device lights the row beside
            /// it (<see cref="DirectUnit.LiveThroughLampArray"/>).</summary>
            public bool LiveThroughLampArray { get; init; }
        }

        internal enum LightingChoice
        {
            None,
            /// <summary>The vendor software's path.</summary>
            Shared,
            /// <summary>The device's own protocol: HID++, Razer, SteelSeries.</summary>
            Native,
            LampArray,
            /// <summary>The device's own protocol and its LampArray units
            /// together (<see cref="Candidate.LiveThroughLampArray"/>).</summary>
            NativeAndLampArray,
        }

        /// <summary>
        /// Which family lights a row, the same order for every vendor.
        /// Every unit of the row counts here, asleep or not
        /// (<see cref="DirectUnit.Asleep"/>): the family is the device's,
        /// and <see cref="Build(IEnumerable{LinkRow}, LinkInputs)"/> then
        /// links only the units of it that answer.
        ///
        /// <para>1. The vendor's program runs, or for a device of the
        /// shared worker a program its model names
        /// (<see cref="ProgramHolding"/>): its shared path, when its SDK
        /// is installed, and nothing else. The program drives every device
        /// of its vendor, and two programs driving one device fight over it
        /// (uncoil apps/uncoild/src/conflicts.rs:34-37, which tells the user
        /// to quit Synapse). <paramref name="directWhenClosed"/> is then set
        /// when the row would be lit on its own without the program, for
        /// the tab's line.</para>
        ///
        /// <para>2. Windows Dynamic Lighting lights a LampArray unit of the
        /// device: the LampArray paths, which their backend leaves unpainted
        /// while Windows lights them, and no native path. A device Windows
        /// lights has a second writer whichever door a color comes in by.
        /// Logitech's LampArray driver, which Windows Update installs,
        /// repaints a mouse over HID++ about twice a second, and a 0x8070
        /// write beside it lasts one sample (OpenLogi issue 1129, where
        /// SetSWControl made no difference). The row keeps its tab, whose
        /// line names Windows and the switch. Not where the device's own
        /// controller stays in charge beside Windows
        /// (<see cref="Candidate.BesideWindows"/>): a Lenovo Legion keyboard
        /// with Dynamic Lighting off in its BIOS takes no colors on its
        /// LampArray interface while Windows' switch for it reads on, and
        /// OpenRGB and Lenovo Legion Toolkit light it through its own
        /// controller (<see cref="LenovoLegionLighting"/>). Step 4 then
        /// lights it.</para>
        ///
        /// <para>3. The device's own controller keeps still pictures only
        /// (<see cref="Candidate.LiveThroughLampArray"/>), and the device
        /// has a LampArray unit Windows does not light: both. A Lenovo
        /// Legion Gen 10 keyboard stores each picture in a lighting profile
        /// and shows no stream of them (<see cref="LenovoLegionLighting"/>).
        /// With Dynamic Lighting on in its BIOS, its LampArray interface
        /// shows every frame, and while a host holds that interface "No
        /// source other than the Host can modify Lamp state" (HID Usage
        /// Tables 1.6, 26.10.1), so the stored picture stays out of sight.
        /// With Dynamic Lighting off, the interface takes no colors and the
        /// stored picture shows. Nothing the keyboard reports tells the two
        /// apart, so both carry the row's colors, and each shows them where
        /// the firmware lets it. OpenRGB drives both as well: its LampArray
        /// detector takes every collection on page 0x59, usage 0x01, with
        /// no vendor left out
        /// (Controllers/HIDLampArrayController/HIDLampArrayControllerDetect.cpp:50).
        /// The LampArray units ride with the device's own unit and are
        /// never linked without it.</para>
        ///
        /// <para>4. The device's own protocol. For a Logitech device that is
        /// HID++ ahead of any LampArray unit, because every reference that
        /// lights a 0x8071 or per-key Logitech device speaks HID++ to it,
        /// none drives Logitech's virtual LampArray unit from a host other
        /// than Windows, and OpenRGB keeps its LampArray detector off
        /// Logitech nodes (OpenRGB
        /// Controllers/LogitechController/LogitechControllerDetect.cpp:1771-1774).
        /// The native path is the proven one, so it comes first.</para>
        ///
        /// <para>5. A LampArray unit of the device.</para>
        ///
        /// <para>6. The shared path again, where it waits for its program
        /// (<paramref name="sharedWaits"/>): a Razer or SteelSeries model
        /// with no unit of its own keeps its tab and its "Waiting for" line.
        /// A Logitech row has no such step, since the LED SDK path is given
        /// only while G HUB runs.</para>
        /// </summary>
        internal static LightingChoice ChooseLighting(bool programRuns, IReadOnlyList<OutputPath> shared, bool sharedWaits,
            IReadOnlyList<Candidate> native, IReadOnlyList<Candidate> lamps, out bool directWhenClosed)
        {
            directWhenClosed = false;
            bool windowsLit = false;
            foreach (var lamp in lamps) windowsLit |= lamp.WindowsLit;
            bool besideWindows = false, live = false;
            foreach (var candidate in native)
            {
                besideWindows |= candidate.BesideWindows;
                live |= candidate.LiveThroughLampArray;
            }
            if (programRuns)
            {
                if (shared.Count == 0) return LightingChoice.None;
                directWhenClosed = !windowsLit && (native.Count > 0 || lamps.Count > 0);
                return LightingChoice.Shared;
            }
            if (windowsLit && !besideWindows) return LightingChoice.LampArray;
            if (live && !windowsLit && lamps.Count > 0) return LightingChoice.NativeAndLampArray;
            if (native.Count > 0) return LightingChoice.Native;
            if (lamps.Count > 0) return LightingChoice.LampArray;
            return sharedWaits && shared.Count > 0 ? LightingChoice.Shared : LightingChoice.None;
        }

        private static void AddOwnUnits(List<Candidate> native, IReadOnlyList<DirectUnit> direct, OutputFamily family, LinkRow row)
        {
            foreach (var unit in direct)
                if (unit.Family == family && MatchesDevice(unit, row))
                    native.Add(new Candidate(unit.Path, unit.MaxClaimLevel, false)
                    {
                        Asleep = unit.Asleep,
                        BesideWindows = unit.LitBesideWindows,
                        LiveThroughLampArray = unit.LiveThroughLampArray,
                    });
        }

        /// <summary>The program that has a row's shared-worker device now:
        /// the first program that runs among those named by a unit of the
        /// row's own device (<see cref="DirectUnit.Programs"/>), asleep or
        /// not, since the program has the device either way. Null when none
        /// runs. <paramref name="named"/> says whether any such unit names a
        /// program at all, which makes the row one the link pass watches
        /// for a starting program while it is claimed
        /// (<see cref="DeviceLinks.HasVendorProgram"/>).</summary>
        internal static VendorProgram ProgramHolding(IReadOnlyList<DirectUnit> direct, LinkRow row,
            PeripheralPresence presence, out bool named)
        {
            named = false;
            VendorProgram holder = null;
            var running = presence?.Programs ?? RunningPrograms.None;
            foreach (var unit in direct)
            {
                if (unit.Family != OutputFamily.HidLightingUnit || !MatchesDevice(unit, row)) continue;
                if (unit.Programs == null || unit.Programs.Count == 0) continue;
                named = true;
                holder ??= running.FirstOf(unit.Programs);
            }
            return holder;
        }

        /// <summary>Links the candidates that answer now
        /// (<see cref="Candidate.Asleep"/>) and returns how many it
        /// linked.</summary>
        private static int LinkAwake(List<Candidate> candidates, List<OutputPath> lighting, ref int maxLevel)
        {
            int linked = 0;
            foreach (var candidate in candidates)
            {
                if (candidate.Asleep) continue;
                AddOnce(lighting, candidate.Path);
                maxLevel = Math.Max(maxLevel, candidate.Level);
                linked++;
            }
            return linked;
        }

        /// <summary>A Razer, SteelSeries or TouchSense unit belongs to the
        /// rows of its own device: the same container, and the same vendor
        /// and product ID, which every collection of one USB device reports.
        /// The pair also tells the built-in devices of a laptop apart, which
        /// all share one container. A unit that lights the PC's own
        /// keyboard through the PC's firmware (<see cref="DirectUnit.BuiltInKeyboard"/>)
        /// has no pair to give, and goes to the keyboard rows in the PC's
        /// own container, where a built-in LampArray unit goes too
        /// (<see cref="MatchesLampArray"/>).</summary>
        internal static bool MatchesDevice(DirectUnit unit, LinkRow row)
            => IsLinkable(row.CapType) && row.VendorRow == null && row.Unit == null
               && row.Container != Guid.Empty && unit.ContainerId == row.Container
               && (unit.BuiltInKeyboard
                   ? row.Container == LocalMachineContainer && IsKeyboard(row.CapType)
                   : unit.VendorId == row.VendorId && unit.ProductId == row.ProductId);

        /// <summary>How many LampArray units each container holds, which
        /// decides whether a unit there is told from its neighbors by
        /// kind. A unit that ignores a raw host still counts: it is there.</summary>
        internal static Dictionary<Guid, int> LampArraysPerContainer(IReadOnlyList<DirectUnit> direct)
        {
            var counts = new Dictionary<Guid, int>();
            foreach (var unit in direct)
            {
                if (unit.Family != OutputFamily.LampArrayUnit || unit.ContainerId == Guid.Empty) continue;
                counts.TryGetValue(unit.ContainerId, out int count);
                counts[unit.ContainerId] = count + 1;
            }
            return counts;
        }

        /// <summary>
        /// Whether a LampArray unit lights a row. Both sit in one container.
        ///
        /// <para>The only unit in its container is that device's lighting.
        /// Outside the PC it goes to every row of the device, as a wired
        /// HID++ unit does (<see cref="Matches"/>). In the PC's own
        /// container it goes to the keyboard rows whatever kind it declares:
        /// every built-in device shares that container, the lit one is the
        /// keyboard, and built-in arrays declare kinds their own tools call
        /// misleading (keybackcon docs/architecture/protocol.md:24).</para>
        ///
        /// <para>Several units in one container are a receiver, a device
        /// with several lit parts, or a laptop with more than a lit
        /// keyboard. Kind 1 then goes to the keyboard rows, kind 2 to the
        /// mouse rows and any other kind to no row, since nothing else tells
        /// the units apart.</para>
        ///
        /// <para>A unit that ignores a raw host
        /// (<see cref="LampArrayIgnoresRawHost"/>) lights no row.</para>
        /// </summary>
        internal static bool MatchesLampArray(DirectUnit unit, LinkRow row, IReadOnlyDictionary<Guid, int> lampArrays)
        {
            if (!IsLinkable(row.CapType) || row.VendorRow != null || row.Unit != null) return false;
            if (row.Container == Guid.Empty || unit.ContainerId != row.Container) return false;
            if (IgnoresRawHost(unit)) return false;
            lampArrays.TryGetValue(unit.ContainerId, out int count);
            if (count <= 1) return unit.ContainerId != LocalMachineContainer || IsKeyboard(row.CapType);
            return unit.Kind == LampArrayKindKeyboard ? IsKeyboard(row.CapType)
                : unit.Kind == LampArrayKindMouse && row.CapType == InputDeviceType.Mouse;
        }

        internal static bool IgnoresRawHost(DirectUnit unit)
        {
            if (unit.Family != OutputFamily.LampArrayUnit) return false;
            foreach (var (vendorId, productId) in LampArrayIgnoresRawHost)
                if (unit.VendorId == vendorId && unit.ProductId == productId) return true;
            return false;
        }

        /// <summary>The unit a unit row stands for, as its backend lists
        /// it, or null.</summary>
        private static DirectUnit FindUnit(OutputPath unit, IReadOnlyList<DirectUnit> direct)
        {
            foreach (var candidate in direct)
                if (candidate.Family == unit.Family && candidate.Key == unit.Key) return candidate;
            return null;
        }

        /// <summary>The HID++ unit a unit row stands for, as the HID++
        /// worker lists it, or null.</summary>
        private static HidppUnit FindHidppUnit(string key, HidppSnapshot hidpp)
        {
            foreach (var candidate in hidpp.Units)
                if (candidate != null && candidate.Key == key) return candidate;
            return null;
        }

        /// <summary>Whether a unit row's unit is there to light: awake, of
        /// a family that lights, and not one that ignores a raw host.</summary>
        private static bool LightsUnitRow(DirectUnit unit)
            => unit.Family != OutputFamily.TouchSenseUnit && !unit.Asleep && !IgnoresRawHost(unit);

        /// <summary>Whether a HID++ unit can be lit on its own: the units a
        /// Logitech row is given a HID++ lighting path to
        /// (<see cref="HidppUnit.HasLightPlan"/>). The worker lists a unit
        /// only while it answers, so a listed one is awake.</summary>
        private static bool LightsUnitRow(HidppUnit unit) => unit.HasRgb && unit.HasLightPlan;

        /// <summary>Whether a unit that is listed and does not answer now
        /// (<see cref="DirectUnit.Asleep"/>) is one of this row's: the unit
        /// a unit row stands for, a LampArray unit the row would be lit
        /// through (<see cref="MatchesLampArray"/>), or a Razer, SteelSeries
        /// or TouchSense unit of the row's own device
        /// (<see cref="MatchesDevice"/>). Such a row waits for its unit, so
        /// it keeps what it had on record.</summary>
        internal static bool MatchesAsleepUnit(LinkRow row, IReadOnlyList<DirectUnit> direct)
        {
            Dictionary<Guid, int> lampArrays = null;
            return MatchesUnit(row, direct, asleepOnly: true, ref lampArrays);
        }

        /// <summary>Whether a listed unit, awake or asleep, is one of this
        /// row's, by the same three ties
        /// (<see cref="MatchesAsleepUnit"/>).</summary>
        internal static bool MatchesAnyUnit(LinkRow row, IReadOnlyList<DirectUnit> direct)
        {
            Dictionary<Guid, int> lampArrays = null;
            return MatchesUnit(row, direct, asleepOnly: false, ref lampArrays);
        }

        /// <summary>The one test behind both. <paramref name="lampArrays"/>
        /// is counted on first need and kept by the caller, so a pass that
        /// asks for every row counts the containers once.</summary>
        private static bool MatchesUnit(LinkRow row, IReadOnlyList<DirectUnit> direct, bool asleepOnly,
            ref Dictionary<Guid, int> lampArrays)
        {
            if (direct == null || row.VendorRow != null) return false;
            foreach (var unit in direct)
            {
                if (asleepOnly && !unit.Asleep) continue;
                if (row.Unit is OutputPath own)
                {
                    if (unit.Family == own.Family && unit.Key == own.Key && !IgnoresRawHost(unit)) return true;
                }
                else if (unit.Family == OutputFamily.LampArrayUnit)
                {
                    lampArrays ??= LampArraysPerContainer(direct);
                    if (MatchesLampArray(unit, row, lampArrays)) return true;
                }
                else if (MatchesDevice(unit, row))
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly HashSet<Guid> NoRows = new();

        /// <summary>The rows whose last unit left every backend's list less
        /// than <paramref name="holdMs"/> ago, which keep what they had on
        /// record that long (<see cref="UnitGoneHoldMs"/>).
        /// <paramref name="lastMatched"/> is the caller's record of when
        /// each row was last matched by a listed unit of its own, awake or
        /// asleep (<see cref="MatchesAnyUnit"/>). A row is on it from its
        /// first match until its hold has run out, whether or not it is
        /// among <paramref name="rows"/> meanwhile, since an unplugged
        /// device's row goes offline and is read no more. A row no unit
        /// ever matched is never held, and neither is a vendor row, which
        /// no unit matches. With no unit listed and nothing on record the
        /// answer costs nothing.</summary>
        internal static IReadOnlySet<Guid> HeldRows(Dictionary<Guid, long> lastMatched, IReadOnlyList<LinkRow> rows,
            IReadOnlyList<DirectUnit> direct, long now, int holdMs = UnitGoneHoldMs)
        {
            direct ??= Array.Empty<DirectUnit>();
            if (direct.Count == 0 && lastMatched.Count == 0) return NoRows;

            // The rows a listed unit matches now: their clocks move on.
            HashSet<Guid> matched = null;
            if (direct.Count > 0)
            {
                Dictionary<Guid, int> lampArrays = null;
                foreach (var row in rows)
                {
                    if (!MatchesUnit(row, direct, asleepOnly: false, ref lampArrays)) continue;
                    lastMatched[row.Device] = now;
                    (matched ??= new HashSet<Guid>()).Add(row.Device);
                }
            }

            // Every other row on record is held until its hold is over,
            // and then leaves the record.
            HashSet<Guid> held = null;
            List<Guid> over = null;
            foreach (var pair in lastMatched)
            {
                if (matched != null && matched.Contains(pair.Key)) continue;
                if (now - pair.Value >= holdMs) (over ??= new List<Guid>()).Add(pair.Key);
                else (held ??= new HashSet<Guid>()).Add(pair.Key);
            }
            if (over != null) foreach (var device in over) lastMatched.Remove(device);
            return held ?? NoRows;
        }

        /// <summary>
        /// The lit units no mouse, keyboard or analog keyboard row reaches:
        /// a headset, a mousepad, a headset stand, a dock, a light strip, a
        /// speaker, a monitor, a chassis light. Each gets a row of its own
        /// (<see cref="PeripheralUnitRow"/>), since a device with no row
        /// cannot be assigned, and a device that cannot be assigned is
        /// never lit. Every family whose units stand for one device each
        /// takes part: LampArray, Razer, SteelSeries, the shared worker's,
        /// and HID++. TouchSense units carry rumble alone and no row is
        /// opened for rumble.
        ///
        /// <para>A unit is reached when a row takes it by the rule its
        /// family links by in
        /// <see cref="Build(IEnumerable{LinkRow}, LinkInputs)"/>
        /// (<see cref="Reaches(LinkRow, DirectUnit, IReadOnlyDictionary{Guid, int})"/>),
        /// whichever path that row ends up lit through. A unit whose
        /// container is not known yet is not one: its row may be a lookup
        /// away. Neither is a LampArray unit that ignores a raw host.</para>
        ///
        /// <para>A unit is not one while a program that drives its device
        /// runs: its vendor's for a Logitech, Razer or SteelSeries device,
        /// or one the unit's shared-worker model names
        /// (<see cref="UnitProgramRuns"/>). The program drives the device
        /// then, a headset or a mousepad included, and the vendor's own row
        /// is how such a device is lit through it, so a row of the unit's
        /// own would have PadForge write beside the program, the fight a
        /// device row is kept out of by the same running flag
        /// (<see cref="ChooseLighting"/>). A row the unit already has holds
        /// no path from the pass that sees the program and retires at the
        /// input manager's next device scan, and the unit waits out
        /// <see cref="UnitRowHoldMs"/> again once the program has
        /// stopped.</para>
        ///
        /// <para>A unit that is asleep (<see cref="DirectUnit.Asleep"/>) is
        /// one only while it already has a row, so that row stays open
        /// while it sleeps and no row is opened for a device that has not
        /// answered. The HID++ worker lists a unit only while it answers,
        /// so a HID++ unit's row retires while the unit sleeps and comes
        /// back, under the same id and with the same assignment, once it
        /// answers again.</para>
        ///
        /// <para>One device can have units of two families, a LampArray
        /// collection beside its vendor's own. A row lights such a device
        /// through one family at a time (<see cref="ChooseLighting"/>), and
        /// a unit row beside it would paint it from a second ruling, so of
        /// the units of one device only the family that rule chooses gets
        /// rows, whether or not a device row reaches the units of that
        /// family (<see cref="DeviceFamilies"/>). A LampArray strip beside
        /// keys a keyboard row lights through its vendor's unit gets
        /// none.</para>
        /// </summary>
        internal static List<DirectUnit> Orphans(IReadOnlyList<LinkRow> rows, LinkInputs inputs)
        {
            var orphans = new List<DirectUnit>();
            var direct = inputs.Units ?? Array.Empty<DirectUnit>();
            var presence = inputs.Presence ?? PeripheralPresence.None;
            var hidpp = inputs.Hidpp ?? HidppSnapshot.Empty;
            var lampArrays = LampArraysPerContainer(direct);
            // What the device rows link, which decides a device's family
            // ahead of the census.
            var deviceRows = new List<LinkRow>(rows.Count);
            foreach (var row in rows)
                if (row.Unit == null) deviceRows.Add(row);
            var families = DeviceFamilies.Of(direct, hidpp, Build(deviceRows, inputs).ByDevice.Values);
            foreach (var unit in direct)
            {
                if (unit == null || !GetsUnitRow(unit.Family) || unit.ContainerId == Guid.Empty || IgnoresRawHost(unit))
                    continue;
                if (UnitProgramRuns(unit, presence) || families.Loses(unit)) continue;
                bool reached = false;
                foreach (var row in rows)
                {
                    if (!Reaches(row, unit, lampArrays)) continue;
                    reached = true;
                    break;
                }
                if (!reached) orphans.Add(unit);
            }
            if (!VendorProgramRuns(LogitechVid, presence))
            {
                foreach (var unit in hidpp.Units)
                {
                    if (unit == null || !LightsUnitRow(unit) || unit.ContainerId == Guid.Empty) continue;
                    bool reached = false;
                    foreach (var row in rows)
                    {
                        if (!Reaches(row, unit)) continue;
                        reached = true;
                        break;
                    }
                    if (reached) continue;
                    var listed = UnitOf(unit);
                    if (!families.Loses(listed)) orphans.Add(listed);
                }
            }
            // The family is chosen as if every unit answered, as a row's
            // is, so a device whose own unit sleeps does not hand its row
            // to its LampArray unit and take it back when it wakes. Then a
            // unit asleep keeps the row it has and gets none it has not.
            orphans.RemoveAll(unit => unit.Asleep && !HasUnitRow(rows, unit.Path));
            return orphans;
        }

        /// <summary>Whether a row other than a unit row takes a direct unit
        /// (<see cref="Reaches(LinkRow, DirectUnit, IReadOnlyDictionary{Guid, int})"/>),
        /// which is then that row's to light and no unit row's.</summary>
        private static bool ReachedByAnyRow(DirectUnit unit, IReadOnlyList<LinkRow> rows,
            IReadOnlyDictionary<Guid, int> lampArrays)
        {
            foreach (var row in rows)
                if (Reaches(row, unit, lampArrays)) return true;
            return false;
        }

        /// <summary>Whether a row other than a unit row takes a HID++
        /// unit (<see cref="Reaches(LinkRow, HidppUnit)"/>).</summary>
        private static bool ReachedByAnyRow(HidppUnit unit, IReadOnlyList<LinkRow> rows)
        {
            foreach (var row in rows)
                if (Reaches(row, unit)) return true;
            return false;
        }

        /// <summary>The families whose units get rows of their own
        /// (<see cref="Orphans"/>): every one that lights a device through
        /// a unit of its own. HID++ units are not direct units and are
        /// read from the HID++ snapshot.</summary>
        internal static bool GetsUnitRow(OutputFamily family)
            => family is OutputFamily.LampArrayUnit or OutputFamily.RazerUnit or OutputFamily.SteelSeriesUnit
                or OutputFamily.HidLightingUnit;

        /// <summary>Whether a row takes a direct unit by the rule its family
        /// links by in <see cref="Build(IEnumerable{LinkRow}, LinkInputs)"/>:
        /// a LampArray unit by <see cref="MatchesLampArray"/>, a Razer or
        /// SteelSeries unit by <see cref="MatchesDevice"/> from a row of
        /// that vendor, and a shared-worker unit by
        /// <see cref="MatchesDevice"/> from a row of any vendor, the rows
        /// the linker reads that family for. A laptop keyboard the
        /// shared worker lights through the PC's firmware is taken by the
        /// keyboard rows in the PC's own container
        /// (<see cref="DirectUnit.BuiltInKeyboard"/>).</summary>
        internal static bool Reaches(LinkRow row, DirectUnit unit, IReadOnlyDictionary<Guid, int> lampArrays)
            => unit.Family switch
            {
                OutputFamily.LampArrayUnit => MatchesLampArray(unit, row, lampArrays),
                OutputFamily.RazerUnit => row.VendorId == RazerVid && MatchesDevice(unit, row),
                OutputFamily.SteelSeriesUnit => row.VendorId == SteelSeriesVid && MatchesDevice(unit, row),
                OutputFamily.HidLightingUnit => MatchesDevice(unit, row),
                _ => false,
            };

        /// <summary>Whether a row takes a HID++ unit: a Logitech mouse or
        /// keyboard row the unit matches by container and kind
        /// (<see cref="Matches"/>), the rows the linker reads HID++ units
        /// for.</summary>
        internal static bool Reaches(LinkRow row, HidppUnit unit)
            => IsLinkable(row.CapType) && row.VendorRow == null && row.Unit == null
               && row.VendorId == LogitechVid && Matches(unit, row);

        /// <summary>A HID++ unit as a unit row needs it: its path, its
        /// container, Logitech's vendor ID and the name the device gave
        /// through feature 0x0005. The worker knows no product ID for a
        /// unit, which may sit behind a receiver, so it carries 0, and the
        /// row is named by the unit's own name.</summary>
        internal static DirectUnit UnitOf(HidppUnit unit)
            => new(OutputFamily.HidppUnit, unit.Key, unit.ContainerId, LogitechVid, 0) { Name = unit.Name };

        /// <summary>Whether a unit row standing for this path is among the
        /// rows.</summary>
        private static bool HasUnitRow(IReadOnlyList<LinkRow> rows, OutputPath path)
        {
            foreach (var row in rows)
                if (row.Unit == path) return true;
            return false;
        }

        /// <summary>
        /// Which family each device outside the PC's own container is lit
        /// through on its own: the units of a LampArray collection, or the
        /// units of its vendor's own protocol (Razer, SteelSeries, the
        /// shared worker, HID++). A family a device row links a unit of is
        /// the device's: its own protocol where the row links one of its
        /// units (step 3 links LampArray units beside it), else the
        /// LampArray units where the row links one of those. A device no
        /// row lights is counted over every lit unit of it, reached by a
        /// row or not, asleep or not, and whether or not a program holds
        /// it, in <see cref="ChooseLighting"/>'s order.
        /// Windows Dynamic Lighting lights a LampArray unit of the device,
        /// and no unit of its own stays in charge beside Windows
        /// (<see cref="DirectUnit.LitBesideWindows"/>): the LampArray units,
        /// which their backend leaves unpainted while Windows holds them
        /// (step 2). Otherwise the device's own protocol (step 4), the order
        /// OpenRGB keeps too: a device node a vendor's detector matches by
        /// vendor and product ID is offered to no generic detector, and its
        /// LampArray detector is a generic one (OpenRGB
        /// DetectionManager.cpp:123-127, 292-299 and 1533-1596,
        /// HIDLampArrayControllerDetect.cpp:50). A unit of the other family
        /// gets no row of its own, since the device's row, or the unit row
        /// of the chosen family, lights the device already. The LampArray
        /// units that ride beside a device's own unit under step 3 are
        /// left out of unit rows: a unit row holds one unit.
        ///
        /// <para>A device is told by its container and vendor ID. Outside
        /// the PC a container is one physical device, the units of a
        /// receiver included, and the vendor ID keeps a hub's or a
        /// monitor's parts of another maker apart. In the PC's own
        /// container every built-in device sits together and is told by its
        /// product ID and kind, so nothing there is counted.</para>
        /// </summary>
        internal sealed class DeviceFamilies
        {
            private readonly HashSet<(Guid, ushort)> _lamps = new();
            private readonly HashSet<(Guid, ushort)> _natives = new();
            private readonly HashSet<(Guid, ushort)> _windowsLit = new();
            private readonly HashSet<(Guid, ushort)> _besideWindows = new();
            private readonly HashSet<(Guid, ushort)> _linkedLamps = new();
            private readonly HashSet<(Guid, ushort)> _linkedNatives = new();

            /// <summary>The census of the direct units and the HID++ units
            /// a row could be lit through on their own: of a family that
            /// gets unit rows, in a known container, and, for a LampArray
            /// unit, not one that ignores a raw host. <paramref name="linked"/>
            /// are the links of the rows other than unit rows, whose
            /// lighting paths name the units a device row lights.</summary>
            internal static DeviceFamilies Of(IReadOnlyList<DirectUnit> direct, HidppSnapshot hidpp,
                IEnumerable<DeviceLinks> linked)
            {
                direct ??= Array.Empty<DirectUnit>();
                hidpp ??= HidppSnapshot.Empty;
                var families = new DeviceFamilies();
                foreach (var unit in direct)
                {
                    if (unit == null || !GetsUnitRow(unit.Family) || IgnoresRawHost(unit)) continue;
                    families.Count(unit);
                }
                foreach (var unit in hidpp.Units)
                    if (unit != null && LightsUnitRow(unit)) families.Count(UnitOf(unit));
                // A vendor row's paths are shared ones, which name no unit.
                foreach (var links in linked)
                {
                    foreach (var path in links.Lighting)
                    {
                        var unit = path.Family == OutputFamily.HidppUnit
                            ? FindHidppUnit(path.Key, hidpp) is HidppUnit h ? UnitOf(h) : null
                            : FindUnit(path, direct);
                        if (unit == null || !Counted(unit, out var device)) continue;
                        if (unit.Family == OutputFamily.LampArrayUnit) families._linkedLamps.Add(device);
                        else families._linkedNatives.Add(device);
                    }
                }
                return families;
            }

            /// <summary>Whether a unit is of a device the census counts,
            /// and that device.</summary>
            private static bool Counted(DirectUnit unit, out (Guid, ushort) device)
            {
                device = (unit.ContainerId, unit.VendorId);
                return unit.ContainerId != Guid.Empty && unit.ContainerId != LocalMachineContainer;
            }

            private void Count(DirectUnit unit)
            {
                if (!Counted(unit, out var device)) return;
                if (unit.Family == OutputFamily.LampArrayUnit)
                {
                    _lamps.Add(device);
                    if (unit.WindowsLit) _windowsLit.Add(device);
                }
                else
                {
                    _natives.Add(device);
                    if (unit.LitBesideWindows) _besideWindows.Add(device);
                }
            }

            /// <summary>Whether the unit's device has units of both
            /// families and the unit is of the one the device is not lit
            /// through. A device in the PC's own container is never
            /// counted, so its units never lose.</summary>
            internal bool Loses(DirectUnit unit)
            {
                if (!Counted(unit, out var device)) return false;
                bool lamp = unit.Family == OutputFamily.LampArrayUnit;
                if (_linkedNatives.Contains(device)) return lamp;
                if (_linkedLamps.Contains(device)) return !lamp;
                if (!_lamps.Contains(device) || !_natives.Contains(device)) return false;
                bool lampsWin = _windowsLit.Contains(device) && !_besideWindows.Contains(device);
                return lamp != lampsWin;
            }
        }

        /// <summary>The orphans that have been orphans for
        /// <paramref name="holdMs"/> without a break, which are the ones
        /// that get a row. A keyboard's row can come online a moment after
        /// its LampArray collection, and without the wait that keyboard
        /// would get a row of its own for the moment between.
        /// <paramref name="since"/> is the caller's record of when each
        /// orphan was first seen. A unit that stops being an orphan leaves
        /// it, so its wait starts over.</summary>
        internal static DirectUnit[] HeldOrphans(Dictionary<OutputPath, long> since, IReadOnlyList<DirectUnit> orphans,
            long now, int holdMs = UnitRowHoldMs)
        {
            List<OutputPath> gone = null;
            foreach (var path in since.Keys)
            {
                bool still = false;
                foreach (var unit in orphans)
                    if (unit.Path == path) { still = true; break; }
                if (!still) (gone ??= new List<OutputPath>()).Add(path);
            }
            if (gone != null) foreach (var path in gone) since.Remove(path);

            var held = new List<DirectUnit>();
            foreach (var unit in orphans)
            {
                if (!since.TryGetValue(unit.Path, out long first)) since[unit.Path] = first = now;
                if (now - first >= holdMs) held.Add(unit);
            }
            return held.ToArray();
        }

        /// <summary>The Chroma category a Razer row lights: the keypad
        /// category for a keypad, else the device's kind by product ID, else
        /// the row's own kind.</summary>
        internal static string RazerCategory(LinkRow row)
        {
            if (Array.IndexOf(RazerKeypadPids, row.ProductId) >= 0) return "keypad";
            // The Huntsman V3 boards openrazer does not list yet, which the
            // analog keyboard reader already knows (AnalogKeyboardCatalog).
            if (PadForge.Engine.Common.AnalogKeyboard.AnalogKeyboardCatalog.IsRazerHuntsmanV3(row.ProductId))
                return "keyboard";
            return PeripheralProductIds.LightsAsKeyboard(row.ProductId, row.CapType,
                PeripheralProductIds.RazerMice, PeripheralProductIds.RazerKeyboards) ? "keyboard" : "mouse";
        }

        /// <summary>The LED SDK type a Logitech unit lights as: its own kind
        /// where it gave one, else its row's.</summary>
        internal static string LedSdkTypeOf(HidppUnit unit, LinkRow row)
        {
            if (unit.IsKeyboardKind) return "keyboard";
            if (unit.IsMouseKind) return "mouse";
            return IsKeyboard(row.CapType) ? "keyboard" : "mouse";
        }

        private static void AddOnce(List<OutputPath> paths, OutputPath path)
        {
            if (!paths.Contains(path)) paths.Add(path);
        }

        /// <summary>The device rows the linker reads: a mouse, a keyboard, and
        /// an analog keyboard's vendor interface (#468), which is a keyboard
        /// too.</summary>
        internal static bool IsLinkable(int capType)
            => capType == InputDeviceType.Mouse || IsKeyboard(capType);

        internal static bool IsKeyboard(int capType)
            => capType == InputDeviceType.Keyboard || capType == InputDeviceType.AnalogKeyboard;

        /// <summary>A unit belongs to a row in its container whose kind it
        /// shares, or to every row there when it gave no kind. A direct unit,
        /// wired or Bluetooth, is the device itself and alone in its
        /// container, so it belongs to every row of it that is a mouse or
        /// keyboard, while a remote or presenter there stays out.</summary>
        internal static bool Matches(HidppUnit unit, LinkRow row)
        {
            if (row.Container == Guid.Empty || unit.ContainerId != row.Container) return false;
            if (unit.DeviceType < 0) return true;
            if (unit.DeviceIndex == PadForge.Common.Input.HidppHapticProtocol.DirectDeviceIndex)
                return unit.IsKeyboardKind || unit.IsMouseKind;
            return IsKeyboard(row.CapType) ? unit.IsKeyboardKind : unit.IsMouseKind;
        }

        /// <summary>Whether a row's device may still be found, so the row
        /// keeps on record what it had: a Logitech row before the HID++
        /// worker's first scan or while its container has a slot that has
        /// not answered, any device row until every direct backend has
        /// scanned once, and a row one of whose units is listed asleep
        /// (<see cref="MatchesAsleepUnit"/>), which answers again when it
        /// wakes. A unit that left its backend's list is waited for no
        /// longer: its collection is gone. The link pass still keeps the
        /// records of that unit's rows a few seconds more, by a rule with
        /// a clock of its own (<see cref="HeldRows"/>). A vendor row is
        /// found by no scan.</summary>
        internal static bool StillLooking(LinkRow row, LinkInputs inputs)
        {
            if (row.VendorRow != null) return false;
            if (!inputs.DirectScanned) return true;
            if (MatchesAsleepUnit(row, inputs.Units)) return true;
            var hidpp = inputs.Hidpp ?? HidppSnapshot.Empty;
            return row.VendorId == LogitechVid && row.Unit == null
                && (!hidpp.Scanned || hidpp.PendingContainers.Contains(row.Container));
        }

        /// <summary>The outputs a row keeps on record. What the links found,
        /// plus what it had while its device may still be found
        /// (<see cref="StillLooking"/>) or its last unit left a moment ago
        /// (<see cref="HeldRows"/>), so a device asleep at launch keeps its
        /// tabs, and so does one that was just unplugged.</summary>
        internal static int Capabilities(int previous, DeviceLinks links, bool stillLooking)
        {
            int found = links == null ? 0 : (int)links.Kinds;
            return stillLooking ? found | previous : found;
        }
    }
}
