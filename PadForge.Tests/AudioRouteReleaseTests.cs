using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Where a Sony pad's audio plays once PadForge stops routing it (#546).
    /// Automatic puts a DualSense back on its headphones once when it replaces
    /// an explicit path, as a DualShock 4's USB route does, and PadForge puts
    /// every pad it left off the headset back on it when it lets go, the way
    /// duaLib's letGo releases a pad. In the collection that owns the audio
    /// service's statics.
    /// </summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class AudioRouteReleaseTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        private static ConcurrentDictionary<Guid, (string Path, HMProfile Profile, bool Ds5)> Routed =>
            (ConcurrentDictionary<Guid, (string Path, HMProfile Profile, bool Ds5)>)typeof(UserEffectsDispatcher)
                .GetField("s_routedOffHeadset", Static)!.GetValue(null)!;

        /// <summary>A Bluetooth DualSense whose reports land in a temp file,
        /// on its own slot dispatcher, staged as EffectDeliveryTests stages
        /// one. Valid flags 0 and 1 are bytes 3 and 4 of report 0x31.</summary>
        private sealed class FilePad : IDisposable
        {
            public readonly Guid Id = Guid.NewGuid();
            public readonly string File;
            public readonly DeviceSlotConfig Config;
            public readonly int AudioControlAt;
            private readonly string _dir;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly Func<int, IReadOnlyDictionary<Guid, DeviceSlotConfig>> _configs =
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            private UserEffectsDispatcher _dispatcher;

            public FilePad(AudioOutputPath path)
            {
                AudioPassthroughService.Shutdown();
                foreach (string name in new[] { "_workerThread", "_btThread" })
                    if (typeof(AudioPassthroughService).GetField(name, Static)!.GetValue(null) is Thread t && t.IsAlive)
                        Assert.True(t.Join(5000));
                _dir = Path.Combine(Path.GetTempPath(), "PadForge-RouteRelease-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_dir);
                File = Path.Combine(_dir, "BTHENUM-report.bin");
                System.IO.File.WriteAllBytes(File, Array.Empty<byte>());
                SettingsManager.UserDevices = new();
                SettingsManager.UserSettings = new();
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = Id, VendorId = 0x054c, ProdId = 0x0ce6,
                    IsOnline = true, IsEnabled = true, DevicePath = File,
                });
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = Id, MapTo = 15 });
                Config = new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = path };
                _dispatcher = new UserEffectsDispatcher(15, Config, startTimer: false);
                AudioControlAt = AudioControlOffset(HMaestroProfileCatalog.GetProfileById("dualsense-bt-full"));
            }

            public void Apply()
            {
                _dispatcher.ApplyOnce();
                (typeof(UserEffectsDispatcher).GetField("_animTimer", Private)!.GetValue(_dispatcher) as Timer)
                    ?.Change(Timeout.Infinite, Timeout.Infinite);
            }

            /// <summary>Runs the claim burst out, so a steady frame asserts
            /// only what changed.</summary>
            public void Settle()
            {
                Apply();
                ((Dictionary<Guid, int>)typeof(UserEffectsDispatcher).GetField("_audioMuteBurstLeft", Private)!
                    .GetValue(_dispatcher)!)[Id] = 0;
                Apply();
            }

            /// <summary>Every frame captured until <see cref="Land"/> reads as
            /// superseded, so it neither lands nor commits.</summary>
            public void Drop() => LastWrites[File] = long.MaxValue;
            public void Land() => LastWrites.TryRemove(File, out _);

            public void Clear() => System.IO.File.WriteAllBytes(File, Array.Empty<byte>());
            public byte[] Read() => System.IO.File.ReadAllBytes(File);

            /// <summary>Retires the dispatcher, as engine stop destroys the
            /// virtual controllers before it releases the routes.</summary>
            public void Retire()
            {
                _dispatcher?.Dispose();
                _dispatcher = null;
            }

            /// <summary>Takes the pad out of every slot, as an unassign, a
            /// deleted slot or a profile that drops it does.</summary>
            public void Unmap()
            {
                var items = SettingsManager.UserSettings.Items;
                for (int i = items.Count - 1; i >= 0; i--)
                    if (items[i].InstanceGuid == Id) items.RemoveAt(i);
            }

            public void Map(int slot) => SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = Id, MapTo = slot });

            /// <summary>Moves the pad to slot 14, whose Output Path is
            /// Automatic, and returns that slot's dispatcher.</summary>
            public UserEffectsDispatcher MoveToAutomaticSlot()
            {
                Unmap();
                Map(14);
                _moved = new UserEffectsDispatcher(14,
                    new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.Automatic },
                    startTimer: false);
                return _moved;
            }
            private UserEffectsDispatcher _moved;

            public void Dispose()
            {
                Retire();
                _moved?.Dispose();
                Land();
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _configs;
                try { Directory.Delete(_dir, true); } catch { }
            }

            private static ConcurrentDictionary<string, long> LastWrites =>
                (ConcurrentDictionary<string, long>)typeof(UserEffectsDispatcher)
                    .GetField("s_lastWriteSeq", Static)!.GetValue(null)!;
        }

        /// <summary>The byte that carries the output path in bits 4-5, found
        /// by encoding the field alone. The last four bytes are the Bluetooth
        /// CRC.</summary>
        private static int AudioControlOffset(HMProfile profile)
        {
            Assert.NotNull(profile);
            byte[] with = HMOutputEncoder.Encode(profile, new Dictionary<string, object> { ["audioControlFlags"] = (byte)0x30 });
            byte[] without = HMOutputEncoder.Encode(profile, new Dictionary<string, object>());
            for (int i = 0; i < with.Length - 4; i++)
                if (with[i] != without[i]) return i;
            throw new InvalidOperationException("the profile places no audio control byte");
        }

        [Theory]
        [InlineData(AudioOutputPath.SpeakerOnly, true)]
        [InlineData(AudioOutputPath.HeadsetAndSpeaker, true)]
        [InlineData(AudioOutputPath.MonoHeadset, true)]
        [InlineData(AudioOutputPath.StereoHeadset, false)]
        public void Automatic_PutsADualSenseBackOnItsHeadphones_OnceAfterAnExplicitPath(AudioOutputPath from, bool restores)
        {
            Routed.Clear();
            using var pad = new FilePad(from);
            try
            {
                pad.Settle();
                Assert.Equal(restores, Routed.ContainsKey(pad.Id));

                pad.Drop();
                pad.Config.AudioOutputPath = AudioOutputPath.Automatic;
                pad.Land();
                pad.Clear();
                pad.Apply();
                byte[] frame = pad.Read();
                if (restores)
                {
                    Assert.Equal(0x80, frame[3] & 0x80);
                    Assert.Equal(0x80, frame[4] & 0x80);
                    Assert.Equal(0, frame[pad.AudioControlAt] & 0x30);
                }
                else
                {
                    Assert.Equal(0, frame[3] & 0x80);
                }
                Assert.False(Routed.ContainsKey(pad.Id));

                pad.Clear();
                pad.Apply();
                Assert.Equal(0, pad.Read()[3] & 0x80);
            }
            finally { Routed.Clear(); }
        }

        [Fact]
        public void LettingGo_PutsADualSensePadForgeLeftOnItsSpeakerBackOnItsHeadphones()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.SpeakerOnly);
            try
            {
                pad.Settle();
                Assert.Equal(0x30, pad.Read()[pad.AudioControlAt] & 0x30);
                Assert.True(Routed.ContainsKey(pad.Id));
                pad.Retire();
                pad.Clear();
                long captured = (long)typeof(UserEffectsDispatcher).GetField("s_dispatchSeq", Static)!.GetValue(null)!;

                UserEffectsDispatcher.ReleaseAudioRoutes();

                // A frame captured before the release now reads as stale.
                var lastWrites = (ConcurrentDictionary<string, long>)typeof(UserEffectsDispatcher)
                    .GetField("s_lastWriteSeq", Static)!.GetValue(null)!;
                Assert.True(lastWrites[pad.File] > captured);
                byte[] frame = pad.Read();
                Assert.Equal(0x31, frame[0]);
                Assert.Equal((byte)0x80, frame[3]);
                Assert.Equal((byte)0x80, frame[4]);
                Assert.Equal(0, frame[pad.AudioControlAt] & 0x30);
                Assert.False(Routed.ContainsKey(pad.Id));
            }
            finally
            {
                UserEffectsDispatcher.ResumeAudioRoutes();
                Routed.Clear();
            }
        }

        [Fact]
        public void LettingGo_LeavesAPadPadForgeNeverRoutedAlone()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.Automatic);
            try
            {
                pad.Settle();
                Assert.False(Routed.ContainsKey(pad.Id));
                pad.Retire();
                pad.Clear();

                UserEffectsDispatcher.ReleaseAudioRoutes();

                Assert.Empty(pad.Read());
            }
            finally
            {
                UserEffectsDispatcher.ResumeAudioRoutes();
                Routed.Clear();
            }
        }

        private static ConcurrentDictionary<Guid, byte> Tried =>
            (ConcurrentDictionary<Guid, byte>)typeof(UserEffectsDispatcher)
                .GetField("s_orphanReleaseTried", Static)!.GetValue(null)!;

        private static void AssertHeadsetFrame(FilePad pad, byte[] frame)
        {
            Assert.True(frame.Length > pad.AudioControlAt, "no frame landed");
            Assert.Equal(0x80, frame[3] & 0x80);
            Assert.Equal(0x80, frame[4] & 0x80);
            Assert.Equal(0, frame[pad.AudioControlAt] & 0x30);
        }

        [Fact]
        public void APadNoSlotMapsAnyMore_GetsItsHeadphonesBack()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.SpeakerOnly);
            try
            {
                pad.Settle();
                Assert.True(Routed.ContainsKey(pad.Id));
                pad.Unmap();
                pad.Clear();
                pad.Apply();
                AssertHeadsetFrame(pad, pad.Read());
                Assert.False(Routed.ContainsKey(pad.Id));
            }
            finally { Routed.Clear(); }
        }

        /// <summary>A deleted slot takes its dispatcher with it before that
        /// dispatcher sees its pads leave, so the dispose sweeps.</summary>
        [Fact]
        public void ADeletedSlot_GivesItsPadItsHeadphonesBack()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.SpeakerOnly);
            try
            {
                pad.Settle();
                pad.Unmap();
                pad.Clear();
                pad.Retire();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (pad.Read().Length == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
                AssertHeadsetFrame(pad, pad.Read());
                Assert.False(Routed.ContainsKey(pad.Id));
            }
            finally { Routed.Clear(); }
        }

        [Fact]
        public void APadMovedToAnAutomaticSlot_GetsItsHeadphonesBack()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.SpeakerOnly);
            try
            {
                pad.Settle();
                Assert.True(Routed.ContainsKey(pad.Id));
                var automatic = pad.MoveToAutomaticSlot();
                pad.Clear();
                automatic.ApplyOnce();
                (typeof(UserEffectsDispatcher).GetField("_animTimer", Private)!.GetValue(automatic) as Timer)
                    ?.Change(Timeout.Infinite, Timeout.Infinite);
                AssertHeadsetFrame(pad, pad.Read());
                Assert.False(Routed.ContainsKey(pad.Id));
            }
            finally { Routed.Clear(); }
        }

        /// <summary>A pad that does not take its release is not written on
        /// every frame. A slot mapping it again makes it eligible once
        /// more.</summary>
        [Fact]
        public void AReleaseThatFails_WaitsForThePadToBeMappedAgain()
        {
            Routed.Clear();
            Tried.Clear();
            using var pad = new FilePad(AudioOutputPath.SpeakerOnly);
            string moved = pad.File + ".away";
            try
            {
                pad.Settle();
                File.Move(pad.File, moved);
                Directory.CreateDirectory(pad.File);
                pad.Unmap();
                pad.Apply();
                Assert.True(Tried.ContainsKey(pad.Id));
                Assert.True(Routed.ContainsKey(pad.Id));
                long attempted = LastWrite(pad.File);
                pad.Apply();
                Assert.Equal(attempted, LastWrite(pad.File));

                Directory.Delete(pad.File);
                File.Move(moved, pad.File);
                pad.Map(15);
                pad.Apply();
                Assert.False(Tried.ContainsKey(pad.Id));
            }
            finally
            {
                if (Directory.Exists(pad.File)) Directory.Delete(pad.File);
                if (File.Exists(moved)) File.Move(moved, pad.File);
                Routed.Clear();
                Tried.Clear();
            }
        }

        private static long LastWrite(string path)
            => ((ConcurrentDictionary<string, long>)typeof(UserEffectsDispatcher)
                .GetField("s_lastWriteSeq", Static)!.GetValue(null)!).TryGetValue(path, out long seq) ? seq : -1;

        /// <summary>A slot that took the pad since the release was decided
        /// wrote a newer frame, and the newer frame stands.</summary>
        [Fact]
        public void AReleaseOlderThanThePadsLastWrite_IsDropped()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.SpeakerOnly);
            try
            {
                pad.Settle();
                pad.Unmap();
                pad.Drop();
                pad.Clear();
                pad.Apply();
                Assert.Empty(pad.Read());
                Assert.True(Routed.ContainsKey(pad.Id));

                pad.Land();
                pad.Apply();
                AssertHeadsetFrame(pad, pad.Read());
            }
            finally { Routed.Clear(); }
        }

        [Fact]
        public void AnOfflinePad_WaitsUntilItIsBack()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.SpeakerOnly);
            try
            {
                pad.Settle();
                var device = SettingsManager.UserDevices.Items.Single(d => d.InstanceGuid == pad.Id);
                device.IsOnline = false;
                pad.Unmap();
                pad.Clear();
                pad.Apply();
                Assert.Empty(pad.Read());
                Assert.True(Routed.ContainsKey(pad.Id));

                device.IsOnline = true;
                pad.Apply();
                AssertHeadsetFrame(pad, pad.Read());
            }
            finally { Routed.Clear(); }
        }

        /// <summary>The shared record leaves a remote-link pad out, since its
        /// owner routes it, so a consumer's pad leaves an explicit path for
        /// Automatic by this dispatcher's own record.</summary>
        [Fact]
        public void APeerPadLeavingAnExplicitPathForAutomatic_GetsItsHeadphonesBack()
        {
            Routed.Clear();
            string path = "peer://route-release-" + Guid.NewGuid();
            var sender = RemoteLinkOutputRouter.SendOutput;
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var configs = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            var lastWrites = (ConcurrentDictionary<string, long>)typeof(UserEffectsDispatcher)
                .GetField("s_lastWriteSeq", Static)!.GetValue(null)!;
            var id = Guid.NewGuid();
            UserEffectsDispatcher dispatcher = null;
            int controlAt = AudioControlOffset(HMaestroProfileCatalog.GetProfileById("dualsense")) - 1;
            byte[] Shipped()
            {
                var targets = (System.Collections.IDictionary)typeof(RemoteLinkOutputRouter)
                    .GetField("_byPath", Static)!.GetValue(null)!;
                object target = targets[path]!;
                return (byte[])target.GetType().GetField("Sony", Private)!.GetValue(target)!;
            }
            void Apply()
            {
                dispatcher.ApplyOnce();
                (typeof(UserEffectsDispatcher).GetField("_animTimer", Private)!.GetValue(dispatcher) as Timer)
                    ?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            try
            {
                RemoteLinkOutputRouter.SendOutput = (_, _, _) => { };
                RemoteLinkOutputRouter.Register(path, "route-release", 0);
                SettingsManager.UserDevices = new();
                SettingsManager.UserSettings = new();
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = id, VendorId = 0x054c, ProdId = 0x0ce6,
                    IsOnline = true, IsEnabled = true, DevicePath = path,
                });
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 15 });
                var cfg = new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.SpeakerOnly };
                dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);
                Apply();
                ((Dictionary<Guid, int>)typeof(UserEffectsDispatcher).GetField("_audioMuteBurstLeft", Private)!
                    .GetValue(dispatcher)!)[id] = 0;
                Apply();
                Assert.Equal(0x30, Shipped()[controlAt] & 0x30);
                Assert.False(Routed.ContainsKey(id));

                lastWrites[path] = long.MaxValue;
                cfg.AudioOutputPath = AudioOutputPath.Automatic;
                lastWrites.TryRemove(path, out _);
                Apply();
                byte[] body = Shipped();
                Assert.Equal(0x80, body[0] & 0x80);
                Assert.Equal(0x80, body[1] & 0x80);
                Assert.Equal(0, body[controlAt] & 0x30);
            }
            finally
            {
                dispatcher?.Dispose();
                lastWrites.TryRemove(path, out _);
                RemoteLinkOutputRouter.Unregister(path);
                RemoteLinkOutputRouter.SendOutput = sender;
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = configs;
                Routed.Clear();
            }
        }

        [Fact]
        public void WhileReleased_NoFrameAssertsAnExplicitPath()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.Automatic);
            try
            {
                pad.Settle();
                UserEffectsDispatcher.ReleaseAudioRoutes();
                pad.Clear();
                pad.Config.AudioOutputPath = AudioOutputPath.SpeakerOnly;
                pad.Apply();
                Assert.Equal(0, pad.Read()[3] & 0x80);
                Assert.False(Routed.ContainsKey(pad.Id));
            }
            finally
            {
                UserEffectsDispatcher.ResumeAudioRoutes();
                Routed.Clear();
            }
        }

        /// <summary>A pad another slot left off its headset gets the headset
        /// back on Automatic, from a dispatcher that never routed it.</summary>
        [Theory]
        [InlineData(0, false, -1, true, 0)]
        [InlineData(0, false, -1, false, -1)]
        [InlineData(0, true, -1, true, 3)]
        [InlineData(4, false, -1, true, 3)]
        [InlineData(0, false, 0, true, -1)]
        public void RouteToWrite_GivesTheHeadsetBackToAPadAnotherSlotRouted(
            int path, bool plays, int last, bool leftOffHeadset, int write)
            => Assert.Equal(write, Ds4UsbAudio.RouteToWrite(path, plays, last, leftOffHeadset));

        /// <summary>On an abnormal exit the dispatchers keep running after the
        /// release, and a DualSense on Automatic takes its speaker path every
        /// frame while a sink plays to it. Released, no frame sends it, and
        /// the next engine start lets it play again.</summary>
        [Fact]
        public void AfterTheRelease_NoFramePutsTheSpeakerBack()
        {
            Routed.Clear();
            using var pad = new FilePad(AudioOutputPath.Automatic);
            var sinks = (System.Collections.IDictionary)typeof(AudioPassthroughService).GetField("_sinks", Static)!.GetValue(null)!;
            object gate = typeof(AudioPassthroughService).GetField("_lock", Static)!.GetValue(null)!;
            var sinkType = typeof(AudioPassthroughService).GetNestedType("Sink", BindingFlags.NonPublic)!;
            object sink = Activator.CreateInstance(sinkType, nonPublic: true)!;
            sinkType.GetField("DeviceGuid")!.SetValue(sink, pad.Id);
            sinkType.GetField("IsPeer")!.SetValue(sink, true);
            lock (gate) sinks[pad.Id] = sink;
            try
            {
                Assert.True(AudioPassthroughService.WantsSpeakerPath(pad.Id));
                pad.Settle();
                Assert.Equal(0x30, pad.Read()[pad.AudioControlAt] & 0x30);
                Assert.True(Routed.ContainsKey(pad.Id));

                UserEffectsDispatcher.ReleaseAudioRoutes();
                Assert.Equal(0, pad.Read()[pad.AudioControlAt] & 0x30);
                pad.Clear();
                pad.Apply();
                Assert.Equal(0, pad.Read()[3] & 0x80);

                UserEffectsDispatcher.ResumeAudioRoutes();
                pad.Clear();
                pad.Apply();
                byte[] frame = pad.Read();
                Assert.Equal(0x80, frame[3] & 0x80);
                Assert.Equal(0x30, frame[pad.AudioControlAt] & 0x30);
            }
            finally
            {
                lock (gate) sinks.Remove(pad.Id);
                UserEffectsDispatcher.ResumeAudioRoutes();
                Routed.Clear();
            }
        }

        /// <summary>The route a frame leaves its pad on: the USB route a
        /// DualShock 4 frame sends, or the path a DualSense frame asserts. A
        /// DualShock 4's speaker volume flag shares bit 7 of its first flags
        /// byte and is not a route.</summary>
        [Fact]
        public void DeliveredRoute_ReadsWhereAFrameLeavesThePad()
        {
            var cfg = new DeviceSlotConfig();
            int speaker = (int)AudioOutputPath.SpeakerOnly;
            Assert.Equal(3, UserEffectsDispatcher.DeliveredRoute(
                Ds5EffectSynthesizer.BuildFields(cfg, audioOutputPath: speaker, assertAudioControl: true), out bool ds5));
            Assert.True(ds5);
            Assert.Equal(-1, UserEffectsDispatcher.DeliveredRoute(
                Ds5EffectSynthesizer.BuildFields(cfg, audioOutputPath: speaker, assertAudioControl: false), out _));
            Assert.Equal(-1, UserEffectsDispatcher.DeliveredRoute(Ds5EffectSynthesizer.BuildFields(cfg), out _));
            Assert.Equal(0, UserEffectsDispatcher.DeliveredRoute(Ds5EffectSynthesizer.HeadsetPathFields(), out ds5));
            Assert.True(ds5);

            Assert.Equal(3, UserEffectsDispatcher.DeliveredRoute(Ds4Fields(Ds4UsbAudio.RouteSpeaker), out ds5));
            Assert.False(ds5);
            Assert.Equal(0, UserEffectsDispatcher.DeliveredRoute(Ds4Fields(Ds4UsbAudio.RouteStereoHeadset), out _));
            var bluetooth = Ds4Fields(-1);
            Assert.Equal(0x80, (byte)bluetooth["validFlag0"] & 0x80);
            Assert.Equal(-1, UserEffectsDispatcher.DeliveredRoute(bluetooth, out _));
        }

        private static Dictionary<string, object> Ds4Fields(int usbRoute)
            => Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0, 0,
                assertVolumes: true, usbRoute: usbRoute);

        /// <summary>The release writes after the dispatchers are gone and
        /// before the devices close, runs again on an abnormal exit, and the
        /// next engine start lifts it. A DualShock 4 takes USB route 0.</summary>
        [Fact]
        public void TheEngine_ReleasesTheRoutesWhenItStops_AndResumesThemWhenItStarts()
        {
            string im = RepoFile("PadForge.App", "Common", "Input", "InputManager.cs").Replace("\r\n", "\n");
            int stop = im.IndexOf("public void Stop()", StringComparison.Ordinal);
            int destroy = im.IndexOf("DestroyAllVirtualControllers();", stop, StringComparison.Ordinal);
            int release = im.IndexOf("UserEffectsDispatcher.ReleaseAudioRoutes();", stop, StringComparison.Ordinal);
            int close = im.IndexOf("CloseAllDevices();", stop, StringComparison.Ordinal);
            Assert.True(stop > 0 && destroy > stop && release > destroy && close > release);

            int quiesce = im.IndexOf("public void QuiesceOutputs()", StringComparison.Ordinal);
            int quiesceEnd = im.IndexOf("private void StopAllForceFeedback()", quiesce, StringComparison.Ordinal);
            Assert.InRange(im.IndexOf("try { UserEffectsDispatcher.ReleaseAudioRoutes(); } catch { }", quiesce, StringComparison.Ordinal),
                quiesce, quiesceEnd);

            int start = im.IndexOf("public void Start()", StringComparison.Ordinal);
            int running = im.IndexOf("_running = true;", start, StringComparison.Ordinal);
            Assert.InRange(im.IndexOf("UserEffectsDispatcher.ResumeAudioRoutes();", start, StringComparison.Ordinal),
                start, running);

            string dispatcher = RepoFile("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs").Replace("\r\n", "\n");
            Assert.Contains("NoteDeliveredRoute(w.Device, w.Path, w.Profile, w.Fields);", dispatcher);
            Assert.Contains("if (PlaysToSpeaker(ud.InstanceGuid))", dispatcher);
            Assert.Contains("Ds4UsbAudio.BuildRouteReport(Ds4UsbAudio.RouteStereoHeadset), stampSonyBtCrc: false);", dispatcher);
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
