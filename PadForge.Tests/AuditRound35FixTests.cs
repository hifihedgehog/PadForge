using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Engine.Touchpad;
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

        // ── Both Sony packers honour a mapped Touchpad press ──

        /// <summary>tp.Click is the PHYSICAL touchpad press; Gamepad.TOUCHPAD
        /// is a mapped or macro-driven one. The DS4 packer honoured both and
        /// the DualSense packer only the physical source, so a macro bound to
        /// Touchpad reached the host on a virtual DS4 and silently did nothing
        /// on a virtual DualSense.</summary>
        [Theory]
        [InlineData("dualshock-4-v1", 6)]
        [InlineData("dualsense", 9)]
        [InlineData("dualsense-edge", 9)]
        public void SonyPackers_HonourAMappedTouchpadPress(string profileId, int buttonByte)
        {
            var packer = PadForge.Common.Input.SonyReportPackers.ForProfile(profileId);
            Assert.NotNull(packer);

            var gp = new PadForge.Engine.Gamepad();
            var tp = new PadForge.Engine.TouchpadState();   // NOT clicked
            var motion = new PadForge.Services.MotionSnapshot();

            var idle = new byte[63];
            packer(in gp, in tp, in motion, 100, false, 0, idle);

            gp.Buttons |= PadForge.Engine.Gamepad.TOUCHPAD;
            var pressed = new byte[63];
            packer(in gp, in tp, in motion, 100, false, 0, pressed);

            Assert.Equal(0, idle[buttonByte] & 0x02);
            Assert.Equal(0x02, pressed[buttonByte] & 0x02);
        }

        // ── Legacy trigger migration must survive the XML load path ──

        /// <summary>The TriggerInputs setter allocated an empty list before
        /// testing its input, and EnsureTriggerInputEntries skips the legacy
        /// migration whenever that field is non-null. So an XML load, which
        /// sets TriggerInputs to null for a legacy macro, permanently blocked
        /// the migration: the macro still FIRED, because the engine reads the
        /// old fields, while the editor showed "Not set".</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void LegacyTrigger_StillMigrates_AfterTheLoadPathSetsTriggerInputs(string loaded)
        {
            var macro = new PadForge.ViewModels.MacroItem
            {
                TriggerDeviceGuid = Guid.NewGuid(),
                TriggerRawButtons = new[] { 3, 7 },
            };

            // What the XML deserializer does for a macro with no <TriggerInputs>.
            macro.TriggerInputs = loaded;

            var entries = macro.GetTriggerInputEntries();
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.RawButton == 3);
            Assert.Contains(entries, e => e.RawButton == 7);
        }

        /// <summary>Positive control: a real TriggerInputs payload must still
        /// parse, or the fix could just be "never populate the list".</summary>
        [Fact]
        public void RealTriggerInputsPayload_StillParses()
        {
            var g = Guid.NewGuid();
            var seed = new PadForge.ViewModels.MacroItem
            {
                TriggerDeviceGuid = g,
                TriggerRawButtons = new[] { 5 },
            };
            string spec = seed.TriggerInputs;
            Assert.False(string.IsNullOrEmpty(spec));

            var loadedMacro = new PadForge.ViewModels.MacroItem { TriggerInputs = spec };
            Assert.Single(loadedMacro.GetTriggerInputEntries());
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

        // ── Dirty-gate coverage for the stick-config mirror ──

        /// <summary>FAMILY guard, not a point test. OnStickConfigPropertyChanged
        /// mirrors each changed StickConfigItem property back onto the
        /// ViewModel, and a parallel HashSet decides whether that change also
        /// marks the document dirty. Any property present in the mirror switch
        /// but absent from the set is silently non-persisting: the user moves
        /// the control, the value reaches the ViewModel, nothing flags dirty,
        /// and save drops it.
        ///
        /// <para>Sensitivity (the stick speed knob, live for keyboard-and-mouse
        /// sticks) was exactly that. Asserting on Sensitivity alone would pin
        /// today's instance; this asserts the INVARIANT, so the next property
        /// added to the switch without a set entry fails here.</para></summary>
        [Fact]
        public void EveryMirroredStickConfigProperty_AlsoMarksTheDocumentDirty()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "PadForge.App", "ViewModels", "PadViewModel.cs"));

            var setBlock = System.Text.RegularExpressions.Regex.Match(
                src, @"StickConfigPropertyNames\s*=\s*new\(\)\s*\{(.*?)\};",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(setBlock.Success, "StickConfigPropertyNames not found. Test is stale.");

            var gated = System.Text.RegularExpressions.Regex
                .Matches(setBlock.Groups[1].Value, @"nameof\(StickConfigItem\.(\w+)\)")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            int handler = src.IndexOf("private void OnStickConfigPropertyChanged", StringComparison.Ordinal);
            Assert.True(handler > 0, "OnStickConfigPropertyChanged not found. Test is stale.");
            var mirrored = System.Text.RegularExpressions.Regex
                .Matches(src.Substring(handler, Math.Min(9000, src.Length - handler)),
                         @"case nameof\(StickConfigItem\.(\w+)\)")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            // Positive control: a zero-match sweep must not read as a pass.
            Assert.True(gated.Count > 10, $"Only parsed {gated.Count} gated names.");
            Assert.True(mirrored.Count > 10, $"Only parsed {mirrored.Count} mirror cases.");
            Assert.Contains("Sensitivity", mirrored);

            var ungated = mirrored.Except(gated).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.True(ungated.Count == 0,
                "Mirrored but never marks dirty, so the value is lost on save: "
                + string.Join(", ", ungated));
        }

        // ── Xbox-slot demand covers every VC-writing macro action ──

        /// <summary>FAMILY guard. MacroNeedsXboxSlot decides whether a
        /// translated macro forces an Xbox slot. Every action that WRITES a
        /// virtual-controller target needs one; the list had six of the eight
        /// and omitted ToggleVcAxis and RepeatVcAxisWhileHeld, both emitted by
        /// this same translator. Keyed off the enum so a new Vc* action that
        /// skips the list fails here rather than in a user's profile.</summary>
        [Fact]
        public void MacroNeedsXboxSlot_CoversEveryVcWritingAction()
        {
            string root = RepoRoot();
            string enumSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "PadForge.SteamWorkshop", "Translation", "TranslatedProfile.cs"));
            string xlat = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "PadForge.SteamWorkshop", "Translation", "ConfigTranslator.cs"));

            var enumBlock = System.Text.RegularExpressions.Regex.Match(
                enumSrc, @"enum TranslatedMacroAction\s*\{(.*?)\n\s*\}",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(enumBlock.Success, "TranslatedMacroAction not found. Test is stale.");

            var vcActions = System.Text.RegularExpressions.Regex
                .Matches(enumBlock.Groups[1].Value, @"^\s*(\w+)\s*=\s*\d+",
                         System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .Where(n => n.Contains("Vc", StringComparison.Ordinal))
                .ToList();

            int at = xlat.IndexOf("private static bool MacroNeedsXboxSlot", StringComparison.Ordinal);
            Assert.True(at > 0, "MacroNeedsXboxSlot not found. Test is stale.");
            string body = xlat.Substring(at, Math.Min(1400, xlat.Length - at));

            // Positive control: prove the sweep matches known members.
            Assert.True(vcActions.Count >= 8, $"Only found {vcActions.Count} Vc actions.");
            Assert.Contains("HoldVcButton", vcActions);

            var missing = vcActions
                .Where(a => !body.Contains("TranslatedMacroAction." + a, StringComparison.Ordinal))
                .ToList();
            Assert.True(missing.Count == 0,
                "VC-writing actions that never demand an Xbox slot: " + string.Join(", ", missing));
        }

        // ── Every device-offline site neutralizes its mapped outputs ──

        /// <summary>FAMILY guard. Step 3 keeps the last OutputState for a
        /// device that has gone offline ("InputState is not cleared on
        /// disconnect"), so whatever was asserted at the moment of teardown
        /// stays stamped on the slot's combined output. Every site that takes
        /// a device offline must therefore neutralize its mapped outputs.
        ///
        /// <para>Two of the seven sites had the call and five did not, which is
        /// the shape this whole round kept finding: the sibling that already
        /// does it right IS the missing half. Keyed off the assignment itself
        /// so a NEW offline path that forgets the call fails here.</para></summary>
        [Fact]
        public void EveryDeviceOfflineSite_NeutralizesItsMappedOutputs()
        {
            string path = System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Common",
                "Input", "InputManager.Step1.UpdateDevices.cs");
            var lines = System.IO.File.ReadAllLines(path);

            var offenders = new System.Collections.Generic.List<int>();
            int sites = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("IsOnline = false", StringComparison.Ordinal)) continue;
                sites++;
                // The neutralize belongs in the same block as the assignment.
                string window = string.Join("\n", lines.Skip(i).Take(16));
                if (!window.Contains("NeutralizeMappedOutputsFor", StringComparison.Ordinal))
                    offenders.Add(i + 1);
            }

            // Positive control: prove the sweep actually located the sites.
            // A rename would otherwise make this test vacuously green.
            Assert.True(sites >= 7, $"Only found {sites} offline sites. Test is stale.");

            Assert.True(offenders.Count == 0,
                "Device-offline sites that leave the last OutputState stamped on the slot, "
                + "so a held input latches after teardown, at lines: "
                + string.Join(", ", offenders));
        }

        // ── Menu-context snapshot cannot go stale ──

        /// <summary>FAMILY guard. The fired-provider loops walk a cached array
        /// instead of enumerating the ConcurrentDictionary, which allocated an
        /// enumerator once per direct-bound item per 1 kHz tick. Correctness of
        /// that cache rests entirely on every site that changes the SET of
        /// contexts invalidating it. A site that adds a context without
        /// invalidating strands a menu: the context exists, and the fired
        /// provider cannot see it. Silent, and it survives any behavioural
        /// test that happens to invalidate first.</summary>
        [Fact]
        public void EveryMenuContextMutation_InvalidatesTheSnapshot()
        {
            string path = System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Common",
                "Input", "InputManager.MenuRuntime.cs");
            var lines = System.IO.File.ReadAllLines(path);

            var offenders = new System.Collections.Generic.List<int>();
            int sites = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                bool mutates = l.Contains("MenuContexts.Clear()", StringComparison.Ordinal)
                    || l.Contains("MenuContexts.TryRemove", StringComparison.Ordinal)
                    || l.Contains("MenuContexts[key] =", StringComparison.Ordinal);
                if (!mutates) continue;
                sites++;
                string window = string.Join("\n", lines.Skip(Math.Max(0, i - 2)).Take(11));
                if (!window.Contains("InvalidateMenuContextsSnapshot", StringComparison.Ordinal))
                    offenders.Add(i + 1);
            }

            // Positive control: the sweep must actually be finding the sites.
            Assert.True(sites >= 4, $"Only found {sites} mutation sites. Test is stale.");

            Assert.True(offenders.Count == 0,
                "MenuContexts mutations that leave the cached snapshot stale, so a menu "
                + "context exists but the fired provider cannot see it, at lines: "
                + string.Join(", ", offenders));
        }

        // ── HasAuthoredContent sees the Base layer trio ──

        /// <summary>HasAuthoredContent is the ONE content gate for cold load
        /// and slot-has-content checks, and its own doc note records that a
        /// hand-list previously discarded a menus-only slot on every launch.
        /// The Base layer's appearance trio was added after that note and was
        /// the next omission of the same kind: a slot whose only authoring was
        /// renaming Base, or giving it a colour or icon, read as empty and was
        /// dropped.</summary>
        [Theory]
        [InlineData("BaseLayerName")]
        [InlineData("BaseColor")]
        [InlineData("BaseIcon")]
        public void MappingSetWithOnlyABaseLayerEdit_CountsAsAuthored(string property)
        {
            var set = new MappingSet();
            Assert.False(set.HasAuthoredContent, "A blank set must read as empty.");

            typeof(MappingSet).GetProperty(property).SetValue(set, "x");
            Assert.True(set.HasAuthoredContent,
                $"A set whose only authoring is {property} reads as empty, so cold load drops it.");
        }

        // ── Every trigger-combo edit drops the armed windows ──

        /// <summary>FAMILY guard. Changing a macro's trigger combo mid-hold has
        /// to invalidate the armed windows, or the OLD combo's hold, streak and
        /// last-sample state gets credited to the new one. SetTriggerInputEntries
        /// did this inline and the three legacy removal paths, which edit the
        /// same combo, did none of it. Keyed off the method names so a fourth
        /// editing path that forgets the call fails here.</summary>
        [Fact]
        public void EveryTriggerComboEdit_ClearsTheArmedWindows()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "PadForge.App", "ViewModels", "MacroItem.cs"));

            string[] editors =
            {
                "SetTriggerInputEntries",
                "RemoveLegacyTriggerButton",
                "RemoveLegacyCustomButton",
                "RemoveLegacyAxisTarget",
            };

            var offenders = new System.Collections.Generic.List<string>();
            foreach (var name in editors)
            {
                int at = src.IndexOf("private void " + name, StringComparison.Ordinal);
                if (at < 0) at = src.IndexOf("public void " + name, StringComparison.Ordinal);
                Assert.True(at > 0, name + " not found. Test is stale.");

                // Bound the body by brace depth. A fixed-size window ran past
                // the closing brace into the NEXT method, which also contains
                // the call, so the guard passed with the call deleted. That is
                // the vacuous-test failure this whole round keeps hunting.
                int open = src.IndexOf('{', at);
                Assert.True(open > 0, name + " has no body. Test is stale.");
                int depth = 0, end = open;
                for (int i = open; i < src.Length; i++)
                {
                    if (src[i] == '{') depth++;
                    else if (src[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { end = i; break; }
                    }
                }
                Assert.True(end > open, name + " body never closed. Test is stale.");

                string body = src.Substring(open, end - open);
                if (!body.Contains("ClearArmedTriggerWindows", StringComparison.Ordinal))
                    offenders.Add(name);
            }

            Assert.True(offenders.Count == 0,
                "Trigger-combo edits that leave an armed window credited to the old combo: "
                + string.Join(", ", offenders));
        }

        // ── CloudMatch must not hand back a borrowed floor ──

        /// <summary>minSoFar is the best score some OTHER template already
        /// achieved, passed in purely so the search can prune. Returning it
        /// unchanged reported THIS template as having scored what that other
        /// template scored, and a template with a looser ThresholdOverride
        /// could then clear its own threshold on a number it never earned.</summary>
        [Fact]
        public void CloudMatch_ReportsNoMatchInsteadOfTheBorrowedFloor()
        {
            // Two clearly different shapes: a flat line and a steep zigzag.
            var line = new System.Collections.Generic.List<System.Numerics.Vector2>();
            var zig = new System.Collections.Generic.List<System.Numerics.Vector2>();
            for (int i = 0; i <= 16; i++)
            {
                float x = i / 16f;
                line.Add(new System.Numerics.Vector2(x, 0.5f));
                zig.Add(new System.Numerics.Vector2(x, (i % 2 == 0) ? 0f : 1f));
            }

            var candidate = ShapeRecognizer.BuildCloud(
                new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<System.Numerics.Vector2>> { line }, 32);
            var candidateLut = ShapeRecognizer.BuildLookupTable(
                candidate, ShapeRecognizer.DefaultLookupTableSize);

            var far = new ShapeTemplate
            {
                Name = "Zigzag",
                FingerCount = 1,
                Enabled = true,
                PointCloud = ShapeRecognizer.BuildCloud(
                    new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<System.Numerics.Vector2>> { zig }, 32),
            };
            far.LookupTable = ShapeRecognizer.BuildLookupTable(
                far.PointCloud, ShapeRecognizer.DefaultLookupTableSize);
            far.LookupTableSize = ShapeRecognizer.DefaultLookupTableSize;

            // Positive control: with no floor to borrow, the mismatch scores a
            // real, finite distance. Without this the assertion below would
            // also pass on a CloudMatch that returned MaxValue for everything.
            float unbounded = ShapeRecognizer.CloudMatch(candidate, candidateLut, far, float.MaxValue);
            Assert.True(unbounded < float.MaxValue, "The mismatch should still produce a real distance.");

            // Now hand it a floor it cannot beat. It must report no match
            // rather than echoing the floor back as its own score.
            float borrowed = 0.0001f;
            float bounded = ShapeRecognizer.CloudMatch(candidate, candidateLut, far, borrowed);
            Assert.True(bounded > borrowed,
                $"CloudMatch returned {bounded} for a floor of {borrowed}, so a template that "
                + "never fitted the candidate is credited with another template's score.");
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
