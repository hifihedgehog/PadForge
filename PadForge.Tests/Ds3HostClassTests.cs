using System;
using System.Linq;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The class sweep behind the DS3's background WinUSB auto-bind (#265).
    ///
    /// <para>The gate shipped searching GUID_DEVCLASS_USB alone, which is the one
    /// setup class a DS3 is never in: that class holds host controllers, root hubs,
    /// hubs and composite parents. A USB function device bound to the inbox HidUsb
    /// sits in HIDCLASS, because HidUsb is installed by input.inf with
    /// Class=HIDClass. The gate therefore returned false for exactly the state it
    /// exists to detect and the auto-bind was unreachable from 4.0.1 through
    /// 4.1.0.</para>
    ///
    /// <para>These assertions are over the CLASS LIST rather than over live PnP,
    /// because the defect was entirely in which classes get searched. A machine
    /// running the suite has no DS3 attached, so a live probe would pass vacuously
    /// on the broken code and prove nothing.</para>
    /// </summary>
    public class Ds3HostClassTests
    {
        // Windows setup-class GUIDs, spelled independently of the production list so
        // a typo there cannot make these agree with it by construction.
        private static readonly Guid HidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");
        private static readonly Guid Unknown = new("4D36E97E-E325-11CE-BFC1-08002BE10318");
        private static readonly Guid UsbBus = new("36FC9E60-C465-11CF-8056-444553540000");

        /// <summary>THE regression. A DS3 on the inbox driver is in HIDCLASS, so a
        /// sweep that omits it can never see the pad it is meant to rescue.</summary>
        [Fact]
        public void TheSweep_CoversTheInboxHidDriversClass()
        {
            Assert.Contains(HidClass, Ds3DriverInstaller.Ds3HostClasses);
        }

        /// <summary>The docstring promises "still on the inbox HID driver (or no
        /// driver)". A driver-less device enumerates under UNKNOWN, so that leg needs
        /// its own class or the second half of the promise is unimplemented.</summary>
        [Fact]
        public void TheSweep_CoversTheDriverlessClass()
        {
            Assert.Contains(Unknown, Ds3DriverInstaller.Ds3HostClasses);
        }

        /// <summary>Searching the bus class is not wrong, it is just not sufficient.
        /// It stays so a composite parent is still seen, where the service allowlist
        /// then rejects it for running usbccgp.</summary>
        [Fact]
        public void TheSweep_StillCoversTheBusClassForCompositeParents()
        {
            Assert.Contains(UsbBus, Ds3DriverInstaller.Ds3HostClasses);
        }

        /// <summary>A class listed twice would double-enumerate every node and give
        /// the allowlist the same device more than once.</summary>
        [Fact]
        public void TheSweep_ListsNoClassTwice()
        {
            var all = Ds3DriverInstaller.Ds3HostClasses;
            Assert.Equal(all.Length, all.Distinct().Count());
        }

        /// <summary>A one-entry sweep is the shape of the bug. Any future edit that
        /// collapses the list back to a single class fails here, whichever class it
        /// picks, because no single class covers both the inbox-HID and driver-less
        /// states.</summary>
        [Fact]
        public void TheSweep_IsNotASingleClassAgain()
        {
            Assert.True(Ds3DriverInstaller.Ds3HostClasses.Length >= 2,
                "the DS3 class sweep collapsed to a single setup class, which is the " +
                "exact shape of #265: no one class holds both a HidUsb-bound pad and " +
                "a driver-less one");
        }
    }
}
