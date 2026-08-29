// 3D controller model system adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Steam Controller (2015) mesh: Valve's own CAD, released March 2016,
// CC BY-NC-SA 4.0. Meshed from the archive's STEP file, which is the exact
// B-rep surface, by tools/steam_controller_2015_mesh.py. Valve named
// every solid, so the button mapping comes from the source names rather
// than from guesswork, and the two-shot button molds come out as
// separate cap and glyph solids.

using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace PadForge.Models3D
{
    /// <summary>
    /// Steam Controller (2015) model, serving the steam-controller and
    /// steam-controller-composite profiles.
    ///
    /// <para>Two departures from the standard part table, both physical.
    /// The pad has ONE analog stick, so there is no right stick and no
    /// RightStickClick mesh. And its D-pad is the LEFT TRACKPAD: SDL drives
    /// that pad as the D-pad and the right one as the right thumbstick, so
    /// the mesh tool quarters each face and the quarters arrive as their own
    /// meshes. The left pad's four are DPadUp/Down/Left/Right and register
    /// through the standard table like any D-pad key. The right pad's four
    /// carry the right stick's axis directions and register below.</para>
    ///
    /// <para>The trackpads and the rear grip buttons are the controls the
    /// standard table has no slot for. Each grip paddle is the FLARED WING
    /// of the rear cover, which Valve models as one solid spanning both
    /// handles, so the mesh tool splits the wings off it at the molded
    /// crease and hands each one the lever that sits behind it.</para>
    /// </summary>
    public class ControllerModelSteamController : ControllerModelBase
    {
        private readonly Model3DGroup LeftPad, RightPad;
        private readonly System.Collections.Generic.List<Model3DGroup> RightPadQuarters = new();

        /// <summary>This pad's touch face is the WHOLE pad, which the
        /// *PadTouch mesh alone is not.
        ///
        /// <para>Both pads are carved into four direction quarters around a
        /// center disc: the left pad's quarters ARE this controller's D-pad
        /// (SDL reads its quadrant bits as a hat) and the right pad's are
        /// the right stick's four directions. The center disc measures 16.9
        /// mm against a pad about 42 mm across, so a finger dot fitted to
        /// the disc alone would crawl around the middle 40% and never reach
        /// an edge.</para></summary>
        public override Model3DGroup[] TouchParts0
            => Parts(Touchpad, "DPadUp", "DPadDown", "DPadLeft", "DPadRight");

        public override Model3DGroup[] TouchParts1
        {
            get
            {
                var list = new System.Collections.Generic.List<Model3DGroup>();
                if (TouchpadRight != null) list.Add(TouchpadRight);
                foreach (var q in RightPadQuarters)
                    if (q != null) list.Add(q);
                return list.ToArray();
            }
        }

        /// <summary>The fitted face IS the pad, so there is no bezel left
        /// to crop.</summary>
        public override double TouchpadXInsetFrac => 0.0;
        public override double TouchpadZTopInsetFrac => 0.0;
        public override double TouchpadZBottomInsetFrac => 0.0;

        /// <summary>Stands a translucent thumbstick on the right pad.
        ///
        /// <para>It is scenery with one job: the stick is this model's
        /// RightThumbRing, so the preview's joystick pass leans it with the
        /// right stick's axes, which the pad already drives (the axis table
        /// maps the pad to RightThumbAxisX and Y, and PadViewModel feeds
        /// RawThumbRX and RY from it). Neither piece is in a click or button
        /// map, so hovering still finds the pad's quarters and its click
        /// underneath.</para>
        ///
        /// <para>Two pieces, because a stick is two things. The doughnut
        /// ring is the collar and stays put on the pad, the way a real one
        /// is fixed to the shell. The stem and its dished cap are the stick
        /// and lean.</para>
        ///
        /// <para>Built from the pad's measured face rather than from
        /// numbers: center, normal and radius all come from TouchSurface, so
        /// it stands square to a pad canted 19 degrees and scales with it.
        /// The pivot sits 19 mm behind the cap, which is where the family
        /// puts one (17 on the DualSense, 19 on the Switch 2 Pro, 20 on the
        /// Xbox Series, 22 on the Xbox 360).</para></summary>
        private void BuildRightStickGhost()
        {
            var pad = TouchpadSurface1;
            if (pad.IsEmpty) return;

            // Everything scales off the pad's own radius. This pad is round,
            // measured: its outline fills 0.786 of its bounding square where
            // a circle gives 0.785.
            double padR = pad.Radius > 0 ? pad.Radius : pad.ExtentU / 2;

            // Stand it on the pad's OWN center, not the fitted plane's. The
            // plane is fitted from the four quarters as well as the center
            // disc, and the quarters are bowl walls, so its midpoint sits a
            // millimetre or so off the pad's axis. The disc is the axis.
            var disc = TouchpadRight.Bounds;
            var discCenter = new Point3D(disc.X + disc.SizeX / 2,
                                         disc.Y + disc.SizeY / 2,
                                         disc.Z + disc.SizeZ / 2);
            var up = pad.Normal;
            double lift = Vector3D.DotProduct(pad.Center - discCenter, up);
            var face = discCenter + up * lift;

            // ── The stick's own shape, in head radii ──
            // Taken off the DualSense, whose numbers the Xbox Series and the
            // Switch 2 Pro repeat within a few percent. Its head is 8.78 mm
            // in radius and its profile runs, from the face back: a dish
            // floor reaching r 0, a rim that widens 0.91 -> 1.00 over 3.5 mm,
            // a 0.52 stem for another 3.5, then a base cone out to 1.36.
            //
            // The DOUGHNUT is that rim. It is the head's own outer wall, so
            // it surrounds the dish and leans WITH the stick. It is not a
            // collar on the shell, which is what an earlier pass built.
            //
            // The base cone is normally buried in the controller's shell, so
            // only its shoulder shows here; the profile stops at the pad.
            double headR = padR * 0.43;
            var shape = new System.Collections.Generic.List<(double R, double H)>
            {
                (1.06, 0.00),   // the base's shoulder, sitting on the pad
                (0.78, 0.12),
                (0.57, 0.30),   // the waist
                (0.54, 0.52),   // the stem
                (0.97, 0.62),   // the rim flares out into the head
                (1.00, 0.86),   // its outer wall, near straight
                (0.94, 0.96),   // rolling over the top
                (0.80, 1.00),   // the dish's outer lip: the doughnut
                (0.45, 0.96),   // the dish floor, sloping back in
                (0.00, 0.94),   // its center
            };
            for (int i = 0; i < shape.Count; i++)
                shape[i] = (shape[i].R * headR, shape[i].H * headR);

            var mb = new MeshBuilder(false, false);
            Revolve(mb, face, up, shape, 44);

            // Dim and cool. A brighter ghost reads as a lit control, and
            // this one is never lit: it is a label for what the pad does.
            var brush = new SolidColorBrush(Color.FromArgb(0x55, 0x8F, 0xA0, 0xB4));
            var material = new DiffuseMaterial(brush);

            RightThumbRing = new Model3DGroup();
            RightThumbRing.Children.Add(
                new GeometryModel3D(mb.ToMesh(), material) { BackMaterial = material });
            Paint(RightThumbRing, material);
            model3DGroup.Children.Add(RightThumbRing);

            // The head carries the four directions by quadrant, which is
            // what a stick's ring does on every other model here. Without
            // this the doughnut is inert: the pad's quarters answer a hover
            // out at the rim while the head in the middle answers nothing,
            // and the head is where a hand reaches for a stick.
            //
            // The base registered nothing for this side, because at that
            // point this controller had no right ring to register.
            RegisterQuadrants(RightThumbRing,
                "RightThumbAxisYNeg", "RightThumbAxisY",
                "RightThumbAxisXNeg", "RightThumbAxisX");

            var pivot = face + up * (headR - 19.0);
            JoystickRotationPointCenterRightMillimeter =
                new Vector3D(pivot.X, pivot.Y, pivot.Z);
        }

        /// <summary>Revolves a (radius, height) profile about an axis and
        /// adds it to the builder. Height runs along the axis from the base
        /// point. One helper covers the collar's torus and the stick's
        /// tapered body, so both stay square to a canted pad without any
        /// transform stack.</summary>
        private static void Revolve(MeshBuilder mb, Point3D basePoint, Vector3D axis,
            System.Collections.Generic.IList<(double R, double H)> profile, int segments)
        {
            if (profile.Count < 2 || segments < 3) return;

            // Any two directions square to the axis will do: the shape is a
            // surface of revolution, so where the seam falls does not show.
            var side = Vector3D.CrossProduct(axis, new Vector3D(0, 0, 1));
            if (side.Length < 1e-6) side = Vector3D.CrossProduct(axis, new Vector3D(1, 0, 0));
            side.Normalize();
            var other = Vector3D.CrossProduct(axis, side);
            other.Normalize();

            Point3D At(int seg, int step)
            {
                double a = 2 * Math.PI * (seg % segments) / segments;
                var (rad, h) = profile[step];
                return basePoint + axis * h + (side * Math.Cos(a) + other * Math.Sin(a)) * rad;
            }

            for (int step = 0; step + 1 < profile.Count; step++)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    var p0 = At(seg, step);
                    var p1 = At(seg + 1, step);
                    var p2 = At(seg + 1, step + 1);
                    var p3 = At(seg, step + 1);

                    // A profile point on the axis makes the quad a triangle.
                    if (profile[step].R < 1e-9)
                        mb.AddTriangle(p0, p2, p3);
                    else if (profile[step + 1].R < 1e-9)
                        mb.AddTriangle(p0, p1, p2);
                    else
                        mb.AddQuad(p0, p1, p2, p3);
                }
            }
        }

        private Model3DGroup[] Parts(Model3DGroup center, params string[] targets)
        {
            var list = new System.Collections.Generic.List<Model3DGroup>();
            if (center != null) list.Add(center);
            foreach (var name in targets)
                if (ButtonMap.TryGetValue(name, out var groups))
                    foreach (var g in groups)
                        if (g != null) list.Add(g);
            return list.ToArray();
        }
        private readonly Model3DGroup LeftGripButton, RightGripButton;

        public ControllerModelSteamController() : base("SteamController")
        {
            // ── Rotation points ─────────────────────────
            // Each is the mesh's own center in X and Z with its rear edge
            // in Y, the same construction the Xbox 360 model uses, read
            // off the converted meshes rather than eyeballed.
            JoystickRotationPointCenterLeftMillimeter = new Vector3D(-18.45f, -8.10f, -14.15f);
            // The right pivot is filled in by BuildRightStickGhost below,
            // from the pad's own measured face: this controller has no right
            // stick to read one off.
            JoystickRotationPointCenterRightMillimeter = new Vector3D(0f, 0f, 0f);
            JoystickMaxAngleDeg = 18.0f;

            ShoulderTriggerRotationPointCenterLeftMillimeter = new Vector3D(-47.10f, -6.60f, 31.10f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D(47.10f, -6.60f, 31.10f);
            TriggerMaxAngleDeg = 16.0f;

            UpwardVisibilityRotationAxisLeft = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationAxisRight = new Vector3D(1, 0, 0);
            UpwardVisibilityRotationPointLeft = new Vector3D(-40.0f, -14.0f, 40.0f);
            UpwardVisibilityRotationPointRight = new Vector3D(40.0f, -14.0f, 40.0f);

            // ── Mappable controls the standard table does not cover ──
            LeftPad = LoadModel("LeftPadTouch.obj");
            RegisterButton("LeftTouchpadClick", LeftPad);
            model3DGroup.Children.Add(LeftPad);

            RightPad = LoadModel("RightPadTouch.obj");
            RegisterButton("RightTouchpadClick", RightPad);
            model3DGroup.Children.Add(RightPad);

            // Both trackpads carry a touch preview. Finger 0 rides the left
            // pad and finger 1 the right, the split the frame packers use.
            Touchpad = LeftPad;
            TouchpadRight = RightPad;

            // The right pad IS the right thumbstick, in SDL's own words:
            // "the right pad is normally mapped to right thumbstick"
            // (SDL_hidapi_steam.c 1673, with RIGHTX and RIGHTY reading
            // sRightPadX/Y at 1650). Its four quarters carry that stick's
            // axis directions and each highlights as itself, which is how a
            // direction with its own mesh behaves everywhere in this tree.
            // The left pad's quarters are D-pad keys and the standard part
            // table has already registered them.
            //
            // Quarters rather than the quadrant-wedge path because these
            // pads are deep concave bowls: the wedge's torus-outward offset
            // drove half of each face sideways ACROSS the bowl instead of
            // off it, so two quadrants cleared and two sank under the
            // surface.
            foreach (var (file, target) in new[]
            {
                ("RightPadUp.obj", "RightThumbAxisYNeg"),
                ("RightPadDown.obj", "RightThumbAxisY"),
                ("RightPadLeft.obj", "RightThumbAxisXNeg"),
                ("RightPadRight.obj", "RightThumbAxisX"),
            })
            {
                var quarter = TryLoadModel(file);
                if (quarter == null) continue;
                RegisterDirection(quarter, target);
                RightPadQuarters.Add(quarter);
                model3DGroup.Children.Add(quarter);
            }

            // ── The right pad IS the right stick, so show one ──
            // Nothing on this controller reads as a right thumbstick, and
            // the pad it lives on looks like the left one, which is a D-pad.
            // A translucent stick standing on the pad's face says what the
            // pad does and leans with it, and being see-through it never
            // hides the pad's own quarters or its touch dot.
            BuildRightStickGhost();

            LeftGripButton = LoadModel("LeftGrip.obj");
            RegisterButton("LeftGrip", LeftGripButton);
            model3DGroup.Children.Add(LeftGripButton);

            RightGripButton = LoadModel("RightGrip.obj");
            RegisterButton("RightGrip", RightGripButton);
            model3DGroup.Children.Add(RightGripButton);

            PaintEverything();

            // Glyph riders. Valve's CAD carries each button as a two-shot
            // mold, the cap and the printed glyph as separate solids, so
            // the letters come out as their own meshes. They ride on
            // their cap's highlight and take the printed colors: the
            // pack's SC art prints ABXY in green, red, blue and yellow on
            // black caps, and the system glyphs in white.
            // Glyph colors are the Xbox 360 class's own, already calibrated
            // for this light rig.
            var glyph = Mat("#D4D4D4");
            AddRiderTo("ButtonA", "B1-Symbol.obj", Mat("#7cb63b"));
            AddRiderTo("ButtonB", "B2-Symbol.obj", Mat("#ff5f4b"));
            AddRiderTo("ButtonX", "B3-Symbol.obj", Mat("#6ac4f6"));
            AddRiderTo("ButtonY", "B4-Symbol.obj", Mat("#faa51f"));
            AddRiderTo("ButtonStart", "StartIcon.obj", glyph);
            AddRiderTo("ButtonBack", "BackIcon.obj", glyph);
            // SteamButton_Label is the in-mold label FILM, 12.2 mm square
            // on a 12.3 mm cap: the whole button face, not a logo glyph.
            // Painted white it was a white disc. There is no logo geometry
            // to color, so the face is a dark button face, a step lighter
            // than the cap so it reads as a button rather than a hole.
            AddRiderTo("ButtonGuide", "SpecialIcon.obj", Mat("#2E2F31"));
        }

        private void AddRiderTo(string padSettingName, string filename, Material material)
        {
            var rider = TryLoadModel(filename);
            if (rider == null) return;
            Paint(rider, material);
            model3DGroup.Children.Add(rider);
            if (ButtonMap.TryGetValue(padSettingName, out var list))
                list.Add(rider);
        }

        /// <summary>Resting colors, calibrated against the black controllers
        /// this tree already ships. The viewport's rig is a #999999 sun, a
        /// #666666 ambient, a #595959 headlight and the ember rim, so a
        /// front-facing surface shows at about 1.3 times its hex. The three
        /// approved dark textures (DS4 Jet Black, Switch 2 Pro, DualSense
        /// Midnight) all have a body median near #202224, and the Switch 2
        /// Pro class's accent constants give the scale above it. Two earlier
        /// palettes were wrong for the same reason: one sampled 2D art, one
        /// assumed the rig was a third as bright as it is.
        ///
        /// <para>The face CAPS are dark, not lettered colors. On this pad
        /// the ABXY caps are black plastic with colored glyphs printed on
        /// them, unlike the Xbox 360's colored shells. The color lives on
        /// the glyph riders added in the constructor, which Valve's STEP
        /// carries as their own solids.</para></summary>
        private void PaintEverything()
        {
            var body    = Mat("#202224");
            var shell   = Mat("#26272A");
            var surface = Mat("#3A3B3D");
            var recess  = Mat("#2E2F31");
            var disc    = Mat("#1A1B1D");

            Paint(MainBody, body);
            Paint(LeftShoulderTrigger, shell);
            Paint(RightShoulderTrigger, shell);
            PaintTarget("LeftShoulder", shell);
            PaintTarget("RightShoulder", shell);
            PaintTarget("LeftGrip", body);
            PaintTarget("RightGrip", body);
            PaintTarget("LeftTouchpadClick", surface);
            PaintTarget("RightTouchpadClick", surface);
            // The pad quarters take the pad's own color: they are the pad's
            // face, cut up so each direction can light on its own.
            foreach (var t in new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
                PaintTarget(t, surface);
            foreach (var q in RightPadQuarters)
                Paint(q, surface);
            PaintTarget("LeftThumbButton", recess);
            // The knurled cap is its own solid and its own control (the
            // stick's directions), so it is painted here rather than riding
            // the click mesh's PaintTarget. A shade below the collar under
            // it, which is what makes the collar read as a ring.
            Paint(LeftThumbRing, disc);
            PaintTarget("ButtonBack", recess);
            PaintTarget("ButtonStart", recess);
            PaintTarget("ButtonGuide", disc);
            foreach (var t in new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY" })
                PaintTarget(t, disc);
        }

        private static Material Mat(string hex) =>
            new DiffuseMaterial(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

        /// <summary>The preview camera is fixed, so every model carries a
        /// constant scale that brings its authoring size to the framing the
        /// camera expects. The Xbox 360 mesh is the reference at 151.45 mm
        /// across, and this pad's meshed STEP measures 161.17 mm.</summary>
        public override double ModelScale => 151.45 / 161.17;

    }
}
