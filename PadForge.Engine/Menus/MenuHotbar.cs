using System;
using PadForge.Engine.Common.Mapping;

namespace PadForge.Engine.Menus
{
    /// <summary>The four inputs of a hotbar.</summary>
    public enum HotbarControl
    {
        Show,
        Previous,
        Next,
        Fire,
    }

    /// <summary>
    /// Steam Input's hotbar, as steamclient64.dll's CHotbarMode runs it
    /// (update 0x1388c1df0). The client builds the hotbar on its D-pad mode:
    /// the host's position becomes four directions, and the hotbar acts on
    /// them. Down shows or hides the bar, Right and Left step the selection,
    /// and Up fires the selected cell ("Pressing Down on the DPad / Joystick
    /// will toggle the menu, Left/Right will scroll through the options, and
    /// Up will activate the selected item", the configurator's own text).
    /// Here the four are <see cref="HotbarControl"/> inputs, any input a
    /// mapping row takes.
    /// </summary>
    public static class MenuHotbar
    {
        /// <summary>The client holds a fired cell at least this long, and
        /// under Always waits this long between two cells (0.0333 s at
        /// 0x1392eabe0).</summary>
        public const double HoldMs = 1000.0 / 30.0;

        /// <summary>The client's direction bits (0x1388b175b): north 1,
        /// south 2, east 4, west 8.</summary>
        private const int FireBit = 1, ShowBit = 2, NextBit = 4, PreviousBit = 8;

        /// <summary>Steam's hotbar dead zone, 20000 of 32767 (the client's
        /// hotbar sets it at 0x1388c266a), as a percent of full travel.</summary>
        public const int SteamDeadzonePercent = 61;

        /// <summary>Whether cell <paramref name="index"/> holds something to
        /// fire. The client skips a cell with no binding when it steps
        /// (0x1388c2068), and only such a cell lets a fired one go.</summary>
        public static bool IsCellBound(MenuDefinitionEntry def, int index)
        {
            if (def?.Items == null) return false;
            for (int i = 0; i < def.Items.Count; i++)
                if (def.Items[i] is { } it && it.Index == index) return true;
            return false;
        }

        /// <summary>Advances a hotbar one poll. <paramref name="layerActive"/>
        /// false is the client's mode switched off: the held cell lets go and
        /// the bar starts over hidden, with nothing selected, the state the
        /// client's mode activation sets (0x1388bfb90).</summary>
        public static void Step(MenuRuntimeState st, MenuDefinitionEntry def, bool layerActive,
            bool show, bool previous, bool next, bool fire, long nowMs)
        {
            if (st == null || def == null) return;
            int count = Math.Max(0, def.CellCount);
            if (!layerActive || count == 0)
            {
                st.Reset();
                return;
            }

            int sel = st.StepIndex;
            if (sel < 0 || sel >= count) sel = count / 2;
            int cur = sel;

            int mask = (fire ? FireBit : 0) | (show ? ShowBit : 0) | (next ? NextBit : 0)
                | (previous ? PreviousBit : 0);
            if (mask != st.PrevStepMask)
            {
                int rising = (st.PrevStepMask ^ mask) & mask;
                st.PrevStepMask = mask;
                if ((rising & ShowBit) != 0)
                {
                    bool wasShown = st.HotbarShown;
                    st.HotbarShown = !wasShown;
                    // Hiding the bar this way drops the selection, so it opens
                    // on the middle cell again whatever Re-Center says.
                    if (wasShown) { sel = -1; cur = -1; }
                }
                if (st.HotbarShown)
                {
                    // Both steps start from the selection the frame began
                    // with, and Previous decides when both rise together.
                    if ((rising & NextBit) != 0 && TryStep(def, count, cur, +1, out int n)) sel = n;
                    if ((rising & PreviousBit) != 0 && TryStep(def, count, cur, -1, out int p)) sel = p;
                }
            }

            bool shown = st.HotbarShown;
            bool fireHeld = fire && shown;
            switch (def.FireType)
            {
                case MenuFireType.Click:
                    if (st.HotbarHeld >= 0 && nowMs > st.HotbarReleaseAtMs && !fireHeld) Release(st);
                    if (sel >= 0 && IsCellBound(def, sel) && st.HotbarHeld == -1 && fireHeld) Press(st, sel, nowMs);
                    break;

                case MenuFireType.ClickRelease:
                    if (st.HotbarHeld == -1 && sel >= 0 && st.HotbarArmed && !fireHeld) Press(st, sel, nowMs);
                    if (nowMs > st.HotbarReleaseAtMs && st.HotbarHeld >= 0 && IsCellBound(def, st.HotbarHeld))
                        Release(st);
                    st.HotbarArmed = fireHeld;
                    break;

                case MenuFireType.TouchRelease:
                    if (st.HotbarHeld == -1 && sel >= 0 && st.HotbarArmed && !shown) Press(st, sel, nowMs);
                    if (nowMs > st.HotbarReleaseAtMs && st.HotbarHeld >= 0 && IsCellBound(def, st.HotbarHeld))
                        Release(st);
                    st.HotbarArmed = shown;
                    break;

                case MenuFireType.Always:
                    if (!shown) sel = -1;
                    if (st.HotbarHeld >= 0)
                    {
                        if (nowMs > st.HotbarReleaseAtMs && sel != st.HotbarHeld)
                        {
                            st.HotbarHeld = -1;
                            st.HotbarReleaseAtMs = nowMs + HoldMs;
                        }
                    }
                    else if (sel >= 0 && nowMs > st.HotbarReleaseAtMs)
                    {
                        Press(st, sel, nowMs);
                    }
                    break;
            }

            if (fireHeld && def.HotbarDismissOnFire) st.HotbarShown = false;
            if (!st.HotbarShown && def.HotbarRecenter) sel = -1;

            st.StepIndex = sel;
            st.AssertedIndex = st.HotbarHeld;
            st.HoveredIndex = st.HotbarShown ? sel : -1;
            st.Engaged = st.HotbarShown;
            st.Clicked = false;
            st.PulsedIndex = -1;
        }

