using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The three Valve wire families: the Steam Deck, the 2015 Steam
    /// Controller and the 2026 Steam Controller.
    ///
    /// <para>These profiles declare almost nothing in their HID descriptors
    /// (0 axes / 0 buttons on the 2015 pad and the Deck persona, 3 / 3 on
    /// the 2026 pad) because the real shape of each device lives in its
    /// extended report. The mapping grid used to size itself from those
    /// declarations and came up empty. It now sizes from the canonical wire
    /// table, and the frame packer resolves every slot through the same
    /// table, so a slot cannot mean one control in the grid and another on
    /// the wire.</para>
    ///
    /// <para>The packer test is the one that matters: it reads HIDMaestro's
    /// own extended-report spec for each composite persona off the SDK on
    /// disk, and for every button bit and axis field the spec names, sets
    /// that role's raw slot, packs, and asserts the byte. That is the
    /// consuming side's definition, not this repo's transcription of it.
    /// The 2026 bits HIDMaestro leaves unnamed are asserted against
    /// sc2-research's TritonButtons table, which is SDL3's
    /// controller_structs.h verbatim.</para>
    /// </summary>
    public class ValveWireTests
    {
        public static readonly string[] ValveIds =
            { "steam-deck", "steam-deck-composite", "steam-controller", "steam-controller-composite", "steam-controller-2" };

        // ── wire tables ────────────────────────────────────────────────

        [Theory]
        [InlineData("steam-deck", NintendoPreviewMap.Family.SteamDeck)]
        [InlineData("steam-deck-composite", NintendoPreviewMap.Family.SteamDeck)]
        [InlineData("steam-controller", NintendoPreviewMap.Family.SteamController)]
        [InlineData("steam-controller-composite", NintendoPreviewMap.Family.SteamController)]
        [InlineData("steam-controller-2", NintendoPreviewMap.Family.SteamController2)]
        [InlineData("switch-pro", NintendoPreviewMap.Family.SwitchPro)]
        [InlineData("switch2-pro-controller", NintendoPreviewMap.Family.Switch2Pro)]
        [InlineData("padforge-custom", NintendoPreviewMap.Family.None)]
        [InlineData("dualsense-composite", NintendoPreviewMap.Family.None)]
        [InlineData(null, NintendoPreviewMap.Family.None)]
        public void FamilyOf_ResolvesEveryIdPrefixInTheRightOrder(string id, NintendoPreviewMap.Family want)
            => Assert.Equal(want, NintendoPreviewMap.FamilyOf(id));

        /// <summary>Each family's table is a bijection: no role twice, and
        /// every raw index round-trips through ToRaw / ToPreview.</summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void ButtonTable_RoundTrips(string id)
        {
            var table = NintendoPreviewMap.ButtonTable(id);
            Assert.Equal(table.Length, table.Distinct().Count());
            for (int i = 0; i < table.Length; i++)
            {
                Assert.Equal($"RawBtn{i}", NintendoPreviewMap.ToRaw(table[i], id));
                Assert.Equal(table[i], NintendoPreviewMap.ToPreview($"RawBtn{i}", id));
            }
        }

        /// <summary>Valve axes interleave the analog triggers, [LX LY LT RX
        /// RY RT], which is what ComputeAxisLayout produces for two sticks
        /// and two triggers and what every packer assumes.</summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void AxisTable_IsTheInterleavedValveLayout(string id)
        {
            Assert.Equal("RawAxis0", NintendoPreviewMap.ToRaw("LeftThumbAxisX", id));
            Assert.Equal("RawAxis1", NintendoPreviewMap.ToRaw("LeftThumbAxisY", id));
            Assert.Equal("RawAxis2", NintendoPreviewMap.ToRaw("LeftTrigger", id));
            Assert.Equal("RawAxis3", NintendoPreviewMap.ToRaw("RightThumbAxisX", id));
            Assert.Equal("RawAxis4", NintendoPreviewMap.ToRaw("RightThumbAxisY", id));
            Assert.Equal("RawAxis5", NintendoPreviewMap.ToRaw("RightTrigger", id));
            Assert.Equal("RightThumbAxisYNeg", NintendoPreviewMap.ToPreview("RawAxis4Neg", id));

            var cfg = new ExtendedSlotConfig { ThumbstickCount = 2, TriggerCount = 2 };
            cfg.ComputeAxisLayout(out var sx, out var sy, out var tr);
            Assert.Equal(new[] { 0, 3 }, sx);
            Assert.Equal(new[] { 1, 4 }, sy);
            Assert.Equal(new[] { 2, 5 }, tr);
        }

        /// <summary>A Nintendo pad still packs its sticks at 0..3 with no
        /// trigger between them: the Valve layout must not leak.</summary>
        [Fact]
        public void NintendoAxisTable_IsUnchanged()
        {
            Assert.Equal("RawAxis2", NintendoPreviewMap.ToRaw("RightThumbAxisX", "switch-pro"));
            Assert.Equal("RawBtn6", NintendoPreviewMap.ToRaw("LeftTrigger", "switch-pro"));
        }

        /// <summary>D-pad encoding per family: hat on the Deck and the 2015
        /// pad, four real buttons on the 2026 pad.</summary>
        [Theory]
        [InlineData("steam-deck-composite", true)]
        [InlineData("steam-controller-composite", true)]
        [InlineData("steam-controller-2", false)]
        public void DPad_EncodingFollowsTheWire(string id, bool hat)
        {
            Assert.Equal(hat, NintendoPreviewMap.DPadIsHat(id));
            string raw = NintendoPreviewMap.ToRaw("DPadLeft", id);
            Assert.Equal(hat, raw == "RawPov0Left");
            if (!hat) Assert.StartsWith("RawBtn", raw);
        }

        /// <summary>The controls each pad has, and the ones it does not. A
        /// role a pad lacks resolves to nothing rather than to wire that is
        /// not there.</summary>
        [Fact]
        public void EachPadHasItsOwnControls()
        {
            // 2015: one stick, two grips, no right stick click, no QAM.
            Assert.True(NintendoPreviewMap.IndexOf("steam-controller", "LeftGrip") >= 0);
            Assert.True(NintendoPreviewMap.IndexOf("steam-controller", "RightThumbButton") < 0);
            Assert.True(NintendoPreviewMap.IndexOf("steam-controller", "ButtonQuickAccess") < 0);
            Assert.True(NintendoPreviewMap.IndexOf("steam-controller", "Paddle1") < 0);
            // Deck and 2026: two sticks, four rear buttons, QAM, no grips.
            foreach (var id in new[] { "steam-deck-composite", "steam-controller-2" })
            {
                Assert.True(NintendoPreviewMap.IndexOf(id, "RightThumbButton") >= 0);
                Assert.True(NintendoPreviewMap.IndexOf(id, "ButtonQuickAccess") >= 0);
                Assert.True(NintendoPreviewMap.IndexOf(id, "Paddle4") >= 0);
                Assert.True(NintendoPreviewMap.IndexOf(id, "LeftGrip") < 0);
            }
        }

        /// <summary>Switching profile across families rewrites bindings by
        /// role and drops the ones the target lacks.</summary>
        [Fact]
        public void TranslateRawTarget_PreservesRolesAcrossValveFamilies()
        {
            string qamOnDeck = NintendoPreviewMap.ToRaw("ButtonQuickAccess", "steam-deck-composite");
            Assert.Equal(NintendoPreviewMap.ToRaw("ButtonQuickAccess", "steam-controller-2"),
                NintendoPreviewMap.TranslateRawTarget(qamOnDeck, "steam-deck-composite", "steam-controller-2"));
            Assert.Null(NintendoPreviewMap.TranslateRawTarget(qamOnDeck, "steam-deck-composite", "steam-controller"));
            // Axes cross families by role too: Nintendo RX is RawAxis2, Valve RX is RawAxis3.
            Assert.Equal("RawAxis3", NintendoPreviewMap.TranslateRawTarget("RawAxis2", "switch-pro", "steam-controller-2"));
            // Same family: untouched.
            Assert.Equal("RawBtn12", NintendoPreviewMap.TranslateRawTarget("RawBtn12", "steam-deck", "steam-deck-composite"));
        }

        // ── labels ─────────────────────────────────────────────────────

        /// <summary>No Valve row is a numbered "Button N". Every raw index
        /// on every Valve wire carries the control's own name.</summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void EveryValveRowIsNamed(string id)
        {
            Assert.True(MacroButtonNames.IsValveLetteredProfile(id));
            Assert.True(MacroButtonNames.IsLetteredProfile(id));
            int n = NintendoPreviewMap.ButtonCount(id);
            for (int i = 1; i <= n; i++)
            {
                string label = MacroButtonNames.RawButtonLabel(id, i);
                Assert.False(string.IsNullOrWhiteSpace(label), $"{id} row {i} has no label");
                Assert.DoesNotContain("Button ", label);
            }
        }

        [Fact]
        public void ValveLabels_AreTheControlsOwnNames()
        {
            string L(string id, string role) => MacroButtonNames.RawButtonLabel(id, NintendoPreviewMap.IndexOf(id, role) + 1);
            Assert.Equal("Steam", L("steam-controller-2", "ButtonGuide"));
            Assert.Equal("Quick Access", L("steam-controller-2", "ButtonQuickAccess"));
            Assert.Equal("R4", L("steam-controller-2", "Paddle1"));
            Assert.Equal("L5", L("steam-deck-composite", "Paddle4"));
            Assert.Equal("Left Grip", L("steam-controller", "LeftGrip"));
            Assert.Equal("Left Pad Click", L("steam-controller", "LeftTouchpadClick"));
            Assert.Equal("View", L("steam-deck-composite", "ButtonBack"));
            Assert.Equal("Back", L("steam-controller", "ButtonBack"));
        }

        // ── packers against HIDMaestro's own spec ──────────────────────

        private static object Prop(object v, string name) =>
            v?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(v);

        /// <summary>HIDMaestro's spec names buttons in its own vocabulary.
        /// The role each name means on a given wire, from the spec's own
        /// bit positions cross-read against SDL: "Touchpad" is the left pad
        /// click on the Deck (bit 17 = LeftPadClick in SDL_hidapi_steamdeck.c)
        /// and the right pad click on the 2015 pad (bit 18 =
        /// STEAM_BUTTON_RIGHTPAD_CLICKED_MASK) and the 2026 pad (bit 22 =
        /// RPad_Click); "LeftPaddle" / "RightPaddle" are L5 / R5 on the Deck
        /// and 2026 pad (their bits are the PADDLE2 bits) and the grips on
        /// the 2015 pad.</summary>
        private static string RoleFor(string specName, string id)
        {
            var fam = NintendoPreviewMap.FamilyOf(id);
            bool sc15 = fam == NintendoPreviewMap.Family.SteamController;
            return specName switch
            {
                "A" => "ButtonA", "B" => "ButtonB", "X" => "ButtonX", "Y" => "ButtonY",
                "LeftBumper" => "LeftShoulder", "RightBumper" => "RightShoulder",
                "Back" => "ButtonBack", "Start" => "ButtonStart", "Guide" => "ButtonGuide",
                "Misc1" => "ButtonQuickAccess",
                "LeftStick" => "LeftThumbButton", "RightStick" => "RightThumbButton",
                "LeftPaddle" => sc15 ? "LeftGrip" : "Paddle4",
                "RightPaddle" => sc15 ? "RightGrip" : "Paddle3",
                "Touchpad" => fam == NintendoPreviewMap.Family.SteamDeck ? "LeftTouchpadClick" : "RightTouchpadClick",
                "DPAD_UP" => "DPadUp", "DPAD_DOWN" => "DPadDown", "DPAD_LEFT" => "DPadLeft", "DPAD_RIGHT" => "DPadRight",
                _ => null,   // trigger digitals and pad touches are derived, not slots
            };
        }

        private static byte[] PackFor(string id, Action<RawHidState> arrange = null, TouchpadState tp = default)
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Povs[0] = -1;
            // A trigger rests at short.MinValue, not at zero: zero is the
            // middle of its travel. Step 3 writes that every frame, so a
            // fixture that leaves the slots at zero is not a rest frame.
            raw.Axes[2] = raw.Axes[5] = short.MinValue;
            arrange?.Invoke(raw);
            var dest = new byte[ValveReportPackers.MaxReportSize];
            ValveReportPackers.ForProfile(id).Pack(raw, tp, default, 1, dest);
            return dest;
        }

        private static void SetRole(RawHidState raw, string id, string role, bool on)
        {
            if (role.StartsWith("DPad") && NintendoPreviewMap.DPadIsHat(id))
            {
                raw.Povs[0] = role switch { "DPadUp" => 0, "DPadRight" => 9000, "DPadDown" => 18000, _ => 27000 };
                return;
            }
            int i = NintendoPreviewMap.IndexOf(id, role);
            Assert.True(i >= 0, $"{id} has no slot for {role}");
            raw.SetButton(i, on);
        }

        /// <summary>The profile, catalog first and HIDMaestro's own context
        /// second. The packer contract holds whether or not the catalog
        /// offers the profile, and the 2026 Steam Controller is held back
        /// from the pickers while its descriptor leads with a mouse (see
        /// HMaestroProfileCatalog.LeadsWithAPointingReport). Its frame
        /// layout still has to be right for the day that lifts.</summary>
        private static HMProfile ProfileById(string id)
        {
            var p = HMaestroProfileCatalog.AllProfiles.FirstOrDefault(x => x.Id == id);
            if (p != null) return p;
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            return ctx.AllProfiles.First(x => x.Id == id);
        }

        /// <summary>For every button HIDMaestro names in the persona's
        /// extended-report spec, the packer sets exactly that bit at exactly
        /// that byte when the role's raw slot is pressed. Bits the spec
        /// leaves unnamed ("_") are skipped here and covered below.</summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void Packer_PutsEveryNamedSpecButtonOnItsBit(string id)
        {
            var profile = ProfileById(id);
            var fields = (IEnumerable)Prop(profile.ExtendedReport, "Fields");
            int checkedBits = 0;
            foreach (var f in fields)
            {
                if ((string)Prop(f, "Type") != "button-mask") continue;
                int baseByte = Convert.ToInt32(Prop(f, "Byte"));
                var names = ((IEnumerable)Prop(f, "Buttons")).Cast<object>().Select(o => o.ToString()).ToArray();
                for (int bit = 0; bit < names.Length; bit++)
                {
                    string role = RoleFor(names[bit], id);
                    if (role == null) continue;
                    var b = PackFor(id, r => SetRole(r, id, role, true));
                    int at = baseByte + bit / 8;
                    int mask = 1 << (bit % 8);
                    Assert.True((b[at] & mask) != 0,
                        $"{id}: spec button '{names[bit]}' (bit {bit}) as role {role} did not set byte {at} mask 0x{mask:X2}");
                    // and nothing else in the mask field lit
                    var clean = PackFor(id);
                    for (int k = 0; k < (names.Length + 7) / 8; k++)
                        Assert.Equal(0, clean[baseByte + k]);
                    checkedBits++;
                }
            }
            Assert.True(checkedBits >= 15, $"{id}: only {checkedBits} named bits found in the spec");
        }

        /// <summary>Every axis field the spec names lands at the spec's
        /// byte offset. Sticks and triggers come from the raw axes, pad
        /// coordinates from the touch surface.</summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void Packer_PutsEveryNamedSpecAxisAtItsOffset(string id)
        {
            var profile = ProfileById(id);
            var fields = (IEnumerable)Prop(profile.ExtendedReport, "Fields");
            var offsets = new Dictionary<string, int>();
            foreach (var f in fields)
            {
                string sem = Prop(f, "Semantic")?.ToString();
                string type = Prop(f, "Type")?.ToString() ?? "";
                if (string.IsNullOrEmpty(sem) || !(type.StartsWith("int16") || type.StartsWith("uint16"))) continue;
                offsets[sem] = Convert.ToInt32(Prop(f, "Byte"));   // last wins: the 16-bit trigger copy on the 2015 pad
            }
            short I16(byte[] b, int off) => (short)(b[off] | (b[off + 1] << 8));

            var sticks = PackFor(id, r => { r.Axes[0] = 1000; r.Axes[1] = 2000; r.Axes[3] = 3000; r.Axes[4] = 4000; r.Axes[2] = 5000; r.Axes[5] = 6000; });
            if (offsets.TryGetValue("leftStickX", out int o)) Assert.Equal(1000, I16(sticks, o));
            if (offsets.TryGetValue("leftStickY", out o)) Assert.Equal(-2000, I16(sticks, o));   // HID down to wire up
            if (offsets.TryGetValue("rightStickX", out o)) Assert.Equal(3000, I16(sticks, o));
            if (offsets.TryGetValue("rightStickY", out o)) Assert.Equal(-4000, I16(sticks, o));
            // Triggers are rescaled on the way out, not copied. The raw
            // surface stores one BIPOLAR, rest at short.MinValue, and every
            // Valve wire carries it UNSIGNED 0 to 32767, which is what SDL
            // decodes back with * 2 - 32768.
            Assert.True(offsets.ContainsKey("leftTrigger") && offsets.ContainsKey("rightTrigger"), id);
            Assert.Equal((5000 + 32768) / 2, I16(sticks, offsets["leftTrigger"]));
            Assert.Equal((6000 + 32768) / 2, I16(sticks, offsets["rightTrigger"]));

            var pads = PackFor(id, tp: new TouchpadState { Down0 = true, X0 = 1f, Y0 = 0.5f, Down1 = true, X1 = 0f, Y1 = 0.5f });
            Assert.Equal(short.MaxValue, I16(pads, offsets["leftPadX"]));
            Assert.Equal(0, I16(pads, offsets["leftPadY"]));
            Assert.Equal(short.MinValue + 1, I16(pads, offsets["rightPadX"]));
        }

        /// <summary>THE PROPERTY for a trigger on the wire: rest reads zero,
        /// full pull reads the top of the range, and half pull reads half.
        ///
        /// <para>The middle is the whole point. The raw surface stores a
        /// trigger BIPOLAR, rest at short.MinValue and full pull at
        /// short.MaxValue, while every Valve wire carries it UNSIGNED 0 to
        /// 32767 (SDL_hidapi_steam.c 1645, _steamdeck.c 234,
        /// _steam_triton.c 222, each decoding back with * 2 - 32768). These
        /// packers CLAMPED to [0, 32767] instead of rescaling, so both ends
        /// came out right by luck and everything between did not: the lower
        /// half of the travel read as zero and the upper half swept the
        /// entire range.</para></summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void Packer_ScalesATriggerFromRestNotFromCenter(string id)
        {
            var profile = ProfileById(id);
            var fields = (IEnumerable)Prop(profile.ExtendedReport, "Fields");
            int off = -1;
            foreach (var f in fields)
            {
                string sem = Prop(f, "Semantic")?.ToString();
                string type = Prop(f, "Type")?.ToString() ?? "";
                if (sem == "leftTrigger" && (type.StartsWith("int16") || type.StartsWith("uint16")))
                    off = Convert.ToInt32(Prop(f, "Byte"));
            }
            Assert.True(off >= 0, id);
            short Read(short rawTrigger)
            {
                var b = PackFor(id, r => r.Axes[2] = rawTrigger);
                return (short)(b[off] | (b[off + 1] << 8));
            }

            Assert.Equal(0, Read(short.MinValue));                    // at rest
            Assert.Equal(short.MaxValue, Read(short.MaxValue));       // fully pulled
            // Half pull is the case a clamp gets wrong: it read zero.
            short half = Read(0);
            Assert.InRange(half, 16000, 16768);
            // And it climbs all the way, rather than sitting at zero until
            // the trigger is half down.
            Assert.True(Read(-16384) > 3000, $"quarter pull read {Read(-16384)}");
        }

        /// <summary>The 2015 pad's right trackpad rides the right-stick axes
        /// when no finger is on the surface, with the finger-down bit set so
        /// Steam sees pad input. SDL maps that pad to the right stick.</summary>
        [Fact]
        public void SteamController2015_RightPadFollowsTheRightStickAxes()
        {
            var b = PackFor("steam-controller-composite", r => { r.Axes[3] = 1234; r.Axes[4] = -777; });
            short I16(int off) => (short)(b[off] | (b[off + 1] << 8));
            Assert.Equal(1234, I16(20));
            Assert.Equal(777, I16(22));
            Assert.NotEqual(0, b[10] & 0x10);   // STEAM_RIGHTPAD_FINGERDOWN_MASK, bit 20
        }

        /// <summary>The 2015 frame header is ValveInReport type 1 length 60,
        /// and the plain umdf2 profile packs the identical frame: its vendor
        /// descriptor is the wired controller's own 64-byte report.</summary>
        [Fact]
        public void SteamController2015_BothProfilesShareTheWiredFrame()
        {
            var a = PackFor("steam-controller", r => r.SetButton(NintendoPreviewMap.IndexOf("steam-controller", "ButtonA"), true));
            var c = PackFor("steam-controller-composite", r => r.SetButton(NintendoPreviewMap.IndexOf("steam-controller-composite", "ButtonA"), true));
            Assert.Equal(a, c);
            Assert.Equal(new byte[] { 0x01, 0x00, 0x01, 0x3C }, a.Take(4));
            Assert.NotEqual(0, a[8] & 0x80);   // STEAM_BUTTON_SOUTH_MASK, bit 7 = A
        }

        /// <summary>The 2026 bits HIDMaestro's spec leaves unnamed, from
        /// sc2-research's TritonButtons table (SDL3 controller_structs.h
        /// verbatim, checked against 9k captured frames): R4 bit 7, L4 bit
        /// 17, and the left pad click at bit 26.</summary>
        [Theory]
        [InlineData("Paddle1", 7)]
        [InlineData("Paddle2", 17)]
        [InlineData("Paddle3", 8)]
        [InlineData("Paddle4", 18)]
        [InlineData("LeftTouchpadClick", 26)]
        [InlineData("RightTouchpadClick", 22)]
        [InlineData("ButtonQuickAccess", 4)]
        [InlineData("ButtonGuide", 16)]
        public void SteamController2026_UnnamedBitsFollowSdl(string role, int bit)
        {
            const string id = "steam-controller-2";
            var b = PackFor(id, r => r.SetButton(NintendoPreviewMap.IndexOf(id, role), true));
            uint bits = (uint)(b[2] | (b[3] << 8) | (b[4] << 16) | (b[5] << 24));
            Assert.Equal(1u << bit, bits);
            Assert.Equal(0x42, b[0]);
        }

        // ── grid ───────────────────────────────────────────────────────

        /// <summary>The Extended config counts a Valve profile sizes the
        /// grid from: two sticks, two triggers, a hat where the wire has
        /// one, and the whole wire table's worth of named buttons.</summary>
        [Theory]
        [InlineData("steam-deck-composite", 2, 2, 1, 18)]
        [InlineData("steam-controller", 2, 2, 1, 14)]
        [InlineData("steam-controller-composite", 2, 2, 1, 14)]
        [InlineData("steam-controller-2", 2, 2, 0, 22)]
        public void WireTable_SizesTheRawSurface(string id, int sticks, int triggers, int povs, int buttons)
        {
            Assert.Equal(sticks, NintendoPreviewMap.StickCount(id));
            Assert.Equal(triggers, NintendoPreviewMap.TriggerCount(id));
            Assert.Equal(povs, NintendoPreviewMap.DPadIsHat(id) ? 1 : 0);
            Assert.Equal(buttons, NintendoPreviewMap.ButtonCount(id));
        }
    }
}
