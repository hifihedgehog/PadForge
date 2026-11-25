using System;
using System.Runtime.InteropServices;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The pump's copy of a compared collection's reports (#600 item 32):
    /// nothing allocated per report, a copy bounded by the slot and by what
    /// Windows handed over, never by the report's own header, and a watch
    /// that lapses when the recording stops renewing it.
    /// </summary>
    [Collection("RawReportTap")]
    public class RawReportTapTests : IDisposable
    {
        private static readonly IntPtr Pad = new(0x5150);

        public void Dispose() => RawReportTap.Clear();

        /// <summary>A RAWHID record as WM_INPUT hands it over: the size and
        /// count, then the reports, each led by its report ID.</summary>
        private static IntPtr Record(int sizeHid, int count, int bytes, params byte[][] reports)
        {
            IntPtr p = Marshal.AllocHGlobal(bytes);
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, sizeHid);
            Marshal.WriteInt32(p, 4, count);
            int at = 8;
            foreach (var r in reports)
            {
                Marshal.Copy(r, 0, IntPtr.Add(p, at), Math.Min(r.Length, bytes - at));
                at += sizeHid;
            }
            return p;
        }

        private static byte[] Report(byte id, int length, byte fill)
        {
            var r = new byte[length];
            Array.Fill(r, fill);
            r[0] = id;
            return r;
        }

        [Fact]
        public void AReport_IsCopiedIntoItsReportIdsSlot_AndNothingIsAllocatedForIt()
        {
            RawReportTap.Watch(new[] { (Pad, new byte[] { 1 }, 64) });
            IntPtr record = Record(64, 1, 8 + 64, Report(1, 64, 0x5A));
            IntPtr other = Record(64, 1, 8 + 64, Report(2, 64, 0x5B));
            try
            {
                RawReportTap.Take(Pad, record, 8 + 64);
                RawReportTap.Take(Pad, other, 8 + 64);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10_000; i++)
                {
                    RawReportTap.Take(Pad, record, 8 + 64);
                    RawReportTap.Take(Pad, other, 8 + 64);
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(0, allocated);

                var into = new byte[64];
                Assert.True(RawReportTap.TryCopy(Pad, 1, into, out int length, out long ticks));
                Assert.Equal(64, length);
                Assert.True(ticks > 0);
                Assert.Equal(1, into[0]);
                Assert.Equal(0x5A, into[63]);
            }
            finally
            {
                Marshal.FreeHGlobal(record);
                Marshal.FreeHGlobal(other);
            }
        }

        [Fact]
        public void ACopy_IsBoundedByTheSlot_AndByWhatWindowsHandedOver_NeverByTheHeader()
        {
            RawReportTap.Watch(new[] { (Pad, new byte[] { 1 }, 16) });
            // The header says 200 bytes a report: the slot keeps its 16.
            IntPtr big = Record(200, 1, 8 + 200, Report(1, 200, 0x11));
            // The header says three reports, Windows handed over one.
            IntPtr many = Record(16, 3, 8 + 16 + 4, Report(1, 16, 0x22));
            // The header says a report longer than all that came.
            IntPtr lying = Record(10_000, 1, 8 + 16, Report(1, 16, 0x33));
            try
            {
                var into = new byte[300];
                RawReportTap.Take(Pad, big, 8 + 200);
                Assert.True(RawReportTap.TryCopy(Pad, 1, into, out int length, out _));
                Assert.Equal(16, length);

                RawReportTap.Take(Pad, many, 8 + 16 + 4);
                Assert.True(RawReportTap.TryCopy(Pad, 1, into, out length, out _));
                Assert.Equal(16, length);
                Assert.Equal(0x22, into[15]);

                RawReportTap.Take(Pad, lying, 8 + 16);
                Assert.True(RawReportTap.TryCopy(Pad, 1, into, out _, out _));
                Assert.Equal(0x22, into[15]);

                // Fewer than eight bytes carry no header at all.
                RawReportTap.Take(Pad, lying, 7);
                Assert.Equal(0x22, into[15]);
            }
            finally
            {
                Marshal.FreeHGlobal(big);
                Marshal.FreeHGlobal(many);
                Marshal.FreeHGlobal(lying);
            }
        }

        [Fact]
        public void AReportOfAnotherId_IsCountedAsOther_AndAnUnwatchedHandleIsLetBe()
        {
            RawReportTap.Watch(new[] { (Pad, new byte[] { 1 }, 16) });
            IntPtr other = Record(16, 1, 8 + 16, Report(0x11, 16, 0x44));
            try
            {
                Assert.Equal(0, RawReportTap.OtherReportTicks(Pad));
                RawReportTap.Take(Pad, other, 8 + 16);
                RawReportTap.Take(Pad, other, 8 + 16);
                Assert.True(RawReportTap.OtherReportTicks(Pad) > 0);
                Assert.Equal(2, RawReportTap.OtherReportCount(Pad));
                Assert.False(RawReportTap.TryCopy(Pad, 1, new byte[16], out _, out _));

                RawReportTap.Take(new IntPtr(0x7777), other, 8 + 16);
                Assert.Equal(0, RawReportTap.OtherReportTicks(new IntPtr(0x7777)));
                Assert.Equal(0, RawReportTap.OtherReportCount(new IntPtr(0x7777)));
            }
            finally { Marshal.FreeHGlobal(other); }
        }

        [Fact]
        public void AWatchNotRenewed_Lapses_AndAWatchKeepsItsReportsWhenSetAgain()
        {
            RawReportTap.Watch(new[] { (Pad, new byte[] { 1 }, 16) });
            IntPtr record = Record(16, 1, 8 + 16, Report(1, 16, 0x66));
            try
            {
                RawReportTap.Take(Pad, record, 8 + 16);
                RawReportTap.Watch(new[] { (Pad, new byte[] { 1, 2 }, 16) });
                Assert.True(RawReportTap.TryCopy(Pad, 1, new byte[16], out _, out _));

                RawReportTap.LapseForTest();
                RawReportTap.Take(Pad, record, 8 + 16);
                Assert.False(RawReportTap.Watching);
            }
            finally { Marshal.FreeHGlobal(record); }
        }
    }

    /// <summary>
    /// The tap is one static table, and a recording in any other test class
    /// sets or ends its watch at every look. xunit runs a
    /// DisableParallelization collection after the parallel ones have ended,
    /// so these tests watch beside no recording.
    /// </summary>
    [CollectionDefinition("RawReportTap", DisableParallelization = true)]
    public sealed class RawReportTapCollection { }
}
