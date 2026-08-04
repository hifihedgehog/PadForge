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

        /// <summary>Stable family identity for model selection. Equals
        /// ModelName by default; appearance-variant models set ModelName
        /// to "{family}.{appearance}" (the embedded-resource folder) and
        /// keep the family here so EnsureModel's identity check doesn't
        /// rebuild every tick.</summary>
        public string ModelFamily;

        /// <summary>Uniform scale to apply at the host ModelVisual3D level
        /// (the parent of model3DGroup AND the sibling finger-sphere
        /// visuals) so the model and its overlay visuals scale together.
        /// Default 1.0; subclasses override when their mesh authoring scale
        /// doesn't match the shared camera framing (DualSense's HC mesh is
        /// ~21 % larger than DS4's, for example). Setting this on
        /// model3DGroup.Transform alone breaks finger-sphere positioning
        /// because the sphere visuals are siblings of model3DGroup, not
        /// children — they don't pick up the same transform unless it's
        /// applied at the ModelVisual3D level.</summary>
        public virtual double ModelScale => 1.0;

        public Model3DGroup MainBody;
        public Model3DGroup LeftThumb, LeftThumbRing;
        public Model3DGroup RightThumb, RightThumbRing;
        public Model3DGroup LeftShoulderTrigger, RightShoulderTrigger;
        public Model3DGroup LeftMotor, RightMotor;

        /// <summary>Touchpad surface (null on models without a touchpad —
        /// only the DS4 mesh exposes one in v3). Set by the concrete subclass
        /// in its constructor; the
        /// 3D preview swaps its material to the accent color when
        /// <see cref="ViewModels.PadViewModel.TouchpadClickPressed"/> is
        /// true, and floats finger spheres just above its surface.</summary>
        public Model3DGroup Touchpad;

        /// <summary>Per-model fractional insets that crop the Touchpad mesh
        /// bounds down to the actual touch-sensitive surface for finger-sphere
        /// positioning. Defaults match the DS4 Screen.obj. Subclasses override
        /// when their Touchpad mesh extends beyond the real touchable area
        /// (e.g. DualSense's Touchpad mesh includes the surrounding front-face
        /// surface and is wider + taller than the real touchpad).</summary>
        public virtual double TouchpadXInsetFrac => 0.03;
        public virtual double TouchpadZTopInsetFrac => 0.12;
        public virtual double TouchpadZBottomInsetFrac => 0.12;

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
            int dot = modelName.IndexOf('.');
            ModelFamily = dot > 0 ? modelName.Substring(0, dot) : modelName;

            // Load common geometry.
            MainBody = LoadModel("MainBody.obj");
            LeftThumbRing = LoadModel("Joystick-Left-Ring.obj");
            RightThumbRing = LoadModel("Joystick-Right-Ring.obj");
            LeftMotor = LoadModel("MotorLeft.obj");
            RightMotor = LoadModel("MotorRight.obj");
            LeftShoulderTrigger = LoadModel("Shoulder-Left-Trigger.obj");
            RightShoulderTrigger = LoadModel("Shoulder-Right-Trigger.obj");

            // Stick rings — quadrant-based X/Y detection handled in ControllerModelView.
            // Not in ClickMap; the view checks IsStickRing() and uses hit position.
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

                if (padSetting == "LeftThumbButton")
                    LeftThumb = group;
                if (padSetting == "RightThumbButton")
                    RightThumb = group;
            }

            // The stick-button highlight covers the WHOLE stick: the ring
            // (cap + knurl riders) joins the thumb-button group list so
            // press/hover/flash glow it with the click mesh (owner ruling:
            // the cap texture glows just like the rest of the stick). Not
            // via RegisterButton: the ring stays a quadrant/axis click
            // target, never a ButtonMap ClickMap entry.
            if (ButtonMap.TryGetValue("LeftThumbButton", out var ltList) && LeftThumbRing != null)
                ltList.Add(LeftThumbRing);
            if (ButtonMap.TryGetValue("RightThumbButton", out var rtList) && RightThumbRing != null)
                rtList.Add(RightThumbRing);

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
        /// Uses the app's accent brush from WPF UI theme resources.
        /// </summary>
        protected virtual void DrawAccentHighlights()
        {
            // Must stay a SOLID brush: GradientHighlight lerps its Color.
            // AccentButtonBackground became an ember gradient in #175, so the
            // highlight now derives from the pinned accent Color instead.
            Brush accentBrush;
            try
            {
                accentBrush = Application.Current.Resources["SystemAccentColorPrimary"] is Color c
                    ? new SolidColorBrush(c)
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C));
            }
            catch
            {
                accentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C));
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

        /// <summary>Loads an embedded texture by suffix (same digit-prefix
        /// mangling workaround as TryLoadModel) and wraps it in a frozen
        /// DiffuseMaterial. ViewportUnits MUST be Absolute for 3D meshes:
        /// the default RelativeToBoundingBox remaps the image onto each
        /// mesh's texcoord bounding box, so every part would render the
        /// whole atlas squeezed onto its own UV island. Decode from a
        /// MemoryStream that outlives BeginInit/EndInit. keepAlpha is for
        /// decal overlays; body atlases ship opaque. Falls back to flat
        /// grey if the resource is missing so the model still renders.</summary>
        protected Material LoadTexturedMaterial(string filename, double opacity = 1.0)
        {
            return TryLoadTexturedMaterial(filename, opacity)
                ?? new DiffuseMaterial(new SolidColorBrush(
                       (Color)ColorConverter.ConvertFromString("#5C5D60")));
        }

        /// <summary>As LoadTexturedMaterial, but returns null when the
        /// embedded resource does not exist (appearance folders may omit
        /// an atlas, e.g. a colorway whose trim merged into the body).</summary>
        protected Material TryLoadTexturedMaterial(string filename, double opacity = 1.0)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string suffix = $".{ModelName}.{filename}";
                foreach (var name in assembly.GetManifestResourceNames())
                {
                    if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream == null) break;
                    var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    var brush = new ImageBrush(bmp)
                    {
                        TileMode = TileMode.None,
                        Stretch = Stretch.Fill,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, 1, 1),
                        Opacity = opacity,
                    };
                    brush.Freeze();
                    var mat = new DiffuseMaterial(brush);
                    mat.Freeze();
                    return mat;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[{GetType().Name}] Texture load failed for {filename}: {ex.Message}");
            }
            return null;
        }

        /// <summary>Rider decal geometries appended into moving groups.
        /// The view's graded glow masks its accent overlay by the rider's
        /// own texture alpha for these (a solid accent layer would paint
        /// the whole invisible plate as a filled rectangle).</summary>
        public readonly System.Collections.Generic.HashSet<GeometryModel3D> RiderDecals = new();

        /// <summary>Riders whose art fully covers their host's face (the
        /// Xbox guide emblem). The view highlights these by tinting the
        /// rider's own texels accent while the host keeps its default
        /// material, so only the glyph art glows. Non-covering riders
        /// hide during highlight instead.</summary>
        public readonly System.Collections.Generic.HashSet<GeometryModel3D> CoveringRiderDecals = new();

        /// <summary>Loads a decal mesh and appends its geometry INTO the
        /// host group so it moves with the host (trigger labels, stick-cap
        /// knurl art). Call after the host's material pass; the rider keeps
        /// its own decal material. Missing file is a no-op so colorways
        /// without a given rider stay valid.</summary>
        protected void AttachRiderDecal(Model3DGroup host, string filename, Material material, bool covering = false)
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
                RiderDecals.Add(geo);
                if (covering)
                    CoveringRiderDecals.Add(geo);
            }
        }

        /// <summary>Applies a material to every GeometryModel3D in the
        /// group (front and back faces).</summary>
        protected static void ApplyMaterial(Model3DGroup group, Material material)
        {
            foreach (var child in group.Children)
                if (child is GeometryModel3D geo)
                {
                    geo.Material = material;
                    geo.BackMaterial = material;
                }
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
