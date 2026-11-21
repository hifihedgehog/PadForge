using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace PadForge.Common
{
    /// <summary>
    /// Gives every TextBlock in a right-to-left window the direction its own
    /// text needs (#505).
    ///
    /// <para>WPF lays out each TextBlock in its window's direction and has no
    /// bracket-pair rule, so Latin text in a right-to-left window is drawn
    /// wrong wherever a bracket or a symbol meets a run boundary: a device
    /// named "Xbox One Controller (2015)" read "(2015) Xbox One Controller",
    /// and "Igor Budzhak (MIT)" followed by Arabic put the closing bracket on
    /// the far side of the name. Two fixes, with no character added to any
    /// text:</para>
    ///
    /// <para>Text with no right-to-left letter that the window's direction
    /// would draw differently takes the direction of its first strong
    /// letter, left to right, as HTML's dir="auto" and Android's first-strong
    /// text direction do. A block that trims its text is drawn differently
    /// too, since the ellipsis goes at the line's end, and so is a number
    /// label such as "#1", which has no letter but reads left to right.
    /// Its alignment and padding are mirrored with it, so
    /// it stays where the layout put it. This sets FlowDirection, TextAlignment
    /// and Padding only, never the text.</para>
    ///
    /// <para>Text that mixes right-to-left letters with Latin bracketed
    /// stretches keeps the window's direction and gets those stretches as
    /// left-to-right Runs, as <see cref="BidiText"/> builds them. New Inlines
    /// cost a TextBlock the binding on its Text: WPF dropped it once the block
    /// had been laid out, and the next value never arrived. So before it
    /// builds Runs, this class takes the text over. It copies the text's
    /// source onto <see cref="SourceTextProperty"/>: the binding on Text, the
    /// binding a template or style put there, or the Content of the
    /// ContentPresenter that shows the string. From then on it builds the
    /// Runs from that source on every change, in either direction. Text a
    /// trigger sets, and Inlines written with formatting, are left alone.</para>
    ///
    /// <para><see cref="UiLanguage"/> sets <see cref="WatchProperty"/> on each
    /// right-to-left window. It inherits, so WPF calls back for every element
    /// that joins the tree, template parts included. WPF raises Loaded only on
    /// elements with handlers of their own, so a class handler for Loaded
    /// would miss most TextBlocks. Tooltips and context menus open outside
    /// their window's tree and are set as they open. Work runs once the change
    /// under way has finished, ahead of the next frame.</para>
    /// </summary>
    internal static class TextDirection
    {
        /// <summary>True on a right-to-left window and everything in it.</summary>
        internal static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
            "Watch", typeof(bool), typeof(TextDirection),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnWatchChanged));

        internal static void SetWatch(DependencyObject d, bool value) => d.SetValue(WatchProperty, value);
        internal static bool GetWatch(DependencyObject d) => (bool)d.GetValue(WatchProperty);

        /// <summary>A watched TextBlock's Text, bound back to the block, so any
        /// change to what it shows reaches this class.</summary>
        internal static readonly DependencyProperty ShownTextProperty = DependencyProperty.RegisterAttached(
            "ShownText", typeof(string), typeof(TextDirection), new PropertyMetadata(null, OnTextChanged));

        /// <summary>The source of a taken-over block's text.</summary>
        internal static readonly DependencyProperty SourceTextProperty = DependencyProperty.RegisterAttached(
            "SourceText", typeof(string), typeof(TextDirection), new PropertyMetadata(null, OnTextChanged));

        private sealed class State
        {
            public bool Pending;
            // Taken over: Bound reads SourceText, otherwise Text set on the block itself.
            public bool Owned;
            public bool Bound;
            public string Built;
            public Inline First;
            public bool Flipped;
            public object TextAlignment;
            public object Padding;
        }

        private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
            "State", typeof(State), typeof(TextDirection), new PropertyMetadata(null));

        private static bool _started;

        /// <summary>Watches tooltips and context menus as they open. Called
        /// once, from <see cref="UiLanguage.Start"/>.</summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
        }

        private static void OnPopupOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement popup) return;
            UIElement owner = (popup as ToolTip)?.PlacementTarget ?? (popup as ContextMenu)?.PlacementTarget;
            SetWatch(popup, owner != null ? GetWatch(owner) : popup.FlowDirection == FlowDirection.RightToLeft);
        }

        private static void OnWatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock block) return;
            if ((bool)e.NewValue && BindingOperations.GetBindingExpressionBase(block, ShownTextProperty) == null)
            {
                BindingOperations.SetBinding(block, ShownTextProperty, new Binding
                {
                    Path = new PropertyPath(TextBlock.TextProperty),
                    RelativeSource = RelativeSource.Self,
                    Mode = BindingMode.OneWay,
                });
            }
            if (Interesting(block, block.Text)) Schedule(block);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock block && Interesting(block, (string)e.NewValue)) Schedule(block);
        }

        /// <summary>False for text this class has nothing to do with, such as
        /// a readout of digits that changes many times a second, so it costs
        /// no dispatcher work: no letter and no number sign, or letters with
        /// no bracket that the window's direction draws as they are, in a
        /// block that does not trim. A block this class changed is
        /// always looked at again.</summary>
        private static bool Interesting(TextBlock block, string text)
        {
            var state = (State)block.GetValue(StateProperty);
            if (state != null && (state.Owned || state.Flipped)) return true;
            if (string.IsNullOrEmpty(text)) return false;
            bool letter = false;
            foreach (char c in text)
            {
                if (char.IsLetter(c)) { letter = true; break; }
            }
            return (letter || HasNumberSign(text))
                && (text.IndexOfAny(Brackets) >= 0 || NeedsLeftToRight(block, text));
        }

        /// <summary>Applies once the change under way has finished, ahead of
        /// the next frame. Several changes before then apply once.</summary>
        private static void Schedule(TextBlock block)
        {
            var state = Ensure(block);
            if (state.Pending) return;
            state.Pending = true;
            block.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                state.Pending = false;
                Apply(block);
            }));
        }

        /// <summary>Gives the block the direction its text needs, keeps a
        /// taken-over block's Runs in step with its source, and puts back what
        /// an earlier call changed.</summary>
        internal static void Apply(TextBlock block)
        {
            var state = Ensure(block);
            string text = (state.Owned && state.Bound ? (string)block.GetValue(SourceTextProperty) : block.Text) ?? string.Empty;
            if (state.Owned && !(state.Built == text && Ours(block, state)))
                Build(block, state, text);

            if (!GetWatch(block) || ContextDirection(block, state) != FlowDirection.RightToLeft)
            {
                Unflip(block, state);
                return;
            }

            bool rightToLeft = false, leftToRight = false;
            foreach (char c in text)
            {
                if (IsRightToLeftLetter(c)) rightToLeft = true;
                else if (char.IsLetter(c)) leftToRight = true;
            }

            if (!rightToLeft && (leftToRight || HasNumberSign(text)) && NeedsLeftToRight(block, text))
            {
                Flip(block, state);
                return;
            }
            Unflip(block, state);

            if (rightToLeft && !state.Owned && BidiText.GetText(block) == null
                && BidiText.Split(text).Exists(p => p.LeftToRight) && TakeOver(block, state))
            {
                Build(block, state, text);
            }
        }

        /// <summary>Left-to-right text that a right-to-left paragraph draws
        /// differently: a bracket anywhere, or anything but a letter first or
        /// anything but a letter or digit last. A digit after a letter takes
        /// the letter's direction, and one in first place does not.</summary>
        internal static bool NeedsLeftToRight(string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) return false;
            if (t.IndexOfAny(Brackets) >= 0) return true;
            return !char.IsLetter(t[0]) || !char.IsLetterOrDigit(t[t.Length - 1]);
        }

        /// <summary><see cref="NeedsLeftToRight(string)"/>, or a block that
        /// cuts its text with an ellipsis. A right-to-left line puts the
        /// ellipsis at its own end, the left, before a Latin name's first
        /// letter ("…Nintendo Switch"), where a left-to-right line puts it
        /// after the last letter shown. A block that fits draws the same
        /// either way.</summary>
        internal static bool NeedsLeftToRight(TextBlock block, string text)
            => NeedsLeftToRight(text)
            || (block.TextTrimming != TextTrimming.None && (text ?? string.Empty).Trim().Length > 0);

        /// <summary>True for text with a number sign before a digit, such as a
        /// slot's "#1". Unicode makes the sign part of its number, and WPF
        /// draws it after the number in a right-to-left line ("1#"). A number
        /// label reads left to right, as the number does.</summary>
        internal static bool HasNumberSign(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (text[i] == '#' && char.IsDigit(text[i + 1])) return true;
            }
            return false;
        }

        /// <summary>The direction of the text's first letter: right to left
        /// for a Hebrew or Arabic-script letter, left to right for any other,
        /// null for text without a letter.</summary>
        internal static FlowDirection? FirstStrongDirection(string text)
        {
            foreach (char c in text ?? string.Empty)
            {
                if (!char.IsLetter(c)) continue;
                return IsRightToLeftLetter(c) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            }
            return null;
        }

        private static readonly char[] Brackets = { '(', ')', '[', ']', '{', '}' };

        /// <summary>The direction the block would have without this class:
        /// its parent's once the block is flipped, its own before.</summary>
        private static FlowDirection ContextDirection(TextBlock block, State state)
        {
            if (!state.Flipped) return block.FlowDirection;
            DependencyObject parent = VisualTreeHelper.GetParent(block) ?? LogicalTreeHelper.GetParent(block);
            return parent != null ? (FlowDirection)parent.GetValue(FrameworkElement.FlowDirectionProperty) : FlowDirection.RightToLeft;
        }

        private static void Flip(TextBlock block, State state)
        {
            if (state.Flipped) return;
            // The app pinned this block's direction itself, or the block is drawn
            // turned, and a mirrored turn would point the other way.
            if (block.ReadLocalValue(FrameworkElement.FlowDirectionProperty) != DependencyProperty.UnsetValue) return;
            if (!IsIdentity(block.RenderTransform) || !IsIdentity(block.LayoutTransform)) return;

            state.TextAlignment = block.ReadLocalValue(TextBlock.TextAlignmentProperty);
            state.Padding = block.ReadLocalValue(TextBlock.PaddingProperty);
            TextAlignment alignment = block.TextAlignment;
            Thickness padding = block.Padding;

            state.Flipped = true;
            block.FlowDirection = FlowDirection.LeftToRight;
            if (alignment == TextAlignment.Left) block.TextAlignment = TextAlignment.Right;
            else if (alignment == TextAlignment.Right) block.TextAlignment = TextAlignment.Left;
            if (padding.Left != padding.Right)
                block.Padding = new Thickness(padding.Right, padding.Top, padding.Left, padding.Bottom);
        }

        private static void Unflip(TextBlock block, State state)
        {
            if (!state.Flipped) return;
            state.Flipped = false;
            block.ClearValue(FrameworkElement.FlowDirectionProperty);
            Restore(block, TextBlock.TextAlignmentProperty, state.TextAlignment);
            Restore(block, TextBlock.PaddingProperty, state.Padding);
        }

        private static void Restore(DependencyObject d, DependencyProperty dp, object saved)
        {
            if (saved == DependencyProperty.UnsetValue || saved == null) d.ClearValue(dp);
            else d.SetValue(dp, saved);
        }

        /// <summary>Finds where the block's text comes from, so this class can
        /// build its Runs from then on. False for text a trigger sets, for
        /// Inlines written with formatting, and for a template binding other
        /// than a ContentPresenter's string, which it cannot follow.</summary>
        private static bool TakeOver(TextBlock block, State state)
        {
            if (!Plain(block)) return false;
            ValueSource source = DependencyPropertyHelper.GetValueSource(block, TextBlock.TextProperty);
            switch (source.BaseValueSource)
            {
                case BaseValueSource.Local:
                case BaseValueSource.ParentTemplate:
                case BaseValueSource.Style:
                case BaseValueSource.ImplicitStyleReference:
                case BaseValueSource.DefaultStyle:
                    break;
                default:
                    return false;
            }

            BindingBase copy = BindingOperations.GetBindingExpressionBase(block, TextBlock.TextProperty) switch
            {
                BindingExpression b => b.ParentBinding,
                MultiBindingExpression m => m.ParentMultiBinding,
                PriorityBindingExpression p => p.ParentPriorityBinding,
                _ => null,
            };
            // A ContentPresenter shows a string through a TextBlock that its
            // template gives the Content.
            if (copy == null && source.BaseValueSource == BaseValueSource.ParentTemplate
                && block.TemplatedParent is ContentPresenter presenter && presenter.Content is string
                && ReferenceEquals(VisualTreeHelper.GetParent(block), presenter))
            {
                copy = new Binding(nameof(ContentPresenter.Content)) { Source = presenter, Mode = BindingMode.OneWay };
            }
            if (copy == null && source.IsExpression) return false;

            state.Owned = true;
            state.Bound = copy != null;
            if (copy != null) BindingOperations.SetBinding(block, SourceTextProperty, copy);
            return true;
        }

        private static void Build(TextBlock block, State state, string text)
        {
            state.Built = text;
            block.Inlines.Clear();
            foreach (var (piece, leftToRight) in BidiText.Split(text))
            {
                var run = new Run(piece);
                if (leftToRight) run.FlowDirection = FlowDirection.LeftToRight;
                block.Inlines.Add(run);
            }
            state.First = block.Inlines.FirstInline;
        }

        /// <summary>True while the block still shows the Runs this class built.</summary>
        private static bool Ours(TextBlock block, State state)
            => state.First != null && ReferenceEquals(block.Inlines.FirstInline, state.First);

        /// <summary>One Run with no formatting of its own: what a TextBlock
        /// holds when only its Text was set.</summary>
        private static bool Plain(TextBlock block)
        {
            if (block.Inlines.Count != 1 || block.Inlines.FirstInline is not Run run || run.GetType() != typeof(Run))
                return false;
            foreach (DependencyProperty dp in Formatting)
            {
                if (run.ReadLocalValue(dp) != DependencyProperty.UnsetValue) return false;
            }
            return run.Style == null;
        }

        private static readonly DependencyProperty[] Formatting =
        {
            TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.FontSizeProperty,
            TextElement.FontFamilyProperty, TextElement.ForegroundProperty, TextElement.BackgroundProperty,
            Inline.TextDecorationsProperty, Inline.BaselineAlignmentProperty,
        };

        private static State Ensure(TextBlock block)
        {
            var state = (State)block.GetValue(StateProperty);
            if (state == null)
            {
                state = new State();
                block.SetValue(StateProperty, state);
            }
            return state;
        }

        private static bool IsIdentity(Transform transform)
            => transform == null || transform.Value.IsIdentity;

        private static bool IsRightToLeftLetter(char c)
            => char.IsLetter(c) && ((c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF));
    }
}
