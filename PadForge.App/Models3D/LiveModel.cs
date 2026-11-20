using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Media3D;
using PadForge.Controls;
using PadForge.Views;

namespace PadForge.Models3D
{
    /// <summary>A controller's 3D model as a Home card shows it moving: the
    /// model its still was drawn from, frozen on the stills' thread once the
    /// still is drawn. Frozen, the interface's thread can read it, and every
    /// card of the model shares its meshes and materials. A card holds its own
    /// frame of groups around those parts (<see cref="NewInstance"/>), turns
    /// its sticks and triggers as the controller's page turns them, and lights
    /// what is held as that page lights it.</summary>
    public sealed class LiveModel
    {
        private LiveModel(ControllerModelBase model, string family, Matrix3D placement, Point3D camera)
        {
            Parts = model.model3DGroup;
            Placement = placement;
            Camera = camera;
            _family = family;
            StickMaxAngle = model.JoystickMaxAngleDeg;
            TriggerMaxAngle = model.TriggerMaxAngleDeg;
            static Point3D At(Vector3D v) => new(v.X, v.Y, v.Z);
            _leftStickPivot = At(model.JoystickRotationPointCenterLeftMillimeter);
            _rightStickPivot = At(model.JoystickRotationPointCenterRightMillimeter);
            _leftTriggerPivot = At(model.ShoulderTriggerRotationPointCenterLeftMillimeter);
            _rightTriggerPivot = At(model.ShoulderTriggerRotationPointCenterRightMillimeter);
            _leftTrigger = model.LeftShoulderTrigger;
            _rightTrigger = model.RightShoulderTrigger;

            // The page moves and lights a stick only when it has a cap
            // (ControllerModelView.UpdateJoystick). The tilt turns the cap,
            // the click mesh and whatever else is registered to the click
            // (ControllerModelView.StickParts). The glow grades the cap and
            // what the click lights.
            var roles = ControllerModelView.ButtonRoles;
            ulong read = 0, extras = 0;
            (Model3DGroup[] Tilt, Model3DGroup[] Glow, int Click) Stick(Model3DGroup ring, Model3DGroup thumb, string click)
            {
                if (ring == null) return (Array.Empty<Model3DGroup>(), Array.Empty<Model3DGroup>(), -1);
                var tilt = new List<Model3DGroup> { ring };
                var glow = new List<Model3DGroup> { ring };
                if (thumb != null && !ReferenceEquals(thumb, ring)) tilt.Add(thumb);
                if (model.ButtonMap.TryGetValue(click, out var registered))
                    foreach (var group in registered)
                        if (group != null && !ReferenceEquals(group, ring))
                        {
                            if (!tilt.Contains(group)) tilt.Add(group);
                            if (!glow.Contains(group)) glow.Add(group);
                        }
                int bit = IndexOf(roles, click);
                if (bit >= 0) read |= 1UL << bit;
                return (tilt.ToArray(), glow.ToArray(), bit);
            }
            (_leftStick, _leftGlow, _leftClick) = Stick(model.LeftThumbRing, model.LeftThumb, "LeftThumbButton");
            (_rightStick, _rightGlow, _rightClick) = Stick(model.RightThumbRing, model.RightThumb, "RightThumbButton");

            // The buttons, in the page's order (ControllerModelView.HighlightButtons),
            // each with the groups that page lights for it: a group whose
            // first part is a mesh.
            var buttons = new List<(int, Model3DGroup[])>();
            for (int bit = 0; bit < roles.Count; bit++)
            {
                if (!model.ButtonMap.TryGetValue(roles[bit], out var groups)) continue;
                var lit = new List<Model3DGroup>();
                foreach (var group in groups)
                    if (group != null && group.Children.Count > 0 && group.Children[0] is GeometryModel3D)
                        lit.Add(group);
                if (lit.Count == 0) continue;
                buttons.Add((bit, lit.ToArray()));
                read |= 1UL << bit;
                if (model.ExtraControlRoles.Contains(roles[bit])) extras |= 1UL << bit;
            }
            _buttons = buttons.ToArray();
            _buttonsRead = read;
            _extraButtons = extras;

            // How each group takes the light: its accent, whether it grades
            // (the page grades a group that has a rest and an accent
            // material), and whether it glows whole when pressed, as a
            // stick's cap and a bumper do (ControllerModelView.SetGroupHighlight).
            Model3DGroup FirstOf(string role)
                => model.ButtonMap.TryGetValue(role, out var list) && list.Count > 0 ? list[0] : null;
            var leftBumper = FirstOf("LeftShoulder");
            var rightBumper = FirstOf("RightShoulder");
            void Take(Model3DGroup group)
            {
                if (group == null || _lit.ContainsKey(group)) return;
                model.HighlightMaterials.TryGetValue(group, out var accent);
                model.DefaultMaterials.TryGetValue(group, out var rest);
                bool whole = ReferenceEquals(group, model.LeftThumbRing) || ReferenceEquals(group, model.RightThumbRing)
                    || ReferenceEquals(group, leftBumper) || ReferenceEquals(group, rightBumper);
                _lit[group] = new Lit(accent, rest != null && accent != null, whole);
            }
            foreach (var (_, groups) in _buttons)
                foreach (var group in groups) Take(group);
            foreach (var group in _leftGlow) Take(group);
            foreach (var group in _rightGlow) Take(group);
            Take(_leftTrigger);
            Take(_rightTrigger);
            _riders = model.RiderDecals;
            _covering = model.CoveringRiderDecals;
            _files = new List<Model3DGroup>(model.Files).ToArray();
        }

