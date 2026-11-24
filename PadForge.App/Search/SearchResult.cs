using PadForge.ViewModels;

namespace PadForge.Search
{
    /// <summary>The groups of Search's results, in the order a tie between
    /// their best scores falls (#574). Documentation always comes last.</summary>
    public enum SearchGroup { Pages, Controllers, Devices, Profiles, Settings, Actions, Docs, Dialogs, Macros, Plugins }

    /// <summary>What a result opens when it is not an entry of a page's
    /// markup.</summary>
    internal enum SearchTarget { Entry, Page, Controller, AddController, Device, Profile, Docs, Macro, Plugin }

    /// <summary>One line of Search's results (#574): the title, the qualifier
    /// that follows it in the quieter color, where it is, and its value.</summary>
    public sealed class SearchResult
    {
        public string Title { get; init; } = string.Empty;

        /// <summary>The words after the title ("Left Stick"), or empty.</summary>
        public string Qualifier { get; init; } = string.Empty;

        /// <summary>The qualifier as the title's line shows it, after a
        /// space, or empty.</summary>
        public string QualifierText => string.IsNullOrEmpty(Qualifier) ? string.Empty : " " + Qualifier;

        /// <summary>The second line: the controller, the section, the card,
        /// and whether it is under More or in a row's options.</summary>
        public string Where { get; init; } = string.Empty;

        /// <summary>The setting's value with its unit ("8 %"), or empty.</summary>
        public string Value { get; internal set; } = string.Empty;

        /// <summary>The value is a number, which reads in the cold color of a
        /// measured value, as Search's board draws "8 %". A word such as On
        /// reads as plain secondary text.</summary>
        public bool ValueIsNumber => Value.Length > 0 && (char.IsDigit(Value[0]) || Value[0] is '-' or '+' or '−');

        public SearchGroup Group { get; init; }

        public string GroupLabel { get; init; } = string.Empty;

        internal int Score { get; init; }

        internal int Order { get; init; }

        /// <summary>What a screen reader hears for the line: the title, the
        /// qualifier, where it is and the value.</summary>
        public string AutomationName
        {
            get
            {
                string name = string.IsNullOrEmpty(Qualifier) ? Title : Title + " " + Qualifier;
                if (!string.IsNullOrEmpty(Where)) name += ", " + Where;
                if (!string.IsNullOrEmpty(Value)) name += ", " + Value;
                return name;
            }
        }

        internal SearchTarget Target { get; init; }

        internal SearchEntry Entry { get; init; }

        /// <summary>The item an entry of a card per item stands for, the
        /// controller's index, the device's row, the profile, or the
        /// documentation's query.</summary>
        internal object Payload { get; init; }

        /// <summary>The controller a controller page's entry stands on, which
        /// its page opens on. Null for every other result.</summary>
        internal PadViewModel Pad { get; init; }

        /// <summary>The value read while the controller page showed
        /// <see cref="Pad"/>, for a controller the page is not bound to.
        /// Null when the value is read once the query has ranked the
        /// result.</summary>
        internal string ReadValue { get; init; }

        public override string ToString() => AutomationName;
    }
}
