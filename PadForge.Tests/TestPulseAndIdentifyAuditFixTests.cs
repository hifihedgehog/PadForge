using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the test-pulse and Identify findings in the 2026-09-15
    /// audit. These are contracts against the source, because the lanes drive
    /// real devices through timers a test cannot observe.
    /// </summary>
    public class TestPulseAndIdentifyAuditFixTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string InputService() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "InputService.cs"));

        // C108: an all-device pulse inherited the previous pulse's filter.

        /// <summary>Both test lanes write the slot's single target filter, and
        /// an empty guid is what every reader treats as "all devices". Writing
        /// it only for a concrete device meant a slot-wide pulse fired while an
        /// earlier targeted pulse's filter was still standing, so it reached one
        /// device until that older pulse's own timer expired.</summary>
        [Fact]
        public void EveryTestPulseWritesItsOwnTargetFilter()
        {
            string src = InputService();

            // Neither lane may narrow the write to a concrete guid any more.
            int conditional = Regex.Matches(src,
                @"if \(deviceGuid\.HasValue && deviceGuid\.Value != Guid\.Empty\)\s*\r?\n\s*_inputManager\.TestRumbleTargetGuid").Count;
            Assert.Equal(0, conditional);

            // Both lanes write it unconditionally, empty guid included.
            int unconditional = Regex.Matches(src,
                @"_inputManager\.TestRumbleTargetGuid\[padIndex\] =\s*\r?\n\s*deviceGuid\.HasValue \? deviceGuid\.Value : Guid\.Empty;").Count;
            Assert.Equal(2, unconditional);
        }

        /// <summary>The consumer really does read an empty guid as no filter,
        /// so writing it is the way to say "every device on the slot".</summary>
        [Fact]
        public void AnEmptyTargetGuidMeansEveryDevice()
        {
            string step2 = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs"));
            Assert.Contains("if (targetGuid != Guid.Empty && targetGuid != ud.InstanceGuid)", step2);
        }

        // C109: the directional block could latch on forever.

        /// <summary>The directional block is written by the main lane alone, so
        /// it needs its own generation. Sitting on the slot-wide counter meant
        /// an impulse pulse bumped that counter without owning a directional
        /// field, the main timer returned before its clear, and no other timer
        /// performs one. An Extended slot then held a constant steering force
        /// and read as game-driven, which silences the user's own Constant
        /// Force.</summary>
        [Fact]
        public void TheDirectionalBlockHasItsOwnGeneration()
        {
            string src = InputService();

            Assert.Contains("private const int PulseFieldDirectional = 4;", src);
            Assert.Contains("new long[InputManager.MaxPads, 5];", src);
            Assert.Contains(
                "long myDirGen = (isExtended && (left != right))",
                src);

            // The clear is gated on that generation, not the slot's.
            var gated = Regex.Match(src,
                @"if \(isExtended && \(left != right\)\s*\r?\n\s*&& _testPulseMotorGeneration\[padIndex, PulseFieldDirectional\] == myDirGen\)\s*\r?\n\s*\{\s*\r?\n\s*vib\.HasDirectionalData = false;");
            Assert.True(gated.Success, "the directional clear is not gated on its own generation");
        }

        /// <summary>The clear also has to sit above the slot-wide return, or
        /// the gate it just gained never gets evaluated.</summary>
        [Fact]
        public void TheDirectionalClearRunsBeforeTheSlotWideReturn()
        {
            string src = InputService();
            int clear = src.IndexOf("vib.HasDirectionalData = false;", StringComparison.Ordinal);
            Assert.True(clear > 0, "the directional clear is gone");

            int slotGate = src.IndexOf("if (_testPulseGeneration[padIndex] != myGen) return;", clear,
                StringComparison.Ordinal);
            Assert.True(slotGate > clear,
                "the slot-wide return still sits above the directional clear");
        }

        /// <summary>The stale field the leak strands is the one the evaluator
        /// reads to decide the slot is game-driven, which is why a latched
        /// value suppresses the user's own force rather than just looking
        /// wrong.</summary>
        [Fact]
        public void ALatchedDirectionalFlagWouldSuppressTheUsersOwnForce()
        {
            string eval = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.Engine", "Common", "ConstantForceEvaluator.cs"));
            Assert.Contains("|| raw.HasDirectionalData", eval);
        }

        // C110: Identify had no writer for Padix converters.

        /// <summary>Every SDL rumble call is inert for the Padix PlayStation
        /// converters by design, because their motors ride a 9-byte report
        /// PadForge writes itself. The unowned Identify train called SDL
        /// directly, so the button was offered and nothing moved.</summary>
        [Fact]
        public void IdentifyWritesPadixConvertersThroughTheirOwnReport()
        {
            string src = InputService();
            int i = src.IndexOf("public void IdentifyDevice(Guid instanceGuid)", StringComparison.Ordinal);
            Assert.True(i > 0, "IdentifyDevice is gone");
            string body = src.Substring(i, Math.Min(6000, src.Length - i));

            Assert.Contains("PadixConverterIdentity", body);
            Assert.Contains("PadixConverterRawHidWriter.Write(", body);
            // The unowned lane leaves the motor snapshot alone. Recording each
            // buzz marked the row active, and Step 2 sends a row with no slot
            // its final zero on the next poll, which cut every pulse to about
            // a millisecond, a Bliss-Box port's (#469) included.
            Assert.DoesNotContain("TryRecordMotorSnapshot(level, level)", body);
            Assert.Contains("BlissBoxRuntime.SetRumble(ud.DevicePath, level, level);", body);

            // The SDL calls survive for every other family.
            Assert.Contains("else if (level != 0) dev.SetRumble(level, level);", body);
            Assert.Contains("else dev.StopRumble();", body);
        }

        /// <summary>The premise holds: the wrapper really does refuse to rumble
        /// that family, so an Identify built on it could not have worked.</summary>
        [Fact]
        public void TheWrapperRefusesToRumbleAPadixConverter()
        {
            string wrap = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.Engine", "Common", "SdlDeviceWrapper.cs"));
            var m = Regex.Match(wrap,
                @"if \(PadixConverterIdentity\.IsPlayStationConverter\(VendorId, ProductId\)\)\s*\r?\n\s*return false;");
            Assert.True(m.Success, "the wrapper no longer refuses the converter family");
        }

        // C111: the pulse train held one route across seconds of awaits.

        /// <summary>Identify resolves the device's slot once and then pulses for
        /// seconds. A reassignment mid-train left the remaining pulses writing
        /// the old slot's target filter, and that filter named a guid nothing on
        /// the slot carried, so the slot's real rumble went silent for the
        /// filter's lifetime.</summary>
        [Fact]
        public void TheIdentifyTrainRevalidatesItsRouteEveryPulse()
        {
            string src = InputService();
            int i = src.IndexOf("public void IdentifyDevice(Guid instanceGuid)", StringComparison.Ordinal);
            Assert.True(i > 0, "IdentifyDevice is gone");
            // To the next member, not a fixed window: a fixed length lost the
            // unowned lane's check once the Bliss-Box branch (#469) grew the
            // method past it.
            int next = src.IndexOf("\n        public ", i + 1, StringComparison.Ordinal);
            string body = src.Substring(i, (next > i ? next : src.Length) - i);

            Assert.Contains("int ResolvePad()", body);
            Assert.Contains("int pad = ResolvePad();", body);

            // Mapped lane: the slot must still be this device's slot.
            Assert.Contains("if (cur == null || !cur.IsOnline || ResolvePad() != pad) return;", body);

            // Unowned lane: a device that gained a slot now has a sole writer,
            // so this lane stops writing it directly.
            Assert.Contains("if (cur == null || !cur.IsOnline || ResolvePad() >= 0) return;", body);
        }

        /// <summary>The unowned lane's bail sits where the motors are already at
        /// zero, so stopping the train can never strand one spinning.</summary>
        [Fact]
        public void TheUnownedLaneOnlyBailsWithTheMotorsAtRest()
        {
            string src = InputService();
            var m = Regex.Match(src,
                @"Buzz\(0\);\s*\r?\n\s*await [^\r\n]+Delay\(200\)[^\r\n]+\r?\n(\s*//[^\r\n]*\r?\n)*\s*var cur = FindUserDevice\(instanceGuid\);\s*\r?\n\s*if \(cur == null \|\| !cur\.IsOnline \|\| ResolvePad\(\) >= 0\) return;");
            Assert.True(m.Success,
                "the unowned lane's revalidation no longer follows a zero write");
        }
    }
}
