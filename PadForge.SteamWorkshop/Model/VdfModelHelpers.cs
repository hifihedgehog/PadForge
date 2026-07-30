using System;
using System.Collections.Generic;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Model
{
    internal static class VdfModelHelpers
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyScalars =
            new Dictionary<string, string>(0);

        /// <summary>
        /// Builds a case-insensitive dictionary of the node's scalar (value) children.
        /// Object children (nested settings) are skipped. Returns an empty dictionary for a
        /// missing node. Duplicate keys resolve last-wins.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ScalarSettings(VdfNode node)
        {
            Dictionary<string, string> d = null;
            foreach (var kv in node.Children)
            {
                if (kv.Value.IsValue)
                    (d ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[kv.Key] = kv.Value.AsString;
            }
            return (IReadOnlyDictionary<string, string>)d ?? EmptyScalars;
        }
    }
}
