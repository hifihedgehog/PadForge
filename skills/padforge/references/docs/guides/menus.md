<!-- https://padforge.org/docs/guides/menus/ as PadForge's skill carries it, from padforge.org's wiki/guides/menus.md. -->
# Menus

*Turn a stick, a touchpad, the D-pad, the face buttons or any two recorded axes into an on-screen ring, grid or hotbar of commands, or open the On-Screen Keyboard.*

<!-- SCREENSHOT: pad-menus -->
![The Macros & Menus section listing three menus, Combat Wheel, Text Chat and Ability Bar, with Combat Wheel selected and its cells in the editor](https://padforge.org/docs/images/pad-menus.png)

A menu puts a set of commands on an input surface. Engage the surface and a ring or grid appears on screen. Point at a cell and it highlights. Depending on the fire mode, the cell's binding fires on click, on release, or the whole time it is hovered. It is the same idea as Steam Input's radial and touch menus, and configs imported from the [Steam Workshop](https://padforge.org/docs/guides/steam-workshop-import/) bring theirs along.

Each slot has its own menu list. Open a controller, go to its **Macros & Menus** section, and click the **+** beside **Menus** (**New Menu**). The selected menu's **More** button holds **Duplicate**, **Remove Last Cell** and **Remove**, and its **Enabled** switch turns it off without deleting it.

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-shift-layers-and-menus-poster.jpg"
       aria-label="Video: a shift layer and a radial menu, recorded live in PadForge 5.2.1">
<source src="https://padforge.org/assets/video/padforge-tutorial-shift-layers-and-menus.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-shift-layers-and-menus.vtt" default>
</video>
<figcaption>A Pit Stop shift layer held on the left shoulder, then a four-cell radial menu on the right stick whose first cell, Map, presses Back, recorded live in PadForge 5.2.1 in 1 minute 18 seconds. PadForge 6 has since rebuilt the interface, so the screens differ from the steps on this page. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-shift-layers-and-menus-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

---

## Style

| Style | How cells are picked |
|---|---|
| **Radial Ring** | By direction. The ring splits into equal wedges, straight up is the first cell, and the wedges run clockwise. Works best on a stick. A radial menu can also carry a **Center Cell**, selected while the stick rests inside the engage deadzone. |
| **Touch Grid** | By position. Cells lay out on a near-square grid, wider than tall (4 cells make 2×2, 12 make 4×3), and your touch point picks the cell under it. Works best on a touchpad. |
| **Keyboard** | No cells. It types the keys of the Windows layout in use. See [On-Screen Keyboard](https://padforge.org/docs/features/on-screen-keyboard/). |
| **Hotbar** | By stepping. A row of cells, shown, stepped along and fired by four controls of its own. See [Hotbar](#hotbar). |

---

## Opens With

The **Opens With** picker sets the input that opens and steers the menu. It reads the physical controllers assigned to the slot, never the virtual controller's layout. The cell bindings below are what the menu outputs. A Record button next to the picker captures the host from a live input, and a reset restores the default (**Right Stick**).

| Host | How it engages |
|---|---|
| **Left Stick** / **Right Stick** | When the stick leaves the **Engage Deadzone** (default 25). |
| **D-Pad** / **Face Buttons** | On a press of one of the four directions. The face buttons count by position: Y up, A down, X left, B right. On a **Radial Ring** the press points at the cell in that direction, two held together point between, and the press itself is the click unless a **Click Input** is recorded. On a **Touch Grid** the presses step through the cells instead, Right or Down forward and Left or Up back, stopping at either end, and each newly selected cell fires once, without **Fire Mode** or **Click Input**. |
| **Touchpad 1** and **Touchpad 2** | On touch. A **Pad Half** picker (**Whole Pad**, **Left Half**, **Right Half**) narrows the menu to half the pad, so one pad can carry two menus, the way Steam treats the PS touchpad as two. |
| **Custom Axes** | Record any two axes as **Steer X** and **Steer Y**, then push past the deadzone like a stick. This is how joysticks, wheels, and other non-gamepad devices host menus. |

### Click Input

**Click Input** sets the button the **On Click** and **On Click Release** fire modes read, recordable from any assigned device. Left empty it shows **(host default)**: the stick click for a stick host, the trackpad click for a touchpad. A **Custom Axes** host has no default click, so record one before using those two fire modes.

---

## Fire Mode

| Mode | When a cell fires |
|---|---|
| **On Click** (default) | The hovered cell's binding holds the whole time the host surface is clicked. |
| **On Click Release** | The hovered cell fires once when the click releases. |
| **On Touch Release** | The last hovered cell fires once when you let go: the finger lifts, or the stick returns inside the deadzone. Letting go with nothing hovered dismisses the menu without firing. |
| **While Hovered** | The hovered cell is active the entire time it is hovered. No click needed. |

---

## Hotbar

The **Hotbar** style is Steam Input's hotbar: a single row of cells, run by four controls.

<!-- SCREENSHOT: pad-menus-hotbar -->
![The Macros & Menus section with the Ability Bar menu selected, its Style set to Hotbar](https://padforge.org/docs/images/pad-menus-hotbar.png)

| Control | What it does |
|---|---|
| **Show or Hide** | Shows the bar, and hides it on the next press. A bar hidden this way opens on its middle cell next time. |
| **Previous** | Selects the cell to the left, skipping cells with nothing set. |
| **Next** | Selects the cell to the right, skipping cells with nothing set. |
| **Fire** | Fires the selected cell, as **Fire Mode** says. |

**Previous** and **Next** act only while the bar is shown. When both are pressed at the same moment, **Previous** wins. The bar opens on its middle cell: Cell 3 of a bar of six, Cell 2 of five.

Each control takes any button on the slot's devices, picked from its list or recorded. A control left empty shows **(host default)** and takes the **Opens With** direction: down for **Show or Hide**, left for **Previous**, right for **Next**, up for **Fire**.

| Opens With | The four directions |
|---|---|
| **D-Pad** | D-pad down, left, right and up. |
| **Face Buttons** | A, X, B and Y, by position. |
| **Left Stick** / **Right Stick** | The stick pushed into the 90-degree span around each direction, past the **Engage Deadzone**. |
| **Touchpad 1** / **Touchpad 2** | The finger in the same spans, past the **Engage Deadzone**, only while the pad is clicked. **Pad Half** narrows it to one half. |
| **Custom Axes** | None. Empty controls show **(not set)** and read nothing, so record all four. |

Switching a menu to **Hotbar** sets its **Engage Deadzone** to 61, Steam's hotbar dead zone, when it still holds the ring default of 25. A stick or touchpad direction engages 1.25 points past the deadzone and lets go 1.25 points below it.

| Switch | Default | When on |
|---|---|---|
| **Wrap Around** | Off | Stepping past the last cell goes to the first, and back. Off, the selection stops at either end. |
| **Hide After Firing** | On | **Fire** hides the bar. Off, the bar stays up and the same cell can fire again. |
| **Re-Center Each Time** | On | While the bar is hidden, the selection goes back to the middle cell. Off, a bar that **Fire** hid opens on the cell it fired. |

**Fire Mode** runs the way Steam's hotbar runs it. A short tap holds the cell for 1/30 of a second.

| Mode | When a cell fires |
|---|---|
| **On Click** (default) | **Fire** presses the selected cell and holds it while **Fire** is held. With **Hide After Firing** on, the press is a short tap. |
| **On Click Release** | The selected cell fires once, as a short tap, when **Fire** is released. With **Hide After Firing** on, it fires as soon as **Fire** hides the bar. |
| **On Touch Release** | A cell fires once, as a short tap, when **Fire** hides the bar with **Hide After Firing** on. With **Re-Center Each Time** on, the middle cell is the one that fires. Hiding the bar with **Show or Hide** fires nothing. |
| **While Hovered** | The selected cell is held the whole time the bar is shown. Stepping lets go of one cell and holds the next, 1/30 of a second later. |

A hotbar has no **Click Input** or **Layer Holds the Menu Open** row. Its **Layer** still applies: while the layer is closed the bar is hidden and fires nothing, and it starts over hidden when the layer opens again. The overlay draws the bar as one row at the menu's **Screen Position**, with the selected cell highlighted. A hotbar from a [Steam Workshop](https://padforge.org/docs/guides/steam-workshop-import/) config imports as this style, with its cells, its three switches and its place near the bottom of the screen.

---

## Cells

**Add Cell** under the cell list adds a cell, **Remove Last Cell** in the menu's **More** menu takes the last one off, and the reset beside **Add Cell** puts the count back. A menu carries 1 to 20 cells. For a radial menu that is the ring count, with the optional **Center Cell** on top. Each cell has:

- A **Label**, rendered on the overlay.
- A binding: **None**, a **Keyboard Key**, a **Controller Button**, or a **Macro**.
- An optional icon, set with the icon button on the row. See [Cell icons](#cell-icons).

**Controller Button** speaks the slot's own lettering: Xbox letters on an Xbox slot, DualShock symbols on a PlayStation slot, Nintendo letters on a Nintendo slot, and the custom layout's numbered buttons on an Extended slot. Keyboard + Mouse, MIDI, and VR slots have no controller buttons to press, so the choice is not offered there, and a button binding left behind by a slot-type switch shows as **Controller Button (not on this slot type)**.

### Macro cells

**Macro** fires one of the slot's [macros](macros.md) by name. The choice appears once the slot has at least one macro, and picking it preselects the first one.

![A menu cell bound to a macro](https://padforge.org/docs/images/menu-macro-cell.png)

The cell is an extra trigger for the macro rather than a copy of it, so the macro's own fire mode decides what happens: a **While Held** macro runs for as long as an On Click cell is held, and a one-shot mode sees the cell's fire as one press. The macro's **Layer** scope and the rest of its settings apply unchanged. A macro with no trigger of its own can be driven entirely from cells.

Renaming the macro updates every cell that names it. A cell whose macro no longer exists shows as **`<name>` (no such macro)** in the picker and does nothing until you pick another. Macro names on a slot are not unique. When two share a name, the cell fires the first.

### Cells as sources

Every cell also fires as a menu-item source, pickable in [Mappings](../features/mappings.md) rows and macro triggers as entries like **Menu 1 Cell 3**. The picker shows each cell's own number. A Radial Ring's cells count from 1, and its **Center Cell**, headed **Center** in the cell list, is **Cell 0** in a picker. A Touch Grid's and a Hotbar's cells count from 0. A cell set to **None** still fires this way, so a cell can do real work with no direct binding. A cell driven through a mapping row or macro shows **Used by a mapping or macro** in its binding picker while it has no direct binding of its own.

A menu imported from the Steam Workshop can carry richer cell behavior (key combos, layer switches, macros). Those arrive wired through the profile's mapping rows and macros automatically, so the cell fires whatever the config authored even though the cell row here shows no direct binding.

---

## Cell icons

Each cell row carries an icon button (tooltip: **Choose this cell’s icon: an emoji, an entry from an icon package, or an image file.**). It opens a picker under the button, and every choice in it is drawn as itself.

<!-- SCREENSHOT: icon-picker -->
![The icon picker on its Emoji tab. Menu cells and shift layers open the same picker.](https://padforge.org/docs/images/icon-picker.png)

- **Emoji** lists the emoji by category, the same catalog the [Shift Layer](shift-layers.md) window uses.
- **Images** shows the pictures in every added icon package, grouped by package. **Browse Files…** at its foot opens a file browser.

**No Icon** at the bottom of the picker clears the icon. The icon the cell holds now is marked, and the picker opens on its tab and its emoji category. A pick closes the picker, and so does a click anywhere else or Escape. Tab moves through the picker. The cell row shows the icon the cell carries.

The file browser accepts:

- A loose image file: `.png`, `.jpg`, `.jpeg`, `.bmp`, or `.gif`. A file kept under PadForge's own folder is stored by relative path, so the pair travels together. One directly beside `PadForge.exe` is stored as `.\name.png`.
- A `.pficons` package. Picking one adds it to the list below. A package with a single image binds it right away, and one with several opens the picker again on that package's pictures.

On the overlay the icon draws at the center of the cell, 30 pixels tall at the menu's normal size. An emoji fills the same box. WPF, which draws the overlay, has no color emoji, so on the PC an emoji draws in one color, the label's, and takes the hover color with the label. The phone's [Web Menus](web-controller.md#web-menus) page draws it in color. The **Icon Size** slider beside the cell's small icon preview scales that cell's icon on its own, from 25% to 200% of the menu's normal icon size, so one cell can read louder than its neighbors. On a Touch Grid with labels shown, an icon above about 120% makes every row of the grid taller to fit it. The slider appears only once the cell has an icon, and clearing the icon puts the size back to 100%. With **Show Labels** on, the icon sits above the label. An icon that cannot be found (a removed package, a moved file) falls back to the label alone. Menus imported from the Steam Workshop keep their Steam icon names and draw them from the local Steam client's art as before.

### Icon packages

![The Icon Packages card under the menus](https://padforge.org/docs/images/menu-icon-packs.png)

An icon package is one zip file of images with the `.pficons` extension. PadForge reads straight from the file and never unpacks it. Keep packages next to `PadForge.exe` to stay portable.

The **Icon Packages** card, folded to one line under the menu editor in the **Macros & Menus** section, manages them:

| Button | What it does |
|---|---|
| **Add Package…** | Add an existing `.pficons` file to the list. |
| **Create Package…** | Pick image files and save them as a new `.pficons` package. The new package is added at once. |
| **Remove** | Remove the selected package from the list. The file is not deleted. |

A package's name comes from a `manifest.json` inside the zip (`{"name": "..."}`) when one exists, otherwise from the file name. A second package with the same name is listed as `name (2)`. A cell remembers its icon as `package/entry`, so a profile shared with someone who has the same package finds the icon on their machine.

Exporting a profile as a `.pfprofile` bundles every icon package its menus reference, the same way it bundles sound packages. Importing lands them beside `PadForge.exe`, reuses a byte-identical copy already there, renames on a name clash, and rewrites the profile's icon references to whatever name the package registered under.

---

## The overlay

While a menu is engaged, a click-through overlay draws the ring or grid at the configured spot on the primary monitor, with the hovered cell highlighted in PadForge's ember accent. It never steals focus from the game.

Per-menu appearance controls: **Show Labels**, **Screen Position** (X and Y percent, default centered), **Size** (10–400%), and **Opacity** (5–100%, default 90).

The overlay itself is optional. The **Overlays** page under **Extras** carries a **Menu Overlay** switch, on by default. Turn it off and menus still hover and fire, just without the picture, which suits layouts you know by muscle memory. One menu shows at a time: the first one engaged wins until it disengages.

---

## On a phone

A Touch Grid menu can also appear on a phone. Turn on **Show on Web Controller** (tooltip: **Shows this menu as a page of the web controller’s Web Menus layout, where a tap on a tile fires its cell.**) and the menu becomes a page of tiles on every phone that runs the web controller's [Web Menus](web-controller.md#web-menus) layout and is assigned to this slot. A tap fires the cell the way the overlay does, mapping rows, macro triggers, and shift layers that read the cell included, and the overlay stays hidden for it.

The switch appears on Touch Grid menus. A menu switched to Radial Ring keeps showing it while it is on, so it can be turned off there or by its reset. The menu's **Layer** and device scope apply on the phone too: a menu scoped to another controller, or to a shift layer that is not active, stays off the phone.

---

## Menus and shift layers

Each menu carries a **Layer** picker naming the [shift layer](shift-layers.md) that permits it. **Any Layer** is the default and the menu is always available. **Base** also allows the menu under other active layers. A named layer permits the menu only while that layer is the active one.

**Layer Holds the Menu Open** changes how the menu opens. Normally a menu opens when you touch its surface or deflect its stick past the engage deadzone. With this on, the named layer opens it: the menu appears the moment the layer goes active and stays up for as long as it is held, and a **Steer With** picker chooses the physical input that moves the highlight while it is open. That input is read from the controllers assigned to the slot, and what each cell fires still comes from the cell's own binding. **Any Layer** and **Base** keep normal surface opening, so the toggle only does something on a named layer.

The fire modes read a little differently in this mode. **On Click** holds the highlighted cell's binding while the click input is held, and a highlighted radial center can fire with the stick at rest. **On Click Release** fires once when the click input releases, falling back to the previous selection if the surface releases in the same instant. **On Touch Release** fires when you lift off or return inside the engage deadzone while the menu stays open, and leaving the layer also commits a selection still being steered.

A menu imported from a Steam config that lived on an action layer engages only while that layer is held. Releasing the layer counts as letting go, so an On Touch Release menu commits its hovered cell right there, matching Steam's mode-shift behavior.

The row reset restores **Any Layer** and turns layer-held opening off.

---

## Reset buttons

Every setting row of a menu carries a per-field reset, and the overlay settings share **Reset Menu Geometry**.

---

## Related pages

- [On-Screen Keyboard](https://padforge.org/docs/features/on-screen-keyboard/): the Keyboard style.
- [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/): imported radial menus, touch menus and hotbars land here.
- [Shift Layers](shift-layers.md): layer-scoped menus and what layer cells become.
- [Button and Axis Mappings](../features/mappings.md): menu cells as mapping sources.
- [Macros](macros.md): a cell can name a macro directly, and any cell can trigger one as a source.
- [Profiles](profiles.md): exported profiles carry the icon packages their menus use.
- [Touchpad](../features/touchpad.md): the other things a touchpad surface can do.
- [Web Controller](web-controller.md#web-menus): the phone page that shows marked Touch Grid menus as tiles.

---

*Last updated for PadForge 6.0.0.*
