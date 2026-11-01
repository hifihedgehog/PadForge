using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using PadForge.Common.Input.Twitch;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// The Twitch Chat row's word list (issue #555): add a word, remove one,
    /// and restore the usual words. Modeled on
    /// <see cref="RegisterVoicePhraseDialog"/>. Every change goes through
    /// <see cref="ChatCommandRegistry"/>, whose RegistryChanged re-derives
    /// the row's objects, refreshes the pickers and saves.
    /// </summary>
    public partial class ChatCommandsDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>A word in the list, shown as viewers type it.</summary>
        public sealed class WordRow
        {
            public string Word { get; init; }
            public string Display => "!" + Word;
        }

        private readonly ObservableCollection<WordRow> _rows = new();

        public ChatCommandsDialog()
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };
            WordListBox.ItemsSource = _rows;
            RefreshList();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            string text = WordBox.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            var problem = ChatCommandRegistry.Check(text);
            if (problem != ChatCommandRegistry.WordProblem.None)
            {
                MessageText.Text = ProblemText(problem);
                return;
            }
            string word = ChatCommandRegistry.Normalize(text);
            bool existed = ChatCommandRegistry.ButtonFor(word) > 0;
            if (ChatCommandRegistry.Add(word) < 0)
            {
                MessageText.Text = string.Format(CultureInfo.CurrentCulture,
                    Strings.Instance.TwitchChat_Problem_Full_Format, ChatCommandRegistry.MaxWords);
                return;
            }
            MessageText.Text = string.Format(CultureInfo.CurrentCulture,
                existed ? Strings.Instance.TwitchChat_Exists_Format : Strings.Instance.TwitchChat_Added_Format,
                "!" + word);
            WordBox.Text = string.Empty;
            WordBox.Focus();
            RefreshList();
        }

        /// <summary>Why a word cannot be added, in the current
        /// language.</summary>
        internal static string ProblemText(ChatCommandRegistry.WordProblem problem)
        {
            var s = Strings.Instance;
            return problem switch
            {
                ChatCommandRegistry.WordProblem.TooLong => string.Format(CultureInfo.CurrentCulture,
                    s.TwitchChat_Problem_TooLong_Format, ChatCommandRegistry.MaxWordLength),
                ChatCommandRegistry.WordProblem.Characters => s.TwitchChat_Problem_Characters,
                ChatCommandRegistry.WordProblem.EndsInTime => s.TwitchChat_Problem_EndsInTime,
                ChatCommandRegistry.WordProblem.Reserved => s.TwitchChat_Problem_Reserved,
                _ => string.Empty,
            };
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string word && word.Length > 0)
            {
                ChatCommandRegistry.Remove(word);
                MessageText.Text = string.Empty;
                RefreshList();
            }
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            ChatCommandRegistry.RestoreUsualWords();
            MessageText.Text = string.Empty;
            RefreshList();
        }

        private void WordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void RefreshList()
        {
            _rows.Clear();
            foreach (var w in ChatCommandRegistry.Words)
                _rows.Add(new WordRow { Word = w.Word });
        }
    }
}
