<!-- https://padforge.org/docs/features/plugins/ as PadForge's skill carries it, from padforge.org's wiki/features/plugins.md. -->
# Plugins

*Programs, in any language, that add devices to PadForge, with settings PadForge draws on its own pages.*

A plugin is a program PadForge starts while the engine runs. It connects to PadForge, declares devices, and sends their inputs, and from then on those devices work like any other: they show on the Devices page, take mappings and feel the game's rumble. A plugin can also declare its own settings and buttons, which PadForge draws in its own style.

<!-- SCREENSHOT: extras-plugins (the Plugins page with the Test Controller card open and one plugin of your own) -->

---

## The page

Open **Extras** in the sidebar and click the **Plugins** tile. Turn on **Run Plugins**. It is off by default. The status card says **No plugin runs.** or **Plugins running: 2. Devices they added: 3.**, followed by a sentence for each plugin that stopped. Home's Plugins tile shows the same line while Run Plugins is on.

The page holds the **Built In** card (the Test Controller and Replay), **Your Plugins** while you have one, the card to add one, and **More** with the plugin kit and each plugin's log.

A plugin runs while **Run Plugins** is on, the engine runs, and its own switch is on. Up to 16 plugins can be on the list.

---

## Add a plugin

Click **Add a Plugin…**, or drop one file on the page. PadForge reads the file and asks first:

> **Add a Plugin.** This program will run with your rights whenever the engine runs.
> File: …
> Signed by: … (or **Not signed**)
> SHA-256: …

**Add Plugin** adds it, switched on, for exactly those bytes. Cancel is the default.

PadForge runs an `.exe` as itself, a `.bat` or `.cmd` in `cmd.exe`, a `.ps1` in Windows PowerShell, and any other file, such as `.py` or `.ahk`, in the program Windows opens it with.

### Approved for its bytes

Before each start, PadForge opens the file so nothing can write to it, hashes it, and starts it only if the hash is the one you approved. The file stays held from the hash until the plugin says hello, so it cannot change in between. After that PadForge checks its size and time once a second, and hashes it again when either moves. A file that changed shows **Changed since you approved it** and **Approve and Restart…**, which asks again with the new hash and restarts the plugin on the new bytes.

### Your rights, never PadForge's

PadForge runs as administrator, and a plugin never does. It starts under the desktop shell, with your ordinary rights and your own environment, inside a job object, so the plugin and everything it starts end when PadForge stops it, closes or stops by itself. If no shell runs, or the shell itself has administrator rights, PadForge does not start the plugin and says why. It never falls back to its own rights.

---

## A plugin's card

Each plugin is a fold card under **Your Plugins**. Its header shows its name, a line with its state, the devices it added and its signer, and its switch. Hover the header for the file's path. Unfolded, it shows the plugin's settings, **Remove** and **Reset All**.

| State | Meaning |
|---|---|
| **Running** | It runs. |
| **Starts with the engine** | It is on and waits for the engine. |
| **Checking the file before it starts** | PadForge is hashing it. |
| **Exited with code 1. Starts again in 4 s.** | It stopped and starts again after a pause. |
| **Not started. Tries again in 2 s.** | Windows refused to start it this time. |
| **Changed since you approved it** | Its file changed. Approve it again. |
| **Stopped after five failures in a row** | It failed five times in a row. **Start** tries again. |
| **Stopped** | It is switched off. |
| **Not started: …** | Why it cannot start, such as **the file is gone** or **it asks for administrator rights**. **Start** tries again. |

A plugin that stops while it should run starts again after 1, 2, 4 and 8 seconds. The fifth failure in a row stops it. A run that lasts a minute clears the count, and each engine start gives a stopped plugin a fresh try.

**Remove** stops the plugin and takes it off the list. Its devices stay on the Devices page as disconnected devices until you remove them there.

### A plugin that stops answering

A plugin keeps a lease by sending a line at least every two seconds (it may ask for 250 ms to 10 s). When the lease lapses, its holds let go and its devices rest. After five seconds of silence its devices go offline at rest. After 60 seconds PadForge ends it, and the back-off starts it again, unless a debugger is attached. When a plugin's pipe closes, its devices rest at once and go offline after a second and a half unless it comes back.

A plugin may add 16 devices. A device's panel on the Devices page says **Added by …**.

---

## Settings a plugin declares

A plugin declares its own settings, and PadForge draws them in groups the plugin names, with a reset on each and **Reset All** for the plugin:

