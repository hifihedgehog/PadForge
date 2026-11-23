using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using PadForge.Common.Themes;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every ContextMenu wears the look in force: the tray icon's menu, the
    /// pages' menus and the profile dialog's. The typography pass of
    /// 2026-11-17 gave the application an implicit ContextMenu style based on
    /// {x:Type ContextMenu}. In the application's own dictionary that lookup
    /// found the style's own key, its base came out empty, and every menu drew
    /// WPF's classic light template on every theme.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ContextMenuThemeTests
    {
        private static string RepoRoot()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }

        [Fact]
        public void AMenuTakesWpfUisMenuInTheLooksOwnColors()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (var (index, id, dark) in new[]
                             {
                                 (2, (string)null, true), (1, (string)null, false),
                                 (2, "Starter:sonic-blue-blur", true), (2, "Starter:rings-map-of-middle-earth", false),
                             })
                    {
                        ThemeManager.Apply(index, id);
                        string look = id ?? (index == 1 ? "Light" : "Dark");
                        // Built as the tray builds its menu, and laid out where it stands, so no popup opens on the screen.
                        var show = new MenuItem { Header = "Show PadForge", FontWeight = FontWeights.SemiBold };
                        var menu = new ContextMenu();
                        menu.Items.Add(show);
                        menu.Items.Add(new Separator());
                        menu.Items.Add(new MenuItem { Header = "Exit" });
                        // An element made in code takes its implicit style when it is initialized.
                        menu.BeginInit();
                        menu.EndInit();
                        Assert.True(menu.ApplyTemplate(), look + ": the menu took no template");
                        menu.Measure(new Size(400, 400));
                        menu.Arrange(new Rect(menu.DesiredSize));

                        var root = VisualTreeHelper.GetChild(menu, 0);
                        Assert.True(root is Wpf.Ui.Controls.EffectThicknessDecorator,
                            look + ": the menu drew WPF's classic template, " + root.GetType().FullName);
                        Color ground = ((SolidColorBrush)app.FindResource("ContextMenuBackground")).Color;
                        Assert.Equal(ground, ((SolidColorBrush)menu.Background).Color);
                        Assert.True(dark == ThemeColor.Luma(ground) < 0.5, look + ": the menu's ground " + ground + " is not of the look's base");
                        Color ink = ((SolidColorBrush)show.Foreground).Color;
                        Assert.True(ThemeColor.Contrast(ink, ground) >= 4.5, look + ": the words " + ink + " do not read on the menu's " + ground);
                        // The reason the application has a menu style at all: the body face reaches every menu.
                        Assert.Equal(app.FindResource("BodyFontFamily"), menu.FontFamily);
                    }
                }
                finally
                {
                    ThemeManager.Apply(2, null);
                }
            }, TimeSpan.FromMinutes(2), "the menu test timed out");
        }

        /// <summary>A style in the application's own dictionaries that is
        /// implicit and based on its own type finds itself, and its base comes
        /// out empty. A style in a page's or another style's resources looks
        /// past itself, as the pages' styles do.</summary>
        [Fact]
        public void NoApplicationStyleIsBasedOnItsOwnType()
        {
            string app = Path.Combine(RepoRoot(), "PadForge.App");
            // App.xaml, the two dictionaries it merges, and the dialogs' dictionary DialogWindow merges into the application.
            string[] files = { "App.xaml", "Resources/ControllerIcons.xaml", "Resources/Shell.xaml", "Resources/Dialogs.xaml" };
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var typeKey = new Regex(@"^\{x:Type\s+([^}\s]+)\s*\}$");
            var selfBase = new Regex(@"^\{StaticResource\s+\{x:Type\s+([^}\s]+)\s*\}\s*\}$");
            var found = new List<string>();
            var examined = new List<string>();
            foreach (string file in files)
            {
                var document = XDocument.Load(Path.Combine(app, file), LoadOptions.SetLineInfo);
                foreach (var style in document.Descendants().Where(e => e.Name.LocalName == "Style"))
                {
                    if (style.Parent?.Name.LocalName != "ResourceDictionary" || style.Attribute(x + "Key") != null) continue;
                    string basedOn = ((string)style.Attribute("BasedOn"))?.Trim();
                    if (basedOn == null) continue;
                    string target = ((string)style.Attribute("TargetType") ?? string.Empty).Trim();
                    var named = typeKey.Match(target);
                    if (named.Success) target = named.Groups[1].Value;
                    examined.Add(target);
                    var self = selfBase.Match(basedOn);
                    if (self.Success && self.Groups[1].Value == target)
                        found.Add(file + ":" + ((IXmlLineInfo)style).LineNumber + " " + target);
                }
            }
            Assert.Empty(found);
            // Positive controls: the walk reads the application's implicit styles with a base, the menu's among them.
            Assert.Contains("ContextMenu", examined);
            Assert.True(examined.Count >= 4, "the walk examined only " + examined.Count + " styles");
        }
    }
}
