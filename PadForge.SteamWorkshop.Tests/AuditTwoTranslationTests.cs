using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v19 (full-repo audit #2) pins: the gyro
    /// rotation frame flip (R1), the wheel hold_repeats turbo (T1), the
    /// pair-host radial deadzone residual (T2), the bare "deadzone" key
    /// (T3), the rotation nonlinear withhold (T5), and the release-hosted
    /// layer / preset edge note (T6).</summary>
    public class AuditTwoTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
                PreferredLanguage = "english",
            });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Audit2\"\n";

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

        private static string Inp(string name, string binding, string activator = "Full_Press",
            string activatorSettings = "")
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"{activator}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n"
             + activatorSettings
             + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n";

        private static string ActSettings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\t\t\t\t\"settings\"\n\t\t\t\t\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\t\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t\t\t\t\t}\n");
            return sb.ToString();
        }

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

        // ─── R1: gyro rotation pitch-to-X frame flip ────────────────────

        [Fact]
        public void GyroMouseJoystick_Rotation_FlipsPitchToXLeg()
        {
            // 30 degrees: cos = 0.866, sin = 0.5. The engine's axis-frame
            // seam flips gyro yaw (not pitch) on thumb X targets, so the
            // authored pitch-to-X coefficient must be +sin for the gyro
            // family to keep the realized matrix orthogonal (finding 1i).
            string vdf = Head
                + Group(1, "mouse_joystick", Settings(("rotation", "30")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);

            var x = p.XboxMappingSet.Rows.Single(r => r.Target == "RightThumbAxisX");
            var pitchLeg = x.Sources.Single(s => s.Descriptor == "Gyro Pitch");
            Assert.False(pitchLeg.Invert); // +sin, not the stick family's -sin
            Assert.Equal(0.5, pitchLeg.GyroSensitivity, 3);
            var yawLeg = x.Sources.Single(s => s.Descriptor == "Gyro Yaw");
            Assert.False(yawLeg.Invert);
            Assert.Equal(0.866, yawLeg.GyroSensitivity, 3);

            var y = p.XboxMappingSet.Rows.Single(r => r.Target == "RightThumbAxisY");
            Assert.False(y.Sources.Single(s => s.Descriptor == "Gyro Yaw").Invert);
            Assert.False(y.Sources.Single(s => s.Descriptor == "Gyro Pitch").Invert);
        }

        [Fact]
        public void StickMouseJoystick_Rotation_KeepsMinusSinOnCrossLeg()
        {
            // Family 0 never hits the engine's gyro frame flip, so its
            // authored cross leg stays -sin (Invert on the Y-source leg).
            string vdf = Head
                + Group(1, "mouse_joystick", Settings(("rotation", "30")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var x = p.XboxMappingSet.Rows.Single(r => r.Target == "RightThumbAxisX");
            var crossLeg = x.Sources.Single(s => s.Descriptor == "Gamepad LeftStickY");
            Assert.True(crossLeg.Invert);
            Assert.Equal(0.5, crossLeg.Sensitivity, 3);
        }

        // ─── T1: wheel hold_repeats turbo ───────────────────────────────

        [Fact]
        public void WheelHoldRepeats_LowersToWheelTurboMacro()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mouse_wheel SCROLL_DOWN",
                        activatorSettings: ActSettings(("hold_repeats", "1"), ("repeat_rate", "250")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.KbmMappingSet.Rows); // no continuous full-scale row
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.RepeatWheelWhileHeld, m.Action);
            Assert.Equal("WhileHeld", m.TriggerMode);
            Assert.Equal(250, m.IntervalMs);
            Assert.Equal(-1, m.WheelTicks);
            Assert.False(m.WheelHorizontal);
            Assert.False(m.ConsumeTrigger);
        }

        // ─── T2: pair-host deadzone radii on a stick host ───────────────
        // SUPERSEDED CONTRACT (round six, R3): this pin used to assert the
        // v19 residual note on a stick-hosted mouse_joystick, which locked
        // the emitter's INCOMPLETENESS in place. The stick host has a
        // companion-axis pair read, so filing a note instead of stamping
        // the geometry was the defect, not the contract. The stick host
        // now stamps ParamStickDeadZoneShape/Inner exactly like
        // EmitMouseAxes and files nothing; the residual is pinned on the
        // hosts that genuinely cannot consume the radii (trackpad and the
        // deflection pair, DeadZoneGeometryTranslationTests).

        [Fact]
        public void PairHostDeadZoneRadii_OnAStick_LandOnTheGeometry()
        {
            string vdf = Head
                + Group(1, "mouse_joystick", Settings(
                    ("deadzone_inner_radius", "8192"), ("deadzone_outer_radius", "28000")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);

            var x = p.XboxMappingSet.Rows.Single(r => r.Target == "RightThumbAxisX");
            var src = Assert.Single(x.Sources);
            // No deadzone_shape key + geometry present reads as Steam's
            // default Cross: the axial pair test (engine shape 1).
            Assert.Equal(1, src.ParamStickDeadZoneShape);
            Assert.Equal(0.25, src.ParamStickDeadZoneInner, 3);
            // The per-axis stamps stay: DeadZone for digital reads, the
            // outer range on the scalar shaping tail.
            Assert.Equal(25, src.DeadZone);
            Assert.Equal(28000.0 / 32767.0, src.ParamRangeOuter, 3);
        }

        // ─── T3: the bare "deadzone" key ────────────────────────────────

        [Fact]
        public void DpadDeadzoneKey_ParsesAsInnerRadius()
        {
            string vdf = Head
                + Group(1, "dpad", Settings(("deadzone", "1638"))
                    + Inputs(Inp("dpad_north", "key_press W")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal(5, Assert.Single(row.Sources).DeadZone); // 1638 / 32767
            // Member-only hosts emit no pair, so no radial residual note.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }

        // ─── T5: rotation withholds the nonlinear stamps ────────────────

        [Fact]
        public void Rotation_WithholdsNonlinearStamps_AndNamesThem()
        {
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(
                    ("rotation", "30"), ("curve_exponent", "4"), ("anti_deadzone", "3277"),
                    ("acceleration", "2"), ("mouse_smoothing", "10")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);

            var legs = p.KbmMappingSet.Rows
                .Where(r => r.Target is "KbmMouseX" or "KbmMouseY")
                .SelectMany(r => r.Sources)
                .ToList();
            Assert.Equal(4, legs.Count);
            Assert.All(legs, s =>
            {
                Assert.Equal(0, s.ParamCurveExponent);
                Assert.Equal(0, s.ParamAntiDeadzone);
                Assert.Equal(0, s.ParamAccel);
                // mouse_smoothing used to survive the rotation lowering as the
                // one linear knob. It is not imported at all now.
            });
            var note = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.RotationNonlinearWithheld);
            Assert.Equal(TranslationStatus.Partial, note.Status);
            Assert.Equal("curve_exponent, anti_deadzone, acceleration",
                Assert.Single(note.ReasonArgs));
        }

        [Fact]
        public void RotationWithoutNonlinearKeys_StaysUnnoted()
        {
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("rotation", "30")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.RotationNonlinearWithheld);
        }

        // ─── T6: release-hosted layer / preset verbs ────────────────────

        [Fact]
        public void ReleaseHostedAddLayer_EmitsActivator_AndNamesThePressEdge()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action ADD_LAYER 2", activator: "Release")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Toggle", act.Mode); // still lowers, one edge early
            var note = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LayerReleaseEdgeApproximated);
            Assert.Equal(TranslationStatus.Partial, note.Status);
            Assert.Equal("ADD_LAYER", Assert.Single(note.ReasonArgs));
        }

        [Fact]
        public void ReleaseHostedChangePreset_NamesThePressEdge()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action CHANGE_PRESET 2", activator: "Release")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.NotEmpty(p.KbmMappingSet.ShiftActivators);
            var note = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LayerReleaseEdgeApproximated);
            Assert.Equal("CHANGE_PRESET", Assert.Single(note.ReasonArgs));
        }

        [Fact]
        public void PressHostedAddLayer_StaysUnnoted()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action ADD_LAYER 2")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LayerReleaseEdgeApproximated);
        }
    }
}
