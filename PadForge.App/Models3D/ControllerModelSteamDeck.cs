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
            var ColorBody   = (Color)ColorConverter.ConvertFromString("#202224");
            var ColorAccent = (Color)ColorConverter.ConvertFromString("#26272A");
            var ColorScreen = (Color)ColorConverter.ConvertFromString("#0C0D10");
            var MaterialBody   = new DiffuseMaterial(new SolidColorBrush(ColorBody));
            var MaterialAccent = new DiffuseMaterial(new SolidColorBrush(ColorAccent));
            var MaterialScreen = new DiffuseMaterial(new SolidColorBrush(ColorScreen));

            // ── Mappable controls the standard table does not cover ──
            // The Quick Access KEY, mirror of the Steam key on the other
            // side: OEM1 is the 16.25 mm button and ThreeDots is the 9.32 mm
            // glyph printed on it, which rides it below. Registering the
            // glyph as the control lit three dots and nothing under them.
            QuickAccess = LoadModel("OEM1.obj");
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

            // The stick BODY, the capacitive barrel between the cap and the
            // collar. It is part of the stick button, not scenery: this pad
            // splits its stick into three solids where every other model
            // ships two, and with the body left cosmetic the button lit only
            // the thin collar at the case and the stick looked half dead.
            // Added BEFORE the paint pass on purpose, so PaintTarget gives it
            // the click mesh's own color.
            AddRiderTo("LeftThumbButton", "LeftStickTouch.obj", MaterialAccent);
            AddRiderTo("RightThumbButton", "RightStickTouch.obj", MaterialAccent);

            PaintEverything();

            // Glyph riders sit on their buttons and share their highlight,
            // the arrangement every model here uses for a label mesh. Valve
            // prints the Deck's in a light gray on a dark key.
            //
            // AFTER the paint pass, which is the half that was wrong: a rider
            // joins its host's ButtonMap list, so PaintTarget reached these
            // too and repainted every letter in its own cap's color.
            var MaterialGlyph = new DiffuseMaterial(new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#D4D4D4")));
            AddRiderTo("ButtonA", "B1-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonB", "B2-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonX", "B3-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonY", "B4-Symbol.obj", MaterialGlyph);
            AddRiderTo("ButtonBack", "BackIcon.obj", MaterialGlyph);
            AddRiderTo("ButtonStart", "StartIcon.obj", MaterialGlyph);
            // The Steam wordmark and the Quick Access dots are the same
            // thing as a letter on a face key, one per side.
            AddRiderTo("ButtonGuide", "SteamText.obj", MaterialGlyph);
            AddRiderTo("ButtonQuickAccess", "ThreeDots.obj", MaterialGlyph);
        }

        /// <summary>Resting colors, calibrated against the black controllers
        /// this tree already ships. The viewport's rig is a #999999 sun, a
        /// #666666 ambient, a #595959 headlight and the ember rim, so a
        /// front-facing surface shows at about 1.3 times its hex. The three
        /// approved dark textures (DS4 Jet Black, Switch 2 Pro, DualSense
        /// Midnight) all have a body median near #202224, and the Switch 2
        /// Pro class's accent constants give the scale above it. Two earlier
        /// palettes were wrong for the same reason: one sampled 2D art, one
        /// assumed the rig was a third as bright as it is.
        ///
        /// <para>Without this every mesh rendered in HelixToolkit's own
        /// default, which is yellow. The cosmetic parts above were painted
        /// from the start; the body and every mappable control were
        /// not.</para></summary>
        private void PaintEverything()
        {
            var shell   = Mat("#202224");
            var panel   = Mat("#2E2F31");
            var surface = Mat("#3A3B3D");
            var recess  = Mat("#26272A");

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
