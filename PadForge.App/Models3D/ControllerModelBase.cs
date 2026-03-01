// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Modifications for PadForge: PadSetting-based button mapping,
// embedded resource loading, click-to-record hit testing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace PadForge.Models3D
{
    /// <summary>
    /// Base class for 3D controller models. Each subclass represents a
    /// controller type (Xbox 360, DS4) with its own meshes, colors, and
    /// rotation points. Adapted from Handheld Companion's IModel.
    /// </summary>
    public abstract class ControllerModelBase : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Button / click mapping
        // ─────────────────────────────────────────────

        /// <summary>PadSetting property name → list of Model3DGroups for highlighting.</summary>
        public Dictionary<string, List<Model3DGroup>> ButtonMap = new();

        /// <summary>Model3DGroup → PadSetting name for hit-test click-to-record.</summary>
        public Dictionary<Model3DGroup, string> ClickMap = new();

        // ─────────────────────────────────────────────
        //  Materials
        // ─────────────────────────────────────────────

        public Dictionary<Model3DGroup, Material> DefaultMaterials = new();
        public Dictionary<Model3DGroup, Material> HighlightMaterials = new();

        // ─────────────────────────────────────────────
        //  Common geometry groups
        // ─────────────────────────────────────────────

        public Model3DGroup model3DGroup = new();
        public string ModelName;

        public Model3DGroup MainBody;
        public Model3DGroup LeftThumb, LeftThumbRing;
        public Model3DGroup RightThumb, RightThumbRing;
        public Model3DGroup LeftShoulderTrigger, RightShoulderTrigger;
        public Model3DGroup LeftMotor, RightMotor;

        // ─────────────────────────────────────────────
        //  Rotation parameters
        // ─────────────────────────────────────────────

        public Vector3D JoystickRotationPointCenterLeftMillimeter;
        public Vector3D JoystickRotationPointCenterRightMillimeter;
        public float JoystickMaxAngleDeg;

        public Vector3D ShoulderTriggerRotationPointCenterLeftMillimeter;
        public Vector3D ShoulderTriggerRotationPointCenterRightMillimeter;
        public float TriggerMaxAngleDeg;

        public Vector3D UpwardVisibilityRotationAxisLeft;
        public Vector3D UpwardVisibilityRotationAxisRight;
        public Vector3D UpwardVisibilityRotationPointLeft;
        public Vector3D UpwardVisibilityRotationPointRight;

        // ─────────────────────────────────────────────
        //  OBJ file → PadSetting mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps HC .obj filenames to PadSetting property names.
        /// HC uses ButtonFlags enum names as filenames; PadForge uses
        /// PadSetting property names for the recording system.
        /// </summary>
        protected static readonly Dictionary<string, string> ButtonFileMap = new()
        {
            { "B1.obj", "ButtonA" },
            { "B2.obj", "ButtonB" },
            { "B3.obj", "ButtonX" },
            { "B4.obj", "ButtonY" },
            { "L1.obj", "LeftShoulder" },
            { "R1.obj", "RightShoulder" },
            { "Back.obj", "ButtonBack" },
            { "Start.obj", "ButtonStart" },
            { "Special.obj", "ButtonGuide" },
            { "DPadUp.obj", "DPadUp" },
            { "DPadDown.obj", "DPadDown" },
            { "DPadLeft.obj", "DPadLeft" },
            { "DPadRight.obj", "DPadRight" },
            { "LeftStickClick.obj", "LeftThumbButton" },
            { "RightStickClick.obj", "RightThumbButton" },
        };

        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        protected ControllerModelBase(string modelName)
        {
            ModelName = modelName;

            // Load common geometry.
            MainBody = LoadModel("MainBody.obj");
            LeftThumbRing = LoadModel("Joystick-Left-Ring.obj");
            RightThumbRing = LoadModel("Joystick-Right-Ring.obj");
            LeftMotor = LoadModel("MotorLeft.obj");
            RightMotor = LoadModel("MotorRight.obj");
            LeftShoulderTrigger = LoadModel("Shoulder-Left-Trigger.obj");
            RightShoulderTrigger = LoadModel("Shoulder-Right-Trigger.obj");

            // Clickable stick rings and triggers.
            ClickMap[LeftThumbRing] = "LeftThumbAxisX";
            ClickMap[RightThumbRing] = "RightThumbAxisX";
            ClickMap[LeftShoulderTrigger] = "LeftTrigger";
            ClickMap[RightShoulderTrigger] = "RightTrigger";

            // Load button meshes.
            foreach (var (filename, padSetting) in ButtonFileMap)
            {
                var group = TryLoadModel(filename);
                if (group == null)
                    continue;

                RegisterButton(padSetting, group);
                model3DGroup.Children.Add(group);

                if (padSetting == "LeftThumbButton") LeftThumb = group;
                if (padSetting == "RightThumbButton") RightThumb = group;
            }

            // Add non-button parts to scene.
            model3DGroup.Children.Add(MainBody);
            model3DGroup.Children.Add(LeftThumbRing);
            model3DGroup.Children.Add(RightThumbRing);
            model3DGroup.Children.Add(LeftMotor);
            model3DGroup.Children.Add(RightMotor);
            model3DGroup.Children.Add(LeftShoulderTrigger);
            model3DGroup.Children.Add(RightShoulderTrigger);
        }

        // ─────────────────────────────────────────────
        //  Button registration
        // ─────────────────────────────────────────────

        protected void RegisterButton(string padSettingName, Model3DGroup group)
        {
            if (!ButtonMap.TryGetValue(padSettingName, out var list))
            {
                list = new List<Model3DGroup>();
                ButtonMap[padSettingName] = list;
            }
            list.Add(group);
            ClickMap[group] = padSettingName;
        }

        // ─────────────────────────────────────────────
        //  Highlight generation
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates accent-colored highlight materials for all children.
        /// Uses the app's accent brush from ModernWpfUI theme resources.
        /// </summary>
        protected virtual void DrawAccentHighlights()
        {
            Brush accentBrush;
            try
            {
                accentBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
            }
            catch
            {
                accentBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
            }

            var highlightMaterial = new DiffuseMaterial(accentBrush);
            foreach (Model3DGroup group in model3DGroup.Children)
                HighlightMaterials[group] = highlightMaterial;
        }

        // ─────────────────────────────────────────────
        //  Embedded resource loading
        // ─────────────────────────────────────────────

        /// <summary>
        /// Loads a .obj mesh from embedded resources. Searches by suffix
        /// (.{ModelName}.{filename}) to handle MSBuild digit-prefix mangling.
        /// </summary>
        protected Model3DGroup LoadModel(string filename)
        {
            var group = TryLoadModel(filename);
            if (group == null)
                throw new FileNotFoundException(
                    $"Embedded 3D model not found: {ModelName}/{filename}");
            return group;
        }

        protected Model3DGroup TryLoadModel(string filename)
        {
            var assembly = Assembly.GetExecutingAssembly();
            // MSBuild prefixes folder names starting with a digit (e.g. "3DModels" → "_3DModels")
            // but keeps hyphens and other characters as-is in resource names.
            // Search by suffix to avoid needing the exact prefix.
            string suffix = $".{ModelName}.{filename}";
            string resourceName = null;

            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = name;
                    break;
                }
            }

            if (resourceName == null)
                return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;

            var reader = new ObjReader();
            var model = reader.Read(stream);
            return model;
        }

        // ─────────────────────────────────────────────
        //  Dispose
        // ─────────────────────────────────────────────

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                ButtonMap?.Clear();
                ClickMap?.Clear();
                DefaultMaterials?.Clear();
                HighlightMaterials?.Clear();
                model3DGroup?.Children.Clear();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ControllerModelBase() => Dispose(false);
    }
}
