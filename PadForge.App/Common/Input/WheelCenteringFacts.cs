using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Steering;

namespace PadForge.Common.Input
{
    /// <summary>
    /// What a device's wheel can do for centering (#588), by the tests the
    /// force feedback pass makes, so the Centering card and the Release Test
    /// offer what the pass will run.
    /// </summary>
    /// <param name="NativeWheel">A Logitech, a Fanatec or a Thrustmaster
    /// wheel, driven by PadForge's own writer, which also sets its
    /// range.</param>
    /// <param name="VehicleAvailable">The wheel can run Vehicle mode.</param>
    /// <param name="SpringAvailable">The wheel has a spring for Auto
    /// Centering Strength.</param>
    /// <param name="ListedTorque">The maker's figure in N·m, or 0.</param>
    /// <param name="Effects">The effects the wheel runs itself.</param>
    /// <param name="DamperSteps">The steps its damper keeps a coefficient
    /// in.</param>
    internal readonly record struct WheelCenteringFacts(bool NativeWheel, bool VehicleAvailable, bool SpringAvailable,
        double ListedTorque, CenteringEffects Effects, int DamperSteps)
    {
        /// <summary>The wheel has a Centering card: either mode runs on it.</summary>
        public bool HasCard => VehicleAvailable || SpringAvailable;

        /// <summary>A Logitech, a Fanatec or a Thrustmaster wheel: the ones
        /// PadForge's own writers drive.</summary>
        public static bool IsNativeWheel(ushort vendorId, ushort productId)
            => LogitechRawHidWriter.IsLogitechWheel(vendorId, productId)
               || FanatecRawHidWriter.IsFanatecWheel(vendorId, productId)
               || ThrustmasterRawHidWriter.IsThrustmasterWheel(vendorId, productId);

        /// <summary>The facts of a device's wheel, or none for a device that
        /// is no wheel. A Logitech, a Fanatec or a Thrustmaster wheel has
        /// them by its saved ids, connected or not, so its card shows while
        /// the wheel is unplugged. A wheel routed through SDL has them only
        /// while it is connected, since they are read from its open
        /// device.</summary>
        public static WheelCenteringFacts Of(UserDevice ud)
        {
            if (ud == null) return default;
            double listed = WheelTorqueList.For(ud.VendorId, ud.ProdId);
            if (IsNativeWheel(ud.VendorId, ud.ProdId))
            {
                return new WheelCenteringFacts(true, true, true, listed, CenteringEffects.All,
                    WheelCenteringWriter.DamperSteps(ud.VendorId, ud.ProdId));
            }
            // A wheel routed through SDL: what its driver lists. The belt on the
            // saved row's type is the Wheel group's own (#282).
            var device = ud.Device;
            if (device == null || ud.CapType != InputDeviceType.Driving) return default;
            return new WheelCenteringFacts(false,
                ForceFeedbackState.IsGenericWheelVehicleCapable(device),
                ForceFeedbackState.IsGenericWheelSpringCapable(device),
                listed,
                ud.ForceFeedbackState?.CenteringEffectsOf(device) ?? CenteringEffects.None,
                WheelCalibration.FineDamperSteps);
        }
    }
}
