using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input.OnScreenKeyboard
{
    /// <summary>
    /// Word suggestions from Windows' own text prediction (#566,
    /// <c>Windows.Data.Text.TextPredictionGenerator</c>), asked off the poll
    /// thread. Windows answers for the languages installed on the PC and
    /// returns nothing for others. Its touch keyboard model mixes in near
    /// misses ("yep" for "hel"), which <see cref="OskEngine.SetSuggestions"/>
    /// filters out. One question runs at a time. A newer one waits for it and
    /// replaces any older one still waiting.
    /// </summary>
    internal sealed class OskPredictor
    {
        private sealed record Ask(string Language, string Word, string Previous, int Version);
        private sealed record Answer(int Version, string[] Words);

        private readonly object _gate = new();
        private Ask _pending;
        private bool _running;
        private Answer _answer;
        private string _generatorLanguage;
        private Windows.Data.Text.TextPredictionGenerator _generator;

        /// <summary>Asks for candidates for the word being typed, or for the
        /// next word when it is empty.</summary>
        public void Request(string language, string word, string previous, int version)
        {
            lock (_gate)
            {
                _pending = new Ask(language ?? "en-US", word ?? "", previous ?? "", version);
                if (_running) return;
                _running = true;
            }
            Task.Run(RunAsync);
        }

        /// <summary>The newest answer, once, or false.</summary>
        public bool TryTake(out int version, out string[] words)
        {
            var a = Interlocked.Exchange(ref _answer, null);
            version = a?.Version ?? 0;
            words = a?.Words;
            return a != null;
        }

        private async Task RunAsync()
        {
            while (true)
            {
                Ask ask;
                lock (_gate)
                {
                    ask = _pending;
                    _pending = null;
                    if (ask == null) { _running = false; return; }
                }
                string[] words = Array.Empty<string>();
                try
                {
                    if (_generator == null || _generatorLanguage != ask.Language)
                    {
                        _generator = new Windows.Data.Text.TextPredictionGenerator(ask.Language);
                        _generatorLanguage = ask.Language;
                    }
                    if (!_generator.LanguageAvailableButNotInstalled)
                    {
                        IReadOnlyList<string> found;
                        if (ask.Word.Length > 0)
                            found = await _generator.GetCandidatesAsync(ask.Word, 10);
                        else if (ask.Previous.Length > 0)
                            found = await _generator.GetNextWordCandidatesAsync(10, new[] { ask.Previous });
                        else
                            found = Array.Empty<string>();
                        words = found?.ToArray() ?? Array.Empty<string>();
                    }
                }
                catch (Exception)
                {
                    // A language Windows has no model for, or a WinRT failure:
                    // no suggestions, the same as an uninstalled language.
                    _generator = null;
                    _generatorLanguage = null;
                }
                Volatile.Write(ref _answer, new Answer(ask.Version, words));
            }
        }
    }
}
