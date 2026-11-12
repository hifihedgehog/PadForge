using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The part that is made when it first shows (#574): the groups of a
    /// macro step's fields, of which a step shows one or two.
    /// </summary>
    public class ShownPartTests
    {
        private static DataTemplate Words()
            => (DataTemplate)XamlReader.Parse(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}'/></DataTemplate>");

        private static int Parts(DependencyObject part) => VisualTreeHelper.GetChildrenCount(part);

        /// <summary>A part holds nothing until it shows, however often it
        /// is measured. It builds its template from its own data when it
        /// shows, and keeps what it built, collapsed, when it hides again.
        /// With no template it holds nothing.</summary>
        [Fact]
        public void APartIsBuiltWhenItFirstShows()
        {
            RunSta(() =>
            {
                var part = new ShownPart { DataContext = "words", PartTemplate = Words() };
                Assert.False(part.Show);
                part.Measure(new Size(200, 200));
                part.Measure(new Size(300, 300));
                Assert.Null(part.Child);
                Assert.Equal(new Size(0, 0), part.DesiredSize);

                part.Show = true;
                var made = Assert.IsType<ContentPresenter>(part.Child);
                Assert.Same(part.PartTemplate, made.ContentTemplate);
                part.Measure(new Size(200, 200));
                Assert.Equal("words", Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(made, 0)).Text);
                Assert.NotEqual(new Size(0, 0), part.DesiredSize);

                part.Show = false;
                Assert.Same(made, part.Child);
                Assert.Equal(Visibility.Collapsed, made.Visibility);
                part.Measure(new Size(200, 200));
                Assert.Equal(new Size(0, 0), part.DesiredSize);
                Assert.Equal(1, Parts(made));

                part.Show = true;
                Assert.Same(made, part.Child);
                Assert.Equal(Visibility.Visible, made.Visibility);

                // What it made follows the part's data.
                part.DataContext = "other words";
                Assert.Equal("other words", made.Content);

                // No template: nothing to hold, until there is one.
                var bare = new ShownPart { DataContext = "words", Show = true };
                Assert.Null(bare.Child);
                bare.PartTemplate = Words();
                Assert.IsType<ContentPresenter>(bare.Child);
            });
        }

        /// <summary>In a template that has just been made, a part whose
        /// flag is false is never built, and one whose flag is true is.
        /// A plain presenter hidden by a binding on its own Visibility is
        /// built in the same place: its data, and so the binding's value,
        /// is nothing until its template is applied.</summary>
        [Fact]
        public void InANewTemplate_APartThatDoesNotShowIsNeverBuilt_WhereAHiddenPresenterIs()
        {
            RunSta(() =>
            {
                var outer = (ContentPresenter)XamlReader.Parse(
                    "<ContentPresenter xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'"
                    + " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:c='clr-namespace:PadForge.Controls;assembly=PadForge'>"
                    + "<ContentPresenter.Resources><DataTemplate x:Key='Inner'><TextBlock Text='{Binding [Words]}'/></DataTemplate></ContentPresenter.Resources>"
                    + "<ContentPresenter.ContentTemplate><DataTemplate><StackPanel>"
                    + "<c:ShownPart PartTemplate='{StaticResource Inner}' Show='{Binding [Off]}'/>"
                    + "<c:ShownPart PartTemplate='{StaticResource Inner}' Show='{Binding [On]}'/>"
                    + "<ContentPresenter Content='{Binding}' ContentTemplate='{StaticResource Inner}' Visibility='{Binding [Hidden]}'/>"
                    + "</StackPanel></DataTemplate></ContentPresenter.ContentTemplate></ContentPresenter>");
                outer.Content = new Dictionary<string, object> { ["Off"] = false, ["On"] = true, ["Hidden"] = Visibility.Collapsed, ["Words"] = "inner" };

                // One layout pass, then the bindings' turn, then another.
                for (int pass = 0; pass < 2; pass++)
                {
                    outer.Measure(new Size(300, 300));
                    outer.Arrange(new Rect(0, 0, 300, 300));
                    outer.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                }
                var panel = Assert.IsType<StackPanel>(VisualTreeHelper.GetChild(outer, 0));
                var off = Assert.IsType<ShownPart>(panel.Children[0]);
                var on = Assert.IsType<ShownPart>(panel.Children[1]);
                var hidden = Assert.IsType<ContentPresenter>(panel.Children[2]);

                Assert.False(off.Show);
                Assert.Null(off.Child);
                Assert.True(on.Show);
                var made = Assert.IsType<ContentPresenter>(on.Child);
                // The part's data is the template's.
                Assert.Equal("inner", Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(made, 0)).Text);
                // What the part is for: the hidden presenter was built.
                Assert.Equal(Visibility.Collapsed, hidden.Visibility);
                Assert.Equal(1, Parts(hidden));
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
