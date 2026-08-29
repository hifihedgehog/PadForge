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

        /// <summary>Surfaces whose hover resolves BY QUADRANT rather than as
        /// one target. The four names are up, down, left and right in model
        /// space (X across the face, +Z toward its top edge).
        ///
        /// <para>A stick's cap is the usual case: its quadrants are the four
        /// axis directions. The 2015 Steam Controller is the interesting one.
        /// SDL maps its LEFT trackpad to the D-pad and its RIGHT trackpad to
        /// the right thumbstick, and says so in the driver ("the left pad is
        /// normally mapped to D-Pad", "the right pad is normally mapped to
        /// right thumbstick", SDL_hidapi_steam.c 1655 and 1673, with RIGHTX
        /// and RIGHTY reading sRightPadX/Y at 1650). Valve molds a D-pad
        /// cross into that left pad and names the solid
        /// TrackPadCoverDirectional. So those two pads carry directions.</para>
        ///
        /// <para>A surface that is ALSO in <see cref="ClickMap"/> splits by
        /// radius: the outer half reads as a direction, the middle stays the
        /// click. A surface that is only here is directions edge to
        /// edge.</para></summary>
        public readonly Dictionary<Model3DGroup, string[]> QuadrantMap = new();

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

        /// <summary>The SECOND touch surface, on a pad that has two. Null on
        /// a one-pad model, where both fingers ride <see cref="Touchpad"/>.
        ///
        /// <para>Every Valve pad has two, and the split is the one the frame
        /// packers already use: finger 0 is the LEFT pad, finger 1 the
        /// right. A one-pad model keeps reporting two fingers on the one
        /// surface, which is what a DualSense does.</para></summary>
        public Model3DGroup TouchpadRight;

        /// <summary>A pad's touchable face: where it sits, which way it
        /// faces, and how far it runs across and up ITS OWN plane.
        ///
        /// <para>An axis-aligned bounding box cannot describe one. Every
        /// Valve pad is canted: the 2026's faces 15 degrees off the
        /// controller's own front and the 2015's 19, so a point placed on
        /// the box lands beside the pad rather than on it, and the box's
        /// corners reach past the pad's outline entirely.</para></summary>
        public readonly struct TouchSurface
        {
            public readonly Point3D Center;
            public readonly Vector3D Normal, AxisU, AxisV;
            public readonly double ExtentU, ExtentV;

            /// <summary>The pad's radius when it is ROUND, else zero.
            ///
            /// <para>The 2015 Steam Controller's pads are circles: their
            /// outlines fill 0.786 of their own bounding square against the
            /// 0.785 a circle gives, with a radius steady to a twentieth of
            /// a millimetre. The 2026's and the Deck's are rounded squares
            /// at 0.975 and 0.983.</para></summary>
            public readonly double Radius;

            public TouchSurface(Point3D center, Vector3D normal, Vector3D u, Vector3D v,
                double extentU, double extentV, double radius)
            {
                Center = center; Normal = normal; AxisU = u; AxisV = v;
                ExtentU = extentU; ExtentV = extentV; Radius = radius;
            }

            public bool IsEmpty => ExtentU <= 0 || ExtentV <= 0;

            /// <summary>The point at normalized (u, v), lifted off the face
            /// along its normal. u runs left to right and v top to bottom,
            /// the convention the touch reports use.
            ///
            /// <para>On a ROUND pad the offset is held inside the circle. A
            /// touch report's two axes span a square, and the corners of
            /// that square are off a round pad: a finger cannot be there.
            /// Real readings sit inside the circle and pass through
            /// untouched, so this only pins the impossible ones to the
            /// rim.</para></summary>
            public Point3D At(double u, double v, double lift)
            {
                double du = (u - 0.5) * ExtentU;
                double dv = (0.5 - v) * ExtentV;

                if (Radius > 0)
                {
                    double away = Math.Sqrt(du * du + dv * dv);
                    if (away > Radius && away > 1e-9)
                    {
                        double scale = Radius / away;
                        du *= scale;
                        dv *= scale;
                    }
                }

                return Center + AxisU * du + AxisV * dv + Normal * lift;
            }
        }

        private TouchSurface? _touchSurface0, _touchSurface1;

        /// <summary>The parts whose front faces make up each pad. One mesh
        /// on nearly every model; the 2015 Steam Controller overrides these
        /// because each of its pads is four direction quarters around a
        /// center disc, and the disc alone is 40% of the pad.</summary>
        public virtual Model3DGroup[] TouchParts0
            => Touchpad == null ? System.Array.Empty<Model3DGroup>() : new[] { Touchpad };
        public virtual Model3DGroup[] TouchParts1
            => (TouchpadRight ?? Touchpad) == null
                ? System.Array.Empty<Model3DGroup>() : new[] { TouchpadRight ?? Touchpad };

        public TouchSurface TouchpadSurface0
            => _touchSurface0 ??= MeasureTouchSurface(TouchParts0);
        public TouchSurface TouchpadSurface1
            => _touchSurface1 ??= MeasureTouchSurface(TouchParts1);

        /// <summary>Fits a pad's front face from its geometry.
        ///
        /// <para>Take the outward-facing triangles for a rough normal, keep
        /// the ones lying within 5 mm of the frontmost point along it, and
        /// re-fit. That 5 mm is what separates a face from its mounting: the
        /// 2026's pad mesh runs 38 mm deep and its front face is a 3 mm
        /// slab carrying 1775 of the 1800 outward-facing triangles, with the
        /// boss more than 20 mm behind. All three Valve faces come out flat
        /// to half a millimetre, so a plane is the whole story.</para>
        ///
        /// <para>The axes are built from the normal rather than fitted, so
        /// they cannot come out arbitrary: U is the normal crossed with the
        /// model's up, which points to the controller's right, and V is U
        /// crossed back, which points to its top.</para></summary>
        protected static TouchSurface MeasureTouchSurface(Model3DGroup[] parts)
        {
            if (parts == null || parts.Length == 0) return default;

            var pts = new System.Collections.Generic.List<Point3D>();
            var tris = new System.Collections.Generic.List<(Point3D A, Point3D B, Point3D C)>();
            foreach (var g in parts)
            {
                if (g == null) continue;
                foreach (var child in g.Children)
                {
                    if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D mesh)
                        continue;
                    var pos = mesh.Positions;
                    var idx = mesh.TriangleIndices;
                    for (int i = 0; i + 2 < idx.Count; i += 3)
                        tris.Add((pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]]));
                }
            }
            if (tris.Count == 0) return default;

            static Vector3D FaceNormal(in (Point3D A, Point3D B, Point3D C) t, out double area)
            {
                var n = Vector3D.CrossProduct(t.B - t.A, t.C - t.A);
                double len = n.Length;
                area = len / 2.0;
                if (len < 1e-12) return new Vector3D(0, 0, 0);
                n /= len;
                return n;
            }

            // Rough outward normal: -Y is out of the controller's face.
            var rough = new Vector3D(0, 0, 0);
            foreach (var tri in tris)
            {
                var n = FaceNormal(tri, out double area);
                if (n.Y < -0.5) rough += n * area;
            }
            if (rough.Length < 1e-9) return default;
            rough.Normalize();

            double front = double.MinValue;
            foreach (var tri in tris)
            {
                var n = FaceNormal(tri, out _);
                if (Vector3D.DotProduct(n, rough) <= 0.5) continue;
                var c = ((Vector3D)tri.A + (Vector3D)tri.B + (Vector3D)tri.C) / 3.0;
                front = Math.Max(front, Vector3D.DotProduct(c, rough));
            }
            if (front == double.MinValue) return default;

            var normal = new Vector3D(0, 0, 0);
            foreach (var tri in tris)
            {
                var n = FaceNormal(tri, out double area);
                if (Vector3D.DotProduct(n, rough) <= 0.5) continue;
                var c = ((Vector3D)tri.A + (Vector3D)tri.B + (Vector3D)tri.C) / 3.0;
                if (Vector3D.DotProduct(c, rough) < front - 5.0) continue;
                normal += n * area;
                pts.Add(tri.A); pts.Add(tri.B); pts.Add(tri.C);
            }
            if (pts.Count == 0 || normal.Length < 1e-9) return default;
            normal.Normalize();

            // A provisional in-plane basis, only to get the points into 2D.
            var baseU = Vector3D.CrossProduct(new Vector3D(0, 0, 1), normal);
            if (baseU.Length < 1e-9) return default;
            baseU.Normalize();
            var baseV = Vector3D.CrossProduct(normal, baseU);
            baseV.Normalize();

            var flat = new List<Point>(pts.Count);
            double n1 = double.MinValue;
            foreach (var p in pts)
            {
                var q = (Vector3D)p;
                flat.Add(new Point(Vector3D.DotProduct(q, baseU), Vector3D.DotProduct(q, baseV)));
                n1 = Math.Max(n1, Vector3D.DotProduct(q, normal));
            }

            // The pad's OWN rectangle, not one aligned to the controller.
            // The 2026's pads are rotated 10.2 degrees within their plane, so
            // an axis-aligned box around one is 18% larger than the pad and
            // its edge midpoints fall outside the outline, which is what put
            // the finger dot past the pad's edge. The minimum-area enclosing
            // rectangle recovers the pad's own edges; on a pad that is not
            // rotated, like the Deck's, it returns the axis-aligned one.
            if (!MinAreaRect(flat, out var dirU, out double halfU, out double halfV,
                             out double midU, out double midV))
                return default;

            // Round or not, measured the same way the rectangle was: the
            // hull's area against the rectangle's. A circle fills 0.785 of
            // its bounding square and a rounded square about 0.98, so the
            // two are never in doubt. The 2015 Steam Controller's pads come
            // out at 0.786.
            //
            // Computed HERE, in the rectangle's own frame and before the
            // axes below are swapped or flipped. Reading it afterwards mixes
            // frames, and mixing them read this pad's radius 11% long, which
            // is enough to push the finger dot off the rim.
            double radius = 0;
            var hull = ConvexHull(flat);
            if (hull.Count >= 3)
            {
                double hullArea = 0;
                for (int i = 0; i < hull.Count; i++)
                {
                    var a = hull[i];
                    var b2 = hull[(i + 1) % hull.Count];
                    hullArea += a.X * b2.Y - b2.X * a.Y;
                }
                hullArea = Math.Abs(hullArea) / 2;
                double rectArea = halfU * 2 * halfV * 2;
                if (rectArea > 0 && hullArea / rectArea < 0.85)
                {
                    double sum = 0;
                    foreach (var q in hull)
                    {
                        double du = q.X * dirU.X + q.Y * dirU.Y - midU;
                        double dv = q.X * -dirU.Y + q.Y * dirU.X - midV;
                        sum += Math.Sqrt(du * du + dv * dv);
                    }
                    radius = sum / hull.Count;
                }
            }

            var axisU = baseU * dirU.X + baseV * dirU.Y;
            var axisV = baseU * -dirU.Y + baseV * dirU.X;
            axisU.Normalize();
            axisV.Normalize();

            // U points to the controller's right and V to its top, whichever
            // way the fit came out, so a touch report's x = 0 is always the
            // pad's left edge.
            if (Math.Abs(axisU.X) < Math.Abs(axisV.X))
            {
                (axisU, axisV) = (axisV, axisU);
                (halfU, halfV) = (halfV, halfU);
                (midU, midV) = (midV, midU);
            }
            if (axisU.X < 0) { axisU = -axisU; midU = -midU; }
            if (axisV.Z < 0) { axisV = -axisV; midV = -midV; }

            var center = (Point3D)(axisU * midU + axisV * midV + normal * n1);

            return new TouchSurface(center, normal, axisU, axisV, halfU * 2, halfV * 2, radius);
        }

        /// <summary>The smallest rectangle enclosing a set of 2D points, by
        /// rotating calipers over the convex hull. A minimum-area rectangle
        /// always has a side flush with a hull edge, so trying each edge in
        /// turn finds it.</summary>
        private static bool MinAreaRect(List<Point> pts, out Vector dir,
            out double halfU, out double halfV, out double midU, out double midV)
        {
            dir = new Vector(1, 0);
            halfU = halfV = midU = midV = 0;

            var hull = ConvexHull(pts);
            if (hull.Count < 3) return false;

            double bestArea = double.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                var e = hull[(i + 1) % hull.Count] - hull[i];
                double len = e.Length;
                if (len < 1e-9) continue;
                var ax = e / len;
                var ay = new Vector(-ax.Y, ax.X);

                double u0 = double.MaxValue, u1 = double.MinValue;
                double v0 = double.MaxValue, v1 = double.MinValue;
                foreach (var p in hull)
                {
                    double u = p.X * ax.X + p.Y * ax.Y;
                    double v = p.X * ay.X + p.Y * ay.Y;
                    if (u < u0) u0 = u;
                    if (u > u1) u1 = u;
                    if (v < v0) v0 = v;
                    if (v > v1) v1 = v;
                }
                double area = (u1 - u0) * (v1 - v0);
                if (area >= bestArea) continue;

                bestArea = area;
                dir = ax;
                halfU = (u1 - u0) / 2;
                halfV = (v1 - v0) / 2;
                midU = (u0 + u1) / 2;
                midV = (v0 + v1) / 2;
            }
            return bestArea < double.MaxValue;
        }

        /// <summary>Monotone-chain convex hull, counter-clockwise.</summary>
        private static List<Point> ConvexHull(List<Point> pts)
        {
            var p = new List<Point>(pts);
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            if (p.Count < 3) return p;

            static double Cross(Point o, Point a, Point b)
                => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            var hull = new List<Point>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                var seq = pass == 0 ? p : Enumerable.Reverse(p);
                foreach (var q in seq)
                {
                    while (hull.Count >= start + 2
                        && Cross(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(q);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

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

            // Load common geometry. Only the body is mandatory. Every other
            // part here is optional for the same reason the button table
            // below is: a pad that does not have the control does not ship
            // the mesh. The 2015 Steam Controller has one stick and no
            // separate ring solid (the bezel is a hole in the case, not a
            // part), so demanding all seven would have meant shipping empty
            // meshes to satisfy the loader.
            MainBody = LoadModel("MainBody.obj");
            LeftThumbRing = TryLoadModel("Joystick-Left-Ring.obj");
            RightThumbRing = TryLoadModel("Joystick-Right-Ring.obj");
            LeftMotor = TryLoadModel("MotorLeft.obj");
            RightMotor = TryLoadModel("MotorRight.obj");
            LeftShoulderTrigger = TryLoadModel("Shoulder-Left-Trigger.obj");
            RightShoulderTrigger = TryLoadModel("Shoulder-Right-Trigger.obj");

            // Stick rings get quadrant-based X/Y detection in ControllerModelView.
            // Not in ClickMap; the view checks IsStickRing() and uses hit position.
            if (LeftShoulderTrigger != null)
                ClickMap[LeftShoulderTrigger] = "LeftTrigger";
            if (RightShoulderTrigger != null)
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

            // Each stick's cap carries its four axis directions by quadrant.
            // A pad with no separate cap solid falls back to its click mesh,
            // which then splits by radius, so every stick has a direction
            // target whatever its mesh split looks like.
            //
            // The cap is NOT part of the stick BUTTON's highlight. It used to
            // be, which meant pressing or hovering the click lit the entire
            // stick and the two controls a stick carries looked like one. The
            // Steam Deck showed it plainly, its cap and its collar being
            // separate solids, and that is where the owner called it.
            RegisterQuadrants(LeftThumbRing ?? LeftThumb,
                "LeftThumbAxisYNeg", "LeftThumbAxisY", "LeftThumbAxisXNeg", "LeftThumbAxisX");
            RegisterQuadrants(RightThumbRing ?? RightThumb,
                "RightThumbAxisYNeg", "RightThumbAxisY", "RightThumbAxisXNeg", "RightThumbAxisX");

            // Add non-button parts to scene, skipping the ones this pad
            // does not have.
            model3DGroup.Children.Add(MainBody);
            foreach (var part in new[] { LeftThumbRing, RightThumbRing, LeftMotor,
                                         RightMotor, LeftShoulderTrigger, RightShoulderTrigger })
                if (part != null)
                    model3DGroup.Children.Add(part);
        }

        // ─────────────────────────────────────────────
        //  Button registration
        // ─────────────────────────────────────────────

        /// <summary>Parts that lean with a stick without lighting with it,
        /// keyed by that stick's ring.
        ///
        /// <para>Tilting and glowing are different sets. The Steam Deck's
        /// stem is the shaft between its cap and its base: it has to lean
        /// with them, and it must NOT light, because the glow belongs on the
        /// base the way it does on every other pad here.</para></summary>
        public readonly Dictionary<Model3DGroup, List<Model3DGroup>> StickRiders = new();

        protected void AddStickRider(Model3DGroup ring, Model3DGroup part)
        {
            if (ring == null || part == null) return;
            if (!StickRiders.TryGetValue(ring, out var list))
                StickRiders[ring] = list = new List<Model3DGroup>();
            list.Add(part);
        }

        /// <summary>Registers a surface whose quadrants are four targets,
        /// in up / down / left / right order. See <see cref="QuadrantMap"/>.</summary>
        protected void RegisterQuadrants(Model3DGroup group, string up, string down, string left, string right)
        {
            if (group == null) return;
            QuadrantMap[group] = new[] { up, down, left, right };
        }

        /// <summary>Registers a group whose whole surface IS one direction,
        /// the way a D-pad key is. It goes in <see cref="ClickMap"/> only:
        /// hover and click-to-record resolve through that, while
        /// <see cref="ButtonMap"/> drives the per-frame press glow and an
        /// axis direction has no pressed state to drive it with.</summary>
        protected void RegisterDirection(Model3DGroup group, string target)
        {
            if (group == null || string.IsNullOrEmpty(target)) return;
            ClickMap[group] = target;
        }

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

        /// <summary>The accent material every glow in the preview is drawn
        /// with: hover, press, and the Map All flash all look one up in
        /// <see cref="HighlightMaterials"/>.</summary>
        protected static Material CreateAccentHighlight()
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

            return new DiffuseMaterial(accentBrush);
        }

        /// <summary>
        /// Creates accent-colored highlight materials for all children.
        /// Uses the app's accent brush from WPF UI theme resources.
        /// </summary>
        protected virtual void DrawAccentHighlights()
        {
            var highlightMaterial = CreateAccentHighlight();
            // Type-checked rather than cast: a model that parks a bare
            // GeometryModel3D in the scene would otherwise take down its
            // own constructor here.
            foreach (var child in model3DGroup.Children)
                if (child is Model3DGroup group)
                    HighlightMaterials[group] = highlightMaterial;
        }

        /// <summary>Gives every interactive group a highlight material if it
        /// does not already have one, so nothing the user can hover, press
        /// or flash is left with no glow to look up. Existing entries are
        /// kept: a model that hand-tunes one (the DualSense's clear plastic)
        /// keeps its own.
        ///
        /// <para>This is a BACKSTOP, and it exists because the per-model
        /// call was forgettable and got forgotten. All three Valve models
        /// shipped without calling <see cref="DrawAccentHighlights"/>, so
        /// their HighlightMaterials were empty and not one button on the
        /// Steam Deck, the 2015 Steam Controller or the 2026 Steam
        /// Controller lit up on hover, on press, or while recording. The
        /// only thing that still worked was the stick-direction wedge,
        /// which draws its own overlay and looks nothing up.</para></summary>
        public void EnsureHighlightMaterials()
        {
            Material accent = null;
            Material Accent() => accent ??= CreateAccentHighlight();

            void Fill(Model3DGroup group)
            {
                if (group == null || HighlightMaterials.ContainsKey(group)) return;
                HighlightMaterials[group] = Accent();
            }

            foreach (var group in ClickMap.Keys)
                Fill(group);
            foreach (var list in ButtonMap.Values)
                foreach (var group in list)
                    Fill(group);
            Fill(LeftThumbRing);
            Fill(RightThumbRing);
            foreach (var child in model3DGroup.Children)
                if (child is Model3DGroup group)
                    Fill(group);
        }

        /// <summary>The ONE construction path for a preview model, used by
        /// the viewport and by the tests that pin the interaction contract,
        /// so the two cannot drift. Ends in
        /// <see cref="EnsureHighlightMaterials"/>: a model reaching the
        /// viewport with no glow materials is the defect that path exists
        /// to make unrepeatable.</summary>
        public static ControllerModelBase Create(string family, string appearance, bool extraControls)
        {
            ControllerModelBase model = family switch
            {
                "DS4" => new ControllerModelDS4(appearance ?? "JetBlack"),
                "DualSense" => new ControllerModelDualSense(appearance ?? "White"),
                "DualSenseEdge" => new ControllerModelDualSenseEdge(),
                "Switch2Pro" => new ControllerModelSwitch2Pro(extraControls),
                "XboxSeries" => new ControllerModelXboxSeries(appearance ?? "Carbon", extraControls),
                "SteamDeck" => new ControllerModelSteamDeck(),
                "SteamController" => new ControllerModelSteamController(),
                "SteamController2" => new ControllerModelSteamController2(),
                _ => new ControllerModelXbox360(),
            };
            model.EnsureHighlightMaterials();
            return model;
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
        /// gray if the resource is missing so the model still renders.</summary>
        /// <summary>Give a material a specular highlight.
        /// DiffuseMaterial has no specular term, so a semi-transparent
        /// diffuse layer renders as a flat tint: the clear ABXY shells
        /// read as no shell at all, and the letters under them look
        /// printed straight onto the button. A Blinn-Phong highlight is
        /// what says "there is a glossy surface here". It is additive,
        /// so the tint underneath keeps its color.</summary>
        protected static Material AddGloss(Material baseMaterial, double intensity, double power)
        {
            if (baseMaterial == null) return null;
            var sheen = new SolidColorBrush(Colors.White) { Opacity = intensity };
            sheen.Freeze();
            var specular = new SpecularMaterial(sheen, power);
            specular.Freeze();
            var group = new MaterialGroup();
            group.Children.Add(baseMaterial);
            group.Children.Add(specular);
            group.Freeze();
            return group;
        }

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
        /// <summary>Give a part its resting color AND register it as that
        /// part's default, which is what the preview restores to after a
        /// press, a hover or a Map All flash.
        ///
        /// <para>Both halves are required. A model that only sets geometry
        /// materials still renders in the loader's own default the moment
        /// anything restores a group, and a model that sets neither renders
        /// in it from the start: HelixToolkit's ObjReader hands back a
        /// yellow default when the OBJ names no material, which is what
        /// every Valve model did until this existed.</para></summary>
        protected void Paint(Model3DGroup group, Material material)
        {
            if (group == null || material == null) return;
            ApplyMaterial(group, material);
            DefaultMaterials[group] = material;
        }

        /// <summary>Paint every part registered under a pad-setting name,
        /// so a control made of several meshes stays one color.</summary>
        protected void PaintTarget(string padSettingName, Material material)
        {
            if (!ButtonMap.TryGetValue(padSettingName, out var list)) return;
            foreach (var g in list)
                Paint(g, material);
        }

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
                QuadrantMap?.Clear();
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
