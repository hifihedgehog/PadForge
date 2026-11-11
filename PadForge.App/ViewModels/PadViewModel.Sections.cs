using System;
using System.Collections.Generic;

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
    /// <para>Two kinds of section. Buttons and Macros &amp; Menus show one
    /// part at a time, because the preview, the mapping table, the macro
    /// editor and the menu editor each fill the page. The other four stack
    /// their tabs as groups in one scrolling column.</para>
    /// </summary>
    public partial class PadViewModel
    {
        /// <summary>How many sections the page has.</summary>
        public const int SectionCount = 6;

        /// <summary>Buttons: the preview, the mapping table and the output
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

        /// <summary>Each section's tabs, in the order the page shows them.
        /// Every tab is in exactly one section.</summary>
        private static readonly int[][] s_sectionTabs =
        {
            new[] { 0, 2, OutputTabIndex },
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
        /// groups. False for Buttons and Macros &amp; Menus, which show one
        /// part at a time.</summary>
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

        /// <summary>Puts every section back on its first tab. A deleted slot
        /// calls it, so the next controller made at this index does not open
        /// a section where the deleted one left it.</summary>
        private void ResetSectionMemory()
        {
            Array.Copy(FirstTabs(), _lastTabOfSection, SectionCount);
            int section = SectionOfTab(_selectedConfigTab);
            if (section >= 0) _lastTabOfSection[section] = _selectedConfigTab;
            OnPropertyChanged(nameof(ButtonsPartIndex));
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

        /// <summary>Which part of Buttons shows: 0 the preview, 1 the mapping
        /// table, 2 the output options. It holds while another section is
        /// selected.</summary>
        public int ButtonsPartIndex
            => Math.Max(0, Array.IndexOf(s_sectionTabs[ButtonsSection], _lastTabOfSection[ButtonsSection]));

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
        /// that section is selected. Buttons and Macros &amp; Menus show only
        /// the selected part. A group the page hides for the selected device
        /// still counts here, since only the page knows that.</summary>
        public bool IsTabShown(int tab)
        {
            int section = SectionOfTab(tab);
            if (section < 0 || section != SelectedSection) return false;
            return IsStackedSection(section) || tab == _selectedConfigTab;
        }
    }
}
