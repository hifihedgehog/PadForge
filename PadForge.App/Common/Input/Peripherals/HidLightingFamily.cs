using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// One controller family of OpenRGB's that PadForge lights through the
    /// shared HID lighting worker (#557, <see cref="HidLightingBackend"/>):
    /// the collections it answers on, by OpenRGB's detector registration,
    /// and for each model its lights and its frames. The worker does the
    /// rest the same way for every family: the scan, the open, the claim,
    /// the paint, the hand-back, the failure and the crash path.
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

    /// <summary>One model of a family: its lights in the order its frames
    /// address them, and how a session with it starts.</summary>
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

        /// <summary>The shortest time between two paints of a changed
        /// color.</summary>
        public int RepaintMs { get; init; }

        /// <summary>How often an unchanged color goes out again, which puts
        /// the claim back after the device dropped it.</summary>
        public int ReassertMs { get; init; }

        /// <summary>Reads what a session needs before its first frame, on a
        /// channel open for read and write. Null when the device did not
        /// answer, and then nothing is claimed.</summary>
        public Func<IHidReportChannel, HidLightingSession> Begin { get; init; }
    }

    /// <summary>One open of a unit: the frames that paint it and the ones
    /// that hand it back, both fixed by what <see cref="HidLightingModel.Begin"/>
    /// read.</summary>
    internal abstract class HidLightingSession
    {
        /// <summary>True when the frames are feature reports, false for
        /// output reports.</summary>
        public abstract bool Feature { get; }

        /// <summary>The frames that show <paramref name="colors"/>, one per
        /// light of <paramref name="lights"/>, the unit's map in the order
        /// of the model's. Each paint claims the device again, so a paint is
        /// its own re-assert.</summary>
        public abstract byte[][] Paint(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors);

        /// <summary>The frames that return the device to its own lighting.
        /// Empty where no frame does.</summary>
        public abstract byte[][] HandBack();
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
