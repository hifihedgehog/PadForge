using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace PadForge.Views
{
    /// <summary>
    /// The window every PadForge dialog is (#574): a title, a line under it, the dialog's own content, and a
    /// foot of buttons over a hairline, on the card fill. Its look is the style keyed by this type in
    /// Resources/Dialogs.xaml. That dictionary builds on button styles in App.xaml's own dictionary, which a
    /// dictionary merged into App.xaml loads ahead of, so each dialog merges it into its own resources here,
    /// before its InitializeComponent runs. A dialog's XAML therefore lists its resources directly under
    /// DialogWindow.Resources, never inside a ResourceDictionary element, which would replace this one.
    /// Windows draws the corners and the shadow. The edge takes the dialogs' edge color.
    /// </summary>
    public class DialogWindow : Wpf.Ui.Controls.FluentWindow
    {
        public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
            nameof(Subtitle), typeof(object), typeof(DialogWindow), new PropertyMetadata(null, OnSlotChanged));
        public static readonly DependencyProperty FootProperty = DependencyProperty.Register(
            nameof(Foot), typeof(object), typeof(DialogWindow), new PropertyMetadata(null, OnSlotChanged));
        public static readonly DependencyProperty IsWideProperty = DependencyProperty.Register(
            nameof(IsWide), typeof(bool), typeof(DialogWindow), new PropertyMetadata(false));
        public static readonly DependencyProperty ShowCloseProperty = DependencyProperty.Register(
            nameof(ShowClose), typeof(bool), typeof(DialogWindow), new PropertyMetadata(false));
        public static readonly DependencyProperty ScrollsBodyProperty = DependencyProperty.Register(
            nameof(ScrollsBody), typeof(bool), typeof(DialogWindow), new PropertyMetadata(true));
        public static readonly DependencyProperty HeadPaddingProperty = DependencyProperty.Register(
            nameof(HeadPadding), typeof(Thickness), typeof(DialogWindow), new PropertyMetadata(new Thickness(22, 20, 22, 0)));
        public static readonly DependencyProperty BodyPaddingProperty = DependencyProperty.Register(
            nameof(BodyPadding), typeof(Thickness), typeof(DialogWindow), new PropertyMetadata(new Thickness(22, 14, 22, 16)));
        public static readonly DependencyProperty FootPaddingProperty = DependencyProperty.Register(
            nameof(FootPadding), typeof(Thickness), typeof(DialogWindow), new PropertyMetadata(new Thickness(22, 14, 22, 14)));

        /// <summary>The dialogs' dictionary, read once per UI thread. A test host runs dialogs on several.</summary>
        [ThreadStatic] private static ResourceDictionary _looks;

        internal static ResourceDictionary Looks => _looks ??= new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PadForge;component/Resources/Dialogs.xaml", UriKind.Absolute),
        };

        private ButtonBase _close;

        public DialogWindow()
        {
            Resources.MergedDictionaries.Add(Looks);
            SetResourceReference(StyleProperty, typeof(DialogWindow));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // ExtendsContentIntoTitleBar leaves no caption, so the dialog moves by any part of it that does not
            // take the click itself. A button, a box and a list row mark the press handled. A border drawn as a
            // button (a chip, a drop zone) marks nothing, and a drag would swallow the release its Click waits
            // for, so a press on anything the pointer shows as clickable stays out of the drag.
            MouseLeftButtonDown += (_, e) =>
            {
                if (PressLandedOnSomethingClickable(e.OriginalSource as DependencyObject)) return;
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            ApplicationThemeManager.Changed += OnThemeChanged;
        }

        /// <summary>The line under the title: text, or a TextBlock with runs of its own.</summary>
        public object Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

        /// <summary>The foot's content, a FlexLinePanel in the DialogFootPanel style.</summary>
        public object Foot { get => GetValue(FootProperty); set => SetValue(FootProperty, value); }

        /// <summary>The Wide head of the 680 dialogs: a 20 title, a secondary line, more room.</summary>
        public bool IsWide { get => (bool)GetValue(IsWideProperty); set => SetValue(IsWideProperty, value); }

        /// <summary>Shows the X at the head's end, as the Wide boards draw it.</summary>
        public bool ShowClose { get => (bool)GetValue(ShowCloseProperty); set => SetValue(ShowCloseProperty, value); }

        /// <summary>The body scrolls once the window reaches its cap. Off for a dialog that sizes its own list.</summary>
        public bool ScrollsBody { get => (bool)GetValue(ScrollsBodyProperty); set => SetValue(ScrollsBodyProperty, value); }

        public Thickness HeadPadding { get => (Thickness)GetValue(HeadPaddingProperty); set => SetValue(HeadPaddingProperty, value); }
        public Thickness BodyPadding { get => (Thickness)GetValue(BodyPaddingProperty); set => SetValue(BodyPaddingProperty, value); }
        public Thickness FootPadding { get => (Thickness)GetValue(FootPaddingProperty); set => SetValue(FootPaddingProperty, value); }

        internal FrameworkElement HeadPart => GetTemplateChild("PART_Head") as FrameworkElement;
        internal TextBlock TitlePart => GetTemplateChild("PART_Title") as TextBlock;
        internal ButtonBase ClosePart => _close;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (_close != null) _close.Click -= Close_Click;
            _close = GetTemplateChild("PART_Close") as ButtonBase;
            if (_close != null) _close.Click += Close_Click;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => OnCloseRequested();

        /// <summary>The head's X closes with no result, as Escape does. A dialog whose X means more overrides this.</summary>
        protected virtual void OnCloseRequested() => Close();

        /// <summary><para>True when a press landed on anything the user can click, so the window drag stands
        /// down and lets the control have its release.</para>
        /// <para>Two tests, and both are needed. The standard controls are named outright. Everything else is
        /// caught by the hand cursor, which is the app's convention for "this Border is a button" and, more to
        /// the point, is what the user is shown before they click: if the pointer says the thing is pressable,
        /// pressing it must not drag the window instead. The Workshop browser's chips and back link went dead
        /// the moment its window became draggable without this guard.</para></summary>
        internal static bool PressLandedOnSomethingClickable(DependencyObject hit)
        {
            for (var d = hit; d != null;)
            {
                if (d is ButtonBase or TextBoxBase or Selector or ScrollBar or Thumb
                    or ScrollViewer or ComboBox or Slider or MenuItem)
                    return true;
                if (d is FrameworkElement fe && fe.Cursor == Cursors.Hand)
                    return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // A long list scrolls inside the body rather than running off the screen.
            double cap = SystemParameters.WorkArea.Height - 64;
            if (cap > 200 && MaxHeight > cap) MaxHeight = cap;
            ApplyEdge();
        }

        // FluentWindow paints the edge on activation and resets it on deactivation, so the dialogs' color goes
        // back on after it.
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ApplyEdge(); }

        protected override void OnDeactivated(EventArgs e) { base.OnDeactivated(e); ApplyEdge(); }

        protected override void OnClosed(EventArgs e)
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            base.OnClosed(e);
        }

        private void OnThemeChanged(ApplicationTheme theme, Color accent) => Dispatcher.BeginInvoke(new Action(ApplyEdge));

        private void ApplyEdge()
        {
            // Windows 11 draws the 1 px edge in a color a window names.
            if (Environment.OSVersion.Version.Build < 22000) return;
            if (ApplicationThemeManager.GetAppTheme() == ApplicationTheme.HighContrast) return;
            // A user who turned on the accent color for window borders keeps it on the active dialog.
            if (IsActive && ApplicationAccentColorManager.IsAccentColorOnTitleBarsEnabled) return;
            if (TryFindResource("SteelPillLineBrush") is not SolidColorBrush brush) return;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int colorRef = brush.Color.R | (brush.Color.G << 8) | (brush.Color.B << 16);
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorRef, sizeof(int));
        }

        private const int DwmwaBorderColor = 34;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // The subtitle and the foot are the window's logical children, as its content is, so a binding in them
        // reads the window's data and FindName reaches their names (FoldCard.HeaderAction does the same).
        private static void OnSlotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var window = (DialogWindow)d;
            if (e.OldValue != null) window.RemoveLogicalChild(e.OldValue);
            if (e.NewValue != null) window.AddLogicalChild(e.NewValue);
        }

        protected override IEnumerator LogicalChildren
        {
            get
            {
                var children = base.LogicalChildren;
                if (Subtitle == null && Foot == null) return children;
                var list = new ArrayList();
                if (children != null)
                    while (children.MoveNext()) list.Add(children.Current);
                if (Subtitle != null) list.Add(Subtitle);
                if (Foot != null) list.Add(Foot);
                return list.GetEnumerator();
            }
        }
    }
}
