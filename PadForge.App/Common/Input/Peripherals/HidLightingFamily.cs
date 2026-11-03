using System;
using System.Collections.Generic;

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

        /// <summary>How long a changed picture has to stay the same before
        /// it goes out. 0: at once.</summary>
        public int HoldMs { get; init; }

        /// <summary>The shortest time between two pictures sent.</summary>
        public int RepaintMs { get; init; }

        /// <summary>How often an open session reads the device's state again
        /// (<see cref="HidLightingSession.Recheck"/>). 0: never.</summary>
        public int RecheckMs { get; init; }

        /// <summary>Starts a session on a channel open for read and write,
        /// reading what it needs first, with a wait the worker supplies.
        /// Null when the device did not answer, and then nothing is
        /// written.</summary>
        public Func<IHidReportChannel, Action<int>, HidLightingSession> Begin { get; init; }

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
    /// path sends when the worker cannot.</summary>
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

        /// <summary>How long a picture that came back
        /// <see cref="HidLightingPaint.Later"/> waits before the next
        /// try.</summary>
        public int LaterMs { get; protected set; }

        /// <summary>Reads the device's state again, so the frames the crash
        /// path holds follow it. False when the device did not
        /// answer.</summary>
        public virtual bool Recheck(IHidReportChannel io, Action<int> wait) => true;

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
