using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PadForge.Engine.RemoteLink;
using Wpf.Ui.Controls;
using S = PadForge.Resources.Strings.Strings;

namespace PadForge.Views
{
    public partial class RemoteAssignmentsDialog : FluentWindow
    {
        private readonly LinkAssignmentChannel _channel;
        private readonly Func<IReadOnlyList<RemotePeerDeviceInfo>> _devices;
        private readonly CancellationTokenSource _closing = new();
        private bool _busy;
        private bool _ready;
        private bool _closed;
        private long _revision;
        private string _deviceId;

        private sealed record SlotChoice(byte Index, string Name, bool Assigned, bool Enabled);

        public RemoteAssignmentsDialog(string peerName, LinkAssignmentChannel channel,
            Func<IReadOnlyList<RemotePeerDeviceInfo>> devices)
        {
            _channel = channel;
            _devices = devices;
            InitializeComponent();
            Title = string.Format(S.Instance.RemoteLink_AssignmentTitle_Format, peerName);
            Loaded += async (_, _) => { _ready = true; await RefreshAsync(true); };
            Closed += (_, _) => { _closed = true; _closing.Cancel(); _closing.Dispose(); };
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            DevicesBox.IsEnabled = !busy;
            SlotsList.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            if (busy) StatusText.Text = S.Instance.RemoteLink_AssignmentBusy;
        }

        private async Task RefreshAsync(bool reloadDevices)
        {
            if (_busy || _closed) return;
            SetBusy(true);
            try
            {
                if (reloadDevices)
                {
                    string selected = (DevicesBox.SelectedItem as RemotePeerDeviceInfo)?.PeerLocalDeviceId;
                    var devices = _devices().ToArray();
                    DevicesBox.ItemsSource = devices;
                    DevicesBox.SelectedItem = devices.FirstOrDefault(d => d.PeerLocalDeviceId == selected) ?? devices.FirstOrDefault();
                }
                if (DevicesBox.SelectedItem is not RemotePeerDeviceInfo device)
                {
                    SlotsList.ItemsSource = null;
                    ProfileText.Text = "";
                    StatusText.Text = S.Instance.RemoteLink_AssignmentNoDevices;
                    return;
                }
                _deviceId = device.PeerLocalDeviceId;
                ShowReply(await _channel.QueryAsync(_deviceId, _closing.Token));
            }
            catch (Exception) { if (!_closed) ShowError(S.Instance.RemoteLink_AssignmentUnavailable); }
            finally { if (!_closed) SetBusy(false); }
        }

        private void ShowError(string message)
        {
            SlotsList.ItemsSource = null;
            ProfileText.Text = "";
            StatusText.Text = message;
        }

        private void ShowReply(LinkAssignmentReply reply)
        {
            if (_closed) return;
            _revision = reply.Revision;
            if (reply.Status is LinkAssignmentStatus.Ok or LinkAssignmentStatus.Stale)
            {
                ProfileText.Text = string.Format(S.Instance.RemoteLink_AssignmentProfile_Format, reply.Profile);
                var slots = reply.Slots ?? Array.Empty<LinkAssignmentSlot>();
                SlotsList.ItemsSource = slots.Select(s => new SlotChoice(s.Index, s.Name, s.Assigned, s.CanAssign || s.Assigned)).ToArray();
                StatusText.Text = reply.Status == LinkAssignmentStatus.Stale ? S.Instance.RemoteLink_AssignmentStale
                    : slots.Length == 0 ? S.Instance.RemoteLink_AssignmentNoSlots : "";
            }
            else ShowError(reply.Status switch
            {
                LinkAssignmentStatus.Denied => S.Instance.RemoteLink_AssignmentDenied,
                LinkAssignmentStatus.TimedOut => S.Instance.RemoteLink_AssignmentTimeout,
                LinkAssignmentStatus.Busy => S.Instance.RemoteLink_AssignmentBusy,
                _ => S.Instance.RemoteLink_AssignmentUnavailable
            });
        }

        private async void AssignmentClicked(object sender, RoutedEventArgs e)
        {
            if (_busy || _closed || sender is not CheckBox check || check.DataContext is not SlotChoice slot) return;
            bool assigned = check.IsChecked == true;
            SetBusy(true);
            try { ShowReply(await _channel.SetAsync(_deviceId, _revision, slot.Index, assigned, _closing.Token)); }
            catch (Exception) { if (!_closed) ShowError(S.Instance.RemoteLink_AssignmentUnavailable); }
            finally { if (!_closed) SetBusy(false); }
        }

        private async void DeviceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && !_busy) await RefreshAsync(false);
        }
        private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync(true);
        private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    }
}
