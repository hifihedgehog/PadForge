using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PadForge.Common
{
    /// <summary>
    /// What a program may do with an element (#600). Append-only: the
    /// numbers are what a test pins.
    /// </summary>
    public enum ReachClass
    {
        /// <summary>No class was given. Read as <see cref="UsersOwn"/>, and a
        /// test fails on it.</summary>
        Unset,
        /// <summary>A program at Change Settings may set or press it.</summary>
        Free,
        /// <summary>The request card asks the user first, every time.</summary>
        Asks,
        /// <summary>No program, ever: Yours Alone.</summary>
        UsersOwn,
    }

    /// <summary>Why an element that asks asks (#600), the rules table's
    /// cases. Append-only.</summary>
    public enum ReachReason
    {
        None,
        /// <summary>It would make PadForge run a program.</summary>
        RunsProgram,
        /// <summary>It would make PadForge open a file or an address.</summary>
        OpensPlace,
        /// <summary>It would make PadForge send keys or mouse input to
        /// Windows.</summary>
        SendsInput,
        /// <summary>It imports a profile file.</summary>
        ImportsProfile,
        /// <summary>It turns on an extra that listens on the network. Asks
        /// only when it turns the extra on.</summary>
        Network,
        /// <summary>It changes drivers or HidHide.</summary>
        Drivers,
        /// <summary>It changes Use Only in PadForge.</summary>
        UseOnly,
    }

    /// <summary>
    /// The reach class every action, dialog button, setting and data-bound
    /// element carries in the markup (#600): <c>common:Reach.Class</c> on a
    /// page's, a dialog's or a window's root, and on any part below it that
    /// differs, with <c>common:Reach.Why</c> where it asks. An element's
    /// class is the strictest one set on it or above it, and its reason the
    /// one set beside the nearest of those (<see cref="Of"/>). Search's index reads the same attributes from
    /// the same markup (SearchIndexBuilder), so a catalog entry and its
    /// element always carry one class.
    ///
    /// <para>The climb follows the element tree and crosses the places WPF
    /// does not carry a value across on its own: a context menu climbs to
    /// the element it opened from, and a popup's content to the popup. A
    /// keyed template sets its own class on its root, which the markup test
    /// holds to.</para>
    ///
    /// <para>No look changes: the properties draw nothing.</para>
    /// </summary>
    public static class Reach
    {
        public static readonly DependencyProperty ClassProperty = DependencyProperty.RegisterAttached(
            "Class", typeof(ReachClass), typeof(Reach), new PropertyMetadata(ReachClass.Unset));

        public static readonly DependencyProperty WhyProperty = DependencyProperty.RegisterAttached(
            "Why", typeof(ReachReason), typeof(Reach), new PropertyMetadata(ReachReason.None));

        public static ReachClass GetClass(DependencyObject element) => (ReachClass)element.GetValue(ClassProperty);
        public static void SetClass(DependencyObject element, ReachClass value) => element.SetValue(ClassProperty, value);
        public static ReachReason GetWhy(DependencyObject element) => (ReachReason)element.GetValue(WhyProperty);
        public static void SetWhy(DependencyObject element, ReachReason value) => element.SetValue(WhyProperty, value);

        /// <summary>What a press of the element would turn on that listens on
        /// the network, as the question card's lines: each extra, named with
        /// the profile that turns it on where the press names one
        /// (AssistantToolWords.ProfileTurnsOn, TurnsOn). Read when a
        /// program's press is planned and again when the user allows it
        /// (#600). Set in code on a dialog's button that brings a profile into
        /// force or lets the foreground watcher do so: by what opens a profile
        /// Delete's confirm and Get Profiles' Save and Apply, and by Edit
        /// Profile for its own Save.</summary>
        public static readonly DependencyProperty StartsProperty = DependencyProperty.RegisterAttached(
            "Starts", typeof(System.Func<System.Collections.Generic.IReadOnlyList<string>>), typeof(Reach), new PropertyMetadata(null));

        public static System.Func<System.Collections.Generic.IReadOnlyList<string>> GetStarts(DependencyObject element)
            => element.GetValue(StartsProperty) as System.Func<System.Collections.Generic.IReadOnlyList<string>>;

        public static void SetStarts(DependencyObject element, System.Func<System.Collections.Generic.IReadOnlyList<string>> value)
            => element.SetValue(StartsProperty, value);

        /// <summary>The class an element ends up with, and why it asks: the
        /// strictest set on it or above it (Free, then Asks, then the user's
        /// own), so a part can only tighten what holds it, never loosen it. A
        /// template that sets Free on its root and is drawn inside a stricter
        /// place takes that place's class. <see cref="ReachClass.Unset"/>
        /// when nothing on the way up sets one.</summary>
        public static (ReachClass Class, ReachReason Why) Of(DependencyObject element)
        {
            ReachClass best = ReachClass.Unset;
            ReachReason why = ReachReason.None;
            for (var d = element; d != null; d = Up(d))
            {
                if (d.ReadLocalValue(ClassProperty) is ReachClass c && c > best)
                {
                    best = c;
                    why = d.ReadLocalValue(WhyProperty) is ReachReason w ? w : ReachReason.None;
                }
            }
            return (best, best == ReachClass.Asks ? why : ReachReason.None);
        }

        /// <summary>The class a program reads an element by: Unset reads as
        /// the user's own.</summary>
        public static ReachClass Effective(DependencyObject element)
            => Of(element).Class is var c && c == ReachClass.Unset ? ReachClass.UsersOwn : c;

        private static DependencyObject Up(DependencyObject d)
        {
            if (d is ContextMenu menu) return menu.PlacementTarget;
            if (d is ToolTip tip) return tip.PlacementTarget;
            DependencyObject parent = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
            parent ??= LogicalTreeHelper.GetParent(d);
            if (parent == null && d is FrameworkElement fe)
            {
                parent = fe.TemplatedParent;
                // A popup's content hangs from the popup by the logical tree
                // alone, and its root by nothing.
                if (parent == null && fe.Parent is Popup popup) parent = popup;
            }
            return parent;
        }
    }
}
