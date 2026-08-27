using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Follow Headphone Jack did nothing, and Mirror System Audio was silent,
    /// unless the user happened to own a virtual DualSense.
    ///
    /// <para>The jack bit lives in <c>s_padJackState</c>, and it had exactly
    /// two writers: the USB jack reader and the Bluetooth mic reader. Both
    /// were owned by a <c>PersonaFeed</c>, which Step 5 creates only when the
    /// slot's virtual controller is a composite USB persona. With no virtual
    /// DualSense there was no feed, so neither reader ran, the bit was never
    /// observed, <see cref="AudioPassthroughService.ResolveOutputPath"/> fell
    /// through to Default, and the DualSense firmware default routes program
    /// audio to the headphones and leaves the internal speaker silent. Silent,
    /// with no error anywhere, and only in that one configuration.</para>
    ///
    /// <para>The jack matters exactly while a sink exists, so the sink owns
    /// the watch now. Owner-confirmed on hardware across both an Xbox virtual
    /// controller with mirrored system audio and a DualSense virtual with its
    /// own audio device.</para>
    /// </summary>
    public class HeadphoneJackFollowTests
    {
        private const int FollowHeadphoneJack = 5;
        private const int Default = 0;
        private const int StereoHeadset = 1;
        private const int SpeakerOnly = 4;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string Source(params string[] parts)
            => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        private static string Audio()
            => Source("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");

        private static string Dispatcher()
            => Source("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");

        // ───────────────────── the resolver, by behavior ─────────────────────

        /// <summary>A plugged reading routes to the headset, an unplugged one
        /// to the speaker. This is the half that always worked, given a
        /// reading to work from.</summary>
        [Theory]
        [InlineData(true, StereoHeadset)]
        [InlineData(false, SpeakerOnly)]
        public void AReading_ResolvesToItsPath(bool plugged, int expected)
        {
            var pad = Guid.NewGuid();
            AudioPassthroughService.NoteHeadphoneJack(pad, plugged);
            Assert.Equal(expected, AudioPassthroughService.ResolveOutputPath(FollowHeadphoneJack, pad));
        }

        /// <summary>THE SYMPTOM. With no reading the resolver degrades to
        /// Default, and Default is the firmware state that keeps the internal
        /// speaker silent. Harmless on its own, fatal when nothing is ever
        /// going to supply a reading.</summary>
        [Fact]
        public void NoReading_DegradesToDefault()
        {
            Assert.Equal(Default,
                AudioPassthroughService.ResolveOutputPath(FollowHeadphoneJack, Guid.NewGuid()));
        }

        /// <summary>A jack reading changes the resolved path, so a plug or
        /// unplug has to move the route rather than latch the first answer.</summary>
        [Fact]
        public void AChangedReading_MovesTheResolvedPath()
        {
            var pad = Guid.NewGuid();
            AudioPassthroughService.NoteHeadphoneJack(pad, true);
            Assert.Equal(StereoHeadset, AudioPassthroughService.ResolveOutputPath(FollowHeadphoneJack, pad));
            AudioPassthroughService.NoteHeadphoneJack(pad, false);
            Assert.Equal(SpeakerOnly, AudioPassthroughService.ResolveOutputPath(FollowHeadphoneJack, pad));
        }

        /// <summary>Every other configured path passes through untouched. Only
        /// value 5 consults the jack.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void AnExplicitPath_IgnoresTheJack(int configured)
        {
            var pad = Guid.NewGuid();
            AudioPassthroughService.NoteHeadphoneJack(pad, true);
            Assert.Equal(configured, AudioPassthroughService.ResolveOutputPath(configured, pad));
        }

        // ──────────── the ownership contract (source-text locks) ────────────
        //
        // "The jack is observable without a persona lane" is a statement about
        // which THREAD owns a HID handle. There is no in-process seam for it:
        // asserting it needs a real DualSense on a real bus. The contract is
        // locked in the source, the pattern this repo already uses when a
        // contract has no seam, rather than left unlocked because it is
        // awkward to reach.

        /// <summary>THE BUG. A jack reader must exist that is NOT owned by a
        /// PersonaFeed. Before the fix every <c>NoteHeadphoneJack</c> call site
        /// was reached through a <c>feed.</c> field.</summary>
        [Fact]
        public void AJackReader_ExistsOutsideThePersonaLane()
        {
            string src = Audio();
            Assert.Contains("JackWatchLoop", src);
            Assert.Contains("EnsureJackWatch", src);

            // The sink-owned loop notes the bit without touching a feed.
            int at = src.IndexOf("private static void JackWatchLoop", StringComparison.Ordinal);
            Assert.True(at > 0, "the sink-owned jack loop moved");
            string body = src.Substring(at, Math.Min(3000, src.Length - at));
            Assert.Contains("NoteHeadphoneJack(pad", body);
            Assert.DoesNotContain("feed.", body);
        }

        /// <summary>The watch is driven by the SINK reconcile, so it exists
        /// exactly while a sink does. Wiring it anywhere persona-scoped would
        /// reintroduce the dependency the fix removes.</summary>
        [Fact]
        public void TheJackWatch_IsDrivenByTheSinkReconcile()
        {
            string src = Audio();
            int at = src.IndexOf("private static void ReconcileOnWorker", StringComparison.Ordinal);
            Assert.True(at > 0, "the reconcile moved");
            int end = src.IndexOf("\n        }", at, StringComparison.Ordinal);
            string body = src.Substring(at, (end > at ? end : Math.Min(at + 12000, src.Length)) - at);
            Assert.Contains("EnsureJackWatch", body);
            Assert.Contains("StopJackWatch", body);
        }

        /// <summary>Both transports are covered. Bluetooth and USB put the same
        /// duaLib status byte at different raw offsets because the Bluetooth
        /// packet starts at data[2], and reading the wrong one returns a bit
        /// from an unrelated field.</summary>
        [Fact]
        public void TheJackWatch_ReadsBothTransportsAtTheirOwnOffsets()
        {
            string body = Audio();
            int at = body.IndexOf("private static void JackWatchLoop", StringComparison.Ordinal);
            string loop = body.Substring(at, Math.Min(3000, body.Length - at));
            Assert.Contains("0x31", loop);   // BT state report id
            Assert.Contains("0x01", loop);   // USB input report id
            Assert.Contains("55", loop);     // BT status byte
            Assert.Contains("54", loop);     // USB status byte
        }

        // ──────── the writer-alive contract that made it silent ────────

        /// <summary>The firmware speaker path is asserted per output report,
        /// so it needs the effects dispatcher's timer alive for exactly the
        /// reason rumble does. UpdateAnimTimer had terms for lightbar
        /// animation, reactive overrides, input-reactive mode and rumble, and
        /// none for audio, so on a static-lightbar slot the assert landed only
        /// when some unrelated feature happened to hold the timer open.</summary>
        [Fact]
        public void TheAnimTimer_HasAnAudioDemandTerm()
        {
            string src = Dispatcher();
            int at = src.IndexOf("private void UpdateAnimTimer", StringComparison.Ordinal);
            Assert.True(at > 0, "UpdateAnimTimer moved");
            // The window has to clear the per-device config walk that sits
            // between the method opening and the demand terms at its end.
            string body = src.Substring(at, Math.Min(7000, src.Length - at));
            Assert.Contains("SlotWantsSpeakerPath", body);
        }

        /// <summary>A sink coming alive nudges the dispatcher, the twin of the
        /// teardown and expired-test nudges that already existed. Of the four
        /// sink transitions this was the only one that did not notify, and it
        /// is the one that ARMS the speaker path.</summary>
        [Fact]
        public void ASinkComingAlive_NotifiesTheDispatcher()
        {
            string src = Audio();
            Assert.Contains("_lastRouted", src);
            int at = src.IndexOf("_lastRouted[slot]", StringComparison.Ordinal);
            Assert.True(at > 0, "the rising-edge check moved");
            string body = src.Substring(Math.Max(0, at - 900), Math.Min(1400, src.Length - Math.Max(0, at - 900)));
            Assert.Contains("NotifySoundRoutingChanged", body);
        }

        /// <summary>The notify re-evaluates demand before dispatching.
        /// Dispatching alone asserted the path once and then went quiet, since
        /// UpdateAnimTimer is otherwise only reached from config edits and the
        /// rumble poke, neither of which fires for a mirror toggle.</summary>
        [Fact]
        public void TheRoutingNotify_ReevaluatesTimerDemand()
        {
            string src = Dispatcher();
            int at = src.IndexOf("public static void NotifySoundRoutingChanged", StringComparison.Ordinal);
            Assert.True(at > 0, "NotifySoundRoutingChanged moved");
            string body = src.Substring(at, Math.Min(1200, src.Length - at));
            int timer = body.IndexOf("UpdateAnimTimer", StringComparison.Ordinal);
            int dispatch = body.IndexOf("DispatchSnapshot", StringComparison.Ordinal);
            Assert.True(timer > 0, "demand is no longer re-evaluated on a routing change");
            Assert.True(dispatch > timer, "demand must be re-evaluated BEFORE the snapshot");
        }
    
        /// <summary>#347: the crossfeed route gate. Automatic and StereoHeadset
        /// are the SAME firmware route, L_R_X (duaLib dataStructures.h line
        /// 284), because Automatic writes nothing and the pad rests on path 0
        /// (duaLib.cpp line 279, "so the audio path can reset back to 0 on
        /// first write"). The first cut gated on StereoHeadset alone, which
        /// left the DSP chain inaudible for every user who never touched
        /// Output Path.</summary>
        [Theory]
        [InlineData(0, true)]    // Automatic: firmware rests on L_R_X
        [InlineData(1, true)]    // StereoHeadset: writes L_R_X
        [InlineData(2, false)]   // MonoHeadset: L_L_X
        [InlineData(3, false)]   // HeadsetAndSpeaker: L_L_R, headset side mono
        [InlineData(4, false)]   // SpeakerOnly: X_X_R
        public void StereoHeadphoneRoute_CoversAutomaticAndStereo(int resolved, bool expected)
            => Assert.Equal(expected, AudioPassthroughService.IsStereoHeadphoneRoute(resolved));

        /// <summary>Follow Headphone Jack resolves before the gate sees it, so
        /// both of its outcomes land on the right side: plugged resolves to
        /// StereoHeadset and crossfeeds, unplugged resolves to SpeakerOnly and
        /// does not.</summary>
        [Theory]
        [InlineData(1, true)]
        [InlineData(4, false)]
        public void StereoHeadphoneRoute_MatchesFollowJackOutcomes(int resolved, bool expected)
            => Assert.Equal(expected, AudioPassthroughService.IsStereoHeadphoneRoute(resolved));
}
}
