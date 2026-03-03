# PadForge

Modern controller mapping utility for Windows. Maps any controller, keyboard, or mouse to virtual Xbox 360, DualShock 4, or custom DirectInput controllers that games see as real hardware.

Built with SDL3, ViGEmBus, vJoy, HelixToolkit, .NET 8 WPF, and Fluent Design. Modern fork of [x360ce](https://github.com/x360ce/x360ce).

## Features

- **Any input to any virtual controller** -- Joysticks, gamepads, keyboards, and mice map to Xbox 360, DualShock 4, or fully custom DirectInput controllers
- **Up to 16 virtual controller slots** -- Mix and match Xbox 360, DualShock 4, and custom DirectInput across up to 16 simultaneous slots, each combining input from multiple physical devices
- **3D and 2D controller visualization** -- Interactive 3D controller model (rotate, zoom, pan) and flat 2D schematic, both showing live button, stick, and trigger state in real time
- **Interactive mapping** -- Record mappings by pressing buttons on your controller, or use "Map All" for quick setup. Auto-mapping for recognized gamepads
- **Dead zones and response curves** -- Per-axis dead zone, anti-dead zone, and linear response curve for sticks and triggers, with live preview
- **Force feedback** -- Rumble passthrough with per-motor strength, overall gain, and motor swap. Haptic fallback for devices without native rumble. DirectInput force feedback for custom controllers
- **Macro system** -- Trigger combos that execute button presses, key presses, delays, and axis manipulation. Supports up to 128 buttons for custom DirectInput controllers, with repeat modes and input device or output controller trigger sources
- **Per-app profile switching** -- Automatically switch controller configurations when specific applications gain focus
- **DSU/Cemuhook motion server** -- Broadcasts gyro and accelerometer data over UDP (port 26760) for emulators like Cemu and Dolphin
- **Driver management** -- One-click install/uninstall for ViGEmBus, HidHide, and vJoy
- **System tray** -- Minimize to tray, start minimized, start at login
- **Portable** -- Single-file self-contained executable

## Screenshots

### Dashboard
![Dashboard](screenshots/dashboard.jpg)
At-a-glance overview showing input engine status (polling rate, device count), virtual controller slots with type badges, DSU motion server, and driver status.

### 3D Controller Visualization
![Controller](screenshots/controller.jpg)
Interactive 3D controller model rendered with HelixToolkit. Rotate, zoom, and pan to inspect from any angle. Buttons, sticks, and triggers highlight in real time. Includes motor activity meters and a "Map All" button for quick auto-mapping. Toggle between 3D and 2D views.

### Button and Axis Mappings
![Mappings](screenshots/mappings.jpg)
Full mapping grid where each output (buttons, sticks, triggers, D-pad) can be assigned to any source input. Record a mapping by pressing a button on your device, or edit the descriptor directly. Supports inversion and half-axis options. Output labels adapt to controller type (DS4 shown: Cross, Circle, Square, Triangle, L1, R1, etc.).

### Stick Dead Zones
![Sticks](screenshots/sticks.jpg)
Per-axis dead zone, anti-dead zone, and linear response curve sliders for left and right thumbsticks, with live circular previews showing current stick position and the active dead zone region.

### Trigger Dead Zones
![Triggers](screenshots/triggers.jpg)
Range sliders, anti-dead zone, and live value bars for left and right triggers showing real-time processed output.

### Force Feedback / Rumble
![Force Feedback](screenshots/force-feedback.jpg)
Rumble configuration with overall gain, per-motor strength sliders, and a swap option. Test Rumble button for quick verification, plus live motor activity bars showing current rumble intensity.

### Macro Editor
![Macros](screenshots/macros.jpg)
Create macros triggered by button combinations from either the output controller or a physical input device. Each macro supports an action sequence of button presses, key presses, delays, and axis manipulation. Configurable fire mode (on press, on release, repeat) with type-aware button names for Xbox 360, DualShock 4, and custom DirectInput controllers (up to 128 buttons).

### Device List
![Devices](screenshots/devices.jpg)
Card-based device list showing all detected gamepads, joysticks, keyboards, and mice with status, type, VID/PID, and slot assignment. Select a device to see its raw input state -- axes as progress bars, buttons as indicator circles, POV as a compass, and gyro/accelerometer values.

### Settings
![Settings](screenshots/settings.jpg)
Appearance theme, input engine options (auto-start, background polling, configurable polling interval), and window behavior (system tray, start minimized, start at login).

### Settings -- Drivers and Diagnostics
![Settings -- Drivers](screenshots/settings-drivers.jpg)
One-click ViGEmBus, HidHide, and vJoy driver management with version info, settings file controls (save, reload, reset, open folder), and diagnostics showing app version, .NET runtime, and SDL version.

### About
![About](screenshots/about.jpg)
Application information, technology stack, and license details.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (included in the single-file publish)

Optional drivers (PadForge can install all of these for you):

- [ViGEmBus](https://github.com/nefarius/ViGEmBus) -- Virtual Xbox 360 and DualShock 4 output
- [vJoy](https://github.com/BrunnerInnovation/vJoy) -- Custom DirectInput joystick/gamepad output with configurable axes, buttons, POVs, and force feedback
- [HidHide](https://github.com/nefarius/HidHide) -- Hide physical controllers from games to prevent double input

## Build

```bash
dotnet publish PadForge.App/PadForge.App.csproj -c Release
```

Output: `PadForge.App/bin/Release/net8.0-windows/win-x64/publish/PadForge.exe`

See [BUILD.md](BUILD.md) for full project structure, architecture details, and developer reference.

## Upstream Projects and Acknowledgments

PadForge stands on the shoulders of these projects. Please consider supporting them:

| Project | Role in PadForge | License |
|---|---|---|
| [x360ce](https://github.com/x360ce/x360ce) | Original codebase this project was forked from | MIT |
| [SDL3](https://github.com/libsdl-org/SDL) | All device input -- joystick, gamepad, keyboard, mouse, sensors | zlib |
| [ViGEmBus](https://github.com/nefarius/ViGEmBus) | Virtual Xbox 360 and DualShock 4 controller driver | MIT |
| [Nefarius.ViGEm.Client](https://github.com/nefarius/ViGEm.NET) | .NET client library for ViGEmBus | MIT |
| [vJoy](https://github.com/BrunnerInnovation/vJoy) | Custom DirectInput joystick/gamepad driver with configurable HID descriptors and force feedback | MIT |
| [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) | 3D controller models (Xbox 360, DualShock 4 OBJ meshes) | CC BY-NC-SA 4.0 |
| [Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack) | 2D controller schematic overlays (Xbox 360, DS4 PNG assets) | MIT |
| [HelixToolkit](https://github.com/helix-toolkit/helix-toolkit) | 3D viewport rendering for WPF | MIT |
| [ModernWpf](https://github.com/Kinnara/ModernWpf) | Fluent Design theme for WPF | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM data binding framework | MIT |
| [HidHide](https://github.com/nefarius/HidHide) | Device hiding driver to prevent double input | MIT |

## Donations

Just knowing you find PadForge useful is reward enough for me. If you truly insist on donating, please donate to your charity of choice and bless humanity. Also consider donating directly to the upstream projects listed above -- they made all of this possible.

**My promise:** I will never make PadForge a paid, freemium, or Patreon early-access paywalled product. Free means free.

## License

This project is licensed under **CC BY-NC-SA 4.0** (Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International).

- **3D controller models** adapted from [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) (CC BY-NC-SA 4.0) -- Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
- **2D controller assets** from [Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack) (MIT) -- by AL2009man
- **Original codebase** forked from [x360ce](https://github.com/x360ce/x360ce) (MIT)
- **SDL3** is licensed under the [zlib License](https://github.com/libsdl-org/SDL/blob/main/LICENSE.txt)
- **ViGEmBus** and **Nefarius.ViGEm.Client** are licensed under the MIT License
- **vJoy** is licensed under the MIT License

See [LICENSE](LICENSE) for the full license text.
