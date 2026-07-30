using System.Linq;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// A click / contact gate is no longer a hidden field on the source. It is
    /// a REAL source on the row plus a Custom combine expression that ANDs it
    /// against the source it gates, because PadForge already exposes that
    /// mechanism: the combine dropdown and its expression are user-editable,
    /// and MappingSource.GateDescriptor was bound to no card at all.
    ///
    /// <para>These helpers assert that contract in one line, so the shape lives
    /// in one place instead of a dozen hand-written source-index assertions.</para>
    /// </summary>
    internal static class GateAssert
    {
        /// <summary>Asserts <paramref name="primary"/> appears on some row of
        /// <paramref name="set"/> gated on <paramref name="gates"/>, in order,
        /// through the row's Custom expression.</summary>
        public static void Gated(MappingSet set, string primary, params string[] gates)
        {
            Assert.NotNull(set);
            var row = set.Rows.SingleOrDefault(
                r => r.Sources.Any(s => s.Descriptor == primary));
            Assert.NotNull(row);

            int i = row.Sources.ToList().FindIndex(s => s.Descriptor == primary);
            Assert.True(i >= 0, $"'{primary}' is not a source on its row");

            // The gates follow the source they gate, in the order the
            // translator resolved them (primary gate, then the second).
            for (int g = 0; g < gates.Length; g++)
            {
                Assert.True(i + 1 + g < row.Sources.Count,
                    $"expected a gate source after '{primary}'");
                Assert.Equal(gates[g], row.Sources[i + 1 + g].Descriptor);
            }

            Assert.Equal("Custom", row.CombineMode);

            // A button gate is && and an axis gate is multiplication by the
            // gate's 0/1 read. Both are the same contract, expressed for the
            // target's type, so accept whichever the row's own type produced
            // rather than hard-coding one and mislabeling the other a failure.
            string button = Term(i, gates.Length, "&&");
            string axis = Term(i, gates.Length, "*");
            string expr = row.CombineExpression ?? "";
            Assert.True(expr.Contains(button) || expr.Contains(axis),
                $"'{primary}' is not gated in the row's expression.{System.Environment.NewLine}"
                + $"  expression: {expr}{System.Environment.NewLine}"
                + $"  wanted:     {button}{System.Environment.NewLine}"
                + $"  or:         {axis}");
        }

        /// <summary>The expression term this source and its gates must form,
        /// for the given gate operator.</summary>
        private static string Term(int index, int gateCount, string op)
        {
            string t = "s[" + index + "]";
            for (int g = 0; g < gateCount; g++)
                t = "(" + t + " " + op + " s[" + (index + 1 + g) + "])";
            return t;
        }

        /// <summary>Asserts the row carrying <paramref name="primary"/> has NO
        /// gate: one source, no Custom expression. The negative half, so a
        /// "gate applied" assertion cannot pass vacuously.</summary>
        public static void Ungated(MappingSet set, string primary)
        {
            var row = set.Rows.SingleOrDefault(
                r => r.Sources.Any(s => s.Descriptor == primary));
            Assert.NotNull(row);
            Assert.DoesNotContain("&&", row.CombineExpression ?? "");
        }
    }
}
