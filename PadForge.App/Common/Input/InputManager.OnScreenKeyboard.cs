using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;

namespace PadForge.Common.Input
{
    /// <summary>A click or a close from the keyboard window, for the poll thread.</summary>
    internal enum OskCommandKind { ClickKey, ClickWheel, Close }

    internal readonly record struct OskCommand(OskCommandKind Kind, int A = 0, int B = 0, int C = 0);

    /// <summary>
    /// The On-Screen Keyboard's runtime (#566). A keyboard menu, a menu of
    /// the Keyboard style on a slot, owns the keyboard while it is open. The
    /// controllers that drive the menu then drive only the keyboard: Step 2
    /// hands <c>ud.InputState</c> a copy of each one held at rest, so
    /// mappings, macros, other menus, touchpad gestures and layer holds all
    /// see it at rest, in every slot it feeds, with no gate of their own.
    /// Keyboard menus read the real state, which Step 2 passes beside the
    /// copy, so the menu's Opens With input can close the keyboard and
    /// another keyboard menu can take it over. After the keyboard closes, the
    /// copy holds only what was down at that moment, each input until it is
    /// let go, so the closing input never reaches a game. Poll thread only,
    /// apart from the volatile inputs and outputs marked below.
    /// </summary>
    public partial class InputManager
    {
        /// <summary>A stick's center in PadForge's 0 to 65535 axis range
        /// (SdlDeviceWrapper adds 32768 to SDL's signed value).</summary>
        private const int OskStickCenter = 32768;
        /// <summary>A trigger counts as let go under a tenth of its travel.</summary>
        private const int OskTriggerRest = 6554;
        /// <summary>A masked controller Step 2 has not read for this long is
        /// gone: it went offline. Every time here is on MenuClock, the menu
        /// runtime's clock, as a keyboard menu is a menu.</summary>
        private const long OskStaleMs = 300;
        /// <summary>An input read as a press is let go under this share of its
        /// press point, the engine's steering hysteresis, so a trigger resting
        /// at the press point does not chatter.</summary>
        private const float OskReleaseShare = OskEngine.SteerReleaseShare;
        /// <summary>A layer that ends this soon after it opened the keyboard
        /// ended because the keyboard held its activator at rest, not because
        /// the user left it. The mask reaches Step 3 one poll after the open.</summary>
        private const long OskLayerSettleMs = 100;
        /// <summary>How far past the press window a single press still acts,
        /// the macro trigger's SinglePressStaleGraceMs.</summary>
        private const long OskSinglePressGraceMs = 250;
        /// <summary>A keyboard menu no device drove for this long, and that
        /// does not own the keyboard, is forgotten.</summary>
        private const long OskMenuForgetMs = 2000;

        // ── From the UI (volatile) ─────────────────────────────

        private volatile IOskLayout _oskLayoutIn;
        private volatile int _oskRepeatDelay = 500, _oskRepeatInterval = 33;
        private readonly ConcurrentQueue<OskCommand> _oskCommands = new();

        internal void SetOnScreenKeyboardLayout(IOskLayout layout) => _oskLayoutIn = layout;
        internal void SetOnScreenKeyboardRepeat(int delayMs, int intervalMs)
        {
            _oskRepeatDelay = Math.Clamp(delayMs, 100, 2000);
            _oskRepeatInterval = Math.Clamp(intervalMs, 10, 1000);
        }
        internal void PostOnScreenKeyboardCommand(OskCommand command) => _oskCommands.Enqueue(command);

        // ── To the UI (volatile) ───────────────────────────────

        private volatile OskView _oskView;
        /// <summary>What the window draws, or null before the keyboard first opens.</summary>
        internal OskView OnScreenKeyboardView => _oskView;

        private readonly object _oskMoveLock = new();
        private float _oskMoveX, _oskMoveY;

        /// <summary>Hold-to-move waiting for the window, in percent of the
        /// work area, emptied by the read.</summary>
        internal (float X, float Y) TakeOnScreenKeyboardMove()
        {
            lock (_oskMoveLock)
            {
                var m = (_oskMoveX, _oskMoveY);
                _oskMoveX = _oskMoveY = 0;
                return m;
            }
        }

        // ── Poll thread ────────────────────────────────────────

        /// <summary>Set by tests before the keyboard first opens, so its keys
        /// land in a record instead of on the PC.</summary>
        internal IOskOutput OnScreenKeyboardOutputOverride { get; set; }

        private enum OskOpenReason { Input, Layer, TextFocus }

        private enum OskRequestKind { Open, Close }

        private readonly record struct OskRequest(int Slot, int MenuId, OskRequestKind Kind, OskOpenReason Reason);

        private OskEngine _osk;
        internal OskEngine OnScreenKeyboardEngine => _osk;
        private int _oskSlot = -1, _oskMenuId = -1;
        private MenuDefinitionEntry _oskDef;
        private OskOpenReason _oskReason;
        private long _oskOpenedMs;
        /// <summary>A layer that ended at the open: the keyboard held its
        /// activator at rest. The keyboard then stays open until it is closed
        /// some other way.</summary>
        private bool _oskLayerIgnored;
        private Guid[] _oskOwned = Array.Empty<Guid>();
        internal IReadOnlyList<Guid> OnScreenKeyboardOwners => _oskOwned;
        /// <summary>The slot and menu id of the keyboard menu that owns the
        /// keyboard, or (-1, -1) while it is closed.</summary>
        internal (int Slot, int MenuId) OnScreenKeyboardOwner => _osk?.IsOpen == true ? (_oskSlot, _oskMenuId) : (-1, -1);
        private OskFamily _oskFamily;
        private readonly Dictionary<Guid, OskMask> _oskMasks = new();
        private readonly OskControlState _oskState = new();
        private readonly bool[] _oskPrevDown = new bool[OskControlState.ActionCount];
        private readonly List<OskRequest> _oskRequests = new();
        private OskPredictor _oskPredictor;
        private int _oskAskedQuery = -1;
        private int _oskPublishedVersion = -1;
        private long _oskPublishedMs;
        private OskSettings _oskSettingsApplied;
        private MenuDefinitionEntry _oskSettingsDef;
        private IOskLayout _oskLayoutApplied;
        private bool _oskCapsSeen;

        /// <summary>A controller the keyboard holds at rest, or one whose
        /// inputs held at the close are kept silent until let go.</summary>
        private sealed class OskMask
        {
            public UserDevice Device;
            public bool Owned;
            public bool[] Buttons;
            public bool Pov, Left, Right, LT, RT, Touch;
            public PooledInputStatePair Pool;
            public CustomInputState Raw, Masked;
            public long RawAtMs;
            public readonly float[] Accel = new float[3], AccelAux = new float[3];
        }

        internal bool OnScreenKeyboardMasks(Guid device) => _oskMasks.ContainsKey(device);

