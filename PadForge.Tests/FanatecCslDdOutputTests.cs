using PadForge.Common.Input;
using static PadForge.Common.Input.SonyHeadsetHid;

namespace PadForge.Tests;

public class FanatecCslDdOutputTests
{
    private static FanatecHidEndpoint.Candidate Collection(string path, uint parent, byte id,
        ushort length = 8, ushort usage = 4, ushort vendor = 0x0EB7, ushort pid = 0x0020)
        => new(path, parent, vendor, pid, 1, usage, length,
            new[] { new HIDP_VALUE_CAPS { ReportID = id, UsagePage = 0xFF00, UsageMin = 2, BitSize = 8, ReportCount = 7 } },
            new[] { id });

    [Fact]
    public void SecondaryInputRoutesToPrimaryCommandCollectionOfSameWheel()
    {
        var collections = new[] { Collection("col02", 10, 2), Collection("other-wheel-col01", 20, 1), Collection("col01", 10, 1) };
        var output = FanatecHidEndpoint.Select("COL02", collections);
        Assert.Equal("col01", output.Path);
        Assert.Equal(1, output.ReportId);
        Assert.Equal(8, output.ReportLength);
    }

    [Fact]
    public void NeverRoutesToAnotherPhysicalWheelWhenOwnCommandChannelIsUnavailable()
    {
        var source = Collection("col02", 10, 2) with { OutputLength = 0 };
        Assert.Null(FanatecHidEndpoint.Select("col02", new[] { source, Collection("other", 20, 1) }));
    }

    [Fact]
    public void CountedBufferCannotBeSelectedAsSevenByteCommandChannel()
    {
        var c = Collection("col03", 10, 255, 64, 0x3A);
        Assert.Null(FanatecHidEndpoint.Select(c.Path, new[] { c }));
    }

    [Fact]
    public void SingleValidatedSecondaryEndpointUsesItsOwnId()
    {
        var c = Collection("col02", 10, 2);
        Assert.Equal(2, FanatecHidEndpoint.Select(c.Path, new[] { c }).ReportId);
    }

    [Fact]
    public void UnvalidatedReportIdAndAmbiguousPrimaryEndpointsAreRefused()
    {
        var c = Collection("source", 10, 1) with { ValidOutputIds = new byte[] { 2 } };
        Assert.Null(FanatecHidEndpoint.Select(c.Path, new[] { c }));
        c = Collection("source", 10, 1);
        Assert.Null(FanatecHidEndpoint.Select(c.Path, new[] { c, Collection("duplicate", 10, 1) }));
    }

