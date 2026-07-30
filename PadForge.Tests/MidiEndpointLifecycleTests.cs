using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// MIDI virtual-controller endpoint identity and corpse handling.
    /// Windows MIDI Services keys a virtual endpoint's devnodes off the
    /// creating app's unique id and can strand them past the app's death,
    /// then adopts a stranded devnode on the next create with the same id.
    /// PadForge therefore uses per-creation unique ids, tracks live
    /// endpoints in-process, and sweeps unclaimed PadForge devnodes.
    /// These tests cover the pure logic: id format, registry matching in
    /// both id spellings, and the janitor's candidate predicate.
    /// </summary>
    public class MidiEndpointLifecycleTests
    {
        // Service contract: unique id max 32 chars (microsoft/MIDI
        // json_defs.h MIDI_CONFIG_JSON_ENDPOINT_VIRTUAL_DEVICE_UNIQUE_ID_MAX_LEN);
        // longer ids get truncated service-side, which would re-introduce
        // collisions. The id must also stay inside the PADFORGE_MIDI
        // family the janitor and scanner recognize.
        [Theory]
        [InlineData(1)]
        [InlineData(9)]
        [InlineData(16)]
        public void UniqueEndpointId_FitsServiceContract(int instanceNum)
        {
            string id = MidiVirtualController.BuildUniqueEndpointId(instanceNum);
            Assert.True(id.Length <= 32, $"'{id}' exceeds the service's 32-char unique-id cap");
            Assert.StartsWith($"PADFORGE_MIDI_{instanceNum}_", id, StringComparison.Ordinal);
        }

        [Fact]
        public void UniqueEndpointId_DiffersPerCreation()
        {
            Assert.NotEqual(
                MidiVirtualController.BuildUniqueEndpointId(1),
                MidiVirtualController.BuildUniqueEndpointId(1));
        }

        // The registry must match both spellings the id family appears in:
        // the devnode instance id (janitor's view) and the endpoint
        // interface id (scanner's view).
        [Fact]
        public void Registry_MatchesDevnodeAndInterfaceSpellings()
        {
            string uid = "PADFORGE_MIDI_1_AABBCCDDEEFF";
            try
            {
                MidiVirtualController.RegisterEndpointForTest(uid, ready: false);

                string devnode = $@"SWD\MIDISRV\MIDIU_APPDEV_{uid}";
                string iface = $@"\\?\SWD#MIDISRV#MIDIU_APPPUB_{uid}#{{e7cce071-3c03-423f-88d3-f1045d02552b}}";

                Assert.True(MidiVirtualController.IsLiveEndpointInstance(devnode));
                Assert.True(MidiVirtualController.IsLiveEndpointInstance(iface));

                // Creating (not ready): scanner must not open it yet.
                Assert.False(MidiVirtualController.IsReadyEndpointInstance(iface));

                MidiVirtualController.RegisterEndpointForTest(uid, ready: true);
                Assert.True(MidiVirtualController.IsReadyEndpointInstance(iface));
            }
            finally
            {
                MidiVirtualController.UnregisterEndpointForTest(uid);
            }
        }

        [Fact]
        public void Registry_DoesNotMatchForeignOrUnregisteredIds()
        {
            string uid = "PADFORGE_MIDI_1_AABBCCDDEEFF";
            try
            {
                MidiVirtualController.RegisterEndpointForTest(uid, ready: true);

                // Different creation of the same slot number stays foreign.
                Assert.False(MidiVirtualController.IsLiveEndpointInstance(
                    @"SWD\MIDISRV\MIDIU_APPDEV_PADFORGE_MIDI_1_000000000000"));
                // Legacy fixed-name corpse from an old build stays foreign.
                Assert.False(MidiVirtualController.IsLiveEndpointInstance(
                    @"SWD\MIDISRV\MIDIU_APPDEV_PADFORGE_MIDI_1"));
            }
            finally
            {
                MidiVirtualController.UnregisterEndpointForTest(uid);
            }
        }

        [Theory]
        // Legacy fixed id, device side (the corpse observed on the bench).
        [InlineData(@"SWD\MIDISRV\MIDIU_APPDEV_PADFORGE_MIDI_1", true)]
        // New per-creation id, client side, interface-id spelling.
        [InlineData(@"\\?\SWD#MIDISRV#MIDIU_APPPUB_PADFORGE_MIDI_2_AABBCCDDEEFF#{guid}", true)]
        // Foreign virtual endpoints are never PadForge's to touch.
        [InlineData(@"SWD\MIDISRV\MIDIU_APPDEV_SOMEDAWDEVICE", false)]
        [InlineData(@"SWD\MIDISRV\MIDIU_DIAG_TRANSPORT", false)]
        [InlineData(null, false)]
        public void Janitor_RecognizesOnlyPadForgeEndpointFamily(string id, bool expected)
        {
            Assert.Equal(expected, MidiEndpointJanitor.IsPadForgeEndpointId(id));
        }

        // A timed-out create or teardown demotes its claim to "abandoned":
        // still protected while the hung RPC might land, sweepable once
        // the grace window passes. Without the expiry, every hung create
        // parked one devnode in Device Manager until the next app launch.
        [Fact]
        public void Registry_AbandonedClaimExpiresAfterGrace()
        {
            string uid = "PADFORGE_MIDI_1_ABANDONTEST0";
            try
            {
                string devnode = $@"SWD\MIDISRV\MIDIU_APPDEV_{uid}";

                MidiVirtualController.AbandonEndpointForTest(uid, Environment.TickCount64);
                Assert.True(MidiVirtualController.IsLiveEndpointInstance(devnode));
                Assert.False(MidiVirtualController.IsReadyEndpointInstance(devnode));
                Assert.False(MidiEndpointJanitor.IsSweepCandidate(devnode));

                MidiVirtualController.AbandonEndpointForTest(uid,
                    Environment.TickCount64 - MidiVirtualController.AbandonedGraceMs - 1_000);
                Assert.False(MidiVirtualController.IsLiveEndpointInstance(devnode));
                Assert.True(MidiEndpointJanitor.IsSweepCandidate(devnode));

                MidiVirtualController.PruneExpiredEndpointClaims();
                Assert.False(MidiVirtualController.IsLiveEndpointInstance(devnode));
            }
            finally
            {
                MidiVirtualController.UnregisterEndpointForTest(uid);
            }
        }

        [Fact]
        public void Janitor_SkipsLiveEndpoints_SweepsCorpses()
        {
            string uid = "PADFORGE_MIDI_1_AABBCCDDEEFF";
            try
            {
                // Registered while creating: protected from the sweep even
                // before the connection opens.
                MidiVirtualController.RegisterEndpointForTest(uid, ready: false);
                Assert.False(MidiEndpointJanitor.IsSweepCandidate($@"SWD\MIDISRV\MIDIU_APPDEV_{uid}"));
                Assert.False(MidiEndpointJanitor.IsSweepCandidate($@"SWD\MIDISRV\MIDIU_APPPUB_{uid}"));

                // Unregistered PadForge devnodes are corpses.
                Assert.True(MidiEndpointJanitor.IsSweepCandidate(
                    @"SWD\MIDISRV\MIDIU_APPDEV_PADFORGE_MIDI_1"));

                // Foreign devnodes are never candidates, live or not.
                Assert.False(MidiEndpointJanitor.IsSweepCandidate(
                    @"SWD\MIDISRV\MIDIU_APPDEV_SOMEDAWDEVICE"));
            }
            finally
            {
                MidiVirtualController.UnregisterEndpointForTest(uid);
            }
        }
    }
}
