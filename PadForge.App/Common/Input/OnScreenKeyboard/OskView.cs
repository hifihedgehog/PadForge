using System;
using System.Collections.Generic;
using System.Text;
using PadForge.Engine.Menus;

namespace PadForge.Common.Input.OnScreenKeyboard
{
    /// <summary>One key as the window draws it.</summary>
    public sealed class OskViewKey
    {
        public OskKeyKind Kind { get; init; }
        public OskSpecial Special { get; init; }
        /// <summary>What the key types now, for a layout, symbol or
        /// suggestion key. A special key's name comes from the window's
        /// strings, except a function key's, which is here ("F5").</summary>
        public string Label { get; init; }
        /// <summary>A small second label: the other Shift level of a
        /// non-letter key, or a letter key's AltGr character.</summary>
        public string Corner { get; init; }
        public float W { get; init; }
        public float Start { get; init; }
        /// <summary>0, or 1 latched, or 2 locked, for a modifier or Caps Lock.</summary>
        public int State { get; init; }
        public bool Enabled { get; init; }
        public bool Dead { get; init; }
    }

    public sealed class OskViewBoard
    {
        public OskViewKey[][] Rows { get; init; }
        public float MaxW { get; init; }
        public bool AlignRight { get; init; }
        /// <summary>The focused key, by board row (the suggestion row is 0
        /// when shown) and index.</summary>
        public int FocusRow { get; init; }
        public int FocusIndex { get; init; }
        /// <summary>The page row the window's clicks name for board row 0.</summary>
        public int RowOffset { get; init; }
    }

    public sealed class OskViewWheel
    {
        /// <summary>32 labels, four per group, in west, north, east, south
        /// order within a group. Null for an empty place.</summary>
        public string[] Labels { get; init; }
        /// <summary>The special key at each place of the keys set, or None.</summary>
        public OskSpecial[] Specials { get; init; }
        public int Petal { get; init; }
        public int Set { get; init; }
    }

    /// <summary>
    /// What the On-Screen Keyboard shows (#566), built on the poll thread and
    /// read by the window on the UI timer with no lock, as the menu overlay's
    /// snapshot is. It never changes after it is built.
    /// </summary>
    public sealed class OskView
    {
        public int Version { get; init; }
        public long StampMs { get; init; }
        public bool Open { get; init; }
        public OskStyle Style { get; init; }
        /// <summary>Whether the function row shows. The UI saves it, and the
        /// style, when the keyboard's own keys change them.</summary>
        public bool FunctionRow { get; init; }
        public OskFamily Family { get; init; }
        public string LayoutName { get; init; }
        public int Page { get; init; }
        public bool Caps { get; init; }
        public int Shift { get; init; }
        public OskViewBoard[] Boards { get; init; }
        public OskViewWheel Wheel { get; init; }
        public OskSettings Settings { get; init; }
        /// <summary>The keyboard menu that owns the keyboard, by slot and menu
        /// id, whose place and size the window reads and writes.</summary>
        public int Slot { get; init; } = -1;
        public int MenuId { get; init; }
        /// <summary>Each control's input in the layout showing, for the hint
        /// bar. A control with no input is left out.</summary>
        public IReadOnlyDictionary<KeyboardControlAction, KeyboardInput> Bindings { get; init; } = EmptyBindings;
        internal static readonly IReadOnlyDictionary<KeyboardControlAction, KeyboardInput> EmptyBindings =
            new Dictionary<KeyboardControlAction, KeyboardInput>();
        /// <summary>The steering inputs, in the menu host grammar ("Gamepad
        /// LeftStick", "Gamepad DPad", "Touchpad 0", "Custom"). The second
        /// is Split's right half.</summary>
        public string Host { get; init; } = "";
        public string Host2 { get; init; } = "";
        /// <summary>Changes when the keys' places change, so the window
        /// rebuilds then and only restyles otherwise.</summary>
        public string Shape { get; init; }

        public static OskView Closed(long nowMs) => new() { Open = false, StampMs = nowMs, Boards = Array.Empty<OskViewBoard>() };

