using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PadForge.Common.Recording;

namespace PadForge.Views
{
    /// <summary>
    /// Read Your Recording (#600): the review of a finished problem
    /// recording. The view model does the work. This passes the clicks to it
    /// and says when the user closes it with the X.
    /// </summary>
    public partial class RecordingReviewView : UserControl
    {
        public RecordingReviewView()
        {
            InitializeComponent();
        }

        /// <summary>Puts the focus on the first part's switch when the
        /// review opens, so the first key press reaches the review. The X
        /// takes it when no part row has drawn yet.</summary>
        public void FocusFirstPart()
        {
            if (PartList.ItemContainerGenerator.ContainerFromIndex(0) is DependencyObject row
                && FirstOf<CheckBox>(row) is { } first && first.Focus())
                return;
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }

        private static T FirstOf<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                if (FirstOf<T>(child) is { } deeper) return deeper;
            }
            return null;
        }

        /// <summary>The X: the owner closes the dialog.</summary>
        public event EventHandler CloseRequested;

        private RecordingReviewModel Model => DataContext as RecordingReviewModel;

        private void ShowPart_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: RecordingReviewModel.PartRow row }) Model?.Select(row);
            ShownLines.ScrollIntoView(ShownLines.Items.Count > 0 ? ShownLines.Items[0] : null);
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            var model = Model;
            if (model != null) await model.SaveAsync();
        }

        private void CopySummary_Click(object sender, RoutedEventArgs e) => Model?.CopySummary();

        private void Delete_Click(object sender, RoutedEventArgs e) => Model?.Delete();

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
