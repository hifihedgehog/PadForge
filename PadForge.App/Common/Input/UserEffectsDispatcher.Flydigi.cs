using System;
using System.Collections.Generic;
using PadForge.Common.Input.Flydigi;
using PadForge.Engine.Data;
using PadForge.Engine.Flydigi;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The dispatcher's lane for Flydigi Apex pads (#586): each pass it
    /// resolves the DualSense effect block each trigger should show, in
    /// the DualSense lane's own order, and hands it to the pad's session,
    /// which plays it as the pad can.
    ///
    /// <para>The order, first one present wins: a game's effect on the
    /// slot's virtual DualSense, a game mod's hold (#529), a macro's
    /// effect (#531), the Impulse Triggers levels as a vibration, the
    /// steering lock's pulse and resistance (#94) on a trigger the tab
    /// leaves off, and the Adaptive Triggers group. The first three come
    /// latched from <see cref="TryGetInboundTriggerEffects(int, Span{byte}, Span{byte}, out bool, out bool)"/>:
    /// a trigger effect is set once and holds until it is replaced, on
    /// a DualSense and on these pads alike.</para>
    ///
    /// <para>A game's off stands for 100 ms before PadForge's own layers
    /// show through. Games set and clear an effect many times a second,
    /// and a release on each clear makes the trigger flutter
    /// (ApexSenseBridge AdaptiveTriggerBridge.cpp apply, and the same hold
    /// in Apex6HapticBridge.cpp, kTriggerClearHold).</para>
    /// </summary>
    internal sealed partial class UserEffectsDispatcher
    {
        internal const long FlydigiOffHoldMs = 100;

        private sealed class FlydigiSides
        {
            public readonly bool[] External = new bool[2];
            public readonly long[] OffSince = new long[2];
        }

        private readonly Dictionary<Guid, FlydigiSides> _flydigiSides = new();

        /// <summary>A Flydigi pad on this slot shows a game's, a mod's or
        /// a macro's effect, so the writer keeps running: the effect ends
        /// at a time no event announces, the fifteen seconds after which a
        /// silent game is taken for gone.</summary>
        private volatile bool _flydigiHoldsExternal;

        /// <summary>One bit per slot whose last pass saw a Flydigi pad with
        /// a session, so a game's trigger write wakes that slot's
        /// dispatcher and no other's.</summary>
        private static int s_flydigiSlots;

        internal static bool SlotHasFlydigi(int padIndex)
            => (uint)padIndex < 32 && (System.Threading.Volatile.Read(ref s_flydigiSlots) & (1 << padIndex)) != 0;

        private void NoteFlydigiSlot(bool any, bool held)
        {
            _flydigiHoldsExternal = held;
            if ((uint)_padIndex >= 32) return;
            int bit = 1 << _padIndex;
            int seen, next;
            do
            {
                seen = System.Threading.Volatile.Read(ref s_flydigiSlots);
                next = any ? seen | bit : seen & ~bit;
            }
            while (seen != next && System.Threading.Interlocked.CompareExchange(ref s_flydigiSlots, next, seen) != seen);
        }

        /// <summary>Hands the pad's session both triggers' blocks for this
        /// pass. True when either shows an effect that is not PadForge's
        /// own.</summary>
        private bool DriveFlydigiTriggers(UserDevice ud, FlydigiApexSession session, DeviceSlotConfig devCfg,
            Guid testTarget, long nowMs)
        {
            Span<byte> left = stackalloc byte[11];
            Span<byte> right = stackalloc byte[11];
            TryGetInboundTriggerEffects(_padIndex, left, right, out bool haveLeft, out bool haveRight);

            // The Impulse Triggers levels, with the DualSense lane's linger:
            // a game drives the motor high and low between two passes, and
            // the vibration holds its mode for 100 ms after the last level
            // above zero.
            byte impulseRight = 0, impulseLeft = 0;
            bool targeted = testTarget == Guid.Empty || ud.InstanceGuid == testTarget;
            if (targeted)
                (impulseRight, impulseLeft) = SlotImpulseTriggerForDeviceProvider?.Invoke(_padIndex, ud.InstanceGuid)
                    ?? ((byte)0, (byte)0);
            if (impulseRight > 0) _impulseLastNonZeroTickRight[ud.InstanceGuid] = nowMs;
            if (impulseLeft > 0) _impulseLastNonZeroTickLeft[ud.InstanceGuid] = nowMs;
            bool rightLinger = targeted && _impulseLastNonZeroTickRight.TryGetValue(ud.InstanceGuid, out long lastRight)
                && nowMs - lastRight < ImpulseAtLingerMs;
            bool leftLinger = targeted && _impulseLastNonZeroTickLeft.TryGetValue(ud.InstanceGuid, out long lastLeft)
                && nowMs - lastLeft < ImpulseAtLingerMs;
            float steerVib = targeted ? SteeringTriggerVibProvider?.Invoke(_padIndex) ?? 0f : 0f;
            float steerRes = targeted ? SteeringAtResistanceProvider?.Invoke(_padIndex) ?? 0f : 0f;

            if (!_flydigiSides.TryGetValue(ud.InstanceGuid, out var sides))
                _flydigiSides[ud.InstanceGuid] = sides = new FlydigiSides();

            bool held = DriveFlydigiSide(session, sides, 1, FlydigiTriggerSide.Right, right, haveRight,
                impulseRight, rightLinger, steerVib, steerRes, devCfg, nowMs);
            held |= DriveFlydigiSide(session, sides, 0, FlydigiTriggerSide.Left, left, haveLeft,
                impulseLeft, leftLinger, steerVib, steerRes, devCfg, nowMs);
            return held;
        }

        private static bool DriveFlydigiSide(FlydigiApexSession session, FlydigiSides sides, int index,
            FlydigiTriggerSide side, ReadOnlySpan<byte> inbound, bool have, byte impulse, bool linger,
            float steerVib, float steerRes, DeviceSlotConfig devCfg, long nowMs)
        {
            if (FlydigiTakesInbound(sides.External, sides.OffSince, index, inbound, have, nowMs, out bool held))
            {
                session.SetTriggerBlock(side, inbound, ours: false);
                return true;
            }
            if (held) return true;

            Span<byte> own = stackalloc byte[11];
            bool right = side == FlydigiTriggerSide.Right;
            var mode = right ? devCfg.RightTriggerMode : devCfg.LeftTriggerMode;
            if (linger)
                Ds5EffectSynthesizer.BuildAtVibrationOverrideBlock(impulse).CopyTo(own);
            else if (mode == AdaptiveTriggerMode.Off && steerVib > 0f)
                Ds5EffectSynthesizer.BuildAtVibrationOverrideBlock((byte)Math.Clamp((int)(steerVib * 255f), 0, 255)).CopyTo(own);
            else if (mode == AdaptiveTriggerMode.Off && steerRes > 0f)
                Ds5EffectSynthesizer.BuildAtResistanceOverrideBlock((byte)Math.Clamp((int)(steerRes * 255f), 0, 255)).CopyTo(own);
            else
                Ds5EffectSynthesizer.EncodeUserTrigger(devCfg, right, own);
            session.SetTriggerBlock(side, own, ours: true);
            return false;
        }

        /// <summary>Whether a trigger shows the inbound block this pass.
        /// True for a block that names an effect. For an inbound off, or
        /// none, false, with <paramref name="held"/> true while the off
        /// has stood for less than <see cref="FlydigiOffHoldMs"/> after an
        /// effect: the trigger then keeps what it has. Pure over its
        /// arguments.</summary>
        internal static bool FlydigiTakesInbound(bool[] external, long[] offSince, int index,
            ReadOnlySpan<byte> inbound, bool have, long nowMs, out bool held)
        {
            held = false;
            if (have && inbound.Length >= 11 && inbound[0] != 0x00 && inbound[0] != 0x05)
            {
                external[index] = true;
                offSince[index] = 0;
                return true;
            }
            if (!external[index]) return false;
            if (offSince[index] == 0) offSince[index] = nowMs;
            if (nowMs - offSince[index] < FlydigiOffHoldMs)
            {
                held = true;
                return false;
            }
            external[index] = false;
            offSince[index] = 0;
            return false;
        }
    }
}
