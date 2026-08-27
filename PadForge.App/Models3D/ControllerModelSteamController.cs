// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Steam Controller (2015) mesh: Valve's own CAD, released March 2016,
// CC BY-NC-SA 4.0. Meshed from the archive's STEP file, which is the exact
// B-rep surface, by tools/steam_controller_2015_mesh.py. Valve named
// every solid, so the button mapping comes from the source names rather
// than from guesswork, and the two-shot button molds come out as
// separate cap and glyph solids.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// Steam Controller (2015) model, serving the steam-controller and
    /// steam-controller-composite profiles.
    ///
    /// <para>Two departures from the standard part table, both physical.
    /// The pad has ONE analog stick, so there is no right stick and no
    /// RightStickClick mesh. And its D-pad is the LEFT TRACKPAD: SDL drives
    /// that pad as the D-pad and the right one as the right thumbstick, so
    /// the mesh tool quarters each face and the quarters arrive as their own
    /// meshes. The left pad's four are DPadUp/Down/Left/Right and register
    /// through the standard table like any D-pad key. The right pad's four
    /// carry the right stick's axis directions and register below.</para>
    ///
    /// <para>The trackpads and the rear grip buttons are the controls the
    /// standard table has no slot for. Each grip paddle is the FLARED WING
    /// of the rear cover, which Valve models as one solid spanning both
    /// handles, so the mesh tool splits the wings off it at the molded
    /// crease and hands each one the lever that sits behind it.</para>
    /// </summary>
    public class ControllerModelSteamController : ControllerModelBase
    {
        private readonly Model3DGroup LeftPad, RightPad;
        private readonly System.Collections.Generic.List<Model3DGroup> RightPadQuarters = new();
        private readonly Model3DGroup LeftGripButton, RightGripButton;

        public ControllerModelSteamController() : base("SteamController")
        {
            // ── Rotation points ─────────────────────────
            // Each is the mesh's own center in X and Z with its rear edge
            // in Y, the same construction the Xbox 360 model uses, read
            // off the converted meshes rather than eyeballed.
            JoystickRotationPointCenterLeftMillimeter = new Vector3D(-18.45f, -8.10f, -14.15f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D(0f, 0f, 0f);
            JoystickMaxAngleDeg = 18.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter = new Vector3D(-47.10f, -6.60f, 31.10f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D(47.10f, -6.60f, 31.10f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft = new Vector3D(-40.0f, -14.0f, 40.0f);
            UpwardVisibilityRotationPointRight = new Vector3D(40.0f, -14.0f, 40.0f);

            // ── Mappable controls the standard table does not cover ──
            LeftPad = LoadModel("LeftPadTouch.obj");
            RegisterButton("LeftTouchpadClick", LeftPad);
            model3DGroup.Children.Add(LeftPad);

            RightPad = LoadModel("RightPadTouch.obj");
            RegisterButton("RightTouchpadClick", RightPad);
            model3DGroup.Children.Add(RightPad);

            // The right pad IS the right thumbstick, in SDL's own words:
            // "the right pad is normally mapped to right thumbstick"
            // (SDL_hidapi_steam.c 1673, with RIGHTX and RIGHTY reading
            // sRightPadX/Y at 1650). Its four quarters carry that stick's
            // axis directions and each highlights as itself, which is how a
            // direction with its own mesh behaves everywhere in this tree.
            // The left pad's quarters are D-pad keys and the standard part
            // table has already registered them.
            //
            // Quarters rather than the quadrant-wedge path because these
            // pads are deep concave bowls: the wedge's torus-outward offset
            // drove half of each face sideways ACROSS the bowl instead of
            // off it, so two quadrants cleared and two sank under the
            // surface.
            foreach (var (file, target) in new[]
            {
                ("RightPadUp.obj", "RightThumbAxisYNeg"),
                ("RightPadDown.obj", "RightThumbAxisY"),
                ("RightPadLeft.obj", "RightThumbAxisXNeg"),
                ("RightPadRight.obj", "RightThumbAxisX"),
            })
            {
                var quarter = TryLoadModel(file);
                if (quarter == null) continue;
                RegisterDirection(quarter, target);
                RightPadQuarters.Add(quarter);
                model3DGroup.Children.Add(quarter);
            }

            LeftGripButton = LoadModel("LeftGrip.obj");
            RegisterButton("LeftGrip", LeftGripButton);
            model3DGroup.Children.Add(LeftGripButton);

            RightGripButton = LoadModel("RightGrip.obj");
            RegisterButton("RightGrip", RightGripButton);
            model3DGroup.Children.Add(RightGripButton);

            PaintEverything();

            // Glyph riders. Valve's CAD carries each button as a two-shot
            // mold, the cap and the printed glyph as separate solids, so
            // the letters come out as their own meshes. They ride on
            // their cap's highlight and take the printed colors: the
            // pack's SC art prints ABXY in green, red, blue and yellow on
            // black caps, and the system glyphs in white.
            // Glyph colors are the Xbox 360 class's own, already calibrated
            // for this light rig.
            var glyph = Mat("#D4D4D4");
            AddRiderTo("ButtonA", "B1-Symbol.obj", Mat("#7cb63b"));
            AddRiderTo("ButtonB", "B2-Symbol.obj", Mat("#ff5f4b"));
            AddRiderTo("ButtonX", "B3-Symbol.obj", Mat("#6ac4f6"));
            AddRiderTo("ButtonY", "B4-Symbol.obj", Mat("#faa51f"));
            AddRiderTo("ButtonStart", "StartIcon.obj", glyph);
            AddRiderTo("ButtonBack", "BackIcon.obj", glyph);
            // SteamButton_Label is the in-mold label FILM, 12.2 mm square
            // on a 12.3 mm cap: the whole button face, not a logo glyph.
            // Painted white it was a white disc. There is no logo geometry
            // to color, so the face is a dark button face, a step lighter
            // than the cap so it reads as a button rather than a hole.
            AddRiderTo("ButtonGuide", "SpecialIcon.obj", Mat("#2E2F31"));
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
        /// <para>The face CAPS are dark, not lettered colors. On this pad
        /// the ABXY caps are black plastic with colored glyphs printed on
        /// them, unlike the Xbox 360's colored shells. The color lives on
        /// the glyph riders added in the constructor, which Valve's STEP
        /// carries as their own solids.</para></summary>
        private void PaintEverything()
        {
            var body    = Mat("#202224");
            var shell   = Mat("#26272A");
            var surface = Mat("#3A3B3D");
            var recess  = Mat("#2E2F31");
            var disc    = Mat("#1A1B1D");

            Paint(MainBody, body);
            Paint(LeftShoulderTrigger, shell);
            Paint(RightShoulderTrigger, shell);
            PaintTarget("LeftShoulder", shell);
            PaintTarget("RightShoulder", shell);
            PaintTarget("LeftGrip", body);
            PaintTarget("RightGrip", body);
            PaintTarget("LeftTouchpadClick", surface);
            PaintTarget("RightTouchpadClick", surface);
            // The pad quarters take the pad's own color: they are the pad's
            // face, cut up so each direction can light on its own.
            foreach (var t in new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
                PaintTarget(t, surface);
            foreach (var q in RightPadQuarters)
                Paint(q, surface);
            PaintTarget("LeftThumbButton", recess);
            // The knurled cap is its own solid and its own control (the
            // stick's directions), so it is painted here rather than riding
            // the click mesh's PaintTarget. A shade below the collar under
            // it, which is what makes the collar read as a ring.
            Paint(LeftThumbRing, disc);
            PaintTarget("ButtonBack", recess);
            PaintTarget("ButtonStart", recess);
            PaintTarget("ButtonGuide", disc);
            foreach (var t in new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY" })
                PaintTarget(t, disc);
        }

        private static Material Mat(string hex) =>
            new DiffuseMaterial(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

        /// <summary>The preview camera is fixed, so every model carries a
        /// constant scale that brings its authoring size to the framing the
        /// camera expects. The Xbox 360 mesh is the reference at 151.45 mm
        /// across, and this pad's meshed STEP measures 161.17 mm.</summary>
        public override double ModelScale => 151.45 / 161.17;

    }
}
