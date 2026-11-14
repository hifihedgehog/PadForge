using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// The capacitive touch sensors of a Valve slot, as controls over the 2D
    /// and 3D previews: the stick tops on the Steam Deck and the 2026 Steam
    /// Controller, and the grips on the 2026 pad (HIDMaestro 1.12.0). The
    /// front art has no place to click them: a stick cap's click is the stick
    /// button, and the grips have no art. Each capsule lights while its output
    /// is on, a click records its row, and it flashes while the row records,
    /// as every other preview control does.
    /// </summary>
    public sealed class TouchSensorStrip : UserControl, IPictureWithPick
    {
        /// <summary>The capacitive roles, in the order the capsules show.</summary>
        internal static readonly string[] Roles = { "LeftStickTouch", "RightStickTouch", "LeftGripTouch", "RightGripTouch" };

        /// <summary>Raised with the role a capsule records, in the preview
        /// element grammar PadPage translates to the slot's raw row.</summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        /// <summary>Raised when the capsules were made again (#574).</summary>
        public event EventHandler PickBoundsChanged;

        private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        // Ember (#175), the color every lit preview control takes.
        private static readonly Brush LitBrush = Frozen(0xFF, 0xFF, 0x6B, 0x2C);
        private static readonly Brush HoverBrush = Frozen(0xFF, 0xFF, 0xA2, 0x4D);
        private static readonly Brush FlashBrush = Frozen(0xFF, 0xFF, 0xA5, 0x00);
        private static readonly Brush RestBrush = Frozen(0x30, 0x88, 0x88, 0x88);
        private static readonly Brush EdgeBrush = Frozen(0x60, 0x88, 0x88, 0x88);

        private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
        private readonly Dictionary<string, Border> _capsules = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hovered = new(StringComparer.Ordinal);
        private PadViewModel _vm;
        private string _flashRole;
        private bool _flashOn;
        private DispatcherTimer _flashTimer;

        public TouchSensorStrip()
        {
            Content = _panel;
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(this, "TouchSensorStrip");
            // Subscribed only while loaded, so a page that leaves the tree
            // does not stay reachable from its slot's view model.
            DataContextChanged += (s, e) => { if (IsLoaded) Attach(DataContext as PadViewModel); };
            Loaded += (s, e) => Attach(DataContext as PadViewModel);
            Unloaded += (s, e) => Attach(null);
            // Each capsule carries its label as text, so a language switch
            // rebuilds them: the view model's own refresh raises none of the
            // properties the strip rebuilds on. Held weakly by Strings.
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnCultureChanged));
                return;
            }
            if (_vm != null) Rebuild();
        }

        /// <summary>The touch roles a slot's capsules show: the capacitive
        /// roles its Valve wire table carries, none on any other slot.</summary>
        internal static IReadOnlyList<string> RolesFor(VirtualControllerType type, string profileId)
        {
            var roles = new List<string>(Roles.Length);
            if (type != VirtualControllerType.Extended || string.IsNullOrEmpty(profileId)) return roles;
            foreach (string role in Roles)
                if (NintendoPreviewMap.IndexOf(profileId, role) >= 0) roles.Add(role);
            return roles;
        }

        /// <summary>The role a recording target names, or null: a Valve slot
        /// records its raw grid rows (RawBtn18), which name a role through
        /// the wire table.</summary>
        internal static string RoleOf(string recordingTarget, string profileId)
        {
            if (string.IsNullOrEmpty(recordingTarget)) return null;
            string role = recordingTarget.StartsWith("Raw", StringComparison.Ordinal)
                ? NintendoPreviewMap.ToPreview(recordingTarget, profileId)
                : recordingTarget;
            return Array.IndexOf(Roles, role) >= 0 ? role : null;
        }

        internal static string LabelOf(string role)
        {
            var s = Strings.Instance;
            return role switch
            {
                "LeftStickTouch" => s.Mapping_CapSenseLeftStickTouch,
                "RightStickTouch" => s.Mapping_CapSenseRightStickTouch,
                "LeftGripTouch" => s.Mapping_CapSenseLeftGripTouch,
                "RightGripTouch" => s.Mapping_CapSenseRightGripTouch,
                _ => role,
            };
        }

        private bool IsOn(string role) => _vm != null && role switch
        {
            "LeftStickTouch" => _vm.LeftStickTouch,
            "RightStickTouch" => _vm.RightStickTouch,
            "LeftGripTouch" => _vm.LeftGripTouch,
            "RightGripTouch" => _vm.RightGripTouch,
            _ => false,
        };

        private void Attach(PadViewModel vm)
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
            Rebuild();
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // The view model raises from the poll's UI tick and from
            // configuration changes. Anything off the UI thread comes back
            // to it.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnVmPropertyChanged(sender, e));
                return;
            }
            switch (e.PropertyName)
            {
                case nameof(PadViewModel.OutputType):
                case nameof(PadViewModel.ProfileId):
                case null:
                case "":
                    Rebuild();
                    break;
                case nameof(PadViewModel.CurrentRecordingTarget):
                    UpdateFlash(_vm?.CurrentRecordingTarget);
                    break;
                case nameof(PadViewModel.LeftStickTouch):
                case nameof(PadViewModel.RightStickTouch):
                case nameof(PadViewModel.LeftGripTouch):
                case nameof(PadViewModel.RightGripTouch):
                    Paint(e.PropertyName);
                    break;
            }
        }

        private void Rebuild()
        {
            StopFlash();
            _panel.Children.Clear();
            _capsules.Clear();
            _hovered.Clear();
            IReadOnlyList<string> roles = _vm == null ? Array.Empty<string>() : RolesFor(_vm.OutputType, _vm.ProfileId);
            foreach (string role in roles)
            {
                string label = LabelOf(role);
                var text = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                var capsule = new Border
                {
                    Child = text,
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(9, 2, 9, 3),
                    Margin = new Thickness(3, 0, 3, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = label,
                };
                AutomationProperties.SetAutomationId(capsule, "TouchSensor_" + role);
                AutomationProperties.SetName(capsule, label);
                string r = role;
                capsule.MouseEnter += (s, e) => { _hovered.Add(r); Paint(r); };
                capsule.MouseLeave += (s, e) => { _hovered.Remove(r); Paint(r); };
                capsule.MouseLeftButtonDown += (s, e) =>
                {
                    ControllerElementRecordRequested?.Invoke(this, r);
                    e.Handled = true;
                };
                _capsules[role] = capsule;
                _panel.Children.Add(capsule);
                Paint(role);
            }
            Visibility = _capsules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateFlash(_vm?.CurrentRecordingTarget);
            PickBoundsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Where a target's capsule is drawn (#574), for the page's
        /// ring on the picked control. A capsule's ends are round.</summary>
        public bool TryGetPickBounds(string target, out Rect bounds, out bool round)
        {
            bounds = default;
            round = true;
            string role = RoleOf(target, _vm?.ProfileId);
            return role != null && _capsules.TryGetValue(role, out var capsule)
                && PicturePick.TryMap(capsule, this, PicturePick.OwnRect(capsule), out bounds);
        }

        /// <summary>One capsule's look: the flash while its row records, ember
        /// while its output is on, a warm edge under the pointer.</summary>
        private void Paint(string role)
        {
            if (!_capsules.TryGetValue(role, out var capsule)) return;
            bool flashing = _flashRole == role && _flashOn;
            capsule.Background = flashing ? FlashBrush : IsOn(role) ? LitBrush : RestBrush;
            capsule.BorderBrush = _hovered.Contains(role) || _flashRole == role ? HoverBrush : EdgeBrush;
        }

        private void UpdateFlash(string recordingTarget)
        {
            string role = _vm == null ? null : RoleOf(recordingTarget, _vm.ProfileId);
            if (role == _flashRole) return;
            StopFlash();
            if (role == null || !_capsules.ContainsKey(role)) return;
            _flashRole = role;
            _flashOn = true;
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += (s, e) => { _flashOn = !_flashOn; Paint(_flashRole); };
            _flashTimer.Start();
            Paint(role);
        }

        private void StopFlash()
        {
            if (_flashTimer != null) { _flashTimer.Stop(); _flashTimer = null; }
            string was = _flashRole;
            _flashRole = null;
            _flashOn = false;
            if (was != null) Paint(was);
        }
    }
}
