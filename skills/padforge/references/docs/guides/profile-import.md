<!-- https://padforge.org/docs/guides/profile-import/ as PadForge's skill carries it, from padforge.org's wiki/guides/profile-import.md. -->
# Import from reWASD, DS4Windows and DSX

*Turn a reWASD `.rewasd` config, a DS4Windows profile `.xml` or a DSX profile `.dsx` into an ordinary PadForge profile, after a preview of what came across.*

PadForge reads all three tools' files directly. You pick or drop the file, a preview lists every binding with its status, and **Import** adds the result to your profile list as a new profile. Nothing runs and nothing changes until you click **Import**, and the profile you have loaded stays loaded.

---

## Open a file

![Get Profiles on its Import a File tab](https://padforge.org/docs/images/profiles-get-file.png)

Three ways in, all from the **Profiles** page:

- Click **Get Profiles** and open **Import a File**. **Import…** in its foot, or a click on its drop zone, opens a file dialog. The dialog's first filter, **Profiles**, shows `.pfprofile`, `.rewasd`, `.xml` and `.dsx` files together. The other filters narrow it to PadForge profiles, reWASD, DS4Windows, DSX, or all files. A file dropped on that zone imports too.
- Click the dashed drop tile after the last profile card (**Drop a profile file here**). It opens **Get Profiles** on **Import a File**.
- Drag the file from Explorer onto the profile cards. The dashed **Drop to Import Profile** overlay appears while a `.pfprofile`, `.rewasd`, `.xml` or `.dsx` file is over them. Dropping several files imports each one in turn.

The dialog adds the three tools' profile folders to its places pane when they exist on this PC:

| Tool | Folder |
|---|---|
| reWASD | `C:\Users\Public\Documents\reWASD\Profiles` (the shared Documents folder) |
| DS4Windows | `%APPDATA%\DS4Windows\Profiles` (the per-user install) |
| DSX | `DSX_Savefile\Configuration Files\Controller Profiles` in DSX's folder, `steamapps\common\DSX`, in each Steam library, and in a beta build's folder inside it |

A DS4Windows build that keeps its profiles beside its own executable is not added. Browse to its `Profiles` folder yourself. DSX rewrites its profile files when it exits, so exit it from its tray icon first to bring over the settings it holds now. DSX 3.1 and later save `.dsx` files. Profiles from DSX 3.0 and earlier did not carry over to 3.1, and PadForge does not read them either.

PadForge decides what a file is from its contents, never from the extension. A file that opens with `{` is read as JSON: a DSX profile when it holds DSX's `profile_type` and `controller_general` sections, a reWASD config otherwise. An XML file has to have a `DS4Windows` root element. Anything else is refused. On the **Import a File** tab the reason shows under the drop zone and the window stays open. A file dropped on the profile cards gives the reason on the message line at the foot of the window:

| Message | When |
|---|---|
| "PadForge could not read that file." | The file is missing or locked. |
| "That file is too large to be a profile." | It is over 16 MB, or a reWASD file holds more than 64 shifts, more than 5000 entries in one table, or an input set of more than four inputs. |
| "That file is not a PadForge, reWASD, DS4Windows or DSX profile." | It is none of these formats, or its contents are malformed. |
| "That file declares a document type, which a profile never needs, so PadForge did not read it." | The XML carries a DTD. |
| "That file is not in the reWASD format PadForge reads, schema 3." | A reWASD file whose `schemaVersion` is not 3. |

---

## The preview

<!-- SCREENSHOT: import-preview -->
![The Import Profile preview over the Profiles page, for a reWASD config named Star Courier: ten bindings under Imported Cleanly and one Skipped gyro tilt zone under Needs a Look](https://padforge.org/docs/images/import-preview.png)

The **Import Profile** window opens over the Profiles page. From the top:

- Under the title, the file, its tool, and the name it lands under: "Star Courier.rewasd, a reWASD file. It becomes the profile Star Courier." A name that is taken gets a number, and the preview shows the numbered name.
- Three counts, each in a tile: **Clean**, **Partial** and **Skipped**. A **Partial** or **Skipped** count above zero is drawn in gold.
- **Needs a Look**: each binding that came across with a difference, or did not come across, in a box of its own. The box names the source and, after an arrow, what it became, then **Skipped** or **Partial** and the reason in full. **Skipped** bindings come first, then **Partial**, each group in the file's own order.
- **Imported Cleanly**: the bindings that came across as they were, two to a line, the file's name for each and what it became.
- A note on what the profile does when it loads and what you still have to do. It names every program the profile's buttons run and every program it starts on load, before you accept anything.

| Status | Meaning |
|---|---|
| **Clean** | The binding came across with the same behavior. |
| **Partial** | It came across with a stated difference, or it needs one step from you. The reason says which. |
| **Skipped** | It did not come across. The reason says why. |

**Clean** describes the translation. It is not a play test, so test the actions you need.

The note can say:

| Note | Meaning |
|---|---|
| "It starts this program when it loads: …" | A DS4Windows `LaunchProgram`. It runs each time the profile loads. |
| "Its buttons run these when pressed: …" | Programs and power commands (log off, shut down, sleep, hibernate, restart) the profile's buttons run, DS4Windows Program special actions included. |
| "After you load this profile, assign your controller to its slots on the Devices page." | Something reads the controller. The import does not pick one for you. |
| "It assigns All Keyboards (Merged) and All Mice (Merged) to its slots when it loads." | Keyboard and mouse inputs read the merged rows, which the profile assigns itself. |
| "This file maps nothing PadForge can use." | No slot came out of it. **Import** is unavailable. |

---

## Import

Click **Import** to add the profile, or **Cancel** to close the preview without adding anything. The message line confirms the result: "Imported “Star Courier”: 10 clean, 0 partial, 1 skipped".

Import adds a new profile and does not load it. Your active profile stays as it was. Click **Load** on the new profile's card, or let auto-switch pick it up. From then on it is an ordinary profile: edit it, export it, or delete it like any other.

### What the profile holds

- **Slots.** One controller slot per virtual controller the file uses, then one **Keyboard + Mouse** slot when anything sends keys or mouse. A reWASD Xbox 360 pad becomes an Xbox 360 slot, Xbox One over Bluetooth an Xbox One S slot, DualShock 4 and DualShock 3 the PlayStation presets of the same name, and Switch Pro a Nintendo slot. A DS4Windows profile whose output is DS4 gets a DualShock 4 slot, any other an Xbox 360 slot, and a `DinputOnly` profile gets no controller slot. A DSX profile gets the controller DSX emulated, Xbox 360, DualShock 4 or DualSense, and Native Mode gets no controller slot.
- **Devices.** Keyboard and mouse inputs read **All Keyboards (Merged)** and **All Mice (Merged)**, so they work the moment the profile loads, on any PC. Assign your controller to the controller slots on the [Devices](../features/devices.md) page.
- **Games.** reWASD's process names become the profile's executables. For DS4Windows, every program in `Auto Profiles.xml` (one folder above `Profiles`) that names this profile for a controller becomes one.
- **The name.** reWASD's app name, else its comment, else "Game - Config" when the file sits in reWASD's own `Profiles\Game\Controller` folder, else the file name. DS4Windows profiles keep their file name. DSX profiles keep the name DSX gave them.
- **Tuning.** Stick and trigger curves, motor strength, the gyro dead zone, the touchpad's glide, the battery light's colors, the player LEDs and their brightness, the mic LED and the headphone volume apply to the controller you assign, unless that controller already has its own setting. A mic LED that follows the PC's mute follows the PC's default microphone.

---

## From reWASD

| reWASD | In PadForge |
|---|---|
| A key, button or stick held to a held output | A [mapping row](../features/mappings.md). Outputs a row cannot carry (media keys, the Windows key, F13 and up, a partial stick value) ride a macro beside it. |
| Single, double, triple and long press, start and release | [Macros](macros.md) on **On Press**, **On Double Press**, **On Triple Press**, **On Long Press** and **On Release**, with the file's own wait times. A single beside a double or triple is **On Single Press**, and beside a long press **On Short Press**, so one button keeps all its gestures. |
| Repeat, turbo and toggle | A repeating macro, **Turbo**, and latches that flip on each press. A toggled sequence loops until the next press. |
| Shifts | [Shift layers](shift-layers.md) under the shift's own name. A shift's own button is a **Hold** or **Toggle** activator. Jumps that hold or toggle carry their delay, double-press window and release edge. Any other jump is a **Switch Layer** macro. |
| Do Not Inherit | The layer stops reading Base for those controls. |
| Unlock shifts | The layer hands the keyboard, the mouse or both back to Windows. |
| Unmaps | Requests for **Consume Mapped Inputs (Hooks)** on the device. A key that no row reads gets an empty macro that holds it back. |
| Radial menus | Radial [menus](menus.md) on the layer that opens them. A sub-menu's sectors join the main ring. |
| Mouse regions | A **Limit Mouse Region** macro held while the button is down. |
| Program and power commands | **Run Program**, named in the preview. |
| Other commands | Toggle virtual controllers, toggle the window, disconnect, gyro on, off and toggle, gyro recenter, Win + Print Screen, Alt + F4. Crosshair commands 1 to 10 (types 101 to 110) become **Toggle Crosshair** on crosshairs 1 to 10, and the command for every crosshair (type 120) becomes **Toggle Crosshair** on **Every Crosshair** ([Crosshair](../features/crosshair.md)). |
| Lightbar | Per layer: a solid color, or the rainbow, breath, heartbeat, stroboscope and shimmer patterns, played by an **Always** macro. |
| Gyro | Gyro mode reads roll and pitch, Tilt mode reads lean. Gyro to a stick on a PlayStation or Nintendo slot drives that slot's motion. |
| Use for UDP server | The profile turns on the DSU motion server while it is loaded. |
| Stick zones and direction zones | Bounded reads at the file's own zone edges. |

## From DS4Windows

| DS4Windows | In PadForge |
|---|---|
| A control with no entry | Sends its own output, the way DS4Windows' default does. Each listed control replaces its own row. |
| Button, key and macro assignments | Mapping rows, or macros for macros, toggled keys and moments such as a touch start. **HoldMacro** and **RepeatMacro** repeat while held. |
| Extras | Rumble and a lightbar color while the control is held. |
| Lightbar macros | A color sequence on the control's press or release. |
| Shift modifiers (`ShiftControl`) | One **Hold** layer per trigger, named "Shift" and the trigger. |
| Special actions | Read from `Actions.xml` one folder above `Profiles`: programs, profile switches (a **Profile Shortcut** action), macros, keys, Bluetooth disconnect, multi-actions (tap, hold past 500 ms, double tap), Xbox Game DVR, gyro calibration and wheel calibration. |
| Gyro output modes | Mouse, Mouse Joystick, Passthru on a DS4 output, Controls and Directional Swipe, each with its engage button and toggle. |
| Touchpad output modes | Mouse with its click regions, Absolute Mouse, Mouse Joystick and Passthru. Trackball mode becomes touchpad **Momentum**. PS + touch toggles the movement, as in DS4Windows. |
| Lightbar | A solid color, the rainbow at its speed and brightness, or the battery light from its low to full color. |
| Trigger effects | Rigid, Full Click and Pulse as adaptive trigger effects. |
| Two-stage triggers | Soft and full pulls in the same mode. |
| Steering wheel emulation | The controller's lean on the named axis. |
| Rumble boost below 100 and the idle disconnect | The motor strength and idle disconnect time. |

## From DSX

| DSX | In PadForge |
|---|---|
| Virtual controller | Xbox 360, DualShock 4 or DualSense. A DualSense whose subtype has audio becomes **DualSense (PS5): Full**. Native Mode makes no controller slot. |
| A control with no binding | Sends its own output, as DSX does, unless its stick, motion or touchpad mode is Disabled. |
| Stick inversion and swapped sticks | The stick rows invert, and swapped sticks drive each other's outputs. |
| Dead zones | A trigger's start and end become its dead zone and range. A simple stick dead zone becomes the stick's radial or axial dead zone. An advanced zone's dead zone, anti-dead zone and saturation become the stick's dead zone, anti-dead zone and range, and its Range scales the output. |
| Adaptive triggers | Each mode a DSX profile saves, played as an [adaptive trigger effect](https://padforge.org/docs/features/adaptive-triggers/) by an **Always** macro: Feedback, Weapon, Vibration, Slope Feedback, both Multiple Position modes, Bow, Galloping, Machine, Custom Trigger Value and DSX's named presets. Each saved trigger preset other than the one in force becomes a macro to bind. |
| Stick modes | Stick to Mouse, Stick to Scroll, Flick Stick, WASD, the arrow keys, custom keys, and the D-pad scheme of Stick to Controller Buttons. MMO mode holds a mouse button while the stick is pushed. Disabled sends nothing. |
| Motion | Motion to Mouse, Motion to Left or Right Stick at DSX's full speed in degrees per second, Motion to Keyboard and Motion to Controller Buttons, with DSX's axis switches and MMO mode. An axis the profile turns off binds nothing. Disabled sends nothing. A PlayStation slot keeps the controller's motion while DSX passes it through, which Disabled never does. |
| Touchpad | Passthrough feeds a PlayStation slot's touchpad. Touchpad to Mouse moves the pointer, and Press to Left Click clicks. Disabled sends nothing. |
| Lightbar | Static, breathing, strobe, the color cycle and the battery light at DSX's brightness, played by an **Always** macro. |
| Player LEDs and mic LED | The controller's player LEDs and their brightness, and a mic LED that follows the PC's mute. |
| Rumble and headset volume | Rumble off, and the controller's headphone volume. |
| Passthrough switches | While DSX lets the game set the lights or the adaptive triggers, PadForge leaves them to the game too. |
| Button Mapping | Bindings on the D-pad: keys, mouse buttons, held shortcuts, turbo and profile switches. A binding that mutes its input stops the input's own output. A binding on any other button shows on its line with what it sends, so you can map it by hand. |

---

## What reads Partial or Skipped

The reasons below are condensed from the preview's text, which names the control, setting or device each time. Partial rows came across with that difference. Skipped rows did not come across.

| Status | Reason in the preview | What to do |
|---|---|---|
| Partial | reWASD stopped these inputs reaching Windows. Turn on Consume Mapped Inputs for the device on the Devices page, which stops the ones this profile maps. | Turn on **Consume Mapped Inputs (Hooks)** for the keyboard or mouse. |
| Partial | In reWASD, a mapped key still reaches Windows because the file maps it without an unmap. With Consume Mapped Inputs on, PadForge holds it back. | Leave the switch off if the game needs that key too. |
| Partial | reWASD hid this controller from games. Turn on Hide from Games for it on the Devices page. | Turn on **Hide from Games (HidHide)** for it on the Devices page. |
| Partial | reWASD stopped mouse movement reaching Windows. PadForge holds back keys and mouse buttons but not movement. | The pointer still moves. |
| Partial | This file keeps two controllers apart. In PadForge, any controller on the slot fires these mappings. | Use one controller per slot. |
| Partial | reWASD sent this controller to another device through an adapter. | PadForge plays it on this PC. Remote Link sends it to another PC running PadForge. |
| Partial | Nothing in this file opens this layer. Under Mappings, right-click the layer and pick Configure Activator. | Give the layer a button with **Configure Activator…** in the layer's right-click menu, in the controller page's **Mappings** section. |
| Partial | Its sub-menus became more slots on one ring. | None. Every action is still on the ring. |
| Partial | The radial menu commits when the stick re-centers or the layer ends. reWASD's Apply button is not used. | None. |
| Partial | reWASD keeps its crosshairs in its own settings, not in the config. Set yours up on the Crosshair page. | The button toggles a PadForge crosshair. Set its look on the **Crosshair** page under Extras. |
| Partial | The movement speed or flick did not come across. | Set the speed on the slot. |
| Partial | PadForge reads Gyro Left and Right from roll and Lean from yaw, reWASD's default. The file does not record whether reWASD's Preferences swapped them. | Swap the rows if your reWASD did. |
| Partial | A touchpad diagonal is two directions at once, which the touchpad reports only when its D-Pad Mode under Motion & Touch is 8-Way. | Set **D-Pad Mode** to **8-Way** in the controller page's **Motion & Touch** section. |
| Partial | The layers tune a stick or trigger differently, so each keeps its own tuning, and the curve comes across as the nearest power curve. | None. |
| Partial | DS4Windows came back to this profile from another one on a button. | Add a **Profile Shortcut** for this profile on the Profiles page. |
| Partial | DS4Windows placed the pointer where the stick points. In PadForge the stick moves the pointer. | None. |
| Partial | A stick direction reads the stick before its rotation, or before its zones and curve. DS4Windows read it after. | None. |
| Partial | DS4Windows shapes a radial stick, or the gyro dead zone, by the angle of the push. | A straight push plays the same. A diagonal one differs. |
| Partial | Scroll, tap or an absolute region on the touchpad did not come across. Set them under Motion & Touch. | Set them in the controller page's **Motion & Touch** section. |
| Partial | DSX does not publish its input numbers. Published DSX profiles use 41 to 44 as the D-pad, so PadForge reads them that way. | None. |
| Partial | DSX gives no unit for this, so PadForge uses its own. | DSX's mouse, scroll, flick, touchpad and motion speeds, MMO mode, rumble intensity, its press thresholds and its light timings. Tune them on the slot. |
| Partial | DSX does not publish what a setting does, so PadForge uses its own, followed by the setting. | An advanced zone, a flick curve, direction zones and a roll axis. |
| Partial | DSX lets the game set this while its passthrough switch is on, so PadForge leaves it to the game too. | Set a light or a trigger effect in PadForge if the game sends none. |
| Partial | In Native Mode with Send Data to Device off, DSX sends nothing of its own to the controller. | None. |
| Partial | DSX does not publish how its turbo times each press, followed by the wait PadForge uses. | Change the macro's repeat delay. |
| Partial | No published DSX profile shows a custom key, so PadForge reads it the way Windows names keys. | Check the key. |
| Partial | DSX does not publish which way its motion axes turn. PadForge reads X as pitch, Y as yaw and Z as roll. | Invert a row if a direction is the wrong way. |
| Partial | PadForge plays a game’s DualSense haptics on the controller’s own actuators when it can, and as rumble when it cannot. | None. |
| Partial | DSX switches to this preset from a shortcut. Its macro is in the controller’s Macros & Menus section, ready to bind. | Give the macro a button. |
| Partial or Skipped | This setting did not come across, followed by its name. | Covers settings with no published unit or counterpart: reWASD's report rate, virtual USB hub, extended inputs, macro time unit and some stick and mouse settings, and DS4Windows' extra gyro buttons beyond the first, the touchpad's Release button mode, rotation and threshold, the gyro mouse threshold, the gyro stick's maximum output and other entries, and DSX's saved lightbar and haptics presets, a trigger end at or below its start, a lightbar section with no color mode and its game feedback passthrough switch turned off. |
| Skipped | PadForge reads the gyro’s direction past one threshold, and reWASD gives no unit for a gyro zone’s edge. | Map the gyro in the controller page's **Mappings** section. |
| Skipped | In Tilt mode, reWASD’s Lean is a turn around the vertical axis, and a motion sensor gives no angle for that turn. | None. |
| Skipped | An Xbox virtual controller has no motion. | Pick a PlayStation or Nintendo controller type to send motion. |
| Skipped | PadForge reads two sticks, and this names a third. | None. |
| Skipped | reWASD keeps its crosshairs in its own settings, not in the config. | A crosshair's picture and look. Set them on the **Crosshair** page under Extras. |
| Skipped | Stick jitter and shaking, or analog key actuation points, did not come across. | None. |
| Skipped | A macro cannot press this input, followed by its name. A row under Mappings can. | Map it as a row. |
| Skipped | Steering wheel emulation did not come across. Map the wheel under Mappings. | DS4Windows named an axis PadForge does not know. Map the wheel in the controller page's **Mappings** section. |
| Skipped | PadForge has no such setting, followed by its name. | DS4Windows' rumble auto-stop, mouse acceleration, button-to-mouse speed, stick fuzz, square stick, anti-snapback, delta acceleration, low-battery flash and charging light, and DSX's low-battery flash, enhanced rumble, game feedback level, audio to haptics, the modes that play an input or the rumble as haptics, Dynamic Rumble Sync, random light colors, middle battery colors and its mic button's PC microphone mute. |
| Skipped | DSX stores this as numbers it does not publish, followed by the numbers. | A DSX button binding off the D-pad, a speed button or a haptics button list. The line says what the binding sent, so map it on the right button. |
| Skipped | No published DSX profile uses this mode, followed by its name. | Set the mode up in PadForge. |
| Skipped | DSX does not write a binding like this one, so the file was changed outside DSX. | None. |
| Skipped | PadForge keeps Master Volume in its app settings, outside any profile. | DSX's speaker volume. Set **Master Volume** in the controller page's **Audio** section. |
| Skipped | This profile names a special action missing from `Actions.xml` beside its Profiles folder, followed by its name. | Copy `Actions.xml` along with the profile and import again. |
| Skipped | An unknown input, output, command or controller button. | The row names it. |

---

## Related pages

- [Profiles](profiles.md): Get Profiles, the drop tile and the profile cards.
- [Switching from Another Tool](https://padforge.org/docs/guides/switching/): moving one game at a time, with a way back.
- [Shift Layers](shift-layers.md), [Macros](macros.md) and [Menus](menus.md): where the layers, press types and radial menus land.
- [Devices](../features/devices.md): assign your controller, and the **Consume Mapped Inputs (Hooks)** and **Hide from Games (HidHide)** switches.
- [Profile Import Internals](https://padforge.org/docs/reference/profile-import-internals/): how the importers work.

---

*Last updated for PadForge 6.0.0.*
