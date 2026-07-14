using System;

namespace PadForge.Engine.Menus
{
    /// <summary>Per-menu runtime state, owned by the poll thread. One
    /// instance per (slot, device, menu id).</summary>
    public sealed class MenuRuntimeState
    {
        /// <summary>Surface engaged last frame (touch / deflection past
        /// the deadzone, AND the hosting layer active).</summary>
        public bool Engaged;

        /// <summary>Host click held last frame (pad click / stick click).</summary>
        public bool Clicked;

        /// <summary>Item index currently hovered, -1 = none. Radial: 0 =
        /// center, 1..N ring; grid: 0-based cell.</summary>
        public int HoveredIndex = -1;

        /// <summary>Item asserted by the hold-shaped fire types (Click /
        /// Always), -1 = none.</summary>
        public int AssertedIndex = -1;

        /// <summary>Item fired by the one-shot fire types (ClickRelease /
        /// TouchRelease), valid until <see cref="PulseUntilMs"/>.</summary>
        public int PulsedIndex = -1;

        public long PulseUntilMs;

        public void Reset()
        {
            Engaged = false;
            Clicked = false;
            HoveredIndex = -1;
            AssertedIndex = -1;
            PulsedIndex = -1;
            PulseUntilMs = 0;
        }
    }

    /// <summary>
    /// The menu commit state machine. Fire-type semantics grounded on
    /// Valve's shipped configurator strings (Touch Menu Activation Style /
    /// Radial Menu Button Type): Click = "activates the menu item when the
    /// button is clicked" (hold-shaped), Release = "when the button is
    /// released" (one-shot), Touch Release = "when the trackpad is no
    /// longer touched or when the mode shift button is released" with "any
    /// position outside of the deadzone is considered touched" on sticks
    /// (one-shot on disengage), Always = "continuously while the trackpad
    /// or joystick is being touched" (hold-shaped). Commit-on-release with
    /// a center dismiss is also sc-controller's proven shape
    /// (scc/osd/menu.py fires the selection on confirm release and cancels
    /// when the stick returns to center).
    /// </summary>
    public static class MenuEvaluator
    {
        /// <summary>How long a one-shot commit stays visible to readers.
        /// Matches the gesture engine's fired-set latch (GestureRecognizer:
        /// fires latch across the 100 ms cooldown window "so downstream
        /// readers ... see a stable fire long enough to pick up the rising
        /// edge at any reasonable polling rate").</summary>
        public const int CommitPulseMs = 100;

        /// <summary>Advances one menu's state for this poll frame.
        /// <paramref name="surfaceActive"/> = physically engaged AND the
        /// hosting layer active; a layer ending therefore lands here as a
        /// release edge, which is exactly Steam's mode-shift-end commit.
        /// (<paramref name="dx"/>, <paramref name="dy"/>) is the
        /// center-relative deflection (-1..1, +Y down) used by radial
        /// menus; (<paramref name="nx"/>, <paramref name="ny"/>) is the
        /// absolute normalized position (0..1, top-left) used by grids.</summary>
        public static void Update(MenuRuntimeState st, MenuDefinitionEntry def,
            bool surfaceActive, bool clicked, double dx, double dy, double nx, double ny,
            long nowMs)
        {
            if (st == null || def == null) return;

            int hover = -1;
            if (surfaceActive)
            {
                if (def.Kind == MenuKind.Radial)
                {
                    hover = MenuSelectionMath.RadialIndexFromVector(dx, dy, def.CellCount,
                        def.HasCenter, Math.Clamp(def.EngageDeadzonePercent, 1, 95) / 100.0);
                }
                else
                {
                    hover = MenuSelectionMath.GridIndexFromPosition(nx, ny, def.CellCount);
                }
            }

            switch (def.FireType)
            {
                case MenuFireType.Click:
                    st.AssertedIndex = surfaceActive && clicked && hover >= 0 ? hover : -1;
                    break;

                case MenuFireType.ClickRelease:
                    // Click released while an item is hovered: one-shot.
                    if (st.Engaged && st.Clicked && !clicked && surfaceActive && st.HoveredIndex >= 0)
                        Pulse(st, st.HoveredIndex, nowMs);
                    st.AssertedIndex = -1;
                    break;

                case MenuFireType.TouchRelease:
                    // Disengage (lift / deadzone / layer end) commits the
                    // last hovered item; disengaging with nothing hovered
                    // (dead center, no center item) dismisses silently.
                    if (st.Engaged && !surfaceActive && st.HoveredIndex >= 0)
                        Pulse(st, st.HoveredIndex, nowMs);
                    st.AssertedIndex = -1;
                    break;

                case MenuFireType.Always:
                    st.AssertedIndex = hover >= 0 ? hover : -1;
                    break;
            }

            // Expire a finished pulse so IsItemFired readers and the
            // overlay stop seeing it.
            if (st.PulsedIndex >= 0 && nowMs >= st.PulseUntilMs)
                st.PulsedIndex = -1;

            st.HoveredIndex = hover;
            st.Engaged = surfaceActive;
            st.Clicked = surfaceActive && clicked;
        }

        /// <summary>True while item <paramref name="index"/> is fired:
        /// asserted by a hold-shaped fire type, or within a one-shot
        /// commit's pulse window.</summary>
        public static bool IsItemFired(MenuRuntimeState st, int index, long nowMs)
            => st != null && index >= 0
            && (st.AssertedIndex == index
                || (st.PulsedIndex == index && nowMs < st.PulseUntilMs));

        private static void Pulse(MenuRuntimeState st, int index, long nowMs)
        {
            st.PulsedIndex = index;
            st.PulseUntilMs = nowMs + CommitPulseMs;
        }
    }
}
