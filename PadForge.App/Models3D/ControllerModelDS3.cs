// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// DualShock 3 mesh: "Sony PlayStation Dualshock 3 wireless controller" by
// 3doverstock, bought on CGTrader, split into per-part OBJs by
// tools/dualshock3_mesh.py. Its one baked diffuse carries every printed
// mark, so every part reads from the same atlas and there is no decal set.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// DualShock 3 controller model. B1 to B4 follow the PlayStation
    /// positions the DS4 and DualSense use (B1 is cross, at the bottom).
    /// SELECT is Back and the PS button is Special. The pad has no
    /// touchpad and no light bar.
    /// </summary>
    public class ControllerModelDS3 : ControllerModelBase
    {
        public ControllerModelDS3() : base("DS3")
        {
            var MaterialBody = LoadTexturedMaterial("Body.png");

            // ── Rotation points, printed by the converter ───────
            // The stick's dome is a sphere to a tenth of a millimeter, the
            // shape that lets it turn in place and keep filling the
            // faceplate hole, so its center is the pivot: 26.9 mm behind
            // the cap's apex. The cap first touches the faceplate at 20.9
            // degrees with both axes at full deflection, 30 with one.
            JoystickRotationPointCenterLeftMillimeter = new Vector3D(-23.129, -5.622, -11.074);
            JoystickRotationPointCenterRightMillimeter = new Vector3D(23.129, -5.614, -11.074);
            JoystickMaxAngleDeg = 19.0f;

            // A third of the way up the trigger, by the Xbox One model's
            // fraction of the part's own bounds. L1 stays more than 7 mm
            // away at any angle. At full pull three vertices on the
            // trigger's lower back edge sink 0.88 mm into the grip's lip
            // beneath it, out of sight under the trigger, against 1.45 mm
            // on the DS4.
            ShoulderTriggerRotationPointCenterLeftMillimeter = new Vector3D(-45.991, 7.838, 37.676);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D(45.991, 7.838, 37.676);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft = new Vector3D(-45.99, -8.74, 45.83);
            UpwardVisibilityRotationPointRight = new Vector3D(45.99, -8.74, 45.83);

            // ── Materials ───────────────────────────────
            // Every part keeps its UVs into the one atlas, the stick caps
            // and the face-button symbols included.
            foreach (var list in ButtonMap.Values)
                foreach (var group in list)
                    Paint(group, MaterialBody);
            foreach (var child in model3DGroup.Children)
                if (child is Model3DGroup group && !DefaultMaterials.ContainsKey(group))
                    Paint(group, MaterialBody);

            // The shell is a single skin. A deflected stick's dome opens a
            // gap onto its inside, and with the atlas on the back faces
            // that gap showed whatever texture sat on the far side of the
            // collar: a bright crescent in each stick's well. Near black
            // reads as the dark interior a real pad shows there. Nothing
            // restores the body's materials, since it is never a hover,
            // press or flash target, so this holds.
            var MaterialInterior = new DiffuseMaterial(
                new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x12)));
            MaterialInterior.Freeze();
            foreach (var child in MainBody.Children)
                if (child is GeometryModel3D geo)
                    geo.BackMaterial = MaterialInterior;

            DrawAccentHighlights();
        }

        /// <summary>The mesh is real-world scale (MainBody width 160.0 mm,
        /// Sony's own figure). The shared camera is framed for DS4-class
        /// meshes (165.7 mm).</summary>
        public override double ModelScale => 165.7 / 160.0;
    }
}
