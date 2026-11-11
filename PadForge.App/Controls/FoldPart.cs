using System;
using System.Windows;
using System.Windows.Controls;
using PadForge.Common;

namespace PadForge.Controls
{
    /// <summary>
    /// An Expander that remembers whether it is open (#574). The two parts
    /// built on it are <see cref="FoldCard"/>, a card that folds to one line,
    /// and <see cref="MoreRow"/>, the row at the foot of a card that opens
    /// the rest of its settings in place. Both keep Expander's keyboard
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
    /// A card that folds to one line (#574): a chevron, its title, a hint
    /// and the value it holds. Opening it shows the card's content under the
    /// line. It draws its own card border, so it sits in a page like any
    /// card.
    /// </summary>
    public sealed class FoldCard : FoldPart
    {
        public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
            nameof(Summary), typeof(string), typeof(FoldCard), new PropertyMetadata(string.Empty));

        /// <summary>The card's state in a word or two, at the end of the
        /// line: Off, a mode's name, a count.</summary>
        public string Summary
        {
            get => (string)GetValue(SummaryProperty);
            set => SetValue(SummaryProperty, value);
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
