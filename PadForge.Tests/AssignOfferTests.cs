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

        // ── "new" is baseline membership, not a per-tick edge ──────────

        /// <summary>The state-driven rule reads "new" from the baseline
        /// registry set: a device present at Start is known, one absent is
        /// new. This is what makes a removed-then-reconnected device offer
        /// again (the owner's Switch 2 Pro case): it was gone at baseline, so
        /// its guid is not in the set, so it reads as new when it returns,
        /// and the offer is a standing condition rather than a missed edge.</summary>
        [Fact]
        public void Baseline_KnownIsNotNew_AbsentIsNew()
        {
            var baseline = new System.Collections.Generic.HashSet<Guid>();
            var carriedOver = Guid.NewGuid();
            baseline.Add(carriedOver);              // in the saved registry at Start
            var removedThenBack = Guid.NewGuid();   // user removed it before restart

            // A carried-over device: known, so the NewDevice prompt does not carry it,
            // but the EmptySlot prompt still can.
            Assert.False(InputService.AssignOfferDecision(
                isNew: false, online: true, offerNew: true, offerEmpty: false,
                slotHasDevices: false, eligible: true, alreadyOnSlot: false, dismissed: false));
            Assert.True(InputService.AssignOfferDecision(
                isNew: false, online: true, offerNew: false, offerEmpty: true,
                slotHasDevices: false, eligible: true, alreadyOnSlot: false, dismissed: false));

            // The removed-then-reconnected device: absent from baseline, so new,
            // so the NewDevice prompt carries it even on a slot that has devices.
            bool isNew = !baseline.Contains(removedThenBack);
            Assert.True(isNew);
            Assert.True(InputService.AssignOfferDecision(
                isNew, online: true, offerNew: true, offerEmpty: false,
                slotHasDevices: true, eligible: true, alreadyOnSlot: false, dismissed: false));
        }

        /// <summary>Offline devices never offer, whatever their newness.</summary>
        [Fact]
        public void Offline_NeverOffers()
        {
            Assert.False(InputService.AssignOfferDecision(
                isNew: true, online: false, offerNew: true, offerEmpty: true,
                slotHasDevices: false, eligible: true, alreadyOnSlot: false, dismissed: false));
        }

        // ── eligibility ────────────────────────────────────────────────

        [Theory]
        [InlineData(InputDeviceType.Keyboard, false)]
        [InlineData(InputDeviceType.Mouse, false)]
        [InlineData(InputDeviceType.Touchpad, false)]
        [InlineData(InputDeviceType.ConsumerControl, false)]
        [InlineData(InputDeviceType.KeyboardScreenKeys, false)]
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
            // Home's prompt (#574), the third switch of the card.
            Assert.True(d.AssignOfferHome);

            d.AssignOfferNewDevice = false;
            d.AssignOfferHome = false;
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(AppSettingsData));
            var sw = new StringWriter();
            ser.Serialize(sw, d);
            var back = (AppSettingsData)ser.Deserialize(new StringReader(sw.ToString()));
            Assert.False(back.AssignOfferNewDevice);
            Assert.True(back.AssignOfferEmptySlot);
            Assert.False(back.AssignOfferHome);

            // A file written before the switch existed reads as on.
            string older = System.Text.RegularExpressions.Regex.Replace(
                sw.ToString(), "<AssignOfferHome>false</AssignOfferHome>", string.Empty);
            Assert.DoesNotContain("AssignOfferHome", older);
            Assert.True(((AppSettingsData)ser.Deserialize(new StringReader(older))).AssignOfferHome);
        }

        // ── discussion #348: the open row keeps its editor ─────────────

        /// <summary>A selected Unassigned row became a plain mapping the
        /// moment its first source landed, and the trigger that swapped a
        /// plain row's cells for a one-line summary then collapsed the cells
        /// (Record, Clear, Invert, Half, Bidirectional) under the open
        /// details strip. A row has no cells now (#574): it is its line,
        /// and its options show under the line by selection alone, so no
        /// state of the row's sources can take them away. This holds the
        /// two facts that make it so. Source-text lock, since the row
        /// template has no in-process seam.</summary>
        [Fact]
        public void AnOpenRowsOptionsShowBySelectionAlone()
        {
            string xaml = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Views", "PadPage.xaml")));
            Assert.Contains("<DataGrid.RowDetailsVisibilityMode>VisibleWhenSelected</DataGrid.RowDetailsVisibilityMode>", xaml);

            int start = xaml.IndexOf("<ControlTemplate TargetType=\"DataGridRow\">", StringComparison.Ordinal);
            Assert.True(start > 0, "the mapping row's template was not found; re-anchor this lock");
            int end = xaml.IndexOf("</ControlTemplate>", start, StringComparison.Ordinal);
            string row = xaml.Substring(start, end - start);
            Assert.Matches(@"<DataGridDetailsPresenter Grid\.Row=""1""\s+Visibility=""\{TemplateBinding DetailsVisibility\}""/>", row);
            // Nothing in the row hides a part of it by what the row holds.
            Assert.DoesNotContain("DataGridCellsPresenter", row);
            Assert.DoesNotContain("IsTrivialDirect", row);
            Assert.DoesNotContain("HasAnySource", row);
        }
    }
}
