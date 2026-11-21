using System;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Haptics a game plays as audio on a DualSense persona, turned into
    /// rumble (discussion #522). The DualSense (PS5): Full and DualSense Edge
    /// (PS5): Full profiles carry the pad's two voice-coil actuators as
    /// channels 3 and 4 of their audio stream. A game that drives them that
    /// way sends no rumble: its output frames leave the rumble emulation bits
    /// clear, which is how audio haptics stay on (SDL_hidapi_ps5.c: "Leaving
    /// emulated rumble bits off will restore audio haptics"), so the slot's
    /// vibration state reads 0.
    ///
    /// <para>The loudness of each haptic channel stands in for that rumble.
    /// The left actuator drives the low (left) motor and the right actuator
    /// the high (right) motor, the pairing SDL and hid-playstation use when
    /// they play rumble on the actuators: SDL's rumble_left takes the
    /// low-frequency motor, hid-playstation's motor_left the strong
    /// magnitude. Every reader of the slot's rumble layers the level on under
    /// max(), the way macro rumble joins, ahead of the Force Feedback group's
    /// gain, motor strengths and swap, and Bass Shakers plays it too.</para>
    ///
    /// <para>A device that plays the haptic track itself takes none of it: a
    /// DualSense being fed the persona's haptic channels, whose rumble bits
    /// would switch it out of audio haptics, and a Steam or Switch pad with
    /// Play DualSense Haptics on This Controller on. chiaki-ng makes the same
    /// reduction for controllers without voice coils and skips it while a
    /// DualSense plays the haptics (gui/src/streamsession.cpp,
    /// PushHapticsFrame).</para>
    /// </summary>
    internal static class PersonaHapticRumble
    {
        /// <summary>The loudness follower's time constant, in seconds.</summary>
        internal const double TimeConstant = 0.020;

        /// <summary>A level older than this is not merged: the host closed
        /// the stream or the persona went away.</summary>
        internal const long StaleMs = 100;

        /// <summary>The follower publishes its level at most this often, the
        /// 10 ms tick the haptic-tone sinks run on. The level is a 20 ms
        /// envelope, so a faster publish carries nothing new, and it caps
        /// motor writes at a game's own rumble rate whatever window size the
        /// host's audio arrives in.</summary>
        internal const long PublishIntervalMs = 10;

        /// <summary>Follows the loudness of one interleaved s16 window's two
        /// haptic channels and returns it as a pack with the left channel on
        /// the low motor and the right on the high motor. The loudness is a
        /// running mean square with a <see cref="TimeConstant"/> time
        /// constant, kept in <paramref name="meanSquareLeft"/> and
        /// <paramref name="meanSquareRight"/> between windows, read the way
        /// <see cref="Engine.Haptics.HapticToneReducer"/> reads PCM loudness:
        /// RMS times 1.4, so a full-scale sine reads full, and nothing below
        /// its silence floor. Pure over its arguments.</summary>
        internal static long Follow(ref double meanSquareLeft, ref double meanSquareRight,
            ReadOnlySpan<byte> pcm, int stride, int leftOffset, int rightOffset, int rateHz)
        {
            if (stride <= 0 || rateHz <= 0 || leftOffset < 0 || rightOffset < 0
                || Math.Max(leftOffset, rightOffset) + 2 > stride)
                return 0L;
            double a = 1.0 - Math.Exp(-1.0 / (TimeConstant * rateHz));
            double l = meanSquareLeft, r = meanSquareRight;
            for (int i = 0; i + stride <= pcm.Length; i += stride)
            {
                double xl = (short)(pcm[i + leftOffset] | (pcm[i + leftOffset + 1] << 8)) / 32768.0;
                double xr = (short)(pcm[i + rightOffset] | (pcm[i + rightOffset + 1] << 8)) / 32768.0;
                l += a * (xl * xl - l);
                r += a * (xr * xr - r);
            }
            meanSquareLeft = l;
            meanSquareRight = r;
            return LfeOutputState.Pack(Level(l), Level(r), 0, 0);
        }

        private static ushort Level(double meanSquare)
        {
            double amp = Math.Min(Math.Sqrt(meanSquare) * 1.4, 1.0);
            return amp < Engine.Haptics.HapticToneReducer.SilenceRms ? (ushort)0 : (ushort)Math.Round(amp * 65535);
        }

        /// <summary><paramref name="level"/>, or 0 once it is more than
        /// <see cref="StaleMs"/> old. Pure.</summary>
        internal static long Fresh(long level, long levelMs, long nowMs)
            => level == 0L || nowMs - levelMs > StaleMs ? 0L : level;

        /// <summary>Layers a level onto <paramref name="raw"/>'s two body
        /// motors under max(). Returns <paramref name="raw"/> unchanged when
        /// the level is 0, or <paramref name="scratch"/> with the merged
        /// motors and every other field copied from <paramref name="raw"/>,
        /// the shape of <see cref="MacroRumbleOverride.Merge"/>.</summary>
        internal static Vibration Merge(Vibration raw, long level, Vibration scratch)
        {
            if (raw == null || scratch == null) return raw;
            ushort low = LfeOutputState.Low(level), high = LfeOutputState.High(level);
            if (low == 0 && high == 0) return raw;
            scratch.LeftMotorSpeed = Math.Max(raw.LeftMotorSpeed, low);
            scratch.RightMotorSpeed = Math.Max(raw.RightMotorSpeed, high);
            scratch.LeftTriggerMotorSpeed = raw.LeftTriggerMotorSpeed;
            scratch.RightTriggerMotorSpeed = raw.RightTriggerMotorSpeed;
            scratch.HasDirectionalData = raw.HasDirectionalData;
            scratch.HasConditionData = raw.HasConditionData;
            scratch.EffectType = raw.EffectType;
            scratch.SignedMagnitude = raw.SignedMagnitude;
            scratch.Direction = raw.Direction;
            scratch.Period = raw.Period;
            scratch.DeviceGain = raw.DeviceGain;
            scratch.ConditionAxisCount = raw.ConditionAxisCount;
            scratch.ConditionAxes = raw.ConditionAxes;
            return scratch;
        }

        /// <summary>Whether <paramref name="device"/> takes the slot's
        /// persona haptics as rumble. Every rumble device does, except one
        /// that plays the haptic track itself. Lock-free, any thread.</summary>
        internal static bool TakesRumble(int slot, Guid device, long nowMs)
            => !AudioPassthroughService.RendersPersonaHaptics(device, nowMs)
            && !HapticToneService.RendersPersonaHaptics(slot, device);

        /// <summary>One caller's reusable state for <see cref="ForDevice"/>:
        /// an all-zero rumble the level is layered onto, the merge's scratch,
        /// and a setting that carries a row's gains with audio rumble off.</summary>
        internal sealed class DeviceScratch
        {
            internal readonly Vibration Zero = new();
            internal readonly Vibration Merged = new();
            internal readonly PadSetting Gains = new() { AudioRumbleEnabled = "0" };
        }

        /// <summary>The persona haptics alone as <paramref name="device"/>
        /// takes them, as the 8-bit right and left motor bytes the Sony
        /// dispatcher writes: for each of the device's rows, the row slot's
        /// fresh level (<see cref="InputManager.WithPersonaHaptics"/>) through
        /// that row's Overall Gain, motor strengths and swap
        /// (<see cref="InputManager.ScaleRumbleForDevice"/>) and its main
        /// motor redirect, the strongest row winning, the same row walk the
        /// dispatcher's full rumble provider makes. No game rumble, macro
        /// rumble, audio rumble, constant force or touchpad pulse joins it.
        /// A row on a slot whose test rumble targets another device
        /// contributes nothing, and a device with no rows reads the slot with
        /// identity gains. (0, 0) when no level is fresh or the device plays
        /// the haptic track itself.</summary>
        internal static (byte right, byte left) ForDevice(InputManager im, int padIndex, Guid device,
            long nowMs, DeviceScratch scratch)
        {
            if (im == null || scratch == null || im.OutputsQuiesced) return (0, 0);
            if ((uint)padIndex >= InputManager.MaxPads) return (0, 0);
            ushort maxL = 0, maxR = 0;
            bool sawRow = false;
            var settings = SettingsManager.UserSettings;
            if (settings != null && device != Guid.Empty)
            {
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null || us.InstanceGuid != device) continue;
                        int slot = us.MapTo;
                        if ((uint)slot >= InputManager.MaxPads) continue;
                        sawRow = true;
                        var tt = im.TestRumbleTargetGuid[slot];
                        if (tt != Guid.Empty && tt != device) continue;
                        AddRow(im, slot, device, us.GetPadSetting(), nowMs, scratch, ref maxL, ref maxR);
                    }
                }
            }
            if (!sawRow) AddRow(im, padIndex, device, null, nowMs, scratch, ref maxL, ref maxR);
            return ((byte)(maxR >> 8), (byte)(maxL >> 8));
        }

        private static void AddRow(InputManager im, int slot, Guid device, PadSetting ps, long nowMs,
            DeviceScratch scratch, ref ushort maxL, ref ushort maxR)
        {
            var v = im.WithPersonaHaptics(slot, device, scratch.Zero, scratch.Merged, nowMs);
            if (ReferenceEquals(v, scratch.Zero)) return;
            PadSetting gains = null;
            if (ps != null)
            {
                // The row's gains only. Its audio rumble is another source,
                // so the copy keeps AudioRumbleEnabled at "0".
                gains = scratch.Gains;
                gains.ForceOverall = ps.ForceOverall;
                gains.LeftMotorStrength = ps.LeftMotorStrength;
                gains.RightMotorStrength = ps.RightMotorStrength;
                gains.ForceSwapMotor = ps.ForceSwapMotor;
            }
            im.ScaleRumbleForDevice(v.LeftMotorSpeed, v.RightMotorSpeed, gains, out ushort l, out ushort r);
            im.GetTriggerRouteMainRedirect(slot, device, out bool zMainL, out bool zMainR);
            if (zMainL) l = 0;
            if (zMainR) r = 0;
            if (l > maxL) maxL = l;
            if (r > maxR) maxR = r;
        }
    }
}
