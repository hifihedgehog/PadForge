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

        // ── The round-34 sibling family, guarded at the source ──

        /// <summary>Walks up from the test binary to the repo root.</summary>
        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        /// <summary>
        /// `CustomInputState.Axis` is UNSIGNED 0..65535 with 32768 at rest, so
        /// normalizing it is a plain divide. Adding 32768 first maps the real
        /// range onto 0.5..1.5, and a resting stick then reads 1.0.
        ///
        /// <para>Round 34 found this family, fixed two of the four sites, and
        /// shipped a test that asserted against a LOCAL reimplementation of the
        /// formula. That test could not see call sites at all, so the two it
        /// missed survived until this round. This one reads the SOURCE, so it
        /// covers every member of the family including ones added later.</para>
        ///
        /// <para>Deliberately NOT a blanket ban on `+ 32768f`: the same
        /// expression is CORRECT on a signed short (gp.ThumbLX, RawHidState.Axes),
        /// which is why a naive sweep would either miss these or condemn those.
        /// The discriminator is the operand's type, so this pins the specific
        /// readers that consume CustomInputState.Axis.</para>
        /// </summary>
        [Theory]
        [InlineData("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs", "ReadExpressionVariable")]
        [InlineData("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs", "ReadExpressionVariableRaw")]
        [InlineData("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs", "ReadAxisFromDevice")]
        [InlineData("PadForge.App/Services/InputService.cs", "ReadCurrentAxes")]
        public void UnsignedAxisReaders_DoNotReAddTheRestOffset(string relPath, string method)
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), relPath));
            int at = src.IndexOf(" " + method + "(", StringComparison.Ordinal);
            Assert.True(at >= 0, $"{method} not found in {relPath}; rename the test with the code.");

            // Body window: from the signature to the next same-indent close.
            int end = src.IndexOf("\n        }", at, StringComparison.Ordinal);
            if (end < 0) end = Math.Min(src.Length, at + 4000);
            string body = src.Substring(at, end - at);

            foreach (var line in body.Split('\n'))
            {
                string code = line.Split(new[] { "//" }, StringSplitOptions.None)[0];
                if (!code.Contains("65535f")) continue;
                Assert.False(code.Contains("32768"),
                    $"{relPath} :: {method} normalizes an unsigned axis with a 32768 shift: "
                    + code.Trim());
            }
        }

        // ── Disabling a macro must end its RUN, not just its latches ──

        /// <summary>The disable lane cleared the five action latch bits but
        /// left the sequence mid-flight, so re-enabling resumed at
        /// CurrentActionIndex and injected the remaining actions with no
        /// trigger press.</summary>
        [Fact]
        public void DisablingAMacro_EndsTheRunNotJustTheLatches()
        {
            var macro = new PadForge.ViewModels.MacroItem { IsEnabled = true };
            macro.Actions.Add(new PadForge.ViewModels.MacroAction { VcToggleLatched = true });
            macro.Actions.Add(new PadForge.ViewModels.MacroAction());

            macro.IsExecuting = true;
            macro.CurrentActionIndex = 1;
            macro.ComboResumeIndex = 1;

            macro.IsEnabled = false;

            Assert.False(macro.IsExecuting);
            Assert.Equal(0, macro.CurrentActionIndex);
            Assert.Equal(0, macro.ComboResumeIndex);
            // The original latch clear must still happen.
            Assert.False(macro.Actions[0].VcToggleLatched);
        }

        /// <summary>Positive control: enabling must not wipe run state, or the
        /// test above would pass on a setter that resets unconditionally.</summary>
        [Fact]
        public void EnablingAMacro_DoesNotTouchRunState()
        {
            var macro = new PadForge.ViewModels.MacroItem { IsEnabled = false };
            macro.IsExecuting = true;
            macro.CurrentActionIndex = 2;

            macro.IsEnabled = true;

            Assert.True(macro.IsExecuting);
            Assert.Equal(2, macro.CurrentActionIndex);
        }

        // ── Per-mapping dictionaries: lock parity across all five families ──

        /// <summary>
        /// The poll thread reads these per mapping row per device per tick
        /// while the UI thread writes them on save. Three of the five families
        /// (raw / MIDI / KBM) locked every operation; MappingDeadZone and
        /// MappingBidirectional locked none, so a save racing a poll could
        /// corrupt the Dictionary walk or throw.
        ///
        /// <para>HONEST LIMITATION, stated because a green test that cannot
        /// fail is worse than no test. This does NOT lock the contract. Both
        /// locks were removed and this still passed, twice, including a
        /// strengthened version using 256 distinct keys to force repeated
        /// dictionary growth and shrink. A plain Dictionary read racing a write
        /// corrupts or throws only nondeterministically, and it did not
        /// reproduce inside a bounded run on this machine.</para>
        ///
        /// <para>So treat this as a smoke test, not proof. The lock fix rests
        /// on parity instead: the three sibling families (raw / MIDI / KBM)
        /// lock every Get, Set and Flush, these two locked none, and the
        /// hazard is documented on the raw family as "the poll thread reads
        /// while the UI thread writes; a plain Dictionary corrupts under
        /// concurrent read+write". If someone later finds a deterministic
        /// harness for this, replace this comment with it.</para>
        /// </summary>
        [Theory]
        [InlineData("deadzone")]
        [InlineData("bidirectional")]
        public async System.Threading.Tasks.Task PerMappingDicts_SurviveConcurrentReadWrite(string family)
        {
            var ps = new PadSetting();
            using var cts = new System.Threading.CancellationTokenSource();
            var faults = new System.Collections.Generic.List<string>();

            // Distinct keys, so the writer repeatedly GROWS and shrinks the
            // dictionary. A concurrent read during a resize is what actually
            // corrupts a plain Dictionary; churning a fixed key set mostly
            // does not, which is why the first cut of this test passed even
            // with the locks removed.
            var reader = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                        for (int k = 0; k < 256; k++)
                            _ = family == "deadzone"
                                ? ps.GetMappingDeadZone("Key" + k)
                                : ps.GetMappingBidirectional("Key" + k);
                }
                catch (Exception ex) { lock (faults) faults.Add(ex.GetType().Name); }
            });

            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 4000; i++)
                    {
                        for (int k = 0; k < 256; k++)
                        {
                            if (family == "deadzone")
                                ps.SetMappingDeadZone("Key" + k, "25");
                            else
                                ps.SetMappingBidirectional("Key" + k, "1");
                        }
                        for (int k = 0; k < 256; k++)
                        {
                            if (family == "deadzone")
                                ps.SetMappingDeadZone("Key" + k, "");
                            else
                                ps.SetMappingBidirectional("Key" + k, "");
                        }
                    }
                }
                catch (Exception ex) { lock (faults) faults.Add(ex.GetType().Name); }
            });

            await writer;
            cts.Cancel();
            var finished = await System.Threading.Tasks.Task.WhenAny(
                reader, System.Threading.Tasks.Task.Delay(15_000));
            Assert.Same(reader, finished);   // a spin inside the walk never returns
            Assert.Empty(faults);
        }

        // ── Every chromeless FluentWindow must be movable ──

        /// <summary>
        /// A FluentWindow with ExtendsContentIntoTitleBar has
        /// WindowChrome.CaptionHeight = 0, so unless it declares a
        /// &lt;ui:TitleBar&gt; or wires a drag itself, NO point in the window is
        /// non-client and the dialog cannot be moved at all. Twelve shipped
        /// that way. This is the family guard: a thirteenth fails here on
        /// arrival instead of after a release.
        /// </summary>
        [Fact]
        public void EveryChromelessFluentWindow_CanBeDragged()
        {
            string root = RepoRoot();
            var offenders = new System.Collections.Generic.List<string>();
            int checkedCount = 0;

            foreach (var xaml in System.IO.Directory.EnumerateFiles(
                         System.IO.Path.Combine(root, "PadForge.App"), "*.xaml",
                         System.IO.SearchOption.AllDirectories))
            {
                string markup = System.IO.File.ReadAllText(xaml);
                if (!markup.Contains("FluentWindow")) continue;
                if (!markup.Contains("ExtendsContentIntoTitleBar=\"True\"")) continue;
                checkedCount++;

                // A real <ui:TitleBar> element supplies the drag region.
                if (System.Text.RegularExpressions.Regex.IsMatch(markup, @"<\w+:TitleBar\b")) continue;
                // Or the markup wires a drag handler directly.
                if (markup.Contains("MouseLeftButtonDown=")) continue;
                // Or the code-behind does.
                string cb = xaml + ".cs";
                if (System.IO.File.Exists(cb))
                {
                    string code = System.IO.File.ReadAllText(cb);
                    if (code.Contains("DragMove()") || code.Contains("MouseLeftButtonDown")) continue;
                }
                offenders.Add(System.IO.Path.GetFileName(xaml));
            }

            // Positive control: if the scan matched nothing, "no offenders"
            // would be vacuously true.
            Assert.True(checkedCount > 0, "found no chromeless FluentWindow to check");
            Assert.True(offenders.Count == 0,
                "these chromeless FluentWindows have no drag region and cannot be moved: "
                + string.Join(", ", offenders));
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
