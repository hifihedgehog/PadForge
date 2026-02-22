using System;
using System.Windows;
using ModernWpf.Controls;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge
{
    /// <summary>
    /// MainWindow code-behind. Wires navigation, creates services, manages
    /// the application lifecycle (engine start/stop on window open/close).
    /// </summary>
    public partial class MainWindow
    {
        private readonly MainViewModel _viewModel;
        private InputService _inputService;
        private SettingsService _settingsService;
        private RecorderService _recorderService;
        private DeviceService _deviceService;

        public MainWindow()
        {
            InitializeComponent();

            // Create root ViewModel.
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Set child DataContexts.
            DashboardPageView.DataContext = _viewModel.Dashboard;
            DevicesPageView.DataContext = _viewModel.Devices;
            SettingsPageView.DataContext = _viewModel.Settings;

            // Create services.
            _settingsService = new SettingsService(_viewModel);
            _inputService = new InputService(_viewModel);
            _recorderService = new RecorderService(_viewModel);
            _deviceService = new DeviceService(_viewModel, _settingsService);

            // Wire engine start/stop commands.
            _viewModel.StartEngineRequested += (s, e) => _inputService.Start();
            _viewModel.StopEngineRequested += (s, e) => _inputService.Stop();

            // Wire settings commands.
            _viewModel.Settings.SaveRequested += (s, e) => _settingsService.Save();
            _viewModel.Settings.ReloadRequested += (s, e) => _settingsService.Reload();
            _viewModel.Settings.ResetRequested += (s, e) => _settingsService.ResetToDefaults();
            _viewModel.Settings.OpenSettingsFolderRequested += OnOpenSettingsFolder;
            _viewModel.Settings.ThemeChanged += OnThemeChanged;

            // Wire ViGEm commands (stubs — full implementation in carried-over ViGEm code).
            _viewModel.Settings.InstallViGEmRequested += (s, e) =>
                _viewModel.StatusText = "ViGEmBus installation not yet implemented.";
            _viewModel.Settings.UninstallViGEmRequested += (s, e) =>
                _viewModel.StatusText = "ViGEmBus uninstallation not yet implemented.";

            // Wire device service events (assign to slot, hide, etc.).
            _deviceService.WireEvents();

            // Refresh PadPage dropdowns immediately after device assignment changes.
            _deviceService.DeviceAssignmentChanged += (s, e) => _inputService.UpdatePadDeviceInfo();

            // Wire devices page refresh.
            _viewModel.Devices.RefreshRequested += (s, e) =>
                _viewModel.StatusText = "Device list refreshed.";

            // Wire test rumble for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                pad.TestRumbleRequested += (s, e) =>
                {
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex);
                };
            }

            // Wire recorder for each pad's mapping rows.
            foreach (var pad in _viewModel.Pads)
            {
                foreach (var mapping in pad.Mappings)
                {
                    var capturedPad = pad;
                    mapping.StartRecordingRequested += (s, e) =>
                    {
                        if (s is MappingItem mi)
                            _recorderService.StartRecording(mi, capturedPad.PadIndex);
                    };
                    mapping.StopRecordingRequested += (s, e) =>
                        _recorderService.CancelRecording();
                }
            }

            // Recorder completion marks settings dirty.
            _recorderService.RecordingCompleted += (s, result) =>
                _settingsService.MarkDirty();

            // Wire macro trigger recording for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;

                // Wire existing macros.
                foreach (var macro in pad.Macros)
                    WireMacroRecording(macro, capturedPad.PadIndex);

                // Wire macros added later.
                pad.Macros.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (MacroItem macro in e.NewItems)
                            WireMacroRecording(macro, capturedPad.PadIndex);
                    }
                };
            }

            // Window events.
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        // ─────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Initialize settings (load from disk).
            _settingsService.Initialize();

            // Populate diagnostic info.
            _viewModel.Settings.ApplicationVersion =
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            _viewModel.Settings.RuntimeVersion = Environment.Version.ToString();
            _viewModel.Settings.XInputLibraryInfo =
                PadForge.Common.Input.InputManager.GetXInputLibraryInfo();

            // Detect ViGEmBus driver.
            try
            {
                bool vigemInstalled = PadForge.Common.Input.InputManager.CheckViGEmInstalled();
                _viewModel.Settings.IsViGEmInstalled = vigemInstalled;
                if (!vigemInstalled)
                    _viewModel.StatusText = "ViGEmBus driver not detected. Virtual controller output disabled.";
            }
            catch (Exception ex)
            {
                _viewModel.Settings.IsViGEmInstalled = false;
                _viewModel.StatusText = $"ViGEm check failed: {ex.Message}";
            }

            // Check SDL2.dll availability.
            try
            {
                var sdlVersion = SDL2.SDL.SDL_Linked_Version();
                _viewModel.Settings.SdlVersion = $"SDL {sdlVersion.major}.{sdlVersion.minor}.{sdlVersion.patch}";
            }
            catch (DllNotFoundException)
            {
                _viewModel.Settings.SdlVersion = "SDL2.dll NOT FOUND";
                _viewModel.StatusText = "SDL2.dll not found! Place SDL2.dll next to PadForge.exe. " +
                    "Download from https://github.com/libsdl-org/SDL/releases";
            }
            catch
            {
                _viewModel.Settings.SdlVersion = "Unknown";
            }

            // Auto-start engine if configured.
            if (_viewModel.Settings.AutoStartEngine)
            {
                _inputService.Start();
            }

            // Apply start-minimized setting.
            if (_viewModel.Settings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
            }

            // Select the first nav item.
            if (NavView.MenuItems.Count > 0)
                NavView.SelectedItem = NavView.MenuItems[0];
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Save settings if dirty.
            if (_settingsService.IsDirty)
            {
                _settingsService.Save();
            }

            // Unwire device service.
            _deviceService?.UnwireEvents();

            // Stop engine and dispose services.
            _recorderService?.Dispose();
            _inputService?.Dispose();
        }

        // ─────────────────────────────────────────────
        //  Navigation
        // ─────────────────────────────────────────────

        private void NavView_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
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
            SettingsPageView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            AboutPageView.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

            bool isPad = tag.StartsWith("Pad") && tag.Length == 4;
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

        private void OnThemeChanged(object sender, int themeIndex)
        {
            ModernWpf.ThemeManager.Current.ApplicationTheme = themeIndex switch
            {
                1 => ModernWpf.ApplicationTheme.Light,
                2 => ModernWpf.ApplicationTheme.Dark,
                _ => null // System default
            };
        }
    }
}
