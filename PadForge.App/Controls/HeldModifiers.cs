using System;
using System.Windows.Input;

namespace PadForge.Controls
{
    /// <summary>
    /// The modifier keys held now, for a control that reads its own keys.
    /// A control asks here and not the keyboard. The keyboard's answer is
    /// the state of the real keys, which a test of the control neither
    /// sets nor may depend on: ClickBorder and CurveEditor leave a key to
    /// the page while a modifier is held, and their key tests failed in
    /// full runs with exactly that symptom (2026-11-07, 2026-11-11).
    /// </summary>
    internal static class HeldModifiers
    {
        [ThreadStatic]
        private static ModifierKeys? t_said;

        /// <summary>The modifier keys held now: the keyboard's, unless a
        /// test has said which are held on this thread.</summary>
        internal static ModifierKeys Now => t_said ?? Keyboard.Modifiers;

        /// <summary>Says which modifier keys are held, on the calling
        /// thread, until the returned object is disposed. What was said
        /// before, if anything, holds again after it.</summary>
        internal static IDisposable Say(ModifierKeys held)
        {
            ModifierKeys? before = t_said;
            t_said = held;
            return new Restore(before);
        }

        private sealed class Restore : IDisposable
        {
            private readonly ModifierKeys? _before;

            public Restore(ModifierKeys? before) => _before = before;

            public void Dispose() => t_said = _before;
        }
    }
}
