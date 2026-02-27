# PadForge — Build & Project Reference

## Overview

PadForge is a modern controller mapping utility (fork of x360ce) rebuilt with:
- **SDL3** for all device input (replaces DirectInput/SharpDX/XInput enumeration)
- **ViGEmBus** for virtual Xbox 360 and DualShock 4 controller output
- **vJoy** for virtual joystick output (optional)
- **DSU/Cemuhook** motion server for gyro/accelerometer passthrough
- **.NET 8 WPF** with ModernWpf Fluent Design
- **MVVM** architecture with CommunityToolkit.Mvvm

## Solution Structure

```
PadForge.sln
├── PadForge.Engine/          (Class library — net8.0-windows)
│   ├── Common/
│   │   ├── SDL3Minimal.cs         SDL3 P/Invoke declarations
│   │   ├── InputTypes.cs          Enums: MapType, ObjectGuid, InputDeviceType, etc.
│   │   ├── SdlDeviceWrapper.cs    SDL joystick/gamepad wrapper (open, read, rumble, GUID)
│   │   ├── SdlKeyboardWrapper.cs  SDL keyboard input wrapper
│   │   ├── SdlMouseWrapper.cs     SDL mouse input wrapper
│   │   ├── ISdlInputDevice.cs     Interface for SDL input devices
│   │   ├── CustomInputState.cs    Unified input state (axes, buttons, POVs, sliders)
│   │   ├── CustomInputHelper.cs   State comparison and update helpers
│   │   ├── CustomInputUpdate.cs   Buffered input change records
│   │   ├── DeviceObjectItem.cs    Device axis/button/POV capability metadata
│   │   ├── DeviceEffectItem.cs    Force feedback effect metadata
│   │   ├── ForceFeedbackState.cs  Rumble + haptic state management
│   │   ├── GamepadTypes.cs        Gamepad axis/button enum definitions
│   │   ├── VirtualControllerTypes.cs  IVirtualController interface + VirtualControllerType enum
│   │   ├── RawInputListener.cs    Windows Raw Input listener
│   │   └── RumbleLogger.cs        Diagnostic rumble event logger
│   ├── Data/
│   │   ├── UserDevice.cs          Physical device record (serializable + runtime)
│   │   ├── UserSetting.cs         Device-to-slot link (serializable)
│   │   └── PadSetting.cs          Mapping configuration (mappings, dead zones, FF properties)
│   └── Properties/
│       └── AssemblyInfo.cs
│
├── PadForge.App/             (WPF Application — net8.0-windows)
│   ├── App.xaml / .cs             Application entry, ModernWpf resources, converter registration
│   ├── MainWindow.xaml / .cs      Shell: NavigationView + status bar + page switching + service wiring
│   ├── Common/
│   │   ├── SettingsManager.cs     Static class: device/setting collections, assignment, defaults
│   │   ├── ControllerIcons.cs     SVG path data for controller type icons
│   │   ├── DriverInstaller.cs     ViGEmBus, HidHide, vJoy driver installation logic
│   │   ├── StartupHelper.cs       Windows startup registry management
│   │   ├── VirtualKey.cs          Virtual key code definitions
│   │   └── Input/
│   │       ├── InputManager.cs                        Main partial: background thread, 6-step pipeline
│   │       ├── InputManager.Step1.UpdateDevices.cs    SDL enumeration, ViGEm filtering
│   │       ├── InputManager.Step2.UpdateInputStates.cs  State reading + force feedback
│   │       ├── InputManager.Step3.UpdateOutputStates.cs  CustomInputState → OutputState mapping
│   │       ├── InputManager.Step4.CombineOutputStates.cs  Multi-device combination per slot
│   │       ├── InputManager.Step4b.EvaluateMacros.cs  Macro evaluation per cycle
│   │       ├── InputManager.Step5.VirtualDevices.cs   Virtual controller output (ViGEm + vJoy)
│   │       ├── InputManager.Step6.RetrieveOutputStates.cs  Copy combined output for UI
│   │       ├── Xbox360VirtualController.cs    ViGEm Xbox 360 virtual controller
│   │       ├── DS4VirtualController.cs        ViGEm DualShock 4 virtual controller
│   │       ├── VJoyVirtualController.cs       vJoy virtual joystick controller
│   │       ├── InputExceptionEventArgs.cs     Error event args
│   │       ├── InputEventArgs.cs              Input event args + InputEventType enum
│   │       └── InputException.cs              Custom exception with pipeline context
│   ├── Converter/
│   │   ├── AxisToPercentConverter.cs
│   │   ├── BoolToColorConverter.cs
│   │   ├── BoolToInstallTextConverter.cs
│   │   ├── BoolToOpacityConverter.cs
│   │   ├── BoolToVisibilityConverter.cs
│   │   ├── NormToCanvasConverter.cs
│   │   ├── NormToTriggerHeightConverter.cs
│   │   ├── NormToTriggerSlideConverter.cs
│   │   ├── NullToCollapsedConverter.cs
│   │   ├── PercentToSizeConverter.cs
│   │   ├── PovToAngleConverter.cs
│   │   ├── StatusToColorConverter.cs
│   │   ├── StringToGeometryConverter.cs
│   │   └── StringToVisibilityConverter.cs
│   ├── Controls/
│   │   └── RangeSlider.cs         Custom dead zone range slider control
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs            INotifyPropertyChanged base
│   │   ├── MainViewModel.cs            Root: navigation, pads, engine status, commands
│   │   ├── DashboardViewModel.cs       Overview: slot summaries, engine stats, driver info
│   │   ├── PadViewModel.cs             Per-slot: visualizer state, mappings, dead zones, FF
│   │   ├── MappingItem.cs              Single mapping row: target, source, recording, options
│   │   ├── MacroItem.cs                Macro definition: trigger, actions, timing
│   │   ├── DevicesViewModel.cs         Device list, raw state display, slot assignment
│   │   ├── DeviceRowViewModel.cs       Single device: identity, status, capabilities
│   │   └── SettingsViewModel.cs        App settings: theme, engine, drivers, diagnostics
│   ├── Views/
│   │   ├── DashboardPage.xaml / .cs    Slot cards, engine card, driver status
│   │   ├── PadPage.xaml / .cs          Visualizer, mapping grid, dead zones, force feedback
│   │   ├── DevicesPage.xaml / .cs      Card-based device list + visual raw input state
│   │   ├── ProfilesPage.xaml / .cs     Per-app profile management
│   │   ├── SettingsPage.xaml / .cs     Theme, engine, drivers, file, diagnostics sections
│   │   ├── AboutPage.xaml / .cs        App info, technology list, license
│   │   ├── ProfileDialog.xaml / .cs    Save/edit profile dialog
│   │   └── CopyFromDialog.xaml / .cs   Copy mappings from another slot
│   ├── Services/
│   │   ├── InputService.cs             Engine ↔ UI bridge: 30Hz DispatcherTimer, state sync
│   │   ├── SettingsService.cs          XML persistence: load/save/reset/reload
│   │   ├── RecorderService.cs          Input recording: baseline → detection → descriptor
│   │   ├── DeviceService.cs            Device assignment and hiding
│   │   ├── DsuMotionServer.cs          DSU/Cemuhook UDP motion server (port 26760)
│   │   └── ForegroundMonitorService.cs Per-app profile switching via foreground window detection
│   ├── Resources/
│   │   ├── ControllerIcons.xaml        XAML icon resource dictionary
│   │   ├── PadForge.ico               Application icon
│   │   ├── SDL3/x64/SDL3.dll          Custom SDL3 fork (WinUSB Switch 2 Pro Controller support)
│   │   ├── SDL3/x64/libusb-1.0.dll    libusb for WinUSB device access
│   │   ├── ViGEmBus_1.22.0_x64_x86_arm64.exe  Embedded ViGEmBus driver installer
│   │   ├── HidHide_1.5.230_x64.exe    Embedded HidHide driver installer
│   │   ├── vJoySetup_v2.2.2.0_Win10_Win11.exe  Embedded vJoy driver installer
│   │   ├── Xbox Series Controller - Front.png
│   │   └── Xbox Series Controller - Top.png
│   ├── Themes/
│   │   └── Generic.xaml               RangeSlider control template
│   └── Properties/
│       └── AssemblyInfo.cs
│
└── tools/
    └── DsuDiag/                  (Console app — DSU protocol diagnostic client)
        ├── DsuDiag.csproj
        └── Program.cs
```

