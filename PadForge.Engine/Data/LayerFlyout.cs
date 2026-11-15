using System;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// How long the Shift Layer Flyout shows a layer (#581), as
    /// <see cref="ShiftActivator.Flyout"/> and <see cref="MappingSet.BaseFlyout"/>
    /// store it.
    ///
    /// <para>A stored value that is empty, or one this build does not know,
    /// reads as the layer's default, so a file written before the setting
    /// behaves as it did: a shift layer keeps its flyout while it is on, and
    /// Base shows its flyout for the window's two seconds when a shift layer
    /// ends.</para>
    /// </summary>
    public static class LayerFlyout
    {
        /// <summary>The flyout stays for as long as the layer is on. A shift
        /// layer's default. Base has no such choice: it is on whenever no
        /// shift layer is.</summary>
        public const string Stay = "Stay";

        /// <summary>The flyout shows when the layer turns on, and the
        /// window's own timer takes it away. Base's default.</summary>
        public const string Brief = "Brief";

        /// <summary>The layer shows no flyout, and Base shows none when the
        /// layer ends.</summary>
        public const string Off = "Off";

        /// <summary>A shift layer's choice, from its stored value.</summary>
        public static string OfLayer(string stored)
            => string.Equals(stored, Brief, StringComparison.Ordinal) ? Brief
             : string.Equals(stored, Off, StringComparison.Ordinal) ? Off
             : Stay;

        /// <summary>Base's choice, from its stored value: Brief or Off.</summary>
        public static string OfBase(string stored)
            => string.Equals(stored, Off, StringComparison.Ordinal) ? Off : Brief;

        /// <summary>What a shift layer's choice is stored as: empty for the
        /// default, so a layer nobody set writes what it always wrote.</summary>
        public static string StoreLayer(string choice)
        {
            string known = OfLayer(choice);
            return known == Stay ? "" : known;
        }

        /// <summary>What Base's choice is stored as: empty for the default.</summary>
        public static string StoreBase(string choice)
            => OfBase(choice) == Off ? Off : "";

        /// <summary>The choice of the layer a slot is on. The first activator
        /// that names the layer speaks for it, as it does for the layer's
        /// name, color and icon on the flyout. A layer no activator names
        /// (one left behind by a delete) reads as the default.</summary>
        public static string OfLayer(MappingSet set, string layerMask)
        {
            var activators = set?.ShiftActivators;
            if (activators != null)
            {
                foreach (var activator in activators)
                {
                    if (activator == null) continue;
                    if (string.Equals(activator.LayerMask, layerMask, StringComparison.Ordinal))
                        return OfLayer(activator.Flyout);
                }
            }
            return Stay;
        }

        /// <summary>Base's choice on a slot. A slot with no mapping set reads
        /// as the default.</summary>
        public static string OfBase(MappingSet set) => OfBase(set?.BaseFlyout);
    }
}
