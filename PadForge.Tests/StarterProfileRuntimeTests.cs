using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Runtime validation for the bundled starter profiles (#256).
    ///
    /// <para>The catalog tests prove the profiles are well FORMED: every
    /// descriptor resolves, every target exists, every set is Authoritative.
    /// They cannot prove a binding actually FIRES. That is the question a
    /// controller in hand would answer, and it is answerable without one:
    /// build a synthetic device state, push the physical input the row names,
    /// and run it through the SAME <see cref="SourceCoercion"/> evaluators
    /// Step 3 calls every poll.</para>
    ///
    /// <para>Nothing here re-implements the read. Each test drives
    /// <see cref="SourceCoercion.EvaluateForButtonTarget"/> or
    /// <see cref="SourceCoercion.EvaluateForBipolarAxisTarget"/> directly, so
    /// a change in the engine's own coercion breaks these rather than
    /// silently passing.</para>
    /// </summary>
    public class StarterProfileRuntimeTests
    {
        private static readonly Guid DevGuid = new("11111111-2222-3333-4444-555555555555");
        private const int AxisCenter = 32768;
        private const int Threshold = 50;

        /// <summary>A synthetic gamepad with NO touchpad surface, which is
        /// the ordinary Xbox-pad case (Touchpads stays null).</summary>
        private static CustomInputState ArrangePad()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Starter Test Pad",
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return state;
        }

        /// <summary>Puts the pad at TRUE rest. The thumb axes centre, but the
        /// TRIGGERS rest at 0 and travel up: leaving them at centre is a
        /// half-pull, which reads as pressed and made the first version of
        /// the at-rest test report every trigger binding as stuck.</summary>
        private static void Reset(CustomInputState s)
        {
            Array.Clear(s.Buttons, 0, s.Buttons.Length);
            for (int i = 0; i < s.Axis.Length; i++) s.Axis[i] = AxisCenter;
            s.Axis[2] = 0;   // left trigger
            s.Axis[5] = 0;   // right trigger
            for (int i = 0; i < s.Povs.Length; i++) s.Povs[i] = -1;
        }

        /// <summary>True for rows whose target is an analog axis. Those must
        /// be read with the bipolar evaluator; asking the BUTTON evaluator
        /// about a stick axis is a category error.</summary>
        private static bool IsAxisTarget(string target)
            => target.EndsWith("AxisX", StringComparison.Ordinal)
            || target.EndsWith("AxisY", StringComparison.Ordinal)
            || target is "LeftTrigger" or "RightTrigger"
            || target.StartsWith("KbmMouse", StringComparison.Ordinal)
            || target.StartsWith("KbmScroll", StringComparison.Ordinal);

        /// <summary>Pushes the physical input an abstract descriptor names.
        /// The alias table is the engine's own, so this cannot drift from
        /// what the profile actually binds.</summary>
        private static bool Press(CustomInputState s, string abstractDescriptor, bool lowerHalf = false)
        {
            var pair = SourceCoercion.GamepadAliasTable
                .FirstOrDefault(a => "Gamepad " + a.Member == abstractDescriptor);
            if (pair.Canonical == null) return false;
            string c = pair.Canonical;

            if (c.StartsWith("Button ", StringComparison.Ordinal))
            {
                s.Buttons[int.Parse(c.Substring(7))] = true; return true;
            }
            if (c.StartsWith("Axis ", StringComparison.Ordinal))
            {
                int idx = int.Parse(c.Substring(5));
                // Triggers (2 and 5) rest at 0 and travel up. The thumb axes
                // are centred, so a wedge pushes one way or the other.
                s.Axis[idx] = idx is 2 or 5 ? 65535 : (lowerHalf ? 0 : 65535);
                return true;
            }
            if (c.StartsWith("POV 0 ", StringComparison.Ordinal))
            {
                s.Povs[0] = c.EndsWith("Up") ? 0
                          : c.EndsWith("Right") ? 9000
                          : c.EndsWith("Down") ? 18000
                          : 27000;
                return true;
            }
            return false;
        }

        private static IEnumerable<(StarterProfileInfo Info, MappingSet Set)> Profiles()
            => StarterProfileCatalog.All.Select(i => (i, i.Build().SlotMappingSets[0]));

        private static IEnumerable<MappingRow> BaseRows(MappingSet set)
            => set.Rows.Where(r => (r.LayerMask ?? "Base") == "Base");

        /// <summary>Evaluates a source the way the ENGINE would for this
        /// target class. Trigger targets are UNIPOLAR (rest 0, travel to 1);
        /// reading one as bipolar gives -1 at rest, which is correct for a
        /// bipolar lane and meaningless for a trigger.</summary>
        private static bool Asserted(CustomInputState state, MappingRow row, MappingSource src)
        {
            if (row.Target is "LeftTrigger" or "RightTrigger")
                return SourceCoercion.EvaluateForTriggerTarget(state, src, 0, DevGuid.ToString()) > 0.001f;
            if (IsAxisTarget(row.Target))
                return Math.Abs(SourceCoercion.EvaluateForBipolarAxisTarget(
                    state, src, 0, false, DevGuid.ToString())) > 0.001f;
            return SourceCoercion.EvaluateForButtonTarget(
                state, src, Threshold, 0, DevGuid.ToString());
        }

        // ── The headline: every authored binding actually fires ──────────

        /// <summary>
        /// For EVERY row of EVERY starter profile, push the physical input
        /// its first gamepad source names and assert the row evaluates
        /// non-zero. This is what a controller in hand would check, done
        /// against the real evaluators.
        ///
        /// <para>This is the test that would have caught the four dead
        /// modifier rows on arrival, independently of the target-set guard.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryAuthoredBinding_FiresWhenItsInputIsPushed()
        {
            var state = ArrangePad();
            var dead = new List<string>();
            int checkedRows = 0;

            foreach (var (info, set) in Profiles())
            {
                foreach (var row in BaseRows(set))
                {
                    var src = row.Sources.FirstOrDefault(s =>
                        s.Descriptor.StartsWith("Gamepad ", StringComparison.Ordinal));
                    if (src == null) continue;   // touchpad / gyro-only rows

                    Reset(state);
                    if (!Press(state, src.Descriptor, src.HalfAxis && src.Invert)) continue;
                    checkedRows++;

                    bool fired = Asserted(state, row, src);

                    if (!fired) dead.Add($"{info.Key}: {row.Target} <- {src.Descriptor}");
                }
            }

            Assert.True(checkedRows > 150, $"only {checkedRows} rows exercised; the harness is not covering the catalog");
            Assert.True(dead.Count == 0,
                "these authored bindings do not fire when their input is pushed:\n  " + string.Join("\n  ", dead));
        }

        /// <summary>Nothing may fire at rest. A binding that reads true with
        /// the pad untouched is worse than one that never fires, because it
        /// spams the game the moment the profile loads.</summary>
        [Fact]
        public void NothingFires_WhenThePadIsAtRest()
        {
            var state = ArrangePad();
            Reset(state);
            var stuck = new List<string>();

            foreach (var (info, set) in Profiles())
                foreach (var row in BaseRows(set))
                    foreach (var src in row.Sources.Where(s =>
                                 s.Descriptor.StartsWith("Gamepad ", StringComparison.Ordinal)))
                    {
                        if (Asserted(state, row, src))
                            stuck.Add($"{info.Key}: {row.Target} <- {src.Descriptor}");
                    }

            Assert.True(stuck.Count == 0,
                "these bindings are asserted with the pad at rest:\n  " + string.Join("\n  ", stuck));
        }

        // ── The specific claims the profiles make ───────────────────────

        /// <summary>WASD: pushing the left stick up types W and not S, and
        /// down types S and not W. The half-axis wedge is the single most
        /// error-prone thing in the catalog, since Invert is the half
        /// SELECTOR rather than a sign flip and getting it backwards would
        /// invert every movement key in three profiles.</summary>
        [Fact]
        public void Wasd_StickUpTypesW_AndStickDownTypesS()
        {
            var state = ArrangePad();
            var set = StarterProfileCatalog.Find("wasd").Build().SlotMappingSets[0];

            MappingSource SrcFor(byte vk) => BaseRows(set)
                .Single(r => r.Target == "KbmKey" + vk.ToString("X2"))
                .Sources.First(s => s.Descriptor.StartsWith("Gamepad ", StringComparison.Ordinal));

            var w = SrcFor(0x57); var s2 = SrcFor(0x53);
            var a = SrcFor(0x41); var d = SrcFor(0x44);

            Reset(state); state.Axis[1] = 0;           // stick UP (SDL: +Y is down)
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, w, Threshold, 0, DevGuid.ToString()), "up did not type W");
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, s2, Threshold, 0, DevGuid.ToString()), "up also typed S");

            Reset(state); state.Axis[1] = 65535;       // stick DOWN
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, s2, Threshold, 0, DevGuid.ToString()), "down did not type S");
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, w, Threshold, 0, DevGuid.ToString()), "down also typed W");

            Reset(state); state.Axis[0] = 0;           // stick LEFT
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, a, Threshold, 0, DevGuid.ToString()), "left did not type A");
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, d, Threshold, 0, DevGuid.ToString()), "left also typed D");

            Reset(state); state.Axis[0] = 65535;       // stick RIGHT
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, d, Threshold, 0, DevGuid.ToString()), "right did not type D");
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, a, Threshold, 0, DevGuid.ToString()), "right also typed A");
        }

        /// <summary>Desktop: A is Enter and B is Escape, each firing only for
        /// its own button. Proves the abstract alias really lands on the
        /// physical button the pad reports.</summary>
        [Fact]
        public void Desktop_AIsEnter_AndBIsEscape()
        {
            var state = ArrangePad();
            var set = StarterProfileCatalog.Find("desktop").Build().SlotMappingSets[0];

            var enter = BaseRows(set).Single(r => r.Target == "KbmKey0D").Sources[0];
            var esc = BaseRows(set).Single(r => r.Target == "KbmKey1B").Sources[0];

            Reset(state); state.Buttons[0] = true;     // A
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, enter, Threshold, 0, DevGuid.ToString()));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, esc, Threshold, 0, DevGuid.ToString()));

            Reset(state); state.Buttons[1] = true;     // B
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, esc, Threshold, 0, DevGuid.ToString()));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, enter, Threshold, 0, DevGuid.ToString()));
        }

        /// <summary>The load-bearing touchpad claim: on a controller with NO
        /// touchpad the pointer sources contribute nothing and the stick
        /// drives the cursor alone. Touchpads stays null on such a device,
        /// which is exactly the ordinary Xbox-pad case.</summary>
        [Fact]
        public void CursorRow_OnAPadWithNoTouchpad_IsDrivenByTheStickAlone()
        {
            var state = ArrangePad();
            Assert.True(state.Touchpads == null, "the synthetic pad should have no touchpad surface");

            foreach (var (info, set) in Profiles())
            {
                if (info.OutputType != VirtualControllerType.KeyboardMouse) continue;
                var row = BaseRows(set).Single(r => r.Target == "KbmMouseX");

                Reset(state);
                foreach (var p in row.Sources.Where(s => SourceCoercion.IsTouchpadPointerDescriptor(s.Descriptor)))
                    Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(state, p, 0, false, DevGuid.ToString()));

                var stick = row.Sources.First(s => s.Descriptor.StartsWith("Gamepad ", StringComparison.Ordinal));
                state.Axis[3] = 65535;   // right stick X hard over
                float v = SourceCoercion.EvaluateForBipolarAxisTarget(state, stick, 0, false, DevGuid.ToString());
                Assert.True(v > 0.5f, $"starter '{info.Key}' cursor did not follow the stick (got {v})");
            }
        }

        /// <summary>Fighting Games drives the D-pad and ONLY the D-pad, which
        /// is what the one-directional-surface rule requires. Pushing the
        /// left stick must move nothing.</summary>
        [Fact]
        public void FightingGames_RespondsToTheDpad_AndIgnoresTheStick()
        {
            var state = ArrangePad();
            var set = StarterProfileCatalog.Find("fighting").Build().SlotMappingSets[0];
            var left = BaseRows(set).Single(r => r.Target == "DPadLeft").Sources[0];

            Reset(state); state.Povs[0] = 27000;       // D-pad LEFT
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, left, Threshold, 0, DevGuid.ToString()),
                "D-pad left did not drive DPadLeft");

            Reset(state); state.Axis[0] = 0;           // left stick hard LEFT
            foreach (var row in BaseRows(set))
                foreach (var src in row.Sources)
                {
                    Assert.False(Asserted(state, row, src),
                        $"the left stick drove '{row.Target}', so Fighting Games has a second directional surface");
                }
        }

        /// <summary>Emulation mirrors the left stick onto the D-pad, because
        /// NES, SNES and Mega Drive cores carry no analog axes. Both the
        /// D-pad and the stick must reach the same target.</summary>
        [Fact]
        public void Emulation_LeftStickAndDpad_BothDriveTheDpad()
        {
            var state = ArrangePad();
            var set = StarterProfileCatalog.Find("emulation").Build().SlotMappingSets[0];
            var row = BaseRows(set).Single(r => r.Target == "DPadLeft");

            Reset(state); state.Povs[0] = 27000;
            Assert.Contains(row.Sources, s =>
                SourceCoercion.EvaluateForButtonTarget(state, s, Threshold, 0, DevGuid.ToString()));

            Reset(state); state.Axis[0] = 0;           // stick hard left
            Assert.Contains(row.Sources, s =>
                SourceCoercion.EvaluateForButtonTarget(state, s, Threshold, 0, DevGuid.ToString()));
        }

        /// <summary>Racing's steering curve must soften near centre and still
        /// reach full lock. A half-deflection reads BELOW half output, and a
        /// full deflection still reads full.</summary>
        [Fact]
        public void Racing_SteeringCurve_SoftensNearCentreAndStillReachesFullLock()
        {
            var state = ArrangePad();
            var set = StarterProfileCatalog.Find("racing").Build().SlotMappingSets[0];
            var steer = BaseRows(set).Single(r => r.Target == "LeftThumbAxisX").Sources[0];

            Reset(state);
            state.Axis[0] = AxisCenter + (65535 - AxisCenter) / 2;      // ~half right
            float half = SourceCoercion.EvaluateForBipolarAxisTarget(state, steer, 0, false, DevGuid.ToString());

            Reset(state);
            state.Axis[0] = 65535;                                       // full right
            float full = SourceCoercion.EvaluateForBipolarAxisTarget(state, steer, 0, false, DevGuid.ToString());

            Assert.True(full > 0.95f, $"full lock did not reach full output (got {full})");
            Assert.True(half < 0.5f, $"the curve did not soften near centre (half deflection gave {half})");
            Assert.True(half > 0.05f, $"the curve crushed mid travel (half deflection gave {half})");
        }

        /// <summary>Gyro Aim keeps the stick live alongside the gyro. Moving
        /// the stick with the pad still must still steer, or the profile has
        /// taken the stick away instead of adding to it.</summary>
        [Fact]
        public void GyroAim_LeavesTheStickLive()
        {
            var state = ArrangePad();
            var set = StarterProfileCatalog.Find("gyroaim").Build().SlotMappingSets[0];
            var row = BaseRows(set).Single(r => r.Target == "RightThumbAxisX");

            var stick = row.Sources.First(s => s.Descriptor.StartsWith("Gamepad ", StringComparison.Ordinal));
            Reset(state);
            state.Axis[3] = 65535;
            Assert.True(SourceCoercion.EvaluateForBipolarAxisTarget(state, stick, 0, false, DevGuid.ToString()) > 0.5f,
                "the right stick no longer steers on Gyro Aim");
        }
    }
}
