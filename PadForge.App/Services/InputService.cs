using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Bridges the background <see cref="InputManager"/> engine with WPF ViewModels.
    /// 
    /// Responsibilities:
    ///   - Creates and owns the InputManager instance
    ///   - Runs a 30Hz DispatcherTimer on the UI thread
    ///   - Reads combined gamepad states from the engine and pushes them to PadViewModels
    ///   - Syncs the device list to DevicesViewModel
    ///   - Updates dashboard statistics
    ///   - Forwards engine events (DevicesUpdated, FrequencyUpdated) to the UI thread
    /// 
    /// Thread model:
    ///   InputManager runs on a background thread at ~1000Hz.
    ///   This service's timer runs on the WPF dispatcher at ~30Hz.
    ///   All ViewModel property sets happen on the UI thread (safe for data binding).
    /// </summary>
    public class InputService : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        /// <summary>UI update interval (~30Hz).</summary>
        private const int UiTimerIntervalMs = 33;

        // ─────────────────────────────────────────────
        //  Fields
        // ─────────────────────────────────────────────

        private readonly MainViewModel _mainVm;
        private readonly Dispatcher _dispatcher;
        private InputManager _inputManager;
        private DispatcherTimer _uiTimer;
        private bool _disposed;

        /// <summary>
        /// Whether the Devices page is currently visible.
        /// When true, the UI timer syncs raw device state to DevicesViewModel.
        /// Set by MainWindow when navigation changes.
        /// </summary>
        public bool IsDevicesPageVisible { get; set; }

        /// <summary>
        /// Whether any Pad page is currently visible.
        /// When true, the UI timer updates mapping row live values.
        /// </summary>
        public bool IsPadPageVisible { get; set; }

        // ── Macro trigger recording state ──
        private MacroItem _recordingMacro;
        private int _recordingPadIndex;
        private ushort _recordedButtons;

        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates a new InputService.
        /// </summary>
        /// <param name="mainVm">The root ViewModel to push state into.</param>
        public InputService(MainViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        // ─────────────────────────────────────────────
        //  Start / Stop
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates the InputManager, subscribes to events, starts the engine
        /// and the UI update timer.
        /// </summary>
        public void Start()
        {
            if (_inputManager != null)
                return; // Already running.

            // Create engine.
            _inputManager = new InputManager();

            // Subscribe to engine events (raised on background thread).
            _inputManager.DevicesUpdated += OnDevicesUpdated;
            _inputManager.FrequencyUpdated += OnFrequencyUpdated;
            _inputManager.ErrorOccurred += OnErrorOccurred;

            // Start engine background thread.
            _inputManager.Start();

            // Create UI update timer on the dispatcher.
            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(UiTimerIntervalMs)
            };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // Update main VM state.
            _mainVm.IsEngineRunning = true;
            _mainVm.StatusText = "Engine started.";
            _mainVm.RefreshCommands();
        }

        /// <summary>
        /// Stops the UI timer and engine, releases resources.
        /// </summary>
        public void Stop()
        {
            // Stop UI timer.
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Tick -= UiTimer_Tick;
                _uiTimer = null;
            }

            // Stop and dispose engine.
            if (_inputManager != null)
            {
                _inputManager.DevicesUpdated -= OnDevicesUpdated;
                _inputManager.FrequencyUpdated -= OnFrequencyUpdated;
                _inputManager.ErrorOccurred -= OnErrorOccurred;
                _inputManager.Dispose();
                _inputManager = null;
            }

            // Update main VM state.
            _mainVm.IsEngineRunning = false;
            _mainVm.PollingFrequency = 0;
            _mainVm.StatusText = "Engine stopped.";
            _mainVm.RefreshCommands();
        }

        /// <summary>
        /// Returns the underlying InputManager (for advanced operations like test rumble).
        /// </summary>
        public InputManager Engine => _inputManager;

        // ─────────────────────────────────────────────
        //  UI Timer Tick (30Hz, UI thread)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called ~30 times per second on the UI thread.
        /// Reads engine state and pushes it to ViewModels.
        /// </summary>
        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (_inputManager == null || !_inputManager.IsRunning)
                return;

            // ── Update Pad ViewModels ──
            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var gp = _inputManager.CombinedXiStates[i];
                var vibration = _inputManager.VibrationStates[i];

                padVm.UpdateFromEngineState(gp, vibration);
            }

            // ── Update Dashboard ──
            UpdateDashboard();

            // ── Update Devices page (only if visible) ──
            if (IsDevicesPageVisible)
            {
                UpdateDevicesRawState();
            }

            // ── Update mapping row live values (only if a Pad page is visible) ──
            if (IsPadPageVisible)
            {
                UpdateMappingLiveValues();
            }

            // ── Macro trigger recording (accumulate buttons) ──
            UpdateMacroTriggerRecording();

            // ── Push ViewModel settings to PadSetting objects (runtime sync) ──
            SyncViewModelToPadSettings();

            // ── Sync macro snapshots to engine ──
            SyncMacroSnapshots();
        }

        // ─────────────────────────────────────────────
        //  Dashboard updates
        // ─────────────────────────────────────────────

        /// <summary>
        /// Pushes engine statistics to the DashboardViewModel.
        /// </summary>
        private void UpdateDashboard()
        {
            var dash = _mainVm.Dashboard;

            dash.EngineStatus = _inputManager.IsRunning ? "Running" : "Stopped";
            dash.PollingFrequency = _inputManager.CurrentFrequency;

            // Count online devices.
            var devices = SettingsManager.UserDevices?.Items;
            if (devices != null)
            {
                int total, online, mapped;
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    total = devices.Count;
                    online = devices.Count(d => d.IsOnline);
                    mapped = 0;

                    var settings = SettingsManager.UserSettings?.Items;
                    if (settings != null)
                    {
                        lock (SettingsManager.UserSettings.SyncRoot)
                        {
                            mapped = settings.Count(s =>
                                devices.Any(d => d.InstanceGuid == s.InstanceGuid && d.IsOnline));
                        }
                    }
                }

                dash.TotalDevices = total;
                dash.OnlineDevices = online;
                dash.MappedDevices = mapped;

                _mainVm.ConnectedDeviceCount = online;
            }

            // Update slot summaries.
            for (int i = 0; i < InputManager.MaxPads && i < dash.SlotSummaries.Count; i++)
            {
                var slot = dash.SlotSummaries[i];
                var padVm = _mainVm.Pads[i];

                slot.IsActive = padVm.IsDeviceOnline;
                slot.DeviceName = padVm.MappedDeviceName;

                // Count mapped and connected devices for this slot.
                var slotSettings = SettingsManager.UserSettings?.FindByPadIndex(i);
                int mappedCount = slotSettings?.Count ?? 0;
                int connectedCount = 0;
                if (slotSettings != null && devices != null)
                {
                    foreach (var us in slotSettings)
                    {
                        if (devices.Any(d => d.InstanceGuid == us.InstanceGuid && d.IsOnline))
                            connectedCount++;
                    }
                }

                slot.MappedDeviceCount = mappedCount;
                slot.ConnectedDeviceCount = connectedCount;
                slot.StatusText = mappedCount == 0 ? "No mapping"
                    : padVm.IsDeviceOnline ? "Active"
                    : "Idle";
            }

            // Update main VM frequency.
            _mainVm.PollingFrequency = _inputManager.CurrentFrequency;
        }

        // ─────────────────────────────────────────────
        //  Devices page raw state
        // ─────────────────────────────────────────────

        /// <summary>
        /// Updates the raw input state display for the selected device
        /// on the Devices page.
        /// </summary>
        private void UpdateDevicesRawState()
        {
            var devVm = _mainVm.Devices;
            var selected = devVm.SelectedDevice;
            if (selected == null)
                return;

            // Find the UserDevice for the selected row.
            UserDevice ud = FindUserDevice(selected.InstanceGuid);
            if (ud == null || ud.InputState == null)
            {
                devVm.RawAxisDisplay = "No data";
                devVm.RawButtonDisplay = "No data";
                devVm.RawPovDisplay = "No data";
                return;
            }

            var state = ud.InputState;

            // Format axes.
            var axisLines = new System.Text.StringBuilder();
            int axisCount = Math.Min(ud.CapAxeCount, CustomInputState.MaxAxis);
            for (int i = 0; i < axisCount; i++)
            {
                axisLines.AppendLine($"Axis {i}: {state.Axis[i],6} ({state.Axis[i] * 100.0 / 65535.0:F1}%)");
            }
            devVm.RawAxisDisplay = axisLines.ToString().TrimEnd();

            // Format buttons.
            var btnParts = new System.Collections.Generic.List<string>();
            int btnCount = Math.Min(ud.CapButtonCount, CustomInputState.MaxButtons);
            for (int i = 0; i < btnCount; i++)
            {
                if (state.Buttons[i])
                    btnParts.Add($"[{i}]");
            }
            devVm.RawButtonDisplay = btnParts.Count > 0
                ? "Pressed: " + string.Join(", ", btnParts)
                : "No buttons pressed";

            // Format POVs.
            var povLines = new System.Text.StringBuilder();
            int povCount = Math.Min(ud.CapPovCount, CustomInputState.MaxPovs);
            for (int i = 0; i < povCount; i++)
            {
                int pov = state.Povs[i];
                string povText = pov < 0 ? "Centered" : $"{pov / 100.0:F1}°";
                povLines.AppendLine($"POV {i}: {povText}");
            }
            devVm.RawPovDisplay = povLines.ToString().TrimEnd();
        }

        // ─────────────────────────────────────────────
        //  Mapping live values
        // ─────────────────────────────────────────────

        /// <summary>
        /// Updates the live value display on mapping rows for the active Pad page.
        /// </summary>
        private void UpdateMappingLiveValues()
        {
            var padVm = _mainVm.SelectedPad;
            if (padVm == null)
                return;

            // Find the primary device for this pad slot.
            UserDevice ud = FindPrimaryDeviceForSlot(padVm.PadIndex);
            if (ud == null || ud.InputState == null)
                return;

            var state = ud.InputState;

            foreach (var mapping in padVm.Mappings)
            {
                if (string.IsNullOrEmpty(mapping.SourceDescriptor))
                {
                    mapping.CurrentValueText = string.Empty;
                    continue;
                }

                // Parse the descriptor and read the current value.
                int value = ReadMappedValue(state, mapping.SourceDescriptor);
                mapping.CurrentValueText = value.ToString();
            }
        }

        /// <summary>
        /// Reads a value from a CustomInputState using a mapping descriptor string.
        /// Simplified version of the Step 3 parser for display purposes.
        /// </summary>
        private static int ReadMappedValue(CustomInputState state, string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor))
                return 0;

            string s = descriptor.Trim();

            // Strip prefixes.
            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
                s = s.Substring(1);
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
                s = s.Substring(1);

            string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
                return 0;

            string typeName = parts[0].ToLowerInvariant();

            return typeName switch
            {
                "axis" when index >= 0 && index < CustomInputState.MaxAxis => state.Axis[index],
                "slider" when index >= 0 && index < CustomInputState.MaxSliders => state.Sliders[index],
                "button" when index >= 0 && index < CustomInputState.MaxButtons => state.Buttons[index] ? 1 : 0,
                "pov" when index >= 0 && index < CustomInputState.MaxPovs => state.Povs[index],
                _ => 0
            };
        }

        // ─────────────────────────────────────────────
        //  Runtime sync: ViewModel → PadSetting
        // ─────────────────────────────────────────────

        /// <summary>
        /// Pushes ViewModel slider values (dead zones, force feedback, linear)
        /// directly to PadSetting objects so the engine picks them up immediately.
        /// Called at 30Hz on the UI thread. String reference writes are atomic in .NET.
        /// </summary>
        private void SyncViewModelToPadSettings()
        {
            var settings = SettingsManager.UserSettings?.Items;
            if (settings == null) return;

            UserSetting[] snapshot;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                snapshot = settings.ToArray();
            }

            foreach (var us in snapshot)
            {
                var ps = us.GetPadSetting();
                if (ps == null) continue;

                int padIndex = us.MapTo;
                if (padIndex < 0 || padIndex >= _mainVm.Pads.Count)
                    continue;

                var padVm = _mainVm.Pads[padIndex];

                // Dead zones (independent X/Y).
                ps.LeftThumbDeadZoneX = padVm.LeftDeadZoneX.ToString();
                ps.LeftThumbDeadZoneY = padVm.LeftDeadZoneY.ToString();
                ps.RightThumbDeadZoneX = padVm.RightDeadZoneX.ToString();
                ps.RightThumbDeadZoneY = padVm.RightDeadZoneY.ToString();

                // Anti-dead zones.
                ps.LeftThumbAntiDeadZone = padVm.LeftAntiDeadZone.ToString();
                ps.RightThumbAntiDeadZone = padVm.RightAntiDeadZone.ToString();

                // Linear response.
                ps.LeftThumbLinear = padVm.LeftLinear.ToString();
                ps.RightThumbLinear = padVm.RightLinear.ToString();

                // Trigger dead zones.
                ps.LeftTriggerDeadZone = padVm.LeftTriggerDeadZone.ToString();
                ps.RightTriggerDeadZone = padVm.RightTriggerDeadZone.ToString();
                ps.LeftTriggerAntiDeadZone = padVm.LeftTriggerAntiDeadZone.ToString();
                ps.RightTriggerAntiDeadZone = padVm.RightTriggerAntiDeadZone.ToString();

                // Force feedback.
                ps.ForceOverall = padVm.ForceOverallGain.ToString();
                ps.LeftMotorStrength = padVm.LeftMotorStrength.ToString();
                ps.RightMotorStrength = padVm.RightMotorStrength.ToString();
                ps.ForceSwapMotor = padVm.SwapMotors ? "1" : "0";

                // Mapping descriptors.
                foreach (var mapping in padVm.Mappings)
                {
                    var prop = typeof(PadSetting).GetProperty(mapping.TargetSettingName);
                    if (prop != null && prop.PropertyType == typeof(string) && prop.CanWrite)
                        prop.SetValue(ps, mapping.SourceDescriptor ?? string.Empty);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Macro snapshot sync
        // ─────────────────────────────────────────────

        /// <summary>
        /// Pushes the current macro lists from PadViewModels to the engine's
        /// MacroSnapshots array. The engine reads these atomically each cycle.
        /// Called at 30Hz on the UI thread.
        /// </summary>
        private void SyncMacroSnapshots()
        {
            if (_inputManager == null)
                return;

            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                if (padVm.Macros.Count == 0)
                {
                    _inputManager.MacroSnapshots[i] = null;
                }
                else
                {
                    // Create a snapshot array. The MacroItem objects are shared references —
                    // runtime state (IsExecuting, CurrentActionIndex, etc.) is read/written
                    // by the engine thread, but the properties themselves are simple fields
                    // that don't need locking for this use case.
                    var snapshot = new MacroItem[padVm.Macros.Count];
                    padVm.Macros.CopyTo(snapshot, 0);
                    _inputManager.MacroSnapshots[i] = snapshot;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Engine event handlers (background thread → UI thread)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called on the background thread when the device list changes.
        /// Marshals to the UI thread to sync DevicesViewModel.
        /// </summary>
        private void OnDevicesUpdated(object sender, EventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                SyncDevicesList();
                UpdatePadDeviceInfo();
            }));
        }

        /// <summary>
        /// Called on the background thread when the frequency measurement updates.
        /// </summary>
        private void OnFrequencyUpdated(object sender, EventArgs e)
        {
            // Frequency is read on the next UI timer tick, no immediate action needed.
        }

        /// <summary>
        /// Called on the background thread when a non-fatal error occurs.
        /// </summary>
        private void OnErrorOccurred(object sender, InputExceptionEventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                _mainVm.StatusText = $"Error: {e.Message}";
            }));
        }

        // ─────────────────────────────────────────────
        //  Device list sync
        // ─────────────────────────────────────────────

        /// <summary>
        /// Synchronizes the DevicesViewModel.Devices collection with
        /// SettingsManager.UserDevices. Called on the UI thread.
        /// 
        /// Filtering strategy (multi-layer to ensure no virtual controllers leak):
        ///   Layer 1: GUID-based — exclude devices whose InstanceGuid matches
        ///            our ViGEm virtual controller XInput slots.
        ///   Layer 2: VID/PID-based — exclude devices with Xbox 360 VID/PID
        ///            (0x045E:0x028E) that aren't XInput-managed, since they're
        ///            ViGEm virtual controllers seen through DirectInput.
        ///   Layer 3: Name-based — exclude devices with "ViGEm" or "Virtual"
        ///            in their name.
        /// </summary>
        private void SyncDevicesList()
        {
            var devVm = _mainVm.Devices;
            var userDevices = SettingsManager.UserDevices?.Items;
            if (userDevices == null)
                return;

            UserDevice[] snapshot;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                snapshot = userDevices.ToArray();
            }

            // Get the set of instance GUIDs belonging to our ViGEm virtual controllers.
            // These should be hidden from the user-facing device list.
            var vigemGuids = _inputManager?.GetViGEmVirtualDeviceGuids() ?? new HashSet<Guid>();

            // Update existing rows and add new ones (skip virtual devices).
            foreach (var ud in snapshot)
            {
                // ── Multi-layer virtual controller filter ──
                if (IsVirtualOrShadowDevice(ud, vigemGuids))
                    continue;

                var row = devVm.FindByGuid(ud.InstanceGuid);
                if (row == null)
                {
                    row = new DeviceRowViewModel();
                    devVm.Devices.Add(row);
                }

                PopulateDeviceRow(row, ud);
            }

            // Remove rows for devices that are no longer valid or are virtual.
            for (int i = devVm.Devices.Count - 1; i >= 0; i--)
            {
                var row = devVm.Devices[i];

                // Remove if this is a virtual device.
                bool isVirtual = vigemGuids.Contains(row.InstanceGuid);

                // Also remove if no longer in the snapshot.
                bool found = false;
                if (!isVirtual)
                {
                    foreach (var ud in snapshot)
                    {
                        if (ud.InstanceGuid == row.InstanceGuid)
                        {
                            // Also re-check virtual status with full device info.
                            if (IsVirtualOrShadowDevice(ud, vigemGuids))
                            {
                                isVirtual = true;
                                break;
                            }
                            found = true;
                            break;
                        }
                    }
                }

                if (isVirtual || !found)
                    devVm.Devices.RemoveAt(i);
            }

            devVm.RefreshCounts();
        }

        /// <summary>
        /// Determines whether a UserDevice is a virtual controller or a shadow device
        /// that should be hidden from the user-facing device list.
        /// 
        /// A "shadow device" is a ViGEm virtual controller that appeared through
        /// DirectInput enumeration with recognizable characteristics but wasn't
        /// caught by the engine-level filter.
        /// </summary>
        /// <param name="ud">The device to check.</param>
        /// <param name="vigemGuids">Set of known ViGEm virtual controller GUIDs.</param>
        /// <returns>True if the device should be hidden.</returns>
        private bool IsVirtualOrShadowDevice(UserDevice ud, HashSet<Guid> vigemGuids)
        {
            // ── Layer 1: GUID match (XInput-based ViGEm devices) ──
            if (vigemGuids.Contains(ud.InstanceGuid))
                return true;

            // ── Layer 2: VID/PID match for ViGEm Xbox 360 controllers ──
            // ViGEm virtual Xbox 360 controllers use VID 0x045E, PID 0x028E.
            // If this device has that VID/PID but is NOT one of our XInput-managed
            // native controllers (IsXInput == true), it's a shadow device.
            //
            // Real Xbox controllers at unoccupied XInput slots have IsXInput == true
            // because they were created by UpdateXInputDevices, not by SDL enumeration.
            // A ViGEm controller that leaked through SDL would have IsXInput == false
            // with the Xbox 360 VID/PID — that's the telltale sign.
            if (ud.VendorId == 0x045E && ud.ProdId == 0x028E && !ud.IsXInput)
                return true;

            // ── Layer 3: Name-based detection ──
            string name = ud.ResolvedName;
            if (!string.IsNullOrEmpty(name))
            {
                if (name.Contains("ViGEm", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Virtual Gamepad", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // ── Layer 4: Hidden flag ──
            if (ud.IsHidden)
                return true;

            return false;
        }

        /// <summary>
        /// Populates a DeviceRowViewModel from a UserDevice.
        /// </summary>
        private void PopulateDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            row.InstanceGuid = ud.InstanceGuid;
            row.DeviceName = ud.ResolvedName;
            row.ProductName = ud.ProductName;
            row.ProductGuid = ud.ProductGuid;
            row.VendorId = ud.VendorId;
            row.ProductId = ud.ProdId;
            row.IsOnline = ud.IsOnline;
            row.IsEnabled = ud.IsEnabled;
            row.IsHidden = ud.IsHidden;
            row.IsXInput = ud.IsXInput;
            row.AxisCount = ud.CapAxeCount;
            row.ButtonCount = ud.CapButtonCount;
            row.PovCount = ud.CapPovCount;
            row.HasRumble = ud.HasForceFeedback;
            row.DevicePath = ud.DevicePath;

            // Resolve device type name.
            row.DeviceType = ud.CapType switch
            {
                InputDeviceType.Gamepad => "Gamepad",
                InputDeviceType.Joystick => "Joystick",
                InputDeviceType.Driving => "Wheel",
                InputDeviceType.Flight => "Flight Stick",
                InputDeviceType.FirstPerson => "First Person",
                InputDeviceType.Supplemental => "Supplemental",
                InputDeviceType.Mouse => "Mouse",
                InputDeviceType.Keyboard => "Keyboard",
                _ => "Device"
            };

            // Resolve slot assignment.
            var us = SettingsManager.UserSettings?.FindByInstanceGuid(ud.InstanceGuid);
            row.AssignedSlot = us?.MapTo ?? -1;
        }

        /// <summary>
        /// Updates PadViewModel device info (name, online status) for all pads.
        /// Populates the MappedDevices collection with ALL devices assigned to each slot.
        /// Called after the device list changes or after a device is assigned to a slot.
        /// </summary>
        public void UpdatePadDeviceInfo()
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            for (int i = 0; i < InputManager.MaxPads && i < _mainVm.Pads.Count; i++)
            {
                var padVm = _mainVm.Pads[i];
                var slotSettings = settings.FindByPadIndex(i);

                if (slotSettings == null || slotSettings.Count == 0)
                {
                    padVm.MappedDevices.Clear();
                    padVm.MappedDeviceName = "No device mapped";
                    padVm.MappedDeviceGuid = Guid.Empty;
                    padVm.IsDeviceOnline = false;
                }
                else
                {
                    // Build list of all mapped devices for this slot.
                    var deviceInfos = new List<PadViewModel.MappedDeviceInfo>();
                    bool anyOnline = false;

                    foreach (var us in slotSettings)
                    {
                        var ud = FindUserDevice(us.InstanceGuid);
                        string name = ud?.ResolvedName ?? "Unknown device";
                        bool online = ud?.IsOnline ?? false;
                        if (online) anyOnline = true;

                        deviceInfos.Add(new PadViewModel.MappedDeviceInfo
                        {
                            Name = name,
                            InstanceGuid = us.InstanceGuid,
                            IsOnline = online
                        });
                    }

                    // Sync the ObservableCollection (minimize UI churn).
                    SyncMappedDevices(padVm.MappedDevices, deviceInfos);

                    // Summary properties for backward compatibility / simple bindings.
                    var primary = slotSettings[0];
                    var primaryUd = FindUserDevice(primary.InstanceGuid);

                    padVm.MappedDeviceName = deviceInfos.Count == 1
                        ? deviceInfos[0].Name
                        : string.Join(" + ", deviceInfos.Select(d => d.Name));
                    padVm.MappedDeviceGuid = primary.InstanceGuid;
                    padVm.IsDeviceOnline = anyOnline;
                }

                padVm.RefreshCommands();
            }
        }

        /// <summary>
        /// Synchronizes the ObservableCollection with a new list,
        /// minimizing UI churn by updating in-place where possible.
        /// </summary>
        private static void SyncMappedDevices(
            System.Collections.ObjectModel.ObservableCollection<PadViewModel.MappedDeviceInfo> collection,
            List<PadViewModel.MappedDeviceInfo> newItems)
        {
            // Remove extras.
            while (collection.Count > newItems.Count)
                collection.RemoveAt(collection.Count - 1);

            // Update existing and add new.
            for (int i = 0; i < newItems.Count; i++)
            {
                if (i < collection.Count)
                {
                    collection[i].Name = newItems[i].Name;
                    collection[i].InstanceGuid = newItems[i].InstanceGuid;
                    collection[i].IsOnline = newItems[i].IsOnline;
                }
                else
                {
                    collection.Add(newItems[i]);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  UserDevice lookup helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a UserDevice by instance GUID from the SettingsManager collection.
        /// </summary>
        private static UserDevice FindUserDevice(Guid instanceGuid)
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                return devices.FirstOrDefault(d => d.InstanceGuid == instanceGuid);
            }
        }

        /// <summary>
        /// Finds the primary (first) UserDevice assigned to a pad slot.
        /// </summary>
        private static UserDevice FindPrimaryDeviceForSlot(int padIndex)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return null;

            var slotSettings = settings.FindByPadIndex(padIndex);
            if (slotSettings == null || slotSettings.Count == 0)
                return null;

            return FindUserDevice(slotSettings[0].InstanceGuid);
        }

        // ─────────────────────────────────────────────
        //  Test rumble
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sends a brief test rumble to the device mapped to the specified pad slot.
        /// </summary>
        /// <param name="padIndex">Pad slot index (0–3).</param>
        public void SendTestRumble(int padIndex)
        {
            if (_inputManager == null || padIndex < 0 || padIndex >= InputManager.MaxPads)
                return;

            // Set vibration for 500ms, then clear.
            _inputManager.VibrationStates[padIndex].LeftMotorSpeed = 32768;
            _inputManager.VibrationStates[padIndex].RightMotorSpeed = 32768;

            // Schedule clearing after 500ms.
            var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            clearTimer.Tick += (s, e) =>
            {
                if (_inputManager != null && padIndex < InputManager.MaxPads)
                {
                    _inputManager.VibrationStates[padIndex].LeftMotorSpeed = 0;
                    _inputManager.VibrationStates[padIndex].RightMotorSpeed = 0;
                }
                clearTimer.Stop();
            };
            clearTimer.Start();
        }

        // ─────────────────────────────────────────────
        //  Macro trigger recording
        // ─────────────────────────────────────────────

        /// <summary>
        /// Starts recording button presses for a macro trigger combo.
        /// While recording, CombinedXiState button flags are OR'd together
        /// each UI tick. Call <see cref="StopMacroTriggerRecording"/> to
        /// finalize and write the result to the MacroItem.
        /// </summary>
        public void StartMacroTriggerRecording(MacroItem macro, int padIndex)
        {
            // Stop any existing recording.
            if (_recordingMacro != null)
                StopMacroTriggerRecording();

            _recordingMacro = macro;
            _recordingPadIndex = padIndex;
            _recordedButtons = 0;
            macro.IsRecordingTrigger = true;
        }

        /// <summary>
        /// Stops the current macro trigger recording session and writes the
        /// accumulated button flags to the MacroItem.
        /// </summary>
        public void StopMacroTriggerRecording()
        {
            if (_recordingMacro == null)
                return;

            _recordingMacro.TriggerButtons = _recordedButtons;
            _recordingMacro.IsRecordingTrigger = false;
            _recordingMacro = null;
            _recordedButtons = 0;
        }

        /// <summary>
        /// Called each UI tick during macro trigger recording.
        /// Accumulates button flags from the CombinedXiState.
        /// </summary>
        private void UpdateMacroTriggerRecording()
        {
            if (_recordingMacro == null || _inputManager == null)
                return;

            if (_recordingPadIndex >= 0 && _recordingPadIndex < InputManager.MaxPads)
            {
                ushort buttons = _inputManager.CombinedXiStates[_recordingPadIndex].Buttons;
                _recordedButtons |= buttons;
            }
        }

        // ─────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }
    }
}
