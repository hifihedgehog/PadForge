using System;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class HeadTrackingInputTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UdpAndFreeTrackOpenOnlyTheirOwnResources(bool udp, bool freeTrack)
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        occupied.ExclusiveAddressUse = true;
        occupied.Bind(new IPEndPoint(IPAddress.Any, 0));
        int occupiedPort = ((IPEndPoint)occupied.LocalEndPoint).Port;
        using var producer = new FreeTrackProducer();
        int readers = 0, firewallCalls = 0;
        using var device = new HeadTrackerDevice(udp, udp ? 0 : occupiedPort, freeTrack, 0, () => 10000,
            () => { readers++; return producer.Reader(); }, _ => firewallCalls++);
        Assert.True(device.Open());
        Assert.Equal(udp, device.UdpEnabled);
        Assert.Equal(freeTrack, device.FreeTrackEnabled);
        Assert.Equal(udp ? 1 : 0, firewallCalls);
        Assert.Equal(freeTrack ? 1 : 0, readers);
        Assert.False(device.UdpBindFailed);
        Assert.Equal(udp, device.BoundUdpPort > 0);

        producer.Write(1, 10);
        device.GetCurrentState();
        Assert.Equal(HeadTrackerSource.None, device.Source);
        producer.Write(2, 30);
        var state = device.GetCurrentState();
        Assert.Equal(freeTrack ? HeadTrackerSource.FreeTrack : HeadTrackerSource.None, device.Source);
        Assert.Equal(freeTrack ? HeadPose.ToAxis(30, HeadTrackingRuntime.RotationRangeDeg) : 32768,
            state.Axis[HeadPose.AxisYaw]);

        int bound = device.BoundUdpPort;
        if (udp)
        {
            using var sender = new UdpClient();
            byte[] packet = HeadPoseDecodeTests.Udp(0, 0, 0, -45, 0, 0);
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, bound));
            var until = DateTime.UtcNow.AddSeconds(2);
            while (device.Source != HeadTrackerSource.Udp && DateTime.UtcNow < until) await Task.Delay(5);
            Assert.Equal(HeadTrackerSource.Udp, device.Source);
            Assert.Equal(HeadPose.ToAxis(-45, HeadTrackingRuntime.RotationRangeDeg),
                device.GetCurrentState().Axis[HeadPose.AxisYaw]);
        }
        device.Dispose();
        Assert.Null(device.GetCurrentState());
        if (udp)
        {
            using var released = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            released.ExclusiveAddressUse = true;
            released.Bind(new IPEndPoint(IPAddress.Any, bound));
        }
    }

    [Fact]
    public void UdpBindFailureLeavesFreeTrackUsableAndVisibleInStatus()
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        occupied.ExclusiveAddressUse = true;
        occupied.Bind(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)occupied.LocalEndPoint).Port;
        using var producer = new FreeTrackProducer();
        using var device = new HeadTrackerDevice(true, port, true, 0, () => 10000, producer.Reader, _ => { });
        Assert.True(device.Open());
        Assert.True(device.UdpBindFailed);
        producer.Write(1, 0);
        device.GetCurrentState();
        producer.Write(2, 20);
        Assert.Equal(HeadPose.ToAxis(20, HeadTrackingRuntime.RotationRangeDeg), device.GetCurrentState().Axis[0]);
        Assert.Equal(HeadTrackerSource.FreeTrack, device.Source);
        string status = Status(device);
        Assert.Contains(Strings.Instance.HeadTracker_StatusFreeTrack, status);
        Assert.Contains(string.Format(Strings.Instance.HeadTracker_StatusPortInUse_Format, port), status);
    }

    [Fact]
    public void FreeTrackOnlyWaitingStatusDoesNotClaimAUdpListener()
    {
        Assert.Equal("FT_SharedMem", FreeTrackReader.HeapName);
        Assert.Equal("FT_Mutext", FreeTrackReader.MutexName);
        using var producer = new FreeTrackProducer();
        using var device = new HeadTrackerDevice(false, 4242, true, 0, () => 10000, producer.Reader, _ => throw new InvalidOperationException());
        Assert.True(device.Open());
        Assert.Equal(Strings.Instance.HeadTracker_StatusFreeTrackWaiting, Status(device));
        Assert.DoesNotContain("4242", Status(device));
    }

    [Fact]
    public void EitherRuntimeInputKeepsTheTrackerEnabledAndReconfiguresItsReader()
    {
        bool udp = HeadTrackingRuntime.Enabled, free = HeadTrackingRuntime.FreeTrackEnabled;
        try
        {
            HeadTrackingRuntime.Enabled = false;
            HeadTrackingRuntime.FreeTrackEnabled = false;
            Assert.False(HeadTrackingRuntime.AnyEnabled);
            int version = HeadTrackingRuntime.Version;
            HeadTrackingRuntime.FreeTrackEnabled = true;
            Assert.True(HeadTrackingRuntime.AnyEnabled);
            Assert.NotEqual(version, HeadTrackingRuntime.Version);
            using var onlyFree = HeadTrackerDevice.FromCurrentSettings();
            Assert.False(onlyFree.UdpEnabled);
            Assert.True(onlyFree.FreeTrackEnabled);
            version = HeadTrackingRuntime.Version;
            HeadTrackingRuntime.Enabled = true;
            Assert.NotEqual(version, HeadTrackingRuntime.Version);
            HeadTrackingRuntime.FreeTrackEnabled = false;
            Assert.True(HeadTrackingRuntime.AnyEnabled);
            using var onlyUdp = HeadTrackerDevice.FromCurrentSettings();
            Assert.True(onlyUdp.UdpEnabled);
            Assert.False(onlyUdp.FreeTrackEnabled);
        }
        finally
        {
            HeadTrackingRuntime.Enabled = udp;
            HeadTrackingRuntime.FreeTrackEnabled = free;
        }
    }

    private static string Status(HeadTrackerDevice device) => (string)typeof(InputService)
        .GetMethod("BuildHeadTrackerStatus", BindingFlags.Static | BindingFlags.NonPublic)
        .Invoke(null, new object[] { device });

    [Fact]
    public async Task FreeTrackFailureLeavesUdpUsableAndVisibleInStatus()
    {
        using var device = new HeadTrackerDevice(true, 0, true, 0, () => 10000,
            () => new FreeTrackReader("", ""), _ => { });
        Assert.True(device.Open());
        Assert.True(device.FreeTrackFailed);
        using var sender = new UdpClient();
        await sender.SendAsync(HeadPoseDecodeTests.Udp(0, 0, 0, 25, 0, 0),
            new IPEndPoint(IPAddress.Loopback, device.BoundUdpPort));
        var until = DateTime.UtcNow.AddSeconds(2);
        while (device.Source != HeadTrackerSource.Udp && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.Equal(HeadTrackerSource.Udp, device.Source);
        Assert.Contains(Strings.Instance.HeadTracker_StatusFreeTrackUnavailable, Status(device));
        Assert.Contains(string.Format(Strings.Instance.HeadTracker_StatusUdp_Format, device.UdpPeer), Status(device));
    }

    [Fact]
    public void PortChangesDoNotReopenFreeTrackWhileUdpIsDisabled()
    {
        bool udp = HeadTrackingRuntime.Enabled, free = HeadTrackingRuntime.FreeTrackEnabled;
        int port = HeadTrackingRuntime.UdpPort;
        try
        {
            HeadTrackingRuntime.Enabled = false;
            HeadTrackingRuntime.FreeTrackEnabled = true;
            int version = HeadTrackingRuntime.Version;
            int changedPort = port == 4242 ? 4243 : 4242;
            HeadTrackingRuntime.UdpPort = changedPort;
            Assert.Equal(version, HeadTrackingRuntime.Version);
            HeadTrackingRuntime.Enabled = true;
            Assert.NotEqual(version, HeadTrackingRuntime.Version);
            using var device = HeadTrackerDevice.FromCurrentSettings();
            Assert.Equal(changedPort, device.UdpPort);
        }
        finally
        {
            HeadTrackingRuntime.UdpPort = port;
            HeadTrackingRuntime.Enabled = udp;
            HeadTrackingRuntime.FreeTrackEnabled = free;
        }
    }

    private sealed class FreeTrackProducer : IDisposable
    {
        private readonly string _name = "PadForge.HeadTest." + Guid.NewGuid().ToString("N");
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private readonly Mutex _mutex;
        public FreeTrackProducer()
        {
            _mapping = MemoryMappedFile.CreateNew(_name, HeadPose.FreeTrackHeapBytes);
            _view = _mapping.CreateViewAccessor();
            _mutex = new Mutex(false, _name + ".Mutex");
        }
        public FreeTrackReader Reader() => new(_name, _name + ".Mutex");
        public void Write(uint id, float yaw)
        {
            Assert.True(_mutex.WaitOne(1000));
            try
            {
                byte[] bytes = HeadPoseDecodeTests.Heap(id, -yaw * MathF.PI / 180, 0, 0, 0, 0, 0);
                _view.WriteArray(0, bytes, 0, bytes.Length);
            }
            finally { _mutex.ReleaseMutex(); }
        }
        public void Dispose() { _view.Dispose(); _mapping.Dispose(); _mutex.Dispose(); }
    }
}
