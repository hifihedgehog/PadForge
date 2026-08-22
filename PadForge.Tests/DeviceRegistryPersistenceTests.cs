using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// When the device registry reaches disk, and when it must not.
    ///
    /// <para>Two opposite defects framed this. Marking the settings dirty on
    /// row CREATION alone missed a row minted by another lane: the PS Move's
    /// is created from identity with no capabilities, filled in memory when
    /// the pad connects, and never saved, so it showed "0 axes, 0 buttons,
    /// 0 POV(s)" whenever offline. Marking on every ARRIVAL then rewrote the
    /// whole config every five seconds on an idle machine, because the
    /// consumer-control lane re-opens its devices about every ten seconds
    /// and LoadCapabilities stamps DateUpdated each time.</para>
    ///
    /// <para>The signature answers the question both attempts approximated:
    /// did anything that reaches disk actually change.</para>
    /// </summary>
    public class DeviceRegistryPersistenceTests
    {
        private static UserDevice Dev(string name = "Pad", int buttons = 11, string serial = "")
            => new UserDevice
            {
                InstanceGuid = System.Guid.Parse("11111111-2222-3333-4444-555555555555"),
                InstanceName = name,
                DevicePath = @"\?\bthps3bus#x",
                SerialNumber = serial,
                CapType = InputDeviceType.Gamepad,
                CapAxeCount = 6,
                CapButtonCount = buttons,
                CapPovCount = 1,
                RawButtonCount = 22,
                CapButtonIndices = new[] { 0, 1, 2 },
            };

        private static string Sig(params UserDevice[] devices)
            => InputService.BuildDeviceRegistrySignature(devices);

        /// <summary>THE IDLE REWRITE. DateUpdated changes on every re-open of
        /// a device and nothing reads it back, so it must not reach the
        /// signature: including it is what rewrote the config forever.</summary>
        [Fact]
        public void DateUpdated_DoesNotCountAsAChange()
        {
            var a = Dev();
            string before = Sig(a);
            a.DateUpdated = a.DateUpdated.AddMinutes(5);
            Assert.Equal(before, Sig(a));
        }

        /// <summary>THE UNSAVED CAPABILITIES. A minted row filling in its
        /// capabilities on connect is exactly the change that has to persist,
        /// and it happens on a row that already exists.</summary>
        [Fact]
        public void CapabilitiesArrivingOnAnExistingRow_CountAsAChange()
        {
            var minted = new UserDevice
            {
                InstanceGuid = System.Guid.Parse("11111111-2222-3333-4444-555555555555"),
                InstanceName = "PlayStation Move Motion Controller",
            };
            string before = Sig(minted);

            minted.CapType = InputDeviceType.Gamepad;
            minted.CapAxeCount = 6;
            minted.CapButtonCount = 11;
            minted.CapPovCount = 1;

            Assert.NotEqual(before, Sig(minted));
        }

        /// <summary>A row appearing or disappearing is a change.</summary>
        [Fact]
        public void AddingOrRemovingARow_CountsAsAChange()
        {
            string one = Sig(Dev());
            string none = Sig();
            Assert.NotEqual(one, none);
        }

        /// <summary>The Bluetooth address the idle disconnect targets. It
        /// arrives long after the row does, from a dock or a device node, and
        /// a row that keeps it only in memory offers a control that vanishes
        /// on restart.</summary>
        [Fact]
        public void AnAddressArriving_CountsAsAChange()
        {
            string before = Sig(Dev(serial: ""));
            Assert.NotEqual(before, Sig(Dev(serial: "00265c507543")));
        }

        /// <summary>Button positions, which the offline listing renders.</summary>
        [Fact]
        public void ButtonPositionsChanging_CountAsAChange()
        {
            var a = Dev();
            string before = Sig(a);
            a.CapButtonIndices = new[] { 0, 1, 2, 12, 13 };
            Assert.NotEqual(before, Sig(a));
        }

        /// <summary>Every persisted field the arrival path stamps counts.
        /// The signature is a mirror of UserDevice's saved state, and a
        /// field LoadFromDevice writes but the signature omits is a field
        /// whose change never reaches disk. That is the same defect the
        /// signature replaced, one field at a time.</summary>
        [Theory]
        [InlineData("SdlGuid")]
        [InlineData("VendorId")]
        [InlineData("ProdId")]
        [InlineData("RawAxisCount")]
        [InlineData("ActuatorCount")]
        [InlineData("CapTouchpadCount")]
        [InlineData("HasGyro")]
        [InlineData("HasAccel")]
        [InlineData("HasGyroAux")]
        [InlineData("HasAccelAux")]
        [InlineData("HasTouchpad")]
        [InlineData("HasRumbleTriggers")]
        [InlineData("HasExtraGenericAxes")]
        [InlineData("CapTouchpadFingerCounts")]
        [InlineData("DeviceObjects")]
        public void EveryArrivalStampedField_CountsAsAChange(string field)
        {
            var a = Dev();
            string before = Sig(a);

            switch (field)
            {
                case "SdlGuid": a.SdlGuid = "deadbeef"; break;
                case "VendorId": a.VendorId = 0x054C; break;
                case "ProdId": a.ProdId = 0x0268; break;
                case "RawAxisCount": a.RawAxisCount = 16; break;
                case "ActuatorCount": a.ActuatorCount = 2; break;
                case "CapTouchpadCount": a.CapTouchpadCount = 2; break;
                case "HasGyro": a.HasGyro = true; break;
                case "HasAccel": a.HasAccel = true; break;
                case "HasGyroAux": a.HasGyroAux = true; break;
                case "HasAccelAux": a.HasAccelAux = true; break;
                case "HasTouchpad": a.HasTouchpad = true; break;
                case "HasRumbleTriggers": a.HasRumbleTriggers = true; break;
                case "HasExtraGenericAxes": a.HasExtraGenericAxes = true; break;
                case "CapTouchpadFingerCounts": a.CapTouchpadFingerCounts = new[] { 2, 2 }; break;
                case "DeviceObjects": a.DeviceObjects = new DeviceObjectItem[3]; break;
                default: Assert.Fail("unhandled field " + field); break;
            }

            Assert.NotEqual(before, Sig(a));
        }

        /// <summary>Nothing changed, nothing written. Stated directly,
        /// because this is the property the idle machine depends on.</summary>
        [Fact]
        public void AnUnchangedRegistry_ProducesAnIdenticalSignature()
        {
            var list = new List<UserDevice> { Dev("A"), Dev("B", buttons: 15) };
            Assert.Equal(
                InputService.BuildDeviceRegistrySignature(list),
                InputService.BuildDeviceRegistrySignature(list));
        }

        /// <summary>A null collection is empty, not a crash: the registry can
        /// be unset early in startup.</summary>
        [Fact]
        public void ANullRegistry_IsEmpty()
        {
            Assert.Equal(string.Empty, InputService.BuildDeviceRegistrySignature(null));
        }
    }
}
