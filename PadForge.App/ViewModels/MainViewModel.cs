using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Engine;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for a single virtual controller sidebar entry.
    /// Line 1: "Virtual Controller N"
    /// Line 2: [type icon] #instance_number
    /// </summary>
    public class NavControllerItemViewModel : ObservableObject
    {
        public NavControllerItemViewModel(int padIndex)
        {
            PadIndex = padIndex;
            _slotNumber = 1;
            _instanceLabel = "#1";
            _iconKey = "XboxControllerIcon";
        }

        /// <summary>Zero-based pad slot index (0–3).</summary>
        public int PadIndex { get; }

        /// <summary>Navigation tag for this item ("Pad1"–"Pad4").</summary>
        public string Tag => $"Pad{PadIndex + 1}";

        private int _slotNumber;
        /// <summary>Overall controller number among active slots (1-based).</summary>
        public int SlotNumber
        {
            get => _slotNumber;
            set => SetProperty(ref _slotNumber, value);
        }

        private string _instanceLabel;
        /// <summary>Per-type instance number label (e.g., "#1", "#2").</summary>
        public string InstanceLabel
        {
            get => _instanceLabel;
            set => SetProperty(ref _instanceLabel, value);
        }

        private string _iconKey;
        /// <summary>Resource key for the controller type icon (e.g., "XboxControllerIcon").</summary>
        public string IconKey
        {
            get => _iconKey;
            set => SetProperty(ref _iconKey, value);
        }

        private bool _isEnabled = true;
        /// <summary>Whether this virtual controller is enabled (ViGEm active).</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private int _connectedDeviceCount;
        /// <summary>Number of mapped physical devices that are currently connected.</summary>
        public int ConnectedDeviceCount
        {
            get => _connectedDeviceCount;
            set => SetProperty(ref _connectedDeviceCount, value);
        }
    }

    /// <summary>
    /// Root ViewModel for the application. Manages navigation state,
    /// the collection of 4 pad ViewModels, and app-wide status information.
    /// Serves as the DataContext for MainWindow.
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        public MainViewModel()
        {
            Title = "PadForge";

            // Create pad ViewModels (one per virtual controller slot).
            for (int i = 0; i < Common.Input.InputManager.MaxPads; i++)
            {
                Pads.Add(new PadViewModel(i));
            }

            Dashboard = new DashboardViewModel();
            Devices = new DevicesViewModel();
            Settings = new SettingsViewModel();

            // Default to Dashboard page.
            _selectedNavTag = "Dashboard";
        }

        // ─────────────────────────────────────────────
        //  Child ViewModels
        // ─────────────────────────────────────────────

        /// <summary>
        /// The 4 virtual controller pad ViewModels (Player 1–4).
        /// </summary>
        public ObservableCollection<PadViewModel> Pads { get; } = new ObservableCollection<PadViewModel>();

        /// <summary>
        /// Sidebar navigation items for virtual controller slots that have mapped devices.
        /// Dynamically rebuilt when device assignments change.
        /// </summary>
        public ObservableCollection<NavControllerItemViewModel> NavControllerItems { get; } = new ObservableCollection<NavControllerItemViewModel>();

        /// <summary>
        /// Raised once after <see cref="RefreshNavControllerItems"/> finishes all
        /// collection changes. MainWindow subscribes to this instead of
        /// <c>NavControllerItems.CollectionChanged</c> to avoid multiple rapid
        /// sidebar rebuilds that corrupt ModernWpf's internal ItemsRepeater.
        /// </summary>
        public event EventHandler NavControllerItemsRefreshed;

        /// <summary>
        /// Rebuilds the sidebar controller entries based on which slots are created,
        /// and computes per-type instance numbers.
        /// </summary>
        public void RefreshNavControllerItems()
        {
            // Determine which slots have been explicitly created.
            var activeSlots = new System.Collections.Generic.List<int>();
            for (int i = 0; i < Pads.Count; i++)
            {
                if (SettingsManager.SlotCreated[i])
                    activeSlots.Add(i);
            }

            // Check if the set of active slots has changed.
            bool slotsChanged = activeSlots.Count != NavControllerItems.Count;
            if (!slotsChanged)
            {
                for (int i = 0; i < activeSlots.Count; i++)
                {
                    if (NavControllerItems[i].PadIndex != activeSlots[i])
                    {
                        slotsChanged = true;
                        break;
                    }
                }
            }

            if (slotsChanged)
            {
                NavControllerItems.Clear();
                foreach (int slot in activeSlots)
                    NavControllerItems.Add(new NavControllerItemViewModel(slot));
            }

            // Compute global slot number and per-type instance numbers.
            int xboxCount = 0;
            int ds4Count = 0;
            int globalCount = 0;

            foreach (var nav in NavControllerItems)
            {
                globalCount++;
                nav.SlotNumber = globalCount;

                var pad = Pads[nav.PadIndex];
                string iconKey;
                int instanceNum;

                switch (pad.OutputType)
                {
                    case VirtualControllerType.DualShock4:
                        ds4Count++;
                        instanceNum = ds4Count;
                        iconKey = "DS4ControllerIcon";
                        break;
                    default:
                        xboxCount++;
                        instanceNum = xboxCount;
                        iconKey = "XboxControllerIcon";
                        break;
                }

                nav.InstanceLabel = $"#{instanceNum}";
                nav.IconKey = iconKey;
                nav.IsEnabled = SettingsManager.SlotEnabled[nav.PadIndex];
            }

            // Only trigger a full sidebar rebuild when slots were added/removed.
            // Property-only changes (icon, label, enabled) are handled in-place
            // by the PropertyChanged subscriptions wired in RebuildControllerSection.
            if (slotsChanged)
                NavControllerItemsRefreshed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Dashboard overview ViewModel.</summary>
        public DashboardViewModel Dashboard { get; }

        /// <summary>Devices list ViewModel.</summary>
        public DevicesViewModel Devices { get; }

        /// <summary>Application settings ViewModel.</summary>
        public SettingsViewModel Settings { get; }

        // ─────────────────────────────────────────────
        //  Navigation
        // ─────────────────────────────────────────────

        private string _selectedNavTag;

        /// <summary>
        /// The tag string of the currently selected navigation item.
        /// Used by MainWindow to determine which page to display.
        /// Values: "Dashboard", "Pad1"–"Pad4", "Devices", "Settings", "About"
        /// </summary>
        public string SelectedNavTag
        {
            get => _selectedNavTag;
            set
            {
                if (SetProperty(ref _selectedNavTag, value))
                {
                    OnPropertyChanged(nameof(IsPadPageSelected));
                    OnPropertyChanged(nameof(SelectedPadIndex));
                }
            }
        }

        /// <summary>
        /// True if a Pad page (Pad1–Pad4) is currently selected.
        /// </summary>
        public bool IsPadPageSelected =>
            _selectedNavTag != null &&
            _selectedNavTag.StartsWith("Pad", StringComparison.Ordinal) &&
            _selectedNavTag.Length == 4 &&
            char.IsDigit(_selectedNavTag[3]);

        /// <summary>
        /// Returns the zero-based pad index for the currently selected Pad page,
        /// or -1 if no Pad page is selected.
        /// </summary>
        public int SelectedPadIndex
        {
            get
            {
                if (IsPadPageSelected && int.TryParse(_selectedNavTag.Substring(3), out int num))
                    return num - 1; // "Pad1" → 0, "Pad2" → 1, etc.
                return -1;
            }
        }

        /// <summary>
        /// Returns the PadViewModel for the currently selected Pad page, or null.
        /// </summary>
        public PadViewModel SelectedPad
        {
            get
            {
                int idx = SelectedPadIndex;
                if (idx >= 0 && idx < Pads.Count)
                    return Pads[idx];
                return null;
            }
        }

        // ─────────────────────────────────────────────
        //  App-wide status
        // ─────────────────────────────────────────────

        private string _statusText = "Ready";

        /// <summary>
        /// Status bar text displayed at the bottom of the main window.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isEngineRunning;

        /// <summary>
        /// Whether the input engine polling loop is currently active.
        /// </summary>
        public bool IsEngineRunning
        {
            get => _isEngineRunning;
            set
            {
                if (SetProperty(ref _isEngineRunning, value))
                {
                    OnPropertyChanged(nameof(EngineStatusText));
                }
            }
        }

        /// <summary>
        /// Display string for engine status: "Running" or "Stopped".
        /// </summary>
        public string EngineStatusText => IsEngineRunning ? "Running" : "Stopped";

        private double _pollingFrequency;

        /// <summary>
        /// Current input polling frequency in Hz.
        /// </summary>
        public double PollingFrequency
        {
            get => _pollingFrequency;
            set => SetProperty(ref _pollingFrequency, value);
        }

        private int _connectedDeviceCount;

        /// <summary>
        /// Number of currently connected input devices.
        /// </summary>
        public int ConnectedDeviceCount
        {
            get => _connectedDeviceCount;
            set => SetProperty(ref _connectedDeviceCount, value);
        }

        private bool _isViGEmInstalled;

        /// <summary>
        /// Whether the ViGEmBus driver is installed and available.
        /// </summary>
        public bool IsViGEmInstalled
        {
            get => _isViGEmInstalled;
            set => SetProperty(ref _isViGEmInstalled, value);
        }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        private RelayCommand _startEngineCommand;

        /// <summary>
        /// Command to start the input engine. Bound to a toolbar button.
        /// The actual start logic is in InputService; this command is
        /// wired up by MainWindow code-behind.
        /// </summary>
        public RelayCommand StartEngineCommand =>
            _startEngineCommand ??= new RelayCommand(
                () => StartEngineRequested?.Invoke(this, EventArgs.Empty),
                () => !IsEngineRunning);

        private RelayCommand _stopEngineCommand;

        /// <summary>
        /// Command to stop the input engine.
        /// </summary>
        public RelayCommand StopEngineCommand =>
            _stopEngineCommand ??= new RelayCommand(
                () => StopEngineRequested?.Invoke(this, EventArgs.Empty),
                () => IsEngineRunning);

        /// <summary>Raised when the user requests to start the engine.</summary>
        public event EventHandler StartEngineRequested;

        /// <summary>Raised when the user requests to stop the engine.</summary>
        public event EventHandler StopEngineRequested;

        /// <summary>
        /// Refreshes the CanExecute state of start/stop commands.
        /// Call after IsEngineRunning changes.
        /// </summary>
        public void RefreshCommands()
        {
            _startEngineCommand?.NotifyCanExecuteChanged();
            _stopEngineCommand?.NotifyCanExecuteChanged();
        }
    }
}