        /// <summary>Step 2, for every device read: the state everything but
        /// the keyboard menus reads. The raw state of a masked controller is
        /// kept for the keyboard.</summary>
        internal CustomInputState OnScreenKeyboardFilter(UserDevice ud, CustomInputState raw)
        {
            if (raw == null || !_oskMasks.TryGetValue(ud.InstanceGuid, out var m)) return raw;
            m.Device = ud;
            m.Raw = raw;
            m.RawAtMs = MenuClock.NowMs;
            var s = m.Pool.Next();
            raw.CopyInto(s);
            if (m.Owned)
            {
                HoldAtRest(s, m);
                m.Masked = s;
                return s;
            }
            if (KeepHeldSilent(s, raw, m))
            {
                m.Masked = s;
                return s;
            }
            _oskMasks.Remove(ud.InstanceGuid);
            return raw;
        }

        private static void HoldAtRest(CustomInputState s, OskMask m)
        {
            Array.Clear(s.Buttons);
            for (int i = 0; i < s.Povs.Length; i++) s.Povs[i] = -1;
            s.Axis[0] = s.Axis[1] = s.Axis[3] = s.Axis[4] = OskStickCenter;
            s.Axis[2] = s.Axis[5] = 0;
            Array.Clear(s.Gyro);
            Array.Clear(s.GyroAux);
            Array.Copy(m.Accel, s.Accel, 3);
            Array.Copy(m.AccelAux, s.AccelAux, 3);
            ClearTouch(s);
            if (s.CapSense != null) Array.Clear(s.CapSense);
            if (s.NfcTag != null) Array.Clear(s.NfcTag);
            s.Ir = default;
            s.JoyCon2MouseDX = s.JoyCon2MouseDY = 0;
            s.MouseRawDX = s.MouseRawDY = 0;
        }

        private static void ClearTouch(CustomInputState s)
        {
            if (s.Touchpads == null) return;
            foreach (var tp in s.Touchpads) tp?.ResetForReuse();
            if (s.Buttons.Length > 16) s.Buttons[16] = false;
        }

        private static bool TouchActive(CustomInputState s)
        {
            if (s.Touchpads == null) return false;
            foreach (var tp in s.Touchpads)
            {
                if (tp == null) continue;
                if (tp.Clicked) return true;
                for (int f = 0; f < tp.MaxFingers; f++)
                    if (tp.FingerDown[f]) return true;
            }
            return false;
        }

        private static bool Deflected(int x, int y)
        {
            double dx = (x - OskStickCenter) / 32767.0, dy = (y - OskStickCenter) / 32767.0;
            return dx * dx + dy * dy > 0.25 * 0.25;
        }

        /// <summary>After the close: what was down at that moment stays at
        /// rest until it is let go. False once nothing is held any more.</summary>
        private static bool KeepHeldSilent(CustomInputState s, CustomInputState raw, OskMask m)
        {
            bool any = false;
            if (m.Buttons != null)
                for (int i = 0; i < m.Buttons.Length && i < raw.Buttons.Length; i++)
                {
                    if (!m.Buttons[i]) continue;
                    if (raw.Buttons[i]) { s.Buttons[i] = false; any = true; }
                    else m.Buttons[i] = false;
                }
            if (m.Pov)
            {
                if (raw.Povs[0] >= 0) { s.Povs[0] = -1; any = true; }
                else m.Pov = false;
            }
            if (m.Left)
            {
                if (Deflected(raw.Axis[0], raw.Axis[1])) { s.Axis[0] = s.Axis[1] = OskStickCenter; any = true; }
                else m.Left = false;
            }
            if (m.Right)
            {
                if (Deflected(raw.Axis[3], raw.Axis[4])) { s.Axis[3] = s.Axis[4] = OskStickCenter; any = true; }
                else m.Right = false;
            }
            if (m.LT)
            {
                if (raw.Axis[2] > OskTriggerRest) { s.Axis[2] = 0; any = true; }
                else m.LT = false;
            }
            if (m.RT)
            {
                if (raw.Axis[5] > OskTriggerRest) { s.Axis[5] = 0; any = true; }
                else m.RT = false;
            }
            if (m.Touch)
            {
                if (TouchActive(raw)) { ClearTouch(s); any = true; }
                else m.Touch = false;
            }
            return any;
        }

        // ── Keyboard menus ─────────────────────────────────────

        /// <summary>A keyboard menu as the runtime knows it: the devices that
        /// drove it this poll with their real states, and its inputs' last
        /// reads. Step 2 notes the devices, the tick reads them.</summary>
        private sealed class OskMenuRecord
        {
            public int Slot, MenuId;
            public MenuDefinitionEntry Def;
            public long Poll = -1;
            public readonly List<UserDevice> Drivers = new(4);
            public readonly List<CustomInputState> Raws = new(4);
            /// <summary>Per driver: whether it reads the device-free inputs,
            /// the "Gamepad ..." names and the steering inputs. A device that
            /// drives the menu only because a control names it reads that
            /// control alone, the #431 rule for non-gamepad rows.</summary>
            public readonly List<bool> AnyOk = new(4);
            public long SeenMs;
            public bool OpenWas, OpenSeen, LayerWas, LayerSeen;
            /// <summary>Opens With's press: when it went down, whether it has
            /// acted, and whether a control took the same press, which the
            /// keyboard keeps for itself.</summary>
            public long OpenDownMs;
            /// <summary>The last press of a double, triple or single press's
            /// chain, or long.MinValue, and the presses in the chain.</summary>
            public long OpenLastPressMs = long.MinValue;
            public int OpenStreak;
            public bool OpenFired, OpenShared;
            /// <summary>While Held: this hold opened the keyboard, so its
            /// release closes it.</summary>
            public bool OpenHeldOwner;
            public OskSources Sources;
        }

        private readonly Dictionary<(int Slot, int MenuId), OskMenuRecord> _oskMenus = new();
        private readonly List<(int Slot, int MenuId)> _oskForget = new();
        /// <summary>Counts ticks. A record's drivers belong to the poll they
        /// were noted in.</summary>
        private long _oskPoll;

        /// <summary>Step 2, from <see cref="UpdateMenuContexts"/>: a device on
        /// a slot that has a keyboard menu. Records the device as one that
        /// drives the menu this poll, with its real state.</summary>
        private void NoteKeyboardMenu(int slot, MenuDefinitionEntry def, UserDevice ud, CustomInputState raw)
        {
            if (def == null || ud == null || raw == null) return;
            var key = (slot, def.MenuId);
            if (!_oskMenus.TryGetValue(key, out var r))
                _oskMenus[key] = r = new OskMenuRecord { Slot = slot, MenuId = def.MenuId };
            if (!ReferenceEquals(r.Def, def))
            {
                r.Def = def;
                r.Sources = null;
                r.OpenSeen = r.LayerSeen = false;
            }
            if (r.Poll != _oskPoll)
            {
                r.Poll = _oskPoll;
                r.Drivers.Clear();
                r.Raws.Clear();
                r.AnyOk.Clear();
            }
            var src = SourcesOf(r);
            bool pinned = !string.IsNullOrEmpty(def.DeviceGuid);
            bool anyOk = pinned
                ? string.Equals(def.DeviceGuid, ud.InstanceGuidString, StringComparison.OrdinalIgnoreCase)
                : InputDeviceType.AnswersAnyDeviceSources(ud.CapType);
            if (!anyOk && !src.Named.Contains(ud.InstanceGuid)) return;
            r.Drivers.Add(ud);
            r.Raws.Add(raw);
            r.AnyOk.Add(anyOk);
            r.SeenMs = MenuClock.NowMs;
        }

