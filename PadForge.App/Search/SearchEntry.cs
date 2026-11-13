using System.Collections.Generic;

namespace PadForge.Search
{
    /// <summary>What a search entry stands for (#574).</summary>
    internal enum SearchKind { Setting, Card, Action, Extra, Section, Part }

    /// <summary>The pages Search reads.</summary>
    internal enum SearchPage { Pad, Settings, Extras, Devices, Profiles }

    /// <summary>How an entry's label is shown on its page, so the locator
    /// looks only at that kind of element.</summary>
    internal enum SearchLabelSource { Text, Toggle, ResetLabel, CardTitle, FoldHeader, Button, Tile, Strip }

    /// <summary>A string key, or a binding path on the item or the view
    /// model. One of the two is set.</summary>
    internal sealed record SearchLabel(string Key, string Path);

    /// <summary>A visibility binding on the way down to an item's row: the
    /// path on the item, the converter's key and its parameter.</summary>
    internal sealed record SearchGate(string Path, string ConverterKey, string ConverterParameter);

    internal enum SearchValueKind { Number, Switch, Choice }

    /// <summary>How a row's value is read: a binding path with its string
    /// format and converter, and the unit after it.</summary>
    internal sealed record SearchValueSource(string Path, string StringFormat, string ConverterKey,
        string UnitKey, string UnitText, SearchValueKind Kind);

    /// <summary>One place Search can land on, read from a page's markup
    /// (SearchIndexBuilder). Fixed at build time: what is shown now is
    /// decided each time Search opens.</summary>
    internal sealed record SearchEntry(
        SearchKind Kind,
        SearchPage Page,
        SearchLabel Label,              // what the result's title reads
        SearchLabel Qualifier,          // the item card's title, the row a sub-row belongs to, or the subsection. Null for none
        SearchLabel CardTitle,          // the card the entry sits in. Null for none or when it is the qualifier
        int Section,                    // the controller page only, 0 to 5. -1 for its header and for other pages
        int Tab,                        // the controller page: the SelectedConfigTab that shows it. -1 otherwise
        string ExtraKey,                // Extras: the page's key ("WebController"), null on the catalog
        bool UnderMore,                 // inside a More row
        bool RowOptions,                // inside a mapping row's option templates
        string ScopeName,               // nearest ancestor x:Name in the page's name scope (null: the page root)
        string ItemsPath,               // for a card made per item: the ItemsSource path ("StickConfigs")
        IReadOnlyList<SearchGate> ItemGates,
        SearchValueSource Value,        // how to read the row's value. Null for none
        string TargetAutomationId,      // the AutomationId of the row's input, when it has one
        SearchLabelSource LabelSource,  // the kind of element that shows the label
        int Order);                     // document order in its page
}
