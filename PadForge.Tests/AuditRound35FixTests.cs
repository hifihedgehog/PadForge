using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-35 audit fixes. Every test here drives PRODUCTION code.
    ///
    /// <para>Round 34 shipped AxisRangeContractTests, which defined its own
    /// <c>Normalize(int axis) =&gt; axis / 65535f</c> and asserted against that.
    /// It pinned the arithmetic and never called ReadExpressionVariable,
    /// ReadExpressionVariableRaw or ReadCurrentAxes, so it could not go red on
    /// the two sites that commit left unfixed. A test that reimplements the
    /// thing it is testing proves only that the test author can do the
    /// arithmetic. Do not add one here.</para>
    /// </summary>
    public class AuditRound35FixTests
    {
        // ── Unknown function in a Custom combine formula ──

        /// <summary>An unknown name compiled with IsValid = true, rendered the
        /// green "valid" status in the editor, and evaluated to a constant 0
        /// forever. The parser already rejected a BARE unknown identifier, so
        /// `pow` errored while `pow(a,2)` did not.</summary>
        [Theory]
        [InlineData("pow(a,2)")]
        [InlineData("sqr(a)")]
        [InlineData("exp(a)")]
        [InlineData("log(a)")]
        public void UnknownFunction_IsRejected_NotSilentlyZero(string formula)
        {
            var compiled = MappingExpression.Compile(formula);
            Assert.False(compiled.IsValid,
                $"'{formula}' compiled as valid; it would evaluate to a constant 0 "
                + "behind a green status in the editor.");
        }

        /// <summary>Positive control. Without this, "rejects unknown functions"
        /// would pass just as well on a parser that rejects everything.</summary>
        [Theory]
        [InlineData("abs(a)")]
        [InlineData("clamp(a,0,1)")]
        [InlineData("lerp(a,b,0.5)")]
        [InlineData("atan2(a,b)")]
        [InlineData("min(a,b)")]
        public void KnownFunctions_StillCompile(string formula)
        {
            Assert.True(MappingExpression.Compile(formula).IsValid,
                $"'{formula}' should still compile.");
        }

        // ── PadSetting stick sensitivity crosses both mirrors ──

        /// <summary>These two were the only 2 of 168 persisted [XmlElement]
        /// string properties missing from CopyablePropertyNames, so CloneDeep
        /// dropped them and the keyboard-and-mouse Speed knob reverted to "1"
        /// on every load.</summary>
        [Fact]
        public void StickSensitivity_SurvivesCloneDeep()
        {
            var ps = new PadSetting
            {
                LeftThumbSensitivity = "2.5",
                RightThumbSensitivity = "0.4",
            };
            var copy = ps.CloneDeep();
            Assert.Equal("2.5", copy.LeftThumbSensitivity);
            Assert.Equal("0.4", copy.RightThumbSensitivity);
        }

        /// <summary>Two PadSettings differing ONLY in sensitivity must not
        /// collide on the checksum, which is the key the save-side dedup and
        /// the UserSetting-to-PadSetting link both use.</summary>
        [Fact]
        public void StickSensitivity_ChangesTheChecksum()
        {
            var a = new PadSetting { LeftThumbSensitivity = "1" };
            var b = new PadSetting { LeftThumbSensitivity = "3" };
            Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());

            var c = new PadSetting { RightThumbSensitivity = "1" };
            var d = new PadSetting { RightThumbSensitivity = "3" };
            Assert.NotEqual(c.ComputeChecksum(), d.ComputeChecksum());
        }

        /// <summary>Positive control for the two above: an untouched pair must
        /// still agree, or "differs" would be true of everything.</summary>
        [Fact]
        public void IdenticalPadSettings_StillShareAChecksum()
        {
            Assert.Equal(new PadSetting().ComputeChecksum(),
                         new PadSetting().ComputeChecksum());
        }

        // ── MenuDefinitionEntry.Clone ──

        /// <summary>The one persisted attribute of thirty that Clone did not
        /// carry, so every profile snapshot / apply and every Copy From Slot
        /// reset In-Menu Sensitivity to its 100 default and persisted the 100.
        /// Reflection over the whole attribute surface, so the NEXT dropped
        /// field fails this too.</summary>
        [Fact]
        public void MenuDefinitionEntryClone_CarriesEveryPersistedScalar()
        {
            var src = new MenuDefinitionEntry
            {
                SensitivityPercent = 37,
                CellCount = 6,
                MenuId = 7,
                Name = "Test",
            };
            var copy = src.Clone();

            Assert.Equal(37, copy.SensitivityPercent);

            var attrProps = typeof(MenuDefinitionEntry)
                .GetProperties()
                .Where(p => p.CanRead && p.CanWrite
                            && (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string))
                            && Attribute.IsDefined(p, typeof(System.Xml.Serialization.XmlAttributeAttribute)))
                .ToList();
            Assert.NotEmpty(attrProps);
            foreach (var p in attrProps)
            {
                Assert.Equal(p.GetValue(src), p.GetValue(copy));
            }
        }

        // ── Trigger motors through BOTH FFB scratch fills ──

        /// <summary>Sibling pair. Each is asserted separately so a mutation to
        /// one cannot be masked by the other.</summary>
        [Fact]
        public void ConstantForceResolve_CarriesTriggerMotors()
        {
            var ps = new PadSetting
            {
                ConstantForceEnabled = "1",
                ConstantForceX = "0.5",
                ConstantForceY = "0",
            };
            var raw = new Vibration
            {
                LeftMotorSpeed = 0,
                RightMotorSpeed = 0,
                LeftTriggerMotorSpeed = 4321,
                RightTriggerMotorSpeed = 8765,
            };
            var scratch = new Vibration();
            var result = ConstantForceEvaluator.Resolve(raw, ps, scratch);

            // Positive control: constant force really did engage, so the
            // assertions below are about the carry and not about an early out.
            Assert.NotSame(raw, result);
            Assert.Equal(4321, result.LeftTriggerMotorSpeed);
            Assert.Equal(8765, result.RightTriggerMotorSpeed);
        }

        [Fact]
        public void MacroRumbleOverrideMerge_CarriesTriggerMotors()
        {
            var ovr = new MacroRumbleOverride();
            ovr.FireSticky(80, 80);

            var raw = new Vibration
            {
                LeftMotorSpeed = 0,
                RightMotorSpeed = 0,
                LeftTriggerMotorSpeed = 1234,
                RightTriggerMotorSpeed = 5678,
            };
            var scratch = new Vibration();
            var result = MacroRumbleOverride.Merge(raw, ovr, scratch);

            Assert.NotSame(raw, result);
            Assert.Equal(1234, result.LeftTriggerMotorSpeed);
            Assert.Equal(5678, result.RightTriggerMotorSpeed);
        }

        // ── Horizontal wheel is a relative-motion target ──

        /// <summary>Omitting it made a touchpad source on the horizontal wheel
        /// read ABSOLUTE pad position, so a finger resting off centre scrolled
        /// sideways continuously.</summary>
        [Theory]
        [InlineData("KbmMouseX", true)]
        [InlineData("KbmMouseY", true)]
        [InlineData("KbmScroll", true)]
        [InlineData("KbmScrollH", true)]
        [InlineData("LeftThumbAxisX", false)]
        [InlineData("RawAxis0", false)]
        [InlineData("", false)]
        public void RelativeMotionTargets_IncludeBothScrollAxes(string target, bool expected)
        {
            Assert.Equal(expected, SourceEvaluator.IsRelativeMotionTarget(target));
        }

        // ── Xbox impulse-trigger PIDs ──

        /// <summary>The BLE re-enumerations of the Xbox One S and Elite Series
        /// 2 were missing, so both pads lost the HasRumbleTriggers force-enable
        /// and the raw-HID impulse writer on that firmware.</summary>
        [Theory]
        [InlineData(0x0B20)]
        [InlineData(0x0B22)]
        [InlineData(0x02EA)]
        [InlineData(0x0B13)]
        public void ImpulseTriggerPids_CoverTheBleReEnumerations(int pid)
        {
            Assert.True(XboxControllerIdentity.IsImpulseTriggerDevice(
                XboxControllerIdentity.MicrosoftVid, (ushort)pid));
        }

        [Fact]
        public void ImpulseTriggerPids_StillRejectNonImpulsePads()
        {
            // Positive control in the negative direction: the predicate must
            // not have become "true for everything".
            Assert.False(XboxControllerIdentity.IsImpulseTriggerDevice(
                XboxControllerIdentity.MicrosoftVid, 0x0289));   // Xbox 360 wired
            Assert.False(XboxControllerIdentity.IsImpulseTriggerDevice(
                0x054C, 0x0B20));                                 // right pid, Sony vid
        }
    }
}