        /// <summary>The layout whose controls a menu reads now: the one the
        /// keyboard shows while this menu owns it, else the saved one.</summary>
        private KeyboardLayout LayoutInUse(OskMenuRecord r)
            => _osk?.IsOpen == true && r.Slot == _oskSlot && r.MenuId == _oskMenuId
                ? (KeyboardLayout)_osk.Style
                : r.Def?.KeyboardLayout ?? KeyboardLayout.Grid;

        private OskSources SourcesOf(OskMenuRecord r)
        {
            var layout = LayoutInUse(r);
            if (r.Sources == null || !r.Sources.Matches(r.Def, layout))
                r.Sources = OskSources.Build(r.Def, layout);
            return r.Sources;
        }

        // ── Sources ────────────────────────────────────────────

        /// <summary>One input of a control: its source with the press point
        /// as its deadzone, the same source with the release point, the
        /// device it reads (empty for every driver that reads device-free
        /// inputs), and its press state, kept for the hysteresis.</summary>
        private sealed class OskLeg
        {
            public MappingSource Src, HeldSrc;
            public Guid Device;
            public string DeviceString;
            public bool Was;
        }

        /// <summary>A control's input: one leg, or two held together.</summary>
        private sealed class OskInputReader
        {
            public OskLeg A, B;
        }

        /// <summary>A keyboard menu's inputs for one layout, built from the
        /// menu and rebuilt when one of them changes. The UI thread replaces
        /// each field it edits, so reference compares find every edit.</summary>
        private sealed class OskSources
        {
            public MenuDefinitionEntry Def;
            public KeyboardLayout Layout;
            private object _controls;
            private string _open, _openDev, _openAnd, _openAndDev, _menuDevice;
            private int _openThreshold;

            public readonly OskInputReader[] Controls = new OskInputReader[OskControlState.ActionCount];
            public readonly int[] Shared = new int[OskControlState.ActionCount];
            /// <summary>Per control: its input holds every input of Opens
            /// With, so a press of Opens With is its press too. Opens With on
            /// X with Backspace on X types a Backspace, and Opens With on Back
            /// with Start still closes when Start is Enter.</summary>
            public readonly bool[] HoldsOpen = new bool[OskControlState.ActionCount];
            public OskInputReader Open;
            public readonly HashSet<Guid> Named = new();
            public readonly Dictionary<KeyboardControlAction, KeyboardInput> Bindings = new();

            public bool Matches(MenuDefinitionEntry def, KeyboardLayout layout)
                => ReferenceEquals(Def, def) && Layout == layout
                   && ReferenceEquals(_controls, def.KeyboardControls)
                   && ReferenceEquals(_open, def.OpenDescriptor) && ReferenceEquals(_openDev, def.OpenDeviceGuid)
                   && ReferenceEquals(_openAnd, def.OpenAndDescriptor) && ReferenceEquals(_openAndDev, def.OpenAndDeviceGuid)
                   && _openThreshold == def.OpenThreshold
                   && ReferenceEquals(_menuDevice, def.DeviceGuid);

            public static OskSources Build(MenuDefinitionEntry def, KeyboardLayout layout)
            {
                var s = new OskSources
                {
                    Def = def,
                    Layout = layout,
                    _controls = def.KeyboardControls,
                    _open = def.OpenDescriptor, _openDev = def.OpenDeviceGuid,
                    _openAnd = def.OpenAndDescriptor, _openAndDev = def.OpenAndDeviceGuid,
                    _openThreshold = def.OpenThreshold,
                    _menuDevice = def.DeviceGuid,
                };
                var inputs = new KeyboardInput[OskControlState.ActionCount];
                foreach (var a in KeyboardMenuDefaults.Actions(layout))
                {
                    var input = KeyboardMenuDefaults.Effective(def, layout, a);
                    if (input.IsEmpty) continue;
                    inputs[(int)a] = input;
                    s.Controls[(int)a] = s.Reader(input);
                    s.Bindings[a] = input;
                }
                // Key controls on one input share a number, so a tap control
                // on the input that also moves the keyboard fires on release.
                // Steering and pointers are read for depth and place, and take
                // no part.
                for (int i = 0; i < inputs.Length; i++)
                {
                    if (s.Controls[i] == null || s.Shared[i] != 0 || !KeyboardMenuDefaults.IsKey((KeyboardControlAction)i)) continue;
                    for (int j = i + 1; j < inputs.Length; j++)
                        if (s.Controls[j] != null && KeyboardMenuDefaults.IsKey((KeyboardControlAction)j)
                            && inputs[i].SameSource(inputs[j]))
                            s.Shared[i] = s.Shared[j] = i + 1;
                }
                var open = KeyboardMenuDefaults.OpenInput(def);
                s.Open = open.IsEmpty ? null : s.Reader(open);
                if (s.Open != null)
                    for (int i = 0; i < inputs.Length; i++)
                        s.HoldsOpen[i] = s.Controls[i] != null && KeyboardMenuDefaults.IsKey((KeyboardControlAction)i)
                                         && Holds(inputs[i], open);
                return s;
            }

            /// <summary>True when each input of <paramref name="open"/> is one
            /// of <paramref name="control"/>'s, on the same device.</summary>
            private static bool Holds(KeyboardInput control, KeyboardInput open)
            {
                static bool Eq(string a, string b) =>
                    string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
                bool Has(string d, string g) =>
                    (Eq(control.Descriptor, d) && Eq(control.DeviceGuid, g))
                    || (control.HasAnd && Eq(control.AndDescriptor, d) && Eq(control.AndDeviceGuid, g));
                return Has(open.Descriptor, open.DeviceGuid)
                       && (!open.HasAnd || Has(open.AndDescriptor, open.AndDeviceGuid));
            }

            private OskInputReader Reader(in KeyboardInput input)
            {
                int press = input.ThresholdPercent;
                return new OskInputReader
                {
                    A = Leg(input.Descriptor, input.DeviceGuid, press),
                    B = input.HasAnd ? Leg(input.AndDescriptor, input.AndDeviceGuid, press) : null,
                };
            }

