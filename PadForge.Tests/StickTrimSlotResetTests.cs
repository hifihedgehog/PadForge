using System;
using System.Collections;
using System.Reflection;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A wholesale row replacement retires the slot's stick-trim levels.
    ///
    /// <para>The all-slots clear already dropped them. The per-slot reset,
    /// which is what a row replacement, a delete and a slot reorder call, did
    /// not, so a re-authored row could inherit the previous trim level
    /// whenever Reset on Release was off. The keys carry the slot, so only the
    /// named slot's entries retire and the neighbors keep theirs.</para>
    ///
    /// <para>In the SettingsManagerStatics collection: a profile switch
    /// there (RevertToDefaultProfile) clears every slot's trim levels, and
    /// run beside it this test lost its neighbor entry mid-test.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class StickTrimSlotResetTests
    {
        private static IDictionary TrimStates()
        {
            var f = typeof(InputManager).GetField("_stickTrimStates",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(f != null, "_stickTrimStates is gone");
            return (IDictionary)f.GetValue(null);
        }

        private static object NewState()
        {
            var t = typeof(InputManager).GetNestedType("StickTrimState", BindingFlags.NonPublic);
            Assert.True(t != null, "StickTrimState is gone");
            return Activator.CreateInstance(t, nonPublic: true);
        }

        private static object Key(int slot, string target, string layer)
        {
            var dict = TrimStates();
            var keyType = dict.GetType().GetGenericArguments()[0];
            return Activator.CreateInstance(keyType, slot, target, layer);
        }

        [Fact]
        public void APerSlotResetRetiresThatSlotsTrimAndLeavesOthers()
        {
            var dict = TrimStates();
            object mine = Key(3, "LeftThumbX", "Base");
            object neighbor = Key(4, "LeftThumbX", "Base");
            dict[mine] = NewState();
            dict[neighbor] = NewState();

            // Positive control: both are really present before the reset, so
            // "gone afterwards" cannot pass vacuously.
            Assert.True(dict.Contains(mine));
            Assert.True(dict.Contains(neighbor));

            InputManager.ResetSourceKindRuntimeForSlot(3);

            Assert.False(dict.Contains(mine), "the reset slot kept its trim level");
            Assert.True(dict.Contains(neighbor), "a neighboring slot lost its trim level");

            dict.Remove(neighbor);
        }
    }
}
