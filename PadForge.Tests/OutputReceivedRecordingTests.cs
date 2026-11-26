using System;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A problem recording takes every report a game sends a virtual
    /// controller (#597), the Sony vendor test feature 0x80 that a DualSense
    /// tester page sends among them. The handler forwards that feature to the
    /// physical DualSense and returns, so the recording's tap has to come
    /// before the forward. The handler is subscribed on the SDK controller,
    /// which the test host never makes, so its order is read from the
    /// source.
    /// </summary>
    public class OutputReceivedRecordingTests
    {
        [Fact]
        public void TheRecordingTakesTheVendorTestFeatureBeforeItIsForwarded()
        {
            string src = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs").Replace("\r", "");
            int handler = src.IndexOf("_controller.OutputReceived += (ctrl, pkt) =>", StringComparison.Ordinal);
            Assert.True(handler >= 0, "the output handler moved");
            int forward = src.IndexOf("_ds5Dispatcher.EnqueueFeature(pkt.ReportId, pkt.Data.Span);", handler, StringComparison.Ordinal);
            int tap = src.IndexOf("Recording.RecordingTaps.GameReport(idx, pkt.Source, pkt.ReportId, pkt.Data.Span);", handler, StringComparison.Ordinal);
            int index = src.IndexOf("int idx = FeedbackPadIndex;", handler, StringComparison.Ordinal);
            Assert.True(forward > handler, "the vendor test forward is gone");
            Assert.True(tap > handler, "the recording's tap is gone");
            Assert.True(index > handler && index < tap, "the tap reads the pad index before it is taken");
            Assert.True(tap < forward, "the vendor test feature is forwarded before the recording takes it");
        }
    }
}
