# The PadForge Plugin Kit

A plugin is a program, in any language, that adds devices to PadForge. PadForge starts it with your ordinary rights whenever the engine runs, approves it for its exact bytes, and starts it again with a growing pause when it stops.

This folder holds the same small plugin four times, and the libraries the script versions use:

| File | What it is |
|---|---|
| `PadForgePlugin.cs`, `PipeOwner.cs` | The plugin in C#. |
| `PadForgePlugin.csproj` | Its build settings: a console program for Windows on .NET 10, warnings as errors. |
| `build.ps1` | Builds it: `powershell -ExecutionPolicy Bypass -File build.ps1`. It needs the .NET SDK 10 and writes `bin\PadForgePlugin.exe`. |
| `plugin.py` with `padforge.py` | The plugin in Python. |
| `plugin.ps1` with `PadForge.psm1` | The plugin in Windows PowerShell. |
| `plugin.ahk` with `PadForge.ahk` | The plugin in AutoHotkey v2. |

To add one, open PadForge, go to Extras, Plugins, and press Add a Plugin…, or drop the file on that page. PadForge shows its path, its signer and its SHA-256 and asks you before it runs it. A file that changes later shows Approve and Restart on its card.

Each one declares a controller with two buttons, a stick and rumble, declares four settings (Speed, Tap Fire, Center and Turns), moves the stick around a circle at the Speed you set, taps Fire once a second while Tap Fire is on, and counts its turns in Turns. PadForge draws the settings in the plugin's card on the Plugins page.

## How a plugin connects

PadForge puts two variables in the plugin's environment: `PADFORGE_PLUGIN_TOKEN`, a key for this one start, and `PADFORGE_PIPE`, the name of the pipe to connect to. The plugin connects to `\\.\pipe\<PADFORGE_PIPE>` and, before it sends anything, checks that the pipe's owner is Administrators or SYSTEM, as `PipeOwner.cs` and each library do. Then it says hello with the key:

```
@1 hello name="Pedal Box" kind=plugin token=<key> protocol=1 units=plain
```

The key works once, and only from the process PadForge started or one it started in turn. Until the hello, PadForge holds the plugin's file open so nothing can change it. After the hello the file is free to rebuild.

A plugin sends a line at least every 2 seconds, `beat` when it has nothing else to say. When it falls silent, its devices rest at once and go offline after 5 seconds, and after 60 seconds PadForge ends it and starts it again with the growing pause, unless a debugger is attached to it. Five failures in a row stop it until you press Start.

## The wire

UTF-8 text, one message a line. A line is a verb and its words, an optional JSON object at the end, and an optional `@tag` in front that the answer repeats. A word is `name=value`, or a bare word. A value with a space or a quote goes in double quotes, with `\"` and `\\` inside. An answer is `ok` and its words, or `err <code> <words>`.

A value sent is PadForge's raw number (axes 0 to 65535, a stick at rest on 32768, hats in hundredths of a degree) or a plain one with its mark: `50%` or `-30%` for an axis, `90deg` or a direction such as `Up+Right` for a hat, and `rest`. A session that says `units=plain` reads plain units: buttons 0 or 1, sticks -100 to 100, triggers and sliders 0 to 100, hats in degrees or -1.

### Verbs

| Verb | What it does |
|---|---|
| `hello name="..." kind=plugin token=... protocol=1 [units=plain] [lease=<ms>]` | Opens the session. The answer gives PadForge's version, the protocol and the level. |
| `declare key=<key> name="..." {json}` | Adds a device, or gives the one of that key a new shape. A plugin may add 16. The answer carries its `id`. |
| `s <id> b0=1 a0=-50% h0=90deg ...` | Sets controls of its device as one change. `b<i>` buttons, `a<i>` axes, `h<i>` hats. |
| `rest <id>` | Puts every control of its device at rest. |
| `gone <id>` | Takes its device offline. |
| `setting key=<key> kind=<kind> name="..." [...] [{json}]` | Declares a setting or control. The answer gives `key=` and the `value=` in force. |
| `setting key=<key> value=<value> [device=<key>]` | Sets a status line, or a value the plugin changed itself. |
| `setting key=<key> [device=<key>] {"choices":[...]}` | Replaces a choice's list, to keep it current. |
| `setting clear` | Takes back every declaration of this run. |
| `log "text"` | A line for the plugin's log on the Plugins page, up to 500 characters. |
| `beat` | Keeps the lease. Answered only with a tag. |
| `bye` | Closes the session. |
| `state`, `activate`, `deactivate`, `query`, `engine on`, `controllers off`, `slot 2 on`, `show`, `hide`, `minimize`, `snapshot`, `record start`, `version`, `exit` | PadForge's commands, in the words `PadForge.Control` takes. |
| `devices`, `caps <id>`, `read <id>`, `outputs <n>` | Lists, a device's named controls, its state, what a virtual controller sends. |
| `sub <id> A`, `sub <id> A claim`, `sub devices`, `unsub` | Events, starting with the whole state. `claim` takes the control from the game while the subscription lives. |
| `own <id> A=1`, `rel <id> A`, `var name=value` | Holds a control of any device, lets go, sets a named value for Custom Expression triggers. |
| `call <tool> {json}` | Runs any assistant tool at its own level. |

