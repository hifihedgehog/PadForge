// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// DualShock 4: purchased hado CGTrader mesh, classified into the HC
// part names by spatial containment against the HC stand-in (the two
// meshes decompose differently, so shell matching cannot align them),
// sticks split at the fleet cap cut. Colorway atlas sets per folder.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// DualShock 4 controller model. Textured hado mesh with per-colorway
    /// atlas sets; the touchpad surface (Screen) doubles as the click
    /// target and finger-sphere host, as on DualSense.
    /// </summary>
    public class ControllerModelDS4 : ControllerModelBase
    {
        public static readonly string[] AppearanceIds = { "JetBlack", "MagmaRed" };
        public static readonly string[] AppearanceNames = { "Jet Black", "Magma Red" };

        private static string Validate(string appearance)
            => System.Array.IndexOf(AppearanceIds, appearance) >= 0 ? appearance : AppearanceIds[0];

        // DS4-specific mesh groups
        private readonly Model3DGroup LeftShoulderMiddle;
        private readonly Model3DGroup RightShoulderMiddle;
        private readonly Model3DGroup Screen;
        private readonly Model3DGroup MainBodyBack;
        private readonly Model3DGroup AuxPort;
        private readonly Model3DGroup Triangle;
        private readonly Model3DGroup DecalOverlay;

        public ControllerModelDS4(string appearance = "JetBlack")
            : base($"DS4.{Validate(appearance)}")
        {
            var MaterialBody = LoadTexturedMaterial("Body.png");
            var MaterialDecal = LoadTexturedMaterial("Decal.png");

            // ── Rotation points ─────────────────────────
            JoystickRotationPointCenterLeftMillimeter = new Vector3D(-25.5f, -5.086f, -21.582f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D(25.5f, -5.086f, -21.582f);
            JoystickMaxAngleDeg = 19.0f;

            // Same rule as the rest of the fleet: a third of the way up
            // the trigger, by the Xbox One model's fraction of the part
            // bounds, instead of at the top edge. The collision check is
            // inconclusive here -- the DS4's L2 nests under L1, so the
            // two meshes already interpenetrate 5.3 mm at rest and the
            // measure cannot separate hinge travel from that overlap.
            // The hinge was in the same wrong place as the others (0.81
            // of the z span), so it is placed by the same rule.
            ShoulderTriggerRotationPointCenterLeftMillimeter = new Vector3D(-38.061f, -0.34f, 18.59f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D(38.061f, -0.34f, 18.59f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft = new Vector3D(-48.868f, -13f, 29.62f);
            UpwardVisibilityRotationPointRight = new Vector3D(48.868f, -13f, 29.62f);

            // ── DS4-specific meshes ─────────────────────
            LeftShoulderMiddle = LoadModel("Shoulder-Left-Middle.obj");
            RightShoulderMiddle = LoadModel("Shoulder-Right-Middle.obj");
            Screen = LoadModel("Screen.obj");
            Touchpad = Screen;                  // click highlight + finger spheres
            ClickMap[Screen] = "TouchpadClick"; // hit target for mapping
            MainBodyBack = LoadModel("MainBodyBack.obj");
            AuxPort = LoadModel("Aux-Port.obj");
            Triangle = LoadModel("Triangle.obj");

            // Face-button symbols and d-pad arrows are DECAL art (their
            // UVs address the decal atlas, not the body atlas: giving
            // them MaterialBody skinned the buttons with whatever the
            // body atlas holds at those coordinates). They ride their
            // buttons so they press and highlight as one piece. Attached
            // after DrawAccentHighlights, below, with MaterialDecal.

            model3DGroup.Children.Add(LeftShoulderMiddle);
            model3DGroup.Children.Add(RightShoulderMiddle);
            model3DGroup.Children.Add(Screen);
            model3DGroup.Children.Add(MainBodyBack);
            model3DGroup.Children.Add(AuxPort);
            model3DGroup.Children.Add(Triangle);

            // ── Materials ───────────────────────────────
            foreach (var (target, _) in ButtonMap)
            {
                if (ButtonMap.TryGetValue(target, out var list))
                    foreach (var grp in list)
                    {
                        ApplyMaterial(grp, MaterialBody);
                        DefaultMaterials[grp] = MaterialBody;
                    }
            }
            foreach (Model3DGroup child in model3DGroup.Children)
            {
                if (DefaultMaterials.ContainsKey(child)) continue;
                ApplyMaterial(child, MaterialBody);
                DefaultMaterials[child] = MaterialBody;
            }

            DrawAccentHighlights();

            foreach (var (file, padSetting) in new[]
            {
                ("B1-Symbol.obj", "ButtonA"), ("B2-Symbol.obj", "ButtonB"),
                ("B3-Symbol.obj", "ButtonX"), ("B4-Symbol.obj", "ButtonY"),
                ("DPadUpArrow.obj", "DPadUp"), ("DPadDownArrow.obj", "DPadDown"),
                ("DPadLeftArrow.obj", "DPadLeft"), ("DPadRightArrow.obj", "DPadRight"),
            })
            {
                if (ButtonMap.TryGetValue(padSetting, out var symList) && symList.Count > 0)
                    AttachRiderDecal(symList[0], file, MaterialDecal);
            }

            AttachRiderDecal(LeftShoulderTrigger, "Decal-Shoulder-Left-Trigger.obj", MaterialDecal);
            AttachRiderDecal(RightShoulderTrigger, "Decal-Shoulder-Right-Trigger.obj", MaterialDecal);
            AttachRiderDecal(LeftThumbRing, "Decal-Joystick-Left-Ring.obj", MaterialDecal);
            AttachRiderDecal(RightThumbRing, "Decal-Joystick-Right-Ring.obj", MaterialDecal);

            DecalOverlay = TryLoadModel("Decal.obj");
            if (DecalOverlay != null)
            {
                ApplyMaterial(DecalOverlay, MaterialDecal);
                DefaultMaterials[DecalOverlay] = MaterialDecal;
                model3DGroup.Children.Add(DecalOverlay);
            }
        }

        /// <summary>Real-world scale mesh (161 mm body width).</summary>
        public override double ModelScale => 1.0;
    }
}