        private static int IndexOf(IReadOnlyList<string> roles, string role)
        {
            for (int i = 0; i < roles.Count; i++)
                if (roles[i] == role) return i;
            return -1;
        }

        /// <summary>The model's parts, frozen. No card shows these groups
        /// themselves: each shows its own frame of groups around the same
        /// meshes.</summary>
        internal Model3DGroup Parts { get; }

        /// <summary>Where the still placed the model.</summary>
        internal Matrix3D Placement { get; }

        /// <summary>Where the still's camera stood. It looks the way the
        /// controller's page looks (ControllerModelFraming).</summary>
        internal Point3D Camera { get; }

        /// <summary>The most a stick tilts and a trigger turns, in degrees:
        /// the model's own.</summary>
        internal float StickMaxAngle { get; }
        internal float TriggerMaxAngle { get; }

        /// <summary>The piece of the still that was kept, in the drawing's
        /// pixels (StagePictures.Fit). A card shows the model through the
        /// same piece, so the model stands where the still stood.</summary>
        internal Int32Rect Cut { get; set; }

        private readonly string _family;
        private readonly Model3DGroup[] _leftStick, _rightStick, _leftGlow, _rightGlow;
        private readonly int _leftClick, _rightClick;
        private readonly Model3DGroup _leftTrigger, _rightTrigger;
        private readonly Point3D _leftStickPivot, _rightStickPivot, _leftTriggerPivot, _rightTriggerPivot;
        private readonly (int Bit, Model3DGroup[] Groups)[] _buttons;
        private readonly Dictionary<Model3DGroup, Lit> _lit = new();
        private readonly HashSet<GeometryModel3D> _riders, _covering;
        private readonly ulong _buttonsRead, _extraButtons;

        /// <summary>The files the model was built from. The model is let go
        /// once the still is drawn, and a rider decal's file had no other
        /// holder (ControllerModelBase.Files): the next collection let it go
        /// while a card still drew its meshes, and the next model of the
        /// family read it again. Read by nothing, held only.</summary>
#pragma warning disable IDE0052
        private readonly Model3DGroup[] _files;
#pragma warning restore IDE0052

        /// <summary>How one group takes the light.</summary>
        private readonly record struct Lit(Material Accent, bool Grades, bool Whole);

        /// <summary>The buttons a card of a preset reads, one bit for each
        /// role of ControllerModelView.ButtonRoles: those the model lights
        /// and the two stick clicks, less the model's extra controls when
        /// the preset lacks them, which the controller's page leaves unwired
        /// (ControllerModelBase.HasExtraControls).</summary>
        internal ulong ButtonsFor(string profileId)
            => _extraButtons == 0 || ControllerModelBase.HasExtraControls(_family, profileId)
                ? _buttonsRead : _buttonsRead & ~_extraButtons;

        /// <summary>The live model of a model drawn for a still, which this
        /// freezes, or null when a part or an accent cannot be frozen, and
        /// the card keeps its still. A model this takes must not be disposed:
        /// disposing empties its frozen parts.</summary>
        internal static LiveModel From(ControllerModelBase model, string family, Matrix3D placement, Point3D camera)
        {
            var parts = model.model3DGroup;
            if (parts == null || !parts.CanFreeze) return null;
            var live = new LiveModel(model, family, placement, camera);
            foreach (var lit in live._lit.Values)
                if (lit.Accent is { CanFreeze: false }) return null;
            parts.Freeze();
            foreach (var lit in live._lit.Values)
                lit.Accent?.Freeze();
            return live;
        }

