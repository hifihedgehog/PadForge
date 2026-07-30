using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v13 permanent guard: the token census.
    ///
    /// <para>The LSTICK_UP regression happened because the token tables
    /// were judged complete by reading the TABLES, not by enumerating
    /// STEAM'S vocabulary. This suite closes that hole from both ends.
    /// The corpus walk extracts every distinct token the committed
    /// fixtures actually carry, per namespace, and the static arrays add
    /// the Steam-side vocabulary harvested (2026-07-17) from the
    /// serializer's own token table in steamclient64.dll and from Valve's
    /// shipped controller_base configs. Every censused token must
    /// translate to either OUTPUT or a NAMED reason. The generic unknown
    /// arms (UnknownXInputButton and friends) may fire only for tokens
    /// OUTSIDE the census, as safety nets.</para>
    ///
    /// <para>When this suite fails on a new fixture, the fix is a
    /// verdict, not a suppression: either the translator lowers the new
    /// token, or it gets a named reasoned skip, or (settings and section
    /// keys only) the disposition table gains the key with a stated
    /// reason.</para>
    ///
    /// <para>The section census (v13, second pass) extends the same
    /// contract from binding tokens to the profile STRUCTURE: every key
    /// at every level of the VDF (top level, top-level settings, group,
    /// input, activator, preset, actions set) observed in the corpus or
    /// in Valve's controller_base templates must be either consumed by
    /// SteamInputConfig.FromVdf / the translator or carry a reasoned
    /// benign disposition below.</para></summary>
    public class VocabularyCensusTests
    {
        // ─── The generic-unknown safety nets ────────────────────────────

        private static readonly HashSet<string> GenericUnknownReasons = new(StringComparer.Ordinal)
        {
            TranslationReasons.UnknownBindingType,
            TranslationReasons.UnknownKey,
            TranslationReasons.UnsupportedKey,
            TranslationReasons.UnknownMouseButton,
            TranslationReasons.UnknownXInputButton,
            TranslationReasons.UnknownGroupMode,
            TranslationReasons.UnknownActivatorType,
            TranslationReasons.UnsupportedControllerAction,
        };

        // ─── Steam-side vocabulary beyond the corpus ────────────────────
        // Harvested from the serializer's contiguous token table in
        // steamclient64.dll (the run RETURN ... RSTICK_RIGHT ...
        // turn_to_face_direction) plus Valve's controller_base templates.
        // Letters A-Z and digits 0-9 are implicit for keys.

        private static readonly string[] SteamXInputTokens =
        {
            "A", "B", "X", "Y",
            "SHOULDER_LEFT", "SHOULDER_RIGHT",
            "TRIGGER_LEFT", "TRIGGER_RIGHT",
            "JOYSTICK_LEFT", "JOYSTICK_RIGHT",
            "START", "SELECT", "STEAM", "GUIDE",
            "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT",
            "LSTICK_UP", "LSTICK_DOWN", "LSTICK_LEFT", "LSTICK_RIGHT",
            "RSTICK_UP", "RSTICK_DOWN", "RSTICK_LEFT", "RSTICK_RIGHT",
        };

        // UNUSED1 is deliberately absent: it is the serializer enum's hole
        // filler between BACKSLASH and SEMICOLON, never a key a config can
        // mean, so it stays with the UnknownKey safety net.
        private static readonly string[] SteamKeyTokens =
        {
            "RETURN", "ENTER", "ESCAPE", "ESC", "BACKSPACE", "TAB", "SPACE",
            "DASH", "EQUALS", "LEFT_BRACKET", "RIGHT_BRACKET", "BACKSLASH", "BACK_SLASH",
            "SEMICOLON", "SINGLE_QUOTE", "BACK_TICK", "COMMA", "PERIOD", "FORWARD_SLASH",
            "CAPSLOCK", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10",
            "F11", "F12", "PRINT_SCREEN", "SCROLL_LOCK", "BREAK", "INSERT", "HOME",
            "PAGE_UP", "DELETE", "END", "PAGE_DOWN",
            "RIGHT_ARROW", "LEFT_ARROW", "DOWN_ARROW", "UP_ARROW",
            "NUM_LOCK", "KEYPAD_FORWARD_SLASH", "KEYPAD_ASTERISK", "KEYPAD_DASH",
            "KEYPAD_PLUS", "KEYPAD_ENTER",
            "KEYPAD_1", "KEYPAD_2", "KEYPAD_3", "KEYPAD_4", "KEYPAD_5",
            "KEYPAD_6", "KEYPAD_7", "KEYPAD_8", "KEYPAD_9", "KEYPAD_0", "KEYPAD_PERIOD",
            "LEFT_ALT", "LEFT_SHIFT", "LEFT_WINDOWS", "LEFT_CONTROL",
            "RIGHT_ALT", "RIGHT_SHIFT", "RIGHT_WINDOWS", "RIGHT_CONTROL",
            "VOLUME_UP", "VOLUME_DOWN", "MUTE", "PLAY", "STOP", "NEXT_TRACK", "PREV_TRACK",
        };

        private static readonly string[] SteamMouseButtonTokens =
        {
            "LEFT", "RIGHT", "MIDDLE", "BACK", "FORWARD",
        };

        private static readonly string[] SteamWheelTokens =
        {
            "SCROLL_UP", "SCROLL_DOWN",
        };

        // Parameterized verbs carry census-shaped args. Everything else is
        // bare. MOUSE_DELTA args from corpus 3456927474.
        private static readonly Dictionary<string, string> SteamControllerActionArgs =
            new(StringComparer.Ordinal)
        {
            ["MOUSE_POSITION"] = "32767 32767",
            ["MOUSE_DELTA"] = "100 0",
            ["SET_LED"] = "255 0 0 100 100 1",
            ["ADD_LAYER"] = "1 0 0",
            ["HOLD_LAYER"] = "1 0 0",
            ["REMOVE_LAYER"] = "1 0 0",
            ["CHANGE_PRESET"] = "1 0 0",
            ["CAMERA_RESET"] = "180 66 90",
        };

        private static readonly string[] SteamControllerActionTokens =
        {
            "MOUSE_POSITION", "MOUSE_DELTA", "SET_LED",
            "ADD_LAYER", "HOLD_LAYER", "REMOVE_LAYER", "CHANGE_PRESET",
            "CAMERA_RESET", "CHANGE_PLAYER_NUMBER",
            "TOGGLE_LIZARD_MODE", "TOGGLE_LIZARD",
            "SCREENSHOT", "SHOW_KEYBOARD",
            "SYSTEM_KEY_0", "SYSTEM_KEY_1",
            "EMPTY_SUB_COMMAND", "EMPTY_BINDING",
            "BRIGHTNESS_UP", "BRIGHTNESS_DOWN", "CONTROLLER_POWEROFF",
            "QUIT_APPLICATION", "TOGGLE_MAGNIFIER", "TOGGLE_RUMBLE",
            "TOGGLE_HAPTICS", "TOGGLE_HUD", "OPEN_CONFIGURATOR",
            "OPEN_QUICKMENU", "FORCE_GUIDE_UP",
            "DOTS_PER_360_CALIBRATION_SPIN", "TURN_TO_FACE_DIRECTION",
            "HOST_POWEROFF", "HOST_SUSPEND", "HOST_RESTART",
            "BIGPICTURE_MINIMIZE", "BIGPICTURE_OPEN", "BIGPICTURE_QUIT",
            "STEAMMUSIC_NEXT", "STEAMMUSIC_PREV", "STEAMMUSIC_PLAYPAUSE",
            "STEAMMUSIC_VOLUP", "STEAMMUSIC_VOLDOWN", "STEAMMUSIC_VOLMUTE",
            "CHORD_HINT_DISPLAY", "CHORD_HINT_DISMISS",
            "GR_MARKER", "GR_TOGGLE", "GR_CLIP",
            "SR_ENABLE", "SR_DISABLE", "SR_TOGGLE_MODE", "SR_STOP_TALK",
            "SR_NEXT_WORD", "SR_PREV_WORD", "SR_NEXT_ITEM", "SR_PREV_ITEM",
            "SR_NEXT_LANDMARK", "SR_PREV_LANDMARK", "SR_NEXT_HEADING", "SR_PREV_HEADING",
            "TS_HOVER", "TS_LC", "TS_RC", "TS_MC", "TS_N", "TS_NONE",
        };

        private static readonly string[] SteamModeTokens =
        {
            "four_buttons", "switches", "dpad", "single_button", "trigger",
            "joystick_move", "joystick_mouse", "joystick_camera", "mouse_joystick",
            "gyro_to_mouse", "flickstick", "absolute_mouse", "relative_mouse",
            "scrollwheel", "touch_menu", "hotbar", "radial_menu", "mouse_region",
            "2dscroll", "disabled", "reference",
        };

        private static readonly string[] SteamActivatorTokens =
        {
            "Full_Press", "Start_Press", "Soft_Press", "Release",
            "Long_Press", "Double_Press",
        };

        // ─── Settings disposition table ─────────────────────────────────
        // Every group-settings key the corpus (fixtures + shipped
        // templates) carries must appear here. Three dispositions:
        // consumed (the translator reads it), named (it lands in a named
        // drop note when present), or benign (deliberately silent, with
        // the reason stated). A new key failing this test needs a verdict.
        private static readonly HashSet<string> KnownGroupSettingKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // consumed
            "curve_exponent", "custom_curve_exponent",
            "deadzone_inner_radius", "deadzone_outer_radius",
            "output_joystick", "output_trigger",
            "sensitivity", "gyro_natural_sensitivity",
            "requires_click", "invert_x", "invert_y",
            "touch_menu_button_count", "touchmenu_button_fire_type",
            "touch_menu_show_labels", "touch_menu_position_x", "touch_menu_position_y",
            "touch_menu_scale", "touch_menu_opacity",
            "scale", "position_x", "position_y",
            "sensitivity_horiz_scale", "sensitivity_vert_scale",
            "referenced_mode",
            "haptic_intensity", "haptic_intensity_override", // per-config aggregate
            // named drops
            "deadzone_shape", "output_curve", "anti_deadzone",
            "rotation", "friction", "mouse_smoothing", "trackball",
            "acceleration", "friction_vert_scale",
            "mouse_dampening_trigger", "mouse_move_threshold",
            "gyro_button", "gyro_ratchet_button_mask", "gyro_button_invert",
            "invert_z",
            // edge_binding_radius/_invert: consumed on stick hosts since
            // v17 (the ring family's radius / inner selector) and on
            // whole-pad trackpad zones (v16); named via the region tuning
            // note on partial trackpad rings.
            "edge_binding_radius", "edge_binding_invert", "transition_time",
            "teleport_start", "teleport_stop",
            // benign: group metadata, not behavior
            "layer",            // ui grouping index of an action layer editor
            "layout",           // configurator ui layout id for the group editor
            "virtual_mode",     // configurator ui sub-style selector
            // benign: engine-default tuning with no channel, where the hosting
            // mode's own approximation note (wheel geometry, double-tap,
            // trigger click) already covers the group
            "deadzone",                        // dpad wedge deadzone, engine wedge default applies
            "deadzone_enable_type",            // deadzone shape selector for the stick mouse read
            "adaptive_threshold",              // trigger soft-pull adaptive threshold
            "analog_emulation_period",         // pwm analog emulation of digital outputs
            "analog_emulation_duty_cycle_pct", // pwm duty cycle sibling
            "button_dist", "button_size",      // diamond touch geometry
            "virtual_cap_size",                // virtual stick cap geometry
            "overlap_region", "overlap",       // dpad wedge overlap geometry
            "doubetap_max_duration",           // steam's spelling of the tap window, engine default
            "edge_spin_velocity",              // wheel edge spin feel, wheel note covers it
            "scroll_angle", "scroll_friction", "scroll_type", "scroll_wrap",
            "scroll_invert",                   // wheel geometry family, wheel note covers it
            "adaptive_centering",              // trackpad stick anchor style, gesture channel is anchor-based
            "hold_repeats", "hold_repeat_inverval", // group-level turbo (steam templates), activator-level lowers
        };

        private static readonly HashSet<string> KnownActivatorSettingKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // consumed (double_tap_time since v17: the DoublePress macro
            // trigger's window; trackpad hosts keep the gesture engine's
            // own tap window)
            "toggle", "hold_repeats", "repeat_rate", "long_press_time",
            "delay_start", "delay_end", "haptic_intensity", "interruptable",
            "double_tap_time",
        };

        // ─── Section disposition tables (v13 section census) ────────────
        // Every KEY at every structural level of the VDF, dispositioned.
        // The corpus walk below enumerates the committed fixtures; the
        // SteamTemplate* arrays add keys observed only in Valve's shipped
        // controller_base templates (54 files harvested 2026-07-17), which
        // are not committed here.

        private static readonly HashSet<string> KnownTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // consumed by SteamInputConfig.FromVdf / the translator
            "version",          // schema gate, v3+ only
            "title",            // profile name via ResolveText
            "description",      // profile description via ResolveText
            "creator",          // CreatorSteamId
            "controller_type",  // label family + trackpad topology
            "localization",     // #token resolution for titles
            "actions",          // set display titles (ActionSetTitles)
            "action_layers",    // layer display titles (ActionSetTitles)
            "group", "preset",
            "settings",         // own table below
            // benign: save provenance, no behavior. revision counters and
            // timestamp are Steam's save bookkeeping; export_type / url /
            // progenitor record where the file came from; controller_caps
            // is the authoring device's capability mask (the config's own
            // groups already say what it uses); game is the template's
            // library display name; touch_layout is the mobile-touch
            // client's on-screen layout blob, no physical input here.
            "revision", "major_revision", "minor_revision", "timestamp",
            "export_type", "url", "progenitor", "controller_caps",
            "game", "touch_layout",
            // benign: pre-2017 version-2 flattened schema (top-level
            // group_source_bindings / switch_bindings, inline group
            // bindings). FromVdf rejects version < 3 before translation,
            // so these never reach the reader. Observed only in the four
            // version-2 controller_base templates.
            "group_source_bindings", "switch_bindings",
        };

        // All four keys ride Steam's client UI, not the mapping. The
        // trackpad mode pair is 0 in the entire corpus AND all of Valve's
        // templates (legacy per-pad default-mode selector, superseded by
        // explicit groups). The cursor pair shows/hides the OS cursor when
        // an action set activates, a client-side flourish with no PadForge
        // channel.
        private static readonly HashSet<string> KnownTopLevelSettingsKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "left_trackpad_mode", "right_trackpad_mode",
            "action_set_trigger_cursor_show", "action_set_trigger_cursor_hide",
        };

        private static readonly HashSet<string> KnownGroupChildKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // consumed
            "id", "mode", "name", "inputs", "settings",
            "gameactions",      // per-preset GameActionsNotSupported count
            // benign: author's prose note on the group, no channel and no
            // behavior (the group NAME is consumed for menu titles)
            "description",
            // benign: version-2 flattened inline bindings, version gate
            // rejects the file first (see top-level table)
            "bindings",
        };

        private static readonly HashSet<string> KnownInputChildKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "activators", "disabled_activators", // both consumed
        };

        private static readonly HashSet<string> KnownActivatorChildKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "bindings", "settings", // both consumed
        };

        private static readonly HashSet<string> KnownPresetChildKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "name", "group_source_bindings", // all consumed
        };

        private static readonly HashSet<string> KnownActionSetChildKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // consumed: the set's display title names the layer the
            // preset becomes (PresetDisplayName)
            "title",
            // benign: Steam Input API set plumbing. legacy_set flags the
            // pre-IGA schema, parent_set_name / set_layer nest layers on
            // Steam's side (PadForge layers are flat), and the button /
            // stickpadgyro bodies DEFINE the in-game actions games read
            // through Steam's API. The bindings that fire them are
            // game_action verbs, which aggregate into the
            // GameActionsNotSupported skip per preset.
            "legacy_set", "parent_set_name", "set_layer",
            "button", "stickpadgyro",
        };

        // Keys observed only in Valve's controller_base templates, merged
        // into the corpus walk the same way the token arrays are.
        private static readonly string[] SteamTemplateTopLevelKeys =
        {
            "game", "touch_layout", "group_source_bindings", "switch_bindings",
        };

        private static readonly string[] SteamTemplateGroupChildKeys =
        {
            "bindings",
        };

        // ─── Corpus census walker ───────────────────────────────────────

        private sealed class Census
        {
            public readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> XInput = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> MouseButtons = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Wheel = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ControllerActions = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> ControllerActionParams = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Modes = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Activators = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> GroupSettings = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ActivatorSettings = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> GyroSettings = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Slots = new(StringComparer.OrdinalIgnoreCase);
        }

        private static Census WalkCorpus()
        {
            var c = new Census();
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(System.IO.File.ReadAllText(path)));

                var gyroGroups = new HashSet<int>();
                foreach (var preset in config.Presets)
                {
                    foreach (var kv in preset.GroupSourceBindings)
                    {
                        var tokens = (kv.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length == 0) continue;
                        c.Slots.Add(tokens[0]);
                        if (tokens[0].Equals("gyro", StringComparison.OrdinalIgnoreCase))
                            gyroGroups.Add(kv.Key);
                    }
                }

                foreach (var group in config.Groups)
                {
                    if (!string.IsNullOrWhiteSpace(group.Mode)) c.Modes.Add(group.Mode.Trim());
                    foreach (var key in group.Settings.Keys)
                    {
                        c.GroupSettings.Add(key);
                        if (gyroGroups.Contains(group.Id)) c.GyroSettings.Add(key);
                    }
                    foreach (var input in group.Inputs.Values)
                    {
                        foreach (var act in input.Activators)
                        {
                            if (!string.IsNullOrWhiteSpace(act.Type)) c.Activators.Add(act.Type.Trim());
                            foreach (var key in act.Settings.Keys) c.ActivatorSettings.Add(key);
                            foreach (var b in act.Bindings)
                            {
                                string type = (b.Type ?? "").Trim();
                                if (type.Length == 0) continue;
                                c.Verbs.Add(type);
                                string first = FirstToken(b.Param);
                                switch (type.ToLowerInvariant())
                                {
                                    case "xinput_button": if (first.Length > 0) c.XInput.Add(first); break;
                                    case "key_press": if (first.Length > 0) c.Keys.Add(first); break;
                                    case "mouse_button": if (first.Length > 0) c.MouseButtons.Add(first); break;
                                    case "mouse_wheel": if (first.Length > 0) c.Wheel.Add(first); break;
                                    case "controller_action":
                                        if (first.Length > 0)
                                        {
                                            c.ControllerActions.Add(first);
                                            if (!c.ControllerActionParams.ContainsKey(first))
                                                c.ControllerActionParams[first] = b.Param;
                                        }
                                        break;
                                }
                            }
                        }
                    }
                }
            }
            return c;
        }

        private static string FirstToken(string s)
        {
            s = (s ?? "").Trim();
            int sp = s.IndexOf(' ');
            return sp < 0 ? s : s.Substring(0, sp);
        }

        // ─── Synthetic translation helpers ──────────────────────────────

        private static TranslatedProfile Translate(string vdf)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 42 });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Census\"\n";

        private static string OneBindingConfig(string binding, string slotToken = "button_diamond",
            string inputName = "button_a", string activator = "Full_Press", string mode = "four_buttons")
            => Head
             + $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"1\"\n\t\t\"mode\"\t\"{mode}\"\n"
             + "\t\t\"inputs\"\n\t\t{\n"
             + $"\t\t\t\"{inputName}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"{activator}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n"
             + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Default\"\n"
             + "\t\t\"group_source_bindings\"\n\t\t{\n"
             + $"\t\t\t\"1\"\t\"{slotToken} active\"\n"
             + "\t\t}\n\t}\n}\n";

        private static void AssertNoGenericUnknown(string binding, TranslatedProfile p)
        {
            var offenders = p.Report.Entries
                .Where(e => GenericUnknownReasons.Contains(e.ReasonKey))
                .Select(e => $"{e.ReasonKey}({string.Join(", ", e.ReasonArgs)})")
                .ToList();
            Assert.True(offenders.Count == 0,
                $"'{binding}' hit a generic-unknown arm: {string.Join("; ", offenders)}. "
                + "Censused tokens need a lowering or a NAMED reasoned skip.");
        }

        // ─── The guards ─────────────────────────────────────────────────

        [Fact]
        public void SerializerXInputFamily_AllResolve()
        {
            // The exact regression the census exists for: Steam's own
            // serializer vocabulary (steamclient64.dll token table) must
            // resolve, token by token, forever.
            foreach (var tok in SteamXInputTokens)
                Assert.True(XInputTargetTable.TryResolve(tok, out _),
                    $"xinput_button {tok} does not resolve");
        }

        [Fact]
        public void XInputTokens_CorpusAndSteam_NeverGenericUnknown()
        {
            var census = WalkCorpus();
            foreach (var tok in census.XInput.Union(SteamXInputTokens, StringComparer.OrdinalIgnoreCase))
            {
                string binding = $"xinput_button {tok}";
                AssertNoGenericUnknown(binding, Translate(OneBindingConfig(binding)));
            }
        }

        [Fact]
        public void KeyTokens_CorpusAndSteam_NeverGenericUnknown()
        {
            var census = WalkCorpus();
            foreach (var tok in census.Keys.Union(SteamKeyTokens, StringComparer.OrdinalIgnoreCase))
            {
                string binding = $"key_press {tok}";
                AssertNoGenericUnknown(binding, Translate(OneBindingConfig(binding)));
            }
        }

        [Fact]
        public void MouseButtonAndWheelTokens_NeverGenericUnknown()
        {
            var census = WalkCorpus();
            foreach (var tok in census.MouseButtons.Union(SteamMouseButtonTokens, StringComparer.OrdinalIgnoreCase))
            {
                string binding = $"mouse_button {tok}";
                AssertNoGenericUnknown(binding, Translate(OneBindingConfig(binding)));
            }
            foreach (var tok in census.Wheel.Union(SteamWheelTokens, StringComparer.OrdinalIgnoreCase))
            {
                string binding = $"mouse_wheel {tok}";
                AssertNoGenericUnknown(binding, Translate(OneBindingConfig(binding)));
            }
        }

        [Fact]
        public void ControllerActionTokens_CorpusAndSteam_NeverGenericUnknown()
        {
            var census = WalkCorpus();
            var tokens = census.ControllerActions
                .Union(SteamControllerActionTokens, StringComparer.OrdinalIgnoreCase);
            foreach (var tok in tokens)
            {
                // Parameterized verbs get census-shaped args. The corpus
                // walker's captured param wins when the fixture carried one.
                string param = census.ControllerActionParams.TryGetValue(tok, out var real)
                    ? real
                    : SteamControllerActionArgs.TryGetValue(tok.ToUpperInvariant(), out var args)
                        ? $"{tok} {args}"
                        : tok;
                string binding = $"controller_action {param}";
                AssertNoGenericUnknown(binding, Translate(OneBindingConfig(binding)));
            }
        }

        [Fact]
        public void BindingVerbs_CorpusUnion_NeverGenericUnknown()
        {
            var census = WalkCorpus();
            var sample = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["key_press"] = "key_press A",
                ["mouse_button"] = "mouse_button LEFT",
                ["mouse_wheel"] = "mouse_wheel SCROLL_UP",
                ["xinput_button"] = "xinput_button A",
                ["controller_action"] = "controller_action SCREENSHOT",
                ["mode_shift"] = "mode_shift joystick 9",
                ["game_action"] = "game_action 1 quicksave 1",
            };
            foreach (var verb in census.Verbs)
            {
                Assert.True(sample.ContainsKey(verb),
                    $"corpus carries binding verb '{verb}' with no census sample; add one");
                AssertNoGenericUnknown(sample[verb], Translate(OneBindingConfig(sample[verb])));
            }
        }

        [Fact]
        public void GroupModes_CorpusAndSteam_NeverUnknownGroupMode()
        {
            var census = WalkCorpus();
            foreach (var mode in census.Modes.Union(SteamModeTokens, StringComparer.OrdinalIgnoreCase))
            {
                // Host on a trackpad: the mode dispatch's unknown arm is
                // mode-based, and a mismatched host produces named skips,
                // never UnknownGroupMode.
                var p = Translate(OneBindingConfig("key_press A",
                    slotToken: "left_trackpad", inputName: "click", mode: mode));
                var offenders = p.Report.Entries
                    .Where(e => e.ReasonKey == TranslationReasons.UnknownGroupMode)
                    .ToList();
                Assert.True(offenders.Count == 0, $"group mode '{mode}' hit UnknownGroupMode");
            }
        }

        [Fact]
        public void ActivatorTypes_CorpusAndSteam_NeverUnknownActivatorType()
        {
            var census = WalkCorpus();
            foreach (var act in census.Activators.Union(SteamActivatorTokens, StringComparer.OrdinalIgnoreCase))
            {
                var p = Translate(OneBindingConfig("key_press A", activator: act));
                var offenders = p.Report.Entries
                    .Where(e => e.ReasonKey == TranslationReasons.UnknownActivatorType)
                    .ToList();
                Assert.True(offenders.Count == 0, $"activator '{act}' hit UnknownActivatorType");
            }
        }

        [Fact]
        public void SlotTokens_Corpus_AllParse()
        {
            var census = WalkCorpus();
            foreach (var slot in census.Slots)
                Assert.True(PhysicalSlotResolver.ParseSlot(slot) != SteamSlot.Unknown,
                    $"slot token '{slot}' parses to Unknown");
        }

        [Fact]
        public void GroupSettingsKeys_Corpus_AllHaveDispositions()
        {
            var census = WalkCorpus();
            var undecided = census.GroupSettings
                .Where(k => !KnownGroupSettingKeys.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            Assert.True(undecided.Count == 0,
                "group settings keys with no disposition (consumed / named / benign): "
                + string.Join(", ", undecided));
        }

        [Fact]
        public void GyroSettingsKeys_Corpus_AllHaveDispositions()
        {
            var census = WalkCorpus();
            var undecided = census.GyroSettings
                .Where(k => !KnownGroupSettingKeys.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            Assert.True(undecided.Count == 0,
                "gyro-hosted settings keys with no disposition: " + string.Join(", ", undecided));
        }

        [Fact]
        public void ActivatorSettingsKeys_Corpus_AllHaveDispositions()
        {
            var census = WalkCorpus();
            var undecided = census.ActivatorSettings
                .Where(k => !KnownActivatorSettingKeys.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            Assert.True(undecided.Count == 0,
                "activator settings keys with no disposition: " + string.Join(", ", undecided));
        }

        // ─── Section census walker (raw VDF, not FromVdf) ───────────────
        // Walks the PARSED documents rather than the typed model, so keys
        // the model ignores are still enumerated. That is the census's
        // whole point: FromVdf cannot vouch for its own coverage.

        private sealed class SectionCensus
        {
            public readonly HashSet<string> TopLevel = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> TopLevelSettings = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> GroupChildren = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> InputChildren = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ActivatorChildren = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> PresetChildren = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ActionSetChildren = new(StringComparer.OrdinalIgnoreCase);
        }

        private static SectionCensus WalkSections()
        {
            var c = new SectionCensus();
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var root = VdfParser.Parse(System.IO.File.ReadAllText(path));
                var mappings = root["controller_mappings"];
                if (mappings.IsMissing) mappings = root;

                foreach (var kv in mappings.Children) c.TopLevel.Add(kv.Key);
                foreach (var kv in mappings["settings"].Children) c.TopLevelSettings.Add(kv.Key);

                foreach (var group in mappings.Multi("group"))
                {
                    foreach (var kv in group.Children) c.GroupChildren.Add(kv.Key);
                    foreach (var input in group["inputs"].Children)
                    {
                        foreach (var kv in input.Value.Children) c.InputChildren.Add(kv.Key);
                        foreach (var act in input.Value["activators"].Children)
                            foreach (var kv in act.Value.Children)
                                c.ActivatorChildren.Add(kv.Key);
                    }
                }

                foreach (var preset in mappings.Multi("preset"))
                    foreach (var kv in preset.Children)
                        c.PresetChildren.Add(kv.Key);

                foreach (var blockName in new[] { "actions", "action_layers" })
                    foreach (var block in mappings.Multi(blockName))
                        foreach (var set in block.Children)
                            foreach (var kv in set.Value.Children)
                                c.ActionSetChildren.Add(kv.Key);
            }
            return c;
        }

        private static void AssertDispositioned(IEnumerable<string> observed,
            HashSet<string> table, string level)
        {
            var undecided = observed
                .Where(k => !table.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            Assert.True(undecided.Count == 0,
                $"{level} keys with no disposition (consumed / benign): "
                + string.Join(", ", undecided));
        }

        // ─── The section guards ─────────────────────────────────────────

        [Fact]
        public void TopLevelKeys_CorpusAndTemplates_AllHaveDispositions()
        {
            var census = WalkSections();
            AssertDispositioned(
                census.TopLevel.Union(SteamTemplateTopLevelKeys, StringComparer.OrdinalIgnoreCase),
                KnownTopLevelKeys, "top-level");
        }

        [Fact]
        public void TopLevelSettingsKeys_Corpus_AllHaveDispositions()
        {
            AssertDispositioned(WalkSections().TopLevelSettings,
                KnownTopLevelSettingsKeys, "top-level settings");
        }

        [Fact]
        public void GroupChildKeys_CorpusAndTemplates_AllHaveDispositions()
        {
            var census = WalkSections();
            AssertDispositioned(
                census.GroupChildren.Union(SteamTemplateGroupChildKeys, StringComparer.OrdinalIgnoreCase),
                KnownGroupChildKeys, "group");
        }

        [Fact]
        public void InputActivatorPresetActionSetKeys_Corpus_AllHaveDispositions()
        {
            var census = WalkSections();
            AssertDispositioned(census.InputChildren, KnownInputChildKeys, "input");
            AssertDispositioned(census.ActivatorChildren, KnownActivatorChildKeys, "activator");
            AssertDispositioned(census.PresetChildren, KnownPresetChildKeys, "preset");
            AssertDispositioned(census.ActionSetChildren, KnownActionSetChildKeys, "actions set");
        }

        // ─── The two consumptions the section census added ──────────────

        [Fact]
        public void PresetName_ResolvesThroughActionSetTitle()
        {
            // Community layer (3451446931): action_layers set
            // Preset_1000001 carries the author's title "Secondary". The
            // layer's user-facing name must be the title, not the token.
            var config = SteamInputConfig.FromVdf(
                VdfParser.Parse(TestFixtures.Read(3451446931)));
            var p = new ConfigTranslator().Translate(config,
                new TranslationOptions { FileId = 3451446931 });
            Assert.Contains(
                p.XboxMappingSet.ShiftActivators.Concat(p.KbmMappingSet.ShiftActivators),
                a => a.LayerName == "Secondary");
        }

        [Fact]
        public void PresetName_ResolvesLocalizedHashTitle()
        {
            // Valve's XCOM 2 config (1129670518): actions set
            // TacticalControls titles itself #Set_TacticalControls, which
            // the config's own english localization renders "Tactical".
            var config = SteamInputConfig.FromVdf(
                VdfParser.Parse(TestFixtures.Read(1129670518)));
            var p = new ConfigTranslator().Translate(config,
                new TranslationOptions { FileId = 1129670518 });
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GameActionsNotSupported
                && e.SourcePath == "Tactical");
        }

        [Fact]
        public void GroupGameActions_CountIntoPresetSkip()
        {
            // A group whose gameactions block links an in-game analog
            // action carries no game_action binding for the motion, yet
            // the linkage is just as Steam-only. It must feed the same
            // aggregate skip. Shape from Valve's XCOM 2 config.
            string vdf = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
                + "\t\"group\"\n\t{\n\t\t\"id\"\t\"1\"\n\t\t\"mode\"\t\"joystick_camera\"\n"
                + "\t\t\"gameactions\"\n\t\t{\n\t\t\t\"TacticalControls\"\t\"TacticalCamera\"\n\t\t}\n"
                + "\t\t\"inputs\"\n\t\t{\n\t\t}\n\t}\n"
                + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"TacticalControls\"\n"
                + "\t\t\"group_source_bindings\"\n\t\t{\n\t\t\t\"1\"\t\"joystick active\"\n\t\t}\n\t}\n}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GameActionsNotSupported);
            Assert.Equal("1", Assert.Single(entry.ReasonArgs));
        }
    }
}
