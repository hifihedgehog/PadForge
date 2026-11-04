using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// One controller family of OpenRGB's that PadForge lights through the
    /// shared HID lighting worker (#557, <see cref="HidLightingBackend"/>):
    /// the collections it answers on, by OpenRGB's detector registration,
    /// and for each model its lights and how a session with it reads,
    /// writes and hands back. The worker does the rest the same way for
    /// every family: the scan, the open, when a picture goes out, the
    /// failure and the crash path.
    /// </summary>
    internal interface IHidLightingFamily
    {
        /// <summary>The family's name for the log.</summary>
        string Name { get; }

        /// <summary>The model at a collection of the host's walk, or null
        /// when the collection is not one of this family's. Asks the device
        /// nothing.</summary>
        HidLightingModel Match(HidCollectionInfo collection);
    }

    /// <summary>
    /// A family whose devices are no HID collections (#557): a laptop
    /// keyboard lit through its firmware's WMI or ACPI interface, or a
    /// keyboard lit through a USB serial port. It finds its devices by a
    /// look of its own and talks to each through a channel of its own kind
    /// (<see cref="HidLightingModel.Open"/>). Its <see cref="IHidLightingFamily.Match"/>
    /// answers null for every collection. The worker's scan, claim,
    /// repaint, hand-back, failure and crash path are the ones every family
    /// has.
    /// </summary>
    internal interface IOwnLookFamily : IHidLightingFamily
    {
        /// <summary>The family's devices present now, asked nothing that
        /// changes them. Called on the worker thread at every walk the host
        /// hands over, so a look that costs keeps what it found. Null when
        /// the look could not be made, and then the units the last look
        /// found stay listed.</summary>
        IReadOnlyList<OwnDevice> Look();
    }

    /// <summary>One device an own-look family found.</summary>
    internal sealed record OwnDevice(string Key, HidLightingModel Model)
    {
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }

        /// <summary>The container the device sits in where the family knows
        /// it. Guid.Empty: the worker looks it up from <see cref="Key"/>,
        /// which is then a device interface path.</summary>
        public Guid Container { get; init; }

        /// <summary>The device lights the PC's own keyboard, which has no
        /// vendor and product ID the family can name, so it goes to the
        /// keyboard rows in the PC's own container
        /// (<see cref="DirectUnit.BuiltInKeyboard"/>).</summary>
        public bool BuiltInKeyboard { get; init; }
    }

    /// <summary>A collection of the same device beside a unit's own
    /// (<see cref="HidLightingModel.Siblings"/>): one with the unit's vendor
    /// and product IDs, on the USB interface, as hidapi numbers it
    /// (<see cref="OpenRgbRegistrations.InterfaceOf"/>), and the usage page
    /// OpenRGB opens it by.</summary>
    internal sealed record HidSibling(int Interface, ushort UsagePage)
    {
        /// <summary>Whether <paramref name="candidate"/> of the walk is this
        /// sibling of <paramref name="unit"/>, the container aside.</summary>
        public bool Is(HidCollectionInfo unit, HidCollectionInfo candidate)
            => unit != null && candidate?.Path != null
            && !string.Equals(candidate.Path, unit.Path, StringComparison.OrdinalIgnoreCase)
            && candidate.VendorId == unit.VendorId && candidate.ProductId == unit.ProductId
            && candidate.UsagePage == UsagePage
            && OpenRgbRegistrations.InterfaceOf(candidate, OpenRgbRegistrations.BusOf(candidate)) == Interface;
    }

    /// <summary>One model of a family: its lights in the order a session
    /// takes their colors, when a picture goes out, and how a session with
    /// it starts.</summary>
    internal sealed class HidLightingModel
    {
        public IHidLightingFamily Family { get; init; }

        /// <summary>OpenRGB's name for the model, for the log.</summary>
        public string Name { get; init; }

        /// <summary>The model's lights. Its order is the order of the colors
        /// <see cref="HidLightingSession.Paint"/> takes.</summary>
        public LightMap Map { get; init; }

        /// <summary>The device's own controller stays in charge of these
        /// lights while Windows Dynamic Lighting holds a LampArray interface
        /// of the same device, so the linker chooses this path over that one
        /// (<see cref="PeripheralLinker.ChooseLighting"/>).</summary>
        public bool LitBesideWindows { get; init; }

        /// <summary>The device's own controller keeps still pictures only,
        /// and a LampArray interface of the same device shows colors that
        /// change wherever the device's firmware lets that interface take
        /// colors, so the linker lights a row through both
        /// (<see cref="PeripheralLinker.ChooseLighting"/>).</summary>
        public bool LiveThroughLampArray { get; init; }

        /// <summary>The programs that light the model's devices while they
        /// run (<see cref="VendorPrograms"/>). While one runs, the linker
        /// leaves the device to it, and the worker lets go of the device
        /// with no hand-back, since one that lands after the program's own
        /// writes would undo them. Nothing is sent to the device meanwhile:
        /// no picture, no hand-back owed from an earlier run, and nothing
        /// on the crash path.</summary>
        public IReadOnlyList<VendorProgram> Programs { get; init; } = Array.Empty<VendorProgram>();

        /// <summary>How long a changed picture has to stay the same before
        /// it goes out. 0: at once.</summary>
        public int HoldMs { get; init; }

        /// <summary>The shortest time between two pictures sent.</summary>
        public int RepaintMs { get; init; }

        /// <summary>How often an open session reads the device's state again
        /// (<see cref="HidLightingSession.Recheck"/>). 0: never.</summary>
        public int RecheckMs { get; init; }

        /// <summary>For a device that drops the host's colors when the host
        /// goes quiet: how often the session's keep-alive goes out
        /// (<see cref="HidLightingSession.KeepAlive"/>) while a picture
        /// shows, timed from the keep-alive before, or from the session's
        /// start before the first one. 0: never.</summary>
        public int KeepAliveMs { get; init; }

        /// <summary>How long a keep-alive waits after a picture went out at
        /// the least. 0: a picture does not put the next keep-alive
        /// off.</summary>
        public int KeepAliveIdleMs { get; init; }

        /// <summary>Starts a session on a channel open for read and write,
        /// reading what it needs first, with a wait the worker supplies.
        /// Null when the device did not answer, and then nothing is
        /// written.</summary>
        public Func<IHidReportChannel, Action<int>, HidLightingSession> Begin { get; init; }

        /// <summary>For a family whose registration also matches collections
        /// it does not light, other makers' devices on a shared ID pair or
        /// firmware OpenRGB refuses: whether a collection is one it lights,
        /// from the strings the device reports
        /// (<see cref="IHidReportChannel.ProductString"/> and the others), as
        /// OpenRGB's detector decides from the strings before it makes a
        /// controller. The worker asks it once while the collection stays in
        /// the walk, on a handle opened with no access right, which writes
        /// nothing, and lists the unit only on true. Null: every collection
        /// the family matches is one it lights.</summary>
        public Func<IHidReportChannel, bool> Identify { get; init; }

        /// <summary>The device's lighting lives on a mouse or keyboard
        /// collection, which Windows holds and opens with no access right
        /// only, as it does for every program: "feature reports can still be
        /// sent and received" on that handle, and hidapi hands OpenRGB that
        /// handle (hidapi windows/hid.c:1009-1026). For every other model a
        /// handle with no access right means another program holds the
        /// collection, and the worker writes nothing to it. For this one the
        /// worker keeps that handle, and the session sends feature reports
        /// alone.</summary>
        public bool HeldByWindows { get; init; }

        /// <summary>The other collections of the device a session talks
        /// through beside the unit's own, as OpenRGB opens a control and a
        /// LED collection of one keyboard (RoccatControllerDetect.cpp:101-203).
        /// A collection is listed only while each of its siblings is in the
        /// walk too. The worker opens them for read and write beside the
        /// unit's channel, from the same container, hands them to
        /// <see cref="BeginWith"/> in this order, and closes them with
        /// it.</summary>
        public IReadOnlyList<HidSibling> Siblings { get; init; } = Array.Empty<HidSibling>();

        /// <summary>The counterpart of <see cref="Begin"/> for a model with
        /// <see cref="Siblings"/>: the unit's channel, then one channel per
        /// sibling in their order.</summary>
        public Func<IHidReportChannel, IReadOnlyList<IHidReportChannel>, Action<int>, HidLightingSession> BeginWith { get; init; }

        /// <summary>For a family whose devices are no HID collections
        /// (<see cref="IOwnLookFamily"/>): opens the device a key names, in
        /// the channel kind the family speaks, or answers null when it did
        /// not open. Null for a HID family, whose collection the worker
        /// opens for read and write.</summary>
        public Func<string, IDisposable> Open { get; init; }

        /// <summary>Starts a session on a channel <see cref="Open"/> made,
        /// the counterpart of <see cref="Begin"/>. The session talks through
        /// that channel and is handed no HID channel (null) by the
        /// worker.</summary>
        public Func<IDisposable, Action<int>, HidLightingSession> BeginOwn { get; init; }

        /// <summary>For a family whose devices are no HID collections: sends
        /// one of the session's crash frames
        /// (<see cref="HidLightingSession.CrashFrames"/>) to the device a key
        /// names through a channel of its own, within a bound in
        /// milliseconds. True when it went out.</summary>
        public Func<string, byte[], int, bool> WriteOnce { get; init; }

        /// <summary>Whether the device may still show colors an earlier run
        /// wrote into its own memory and never handed back. Null: never.</summary>
        public Func<bool> Owed { get; init; }

        /// <summary>Hands those colors back on a channel open for read and
        /// write, with no claim on the device. True when nothing is owed any
        /// more.</summary>
        public Func<IHidReportChannel, Action<int>, bool> Repair { get; init; }
    }

    /// <summary>What became of a picture a session was given.</summary>
    internal enum HidLightingPaint
    {
        /// <summary>It went out.</summary>
        Sent,

        /// <summary>Nothing went out and nothing failed: the session tries
        /// again after <see cref="HidLightingSession.LaterMs"/>.</summary>
        Later,

        /// <summary>A write or a read failed.</summary>
        Failed,
    }

    /// <summary>One open of a unit: it shows pictures and hands the device
    /// back, on the channel the worker opened, and keeps what the crash
    /// path sends when the worker cannot. A session of a family whose
    /// devices are no HID collections talks through the channel it began
    /// on (<see cref="HidLightingModel.BeginOwn"/>), and the
    /// <c>io</c> it is handed is null.</summary>
    internal abstract class HidLightingSession
    {
        /// <summary>True when the frames are feature reports, false for
        /// output reports.</summary>
        public abstract bool Feature { get; }

        /// <summary>Shows <paramref name="colors"/>, one per light of
        /// <paramref name="lights"/>, the unit's map in the order of the
        /// model's.</summary>
        public abstract HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
            IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors);

        /// <summary>The map of the device this session opened, read from the
        /// device, or null to keep the model's (<see cref="HidLightingModel.Map"/>).
        /// Read once, right after the session begins. From then on, for as
        /// long as the device stays in the walk, the worker publishes it,
        /// with the names Map Key Lights learned for it, renders every
        /// picture by it and hands <see cref="Paint"/> its lights, across
        /// closes and reopens. The model's map goes back only when the unit
        /// leaves the walk.</summary>
        public virtual LightMap Map => null;

        /// <summary>How long a picture that came back
        /// <see cref="HidLightingPaint.Later"/> waits before the next
        /// try.</summary>
        public int LaterMs { get; protected set; }

        /// <summary>The hold and the shortest time between two pictures for
        /// the device this session opened, where the session tells apart
        /// devices one model covers, one that stores each picture and one
        /// that does not. Null: the model's
        /// (<see cref="HidLightingModel.HoldMs"/>,
        /// <see cref="HidLightingModel.RepaintMs"/>). Read at every
        /// picture.</summary>
        public virtual int? HoldMs => null;

        public virtual int? RepaintMs => null;

        /// <summary>Reads the device's state again, so the frames the crash
        /// path holds follow it. False when the device did not
        /// answer.</summary>
        public virtual bool Recheck(IHidReportChannel io, Action<int> wait) => true;

        /// <summary>What goes out on the model's keep-alive interval
        /// (<see cref="HidLightingModel.KeepAliveMs"/>), given the picture
        /// the device shows, <paramref name="colors"/> for
        /// <paramref name="lights"/>: by default that picture again. False
        /// when a write or a read failed.</summary>
        public virtual bool KeepAlive(IHidReportChannel io, Action<int> wait,
            IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            => Paint(io, wait, lights, colors) != HidLightingPaint.Failed;

        /// <summary>Returns the device to its own lighting. False when a
        /// write or a read failed.</summary>
        public abstract bool HandBack(IHidReportChannel io, Action<int> wait);

        /// <summary>The frames the crash path sends, in order, each through
        /// a report of its own, to do what <see cref="HandBack"/> would.
        /// Empty when there is nothing to undo or the crash path cannot undo
        /// it. The same array until it changes.</summary>
        public abstract byte[][] CrashFrames { get; }
    }

    /// <summary>The families the shared worker serves. One line per family,
    /// in the order a collection is offered to them.</summary>
    internal static class HidLightingFamilies
    {
        public static readonly IReadOnlyList<IHidLightingFamily> All = new IHidLightingFamily[]
        {
            LenovoLegionLighting.Instance,
            LenovoLegionUsbLighting.Instance,
            Lenovo4ZoneLighting.Instance,
            LenovoK500Lighting.Instance,
            LenovoK510Lighting.Instance,
            LenovoM300Lighting.Instance,
            AsusTufLaptopLighting.Instance,
            HpOmenLaptopLighting.Instance,
            DygmaRaiseLighting.Instance,
            CoolerMasterLighting.Instance,
            HyperXKeyboardLighting.Instance,
            CougarLighting.Instance,
            DuckyKeyboardLighting.Instance,
            MsiKeyboardLighting.Instance,
            MsiLaptopLighting.Instance,
            Msi3ZoneLighting.Instance,
            MsiVigorLighting.Instance,
            AlienwareLighting.Instance,
            AlienwareKeyboardLighting.Instance,
            AcerNitroLighting.Instance,
            IonicoLighting.Instance,
            ClevoLighting.Instance,
            GigabyteAorusLaptopLighting.Instance,
            EvgaLighting.Instance,
            A4TechLighting.Instance,
            RoccatLighting.Instance,
            RoccatVulcanLighting.Instance,
            CherryKeyboardLighting.Instance,
            EVisionKeyboardLighting.Instance,
            DasKeyboardLighting.Instance,
            AsusAuraUsbLighting.Instance,
            AsusAuraCoreLighting.Instance,
            AsusLegacyUsbLighting.Instance,
            MountainKeyboardLighting.Instance,
            MintakaKeyboardLighting.Instance,
            SkyloongKeyboardLighting.Instance,
            WitmodKeyboardLighting.Instance,
            WinbondKeyboardLighting.Instance,
            ZetKeyboardLighting.Instance,
            XpgSummonerLighting.Instance,
            OksKeyboardLighting.Instance,
            LightSaltLighting.Instance,
            DarkProjectKeyboardLighting.Instance,
            MntKeyboardLighting.Instance,
            FnaticStreakLighting.Instance,
            PoseidonZRgbLighting.Instance,
            SayoDeviceLighting.Instance,
            HyteKeyboardLighting.Instance,
            AocKeyboardLighting.Instance,
            CorsairPeripheralLighting.Instance,
            CorsairPeripheralV2Lighting.Instance,
            WootingKeyboardLighting.Instance,
            SinowealthLighting.Instance,
            SinowealthMouseLighting.Instance,
            QmkLighting.Instance,
            ValkyrieKeyboardLighting.Instance,
            GaiZhongGaiLighting.Instance,
            AnnePro2Lighting.Instance,
            RedSquareKeyroxLighting.Instance,
            RoyuanLighting.Instance,
            KeychronKeyboardLighting.Instance,
            TrustLighting.Instance,
        };

        /// <summary>The model at a collection, from the first family that
        /// claims it, or null.</summary>
        public static HidLightingModel Match(HidCollectionInfo collection, IReadOnlyList<IHidLightingFamily> families = null)
        {
            if (collection?.Path == null) return null;
            foreach (var family in families ?? All)
            {
                var model = family.Match(collection);
                if (model != null) return model;
            }
            return null;
        }
    }
}
