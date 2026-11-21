using System.Globalization;
using PadForge.Common;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Flipping CurrentUICulture is process-global state: run in
    /// isolation so parallel tests that read localized strings never observe
    /// the mid-test switch (3 unrelated tests failed exactly that way on
    /// this file's first run).</summary>
    [CollectionDefinition("CultureSwitching", DisableParallelization = true)]
    public class CultureSwitchingCollection { }

    /// <summary>
    /// Live language switching must rebuild every localized picker list.
    /// The failure mode this locks against is silent: a static readonly
    /// backing list (or an x:Static ItemsSource) captures the labels once
    /// at class load, the language change updates every directly-bound
    /// string, and only the dropdowns keep the old language. That exact
    /// bug shipped in the menu editor and the mapping grid's Kind picker
    /// (owner report 2026-07-16).
    /// </summary>
    [Collection("CultureSwitching")]
    public class CultureChangeOptionListTests
    {
        /// <summary>Flips to German, asserts the menu editor option lists and
        /// the fire-mode description re-resolve, then restores the culture
        /// in a finally so no other test observes the switch.</summary>
        [Fact]
        public void MenuEditorOptionLists_RebuildOnCultureChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                var editor = new MenuEditorItem(new MenuDefinitionEntry());
                string fireEn = editor.FireOptions[0].Label;
                string fireDescEn = editor.SelectedFireDescription;
                string kindEn = editor.KindOptions[0].Label;
                string bindEn = editor.Cells[0].BindingKindOptions[0].Label;

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));

                Assert.NotEqual(fireEn, editor.FireOptions[0].Label);
                Assert.NotEqual(fireDescEn, editor.SelectedFireDescription);
                Assert.NotEqual(kindEn, editor.KindOptions[0].Label);
                Assert.NotEqual(bindEn, editor.Cells[0].BindingKindOptions[0].Label);

                // The rebuilt lists must resolve to the ACTIVE culture's
                // resources, not merely differ from English.
                Assert.Equal(Strings.Instance.Menu_Fire_Click, editor.FireOptions[0].Label);
                Assert.Equal(Strings.Instance.Menu_Fire_Click_Desc, editor.SelectedFireDescription);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>Haitian Creole and Chuukese share LCID 4096, the number
        /// .NET gives every language Windows has none for. The option lists
        /// were stamped with the LCID, so a switch from one to the other kept
        /// the first one's text (#505). Each probe reads differently in the
        /// two languages, which the loop checks first, so the test cannot
        /// pass on two equal lists.</summary>
        [Fact]
        public void OptionLists_FollowASwitchBetweenLanguagesThatShareAnLcid()
        {
            var before = CultureInfo.CurrentUICulture;
            var haitian = CultureInfo.GetCultureInfo("ht");
            var chuukese = CultureInfo.GetCultureInfo("chk");
            Assert.Equal(haitian.LCID, chuukese.LCID);
            try
            {
                var mapping = new MappingItem("Gyro", "MotionGyro", MappingCategory.Motion);
                var kbm = new KbmSlotConfig();
                var editor = new MenuEditorItem(new MenuDefinitionEntry());

                Strings.ChangeCulture(haitian);
                string[] first = Probe(mapping, kbm, editor);

                Strings.ChangeCulture(chuukese);
                string[] second = Probe(mapping, kbm, editor);
                var s = Strings.Instance;
                string[] expected =
                {
                    s.Pad_Mapping_Kind_Direct,
                    s.Pad_Combine_MaxAbs_Name,
                    s.Pad_Mapping_MotionSpeed_Name,
                    s.Pad_Kbm_Surfaces_MouseOnly_Name,
                    s.Pad_Kbm_Socd_Mode_Off_Name,
                    s.Key_Up,
                    s.Menu_Fire_Click,
                    s.Curve_Linear,
                    s.Curve_Linear,
                    // The menu cell's key list takes the macro editor's name
                    // for the key, which reads Key_Up.
                    MacroAction.VirtualKeyDisplayName(VirtualKey.Up),
                };
                Assert.Equal(s.Key_Up, expected[^1]);

                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.NotEqual(expected[i], first[i]);
                    Assert.Equal(expected[i], second[i]);
                }
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>The first option of each cached list, in the order the
        /// test above expects them.</summary>
        private static string[] Probe(MappingItem mapping, KbmSlotConfig kbm, MenuEditorItem editor)
        {
            StickConfigItem.EnsurePresetsCultureCurrent();
            TriggerConfigItem.EnsurePresetsCultureCurrent();
            return new[]
            {
                MappingSourceItem.KindOptions[0].Name,
                mapping.AvailableCombineModes.Single(o => o.Value == "MaxAbs").Name,
                mapping.MotionResponseOptions[0].Name,
                kbm.AvailableKbmSurfaces.Single(o => o.Value == "MouseOnly").Name,
                kbm.AvailableSocdModes.Single(o => o.Value == "Off").Name,
                KbmSlotConfig.GetKeyOptions().Single(o => o.Vk == 0x26).Label,
                editor.FireOptions[0].Label,
                StickConfigItem.CurvePresetNames[0],
                TriggerConfigItem.CurvePresetNames[0],
                editor.Cells[0].KeyOptions.Single(o => o.Vk == 0x26).Label,
            };
        }

        [Fact]
        public void MappingKindOptions_RebuildOnCultureChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                string en = MappingSourceItem.KindOptions[0].Name;

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                Assert.Equal(Strings.Instance.Pad_Mapping_Kind_Direct,
                    MappingSourceItem.KindOptions[0].Name);
                Assert.NotEqual(en, MappingSourceItem.KindOptions[0].Name);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }
}
