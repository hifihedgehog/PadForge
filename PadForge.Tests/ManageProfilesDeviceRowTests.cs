using System;
using System.Reflection;
using HIDMaestro;
using PadForge.Resources.Strings;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The line under each connected device in Manage Profiles: its ids, its
    /// kind and its maker, built from the interface's strings, so the kind
    /// follows the language and no dash joins the maker on.
    /// </summary>
    public class ManageProfilesDeviceRowTests
    {
        private static HMHidDeviceInfo Info(ushort page, ushort usage, string maker) =>
            (HMHidDeviceInfo)typeof(HMHidDeviceInfo)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0]
                .Invoke(new object[]
                {
                    (ushort)0x046D, (ushort)0xC077, (ushort)0x0100, "Test Device", maker, null,
                    page, usage, (ushort)8, @"\\?\hid#vid_046d&pid_c077#test",
                });

        private static string Line(ushort page, ushort usage, string maker) =>
            new ManageProfilesDialog.DeviceRow { Info = Info(page, usage, maker) }.DetailLine;

        private const string Ids = "VID_046D:PID_C077";

        [Fact]
        public void EachKindIsNamedInTheInterfacesLanguage()
        {
            var s = Strings.Instance;
            Assert.Equal(string.Format(s.ImportFromDevice_DeviceDetailMaker_Format, Ids, s.ImportFromDevice_UsageMouse, "Logitech"),
                Line(0x01, 0x02, "Logitech"));
            Assert.Equal(string.Format(s.ImportFromDevice_DeviceDetail_Format, Ids, s.ImportFromDevice_UsageJoystick),
                Line(0x01, 0x04, null));
            Assert.Equal(string.Format(s.ImportFromDevice_DeviceDetail_Format, Ids, s.ImportFromDevice_UsageGamepad),
                Line(0x01, 0x05, "  "));
            Assert.Equal(string.Format(s.ImportFromDevice_DeviceDetail_Format, Ids, s.ImportFromDevice_UsageKeyboard),
                Line(0x01, 0x06, ""));
            Assert.Equal(string.Format(s.ImportFromDevice_DeviceDetail_Format, Ids, s.ImportFromDevice_UsageMultiAxis),
                Line(0x01, 0x08, null));
            // A usage with no name is its page and number, which need no words.
            Assert.Equal(string.Format(s.ImportFromDevice_DeviceDetailMaker_Format, Ids, "0x0C:0x01", "Maker"),
                Line(0x0C, 0x01, "Maker"));
            Assert.DoesNotContain('\u2014', Line(0x01, 0x02, "Logitech"));
        }
    }
}
