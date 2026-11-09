using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;

namespace PadForge.Common.ConfigImport
{
    /// <summary>One physical input a config names, in PadForge's source
    /// grammar. <see cref="Control"/> is what a layer overrides: the same
    /// physical input carries the same string in every layer.</summary>
    internal sealed class ImportInput
    {
        public string Control = "";
        public string Label = "";
        public string Descriptor = "";
        public string DeviceGuid = "";
        public bool HalfAxis;
        public bool Invert;
        public int DeadZone;
        public bool IsKeyboard;
        public bool IsMouse;
        public bool IsAnalog;

        /// <summary>A second read this input needs to count, on the same
        /// device. Empty when it stands alone.</summary>
        public string Gate = "";

        /// <summary>A bounded read of this same input on this same device: a
        /// zone, a sector or a two-stage pull (BoundedReads). A row keeps its
        /// own read and takes this as its gate. A macro entry reads this
        /// alone, because a macro's entries each read any device on the slot
        /// and the two parts must read the same one. Empty when one threshold
        /// says it all.</summary>
        public string Band = "";

        /// <summary>Stops the input reading at <paramref name="next"/>, the
        /// edge of the zone above. No edge, or one at the top of the travel,
        /// leaves the read open upward.</summary>
        public ImportInput Below(int next)
        {
            Band = next > DeadZone && next < 100 && DeadZone > 0
                ? BoundedReads.Between(DeadZone, next, HalfAxis, Invert, Descriptor)
                : "";
            return this;
        }

        /// <summary>Set on an input that is one moment and not a state, the
        /// start or the end of a touch. It fires on that edge and has nothing
        /// a row could hold.</summary>
        public MacroTriggerMode? EdgeOnly;

        public MappingSource ToSource()
        {
            var src = new MappingSource
            {
                DeviceGuid = DeviceGuid ?? "",
                Descriptor = Descriptor,
                HalfAxis = HalfAxis,
                Invert = Invert,
            };
            if (DeadZone > 0) src.DeadZone = DeadZone;
            if (Gate.Length > 0) src.GateDescriptor = Gate;
            if (Band.Length > 0) src.GateDescriptor = Band;
            return src;
        }

        /// <summary>A macro trigger entry. Abstract and gesture descriptors
        /// ride the descriptor form, whose readers canonicalize them with the
        /// same per-device tuning a row gets. A named keyboard or mouse button
        /// rides the raw form the macro editor records.</summary>
        public MacroItem.TriggerInputEntry ToTriggerEntry()
        {
            Guid.TryParse(DeviceGuid ?? "", out Guid g);
            if (Band.Length > 0)
                return new MacroItem.TriggerInputEntry { DeviceGuid = g, SourceDescriptor = Band };
            if (g != Guid.Empty && !HalfAxis
                && Descriptor.StartsWith("Button ", StringComparison.Ordinal)
                && int.TryParse(Descriptor.AsSpan(7), out int raw))
            {
                return new MacroItem.TriggerInputEntry { DeviceGuid = g, RawButton = raw };
            }
            var entry = new MacroItem.TriggerInputEntry { DeviceGuid = g, SourceDescriptor = Descriptor };
            if (HalfAxis)
            {
                entry.HalfAxis = true;
                entry.Invert = Invert;
            }
            if (DeadZone > 0) entry.DescriptorDeadZone = DeadZone;
            return entry;
        }
    }

    internal enum ImportOutputKind
    {
        Key,
        MouseButton,
        MouseAxis,
        Wheel,
        PadButton,
        PadStick,
        PadTrigger,
    }

    /// <summary>One thing a config sends. Knows its row target and how it
    /// lowers to a macro step.</summary>
    internal sealed class ImportOutput
    {
        public ImportOutputKind Kind;
        public string Label = "";
        public string Target = "";
        public byte Vk;
        public int MouseButton;
        public bool Negative;
        public ushort PadBit;
        public double Scale = 1.0;
        public int PadId = 1;

        /// <summary>Adds to the stick's own position instead of setting it.</summary>
        public bool Relative;

        public bool IsKbm => Kind is ImportOutputKind.Key or ImportOutputKind.MouseButton
            or ImportOutputKind.MouseAxis or ImportOutputKind.Wheel;

        /// <summary>True when a mapping row can carry it. The keyboard row
        /// engine's key list is closed, so media keys, the Windows key and
        /// F13 up ride macros instead. A press reads full scale on a stick row
        /// whatever its sensitivity, so a partial stick value rides a macro
        /// too.</summary>
        public bool HasRow => Kind switch
        {
            ImportOutputKind.Key => SteamInputVkTable.IsRowKey(Vk),
            ImportOutputKind.PadStick or ImportOutputKind.PadTrigger => Scale == 1.0 && !Relative,
            _ => true,
        };

        public static ImportOutput Key(byte vk) => new()
        {
            Kind = ImportOutputKind.Key,
            Vk = vk,
            Target = SteamInputVkTable.KbmKeyTarget(vk),
            Label = KeyName(vk),
        };

        /// <param name="index">0 Left, 1 Right, 2 Middle, 3 X1, 4 X2.</param>
        public static ImportOutput Mouse(int index) => new()
        {
            Kind = ImportOutputKind.MouseButton,
            MouseButton = index,
            Target = "KbmMBtn" + index,
            Label = index switch
            {
                0 => "Mouse Left",
                1 => "Mouse Right",
                2 => "Mouse Middle",
                3 => "Mouse X1",
                _ => "Mouse X2",
            },
        };

        public static ImportOutput MouseMove(bool horizontal, bool negative) => new()
        {
            Kind = ImportOutputKind.MouseAxis,
            Target = horizontal ? "KbmMouseX" : "KbmMouseY",
            Negative = negative,
            Label = horizontal ? (negative ? "Mouse Left" : "Mouse Right") : (negative ? "Mouse Up" : "Mouse Down"),
        };

        public static ImportOutput MouseWheel(bool horizontal, bool negative) => new()
        {
            Kind = ImportOutputKind.Wheel,
            Target = horizontal ? "KbmScrollH" : "KbmScroll",
            Negative = negative,
            Label = horizontal ? (negative ? "Wheel Left" : "Wheel Right") : (negative ? "Wheel Up" : "Wheel Down"),
        };

        public static ImportOutput Pad(string target) => new()
        {
            Kind = ImportOutputKind.PadButton,
            Target = target,
            PadBit = XInputTargetTable.BitForTarget(target),
            Label = target switch
            {
                "ButtonA" => "A",
                "ButtonB" => "B",
                "ButtonX" => "X",
                "ButtonY" => "Y",
                "LeftShoulder" => "LB",
                "RightShoulder" => "RB",
                "ButtonBack" => "Back",
                "ButtonStart" => "Start",
                "ButtonGuide" => "Guide",
                "LeftThumbButton" => "Left stick click",
                "RightThumbButton" => "Right stick click",
                "DPadUp" => "D-pad Up",
                "DPadDown" => "D-pad Down",
                "DPadLeft" => "D-pad Left",
                "DPadRight" => "D-pad Right",
                "TouchpadClick" => "Touchpad click",
                _ => target,
            },
        };

        public static ImportOutput Stick(string target, bool negative) => new()
        {
            Kind = ImportOutputKind.PadStick,
            Target = target,
            Negative = negative,
            Label = (target.StartsWith("Left", StringComparison.Ordinal) ? "Left stick " : "Right stick ")
                + (target.EndsWith("X", StringComparison.Ordinal)
                    ? (negative ? "Left" : "Right")
                    : (negative ? "Up" : "Down")),
        };

        public static ImportOutput Trigger(string target) => new()
        {
            Kind = ImportOutputKind.PadTrigger,
            Target = target,
            Label = target == "LeftTrigger" ? "Left trigger" : "Right trigger",
        };

        /// <summary>A key's name for the preview. Numpad Enter has no VK of
        /// its own, and PadForge keeps it at 0x88
        /// (RawInputListener.NumpadEnterKey).</summary>
        public static string KeyName(byte vk)
            => vk == 0x88 ? "NumpadEnter"
            : Enum.IsDefined(typeof(VirtualKey), (int)vk)
                ? ((VirtualKey)vk).ToString()
                : "0x" + vk.ToString("X2");

