using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PadForge.Engine;
using PadForge.Engine.Tablets;
using Xunit;

namespace PadForge.Tests;

public class TabletReportStateTests
{
    internal sealed class Values : ITabletReportValues
    {
        internal uint X = 25, Y = 75, Pressure = 128;
        internal readonly HashSet<ushort> Down = new() { 0x42, 0x32 };
        internal ushort FailUsage;
        internal bool FailButtons;
        public uint ReadValue(TabletReportDescriptor.ValueField field, IntPtr report, int length, out uint value)
        {
            value = field.Page == 13 ? Pressure : field.Usage == 0x30 ? X : Y;
            return field.Usage == FailUsage ? 0xC0110004u : TabletNative.Success;
        }
        public uint ReadButtons(TabletReportDescriptor.ButtonField field, IntPtr report, int length, ushort[] values, ref uint count)
        {
            count = 0;
            if (FailButtons) return 0xC0110004;
            foreach (ushort value in Down) values[count++] = value;
            return TabletNative.Success;
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly Values Values = new();
        internal readonly TabletReportDescriptor Decoder;
        internal readonly IntPtr Report = Marshal.AllocHGlobal(2);
        internal Fixture(bool pressure = true, byte buttonReport = 1, byte valueReport = 1)
        {
            Decoder = new TabletReportDescriptor("test", "HID\\test", "test", "", 1, 2, IntPtr.Zero, 2,
                new(valueReport, 1, 1, 0x30, 0, 100, 8, true), new(valueReport, 1, 1, 0x31, 0, 100, 8, true),
                pressure ? new(valueReport, 13, 1, 0x30, 0, 255, 8, false) : null,
                new(buttonReport, 13, 1, 0x42),
                new[] { new TabletReportDescriptor.ButtonField(buttonReport, 13, 1, 0x45), new(buttonReport, 13, 1, 0x32) }, 8, Values);
        }
        internal bool Decode(byte id = 1)
        {
            Marshal.WriteByte(Report, id);
            return Decoder.Decode(Report, 2);
        }
        internal TouchpadInputState Snapshot()
        {
            var state = new CustomInputState();
            Decoder.CopyInto(state);
            return state.Touchpads[0];
        }
        public void Dispose() { Decoder.Dispose(); Marshal.FreeHGlobal(Report); }
    }

    [Fact]
    public void ContactKeepsIdentityUntilLiftAndPressureStopsOnLift()
    {
        using var f = new Fixture();
        Assert.True(f.Decode());
        var first = f.Snapshot();
        Assert.True(first.FingerDown[0]);
        Assert.Equal(128f / 255f, first.FingerPressure[0], 6);
        f.Values.X = 50;
        Assert.True(f.Decode());
        Assert.Equal(first.FingerContactId[0], f.Snapshot().FingerContactId[0]);
        f.Values.Down.Remove(0x42);
        Assert.True(f.Decode());
        Assert.False(f.Snapshot().FingerDown[0]);
        Assert.Equal(0, f.Snapshot().FingerPressure[0]);
        f.Values.Down.Add(0x42);
        Assert.True(f.Decode());
        Assert.NotEqual(first.FingerContactId[0], f.Snapshot().FingerContactId[0]);
        Assert.False(f.Snapshot().Clicked);
    }

    [Fact]
    public void EraserContactCarriesPressureWithoutTheTipSwitch()
    {
        using var f = new Fixture();
        f.Values.Down.Remove(0x42);
        f.Values.Down.Add(0x45);
        Assert.True(f.Decode());
        Assert.True(f.Snapshot().FingerDown[0]);
        Assert.Equal(128f / 255f, f.Snapshot().FingerPressure[0], 6);
    }

    [Fact]
    public void AParserErrorDoesNotPartiallyPublishAnotherAxis()
    {
        using var f = new Fixture();
        Assert.True(f.Decode());
        var before = f.Snapshot();
        f.Values.X = 90;
        f.Values.FailUsage = 0x31;
        Assert.False(f.Decode());
        var after = f.Snapshot();
        Assert.Equal(before.FingerX[0], after.FingerX[0]);
        Assert.Equal(before.FingerY[0], after.FingerY[0]);
        Assert.Equal(before.FingerContactId[0], after.FingerContactId[0]);
    }

    [Fact]
    public void NullCoordinatesKeepTheLastPositionAndReleaseContact()
    {
        using var f = new Fixture();
        Assert.True(f.Decode());
        f.Values.X = 101;
        Assert.True(f.Decode());
        Assert.Equal(0.25f, f.Snapshot().FingerX[0]);
        Assert.False(f.Snapshot().FingerDown[0]);
        Assert.Equal(0, f.Snapshot().FingerPressure[0]);
    }

    [Fact]
    public void UnrelatedAndMalformedReportsLeaveTheSnapshotAlone()
    {
        using var f = new Fixture();
        Assert.True(f.Decode());
        int contact = f.Snapshot().FingerContactId[0];
        Assert.False(f.Decode(9));
        Assert.False(f.Decoder.Decode(f.Report, 1));
        f.Values.FailButtons = true;
        Assert.False(f.Decode());
        Assert.Equal(contact, f.Snapshot().FingerContactId[0]);
        Assert.True(f.Snapshot().FingerDown[0]);
    }

    [Fact]
    public void ASeparateSwitchReportCanReleaseTheContact()
    {
        using var f = new Fixture(buttonReport: 2);
        Assert.True(f.Decode(1));
        Assert.False(f.Snapshot().FingerDown[0]);
        Assert.True(f.Decode(2));
        Assert.True(f.Snapshot().FingerDown[0]);
        Assert.Equal(128f / 255f, f.Snapshot().FingerPressure[0], 6);
        f.Values.Down.Clear();
        Assert.True(f.Decode(2));
        Assert.False(f.Snapshot().FingerDown[0]);
        Assert.Equal(0.25f, f.Snapshot().FingerX[0]);
    }

    [Fact]
    public void MissingPressureDoesNotInventAValue()
    {
        using var f = new Fixture(pressure: false);
        Assert.True(f.Decode());
        Assert.True(f.Snapshot().FingerDown[0]);
        Assert.Equal(0, f.Snapshot().FingerPressure[0]);
        Assert.False(f.Decoder.HasPressure);
    }

    [Fact]
    public void DisposedDecoderRejectsLateReports()
    {
        using var f = new Fixture();
        Assert.True(f.Decode());
        f.Decoder.Dispose();
        Assert.False(f.Decode());
        Assert.False(f.Snapshot().FingerDown[0]);
    }

    [Fact]
    public void UnsignedMaximumIsBoundedByTheFieldWidth()
    {
        var field = new TabletReportDescriptor.ValueField(1, 1, 1, 0x30, 0, -1, 16, false);
        Assert.True(field.TryNormalize(65535, out float value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void ReportZeroCarriesTheContactWhenTheDescriptorHasNoNumberedReports()
    {
        using var f = new Fixture(buttonReport: 0, valueReport: 0);
        Assert.True(f.Decode(0));
        Assert.True(f.Snapshot().FingerDown[0]);
        Assert.Equal(0.25f, f.Snapshot().FingerX[0]);
        Assert.Equal(128f / 255f, f.Snapshot().FingerPressure[0], 6);
    }
}
