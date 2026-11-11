using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// The Wheel tab's Rotation Range slider offers the range the wheel takes
/// (discussion #512). It offered 40° to 2,520° on every wheel while each
/// writer clamped to its model in silence, so a TS-PC set to 40° was sent 140°.
/// The bounds load with the device whose range loads, so switching between
/// two wheels on a slot never clamps one wheel's range by the other's.
/// </summary>
[Collection("SettingsManagerStatics")]
public class WheelRangeLimitsTests
{
    [Theory]
    // Thrustmaster: the T300RS range command, within the range Thrustmaster
    // documents for each model.
    [InlineData(0x044F, 0xB66E, 40, 1080)]   // T300RS (PS3 normal), also the T-GT II
    [InlineData(0x044F, 0xB66F, 40, 1080)]   // T300RS (PS3 advanced)
    [InlineData(0x044F, 0xB66D, 40, 1080)]   // T300RS (PS4)
    [InlineData(0x044F, 0xB689, 40, 1080)]   // TS-PC Racer (#512)
    [InlineData(0x044F, 0xB692, 40, 1080)]   // TS-XW
    [InlineData(0x044F, 0xB696, 180, 900)]   // T248
    [InlineData(0x044F, 0xB669, 140, 900)]   // TX
    // Logitech: lg4ff_devices, a wheel with a fixed range pinned to it.
    [InlineData(0x046D, 0xC24F, 40, 900)]    // G29
    [InlineData(0x046D, 0xC266, 40, 900)]    // G923
    [InlineData(0x046D, 0xC29B, 40, 900)]    // G27
    [InlineData(0x046D, 0xC298, 40, 900)]    // Driving Force Pro
    [InlineData(0x046D, 0xC295, 270, 270)]   // MOMO Force
    [InlineData(0x046D, 0xCA03, 270, 270)]   // MOMO Racing
    [InlineData(0x046D, 0xC294, 270, 270)]   // Driving Force
    [InlineData(0x046D, 0xC293, 180, 180)]   // WingMan Formula Force GP
    // Fanatec: hid-ftec's min_range 90 up to the base's maximum.
    [InlineData(0x0EB7, 0x0E03, 90, 1080)]   // CSL Elite
    [InlineData(0x0EB7, 0x0001, 90, 900)]    // ClubSport V2
    [InlineData(0x0EB7, 0x0020, 90, 2520)]   // CSL DD
    public void EachWheelOffersTheRangeItsWriterSends(int vendorId, int productId, int min, int max)
    {
        Assert.Equal((min, max), WheelRangeLimits.For((ushort)vendorId, (ushort)productId));
    }

    [Theory]
    [InlineData(0x045E, 0x02FF)]   // Xbox One controller
    [InlineData(0x044F, 0xB10A)]   // T.16000M joystick
    [InlineData(0x044F, 0xB677)]   // T150, another protocol
    public void ADeviceNoWriterDrivesHasNoBounds(int vendorId, int productId)
    {
        Assert.Null(WheelRangeLimits.For((ushort)vendorId, (ushort)productId));
    }

    [Fact]
    public void TheRangeLoadsInsideTheWheelsBounds()
    {
        var vm = new MainViewModel().Pads[0];
        vm.LoadWheelRotationRange(40, (40, 1080));
        Assert.Equal((40, 1080, 40), (vm.WheelRangeMinimum, vm.WheelRangeMaximum, vm.WheelRotationRange));
        // A stored range the wheel cannot turn loads as the bound it turns.
        vm.LoadWheelRotationRange(150, (180, 900));
        Assert.Equal((180, 900, 180), (vm.WheelRangeMinimum, vm.WheelRangeMaximum, vm.WheelRotationRange));
        vm.LoadWheelRotationRange(900, (270, 270));
        Assert.Equal((270, 270, 270), (vm.WheelRangeMinimum, vm.WheelRangeMaximum, vm.WheelRotationRange));
        // Out of a pinned range into a wider one: the bounds move apart
        // without the minimum passing the maximum.
        vm.LoadWheelRotationRange(2000, (90, 2520));
        Assert.Equal((90, 2520, 2000), (vm.WheelRangeMinimum, vm.WheelRangeMaximum, vm.WheelRotationRange));
        vm.WheelRotationRange = 3000;
        Assert.Equal(2520, vm.WheelRotationRange);
        // No native writer: the widest any writer takes.
        vm.LoadWheelRotationRange(900, null);
        Assert.Equal((WheelRangeLimits.AnyMin, WheelRangeLimits.AnyMax, 900),
            (vm.WheelRangeMinimum, vm.WheelRangeMaximum, vm.WheelRotationRange));
    }

