<!-- https://padforge.org/docs/features/force-feedback/ as PadForge's skill carries it, from padforge.org's wiki/features/force-feedback.md. -->
# Force Feedback

*Pass game rumble through to your pad or haptic mouse, route DirectInput force feedback to wheels and sticks, drive extra rumble from your system audio, and play the rumble stream through bass shakers.*

![Force feedback settings with motor strength sliders and test button](https://padforge.org/docs/images/pad-forcefeedback.png)

The **Force Feedback** card sits in the **Rumble & Haptics** section of a controller's page. Its settings belong to the device picked in the **Driven by** row of the controller's header, so its chip reads **This device**. Every physical pad mapped to the controller keeps its own Overall Gain, motor, Swap, Audio Rumble, Steering Angle Rumble and Constant Force values. Pick a different device in the Driven by row and the card follows it.

The card shows **Overall Gain**, the live **Motor Activity** bars and **Test Rumble**. Its **More** row holds each motor's gain and test, Fold Trigger Rumble into Main Motors, Swap Left and Right Motors, Steering Angle Rumble and Constant Force. Under it, **Audio Rumble** and **Trigger Routing** fold open as cards of their own.

See [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/) for trigger-motor effects.

The card appears on every slot type, including Keyboard + Mouse and MIDI, while the selected device is a controller-class device (gamepad, joystick, wheel, flight, or first-person controller), a Novint Falcon, a haptic mouse, or the Razer Sensa row. Select any other keyboard or mouse in the Driven by row and the card hides until you pick one of those again. Keyboard + Mouse and MIDI slots do not send rumble upstream to games, but Audio Rumble still feeds into the same combined-vibration buffer routed to whichever physical device is mapped. Test Rumble fires haptics on the currently selected device only, so you can verify one pad without buzzing the others.

To also play the game's rumble through speakers or a tactile transducer, see [Bass Shakers](#bass-shakers) below. The controller's motors keep running. That card belongs to the whole controller and has its own settings.

---

## How rumble works

Most controllers have two vibration motors:

| Motor | Character | Typical use |
|---|---|---|
| Left (low-frequency) | Heavy, deep thump | Explosions, collisions, engine vibration |
| Right (high-frequency) | Light, sharp buzz | Gunfire, road texture, UI feedback |

Games blend both motors for varied effects. A wall hit slams the left motor and gently buzzes the right. A machine gun pulses the right motor rapidly with the left idle.

---

## Rumble signal flow

Games never talk to your physical controller directly.

1. **Game** sends rumble to PadForge's virtual controller (Xbox, PlayStation, Nintendo, or Extended).
2. **PadForge** reads the left and right motor values.
3. **Your settings** apply: gain, per-motor strength, motor swap.
4. **PadForge** forwards the adjusted rumble to your physical controller.

PadForge applies this on every polling cycle, 1,000 times a second at the default polling rate. DualSense and DualShock 4 pads take it in their combined effect update, about 30 times a second.

The same slot rumble reaches a haptic mouse or the Razer Sensa row assigned to the slot. See [Haptic mice and the Razer Sensa row](#haptic-mice-and-the-razer-sensa-row).

### Multi-slot rumble

When one physical controller is assigned to several virtual slots, PadForge takes the highest motor value from any active slot. No rumble signals are lost.

### DualSense haptics played as audio

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-dualsense-haptics-as-rumble-poster.jpg"
       aria-label="Video: DualSense haptics on a rumble pad, recorded live in PadForge 5.2.1">
<source src="https://padforge.org/assets/video/padforge-tutorial-dualsense-haptics-as-rumble.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-dualsense-haptics-as-rumble.vtt" default>
</video>
<figcaption>A PlayStation virtual controller on DualSense (PS5): Full with an Xbox Series X Controller assigned, and its Overall Gain, recorded live in PadForge 5.2.1 in 59 seconds. PadForge 6 has since rebuilt the interface, so the screens differ from the steps on this page. No game ran during the take. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-dualsense-haptics-as-rumble-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

A game that supports the DualSense's haptics over USB plays them through the controller's audio and sends no rumble. On a **DualSense (PS5): Full** or **DualSense Edge (PS5): Full** virtual controller, PadForge turns the loudness of the left haptic channel into left motor rumble and the right channel into right motor rumble. Every controller on the slot gets it the way it gets game rumble: through its own Overall Gain, motor strengths and swap, and combined with any game rumble by taking the stronger of the two. Overall Gain at 0% turns it off for that controller. On a real DualSense that a game also writes through the virtual one, PadForge's rumble rides the game's own frames, so the game's writes do not cut it off. When a game hands its rumble back to the haptics, a DualSense that cannot play them gets the converted rumble at once.

Two kinds of controller get none of it, because they play the haptics themselves: a DualSense or DualSense Edge on the slot, and a Steam or Switch controller with **Play DualSense Haptics on This Controller** on. A DualSense that PadForge cannot send the haptics to, because its audio is unavailable, gets the rumble instead.

---

## Rumble settings

Overall Gain is on the card. The rest are under **More**.

| Setting | Range | Default | What it does |
|---|---|---|---|
| Overall Gain | 0–100% | 100% | Master vibration strength. Scales both motors. 0% disables rumble. |
| Left Motor | 0–100% | 100% | Low-frequency motor strength. Its **Test** button runs this motor alone. |
| Right Motor | 0–100% | 100% | High-frequency motor strength. Its **Test** button runs this motor alone. |
| Swap Left and Right Motors | On/Off | Off | Flips which physical motor receives the left vs. right signal. Use when rumble feels backwards. Not shown for a haptic mouse or keyboard, or for the Razer Sensa row, which play the stronger motor whichever side it is on. |
| Fold Trigger Rumble into Main Motors | On/Off | Off | Sends each trigger channel to the body motor on the same side. Shows only on a pad that rumbles but has no trigger motors, where impulse trigger data would otherwise go unfelt. |

---

## Steering Angle Rumble

**Steering Angle Rumble**, under the Force Feedback card's **More**, adds continuous feedback as the selected virtual stick axis moves away from center. It works with a non-FFB wheel mapped to a virtual Xbox or PlayStation controller. Select the wheel in the Driven by row, turn on **Enable Steering Angle Rumble**, and select the virtual axis used for steering. Its settings show while it is on.

![Steering Angle Rumble on an Xbox slot's Force Feedback card](https://padforge.org/docs/images/pad-steering-angle-rumble.png)

| Setting | Default | Range |
| --- | --- | --- |
| Enable Steering Angle Rumble | Off | On / Off |
| Virtual Axis | Left Stick X | Left Stick X/Y or Right Stick X/Y |
| Motor | Split by Direction | Split by Direction, Left Motor, Right Motor |
| Strength | 50% | 0-100% |
| Center Deadzone | 2% | 0-25% |

With Split by Direction, negative axis movement drives the left, low-frequency motor and positive movement drives the right, high-frequency motor. The two motors sit in different places and spin different weights on most pads, so the same angle can feel different on each side. Left Motor or Right Motor puts both directions on that one motor, so a turn of the same size feels the same either way. Intensity rises linearly from the edge of the center deadzone to the chosen strength at full travel. Center adds no rumble. Games and other enabled effects can still vibrate there.

The cue follows the final mapped output, including curves, inversion, layers, macros, and the combination of assigned sources. Each physical device keeps its own settings. Overall Gain, motor strength, Swap Left and Right Motors, and trigger routing apply afterward, as they do to game rumble. With Swap on, the picked motor swaps too. Game rumble and the steering cue combine by taking the stronger value on each motor.

This produces vibration rather than steering torque. Native Logitech, Fanatec, and Thrustmaster wheel writers, Fanatec pedals, and devices reporting native constant-force support keep their existing feedback and get no steering cue. Extended, Nintendo, Keyboard + Mouse, MIDI, and VR outputs get no steering cue. It reads the stick axes of an Xbox or PlayStation virtual controller only. Physical motor placement depends on the device.

The [steering-lock pulse](https://padforge.org/docs/guides/steering/#at-lock-feedback) remains a separate option for a brief cue at full lock. Ordinary wheel-axis mappings can use Steering Angle Rumble without enabling Winding, Angle to Axis, or Motion Lean.

---

## Test Rumble

Click **Test Rumble**, beside the Motor Activity bars, to send a short vibration pulse. Confirms the device supports rumble, PadForge is forwarding correctly, and your current settings produce the effect you wanted. Under **More**, the **Test** button on the Left Motor and Right Motor rows runs one motor alone.

If several physical devices share a slot, the test pulse only fires on the device you are configuring.

---

## Live motor activity

<!-- SCREENSHOT: pad-motor-activity -->
![Motor Activity with the Raw and Output bars for the left and right motors](https://padforge.org/docs/images/pad-motor-activity.png)

The **Motor Activity** well on the Force Feedback card draws two bars on one track for each motor (Left Motor, Right Motor) in real time as games send rumble:

- **Raw** (cold color): the rumble the game sent to the virtual controller, before this device's gain, strength, swap, constant force, trigger routing, or audio rumble touch it. It includes [haptics played as audio](#dualsense-haptics-played-as-audio).
- **Output** (ember): what the selected physical device receives.

The gap between the two bars is what this device's own settings did to the game's command. At defaults, with no audio rumble, constant force, or trigger routing, they match. The figure at the end of each row is the Output percentage. Hover the bars to read both, Raw and Output. Left is the low-frequency motor, right is the high-frequency motor.

Use the bars to:

- Watch which motor a game favors and how hard it pushes.
- See what your strength and swap settings deliver to the selected device.
- Confirm audio bass rumble is firing when you expect.

---

## Haptic fallback

Some devices (racing wheels, arcade sticks, flight sticks) have no rumble motors but support haptic force feedback. PadForge detects them and translates rumble into haptic effects.

| Priority | Strategy | Behavior |
|---|---|---|
| 1st | Left/Right effects | Two independent vibration channels, closest to dual-motor rumble. |
| 2nd | Sine wave effects | Periodic vibration. Slow oscillation for the heavy motor, fast buzz for the light one. |
| 3rd | Constant force effects | Steady force matching the stronger motor. Less nuanced but still usable. |

No setup. If a device supports neither rumble nor haptics, vibration is skipped.

A PC with the USB Vibration driver, whose force feedback module is `EZFRD64.DLL`, now runs DirectInput force feedback. PadForge's input library patches that module as it loads, on 64-bit Windows 10 and later. This has not run on hardware with that driver yet.

### Directional forces

For haptic devices, PadForge preserves force direction instead of collapsing it to one value.

- **Joysticks** with two or more haptic axes receive true 2D directional forces. A leftward force pushes harder on the left side.
- **Racing wheels** with one axis receive forces projected onto the steering axis. A rightward force pushes the wheel clockwise.

Directional forces apply to constant, ramp, and all periodic effects (sine, square, triangle, sawtooth up and down). If a device lacks the requested effect, PadForge falls back to scalar rumble.

---

## Condition effects

Position- or velocity-dependent forces used in racing and flight sims. Unlike rumble, they react to how you move the controller or wheel.

| Effect | Feel | Common use |
|---|---|---|
| Spring | Centering force, stronger farther from center | Wheel self-centering, joystick return-to-neutral |
| Damper | Speed-dependent resistance | Hydraulic steering simulation |
| Friction | Constant resistance in any direction | Road surface feel |
| Inertia | Resistance to speed changes | Heavy steering column or flight yoke mass |

Overall Gain scales condition effect strength. Wheels and force feedback sticks feel these best. Standard gamepads receive them as scalar rumble.

---

## Extended force feedback

When a slot uses **Extended** output, force feedback flows through the DirectInput pipeline.

- Games send DirectInput effects (constant force, sine, ramp, sawtooth, condition effects) to the Extended virtual controller.
- PadForge turns those effects into rumble or haptic output on your physical controller.
- Overall Gain applies to every effect. Left Motor, Right Motor, and Swap Left and Right Motors apply when an effect reaches the pad as rumble. Directional and condition forces on a wheel or force feedback stick use Overall Gain alone.

The Extended virtual controller advertises DirectInput force feedback by default. Turn **Force Feedback** off on the **Extended Controller** card in the Mappings section (with **Customize** on) and DirectInput games see a plain joystick with no force feedback instead.

> **Recognized wheels get native force feedback.** When the assigned wheel is a supported Logitech, Fanatec, or Thrustmaster model, PadForge sends these effects to the wheel using its own force feedback instead of a generic haptic. The **Wheel** card adds rotation range and RPM shift LEDs, and the **Centering** card adds Spring and Vehicle centering. See [Wheel](https://padforge.org/docs/features/wheel/) and [Wheel Centering](https://padforge.org/docs/features/wheel-centering/).

---

## PlayStation pads

When the physical device you mapped is a DualShock 4, DualSense, or DualSense Edge, PadForge sends rumble and lightbar together in one update to the pad, whatever the slot's output type. On the DualSense family that same update carries the adaptive triggers, the player row, and the mic LED. A DualShock 4 has none of those. Game rumble and your effect settings arrive in sync, so nothing fights over the device.

On a DualSense (PS5): Full slot, a DualShock 4 rumbles with the [haptics a game plays as audio](#dualsense-haptics-played-as-audio), and a DualSense plays them on its own actuators.

### PlayStation pads through a Padix converter

PS1 and PS2 pads reach the PC through Padix PSX/USB converter boards, sold by Buffalo as the BSGC101 (one port) and BSGC201 (two ports). PadForge drives the pad's two motors directly: the low-frequency channel sets the big motor's strength and the high-frequency channel switches the small motor on and off, the same split SDL uses for a DualShock 3. Buffalo's driver package is not needed, and installing it changes nothing here, because PadForge never goes through its DirectInput plug-in. The Force Feedback card appears for the converter whether or not that package is installed.

---

## Haptic mice and the Razer Sensa row

A haptic mouse assigned to a virtual controller takes that controller's rumble through this card, the way a gamepad does, with its own settings on each virtual controller. The Razer Sensa row does the same for Razer Sensa HD devices. Each plays a single strength, taken from the stronger of the two motors. None has trigger motors, so **Fold Trigger Rumble into Main Motors** shows for them.

![The Force Feedback card of a SteelSeries Rival 710 assigned to an Xbox virtual controller](https://padforge.org/docs/images/pad-forcefeedback-peripheral.png)

| Device | How the rumble plays | Needs |
|---|---|---|
| **Logitech MX Master 4** | Logitech's own haptic waveforms, sent straight to the mouse over HID++ | No Logitech software |
| **SteelSeries Rival 500, 700 and 710** | The tactile handler in SteelSeries GG | SteelSeries GG |
| **Logitech iFeel Mouse and iFeel MouseMan** | A buzz at the rumble's strength, sent straight to the mouse | The mouse on PadForge's driver, from **Play Rumble on This Mouse** on the Devices page |
| **Razer Sensa HD devices**, such as the Wolverine V3 Pro, Kraken V4 Pro and Freyja | The **Razer Sensa** row on the Devices page, through the Interhaptics engine inside PadForge | Razer Synapse 4 with Sensa HD Haptics, and the x64 build |

On an MX Master 4 and a Rival, rumble strength picks one of three levels: the mouse starts playing at 5 percent, and the level steps up at 33 and 66 percent. The MX Master 4 plays a light, medium or strong pulse, every 250 ms at the faintest rumble and faster as the rumble grows, up to every 80 ms at full strength. A Rival repeats a 30 ms pulse 5 times a second, a 55 ms pulse 7 times, or an 80 ms pulse 10 times.

SteelSeries GG drives every tactile Rival at once, so the Rival on the virtual controller with the smallest player number sets their rumble. Clicking the rumble mark of any tactile Rival on the [Devices](devices.md) page buzzes all of them, whether or not that Rival is assigned. The motor stops as soon as no Rival is assigned. Two seconds after PadForge stops using GG, GG gets its devices back.

In the x64 build, the Razer Sensa row appears on the [Devices](devices.md) page once Razer Synapse's Sensa HD Haptics runtime is installed. Synapse with Chroma alone brings no row. Assign it to a virtual controller like any device. PadForge runs the Sensa engine only while the row is assigned, or while the rumble mark on the Devices page buzzes it. In Synapse, set the device's Haptic Source to Sensa HD Games. Earlier versions sent rumble to Sensa HD devices from a Dashboard toggle. A toggle that was on now assigns the Razer Sensa row to the virtual controller with the smallest player number, if there is one.

A haptic mouse shared over [Remote Link](../guides/remote-link.md) takes rumble from the other PC's virtual controllers too. The Razer Sensa row is not shared.

An iFeel mouse buzzes at the rumble's own strength, from 2 percent up, once it is on PadForge's driver. The switch costs Windows the mouse as a pointer, so read [Logitech iFeel mice](https://padforge.org/docs/features/peripherals/#logitech-ifeel-mice) first. The other mice built on Immersion TouchSense take no rumble.

A line above the Force Feedback card says where the rumble goes:

| Line | When it shows |
|---|---|
| *Rumble plays on {0} as haptic pulses, sent straight to the device. Stronger rumble picks a stronger pulse and repeats it faster.* | A Logitech haptic mouse, with its name in place of `{0}`. |
| *Haptic feedback is turned off for {0} in Logi Options+, so it plays nothing until you turn it back on there.* | The mouse's own haptic feedback is off. |
| *Rumble plays on {0} as a buzz, sent straight to the device.* | An iFeel mouse on PadForge's driver, with its name in place of `{0}`. |
| *This device has not answered yet. Its rumble plays once it wakes up.* | The Logitech mouse is asleep, unplugged or not answering. |
| *Rumble plays through the tactile handler in SteelSeries GG.* | GG answers, and this Rival sets the rumble. |
| *SteelSeries GG drives every tactile Rival at once, so the one on Virtual Controller {0} sets their rumble.* | Another Rival, on the virtual controller numbered `{0}`, sets the rumble. |
| *Waiting for SteelSeries GG. Rumble plays once GG runs.* | GG does not answer. |
| *Rumble plays on your Razer Sensa HD devices through Razer Synapse.* | Synapse's Sensa runtime answers. |
| *Waiting for Sensa HD Haptics in Razer Synapse 4. Set the device’s Haptic Source to Sensa HD Games in Synapse.* | Synapse's Sensa runtime does not answer. |
| *Not Available on ARM64* | A Razer Sensa row in the ARM64 build, from settings the x64 build saved. |

The line is empty while PadForge's input engine is stopped, and on a virtual controller that is not live, such as one that is turned off. Only *Not Available on ARM64* still shows then.

See [Mice, Keyboards and Vendor Rows](https://padforge.org/docs/features/peripherals/) for the vendor rows and the lighting these devices take.

---

## Novint Falcon

A Novint Falcon has no motors of its own to rumble. Its slot's rumble and force feedback become force on the handle, which PadForge computes about a thousand times a second. A **Novint Falcon** card above the Force Feedback card holds the Falcon's feel, which button turns rumble into a kick toward you, the kick and shake strengths, the Aim feel's Brace and Preload buttons, gravity compensation and the force limit. The Falcon's **Calibrate Reach** belongs to the device, so it is on its row on the Devices page. The rumble settings above apply first, as for any controller. See [Novint Falcon](https://padforge.org/docs/features/novint-falcon/).

---

## Constant Force

A per-device override, under the Force Feedback card's **More**, that drives a continuous force on the assigned physical device until you turn it off. **Apply Constant Force** turns it on, and while it is on a 2D grid and signed X / Y sliders show under it, each from −1.00 to 1.00. Click or drag in the grid to set the direction and strength of the force vector. The grid origin is centered. The dot's distance from center sets magnitude. The dot marks the side the force comes from: Y at +1 (dot below center) pushes forward, and X at +1 (dot right of center) pushes left.

**Override-with-resume rule.** While the toggle is on, PadForge keeps applying the configured force as long as no game or program is sending non-zero force to that device/slot pair. The moment a game sends non-zero motor rumble or a force feedback effect, the game's force takes over. Rumble aimed only at the trigger motors leaves the constant force running. The moment the game returns to silence, the constant force resumes. Macro rumble counts as game force here, so a macro pulse takes over the same way, and the constant force resumes once the pulse ends.

### Routing

- **Force feedback devices (wheels, joysticks)** receive a real directional force. Single-axis devices (wheels) project the angle onto the steering axis, so the wheel pulls toward the direction you set.
- **Rumble-only devices (Xbox-style pads, generic gamepads)** get a quadrant motor mapping. The vertical component drives the heavy low-frequency motor, the horizontal component drives the light high-frequency motor, with a half-bleed across so diagonals engage both.
- **PlayStation pads (DualShock 4, DualSense, DualSense Edge)** route through the same combined update that carries their rumble.

**Persistence.** Saved per device, per slot. Survives PadForge restarts.

**Why it's there.** Issue #29 asked for it: a real wheel mapped to a virtual Xbox controller for a game that does not speak DirectInput. The force does not change with the wheel's position, so it pulls the wheel one way and never returns it to center. For a centering pull on a wheel that supports it, use the **Centering** card ([Wheel Centering](https://padforge.org/docs/features/wheel-centering/)).

The Motor Activity meter reflects the constant force when it is the active source. What you see on the meter is what the device receives.

---

## Audio Rumble

PadForge can drive controller vibration from any system audio (games, music, video). It follows your system audio output, isolates the bass frequencies, and turns bass energy into motor speed. The result is vibration that pulses with bass-heavy sounds.

This is the opposite direction from [Bass Shakers](#bass-shakers): Audio Rumble turns audio into vibration, Bass Shakers turns vibration into audio.

Audio bass rumble **combines with game rumble**. PadForge takes whichever signal is stronger at any moment. Audio fills gaps during cutscenes, menus, or quiet gameplay where the game sends nothing.

PadForge follows your default audio output device. Switch from speakers to headphones and you do not need to reconfigure anything.

### Turning it on

1. Open the controller's **Rumble & Haptics** section and pick the device in the Driven by row.
2. On the **Audio Rumble** card under the Force Feedback card, turn on **Enable Audio Rumble**.
3. Play audio. Open the card and adjust the sliders while you watch the Level meter.

### Settings

| Setting | Range | Default | What it does |
|---|---|---|---|
| Sensitivity | 1.0–60.0 | 4.0 | Bass intensity multiplier. Higher means stronger rumble from quieter audio. |
| Bass Cutoff (Hz) | 20–2000 Hz | 80 Hz | Low-pass cutoff. Lower keeps deep sub-bass only. Higher lets more of the sound through, up to engine notes well above the bass range. |
| Left Motor | 0–100% | 100% | Audio-driven low-frequency motor scale. |
| Right Motor | 0–100% | 100% | Audio-driven high-frequency motor scale. |

The Level meter shows current bass energy as audio plays.

### Practical scenarios

| Scenario | Sensitivity | Bass Cutoff | Notes |
|---|---|---|---|
| Action games without native rumble | 4.0 | 80 Hz | Raise sensitivity if rumble feels subtle. |
| Music listening | 6–8 | 100–150 Hz | Picks up kicks and bass guitar. Right motor at 50% for deeper feel. |
| Movies and video | 4.0 | 40–60 Hz | Isolates deep cinematic rumble (thunder, explosions, LFE). |
| Racing games with weak rumble | 4.0 | 40–60 Hz | Adds engine rumble and collision impacts from the game's own audio. |
| Wheel force feedback from engine sound | 4.0 and up | 300–1000 Hz | Some games' engines sit above 200 Hz. Raise the cutoff until the engine note comes through, then raise sensitivity for strength. |
| Horror and atmospheric games | 2–3 | 30–50 Hz | Subtle vibration from ambient bass drones. |

---

## Trigger Routing

![Trigger Routing card with per-trigger Source, Mode, Scale, and Activator controls](https://padforge.org/docs/images/pad-trigger-routing.png)

The **Trigger Routing** card, which folds open under the Force Feedback card, pushes the main rumble-motor strength into the trigger motors, one trigger at a time. Each trigger, **Left Trigger** and **Right Trigger**, carries its own Source, Mode, Scale, Activator, and Activator Mode.

| Control | Options | What it does |
|---|---|---|
| **Source** | None (Off), Left Motor, Right Motor, Max of Both Motors, Sum of Both Motors | Which body-motor value feeds this trigger. **None** is a no-op, so existing rumble is unchanged. |
| **Mode** | Off, Duplicate (Keep Main Motor), Redirect (Silence Main Motor) | **Duplicate** sends the value to the trigger and keeps the body motor running. **Redirect** sends it to the trigger and silences the body motor. |
| **Scale** | 0–200%, default 100% | Scales the routed strength for this trigger. |
| **Activator** | A button picker | Gates when the routing is live. An empty activator keeps the routing engaged in every mode except Toggle, which needs a button to switch it on. |
| **Activator Mode** | Hold, Toggle, Release to Aim, Always On | How the activator gates the routing. |

The Activator Modes: **Hold** engages the routing while the activator button is held. **Toggle** flips it on or off with each press. **Release to Aim** engages while the activator button is not held. **Always On** ignores the button.

Routing reaches both Xbox impulse triggers and DualSense Adaptive Trigger Vibration. The DualSense path fires no matter what output type the slot uses, so a PlayStation-output slot still gets it.

The **Rumble Trigger Override** macro action drives the trigger channel directly, with a paired **Stop Trigger Vibration** action to release it. See [Macros](../guides/macros.md).

Each setting row within each trigger (Source, Mode, Scale, Activator, Activator Mode) has its own reset button, and the card has a whole-card Reset.

See [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/) for game-driven impulse passthrough, audio bass on the triggers, and the steady-force trigger override.

---

## Reset controls

Every slider has its own reset button. Reset All restores everything on its card.

- **Force Feedback Reset All**: Gain 100%, both motors 100%, Swap Left and Right Motors off, Fold Trigger Rumble into Main Motors off, Steering Angle Rumble back to its defaults, Constant Force off at X = 0 and Y = 0, and the Audio Rumble card below it back to its defaults. It leaves Trigger Routing alone.
- **Audio Rumble Reset All**: Disabled, sensitivity 4.0, cutoff 80 Hz, both motors 100%.
- **Trigger Routing Reset All**: every trigger back to Source None (off), Mode Duplicate, Scale 100%, activator cleared, Activator Mode Hold.

---

## Bass Shakers

<!-- SCREENSHOT: pad-bass-shakers -->
![Rumble to Audio routing game feedback to a bass shaker](https://padforge.org/docs/images/pad-bass-shakers.png)

The **Bass Shakers** card, at the end of the Rumble & Haptics section, routes the game rumble and force feedback this virtual controller receives to an audio output as low-frequency tones for bass shakers and subwoofers. Game feedback and Test Rumble play through the audio output. Macro rumble stays on the controller. On a **DualSense (PS5): Full** or **DualSense Edge (PS5): Full** virtual controller, the haptics a game plays through the controller's audio drive **Low Motor** and **High Motor** too.

Unlike the Force Feedback card, Bass Shakers settings belong to the **whole controller**, not to a device, and its chip says so. The card shows on Xbox, PlayStation, and Nintendo slots, plus Extended slots that advertise force feedback (the **Force Feedback** toggle with **Customize** on, or a catalog profile that ships with it) and the Steam Deck Controller (Composite), SInput Gamepad and Joypad-OS Steam Input Gamepad profiles, whose rumble commands PadForge decodes directly. Other Extended slots, Keyboard + Mouse, MIDI, and VR hide it.

### Turning it on

1. Open the controller's **Rumble & Haptics** section and find the **Bass Shakers** card.
2. Turn on **Route Rumble to an Audio Output**.
3. Open **More** and pick an **Output Device**. System Default follows the Windows default playback device.
4. Play a game that rumbles, or click a voice's **Test** button.

Turning the toggle off keeps every setting. If the selected output device disappears, audio stays off until it returns, and the status line under the picker says so. Bluetooth audio devices add noticeable latency.

### Output settings

| Setting | Options / Range | Default | What it does |
|---|---|---|---|
| Channel Mode | Mono (All Channels) / Controller Stereo | Mono (All Channels) | Mono plays every voice on all speaker channels. Controller Stereo splits them like the controller: low motor and left trigger on the left channel, high motor and right trigger on the right. |
| Master Gain | 0–100% | 50% | Overall loudness applied after each voice’s own gain. Keep headroom so the four voices do not clip when they play together. |

### Voices

Four voices, one per feedback channel. Each row has an enable checkbox, a tone frequency, a gain, a live level meter, a **Test** button that plays the tone for 1.5 seconds, and a reset.

| Voice | Default frequency | Frequency range | Default gain |
|---|---|---|---|
| Low Motor | 40 Hz | 20–120 Hz | 100% |
| High Motor | 80 Hz | 20–120 Hz | 100% |
| Left Trigger | 60 Hz | 20–120 Hz | 100% |
| Right Trigger | 60 Hz | 20–120 Hz | 100% |

The default frequencies are starting points, not measured shaker frequencies. Shaker and amp response varies, so click **Frequency Sweep** to sweep a tone from 20 to 120 Hz over eight seconds on the low motor routing, note where your shaker responds strongest, and set the voice frequencies there. **Stop** ends the test tone or sweep.

**Reset All** on the card restores the output device to system default, Channel Mode to Mono (All Channels), Master Gain to 50%, and every voice to enabled, 100% gain, and its default frequency.

---

## Tips

- **Start with defaults.** 100% gain passes through exactly what the game sends. Only reduce if vibration is too strong.
- **Watch the motor activity bars** during gameplay to see which motor a game favors, then adjust.
- **Lower the right motor for less buzz.** 60–80% keeps deep thumps while softening high-frequency buzz.
- **Tune during gameplay rather than on Test Rumble alone.** Real games use varied patterns the test pulse cannot replicate.
- **Audio rumble runs alongside game rumble.** It activates when bass exceeds the game's own signal.
- **Each device gets its own settings.** Tune force feedback per controller on each slot.
- **Use Extended output for racing wheels.** Condition effects and directional forces are only available through the DirectInput pipeline.

---

## Troubleshooting

| Problem | Try this |
|---|---|
| No vibration at all | Check Overall Gain > 0%. Click Test Rumble to confirm the device supports it. |
| Vibration too weak | Raise Overall Gain and per-motor sliders. |
| Vibration too strong | Lower Overall Gain or per-motor sliders. |
| Rumble feels reversed | Turn on **Swap Left and Right Motors**. |
| Rumble stops intermittently | Check no other software (Steam Input, etc.) is competing for the device. |
| Audio rumble not working | Confirm audio plays through your default output. Check the Level meter. Raise sensitivity if it barely moves. |
| Audio rumble too aggressive | Lower sensitivity or drop the bass cutoff. |
| No sound from bass shakers | Check the routing toggle, the status line under the Output Device picker, Master Gain, and each voice's enable. Confirm the game rumbles at all on the Motor Activity bars. |
| Haptic mouse or Sensa device stays still | Read the line above its Force Feedback card. It says when the mouse is asleep, when its haptic feedback is off in Logi Options+, and when SteelSeries GG or Razer Synapse does not answer. |
| Wheel FFB feels wrong | Use an Extended output slot, not Xbox or PlayStation. |
| Condition effects feel weak | Raise Overall Gain. Condition effects scale with it. |

---

## Related pages

- [Controller Slots](controller-slots.md): create and configure virtual controllers.
- [Bass Shakers](https://padforge.org/docs/features/bass-shakers/): the whole-controller card in full, voice by voice.
- [Wheel](https://padforge.org/docs/features/wheel/): native force feedback for Logitech, Fanatec, and Thrustmaster wheels, plus rotation range and RPM LEDs.
- [Wheel Centering](https://padforge.org/docs/features/wheel-centering/): Spring and Vehicle centering for a force feedback wheel, and the Release Test.
- [Button and Axis Mappings](mappings.md): map physical inputs.
- [Stick Deadzones](stick-deadzones.md): thumbstick deadzone and response curves.
- [Trigger Deadzones](trigger-deadzones.md): pair trigger adjustments with rumble tuning.
- [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/): trigger-motor passthrough, audio bass on triggers, constant trigger force.
- [Adaptive Triggers](https://padforge.org/docs/features/adaptive-triggers/): DualSense trigger resistance, weapon, vibration, slope, and multi-position effects.
- [Devices](devices.md): check rumble and haptic capability per device.
- [Mice, Keyboards and Vendor Rows](https://padforge.org/docs/features/peripherals/): rumble and lighting on the mice, keyboards and vendor rows assigned to a virtual controller.
- [Novint Falcon](https://padforge.org/docs/features/novint-falcon/): rumble and force feedback as force on the Falcon's handle.
- [Troubleshooting](../troubleshooting.md): wider help with rumble issues.

---

*Last updated for PadForge 6.0.0.*
