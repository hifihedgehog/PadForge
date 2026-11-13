using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PadForge.Controls
{
    /// <summary>
    /// A tile of the Extras catalog (#574): an extra's glyph, its name, one
    /// line on what it is for, its switch, and its live status while it is
    /// on.
    ///
    /// <para>The tile holds two controls, side by side in the tab order. The
    /// tile's own button covers the whole card and asks for the extra's page
    /// (<see cref="OpenRequested"/>). The switch sits on top of it at the
    /// card's corner, so a click on the switch turns the extra on or off and
    /// never opens the page. Each carries an AutomationId made from
    /// <see cref="Key"/>: Extras_Open_ and Extras_Switch_ plus the key.</para>
    ///
    /// <para>Home shows an extra that is on as a second face of the same
    /// control (the HomeExtraTile style): the glyph, the name, the word On
    /// with the status or the one line after it, one button that opens the
    /// extra's page, and no switch. A tile with <see cref="ActionText"/>
    /// has a second button at its end that opens the page too: always on
    /// Home, and in the catalog while the extra runs.</para>
    /// </summary>
    [TemplatePart(Name = OpenPartName, Type = typeof(ButtonBase))]
    [TemplatePart(Name = SwitchPartName, Type = typeof(ToggleButton))]
    [TemplatePart(Name = ActionPartName, Type = typeof(ButtonBase))]
    public sealed class ExtraTile : Control
    {
        public const string OpenPartName = "PART_Open";
        public const string SwitchPartName = "PART_Switch";
        public const string ActionPartName = "PART_Action";
        public const string OpenIdPrefix = "Extras_Open_";
        public const string SwitchIdPrefix = "Extras_Switch_";

        /// <summary>What the button's AutomationId starts with on Home, where
        /// an extra that is on has a second, smaller face (#574).</summary>
        public const string HomeOpenIdPrefix = "Home_Extra_";

        public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
            nameof(Key), typeof(string), typeof(ExtraTile),
            new PropertyMetadata(null, (d, _) => ((ExtraTile)d).NameParts()));

        public static readonly DependencyProperty OpenPrefixProperty = DependencyProperty.Register(
            nameof(OpenPrefix), typeof(string), typeof(ExtraTile),
            new PropertyMetadata(OpenIdPrefix, (d, _) => ((ExtraTile)d).NameParts()));

        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(ExtraTile), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(ExtraTile), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
            nameof(Line), typeof(string), typeof(ExtraTile),
            new PropertyMetadata(string.Empty, (d, _) => ((ExtraTile)d).SetSummary()));

        public static readonly DependencyProperty SwitchLabelProperty = DependencyProperty.Register(
            nameof(SwitchLabel), typeof(string), typeof(ExtraTile), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
            nameof(IsOn), typeof(bool), typeof(ExtraTile),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
            nameof(Status), typeof(string), typeof(ExtraTile),
            new PropertyMetadata(string.Empty, (d, _) => ((ExtraTile)d).SetSummary()));

        private static readonly DependencyPropertyKey SummaryKey = DependencyProperty.RegisterReadOnly(
            nameof(Summary), typeof(string), typeof(ExtraTile), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty SummaryProperty = SummaryKey.DependencyProperty;

        public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
            nameof(ActionText), typeof(string), typeof(ExtraTile), new PropertyMetadata(string.Empty));

        public static readonly RoutedEvent OpenRequestedEvent = EventManager.RegisterRoutedEvent(
            nameof(OpenRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ExtraTile));

        private ButtonBase _open;
        private ButtonBase _action;
        private ToggleButton _switch;

        public ExtraTile()
        {
            // The two parts take keyboard focus. The tile itself is their frame.
            Focusable = false;
            IsTabStop = false;
        }

        /// <summary>The extra's name in code: the name of its page and the
        /// tail of its parts' AutomationIds. It never shows and is never
        /// translated.</summary>
        public string Key
        {
            get => (string)GetValue(KeyProperty);
            set => SetValue(KeyProperty, value);
        }

        /// <summary>What the button's AutomationId starts with, ahead of the
        /// key. The catalog's tiles keep <see cref="OpenIdPrefix"/>. A row
        /// on Home takes <see cref="HomeOpenIdPrefix"/>, so the two faces of
        /// one extra never share an id.</summary>
        public string OpenPrefix
        {
            get => (string)GetValue(OpenPrefixProperty);
            set => SetValue(OpenPrefixProperty, value);
        }

        /// <summary>The extra's glyph, in Segoe MDL2 Assets.</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>The extra's name.</summary>
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>One line on what the extra is for.</summary>
        public string Line
        {
            get => (string)GetValue(LineProperty);
            set => SetValue(LineProperty, value);
        }

        /// <summary>The switch's name for a screen reader: the label the
        /// extra's own page gives the same setting.</summary>
        public string SwitchLabel
        {
            get => (string)GetValue(SwitchLabelProperty);
            set => SetValue(SwitchLabelProperty, value);
        }

        /// <summary>Whether the extra is on. The switch writes it back.</summary>
        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        /// <summary>The extra's live status, shown while it is on. An extra
        /// with no status line leaves it empty.</summary>
        public string Status
        {
            get => (string)GetValue(StatusProperty);
            set => SetValue(StatusProperty, value);
        }

        /// <summary>What Home says under the name of an extra that is on:
        /// its live status when it has one, and its one-line description
        /// otherwise.</summary>
        public string Summary => (string)GetValue(SummaryProperty);

        private void SetSummary()
            => SetValue(SummaryKey, string.IsNullOrEmpty(Status) ? (Line ?? string.Empty) : Status);

        /// <summary>The words on a second button at the tile's end (Show QR
        /// on the Web Controller's tile), or empty for none. The button
        /// opens the extra's page, as the tile does.</summary>
        public string ActionText
        {
            get => (string)GetValue(ActionTextProperty);
            set => SetValue(ActionTextProperty, value);
        }

        /// <summary>Raised when the tile is clicked anywhere but its switch.</summary>
        public event RoutedEventHandler OpenRequested
        {
            add => AddHandler(OpenRequestedEvent, value);
            remove => RemoveHandler(OpenRequestedEvent, value);
        }

        public override void OnApplyTemplate()
        {
            if (_open != null) _open.Click -= OnOpenClick;
            if (_action != null) _action.Click -= OnOpenClick;
            base.OnApplyTemplate();
            _open = GetTemplateChild(OpenPartName) as ButtonBase;
            _action = GetTemplateChild(ActionPartName) as ButtonBase;
            _switch = GetTemplateChild(SwitchPartName) as ToggleButton;
            if (_open != null) _open.Click += OnOpenClick;
            if (_action != null) _action.Click += OnOpenClick;
            NameParts();
        }

        /// <summary>Puts keyboard focus on the tile's own button, for the
        /// way back from the extra's page.</summary>
        public bool FocusOpen() => _open != null && _open.Focus();

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(OpenRequestedEvent, this));
        }

        private void NameParts()
        {
            string key = Key;
            if (string.IsNullOrEmpty(key)) return;
            if (_open != null) AutomationProperties.SetAutomationId(_open, (OpenPrefix ?? OpenIdPrefix) + key);
            if (_action != null) AutomationProperties.SetAutomationId(_action, (OpenPrefix ?? OpenIdPrefix) + key + "_Action");
            if (_switch != null) AutomationProperties.SetAutomationId(_switch, SwitchIdPrefix + key);
        }
    }
}
