using System;
using System.Collections.Generic;
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
            OnPropertyChanged(nameof(MacrosPartIndex));
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

        private void RaiseButtonsViewChanged()
        {
            OnPropertyChanged(nameof(ButtonsViewIndex));
            OnPropertyChanged(nameof(IsButtonsPictureView));
            OnPropertyChanged(nameof(IsButtonsListView));
            OnPropertyChanged(nameof(ShowOutputGroup));
        }

        /// <summary>Which part of Macros &amp; Menus shows: 0 the macros,
        /// 1 the menus.</summary>
        public int MacrosPartIndex
            => Math.Max(0, Array.IndexOf(s_sectionTabs[MacrosSection], _lastTabOfSection[MacrosSection]));

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
            }
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
