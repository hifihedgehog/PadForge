using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// The Settings page's search (#574). The page names its five groups
    /// (Group, on each group's panel) and its cards (Card, the card's title,
    /// on each card), and each thing the box shows or hides is a
    /// SettingItem. An item's text is what it shows and its tooltips say and
    /// its Terms, the title of any fold card it sits in, its card's title and
    /// Terms, and its group's name. It matches when every word of the query
    /// is somewhere in that text, compared as the interface's language
    /// compares text, with case, accents, width, kana type and symbols set
    /// aside.
    /// </summary>
    public static class SettingSearch
    {
        public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
            "Group", typeof(string), typeof(SettingSearch), new PropertyMetadata(null));

        public static readonly DependencyProperty CardProperty = DependencyProperty.RegisterAttached(
            "Card", typeof(string), typeof(SettingSearch), new PropertyMetadata(null));

        public static readonly DependencyProperty TermsProperty = DependencyProperty.RegisterAttached(
            "Terms", typeof(string), typeof(SettingSearch), new PropertyMetadata(null));

        /// <summary>The name of the group whose panel this is.</summary>
        public static string GetGroup(DependencyObject element) => (string)element.GetValue(GroupProperty);

        public static void SetGroup(DependencyObject element, string value) => element.SetValue(GroupProperty, value);

        /// <summary>The title of the card this is.</summary>
        public static string GetCard(DependencyObject element) => (string)element.GetValue(CardProperty);

        public static void SetCard(DependencyObject element, string value) => element.SetValue(CardProperty, value);

        /// <summary>Words a card or an item answers to that it does not show.</summary>
        public static string GetTerms(DependencyObject element) => (string)element.GetValue(TermsProperty);

        public static void SetTerms(DependencyObject element, string value) => element.SetValue(TermsProperty, value);

        private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace
            | CompareOptions.IgnoreSymbols | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

        /// <summary>The query's words: split on white space, keeping each word
        /// that holds a letter or a digit (a word of symbols alone would match
        /// everything once symbols are set aside).</summary>
        internal static string[] Words(string query) => string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                   .Where(w => w.Any(char.IsLetterOrDigit)).ToArray();

        /// <summary>Every word is in the text. No words match nothing.</summary>
        internal static bool Matches(string text, IReadOnlyList<string> words, CultureInfo culture)
        {
            if (words == null || words.Count == 0 || string.IsNullOrEmpty(text)) return false;
            var compare = (culture ?? CultureInfo.CurrentUICulture).CompareInfo;
            foreach (string word in words)
                if (compare.IndexOf(text, word, Loose) < 0) return false;
            return true;
        }

        /// <summary>The words an item shows and says: each TextBlock's text,
        /// each control's string content, each string tooltip and each Terms
        /// in its logical tree. A reset button's words ("Reset ...") are left
        /// out, and so is any part collapsed inside the item. The item's own
        /// rule is not read here.</summary>
        internal static string TextOf(DependencyObject item)
        {
            var text = new StringBuilder();
            Gather(item, text, isItem: true);
            return text.ToString();
        }

        private static void Gather(DependencyObject part, StringBuilder text, bool isItem)
        {
            if (part is SettingResetButton) return;
            if (!isItem && part is UIElement { Visibility: Visibility.Collapsed }) return;
            switch (part)
            {
                case TextBlock block:
                    Add(text, block.Text);
                    break;
                case ContentControl { Content: string content }:
                    Add(text, content);
                    break;
            }
            if (part is FrameworkElement { ToolTip: string tip }) Add(text, tip);
            Add(text, GetTerms(part));
            foreach (object child in LogicalTreeHelper.GetChildren(part))
                if (child is DependencyObject inner) Gather(inner, text, false);
        }

        private static void Add(StringBuilder text, string words)
        {
            if (string.IsNullOrWhiteSpace(words)) return;
            if (text.Length > 0) text.Append(' ');
            text.Append(words);
        }

        /// <summary>Shows what matches under one group's panel and returns how
        /// many items show: a card with none collapses, an item that does not
        /// match is filtered out, and a fold or More row with a match inside is
        /// opened for the search and added to <paramref name="opened"/>.</summary>
        internal static int Filter(FrameworkElement group, IReadOnlyList<string> words,
                                   CultureInfo culture, ICollection<FoldPart> opened)
        {
            int shown = 0;
            foreach (UIElement card in Cards(group))
            {
                string context = string.Join(" ", new[] { GetCard(card), GetTerms(card), GetGroup(group) }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                int count = Walk(card, context, words, culture, opened);
                card.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                shown += count;
            }
            return shown;
        }

        /// <summary>Puts every item and card under the panel back as the page's
        /// own rules have them.</summary>
        internal static void Clear(DependencyObject group)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(group))
            {
                if (child is not DependencyObject part) continue;
                if (part is SettingItem item) item.IsFilteredOut = false;
                if (part is UIElement card && GetCard(part) != null) card.Visibility = Visibility.Visible;
                Clear(part);
            }
        }

        /// <summary>The cards under a panel, in order: each part that names
        /// its card. A card is not looked into for another.</summary>
        private static IEnumerable<UIElement> Cards(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject part) continue;
                if (part is UIElement card && GetCard(part) != null)
                {
                    yield return card;
                    continue;
                }
                foreach (UIElement inner in Cards(part))
                    yield return inner;
            }
        }

        /// <summary>Filters the items under a part and returns how many of
        /// them show. A container's count is its items'. A leaf counts when it
        /// matches and its own rule shows it.</summary>
        private static int Walk(DependencyObject parent, string context, IReadOnlyList<string> words,
                                CultureInfo culture, ICollection<FoldPart> opened)
        {
            int count = 0;
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                switch (child)
                {
                    case SettingItem item when HoldsItems(item):
                    {
                        int inner = Walk(item, context, words, culture, opened);
                        item.IsFilteredOut = inner == 0;
                        if (item.OwnVisibility == Visibility.Visible) count += inner;
                        break;
                    }
                    case SettingItem item:
                    {
                        bool match = Matches(TextOf(item) + " " + context, words, culture);
                        item.IsFilteredOut = !match;
                        if (match && item.OwnVisibility == Visibility.Visible) count++;
                        break;
                    }
                    case FoldPart part:
                    {
                        // A fold card's title is its rows' context. The More
                        // row's word and hint are not: "repeat" would show
                        // every row under a hint that names one.
                        string inside = part is FoldCard && part.Header is string title && title.Length > 0
                            ? context + " " + title
                            : context;
                        int inner = Walk(part, inside, words, culture, opened);
                        if (inner > 0 && !part.IsExpanded)
                        {
                            part.ShowForSearch(true);
                            opened.Add(part);
                        }
                        count += inner;
                        break;
                    }
                    case DependencyObject other:
                        count += Walk(other, context, words, culture, opened);
                        break;
                }
            }
            return count;
        }

        /// <summary>Whether a part holds a SettingItem anywhere in its logical tree.</summary>
        private static bool HoldsItems(DependencyObject part)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(part))
            {
                if (child is SettingItem) return true;
                if (child is DependencyObject inner && HoldsItems(inner)) return true;
            }
            return false;
        }
    }
}
