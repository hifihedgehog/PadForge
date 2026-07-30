using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The CLASS of defect behind the 2026-07-28 profile clobber, not the one
    /// instance of it. Two rules, both of which the shipped code broke:
    ///
    /// <para>1. While a caller has swapped the domain mapping sets and has not
    /// yet reconciled the grids, NOTHING may push the grids back into the
    /// domain. The grids still describe the OUTGOING state. The flag for this
    /// existed and was correct, but it was a private field consulted at ONE of
    /// the writer's four call sites, and the push that actually fires during
    /// ApplyProfile arrives through a fifth path nobody had enumerated:
    /// UpdatePadDeviceInfo rebuilds the pad device lists, that changes the
    /// selected device, and OnSelectedDeviceChanged pushes. Guarding a writer
    /// at its callers is only ever as good as the caller list, so these tests
    /// pin the guard INSIDE the writer, where a future caller inherits it.</para>
    ///
    /// <para>2. A slot the incoming state deliberately left null must not be
    /// RESURRECTED. The writer opens with
    /// <c>sets[slot] ?? (sets[slot] = new MappingSet())</c>, so a stray push
    /// did not merely overwrite rows, it recreated a slot an authored-empty
    /// profile owned zero of and filled it from the outgoing profile's grid.
    /// Null means "this state authored nothing here", and a lazy-create on a
    /// write path silently converts that into an inherited-full which then
    /// persists as if the user had authored it.</para>
    ///
    /// <para>Every test here is mutation-proved against the guard: disabling
    /// the early return in PushUiExtraSourcesIntoSlotMappingSets turns them
    /// red. The transition matrix covers every entry point that swaps domain
    /// state, so a new one wired without the guard fails on arrival rather
    /// than after a release.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DomainSwapClobberTests : IDisposable
    {
        private static readonly Guid OutgoingPad = new("32462c1a-3538-3ea7-4ed1-692056f86c4b");
        private static readonly Guid IncomingPad = new("77777777-7777-7777-7777-777777777777");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly ProfileData _savedPendingDefault;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;
        private readonly Action _savedAfterRefresh;

        public DomainSwapClobberTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedProfiles = SettingsManager.Profiles;
            _savedActiveProfileId = SettingsManager.ActiveProfileId;
            _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedXboxOrder = SettingsManager.XboxSlotOrder;
            _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
        }

        public void Dispose()
        {
            // The stale flag is static and UI-thread-only in production. A
            // test that throws mid-window would otherwise leak it into every
            // later test in this collection and silently disable their pushes.
            InputService.VmMappingsStale = false;
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        private static (MainViewModel vm, InputService svc, SettingsService ss) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            var svc = new InputService(vm) { SettingsService = ss };
            return (vm, svc, ss);
        }

        private static void AddPad(Guid guid, string name)
        {
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = guid,
                    ProductGuid = guid,
                    InstanceName = name,
                    ProductName = name,
                    IsOnline = true,
                    CapType = PadForge.Engine.InputDeviceType.Gamepad,
                });
        }

        private static void AssignTo(Guid guid, int slot)
        {
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                var us = SettingsManager.UserSettings.Items
                    .FirstOrDefault(u => u.InstanceGuid == guid);
                if (us == null)
                {
                    us = new UserSetting { InstanceGuid = guid };
                    us.SetPadSetting(new PadSetting());
                    SettingsManager.UserSettings.Items.Add(us);
                }
                us.MapTo = slot;
            }
        }

        private static void MapButtonA(int slot, Guid deviceGuid)
        {
            var ms = SettingsManager.SlotMappingSets[slot]
                  ?? (SettingsManager.SlotMappingSets[slot] = new MappingSet());
            ms.Rows.RemoveAll(r => r.Target == "ButtonA");
            var row = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            row.Sources.Add(new MappingSource
            { Kind = "Direct", Descriptor = "Button 0", DeviceGuid = deviceGuid.ToString() });
            ms.Rows.Add(row);
        }

        /// <summary>Every device GUID the live slot's rows currently bind to.</summary>
        private static HashSet<string> LiveGuids(int slot)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = SettingsManager.SlotMappingSets[slot]?.Rows;
            if (rows == null) return set;
            foreach (var r in rows)
                foreach (var s in r.Sources ?? new List<MappingSource>())
                    if (!string.IsNullOrEmpty(s?.DeviceGuid)) set.Add(s.DeviceGuid);
            return set;
        }

        /// <summary>The default profile as the owner has it: one created slot,
        /// one pad assigned to it, one authored row, and a grid hydrated from
        /// that row so the ViewModels are a live source of push material.</summary>
        private static (MainViewModel vm, InputService svc) ArrangePopulatedDefault()
        {
            var (vm, svc, _) = Arrange();
            AddPad(OutgoingPad, "Steam Controller");
            AddPad(IncomingPad, "Xbox pad");
            AssignTo(OutgoingPad, 0);
            MapButtonA(0, OutgoingPad);
            svc.UpdatePadDeviceInfo();
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            svc.SaveActiveProfileState();   // captures the default snapshot

            // Positive control for every test built on this fixture: the grid
            // really does carry the outgoing pad, so "the outgoing pad did not
            // survive the transition" is a claim about the guard rather than
            // about an empty ViewModel.
            Assert.Equal(OutgoingPad.ToString(),
                vm.Pads[0].Mappings.First(m => m.TargetSettingName == "ButtonA").PrimarySourceDeviceGuid,
                ignoreCase: true);
            Assert.Contains(OutgoingPad.ToString(), LiveGuids(0), StringComparer.OrdinalIgnoreCase);
            return (vm, svc);
        }

        // ── Rule 1: the writer owns the guard, so every caller inherits it ──

        /// <summary>The contract a future fifth caller depends on. If this
        /// check ever moves back out to the call sites, this goes red.</summary>
        [Fact]
        public void PushIsANoOp_WhileTheDomainSwapWindowIsOpen()
        {
            var (vm, svc) = ArrangePopulatedDefault();

            // The swap: the incoming state owns nothing at slot 0.
            SettingsManager.SlotMappingSets[0] = null;

            InputService.VmMappingsStale = true;
            try { svc.PushUiIntoSlotMappingSetsForTest(); }
            finally { InputService.VmMappingsStale = false; }

            Assert.Null(SettingsManager.SlotMappingSets[0]);
        }

        /// <summary>Positive control for the test above. With the window shut
        /// the push MUST write, or "no-op while stale" would be vacuously true
        /// of a push that never does anything.</summary>
        [Fact]
        public void PushStillWrites_OnceTheWindowIsShut()
        {
            var (vm, svc) = ArrangePopulatedDefault();
            SettingsManager.SlotMappingSets[0] = null;

            Assert.False(InputService.VmMappingsStale);
            svc.PushUiIntoSlotMappingSetsForTest();

            Assert.NotNull(SettingsManager.SlotMappingSets[0]);
            Assert.NotEmpty(SettingsManager.SlotMappingSets[0].Rows);
        }

        /// <summary>The path that actually shipped the bug, driven through its
        /// real entry point rather than the test seam. UpdatePadDeviceInfo
        /// rebuilds the pad device lists, which changes the selected device,
        /// which raises OnSelectedDeviceChanged, which pushes. That call site
        /// had no guard of its own and never will need one.</summary>
        [Fact]
        public void SelectionChangeDuringTheWindow_DoesNotPush()
        {
            var (vm, svc) = ArrangePopulatedDefault();

            SettingsManager.SlotMappingSets[0] = null;
            lock (SettingsManager.UserSettings.SyncRoot)
                foreach (var us in SettingsManager.UserSettings.Items)
                    us.MapTo = -1;

            InputService.VmMappingsStale = true;
            try { svc.UpdatePadDeviceInfo(); }
            finally { InputService.VmMappingsStale = false; }

            Assert.Null(SettingsManager.SlotMappingSets[0]);
        }

        // ── Rule 2: an authored-empty slot is never resurrected ──

        /// <summary>Null is a value, not an absence. The lazy-create on the
        /// write path is what turned "this profile authored nothing here" into
        /// "this profile inherited everything from the last one".</summary>
        [Fact]
        public void PushDuringTheWindow_DoesNotResurrectANullSlot()
        {
            var (vm, svc) = ArrangePopulatedDefault();

            for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = null;

            InputService.VmMappingsStale = true;
            try
            {
                svc.PushUiIntoSlotMappingSetsForTest();
                svc.UpdatePadDeviceInfo();
                svc.PushUiIntoSlotMappingSetsForTest();
            }
            finally { InputService.VmMappingsStale = false; }

            Assert.All(SettingsManager.SlotMappingSets, Assert.Null);
        }

        // ── The transition matrix ──
        //
        // Every entry point that swaps domain state gets the same two
        // assertions: an incoming state that owns nothing keeps owning
        // nothing, and the outgoing state's device never survives into it.
        // A new transition wired without the guard fails here on arrival.

        private static ProfileData EmptyProfile(InputService svc)
            => svc.CreateEmptyProfile("Empty", "");

        [Fact]
        public void Transition_LoadProfile_IntoAnEmptyProfile_InheritsNothing()
        {
            var (vm, svc) = ArrangePopulatedDefault();
            var p = EmptyProfile(svc);

            svc.LoadProfile(p.Id);

            Assert.Empty(LiveGuids(0));
            Assert.Equal(0, SettingsManager.SlotMappingSets[0]?.Rows?.Count ?? 0);
        }

        [Fact]
        public void Transition_ApplyProfile_IntoAnEmptyProfile_InheritsNothing()
        {
            var (vm, svc) = ArrangePopulatedDefault();
            var p = EmptyProfile(svc);

            SettingsManager.ActiveProfileId = p.Id;
            svc.ApplyProfile(p);

            Assert.Empty(LiveGuids(0));
        }

        [Fact]
        public void Transition_RevertToDefault_RestoresTheDefaultsOwnDevice()
        {
            var (vm, svc) = ArrangePopulatedDefault();
            var p = EmptyProfile(svc);
            svc.LoadProfile(p.Id);

            // Build the incoming profile up on its OWN pad, in the order the
            // UI does it: assign, rebuild the device lists, author, hydrate.
            // Authoring before the rebuild lets the rebuild's push overwrite
            // the row with the still-empty grid, and the grid then never
            // carries the incoming pad at all, which makes the assertion below
            // pass for the wrong reason.
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            AssignTo(IncomingPad, 0);
            svc.UpdatePadDeviceInfo();
            MapButtonA(0, IncomingPad);
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            Assert.Equal(IncomingPad.ToString(),
                vm.Pads[0].Mappings.First(m => m.TargetSettingName == "ButtonA").PrimarySourceDeviceGuid,
                ignoreCase: true);

            svc.RevertToDefaultProfile();

            Assert.Contains(OutgoingPad.ToString(), LiveGuids(0), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(IncomingPad.ToString(), LiveGuids(0), StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Transition_DeleteActiveProfile_RestoresTheDefaultsOwnDevice()
        {
            var (vm, svc) = ArrangePopulatedDefault();
            var p = EmptyProfile(svc);
            svc.LoadProfile(p.Id);

            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            AssignTo(IncomingPad, 0);
            svc.UpdatePadDeviceInfo();
            MapButtonA(0, IncomingPad);
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);

            Assert.True(svc.DeleteProfile(p.Id));

            Assert.DoesNotContain(IncomingPad.ToString(), LiveGuids(0), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The no-snapshot arm of the revert, which is the state every
        /// restart taken on the default profile comes back in: the snapshot is
        /// only persisted while a NAMED profile is active. It swaps the domain
        /// sets the same way ApplyProfile does, so it must hold the same
        /// window, and the legacy rebuild raises AfterMappingSetsRefreshed
        /// right in the middle of it.
        ///
        /// <para>This asserts the WINDOW, not the resulting device. Which pad
        /// the default ends up on after a snapshot-less revert is a separate
        /// question with no settled answer: the outgoing profile's assignments
        /// are the only ones in memory at that point, and dropping them would
        /// leave the default with no devices at all. That call is the owner's,
        /// and this test deliberately does not prejudge it.</para></summary>
        [Fact]
        public void Transition_RevertWithNoSnapshot_HoldsTheSwapWindowAcrossTheRebuild()
        {
            var (vm, svc) = ArrangePopulatedDefault();
            var p = EmptyProfile(svc);
            svc.LoadProfile(p.Id);

            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            AssignTo(IncomingPad, 0);
            svc.UpdatePadDeviceInfo();
            MapButtonA(0, IncomingPad);
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);

            bool? staleAtRebuild = null;
            var prior = SettingsService.AfterMappingSetsRefreshed;
            SettingsService.AfterMappingSetsRefreshed = () =>
            {
                staleAtRebuild ??= InputService.VmMappingsStale;
                prior?.Invoke();
            };
            try
            {
                svc.ClearDefaultProfileSnapshotForTest();
                svc.RevertToDefaultProfile();
            }
            finally { SettingsService.AfterMappingSetsRefreshed = prior; }

            Assert.True(staleAtRebuild.HasValue,
                "the snapshot-less revert never reached the legacy rebuild, so this "
                + "test proved nothing about the window around it.");
            Assert.True(staleAtRebuild.Value,
                "the snapshot-less revert rebuilt the mapping sets with the swap "
                + "window shut, so anything that pushes during the rebuild writes "
                + "the outgoing profile's grid over the canon rebuild.");
        }
    }
}
