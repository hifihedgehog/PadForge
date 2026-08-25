// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Steam Deck mesh: Handheld Companion's own per-part OBJ set, used as
// shipped. Unlike every other model here it needed no splitting, because
// HC authored it against this same per-part contract. Cross-checked for
// proportion against Valve's official Steam Deck CAD
// (gitlab.steamos.cloud/SteamDeck/hardware, CC BY-NC-SA 4.0).

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// Steam Deck model, serving the steam-deck and steam-deck-composite
    /// profiles.
    ///
    /// <para>The Deck carries four controls the standard part table has no
    /// slot for: two trackpads, four back grips, and the Quick Access
    /// button beside the Steam button. Paddle handedness follows the 2D
    /// layout and the CustomInputState button order, where the odd paddles
    /// are the RIGHT pair: Paddle1 = R4, Paddle2 = L4, Paddle3 = R5,
    /// Paddle4 = L5.</para>
    ///
    /// <para>The screen, volume rocker, power button, Steam wordmark and
    /// the leftover shell pieces are cosmetic. They are added to the scene
    /// so the Deck looks like a Deck, and are deliberately not registered,
    /// so they never become click-to-record targets.</para>
    /// </summary>
    public class ControllerModelSteamDeck : ControllerModelBase
    {
        private readonly Model3DGroup QuickAccess;
        private readonly Model3DGroup LeftPad, RightPad;
        private readonly Model3DGroup L4, L5, R4, R5;

        public ControllerModelSteamDeck() : base("SteamDeck")
        {
            var ColorBody   = (Color)ColorConverter.ConvertFromString("#2B2B2E");
            var ColorAccent = (Color)ColorConverter.ConvertFromString("#3C3D40");
            var ColorScreen = (Color)ColorConverter.ConvertFromString("#101114");
            var MaterialBody   = new DiffuseMaterial(new SolidColorBrush(ColorBody));
            var MaterialAccent = new DiffuseMaterial(new SolidColorBrush(ColorAccent));
            var MaterialScreen = new DiffuseMaterial(new SolidColorBrush(ColorScreen));

            // ── Mappable controls the standard table does not cover ──
            QuickAccess = LoadModel("ThreeDots.obj");
            RegisterButton("ButtonQuickAccess", QuickAccess);
            model3DGroup.Children.Add(QuickAccess);

            LeftPad = LoadModel("LeftPadTouch.obj");
            RegisterButton("LeftTouchpadClick", LeftPad);
            model3DGroup.Children.Add(LeftPad);

            RightPad = LoadModel("RightPadTouch.obj");
            RegisterButton("RightTouchpadClick", RightPad);
            model3DGroup.Children.Add(RightPad);

            R4 = LoadModel("R4.obj");
            RegisterButton("Paddle1", R4);
            model3DGroup.Children.Add(R4);

            L4 = LoadModel("L4.obj");
            RegisterButton("Paddle2", L4);
            model3DGroup.Children.Add(L4);

            R5 = LoadModel("R5.obj");
            RegisterButton("Paddle3", R5);
            model3DGroup.Children.Add(R5);

            L5 = LoadModel("L5.obj");
            RegisterButton("Paddle4", L5);
            model3DGroup.Children.Add(L5);

            // ── Cosmetic geometry: drawn, never registered ──
            AddCosmetic("MainBodyLeftOver.obj", MaterialBody);
            AddCosmetic("Screen.obj", MaterialScreen);
            AddCosmetic("PowerButton.obj", MaterialAccent);
            AddCosmetic("VolumeUp.obj", MaterialAccent);
            AddCosmetic("VolumeDown.obj", MaterialAccent);
            AddCosmetic("SteamText.obj", MaterialAccent);
            AddCosmetic("OEM1.obj", MaterialAccent);
            AddCosmetic("LeftStickTouch.obj", MaterialAccent);
            AddCosmetic("RightStickTouch.obj", MaterialAccent);

            // Glyph riders sit on their buttons and share their highlight,
            // the same arrangement the other models use for label meshes.
            AddRiderTo("ButtonA", "B1-Symbol.obj");
            AddRiderTo("ButtonB", "B2-Symbol.obj");
            AddRiderTo("ButtonX", "B3-Symbol.obj");
            AddRiderTo("ButtonY", "B4-Symbol.obj");
            AddRiderTo("ButtonBack", "BackIcon.obj");
            AddRiderTo("ButtonStart", "StartIcon.obj");
        }

        /// <summary>The preview camera is fixed, so every model carries a
        /// constant scale that brings its authoring size to the framing the
        /// camera expects. The Xbox 360 mesh is the reference at 151.45 mm
        /// across, and Handheld Companion authored this one at 298.30 mm, close to twice that.</summary>
        public override double ModelScale => 151.45 / 298.30;

        private void AddCosmetic(string filename, Material material)
        {
            var group = TryLoadModel(filename);
            if (group == null) return;
            foreach (var child in group.Children)
                if (child is GeometryModel3D g)
                {
                    g.Material = material;
                    g.BackMaterial = material;
                }
            model3DGroup.Children.Add(group);
        }

        private void AddRiderTo(string padSettingName, string filename)
        {
            var rider = TryLoadModel(filename);
            if (rider == null) return;
            model3DGroup.Children.Add(rider);
            if (ButtonMap.TryGetValue(padSettingName, out var list))
                list.Add(rider);
        }
    }
}
