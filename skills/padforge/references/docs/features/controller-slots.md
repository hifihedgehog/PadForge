<!-- https://padforge.org/docs/features/controller-slots/ as PadForge's skill carries it, from padforge.org's wiki/features/controller-slots.md. -->
# Controller Slots

*A slot is a virtual controller PadForge creates. The game sees it as a real, separate pad. Up to 16 slots in any mix of types.*

---

## What a slot owns

Each slot has its own:

- Controller type (Xbox, PlayStation, Nintendo, Extended, Keyboard+Mouse, MIDI, VR)
- [Button and Axis Mappings](mappings.md) (multi-source rows, shift layers, cross-device chords, custom formulas)
- [Stick Deadzones](stick-deadzones.md) and [Trigger Deadzones](trigger-deadzones.md)
- [Force Feedback](force-feedback.md) / rumble settings
- Bass Shakers (routes the game rumble and force feedback the slot receives to an audio output as low-frequency tones for bass shakers and subwoofers)
- [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/) (Xbox One / Elite / Series sources, routed to DualSense as AT Vibration)
- [Adaptive Triggers](https://padforge.org/docs/features/adaptive-triggers/) (DualSense / DualSense Edge)
- [Lighting](lighting.md) (DualSense / DualSense Edge / DualShock 4 lightbar, PS Move sphere, Guide button LED brightness for Xbox One / Elite / Series pads and the 2015 Steam Controller, and HOME button LED brightness for the Switch Pro Controller, right Joy-Con, combined Joy-Con pair, and charging grip)
- [Gyro](../guides/gyro.md) tuning, calibration, engage gates, and Motion Passthrough
- [Touchpad](touchpad.md) outputs (stick / D-pad / mouse) and gesture engine
- [Controller Audio](https://padforge.org/docs/features/controller-audio/) (speaker passthrough, macro sounds, volume)
- [Macros](../guides/macros.md)
- [Menus](../guides/menus.md)
- [Shift Layers](../guides/shift-layers.md)

The controller page's Copy / Paste / Copy From operations carry every per-device tuning on this list along with the mapping table. A Motion Gyro or Motion Accelerometer row that loses every input when its devices are matched to the target slot is dropped, as device cleanup does, so the target's motion auto-map fills it again. A motion row you switched off by emptying it, or one marked not to inherit, copies as it is. See [Button and Axis Mappings](mappings.md) for the matching rules.

---

## Add a controller

**Add a Controller** opens over the page from any of these:

- **[Home](dashboard.md).** The **Add Controller** tile after the last controller card, or **Use It** on Home's offer for a new device.
- **Sidebar.** **Add Controller** under the controller pills. It shrinks to a bare **+** when the sidebar is narrow.
- **[Devices](devices.md).** **Use It** on the row of a connected game controller nothing uses, or **New Controller** under **Used By** in a device's panel.
- **Home's first page.** "More types: Nintendo, Extended, MIDI and VR", while no controller exists. Its three tiles make an Xbox, PlayStation or Keyboard + Mouse controller at once.

![Add a Controller](https://padforge.org/docs/images/add-controller-popup.png)

The dialog reads "Pick what games should see. The controller is added as soon as you click." Top to bottom:

- **Driven By** lists the connected game controllers that nothing uses, each with its link and battery, then **Choose Later**. The first is picked. The new controller takes the device picked here. Opened for one device (from an offer, **Use It** or **New Controller**), the dialog lists that device. With no device to offer, the line is left out.
- **Common** holds three tiles: **Xbox** ("The layout most PC games expect."), **PlayStation** ("For games with PlayStation prompts, touchpad and lightbar.") and **Nintendo** ("Switch button layout, for games and emulators that expect it.").
- **More Outputs** holds four smaller tiles: **Keyboard + Mouse** ("Keys, cursor and scroll from a pad."), **Extended** ("Wheels, flight sticks and custom layouts."), **MIDI** ("Notes and control changes for music software.") and **VR** ("Virtual VR controllers through SteamVR.").
- The foot: "To change the type later, click the type chip on the controller. It takes one click."

A click on a tile makes the controller, closes the dialog and opens the new controller's page. The X or Escape closes the dialog with nothing made.

### Tile drivers

| Tile | Creates |
|------|---------|
| **Xbox** | Xbox virtual controller (needs HIDMaestro) |
| **PlayStation** | PlayStation virtual controller (needs HIDMaestro) |
| **Nintendo** | Nintendo virtual controller (needs HIDMaestro) |
| **Keyboard + Mouse** | Keyboard and mouse output (no driver for Windows input) |
| **Extended** | Extended DirectInput joystick (needs HIDMaestro) |
| **MIDI** | MIDI output device (Windows MIDI Services, or the legacy MIDI API without it) |
| **VR** | SteamVR hand pair (needs SteamVR) |

### Tiles that cannot be used

A tile dims and its line says why when:

- **MIDI has no API.** "Windows MIDI Services is not responding. See it in Settings, under Drivers.", or the reason the Windows MIDI Services row in Settings gives. Its tooltip reads "MIDI (not running, see Windows MIDI Services in Settings, under Drivers)".
- **SteamVR is missing.** "Needs SteamVR. Install it from Settings, under Drivers." Tooltip: "VR (requires SteamVR)".
- **A VR controller exists.** "There is a VR controller already. One drives both hands."
- **The type is at its cap.** The tooltip names the cap, such as "Xbox (max 16)".

Xbox, PlayStation, Nintendo, Extended and Keyboard + Mouse never dim for a missing driver. See [Driver Management](driver-management.md).

With 16 controllers the dialog says so: "PadForge has 16 controllers, the most it can run. Delete one to add another." Home's **Add Controller** tile and the sidebar's entry are gone while every type is at capacity.

---

## Pick a type

Pick the type when you add the controller. You can also switch an existing controller's type from the type chip on its sidebar pill or Home card. See [Changing a controller's type](#changing-a-controllers-type).

### Xbox

The default. 2 sticks, 2 triggers, 1 D-Pad, 11 buttons, plus Share on the Xbox Series presets. HIDMaestro-backed. Almost every PC game with controller support reads Xbox-style input natively, so this is what to pick when you do not know what to pick.

### PlayStation

HIDMaestro-backed. 2 sticks, 2 triggers, 1 D-Pad, 12 buttons including the touchpad click. The DualSense presets add Mic Mute, and the DualSense Edge adds its back paddles and Fn buttons. The two DualShock 3 presets have 11 buttons, Select and Start where the others have Share and Options, and no touchpad, as on the real pad. The controller ships as a DualSense. Switch to DualShock 4, DualShock 3 or DualSense Edge from the **Preset** picker in the page header. Pick this type for PlayStation PC ports with Circle / Cross / Triangle / Square prompts, emulators that need touchpad or lightbar, or motion streaming through the [DSU Motion Server](https://padforge.org/docs/reference/dsu-motion-server/).

For PCSX2 and RPCS3, pick **DualShock 3 (SIXAXIS): Full**. It presents a DualShock 3 the way Sony's sixaxis driver and DsHidMini's SXS mode do, with analog L2 and R2, the accelerometer and yaw gyro, and pressure on ten buttons, which its grid's [button pressure](mappings.md#button-pressure) rows set. A game's rumble comes back through it to your controller. The plain **DualShock 3 (SIXAXIS)** preset passes game rumble back too.

![PlayStation controller: the Preset picker selects DualShock 4, DualShock 3, DualSense or DualSense Edge, with the controller view below](https://padforge.org/docs/images/pad-playstation-configbar.png)

### Nintendo

HIDMaestro-backed. Four presets: **Nintendo Switch Pro Controller** (the default), **Nintendo Switch Pro Controller (USB)**, **Nintendo Switch 2 Pro Controller**, and **Nintendo Switch 2 Pro Controller: Full**. Pick one from the **Preset** picker in the page header.

The Switch Pro preset gives 2 sticks, 1 D-Pad, and 14 buttons with Nintendo lettering: B, A, Y, X, L, R, ZL, ZR, Minus, Plus, the stick clicks, Home, and Capture. Both Switch 2 Pro presets carry 21, adding C, GL, and GR among others, on their own wire order. None of the three has analog triggers (ZL and ZR are digital buttons), so Trigger Deadzones does not appear in Sticks & Triggers. There is no Customize surface on any of them. The controller deploys the preset as-is.

On the Switch Pro preset, map a motion source on the slot, or a stick on the [Motion Pitch, Yaw and Roll](mappings.md#motion-pitch-yaw-and-roll) rows, and it streams into the virtual pad's gyro and accelerometer, so games and emulators that read Switch Pro motion get it natively. Rumble the game sends to that virtual pad is decoded, so [Force Feedback](force-feedback.md) to the mapped device and Bass Shakers both work.

The two Switch 2 Pro presets are one pad in two forms, and the program that reads it decides which to pick.

**Nintendo Switch 2 Pro Controller** is a HID gamepad. DirectInput, Windows.Gaming.Input, browsers and every other program read its buttons and sticks. It carries neither motion nor rumble: its input report has no motion data, and HIDMaestro decodes no rumble for it.

**Nintendo Switch 2 Pro Controller: Full** is the pad as it appears over USB, with the second interface a program has to start it through. Steam reads it, and so does a program whose SDL is built with libusb. They get the gyro and the accelerometer, from a motion source on the slot or from the Motion rows, and the rumble they send comes back to the mapped device and Bass Shakers. Every other program sees a gamepad that never reports, which is what a real Switch 2 Pro Controller on a cable gives it on Windows. Like the default PlayStation preset, it rides HIDMaestro's USB transport, which installs the first time a slot needs it (see [Driver Management](driver-management.md)).

HIDMaestro's release tests ran the Full preset against Steam and against PadForge's own SDL build. No game has been run on it from PadForge yet, and stock SDL3 has not been run against it.

Joy-Cons, the NSO retro pads, and the GameCube adapter are not in this category. Their profiles live under Extended.

![Nintendo controller: the Preset picker holds Nintendo Switch Pro Controller, with the controller view below](https://padforge.org/docs/images/pad-nintendo-configbar.png)

### Extended (HIDMaestro)

A customizable virtual joystick. Up to 8 axes, 128 buttons, 4 POV hats. The **Preset** picker picks the Custom entry or one of the 102 HIDMaestro profiles it lists for Extended, and the Extended card under the picture sets how many of each the device exposes with **Customize** (HOTAS, wheels, third-party gamepads, and Valve's controllers). The DirectInput device name Windows reports comes from the active profile. A schematic view shows the live HID layout, except on the Valve profiles, which draw the pad itself.

Sim titles (DCS World, MSFS, X-Plane, iRacing) read DirectInput best. Extended slots also deliver [force feedback](force-feedback.md) to sim titles that support it.

![Extended (HIDMaestro) controller: the Preset picker in the header, the schematic of the live HID layout and the Extended card below](https://padforge.org/docs/images/pad-extended-configbar.png)

#### Valve personas

Five profiles in the Extended picker are Valve pads. Pick one and the slot takes that pad's identity, its control names in the mapping grid, and its body in the picture.

| Profile | Identity | Input frame |
|---|---|---|
| **Steam Deck Controller** | Valve `28DE:1205`, product string "Steam Deck", a standard HID gamepad (6 axes, 21 buttons, 1 hat) | Field-encoded HID, like any other Extended profile |
| **Steam Deck Controller (Composite)** | Valve `28DE:1205` on a USB persona, product string "Steam Controller", five interfaces taken from a real unit's USB dump: the lizard-mode mouse and keyboard, the vendor controller interface, and a CDC pair | The Deck's native 64-byte state report |
| **Steam Controller (Wired)** | Valve `28DE:1102`, product string "Steam Controller", the 2015 pad's vendor-page descriptor | The 2015 pad's native 64-byte report |
| **Steam Controller (Composite)** | Valve `28DE:1102` on a USB persona, product string "Wired Controller", three interfaces from a real unit's dump | The same 2015 frame |
| **Steam Controller (2026)** | Valve `28DE:1302` on a USB persona, one HID interface carrying the mouse, keyboard, and controller state as separate report ids | The 2026 pad's native 54-byte report |

A composite persona rides the real USB stack, so Steam and SDL see Valve hardware and run their own handshake against it. On the bench, live Steam claimed the Steam Controller (Composite) persona: it opened the device, reserved an XInput slot, took the input stream, and loaded its Steam Controller configuration. Stock SDL3 claims it as a Steam Controller with two touchpads, a gyro, and an accelerometer. What Steam does with the persona's motion data and haptics has not been checked on a live session.

![Extended slot on the Steam Controller profile: the Valve row names in the grid and the 2015 pad's own body in the preview](https://padforge.org/docs/images/pad-extended-steam-controller.png)

![Extended slot on the Steam Deck profile: the Deck's body with both trackpads and the four rear buttons](https://padforge.org/docs/images/pad-extended-steam-deck.png)

The mapping grid uses Valve's names. Buttons are A, B, X, Y, L1, R1, L3, Steam, and the pad clicks (Left Pad Click, Right Pad Click) on every profile, plus R3 on the Steam Deck and the 2026 pad, then per pad:

| Control | Steam Deck | Steam Controller (2015) | Steam Controller (2026) |
|---|---|---|---|
| Center buttons | View, Menu, Quick Access | Back, Start | View, Menu, Quick Access |
| Rear buttons | R4, L4, R5, L5 | Left Grip, Right Grip | R4, L4, R5, L5 |
| D-Pad | POV hat | POV hat, which the pad reports from the left trackpad's four click zones | Four discrete buttons |
| Right stick | Real stick | The right trackpad, ridden as a stick | Real stick |
| Touch sensors | Left Stick Touch, Right Stick Touch | None | Left Stick Touch, Right Stick Touch, Left Grip Touch, Right Grip Touch |

Every profile carries Left Stick X / Y, Right Stick X / Y, Left Trigger, Right Trigger, both trackpads as Left Pad X, Left Pad Y, Left Pad Touch, Right Pad X, Right Pad Y, Right Pad Touch, the Motion Gyro and Motion Accelerometer rows, and the Motion Pitch, Motion Yaw, and Motion Roll rows. The virtual pad carries one finger per trackpad, so the left pad follows the slot's first touch finger and the right pad the second.

Assign a Steam Deck, a Steam Controller, or any other gamepad and the slot auto-maps it: the face buttons, bumpers, sticks, triggers, View / Menu, Steam, and Quick Access from the pad's buttons, the rear buttons from SDL's four paddle positions (the two grips on the 2015 wire), the D-Pad from the device's hat, each trackpad from the device's matching touchpad (a one-pad device lands on the left pad), the pad clicks from the click buttons the device advertises, gyro and accelerometer from the device's sensors, and the stick and grip touch sensors from the ones the pad has. A Steam Deck lists its two stick tops and a 2026 Steam Controller its stick tops and grips under the pad's own name in the source picker. Over the preview, a row of capsules shows each sensor the slot's pad carries: a capsule lights while its output is on, and a click on it records its row.

Switching one Valve profile to another moves every binding to the same control on the new pad, drops bindings for controls the new pad lacks, and auto-maps the controls the old pad never had, such as the 2026 pad's D-Pad buttons after a Steam Deck. A profile restored at launch changes nothing. The same rule covers the two Nintendo presets.

### Keyboard+Mouse

Always available. Sends keyboard and mouse input instead of emulating a gamepad. Map buttons to keys, sticks to mouse movement, triggers to scroll. Good for older PC games without controller support, accessibility setups where a gamepad is easier to hold than a keyboard, and desktop or non-game use. An interactive keyboard-and-mouse preview lights up in real time as you press buttons.

A line at the foot of the picture's card picks what each half sends through. **Keyboard** and **Mouse** both start at **Windows Input**, which needs no driver: PadForge types and moves the pointer through Windows itself, and programs can tell that the input came from software. Either half can instead pick one of HIDMaestro's 51 keyboard or 52 mouse models. The slot then sends through a virtual keyboard or mouse that Windows and the programs that read devices see as that hardware, and HIDMaestro installs itself the first time a slot needs it. Each picker shows while the slot's surface mode drives its half, and each has a reset button.

Tags beside a picked model say what it can send: **Keys at Once** (how many keys it holds down together, or **Any**), **Media Keys** and **Lock Lights** on a keyboard, and **Buttons**, **Wheel** and **Tilt Wheel** on a mouse. The mapping table hides the rows the model cannot send and keeps their bindings, so they come back with Windows input or a model that has them. A keyboard with a mouse report of its own, such as the ThinkPad Compact Keyboard with its TrackPoint, also appears in the Mouse picker as **Same Device as the Keyboard**, and a mouse with keys appears in the Keyboard picker as **Same Device as the Mouse**. Picking it makes one device serve both halves, as the hardware does. [Supported Devices](https://padforge.org/docs/devices/supported/#virtual-keyboards-and-mice) lists every model.

The preview draws a picked keyboard as its own board, with the legends of the active input layout, and shows Num Lock, Caps Lock and Scroll Lock as Windows sets them on a keyboard that has lock lights. A picked mouse shows the buttons, wheel and tilt it has. Every key, mouse control and lock light in the preview is a control as well: a click records its row. See [Keyboard + Mouse preview](https://padforge.org/docs/features/visualization/#keyboard-mouse-preview).

The key rows run past F12: F13 to F24, the key an ISO keyboard has beside Left Shift (**Non-US \ |**), the Japanese **Hankaku/Zenkaku**, **No Convert**, **Convert** and **Kana**, and the media, browser and launch keys. A virtual keyboard presses the key that types a row's character in the foreground window's input layout, the key Windows input types, so a mapped A types "a" on AZERTY too.

Numpad Enter has its own row, apart from Enter, and its own entry in the SOCD key lists. The preview keyboard drew the key, but no row existed for a click on it to record into.

The output cards under the picture include a **Simultaneous Opposite Cardinal Directions (SOCD)** card. On a Keyboard+Mouse slot it cleans opposing key pairs, Snap Tap style: when both keys of a pair are held, the chosen rule decides which press the game sees. The modes are Off, Last Wins (Snap Tap), Neutral, and First Wins. Add your own key pairs. This keeps fighting-game and platformer inputs legal on keyboard.

When a mouse is the controller's selected device, the **Mouse Gestures** card appears in Sticks & Triggers. Hold a gesture button, flick, and one action fires on release: left, right, up, down, or a plain click below the flick distance. Any of the five mouse buttons can arm the recognizer, each carrying its own five gestures, and a Custom option arms it from a recorded input on any device instead. Flick Distance is in raw counts and Cooldown is in milliseconds. Map the five gestures in the mapping list, or use them as macro triggers.

![The Mouse Gestures card on a Keyboard and Mouse slot](https://padforge.org/docs/images/pad-mouse-gestures.png)

Xbox, PlayStation, Nintendo, and Extended slots get the same card for button pairs, with the same four modes. There it applies to the slot's final combined output, so physical, mapped, and macro presses are all cleaned. Xbox and PlayStation slots pick named buttons for each pair. Nintendo and Extended slots enter raw button indices.

![Keyboard and mouse controller preview](https://padforge.org/docs/images/pad-kbm-preview.png)

### MIDI

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-padforge-as-a-midi-device-poster.jpg"
       aria-label="Video: a controller as a MIDI device, recorded live in PadForge 5.2.2">
<source src="https://padforge.org/assets/video/padforge-tutorial-padforge-as-a-midi-device.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-padforge-as-a-midi-device.vtt" default>
</video>
<figcaption>A MIDI virtual controller fed by a DualSense: its channel, CC and note settings, the CC bars and note keys, and the Windows MIDI Services card, recorded live in PadForge 5.2.2 in 59 seconds. PadForge 6 has since rebuilt the interface, so the screens differ from the steps on this page. No music app was connected during the take. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-padforge-as-a-midi-device-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

Backed by Windows MIDI Services. Axes send Control Change (CC) messages. Buttons send Note On / Note Off. The **MIDI Output** card under the picture sets the channel, and under its **More** the CC count, note count, starting CC and note numbers, and velocity. Changes reach a running slot on its next poll, on the same port, so a DAW listening to it stays connected. A sustain pedal or other on/off controller (CC 64 to 69) that PadForge left on is turned off on its old channel and CC number when either changes, and when the slot stops. Creates a system-wide virtual MIDI device with no third-party loopback software needed. Turn any gamepad into a MIDI controller for DAWs (Ableton Live, FL Studio, Reaper), VJ software, or stage lighting.

Without Windows MIDI Services, the slot runs on the legacy MIDI API, which cannot create a device. The **MIDI Output** card then adds **Output Port**, and the slot sends to the existing port picked there, such as a MIDI interface, a synth or a loopback driver. A slot that cannot open its port says why in the message line: no port picked ("Virtual Controller *N* has no Output Port. Pick one on its page."), a port that is not connected, a port already in use, or a port that did not open. See [The legacy MIDI API](driver-management.md#the-legacy-midi-api).

![MIDI controller configuration](https://padforge.org/docs/images/pad-midi-configbar.png)

### VR

Backed by HIDMaestro's OpenVR driver and needs SteamVR installed. One slot drives both SteamVR hands, left and right, so only one VR slot can exist at a time. There is no per-slot VR config: the driver ships one identity and haptics fan out like game rumble. The driver registers its devices only while a consumer is live, so an idle machine shows no phantom controllers.

Sticks & Triggers and the output cards hide on a VR controller. See [Virtual VR Controllers](https://padforge.org/docs/features/vr-controllers/).

> **Note:** You can switch any slot to MIDI or VR and back to a gamepad type. Switching to MIDI needs a MIDI API that starts (Windows MIDI Services or the legacy MIDI API), and switching to VR needs SteamVR. Each switch re-runs auto-mapping for the new type.

---

## Controllers in the sidebar and on Home

Every controller has a pill in the sidebar, under the **Controllers** label that follows Home, Devices, Profiles and Extras, and a card on [Home](dashboard.md#controllers). The controller's page header shows the same type mark on a larger tile, which takes no click.

| Where | Layout |
|---|---|
| Sidebar pill | Power flame, controller number, type chip, `#N`, Preset name, then the delete **X**, which shows while the pointer is on the pill or the X has the keyboard focus |
| Home card | Type tile, type name and `#N`, Preset name, state pill and delete **X** in its head, then the picture and **Driven by**. See [Home](dashboard.md#detailed-card). |

A pill is a full-round outline as wide as the sidebar. The open controller's pill has an ember outline and a warm fill. Of the others, a forging pill has a soft ember outline and a soft light around it, a waiting one a gold outline, and a cold one a plain hairline.

- **Power flame.** Turns the controller on or off. Its tooltip is the state: Disabled, Initializing, No device, Engine stopped, Virtual controller failed, Awaiting devices, Idle or Forging.
- **Controller number.** The controller's place among all of them: 1, 2, 3 and on.
- **Type chip.** The type's mark. Ember while the controller is forging, steel otherwise. Click it to open the type bar. See [Changing a controller's type](#changing-a-controllers-type).
- **`#N`.** The controller's number among the controllers of its type. The second Xbox controller reads `#2`.
- **Preset name.** On Xbox, PlayStation, Nintendo and Extended controllers, the name of the controller's HIDMaestro profile, such as the DualSense. On a Keyboard + Mouse controller, the input surfaces its **Input Surfaces** picker sets. On MIDI and VR controllers, the type's name, since they have no Preset. A long name trims at rest, scrolls to its end while the pointer is on the pill, and shows whole in its tooltip.

A click on the rest of the pill opens the controller's page. Drag a device's row from the [Devices](devices.md) page onto a pill to assign the device to that controller.

**Add Controller** follows the last pill while at least one type has room.

### Changing a controller's type

Click a type chip, on a sidebar pill or a Home card, and the type bar opens with the controller's current type lit: beside the pill, or under the card. Click another type to change the controller. Clicking the current type changes nothing. MIDI and VR carry the same requirements as when you add them, and say why when they cannot be picked.

A type change re-runs auto-mapping for the controller's devices, moves the controller to the end of its type group, resets the Preset to the new type's default and returns the stick and trigger dead zone settings to their defaults. A Preset is not remembered per type. A [profile](../guides/profiles.md) keeps one.

---

## Quick type reference

| Situation | Recommended type |
|-----------|------------------|
| Steam, Epic, or Microsoft Store game (Elden Ring, Forza, Halo, etc.) | **Xbox** |
| PlayStation PC port with PS button prompts | **PlayStation** |
| Streaming gyro/motion to Cemu, Yuzu, or another emulator | **PlayStation** + DSU |
| Emulator or game that reads a Switch Pro Controller, with native gyro | **Nintendo** |
| Gyro from a stick or buttons ([Motion Pitch, Yaw and Roll](mappings.md#motion-pitch-yaw-and-roll)) | **PlayStation** or **Nintendo** |
| Flight sim, racing sim, or space sim | **Extended** |
| HOTAS, racing wheel, or custom button box | **Extended** |
| Game with keyboard+mouse only | **Keyboard+Mouse** |
| Accessibility or desktop use | **Keyboard+Mouse** |
| DAW, VJ software, or stage lighting | **MIDI** |
| Driving a SteamVR hand pair from a gamepad | **VR** |
| Not sure | **Xbox** |

When in doubt, start with Xbox. It has the widest game support and you can switch later.

---

## Slot limits

Up to **16 slots total** across all types. Any mix is allowed within each type's own cap.

### Per-type limits

| Type | Max slots | Reason |
|------|:---------:|--------|
| **Xbox** | 16 | HIDMaestro creates all 16. XInput games see only the first 4. SDL/DirectInput games see all 16. |
| **PlayStation** | 16 | HIDMaestro creates all 16. SDL/DirectInput games see all of them. |
| **Nintendo** | 16 | HIDMaestro creates all 16. SDL/DirectInput games see all of them. |
| **Extended** | 16 | HIDMaestro per-type cap, same as the other gamepad types. |
| **Keyboard+Mouse** | 16 | Capped at the overall slot count. |
| **MIDI** | 16 | Capped at the overall slot count. |
| **VR** | 1 | One slot already drives both SteamVR hands, and SteamVR tracks one left+right pair. A second slot would fight the first over the same two devices. |

Most users need 1 to 4 slots. The 16-slot cap covers arcade cabinets, sim rigs, and multi-output MIDI stations.

### XInput 4-slot visibility limit

The Windows **XInput API** only exposes **4 Xbox-type controllers**. This is a Windows limit, not a PadForge one.

- **4 or fewer Xbox slots.** Every game sees all of them.
- **More than 4 Xbox slots.** All 16 exist and work. **XInput** games detect only the first 4. **SDL, DirectInput, and raw HID** games see all 16.
- PlayStation slots are unaffected by the XInput cap.
- Nintendo, Extended, Keyboard+Mouse, MIDI, and VR slots are unaffected.

**Tip:** For more than 4 local-multiplayer gamepads, mix Xbox with PlayStation or Nintendo slots, or use Extended (no 4-controller cap).

### DSU/Cemuhook 4-slot limit

The [DSU/Cemuhook motion protocol](https://padforge.org/docs/reference/dsu-motion-server/) supports a maximum of **4 slots**. Only the first 4 broadcast motion data. Slots 5 to 16 work normally for gamepad output but skip DSU. This is a protocol limit.

---

## Enable, disable, delete

### Enable and disable

Click the power flame on a controller's sidebar pill, or the state pill on its [Home](dashboard.md#controller-state) card, to turn it on or off. The page header's power button does the same, with the state word beside it. The flame's fill and glow show the controller's state.

| Flame | Meaning |
|-------|---------|
| **Ember, with a glow** | Enabled with devices mapped, output live. Stays ember through the inactivity grace period (all of the controller's mapped devices offline for the **Inactivity Timeout**, default 60 seconds, set on the [Settings](settings.md#inactivity-timeout) page, 0 disables it). |
| **Gold** | Enabled and mapped, but no live virtual controller. Nothing is connected, the engine is stopped or idle, or the inactivity timeout fired and tore the controller down. The configuration is kept. A device coming back while the engine runs rebuilds the virtual controller. |
| **Gold, tooltip "Virtual controller failed"** | The driver could not create the virtual controller. Switching the controller's Preset or toggling it retries the create. |
| **Steel outline** | Off, or on with no devices mapped. Games cannot see it. Settings are kept. |
| **Flashing ember** | Initializing. The virtual controller is coming up. |

Disabling a controller hides it from games without losing its configuration.

### Delete

Click the **X** on a controller's sidebar pill or its Home card, or **Delete** in the **More** menu of its page header. One click deletes, with no confirmation.

- The virtual controller is removed from the system.
- All its settings (mappings, deadzones, macros) are permanently deleted.
- Physical devices are unassigned from this controller only.
- The remaining controllers shift down. Deleting controller 2 of four renumbers 3 and 4 to 2 and 3.

> **Tip:** To hide a controller temporarily without losing settings, disable it instead.

---

## Reorder controllers

Drag a controller's card on [Home](dashboard.md#opening-and-reordering) or its pill in the sidebar to change the order.

### How to reorder

1. Press and hold a card or a pill.
2. Drag it to the spot you want.
3. Release.

### Why order matters

The order sets the controller number. **Controller 1** is whichever comes first. Games assign player numbers based on this order. Emulators bind controllers to player ports.

### What happens on reorder

Reordering two controllers of the same type does not disconnect the game, as long as both use the same Preset. The swap is instant and the game keeps seeing both controllers.

- **Same Preset on both moved controllers.** No rebuild. The game notices nothing.
- **Different Presets.** Only the positions whose Preset changed rebuild and reconnect at their new spot. Controllers that kept their Preset stay connected.
- **Disabled or no-device controllers.** Invisible to games. Moving them changes nothing until a device comes back.

### Automatic type grouping

New controllers group by type. Xbox first, then PlayStation, Nintendo, Extended, Keyboard + Mouse, MIDI, VR. This keeps XInput numbering predictable.

Dragging reorders controllers inside a type group. Cross-type drops are ignored, so the grouping itself stays fixed.

---

## Open a controller

Click a controller's card on [Home](dashboard.md) or its pill in the sidebar. The controller's page opens with its header and a row of six sections.

<!-- SCREENSHOT: pad-config-tabs -->
![The header and the six section buttons of a controller's page](https://padforge.org/docs/images/pad-config-tabs.png)

The header holds the type tile, the type name and number, the **Preset** picker (**Input Surfaces** on a Keyboard + Mouse controller), the power button with its state word and the **More** menu. A second line holds a pill for each device that drives the controller and **Add Device**. A VR controller's header says whether SteamVR is there.

| Section | What it holds |
|---------|---------------|
| **Mappings** | Two views under a switch. **Picture**: the live controller view ([3D and 2D Visualization](https://padforge.org/docs/features/visualization/)) with the picked control's rows under it, then the output cards (the SOCD cleaner and **Keep Controller Awake**), an Extended controller's card, or a MIDI controller's **MIDI Output** card. **List**: every mapping row, with shift layers ([Button and Axis Mappings](mappings.md)). A Keyboard + Mouse controller names this section **Keys & Mouse**, a MIDI controller **Notes & Controls**, and a VR controller **Hands**. |
| **Sticks & Triggers** | [Stick Deadzones](stick-deadzones.md), [Trigger Deadzones](trigger-deadzones.md) and a mouse's gestures. |
| **Rumble & Haptics** | [Force Feedback](force-feedback.md), [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/), [Adaptive Triggers](https://padforge.org/docs/features/adaptive-triggers/), the [Wheel](https://padforge.org/docs/features/wheel/) and Bass Shakers. |
| **Motion & Touch** | [Gyro](../guides/gyro.md), [Touchpad](touchpad.md) and the IR pointer. |
| **Lights & Sound** | [Lighting](lighting.md) and [Controller Audio](https://padforge.org/docs/features/controller-audio/). |
| **Macros & Menus** | [Macros](../guides/macros.md) and [Menus](../guides/menus.md), a list of both beside the editor of the one picked. |

A section shows only while one of its parts has something for this controller and its selected device, so a controller with no gyro, touchpad or pointer has no Motion & Touch. Mappings and Macros & Menus are always there.

**Bass Shakers** shows on Xbox, PlayStation and Nintendo controllers, and on Extended controllers with force feedback enabled (the Force Feedback switch decides when Customize is on, the profile's own descriptor when it is off). With Customize off it also shows on the Steam Deck Controller (Composite), SInput Gamepad and Joypad-OS Steam Input Gamepad profiles, whose rumble commands PadForge decodes without a force feedback descriptor. It routes the game rumble and force feedback the virtual controller receives to an audio output as low-frequency tones for bass shakers and subwoofers. Extended controllers without force feedback, Keyboard + Mouse, MIDI and VR controllers have no feedback surface and hide it.

The output cards show on every type except MIDI and VR. The SOCD cleaner is among them for every type that shows them. **Keep Controller Awake** sits beside it on Xbox and PlayStation controllers only. That card holds a small stick deflection so games that switch their prompts the moment they see mouse or keyboard input keep treating the pad as the active device.

The other parts follow the physical device selected in the header, not the controller's output type. Assign a DualSense to a PlayStation controller and Adaptive Triggers appears. Assign a DualShock 4 to the same controller and it does not, because the DualShock 4 has no adaptive triggers. Select another device and the parts follow it.

| Part | Section | Appears for |
|-----|---------|-------------|
| [Stick Deadzones](stick-deadzones.md) | Sticks & Triggers | every controller except MIDI and VR (gated by type, not the device) |
| [Trigger Deadzones](trigger-deadzones.md) | Sticks & Triggers | every controller except Keyboard + Mouse, MIDI and VR (gated by type, not the device). Nintendo and Extended controllers whose profile has no analog triggers hide it too, since the Switch Pro's ZL / ZR are digital |
| Mouse gestures | Sticks & Triggers | a mouse |
| [Force Feedback](force-feedback.md) | Rumble & Haptics | a stick-class input, a haptic mouse or keyboard, or a Novint Falcon (hidden for other keyboards, mice, touchpads and MIDI) |
| [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/) | Rumble & Haptics | Xbox One / Elite / Series trigger motors |
| [Adaptive Triggers](https://padforge.org/docs/features/adaptive-triggers/) | Rumble & Haptics | a DualSense or DualSense Edge |
| [Wheel](https://padforge.org/docs/features/wheel/) | Rumble & Haptics | a racing wheel. A recognized Logitech, Fanatec or Thrustmaster model gets the Wheel card (rotation range, RPM shift LEDs) and the Centering card. A wheel routed through SDL with a spring or a constant force gets the Centering card alone |
| [Gyro](../guides/gyro.md) | Motion & Touch | a gyro or an accelerometer. The gyro-rate cards need a gyro |
| [Touchpad](touchpad.md) | Motion & Touch | a touch surface (DualShock 4, DualSense, the Steam Controller family, Steam Deck, Windows Precision Touchpads) |
| IR pointer | Motion & Touch | an IR camera ([Wii Remote](https://padforge.org/docs/devices/wii-controllers/)) |
| [Lighting](lighting.md) | Lights & Sound | a lightbar or lit sphere (DualSense family, DualShock 4, PS Move), a Guide LED (Xbox One / Elite / Series, 2015 Steam Controller), a HOME button LED (Switch Pro Controller, right Joy-Con, combined Joy-Con pair, charging grip), or the lighting of a keyboard, mouse or vendor row |
| [Controller Audio](https://padforge.org/docs/features/controller-audio/) | Lights & Sound | a speaker (DualSense family, DualShock 4, Wii Remote), or a haptic actuator that plays macro sounds as tones (Joy-Con, Switch Pro Controller, Steam Controller 2015, Steam Deck, Steam Controller 2026) |

Keyboard + Mouse controllers keep [Stick Deadzones](stick-deadzones.md) for mapping the sticks to mouse movement and scroll. MIDI controllers hide the stick and trigger parts, since a MIDI controller maps to CC and note values, not sticks and triggers.

---

## One device on several controllers

One physical controller can feed several virtual controllers at once.

### Use cases

- **Split output.** One controller drives an Xbox controller (for the game) and a MIDI controller (for music) at the same time.
- **Mirrored output.** The same controller into two Xbox controllers so two in-game characters move identically.
- **Sim rigs.** Route one HOTAS to several Extended controllers with different axis configs.

### How to assign

1. **[Devices](devices.md) page.** A device's panel lists a chip for each controller under **Used By**. Click a chip to assign or unassign.
2. **Sidebar.** Drag a device's row onto a controller's pill.
3. **The offer band.** With [Assignment Prompts](settings.md#assignment-prompts) on, a controller's page shows a band under **Driven by** when a game-input device is online and not yet on that controller: "*Device name* just connected. Assign it to this virtual controller?" with **Assign** and **Not Now**. Two settings decide who is offered, both on by default. **Offer New Devices to the Open Virtual Controller** covers devices PadForge has never seen before. **Offer Any Connecting Device When the Open Virtual Controller Has No Devices** covers any device, new or known, while the open controller has nothing assigned. The band is a standing condition, not a one-shot event: it appears whether the device connected before or after you opened the page, and it stays until you assign the device, click Not Now (which dismisses that device for that controller for the rest of the session), or the device goes offline. Keyboards, mice, touchpads and media-key devices are never offered, and nothing is offered on the first device walk after the engine starts.

Each pairing of a device and a controller has its own mappings, deadzones and settings. The same device on controller 1 and controller 3 can have completely different configs.

---

## Related pages

- [Home](dashboard.md): the controller cards and engine status.
- [Devices](devices.md): assign physical controllers to virtual controllers.
- [Installation](https://padforge.org/docs/start/installation/): first-time setup and driver install.
- [Profiles](../guides/profiles.md): save and switch controller configs per game.
- [Button and Axis Mappings](mappings.md): map physical inputs to virtual outputs, with shift layers.
- [Shift Layers](../guides/shift-layers.md): per-controller overlay mapping tables.
- [Macros](../guides/macros.md): button combo triggers, axis triggers and Custom Expression triggers.
- [Menus](../guides/menus.md): on-screen ring and grid menus.
- [Stick Deadzones](stick-deadzones.md) and [Trigger Deadzones](trigger-deadzones.md): stick and trigger tuning.
- [Force Feedback](force-feedback.md) and [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/): body-rumble and trigger-motor effects.
- [Adaptive Triggers](https://padforge.org/docs/features/adaptive-triggers/) and [Lighting](lighting.md): DualSense-specific effects.
- [Touchpad](touchpad.md) and [Controller Audio](https://padforge.org/docs/features/controller-audio/): touchpad gestures and speaker output.
- [Gyro](../guides/gyro.md): motion-sensor mapping.
- [Wheel](https://padforge.org/docs/features/wheel/): native force-feedback wheel settings.
- [Virtual VR Controllers](https://padforge.org/docs/features/vr-controllers/): the VR type and its SteamVR hand pair.
- [Driver Management](driver-management.md): driver requirements per controller type.

---

*Last updated for PadForge 6.0.0.*
