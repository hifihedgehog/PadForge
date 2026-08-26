// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Steam Controller (2026) mesh: Valve's own CAD, published with the
// hardware, CC BY-NC-SA 4.0. Valve released it as ONE merged solid with
// no part names, so unlike the 2015 pad there is nothing to read the
// mapping off. tools/steam_controller_2026_mesh.py recovers the parts:
// it splits the solid along its sharp edges, then names each piece by
// projecting it onto the front elevation from Valve's reference drawing,
// which is the same drawing the 2D art is built from. The bumpers, the
// triggers and the four rear buttons have no silhouette in a front view
// and are named by their position instead.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// Steam Controller (2026) model, serving the steam-controller-2
    /// profile.
    ///
    /// <para>Where the 2015 pad had one stick, two trackpads and no
    /// D-pad, this one carries two sticks, two square trackpads, a real
    /// D-pad and four rear buttons, so the two generations get separate
    /// meshes rather than sharing one.</para>
    ///
    /// <para>Rear-button handedness follows the translator, the same
    /// arrangement the Steam Deck model uses: R4 is Paddle1, L4 is
    /// Paddle2, R5 is Paddle3 and L5 is Paddle4.</para>
    /// </summary>
    public class ControllerModelSteamController2 : ControllerModelBase
    {
        private readonly Model3DGroup LeftPad, RightPad;
        private readonly Model3DGroup QuickAccess;
        private readonly Model3DGroup L4, L5, R4, R5;

        public ControllerModelSteamController2() : base("SteamController2")
        {
            // ── Rotation points ─────────────────────────
            // Each is the mesh's own centre in X and Z with its rear edge
            // in Y, the construction the Xbox 360 model uses, read off
            // the converted meshes rather than eyeballed.
            JoystickRotationPointCenterLeftMillimeter = new Vector3D(-23.13f, -28.26f, -31.35f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D(23.08f, -28.26f, -31.34f);
            JoystickMaxAngleDeg = 18.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter = new Vector3D(-47.73f, 15.11f, -6.25f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D(47.73f, 15.11f, -6.24f);
            TriggerMaxAngleDeg = 14.0f;

            UpwardVisibilityRotationAxisLeft = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft = new Vector3D(-47.7f, -14.1f, -1.1f);
            UpwardVisibilityRotationPointRight = new Vector3D(47.7f, -14.1f, -1.1f);

            // ── Mappable controls the standard table does not cover ──
            LeftPad = LoadModel("LeftPadTouch.obj");
            RegisterButton("LeftTouchpadClick", LeftPad);
            model3DGroup.Children.Add(LeftPad);

            RightPad = LoadModel("RightPadTouch.obj");
            RegisterButton("RightTouchpadClick", RightPad);
            model3DGroup.Children.Add(RightPad);

            QuickAccess = LoadModel("ThreeDots.obj");
            RegisterButton("ButtonQuickAccess", QuickAccess);
            model3DGroup.Children.Add(QuickAccess);

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
        /// <para>Face buttons stay dark for the same reason as the 2015
        /// pad. Valve prints the glyph on a black cap, and the CAD carries
        /// no glyph mesh to colour.</para></summary>
        private void PaintEverything()
        {
            var body    = Mat("#707477");
            var shell   = Mat("#7A7E82");
            var surface = Mat("#8C9095");
            var recess  = Mat("#5A5E62");
            var disc    = Mat("#4A4D50");

            Paint(MainBody, body);
            Paint(LeftThumbRing, recess);
            Paint(RightThumbRing, recess);
            Paint(LeftShoulderTrigger, shell);
            Paint(RightShoulderTrigger, shell);
            PaintTarget("LeftShoulder", shell);
            PaintTarget("RightShoulder", shell);
            PaintTarget("LeftTouchpadClick", surface);
            PaintTarget("RightTouchpadClick", surface);
            PaintTarget("LeftThumbButton", surface);
            PaintTarget("RightThumbButton", surface);
            foreach (var t in new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
                PaintTarget(t, surface);
            foreach (var t in new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY", "ButtonGuide" })
                PaintTarget(t, disc);
            foreach (var t in new[] { "ButtonBack", "ButtonStart", "ButtonQuickAccess" })
                PaintTarget(t, recess);
            foreach (var t in new[] { "Paddle1", "Paddle2", "Paddle3", "Paddle4" })
                PaintTarget(t, shell);
        }

        private static Material Mat(string hex) =>
            new DiffuseMaterial(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

        /// <summary>The preview camera is fixed, so every model carries a
        /// constant scale that brings its authoring size to the framing the
        /// camera expects. The Xbox 360 mesh is the reference at 151.45 mm
        /// across, and Valve's CAD puts this pad at 158.70 mm.</summary>
        public override double ModelScale => 151.45 / 158.70;

    }
}
