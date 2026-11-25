using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// Home's waiting recording card (#600): Review and Delete, with the
    /// recording's path. MainWindow opens the review or deletes it.
    /// </summary>
    public partial class RecordingWaitingCard : UserControl
    {
        public RecordingWaitingCard()
        {
            InitializeComponent();
        }

        public event EventHandler<string> ReviewRequested;
        public event EventHandler<string> DeleteRequested;

        private void RecordingReview_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.DashboardViewModel { WaitingRecording: { } path }) ReviewRequested?.Invoke(this, path);
        }

        private void RecordingDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.DashboardViewModel { WaitingRecording: { } path }) DeleteRequested?.Invoke(this, path);
        }
    }
}