            private OskLeg Leg(string descriptor, string device, int press)
            {
                Guid g = Guid.Empty;
                if (!string.IsNullOrWhiteSpace(device) && Guid.TryParse(device.Trim(), out var parsed))
                {
                    g = parsed;
                    Named.Add(g);
                }
                return new OskLeg
                {
                    Src = SourceOf(descriptor, press),
                    HeldSrc = SourceOf(descriptor, ReleasePercent(press)),
                    Device = g,
                    DeviceString = g == Guid.Empty ? null : g.ToString().ToLowerInvariant(),
                };
            }

            /// <summary>Where a held press lets go: 70% of the way to the
            /// press point, at least 1%.</summary>
            internal static int ReleasePercent(int press)
                => Math.Max(1, (int)MathF.Round(press * OskReleaseShare));

            /// <summary>A descriptor in the legacy I/H prefix form as a
            /// source with the Invert and HalfAxis flags the reads take, and
            /// <paramref name="deadZone"/> as the press point of its button
            /// read (MappingSource.DeadZone, a Mappings row's own).</summary>
            internal static MappingSource SourceOf(string descriptor, int deadZone = KeyboardInput.DefaultThreshold)
            {
                string clean = SourceCoercion.StripLegacyPrefix((descriptor ?? "").Trim(), out bool invert, out bool half);
                return new MappingSource { Descriptor = clean, Invert = invert, HalfAxis = half, DeadZone = deadZone };
            }
        }

        // ── Reading a menu's inputs ────────────────────────────

        /// <summary>Whether a leg reads driver <paramref name="i"/>. A leg
        /// that names a device reads that device. A device-free leg reads
        /// every driver that answers device-free inputs and has what the leg
        /// reads, the #431 rule mapping rows and menus keep: a mouse's wheel
        /// is no half-pulled trigger, and a device with no stick holds 0 on
        /// its axes, which would read as a full push.</summary>
        private static bool LegReads(OskMenuRecord r, OskLeg leg, int i)
        {
            var ud = r.Drivers[i];
            if (leg.Device != Guid.Empty) return ud.InstanceGuid == leg.Device;
            return r.AnyOk[i] && HasAxesFor(ud, leg.Src, r.Raws[i]);
        }

        /// <summary>A leg's depth across the drivers it reads, 0 to 1: the
        /// read a Mappings row with a trigger target takes, a button 0 or 1,
        /// a half axis its travel, a trigger its pull. An input that read
        /// has no depth for (a zone, a stick sector, a two-stage pull, IR
        /// Offscreen) counts 1 while its button read holds.</summary>
        private static float LegLevel(OskMenuRecord r, OskLeg leg)
        {
            if (leg?.Src == null) return 0f;
            float level = 0f;
            for (int i = 0; i < r.Drivers.Count; i++)
            {
                if (!LegReads(r, leg, i)) continue;
                string dev = r.Drivers[i].InstanceGuidString;
                float v = SourceCoercion.EvaluateForTriggerTarget(r.Raws[i], leg.Src, r.Slot, dev);
                if (v <= 0f && SourceCoercion.EvaluateForButtonTarget(r.Raws[i], leg.Src, KeyboardInput.DefaultThreshold, r.Slot, dev))
                    v = 1f;
                if (v > level) level = v;
            }
            return level;
        }

        /// <summary>A leg's press across the drivers it reads: the read a
        /// Mappings row with a button target takes
        /// (SourceCoercion.EvaluateForButtonTarget), so every input that
        /// row can press with presses here, at the leg's press point, and
        /// at its release point while held, so an analog input resting at
        /// the point does not type twice.</summary>
        private static bool LegHeld(OskMenuRecord r, OskLeg leg)
        {
            if (leg?.Src == null) return false;
            var src = leg.Was ? leg.HeldSrc : leg.Src;
            bool held = false;
            for (int i = 0; i < r.Drivers.Count && !held; i++)
                held = LegReads(r, leg, i)
                       && SourceCoercion.EvaluateForButtonTarget(r.Raws[i], src, KeyboardInput.DefaultThreshold,
                           r.Slot, r.Drivers[i].InstanceGuidString);
            leg.Was = held;
            return held;
        }

        private static bool InputHeld(OskMenuRecord r, OskInputReader input)
        {
            if (input?.A == null) return false;
            // Both legs read every poll, so each keeps its hysteresis.
            bool a = LegHeld(r, input.A);
            bool b = input.B == null || LegHeld(r, input.B);
            return a && b;
        }

        /// <summary>A steering control's depth, 0 to 1: its input's level, the
        /// second input of a combination held as a gate.</summary>
        private static float InputLevel(OskMenuRecord r, OskInputReader input)
        {
            if (input?.A == null) return 0f;
            bool gate = input.B == null || LegHeld(r, input.B);
            float level = LegLevel(r, input.A);
            return gate ? level : 0f;
        }

        /// <summary>A steering input from its four direction controls, -1 to 1
        /// with +Y down, the SDL frame the engine steers in.</summary>
        private static (float X, float Y) SteerOf(OskMenuRecord r, OskSources s,
            KeyboardControlAction up, KeyboardControlAction down, KeyboardControlAction left, KeyboardControlAction right)
        {
            float x = InputLevel(r, s.Controls[(int)right]) - InputLevel(r, s.Controls[(int)left]);
            float y = InputLevel(r, s.Controls[(int)down]) - InputLevel(r, s.Controls[(int)up]);
            return (Math.Clamp(x, -1f, 1f), Math.Clamp(y, -1f, 1f));
        }

        /// <summary>Whether a driver has a pointer's position input: a
        /// touchpad by its number, any other input by the #431 test.</summary>
        private static bool HasPointerInput(OskMenuRecord r, OskLeg leg, int i)
        {
            if (!LegReads(r, leg, i)) return false;
            string d = leg.Src.Descriptor ?? "";
            if (d.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                int end = d.IndexOf(' ', 9);
                if (end < 0 || !int.TryParse(d.AsSpan(9, end - 9), out int pad)) return false;
                var pads = r.Raws[i].Touchpads;
                return pads != null && pad >= 0 && pad < pads.Length && pads[pad] != null;
            }
            return true;
        }

