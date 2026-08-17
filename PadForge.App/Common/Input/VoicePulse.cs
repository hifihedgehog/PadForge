using System;
using System.Collections.Concurrent;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Pulse store for voice-phrase buttons on microphone-bearing pads
    /// (issue #317). Recognition stamps a pulse here per pad; the engine's
    /// <see cref="SdlDeviceWrapper.ExternalVoiceAugment"/> hook applies it
    /// into the pad's state every poll, so a spoken phrase reads as an
    /// ordinary button press on the pad that heard it.
    ///
    /// Phrase buttons live at <see cref="ButtonBase"/> + the phrase's stable
    /// registry index (with the "Any Phrase" pulse at ButtonBase itself),
    /// far above any physical or extended button, so they can never collide
    /// with a real input and the registry can grow without touching the
    /// pad's own surface.
    ///
    /// The pulse contract is copied from the NFC lane, latch bug included:
    /// 175 ms so a 60 Hz macro poll catches one clean rising-then-falling
    /// edge, and the state is REWRITTEN every poll (true during the pulse,
    /// false after) because skipping the clear once latched a button
    /// forever.
    /// </summary>
    internal static class VoicePulse
    {
        /// <summary>First raw-button index used for voice phrases on a pad.
        /// Single-sourced from the Engine's descriptor grammar so the
        /// "Voice Phrase N" read lands exactly where this stamp writes.</summary>
        public const int ButtonBase =
            PadForge.Engine.Common.Mapping.SourceCoercion.VoicePhraseButtonBase;

        private const int PulseMs = 175;

        // Per-pad pulse expiries, indexed by the phrase's registry button
        // (0 = Any Phrase). Sized so ButtonBase + MaxButton stays in range.
        private static readonly ConcurrentDictionary<Guid, long[]> _pulses = new();

        /// <summary>Stamps a recognition onto a pad: the Any Phrase pulse
        /// plus, when the phrase is registered, its own button's pulse.</summary>
        public static void Stamp(Guid padGuid, int registryButton)
        {
            var arr = _pulses.GetOrAdd(padGuid, _ => new long[CustomInputState.MaxButtons - ButtonBase]);
            long until = Environment.TickCount64 + PulseMs;
            arr[0] = until;
            if (registryButton > 0 && registryButton < arr.Length)
                arr[registryButton] = until;
        }

        /// <summary>The engine hook target. Cheap for every non-voice pad:
        /// one dictionary miss.</summary>
        public static void Apply(Guid padGuid, CustomInputState state)
        {
            if (!_pulses.TryGetValue(padGuid, out var arr)) return;
            long now = Environment.TickCount64;
            for (int i = 0; i < arr.Length; i++)
            {
                long until = arr[i];
                if (until == 0 && !state.Buttons[ButtonBase + i]) continue;
                bool pressed = until != 0 && now < until;
                if (!pressed) arr[i] = 0;
                state.Buttons[ButtonBase + i] = pressed;
            }
        }

        /// <summary>Drops a pad's pulse state (device gone).</summary>
        public static void Forget(Guid padGuid) => _pulses.TryRemove(padGuid, out _);
    }
}