| Kind | Drawn as |
|---|---|
| switch | A switch. |
| number | A slider on its step, with its range and unit. |
| choice | A list, fixed or kept current by the plugin. |
| text | A text box, up to 1 KB. |
| status | A line the plugin updates. |
| button | A button the plugin acts on. |
| pad | A square you drag to steer by hand, back to center on release unless the plugin says it stays. The arrow keys move it and Home centers it. |
| input | **Pick**, then **Press a control…**: the device input you press is the value. |
| help | A paragraph. |

A plugin's settings sit on its card. A plugin device's own settings sit in that device's panel on the Devices page, in a card titled **… Settings**, with its help. The card says why the settings are missing when they are: **Its settings show here while it runs.**, **It declares no settings.** or **PadForge could not read its settings: ….**

A value you change reaches the plugin at once, before anything else PadForge sends it. Values live in PadForge.xml, only those that differ from their defaults, up to 64 settings, 1 KB a value and 16 KB a plugin. A device's values go when you remove its row on the Devices page, and a plugin's go when you remove the plugin. Assistants reach the same settings and buttons through the [catalog](assistants.md), at **Change Settings**.

---

## Test Controller

A built-in controller that moves by itself, so a mapping can be tried with no hardware in hand. Its switch is on its card under **Built In**, off by default.

| Setting | Range | Default |
|---|---|---|
| **What Moves** | Sticks and Triggers, Sticks, Triggers | Sticks and Triggers |
| **Pattern** | Sweep, Circle | Sweep |
| **Move Time** | 0.5 to 20 s | 4 s |
| **Rest Time** | 0 to 20 s | 2 s |
| **Press Buttons** | on or off | on |
| **Press Navigation Buttons** | on or off | off |
| **Turn the D-Pad** | on or off | off |
| **Press Time** | 50 to 5,000 ms | 250 ms |
| **Controllers** | 1 to 4 | 1 |
| **By Hand** | on or off | off |
| **Reads per Second** | a status | |

The sticks, the triggers or both, as **What Moves** picks, move together for the Move Time and rest for the Rest Time, while one button at a time is held for the Press Time. With **Turn the D-Pad** on, the D-pad turns once round while they move. Its motion is worked out at each read of the engine, so it is as fine as the polling rate.

**Press Navigation Buttons** and **Turn the D-Pad** start off because Windows apps act on a virtual Xbox controller's A, B, Back, Start and D-pad in the window in front, and its Guide button opens Xbox Game Bar. With them off, the Test Controller presses X, Y, the shoulders and the stick buttons.

**By Hand** stops the motion and draws the controller's sticks as pads, its triggers as sliders and its buttons as toggles, to set with the mouse. Each stays where you leave it, after a restart too.

## Replay

Replay plays a [problem recording](problem-recorder.md) back as the devices that made it, with the timing they had. They are new devices on this PC, named **… (Replay)**, mapped as you or a profile map them. Turn it on, click **Open a Recording…**, then **Play**, with **Repeat** to loop. It plays up to 16 devices.

---

## The plugin kit

**Save the Plugin Kit…** under More, or `PadForge.exe --plugin-kit <folder>`, writes a folder with:

- `README.md`, a guide to every verb, event, declaration and kind of setting,
- the same sample plugin in C# (`PadForgePlugin.cs`, with `PadForgePlugin.csproj` and `build.ps1`), Python (`plugin.py`), PowerShell (`plugin.ps1`) and AutoHotkey v2 (`plugin.ahk`),
- PadForge's script libraries.

Each sample declares a device and four settings: a number, a switch, a button and a status.

### The wire

A plugin speaks protocol 1 on the `PadForge.Automation` pipe: UTF-8 lines, one message a line, a verb and its words, an optional JSON object at the end, and an optional `@tag` the answer repeats. PadForge hands the plugin a one-time key in `PADFORGE_PLUGIN_TOKEN` and the pipe's name in `PADFORGE_PIPE`, and takes the key only from the process it started or one in its job.

| Line | Meaning |
|---|---|
| `hello name="Pedal Box" kind=plugin token=<key> protocol=1 units=plain` | Opens the session. |
| `declare key=<key> name="..." {json}` | Adds a device. |
| `s <id> b0=1 a0=-50% h0=90deg` | Sets controls as one change. |
| `rest <id>`, `gone <id>` | Rests the device, or takes it offline. |
| `setting key=<key> kind=<kind> name="..."` | Declares a setting. `setting key=<key> value=<value>` sets a value or a status. |
| `log "text"` | A line in the plugin's log under More, up to 500 characters. |
| `beat`, `bye` | Keeps the lease, closes. |

Feedback arrives as `e rumble`, `e trigrumble`, `e trigfx`, `e led` and `e player` for a device that declared it, and settings as `e setting` and `e press`. The kit's README lists every word.

See also: [Automation](automation.md), [Command Line](../reference/command-line.md).
