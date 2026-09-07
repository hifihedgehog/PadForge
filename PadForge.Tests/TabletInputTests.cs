using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.Tablets;
using Xunit;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class TabletInputTests
{
    private static UserDevice Tablet(bool pressure) => new()
    {
        CapType = InputDeviceType.Tablet,
        HasTouchpad = true,
        CapTouchpadCount = 1,
        CapTouchpadFingerCounts = new[] { 1 },
        CapTouchpadPressure = pressure,
        CapTouchpadClick = false
    };

    [Fact]
    public void TabletTypePreservesExistingValues()
    {
        Assert.Equal(26, InputDeviceType.Touchpad);
        Assert.Equal(34, InputDeviceType.HeadTracker);
        Assert.Equal(35, InputDeviceType.Tablet);
    }

    [Fact]
    public void HidCapabilityLayoutsMatchTheWindowsAbi()
    {
        Assert.Equal(64, Marshal.SizeOf<TabletNative.Caps>());
        Assert.Equal(72, Marshal.SizeOf<TabletNative.ValueCap>());
        Assert.Equal(72, Marshal.SizeOf<TabletNative.ButtonCap>());
        Assert.Equal(40, Marshal.OffsetOf<TabletNative.ValueCap>(nameof(TabletNative.ValueCap.LogicalMin)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<TabletNative.ValueCap>(nameof(TabletNative.ValueCap.UsageMin)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<TabletNative.ButtonCap>(nameof(TabletNative.ButtonCap.UsageMin)).ToInt32());
    }

    [Theory]
    [InlineData(0u, 0f)]
    [InlineData(32768u, 0.5000076f)]
    [InlineData(65535u, 1f)]
    [InlineData(65536u, 1f)]
    public void UnsignedCoordinateUsesItsDeclaredRange(uint raw, float expected)
    {
        var field = new TabletReportDescriptor.ValueField(1, 1, 1, 0x30, 0, 65535, 16, false);
        Assert.True(field.TryNormalize(raw, out float value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData(0x80u, 0f)]
    [InlineData(0u, 128f / 255f)]
    [InlineData(0x7Fu, 1f)]
    public void SignedFieldIsSignExtendedBeforeNormalization(uint raw, float expected)
    {
        var field = new TabletReportDescriptor.ValueField(2, 13, 1, 0x3D, -128, 127, 8, false);
        Assert.True(field.TryNormalize(raw, out float value));
        Assert.Equal(expected, value, 6);
    }

    [Fact]
    public void NullAndInvalidRangesDoNotBecomeContacts()
    {
        var nullable = new TabletReportDescriptor.ValueField(1, 1, 1, 0x30, 0, 100, 8, true);
        Assert.False(nullable.TryNormalize(101, out _));
        Assert.True(nullable.TryNormalize(100, out float edge));
        Assert.Equal(1, edge);
        Assert.False(new TabletReportDescriptor.ValueField(1, 1, 1, 0x30, 10, 10, 16, false).TryNormalize(10, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflinePickerUsesVerifiedTabletCapabilities(bool pressure)
    {
        var choices = MappingDisplayResolver.BuildInputChoices(Tablet(pressure)).Select(c => c.Descriptor).ToArray();
        Assert.Contains("Touchpad 0 Finger 0 X", choices);
        Assert.Contains("Touchpad 0 Finger 0 Down", choices);
        Assert.Equal(pressure, choices.Contains("Touchpad 0 Finger 0 Pressure"));
        Assert.DoesNotContain(choices, c => c.StartsWith("Touchpad 0 Click"));
        Assert.DoesNotContain(choices, c => c.Contains("Finger 1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilitiesSurviveSettingsRoundTrip(bool pressure)
    {
        var serializer = new XmlSerializer(typeof(UserDevice));
        using var output = new StringWriter();
        serializer.Serialize(output, Tablet(pressure));
        var restored = (UserDevice)serializer.Deserialize(new StringReader(output.ToString()));
        Assert.True(restored.IsTablet);
        Assert.Equal(pressure, restored.SupportsTouchpadPressure);
        Assert.False(restored.SupportsTouchpadClick);
        Assert.Equal(new[] { 1 }, restored.CapTouchpadFingerCounts);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RemoteCapabilitiesPreserveUnknownAndUnsupported(bool? pressure, bool? click)
    {
        var info = new RemotePeerDeviceInfo
        {
            PeerLocalDeviceId = "tablet-test", Name = "Tablet", InputDeviceType = InputDeviceType.Tablet,
            HasTouchpad = true, NumTouchpads = 1, TouchpadFingerCounts = new[] { 1 },
            TouchpadPressureSupported = pressure, TouchpadClickSupported = click
        };
        var round = Assert.Single(LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { info })));
        Assert.Equal(pressure, round.TouchpadPressureSupported);
        Assert.Equal(click, round.TouchpadClickSupported);
        Assert.Equal(new[] { 1 }, round.TouchpadFingerCounts);
    }

    [Fact]
    public void DefaultVirtualTouchpadHasOneContactAndNoInventedClick()
    {
        var setting = SettingsManager.CreateDefaultPadSetting(Tablet(true), VirtualControllerType.PlayStation);
        Assert.Equal("Touchpad 0 Finger 0 X", setting.TouchpadX1);
        Assert.Equal("Touchpad 0 Finger 0 Y", setting.TouchpadY1);
        Assert.Equal("Touchpad 0 Finger 0 Down", setting.TouchpadContact1);
        Assert.True(string.IsNullOrEmpty(setting.TouchpadContact2));
        Assert.True(string.IsNullOrEmpty(setting.TouchpadClick));
    }

    [Fact]
    public void TabletPressureDrivesTheExistingAnalogConsumer()
    {
        var state = new CustomInputState { Touchpads = new[] { new TouchpadInputState(1) } };
        state.Touchpads[0].FingerDown[0] = true;
        state.Touchpads[0].FingerPressure[0] = 128f / 255f;
        var source = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Pressure" };
        Assert.Equal(128f / 255f, SourceCoercion.EvaluateForTriggerTarget(state, source), 6);
        state.Touchpads[0].FingerDown[0] = false;
        Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(state, source));
    }

    [Fact]
    public void DecodedTabletContactDrivesRelativeMouseWithoutAnInitialJump()
    {
        using var f = new TabletReportStateTests.Fixture();
        using var device = new WindowsTabletDevice(f.Decoder, null);
        var source = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = device.InstanceGuid.ToString() };
        long ticks = 0;
        float Read()
        {
            Assert.True(f.Decode());
            SourceCoercion.BeginPollFrame();
            ticks += 40000;
            return SourceCoercion.ReadTouchpadMouseCounts(device.GetCurrentState(), source, 383420,
                device.InstanceGuid.ToString(), 0.004f, true, ticks, 10_000_000).X;
        }
        Assert.Equal(0, Read());
        f.Values.X = 30;
        Assert.True(Read() > 0);
        f.Values.Down.Clear();
        Read();
        f.Values.X = 90;
        f.Values.Down.UnionWith(new ushort[] { 0x42, 0x32 });
        Assert.Equal(0, Read());
    }

    [Theory]
    [InlineData(50u, 5u, "North")]
    [InlineData(50u, 95u, "South")]
    [InlineData(5u, 50u, "West")]
    [InlineData(95u, 50u, "East")]
    public void DecodedTabletZonesSelectOneDirection(uint x, uint y, string direction)
    {
        using var f = new TabletReportStateTests.Fixture();
        using var device = new WindowsTabletDevice(f.Decoder, null);
        f.Values.X = x; f.Values.Y = y;
        Assert.True(f.Decode());
        var state = device.GetCurrentState();
        foreach (string zone in new[] { "North", "South", "West", "East" })
            Assert.Equal(zone == direction, SourceCoercion.EvaluateForButtonTarget(state,
                new MappingSource { Descriptor = $"Touchpad 0 Finger 0 Down {zone}" }, 50));
    }

    [Fact]
    public void DecodedTabletContactReachesVirtualTouchpadAndReleasesOnLift()
    {
        using var f = new TabletReportStateTests.Fixture();
        using var device = new WindowsTabletDevice(f.Decoder, null);
        var setting = SettingsManager.CreateDefaultPadSetting(Tablet(true), VirtualControllerType.PlayStation);
        var map = typeof(InputManager).GetMethod("MapInputToTouchpad", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(map);
        TouchpadState Read(TouchpadState previous)
        {
            Assert.True(f.Decode());
            return (TouchpadState)map.Invoke(null, new object[] { device.GetCurrentState(), setting, previous, null, device.InstanceGuid.ToString(), 383421 });
        }
        var down = Read(default);
        Assert.True(down.Down0);
        Assert.False(down.Down1);
        Assert.False(down.Click);
        Assert.Equal(0.25f, down.X0, 4);
        Assert.Equal(0.75f, down.Y0, 4);
        f.Values.Down.Clear();
        Assert.False(Read(down).Down0);
    }

    [Fact]
    public void ChangedPressureCapabilityChangesThePersistedRegistrySignature()
    {
        var tablet = Tablet(false);
        string before = PadForge.Services.InputService.BuildDeviceRegistrySignature(new[] { tablet });
        tablet.CapTouchpadPressure = true;
        Assert.NotEqual(before, PadForge.Services.InputService.BuildDeviceRegistrySignature(new[] { tablet }));
    }
}