        /// <summary>A card's own frame around the shared parts, at rest. Each
        /// group is copied, and each mesh the card lights, holding the same
        /// frozen geometry and materials, so making one takes the interface's
        /// thread no longer than walking the model.</summary>
        internal LiveModelInstance NewInstance()
        {
            var groups = new Dictionary<Model3DGroup, Model3DGroup>();
            var leaves = new Dictionary<GeometryModel3D, GeometryModel3D>();
            GeometryModel3D CopyLeaf(GeometryModel3D leaf)
            {
                if (!leaves.TryGetValue(leaf, out var copy))
                {
                    copy = new GeometryModel3D(leaf.Geometry, leaf.Material)
                    {
                        BackMaterial = leaf.BackMaterial,
                        Transform = leaf.Transform,
                    };
                    leaves[leaf] = copy;
                }
                return copy;
            }
            Model3DGroup Copy(Model3DGroup source)
            {
                var copy = new Model3DGroup { Transform = source.Transform };
                bool lit = _lit.ContainsKey(source);
                foreach (var child in source.Children)
                    copy.Children.Add(child switch
                    {
                        Model3DGroup group => Copy(group),
                        GeometryModel3D leaf when lit => CopyLeaf(leaf),
                        _ => child,
                    });
                groups[source] = copy;
                return copy;
            }
            var root = Copy(Parts);

            var riders = new HashSet<GeometryModel3D>();
            var covering = new HashSet<GeometryModel3D>();
            foreach (var (leaf, copy) in leaves)
            {
                if (_riders.Contains(leaf)) riders.Add(copy);
                if (_covering.Contains(leaf)) covering.Add(copy);
            }
            var lights = new Dictionary<Model3DGroup, LiveModelInstance.Light>();
            foreach (var (group, lit) in _lit)
                if (groups.TryGetValue(group, out var copy))
                    lights[group] = new LiveModelInstance.Light(copy, lit.Accent, lit.Grades, lit.Whole, riders);
            LiveModelInstance.Light[] Lights(Model3DGroup[] of)
            {
                var list = new List<LiveModelInstance.Light>();
                foreach (var group in of)
                    if (lights.TryGetValue(group, out var light)) list.Add(light);
                return list.ToArray();
            }
            var buttons = new (int, LiveModelInstance.Light[])[_buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
                buttons[i] = (_buttons[i].Bit, Lights(_buttons[i].Groups));
            LiveModelInstance.Light LightOf(Model3DGroup group)
                => group != null && lights.TryGetValue(group, out var light) ? light : null;

            var instance = new LiveModelInstance(root, StickMaxAngle, TriggerMaxAngle,
                new List<LiveModelInstance.Light>(lights.Values).ToArray(), buttons, riders, covering)
            {
                LeftGlow = Lights(_leftGlow),
                RightGlow = Lights(_rightGlow),
                LeftClick = _leftClick,
                RightClick = _rightClick,
                LeftTriggerLight = LightOf(_leftTrigger),
                RightTriggerLight = LightOf(_rightTrigger),
            };
            instance.LeftStick = LiveModelInstance.Tilt(groups, _leftStick, _leftStickPivot);
            instance.RightStick = LiveModelInstance.Tilt(groups, _rightStick, _rightStickPivot);
            instance.LeftTrigger = LiveModelInstance.Turn(groups, _leftTrigger, _leftTriggerPivot);
            instance.RightTrigger = LiveModelInstance.Turn(groups, _rightTrigger, _rightTriggerPivot);
            return instance;
        }
    }

    /// <summary>One card's model: its frame of groups, the turns its sticks
    /// and triggers take and the light on what is held. Interface thread
    /// only.</summary>
    internal sealed class LiveModelInstance
    {
        internal LiveModelInstance(Model3DGroup root, float stickMaxAngle, float triggerMaxAngle,
            Light[] lights, (int Bit, Light[] Lights)[] buttons,
            HashSet<GeometryModel3D> riders, HashSet<GeometryModel3D> covering)
        {
            Root = root;
            _stickMaxAngle = stickMaxAngle;
            _triggerMaxAngle = triggerMaxAngle;
            _lights = lights;
            _buttons = buttons;
            _riders = riders;
            _covering = covering;
        }