## Prerequisites

- .NET 8 SDK (net8.0-windows)
- Windows 10/11

All native DLLs and driver installers are included in the repository under `PadForge.App/Resources/`.

## NuGet Dependencies

**PadForge.Engine.csproj:**
```
(none — pure P/Invoke, no third-party packages)
```

**PadForge.App.csproj:**
```
ModernWpfUI (>= 0.9.6)
CommunityToolkit.Mvvm (>= 8.2.2)
Nefarius.ViGEm.Client (>= 1.21.256)
```

## Build

```bash
dotnet publish -c Release PadForge.App/PadForge.App.csproj
```

Output: `PadForge.App/bin/Release/net8.0-windows/win-x64/publish/PadForge.exe` (single-file)

> **Note:** Always use `dotnet publish`, not `dotnet build`. The project is configured for single-file publish.

## Runtime Requirements

1. **SDL3.dll** — Included in the repo (`Resources/SDL3/x64/`). This is a custom fork with
   WinUSB support for Switch 2 Pro Controller. Copied to the output directory automatically.

2. **ViGEmBus** (optional) — Required for virtual Xbox 360 and DualShock 4 controller output.
   The app includes a built-in installer or you can install manually from
   https://github.com/nefarius/ViGEmBus/releases.

3. **vJoy** (optional) — Required for virtual joystick output. The app includes a built-in
   installer or you can install manually from
   https://github.com/BrunnerInnovation/vJoy/releases.

