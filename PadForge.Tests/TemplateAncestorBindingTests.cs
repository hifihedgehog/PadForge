using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A binding that looks up its TabItem works only where the TabItem is
    /// still an ancestor when WPF resolves it. WPF resolves an ancestor lookup
    /// after a layout pass, never at once (BindingExpression.AttachToContext),
    /// and walks the visual parent first, the logical parent only where there
    /// is no visual one (FrameworkObject.GetRawPreferVisualParent). A tab's
    /// content shows inside the tab control's own presenter, not inside its
    /// TabItem. The pad page is built once, collapsed, so the fixed content of
    /// its tabs finds the TabItem through the logical parent. A template builds
    /// its rows while the tab shows, under the presenter, where no TabItem is
    /// an ancestor: each Keyboard Controls row's Record button had no command,
    /// and opening the Menus tab on a Keyboard menu logged 29 binding errors,
    /// one per row.
    /// </summary>
    public class TemplateAncestorBindingTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static readonly string[] TemplateNames = { "DataTemplate", "HierarchicalDataTemplate", "ControlTemplate" };

        /// <summary>A TabItem's own header and control template do sit inside
        /// the TabItem.</summary>
        private static bool BelongsToATabItem(XElement template) =>
            template.Ancestors().Any(a =>
                a.Name.LocalName is "TabItem.HeaderTemplate" or "TabItem.Template"
                || (a.Name.LocalName == "Style"
                    && ((string)a.Attribute("TargetType") ?? "") is "TabItem" or "{x:Type TabItem}"));

        private static bool LooksUpATabItem(string value) =>
            value.Contains("RelativeSource", StringComparison.Ordinal)
            && (value.Contains("AncestorType=TabItem", StringComparison.Ordinal)
                || value.Contains("AncestorType={x:Type TabItem}", StringComparison.Ordinal));

        [Fact]
        public void NoTemplate_LooksUpTheTabItemItShowsIn()
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (rel, text) in XamlA11yAudit.Files(RepoRoot()))
            {
                var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
                foreach (var template in doc.Descendants().Where(e => TemplateNames.Contains(e.Name.LocalName)))
                {
                    if (BelongsToATabItem(template)) continue;
                    foreach (var element in template.Descendants())
                        if (element.Attributes().Any(a => LooksUpATabItem(a.Value)))
                            found.Add($"{rel}:{((IXmlLineInfo)element).LineNumber}");
                }
            }
            Assert.True(found.Count == 0, "Template bindings that look up a TabItem: " + string.Join(", ", found));
        }

        [Fact]
        public void TheScan_SeesALookupInATemplate_AndPassesOneOutside()
        {
            // The positive control: the same scan on a page that has one of each.
            const string page = """
                <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <TabControl>
                    <TabItem>
                      <StackPanel>
                        <Button Command="{Binding DataContext.A, RelativeSource={RelativeSource AncestorType=TabItem}}"/>
                        <ItemsControl>
                          <ItemsControl.ItemTemplate>
                            <DataTemplate>
                              <Button Command="{Binding DataContext.B,
                                  RelativeSource={RelativeSource AncestorType=TabItem}}"/>
                            </DataTemplate>
                          </ItemsControl.ItemTemplate>
                        </ItemsControl>
                      </StackPanel>
                    </TabItem>
                  </TabControl>
                </UserControl>
                """;
            var doc = XDocument.Parse(page, LoadOptions.SetLineInfo);
            var lines = doc.Descendants().Where(e => TemplateNames.Contains(e.Name.LocalName))
                .Where(t => !BelongsToATabItem(t))
                .SelectMany(t => t.Descendants())
                .Where(e => e.Attributes().Any(a => LooksUpATabItem(a.Value)))
                .Select(e => ((IXmlLineInfo)e).LineNumber)
                .ToList();
            Assert.Equal(new[] { 10 }, lines);
        }
    }
}
