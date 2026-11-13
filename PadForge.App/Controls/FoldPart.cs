using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Common;

namespace PadForge.Controls
{
    /// <summary>
    /// An Expander that remembers whether it is open (#574). The two parts
    /// built on it are <see cref="FoldCard"/>, a part that folds to one line
    /// in a <see cref="FoldList"/>, and <see cref="MoreRow"/>, the row at
    /// the foot of a card that opens the rest of its settings in place.
    /// Both keep Expander's keyboard
    /// handling and its expand and collapse pattern, so a screen reader and
    /// a UI Automation script open them the way they open any Expander.
    ///
    /// <para>A part with a <see cref="StateKey"/> reads its open state from
    /// <see cref="CardFoldState"/> and writes it back when the user opens or
    /// closes it. A part with no key opens closed and is not stored.</para>
    /// </summary>
    public abstract class FoldPart : Expander
    {
        public static readonly DependencyProperty StateKeyProperty = DependencyProperty.Register(
            nameof(StateKey), typeof(string), typeof(FoldPart),
            new PropertyMetadata(null, (d, _) => ((FoldPart)d).ReadState()));

        public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
            nameof(Hint), typeof(string), typeof(FoldPart), new PropertyMetadata(string.Empty));

        /// <summary>Set while <see cref="ReadState"/> applies the stored
        /// state, so applying it is not written back as a change.</summary>
        private bool _reading;

        protected FoldPart()
        {
            // Held weakly by CardFoldState, so no unsubscribe is owed.
            CardFoldState.Replaced += OnStateReplaced;
        }

        /// <summary>The name this part is stored under, unique in the app.
        /// It never shows and is never translated.</summary>
        public string StateKey
        {
            get => (string)GetValue(StateKeyProperty);
            set => SetValue(StateKeyProperty, value);
        }

