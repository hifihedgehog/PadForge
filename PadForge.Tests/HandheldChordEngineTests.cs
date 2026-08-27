using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Contract tests for the handheld chord state machine (issue #343):
    /// what the low-level hook is told to swallow or pass, when a button
    /// asserts and releases, when held prefixes replay, the Win mask, and
    /// the capture flow. Codes are VK codes; the modifiers use the
    /// left/right-specific codes the hook reports.
    /// </summary>
    public class HandheldChordEngineTests
    {
        private const int LCtrl = 0xA2, LWin = 0x5B, RCtrl = 0xA3, LMenu = 0xA4;
        private const int F17 = 0x80, F11 = 0x7A, F23 = 0x86, D = 0x44, L = 0x4C, O = 0x4F, A = 0x41;
        private const int MouseL = HandheldChordDefinition.MouseCode + 0;
        private const int MouseX2 = HandheldChordDefinition.MouseCode + 4;

        private static HandheldChordEngine Engine(params HandheldChordDefinition[] chords)
        {
            var e = new HandheldChordEngine();
            e.SetChords(chords);
            return e;
        }

        private static HandheldChordDefinition Chord(string name, int button, params int[] keys)
            => new() { Name = name, Button = button, Keys = keys };

        [Fact]
        public void SingleKeyChord_SwallowsAndAsserts_ReleasesOnUp()
        {
            var e = Engine(Chord("Custom Key Big", 1, F23));
            var changes = new List<(int, bool)>();
            e.ButtonChanged += (b, s) => changes.Add((b, s));

            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F23, true, 0));
            Assert.True(e.IsButtonDown(1));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F23, false, 10));
            Assert.False(e.IsButtonDown(1));
            Assert.Equal(new[] { (1, true), (1, false) }, changes);
            Assert.Empty(e.PendingReplays);
        }

        [Fact]
        public void ModifierChord_PassesModifiers_SwallowsCompletingKey_AsksWinMask()
        {
            // AYANEO "Custom Key Big": RControl + LWin + F17.
            var e = Engine(Chord("Custom Key Big", 1, RCtrl, LWin, F17));

            Assert.Equal(ChordDecision.Pass, e.OnEvent(RCtrl, true, 0));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(LWin, true, 1));
            Assert.False(e.WinMaskRequested);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F17, true, 2));
            Assert.True(e.IsButtonDown(1));
            Assert.True(e.WinMaskRequested, "a completed chord holding Win must mask the Start menu");

            // Release order as firmware types it: F17, LWin, RCtrl.
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F17, false, 3));
            Assert.False(e.IsButtonDown(1));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(LWin, false, 4));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(RCtrl, false, 5));
            Assert.Empty(e.PendingReplays);
        }

        [Fact]
        public void WinD_ChordSwallowsD_SoTheDesktopDoesNotMinimize()
        {
            var e = Engine(Chord("Custom Key Small", 2, LWin, D));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(LWin, true, 0));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(D, true, 1));
            Assert.True(e.IsButtonDown(2));
            Assert.True(e.WinMaskRequested);
        }

        [Fact]
        public void PrefixKey_IsHeld_ThenConsumedWhenChordCompletes()
        {
            // GPD back button: F11 + L. F11 arrives first and must not reach the game.
            var e = Engine(Chord("Bottom button left", 3, F11, L));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F11, true, 0));
            Assert.True(e.HasHeldKeys);
            Assert.False(e.IsButtonDown(3));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(L, true, 5));
            Assert.True(e.IsButtonDown(3));
            Assert.False(e.HasHeldKeys);
            Assert.Empty(e.PendingReplays);
            // Both ups swallowed: their downs were.
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(L, false, 20));
            Assert.False(e.IsButtonDown(3));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F11, false, 21));
        }

        [Fact]
        public void PrefixKey_ReplaysOnHoldTimeout_SoATypedF11StillLands()
        {
            var e = Engine(Chord("Bottom button left", 3, F11, L));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F11, true, 0));
            e.Tick(HandheldChordEngine.HoldMs - 1);
            Assert.Empty(e.PendingReplays);
            e.Tick(HandheldChordEngine.HoldMs);
            Assert.Equal(new[] { (F11, true) }, e.PendingReplays);
            Assert.False(e.HasHeldKeys);
            // The later up passes normally: the down was replayed, so the OS
            // has a matching down to pair it with.
            Assert.Equal(ChordDecision.Pass, e.OnEvent(F11, false, 200));
        }

        [Fact]
        public void PrefixKey_TappedBeforeCompletion_ReplaysDownAndUp()
        {
            var e = Engine(Chord("Bottom button left", 3, F11, L));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F11, true, 0));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F11, false, 20));
            Assert.Equal(new[] { (F11, true), (F11, false) }, e.PendingReplays);
        }

        [Fact]
        public void ForeignKey_EndsThePrefix_AndPasses()
        {
            var e = Engine(Chord("Bottom button left", 3, F11, L));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F11, true, 0));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(A, true, 5));
            Assert.Equal(new[] { (F11, true) }, e.PendingReplays);
            Assert.False(e.IsButtonDown(3));
        }

        [Fact]
        public void KeyNotInAnyChord_PassesUntouched()
        {
            var e = Engine(Chord("Custom Key Big", 1, RCtrl, LWin, F17));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(A, true, 0));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(A, false, 1));
            Assert.Empty(e.PendingReplays);
        }

        [Fact]
        public void TwoChordsSharingAPrefix_ResolveByTheCompletingKey()
        {
            // AYANEO top-left (Ctrl+Win+F15) versus top-right (Ctrl+Win+F16).
            var e = Engine(Chord("Top Left", 3, LCtrl, LWin, 0x7E), Chord("Top Right", 4, LCtrl, LWin, 0x7F));
            e.OnEvent(LCtrl, true, 0);
            e.OnEvent(LWin, true, 1);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(0x7F, true, 2));
            Assert.False(e.IsButtonDown(3));
            Assert.True(e.IsButtonDown(4));
        }

        [Fact]
        public void AllModifierChord_AssertsOnTheLastModifier()
        {
            // OneXPlayer Orange: LControl + LWin + LMenu, all modifiers.
            var e = Engine(Chord("Orange", 5, LCtrl, LWin, LMenu));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(LCtrl, true, 0));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(LWin, true, 1));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(LMenu, true, 2));
            Assert.True(e.IsButtonDown(5));
            Assert.True(e.WinMaskRequested);
        }

        [Fact]
        public void MouseChord_HoldsTheFirstButton_ConsumesOnCompletion()
        {
            // GPD / Ayn menu key: LButton + XButton2 typed together.
            var e = Engine(Chord("Menu", 6, MouseL, MouseX2));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(MouseL, true, 0));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(MouseX2, true, 1));
            Assert.True(e.IsButtonDown(6));
            Assert.Empty(e.PendingReplays);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(MouseX2, false, 30));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(MouseL, false, 31));
            Assert.False(e.IsButtonDown(6));
        }

        [Fact]
        public void AutoRepeat_KeepsTheFirstDecision()
        {
            var e = Engine(Chord("Custom Key Big", 1, F23));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F23, true, 0));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F23, true, 500)); // typematic repeat
            Assert.True(e.IsButtonDown(1));
        }

        [Fact]
        public void SetChords_DroppingAnActiveChord_ReleasesItsButton()
        {
            var e = Engine(Chord("Custom Key Big", 1, F23));
            e.OnEvent(F23, true, 0);
            Assert.True(e.IsButtonDown(1));
            e.SetChords(Array.Empty<HandheldChordDefinition>());
            Assert.False(e.IsButtonDown(1));
            Assert.False(e.HasChords);
        }

        [Fact]
        public void Capture_SwallowsEverything_ReportsPressedSetOnFullRelease()
        {
            var e = Engine(Chord("existing", 1, F23));
            int[] captured = null;
            e.CaptureCompleted += c => captured = c;
            e.BeginCapture(0);
            Assert.True(e.IsCapturing);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(RCtrl, true, 1));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(LWin, true, 2));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(O, true, 3));
            Assert.Null(captured);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(O, false, 10));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(LWin, false, 11));
            Assert.Null(captured);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(RCtrl, false, 12));
            Assert.Equal(new[] { RCtrl, LWin, O }, captured);
            Assert.False(e.IsCapturing);
            // The existing chord was not disturbed.
            Assert.False(e.IsButtonDown(1));
        }

        [Fact]
        public void Capture_PassesAnUpWhoseDownItNeverSaw_SoNoKeyStaysDownInTheOs()
        {
            // The Enter that clicked Start Learning went down before the
            // capture began; its up must reach the OS.
            var e = Engine();
            e.BeginCapture(0);
            Assert.Equal(ChordDecision.Pass, e.OnEvent(0x0D, false, 1));
            Assert.True(e.IsCapturing);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F23, true, 2));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F23, false, 3));
            Assert.False(e.IsCapturing);
        }

        [Fact]
        public void PrefixIsOrderAware_DAloneIsTypingForAWinDChord()
        {
            // WASD must never pay the hold: D is the LAST key of the learned
            // order, so D alone is not a prefix. Win alone is (and passes,
            // being a modifier).
            var e = Engine(Chord("Home", 1, LWin, D));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(D, true, 0));
            Assert.Empty(e.PendingReplays);
            Assert.False(e.HasHeldKeys);
            Assert.Equal(ChordDecision.Pass, e.OnEvent(D, false, 5));
            // The firmware order still completes.
            Assert.Equal(ChordDecision.Pass, e.OnEvent(LWin, true, 10));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(D, true, 11));
            Assert.True(e.IsButtonDown(1));
        }

        [Fact]
        public void PrefixInTheWrongOrder_IsNotHeld()
        {
            // GPD-style [F11, L]: L first is typing, F11 first is the prefix.
            var e = Engine(Chord("L4", 1, F11, L));
            Assert.Equal(ChordDecision.Pass, e.OnEvent(L, true, 0));
            Assert.False(e.HasHeldKeys);
            e.OnEvent(L, false, 1);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F11, true, 2));
            Assert.True(e.HasHeldKeys);
        }

        [Fact]
        public void SetChords_KeepsAnActiveChordRedefinedIdentically_ReleasesARemovedOneWithTheEvent()
        {
            var e = Engine(Chord("a", 1, F23), Chord("b", 2, F17));
            var changes = new List<(int, bool)>();
            e.ButtonChanged += (b, s) => changes.Add((b, s));
            e.OnEvent(F23, true, 0);
            e.OnEvent(F17, true, 1);
            changes.Clear();
            // The registry hands out fresh objects on every change.
            e.SetChords(new[] { Chord("a renamed", 1, F23) });
            Assert.True(e.IsButtonDown(1));
            Assert.False(e.IsButtonDown(2));
            Assert.Equal(new[] { (2, false) }, changes);
        }

        [Fact]
        public void Reset_ReleasesEverything_WithEvents_AndForgetsHeldPrefixes()
        {
            var e = Engine(Chord("a", 1, F23), Chord("l4", 2, F11, L));
            var changes = new List<(int, bool)>();
            e.ButtonChanged += (b, s) => changes.Add((b, s));
            e.OnEvent(F23, true, 0);
            e.OnEvent(F11, true, 1); // held prefix
            changes.Clear();
            e.Reset();
            Assert.False(e.IsButtonDown(1));
            Assert.Equal(new[] { (1, false) }, changes);
            Assert.False(e.HasHeldKeys);
            Assert.False(e.HasPendingWork);
            // A fresh F23 press is a fresh press, not an auto-repeat.
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(F23, true, 2));
            Assert.True(e.IsButtonDown(1));
        }

        [Fact]
        public void AltChord_AsksForTheMaskToo()
        {
            var e = Engine(Chord("alt f13", 1, LMenu, 0x7C));
            e.OnEvent(LMenu, true, 0);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(0x7C, true, 1));
            Assert.True(e.TakeWinMask());
            Assert.False(e.TakeWinMask());
        }

        [Fact]
        public void SupersetChord_WinsOverItsSubset_WhenBothCompleteAtOnce()
        {
            var e = Engine(Chord("Home", 1, LWin, D), Chord("Alt Home", 2, LCtrl, LWin, D));
            e.OnEvent(LCtrl, true, 0);
            e.OnEvent(LWin, true, 1);
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(D, true, 2));
            Assert.True(e.IsButtonDown(2));
            Assert.False(e.IsButtonDown(1));
        }

        [Fact]
        public void Capture_IdleTimeout_CompletesEmpty()
        {
            var e = Engine();
            int[] captured = null;
            e.CaptureCompleted += c => captured = c;
            e.BeginCapture(0);
            e.Tick(HandheldChordEngine.CaptureIdleTimeoutMs - 1);
            Assert.Null(captured);
            e.Tick(HandheldChordEngine.CaptureIdleTimeoutMs);
            Assert.NotNull(captured);
            Assert.Empty(captured);
            Assert.False(e.IsCapturing);
        }

        [Fact]
        public void CopyButtonState_MirrorsActiveChords()
        {
            var e = Engine(Chord("a", 7, F23), Chord("b", 9, F17));
            e.OnEvent(F17, true, 0);
            var dest = new bool[PadForge.Engine.CustomInputState.MaxButtons];
            e.CopyButtonState(dest);
            Assert.False(dest[7]);
            Assert.True(dest[9]);
        }
    }

    /// <summary>
    /// Replay tests for the vendor-report learner (issue #343). Each test is
    /// a byte capture standing in for a device: the Legion Go paddle mask at
    /// byte 20 next to a moving IMU, the GPD Win 5 back buttons, the ROG
    /// Ally value-per-press stream, and the Legion report shifted by two.
    /// </summary>
    public class VendorReportLearnerTests
    {
        private static byte[] Report(int len, params (int idx, byte val)[] set)
        {
            var b = new byte[len];
            foreach (var (i, v) in set) b[i] = v;
            return b;
        }

        [Fact]
        public void NoiseMask_MarksOnlyBitsThatMovedWhileIdle()
        {
            var idle = new List<byte[]>
            {
                Report(64, (35, 0x10), (36, 0x20)),
                Report(64, (35, 0x11), (36, 0x20)),
                Report(64, (35, 0x13), (36, 0x21)),
            };
            var mask = VendorReportLearner.NoiseMask(idle);
            Assert.Equal(0x03, mask[35]);
            Assert.Equal(0x01, mask[36]);
            Assert.Equal(0x00, mask[20]);
        }

        [Fact]
        public void LegionPaddle_FoundAsOneBit_WhileImuBytesMove()
        {
            // Byte 20 bit 0x80 = Y1 per the Legion report; bytes 35..59 = IMU.
            var idle0 = Report(64, (35, 0x10), (41, 0x7F));
            var idleSamples = new List<byte[]> { idle0, Report(64, (35, 0x12), (41, 0x70)), Report(64, (35, 0x1F), (41, 0x01)) };
            var noise = VendorReportLearner.NoiseMask(idleSamples);
            var press = new List<byte[]> { Report(64, (20, 0x80), (35, 0x33), (41, 0x02)), Report(64, (20, 0x80), (35, 0x0A), (41, 0x55)) };
            var release = new List<byte[]> { Report(64, (35, 0x44), (41, 0x66)) };

            var found = VendorReportLearner.Learn(idle0, noise, press, release);

            var c = Assert.Single(found);
            Assert.Equal(20, c.ByteIndex);
            Assert.Equal(0x80, c.Mask);
            Assert.Equal(VendorButtonKind.Bit, c.Kind);
        }

        [Fact]
        public void Tap_InsideThePressWindow_IsLearnedAsABit()
        {
            // Bench 2026-08-25: a tap-only key leaves pressed samples followed
            // by released ones inside the press window; a hold is not required.
            var idle = Report(64, (20, 0x00));
            var noise = new byte[64];
            var press = new List<byte[]> { Report(64, (20, 0x00)), Report(64, (20, 0x80)), Report(64, (20, 0x80)), Report(64, (20, 0x00)) };
            var release = new List<byte[]> { Report(64, (20, 0x00)) };
            var found = VendorReportLearner.Learn(idle, noise, press, release);
            var c = Assert.Single(found);
            Assert.Equal(20, c.ByteIndex);
            Assert.Equal(0x80, c.Mask);
            Assert.Equal(0x80, c.Value);
            Assert.Equal(VendorButtonKind.Bit, c.Kind);
        }

        [Fact]
        public void AllyTap_WithItsReleaseCodeInsideThePressWindow_LearnsThePressCode()
        {
            // Event-style firmware: 167 on press, 168 on release, both inside
            // the press window when the user taps. The first differing value
            // is the press code; the release code never masquerades as it.
            var idle = Report(64);
            var noise = new byte[64];
            var press = new List<byte[]> { Report(64, (1, 167)), Report(64, (1, 168)) };
            var found = VendorReportLearner.Learn(idle, noise, press, new List<byte[]>());
            var c = Assert.Single(found);
            Assert.Equal(1, c.ByteIndex);
            Assert.Equal(VendorButtonKind.Value, c.Kind);
            Assert.Equal(167, c.Value);
        }

        [Fact]
        public void BitThatDoesNotReturnOnRelease_IsNotAButton()
        {
            var idle0 = Report(16);
            var press = new List<byte[]> { Report(16, (3, 0x04)) };
            var release = new List<byte[]> { Report(16, (3, 0x04)) }; // stays set: a latch, not a button
            Assert.Empty(VendorReportLearner.Learn(idle0, VendorReportLearner.NoiseMask(new List<byte[]> { idle0 }), press, release));
        }

        [Fact]
        public void GpdWin5_SeveralBitsRisingTogether_IsLearnedAsAValue()
        {
            // HC reads (report.Data[9] & 0x69) != 0 for R4: the three bits rise
            // together. More than one bit in a byte is a code, learned as exact
            // equality, so a neighboring code never reads as this button.
            var idle0 = Report(16);
            var press = new List<byte[]> { Report(16, (9, 0x69)), Report(16, (9, 0x69)) };
            var release = new List<byte[]> { Report(16) };
            var found = VendorReportLearner.Learn(idle0, VendorReportLearner.NoiseMask(new List<byte[]> { idle0 }), press, release);
            var c = Assert.Single(found);
            Assert.Equal(9, c.ByteIndex);
            Assert.Equal(VendorButtonKind.Value, c.Kind);
            Assert.Equal(0x69, c.Value);
            var def = new VendorButtonDefinition { ByteIndex = 9, Value = 0x69, Kind = VendorButtonKind.Value };
            Assert.True(def.Evaluate(Report(16, (9, 0x69))));
            Assert.False(def.Evaluate(Report(16, (9, 0x01))), "a partial pattern must not read as pressed");
            Assert.False(def.Evaluate(Report(16, (9, 0x6B))), "a sibling code must not read as pressed");
        }

        [Fact]
        public void TwoSingleBitButtons_InOneByte_StayBits()
        {
            // Legion Y1 (0x80) learned alone: a single bit stays a mask, so a
            // later press of Y1 together with Y2 (0xC0) still reads Y1 as down.
            var idle0 = Report(64);
            var press = new List<byte[]> { Report(64, (20, 0x80)) };
            var release = new List<byte[]> { Report(64) };
            var c = Assert.Single(VendorReportLearner.Learn(idle0, VendorReportLearner.NoiseMask(new List<byte[]> { idle0 }), press, release));
            Assert.Equal(VendorButtonKind.Bit, c.Kind);
            var def = new VendorButtonDefinition { ByteIndex = 20, Mask = 0x80, Value = 0x80, Kind = VendorButtonKind.Bit };
            Assert.True(def.Evaluate(Report(64, (20, 0xC0))));
        }

        [Fact]
        public void AllyStyle_ValuePerEvent_FoundAsValueCandidate()
        {
            // Report byte 1 carries a key code (166) during the press, 0 otherwise.
            var idle0 = Report(4, (0, 0x5A));
            var press = new List<byte[]> { Report(4, (0, 0x5A), (1, 166)), Report(4, (0, 0x5A), (1, 166)) };
            var release = new List<byte[]> { Report(4, (0, 0x5A)) };
            var found = VendorReportLearner.Learn(idle0, VendorReportLearner.NoiseMask(new List<byte[]> { idle0 }), press, release);
            var c = Assert.Single(found);
            Assert.Equal(VendorButtonKind.Value, c.Kind);
            Assert.Equal(1, c.ByteIndex);
            Assert.Equal(166, c.Value);
            var def = new VendorButtonDefinition { ReportId = 0x5A, ByteIndex = 1, Value = 166, Kind = VendorButtonKind.Value };
            Assert.True(def.Evaluate(Report(4, (0, 0x5A), (1, 166))));
            Assert.False(def.Evaluate(Report(4, (0, 0x5A), (1, 56))));
            Assert.False(def.Evaluate(Report(4, (0, 0x5B), (1, 166))), "another report id never matches");
        }

        [Fact]
        public void MisalignedReport_IsLearnedWhereItLives()
        {
            // The Legion firmware sometimes shifts the whole report by two
            // bytes. The learner never assumes an offset, so the paddle is
            // found at 18 on such a unit and the definition reads it there.
            var idle0 = Report(64);
            var press = new List<byte[]> { Report(64, (18, 0x80)) };
            var release = new List<byte[]> { Report(64) };
            var c = Assert.Single(VendorReportLearner.Learn(idle0, VendorReportLearner.NoiseMask(new List<byte[]> { idle0 }), press, release));
            Assert.Equal(18, c.ByteIndex);
        }

        [Fact]
        public void TwoButtonsPressedTogether_ReadAsOneCode_TheUserPressesAgain()
        {
            // Y1 and Y2 at once flip two bits in byte 20. The learner cannot
            // tell two buttons from one two-bit code, so it records the code;
            // the dialog shows it and the user learns each paddle alone.
            var idle0 = Report(64);
            var press = new List<byte[]> { Report(64, (20, 0xC0)) };
            var release = new List<byte[]> { Report(64) };
            var found = VendorReportLearner.Learn(idle0, VendorReportLearner.NoiseMask(new List<byte[]> { idle0 }), press, release);
            var c = Assert.Single(found);
            Assert.Equal(VendorButtonKind.Value, c.Kind);
            Assert.Equal(0xC0, c.Value);
        }

        [Fact]
        public void NoisyByte_NeverBecomesAButton_EvenIfItChangedOnPress()
        {
            var idleSamples = new List<byte[]> { Report(8, (2, 0x01)), Report(8, (2, 0x02)), Report(8, (2, 0x03)) };
            var noise = VendorReportLearner.NoiseMask(idleSamples);
            var press = new List<byte[]> { Report(8, (2, 0x00)) };
            var release = new List<byte[]> { Report(8, (2, 0x01)) };
            Assert.Empty(VendorReportLearner.Learn(idleSamples[0], noise, press, release));
        }
    }

    public class MachineIdentityTests
    {
        [Fact]
        public void DisplayName_PrefersFamilyOverBareModelCode()
        {
            var id = new MachineIdentity { Manufacturer = "LENOVO", ProductName = "83RU", Family = "Legion Pro 7 16AFR10H" };
            Assert.Equal("Legion Pro 7 16AFR10H", id.DisplayName);
            Assert.Equal("LENOVO|83RU", id.Key);
        }

        [Fact]
        public void DisplayName_KeepsAWordyProductName()
        {
            var id = new MachineIdentity { Manufacturer = "AYANEO", ProductName = "AIR 1S", Family = "AIR" };
            Assert.Equal("AIR 1S", id.DisplayName);
        }
    }
}
