using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PadForge.Controls;
using PadForge.Resources.Strings;

namespace PadForge.Common
{
    /// <summary>
    /// Names controls for screen readers as each control first shows, from
    /// text the UI already shows wherever there is any. Nothing on screen
    /// changes.
    /// The order is the HTML accessible-name order: visible text first, then
    /// the tooltip (HTML's title), then placeholder text.
    ///
    /// <para>A button that shows text keeps it as its name. One whose content
    /// is a panel holding text is labeled by the first text it shows, since
    /// FrameworkElement.GetPlainText gives a panel no text and the button
    /// would read nothing. A button that shows no text (a glyph, an icon, a
    /// shape) is named by its tooltip, when the tooltip is a string, the
    /// string BidiText.ToolTip draws, or a TextBlock.</para>
    ///
    /// <para>An input (TextBox, NumberBox, PasswordBox, ComboBox, Slider,
    /// RangeSlider, RichTextBox, ListBox, CurveEditor) without a name of its
    /// own is
    /// labeled by the label beside it or above its row (FindLabel), and
    /// AccessibilityXamlTests counts the inputs that rule reaches as named. An
    /// input without such a label is named by its tooltip, or else by its
    /// placeholder text. A row's later input of a kind (the second end of a
    /// range, the raw value beside a percentage) adds what sets it apart to
    /// the row's label: the word between a range's two ends, and its own
    /// tooltip when that is a short phrase (SetRowName).</para>
    ///
    /// <para>A list item whose data has no ToString of its own and whose
    /// content is not text would read a type name, since
    /// ItemAutomationPeer.GetNameCore falls back to the item's ToString(). An
    /// enum value would read its code name in place of the text its template
    /// shows. Such an item is labeled by the first text it shows. Items that
    /// already read well are left alone.</para>
    ///
    /// <para>A button that is a part of a control's template and shows no
    /// text (a scroll bar's arrows, a number box's arrows, the title bar's
    /// buttons) is named for what it does, from strings kept for that
    /// (NamePart). A glyph wpf-ui draws inside a button (a check box's
    /// mark, a scroll bar's arrow) reads as the button does
    /// (NameGlyphs).</para>
    ///
    /// <para>A name set in XAML or code is never replaced. A name set here is
    /// marked, so a control that shows again after being collapsed is named
    /// again.</para>
    /// </summary>
    internal static class AccessibleNames
    {
        private static bool _started;

        private static readonly DependencyProperty AutoProperty = DependencyProperty.RegisterAttached(
            "Auto", typeof(bool), typeof(AccessibleNames), new PropertyMetadata(false));