        /// <summary>A pointer, or null when no driver has its position: a
        /// controller with one touchpad points with the whole board, not
        /// half of it. Its place comes from the first driver it reads whose
        /// touch input is held there, read across the whole range of its
        /// two position inputs, 0 to 1.</summary>
        private static OskTouch? PointerOf(OskMenuRecord r, OskSources s,
            KeyboardControlAction ax, KeyboardControlAction ay, KeyboardControlAction at, KeyboardControlAction ac)
        {
            var xr = s.Controls[(int)ax]?.A;
            if (xr?.Src == null) return null;
            int first = -1, touching = -1;
            var touchReader = s.Controls[(int)at];
            for (int i = 0; i < r.Drivers.Count; i++)
            {
                if (!HasPointerInput(r, xr, i)) continue;
                if (first < 0) first = i;
                var t = touchReader?.A;
                if (t?.Src != null && LegReads(r, t, i)
                    && SourceCoercion.EvaluateForButtonTarget(r.Raws[i], t.Src, KeyboardInput.DefaultThreshold,
                        r.Slot, r.Drivers[i].InstanceGuidString))
                {
                    touching = i;
                    break;
                }
            }
            if (first < 0) return null;
            bool down = InputHeld(r, touchReader);
            bool click = InputHeld(r, s.Controls[(int)ac]);
            if (!down && !click) return default(OskTouch);
            int at0 = touching >= 0 ? touching : first;
            float Axis(OskLeg leg)
            {
                if (leg?.Src == null) return 0f;
                int i = LegReads(r, leg, at0) ? at0 : -1;
                for (int k = 0; i < 0 && k < r.Drivers.Count; k++)
                    if (LegReads(r, leg, k)) i = k;
                return i < 0 ? 0f
                    : SourceCoercion.EvaluateForBipolarAxisTarget(r.Raws[i], leg.Src, r.Slot, false, r.Drivers[i].InstanceGuidString);
            }
            float x = Axis(xr), y = Axis(s.Controls[(int)ay]?.A);
            return new OskTouch(down, Math.Clamp((x + 1f) / 2f, 0f, 1f), Math.Clamp((y + 1f) / 2f, 0f, 1f), click);
        }

        /// <summary>The keyboard's controls this poll, from the owner menu's
        /// inputs on the real states of the devices that drive it.</summary>
        private void ReadControls(OskMenuRecord r, OskControlState into)
        {
            into.Clear();
            var src = SourcesOf(r);
            for (int i = 0; i < OskControlState.ActionCount; i++)
            {
                if (!KeyboardMenuDefaults.IsKey((KeyboardControlAction)i)) continue;
                into.Down[i] = InputHeld(r, src.Controls[i]);
                into.Shared[i] = src.Shared[i];
            }
            (into.SX, into.SY) = SteerOf(r, src, KeyboardControlAction.SteerUp, KeyboardControlAction.SteerDown,
                KeyboardControlAction.SteerLeft, KeyboardControlAction.SteerRight);
            (into.S2X, into.S2Y) = SteerOf(r, src, KeyboardControlAction.Steer2Up, KeyboardControlAction.Steer2Down,
                KeyboardControlAction.Steer2Left, KeyboardControlAction.Steer2Right);
            var p1 = PointerOf(r, src, KeyboardControlAction.Pointer1X, KeyboardControlAction.Pointer1Y,
                KeyboardControlAction.Pointer1Touch, KeyboardControlAction.Pointer1Click);
            var p2 = PointerOf(r, src, KeyboardControlAction.Pointer2X, KeyboardControlAction.Pointer2Y,
                KeyboardControlAction.Pointer2Touch, KeyboardControlAction.Pointer2Click);
            if (p1 is { } a && p2 is { } b) into.Touch = new[] { a, b };
            else if (p1 is { } c) into.Touch = new[] { c };
            else if (p2 is { } d) into.Touch = new[] { d };
        }

        // ── The tick ───────────────────────────────────────────

        private OskEngine EnsureOsk()
        {
            if (_osk != null) return _osk;
            var windows = OnScreenKeyboardOutputOverride ?? new OskWindowsOutput(
                ForegroundOrAppliedLayout,
                () => _oskSlot < 0 || !IsSlotRestricted(_oskSlot));
            _oskWindowsOut = windows;
            _osk = new OskEngine(new OskRouter(this, windows));
            return _osk;
        }

        private IntPtr ForegroundOrAppliedLayout()
            => (_oskLayoutApplied as OskWindowsLayout)?.Handle is { } h && h != IntPtr.Zero
                ? h : OskWindowsKeyboard.ForegroundLayout();

        /// <summary>Once per poll, after the macro pass. Reads every keyboard
        /// menu's Opens With and layer, takes the text-focus state and the
        /// window's clicks, runs the engine for the menu that owns it, routes
        /// its keys, and publishes the view. <paramref name="step5Runs"/> is
        /// false on the idle path, where no slot's keyboard is fed this poll.</summary>
        internal void TickOnScreenKeyboard(bool step5Runs = true)
        {
            long now = MenuClock.NowMs;
            _oskStep5Runs = step5Runs;
            RefreshKeyboardMenus(now);
            ReconcileMenuKeyboards(now);
            bool open = _osk?.IsOpen == true;
            bool busy = open || _oskMenus.Count > 0 || !_oskCommands.IsEmpty || _oskMasks.Count > 0
                || _oskOut.Count > 0 || TextFocusChanged;
            if (!busy)
            {
                _oskPoll++;
                return;
            }
            var e = EnsureOsk();
            e.RepeatDelayMs = _oskRepeatDelay;
            e.RepeatIntervalMs = _oskRepeatInterval;
            ApplyLayout(e);

            // What can switch the layout goes before the controls are read:
            // the window's clicks, on the keys it drew, then the owner menu's
            // settings. The controls are then read for the layout the engine
            // ticks with, and a switch guards what the new layout reads.
            while (_oskCommands.TryDequeue(out var c))
            {
                switch (c.Kind)
                {
                    case OskCommandKind.ClickKey: e.ClickKey(c.A, c.B, c.C); break;
                    case OskCommandKind.ClickWheel: e.ClickWheel(c.A, c.B); break;
                    case OskCommandKind.Close: CloseKeyboard(); break;
                }
            }
            if (e.IsOpen) ApplyOwnerSettings(e, force: false);
            open = e.IsOpen;

            // The owner's controls next: a press of Opens With that a control
            // reading the same input takes too, on the same poll, is the
            // control's.
            bool openTaken = false;
            if (open && _oskMenus.TryGetValue((_oskSlot, _oskMenuId), out var owner))
            {
                Array.Copy(_oskState.Down, _oskPrevDown, OskControlState.ActionCount);
                if (owner.Poll == _oskPoll) ReadControls(owner, _oskState);
                else _oskState.Clear();
                var held = SourcesOf(owner).HoldsOpen;
                for (int i = 0; i < OskControlState.ActionCount; i++)
                    if (held[i] && _oskState.Down[i] && !_oskPrevDown[i]) { openTaken = true; break; }
            }

            ReadMenuInputs(now, openTaken);
            ReadTextFocus(now);

            for (int i = 0; i < _oskRequests.Count; i++)
                HandleKeyboardRequest(_oskRequests[i], now);
            _oskRequests.Clear();

            // A controller Step 2 stopped reading went offline. Its mask goes,
            // whether the keyboard owns it or it only keeps a held input
            // silent, and the keyboard closes when the last one it owns goes.
            if (_oskMasks.Count > 0) DropStaleMasks(now);
            if (e.IsOpen) TrackOwned(now);
            // The keyboard types on this PC, so a restricted Remote Link peer
            // that starts feeding its slot closes it, as one there keeps it
            // from opening. The macro pass checks the slot each poll too.
            if (e.IsOpen && IsSlotRestricted(_oskSlot)) CloseKeyboard();

            if (e.IsOpen)
            {
                e.Tick(_oskState, now);
                var move = e.TakeMove();
                if (move.X != 0 || move.Y != 0)
                    lock (_oskMoveLock) { _oskMoveX += move.X; _oskMoveY += move.Y; }
                if (e.CloseRequested) CloseKeyboard();
            }

            if (e.IsOpen && e.Settings.Suggestions)
            {
                var q = e.SuggestionQuery;
                if (q.Version != _oskAskedQuery)
                {
                    _oskAskedQuery = q.Version;
                    if (q.Word.Length > 0 || q.Previous.Length > 0)
                        (_oskPredictor ??= new OskPredictor()).Request(_oskLayoutApplied?.LanguageTag, q.Word, q.Previous, q.Version);
                }
                if (_oskPredictor != null && _oskPredictor.TryTake(out int v, out var words))
                    e.SetSuggestions(v, words);
            }

            if (e.Version != _oskPublishedVersion || now - _oskPublishedMs >= 100)
            {
                var vs = e.IsOpen && _oskMenus.TryGetValue((_oskSlot, _oskMenuId), out var ow) ? ow.Sources : null;
                _oskView = OskView.Build(e, _oskFamily, now, e.IsOpen ? _oskSlot : -1, e.IsOpen ? _oskMenuId : -1,
                    vs?.Bindings);
                _oskPublishedVersion = e.Version;
                _oskPublishedMs = now;
            }
            ApplyOutput();
            ForgetIdleMenus(now);
            _oskPoll++;
        }

