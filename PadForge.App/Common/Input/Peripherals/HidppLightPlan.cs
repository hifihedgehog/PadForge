using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>How one frame of a light plan goes out.</summary>
    internal enum HidppStepKind
    {
        /// <summary>Written, and nothing waits on its answer.</summary>
        Write,

        /// <summary>The frame that shows what the frames before it staged.
        /// The worker waits for its answer, and the paint counts as sent
        /// only once the device gives it.</summary>
        Commit,

        /// <summary>A frame of the per-key stream. The worker waits for its
        /// answer. One that goes unanswered ends the paint, and one the
        /// device refuses ends the per-key path for the unit.</summary>
        Stream,

        /// <summary>The choice of the Host streaming effect. The worker
        /// waits for its answer. One that goes unanswered ends the paint,
        /// and a refusal is logged and the paint goes on.</summary>
        Select,
    }

    /// <summary>One frame of a light plan and how it goes out.</summary>
    internal readonly record struct HidppStep(byte[] Frame, HidppStepKind Kind = HidppStepKind.Write);

    /// <summary>
    /// The frames that light one HID++ unit (#494), as pure lists: what
    /// takes the lighting from the device's own effect at each claim level,
    /// what paints a color, what holds the claim, and what hands the device
    /// back. One plan per pair of features a device lights through:
    ///
    /// <para><see cref="ZonePlan"/>, feature 0x8070 alone, the class the
    /// first build lit. <see cref="ZonePerKeyPlan"/>, 0x8070 with the
    /// per-key feature 0x8080, the G810 class. <see cref="ClusterPlan"/>,
    /// feature 0x8071 alone. <see cref="ClusterStreamPlan"/>, 0x8071 with
    /// the per-key feature 0x8081 on a keyboard.</para>
    ///
    /// <para>The claim level is how firmly the device is held: 0 its colors
    /// alone, 1 its lighting power too, 2 its on-board memory mode off as
    /// well. Only a 0x8071 device has levels above 0. A plan builds no frame
    /// that writes the device's memory.</para>
    /// </summary>
    internal abstract class HidppLightPlan
    {
        /// <summary>How long the worker waits for the answer to a frame it
        /// awaits, OpenRGB's wait for a per-key answer
        /// (LogitechHIDPP20Controller.h:511). A wired board answers in 2 to
        /// 6 ms (g810-led-resources
        /// dumps/g810-spectrum/set_fx_color_00ff00.pcapng).</summary>
        public const int AckTimeoutMs = 60;

        /// <summary>Paints in a row whose awaited frame went unanswered
        /// after which a unit is left alone until its next keep-alive.
        /// OpenRGB reads five as a sleeping device
        /// (LogitechHIDPP20Controller.h:516-524).</summary>
        public const int FailedPaintLimit = 5;

        protected readonly byte Device;

        protected HidppLightPlan(HidppUnit unit, int maxLevel)
        {
            Device = unit.DeviceIndex;
            MaxLevel = maxLevel;
        }

        /// <summary>The highest claim level the plan has frames for.</summary>
        public int MaxLevel { get; }

        /// <summary>The least time between two frames to the unit, 0 for
        /// frames that go out back to back.</summary>
        public abstract int FrameGapMs { get; }

        /// <summary>The least time between two paints of the unit.</summary>
        public abstract int PaintMs { get; }

        /// <summary>The read of the on-board memory mode a claim at level 2
        /// makes first, or null for a plan with no mode to switch.</summary>
        public virtual byte[] ModeRead => null;

        /// <summary>The mode a <see cref="ModeRead"/> answer carries.</summary>
        public virtual byte ModeOf(HidppReply reply) => 0;

        /// <summary>Whether a claim at <paramref name="level"/> switches the
        /// device to host mode, given the mode read before it: only at level
        /// 2, and only when the device was in its on-board memory mode. A
        /// device its owner keeps in host mode is left there, and nothing
        /// is switched back at the hand-back that the claim did not switch.</summary>
        public virtual bool SwitchesMode(int level, byte savedMode) => false;

        /// <summary>The frames that take the lighting. At level 2 the switch
        /// to host mode comes first, when <see cref="SwitchesMode"/> holds.</summary>
        public abstract HidppStep[] Claim(int level, byte savedMode);

        /// <summary>The frames that show one color. <paramref name="full"/>
        /// is the first paint after a claim, which sets up everything a
        /// later paint relies on.</summary>
        public abstract HidppStep[] Paint(int rgb, bool full);

        /// <summary>Whether the plan paints a color per light (#557,
        /// <see cref="PaintLights"/>).</summary>
        public virtual bool PaintsLights => false;

        /// <summary>The frames that show one color per light of
        /// <paramref name="map"/> (#557), <paramref name="colors"/> in the
        /// map's order. With <paramref name="shown"/>, what the lights show
        /// now, only the lights whose color changed go out. Without it the
        /// whole picture does, as a keep-alive and a first paint send it.
        /// <paramref name="full"/> is the first paint after a claim. Null
        /// for a plan that paints one color alone.</summary>
        public virtual HidppStep[] PaintLights(LightMap map, int[] colors, int[] shown, bool full) => null;

        /// <summary>The frames that hold the claim of a unit painted light by
        /// light (#557), as <see cref="KeepAlive"/> holds one painted one
        /// color. Null for a plan that paints one color alone.</summary>
        public virtual HidppStep[] KeepAliveLights(int level, LightMap map, int[] colors, bool painted) => null;

        /// <summary>The frames that hold the claim, sent on the worker's
        /// re-assert interval. For a plan with a <see cref="ClaimRead"/>
        /// they are what goes out while that read shows the claim
        /// standing. <paramref name="painted"/> is false until a paint has
        /// been shown in full since the claim: a plan whose later paints
        /// rely on what its first one set up sends that first paint whole
        /// until one has landed.</summary>
        public abstract HidppStep[] KeepAlive(int level, int rgb, bool painted);

        /// <summary>The frames that hand the device back to its own
        /// lighting: the release first, and at level 2 the mode the claim
        /// switched away from last.</summary>
        public abstract HidppStep[] HandBack(int level, byte savedMode);

        /// <summary>The read a keep-alive makes before anything else, or
        /// null for a plan whose keep-alive repeats the claim unasked.</summary>
        public virtual byte[] ClaimRead => null;

        /// <summary>Whether a <see cref="ClaimRead"/> answer shows the claim
        /// still standing.</summary>
        public virtual bool ClaimStands(HidppReply reply) => true;

        /// <summary>The plan the unit falls back to when the device refuses
        /// a per-key frame, or null for a plan with nothing to fall back
        /// from.</summary>
        public virtual HidppLightPlan WithoutPerKey => null;

        /// <summary>Whether a refused awaited frame of this kind sends the
        /// unit to <see cref="WithoutPerKey"/>: any per-key frame, and the
        /// frame end that shows them.</summary>
        public virtual bool FallsBackOn(HidppStepKind kind) => kind is HidppStepKind.Stream or HidppStepKind.Commit;

        /// <summary>The frames alone, for the crash path's record.</summary>
        public static byte[][] Frames(HidppStep[] steps)
        {
            var frames = new byte[steps.Length][];
            for (int i = 0; i < steps.Length; i++) frames[i] = steps[i].Frame;
            return frames;
        }

        /// <summary>The plan for a unit, or null for one this build cannot
        /// paint (<see cref="HidppUnit.HasLightPlan"/>). A unit that carries
        /// no effect pair of its own, one built from its first RGB feature
        /// alone, is the zone-only 0x8070 class.</summary>
        public static HidppLightPlan For(HidppUnit unit)
        {
            if (unit == null || !unit.HasLightPlan) return null;
            ushort effect = unit.EffectFeatureId != 0 ? unit.EffectFeatureId : unit.RgbFeatureId;
            byte index = unit.EffectFeatureId != 0 ? unit.EffectIndex : unit.RgbIndex;
            switch (effect)
            {
                case HidppUnitProtocol.ColorLedEffects:
                    return unit.PerKeyFeatureId == HidppUnitProtocol.PerKeyLighting && unit.PerKeyIndex != 0
                        ? new ZonePerKeyPlan(unit, index)
                        : new ZonePlan(unit, index);
                case HidppUnitProtocol.RgbEffects:
                    return ClusterStreamPlan.Fits(unit) ? new ClusterStreamPlan(unit, index) : new ClusterPlan(unit, index);
                default:
                    return null;
            }
        }

        protected static HidppStep Write(byte[] frame) => new(frame);
    }

    /// <summary>
    /// Feature 0x8070 alone: the zone-only class the first build lit, with
    /// the frames it sent then, back to back and unawaited. SetSWControl
    /// [01 01] takes the lighting, each zone gets the static effect in the
    /// color, both go out again at every keep-alive, and [00 00] hands the
    /// lighting back (OpenRGB LogitechHIDPP20Controller.cpp:3935-3946 and
    /// 4058-4078).
    /// </summary>
    internal class ZonePlan : HidppLightPlan
    {
        protected readonly byte Effect;
        protected readonly HidppZone[] Zones;

        public ZonePlan(HidppUnit unit, byte effectIndex) : base(unit, 0)
        {
            Effect = effectIndex;
            Zones = unit.Zones;
        }

        public override int FrameGapMs => 0;
        public override int PaintMs => HidppBackend.PaintMs;

        public override HidppStep[] Claim(int level, byte savedMode)
            => new[] { Write(HidppUnitProtocol.SetSwControl(Device, Effect, claim: true)) };

        public override HidppStep[] Paint(int rgb, bool full)
        {
            var steps = new List<HidppStep>(Zones.Length);
            AddZones(steps, rgb);
            return steps.ToArray();
        }

        public override HidppStep[] KeepAlive(int level, int rgb, bool painted)
        {
            var steps = new List<HidppStep>();
            steps.AddRange(Claim(level, 0));
            steps.AddRange(Paint(rgb, full: true));
            return steps.ToArray();
        }

        public override HidppStep[] HandBack(int level, byte savedMode)
            => new[] { Write(HidppUnitProtocol.SetSwControl(Device, Effect, claim: false)) };

        protected void AddZones(List<HidppStep> steps, int rgb)
        {
            foreach (var zone in Zones)
                steps.Add(Write(HidppUnitProtocol.SetStaticColor(Device, Effect, zone,
                    (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));
        }
    }

    /// <summary>
    /// Feature 0x8070 with the per-key feature 0x8080, the G810 class. The
    /// claim and the zone effects are <see cref="ZonePlan"/>'s. On a G810
    /// the two features draw into one buffer. After the static effect on
    /// both zones every key, the logo and the media keys read the zone
    /// color back, the media keys with the red of 01 that a fill before the
    /// effect had set back at 00, and a lock indicator that is lit reads
    /// the color of the fill (g810-led-resources
    /// dumps/g810-spectrum/set_fx_color_00ff00.pcapng, the readbacks after
    /// its zone frames). That capture cannot show whether the effect colors
    /// the indicators too: the fill before it carried the same color.
    ///
    /// <para>So each paint also fills the indicators and the media keys and
    /// commits, the frames Logitech Gaming Software sends for its fixed
    /// color (the same capture: 4C 00 40, 4C 00 02, 5C, then the two zone
    /// frames). The zone effects go first here, where that program sends
    /// them last. For a color the two orders end the same but for the
    /// media keys' red, which the fill here leaves at 01 where the effect
    /// would leave 00. For black the
    /// static effect is sent with no color marker, which leaves the buffer
    /// as it stands (<see cref="HidppUnitProtocol.SetStaticColor"/>), so
    /// black also fills the keys, the G-keys and the logo, the fills that
    /// program clears a board with
    /// (dumps/g810-spectrum/set_keys_colors.pcapng), and only with the
    /// effect ended first can nothing draw over them.</para>
    ///
    /// <para>The commit is awaited: a board answers busy to one that comes
    /// too soon, and a commit that is lost leaves the fills unshown.</para>
    ///
    /// <para>The hand-back fills the indicators and the media keys white
    /// before the release. The release returns the keys to the board's own
    /// effect, and the indicators would keep the last fill, dark after
    /// black. What they showed before the claim cannot be read back: an
    /// indicator that is off reads 00 00 00 whatever its fill
    /// (set_fx_color_00ff00.pcapng, the indicator readback after the fill:
    /// 01 00 FF 00 02 00 00 00 ...), and the board answers the read on its
    /// 64-byte report 0x12, on a collection this channel does not open.
    /// White is what Logitech Gaming Software gives both when it starts a
    /// board
    /// (dumps/g910-spark/G910_gkey_mapping_and_mkeys.pcapng: 4E 00 40 FF FF
    /// FF, 4E 00 02 FF FF FF).</para>
    /// </summary>
    internal sealed class ZonePerKeyPlan : ZonePlan
    {
        private readonly byte _perKey;
        private readonly HidppUnit _unit;

        public ZonePerKeyPlan(HidppUnit unit, byte effectIndex) : base(unit, effectIndex)
        {
            _perKey = unit.PerKeyIndex;
            _unit = unit;
        }

        /// <summary>A board whose keys were read paints them one by one
        /// (#557).</summary>
        public override bool PaintsLights => _unit.KeyTypes.Length > 0;

        /// <summary>A board that refuses a key frame is painted one color
        /// from then on. A refused commit is a failed paint, as it always
        /// was.</summary>
        public override bool FallsBackOn(HidppStepKind kind) => kind == HidppStepKind.Stream;

        public override HidppLightPlan WithoutPerKey
            => PaintsLights ? new ZonePerKeyPlan(_unit with { KeyTypes = Array.Empty<HidppKeyType>() }, Effect) : null;

        /// <summary>
        /// One color per key (#557), the way Logitech Gaming Software paints
        /// a G810 key by key (g810-led-resources
        /// dumps/g810-spectrum/set_keys_colors.pcapng) and OpenRGB sends a
        /// frame (SetPerKey8080). The whole picture is the static effect on
        /// each zone in black, which carries no color marker and so passes
        /// the per-key buffer through (OpenRGB SetZoneEffect: "pass-through
        /// to per-key buffer"), then each key type filled with the color most
        /// of its keys take, then the keys of every other color, 14 to a very
        /// long frame, then the awaited commit. A change sends the keys whose
        /// color changed and the commit, or the whole picture when that
        /// takes fewer frames. A media key's red follows
        /// <see cref="HidppUnitProtocol.MediaRed"/>, as its fill's does.
        /// </summary>
        public override HidppStep[] PaintLights(LightMap map, int[] colors, int[] shown, bool full)
        {
            var whole = new List<HidppStep>();
            AddZones(whole, 0);
            var under = new int[colors.Length];
            foreach (var type in _unit.KeyTypes)
            {
                var ofType = new List<int>();
                for (int i = 0; i < map.Lights.Count && i < colors.Length; i++)
                    if (HidppKeyMap.KeyAt(map.Lights[i].Address).Type == type.Type) ofType.Add(i);
                if (ofType.Count == 0) continue;
                var typeColors = new int[ofType.Count];
                for (int k = 0; k < typeColors.Length; k++) typeColors[k] = colors[ofType[k]];
                int most = ClusterStreamPlan.MostCommon(typeColors);
                foreach (int i in ofType) under[i] = most;
                whole.Add(Fill(type.Type, Ready(type.Type, most)));
            }
            AddKeys(whole, map, colors, under);
            if (shown != null && !full)
            {
                var changed = new List<HidppStep>();
                AddKeys(changed, map, colors, shown);
                if (changed.Count <= whole.Count) whole = changed;
            }
            whole.Add(new HidppStep(HidppUnitProtocol.PerKeyCommit(Device, _perKey), HidppStepKind.Commit));
            return whole.ToArray();
        }

        /// <summary>The claim again and the whole picture, as
        /// <see cref="ZonePlan.KeepAlive"/> sends the claim and the color.</summary>
        public override HidppStep[] KeepAliveLights(int level, LightMap map, int[] colors, bool painted)
        {
            var steps = new List<HidppStep>();
            steps.AddRange(Claim(level, 0));
            steps.AddRange(PaintLights(map, colors, null, full: true));
            return steps.ToArray();
        }

        /// <summary>The key frames for the lights whose color differs from
        /// what they show, by type in the board's order, 14 to a frame, as
        /// OpenRGB groups a frame's keys (SetPerKey8080).</summary>
        private void AddKeys(List<HidppStep> steps, LightMap map, int[] colors, int[] shown)
        {
            var byType = new Dictionary<ushort, List<(byte Id, int Rgb)>>();
            for (int i = 0; i < map.Lights.Count && i < colors.Length; i++)
            {
                if (colors[i] == shown[i]) continue;
                var (type, id) = HidppKeyMap.KeyAt(map.Lights[i].Address);
                if (!byType.TryGetValue(type, out var keys)) byType[type] = keys = new List<(byte, int)>();
                keys.Add((id, Ready(type, colors[i])));
            }
            foreach (var type in _unit.KeyTypes)
            {
                if (!byType.TryGetValue(type.Type, out var keys)) continue;
                for (int i = 0; i < keys.Count; i += HidppUnitProtocol.PerKeyKeysPerFrame)
                    steps.Add(new HidppStep(HidppUnitProtocol.PerKeySetKeys(Device, _perKey, type.Type,
                        keys.GetRange(i, Math.Min(HidppUnitProtocol.PerKeyKeysPerFrame, keys.Count - i))), HidppStepKind.Stream));
            }
        }

        /// <summary>A color as a key of <paramref name="type"/> takes it: a
        /// media key's red follows <see cref="HidppUnitProtocol.MediaRed"/>.</summary>
        private static int Ready(ushort type, int rgb)
        {
            if (type != HidppUnitProtocol.KeyTypeMedia) return rgb;
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            return (HidppUnitProtocol.MediaRed(r, g, b) << 16) | (g << 8) | b;
        }

        private HidppStep Fill(ushort keyType, int rgb) => Fill(keyType, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        public override HidppStep[] Paint(int rgb, bool full)
        {
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            var steps = new List<HidppStep>(Zones.Length + 6);
            AddZones(steps, rgb);
            steps.Add(Fill(HidppUnitProtocol.KeyTypeIndicators, r, g, b));
            steps.Add(Fill(HidppUnitProtocol.KeyTypeMedia, HidppUnitProtocol.MediaRed(r, g, b), g, b));
            if (rgb == 0)
            {
                steps.Add(Fill(HidppUnitProtocol.KeyTypeKeys, 0, 0, 0));
                steps.Add(Fill(HidppUnitProtocol.KeyTypeGKeys, 0, 0, 0));
                steps.Add(Fill(HidppUnitProtocol.KeyTypeLogo, 0, 0, 0));
            }
            steps.Add(new HidppStep(HidppUnitProtocol.PerKeyCommit(Device, _perKey), HidppStepKind.Commit));
            return steps.ToArray();
        }

        public override HidppStep[] HandBack(int level, byte savedMode) => new[]
        {
            Fill(HidppUnitProtocol.KeyTypeIndicators, 0xFF, 0xFF, 0xFF),
            Fill(HidppUnitProtocol.KeyTypeMedia, 0xFF, 0xFF, 0xFF),
            new HidppStep(HidppUnitProtocol.PerKeyCommit(Device, _perKey), HidppStepKind.Commit),
            Write(HidppUnitProtocol.SetSwControl(Device, Effect, claim: false)),
        };

        private HidppStep Fill(ushort keyType, byte r, byte g, byte b)
            => Write(HidppUnitProtocol.PerKeyFill(Device, _perKey, keyType, r, g, b));
    }

    /// <summary>
    /// Feature 0x8071 alone: one Fixed effect per cluster under a claim made
    /// with manageSwControl.
    ///
    /// <para>Level 0 claims with [01 01 00], the cluster flag alone. The
    /// device keeps its power saving and its on-board profile keeps running
    /// (Logitech's x8071 document, p.26-27, and OpenLogi pull request 523,
    /// where a G903 took five colors under it). Levels 1 and 2 claim as
    /// Logitech's own program does, [01 03 07], power mode Full, [01 03 05]
    /// (g810-led-resources dumps/g815/G815-Hotplug-to-start.pcapng, and
    /// rigdeck lighting.py:27-30, which replays the three on a PRO X TKL
    /// RAPID with no mode frame). The power flag stops the device from
    /// dimming its lights by itself. That program's write of the power
    /// timers between them is left out. Level 2 switches the device to host
    /// mode first, the order that program and Solaar keep
    /// (settings_templates.py:3332-3339), and only a device read in its
    /// on-board memory mode is switched.</para>
    ///
    /// <para>The bytes are fixed at every level. They are never the flags
    /// read back and widened, so a power flag a program that died left
    /// behind does not outlive a claim at level 0.</para>
    ///
    /// <para>A keep-alive repeats the claim and the colors: [01 01 00] at
    /// level 0, and [01 03 05] with power mode Full at the levels above,
    /// never [01 03 07] and never the mode frame. The mode is switched
    /// once per claim: on a G515 the call "that switches the keyboard
    /// between onboard and host control mode" is named as "the call that
    /// resets the keyboard's internal state and drops the modifiers", with
    /// the same behavior under Logitech's own program (Solaar issue 3310,
    /// where it is written as 0x8081 function 6, the number of the 0x8101
    /// mode call). The hand-back is [01 00 00], then the mode the claim
    /// switched away from, release first as OpenRGB and Solaar order it
    /// (LogitechHIDPP20Controller.cpp:3837-3877,
    /// settings_templates.py:3374-3388).</para>
    ///
    /// <para>Frames to one device are spaced: OpenLogi keeps 8 ms between
    /// its cluster writes because "the controller can drop closely-spaced
    /// reports" (openlogi-device write/lighting.rs:61-62, and
    /// write/lighting/rgb.rs:71 where the wait follows each cluster). A
    /// device is repainted no oftener than every 50 ms, the 20 frames a
    /// second OpenGHub streams to these devices (docs/lighting.md:14-17).</para>
    /// </summary>
    internal class ClusterPlan : HidppLightPlan
    {
        /// <summary>The least time between two frames to a 0x8071 device.</summary>
        public const int ClusterGapMs = 8;

        /// <summary>The least time between two paints of a 0x8071 device.</summary>
        public const int ClusterPaintMs = 50;

        protected readonly HidppUnit Unit;
        protected readonly byte Effect;

        public ClusterPlan(HidppUnit unit, byte effectIndex) : base(unit, Math.Clamp(unit.MaxClaimLevel, 0, 2))
        {
            Unit = unit;
            Effect = effectIndex;
        }

        public override int FrameGapMs => ClusterGapMs;
        public override int PaintMs => ClusterPaintMs;

        public override byte[] ModeRead => Unit.ModeFeatureId == 0 || Unit.ModeIndex == 0
            ? null
            : HidppUnitProtocol.ModeRead(Unit.ModeFeatureId, Device, Unit.ModeIndex);

        public override byte ModeOf(HidppReply reply) => reply.Param(0);

        public override bool SwitchesMode(int level, byte savedMode)
            => level >= 2 && ModeRead != null && savedMode == HidppUnitProtocol.OnBoardMode(Unit.ModeFeatureId);

        public override HidppStep[] Claim(int level, byte savedMode)
        {
            var steps = new List<HidppStep>(4);
            if (SwitchesMode(level, savedMode))
                steps.Add(Write(HidppUnitProtocol.ModeWrite(Unit.ModeFeatureId, Device, Unit.ModeIndex,
                    HidppUnitProtocol.HostMode(Unit.ModeFeatureId))));
            if (level <= 0)
            {
                steps.Add(Write(HidppUnitProtocol.RgbSwControl(Device, Effect, HidppUnitProtocol.SwControlClusters, 0x00)));
            }
            else
            {
                steps.Add(Write(HidppUnitProtocol.RgbSwControl(Device, Effect, Both, 0x07)));
                steps.Add(Write(HidppUnitProtocol.RgbPowerFull(Device, Effect)));
                steps.Add(Write(HidppUnitProtocol.RgbSwControl(Device, Effect, Both, 0x05)));
            }
            return steps.ToArray();
        }

        public override HidppStep[] Paint(int rgb, bool full)
        {
            var steps = new List<HidppStep>(Unit.Zones.Length);
            AddClusters(steps, rgb);
            return steps.ToArray();
        }

        public override HidppStep[] KeepAlive(int level, int rgb, bool painted)
        {
            var steps = new List<HidppStep>(Unit.Zones.Length + 2);
            AddHold(steps, level);
            AddClusters(steps, rgb);
            return steps.ToArray();
        }

        public override HidppStep[] HandBack(int level, byte savedMode)
        {
            var steps = new List<HidppStep>(2)
            {
                Write(HidppUnitProtocol.RgbSwControl(Device, Effect, 0x00, 0x00)),
            };
            if (SwitchesMode(level, savedMode))
                steps.Add(Write(HidppUnitProtocol.ModeWrite(Unit.ModeFeatureId, Device, Unit.ModeIndex, savedMode)));
            return steps.ToArray();
        }

        /// <summary>The claim as a keep-alive repeats it.</summary>
        protected void AddHold(List<HidppStep> steps, int level)
        {
            if (level <= 0)
            {
                steps.Add(Write(HidppUnitProtocol.RgbSwControl(Device, Effect, HidppUnitProtocol.SwControlClusters, 0x00)));
                return;
            }
            steps.Add(Write(HidppUnitProtocol.RgbSwControl(Device, Effect, Both, 0x05)));
            steps.Add(Write(HidppUnitProtocol.RgbPowerFull(Device, Effect)));
        }

        protected void AddClusters(List<HidppStep> steps, int rgb)
        {
            foreach (var cluster in Unit.Zones)
                steps.Add(Write(HidppUnitProtocol.RgbFixedColor(Device, Effect, cluster,
                    (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));
        }

        private const byte Both = HidppUnitProtocol.SwControlClusters | HidppUnitProtocol.SwControlPower;
    }

    /// <summary>
    /// Feature 0x8071 with the per-key feature 0x8081, on a keyboard. The
    /// cluster effect "lights the main board only (not media/G-keys)"
    /// (g915x-heatmap PROTOCOL.md:97), so the color also goes to every zone
    /// through the per-key feature. The claim, its levels and the hand-back
    /// are <see cref="ClusterPlan"/>'s: the per-key frames add no claim of
    /// their own, and after the release the profile's own effect returns
    /// (rigdeck lighting.py:34-36).
    ///
    /// <para>A first paint sets each cluster to the Fixed effect in the
    /// color, so the board is right through the frames Logitech's own
    /// program uses for a solid color (OpenRGB issue 4461, all_blue.pcapng),
    /// whatever the per-key frames then do.
    /// It then chooses the Host streaming effect when the keyboard lists
    /// one, as that program does before its first per-key frame
    /// (<see cref="HidppUnitProtocol.RgbHostStreaming"/>), paints one range
    /// over every zone, and ends the frame. The range and the frame end go
    /// out twice: the first per-key frame after a claim can land before
    /// the firmware has let go, and Solaar replays it once for that
    /// (settings_templates.py:4089-4093).</para>
    ///
    /// <para>A later paint is the range and the frame end alone. A cluster
    /// effect sent while the per-key frames show takes the display back and
    /// blanks what they painted (g915x-heatmap PROTOCOL.md:90-92), so no
    /// cluster frame follows the first paint. For the same reason the
    /// keep-alive does not repeat the claim and the cluster colors. It
    /// reads the claim, repaints through the per-key frames while the
    /// cluster flag still stands, and the worker claims and paints in full
    /// again only when the read shows the flag gone, which is how a
    /// keyboard that slept and woke behind its receiver reads. A keyboard
    /// whose first paint has not landed yet has nothing on show to take
    /// back, and its keep-alive is that first paint whole.</para>
    ///
    /// <para>Every per-key frame is awaited. Logitech's guideline for the
    /// feature is that "all the hidpp requests have to be validated"
    /// (x8081 document, p.3), its own program waits for each answer
    /// (OpenRGB issue 4461, unplug_and_replug.pcapng), and a refused range
    /// would otherwise leave the board on the stream's empty picture. A
    /// mouse with both features stays on <see cref="ClusterPlan"/>: the one
    /// such mouse known, the G502 X PLUS, answers with a single cluster
    /// (OpenRGB-Fixed diagnostics/OpenRGB-Diagnostics/2026-09-13-g502-rcca/
    /// control-after-handoff.json:42-59), and optimized per-key frames left
    /// one of its lights on an old color (HaloDaemon-plugins
    /// lighting.lua:128-131).</para>
    /// </summary>
    internal sealed class ClusterStreamPlan : ClusterPlan
    {
        public ClusterStreamPlan(HidppUnit unit, byte effectIndex) : base(unit, effectIndex) { }

        /// <summary>A keyboard with 0x8081 beside its 0x8071, and at least
        /// one zone to paint.</summary>
        public static bool Fits(HidppUnit unit)
            => unit.IsKeyboardKind
               && unit.PerKeyFeatureId == HidppUnitProtocol.PerKeyLightingV2 && unit.PerKeyIndex != 0
               && unit.PerKeyMin != 0 && unit.PerKeyMax >= unit.PerKeyMin;

        public override HidppStep[] Paint(int rgb, bool full)
        {
            var steps = new List<HidppStep>(Unit.Zones.Length + 5);
            if (full)
            {
                AddClusters(steps, rgb);
                if (Unit.HostStreamingIndex >= 0)
                    steps.Add(new HidppStep(HidppUnitProtocol.RgbHostStreaming(Device, Effect, (byte)Unit.HostStreamingIndex),
                        HidppStepKind.Select));
                AddFrame(steps, rgb);
            }
            AddFrame(steps, rgb);
            return steps.ToArray();
        }

        public override HidppStep[] KeepAlive(int level, int rgb, bool painted) => Paint(rgb, full: !painted);

        public override byte[] ClaimRead => HidppUnitProtocol.RgbSwControlRead(Device, Effect);

        public override bool ClaimStands(HidppReply reply) => (reply.Param(1) & HidppUnitProtocol.SwControlClusters) != 0;

        public override HidppLightPlan WithoutPerKey => new ClusterPlan(Unit, Effect);

        public override bool PaintsLights => true;

        /// <summary>The whole picture again, so a zone the keyboard never
        /// took is put right within one interval, as OpenRGB's rolling
        /// resync puts it right (RGBController_LogitechHIDPP20.cpp,
        /// DeviceUpdateLEDs). A keyboard whose first paint has not landed
        /// gets that first paint whole, as <see cref="KeepAlive"/> sends it.</summary>
        public override HidppStep[] KeepAliveLights(int level, LightMap map, int[] colors, bool painted)
            => PaintLights(map, colors, null, full: !painted);

        /// <summary>
        /// One color per zone (#557). A first paint after a claim is the
        /// one-color first paint in the color most zones take, its clusters,
        /// Host streaming and its first frame, so the replay the firmware
        /// may need is the frame Logitech's own program sends then. The
        /// whole picture is one frame: a range over every zone in that
        /// color, then the zones of every other color, then the frame end,
        /// so a keep-alive that repaints it never shows the one color
        /// between. A change sends the zones whose color changed, or the
        /// whole picture when that takes fewer frames, and the frame end.
        /// The zones go out as OpenRGB groups them (<see cref="AddZones"/>).
        /// </summary>
        public override HidppStep[] PaintLights(LightMap map, int[] colors, int[] shown, bool full)
        {
            var steps = new List<HidppStep>();
            int most = MostCommon(colors);
            if (full)
            {
                AddClusters(steps, most);
                if (Unit.HostStreamingIndex >= 0)
                    steps.Add(new HidppStep(HidppUnitProtocol.RgbHostStreaming(Device, Effect, (byte)Unit.HostStreamingIndex),
                        HidppStepKind.Select));
                AddFrame(steps, most);
            }
            var whole = new List<HidppStep> { Range(most) };
            var under = new int[colors.Length];
            Array.Fill(under, most);
            AddZones(whole, map, colors, under);
            if (shown != null && !full)
            {
                var changed = new List<HidppStep>();
                AddZones(changed, map, colors, shown);
                if (changed.Count <= whole.Count) whole = changed;
            }
            steps.AddRange(whole);
            steps.Add(new HidppStep(HidppUnitProtocol.PerKeyFrameEnd(Device, Unit.PerKeyIndex), HidppStepKind.Commit));
            return steps.ToArray();
        }

        /// <summary>
        /// The frames for the zones whose color differs from what they show,
        /// grouped as OpenRGB groups a frame's changed zones
        /// (RGBController_LogitechHIDPP20.cpp, DeviceUpdateLEDs), by color.
        /// A color on two zones or more sends each run of three or more
        /// neighboring zones as a range, three ranges to a frame, and its
        /// other zones as one color for up to 13 zones. A color on one zone
        /// alone goes with the other lone ones, four to a frame. Logitech's
        /// own guideline for the feature orders it the same way, ranges
        /// first, then one value for scattered zones, then single zones
        /// (x8081 document, p.3-4).
        /// </summary>
        private void AddZones(List<HidppStep> steps, LightMap map, int[] colors, int[] shown)
        {
            var byColor = new SortedDictionary<int, List<byte>>();
            for (int i = 0; i < map.Lights.Count && i < colors.Length; i++)
            {
                int zone = map.Lights[i].Address;
                if (zone < 1 || zone > 254 || colors[i] == shown[i]) continue;
                if (!byColor.TryGetValue(colors[i], out var zones)) byColor[colors[i]] = zones = new List<byte>();
                zones.Add((byte)zone);
            }
            var lone = new List<(byte Zone, int Rgb)>();
            foreach (var (rgb, zones) in byColor)
            {
                if (zones.Count == 1)
                {
                    lone.Add((zones[0], rgb));
                    continue;
                }
                zones.Sort();
                var ranges = new List<(byte First, byte Last, int Rgb)>();
                var scattered = new List<byte>();
                int start = 0;
                for (int i = 1; i <= zones.Count; i++)
                {
                    if (i < zones.Count && zones[i] == zones[i - 1] + 1) continue;
                    if (i - start >= 3) ranges.Add((zones[start], zones[i - 1], rgb));
                    else for (int j = start; j < i; j++) scattered.Add(zones[j]);
                    start = i;
                }
                for (int i = 0; i < ranges.Count; i += HidppUnitProtocol.PerKeyRangesMax)
                    steps.Add(Stream(HidppUnitProtocol.PerKeyRanges(Device, Unit.PerKeyIndex,
                        ranges.GetRange(i, Math.Min(HidppUnitProtocol.PerKeyRangesMax, ranges.Count - i)))));
                for (int i = 0; i < scattered.Count; i += HidppUnitProtocol.PerKeySingleMax)
                    steps.Add(Stream(HidppUnitProtocol.PerKeySingleValue(Device, Unit.PerKeyIndex, rgb,
                        scattered.GetRange(i, Math.Min(HidppUnitProtocol.PerKeySingleMax, scattered.Count - i)))));
            }
            for (int i = 0; i < lone.Count; i += HidppUnitProtocol.PerKeyIndividualMax)
                steps.Add(Stream(HidppUnitProtocol.PerKeyIndividual(Device, Unit.PerKeyIndex,
                    lone.GetRange(i, Math.Min(HidppUnitProtocol.PerKeyIndividualMax, lone.Count - i)))));
        }

        /// <summary>The color the most lights take, the lowest of those
        /// tied, so the same picture gives the same frames.</summary>
        internal static int MostCommon(int[] colors)
        {
            var counts = new Dictionary<int, int>();
            int best = 0, bestCount = 0;
            foreach (int rgb in colors)
            {
                counts.TryGetValue(rgb, out int count);
                counts[rgb] = ++count;
                if (count > bestCount || (count == bestCount && rgb < best))
                {
                    best = rgb;
                    bestCount = count;
                }
            }
            return best;
        }

        private static HidppStep Stream(byte[] frame) => new(frame, HidppStepKind.Stream);

        /// <summary>One range over every zone.</summary>
        private HidppStep Range(int rgb)
            => Stream(HidppUnitProtocol.PerKeyRange(Device, Unit.PerKeyIndex, Unit.PerKeyMin, Unit.PerKeyMax,
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

        /// <summary>One range over every zone, then the frame end.</summary>
        private void AddFrame(List<HidppStep> steps, int rgb)
        {
            steps.Add(Range(rgb));
            steps.Add(new HidppStep(HidppUnitProtocol.PerKeyFrameEnd(Device, Unit.PerKeyIndex), HidppStepKind.Commit));
        }
    }
}
