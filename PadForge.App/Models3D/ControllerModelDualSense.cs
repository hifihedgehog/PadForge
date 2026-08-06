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
        public static readonly string[] AppearanceIds =
        {
            "White", "Midnight", "CosmicRed", "GrayCamo", "NovaPink",
            "DeepEarthCobalt", "DeepEarthSterling", "DeepEarthVolcanic",
            "FFXVI", "SpiderMan2",
        };
        public static readonly string[] AppearanceNames =
        {
            "White", "Midnight Black", "Cosmic Red", "Gray Camouflage", "Nova Pink",
            "Deep Earth Cobalt Blue", "Deep Earth Sterling Silver", "Deep Earth Volcanic Red",
            "Final Fantasy XVI", "Spider-Man 2",
        };

        private readonly Model3DGroup DecalOverlay;
        private readonly Model3DGroup TransparentTrim;

        private static string Validate(string appearance)
            => System.Array.IndexOf(AppearanceIds, appearance) >= 0 ? appearance : AppearanceIds[0];

        public ControllerModelDualSense(string appearance = "White")
            : this(Validate(appearance), "DualSense") { }

        /// <summary>Family-scoped constructor. The DualSense Edge is
        /// its OWN family (its profiles must always get the Edge mesh,
        /// never a plain DualSense), and reuses this body wholesale.</summary>
        protected ControllerModelDualSense(string appearance, string family)
            : base($"{family}.{appearance}")
        {
            var MaterialBody = LoadTexturedMaterial("Body.png");
            // The Transparent part is the clear plastic: the face-button
            // domes over the glyph plates, the lightbar, and the mic bar.
            // White ships a dedicated translucent atlas (alpha from the
            // source opacity map); a colorway whose trim merged into the
            // body mesh samples the body atlas at 30% opacity instead.
            // Clear plastic needs a highlight to read as clear plastic:
            // flat diffuse alone left the d-pad and face-button domes
            // barely there. Keep the ungloss'd material for the highlight
            // fallback below, which reads a DiffuseMaterial brush.
            var MaterialTransparentFlat = TryLoadTexturedMaterial("Transparent.png")
                ?? LoadTexturedMaterial("Body.png", 0.30);
            var MaterialTransparent = AddGloss(MaterialTransparentFlat, 0.60, 40.0);
            var MaterialDecal = LoadTexturedMaterial("Decal.png");

            // ── Rotation points (from the exported part bounds: stick
            //    caps c=(±25.7, −25.0, −0.1); triggers c=(±49.4, 8.5,
            //    43.2), hinge a third of the way up them) ─────────
            JoystickRotationPointCenterLeftMillimeter  = new Vector3D(-25.7f, -15.0f, -0.1f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D( 25.7f, -15.0f, -0.1f);
            JoystickMaxAngleDeg = 14.0f;

            // At the top edge the paddle drove 53 of 400 sampled
            // vertices 2.11 mm inside the bumper at full pull; here it
            // clears by 0.56 mm. Placed by the Xbox One model's own
            // fraction of the trigger bounds -- that model has always
            // deflected correctly. The Edge overrides this: its trigger
            // mesh sits higher.
            ShoulderTriggerRotationPointCenterLeftMillimeter  = new Vector3D(-49.4f, 4.99f, 41.0f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D( 49.4f, 4.99f, 41.0f);
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

            // ── DualSense Edge extras ───────────────────
            // The Edge's back buttons and Fn buttons are their own
            // shells, re-filed out of MainBody (back pair) and the
            // stick housings (Fn pair). Only the Edge asset folder
            // carries these files, so TryLoadModel gates them off the
            // plain colorways, the same way the stick housings gate
            // themselves. Targets are the PadSetting field names.
            foreach (var (file, target) in new[]
            {
                ("LeftBackButton.obj", "LeftPaddle"),
                ("RightBackButton.obj", "RightPaddle"),
                ("LeftFnButton.obj", "LeftFunction"),
                ("RightFnButton.obj", "RightFunction"),
            })
            {
                var extra = TryLoadModel(file);
                if (extra == null) continue;
                RegisterButton(target, extra);
                model3DGroup.Children.Add(extra);
            }

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

            // The Edge's sticks are removable modules with their OWN
            // atlas; every other colorway UVs them into the body atlas,
            // so a missing StickModule.png just falls back to the body.
            var MaterialStick = TryLoadTexturedMaterial("StickModule.png") ?? MaterialBody;
            foreach (var stickGroup in new[] { LeftThumb, RightThumb, LeftThumbRing, RightThumbRing })
            {
                if (stickGroup is not Model3DGroup sg) continue;
                ApplyMaterial(sg, MaterialStick);
                DefaultMaterials[sg] = MaterialStick;
            }
            // The Edge's stick modules include a FIXED housing that must
            // not swing with deflection, so it ships as its own static
            // part carrying the module atlas.
            foreach (var housing in new[] { "StickHousingL.obj", "StickHousingR.obj" })
            {
                var hg = TryLoadModel(housing);
                if (hg == null) continue;
                ApplyMaterial(hg, MaterialStick);
                DefaultMaterials[hg] = MaterialStick;
                model3DGroup.Children.Add(hg);
            }
            // The Fn buttons came out of the stick housings, so their
            // UVs live in the module atlas, not the body's. The button
            // material pass above gave them the body atlas; re-point.
            foreach (var fnTarget in new[] { "LeftFunction", "RightFunction" })
            {
                if (!ButtonMap.TryGetValue(fnTarget, out var fnList)) continue;
                foreach (var grp in fnList)
                {
                    ApplyMaterial(grp, MaterialStick);
                    DefaultMaterials[grp] = MaterialStick;
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
            // The L1/R1 lettering sat in the static Decal overlay, so a
            // bumper press lit the button while its label stayed grey --
            // L2/R2 glowed because the trigger path grades its riders.
            // Ride them on the bumpers, as the Series model does.
            if (ButtonMap.TryGetValue("LeftShoulder", out var lbList) && lbList.Count > 0)
                AttachRiderDecal(lbList[0], "Decal-L1.obj", MaterialDecal);
            if (ButtonMap.TryGetValue("RightShoulder", out var rbList) && rbList.Count > 0)
                AttachRiderDecal(rbList[0], "Decal-R1.obj", MaterialDecal);
            // The Edge's Fn labels ride their buttons for the same
            // reason: a static-overlay label stays grey while the
            // button under it lights.
            if (ButtonMap.TryGetValue("LeftFunction", out var lfnList) && lfnList.Count > 0)
                AttachRiderDecal(lfnList[0], "Decal-Fn-Left.obj", MaterialDecal);
            if (ButtonMap.TryGetValue("RightFunction", out var rfnList) && rfnList.Count > 0)
                AttachRiderDecal(rfnList[0], "Decal-Fn-Right.obj", MaterialDecal);

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
                ? HighlightMaterials[Touchpad] : MaterialTransparentFlat;
            model3DGroup.Children.Add(TransparentTrim);
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
