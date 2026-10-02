using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Where a pad reports button pressure, for the default mapping of the
    /// button pressure rows (discussion #476). The answer is an axis per
    /// pressure target, in <see cref="MappingSetMigrator.PressureTargets"/>
    /// order: cross, circle, square, triangle, L1, R1, D-pad up, down, left,
    /// right. Null means PadForge knows no layout for the pad, and the rows
    /// stay empty for the user to record.
    ///
    /// <para>A DualShock 2 in a Bliss-Box port gets its pressures ("Cross
    /// Pressure" and the rest) from the port's placement instead
    /// (<see cref="PadForge.Engine.Common.BlissBox.BlissBoxGamepadMap.PressureAxes"/>).
    /// A pressure row's recording takes the pressure axis rather than the
    /// button (RecorderService._analogOnly).</para>
    /// </summary>
    internal static class ButtonPressureSources
    {
        /// <summary>The DualShock 3's first pressure axis. SDL's PS3 driver
        /// posts the ten pressures on joystick axes 6 to 15 in the targets'
        /// order (SDL_hidapi_ps3.c button_axis_offsets: south, east, west,
        /// north, the shoulders, then up, down, left, right), and PadForge's
        /// own DualShock 3 reader copies that order on both transports
        /// (Ds3DirectService's virtual joystick).</summary>
        internal const int Ds3FirstPressureAxis = 6;

        /// <summary>
        /// True for a DualShock 3 whose axes 6 to 15 are its pressures in SDL's
        /// order: 054C:0268 with 16 raw axes, opened by SDL's PS3 driver (11
        /// buttons, SDL_hidapi_ps3.c) or by PadForge's own reader (15 buttons,
        /// Ds3DirectService). PCSX2 accepts the same 16 axes and 11 buttons
        /// (SDLInputSource::IsControllerSixaxis).
        ///
        /// <para>DsHidMini's SDF mode also reaches SDL with 16 axes, but with
        /// 17 buttons (01_SDF_Col1_GamePad.h) and its pressures in another
        /// order: up, right, down, left, L1, R1, triangle, circle, cross,
        /// square (DsHid.c DS3_RAW_TO_SDF_HID_INPUT_REPORT). PCSX2 leaves it
        /// out for that reason, and so does this.</para>
        /// </summary>
        internal static bool IsSdlOrderDualShock3(ushort vendorId, ushort productId,
            int rawAxes, int rawButtons)
            => vendorId == 0x054C && productId == 0x0268
            && rawAxes >= Ds3FirstPressureAxis + PadForge.Engine.Common.ButtonPressureState.Count
            && (rawButtons == 11 || rawButtons == 15);

        /// <summary>The axis each pressure target reads on
        /// <paramref name="ud"/>, or null.</summary>
        internal static int[] AxesFor(UserDevice ud)
        {
            if (ud?.Device is not SdlDeviceWrapper w
                || !IsSdlOrderDualShock3(w.VendorId, w.ProductId, w.RawAxisCount, w.RawButtonCount))
                return null;
            var axes = new int[PadForge.Engine.Common.ButtonPressureState.Count];
            for (int i = 0; i < axes.Length; i++) axes[i] = Ds3FirstPressureAxis + i;
            return axes;
        }
    }
}
