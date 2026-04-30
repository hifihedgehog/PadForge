using System;
using System.ComponentModel;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Per-virtual-DS5-VC dispatcher for Feature B (user-configured
    /// adaptive trigger / lightbar / audio effects). Subscribes to the
    /// slot's <see cref="PlayStationSlotConfig"/> PropertyChanged and
    /// re-synthesizes + sends the DS5 effect message to every assigned
    /// physical DualSense whenever the user touches a setting on the
    /// Adaptive Triggers or Lighting tab.
    ///
    /// <para>Game-driven Feature A passthrough (handled by
    /// <see cref="DualSensePassthroughDispatcher"/>) runs independently
    /// — game writes win per packet because they fire at high cadence.
    /// Feature B fills the silence between game writes: when the
    /// user-configured layer is enabled and no game has written
    /// recently, the trigger / lightbar settings the user picked here
    /// are what the physical pad reflects.</para>
    ///
    /// <para>The dispatcher writes synchronously on the UI thread when
    /// PropertyChanged fires. The cost is one byte-array allocation
    /// (47 bytes), one synthesizer call, and one
    /// <see cref="SDL_SendGamepadEffect"/> per assigned physical DS5.
    /// Total is well under a millisecond per user-interaction event,
    /// which is bounded by how fast a human can drag a slider.</para>
    /// </summary>
    internal sealed class UserEffectsDispatcher : IDisposable
    {
        private const ushort SonyVid = 0x054C;
        private const ushort PidStandard = 0x0CE6;
        private const ushort PidEdge = 0x0DF2;

        private readonly int _padIndex;
        private PlayStationSlotConfig _config;
        private bool _disposed;

        public UserEffectsDispatcher(int padIndex, PlayStationSlotConfig config)
        {
            _padIndex = padIndex;
            _config = config;
            if (_config != null)
                _config.PropertyChanged += OnConfigChanged;
        }

        /// <summary>Re-binds to a new <see cref="PlayStationSlotConfig"/>
        /// instance. Used when the parent <see cref="PadViewModel"/>
        /// reassigns its config via the setter (e.g. profile load).</summary>
        public void Rebind(PlayStationSlotConfig config)
        {
            if (_disposed) return;
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = config;
            if (_config != null)
                _config.PropertyChanged += OnConfigChanged;
            // Push a snapshot immediately so the assigned DS5 reflects
            // the new config without waiting for the next user edit.
            ApplyOnce();
        }

        /// <summary>Manually trigger one apply pass. Used after the
        /// dispatcher is constructed (initial state) and from Rebind.</summary>
        public void ApplyOnce()
        {
            if (_disposed || _config == null) return;
            DispatchSnapshot();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = null;
        }

        private void OnConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            // Every PlayStationSlotConfig field change re-applies the
            // full message. Synthesis is cheap; the alternative would be
            // a per-field write that misses subtle interactions between
            // enable_bits flags. Last-writer-wins matches the game-driven
            // path's semantics.
            DispatchSnapshot();
        }

        private void DispatchSnapshot()
        {
            if (_config == null) return;

            // Synthesize once per dispatch; reuse the buffer across the
            // multi-DS5 fan-out below.
            var buffer = new byte[Ds5EffectSynthesizer.PayloadSize];
            int len = Ds5EffectSynthesizer.Build(_config, buffer);
            if (len <= 0) return;

            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            if (settings == null || devices == null) return;

            // Resolve assigned DS5 GUIDs.
            var guids = new System.Collections.Generic.List<Guid>(4);
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null) continue;
                    if (us.MapTo != _padIndex) continue;
                    if (us.InstanceGuid == Guid.Empty) continue;
                    guids.Add(us.InstanceGuid);
                }
            }
            if (guids.Count == 0) return;

            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null || !ud.IsOnline) continue;
                    if (ud.VendorId != SonyVid) continue;
                    if (ud.ProdId != PidStandard && ud.ProdId != PidEdge) continue;
                    if (!guids.Contains(ud.InstanceGuid)) continue;

                    IntPtr handle = ud.Device?.GamepadHandle ?? IntPtr.Zero;
                    if (handle == IntPtr.Zero) continue;

                    try
                    {
                        SDL_SendGamepadEffect(handle, buffer, 0, len);
                    }
                    catch
                    {
                        // Device gone stale mid-write — drop and continue.
                    }
                }
            }
        }
    }
}
