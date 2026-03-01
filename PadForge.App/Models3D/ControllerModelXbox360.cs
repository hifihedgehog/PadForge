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
    /// Xbox 360 controller model. Adapted from Handheld Companion's ModelXBOX360.
    /// </summary>
    public class ControllerModelXbox360 : ControllerModelBase
    {
        // Xbox 360–specific mesh groups
        private readonly Model3DGroup MainBodyCharger;
        private readonly Model3DGroup SpecialRing;
        private readonly Model3DGroup SpecialLED;
        private readonly Model3DGroup LeftShoulderBottom;
        private readonly Model3DGroup RightShoulderBottom;

        private readonly Model3DGroup B1Button;
        private readonly Model3DGroup B2Button;
        private readonly Model3DGroup B3Button;
        private readonly Model3DGroup B4Button;

        public ControllerModelXbox360() : base("XBOX360")
        {
            // ── Colors ──────────────────────────────────
            var ColorPlasticBlack = (Color)ColorConverter.ConvertFromString("#707477");
            var ColorPlasticWhite = (Color)ColorConverter.ConvertFromString("#D4D4D4");
            var ColorPlasticSilver = (Color)ColorConverter.ConvertFromString("#CEDAE1");

            var ColorPlasticYellow = (Color)ColorConverter.ConvertFromString("#faa51f");
            var ColorPlasticGreen = (Color)ColorConverter.ConvertFromString("#7cb63b");
            var ColorPlasticRed = (Color)ColorConverter.ConvertFromString("#ff5f4b");
            var ColorPlasticBlue = (Color)ColorConverter.ConvertFromString("#6ac4f6");

            var ColorPlasticYellowTransparent = ColorPlasticYellow;
            var ColorPlasticGreenTransparent = ColorPlasticGreen;
            var ColorPlasticRedTransparent = ColorPlasticRed;
            var ColorPlasticBlueTransparent = ColorPlasticBlue;

            byte TransparencyAmount = 150;
            ColorPlasticYellowTransparent.A = TransparencyAmount;
            ColorPlasticGreenTransparent.A = TransparencyAmount;
            ColorPlasticRedTransparent.A = TransparencyAmount;
            ColorPlasticBlueTransparent.A = TransparencyAmount;

            var MaterialPlasticBlack = new DiffuseMaterial(new SolidColorBrush(ColorPlasticBlack));
            var MaterialPlasticWhite = new DiffuseMaterial(new SolidColorBrush(ColorPlasticWhite));
            var MaterialPlasticSilver = new DiffuseMaterial(new SolidColorBrush(ColorPlasticSilver));

            var MaterialPlasticYellow = new DiffuseMaterial(new SolidColorBrush(ColorPlasticYellow));
            var MaterialPlasticGreen = new DiffuseMaterial(new SolidColorBrush(ColorPlasticGreen));
            var MaterialPlasticRed = new DiffuseMaterial(new SolidColorBrush(ColorPlasticRed));
            var MaterialPlasticBlue = new DiffuseMaterial(new SolidColorBrush(ColorPlasticBlue));

            var MaterialPlasticYellowTransparent = new DiffuseMaterial(new SolidColorBrush(ColorPlasticYellowTransparent));
            var MaterialPlasticGreenTransparent = new DiffuseMaterial(new SolidColorBrush(ColorPlasticGreenTransparent));
            var MaterialPlasticRedTransparent = new DiffuseMaterial(new SolidColorBrush(ColorPlasticRedTransparent));
            var MaterialPlasticBlueTransparent = new DiffuseMaterial(new SolidColorBrush(ColorPlasticBlueTransparent));

            // ── Rotation points ─────────────────────────
            JoystickRotationPointCenterLeftMillimeter = new Vector3D(-42.231f, -6.10f, 21.436f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D(21.013f, -6.1f, -3.559f);
            JoystickMaxAngleDeg = 19.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter = new Vector3D(-44.668f, 3.087f, 39.705);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D(44.668f, 3.087f, 39.705);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft = new Vector3D(-36.226f, -14.26f, 47.332f);
            UpwardVisibilityRotationPointRight = new Vector3D(36.226f, -14.26f, 47.332f);

            // ── Load Xbox 360–specific meshes ───────────
            MainBodyCharger = LoadModel("MainBody-Charger.obj");
            SpecialRing = LoadModel("SpecialRing.obj");
            SpecialLED = LoadModel("SpecialLED.obj");
            LeftShoulderBottom = LoadModel("LeftShoulderBottom.obj");
            RightShoulderBottom = LoadModel("RightShoulderBottom.obj");

            B1Button = LoadModel("B1Button.obj");
            B2Button = LoadModel("B2Button.obj");
            B3Button = LoadModel("B3Button.obj");
            B4Button = LoadModel("B4Button.obj");

            model3DGroup.Children.Add(MainBodyCharger);
            model3DGroup.Children.Add(SpecialRing);
            model3DGroup.Children.Add(SpecialLED);
            model3DGroup.Children.Add(LeftShoulderBottom);
            model3DGroup.Children.Add(RightShoulderBottom);
            model3DGroup.Children.Add(B1Button);
            model3DGroup.Children.Add(B2Button);
            model3DGroup.Children.Add(B3Button);
            model3DGroup.Children.Add(B4Button);

            // ── Per-button colors ───────────────────────
            // Face button colored accents (A=Green, B=Red, X=Blue, Y=Yellow)
            SetMaterial(B1Button, MaterialPlasticGreenTransparent);
            SetMaterial(B2Button, MaterialPlasticRedTransparent);
            SetMaterial(B3Button, MaterialPlasticBlueTransparent);
            SetMaterial(B4Button, MaterialPlasticYellowTransparent);
            SetMaterial(SpecialLED, MaterialPlasticGreenTransparent);

            // ButtonMap face button colors (used for highlight toggle)
            if (ButtonMap.TryGetValue("ButtonA", out var mapA))
                foreach (var m in mapA) { DefaultMaterials[m] = MaterialPlasticGreen; SetMaterial(m, MaterialPlasticGreen); }
            if (ButtonMap.TryGetValue("ButtonB", out var mapB))
                foreach (var m in mapB) { DefaultMaterials[m] = MaterialPlasticRed; SetMaterial(m, MaterialPlasticRed); }
            if (ButtonMap.TryGetValue("ButtonX", out var mapX))
                foreach (var m in mapX) { DefaultMaterials[m] = MaterialPlasticBlue; SetMaterial(m, MaterialPlasticBlue); }
            if (ButtonMap.TryGetValue("ButtonY", out var mapY))
                foreach (var m in mapY) { DefaultMaterials[m] = MaterialPlasticYellow; SetMaterial(m, MaterialPlasticYellow); }
            if (ButtonMap.TryGetValue("ButtonGuide", out var mapGuide))
                foreach (var m in mapGuide) { DefaultMaterials[m] = MaterialPlasticSilver; SetMaterial(m, MaterialPlasticSilver); }

            // ── Generic materials ───────────────────────
            foreach (Model3DGroup child in model3DGroup.Children)
            {
                if (DefaultMaterials.ContainsKey(child))
                    continue;

                // White body parts
                if (child == MainBody || child == LeftMotor || child == RightMotor
                    || child == LeftShoulderBottom || child == RightShoulderBottom)
                {
                    SetMaterial(child, MaterialPlasticWhite);
                    DefaultMaterials[child] = MaterialPlasticWhite;
                    continue;
                }

                // Default black
                SetMaterial(child, MaterialPlasticBlack);
                DefaultMaterials[child] = MaterialPlasticBlack;
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
