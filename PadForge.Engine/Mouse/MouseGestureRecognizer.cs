using System;
using PadForge.Engine.Common.Mapping;

namespace PadForge.Engine.Mouse
{
    /// <summary>
    /// The mouse-gesture recognizer (issue #200), Logi Options+ semantics
    /// generalized to every mouse button: while a selected gesture button is
    /// held, its own session accumulates raw displacement; at that button's
    /// release the net movement classifies. Dominant axis at or past the
    /// flick threshold fires exactly one of Left / Right / Up / Down for
    /// THAT button; below the threshold the release fires that button's
    /// Click. Each button is an independent session with its own cooldown,
    /// so different gesture buttons carry different mapping combos.
    /// Session index 5 is the Custom activation (discussion #216): a
    /// recorded cross-device input (key / gamepad button / axis past the
    /// button threshold) arms it through <see cref="ComposePressedMask"/>,
    /// then classifies identically to a mouse button's session.
    ///
    /// <para>Fired keys latch in
    /// <see cref="MouseGestureContext.FiredGesturesThisFrame"/> as
    /// "{buttonIndex} {Gesture}" ("3 Left") for the cooldown window,
    /// mirroring the touchpad lane's indexed pulse contract. Descriptors are
    /// "Mouse Gesture {buttonIndex} {Gesture}".</para>
    ///
    /// <para>Pure function of its inputs, polling-thread only, no allocation
    /// on the hot path (fired keys come from the precomposed
    /// <see cref="Keys"/> table). Deltas arrive as raw counts recovered from
    /// the already-published centered axes; the recognizer must NEVER
    /// consume RawInput deltas itself (the wrapper's consume-and-zero read
    /// owns that source).</para>
    /// </summary>
    public static class MouseGestureRecognizer
    {
        public const string GestureLeft = "Left";
        public const string GestureRight = "Right";
        public const string GestureUp = "Up";
        public const string GestureDown = "Down";
        public const string GestureClick = "Click";

        /// <summary>The mapping-descriptor prefix for the family.</summary>
        public const string DescriptorPrefix = "Mouse Gesture ";

        /// <summary>Gesture names in stable order: Left, Right, Up, Down,
        /// Click. Index into <see cref="Keys"/>' inner arrays.</summary>
        public static readonly string[] GestureNames =
            { GestureLeft, GestureRight, GestureUp, GestureDown, GestureClick };

        /// <summary>Precomposed fired-set keys, [buttonIndex][gestureIndex]
        /// = "{buttonIndex} {Gesture}". Interned once so the 1 kHz paths
        /// never compose strings.</summary>
        public static readonly string[][] Keys = BuildKeys();

        private static string[][] BuildKeys()
        {
            var keys = new string[MouseGestureContext.ButtonCount][];
            for (int b = 0; b < MouseGestureContext.ButtonCount; b++)
            {
                keys[b] = new string[GestureNames.Length];
                for (int g = 0; g < GestureNames.Length; g++)
                    keys[b][g] = b + " " + GestureNames[g];
            }
            return keys;
        }

        /// <summary>Composes the pressed mask <see cref="Update"/> consumes
        /// for a tick (discussion #216): the raw mouse buttons clamped to
        /// the five physical indices, plus the Custom bit when the
        /// settings' recorded cross-device input is held. The held read is
        /// <see cref="SourceCoercion.ButtonHeldProvider"/>, the same reader
        /// the Aim Engage / trigger-route / haptic-mirror engage settles
        /// use, so buttons hold while pressed and axis descriptors hold
        /// past the button threshold. The provider's empty-descriptor
        /// pass-through convention (unconfigured = true) is deliberately
        /// bypassed: an unconfigured Custom button must stay inert, so the
        /// provider is only consulted when the Custom bit is selected AND a
        /// descriptor is recorded. Poll thread only, zero extra work when
        /// Custom is unselected or the card is disabled (Update ignores
        /// the mask in that state, so the read would be dead work).</summary>
        public static int ComposePressedMask(
            int mouseButtonsMask, MouseGestureSettings settings, int slotIndex)
        {
            int mask = mouseButtonsMask & ((1 << MouseGestureContext.MouseButtonCount) - 1);
            if (settings != null
                && settings.Enabled
                && (settings.GestureButtons & (1 << MouseGestureContext.CustomButtonIndex)) != 0
                && !string.IsNullOrEmpty(settings.CustomEngageButton)
                && (SourceCoercion.ButtonHeldProvider?.Invoke(
                        settings.CustomEngageDeviceGuid ?? "",
                        settings.CustomEngageButton, slotIndex) ?? false))
            {
                mask |= 1 << MouseGestureContext.CustomButtonIndex;
            }
            return mask;
        }