        /// <summary>One line beside the header on what the part holds.</summary>
        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }

        private void OnStateReplaced(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) ReadState();
            else Dispatcher.BeginInvoke(new Action(ReadState));
        }

        private void ReadState()
        {
            string key = StateKey;
            if (string.IsNullOrEmpty(key)) return;
            _reading = true;
            try { SetCurrentValue(IsExpandedProperty, CardFoldState.IsOpen(key)); }
            finally { _reading = false; }
        }

        /// <summary>Opens or closes the part without storing it (#574). The
        /// Settings page's search opens a part whose rows match while it lasts.</summary>
        internal void ShowForSearch(bool open)
        {
            _reading = true;
            try { SetCurrentValue(IsExpandedProperty, open); }
            finally { _reading = false; }
        }

        /// <summary>The search is over: the part goes back to its stored state,
        /// or closed when it has no key.</summary>
        internal void EndSearch()
        {
            if (string.IsNullOrEmpty(StateKey)) ShowForSearch(false);
            else ReadState();
        }

        protected override void OnExpanded()
        {
            base.OnExpanded();
            Store(true);
        }

        protected override void OnCollapsed()
        {
            base.OnCollapsed();
            Store(false);
        }

        private void Store(bool open)
        {
            if (_reading) return;
            CardFoldState.Set(StateKey, open);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new FoldPartPeer(this);

        /// <summary>An Expander's peer that lists nothing of what a closed
        /// part holds. WPF keeps a closed Expander's content in the tree and
        /// leaves it out of the control view while it is not visible
        /// (UIElementAutomationPeer.IsControlElementCore). That rule misses
        /// the items of a picker inside: a ComboBox whose list was opened
        /// once keeps a peer for each item, and an item whose container is
        /// gone (the list was filled again while the part was closed) counts
        /// as a control with no visible owner to say otherwise
        /// (ItemAutomationPeer.IsControlElementCore returns true with no
        /// wrapper). A closed Engage part then read 157 list items to a
        /// screen reader. What stands under the template's ExpandSite is
        /// left out while the part is closed, and the header and its action
        /// stay. Opening the part lays its content out, which marks this
        /// peer stale (UIElementHelper.InvalidateAutomationAncestors), so
        /// the content is listed again.</summary>
        private sealed class FoldPartPeer : ExpanderAutomationPeer
        {
            public FoldPartPeer(FoldPart owner) : base(owner) { }

            protected override List<AutomationPeer> GetChildrenCore()
            {
                List<AutomationPeer> children = base.GetChildrenCore();
                var part = (FoldPart)Owner;
                if (children == null || part.IsExpanded || part.Template?.FindName("ExpandSite", part) is not Visual site)
                    return children;
                children.RemoveAll(peer => peer is UIElementAutomationPeer { Owner: Visual owner } && site.IsAncestorOf(owner));
                return children;
            }
        }
    }

    /// <summary>
    /// A part that folds to one line (#574): a chevron, its title, a hint
    /// and the value it holds. Opening it shows its settings under the line.
    /// It draws no card of its own: a <see cref="FoldList"/> holds one or
    /// more of them in one card, with a hairline between each two.
    /// </summary>
    public sealed class FoldCard : FoldPart
    {
        public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
            nameof(Summary), typeof(string), typeof(FoldCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(FoldCard), new PropertyMetadata(null));

        public static readonly DependencyProperty HeaderActionProperty = DependencyProperty.Register(
            nameof(HeaderAction), typeof(object), typeof(FoldCard), new PropertyMetadata(null, OnHeaderActionChanged));

        public static readonly DependencyProperty SummaryForegroundProperty = DependencyProperty.Register(
            nameof(SummaryForeground), typeof(System.Windows.Media.Brush), typeof(FoldCard), new PropertyMetadata(null));

        public static readonly DependencyProperty HeaderMinHeightProperty = DependencyProperty.Register(
            nameof(HeaderMinHeight), typeof(double), typeof(FoldCard), new PropertyMetadata(48.0));

        public static readonly DependencyProperty ChevronMarginProperty = DependencyProperty.Register(
            nameof(ChevronMargin), typeof(Thickness), typeof(FoldCard), new PropertyMetadata(new Thickness(0, 0, 12, 0)));

        /// <summary>The least height of the part's line: 48, or what a
        /// page's list of its own gives its lines.</summary>
        public double HeaderMinHeight
        {
            get => (double)GetValue(HeaderMinHeightProperty);
            set => SetValue(HeaderMinHeightProperty, value);
        }

        /// <summary>The room around the chevron: 12 between it and the
        /// title, or what puts the title under a list's own column.</summary>
        public Thickness ChevronMargin
        {
            get => (Thickness)GetValue(ChevronMarginProperty);
            set => SetValue(ChevronMarginProperty, value);
        }

        /// <summary>The part's state in a word or two, at the end of the
        /// line while it is folded: Off, a mode's name, a count.</summary>
        public string Summary
        {
            get => (string)GetValue(SummaryProperty);
            set => SetValue(SummaryProperty, value);
        }

        /// <summary>What the part does, in full. It is the tooltip of the
        /// line, since an open part shows its settings and no paragraph.</summary>
        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>The color of <see cref="Summary"/>. The style sets the
        /// secondary text color, the boards' value at a line's end. A list
        /// that writes a count there sets the tertiary one (the Devices
        /// page's folds).</summary>
        public System.Windows.Media.Brush SummaryForeground
        {
            get => (System.Windows.Media.Brush)GetValue(SummaryForegroundProperty);
            set => SetValue(SummaryForegroundProperty, value);
        }

        /// <summary>What stands at the end of the line while the part is
        /// open, in the summary's place: its Reset All button.</summary>
        public object HeaderAction
        {
            get => GetValue(HeaderActionProperty);
            set => SetValue(HeaderActionProperty, value);
        }

        // The action is the part's logical child, as its Header is, so a
        // binding on it reads the part's data and a name scope finds it.
        private static void OnHeaderActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var card = (FoldCard)d;
            if (e.OldValue != null) card.RemoveLogicalChild(e.OldValue);
            if (e.NewValue != null) card.AddLogicalChild(e.NewValue);
        }

        protected override System.Collections.IEnumerator LogicalChildren
        {
            get
            {
                var children = base.LogicalChildren;
                if (HeaderAction == null) return children;
                var list = new System.Collections.ArrayList();
                if (children != null)
                    while (children.MoveNext()) list.Add(children.Current);
                list.Add(HeaderAction);
                return list.GetEnumerator();
            }
        }
    }

    /// <summary>
    /// The More row at the foot of a card (#574). The card shows the two or
    /// three settings most people use, and this row opens the rest under
    /// itself, in place.
    /// </summary>
    public sealed class MoreRow : FoldPart
    {
    }
}
