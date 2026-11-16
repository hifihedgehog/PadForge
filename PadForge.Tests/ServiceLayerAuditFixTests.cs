using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the service-layer findings in the 2026-09-15 audit.
    /// </summary>
    public class ServiceLayerAuditFixTests
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

        // C112: a device class that could never resolve.

        /// <summary>Every real 2015 Steam Controller resolves to its own
        /// gesture device class. The arm named a product id that exists
        /// nowhere else in this repository, so it never matched and every
        /// Steam Controller resolved as the wildcard, which means a gesture
        /// scoped to one could not match the hardware it was recorded on.</summary>
        [Theory]
        [InlineData((ushort)0x1101)]   // CHELL
        [InlineData((ushort)0x1102)]   // wired
        [InlineData((ushort)0x1105)]   // Bluetooth
        [InlineData((ushort)0x1106)]   // Bluetooth
        [InlineData((ushort)0x1142)]   // dongle
        public void EveryRealSteamControllerIsRecognized(ushort pid)
        {
            Assert.True(SteamHomeLedSetter.IsSteamController2015(0x28DE, pid));
        }

        /// <summary>The id the arm used to name is recognized by nothing, so
        /// it was never a real member of the family.</summary>
        [Fact]
        public void TheIdTheArmUsedToNameIsNotARealMember()
        {
            Assert.False(SteamHomeLedSetter.IsSteamController2015(0x28DE, 0x11FF));
            Assert.DoesNotContain("0x11FF", InputService());
        }

        /// <summary>The class resolver asks the shared predicate rather than
        /// carrying a fifth copy of the family list.</summary>
        [Fact]
        public void TheClassResolverUsesTheSharedPredicate()
        {
            Assert.Contains("SteamHomeLedSetter.IsSteamController2015(ud.VendorId, pid)", InputService());
        }

        // C87: a keyboard and mouse slot took an Xbox number.

        /// <summary>The slot ledger numbers a keyboard and mouse slot on its
        /// own series, the way the sidebar already does. Falling through to
        /// the default consumed an Xbox number, so every later Xbox card
        /// shifted up one and the same slot read differently on two pages.</summary>
        [Fact]
        public void AKeyboardAndMouseSlotHasItsOwnNumberSeries()
        {
            string src = InputService();
            Assert.Contains("midiCount = 0, kbmCount = 0, vrCount = 0", src);
            var m = Regex.Match(src,
                @"case VirtualControllerType\.KeyboardMouse:\s*\r?\n\s*kbmCount\+\+;\s*\r?\n\s*slot\.TypeInstanceLabel = LiveValueString\(kbmCount\);");
            Assert.True(m.Success, "the ledger still folds a keyboard and mouse slot into the Xbox series");
        }

        // C85: a held button read from an offline device.

        /// <summary>A failed read marks a device offline without clearing its
        /// state, so reading the record anyway resurrects whatever was held at
        /// the moment the read failed. The mapping path applies the online
        /// rule and records why; this provider did not.</summary>
        [Fact]
        public void TheHeldButtonProviderReadsOnlineDevicesOnly()
        {
            Assert.Contains("if (ud == null || !ud.IsOnline || ud.InputState == null) return false;",
                InputService());
        }

        // C84: the reported poll rate ignored a profile override.

        /// <summary>The engine reports the rate the loop is actually running
        /// at. A profile can override the global setting, and the resolved
        /// value lives on the manager, so reading the setting reported a rate
        /// the loop was not using and sized the gyro smoothing window from it.</summary>
        [Fact]
        public void TheReportedPollRateFollowsTheResolvedRate()
        {
            Assert.Contains("if (_inputManager != null) return _inputManager.PollingRateHz;", InputService());
        }

        // C96: a single-slot edit reset every slot's menu runtime.

        /// <summary>Pasting menus onto one slot, or copying a mapping set onto
        /// one slot, used the global reset, which clears every slot's menu
        /// contexts and drivers and drops any open overlay. The whole-profile
        /// swap keeps the global reset, which is what it is for.</summary>
        [Fact]
        public void ASingleSlotEditRetiresOnlyThatSlotsMenuRuntime()
        {
            string src = InputService();

            // The two single-slot edits: paste menus onto a slot, and copy a
            // whole mapping set onto a slot.
            Assert.Contains("_inputManagerStatic?.ClearMenuRuntimeForSlot(padIndex);", src);
            Assert.Contains("_inputManagerStatic?.ClearMenuRuntimeForSlot(targetSlot);", src);

            // Neither of them may reach for the global reset any more.
            int staticGlobal = Regex.Matches(src, @"_inputManagerStatic\?\.ResetMenuRuntime\(\);").Count;
            Assert.Equal(0, staticGlobal);

            // The whole-profile swap keeps it, which is what it is for, and
            // it is the only site left holding it.
            int anyGlobal = Regex.Matches(src, @"ResetMenuRuntime\(\);").Count;
            Assert.Equal(1, anyGlobal);
            Assert.Contains("_inputManager?.ResetMenuRuntime();", src);
        }

        // C105: gate legs escaped the suppression set.

        /// <summary>A per-source gate is read as input on the same device,
        /// whatever the source kind, and the suppression set never collected
        /// it, so a key used to gate a suppressed source stayed unconsumed and
        /// leaked to the foreground application.</summary>
        [Fact]
        public void TheSuppressionSetCollectsBothGateLegs()
        {
            string src = InputService();
            int i = src.IndexOf("Collect exactly the descriptors each kind reads", StringComparison.Ordinal);
            if (i < 0) i = src.IndexOf("Then exactly the descriptors each kind reads", StringComparison.Ordinal);
            Assert.True(i > 0, "the suppression collector is gone");

            string before = src.Substring(Math.Max(0, i - 900), Math.Min(900, i));
            Assert.Contains("AddDescriptor(src.GateDescriptor);", before);
            Assert.Contains("AddDescriptor(src.Gate2Descriptor);", before);
        }
    }
}
