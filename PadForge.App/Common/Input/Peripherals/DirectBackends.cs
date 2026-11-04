using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>The direct backends the peripheral host starts with the
    /// engine (#494), one per family that lights or rumbles a device on its
    /// own. Each is made here and nowhere else, so adding a family is one
    /// line in <see cref="Create"/>, and each is made inside a guard
    /// (<see cref="Add"/>).</summary>
    internal static class DirectBackends
    {
        /// <summary>A new set of backends for one host. The order is the
        /// order the crash path hands back in, after the HID++ units, which
        /// lead because only they can leave a device in host mode
        /// (<see cref="PeripheralOutputHost.ReleaseLightingNow"/>).</summary>
        internal static IReadOnlyList<IDirectUnitBackend> Create()
        {
            var backends = new List<IDirectUnitBackend>();

            // One backend per line, each under its own heading, so two
            // families added side by side never touch the same lines. A
            // line names the family and how its backend is made:
            //     Add(backends, OutputFamily.<family>, () => new <its backend>());

            // HID LampArray. First, since its hand-back is one short report
            // that returns a device to its own effect.
            Add(backends, OutputFamily.LampArrayUnit, () => new LampArrayBackend());

            // SteelSeries.
            Add(backends, OutputFamily.SteelSeriesUnit, () => new SteelSeriesDirectBackend());

            // The OpenRGB families of the shared HID lighting worker (#557).
            Add(backends, OutputFamily.HidLightingUnit, () => new HidLightingBackend());

            // Razer. Last of the lighting families: its hand-back can wait
            // on a lock other tools share.
            Add(backends, OutputFamily.RazerUnit, () => new RazerDirectBackend());

            // TouchSense. Rumble only, so the crash path's lighting pass
            // asks nothing of it.
            Add(backends, OutputFamily.TouchSenseUnit, () => new TouchSenseBackend());

            // The ASUS Aura SDK while Armoury Crate runs (#557).
            Add(backends, OutputFamily.AuraSdkType, () => new AuraSdkBackend());

            // The MSI Mystic Light SDK while Mystic Light runs (#557).
            Add(backends, OutputFamily.MysticLightType, () => new MysticLightBackend());

            return backends;
        }

        /// <summary>Makes one backend and lists it. The host is made on the
        /// engine's start path, so a constructor that throws must cost that
        /// family alone: it is named in the log, left out, and every other
        /// family and the host are made all the same.</summary>
        internal static void Add(List<IDirectUnitBackend> backends, OutputFamily family, Func<IDirectUnitBackend> make)
        {
            IDirectUnitBackend made = null;
            try { made = make(); }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL {family} backend not made: {ex.GetType().Name}");
            }
            if (made != null) backends.Add(made);
        }
    }
}
