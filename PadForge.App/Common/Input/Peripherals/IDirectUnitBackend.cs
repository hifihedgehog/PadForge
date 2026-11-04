using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// One family of devices lit or rumbled one at a time, each through its
    /// own collection (#494): HID LampArray, Razer, SteelSeries, TouchSense.
    /// The peripheral host holds every such backend through this, starts and
    /// stops it with the engine, hands it the present HID collections, and
    /// links rows to the units it finds. <see cref="HidppBackend"/> is the
    /// model and keeps its own shape.
    ///
    /// <para>A backend has one worker thread that owns every channel of its
    /// family, so a scan, a claim and a color never interleave on one
    /// collection. In the order a lighting backend runs them:</para>
    ///
    /// <para>Scan. From the list <see cref="Rescan"/> hands over, by
    /// descriptor and table, with no write to any device. The result goes
    /// out as one new <see cref="Snapshot"/> and <see cref="SnapshotChanged"/>.</para>
    ///
    /// <para>Rows. A unit reaches the rows of its own device by container:
    /// the host looks each row's container up from the row's DevicePath
    /// (<see cref="PeripheralOutputHost.ContainerPath"/>), and the linker
    /// compares it with the unit's <see cref="DirectUnit.ContainerId"/>. A
    /// TouchSense mouse bound to WinUSB is no mouse to Windows any more,
    /// so its lane opens a row for it, and that row carries the mouse's
    /// WinUSB interface path as its DevicePath, so the host's container
    /// lookup matches it to its unit. A path of PadForge's own there names
    /// no device and matches nothing.</para>
    ///
    /// <para>Open. Only a unit whose path a row links and on which a claim
    /// resolves (<see cref="PeripheralOutputs.TryResolveColor(OutputPath, out int, out PeripheralOutputs.Claimant)"/>),
    /// so a device nobody assigned costs nothing.</para>
    ///
    /// <para>Record, claim and paint. The unit's hand-back goes into the
    /// crash record before its first claim frame goes out, and a worker
    /// whose stop is set claims nothing more
    /// (HidppBackend.PaintLighting). The first color follows the claim in
    /// the same pass. The claim and the color go out again on the family's
    /// interval, on a clock apart from the color's, so a color that changes
    /// faster than the interval never holds the claim back.</para>
    ///
    /// <para>Hand back. When the path leaves the link table or no claim
    /// resolves on it: the family's hand-back, then the handle is closed.
    /// Two exceptions send nothing. A path withdrawn because the vendor's
    /// program started (<see cref="PeripheralPresence.RazerSoftwareRunning"/>,
    /// <see cref="PeripheralPresence.SteelSeriesGGRunning"/>, a program a
    /// shared-worker model names, <see cref="HidLightingModel.Programs"/>): the program
    /// has taken the device, and a hand-back that lands after its claim
    /// undoes it. And a LampArray unit Windows Dynamic Lighting started to
    /// light, whose hand-back would switch Windows' own effect off.</para>
    ///
    /// <para>Fail. A failed write closes the unit's channel and marks the
    /// unit asleep (<see cref="DirectUnit.Asleep"/>) in the next
    /// <see cref="Snapshot"/>. It never takes the unit off the list. A
    /// unit stays listed while its HID collection is in the last walk
    /// <see cref="Rescan"/> handed over, asleep for as long as its channel
    /// is closed after a failure or it does not answer, and it leaves the
    /// list when its collection leaves the walk. A family whose devices
    /// are no HID collections keeps a unit while its own look still finds
    /// the device, by the same rule. While a unit is asleep the linker
    /// links no path to it, the rows that match it keep the outputs they
    /// had on record, so their tabs stay up with the line for a device
    /// that has not answered yet, and a unit with a row of its own keeps
    /// that row. Its claim stays where the dispatcher put it, and paints
    /// again once the unit answers and is listed awake. The backend tries
    /// an asleep unit again on its own interval, and asks for a walk
    /// after the failure (<see cref="RescanRequested"/>), since the
    /// device may have left with no row to say so. When the unit does
    /// leave the list, its rows keep what they had on record for
    /// <see cref="PeripheralLinker.UnitGoneHoldMs"/> more. The row of a
    /// device that was unplugged is offline by then, and an offline row
    /// keeps its record.</para>
    ///
    /// <para>Owe. A lit unit the worker lets go of while its device may
    /// still show the claim, after a failed write, a missed answer or a
    /// hand-back that did not go out, is owed its hand-back. The
    /// hand-back keeps its place on the crash record until it is
    /// settled. It is sent once the unit answers again, whether or not
    /// anything lights it by then, ahead of whatever else the unit is
    /// sent, or a new claim on the unit takes it over where that claim's
    /// own hand-back is the same one. A unit that does not take it stays
    /// owed. It ends with nothing sent where a hand-back sends nothing,
    /// as above, and when the unit leaves the list.</para>
    ///
    /// <para>Stop. The worker's finally hands back every unit it holds
    /// before it closes its handles, and sends what it still owes through
    /// a bounded write on a handle of its own. A backend takes calls
    /// after its stop and does nothing: the host's link pass can outlive
    /// the wait Stop makes for it, so a <see cref="Rescan"/> or a read of
    /// <see cref="Snapshot"/> that was already on its way can arrive
    /// beside <see cref="Stop"/> or after it, and after Dispose too. The
    /// host reads its own stop before each such call and makes none once
    /// it is set. For the one that slips through, a stopped or disposed
    /// backend keeps or drops what it is handed, opens nothing, starts no
    /// thread and throws nothing.</para>
    ///
    /// <para>A lighting worker wakes on
    /// <see cref="PeripheralOutputs.LightingChanged"/>, a haptic one on
    /// <see cref="PeripheralOutputs.HapticsChanged"/>, and each waits on its
    /// idle tick with no I/O while no row links one of its paths.</para>
    /// </summary>
    internal interface IDirectUnitBackend : IDisposable
    {
        /// <summary>The family of every path this backend serves.</summary>
        OutputFamily Family { get; }

        /// <summary>What the last scan found and whether one has run, for
        /// the linker and the tab's route line, in one object swapped whole
        /// (<see cref="DirectBackendSnapshot"/>), so the link pass never
        /// reads the units of one scan beside the scan state of another.
        /// <see cref="DirectBackendSnapshot.NotScanned"/> until the first
        /// scan has run. The link thread reads it once on every pass. A
        /// read that throws or gives null counts as not scanned with no
        /// units for that pass, which keeps every device row's recorded
        /// outputs, and the host logs it once.</summary>
        DirectBackendSnapshot Snapshot { get; }

        /// <summary>Raised after a new <see cref="Snapshot"/> is in place,
        /// so the host's link pass picks the change up at once. Any thread.
        /// Subscribers must not block.</summary>
        event Action SnapshotChanged;

        /// <summary>Raised when the backend wants the present collections
        /// walked again before the next slow walk: a write to a unit of its
        /// own failed, and the device may have left with no mouse or
        /// keyboard row to say so. The host then walks once and calls
        /// <see cref="Rescan"/> on every backend, no sooner than
        /// <see cref="PeripheralOutputHost.RescanFloorMs"/> after its last
        /// walk, however many asked in between. A walk that finds the
        /// collection gone takes the unit off the list, and its rows keep
        /// what they had on record for
        /// <see cref="PeripheralLinker.UnitGoneHoldMs"/> more, so it does
        /// not matter whether this walk or the row's own going offline
        /// comes first. Any thread. Subscribers must not block.</summary>
        event Action RescanRequested;

        void Start();

        /// <summary>Hands back every unit the backend holds, then closes
        /// its handles. The host calls it on every stop of the engine and
        /// once more when it is disposed, so it does nothing on a backend
        /// that is not running. Every other member stays callable after
        /// it, and after Dispose, and does nothing then.</summary>
        void Stop();

        /// <summary>The HID collections present now, from the host's one
        /// walk (<see cref="HidCollectionRuntime.Enumerate"/>): at start,
        /// when a device row came or went, when Windows reported a HID
        /// interface arriving, when a backend asked
        /// (<see cref="RescanRequested"/>), and every
        /// <see cref="PeripheralOutputHost.RescanMs"/>. Called on the host's
        /// link thread, so it must not block: keep the list and wake the
        /// worker. A family whose devices are no HID collections takes the
        /// call as its cue to look again.</summary>
        void Rescan(IReadOnlyList<HidCollectionInfo> present);

        /// <summary>The crash path's first step, called on every backend
        /// before any is waited for: claim nothing more. Must not block.</summary>
        void SignalStop();

        /// <summary>The crash path's second step. Waits for the worker to
        /// hand back on its own channel, at most 250 ms and never past
        /// <paramref name="deadline"/>, then hands each unit still on record
        /// back through a handle of its own, each call bounded
        /// (<see cref="HidReportChannel.RunBounded"/>), and stops at the
        /// deadline, a tick of Environment.TickCount64.</summary>
        void ReleaseClaimedNow(long deadline);

        /// <summary>For an abnormal exit, after the engine zeroed every
        /// level: a haptic family sends its stop inside
        /// <paramref name="waitMs"/>. A lighting family has nothing to do.</summary>
        void WaitForSilence(int waitMs) { }
    }

    /// <summary>What one direct backend has found, swapped whole so the
    /// link pass never reads half a scan, the shape
    /// <see cref="HidppSnapshot"/> has for the HID++ worker. A backend
    /// makes a new one for every change and never alters one it has
    /// handed out.</summary>
    internal sealed class DirectBackendSnapshot
    {
        /// <summary>No scan has run yet: no units, and nothing the backend
        /// has not looked for reads as missing.</summary>
        public static readonly DirectBackendSnapshot NotScanned = new(Array.Empty<DirectUnit>(), scanned: false);

        public IReadOnlyList<DirectUnit> Units { get; }

        /// <summary>False until the backend's first scan has run. A row
        /// keeps the outputs it had on record until every backend's
        /// has.</summary>
        public bool Scanned { get; }

        /// <summary>The program the backend speaks to runs and has not said
        /// which devices it holds: OpenRGB with its SDK server off or not
        /// answering (#557, <see cref="OpenRgbBackend"/>). Every device it
        /// may drive is then left to it
        /// (<see cref="LinkInputs.OpenRgbUnanswered"/>).</summary>
        public bool ProgramUnanswered { get; init; }

        public DirectBackendSnapshot(IReadOnlyList<DirectUnit> units, bool scanned = true)
        {
            Units = units ?? Array.Empty<DirectUnit>();
            Scanned = scanned;
        }
    }

    /// <summary>What the direct backends have found, swapped whole so a tab
    /// never reads half a pass. The link pass builds one on every pass from
    /// the list it linked from.</summary>
    internal sealed class DirectSnapshot
    {
        public static readonly DirectSnapshot Empty =
            new(Array.Empty<DirectUnit>(), Array.Empty<OutputFamily>(), scanned: true);

        public IReadOnlyList<DirectUnit> Units { get; }

        /// <summary>The families a direct backend serves in this engine
        /// run. Empty while none runs, and then no device row can be waiting
        /// for one to find it.</summary>
        public IReadOnlyList<OutputFamily> Families { get; }

        /// <summary>False until every direct backend has scanned once.</summary>
        public bool Scanned { get; }

        public DirectSnapshot(IReadOnlyList<DirectUnit> units, IReadOnlyList<OutputFamily> families, bool scanned = true)
        {
            Units = units ?? Array.Empty<DirectUnit>();
            Families = families ?? Array.Empty<OutputFamily>();
            Scanned = scanned;
        }

        /// <summary>The unit a path names, or null.</summary>
        public DirectUnit Find(OutputPath path)
        {
            foreach (var unit in Units)
                if (unit.Family == path.Family && unit.Key == path.Key) return unit;
            return null;
        }

        /// <summary>True when both hold the same units in the same order,
        /// the same families and the same scan state, so publishing one
        /// raises a status change only when a tab would read something
        /// new.</summary>
        public bool SameAs(DirectSnapshot other)
        {
            if (other == null || other.Scanned != Scanned || other.Families.Count != Families.Count) return false;
            for (int i = 0; i < Families.Count; i++)
                if (other.Families[i] != Families[i]) return false;
            return SameUnits(other.Units, Units);
        }

        internal static bool SameUnits(IReadOnlyList<DirectUnit> a, IReadOnlyList<DirectUnit> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!Equals(a[i], b[i])) return false;
            return true;
        }
    }
}