        /// <summary>What the card's viewport shows.</summary>
        internal Model3DGroup Root { get; }

        private readonly float _stickMaxAngle, _triggerMaxAngle;
        private readonly Light[] _lights;
        private readonly (int Bit, Light[] Lights)[] _buttons;
        private readonly HashSet<GeometryModel3D> _riders, _covering;

        internal (AxisAngleRotation3D Up, AxisAngleRotation3D Across) LeftStick, RightStick;
        internal AxisAngleRotation3D LeftTrigger, RightTrigger;

        /// <summary>What each stick's tilt grades, and the bit of its click.</summary>
        internal Light[] LeftGlow = Array.Empty<Light>(), RightGlow = Array.Empty<Light>();
        internal int LeftClick = -1, RightClick = -1;
        internal Light LeftTriggerLight, RightTriggerLight;

        /// <summary>How a group is lit: as at rest, pressed, or graded by how
        /// far its control is held.</summary>
        internal enum Glow : byte { Rest, Pressed, Graded }

        /// <summary>One group the card lights: its copy, its meshes with
        /// the materials they rest in, and the light it shows.</summary>
        internal sealed class Light
        {
            internal Light(Model3DGroup group, Material accent, bool grades, bool whole,
                HashSet<GeometryModel3D> riders)
            {
                Group = group;
                Accent = accent;
                Grades = grades;
                Whole = whole;
                Primary = group.Children.Count > 0 ? group.Children[0] as GeometryModel3D : null;
                var leaves = new List<GeometryModel3D>();
                foreach (var child in group.Children)
                    if (child is GeometryModel3D leaf) leaves.Add(leaf);
                Leaves = leaves.ToArray();
                Rest = new Material[Leaves.Length];
                RestBack = new Material[Leaves.Length];
                Rider = new bool[Leaves.Length];
                for (int i = 0; i < Leaves.Length; i++)
                {
                    Rest[i] = Leaves[i].Material;
                    RestBack[i] = Leaves[i].BackMaterial;
                    Rider[i] = riders.Contains(Leaves[i]);
                }
            }

            internal readonly Model3DGroup Group;
            internal readonly GeometryModel3D Primary;
            internal readonly GeometryModel3D[] Leaves;
            internal readonly Material[] Rest, RestBack;
            internal readonly bool[] Rider;
            internal readonly Material Accent;
            internal readonly bool Grades, Whole;
            internal Glow Shown, Wanted;
            internal float ShownBy, WantedBy;
        }

        /// <summary>A stick's two turns, about the model's up axis and then
        /// its across axis through the pivot, set on every group the stick
        /// is: the controller page's transform (ControllerModelView.UpdateJoystick).</summary>
        internal static (AxisAngleRotation3D Up, AxisAngleRotation3D Across) Tilt(
            Dictionary<Model3DGroup, Model3DGroup> copies, Model3DGroup[] parts, Point3D pivot)
        {
            var up = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
            var across = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
            var tilt = new Transform3DGroup();
            tilt.Children.Add(new RotateTransform3D(up, pivot));
            tilt.Children.Add(new RotateTransform3D(across, pivot));
            foreach (var part in parts)
                if (copies.TryGetValue(part, out var copy)) copy.Transform = tilt;
            return (up, across);
        }

        /// <summary>A trigger's turn about the model's across axis through
        /// its hinge: the controller page's transform (ControllerModelView.UpdateTrigger).</summary>
        internal static AxisAngleRotation3D Turn(Dictionary<Model3DGroup, Model3DGroup> copies, Model3DGroup part, Point3D hinge)
        {
            if (part == null || !copies.TryGetValue(part, out var copy)) return null;
            var turn = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
            copy.Transform = new RotateTransform3D(turn, hinge);
            return turn;
        }

        /// <summary>Turns the sticks and the triggers for what is held, by the
        /// controller page's own angles (<see cref="ControllerPose"/>), and
        /// lights it as that page does. A turn already at its angle and a
        /// light already showing are left alone, so a controller at rest
        /// changes nothing to draw.</summary>
        internal void Pose(StagePose pose)
        {
            Set(LeftStick, ControllerPose.StickAngles(pose.LeftX, pose.LeftY, _stickMaxAngle));
            Set(RightStick, ControllerPose.StickAngles(pose.RightX, pose.RightY, _stickMaxAngle));
            Set(LeftTrigger, ControllerPose.TriggerAngle(pose.LeftPull, _triggerMaxAngle));
            Set(RightTrigger, ControllerPose.TriggerAngle(pose.RightPull, _triggerMaxAngle));
            LightUp(pose);
        }

