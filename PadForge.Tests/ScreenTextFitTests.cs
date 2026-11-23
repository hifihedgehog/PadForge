using System;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine.Common.BlissBox;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The device screens draw a profile's name as large as fits: the VMU,
    /// the keyboard screens and the Flydigi screen share one fit. A name too
    /// long for the screen at the smallest size was drawn at that size from
    /// the top anyway, and its lower lines ran past the screen's bottom edge,
    /// cut through their letters. It now keeps the lines that fit and ends
    /// in an ellipsis.
    /// </summary>
    public class ScreenTextFitTests
    {
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        [Fact]
        public void ANameTooLongForTheScreen_StaysInsideIt() => RunSta(() =>
        {
            string words = string.Join(" ", Enumerable.Repeat("Profile", 38));
            Assert.True(words.Length >= 300);
            string word = new string('W', 300);

            // Positive control: a short name fits at a size above the floor.
            var shortName = DreamcastScreenService.FitText("Racing");
            Assert.True(shortName.Height <= BlissBoxScreen.Height);
            Assert.True(shortName.Extent > 0);

            var vmu = DreamcastScreenService.FitText(words);
            Assert.True(vmu.Height <= BlissBoxScreen.Height,
                $"the VMU's name is {vmu.Height:0.#} high on a screen {BlissBoxScreen.Height} high");
            var vmuWord = DreamcastScreenService.FitText(word);
            Assert.True(vmuWord.Height <= BlissBoxScreen.Height,
                $"the VMU's one-word name is {vmuWord.Height:0.#} high on a screen {BlissBoxScreen.Height} high");

            // The Flydigi screen's text box: 160 by 80 less a margin of 8.
            var flydigi = KeyboardScreenRenderer.FitText(words + " " + words, Brushes.White, 144, 64);
            Assert.True(flydigi.Height <= 64, $"the Flydigi screen's name is {flydigi.Height:0.#} high in a box 64 high");
        });
    }
}
