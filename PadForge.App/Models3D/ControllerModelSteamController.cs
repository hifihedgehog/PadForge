// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Steam Controller (2015) mesh: Valve's own CAD, released March 2016 as
// an archive of separate-part STLs, CC BY-NC-SA 4.0. Converted to this
// repo's per-part OBJ contract by tools/steam_controller_2015_mesh.py.
// Valve named every part, so the button mapping comes from the source
// filenames rather than from guesswork.

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
    /// RightStickClick mesh. And it has no D-pad at all: the left
    /// trackpad serves that role, which is why the 2D layout for this pad
    /// carries no DPad entries either.</para>
    ///
    /// <para>The trackpads and the rear grip buttons are the controls the
    /// standard table has no slot for. Valve ships each as its own solid
    /// (the grips are the battery-door levers, which is what they
    /// physically are on this pad), so each registers directly.</para>
    /// </summary>
    public class ControllerModelSteamController : ControllerModelBase
    {
        private readonly Model3DGroup LeftPad, RightPad;
        private readonly Model3DGroup LeftGripButton, RightGripButton;

        public ControllerModelSteamController() : base("SteamController")
        {
            // ── Rotation points ─────────────────────────
            // Each is the mesh's own centre in X and Z with its rear edge
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

            LeftGripButton = LoadModel("LeftGrip.obj");
            RegisterButton("LeftGrip", LeftGripButton);
            model3DGroup.Children.Add(LeftGripButton);

            RightGripButton = LoadModel("RightGrip.obj");
            RegisterButton("RightGrip", RightGripButton);
            model3DGroup.Children.Add(RightGripButton);

            PaintEverything();
        }

        /// <summary>Resting colours, sampled from this pad's own shipped 2D
        /// art rather than picked, so the two previews agree and both match
        /// the hardware. The five that cover the device are its body at
        /// #1E2A30, a slightly cooler shell at #24303C, the trackpad
        /// surfaces at #5A606C, the recesses at #424E54 and the button
        /// discs at #1E1E1E.
        ///
        /// <para>The face buttons are DARK, not lettered colours. On this
        /// pad the ABXY caps are black plastic with coloured glyphs printed
        /// on them, unlike the Xbox 360's coloured shells, and there are no
        /// glyph meshes in Valve's CAD to carry the colour.</para></summary>
        private void PaintEverything()
        {
            var body    = Mat("#1E2A30");
            var shell   = Mat("#24303C");
            var surface = Mat("#5A606C");
            var recess  = Mat("#424E54");
            var disc    = Mat("#1E1E1E");

            Paint(MainBody, body);
            Paint(LeftShoulderTrigger, shell);
            Paint(RightShoulderTrigger, shell);
            PaintTarget("LeftShoulder", shell);
            PaintTarget("RightShoulder", shell);
            PaintTarget("LeftGrip", body);
            PaintTarget("RightGrip", body);
            PaintTarget("LeftTouchpadClick", surface);
            PaintTarget("RightTouchpadClick", surface);
            PaintTarget("LeftThumbButton", recess);
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
        /// across, and this pad's reconstructed skin measures 161.39 mm.</summary>
        public override double ModelScale => 151.45 / 161.39;

    }
}