4. **HidHide** (optional) — For hiding physical controllers from games. Built-in installer included.

5. **xinput1_4.dll** — Ships with Windows. Used in Step 5 for Xbox 360 slot mask detection.

## Architecture Notes

### Threading Model
- **InputManager** runs a background thread at configurable polling rate (default ~1000Hz).
  Uses hybrid sleep/spin-wait for sub-ms precision.
- **InputService** runs a DispatcherTimer on the UI thread at ~30Hz.
- State transfer: InputManager writes to `CombinedOutputStates[]` arrays;
  InputService reads them and pushes to ViewModels.
- All ViewModel property sets happen on the UI thread.

### 6-Step Pipeline (per cycle)
1. **UpdateDevices** — SDL enumeration, open new, detect disconnections, filter ViGEm devices
2. **UpdateInputStates** — Read axes/buttons/POVs/sensors from SDL; apply force feedback
3. **UpdateOutputStates** — Map CustomInputState → OutputState via PadSetting descriptors
4. **CombineOutputStates** — Merge multiple devices per slot (OR/MAX/largest-magnitude)
   - **4b. EvaluateMacros** — Process macro triggers and actions
5. **VirtualDevices** — Feed ViGEm (Xbox 360 / DS4) and vJoy virtual controllers
6. **RetrieveOutputStates** — Copy combined output for UI display

### Virtual Controller Types
- **Xbox 360** — via ViGEmBus (`Xbox360VirtualController.cs`)
- **DualShock 4** — via ViGEmBus (`DS4VirtualController.cs`)
- **vJoy** — via vJoyInterface.dll P/Invoke (`VJoyVirtualController.cs`)
- Up to 8 virtual controllers (4 Xbox 360 + 4 DS4 max per type, 16 vJoy max)

### Mapping Descriptors
String format: `"[I][H]{Type} {Index} [{Direction}]"`
- `Button 0`, `Axis 1`, `IHAxis 2`, `POV 0 Up`, `Slider 0`
- Prefixes: `I` = inverted, `H` = half-axis, `IH` = inverted half

### Settings File (PadForge.xml)
```xml
<PadForgeSettings>
  <Devices><Device>...</Device></Devices>
  <UserSettings><Setting>...</Setting></UserSettings>
  <PadSettings><PadSetting>...</PadSetting></PadSettings>
  <AppSettings>...</AppSettings>
  <Profiles><ProfileData>...</ProfileData></Profiles>
</PadForgeSettings>
```

### DSU Motion Server
- UDP server on port 26760 (Cemuhook protocol)
- Broadcasts gyro/accelerometer data from SDL sensor-capable controllers
- Compatible with Cemu, Dolphin, and other DSU clients
- Diagnostic tool: `tools/DsuDiag/`
