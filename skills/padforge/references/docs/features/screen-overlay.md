<!-- https://padforge.org/docs/features/screen-overlay/ as PadForge's skill carries it, from padforge.org's wiki/features/screen-overlay.md. -->
# Screen Overlay

*A window above your game that shows a picture, a timer, the time, a lightbar's color or the devices that are not connected, with no keyboard screen needed.*

A Screen Overlay shows anything a [keyboard screen](https://padforge.org/docs/features/keyboard-screens/) can show, in a borderless window on the PC's own display. It needs no keyboard screen, opens no device and runs with **Drive Keyboard Screens** off. Up to eight overlays run at once, each with its own window and its own choice of what to show.

<!-- SCREENSHOT: dashboard-screen-overlay -->
![The Screen Overlay page under Extras with one overlay card: Overlay 1 and its status, Reset All, the Enable Screen Overlay switch, the Shows row with Choose What It Shows…, the Opacity slider at 80 percent and Reset Position, and Add Overlay at the top](https://padforge.org/docs/images/dashboard-screen-overlay.png)

---

## The page

Open **Extras** in the sidebar and click the **Screen Overlay** tile, in the **On Screen** group. The page starts with one overlay, off. The overlay windows show while the input engine runs.

The tile's own switch is on while any overlay is on. Turned off, it turns every overlay off and remembers which were on. Turned on again, it brings those back, or the first overlay when it remembers none.

Each overlay has a card. Its head names it, **Overlay 1**, **Overlay 2** and so on, with its status beside the name: **Running** or **Stopped**. A running overlay with nothing to show says why, as **Running, no picture chosen** or **Running, no lightbar color**. **Reset All** puts the overlay's switch, what it shows, its opacity and its place back to their defaults.

| Control | What it does |
|---|---|
| **Enable Screen Overlay** | Shows the overlay's window. Off by default. |
| **Shows** | What the overlay shows now, and **Choose What It Shows…**, which opens the overlay's dialog, below. |
| **Opacity** | How opaque the overlay's content is, 0 to 100%, 80% by default. The outline and grip that show while you arrange it keep theirs. |
| **Place on Screen** | **Reset Position** centers an overlay that is up on its monitor. An overlay that is off opens centered on the monitor of the window in front the next time it shows. |
| **Remove** | In the card's head. Removes this overlay. Shown only while the page holds more than one. |
| **Add Overlay** | At the top of the page. Adds an overlay, up to eight. |

A new overlay takes the lowest free number, and an overlay keeps its number when another is removed, so a macro that names overlay 3 always reaches the same window. Macros and the overlay's dialog call it by its full name, **Screen Overlay 3**.

---

## What an overlay shows

**Choose What It Shows…** opens a dialog with a **Show** box and a preview in the shape of the overlay's window.

| Show | The overlay shows |
|---|---|
| **Off** | Nothing. |
| **Profile Name** | The active profile's name, following each profile switch. |
| **Profile Number** | The active profile's place in the profile list. The Default profile is 0. |
| **Profile Picture** | The active profile's own picture, or its name when it has none. Each profile keeps its own picture. The dialog's **Choose Picture for** button, which names the active profile, sets it, and **Remove Profile Picture** clears it. |
| **Clock** | The time of day. |
| **Play Time** | Hours and minutes since the active profile switched in. A profile tied to a game's program switches in when the game starts, so the count is that session. Under the Default profile it counts from the engine's start. |
| **Chosen Picture** | A picture you choose with **Choose Picture…**, such as a PNG, JPEG, BMP or GIF. |
| **Stopwatch** | The stopwatch the Stopwatch macro starts, pauses and resets, the same one every keyboard screen shows. |
| **Lightbar Color** | The color of the lightbar on the DualShock 4 or DualSense in the slot chosen in the **Slot** box, or with **Lowest Slot with a Color**, the lowest slot that has one. This is the default. |
| **Offline Devices** | The mapped devices that are not connected, by the names the Devices page shows, or **All connected** when none is missing. See [Connection Notes](connection-notes.md#offline-devices-on-a-screen). |

Text is white on a black field, as large as the window holds, on as many lines as the words need. A picture scales up or down until it touches the window's edges, with its proportions and its transparency kept, so the parts of a PNG with no color show what is underneath. A lightbar color fills the window. Until a color has reached a lightbar, a **Lightbar Color** overlay shows nothing.

A **Chosen Picture** has a **Zoom** box, 100% by default and up to 2000%. Above 100% the window shows part of the picture, for a map, and **Pan X** and **Pan Y** set which part, in the picture's pixels. The Pan Keyboard Screen Picture macro moves it too, and so does dragging with the left mouse button while PadForge is in front. The pan stops at the picture's edges.

What each overlay shows is one setting per overlay number. Profiles do not change it, apart from each profile's own **Profile Picture**.

---

## Moving and sizing

While PadForge is the window in front, every overlay that is up shows an outline and a grip in its bottom-right corner, and takes the mouse:

- Drag with the right mouse button, or one finger on a touch screen, to move it.
- Drag the grip to size it. An overlay opens at 320 by 240 and goes no smaller than 80 by 60.
- An overlay with nothing to show says so in its middle: *No picture chosen.*, *No DualShock 4 or DualSense lightbar color to show.* or *Nothing to show yet. Choose what it shows on the Screen Overlay page, under Extras.*

While any other program is in front, the outline and grip go away and clicks pass through the overlay to the program beneath it. The overlay never takes the focus from a game and has no taskbar button. Once a second it goes back above a game that made its own window topmost after the overlay came up. PadForge's own radial menu and flyouts stay above it.

The overlay remembers the monitor you leave it on. A new overlay opens centered on the monitor of the window in front, each number a little further down and across than the one before. An overlay whose saved place no monitor shows, such as one on a display that was unplugged, comes back centered on its monitor. The window keeps its left-to-right layout in every language, so the grip stays in the bottom-right corner.

---

## Macros

A macro on any slot reaches the overlays, whether or not their windows are up, and needs neither **Drive Keyboard Screens** nor a Devices row. Each overlay action has an **Overlay** box: **Every Overlay**, or **Screen Overlay 1** to **Screen Overlay 8**. **Every Overlay** means each overlay on the page.

| Action | On an overlay |
|---|---|
| **Toggle Screen Overlay** | Turns the named overlay on or off. With **Every Overlay**, any that is on turns them all off, and none on turns them all on. The window comes up at once, so a macro that turns an overlay on and then shows a picture has the picture up in the same press. Listed under **Layers & Overlays**. |
| **Show Keyboard Screen** | With **Screen** set to **Screen Overlay**: up to eight pictures, a frame time of at least a tenth of a second and a repeat count. Each picture keeps its own shape, up to 800 pixels on its longer side. The pictures play over the overlay's mode, which returns when they end. A repeat count of 0 keeps them until the next Show Keyboard Screen or Keyboard Screen Mode macro. |
| **Keyboard Screen Mode** | With **Screen** set to **Screen Overlay**: switches what the overlay shows and keeps it as its setting. It also ends pictures held by a repeat count of 0. |
| **Pan Keyboard Screen Picture** | Moves a zoomed **Chosen Picture** left, right, up or down by its step, 16 pixels by default. A macro that names one overlay moves that overlay alone. **Every Overlay** also moves the keyboard screens that feed the macro's slot. |
| **Stopwatch** | Starts, pauses or resets the one stopwatch every overlay and keyboard screen shows. |

A show keeps its clock while its window is down, so a window that comes up shows the picture the show has reached. A pan or a mode change made while the window is down is there when it comes back. See [Macros](../guides/macros.md).

---

## Profiles

Each [profile](../guides/profiles.md) keeps the page's overlays: which exist, each one's switch, opacity, monitor, place and size. A profile switch puts up the overlays that profile has on and takes down the rest. A profile saved before 5.3.0 has one overlay, off.

---

## Imported profiles

The [import](../guides/profile-import.md) turns a reWASD button that toggles one of its ten crosshairs into **Toggle Crosshair** for PadForge's crosshair of that number, and reWASD's command for every crosshair into **Toggle Crosshair** for **Every Crosshair**. Those land on the [Crosshair](crosshair.md) page, not on a Screen Overlay.

See the technical page, [Keyboard Screens Internals](https://padforge.org/docs/reference/keyboard-screens-internals/#the-screen-overlay).

---

*Last updated for PadForge 6.0.0.*
