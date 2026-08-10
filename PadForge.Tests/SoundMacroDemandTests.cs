using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Controller-routed macro audio demand is derived from configuration,
    /// never latched from playback.
    ///
    /// <para>The old shape was an add-only static HashSet: the first macro
    /// sound played on a slot put its audio transport into a keep-alive set
    /// for the rest of the process, surviving the macro's deletion, profile
    /// switches, and the device's unassignment. Same defect class as the
    /// slot-reassign identity latch (a session-scoped latch with no answer
    /// for "the thing it describes went away"), fixed the same day.</para>
    ///
    /// <para><see cref="SoundMacroService.SnapshotWantsControllerAudio"/> is
    /// the demand signal now: the audio reconcile reads it per pass through
    /// AudioPassthroughService.SlotWantsMacroAudioProvider, so a configured
    /// sound macro pre-builds the transport (first trigger no longer falls
    /// into the pendingActivation drop) and removing the last one tears it
    /// down on the next pass. Reconnect stickiness survives, because the
    /// CONFIG persists across a reconnect even though playback state does
    /// not.</para>
    /// </summary>
    public class SoundMacroDemandTests
    {
        private static MacroItem Macro(params MacroActionType[] actionTypes)
        {
            var m = new MacroItem();
            foreach (var t in actionTypes)
                m.Actions.Add(new MacroAction { Type = t });
            return m;
        }

        [Fact]
        public void NullSnapshot_MeansNoDemand()
        {
            Assert.False(SoundMacroService.SnapshotWantsControllerAudio(null));
        }

        [Fact]
        public void EmptySnapshot_MeansNoDemand()
        {
            Assert.False(SoundMacroService.SnapshotWantsControllerAudio(
                new MacroItem[0]));
        }

        [Fact]
        public void MacrosWithoutSoundActions_MeanNoDemand()
        {
            // SoundStop is deliberately included: it is the adjacent enum
            // value to PlaySound and stops sounds rather than playing them,
            // so it must not hold a transport open on its own.
            Assert.False(SoundMacroService.SnapshotWantsControllerAudio(new[]
            {
                Macro(),
                Macro(MacroActionType.SoundStop),
            }));
        }

        [Fact]
        public void AnyPlaySoundAction_MeansDemand()
        {
            Assert.True(SoundMacroService.SnapshotWantsControllerAudio(new[]
            {
                Macro(MacroActionType.SoundStop),
                Macro(MacroActionType.SoundStop, MacroActionType.PlaySound),
            }));
        }

        [Fact]
        public void NullMacroEntries_AreSkippedNotThrown()
        {
            // SyncMacroSnapshots swaps arrays atomically, but defensive
            // null-tolerance keeps a torn snapshot from taking down the
            // audio worker.
            Assert.False(SoundMacroService.SnapshotWantsControllerAudio(
                new MacroItem[] { null, Macro(MacroActionType.SoundStop) }));
            Assert.True(SoundMacroService.SnapshotWantsControllerAudio(
                new MacroItem[] { null, Macro(MacroActionType.PlaySound) }));
        }

        [Fact]
        public void RemovingTheSoundAction_RemovesDemand()
        {
            // The teardown half the latch never had: the same macro object,
            // edited to no longer play a sound, stops demanding a transport.
            var m = Macro(MacroActionType.PlaySound);
            var snapshot = new[] { m };
            Assert.True(SoundMacroService.SnapshotWantsControllerAudio(snapshot));

            m.Actions.Clear();
            Assert.False(SoundMacroService.SnapshotWantsControllerAudio(snapshot));
        }
    }
}
