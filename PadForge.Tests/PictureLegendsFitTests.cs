using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Engine;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The words drawn inside the Keys and Notes pictures stay inside what
    /// holds them. A key's legend fits inside its key with room at each
    /// side: NumLk was cut at its key's right edge, and Home, PgUp and PgDn
    /// ran from edge to edge of theirs. A long legend draws smaller, a short
    /// one keeps its size. The note under an approximate keyboard wraps to
    /// the keyboard's width, and the MIDI picture is as wide as its section
    /// titles, which a slot with one control cut off at the picture's edge.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class PictureLegendsFitTests
    {
        /// <summary>The room a legend keeps at each side of its key, in the
        /// keyboard's own units: a key is 22 wide.</summary>
        private const double LegendRoom = 2;

        private static void Lay(FrameworkElement view, double width, double height)
        {
            var size = new Size(width, height);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
        }

        /// <summary>The size a text block's words take on one line, unclipped.</summary>
        private static Size Natural(TextBlock text)
        {
            var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            var formatted = new FormattedText(text.Text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, text.FontSize, Brushes.Black, 1.0);
            return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
        }

        /// <summary>The folder of the English strings and their translations.</summary>
        private static string StringsFolder()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!.FullName, "PadForge.App", "Resources", "Strings");
        }

        private static TextBlock LegendOf(Border key)
            => key.Child as TextBlock ?? (key.Child as Viewbox)?.Child as TextBlock;

        /// <summary>Checks every key the keyboard canvas holds: where its
        /// legend's words are drawn, in the key's own units.</summary>
        private static int EveryLegendFits(string board, KBMPreviewView view, Canvas keys)
        {
            int checkedKeys = 0;
            foreach (var key in keys.Children.OfType<Border>())
            {
                var legend = LegendOf(key);
                if (legend == null || string.IsNullOrEmpty(legend.Text)) continue;
                Size words = Natural(legend);
                Rect drawn = legend.TransformToAncestor(key).TransformBounds(new Rect(new Point(0, 0), words));
                string where = $"{board}: \"{legend.Text}\" is drawn at {drawn.Left:0.##} to {drawn.Right:0.##} in a key {key.Width:0.##} wide";
                Assert.True(drawn.Left >= LegendRoom - 0.01, where);
                Assert.True(drawn.Right <= key.Width - LegendRoom + 0.01, where);
                Assert.True(drawn.Top >= -0.01 && drawn.Bottom <= key.Height + 0.01, where + $", {drawn.Top:0.##} to {drawn.Bottom:0.##} in a key {key.Height:0.##} tall");
                checkedKeys++;
            }
            return checkedKeys;
        }

        [Fact]
        public void EveryKeyLegendFitsInsideItsKeyWithRoomAtEachSide()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var view = new KBMPreviewView();
                var keys = (Canvas)view.FindName("KeyboardCanvas");
                var generic = typeof(KBMPreviewView).GetMethod("BuildGenericKeyboard", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var device = typeof(KBMPreviewView).GetMethod("BuildDeviceKeyboard", BindingFlags.NonPublic | BindingFlags.Instance)!;

                keys.Children.Clear();
                generic.Invoke(view, new object[] { null });
                Lay(view, 1500, 700);
                Assert.True(EveryLegendFits("the generic keyboard", view, keys) > 100);

                foreach (string id in KeyboardFamilies.ProfileIds.OrderBy(i => i, StringComparer.Ordinal))
                {
                    keys.Children.Clear();
                    device.Invoke(view, new object[] { KeyboardFamilies.For(id), null });
                    Lay(view, 1500, 700);
                    Assert.True(EveryLegendFits(id, view, keys) > 0, id);
                }
            }, TimeSpan.FromMinutes(2), "the key legend test timed out");
        }

        /// <summary>The note under a keyboard drawn as the nearest standard
        /// set stays inside the narrowest such keyboard in every language
        /// PadForge ships.</summary>
        [Fact]
        public void TheApproximateNoteStaysInsideTheKeyboardsWidth()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var before = CultureInfo.CurrentUICulture;
                var cultures = System.IO.Directory.GetFiles(StringsFolder(), "Strings.*.resx")
                    .Select(file => System.IO.Path.GetFileNameWithoutExtension(file).Substring("Strings.".Length))
                    .Prepend("en").ToList();
                Assert.True(cultures.Count > 100);
                foreach (string culture in cultures)
                try
                {
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                    var view = new KBMPreviewView();
                    var keys = (Canvas)view.FindName("KeyboardCanvas");
                    var device = typeof(KBMPreviewView).GetMethod("BuildDeviceKeyboard", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var family = KeyboardFamilies.For("apple-magic-keyboard-a1644");
                    Assert.True(family.Approximate);
                    keys.Children.Clear();
                    device.Invoke(view, new object[] { family, null });
                    Lay(view, 1500, 700);
                    var note = keys.Children.OfType<TextBlock>().Single();
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Pad_Kbm_Preview_Approximate, note.Text);
                    double right = Canvas.GetLeft(note) + note.ActualWidth;
                    double bottom = Canvas.GetTop(note) + note.ActualHeight;
                    Assert.True(right <= keys.Width + 0.01,
                        $"{culture}: the note runs to {right:0.##} on a keyboard {keys.Width:0.##} wide ({Natural(note).Width:0.##} on one line)");
                    Assert.True(bottom <= keys.Height + 0.01, $"{culture}: the note ends at {bottom:0.##} under a canvas {keys.Height:0.##} tall");
                }
                finally { CultureInfo.CurrentUICulture = before; }
            }, TimeSpan.FromMinutes(2), "the approximate note test timed out");
        }

        /// <summary>The MIDI picture is as wide as its section titles: a
        /// slot with one control or one note shows "CC Outputs" and "Note
        /// Outputs" whole.</summary>
        [Fact]
        public void TheMidiPicturesTitlesStayInsideIt()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                foreach (var (cc, notes) in new[] { (1, 0), (0, 1), (1, 1), (6, 6) })
                {
                    var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Midi };
                    vm.MidiConfig.CcCount = cc;
                    vm.MidiConfig.NoteCount = notes;
                    var view = new MidiPreviewView();
                    view.Bind(vm);
                    var canvas = (Canvas)view.FindName("MidiCanvas");
                    Lay(view, 1200, 600);
                    foreach (var text in canvas.Children.OfType<TextBlock>())
                    {
                        double right = Canvas.GetLeft(text) + Natural(text).Width;
                        Assert.True(right <= canvas.Width + 0.01,
                            $"{cc} controls, {notes} notes: \"{text.Text}\" runs to {right:0.##} in a picture {canvas.Width:0.##} wide");
                    }
                    view.Unbind();
                }
            }, TimeSpan.FromMinutes(2), "the MIDI title test timed out");
        }
    }
}
