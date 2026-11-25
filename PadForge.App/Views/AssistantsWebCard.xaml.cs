using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>The Assistants on the Web card (#600 item 11). While the
    /// armed window runs, it reads its view model again each second, so
    /// Accepting until ends on time.</summary>
    public partial class AssistantsWebCard : UserControl
    {
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

        public AssistantsWebCard()
        {
            InitializeComponent();
            _clock.Tick += (_, _) => (DataContext as WebAccessViewModel)?.Refresh();
            Loaded += (_, _) => _clock.Start();
            Unloaded += (_, _) => _clock.Stop();
        }
    }
}