        private void ApplyLayout(OskEngine e)
        {
            var layout = _oskLayoutIn;
            if (layout != null && !ReferenceEquals(layout, _oskLayoutApplied))
            {
                e.SetLayout(layout);
                _oskLayoutApplied = layout;
                _oskCapsSeen = layout.CapsOn;
            }
            else if (layout != null && layout.CapsOn != _oskCapsSeen)
            {
                _oskCapsSeen = layout.CapsOn;
                e.NoteCapsChanged();
            }
        }

        /// <summary>The owner menu's layout, function row, suggestions,
        /// symbols and steering deadzone, as engine settings. A new object
        /// only when one changed.
        /// The keyboard's own layout key and Fn key change the engine first,
        /// and the window saves them to the menu.</summary>
        private void ApplyOwnerSettings(OskEngine e, bool force)
        {
            var def = _oskDef;
            if (def == null) return;
            var cur = _oskSettingsApplied;
            var style = (OskStyle)Math.Clamp((int)def.KeyboardLayout, 0, 2);
            string symbols = def.Symbols ?? "";
            float deadzone = Math.Clamp(def.EngageDeadzonePercent, 1, 95) / 100f;
            if (!force && cur != null && ReferenceEquals(_oskSettingsDef, def) && cur.Style == style
                && cur.FunctionRow == def.FunctionRow && cur.Suggestions == def.Suggestions
                && string.Equals(cur.Symbols, symbols, StringComparison.Ordinal)
                && cur.SteerDeadzone == deadzone)
                return;
            var s = new OskSettings
            {
                Style = style,
                FunctionRow = def.FunctionRow,
                Suggestions = def.Suggestions,
                Symbols = symbols,
                SteerDeadzone = deadzone,
            };
            e.ApplySettings(s, force);
            _oskSettingsApplied = s;
            _oskSettingsDef = def;
        }

        /// <summary>Each keyboard menu's Opens With press, and its layer for a
        /// menu its layer holds open. A press while the menu owns the
        /// keyboard closes it, unless a control on the same input took the
        /// press (<paramref name="openTaken"/>). A press elsewhere opens it
        /// for that menu, in its layer.</summary>
        private void ReadMenuInputs(long now, bool openTaken)
        {
            var sets = SettingsManager.SlotMappingSets;
            foreach (var r in _oskMenus.Values)
            {
                if (r.Poll != _oskPoll || r.Def == null || !r.Def.Enabled) continue;
                var src = SourcesOf(r);
                bool isOwner = _osk?.IsOpen == true && r.Slot == _oskSlot && r.MenuId == _oskMenuId;
                MappingSet set = sets != null && r.Slot >= 0 && r.Slot < sets.Length ? sets[r.Slot] : null;
                bool layerOk = IsMenuLayerOpen(r.Slot, set, r.Def);

                // Opens With acts once per press, by the macro trigger's rule
                // for its press mode (EvaluateHoldForMsTrigger,
                // EvaluateShortPressTrigger, EvaluateDoublePressTrigger,
                // EvaluateTriplePressTrigger, EvaluateSinglePressTrigger). An
                // input held when the menu is first seen waits for its
                // release.
                bool held = InputHeld(r, src.Open);
                if (!r.OpenSeen)
                {
                    r.OpenSeen = true;
                    r.OpenFired = held;
                }
                else
                {
                    bool rose = held && !r.OpenWas, fell = !held && r.OpenWas;
                    if (rose)
                    {
                        r.OpenDownMs = now;
                        r.OpenFired = false;
                        r.OpenShared = isOwner && openTaken;
                    }
                    int ms = KeyboardMenuDefaults.OpenPressMs(r.Def);
                    bool act = false;
                    switch (r.Def.OpenPress)
                    {
                        case KeyboardOpenPress.Hold:
                            act = held && !r.OpenFired && now - r.OpenDownMs >= ms;
                            break;
                        case KeyboardOpenPress.ShortPress:
                            act = fell && !r.OpenFired && now - r.OpenDownMs < ms;
                            break;
                        case KeyboardOpenPress.Release:
                            act = fell && !r.OpenFired;
                            break;
                        case KeyboardOpenPress.DoublePress:
                            if (rose)
                            {
                                act = r.OpenLastPressMs != long.MinValue && now - r.OpenLastPressMs <= ms;
                                r.OpenLastPressMs = act ? long.MinValue : now;
                            }
                            break;
                        case KeyboardOpenPress.TriplePress:
                            if (rose)
                            {
                                bool chained = r.OpenLastPressMs != long.MinValue && now - r.OpenLastPressMs <= ms;
                                r.OpenStreak = chained ? r.OpenStreak + 1 : 1;
                                r.OpenLastPressMs = now;
                                if (r.OpenStreak >= 3)
                                {
                                    act = true;
                                    r.OpenStreak = 0;
                                    r.OpenLastPressMs = long.MinValue;
                                }
                            }
                            break;
                        case KeyboardOpenPress.SinglePress:
                            if (rose)
                            {
                                bool chained = r.OpenLastPressMs != long.MinValue && now - r.OpenLastPressMs <= ms;
                                r.OpenStreak = chained ? r.OpenStreak + 1 : 1;
                                r.OpenLastPressMs = now;
                            }
                            else if (r.OpenLastPressMs != long.MinValue && now - r.OpenLastPressMs > ms)
                            {
                                long elapsed = now - r.OpenLastPressMs;
                                if (r.OpenStreak == 1)
                                {
                                    r.OpenStreak = 0;
                                    r.OpenLastPressMs = long.MinValue;
                                    // A window that ended long ago predates a
                                    // stop: no ghost press.
                                    act = elapsed <= ms + OskSinglePressGraceMs;
                                }
                                else if (!held)
                                {
                                    r.OpenStreak = 0;
                                    r.OpenLastPressMs = long.MinValue;
                                }
                            }
                            break;
                        case KeyboardOpenPress.WhileHeld:
                            // Open on the press, closed on the release of
                            // the press that opened it.
                            if (rose && !isOwner && layerOk)
                            {
                                r.OpenHeldOwner = true;
                                _oskRequests.Add(new OskRequest(r.Slot, r.MenuId, OskRequestKind.Open, OskOpenReason.Input));
                            }
                            else if (fell)
                            {
                                if (isOwner && r.OpenHeldOwner)
                                    _oskRequests.Add(new OskRequest(r.Slot, r.MenuId, OskRequestKind.Close, OskOpenReason.Input));
                                r.OpenHeldOwner = false;
                            }
                            break;
                        default:
                            act = rose;
                            break;
                    }
                    if (act)
                    {
                        r.OpenFired = true;
                        if (isOwner)
                        {
                            if (!r.OpenShared)
                                _oskRequests.Add(new OskRequest(r.Slot, r.MenuId, OskRequestKind.Close, OskOpenReason.Input));
                        }
                        else if (layerOk)
                            _oskRequests.Add(new OskRequest(r.Slot, r.MenuId, OskRequestKind.Open, OskOpenReason.Input));
                    }
                }
                r.OpenWas = held;

                string mask = r.Def.LayerMask ?? "";
                bool holds = r.Def.LayerHoldsOpen && mask.Length > 0 && mask != "Base";
                if (holds && r.LayerSeen && layerOk != r.LayerWas)
                {
                    if (layerOk)
                        _oskRequests.Add(new OskRequest(r.Slot, r.MenuId, OskRequestKind.Open, OskOpenReason.Layer));
                    else if (isOwner && !_oskLayerIgnored)
                    {
                        // However the keyboard opened, a layer held by an
                        // input it now holds at rest ends at once. That end
                        // is the keyboard's own, so it stays open.
                        if (now - _oskOpenedMs <= OskLayerSettleMs)
                            _oskLayerIgnored = true;
                        else
                            _oskRequests.Add(new OskRequest(r.Slot, r.MenuId, OskRequestKind.Close, OskOpenReason.Layer));
                    }
                }
                r.LayerWas = layerOk;
                r.LayerSeen = true;
            }
        }

