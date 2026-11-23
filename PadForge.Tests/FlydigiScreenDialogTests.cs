using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Interop;
using PadForge.Common.Input.Flydigi;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Flydigi screen dialog (#586) made whole, on the test host's
    /// stand-in application.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class FlydigiScreenDialogTests
    {
        /// <summary>The constructor picks the first source and fit, and each
        /// pick builds the preview, which stops the preview's timer. The
        /// timer has to exist before the first pick: made after it, the
        /// constructor threw and the dialog never opened.</summary>
        [Fact]
        public void TheDialogOpens() => DialogTestHost.Run(() =>
        {
            var ctor = typeof(FlydigiScreenDialog).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var binding = new FlydigiScreenSender.Apex4Binding();
            var dialog = (FlydigiScreenDialog)ctor.Invoke(new object[] { Guid.NewGuid(), PadForge.Engine.Flydigi.FlydigiApexModel.Apex4,
                binding, "Racing", 2, null });
            try
            {
                new WindowInteropHelper(dialog).EnsureHandle();
                Assert.NotNull(dialog.FindName("SourceBox"));
            }
            finally
            {
                dialog.Close();
            }
        });

        /// <summary>Closing during a send asks whether to stop it. The
        /// question is modal, so it pumps the dispatcher, and a send that
        /// ends while it is up runs its finally there and clears the send.
        /// Stop then has nothing to stop and the dialog closes: it threw a
        /// NullReferenceException out of Closing. A send still running is
        /// stopped, and the other answer keeps the dialog open.</summary>
        [Fact]
        public void ASendThatEndsWhileTheStopQuestionIsUp_LetsTheDialogClose() => DialogTestHost.Run(() =>
        {
            var ctor = typeof(FlydigiScreenDialog).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var dialog = (FlydigiScreenDialog)ctor.Invoke(new object[] { Guid.NewGuid(), PadForge.Engine.Flydigi.FlydigiApexModel.Apex4,
                new FlydigiScreenSender.Apex4Binding(), "Racing", 2, null });
            var sending = typeof(FlydigiScreenDialog).GetField("_sending", BindingFlags.NonPublic | BindingFlags.Instance);
            var closing = (Action<object, CancelEventArgs>)Delegate.CreateDelegate(typeof(Action<object, CancelEventArgs>), dialog,
                typeof(FlydigiScreenDialog).GetMethod("OnClosing", BindingFlags.NonPublic | BindingFlags.Instance,
                    new[] { typeof(object), typeof(CancelEventArgs) }));
            var ask = FlydigiScreenDialog.AskToStop;
            try
            {
                // Keep sending: the dialog stays and the send goes on.
                var running = new CancellationTokenSource();
                sending.SetValue(dialog, running);
                FlydigiScreenDialog.AskToStop = _ => false;
                var keep = new CancelEventArgs();
                closing(dialog, keep);
                Assert.True(keep.Cancel);
                Assert.False(running.IsCancellationRequested);

                // Stop while the send runs: the send is stopped.
                FlydigiScreenDialog.AskToStop = _ => true;
                var stop = new CancelEventArgs();
                closing(dialog, stop);
                Assert.False(stop.Cancel);
                Assert.True(running.IsCancellationRequested);

                // The send ends while the question is up, then Stop.
                sending.SetValue(dialog, new CancellationTokenSource());
                bool asked = false;
                FlydigiScreenDialog.AskToStop = _ =>
                {
                    asked = true;
                    sending.SetValue(dialog, null);
                    return true;
                };
                var late = new CancelEventArgs();
                closing(dialog, late);
                Assert.True(asked);
                Assert.False(late.Cancel);
            }
            finally
            {
                FlydigiScreenDialog.AskToStop = ask;
                sending.SetValue(dialog, null);
                dialog.Close();
            }
        });
    }
}
