// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Modifications for PadForge: PadSetting-based button mapping,
// embedded resource loading, click-to-record hit testing.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// DualSense controller model. Adapted from Handheld Companion's
    /// ModelDualSense. Material palette and rotation parameters match the
    /// upstream HC model exactly so the visual identity carries over.
    /// </summary>
    public class ControllerModelDualSense : ControllerModelBase
    {
        // DualSense-specific mesh groups
        private readonly Model3DGroup AudioJack;
        private readonly Model3DGroup Charger;
        private readonly Model3DGroup LED1, LED2, LED3;
        private readonly Model3DGroup MainBodyBack;
        private readonly Model3DGroup MainBodyFront;
        private readonly Model3DGroup USBPort;
        private readonly Model3DGroup ShareSymbol;
        private readonly Model3DGroup MenuSymbol;
        private readonly Model3DGroup B1Button, B2Button, B3Button, B4Button;
        private readonly Model3DGroup B1ButtonSymbol, B2ButtonSymbol, B3ButtonSymbol, B4ButtonSymbol;
        private readonly Model3DGroup DPadDownArrow, DPadUpArrow, DPadLeftArrow, DPadRightArrow;
        private readonly Model3DGroup DPadDownCover, DPadUpCover, DPadLeftCover, DPadRightCover;

        public ControllerModelDualSense() : base("DualSense")
        {
            // ── Colors (HC palette) ─────────────────────
            var ColorPlasticBlack = (Color)ColorConverter.ConvertFromString("#21242E");
            var ColorPlasticGrey  = (Color)ColorConverter.ConvertFromString("#7C7F8C");
            var ColorPlasticWhite = (Color)ColorConverter.ConvertFromString("#DADFE8");
            var ColorMetal        = (Color)ColorConverter.ConvertFromString("#5A4928");
            var ColorLEDOff       = (Color)ColorConverter.ConvertFromString("#35383E");

            var MaterialPlasticBlack = new DiffuseMaterial(new SolidColorBrush(ColorPlasticBlack));
            var MaterialPlasticGrey  = new DiffuseMaterial(new SolidColorBrush(ColorPlasticGrey));
            var MaterialPlasticWhite = new DiffuseMaterial(new SolidColorBrush(ColorPlasticWhite));
            var MaterialMetal        = new DiffuseMaterial(new SolidColorBrush(ColorMetal));
            var MaterialLEDOff       = new DiffuseMaterial(new SolidColorBrush(ColorLEDOff));

            // Translucent face-button caps (HC sets alpha=100 over white).
            var ColorPlasticTransparent = ColorPlasticWhite;
            ColorPlasticTransparent.A = 100;
            var MaterialPlasticTransparent = new DiffuseMaterial(new SolidColorBrush(ColorPlasticTransparent));

            // Player-indicator LEDs use the app accent brush.
            Brush accentBrush;
            try { accentBrush = (Brush)System.Windows.Application.Current.Resources["AccentButtonBackground"]; }
            catch { accentBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)); }
            var MaterialHighlight = new DiffuseMaterial(accentBrush);

            // ── Rotation points (from HC) ───────────────
            JoystickRotationPointCenterLeftMillimeter  = new Vector3D(-30.339f, -10.7f, -1.507f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D( 30.339f, -10.7f, -1.507f);
            JoystickMaxAngleDeg = 14.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter  = new Vector3D(-65.4f, -0.64f, 45.8f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D( 65.4f, -0.64f, 45.8f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft  = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft  = new Vector3D(-60.83f, -26.2f, 60.9f);
            UpwardVisibilityRotationPointRight = new Vector3D( 60.83f, -26.2f, 60.9f);

            // ── Load DualSense-specific meshes ──────────
            AudioJack     = LoadModel("AudioJack.obj");
            Charger       = LoadModel("Charger.obj");
            LED1          = LoadModel("LED1.obj");
            LED2          = LoadModel("LED2.obj");
            LED3          = LoadModel("LED3.obj");
            MainBodyBack  = LoadModel("MainBodyBack.obj");
            MainBodyFront = LoadModel("MainBodyFront.obj");
            USBPort       = LoadModel("USBPort.obj");
            ShareSymbol   = LoadModel("ShareSymbol.obj");
            MenuSymbol    = LoadModel("MenuSymbol.obj");

            DPadDownArrow  = LoadModel("DPadDownArrow.obj");
            DPadUpArrow    = LoadModel("DPadUpArrow.obj");
            DPadLeftArrow  = LoadModel("DPadLeftArrow.obj");
            DPadRightArrow = LoadModel("DPadRightArrow.obj");
            DPadDownCover  = LoadModel("DPadDownCover.obj");
            DPadUpCover    = LoadModel("DPadUpCover.obj");
            DPadLeftCover  = LoadModel("DPadLeftCover.obj");
            DPadRightCover = LoadModel("DPadRightCover.obj");

            B1Button       = LoadModel("B1Button.obj");
            B2Button       = LoadModel("B2Button.obj");
            B3Button       = LoadModel("B3Button.obj");
            B4Button       = LoadModel("B4Button.obj");
            B1ButtonSymbol = LoadModel("B1ButtonSymbol.obj");
            B2ButtonSymbol = LoadModel("B2ButtonSymbol.obj");
            B3ButtonSymbol = LoadModel("B3ButtonSymbol.obj");
            B4ButtonSymbol = LoadModel("B4ButtonSymbol.obj");

            // HC's MainBody.obj is one file with multiple connected
            // components — both grip handles, the central front-face area,
            // dpad pieces, and face-button pieces are all joined together.
            // The central front-face component IS the touchpad surface
            // (Comp02 from the connectivity analysis: 2720 faces, X∈[-42,38]
            // Y∈[-30.5,-18.1] Z∈[19,63]). tools/overlay_positions.py runs
            // a one-time split that writes MainBody.obj minus that
            // component AND a separate Touchpad.obj with just the
            // touchpad surface — load both here so MainBody renders the
            // grip + button areas, Touchpad is its own click-mappable +
            // highlight-able mesh.
            Touchpad = LoadModel("Touchpad.obj");
            ClickMap[Touchpad] = "TouchpadClick";
            model3DGroup.Children.Add(Touchpad);

            // ── Add to scene graph (HC ordering) ────────
            model3DGroup.Children.Add(AudioJack);
            model3DGroup.Children.Add(Charger);
            model3DGroup.Children.Add(LED1);
            model3DGroup.Children.Add(LED2);
            model3DGroup.Children.Add(LED3);
            model3DGroup.Children.Add(MainBodyBack);
            model3DGroup.Children.Add(MainBodyFront);
            model3DGroup.Children.Add(USBPort);
            model3DGroup.Children.Add(ShareSymbol);
            model3DGroup.Children.Add(MenuSymbol);

            model3DGroup.Children.Add(DPadDownArrow);
            model3DGroup.Children.Add(DPadUpArrow);
            model3DGroup.Children.Add(DPadLeftArrow);
            model3DGroup.Children.Add(DPadRightArrow);
            model3DGroup.Children.Add(DPadDownCover);
            model3DGroup.Children.Add(DPadUpCover);
            model3DGroup.Children.Add(DPadLeftCover);
            model3DGroup.Children.Add(DPadRightCover);

            model3DGroup.Children.Add(B1ButtonSymbol);
            model3DGroup.Children.Add(B2ButtonSymbol);
            model3DGroup.Children.Add(B3ButtonSymbol);
            model3DGroup.Children.Add(B4ButtonSymbol);
            model3DGroup.Children.Add(B1Button);
            model3DGroup.Children.Add(B2Button);
            model3DGroup.Children.Add(B3Button);
            model3DGroup.Children.Add(B4Button);

            // ── Per-button material (matches HC's switch) ─
            // Inputs that HC paints black: shoulders, triggers, stick-clicks,
            // Special. Everything else: white.
            foreach (var (target, _) in ButtonMap)
            {
                Material mat = (target == "LeftShoulder" || target == "LeftTrigger"
                             || target == "RightShoulder" || target == "RightTrigger"
                             || target == "LeftThumbButton" || target == "RightThumbButton"
                             || target == "ButtonGuide")
                    ? MaterialPlasticBlack : MaterialPlasticWhite;
                if (ButtonMap.TryGetValue(target, out var list))
                    foreach (var grp in list)
                    {
                        SetMaterial(grp, mat);
                        DefaultMaterials[grp] = mat;
                    }
            }

            // ── Generic / specific materials ────────────
            foreach (Model3DGroup child in model3DGroup.Children)
            {
                if (DefaultMaterials.ContainsKey(child)) continue;

                // Black body parts (front shell, accent rings, jack/USB).
                if (child == MainBodyFront || child == AudioJack || child == USBPort
                    || child == LeftThumbRing || child == RightThumbRing
                    || child == LeftShoulderTrigger || child == RightShoulderTrigger)
                {
                    SetMaterial(child, MaterialPlasticBlack);
                    DefaultMaterials[child] = MaterialPlasticBlack;
                    continue;
                }

                // Grey symbols (Share / Menu / DPad arrows / face-button glyphs).
                if (child == ShareSymbol || child == MenuSymbol
                    || child == DPadUpArrow || child == DPadRightArrow
                    || child == DPadDownArrow || child == DPadLeftArrow
                    || child == B1ButtonSymbol || child == B2ButtonSymbol
                    || child == B3ButtonSymbol || child == B4ButtonSymbol)
                {
                    SetMaterial(child, MaterialPlasticGrey);
                    DefaultMaterials[child] = MaterialPlasticGrey;
                    continue;
                }

                // Lit player-indicator LEDs.
                if (child == LED1 || child == LED2)
                {
                    SetMaterial(child, MaterialHighlight);
                    DefaultMaterials[child] = MaterialHighlight;
                    continue;
                }

                if (child == Charger)
                {
                    SetMaterial(child, MaterialMetal);
                    DefaultMaterials[child] = MaterialMetal;
                    continue;
                }

                if (child == LED3)
                {
                    SetMaterial(child, MaterialLEDOff);
                    DefaultMaterials[child] = MaterialLEDOff;
                    continue;
                }

                // Translucent face-button caps + DPad covers.
                if (child == DPadDownCover || child == DPadUpCover
                    || child == DPadLeftCover || child == DPadRightCover
                    || child == B1Button || child == B2Button
                    || child == B3Button || child == B4Button)
                {
                    SetMaterial(child, MaterialPlasticTransparent);
                    DefaultMaterials[child] = MaterialPlasticTransparent;
                    continue;
                }

                // Default: white plastic.
                SetMaterial(child, MaterialPlasticWhite);
                DefaultMaterials[child] = MaterialPlasticWhite;
            }

            DrawAccentHighlights();
        }

        /// <summary>HC modeled the DualSense larger than the DS4 in raw
        /// mesh units (MainBody width 199.9 mm vs 165.7 mm, ~21 % bigger)
        /// even though the real-world controllers are nearly identical
        /// in width. The shared viewport camera is sized for DS4-class
        /// meshes, so we ask the host view to apply this uniform scale
        /// at the ModelVisual3D level — that scales BOTH the controller
        /// mesh AND the sibling finger-sphere visuals together so stick
        /// highlights and touchpad finger dots stay glued to the right
        /// surface.</summary>
        public override double ModelScale => 165.7 / 199.9;

        // The DualSense Touchpad mesh (Comp02 from MainBody) extends well
        // beyond the actual touch-sensitive surface: bounds are roughly
        // X∈[-42, 38] (80 mm) and Z∈[19, 63] (44 mm), versus the real touch
        // area of ~52 × 32 mm. Crop the finger-positioning region down so the
        // sphere maps to where a real DualSense finger lands instead of
        // sliding past the touchpad's visual edges.
        public override double TouchpadXInsetFrac => 0.175;       // (80 − 52) / 2 / 80
        public override double TouchpadZTopInsetFrac => 0.10;     // small bezel above active area
        public override double TouchpadZBottomInsetFrac => 0.02;  // let finger reach close to mesh bottom; previous 0.12 stopped well above the visual edge

        private static void SetMaterial(Model3DGroup group, Material material)
        {
            if (group.Children.Count > 0 && group.Children[0] is GeometryModel3D geo)
            {
                geo.Material = material;
                geo.BackMaterial = material;
            }
        }
    }
}
