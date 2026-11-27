using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Output causes (#600): what a program fed
        // ─────────────────────────────────────────────
        //
        // A program feeds PadForge three ways: a device of its own, a hold
        // or claim on a control of any device, and a script value a Custom
        // Expression reads. Step 2 marks what each device's state carries of
        // a program (NoteProgramMarks). After Step 4 every slot works out
        // which of its keys, buttons and pointer a marked control can reach,
        // and whether its macros can read one (WorkOutOutputCauses). A mark
        // stays on a key or button while it stays down, on the pointer while
        // it moves, on the slot's macros while the slot's state is off rest
        // and on a shift layer while the layer runtime is off rest, so a
        // state a program set keeps its mark until it comes to rest or the
        // person's own input sets it again with no program feeding it.
        // Everything here runs on the poll thread.

        /// <summary>What a program fed of one device on this pass.</summary>
        internal sealed class ProgramMark
        {
            /// <summary>The session that fed it, 0 when none is known.</summary>
            public int Session;

            /// <summary>The whole device is a program's.</summary>
            public bool Whole;

            /// <summary>The controls a program holds, claims or held until
            /// the device's own value of them changes.</summary>
            public readonly HashSet<(char Kind, int Index)> Controls = new();

            public OutputCause Cause => OutputCause.Program(Session);
        }

        private readonly Dictionary<Guid, ProgramMark> _programMarks = new();

        // Marks kept from pass to pass, so a device a program drives costs no
        // allocation per pass.
        private readonly Dictionary<Guid, ProgramMark> _markPool = new();

        // The controls each device's holds covered on the last pass, and the
        // controls let go of since, each with the device's own value when
        // the hold ended. A let-go control stays marked until that value
        // changes, so the edge back to the device's own value (a release
        // after a program's press) is the program's.
        private readonly Dictionary<Guid, (int Session, HashSet<(char, int)> Controls)> _heldLastPass = new();
        private readonly Dictionary<Guid, (int Session, Dictionary<(char, int), int> Own)> _holdResidue = new();

        /// <summary>The causes of each Keyboard + Mouse slot's output on
        /// this pass, which Step 5 sends with it.</summary>
        internal KbmCause[] CombinedKbmCauses { get; } = new KbmCause[MaxPads];

        /// <summary>The cause of what a slot's macros and menus read on this
        /// pass.</summary>
        internal OutputCause[] SlotMacroCauses { get; } = new OutputCause[MaxPads];

        // The sticky marks carried from the last pass.
        private readonly KbmCause[] _kbmCausesKept = new KbmCause[MaxPads];
        private readonly OutputCause[] _slotCausesKept = new OutputCause[MaxPads];
        private readonly OutputCause[] _layerCausesKept = new OutputCause[MaxPads];
        private readonly bool[] _causesKeptValid = new bool[MaxPads];

        /// <summary>What a program fed of a device on this pass, or
        /// null.</summary>
        internal ProgramMark ProgramMarkOf(Guid device)
            => _programMarks.TryGetValue(device, out var mark) ? mark : null;

        // The devices a program fed whole on the last pass, with the session,
        // so the pass a device leaves on (gone, taken offline) still knows
        // its cause.
        private readonly Dictionary<Guid, int> _wholeMarksLastPass = new();

        /// <summary>Step 2, before the devices are read.</summary>
        private void BeginProgramMarks()
        {
            if (_programMarks.Count == 0 && _wholeMarksLastPass.Count == 0) return;
            _wholeMarksLastPass.Clear();
            foreach (var pair in _programMarks)
                if (pair.Value.Whole) _wholeMarksLastPass[pair.Key] = pair.Value.Session;
            _programMarks.Clear();
        }

        /// <summary>Step 2, for every device read: marks what a program fed
        /// of it. <paramref name="own"/> is the device's own state, before
        /// any hold.</summary>
        internal void NoteProgramMarks(UserDevice ud, CustomInputState own)
        {
            Guid g = ud.InstanceGuid;
            ProgramMark mark = null;
            if (ud.Device is ProgramDevice { PadForgeOwn: false } program)
            {
                mark = MarkFor(g, program.OwnerSession);
                mark.Whole = true;
            }

            HashSet<(char, int)> heldNow = null;
            int heldSession = 0;
            var table = ControlHolds.Current;
            if (table != null && table.TryGetValue(g, out var holds))
            {
                heldNow = holds.Controls;
                heldSession = holds.Owner;
            }

            // Counts first: with nothing held now or on the last pass, and
            // no residue, a device costs no lookup.
            if (_heldLastPass.Count > 0 && _heldLastPass.TryGetValue(g, out var last))
            {
                foreach (var c in last.Controls)
                {
                    if (heldNow != null && heldNow.Contains(c)) continue;
                    if (!_holdResidue.TryGetValue(g, out var residue))
                        _holdResidue[g] = residue = (last.Session, new Dictionary<(char, int), int>());
                    residue.Own[c] = OwnValue(own, c);
                }
                if (heldNow == null) _heldLastPass.Remove(g);
            }
            if (heldNow != null && heldNow.Count > 0)
            {
                _heldLastPass[g] = (heldSession, heldNow);
                mark ??= MarkFor(g, heldSession);
                mark.Controls.UnionWith(heldNow);
            }

            if (_holdResidue.Count > 0 && _holdResidue.TryGetValue(g, out var kept))
            {
                List<(char, int)> moved = null;
                foreach (var (c, value) in kept.Own)
                {
                    if (heldNow != null && heldNow.Contains(c)) continue;
                    if (OwnValue(own, c) != value) (moved ??= new()).Add(c);
                    else
                    {
                        mark ??= MarkFor(g, kept.Session);
                        mark.Controls.Add(c);
                    }
                }
                if (moved != null) foreach (var c in moved) kept.Own.Remove(c);
                if (kept.Own.Count == 0) _holdResidue.Remove(g);
            }
        }

        private ProgramMark MarkFor(Guid device, int session)
        {
            if (_programMarks.TryGetValue(device, out var mark))
            {
                if (mark.Session <= 0) mark.Session = session;
                return mark;
            }
            if (!_markPool.TryGetValue(device, out mark)) _markPool[device] = mark = new ProgramMark();
            mark.Session = session;
            mark.Whole = false;
            mark.Controls.Clear();
            _programMarks[device] = mark;
            return mark;
        }

        /// <summary>A control's value in a state, as the holds name it.</summary>
        private static int OwnValue(CustomInputState s, (char Kind, int Index) c)
        {
            if (s == null) return int.MinValue;
            switch (c.Kind)
            {
                case 'b':
                    return s.Buttons != null && (uint)c.Index < (uint)s.Buttons.Length && s.Buttons[c.Index] ? 1 : 0;
                case 'h':
                    return s.Povs != null && (uint)c.Index < (uint)s.Povs.Length ? s.Povs[c.Index] : int.MinValue;
                default:
                    if (c.Index < CustomInputState.MaxAxis)
                        return s.Axis != null && c.Index >= 0 && c.Index < s.Axis.Length ? s.Axis[c.Index] : int.MinValue;
                    int slider = c.Index - CustomInputState.MaxAxis;
                    return s.Sliders != null && slider < s.Sliders.Length ? s.Sliders[slider] : int.MinValue;
            }
        }

        // The devices a slot reads on this pass, for the "(Any Device)"
        // sources.
        private readonly List<Guid> _causeSlotDevices = new();
        private readonly UserSetting[] _causeSettingBuffer = new UserSetting[64];

        /// <summary>After Step 4: works out each slot's causes for this pass
        /// from the marks, its rows and activators, and what it carried from
        /// the last pass.</summary>
        internal void WorkOutOutputCauses()
        {
            var settings = SettingsManager.UserSettings;
            var sets = SettingsManager.SlotMappingSets;
            bool anyMarked = false;
            for (int slot = 0; slot < MaxPads; slot++)
            {
                // With nothing a program fed on this pass or kept from one,
                // every part of the slot is the person's own: no lookup.
                if (_programMarks.Count == 0 && NothingKept(slot))
                {
                    CombinedKbmCauses[slot] = KbmCause.AllOwn;
                    SlotMacroCauses[slot] = OutputCause.Own;
                    continue;
                }
                _causeSlotDevices.Clear();
                if (settings != null)
                {
                    int n = settings.FindByPadIndex(slot, _causeSettingBuffer);
                    for (int i = 0; i < n && i < _causeSettingBuffer.Length; i++)
                        if (_causeSettingBuffer[i] != null) _causeSlotDevices.Add(_causeSettingBuffer[i].InstanceGuid);
                }
                var set = sets != null && slot < sets.Length ? sets[slot] : null;
                WorkOutSlotCauses(slot, set, _causeSlotDevices);
                anyMarked |= !SlotMacroCauses[slot].IsOwn || CombinedKbmCauses[slot].AnyMarked;
            }
            // The devices that left serve this one pass. The idle pass works
            // the causes out again with no Step 2, and must not find them.
            _wholeMarksLastPass.Clear();
            ProgramInputGate.MarksKept = anyMarked;
        }

        /// <summary>One slot's causes. <paramref name="devices"/> are the
        /// devices assigned to it.</summary>
        internal void WorkOutSlotCauses(int slot, MappingSet set, IReadOnlyList<Guid> devices)
        {
            var pass = KbmCause.AllOwn;
            var slotCause = OutputCause.Own;
            bool anyActivatorMarked = false;

            if (_programMarks.Count > 0)
            {
                // A device of the slot that a program fed reaches its macros
                // and any target its rows leave to the legacy read.
                // Indexed: a foreach over the interface boxes the list's
                // enumerator on every call.
                var deviceCause = OutputCause.Own;
                for (int d = 0; d < devices.Count; d++)
                    if (_programMarks.TryGetValue(devices[d], out var m)) deviceCause = deviceCause.And(m.Cause);
                slotCause = deviceCause;

                ulong row0 = 0, row1 = 0, row2 = 0, row3 = 0;
                byte rowButtons = 0;
                bool rowPointer = false;
                var rows = set?.Rows;
                if (rows != null)
                {
                    for (int r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        if (row == null) continue;
                        var cause = RowCause(row.Sources, devices);
                        if (!cause.IsOwn) slotCause = slotCause.And(cause);
                        string target = row.Target ?? "";
                        if (!target.StartsWith("Kbm", StringComparison.Ordinal)) continue;
                        if (target.StartsWith("KbmKey", StringComparison.Ordinal)
                            && int.TryParse(target.AsSpan(6), System.Globalization.NumberStyles.HexNumber, null, out int vk)
                            && (uint)vk < 256)
                        {
                            SetBit(ref row0, ref row1, ref row2, ref row3, vk);
                            if (!cause.IsOwn) pass.MarkKey(vk, cause);
                        }
                        else if (target.StartsWith("KbmMBtn", StringComparison.Ordinal)
                            && int.TryParse(target.AsSpan(7), out int bit) && (uint)bit < 8)
                        {
                            rowButtons |= (byte)(1 << bit);
                            if (!cause.IsOwn) { pass.OwnButtons &= (byte)~(1 << bit); pass.Other = pass.Other.And(cause); }
                        }
                        else
                        {
                            rowPointer = true;
                            if (!cause.IsOwn) { pass.OwnPointer = false; pass.Other = pass.Other.And(cause); }
                        }
                    }
                }
                if (!deviceCause.IsOwn)
                {
                    // A target no row names reads its legacy mapping on each
                    // device of the slot.
                    pass.OwnKeys0 &= row0;
                    pass.OwnKeys1 &= row1;
                    pass.OwnKeys2 &= row2;
                    pass.OwnKeys3 &= row3;
                    pass.OwnButtons &= rowButtons;
                    if (!rowPointer) pass.OwnPointer = false;
                    pass.Other = pass.Other.And(deviceCause);
                }

                var activators = set?.ShiftActivators;
                if (activators != null)
                {
                    for (int a = 0; a < activators.Count; a++)
                    {
                        var act = activators[a];
                        if (act == null) continue;
                        var cause = ActivatorCause(act, devices);
                        if (cause.IsOwn) continue;
                        anyActivatorMarked = true;
                        slotCause = slotCause.And(cause);
                    }
                }
            }

            // The shift layer: a layer engaged while an activator reads a
            // program's control, or kept from such a pass, keeps the mark
            // until the layer runtime comes to rest.
            bool layerAtRest = LayerRuntimeAtRest(slot);
            var layerCause = OutputCause.Own;
            if (!layerAtRest)
            {
                if (anyActivatorMarked) layerCause = slotCause;
                else if (_causesKeptValid[slot] && !_layerCausesKept[slot].IsOwn) layerCause = _layerCausesKept[slot];
            }
            _layerCausesKept[slot] = layerCause;

            // A program's device of the slot that was read on the last pass
            // and is not on this one left with what it fed: the release its
            // leaving makes is read on this pass, with the slot at rest, and
            // is the program's.
            if (_wholeMarksLastPass.Count > 0)
                for (int i = 0; i < devices.Count; i++)
                    if (!_programMarks.ContainsKey(devices[i]) && _wholeMarksLastPass.TryGetValue(devices[i], out int session))
                        slotCause = slotCause.And(OutputCause.Program(session));

            // The slot's own state read by its macros keeps a mark until it
            // comes to rest.
            var kept = _causesKeptValid[slot] ? _slotCausesKept[slot] : OutputCause.Own;
            if (slotCause.IsOwn && !kept.IsOwn && !SlotStateAtRest(slot)) slotCause = kept;
            slotCause = slotCause.And(layerCause);
            _slotCausesKept[slot] = slotCause;
            SlotMacroCauses[slot] = slotCause;

            // Keys and buttons keep their mark while they stay down, the
            // pointer while it moves.
            var kbm = CombinedKbmRawStates[slot];
            if (_causesKeptValid[slot])
            {
                var prev = _kbmCausesKept[slot];
                pass.OwnKeys0 &= prev.OwnKeys0 | ~kbm.Keys0;
                pass.OwnKeys1 &= prev.OwnKeys1 | ~kbm.Keys1;
                pass.OwnKeys2 &= prev.OwnKeys2 | ~kbm.Keys2;
                pass.OwnKeys3 &= prev.OwnKeys3 | ~kbm.Keys3;
                pass.OwnButtons &= (byte)(prev.OwnButtons | ~kbm.MouseButtons);
                if (!prev.OwnPointer && PointerMoves(in kbm)) pass.OwnPointer = false;
                if (!prev.Other.IsOwn && pass.AnyMarked) pass.Other = pass.Other.And(prev.Other);
            }
            if (!layerCause.IsOwn)
            {
                var all = KbmCause.All(layerCause);
                all.Other = layerCause.And(pass.Other);
                pass = all;
            }
            if (!pass.AnyMarked) pass.Other = OutputCause.Own;
            _kbmCausesKept[slot] = pass;
            _causesKeptValid[slot] = true;
            CombinedKbmCauses[slot] = pass;
        }

        /// <summary>Whether a slot carries no mark from an earlier pass.</summary>
        private bool NothingKept(int slot)
            => !_causesKeptValid[slot]
            || (_slotCausesKept[slot].IsOwn && _layerCausesKept[slot].IsOwn && !_kbmCausesKept[slot].AnyMarked);

        private static void SetBit(ref ulong k0, ref ulong k1, ref ulong k2, ref ulong k3, int vk)
        {
            ulong bit = 1UL << (vk & 63);
            switch (vk >> 6)
            {
                case 0: k0 |= bit; break;
                case 1: k1 |= bit; break;
                case 2: k2 |= bit; break;
                default: k3 |= bit; break;
            }
        }

        private static bool PointerMoves(in KbmRawState k)
            => k.MouseDeltaX != 0 || k.MouseDeltaY != 0 || k.ScrollDelta != 0 || k.ScrollDeltaH != 0
            || k.MouseFlickX != 0 || k.MouseGyroX != 0f || k.MouseGyroY != 0f || k.MouseTouchX != 0f || k.MouseTouchY != 0f
            || k.MouseStickCoastX != 0f || k.MouseStickCoastY != 0f || k.MouseAbsValid;

        /// <summary>Whether a slot's combined output, which its macros read,
        /// is at rest: no button down, every hat centered and every axis at
        /// its rest. Step 3 has already put an axis inside its dead zone at
        /// its rest, so an axis off rest is one past its dead zone.</summary>
        private bool SlotStateAtRest(int slot)
        {
            if (SlotRawHidSurface[slot])
            {
                var raw = CombinedRawHidStates[slot];
                if (raw.Buttons != null) foreach (uint w in raw.Buttons) if (w != 0) return false;
                if (raw.Povs != null) foreach (int p in raw.Povs) if (p >= 0) return false;
                if (raw.Axes != null)
                {
                    var layout = SlotCustomLayouts[slot];
                    for (int i = 0; i < raw.Axes.Length; i++)
                        if (raw.Axes[i] != (layout.IsTriggerSlot(i) ? short.MinValue : (short)0)) return false;
                }
                return true;
            }
            var gp = CombinedOutputStates[slot];
            return gp.Buttons == 0 && gp.LeftTrigger == 0 && gp.RightTrigger == 0
                && gp.ThumbLX == 0 && gp.ThumbLY == 0 && gp.ThumbRX == 0 && gp.ThumbRY == 0;
        }

        /// <summary>Whether a slot's shift layer runtime holds nothing: no
        /// layer engaged, no activator toggled, stuck or gated open.</summary>
        private static bool LayerRuntimeAtRest(int slot)
        {
            var rt = (uint)slot < (uint)_shiftRuntime.Length ? _shiftRuntime[slot] : null;
            if (rt == null) return true;
            lock (rt.SyncRoot)
            {
                if (rt.Stack.Count > 0 || !string.IsNullOrEmpty(rt.CustomLayer)) return false;
            }
            foreach (bool b in rt.ToggleOn) if (b) return false;
            foreach (bool b in rt.StickyEngaged) if (b) return false;
            foreach (bool b in rt.HostGateOpen) if (b) return false;
            return true;
        }

        /// <summary>The cause of a row's sources: a program's when any can
        /// read a control a program fed. An "(Any Device)" source reads
        /// every device of the slot.</summary>
        internal OutputCause RowCause(IList<MappingSource> sources, IReadOnlyList<Guid> devices)
        {
            var cause = OutputCause.Own;
            if (sources == null) return cause;
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                if (src == null) continue;
                bool readsWithout = string.IsNullOrEmpty(src.Descriptor) && ReadsWithoutDescriptor(src.Kind);
                cause = cause.And(ReadCause(src.DeviceGuid, src.Descriptor, devices, readsWithout));
                cause = cause.And(ReadCause(src.ParamUpDeviceGuid ?? src.DeviceGuid, src.ParamUp, devices, false));
                cause = cause.And(ReadCause(src.ParamDownDeviceGuid ?? src.DeviceGuid, src.ParamDown, devices, false));
                cause = cause.And(ReadCause(src.ParamModifierDeviceGuid ?? src.DeviceGuid, src.ParamModifier, devices, false));
                cause = cause.And(ReadCause(src.DeviceGuid, src.ParamYDescriptor, devices, false));
            }
            return cause;
        }

        /// <summary>The cause of a shift activator's reads.</summary>
        internal OutputCause ActivatorCause(ShiftActivator act, IReadOnlyList<Guid> devices)
        {
            var cause = ReadCause(act.DeviceGuid, act.Descriptor, devices, false);
            cause = cause.And(ReadCause(act.DeviceGuid, act.ChordSecondDescriptor, devices, false));
            cause = cause.And(ReadCause(act.DeviceGuid, act.CyclePrevDescriptor, devices, false));
            cause = cause.And(ReadCause(act.DeviceGuid, act.GateDescriptor, devices, false));
            return cause.And(ReadCause(act.DeviceGuid, act.Gate2Descriptor, devices, false));
        }

        /// <summary>Source kinds that read their device with no
        /// descriptor (gyro, touchpad, motion): every kind but the ones that
        /// read a descriptor or keys.</summary>
        private static bool ReadsWithoutDescriptor(string kind)
            => !(string.IsNullOrEmpty(kind) || kind is "Direct" or "Toggle" or "RapidTrigger" or "Incremental" or "Ramped" or "InvertOnHold");

        private OutputCause ReadCause(string deviceGuid, string descriptor, IReadOnlyList<Guid> devices, bool wholeDevice)
        {
            if (string.IsNullOrEmpty(descriptor) && !wholeDevice) return OutputCause.Own;
            if (string.IsNullOrEmpty(deviceGuid))
            {
                var cause = OutputCause.Own;
                for (int d = 0; d < devices.Count; d++) cause = cause.And(ReadCause(devices[d], descriptor, wholeDevice));
                return cause;
            }
            return Guid.TryParse(deviceGuid, out var device) ? ReadCause(device, descriptor, wholeDevice) : OutputCause.Own;
        }

        /// <summary>The cause of reading <paramref name="descriptor"/> on a
        /// device. A descriptor that names no button, axis, slider or hat
        /// (an engine-owned source) reads the whole device.</summary>
        private OutputCause ReadCause(Guid device, string descriptor, bool wholeDevice)
        {
            if (!_programMarks.TryGetValue(device, out var mark)) return OutputCause.Own;
            if (mark.Whole || wholeDevice) return mark.Cause;
            foreach (string part in PipePartsCached(descriptor))
            {
                var d = ParseDescriptor(part);
                if (!d.IsValid) return mark.Cause;
                (char, int) control = d.Type switch
                {
                    MapType.Button => ('b', d.Index),
                    MapType.Axis => ('a', d.Index),
                    MapType.Slider => ('a', CustomInputState.MaxAxis + d.Index),
                    _ => ('h', d.Index),
                };
                if (mark.Controls.Contains(control)) return mark.Cause;
            }
            return OutputCause.Own;
        }
    }
}
