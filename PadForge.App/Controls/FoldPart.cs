using System;
using System.Windows;
using System.Windows.Controls;
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
