using System;
using System.Linq;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using Xunit;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>The yaw-and-roll axis of Steam's absolute mouse class
    /// (gyro_axis 2 on absolute_mouse and mouse_joystick), as the client
    /// blends it (steamclient64.dll 0x1388af6f0): each leg is its rate times
    /// its scale over 10000, and the output is the signed length of the two
    /// when their signs agree or the yaw leg dominates, else the residual
    /// sqrt of the roll leg squared less the yaw leg squared, signed by the
    /// roll leg. The import carries the join on the row's Custom combine, so
    /// the row is exact and no line is named.</summary>
    public class SteamGyroYawRollBlendTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Blend\"\n";

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

        private const string FaceA =
            "\t\t\"inputs\"\n\t\t{\n\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
            + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
            + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button A\"\n"
            + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n";

        private static TranslatedProfile One(string mode, params (string Key, string Value)[] settings)
            => Translate(Head
                + Group(1, mode, settings.Length > 0 ? Settings(settings) : "")
                + Group(2, "four_buttons", FaceA)
                + Preset(0, "Default", (1, "gyro active"), (2, "button_diamond active"))
                + "}\n");

        private static MappingRow Row(TranslatedProfile p, bool kbm, string target)
            => Assert.Single((kbm ? p.KbmMappingSet : p.XboxMappingSet).Rows, r => r.Target == target);

        private static Func<float, float, float> Blend(MappingRow row)
        {
            Assert.Equal("Custom", row.CombineMode);
            var compiled = MappingExpression.Compile(row.CombineExpression);
            Assert.Equal("", compiled.Error);
            return (yaw, roll) => compiled.Evaluate(new[] { yaw, roll });
        }

        [Fact]
        public void GyroAxis2_JoinsItsLegs_OnTheRowsCustomCombine()
        {
            var p = One("absolute_mouse", ("gyro_axis", "2"),
                ("gyro_yaw_scale", "7494"), ("gyro_roll_scale", "2506"));
            var row = Row(p, kbm: true, "KbmMouseX");
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal(2, row.Sources.Count);
            Assert.Equal("Gyro Yaw", row.Sources[0].Descriptor);
            Assert.False(row.Sources[0].Invert);
            Assert.Equal(0.7494, row.Sources[0].GyroSensitivity, 4);
            Assert.Equal("Gyro Roll", row.Sources[1].Descriptor);
            Assert.True(row.Sources[1].Invert);
            Assert.Equal(0.2506, row.Sources[1].GyroSensitivity, 4);
            Assert.DoesNotContain(p.Report.Entries, e => e.ReasonKey == "Workshop_Tr_GyroYawRollBlend");
        }

        [Fact]
        public void TheJoin_EvaluatesAsTheClientComputesIt()
        {
            var eval = Blend(Row(One("absolute_mouse", ("gyro_axis", "2")), kbm: true, "KbmMouseX"));
            // Agreeing signs: the signed length.
            Assert.Equal(0.5, eval(0.3f, 0.4f), 4);
            Assert.Equal(-0.5, eval(-0.3f, -0.4f), 4);
            // Opposing signs with the yaw leg strictly larger: still the
            // length, signed by yaw (0x1388af82f takes the residual only
            // when the roll leg is at least the yaw leg).
            Assert.Equal(0.5, eval(0.4f, -0.3f), 4);
            // Opposing signs with the roll leg at least the yaw leg: the
            // residual, signed by the roll leg.
            Assert.Equal(Math.Sqrt(0.07), eval(-0.3f, 0.4f), 4);
            Assert.Equal(-Math.Sqrt(0.07), eval(0.3f, -0.4f), 4);
            // One leg alone passes through, zero counting as positive.
            Assert.Equal(-0.5, eval(0f, -0.5f), 4);
            Assert.Equal(0.25, eval(0.25f, 0f), 4);
        }

        [Fact]
        public void MouseJoystick_CarriesTheSameJoin_OnItsStickRow()
        {
            var p = One("mouse_joystick", ("gyro_axis", "2"));
            var row = Row(p, kbm: false, "RightThumbAxisX");
            var eval = Blend(row);
            Assert.Equal(0.5, eval(0.3f, 0.4f), 4);
            Assert.Equal(Math.Sqrt(0.07), eval(-0.3f, 0.4f), 4);
        }

        [Fact]
        public void ARotatedGroup_AddsThePitchLeg_AfterTheJoinedPair()
        {
            var p = One("absolute_mouse", ("gyro_axis", "2"), ("rotation_2", "30"));
            var row = Row(p, kbm: true, "KbmMouseX");
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal(3, row.Sources.Count);
            Assert.Equal("Gyro Pitch", row.Sources[2].Descriptor);
            var compiled = MappingExpression.Compile(row.CombineExpression);
            Assert.Equal("", compiled.Error);
            // The join of the first two, plus the pitch leg (the rotation's
            // trigonometric shares ride the sensitivities, not the formula).
            Assert.Equal(0.5 + 0.2, compiled.Evaluate(new[] { 0.3f, 0.4f, 0.2f }), 4);
        }
    }
}
