using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The manifest and the controller callouts rendered the
    /// engine's own identifiers at users: "Gamepad Paddle2",
    /// "LeftThumbAxisX", "ButtonA". That is programmer vocabulary on a
    /// screen people read to decide whether to install a stranger's
    /// config.</summary>
    public class WorkshopInputNamingTests
    {
        [Theory]
        [InlineData("Gamepad Paddle2", "Paddle 2")]
        [InlineData("ButtonA", "A")]
        [InlineData("ButtonY", "Y")]
        [InlineData("LeftTrigger", "Left Trigger")]
        [InlineData("LeftShoulder", "Left Bumper")]
        [InlineData("LeftThumbAxisX", "Left Stick")]
        [InlineData("RightThumbAxisY", "Right Stick")]
        [InlineData("LeftThumbButton", "Left Stick Click")]
        [InlineData("DPadUp", "D-Pad Up")]
        [InlineData("ButtonStart", "Menu")]
        [InlineData("ButtonBack", "View")]
        public void NamesAreWhatAPlayerWouldSay(string raw, string expected)
            => Assert.Equal(expected, WorkshopBrowseDialog.FriendlySource(raw));

        /// <summary>Nothing user-facing may come out as run-together Pascal
        /// case, even for an input we never named.</summary>
        [Theory]
        [InlineData("SomeFutureThing")]
        [InlineData("Gamepad WeirdNewPad3")]
        public void UnknownInputsStillReadAsWords(string raw)
        {
            var s = WorkshopBrowseDialog.FriendlySource(raw);
            Assert.Contains(" ", s);
            Assert.DoesNotContain("Gamepad ", s);
        }

        [Theory]
        [InlineData("LeftStickX", "Left Stick X")]
        [InlineData("Paddle2", "Paddle 2")]
        public void SpacingSplitsOnCaseAndDigitBoundaries(string raw, string expected)
            => Assert.Equal(expected, WorkshopBrowseDialog.SpaceIdentifier(raw));

        /// <summary>THE JOIN THAT MUST NOT BREAK. Display names are
        /// humanized, but the controller art keys on the ENGINE identifier,
        /// so the art target has to stay raw. Humanizing both would silently
        /// stop lighting any button.</summary>
        [Theory]
        [InlineData("ButtonA", "ButtonA")]
        [InlineData("LeftTrigger", "LeftTrigger")]
        [InlineData("DPadUp", "DPadUp")]
        [InlineData("LeftThumbAxisX", "LeftThumbRing")]
        [InlineData("RightThumbAxisY", "RightThumbRing")]
        public void ArtTargetStaysTheEngineIdentifier(string target, string expected)
            => Assert.Equal(expected, WorkshopBrowseDialog.ArtTargetFor(target));
    }
}
