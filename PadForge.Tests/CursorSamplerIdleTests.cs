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
    }
}