        /// <summary>The AxisHold payload, the same frames the Workshop
        /// materializer's MapAxisTarget writes: triggers pull on 0..32767,
        /// stick X keeps its sign, stick Y negates because XInput's up is
        /// positive while the row frame's up is negative.</summary>
        public (MacroAxisTarget Target, short Value) AxisPayload()
        {
            double m = Math.Clamp(Scale, 0.0, 1.0);
            short pos = (short)Math.Round(32767 * m);
            short neg = (short)Math.Round(-32768 * m);
            return Target switch
            {
                "LeftTrigger" => (MacroAxisTarget.LeftTrigger, pos),
                "RightTrigger" => (MacroAxisTarget.RightTrigger, pos),
                "LeftThumbAxisX" => (MacroAxisTarget.LeftStickX, Negative ? neg : pos),
                "RightThumbAxisX" => (MacroAxisTarget.RightStickX, Negative ? neg : pos),
                "LeftThumbAxisY" => (MacroAxisTarget.LeftStickY, Negative ? pos : neg),
                "RightThumbAxisY" => (MacroAxisTarget.RightStickY, Negative ? pos : neg),
                _ => (MacroAxisTarget.None, (short)0),
            };
        }

        /// <summary>One press and release.</summary>
        public ActionData Tap(int ms = 50)
        {
            switch (Kind)
            {
                case ImportOutputKind.Key:
                    return new ActionData { Type = MacroActionType.KeyPress, KeyCode = Vk, DurationMs = ms };
                case ImportOutputKind.MouseButton:
                    return new ActionData
                    {
                        Type = MacroActionType.MouseButtonPress,
                        MouseButton = (MacroMouseButton)MouseButton,
                        DurationMs = ms,
                    };
                case ImportOutputKind.MouseAxis:
                    int amount = (int)Math.Round(Math.Max(1, Scale) * (Negative ? -1 : 1));
                    return new ActionData
                    {
                        Type = MacroActionType.MouseNudge,
                        NudgeDx = Target == "KbmMouseX" ? amount : 0,
                        NudgeDy = Target == "KbmMouseY" ? amount : 0,
                    };
                case ImportOutputKind.Wheel:
                    return WheelTap();
                case ImportOutputKind.PadButton:
                    return new ActionData { Type = MacroActionType.ButtonPress, ButtonFlags = PadBit, DurationMs = ms };
                default:
                    var (axis, value) = AxisPayload();
                    return new ActionData
                    {
                        Type = MacroActionType.AxisHold,
                        AxisTarget = axis,
                        AxisValue = value,
                        DurationMs = ms,
                    };
            }
        }

        /// <summary>The axis nudged by this output for as long as the action
        /// is current (reWASD's relative deflection, the action built for it).</summary>
        public ActionData Add(int ms = 1)
        {
            var (axis, value) = AxisPayload();
            return new ActionData { Type = MacroActionType.AxisAdd, AxisTarget = axis, AxisValue = value, DurationMs = ms };
        }

        /// <summary>Pressed and left down until <see cref="Up"/>. Null for an
        /// output with no held state, a nudge or a wheel detent.</summary>
        public ActionData Down() => Latch(MacroLatchDirection.On);

        public ActionData Up() => Latch(MacroLatchDirection.Off);

        /// <summary>Flips the held state on each fire.</summary>
        public ActionData Flip() => Latch(MacroLatchDirection.Toggle);

        private ActionData Latch(MacroLatchDirection direction)
        {
            switch (Kind)
            {
                case ImportOutputKind.Key:
                    return new ActionData { Type = MacroActionType.ToggleKey, KeyCode = Vk, LatchDirection = direction };
                case ImportOutputKind.MouseButton:
                    return new ActionData
                    {
                        Type = MacroActionType.ToggleMouseButton,
                        MouseButton = (MacroMouseButton)MouseButton,
                        LatchDirection = direction,
                    };
                case ImportOutputKind.PadButton:
                    return new ActionData { Type = MacroActionType.ToggleVcButton, ButtonFlags = PadBit, LatchDirection = direction };
                case ImportOutputKind.PadStick:
                case ImportOutputKind.PadTrigger:
                    // The axis latches already come as set and release: Set
                    // Axis (Latched) and Release Axis Latches (#251).
                    var (axis, value) = AxisPayload();
                    return direction switch
                    {
                        MacroLatchDirection.On => new ActionData { Type = MacroActionType.AxisSetLatched, AxisTarget = axis, AxisValue = value },
                        MacroLatchDirection.Off => new ActionData { Type = MacroActionType.AxisLatchRelease, AxisTarget = axis },
                        _ => new ActionData { Type = MacroActionType.ToggleVcAxis, AxisTarget = axis, AxisValue = value },
                    };
                default:
                    return null;
            }
        }

        /// <summary>Repeats while the macro runs. Null when the output has no
        /// repeating action of its own.</summary>
        public ActionData Pulse(int intervalMs)
        {
            switch (Kind)
            {
                case ImportOutputKind.Key:
                    return new ActionData { Type = MacroActionType.RepeatKeyWhileHeld, KeyCode = Vk, IntervalMs = intervalMs };
                case ImportOutputKind.PadButton:
                    return new ActionData { Type = MacroActionType.RepeatVcButtonWhileHeld, ButtonFlags = PadBit, IntervalMs = intervalMs };
                case ImportOutputKind.PadStick:
                case ImportOutputKind.PadTrigger:
                    var (axis, value) = AxisPayload();
                    return new ActionData
                    {
                        Type = MacroActionType.RepeatVcAxisWhileHeld,
                        AxisTarget = axis,
                        AxisValue = value,
                        IntervalMs = intervalMs,
                    };
                default:
                    return null;
            }
        }

        // The Workshop translator's wheel frame: vertical up is +1 and down
        // -1, horizontal right is +1 and left -1.
        private ActionData WheelTap() => new()
        {
            Type = MacroActionType.MouseWheelTap,
            AxisValue = Target == "KbmScrollH"
                ? (short)(Negative ? -1 : 1)
                : (short)(Negative ? 1 : -1),
            WheelHorizontal = Target == "KbmScrollH",
        };
    }

    /// <summary>A macro waiting to be written. Kept as a description because a
    /// layer that cannot inherit gets its own copy under its own mask.</summary>
    internal sealed class ImportMacro
    {
        public string Name = "";
        public string Control;
        public string Layer = ImportBuilder.BaseLayer;
        public IReadOnlyList<ImportInput> Inputs = Array.Empty<ImportInput>();
        public MacroTriggerMode Mode = MacroTriggerMode.OnPress;
        public int HoldMs = 500;
        public int DoublePressMs = 442;
        public MacroRepeatMode Repeat = MacroRepeatMode.Once;
        public int RepeatDelayMs = 100;
        public int PairKey;
        public int PadId = 1;
        public IReadOnlyList<ActionData> Actions = Array.Empty<ActionData>();

        /// <summary>True for a macro whose Base copy may keep running under a
        /// layer that has its own, because the layer's runs after it in the
        /// same frame: the lightbar. It never makes a layer stop inheriting.</summary>
        public bool RunsBesideLayers;

        /// <summary>True when the file asks a macro's held keys and color to
        /// stay on after it stops, for a later macro to clear.</summary>
        public bool KeepsWhatItHolds;

        /// <summary>True for a block: an empty macro whose trigger is the
        /// one key or button it holds back from Windows.</summary>
        public bool ConsumesTrigger;
    }

