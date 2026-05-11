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
    /// Xbox One family controller model. Adapted from Handheld Companion's
    /// ModelXBOXOne. Used by PadForge for Xbox One, Xbox Elite, and
    /// Xbox Series profiles (HC has no Xbox Series mesh — Series profiles
    /// borrow this one).
    /// </summary>
    public class ControllerModelXboxOne : ControllerModelBase
    {
        // Xbox One-specific mesh groups
        private readonly Model3DGroup BackSymbol;
        private readonly Model3DGroup BatteryDoor;
        private readonly Model3DGroup BatteryDoorInner;
        private readonly Model3DGroup MainBodyBack;
        private readonly Model3DGroup MainBodySide;
        private readonly Model3DGroup MainBodyTop;
        private readonly Model3DGroup ShareButton;
        private readonly Model3DGroup ShareButtonSymbol;
        private readonly Model3DGroup SpecialOuter;
        private readonly Model3DGroup StartSymbol;
        private readonly Model3DGroup USBPortInner;
        private readonly Model3DGroup USBPortOuter;
        private readonly Model3DGroup B1Button, B1Interior, B1Interior2;
        private readonly Model3DGroup B2Button, B2Interior, B2Interior2;
        private readonly Model3DGroup B3Button, B3Interior, B3Interior2;
        private readonly Model3DGroup B4Button, B4Interior, B4Interior2;

        public ControllerModelXboxOne() : this(enableShare: false) { }

        /// <param name="enableShare">Wire the Share mesh into the
        /// click-to-record + highlight maps. True only for Xbox Series
        /// profiles — Xbox One / 360 profiles don't expose Share so the
        /// mesh stays inert (visible body geometry but no hover / click /
        /// accent-highlight behavior).</param>
        public ControllerModelXboxOne(bool enableShare) : base("XBOXONE")
        {
            // ── Colors (HC palette) ─────────────────────
            var ColorPlasticBlack  = (Color)ColorConverter.ConvertFromString("#26272C");
            var ColorPlasticWhite  = (Color)ColorConverter.ConvertFromString("#D8D7DC");
            var ColorPlasticYellow = (Color)ColorConverter.ConvertFromString("#E4D70E");
            var ColorPlasticGreen  = (Color)ColorConverter.ConvertFromString("#76BA58");
            var ColorPlasticRed    = (Color)ColorConverter.ConvertFromString("#FA3D45");
            var ColorPlasticBlue   = (Color)ColorConverter.ConvertFromString("#119AE5");

            var ColorPlasticTransparent = (Color)ColorConverter.ConvertFromString("#232323");
            ColorPlasticTransparent.A = 50;

            var MaterialPlasticBlack  = new DiffuseMaterial(new SolidColorBrush(ColorPlasticBlack));
            var MaterialPlasticWhite  = new DiffuseMaterial(new SolidColorBrush(ColorPlasticWhite));
            var MaterialPlasticYellow = new DiffuseMaterial(new SolidColorBrush(ColorPlasticYellow));
            var MaterialPlasticGreen  = new DiffuseMaterial(new SolidColorBrush(ColorPlasticGreen));
            var MaterialPlasticRed    = new DiffuseMaterial(new SolidColorBrush(ColorPlasticRed));
            var MaterialPlasticBlue   = new DiffuseMaterial(new SolidColorBrush(ColorPlasticBlue));
            var MaterialPlasticTransparent = new DiffuseMaterial(new SolidColorBrush(ColorPlasticTransparent));

            // ── Rotation points (from HC) ───────────────
            JoystickRotationPointCenterLeftMillimeter  = new Vector3D(-39.0f, -8.0f, 22.2f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D( 20.0f, -8.0f, -1.1f);
            JoystickMaxAngleDeg = 17.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter  = new Vector3D(-44.668f, 3.087f, 39.705f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D( 44.668f, 3.087f, 39.705f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft  = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft  = new Vector3D(-28.7f, -20.3f, 52.8f);
            UpwardVisibilityRotationPointRight = new Vector3D( 28.7f, -20.3f, 52.8f);

            // ── Load Xbox One-specific meshes ───────────
            BackSymbol        = LoadModel("BackSymbol.obj");
            BatteryDoor       = LoadModel("BatteryDoor.obj");
            BatteryDoorInner  = LoadModel("BatteryDoorInner.obj");
            SpecialOuter      = LoadModel("SpecialOuter.obj");
            MainBodyBack      = LoadModel("MainBodyBack.obj");
            MainBodyTop       = LoadModel("MainBodyTop.obj");
            MainBodySide      = LoadModel("MainBodySide.obj");
            ShareButton       = LoadModel("ShareButton.obj");
            ShareButtonSymbol = LoadModel("ShareButtonSymbol.obj");
            // Wire the Share mesh into click-to-record + accent-highlight
            // ONLY for Xbox Series profiles (enableShare=true). On Xbox
            // One / 360 profiles HM silently drops the Share bit and the
            // mapping UI doesn't surface it — leaving the mesh inert
            // (visible body geometry, no hover / click / highlight)
            // matches user expectation that the button does nothing on
            // those profiles.
            if (enableShare)
            {
                // Register only the button BODY for click + highlight.
                // The symbol mesh stays black (assigned via the
                // MaterialPlasticBlack pass below) so the lettering
                // is still readable when the face turns accent-blue
                // on press — matching how Start / Back symbols
                // remain visible against their highlighted faces.
                RegisterButton("ButtonShare", ShareButton);
            }
            StartSymbol       = LoadModel("StartSymbol.obj");
            USBPortInner      = LoadModel("USBPortInner.obj");
            USBPortOuter      = LoadModel("USBPortOuter.obj");

            B1Interior  = LoadModel("B1-Interior.obj");
            B1Interior2 = LoadModel("B1-Interior2.obj");
            B1Button    = LoadModel("B1-Button.obj");
            B2Interior  = LoadModel("B2-Interior.obj");
            B2Interior2 = LoadModel("B2-Interior2.obj");
            B2Button    = LoadModel("B2-Button.obj");
            B3Interior  = LoadModel("B3-Interior.obj");
            B3Interior2 = LoadModel("B3-Interior2.obj");
            B3Button    = LoadModel("B3-Button.obj");
            B4Interior  = LoadModel("B4-Interior.obj");
            B4Interior2 = LoadModel("B4-Interior2.obj");
            B4Button    = LoadModel("B4-Button.obj");

            // ── Add to scene graph ──────────────────────
            model3DGroup.Children.Add(BackSymbol);
            model3DGroup.Children.Add(BatteryDoor);
            model3DGroup.Children.Add(BatteryDoorInner);
            model3DGroup.Children.Add(SpecialOuter);
            model3DGroup.Children.Add(MainBodyBack);
            model3DGroup.Children.Add(MainBodyTop);
            model3DGroup.Children.Add(MainBodySide);
            model3DGroup.Children.Add(ShareButton);
            model3DGroup.Children.Add(ShareButtonSymbol);
            model3DGroup.Children.Add(StartSymbol);
            model3DGroup.Children.Add(USBPortInner);
            model3DGroup.Children.Add(USBPortOuter);

            model3DGroup.Children.Add(B1Interior);
            model3DGroup.Children.Add(B1Interior2);
            model3DGroup.Children.Add(B1Button);
            model3DGroup.Children.Add(B2Interior);
            model3DGroup.Children.Add(B2Interior2);
            model3DGroup.Children.Add(B2Button);
            model3DGroup.Children.Add(B3Interior);
            model3DGroup.Children.Add(B3Interior2);
            model3DGroup.Children.Add(B3Button);
            model3DGroup.Children.Add(B4Interior);
            model3DGroup.Children.Add(B4Interior2);
            model3DGroup.Children.Add(B4Button);

            // ── Per-button color (HC's Xbox face-button palette) ──
            // B1=A=green, B2=B=red, B3=X=blue, B4=Y=yellow.
            // ButtonBack/Start are white, everything else black.
            void Paint(string padSetting, Material mat)
            {
                if (!ButtonMap.TryGetValue(padSetting, out var list)) return;
                foreach (var grp in list)
                {
                    SetMaterial(grp, mat);
                    DefaultMaterials[grp] = mat;
                }
            }
            Paint("ButtonA", MaterialPlasticGreen);
            Paint("ButtonB", MaterialPlasticRed);
            Paint("ButtonX", MaterialPlasticBlue);
            Paint("ButtonY", MaterialPlasticYellow);
            Paint("ButtonBack", MaterialPlasticWhite);
            Paint("ButtonStart", MaterialPlasticWhite);
            // Black: shoulders, triggers, stick clicks, guide, dpad
            foreach (var t in new[] { "LeftShoulder", "RightShoulder",
                                       "LeftTrigger", "RightTrigger",
                                       "LeftThumbButton", "RightThumbButton",
                                       "ButtonGuide",
                                       "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
                Paint(t, MaterialPlasticBlack);

            // ── Generic / specific body materials ───────
            foreach (Model3DGroup child in model3DGroup.Children)
            {
                if (DefaultMaterials.ContainsKey(child)) continue;

                // Black body parts.
                if (child == USBPortOuter
                    || child == B1Interior || child == B1Interior2
                    || child == B2Interior || child == B2Interior2
                    || child == B3Interior || child == B3Interior2
                    || child == B4Interior || child == B4Interior2
                    || child == LeftThumbRing || child == RightThumbRing
                    || child == LeftShoulderTrigger || child == RightShoulderTrigger
                    || child == ShareButtonSymbol || child == StartSymbol || child == BackSymbol)
                {
                    SetMaterial(child, MaterialPlasticBlack);
                    DefaultMaterials[child] = MaterialPlasticBlack;
                    continue;
                }

                // Translucent face-button caps over the colored interior.
                if (child == B1Button || child == B2Button
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