        public static void Update(
            MouseGestureContext ctx,
            MouseGestureSettings settings,
            int pressedButtonsMask,
            double dxCounts,
            double dyCounts,
            long nowMs)
        {
            if (ctx == null) return;
            if (settings == null || !settings.Enabled)
            {
                if (ctx.FiredGesturesThisFrame.Count > 0 || AnyDown(ctx))
                    ctx.Reset();
                return;
            }

            int gestureMask = settings.GestureButtons;
            int threshold = Math.Max(1, settings.FlickThresholdCounts);

            for (int b = 0; b < MouseGestureContext.ButtonCount; b++)
            {
                bool selected = (gestureMask & (1 << b)) != 0;
                if (!selected)
                {
                    // Deselected buttons drop any stale session so a later
                    // re-select starts clean.
                    if (ctx.ButtonWasDown[b] || ctx.CooldownUntilTimestampMs[b] != 0)
                        ClearButton(ctx, b);
                    continue;
                }

                // This button's cooldown expiry ends its fired pulse.
                if (ctx.CooldownUntilTimestampMs[b] != 0 && nowMs >= ctx.CooldownUntilTimestampMs[b])
                {
                    RemoveButtonKeys(ctx, b);
                    ctx.CooldownUntilTimestampMs[b] = 0;
                }

                bool down = (pressedButtonsMask & (1 << b)) != 0;

                if (down && !ctx.ButtonWasDown[b])
                {
                    // Fresh session for this button: discard its leftover
                    // latched fires so the prior gesture cannot bleed in.
                    ctx.AccumDx[b] = 0;
                    ctx.AccumDy[b] = 0;
                    RemoveButtonKeys(ctx, b);
                    ctx.CooldownUntilTimestampMs[b] = 0;
                }

                if (down)
                {
                    ctx.AccumDx[b] += dxCounts;
                    ctx.AccumDy[b] += dyCounts;
                }
                else if (ctx.ButtonWasDown[b])
                {
                    // Release: classify this button's net displacement.
                    // Screen convention: dx > 0 right, dy > 0 down.
                    double ax = Math.Abs(ctx.AccumDx[b]);
                    double ay = Math.Abs(ctx.AccumDy[b]);

                    int g;
                    if (ax < threshold && ay < threshold)
                        g = 4; // Click
                    else if (ax >= ay)
                        g = ctx.AccumDx[b] < 0 ? 0 : 1; // Left : Right
                    else
                        g = ctx.AccumDy[b] < 0 ? 2 : 3; // Up : Down

                    ctx.FiredGesturesThisFrame.Add(Keys[b][g]);
                    ctx.CooldownUntilTimestampMs[b] = nowMs + Math.Max(0, settings.CooldownMs);
                    ctx.AccumDx[b] = 0;
                    ctx.AccumDy[b] = 0;
                }

                ctx.ButtonWasDown[b] = down;
            }
        }

        private static bool AnyDown(MouseGestureContext ctx)
        {
            for (int b = 0; b < MouseGestureContext.ButtonCount; b++)
                if (ctx.ButtonWasDown[b]) return true;
            return false;
        }

        private static void ClearButton(MouseGestureContext ctx, int b)
        {
            ctx.ButtonWasDown[b] = false;
            ctx.AccumDx[b] = 0;
            ctx.AccumDy[b] = 0;
            ctx.CooldownUntilTimestampMs[b] = 0;
            RemoveButtonKeys(ctx, b);
        }

        private static void RemoveButtonKeys(MouseGestureContext ctx, int b)
        {
            if (ctx.FiredGesturesThisFrame.Count == 0) return;
            var keys = Keys[b];
            for (int g = 0; g < keys.Length; g++)
                ctx.FiredGesturesThisFrame.Remove(keys[g]);
        }
    }
}