        private void HandleKeyboardRequest(in OskRequest q, long now)
        {
            var e = EnsureOsk();
            bool mine = e.IsOpen && q.Slot == _oskSlot && q.MenuId == _oskMenuId;
            switch (q.Kind)
            {
                case OskRequestKind.Close:
                    if (mine) CloseKeyboard();
                    break;
                case OskRequestKind.Open:
                    if (!mine) OpenKeyboard(q.Slot, q.MenuId, q.Reason, now);
                    break;
            }
        }

        /// <summary>Opens the keyboard for a keyboard menu, or hands it over
        /// from the menu that has it. A slot fed by a restricted Remote Link
        /// peer cannot open it, since the keyboard types on this PC.</summary>
        private void OpenKeyboard(int slot, int menuId, OskOpenReason reason, long now)
        {
            if (slot < 0 || slot >= MaxPads || IsSlotRestricted(slot)) return;
            var sets = SettingsManager.SlotMappingSets;
            var def = sets != null && slot < sets.Length ? FindMenu(sets[slot], menuId) : null;
            if (def == null || !def.Enabled || def.Kind != MenuKind.Keyboard) return;
            var e = EnsureOsk();
            _oskMenus.TryGetValue((slot, menuId), out var r);
            bool noted = r != null && r.Poll == _oskPoll;
            var guids = new List<Guid>();
            if (noted)
                foreach (var ud in r.Drivers)
                    if (!guids.Contains(ud.InstanceGuid)) guids.Add(ud.InstanceGuid);
            // The controllers of the menu that had it are let go, each held
            // input kept silent until released.
            foreach (var g in _oskOwned)
                if (!guids.Contains(g)) ReleaseToGuard(g);
            if (noted)
                for (int i = 0; i < r.Drivers.Count; i++) Own(r.Drivers[i], r.Raws[i], now);
            _oskOwned = guids.ToArray();
            _oskSlot = slot;
            _oskMenuId = menuId;
            _oskDef = def;
            _oskReason = reason;
            _oskOpenedMs = now;
            _oskLayerIgnored = false;
            _oskFamily = FamilyOf(_oskOwned);
            ApplyOwnerSettings(e, force: true);
            // What the controllers hold now reads nothing until let go, so
            // the opener's own press types nothing.
            var held = new OskControlState();
            if (noted) ReadControls(r, held);
            _oskState.CopyFrom(held);
            Array.Copy(held.Down, _oskPrevDown, OskControlState.ActionCount);
            e.Open(held);
            _oskAskedQuery = -1;
            ResetOutput();
        }

        /// <summary>Starts holding a controller at rest.</summary>
        private void Own(UserDevice ud, CustomInputState raw, long now)
        {
            var g = ud.InstanceGuid;
            if (!_oskMasks.TryGetValue(g, out var m)) _oskMasks[g] = m = new OskMask();
            m.Device = ud;
            m.Raw ??= raw;
            m.Owned = true;
            m.Buttons = null;
            m.Pov = m.Left = m.Right = m.LT = m.RT = m.Touch = false;
            m.RawAtMs = now;
            var s = raw ?? ud.InputState;
            if (s != null)
            {
                Array.Copy(s.Accel, m.Accel, 3);
                Array.Copy(s.AccelAux, m.AccelAux, 3);
            }
        }

