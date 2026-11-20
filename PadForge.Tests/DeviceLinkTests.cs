using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Devices page names a keyboard's and a mouse's link as it names a
    /// controller's.
    ///
    /// <para>The page named Bluetooth alone, so a USB keyboard, a laptop's
    /// PS/2 keyboard and its touchpad showed no link at all (owner,
    /// 2026-11-17). The device tree names each: the driver under the device
    /// and the bus that enumerated each node above it. The walks below are
    /// the ones read off a laptop's tree that day.</para>
    /// </summary>
    public class DeviceLinkTests
    {
        private static AncestryHop Hop(string instanceId, string service = null)
            => new(instanceId, Array.Empty<string>(), service);

        private static readonly AncestryHop[] UsbKeyboard =
        {
            Hop(@"HID\VID_048D&PID_C197&COL04\8&11B458CB&0&0003", "kbdhid"),
            Hop(@"USB\VID_048D&PID_C197\7&14999c31&0&4", "HidUsb"),
            Hop(@"USB\VID_05E3&PID_0610\6&6e5fe31&0&2", "USBHUB3"),
            Hop(@"USB\ROOT_HUB30\5&2b4ec4c7&0&0", "USBHUB3"),
            Hop(@"PCI\VEN_1022&DEV_15B7&SUBSYS_381117AA&REV_00\4&16012499&0&0441", "USBXHCI"),
        };

        [Fact]
        public void TheLaptopsDevicesNameTheirLinks()
        {
            Assert.Equal(DeviceLink.Usb, DeviceTransport.LinkOf(UsbKeyboard));
            Assert.Equal(DeviceLink.Ps2, DeviceTransport.LinkOf(new[]
            {
                Hop(@"ACPI\KBC8042\4&2574F154&0", "i8042prt"),
                Hop(@"PCI\VEN_1022&DEV_790E&SUBSYS_D6011022&REV_51\3&11583659&0&A3", "msisadrv"),
            }));
            Assert.Equal(DeviceLink.I2c, DeviceTransport.LinkOf(new[]
            {
                Hop(@"HID\GXTP5100&COL01\4&C834CD3&0&0000", "mouhid"),
                Hop(@"ACPI\GXTP5100\3&62d7e73&0", "hidi2c"),
                Hop(@"ACPI\AMDI0010\0", "iaLPSS2i_I2C"),
            }));
            Assert.Equal(DeviceLink.Bluetooth, DeviceTransport.LinkOf(new[]
            {
                Hop(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\A&32928F16&A&0000"),
                Hop(@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\9&1479b2ee&0&0C275670ABDA_C00000000", "HidBth"),
                Hop(@"BTH\MS_BTHBRB\8&23b5fab&1&1", "BthEnum"),
                Hop(@"USB\VID_8087&PID_0032\7&14999c31&0&3", "BTHUSB"),
            }));
            Assert.Equal(DeviceLink.Bluetooth, DeviceTransport.LinkOf(new[]
            {
                Hop(@"HID\{00001812-0000-1000-8000-00805F9B34FB}&DEV&VID_045E&PID_0B13&REV_0523&A88C3E2C8A96&IG_00\B&892F461&1&0000"),
                Hop(@"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13_REV&0523_a88c3e2c8a96\a&339b5747&2&0016", "mshidumdf"),
            }));
        }

        /// <summary>Software makes no claim: a virtual controller, a remote
        /// desktop's mouse, and a USB device a virtual host controller
        /// presents.</summary>
        [Fact]
        public void AVirtualDeviceHasNoLink()
        {
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(new[]
            {
                Hop(@"HID\HIDMAESTRO_VID_045E_PID_0B13&IG_00\1&D3DD7A1F&0&0000"),
                Hop(@"SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\HM_D3DD7A1FDF0FD306", "mshidumdf"),
            }));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(new[]
            {
                Hop(@"HID\HIDCLASS\1&2D595CA7&1499&0000", "mouhid"),
                Hop(@"ROOT\HIDCLASS\0000", "mshidkmdf"),
            }));
            // ViGEmBus: a DualShock 4 on USB, under a root-enumerated bus.
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(new[]
            {
                Hop(@"HID\VID_054C&PID_05C4\9&1&0&0000"),
                Hop(@"USB\VID_054C&PID_05C4\1&2&3&4", "HidUsb"),
                Hop(@"ROOT\SYSTEM\0001", "ViGEmBus"),
            }));
            // usbip-win2: a USB device under its emulated host controller.
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(new[]
            {
                Hop(@"HID\VID_045E&PID_0B12\9&1&0&0000"),
                Hop(@"USB\VID_045E&PID_0B12\HM0123456789AB", "HidUsb"),
                Hop(@"USB\ROOT_HUB30\8&1&0", "USBHUB3"),
                Hop(@"ROOT\USB\0000", "usbip2_ude"),
            }));
        }

        [Fact]
        public void AWalkThatEndsBeforeItSettlesMakesNoClaim()
        {
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(UsbKeyboard.Take(4)));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(Array.Empty<AncestryHop>()));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf((IEnumerable<AncestryHop>)null));
            // The walk reads no more than Step 1's guard does.
            var deep = Enumerable.Repeat(Hop(@"USB\VID_05E3&PID_0610\6&1&0&2", "USBHUB3"), HidMaestroAncestry.MaxHops)
                .Append(Hop(@"PCI\VEN_1022&DEV_15B7\4&1&0&0441", "USBXHCI"));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(deep));
        }

        /// <summary>A path is read off the tree once, a path the tree does
        /// not hold included, and a Bluetooth path or one that names no node
        /// is never walked. A row coming online forgets its path's answer
        /// (DeviceTransport.ForgetLink), so the next read walks again.</summary>
        [Fact]
        public void EachPathIsWalkedOnce()
        {
            const string Keyboard = @"\\?\HID#VID_048D&PID_C197&COL04#8&11b458cb&0&0003#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
            const string Away = @"\\?\HID#VID_046D&PID_C547&MI_00#8&6&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
            const string Bluetooth = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var walked = new List<string>();
            IEnumerable<AncestryHop> Walk(string path)
            {
                walked.Add(path);
                return path == Keyboard ? UsbKeyboard : null;
            }
            var known = new Dictionary<string, DeviceLink>(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(DeviceLink.Usb, DeviceTransport.LinkOf(Keyboard, 0x048D, 0xC197, Walk, known));
            Assert.Equal(DeviceLink.Usb, DeviceTransport.LinkOf(Keyboard, 0x048D, 0xC197, Walk, known));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(Away, 0x046D, 0xC547, Walk, known));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(Away, 0x046D, 0xC547, Walk, known));
            Assert.Equal(DeviceLink.Bluetooth, DeviceTransport.LinkOf(Bluetooth, 0x054C, 0x0CE6, Walk, known));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf("XInput#0", 0x045E, 0x0B12, Walk, known));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf("midi://1", 0, 0, Walk, known));
            Assert.Equal(DeviceLink.None, DeviceTransport.LinkOf(null, 0, 0, Walk, known));
            Assert.Equal(new[] { Keyboard, Away }, walked);
        }

        /// <summary>Both walks that find the node Windows keeps for a
        /// device that has left the tree pass cfgmgr32.h's
        /// CM_LOCATE_DEVNODE_PHANTOM, which is 1. The headset repair passed
        /// 4, CM_LOCATE_DEVNODE_NOVALIDATION, which Windows does not use,
        /// so its fallback found no such node.</summary>
        [Fact]
        public void TheRememberedNodeFlagIsCfgmgr32s()
        {
            foreach (var type in new[] { typeof(PadForge.Services.HeadsetTrackerRepair), typeof(PadForge.Common.Input.InputManager) })
            {
                var field = type.GetField("CM_LOCATE_DEVNODE_PHANTOM", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(field);
                Assert.Equal(1u, (uint)field.GetRawConstantValue());
            }
        }

        [Fact]
        public void ARowNamesItsLinkInWords()
        {
            var pad = new DeviceRowViewModel
            {
                DevicePath = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                VendorId = 0x054C, ProductId = 0x0CE6,
            };
            Assert.Equal(DeviceLink.Bluetooth, pad.Link);
            Assert.True(pad.HasLink);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Pad_Transport_Bluetooth, pad.LinkText);

            var feed = new DeviceRowViewModel { DevicePath = "midi://1" };
            Assert.False(feed.HasLink);
            Assert.Equal("", feed.LinkText);

            var strings = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(new[] { "USB", "PS/2", "I2C" }, new[] { strings.Pad_Transport_Usb, strings.Pad_Transport_Ps2, strings.Pad_Transport_I2c });
        }

        /// <summary>The row's link words follow its path, and are read
        /// again when the device comes back: a device first seen while it
        /// was away is not in the tree then.</summary>
        [Fact]
        public void ANewPathOrAReturnRaisesTheLink()
        {
            var row = new DeviceRowViewModel();
            var raised = new List<string>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            row.DevicePath = "midi://2";
            Assert.Contains(nameof(DeviceRowViewModel.Link), raised);
            Assert.Contains(nameof(DeviceRowViewModel.HasLink), raised);
            Assert.Contains(nameof(DeviceRowViewModel.LinkText), raised);

            raised.Clear();
            row.IsOnline = !row.IsOnline;
            Assert.Contains(nameof(DeviceRowViewModel.Link), raised);
            Assert.Contains(nameof(DeviceRowViewModel.HasLink), raised);
            Assert.Contains(nameof(DeviceRowViewModel.LinkText), raised);
            Assert.Contains(nameof(DeviceRowViewModel.HasLinkOrBattery), raised);
        }
    }
}
