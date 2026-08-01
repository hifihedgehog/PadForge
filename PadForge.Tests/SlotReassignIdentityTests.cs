using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Re-assigning a device to a slot must re-arm that slot's identity.
    ///
    /// <para>Reported on hardware 2026-08-01: unassign a DualSense from a
    /// slot, re-assign it, and it never picks up the slot's identity
    /// colour. Only restarting PadForge brought it back.</para>
    ///
    /// <para>Two latches survived the unassign. The per-slot external-write
    /// record is static, keyed by pad index, and deliberately never reset so
    /// a game's lightbar write survives virtual-controller recreates on the
    /// slot (#191). That makes <c>LightbarEverExternal</c> true for the life
    /// of the process once anything writes the bar, and the identity floor is
    /// <c>playerNumber &gt; 0 &amp;&amp; !LightbarEverExternal</c>, so it could
    /// never re-arm. The ownership seed set kept the device's GUID too, since
    /// an unassigned device is skipped by the GUID filter rather than removed,
    /// so the re-add returned false and silently skipped the seed block.</para>
    ///
    /// <para>Surviving a VC recreate is correct. Surviving the device leaving
    /// the slot is not: the next assignment is a fresh identity claim.</para>
    /// </summary>
    public class SlotReassignIdentityTests
    {
        private static HashSet<Guid> Set(params Guid[] g) => new HashSet<Guid>(g);

        private static readonly Guid A = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid B = new Guid("bbbbbbbb-0000-0000-0000-000000000002");

        // ── The change detector that drives the re-arm ──

        [Fact]
        public void Unassign_IsSeenAsAChange()
        {
            Assert.True(UserEffectsDispatcher.AssignmentSetChanged(
                new List<Guid>(), Set(A)));
        }

        [Fact]
        public void Reassign_IsSeenAsAChange()
        {
            Assert.True(UserEffectsDispatcher.AssignmentSetChanged(
                new List<Guid> { A }, Set()));
        }

        [Fact]
        public void SwappingOneDeviceForAnother_IsSeenAsAChange()
        {
            // Same count, different member. A count-only check would miss
            // this and leave the previous device's identity latched.
            Assert.True(UserEffectsDispatcher.AssignmentSetChanged(
                new List<Guid> { B }, Set(A)));
        }

        [Fact]
        public void SteadyState_IsNotAChange()
        {
            // This runs on every dispatch, so a false positive here would
            // wipe a game's lightbar claim continuously and re-break #191.
            Assert.False(UserEffectsDispatcher.AssignmentSetChanged(
                new List<Guid> { A }, Set(A)));
            Assert.False(UserEffectsDispatcher.AssignmentSetChanged(
                new List<Guid> { A, B }, Set(A, B)));
            Assert.False(UserEffectsDispatcher.AssignmentSetChanged(
                new List<Guid>(), Set()));
        }

        [Fact]
        public void OrderDoesNotMatter()
        {
            Assert.False(UserEffectsDispatcher.AssignmentSetChanged(
                new List<Guid> { B, A }, Set(A, B)));
        }

        [Fact]
        public void NullsAreTreatedAsNoChange()
        {
            Assert.False(UserEffectsDispatcher.AssignmentSetChanged(null, Set(A)));
            Assert.False(UserEffectsDispatcher.AssignmentSetChanged(new List<Guid> { A }, null));
        }

        // ── What the re-arm buys, at the synthesizer ──

        [Fact]
        public void FloorArmed_AuthorsTheSlotIdentityColour()
        {
            // The state a re-assigned slot must land in.
            var fields = Ds5EffectSynthesizer.BuildFields(
                new DeviceSlotConfig(),
                overrides: new UserEffectsDispatcher.ExternalSubsystemOverrides
                {
                    LightbarEverExternal = false,
                },
                playerNumber: 1);

            Assert.Equal(new byte[] { 0x00, 0x00, 0x40 }, (byte[])fields["lightbar"]);
            Assert.Equal(0x04, (byte)fields["validFlag1"] & 0x04);   // lightbar enable asserted
        }

        [Fact]
        public void FloorDisarmed_CarriesTheOldColourAndNeverReclaims()
        {
            // The stuck state the bug left behind: the latch is set, so the
            // floor stands down and the pad keeps whatever it was last told,
            // with the enable bit clear. Correct while a game owns the bar,
            // wrong after the device has left and returned.
            var fields = Ds5EffectSynthesizer.BuildFields(
                new DeviceSlotConfig(),
                overrides: new UserEffectsDispatcher.ExternalSubsystemOverrides
                {
                    LightbarEverExternal = true,
                    LastLightbarRgb = new byte[] { 0x00, 0xFF, 0x00 },
                },
                playerNumber: 1);

            Assert.Equal(new byte[] { 0x00, 0xFF, 0x00 }, (byte[])fields["lightbar"]);
            Assert.Equal(0, (byte)fields["validFlag1"] & 0x04);      // enable stays clear
        }

        [Fact]
        public void TheTwoStatesDiffer_SoClearingTheLatchIsWhatFixesIt()
        {
            // Ties the unit under test to the symptom: same config, same
            // player number, and the ONLY difference is the latch.
            var cfg = new DeviceSlotConfig();
            var stuck = Ds5EffectSynthesizer.BuildFields(cfg,
                overrides: new UserEffectsDispatcher.ExternalSubsystemOverrides
                {
                    LightbarEverExternal = true,
                    LastLightbarRgb = new byte[] { 0x00, 0xFF, 0x00 },
                },
                playerNumber: 1);
            var rearmed = Ds5EffectSynthesizer.BuildFields(cfg,
                overrides: new UserEffectsDispatcher.ExternalSubsystemOverrides
                {
                    LightbarEverExternal = false,
                },
                playerNumber: 1);

            Assert.NotEqual((byte[])stuck["lightbar"], (byte[])rearmed["lightbar"]);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x40 }, (byte[])rearmed["lightbar"]);
        }
    }
}
