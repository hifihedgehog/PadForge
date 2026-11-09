using System.Linq;
using PadForge.Engine.Common.Mapping;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Steam's trigger mode and Soft Press activator import as the
    /// reads steamclient64.dll runs: the trigger's Soft Pull through its own
    /// trigger and Threshold Trigger Style, the full pull at 95 percent, the
    /// Soft Press by the analog value its table picks per input, and the gyro
    /// modes' tilt members.</summary>
    public class SteamTriggerAndSoftPressTests
    {
        private static TranslatedProfile Translate(string vdf)
            => new ConfigTranslator().Translate(SteamInputConfig.FromVdf(VdfParser.Parse(vdf)),
                new TranslationOptions { FileId = 42 });

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Pull\"\n";

        private static string Group(int id, string mode, string body)
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{body}\t}}\n";

        private static string Inputs(params string[] members) => "\t\t\"inputs\"\n\t\t{\n" + string.Concat(members) + "\t\t}\n";

        /// <summary>A member with one or more activators, each a type, a
        /// binding and its settings.</summary>
        private static string Inp(string name, params (string Type, string Binding, (string, string)[] Settings)[] acts)
        {
            var sb = new System.Text.StringBuilder($"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n");
            foreach (var (type, binding, settings) in acts)
            {
                sb.Append($"\t\t\t\t\t\"{type}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n");
                sb.Append($"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n\t\t\t\t\t\t}}\n");
                if (settings != null && settings.Length > 0)
                {
                    sb.Append("\t\t\t\t\t\t\"settings\"\n\t\t\t\t\t\t{\n");
                    foreach (var (k, v) in settings) sb.Append($"\t\t\t\t\t\t\t\"{k}\"\t\"{v}\"\n");
                    sb.Append("\t\t\t\t\t\t}\n");
                }
                sb.Append("\t\t\t\t\t}\n");
            }
            return sb.Append("\t\t\t\t}\n\t\t\t}\n").ToString();
        }

        private static (string, string, (string, string)[]) Full(string binding) => ("Full_Press", binding, null);
        private static (string, string, (string, string)[]) Soft(string binding, params (string, string)[] s) => ("Soft_Press", binding, s);

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs) sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            return sb.Append("\t\t}\n").ToString();
        }

        private static string Preset(string source)
            => "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Default\"\n\t\t\"group_source_bindings\"\n\t\t{\n"
             + $"\t\t\t\"1\"\t\"{source} active\"\n\t\t}}\n\t}}\n";

        private static TranslatedProfile One(string mode, string source, string members, params (string, string)[] settings)
            => Translate(Head + Group(1, mode, Inputs(members) + Settings(settings)) + Preset(source) + "}\n");

        private static string KeyRow(TranslatedProfile p, string key)
            => Assert.Single(Assert.Single(p.KbmMappingSet.Rows, r => r.Target == key).Sources).Descriptor;

        [Fact]
        public void TriggerEdge_IsTheSoftPull_HairTriggerAt10000ByDefault()
        {
            var p = One("trigger", "left_trigger", Inp("edge", Full("key_press E")));
            Assert.Equal(BoundedReads.Pull(1, false, 10000, false, "Gamepad LeftTrigger"), KeyRow(p, "KbmKey45"));
        }

        [Fact]
        public void TriggerEdge_TakesTheRadiusAndStyle_AndKnowsTheClickIsBound()
        {
            var p = One("trigger", "right_trigger",
                Inp("edge", Full("key_press E")) + Inp("click", Full("key_press Q")),
                ("edge_binding_radius", "2627"), ("adaptive_threshold", "3"));
            Assert.Equal(BoundedReads.Pull(3, false, 2627, true, "Gamepad RightTrigger"), KeyRow(p, "KbmKey45"));
            // The click keeps the full pull, 95 percent.
            var click = Assert.Single(Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey51").Sources);
            Assert.Equal("Gamepad RightTrigger", click.Descriptor);
            Assert.True(click.HalfAxis);
            Assert.Equal(90, click.DeadZone);
        }

        [Fact]
        public void TriggerClick_UnderHipFireExclusive_GivesWayToTheSoftPull()
        {
            var p = One("trigger", "left_trigger",
                Inp("edge", Full("key_press E")) + Inp("click", Full("key_press Q")),
                ("adaptive_threshold", "5"));
            Assert.Equal(BoundedReads.Pull(5, true, 10000, true, "Gamepad LeftTrigger"), KeyRow(p, "KbmKey51"));
        }

        [Fact]
        public void TriggerClick_UnderHipFireExclusive_WithNoEdge_IsTheFullPull()
        {
            var p = One("trigger", "left_trigger", Inp("click", Full("key_press Q")), ("adaptive_threshold", "5"));
            var click = Assert.Single(Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey51").Sources);
            Assert.Equal("Gamepad LeftTrigger", click.Descriptor);
            Assert.Equal(90, click.DeadZone);
        }

        [Fact]
        public void SoftPress_OnATrigger_ReadsThePullWithItsThresholdAndStyle()
        {
            var p = One("trigger", "right_trigger",
                Inp("edge", Soft("key_press E", ("activation_threshold", "9830"), ("soft_press_sytle", "1"))));
            Assert.Equal(BoundedReads.Press(1, false, 9830, false, false, "-", "Gamepad RightTrigger"), KeyRow(p, "KbmKey45"));
            var simple = One("trigger", "left_trigger", Inp("click", Soft("key_press E")));
            Assert.Equal(BoundedReads.Press(0, false, 4096, false, false, "-", "Gamepad LeftTrigger"), KeyRow(simple, "KbmKey45"));
        }

        [Fact]
        public void SoftPress_HipFireExclusive_HoldsItsMembersFullPressOff()
        {
            var p = One("trigger", "left_trigger",
                Inp("click", Soft("key_press E", ("soft_press_sytle", "5")), Full("key_press Q")));
            Assert.Equal(BoundedReads.Press(5, false, 4096, false, true, "-", "Gamepad LeftTrigger"), KeyRow(p, "KbmKey45"));
            Assert.Equal(BoundedReads.Press(5, true, 4096, false, true, "-", "Gamepad LeftTrigger"), KeyRow(p, "KbmKey51"));
        }

        [Fact]
        public void SoftPress_OnADpadDirection_ReadsTheComponent()
        {
            var p = One("dpad", "left_trackpad", Inp("dpad_north", Soft("key_press E", ("activation_threshold", "14745"))));
            Assert.Equal(BoundedReads.Press(0, false, 14745, false, false, "Y-", "Touchpad 0 Finger 0 Ring"),
                KeyRow(p, "KbmKey45"));
        }

        [Fact]
        public void SoftPress_OnAStickEdge_ReadsTheLength()
        {
            var p = One("joystick_move", "right_joystick", Inp("edge", Soft("key_press E")));
            Assert.Equal(BoundedReads.Press(0, false, 4096, false, false, "len", "Gamepad RightStickRing"),
                KeyRow(p, "KbmKey45"));
        }

        [Fact]
        public void SoftPress_OnATrackpadClick_KeepsTheClick()
        {
            var p = One("absolute_mouse", "right_trackpad", Inp("click", Soft("key_press E", ("activation_threshold", "9830"))));
            Assert.Equal("Touchpad 1 Click", KeyRow(p, "KbmKey45"));
        }

        [Theory]
        [InlineData("four_buttons", "button_diamond", "button_a")]
        [InlineData("absolute_mouse", "center_trackpad", "click")]
        public void SoftPress_OnAnInputWithNoAnalogValue_IsNeverFired(string mode, string source, string member)
        {
            var p = One(mode, source, Inp(member, Soft("key_press E")));
            Assert.DoesNotContain(p.KbmMappingSet.Rows, r => r.Target == "KbmKey45");
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.SoftPressNoAnalog);
        }

        [Theory]
        [InlineData("gyro_to_mouse")]
        [InlineData("gyro_to_joystick")]
        [InlineData("absolute_mouse")]
        public void GyroMode_TiltMembers_ReadTheRollPast2000(string mode)
        {
            // deadzone_outer_radius 2000 by default: the roll leaves the horizon
            // past 5.4933 degrees, and the default gyro button (the right pad
            // touch) turns the gyro on.
            double h = 32767 * 1.25 / 100;
            string pair = "Gyro Tilt " + System.Math.Round(2000 / 32767.0 * 90, 4)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + " 90";
            var p = One(mode, "gyro", Inp("dpad_east", Full("key_press E")) + Inp("dpad_west", Full("key_press Q")));
            Assert.Equal(BoundedReads.Level((2000 + h) / 32767 * 100, (2000 - h) / 32767 * 100, false, "X+",
                BoundedGate.Engaged, pair), KeyRow(p, "KbmKey45"));
            Assert.Equal(BoundedReads.Level((2000 + h) / 32767 * 100, (2000 - h) / 32767 * 100, false, "X-",
                BoundedGate.Engaged, pair), KeyRow(p, "KbmKey51"));
        }

        [Fact]
        public void GyroMode_NorthAndSouth_AreNeverFired()
        {
            var p = One("gyro_to_mouse", "gyro", Inp("dpad_north", Full("key_press E")));
            Assert.DoesNotContain(p.KbmMappingSet.Rows, r => r.Target == "KbmKey45");
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void ScrollWheel_TouchMember_Fires()
        {
            var p = One("scrollwheel", "left_trackpad", Inp("touch", Full("key_press E")));
            Assert.Equal("Touchpad 0 Finger 0 Down", KeyRow(p, "KbmKey45"));
        }
    }
}
