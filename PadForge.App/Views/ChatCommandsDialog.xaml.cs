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
    public partial class ChatCommandsDialog : DialogWindow
    {
        /// <summary>A word in the list, shown as viewers type it.</summary>
        public sealed class WordRow
        {
            public string Word { get; init; }
            public string Display => "!" + Word;

            public override string ToString() => Display;
        }

        private readonly ObservableCollection<WordRow> _rows = new();

        public ChatCommandsDialog()
        {
            InitializeComponent();
            WordListBox.ItemsSource = _rows;
            RefreshList();
            Loaded += (_, _) => WordBox.Focus();
        }

        /// <summary>The line under the Word box: the last add's result, or
        /// why the word cannot be one. Empty, it takes no room.</summary>
        private void Say(string message)
        {
            MessageText.Text = message ?? string.Empty;
            MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            string text = WordBox.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            var problem = ChatCommandRegistry.Check(text);
            if (problem != ChatCommandRegistry.WordProblem.None)
            {
                Say(ProblemText(problem));
                return;
            }
            string word = ChatCommandRegistry.Normalize(text);
            bool existed = ChatCommandRegistry.ButtonFor(word) > 0;
            if (ChatCommandRegistry.Add(word) < 0)
            {
                Say(string.Format(CultureInfo.CurrentCulture,
                    Strings.Instance.TwitchChat_Problem_Full_Format, ChatCommandRegistry.MaxWords));
                return;
            }
            Say(string.Format(CultureInfo.CurrentCulture,
                existed ? Strings.Instance.TwitchChat_Exists_Format : Strings.Instance.TwitchChat_Added_Format,
                "!" + word));
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
                Say(null);
                RefreshList();
                // The row and its button are gone, so the focus they held
                // goes to the Word box.
                WordBox.Focus();
            }
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            ChatCommandRegistry.RestoreUsualWords();
            Say(null);
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

        private void RefreshList()
        {
            _rows.Clear();
            foreach (var w in ChatCommandRegistry.Words)
                _rows.Add(new WordRow { Word = w.Word });
            WordsBlock.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
