using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Sound playback for macro actions (issue #83). Decodes .wav / .mp3 /
    /// .m4a / .aac / .wma / .flac via Media Foundation, caches the PCM, and
    /// plays each sound into the pad's audio targets.
    ///
    /// <para><b>Routing is per assigned device, by convention.</b> When the
    /// slot has speaker-capable Sony pads (DualSense / Edge / DS4), the
    /// sound is placed into every such pad's controller sink owned by
    /// <see cref="AudioPassthroughService"/> — USB Audio Class endpoint or
    /// the Bluetooth HID audio stream, both per the DualSenseY-v2 reference.
    /// When the slot has no speaker-capable pad, the sound falls back to the
    /// system default output. There is no output picker.</para>
    ///
    /// <para><b>Threading.</b> <see cref="Play"/> is called from the polling
    /// thread inside macro execution and must not block: uncached files
    /// decode on the thread pool, cached files start within mixer latency.</para>
    ///
    /// <para><b>Loop lifecycle.</b> A looping sound stops on (a) a SoundStop
    /// action, (b) the owning While-Held / Until-Release macro's trigger
    /// release, (c) Stop All on the Audio tab, (d) engine stop. One-shots
    /// play out and self-remove from their mixers.</para>
    /// </summary>
    public static class SoundMacroService
    {
        private const int MaxPads = 16;
        internal const int MixSampleRate = 48000; // matches the controller sinks
        private const int MixChannels = 2;
        private const long CacheByteCap = 128L * 1024 * 1024;

        private static readonly object _lock = new();

        // ── Decoded-file cache ──
        private sealed class CachedSound
        {
            public float[] Samples;        // 48k stereo interleaved
            public long LastUsedTicks;
            public long Bytes => (long)Samples.Length * sizeof(float);
        }
        private static readonly Dictionary<string, CachedSound> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        // ── System-default fallback output, per slot ──
        private sealed class SlotOutput
        {
            public IWavePlayer Player;
            public MixingSampleProvider Mixer;
        }
        private static readonly SlotOutput[] _outputs = new SlotOutput[MaxPads];
        private static readonly bool[] _controllerRouted = new bool[MaxPads];
        private static readonly int[] _masterVolume = CreateFilled(100);

        private static int[] CreateFilled(int v)
        {
            var a = new int[MaxPads];
            for (int i = 0; i < a.Length; i++) a[i] = v;
            return a;
        }

        // ── Active sounds: one logical sound = placements in 1..N mixers ──
        private sealed class ActiveSound
        {
            public List<(MixingSampleProvider Mixer, VolumeSampleProvider Volume, ISampleProvider Input)> Placements = new();
            public float ActionVolume;           // 0..1, from the action
            public bool Loop;
            public object MacroKey;
            public string FilePath;              // loop idempotence key
            public bool OnController;            // sinks own loudness via the firmware volume byte
        }
        private static readonly List<ActiveSound>[] _active = CreateLists();

        private static List<ActiveSound>[] CreateLists()
        {
            var a = new List<ActiveSound>[MaxPads];
            for (int i = 0; i < a.Length; i++) a[i] = new List<ActiveSound>();
            return a;
        }

        // ─────────────────────────────────────────────
        //  Per-slot configuration
        // ─────────────────────────────────────────────

        /// <summary>Per-slot master volume (0-100). On the PC fallback output
        /// it scales the sample amplitude; on controller sinks loudness is the
        /// firmware speaker volume byte (the DS5 dispatcher reads
        /// <see cref="GetSlotVolume"/> per output report), so live sounds
        /// retune immediately on both paths.</summary>
        public static void SetSlotVolume(int slot, int volumePct)
        {
            if ((uint)slot >= MaxPads) return;
            int v = Math.Clamp(volumePct, 0, 100);
            lock (_lock)
            {
                _masterVolume[slot] = v;
                // Controller-routed sounds get master volume from the firmware
                // speaker volume byte (live, per output report); only the PC
                // fallback applies it in the sample domain.
                foreach (var a in _active[slot])
                    foreach (var p in a.Placements)
                        p.Volume.Volume = a.ActionVolume * (a.OnController ? 1f : v / 100f);
            }
        }

        /// <summary>Current per-slot master volume (0-100).</summary>
        public static int GetSlotVolume(int slot)
            => (uint)slot < MaxPads ? _masterVolume[slot] : 100;

        /// <summary>Set by <see cref="AudioPassthroughService"/>: when a slot
        /// has at least one live controller sink, the system-default fallback
        /// output is torn down (sounds go to the controller instead).</summary>
        internal static void SetSlotControllerRouted(int slot, bool routed)
        {
            if ((uint)slot >= MaxPads) return;
            lock (_lock)
            {
                if (_controllerRouted[slot] == routed) return;
                _controllerRouted[slot] = routed;
                if (routed) TeardownSlotOutput_NoLock(slot);
            }
        }

        // ─────────────────────────────────────────────
        //  Playback
        // ─────────────────────────────────────────────

        /// <summary>Plays a sound file on the slot's targets (controller
        /// sinks, or the system default when the slot has none). Non-blocking.
        /// <paramref name="macroKey"/> identifies the starting macro so
        /// trigger-release / SoundStop can stop what it started.</summary>
        public static void Play(int slot, object macroKey, string filePath, int volumePct, bool loop)
        {
            if ((uint)slot >= MaxPads || string.IsNullOrWhiteSpace(filePath)) return;

            CachedSound snd;
            lock (_lock)
            {
                // Loop idempotence: an Until-Release macro restarts its action
                // list every cycle while held — re-firing a loop this macro
                // already has running for this file must not stack a second
                // instance.
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
                StartSound(slot, macroKey, snd, filePath, volumePct, loop);
                return;
            }

            Task.Run(() =>
            {
                var decoded = DecodeAndCache(filePath);
                if (decoded != null)
                    StartSound(slot, macroKey, decoded, filePath, volumePct, loop);
            });
        }

        /// <summary>Short generated beep (880 Hz, 200 ms) for the Audio tab's
        /// Test button. Plays a short beep through ONLY the assigned device
        /// selected in the Audio tab (every tab right of the assigned-devices
        /// list is per-device), or nowhere when that device has no live speaker
        /// sink. It never fans out to the slot's other pads.</summary>
        public static void PlayTestBeep(int slot, Guid deviceGuid)
        {
            if ((uint)slot >= MaxPads) return;
            StartPlacements(slot, macroKey: null, filePath: null, actionVolume: 1f, loop: false,
                makeInput: () =>
                {
                    var gen = new SignalGenerator(MixSampleRate, MixChannels)
                    {
                        Type = SignalGeneratorType.Sin,
                        Frequency = 880,
                        Gain = 1.0, // full scale so the Test button demos max loudness
                    };
                    return new OffsetSampleProvider(gen) { Take = TimeSpan.FromMilliseconds(200) };
                },
                deviceFilter: deviceGuid);
        }

        /// <summary>Stops every loop the given macro started on the slot
        /// (one-shots play out — a release shouldn't clip an explosion).</summary>
        public static void StopLoopsForMacro(int slot, object macroKey)
        {
            if ((uint)slot >= MaxPads || macroKey == null) return;
            lock (_lock)
            {
                var list = _active[slot];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Loop && ReferenceEquals(list[i].MacroKey, macroKey))
                    {
                        RemovePlacements_NoLock(list[i]);
                        list.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Stops every sound on the slot (SoundStop action / Stop All).</summary>
        public static void StopSlot(int slot)
        {
            if ((uint)slot >= MaxPads) return;
            lock (_lock)
            {
                foreach (var a in _active[slot])
                    RemovePlacements_NoLock(a);
                _active[slot].Clear();
            }
        }

        /// <summary>Engine shutdown: stop everything, release audio clients.</summary>
        public static void StopAll()
        {
            lock (_lock)
            {
                for (int s = 0; s < MaxPads; s++)
                {
                    foreach (var a in _active[s]) RemovePlacements_NoLock(a);
                    _active[s].Clear();
                    TeardownSlotOutput_NoLock(s);
                }
            }
            AudioPassthroughService.Shutdown();
            WiiSpeakerService.Shutdown();
        }

        // ─────────────────────────────────────────────
        //  Internals
        // ─────────────────────────────────────────────

        private static void StartSound(int slot, object macroKey, CachedSound snd, string filePath, int volumePct, bool loop)
        {
            StartPlacements(slot, macroKey, filePath, Math.Clamp(volumePct, 0, 100) / 100f, loop,
                makeInput: () => new CachedSoundProvider(snd.Samples, loop));
        }

        private static void StartPlacements(int slot, object macroKey, string filePath,
            float actionVolume, bool loop, Func<ISampleProvider> makeInput, Guid? deviceFilter = null)
        {
            try
            {
                // Controller sinks first (per assigned device, by convention);
                // fall back to the system default output only when the slot
                // has no speaker-capable pad at all. pendingActivation means
                // the worker is still opening the pad's transport — drop the
                // sound rather than leak it to the PC speakers; the next
                // trigger lands on the pad.
                var targets = AudioPassthroughService.GetSlotSinkMixers(slot, out bool pendingActivation, deviceFilter);
                // Wii Remote speakers are a parallel sink family (#146); fan the
                // same macro sound into them too.
                targets.AddRange(WiiSpeakerService.GetSlotSinkMixers(slot, deviceFilter));

                lock (_lock)
                {
                    bool onController = targets.Count > 0;
                    if (!onController)
                    {
                        // Device-scoped play (the Audio-tab test signal targets only the
                        // selected device): play on that device or nowhere, never on the
                        // PC speakers.
                        if (deviceFilter != null) return;
                        if (pendingActivation || _controllerRouted[slot]) return;
                        var o = EnsureDefaultOutput_NoLock(slot);
                        if (o?.Mixer == null) return;
                        targets = new List<MixingSampleProvider> { o.Mixer };
                    }

                    var entry = new ActiveSound
                    {
                        ActionVolume = actionVolume,
                        Loop = loop,
                        MacroKey = macroKey,
                        FilePath = filePath,
                        OnController = onController,
                    };
                    foreach (var mixer in targets)
                    {
                        var input = makeInput();
                        var vol = new VolumeSampleProvider(input)
                        {
                            // Controller sinks play full-scale samples; the
                            // firmware speaker volume byte carries the master
                            // volume there. The PC fallback has no such byte.
                            Volume = actionVolume * (onController ? 1f : _masterVolume[slot] / 100f),
                        };
                        entry.Placements.Add((mixer, vol, vol));
                        mixer.AddMixerInput((ISampleProvider)vol);
                    }

                    // Reap finished one-shots (their mixers already dropped them).
                    _active[slot].RemoveAll(a =>
                        a.Placements.Count == 0
                        || a.Placements.All(p => !p.Mixer.MixerInputs.Contains(p.Input)));
                    _active[slot].Add(entry);
                }
            }
            catch
            {
                lock (_lock) TeardownSlotOutput_NoLock(slot);
            }
        }

        private static void RemovePlacements_NoLock(ActiveSound a)
        {
            foreach (var p in a.Placements)
            {
                try { p.Mixer.RemoveMixerInput(p.Input); } catch { }
            }
            a.Placements.Clear();
        }

        private static SlotOutput EnsureDefaultOutput_NoLock(int slot)
        {
            var o = _outputs[slot] ??= new SlotOutput();
            if (o.Player != null) return o;
            try
            {
                var mixer = new MixingSampleProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels))
                {
                    ReadFully = true, // keep the stream open; ended inputs auto-remove
                };
                var player = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 60);
                player.Init(mixer);
                player.Play();
                o.Mixer = mixer;
                o.Player = player;
            }
            catch
            {
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
        }

        private static CachedSound DecodeAndCache(string filePath)
        {
            try
            {
                // Sound-package reference (pfsound://Package/entry): bytes
                // come straight out of the registered package zip — no
                // extraction, no temp files. Plain paths use the file
                // route unchanged. The cache key is the reference string
                // either way.
                MemoryStream packageStream = null;
                if (SoundPackageManager.IsPackageRef(filePath))
                {
                    byte[] bytes = SoundPackageManager.TryReadSound(filePath);
                    if (bytes == null) return null;
                    packageStream = new MemoryStream(bytes, writable: false);
                }
                else if (!File.Exists(filePath)) return null;

                // Media Foundation decodes wav/mp3/m4a/aac/wma/flac on Win10+.
                using var reader = packageStream != null
                    ? (WaveStream)new StreamMediaFoundationReader(packageStream)
                    : new MediaFoundationReader(filePath);
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