    /// <summary>A device switch saves the outgoing device's values, then
    /// loads the incoming device's. A CSL DD at 2,520° and a G29 on one slot
    /// each keep their own range across switches both ways, so nothing clamps
    /// the CSL DD by the G29's 900° before its save.</summary>
    [Fact]
    public void TwoWheelsOnOneSlotKeepTheirOwnRanges()
    {
        var oldSettings = SettingsManager.UserSettings;
        var oldDevices = SettingsManager.UserDevices;
        InputService input = null;
        try
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            var dd = AddWheel(0x0EB7, 0x0020, "2520");
            var g29 = AddWheel(0x046D, 0xC24F, "540");

            var main = new MainViewModel();
            input = new InputService(main);
            var pad = main.Pads[0];
            var ddRow = new PadViewModel.MappedDeviceInfo { InstanceGuid = dd.Device, Name = "CSL DD", IsOnline = true };
            var g29Row = new PadViewModel.MappedDeviceInfo { InstanceGuid = g29.Device, Name = "G29", IsOnline = true };
            pad.MappedDevices.Add(ddRow);
            pad.MappedDevices.Add(g29Row);

            pad.SelectedMappedDevice = ddRow;
            Assert.Equal((90, 2520, 2520), (pad.WheelRangeMinimum, pad.WheelRangeMaximum, pad.WheelRotationRange));
            pad.SelectedMappedDevice = g29Row;
            Assert.Equal((40, 900, 540), (pad.WheelRangeMinimum, pad.WheelRangeMaximum, pad.WheelRotationRange));
            Assert.Equal("2520", dd.Setting.GetPadSetting().RotationRange);
            pad.SelectedMappedDevice = ddRow;
            Assert.Equal((90, 2520, 2520), (pad.WheelRangeMinimum, pad.WheelRangeMaximum, pad.WheelRotationRange));
            Assert.Equal("540", g29.Setting.GetPadSetting().RotationRange);
        }
        finally
        {
            if (input != null)
            {
                PadForge.Resources.Strings.Strings.CultureChanged -=
                    (Action)Delegate.CreateDelegate(typeof(Action), input,
                        typeof(InputService).GetMethod("OnCultureChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
                input.Dispose();
            }
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.UserDevices = oldDevices;
        }
    }

    /// <summary>The settings file's load bounds the range by the device
    /// whose setting it loads, as the device switch does.</summary>
    [Fact]
    public void TheStartupLoadBoundsTheRangeByItsDevice()
    {
        var oldSettings = SettingsManager.UserSettings;
        var oldDevices = SettingsManager.UserDevices;
        try
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            var t248 = AddWheel(0x044F, 0xB696, "150");
            var main = new MainViewModel();
            var store = new SettingsService(main);
            typeof(SettingsService).GetMethod("LoadPadSettings", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(store, new object[] { new[] { t248.Setting }, new[] { t248.Setting.GetPadSetting() } });
            var pad = main.Pads[0];
            Assert.Equal((180, 900, 180), (pad.WheelRangeMinimum, pad.WheelRangeMaximum, pad.WheelRotationRange));
        }
        finally
        {
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.UserDevices = oldDevices;
        }
    }

    /// <summary>The Pad page's slider, built from its own markup, writes no
    /// pad's range when the page moves between pads whose wheels take
    /// different ranges. WPF clamps the slider's value to new bounds without
    /// writing it back, and the bounds and value of each pad already agree.</summary>
    [Fact]
    public void ThePagesSliderWritesNoRangeWhenItMovesBetweenPads()
    {
        RunSta(() =>
        {
            var main = new MainViewModel();
            var tsPc = main.Pads[0];
            tsPc.LoadWheelRotationRange(40, (40, 1080));
            var momo = main.Pads[1];
            momo.LoadWheelRotationRange(270, (270, 270));
            int writes = 0;
            void Count(object s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(PadViewModel.WheelRotationRange)) writes++;
            }
            tsPc.PropertyChanged += Count;
            momo.PropertyChanged += Count;

            var slider = (Slider)XamlReader.Parse(RotationRangeSliderMarkup());
            var host = new ContentControl { Content = slider };
            var window = new Window
            {
                Content = host, Width = 300, Height = 80, Left = -10000,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false,
            };
            window.Show();
            try
            {
                host.DataContext = tsPc;
                Pump();
                Assert.Equal((40.0, 1080.0, 40.0), (slider.Minimum, slider.Maximum, slider.Value));
                host.DataContext = momo;
                Pump();
                Assert.Equal((270.0, 270.0, 270.0), (slider.Minimum, slider.Maximum, slider.Value));
                host.DataContext = tsPc;
                Pump();
                Assert.Equal((40.0, 1080.0, 40.0), (slider.Minimum, slider.Maximum, slider.Value));
                Assert.Equal(0, writes);
                Assert.Equal(40, tsPc.WheelRotationRange);
                Assert.Equal(270, momo.WheelRotationRange);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Set Rotation Range off leaves the range to the wheel and its
    /// own software (#512). A new device sends its range, and one left to the
    /// wheel sends none, on this PC or through a peer that owns the wheel.</summary>
    [Fact]
    public void ARangeLeftToTheWheelIsNeverSent()
    {
        Assert.Equal("1", new PadSetting().RotationRangeEnabled);
        Assert.True(WheelRangeLimits.SendsRange(new PadSetting()));
        Assert.False(WheelRangeLimits.SendsRange(new PadSetting { RotationRangeEnabled = "0" }));

        var left = new OutputEffectCodec.WheelFrame(false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            100, 0, 540, 0, rangeLeftToWheel: true);
        Assert.Equal(0, InputService.RemoteWheelRange(left));
        var sent = new OutputEffectCodec.WheelFrame(false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            100, 0, 540, 0);
        Assert.Equal(540, InputService.RemoteWheelRange(sent));
    }

    /// <summary>The switch is part of the device's settings: the settings
    /// checksum sees it, a clone keeps it, and a load reads it back.</summary>
    [Fact]
    public void TheSwitchTravelsWithTheDevicesSettings()
    {
        var off = new PadSetting { RotationRangeEnabled = "0" };
        Assert.NotEqual(new PadSetting().ComputeChecksum(), off.ComputeChecksum());
        Assert.Equal("0", off.CloneDeep().RotationRangeEnabled);

        var vm = new MainViewModel().Pads[0];
        InputService.LoadPadSettingIntoViewModel(vm, off);
        Assert.False(vm.WheelRotationRangeEnabled);
        InputService.LoadPadSettingIntoViewModel(vm, new PadSetting());
        Assert.True(vm.WheelRotationRangeEnabled);
    }

    /// <summary>The row's own reset and the Wheel card's Reset All both turn
    /// the switch back on.</summary>
    [Fact]
    public void BothResetsTurnTheSwitchBackOn()
    {
        var vm = new MainViewModel().Pads[0];
        vm.WheelRotationRangeEnabled = false;
        vm.ResetSettingCommand.Execute(nameof(PadViewModel.WheelRotationRangeEnabled));
        Assert.True(vm.WheelRotationRangeEnabled);
        vm.WheelRotationRangeEnabled = false;
        vm.ResetWheelAllCommand.Execute(null);
        Assert.True(vm.WheelRotationRangeEnabled);
    }

    /// <summary>Two wheels on one slot each keep their own switch across a
    /// device switch, which saves the outgoing wheel before the incoming one
    /// loads.</summary>
    [Fact]
    public void EachWheelKeepsItsOwnSwitch()
    {
        var oldSettings = SettingsManager.UserSettings;
        var oldDevices = SettingsManager.UserDevices;
        InputService input = null;
        try
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            var tsPc = AddWheel(0x044F, 0xB689, "540");
            var g29 = AddWheel(0x046D, 0xC24F, "900");

            var main = new MainViewModel();
            input = new InputService(main);
            var pad = main.Pads[0];
            var tsPcRow = new PadViewModel.MappedDeviceInfo { InstanceGuid = tsPc.Device, Name = "TS-PC", IsOnline = true };
            var g29Row = new PadViewModel.MappedDeviceInfo { InstanceGuid = g29.Device, Name = "G29", IsOnline = true };
            pad.MappedDevices.Add(tsPcRow);
            pad.MappedDevices.Add(g29Row);

            pad.SelectedMappedDevice = tsPcRow;
            Assert.True(pad.WheelRotationRangeEnabled);
            pad.WheelRotationRangeEnabled = false;
            pad.SelectedMappedDevice = g29Row;
            Assert.True(pad.WheelRotationRangeEnabled);
            Assert.Equal("0", tsPc.Setting.GetPadSetting().RotationRangeEnabled);
            pad.SelectedMappedDevice = tsPcRow;
            Assert.False(pad.WheelRotationRangeEnabled);
            Assert.Equal("1", g29.Setting.GetPadSetting().RotationRangeEnabled);
        }
        finally
        {
            if (input != null)
            {
                PadForge.Resources.Strings.Strings.CultureChanged -=
                    (Action)Delegate.CreateDelegate(typeof(Action), input,
                        typeof(InputService).GetMethod("OnCultureChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
                input.Dispose();
            }
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.UserDevices = oldDevices;
        }
    }

    /// <summary>The Rotation Range row rests while the range is left to the
    /// wheel: the switch sits above it and turns the slider's row off.</summary>
    [Fact]
    public void TheSliderRestsWhileTheRangeIsLeftToTheWheel()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "PadForge.App"))) d = d.Parent;
        Assert.NotNull(d);
        string page = File.ReadAllText(Path.Combine(d.FullName, "PadForge.App", "Views", "PadPage.xaml"));
        int box = page.IndexOf("IsChecked=\"{Binding WheelRotationRangeEnabled, Mode=TwoWay}\"", StringComparison.Ordinal);
        int row = page.IndexOf("<DockPanel LastChildFill=\"False\" MinHeight=\"44\" IsEnabled=\"{Binding WheelRotationRangeEnabled}\">", StringComparison.Ordinal);
        int slider = page.IndexOf("<Slider Value=\"{Binding WheelRotationRange, Mode=TwoWay}\"", StringComparison.Ordinal);
        Assert.True(box > 0 && row > box && slider > row, "the switch, its row and the slider are out of order");
        Assert.DoesNotContain("</DockPanel>", page.Substring(row, slider - row));
    }

    private static (Guid Device, UserSetting Setting) AddWheel(int vendorId, int productId, string range)
    {
        var id = Guid.NewGuid();
        SettingsManager.UserDevices.Items.Add(new UserDevice
        {
            InstanceGuid = id, ProductGuid = id, VendorId = (ushort)vendorId, ProdId = (ushort)productId,
            CapType = InputDeviceType.Driving, IsOnline = true, IsEnabled = true,
        });
        var us = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = 0, IsEnabled = true };
        us.SetPadSetting(new PadSetting { RotationRange = range });
        SettingsManager.UserSettings.Items.Add(us);
        return (id, us);
    }

    /// <summary>The Rotation Range slider as PadPage.xaml writes it, its
    /// style left out, since the page's resources are not loaded here.</summary>
    private static string RotationRangeSliderMarkup()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
            d = d.Parent;
        Assert.True(d != null, "repository root not found");
        string page = File.ReadAllText(Path.Combine(d.FullName, "PadForge.App", "Views", "PadPage.xaml"));
        var match = Regex.Match(page, @"<Slider Value=""\{Binding WheelRotationRange, Mode=TwoWay\}""[^>]*/>");
        Assert.True(match.Success, "the Rotation Range slider was not found in PadPage.xaml");
        string slider = Regex.Replace(match.Value, @"\s+Style=""\{StaticResource [^}]*\}""", "");
        return slider.Replace("<Slider ",
            "<Slider xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
            + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>WPF elements need an STA thread.</summary>
    private static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(30000), "the slider test timed out");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
