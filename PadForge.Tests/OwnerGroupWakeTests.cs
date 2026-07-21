using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    // Locks the pure cores of the 2026-07-20 single-writer ownership
    // arc: owner election + share-group mask (UserEffectsDispatcher.
    // ResolveOwnersAndGroup), the Sony share-mask fold, and the
    // group-OR wake decision (InputManager.FoldShareMasks / GroupNeed).
    // The multi-slot failure these guard: a pad owned by an idle slot
    // must still wake and rumble when a sharing slot has need.
    public class OwnerGroupWakeTests
    {
        private static UserSetting Row(Guid g, int slot) =>
            new UserSetting { InstanceGuid = g, MapTo = slot };

        private static (Dictionary<Guid, int> owners, int mask) Resolve(
            IList<UserSetting> rows, List<Guid> guids, int padIndex,
            Func<int, bool> live)
        {
            var owners = new Dictionary<Guid, int>();
            int mask = 1 << padIndex;
            UserEffectsDispatcher.ResolveOwnersAndGroup(
                rows, guids, padIndex, live, owners, ref mask);
            return (owners, mask);
        }

        [Fact]
        public void LowestLiveSlotWinsOwnership()
        {
            var g = Guid.NewGuid();
            var rows = new List<UserSetting> { Row(g, 5), Row(g, 2), Row(g, 9) };
            var (owners, mask) = Resolve(rows, new List<Guid> { g }, 5, s => true);
            Assert.Equal(2, owners[g]);
            Assert.Equal((1 << 5) | (1 << 2) | (1 << 9), mask);
        }

        [Fact]
        public void DeadLowerSlotIsSkipped()
        {
            var g = Guid.NewGuid();
            var rows = new List<UserSetting> { Row(g, 2), Row(g, 5) };
            // Slot 2's dispatcher is not live: ownership stays with 5.
            var (owners, _) = Resolve(rows, new List<Guid> { g }, 5, s => s != 2);
            Assert.Equal(5, owners[g]);
        }

        [Fact]
        public void SoleSlotOwnsItself()
        {
            var g = Guid.NewGuid();
            var rows = new List<UserSetting> { Row(g, 7) };
            var (owners, mask) = Resolve(rows, new List<Guid> { g }, 7, s => true);
            Assert.Equal(7, owners[g]);
            Assert.Equal(1 << 7, mask);
        }

        [Fact]
        public void ForeignAndInvalidRowsAreIgnored()
        {
            var g = Guid.NewGuid();
            var other = Guid.NewGuid();
            var rows = new List<UserSetting>
            {
                Row(g, 4),
                Row(other, 0),           // different device: not in guids
                Row(g, -1),              // unassigned sentinel
                Row(g, 99),              // out of range
                Row(Guid.Empty, 1),      // empty guid
                null,
            };
            var (owners, mask) = Resolve(rows, new List<Guid> { g }, 4, s => true);
            Assert.Equal(4, owners[g]);
            Assert.Equal(1 << 4, mask);
            Assert.False(owners.ContainsKey(other));
        }

        [Fact]
        public void FoldUnionsMasksAcrossSharedSlots()
        {
            var g1 = Guid.NewGuid();
            var g2 = Guid.NewGuid();
            var cfg = new (bool audio, bool cfPoke, int shareMask)[16];
            var guidMasks = new Dictionary<Guid, int>
            {
                [g1] = (1 << 0) | (1 << 2),   // pad 1 on slots 0+2
                [g2] = (1 << 2) | (1 << 5),   // pad 2 on slots 2+5
            };
            InputManager.FoldShareMasks(guidMasks, cfg);
            Assert.Equal((1 << 0) | (1 << 2), cfg[0].shareMask);
            Assert.Equal((1 << 0) | (1 << 2) | (1 << 5), cfg[2].shareMask);
            Assert.Equal((1 << 2) | (1 << 5), cfg[5].shareMask);
            Assert.Equal(0, cfg[1].shareMask);
        }

        [Fact]
        public void GroupNeedOrsAcrossShareGroup()
        {
            // Slot 0 owns the pad, slot 2 shares it and has game need:
            // the owner must be woken.
            int share = (1 << 0) | (1 << 2);
            var (game, audio) = InputManager.GroupNeed(
                needGameMask: 1 << 2, needAudioMask: 0, shareMask: share, padIndex: 0);
            Assert.True(game);
            Assert.False(audio);

            // Audio config on the sharing slot keeps the owner's timer too.
            (game, audio) = InputManager.GroupNeed(0, 1 << 2, share, 0);
            Assert.False(game);
            Assert.True(audio);
        }

        [Fact]
        public void GroupNeedFallsBackToOwnBitWithoutSonyGroup()
        {
            // shareMask 0 (no Sony assignment / pre-refresh): own bit only.
            var (game, _) = InputManager.GroupNeed(1 << 3, 0, 0, 3);
            Assert.True(game);
            (game, _) = InputManager.GroupNeed(1 << 4, 0, 0, 3);
            Assert.False(game);
        }
    }
}
