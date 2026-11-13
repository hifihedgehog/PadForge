using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Resources.Strings;
using PadForge.Search;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What Search shows (#574): the query, the results grouped under their
    /// labels with the first picked, the line that says nothing matches, and
    /// the line a screen reader hears for the picked result. The query runs
    /// on each change of the words and again on a change of language.
    /// </summary>
    public sealed class SearchViewModel : ObservableObject
    {
        private readonly Func<string, IReadOnlyList<SearchResult>> _query;
        private string _queryText = string.Empty;
        private SearchResult _selected;

        internal SearchViewModel(Func<string, IReadOnlyList<SearchResult>> query)
        {
            _query = query;
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Results);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchResult.GroupLabel)));
            ResultsView = view;
            Strings.CultureChanged += () =>
            {
                Run();
                OnPropertyChanged(nameof(ShortcutText));
            };
        }

        /// <summary>The words typed. Each change runs the query.</summary>
        public string Query
        {
            get => _queryText;
            set
            {
                if (SetProperty(ref _queryText, value ?? string.Empty)) Run();
            }
        }

        public ObservableCollection<SearchResult> Results { get; } = new();

        /// <summary>The results grouped under their labels, in the order the
        /// query gave: each group's best, Documentation last.</summary>
        public ICollectionView ResultsView { get; }

        /// <summary>The picked result: the first after each query.</summary>
        public SearchResult SelectedResult
        {
            get => _selected;
            set
            {
                if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(SelectedAnnouncement));
            }
        }

        public bool HasResults => Results.Count > 0;

        /// <summary>"Nothing in PadForge matches ..." while the query finds
        /// nothing but the documentation, else empty.</summary>
        public string NoResultsText { get; private set; } = string.Empty;

        public bool HasNoResultsText => NoResultsText.Length > 0;

        /// <summary>What a screen reader hears as the pick moves, while the
        /// focus stays in the query box.</summary>
        public string SelectedAnnouncement => _selected?.AutomationName ?? string.Empty;

        /// <summary>The shortcut's key label: the language's Ctrl and K.</summary>
        public string ShortcutText => Strings.Instance.Key_Control + " K";

        /// <summary>Raised by Enter or a click, with the result to open.</summary>
        public event EventHandler<SearchResult> OpenRequested;

        /// <summary>Raised by Escape.</summary>
        public event EventHandler CloseRequested;

        /// <summary>Runs the query again: on a change of language, and when
        /// Search opens on the words it had.</summary>
        public void Run()
        {
            var found = _query?.Invoke(_queryText) ?? Array.Empty<SearchResult>();
            Results.Clear();
            foreach (var r in found) Results.Add(r);
            bool onlyDocs = Results.All(r => r.Group == SearchGroup.Docs);
            NoResultsText = _queryText.Trim().Length > 0 && onlyDocs
                ? string.Format(Strings.Instance.Search_NoResults_Format, _queryText.Trim())
                : string.Empty;
            SelectedResult = Results.FirstOrDefault();
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(NoResultsText));
            OnPropertyChanged(nameof(HasNoResultsText));
        }

        /// <summary>Moves the pick by <paramref name="delta"/> results, and
        /// stops at either end.</summary>
        public void MoveSelection(int delta)
        {
            if (Results.Count == 0) return;
            int at = _selected == null ? -1 : Results.IndexOf(_selected);
            int to = Math.Max(0, Math.Min(Results.Count - 1, (at < 0 ? 0 : at + delta)));
            SelectedResult = Results[to];
        }

        public void Open()
        {
            if (_selected != null) OpenRequested?.Invoke(this, _selected);
        }

        public void Open(SearchResult result)
        {
            if (result != null) OpenRequested?.Invoke(this, result);
        }

        public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
