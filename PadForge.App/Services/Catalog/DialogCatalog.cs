using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PadForge.Common;
using PadForge.Search;

namespace PadForge.Services.Catalog
{
    /// <summary>
    /// The dialogs of the catalog (#600). Their markup is read like a page's
    /// (SearchIndexBuilder.BuildCatalog): each dialog is an entry, with its
    /// title, and its buttons and settings are entries of the dialog.
    ///
    /// <para>Search lands a dialog on the button that opens it. A dialog a
    /// page's button opens is in <see cref="Openers"/>, by the page and the
    /// button's Click handler, which a test reads in the code behind the
    /// page. Every other dialog PadForge opens by itself or from a command
    /// that names no button, and is in <see cref="OpenedByPadForge"/>. A
    /// test holds every dialog to one of the two.</para>
    /// </summary>
    internal static class DialogCatalog
    {
        /// <summary>The page button, row button or menu item that opens each
        /// dialog, by the dialog's name: the page and the element's Click
        /// handler or Command.</summary>
        internal static readonly IReadOnlyDictionary<string, (SearchPage Page, string Handler)> Openers =
            new Dictionary<string, (SearchPage, string)>(StringComparer.Ordinal)
            {
                ["ChatCommandsDialog"] = (SearchPage.Extras, "ManageChatCommands_Click"),
                ["CustomTelemetryFormatsDialog"] = (SearchPage.Extras, "GameTelemetryFormatsButton_Click"),
                ["LearnHandheldButtonDialog"] = (SearchPage.Devices, "LearnHandheldButton_Click"),
                ["ManageProfilesDialog"] = (SearchPage.Pad, "ExtendedImportBtn_Click"),
                ["MapKeyLightsDialog"] = (SearchPage.Devices, "MapKeyLights_Click"),
                ["RegisterNfcTagDialog"] = (SearchPage.Devices, "RegisterNfcTag_Click"),
                ["RegisterVoicePhraseDialog"] = (SearchPage.Devices, "ManageVoicePhrases_Click"),
                ["ShiftActivatorDialog"] = (SearchPage.Pad, "AddShiftLayer_Click"),
                // Paste, Find and Guess on the SDL Mapping card, and Add Rows on
                // the Controller Database row, open the one dialog (#600).
                ["SdlMappingDialog"] = (SearchPage.Devices, "SdlPaste_Click"),
                // Commands of the page's data, whose button or menu item
                // names them.
                ["GetProfilesDialog"] = (SearchPage.Profiles, "GetProfilesCommand"),
                ["CopyFromDialog"] = (SearchPage.Pad, "CopyFromCommand"),
            };

        /// <summary>The dialogs no entry of a page opens: PadForge opens them
        /// by itself (a driver switch, a request, a device's screen), from the
        /// window's own parts, from a command of a view's data, or from the
        /// macro editor, whose actions the index leaves to the user (Pick a Sound),
        /// or from a mapping row's icon button, which has no words to index
        /// (Release Test). A row's items reach the catalog through
        /// LiveCatalog.AddProvider.</summary>
        internal static readonly IReadOnlySet<string> NoPageOpener = new HashSet<string>(StringComparer.Ordinal)
        {
            "AddControllerView", "AnswerPairDialog", "AutomationRequestCard", "BlissBoxPlayerDialog", "ConfirmDialog",
            "DreamcastScreenDialog", "FlydigiScreenDialog", "ImportConfigDialog", "KeepDriverDialog",
            "KeyboardScreenDialog", "PairDeviceDialog", "PickSoundDialog", "RecordingReviewView", "ReleaseTestDialog", "ProfileDialog", "RemoteAssignmentsDialog", "RemoteLinkPairDialog",
            "RemoteLinkPasswordDialog", "SearchPanel", "ThemeBuilderDialog", "TouchpadGestureRecorderDialog",
            "UninstallBthPS3Dialog", "UninstallHIDMaestroDialog",
        };
    }

    /// <summary>A button of an open dialog, as the catalog reads it: its
    /// words, its AutomationId, its class and whether it takes a
    /// press.</summary>
    internal sealed record OpenDialogButton(string Words, string AutomationId, ReachClass Reach, ReachReason Why, bool Enabled, Control Element);

    /// <summary>An open window or in-window dialog, read now: its name, its
    /// title, every line of text it shows, its buttons, and its class.</summary>
    internal sealed record OpenDialog(string Name, string Title, IReadOnlyList<string> Text, IReadOnlyList<OpenDialogButton> Buttons,
        ReachClass Reach, bool IsModal, FrameworkElement Element);

    /// <summary>
    /// The one registry every window and every in-window dialog passes
    /// through (#600). <see cref="Watch"/> takes every Window PadForge shows,
    /// as WPF loads it (a class handler on Window's Loaded), and lets it go
    /// when it closes. A ShellDialog registers itself while it is open. So
    /// a dialog added later joins with no change here, and the catalog, the
    /// assistant tools and Search read what is open from one list.
    ///
    /// <para>The list is kept under a lock, since the test host runs
    /// dialogs on several interface threads. Reading an entry's contents
    /// (<see cref="Describe"/>) is done on that element's own thread.</para>
    /// </summary>
    internal static class DialogRegistry
    {
        private static readonly object Sync = new();
        private static readonly List<FrameworkElement> Open = new();
        private static bool _watching;

