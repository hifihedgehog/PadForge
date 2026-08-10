using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Locks the DS3 reconnect-suppression balance. Suppression became
    /// refcounted in audit round 41 (two overlapping flows could
    /// un-suppress each other under the old bool), and the first cut broke
    /// the unpair flow's documented contract: the Devices page acquires
    /// one claim before launching the background unpair, and
    /// UnpairAllDs3's single release covers it (ownership transfer). The
    /// broken shape acquired AGAIN inside UnpairAllDs3, leaking one claim
    /// per unpair, and the monitor never re-grabbed a pad until the app
    /// restarted (owner repro 2026-08-08: delete the controller and USB
    /// never reconnects).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class Ds3SuppressBalanceTests : System.IDisposable
    {
        public Ds3SuppressBalanceTests() => Ds3DirectService.ResetSuppressionForTest();
        public void Dispose() => Ds3DirectService.ResetSuppressionForTest();

        [Fact]
        public void OneAcquireOneRelease_Unsuppresses()
        {
            Ds3DirectService.SuppressAndRelease();
            Assert.True(Ds3DirectService.IsReconnectSuppressedForTest);
            Ds3DirectService.AllowReconnect();
            Assert.False(Ds3DirectService.IsReconnectSuppressedForTest);
        }

        /// <summary>The unpair flow's shape: the UI acquires, the
        /// background sweep releases the transferred claim. Exactly one
        /// release must fully un-suppress.</summary>
        [Fact]
        public void UnpairShape_UiAcquire_SweepRelease_Unsuppresses()
        {
            // DevicesPage, before Task.Run.
            Ds3DirectService.SuppressAndRelease();
            // UnpairAllDs3's finally (ownership transfer: no second acquire).
            Ds3DirectService.AllowReconnect();
            Assert.False(Ds3DirectService.IsReconnectSuppressedForTest);
        }

        /// <summary>The overlap the refcount exists for: pairing and
        /// unpair running together must not un-suppress each other. The
        /// first finish releases its own claim only; the monitor stays
        /// suppressed until the second finishes.</summary>
        [Fact]
        public void OverlappingFlows_FirstReleaseKeepsSecondSuppressed()
        {
            Ds3DirectService.SuppressAndRelease();   // ceremony
            Ds3DirectService.SuppressAndRelease();   // unpair
            Ds3DirectService.AllowReconnect();       // ceremony ends first
            Assert.True(Ds3DirectService.IsReconnectSuppressedForTest);
            Ds3DirectService.AllowReconnect();       // unpair ends
            Assert.False(Ds3DirectService.IsReconnectSuppressedForTest);
        }

        /// <summary>An unbalanced EXTRA release must clamp at zero rather
        /// than un-suppress a claim someone else still holds.</summary>
        [Fact]
        public void ExtraRelease_ClampsAtZero()
        {
            Ds3DirectService.AllowReconnect();       // stray release, no claim
            Assert.False(Ds3DirectService.IsReconnectSuppressedForTest);
            Ds3DirectService.SuppressAndRelease();
            Assert.True(Ds3DirectService.IsReconnectSuppressedForTest);
            Ds3DirectService.AllowReconnect();
            Assert.False(Ds3DirectService.IsReconnectSuppressedForTest);
        }
    }
}
