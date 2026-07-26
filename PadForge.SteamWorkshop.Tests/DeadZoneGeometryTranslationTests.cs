using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Steam's stick deadzone GEOMETRY, imported for real rather
    /// than noted as a shortfall.
    ///
    /// A stick-hosted joystick group carries deadzone_shape: Circle means the
    /// inner radius is a radial test over the X/Y pair, Cross and Square mean
    /// a per-axis test. The engine has supported both since v25 through
    /// ParamStickDeadZoneShape (2 = radial, 1 = axial) plus
    /// ParamStickDeadZoneInner, and the stick-hosted MOUSE emitter already
    /// stamped it. The stick-hosted THUMB PAIR emitter did not: it emitted a
    /// per-axis deadzone and filed a "deadzone radii apply per axis, not
    /// radially" note in the import report. So a Circle deadzone silently
    /// became a square one and the report admitted it instead of fixing it.
    ///
    /// The engine finds the companion axis by INDEX (0 with 1, 3 with 4) and
    /// "Gamepad LeftStickX" resolves to "Axis 0", so the pair test genuinely
    /// applies to what this emitter produces.</summary>
    public class DeadZoneGeometryTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"DZ\"\n";

        private static string Group(int id, string mode, string inputsAndSettings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{inputsAndSettings}\t}}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

        private static string Inputs(params string[] members)
            => "\t\t\"inputs\"\n\t\t{\n" + string.Concat(members) + "\t\t}\n";

        private static string Inp(string name, string binding)
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n"
             + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n";

        private static string Preset(int id, string name, params (int GroupId, string Binding)[] entries)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\t\"preset\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"name\"\t\"{name}\"\n");
            sb.Append("\t\t\"group_source_bindings\"\n\t\t{\n");
            foreach (var e in entries)
                sb.Append($"\t\t\t\"{e.GroupId}\"\t\"{e.Binding}\"\n");
            sb.Append("\t\t}\n\t}\n");
            return sb.ToString();
        }

        private static string StickVdf(params (string Key, string Value)[] settings)
            => Head
             + Group(1, "joystick_move", Inputs(Inp("click", "xinput_button JOYSTICK_LEFT")) + Settings(settings))
             + Preset(0, "Default", (1, "joystick active"))
             + "}\n";

        private static MappingSourceView[] StickSources(TranslatedProfile p)
            => p.XboxMappingSet.Rows
                .Where(r => r.Target == "LeftThumbAxisX" || r.Target == "LeftThumbAxisY")
                .SelectMany(r => r.Sources)
                .Select(s => new MappingSourceView(s.Descriptor, s.ParamStickDeadZoneShape, s.ParamStickDeadZoneInner))
                .ToArray();

        private readonly record struct MappingSourceView(string Descriptor, int Shape, double Inner);

        /// <summary>THE FIX. Circle is Steam's radial deadzone, so the emitted
        /// pair must carry the radial shape (engine 2) and the inner radius,
        /// and the import must NOT file a shortfall note about it.</summary>
        [Fact]
        public void CircleDeadZone_EmitsTheRadialPairTest_AndFilesNoResidual()
        {
            var p = Translate(StickVdf(("deadzone_shape", "1"), ("deadzone_inner_radius", "8192")));

            var sources = StickSources(p);
            Assert.NotEmpty(sources);
            Assert.All(sources, s => Assert.Equal(2, s.Shape));       // 2 = radial pair test
            Assert.All(sources, s => Assert.True(s.Inner > 0.0,
                "the authored inner radius must reach the source, not just the report"));

            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }

        /// <summary>Cross is per-axis by design, so it maps to the axial shape
        /// (engine 1). Still a real stamp, still no shortfall note: the
        /// geometry is honoured, it simply is not radial.</summary>
        [Fact]
        public void CrossDeadZone_EmitsTheAxialShape_AndFilesNoResidual()
        {
            var p = Translate(StickVdf(("deadzone_shape", "0"), ("deadzone_inner_radius", "8192")));

            var sources = StickSources(p);
            Assert.NotEmpty(sources);
            Assert.All(sources, s => Assert.Equal(1, s.Shape));       // 1 = axial

            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }

        /// <summary>Square is the other per-axis shape and folds to axial too,
        /// so no config with an authored shape reaches the user as a caveat.</summary>
        [Fact]
        public void SquareDeadZone_EmitsTheAxialShape()
        {
            var p = Translate(StickVdf(("deadzone_shape", "2"), ("deadzone_inner_radius", "8192")));
            Assert.All(StickSources(p), s => Assert.Equal(1, s.Shape));
        }

        /// <summary>A group with no geometry at all stamps nothing, so an
        /// unauthored stick is not given a deadzone it never asked for.</summary>
        [Fact]
        public void NoGeometryAuthored_StampsNothing()
        {
            var p = Translate(StickVdf());
            Assert.All(StickSources(p), s => Assert.Equal(0, s.Shape));
        }

        /// <summary>The outer radius alone is geometry too: applying it moves
        /// inside the same transform, so it must arm the stamp even with no
        /// inner radius authored.</summary>
        [Fact]
        public void OuterRadiusAlone_ArmsTheStamp()
        {
            var p = Translate(StickVdf(("deadzone_outer_radius", "28800")));
            Assert.All(StickSources(p), s => Assert.NotEqual(0, s.Shape));
        }

        /// <summary>The stick emitter has TWO paths and both must stamp. The
        /// tests above drive the matched (uncrossed) one; output_joystick 2 on
        /// the left stick crosses the pair to the right thumb and takes the
        /// other emitter entirely. Mutation testing caught that the first pass
        /// only covered one of them.</summary>
        [Fact]
        public void CrossedStickPair_AlsoStampsTheGeometry()
        {
            var p = Translate(StickVdf(("output_joystick", "2"),
                ("deadzone_shape", "1"), ("deadzone_inner_radius", "8192")));

            var crossed = p.XboxMappingSet.Rows
                .Where(r => r.Target == "RightThumbAxisX" || r.Target == "RightThumbAxisY")
                .SelectMany(r => r.Sources)
                .ToArray();

            Assert.NotEmpty(crossed);
            Assert.All(crossed, s => Assert.Equal(2, s.ParamStickDeadZoneShape));
            Assert.All(crossed, s => Assert.True(s.ParamStickDeadZoneInner > 0.0));

            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }

        /// <summary>The trackpad host KEEPS the note, and that is honest: its
        /// pair rides Touchpad finger / gesture descriptors, which the engine
        /// reads outside the Axis path where the geometry is applied, so there
        /// is no companion-axis pair test to consume the radii. Pinned so the
        /// distinction is deliberate rather than an oversight. Round six (R6):
        /// this now drives joystick_move on a trackpad, the exact branch the
        /// commit preserved; the first version drove absolute_mouse, whose
        /// residual fires from a different emitter, so deleting the pinned
        /// branch left this green.</summary>
        [Fact]
        public void TrackpadHost_StillReportsTheResidual()
        {
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "xinput_button JOYSTICK_LEFT"))
                    + Settings(("deadzone_shape", "1"), ("deadzone_inner_radius", "8192")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Contains(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }

        /// <summary>Round six (R3): mouse_joystick ("As Joystick,
        /// Mouse-like") on a STICK host is the other stick-capable
        /// thumb-pair emitter, and the 67fca4d9 pass missed it: it kept
        /// filing the residual and stamped no geometry, so a Circle
        /// deadzone still came through square there. It now mirrors
        /// EmitMouseAxes exactly. Default output is the right thumb.</summary>
        [Fact]
        public void MouseJoystickOnAStick_StampsTheGeometry_AndFilesNoResidual()
        {
            string vdf = Head
                + Group(1, "mouse_joystick",
                    Inputs(Inp("click", "xinput_button JOYSTICK_LEFT"))
                    + Settings(("deadzone_shape", "1"), ("deadzone_inner_radius", "8192")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var sources = p.XboxMappingSet.Rows
                .Where(r => r.Target == "RightThumbAxisX" || r.Target == "RightThumbAxisY")
                .SelectMany(r => r.Sources)
                .ToArray();

            Assert.NotEmpty(sources);
            Assert.All(sources, s => Assert.Equal(2, s.ParamStickDeadZoneShape));
            Assert.All(sources, s => Assert.True(s.ParamStickDeadZoneInner > 0.0));

            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }

        /// <summary>Round six (R4): a FULL inner radius (32767, a stick
        /// meant to be dead until the rim) folded to dzPct 100 and was
        /// stamped as 1.0, which the engine's geometry read treats as
        /// unset, so the strictest authored deadzone imported as none at
        /// all. The stamp caps at 0.99: only the outermost sliver of
        /// travel registers, which is the author's intent.</summary>
        [Fact]
        public void FullInnerRadius_ClampsBelowTheEngineGate()
        {
            var p = Translate(StickVdf(("deadzone_shape", "1"),
                ("deadzone_inner_radius", "32767")));

            var sources = StickSources(p);
            Assert.NotEmpty(sources);
            Assert.All(sources, s => Assert.Equal(0.99, s.Inner, 6));
        }

        /// <summary>Round six (R5): gyro_to_joystick_deflection reads the
        /// gravity-lean pair, which is not an Axis-path read, so the
        /// geometry stamp cannot land there. It used to drop an authored
        /// deadzone with NO note, the one pair host that lost the radii
        /// silently; it now files the same residual as every other
        /// non-stick pair host.</summary>
        [Fact]
        public void DeflectionHost_ReportsTheResidual()
        {
            string vdf = Head
                + Group(1, "gyro_to_joystick_deflection",
                    Inputs(Inp("click", "xinput_button JOYSTICK_LEFT"))
                    + Settings(("deadzone_inner_radius", "8192")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Contains(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }
    }
}