    /// <summary>
    /// What both importers share. They describe bindings per layer and per
    /// physical input, and this turns the description into a
    /// <see cref="ProfileData"/>: one pad slot per virtual controller the file
    /// names, one keyboard and mouse slot when anything sends keys or mouse,
    /// and macros, activators and menus on the right slots. The slot assembly
    /// follows WorkshopProfileMaterializer and StarterProfileCatalog.Wrap.
    ///
    /// <para>The layer work is the part with no precedent. In reWASD and
    /// DS4Windows a layer overrides by INPUT: while it is on, a button it maps
    /// loses its Main mapping and every other button keeps its own. PadForge's
    /// rows override by OUTPUT: a layer row replaces the Base row of its
    /// target. Writing the file's rows as they stand would double-fire one way
    /// and silently kill the physical button that drives the output the other
    /// way. So <see cref="Finish"/> works out what each layer's input table
    /// comes to, and writes a layer row for each target whose sources differ
    /// from Base, a sourceless NoInherit row where nothing is left.</para>
    ///
    /// <para>A macro has one layer mask, and "Base" stays open on an inheriting
    /// layer (MacroLayerGateOpen). When a layer overrides an input that drives
    /// a Base macro, the only exact form is a layer that does not inherit:
    /// Base closes there, and the layer takes its own copy of every row and
    /// macro it keeps. Only those layers pay for that.</para>
    /// </summary>
    internal sealed class ImportBuilder
    {
        public const string BaseLayer = "Base";

        /// <summary>The merged keyboard and mouse rows, whose guids hash their
        /// fixed paths and so are the same on every PC. Keyboard and mouse
        /// sources must name a device: an "(Any Device)" source reading
        /// "Button 9" also fires on a gamepad's right stick click, and
        /// "Button 0" on its A. These read every keyboard and mouse there is,
        /// which is what a config's keyboard and mouse mean.</summary>
        public static readonly string AllKeyboardsGuid = SdlKeyboardWrapper.BuildGuid("aggregate://keyboards").ToString();
        public static readonly string AllMiceGuid = SdlKeyboardWrapper.BuildGuid("aggregate://mice").ToString();

        private sealed class Binding
        {
            public string Layer;
            public string Control;
            public string Target;
            public int PadId;
            public MappingSource Source;

            /// <summary>The inputs the binding reads, with their labels.</summary>
            public List<(string Descriptor, string Label)> Reads;

            /// <summary>A hardware remap: reWASD replaces the input's native
            /// key, so holding it back matches.</summary>
            public bool ReplacesNative;
        }

        private sealed class LayerInfo
        {
            public string Mask;
            public string Name;
            public bool Expand;
            public readonly HashSet<string> Blocked = new(StringComparer.Ordinal);

            /// <summary>reWASD's unlock layers: the keyboard, the mouse or
            /// both work as they do without the profile, so Base's mappings
            /// on them stop on this layer.</summary>
            public bool UnlocksKeyboard;
            public bool UnlocksMouse;
        }

        private sealed class PadSlot
        {
            public int Id;
            public VirtualControllerType Type;
            public string ProfileId;
        }

        private readonly List<Binding> _bindings = new();
        private readonly List<LayerInfo> _layers = new();
        private readonly List<ImportMacro> _macros = new();
        private readonly List<ShiftActivator> _activators = new();
        private readonly List<MenuDefinitionEntry> _menus = new();
        private readonly List<PadSlot> _pads = new();
        private readonly HashSet<string> _sumRows = new(StringComparer.Ordinal);
        private readonly List<(ImportInput Input, string Name, string Layer)> _blocks = new();
        private readonly HashSet<string> _consumeAdvised = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Layer, string Target), (double Floor, double Scale, int PadId)> _stickShapes = new();
        private int _pairSeq;

        public TranslationReport Report { get; } = new();
        public bool ReadsKeyboard { get; set; }
        public bool ReadsMouse { get; set; }

        /// <summary>True once <see cref="Finish"/> finds something that reads
        /// whichever controller is on the slot.</summary>
        public bool ReadsController { get; private set; }

        /// <summary>Gyro engage stamped on every claimed slot, the
        /// materializer's rule: split configs host gyro rows on both sides,
        /// and the gate is per slot.</summary>
        public string GyroEngageDescriptor { get; set; } = "";
        public bool GyroEngageInvert { get; set; }
        public bool GyroEngageToggle { get; set; }

        /// <summary>What the file's buttons start or power off, for the
        /// preview to say before anything is accepted.</summary>
        public List<string> Runs { get; } = new();

        /// <summary>A stick or trigger's response curve, in the card's point
        /// format, for whichever device lands on the slot.</summary>
        public Dictionary<string, string> Curves { get; } = new(StringComparer.Ordinal);

        public string RumblePercent { get; set; } = "";
        public string IdleDisconnectSeconds { get; set; } = "";

        /// <summary>Starts the DSU motion server for emulators while the
        /// profile is loaded.</summary>
        public bool EnableDsu { get; set; }

        /// <summary>The gyro dead zone in degrees per second, as text, for
        /// whichever device lands on the slot. Empty means none.</summary>
        public string GyroDeadZone { get; set; } = "";

        /// <summary>The touchpad momentum glide, as text, for pad 0 of
        /// whichever device lands on the slot. Empty means none.</summary>
        public string TouchpadGlide { get; set; } = "";

        /// <summary>The battery light's empty and full colors,
        /// "r,g,b;r,g,b", or empty.</summary>
        public string BatteryColors { get; set; } = "";

        /// <summary>What the profile starts by itself when it loads.</summary>
        public List<string> Starts { get; } = new();

        /// <summary>The Run Program action for something the file starts, noted
        /// for the preview.</summary>
        public ActionData Run(string label, string path, string args = "", string workDir = "", bool onLoad = false)
        {
            var list = onLoad ? Starts : Runs;
            if (!list.Contains(label)) list.Add(label);
            return new ActionData
            {
                Type = MacroActionType.RunProgram,
                ProgramPath = path,
                ProgramArgs = args ?? "",
                ProgramWorkingDir = workDir ?? "",
            };
        }

        private readonly HashSet<string> _curvesOnRows = new(StringComparer.Ordinal);

        /// <summary>The curve stamps for a slot's set. A target whose rows
        /// tune it differently on its layers keeps its rows' tuning at
        /// assignment (WorkshopTuningApplier.TargetsKeepingTheirRows), and its
        /// card stays as it was, so its curve rides the rows that read the
        /// stick or trigger as the nearest power curve, and the report says
        /// so.</summary>
        private void WriteCurves(MappingSet set)
        {
            var kept = PadForge.Services.WorkshopTuningApplier.TargetsKeepingTheirRows(set);
            var entries = new List<string>();
            foreach (var (target, points) in Curves)
            {
                if (!kept.Contains(target))
                {
                    entries.Add(target + "=" + points);
                    continue;
                }
                double exponent = PowerFit(points);
                foreach (var row in set.Rows.Where(r => r.Target == target))
                    foreach (var src in row.Sources.Where(s => s.ParamCurveExponent <= 0 && ReadsTheCard(s.Descriptor)))
                        src.ParamCurveExponent = exponent;
                if (_curvesOnRows.Add(target))
                    Partial(ImportReasons.CurveOnRows, target, "Curve", "", target);
            }
            set.WorkshopCurves = string.Join("|", entries);
        }

        /// <summary>A stick or trigger read that a card would shape.</summary>
        private static bool ReadsTheCard(string descriptor) => descriptor is
            "Gamepad LeftStickX" or "Gamepad LeftStickY" or "Gamepad RightStickX" or "Gamepad RightStickY"
            or "Gamepad LeftTrigger" or "Gamepad RightTrigger";

        /// <summary>The exponent e whose x^e lies nearest a curve's points in
        /// least squares, on 0.05 to 20.</summary>
        internal static double PowerFit(string points)
        {
            var pts = Common.CurveLut.Parse(points).Where(p => p.X > 0.0).ToList();
            if (pts.Count == 0) return 1.0;
            double Error(double e) => pts.Sum(p => (Math.Pow(p.X, e) - p.Y) * (Math.Pow(p.X, e) - p.Y));
            double lo = Math.Log(0.05), hi = Math.Log(20.0);
            for (int i = 0; i < 80; i++)
            {
                double a = lo + (hi - lo) / 3.0, b = hi - (hi - lo) / 3.0;
                if (Error(Math.Exp(a)) < Error(Math.Exp(b))) hi = b;
                else lo = a;
            }
            return Math.Exp((lo + hi) / 2.0);
        }

