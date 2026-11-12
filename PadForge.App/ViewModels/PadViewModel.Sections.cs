using System;
using System.Collections.Generic;
using PadForge.Engine;
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
            OnPropertyChanged(nameof(ShowMidiCard));
            OnPropertyChanged(nameof(ShowExtendedCard));
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

        /// <summary>The bar's heading: the picked control's name. On a
        /// gamepad a stick's axes and the D-pad's directions are one
        /// control, named as the list's group is. Any other control is its
        /// one row.</summary>
        public string PickedName
        {
            get
            {
                var row = PickedRow;
                if (row == null) return string.Empty;
                if (!HasGamepadRows(OutputType)) return row.TargetLabel;
                return row.Category switch
                {
                    MappingCategory.DPad => Strings.Instance.Preview_DPad,
                    MappingCategory.LeftStick => Strings.Instance.Menu_Host_LeftStick,
                    MappingCategory.RightStick => Strings.Instance.Menu_Host_RightStick,
                    _ => row.TargetLabel,
                };
            }
        }

        /// <summary>The bar shows: under the picture, with a control
        /// picked.</summary>
        public bool ShowPictureBar => IsButtonsPictureView && PickedRow != null;

        /// <summary>A Keyboard + Mouse slot's foot shows at the foot of the
        /// picture's card, and under the list: what the slot's keyboard and
        /// its mouse type through.</summary>
        public bool ShowKbmFoot => OutputType == VirtualControllerType.KeyboardMouse;

        /// <summary>The picture is the card's foot, and takes its lower
        /// corners: nothing is picked, so no bar follows it, and the slot
        /// has no foot of its own.</summary>
        public bool PictureIsCardFoot => !ShowPictureBar && !ShowKbmFoot;

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
            OnPropertyChanged(nameof(PickedRow));
            OnPropertyChanged(nameof(PickedName));
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
            return IsFamilyCategory(rowA.Category) && rowA.Category == rowB.Category;
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
        /// pick of one of their rows shows them all.</summary>
        internal static bool IsFamilyCategory(MappingCategory category)
            => category is MappingCategory.DPad or MappingCategory.LeftStick or MappingCategory.RightStick;

        /// <summary>Whether a row belongs under the picture for a pick: the
        /// picked row itself, or any row of the picked row's family. Names
        /// and a category are compared, never row objects, so the answer
        /// holds after the rows are rebuilt.</summary>
        internal static bool RowMatchesPick(MappingItem m, string pickedTarget, MappingCategory? family)
        {
            if (m == null || string.IsNullOrEmpty(pickedTarget)) return false;
            if (string.Equals(m.TargetSettingName, pickedTarget, StringComparison.OrdinalIgnoreCase)) return true;
            return family.HasValue && m.Category == family.Value;
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

        private readonly HashSet<MappingCategory> _closedListGroups = new HashSet<MappingCategory>();

        /// <summary>Whether a group of the list shows its rows. Every group
        /// starts open. The state is kept here by category, since the table
        /// builds its groups again whenever its rows change. Session only,
        /// never saved.</summary>
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

        /// <summary>What the line under the picture says: Map All's prompt
        /// while it runs, the control being recorded while one is, and
        /// otherwise how to map a control.</summary>
        public string PictureLineText
        {
            get
            {
                if (!string.IsNullOrEmpty(_mapAllPromptText)) return _mapAllPromptText;
                var row = RowOfTarget(_currentRecordingTarget);
                if (row != null) return string.Format(Strings.Instance.Pad_Listening_Format, row.TargetLabel);
                return Strings.Instance.Pad_PictureHint;
            }
        }

        private void RaiseListeningChanged()
        {
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(PictureLineText));
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

        /// <summary>The Keep Controller Awake card's value while folded.</summary>
        public string KeepAwakeSummary
            => KeepAwakeEnabled ? Strings.Instance.Common_On : Strings.Instance.Common_Off;
    }
}
