using System;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Engine.Steering;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        /// <summary>The Centering section of a device's settings into the
        /// pad's view model (#588), with the facts of the device's wheel and
        /// whether the Release Test has measured it.</summary>
        internal static void LoadCenteringSettings(PadViewModel padVm, PadSetting ps, Guid deviceGuid)
        {
            var ud = FindCenteringDevice(deviceGuid);
            padVm.LoadCentering(ps, WheelCenteringFacts.Of(ud));
            padVm.CenteringMeasured = WheelCalibration.Parse(ud?.WheelCalibration)?.IsValid == true;
        }

        /// <summary>The device with this identity, for the Centering card's
        /// live view and the Release Test.</summary>
        internal static UserDevice FindCenteringDevice(Guid deviceGuid)
            => deviceGuid == Guid.Empty ? null : FindUserDevice(deviceGuid);
    }
}
