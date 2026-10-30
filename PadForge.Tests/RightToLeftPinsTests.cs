using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #505: Arabic, Persian, Urdu and Central Kurdish read right to left,
    /// and a window in one of them mirrors its layout. WPF mirrors every
    /// element that inherits the direction, drawings included: rendered on
    /// the bench before the pins went in, the pad page's controller came out
    /// with A, B, X and Y on the left and the left stick on the right.
    ///
    /// <para>So every drawing declares FlowDirection="LeftToRight" itself or
    /// sits under an element that does, and WPF un-mirrors it at that
    /// element. These tests read the markup and hold that for every Canvas,
    /// Path, Polygon, Polyline, Line, Image and 3D viewport, so a drawing
    /// added later cannot quietly mirror. Ellipses and rectangles are left
    /// out: on their own they are the same either way.</para>
    /// </summary>
    public class RightToLeftPinsTests
    {
        private static readonly HashSet<string> Drawings = new(StringComparer.Ordinal)
            { "Canvas", "Path", "Polygon", "Polyline", "Line", "Image", "Viewport3D", "HelixViewport3D", "InkCanvas" };

        /// <summary>The one drawing that must mirror with its window: the
        /// welcome tour cuts its spotlight out at the window coordinates of
        /// the control it points at.</summary>
        private const string Mirrors = "TourCanvas";

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        private static string AppDirectory()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return Path.Combine(d.FullName, "PadForge.App");
        }

        private static IEnumerable<string> AppSources(string pattern)
        {
            string app = AppDirectory();
            foreach (string path in Directory.EnumerateFiles(app, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(app, path).Replace('\\', '/');
                if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal))
                    continue;
                yield return path;
            }
        }

        /// <summary>The drawings in one markup file that would mirror: each
        /// as "name at line". <paramref name="named"/> collects the x:Name of
        /// every unpinned drawing that has one.</summary>
        internal static List<string> Unpinned(string xaml, List<string> named = null)
        {
            var found = new List<string>();
            var pinned = new Stack<bool>();
            using var reader = XmlReader.Create(new StringReader(xaml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            var lines = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    pinned.Pop();
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element) continue;
                bool here = (pinned.Count > 0 && pinned.Peek()) || reader.GetAttribute("FlowDirection") == "LeftToRight";
                if (!here && Drawings.Contains(reader.LocalName))
                {
                    string name = reader.GetAttribute("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
                    if (name != null) named?.Add(name);
                    if (name != Mirrors) found.Add(reader.LocalName + " at line " + lines.LineNumber);
                    // What sits inside it is answered for by this one: a pin
                    // here covers it, and the tour's own shapes mirror with it.
                    here = true;
                }
                if (!reader.IsEmptyElement) pinned.Push(here);
            }
            return found;
        }

        [Fact]
        public void EveryDrawingInTheMarkupStaysLeftToRight()
        {
            var offenders = new List<string>();
            int files = 0;
            foreach (string path in AppSources("*.xaml"))
            {
                files++;
                foreach (string drawing in Unpinned(File.ReadAllText(path)))
                    offenders.Add(Path.GetFileName(path) + ": " + drawing);
            }
            Assert.True(files > 40, "only " + files + " markup files were read");
            Assert.True(offenders.Count == 0,
                offenders.Count + " drawing(s) would mirror in a right-to-left language. Add FlowDirection=\"LeftToRight\" to each or to an element above it: "
                + string.Join("; ", offenders.Take(40)));
        }

        [Fact]
        public void TheWelcomeTourIsTheOneDrawingThatMirrors()
        {
            var named = new List<string>();
            foreach (string path in AppSources("*.xaml"))
                Unpinned(File.ReadAllText(path), named);
            Assert.Equal(new[] { Mirrors }, named.Where(n => n == Mirrors).ToArray());
        }

        /// <summary>The check above reports nothing on the tree, which is
        /// worth little unless it can report something. Fed a drawing with
        /// no pin, one pinned itself and one under a pinned parent, it must
        /// name the first alone.</summary>
        [Fact]
        public void TheCheckSeparatesAPinnedDrawingFromAnUnpinnedOne()
        {
            const string head = "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";
            Assert.Equal(new[] { "Path at line 1" }, Unpinned(head + "<Path/></Grid>"));
            Assert.Empty(Unpinned(head + "<Path FlowDirection=\"LeftToRight\"/></Grid>"));
            Assert.Empty(Unpinned(head + "<Border FlowDirection=\"LeftToRight\"><StackPanel><Image/><Canvas><Line/></Canvas></StackPanel></Border></Grid>"));
            Assert.Equal(new[] { "Canvas at line 1" }, Unpinned(head + "<Border FlowDirection=\"LeftToRight\"/><Canvas><Line/></Canvas></Grid>"));
            Assert.Equal(new[] { "Image at line 1" }, Unpinned(head + "<Image FlowDirection=\"RightToLeft\"/></Grid>"));
            Assert.Empty(Unpinned(head + "<Ellipse/><Rectangle/><TextBlock/></Grid>"));
            Assert.Empty(Unpinned(head + "<Canvas x:Name=\"TourCanvas\"/></Grid>"));
        }

        /// <summary>A drawing built in code is in no markup file, so the
        /// check above cannot see it. Each is counted here, by file. All but
        /// the main window's are added under a parent that is pinned: the
        /// canvas of the control that builds them, the lightbar host, or a
        /// window that keeps left to right. The main window's nine are the
        /// flames and the platform logos of the navigation rail and its
        /// pop-ups, the type bar's change-type row among them (discussion
        /// #543), which go into mirrored rows, so each pins itself.
        /// MarqueeLabel's canvas carries text, not a drawing, and mirrors
        /// with the language on purpose: in a right-to-left one the hidden
        /// end of a name sits on the left, and the scroll must reveal it. A
        /// new one moves a count and sends its author here.</summary>
        [Fact]
        public void EveryDrawingBuiltInCodeIsOnTheList()
        {
            const string types = "Canvas|Path|Polygon|Polyline|Image|Viewport3D|HelixViewport3D";
            var pattern = new Regex(@"\bnew\s+(?:[A-Za-z_][\w.]*\.)?(?:" + types + @")\b\s*[({]|\b(?:" + types + @")\s+\w+\s*=\s*new\s*\(");
            var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in AppSources("*.cs"))
            {
                int count = pattern.Matches(File.ReadAllText(path)).Count;
                if (count > 0) found[Path.GetFileName(path)] = count;
            }
            var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["ControllerModel2DView.xaml.cs"] = 2,
                ["ControllerSchematicView.xaml.cs"] = 4,
                ["CurveEditor.xaml.cs"] = 1,
                ["GunCalibrationScreen.cs"] = 1,
                ["KBMPreviewView.xaml.cs"] = 4,
                ["MainWindow.xaml.cs"] = 9,
                ["MarqueeLabel.cs"] = 1,
                ["MenuOverlayWindow.xaml.cs"] = 2,
                ["MouseGlyph.cs"] = 3,
                ["PadPage.xaml.cs"] = 4,
                ["TouchpadGestureRecorderDialog.xaml.cs"] = 1,
                ["TriggerEffectGraph.xaml.cs"] = 2,
                ["TriggerTravelArc.xaml.cs"] = 2,
                ["VRPreviewView.xaml.cs"] = 4,
                ["WorkshopControllerPreview.xaml.cs"] = 2,
            };
            Assert.Equal(expected, found);

            string pad = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "PadPage.xaml"));
            Assert.Contains("<Grid x:Name=\"LightbarPreviewHost\" FlowDirection=\"LeftToRight\"/>", pad);

            string main = File.ReadAllText(Path.Combine(AppDirectory(), "MainWindow.xaml.cs"));
            int pinned = Regex.Matches(main,
                @"new System\.Windows\.Shapes\.Path\s*\{\s*FlowDirection = FlowDirection\.LeftToRight,").Count;
            Assert.Equal(9, pinned);
        }

        /// <summary>The left motor's capsule stays under the controller's
        /// left grip. The row is a Grid, not a drawing, so the general check
        /// does not cover it.</summary>
        [Fact]
        public void TheMotorCapsulesKeepTheirSides()
        {
            string pad = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "PadPage.xaml"));
            Match row = Regex.Match(pad, @"<Grid x:Name=""MotorBarsGrid""[^>]*>");
            Assert.True(row.Success);
            Assert.Contains("FlowDirection=\"LeftToRight\"", row.Value);
        }

        [Fact]
        public void TheEqualizerCurvePinsItself()
        {
            RunSta(() =>
            {
                var curve = new PadForge.Controls.EqCurveControl();
                var host = new Grid { FlowDirection = FlowDirection.RightToLeft };
                host.Children.Add(curve);
                Assert.Equal(FlowDirection.LeftToRight, curve.FlowDirection);
            });
        }

        /// <summary>The phone pages hold fixed English text, whatever the
        /// interface language is: they are not translated. So each declares
        /// English and no direction. A page that took the interface language
        /// would tell a screen reader its English was Arabic and lay it out
        /// right to left.</summary>
        [Fact]
        public void ThePhonePagesDeclareTheEnglishTheyAreWrittenIn()
        {
            string[] pages = Directory.GetFiles(Path.Combine(AppDirectory(), "WebAssets"), "*.html");
            Assert.True(pages.Length >= 6, "only " + pages.Length + " phone pages were found");
            foreach (string page in pages)
            {
                string html = File.ReadAllText(page);
                Assert.True(html.Contains("<html lang=\"en\">", StringComparison.Ordinal), Path.GetFileName(page) + " declares no language");
                Assert.False(Regex.IsMatch(html, @"<(html|body)[^>]*\sdir="), Path.GetFileName(page) + " declares a direction");
            }
        }

        /// <summary>A window that pictures a physical thing keeps left to
        /// right whatever the language: the touchpad overlay, the on-screen
        /// menus and the light gun's calibration targets.</summary>
        [Fact]
        public void AWindowThatPicturesAPhysicalThingKeepsLeftToRight()
        {
            RunSta(() =>
            {
                XmlLanguage language = UiLanguage.Undeclared;
                var ordinary = new Window();
                UiLanguage.Declare(ordinary, language, FlowDirection.RightToLeft);
                Assert.Equal(FlowDirection.RightToLeft, ordinary.FlowDirection);

                var picture = new Window();
                UiLanguage.KeepLeftToRight(picture);
                UiLanguage.Declare(picture, language, FlowDirection.RightToLeft);
                Assert.Equal(FlowDirection.LeftToRight, picture.FlowDirection);
                UiLanguage.KeepLeftToRight(null);
            });

            string views = Path.Combine(AppDirectory(), "Views");
            foreach (string file in new[] { "TouchpadOverlay.xaml.cs", "MenuOverlayWindow.xaml.cs" })
                Assert.Contains("PadForge.Common.UiLanguage.KeepLeftToRight(this);", File.ReadAllText(Path.Combine(views, file)));
            Assert.Contains("PadForge.Common.UiLanguage.KeepLeftToRight(Window);",
                File.ReadAllText(Path.Combine(views, "GunCalibrationScreen.cs")));
        }
    }
}