        /// <summary>A curve's points, the card's own format.</summary>
        public static string CurvePoints(Func<double, double> curve, int steps = 16)
        {
            var parts = new List<string>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                double x = (double)i / steps;
                double y = Math.Clamp(curve(x), 0.0, 1.0);
                parts.Add(x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ","
                        + y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            }
            return string.Join(";", parts);
        }

        public int NextPairKey() => ++_pairSeq;

        public void DeclarePad(int id, VirtualControllerType type, string profileId)
        {
            if (_pads.Any(p => p.Id == id)) return;
            _pads.Add(new PadSlot { Id = id, Type = type, ProfileId = profileId });
        }

        public bool HasPad => _pads.Count > 0;

        /// <summary>Takes back every pad slot and what would have read onto
        /// one, for a file that keeps the virtual controller off.</summary>
        public void DropPads()
        {
            _bindings.RemoveAll(b => b.PadId != 0);
            _pads.Clear();
        }

        /// <summary>The virtual controller type a pad output lands on.</summary>
        public VirtualControllerType? PadTypeOf(int id)
            => _pads.FirstOrDefault(p => p.Id == id)?.Type ?? _pads.FirstOrDefault()?.Type;

        public void AddLayer(string mask, string name)
        {
            if (_layers.Any(l => l.Mask == mask)) return;
            _layers.Add(new LayerInfo { Mask = mask, Name = name });
        }

        public string LayerName(string mask)
            => mask == BaseLayer ? BaseLayer : _layers.FirstOrDefault(l => l.Mask == mask)?.Name ?? mask;

        /// <summary>Binds an input's source to a row target on a layer.</summary>
        public void Bind(string layer, string control, string target, MappingSource source, int padId = 1)
        {
            _bindings.Add(new Binding
            {
                Layer = layer,
                Control = control,
                Target = target,
                PadId = padId,
                Source = source,
            });
        }

        /// <summary>The input drives this output on this layer. Direction
        /// outputs read negative through the source's output flip.</summary>
        public void Bind(string layer, ImportInput input, ImportOutput output, IReadOnlyList<ImportInput> gates = null,
            bool replacesNative = false)
        {
            var src = input.ToSource();
            if (output.Kind is ImportOutputKind.PadStick or ImportOutputKind.MouseAxis or ImportOutputKind.Wheel)
                SetOutputDirection(src, output.Negative);
            var reads = new List<(string, string)> { (input.Descriptor, input.Label) };
            if (gates != null)
            {
                if (gates.Count > 0) src.GateDescriptor = gates[0].Descriptor;
                if (gates.Count > 1) src.Gate2Descriptor = gates[1].Descriptor;
                for (int i = 0; i < gates.Count && i < 2; i++) reads.Add((gates[i].Descriptor, gates[i].Label));
            }
            Bind(layer, input.Control, output.Target, src, output.IsKbm ? 0 : output.PadId);
            _bindings[^1].Reads = reads;
            _bindings[^1].ReplacesNative = replacesNative;
        }

        /// <summary>The import asks for Consume Mapped Inputs on this device
        /// (ImportReasons.ConsumeKeys).</summary>
        public void AdviseConsume(string deviceGuid) => _consumeAdvised.Add(deviceGuid ?? "");

        /// <summary>A profile's own lightbar color, written every frame while
        /// its macro is open. It holds a moment past each write and needs no
        /// clear, so it ends by itself when the profile switches away, where a
        /// Sticky override would stay on the bar with nothing left to clear it.</summary>
        public static ActionData ProfileColor(byte r, byte g, byte b) => new()
        {
            Type = MacroActionType.LightbarColor,
            LightbarHoldMode = MacroLightbarHoldMode.Reactive,
            LightbarColorSource = MacroLightbarColorSource.Fixed,
            LightbarHoldMs = 250,
            LightbarFadeMs = 0,
            LightbarR = r,
            LightbarG = g,
            LightbarB = b,
        };

        /// <summary>The steps of a lighting pattern that plays on an Always
        /// macro: a color held past the wait that follows it, so the bar never
        /// falls back between two steps, and it ends by itself when the
        /// profile switches away.</summary>
        public static ActionData[] LightStep(byte r, byte g, byte b, int holdMs, int fadeMs, int waitMs)
        {
            var color = ProfileColor(r, g, b);
            color.LightbarHoldMs = holdMs;
            color.LightbarFadeMs = fadeMs;
            return new[] { color, new ActionData { Type = MacroActionType.Delay, DurationMs = waitMs } };
        }

        /// <summary>Every hue once around the given time, in steps too short to
        /// see, as an Always macro plays it, at a fraction of full
        /// brightness.</summary>
        public static List<ActionData> RainbowPattern(int periodMs, double brightness = 1.0)
        {
            const int Steps = 36;
            int wait = Math.Max(10, periodMs / Steps);
            brightness = Math.Clamp(brightness, 0.0, 1.0);
            var actions = new List<ActionData>(Steps * 2);
            for (int i = 0; i < Steps; i++)
            {
                var (r, g, b) = FromHue(i * 360.0 / Steps);
                actions.AddRange(LightStep((byte)(r * brightness), (byte)(g * brightness), (byte)(b * brightness), wait + 100, 0, wait));
            }
            return actions;
        }

