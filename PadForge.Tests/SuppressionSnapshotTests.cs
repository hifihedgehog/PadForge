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
        public async Task ConcurrentReadDuringRebuild_NeverThrows()
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
            var finished = await Task.WhenAny(reader, Task.Delay(10_000));
            Assert.Same(reader, finished);   // a spin inside the set walk never finishes
            Assert.Empty(faults);
        }

        /// <summary>The reader-visible contract: whatever the preview captured
        /// stays valid across a clear.
        ///
        /// <para>This asserted only that an UNSUPPRESSED source reads
        /// unsuppressed, before and after. The fixture never populated the
        /// suppression set, so both assertions were true of an empty set and
        /// the test passed with ClearShiftRuntime turned into a complete no-op.
        /// A test asserting that something did NOT change needs a positive
        /// control proving the fixture can produce the changed state at
        /// all.</para></summary>
        [Fact]
        public void ClearingASlot_DoesNotMutateAPreviouslyPublishedSnapshot()
        {
            InputManager.ClearAllShiftRuntime();

            // Arm a real postpone activator so the LIVE published set is
            // populated. ResolveActiveLayerMask rebuilds and publishes it at
            // its tail, which is the only way in: the published arrays are
            // private and AddPostponeKey fills a caller-supplied set, not the
            // live one. PostponeMapping stays false, which is what opts an
            // activator INTO suppressing its own source row.
            var ms = new MappingSet();
            ms.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "Shift1",
                LayerName = "Shift1",
                Mode = "Hold",
                Descriptor = "Button 8",
                DeviceGuid = "dev-a",
            });

            var st = new PadForge.Engine.CustomInputState();
            st.Buttons[8] = true;                       // activator held
            InputManager.ResolveActiveLayerMask(0, ms, st, "dev-a");

            // POSITIVE CONTROL. The live reader must SEE the suppression, or
            // every "not suppressed" assertion below is vacuously true. This
            // is exactly what the test lacked: it asserted only that an
            // unsuppressed source read unsuppressed, which held of an empty
            // set and passed with ClearShiftRuntime made a total no-op.
            Assert.True(InputManager.IsSourceSuppressedPostpone(0, "dev-a", "Button 8"),
                "fixture failed to arm: the rest of this test would be vacuous");

            // The reader-visible contract: clearing the slot releases it.
            InputManager.ClearShiftRuntime(0);
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, "dev-a", "Button 8"));

            // And an unrelated slot was never affected either way.
            Assert.False(InputManager.IsSourceSuppressedPostpone(1, "dev-a", "Button 8"));
        }

        [Fact]
        public void OutOfRangeSlot_IsNotSuppressed()
        {
            Assert.False(InputManager.IsSourceSuppressedPostpone(-1, "dev", "Button 0"));
            Assert.False(InputManager.IsSourceSuppressedPostpone(9999, "dev", "Button 0"));
        }
    }
}
