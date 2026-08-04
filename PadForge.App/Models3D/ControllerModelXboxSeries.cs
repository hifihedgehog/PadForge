// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Xbox Series X|S controller mesh: purchased hado CGTrader model,
// split per-part (33 shells classified, hybrid d-pad disc bisected
// into four wedges, sticks neck-split into cap-head ring groups and
// stem/base click groups). Colorway atlas sets: Carbon Black and
// Robot White, with more variants sharing this mesh.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// Xbox Series controller model. Replaces the Xbox One stand-in for
    /// Series profiles and carries a real Share button (RegisterButton
    /// "ButtonShare"). Decal overlay draws after the opaque parts and
    /// the transparent trim last, per the layering contract.
    /// </summary>
    public class ControllerModelXboxSeries : ControllerModelBase
    {
        public static readonly string[] AppearanceIds =
        {
            "Carbon", "Robot", "ElectricVolt", "DaystrikeCamo", "HaloInfinite",
            "Starfield", "StellarShift", "DeepPink", "Porsche75th",
            "VelocityGreen", "PulseRed", "ShockBlue",
        };
        public static readonly string[] AppearanceNames =
        {
            "Carbon Black", "Robot White", "Electric Volt", "Daystrike Camo", "Halo Infinite",
            "Starfield", "Stellar Shift", "Deep Pink", "Porsche 75th Anniversary",
            "Velocity Green", "Pulse Red", "Shock Blue",
        };

        private readonly Model3DGroup ShareButton;
        private readonly Model3DGroup DecalOverlay;
        private readonly Model3DGroup TransparentTrim;

        private static string Validate(string appearance)
            => System.Array.IndexOf(AppearanceIds, appearance) >= 0 ? appearance : AppearanceIds[0];

        public ControllerModelXboxSeries(string appearance = "Carbon")
            : base($"XboxSeries.{Validate(appearance)}")
        {
            var MaterialBody = LoadTexturedMaterial("Body.png");
            var MaterialTransparent = TryLoadTexturedMaterial("Transparent.png")
                ?? LoadTexturedMaterial("Body.png", 0.30);
            var MaterialDecal = LoadTexturedMaterial("Decal.png");

            // ── Rotation points (from part bounds: stick caps
            //    c=(−39.6/−30.3/21.4) and (20.0/−30.3/−3.0); triggers
            //    c=(±43.9, 1.1, 42.6), hinge at their top edge) ──
            JoystickRotationPointCenterLeftMillimeter  = new Vector3D(-39.6f, -18.0f, 21.4f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D( 20.0f, -18.0f, -3.0f);
            JoystickMaxAngleDeg = 14.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter  = new Vector3D(-43.9f, -5.0f, 50.0f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D( 43.9f, -5.0f, 50.0f);
            // Analog triggers with real travel; fitted so the tip stays
            // clear of the shell at full pull.
            TriggerMaxAngleDeg = 12.0f;

            UpwardVisibilityRotationAxisLeft  = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft  = new Vector3D(-37.4f, -15.0f, 48.0f);
            UpwardVisibilityRotationPointRight = new Vector3D( 37.4f, -15.0f, 48.0f);

            // ── Series-specific meshes ──────────────────
            ShareButton = LoadModel("Share.obj");
            RegisterButton("ButtonShare", ShareButton);
            model3DGroup.Children.Add(ShareButton);

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

            // Rider decals move with their hosts: the stick caps' knurl
            // rings ride the cap-head ring groups, the dotted grip panels
            // ride the triggers and bumpers they are printed on.
            AttachRiderDecal(LeftThumbRing, "Decal-Joystick-Left-Ring.obj", MaterialDecal);
            AttachRiderDecal(RightThumbRing, "Decal-Joystick-Right-Ring.obj", MaterialDecal);
            AttachRiderDecal(LeftShoulderTrigger, "Decal-Shoulder-Left-Trigger.obj", MaterialDecal);
            AttachRiderDecal(RightShoulderTrigger, "Decal-Shoulder-Right-Trigger.obj", MaterialDecal);
            if (ButtonMap.TryGetValue("LeftShoulder", out var lbList) && lbList.Count > 0)
                AttachRiderDecal(lbList[0], "Decal-L1.obj", MaterialDecal);
            if (ButtonMap.TryGetValue("RightShoulder", out var rbList) && rbList.Count > 0)
                AttachRiderDecal(rbList[0], "Decal-R1.obj", MaterialDecal);
            // The Xbox emblem covers nearly the whole guide button, so it
            // rides the guide group as a COVERING rider: highlight tints
            // the emblem's own art accent while the button stays default.
            if (ButtonMap.TryGetValue("ButtonGuide", out var guideList) && guideList.Count > 0)
                AttachRiderDecal(guideList[0], "Decal-Special.obj", MaterialDecal, covering: true);

            // Static decal overlay after the opaque parts (puffed 0.22 mm
            // at export): guide logo, View/Menu/Share glyphs, top-rail
            // marks. Then the clear ABXY domes last.
            DecalOverlay = LoadModel("Decal.obj");
            ApplyMaterial(DecalOverlay, MaterialDecal);
            DefaultMaterials[DecalOverlay] = MaterialDecal;
            model3DGroup.Children.Add(DecalOverlay);

            TransparentTrim = LoadModel("Transparent.obj");
            ApplyMaterial(TransparentTrim, MaterialTransparent);
            DefaultMaterials[TransparentTrim] = MaterialTransparent;
            model3DGroup.Children.Add(TransparentTrim);
        }

        /// <summary>Real-world scale mesh (155.3 mm body width); 1.0
        /// keeps the tall bumper fins inside the shared framing.</summary>
        public override double ModelScale => 1.0;
    }
}
