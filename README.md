# PadForge

Modern controller mapping utility for Windows. Maps any controller, keyboard, or mouse to virtual Xbox 360, DualShock 4, DirectInput, MIDI, or Keyboard+Mouse controllers that games and applications see as real hardware.

Built with SDL3, Windows Raw Input, ViGEmBus, vJoy, Windows MIDI Services, HelixToolkit, .NET 10 WPF, and Fluent Design. Modern fork of [x360ce](https://github.com/x360ce/x360ce).

## Features

- **Any input to any virtual controller** — Joysticks, gamepads, keyboards, and mice map to Xbox 360, DualShock 4, fully custom DirectInput controllers (up to 8 axes, 128 buttons, 4 POV hats), virtual MIDI devices, or keyboard+mouse output
- **Up to 16 virtual controller slots** — Mix and match Xbox 360, DualShock 4, DirectInput, MIDI, and Keyboard+Mouse across up to 16 simultaneous slots, each combining input from multiple physical devices
- **Keyboard+Mouse virtual controller** — Map controller buttons to keyboard key presses and sticks/triggers to mouse movement or scroll. No driver required — always available
- **3D and 2D controller visualization** — Interactive 3D controller model with mouse and touch gestures (rotate, pinch-to-zoom, two-finger pan) and flat 2D schematic, both showing live button, stick, and trigger state in real time. Keyboard+Mouse slots show an interactive keyboard and mouse preview
- **Web browser virtual controller** — Use any touchscreen device as a controller via a built-in web server. Serves Xbox 360 and DS4 layouts with real-time touch input over WebSocket, including dual analog sticks, 8-way D-pad, triggers, and rumble feedback
- **Interactive mapping** — Record mappings by pressing buttons on your controller, or use "Map All" for quick setup. Auto-mapping for recognized gamepads. Force raw joystick mode for devices with incorrect SDL3 gamepad remapping
- **Sensitivity curves** — Per-axis sensitivity curve editors for sticks (independent X and Y) and triggers. Choose from presets (Linear, Smooth, Aggressive, Instant, S-Curve, Delay) or create custom multi-point curves with interactive drag-and-drop editors. Live indicator shows real-time position on the curve
- **Dead zone shapes** — Six dead zone algorithms: Scaled Radial (default), Radial, Axial, Hybrid, Sloped Scaled Axial, and Sloped Axial. Per-axis dead zone, anti-dead zone, and linear response for sticks and triggers, with live preview, plus stick center offset calibration and max range
- **Force feedback** — Rumble passthrough with per-motor strength, overall gain, and motor swap. Haptic fallback for devices without native rumble. DirectInput force feedback for custom controllers
- **MIDI virtual controller output** — Map any input to virtual MIDI devices. Axes send Control Change messages, buttons send Note On/Off. Configurable MIDI channel (1–16), CC mapping, note mapping, and velocity. Requires Windows MIDI Services (PadForge can install it for you)
- **Macro system** — Trigger macros with combo triggers combining buttons, axes (with configurable threshold), and POV hat directions. Execute action sequences of button presses, key presses, mouse actions (move, click, scroll with configurable sensitivity), delays, system volume, per-app volume, and axis manipulation. Supports "Always" mode (runs continuously without a trigger), up to 128 buttons for custom DirectInput controllers, repeat modes, and input device or output controller trigger sources
- **Per-app profile switching** — Automatically switch controller configurations when specific applications gain focus
- **DSU/Cemuhook motion server** — Broadcasts gyro and accelerometer data over UDP (port 26760) for emulators like Cemu and Dolphin
- **Input hiding** — Automatically hide physical controllers from games via HidHide (driver-level, prevents double input) or consume only mapped keyboard/mouse inputs via low-level hooks (no driver needed). Per-device toggles with auto-enable for gamepads and safety warnings for mice/keyboards. Built-in HidHide app whitelisting
- **Driver management** — One-click install/uninstall for ViGEmBus, HidHide, vJoy, and Windows MIDI Services. Built-in HidHide device blacklisting and app whitelisting — no external configuration tool needed
- **Flight-sim-grade input precision** — 1000 Hz polling with sub-millisecond jitter via high-resolution waitable timers, bit-perfect axis passthrough at default settings, double-precision dead zone math, and 16-bit (65536-position) vJoy axis output that exceeds the resolution of physical controller ADCs
- **Multilingual support** — Switch languages live from Settings without restarting. Community-contributed translations via .resx resource files
- **System tray** — Minimize to tray, start minimized, start at login
- **Portable** — Single-file self-contained executable

## Screenshots

### Dashboard
![Dashboard](screenshots/dashboard.jpg)
At-a-glance overview showing input engine status (polling rate, device count), virtual controller slots with type badges, DSU motion server, and driver status.

### 3D Controller Visualization
![Controller](screenshots/controller.jpg)
Interactive 3D controller model rendered with HelixToolkit. Rotate, zoom, and pan to inspect from any angle. Buttons, sticks, and triggers highlight in real time. Includes motor activity meters and a "Map All" button for quick auto-mapping. Toggle between 3D and 2D views.

### 2D Controller Visualization
![Controller 2D](screenshots/controller-2d.jpg)
Flat 2D schematic view showing the same live button, stick, and trigger state as the 3D view. Useful for quick at-a-glance input monitoring.

### Button and Axis Mappings
![Mappings](screenshots/mappings.jpg)
Full mapping grid where each output (buttons, sticks, triggers, D-pad) can be assigned to any source input. Record a mapping by pressing a button on your device, or edit the descriptor directly. Supports inversion and half-axis options. Output labels adapt to controller type (DS4 shown: Cross, Circle, Square, Triangle, L1, R1, etc.).

### Stick Dead Zones
![Sticks](screenshots/sticks.jpg)
Per-axis dead zone, anti-dead zone, and linear response curve sliders for left and right thumbsticks, with live circular previews showing current stick position and the active dead zone region. Six dead zone shape algorithms and per-axis sensitivity curve editors with preset and custom modes.

### Trigger Dead Zones
![Triggers](screenshots/triggers.jpg)
Range sliders, anti-dead zone, and live value bars for left and right triggers showing real-time processed output. Per-trigger sensitivity curves with the same preset and custom editor as sticks.

### Force Feedback / Rumble
![Force Feedback](screenshots/force-feedback.jpg)
Rumble configuration with overall gain, per-motor strength sliders, and a swap option. Test Rumble button for quick verification, plus live motor activity bars showing current rumble intensity.

### Macro Editor
![Macros](screenshots/macros.jpg)
Create macros with combo triggers combining buttons, axes (with threshold), and POV hat directions from either the output controller or a physical input device. Each macro supports an action sequence of button presses, key presses, mouse actions (move, click, scroll), delays, system volume, per-app volume, and axis manipulation. Four fire modes: on press, on release, while held, and "Always" (runs continuously without a trigger). Type-aware button names for Xbox 360, DualShock 4, and custom DirectInput controllers (up to 128 buttons).

### Keyboard+Mouse Virtual Controller
![KBM Preview](screenshots/kbm-preview.jpg)
Keyboard+Mouse slots show an interactive keyboard and mouse preview with live highlighting of mapped keys and mouse buttons.

### vJoy Custom DirectInput Controller
![vJoy](screenshots/vjoy.jpg)
Configuration bar for vJoy slots lets you set the number of axes (1-8), buttons (1-128), POV hats (0-4), and thumbsticks. Includes a live schematic view showing the custom controller layout.

### MIDI Virtual Controller
![MIDI](screenshots/midi.jpg)
MIDI slot configuration with channel selection (1-16) and velocity control. Axes send Control Change messages, buttons send Note On/Off. Requires Windows MIDI Services.

### Add Controller
![Add Controller](screenshots/add-controller-popup.jpg)
The Add Controller popup lets you create Xbox 360, DualShock 4, vJoy, Keyboard+Mouse, and MIDI virtual controllers. Type buttons are dimmed when their per-type limit is reached.

### Profiles
![Profiles](screenshots/profiles.jpg)
Per-app profile switching. Create named profiles that automatically activate when specific applications gain focus. Each profile stores its own controller mappings and settings.

### Device List
![Devices](screenshots/devices.jpg)
Card-based device list showing all detected gamepads, joysticks, keyboards, and mice with status, type, VID/PID, and slot assignment. Per-device input hiding toggles: "Hide from games" (HidHide driver-level) and "Consume mapped inputs" (low-level hooks for keyboards/mice). Select a device to see its raw input state — axes as progress bars, buttons as indicator circles, POV as a compass, and gyro/accelerometer values.

### Settings
![Settings](screenshots/settings.jpg)
Language selector for live multilingual switching, appearance theme, input engine options (auto-start, background polling, configurable polling interval, master input hiding toggle), and window behavior (system tray, start minimized, start at login).

### Settings — Input Hiding
![Settings — Input Hiding](screenshots/settings-hidhide.jpg)
HidHide driver-level input hiding configuration with app whitelisting, per-device toggles, and low-level keyboard/mouse hook options.

### Settings — Drivers and Diagnostics
![Settings — Drivers](screenshots/settings-drivers.jpg)
One-click ViGEmBus, HidHide, vJoy, and Windows MIDI Services driver management with version info, settings file controls (save, reload, reset, open folder), and diagnostics showing app version, .NET runtime, and SDL version.

### About
![About](screenshots/about.jpg)
Application information, technology stack, and license details.

### Web Controller
![Web Controller - Landing](screenshots/web-landing.jpg)
![Web Controller - Xbox 360](screenshots/web-controller.jpg)

Built-in web server lets any touchscreen device act as a virtual controller. Choose Xbox 360 or DS4 layout, with responsive touch controls including dual virtual analog sticks, 8-way D-pad, and real-time visual feedback matching the desktop 2D controller view.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (included in the single-file publish)

Optional drivers (PadForge can install all of these for you):

- [ViGEmBus](https://github.com/nefarius/ViGEmBus) — Virtual Xbox 360 and DualShock 4 output
- [vJoy](https://github.com/BrunnerInnovation/vJoy) — Custom DirectInput joystick/gamepad output with configurable axes, buttons, POVs, and force feedback
- [HidHide](https://github.com/nefarius/HidHide) — Hide physical controllers from games to prevent double input
- [Windows MIDI Services](https://github.com/microsoft/MIDI) — Virtual MIDI device output for MIDI virtual controllers

## Build

```bash
dotnet publish PadForge.App/PadForge.App.csproj -c Release
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/PadForge.exe`

See [BUILD.md](BUILD.md) for full project structure, architecture details, and developer reference.

## Upstream Projects and Acknowledgments

PadForge stands on the shoulders of these projects. Please consider supporting them:

| Project | Role in PadForge | License |
|---|---|---|
| [x360ce](https://github.com/x360ce/x360ce) | Original codebase this project was forked from | MIT |
| [SDL3](https://github.com/libsdl-org/SDL) | Controller input — joystick, gamepad, and sensor enumeration and reading | zlib |
| [ViGEmBus](https://github.com/nefarius/ViGEmBus) | Virtual Xbox 360 and DualShock 4 controller driver | MIT |
| [Nefarius.ViGEm.Client](https://github.com/nefarius/ViGEm.NET) | .NET client library for ViGEmBus | MIT |
| [vJoy](https://github.com/BrunnerInnovation/vJoy) | Custom DirectInput joystick/gamepad driver with configurable HID descriptors and force feedback | MIT |
| [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) | 3D controller models (Xbox 360, DualShock 4 OBJ meshes) | CC BY-NC-SA 4.0 |
| [Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack) | 2D controller schematic overlays (Xbox 360, DS4 PNG assets) | MIT |
| [HelixToolkit](https://github.com/helix-toolkit/helix-toolkit) | 3D viewport rendering for WPF | MIT |
| [ModernWpf](https://github.com/Kinnara/ModernWpf) | Fluent Design theme for WPF | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM data binding framework | MIT |
| [HidHide](https://github.com/nefarius/HidHide) | Device hiding driver to prevent double input | MIT |
| [Windows MIDI Services](https://github.com/microsoft/MIDI) | Virtual MIDI device SDK for MIDI controller output | MIT |

## Donations

Just knowing you find PadForge useful is reward enough for me. If you truly insist on donating, please donate to your charity of choice and bless humanity. Also consider donating directly to the upstream projects listed above — they made all of this possible.

**My promise:** I will never make PadForge a paid, freemium, or Patreon early-access paywalled product. Free means free.

## License

This project is licensed under **CC BY-NC-SA 4.0** (Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International).

- **3D controller models** adapted from [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) (CC BY-NC-SA 4.0) — Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
- **2D controller assets** from [Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack) (MIT) — by AL2009man
- **Original codebase** forked from [x360ce](https://github.com/x360ce/x360ce) (MIT)
- **SDL3** is licensed under the [zlib License](https://github.com/libsdl-org/SDL/blob/main/LICENSE.txt)
- **ViGEmBus** and **Nefarius.ViGEm.Client** are licensed under the MIT License
- **vJoy** is licensed under the MIT License
- **Windows MIDI Services** is licensed under the MIT License
- **HidHide** is licensed under the MIT License

See [LICENSE](LICENSE) for the full license text.
