using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
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
            return src;
        }

        /// <summary>A macro trigger entry. Abstract and gesture descriptors
        /// ride the descriptor form, whose readers canonicalize them with the
        /// same per-device tuning a row gets. A named keyboard or mouse button
        /// rides the raw form the macro editor records.</summary>
        public MacroItem.TriggerInputEntry ToTriggerEntry()
        {
            Guid.TryParse(DeviceGuid ?? "", out Guid g);
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
            ImportOutputKind.PadStick or ImportOutputKind.PadTrigger => Scale == 1.0,
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

        public static string KeyName(byte vk)
            => Enum.IsDefined(typeof(VirtualKey), (int)vk)
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
        }

        private sealed class LayerInfo
        {
            public string Mask;
            public string Name;
            public bool Expand;
            public readonly HashSet<string> Blocked = new(StringComparer.Ordinal);
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
        public void Bind(string layer, ImportInput input, ImportOutput output, IReadOnlyList<ImportInput> gates = null)
        {
            var src = input.ToSource();
            if (output.Kind is ImportOutputKind.PadStick or ImportOutputKind.MouseAxis or ImportOutputKind.Wheel)
                SetOutputDirection(src, output.Negative);
            if (gates != null)
            {
                if (gates.Count > 0) src.GateDescriptor = gates[0].Descriptor;
                if (gates.Count > 1) src.Gate2Descriptor = gates[1].Descriptor;
            }
            Bind(layer, input.Control, output.Target, src, output.IsKbm ? 0 : output.PadId);
        }

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

        private HashSet<string> OwnControls(LayerInfo layer)
        {
            var own = new HashSet<string>(layer.Blocked, StringComparer.Ordinal);
            foreach (var b in _bindings)
                if (b.Layer == layer.Mask) own.Add(b.Control);
            foreach (var m in _macros)
                if (m.Layer == layer.Mask && m.Control != null) own.Add(m.Control);
            return own;
        }

        public ProfileData Finish(string name, string executables)
        {
            var ownByLayer = _layers.ToDictionary(l => l, OwnControls);

            foreach (var layer in _layers)
            {
                var own = ownByLayer[layer];
                if (_macros.Any(m => m.Layer == BaseLayer && !m.RunsBesideLayers && m.Control != null && own.Contains(m.Control)))
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
            bool needsKbm = _bindings.Any(b => b.PadId == 0) || (_macros.Count > 0 && !_pads.Any());

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

                foreach (var (slot, target) in targets)
                {
                    var sources = Distinct(kept.Concat(ownBindings)
                        .Where(b => SlotOfBinding(b.PadId) == slot && b.Target == target)
                        .Select(b => b.Source.Clone()));
                    var row = new MappingRow { Target = target, LayerMask = layer.Mask };
                    if (_sumRows.Contains(target)) row.CombineMode = "Sum";
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
                // Never consume: a pair's legs read one trigger, and a row
                // may read it too.
                ConsumeTriggerButtons = false,
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