        /// <summary>Watches buttons, inputs and list items as they first
        /// show. Called once, at startup.</summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;
            // Loaded would miss most controls: WPF sends it only to an element
            // with a Loaded handler of its own, a class handler not counting
            // (FrameworkElement.ThisHasLoadedChangeEventHandler,
            // BroadcastEventHelper.OnBroadcastCallback). SizeChanged reaches
            // every element whose size changes (UIElement.
            // markForSizeChangedIfNeeded, FrameworkElement.OnRenderSizeChanged),
            // so a control is named when it first takes a size, which is when
            // it first shows, and again each time it shows after being
            // collapsed.
            var onInput = Sized(fe => NameInput(fe));
            // A CurveEditor takes the axis label above it as an input does.
            // A range slider takes its row's label, and its two thumbs read
            // by it (RangeThumbPeer).
            foreach (Type type in new[] { typeof(TextBox), typeof(PasswordBox), typeof(ComboBox), typeof(Slider), typeof(RangeSlider), typeof(RichTextBox), typeof(ListBox), typeof(CurveEditor) })
                EventManager.RegisterClassHandler(type, FrameworkElement.SizeChangedEvent, onInput);
            // Button, RepeatButton and ToggleButton, so CheckBox and
            // RadioButton too.
            EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.SizeChangedEvent,
                Sized(fe => NameButton((ButtonBase)fe)));
            // A ListBox, ListView or ComboBox wraps each item in a
            // ListBoxItem, a plain ItemsControl in a ContentPresenter. A
            // plain list's item also reads its children again, now that
            // its content is built (ListItemPeers).
            EventManager.RegisterClassHandler(typeof(ListBoxItem), FrameworkElement.SizeChangedEvent,
                Sized(fe => NameItem(fe)));
            EventManager.RegisterClassHandler(typeof(ContentPresenter), FrameworkElement.SizeChangedEvent,
                Sized(fe =>
                {
                    NameItem(fe);
                    ListItemPeers.Refresh(fe);
                }));
        }

        /// <summary>A SizeChanged handler that names an element when it grows
        /// from no size, and leaves every later resize alone.</summary>
        private static SizeChangedEventHandler Sized(Action<FrameworkElement> name)
            => (sender, e) =>
            {
                if (e.PreviousSize.Width == 0 && e.PreviousSize.Height == 0 && sender is FrameworkElement fe)
                    name(fe);
            };

        /// <summary>"Slot 3": the label and number the Dashboard card shows
        /// for a slot, which name the slot wherever it appears.</summary>
        internal static string SlotTitle(int slotNumber)
            => Strings.Instance.Dashboard_Slot + " " + slotNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>Names a shown button that has no name of its own. A part
        /// of a control's template is NamePart's.</summary>
        internal static void NameButton(ButtonBase button)
        {
            NameGlyphs(button);
            if (HasOwnName(button))
                return;
            if (IsTemplatePart(button))
            {
                NamePart(button);
                return;
            }
            ClearAuto(button);
            if (button.Content is FrameworkElement content && content is not TextBlock
                && FirstShownText(content) is TextBlock text)
            {
                NameAfter(button, text);
            }
            else
            {
                var name = new MultiBinding { Converter = ButtonNameConverter.Instance };
                name.Bindings.Add(new Binding(nameof(FrameworkElement.ToolTip)) { Source = button });
                name.Bindings.Add(new Binding(nameof(ContentControl.Content)) { Source = button });
                name.Bindings.Add(new Binding { Path = new PropertyPath(BidiText.ToolTipProperty), Source = button });
                if (button.ToolTip is TextBlock tip)
                    name.Bindings.Add(TipText(tip));
                BindingOperations.SetBinding(button, AutomationProperties.NameProperty, name);
            }
            button.SetValue(AutoProperty, true);
        }

        /// <summary>The text a TextBlock tooltip shows. The TextBlock joins
        /// the tree only when the tooltip opens
        /// (PopupControlService.BeginShowToolTip), so before then a binding of
        /// its own to the data context reads nothing. Such a binding is copied
        /// to read the button's data context, which the open tooltip inherits
        /// through its popup (Popup uses its placement target as its
        /// inheritance parent). Text set in the XAML or bound to a source
        /// reads as it is.</summary>
        private static BindingBase TipText(TextBlock tip)
        {
            if (BindingOperations.GetBinding(tip, TextBlock.TextProperty) is Binding b
                && b.Source == null && b.ElementName == null && b.RelativeSource == null)
            {
                var copy = new Binding
                {
                    Path = b.Path,
                    Converter = b.Converter,
                    ConverterParameter = b.ConverterParameter,
                    ConverterCulture = b.ConverterCulture,
                    StringFormat = b.StringFormat,
                    Mode = BindingMode.OneWay,
                };
                // BindingBase stores even an unset value it is given, so only
                // values the tooltip's binding sets are copied.
                if (b.TargetNullValue != DependencyProperty.UnsetValue)
                    copy.TargetNullValue = b.TargetNullValue;
                if (b.FallbackValue != DependencyProperty.UnsetValue)
                    copy.FallbackValue = b.FallbackValue;
                return copy;
            }
            return new Binding(nameof(TextBlock.Text)) { Source = tip, Mode = BindingMode.OneWay };
        }

        /// <summary>Names the glyphs a button's template or content draws
        /// with a plain TextBlock, after the button. wpf-ui's SymbolIcon and
        /// FontIcon build their TextBlock in code (FontIcon.InitializeChildren),
        /// outside any template, so TextBlockAutomationPeer keeps it in the
        /// control view and a screen reader read the glyph's private-use
        /// character: the mark of a checked check box (CheckBox.xaml
        /// ControlIcon) and a scroll bar's arrows (ScrollBar.xaml
        /// UiScrollBarLineButton). A stock TextBlock cannot leave the control
        /// view, so the glyph reads as its button does, as the text a button
        /// shows reads as the button's name. PadForge's own glyphs are
        /// GlyphText, which is out of the control view, and a button inside
        /// this one names its own.</summary>
        internal static void NameGlyphs(ButtonBase button)
        {
            var stack = new Stack<DependencyObject>();
            for (int i = VisualTreeHelper.GetChildrenCount(button) - 1; i >= 0; i--)
                stack.Push(VisualTreeHelper.GetChild(button, i));
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                if (d is ButtonBase)
                    continue;
                if (d is TextBlock text)
                {
                    if (text is not GlyphText && IsGlyphOnly(text.Text) && !HasOwnName(text)
                        && (text.TemplatedParent == null || text.TemplatedParent is ContentPresenter))
                    {
                        var name = new MultiBinding { Converter = GlyphNameConverter.Instance };
                        name.Bindings.Add(new Binding { Path = new PropertyPath(AutomationProperties.NameProperty), Source = button });
                        name.Bindings.Add(new Binding(nameof(ContentControl.Content)) { Source = button });
                        BindingOperations.SetBinding(text, AutomationProperties.NameProperty, name);
                        text.SetValue(AutoProperty, true);
                    }
                    continue;
                }
                int count = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
                for (int i = count - 1; i >= 0; i--)
                    stack.Push(VisualTreeHelper.GetChild(d, i));
            }
        }

        /// <summary>A glyph's name from [0] its button's name and [1] its
        /// button's content: the name, or else the text the button shows.
        /// Empty when the button has neither, which leaves the glyph as it
        /// was.</summary>
        internal sealed class GlyphNameConverter : IMultiValueConverter
        {
            internal static readonly GlyphNameConverter Instance = new();

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values.Length > 0 && values[0] is string name && IsLabelText(name))
                    return name;
                return values.Length > 1 && values[1] is string shown && IsLabelText(shown) ? shown : string.Empty;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
        }

        /// <summary>The Strings key that names a part of a control's template
        /// by the command it runs: a scroll bar's arrows and the page areas
        /// either side of its thumb, and a slider's track either side of its
        /// thumb.</summary>
        private static readonly Dictionary<ICommand, string> CommandNames = new()
        {
            [ScrollBar.LineUpCommand] = nameof(Strings.Common_ScrollUp),
            [ScrollBar.LineDownCommand] = nameof(Strings.Common_ScrollDown),
            [ScrollBar.LineLeftCommand] = nameof(Strings.Common_ScrollLeft),
            [ScrollBar.LineRightCommand] = nameof(Strings.Common_ScrollRight),
            [ScrollBar.PageUpCommand] = nameof(Strings.Common_PageUp),
            [ScrollBar.PageDownCommand] = nameof(Strings.Common_PageDown),
            [ScrollBar.PageLeftCommand] = nameof(Strings.Common_PageLeft),
            [ScrollBar.PageRightCommand] = nameof(Strings.Common_PageRight),
            [Slider.DecreaseLarge] = nameof(Strings.Common_Decrease),
            [Slider.IncreaseLarge] = nameof(Strings.Common_Increase),
        };

        /// <summary>The Strings key that names a wpf-ui template part by its
        /// name: a number box's arrows, and the clear button of a number box
        /// or a text box.</summary>
        private static readonly Dictionary<string, string> PartNames = new(StringComparer.Ordinal)
        {
            ["PART_InlineIncrementButton"] = nameof(Strings.Common_Increase),
            ["PART_InlineDecrementButton"] = nameof(Strings.Common_Decrease),
            ["PART_CompactIncrementButton"] = nameof(Strings.Common_Increase),
            ["PART_CompactDecrementButton"] = nameof(Strings.Common_Decrease),
            ["PART_ClearButton"] = nameof(Strings.Common_Clear),
        };

        /// <summary>Names a button that is a part of a control's template:
        /// a scroll bar's arrows and page areas, a slider's track, a number
        /// box's arrows and clear button, and the title bar's minimize,
        /// maximize or restore, and close buttons. The name is bound to its
        /// Strings key, so it follows a language change, and a title bar
        /// button's follows its type as maximize turns to restore. Only the
        /// name is set: what the part draws stays as its template draws it.
        /// Other parts are left to the control whose template they are.</summary>
        internal static void NamePart(ButtonBase button)
        {
            BindingBase name;
            if (button is Wpf.Ui.Controls.TitleBarButton bar)
            {
                var multi = new MultiBinding { Converter = TitleBarNameConverter.Instance };
                multi.Bindings.Add(new Binding(nameof(Wpf.Ui.Controls.TitleBarButton.ButtonType)) { Source = bar });
                foreach (string key in TitleBarNameConverter.Keys)
                    multi.Bindings.Add(new Binding(key) { Source = Strings.Instance });
                name = multi;
            }
            else if (PartKey(button) is string key)
            {
                name = new Binding(key) { Source = Strings.Instance };
            }
            else
            {
                return;
            }
            ClearAuto(button);
            BindingOperations.SetBinding(button, AutomationProperties.NameProperty, name);
            button.SetValue(AutoProperty, true);
        }

        private static string PartKey(ButtonBase button)
            => button.Command != null && CommandNames.TryGetValue(button.Command, out string byCommand) ? byCommand
             : PartNames.TryGetValue(button.Name ?? string.Empty, out string byName) ? byName
             : null;

        /// <summary>A title bar button's name from [0] its ButtonType and the
        /// strings [1] Minimize, [2] Maximize, [3] Restore and [4] Close.
        /// Empty for the help button, which PadForge's title bar hides.</summary>
        internal sealed class TitleBarNameConverter : IMultiValueConverter
        {
            internal static readonly TitleBarNameConverter Instance = new();

            internal static readonly string[] Keys =
            {
                nameof(Strings.Main_Minimize),
                nameof(Strings.Main_Maximize),
                nameof(Strings.Main_Restore),
                nameof(Strings.Common_Close),
            };

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                int index = values.Length > 0 && values[0] is Wpf.Ui.Controls.TitleBarButtonType type
                    ? type switch
                    {
                        Wpf.Ui.Controls.TitleBarButtonType.Minimize => 1,
                        Wpf.Ui.Controls.TitleBarButtonType.Maximize => 2,
                        Wpf.Ui.Controls.TitleBarButtonType.Restore => 3,
                        Wpf.Ui.Controls.TitleBarButtonType.Close => 4,
                        _ => 0,
                    }
                    : 0;
                return index > 0 && index < values.Length && values[index] is string text ? text : string.Empty;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
        }

        /// <summary>Names a shown input that has no name of its own. A slider
        /// names its track's buttons too.</summary>
        internal static void NameInput(FrameworkElement input)
        {
            // The increase button of PadForge's slider template draws nothing,
            // and wpf-ui's implicit RepeatButton style aligns it left and
            // center (Button.xaml DefaultRepeatButtonStyle), so it never takes
            // a size and has no first size of its own to be named on.
            if (input is Slider slider && slider.Template?.FindName("PART_Track", slider) is Track track)
            {
                if (track.DecreaseRepeatButton != null)
                    NameButton(track.DecreaseRepeatButton);
                if (track.IncreaseRepeatButton != null)
                    NameButton(track.IncreaseRepeatButton);
            }
            if (IsTemplatePart(input) || HasOwnName(input))
                return;
            ClearAuto(input);
            if (FindLabel(input) is object label)
            {
                FrameworkElement joiner = JoinerBefore(input);
                bool later = FollowsOneOfItsKind(input);
                if (joiner != null || later)
                    SetRowName(input, label, joiner, later);
                else
                    SetLabel(input, label);
            }
            else
            {
                var name = new MultiBinding { Converter = FirstTextConverter.Instance };
                name.Bindings.Add(new Binding(nameof(FrameworkElement.ToolTip)) { Source = input });
                name.Bindings.Add(new Binding { Path = new PropertyPath(BidiText.ToolTipProperty), Source = input });
                if (input is Wpf.Ui.Controls.TextBox)
                    name.Bindings.Add(new Binding(nameof(Wpf.Ui.Controls.TextBox.PlaceholderText)) { Source = input });
                BindingOperations.SetBinding(input, AutomationProperties.NameProperty, name);
            }
            input.SetValue(AutoProperty, true);
        }

        /// <summary>Labels a shown list item container (a ListBoxItem, or
        /// the ContentPresenter a plain ItemsControl wraps an item in) that
        /// would read a type name or an enum's code name.</summary>
        internal static void NameItem(FrameworkElement container)
        {
            if (HasOwnName(container))
                return;
            // Cleared first: a recycled container may now hold an item that
            // reads well on its own.
            ClearAuto(container);
            // A ContentPresenter inside a control's template is no item:
            // only an items panel's children have an owner.
            ItemsControl owner = ItemsControl.ItemsControlFromItemContainer(container);
            if (owner == null)
                return;
            object data = owner.ItemContainerGenerator.ItemFromContainer(container);
            if (data == null || data == DependencyProperty.UnsetValue)
                data = container;
            if (data is string || container is ContentControl { Content: string } || container is ContentPresenter { Content: string })
                return;
            // An enum value reads its code name, not the text its template
            // shows.
            string fallback = data.ToString() ?? string.Empty;
            if (data is not Enum && !fallback.StartsWith(data.GetType().ToString(), StringComparison.Ordinal))
                return;
            if (FirstShownText(container) is TextBlock text)
            {
                NameAfter(container, text);
                container.SetValue(AutoProperty, true);
            }
        }

        /// <summary>Names a control by a label FindLabel returned.</summary>
        private static void SetLabel(FrameworkElement fe, object label)
        {
            if (label is SettingResetButton reset)
                BindingOperations.SetBinding(fe, AutomationProperties.NameProperty,
                    new Binding(nameof(SettingResetButton.SettingLabel)) { Source = reset });
            else if (label is FrameworkElement element)
                NameAfter(fe, element);
        }

        /// <summary>Names an input that shares its row's label with an input
        /// before it: the label, then the word between the two ends of a range
        /// when the input is the second end, then the input's own tooltip when
        /// it follows an input of its kind and the tooltip is a short phrase.
        /// A trigger's Range row reads Range, then Range, to, then Range, Raw
        /// axis value, then Range, to, Raw axis value. With the label alone the
        /// boxes of a row read alike, and with the nearest text alone the
        /// second end read "to" and every raw box of a card read by its
        /// tooltip. Each part is bound, so the name follows a language
        /// change.</summary>
        private static void SetRowName(FrameworkElement input, object label, FrameworkElement joiner, bool withTip)
        {
            var name = new MultiBinding { Converter = RowNameConverter.Instance, ConverterParameter = withTip };
            name.Bindings.Add(label is SettingResetButton reset
                ? new Binding(nameof(SettingResetButton.SettingLabel)) { Source = reset }
                : new Binding(label is TextBlock ? nameof(TextBlock.Text) : nameof(ContentControl.Content)) { Source = label, Mode = BindingMode.OneWay });
            name.Bindings.Add(joiner != null
                ? new Binding(nameof(TextBlock.Text)) { Source = joiner, Mode = BindingMode.OneWay }
                : new Binding { Source = string.Empty });
            name.Bindings.Add(new Binding(nameof(FrameworkElement.ToolTip)) { Source = input });
            name.Bindings.Add(new Binding { Path = new PropertyPath(BidiText.ToolTipProperty), Source = input });
            BindingOperations.SetBinding(input, AutomationProperties.NameProperty, name);
            if (label is FrameworkElement element and not SettingResetButton)
                AutomationProperties.SetLabeledBy(input, element);
        }

        /// <summary>A row input's name from [0] the row's label, [1] the word
        /// before the input and [2] the input's ToolTip or [3] its
        /// BidiText.ToolTip string, joined by a comma. The tooltip joins only
        /// when the parameter is true and it is a phrase, not a sentence: a
        /// sentence is help, and UI Automation reads it as the help
        /// text.</summary>
        internal sealed class RowNameConverter : IMultiValueConverter
        {
            internal static readonly RowNameConverter Instance = new();

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                var parts = new List<string>(3);
                if (values.Length > 0 && values[0] is string label && IsLabelText(label))
                    parts.Add(label);
                if (values.Length > 1 && values[1] is string word && IsLabelText(word))
                    parts.Add(word);
                if (parameter is true)
                {
                    string tip = values.Length > 2 && values[2] is string own ? own
                               : values.Length > 3 && values[3] is string bidi ? bidi : null;
                    if (IsPhrase(tip))
                        parts.Add(tip);
                }
                return string.Join(", ", parts);
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;

            /// <summary>Readable text of at most 48 characters with no
            /// sentence end in it.</summary>
            internal static bool IsPhrase(string text)
                => text != null && IsLabelText(text) && text.Length <= 48 && text.IndexOfAny(SentenceEnds) < 0;

            private static readonly char[] SentenceEnds = { '.', '!', '?', (char)0x3002, (char)0xFF01, (char)0xFF1F, (char)0x061F, (char)0x0964 };
        }

        /// <summary>The word right before an input that joins it to the input
        /// before it, or null.</summary>
        private static FrameworkElement JoinerBefore(FrameworkElement input)
        {
            if (LogicalTreeHelper.GetParent(input) is not DependencyObject parent)
                return null;
            FrameworkElement before = null;
            bool grid = parent is Grid;
            int row = Grid.GetRow(input);
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (ReferenceEquals(child, input))
                    return before != null && IsJoiner(before) ? before : null;
                if (child is FrameworkElement fe && (!grid || Grid.GetRow(fe) == row))
                    before = fe;
            }
            return null;
        }

        /// <summary>Whether an input of the same kind comes before this one
        /// in its row with nothing but inputs, separators, joining words and
        /// reset buttons between: the two share the row's label.</summary>
        private static bool FollowsOneOfItsKind(FrameworkElement input)
        {
            if (LogicalTreeHelper.GetParent(input) is not DependencyObject parent)
                return false;
            var siblings = new List<FrameworkElement>();
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
                if (child is FrameworkElement fe)
                    siblings.Add(fe);
            bool grid = parent is Grid;
            int row = Grid.GetRow(input);
            for (int i = siblings.IndexOf(input) - 1; i >= 0; i--)
            {
                FrameworkElement s = siblings[i];
                if (grid && Grid.GetRow(s) != row)
                    continue;
                if (IsInput(s))
                {
                    if (SameKind(s, input))
                        return true;
                    continue;
                }
                if (s is SettingResetButton || IsSeparator(s) || IsJoiner(s))
                    continue;
                return false;
            }
            return false;
        }

        /// <summary>Two inputs UI Automation gives one control type: two
        /// boxes, two pickers or two sliders.</summary>
        private static bool SameKind(FrameworkElement a, FrameworkElement b)
            => (a is TextBox && b is TextBox) || (a is ComboBox && b is ComboBox) || (a is Slider && b is Slider)
               || a.GetType() == b.GetType();

        /// <summary>The word between a range's two ends (Common_To): it joins
        /// two inputs and labels neither.</summary>
        private static bool IsJoiner(FrameworkElement fe)
            => fe is TextBlock && BindingOperations.GetBinding(fe, TextBlock.TextProperty) is Binding b
               && ReferenceEquals(b.Source, Strings.Instance) && b.Path?.Path == nameof(Strings.Common_To);

        /// <summary>Names a control after a label element. The name is bound
        /// to the label's text, so it follows the label through a language
        /// change, and LabeledBy records the relation. LabeledBy alone does
        /// not name a control: UIElementAutomationPeer.GetLabeledByCore reads
        /// only a label peer that already exists (UIElement.GetAutomationPeer),
        /// and a screen reader asks a focused control's name before anything
        /// has made its label's peer.</summary>
        private static void NameAfter(FrameworkElement fe, FrameworkElement label)
        {
            string path = label is TextBlock ? nameof(TextBlock.Text) : nameof(ContentControl.Content);
            BindingOperations.SetBinding(fe, AutomationProperties.NameProperty,
                new Binding(path) { Source = label, Mode = BindingMode.OneWay });
            AutomationProperties.SetLabeledBy(fe, label);
        }

        /// <summary>A part of another control's template, which that control
        /// names. An element made from a DataTemplate has a ContentPresenter
        /// as its templated parent and is named like any other, the line
        /// TextBlockAutomationPeer.IsControlElementCore draws too.</summary>
        private static bool IsTemplatePart(FrameworkElement fe)
            => fe.TemplatedParent != null && fe.TemplatedParent is not ContentPresenter;

        private static bool HasOwnName(FrameworkElement fe)
            => !(bool)fe.GetValue(AutoProperty)
               && (!string.IsNullOrEmpty(AutomationProperties.GetName(fe)) || AutomationProperties.GetLabeledBy(fe) != null);

        /// <summary>Drops a name set here before, so a control that shows
        /// again, in a new place or with a new item, is named afresh.</summary>
        private static void ClearAuto(FrameworkElement fe)
        {
            if (!(bool)fe.GetValue(AutoProperty))
                return;
            fe.ClearValue(AutomationProperties.NameProperty);
            fe.ClearValue(AutomationProperties.LabeledByProperty);
            fe.ClearValue(AutoProperty);
        }

        /// <summary>An input, for the label scan: a range slider sits
        /// between a label and the boxes that set its two ends.</summary>
        private static bool IsInput(DependencyObject d)
            => d is TextBox or PasswordBox or ComboBox or Slider or RichTextBox or Wpf.Ui.Controls.AutoSuggestBox or RangeSlider;

        /// <summary>The label an input takes: a TextBlock or Label, or a reset
        /// button whose SettingLabel names the setting. Null when none is
        /// found. The input's own row is searched first. When the input sits
        /// in a panel or border and nothing before it there stops the search,
        /// the search moves to the row that panel sits in, up to three levels:
        /// the label above a slider and its value box, or above the panel a
        /// dialog's picker sits in.</summary>
        internal static object FindLabel(FrameworkElement input)
        {
            FrameworkElement element = input;
            for (int level = 0; level <= 3; level++)
            {
                if (FindLabelAt(element, out bool open) is object label)
                    return label;
                if (!open || LogicalTreeHelper.GetParent(element) is not FrameworkElement row || row is not (Panel or Decorator))
                    return null;
                element = row;
            }
            return null;
        }

        /// <summary>The label beside an element in its parent and Grid row.
        /// The row's reset button comes first: its SettingLabel names the
        /// setting itself, where the text beside a field can be as short as the
        /// "Port" four Dashboard rows show. That is a reset button later in the
        /// row, past the element's value box and any value it shows but not
        /// past another label, or one docked ahead of it (a DockPanel lists the
        /// right-docked button first). Next comes the nearest earlier sibling
        /// that holds a label, with inputs, reset buttons, literal text
        /// without a letter (a dash between two range boxes) and the word
        /// between a range's two ends passed over.
        /// Literal text right after an input is that input's unit (the Hz after
        /// a frequency box) and labels nothing. In a Grid, a row above the
        /// element's row that holds a label and no input labels it last. Open
        /// is whether nothing before the element stopped the search.</summary>
        private static object FindLabelAt(FrameworkElement input, out bool open)
        {
            open = false;
            if (LogicalTreeHelper.GetParent(input) is not DependencyObject parent)
                return null;
            var siblings = new List<FrameworkElement>();
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
                if (child is FrameworkElement fe)
                    siblings.Add(fe);
            int index = siblings.IndexOf(input);
            if (index < 0)
                return null;
            bool grid = parent is Grid;
            int row = Grid.GetRow(input);

            SettingResetButton before = null;
            FrameworkElement textLabel = null;
            bool passedInput = false;
            open = true;
            for (int i = index - 1; i >= 0; i--)
            {
                FrameworkElement s = siblings[i];
                if (grid && Grid.GetRow(s) != row)
                    continue;
                if (IsInput(s))
                {
                    passedInput = true;
                    continue;
                }
                if (s is SettingResetButton r)
                {
                    if (!passedInput && before == null && !string.IsNullOrEmpty(r.SettingLabel))
                        before = r;
                    continue;
                }
                if (IsSeparator(s) || IsJoiner(s))
                    continue;
                open = false;
                if (IsLiteral(s) && EarlierInRow(siblings, i, grid, row) is { } prior && IsInput(prior))
                    break;
                // A panel holding an input of its own is another control's
                // row, not this input's label.
                if (!HoldsInput(s))
                    textLabel = FirstStringsLabel(s);
                break;
            }
            for (int i = index + 1; i < siblings.Count; i++)
            {
                FrameworkElement s = siblings[i];
                if (grid && Grid.GetRow(s) != row)
                    continue;
                if (s is SettingResetButton reset && !string.IsNullOrEmpty(reset.SettingLabel))
                    return reset;
                if (IsInput(s) || IsJoiner(s))
                    continue;
                // A label starts another control's row.
                if (FirstStringsLabel(s) != null)
                    break;
            }
            if (before != null)
                return before;
            if (textLabel != null)
                return textLabel;
            if (open && grid && row > 0)
                return RowAbove(siblings, row, ref open);
            return null;
        }

        /// <summary>The label in the Grid row above an element's row: a Grid
        /// that stacks a label row over a control row. An empty row above
        /// leaves the search open, and a row holding an input closes it with
        /// no label.</summary>
        private static FrameworkElement RowAbove(List<FrameworkElement> siblings, int row, ref bool open)
        {
            var above = siblings.FindAll(s => Grid.GetRow(s) == row - 1);
            if (above.Count == 0)
                return null;
            open = false;
            if (above.Exists(s => IsInput(s) || HoldsInput(s)))
                return null;
            foreach (FrameworkElement s in above)
                if (FirstStringsLabel(s) is FrameworkElement label)
                    return label;
            return null;
        }

        /// <summary>The first TextBlock or Label under an element, in document
        /// order, whose text is bound to a Strings key or written in the XAML
        /// with a letter in it (the R, G and B beside a color's sliders). A
        /// value bound to data is no label, nor is a dash or a unit
        /// sign.</summary>
        private static FrameworkElement FirstStringsLabel(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                if (d is TextBlock tb && IsLabelText(tb.Text) && IsLabelSource(tb, TextBlock.TextProperty))
                    return tb;
                if (d is Label lb && lb.Content is string s && IsLabelText(s) && IsLabelSource(lb, ContentControl.ContentProperty))
                    return lb;
                var children = new List<DependencyObject>();
                foreach (object child in LogicalTreeHelper.GetChildren(d))
                    if (child is DependencyObject c)
                        children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
            return null;
        }

        /// <summary>Literal text without a letter, such as the dash between
        /// two range boxes.</summary>
        private static bool IsSeparator(FrameworkElement fe)
            => LiteralText(fe) is string text && !HasLetter(text);

        private static bool IsLiteral(FrameworkElement fe) => LiteralText(fe) != null;

        /// <summary>Text a TextBlock or Label is given in the XAML itself, not
        /// through a binding. Null otherwise. Text written on an element of a
        /// DataTemplate or ControlTemplate is no local value: the template
        /// gives it (BaseValueSource.ParentTemplate), and ReadLocalValue
        /// answers unset for it. Read that way, the % after a value box inside
        /// a card's template was no unit, the label search stopped at it, and
        /// the box after it fell back to its tooltip.</summary>
        private static string LiteralText(FrameworkElement fe)
        {
            DependencyProperty property = fe switch
            {
                TextBlock => TextBlock.TextProperty,
                Label => ContentControl.ContentProperty,
                _ => null,
            };
            if (property == null)
                return null;
            ValueSource source = DependencyPropertyHelper.GetValueSource(fe, property);
            if (source.IsExpression || source.BaseValueSource is not (BaseValueSource.Local or BaseValueSource.ParentTemplate))
                return null;
            return fe.GetValue(property) as string;
        }

        /// <summary>The sibling before index in the same Grid row, or
        /// null.</summary>
        private static FrameworkElement EarlierInRow(List<FrameworkElement> siblings, int index, bool grid, int row)
        {
            for (int i = index - 1; i >= 0; i--)
                if (!grid || Grid.GetRow(siblings[i]) == row)
                    return siblings[i];
            return null;
        }

        /// <summary>Whether an element holds an input among its logical
        /// descendants.</summary>
        private static bool HoldsInput(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject c)
                    stack.Push(c);
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                if (IsInput(d))
                    return true;
                foreach (object child in LogicalTreeHelper.GetChildren(d))
                    if (child is DependencyObject c)
                        stack.Push(c);
            }
            return false;
        }

        /// <summary>Whether a label's text is bound to a Strings key, or is a
        /// literal holding a letter.</summary>
        private static bool IsLabelSource(DependencyObject d, DependencyProperty property)
        {
            if (BindingOperations.GetBinding(d, property) is Binding b)
                return ReferenceEquals(b.Source, Strings.Instance);
            return d is FrameworkElement fe && LiteralText(fe) is string literal && HasLetter(literal);
        }

        private static bool HasLetter(string text)
        {
            foreach (char c in text)
                if (char.IsLetter(c))
                    return true;
            return false;
        }

        private static bool IsLabelText(string text)
            => !string.IsNullOrWhiteSpace(text) && text != "%" && !HasGlyph(text);

        /// <summary>The first visible TextBlock in an element's visual tree
        /// that holds readable text.</summary>
        private static TextBlock FirstShownText(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                if (d is TextBlock tb && tb.IsVisible && IsLabelText(tb.Text))
                    return tb;
                int count = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
                for (int i = count - 1; i >= 0; i--)
                    stack.Push(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        /// <summary>Whether text is one or more icon glyphs and nothing
        /// else: characters of the private-use range U+E000 to U+F8FF or of
        /// the private-use planes from U+F0000 up, with any white space
        /// between.</summary>
        private static bool IsGlyphOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            bool any = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                    continue;
                // Half a surrogate pair is no glyph, and ConvertToUtf32
                // throws on one. A name cut short in a device's own string
                // can end on a half, and this runs on every text under a
                // button.
                int point;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    point = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else if (char.IsSurrogate(text[i]))
                    return false;
                else
                    point = text[i];
                if (!((point >= 0xE000 && point <= 0xF8FF) || point >= 0xF0000))
                    return false;
                any = true;
            }
            return any;
        }

        /// <summary>Whether text holds a Segoe icon glyph, the private-use
        /// range U+E000 to U+F8FF.</summary>
        private static bool HasGlyph(string text)
        {
            foreach (char c in text)
                if (c >= 0xE000 && c <= 0xF8FF)
                    return true;
            return false;
        }

        /// <summary>A button's name from [0] its ToolTip, [1] its Content,
        /// [2] its BidiText.ToolTip and [3] the text a TextBlock tooltip shows
        /// (TipText): the tooltip's text when the content shows no text,
        /// otherwise empty. UI Automation treats an empty name as unset and
        /// falls back to the button's own text
        /// (FrameworkElementAutomationPeer.GetNameCore), so text a button
        /// shows stays its name. BidiText.ToolTip draws its string as a
        /// TextBlock, so the string is read from the attached property. Any
        /// other tooltip element gives empty: bound as is, it would read
        /// "System.Windows.Controls.ToolTip". [3] is read only while the
        /// tooltip is still a TextBlock, the one it was taken from until the
        /// button is named again.</summary>
        internal sealed class ButtonNameConverter : IMultiValueConverter
        {
            internal static readonly ButtonNameConverter Instance = new();

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values.Length < 2 || ShowsText(values[1]))
                    return string.Empty;
                if (values[0] is string tip && IsLabelText(tip))
                    return tip;
                if (values.Length > 2 && values[2] is string bidi && IsLabelText(bidi))
                    return bidi;
                return values.Length > 3 && values[0] is TextBlock && values[3] is string shown && IsLabelText(shown)
                    ? shown : string.Empty;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;

            /// <summary>Whether content is text a screen reader would speak: a
            /// string or a TextBlock holding no glyph.</summary>
            internal static bool ShowsText(object content)
            {
                string text = content switch
                {
                    string s => s,
                    TextBlock tb => tb.Text,
                    _ => null,
                };
                return text != null && IsLabelText(text);
            }
        }

        /// <summary>An unlabeled input's name: the first of its values (the
        /// ToolTip, the BidiText.ToolTip string, then the placeholder text)
        /// that is readable text, or empty.</summary>
        internal sealed class FirstTextConverter : IMultiValueConverter
        {
            internal static readonly FirstTextConverter Instance = new();

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                foreach (object value in values)
                    if (value is string text && IsLabelText(text))
                        return text;
                return string.Empty;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
        }
    }
}
