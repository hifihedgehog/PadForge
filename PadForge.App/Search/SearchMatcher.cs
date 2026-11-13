using System;
using System.Globalization;

namespace PadForge.Search
{
    /// <summary>How well a result matches what was typed (#574). Case,
    /// accents, kana type and width are ignored, as the language's own
    /// comparison does. Every word typed must be found, in the title, the
    /// where line or, in a language other than English, the English
    /// title.</summary>
    internal static class SearchMatcher
    {
        private const CompareOptions Loose =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        /// <summary>Whether <paramref name="haystack"/> holds
        /// <paramref name="token"/>, and whether it does at the start of a
        /// word (at the start, or after a character that is no letter or
        /// digit, so "Anti-Deadzone" holds "Deadzone" at a word start) or at
        /// the very start.</summary>
        internal static bool Find(string haystack, string token, out bool atWordStart, out bool atStart)
        {
            atWordStart = atStart = false;
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(token)) return false;
            var compare = CultureInfo.CurrentUICulture.CompareInfo;
            bool found = false;
            for (int from = 0; from < haystack.Length;)
            {
                int at = compare.IndexOf(haystack, token, from, Loose);
                if (at < 0) break;
                found = true;
                if (at == 0)
                {
                    atStart = atWordStart = true;
                    return true;
                }
                if (!char.IsLetterOrDigit(haystack[at - 1]))
                {
                    atWordStart = true;
                    return true;
                }
                from = at + 1;
            }
            return found;
        }

        /// <summary>The score of one result, 0 when a word typed is found
        /// nowhere. Per word the best of: the title's start 60, a word start
        /// in the title 45, inside the title 25, a word start in the where
        /// line 12, inside it 6, a word start in the English title 20, inside
        /// it 10. A query that is the whole title adds 100.</summary>
        internal static int Score(string query, string title, string where, string englishTitle)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;
            var tokens = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            bool english = !string.IsNullOrEmpty(englishTitle) && !string.Equals(englishTitle, title, StringComparison.Ordinal);
            int score = 0;
            foreach (string token in tokens)
            {
                int best = 0;
                if (Find(title, token, out bool titleWord, out bool titleStart))
                    best = titleStart ? 60 : titleWord ? 45 : 25;
                if (Find(where, token, out bool whereWord, out _))
                    best = Math.Max(best, whereWord ? 12 : 6);
                if (english && Find(englishTitle, token, out bool englishWord, out _))
                    best = Math.Max(best, englishWord ? 20 : 10);
                if (best == 0) return 0;
                score += best;
            }
            if (CultureInfo.CurrentUICulture.CompareInfo.Compare(query.Trim(), title ?? string.Empty, Loose) == 0)
                score += 100;
            return score;
        }
    }
}
