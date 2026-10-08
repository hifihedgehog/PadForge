using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One zone of a feature 0x8070 device and the index of its
    /// static effect, the effect whose ID is 0x0001.</summary>
    internal readonly record struct HidppZone(byte Zone, byte StaticEffectIndex);

    /// <summary>
    /// One Logitech device on a HID++ collection (#494): its slot, its name
    /// and kind from feature 0x0005, its haptics (0x19B0), its RGB feature,
    /// and its battery feature. A receiver's paired devices are separate
    /// units on the same collection.
    /// </summary>
    internal sealed record HidppUnit
    {
        public string ChannelPath { get; init; }
        public Guid ContainerId { get; init; }
        public byte DeviceIndex { get; init; }
        public string Name { get; init; }

        /// <summary>Feature 0x0005 function 2, or -1 when the device did not
        /// say. Solaar's table (hidpp20_constants.py:251-260): 0 keyboard,
        /// 1 remote, 2 numpad, 3 mouse, 4 touchpad, 5 trackball, 6 presenter,
        /// 7 receiver.</summary>
        public int DeviceType { get; init; } = -1;

        public byte HapticIndex { get; init; }
        public uint WaveformMask { get; init; }
        public bool FeedbackEnabled { get; init; } = true;

        /// <summary>The RGB feature the device lists: 0x8071, 0x8081, 0x8080
        /// or 0x8070, the first of those it has, or 0 for none.</summary>
        public ushort RgbFeatureId { get; init; }
        public byte RgbIndex { get; init; }

        /// <summary>The zones a 0x8070 device can be lit through directly,
        /// each with its static effect. Empty for every other RGB feature.</summary>
        public HidppZone[] Zones { get; init; } = Array.Empty<HidppZone>();

        /// <summary>0x1004 (unified battery) or 0x1000 (battery status), or
        /// 0 for a device that reports neither.</summary>
        public ushort BatteryFeatureId { get; init; }
        public byte BatteryIndex { get; init; }

        /// <summary>The charge last read, 0..100, or -1 when unknown.</summary>
        public int BatteryPercent { get; init; } = -1;

        /// <summary>The path key: the collection plus the device index.</summary>
        public string Key => ChannelPath + "|" + DeviceIndex.ToString("X2");

        /// <summary>The device plays at least one of the three collisions
        /// rumble picks from.</summary>
        public bool HasHaptics => HapticIndex != 0 && HapticRumbleShaper.Waveform(1, WaveformMask) != null;

        public bool HasRgb => RgbFeatureId != 0;

        /// <summary>Lit directly: a zone-only 0x8070 device, whose claim is a
        /// single SetSWControl (OpenRGB LogitechHIDPP20Controller.cpp:4038-4065,
        /// "the official app and Solaar both do exactly this").</summary>
        public bool DirectRgb => RgbFeatureId == HidppUnitProtocol.ColorLedEffects && Zones.Length > 0;

        /// <summary>A keyboard or a numpad.</summary>
        public bool IsKeyboardKind => DeviceType is 0 or 2;

        /// <summary>A mouse, touchpad or trackball.</summary>
        public bool IsMouseKind => DeviceType is 3 or 4 or 5;
    }

    /// <summary>
    /// The HID++ 2.0 frames beyond haptics that a unit needs (#494), each
    /// from the clones beside the other references.
    ///
    /// <para>0x0005 (OpenRGB LogitechProtocolCommon.h:43-46, Solaar
    /// hidpp20.py:1836-1870): function 0 the name length, 1 a fragment at an
    /// offset, 2 the device type.</para>
    ///
    /// <para>0x8070 COLOR_LED_EFFECTS (libratbag hidpp20.c:871-1051, Solaar
    /// hidpp20.py:1330-1383 and settings_templates.py:3204-3293, OpenRGB
    /// LogitechGLightsyncController.cpp:78-166): function 0 the zone count in
    /// payload 0, 1 a zone's effect count in payload 3, 2 an effect's ID
    /// big-endian in payloads 2 and 3, 3 SetZoneEffect, 8 SetSWControl. The
    /// static effect is ID 0x0001, found by scanning, never by a fixed index,
    /// because the index differs between models (the G203 and G502 pages in
    /// the OpenRGB wiki).</para>
    ///
    /// <para>Battery (Solaar hidpp20.py:1896-1904 and 2191-2265): 0x1004
    /// function 1 gives the percent in payload 0 and, when that is 0, a level
    /// in payload 1. 0x1000 function 0 gives the percent in payload 0,
    /// 0 meaning unknown.</para>
    /// </summary>
    internal static class HidppUnitProtocol
    {
        public const ushort DeviceTypeAndName = 0x0005;
        public const ushort ColorLedEffects = 0x8070;
        public const ushort RgbEffects = 0x8071;
        public const ushort PerKeyLighting = 0x8080;
        public const ushort PerKeyLightingV2 = 0x8081;
        public const ushort UnifiedBattery = 0x1004;
        public const ushort BatteryStatus = 0x1000;

        public const ushort StaticEffectId = 0x0001;

        public const byte FunctionGetZoneInfo = 1;
        public const byte FunctionGetZoneEffectInfo = 2;
        public const byte FunctionSetZoneEffect = 3;
        public const byte FunctionSetSwControl = 8;

        /// <summary>0x8070 SetZoneEffect with the static effect: the zone, the
        /// static effect's index, the color, then 0x02, OpenRGB's "fixed color
        /// marker", or 0x00 for black, which OpenRGB sends as a pass-through
        /// (LogitechHIDPP20Controller.cpp:5647-5663). The persist byte stays
        /// 0, as every live writer sends it, so nothing is saved to the
        /// device's memory.</summary>
        public static byte[] SetStaticColor(byte deviceIndex, byte featureIndex, HidppZone zone, byte r, byte g, byte b)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionSetZoneEffect, new byte[]
            {
                zone.Zone, zone.StaticEffectIndex, r, g, b,
                (byte)((r | g | b) != 0 ? 0x02 : 0x00),
            });

        /// <summary>0x8070 SetSWControl: [01 01] takes the lighting from the
        /// device's own effect engine, [00 00] hands it back (OpenRGB
        /// LogitechHIDPP20Controller.cpp:3935-3946 and 4058-4078).</summary>
        public static byte[] SetSwControl(byte deviceIndex, byte featureIndex, bool claim)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionSetSwControl,
                claim ? new byte[] { 0x01, 0x01 } : new byte[] { 0x00, 0x00 });

        /// <summary>The percent a battery answer gives, or -1 when it gives
        /// none, by Solaar's decoders (hidpp20.py:2191-2265): 0x1000 reads a
        /// zero percent as unknown, and 0x1004 puts its level byte in place
        /// of one, 8 full at 90, 4 good at 50, 2 low at 20, 1 critical at 5
        /// and any other value empty at 0 (common.py:652-657). A percent over
        /// 100 reads as 100.</summary>
        public static int BatteryPercent(ushort featureId, HidppReply reply)
        {
            int percent = reply.Param(0);
            if (percent > 0) return Math.Min(percent, 100);
            if (featureId != UnifiedBattery) return -1;
            return reply.Param(1) switch
            {
                8 => 90,
                4 => 50,
                2 => 20,
                1 => 5,
                _ => 0,
            };
        }

        public static byte BatteryFunction(ushort featureId) => featureId == UnifiedBattery ? (byte)1 : (byte)0;
    }

    /// <summary>How a Logitech receiver keeps its pairing table.</summary>
    internal enum HidppReceiverKind
    {
        /// <summary>Not a receiver this code reads, or not a receiver.</summary>
        None,
        /// <summary>Unifying and LIGHTSPEED: sub-register 0x20 + slot - 1.</summary>
        Unifying,
        /// <summary>Bolt: sub-register 0x50 + slot.</summary>
        Bolt,
    }

    /// <summary>
    /// The receiver's own pairing table (#494), which tells a slot with a
    /// device asleep from a slot with no device. Both look the same on the
    /// long collection: a request to either goes unanswered there, since a
    /// receiver answers it with a HID++ 1.0 error, a short report. HID++ 1.0
    /// long register 0x2B5 (request 0x83B5, Solaar hidpp10.py:56-60) holds
    /// one pairing record per slot, and the receiver answers it itself,
    /// asleep device or not (OpenRGB LogitechProtocolCommon.cpp:155-160 for
    /// the same register's name record). Solaar reads a slot's record at
    /// 0x20 + slot - 1 on a Unifying or LIGHTSPEED receiver
    /// (receiver.py:266-276) and 0x50 + slot on a Bolt one
    /// (receiver.py:502-503), and takes an error as "no device there".
    ///
    /// <para>The request goes out short on the receiver's short collection
    /// (usage page 0xFF00, usage 1). The record comes back long, on the long
    /// collection the worker already reads, echoing 0x83, 0xB5 and the
    /// sub-register (Solaar base.py:769-774). An error comes back short on
    /// the short collection: 0x8F, then 0x83 0xB5, then the code
    /// (base.py:730-735).</para>
    /// </summary>
    internal static class HidppReceiverProtocol
    {
        public const byte ShortReportId = 0x10;
        public const int ShortReportLength = 7;
        public const ushort ShortUsage = 0x0001;
        public const byte GetLongRegister = 0x83;
        public const byte ErrorMessage = 0x8F;
        public const byte ReceiverInfo = 0xB5;

        /// <summary>Solaar waits 0.9 seconds for a receiver's answer and
        /// twice that for a long register read (base.py:129 and 698-701).</summary>
        public const int RegisterTimeoutMs = 1800;

        /// <summary>The receivers whose pairing table this reads, by product
        /// ID from Solaar's receiver list (base_usb.py:149-178): Bolt, and
        /// the Unifying and LIGHTSPEED receivers. Nano and older receivers
        /// stay out, since Solaar falls back to an undocumented register for
        /// some of them (receiver.py:277-282).</summary>
        public static HidppReceiverKind KindOf(ushort productId) => productId switch
        {
            0xC548 => HidppReceiverKind.Bolt,
            0xC52B or 0xC532 or 0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D
                => HidppReceiverKind.Unifying,
            _ => HidppReceiverKind.None,
        };

        public static byte PairingSub(HidppReceiverKind kind, byte slot)
            => kind == HidppReceiverKind.Bolt ? (byte)(0x50 + slot) : (byte)(0x20 + slot - 1);

        /// <summary>The short request for one long register record.</summary>
        public static byte[] ReadRegister(byte register, byte sub)
            => new byte[] { ShortReportId, HidppHapticProtocol.DirectDeviceIndex, GetLongRegister, register, sub, 0x00, 0x00 };

        /// <summary>Whether a report answers that request: the long record,
        /// or a HID++ 1.0 error for it.</summary>
        public static HidppReplyKind Match(ReadOnlySpan<byte> report, byte register, byte sub, out byte errorCode)
        {
            errorCode = 0;
            if (report.Length >= 5 && report[0] == HidppHapticProtocol.LongReportId
                && report[1] == HidppHapticProtocol.DirectDeviceIndex
                && report[2] == GetLongRegister && report[3] == register && report[4] == sub)
                return HidppReplyKind.Answer;
            if (report.Length >= 6 && report[0] == ShortReportId
                && report[1] == HidppHapticProtocol.DirectDeviceIndex
                && report[2] == ErrorMessage && report[3] == GetLongRegister && report[4] == register)
            {
                errorCode = report[5];
                return HidppReplyKind.Error;
            }
            return HidppReplyKind.None;
        }

        /// <summary>A path's short collection: the same device and the same
        /// interface, by OpenRGB's path key (LogitechControllerDetect.cpp:588-625),
        /// which drops the collection number and the instance suffix Windows
        /// gives each collection.</summary>
        public static VendorHidCollection ShortSibling(VendorHidCollection longCollection,
            IEnumerable<VendorHidCollection> all)
        {
            if (longCollection == null || all == null) return null;
            string key = PathKey(longCollection.Path);
            foreach (var c in all)
            {
                if (c == null || c.VendorId != longCollection.VendorId || c.ProductId != longCollection.ProductId) continue;
                if (c.UsagePage != HidppHapticProtocol.UsagePage || c.Usage != ShortUsage) continue;
                if (string.Equals(PathKey(c.Path), key, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        /// <summary>OpenRGB's LogitechDevicePathKey: removes "&amp;ColNN" and,
        /// when it was there, the last "&amp;..." segment before the GUID, the
        /// collection's own instance number.</summary>
        public static string PathKey(string path)
        {
            string key = path ?? string.Empty;
            int col = key.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            if (col < 0) return key;
            int end = col + 4;
            while (end < key.Length && Uri.IsHexDigit(key[end])) end++;
            key = key.Remove(col, end - col);
            int guid = key.LastIndexOf('#');
            int last = guid < 0 ? -1 : key.LastIndexOf('&', guid);
            if (last >= 0) key = key.Remove(last, guid - last);
            return key;
        }
    }

    /// <summary>
    /// Finds the units on one HID++ collection (#494): the haptic probe the
    /// first build shipped (<see cref="HidppHapticProbe"/>'s slot walk and
    /// backoff, unchanged), widened to every output a unit can have. A slot
    /// is alive once Root answers for 0x0005. Every other feature is asked
    /// only of a live slot, and a missing answer to an extra leaves that
    /// output off rather than failing the unit.
    /// </summary>
    internal static class HidppUnitProbe
    {
        /// <summary>Unanswered pairing reads in a row after which a receiver
        /// is not asked for its table again.</summary>
        public const int PairingGiveUp = 3;

        public static List<HidppUnit> Probe(IHidppChannel channel, HidppPathState state, Guid containerId, long now)
        {
            var found = new List<HidppUnit>();
            int timeout = HidppHapticProtocol.TimeoutMs(channel.Bluetooth);

            // A receiver known by its product ID is never a device itself, so
            // 0xFF is not asked, and its pairing table says which slots are
            // worth asking.
            if (state.ReceiverKind != HidppReceiverKind.None)
            {
                state.Receiver = true;
                ReadPairing(channel, state);
            }

            if (!state.Receiver)
            {
                AskSlot(channel, state, HidppHapticProtocol.DirectDeviceIndex, containerId, now, timeout, found, out bool answered);
                if (answered) state.Direct = true;
            }
            // A Bluetooth path is the device itself: it has no slots.
            if (state.Direct || state.Dead || channel.Bluetooth) return found;

            for (byte index = HidppHapticProtocol.FirstReceiverIndex; index <= HidppHapticProtocol.LastReceiverIndex; index++)
            {
                AskSlot(channel, state, index, containerId, now, timeout, found, out bool answered);
                if (answered) state.Receiver = true;
                if (state.Dead) break;
            }
            return found;
        }

        /// <summary>True while some slot of the path keeps the scan fast
        /// (<see cref="HidppPathState.Waiting"/>).</summary>
        public static bool Pending(HidppPathState state, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = state.Direct || bluetooth ? 0 : HidppHapticProtocol.LastReceiverIndex;
            for (int slot = first; slot <= last; slot++)
                if (state.Waiting(slot)) return true;
            return false;
        }

        /// <summary>True while some slot of the path is still to be asked
        /// (<see cref="HidppPathState.Open"/>), so a device there may yet
        /// answer, a mouse asleep since launch among them, and a row in the
        /// same container keeps the outputs it had. A slot the receiver
        /// reports empty, or one whose device answered, holds nothing.</summary>
        public static bool HasOpenSlot(HidppPathState state, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = state.Direct || bluetooth ? 0 : HidppHapticProtocol.LastReceiverIndex;
            for (int slot = first; slot <= last; slot++)
                if (state.Open(slot)) return true;
            return false;
        }

        /// <summary>A path whose collection could not be opened answers
        /// nothing: each slot due to be asked takes a miss, so the path leaves
        /// the fast cadence after the same first walk as a silent slot.</summary>
        internal static void MissOpenSlots(HidppPathState state, long now, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = state.Direct || bluetooth ? 0 : HidppHapticProtocol.LastReceiverIndex;
            for (int slot = first; slot <= last; slot++)
                if (state.Open(slot) && now >= state.RetryAt[slot]) Backoff(state, slot, now);
        }

        /// <summary>Reads the pairing record of every slot no device has
        /// answered from (<see cref="HidppReceiverProtocol"/>). A record means
        /// a device is paired there, asleep or not. An error means none is,
        /// but only when the same read errs twice, since another program
        /// reading the register at the same moment could hand this read its
        /// error. A read that goes unanswered leaves the table unknown and
        /// stops the pass, since the rest would wait out the same timeout.
        /// After <see cref="PairingGiveUp"/> such passes the table is not read
        /// again, and every slot goes back to the walk.</summary>
        internal static void ReadPairing(IHidppChannel channel, HidppPathState state,
            int timeout = HidppReceiverProtocol.RegisterTimeoutMs)
        {
            if (state.PairingMisses >= PairingGiveUp) return;
            for (byte slot = HidppHapticProtocol.FirstReceiverIndex; slot <= HidppHapticProtocol.LastReceiverIndex; slot++)
            {
                if (state.Settled[slot]) continue;
                byte sub = HidppReceiverProtocol.PairingSub(state.ReceiverKind, slot);
                var reply = channel.ReadReceiverRegister(HidppReceiverProtocol.ReceiverInfo, sub, timeout);
                if (reply.Kind == HidppReplyKind.Error)
                    reply = channel.ReadReceiverRegister(HidppReceiverProtocol.ReceiverInfo, sub, timeout);
                switch (reply.Kind)
                {
                    case HidppReplyKind.Answer:
                        state.PairingMisses = 0;
                        state.Unpaired[slot] = false;
                        break;
                    case HidppReplyKind.Error:
                        state.PairingMisses = 0;
                        state.Unpaired[slot] = true;
                        state.Held[slot] = false;
                        break;
                    default:
                        if (++state.PairingMisses >= PairingGiveUp) Array.Clear(state.Unpaired);
                        return;
                }
            }
        }

        private static void AskSlot(IHidppChannel channel, HidppPathState state, byte deviceIndex, Guid containerId,
            long now, int timeout, List<HidppUnit> found, out bool answered)
        {
            answered = false;
            int slot = HidppPathState.Slot(deviceIndex);
            if (!state.Open(slot) || now < state.RetryAt[slot]) return;

            var nameFeature = channel.Request(deviceIndex, 0x00, 0, FeatureId(HidppUnitProtocol.DeviceTypeAndName), timeout);
            if (nameFeature.Hidpp10)
            {
                // The receiver answered for the slot in HID++ 1.0. Its device
                // speaks only HID++ 1.0 and has none of these features, so
                // the slot settles. Any other code says it cannot be reached
                // now, asleep or away (Solaar base.py:841-854), so it is asked
                // again after the backoff. A receiver's error for 0xFF never
                // makes the path a device.
                answered = deviceIndex != HidppHapticProtocol.DirectDeviceIndex;
                if (nameFeature.ErrorCode == HidppHapticProtocol.Hidpp10InvalidSubId)
                    state.Settled[slot] = true;
                else
                    Backoff(state, slot, now);
                return;
            }
            switch (nameFeature.Kind)
            {
                case HidppReplyKind.Answer:
                    answered = true;
                    var unit = Describe(channel, deviceIndex, nameFeature.Param(0), containerId, timeout, out bool incomplete);
                    if (unit == null)
                    {
                        // A write failed halfway: the collection went away.
                        state.Dead = true;
                        return;
                    }
                    if (incomplete)
                    {
                        // The device stopped answering partway, going to
                        // sleep or out of range. Settling now would keep it
                        // without an output for as long as the path lasts, so
                        // the slot backs off and is asked again in full.
                        Backoff(state, slot, now);
                        return;
                    }
                    state.Settled[slot] = true;
                    // Battery alone feeds the Battery lighting mode, which
                    // needs a lit device, so a unit with neither output is
                    // not kept.
                    if (unit.HasHaptics || unit.HasRgb)
                    {
                        found.Add(unit);
                        state.Held[slot] = true;
                    }
                    else
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ '{unit.Name}' index=0x{unit.DeviceIndex:X2} has no haptics or RGB");
                    return;
                case HidppReplyKind.Error:
                    answered = true;
                    // Busy after the channel's own retry: asked again after
                    // the backoff. Any other refusal of Root.getFeature, which
                    // reports a missing feature as index 0 and never as an
                    // error, leaves nothing to offer, and asking again
                    // changes nothing.
                    if (nameFeature.ErrorCode == HidppHapticProtocol.ErrorBusy)
                    {
                        Backoff(state, slot, now);
                        return;
                    }
                    state.Settled[slot] = true;
                    return;
                case HidppReplyKind.WriteFailed:
                    state.Dead = true;
                    return;
                default:
                    Backoff(state, slot, now);
                    return;
            }
        }

        /// <summary>The first build's backoff: 5 seconds, then 10, then 15
        /// at most.</summary>
        private static void Backoff(HidppPathState state, int slot, long now)
        {
            int misses = state.Misses[slot]++;
            long wait = Math.Min((long)HidppHapticProbe.FirstRetryMs << Math.Min(misses, 4), HidppHapticProbe.MaxRetryMs);
            state.RetryAt[slot] = now + wait;
        }

        /// <summary>Every output of a live unit. Null only when a write
        /// failed, which means the collection is gone. <paramref name="incomplete"/>
        /// is set when a request that decides an output went unanswered: the
        /// device type, the feature lookups, the haptic capabilities and the
        /// zone walk. The unit then comes back as far as it got, and the caller
        /// drops it and asks again in full, so nothing more is asked of a
        /// device that stopped answering, where each request would wait out
        /// its own timeout on the one worker thread. The type decides which
        /// rows a receiver's unit lights and rumbles, so a missed answer is
        /// asked again, while a device that refuses the request keeps an
        /// unknown type. The name and the haptic configuration are extras a
        /// missing answer only leaves unknown.</summary>
        internal static HidppUnit Describe(IHidppChannel channel, byte deviceIndex, byte nameIndex,
            Guid containerId, int timeout, out bool incomplete)
        {
            incomplete = false;
            string name = null;
            int type = -1;
            if (nameIndex != 0)
            {
                var typeReply = channel.Request(deviceIndex, nameIndex, 2, Array.Empty<byte>(), timeout);
                if (typeReply.Kind == HidppReplyKind.WriteFailed) return null;
                if (Missed(typeReply))
                {
                    incomplete = true;
                    return new HidppUnit { ChannelPath = channel.Path, ContainerId = containerId, DeviceIndex = deviceIndex };
                }
                if (typeReply.Kind == HidppReplyKind.Answer) type = typeReply.Param(0);
                name = HidppHapticProbe.ReadName(channel, deviceIndex, nameIndex, timeout);
            }
            var unit = new HidppUnit
            {
                ChannelPath = channel.Path,
                ContainerId = containerId,
                DeviceIndex = deviceIndex,
                Name = name,
                DeviceType = type,
            };

            byte hapticIndex = Feature(channel, deviceIndex, HidppHapticProtocol.HapticFeature, timeout, out var reply);
            if (reply.Kind == HidppReplyKind.WriteFailed) return null;
            if (Missed(reply)) { incomplete = true; return unit; }
            if (hapticIndex != 0)
            {
                var caps = channel.Request(deviceIndex, hapticIndex,
                    HidppHapticProtocol.FunctionGetCapabilities, Array.Empty<byte>(), timeout);
                if (caps.Kind == HidppReplyKind.WriteFailed) return null;
                // Without the mask nothing can be chosen, so anything but the
                // mask, a timeout or a device still busy after the channel's
                // retry, is a miss to ask again, the first build's rule.
                if (caps.Kind != HidppReplyKind.Answer) { incomplete = true; return unit; }
                var config = channel.Request(deviceIndex, hapticIndex,
                    HidppHapticProtocol.FunctionGetConfiguration, Array.Empty<byte>(), timeout);
                if (config.Kind == HidppReplyKind.WriteFailed) return null;
                unit = unit with
                {
                    HapticIndex = hapticIndex,
                    WaveformMask = HidppHapticProtocol.WaveformMask(caps),
                    FeedbackEnabled = config.Kind != HidppReplyKind.Answer || HidppHapticProtocol.FeedbackEnabled(config),
                };
            }

            foreach (ushort id in new[]
            {
                HidppUnitProtocol.RgbEffects, HidppUnitProtocol.PerKeyLightingV2,
                HidppUnitProtocol.PerKeyLighting, HidppUnitProtocol.ColorLedEffects,
            })
            {
                byte index = Feature(channel, deviceIndex, id, timeout, out reply);
                if (reply.Kind == HidppReplyKind.WriteFailed) return null;
                if (Missed(reply)) { incomplete = true; return unit; }
                if (index == 0) continue;
                unit = unit with { RgbFeatureId = id, RgbIndex = index };
                break;
            }
            if (unit.RgbFeatureId == HidppUnitProtocol.ColorLedEffects)
            {
                var zones = StaticZones(channel, deviceIndex, unit.RgbIndex, timeout, out bool zonesMissed);
                if (zones == null) return null;
                if (zonesMissed) { incomplete = true; return unit; }
                unit = unit with { Zones = zones };
            }

            foreach (ushort id in new[] { HidppUnitProtocol.UnifiedBattery, HidppUnitProtocol.BatteryStatus })
            {
                byte index = Feature(channel, deviceIndex, id, timeout, out reply);
                if (reply.Kind == HidppReplyKind.WriteFailed) return null;
                if (Missed(reply)) { incomplete = true; return unit; }
                if (index == 0) continue;
                unit = unit with { BatteryFeatureId = id, BatteryIndex = index };
                break;
            }
            return unit;
        }

        /// <summary>The zones of a 0x8070 device that list the static effect,
        /// each with that effect's index. GetZoneInfo carries Solaar's
        /// trailing FF 00 (hidpp20.py:1354-1357). Null when a write failed.
        /// <paramref name="missed"/> is set at the first request that went
        /// unanswered or stayed busy (<see cref="Missed"/>), where the walk
        /// stops, so the caller asks again rather than keep a device with
        /// zones missing.</summary>
        internal static HidppZone[] StaticZones(IHidppChannel channel, byte deviceIndex, byte featureIndex, int timeout,
            out bool missed)
        {
            missed = false;
            var info = channel.Request(deviceIndex, featureIndex, 0, Array.Empty<byte>(), timeout);
            if (info.Kind == HidppReplyKind.WriteFailed) return null;
            missed = Missed(info);
            if (info.Kind != HidppReplyKind.Answer) return Array.Empty<HidppZone>();
            int zoneCount = Math.Min((int)info.Param(0), 16);
            var zones = new List<HidppZone>(zoneCount);
            for (int z = 0; z < zoneCount; z++)
            {
                var zoneInfo = channel.Request(deviceIndex, featureIndex, HidppUnitProtocol.FunctionGetZoneInfo,
                    new byte[] { (byte)z, 0xFF, 0x00 }, timeout);
                if (zoneInfo.Kind == HidppReplyKind.WriteFailed) return null;
                if (Missed(zoneInfo)) { missed = true; break; }
                if (zoneInfo.Kind != HidppReplyKind.Answer) continue;
                int effectCount = Math.Min((int)zoneInfo.Param(3), 32);
                for (int e = 0; e < effectCount && !missed; e++)
                {
                    var effect = channel.Request(deviceIndex, featureIndex, HidppUnitProtocol.FunctionGetZoneEffectInfo,
                        new byte[] { (byte)z, (byte)e }, timeout);
                    if (effect.Kind == HidppReplyKind.WriteFailed) return null;
                    if (Missed(effect)) { missed = true; break; }
                    if (effect.Kind != HidppReplyKind.Answer) continue;
                    ushort id = (ushort)((effect.Param(2) << 8) | effect.Param(3));
                    if (id != HidppUnitProtocol.StaticEffectId) continue;
                    zones.Add(new HidppZone((byte)z, (byte)e));
                    break;
                }
                if (missed) break;
            }
            return zones.ToArray();
        }

        /// <summary>A request to ask again: unanswered, still busy after the
        /// channel's own retry, which the device clears on its own, or
        /// refused by the receiver in HID++ 1.0 because the device dropped
        /// off partway.</summary>
        internal static bool Missed(HidppReply reply)
            => reply.Kind == HidppReplyKind.Timeout
               || reply.Hidpp10
               || (reply.Kind == HidppReplyKind.Error && reply.ErrorCode == HidppHapticProtocol.ErrorBusy);

        /// <summary>The device's index for a feature, 0 when it lacks it or
        /// did not answer. <paramref name="reply"/> says which.</summary>
        private static byte Feature(IHidppChannel channel, byte deviceIndex, ushort featureId, int timeout,
            out HidppReply reply)
        {
            reply = channel.Request(deviceIndex, 0x00, 0, FeatureId(featureId), timeout);
            return reply.Kind == HidppReplyKind.Answer ? reply.Param(0) : (byte)0;
        }

        /// <summary>A unit's charge, or -1 when it did not say.</summary>
        public static int ReadBattery(IHidppChannel channel, HidppUnit unit)
        {
            if (unit.BatteryFeatureId == 0) return -1;
            var reply = channel.Request(unit.DeviceIndex, unit.BatteryIndex,
                HidppUnitProtocol.BatteryFunction(unit.BatteryFeatureId), Array.Empty<byte>(),
                HidppHapticProtocol.TimeoutMs(channel.Bluetooth));
            return reply.Kind == HidppReplyKind.Answer
                ? HidppUnitProtocol.BatteryPercent(unit.BatteryFeatureId, reply)
                : -1;
        }

        private static byte[] FeatureId(ushort featureId) => new[] { (byte)(featureId >> 8), (byte)featureId };
    }
}
