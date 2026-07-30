using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round nineteen.
    ///
    /// <para>MappingExpression had ZERO test references anywhere in the
    /// test project: a 31 KB parser and evaluator, user-facing (the Pad
    /// page's formula box, with a fully localized Pad_Formula_Error_*
    /// surface), and evaluated on the poll thread through a compile cache.
    /// That was the largest coverage gap found in the whole audit
    /// marathon. These cover the grammar's contract.</para>
    ///
    /// <para>ONE DEFECT FOUND, pinned below rather than changed:
    /// <see cref="SingleLetterVariable_e_IsShadowedByEulersConstant"/>.
    /// Fixing it means changing the semantics of a shipped user-facing
    /// language, and both directions break somebody, so it is the owner's
    /// call and not an audit's.</para></summary>
    public class AuditJuly26RoundNineteenTests
    {
        private static float Eval(string expr, params float[] sources)
        {
            var c = MappingExpression.Compile(expr);
            Assert.True(c.IsValid, $"failed to compile \"{expr}\": {c.Error}");
            return c.Evaluate(sources);
        }

        // ── Precedence and associativity ─────────────────────────────

        [Theory]
        [InlineData("1 + 2 * 3", 7)]          // not 9
        [InlineData("(1 + 2) * 3", 9)]
        [InlineData("10 - 2 - 3", 5)]         // left-associative, not 11
        [InlineData("100 / 10 / 2", 5)]       // left-associative, not 20
        [InlineData("2 + 3 > 4", 1)]          // additive binds tighter than comparison
        [InlineData("1 > 2 == 0", 1)]         // comparison binds tighter than equality
        [InlineData("-3 + 5", 2)]
        [InlineData("-(3 + 5)", -8)]
        public void PrecedenceAndAssociativity(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr), 4);
        }

        [Theory]
        [InlineData("1 > 0 ? 10 : 20", 10)]
        [InlineData("0 ? 10 : 20", 20)]
        [InlineData("1 ? 2 ? 3 : 4 : 5", 3)]  // nested ternary
        public void Ternary(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr), 4);
        }

        [Theory]
        [InlineData("1 && 1", 1)]
        [InlineData("1 && 0", 0)]
        [InlineData("0 || 0", 0)]
        [InlineData("0 || 1", 1)]
        [InlineData("2 && 3", 1)]             // truthiness collapses to 1
        public void BooleanOperatorsCollapseToOneOrZero(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr), 4);
        }

        // ── The arithmetic safety rails ──────────────────────────────

        /// <summary>Division and modulo by zero yield 0 rather than
        /// Infinity or NaN. This runs on the poll thread and its result
        /// drives an axis, so a non-finite escaping here would be a stuck
        /// or garbage output rather than a visible error.</summary>
        [Theory]
        [InlineData("1 / 0", 0)]
        [InlineData("5 % 0", 0)]
        [InlineData("-1 / 0", 0)]
        public void DivisionByZeroIsZero(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr), 4);
        }

        /// <summary>The INLINE divide-by-zero guard, isolated from the
        /// top-level non-finite guard that would otherwise mask it.
        ///
        /// <para>The cases above cannot tell the two layers apart: C# gives
        /// 1.0/0.0 as Infinity rather than throwing, so Evaluate's final
        /// IsInfinity check returns 0 whether or not the operator guards
        /// itself, and mutating the operator left them all green. As a
        /// SUB-expression the layers diverge, because the guard keeps the
        /// rest of the formula alive (0 + 5) while the outer net discards
        /// the whole result (Infinity + 5 is still infinite, so 0). A
        /// formula that divides by a source which happens to be centred
        /// therefore keeps computing instead of collapsing to nothing.</para></summary>
        [Theory]
        [InlineData("1 / 0 + 5", 5)]
        [InlineData("5 % 0 + 5", 5)]
        [InlineData("max(1 / 0, 3)", 3)]
        public void DivideByZeroGuardKeepsTheRestOfTheFormulaAlive(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr), 4);
        }

        /// <summary>A non-finite RESULT also clamps to 0. sqrt(-1) is NaN,
        /// and the evaluator's top-level guard catches it.</summary>
        [Fact]
        public void NonFiniteResultClampsToZero()
        {
            Assert.Equal(0f, Eval("sqrt(0 - 1)"), 4);
        }

        // ── Functions ────────────────────────────────────────────────

        [Theory]
        [InlineData("abs(0 - 4)", 4)]
        [InlineData("min(3, 7)", 3)]
        [InlineData("max(3, 7)", 7)]
        [InlineData("clamp(9, 0, 1)", 1)]
        [InlineData("clamp(-9, 0, 1)", 0)]
        [InlineData("clamp(0.5, 0, 1)", 0.5f)]
        [InlineData("sign(0 - 2)", -1)]
        [InlineData("sign(0)", 0)]
        [InlineData("floor(1.9)", 1)]
        [InlineData("ceil(1.1)", 2)]
        [InlineData("round(1.5)", 2)]
        [InlineData("sqrt(9)", 3)]
        public void Functions(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr), 4);
        }

        [Fact]
        public void PiConstantResolves()
        {
            Assert.Equal(3.14159f, Eval("pi"), 4);
        }

        [Theory]
        [InlineData("true", 1)]
        [InlineData("false", 0)]
        public void BooleanLiterals(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr), 4);
        }

        // ── Source addressing ────────────────────────────────────────

        /// <summary>Letters address sources positionally, a=0 through
        /// d=3.</summary>
        [Theory]
        [InlineData("a", 10)]
        [InlineData("b", 20)]
        [InlineData("c", 30)]
        [InlineData("d", 40)]
        public void SingleLetterVariablesAddressSourcesPositionally(string expr, float expected)
        {
            Assert.Equal(expected, Eval(expr, 10, 20, 30, 40, 50), 4);
        }

        /// <summary>A letter past the supplied sources reads 0 rather than
        /// throwing, so a formula outliving a device removal degrades
        /// quietly.</summary>
        [Fact]
        public void OutOfRangeLetterReadsZero()
        {
            Assert.Equal(0f, Eval("z", 10, 20), 4);
        }

        /// <summary>THE FIX (owner decision, 2026-07-26). Every letter
        /// addresses a source, so <c>e</c> addresses the FIFTH one like any
        /// other letter.
        ///
        /// <para>It used to return Euler's number. SingleLetterSourceNode
        /// maps a letter to a source by <c>Letter - 'a'</c>, making 'e'
        /// index 4, but ParsePrimary tested the "e" constant BEFORE the
        /// single-letter branch, so a formula on a row with five or more
        /// sources silently evaluated 2.718281828 instead of that source's
        /// value, with no error and no warning. Rows carry no source-count
        /// cap, so any author adding a fifth extra source could hit
        /// it.</para>
        ///
        /// <para>The trade was lopsided: this grammar has no power operator
        /// and no exp(), so Euler's number could only ever be a magic
        /// multiplier, while the fifth source is something users reach for
        /// naturally. <c>pi</c> keeps its constant because two characters
        /// cannot collide with a single-letter source.</para></summary>
        [Fact]
        public void SingleLetterVariable_e_AddressesTheFifthSource()
        {
            Assert.Equal(50f, Eval("e", 10, 20, 30, 40, 50), 4);
        }

        /// <summary>All five leading letters now read consecutively, which
        /// is the property the old constant broke.</summary>
        [Fact]
        public void LettersAToE_ReadConsecutiveSources()
        {
            var src = new float[] { 10, 20, 30, 40, 50 };
            Assert.Equal(10f, Eval("a", src), 4);
            Assert.Equal(20f, Eval("b", src), 4);
            Assert.Equal(30f, Eval("c", src), 4);
            Assert.Equal(40f, Eval("d", src), 4);
            Assert.Equal(50f, Eval("e", src), 4);
        }

        /// <summary>The indexed form still agrees with the letter form, so
        /// the documented workaround keeps working for anyone who adopted
        /// it.</summary>
        [Fact]
        public void IndexedFormAgreesWithTheLetterForm()
        {
            var src = new float[] { 10, 20, 30, 40, 50 };
            Assert.Equal(Eval("s[4]", src), Eval("e", src), 4);
            Assert.Equal(50f, Eval("s[4]", src), 4);
        }

        /// <summary>pi is unaffected: it is two characters, so it cannot
        /// shadow a source.</summary>
        [Fact]
        public void PiSurvivesTheChange()
        {
            Assert.Equal(3.14159f, Eval("pi", 10, 20, 30, 40, 50), 4);
        }

        [Fact]
        public void IndexedFormIsBoundsChecked()
        {
            Assert.Equal(0f, Eval("s[99]", 10, 20), 4);
        }

        // ── Compile failures ─────────────────────────────────────────

        /// <summary>Malformed formulas must report invalid rather than
        /// throwing into the caller, since this compiles from user text.</summary>
        [Theory]
        [InlineData("1 +")]
        [InlineData("(1 + 2")]
        [InlineData("min(1,")]
        [InlineData("1 = 1")]        // single equals is explicitly rejected
        [InlineData("@")]
        [InlineData("nosuchvar")]
        public void MalformedExpressionsCompileInvalid(string expr)
        {
            var c = MappingExpression.Compile(expr);
            Assert.False(c.IsValid);
            Assert.False(string.IsNullOrEmpty(c.Error));
        }

        /// <summary>An invalid compile still evaluates to a safe 0 rather
        /// than throwing on the poll thread.</summary>
        [Fact]
        public void InvalidCompileEvaluatesToZero()
        {
            var c = MappingExpression.Compile("1 +");
            Assert.False(c.IsValid);
            Assert.Equal(0f, c.Evaluate(new float[] { 1, 2, 3 }));
        }
    }
}
