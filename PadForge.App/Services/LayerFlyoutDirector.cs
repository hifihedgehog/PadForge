using System;
using PadForge.Engine.Data;

namespace PadForge.Services
{
    /// <summary>
    /// Decides what the Shift Layer Flyout does on each tick of the UI timer
    /// (#581). It reads the layer every slot is on and each layer's Flyout
    /// choice (<see cref="LayerFlyout"/>), and answers with one step: show a
    /// layer, keep the flyout up, hide it, or nothing. It holds no window, so
    /// its rules run in a test with no screen.
    ///
    /// <para>The rules, in order:</para>
    /// <list type="number">
    /// <item>A layer set to stay owns the flyout while it is on: the viewed
    /// slot's first, else the lowest slot's. While one is on, nothing else
    /// shows.</item>
    /// <item>Otherwise a slot that changes to a layer set to show briefly
    /// shows that layer once, and the window's own timer takes it away.</item>
    /// <item>Otherwise a slot that goes back to Base shows Base once, unless
    /// Base is set to show nothing or the layer that ended was.</item>
    /// <item>The flyout never shows a layer its slot has left: a layer that
    /// ends with nothing to follow it takes the flyout down at once.</item>
    /// <item>When several slots change in one tick, the slot the flyout was
    /// showing comes first, then the viewed slot, then the lowest.</item>
    /// </list>
    ///
    /// <para>With every layer on its default this is what the flyout did
    /// before the choice existed: a layer's flyout stayed while the layer was
    /// on, and Base showed for two seconds once every slot was back on
    /// it.</para>
    ///
    /// <para>The choices are read on every tick, so one changed in the Shift
    /// Layer window applies to a layer that is on.</para>
    /// </summary>
    internal sealed class LayerFlyoutDirector
    {
        internal const string Base = "Base";

        internal enum Step
        {
            /// <summary>Leave the flyout as it is.</summary>
            None,
            /// <summary>Show <see cref="Decision.Mask"/> of
            /// <see cref="Decision.Slot"/>. Base's mask asks for the slot's
            /// Base flyout.</summary>
            Show,
            /// <summary>Hold the flyout up for the layer it shows. The layer
            /// is named, so the caller can show it again if the flyout went
            /// down by itself.</summary>
            KeepAlive,
            /// <summary>Take the flyout down now.</summary>
            Hide,
        }

        internal readonly struct Decision
        {
            public Decision(Step step, int slot, string mask)
            {
                Step = step;
                Slot = slot;
                Mask = mask;
            }

            public Step Step { get; }
            public int Slot { get; }
            public string Mask { get; }
        }

        // The layer each slot was on at the last tick.
        private string[] _masks;

        // The layer the flyout was last asked to show, and whether that
        // layer is one that stays. The window may have let a brief one go
        // since: only a layer that stays is held up.
        private int _shownSlot = -1;
        private string _shownMask = Base;
        private bool _shownStays;

        /// <summary>Forgets everything, as when the flyout is turned off or
        /// there are no mapping sets. True when a flyout asked for here may
        /// still be up, so the caller takes it down.</summary>
        internal bool Reset()
        {
            bool shown = _shownSlot >= 0;
            _masks = null;
            Forget();
            return shown;
        }

        private void Forget()
        {
            _shownSlot = -1;
            _shownMask = Base;
            _shownStays = false;
        }

        /// <summary>One tick. <paramref name="layerOf"/> gives the layer a
        /// slot is on (<c>InputManager.GetEngagedLayerMask</c> in the app),
        /// and <paramref name="viewedSlot"/> is the controller page that is
        /// open, or -1.</summary>
        internal Decision Tick(MappingSet[] sets, Func<int, MappingSet, string> layerOf, int viewedSlot)
        {
            int count = sets.Length;
            if (_masks == null || _masks.Length != count)
            {
                // A first tick, or a changed slot count: every slot starts
                // from Base, so a layer that is already on reads as one that
                // just turned on.
                _masks = new string[count];
                Array.Fill(_masks, Base);
                Forget();
            }

            int staySlot = -1, newsSlot = -1, newsRank = 0;
            string stayMask = null, newsMask = null;
            for (int slot = 0; slot < count; slot++)
            {
                var set = sets[slot];
                string mask = layerOf(slot, set);
                if (string.IsNullOrEmpty(mask)) mask = Base;
                string before = _masks[slot];
                _masks[slot] = mask;

                bool onBase = string.Equals(mask, Base, StringComparison.Ordinal);
                string choice = onBase ? null : LayerFlyout.OfLayer(set, mask);

                // The viewed slot's layer wins, else the lowest slot's.
                if (choice == LayerFlyout.Stay && (staySlot < 0 || slot == viewedSlot))
                {
                    staySlot = slot;
                    stayMask = mask;
                }

                if (string.Equals(mask, before, StringComparison.Ordinal)) continue;

                // The slot changed layers. A layer that shows briefly is
                // news. So is Base after a layer, unless either is silent.
                bool news = onBase
                    ? LayerFlyout.OfLayer(set, before) != LayerFlyout.Off && LayerFlyout.OfBase(set) != LayerFlyout.Off
                    : choice == LayerFlyout.Brief;
                if (!news) continue;

                // When slots change in the same tick, the one the flyout was
                // showing comes first (its layer is the one seen to end),
                // then the viewed slot, then the lowest.
                int rank = slot == _shownSlot ? 3 : slot == viewedSlot ? 2 : 1;
                if (rank > newsRank)
                {
                    newsSlot = slot;
                    newsMask = mask;
                    newsRank = rank;
                }
            }

            if (staySlot >= 0)
            {
                bool same = _shownStays && staySlot == _shownSlot
                    && string.Equals(stayMask, _shownMask, StringComparison.Ordinal);
                _shownSlot = staySlot;
                _shownMask = stayMask;
                _shownStays = true;
                return new Decision(same ? Step.KeepAlive : Step.Show, staySlot, stayMask);
            }

            if (newsSlot >= 0)
            {
                _shownSlot = newsSlot;
                _shownMask = newsMask;
                _shownStays = false;
                return new Decision(Step.Show, newsSlot, newsMask);
            }

            // Nothing new. A layer that stayed and is no longer one that
            // stays, or a layer its slot has left, goes at once. A brief one
            // that is still its slot's layer is left to the window's timer.
            // The slot shown is one of this tick's: a changed slot count ran
            // Forget above.
            if (_shownSlot >= 0
                && (_shownStays
                    || !string.Equals(_masks[_shownSlot], _shownMask, StringComparison.Ordinal)))
            {
                Forget();
                return new Decision(Step.Hide, -1, Base);
            }

            return default;
        }
    }
}
