using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ModernWpf.Controls;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge
{
    /// <summary>
    /// MainWindow code-behind. Wires navigation, creates services, manages
    /// the application lifecycle (engine start/stop on window open/close).
    /// </summary>
    public partial class MainWindow
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly MainViewModel _viewModel;
        private InputService _inputService;
        private SettingsService _settingsService;
        private RecorderService _recorderService;
        private DeviceService _deviceService;
        private Popup _controllerTypePopup;
        private DateTime _popupClosedAt;

        /// <summary>When non-null, the next recording result goes to this mapping's NegSourceDescriptor.</summary>
        private MappingItem _pendingNegMapping;
        /// <summary>Saved positive descriptor while recording the negative direction.</summary>
        private string _savedPosDescriptor;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private System.Windows.Threading.DispatcherTimer _driverStatusTimer;
        private bool _previousViGEmInstalled;
        private bool _previousVJoyInstalled;

        // Drag reorder state for sidebar controller cards.
        private Point _cardDragStartPoint;
        private System.Windows.Controls.Border _cardDragSource;
        private CardDragAdorner _dragAdorner;
        private InsertionLineAdorner _insertionAdorner;
        private System.Windows.Documents.AdornerLayer _dragAdornerLayer;

        public MainWindow()
        {
            InitializeComponent();

            // Custom title bar — make it draggable.
            CustomTitleBar.MouseLeftButtonDown += (_, _) => DragMove();

            // Create root ViewModel.
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Set child DataContexts.
            DashboardPageView.DataContext = _viewModel.Dashboard;
            DevicesPageView.DataContext = _viewModel.Devices;
            SettingsPageView.DataContext = _viewModel.Settings;
            ProfilesPageView.DataContext = _viewModel.Settings;

            // Create services.
            _settingsService = new SettingsService(_viewModel);
            _inputService = new InputService(_viewModel) { SettingsService = _settingsService };
            _recorderService = new RecorderService(_viewModel);
            _deviceService = new DeviceService(_viewModel, _settingsService);

            // Wire driver uninstall guards — lambda queries the ViewModel's Pads for active slot types.
            _viewModel.Settings.HasAnyViGEmSlots = () =>
            {
                for (int i = 0; i < InputManager.MaxPads; i++)
                    if (SettingsManager.SlotCreated[i] &&
                        (_viewModel.Pads[i].OutputType == VirtualControllerType.Xbox360 ||
                         _viewModel.Pads[i].OutputType == VirtualControllerType.DualShock4))
                        return true;
                return false;
            };
            _viewModel.Settings.HasAnyVJoySlots = () =>
            {
                for (int i = 0; i < InputManager.MaxPads; i++)
                    if (SettingsManager.SlotCreated[i] &&
                        _viewModel.Pads[i].OutputType == VirtualControllerType.VJoy)
                        return true;
                return false;
            };
            _viewModel.Settings.HasAnyMidiSlots = () =>
            {
                for (int i = 0; i < InputManager.MaxPads; i++)
                    if (SettingsManager.SlotCreated[i] &&
                        _viewModel.Pads[i].OutputType == VirtualControllerType.Midi)
                        return true;
                return false;
            };
            _viewModel.Settings.HasAnyHidHideDevices = () =>
            {
                var devices = SettingsManager.UserDevices;
                if (devices == null) return false;
                lock (devices.SyncRoot)
                {
                    foreach (var ud in devices.Items)
                        if (ud.HidHideEnabled) return true;
                }
                return false;
            };

            // Wire engine start/stop commands.
            _viewModel.StartEngineRequested += (s, e) => _inputService.Start();
            _viewModel.StopEngineRequested += (s, e) => _inputService.Stop();

            // Wire settings commands.
            _viewModel.Settings.SaveRequested += (s, e) =>
            {
                _settingsService.Save();
                // Refresh default snapshot so future profile reverts use the latest saved state.
                if (SettingsManager.ActiveProfileId == null)
                    _inputService.RefreshDefaultSnapshot();
                // Recalculate input suppression sets after save pushes ViewModel mappings to PadSettings.
                _inputService.ApplyDeviceHiding();
            };
            _settingsService.AutoSaved += (s, e) =>
            {
                if (SettingsManager.ActiveProfileId == null)
                    _inputService.RefreshDefaultSnapshot();
                // Recalculate input suppression sets after save pushes ViewModel mappings to PadSettings.
                _inputService.ApplyDeviceHiding();
            };
            _viewModel.Settings.ReloadRequested += (s, e) => _settingsService.Reload();
            _viewModel.Settings.ResetRequested += (s, e) => _settingsService.ResetToDefaults();
            _viewModel.Settings.OpenSettingsFolderRequested += OnOpenSettingsFolder;
            _viewModel.Settings.ThemeChanged += OnThemeChanged;
            _viewModel.Settings.NewProfileRequested += OnNewProfile;
            _viewModel.Settings.SaveAsProfileRequested += OnSaveAsProfile;
            _viewModel.Settings.DeleteProfileRequested += OnDeleteProfile;
            _viewModel.Settings.EditProfileRequested += OnEditProfile;
            _viewModel.Settings.LoadProfileRequested += OnLoadProfile;
            _viewModel.Settings.RevertToDefaultRequested += OnRevertToDefault;

            // Persist Settings VM changes (theme, polling, checkboxes) and handle login toggle.
            _viewModel.Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.StartAtLogin))
                    Common.StartupHelper.SetStartupEnabled(_viewModel.Settings.StartAtLogin);

                if (e.PropertyName is nameof(SettingsViewModel.SelectedThemeIndex)
                     or nameof(SettingsViewModel.AutoStartEngine)
                     or nameof(SettingsViewModel.MinimizeToTray)
                     or nameof(SettingsViewModel.StartMinimized)
                     or nameof(SettingsViewModel.StartAtLogin)
                     or nameof(SettingsViewModel.EnablePollingOnFocusLoss)
                     or nameof(SettingsViewModel.PollingRateMs)
                     or nameof(SettingsViewModel.EnableInputHiding)
                     or nameof(SettingsViewModel.Use2DControllerView)
                     or nameof(SettingsViewModel.EnableAutoProfileSwitching))
                    _settingsService.MarkDirty();
            };

            // Persist DSU / web controller server settings on change (Dashboard VM).
            _viewModel.Dashboard.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(DashboardViewModel.EnableDsuMotionServer)
                     or nameof(DashboardViewModel.DsuMotionServerPort)
                     or nameof(DashboardViewModel.EnableWebController)
                     or nameof(DashboardViewModel.WebControllerPort))
                    _settingsService.MarkDirty();
            };

            // Wire ViGEm install/uninstall commands.
            _viewModel.Settings.InstallViGEmRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_InstallingViGEm, DriverInstaller.InstallViGEmBus, RefreshViGEmStatus);
            _viewModel.Settings.UninstallViGEmRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_UninstallingViGEm, DriverInstaller.UninstallViGEmBus, RefreshViGEmStatus);

            // Wire HidHide install/uninstall commands.
            _viewModel.Settings.InstallHidHideRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_InstallingHidHide, DriverInstaller.InstallHidHide, RefreshHidHideStatus);
            _viewModel.Settings.UninstallHidHideRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_UninstallingHidHide, DriverInstaller.UninstallHidHide, RefreshHidHideStatus);

            // Wire HidHide whitelist add (file browser).
            _viewModel.Settings.AddWhitelistPathRequested += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Strings.Instance.FileDialog_SelectWhitelist,
                    Filter = Strings.Instance.FileDialog_ExeFilter,
                    CheckFileExists = true
                };
                if (dlg.ShowDialog(this) == true)
                {
                    string path = dlg.FileName;
                    if (!_viewModel.Settings.HidHideWhitelistPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _viewModel.Settings.HidHideWhitelistPaths.Add(path);
                        _viewModel.Settings.RaiseWhitelistChanged();
                    }
                }
            };

            // Wire HidHide whitelist changes → re-apply device hiding.
            _viewModel.Settings.WhitelistChanged += (s, e) =>
            {
                _inputService?.ApplyDeviceHiding();
            };

            // Wire vJoy install/uninstall commands.
            _viewModel.Settings.InstallVJoyRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_InstallingVJoy, DriverInstaller.InstallVJoy, OnVJoyDriverChanged);
            _viewModel.Settings.UninstallVJoyRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_UninstallingVJoy, DriverInstaller.UninstallVJoy, OnVJoyDriverChanged);

            // Wire MIDI Services install/uninstall commands.
            _viewModel.Settings.InstallMidiServicesRequested += async (s, e) =>
            {
                _viewModel.StatusText = Strings.Instance.Status_DownloadingMidi;
                DriverOverlayText.Text = Strings.Instance.Status_DownloadingInstallingMidi;
                DriverOverlay.Visibility = Visibility.Visible;
                try
                {
                    await DriverInstaller.InstallMidiServicesAsync();
                    _viewModel.StatusText = Strings.Instance.Common_Ready;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    _viewModel.StatusText = Strings.Instance.Status_OperationCancelled;
                }
                catch (Exception ex)
                {
                    _viewModel.StatusText = string.Format(Strings.Instance.Status_MidiInstallFailed_Format, ex.Message);
                }
                finally
                {
                    DriverOverlay.Visibility = Visibility.Collapsed;
                    RefreshMidiServicesStatus();
                }
            };
            _viewModel.Settings.UninstallMidiServicesRequested += async (s, e) =>
            {
                // The uninstall guard prevents this when MIDI slots are active, so the
                // SDK runtime won't be loaded in-process. Safe to wait for the uninstaller.
                // Abandon the initializer just in case (e.g. IsAvailable was called elsewhere).
                Common.Input.MidiVirtualController.Shutdown(skipDispose: true);
                await RunDriverOperationAsync(
                    Strings.Instance.Status_UninstallingMidi, DriverInstaller.UninstallMidiServices, RefreshMidiServicesStatus);
            };

            // Wire device service events (assign to slot, hide, etc.).
            _deviceService.WireEvents();

            // Refresh PadPage dropdowns and Devices-page slot buttons after assignment changes.
            _deviceService.DeviceAssignmentChanged += (s, e) =>
            {
                _inputService.RefreshDeviceList();
                _viewModel.Devices.RefreshSlotButtons();
            };

            // Re-apply device hiding when a toggle changes.
            _deviceService.DeviceHidingStateChanged += (s, e) =>
            {
                _inputService.ApplyDeviceHiding();
                _inputService.RefreshMappingDropdowns();
                _viewModel.Settings.RefreshDriverGuards();
            };

            // After assigning a device to a slot, navigate to that controller page.
            _deviceService.NavigateToSlotRequested += (s, slotIndex) => NavigateToSlot(slotIndex);

            // Wire devices page refresh.
            _viewModel.Devices.RefreshRequested += (s, e) =>
            {
                _inputService.RefreshDeviceList();
                _viewModel.StatusText = Strings.Instance.Status_DeviceListRefreshed;
            };

            // Wire test rumble for each pad (both motors, or individual).
            foreach (var pad in _viewModel.Pads)
            {
                pad.TestRumbleRequested += (s, e) =>
                {
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, pvm.SelectedMappedDevice?.InstanceGuid);
                };
                pad.TestLeftMotorRequested += (s, e) =>
                {
                    // Controller preview tab: rumble all devices in the slot (null = no filter).
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, null, true, false);
                };
                pad.TestRightMotorRequested += (s, e) =>
                {
                    // Controller preview tab: rumble all devices in the slot (null = no filter).
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, null, false, true);
                };
            }

            // Wire recorder for each pad's mapping rows.
            // Also listen for CollectionChanged so new mappings (from RebuildMappings) get wired.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;

                // Wire existing mappings.
                foreach (var mapping in pad.Mappings)
                    WireMappingItemEvents(mapping, capturedPad);

                // Re-wire when mappings are rebuilt (OutputType or vJoy config change).
                pad.Mappings.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (MappingItem mi in e.NewItems)
                            WireMappingItemEvents(mi, capturedPad);
                    }
                };

                // Pad setting changes (dead zones, force feedback, etc.) trigger autosave.
                pad.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName is
                        nameof(PadViewModel.LeftDeadZoneX) or nameof(PadViewModel.LeftDeadZoneY) or
                        nameof(PadViewModel.RightDeadZoneX) or nameof(PadViewModel.RightDeadZoneY) or
                        nameof(PadViewModel.LeftAntiDeadZoneX) or nameof(PadViewModel.LeftAntiDeadZoneY) or
                        nameof(PadViewModel.RightAntiDeadZoneX) or nameof(PadViewModel.RightAntiDeadZoneY) or
                        nameof(PadViewModel.LeftLinear) or nameof(PadViewModel.RightLinear) or
                        nameof(PadViewModel.LeftSensitivityCurveX) or nameof(PadViewModel.LeftSensitivityCurveY) or
                        nameof(PadViewModel.RightSensitivityCurveX) or nameof(PadViewModel.RightSensitivityCurveY) or
                        nameof(PadViewModel.LeftTriggerSensitivityCurve) or nameof(PadViewModel.RightTriggerSensitivityCurve) or
                        nameof(PadViewModel.LeftMaxRangeX) or nameof(PadViewModel.LeftMaxRangeY) or
                        nameof(PadViewModel.RightMaxRangeX) or nameof(PadViewModel.RightMaxRangeY) or
                        nameof(PadViewModel.LeftCenterOffsetX) or nameof(PadViewModel.LeftCenterOffsetY) or
                        nameof(PadViewModel.RightCenterOffsetX) or nameof(PadViewModel.RightCenterOffsetY) or
                        nameof(PadViewModel.LeftTriggerDeadZone) or nameof(PadViewModel.RightTriggerDeadZone) or
                        nameof(PadViewModel.LeftTriggerAntiDeadZone) or nameof(PadViewModel.RightTriggerAntiDeadZone) or
                        nameof(PadViewModel.LeftTriggerMaxRange) or nameof(PadViewModel.RightTriggerMaxRange) or
                        nameof(PadViewModel.ForceOverallGain) or nameof(PadViewModel.LeftMotorStrength) or
                        nameof(PadViewModel.RightMotorStrength) or nameof(PadViewModel.SwapMotors) or
                        nameof(PadViewModel.AudioRumbleEnabled) or nameof(PadViewModel.AudioRumbleSensitivity) or
                        nameof(PadViewModel.AudioRumbleCutoffHz) or nameof(PadViewModel.AudioRumbleLeftMotor) or
                        nameof(PadViewModel.AudioRumbleRightMotor) or
                        nameof(PadViewModel.OutputType))
                        _settingsService.MarkDirty();
                };

                // vJoy custom stick/trigger config changes (indices 2+) trigger autosave.
                pad.ConfigItemDirtyCallback = () => _settingsService.MarkDirty();

                // VJoyConfig property changes (preset, counts) trigger autosave.
                pad.VJoyConfig.PropertyChanged += (s, e) => _settingsService.MarkDirty();
            }

            // Recorder completion marks settings dirty + clear flash + advance Map All.
            _recorderService.RecordingCompleted += (s, result) =>
            {
                _settingsService.MarkDirty();
                var activePad = _viewModel.SelectedPad;
                if (activePad == null) return;

                Guid deviceGuid = activePad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                // ── Neg-recording mode: redirect result to NegSourceDescriptor ──
                if (_pendingNegMapping != null)
                {
                    var negMapping = _pendingNegMapping;
                    _pendingNegMapping = null;

                    if (result.Type == MapType.Axis && negMapping.HasNegDirection)
                    {
                        // A full analog axis covers both directions,
                        // so write it to the primary descriptor and clear neg.
                        negMapping.LoadDescriptor(result.Descriptor);
                        negMapping.NegSourceDescriptor = string.Empty;
                        _savedPosDescriptor = null;
                        if (deviceGuid != Guid.Empty)
                            InputService.ResolveDisplayText(negMapping, deviceGuid);
                        negMapping.SyncSelectedInputFromDescriptor();

                        _viewModel.StatusText = string.Format(Strings.Instance.Status_Recorded_Format, negMapping.TargetLabel, negMapping.SourceDisplayText);

                        if (activePad.IsMapAllActive)
                            activePad.OnMapAllItemCompleted();
                        else
                            activePad.CurrentRecordingTarget = null;
                        return;
                    }

                    // Button recorded — write to neg descriptor.
                    // The recorder always writes to SourceDescriptor (line 358 of RecorderService),
                    // so undo that: redirect the value to NegSourceDescriptor instead.
                    negMapping.NegSourceDescriptor = result.Descriptor;
                    bool hadSavedPos = _savedPosDescriptor != null;
                    if (hadSavedPos)
                    {
                        // Came from auto-prompt (pos already recorded) — restore saved positive.
                        negMapping.SourceDescriptor = _savedPosDescriptor;
                        _savedPosDescriptor = null;
                    }
                    else
                    {
                        // First recording for this axis (e.g., Y first phase in Map All).
                        // The recorder contaminated SourceDescriptor — clear it so only
                        // NegSourceDescriptor holds this recording.
                        negMapping.SourceDescriptor = string.Empty;
                    }

                    if (deviceGuid != Guid.Empty)
                    {
                        InputService.ResolveDisplayText(negMapping, deviceGuid);
                        InputService.ResolveNegDisplayText(negMapping, deviceGuid);
                    }
                    negMapping.SyncSelectedInputFromDescriptor();

                    if (!hadSavedPos && negMapping.HasNegDirection && !activePad.IsMapAllActive)
                    {
                        // Came from a neg-quadrant click — now auto-prompt for the positive direction.
                        // (Map All handles the second phase itself via MapAllRecordingNeg.)
                        bool isXAxis = negMapping.TargetSettingName.Contains("AxisX")
                            || negMapping.TargetLabel.EndsWith(" X", StringComparison.Ordinal);
                        string dirHint = isXAxis ? Strings.Instance.Status_DirectionRight : Strings.Instance.Status_DirectionDown;
                        _viewModel.StatusText = string.Format(Strings.Instance.Status_NowMap_Format, negMapping.TargetLabel, dirHint);

                        // Switch to Controller tab so the 3D directional arrow is visible.
                        activePad.SelectedConfigTab = 0;

                        // Update recording target to pos for flash/arrow.
                        activePad.CurrentRecordingTarget = negMapping.TargetSettingName;

                        // Start recording — result will go to SourceDescriptor via normal path.
                        // Neutralize baseline so the previous POV/button press doesn't block detection.
                        _savedPosDescriptor = null;
                        _recorderService.StartRecording(negMapping, activePad.PadIndex, deviceGuid, neutralizeBaseline: true);
                        return;
                    }

                    _viewModel.StatusText = string.Format(Strings.Instance.Status_Recorded_Format, negMapping.TargetLabel, negMapping.SourceDisplayText);

                    if (activePad.IsMapAllActive)
                    {
                        if (!hadSavedPos)
                        {
                            // Y axis first phase came through _pendingNegMapping (neg=up recorded).
                            // Tell Map All a second phase (pos=down) is still needed.
                            activePad.MapAllRecordingNeg = true;
                        }
                        else
                        {
                            // X axis: both pos and neg were recorded in one round
                            // (normal path auto-prompted neg). Clear the flag so
                            // OnMapAllItemCompleted advances to the next mapping.
                            activePad.MapAllRecordingNeg = false;
                        }
                        activePad.OnMapAllItemCompleted();
                    }
                    else
                        activePad.CurrentRecordingTarget = null;
                    return;
                }

                // ── Normal recording ──
                if (deviceGuid != Guid.Empty)
                    InputService.ResolveDisplayText(result.Mapping, deviceGuid);
                result.Mapping.SyncSelectedInputFromDescriptor();

                // If a directional input (button, POV, slider) was recorded for a bidirectional axis,
                // auto-prompt for neg direction (but only if neg isn't already mapped — avoids
                // re-prompting after a neg-quadrant click that already auto-prompted for pos).
                if (result.Type != MapType.Axis && result.Mapping.HasNegDirection
                    && string.IsNullOrEmpty(result.Mapping.NegSourceDescriptor))
                {
                    // Save the positive descriptor before the recorder overwrites it.
                    _savedPosDescriptor = result.Mapping.SourceDescriptor;
                    _pendingNegMapping = result.Mapping;

                    // Neg X = left, Neg Y = up (Y inverted by NegateAxis in Step 3).
                    bool isXAxis2 = result.Mapping.TargetSettingName.Contains("AxisX")
                        || result.Mapping.TargetLabel.EndsWith(" X", StringComparison.Ordinal);
                    string dirHint = isXAxis2 ? Strings.Instance.Status_DirectionLeft : Strings.Instance.Status_DirectionUp;
                    _viewModel.StatusText = string.Format(Strings.Instance.Status_NowMap_Format, result.Mapping.TargetLabel, dirHint);

                    if (activePad.IsMapAllActive)
                    {
                        activePad.MapAllRecordingNeg = true;
                        // Update the Map All overlay prompt to show the correct direction.
                        int idx = activePad.MapAllCurrentIndex;
                        activePad.MapAllPromptText = string.Format(Strings.Instance.Status_MapPrompt_Format, result.Mapping.TargetLabel, dirHint, idx + 1, activePad.Mappings.Count);
                    }

                    // Switch to Controller tab so the 3D directional arrow is visible.
                    activePad.SelectedConfigTab = 0;

                    // Update the recording target to the neg setting for flash/arrow.
                    activePad.CurrentRecordingTarget = result.Mapping.NegSettingName;

                    // Start recording again for the neg direction.
                    // Neutralize baseline so the previous POV/button press doesn't block detection.
                    _recorderService.StartRecording(result.Mapping, activePad.PadIndex, deviceGuid,
                        neutralizeBaseline: true, negRecording: true);
                    return;
                }

                // If a full analog axis was recorded for a bidirectional target, clear neg (axis covers both directions).
                if (result.Mapping.HasNegDirection && result.Type == MapType.Axis)
                {
                    result.Mapping.NegSourceDescriptor = string.Empty;
                }

                _viewModel.StatusText = string.Format(Strings.Instance.Status_Recorded_Format, result.Mapping.TargetLabel, result.Mapping.SourceDisplayText);

                if (activePad.IsMapAllActive)
                    activePad.OnMapAllItemCompleted();
                else
                    activePad.CurrentRecordingTarget = null;
            };

            // Recording timeout clears flash + advances Map All.
            _recorderService.RecordingTimedOut += (s, e) =>
            {
                // If we were waiting for neg, restore the positive descriptor.
                if (_pendingNegMapping != null && _savedPosDescriptor != null)
                    _pendingNegMapping.SourceDescriptor = _savedPosDescriptor;
                _pendingNegMapping = null;
                _savedPosDescriptor = null;

                var activePad = _viewModel.SelectedPad;
                if (activePad != null)
                {
                    if (activePad.IsMapAllActive)
                        activePad.OnMapAllItemCompleted();
                    else
                        activePad.CurrentRecordingTarget = null;
                }
            };

            // Wire click-to-record from controller visual elements.
            PadPageView.ControllerElementRecordRequested += (s, targetName) =>
            {
                var padVm = _viewModel.SelectedPad;
                if (padVm == null) return;

                // Toggle: if already recording this element, cancel.
                if (padVm.CurrentRecordingTarget == targetName)
                {
                    _recorderService.CancelRecording();
                    padVm.CurrentRecordingTarget = null;
                    _pendingNegMapping = null;
                    _savedPosDescriptor = null;
                    return;
                }

                // Check if this is a neg target (e.g., "LeftThumbAxisXNeg").
                bool isNegTarget = targetName.EndsWith("Neg", StringComparison.Ordinal);
                string posTargetName = isNegTarget ? targetName.Substring(0, targetName.Length - 3) : targetName;

                var mapping = padVm.Mappings.FirstOrDefault(m =>
                    string.Equals(m.TargetSettingName, posTargetName, StringComparison.OrdinalIgnoreCase));
                if (mapping == null) return;

                Guid deviceGuid = padVm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                if (isNegTarget)
                    _pendingNegMapping = mapping;

                _recorderService.StartRecording(mapping, padVm.PadIndex, deviceGuid,
                    negRecording: isNegTarget);

                // Only show arrow/flash if recording actually started (device available).
                if (_recorderService.IsRecording)
                    padVm.CurrentRecordingTarget = targetName;
            };

            // Wire Map All events for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;
                pad.MapAllRecordRequested += (s, mapping) =>
                {
                    Guid deviceGuid = capturedPad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                    // Y axes record neg (up in game) first due to NegateAxis inversion.
                    // Pre-set _pendingNegMapping so the recorder result goes to NegSourceDescriptor.
                    // For vJoy custom sticks: label ends with " Y" (e.g. "Stick 1 Y").
                    bool isYAxis = mapping.HasNegDirection
                        && (mapping.TargetSettingName.Contains("AxisY")
                            || mapping.TargetLabel.EndsWith(" Y", StringComparison.Ordinal));
                    bool isYFirstPhase = isYAxis && !capturedPad.MapAllRecordingNeg;
                    if (isYFirstPhase)
                        _pendingNegMapping = mapping;

                    // For non-Y bidirectional axes (X), clear stale neg descriptor so the
                    // auto-prompt for the opposite direction fires after the first button
                    // is recorded (auto-prompt is gated by NegSourceDescriptor being empty).
                    if (mapping.HasNegDirection && !isYAxis)
                        mapping.NegSourceDescriptor = string.Empty;

                    _recorderService.StartRecording(mapping, capturedPad.PadIndex, deviceGuid,
                        negRecording: isYFirstPhase);
                };
                pad.MapAllCancelRequested += (s, e) =>
                    _recorderService.CancelRecording();
            }

            // Wire macro trigger recording for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;

                // Wire existing macros.
                foreach (var macro in pad.Macros)
                {
                    WireMacroRecording(macro, capturedPad.PadIndex);
                    WireMacroDirty(macro);
                }

                // Wire macros added later + mark dirty on add/remove.
                pad.Macros.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (MacroItem macro in e.NewItems)
                        {
                            WireMacroRecording(macro, capturedPad.PadIndex);
                            WireMacroDirty(macro);
                        }
                    }
                    _settingsService.MarkDirty();
                };
            }

            // Wire copy/paste/copy-from for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;
                pad.CopySettingsRequested += (s, e) => OnCopySettings(capturedPad);
                pad.PasteSettingsRequested += (s, e) => OnPasteSettings(capturedPad);
                pad.CopyFromRequested += (s, e) => OnCopyFrom(capturedPad);
            }

            // Build the sidebar navigation items dynamically.
            BuildNavigationItems();

            // Wire Dashboard "Add Controller" to show type-selection popup.
            DashboardPageView.AddControllerRequested += (s, e) =>
            {
                ShowControllerTypePopup(DashboardPageView.AddControllerCardElement, PlacementMode.Bottom);
            };

            // Wire Dashboard delete + toggle events.
            DashboardPageView.DeleteSlotRequested += (s, slotIndex) =>
            {
                // Deferred — DeleteSlot fires DeviceAssignmentChanged → RebuildControllerSection.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_viewModel.SelectedPadIndex == slotIndex)
                        SelectNavItemByTag("Dashboard");

                    _deviceService.DeleteSlot(slotIndex);
                    _viewModel.Devices.RefreshSlotButtons();
                    _inputService.RefreshDeviceList();
                }));
            };

            DashboardPageView.SlotEnabledToggled += (s, args) =>
            {
                _deviceService.SetSlotEnabled(args.SlotIndex, args.IsEnabled);
                // Refresh sidebar so power button updates.
                _viewModel.RefreshNavControllerItems();
            };

            DashboardPageView.EngineToggleRequested += (s, e) =>
            {
                if (_viewModel.IsEngineRunning)
                    _inputService.Stop();
                else
                    _inputService.Start();
                // Force full sidebar card rebuild — engine state affects power icon colors
                // but isn't a NavControllerItemViewModel property, so in-place updates miss it.
                RebuildControllerSection();
            };

            DashboardPageView.SlotTypeChangeRequested += (s, args) =>
            {
                // Re-automap devices before setting OutputType so that when
                // RebuildMappings fires, the PadSetting already has correct mappings.
                SettingsManager.ReAutoMapSlot(args.SlotIndex, args.Type);
                _viewModel.Pads[args.SlotIndex].OutputType = args.Type;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
                _inputService.RefreshDeviceList();
                _viewModel.Devices.RefreshSlotButtons();
            };

            DashboardPageView.SlotSwapRequested += (s, args) =>
            {
                _inputService.SwapSlots(args.PadIndexA, args.PadIndexB);
                _settingsService.MarkDirty();
            };

            DashboardPageView.SlotMoveRequested += (s, args) =>
            {
                _inputService.MoveSlot(args.SourcePadIndex, args.TargetVisualPos);
                _settingsService.MarkDirty();
            };

            DashboardPageView.SlotCardClicked += (s, slotIndex) =>
            {
                NavigateToSlot(slotIndex);
            };

            // Window events.
            Loaded += OnLoaded;
            Closing += OnClosing;
            StateChanged += OnStateChanged;

            // Live language switching: refresh sidebar nav items when culture changes.
            Strings.CultureChanged += OnCultureChanged;

            // ── Early initialization (before window is shown) ──
            // Settings must be loaded before Show() so App.OnStartup can
            // decide whether to show the window at all (start-minimized-to-tray).
            _settingsService.Initialize();

            // Sync StartAtLogin with actual registry state (user may have removed it externally).
            _viewModel.Settings.StartAtLogin = Common.StartupHelper.IsStartupEnabled();

            SetupNotifyIcon();

            // Expose start-minimized state for App.OnStartup.
            ShouldStartMinimized = _viewModel.Settings.StartMinimized;
            ShouldStartMinimizedToTray = _viewModel.Settings.StartMinimized
                && _viewModel.Settings.MinimizeToTray;

            // If starting minimized to tray, make the tray icon visible now.
            if (ShouldStartMinimizedToTray)
                _notifyIcon.Visible = true;

            // Enforce type-group ordering (Xbox 360 > DS4 > vJoy) on startup.
            // Handles backward-compat with old configs that have interleaved types.
            if (_inputService.EnsureTypeGroupOrder(silent: true))
                _settingsService.MarkDirty();

            // Detect drivers early (before sidebar rebuild) so power icons show correct
            // colors even when starting minimized to tray (where OnLoaded never fires).
            RefreshViGEmStatus();
            _previousViGEmInstalled = _viewModel.Dashboard.IsViGEmInstalled;
            RefreshHidHideStatus();
            RefreshVJoyStatus();
            _previousVJoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;
            RefreshMidiServicesStatus();
            StartDriverStatusTimer();

            // Populate sidebar and dashboard with saved slots regardless of engine state,
            // so virtual controllers are visible for configuration even when the engine is off.
            _viewModel.RefreshNavControllerItems();
            RefreshDashboardActiveSlots();

            // vJoy device nodes are created on demand by the engine (CreateVJoyController)
            // when slots become active — same pattern as ViGEm. No pre-creation needed.
            if (_viewModel.Settings.AutoStartEngine)
                _inputService.Start();
        }

        /// <summary>Whether the app should start minimized (to taskbar).</summary>
        public bool ShouldStartMinimized { get; private set; }

        /// <summary>Whether the app should start hidden to the system tray.</summary>
        public bool ShouldStartMinimizedToTray { get; private set; }

        // ─────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────

        private void StartDriverStatusTimer()
        {
            if (_driverStatusTimer != null) return;
            _driverStatusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _driverStatusTimer.Tick += (s, ev) =>
            {
                bool wasViGEmInstalled = _previousViGEmInstalled;
                RefreshViGEmStatus();
                RefreshHidHideStatus();
                RefreshVJoyStatus();
                RefreshMidiServicesStatus();

                bool nowViGEmInstalled = _viewModel.Dashboard.IsViGEmInstalled;
                _previousViGEmInstalled = nowViGEmInstalled;

                // ViGEm installed mid-session: restart engine to recreate ViGEmClient and virtual controllers.
                if (!wasViGEmInstalled && nowViGEmInstalled && _viewModel.IsEngineRunning)
                {
                    _inputService.Stop();
                    _inputService.Start();
                    _viewModel.StatusText = Strings.Instance.Status_ViGEmDetectedRestarted;
                }

                // ViGEm status changed: force full sidebar rebuild for power icon colors.
                if (wasViGEmInstalled != nowViGEmInstalled)
                    RebuildControllerSection();

                // vJoy installed/reinstalled mid-session: reset cached state and restart engine.
                bool wasVJoyInstalled = _previousVJoyInstalled;
                bool nowVJoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;
                _previousVJoyInstalled = nowVJoyInstalled;

                if (wasVJoyInstalled != nowVJoyInstalled)
                {
                    // Always reset cached DLL/registry state on any install status change.
                    PadForge.Common.Input.VJoyVirtualController.ResetState();

                    if (nowVJoyInstalled && _viewModel.IsEngineRunning)
                    {
                        _inputService.Stop(preserveVJoyNodes: true);
                        _inputService.Start();
                        _viewModel.StatusText = Strings.Instance.Status_VJoyDetectedRestarted;
                    }
                }
            };
            _driverStatusTimer.Start();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Driver detection and timer are initialized in the constructor so they
            // work even when starting minimized to tray (where OnLoaded never fires).

            // Populate diagnostic info.
            _viewModel.Settings.ApplicationVersion =
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            _viewModel.Settings.RuntimeVersion = Environment.Version.ToString();

            // Check SDL3.dll availability.
            try
            {
                var sdlVersion = SDL3.SDL.SDL_Linked_Version();
                _viewModel.Settings.SdlVersion = $"SDL {sdlVersion.major}.{sdlVersion.minor}.{sdlVersion.patch}";
            }
            catch (DllNotFoundException)
            {
                _viewModel.Settings.SdlVersion = Strings.Instance.Status_SDL3NotFound;
                _viewModel.StatusText = Strings.Instance.Status_SDL3NotFoundDetail;
            }
            catch
            {
                _viewModel.Settings.SdlVersion = Strings.Instance.Common_Unknown;
            }

            // Select the first nav item.
            if (NavView.MenuItems.Count > 0)
                NavView.SelectedItem = NavView.MenuItems[0];
        }

        private bool _shutdownComplete;

        private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_shutdownComplete)
                return; // Second close call after async shutdown — let it through.

            // Cancel the close so the window stays visible during shutdown.
            e.Cancel = true;

            // Show shutdown overlay and ensure window is visible.
            ShutdownOverlay.Visibility = System.Windows.Visibility.Visible;
            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                try { Show(); } catch (InvalidOperationException) { }
                WindowState = WindowState.Normal;
            }

            // Save settings synchronously (fast, UI-bound data).
            if (_settingsService.IsDirty)
                _settingsService.Save();

            // Stop driver status polling.
            _driverStatusTimer?.Stop();
            _driverStatusTimer = null;

            // Dispose tray icon and helper window.
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_trayMenuHost != null)
            {
                _trayMenuHost.Close();
                _trayMenuHost = null;
            }

            // Unwire device service.
            _deviceService?.UnwireEvents();

            // Run the slow shutdown work (controller disposal, vJoy node removal) off the UI thread.
            await System.Threading.Tasks.Task.Run(() =>
            {
                _recorderService?.Dispose();
                _inputService?.Dispose();
                Common.Input.MidiVirtualController.Shutdown();
            });

            // All done — close for real.
            _shutdownComplete = true;
            Close();
        }

        // ─────────────────────────────────────────────
        //  Navigation
        // ─────────────────────────────────────────────

        // SVG path data for controller type icons — shared via ControllerIcons static class.
        private const string XboxSvgPath = Common.ControllerIcons.XboxSvgPath;
        private const string DS4SvgPath = Common.ControllerIcons.DS4SvgPath;
        private const string VJoySvgPath = Common.ControllerIcons.VJoySvgPath;


        /// <summary>Static nav items whose Content must be refreshed on culture change.</summary>
        private NavigationViewItem _navDashboard, _navProfiles, _navDevices;

        /// <summary>Index in NavView.MenuItems where the first controller entry goes (after Dashboard, Profiles, Devices).</summary>
        private const int ControllerInsertIndex = 3;

        /// <summary>Re-entrancy guard for <see cref="RebuildControllerSection"/>.</summary>
        private bool _rebuildingControllerSection;

        /// <summary>Tracks PropertyChanged subscriptions so they can be unsubscribed on rebuild.</summary>
        private readonly List<(System.ComponentModel.INotifyPropertyChanged Source, System.ComponentModel.PropertyChangedEventHandler Handler)> _navItemHandlers = new();

        /// <summary>
        /// Programmatically builds the NavigationView menu items.
        /// Static items: Dashboard, separators, Devices, Profiles.
        /// Dynamic items: controller entries + "Add" button, rebuilt when NavControllerItems changes.
        /// </summary>
        private void BuildNavigationItems()
        {
            // Card drag-reorder: NavView-level handlers for threshold, movement, and drop.
            NavView.PreviewMouseMove += OnNavViewDragMove;
            NavView.PreviewMouseLeftButtonUp += OnNavViewDragEnd;
            NavView.PreviewKeyDown += OnNavViewDragKeyDown;

            // ItemInvoked fires even when SelectsOnInvoked=false (used for AddController).
            NavView.ItemInvoked += NavView_ItemInvoked;

            NavView.MenuItems.Clear();

            // Dashboard.
            _navDashboard = new NavigationViewItem
            {
                Content = Strings.Instance.Dashboard_Title,
                Tag = "Dashboard",
                Icon = new FontIcon { Glyph = "\uF404" }
            };
            NavView.MenuItems.Add(_navDashboard);

            // Profiles.
            _navProfiles = new NavigationViewItem
            {
                Tag = "Profiles",
                Icon = new FontIcon { Glyph = "\uE8F1" },
                Content = Strings.Instance.Profiles_Title
            };
            NavView.MenuItems.Add(_navProfiles);

            // Devices.
            _navDevices = new NavigationViewItem
            {
                Content = Strings.Instance.Devices_Title,
                Tag = "Devices",
                Icon = new FontIcon { Glyph = "\uE772" }
            };
            NavView.MenuItems.Add(_navDevices);

            // Controller entries (initially none — populated dynamically).
            RebuildControllerSection();

            // Subscribe to the single "done" event instead of CollectionChanged.
            // Deferred to Background priority so the NavigationView's internal
            // ItemsRepeater completes ALL pending layout/render passes before we
            // tear down and rebuild MenuItems. Normal priority can still interleave
            // with layout, causing the ItemsRepeater's cached index to go stale and
            // crash in ViewManager.GetElementFromElementFactory (MeasureOverride).
            _viewModel.NavControllerItemsRefreshed += (s, e) =>
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        RebuildControllerSection();
                        _viewModel.Devices.RefreshSlotButtons();
                        _inputService.RefreshProfileTopology();
                    }));

            // Subscribe to OutputType changes on each pad to refresh sidebar
            // and profile topology badges (type change doesn't add/remove slots
            // so NavControllerItemsRefreshed won't fire — call topology directly).
            foreach (var pad in _viewModel.Pads)
            {
                pad.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PadViewModel.OutputType))
                    {
                        _viewModel.RefreshNavControllerItems();
                        _inputService.RefreshProfileTopology();
                    }
                };
            }
        }

        private void OnCultureChanged()
        {
            if (_navDashboard != null) _navDashboard.Content = Strings.Instance.Dashboard_Title;
            if (_navProfiles != null) _navProfiles.Content = Strings.Instance.Profiles_Title;
            if (_navDevices != null) _navDevices.Content = Strings.Instance.Devices_Title;

            // Footer items — use x:Name references directly.
            NavSettings.Content = Strings.Instance.Settings_Title;
            NavAbout.Content = Strings.Instance.About_Title;

            // Refresh "Add Controller" and controller card labels by rebuilding the dynamic section.
            RebuildControllerSection();

            // Persist the language change.
            _settingsService.MarkDirty();
        }

        /// <summary>
        /// Rebuilds only the controller section of the sidebar (between the two separators).
        /// Preserves Dashboard, Devices, Profiles, and footer items.
        ///
        /// Uses a re-entrancy guard because <see cref="RefreshNavControllerItems"/> fires
        /// CollectionChanged once for Clear() and once per Add(), each of which would
        /// re-trigger this method. The guard ensures only one rebuild occurs.
        ///
        /// Saves and restores the NavView selection tag so that removing/re-adding
        /// Devices or Profiles (which are rebuilt each time) doesn't break the
        /// current navigation state.
        /// </summary>
        private void RebuildControllerSection()
        {
            if (_rebuildingControllerSection)
                return;

            // Save current selection tag before tearing down items.
            string selectedTag = (NavView.SelectedItem as NavigationViewItem)?.Tag?.ToString();

            _rebuildingControllerSection = true;
            try
            {
                // Unsubscribe old PropertyChanged handlers to prevent leaks.
                foreach (var (source, handler) in _navItemHandlers)
                    source.PropertyChanged -= handler;
                _navItemHandlers.Clear();

                // Remove everything from ControllerInsertIndex onward.
                // This fires NavView_SelectionChanged for intermediate states —
                // the flag suppresses those events (see guard in that handler).
                while (NavView.MenuItems.Count > ControllerInsertIndex)
                    NavView.MenuItems.RemoveAt(ControllerInsertIndex);

                // Add controller entries for each active slot.
                foreach (var navItem in _viewModel.NavControllerItems)
                {
                    var menuItem = CreateControllerNavItem(navItem);
                    NavView.MenuItems.Add(menuItem);

                    var capturedMenuItem = menuItem;
                    var capturedNavItem = navItem;
                    System.ComponentModel.PropertyChangedEventHandler handler = (s, e) =>
                    {
                        if (e.PropertyName is nameof(NavControllerItemViewModel.InstanceLabel)
                            or nameof(NavControllerItemViewModel.IconKey)
                            or nameof(NavControllerItemViewModel.IsEnabled)
                            or nameof(NavControllerItemViewModel.SlotNumber)
                            or nameof(NavControllerItemViewModel.ConnectedDeviceCount)
                            or nameof(NavControllerItemViewModel.IsInitializing))
                        {
                            UpdateControllerNavItemContent(capturedMenuItem, capturedNavItem);
                        }
                    };
                    navItem.PropertyChanged += handler;
                    _navItemHandlers.Add((navItem, handler));
                }

                // "Add Controller" button (visible if any controller type has remaining capacity).
                if (HasAnyControllerTypeCapacity())
                {
                    var addItem = new NavigationViewItem
                    {
                        Tag = "AddController",
                        Icon = new FontIcon { Glyph = "\uE710" }, // + icon
                        Content = Strings.Instance.Main_AddController,
                        SelectsOnInvoked = false
                    };
                    NavView.MenuItems.Add(addItem);
                }

            }
            finally
            {
                _rebuildingControllerSection = false;
            }

            // Restore selection AFTER the guard is cleared so
            // NavView_SelectionChanged processes it normally.
            if (!string.IsNullOrEmpty(selectedTag))
            {
                NavigationViewItem match = null;
                NavigationViewItem fallback = null;
                foreach (var mi in NavView.MenuItems)
                {
                    if (mi is NavigationViewItem nvi)
                    {
                        if (nvi.Tag?.ToString() == selectedTag)
                        {
                            match = nvi;
                            break;
                        }
                        if (nvi.Tag?.ToString() == "Dashboard")
                            fallback = nvi;
                    }
                }
                NavView.SelectedItem = match ?? fallback;
            }

            // Refresh uninstall button guards (disabled when slots of that type exist).
            _viewModel.Settings.RefreshDriverGuards();
        }

        /// <summary>
        /// Creates a NavigationViewItem with two-line content for a virtual controller slot.
        /// </summary>
        private NavigationViewItem CreateControllerNavItem(NavControllerItemViewModel navItem)
        {
            var menuItem = new NavigationViewItem { Tag = navItem.Tag };
            System.Windows.Automation.AutomationProperties.SetName(menuItem, navItem.Tag);
            UpdateControllerNavItemContent(menuItem, navItem);
            return menuItem;
        }

        // Power button icon: E7E8 = PowerButton glyph in Segoe MDL2 Assets.
        private const string PowerGlyph = "\uE7E8";

        /// <summary>
        /// Updates the Content and Icon of a controller NavigationViewItem.
        /// Compact card with rounded border: [Power] [Gamepad] #N | [Xbox][PS] #N [X]
        /// </summary>
        private void UpdateControllerNavItemContent(NavigationViewItem menuItem, NavControllerItemViewModel navItem)
        {
            string iconKey = navItem.IconKey;
            bool isXbox = iconKey == "XboxControllerIcon";
            bool isDS4 = iconKey == "DS4ControllerIcon";
            bool isVJoy = iconKey == "VJoyControllerIcon";
            bool isMidi = iconKey == "MidiControllerIcon";
            bool isKbm = iconKey == "KeyboardMouseControllerIcon";

            var row = new System.Windows.Controls.DockPanel();

            // Delete button — docked right so it stays at the far end of the card.
            var deleteBtn = new System.Windows.Controls.Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Background = System.Windows.Media.Brushes.Transparent,
                ToolTip = Strings.Instance.Main_DeleteVC,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.5
            };
            deleteBtn.Click += OnSidebarDeleteSlot;
            System.Windows.Controls.DockPanel.SetDock(deleteBtn, System.Windows.Controls.Dock.Right);
            row.Children.Add(deleteBtn);

            // Power button (green = enabled + active, yellow = enabled + warning, red = disabled,
            // flashing green = initializing).
            var outputType = _viewModel.Pads[navItem.PadIndex].OutputType;
            System.Windows.Media.SolidColorBrush powerColor;
            string powerTooltip;
            bool isInitializing = navItem.IsInitializing;
            if (!navItem.IsEnabled)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36)); // red
                powerTooltip = Strings.Instance.Common_Disabled;
                isInitializing = false;
            }
            else if (isInitializing)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // green
                powerTooltip = Strings.Instance.Main_Initializing;
            }
            else if (!_viewModel.IsEngineRunning)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = Strings.Instance.Main_EngineStopped;
            }
            else if (!_viewModel.Dashboard.IsViGEmInstalled && outputType != VirtualControllerType.VJoy && outputType != VirtualControllerType.Midi && outputType != VirtualControllerType.KeyboardMouse)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = Strings.Instance.Main_ViGEmNotInstalled;
            }
            else if (navItem.ConnectedDeviceCount == 0)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = Strings.Instance.Main_AwaitingControllers;
            }
            else
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // green
                powerTooltip = Strings.Instance.Main_Active;
            }

            var powerTextBlock = new System.Windows.Controls.TextBlock
            {
                Text = PowerGlyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = powerColor
            };

            // Apply flashing opacity animation when initializing.
            if (isInitializing)
            {
                var flashAnimation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.15,
                    Duration = new Duration(TimeSpan.FromMilliseconds(500)),
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                powerTextBlock.BeginAnimation(System.Windows.UIElement.OpacityProperty, flashAnimation);
            }

            var powerBtn = new System.Windows.Controls.Button
            {
                Content = powerTextBlock,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                ToolTip = powerTooltip,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            powerBtn.Click += OnSidebarPowerToggle;
            row.Children.Add(powerBtn);

            // Gamepad icon + global slot number.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "\uE7FC",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"{navItem.SlotNumber}",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
                Width = 16,
                TextAlignment = TextAlignment.Center
            });

            // Separator.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "|",
                FontSize = 12,
                Opacity = 0.3,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            });

            // Type-switch buttons: Xbox / DS4 / vJoy / MIDI — shown for all cards.
            // Xbox type button — use SetResourceReference for theme-aware Fill.
            var xboxPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(XboxSvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            xboxPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            var xboxBtn = new System.Windows.Controls.Button
            {
                Content = xboxPath,
                ToolTip = Strings.Instance.ControllerType_Xbox360,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isXbox ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            xboxBtn.Click += OnSidebarTypeXbox;
            row.Children.Add(xboxBtn);

            // PS type button — use SetResourceReference for theme-aware Fill.
            var ds4Path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(DS4SvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            ds4Path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            var ds4Btn = new System.Windows.Controls.Button
            {
                Content = ds4Path,
                ToolTip = Strings.Instance.ControllerType_DualShock4,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isDS4 ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            ds4Btn.Click += OnSidebarTypeDS4;
            row.Children.Add(ds4Btn);

            // vJoy type button — use SetResourceReference for theme-aware Fill.
            var vjoyPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(VJoySvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            vjoyPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            var vjoyBtn = new System.Windows.Controls.Button
            {
                Content = vjoyPath,
                ToolTip = Strings.Instance.ControllerType_DirectInput,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isVJoy ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            vjoyBtn.Click += OnSidebarTypeVJoy;
            row.Children.Add(vjoyBtn);

            // Keyboard+Mouse type button — MDL2 glyph E961.
            var kbmBtn = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "\uE961",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13
                },
                ToolTip = Strings.Instance.ControllerType_KeyboardMouse,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isKbm ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            kbmBtn.Click += OnSidebarTypeKeyboardMouse;
            row.Children.Add(kbmBtn);

            // MIDI type button — MDL2 glyph (music note).
            var midiBtn = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "\uE8D6",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13
                },
                ToolTip = Strings.Instance.ControllerType_MIDI,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isMidi ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            midiBtn.Click += OnSidebarTypeMidi;
            row.Children.Add(midiBtn);

            // Per-type instance label.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = navItem.InstanceLabel,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
                Width = 12,
                TextAlignment = TextAlignment.Center
            });

            // Wrap in a rounded card border.
            var card = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(2),
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                Child = row,
                Tag = navItem.PadIndex
            };
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");

            // Drag reordering — mouse-down recorded here, threshold + movement tracked at NavView level.
            card.PreviewMouseLeftButtonDown += OnCardDragStart;

            // Cross-panel: accept device drops from Devices page.
            card.AllowDrop = true;
            card.Drop += OnSidebarCardDrop;
            card.DragOver += OnSidebarCardDragOver;
            card.DragLeave += OnSidebarCardDragLeave;

            menuItem.Content = card;

            // No left gutter icon — the power button inside the card shows state.
            menuItem.Icon = null;
        }

        /// <summary>Handles sidebar power toggle button click.</summary>
        private void OnSidebarPowerToggle(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                bool newState = !SettingsManager.SlotEnabled[padIndex];
                _deviceService.SetSlotEnabled(padIndex, newState);
                // Refresh nav items so IsEnabled updates and content rebuilds.
                _viewModel.RefreshNavControllerItems();
            }
        }

        /// <summary>Handles sidebar Xbox type button click.</summary>
        private void OnSidebarTypeXbox(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Xbox360);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Xbox360;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar DualShock 4 type button click.</summary>
        private void OnSidebarTypeDS4(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.DualShock4);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.DualShock4;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar vJoy type button click.</summary>
        private void OnSidebarTypeVJoy(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                // Device nodes are created on demand by the engine (CreateVJoyController)
                // when the slot becomes active — same pattern as ViGEm.
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.VJoy);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.VJoy;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar Keyboard+Mouse type button click.</summary>
        private void OnSidebarTypeKeyboardMouse(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.KeyboardMouse);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.KeyboardMouse;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar MIDI type button click.</summary>
        private void OnSidebarTypeMidi(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Midi);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Midi;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>
        /// Handles the sidebar delete button click for a virtual controller slot.
        /// </summary>
        private void OnSidebarDeleteSlot(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // Prevent NavigationView selection change.
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int slotIndex)
            {
                // Deferred to next dispatcher frame — DeleteSlot() fires
                // DeviceAssignmentChanged → RebuildControllerSection() which removes
                // the NavViewItem whose child button we're inside of right now.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Navigate away if we're viewing the slot being deleted.
                    if (_viewModel.SelectedPadIndex == slotIndex)
                        SelectNavItemByTag("Dashboard");

                    _deviceService.DeleteSlot(slotIndex);
                }));
            }
        }

        // ─────────────────────────────────────────────
        //  Sidebar card drag reordering (manual mouse capture)
        // ─────────────────────────────────────────────

        private bool _isDraggingCard;
        private int _dragSourcePadIndex;
        private int _dragSourceVisualPos;
        private int _dragDropIndex;
        private bool _dragIsSwapMode;       // true = swap with card under cursor; false = insert between cards
        private int _dragSwapTargetPadIndex = -1;
        private System.Windows.Controls.Border _dragSwapHighlight; // card currently highlighted for swap

        /// <summary>Records the mouse-down point on a card border (per-card handler).</summary>
        private void OnCardDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var card = sender as System.Windows.Controls.Border;

            // Don't initiate drag from button clicks inside the card.
            if (e.OriginalSource is DependencyObject source && IsInsideButton(source, card))
            {
                _cardDragSource = null;
                return;
            }

            _cardDragStartPoint = e.GetPosition(null);
            _cardDragSource = card;
        }

        /// <summary>NavView-level: threshold check when idle, position tracking while dragging.</summary>
        private void OnNavViewDragMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                if (_isDraggingCard) EndCardDrag(cancel: true);
                _cardDragSource = null;
                return;
            }

            if (_isDraggingCard)
            {
                UpdateDragPosition(e.GetPosition(NavView));
                return;
            }

            if (_cardDragSource == null) return;

            Vector diff = _cardDragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                BeginCardDrag();
            }
        }

        /// <summary>NavView-level: ends drag on mouse-up.</summary>
        private void OnNavViewDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDraggingCard)
            {
                EndCardDrag(cancel: false);
                e.Handled = true;
            }
            _cardDragSource = null;
        }

        /// <summary>NavView-level: cancels drag on Escape.</summary>
        private void OnNavViewDragKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_isDraggingCard && e.Key == System.Windows.Input.Key.Escape)
            {
                EndCardDrag(cancel: true);
                e.Handled = true;
            }
        }

        private void BeginCardDrag()
        {
            if (_cardDragSource == null || _cardDragSource.Tag is not int padIndex) return;

            var cards = GetControllerCardBounds();
            if (cards.Count == 0) return;

            _dragSourcePadIndex = padIndex;
            _dragSourceVisualPos = cards.FindIndex(c => c.PadIndex == padIndex);
            if (_dragSourceVisualPos < 0) return;

            _dragAdornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(NavView);
            if (_dragAdornerLayer == null) return;

            // Snapshot the card visual before hiding.
            var snapshot = CaptureCardVisual(_cardDragSource);
            if (snapshot == null) return;

            _dragAdorner = new CardDragAdorner(NavView, snapshot, _cardDragSource.RenderSize);
            _dragAdornerLayer.Add(_dragAdorner);

            var accentBrush = (System.Windows.Media.Brush)FindResource("SystemControlHighlightAccentBrush");
            _insertionAdorner = new InsertionLineAdorner(NavView, accentBrush);
            _dragAdornerLayer.Add(_insertionAdorner);

            _cardDragSource.Opacity = 0;
            _isDraggingCard = true;
            _dragDropIndex = _dragSourceVisualPos;
            System.Windows.Input.Mouse.Capture(NavView, System.Windows.Input.CaptureMode.SubTree);
        }

        private void UpdateDragPosition(Point navViewPos)
        {
            _dragAdorner?.UpdatePosition(navViewPos);

            var cards = GetControllerCardBounds();
            if (cards.Count == 0) return;

            // If cursor has moved outside the card column horizontally (e.g. dragged
            // into the main content area for cross-panel assignment), don't trigger
            // any swap/insert reordering — just clear indicators.
            {
                double colLeft = cards[0].Left;
                double colRight = cards[0].Left + cards[0].Width;
                if (navViewPos.X < colLeft - 20 || navViewPos.X > colRight + 20)
                {
                    _dragIsSwapMode = false;
                    _dragDropIndex = -1;
                    _dragSwapTargetPadIndex = -1;
                    ClearSwapHighlight();
                    _insertionAdorner?.Update(0, 0, 0, false);
                    return;
                }
            }

            // Edge zone = 25% of card height at top/bottom → insert between cards.
            // Middle zone = 50% of card height → swap with that card.
            const double edgeFraction = 0.25;

            bool isSwap = false;
            int swapCardIndex = -1;
            int dropIndex = cards.Count;

            for (int i = 0; i < cards.Count; i++)
            {
                double height = cards[i].Bottom - cards[i].Top;
                double edgeSize = height * edgeFraction;
                double topEdge = cards[i].Top + edgeSize;
                double bottomEdge = cards[i].Bottom - edgeSize;

                if (navViewPos.Y >= topEdge && navViewPos.Y <= bottomEdge)
                {
                    // Cursor is in the middle zone — swap mode (skip if it's the source card).
                    if (i != _dragSourceVisualPos)
                    {
                        isSwap = true;
                        swapCardIndex = i;
                    }
                    break;
                }
                else if (navViewPos.Y < topEdge)
                {
                    // Cursor is above this card's middle zone — insert before this card.
                    dropIndex = i;
                    break;
                }
                // else cursor is below this card's middle zone — continue to next card
            }

            // ── Type-group validation ──
            // Block cross-type reordering: only allow swap/insert within the same type group.
            var sourceType = _viewModel.Pads[_dragSourcePadIndex].OutputType;

            if (isSwap)
            {
                // Reject swap if target is a different type.
                if (_viewModel.Pads[cards[swapCardIndex].PadIndex].OutputType != sourceType)
                    isSwap = false;
            }

            if (!isSwap)
            {
                // Reject insertion outside the source's type group.
                if (!IsInsertionInSameTypeGroup(dropIndex, sourceType, cards))
                    dropIndex = -1;
            }

            _dragIsSwapMode = isSwap;

            if (isSwap)
            {
                // Swap mode: highlight target card, hide insertion line.
                _dragDropIndex = -1;
                _dragSwapTargetPadIndex = cards[swapCardIndex].PadIndex;
                _insertionAdorner?.Update(0, 0, 0, false);
                SetSwapHighlight(cards[swapCardIndex].PadIndex, true);
            }
            else
            {
                // Insert mode: show insertion line, clear swap highlight.
                _dragDropIndex = dropIndex;
                _dragSwapTargetPadIndex = -1;
                ClearSwapHighlight();

                bool noMove = dropIndex < 0 || dropIndex == _dragSourceVisualPos || dropIndex == _dragSourceVisualPos + 1;
                if (noMove || _insertionAdorner == null)
                {
                    _insertionAdorner?.Update(0, 0, 0, false);
                }
                else
                {
                    double lineY;
                    if (dropIndex == 0)
                        lineY = cards[0].Top - 1;
                    else if (dropIndex >= cards.Count)
                        lineY = cards[cards.Count - 1].Bottom + 1;
                    else
                        lineY = (cards[dropIndex - 1].Bottom + cards[dropIndex].Top) / 2;

                    _insertionAdorner.Update(lineY, cards[0].Left, cards[0].Width, true);
                }
            }
        }

        /// <summary>
        /// Returns true if the insertion point at <paramref name="insertionVisualPos"/>
        /// is adjacent to at least one card of the same type as <paramref name="sourceType"/>.
        /// </summary>
        private bool IsInsertionInSameTypeGroup(int insertionVisualPos, VirtualControllerType sourceType, List<CardBounds> cards)
        {
            if (insertionVisualPos < 0) return false;
            // Check the card above the insertion point.
            if (insertionVisualPos > 0)
            {
                int abovePad = cards[insertionVisualPos - 1].PadIndex;
                if (_viewModel.Pads[abovePad].OutputType == sourceType)
                    return true;
            }
            // Check the card below the insertion point.
            if (insertionVisualPos < cards.Count)
            {
                int belowPad = cards[insertionVisualPos].PadIndex;
                if (_viewModel.Pads[belowPad].OutputType == sourceType)
                    return true;
            }
            return false;
        }

        private void SetSwapHighlight(int padIndex, bool highlight)
        {
            // Clear previous highlight if it's a different card.
            if (_dragSwapHighlight != null && (_dragSwapHighlight.Tag is int prevPad) && prevPad != padIndex)
                ClearSwapHighlight();

            if (!highlight) { ClearSwapHighlight(); return; }

            // Find the card Border for this padIndex.
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi &&
                    nvi.Content is System.Windows.Controls.Border card &&
                    card.Tag is int idx && idx == padIndex)
                {
                    var accent = FindResource("SystemControlHighlightAccentBrush") as System.Windows.Media.Brush
                              ?? System.Windows.Media.Brushes.DodgerBlue;
                    card.BorderBrush = accent;
                    _dragSwapHighlight = card;
                    break;
                }
            }
        }

        private void ClearSwapHighlight()
        {
            if (_dragSwapHighlight == null) return;
            _dragSwapHighlight.BorderBrush = System.Windows.Media.Brushes.Transparent;
            _dragSwapHighlight = null;
        }

        private void EndCardDrag(bool cancel)
        {
            System.Windows.Input.Mouse.Capture(null);
            _isDraggingCard = false;

            if (_cardDragSource != null)
                _cardDragSource.Opacity = 1;

            ClearSwapHighlight();

            // Remove adorners.
            if (_dragAdornerLayer != null)
            {
                if (_dragAdorner != null) _dragAdornerLayer.Remove(_dragAdorner);
                if (_insertionAdorner != null) _dragAdornerLayer.Remove(_insertionAdorner);
            }
            _dragAdorner = null;
            _insertionAdorner = null;
            _dragAdornerLayer = null;

            if (!cancel)
            {
                bool handled = false;
                if (_dragIsSwapMode && _dragSwapTargetPadIndex >= 0)
                {
                    // Direct swap between two cards.
                    int srcPad = _dragSourcePadIndex;
                    int tgtPad = _dragSwapTargetPadIndex;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _inputService.SwapSlots(srcPad, tgtPad);
                        _settingsService.MarkDirty();
                    }));
                    handled = true;
                }
                else if (!_dragIsSwapMode && _dragDropIndex >= 0)
                {
                    // Insert mode: convert dropIndex to target visual position.
                    int targetVisualPos;
                    if (_dragDropIndex <= _dragSourceVisualPos)
                        targetVisualPos = _dragDropIndex;
                    else if (_dragDropIndex <= _dragSourceVisualPos + 1)
                        targetVisualPos = _dragSourceVisualPos; // no move
                    else
                        targetVisualPos = _dragDropIndex - 1;

                    if (targetVisualPos != _dragSourceVisualPos)
                    {
                        int srcPad = _dragSourcePadIndex;
                        int tgtPos = targetVisualPos;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _inputService.MoveSlot(srcPad, tgtPos);
                            _settingsService.MarkDirty();
                        }));
                        handled = true;
                    }
                }

                // Cross-panel: sidebar card dropped over a Devices-page device card.
                if (!handled)
                    TryAssignDeviceFromSidebarDrop(_dragSourcePadIndex);
            }

            _cardDragSource = null;
        }

        // ── Helpers ──

        private record struct CardBounds(int PadIndex, double Left, double Top, double Bottom, double Width);

        private List<CardBounds> GetControllerCardBounds()
        {
            var result = new List<CardBounds>();
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi &&
                    nvi.Content is System.Windows.Controls.Border card &&
                    card.Tag is int padIndex)
                {
                    try
                    {
                        var transform = card.TransformToVisual(NavView);
                        var topLeft = transform.Transform(new Point(0, 0));
                        result.Add(new CardBounds(padIndex, topLeft.X, topLeft.Y,
                            topLeft.Y + card.ActualHeight, card.ActualWidth));
                    }
                    catch { /* not in visual tree */ }
                }
            }
            return result;
        }

        private static System.Windows.Media.ImageSource CaptureCardVisual(System.Windows.Controls.Border card)
        {
            if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return null;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(card);
            int w = (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(card);
            return rtb;
        }

        private static bool IsInsideButton(DependencyObject source, DependencyObject boundary)
        {
            var current = source;
            while (current != null && current != boundary)
            {
                if (current is System.Windows.Controls.Button) return true;
                current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        // ── Cross-panel drag assignment ──

        /// <summary>
        /// When a sidebar controller card is dropped over a Devices-page device card,
        /// assign that device to the controller slot.
        /// </summary>
        private void TryAssignDeviceFromSidebarDrop(int padIndex)
        {
            if (DevicesPageView.Visibility != Visibility.Visible) return;

            var screenPos = System.Windows.Forms.Control.MousePosition;
            var wpfPos = DevicesPageView.PointFromScreen(new Point(screenPos.X, screenPos.Y));

            // Hit-test the Devices page to find a device card Border.
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(DevicesPageView, wpfPos);
            if (hit?.VisualHit == null) return;

            // Walk up from hit element to find a card Border whose DataContext is DeviceRowViewModel.
            DependencyObject current = hit.VisualHit;
            while (current != null)
            {
                if (current is FrameworkElement fe &&
                    fe.DataContext is ViewModels.DeviceRowViewModel device)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                        _deviceService.AssignDeviceToSlot(device.InstanceGuid, padIndex)));
                    return;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
        }

        /// <summary>
        /// When a Devices-page device card is dropped on a sidebar controller card,
        /// assign that device to the controller slot.
        /// </summary>
        private void OnSidebarCardDrop(object sender, DragEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border card) return;
            if (card.Tag is not int padIndex) return;

            if (e.Data.GetDataPresent("DeviceInstanceGuid"))
            {
                var guid = (Guid)e.Data.GetData("DeviceInstanceGuid");
                Dispatcher.BeginInvoke(new Action(() =>
                    _deviceService.AssignDeviceToSlot(guid, padIndex)));
                e.Handled = true;
            }
        }

        private void OnSidebarCardDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("DeviceInstanceGuid"))
            {
                e.Effects = DragDropEffects.Link;
                e.Handled = true;

                // Highlight the card.
                if (sender is System.Windows.Controls.Border card && card.Tag is int padIndex)
                    SetSwapHighlight(padIndex, true);
            }
        }

        private void OnSidebarCardDragLeave(object sender, DragEventArgs e)
        {
            ClearSwapHighlight();
        }

        // ── Adorners ──

        /// <summary>Renders a bitmap snapshot of the dragged card following the cursor.</summary>
        private class CardDragAdorner : System.Windows.Documents.Adorner
        {
            private readonly System.Windows.Media.ImageBrush _brush;
            private readonly Size _size;
            private Point _position;

            public CardDragAdorner(UIElement adornedElement, System.Windows.Media.ImageSource snapshot, Size cardSize)
                : base(adornedElement)
            {
                _brush = new System.Windows.Media.ImageBrush(snapshot);
                _size = cardSize;
                IsHitTestVisible = false;
            }

            public void UpdatePosition(Point pos)
            {
                _position = pos;
                InvalidateVisual();
            }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                dc.DrawRectangle(_brush, null,
                    new Rect(
                        _position.X - _size.Width / 2,
                        _position.Y - _size.Height / 2,
                        _size.Width, _size.Height));
            }
        }

        /// <summary>Draws a horizontal accent line at the insertion point between cards.</summary>
        private class InsertionLineAdorner : System.Windows.Documents.Adorner
        {
            private readonly System.Windows.Media.Brush _brush;
            private double _y, _x, _width;
            private bool _visible;

            public InsertionLineAdorner(UIElement adornedElement, System.Windows.Media.Brush accentBrush)
                : base(adornedElement)
            {
                _brush = accentBrush;
                IsHitTestVisible = false;
            }

            public void Update(double y, double x, double width, bool visible)
            {
                _y = y; _x = x; _width = width; _visible = visible;
                InvalidateVisual();
            }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                if (!_visible) return;
                dc.DrawRectangle(_brush, null, new Rect(_x, _y - 1, _width, 3));
            }
        }

        /// <summary>
        /// Programmatically navigates to the Devices page.
        /// </summary>
        private void NavigateToDevices()
        {
            SelectNavItemByTag("Devices");
        }

        /// <summary>
        /// Shows a popup anchored to the given element with Xbox 360 and DS4 controller type buttons.
        /// Clicking a button creates a new slot of that type and navigates to it.
        /// </summary>
        /// <summary>
        /// Returns true if at least one virtual controller type has remaining capacity.
        /// </summary>
        private bool HasAnyControllerTypeCapacity()
        {
            int xboxCount = 0, ds4Count = 0, vjoyCount = 0, midiCount = 0, kbmCount = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                switch (_viewModel.Pads[i].OutputType)
                {
                    case VirtualControllerType.Xbox360: xboxCount++; break;
                    case VirtualControllerType.DualShock4: ds4Count++; break;
                    case VirtualControllerType.VJoy: vjoyCount++; break;
                    case VirtualControllerType.Midi: midiCount++; break;
                    case VirtualControllerType.KeyboardMouse: kbmCount++; break;
                }
            }
            return xboxCount < SettingsManager.MaxXbox360Slots
                || ds4Count < SettingsManager.MaxDS4Slots
                || vjoyCount < SettingsManager.MaxVJoySlots
                || (DriverInstaller.IsMidiServicesInstalled() && midiCount < SettingsManager.MaxMidiSlots)
                || kbmCount < SettingsManager.MaxKeyboardMouseSlots;
        }

        private void ShowControllerTypePopup(UIElement anchor, PlacementMode placement = PlacementMode.Right)
        {
            // If the popup is already open, close it instead of opening a duplicate.
            if (_controllerTypePopup != null && _controllerTypePopup.IsOpen)
            {
                _controllerTypePopup.IsOpen = false;
                _controllerTypePopup = null;
                return;
            }

            // StaysOpen=false closes the popup when the anchor is clicked, then the
            // click handler fires and would immediately reopen it. Suppress reopening
            // if the popup was just dismissed within the same click cycle.
            if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 300)
                return;

            var popup = new Popup
            {
                StaysOpen = false,
                Placement = placement,
                PlacementTarget = anchor,
                AllowsTransparency = true
            };
            popup.Closed += (s, e) =>
            {
                _controllerTypePopup = null;
                _popupClosedAt = DateTime.UtcNow;
            };
            _controllerTypePopup = popup;

            // Center the popup horizontally below the anchor when using Bottom placement.
            if (placement == PlacementMode.Bottom && anchor is FrameworkElement fe)
            {
                popup.Opened += (s, e) =>
                {
                    if (popup.Child is FrameworkElement popupContent)
                    {
                        popupContent.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                        double popupWidth = popupContent.DesiredSize.Width;
                        double anchorWidth = fe.ActualWidth;
                        popup.HorizontalOffset = (anchorWidth - popupWidth) / 2;
                    }
                };
            }

            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    Opacity = 0.3,
                    ShadowDepth = 2
                }
            };
            border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PopupBackgroundBrush");

            var stack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            // Count existing slots by type for per-type capacity check.
            int xboxCount = 0, ds4Count = 0, vjoyCount = 0, midiCount = 0, kbmCount = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                switch (_viewModel.Pads[i].OutputType)
                {
                    case VirtualControllerType.Xbox360: xboxCount++; break;
                    case VirtualControllerType.DualShock4: ds4Count++; break;
                    case VirtualControllerType.VJoy: vjoyCount++; break;
                    case VirtualControllerType.Midi: midiCount++; break;
                    case VirtualControllerType.KeyboardMouse: kbmCount++; break;
                }
            }

            // Xbox 360 button — theme-aware icon fill.
            var xboxPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(XboxSvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            xboxPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            bool xboxAtCapacity = xboxCount >= SettingsManager.MaxXbox360Slots;
            bool vigemInstalled = _viewModel.Dashboard.IsViGEmInstalled;
            bool xboxDisabled = xboxAtCapacity || !vigemInstalled;
            var xboxBtn = new System.Windows.Controls.Button
            {
                Content = xboxPopupPath,
                ToolTip = !vigemInstalled ? Strings.Instance.Main_Xbox360_ViGEmNotInstalled
                        : xboxAtCapacity ? string.Format(Strings.Instance.Main_Xbox360_Max_Format, SettingsManager.MaxXbox360Slots)
                        : Strings.Instance.ControllerType_Xbox360,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !xboxDisabled,
                Opacity = xboxDisabled ? 0.35 : 1.0
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(xboxBtn, "AddXbox360Btn");
            xboxBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Xbox360);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.Xbox360);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(xboxBtn);

            // DS4 button — theme-aware icon fill.
            var ds4PopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(DS4SvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            ds4PopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            bool ds4AtCapacity = ds4Count >= SettingsManager.MaxDS4Slots;
            bool ds4Disabled = ds4AtCapacity || !vigemInstalled;
            var ds4Btn = new System.Windows.Controls.Button
            {
                Content = ds4PopupPath,
                ToolTip = !vigemInstalled ? Strings.Instance.Main_DS4_ViGEmNotInstalled
                        : ds4AtCapacity ? string.Format(Strings.Instance.Main_DS4_Max_Format, SettingsManager.MaxDS4Slots)
                        : Strings.Instance.ControllerType_DualShock4,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !ds4Disabled,
                Opacity = ds4Disabled ? 0.35 : 1.0
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(ds4Btn, "AddDS4Btn");
            ds4Btn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.DualShock4);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.DualShock4);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(ds4Btn);

            // vJoy button — theme-aware icon fill.
            var vjoyPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(VJoySvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            vjoyPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            bool vjoyAtCapacity = vjoyCount >= SettingsManager.MaxVJoySlots;
            bool vjoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;
            bool vjoyDisabled = vjoyAtCapacity || !vjoyInstalled;
            var vjoyBtn = new System.Windows.Controls.Button
            {
                Content = vjoyPopupPath,
                ToolTip = !vjoyInstalled ? Strings.Instance.Main_DI_DriverNotInstalled
                        : vjoyAtCapacity ? string.Format(Strings.Instance.Main_DI_Max_Format, SettingsManager.MaxVJoySlots)
                        : Strings.Instance.ControllerType_DirectInput,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !vjoyDisabled,
                Opacity = vjoyDisabled ? 0.35 : 1.0
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(vjoyBtn, "AddVJoyBtn");
            vjoyBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;

                // Device nodes are created on demand by the engine (CreateVJoyController)
                // when the slot becomes active — same pattern as ViGEm.
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.VJoy);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.VJoy);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(vjoyBtn);

            // Keyboard+Mouse button — MDL2 glyph E961.
            var kbmPopupIcon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE961",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            bool kbmAtCapacity = kbmCount >= SettingsManager.MaxKeyboardMouseSlots;
            var kbmPopupBtn = new System.Windows.Controls.Button
            {
                Content = kbmPopupIcon,
                ToolTip = kbmAtCapacity ? string.Format(Strings.Instance.Main_KBM_Max_Format, SettingsManager.MaxKeyboardMouseSlots)
                        : Strings.Instance.ControllerType_KeyboardMouse,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !kbmAtCapacity,
                Opacity = kbmAtCapacity ? 0.35 : 1.0
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(kbmPopupBtn, "AddKeyboardMouseBtn");
            kbmPopupBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.KeyboardMouse);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.KeyboardMouse);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(kbmPopupBtn);

            // MIDI button — theme-aware icon fill.
            var midiPopupIcon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE8D6",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            bool midiAvailable = DriverInstaller.IsMidiServicesInstalled();
            bool midiAtCapacity = midiCount >= SettingsManager.MaxMidiSlots;
            bool midiDisabled = !midiAvailable || midiAtCapacity;
            string midiTooltip = !midiAvailable ? Strings.Instance.Main_MIDI_RequiresMidiServices
                               : midiAtCapacity ? string.Format(Strings.Instance.Main_MIDI_Max_Format, SettingsManager.MaxMidiSlots)
                               : Strings.Instance.ControllerType_MIDI;
            var midiBtn = new System.Windows.Controls.Button
            {
                Content = midiPopupIcon,
                ToolTip = midiTooltip,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !midiDisabled,
                Opacity = midiDisabled ? 0.35 : 1.0
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(midiBtn, "AddMidiBtn");
            midiBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Midi);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.Midi);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(midiBtn);

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }

        /// <summary>
        /// Programmatically navigates to a controller slot page (e.g., "Pad1").
        /// </summary>
        private void NavigateToSlot(int slotIndex)
        {
            SelectNavItemByTag($"Pad{slotIndex + 1}");
        }

        /// <summary>
        /// Returns the last created slot index of the given type, or -1 if none.
        /// Used after EnsureTypeGroupOrder to navigate to a newly created slot
        /// whose index may have shifted during re-sorting.
        /// </summary>
        private int FindLastSlotOfType(VirtualControllerType type)
        {
            int last = -1;
            for (int i = 0; i < InputManager.MaxPads; i++)
                if (SettingsManager.SlotCreated[i] && _viewModel.Pads[i].OutputType == type)
                    last = i;
            return last;
        }

        /// <summary>
        /// Selects a NavigationViewItem by its Tag string.
        /// </summary>
        private void SelectNavItemByTag(string tag)
        {
            foreach (var mi in NavView.MenuItems)
            {
                if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = nvi;
                    return;
                }
            }

            foreach (var mi in NavView.FooterMenuItems)
            {
                if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = nvi;
                    return;
                }
            }
        }

        /// <summary>
        /// Handles clicks on non-selectable NavigationView items (e.g. "Add Controller").
        /// SelectsOnInvoked=false prevents the blue indicator from moving to these items,
        /// but ItemInvoked still fires so we can show the popup.
        /// </summary>
        private void NavView_ItemInvoked(NavigationView sender,
            NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is NavigationViewItem nvi
                && nvi.Tag?.ToString() == "AddController")
            {
                ShowControllerTypePopup(nvi);
            }
        }

        private static T FindVisualChildByType<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match && predicate(match))
                    return match;
                var result = FindVisualChildByType(child, predicate);
                if (result != null)
                    return result;
            }
            return null;
        }

        private static T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && fe.Name == name)
                    return fe;
                var result = FindVisualChild<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void NavView_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            // Skip intermediate selection events fired while RebuildControllerSection
            // is tearing down and re-adding items. The rebuild restores the correct
            // selection after the guard flag is cleared.
            if (_rebuildingControllerSection)
                return;

            string tag;

            if (args.IsSettingsSelected)
            {
                tag = "Settings";
            }
            else if (args.SelectedItem is NavigationViewItem item)
            {
                tag = item.Tag?.ToString() ?? "Dashboard";
            }
            else
            {
                return;
            }

            // Update ViewModel navigation state.
            _viewModel.SelectedNavTag = tag;

            // Swap visible page.
            DashboardPageView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            DevicesPageView.Visibility = tag == "Devices" ? Visibility.Visible : Visibility.Collapsed;
            ProfilesPageView.Visibility = tag == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPageView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            AboutPageView.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

            bool isPad = tag.StartsWith("Pad") && tag.Length >= 4 && int.TryParse(tag.Substring(3), out _);
            PadPageView.Visibility = isPad ? Visibility.Visible : Visibility.Collapsed;

            // Update PadPage DataContext to the selected pad.
            if (isPad)
            {
                var padVm = _viewModel.SelectedPad;
                if (padVm != null)
                    PadPageView.DataContext = padVm;
            }

            // Notify InputService which pages are visible (for optimization).
            _inputService.IsDevicesPageVisible = tag == "Devices";
            _inputService.IsPadPageVisible = isPad;
        }

        // ─────────────────────────────────────────────
        //  Settings handlers
        // ─────────────────────────────────────────────

        private void OnOpenSettingsFolder(object sender, EventArgs e)
        {
            string folder = System.IO.Path.GetDirectoryName(_settingsService.SettingsFilePath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }

        private void OnNewProfile(object sender, EventArgs e)
        {
            var dialog = new Views.ProfileDialog { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            string name = dialog.ProfileName;
            string exePaths = string.Join("|", dialog.ExecutablePaths);

            // Create an empty shell profile — no VCs, no device assignments.
            var profile = new ProfileData
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                ExecutableNames = exePaths,
                Entries = Array.Empty<ProfileEntry>(),
                PadSettings = Array.Empty<PadSetting>(),
                SlotCreated = new bool[InputManager.MaxPads],
                SlotEnabled = new bool[InputManager.MaxPads],
                SlotControllerTypes = new int[InputManager.MaxPads],
            };

            SettingsManager.Profiles.Add(profile);

            var listItem = new ViewModels.ProfileListItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Executables = FormatExePaths(exePaths),
            };
            SettingsService.UpdateTopologyCounts(listItem, profile.SlotCreated, profile.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);

            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileCreatedEmpty_Format, name);
        }

        private void OnSaveAsProfile(object sender, EventArgs e)
        {
            var dialog = new Views.ProfileDialog { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            string name = dialog.ProfileName;
            // Store full paths pipe-separated.
            string exePaths = string.Join("|", dialog.ExecutablePaths);

            // Snapshot current settings into a new profile.
            var snapshot = _inputService.SnapshotCurrentProfile();
            snapshot.Id = Guid.NewGuid().ToString("N");
            snapshot.Name = name.Trim();
            snapshot.ExecutableNames = exePaths;

            SettingsManager.Profiles.Add(snapshot);

            var listItem = new ViewModels.ProfileListItem
            {
                Id = snapshot.Id,
                Name = snapshot.Name,
                Executables = FormatExePaths(exePaths),
            };
            SettingsService.UpdateTopologyCounts(listItem, snapshot.SlotCreated, snapshot.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);

            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileCreated_Format, name);
        }

        /// <summary>
        /// Formats pipe-separated full paths into a display string showing just file names.
        /// </summary>
        private static string FormatExePaths(string pipeSeparatedPaths)
        {
            if (string.IsNullOrEmpty(pipeSeparatedPaths))
                return string.Empty;

            var parts = pipeSeparatedPaths.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var names = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                names[i] = System.IO.Path.GetFileName(parts[i]);
            return string.Join(", ", names);
        }

        private void OnDeleteProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            SettingsManager.Profiles.RemoveAll(p => p.Id == selected.Id);

            _viewModel.Settings.ProfileItems.Remove(selected);
            _viewModel.Settings.SelectedProfile = null;

            if (SettingsManager.ActiveProfileId == selected.Id)
            {
                SettingsManager.ActiveProfileId = null;
                _viewModel.Settings.ActiveProfileInfo = Strings.Instance.Common_Default;
                // Restore the default profile state so the deleted profile's
                // topology doesn't persist and overwrite the default snapshot.
                _inputService.ApplyDefaultProfile();
            }

            _settingsService.MarkDirty();
            _inputService.RefreshProfileTopology();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileDeleted_Format, selected.Name);
        }

        private void OnEditProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
            if (profile == null) return;

            var exePaths = string.IsNullOrEmpty(profile.ExecutableNames)
                ? Array.Empty<string>()
                : profile.ExecutableNames.Split('|', StringSplitOptions.RemoveEmptyEntries);

            var dialog = new Views.ProfileDialog { Owner = this };
            dialog.LoadForEdit(profile.Name, exePaths);

            if (dialog.ShowDialog() != true)
                return;

            string newName = dialog.ProfileName;
            string newExePaths = string.Join("|", dialog.ExecutablePaths);

            profile.Name = newName;
            profile.ExecutableNames = newExePaths;

            selected.Name = newName;
            selected.Executables = FormatExePaths(newExePaths);

            // Update active profile label if this profile is currently active.
            if (SettingsManager.ActiveProfileId == profile.Id)
                _viewModel.Settings.ActiveProfileInfo = newName;

            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileUpdated_Format, newName);
        }

        private void OnLoadProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            // Loading the Default profile is equivalent to reverting.
            if (selected.IsDefault)
            {
                OnRevertToDefault(sender, e);
                return;
            }

            var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
            if (profile == null) return;

            // Already on this profile — nothing to do.
            if (SettingsManager.ActiveProfileId == profile.Id)
                return;

            // Save outgoing profile state before switching.
            _inputService.SaveActiveProfileState();

            // Set ActiveProfileId BEFORE ApplyProfile so that
            // RefreshActiveProfileTopologyLabel updates the correct profile.
            SettingsManager.ActiveProfileId = profile.Id;
            _viewModel.Settings.ActiveProfileInfo = profile.Name;
            _inputService.ApplyProfile(profile);
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileLoaded_Format, profile.Name);
        }

        private void OnRevertToDefault(object sender, EventArgs e)
        {
            if (SettingsManager.ActiveProfileId == null)
                return;

            // Save outgoing profile state before reverting.
            _inputService.SaveActiveProfileState();

            // Set ActiveProfileId BEFORE ApplyProfile so that
            // RefreshActiveProfileTopologyLabel updates the correct profile.
            SettingsManager.ActiveProfileId = null;
            _viewModel.Settings.ActiveProfileInfo = Strings.Instance.Common_Default;
            _inputService.ApplyDefaultProfile();
            _viewModel.StatusText = Strings.Instance.Status_ProfileRevertedDefault;
        }

        /// <summary>
        /// Wires StartRecordingRequested, StopRecordingRequested, and PropertyChanged
        /// for a single MappingItem. Called both on initial setup and when Mappings
        /// are rebuilt (OutputType change, vJoy config change).
        /// </summary>
        private void WireMappingItemEvents(MappingItem mapping, PadViewModel capturedPad)
        {
            mapping.StartRecordingRequested += (s, e) =>
            {
                if (s is MappingItem mi)
                {
                    Guid deviceGuid = capturedPad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                    // Y axes: record neg (up in game) first due to NegateAxis inversion.
                    // For standard gamepad: TargetSettingName contains "AxisY".
                    // For vJoy custom sticks: TargetSettingName is "VJoyAxisN" — check label for "Y".
                    bool isYAxis = mi.HasNegDirection
                        && (mi.TargetSettingName.Contains("AxisY")
                            || mi.TargetLabel.EndsWith(" Y", StringComparison.Ordinal));
                    if (isYAxis)
                        _pendingNegMapping = mi;

                    _recorderService.StartRecording(mi, capturedPad.PadIndex, deviceGuid,
                        negRecording: isYAxis);

                    // Only show arrow/flash if recording actually started (device available).
                    if (_recorderService.IsRecording)
                        capturedPad.CurrentRecordingTarget = isYAxis ? mi.NegSettingName : mi.TargetSettingName;
                }
            };
            mapping.StopRecordingRequested += (s, e) =>
            {
                _recorderService.CancelRecording();
                capturedPad.CurrentRecordingTarget = null;
            };

            // Mapping descriptor changes (inversion, half-axis, source) trigger autosave.
            mapping.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(MappingItem.SourceDescriptor)
                    or nameof(MappingItem.NegSourceDescriptor)
                    or nameof(MappingItem.IsInverted)
                    or nameof(MappingItem.IsHalfAxis)
                    or nameof(MappingItem.MappingDeadZone))
                    _settingsService.MarkDirty();
            };
        }

        private void WireMacroRecording(MacroItem macro, int padIndex)
        {
            macro.RecordTriggerRequested += (s, e) =>
            {
                if (s is MacroItem mi)
                {
                    if (mi.IsRecordingTrigger)
                        _inputService.StartMacroTriggerRecording(mi, padIndex);
                    else
                        _inputService.StopMacroTriggerRecording();
                }
            };
        }

        /// <summary>
        /// Wires a macro and its actions to trigger auto-save on any property change.
        /// </summary>
        private void WireMacroDirty(MacroItem macro)
        {
            macro.PropertyChanged += (s, e) => _settingsService.MarkDirty();
            foreach (var action in macro.Actions)
                action.PropertyChanged += (s, e) => _settingsService.MarkDirty();
            macro.Actions.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (MacroAction action in e.NewItems)
                        action.PropertyChanged += (s2, e2) => _settingsService.MarkDirty();
                _settingsService.MarkDirty();
            };
        }

        private void OnThemeChanged(object sender, int themeIndex)
        {
            ModernWpf.ThemeManager.Current.ApplicationTheme = themeIndex switch
            {
                1 => ModernWpf.ApplicationTheme.Light,
                2 => ModernWpf.ApplicationTheme.Dark,
                _ => null // System default
            };
        }

        // ─────────────────────────────────────────────
        //  System tray
        // ─────────────────────────────────────────────

        private void SetupNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = Strings.Instance.Common_PadForge;

            // Load icon from the running executable.
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);

            // Use WPF ContextMenu for ModernWPF-themed tray menu (no WinForms ContextMenuStrip).
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                    Dispatcher.BeginInvoke(() => ShowTrayContextMenu());
            };

            // Double-click to restore.
            _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        /// <summary>Invisible helper window that keeps ModernWPF styles available for the tray context menu
        /// even when the main window is hidden.</summary>
        private Window _trayMenuHost;

        private void ShowTrayContextMenu()
        {
            // Ensure the invisible host window exists so the context menu inherits ModernWPF styles.
            if (_trayMenuHost == null)
            {
                _trayMenuHost = new Window
                {
                    Width = 0, Height = 0,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    AllowsTransparency = true,
                    Opacity = 0,
                };
                _trayMenuHost.Show();
            }

            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = _trayMenuHost,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            };

            var showItem = new System.Windows.Controls.MenuItem
            {
                Header = Strings.Instance.Main_ShowPadForge,
                FontWeight = FontWeights.SemiBold,
            };
            showItem.Click += (s, e) => RestoreFromTray();
            menu.Items.Add(showItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem
            {
                Header = "Exit",
            };
            exitItem.Click += (s, e) => { _notifyIcon.Visible = false; Close(); };
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
            {
                Hide();
                _notifyIcon.Visible = true;
            }
        }

        private bool _isRestoring;

        private void RestoreFromTray()
        {
            if (_isRestoring || IsVisible) return;
            _isRestoring = true;
            try
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                _notifyIcon.Visible = false;
            }
            finally { _isRestoring = false; }
        }

        // ─────────────────────────────────────────────
        //  Copy / Paste / Copy From
        // ─────────────────────────────────────────────

        private void OnCopySettings(PadViewModel padVm)
        {
            var ps = _inputService.GetCurrentPadSetting(padVm.PadIndex);
            if (ps == null)
            {
                _viewModel.StatusText = Strings.Instance.Status_NoDeviceToCopyFrom;
                return;
            }

            try
            {
                var copyOutputType = padVm.OutputType;
                bool copyIsCustomVJoy = copyOutputType == VirtualControllerType.VJoy
                    && !padVm.VJoyConfig.IsGamepadPreset;
                Clipboard.SetText(ps.ToJson(copyOutputType, copyIsCustomVJoy));
                _viewModel.StatusText = Strings.Instance.Status_SettingsCopied;
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = string.Format(Strings.Instance.Status_CopyFailed_Format, ex.Message);
            }
        }

        private void OnPasteSettings(PadViewModel padVm)
        {
            try
            {
                string json = Clipboard.GetText();
                var ps = PadSetting.FromJson(json,
                    out VirtualControllerType srcType, out bool srcIsCustomVJoy);
                if (ps == null)
                {
                    _viewModel.StatusText = Strings.Instance.Status_InvalidClipboard;
                    return;
                }

                var targetType = padVm.OutputType;
                bool targetIsCustomVJoy = targetType == VirtualControllerType.VJoy
                    && !padVm.VJoyConfig.IsGamepadPreset;

                _inputService.ApplyPadSettingToCurrentDeviceTranslated(
                    padVm.PadIndex, ps,
                    srcType, srcIsCustomVJoy,
                    targetType, targetIsCustomVJoy);
                _settingsService.MarkDirty();
                _viewModel.StatusText = Strings.Instance.Status_SettingsPasted;
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = string.Format(Strings.Instance.Status_PasteFailed_Format, ex.Message);
            }
        }

        private void OnCopyFrom(PadViewModel padVm)
        {
            // Flush all pad UIs to storage so source PadSettings reflect current state.
            _inputService.FlushAllPadViewModels();

            // Build list of all devices that have configured settings.
            var entries = new List<CopyFromDialog.DeviceEntry>();
            var settings = SettingsManager.UserSettings?.Items;
            if (settings != null)
            {
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    foreach (var us in settings)
                    {
                        // Skip the same device+slot combination (allow same device on other slots).
                        if (padVm.SelectedMappedDevice != null &&
                            us.InstanceGuid == padVm.SelectedMappedDevice.InstanceGuid &&
                            us.MapTo == padVm.PadIndex)
                            continue;

                        var ps = us.GetPadSetting();
                        if (ps == null || !ps.HasAnyMapping)
                            continue;

                        // Get the real device name from UserDevice (InstanceName on
                        // UserSetting is often empty).
                        var ud = SettingsManager.FindDeviceByInstanceGuid(us.InstanceGuid);
                        string name = ud?.InstanceName;
                        if (string.IsNullOrEmpty(name))
                            name = ud?.ProductName;
                        if (string.IsNullOrEmpty(name))
                            name = us.InstanceGuid.ToString();

                        string slot = us.MapTo >= 0 && us.MapTo < InputManager.MaxPads
                            ? string.Format(Strings.Instance.Status_VirtualController_Format, us.MapTo + 1, $"{us.InstanceGuid:D}")
                            : string.Format(Strings.Instance.Status_Unmapped_Format, $"{us.InstanceGuid:D}");

                        // Determine layout type from the slot's output type.
                        var outputType = VirtualControllerType.Xbox360;
                        bool isCustomVJoy = false;
                        if (us.MapTo >= 0 && us.MapTo < _viewModel.Pads.Count)
                        {
                            var srcPad = _viewModel.Pads[us.MapTo];
                            outputType = srcPad.OutputType;
                            isCustomVJoy = outputType == VirtualControllerType.VJoy
                                && !srcPad.VJoyConfig.IsGamepadPreset;
                        }

                        string layoutLabel = $"({MappingTranslation.GetLayoutLabel(outputType, isCustomVJoy)})";

                        entries.Add(new CopyFromDialog.DeviceEntry
                        {
                            Name = name,
                            SlotLabel = slot,
                            LayoutLabel = layoutLabel,
                            InstanceGuid = us.InstanceGuid,
                            PadSetting = ps,
                            OutputType = outputType,
                            IsCustomVJoy = isCustomVJoy
                        });
                    }
                }
            }

            if (entries.Count == 0)
            {
                _viewModel.StatusText = Strings.Instance.Status_NoOtherDevices;
                return;
            }

            var dialog = new CopyFromDialog(entries) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedEntry != null)
            {
                var srcEntry = dialog.SelectedEntry;
                var targetOutputType = padVm.OutputType;
                bool targetIsCustomVJoy = targetOutputType == VirtualControllerType.VJoy
                    && !padVm.VJoyConfig.IsGamepadPreset;

                _inputService.ApplyPadSettingToCurrentDeviceTranslated(
                    padVm.PadIndex, srcEntry.PadSetting,
                    srcEntry.OutputType, srcEntry.IsCustomVJoy,
                    targetOutputType, targetIsCustomVJoy);
                _settingsService.MarkDirty();
                _viewModel.StatusText = Strings.Instance.Status_SettingsCopiedFromDevice;
            }
        }

        // ─────────────────────────────────────────────
        //  Driver install/uninstall helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Runs a driver install/uninstall operation on a background thread,
        /// then refreshes the driver status on the UI thread.
        /// </summary>
        private async Task RunDriverOperationAsync(string statusMessage, Action operation, Action refreshStatus)
        {
            _viewModel.StatusText = statusMessage;
            DriverOverlayText.Text = statusMessage;
            DriverOverlay.Visibility = Visibility.Visible;
            try
            {
                await Task.Run(operation);
                _viewModel.StatusText = Strings.Instance.Common_Ready;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User declined UAC prompt.
                _viewModel.StatusText = Strings.Instance.Status_OperationCancelled;
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = string.Format(Strings.Instance.Status_DriverOperationFailed_Format, ex.Message);
            }
            finally
            {
                DriverOverlay.Visibility = Visibility.Collapsed;
            }
            refreshStatus();
        }

        /// <summary>
        /// Rebuilds the dashboard SlotSummaries from SettingsManager state.
        /// Used at startup and when slots change while the engine is off.
        /// </summary>
        private void RefreshDashboardActiveSlots()
        {
            var activeSlots = new System.Collections.Generic.List<int>();
            int xboxCount = 0, ds4Count = 0, vjoyCount = 0, midiCount = 0, kbmCount = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (SettingsManager.SlotCreated[i])
                {
                    activeSlots.Add(i);
                    switch (_viewModel.Pads[i].OutputType)
                    {
                        case VirtualControllerType.Xbox360: xboxCount++; break;
                        case VirtualControllerType.DualShock4: ds4Count++; break;
                        case VirtualControllerType.VJoy: vjoyCount++; break;
                        case VirtualControllerType.Midi: midiCount++; break;
                        case VirtualControllerType.KeyboardMouse: kbmCount++; break;
                    }
                }
            }
            bool canAddMore = xboxCount < SettingsManager.MaxXbox360Slots
                           || ds4Count < SettingsManager.MaxDS4Slots
                           || vjoyCount < SettingsManager.MaxVJoySlots
                           || midiCount < SettingsManager.MaxMidiSlots
                           || kbmCount < SettingsManager.MaxKeyboardMouseSlots;
            _viewModel.Dashboard.RefreshActiveSlots(activeSlots, canAddMore);
        }

        private void RefreshViGEmStatus()
        {
            try
            {
                bool installed = PadForge.Common.Input.InputManager.CheckViGEmInstalled();
                _viewModel.Settings.IsViGEmInstalled = installed;
                _viewModel.Dashboard.IsViGEmInstalled = installed;

                var version = DriverInstaller.GetViGEmVersion();
                _viewModel.Settings.ViGEmVersion = version ?? string.Empty;
                _viewModel.Dashboard.ViGEmVersion = version ?? string.Empty;

                if (!installed)
                    _viewModel.StatusText = Strings.Instance.Status_ViGEmNotDetected;
            }
            catch (Exception ex)
            {
                _viewModel.Settings.IsViGEmInstalled = false;
                _viewModel.Dashboard.IsViGEmInstalled = false;
                _viewModel.StatusText = string.Format(Strings.Instance.Status_ViGEmCheckFailed_Format, ex.Message);
            }
        }

        private void RefreshHidHideStatus()
        {
            try
            {
                bool installed = DriverInstaller.IsHidHideInstalled();
                _viewModel.Settings.IsHidHideInstalled = installed;
                _viewModel.Dashboard.IsHidHideInstalled = installed;
                _viewModel.Settings.HidHideVersion = DriverInstaller.GetHidHideVersion() ?? string.Empty;
            }
            catch
            {
                _viewModel.Settings.IsHidHideInstalled = false;
                _viewModel.Dashboard.IsHidHideInstalled = false;
            }
        }

        private void RefreshVJoyStatus()
        {
            try
            {
                bool installed = DriverInstaller.IsVJoyInstalled();
                _viewModel.Settings.IsVJoyInstalled = installed;
                _viewModel.Dashboard.IsVJoyInstalled = installed;
                _viewModel.Settings.VJoyVersion = DriverInstaller.GetVJoyVersion() ?? string.Empty;
            }
            catch
            {
                _viewModel.Settings.IsVJoyInstalled = false;
                _viewModel.Dashboard.IsVJoyInstalled = false;
            }
        }

        private void RefreshMidiServicesStatus()
        {
            try
            {
                bool installed = DriverInstaller.IsMidiServicesInstalled();
                _viewModel.Settings.IsMidiServicesInstalled = installed;
                _viewModel.Dashboard.IsMidiServicesInstalled = installed;
                _viewModel.Settings.MidiServicesVersion = installed ? "Windows MIDI Services" : string.Empty;
            }
            catch
            {
                _viewModel.Settings.IsMidiServicesInstalled = false;
                _viewModel.Dashboard.IsMidiServicesInstalled = false;
            }
        }

        /// <summary>
        /// Called after vJoy install/uninstall via PadForge's own buttons.
        /// Resets cached vJoy state and restarts the engine so the new driver
        /// is picked up immediately without requiring a PadForge relaunch.
        /// </summary>
        private void OnVJoyDriverChanged()
        {
            RefreshVJoyStatus();
            _previousVJoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;

            PadForge.Common.Input.VJoyVirtualController.ResetState();

            if (_viewModel.IsEngineRunning)
            {
                // Preserve vJoy device nodes during restart so the DLL's internal
                // device handles stay valid. Nodes are disabled (not removed) during
                // Stop, then re-enabled by EnsureDevicesAvailable when vJoy slots
                // become active — same pattern as "delete last vJoy + re-add".
                _inputService.Stop(preserveVJoyNodes: true);
                _inputService.Start();
                _viewModel.StatusText = _viewModel.Dashboard.IsVJoyInstalled
                    ? Strings.Instance.Status_VJoyInstalledRestarted
                    : Strings.Instance.Status_VJoyRemovedRestarted;
            }
        }
    }
}
