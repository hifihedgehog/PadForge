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
            var ColorBody   = (Color)ColorConverter.ConvertFromString("#707477");
            var ColorAccent = (Color)ColorConverter.ConvertFromString("#7A7E82");
            var ColorScreen = (Color)ColorConverter.ConvertFromString("#202226");
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
            // Valve prints the Deck's glyphs in white on a dark cap, and
            // the letters carry no colour of their own.
            var MaterialGlyph = new DiffuseMaterial(new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#D4D4D4")));
            AddRiderTo("ButtonA", "B1-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonB", "B2-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonX", "B3-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonY", "B4-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonBack", "BackIcon.obj", MaterialGlyph);
            AddRiderTo("ButtonStart", "StartIcon.obj", MaterialGlyph);

            PaintEverything();
        }

        /// <summary>Resting colours, on this tree's 3D convention rather than
        /// sampled from 2D art. The app lights a model with one white
        /// headlight at brightness 0.35 and an ember rim, and nothing else,
        /// so a material shows at roughly a third of its hex. That is why the
        /// Xbox 360 class writes black plastic as #707477, and every value
        /// here is that value or a step off it, so this pad sits beside the
        /// reference model as the same black plastic. The 2D art's #1E2A30,
        /// used here once, rendered as a blue-grey under an orange rim at a
        /// third of its brightness.
        ///
        /// <para>Without this every mesh rendered in HelixToolkit's own
        /// default, which is yellow. The cosmetic parts above were painted
        /// from the start; the body and every mappable control were
        /// not.</para></summary>
        private void PaintEverything()
        {
            var shell   = Mat("#707477");
            var panel   = Mat("#7A7E82");
            var surface = Mat("#8C9095");
            var recess  = Mat("#5A5E62");

            Paint(MainBody, shell);
            Paint(LeftThumbRing, recess);
            Paint(RightThumbRing, recess);
            Paint(LeftShoulderTrigger, panel);
            Paint(RightShoulderTrigger, panel);
            Paint(LeftMotor, recess);
            Paint(RightMotor, recess);

            PaintTarget("LeftShoulder", panel);
            PaintTarget("RightShoulder", panel);
            PaintTarget("LeftTouchpadClick", recess);
            PaintTarget("RightTouchpadClick", recess);
            PaintTarget("LeftThumbButton", surface);
            PaintTarget("RightThumbButton", surface);
            PaintTarget("ButtonQuickAccess", panel);
            foreach (var t in new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
                PaintTarget(t, panel);
            foreach (var t in new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY",
                                      "ButtonBack", "ButtonStart", "ButtonGuide" })
                PaintTarget(t, panel);
            foreach (var t in new[] { "Paddle1", "Paddle2", "Paddle3", "Paddle4" })
                PaintTarget(t, recess);
        }

        private static Material Mat(string hex) =>
            new DiffuseMaterial(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

        /// <summary>The preview camera is fixed, so every model carries a
        /// constant scale that brings its authoring size to the framing the
        /// camera expects. The Xbox 360 mesh is the reference at 151.45 mm
        /// across, and Handheld Companion authored this one at 298.30 mm, close to twice that.</summary>
        public override double ModelScale => 151.45 / 298.30;

        private void AddCosmetic(string filename, Material material)
        {
            var group = TryLoadModel(filename);
            if (group == null) return;
            // Paint registers the resting material as well as applying it.
            // Setting only the geometry material leaves the part yellow
            // again the moment anything restores it.
            Paint(group, material);
            model3DGroup.Children.Add(group);
        }

        private void AddRiderTo(string padSettingName, string filename, Material material)
        {
            var rider = TryLoadModel(filename);
            if (rider == null) return;
            Paint(rider, material);
            model3DGroup.Children.Add(rider);
            if (ButtonMap.TryGetValue(padSettingName, out var list))
                list.Add(rider);
        }
    }
}
