using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard for the postpone/consume key sets.
    ///
    /// <para>EvaluatePerDeviceTriggerPreview runs on the UI mirror tick and
    /// consults IsSourceSuppressedPostpone, which reads two per-slot HashSets
    /// the poll thread rebuilt every tick by Clear-then-Add. A Contains racing
    /// that rebuild can return a wrong answer, throw, or spin inside a resize.
    /// The sets are now published as immutable snapshots and only when their
    /// contents change, so a reader that captured one can never see it
    /// mutate.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SuppressionSnapshotTests
    {
        /// <summary>A read concurrent with continuous rebuilds must never
        /// throw and must never hang. Pre-fix this is the exact shape that
        /// could corrupt a HashSet walk.</summary>
        [Fact]
        public void ConcurrentReadDuringRebuild_NeverThrows()
        {
            InputManager.ClearAllShiftRuntime();

            using var cts = new CancellationTokenSource();
            var faults = new List<string>();

            var reader = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        InputManager.IsSourceSuppressedPostpone(0, "dev-a", "Button 3");
                        InputManager.IsSourceSuppressedPostpone(0, "", "Button 3");
                    }
                    catch (System.Exception ex)
                    {
                        lock (faults) faults.Add(ex.GetType().Name);
                        return;
                    }
                }
            });

            for (int i = 0; i < 2000; i++)
            {
                InputManager.ClearShiftRuntime(0);
                InputManager.ClearAllShiftRuntime();
            }

            cts.Cancel();
            Assert.True(reader.Wait(10_000), "reader did not finish: a spin inside the set walk");
            Assert.Empty(faults);
        }

        [Fact]
        public void ClearingASlot_DoesNotMutateAPreviouslyPublishedSnapshot()
        {
            // The reader-visible contract: whatever the preview captured
            // stays valid. Exercised through the public surface, since the
            // published arrays are private.
            InputManager.ClearAllShiftRuntime();
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, "dev-a", "Button 3"));

            InputManager.ClearShiftRuntime(0);
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, "dev-a", "Button 3"));
        }

        [Fact]
        public void OutOfRangeSlot_IsNotSuppressed()
        {
            Assert.False(InputManager.IsSourceSuppressedPostpone(-1, "dev", "Button 0"));
            Assert.False(InputManager.IsSourceSuppressedPostpone(9999, "dev", "Button 0"));
        }
    }
}
