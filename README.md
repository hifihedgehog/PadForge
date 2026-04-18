<p align="center">
  <img src="screenshots/icon.png" alt="PadForge" width="128">
</p>

<h1 align="center">PadForge</h1>

*"And we talk of Christ, we rejoice in Christ, we preach of Christ, we prophesy of Christ, and we write according to our prophecies, that our children may know to what source they may look for a remission of their sins."* — 2 Nephi 25:26

*Glory, honor, and praise to the Lord Jesus Christ, the source of all truth, forever and ever.*

---

PadForge is a Windows controller remapper. It takes input from whatever physical device you have (gamepads, joysticks, keyboards, mice, touchscreens) and feeds it into virtual controllers that games see as real hardware: Xbox 360, DualShock 4, DirectInput, MIDI, or keyboard and mouse.

Fork of [x360ce](https://github.com/x360ce/x360ce), rewritten on SDL3, ViGEmBus, vJoy, HidHide, Windows MIDI Services, HelixToolkit, and .NET 10 WPF.

---

## Features

### Input and output

- Any physical input into any virtual controller. Joysticks, gamepads, keyboards, and mice feed Xbox 360, DualShock 4, custom DirectInput (up to 8 axes, 128 buttons, 4 POV hats), virtual MIDI, or keyboard and mouse output.
- Up to 16 virtual controllers at once, mixing types. Each slot can merge input from multiple physical devices.
- Keyboard and mouse output without a driver: map buttons to key presses, sticks or triggers to mouse movement or scroll.
- DSU / Cemuhook gyro and accelerometer broadcast over UDP port 26760 for Cemu, Dolphin, and similar emulators.

### Mapping

- Record a binding by pressing a button, pick from a dropdown (which includes raw buttons beyond the standard 11), or run "Map All" for a one-pass setup.
- Auto-mapping for recognized gamepads. Force-raw mode bypasses SDL3's remapping when it guesses wrong.
- Dropdowns persist while devices are offline so you don't lose state on disconnect.
- Per-axis sensitivity curves for sticks (independent X and Y) and triggers. Six presets (Linear, Smooth, Aggressive, Instant, S-Curve, Delay) or custom multi-point curves with a drag-and-drop editor and a live position indicator.
- Six deadzone algorithms (Scaled Radial, Radial, Axial, Hybrid, Sloped Scaled Axial, Sloped Axial) with per-axis deadzone, anti-deadzone, linear response, stick-center calibration, max range, and per-mapping axis-to-button activation thresholds with half-axis support for centered joysticks.

### Rumble and force feedback

- Rumble passthrough with per-motor strength, overall gain, and motor swap. Haptic fallback for devices without native rumble.
- Audio bass rumble: captures system audio and converts bass frequencies to per-device vibration through a 48 dB/octave filter with configurable sensitivity and cutoff.
- DirectInput force feedback relay for custom vJoy controllers.

### Visualization

- 3D HelixToolkit controller model. Rotate, zoom, pan. Buttons, sticks, and triggers highlight in real time.
- 2D schematic showing the same live state in a compact layout.
- Keyboard and mouse preview for the KBM output type, showing every mapped key and button.
- Built-in WebSocket server turns any touchscreen into a wireless controller. Xbox 360 and DS4 layouts, dual analog sticks, 8-way D-pad, triggers, rumble feedback.

### Macros

- Combo triggers built from up to 8 buttons, axes (with configurable threshold), and POV directions, sourced from the virtual output or a physical input device.
- Action sequences: button presses, key presses, mouse move / click / scroll, delays, system and per-app volume, and axis manipulation. Four fire modes (on press, on release, while held, always). Supports 128 buttons on custom DirectInput controllers and repeat modes.

### Profiles

- Per-application profiles. Switch automatically when a given app gains focus. A Win11-style flyout shows the active profile, initialization progress, and warnings for offline controllers.
- Controller shortcuts: assign button combos (cross-device, axis direction supported) to cycle Next / Previous, jump to a specific profile, or toggle the PadForge window without touching the keyboard.

### System integration

- HidHide driver-level hiding of physical controllers so games don't see double input. Low-level hooks consume only mapped keyboard and mouse input. Per-device toggles auto-enable for gamepads, with warnings for mice and keyboards.
- Built-in installer for ViGEmBus, HidHide, vJoy, and Windows MIDI Services. Status, version info, and device blacklist / app whitelist controls live in Settings.

### MIDI output

- Virtual MIDI endpoint output. Axes send Control Change, buttons send Note On / Off. Channel 1 to 16, configurable CC mapping, note mapping, and velocity. PadForge creates its own system-wide endpoint, so loopMIDI is not required. Needs Windows MIDI Services (installable from Settings).

### Performance

- 1000 Hz polling with sub-millisecond jitter via high-resolution waitable timers.
- Bit-perfect axis passthrough at default settings. Double-precision deadzone math. 15-bit (32768-position) vJoy axis output exceeding the resolution of most physical ADCs.
- Language switch in Settings with no restart. Community translations via .resx resource files.
- Minimize to tray, start minimized, or launch at login.
- Single-file self-contained executable. No installer.

---

## Screenshots

### Dashboard
![Dashboard](screenshots/dashboard.jpg)
Polling rate, device count, virtual controller slots with type badges, DSU motion server status, and driver health on one screen.

### 3D controller visualization
![Controller](screenshots/controller.jpg)
Interactive 3D model. Rotate, zoom, and pan to inspect from any angle while buttons, sticks, and triggers highlight live.

### 2D controller visualization
![Controller 2D](screenshots/controller-2d.jpg)
Flat schematic reflecting the same live state as the 3D view.

### Button and axis mappings
![Mappings](screenshots/mappings.jpg)
Full mapping grid with record-by-press, dropdown selection, inversion, and half-axis options. Output labels adapt to controller type (DS4 shown).

### Stick deadzones
![Sticks](screenshots/sticks.jpg)
Per-axis deadzone, anti-deadzone, and linear response with live circular previews, six shape algorithms, and per-axis sensitivity curve editors.

### Trigger deadzones
![Triggers](screenshots/triggers.jpg)
Range sliders, anti-deadzone, and live value bars for each trigger alongside per-trigger sensitivity curves.

### Force feedback and rumble
![Force Feedback](screenshots/force-feedback.jpg)
Overall gain, per-motor strength, motor swap, audio bass rumble with configurable sensitivity and cutoff, test button, and live motor activity meters.

### Macro editor
![Macros](screenshots/macros.jpg)
Combo triggers from buttons, axes, and POV hats fire action sequences of key presses, mouse actions, delays, volume control, and axis manipulation across four fire modes.

### Keyboard and mouse virtual controller
![KBM Preview](screenshots/kbm-preview.jpg)
Preview highlighting every mapped key and button in real time.

### vJoy custom DirectInput controller
![vJoy](screenshots/vjoy.jpg)
Configure thumbsticks, triggers (up to 8 axes shared between them), buttons (1 to 128), and POV hats (0 to 4) with a live schematic of the custom layout.

### MIDI virtual controller
![MIDI](screenshots/midi.jpg)
Channel selection (1 to 16), velocity control, CC and note mapping. Axes as Control Change, buttons as Note On / Off.

### Add controller
![Add Controller](screenshots/add-controller-popup.jpg)
Create Xbox 360, DualShock 4, vJoy, Keyboard+Mouse, or MIDI virtual controllers. Type buttons dim at their per-type limit.

### Profiles
![Profiles](screenshots/profiles.jpg)
Named profiles that activate automatically when specific applications gain focus, each with its own mappings and settings.

### Device list
![Devices](screenshots/devices.jpg)
Card-based list of all detected gamepads, joysticks, keyboards, and mice with status, type, VID/PID, slot assignment, and per-device input hiding toggles. Select a device to see raw axes, buttons, POV compass, and gyro / accelerometer values.

### Settings
![Settings](screenshots/settings.jpg)
Language, appearance theme, input engine options (auto-start, background polling, configurable polling interval, master input hiding toggle), and window behavior.

### Settings, input hiding
![Settings — Input Hiding](screenshots/settings-hidhide.jpg)
HidHide driver-level configuration with app whitelisting, per-device toggles, and low-level keyboard/mouse hook options.

### Settings, drivers and diagnostics
![Settings — Drivers](screenshots/settings-drivers.jpg)
Driver management for ViGEmBus, HidHide, vJoy, and Windows MIDI Services with version info, settings file controls, and diagnostics.

### About
![About](screenshots/about.jpg)
Application info, technology stack, and license details.

### Web controller
![Web Controller - Landing](screenshots/web-landing.jpg)
![Web Controller - Xbox 360](screenshots/web-controller.jpg)
Built-in web server turns any touchscreen into a virtual controller with dual analog sticks, 8-way D-pad, triggers, and live visual feedback.

---

## Known limitations

- PadForge runs elevated so it can install and manage drivers. Non-elevated apps still read the virtual controllers normally, but driver operations need admin.
- HidHide's device hiding is global per machine account, not per-process.
- DirectInput force feedback routing requires the vJoy driver.
- Some games poll directly via xinput1_4 rather than going through the standard XInput slot assignments; behavior there depends on the game.
- Windows MIDI Services requires Windows 10 or Windows 11. The MIDI output type is hidden on systems without it.

---

## Requirements

| Requirement | Details |
|---|---|
| **OS** | Windows 10 or 11 (x64) |
| **Runtime** | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (included in the single-file publish) |

### Optional drivers

PadForge installs these from Settings if they aren't already present:

| Driver | Purpose |
|---|---|
| [ViGEmBus](https://github.com/nefarius/ViGEmBus) | Virtual Xbox 360 and DualShock 4 output |
| [vJoy](https://github.com/BrunnerInnovation/vJoy) | Custom DirectInput output with configurable axes, buttons, POVs, and force feedback |
| [HidHide](https://github.com/nefarius/HidHide) | Hide physical controllers from games to prevent double input |
| [Windows MIDI Services](https://github.com/microsoft/MIDI) | Virtual MIDI device output |

---

## Build

```bash
dotnet publish PadForge.App/PadForge.App.csproj -c Release
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/PadForge.exe`

See [BUILD.md](BUILD.md) for project structure, architecture, and developer reference.

---

## Upstream projects and acknowledgments

PadForge stands on these projects. Please consider supporting them directly.

| Project | Role in PadForge | License |
|---|---|---|
| [x360ce](https://github.com/x360ce/x360ce) | Original codebase this project was forked from | MIT |
| [SDL3](https://github.com/libsdl-org/SDL) | Controller input: joystick, gamepad, and sensor enumeration and reading | zlib |
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

---

## Donations

Knowing PadForge is useful is reward enough. If you truly insist on donating, please donate to your charity of choice and bless humanity. If you can't think of one, consider [Humanitarian Services of The Church of Jesus Christ of Latter-day Saints](https://philanthropies.churchofjesuschrist.org/humanitarian-services). Also consider donating directly to the upstream projects listed above. They made all of this possible.

**My promise:** PadForge will never become paid, freemium, or Patreon early-access paywalled. Free means free.

---

## License

This project is licensed under **CC BY-NC-SA 4.0** (Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International).

- **3D controller models** adapted from [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) (CC BY-NC-SA 4.0). Copyright (c) CasperH2O, Lesueur Benjamin, trippyone.
- **2D controller assets** from [Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack) (MIT), by AL2009man.
- **Original codebase** forked from [x360ce](https://github.com/x360ce/x360ce) (MIT).
- **SDL3** is licensed under the [zlib License](https://github.com/libsdl-org/SDL/blob/main/LICENSE.txt).
- **ViGEmBus** and **Nefarius.ViGEm.Client** are licensed under the MIT License.
- **vJoy** is licensed under the MIT License.
- **Windows MIDI Services** is licensed under the MIT License.
- **HidHide** is licensed under the MIT License.

See [LICENSE](LICENSE) for the full license text.
