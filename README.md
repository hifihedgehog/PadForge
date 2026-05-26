<p align="center">
  <img src="screenshots/icon.png" alt="PadForge" width="128">
</p>

<h1 align="center">PadForge</h1>

*"And we talk of Christ, we rejoice in Christ, we preach of Christ, we prophesy of Christ, and we write according to our prophecies, that our children may know to what source they may look for a remission of their sins."* — 2 Nephi 25:26

*Glory, honor, and praise to the Lord Jesus Christ, the source of all truth, forever and ever.*

---

**PadForge makes any input look like any controller.** Plug in a steering wheel. The game sees a PlayStation pad. Use a DualSense. The game sees an Xbox 360. Map your keyboard. The game sees a flight stick. Open a tab on your phone. That tab becomes a gamepad your PC games can use.

Free Windows app. No subscription. No paywall. No nag screens. Built on SDL3, [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro), [OpenXInput](https://github.com/hifihedgehog/OpenXinput), HidHide, Windows MIDI Services, HelixToolkit, WPF UI, and .NET 10.

PadForge is for sim racers running wheels in games that only understand Xbox controllers. For DualSense owners who want adaptive triggers and lightbar effects in Steam games that ignore them. For accessibility users mapping whatever hardware they can use. For anyone whose controller doesn't match what their game expects.

> **New in 3.3.** Touchpad tab grew two output cards: **Mouse Output** (per-axis sensitivity + invert for a touchpad finger driving mouse X/Y) and **Stick / D-Pad Output** (a touchpad finger becomes a virtual analog stick or wedge-thresholded D-pad). Gesture engine fanned out per slot. Two slots sharing one physical touchpad each carry their own toggles, tuning, and gesture context. Keyboard / Mouse virtual controller adds Print Screen, Scroll Lock, Pause, and Num Lock. Every touchpad feature toggle is off by default so gestures and outputs are opt-in. Carrying forward from 3.2: the rebuilt mapping engine (multi-source rows, shift layers, cross-device chords, drag-and-drop formula editor), gyro at Steam Input parity, Impulse Triggers tab, and 2026 Steam Controller support through the bundled SDL3 fork. [Wiki](https://github.com/hifihedgehog/PadForge/wiki).

<p align="center">
  <a href="https://github.com/hifihedgehog/HIDMaestro">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="screenshots/hidmaestro-logo-dark.png">
      <img src="screenshots/hidmaestro-logo-light.png" alt="HIDMaestro" width="96">
    </picture>
  </a>
  <br>
  <em>Powered by HIDMaestro. One driver, 225+ device profiles.</em>
</p>

---

## What PadForge does for you

### That game that won't read your wheel? It will now.

PadForge translates a PS5 DualSense into the Xbox pad a Steam game expects. A Logitech G29 wheel into the gamepad a racing game accepts. A Saitek HOTAS into the gamepad a flight game stubbornly insists on. The game never knows the difference.

![Mappings tab](screenshots/mappings.jpg)

### Pedals, wheel, and HOTAS throttle. One virtual stick.

One mapping row can read from any number of physical inputs across any number of physical devices. Six combine modes (Strongest, Combined, Average, Either, Both, Only one) plus a drag-and-drop custom formula editor. Cross-device chords so a button on the wheel and a button on the shifter trigger one virtual press.

![Multi-source mapping row with combine modes and formula editor](screenshots/mappings.jpg)

### Caps Lock for your controller.

Each slot can carry extra mapping tables that turn on while a button, chord, or axis is held. Five activation modes (Hold, Toggle, Sticky, Cycle, Custom jump-to). Per-layer color and emoji icon. A Win11-style flyout pops to confirm the active layer the moment it engages.

![Shift layer tab strip above the mapping grid](screenshots/mappings.jpg)

### Aim with the controller, not the stick.

Reference frames (Local, Player, World). Dual-threshold smoothing. Real-world calibration. A cross-device Aim Engage button. Tuning saves per pad per slot, so the same pad on two slots can feel two different ways. Gyro Pitch / Yaw / Roll bind as first-class sources in the mapping table.

![Gyro tab](screenshots/gyro.jpg)

### Forza, Gears, and Halo on your real Xbox pad.

PadForge passes Xbox impulse trigger data straight to the assigned physical Xbox One, Elite, or Series pad. The same data routes to DualSense as Adaptive Trigger Vibration so a DualSense playing Forza buzzes the triggers in step with an Xbox One pad doing the same. Plus audio-bass-driven trigger rumble and a constant trigger force that resumes when the game stops.

![Impulse Triggers tab](screenshots/impulse-triggers.jpg)

### Adaptive triggers and lightbar that don't need the game's blessing.

Seven adaptive trigger modes with a live preview that draws the resistance curve as you drag. Thirteen lightbar modes, six of them tied to your system audio (three Audio Pulse variants, three Audio Bands variants). The DualSense lights and triggers light up in games that have never heard of a DualSense.

| ![Adaptive Triggers tab](screenshots/adaptive-triggers.jpg) | ![Lighting tab](screenshots/lighting.jpg) |
|:---:|:---:|

### Turn the DualSense pad into a mouse, a stick, or a D-pad.

A Touchpad tab on every slot whose source carries a touchpad surface (DualSense, DualSense Edge, DS4, Web Controller, on-screen Touchpad Overlay, Windows Precision Touchpad). Map a finger to mouse X/Y with per-axis sensitivity and invert. Anchor a virtual analog stick where your finger lands. Drop a wedge-thresholded D-pad on top. The gesture stack covers 4-way and 8-way swipes, taps, longpress, pinch, rotate, three- to five-finger gestures, and shape templates (Circle, Square, Triangle, Z, Checkmark). Every toggle saves per slot.

![Touchpad tab](screenshots/touchpad.jpg)

### Open a browser. Press buttons.

PadForge runs a tiny web server. Any device with a browser on your Wi-Fi can load it, pick a layout (Xbox 360, DualShock 4, or multi-touch touchpad), and play. Up to 16 phones at once, each a separate virtual pad. Touch buttons, dual analog sticks, an 8-way D-pad. Rumble feedback through the Vibration API. No app to install on the phone.

No phone handy? Turn on **Touchpad Overlay** from the Dashboard. A transparent on-screen touch surface pins to any monitor and drives the DS4 or DualSense touchpad directly.

![Web controller](screenshots/web-controller.jpg)

### Local co-op without limits.

Two sim racers on two wheels at once. A flight stick plus throttle plus rudder pedals as one virtual HOTAS. Mixed gamepad types in one session. Up to 16 controllers. One combo press toggles every virtual controller on or off when you need to step away.

![Dashboard with multiple slots](screenshots/dashboard.jpg)

### Gyro into Cemu, Dolphin, Yuzu, and Ryujinx.

The built-in DSU / Cemuhook server broadcasts gyroscope and accelerometer on UDP port 26760 so emulators can use real motion for Splatoon, Wii titles, 3DS games, and anything else that asks for it. DualSense, DualShock 4, Switch Pro, and 2026 Steam Controller sources all work out of the box.

![Dashboard Motion Server section with port and enable toggle](screenshots/dashboard.jpg)

### A 16-channel MIDI controller, no extra hardware.

Map sticks to Control Change messages. Map buttons to Note On / Note Off. Set velocity per slot. PadForge creates a real Windows MIDI endpoint through Windows MIDI Services that DAWs (Ableton Live, FL Studio, Reaper), VJ tools, and stage lighting apps can subscribe to. No loopMIDI bridge.

![MIDI virtual controller](screenshots/midi.jpg)

---

## PadForge vs other controller mappers

| | PadForge | x360ce | XOutput | reWASD | DS4Windows | Steam Input |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| Free | ✅ | ✅ | ✅ | $9.99+ | ✅ | ✅ |
| Open source | ✅ | ✅ | ✅ archived | ❌ | ✅ | ❌ |
| Works outside Steam | ✅ | ✅ | ✅ | ✅ | ✅ | only via Add Non-Steam Game |
| Actively developed | ✅ 2026 | no release since Nov 2020 | deprecated 2024 | ✅ v9.4 (2026) | ✅ v3.5 (Feb 2026) | ✅ |
| Xbox 360 virtual output | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Xbox One / Series virtual output | ✅ | ❌ | ❌ | ✅ Xbox One | ❌ | ❌ |
| DualShock 4 virtual output | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ |
| DualSense virtual output | ✅ | ❌ | ❌ | ❌ input only | ❌ | ❌ |
| Switch Pro virtual output | ✅ via HIDMaestro | ❌ | ❌ | ✅ | ❌ | ❌ |
| Flight stick / wheel / HOTAS virtual output (DirectInput) | ✅ 225+ HM profiles | ❌ | ❌ | ❌ | ❌ | ❌ |
| MIDI virtual output | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Keyboard + Mouse virtual output | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ |
| Multi-source per row (one output, many inputs) | ✅ 6 combine modes + formula | ⚠️ "Combine Into" merges pads | ⚠️ MapperDataCollection (basic) | ❌ uses per-input Activators | ❌ | ⚠️ per-input Activators |
| Custom formula editor (arithmetic, logic, if-then-else) | ✅ drag-and-drop operators + 10 starter recipes | ❌ | ❌ | ❌ | ❌ | ❌ |
| Shift layers / modifier overlays | ✅ Hold / Toggle / Sticky / Cycle / Custom | ❌ | ❌ | ✅ up to 10 (Hold / Toggle / Custom) | ✅ Mode Shifts | ✅ Action Set Layers (stackable) |
| Cross-device chords (input on pad A + input on pad B) | ✅ | ❌ | ❌ | ✅ via Group of devices | ❌ | ❌ same controller only |
| Gyro mapping | ✅ Local / Player / World, RWC, Aim Engage | ❌ | ❌ | ✅ since v5.3 (curves, Flick Stick) | ✅ gyro-to-mouse, gyro-to-RS | ✅ |
| Xbox Impulse Trigger passthrough | ✅ + DualSense AT Vibration auto-route | ❌ | ❌ | ✅ Xbox One output only | ❌ | ❌ |
| Constant trigger force | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Audio-bass trigger rumble | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Audio-bass body rumble | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| DualSense Adaptive Triggers | ✅ 7 modes + GameCube preset | ❌ | ❌ | ✅ 11 presets | ⚠️ limited | ❌ |
| DualSense lightbar | ✅ 13 modes inc. Strobe + Battery | ❌ | ❌ | ✅ 6 modes + Player LED + Mic LED | ⚠️ basic, no audio | ⚠️ unverified |
| HID PID 1.0 force feedback (wheels) | ✅ | ✅ constant + periodic (DirectInput) | ⚠️ basic passthrough only | ❌ | ❌ | ❌ |
| DSU / Cemuhook motion server (Cemu, Dolphin, Yuzu) | ✅ | ❌ | ❌ | ✅ port 26760 | ✅ | ❌ |
| Phone as controller | ✅ in-browser, no app install, up to 16 phones at once, touchpad layout included | ❌ | ❌ | ⚠️ reWASD Mobile app (one phone, no touchpad layout) | ❌ | ❌ |
| Per-app profile switching | ✅ | ✅ since v4.17.12 (Nov 2020) | ❌ | ✅ Autodetect | ✅ | ✅ per-game by design |
| Max simultaneous virtual controllers | 16 | 4 (hard-coded PAD1-4 in UI) | 4 (UI matches XInput slot indices) | 4 (Slot UI cap) | 4 (Output Slots UI cap) | 1 per physical pad |
| 1000 Hz polling | ✅ | ⚠️ unverified | ⚠️ unverified | ✅ user-selectable 500 / 1000 Hz | ✅ on USB DS4 | ⚠️ unverified |
| 3D + 2D controller visualization | ✅ | ⚠️ 2D Xbox 360 only | ❌ | ⚠️ 2D only | ⚠️ basic | ⚠️ configurator preview |
| Multi-point sensitivity curve editor | ✅ draggable points | ⚠️ single slider | ⚠️ deadzone only | ✅ custom 4-point | ⚠️ preset curves | ✅ response curves |
| 2026 Steam Controller support | ✅ via SDL3 fork | ❌ | ❌ | ⚠️ unverified | ❌ | ✅ |

Comparison reflects each tool's shipping release at the time of this README. Verified against each project's own docs and source: x360ce v4.17.15.0 changelog; XOutput README + 3.x source; reWASD help.rewasd.com (v9.4); ds4windowsapp/DS4Windows v3.5; Steamworks Documentation (Action Set Layers / Activators / Mode Shifting / Input Source Modes). ⚠️ means the feature exists but is limited or unverified at the level of detail PadForge implements it.

---

## Quick start

1. Download `PadForge.exe` from the [latest release](https://github.com/hifihedgehog/PadForge/releases/latest).
2. Run it. PadForge always runs elevated, so Windows shows the UAC prompt at startup. The first launch installs HIDMaestro inside that same elevated session.
3. Click **Add Controller** on the Dashboard. Pick Xbox, PlayStation, Extended, MIDI, or Keyboard+Mouse.
4. On the new slot, drag a physical device onto it from the sidebar.
5. Most controllers auto-map on assign. For the rest, click **Map All** to walk every button in one pass, or use the **Mappings** tab to bind one at a time.
6. Launch your game. The game sees the virtual controller as real hardware.

Most games "just work" after step 5. If a game sees both your physical and virtual controller at once, install HidHide from **Settings → Drivers** to hide the physical one.

---

## Screenshots

### Dashboard
![Dashboard](screenshots/dashboard.jpg)
Polling rate, device count, every virtual controller slot, DSU motion server, web controller server, and driver health on one screen.

### 3D controller visualization
![Controller](screenshots/controller.jpg)
Interactive 3D model per profile. Rotate, zoom, pan. Buttons, sticks, and triggers highlight while you press them. Xbox Series profiles add a clickable Share button.

### 2D controller visualization
![Controller 2D](screenshots/controller-2d.jpg)
Flat schematic of the same controller, same live state. Useful on small monitors or for streaming overlays.

### Button and axis mappings
![Mappings](screenshots/mappings.jpg)
Record a binding by pressing a button. Pick from a dropdown of every available input (including raw HID buttons past the standard 11). Set Invert, Half-axis, or a per-mapping threshold for axis-to-button activation.

### Stick deadzones
![Sticks](screenshots/sticks.jpg)
Six deadzone shapes (Scaled Radial, Radial, Axial, Hybrid, Sloped Scaled Axial, Sloped Axial). Per-axis deadzone, anti-deadzone, linear response, center calibration, and a custom sensitivity-curve editor with draggable points.

### Trigger deadzones
![Triggers](screenshots/triggers.jpg)
Floor and ceiling per trigger. Anti-deadzone. Sensitivity curves. Live value bars at 0.1% precision.

### Force feedback and rumble
![Force Feedback](screenshots/force-feedback.jpg)
Per-motor strength, overall gain, motor swap. Live motor activity bars. Audio Bass Rumble captures system audio, isolates bass through a 48 dB/octave filter, and pushes it to the rumble motors. Music feels physical even when the game is silent.

### DualSense Adaptive Triggers
![Adaptive Triggers](screenshots/adaptive-triggers.jpg)
Seven trigger effect modes. Off, Feedback, Weapon, Vibration, Multi-Position Feedback, Slope, Multi-Position Vibration. A live preview draws the resistance and amplitude curve while you drag Range, Strength, and Frequency. One-click GameCube preset loads parameters that mimic the click of a real GameCube trigger.

### DualSense lightbar
![Lighting](screenshots/lighting.jpg)
Thirteen lightbar modes including three Audio Pulse variants and three Audio Bands variants that react to system audio in real time. Three Input Reactive variants flash on button presses (Random Color, Cycle Through Palette, Base Color). Plus the indicator-LED card for player pattern, mute LED, and brightness.

### Touchpad
![Touchpad](screenshots/touchpad.jpg)
Per-slot touchpad tuning on any source with a touchpad surface (DualSense, DualSense Edge, DS4, Web Controller, on-screen Touchpad Overlay, Windows Precision Touchpad). Five cards: Stick / D-Pad Output (anchor-relative virtual stick + wedge D-pad), Mouse Output (per-axis sensitivity and invert), Gesture Detection (master enable + cooldown), In-Box Gestures (swipes, taps, longpress, pinch, rotate, three- to five-finger, shape templates), Custom Gestures (recorded shape templates per profile).

### Macros
![Macros](screenshots/macros.jpg)
Combo triggers from buttons, axes, and POV directions. Action sequences with key presses, mouse moves, scroll, delays, system volume, app volume, lightbar overrides, and rumble overrides. Four fire modes (on press, on release, while held, always).

### Per-app profiles
![Profiles](screenshots/profiles.jpg)
Each profile holds its own mappings, deadzones, force feedback, lighting, and macros. PadForge watches the foreground window and switches profiles automatically when a matching app gains focus. Controller-shortcut combos cycle profiles without touching the keyboard.

### Keyboard + Mouse virtual controller
![KBM Preview](screenshots/kbm-preview.jpg)
Map a controller stick to mouse movement. Map face buttons to WASD. The preview lights up every mapped key and mouse button in real time.

### Extended virtual controller
![Extended](screenshots/extended.jpg)
Flight sticks, racing wheels, HOTAS, third-party gamepads. 225+ HIDMaestro profiles plus a Custom mode that builds a HID descriptor from scratch. Up to 8 axes, 128 buttons, 4 POV hats. Configurable VID, PID, and product string.

### PlayStation virtual controller
![PlayStation](screenshots/playstation.jpg)
DualShock 4, DualSense, and DualSense Edge through HIDMaestro. Source gyro, accelerometer, touchpad, and battery passed through to the game.

### MIDI virtual controller
![MIDI](screenshots/midi.jpg)
Channel 1-16. Configurable CC mapping, note mapping, and velocity. Axes send Control Change. Buttons send Note On / Off. No loopMIDI required. PadForge creates its own system endpoint via Windows MIDI Services.

### Add controller
![Add Controller](screenshots/add-controller-popup.jpg)
Pick the virtual controller type. Buttons dim when you hit the per-type limit.

### Devices
![Devices](screenshots/devices.jpg)
Every detected gamepad, joystick, keyboard, mouse, and touchpad as a card. Live raw axes, buttons, POV compass, gyro / accelerometer values, and touchpad finger positions for the selected device. Per-device HidHide toggle and Force Raw Joystick mode for when SDL3 guesses the gamepad layout wrong.

### Web controller
![Web Controller](screenshots/web-controller.jpg)
Connect a phone or tablet over Wi-Fi. Browser shows an Xbox 360 layout, a DualShock 4 layout, or a multi-touch touchpad layout with virtual sticks, D-pad, triggers, and rumble. Touch the sticks to push them. Tap to click.

### Settings
![Settings](screenshots/settings.jpg)
Language (10 locales, live-switch with no restart). Theme (System / Light / Dark). Polling interval (1-16 ms). Auto-start at login, minimize to tray, master input-hiding toggle.

---

## Known limits

- PadForge runs elevated so it can install and manage the HIDMaestro driver. Non-elevated games still read the virtual controllers normally.
- HidHide's device hiding is global per user account, not per-game.
- The MIDI virtual controller needs Windows MIDI Services (Windows 11 24H2 / build 26100 or later). On older systems the MIDI type is hidden.

---

## Requirements

Windows 10 or 11 on x64. The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) is bundled in the single-file release, so there is nothing else to install.

### Drivers

PadForge installs **HIDMaestro** on first run. HIDMaestro is the engine that creates virtual controllers. Add a slot and HIDMaestro spins up a HID device matching the controller "shape" you picked.

Two more drivers are optional. PadForge offers to install each one only when you need its feature:

| Driver | Install when |
|---|---|
| [HidHide](https://github.com/nefarius/HidHide) | A game sees both your physical and virtual controller at once |
| [Windows MIDI Services](https://github.com/microsoft/MIDI) | You want the MIDI virtual controller type |

**OpenXInput** is bundled inside `PadForge.exe`. No separate install. It filters PadForge's own virtual controllers out of its own XInput view so device enumeration stays clean.

---

## Build from source

```bash
dotnet publish PadForge.App/PadForge.App.csproj -c Release
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/PadForge.exe`

See [BUILD.md](BUILD.md) for project structure, architecture notes, and developer reference. See the [wiki](https://github.com/hifihedgehog/PadForge/wiki) for deeper dives into the input pipeline, virtual controller backends, settings file format, and visualization renderer.

---

## Don't see your controller in the picker?

PadForge's controller picker is the set of HIDMaestro profiles that ship with a captured HID descriptor. A few controllers are missing their captures, so they don't appear yet. If you own one of those controllers, you can capture it yourself from inside PadForge. No extra tools, no admin.

To capture and use a profile locally:

1. Create or open any **Extended**-type slot.
2. On the Controller page, click **Imported profiles…** on the Extended config bar.
3. Under **Connected devices available to import**, pick your plugged-in device and click **Import**.
4. The new profile appears in the slot's dropdown with a "(User Generated)" suffix and stays available across every Extended slot from then on.

Profiles live inside `PadForge.xml` and travel with your settings.

To share a captured profile upstream:

1. In the same dialog, select your imported profile under **Your imported profiles**.
2. Click **Export…** and save the JSON.
3. Open a [profile contribution issue on HIDMaestro](https://github.com/hifihedgehog/HIDMaestro/issues/new?template=profile-contribution.yml) and attach the file. Once merged, the profile ships in the next HIDMaestro release for everyone.

To import a profile someone else captured:

1. Click **Import from file…** in the same dialog and pick the `.json` they sent you.

PadForge reads only the HID descriptor during capture. It does not record or forward your controller's input.

---

## Built on the work of these projects

PadForge stands on these projects. Please consider supporting them directly.

| Project | Role | License |
|---|---|---|
| [x360ce](https://github.com/x360ce/x360ce) | Original codebase this fork started from | MIT |
| [SDL3](https://github.com/libsdl-org/SDL) | Controller input: joystick, gamepad, and sensor enumeration | zlib |
| [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) | User-mode UMDF2 virtual HID controller engine with 225+ device profiles | MIT |
| [OpenXInput](https://github.com/hifihedgehog/OpenXinput) | Drop-in `xinput1_4.dll` replacement that filters PadForge's own virtual controllers from its own XInput view | upstream trademark disclaimer |
| [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) | 3D controller OBJ meshes (Xbox 360, Xbox One, DualShock 4, DualSense) | CC BY-NC-SA 4.0 |
| [Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack) | 2D controller PNG schematics (Xbox 360, Xbox One S, Xbox Series, DualShock 4, DualSense) | MIT |
| [HelixToolkit](https://github.com/helix-toolkit/helix-toolkit) | 3D viewport rendering for WPF | MIT |
| [WPF UI](https://github.com/lepoco/wpfui) | Fluent 2 design system for WPF | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM data binding framework | MIT |
| [NAudio.Wasapi](https://github.com/naudio/NAudio) | WASAPI loopback capture for audio-bass rumble | MIT |
| [HidHide](https://github.com/nefarius/HidHide) | Per-device hiding driver to prevent double input | MIT |
| [Windows MIDI Services](https://github.com/microsoft/MIDI) | Virtual MIDI device SDK | MIT |

---

## Donations

Knowing PadForge is useful is reward enough. If you truly insist on donating, please donate to your charity of choice and bless humanity. If you can't think of one, consider [Humanitarian Services of The Church of Jesus Christ of Latter-day Saints](https://philanthropies.churchofjesuschrist.org/humanitarian-services). Also consider donating directly to the upstream projects above. They made all of this possible.

**My promise:** PadForge will never become paid, freemium, or Patreon early-access paywalled. Free means free.

---

## License

This project is licensed under **CC BY-NC-SA 4.0** (Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International).

- **3D controller models** adapted from [Handheld Companion](https://github.com/Valkirie/HandheldCompanion) (CC BY-NC-SA 4.0). Copyright (c) CasperH2O, Lesueur Benjamin, trippyone.
- **2D controller assets** from [Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack) (MIT), by AL2009man.
- **Original codebase** forked from [x360ce](https://github.com/x360ce/x360ce) (MIT).
- **SDL3** is licensed under the [zlib License](https://github.com/libsdl-org/SDL/blob/main/LICENSE.txt).
- **HIDMaestro** is licensed under the MIT License.
- **WPF UI** is licensed under the MIT License.
- **Windows MIDI Services** is licensed under the MIT License.
- **HidHide** is licensed under the MIT License.
- **OpenXInput** ships only an upstream Microsoft-trademark disclaimer (no OSS license grant). Redistributed as-is under the same terms.

See [LICENSE](LICENSE) for the full license text.
