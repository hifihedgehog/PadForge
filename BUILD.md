# PadForge: Build & Project Reference

## Overview

PadForge is a controller mapping utility (fork of [x360ce](https://github.com/x360ce/x360ce)) rebuilt with:
- **DSU/Cemuhook** motion server for gyro/accelerometer passthrough
- **[HelixToolkit](https://github.com/helix-toolkit/helix-toolkit)** for interactive 3D controller visualization
- **[HIDMaestro](https://github.com/hifihedgehog/HIDMaestro)** as the single virtual-controller backend (Xbox / PlayStation / Nintendo / Extended types)
- **MVVM** architecture with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- **.NET 10 WPF** with [WPF-UI](https://github.com/lepoco/wpfui) Fluent Design
- **[OpenXInput](https://github.com/hifihedgehog/OpenXinput)** XInput shim, embedded in the single-file build
- **[SDL3](https://github.com/libsdl-org/SDL)** (custom fork under `SDL3-build/SDL/` with HIDMaestro filtering) for all device input

2D controller schematics from **[Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack)** by AL2009man (MIT).
3D controller models adapted from **[Handheld Companion](https://github.com/Valkirie/HandheldCompanion)** (CC BY-NC-SA 4.0).
Steam Controller hardware art derives from **Valve's own published CAD** (CC BY-NC-SA 4.0): the 2015 controller from the STEP file in its March 2016 release, the 2026 controller from the solid model and reference drawing shipped with the hardware. The Steam Deck body is Handheld Companion's own mesh. `tools/steam_controller_2015_mesh.py` and `tools/steam_controller_2026_mesh.py` do the conversion, and `tools/overlay_positions.py` builds the 2026 two-dimensional art from the same drawing. The DualShock 3 model is a mesh bought on CGTrader from 3doverstock, split by `tools/dualshock3_mesh.py`, and `tools/dualshock3_art.py` draws its two-dimensional art in the DualShock 4 set's style from a measured front photograph.

## Solution Structure

```
PadForge.sln
├── PadForge.Engine/          (Class library -- net10.0-windows)
│   ├── Common/
│   │   ├── SDL3Minimal.cs         SDL3 P/Invoke declarations
│   │   ├── InputTypes.cs          Enums and constant tables: MapType, ObjectGuid, InputDeviceType, etc.
│   │   ├── SdlDeviceWrapper.cs    SDL joystick/gamepad wrapper (open, read, rumble, GUID)
│   │   ├── SdlKeyboardWrapper.cs  SDL keyboard input wrapper
│   │   ├── SdlMouseWrapper.cs     SDL mouse input wrapper
│   │   ├── ISdlInputDevice.cs     Interface for SDL input devices
│   │   ├── CustomInputState.cs    Unified input state (axes, buttons, POVs, sliders)
│   │   ├── DeviceObjectItem.cs    Device axis/button/POV capability metadata
│   │   ├── ForceFeedbackState.cs  Rumble + SDL haptic state management
│   │   ├── GamepadTypes.cs        Gamepad, TouchpadState, RawHidState, KbmRawState, MidiRawState
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
│   ├── App.xaml / .cs             Entry point, WPF-UI resources, converter registration
│   ├── MainWindow.xaml / .cs      Shell: NavigationView + status bar + page switching
│   │
│   ├── Common/
│   │   ├── SettingsManager.cs     Static: device/setting collections, assignment, defaults
│   │   ├── ControllerIcons.cs     SVG path data for controller type icons
│   │   ├── DriverInstaller.cs     HidHide, MIDI Services and SteamVR install/uninstall, legacy
│   │   │                          ViGEmBus/vJoy removal (HIDMaestro installs its own driver)
│   │   ├── HidHideController.cs   HidHide IOCTL API (blacklist, whitelist, cloaking)
│   │   ├── StartupHelper.cs       Launch-at-logon Task Scheduler task
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
│   │       ├── HMaestroVirtualController.cs   HIDMaestro VC for Xbox / PlayStation / Nintendo / Extended types
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
│   │   ├── ControllerModelXbox360.cs    Xbox 360 mesh loading (31 OBJ files)
│   │   ├── ControllerModelDS4.cs        DualShock 4 mesh loading (37 OBJ files)
│   │   └── (eight more families, DualShock 3 to Xbox Series)
│   │
│   ├── 3DModels/
│   │   ├── DS4/                         DualShock 4 OBJ meshes, one folder per colorway
│   │   ├── XBOX360/                     Xbox 360 OBJ meshes
│   │   └── (eight more families)
│   │
│   ├── Models2D/
│   │   ├── ControllerOverlayLayout.cs   Layout data for 2D overlays
│   │   └── (generated position data)
│   │
│   ├── 2DModels/
│   │   ├── DS4/                         DualShock 4 PNG overlays (29 images)
│   │   ├── XBOX360/                     Xbox 360 PNG overlays (28 images)
│   │   └── (twelve more families)
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
│   │   ├── index.html                Landing page (controller layouts, touchpad, custom, browser gamepad)
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
│   │   ├── */arm64/                   ARM64 copies of the native DLLs, beside the x64 folders of
│   │   │                              SDL3, OpenXInput and VisualCpp, plus Vosk/arm64/libvosk.dll
│   │   ├── HIDMaestro/HIDMaestro.Core.dll  HIDMaestro managed client
│   │   └── HidHide_1.5.230_x64.exe    Embedded HidHide installer
│   │
│   ├── Themes/
│   │   └── Generic.xaml               RangeSlider control template
│   └── Properties/
│       └── AssemblyInfo.cs
│
├── PadForge.SteamWorkshop/        (Class library, net10.0-windows) Steam Workshop config import
├── PadForge.Tests/                xunit suite for the App and the Engine
├── PadForge.SteamWorkshop.Tests/  xunit suite for the Workshop client
├── PadForge.NativeChecks/         Console helper PadForge.Tests runs against the bundled SDL3.dll
│
└── tools/
    ├── DsuDiag/                  DSU/Cemuhook diagnostic client
    │   ├── DsuDiag.csproj
    │   └── Program.cs            Real-time DSU slot data viewer
    ├── Ds4InputDump/             Raw DualShock 4 input dump for the PlayStation VC path
    │   ├── Ds4InputDump.csproj
    │   └── Program.cs
    └── overlay_positions.py      Extract 2D overlay positions from SVG assets
```

## Prerequisites

- .NET 10 SDK
- Windows 10 or Windows 11. An x64 machine builds both the x64 and the ARM64 exe. The `10.0.26100.0` in the target framework names the Windows SDK the build compiles against, restored from NuGet, not a Windows build the machine needs.

All native DLLs, driver installers, and model assets are included in the repository under `PadForge.App/Resources/`, `PadForge.App/3DModels/`, and `PadForge.App/2DModels/`.

## NuGet Dependencies

**PadForge.App.csproj:**
```
CommunityToolkit.Mvvm 8.2.2                        MVVM data binding
Concentus 2.2.2                                    Opus codec for DualSense Bluetooth speaker and microphone audio
HelixToolkit.Core.Wpf 2.27.3                       3D viewport rendering
Microsoft.Windows.Devices.Midi2 1.0.16-rc.3.7      Virtual MIDI device output (from nuget-local/)
NAudio.Wasapi 2.2.1                                WASAPI loopback capture and output
Nefarius.Utilities.DeviceManagement 5.2.0          Driver-store installs for the DualShock 3 Bluetooth stack
System.Management 10.0.11                          WMI queries for the handheld hidden-button learner
System.Speech 10.0.0                               SAPI recognizer for voice macros
Vosk 0.3.38                                        Offline recognizer for voice macros
WPF-UI 4.3.0                                       Fluent Design theme
```

**PadForge.Engine.csproj:**
```
BouncyCastle.Cryptography 2.6.2                    Remote Link pairing and transport cryptography
System.Security.Cryptography.ProtectedData 10.0.9  DPAPI protection for this PC's Remote Link identity key
```

**PadForge.SteamWorkshop.csproj:**
```
SteamKit2 3.4.0                                    Anonymous Steam session for Workshop config import
```

## Build

```bash
dotnet publish -c Release PadForge.App/PadForge.App.csproj
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/PadForge.exe` (single-file, self-contained)

> **Note:** Always use `dotnet publish`, not `dotnet build`. The project is configured for single-file publish with self-contained runtime.

> **Build from the .NET SDK, not Visual Studio's MSBuild.** The 3D art is packed at
> build time by an inline task that needs Brotli, which exists on the .NET SDK's
> runtime and not on the .NET Framework build of MSBuild that Visual Studio uses.
> Building from the IDE fails before any compile with a message naming this. Use the
> `dotnet` CLI, or point Visual Studio at it.

### ARM64 (preliminary, 4.5.1)

```bash
dotnet publish -c Release -r win-arm64 PadForge.App/PadForge.App.csproj
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-arm64/publish/PadForge.exe`. With no `-r` the build is x64, so existing scripts produce what they always have.

Every bundled native binary sits in a folder named for its architecture: `Resources/SDL3/x64` and `Resources/SDL3/arm64`, and the same pair for `OpenXInput` and `VisualCpp`. `Interhaptics` has an `x64` folder only. The `$(NativeArch)` property picks the folder. `NativeBinaryArchitectureTests` reads the PE header of every `.dll` and `.sys` in an `x64` or `arm64` folder under `Resources` and fails when a file's machine type differs from its folder name.

A publish for either architecture is refused if `SDL3.dll` or `libusb-1.0.dll` is missing from `Resources/SDL3/<arch>`, or `xinput1_4.dll` from `Resources/OpenXInput/<arch>`, and an ARM64 publish is refused if `libvosk.dll` is missing from `Resources/Vosk/arm64`. Those `Content` items are conditioned on `Exists`, so without the `RequireBundledNatives` target a missing file would publish anyway, and the auto build runs no tests that would notice. Without `SDL3.dll` the input engine cannot start. Without `libusb-1.0.dll` wired Switch 2 controllers and the GameCube adapter never open. Without the fork's `xinput1_4.dll` SDL loads the system one, and PadForge reads its own virtual controllers back as input. Without the ARM64 `libvosk.dll` voice macros lose the Vosk recognizer on ARM64. A publish for any runtime other than `win-x64` and `win-arm64` is refused too, since it would be handed the x64 libraries. `SDL3.dll` and `xinput1_4.dll` come from builds of the two forks, the ARM64 pair cross-compiled on an x64 machine with `cmake -A ARM64`. A plain `dotnet build` compiles without any of them.

Three features have a native half, and `PadForge.Engine/Common/PlatformSupport.cs` decides each one by the architecture that half follows:

| Feature | Decided by | On ARM64 |
|---|---|---|
| HidHide | Machine architecture | A kernel driver has to match the machine, so the choice follows `OSArchitecture` and not the build. An ARM64 machine gets HidHide's ARM64 driver (`Resources/HidHideArm64/HidHide_ARM64.zip`, driver 1.6.280.0), installed with HidHide's tool (`nefconc.exe`, nefcon 1.20.0, ARM64). `HidHideArm64Installer` follows the order of HidHide's setup. An x64 machine gets the x64 MSI |
| Vosk voice engine | Process architecture | The ARM64 build bundles `Resources/Vosk/arm64/libvosk.dll`. The Vosk package carries the x64 one |
| Razer Sensa HD haptics | Process architecture | Not available. The Interhaptics SDK ships for Win32 and x64 only, and Razer lists Synapse for x86-64 Windows only |

Each rule names the architectures that have the native half and answers false for every other.

`libvosk.dll` for ARM64 is not published by anyone. Vosk's maintainer wrote a Windows ARM64 recipe (vosk-api 1b308a30, `travis/Dockerfile.winaarch64`) and never shipped its output. `tools/build-libvosk-arm64.sh` is that recipe with the same flags and every source pinned to a commit, run from Git Bash with a portable llvm-mingw toolchain, and its header lists each difference from upstream with the reason. It links the C++ runtime statically, so the DLL imports `KERNEL32` and the Universal C Runtime alone, where the x64 one needs three MinGW DLLs beside it. The same recipe aimed at x64 produced recognition output identical to the official x64 library, word timings included. The ARM64 DLL has not run, because the bench is x64. `BundledVoskArm64Tests` checks that it exports every function the managed binding calls and needs no companion DLL.

A HidHide class filter entry that names a driver which is not running can stop every keyboard and mouse from starting. HidHide's x64 setup ships a watchdog service for that. An ARM64 install has none, so `HidHideArm64Installer` adds the filters only after the driver's control device opens, and at startup on an ARM64 machine takes out any filter entry whose service is gone, or is registered and stopped, which is the watchdog's own condition. On removal it takes the filters off before the device node, as HidHide's setup does. If the removal then stops part way with the node still in, it adds the filters back, provided the driver still answers and no command is still running after its three-minute wait. When every add succeeds the state is a complete install again and Uninstall stays on offer, and an add that fails is logged. nefcon's `remove` takes the device node alone. The service and the driver package stay, as they do after HidHide's own uninstall. `HidHideArm64InstallerTests` pins that order and the hashes of both bundled files.

Four more features load a library the vendor's own software installs, and PadForge has no gate for them because the load itself answers. `LogitechGkey.dll` comes in x64 and x86 only (`LogitechGKeyCatalog`), and the SteamVR input service loads `bin\win64\openvr_api.dll` (`OpenVrConsumerService`), so an ARM64 process gets neither. The LIGHTSYNC engine and the OpenXR runtime are whatever the registry names, so they work in an ARM64 process only if the vendor registers an ARM64 one. None of these crashes the app. G-keys, LIGHTSYNC and OpenXR each show a status line, and the SteamVR input service logs the failed load and keeps retrying.

One more ARM64 piece lives in the SDL fork, not in PadForge. The fork's Xbox Elite paddle reader (`SDL_XINPUT_PADDLES`) builds for x64 and, since fork commit a1416320e2, for ARM64. It has two routes. The Bluetooth route uses public WinRT calls. The USB and Xbox Wireless Adapter route reads an undocumented format from the Windows GameInput service, so the fork switches it on only where the files behind that format belong to a version family it has read, by product version: `GameInputSvc.exe` and `GameInput.dll` 0.2309.26100 from revision 8875, `Windows.Gaming.Input.dll` 10.0.26100 from 8737 and `drivers\xboxgip.sys` 10.0.26100 from 8972. `GameInputRedist.dll` is optional, and must be major version 3 when it is there. That is Windows 11 24H2 or 25H2 at build 26100.8973 or 26200.8973 (July 28, 2026) or later. On Windows 10 and older Windows 11 the route stays off, and paddles are read over Bluetooth only. The version resource carries no architecture, so one table serves x64 and ARM64. Until fork commit 5df5eff539 the check was five exact file hashes. They matched the Windows updates dated 2026-08-27 to 2026-09-14 with GameInput redistributable 3.3.221.0, and no ARM64 installation (hifihedgehog/SDL#32). When the route stays off, the joystick's `SDL.joystick.xinput.paddle.error` property holds the reason. PadForge does not read it yet.

Drivers that install into Windows follow the machine. HIDMaestro 1.10.0 and BthPS3 3.2.0 each carry an x64 and an ARM64 payload and install the one that matches, the DualShock 3 WinUSB package is signed with the matching catalog OS, and the Windows MIDI Services download picks the `-arm64` installer on an ARM64 machine.

Of the Visual C++ runtime, both builds bundle `vcruntime140.dll` and `msvcp140.dll`. `SDL3.dll` imports `msvcp140.dll` for the Elite paddle reader, which is C++. Neither item carries an `Exists` condition, so a missing file stops the build and names it. `vcruntime140_1.dll` is x64 only. It holds an exception handler that exists for the x64 ABI alone, and the copy in Microsoft's ARM64 redist folder is an x64 image. `BundledSdlRuntimeImportsTests` reads each bundled `SDL3.dll` for the runtime DLLs it names and fails when one is not in that architecture's `Resources/VisualCpp` folder. It did exactly that when the ARM64 `SDL3.dll` first arrived with the paddle reader in it.

None of the ARM64 path has run on ARM64 hardware. The bench is x64.

## Runtime Requirements

1. **SDL3.dll**: Included in the repo (`Resources/SDL3/<arch>/`). Custom fork with HIDMaestro
   filtering and WinUSB support for Switch 2 Pro Controller. Copied to the output directory
   automatically.

2. **HIDMaestro**: Required for all gamepad-style virtual controllers (Xbox, PlayStation,
   Nintendo, Extended) and the VR controller pair. The app embeds `HIDMaestro.Core.dll`, which
   carries the driver and installs it on first start. No separate install step.

3. **OpenXInput shim** (`xinput1_4.dll`): Custom XInput replacement DLL embedded in the
   single-file build from `Resources/OpenXInput/<arch>/`. Filters HIDMaestro virtual
   controllers out of PadForge's own XInput view. SDL loads it by name, and `App.OnStartup`
   puts the single-file extraction folder on the DLL search path with `SetDllDirectory`, so
   this copy wins over System32's. Do NOT ship the fork's `devobj.dll`: it is a link-time
   stub, and bundling it once hijacked the real System32 devobj.dll process-wide and crashed
   setupapi.

4. **HidHide** (optional): For hiding physical controllers from games. Built-in installer included.

## Architecture Notes

### Threading Model
- **InputManager** runs a background thread at configurable polling rate (default ~1000Hz).
  Uses hybrid sleep/spin-wait for sub-ms precision.
- **InputService** runs a DispatcherTimer on the UI thread at ~30Hz.
- State transfer: InputManager writes to `CombinedOutputStates[]` and the per-type raw arrays
  (`CombinedRawHidStates[]`, `CombinedKbmRawStates[]`, `CombinedMidiRawStates[]`,
  `CombinedVrRawStates[]`, `CombinedTouchpadStates[]`). InputService reads them and pushes to ViewModels.
- All ViewModel property sets happen on the UI thread.

### 6-Step Pipeline (per cycle)
1. **UpdateDevices**: SDL enumeration, open new, detect disconnections, filter HIDMaestro virtuals
2. **UpdateInputStates**: Read axes/buttons/POVs/sensors from SDL, and apply force feedback + haptic
3. **UpdateOutputStates**: Map CustomInputState -> OutputState through the slot's `MappingSet` rows, or through the PadSetting descriptors when the set has no rows
4. **CombineOutputStates**: Merge multiple devices per slot (OR/MAX/largest-magnitude)
   - **4b. EvaluateMacros**: Process macro triggers and actions (gamepad + extended paths)
5. **VirtualDevices**: Submit state to HIDMaestro via `HMController.SubmitState` / `SubmitRawReport`. KBM and MIDI VCs emit through their respective backends
6. **RetrieveOutputStates**: Copy combined output for UI display

### Virtual Controller Types
All gamepad-style virtuals run on HIDMaestro via `HMaestroVirtualController.cs`:
- **Xbox**: Xbox-family profiles (Xbox 360, Xbox One, Xbox Series, Elite, Adaptive), up to `MaxPads` (16) simultaneous (XInput visibility caps at 4)
- **PlayStation**: DualShock and DualSense profiles, up to 16 simultaneous
- **Nintendo**: the Switch Pro Controller and Switch 2 Pro Controller profiles, up to 16 simultaneous
- **Extended**: every other offered HIDMaestro profile (third-party pads, wheels, flight sticks) plus fully custom HID descriptors, up to 16 simultaneous

HIDMaestro ships 231 profiles. PadForge offers the 133 that carry a captured HID descriptor (`HMProfile.IsDeployable`), split into these four buckets by `HMaestroProfileCatalog`.

Non-gamepad virtuals:
- **KeyboardMouse**: `KeyboardMouseVirtualController.cs`, up to 16 simultaneous
- **MIDI**: `MidiVirtualController.cs` via Windows MIDI Services 2, up to 16 simultaneous
- **VR**: `HMaestroVRController.cs`, one SteamVR left and right hand pair through HIDMaestro's OpenVR driver, one slot at most

### Mapping Descriptors
String format: `"[I][H]{Type} {Index} [{Direction}]"`
- `Button 0`, `Axis 1`, `IHAxis 2`, `POV 0 Up`, `Slider 0`
- Prefixes: `I` = inverted, `H` = half-axis, `IH` = inverted half
- The prefixes are the legacy form. A `MappingSource` stores `Invert` and `HalfAxis` as their own attributes, and `SourceCoercion.StripLegacyPrefix` still reads the prefixed strings older settings carry

### Controller Visualization
- **3D View** (`ControllerModelView`): HelixToolkit.WPF viewport with per-part OBJ meshes for ten
  controller families (the `Models3D/` classes). Mouse/touch rotation, zoom, pan.
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
  <Profiles><Profile>...</Profile></Profiles>
  <!-- plus SlotMappingSets, DeviceTunings, SoundPackages, NfcTags and other sections -->
</PadForgeSettings>
```

### DSU Motion Server
- UDP server on port 26760 by default (Cemuhook protocol)
- Sends gyro/accelerometer data from SDL sensor-capable controllers to subscribed clients
- Compatible with Cemu, Dolphin, and other DSU clients
- Diagnostic tool: `tools/DsuDiag/`

### Diagnostic Tools
- **DsuDiag** (`tools/DsuDiag/`): Real-time DSU protocol client showing per-slot motion data
- **Ds4InputDump** (`tools/Ds4InputDump/`): Raw DualShock 4 and DualSense input dump for debugging the PlayStation VC path