        /// <summary>The page's three passes, in its order
        /// (ControllerModelView.OnRendering), each choosing a light for the
        /// groups it drives. Then each group that changed shows its new
        /// light.</summary>
        private void LightUp(StagePose pose)
        {
            foreach (var light in _lights)
            {
                light.Wanted = light.Shown;
                light.WantedBy = light.ShownBy;
            }
            // Buttons (HighlightButtons): a held one glows and any other
            // rests. A group two buttons share takes the later one's light,
            // as it does there.
            foreach (var (bit, lights) in _buttons)
            {
                bool held = (pose.Held & (1UL << bit)) != 0;
                foreach (var light in lights)
                    Want(light, held && light.Accent != null ? Glow.Pressed : Glow.Rest, 0);
            }
            // Sticks (UpdateJoystick).
            Grade(LeftGlow, LeftClick, pose.LeftX, pose.LeftY, pose.LeftTouch, pose.Held);
            Grade(RightGlow, RightClick, pose.RightX, pose.RightY, pose.RightTouch, pose.Held);
            // Triggers (UpdateTrigger): a pull grades the trigger.
            Pull(LeftTriggerLight, pose.LeftPull);
            Pull(RightTriggerLight, pose.RightPull);
            foreach (var light in _lights)
                Show(light);
        }

        /// <summary>A stick's tilt grades its cap and what its click lights,
        /// by the farther of its two axes, and a finger on its top glows
        /// them faintly. While its click is held the stick is left as the
        /// click's light left it.</summary>
        private static void Grade(Light[] glow, int click, short x, short y, bool touched, ulong held)
        {
            if (glow.Length == 0 || (click >= 0 && (held & (1UL << click)) != 0)) return;
            float across = x / (float)short.MaxValue, up = y / (float)short.MaxValue;
            bool moved = Math.Abs(across) > ModelGlow.StickGlowsFrom || Math.Abs(up) > ModelGlow.StickGlowsFrom;
            float by = Math.Max(Math.Abs(across), Math.Abs(up));
            if (touched)
            {
                moved = true;
                by = Math.Max(by, ControllerModelView.StickTouchGlow);
            }
            foreach (var light in glow)
                Want(light, moved && light.Grades ? Glow.Graded : Glow.Rest, by);
        }

        private static void Pull(Light light, double pull)
        {
            if (light == null) return;
            float by = (float)pull;
            Want(light, by > ModelGlow.TriggerGlowsFrom && light.Grades ? Glow.Graded : Glow.Rest, by);
        }

        private static void Want(Light light, Glow glow, float by)
        {
            light.Wanted = glow;
            light.WantedBy = glow == Glow.Graded ? by : 0;
        }

        /// <summary>Shows a group's new light. A new kind of light starts from
        /// rest, so nothing the last one set stays.</summary>
        private void Show(Light light)
        {
            if (light.Wanted == light.Shown && light.WantedBy == light.ShownBy) return;
            if (light.Wanted != light.Shown)
                for (int i = 0; i < light.Leaves.Length; i++)
                {
                    light.Leaves[i].Material = light.Rest[i];
                    light.Leaves[i].BackMaterial = light.RestBack[i];
                }
            if (light.Wanted == Glow.Pressed)
                ModelGlow.SetGroupHighlight(light.Group, light.Primary, light.Accent, light.Whole, _riders, _covering);
            else if (light.Wanted == Glow.Graded)
                for (int i = 0; i < light.Leaves.Length; i++)
                    light.Leaves[i].Material = ModelGlow.GradientHighlight(light.Leaves[i],
                        light.Rest[i], light.Accent, light.WantedBy, light.Rider[i]);
            light.Shown = light.Wanted;
            light.ShownBy = light.WantedBy;
        }

        private static void Set((AxisAngleRotation3D Up, AxisAngleRotation3D Across) stick, (float AboutUp, float AboutAcross) angles)
        {
            Set(stick.Up, angles.AboutUp);
            Set(stick.Across, angles.AboutAcross);
        }

        private static void Set(AxisAngleRotation3D turn, float angle)
        {
            if (turn != null && turn.Angle != angle) turn.Angle = angle;
        }
    }
}
