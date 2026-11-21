using System.Linq;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Each 3D controller draws its View button (the Back target) on the
    /// left and its Menu button (the Start target) on the right, as the
    /// pads have them and as SDL names them (SDL_hidapi_steamdeck.c: the
    /// Deck's VIEW button is SDL_GAMEPAD_BUTTON_BACK, MENU is START). The
    /// Steam Deck's model had the two the other way round: its left button,
    /// with the two-squares mark, recorded Menu, and the Picture's tags
    /// read Menu on the left and View on the right.
    /// </summary>
    public class ControllerModelSidesTests
    {
        private static readonly string[] s_families =
        {
            "XBOX360", "XboxSeries", "DS3", "DS4", "DualSense", "DualSenseEdge",
            "Switch2Pro", "SteamDeck", "SteamController", "SteamController2",
        };

        /// <summary>The middle of every part a target lights, across the
        /// model: under zero is the left of the picture, since the default
        /// camera's right is +x (ControllerModelFraming.Look crossed with
        /// Up).</summary>
        private static double Across(ControllerModelBase model, string target)
        {
            Assert.True(model.ButtonMap.TryGetValue(target, out var parts) && parts.Count > 0, target + " has no part");
            Rect3D box = Rect3D.Empty;
            foreach (var part in parts) box.Union(part.Bounds);
            return box.X + box.SizeX / 2;
        }

        [Fact]
        public void TheCameraLooksWithPlusXToItsRight()
        {
            Vector3D right = Vector3D.CrossProduct(PadForge.Views.ControllerModelFraming.Look, PadForge.Views.ControllerModelFraming.Up);
            Assert.True(right.X > 0.99 * right.Length, right.ToString());
        }

        [Theory]
        [MemberData(nameof(Families))]
        public void ViewIsOnTheLeftAndMenuOnTheRight(string family)
        {
            using var model = ControllerModelBase.Create(family, null, false);
            double view = Across(model, "ButtonBack");
            double menu = Across(model, "ButtonStart");
            Assert.True(view < 0 && menu > 0, $"{family}: View is drawn at {view:0.#} and Menu at {menu:0.#} across the model");
        }

        public static System.Collections.Generic.IEnumerable<object[]> Families() => s_families.Select(f => new object[] { f });
    }
}
