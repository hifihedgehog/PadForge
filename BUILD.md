# PadForge -- Build & Project Reference

## Overview

PadForge is a controller mapping utility (fork of [x360ce](https://github.com/x360ce/x360ce)) rebuilt with:
- **[SDL3](https://github.com/libsdl-org/SDL)** (custom fork under `SDL3-build/SDL/` with HIDMaestro filtering) for all device input
- **[HIDMaestro](https://github.com/hifihedgehog/HIDMaestro)** as the single virtual-controller backend (Microsoft / PlayStation / Extended types)
- **[OpenXInput](https://github.com/hifihedgehog/OpenXinput)** XInput shim, embedded in the single-file build
- **[HelixToolkit](https://github.com/helix-toolkit/helix-toolkit)** for interactive 3D controller visualization
- **DSU/Cemuhook** motion server for gyro/accelerometer passthrough
- **.NET 10 WPF** with [WPF-UI](https://github.com/lepoco/wpfui) Fluent Design
- **MVVM** architecture with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)

3D controller models adapted from **[Handheld Companion](https://github.com/Valkirie/HandheldCompanion)** (CC BY-NC-SA 4.0).
2D controller schematics from **[Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack)** by AL2009man (MIT).

## Solution Structure

```
PadForge.sln
├── PadForge.Engine/          (Class library -- net10.0-windows)
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
│   │   ├── GamepadTypes.cs        Gamepad/OutputState/ExtendedRawState types
│   │   ├── VirtualControllerTypes.cs  IVirtualController + VirtualControllerType enum
│   │   ├── RawInputListener.cs    Windows Raw Input listener
│   │   └── InputHookManager.cs    WH_KEYBOARD_LL / WH_MOUSE_LL input suppression hooks
│   ├── Data/
│   │   ├── UserDevice.cs          Physical device record (serializable + runtime)
│   │   ├── UserSetting.cs         Device-to-slot link (serializable)
│   │   └── PadSetting.cs          Mapping configuration (mappings, deadzones, FF)
│   └── Properties/
│       └── AssemblyInfo.cs
│
├── PadForge.App/             (WPF Application -- net10.0-windows10.0.26100.0)
│   ├── App.xaml / .cs             Entry point, ModernWpf resources, converter registration
│   ├── MainWindow.xaml / .cs      Shell: NavigationView + status bar + page switching
│   │
│   ├── Common/
│   │   ├── SettingsManager.cs     Static: device/setting collections, assignment, defaults
│   │   ├── ControllerIcons.cs     SVG path data for controller type icons
│   │   ├── DriverInstaller.cs     HIDMaestro, HidHide driver install/uninstall
│   │   ├── HidHideController.cs   HidHide IOCTL API (blacklist, whitelist, cloaking)
│   │   ├── StartupHelper.cs       Windows startup registry management
│   │   ├── VirtualKey.cs          Virtual key code definitions
│   │   └── Input/
│   │       ├── InputManager.cs                          Main partial: background thread, pipeline
│   │       ├── InputManager.Step1.UpdateDevices.cs      SDL enumeration, HIDMaestro filtering
│   │       ├── InputManager.Step2.UpdateInputStates.cs  State reading + force feedback
│   │       ├── InputManager.Step3.UpdateOutputStates.cs CustomInputState -> OutputState mapping
│   │       ├── InputManager.Step4.CombineOutputStates.cs  Multi-device merge per slot
│   │       ├── InputManager.Step4b.EvaluateMacros.cs    Macro evaluation (gamepad + extended)
│   │       ├── InputManager.Step5.VirtualDevices.cs     Virtual controller output (HIDMaestro / KBM / MIDI)
│   │       ├── InputManager.Step6.RetrieveOutputStates.cs  Copy combined output for UI
│   │       ├── HMaestroVirtualController.cs   HIDMaestro VC for Microsoft / PlayStation / Extended types
│   │       ├── KeyboardMouseVirtualController.cs  Virtual keyboard + mouse output
│   │       └── MidiVirtualController.cs       Virtual MIDI device output
│   │
│   ├── Views/
│   │   ├── DashboardPage.xaml / .cs         Slot cards, engine stats, driver status
│   │   ├── PadPage.xaml / .cs               Mapping grid, deadzones, force feedback, macros
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
│   │   ├── PadViewModel.cs             Per-slot: visualizer, mappings, deadzones, macros
│   │   ├── MappingItem.cs              Single mapping row: target, source, recording, options
│   │   ├── MacroItem.cs                Macro: trigger, actions, timing, button style, extended targets
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
│   │   ├── ForegroundMonitorService.cs Per-app profile switching via foreground detection
│   │   └── WebControllerServer.cs     Embedded HTTP+WebSocket server for browser virtual controllers
│   │
│   ├── WebAssets/
│   │   ├── index.html                Landing page (Xbox 360 / DS4 layout selection)
│   │   ├── controller.html           Controller UI shell (dynamic PNG overlay layout)
│   │   ├── css/controller.css        Dark responsive touch-optimized styles
│   │   ├── js/controller_client.js   WebSocket client + touch input handling
│   │   └── js/nipplejs.min.js        Virtual joystick library for analog sticks
│   │
│   ├── Converter/                      WPF value converters (bool, axis, visibility, etc.)
│   ├── Controls/
│   │   └── RangeSlider.cs              Custom deadzone range slider control
│   │
│   ├── Resources/
│   │   ├── ControllerIcons.xaml        XAML icon resource dictionary
│   │   ├── PadForge.ico               Application icon
│   │   ├── SDL3/x64/SDL3.dll          Custom SDL3 fork (HIDMaestro filter, Switch 2 Pro)
│   │   ├── SDL3/x64/libusb-1.0.dll    libusb for WinUSB device access
│   │   ├── OpenXInput/x64/xinput1_4.dll  Custom XInput shim (filters HIDMaestro virtuals from PadForge's own view)
│   │   ├── OpenXInput/x64/devobj.dll  Companion DLL for the XInput shim
│   │   ├── HIDMaestro/HIDMaestro.Core.dll  HIDMaestro managed client
│   │   ├── HidHide_1.5.230_x64.exe    Embedded HidHide installer
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
    ├── Ds4InputDump/             Raw DualShock 4 input dump for the PlayStation VC path
    │   ├── Ds4InputDump.csproj
    │   └── Program.cs
    ├── vJoy/                     Legacy v2 vJoy diagnostic harnesses (kept for history; v3 uses HIDMaestro)
    └── overlay_positions.py      Extract 2D overlay positions from SVG assets
```

## Prerequisites

- .NET 10 SDK
- Windows 10 (build 26100+) or Windows 11 (x64)

All native DLLs, driver installers, and model assets are included in the repository under `PadForge.App/Resources/`, `PadForge.App/Models3D/`, and `PadForge.App/2DModels/`.

## NuGet Dependencies

**PadForge.Engine.csproj:**
```
(none -- pure P/Invoke, no third-party packages)
```

**PadForge.App.csproj:**
```
WPF-UI (>= 4.2.0)                       Fluent Design theme
HelixToolkit.Core.Wpf (>= 2.27.3)       3D viewport rendering
CommunityToolkit.Mvvm (>= 8.2.2)        MVVM data binding
Microsoft.Windows.Devices.Midi2 (>= 1.0.16-rc.3.7)  Virtual MIDI device output
NAudio.Wasapi (>= 2.2.1)                WASAPI loopback for audio bass rumble
```

## Build

```bash
dotnet publish -c Release PadForge.App/PadForge.App.csproj
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/PadForge.exe` (single-file, self-contained)

> **Note:** Always use `dotnet publish`, not `dotnet build`. The project is configured for single-file publish with self-contained runtime.

## Runtime Requirements

1. **SDL3.dll** -- Included in the repo (`Resources/SDL3/x64/`). Custom fork with HIDMaestro
   filtering and WinUSB support for Switch 2 Pro Controller. Copied to the output directory
   automatically.

2. **HIDMaestro** -- Required for all gamepad-style virtual controllers (Microsoft, PlayStation,
   Extended). The app embeds the HIDMaestro installer and managed client; no separate install step.

3. **OpenXInput shim** (`xinput1_4.dll`, `devobj.dll`) -- Custom XInput replacement DLLs embedded
   in the single-file build under `Resources/OpenXInput/x64/`. Filters HIDMaestro virtual
   controllers out of PadForge's own XInput view. Loaded via `SetDllDirectory` preload.

4. **HidHide** (optional) -- For hiding physical controllers from games. Built-in installer included.

## Architecture Notes

### Threading Model
- **InputManager** runs a background thread at configurable polling rate (default ~1000Hz).
  Uses hybrid sleep/spin-wait for sub-ms precision.
- **InputService** runs a DispatcherTimer on the UI thread at ~30Hz.
- State transfer: InputManager writes to `CombinedOutputStates[]` and `CombinedExtendedRawStates[]`;
  InputService reads them and pushes to ViewModels.
- All ViewModel property sets happen on the UI thread.

### 6-Step Pipeline (per cycle)
1. **UpdateDevices** -- SDL enumeration, open new, detect disconnections, filter HIDMaestro virtuals
2. **UpdateInputStates** -- Read axes/buttons/POVs/sensors from SDL; apply force feedback + haptic
3. **UpdateOutputStates** -- Map CustomInputState -> OutputState via PadSetting descriptors
4. **CombineOutputStates** -- Merge multiple devices per slot (OR/MAX/largest-magnitude)
   - **4b. EvaluateMacros** -- Process macro triggers and actions (gamepad + extended paths)
5. **VirtualDevices** -- Submit state to HIDMaestro via `HMContext.SubmitState` / `SubmitRawReport`; KBM and MIDI VCs emit through their respective backends
6. **RetrieveOutputStates** -- Copy combined output for UI display

### Virtual Controller Types
All gamepad-style virtuals run on HIDMaestro via `HMaestroVirtualController.cs`:
- **Microsoft** -- Xbox 360 layout, up to `MaxPads` (16) simultaneous (XInput visibility caps at 4)
- **PlayStation** -- DualShock 4 layout, up to 16 simultaneous
- **Extended** -- Fully custom HID descriptors, up to 16 simultaneous

Non-gamepad virtuals:
- **KeyboardMouse** -- `KeyboardMouseVirtualController.cs`, up to 16 simultaneous
- **MIDI** -- `MidiVirtualController.cs` via Windows MIDI Services 2, up to 16 simultaneous

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
- **Ds4InputDump** (`tools/Ds4InputDump/`) -- Raw DualShock 4 input dump for debugging the PlayStation VC path
