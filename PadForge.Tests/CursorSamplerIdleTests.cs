using System;
using System.IO;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Common.Mapping;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Mouse Position sampler ticks at 200 Hz while something reads it
    /// and idled at a 250 ms tick when nothing did, four pool work items a
    /// second for no reader. A read, a pin and a clamp each wake it, so an
    /// idle sampler waits for one of them.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class CursorSamplerIdleTests
    {
        [Fact]
        public void AnIdleSampler_TicksNoMoreUntilARead()
        {
            var cursor = SourceCoercion.MouseCursorProvider;
            var point = SourceCoercion.MouseCursorPointProvider;
            CursorControlService service = null;
            try
            {
                service = new CursorControlService();
                Assert.True(SpinWait.SpinUntil(() => service.SamplerIdle, 30000), "the sampler idled");
                int ticks = service.TickCount;
                Thread.Sleep(600);
                Assert.Equal(ticks, service.TickCount);

                SourceCoercion.MouseCursorProvider();
                Assert.True(SpinWait.SpinUntil(() => service.TickCount > ticks, 30000), "a read woke the sampler");
            }
            finally
            {
                service?.Dispose();
                SourceCoercion.MouseCursorProvider = cursor;
                SourceCoercion.MouseCursorPointProvider = point;
            }
        }

        /// <summary>A pin or clamp pressed while the tick that goes idle runs
        /// finds the sampler still awake and wakes nothing. The tick reads the
        /// demand again after it stops the timer, so the press is kept.</summary>
        [Fact]
        public void TheTickThatGoesIdleReadsTheDemandAgainAfterTheStop()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string source = File.ReadAllText(Path.Combine(d.FullName, "PadForge.App", "Services", "CursorControlService.cs")).Replace("\r\n", "\n");
            int tick = source.IndexOf("private void Tick()", StringComparison.Ordinal);
            Assert.True(tick > 0, "Tick moved");
            int stop = source.IndexOf("_timer?.Change(Timeout.Infinite, Timeout.Infinite);", tick, StringComparison.Ordinal);
            Assert.True(stop > tick);
            int ret = source.IndexOf("return;", stop, StringComparison.Ordinal);
            string after = source.Substring(stop, ret - stop);
            Assert.Contains("System.Threading.Interlocked.MemoryBarrier();", after);
            Assert.Contains("if (SamplerWanted())\n                    {\n                        _samplerIdle = false;\n                        try { _timer?.Change(0, SampleIntervalMs); }", after);
        }
    }
}
