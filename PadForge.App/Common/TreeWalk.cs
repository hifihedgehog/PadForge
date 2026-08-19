using System.Windows;
using System.Windows.Media;

namespace PadForge.Common
{
    /// <summary>
    /// Hit-test-safe upward tree step. VisualTreeHelper.GetParent THROWS
    /// InvalidOperationException on anything that is not a Visual/Visual3D,
    /// and mouse-event OriginalSource (and InputHitTest results) can be a
    /// ContentElement: a TextBlock composed of inline Runs reports the Run
    /// itself as the click target. Every upward walk seeded from an event's
    /// OriginalSource must step through here (discussion #333: clicking the
    /// slot type badge, whose label is built from Runs, crashed the window's
    /// focus-clearing handler). ContentElements climb their logical tree,
    /// which reaches the hosting Visual (Run -> TextBlock) and the walk
    /// continues visually from there.
    /// </summary>
    internal static class TreeWalk
    {
        internal static DependencyObject Parent(DependencyObject d)
        {
            if (d == null) return null;
            return d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
    }
}
