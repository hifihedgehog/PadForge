using System;
using System.IO;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Assignment Prompts (Settings card) and the pad-page offer
    /// banner, plus the discussion #348 row-template lock.</summary>
    [Collection("SettingsManagerStatics")]
    public class AssignOfferTests
    {
        // ── the pure offer rule ────────────────────────────────────────

        [Theory]
        // NewDevice prompt: a never-seen device that came online, slot state irrelevant.
        [InlineData(true, true, true, false, true, true, false, false, true)]
        [InlineData(true, true, true, false, false, true, false, false, true)]
        // A known device does not trip the NewDevice prompt.
        [InlineData(false, true, true, false, false, true, false, false, false)]
        // EmptySlot prompt: any device when the open slot has nothing.
        [InlineData(false, true, false, true, false, true, false, false, true)]
        [InlineData(true, true, false, true, false, true, false, false, true)]
        // EmptySlot prompt stays quiet once the slot has a device.
        [InlineData(false, true, false, true, true, true, false, false, false)]
        // Both off: never.
        [InlineData(true, true, false, false, false, true, false, false, false)]
        // Not a connection edge this walk: never, even if new.
        [InlineData(true, false, true, true, false, true, false, false, false)]
        // Ineligible device type: never.
        [InlineData(true, true, true, true, false, false, false, false, false)]
        // Already on the slot: never.
        [InlineData(true, true, true, true, false, true, true, false, false)]
        // Dismissed for this slot: never.
        [InlineData(true, true, true, true, false, true, false, true, false)]
        public void Decision(bool isNew, bool cameOnline, bool offerNew, bool offerEmpty,
            bool slotHasDevices, bool eligible, bool alreadyOnSlot, bool dismissed, bool expected)
        {
            Assert.Equal(expected, InputService.AssignOfferDecision(
                isNew, cameOnline, offerNew, offerEmpty, slotHasDevices, eligible, alreadyOnSlot, dismissed));
        }

        /// <summary>Positive control for the table: with every guard open,
        /// the two prompts are independent (either one alone carries).</summary>
        [Fact]
        public void Decision_EitherPromptAloneCarries()
        {
            Assert.True(InputService.AssignOfferDecision(true, true, true, false, true, true, false, false));
            Assert.True(InputService.AssignOfferDecision(false, true, false, true, false, true, false, false));
            Assert.False(InputService.AssignOfferDecision(false, true, true, false, false, true, false, false));
            Assert.False(InputService.AssignOfferDecision(true, true, false, true, true, true, false, false));
        }

        // ── eligibility ────────────────────────────────────────────────

        [Theory]
        [InlineData(InputDeviceType.Keyboard, false)]
        [InlineData(InputDeviceType.Mouse, false)]
        [InlineData(InputDeviceType.Touchpad, false)]
        [InlineData(InputDeviceType.ConsumerControl, false)]
        [InlineData(InputDeviceType.Gamepad, true)]
        [InlineData(InputDeviceType.Joystick, true)]
        public void Eligibility_DesktopPeripheralsNeverOffered(int capType, bool expected)
        {
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = capType, ProductName = "X", InstanceName = "X" };
            Assert.Equal(expected, InputService.IsAssignOfferEligible(ud));
        }

        [Fact]
        public void Eligibility_NullOrEmptyGuid_Never()
        {
            Assert.False(InputService.IsAssignOfferEligible(null));
            Assert.False(InputService.IsAssignOfferEligible(new UserDevice { CapType = InputDeviceType.Gamepad }));
        }

        // ── the pad-side offer state ───────────────────────────────────

        [Fact]
        public void PadOffer_SetClearAndCommands()
        {
            var vm = new PadForge.ViewModels.PadViewModel(0);
            Assert.False(vm.HasAssignOffer);

            var g = Guid.NewGuid();
            vm.SetAssignOffer(g, "DualSense");
            Assert.True(vm.HasAssignOffer);
            Assert.Equal(g, vm.AssignOfferGuid);
            Assert.Contains("DualSense", vm.AssignOfferText);

            Guid accepted = Guid.Empty, dismissed = Guid.Empty;
            vm.AssignOfferAccepted += (_, x) => accepted = x;
            vm.AssignOfferDismissed += (_, x) => dismissed = x;

            vm.AcceptAssignOfferCommand.Execute(null);
            Assert.Equal(g, accepted);
            Assert.False(vm.HasAssignOffer);      // cleared before the event fires
            Assert.Equal(Guid.Empty, dismissed);

            vm.SetAssignOffer(g, "DualSense");
            vm.DismissAssignOfferCommand.Execute(null);
            Assert.Equal(g, dismissed);
            Assert.False(vm.HasAssignOffer);

            // Empty guid is a clear, not an offer.
            vm.SetAssignOffer(Guid.Empty, "x");
            Assert.False(vm.HasAssignOffer);
        }

        // ── the settings round-trip ────────────────────────────────────

        [Fact]
        public void Settings_DefaultOnAndRoundTrip()
        {
            var d = new AppSettingsData();
            Assert.True(d.AssignOfferNewDevice);
            Assert.True(d.AssignOfferEmptySlot);

            d.AssignOfferNewDevice = false;
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(AppSettingsData));
            var sw = new StringWriter();
            ser.Serialize(sw, d);
            var back = (AppSettingsData)ser.Deserialize(new StringReader(sw.ToString()));
            Assert.False(back.AssignOfferNewDevice);
            Assert.True(back.AssignOfferEmptySlot);
        }

        // ── discussion #348: the row keeps its cells while selected ────

        /// <summary>A selected Unassigned row becomes IsTrivialDirect the
        /// moment its first source lands, and nothing sets the expansion
        /// override on that path, so the compact-swap trigger collapsed
        /// the cells (Record, Clear, Invert, Half, Bidirectional) under the
        /// open details strip. The fix is the IsRowSelected=False condition
        /// the fan-in trigger already carried. Source-text lock, since the
        /// template trigger has no in-process seam.</summary>
        [Fact]
        public void TrivialRowCompactSwap_IsGatedOnNotSelected()
        {
            string xaml = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Views", "PadPage.xaml")));
            int i = xaml.IndexOf("<Condition Binding=\"{Binding IsTrivialDirect}\" Value=\"True\"/>", StringComparison.Ordinal);
            Assert.True(i > 0, "trivial-direct trigger not found; re-anchor this lock");
            int end = xaml.IndexOf("</MultiDataTrigger.Conditions>", i, StringComparison.Ordinal);
            string conditions = xaml.Substring(i, end - i);
            Assert.Contains("<Condition Binding=\"{Binding IsRowSelected}\" Value=\"False\"/>", conditions);
            Assert.Contains("<Condition Binding=\"{Binding IsExpandedOverride}\" Value=\"False\"/>", conditions);
        }
    }
}
