// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// DualSense mesh: purchased hado CGTrader model, split into per-part
// OBJs from the source's welded main object (35 shells classified
// against the physical layout; the source ships real stick rings,
// individual d-pad buttons, the touchpad, and separate Decal and
// Transparent overlay meshes with their own atlases). Two colorway
// atlas sets are baked in: White and Midnight Black.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// DualSense controller model on the hado mesh. B1–B4 follow the
    /// positional convention (B1=Cross bottom, B2=Circle right,
    /// B3=Square left, B4=Triangle top). The Decal overlay mesh carries
    /// the glyphs and labels with alpha, so it is added to the scene
    /// last for WPF transparency ordering.
    /// </summary>
    public class ControllerModelDualSense : ControllerModelBase
    {
        /// <summary>Colorway atlas sets available for this mesh; each
        /// entry maps to embedded {name}_Body/_Transparent/_Decal.png.</summary>
        public static readonly string[] AppearanceIds = { "White", "Midnight" };
        public static readonly string[] AppearanceNames = { "White", "Midnight Black" };

        private readonly Model3DGroup DecalOverlay;
        private readonly Model3DGroup TransparentTrim;

        private static string Validate(string appearance)
            => System.Array.IndexOf(AppearanceIds, appearance) >= 0 ? appearance : AppearanceIds[0];

        public ControllerModelDualSense(string appearance = "White")
            : base($"DualSense.{Validate(appearance)}")
        {
            var MaterialBody = LoadTexturedMaterial("Body.png");
            // The Transparent part is the clear plastic: the face-button
            // domes over the glyph plates, the lightbar, and the mic bar.
            // White ships a dedicated translucent atlas (alpha from the
            // source opacity map); a colorway whose trim merged into the
            // body mesh samples the body atlas at 30% opacity instead.
            var MaterialTransparent = TryLoadTexturedMaterial("Transparent.png")
                ?? LoadTexturedMaterial("Body.png", 0.30);
            var MaterialDecal = LoadTexturedMaterial("Decal.png");

            // ── Rotation points (from the exported part bounds: stick
            //    caps c=(±25.7, −25.0, −0.1); triggers c=(±49.4, 8.5,
            //    43.2), hinge at their top edge) ─────────
            JoystickRotationPointCenterLeftMillimeter  = new Vector3D(-25.7f, -15.0f, -0.1f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D( 25.7f, -15.0f, -0.1f);
            JoystickMaxAngleDeg = 14.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter  = new Vector3D(-49.4f, 0.0f, 50.0f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D( 49.4f, 0.0f, 50.0f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft  = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft  = new Vector3D(-49.6f, -15.0f, 48.0f);
            UpwardVisibilityRotationPointRight = new Vector3D( 49.6f, -15.0f, 48.0f);

            // ── DualSense-specific meshes ───────────────
            Touchpad = LoadModel("Touchpad.obj");
            ClickMap[Touchpad] = "TouchpadClick";
            model3DGroup.Children.Add(Touchpad);

            // Added to the scene AFTER the decal overlay (see below):
            // the clear domes must draw after the glyph plates beneath
            // them, or their depth writes reject the later-drawn plates.
            TransparentTrim = LoadModel("Transparent.obj");

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

            // The ring group IS the stick cap head (XBOXONE reference:
            // ring = whole cap, stick = stem+base, zero overlap), so it
            // shares the body atlas and is invisible as a split at rest.
            foreach (Model3DGroup child in model3DGroup.Children)
            {
                if (DefaultMaterials.ContainsKey(child)) continue;
                Material mat = child == TransparentTrim ? MaterialTransparent : MaterialBody;
                ApplyMaterial(child, mat);
                DefaultMaterials[child] = mat;
            }

            DrawAccentHighlights();

            // Rider decals: the L2/R2 label faces are carved out of the
            // static overlay and appended INTO the trigger groups (after
            // the body material pass) so the labels rotate with a pulled
            // trigger instead of floating in place.
            AttachRiderDecal(LeftShoulderTrigger, "Decal-Shoulder-Left-Trigger.obj", MaterialDecal);
            AttachRiderDecal(RightShoulderTrigger, "Decal-Shoulder-Right-Trigger.obj", MaterialDecal);

            // The stick caps' knurl-ring artwork is decal too; it rides
            // the ring groups (the moving cap heads) so it deflects with
            // the stick instead of floating at the rest position.
            AttachRiderDecal(LeftThumbRing, "Decal-Joystick-Left-Ring.obj", MaterialDecal);
            AttachRiderDecal(RightThumbRing, "Decal-Joystick-Right-Ring.obj", MaterialDecal);

            // Static decal overlay last: its atlas alpha carries the rest
            // of the glyphs and labels, and WPF renders transparency in
            // scene order.
            DecalOverlay = LoadModel("Decal.obj");
            ApplyMaterial(DecalOverlay, MaterialDecal);
            DefaultMaterials[DecalOverlay] = MaterialDecal;
            model3DGroup.Children.Add(DecalOverlay);

            // Clear plastic last: lightbar, mic bar, and the button domes
            // over their glyph plates. Material applied here because the
            // generic loop above ran before this group joined the scene.
            ApplyMaterial(TransparentTrim, MaterialTransparent);
            DefaultMaterials[TransparentTrim] = MaterialTransparent;
            HighlightMaterials[TransparentTrim] = HighlightMaterials.ContainsKey(Touchpad)
                ? HighlightMaterials[Touchpad] : MaterialTransparent;
            model3DGroup.Children.Add(TransparentTrim);
        }

        private void AttachRiderDecal(Model3DGroup host, string filename, Material material)
        {
            var rider = TryLoadModel(filename);
            if (rider == null) return;
            var geos = new System.Collections.Generic.List<GeometryModel3D>();
            foreach (var child in rider.Children)
                if (child is GeometryModel3D geo)
                    geos.Add(geo);
            rider.Children.Clear();
            foreach (var geo in geos)
            {
                geo.Material = material;
                geo.BackMaterial = material;
                host.Children.Add(geo);
            }
        }

        /// <summary>The hado mesh is real-world scale (MainBody width
        /// 160.6 mm); the shared camera is framed for DS4-class meshes
        /// (165.7 mm).</summary>
        public override double ModelScale => 165.7 / 160.6;

        // Touchpad mesh is 64.5 × 35.0 mm; the real touch-active area is
        // ~52 × 32 mm centered slightly high.
        public override double TouchpadXInsetFrac => 0.097;      // (64.5 − 52) / 2 / 64.5
        public override double TouchpadZTopInsetFrac => 0.05;
        public override double TouchpadZBottomInsetFrac => 0.04;
    }
}
