using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.ConfigImport;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The device has one stick card and one trigger card, and a
    /// slot's layers each write the same outputs. WorkshopTuningApplier moves
    /// a target's shaping stamps onto the card only when every row that
    /// writes the target, on every layer, carries the same stamps. Otherwise
    /// each row keeps its own and the engine applies them on the row's read,
    /// so each layer plays as authored.</summary>
    [Collection("SettingsManagerStatics")]
    public class FoldLayerTests
    {
        private const string OneLayer = """[{"id":1,"type":"default","description":"Combat"}]""";

        private static MappingSet[] Clone(MappingSet[] sets)
        {
            var serializer = new XmlSerializer(typeof(MappingSet[]));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            serializer.Serialize(writer, sets);
            using var reader = new StringReader(writer.ToString());
            return (MappingSet[])serializer.Deserialize(reader);
        }

        private static PadSetting Fold(MappingSet[] sets)
        {
            var saved = SettingsManager.SlotMappingSets;
            var ps = new PadSetting();
            try
            {
                SettingsManager.SlotMappingSets = sets;
                WorkshopTuningApplier.ApplyToAssignedDevice(0, ps, Guid.NewGuid().ToString());
            }
            finally
            {
                SettingsManager.SlotMappingSets = saved;
            }
            return ps;
        }

        private static CustomInputState Pushed(int axis, double value)
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            state.Axis[axis] = axis is 2 or 5
                ? (int)Math.Round(value * 65535.0)
                : 32768 + (int)Math.Round(value * 32767.0);
            return state;
        }

        // SpaceSim's Precision layer softens the flight axes to exponent 2.6
        // at half scale, Base keeps 1.6. Before the fold each row plays its
        // own. After it, each layer still does.
        [Theory]
        [InlineData("Base")]
        [InlineData("Precision")]
        public void SpaceSimsLayers_EachPlayTheirOwnCurve(string layer)
        {
            var sets = StarterProfileCatalog.Find("spacesim").Build().SlotMappingSets;
            var before = sets[0].Rows.Single(r => r.Target == "LeftThumbAxisX" && r.LayerMask == layer).Sources.Single();
            var played = Ds4StickChain.Played(sets, left: true, layer);
            for (int i = 1; i <= 20; i++)
            {
                double x = i / 20.0;
                double want = SourceCoercion.EvaluateForBipolarAxisTarget(Pushed(0, x), before);
                double got = played(x, 0).X;
                Assert.True(Math.Abs(got - want) <= 0.02, $"{layer} at {x:0.00}: played {got:0.000}, row {want:0.000}");
            }
        }

        // A reWASD file whose layer carries its own stick curve. The layer
        // plays its curve's power fit, as the import says, and Base its own
        // curve, not the layer's or the other way round.
        [Fact]
        public void ARewasdLayersOwnCurve_PlaysOnThatLayer()
        {
            string baseCurve = """{"points":{"horizontal":[{"travel":8192,"value":2048},{"travel":16384,"value":8192},{"travel":32767,"value":32767}]}}""";
            string layerCurve = """{"points":{"horizontal":[{"travel":8192,"value":16384},{"travel":16384,"value":24576},{"travel":32767,"value":32767}]}}""";
            var r = RewasdImportTests.Import(RewasdImportTests.File(shifts: OneLayer,
                hardware: "[{\"id\":1,\"name\":\"gamepad\",\"convertToVirtual\":true,\"default\":{\"sticks\":[{\"name\":\"left\",\"curves\":" + baseCurve + "}]},"
                    + "\"shifts\":[{\"shiftId\":1,\"sticks\":[{\"name\":\"left\",\"curves\":" + layerCurve + "}]}]}]"));
            Assert.False(r.Refused, r.RefusedKey);
            var sets = r.Profile.SlotMappingSets;
            var basePlayed = Ds4StickChain.Played(sets, left: true, "Base");
            var layerPlayed = Ds4StickChain.Played(sets, left: true, "Shift1");
            // Base at half travel: 8192 of 32767, a quarter. The layer's: three
            // quarters. A power fit lands within a tenth of each.
            Assert.InRange(basePlayed(0.5, 0).X, 0.15, 0.35);
            Assert.InRange(layerPlayed(0.5, 0).X, 0.65, 0.85);
            Assert.InRange(basePlayed(0, 0.5).Y, 0.15, 0.35);
            Assert.InRange(layerPlayed(0, 0.5).Y, 0.65, 0.85);
        }

        // Base and a layer that agree still move onto the card, and the row
        // keeps nothing, so the card applies the curve once.
        [Fact]
        public void RowsThatAgree_StillFoldOntoTheCard()
        {
            var set = new MappingSet();
            foreach (string layer in new[] { "Base", "Shift1" })
                foreach (string axis in new[] { "X", "Y" })
                    set.Rows.Add(new MappingRow
                    {
                        Target = "LeftThumbAxis" + axis,
                        LayerMask = layer,
                        Sources = { new MappingSource { Descriptor = "Gamepad LeftStick" + axis, ParamCurveExponent = 2.0, ParamAntiDeadzone = 0.1 } },
                    });
            var sets = new[] { set };
            var ps = Fold(Clone(sets));
            Assert.False(Common.CurveLut.IsLinear(ps.LeftThumbSensitivityCurveX));
            Assert.Equal("10", ps.LeftThumbAntiDeadZoneX);
            var played = Ds4StickChain.Played(sets, left: true, "Shift1");
            // 0.1 + 0.9 x 0.5^2.
            Assert.InRange(played(0.5, 0).X, 0.315, 0.335);
        }

        // reWASD writes a trigger's dead zone per layer when the layer sets
        // "Affect gamepad behavior for games". Base's 10 percent and the
        // layer's 30 differ, so each trigger row keeps its own dead zone and
        // the trigger read applies it.
        [Fact]
        public void ARewasdLayersOwnTriggerDeadZone_PlaysOnThatLayer()
        {
            var r = RewasdImportTests.Import(RewasdImportTests.File(shifts: OneLayer,
                hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"default":{"triggers":[{"name":"left","affectHardware":true,"zones":[{"name":"low","x":3277}]}]},"shifts":[{"shiftId":1,"triggers":[{"name":"left","affectHardware":true,"zones":[{"name":"low","x":9830}]}]}]}]"""));
            Assert.False(r.Refused, r.RefusedKey);
            var sets = Clone(r.Profile.SlotMappingSets);
            var ps = Fold(sets);
            double Read(string layer, double pull)
            {
                var src = Ds4StickChain.Saved(sets[0].Rows.Single(x => x.Target == "LeftTrigger" && x.LayerMask == layer)).Single();
                float v = SourceCoercion.EvaluateForTriggerTarget(Pushed(2, pull), src);
                ushort pulled = (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535.0);
                return InputManager.ApplyTriggerDeadZone(pulled, double.Parse(ps.LeftTriggerDeadZone, CultureInfo.InvariantCulture),
                    double.Parse(ps.LeftTriggerAntiDeadZone, CultureInfo.InvariantCulture),
                    double.Parse(ps.LeftTriggerMaxRange, CultureInfo.InvariantCulture),
                    Common.CurveLut.GetOrBuild(ps.LeftTriggerSensitivityCurve)) / 65535.0;
            }
            Assert.Equal(0.0, Read("Shift1", 0.2), 3);
            Assert.InRange(Read("Base", 0.2), (0.2 - 0.1) / 0.9 - 0.01, (0.2 - 0.1) / 0.9 + 0.01);
            Assert.InRange(Read("Shift1", 0.5), (0.5 - 0.3) / 0.7 - 0.01, (0.5 - 0.3) / 0.7 + 0.01);
        }

        // A trigger dead zone that every row agrees on moves onto the card and
        // leaves the row, so a pull reads past it once.
        [Fact]
        public void AFoldedTriggerDeadZone_AppliesOnce()
        {
            var r = RewasdImportTests.Import(RewasdImportTests.File(
                hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"default":{"triggers":[{"name":"left","affectHardware":true,"zones":[{"name":"low","x":6553}]}]}}]"""));
            Assert.False(r.Refused, r.RefusedKey);
            var sets = Clone(r.Profile.SlotMappingSets);
            var ps = Fold(sets);
            Assert.Equal("20", ps.LeftTriggerDeadZone);
            var src = sets[0].Rows.Single(x => x.Target == "LeftTrigger" && x.LayerMask == "Base").Sources.Single();
            Assert.Equal(0, src.ParamStickDeadZoneShape);
            float v = SourceCoercion.EvaluateForTriggerTarget(Pushed(2, 0.5), src);
            ushort pulled = (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535.0);
            double read = InputManager.ApplyTriggerDeadZone(pulled, 20, 0, 100) / 65535.0;
            Assert.InRange(read, 0.365, 0.385);
        }
    }
}
