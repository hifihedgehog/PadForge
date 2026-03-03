# PadForge -- Build & Project Reference

## Overview

PadForge is a modern controller mapping utility (fork of [x360ce](https://github.com/x360ce/x360ce)) rebuilt with:
- **[SDL3](https://github.com/libsdl-org/SDL)** for all device input (replaces DirectInput/SharpDX/XInput enumeration)
- **[ViGEmBus](https://github.com/nefarius/ViGEmBus)** for virtual Xbox 360 and DualShock 4 controller output
- **[vJoy](https://github.com/BrunnerInnovation/vJoy)** for virtual joystick output with custom HID descriptors and force feedback
- **[HelixToolkit](https://github.com/helix-toolkit/helix-toolkit)** for interactive 3D controller visualization
- **DSU/Cemuhook** motion server for gyro/accelerometer passthrough
- **.NET 8 WPF** with [ModernWpf](https://github.com/Kinnara/ModernWpf) Fluent Design
- **MVVM** architecture with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)

3D controller models adapted from **[Handheld Companion](https://github.com/Valkirie/HandheldCompanion)** (CC BY-NC-SA 4.0).
2D controller schematics from **[Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack)** by AL2009man (MIT).

## Solution Structure

```
PadForge.sln
├── PadForge.Engine/          (Class library -- net8.0-windows)
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
│   │   ├── ForceFeedbackState.cs  Rumble + SDL haptic state management
│   │   ├── GamepadTypes.cs        Gamepad/OutputState/VJoyRawState types
│   │   ├── VirtualControllerTypes.cs  IVirtualController + VirtualControllerType enum
│   │   ├── RawInputListener.cs    Windows Raw Input listener
│   │   └── RumbleLogger.cs        Diagnostic rumble event logger
│   ├── Data/
│   │   ├── UserDevice.cs          Physical device record (serializable + runtime)
│   │   ├── UserSetting.cs         Device-to-slot link (serializable)
│   │   └── PadSetting.cs          Mapping configuration (mappings, dead zones, FF)
│   └── Properties/
│       └── AssemblyInfo.cs
│
├── PadForge.App/             (WPF Application -- net8.0-windows)
│   ├── App.xaml / .cs             Entry point, ModernWpf resources, converter registration
│   ├── MainWindow.xaml / .cs      Shell: NavigationView + status bar + page switching
│   │
│   ├── Common/
│   │   ├── SettingsManager.cs     Static: device/setting collections, assignment, defaults
│   │   ├── ControllerIcons.cs     SVG path data for controller type icons
│   │   ├── DriverInstaller.cs     ViGEmBus, HidHide, vJoy driver install/uninstall
│   │   ├── StartupHelper.cs       Windows startup registry management
│   │   ├── VirtualKey.cs          Virtual key code definitions
│   │   └── Input/
│   │       ├── InputManager.cs                          Main partial: background thread, pipeline
│   │       ├── InputManager.Step1.UpdateDevices.cs      SDL enumeration, ViGEm filtering
│   │       ├── InputManager.Step2.UpdateInputStates.cs  State reading + force feedback
│   │       ├── InputManager.Step3.UpdateOutputStates.cs CustomInputState -> OutputState mapping
│   │       ├── InputManager.Step4.CombineOutputStates.cs  Multi-device merge per slot
│   │       ├── InputManager.Step4b.EvaluateMacros.cs    Macro evaluation (gamepad + custom vJoy)
│   │       ├── InputManager.Step5.VirtualDevices.cs     Virtual controller output (ViGEm + vJoy)
│   │       ├── InputManager.Step6.RetrieveOutputStates.cs  Copy combined output for UI
│   │       ├── Xbox360VirtualController.cs    ViGEm Xbox 360 virtual controller
│   │       ├── DS4VirtualController.cs        ViGEm DualShock 4 virtual controller
│   │       └── VJoyVirtualController.cs       vJoy virtual joystick (P/Invoke, HID descriptors, FFB)
│   │
│   ├── Views/
│   │   ├── DashboardPage.xaml / .cs         Slot cards, engine stats, driver status
│   │   ├── PadPage.xaml / .cs               Mapping grid, dead zones, force feedback, macros
│   │   ├── DevicesPage.xaml / .cs           Card-based device list + visual raw input state
│   │   ├── ProfilesPage.xaml / .cs          Per-app profile management and auto-switching
│   │   ├── SettingsPage.xaml / .cs          Theme, engine, drivers, diagnostics
│   │   ├── AboutPage.xaml / .cs             App info, technology list, license
│   │   ├── ControllerModelView.xaml / .cs   3D interactive HelixToolkit viewport
│   │   ├── ControllerModel2DView.xaml / .cs 2D Canvas-based schematic with PNG overlays
│   │   ├── ControllerSchematicView.xaml / .cs  Alternative 2D schematic layout
│   │   ├── ProfileDialog.xaml / .cs         Save/edit profile dialog
│   │   └── CopyFromDialog.xaml / .cs        Copy mappings from another slot
│   │
│   ├── Models3D/
│   │   ├── ControllerModelBase.cs       Abstract base: OBJ loading, button map, materials
│   │   ├── ControllerModelXbox360.cs    Xbox 360 mesh loading (25 OBJ files)
│   │   ├── ControllerModelDS4.cs        DualShock 4 mesh loading (36 OBJ files)
│   │   └── 3DModels/
│   │       ├── DS4/                     DualShock 4 OBJ meshes
│   │       └── XBOX360/                 Xbox 360 OBJ meshes
│   │
│   ├── Models2D/
│   │   ├── ControllerOverlayLayout.cs   Layout data for 2D overlays
│   │   └── (generated position data)
│   │
│   ├── 2DModels/
│   │   ├── DS4/                         DualShock 4 PNG overlays (16 images)
│   │   └── XBOX360/                     Xbox 360 PNG overlays (21 images)
│   │
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs            INotifyPropertyChanged base
│   │   ├── MainViewModel.cs            Root: navigation, pads, engine status, commands
│   │   ├── DashboardViewModel.cs       Overview: slot summaries, engine stats, driver info
│   │   ├── PadViewModel.cs             Per-slot: visualizer, mappings, dead zones, macros
│   │   ├── MappingItem.cs              Single mapping row: target, source, recording, options
│   │   ├── MacroItem.cs                Macro: trigger, actions, timing, button style, custom vJoy
│   │   ├── DevicesViewModel.cs         Device list, raw state display, slot assignment
│   │   ├── DeviceRowViewModel.cs       Single device: identity, status, capabilities
│   │   └── SettingsViewModel.cs        App settings: theme, engine, drivers, diagnostics
│   │
│   ├── Services/
│   │   ├── InputService.cs             Engine <-> UI bridge: 30Hz DispatcherTimer, state sync
│   │   ├── SettingsService.cs          XML persistence: load/save/reset/reload
│   │   ├── RecorderService.cs          Input recording: baseline -> detection -> descriptor
│   │   ├── DeviceService.cs            Device assignment and hiding
│   │   ├── DsuMotionServer.cs          DSU/Cemuhook UDP motion server (port 26760)
│   │   └── ForegroundMonitorService.cs Per-app profile switching via foreground detection
│   │
│   ├── Converter/                      WPF value converters (bool, axis, visibility, etc.)
│   ├── Controls/
│   │   └── RangeSlider.cs              Custom dead zone range slider control
│   │
│   ├── Resources/
│   │   ├── ControllerIcons.xaml        XAML icon resource dictionary
│   │   ├── PadForge.ico               Application icon
│   │   ├── SDL3/x64/SDL3.dll          Custom SDL3 fork (WinUSB Switch 2 Pro Controller)
│   │   ├── SDL3/x64/libusb-1.0.dll    libusb for WinUSB device access
│   │   ├── ViGEmBus_1.22.0_x64_x86_arm64.exe  Embedded ViGEmBus installer
│   │   ├── HidHide_1.5.230_x64.exe    Embedded HidHide installer
│   │   ├── vJoyDriver.zip             Embedded vJoy driver (vjoy.sys, hidkmdf.sys, vjoy.inf)
│   │   └── Xbox Series Controller - *.png  Dashboard controller images
│   │
│   ├── Themes/
│   │   └── Generic.xaml               RangeSlider control template
│   └── Properties/
│       └── AssemblyInfo.cs
│
└── tools/
    ├── DsuDiag/                  DSU/Cemuhook diagnostic client
    │   ├── DsuDiag.csproj
    │   └── Program.cs            Real-time DSU slot data viewer
    ├── vJoy/
    │   ├── Test/                 vJoy device creation and input test tool
    │   │   ├── VJoyTest.csproj
    │   │   ├── Program.cs        Configurable axes/buttons/POVs with WinMM readback
    │   │   └── test_create_device.ps1
    │   ├── FfbTest/              DirectInput force feedback test tool
    │   │   ├── FfbTest.csproj
    │   │   └── Program.cs        ConstantForce/Sine effects with interactive menu
    │   └── SDK/                  vJoy SDK reference
    │       └── SDK.zip
    ├── overlay_positions.py      Extract 2D overlay positions from SVG assets
    └── cleanup_vjoy.ps1          vJoy cleanup utility
```

## Prerequisites

- .NET 8 SDK (net8.0-windows)
- Windows 10 or 11 (x64)

All native DLLs, driver installers, and model assets are included in the repository under `PadForge.App/Resources/`, `PadForge.App/Models3D/`, and `PadForge.App/2DModels/`.

## NuGet Dependencies

**PadForge.Engine.csproj:**
```
(none -- pure P/Invoke, no third-party packages)
```

**PadForge.App.csproj:**
```
ModernWpfUI (>= 0.9.6)           Fluent Design theme
HelixToolkit.Core.Wpf (>= 2.27.3) 3D viewport rendering
CommunityToolkit.Mvvm (>= 8.2.2)  MVVM data binding
Nefarius.ViGEm.Client (>= 1.21.256) ViGEm virtual controller client
```

## Build

```bash
dotnet publish -c Release PadForge.App/PadForge.App.csproj
```

Output: `PadForge.App/bin/Release/net8.0-windows/win-x64/publish/PadForge.exe` (single-file, self-contained)

> **Note:** Always use `dotnet publish`, not `dotnet build`. The project is configured for single-file publish with self-contained runtime.

## Runtime Requirements

1. **SDL3.dll** -- Included in the repo (`Resources/SDL3/x64/`). This is a custom fork with
   WinUSB support for Switch 2 Pro Controller. Copied to the output directory automatically.

2. **ViGEmBus** (optional) -- Required for virtual Xbox 360 and DualShock 4 controller output.
   The app includes a built-in installer or you can install manually from
   https://github.com/nefarius/ViGEmBus/releases.

3. **vJoy** (optional) -- Required for virtual joystick output. The app includes embedded driver
   files and handles installation/device creation automatically. Manual install available from
   https://github.com/BrunnerInnovation/vJoy/releases.

4. **HidHide** (optional) -- For hiding physical controllers from games. Built-in installer included.

5. **xinput1_4.dll** -- Ships with Windows. Used in Step 5 for Xbox 360 slot mask detection.

## Architecture Notes

### Threading Model
- **InputManager** runs a background thread at configurable polling rate (default ~1000Hz).
  Uses hybrid sleep/spin-wait for sub-ms precision.
- **InputService** runs a DispatcherTimer on the UI thread at ~30Hz.
- State transfer: InputManager writes to `CombinedOutputStates[]` and `CombinedVJoyRawStates[]`;
  InputService reads them and pushes to ViewModels.
- All ViewModel property sets happen on the UI thread.

### 6-Step Pipeline (per cycle)
1. **UpdateDevices** -- SDL enumeration, open new, detect disconnections, filter ViGEm/vJoy devices
2. **UpdateInputStates** -- Read axes/buttons/POVs/sensors from SDL; apply force feedback + haptic
3. **UpdateOutputStates** -- Map CustomInputState -> OutputState via PadSetting descriptors
4. **CombineOutputStates** -- Merge multiple devices per slot (OR/MAX/largest-magnitude)
   - **4b. EvaluateMacros** -- Process macro triggers and actions (dual path: gamepad + custom vJoy)
5. **VirtualDevices** -- Feed ViGEm (Xbox 360 / DS4) and vJoy virtual controllers
6. **RetrieveOutputStates** -- Copy combined output for UI display

### Virtual Controller Types
- **Xbox 360** -- via ViGEmBus (`Xbox360VirtualController.cs`), up to 4 simultaneous
- **DualShock 4** -- via ViGEmBus (`DS4VirtualController.cs`), up to 4 simultaneous
- **vJoy** -- via vJoyInterface.dll P/Invoke (`VJoyVirtualController.cs`), up to 16 simultaneous
  - Supports Xbox 360 and DualShock 4 presets, or fully custom HID descriptors (up to 16 axes, 128 buttons, 4 POVs)
  - Force feedback via DirectInput IOCTL path (ConstantForce, Sine, LeftRight effects)
  - Single device node architecture with dynamic registry-based descriptor management

### Mapping Descriptors
String format: `"[I][H]{Type} {Index} [{Direction}]"`
- `Button 0`, `Axis 1`, `IHAxis 2`, `POV 0 Up`, `Slider 0`
- Prefixes: `I` = inverted, `H` = half-axis, `IH` = inverted half

### Controller Visualization
- **3D View** (`ControllerModelView`): HelixToolkit.WPF viewport with OBJ meshes from Handheld Companion.
  Xbox 360 (25 parts) and DualShock 4 (36 parts). Mouse/touch rotation, zoom, pan.
- **2D View** (`ControllerModel2DView`): Canvas with PNG overlays from Gamepad-Asset-Pack.
  Button/stick/trigger state shown via opacity toggling on overlay images.

### Settings File (PadForge.xml)
```xml
<PadForgeSettings>
  <Devices><Device>...</Device></Devices>
  <UserSettings><Setting>...</Setting></UserSettings>
  <PadSettings><PadSetting>...</PadSetting></PadSettings>
  <AppSettings>...</AppSettings>
  <Macros><Macro>...</Macro></Macros>
  <Profiles><ProfileData>...</ProfileData></Profiles>
</PadForgeSettings>
```

### DSU Motion Server
- UDP server on port 26760 (Cemuhook protocol)
- Broadcasts gyro/accelerometer data from SDL sensor-capable controllers
- Compatible with Cemu, Dolphin, and other DSU clients
- Diagnostic tool: `tools/DsuDiag/`

### Diagnostic Tools
- **DsuDiag** (`tools/DsuDiag/`) -- Real-time DSU protocol client showing per-slot motion data
- **VJoyTest** (`tools/vJoy/Test/`) -- vJoy device creation with configurable axes/buttons/POVs and WinMM readback
- **FfbTest** (`tools/vJoy/FfbTest/`) -- DirectInput force feedback effects (ConstantForce, Sine) with interactive menu
