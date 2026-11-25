using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// The guided pointer's callout (#600): the assistant's name, its words
    /// and the step line. The pointer (AssistantPointer) fills it, places it
    /// beside the ringed place and takes it away.
    /// </summary>
    public partial class AssistantCallout : UserControl
    {
        public AssistantCallout()
        {
            InitializeComponent();
        }

        /// <summary>Raised when the user closes it.</summary>
        internal event EventHandler Closed;

        /// <summary>Shows a step: the program's name, its words and the
        /// line that says which step it is and how it goes on.</summary>
        internal void Fill(string program, string words, string step)
        {
            ProgramText.Text = program ?? string.Empty;
            WordsText.Text = words ?? string.Empty;
            WordsText.Visibility = string.IsNullOrEmpty(words) ? Visibility.Collapsed : Visibility.Visible;
            StepText.Text = step ?? string.Empty;
            StepText.Visibility = string.IsNullOrEmpty(step) ? Visibility.Collapsed : Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(this, string.Join(" ", program, words, step).Trim());
        }

        internal string Program => ProgramText.Text;
        internal string Words => WordsText.Text;
        internal string Step => StepText.Text;

        private void Close_Click(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
    }
}
