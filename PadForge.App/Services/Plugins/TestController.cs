using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using PadForge.Engine;
using PadForge.Resources.Strings;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// What the Test Controller is set to (#600 item 19), read once from
    /// its declared settings whenever one changes, so the poll thread reads
    /// one reference and takes no lock.
    /// </summary>
    internal sealed record TestControllerConfig
    {
        public bool MoveSticks { get; init; } = true;
        public bool MoveTriggers { get; init; } = true;
        public bool Circle { get; init; }
        public int MoveMs { get; init; } = 4000;
        public int RestMs { get; init; } = 2000;
        public bool PressButtons { get; init; } = true;
        public bool PressNavigation { get; init; }
        public bool TurnDpad { get; init; }
        public int PressMs { get; init; } = 250;
        public int Controllers { get; init; } = 1;
        public bool ByHand { get; init; }

        /// <summary>Under By Hand: each stick's position, X then Y, -100 to
        /// 100 with Y growing downward.</summary>
        public (double X, double Y) LeftStick { get; init; }
        public (double X, double Y) RightStick { get; init; }

        /// <summary>Under By Hand: each trigger, 0 to 100.</summary>
        public double LeftTrigger { get; init; }
        public double RightTrigger { get; init; }

        /// <summary>Under By Hand: the buttons held, by the device's button
        /// index.</summary>
        public bool[] Buttons { get; init; } = new bool[TestControllerPattern.ButtonNames.Length];
    }

    /// <summary>
    /// The Test Controller (#600 item 19): a controller that moves by itself
    /// so a mapping can be tried with no hardware, its settings declared as
    /// a plugin declares its own (item 17) and drawn by the same templates.
    ///
    /// <para>Its motion: the sticks and triggers move for the Move Time and
    /// rest for the Rest Time, one button at a time is held for
    /// the Press Time, and the D-pad turns once round while they move. The
    /// navigation buttons (A, B, Back, Start and Guide) and the D-pad start
    /// off. Windows apps act on a virtual Xbox controller's A, B, Back,
    /// Start and D-pad in the window in front (Microsoft's guide to gamepad
    /// interactions, 2026-07-08), and its Guide button opens Xbox Game Bar
    /// (a Microsoft Q&amp;A answer of 2026-05-16).</para>
    /// </summary>
    internal static class TestControllerPattern
    {
        /// <summary>The standard gamepad slots it has, in SDL's layout as
        /// PadForge reads it (SettingsManager.CreateDefaultPadSetting): the
        /// eleven buttons, the six axes and the D-pad.</summary>
        internal static readonly string[] ButtonNames =
            { "A", "B", "X", "Y", "Left Shoulder", "Right Shoulder", "Back", "Start", "Left Stick Button", "Right Stick Button", "Guide" };
        internal static readonly string[] AxisNames =
            { "Left X", "Left Y", "Left Trigger", "Right X", "Right Y", "Right Trigger" };

        /// <summary>The buttons a Windows app acts on in the window in front,
        /// and Guide, which opens Xbox Game Bar: A, B, Back, Start,
        /// Guide.</summary>
        internal static readonly int[] NavigationButtons = { 0, 1, 6, 7, 10 };

        /// <summary>The buttons pressed with the navigation buttons off: X,
        /// Y, the shoulders and the stick buttons.</summary>
        internal static readonly int[] PlainButtons = { 2, 3, 4, 5, 8, 9 };

        /// <summary>Every button in turn: the plain ones and the navigation
        /// ones in the device's order.</summary>
        internal static readonly int[] AllButtons = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        internal const int Center = 32768;
        internal const int LeftTrigger = 2, RightTrigger = 5;
        internal const int MostControllers = 4;

        /// <summary>The device's shape: a standard gamepad's, reported as a
        /// gamepad so it is read and auto-mapped one to one like a real
        /// pad.</summary>
        internal static ProgramDeviceShape Shape()
        {
            var buttons = new (int, string)[ButtonNames.Length];
            for (int i = 0; i < buttons.Length; i++) buttons[i] = (i, ButtonNames[i]);
            var axes = new ProgramAxis[AxisNames.Length];
            for (int i = 0; i < axes.Length; i++)
                axes[i] = new ProgramAxis(i, AxisNames[i], i is LeftTrigger or RightTrigger ? 0 : Center);
            return new ProgramDeviceShape
            {
                Buttons = buttons,
                Axes = axes,
                Hats = new[] { "D-Pad" },
                DeviceType = InputDeviceType.Gamepad,
            };
        }

        /// <summary>Everything at rest: sticks centered, triggers out,
        /// buttons up, the hat centered.</summary>
        internal static CustomInputState Rest()
        {
            var s = new CustomInputState();
            Fill(s, null, 0);
            return s;
        }

        /// <summary>
        /// Writes the state at <paramref name="ms"/> into
        /// <paramref name="s"/>, which is reused: every control it has is
        /// written. A pure function of the settings and the time, so the
        /// same time gives the same state. Null settings give rest.
        /// </summary>
        internal static void Fill(CustomInputState s, TestControllerConfig c, long ms)
        {
            Array.Clear(s.Buttons);
            for (int i = 0; i < s.Povs.Length; i++) s.Povs[i] = -1;
            for (int i = 0; i < s.Axis.Length; i++) s.Axis[i] = 0;
            s.Axis[0] = s.Axis[1] = s.Axis[3] = s.Axis[4] = Center;
            if (c == null) return;

            if (c.ByHand)
            {
                s.Axis[0] = Stick(c.LeftStick.X);
                s.Axis[1] = Stick(c.LeftStick.Y);
                s.Axis[3] = Stick(c.RightStick.X);
                s.Axis[4] = Stick(c.RightStick.Y);
                s.Axis[LeftTrigger] = Trigger(c.LeftTrigger);
                s.Axis[RightTrigger] = Trigger(c.RightTrigger);
                for (int i = 0; i < c.Buttons.Length && i < s.Buttons.Length; i++) s.Buttons[i] = c.Buttons[i];
                return;
            }

            // One button at a time, the next after each press.
            if (c.PressButtons)
            {
                var turns = c.PressNavigation ? AllButtons : PlainButtons;
                long press = ms / Math.Max(50, c.PressMs);
                s.Buttons[turns[(int)(press % turns.Length)]] = true;
            }

            // Moving for the Move Time, then resting for the Rest Time. Every
            // other cycle the sweep runs the other way.
            int busy = Math.Max(500, c.MoveMs);
            int idle = Math.Max(0, c.RestMs);
            long cycle = busy + idle;
            long time = ms % cycle;
            if (time >= busy) return;
            bool invert = (ms / cycle) % 2 == 1;
            double into = (double)time / busy;
            // From one end to the other and back over the moving time.
            double ramp = into < 0.5 ? into * 2 : (1 - into) * 2;

            if (c.TurnDpad) s.Povs[0] = (int)(into * 8) % 8 * 4500;

            if (c.MoveSticks)
            {
                double x, y;
                if (c.Circle)
                {
                    double angle = into * 2 * Math.PI;
                    x = Math.Sin(angle);
                    y = -Math.Cos(angle);
                }
                else
                {
                    x = Math.Sin(into * 2 * Math.PI) * (invert ? -1 : 1);
                    y = ramp * 2 - 1;
                }
                s.Axis[0] = s.Axis[3] = Stick(x * 100);
                s.Axis[1] = s.Axis[4] = Stick(y * 100);
            }
            if (c.MoveTriggers) s.Axis[LeftTrigger] = s.Axis[RightTrigger] = Trigger(ramp * 100);
        }

        /// <summary>A stick's -100 to 100 as a raw axis, 1 to 65535 about
        /// the center.</summary>
        internal static int Stick(double plain) => Math.Clamp((int)Math.Round(Center + plain / 100 * 32767), 0, 65535);

        /// <summary>A trigger's 0 to 100 as a raw axis.</summary>
        internal static int Trigger(double plain) => Math.Clamp((int)Math.Round(plain / 100 * 65535), 0, 65535);

        // ── Its declarations ──

        internal const string KeyMoves = "moves", KeyPattern = "pattern", KeyMove = "move-time", KeyRest = "rest-time";
        internal const string KeyButtons = "press-buttons", KeyNavigation = "press-navigation", KeyDpad = "turn-dpad";
        internal const string KeyPress = "press-time", KeyControllers = "controllers", KeyHand = "by-hand", KeyRate = "reads";
        internal const string KeyLeftStick = "hand-left-stick", KeyRightStick = "hand-right-stick";
        internal const string KeyLeftTrigger = "hand-left-trigger", KeyRightTrigger = "hand-right-trigger";

        /// <summary>The By Hand toggles' keys and the button each holds, in
        /// the order the row draws them.</summary>
        internal static readonly (string Key, int Button, string Words)[] HandButtons =
        {
            ("hand-a", 0, "A"), ("hand-b", 1, "B"), ("hand-x", 2, "X"), ("hand-y", 3, "Y"),
            ("hand-lb", 4, "LB"), ("hand-rb", 5, "RB"), ("hand-ls", 8, "LS"), ("hand-rs", 9, "RS"),
            ("hand-back", 6, "Back"), ("hand-start", 7, "Start"), ("hand-guide", 10, "Guide"),
        };

        /// <summary>Declares the Test Controller's settings into
        /// <paramref name="set"/>, in the language in use. Declared again
        /// when the language changes: the values stay.</summary>
        internal static void Declare(PluginSettingSet set)
        {
            var s = Strings.Instance;
            void Add(Dictionary<string, string> words, string choicesJson = null)
            {
                System.Text.Json.JsonElement? json = null;
                System.Text.Json.JsonDocument doc = null;
                if (choicesJson != null)
                {
                    doc = System.Text.Json.JsonDocument.Parse(choicesJson);
                    json = doc.RootElement;
                }
                try
                {
                    var d = PluginDeclarations.Read(words, json, out string error)
                            ?? throw new InvalidOperationException("The Test Controller's declaration of " + words["key"] + " was refused: " + error);
                    set.Declare(d, out _);
                }
                finally { doc?.Dispose(); }
            }
            string Choices(params (string Value, string Words)[] list)
            {
                var items = new List<object>();
                foreach (var (value, words) in list) items.Add(new { value, name = words });
                return System.Text.Json.JsonSerializer.Serialize(new { choices = items });
            }
            static Dictionary<string, string> W(params (string, string)[] pairs)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in pairs) if (v != null) d[k] = v;
                return d;
            }

            Add(W(("key", KeyMoves), ("kind", "choice"), ("name", s.Plugins_TC_WhatMoves), ("default", "both")),
                Choices(("both", s.Plugins_TC_WhatMoves_Both), ("sticks", s.Plugins_TC_WhatMoves_Sticks), ("triggers", s.Plugins_TC_WhatMoves_Triggers)));
            Add(W(("key", KeyPattern), ("kind", "choice"), ("name", s.Plugins_TC_Pattern), ("default", "sweep")),
                Choices(("sweep", s.Plugins_TC_Pattern_Sweep), ("circle", s.Plugins_TC_Pattern_Circle)));
            Add(W(("key", KeyMove), ("kind", "number"), ("name", s.Plugins_TC_MoveTime), ("min", "0.5"), ("max", "20"), ("step", "0.5"),
                ("unit", "s"), ("default", "4")));
            Add(W(("key", KeyRest), ("kind", "number"), ("name", s.Plugins_TC_RestTime), ("min", "0"), ("max", "20"), ("step", "0.5"),
                ("unit", "s"), ("default", "2")));
            Add(W(("key", KeyButtons), ("kind", "switch"), ("name", s.Plugins_TC_PressButtons), ("default", "true")));
            Add(W(("key", KeyNavigation), ("kind", "switch"), ("name", s.Plugins_TC_PressNavigation), ("help", s.Plugins_TC_PressNavigation_Help),
                ("default", "false")));
            Add(W(("key", KeyDpad), ("kind", "switch"), ("name", s.Plugins_TC_TurnDpad), ("help", s.Plugins_TC_TurnDpad_Help), ("default", "false")));
            Add(W(("key", KeyPress), ("kind", "number"), ("name", s.Plugins_TC_PressTime), ("min", "50"), ("max", "5000"), ("step", "50"),
                ("unit", "ms"), ("default", "250")));
            Add(W(("key", KeyControllers), ("kind", "choice"), ("name", s.Plugins_TC_Controllers), ("default", "1")),
                Choices(("1", (1).ToString(CultureInfo.CurrentCulture)), ("2", (2).ToString(CultureInfo.CurrentCulture)),
                    ("3", (3).ToString(CultureInfo.CurrentCulture)), ("4", (4).ToString(CultureInfo.CurrentCulture))));
            Add(W(("key", KeyHand), ("kind", "switch"), ("name", s.Plugins_TC_ByHand), ("help", s.Plugins_TC_ByHand_Help), ("default", "false")));
            Add(W(("key", KeyLeftStick), ("kind", "pad"), ("name", s.Plugins_TC_LeftStick), ("look", "compact"), ("when", KeyHand)));
            Add(W(("key", KeyRightStick), ("kind", "pad"), ("name", s.Plugins_TC_RightStick), ("look", "compact"), ("when", KeyHand)));
            Add(W(("key", KeyLeftTrigger), ("kind", "number"), ("name", s.Plugins_TC_LeftTrigger), ("min", "0"), ("max", "100"),
                ("unit", "%"), ("look", "compact"), ("when", KeyHand), ("default", "0")));
            Add(W(("key", KeyRightTrigger), ("kind", "number"), ("name", s.Plugins_TC_RightTrigger), ("min", "0"), ("max", "100"),
                ("unit", "%"), ("look", "compact"), ("when", KeyHand), ("default", "0")));
            foreach (var (key, _, words) in HandButtons)
                Add(W(("key", key), ("kind", "switch"), ("name", words), ("look", "compact"), ("when", KeyHand), ("default", "false")));
            Add(W(("key", KeyRate), ("kind", "status"), ("name", s.Plugins_TC_ReadsPerSecond)));
        }

        /// <summary>The settings in force, read from
        /// <paramref name="set"/>.</summary>
        internal static TestControllerConfig Read(PluginSettingSet set)
        {
            string V(string key) => set.ValueOf(null, key);
            double N(string key, double fallback) => PluginDeclarations.TryNumber(V(key), out double n) ? n : fallback;
            bool B(string key) => V(key) == "true";
            string moves = V(KeyMoves) ?? "both";
            var buttons = new bool[ButtonNames.Length];
            foreach (var (key, button, _) in HandButtons) buttons[button] = B(key);
            return new TestControllerConfig
            {
                MoveSticks = moves is "both" or "sticks",
                MoveTriggers = moves is "both" or "triggers",
                Circle = V(KeyPattern) == "circle",
                MoveMs = (int)Math.Round(N(KeyMove, 4) * 1000),
                RestMs = (int)Math.Round(N(KeyRest, 2) * 1000),
                PressButtons = B(KeyButtons),
                PressNavigation = B(KeyNavigation),
                TurnDpad = B(KeyDpad),
                PressMs = (int)Math.Round(N(KeyPress, 250)),
                Controllers = Math.Clamp((int)N(KeyControllers, 1), 1, MostControllers),
                ByHand = B(KeyHand),
                LeftStick = PluginValues.Pad(V(KeyLeftStick)),
                RightStick = PluginValues.Pad(V(KeyRightStick)),
                LeftTrigger = N(KeyLeftTrigger, 0),
                RightTrigger = N(KeyRightTrigger, 0),
                Buttons = buttons,
            };
        }
    }

    /// <summary>
    /// One Test Controller's motion, worked out on the poll thread each
    /// time the engine reads the device (ProgramDevice.Live), so it is as
    /// fine as the polling rate. It counts the reads for Reads per Second,
    /// and hands the state to the device every 20 ms for readers off the
    /// poll thread (ProgramDevice.PeekState).
    /// </summary>
    internal sealed class TestControllerMotion
    {
        private readonly Func<TestControllerConfig> _config;
        private readonly Func<long> _nowMs;
        private readonly CustomInputState _state = new();
        private readonly ProgramDevice _device;
        private int _reads;
        private long _lastPublishMs = long.MinValue;

        public TestControllerMotion(ProgramDevice device, Func<TestControllerConfig> config, Func<long> nowMs = null)
        {
            _device = device;
            _config = config;
            var watch = Stopwatch.StartNew();
            _nowMs = nowMs ?? (() => watch.ElapsedMilliseconds);
            TestControllerPattern.Fill(_state, null, 0);
        }

        /// <summary>The state at this read. On the poll thread.</summary>
        public CustomInputState Read()
        {
            Interlocked.Increment(ref _reads);
            long now = _nowMs();
            TestControllerPattern.Fill(_state, _config(), now);
            if (now - _lastPublishMs >= 20)
            {
                _lastPublishMs = now;
                _device?.Publish(_state);
            }
            return _state;
        }

        /// <summary>The reads since the last call, which the page asks once
        /// a second.</summary>
        public int TakeReads() => Interlocked.Exchange(ref _reads, 0);
    }
}
