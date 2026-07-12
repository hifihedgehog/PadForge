<!-- DRAFT for owner review. Not committed to the repo, not published.
     Image URLs resolve only once the v4.0.0 tag exists (raw links 404
     in preview until then). Feature list pending final cross-check
     against the wiki completeness gate. -->

# PadForge 4.0.0

The biggest PadForge release yet. **DualShock 3** pads pair over Bluetooth from inside the app and work over USB, with motion. **Wii pointer modes** turn the Wii Remote into an FPS mouse or a bordered pointer. **Mouse gestures** fire actions from a held button and a flick. **Stick-assisted triggers** give digital triggers a real analog ramp. **SOCD cleaning** comes to the Keyboard & Mouse controller. **Guide button LED brightness** for Xbox pads and the Steam Controller. And the whole app wears a new look.

| DualShock 3 | Wii pointer modes |
|:---:|:---:|
| ![DualShock 3](https://raw.githubusercontent.com/hifihedgehog/PadForge/v4.0.0/screenshots/ds3-pair.jpg) | ![Wii pointer modes](https://raw.githubusercontent.com/hifihedgehog/PadForge/v4.0.0/screenshots/pointer.jpg) |

| Mouse gestures | Guide button LED |
|:---:|:---:|
| ![Mouse gestures](https://raw.githubusercontent.com/hifihedgehog/PadForge/v4.0.0/screenshots/mouse-gestures.jpg) | ![Guide button LED](https://raw.githubusercontent.com/hifihedgehog/PadForge/v4.0.0/screenshots/guide-led.jpg) |

## DualShock 3

Pair a DualShock 3 over Bluetooth without leaving PadForge. Open the Devices page, click **Pair**, pick **DualShock 3**, and plug the pad in over USB once for the ceremony. PadForge installs the signed BthPS3 driver on demand, writes the pairing to the pad, and from then on the controller connects wirelessly on the PS button. USB works too, no pairing needed.

Motion is in: the sixaxis accelerometer and gyro flow through the same pipeline that drives gyro aim and the DSU motion server. The ten pressure-sensitive button axes map like any other source. Rumble, the player LED, and battery reporting all work over both transports. Removing the pad from the device list clears its Bluetooth pairing.

## Wii pointer modes

The Wii Remote's IR pointer now has modes: **FPS Mouse** for raw relative aim, aspect-corrected border modes that pin the cursor at the screen edge, and **IR Offscreen** handling that freezes the cursor when the sensor bar drops out of view instead of jumping. A macro action and a cycle action switch modes mid-game. Aim uses both sensor-bar dots with a margin stretch matched to the console lineage.

<!-- SECTION DRAFTS PENDING GATE CROSS-CHECK: -->

## Mouse gestures

Hold a mouse button, flick in a direction, and an action fires. Any set of mouse buttons, five gestures each (up, down, left, right, and click), configured on the Mouse tab.

## Stick-assisted analog triggers

A digital trigger (a keyboard key, a button) can now ramp like an analog one. Map a stick as the trim source and the stick's deflection raises and lowers the held trigger level. Trim rate, deadzone, and release behavior are per-mapping.

## SOCD cleaning

The Keyboard & Mouse controller resolves simultaneous opposing keys the way tournament hitboxes do: last-wins (Snap Tap), first-wins, or neutral. Applies across all mapped keys, not per pair.

## Guide button LED brightness

Set the Guide button brightness on Xbox One, Elite, and Series pads over USB, and the Home button LED on the 2015 Steam Controller. Fixed level or battery-following. A macro action changes it on the fly.

## Long-press and auto-cancel shift layers

A Toggle, Latch, or Sticky layer activator can now require a hold before it fires, and a Toggle layer can cancel itself after a period of no output. Both are per-activator with their own delay fields.

## Text Block macro action

A macro can type out a block of plain text, character by character, with a per-character delay.

## Clone Device 1:1

One click on an Extended slot copies a physical device's controls straight through, control for control, including trigger-to-trigger routing. Works on assigned devices even while they're offline.

## Raw joystick axes

Devices opened in raw mode expose every axis they have, up to 24, as generic "Axis N" sources instead of stopping at six.

## Steam Controller haptics

The haptic Sound Output for the 2015 Steam Controller, Steam Deck, and Steam Controller 2026 gains a high-tone cut/fold filter with boundary hysteresis, and a flattened downmix passband.

## A new look

<!-- TODO: one paragraph on the v4 visual overhaul (Ember accent, instrument-cluster status bar, profile pill, slot-card heat, dialog language). Verify scope against the delta before writing. -->

## Fixed

<!-- TODO: user-visible fixes from the delta: DS3 rumble dropout, M+ churn loop / identify dead-window, IR+MotionPlus coexistence, player-identity LED blips, device-dropout grace, mouse-macro poll-rate (3.6.1 carried), binding-error launch burst, no more diag.log/marker files beside the exe, macros QoL list. -->

## Credits

New in this release's reference credits: xbledctl (Guide LED), JoyShockMapper and GamepadMotionHelpers (steering and gyro-space math), SteamControllerSinger and SteamHapticsSinger (haptic layouts), hitboxer (SOCD semantics), Dolphin, DS4Windows, xone/xow, WiimoteLib, and joycon-singer as documentation references, and libusb, nipplejs, and SDL_GameControllerDB join the shipped-component attributions. Full details in the README and LICENSE.

## Install and upgrade

Replace `PadForge.exe` in your PadForge folder, or download the ZIP below and unzip. Settings carry over. The DualShock 3 Bluetooth driver installs from inside the app only when you pair a DS3.

## Compatibility

Windows 10 1809 or later, .NET 10 self-contained, runs elevated.

---

Full changelog: https://github.com/hifihedgehog/PadForge/compare/v3.6.1...v4.0.0