        /// <summary>Raised when an entry opens or closes, on the thread
        /// that opened or closed it.</summary>
        internal static event Action Changed;

        /// <summary>Starts taking every Window as it loads. Once per process;
        /// the app calls it before its first window shows.</summary>
        internal static void Watch()
        {
            lock (Sync)
            {
                if (_watching) return;
                _watching = true;
            }
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Register(window);
            }));
            // A menu, the tray's and those built in code included, is open
            // while it shows.
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is ContextMenu menu) Register(menu);
            }));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is ContextMenu menu) Unregister(menu);
            }));
        }

        /// <summary>Takes an element as open until it closes: a window until
        /// its Closed, any other until <see cref="Unregister"/>.</summary>
        internal static void Register(FrameworkElement element)
        {
            if (element == null) return;
            lock (Sync)
            {
                if (Open.Contains(element)) return;
                Open.Add(element);
            }
            if (element is Window window) window.Closed += OnClosed;
            Changed?.Invoke();
        }

        internal static void Unregister(FrameworkElement element)
        {
            bool removed;
            lock (Sync) removed = Open.Remove(element);
            if (element is Window window) window.Closed -= OnClosed;
            if (removed) Changed?.Invoke();
        }

        private static void OnClosed(object sender, EventArgs e) => Unregister(sender as FrameworkElement);

        /// <summary>What is open and shows now, oldest first. A window
        /// that is hidden (the tray's menu host, a flyout between its notes,
        /// the main window in the tray) is left out unless
        /// <paramref name="hiddenToo"/>.</summary>
        internal static IReadOnlyList<FrameworkElement> Snapshot(bool hiddenToo = false)
        {
            List<FrameworkElement> all;
            lock (Sync) all = Open.ToList();
            if (hiddenToo) return all;
            return all.Where(e => e.Dispatcher.CheckAccess() ? e.IsVisible : e.Dispatcher.Invoke(() => e.IsVisible)).ToList();
        }

        /// <summary>
        /// An open entry as the catalog reads it, read on its own thread:
        /// the class's name, the title, each TextBlock's text in document
        /// order, each button with its words, AutomationId, class and
        /// whether it takes a press. A button's words are its text, else
        /// its screen-reader name.
        /// </summary>
        internal static OpenDialog Describe(FrameworkElement element)
        {
            if (element == null) return null;
            if (!element.Dispatcher.CheckAccess()) return element.Dispatcher.Invoke(() => Describe(element));
            string title = element switch
            {
                Window w => w.Title,
                PadForge.Controls.ShellDialog shell => AutomationProperties.GetName(shell) is { Length: > 0 } n ? n : (shell.Content as FrameworkElement)?.GetType().Name,
                _ => AutomationProperties.GetName(element),
            };
            var text = new List<string>();
            var buttons = new List<OpenDialogButton>();
            foreach (var part in Walk(element))
            {
                if (part is TextBlock block && block.IsVisible && !string.IsNullOrWhiteSpace(block.Text) && !IsInButton(block, element))
                    text.Add(block.Text);
                else if ((part is ButtonBase || part is MenuItem) && part is Control button && button.IsVisible)
                {
                    string words = button is MenuItem item ? item.Header as string ?? AutomationProperties.GetName(item) : WordsOf((ButtonBase)button);
                    var (cls, why) = Reach.Of(button);
                    buttons.Add(new OpenDialogButton(words, AutomationProperties.GetAutomationId(button), cls == ReachClass.Unset ? ReachClass.UsersOwn : cls,
                        why, button.IsEnabled, button));
                }
            }
            string name = (element is PadForge.Controls.ShellDialog s ? s.Content as FrameworkElement : element)?.GetType().Name;
            return new OpenDialog(name, title, text, buttons, Reach.Effective(element),
                element is Window win && System.Windows.Interop.ComponentDispatcher.IsThreadModal && win.IsActive, element);
        }

        private static string WordsOf(ButtonBase button)
        {
            if (button.Content is string s && s.Length > 0) return s;
            foreach (var t in Walk(button).OfType<TextBlock>())
                if (!string.IsNullOrWhiteSpace(t.Text)) return t.Text;
            return AutomationProperties.GetName(button);
        }

        private static bool IsInButton(DependencyObject part, DependencyObject stop)
        {
            for (var d = LogicalTreeHelper.GetParent(part) ?? System.Windows.Media.VisualTreeHelper.GetParent(part); d != null && d != stop;
                 d = LogicalTreeHelper.GetParent(d) ?? (d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : null))
                if (d is ButtonBase) return true;
            return false;
        }

        /// <summary>An element and everything under it, logical tree first,
        /// then what a template draws.</summary>
        private static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            var seen = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            var order = new List<DependencyObject>();
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!seen.Add(d)) continue;
                order.Add(d);
                var kids = new List<DependencyObject>();
                foreach (object child in LogicalTreeHelper.GetChildren(d))
                    if (child is DependencyObject c) kids.Add(c);
                if (d is System.Windows.Media.Visual)
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                        kids.Add(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
            }
            return order;
        }
    }
}
