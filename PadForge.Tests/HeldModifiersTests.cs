using System.IO;
using System.Threading;
using System.Windows.Input;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A control that reads its own keys asks HeldModifiers which modifier
    /// keys are held, so a test of the control says which are and does not
    /// read the real keyboard. Two key tests failed in full runs with the
    /// symptom a held modifier gives (2026-11-07, 2026-11-11).
    /// </summary>
    public class HeldModifiersTests
    {
        /// <summary>What a test says holds on its own thread until it is
        /// disposed, and what was said before holds again after it. Another
        /// thread has its own.</summary>
        [Fact]
        public void WhatIsSaidHoldsOnItsThreadUntilItIsDisposed()
        {
            using (HeldModifiers.Say(ModifierKeys.Control))
            {
                Assert.Equal(ModifierKeys.Control, HeldModifiers.Now);
                using (HeldModifiers.Say(ModifierKeys.Shift | ModifierKeys.Alt))
                    Assert.Equal(ModifierKeys.Shift | ModifierKeys.Alt, HeldModifiers.Now);
                Assert.Equal(ModifierKeys.Control, HeldModifiers.Now);

                ModifierKeys elsewhere = ModifierKeys.Windows;
                var thread = new Thread(() =>
                {
                    using (HeldModifiers.Say(ModifierKeys.None))
                        elsewhere = HeldModifiers.Now;
                });
                thread.Start();
                thread.Join();
                Assert.Equal(ModifierKeys.None, elsewhere);
                Assert.Equal(ModifierKeys.Control, HeldModifiers.Now);
            }
        }

        /// <summary>The two controls whose keys a test sends ask
        /// HeldModifiers, never the keyboard itself.</summary>
        [Theory]
        [InlineData("ClickBorder.cs")]
        [InlineData("CurveEditor.xaml.cs")]
        public void AControlWhoseKeysATestSendsAsksHeldModifiers(string file)
        {
            string code = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("PadForge.App", "Controls", file)));
            Assert.Contains("HeldModifiers.Now", code);
            Assert.DoesNotContain("Keyboard.Modifiers", code);
        }
    }
}
