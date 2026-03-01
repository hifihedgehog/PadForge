// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Modifications for PadForge: PadSetting-based button mapping,
// embedded resource loading, click-to-record hit testing.

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>
    /// DualShock 4 controller model. Adapted from Handheld Companion's ModelDS4.
    /// </summary>
    public class ControllerModelDS4 : ControllerModelBase
    {
        // DS4-specific mesh groups
        private readonly Model3DGroup LeftShoulderMiddle;
        private readonly Model3DGroup RightShoulderMiddle;
        private readonly Model3DGroup Screen;
        private readonly Model3DGroup MainBodyBack;
        private readonly Model3DGroup AuxPort;
        private readonly Model3DGroup Triangle;
        private readonly Model3DGroup DPadDownArrow;
        private readonly Model3DGroup DPadUpArrow;
        private readonly Model3DGroup DPadLeftArrow;
        private readonly Model3DGroup DPadRightArrow;

        public ControllerModelDS4() : base("DS4")
        {
            // ── Colors ──────────────────────────────────
            var ColorPlasticBlack = (Color)ColorConverter.ConvertFromString("#38383A");
            var ColorPlasticWhite = (Color)ColorConverter.ConvertFromString("#E0E0E0");

            var MaterialPlasticBlack = new DiffuseMaterial(new SolidColorBrush(ColorPlasticBlack));
            var MaterialPlasticWhite = new DiffuseMaterial(new SolidColorBrush(ColorPlasticWhite));

            var MaterialPlasticTriangle = new DiffuseMaterial(
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66a0a4")));
            var MaterialPlasticCross = new DiffuseMaterial(
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#96b2d9")));
            var MaterialPlasticCircle = new DiffuseMaterial(
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d66673")));
            var MaterialPlasticSquare = new DiffuseMaterial(
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d7bee5")));

            // ── Rotation points ─────────────────────────
            JoystickRotationPointCenterLeftMillimeter = new Vector3D(-25.5f, -5.086f, -21.582f);
            JoystickRotationPointCenterRightMillimeter = new Vector3D(25.5f, -5.086f, -21.582f);
            JoystickMaxAngleDeg = 19.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter = new Vector3D(-38.061f, 3.09f, 26.842f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D(38.061f, 3.09f, 26.842f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft = new Vector3D(-48.868f, -13f, 29.62f);
            UpwardVisibilityRotationPointRight = new Vector3D(48.868f, -13f, 29.62f);

            // ── Load DS4-specific meshes ─────────────────
            LeftShoulderMiddle = LoadModel("Shoulder-Left-Middle.obj");
            RightShoulderMiddle = LoadModel("Shoulder-Right-Middle.obj");
            Screen = LoadModel("Screen.obj");
            MainBodyBack = LoadModel("MainBodyBack.obj");
            AuxPort = LoadModel("Aux-Port.obj");
            Triangle = LoadModel("Triangle.obj");
            DPadDownArrow = LoadModel("DPadDownArrow.obj");
            DPadUpArrow = LoadModel("DPadUpArrow.obj");
            DPadLeftArrow = LoadModel("DPadLeftArrow.obj");
            DPadRightArrow = LoadModel("DPadRightArrow.obj");

            // DS4 face button symbols (B1-Symbol.obj, etc.)
            var symbolMap = new (string file, string padSetting, Material symbolMat)[]
            {
                ("B1-Symbol.obj", "ButtonA", MaterialPlasticCross),
                ("B2-Symbol.obj", "ButtonB", MaterialPlasticCircle),
                ("B3-Symbol.obj", "ButtonX", MaterialPlasticSquare),
                ("B4-Symbol.obj", "ButtonY", MaterialPlasticTriangle),
            };

            foreach (var (file, padSetting, symbolMat) in symbolMap)
            {
                var symbol = TryLoadModel(file);
                if (symbol == null) continue;

                // Add symbol to the same ButtonMap entry so it highlights together
                if (ButtonMap.TryGetValue(padSetting, out var list))
                    list.Add(symbol);

                model3DGroup.Children.Add(symbol);
                SetMaterial(symbol, symbolMat);
                DefaultMaterials[symbol] = symbolMat;
            }

            model3DGroup.Children.Add(LeftShoulderMiddle);
            model3DGroup.Children.Add(RightShoulderMiddle);
            model3DGroup.Children.Add(Screen);
            model3DGroup.Children.Add(MainBodyBack);
            model3DGroup.Children.Add(AuxPort);
            model3DGroup.Children.Add(Triangle);
            model3DGroup.Children.Add(DPadDownArrow);
            model3DGroup.Children.Add(DPadUpArrow);
            model3DGroup.Children.Add(DPadLeftArrow);
            model3DGroup.Children.Add(DPadRightArrow);

            // ── Per-button colors ───────────────────────
            // DS4 base face buttons are black; symbols get their own colors (applied above)
            if (ButtonMap.TryGetValue("ButtonA", out var mapA))
                foreach (var m in mapA)
                    if (!DefaultMaterials.ContainsKey(m))
                    { DefaultMaterials[m] = MaterialPlasticBlack; SetMaterial(m, MaterialPlasticBlack); }
            if (ButtonMap.TryGetValue("ButtonB", out var mapB))
                foreach (var m in mapB)
                    if (!DefaultMaterials.ContainsKey(m))
                    { DefaultMaterials[m] = MaterialPlasticBlack; SetMaterial(m, MaterialPlasticBlack); }
            if (ButtonMap.TryGetValue("ButtonX", out var mapX))
                foreach (var m in mapX)
                    if (!DefaultMaterials.ContainsKey(m))
                    { DefaultMaterials[m] = MaterialPlasticBlack; SetMaterial(m, MaterialPlasticBlack); }
            if (ButtonMap.TryGetValue("ButtonY", out var mapY))
                foreach (var m in mapY)
                    if (!DefaultMaterials.ContainsKey(m))
                    { DefaultMaterials[m] = MaterialPlasticBlack; SetMaterial(m, MaterialPlasticBlack); }

            // ── Generic materials ───────────────────────
            foreach (Model3DGroup child in model3DGroup.Children)
            {
                if (DefaultMaterials.ContainsKey(child))
                    continue;

                // White body parts
                if (child == MainBody || child == LeftMotor || child == RightMotor
                    || child == Triangle)
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
