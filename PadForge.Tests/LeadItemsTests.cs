using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The parts a panel makes from a list and stands before its own
    /// (#574): the inputs of a macro's trigger before Record and Add from
    /// List on one line.
    /// </summary>
    public class LeadItemsTests
    {
        /// <summary>A part of the panel's own that counts how often it is
        /// put under a parent or taken from one.</summary>
        private sealed class Fixed : Border
        {
            public int Moves;

            protected override void OnVisualParentChanged(DependencyObject oldParent)
            {
                Moves++;
                base.OnVisualParentChanged(oldParent);
            }
        }

        private static DataTemplate Words()
            => (DataTemplate)XamlReader.Parse(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}'/></DataTemplate>");

        private static (object Item, DataTemplate Template)[] Made(Panel panel, int count)
            => panel.Children.Cast<UIElement>().Take(count)
                .Select(c => Assert.IsType<ContentPresenter>(c))
                .Select(p => (p.Content, p.ContentTemplate)).ToArray();

        /// <summary>One part for each item, in the list's order, before the
        /// panel's own parts, and the part of the word between each two.
        /// The first item has no word before it and the last none after
        /// it.</summary>
        [Fact]
        public void APartIsMadeForEachItem_BeforeThePanelsOwn_WithTheWordBetweenTwo()
        {
            RunSta(() =>
            {
                var panel = new StackPanel();
                var record = new Fixed();
                var list = new Fixed();
                panel.Children.Add(record);
                panel.Children.Add(list);
                var pill = Words();
                var and = Words();
                LeadItems.SetTemplate(panel, pill);
                LeadItems.SetBetween(panel, and);
                Assert.Equal(new UIElement[] { record, list }, panel.Children.Cast<UIElement>());

                LeadItems.SetSource(panel, new[] { "A", "Left Trigger", "B" });
                Assert.Equal(7, panel.Children.Count);
                Assert.Equal(new (object, DataTemplate)[] { ("A", pill), ("Left Trigger", and), ("Left Trigger", pill), ("B", and), ("B", pill) },
                    Made(panel, 5));
                Assert.Equal(new UIElement[] { record, list }, panel.Children.Cast<UIElement>().Skip(5));

                // One item: its part alone, with no word.
                LeadItems.SetSource(panel, new[] { "A" });
                Assert.Equal(new (object, DataTemplate)[] { ("A", pill) }, Made(panel, 1));
                Assert.Equal(new UIElement[] { record, list }, panel.Children.Cast<UIElement>().Skip(1));

                // No word set: the items' parts alone.
                LeadItems.SetBetween(panel, null);
                LeadItems.SetSource(panel, new[] { "A", "B" });
                Assert.Equal(new (object, DataTemplate)[] { ("A", pill), ("B", pill) }, Made(panel, 2));
                Assert.Equal(4, panel.Children.Count);
            });
        }

        /// <summary>The panel's own parts are put under it once and never
        /// taken out, whatever the list does: a part that holds the
        /// keyboard focus keeps it when an input is recorded, picked or
        /// removed. Only the made parts are replaced.</summary>
        [Fact]
        public void ThePanelsOwnPartsStayWhereTheyAre_WhenTheListChanges()
        {
            RunSta(() =>
            {
                var panel = new StackPanel();
                var record = new Fixed();
                var list = new Fixed();
                LeadItems.SetTemplate(panel, Words());
                LeadItems.SetBetween(panel, Words());
                // A list that is there before the panel's own parts are.
                LeadItems.SetSource(panel, new[] { "A", "B" });
                panel.Children.Add(record);
                panel.Children.Add(list);
                Assert.Equal(5, panel.Children.Count);
                Assert.Equal(new UIElement[] { record, list }, panel.Children.Cast<UIElement>().Skip(3));

                var first = panel.Children[0];
                foreach (IEnumerable<string> next in new[] { new[] { "A", "B", "C" }, new[] { "C" }, Array.Empty<string>(), null, new[] { "A", "B" } })
                {
                    LeadItems.SetSource(panel, next);
                    int made = next == null ? 0 : Math.Max(0, next.Count() * 2 - 1);
                    Assert.Equal(made + 2, panel.Children.Count);
                    Assert.Equal(new UIElement[] { record, list }, panel.Children.Cast<UIElement>().Skip(made));
                }
                Assert.Equal(1, record.Moves);
                Assert.Equal(1, list.Moves);
                // The made parts are new ones each time.
                Assert.NotSame(first, panel.Children[0]);
            });
        }

        /// <summary>Nothing is made without the items' template, in
        /// whichever order the three are set, and each made part has its
        /// item as its data.</summary>
        [Fact]
        public void PartsAreMadeOnceTheTemplateIsThere_AndEachHasItsItemAsItsData()
        {
            RunSta(() =>
            {
                var panel = new StackPanel();
                var own = new Fixed();
                panel.Children.Add(own);
                LeadItems.SetSource(panel, new[] { "A", "B" });
                LeadItems.SetBetween(panel, Words());
                Assert.Equal(new UIElement[] { own }, panel.Children.Cast<UIElement>());

                LeadItems.SetTemplate(panel, Words());
                Assert.Equal(4, panel.Children.Count);
                panel.Measure(new Size(400, 400));
                Assert.Equal(new[] { "A", "B", "B" }, panel.Children.Cast<UIElement>().Take(3)
                    .Select(p => Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(p, 0)).Text));

                // The template taken away again: the made parts go, and the
                // panel's own stays.
                LeadItems.SetTemplate(panel, null);
                Assert.Equal(new UIElement[] { own }, panel.Children.Cast<UIElement>());
                Assert.Equal(1, own.Moves);
            });
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
