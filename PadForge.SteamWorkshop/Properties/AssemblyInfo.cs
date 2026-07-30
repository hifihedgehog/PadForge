using System.Runtime.CompilerServices;

// The translator's slot/mode predicates are pinned by tests. They decide
// whether an authored curve cluster reaches the emitted rows at all, and a
// wrong answer is silent: the profile translates, and the curve is simply
// absent. PadForge.Engine and PadForge.App already expose their internals to
// the test assembly for the same reason.
[assembly: InternalsVisibleTo("PadForge.Tests")]