    [Theory]
    [InlineData(0x054C, 0x0020)]
    [InlineData(0x0EB7, 0x183B)]
    public void OtherVendorsAndPedalsAreNotRerouted(ushort vid, ushort pid)
    {
        var c = Collection("source", 10, 1, vendor: vid, pid: pid);
        Assert.Null(FanatecHidEndpoint.Select(c.Path, new[] { c }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EncoderPreservesSevenCommandBytesAndPrependsResolvedId(byte id)
    {
        byte[] command = { 0x01, 0x08, 0x12, 0x80, 0, 0, 1 };
        var report = FanatecRawHidWriter.BuildWheelReport(command, id, 8);
        Assert.Equal(id, report[0]);
        Assert.Equal(command, report[1..]);
        Assert.Throws<ArgumentOutOfRangeException>(() => FanatecRawHidWriter.BuildWheelReport(command, id, 64));
    }

    [Theory]
    [InlineData(true, 8, true)]
    [InlineData(true, 7, false)]
    [InlineData(false, 8, false)]
    [InlineData(false, 0, false)]
    public void OnlyCompleteSuccessfulTransfersAreDelivered(bool completed, uint count, bool expected)
        => Assert.Equal(expected, RawHidOutput.IsCompleteWrite(completed, count, 8));

    [Fact]
    public void DescriptorStructLayoutsMatchWindowsAbi()
    {
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf<HIDP_CAPS>());
        Assert.Equal(72, System.Runtime.InteropServices.Marshal.SizeOf<HIDP_VALUE_CAPS>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopAttemptsAllFiveSlotsAndPropagatesAnyFailure(bool failSpring)
    {
        var attempted = new List<uint>();
        bool ok = FanatecRawHidWriter.DisableAllSlots(effect =>
        {
            attempted.Add(effect);
            return !(failSpring && effect == 8);
        });
        Assert.Equal(new uint[] { 0, 8, 9, 10, 11 }, attempted);
        Assert.Equal(!failSpring, ok);
    }

    [Fact]
    public void NativeMetadataProbe_WhenCslDdIsPresent_ResolvesEveryCollectionToPrimaryOutput()
    {
        // This production discovery routine opens access=0 handles only.
        // It is safe to run without the app, SDL or the input engine.
        var candidates = FanatecHidEndpoint.Enumerate();
        if (candidates.Count == 0) return; // Hardware-independent CI remains usable.
        foreach (var candidate in candidates)
        {
            var selected = FanatecHidEndpoint.Select(candidate.Path, candidates);
            Assert.NotNull(selected);
            Assert.Equal(candidate.Parent, selected.Parent);
            Assert.Equal(8, selected.ReportLength);
            Assert.Equal(1, selected.ReportId);
        }
        string evidencePath = Environment.GetEnvironmentVariable("PADFORGE_METADATA_TEST_OUTPUT");
        if (!string.IsNullOrEmpty(evidencePath))
            System.IO.File.WriteAllText(evidencePath, System.Text.Json.JsonSerializer.Serialize(new
            {
                Mode = "MetadataOnly_NoWrites", CollectionCount = candidates.Count,
                Routes = candidates.Select(c => new { Source = c.Path, c.Parent, c.OutputLength,
                    OutputReportIds = c.ValidOutputIds.Select(id => $"0x{id:X2}"),
                    Output = FanatecHidEndpoint.Select(c.Path, candidates) })
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

public class WheelFfbWriteCacheTests
{
    [Fact]
    public void FailedIdenticalForceRetriesAfterBackoffAndIsNotApplied()
    {
        var cache = new WheelFfbWriteCache<int>();
        Assert.Equal(WheelWriteDecision.Write, cache.Decide("wheel", 10, 1000));
        cache.Record("wheel", 10, false, 1000);
        Assert.False(cache.TryGetApplied("wheel", out _));
        Assert.Equal(WheelWriteDecision.RetryBackoff, cache.Decide("wheel", 10, 1249));
        Assert.Equal(WheelWriteDecision.Write, cache.Decide("wheel", 10, 1250));
        cache.Record("wheel", 10, true, 1250);
        Assert.True(cache.TryGetApplied("wheel", out int applied));
        Assert.Equal(10, applied);
        Assert.Equal(WheelWriteDecision.Unchanged, cache.Decide("wheel", 10, 1251));
    }

    [Fact]
    public void FailedChangedForceInvalidatesPriorSuccess()
    {
        var cache = new WheelFfbWriteCache<int>();
        cache.Record("wheel", 10, true, 0);
        cache.Record("wheel", 20, false, 1);
        Assert.Equal(WheelWriteDecision.Write, cache.Decide("wheel", 10, 251));
    }

    [Fact]
    public void NewStopBypassesBackoffOnceButRepeatedFailureIsThrottled()
    {
        var cache = new WheelFfbWriteCache<int>();
        cache.Record("wheel", 10, false, 1000);
        Assert.Equal(WheelWriteDecision.Write, cache.Decide("wheel", 0, 1001, urgentStop: true));
        cache.Record("wheel", 0, false, 1001, isStop: true);
        Assert.Equal(WheelWriteDecision.RetryBackoff, cache.Decide("wheel", 0, 1002, urgentStop: true));
        // Even a changed endpoint generation must not create a tight retry loop.
        Assert.Equal(WheelWriteDecision.RetryBackoff, cache.Decide("wheel", -1, 1002, urgentStop: true));
    }

    [Fact]
    public void ChangingActiveForceDoesNotBypassUnavailableDeviceBackoff()
    {
        var cache = new WheelFfbWriteCache<int>();
        cache.Record("wheel", 10, false, 0);
        Assert.Equal(WheelWriteDecision.RetryBackoff, cache.Decide("wheel", 11, 1));
    }

    [Fact]
    public void DisconnectAndSettingsReinitializationRequireRedelivery()
    {
        var cache = new WheelFfbWriteCache<int>();
        cache.Record("wheel", 10, true, 0);
        Assert.True(cache.TryRemove("WHEEL", out _));
        Assert.Equal(WheelWriteDecision.Write, cache.Decide("wheel", 10, 1));
    }

    [Fact]
    public void FailedUnassignmentStopKeepsStateUntilRetry()
    {
        var cache = new WheelFfbWriteCache<int>();
        cache.Record("wheel", 10, true, 1000);
        cache.Defer("wheel", 1001);
        Assert.True(cache.ContainsKey("wheel"));
        Assert.False(cache.CanRetry("wheel", 1250));
        Assert.True(cache.CanRetry("wheel", 1251));
        Assert.False(cache.TryGetApplied("wheel", out _));
    }
}
