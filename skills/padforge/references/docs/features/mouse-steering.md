<!-- https://padforge.org/docs/features/mouse-steering/ as PadForge's skill carries it, from padforge.org's wiki/features/mouse-steering.md. -->
# Mouse Steering

*Steer with a mouse, its scroll wheel, a trackball or a spinner: how fast it moves, where the pointer is, or how far it has traveled, each with the same steer gate.*

A mouse can drive a stick or a wheel axis in several ways, and each way is its own source in a mapping row's **Source** picker. Pick the one that fits the game, set it on the source's own line, and the row's **Value** meter shows the result.

<!-- SCREENSHOT: pad-mouse-steering (a mapping row with Mouse Travel X and its settings line, Centering on Spring) -->

---

## The mouse families

Open the **Source** picker on a mapping row of a controller that has a mouse assigned. The mouse group lists each family with a line under its name, in the open list, that says what it does:

| Source | What it does | Preview |
|---|---|---|
| **Mouse Speed X**, **Mouse Speed Y** | How fast the mouse moves. Lets go when the hand stops. | Yes |
| **Mouse Position X**, **Mouse Position Y** | Where the pointer is on its monitor, from its center or from where it was when steering began. | Yes |
| **Mouse Travel X**, **Mouse Travel Y** | How far the mouse has moved, held where the hand leaves it, as a wheel is. | Yes |
| **Wheel Travel** | Scroll notches held as a position, at full precision on a fine-scrolling wheel. | Yes |
| **Wheel Travel Sideways** | The sideways wheel's notches held as a position, at full precision on a fine-scrolling wheel. | Yes |
| **Mouse Scroll** | A push for each notch, back to rest at once. | No |

The wheels are read per mouse in Windows' own units, 120 to a notch, so a fine-scrolling wheel's smaller steps count in full. Wheel Travel is positive up, the sideways wheel positive to the right.

### Preview

Click **Preview** on a family in the open list. A gauge takes its place, **Previewing. Move your mouse.**, and follows your own mouse wherever the pointer goes, with a caption such as **Held at 40% right** or **At the center**. It runs PadForge's own steering on the row's settings, or the defaults when the row does not read that family yet. Nothing is mapped until you pick a family. Picking one, pressing Escape, clicking away or previewing another family ends it. With a **Steer Input** that is not held, the caption reads **Moves while … is held.**

The row's **Value** meter stays the one live view of the mapped axis.

---

## The settings on the source's line

Every setting has its own reset button.

### Travel: Mouse Travel, Wheel Travel and a relative axis

| Setting | Range | Default | What it does |
|---|---|---|---|
| **Travel to Full Lock** | 100 to 50,000 counts | 2,000 | How far the mouse or the wheel moves from center to full lock, in the counts it reports. A higher number steers finer and takes more hand travel. |
| **Centering** | Off, Spring, From Telemetry, From the Game's Force | Off | What brings the wheel back. |
| **Return Time** | 0 to 5 seconds | 0.75 | How long Spring takes to bring the wheel 95% of the way back. 0 centers it at once. Shows under Spring and From Telemetry. |
| **Steer** | Always, While Held and Center, While Held and Stay, Toggle | Always | Whether an input gates the steering. |
| **Steer Input** | a button on the same device | none | The input Steer reads. Shows when Steer is not Always. |
| **Smoothing** | 0 to 100 ms | 0 | Evens out a mouse that reports unevenly. The stick trails the hand by about this long. 0 is off. |
| **Lock at Speed** | 20 to 100% | 100 | How much of the lock is left at 200 km/h. Shows while [Game Telemetry](game-telemetry.md) is on. |

**Centering**:

- **Off** holds the wheel where the hand left it.
- **Spring** brings it back over Return Time.
- **From Telemetry** pulls it back as a car's steering does at the game's speed and grip: steeply near center and softer further out, still at a standstill, firmer with speed, light when the tires slide. With no game sending telemetry it pulls as at 100 km/h on a dry road. It is worked out in steps of a millisecond or less, so the wheel comes back the same at 60 Hz and at 1,000 Hz.
- **From the Game’s Force** lets a virtual wheel's own force feedback turn it.

### Steer

**Steer** works on Mouse Speed, Mouse Position and every travel source, so one mouse can steer and look around:

| Steer | What happens |
|---|---|
| **Always** | The mouse steers at all times. |
| **While Held and Center** | It steers only while the Steer Input is held, then comes back to center, by Centering where the source has one and at once where it has none. |
| **While Held and Stay** | It steers only while the input is held, and stays where it was let go. |
| **Toggle** | Each press of the Steer Input turns steering on or off. |

A row saved with the older **While Held** keeps what it did: on a travel source with Centering Off it reads as While Held and Stay, and otherwise as While Held and Center.

### Mouse Speed

| Setting | Range | Default | What it does |
|---|---|---|---|
| **Sensitivity** | 0.1 to 10× | 1.00 | How much of the stick a speed gives. At 1.00 the stick reaches full deflection at 16,000 counts a second, at every polling rate and mouse report rate. |
| **Back to Center** | 0 to 2 seconds | 0 | Once the mouse slows or stops, the stick comes back no faster than this. 0 lets it come back with the mouse. |

Mouse Speed also takes Smoothing, Steer and Steer Input. Its scale no longer changes with the polling rate or the mouse's report rate, and a mapping made before reads the same at 1,000 Hz, PadForge's default.

### Mouse Position

| Setting | Range | Default | What it does |
|---|---|---|---|
| **Sensitivity** | 0.1 to 5× | 1.0 | 1.0 reaches full deflection a tenth of the monitor's width from its center. |
| **Start Where Steering Began** | on or off | off | With a Steer Input, measures from where the pointer was when steering began, as a stick on a phone's screen starts where the finger lands. Shows when Steer is not Always. |

Position is measured on the monitor the pointer is on. Mouse Position also takes Smoothing, Steer and Steer Input.

---

## A trackball or a spinner

SDL reads a generic controller through DirectInput, which reads every axis as a position, so a trackball or spinner that shows up as a joystick reads as a plain axis. On the source's line of that axis, turn on **This axis moves relatively** to tell PadForge the axis moves this way. The axis then holds a position as Mouse Travel does and takes the travel settings above. The switch shows on an axis or slider of any device that is not a mouse or a keyboard.

## Centimeters

Set **Mouse DPI** in the mouse's **Settings** card on the Devices page: the counts it reports for each inch it moves, as its maker's software sets it. 0 means not set. With the DPI set, **Travel to Full Lock** for Mouse Travel X and Y also reads in centimeters, such as **counts, 3.2 cm** for 2,000 counts at 1,600 DPI.

---

## Assistants and scripts

The travel, centering, steer and sensitivity settings of a row's mouse source are catalog entries. An [assistant](assistants.md) at Change Settings reads and sets them as you do on the row, and only the ones the row shows.

See also: [Steering with any stick](https://padforge.org/docs/guides/steering/), [Wheels](https://padforge.org/docs/features/wheel/), [Wheel Centering](https://padforge.org/docs/features/wheel-centering/).
