using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.Mapping
{
    /// <summary>
    /// Combines per-source values into a row's final output per the row's
    /// <c>CombineMode</c>. Stateless; <see cref="MappingExpression.MappingExpression"/>
    /// handles the <c>Custom</c> mode separately because it carries a
    /// compiled AST.
    /// </summary>
    public static class CombineHelper
    {
        /// <summary>Combines bipolar / unipolar axis source values.
        /// Empty <paramref name="mode"/> defaults to MaxAbs.</summary>
        public static float CombineAxis(string mode, IList<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            if (values.Count == 1) return values[0];

            switch (mode)
            {
                case "Sum":
                {
                    float s = 0f;
                    for (int i = 0; i < values.Count; i++) s += values[i];
                    return s;
                }
                case "Average":
                {
                    float s = 0f;
                    for (int i = 0; i < values.Count; i++) s += values[i];
                    return s / values.Count;
                }
                case "MaxAbs":
                case "":
                default:
                {
                    float best = values[0];
                    for (int i = 1; i < values.Count; i++)
                    {
                        if (Math.Abs(values[i]) > Math.Abs(best))
                            best = values[i];
                    }
                    return best;
                }
            }
        }

        /// <summary>Combines bool button source values. Empty
        /// <paramref name="mode"/> defaults to OR.</summary>
        public static bool CombineButton(string mode, IList<bool> values)
        {
            if (values == null || values.Count == 0) return false;
            if (values.Count == 1) return values[0];

            switch (mode)
            {
                case "AND":
                {
                    for (int i = 0; i < values.Count; i++)
                        if (!values[i]) return false;
                    return true;
                }
                case "XOR":
                {
                    int trueCount = 0;
                    for (int i = 0; i < values.Count; i++)
                        if (values[i]) trueCount++;
                    return trueCount == 1;
                }
                case "OR":
                case "":
                default:
                {
                    for (int i = 0; i < values.Count; i++)
                        if (values[i]) return true;
                    return false;
                }
            }
        }
    }
}
