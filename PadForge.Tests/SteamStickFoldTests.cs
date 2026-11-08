using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A Steam joystick_move group's dead zone geometry, scale and
    /// curve stamp its rows, and WorkshopTuningApplier moves them onto the
    /// device's stick card at assignment. The row reads the geometry, then
    /// the scale, then the curve (SourceCoercion.ReadAsBipolar,
    /// ApplyStickDeadZoneShape before PerSourceSensitivity before
    /// ApplyCurveRangeShaping). The card runs after the row, so a scale left
    /// on the row would read before the card's dead zone and move its edge
    /// by the scale. The stick plays the same after the move as before
    /// it.</summary>
    [Collection("SettingsManagerStatics")]
    public class SteamStickFoldTests
    {
        private static MappingSet Translate(string shape, string scaleX, string scaleY, string curve)
        {
            string vdf = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"DZ\"\n"
                + "\t\"group\"\n\t{\n\t\t\"id\"\t\"1\"\n\t\t\"mode\"\t\"joystick_move\"\n"
                + "\t\t\"inputs\"\n\t\t{\n\t\t\t\"click\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"binding\"\t\"xinput_button JOYSTICK_LEFT\"\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n"
                + "\t\t\"settings\"\n\t\t{\n\t\t\t\"deadzone_inner_radius\"\t\"6553\"\n\t\t\t\"deadzone_shape\"\t\"" + shape + "\"\n"
                + "\t\t\t\"sensitivity_horiz_scale\"\t\"" + scaleX + "\"\n\t\t\t\"sensitivity_vert_scale\"\t\"" + scaleY + "\"\n"
                + "\t\t\t\"curve_exponent\"\t\"" + curve + "\"\n\t\t}\n\t}\n"
                + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Default\"\n\t\t\"group_source_bindings\"\n\t\t{\n\t\t\t\"1\"\t\"joystick active\"\n\t\t}\n\t}\n}\n";
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 42 }).XboxMappingSet;
        }

        // Circle (1) and Cross (0), a scale over and under 100 on each axis,
        // and Steam's Wide curve (exponent 2) beside one.
        [Theory]
        [InlineData("1", "200", "200", "0")]
        [InlineData("0", "200", "200", "0")]
        [InlineData("1", "50", "50", "0")]
        [InlineData("0", "50", "150", "0")]
        [InlineData("1", "150", "70", "3")]
        [InlineData("1", "100", "100", "3")]
        public void AStickPlaysTheSameOnTheCardAsOnItsRows(string shape, string scaleX, string scaleY, string curve)
        {
            var set = Translate(shape, scaleX, scaleY, curve);
            var rowX = set.Rows.Single(r => r.Target == "LeftThumbAxisX" && r.LayerMask == "Base");
            var rowY = set.Rows.Single(r => r.Target == "LeftThumbAxisY" && r.LayerMask == "Base");
            var srcX = Assert.Single(rowX.Sources);
            var srcY = Assert.Single(rowY.Sources);
            Assert.NotEqual(0, srcX.ParamStickDeadZoneShape);
            var played = Ds4StickChain.Played(new[] { set }, left: true);
            for (int degrees = 0; degrees < 360; degrees += 15)
            {
                for (int i = 1; i <= 40; i++)
                {
                    double rho = i / 40.0;
                    double x = rho * Math.Cos(degrees * Math.PI / 180.0), y = rho * Math.Sin(degrees * Math.PI / 180.0);
                    // The inner radius's own edge.
                    if (Math.Abs(rho - 0.2) < 0.01 || (shape == "0" && (Math.Abs(Math.Abs(x) - 0.2) < 0.01 || Math.Abs(Math.Abs(y) - 0.2) < 0.01)))
                        continue;
                    var state = new CustomInputState();
                    Array.Fill(state.Axis, 32768);
                    state.Axis[0] = 32768 + (int)Math.Round(x * 32767.0);
                    state.Axis[1] = 32768 + (int)Math.Round(y * 32767.0);
                    double wantX = SourceCoercion.EvaluateForBipolarAxisTarget(state, srcX);
                    double wantY = SourceCoercion.EvaluateForBipolarAxisTarget(state, srcY);
                    var got = played(x, y);
                    Assert.True(Math.Abs(got.X - wantX) <= 0.02 && Math.Abs(got.Y - wantY) <= 0.02,
                        $"{degrees} degrees, {rho:0.000}: card ({got.X:0.000}, {got.Y:0.000}), rows ({wantX:0.000}, {wantY:0.000})");
                }
            }
        }
    }
}