        /// <summary>The client's step (0x1388c203d and 0x1388c20b2): from the
        /// selection, one cell at a time, at most a lap of the bar, to the
        /// next cell with a binding. At an end it stops, or with Wrap goes on
        /// from the other end.</summary>
        private static bool TryStep(MenuDefinitionEntry def, int count, int cur, int dir, out int found)
        {
            found = cur;
            int idx = cur;
            for (int left = count; left > 0; left--)
            {
                idx += dir;
                if (idx >= count)
                {
                    if (!def.HotbarWrap) return false;
                    idx = 0;
                }
                else if (idx < 0)
                {
                    if (!def.HotbarWrap) return false;
                    idx = count - 1;
                }
                if (idx != cur && IsCellBound(def, idx))
                {
                    found = idx;
                    return true;
                }
            }
            return false;
        }

        private static void Press(MenuRuntimeState st, int index, long nowMs)
        {
            st.HotbarHeld = index;
            st.HotbarReleaseAtMs = nowMs + HoldMs;
        }

        private static void Release(MenuRuntimeState st)
        {
            st.HotbarHeld = -1;
            st.HotbarReleaseAtMs = double.MaxValue;
        }

        /// <summary>The input a hotbar control reads when the menu names none:
        /// the host's own direction. The D-pad and the face buttons read their
        /// buttons by position. A stick or a touchpad reads the direction the
        /// way the client's D-pad mode does (0x1388b3390): four 90-degree
        /// spans with a fiftieth of a span's margin, engaged past the dead
        /// zone plus 1.25 percent and held down to it less 1.25 percent, a
        /// touchpad only while clicked (the D-pad mode's default on a
        /// trackpad). Empty for a host with no directions of its own, such as
        /// a Custom opener, whose controls are recorded.</summary>
        public static string DefaultControl(MenuDefinitionEntry def, HotbarControl control)
        {
            if (def == null) return "";
            string host = (def.HostDescriptor ?? "").Trim();
            if (host == "Gamepad DPad")
            {
                return control switch
                {
                    HotbarControl.Show => "Gamepad DPadDown",
                    HotbarControl.Previous => "Gamepad DPadLeft",
                    HotbarControl.Next => "Gamepad DPadRight",
                    _ => "Gamepad DPadUp",
                };
            }
            if (host == "Gamepad Diamond")
            {
                return control switch
                {
                    HotbarControl.Show => "Gamepad ButtonA",
                    HotbarControl.Previous => "Gamepad ButtonX",
                    HotbarControl.Next => "Gamepad ButtonB",
                    _ => "Gamepad ButtonY",
                };
            }

            string pair;
            var gate = BoundedGate.None;
            if (host == "Gamepad LeftStick") pair = SourceCoercion.LeftStickRingDescriptor;
            else if (host == "Gamepad RightStick") pair = SourceCoercion.RightStickRingDescriptor;
            else if (host.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                string half = def.HostHalf switch { 1 => " Left", 2 => " Right", _ => "" };
                pair = $"{host} Finger 0 Ring{half}";
                gate = BoundedGate.Click;
            }
            else return "";

            double dz = Math.Clamp(def.EngageDeadzonePercent, 1, 95);
            double press = Math.Min(dz + 1.25, 100.0);
            double release = Math.Max(dz - 1.25, 0.0);
            // East 0, north 90, west 180, south 270 degrees.
            double center = control switch
            {
                HotbarControl.Next => 0.0,
                HotbarControl.Fire => 90.0,
                HotbarControl.Previous => 180.0,
                _ => 270.0,
            };
            return BoundedReads.Wedge(center, 45.0, 90.0 / 50.0, press, release, gate, pair);
        }

        /// <summary>The input a control reads: the menu's own, else the
        /// host's.</summary>
        public static string ControlDescriptor(MenuDefinitionEntry def, HotbarControl control)
        {
            if (def == null) return "";
            string own = control switch
            {
                HotbarControl.Show => def.HotbarShowDescriptor,
                HotbarControl.Previous => def.HotbarPreviousDescriptor,
                HotbarControl.Next => def.HotbarNextDescriptor,
                _ => def.HotbarFireDescriptor,
            };
            own = (own ?? "").Trim();
            return own.Length > 0 ? own : DefaultControl(def, control);
        }
    }
}
