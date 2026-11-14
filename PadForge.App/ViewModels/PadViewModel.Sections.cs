using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Data;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The controller page's six sections (#574). The page had up to fourteen
    /// tabs in two rows. It shows one row of six sections now, and each
    /// section holds what several tabs held.
    ///
    /// <para><see cref="SelectedConfigTab"/> keeps its eighteen values, so
    /// everything that names a tab by number still works. This file adds the
    /// map from a tab to its section and the state the page reads to draw the
    /// selected one.</para>
    ///
    /// <para>Three kinds of section. Buttons shows the picture of the
    /// controller with the picked control's rows and the output cards under
    /// it, or the list of every row. Macros &amp; Menus shows one part at a
    /// time, because the macro editor and the menu editor each fill the
    /// page. The other four stack their tabs as groups in one scrolling
    /// column.</para>
    /// </summary>
    public partial class PadViewModel
    {
        /// <summary>How many sections the page has.</summary>
        public const int SectionCount = 6;

        /// <summary>Buttons: the picture, the mapping list and the output
        /// options.</summary>
        public const int ButtonsSection = 0;

        /// <summary>Sticks &amp; Triggers: sticks, triggers and a mouse's
        /// gestures.</summary>
        public const int SticksSection = 1;

        /// <summary>Rumble &amp; Haptics: force feedback, impulse triggers,
        /// adaptive triggers, the wheel and bass shakers.</summary>
        public const int RumbleSection = 2;

        /// <summary>Motion &amp; Touch: gyro, touchpad and pointer.</summary>
        public const int MotionSection = 3;

        /// <summary>Lights &amp; Sound: lighting and audio.</summary>
        public const int LightsSection = 4;

        /// <summary>Macros &amp; Menus.</summary>
        public const int MacrosSection = 5;

        /// <summary>The tab that is the picture of the controller.</summary>
        public const int PictureTabIndex = 0;

        /// <summary>The tab that is the list of every mapping row.</summary>
        public const int MappingsTabIndex = 2;

        /// <summary>Each section's tabs, in the order the page shows them.
        /// Every tab is in exactly one section.</summary>
        private static readonly int[][] s_sectionTabs =
        {
            new[] { PictureTabIndex, MappingsTabIndex, OutputTabIndex },
            new[] { 3, 4, 14 },
            new[] { 5, 9, 6, 11, BassShakersTabIndex },
            new[] { 8, 10, 13 },
            new[] { 7, AudioTabIndex },
            new[] { 1, 15 },
        };

        /// <summary>The tabs of a section in page order, empty for a number
        /// that names no section.</summary>
        public static IReadOnlyList<int> TabsOfSection(int section)
            => section >= 0 && section < SectionCount ? s_sectionTabs[section] : Array.Empty<int>();

        /// <summary>The section that holds a tab, or -1 for a number that
        /// names no tab.</summary>
        public static int SectionOfTab(int tab)
        {
            for (int s = 0; s < SectionCount; s++)
                if (Array.IndexOf(s_sectionTabs[s], tab) >= 0)
                    return s;
            return -1;
        }

        /// <summary>True for the four sections that stack their tabs as
        /// groups. False for Buttons and Macros &amp; Menus.</summary>
        public static bool IsStackedSection(int section)
            => section is >= SticksSection and <= LightsSection;

        /// <summary>The tab each section was last on. A section opens there
        /// when its button is clicked. It starts on each section's first
        /// tab.</summary>
        private readonly int[] _lastTabOfSection = FirstTabs();

        private static int[] FirstTabs()
        {
            var first = new int[SectionCount];
            for (int s = 0; s < SectionCount; s++)
                first[s] = s_sectionTabs[s][0];
            return first;
        }

        /// <summary>Puts every section back on its first tab and forgets the
        /// picked control. A deleted slot calls it, so the next controller
        /// made at this index does not open a section where the deleted one
        /// left it.</summary>
        private void ResetSectionMemory()
        {
            int oldView = ButtonsViewIndex;
            Array.Copy(FirstTabs(), _lastTabOfSection, SectionCount);
            int section = SectionOfTab(_selectedConfigTab);
            if (section >= 0) _lastTabOfSection[section] = _selectedConfigTab;
            _pickedTarget = null;
            OnPropertyChanged(nameof(PickedTarget));
            if (ButtonsViewIndex != oldView) RaiseButtonsViewChanged();
            ApplyMappingPickerFilter();
            RaiseMacrosPartChanged();
        }

        /// <summary>The header chip's logo for the slot's type, or null for
        /// the types drawn with a glyph. The same drawing as the slot's Home
        /// card and its sidebar entry (SlotTypeIconMap).</summary>
        public System.Windows.Media.Geometry TypeGeometry => SlotTypeIconMap.GeometryFor(_outputType);

        /// <summary>The header chip's glyph for the types without a logo,
        /// empty for the others.</summary>
        public string TypeGlyph => SlotTypeIconMap.GlyphFor(_outputType);

        /// <summary>The section that holds the selected tab. The page's
        /// content follows it.</summary>
        public int SelectedSection => Math.Max(0, SectionOfTab(_selectedConfigTab));

        /// <summary>Which view of Buttons shows: 0 the picture, with the
        /// picked control's rows and the output cards under it, 1 the list
        /// of every row. Tab 2 is the list. Tabs 0 and 17 are the picture.
        /// It holds while another section is selected.</summary>
        public int ButtonsViewIndex => _lastTabOfSection[ButtonsSection] == MappingsTabIndex ? 1 : 0;

        /// <summary>Buttons shows the picture.</summary>
        public bool IsButtonsPictureView => ButtonsViewIndex == 0;

        /// <summary>Buttons shows the list.</summary>
        public bool IsButtonsListView => ButtonsViewIndex == 1;

        /// <summary>The output cards show: under the picture, on a slot type
        /// that has any.</summary>
        public bool ShowOutputGroup => IsButtonsPictureView && OutputTabVisible;

        /// <summary>A MIDI slot's card shows under the picture's card, where
        /// a controller has its output cards: the port, the channel and the
        /// runs of controls and notes.</summary>
        public bool ShowMidiCard => IsButtonsPictureView && OutputType == VirtualControllerType.Midi;

        /// <summary>An Extended slot's card shows under the picture's card,
        /// over the output cards: what is done to the slot as a whole, and
        /// Customize with what it sets.</summary>
        public bool ShowExtendedCard => IsButtonsPictureView && OutputType == VirtualControllerType.Extended;

        /// <summary>The header says SteamVR's state after the slot's name:
        /// a VR slot drives nothing without SteamVR, and nothing else on
        /// the page says whether it is there.</summary>
        public bool ShowSteamVrState => OutputType == VirtualControllerType.Vr;

        private void RaiseButtonsViewChanged()
        {
            OnPropertyChanged(nameof(ButtonsViewIndex));
            OnPropertyChanged(nameof(IsButtonsPictureView));
            OnPropertyChanged(nameof(IsButtonsListView));
            OnPropertyChanged(nameof(ShowOutputGroup));
            OnPropertyChanged(nameof(PictureIsCardFoot));
            OnPropertyChanged(nameof(ShowMidiCard));
            OnPropertyChanged(nameof(ShowExtendedCard));
            OnPropertyChanged(nameof(ShowVrFoot));
            OnPropertyChanged(nameof(ShowPictureLine));
            RaisePickedChanged();
        }

        /// <summary>Which part of Macros &amp; Menus shows: 0 the macros,
        /// 1 the menus.</summary>
        public int MacrosPartIndex
            => Math.Max(0, Array.IndexOf(s_sectionTabs[MacrosSection], _lastTabOfSection[MacrosSection]));

        /// <summary>The Macros tab's number.</summary>
        public const int MacrosTabIndex = 1;

        /// <summary>The Menus tab's number.</summary>
        public const int MenusTabIndex = 15;

        /// <summary>The macro its list shows as picked: the selected macro
        /// while the macros are the part that shows, and none while the
        /// menus are, so one item is picked across the two lists. Picking a
        /// macro selects it and shows the macros. A list hands back nothing
        /// when its pick goes to the other list, which selects
        /// nothing.</summary>
        public MacroItem ListedMacro
        {
            get => MacrosPartIndex == 0 ? SelectedMacro : null;
            set
            {
                if (value == null) return;
                SelectedMacro = value;
                SelectedConfigTab = MacrosTabIndex;
            }
        }

        /// <summary>The menu its list shows as picked, as
        /// <see cref="ListedMacro"/> is for the macros.</summary>
        public MenuEditorItem ListedMenu
        {
            get => MacrosPartIndex == 1 ? SelectedMenu : null;
            set
            {
                if (value == null) return;
                SelectedMenu = value;
                SelectedConfigTab = MenusTabIndex;
            }
        }

        /// <summary>Shows a part of Macros &amp; Menus while that section is
        /// the one on the page: a macro or a menu just made shows in its
        /// editor, whichever part showed before. Off the section it does
        /// nothing, so a macro added by code moves no page.</summary>
        private void ShowMacrosPart(int tab)
        {
            if (SelectedSection == MacrosSection) SelectedConfigTab = tab;
        }

        /// <summary>The part of Macros &amp; Menus that shows changed, and
        /// with it which list has the picked item.</summary>
        private void RaiseMacrosPartChanged()
        {
            OnPropertyChanged(nameof(MacrosPartIndex));
            OnPropertyChanged(nameof(ListedMacro));
            OnPropertyChanged(nameof(ListedMenu));
        }

        /// <summary>The tab a section was last on, 0 for a number that names
        /// no section.</summary>
        public int LastTabOfSection(int section)
            => section >= 0 && section < SectionCount ? _lastTabOfSection[section] : 0;

        /// <summary>Whether a tab's content is on the page now. A stacked
        /// section shows all of its tabs at once, so any of them counts while
        /// that section is selected. In Buttons the picture and the output
        /// cards show together and the list shows alone. Macros &amp; Menus
        /// shows only the selected part. A group the page hides for the
        /// selected device still counts here, since only the page knows
        /// that.</summary>
        public bool IsTabShown(int tab)
        {
            int section = SectionOfTab(tab);
            if (section < 0 || section != SelectedSection) return false;
            if (IsStackedSection(section)) return true;
            if (section == ButtonsSection) return (tab == MappingsTabIndex) == IsButtonsListView;
            return tab == _selectedConfigTab;
        }

        // ─────────────────────────────────────────────
        //  The picked control (#574)
        // ─────────────────────────────────────────────

        private string _pickedTarget;

        /// <summary>The control the bar under the picture shows: the
        /// TargetSettingName of the one last clicked on the picture, last
        /// recorded, or last selected in the list. Session only, never
        /// saved. A direction's own name ("LeftThumbAxisXNeg") picks its
        /// row.</summary>
        public string PickedTarget
        {
            get => _pickedTarget;
            set
            {
                if (string.Equals(_pickedTarget, value, StringComparison.Ordinal)) return;
                string old = _pickedTarget;
                _pickedTarget = value;
                ClearRecordRefusal();
                // The rows follow before the change is raised, so the page
                // finds the picked row in the table when it hears of the
                // pick. A pick inside the family already shown changes no
                // rows, and the table is left alone: rebuilding its rows
                // under a click on one of them would take the row out from
                // under the pointer.
                if (IsButtonsPictureView && !SamePickRows(old, value)) ApplyMappingPickerFilter();
                OnPropertyChanged();
                RaisePickedChanged();
            }
        }

        /// <summary>The row the bar under the picture is on: the one
        /// <see cref="PickedTarget"/> names. Its value, Add Source and
        /// options stand at the bar's end. Null with nothing picked.</summary>
        public MappingItem PickedRow => RowOfTarget(_pickedTarget);

        /// <summary>The targets of the controls the pick names: the picked
        /// row's, and with a row of a D-pad or a stick picked, those of
        /// every row of that control, which are the rows the bar under the
        /// picture holds. The picture rings what they draw.</summary>
        internal IReadOnlyList<string> PickedTargets() => TargetsOfControl(PickedRow);

        /// <summary>The targets of the control under the picture's pointer
        /// (#574), which the page rings as the Buttons board draws a hover:
        /// every row's of that control, as a pick's ring has them. None for
        /// a target with no row, and none for the picked control, which has
        /// its own ring.</summary>
        internal IReadOnlyList<string> HoveredTargets(string hovered)
        {
            var row = RowOfTarget(hovered);
            var picked = PickedRow;
            if (row == null || ReferenceEquals(row, picked) || (picked != null && SameControl(row, picked)))
                return Array.Empty<string>();
            return TargetsOfControl(row);
        }

        /// <summary>The targets of the control a row is part of: the row's
        /// own, or a D-pad's or a stick's every row. None for no row.</summary>
        private IReadOnlyList<string> TargetsOfControl(MappingItem row)
        {
            if (row == null) return Array.Empty<string>();
            if (FamilyOf(row) == null) return new[] { row.TargetSettingName };
            var targets = new List<string>();
            foreach (var m in Mappings)
                if (SameControl(m, row)) targets.Add(m.TargetSettingName);
            return targets;
        }

        /// <summary>The bar's heading: the picked control's name. A stick
        /// and a D-pad are named as the list's group is, whichever of
        /// their rows was picked: a stick's axes and its click, the D-pad's
        /// directions. Where a slot has several controls to a category (a
        /// custom Extended slot's sticks and POVs), and on a keyboard and
        /// mouse slot, the control is named by the picked row. So is every
        /// one-row control, by the picture's name for it where that is not
        /// the row's.</summary>
        public string PickedName
        {
            get
            {
                var row = PickedRow;
                if (row == null) return string.Empty;
                if (row.Control != null || OutputType == VirtualControllerType.KeyboardMouse) return row.BarLabel;
                return FamilyOf(row) switch
                {
                    MappingCategory.DPad => Strings.Instance.Preview_DPad,
                    MappingCategory.LeftStick => Strings.Instance.Menu_Host_LeftStick,
                    MappingCategory.RightStick => Strings.Instance.Menu_Host_RightStick,
                    _ => row.BarLabel,
                };
            }
        }

        /// <summary>What kind of control the picked one is, under its name
        /// in the bar, as the boards have it: "Face button", "Stick",
        /// "Keyboard key". Empty with nothing picked, or for a control
        /// with no kind to name.</summary>
        public string PickedKind => KindOf(PickedRow);

        /// <summary>The kind of control a row is an output of, in the
        /// words of the bar's small line. A row of a stick or a D-pad is
        /// that control's kind, a stick's click included. Any other row of
        /// a controller goes by its group, and a button by which button it
        /// is: a lettered preset's raw rows by the control each one is. A
        /// keyboard and mouse slot's rows and a VR slot's go by their
        /// target's name, and a VR control says which hand it is in. A
        /// MIDI note's line is the row's own name, since the bar heads a
        /// note by its pitch. A pressure row has no kind to name.</summary>
        internal string KindOf(MappingItem row)
        {
            if (row == null) return string.Empty;
            var s = Strings.Instance;
            string target = row.TargetSettingName ?? string.Empty;
            switch (OutputType)
            {
                case VirtualControllerType.KeyboardMouse:
                    if (target.StartsWith("KbmKey", StringComparison.Ordinal)) return s.Pad_Kind_Key;
                    if (target.StartsWith("KbmMBtn", StringComparison.Ordinal)) return s.Pad_Kind_MouseButton;
                    if (target.StartsWith("KbmScroll", StringComparison.Ordinal)) return s.Pad_Kind_ScrollWheel;
                    if (target.StartsWith("KbmMouse", StringComparison.Ordinal)) return s.Pad_Kind_MouseMovement;
                    return string.Empty;
                case VirtualControllerType.Midi:
                    if (row.PictureName != null) return row.TargetLabel;
                    return target.StartsWith("MidiCC", StringComparison.Ordinal) ? s.Pad_Kind_ControlChange : string.Empty;
                case VirtualControllerType.Vr:
                    return VrKindOf(target, s);
            }
            switch (FamilyOf(row))
            {
                case MappingCategory.DPad: return s.Pad_Kind_DPad;
                case MappingCategory.LeftStick:
                case MappingCategory.RightStick: return s.Pad_Kind_Stick;
            }
            switch (row.Category)
            {
                case MappingCategory.Triggers:
                    return MappingSetMigrator.IsPressureTarget(target) ? string.Empty : s.Pad_Kind_Trigger;
                case MappingCategory.Touchpad: return s.Pad_Kind_Touchpad;
                case MappingCategory.Motion: return s.Pad_Kind_Motion;
            }
            return (row.Role ?? target) switch
            {
                "ButtonA" or "ButtonB" or "ButtonX" or "ButtonY" => s.Pad_Kind_FaceButton,
                "LeftShoulder" or "RightShoulder" => s.Pad_Kind_ShoulderButton,
                "ButtonBack" or "ButtonStart" or "ButtonGuide" or "ButtonShare" or "ButtonMute"
                    or "ButtonC" or "ButtonQuickAccess" => s.Pad_Kind_SystemButton,
                "LeftTrigger" or "RightTrigger" => s.Pad_Kind_Trigger,
                "TouchpadClick" or "LeftTouchpadClick" or "RightTouchpadClick" => s.Pad_Kind_Touchpad,
                _ => s.Pad_Kind_Button,
            };
        }

        /// <summary>The kind of a VR control and the hand it is in: a
        /// button, the touch sensor of A or B, the pull of a trigger or a
        /// grip, or the stick, whose click is one of its rows.</summary>
        private static string VrKindOf(string target, Strings s)
        {
            if (target.Length < 4 || !target.StartsWith("Vr", StringComparison.Ordinal)) return string.Empty;
            string part = target.Substring(3);
            string kind = part switch
            {
                "A" or "B" or "System" or "GripClick" or "TriggerClick" => s.Pad_Kind_Button,
                "ATouch" or "BTouch" => s.Pad_Kind_TouchSensor,
                "Trigger" or "Grip" => s.Pad_Kind_AnalogPull,
                _ => part.StartsWith("Stick", StringComparison.Ordinal) ? s.Pad_Kind_Stick : string.Empty,
            };
            if (kind.Length == 0) return string.Empty;
            return string.Format(target[2] == 'L' ? s.Pad_Kind_LeftHand_Format : s.Pad_Kind_RightHand_Format, kind);
        }

        /// <summary>The bar shows: under the picture, with a control
        /// picked.</summary>
        public bool ShowPictureBar => IsButtonsPictureView && PickedRow != null;

        /// <summary>The bar shows the picked row's live value: on every slot
        /// but a Keyboard + Mouse or a MIDI slot, whose boards draw the bar
        /// with none. A key, a mouse button or a note is on or off, and the
        /// picture lights it.</summary>
        public bool ShowPickedValue => OutputType is not (VirtualControllerType.KeyboardMouse or VirtualControllerType.Midi);

        /// <summary>A Keyboard + Mouse slot's foot shows at the foot of the
        /// picture's card, and under the list: what the slot's keyboard and
        /// its mouse type through.</summary>
        public bool ShowKbmFoot => OutputType == VirtualControllerType.KeyboardMouse;

        /// <summary>The note under a VR slot's card, as its board draws it:
        /// Map All skips the A and B touch outputs, and how to bind one. It
        /// says to click the ring around A or B, so it shows under the
        /// picture, where the rings are, and not under the list.</summary>
        public bool ShowVrFoot => IsButtonsPictureView && OutputType == VirtualControllerType.Vr;

        /// <summary>The first section's name, by what the slot's picture
        /// holds, as each slot's board names it: Buttons on a controller,
        /// Keys &amp; Mouse on a Keyboard + Mouse slot, Notes &amp; Controls on
        /// a MIDI slot, Hands on a VR slot.</summary>
        public string FirstSectionName => OutputType switch
        {
            VirtualControllerType.KeyboardMouse => Strings.Instance.Pad_Section_KeysMouse,
            VirtualControllerType.Midi => Strings.Instance.Pad_Section_NotesControls,
            VirtualControllerType.Vr => Strings.Instance.Pad_Section_Hands,
            _ => Strings.Instance.Pad_Section_Buttons,
        };

        /// <summary>The hint over the picture, in the words of what the
        /// slot's picture holds: keys and mouse buttons, notes and sliders,
        /// or a controller's controls.</summary>
        public string PictureHintText => OutputType switch
        {
            VirtualControllerType.KeyboardMouse => Strings.Instance.Pad_PictureHint_Kbm,
            VirtualControllerType.Midi => Strings.Instance.Pad_PictureHint_Midi,
            _ => Strings.Instance.Pad_PictureHint,
        };

        /// <summary>The picture is the card's foot, and takes its lower
        /// corners: nothing is picked, so no bar follows it, the slot has no
        /// foot of its own, and no output lines stand in the card's tray
        /// under it (the sections pass).</summary>
        public bool PictureIsCardFoot => !ShowPictureBar && !ShowKbmFoot && !ShowOutputGroup;

        private bool _isPickedOptionsOpen;

        /// <summary>Whether the bar shows the current row's options under
        /// itself. It holds from one pick to the next. Session only, never
        /// saved.</summary>
        public bool IsPickedOptionsOpen
        {
            get => _isPickedOptionsOpen;
            set
            {
                if (!SetProperty(ref _isPickedOptionsOpen, value)) return;
                OnPropertyChanged(nameof(ShowPickedOptions));
            }
        }

        /// <summary>The current row's options show under the bar.</summary>
        public bool ShowPickedOptions => ShowPictureBar && _isPickedOptionsOpen;

        /// <summary>The picked row may be another: after a pick, a change
        /// of view, or rows built again.</summary>
        private void RaisePickedChanged()
        {
            RefreshPickedRows();
            OnPropertyChanged(nameof(PickedRow));
            OnPropertyChanged(nameof(PickedName));
            OnPropertyChanged(nameof(PickedKind));
            OnPropertyChanged(nameof(ShowPictureBar));
            OnPropertyChanged(nameof(ShowPickedOptions));
            OnPropertyChanged(nameof(PictureIsCardFoot));
        }

        /// <summary>Whether two picks put the same rows under the picture:
        /// the same row, or two rows of one family.</summary>
        private bool SamePickRows(string a, string b)
        {
            var rowA = RowOfTarget(a);
            var rowB = RowOfTarget(b);
            if (rowA == null || rowB == null) return false;
            if (ReferenceEquals(rowA, rowB)) return true;
            return SameControl(rowA, rowB);
        }

        /// <summary>Whether two rows are of one control on the picture:
        /// both of one family, and of the same control where the slot has
        /// several to the category.</summary>
        internal static bool SameControl(MappingItem a, MappingItem b)
            => FamilyOf(a) is { } family && FamilyOf(b) == family
               && string.Equals(a.Control, b.Control, StringComparison.Ordinal);

        /// <summary>The control a row is part of on the picture, by the
        /// category of that control's own rows: a D-pad's directions and a
        /// stick's two axes by their category, and a stick's click by the
        /// stick it is on, as the Buttons board has it. The click is a
        /// button in the list and one of its stick's rows under the
        /// picture. Null for a row that is a control by itself.</summary>
        internal static MappingCategory? FamilyOf(MappingItem m)
        {
            if (m == null) return null;
            if (IsFamilyCategory(m.Category)) return m.Category;
            return (m.Role ?? m.TargetSettingName) switch
            {
                "LeftThumbButton" or "VrLStickClick" => MappingCategory.LeftStick,
                "RightThumbButton" or "VrRStickClick" => MappingCategory.RightStick,
                _ => null,
            };
        }

        /// <summary>The order of a control's rows under the picture: its
        /// own rows as the list has them, then a row that joins it from
        /// another group. So a stick's two axes stand before its click, as
        /// the Buttons board has them.</summary>
        private sealed class BarOrder : System.Collections.IComparer
        {
            private readonly ObservableCollection<MappingItem> _rows;

            internal BarOrder(ObservableCollection<MappingItem> rows) => _rows = rows;

            public int Compare(object x, object y)
            {
                var a = x as MappingItem;
                var b = y as MappingItem;
                int joins = Joins(a).CompareTo(Joins(b));
                return joins != 0 ? joins : _rows.IndexOf(a).CompareTo(_rows.IndexOf(b));
            }

            private static bool Joins(MappingItem m)
                => m != null && !IsFamilyCategory(m.Category) && FamilyOf(m) != null;
        }

        private ListCollectionView _pickedRowsView;
        private string _barTarget;
        private MappingCategory? _barFamily;
        private string _barControl;

        /// <summary>The rows the bar under the picture holds: the picked
        /// control's, in the bar's order, and only those the slot's surface
        /// mode lets it drive. A view of its own over the rows. The bar
        /// shared the table's view before, so in the list, hidden, it built
        /// a block for every row of the table: 148 on a Keyboard + Mouse
        /// slot, about a second on each switch to the list.</summary>
        public System.ComponentModel.ICollectionView PickedRowsView
        {
            get
            {
                if (_pickedRowsView == null)
                {
                    TakeBarPick();
                    _pickedRowsView = new ListCollectionView(Mappings)
                    {
                        CustomSort = new BarOrder(Mappings),
                        Filter = IsBarRow,
                    };
                }
                return _pickedRowsView;
            }
        }

        /// <summary>Whether a row is one of the bar's: of the picked
        /// control, and on a half the slot drives.</summary>
        private bool IsBarRow(object o)
            => _barTarget != null && o is MappingItem m
               && RowMatchesPick(m, _barTarget, _barFamily, _barControl)
               && RowMatchesSurface(m, "All", OutputType == VirtualControllerType.KeyboardMouse ? KbmConfig : null);

        /// <summary>The pick the bar's filter reads, by name, family and
        /// control, so it holds while the rows are built again.</summary>
        private void TakeBarPick()
        {
            var picked = RowOfTarget(_pickedTarget);
            _barTarget = picked?.TargetSettingName;
            _barFamily = FamilyOf(picked);
            _barControl = picked?.Control;
        }

        /// <summary>The bar's rows follow a pick, rows built again, and a
        /// change of what the slot drives.</summary>
        private void RefreshPickedRows()
        {
            if (_pickedRowsView == null) return;
            TakeBarPick();
            _pickedRowsView.Refresh();
        }

        /// <summary>The row a target names, by its own name or by its
        /// opposite direction's, or null.</summary>
        internal MappingItem RowOfTarget(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            foreach (var m in Mappings)
            {
                if (string.Equals(m.TargetSettingName, target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.NegSettingName, target, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
            return null;
        }

        /// <summary>The categories whose rows are one control on the
        /// picture: a stick's two axes and the D-pad's four directions. A
        /// pick of one of their rows shows them all, and a stick's click
        /// with its axes (<see cref="FamilyOf"/>).</summary>
        internal static bool IsFamilyCategory(MappingCategory category)
            => category is MappingCategory.DPad or MappingCategory.LeftStick or MappingCategory.RightStick;

        /// <summary>Whether a row belongs under the picture for a pick: the
        /// picked row itself, or any row of the picked row's control, which
        /// is its family and, on a slot with several controls to a
        /// category, the one it names. Names and a category are compared,
        /// never row objects, so the answer holds after the rows are
        /// rebuilt.</summary>
        internal static bool RowMatchesPick(MappingItem m, string pickedTarget, MappingCategory? family, string control)
        {
            if (m == null || string.IsNullOrEmpty(pickedTarget)) return false;
            if (string.Equals(m.TargetSettingName, pickedTarget, StringComparison.OrdinalIgnoreCase)) return true;
            return family.HasValue && FamilyOf(m) == family.Value
                && string.Equals(m.Control, control, StringComparison.Ordinal);
        }

        // ─────────────────────────────────────────────
        //  The list's groups (#574)
        // ─────────────────────────────────────────────

        /// <summary>The types whose rows are the fixed gamepad grid: buttons,
        /// the D-pad, the triggers and the two sticks, with the touchpad and
        /// motion rows where the type has them. Keyboard + Mouse, MIDI and
        /// VR have row sets of their own, and an Extended or Nintendo slot's
        /// rows follow its preset.</summary>
        internal static bool HasGamepadRows(VirtualControllerType type)
            => type is not (VirtualControllerType.KeyboardMouse or VirtualControllerType.Midi
                            or VirtualControllerType.Vr or VirtualControllerType.Extended
                            or VirtualControllerType.Nintendo);

        /// <summary>Whether the table's rows stand under a header for each
        /// kind of output. A gamepad's list has them. The rows under the
        /// picture never do: they are one control's.</summary>
        internal bool ListIsGrouped => IsButtonsListView && HasGamepadRows(OutputType);

        /// <summary>Gives the table's rows their group headers, or takes
        /// them away. The view is left alone when it is already as wanted,
        /// since a change builds every row again.</summary>
        private void SyncListGroups(System.ComponentModel.ICollectionView rowView)
        {
            var groups = rowView?.GroupDescriptions;
            if (groups == null) return;
            bool want = ListIsGrouped;
            if (want == groups.Count > 0) return;
            if (want)
                groups.Add(new System.Windows.Data.PropertyGroupDescription(nameof(MappingItem.Category)));
            else
                groups.Clear();
        }

        private readonly HashSet<MappingCategory> _closedListGroups = new HashSet<MappingCategory>
        {
            MappingCategory.LeftStick, MappingCategory.RightStick,
        };

        /// <summary>Whether a group of the list shows its rows. The two
        /// sticks' groups start folded, as the List board opens (#574), and
        /// every other group starts open. The state is kept here by
        /// category, since the table builds its groups again whenever its
        /// rows change. Session only, never saved.</summary>
        public bool IsListGroupOpen(MappingCategory category) => !_closedListGroups.Contains(category);

        /// <summary>Records that a group of the list was opened or folded.
        /// The page shows it (PadPage.SyncListGroup).</summary>
        public void SetListGroupOpen(MappingCategory category, bool open)
        {
            if (open) _closedListGroups.Remove(category);
            else _closedListGroups.Add(category);
        }

        // ─────────────────────────────────────────────
        //  The line under the picture (#574)
        // ─────────────────────────────────────────────

        /// <summary>A recording is waiting for an input on this slot: one
        /// started on the picture, by a row's record button or by Map
        /// All.</summary>
        public bool IsListening
            => !string.IsNullOrEmpty(_currentRecordingTarget) || !string.IsNullOrEmpty(_mapAllPromptText);

        /// <summary>The line under the picture shows: in the picture's view,
        /// a recording waits for an input, or the last one asked for could
        /// not start. The list has no such line: a row shows its own
        /// recording state.</summary>
        public bool ShowPictureLine => IsButtonsPictureView && (IsListening || _recordRefusal != null);

        /// <summary>What the line under the picture says: Map All's prompt
        /// while it runs, the control being recorded while one is, then why
        /// the last recording could not start, and otherwise how to map a
        /// control.</summary>
        public string PictureLineText
        {
            get
            {
                if (!string.IsNullOrEmpty(_mapAllPromptText)) return _mapAllPromptText;
                var row = RowOfTarget(_currentRecordingTarget);
                if (row != null) return string.Format(Strings.Instance.Pad_Listening_Format, row.TargetLabel);
                if (_recordRefusal != null) return _recordRefusal;
                return PictureHintText;
            }
        }

        private void RaiseListeningChanged()
        {
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(ShowPictureLine));
            OnPropertyChanged(nameof(PictureLineText));
        }

        /// <summary>How long the line under the picture says why a
        /// recording could not start, when nothing else clears it.</summary>
        internal static readonly TimeSpan RecordRefusalShown = TimeSpan.FromSeconds(6);

        private string _recordRefusal;
        private System.Windows.Threading.DispatcherTimer _recordRefusalTimer;

        /// <summary>Why the last recording asked for on this slot could not
        /// start, or null. Session only, never saved.</summary>
        public string RecordRefusal => _recordRefusal;

        /// <summary>The recorder refused a recording on this slot: no device
        /// of the slot is connected, or none has the motion sensor a motion
        /// row records. A click on a control did nothing else, so the line
        /// under the picture says why, where the click was, until the next
        /// pick, a recording that starts, or <see cref="RecordRefusalShown"/>
        /// passes. The status line at the window's foot says it too.</summary>
        public void ShowRecordRefusal(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            _recordRefusal = reason;
            if (_recordRefusalTimer == null)
            {
                _recordRefusalTimer = new System.Windows.Threading.DispatcherTimer { Interval = RecordRefusalShown };
                _recordRefusalTimer.Tick += (_, _) => ClearRecordRefusal();
            }
            _recordRefusalTimer.Stop();
            _recordRefusalTimer.Start();
            OnPropertyChanged(nameof(RecordRefusal));
            RaiseListeningChanged();
        }

        /// <summary>The line no longer says why a recording could not
        /// start.</summary>
        private void ClearRecordRefusal()
        {
            _recordRefusalTimer?.Stop();
            if (_recordRefusal == null) return;
            _recordRefusal = null;
            OnPropertyChanged(nameof(RecordRefusal));
            RaiseListeningChanged();
        }

        // ─────────────────────────────────────────────
        //  What the folded output cards say (#574)
        // ─────────────────────────────────────────────

        /// <summary>The SOCD card's value while folded: the mode's name.</summary>
        public string SocdSummary
        {
            get
            {
                string mode = SocdMode;
                foreach (var option in AvailableSlotSocdModes)
                    if (string.Equals(option.Value, mode, StringComparison.Ordinal))
                        return option.Name;
                return Strings.Instance.Common_Off;
            }
        }
    }
}