        public static OskView Build(OskEngine e, OskFamily family, long nowMs, int slot = -1, int menuId = 0,
            IReadOnlyDictionary<KeyboardControlAction, KeyboardInput> bindings = null, string host = "", string host2 = "")
        {
            if (e == null || !e.IsOpen) return Closed(nowMs);
            var shape = new StringBuilder();
            shape.Append((int)e.Style).Append('|').Append(e.Page).Append('|').Append(e.FunctionRow)
                 .Append('|').Append(e.Settings.Suggestions).Append('|').Append(e.Layout?.Name);
            var boards = new List<OskViewBoard>();
            OskViewWheel wheel = null;
            if (e.Style == OskStyle.Wheel)
            {
                var sets = e.WheelSets();
                var set = sets[Math.Clamp(e.WheelSet, 0, sets.Length - 1)];
                var labels = new string[32];
                var specials = new OskSpecial[32];
                for (int i = 0; i < 32 && i < set.Length; i++)
                {
                    switch (set[i])
                    {
                        case string s:
                            labels[i] = e.ShiftState > 0 && s.Length == 1 && char.IsLetter(s[0]) ? s.ToUpperInvariant() : s;
                            break;
                        case OskKey k:
                            labels[i] = k.Text;
                            specials[i] = k.Special;
                            break;
                    }
                }
                wheel = new OskViewWheel { Labels = labels, Specials = specials, Petal = e.Petal, Set = e.WheelSet };
            }
            else
            {
                var src = e.Boards();
                for (int bi = 0; bi < src.Length; bi++)
                {
                    var b = src[bi];
                    var focus = e.Style == OskStyle.Grid ? e.GridFocus : bi == 0 ? e.LeftFocus : e.RightFocus;
                    e.Normalize(focus, b);
                    var rows = new OskViewKey[b.Rows.Length][];
                    for (int r = 0; r < b.Rows.Length; r++)
                    {
                        rows[r] = new OskViewKey[b.Rows[r].Length];
                        for (int i = 0; i < b.Rows[r].Length; i++)
                            rows[r][i] = ViewOf(e, b.Rows[r][i], b.Starts[r][i]);
                        shape.Append('|').Append(b.Rows[r].Length);
                    }
                    boards.Add(new OskViewBoard
                    {
                        Rows = rows,
                        MaxW = b.MaxW,
                        AlignRight = b.AlignRight,
                        FocusRow = focus.R + b.Offset,
                        FocusIndex = focus.I,
                        RowOffset = -b.Offset,
                    });
                }
            }
            return new OskView
            {
                Version = e.Version,
                StampMs = nowMs,
                Open = true,
                Style = e.Style,
                FunctionRow = e.FunctionRow,
                Family = family,
                LayoutName = e.Layout?.Name,
                Page = e.Page,
                Caps = e.Layout?.CapsOn == true,
                Shift = e.ShiftState,
                Boards = boards.ToArray(),
                Wheel = wheel,
                Settings = e.Settings,
                Slot = slot,
                MenuId = menuId,
                Bindings = bindings ?? EmptyBindings,
                Host = host ?? "",
                Host2 = host2 ?? "",
                Shape = shape.ToString(),
            };
        }

        private static OskViewKey ViewOf(OskEngine e, OskKey k, float start)
        {
            string label = null, corner = null;
            int state = 0;
            bool dead = false;
            switch (k.Kind)
            {
                case OskKeyKind.Layout:
                {
                    var t = k.LayoutText;
                    label = e.LevelText(t);
                    dead = e.LevelDead(t);
                    bool letter = t.CapsAffects;
                    if (!letter)
                        corner = e.ShiftState > 0 ? t.Base : t.Shift;
                    else if (e.AltGrState == 0 && t.AltGr != null)
                        corner = t.AltGr;
                    if (corner == label) corner = null;
                    break;
                }
                case OskKeyKind.Symbol:
                    label = k.Text;
                    if (label != null && e.ShiftState > 0 && label.Length == 1 && char.IsLetter(label[0]))
                        label = label.ToUpperInvariant();
                    break;
                case OskKeyKind.Suggestion:
                    label = k.SuggestionIndex < e.Suggestions.Length ? e.Suggestions[k.SuggestionIndex] : null;
                    break;
                default:
                    label = k.Text;
                    state = k.Special switch
                    {
                        OskSpecial.Shift => e.ShiftState,
                        OskSpecial.Ctrl => e.CtrlState,
                        OskSpecial.Alt => e.AltState,
                        OskSpecial.Win => e.WinState,
                        OskSpecial.AltGr => e.AltGrState,
                        OskSpecial.CapsLock => e.Layout?.CapsOn == true ? 2 : 0,
                        OskSpecial.Fn => e.FunctionRow ? 2 : 0,
                        _ => 0,
                    };
                    break;
            }
            return new OskViewKey
            {
                Kind = k.Kind,
                Special = k.Special,
                Label = label,
                Corner = corner,
                W = k.W,
                Start = start,
                State = state,
                Enabled = e.IsEnabled(k),
                Dead = dead,
            };
        }
    }
}
