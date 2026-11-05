using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One zone of a feature 0x8070 device, or one cluster of a
    /// feature 0x8071 device, and the index of its static effect, the effect
    /// whose ID is 0x0001. The index is the effect's place in that zone's or
    /// cluster's own list, which differs between zones of one device.</summary>
    internal readonly record struct HidppZone(byte Zone, byte StaticEffectIndex);

    /// <summary>One key type of a feature 0x8080 keyboard (#557): its bit and
    /// the IDs of its keys, as the keyboard lists them
    /// (<see cref="HidppUnitProbe.KeyTypes"/>).</summary>
    internal sealed record HidppKeyType(ushort Type, byte[] Keys)
    {
        /// <summary>Whether two hold the same type and keys.</summary>
        public bool SameAs(HidppKeyType other)
            => other != null && Type == other.Type && Keys.AsSpan().SequenceEqual(other.Keys);
    }

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

        /// <summary>What the device is painted through: the zones of a 0x8070
        /// device or the clusters of a 0x8071 one, each with its static
        /// effect. Empty for a device with no effect feature.</summary>
        public HidppZone[] Zones { get; init; } = Array.Empty<HidppZone>();

        /// <summary>Where each of <see cref="Zones"/> is on the device, in
        /// the same order, as the zone or cluster reports it: 1 primary, 2
        /// logo, 3 left side, 4 right side (libratbag hidpp20.h
        /// hidpp20_color_led_location, OpenRGB zone_location_name), for a
        /// device painted zone by zone (#557).</summary>
        public ushort[] ZoneLocations { get; init; } = Array.Empty<ushort>();

        /// <summary>The effect feature the unit is painted through, 0x8070
        /// or 0x8071, or 0 for none. Kept apart from
        /// <see cref="RgbFeatureId"/>, which is the first RGB feature the
        /// device lists: a board with both an effect feature and a per-key
        /// one needs each by its own index. The probe fills it for every
        /// unit it finds. A unit built with only <see cref="RgbFeatureId"/>
        /// is lit by <see cref="DirectRgb"/> alone.</summary>
        public ushort EffectFeatureId { get; init; }
        public byte EffectIndex { get; init; }

        /// <summary>The per-key feature, 0x8080 or 0x8081, or 0 for none.</summary>
        public ushort PerKeyFeatureId { get; init; }
        public byte PerKeyIndex { get; init; }

        /// <summary>The lowest and the highest zone a 0x8081 device reports,
        /// the two ends of the one range frame that paints them all. Zero
        /// for every other per-key feature, and for a 0x8081 device with no
        /// 0x8071 claim to paint under.</summary>
        public byte PerKeyMin { get; init; }
        public byte PerKeyMax { get; init; }

        /// <summary>Every zone a 0x8081 keyboard reports present, lowest
        /// first, from 1 to 254, for its light map (#557,
        /// <see cref="HidppKeyMap"/>). Empty for every other unit.</summary>
        public byte[] PerKeyZones { get; init; } = Array.Empty<byte>();

        /// <summary>The layout code feature 0x4540 gives for a keyboard
        /// painted key by key, which says its region
        /// (<see cref="HidppKeyMap.KeysOf"/>), or 0 when the keyboard does
        /// not say.</summary>
        public int KeyboardLayout { get; init; }

        /// <summary>The model IDs feature 0x0003 lists for a keyboard painted
        /// key by key, one per transport it has, in the order of its
        /// transport bits. Empty when unread.</summary>
        public ushort[] ModelIds { get; init; } = Array.Empty<ushort>();

        /// <summary>The key types of a 0x8080 keyboard whose very long
        /// collection is open, with their keys (#557). Empty for every other
        /// unit.</summary>
        public HidppKeyType[] KeyTypes { get; init; } = Array.Empty<HidppKeyType>();

        /// <summary>The place of the Host streaming effect (ID 0x0013) in a
        /// 0x8071 device's list of multi-cluster effects, or -1 for a
        /// device that lists none. Logitech's own program chooses that
        /// effect on cluster 0xFF before its first per-key frame
        /// (<see cref="HidppUnitProtocol.RgbHostStreaming"/>).</summary>
        public int HostStreamingIndex { get; init; } = -1;

        /// <summary>The feature that switches the device between its
        /// on-board memory mode and host mode, 0x8100 or 0x8101, or 0 for
        /// none.</summary>
        public ushort ModeFeatureId { get; init; }
        public byte ModeIndex { get; init; }

        /// <summary>How firmly this unit can be held, the top of what its
        /// rows' Lighting tabs offer: 0 colors alone, 1 its lighting power
        /// too, 2 its on-board memory mode off as well.</summary>
        public int MaxClaimLevel { get; init; }

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

        /// <summary>The unit can be painted on its own: the zone-only 0x8070
        /// class above, or an effect feature with at least one zone or
        /// cluster to paint. The linker gives a HID++ lighting path to the
        /// units this is true for.</summary>
        public bool HasLightPlan => DirectRgb || (EffectFeatureId != 0 && Zones.Length > 0);

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
    ///
    /// <para>0x8071 RGB_EFFECTS (Logitech's "x8071 - RGB Effects", version
    /// 4, the function list on p.1-2, with the same numbers in OpenRGB
    /// LogitechHIDPP20Controller.h:77-85): function 0 getInfo, 1
    /// setRgbClusterEffect, 5 manageSwControl, 8 manageRgbPowerMode. The
    /// numbers are not 0x8070's. There function 8 is SetSWControl, and here
    /// it sets the power mode, so a 0x8070 claim sent to a 0x8071 index
    /// claims nothing and its release releases nothing. Never sent:
    /// function 3, which writes the device's saved configuration, function
    /// 7 as a write, which changes its power timers, function 9, which puts
    /// it into deep sleep (p.33), and any persistence value with bit 1 set,
    /// which writes its memory (p.11).</para>
    ///
    /// <para>0x8080 PER_KEY_LIGHTING (keyleds feature_leds.c:28-35, OpenRGB
    /// LogitechHIDPP20Controller.h:106-111, with no document from Logitech):
    /// function 4 fills one key type with a color and function 5 shows what
    /// was filled. 0x8081 PER_KEY_LIGHTING_V2 (Logitech's "x8081 - Per-Key
    /// Lighting", version 0, p.4): function 0 getInfo, 5 setRangeRgbZones,
    /// 7 frameEnd.</para>
    ///
    /// <para>The on-board memory mode (libratbag hidpp20.c:1782-1800, OpenRGB
    /// LogitechHIDPP20Controller.cpp:3978-3998): feature 0x8100 reads it
    /// with function 2 and sets it with function 1, 1 on-board and 2 host.
    /// Feature 0x8101 does both with function 6, 3 on-board and 5 host.</para>
    /// </summary>
    internal static class HidppUnitProtocol
    {
        public const ushort DeviceInformation = 0x0003;
        public const ushort DeviceTypeAndName = 0x0005;
        public const ushort ColorLedEffects = 0x8070;
        public const ushort RgbEffects = 0x8071;
        public const ushort PerKeyLighting = 0x8080;
        public const ushort PerKeyLightingV2 = 0x8081;
        public const ushort OnboardProfiles = 0x8100;
        public const ushort ProfileManagement = 0x8101;

        /// <summary>KEYBOARD_LAYOUT_2: function 0 answers the layout code in
        /// its first byte (Solaar hidpp20.py get_keyboard_layout, OpenRGB
        /// LogitechHIDPP20Controller.cpp DiscoverKeyboardLayout).</summary>
        public const ushort KeyboardLayout = 0x4540;
        public const ushort UnifiedBattery = 0x1004;
        public const ushort BatteryStatus = 0x1000;

        public const ushort StaticEffectId = 0x0001;

        /// <summary>Effect 19 of the 0x8071 list, "Host streaming", the
        /// effect that shows the per-key buffer (Logitech's x8071 document,
        /// p.16).</summary>
        public const ushort HostStreamingEffectId = 0x0013;

        public const byte FunctionGetZoneInfo = 1;
        public const byte FunctionGetZoneEffectInfo = 2;
        public const byte FunctionSetZoneEffect = 3;
        public const byte FunctionSetSwControl = 8;

        public const byte FunctionRgbGetInfo = 0;
        public const byte FunctionRgbSetClusterEffect = 1;
        public const byte FunctionRgbSwControl = 5;
        public const byte FunctionRgbPowerMode = 8;

        /// <summary>0x8080's functions (keyleds feature_leds.c:28-35):
        /// the key type bits, a type's key count and top channel values, a
        /// page of a type's keys, key colors, a type's fill, and the commit.</summary>
        public const byte FunctionPerKeyGetInfo = 0;
        public const byte FunctionPerKeyGetTypeInfo = 1;
        public const byte FunctionPerKeyGetKeys = 2;
        public const byte FunctionPerKeySetKeys = 3;
        public const byte FunctionPerKeyFill = 4;
        public const byte FunctionPerKeyCommit = 5;

        /// <summary>The keys one very long frame of 0x8080 lists or colors:
        /// 60 parameter bytes, 4 of them the type and the count, then 4 per
        /// key (OpenRGB HIDPP20_8080_KEYS_PER_FRAME, Logitech Gaming
        /// Software's 12 FF 0C 3A 00 01 00 0E frames).</summary>
        public const int PerKeyKeysPerFrame = 14;

        /// <summary>The single-bit key types OpenRGB walks: keys, media,
        /// G-keys, buttons, the logo and the indicators. A set bit outside
        /// them is never asked about (LogitechHIDPP20Controller.cpp
        /// DiscoverPerKey8080).</summary>
        public static readonly ushort[] KnownKeyTypes =
        {
            KeyTypeKeys, KeyTypeMedia, KeyTypeGKeys, KeyTypeButtons, KeyTypeLogo, KeyTypeIndicators,
        };

        public const byte FunctionPerKeyV2GetInfo = 0;
        public const byte FunctionPerKeyV2SetIndividual = 1;
        public const byte FunctionPerKeyV2SetRange = 5;
        public const byte FunctionPerKeyV2SetSingleValue = 6;
        public const byte FunctionPerKeyV2FrameEnd = 7;

        /// <summary>The most zones one setIndividualRgbZones frame colors,
        /// the most ranges one setRangeRgbZones frame holds, and the most
        /// zones one setRgbZonesSingleValue frame colors (Logitech's x8081
        /// document, p.6-7, p.12-13 and p.14-15).</summary>
        public const int PerKeyIndividualMax = 4, PerKeyRangesMax = 3, PerKeySingleMax = 13;

        public const byte FunctionOnboardSetMode = 1;
        public const byte FunctionOnboardGetMode = 2;
        public const byte FunctionProfileMode = 6;

        /// <summary>0x8071 getInfo's "all clusters" and "all effects", and
        /// the cluster a multi-cluster effect is set on.</summary>
        public const byte AllClusters = 0xFF;

        /// <summary>0x8071 manageSwControl's flags (Logitech's x8071
        /// document, p.26-27): the software controls every cluster, and the
        /// software controls the power modes, which stops the device from
        /// dimming and switching its lights off by itself.</summary>
        public const byte SwControlClusters = 0x01;
        public const byte SwControlPower = 0x02;

        /// <summary>The 0x8080 key types, single bits (keyleds.h:153-157,
        /// OpenRGB LogitechHIDPP20Controller.h:174-181).</summary>
        public const ushort KeyTypeKeys = 0x0001;
        public const ushort KeyTypeMedia = 0x0002;
        public const ushort KeyTypeGKeys = 0x0004;
        public const ushort KeyTypeButtons = 0x0008;
        public const ushort KeyTypeLogo = 0x0010;
        public const ushort KeyTypeIndicators = 0x0040;

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

        /// <summary>0x8071 getInfo. One function answers three questions,
        /// told apart by its first two bytes (Logitech's x8071 document,
        /// p.2-3): [FF FF] the device, [cluster FF] one cluster, [cluster
        /// effect] one effect of a cluster, with cluster FF for the list of
        /// multi-cluster effects. The third byte, typeOfInfo, is 0. Logitech's
        /// own program asks all three this way (g810-led-resources
        /// dumps/g815/G815-Hotplug-to-start.pcapng: 11 FF 0F 0C FF FF 00,
        /// then 00 FF 00, then 00 00 00).</summary>
        public static byte[] RgbGetInfo(byte deviceIndex, byte featureIndex, byte cluster, byte effect)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionRgbGetInfo, new byte[] { cluster, effect, 0x00 });

        /// <summary>0x8071 manageSwControl as a write: [01, flags, events]
        /// (Logitech's x8071 document, p.26-28). [01 01 00] takes the
        /// clusters alone, [01 00 00] hands everything back, and Logitech's
        /// own program claims with [01 03 07] and settles on [01 03 05]
        /// (G815-Hotplug-to-start.pcapng: 11 FF 0F 5C 01 03 07, then
        /// 11 FF 0F 5C 01 03 05).</summary>
        public static byte[] RgbSwControl(byte deviceIndex, byte featureIndex, byte flags, byte events)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionRgbSwControl, new byte[] { 0x01, flags, events });

        /// <summary>0x8071 manageSwControl as a read: [00]. The answer
        /// carries [00, flags, events] as they stand (Logitech's x8071
        /// document, p.26-28). Logitech's own program reads it before it
        /// claims (G815-Hotplug-to-start.pcapng: 11 FF 0F 5C 00).</summary>
        public static byte[] RgbSwControlRead(byte deviceIndex, byte featureIndex)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionRgbSwControl, new byte[] { 0x00 });

        /// <summary>0x8071 setRgbClusterEffect with the Fixed effect
        /// (Logitech's x8071 document, p.11-12): the cluster, the effect's
        /// index in that cluster, the color, then 0x02, "no effect", which
        /// shows the color with no ramp. Byte 12 is 0x01: full power mode,
        /// and volatile memory alone. With 0x00 there a device acknowledges
        /// the frame and shows nothing ("verified on a G502 X PLUS and a
        /// G915", OpenGHub features.rs:1037-1039), and bit 1 would write the
        /// device's memory, which this never sets. The bytes are the ones
        /// Logitech's own program sends
        /// (dumps/g815/G815-FX-color-8D40FF.pcapng:
        /// 11 FF 0F 1C 01 01 8D 40 FF 02 00 00 00 00 00 00 01).
        ///
        /// <para>Black keeps the 0x02. OpenRGB sends 0x00 for black, as a
        /// pass-through to the per-key buffer
        /// (LogitechHIDPP20Controller.cpp:5647-5663), and Logitech's own
        /// program sends black to a G915 X as Fixed with 0x02 (OpenRGB issue
        /// 4461, all_off.pcapng: 11 FF 08 1D 00 01 00 00 00 02 ... 01). The
        /// capture is the vendor's own frame, taken with the keyboard's
        /// answers, so it is the one followed.</para></summary>
        public static byte[] RgbFixedColor(byte deviceIndex, byte featureIndex, HidppZone cluster, byte r, byte g, byte b)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionRgbSetClusterEffect, new byte[]
            {
                cluster.Zone, cluster.StaticEffectIndex, r, g, b, 0x02, 0, 0, 0, 0, 0, 0, 0x01,
            });

        /// <summary>0x8071 setRgbClusterEffect choosing Host streaming on
        /// every cluster: cluster FF, the effect's place in the multi-cluster
        /// list, no parameters, and byte 12 at 0x01 as for the Fixed effect.
        /// Logitech's own program sends it to a G915 X 30 ms before its
        /// first per-key frame (OpenRGB issue 4461, unplug_and_replug.pcapng:
        /// 11 FF 08 1D FF 01 00 00 00 00 00 00 00 00 00 00 01), and to a
        /// PRO X TKL RAPID, where rigdeck replays it on the keyboard
        /// (rigdeck lighting.py:27-31).</summary>
        public static byte[] RgbHostStreaming(byte deviceIndex, byte featureIndex, byte entry)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionRgbSetClusterEffect, new byte[]
            {
                AllClusters, entry, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01,
            });

        /// <summary>0x8071 manageRgbPowerMode as a write of Full RGB, [01 01]
        /// (Logitech's x8071 document, p.31-32), the one power mode this
        /// ever sets. The device refuses it unless the power flag of
        /// <see cref="RgbSwControl"/> is set (p.33). Logitech's own program
        /// sends it between its two claim frames
        /// (G815-Hotplug-to-start.pcapng: 11 FF 0F 8C 01 01).</summary>
        public static byte[] RgbPowerFull(byte deviceIndex, byte featureIndex)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionRgbPowerMode, new byte[] { 0x01, 0x01 });

        /// <summary>0x8080 fill: one key type, big-endian, and a color. The
        /// board keeps it in a buffer until <see cref="PerKeyCommit"/>
        /// (keyleds feature_leds.c:218-227). Logitech Gaming Software sends
        /// it for its fixed color (g810-led-resources
        /// dumps/g810-spectrum/set_fx_color_00ff00.pcapng:
        /// 11 FF 0C 4C 00 40 00 FF 00).</summary>
        public static byte[] PerKeyFill(byte deviceIndex, byte featureIndex, ushort keyType, byte r, byte g, byte b)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyFill, new byte[]
            {
                (byte)(keyType >> 8), (byte)keyType, r, g, b,
            });

        /// <summary>0x8080 setKeyColors on the very long report: the key
        /// type, the count, then each key's ID and color, up to 14 (keyleds
        /// feature_leds.c keyleds_set_leds, OpenRGB SetPerKey8080). The
        /// board holds them until <see cref="PerKeyCommit"/>. Logitech Gaming
        /// Software sends its key colors so (set_keys_colors.pcapng:
        /// 12 FF 0C 3A 00 01 00 0E 58 00 DC FF 47 00 DC FF ...).</summary>
        public static byte[] PerKeySetKeys(byte deviceIndex, byte featureIndex, ushort keyType, IReadOnlyList<(byte Id, int Rgb)> keys)
        {
            var p = new byte[4 + keys.Count * 4];
            p[0] = (byte)(keyType >> 8);
            p[1] = (byte)keyType;
            p[2] = (byte)(keys.Count >> 8);
            p[3] = (byte)keys.Count;
            for (int i = 0; i < keys.Count; i++)
            {
                var (id, rgb) = keys[i];
                p[4 + i * 4] = id;
                p[5 + i * 4] = (byte)(rgb >> 16);
                p[6 + i * 4] = (byte)(rgb >> 8);
                p[7 + i * 4] = (byte)rgb;
            }
            return HidppHapticProtocol.VeryLongFrame(deviceIndex, featureIndex, FunctionPerKeySetKeys, p);
        }

        /// <summary>0x8080 commit: shows what the fills staged (keyleds
        /// feature_leds.c:239-244, set_fx_color_00ff00.pcapng:
        /// 11 FF 0C 5C). A board answers busy to one sent within about 5 ms
        /// of the commit before, and Logitech Gaming Software sends it again
        /// (dumps/g810-spectrum/set_fx_color_cycle_fast_00ff00.pcapng:
        /// 11 FF FF 0C 5C 08, then the commit once more).</summary>
        public static byte[] PerKeyCommit(byte deviceIndex, byte featureIndex)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyCommit, Array.Empty<byte>());

        /// <summary>The red a 0x8080 media fill carries. Logitech Gaming
        /// Software never sends red 0 there with a color: it sends
        /// [01 FF 00] for 00 FF 00 (set_fx_color_00ff00.pcapng:
        /// 11 FF 0C 4C 00 02 01 FF 00). A G910's media lights have red as
        /// their one channel, with a top value of 1
        /// (dumps/g910-spark/G910_gkey_mapping_and_mkeys.pcapng, the key
        /// type's answer 00 05 01 00 00), so a color with no red would
        /// switch them off. Black stays black here, so that black is dark.</summary>
        public static byte MediaRed(byte r, byte g, byte b) => r == 0 && (g | b) != 0 ? (byte)0x01 : r;

        /// <summary>0x8081 getInfo for one page of zone presence: [00, page,
        /// 00]. The answer repeats the two bytes and then holds 14 bytes of
        /// bits, bit i for zone page * 112 + i (Logitech's x8081 document,
        /// p.4-6). A G915 X answers page 1 with
        /// 00 01 00 00 00 00 00 7A 00 00 F0 1F 00 00 04 00
        /// (unplug_and_replug.pcapng).</summary>
        public static byte[] PerKeyZonePage(byte deviceIndex, byte featureIndex, byte page)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyV2GetInfo, new byte[] { 0x00, page, 0x00 });

        /// <summary>0x8081 setRangeRgbZones with one range: the first zone,
        /// the last, and the color for every zone between (Logitech's x8081
        /// document, p.12-14). The zero after it ends the list. One range
        /// from the lowest zone the device has to the highest paints the
        /// whole device, since the firmware passes over the IDs it lacks:
        /// OpenRGB sends exactly that ("Gaps in zone IDs are silently
        /// ignored by firmware", LogitechHIDPP20Controller.cpp:4646-4680),
        /// and Solaar sends the first and last key of its map
        /// (settings_templates.py:4024-4030). Logitech's own program sends
        /// one range per run of zones, which costs a frame or two more
        /// (unplug_and_replug.pcapng: 11 FF 09 5D 01 6F 00 00 00 B4 BC ...).</summary>
        public static byte[] PerKeyRange(byte deviceIndex, byte featureIndex, byte first, byte last, byte r, byte g, byte b)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyV2SetRange, new byte[] { first, last, r, g, b });

        /// <summary>0x8081 setRangeRgbZones with up to three ranges, each
        /// [first, last, R, G, B], the first below the last (Logitech's
        /// x8081 document, p.12-14). The zero after the last ends the list.
        /// OpenRGB packs a frame's runs of three or more zones of one color
        /// this way, three to a frame (RGBController_LogitechHIDPP20.cpp,
        /// DeviceUpdateLEDs' fn5 batches).</summary>
        public static byte[] PerKeyRanges(byte deviceIndex, byte featureIndex, IReadOnlyList<(byte First, byte Last, int Rgb)> ranges)
        {
            var p = new byte[ranges.Count * 5];
            for (int i = 0; i < ranges.Count; i++)
            {
                var (first, last, rgb) = ranges[i];
                p[i * 5] = first;
                p[i * 5 + 1] = last;
                p[i * 5 + 2] = (byte)(rgb >> 16);
                p[i * 5 + 3] = (byte)(rgb >> 8);
                p[i * 5 + 4] = (byte)rgb;
            }
            return HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyV2SetRange, p);
        }

        /// <summary>0x8081 setIndividualRgbZones: up to four [zone, R, G, B]
        /// (Logitech's x8081 document, p.6-7). The zero after the last ends
        /// the list. OpenRGB sends a frame's lone colors this way
        /// (LogitechHIDPP20Controller.cpp SetPerKeyColors).</summary>
        public static byte[] PerKeyIndividual(byte deviceIndex, byte featureIndex, IReadOnlyList<(byte Zone, int Rgb)> zones)
        {
            var p = new byte[zones.Count * 4];
            for (int i = 0; i < zones.Count; i++)
            {
                var (zone, rgb) = zones[i];
                p[i * 4] = zone;
                p[i * 4 + 1] = (byte)(rgb >> 16);
                p[i * 4 + 2] = (byte)(rgb >> 8);
                p[i * 4 + 3] = (byte)rgb;
            }
            return HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyV2SetIndividual, p);
        }

        /// <summary>0x8081 setRgbZonesSingleValue: the color, then up to 13
        /// zones it goes to, not necessarily neighbors (Logitech's x8081
        /// document, p.14-15). The zero after the last ends the list.
        /// OpenRGB sends the scattered zones of one color this way
        /// (RGBController_LogitechHIDPP20.cpp, DeviceUpdateLEDs' fn6
        /// batches).</summary>
        public static byte[] PerKeySingleValue(byte deviceIndex, byte featureIndex, int rgb, IReadOnlyList<byte> zones)
        {
            var p = new byte[3 + zones.Count];
            p[0] = (byte)(rgb >> 16);
            p[1] = (byte)(rgb >> 8);
            p[2] = (byte)rgb;
            for (int i = 0; i < zones.Count; i++) p[3 + i] = zones[i];
            return HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyV2SetSingleValue, p);
        }

        /// <summary>0x8081 frameEnd: shows what the range staged. Every
        /// byte is zero: persistence 0, volatile memory, and the frame
        /// counts a live stream carries (Logitech's x8081 document,
        /// p.16-17, unplug_and_replug.pcapng: 11 FF 09 7D 00 ...).</summary>
        public static byte[] PerKeyFrameEnd(byte deviceIndex, byte featureIndex)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionPerKeyV2FrameEnd, Array.Empty<byte>());

        /// <summary>The value a mode read gives while the device runs its
        /// on-board memory mode, and the value that switches it to host
        /// mode: 1 and 2 on feature 0x8100 (libratbag hidpp20.c:1798-1800),
        /// 3 and 5 on feature 0x8101 (OpenRGB
        /// LogitechHIDPP20Controller.cpp:3864-3869 and 3980-3985).</summary>
        public static byte OnBoardMode(ushort modeFeatureId) => modeFeatureId == ProfileManagement ? (byte)0x03 : (byte)0x01;
        public static byte HostMode(ushort modeFeatureId) => modeFeatureId == ProfileManagement ? (byte)0x05 : (byte)0x02;

        /// <summary>Reads the mode. Feature 0x8100: function 2, and the
        /// answer's first byte is the mode (libratbag hidpp20.c:2046-2069,
        /// G815-Hotplug-to-start.pcapng: 11 FF 11 2C). Feature 0x8101:
        /// function 6 with [00]. A G915 X answers it 03 00 01 in its
        /// on-board memory mode and 00 00 01 once Logitech's own program
        /// has switched it to host mode, and the read changes neither
        /// (unplug_and_replug.pcapng: 10 FF 0E 6D 00 before and after
        /// 10 FF 0E 6D 05). The same capture and rigdeck read it with [0F]
        /// as well, whose answer in host mode no source shows, so [00] is
        /// the read used.</summary>
        public static byte[] ModeRead(ushort modeFeatureId, byte deviceIndex, byte featureIndex)
            => modeFeatureId == ProfileManagement
                ? HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionProfileMode, new byte[] { 0x00 })
                : HidppHapticProtocol.Frame(deviceIndex, featureIndex, FunctionOnboardGetMode, Array.Empty<byte>());

        /// <summary>Writes a mode: function 1 on feature 0x8100, function 6
        /// on feature 0x8101, with the mode as the one byte
        /// (G815-Hotplug-to-start.pcapng: 11 FF 11 1C 02,
        /// unplug_and_replug.pcapng: 10 FF 0E 6D 05).</summary>
        public static byte[] ModeWrite(ushort modeFeatureId, byte deviceIndex, byte featureIndex, byte mode)
            => HidppHapticProtocol.Frame(deviceIndex, featureIndex,
                modeFeatureId == ProfileManagement ? FunctionProfileMode : FunctionOnboardSetMode, new[] { mode });

        /// <summary>The G915 TKL's IDs on its receiver and on its cable. Its
        /// top row is G-keys that the on-board profile makes report F1 to
        /// F12, and in host mode the row is dead, so this keyboard is never
        /// switched to host mode (OpenRGB LogitechHIDPP20Controller.h:199
        /// and 221, its KEEP_ONBOARD_MODE entry).</summary>
        public const ushort G915TklWirelessId = 0x408E;
        public const ushort G915TklUsbId = 0xC343;

        /// <summary>Whether a 0x0003 getDeviceInfo answer names a device
        /// that must stay in its on-board memory mode
        /// (<see cref="G915TklWirelessId"/>). The answer is the same on
        /// every transport, which is what makes it the way to know a device
        /// on a cable, behind a receiver and over Bluetooth alike: byte 6
        /// holds one bit per transport, Bluetooth, Bluetooth LE, the
        /// receiver's link and USB from bit 0, and bytes 7 to 12 hold one
        /// product ID for each bit set, in that order (Logitech's "x0003 -
        /// device information", version 4, p.1-3, Solaar
        /// hidpp20.py:1821-1834).</summary>
        public static bool KeepsOnBoardMode(HidppReply deviceInfo)
        {
            foreach (var (bit, id) in ModelIdsByTransport(deviceInfo))
                if ((bit == 2 && id == G915TklWirelessId) || (bit == 3 && id == G915TklUsbId)) return true;
            return false;
        }

        /// <summary>The model IDs a 0x0003 getDeviceInfo answer lists, in
        /// the order of its transport bits (<see cref="KeepsOnBoardMode"/>).</summary>
        public static ushort[] ModelIds(HidppReply deviceInfo)
        {
            var ids = new List<ushort>(3);
            foreach (var (_, id) in ModelIdsByTransport(deviceInfo)) ids.Add(id);
            return ids.ToArray();
        }

        private static IEnumerable<(int Bit, ushort Id)> ModelIdsByTransport(HidppReply deviceInfo)
        {
            int transports = deviceInfo.Param(6);
            int at = 7;
            for (int bit = 0; bit < 4 && at + 1 <= 12; bit++)
            {
                if ((transports & (1 << bit)) == 0) continue;
                ushort id = (ushort)((deviceInfo.Param(at) << 8) | deviceInfo.Param(at + 1));
                at += 2;
                yield return (bit, id);
            }
        }

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

        /// <summary>The POWERPLAY's LIGHTSPEED receiver, 046D:C53A
        /// (OpenRGB LogitechControllerDetect.cpp:96 and 1684, Solaar
        /// base_usb.py:172 and 201), which carries the mat's own lighting
        /// as device index 7, wireless ID 405F (libratbag
        /// data/devices/logitech-g-powerplay.device:3-8, Solaar
        /// descriptors.py:445). The mouse it charges pairs in its
        /// slots as with any receiver.</summary>
        public const ushort PowerPlayReceiver = 0xC53A;

        /// <summary>Whether a receiver carries a device of its own at
        /// <see cref="HidppHapticProtocol.BuiltInDeviceIndex"/>.</summary>
        public static bool HasBuiltInDevice(ushort productId) => productId == PowerPlayReceiver;

        /// <summary>The short request for one long register record.</summary>
        public static byte[] ReadRegister(byte register, byte sub)
        {
            var frame = new byte[ShortReportLength];
            frame[0] = ShortReportId;
            frame[1] = HidppHapticProtocol.DirectDeviceIndex;
            frame[2] = GetLongRegister;
            frame[3] = register;
            frame[4] = sub;
            return frame;
        }

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

        /// <summary>A keyboard's very long collection (#557): the same device
        /// and the same interface by the path key, on usage page 0xFF43 usage
        /// 0x0604, which OpenRGB opens beside any collection on that page
        /// (LogitechControllerDetect.cpp:1237-1265). Null for a collection on
        /// another page and for a keyboard without one.</summary>
        public static VendorHidCollection VeryLongSibling(VendorHidCollection longCollection,
            IEnumerable<VendorHidCollection> all)
        {
            if (longCollection == null || all == null || longCollection.UsagePage != 0xFF43) return null;
            string key = PathKey(longCollection.Path);
            foreach (var c in all)
            {
                if (c == null || c.VendorId != longCollection.VendorId || c.ProductId != longCollection.ProductId) continue;
                if (c.UsagePage != 0xFF43 || c.Usage != HidppHapticProtocol.VeryLongUsage) continue;
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

            for (byte index = HidppHapticProtocol.FirstReceiverIndex; index <= LastSlot(state, channel.Bluetooth); index++)
            {
                AskSlot(channel, state, index, containerId, now, timeout, found, out bool answered);
                if (answered) state.Receiver = true;
                if (state.Dead) break;
            }
            return found;
        }

        /// <summary>The last index a path's walk asks: none past 0xFF for a
        /// device or a Bluetooth path, the six slots of a receiver, and the
        /// device a receiver carries itself where it has one
        /// (<see cref="HidppPathState.BuiltInDevice"/>).</summary>
        internal static int LastSlot(HidppPathState state, bool bluetooth)
            => state.Direct || bluetooth ? 0
                : state.BuiltInDevice ? HidppHapticProtocol.BuiltInDeviceIndex
                : HidppHapticProtocol.LastReceiverIndex;

        /// <summary>True while some slot of the path keeps the scan fast
        /// (<see cref="HidppPathState.Waiting"/>).</summary>
        public static bool Pending(HidppPathState state, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = LastSlot(state, bluetooth);
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
            int last = LastSlot(state, bluetooth);
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
            int last = LastSlot(state, bluetooth);
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
        /// again, and every slot goes back to the walk. A read that never went
        /// out, on a channel without the receiver's short collection or one
        /// whose short write failed, stops the pass and counts toward
        /// nothing, since the receiver was never asked.</summary>
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
                    case HidppReplyKind.Timeout:
                        if (++state.PairingMisses >= PairingGiveUp) Array.Clear(state.Unpaired);
                        return;
                    default:
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
                var zones = StaticZones(channel, deviceIndex, unit.RgbIndex, timeout, out bool zonesMissed, out var where);
                if (zones == null) return null;
                if (zonesMissed) { incomplete = true; return unit; }
                // A zone-only device is painted through the feature just
                // found, so it is asked nothing more than it ever was.
                unit = unit with
                {
                    Zones = zones, ZoneLocations = where,
                    EffectFeatureId = HidppUnitProtocol.ColorLedEffects, EffectIndex = unit.RgbIndex,
                };
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

            // The first pass stops at the first RGB feature it finds, which
            // for every device but a zone-only one is not the whole story:
            // a 0x8071 device may have 0x8081 beside it, and a board whose
            // first is a per-key feature is painted through its 0x8070.
            if (unit.RgbFeatureId != 0 && unit.RgbFeatureId != HidppUnitProtocol.ColorLedEffects)
            {
                var lit = DescribeLighting(channel, unit, timeout, out bool lightingMissed);
                if (lit == null) return null;
                if (lightingMissed) { incomplete = true; return unit; }
                unit = lit;
            }
            return unit;
        }

        /// <summary>
        /// The lighting of a unit whose first RGB feature is not 0x8070,
        /// asked after everything the first pass asks, so that pass and its
        /// order stay what they were. The unit ends with its effect feature
        /// and its per-key feature as separate pairs. Null when a write
        /// failed. <paramref name="missed"/> is set at the first request
        /// that went unanswered (<see cref="Missed"/>), and the caller then
        /// asks the unit again in full.
        ///
        /// <para>A 0x8071 device: 0x8081 is looked up, the clusters are
        /// walked for the Fixed effect, the zones of a 0x8081 found are
        /// read, and a keyboard with both has its multi-cluster effects
        /// walked for Host streaming, then its layout code (0x4540) and its
        /// model IDs (0x0003) read for its light map (#557). Then the
        /// feature that switches the on-board memory mode is looked up,
        /// 0x8101 before 0x8100, the order OpenRGB and Solaar test them in
        /// (LogitechHIDPP20Controller.cpp:3978-3998,
        /// settings_templates.py:3334-3337), and the claim levels the unit
        /// offers follow from it (<see cref="HidppUnit.MaxClaimLevel"/>):
        /// its lighting power always, and its on-board memory mode off
        /// where it has that feature and is not a keyboard that must stay
        /// in it (<see cref="HidppUnitProtocol.KeepsOnBoardMode"/>).</para>
        ///
        /// <para>A device whose first RGB feature is a per-key one, 0x8080
        /// or 0x8081 with no 0x8071: 0x8070 is looked up and its zones
        /// walked. Every known board with 0x8080 lists 0x8070 too (g810-led
        /// Keyboard.cpp:911-937 sends 0x8070 function 3 to each), and that
        /// is the feature such a board is claimed and painted through. With
        /// no effect feature there is no claim to take, and the unit has no
        /// plan.</para>
        /// </summary>
        internal static HidppUnit DescribeLighting(IHidppChannel channel, HidppUnit unit, int timeout, out bool missed)
        {
            missed = false;
            byte device = unit.DeviceIndex;
            HidppReply reply;
            if (unit.RgbFeatureId != HidppUnitProtocol.RgbEffects)
            {
                unit = unit with { PerKeyFeatureId = unit.RgbFeatureId, PerKeyIndex = unit.RgbIndex };
                byte zonesAt = Feature(channel, device, HidppUnitProtocol.ColorLedEffects, timeout, out reply);
                if (reply.Kind == HidppReplyKind.WriteFailed) return null;
                if (Missed(reply)) { missed = true; return unit; }
                if (zonesAt == 0) return unit;
                var zones = StaticZones(channel, device, zonesAt, timeout, out missed, out var where);
                if (zones == null) return null;
                if (missed) return unit;
                unit = unit with
                {
                    EffectFeatureId = HidppUnitProtocol.ColorLedEffects, EffectIndex = zonesAt, Zones = zones, ZoneLocations = where,
                };

                // A keyboard of the G810 class whose very long collection is
                // open is read for its keys, which answer on that report
                // alone, then for its region and its model (#557).
                if (unit.PerKeyFeatureId == HidppUnitProtocol.PerKeyLighting && unit.IsKeyboardKind && channel.HasVeryLong)
                {
                    if (!KeyTypes(channel, device, unit.PerKeyIndex, timeout, out var types, out missed)) return null;
                    if (missed) return unit;
                    if (types.Length > 0)
                    {
                        unit = unit with { KeyTypes = types };
                        if (!ReadKeyboardIdentity(channel, unit, timeout, out unit, out _, out _, out missed)) return null;
                    }
                }
                return unit;
            }

            unit = unit with { EffectFeatureId = HidppUnitProtocol.RgbEffects, EffectIndex = unit.RgbIndex };
            byte perKey = Feature(channel, device, HidppUnitProtocol.PerKeyLightingV2, timeout, out reply);
            if (reply.Kind == HidppReplyKind.WriteFailed) return null;
            if (Missed(reply)) { missed = true; return unit; }

            var clusters = FixedClusters(channel, device, unit.RgbIndex, timeout, out missed, out int multiCluster, out var places);
            if (clusters == null) return null;
            if (missed) return unit;
            unit = unit with { Zones = clusters, ZoneLocations = places };

            HidppReply info = default;
            bool infoRead = false;
            if (perKey != 0)
            {
                unit = unit with { PerKeyFeatureId = HidppUnitProtocol.PerKeyLightingV2, PerKeyIndex = perKey };
                if (!ZonePresence(channel, device, perKey, timeout, out byte[] zones, out missed)) return null;
                if (missed) return unit;
                unit = unit with
                {
                    PerKeyMin = zones.Length > 0 ? zones[0] : (byte)0,
                    PerKeyMax = zones.Length > 0 ? zones[^1] : (byte)0,
                };
                if (clusters.Length > 0 && ClusterStreamPlan.Fits(unit))
                {
                    int streaming = HostStreaming(channel, device, unit.RgbIndex, multiCluster, timeout, out missed, out bool failed);
                    if (failed) return null;
                    if (missed) return unit;
                    unit = unit with { HostStreamingIndex = streaming };

                    // A keyboard painted key by key (#557) is read for its
                    // light map: its zones, its region and its model.
                    unit = unit with { PerKeyZones = zones };
                    if (!ReadKeyboardIdentity(channel, unit, timeout, out unit, out info, out infoRead, out missed)) return null;
                    if (missed) return unit;
                }
            }
            // With no cluster to paint there is no claim, so no level to
            // offer and no mode to look for.
            if (clusters.Length == 0) return unit;

            ushort modeFeature = HidppUnitProtocol.ProfileManagement;
            byte mode = Feature(channel, device, modeFeature, timeout, out reply);
            if (reply.Kind == HidppReplyKind.WriteFailed) return null;
            if (Missed(reply)) { missed = true; return unit; }
            if (mode == 0)
            {
                modeFeature = HidppUnitProtocol.OnboardProfiles;
                mode = Feature(channel, device, modeFeature, timeout, out reply);
                if (reply.Kind == HidppReplyKind.WriteFailed) return null;
                if (Missed(reply)) { missed = true; return unit; }
            }
            if (mode == 0) return unit with { MaxClaimLevel = 1 };

            // The one way to know the device on any transport: its model ID.
            // A device that lists the feature and refuses the read is not
            // switched, since it cannot be told from the keyboard that must
            // not be. One that lacks the feature is not that keyboard, which
            // lists it (Solaar docs/devices, the G915 TKL's dump for 408E).
            if (!infoRead)
            {
                if (!ReadDeviceInfo(channel, device, timeout, out info, out infoRead, out missed)) return null;
                if (missed) return unit;
            }
            bool staysOnBoard = infoRead && (info.Kind != HidppReplyKind.Answer || HidppUnitProtocol.KeepsOnBoardMode(info));
            return unit with { ModeFeatureId = modeFeature, ModeIndex = mode, MaxClaimLevel = staysOnBoard ? 1 : 2 };
        }

        /// <summary>The layout code (0x4540) and the model IDs (0x0003) of a
        /// keyboard painted key by key, for its light map (#557,
        /// <see cref="HidppKeyMap"/>), with the 0x0003 answer for the mode's
        /// own question. False when a write failed. <paramref name="missed"/>
        /// is set when a request went unanswered, and <paramref name="read"/>
        /// is the unit as far as the reads got.</summary>
        private static bool ReadKeyboardIdentity(IHidppChannel channel, HidppUnit unit, int timeout, out HidppUnit read,
            out HidppReply info, out bool infoRead, out bool missed)
        {
            read = unit;
            info = default;
            infoRead = false;
            missed = false;
            byte device = unit.DeviceIndex;
            byte layoutAt = Feature(channel, device, HidppUnitProtocol.KeyboardLayout, timeout, out var reply);
            if (reply.Kind == HidppReplyKind.WriteFailed) return false;
            if (Missed(reply)) { missed = true; return true; }
            if (layoutAt != 0)
            {
                var layout = channel.Request(device, layoutAt, 0, Array.Empty<byte>(), timeout);
                if (layout.Kind == HidppReplyKind.WriteFailed) return false;
                if (Missed(layout)) { missed = true; return true; }
                if (layout.Kind == HidppReplyKind.Answer) read = read with { KeyboardLayout = layout.Param(0) };
            }
            if (!ReadDeviceInfo(channel, device, timeout, out info, out infoRead, out missed)) return false;
            if (missed) return true;
            if (infoRead && info.Kind == HidppReplyKind.Answer) read = read with { ModelIds = HidppUnitProtocol.ModelIds(info) };
            return true;
        }

        /// <summary>
        /// The key types of a 0x8080 keyboard and the IDs of their keys, as
        /// OpenRGB and Logitech Gaming Software walk them
        /// (LogitechHIDPP20Controller.cpp DiscoverPerKey8080 and
        /// ReadPerKey8080Colors, g810-led-resources
        /// dumps/g810-spectrum/set_keys_colors.pcapng: 11 FF 0C 0A, then 1A
        /// and 2A for each type): function 0's type bits, for each known bit
        /// function 1's key count and top channel values, then function 2's
        /// pages of 14 keys, as many as the count needs. The count bounds
        /// the pages, never the keys: a G810 counts 105 keys and its eight
        /// pages list 111 (the same capture). A key ID of 0 is padding. A
        /// type whose count cannot be read is paged until a page lists
        /// nothing, within OpenRGB's bound, and a type whose count is 0 has
        /// no keys. False when a write failed. <paramref name="missed"/> is
        /// set when a request went unanswered.
        /// </summary>
        internal static bool KeyTypes(IHidppChannel channel, byte deviceIndex, byte featureIndex, int timeout,
            out HidppKeyType[] types, out bool missed)
        {
            types = Array.Empty<HidppKeyType>();
            missed = false;
            var info = channel.Request(deviceIndex, featureIndex, HidppUnitProtocol.FunctionPerKeyGetInfo, Array.Empty<byte>(), timeout);
            if (info.Kind == HidppReplyKind.WriteFailed) return false;
            if (Missed(info)) { missed = true; return true; }
            if (info.Kind != HidppReplyKind.Answer) return true;
            int flags = (info.Param(0) << 8) | info.Param(1);
            int maxKeys = (info.Param(5) << 8) | info.Param(6);
            var found = new List<HidppKeyType>();
            foreach (ushort type in HidppUnitProtocol.KnownKeyTypes)
            {
                if ((flags & type) == 0) continue;
                byte hi = (byte)(type >> 8), lo = (byte)type;
                var typeInfo = channel.Request(deviceIndex, featureIndex, HidppUnitProtocol.FunctionPerKeyGetTypeInfo,
                    new[] { hi, lo }, timeout);
                if (typeInfo.Kind == HidppReplyKind.WriteFailed) return false;
                if (Missed(typeInfo)) { missed = true; return true; }
                bool counted = typeInfo.Kind == HidppReplyKind.Answer;
                int count = counted ? (typeInfo.Param(0) << 8) | typeInfo.Param(1) : 0;
                if (counted && count == 0) continue;
                int pages = counted
                    ? (count + HidppUnitProtocol.PerKeyKeysPerFrame - 1) / HidppUnitProtocol.PerKeyKeysPerFrame
                    : Math.Max(16, maxKeys / HidppUnitProtocol.PerKeyKeysPerFrame + 4);
                var keys = new List<byte>();
                for (int page = 0; page < pages; page++)
                {
                    int offset = page * HidppUnitProtocol.PerKeyKeysPerFrame;
                    byte offHi = (byte)(offset >> 8), offLo = (byte)offset;
                    var answer = AskEchoed(channel, deviceIndex, featureIndex, HidppUnitProtocol.FunctionPerKeyGetKeys,
                        new byte[] { hi, lo, offHi, offLo, 0x00 }, timeout,
                        r => r.Param(0) == hi && r.Param(1) == lo && r.Param(2) == offHi && r.Param(3) == offLo);
                    if (answer.Kind == HidppReplyKind.WriteFailed) return false;
                    if (Missed(answer)) { missed = true; return true; }
                    if (answer.Kind != HidppReplyKind.Answer) break;
                    int live = 0;
                    for (int e = 0; e < HidppUnitProtocol.PerKeyKeysPerFrame; e++)
                    {
                        byte id = answer.Param(4 + e * 4);
                        if (id == 0) continue;
                        keys.Add(id);
                        live++;
                    }
                    if (live == 0) break;
                }
                if (keys.Count == 0) continue;
                found.Add(new HidppKeyType(type, keys.ToArray()));
            }
            types = found.ToArray();
            return true;
        }

        /// <summary>Feature 0x0003's getDeviceInfo answer, which lists the
        /// device's model IDs (<see cref="HidppUnitProtocol.ModelIds"/>).
        /// <paramref name="read"/> is false for a device without the
        /// feature. False when a write failed. <paramref name="missed"/> is
        /// set when a request went unanswered.</summary>
        private static bool ReadDeviceInfo(IHidppChannel channel, byte device, int timeout, out HidppReply info,
            out bool read, out bool missed)
        {
            info = default;
            read = false;
            missed = false;
            byte infoAt = Feature(channel, device, HidppUnitProtocol.DeviceInformation, timeout, out var reply);
            if (reply.Kind == HidppReplyKind.WriteFailed) return false;
            if (Missed(reply)) { missed = true; return true; }
            if (infoAt == 0) return true;
            info = channel.Request(device, infoAt, 0, Array.Empty<byte>(), timeout);
            if (info.Kind == HidppReplyKind.WriteFailed) return false;
            if (Missed(info)) { missed = true; return true; }
            read = true;
            return true;
        }

        /// <summary>
        /// The clusters of a 0x8071 device that list the Fixed effect, each
        /// with that effect's index, found by its ID: the index differs
        /// between the clusters of one device (on a G815 Logitech's own
        /// program sets breathing as index 2 on one cluster and 3 on the
        /// other, g810-led-resources
        /// dumps/g815/G815-FX-breathing-FD7600-5000ms.pcapng). The device
        /// answer gives the cluster count in payload 2 and the number of
        /// multi-cluster effects in payload 7, a cluster answer its location
        /// in payloads 2 and 3 and its effect count in payload 4, and an
        /// effect answer the effect's ID in payloads 2 and 3 (Logitech's
        /// x8071 document, p.4-7). Null when a write failed.
        /// <paramref name="missed"/> is set at the first request that went
        /// unanswered, where the walk stops.
        ///
        /// <para>A cluster whose location is 0x0081 to 0x0084 is left out:
        /// those are the states one shared light of a microphone shows,
        /// not places to paint (the same document, p.5-6).</para>
        /// </summary>
        internal static HidppZone[] FixedClusters(IHidppChannel channel, byte deviceIndex, byte featureIndex, int timeout,
            out bool missed, out int multiCluster, out ushort[] locations)
        {
            missed = false;
            multiCluster = 0;
            locations = Array.Empty<ushort>();
            var where = new List<ushort>();
            const byte all = HidppUnitProtocol.AllClusters;
            var info = AskInfo(channel, deviceIndex, featureIndex, all, all, wholeEcho: false, timeout);
            if (info.Kind == HidppReplyKind.WriteFailed) return null;
            missed = Missed(info);
            if (info.Kind != HidppReplyKind.Answer) return Array.Empty<HidppZone>();
            int clusterCount = Math.Min((int)info.Param(2), 16);
            multiCluster = Math.Min((int)info.Param(7), 16);
            var clusters = new List<HidppZone>(clusterCount);
            for (int c = 0; c < clusterCount; c++)
            {
                var cluster = AskInfo(channel, deviceIndex, featureIndex, (byte)c, all, wholeEcho: false, timeout);
                if (cluster.Kind == HidppReplyKind.WriteFailed) return null;
                if (Missed(cluster)) { missed = true; break; }
                if (cluster.Kind != HidppReplyKind.Answer) continue;
                int location = (cluster.Param(2) << 8) | cluster.Param(3);
                if (location >= 0x0081 && location <= 0x0084) continue;
                int effectCount = Math.Min((int)cluster.Param(4), 32);
                for (int e = 0; e < effectCount && !missed; e++)
                {
                    var effect = AskInfo(channel, deviceIndex, featureIndex, (byte)c, (byte)e, wholeEcho: true, timeout);
                    if (effect.Kind == HidppReplyKind.WriteFailed) return null;
                    if (Missed(effect)) { missed = true; break; }
                    if (effect.Kind != HidppReplyKind.Answer) continue;
                    ushort id = (ushort)((effect.Param(2) << 8) | effect.Param(3));
                    if (id != HidppUnitProtocol.StaticEffectId) continue;
                    clusters.Add(new HidppZone((byte)c, (byte)e));
                    where.Add((ushort)location);
                    break;
                }
                if (missed) break;
            }
            locations = where.ToArray();
            return clusters.ToArray();
        }

        /// <summary>
        /// The place of the Host streaming effect among a 0x8071 device's
        /// multi-cluster effects, or -1 when it lists none. The list is the
        /// one cluster 0xFF takes its effects from, read entry by entry as
        /// Logitech's own program reads it (OpenRGB issue 4461,
        /// unplug_and_replug.pcapng: 11 FF 08 0D FF 01 00, answered
        /// FF 01 00 13). The walk ends at the effect, or at the first entry
        /// the device refuses. <paramref name="failed"/> is set when a write
        /// failed, <paramref name="missed"/> when a request went unanswered.
        /// </summary>
        internal static int HostStreaming(IHidppChannel channel, byte deviceIndex, byte featureIndex, int count, int timeout,
            out bool missed, out bool failed)
        {
            missed = false;
            failed = false;
            for (int e = 0; e < count; e++)
            {
                var effect = AskInfo(channel, deviceIndex, featureIndex, HidppUnitProtocol.AllClusters, (byte)e,
                    wholeEcho: true, timeout);
                if (effect.Kind == HidppReplyKind.WriteFailed) { failed = true; return -1; }
                if (Missed(effect)) { missed = true; return -1; }
                if (effect.Kind != HidppReplyKind.Answer) return -1;
                if (((effect.Param(2) << 8) | effect.Param(3)) == HidppUnitProtocol.HostStreamingEffectId) return e;
            }
            return -1;
        }

        /// <summary>
        /// Every zone a 0x8081 device has, lowest first, from its three
        /// pages of presence bits, bit i of a page for zone page * 112 + i
        /// with the first bit the low one of the first byte (Logitech's
        /// x8081 document, p.4-6, read the same way by OpenRGB
        /// LogitechHIDPP20Controller.cpp:3026-3082 and Solaar
        /// settings_templates.py:4165-4169). Zones 0 and 255 are not zones
        /// and are passed over, though a device may set their bits (the
        /// same document, p.1 and p.5). A page the device refuses holds
        /// none. Empty for a device with no zone. False when a write
        /// failed. <paramref name="missed"/> is set when a request went
        /// unanswered.
        /// </summary>
        internal static bool ZonePresence(IHidppChannel channel, byte deviceIndex, byte featureIndex, int timeout,
            out byte[] zones, out bool missed)
        {
            var found = new List<byte>();
            zones = Array.Empty<byte>();
            missed = false;
            for (byte page = 0; page <= 2; page++)
            {
                byte asked = page;
                var reply = AskEchoed(channel, deviceIndex, featureIndex, HidppUnitProtocol.FunctionPerKeyV2GetInfo,
                    new byte[] { 0x00, page, 0x00 }, timeout, r => r.Param(0) == 0x00 && r.Param(1) == asked);
                if (reply.Kind == HidppReplyKind.WriteFailed) return false;
                if (Missed(reply)) { missed = true; return true; }
                if (reply.Kind != HidppReplyKind.Answer) continue;
                for (int bit = 0; bit < 112; bit++)
                {
                    if ((reply.Param(2 + bit / 8) & (1 << (bit % 8))) == 0) continue;
                    int zone = page * 112 + bit;
                    if (zone < 1 || zone > 254) continue;
                    found.Add((byte)zone);
                }
            }
            zones = found.ToArray();
            return true;
        }

        /// <summary>One 0x8071 getInfo question. All three kinds of question
        /// share one function, so the answer is checked against the
        /// question: its first byte against the cluster asked about, and
        /// for an effect its second byte against the effect. A device or a
        /// cluster answer is not held to its second byte, which a G915 X
        /// answers as 00 where the question carried FF (OpenRGB issue 4461,
        /// unplug_and_replug.pcapng: 10 FF 08 0D FF FF 00, answered
        /// FF 00 02 00 63 00 04 08).</summary>
        private static HidppReply AskInfo(IHidppChannel channel, byte deviceIndex, byte featureIndex, byte cluster,
            byte effect, bool wholeEcho, int timeout)
            => AskEchoed(channel, deviceIndex, featureIndex, HidppUnitProtocol.FunctionRgbGetInfo,
                new byte[] { cluster, effect, 0x00 }, timeout,
                r => r.Param(0) == cluster && (!wholeEcho || r.Param(1) == effect));

        /// <summary>A request whose answer names the question it answers.
        /// An answer to another question is not this one's: the same
        /// function byte under the same software ID can carry another
        /// program's answer, and Logitech's own program has used this ID
        /// (g810-led-resources dumps/g815/G815-Hotplug-to-start.pcapng,
        /// every frame). Such an answer is asked for once more. The second
        /// one is taken when it names the question, or when it is the first
        /// one byte for byte, which is a device that names no question at
        /// all. Anything else reads as unanswered, so the unit is asked
        /// again in full.</summary>
        private static HidppReply AskEchoed(IHidppChannel channel, byte deviceIndex, byte featureIndex, byte function,
            byte[] parameters, int timeout, Func<HidppReply, bool> names)
        {
            var reply = channel.Request(deviceIndex, featureIndex, function, parameters, timeout);
            if (reply.Kind != HidppReplyKind.Answer || names(reply)) return reply;
            var again = channel.Request(deviceIndex, featureIndex, function, parameters, timeout);
            if (again.Kind != HidppReplyKind.Answer || names(again)) return again;
            for (int i = 0; i < HidppHapticProtocol.LongReportLength - 4; i++)
                if (reply.Param(i) != again.Param(i)) return new HidppReply(HidppReplyKind.Timeout);
            return again;
        }

        /// <summary>The zones of a 0x8070 device that list the static effect,
        /// each with that effect's index. GetZoneInfo carries Solaar's
        /// trailing FF 00 (hidpp20.py:1354-1357). Null when a write failed.
        /// <paramref name="missed"/> is set at the first request that went
        /// unanswered or stayed busy (<see cref="Missed"/>), where the walk
        /// stops, so the caller asks again rather than keep a device with
        /// zones missing.</summary>
        internal static HidppZone[] StaticZones(IHidppChannel channel, byte deviceIndex, byte featureIndex, int timeout,
            out bool missed, out ushort[] locations)
        {
            missed = false;
            locations = Array.Empty<ushort>();
            var where = new List<ushort>();
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
                    where.Add((ushort)((zoneInfo.Param(1) << 8) | zoneInfo.Param(2)));
                    break;
                }
                if (missed) break;
            }
            locations = where.ToArray();
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
