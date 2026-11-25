<!-- https://padforge.org/docs/guides/gyro/ as PadForge's skill carries it, from padforge.org's wiki/guides/gyro.md. -->
# Gyro

*Per-pad motion-sensor mapping with grip, calibration, sensitivity, smoothing, and engage gates.*

<!-- SCREENSHOT: pad-gyro -->
![The Gyro card in the Motion & Touch section: Motion Passthrough, Calibration, the two sensitivities, the live horizon and rates, and the lines below](https://padforge.org/docs/images/pad-gyro.png)

The **Gyro** card sits in the **Motion & Touch** section of the controller page. It appears when the device picked under **Driven by** exposes a gyroscope or an accelerometer. Tuning saves per pad per slot, so the same pad on two different slots can carry two different feels.

The card's face holds **Motion Passthrough**, **Calibration**, **Horizontal Sensitivity** and **Vertical Sensitivity**, and a live well: a horizon drawn from the accelerometer beside the Pitch, Yaw and Roll rates in °/s, with the accelerometer's X, Y and Z in g under them. **More** opens Units, the three Invert switches, Real-World Calibration and each sensitivity's degrees per turn. Under it sit lines that fold open: **Grip**, **Engage**, **Response Shaping**, **Reference Frame**, **Motion Steering** and **Gyro Tilt**, then **Pitch and Roll Simulation** and **Compass** on the controllers that have them.

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-gyro-and-flick-stick-poster.jpg"
       aria-label="Video: gyro aim, the aim button and Flick Stick, recorded live in PadForge 5.2.1">
<source src="https://padforge.org/assets/video/padforge-tutorial-gyro-and-flick-stick.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-gyro-and-flick-stick.vtt" default>
</video>
<figcaption>Calibrate Gyro, the left trigger as the aim button, then Flick Stick on Mouse X and Gyro Pitch on Mouse Y of a Keyboard + Mouse controller, recorded live in PadForge 5.2.1 in 1 minute 31 seconds. PadForge 6 has since rebuilt the interface, so the screens differ from the steps on this page. The DualSense sat on the desk, so nothing turns or aims on screen. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-gyro-and-flick-stick-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

---

## When the card shows

The card is visible when the selected mapped device reports a motion sensor of either kind. DualSense, DualShock 4, Joy-Con, Switch Pro, Switch 2 Pro, Steam Controller, Steam Deck, the Wii Remote, a handheld PC's own motion sensor, and a Sony headset's head motion all qualify. A device with no gamepad mapping qualifies too, among them the Myo armband, the Rift DK1 and the Windows Mixed Reality motion controllers. Pads with no motion sensor never see the card.

On a device with an accelerometer and no gyroscope, such as a Wii Remote without Motion Plus, the card shows only what reads the accelerometer: the horizon, Grip, Motion Steering, and Gyro Tilt. The rate rows (Motion Passthrough, Calibration, the sensitivities and their More, the live rates) and the Engage, Response Shaping and Reference Frame lines stay hidden.

A DualShock 3 also gets the [Pitch and Roll Simulation](#pitch-and-roll-simulation) line, because its gyro senses one axis.

---

## Grip

<!-- SCREENSHOT: pad-gyro-grip -->
![The Grip card with the Held As dropdown](https://padforge.org/docs/images/pad-gyro-grip.png)

**Grip** is the first line under the card. Its one row, **Held As**, tells PadForge how the controller sits in your hands. The driver delivers every controller's motion in the frame of its natural hold, which for a Wii Remote is aimed at the screen. Hold the remote sideways and every axis a game reads is a quarter turn off. Held As rotates the gyro, the accelerometer, and the gravity estimate together into the frame of the hold, for the mapping rows, for the motion the virtual controller reports, and for the [DSU Motion Server](https://padforge.org/docs/reference/dsu-motion-server/) feed.

The four holds name the face (the button side) and the top edge (the pointer end) so there is no guessing which "sideways" is meant.

| Held As | Face points | Top edge points | Rotation, (x, y, z) becomes |
|---|---|---|---|
| Pointing | Up | At the screen | (x, y, z), unchanged. The default. |
| Sideways, Face Up | Up | Left | (z, y, -x) |
| Wii Wheel, Face Toward You | Toward you | Left | (z, x, y) |
| Upright | Toward you | Up, at the ceiling | (x, -z, y) |

Sideways, Face Up is the NES-style hold, and its rotation is the same swap SDL applies to a single left Joy-Con held sideways. Wii Wheel, Face Toward You is the hold the wheel shell puts the remote in. In that hold the steering motion is a turn about the remote's face, which is body yaw, and the table turns it into the roll a flat pad reports when tipped like a wheel. The two sideways holds differ only in which way the face points, and they need different tables: the face-up table steers on yaw in the wheel hold.

All three rotations are proper rotations, so one table serves the gyro, the accelerometer, and gravity alike, and the motion the virtual controller reports stays consistent with the mapping rows.

**The D-pad follows the grip.** Under Sideways, Face Up and Wii Wheel, Face Toward You the hat turns a quarter turn with the sensors, so the remote's physical Right reads as Up, Up as Left, Left as Down, and Down as Right, with the diagonals in between. Upright leaves the hat alone. The rotated hat feeds mapping rows, macro triggers, and the Record button, so a press recorded in the hold fires on that same press. The Devices page keeps showing the physical direction.

**A grip change recenters.** Switching Held As re-references that device's motion state on the slot: the gravity estimate re-seeds from the next accelerometer sample, and the lean, tilt, and shake neutrals re-capture. Hold the controller in the new grip for a moment after switching. Calibration is not affected, because the gyro bias is sampled in the sensor's own frame and subtracted there before the rotation. Calibrating and changing the grip stay independent.

Held As is per pad per slot like the rest of the card, and it rotates the body sensor only. A Nunchuk or a standalone left Joy-Con is a separate body in the other hand and keeps its own frame. On a combined Joy-Con pair, the fused Gyro Pitch / Yaw / Roll rows rotate both halves together, while the Left Joy-Con rows keep the left half's frame.

The row's reset button (**Reset Grip**) returns Held As to Pointing.

---

## Pitch and Roll Simulation

A DualShock 3 has one gyro axis. It senses turning the pad left and right, and reads zero for tilting it up and down or rolling it. On a DualShock 3 the card shows a **Pitch and Roll Simulation** line, and its **Simulate Pitch and Roll** switch fills those two axes with rates worked out from the accelerometer. Yaw stays the gyro's own reading. The simulated axes reach the mapping rows, gyro to mouse, the motion the virtual controller reports, the [DSU Motion Server](https://padforge.org/docs/reference/dsu-motion-server/), and this card's live rates. Gyro Tilt and calibration keep reading the sensor, and a [Remote Link](remote-link.md) peer receives the raw sensor and turns the option on for its own copy of the pad. It is off by default and, like the rest of the card, set per pad per slot. No other controller shows the line.

The rate is the one an accelerometer can see. Gravity points the same way in the world, so as the pad turns, the direction the accelerometer reads turns the other way, and the rate is how fast it moves. That has limits a real gyro doesn't:

- A rotation about any level axis reads in full. Rolled to one side, a pitch reads less, cos² of the roll angle: 75% at 30°. Tilted back, a roll reads less the same way.
- Turning the pad around the vertical never shows on pitch or roll. Turning it in its own plane while it is tilted back shows partly as roll.
- The accelerometer can't tell a push from a tilt. A shove up or down reads as no motion while it lasts. A turn made during a shove shorter than a quarter second arrives just after it, and a longer shove starts the estimate over. Moving the pad sideways or forward without turning it reads as a brief pitch or roll.
- The accelerometer counts in steps of half a degree. A pad lying still flickers one step now and then, which reads as up to 5°/s for a moment at the default smoothing.

**Simulation Smoothing** sets how long the accelerometer is averaged before the rates are worked out, 20 to 250 ms, default 100 ms. Longer is steadier and slower to follow the pad. Shorter follows faster and shows more of those steps. The [Response Shaping](#response-shaping) deadzone and smoothing apply on top, and they reach the virtual controller only with [Motion Passthrough](#motion-passthrough) set to **Tuned**.

In the Upright and Wii Wheel holds the yaw lane reads a pad axis that points up. The DualShock 3 has no gyro on that axis and the simulation can't see rotation about it, so turning the pad left and right in those holds reads nothing. The pad's own gyro axis faces you there and reads as roll. Use Pointing or Sideways, Face Up for yaw aim.

---

## Motion Passthrough

**Motion Passthrough** is the first row of the Gyro card, a choice of **Raw** or **Tuned**, and a line under it says what the game reads.

**Raw** is the default. The virtual controller hands the game a clean, calibrated sensor reading: bias is subtracted, no other filtering. Same goes for the DSU motion server, so a Cemu / Dolphin / Yuzu / Ryujinx client sees the raw sensor too. Raw is the right default when the game has its own gyro tuning. The line reads "The game reads the calibrated motion and applies its own sensitivity and smoothing."

Pick **Tuned** to route the rest of the Gyro card's tuning through the motion the virtual controller reports: deadzone, horizontal and vertical sensitivity, smoothing, response curve, invert, and the reference-frame projection. The [engage gates](#engage-gates) come with it, so a slot with Easy Aim or an Aim Engage button set reports zero motion while the gate is closed. Useful when you want PadForge's curve and smoothing to land in a game that exposes only raw motion.

The virtual controllers that carry motion to the game are PlayStation slots (DualShock 4, DualSense), Nintendo slots on the two Switch Pro presets and the Switch 2 Pro: Full preset, and Extended slots on the Valve profiles that send native frames, every one but the plain Steam Deck Controller. The plain DualShock 3 (SIXAXIS), plain Switch 2 Pro and plain Steam Deck Controller presets carry no motion in their reports, and DualShock 3 (SIXAXIS): Full carries the accelerometer and the yaw gyro only. Xbox, MIDI, Keyboard + Mouse, and every other Extended profile have no motion channel. DSU still broadcasts the motion rows of slots 1 to 4 while DSU is on, whatever the preset.

On those slots a controller with no motion sensor can send motion too. The [Motion Pitch, Yaw and Roll](../features/mappings.md#motion-pitch-yaw-and-roll) rows in the Mappings section turn a stick, a trigger, or buttons into the virtual controller's motion.

Calibration drift correction always applies, Raw or Tuned. The choice only gates the discretionary tuning. The Grip rotation applies either way too, because a hold is a fact about the frame, not a tuning choice. On a DualShock 3, [Pitch and Roll Simulation](#pitch-and-roll-simulation) applies either way as well.

**Leave it on Raw for emulators.** An emulator integrates the gyro rate into an orientation. On Tuned, the card's default tuning runs the tightening and smoothing thresholds and then a 3°/s deadzone on that rate before it leaves. A rate that reads zero below 3°/s and three degrees short above it accumulates orientation error the emulator cannot see, so steering drifts after a tilt and breaks on a sharp turn. The live rates on this card show the calibrated rate, not the tuned passthrough, so the distortion is invisible there as well. If motion in an emulator drifts or turns the wrong way, this row is the first thing to check.

---

## Motion Steering

The **Motion Steering** line tunes the **Motion Lean** input: tilt the whole controller like a wheel to drive a steering axis. **Lean Inner Deadzone** and **Lean Outer Deadzone** (0° to 179°) and **Controller Orientation** (Forward, Left, Right, Backward) live here, and the line ends with the two deadzones.

Motion Lean is a normal input. Pick it from the input dropdown in the mapping table and bind it to a stick axis. Full tuning and starting values are on [Steering](https://padforge.org/docs/guides/steering/).

---

### Gyro Tilt

The **Gyro Tilt** line is an envelope for the **Gyro Tilt X** and **Gyro Tilt Y** inputs: hold a tilt and the stick holds its deflection, the way Steam's Joystick Deflection mode behaves. **Full Tilt Range** sets the angle that reaches full deflection (1° to 90°, default 25°), **Tilt Deadzone** ignores tilt around the resting grip and subtracts before scaling (0° to 89°, default 0), and the **Gyro Recenter** macro action re-zeroes the resting grip. Yaw, turning the controller flat, is invisible to gravity and does not move these inputs.

On controllers with a gyroscope and accelerometer, Gyro Tilt updates its orientation estimate on each input poll. Gyro rotation follows the movement, and accelerometer correction keeps the gravity reference stable. It uses the device's existing gyro calibration. Motion Lean and Gyro Lean keep their smoothed response. Devices with only an accelerometer keep the existing gravity estimate.

![The Gyro Tilt line, open](https://padforge.org/docs/images/pad-gyro-tilt.png)

## Calibration

Zero the at-rest reading so gyro mappings don't drift the mouse or stick while you hold still.

1. Put the controller on a flat surface. Hands off.
2. Click **Calibrate Gyro**.
3. PadForge samples for about 1.5 seconds. The averaged reading becomes the device bias.
4. The bias is subtracted from every raw sample going forward.

The line under the buttons shows the last successful calibration ("Last calibrated: …", or "Never calibrated"). The card's live well shows the current Pitch, Yaw, and Roll rate in degrees per second so you can confirm the rest-state floor, and the **Live Accelerometer** line under it shows the X, Y, and Z reading in g. Both readouts are in the held frame set by [Grip](#grip), the same values the mapping rows and the virtual controller get. With Pitch and Roll Simulation on, the rates show the simulated values for the pad's own pitch and roll, which calibration does not change. In the Upright and Wii Wheel holds they read as pitch and yaw, and the Roll rate is the gyro.

On a combined Joy-Con pair, the left half's gyro keeps its own bias. **Calibrate Gyro** samples both halves in the same pass, and a profile calibrated before 4.1.0 gets an automatic aux-only pass on connect that measures the left sensor without touching the stored primary bias.

**Reset Calibration** clears the bias (both halves on a pair) and the timestamp. The next polling cycle re-runs the auto-calibration.

**A DualShock 3 needs the button.** Its yaw part reads against a nominal center on PadForge's own DualShock 3 path, on DsHidMini before 3.15.0, and on later DsHidMini for a Bluetooth pad it has never seen over USB. Units rest far enough off that center to read up to 157°/s while still. The automatic pass at connect leaves an offset that large alone: a stuck part reads steady too, and a pad just picked up looks the same as one at rest. Put the pad down and press **Calibrate Gyro**, and the yaw takes the offset. Give the pad half a minute after it connects first: one pad on record moved its center by about 12°/s over its first 20 seconds. On those paths a part resting against either end of its range stays refused, since one direction can't register at all. DsHidMini 3.15.0 and later zero the word before PadForge reads it for a pad they have seen over USB, so there a dead part can't be told from an offset. A press made while the automatic pass is running waits for it and then runs.

A calibration now belongs to the pad it measured. PadForge tells DualShock 3s apart by their Bluetooth address, read from the pad on PadForge's own connection and from DsHidMini's device node. A DualShock 3 ignores a calibration taken before its yaw direction changed or taken on another pad, including one a paste or a profile carried over, and measures again: the automatic pass takes a small offset, and a large one waits for the button. A calibration also records what served the pad, and under DsHidMini whether DsHidMini held the pad's own calibration, because PadForge's own connection, DsHidMini before 3.15.0, and DsHidMini 3.15.0 or later with and without that calibration each center the yaw differently. Moving between them, updating DsHidMini, or connecting a Bluetooth pad over USB once, which hands DsHidMini its calibration, means measuring again. A pad whose address PadForge can't read takes only an offset under 8.6°/s, the bound every other controller has. A DualShock 3 shared over [Remote Link](remote-link.md) carries its owner's identity, so the receiving PC calibrates it as the owner would, when the sharing PC runs a version that sends it. On PadForge's own connection every DualShock 3 is one device, and a slot holds one calibration for it, so after swapping pads there, press **Calibrate Gyro** again. Any other controller ignores a carried bias too large for its own gyro.

---

## Sensitivity

Top-level scaling and axis inversion. **Horizontal Sensitivity** and **Vertical Sensitivity** sit on the Gyro card. Units, the Invert switches and Real-World Calibration are under its **More**. Every gyro mapping on this device inherits these values.

### Units

The **Units** dropdown offers **Multiplier** (the default) and **Degrees per Screen Turn**. It is a saved preference only: neither choice changes how the sliders read. Both sensitivity sliders always take a multiplier, and **More** shows each one's Steam-style equivalent at all times, as "≈ N° per turn". 1.0× reads ≈ 360° per turn, 2.5× reads ≈ 144° per turn.

### Horizontal and Vertical

| Slider | Drives |
|---|---|
| Horizontal Sensitivity | Yaw and Roll gyro contribution |
| Vertical Sensitivity | Pitch gyro contribution |

Both range 0.1×–10×. The two axes are independent. Set Horizontal to 2.5× and Vertical to 1.0× for fast turning with conservative tilt aim.

### Invert Pitch (Y), Invert Yaw (X), Invert Roll (X)

Three per-axis flips applied after the reference-frame projection. Since 4.3.0 yaw and roll carry separate switches, so a pad that needs its yaw flipped keeps its roll as it was. Each one also flips the horizontal blend while its axis is the dominant motion. These run independently from any Invert flags on the mapping table.

### Real-World Calibration

In-game degrees per physical degree of controller rotation, 0 to 2. 0 turns the correction off. 1.0 means a 90° wrist rotation produces a 90° camera turn.

Calibrate once per game: turn the pad a full physical rotation, look at how far the in-game camera turned, and adjust until the two match.

---

## Reference frames

The **Reference Frame** line's one row, **Space**, picks how raw gyro motion is mapped to camera input.

| Space | Behavior |
|---|---|
| Local | Raw controller axes. Pitch is whatever the IMU calls pitch. Holding the pad tilted changes which way feels like "horizontal." |
| Player | Horizontal motion is projected onto the real-world vertical (gravity). The same wrist twist always turns the camera, regardless of how you're holding the pad. |
| World | World additionally re-projects vertical motion onto world axes. Useful when the pad pitches around a lot during play. |

Local stays as the default so existing configs feel identical.

In 4.5.3 and earlier, Player and World space turned the camera the opposite way to Local. They now turn it the same way. If you turned on **Invert Yaw (X)** to correct that, turn it off.

---

## Compass

A **Compass** line appears at the foot of the Gyro card, and only on a controller that carries a magnetometer. Anchoring yaw to magnetic north removes the slow horizontal drift gyro aiming accumulates.

- **Anchor Yaw to Compass.** Off by default. Turn it on after calibrating.
- **Calibrate Magnetometer.** Press it, rotate the controller through every orientation in a figure-8 for a few seconds, then press again. The button reads **Finish Calibration** while the sweep runs.

The correction is measured on the body's yaw axis, so it applies only while [Held As](#grip) leaves yaw on the yaw lane (Pointing or Sideways, Face Up). Wii Wheel and Upright feed yaw from another body axis and skip it.

The line has a reset beside its switch and no Reset All.

---

## Response Shaping

The **Response Shaping** line: a deadzone, two smoothing thresholds, a smoothing window, an acceleration term, and an output curve.

### Deadzone

Rotation rate (°/s, 0 to 30) below which gyro output is treated as zero. Rejects at-rest hand tremor. Rates past the threshold pass through with the threshold subtracted, so there's no jump at the boundary.

### Dual-threshold smoothing

Two sliders, 0 to 30°/s each, set the rate window where smoothing kicks in, and a third sets the window's length.

| Slider | Effect |
|---|---|
| Tightening | Below this rate, the signal is replaced by the smoothing buffer's average. Drops hand tremor. Default 3°/s. |
| Smoothing Threshold | Above this rate, raw signal passes through. Fast turns keep their precision. Default 8°/s. |
| Smoothing Window | How long a window the buffer averages over, 10 to 200 ms. Larger = heavier smoothing below the tightening threshold. Default 50 ms. |

Between Tightening and Smoothing Threshold a linear ramp blends raw and averaged signal.

### Acceleration

Rate-dependent gain, 0 to 5. 0 is off (plain linear scaling). Higher values pass slow rates through unchanged while amplifying fast rates. Lets precision aim and fast turns share one sensitivity setting.

Gyro-source mapping rows carry their own **Acceleration** control that composes with this one. See [Per-mapping tuning](#per-mapping-tuning).

### Output Curve

Reshape applied after smoothing, before either Acceleration stage.

| Curve | Feel |
|---|---|
| Linear | Pass-through. 1:1 input to output. |
| Aggressive | x² (slow stays slow, fast amplifies hard) |
| Relaxed | √x (slow amplifies, fast saturates) |
| Wide | x^1.5. Sits between Linear and Aggressive. Milder than Aggressive. |
| Extra Wide | x^2.5. Stronger than Aggressive. |

The stages run in a fixed order: curve first, then this card's Acceleration, then the per-row Acceleration from the mapping table.

---

## Engage gates

The **Engage** line limits when the gyro is active. Gates are optional. Gyro fires only while every configured gate is active.

Three gates ship: stick deflection (**Easy Aim**), a button gate (**Aim Engage Button**), and the buttons of [Gyro On](#gyro-on), set per layer. Whether the engage button engages while held, toggles, or engages while released is set by **Engage Mode** below. Leave every gate at its disabled value for always-on gyro. Macros can also drive the engage state, covered in [Macro control](#macro-control).

### Easy Aim Threshold

Stick deflection (0–100%) below which gyro output is zeroed. Which stick and which direction count is set by the two controls below.

| Value | Behavior |
|---|---|
| 0% | Disabled. Gyro is always on. |
| 1–100% | Gyro fires only while the chosen stick's deflection exceeds this percentage. |

Above the threshold the gyro signal passes through. Below it the gyro output goes to zero, which recenters any virtual stick the gyro is bound to.

The threshold reads the stick's raw deflection, before the stick's own deadzone. So you can set the gyro-engage threshold lower than the stick deadzone. A tiny stick nudge the game itself ignores still arms the gyro.

### Engage Stick / Engage Direction

Two dropdowns under the Easy Aim threshold pick what deflection arms the gyro.

**Engage Stick** picks which stick counts.

| Option | Behavior |
|---|---|
| Right Stick | Right-stick deflection arms the gyro. |
| Left Stick | Left-stick deflection arms the gyro. |
| Either Stick | Either stick past the threshold arms the gyro. |

**Engage Direction** picks which direction of that stick's movement counts.

| Option | Behavior |
|---|---|
| Full | Any direction. |
| Horizontal (X) | Left or right travel only. |
| Vertical (Y) | Up or down travel only. |
| Left (X-) | Leftward travel only. |
| Right (X+) | Rightward travel only. |
| Down (Y-) | Downward travel only. |
| Up (Y+) | Upward travel only. |

Pair a direction with a low threshold to arm the gyro on a small push one way while the game still reads the stick normally.

### Aim Engage Button

Cross-device picker for the button that gates gyro. **Engage Mode** sets how.

- The dropdown lists every input on every device the slot has access to, grouped by physical device.
- The device-name subtitle under the picker shows which device owns the chosen input. Matches the mapping-row pattern in the rest of the app.
- The Record button next to the picker records the next physical input press as the aim engage button.
- Leave the field unset to disable the button gate.

When both this button and the Engage Stick's deflection are configured, both must be active for gyro to fire. Releasing either gate zeroes the gyro output.

### Engage Mode

Sets how the Aim Engage button behaves.

| Mode | Behavior |
|---|---|
| Hold | Gyro fires while the button is held. The default. Leave the button unset for always-on gyro. |
| Toggle | Each press flips gyro on or off. Release does nothing. The state sticks until the next press. |
| Release to Aim | Gyro fires while the button is not held. Holding the button pauses gyro. New in 4.1.0. |

Release to Aim is Steam's inverted engage button. A [Steam Workshop import](https://padforge.org/docs/guides/steam-workshop-import/) whose config sets `gyro_button_invert` arrives in this mode. With no button set, Release to Aim behaves like always-on.

Toggle state resets to off on a profile switch or app restart. It isn't saved between sessions.

### Gyro On

**Gyro On** turns the gyro on and off from buttons, on top of the engage button, and can do it differently on each [shift layer](shift-layers.md). It carries what one engage button cannot: several buttons that turn the gyro on, a set that needs every one of them held, a layer whose gyro turns on differently, and Steam's ratchet, a button that turns the gyro off while held.

| Row | What it does |
|---|---|
| Layer | Shown when the slot has shift layers. Picks the layer whose Gyro On the rows below show and change. It stores nothing and has no reset. |
| Gyro On | When the gyro works on that layer. Base offers **Always On** (the default), **While Held**, **While Not Held** and **Toggle on Each Press**. A shift layer adds **Same as Base**, its default, which follows Base. |
| Gyro On Buttons | Shown for While Held, While Not Held and Toggle on Each Press. A list of buttons, each with a check box. The checked buttons are read on any controller of the slot. |
| Needs | Shown with Gyro On Buttons. **Any Button** (the default) turns the gyro on with one checked button held, **Every Button** only with all of them held. |
| Re-Center When Gyro Turns On | Off by default. Takes the controller's pose as the resting point each time the gyro turns on, the way the [Gyro Recenter](macros.md#gyro-recenter) macro action does. |

The engaged layer's Gyro On applies, else Base's. A layer on Always On, or with no button checked, keeps the gyro on. Each layer keeps its own Toggle state. **While Not Held** is Steam's ratchet: hold the button to lift the gyro, the way you lift a mouse to bring it back to the middle of the pad.

Gyro On is stored with the device. When two devices on a slot set it, the one PadForge stores first applies, the same rule the Aim Engage Button follows. Re-Center When Gyro Turns On applies when any device on the slot has it on.

A [Steam Workshop import](https://padforge.org/docs/guides/steam-workshop-import/) fills these rows: its gyro activation, its ratchet as Base's While Not Held, and its re-center (Steam's deflection mode with a gyro button and Use Relative on). They land on the device's Engage line when the device is assigned to the slot, and a profile saved before this version moves them there when it loads. A device that already has Gyro On set keeps it.

---

## Macro control

Two actions in the [macro editor](macros.md) drive gyro directly.

- **Set Gyro Engaged** sets the slot's engage state, with a Toggle / On / Off mode. It OR-combines with the Aim Engage button at the evaluator, so either source can engage and both must release to disengage. Gyro On applies on top of both. Details on [Set Gyro Engaged](macros.md#set-gyro-engaged).
- **Gyro Recenter** (new in 4.1.0) zeroes the pad's accumulated gyro aim references on press. Smoothing history clears, the Motion Lean neutral re-captures, the gravity estimate re-seeds from the controller's current pose, and the turn the [Motion Pitch, Yaw and Roll](../features/mappings.md#motion-pitch-yaw-and-roll) rows built levels. Details on [Gyro Recenter](macros.md#gyro-recenter).

---

## Output routing

Gyro doesn't have a card-level "send to mouse" or "send to stick" switch. The destination is whatever the mapping table says.

- Bind **Gyro Pitch / Yaw / Roll** to **Mouse X / Mouse Y** to get a gyro mouse.
- Bind **Gyro Pitch / Yaw / Roll** to **Right Stick X / Right Stick Y** (or any virtual stick axis) to get a gyro stick.
- Bind to any Extended axis if you're driving a custom HID profile.

Stick output recenters as soon as the engage gates close, because zeroed gyro output writes zero stick deflection. Mouse output just stops moving.

See [Button and Axis Mappings](../features/mappings.md) for the full source/destination reference.

---

## Rate versus tilt

PadForge covers both of Steam Input's gyro-to-stick modes. The names differ, so here is the translation:

| Steam calls it | PadForge equivalent |
|---|---|
| **Gyro to Joystick (Camera)**, `gyro_to_joystick` | Map **Gyro Pitch / Yaw / Roll** to a stick axis |
| **Gyro to Joystick (Deflection)**, `gyro_to_joystick_deflection` | Map **Gyro Tilt X / Y** (adjustable range) or **Gyro Lean X / Y** (fixed 90° range) to a stick axis |

Steam's separate "Joystick Camera" is a stick-group mode in its schema, not a gyro mode. The configurator shows both families in the same place, which makes them easy to conflate.

**Rate** is the default and what the plain gyro axes give you: instantaneous rotation speed maps to stick deflection, so you stop tilting and the stick recenters while the in-game camera stays where you turned it. This is deliberate and will not change. An integrated tilt-to-stick path shipped once and was removed, because holding the controller tilted kept the camera turning forever, the opposite of how gyro aim is supposed to feel. JoyShockMapper, Steam Input, and Splatoon all treat gyro-to-stick as rate.

**Tilt** holds. Keep the controller tilted and the stick keeps its deflection for as long as you hold the tilt. Three inputs map that angle:

- **Gyro Tilt X / Gyro Tilt Y** reach full deflection at the range set on this card's **Gyro Tilt** line (default 25°), with a tilt deadzone that subtracts before scaling. This is the closest match to Steam's Deflection mode.
- **Gyro Lean X / Gyro Lean Y** are the fixed-envelope originals: 90° of tilt is full scale, and the per-source Sensitivity dial on the mapping row scales the read.
- **Motion Lean** is the steering-oriented tilt input with its own inner/outer deadzones and grip orientation on the [Motion Steering](#motion-steering) line.

All three capture your resting grip as the neutral when the controller connects, and the **Gyro Recenter** [macro action](macros.md) re-zeroes it mid-session. A change of [Held As](#grip) re-zeroes it as well. Gyro Tilt also uses the saved gyro bias. See [Calibration](#calibration) for the existing Calibrate Gyro command.

**Held yaw is not possible.** Gravity points down: it moves when you pitch or roll the controller, and it does not move at all when you turn the controller flat around the vertical axis. So the tilt inputs hold pitch and roll, never yaw. Holding a yaw rotation would need the gyro's rate integrated into an angle, which drifts without something to correct it, and no shipped mode does that today. To turn a camera with gyro, use rate mode.

The [Motion Pitch, Yaw and Roll](../features/mappings.md#motion-pitch-yaw-and-roll) rows run the other way, from a stick or buttons to the virtual controller's motion, with the same two choices: **Speed** is rate and **Angle** is tilt. Yaw is Speed only there, for the same reason.

---

## Left Joy-Con aux gyro

On a combined Joy-Con pair, the plain Gyro Pitch / Yaw / Roll sources read both halves averaged, each debiased with its own bias. The right half alone is reachable as **Right Joy-Con Gyro Pitch** / **Right Joy-Con Gyro Yaw** / **Right Joy-Con Gyro Roll**. The left half is a second physical sensor, and since 4.1.0 the mapping table's input dropdown exposes it as its own sources.

| Source | Reads |
|---|---|
| Left Joy-Con Gyro Pitch / Yaw / Roll | The left half's rotation rate, one axis per row. |
| Left Joy-Con Motion Gyro | Bundled passthrough. Streams the left half's full rate vector to the virtual controller's motion channel and the DSU server instead of the right half's. |
| Left Joy-Con Lean | Tilt steering from the left half's accelerometer. |
| Left Joy-Con Accelerometer | Bundled accelerometer passthrough from the left half. |

The aux sources appear only when the paired device reports the second sensor. They run through the same pipeline as the primary sources: this card's sensitivity, response shaping, and engage gates all apply. Player and World space project against the left half's own gravity, and [Calibration](#calibration) keeps a separate bias for the left sensor. The [Grip](#grip) rotation does not reach these rows. The left half is its own body and keeps its own frame.

On a Wii Remote with a Nunchuk, the lean and accelerometer rows carry Nunchuk names instead. The Nunchuk has no gyro, so the three rate sources stay Joy-Con only.

---

## Pairing with Flick Stick

Flick stick turns a thumbstick into a compass for mouse-driven camera control. It turns horizontally only, so gyro is its natural partner for vertical aim.

- On a Keyboard + Mouse slot, map **Flick Stick (Right Stick)** to Mouse X, and bind **Gyro Pitch** to Mouse Y for the vertical.
- The **Flick Stick** line in the Sticks & Triggers section tunes it. 4.1.0 adds **Rotation Offset**, which turns the whole flick map by up to ±180°. Positive is clockwise, and snapping applies after the offset.
- Full settings and starting values are on [Flick Stick](../features/stick-deadzones.md#flick-stick).

---

## Per-mapping tuning

Every mapping row whose source is a Gyro axis carries its own **Sensitivity** dial (0.1×–10×, shown only on gyro-source rows). 1.0 is the engine's default 500°/s → ±1 deflection scale. The per-row multiplier composes with the device-level Horizontal and Vertical sensitivities on this card.

Use the per-row dial when one mapping needs to feel different from the rest. A camera bind at 1.0× alongside a steering bind at 0.3× lets you keep the camera fast while taming the wheel.

Since 4.1.0, rows with a continuous source also carry an **Acceleration** control (0–5). Fast motion on that row's input is amplified, slow motion passes through unchanged, and 0 keeps the response flat. On a gyro row it applies after this card's Acceleration, so the two compose. Steam Workshop imports carry Steam's mouse acceleration here on stick-hosted rows.

---

## Gyro in an imported profile

A [Steam Workshop import](https://padforge.org/docs/guides/steam-workshop-import/) carries Steam's gyro scales and ratios onto the mapping rows, and its gyro activation onto [Gyro On](#gyro-on). An older gyro mode whose axis is yaw plus roll joins the two the way Steam does: the signed length of the two rates when their signs agree or the yaw rate is larger, otherwise the roll rate's sign with the difference of their squares. The join rides the row's Custom combine, so the row no longer sums yaw and roll, which ran up to 1.41 times Steam's speed on a diagonal turn.

An [import from DS4Windows](profile-import.md) folds the profile's gyro dead zone onto this card's **Deadzone**, and converts the gyro-as-stick maximum and anti-dead zones at DS4Windows' 16 counts per degree per second against the 500°/s full scale. How far past the dead zone DS4Windows rescales the rate is not measured against PadForge's response, so the result near full deflection can differ.

The Gyro Aim [starter profile](https://padforge.org/docs/guides/starter-profiles/#gyro-aim-engages-on-the-left-trigger) inverts the pitch on the right stick's Y row, so nose up looks up.

---

## Per pad, per slot persistence

Calibration, sensitivity, smoothing, curve, and engage values save per pad per slot. Assigning the same pad to two slots gives you two independent tunings. Removing the pad from a slot drops its values there, and assigning it again starts from the defaults.

The values save automatically to your profile whenever you change anything on this card.

---

## Reset buttons

Every row has a reset button (circular arrow icon). The Gyro card's header and the Engage, Response Shaping, Motion Steering, Gyro Tilt and Pitch and Roll Simulation lines carry a **Reset All** that snaps every value in it back to defaults. Grip, Reference Frame and Compass have one row each and only that row's reset.

| Button | Resets |
|---|---|
| The Gyro card's Reset All | Motion Passthrough, Horizontal and Vertical Sensitivity, and under More, Units, all three Invert switches and Real-World Calibration. Not the calibration, and not the lines below |
| Reset Calibration | Auto-cal bias + timestamp |
| Reset Grip | Held As back to Pointing |
| Reset Space | Space back to Local |
| Engage's Reset All | Easy Aim Threshold, Engage Stick, Engage Direction, Aim Engage Button, Engage Mode, Gyro On on every layer, Re-Center When Gyro Turns On |
| Reset Gyro On | The shown layer's Gyro On: Base back to Always On, a shift layer back to Same as Base |
| Response Shaping's Reset All | Deadzone, Tightening, Smoothing Threshold, Smoothing Window, Acceleration, Output Curve |
| Motion Steering's Reset All | Both lean deadzones and the orientation |
| Gyro Tilt's Reset All | Full Tilt Range, Tilt Deadzone |
| Pitch and Roll Simulation's Reset All | Simulate Pitch and Roll, Simulation Smoothing (DualShock 3) |

---

## Suggested starting points

Starting values only. Tune against the live rate readout and in-game feel.

### First-person shooter, gyro-mouse aim

| Setting | Value |
|---|---|
| Space | Player |
| Horizontal Sensitivity | 1.0× (the hint reads ≈ 360°/turn) |
| Vertical Sensitivity | 1.0× |
| Deadzone | 3°/s |
| Tightening | 3°/s |
| Smoothing Threshold | 8°/s |
| Smoothing Window | 50 ms |
| Acceleration | 0 |
| Output Curve | Linear |
| Easy Aim Threshold | 0% (always on) or 15% if stick aim shares the right stick |
| Aim Engage Button | L2 or unset |

### Console-style flick aim (gyro to right stick)

| Setting | Value |
|---|---|
| Space | Player |
| Horizontal Sensitivity | 2.5× |
| Vertical Sensitivity | 1.5× |
| Deadzone | 5°/s |
| Tightening | 5°/s |
| Smoothing Threshold | 12°/s |
| Acceleration | 0.5 |
| Output Curve | Wide |
| Easy Aim Threshold | 10% |
| Aim Engage Button | Unset |

### Racing / flight stick tilt steering

| Setting | Value |
|---|---|
| Space | Local |
| Horizontal Sensitivity | 1.0× |
| Vertical Sensitivity | 0.5× |
| Deadzone | 8°/s |
| Tightening | 6°/s |
| Smoothing Threshold | 15°/s |
| Acceleration | 0 |
| Output Curve | Relaxed |
| Easy Aim Threshold | 0% |
| Aim Engage Button | Unset |

---

## Related pages

- [Button and Axis Mappings](../features/mappings.md): bind Gyro Pitch / Yaw / Roll to virtual destinations and set per-row multipliers.
- [Flick Stick](../features/stick-deadzones.md#flick-stick): stick-driven horizontal flick turns that pair with gyro for vertical aim.
- [Stick Deadzones](../features/stick-deadzones.md): sets the stick's in-game deadzone. Easy Aim reads raw deflection before that deadzone, so the gyro-engage threshold can sit lower.
- [Macros](macros.md): the Set Gyro Engaged and Gyro Recenter actions.
- [DSU Motion Server](https://padforge.org/docs/reference/dsu-motion-server/): broadcast the calibrated gyro and accelerometer feed to Cemu, Dolphin, Yuzu, and Ryujinx over UDP.
- [Steering](https://padforge.org/docs/guides/steering/): tune the Motion Lean tilt-steering input whose line sits on this card.
- [Shift Layers](shift-layers.md): the layers Gyro On can set apart.
- [Wii Controllers](https://padforge.org/docs/devices/wii-controllers/): the Wii Remote's holds and the emulator setup that goes with them.

---

*Last updated for PadForge 6.0.0.*