        private static (byte R, byte G, byte B) FromHue(double hue)
        {
            double x = 1.0 - Math.Abs(hue / 60.0 % 2.0 - 1.0);
            var (r, g, b) = (int)(hue / 60.0) switch
            {
                0 => (1.0, x, 0.0),
                1 => (x, 1.0, 0.0),
                2 => (0.0, 1.0, x),
                3 => (0.0, x, 1.0),
                4 => (x, 0.0, 1.0),
                _ => (1.0, 0.0, x),
            };
            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        /// <summary>The Workshop translator's SetOutputInvert: a half-axis
        /// read spends Invert on choosing the half, so the output flip moves
        /// to its own field.</summary>
        public static void SetOutputDirection(MappingSource src, bool negative)
        {
            if (Engine.Common.Mapping.SourceCoercion.InvertConsumedByHalfAxisRead(src))
                src.InvertOutput = negative;
            else
                src.Invert = negative;
        }

        /// <summary>Removes what an input does on a layer. On Base the
        /// bindings go. On another layer the input also stops inheriting
        /// Base.</summary>
        public void Remove(string layer, string control)
        {
            _bindings.RemoveAll(b => b.Layer == layer && b.Control == control);
            if (layer != BaseLayer)
                _layers.FirstOrDefault(l => l.Mask == layer)?.Blocked.Add(control);
        }

        /// <summary>The layer hands the keyboard, the mouse or both back to
        /// Windows: Base's mappings that read them stop on it, and the
        /// layer's own stay.</summary>
        public void Unlock(string layer, bool keyboard, bool mouse)
        {
            var info = _layers.FirstOrDefault(l => l.Mask == layer);
            if (info == null) return;
            info.UnlocksKeyboard |= keyboard;
            info.UnlocksMouse |= mouse;
        }

        public bool HasLayer(string mask) => mask == BaseLayer || _layers.Any(l => l.Mask == mask);

        /// <summary>Moves what an input does from one layer to another.</summary>
        public void MoveBindings(string control, string fromLayer, string toLayer)
        {
            foreach (var b in _bindings.Where(b => b.Layer == fromLayer && b.Control == control))
                b.Layer = toLayer;
        }

        /// <summary>Copies what an input does on one layer onto another,
        /// each source changed as given.</summary>
        public void CopyBindings(string control, string fromLayer, string toLayer, Action<MappingSource> change)
        {
            foreach (var b in _bindings.Where(b => b.Layer == fromLayer && b.Control == control).ToList())
            {
                var src = b.Source.Clone();
                change(src);
                _bindings.Add(new Binding { Layer = toLayer, Control = b.Control, Target = b.Target, PadId = b.PadId, Source = src });
            }
        }

        /// <summary>A key or button the file stops reaching Windows on a
        /// layer. Consume Mapped Inputs holds back one a row running on that
        /// layer reads already.
        /// One no row reads takes an empty macro on that layer that consumes
        /// its trigger, which the hook holds back the same way
        /// (InputService.MacroTriggerConsumesKey).</summary>
        public void Block(ImportInput input, string name, string layer = BaseLayer) => _blocks.Add((input, name, layer));

        /// <summary>A virtual stick axis's output range on a layer, reWASD's
        /// initial and maximum deflection: any movement starts at the floor
        /// and the axis tops out at the scale. The floor rides each source as
        /// its anti-deadzone, and the scale is an Always Scale Axis macro on
        /// the layer, so a key and the mouse both land inside the range.</summary>
        public void StickShape(string layer, string target, double floor, double scale, int padId)
            => _stickShapes[(layer, target)] = (floor, scale, padId);

        /// <summary>A rotated stick's rows add their two legs instead of
        /// taking the larger one.</summary>
        public void SumRow(string target) => _sumRows.Add(target);

        /// <summary>The layer activator an input makes: a button, a chord of
        /// two with a gate for a third, or an axis read past its threshold.</summary>
        public static ShiftActivator ActivatorFor(IReadOnlyList<ImportInput> inputs, string mode, string layer)
        {
            var first = inputs[0];
            var act = new ShiftActivator
            {
                DeviceGuid = first.DeviceGuid ?? "",
                Descriptor = first.Descriptor,
                Mode = mode,
                LayerMask = layer,
            };
            if (first.Gate.Length > 0) act.GateDescriptor = first.Gate;
            if (first.Band.Length > 0) act.GateDescriptor = first.Band;
            if (first.IsAnalog)
            {
                act.Kind = "Axis";
                act.AxisHalf = first.HalfAxis;
                act.AxisInvert = first.Invert;
                if (first.DeadZone > 0) act.AxisThreshold = first.DeadZone / 100.0;
                if (inputs.Count > 1) act.GateDescriptor = inputs[1].Descriptor;
                if (inputs.Count > 2) act.Gate2Descriptor = inputs[2].Descriptor;
            }
            else if (inputs.Count > 1)
            {
                act.Kind = "Chord";
                act.ChordSecondDeviceGuid = inputs[1].DeviceGuid ?? "";
                act.ChordSecondDescriptor = inputs[1].Descriptor;
                if (inputs.Count > 2) act.Gate2Descriptor = inputs[2].Descriptor;
            }
            return act;
        }

        public bool HasBinding(string layer, string control)
            => _bindings.Any(b => b.Layer == layer && b.Control == control);

        public void AddActivator(ShiftActivator activator) => _activators.Add(activator);

        public void AddMenu(MenuDefinitionEntry menu) => _menus.Add(menu);

        /// <summary>A macro that repeats until released can stop between a
        /// down and its up, and a latch outlives its macro, so it takes an
        /// OnRelease twin that clears what it may have left set.</summary>
        public void AddMacro(ImportMacro macro)
        {
            var clears = ReleaseOf(macro.Actions);
            bool stopsMidway = macro.Repeat == MacroRepeatMode.UntilRelease
                               && macro.PairKey == 0 && clears.Count > 0 && !macro.KeepsWhatItHolds;
            if (stopsMidway && macro.Mode == MacroTriggerMode.Toggle)
            {
                // A Toggle stops on the press that unlatches it, and this twin
                // runs first, so a fresh run's first down is not cleared.
                macro.PairKey = NextPairKey();
                _macros.Add(new ImportMacro
                {
                    Name = macro.Name + " (release)",
                    Control = macro.Control,
                    Layer = macro.Layer,
                    Inputs = macro.Inputs,
                    Mode = MacroTriggerMode.OnPress,
                    PairKey = macro.PairKey,
                    PadId = macro.PadId,
                    Actions = clears,
                });
            }
            else if (stopsMidway)
            {
                macro.PairKey = NextPairKey();
                _macros.Add(macro);
                _macros.Add(new ImportMacro
                {
                    Name = macro.Name + " (release)",
                    Control = macro.Control,
                    Layer = macro.Layer,
                    Inputs = macro.Inputs,
                    Mode = MacroTriggerMode.OnRelease,
                    PairKey = macro.PairKey,
                    PadId = macro.PadId,
                    Actions = clears,
                });
                return;
            }
            _macros.Add(macro);
        }

        /// <summary>The steps that release what these steps latch.</summary>
        public static List<ActionData> ReleaseOf(IEnumerable<ActionData> actions)
        {
            var clears = new List<ActionData>();
            foreach (var a in actions.Reverse())
            {
                if (a == null) continue;
                switch (a.Type)
                {
                    case MacroActionType.ToggleKey when a.LatchDirection == MacroLatchDirection.On:
                        clears.Add(new ActionData { Type = a.Type, KeyCode = a.KeyCode, LatchDirection = MacroLatchDirection.Off });
                        break;
                    case MacroActionType.ToggleMouseButton when a.LatchDirection == MacroLatchDirection.On:
                        clears.Add(new ActionData { Type = a.Type, MouseButton = a.MouseButton, LatchDirection = MacroLatchDirection.Off });
                        break;
                    case MacroActionType.ToggleVcButton when a.LatchDirection == MacroLatchDirection.On:
                        clears.Add(new ActionData { Type = a.Type, ButtonFlags = a.ButtonFlags, LatchDirection = MacroLatchDirection.Off });
                        break;
                    case MacroActionType.AxisSetLatched:
                        clears.Add(new ActionData { Type = MacroActionType.AxisLatchRelease, AxisTarget = a.AxisTarget });
                        break;
                    case MacroActionType.LightbarColor when a.LightbarHoldMode == MacroLightbarHoldMode.Sticky:
                        clears.Add(new ActionData { Type = MacroActionType.LightbarColorClear });
                        break;
                }
            }
            return clears;
        }

        /// <summary>The Workshop's HoldKey pair: the press leg sets the
        /// latches, so the held output rides the reconcile that releases it
        /// when the engine stops, and an OnRelease twin clears them. Clearing
        /// an unset latch is a no-op, so a tap below a long-press threshold
        /// stays harmless.</summary>
        public void AddHoldPair(ImportMacro press, IReadOnlyList<ActionData> up)
        {
            press.Repeat = MacroRepeatMode.UntilRelease;
            if (up.Count == 0)
            {
                _macros.Add(press);
                return;
            }
            int key = NextPairKey();
            press.PairKey = key;
            _macros.Add(press);
            _macros.Add(new ImportMacro
            {
                Name = press.Name + " (release)",
                Control = press.Control,
                Layer = press.Layer,
                Inputs = press.Inputs,
                Mode = MacroTriggerMode.OnRelease,
                PairKey = key,
                PadId = press.PadId,
                Actions = up,
            });
        }

        /// <summary>Report line for a binding that came across as written.</summary>
        public void Clean(string path, string binding, string emitted)
            => Report.Add(TranslationStatus.Clean, "", path, binding, emitted);

        public void Partial(string reasonKey, string path, string binding, string emitted, params string[] args)
            => Report.Add(TranslationStatus.Partial, reasonKey, path, binding, emitted, args);

        public void Skipped(string reasonKey, string path, string binding, params string[] args)
            => Report.Add(TranslationStatus.Skipped, reasonKey, path, binding, "", args);

        /// <summary>reWASD sends a mapped key's native key unless the file
        /// unmaps it (reWASD blog, How to reassign keyboard keys: "we suggest
        /// using Unmaps for keyboard keys, so the native action did not
        /// bother the mapping you have added"). Consume Mapped Inputs, which
        /// the import asks for on a device the file unmaps keys of, holds
        /// back every key a row reads. So each key or button of such a device
        /// that a running row reads on a layer where the file does not unmap
        /// it gets a line, once. A hardware remap replaces the native key in
        /// reWASD, so it gets none.</summary>
        private void ReportKeysTheSwitchHoldsBack(Dictionary<LayerInfo, HashSet<string>> ownByLayer)
        {
            if (_consumeAdvised.Count == 0) return;
            var reported = new HashSet<(string, string)>();
            var layers = new List<LayerInfo> { null };
            layers.AddRange(_layers);
            foreach (var layer in layers)
            {
                string mask = layer?.Mask ?? BaseLayer;
                var own = layer != null ? ownByLayer[layer] : null;
                foreach (var b in _bindings)
                {
                    bool runs = layer == null
                        ? b.Layer == BaseLayer
                        : b.Layer == mask || (b.Layer == BaseLayer && !own.Contains(b.Control));
                    if (!runs || b.ReplacesNative || b.Reads == null) continue;
                    string device = b.Source.DeviceGuid ?? "";
                    if (!_consumeAdvised.Contains(device)) continue;
                    foreach (var (descriptor, label) in b.Reads)
                    {
                        if (string.IsNullOrEmpty(descriptor) || !descriptor.StartsWith("Button ", StringComparison.Ordinal)) continue;
                        bool unmapped = _blocks.Any(x => x.Input.Descriptor == descriptor && Same(x.Input.DeviceGuid, device)
                            && (x.Layer == mask || (x.Layer == BaseLayer && own != null && !own.Contains(x.Input.Control))));
                        if (unmapped || !reported.Add((device.ToLowerInvariant(), descriptor))) continue;
                        Partial(ImportReasons.MappedKeyHeldBack,
                            layer == null ? label : LayerName(mask) + " / " + label, label, "", label);
                    }
                }
            }
        }

        private HashSet<string> OwnControls(LayerInfo layer)
        {
            var own = new HashSet<string>(layer.Blocked, StringComparer.Ordinal);
            foreach (var b in _bindings)
                if (b.Layer == layer.Mask) own.Add(b.Control);
            foreach (var m in _macros)
                if (m.Layer == layer.Mask && m.Control != null) own.Add(m.Control);
            if (layer.UnlocksKeyboard || layer.UnlocksMouse)
            {
                // A chord with an unlocked part cannot fire either: that key
                // or button reaches Windows instead.
                bool Unlocked(string control) => control.Split('+').Any(p =>
                    (layer.UnlocksKeyboard && p.StartsWith("kb:", StringComparison.Ordinal))
                    || (layer.UnlocksMouse && (p.StartsWith("ms:", StringComparison.Ordinal)
                                               || p.StartsWith("gp:stick:mouse:", StringComparison.Ordinal))));
                foreach (var b in _bindings)
                    if (b.Layer == BaseLayer && Unlocked(b.Control)) own.Add(b.Control);
                foreach (var m in _macros)
                    if (m.Layer == BaseLayer && m.Control != null && Unlocked(m.Control)) own.Add(m.Control);
                // Base's unmap of a key or button the layer hands back stops
                // there too: the layer replaces Base (Finish) and takes no
                // copy of that block.
                foreach (var b in _blocks)
                    if (b.Layer == BaseLayer && !string.IsNullOrEmpty(b.Input.Control) && Unlocked(b.Input.Control))
                        own.Add(b.Input.Control);
            }
            return own;
        }

        /// <summary>The Scale Axis macros for each stick range under a
        /// scale, and the block that stops Base's on a layer whose own range
        /// has none. Runs before layers work out what they own.</summary>
        private void AddStickRangeMacros()
        {
            foreach (var ((layer, target), shape) in _stickShapes)
            {
                string control = "stickrange:" + target;
                if (shape.Scale < 1.0)
                {
                    var (axis, _) = ImportOutput.Stick(target, false).AxisPayload();
                    _macros.Add(new ImportMacro
                    {
                        Name = ImportOutput.Stick(target, false).Label.Replace(" Right", " X").Replace(" Down", " Y") + " range",
                        Control = control,
                        Layer = layer,
                        Mode = MacroTriggerMode.Always,
                        PadId = shape.PadId,
                        Actions = new[]
                        {
                            new ActionData
                            {
                                Type = MacroActionType.AxisScale,
                                AxisTarget = axis,
                                // The engine multiplies by 1 + value / 32767.
                                AxisValue = (short)Math.Round((shape.Scale - 1.0) * 32767),
                                DurationMs = int.MaxValue,
                            },
                        },
                    });
                }
                else if (layer != BaseLayer
                         && _stickShapes.TryGetValue((BaseLayer, target), out var baseShape) && baseShape.Scale < 1.0)
                {
                    _layers.FirstOrDefault(l => l.Mask == layer)?.Blocked.Add(control);
                }
            }
        }

        /// <summary>The floor a stick axis row's sources take on a layer: the
        /// layer's own range, else Base's, else none.</summary>
        private double? FloorOf(string layer, string target)
        {
            if (_stickShapes.TryGetValue((layer, target), out var own)) return own.Floor;
            if (_stickShapes.TryGetValue((BaseLayer, target), out var inherited)) return inherited.Floor;
            return null;
        }

        private static void SetFloor(IEnumerable<MappingSource> sources, double? floor)
        {
            if (floor == null) return;
            foreach (var s in sources) s.ParamAntiDeadzone = floor.Value;
        }

        public ProfileData Finish(string name, string executables)
        {
            AddStickRangeMacros();
            var ownByLayer = _layers.ToDictionary(l => l, OwnControls);

            foreach (var layer in _layers)
            {
                var own = ownByLayer[layer];
                if (_macros.Any(m => m.Layer == BaseLayer && !m.RunsBesideLayers && m.Control != null && own.Contains(m.Control)))
                    layer.Expand = true;
                // A shift inherits Main's unmap for every control it does not
                // map itself, and a control it maps or does not inherit works
                // without it (reWASD help, Shift mode). Base's block runs on
                // every layer that inherits, so a layer that owns a control
                // Base unmaps has to replace Base.
                if (_blocks.Any(b => b.Layer == BaseLayer && own.Contains(b.Input.Control)))
                    layer.Expand = true;
                // A solid color writes every frame and ends the frame on top
                // of Base's. A pattern writes between waits, and Base's color
                // would cover it, so Base's has to stop on that layer.
                if (_macros.Any(m => m.Layer == layer.Mask && m.Control != null && !m.RunsBesideLayers
                        && _macros.Any(b => b.Layer == BaseLayer && b.Control == m.Control)))
                    layer.Expand = true;
            }

            int maxPads = InputManager.MaxPads;
            var created = new bool[maxPads];
            var enabled = new bool[maxPads];
            var types = new int[maxPads];
            var ids = new string[maxPads];
            var sets = new MappingSet[maxPads];

            if (!_pads.Any() && _bindings.Any(b => b.PadId != 0))
                DeclarePad(1, VirtualControllerType.Xbox, InputManager.GetDefaultProfileId(VirtualControllerType.Xbox));

            // A macro rides a slot, so a file of nothing but macros still
            // needs one. Its keys go out through SendInput either way.
            bool needsKbm = _bindings.Any(b => b.PadId == 0) || ((_macros.Count > 0 || _blocks.Count > 0) && !_pads.Any());

            // Slot numbers: pads first, in file order, then keyboard and mouse.
            var slotOfPad = new Dictionary<int, int>();
            int next = 0;
            foreach (var pad in _pads)
            {
                if (next >= maxPads) break;
                slotOfPad[pad.Id] = next;
                created[next] = true;
                enabled[next] = true;
                types[next] = (int)pad.Type;
                ids[next] = pad.ProfileId;
                sets[next] = new MappingSet { Authoritative = true };
                next++;
            }
            int kbmSlot = -1;
            if (needsKbm && next < maxPads)
            {
                kbmSlot = next++;
                created[kbmSlot] = true;
                enabled[kbmSlot] = true;
                types[kbmSlot] = (int)VirtualControllerType.KeyboardMouse;
                ids[kbmSlot] = InputManager.GetDefaultProfileId(VirtualControllerType.KeyboardMouse);
                sets[kbmSlot] = new MappingSet { Authoritative = true };
            }
            int SlotOfBinding(int padId)
            {
                if (padId == 0) return kbmSlot;
                if (slotOfPad.TryGetValue(padId, out int s)) return s;
                return slotOfPad.Count > 0 ? slotOfPad.Values.First() : -1;
            }

            // Base rows, in the order the bindings were made.
            foreach (var group in _bindings.Where(b => b.Layer == BaseLayer)
                         .GroupBy(b => (Slot: SlotOfBinding(b.PadId), b.Target)))
            {
                if (group.Key.Slot < 0) continue;
                var row = new MappingRow { Target = group.Key.Target, LayerMask = BaseLayer };
                if (_sumRows.Contains(group.Key.Target)) row.CombineMode = "Sum";
                row.Sources.AddRange(Distinct(group.Select(b => b.Source)));
                // The binding's own source objects, so a layer that copies
                // them inherits Base's floor.
                SetFloor(row.Sources, _stickShapes.TryGetValue((BaseLayer, row.Target), out var shape) ? shape.Floor : null);
                sets[group.Key.Slot].Rows.Add(row);
            }

            // Layer rows: the input table the layer comes to, per target.
            var baseBindings = _bindings.Where(b => b.Layer == BaseLayer).ToList();
            foreach (var layer in _layers)
            {
                var own = ownByLayer[layer];
                var ownBindings = _bindings.Where(b => b.Layer == layer.Mask).ToList();
                var kept = baseBindings.Where(b => !own.Contains(b.Control)).ToList();

                var targets = new List<(int Slot, string Target)>();
                void Touch(int slot, string target)
                {
                    if (slot >= 0 && !targets.Contains((slot, target))) targets.Add((slot, target));
                }
                foreach (var b in ownBindings) Touch(SlotOfBinding(b.PadId), b.Target);
                foreach (var b in baseBindings)
                    if (layer.Expand || own.Contains(b.Control)) Touch(SlotOfBinding(b.PadId), b.Target);
                // A layer with its own stick range needs its own rows for that
                // stick, even where it maps nothing new.
                foreach (var ((shapeLayer, shapeTarget), shape) in _stickShapes)
                {
                    if (shapeLayer != layer.Mask) continue;
                    int slot = SlotOfBinding(shape.PadId);
                    if (baseBindings.Concat(ownBindings).Any(b => SlotOfBinding(b.PadId) == slot && b.Target == shapeTarget))
                        Touch(slot, shapeTarget);
                }

                foreach (var (slot, target) in targets)
                {
                    var sources = Distinct(kept.Concat(ownBindings)
                        .Where(b => SlotOfBinding(b.PadId) == slot && b.Target == target)
                        .Select(b => b.Source.Clone()));
                    var row = new MappingRow { Target = target, LayerMask = layer.Mask };
                    if (_sumRows.Contains(target)) row.CombineMode = "Sum";
                    SetFloor(sources, FloorOf(layer.Mask, target));
                    if (sources.Count == 0)
                    {
                        // A layer that replaces Base already outputs nothing
                        // here. An inheriting one must say so, the resolver's
                        // own rule: a zero-source layer row blocks Base
                        // fallthrough when it is an explicit NoInherit.
                        if (layer.Expand) continue;
                        row.NoInherit = true;
                    }
                    else
                    {
                        row.Sources.AddRange(sources);
                    }
                    sets[slot].Rows.Add(row);
                }
            }

            // A layer needs an activator to exist at all: one owns its tab. A
            // layer the file reaches by no button, or only by a one-shot jump,
            // takes the No Button mode, which never engages by itself, and a
            // file that truly never opens it says so.
            foreach (var layer in _layers)
            {
                if (_activators.Any(a => a.LayerMask == layer.Mask)) continue;
                _activators.Add(new ShiftActivator { Mode = "Passive", LayerMask = layer.Mask, Descriptor = "" });
                bool jumped = _macros.Any(m => m.Actions.Any(a =>
                    a.Type == MacroActionType.SwitchLayer && a.SwitchLayerMask == layer.Mask));
                if (!jumped) Partial(ImportReasons.LayerNoInput, layer.Name, layer.Name, "");
            }

            // Two activators that read alike are one: abstract sources make a
            // file's two controllers the same input.
            var activators = _activators
                .GroupBy(a => (a.Descriptor, a.DeviceGuid, a.Mode, a.LayerMask, a.HostLayerMask, a.Kind, a.ChordSecondDescriptor))
                .Select(g => g.First())
                .ToList();

            // Activators and menus ride every claimed slot: a split config
            // feeds both slots from one device, and the layer and menu
            // runtimes are per slot.
            for (int i = 0; i < maxPads; i++)
            {
                if (sets[i] == null) continue;
                foreach (var act in activators)
                {
                    var clone = act.Clone();
                    clone.InheritUnmapped = !_layers.Any(l => l.Mask == act.LayerMask && l.Expand);
                    if (string.IsNullOrEmpty(clone.LayerName)) clone.LayerName = LayerName(act.LayerMask);
                    sets[i].ShiftActivators.Add(clone);
                }
                foreach (var menu in _menus) sets[i].Menus.Add(menu.Clone());
                if (!string.IsNullOrEmpty(GyroEngageDescriptor))
                {
                    sets[i].WorkshopGyroEngageDescriptor = GyroEngageDescriptor;
                    sets[i].WorkshopGyroEngageInvert = GyroEngageInvert;
                    sets[i].WorkshopGyroEngageToggle = GyroEngageToggle;
                }
                if (Curves.Count > 0)
                    WriteCurves(sets[i]);
                sets[i].WorkshopRumblePercent = RumblePercent ?? "";
                sets[i].WorkshopIdleDisconnectSeconds = IdleDisconnectSeconds ?? "";
                sets[i].WorkshopBatteryColors = BatteryColors ?? "";
                sets[i].WorkshopGyroDeadZone = GyroDeadZone ?? "";
                sets[i].WorkshopTouchpadGlide = TouchpadGlide ?? "";
            }

            // Unclaimed slots stay non-authoritative, so a slot the user
            // creates later automaps normally.
            for (int i = 0; i < maxPads; i++) sets[i] ??= new MappingSet();

            var macros = new List<MacroData>();
            int firstPadSlot = slotOfPad.Count > 0 ? slotOfPad.Values.First() : Math.Max(kbmSlot, 0);
            int SlotOfMacro(ImportMacro m) => slotOfPad.TryGetValue(m.PadId, out int s) ? s : firstPadSlot;

            foreach (var m in _macros)
            {
                var data = ToMacroData(m, m.Layer, m.PairKey, SlotOfMacro(m));
                if (data != null) macros.Add(data);
            }
            foreach (var layer in _layers.Where(l => l.Expand))
            {
                var own = ownByLayer[layer];
                var pairMap = new Dictionary<int, int>();
                foreach (var m in _macros)
                {
                    if (m.Layer != BaseLayer) continue;
                    if (m.Control != null && own.Contains(m.Control)) continue;
                    int pair = 0;
                    if (m.PairKey != 0 && !pairMap.TryGetValue(m.PairKey, out pair))
                        pairMap[m.PairKey] = pair = NextPairKey();
                    var data = ToMacroData(m, layer.Mask, pair, SlotOfMacro(m));
                    if (data != null) macros.Add(data);
                }
            }

            // Each block sits on the layer that unmaps the key, so a key one
            // layer unmaps still reaches Windows on the others. A block is
            // only for a key no row reads where the block runs: the hook
            // eats a row's key only while the row runs
            // (InputManager.RowReadsUnder), and a block beside such a row
            // would also stop it while the key is down (the engine's
            // consume). A layer's block runs on that layer, where Base's
            // bindings it does not own run with its own. A Base block also
            // runs on every layer that inherits unmapped targets
            // (MacroLayerGateOpen).
            bool ReadOnLayer(LayerInfo layer, ImportInput input)
            {
                var own = ownByLayer[layer];
                return _bindings.Any(b => (b.Layer == layer.Mask || (b.Layer == BaseLayer && !own.Contains(b.Control)))
                                          && b.Source.Descriptor == input.Descriptor && Same(b.Source.DeviceGuid, input.DeviceGuid));
            }
            bool ReadWhereBlockRuns(ImportInput input, string blockLayer)
            {
                if (blockLayer != BaseLayer)
                {
                    var layer = _layers.FirstOrDefault(l => l.Mask == blockLayer);
                    return layer != null && ReadOnLayer(layer, input);
                }
                return _bindings.Any(b => b.Layer == BaseLayer && b.Source.Descriptor == input.Descriptor && Same(b.Source.DeviceGuid, input.DeviceGuid))
                       || _layers.Any(l => !l.Expand && ReadOnLayer(l, input));
            }
            // A layer that replaces Base closes Base's blocks, so it takes
            // its own copy of each one for a control it does not own, as it
            // takes Base's macros above.
            var blocks = new List<(ImportInput Input, string Name, string Layer)>(_blocks);
            foreach (var layer in _layers.Where(l => l.Expand))
            {
                var own = ownByLayer[layer];
                foreach (var b in _blocks)
                    if (b.Layer == BaseLayer && !own.Contains(b.Input.Control))
                        blocks.Add((b.Input, b.Name, layer.Mask));
            }
            var blockedReads = new HashSet<(string, string, string)>();
            foreach (var (input, blockName, blockLayer) in blocks)
            {
                if (ReadWhereBlockRuns(input, blockLayer)) continue;
                if (!blockedReads.Add((input.Descriptor, (input.DeviceGuid ?? "").ToLowerInvariant(), blockLayer))) continue;
                var data = ToMacroData(new ImportMacro
                {
                    Name = blockName,
                    Inputs = new[] { input },
                    ConsumesTrigger = true,
                }, blockLayer, 0, firstPadSlot);
                if (data != null) macros.Add(data);
            }

            ReportKeysTheSwitchHoldsBack(ownByLayer);

            var (entries, padSettings) = AssignMergedDevices(sets, created, macros);
            ReadsController = _bindings.Any(b => string.IsNullOrEmpty(b.Source.DeviceGuid)
                                                 && !b.Source.Descriptor.StartsWith("Menu ", StringComparison.Ordinal))
                              || _macros.Any(m => m.Inputs.Any(i => string.IsNullOrEmpty(i.DeviceGuid)));

            Report.XboxRowCount = 0;
            Report.KbmRowCount = kbmSlot >= 0 ? sets[kbmSlot].Rows.Count : 0;
            foreach (int slot in slotOfPad.Values) Report.XboxRowCount += sets[slot].Rows.Count;
            Report.MacroCount = macros.Count;
            Report.MenuCount = _menus.Count;
            Report.ShiftActivatorCount = _activators.Count;

            return new ProfileData
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Imported profile" : name.Trim(),
                ExecutableNames = executables ?? string.Empty,
                SlotCreated = created,
                SlotEnabled = enabled,
                SlotControllerTypes = types,
                SlotProfileIds = ids,
                SlotModel3DAppearances = SlotAppearancePersistence.Empty(),
                SlotMappingSets = sets,
                // Empty, never null: null is the legacy sentinel for "leave
                // the live macros alone", and this profile owns its state.
                Macros = macros.ToArray(),
                Entries = entries,
                PadSettings = padSettings,
                EnableDsuMotionServer = EnableDsu,
            };
        }

