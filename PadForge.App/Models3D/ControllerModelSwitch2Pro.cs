// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Switch 2 Pro Controller mesh: purchased hado CGTrader model
// (https://www.cgtrader.com/3d-models/electronics/computer/pro-controller-2),
// split into per-part OBJs from the single welded 53k-poly source via
// loose-part separation. Part positions verified against the physical
// Switch 2 Pro layout; palette sampled from the model's base-color texture.

using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// Nintendo Switch 2 Pro Controller model. Serves every Nintendo slot
    /// (both the switch-pro and switch2-pro profile families, the same
    /// arrangement as Xbox Series profiles riding the Xbox One mesh).
    /// B1–B4 mesh files are assigned by NINTENDO LABEL, not position:
    /// the raw→preview bridge maps wire button 1 (physical A, right
    /// position) to "ButtonA", so B1.obj IS the right-position button.
    /// </summary>
    public class ControllerModelSwitch2Pro : ControllerModelBase
    {
        // Switch-2-specific mesh groups
        private readonly Model3DGroup Capture;
        private readonly Model3DGroup CButton;
        private readonly Model3DGroup GL, GR;
        private readonly Model3DGroup LED1, LED2, LED3, LED4;

        public ControllerModelSwitch2Pro() : base("Switch2Pro")
        {
            // ── Colors (sampled from the purchased BC texture: body grey
            //    #5C5D60 dominates 82 % of texels; darker tones derived
            //    from it for pressable surfaces so they read as distinct
            //    under flat WPF shading) ──────────────────
            var ColorBody      = (Color)ColorConverter.ConvertFromString("#5C5D60");
            var ColorButton    = (Color)ColorConverter.ConvertFromString("#46474A");
            var ColorStick     = (Color)ColorConverter.ConvertFromString("#3A3B3D");
            var ColorLEDOff    = (Color)ColorConverter.ConvertFromString("#2E2F31");

            var MaterialBody   = new DiffuseMaterial(new SolidColorBrush(ColorBody));
            var MaterialButton = new DiffuseMaterial(new SolidColorBrush(ColorButton));
            var MaterialStick  = new DiffuseMaterial(new SolidColorBrush(ColorStick));
            var MaterialLEDOff = new DiffuseMaterial(new SolidColorBrush(ColorLEDOff));

            // Player-1 LED uses the app accent brush, like DualSense.
            Brush accentBrush;
            try { accentBrush = (Brush)System.Windows.Application.Current.Resources["AccentButtonBackground"]; }
            catch { accentBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)); }
            var MaterialLEDOn = new DiffuseMaterial(accentBrush);

            // ── Rotation points (derived from the exported part bounds:
            //    stick caps center (−39.6, −21.2, 19.7) / (17.7, −19.7,
            //    −1.2), pivot set at the cap base plane inside the well;
            //    triggers c=(±42.8, 7.1, 44.4), hinge at their top edge) ──
            JoystickRotationPointCenterLeftMillimeter  = new Vector3D(-39.6f, -10.0f, 19.7f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D( 17.7f, -10.0f, -1.2f);
            JoystickMaxAngleDeg = 14.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter  = new Vector3D(-42.8f, 0.0f, 50.0f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D( 42.8f, 0.0f, 50.0f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft  = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft  = new Vector3D(-45.0f, -15.0f, 48.0f);
            UpwardVisibilityRotationPointRight = new Vector3D( 45.0f, -15.0f, 48.0f);

            // ── Load Switch-2-specific meshes ───────────
            // Capture is a real input on the raw surface (wire button 13,
            // preview name "ButtonShare", the same grammar slot Xbox
            // Series Share rides), so it registers as a button.
            Capture = LoadModel("Capture.obj");
            RegisterButton("ButtonShare", Capture);
            model3DGroup.Children.Add(Capture);

            // C button, GL, and GR exist on the physical Switch 2 Pro but
            // have no rows on the virtual Switch Pro raw surface (wire
            // order ends at button 13), so they render as inert cosmetic
            // meshes: no click-to-record, no flash registration.
            CButton = LoadModel("CButton.obj");
            GL      = LoadModel("GL.obj");
            GR      = LoadModel("GR.obj");
            model3DGroup.Children.Add(CButton);
            model3DGroup.Children.Add(GL);
            model3DGroup.Children.Add(GR);

            LED1 = LoadModel("LED1.obj");
            LED2 = LoadModel("LED2.obj");
            LED3 = LoadModel("LED3.obj");
            LED4 = LoadModel("LED4.obj");
            model3DGroup.Children.Add(LED1);
            model3DGroup.Children.Add(LED2);
            model3DGroup.Children.Add(LED3);
            model3DGroup.Children.Add(LED4);

            // ── Per-button materials ────────────────────
            // Stick caps darker than the flat buttons; everything
            // pressable darker than the body shell.
            foreach (var (target, _) in ButtonMap)
            {
                Material mat = (target == "LeftThumbButton" || target == "RightThumbButton")
                    ? MaterialStick : MaterialButton;
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

                if (child == LED1)
                {
                    SetMaterial(child, MaterialLEDOn);
                    DefaultMaterials[child] = MaterialLEDOn;
                    continue;
                }

                if (child == LED2 || child == LED3 || child == LED4)
                {
                    SetMaterial(child, MaterialLEDOff);
                    DefaultMaterials[child] = MaterialLEDOff;
                    continue;
                }

                if (child == LeftThumbRing || child == RightThumbRing
                    || child == CButton
                    || child == LeftShoulderTrigger || child == RightShoulderTrigger)
                {
                    SetMaterial(child, MaterialStick);
                    DefaultMaterials[child] = MaterialStick;
                    continue;
                }

                // Body shell, GL/GR (part of the back shell visually),
                // motors (occluded inside the grips).
                SetMaterial(child, MaterialBody);
                DefaultMaterials[child] = MaterialBody;
            }

            DrawAccentHighlights();
        }

        /// <summary>The split mesh is real-world scale (MainBody width
        /// 148.0 mm). The shared viewport camera is sized for DS4-class
        /// meshes (165.7 mm), so scale up to match the framing.</summary>
        public override double ModelScale => 165.7 / 148.0;

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
