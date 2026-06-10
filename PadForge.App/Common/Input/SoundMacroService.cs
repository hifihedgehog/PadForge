using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Sound playback for macro actions (issue #83). Plays .wav / .mp3 /
    /// .m4a / .aac / .wma / .flac files through a per-slot mixer so each
    /// pad routes its sounds to its own output device — the system default,
    /// or a specific render endpoint such as a USB DualSense's "Wireless
    /// Controller" speakers for Wii-remote-style from-the-controller audio.
    ///
    /// <para><b>Threading.</b> <see cref="Play"/> is called from the polling
    /// thread inside macro execution and must not block. Decoding happens
    /// once per file on the thread pool (Media Foundation), after which the
    /// decoded PCM is cached and playback starts instantly. The NAudio
    /// mixer (<see cref="MixingSampleProvider"/>) is internally locked, so
    /// adding/removing sounds is safe from any thread.</para>
    ///
    /// <para><b>Loop lifecycle.</b> A looping sound is keyed to the macro
    /// that started it. It stops when (a) a <c>SoundStop</c> action runs on
    /// the slot, (b) the owning macro's execution ends by trigger release
    /// (the While-Held / Until-Release path in Step 4b), (c) the user hits
    /// Stop All on the Audio tab, or (d) the engine stops. One-shot sounds
    /// remove themselves from the mixer when they finish.</para>
    /// </summary>
    public static class SoundMacroService
    {
        private const int MaxPads = 16;
        private const int MixSampleRate = 44100;
        private const int MixChannels = 2;
        private const long CacheByteCap = 128L * 1024 * 1024; // decoded PCM cap

        private static readonly object _lock = new();

        // ── Decoded-file cache ──
        private sealed class CachedSound
        {
            public float[] Samples;        // 44.1k stereo interleaved
            public long LastUsedTicks;
            public long Bytes => (long)Samples.Length * sizeof(float);
        }
        private static readonly Dictionary<string, CachedSound> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        // ── Per-slot output (device + mixer) ──
        private sealed class SlotOutput
        {
            public string DeviceId = "";    // "" = system default render endpoint
            public IWavePlayer Player;
            public MixingSampleProvider Mixer;
            public bool IsControllerEndpoint; // DualSense/DS4 USB audio device
        }
        private static readonly SlotOutput[] _outputs = new SlotOutput[MaxPads];
        private static readonly int[] _masterVolume = CreateFilled(100);

        private static int[] CreateFilled(int v)
        {
            var a = new int[MaxPads];
            for (int i = 0; i < a.Length; i++) a[i] = v;
            return a;
        }

        // Set when the slot's endpoint is the physical controller's own USB
        // audio device; consumed by the DS5 effect dispatcher, which owns the
        // pad's output report. The DualSense routes its USB stereo to the
        // headphone path by default and keeps the internal speaker silent —
        // playing through the speaker requires the output report to select
        // the speaker path and set a speaker volume (verified against
        // dualsensectl: valid_flag0 bits 0x20|0x80, audio_flags path 3<<4,
        // speaker volume effective 0x3D-0x64).
        private static readonly bool[] _speakerRoute = new bool[MaxPads];
        private static readonly bool[] _speakerRouteCleared = new bool[MaxPads];

        /// <summary>True when the slot's sounds target the physical
        /// controller's own audio endpoint, so the DS5 dispatcher should
        /// assert the speaker output path + volume in the output report.</summary>
        public static bool WantsControllerSpeaker(int slot)
            => (uint)slot < MaxPads && _speakerRoute[slot];

        /// <summary>Speaker volume byte for the output report. The firmware's
        /// effective range tops out at 0x64; map the slot master volume onto
        /// 0..0x64 linearly.</summary>
        public static byte SpeakerVolumeByte(int slot)
        {
            int v = (uint)slot < MaxPads ? _masterVolume[slot] : 100;
            return (byte)(v * 0x64 / 100);
        }

        /// <summary>One-shot: true exactly once after the slot's routing
        /// switches away from the controller endpoint, so the dispatcher can
        /// restore the default headphone path instead of leaving the speaker
        /// path latched in firmware.</summary>
        public static bool TryConsumeSpeakerRouteCleared(int slot)
        {
            if ((uint)slot >= MaxPads) return false;
            lock (_lock)
            {
                if (!_speakerRouteCleared[slot]) return false;
                _speakerRouteCleared[slot] = false;
                return true;
            }
        }

        // ── Active sounds, keyed by owning (slot, macro) for targeted stops ──
        private sealed class ActiveSound
        {
            public ISampleProvider MixerInput;   // what was added to the mixer
            public VolumeSampleProvider Volume;  // live master-volume retune
            public float ActionVolume;           // 0..1, from the action
            public bool Loop;
            public object MacroKey;
            public string FilePath;              // loop idempotence key
        }
        private static readonly List<ActiveSound>[] _active = CreateLists();

        private static List<ActiveSound>[] CreateLists()
        {
            var a = new List<ActiveSound>[MaxPads];
            for (int i = 0; i < a.Length; i++) a[i] = new List<ActiveSound>();
            return a;
        }

        // ─────────────────────────────────────────────
        //  Output devices (for the Audio tab picker)
        // ─────────────────────────────────────────────

        /// <summary>Active render endpoints as (id, friendly name). The
        /// empty-id entry meaning "system default" is the UI's to add.</summary>
        public static List<(string Id, string Name)> GetOutputDevices()
        {
            var list = new List<(string, string)>();
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try { list.Add((dev.ID, dev.FriendlyName)); }
                    finally { dev.Dispose(); }
                }
            }
            catch { /* no audio service — picker shows default only */ }
            return list;
        }

        // ─────────────────────────────────────────────
        //  Per-slot configuration
        // ─────────────────────────────────────────────

        /// <summary>Routes a slot's sounds to a render endpoint ("" = system
        /// default). A change tears down the slot's current output; the next
        /// sound recreates it on the new device.</summary>
        public static void SetSlotDevice(int slot, string deviceId)
        {
            if ((uint)slot >= MaxPads) return;
            deviceId ??= "";

            // Resolve the endpoint's friendly name OUTSIDE the lock (COM
            // enumeration). "Wireless Controller" is the Sony USB product
            // string (not localized), so it identifies a DualSense / DS4
            // audio endpoint across Windows display languages.
            bool isController = false;
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    using var en = new MMDeviceEnumerator();
                    using var dev = en.GetDevice(deviceId);
                    isController = (dev.FriendlyName ?? "")
                        .IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { }
            }

            lock (_lock)
            {
                var o = _outputs[slot];
                if (o != null && string.Equals(o.DeviceId, deviceId, StringComparison.Ordinal))
                    return;
                bool wasController = _speakerRoute[slot];
                TeardownSlotOutput_NoLock(slot);
                _outputs[slot] = new SlotOutput { DeviceId = deviceId, IsControllerEndpoint = isController };
                _speakerRoute[slot] = isController;
                if (wasController && !isController)
                    _speakerRouteCleared[slot] = true; // dispatcher restores headphone path once
            }
        }

        /// <summary>Per-slot master volume (0-100). Applied multiplicatively
        /// with each action's own volume; live sounds retune immediately.</summary>
        public static void SetSlotVolume(int slot, int volumePct)
        {
            if ((uint)slot >= MaxPads) return;
            int v = Math.Clamp(volumePct, 0, 100);
            lock (_lock)
            {
                _masterVolume[slot] = v;
                foreach (var a in _active[slot])
                    a.Volume.Volume = a.ActionVolume * (v / 100f);
            }
        }

        // ─────────────────────────────────────────────
        //  Playback
        // ─────────────────────────────────────────────

        /// <summary>Plays a sound file on the slot's output. Non-blocking:
        /// an uncached file decodes on the thread pool first (one-time per
        /// file), a cached file starts within the mixer's buffer latency.
        /// <paramref name="macroKey"/> identifies the starting macro so
        /// trigger-release / SoundStop can stop what it started.</summary>
        public static void Play(int slot, object macroKey, string filePath, int volumePct, bool loop)
        {
            if ((uint)slot >= MaxPads || string.IsNullOrWhiteSpace(filePath)) return;

            CachedSound snd;
            lock (_lock)
            {
                // Loop idempotence: an Until-Release macro restarts its action
                // list every cycle while held — re-firing a loop that this
                // macro already has running for this file must not stack a
                // second instance.
                if (loop && macroKey != null)
                {
                    foreach (var a in _active[slot])
                    {
                        if (a.Loop && ReferenceEquals(a.MacroKey, macroKey)
                            && string.Equals(a.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                }

                _cache.TryGetValue(filePath, out snd);
            }
            if (snd != null)
            {
                snd.LastUsedTicks = Environment.TickCount64;
                StartCached(slot, macroKey, snd, filePath, volumePct, loop);
                return;
            }

            // First use of this file — decode off-thread, then start.
            Task.Run(() =>
            {
                var decoded = DecodeAndCache(filePath);
                if (decoded != null)
                    StartCached(slot, macroKey, decoded, filePath, volumePct, loop);
            });
        }

        /// <summary>Plays a short generated beep (880 Hz, 200 ms) for the
        /// Audio tab's Test button — verifies the device route with no file.</summary>
        public static void PlayTestBeep(int slot)
        {
            if ((uint)slot >= MaxPads) return;
            var gen = new SignalGenerator(MixSampleRate, MixChannels)
            {
                Type = SignalGeneratorType.Sin,
                Frequency = 880,
                Gain = 0.4,
            };
            ISampleProvider beep = new OffsetSampleProvider(gen)
            {
                Take = TimeSpan.FromMilliseconds(200),
            };
            beep = new FadeInOutSampleProvider(beep, initiallySilent: false);
            StartProvider(slot, macroKey: null, beep, filePath: null, actionVolume: 1f, loop: false);
        }

        /// <summary>Stops every loop the given macro started on the slot
        /// (one-shots are left to finish — a release shouldn't clip an
        /// explosion). Called by Step 4b when a While-Held / Until-Release
        /// macro's trigger releases.</summary>
        public static void StopLoopsForMacro(int slot, object macroKey)
        {
            if ((uint)slot >= MaxPads || macroKey == null) return;
            lock (_lock)
            {
                var mixer = _outputs[slot]?.Mixer;
                var list = _active[slot];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Loop && ReferenceEquals(list[i].MacroKey, macroKey))
                    {
                        mixer?.RemoveMixerInput(list[i].MixerInput);
                        list.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Stops every sound on the slot (the SoundStop action and
        /// the Audio tab's Stop All button).</summary>
        public static void StopSlot(int slot)
        {
            if ((uint)slot >= MaxPads) return;
            lock (_lock)
            {
                var mixer = _outputs[slot]?.Mixer;
                foreach (var a in _active[slot])
                    mixer?.RemoveMixerInput(a.MixerInput);
                _active[slot].Clear();
            }
        }

        /// <summary>Engine shutdown: stop everything and release the audio clients.</summary>
        public static void StopAll()
        {
            lock (_lock)
            {
                for (int s = 0; s < MaxPads; s++)
                {
                    _active[s].Clear();
                    TeardownSlotOutput_NoLock(s);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Internals
        // ─────────────────────────────────────────────

        private static void StartCached(int slot, object macroKey, CachedSound snd, string filePath, int volumePct, bool loop)
        {
            ISampleProvider src = new CachedSoundProvider(snd.Samples, loop);
            StartProvider(slot, macroKey, src, filePath, Math.Clamp(volumePct, 0, 100) / 100f, loop);
        }

        private static void StartProvider(int slot, object macroKey, ISampleProvider src, string filePath, float actionVolume, bool loop)
        {
            try
            {
                lock (_lock)
                {
                    var o = EnsureOutput_NoLock(slot);
                    if (o?.Mixer == null) return;

                    var vol = new VolumeSampleProvider(src)
                    {
                        Volume = actionVolume * (_masterVolume[slot] / 100f),
                    };
                    var entry = new ActiveSound
                    {
                        MixerInput = vol,
                        Volume = vol,
                        ActionVolume = actionVolume,
                        Loop = loop,
                        MacroKey = macroKey,
                        FilePath = filePath,
                    };
                    // Reap finished one-shots so the bookkeeping list doesn't
                    // grow unboundedly (the mixer already removed them).
                    var live = o.Mixer.MixerInputs as IEnumerable<ISampleProvider>;
                    var liveSet = live != null ? new HashSet<ISampleProvider>(live) : null;
                    if (liveSet != null)
                        _active[slot].RemoveAll(a => !liveSet.Contains(a.MixerInput));

                    _active[slot].Add(entry);
                    o.Mixer.AddMixerInput((ISampleProvider)vol);
                }
            }
            catch
            {
                // Device vanished mid-add — drop the output; the next Play recreates it.
                lock (_lock) TeardownSlotOutput_NoLock(slot);
            }
        }

        private static SlotOutput EnsureOutput_NoLock(int slot)
        {
            var o = _outputs[slot] ??= new SlotOutput();
            if (o.Player != null) return o;

            try
            {
                MMDevice device = null;
                try
                {
                    using var en = new MMDeviceEnumerator();
                    device = string.IsNullOrEmpty(o.DeviceId)
                        ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : en.GetDevice(o.DeviceId);

                    var mixer = new MixingSampleProvider(
                        WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels))
                    {
                        ReadFully = true, // keep the stream open; ended inputs auto-remove
                    };

                    // Feed plain stereo. On a DualSense the extra USB
                    // channels are the HAPTIC ACTUATORS, not the speaker —
                    // duplicating program audio into them buzzes the motors.
                    // The speaker is reached through the firmware's output
                    // path (the DS5 dispatcher asserts it in the output
                    // report when this slot routes to the controller); that
                    // path taps the program channels, so for controller
                    // endpoints the stereo is mono-summed onto both channels
                    // — the speaker path hears the full mix no matter which
                    // channel the firmware taps.
                    ISampleProvider feed = o.IsControllerEndpoint
                        ? new MonoSumProvider(mixer)
                        : mixer;

                    var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
                    player.Init(feed);
                    player.Play();
                    o.Mixer = mixer;
                    o.Player = player;
                }
                finally
                {
                    device?.Dispose();
                }
            }
            catch
            {
                // Selected endpoint missing/unplugged. Leave Player null; a
                // later Play retries (and the user can repick the device).
                o.Player = null;
                o.Mixer = null;
            }
            return o;
        }

        private static void TeardownSlotOutput_NoLock(int slot)
        {
            var o = _outputs[slot];
            if (o == null) return;
            try { o.Player?.Stop(); } catch { }
            try { o.Player?.Dispose(); } catch { }
            o.Player = null;
            o.Mixer = null;
            _active[slot].Clear();
        }

        private static CachedSound DecodeAndCache(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                // Media Foundation decodes wav/mp3/m4a/aac/wma/flac on Win10+.
                using var reader = new MediaFoundationReader(filePath);
                ISampleProvider sp = reader.ToSampleProvider();
                if (sp.WaveFormat.Channels == 1)
                    sp = new MonoToStereoSampleProvider(sp);
                else if (sp.WaveFormat.Channels != MixChannels)
                    return null; // >2ch sources are out of scope
                if (sp.WaveFormat.SampleRate != MixSampleRate)
                    sp = new WdlResamplingSampleProvider(sp, MixSampleRate);

                var all = new List<float>(1 << 18);
                var buf = new float[MixSampleRate]; // ~0.5s stereo per read
                int read;
                while ((read = sp.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < read; i++) all.Add(buf[i]);
                    // Hard stop at the cache cap so one giant file can't
                    // balloon memory; the tail is dropped.
                    if ((long)all.Count * sizeof(float) > CacheByteCap) break;
                }
                if (all.Count == 0) return null;

                var snd = new CachedSound
                {
                    Samples = all.ToArray(),
                    LastUsedTicks = Environment.TickCount64,
                };

                lock (_lock)
                {
                    // Evict least-recently-used entries past the cap.
                    long total = _cache.Values.Sum(c => c.Bytes) + snd.Bytes;
                    while (total > CacheByteCap && _cache.Count > 0)
                    {
                        var oldest = _cache.OrderBy(kv => kv.Value.LastUsedTicks).First();
                        total -= oldest.Value.Bytes;
                        _cache.Remove(oldest.Key);
                    }
                    _cache[filePath] = snd;
                }
                return snd;
            }
            catch
            {
                return null; // unreadable/unsupported file — the action is a no-op
            }
        }

        /// <summary>Mono-sums the stereo mix onto both channels. Used for
        /// controller endpoints so the firmware speaker path (which taps one
        /// program channel) carries the full mix.</summary>
        private sealed class MonoSumProvider : ISampleProvider
        {
            private readonly ISampleProvider _stereo;

            public MonoSumProvider(ISampleProvider stereo)
            {
                _stereo = stereo;
                WaveFormat = stereo.WaveFormat;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _stereo.Read(buffer, offset, count);
                for (int f = 0; f + 1 < read; f += 2)
                {
                    float m = (buffer[offset + f] + buffer[offset + f + 1]) * 0.5f;
                    buffer[offset + f] = m;
                    buffer[offset + f + 1] = m;
                }
                return read;
            }
        }

        /// <summary>Reads cached PCM, optionally looping forever. Returning 0
        /// at the end makes the mixer drop the input (one-shot cleanup).</summary>
        private sealed class CachedSoundProvider : ISampleProvider
        {
            private readonly float[] _samples;
            private readonly bool _loop;
            private long _pos;

            public CachedSoundProvider(float[] samples, bool loop)
            {
                _samples = samples;
                _loop = loop;
            }

            public WaveFormat WaveFormat { get; } =
                WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels);

            public int Read(float[] buffer, int offset, int count)
            {
                int written = 0;
                while (written < count)
                {
                    long remain = _samples.Length - _pos;
                    if (remain <= 0)
                    {
                        if (!_loop) break;
                        _pos = 0;
                        remain = _samples.Length;
                    }
                    int n = (int)Math.Min(remain, count - written);
                    Array.Copy(_samples, _pos, buffer, offset + written, n);
                    _pos += n;
                    written += n;
                }
                return written;
            }
        }
    }
}