        /// <summary>A profile owns its slot assignments, and a row reads only
        /// a device on its slot. So each slot that reads the keyboard or the
        /// mouse claims the merged row for it, and a keyboard-to-gamepad
        /// profile works the moment it loads. The controller stays the user's
        /// to assign, as with any imported profile, because which one is
        /// theirs to say.</summary>
        private (ProfileEntry[], PadSetting[]) AssignMergedDevices(MappingSet[] sets, bool[] created, List<MacroData> macros)
        {
            var template = new PadSetting();
            template.UpdateChecksum();
            var entries = new List<ProfileEntry>();

            void Claim(string guid)
            {
                for (int slot = 0; slot < sets.Length; slot++)
                {
                    if (!created[slot]) continue;
                    var set = sets[slot];
                    bool reads = set.Rows.Any(r => r.Sources.Any(s => Same(s.DeviceGuid, guid)))
                        || set.ShiftActivators.Any(a => Same(a.DeviceGuid, guid) || Same(a.ChordSecondDeviceGuid, guid))
                        || macros.Any(m => m.PadIndex == slot && (m.TriggerInputs ?? "").Contains(guid, StringComparison.OrdinalIgnoreCase));
                    if (!reads) continue;
                    entries.Add(new ProfileEntry
                    {
                        InstanceGuid = Guid.Parse(guid),
                        // No product. Every keyboard shares one product guid,
                        // and the profile apply's same-product fallback would
                        // rebind these rows to the first physical keyboard
                        // with a setting of its own. The instance guid is the
                        // same on every PC, so it needs no fallback.
                        ProductGuid = Guid.Empty,
                        MapTo = slot,
                        PadSettingChecksum = template.PadSettingChecksum,
                    });
                }
            }
            Claim(AllKeyboardsGuid);
            Claim(AllMiceGuid);

            return entries.Count > 0
                ? (entries.ToArray(), new[] { template })
                : (null, null);
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>A row's sources with what repeats taken out. Abstract
        /// sources make a file's two controllers one input, and a file can map
        /// a stick direction to itself on top of the native function it keeps,
        /// which a whole-axis read already carries. On a summed row that would
        /// count twice.</summary>
        private static List<MappingSource> Distinct(IEnumerable<MappingSource> sources)
        {
            var kept = new List<MappingSource>();
            var all = sources.ToList();
            foreach (var s in all)
            {
                if (kept.Any(k => SameRead(k, s))) continue;
                bool carriedByWhole = s.HalfAxis && s.Sensitivity == 1.0 && all.Any(w =>
                    !w.HalfAxis && !w.Invert
                    && w.Descriptor == s.Descriptor
                    && Same(w.DeviceGuid, s.DeviceGuid)
                    // Each half keeps its own sign on the row: the lower half
                    // flips the output, the upper does not.
                    && s.Invert == s.InvertOutput);
                if (!carriedByWhole) kept.Add(s);
            }
            return kept;
        }

        private static bool SameRead(MappingSource a, MappingSource b)
            => a.Descriptor == b.Descriptor
               && Same(a.DeviceGuid, b.DeviceGuid)
               && a.HalfAxis == b.HalfAxis
               && a.Invert == b.Invert
               && a.InvertOutput == b.InvertOutput
               && a.DeadZone == b.DeadZone
               && a.Sensitivity == b.Sensitivity
               && a.GateDescriptor == b.GateDescriptor
               && a.Gate2Descriptor == b.Gate2Descriptor;

        private static MacroData ToMacroData(ImportMacro m, string layerMask, int pairId, int slot)
        {
            var specs = new List<string>(m.Inputs.Count);
            foreach (var input in m.Inputs)
            {
                string spec = input.ToTriggerEntry().Spec;
                // A trigger missing a leg would fire too easily, so the macro
                // does not ship.
                if (string.IsNullOrEmpty(spec)) return null;
                specs.Add(spec);
                // A gated input is one more entry: macro entries AND.
                if (input.Gate.Length > 0)
                    specs.Add(new MacroItem.TriggerInputEntry { SourceDescriptor = input.Gate }.Spec);
            }
            bool always = m.Mode == MacroTriggerMode.Always;
            if (specs.Count == 0 && !always) return null;

            return new MacroData
            {
                PadIndex = slot,
                Name = string.IsNullOrWhiteSpace(m.Name) ? "Imported macro" : m.Name,
                IsEnabled = true,
                TriggerSource = MacroTriggerSource.InputDevice,
                TriggerInputs = specs.Count > 0 ? string.Join("|", specs) : null,
                TriggerButtons = 0,
                TriggerAxisTargets = null,
                // Never consume, but for a block: a pair's legs read one
                // trigger, and a row may read it too. A block's key is one no
                // row reads.
                ConsumeTriggerButtons = m.ConsumesTrigger,
                TriggerMode = m.Mode,
                TriggerHoldMs = Math.Clamp(m.HoldMs, 50, 10000),
                TriggerDoublePressMs = Math.Clamp(m.DoublePressMs, 50, 5000),
                RepeatMode = m.Repeat,
                RepeatDelayMs = m.RepeatDelayMs,
                PairId = pairId,
                LayerMask = layerMask,
                Actions = m.Actions.ToArray(),
            };
        }
    }
}
