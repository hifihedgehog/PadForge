<!-- https://padforge.org/docs/guides/profiles/ as PadForge's skill carries it, from padforge.org's wiki/guides/profiles.md. -->
# Profiles

*A full snapshot of your PadForge setup. Switch profiles and the slots, mappings, deadzones, macros, and feedback settings all change at once.*

<!-- SCREENSHOT: profiles -->
![Profiles page with a card for each profile, the starter profiles, and the Automatic Switching, Shortcuts and External Control cards](https://padforge.org/docs/images/profiles.png)

Profiles can switch on their own when a game gains focus, from a controller-button combo you record, or on a command from a launcher or script.

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-profile-per-game-poster.jpg"
       aria-label="Video: one profile per game, recorded live in PadForge 5.2.0">
<source src="https://padforge.org/assets/video/padforge-tutorial-profile-per-game.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-profile-per-game.vtt" default>
</video>
<figcaption>Save As, a linked program and auto-switch, recorded live in PadForge 5.2.0 in 1 minute 1 second. The Profiles page has been rebuilt since, so the buttons sit in other places today. Character Map stands in for a game, and a panel covers the file dialog, which lists the recording PC's own folders. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-profile-per-game-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

---

## What a profile holds

| Setting | What is captured |
|---|---|
| Virtual controller topology | Created slots, enabled slots, and the type of each slot (Xbox, PlayStation, Nintendo, Extended, MIDI, Keyboard + Mouse, VR). |
| Button and axis mappings | Every per-device mapping for every slot. See [Button and Axis Mappings](../features/mappings.md). |
| Deadzones | Per-axis [Stick Deadzones](../features/stick-deadzones.md) and per-trigger [Trigger Deadzones](../features/trigger-deadzones.md). |
| SOCD cleaning | Per-slot Simultaneous Opposite Cardinal Directions (SOCD) mode and button pairs, applied to the slot's final combined output. |
| Force feedback | Per-slot [Force Feedback](../features/force-feedback.md) settings. |
| Bass Shakers | Per-slot Rumble to Audio routing. Output device, gain, speaker placement, and the tone tuning for each of the four rumble voices. |
| Impulse triggers | Per-slot [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/) gain, swap, Constant Trigger Force, and Audio Bass Trigger Rumble. |
| Adaptive triggers | DualSense [Adaptive Triggers](https://padforge.org/docs/features/adaptive-triggers/) modes and curves. |
| Lighting | [Lighting](../features/lighting.md) modes for DualSense and DualShock 4 lightbars, plus Guide Button LED brightness for Xbox One and later controllers, Steam Controllers, Switch Pro Controllers, and right Joy-Cons. |
| Gyro | Per-pad-per-slot [Gyro](gyro.md) tuning. Reference frame, sensitivity, smoothing, real-world calibration, Aim Engage button. |
| Flick Stick | Per-pad-per-slot [Flick Stick](../features/stick-deadzones.md#flick-stick) tuning. Dots per 360°, Flick Time, Flick Threshold, Snap Angle, Snap Strength, Front Angle Deadzone, Sweep Smoothing. |
| Macros | All [Macros](macros.md). Triggers, actions, repeat modes. |
| Menus | Per-slot radial and touch [Menus](menus.md): cells, host input, fire mode, and overlay placement. |
| Touchpad outputs and gestures | Per-slot [Touchpad](../features/touchpad.md) toggles (Stick / D-Pad, Mouse, absolute pointer, swipe haptics, gesture detection) plus custom shape templates recorded with the recorder dialog. Different games can carry different gesture catalogs. |
| Crosshairs, Magnifier and Screen Overlays | The [Crosshair](../features/crosshair.md), [Magnifier](../features/magnifier.md) and [Screen Overlay](../features/screen-overlay.md) settings from Extras. |
| Extended slot shape | Per-slot Sticks, Triggers, POVs, and Buttons counts for Extended (HIDMaestro) slots. |
| Polling rate override | Optional. A profile can carry its own polling rate, set in the profile dialog. While that profile is active it replaces the global Settings value. See [Polling rate override](#polling-rate-override). |

A profile switch changes all of these at once. Its macro list replaces the current macros, and an empty list clears them. A profile saved before macros rode profiles has no macro list and leaves the current macros alone. Physical controllers stay connected. Only the virtual side changes.

---

## The Default profile

**Default** loads at startup and runs any time no other profile matches the foreground app. If a named profile was active when PadForge closed, startup puts it back on top of Default, and a launch command from a launcher or script, or auto-switching, can pick another. You cannot delete, rename, or edit Default. Its card carries a **Built-in** tag.

Set Default to your general layout, like a standard Xbox pad for platformers. Make game-specific profiles only where you want something different.

> **Important.** Changes you make on any page (Home, a controller's page, Extras, etc.) save into the profile that is loaded right now.

---

## Auto-switch on app focus

PadForge watches the foreground window at about 30 Hz. When the app in front matches a profile, that profile loads.

1. Add one or more game executables to a profile.
2. The matching app gains focus. PadForge loads the profile.
3. An unmatched app gains focus (desktop, browser, etc.). PadForge goes back to Default.

The outgoing profile's state saves before the new one loads. A virtual controller whose slot keeps the same preset stays connected through the switch. One whose preset changes is rebuilt with the new identity, and one the new profile turns off or removes goes away.

Turn it on from the **Automatic Switching** card on the **Profiles** page: switch on **Switch Profiles by Game**.

### What is in front now

With **Switch Profiles by Game** on, a line under the card's title names the program in front right now, by its file name, and says what it loads. It reads once a second, so you can confirm matching works without launching the game blind.

| The line reads | When |
|---|---|
| **In front now: game.exe, which loads Racing** | A profile lists that program. |
| **In front now: game.exe, no profile set for it** | No profile lists it. |
| **In front now: game.exe, and no profile has a game set yet** | No profile lists any program. Nothing can auto-switch until you add one. |

<!-- SCREENSHOT: profiles-foreground-readout -->
![The In front now line on the Automatic Switching card, naming a program a profile loads for](https://padforge.org/docs/images/profiles-foreground-readout.png)

---

## Make a profile

1. Open the **Profiles** page in the sidebar.
2. Click **New Profile** at the top of the page for a profile with no controllers. To clone the current setup, open the **More** menu (the three dots) on the active profile's card and pick **Save As…**.
3. Type a **Name** (Forza, Elden Ring, Flight Sim, etc.).
4. Under **Games That Load It**, click **Browse…** and pick the game's executable (multi-select works), or click **Add a Running Game** and pick one of the programs that have a window open now.
5. Click **Save**.

Each game shows as a chip with its file name. The chip's tooltip gives the full path, and its remove button takes it off the list. Nothing is kept until you click **Save**.

### The Save As path

The fastest way to make a per-game profile:

1. Set PadForge up the way you want for one game. Mappings, deadzones, macros, slots.
2. Open **Profiles**, open the active card's **More** menu, and pick **Save As…**.
3. Name it after the game.
4. Add the game's executable under **Games That Load It**, then click **Save**.

The working setup is now a saved profile, and it loads on its own when the game comes to the front once **Switch Profiles by Game** is on. It saves no choice for the **Enable UDP Tracking Input** and **Enable FreeTrack 2.0 Shared Memory Input** switches on the [Head Tracking](../features/head-tracking.md) page under Extras, so loading it leaves them as they are. Flip either one while the profile is active to save that choice into it.

### Save Current Setup Here

**Save Current Setup Here** in a stored profile's **More** menu replaces what that profile holds with the current setup, after a confirmation. The profile keeps its name, its games, and its polling rate. The active profile's menu and Default's menu do not offer it.

---

## Executable matching

Each profile can list one or more executables. When the foreground process matches one of them, the profile loads.

- Added through **Browse…** or **Add a Running Game** in the profile dialog. Stored as full paths.
- Multiple executables per profile. One profile can cover several launchers or game versions.
- Case-insensitive. `C:\Games\EldenRing.exe` matches `c:\games\eldenring.exe`.
- The remove button on a game's chip drops it from the list.

---

## Profile cards

The page shows one card per profile, with a dashed drop tile after the last one. Each card carries:

- The profile name, with a **Built-in** tag on Default.
- A game line. **No game set** when the profile lists no executable. Otherwise up to two executable names, then a `+N` marker for the rest, after **Loads for** while **Switch Profiles by Game** is on and **Set for** while it is off. The tooltip lists every game.
- A picture of each controller in the profile, with its type and number under it, as many as the card has room for, then a `+N` count of the rest. A profile with no controllers shows **No controllers** instead.
- **Load** at the foot, or **Active** with a flame on the card of the profile that is loaded now.
- A **More** button (three dots) with the card's menu.

The active profile's card carries an ember edge and a glow, and its controller pictures are lit. A click on one of them opens that controller's page. Double-click a card to load its profile.

---

## Manage profiles

| Action | Where | What it does |
|---|---|---|
| **New Profile** | Top of the page | A profile with no controllers. |
| **Get Profiles** | Top of the page | Opens Get Profiles: **Starters**, **Community**, and **Import a File**. |
| **Load** | A card's foot | Applies that profile. Double-click on the card also loads. |
| **Edit…** | **More** menu | Rename the profile, edit its games, set its polling rate, and, on a profile imported from the Steam Workshop, turn its game's look on or off. Not on Default. |
| **Save As…** | **More** menu, active card only | Clones the current setup into a new profile. |
| **Save Current Setup Here** | **More** menu, other stored profiles | Replaces what the profile holds with the current setup. |
| **Export…** | **More** menu | Writes the profile to a shareable `.pfprofile` file. |
| **Delete** | **More** menu | Removes the profile after a confirmation. Not on Default. |

**Get Profiles** has three tabs:

- **Starters**: save a ready-made genre archetype as a new profile. See [Start from a starter profile](https://padforge.org/docs/guides/starter-profiles/). The page's **Starter Profiles** row shows the first four. A click on one adds it, and **See All** opens this tab.
- **Community**: browse the Steam Workshop and import a community-made Steam Input config as a new profile. See [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/).
- **Import a File**: read a `.pfprofile` back in, or turn a reWASD `.rewasd` config, a DS4Windows profile `.xml` or a DSX profile `.dsx` into a new profile. See [Import from reWASD, DS4Windows and DSX](profile-import.md).

---

## Polling rate override

A profile can carry its own polling rate. In the **New Profile**, **Save As Profile**, or **Edit Profile** dialog, open **More** and pick a value from the **Polling Rate** dropdown.

| Choice | Interval |
|---|---|
| **Default (Global Setting)** | Follows **Polling Rate (Unless a Profile Overrides It)** on the [Settings](../features/settings.md) page. |
| **10000 Hz (0.1 ms)** | 0.1 ms |
| **8000 Hz (0.125 ms)** | 0.125 ms |
| **4000 Hz (0.25 ms)** | 0.25 ms |
| **2000 Hz (0.5 ms)** | 0.5 ms |
| **1000 Hz (1 ms)** | 1 ms |
| **500 Hz (2 ms)** | 2 ms |
| **250 Hz (4 ms)** | 4 ms |
| **125 Hz (8 ms)** | 8 ms |
| **60 Hz (16.667 ms)** | 16.667 ms |

A profile from before PadForge 6 stored its override as an interval in milliseconds. It converts to the nearest rate, and a rate the list does not hold gets an entry of its own in the dialog.

While a profile that carries an override is active, the override sets the loop rate and the global Settings value does nothing. Every other profile, Default included, falls back to the global value.

The Settings page names the profile in charge beside the polling rate box, in gold text:
**Overridden while profile “Racing” is active: 125 Hz**. The line is hidden while
the global value rules.

<!-- SCREENSHOT: profile-polling-override -->
![The Polling Rate dropdown under More in the profile dialog](https://padforge.org/docs/images/profile-polling-override.png)

The poll loop stays global and nothing restarts on a switch. The loop recomputes
its target every cycle, so a new rate takes effect on the next tick.

---

## Share and back up profiles

Export writes a profile to a `.pfprofile` file. That file is one archive holding the profile plus any sound packages its macros use, so a profile with custom macro sounds travels complete. A profile's [game look](https://padforge.org/docs/features/themes/#a-steam-profiles-game-look) travels as its game and colors, without its picture. Send it to someone else, or keep it as a backup.

1. Open the profile card's **More** menu.
2. Click **Export…**.
3. Pick a location and save. The file is named after the profile.

You can export the **Default** profile too. It writes the Default profile's own settings, even while another profile is active.

Import reads a `.pfprofile` back in.

1. Click **Get Profiles** and open **Import a File**, or click the drop tile after the last card.
2. Click **Import…** and pick the `.pfprofile` file.
3. The profile joins the cards, and any bundled sound packages install alongside it. If the name already exists, PadForge adds a number.

Import does not activate the profile. Load it, or let auto-switch pick it up.

The same tab reads reWASD `.rewasd` configs, DS4Windows profile `.xml` files and DSX `.dsx` profiles. Those open a preview of what came across first, and **Import** there adds a new profile. See [Import from reWASD, DS4Windows and DSX](profile-import.md).

### Drag and drop

Drag a `.pfprofile`, `.rewasd`, DS4Windows `.xml` or DSX `.dsx` file from Explorer and drop it onto the profile cards to import it. A dashed **Drop to Import Profile** overlay appears while a valid file is over the cards. Several files dropped at once import one after another. The drop tile after the last card (**Drop a profile file here**, **PadForge, reWASD, DS4Windows or DSX**) and the **Import a File** tab take a drop the same way.

---

## Community configs from the Steam Workshop

The **Community** tab of **Get Profiles** imports a community-made Steam Input config from the Steam Workshop as a new profile. The full flow (opt-in, browsing, the translation manifest) lives on [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/). What matters here:

- An imported profile carries only the pads the config actually drives. A config with both controller and keyboard/mouse bindings gets an **Xbox** pad and a **Keyboard + Mouse** pad, a pure keyboard/mouse layout gets a single Keyboard + Mouse pad, and a plain controller passthrough gets a single Xbox pad. Extra action sets arrive as [Shift Layers](shift-layers.md), cursor warps and autofire as [Macros](macros.md) on the Xbox pad.
- It ships with no device assignments and no executable. Assign your controller to each created pad, then pick **Edit…** in the card's **More** menu and add the game's executable so auto-switch can pick it up.
- It can wear the game's look, the game's library picture and four colors taken from it, while it is the active profile. Turn on **Wear the Game’s Look** at the import or later in **Edit…**. See [Themes](https://padforge.org/docs/features/themes/#a-steam-profiles-game-look).
- Imports always create a new profile. A taken name gets a number appended.
- The profile remembers which Workshop config it came from, so **Check Imported Profiles for Updates** on the [Settings](../features/settings.md) page can tell you when the config changed upstream. A **Save As…** fork counts as your own work and drops that link, and so does **Save Current Setup Here**.

---

## Controller shortcuts

Record a controller button combo and have it switch profiles, show or hide the window, or turn every virtual controller on or off. You can do this without a keyboard.

### Shortcut modes

| Mode | What it does |
|---|---|
| **Next Profile** | Move forward one step in the profile list. |
| **Previous Profile** | Move backward one step. |
| **Specific Profile** | Jump straight to a named profile. |
| **Show or Hide PadForge** | Minimize PadForge when it is in front. Restore and raise it when it is minimized, hidden, or in the background. Honors the **Minimize to System Tray** setting and full screen. |
| **Turn Virtual Controllers On or Off** | Turn every created controller on or off with one combo. If any slot is enabled, the combo disables all of them. If every slot is already disabled, the combo enables them all. A bottom-of-screen flyout confirms the new state. |

### Add a shortcut

1. Open the **Profiles** page. Click **Add Shortcut** on the **Shortcuts** card.
2. A shortcut reads as a sentence: the mode, **when**, the buttons, **on**, the device. Pick a mode from the first dropdown.
3. For **Specific Profile**, pick the target profile from the dropdown that appears after it.
4. Pick a device after **on**. One specific controller, or **Any Connected Device** to fire from any pad.
5. Click the record button and press your combo within 5 seconds. The seconds count down while it records.

Each button of the combo shows as a key. The clear button empties the combo, and **Delete** at the row's end removes the shortcut. **Reset All** on the card puts each shortcut's mode, profile, and device back to their defaults and keeps the recorded buttons.

### Recording details

- Buttons. Press one or more at the same time to make a combo.
- Axes. Triggers and sticks count as inputs, with direction (left-stick-left can be Previous, left-stick-right can be Next).
- Cross-device combos. Buttons from different controllers can combine into one shortcut.

---

## External control from launchers and scripts

A launcher, script, or automation tool can activate and deactivate profiles from
outside PadForge. Playnite and LaunchBox are the two everyone asks about, and
both work the same way: run one command before the game starts and another after
it closes.

Turn it on under **Extras**, **Automation**: switch on **Allow Outside Programs**.
The level **Commands**, the default, is enough to switch profiles. PadForge then
serves a local named pipe, `PadForge.Control`, while Automation is on, whether or
not the engine runs. Local machine only, off by default. The **External Control**
card on this page says what Automation allows now, with **Open Automation**. A
settings file that had the old **Allow External Control by Launchers and Scripts**
on opens with Automation on at Commands. See [Automation](../features/automation.md).

<!-- SCREENSHOT: profiles-external-control (the External Control card on the Profiles page, with Open Automation) -->
![The External Control card on the Profiles page, with Open Automation](https://padforge.org/docs/images/profiles-external-control.png)

### The commands

One UTF-8 line in, one line out, one command per connection. Verbs and replies
are fixed ASCII and never translate.

| Command | What it does | Reply |
|---|---|---|
| `activate <name>` | Loads the profile with that name (or its id) and holds it. Name matching ignores case. | `ok <name>` |
| `deactivate` | Releases the hold and goes back to Default. | `ok default` |
| `query` | Reports the active profile and whether it is held. | `ok <name> pinned` or `ok <name> unpinned`. The name reads `default` when no profile is active. |

The pipe takes more commands than these, for the engine, the virtual controllers,
the window and diagnostics. [Command Line](../reference/command-line.md#the-padforgecontrol-pipe)
lists them all.

These replies cover the failures:

| Reply | Cause |
|---|---|
| `error unknown-profile` | No profile matches that name or id. |
| `error unknown-command` | The verb is not one the pipe knows. |
| `error empty` | The line was blank, or `activate` arrived with no name. |
| `error internal` | The command threw. |

PadForge answers every connection, a blank line included, so a client waiting on
a reply never hangs.

### The hold

An externally activated profile is **held**. Auto-switch on app focus cannot
replace it while the hold lasts, so the same game can run under different
profiles depending on how it was launched. The hold is never written to disk. It
ends when:

- the script sends `deactivate`,
- you switch profiles yourself, by the profile switcher in the title bar, a card's
  **Load** button on this page (loading **Default** reverts), a controller shortcut, or a
  [Web Menus](web-controller.md#profiles-from-the-phone) phone,
- you turn Automation off,
- PadForge restarts.

A profile set by `--profile` as PadForge starts is held the same way. The launch
command runs once, so restarting the engine does not apply it again.

A `deactivate` and Automation turned off both tell the foreground monitor to re-read
the window in front, so a still-focused game picks its own rule back up on the
next check.

### From PowerShell

[Command Line](../reference/command-line.md#a-powershell-function) has `Send-PadForge`,
a PowerShell function that checks the pipe belongs to PadForge, sends one line and
returns the answer. It runs from a normal, unelevated process, with no UAC prompt:

```powershell
Send-PadForge 'activate Racing'
Send-PadForge 'deactivate'
```

A script that wants to check for `error unknown-profile` tests the answer it returns.

### Playnite

In a game's settings, add two script actions (or use a plugin):

- **Before starting a game**: `Send-PadForge 'activate <profile>'`, with the function above it.
- **After exiting a game**: `Send-PadForge 'deactivate'`.

Two Playnite entries for the same game can activate two different profiles, which
is exactly the "Play with Controller" versus "Play with Keyboard & Mouse" split.
Plugin authors can skip PowerShell and open the pipe directly with
`NamedPipeClientStream`, check that the pipe's owner is Administrators, write one
UTF-8 line ending in a newline, and read one line back.

### LaunchBox

Add two Additional Applications to the game:

1. **Application path**: `PadForge.exe` (its full path). **Parameters**: `--profile "<profile>"`. Check **Automatically run before main application**.
2. The same with `--default-profile`, checking **Automatically run after main application**.

### Command-line form

`PadForge.exe --profile "Name"` and `PadForge.exe --default-profile` map onto
`activate` and `deactivate`. Which path they take depends on whether PadForge is
already up.

| State | What happens |
|---|---|
| Already running | The second start hands the command to the running copy over the pipe, prints the reply, and exits with no window and no UAC prompt. This needs Automation on. With Automation off it exits with code 5 and changes nothing. |
| Not running | PadForge starts, with its UAC prompt, and applies the profile once the engine is up, so a launcher can start it straight into a profile. This works with Automation off. |

The exit codes are on [Command Line](../reference/command-line.md#exit-codes).

---

## Switch flyout

A small flyout slides up from the taskbar when a controller shortcut, a Profile Shortcut macro action, or a [Web Menus](web-controller.md#profiles-from-the-phone) phone switches the profile.

| Stage | What shows |
|---|---|
| Profile name | The new profile's name. Two seconds. |
| Initializing | **Initializing** with a flashing icon while the virtual controllers start up. |
| Active | **Forging** with an accent-colored checkmark. The controllers are ready. Two seconds. |
| Offline warning | After the Active stage, if one or more controllers have no online physical devices, a warning icon and **One or more controllers offline** show for another two seconds. |

The flyout matches the Windows 11 volume OSD styling and follows your light or dark theme.

An auto-switch on app focus does not raise this flyout. The profile pill in the title bar shows the new name and flares for a moment.

The Turn Virtual Controllers On or Off shortcut shows its own flyout (**Virtual Controllers Enabled** or **Virtual Controllers Disabled**) instead of the profile flyout.

The **Overlays** page under Extras carries a **Profile Switch Overlay** switch, on by default. Switch it off and shortcuts still switch profiles, just without the flyout.

---

## Examples

| Scenario | Setup |
|---|---|
| Racing game with custom deadzones | Make a "Forza" profile with wider trigger deadzones and a steeper stick curve. Add `ForzaHorizon5.exe`. It loads on launch and reverts on Alt+Tab. |
| Flight sim on Extended (HIDMaestro) | Make an "MSFS" profile that uses an Extended slot instead of Xbox. Map axes to flight-stick axes. Add `FlightSimulator.exe`. Other games keep the Default Xbox profile. |
| Switch emulator with a Nintendo pad | Make a profile that uses a Nintendo slot instead of Xbox. The emulator sees a Switch Pro Controller and shows Nintendo button prompts. Add the emulator's executable. |
| Several emulators, one profile | Make an "Emulators" profile and add `Dolphin.exe`, `Cemu.exe`, and `Ryujinx.exe`. All three load the same setup. |
| Macros for one game only | Make a profile with D-pad-to-keyboard macros for an MMO and add the MMO executable. Keep Default free of those macros. With auto-switching on, the MMO profile loads when the game comes to the front and Default returns when you leave it. |
| Quick on/off from the couch | Bind **Turn Virtual Controllers On or Off** to LS + RS. Press it to make every virtual pad go away (for keyboard play), press again to bring them all back. |

---

## Tips

- Set up Default first. It is your everyday layout.
- **Save As…** from a working setup is faster than building a profile from scratch.
- Test auto-switch by Alt+Tabbing between your game and another app. Watch the **In front now** line and the profile pill in the title bar.
- Macros save per profile and run while their profile is active. Auto-switching picks the profile by the game in front, but a profile you load by hand, or one a launcher holds, keeps its macros after you leave the game.
- Physical device connections stay open across switches. Only the virtual side changes.
- Export profiles to `.pfprofile` files to back them up or share them.
- Controller shortcuts beat Alt+Tabbing for mid-game profile changes.

---

## Limitations

- Auto-switch reads the foreground window. Games launched from a launcher that stays in front (some bootstrappers) may need the launcher's executable in the list too.
- Match is by full file path. A game installed in two places needs both paths added.
- Turn Virtual Controllers On or Off only acts on slots you have created. It does not create or remove slots.

---

## Related pages

- [Home](../features/dashboard.md): the controllers of the active profile.
- [Controller Slots](../features/controller-slots.md): a profile switch updates every slot in the configuration.
- [Devices](../features/devices.md): physical device connections stay open across switches.
- [Settings](../features/settings.md): **Check Imported Profiles for Updates** for profiles imported from the Steam Workshop.
- [Button and Axis Mappings](../features/mappings.md): stored per profile.
- [Macros](macros.md): stored per profile.
- [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/): import community configs as profiles.
- [Themes](https://padforge.org/docs/features/themes/): the game look a Steam profile can wear.
- [External Control Internals](https://padforge.org/docs/reference/external-control-internals/): the pipe, its security descriptor, and the held-profile state machine.

---

*Last updated for PadForge 6.0.0.*
