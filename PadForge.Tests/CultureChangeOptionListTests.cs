using System.Globalization;
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
    /// bug shipped on the Menus tab and the mapping grid's Kind picker
    /// (owner report 2026-07-16).
    /// </summary>
    [Collection("CultureSwitching")]
    public class CultureChangeOptionListTests
    {
        /// <summary>Flips to German, asserts the Menus-tab option lists and
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