        /// <summary>While open: a device that starts driving the owner menu,
        /// one that came online, is held at rest too, and the keyboard closes
        /// when the last controller it held went offline.</summary>
        private void TrackOwned(long now)
        {
            if (_oskMenus.TryGetValue((_oskSlot, _oskMenuId), out var r) && r.Poll == _oskPoll)
            {
                for (int i = 0; i < r.Drivers.Count; i++)
                {
                    var g = r.Drivers[i].InstanceGuid;
                    if (Array.IndexOf(_oskOwned, g) >= 0) continue;
                    Own(r.Drivers[i], r.Raws[i], now);
                    var grown = new Guid[_oskOwned.Length + 1];
                    _oskOwned.CopyTo(grown, 0);
                    grown[^1] = g;
                    _oskOwned = grown;
                    if (_oskOwned.Length == 1) _oskFamily = FamilyOf(_oskOwned);
                }
            }
            if (_oskOwned.Length == 0) return;
            int live = 0;
            foreach (var g in _oskOwned)
                if (_oskMasks.TryGetValue(g, out var m) && m.Owned) live++;
            if (live == _oskOwned.Length) return;
            var kept = new Guid[live];
            int k = 0;
            foreach (var g in _oskOwned)
                if (_oskMasks.TryGetValue(g, out var m) && m.Owned) kept[k++] = g;
            _oskOwned = kept;
            if (live == 0) CloseKeyboard();
        }

        private void CloseKeyboard()
        {
            foreach (var g in _oskOwned) ReleaseToGuard(g);
            _oskOwned = Array.Empty<Guid>();
            _oskSlot = _oskMenuId = -1;
            _oskDef = null;
            _oskLayerIgnored = false;
            _osk?.Close();
        }

        private readonly List<Guid> _oskStale = new();

        private void DropStaleMasks(long now)
        {
            _oskStale.Clear();
            foreach (var kv in _oskMasks)
                if (now - kv.Value.RawAtMs > OskStaleMs) _oskStale.Add(kv.Key);
            foreach (var g in _oskStale) _oskMasks.Remove(g);
        }

        /// <summary>Stops holding a controller at rest. What it holds right
        /// now stays silent until let go.</summary>
        private void ReleaseToGuard(Guid g)
        {
            if (!_oskMasks.TryGetValue(g, out var m)) return;
            var raw = m.Raw;
            if (raw == null) { _oskMasks.Remove(g); return; }
            m.Owned = false;
            m.Buttons = (bool[])raw.Buttons.Clone();
            m.Pov = raw.Povs[0] >= 0;
            m.Left = Deflected(raw.Axis[0], raw.Axis[1]);
            m.Right = Deflected(raw.Axis[3], raw.Axis[4]);
            m.LT = raw.Axis[2] > OskTriggerRest;
            m.RT = raw.Axis[5] > OskTriggerRest;
            m.Touch = TouchActive(raw);
            bool any = m.Pov || m.Left || m.Right || m.LT || m.RT || m.Touch;
            for (int i = 0; !any && i < m.Buttons.Length; i++) any = m.Buttons[i];
            if (!any)
            {
                _oskMasks.Remove(g);
                // Put the real state back now rather than a tick late.
                if (m.Device != null && m.Raw != null) m.Device.InputState = m.Raw;
            }
        }

        /// <summary>A keyboard menu no device drove for a while is forgotten,
        /// unless it owns the keyboard. One that is no longer on its slot,
        /// is off, or changed style closes the keyboard it owns.</summary>
        private void ForgetIdleMenus(long now)
        {
            if (_osk?.IsOpen == true)
            {
                var def = _oskDef;
                var sets = SettingsManager.SlotMappingSets;
                bool current = def != null && def.Enabled && def.Kind == MenuKind.Keyboard
                    && sets != null && IsCurrentMenuOwner(sets, _oskSlot, _oskMenuId, def);
                if (!current) CloseKeyboard();
            }
            if (_oskMenus.Count == 0) return;
            _oskForget.Clear();
            foreach (var kv in _oskMenus)
            {
                bool owner = _osk?.IsOpen == true && kv.Key.Slot == _oskSlot && kv.Key.MenuId == _oskMenuId;
                if (!owner && now - kv.Value.SeenMs > OskMenuForgetMs) _oskForget.Add(kv.Key);
            }
            foreach (var k in _oskForget) _oskMenus.Remove(k);
        }

        private static UserDevice FindUserDevice(Guid g)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return null;
            lock (devices.SyncRoot)
                foreach (var ud in devices.Items)
                    if (ud.InstanceGuid == g) return ud;
            return null;
        }

        /// <summary>The label set of the first held controller: SDL's face
        /// labels (Cross south on PlayStation, B south on Nintendo, A south
        /// with X east on GameCube), else the vendor, else Xbox.</summary>
        private static OskFamily FamilyOf(Guid[] guids)
        {
            foreach (var g in guids)
            {
                var ud = FindUserDevice(g);
                if (ud == null) continue;
                if (ud.Device is SdlDeviceWrapper w)
                {
                    switch (w.FaceButtonLabel(SDL3.SDL.SDL_GAMEPAD_BUTTON_SOUTH))
                    {
                        case SDL3.SDL.SDL_GAMEPAD_BUTTON_LABEL_CROSS: return OskFamily.PlayStation;
                        case SDL3.SDL.SDL_GAMEPAD_BUTTON_LABEL_B: return OskFamily.Nintendo;
                        case SDL3.SDL.SDL_GAMEPAD_BUTTON_LABEL_A:
                            return w.FaceButtonLabel(SDL3.SDL.SDL_GAMEPAD_BUTTON_EAST) == SDL3.SDL.SDL_GAMEPAD_BUTTON_LABEL_X
                                ? OskFamily.GameCube : OskFamily.Xbox;
                    }
                }
                if (ud.VendorId == 0x054C) return OskFamily.PlayStation;
                if (ud.VendorId == 0x057E) return OskFamily.Nintendo;
                return OskFamily.Xbox;
            }
            return OskFamily.Xbox;
        }

        /// <summary>Back to the state before the keyboard first opened: the
        /// engine's start, and a test's.</summary>
        internal void ResetOnScreenKeyboardRuntime()
        {
            foreach (var m in _oskMasks.Values)
                if (m.Device != null && m.Raw != null) m.Device.InputState = m.Raw;
            _oskMasks.Clear();
            _oskOwned = Array.Empty<Guid>();
            _oskSlot = _oskMenuId = -1;
            _oskDef = null;
            _oskSettingsApplied = null;
            _oskSettingsDef = null;
            _oskLayerIgnored = false;
            _oskMenus.Clear();
            _oskRequests.Clear();
            _oskState.Clear();
            Array.Clear(_oskPrevDown);
            while (_oskCommands.TryDequeue(out _)) { }
            ResetOutput();
            ResetTextFocus();
            // A test's reset can find keyboards a run made. An engine start
            // finds none: the last stop removed them.
            _oskHidRetire.Clear();
            _oskHidRetire.AddRange(_oskHid.Keys);
            foreach (var k in _oskHidRetire) RetireMenuKeyboard(k);
            _oskHidClosed = false;
            _oskMenuScan = Array.Empty<OskMenuRef>();
            _oskScanMs = _oskHidCheckedMs = long.MinValue / 2;
            _oskWantsTextFocus = false;
            _osk?.Close();
            _oskView = null;
            _oskPublishedVersion = -1;
            lock (_oskMoveLock) { _oskMoveX = _oskMoveY = 0; }
        }
    }
}