### Events PadForge sends

| Event | When |
|---|---|
| `e rumble <id> <low> <high>` | A game's rumble for its device, 0 to 65535 each. |
| `e trigrumble <id> <left> <right>` | Trigger rumble. |
| `e trigfx <id> <left> <right>` | Adaptive trigger effects, for a device that declares `trigfx`: each trigger's 11-byte effect program as 22 hex digits, its mode first, as the game wrote it to the controller's DualSense. |
| `e led <id> <rrggbb>` | The light's color. |
| `e player <id> <n>` | The player number, from 1. |
| `e setting key=<key> value=<value> [device=<key>]` | The user, or an assistant, changed a setting. It arrives before anything PadForge sends after it. A reset sends the default. |
| `e press key=<key> [device=<key>]` | The user pressed a button the plugin declared. |
| `e choices key=<key> [device=<key>]` | A choice's list opened: send its list as it is now. |
| `e lease lapsed` | The plugin was silent for its lease. Its devices rest until it speaks again. |

### What a device declares

`declare`'s JSON object may hold `buttons`, `axes` and `sliders` (names, or objects with `index`, `name` and `rest`, a rest being raw or a percent such as `"50%"`), `hats` (names), `gyro`, `accel` and `battery` (true or false), `touchpads` (finger counts) and `feedback` (any of `rumble`, `trigrumble`, `trigfx`, `light`, `player`). Trigger rumble and trigger effects bring rumble with them.

## Settings and controls

A plugin declares its own settings with `setting`, and PadForge draws them in its own style on the plugin's card, or on a device's panel for a device's own setting (`device=<key>`). Every value is kept in PadForge.xml for the plugin and for each device, with a reset each and Reset All, and is dropped with the plugin or the device. Assistants reach the same settings and buttons through PadForge's catalog.

Limits: 64 settings a plugin, 1 KB a value, 16 KB of values a plugin. Words are cleaned and capped: a name at 64 characters, help at 300, a group at 64, a unit at 12, a choice at 64.

### The words of a declaration

| Word | For | Meaning |
|---|---|---|
| `key` | every kind | 1 to 64 letters, digits, `_`, `.` or `-`. |
| `kind` | every kind | One of the kinds below. |
| `name` | every kind | The label. The key when it has none. |
| `help` | every kind | What it does. A switch's and a button's stand in the row, others show on hover and to screen readers. |
| `group` | every kind | A heading the setting sits under, in the order groups first appear. |
| `device` | every kind | A device's key: the setting belongs to that device's panel. |
| `default` | values | The value a new plugin or device starts with. |
| `min`, `max`, `step` | number | The range and the step. A value outside the range is refused with the range in words. |
| `unit` | number | Shown after the value: `s`, `ms`, `%`. |
| `choices` | choice | In the JSON: `["A","B"]`, or `[{"value":"a","name":"A"}]`. Or a word: `choices="A,B"`. |
| `stay` | pad | `1`: the pad stays where it is let go. Otherwise it goes back to its center. |
| `when` | every kind | The key of a switch: shown only while that switch is on. |
| `look` | pad, number, switch | `compact`: drawn beside the group's other compact ones in one row, a switch as a toggle. |

The JSON object may give any of these words too, and its values win.

### The kinds

| Kind | Drawn as | Value |
|---|---|---|
| `switch` | A switch. | `true` or `false`. |
| `number` | A slider with its value and unit. | The number on its step. |
| `choice` | A list, fixed or kept current by the plugin. | The entry's value. |
| `text` | A text box. | Text, up to 1 KB. |
| `status` | Read only text, updated by the plugin with `setting key=... value=...`. | Text. Not kept. |
| `button` | A button. A press sends `e press`. | None. |
| `pad` | A square the user drags. | `x,y`, each -100 to 100, y growing downward. |
| `input` | Pick, then the user presses a control on a device. | `<device id> <control>`, such as `3f2a9c1d Button 2`. |
| `help` | A paragraph. With `device=`, the device's own help. | None. |

Example:

```
@2 setting key=curve kind=choice name="Brake Curve" group=Pedals {"choices":["Linear","Progressive"],"default":"Progressive"}
@2 ok key=curve value=Progressive
@3 setting key=dead kind=number name="Dead Zone" min=0 max=20 unit=% default=3
@4 setting key=port kind=status name="Port"
@5 setting key=port value=COM4
@6 setting key=cal kind=button name="Calibrate" help="Press each pedal fully once."
@7 setting key=about kind=help device=pedals help="Three pedals on one USB serial board."
```

## Exit codes and signing

A plugin's exit code shows in its log. Sign it with your certificate and its card names you as its signer, and a rebuilt, signed file still asks for approval, since approval is for the bytes.
