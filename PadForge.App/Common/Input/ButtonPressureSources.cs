using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;

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

        /// <summary>The buttons SDL's PS3 driver opens a DualShock 3 with
        /// (SDL_hidapi_ps3.c, joystick->nbuttons).</summary>
        private const int SdlPs3DriverButtons = 11;

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
            && (rawButtons == SdlPs3DriverButtons || rawButtons == Ds3DirectService.VirtualJoystickButtons);

        /// <summary>An original Xbox controller's first pressure axis
        /// (discussion #483). The fork's XID driver posts the report's six
        /// analog buttons on joystick axes 6 to 11 in the order A, B, X, Y,
        /// White, Black (SDL_hidapi_xid_proto.c SDL_XID_DecodeGamepad), at the
        /// DualShock 3's scale, rest at the axis minimum. Its mapping puts
        /// White on the left shoulder and Black on the right
        /// (SDL_XID_MAPPING_GAMEPAD), so the six are the targets' cross,
        /// circle, square, triangle, L1 and R1. The pad's D-pad is digital,
        /// as in every XID report, so the D-pad targets get no axis.</summary>
        internal const int XidFirstPressureAxis = 6;

        /// <summary>The XID targets with a pressure: cross through R1.</summary>
        private const int XidPressureTargets = 6;

        /// <summary>The axes the fork's XID driver opens every device of the
        /// gamepad family with (SDL_hidapi_xid_proto.h SDL_XID_GAMEPAD_AXES).</summary>
        private const int XidGamepadAxes = 12;

        /// <summary>
        /// True for an original Xbox controller of the gamepad family as the
        /// fork's XID driver opens it: an ID of the fork's XID table
        /// (<see cref="PadForge.Services.VendorUsbDriverInstaller.XidIdentities"/>),
        /// 12 axes, and 11 buttons for a pad, wheel or stick, 12 for a light
        /// gun or 15 for a dance pad (SDL_hidapi_xid_proto.h). The Steel
        /// Battalion's 9 axes leave it out. <paramref name="rawButtons"/> is
        /// null for a cached entry, which keeps no button count.
        /// </summary>
        internal static bool IsXidGamepad(ushort vendorId, ushort productId, int rawAxes, int? rawButtons)
            => rawAxes == XidGamepadAxes
            && rawButtons is null or 11 or 12 or 15
            && System.Array.IndexOf(PadForge.Services.VendorUsbDriverInstaller.XidIdentities, (vendorId, productId)) >= 0;

        /// <summary>The pressure layouts PadForge knows.</summary>
        private enum Layout { None, Ds3, Xid }

        /// <summary>The axis each pressure target reads on
        /// <paramref name="ud"/>, -1 for a target the pad has no pressure
        /// for, or null. A pad that is not connected answers from its cached
        /// entry, so assigning a cached DualShock 3 or original Xbox controller
        /// maps its pressure the way assigning a connected one does.</summary>
        internal static int[] AxesFor(UserDevice ud)
        {
            Layout layout = ud?.Device switch
            {
                SdlDeviceWrapper w => IsSdlOrderDualShock3(w.VendorId, w.ProductId, w.RawAxisCount, w.RawButtonCount) ? Layout.Ds3
                    : IsHidapi(w.SdlGuid) && IsXidGamepad(w.VendorId, w.ProductId, w.RawAxisCount, w.RawButtonCount) ? Layout.Xid
                    : Layout.None,
                // A pad shared over Remote Link answers from the owner's
                // counts the link carries. The proxy's own RawButtonCount
                // folds the driver's 11 or 15 into the 22 gamepad positions,
                // so the owner's count comes from Info. An old peer that
                // sends no raw counts gets no answer.
                RemotePeerDevice r => IsSdlOrderDualShock3(r.Info.VendorId, r.Info.ProductId,
                        r.Info.RawAxisCount, r.Info.RawButtonCount) ? Layout.Ds3
                    : IsXidGamepad(r.Info.VendorId, r.Info.ProductId, r.Info.RawAxisCount, r.Info.RawButtonCount) ? Layout.Xid
                    : Layout.None,
                null => CachedLayout(ud),
                _ => Layout.None,
            };
            if (layout == Layout.None) return null;
            var axes = new int[PadForge.Engine.Common.ButtonPressureState.Count];
            for (int i = 0; i < axes.Length; i++)
                axes[i] = layout == Layout.Ds3 ? Ds3FirstPressureAxis + i
                    : i < XidPressureTargets ? XidFirstPressureAxis + i
                    : -1;
            return axes;
        }

        /// <summary>The layout of a cached pad. A DualShock 3 answers by the
        /// raw button count its driver implies. An original Xbox controller
        /// answers by its ID and axes, through HIDAPI, the backend the fork's
        /// XID driver runs on.</summary>
        private static Layout CachedLayout(UserDevice ud)
        {
            if (ud == null) return Layout.None;
            if (CachedRawButtons(ud.SdlGuid) is int buttons
                && IsSdlOrderDualShock3(ud.VendorId, ud.ProdId, ud.RawAxisCount, buttons))
                return Layout.Ds3;
            return IsHidapi(ud.SdlGuid) && IsXidGamepad(ud.VendorId, ud.ProdId, ud.RawAxisCount, rawButtons: null)
                ? Layout.Xid
                : Layout.None;
        }

        /// <summary>True when the SDL GUID names HIDAPI as the driver
        /// (SDL_CreateJoystickGUID data[14] 'h').</summary>
        private static bool IsHidapi(string sdlGuid) => SdlDeviceWrapper.BackendFromGuid(sdlGuid) == "hidapi";

        /// <summary>The raw button count of the driver that last opened a
        /// cached pad. The entry itself keeps the larger of that count and the
        /// 22 gamepad positions (UserDevice.LoadFromDevice), which hides 11,
        /// 15 and DsHidMini SDF's 17 alike. The SDL GUID it also keeps names
        /// the driver in its signature byte (SDL_CreateJoystickGUID data[14]):
        /// HIDAPI is SDL's PS3 driver for a DualShock 3, and a virtual
        /// joystick with the DualShock 3's ids is PadForge's own reader. Any
        /// other driver gives no answer.</summary>
        private static int? CachedRawButtons(string sdlGuid) => SdlDeviceWrapper.BackendFromGuid(sdlGuid) switch
        {
            "hidapi" => SdlPs3DriverButtons,
            "virtual" => Ds3DirectService.VirtualJoystickButtons,
            _ => null,
        };
    }
}
